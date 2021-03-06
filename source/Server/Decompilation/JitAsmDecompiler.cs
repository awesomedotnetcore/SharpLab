using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Microsoft.Diagnostics.Runtime;
using SharpDisasm;
using SharpDisasm.Translators;
using SharpLab.Runtime;
using SharpLab.Server.Common;

namespace SharpLab.Server.Decompilation {
    [UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
    public class JitAsmDecompiler : IDecompiler {
        private readonly Pool<ClrRuntime> _runtimePool;

        public string LanguageName => TargetNames.JitAsm;

        public JitAsmDecompiler(Pool<ClrRuntime> runtimePool) {
            _runtimePool = runtimePool;
        }

        public void Decompile(CompilationStreamPair streams, TextWriter codeWriter) {
            Argument.NotNull(nameof(streams), streams);
            Argument.NotNull(nameof(codeWriter), codeWriter);

            using (var loadContext = new CustomAssemblyLoadContext(shouldShareAssembly: _ => true)) {
                var assembly = loadContext.LoadFromStream(streams.AssemblyStream);
                ValidateStaticConstructors(assembly);

                using var runtimeLease = _runtimePool.GetOrCreate();
                var runtime = runtimeLease.Object;

                runtime.Flush();
                var context = new JitWriteContext(codeWriter, runtime);
                context.Translator = new IntelTranslator {
                    SymbolResolver = (Instruction instruction, long addr, ref long offset) =>
                        ResolveSymbol(runtime, instruction, addr, context.CurrentMethodAddress)
                };

                WriteJitInfo(runtime.ClrInfo, codeWriter);
                WriteProfilerState(codeWriter);

                var architecture = MapArchitecture(runtime.ClrInfo.DacInfo.TargetArchitecture);
                foreach (var type in assembly.DefinedTypes) {
                    if (type.IsNested)
                        continue; // it's easier to handle nested generic types recursively, so we suppress all nested for consistency
                    DisassembleAndWriteMembers(context, type);
                }
            }
        }

        private void ValidateStaticConstructors(Assembly assembly) {
            foreach (var type in assembly.DefinedTypes) {
                foreach (var constructor in type.DeclaredConstructors) {
                    if (constructor.IsStatic)
                        throw new NotSupportedException($"Type {type} has a static constructor, which is not supported by SharpLab JIT decompiler.");
                }
            }
        }

        private void WriteJitInfo(ClrInfo clr, TextWriter writer) {
            writer.WriteLine(
                "; {0:G} CLR {1} ({2}) on {3}.",
                clr.Flavor, clr.Version, Path.GetFileName(clr.ModuleInfo.FileName), clr.DacInfo.TargetArchitecture.ToString("G").ToLowerInvariant()
            );
        }

        private void WriteProfilerState(TextWriter writer) {
            if (!ProfilerState.Active)
                return;

            writer.WriteLine("; Note: Running under profiler, which affects JIT assembly in heap allocations.");
        }

        private void DisassembleAndWriteMembers(JitWriteContext context, TypeInfo type, ImmutableArray<Type>? genericArgumentTypes = null) {
            if (type.IsGenericTypeDefinition) {
                if (TryDisassembleAndWriteMembersOfGeneric(context, type, genericArgumentTypes))
                    return;
            }

            foreach (var constructor in type.DeclaredConstructors) {
                DisassembleAndWriteMethod(context, constructor);
            }

            foreach (var method in type.DeclaredMethods) {
                if (method.IsAbstract)
                    continue;
                DisassembleAndWriteMethod(context, method);
            }

            foreach (var nested in type.DeclaredNestedTypes) {
                DisassembleAndWriteMembers(context, nested, genericArgumentTypes);
            }
        }

        private bool TryDisassembleAndWriteMembersOfGeneric(JitWriteContext context, TypeInfo type, ImmutableArray<Type>? parentArgumentTypes = null) {
            var hadAttribute = false;
            foreach (var attribute in type.GetCustomAttributes<JitGenericAttribute>(false)) {
                hadAttribute = true;

                var fullArgumentTypes = (parentArgumentTypes ?? ImmutableArray<Type>.Empty)
                    .AddRange(attribute.ArgumentTypes);
                var genericInstance = type.MakeGenericType(fullArgumentTypes.ToArray());
                DisassembleAndWriteMembers(context, genericInstance.GetTypeInfo(), fullArgumentTypes);
            }
            if (hadAttribute)
                return true;

            if (parentArgumentTypes != null) {
                var genericInstance = type.MakeGenericType(parentArgumentTypes.Value.ToArray());
                DisassembleAndWriteMembers(context, genericInstance.GetTypeInfo(), parentArgumentTypes);
                return true;
            }

            return false;
        }

        private void DisassembleAndWriteMethod(JitWriteContext context, MethodBase method) {
            if ((method.MethodImplementationFlags & MethodImplAttributes.Runtime) == MethodImplAttributes.Runtime) {
                WriteSignatureFromReflection(context, method);
                context.Writer.WriteLine("    ; Cannot produce JIT assembly for runtime-implemented method.");
                return;
            }

            if (method.DeclaringType?.IsGenericTypeDefinition ?? false) {
                WriteIgnoredOpenGeneric(context, method);
                return;
            }

            if (method.IsGenericMethodDefinition) {
                DisassembleAndWriteGenericMethod(context, (MethodInfo)method);
                return;
            }

            DisassembleAndWriteSimpleMethod(context, method);
        }

        private void DisassembleAndWriteGenericMethod(JitWriteContext context, MethodInfo method) {
            var hasAttribute = false;
            foreach (var attribute in method.GetCustomAttributes<JitGenericAttribute>()) {
                hasAttribute = true;
                var genericInstance = method.MakeGenericMethod(attribute.ArgumentTypes);
                DisassembleAndWriteSimpleMethod(context, genericInstance);
            }
            if (!hasAttribute)
                WriteIgnoredOpenGeneric(context, method);
        }

        private void DisassembleAndWriteSimpleMethod(JitWriteContext context, MethodBase method) {
            var handle = method.MethodHandle;
            RuntimeHelpers.PrepareMethod(handle);
            
            var clrMethod = context.Runtime.GetMethodByHandle((ulong)handle.Value.ToInt64());
            var regions = FindNonEmptyHotColdInfo(clrMethod);

            if (clrMethod == null || regions == null) {
                context.Runtime.Flush();
                clrMethod = context.Runtime.GetMethodByHandle((ulong)handle.Value.ToInt64());
                regions = FindNonEmptyHotColdInfo(clrMethod);
            }

            if (clrMethod == null || regions == null) {
                var address = (ulong)handle.GetFunctionPointer().ToInt64();
                clrMethod = context.Runtime.GetMethodByAddress(address);
                regions = FindNonEmptyHotColdInfo(clrMethod);
            }

            var writer = context.Writer;
            if (clrMethod != null) {
                writer.WriteLine();
                writer.WriteLine(clrMethod.GetFullSignature());
            }
            else {
                WriteSignatureFromReflection(context, method);
            }

            if (regions == null) {
                if (method.IsGenericMethod || (method.DeclaringType?.IsGenericType ?? false)) {
                    writer.WriteLine("    ; Failed to find HotColdInfo for generic method (reference types?).");
                    writer.WriteLine("    ; If you know a solution, please comment at https://github.com/ashmind/SharpLab/issues/99.");
                    return;
                }
                writer.WriteLine("    ; Failed to find HotColdRegions — please report at https://github.com/ashmind/SharpLab/issues.");
                return;
            }

            var methodAddress = regions.HotStart;
            context.CurrentMethodAddress = methodAddress;

            var architecture = MapArchitecture(context.Runtime.DataTarget.Architecture);
            using (var disasm = new Disassembler(new IntPtr(unchecked((long)methodAddress)), (int)regions.HotSize, architecture, methodAddress)) {
                var translator = context.Translator!;
                foreach (var instruction in disasm.Disassemble()) {
                    writer.Write("    L");
                    writer.Write((instruction.Offset - methodAddress).ToString("x4"));
                    writer.Write(": ");
                    writer.WriteLine(translator.Translate(instruction));
                }
            }
        }

        private void WriteIgnoredOpenGeneric(JitWriteContext context, MethodBase method) {
            WriteSignatureFromReflection(context, method);
            var writer = context.Writer;
            writer.WriteLine("    ; Open generics cannot be JIT-compiled.");
            writer.WriteLine("    ; However you can use attribute SharpLab.Runtime.JitGeneric to specify argument types.");
            writer.WriteLine("    ; Example: [JitGeneric(typeof(int)), JitGeneric(typeof(string))] void M<T>() { ... }.");
        }

        private static void WriteSignatureFromReflection(JitWriteContext context, MethodBase method) {
            context.Writer.WriteLine();

            var md = (ulong)method.MethodHandle.Value.ToInt64();
            var signature = context.Runtime.DacLibrary.SOSDacInterface.GetMethodDescName(md);

            context.Writer.WriteLine(signature ?? "Unknown Method");
        }

        private static string? ResolveSymbol(ClrRuntime runtime, Instruction instruction, long addr, ulong currentMethodAddress) {
            var operand = instruction.Operands.Length > 0 ? instruction.Operands[0] : null;
            if (operand?.PtrOffset == 0) {
                var lvalue = GetOperandLValue(operand!);
                if (lvalue == null)
                    return $"{operand!.RawValue} ; failed to resolve lval ({operand.Size}), please report at https://github.com/ashmind/SharpLab/issues";
                var baseOffset = instruction.PC - currentMethodAddress;
                return $"L{baseOffset + lvalue:x4}";
            }

            return runtime.GetMethodByAddress(unchecked((ulong)addr))?.GetFullSignature();
        }

        private static ulong? GetOperandLValue(Operand operand) {
            switch (operand.Size) {
                case 8:  return (ulong)operand.LvalSByte;
                case 16: return (ulong)operand.LvalSWord;
                case 32: return (ulong)operand.LvalSDWord;
                default: return null;
            }
        }

        private HotColdRegions? FindNonEmptyHotColdInfo(ClrMethod? method) {
            if (method == null)
                return null;

            // I can't really explain this, but it seems that some methods 
            // are present multiple times in the same type -- one compiled
            // and one not compiled. A bug in clrmd?
            if (method.HotColdInfo.HotSize > 0)
                return method.HotColdInfo;

            if (method.Type == null)
                return null;

            var methodSignature = method.GetFullSignature();
            foreach (var other in method.Type.Methods) {
                if (other.MetadataToken == method.MetadataToken && other.GetFullSignature() == methodSignature && other.HotColdInfo.HotSize > 0)
                    return other.HotColdInfo;
            }

            return null;
        }

        private ArchitectureMode MapArchitecture(Architecture architecture) => architecture switch
        {
            Architecture.Amd64 => ArchitectureMode.x86_64,
            Architecture.X86 => ArchitectureMode.x86_32,
            // ReSharper disable once HeapView.BoxingAllocation
            // ReSharper disable once HeapView.ObjectAllocation.Evident
            _ => throw new Exception($"Unsupported architecture mode {architecture}."),
        };

        private class JitWriteContext {
            public JitWriteContext(TextWriter writer, ClrRuntime runtime) {
                Writer = writer;
                Runtime = runtime;
            }
            
            public TextWriter Writer { get; }
            public ClrRuntime Runtime { get; }
            public ulong CurrentMethodAddress { get; set; }
            public Translator? Translator { get; set; }
        }
    }
}