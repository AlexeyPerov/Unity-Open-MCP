using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools
{
    static class RoslynHost
    {
        private static Assembly _ca;
        private static Assembly _cacs;
        private static bool _initAttempted;

        // B40 — MetadataReference construction is the expensive part of every
        // compile: BuildMetadataReferences walks every loaded assembly and calls
        // CreateFromAssembly / CreateFromFile (which opens and parses each PE).
        // The set of referenced assemblies is effectively stable for the session
        // (Unity loads its assemblies once at startup; execute_csharp does not
        // load new production assemblies), so cache the built references keyed by
        // the assembly count. A domain reload clears the static field; a genuine
        // new assembly bumps the count and triggers one rebuild.
        private static List<object> _cachedReferences;
        private static int _cachedReferenceAssemblyCount = -1;

        public static bool IsAvailable { get; private set; }
        public static string LastInitError { get; private set; }

        public static bool Initialize()
        {
            if (IsAvailable) return true;
            if (_initAttempted) return false;
            _initAttempted = true;

            var contentsPath = EditorApplication.applicationContentsPath;
            foreach (var roslynDir in GetRoslynDirectoryCandidates(contentsPath))
            {
                if (TryLoadRoslyn(roslynDir))
                {
                    IsAvailable = true;
                    LastInitError = null;
                    return true;
                }
            }

            if (string.IsNullOrEmpty(LastInitError))
                LastInitError = "Roslyn directory not found in Unity installation";

            Debug.LogWarning($"[Unity Open MCP Bridge] {LastInitError}");
            return false;
        }

        private static IEnumerable<string> GetRoslynDirectoryCandidates(string contentsPath)
        {
            yield return Path.Combine(contentsPath, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn");
            yield return Path.Combine(contentsPath, "DotNetSdkRoslyn");
            yield return Path.Combine(contentsPath, "Tools", "roslyn");
        }

        private static bool TryLoadRoslyn(string roslynDir)
        {
            if (!Directory.Exists(roslynDir))
                return false;

            var codeAnalysisPath = Path.Combine(roslynDir, "Microsoft.CodeAnalysis.dll");
            var codeAnalysisCSharpPath = Path.Combine(roslynDir, "Microsoft.CodeAnalysis.CSharp.dll");
            if (!File.Exists(codeAnalysisPath) || !File.Exists(codeAnalysisCSharpPath))
                return false;

            // feedback-01-08-glm §1 — Unity 6000.0.x ships the Roslyn compiler
            // assemblies under DotNetSdkRoslyn as ReadyToRun (R2R) composite
            // images that the editor's Mono runtime cannot load
            // ("Invalid Image: …/Microsoft.CodeAnalysis.dll", "Could not load
            // image … due to Invalid data directory 3"). Detect such images
            // from their PE header BEFORE attempting a LoadFrom, so an
            // unloadable candidate is skipped cleanly with a precise error
            // instead of the generic "Roslyn init failed" (and without wasting
            // the deps-load loop that silently swallows every R2R failure).
            if (IsReadyToRunImage(codeAnalysisPath))
            {
                LastInitError = $"Roslyn candidate '{roslynDir}' ships ReadyToRun (R2R) images " +
                    "the editor's Mono runtime cannot load (Microsoft.CodeAnalysis.dll). Skipping; " +
                    "the Mono-loadable Roslyn under MonoBleedingEdge is the supported execute_csharp backend.";
                Debug.LogWarning($"[Unity Open MCP Bridge] {LastInitError}");
                return false;
            }

            try
            {
                foreach (var dep in Directory.GetFiles(roslynDir, "*.dll"))
                {
                    var fileName = Path.GetFileName(dep);
                    if (fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { Assembly.LoadFrom(dep); } catch { }
                }

                _ca = Assembly.LoadFrom(codeAnalysisPath);
                _cacs = Assembly.LoadFrom(codeAnalysisCSharpPath);
                return true;
            }
            catch (Exception e)
            {
                _ca = null;
                _cacs = null;
                LastInitError = $"Roslyn init failed for {roslynDir}: {e.Message}";
                Debug.LogWarning($"[Unity Open MCP Bridge] {LastInitError}");
                return false;
            }
        }

        /// <summary>
        /// Detect a non-pure-IL (ReadyToRun / mixed-native) PE image from its
        /// header, WITHOUT loading the assembly. Unity 6000.0.x ships the Roslyn
        /// compiler assemblies under DotNetSdkRoslyn as ReadyToRun composite
        /// images the editor's Mono runtime cannot load ("Invalid Image",
        /// "Invalid data directory 3"). The robust signal is the COR20 header's
        /// Flags: a pure-IL (Mono-loadable) managed assembly always sets
        /// <c>COMImageFlags.ILOnly</c> (0x1); an R2R composite clears it. Reads
        /// only the PE/COR20 headers (DOS stub → PE sig → optional header →
        /// COR20 data directory → COR20 header via the section table). Never
        /// throws on a truncated/garbage file — returns false so the normal
        /// LoadFrom path reports the real load error.
        /// </summary>
        internal static bool IsReadyToRunImage(string dllPath)
        {
            try
            {
                using (var fs = File.OpenRead(dllPath))
                using (var br = new BinaryReader(fs, System.Text.Encoding.ASCII, leaveOpen: true))
                {
                    if (fs.Length < 0x40) return false;
                    // DOS header: 'MZ' at 0, PE-header offset at 0x3c.
                    if (br.ReadByte() != (byte)'M' || br.ReadByte() != (byte)'Z') return false;
                    fs.Position = 0x3c;
                    int peOffset = br.ReadInt32();
                    if (peOffset <= 0 || peOffset + 24 > fs.Length) return false;

                    // PE signature 'PE\0\0' + COFF header (20 bytes).
                    fs.Position = peOffset;
                    if (br.ReadByte() != (byte)'P' || br.ReadByte() != (byte)'E'
                        || br.ReadByte() != 0 || br.ReadByte() != 0) return false;
                    br.ReadUInt16(); // Machine
                    int numSections = br.ReadUInt16();
                    br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
                    int sizeOfOptionalHeader = br.ReadUInt16();
                    br.ReadUInt16(); // Characteristics

                    // Optional header: the standard+NT fields (before the data
                    // directory array) are 96 bytes for PE32 and 112 for PE32+.
                    // The COR20 directory is data-directory index 14 (each entry
                    // is 8 bytes: RVA + size).
                    int optionalStart = peOffset + 24;
                    fs.Position = optionalStart;
                    ushort magic = br.ReadUInt16();
                    int dataDirStart = optionalStart + (magic == 0x20b ? 112 : 96);
                    int cor20DirPos = dataDirStart + 14 * 8;
                    if (cor20DirPos + 8 > fs.Length) return false;
                    fs.Position = cor20DirPos;
                    int cor20Rva = br.ReadInt32();
                    int cor20Size = br.ReadInt32();
                    if (cor20Rva == 0 || cor20Size == 0) return false; // not a managed assembly

                    // Section table immediately follows the optional header.
                    long sectionTable = optionalStart + sizeOfOptionalHeader;
                    long corHeaderOffset = ResolveRva(fs, sectionTable, numSections, cor20Rva);
                    if (corHeaderOffset < 0 || corHeaderOffset + 0x18 > fs.Length) return false;

                    // COR20 header: cb(4) MajorRuntimeVersion(2) MinorRuntimeVersion(2)
                    // MetaData(8) Flags(4 @ 0x10).
                    fs.Position = corHeaderOffset + 0x10;
                    int flags = br.ReadInt32();
                    const int corILonly = 0x00000001;
                    return (flags & corILonly) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Resolve a PE Relative Virtual Address to a file offset via
        /// the section table. Each section header is 40 bytes: VirtualAddress
        /// at +12, VirtualSize at +8, PointerToRawData at +20, SizeOfRawData at
        /// +16. Returns -1 when the RVA is outside every section.</summary>
        private static long ResolveRva(FileStream fs, long sectionTable, int numSections, int rva)
        {
            fs.Position = sectionTable;
            var sect = new byte[40];
            for (int i = 0; i < numSections; i++)
            {
                if (fs.Read(sect, 0, 40) < 40) return -1;
                int virtualSize = BitConverter.ToInt32(sect, 8);
                int virtualAddress = BitConverter.ToInt32(sect, 12);
                int sizeOfRawData = BitConverter.ToInt32(sect, 16);
                int pointerToRawData = BitConverter.ToInt32(sect, 20);
                int sectionEnd = virtualAddress + (virtualSize > 0 ? virtualSize : sizeOfRawData);
                if (rva >= virtualAddress && rva < sectionEnd)
                    return pointerToRawData + (rva - virtualAddress);
            }
            return -1;
        }

        public static (byte[] pe, string errors) Compile(string source)
        {
            try
            {
                return CompileInternal(source);
            }
            catch (TargetInvocationException tie)
            {
                return (null, tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }
        }

        private static (byte[] pe, string errors) CompileInternal(string source)
        {
            var syntaxTreeType = _ca.GetType("Microsoft.CodeAnalysis.SyntaxTree");
            var metadataRefType = _ca.GetType("Microsoft.CodeAnalysis.MetadataReference");
            var enumerableOfSyntaxTree = typeof(IEnumerable<>).MakeGenericType(syntaxTreeType);
            var enumerableOfMetadataRef = typeof(IEnumerable<>).MakeGenericType(metadataRefType);
            var cSharpCompilationOptionsType = _cacs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
            var outputKindType = _ca.GetType("Microsoft.CodeAnalysis.OutputKind");

            var (syntaxTree, parseError) = ParseSyntaxTree(source, syntaxTreeType);
            if (syntaxTree == null)
                return (null, parseError ?? "Could not parse C# source");

            var references = BuildMetadataReferences(metadataRefType);
            if (references == null)
                return (null, "Could not find MetadataReference.CreateFromAssembly or CreateFromFile method");

            var dllOutput = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            object options;
            var ctor = cSharpCompilationOptionsType.GetConstructors()
                .Where(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == outputKindType)
                .OrderByDescending(c => c.GetParameters().Count(p => p.IsOptional))
                .FirstOrDefault();
            if (ctor != null)
                options = InvokeWithOptionalDefaults(ctor, null, dllOutput);
            else
                options = Activator.CreateInstance(cSharpCompilationOptionsType);

            var compType = _cacs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            var createMethod = FindStaticMethod(compType, "Create", typeof(string));
            if (createMethod == null)
                return (null, "Could not find CSharpCompilation.Create method");

            var compilation = InvokeWithOptionalDefaults(createMethod, null, "UnityOpenMcpSnippet");

            var syntaxTrees = Array.CreateInstance(syntaxTreeType, 1);
            syntaxTrees.SetValue(syntaxTree, 0);

            var addTrees = compType.GetMethod("AddSyntaxTrees", new[] { enumerableOfSyntaxTree });
            if (addTrees != null)
                compilation = addTrees.Invoke(compilation, new object[] { syntaxTrees });

            var refs = Array.CreateInstance(metadataRefType, references.Count);
            for (int r = 0; r < references.Count; r++)
                refs.SetValue(references[r], r);

            var addRefs = compType.GetMethod("AddReferences", new[] { enumerableOfMetadataRef });
            if (addRefs != null)
                compilation = addRefs.Invoke(compilation, new object[] { refs });

            var withOptions = compType.GetMethod("WithOptions", new[] { cSharpCompilationOptionsType });
            if (withOptions != null)
                compilation = withOptions.Invoke(compilation, new object[] { options });

            // B40 — peStream is IDisposable; scope it with `using` so every
            // return path (emit failure diagnostics, success, or a throw from
            // Emit) disposes the underlying managed buffer holder. MemoryStream
            // holds no unmanaged resource, but disposing is the documented
            // contract and keeps the buffer eligible for immediate GC.
            using (var peStream = new MemoryStream())
            {
                var emitMethod = FindInstanceMethod(compType, "Emit", p => typeof(Stream).IsAssignableFrom(p.ParameterType));
                if (emitMethod == null)
                    return (null, "Could not find compilation Emit method");

                var emitResult = InvokeWithOptionalDefaults(emitMethod, compilation, peStream);

                var emitResultType = _ca.GetType("Microsoft.CodeAnalysis.Emit.EmitResult");
                var successProp = emitResultType.GetProperty("Success");
                var success = (bool)successProp.GetValue(emitResult);

                if (!success)
                {
                    var diagsProp = emitResultType.GetProperty("Diagnostics");
                    var diags = (System.Collections.IEnumerable)diagsProp.GetValue(emitResult);
                    var errorMessages = new List<string>();
                    var diagnosticType = _ca.GetType("Microsoft.CodeAnalysis.Diagnostic");
                    var severityProp = diagnosticType.GetProperty("Severity");

                    var getMessage = diagnosticType.GetMethod("GetMessage", Type.EmptyTypes);
                    foreach (var d in diags)
                    {
                        var severity = severityProp.GetValue(d);
                        if (severity.ToString() == "Error")
                        {
                            var msg = getMessage?.Invoke(d, null)?.ToString() ?? d?.ToString();
                            errorMessages.Add(string.IsNullOrEmpty(msg) ? "Unknown error" : msg);
                        }
                    }

                    return (null, string.Join("\n", errorMessages));
                }

                return (peStream.ToArray(), null);
            }
        }

        private static (object syntaxTree, string error) ParseSyntaxTree(string source, Type syntaxTreeType)
        {
            var cstType = _cacs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
            var parseText = FindStaticMethod(cstType, "ParseText", typeof(string));
            if (parseText != null)
                return (InvokeWithOptionalDefaults(parseText, null, source), null);

            var sourceTextType = _ca.GetType("Microsoft.CodeAnalysis.Text.SourceText");
            var from = FindStaticMethod(sourceTextType, "From", typeof(string));
            if (from == null)
                return (null, "Could not find CSharpSyntaxTree.ParseText or SourceText.From method");

            var sourceText = InvokeWithOptionalDefaults(from, null, source);
            parseText = FindStaticMethod(cstType, "ParseText", sourceTextType);
            if (parseText == null)
                return (null, "Could not find CSharpSyntaxTree.ParseText method");

            var tree = InvokeWithOptionalDefaults(parseText, null, sourceText);
            if (tree != null && !syntaxTreeType.IsInstanceOfType(tree))
                return (null, "CSharpSyntaxTree.ParseText returned unexpected type");

            return (tree, null);
        }

        private static List<object> BuildMetadataReferences(Type metadataRefType)
        {
            var createFromAssembly = FindStaticMethodMinimal(metadataRefType, "CreateFromAssembly", typeof(Assembly));
            var createFromFile = FindStaticMethod(metadataRefType, "CreateFromFile", typeof(string));
            if (createFromAssembly == null && createFromFile == null)
                return null;

            // B40 — MetadataReference construction (CreateFromAssembly /
            // CreateFromFile) opens and parses every loaded PE and is the
            // expensive part of every compile. The referenced assembly set is
            // effectively stable for the session: Unity loads its assemblies at
            // startup and execute_csharp does not load new production assemblies.
            // Cache the built references keyed by the loaded-assembly count so a
            // genuine new assembly (e.g. a freshly compiled snippet, or a
            // domain-bound package) triggers exactly one rebuild, while the
            // common repeated-call case reuses the cached list. A domain reload
            // clears the static fields, so the cache never outlives its
            // AppDomain.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (_cachedReferences != null && _cachedReferenceAssemblyCount == assemblies.Length)
                return _cachedReferences;

            var references = new List<object>();
            foreach (var asm in assemblies)
            {
                if (asm.IsDynamic) continue;

                object reference = null;
                if (createFromAssembly != null)
                {
                    try { reference = InvokeWithOptionalDefaults(createFromAssembly, null, asm); }
                    catch { }
                }

                if (reference == null && createFromFile != null && !string.IsNullOrEmpty(asm.Location))
                {
                    try { reference = InvokeWithOptionalDefaults(createFromFile, null, asm.Location); }
                    catch { }
                }

                if (reference != null)
                    references.Add(reference);
            }

            _cachedReferences = references;
            _cachedReferenceAssemblyCount = assemblies.Length;
            return references;
        }

        private static MethodInfo FindStaticMethod(Type type, string name, Type firstParamType)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name &&
                            m.GetParameters().Length >= 1 &&
                            ParameterTypeMatches(m.GetParameters()[0].ParameterType, firstParamType))
                .OrderByDescending(m => m.GetParameters().Count(p => p.IsOptional))
                .ThenBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        private static MethodInfo FindStaticMethodMinimal(Type type, string name, Type firstParamType)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name &&
                            m.GetParameters().Length >= 1 &&
                            ParameterTypeMatches(m.GetParameters()[0].ParameterType, firstParamType))
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, Func<ParameterInfo, bool> firstParamMatches)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name &&
                            m.GetParameters().Length >= 1 &&
                            firstParamMatches(m.GetParameters()[0]))
                .OrderByDescending(m => m.GetParameters().Count(p => p.IsOptional))
                .ThenBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        private static bool ParameterTypeMatches(Type parameterType, Type expectedType)
        {
            return parameterType == expectedType ||
                   string.Equals(parameterType.FullName, expectedType.FullName, StringComparison.Ordinal);
        }

        private static object InvokeWithOptionalDefaults(MethodBase method, object target, params object[] providedArgs)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < providedArgs.Length)
                    args[i] = providedArgs[i];
                else if (parameters[i].IsOptional)
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue ?? Type.Missing : Type.Missing;
                else
                    throw new TargetParameterCountException(
                        $"Required parameter '{parameters[i].Name}' was not provided for {method.DeclaringType?.Name}.{method.Name}");
            }

            if (method is ConstructorInfo constructor)
                return constructor.Invoke(args);

            return method.Invoke(target, args);
        }
    }
}
