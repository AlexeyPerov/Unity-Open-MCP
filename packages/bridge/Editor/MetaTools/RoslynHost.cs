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

        // B40 + fd-exhaustion fix — MetadataReference construction is the
        // expensive part of every compile: BuildMetadataReferences walks every
        // file-backed loaded assembly (~400 on a real editor). The built list
        // is cached; the key is the count of REFERENCABLE assemblies
        // (non-dynamic, non-empty Location) — see BuildMetadataReferences for
        // why that count is an exact key within a domain. A domain reload
        // clears the statics.
        private static List<object> _cachedReferences;
        private static int _cachedReferenceAssemblyCount = -1;
        // AssemblyMetadata instances owned by the current _cachedReferences
        // (stream-factory path). Disposed on rebuild / Reinitialize so the
        // prefetched metadata blobs (native memory) are freed promptly instead
        // of waiting for finalizers.
        private static List<IDisposable> _cachedReferenceMetadata;
        // Test seam: number of full reference-list rebuilds this domain. The
        // fd-exhaustion regression test pins that loading a byte[] snippet
        // assembly between two compiles does NOT trigger a rebuild
        // (empty-Location assemblies are outside the cache key).
        internal static int ReferenceBuildCount;

        // Reflection handles for the no-fd metadata factory
        // (AssemblyMetadata.CreateFromStream + PEStreamOptions.PrefetchMetadata
        // + GetReference). Resolved once per domain from _ca; reset by
        // Reinitialize. _streamFactoryProbed latches a failed probe so a Roslyn
        // without the API is probed once, not on every compile.
        private static bool _streamFactoryProbed;
        private static MethodInfo _metadataCreateFromStream;
        private static object _prefetchMetadataFlag;
        private static MethodInfo _metadataGetReference;

        public static bool IsAvailable { get; private set; }
        public static string LastInitError { get; private set; }

        /// <summary>
        /// Shared agent-facing message for roslyn_unavailable failures
        /// (execute_csharp and script validation). Explains the Unity 6000.x
        /// R2R situation and both remediation paths, with the download's
        /// size/source/destination disclosed up front.
        /// </summary>
        public static string UnavailableHint =>
            "Could not load Roslyn compiler assemblies from the Unity installation. " +
            (string.IsNullOrEmpty(LastInitError) ? "" : LastInitError + " ") +
            "Unity 6000.x ships only ReadyToRun (R2R) Roslyn images the editor's Mono " +
            "runtime cannot load; reinstalling Unity does not help. To enable this tool, " +
            "install the IL-only Roslyn " + RoslynFallback.RoslynFallbackConfig.RoslynVersion +
            " fallback (~15 MB, downloaded from nuget.org with SHA-256 pinned packages, " +
            "installed to ~/" + Config.BridgeConstants.SettingsDirName + "/roslyn): call " +
            "execute_csharp with {\"setup_roslyn\": true}, or use the Unity menu " +
            "'Tools > Unity Open MCP Bridge - Install Roslyn Fallback'.";

        /// <summary>
        /// Reset the one-shot init latch and probe the candidate directories
        /// again. Used after the Roslyn fallback installer completes so a
        /// freshly installed ~/.unity-open-mcp/roslyn dir is picked up without
        /// a domain reload. Main thread only.
        /// </summary>
        public static void Reinitialize()
        {
            IsAvailable = false;
            _initAttempted = false;
            LastInitError = null;
            _ca = null;
            _cacs = null;
            DisposeCachedReferences();
            // The stream-factory handles were resolved from the old _ca; force
            // a re-probe against whichever Roslyn the re-init loads.
            _streamFactoryProbed = false;
            _metadataCreateFromStream = null;
            _prefetchMetadataFlag = null;
            _metadataGetReference = null;
            // Detach any previously registered fallback resolver and clear its
            // one-shot latch. Without this, a re-install (e.g. to a different
            // RoslynDirOverride, or after an atomic dir swap deleted the old
            // install dir) leaves the AppDomain.AssemblyResolve handler
            // permanently pinned to a stale roslynDir, and the guard below
            // makes RegisterFallbackResolver a no-op so it never re-binds.
            if (_fallbackResolveHandler != null)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= _fallbackResolveHandler;
                _fallbackResolveHandler = null;
            }
            _fallbackResolverRegistered = false;
            Initialize();
        }

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

            // The ONE Roslyn warning per domain: every candidate failed, so C#
            // execution is genuinely unavailable and the operator has to act.
            // Per-candidate failures stay silent (see TryLoadRoslyn) — a skipped
            // candidate followed by a working one is not a problem to report.
            Debug.LogWarning($"[Unity Open MCP Bridge] {LastInitError}");
            return false;
        }

        internal static IEnumerable<string> GetRoslynDirectoryCandidates(string contentsPath)
        {
            yield return Path.Combine(contentsPath, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn");
            yield return Path.Combine(contentsPath, "DotNetSdkRoslyn");
            yield return Path.Combine(contentsPath, "Tools", "roslyn");
            // Downloaded IL-only NuGet Roslyn fallback (Unity 6000.x ships only
            // R2R images — see IsReadyToRunImage). LAST so an editor-shipped
            // Mono Roslyn keeps winning where one exists (2022.3), preserving
            // diagnostic parity with the project compiler.
            yield return RoslynFallback.RoslynFallbackConfig.InstallDir;
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
                // specs/feedback.md 2026-08-14 — record, do NOT log. Skipping an
                // R2R candidate is only interesting if EVERY candidate then fails;
                // on Unity 6000.x the IL-only fallback (the last candidate) loads
                // right after, so the warning described a non-problem. It fired on
                // every domain reload and, via the per-call `logs[]` capture,
                // reappeared in every execute_csharp response for the rest of the
                // session (~90 tokens x ~40 calls of one already-resolved fact).
                // Initialize() logs LastInitError once when all candidates fail,
                // which is the only case an operator needs to see.
                LastInitError = $"Roslyn candidate '{roslynDir}' ships ReadyToRun (R2R) images " +
                    "the editor's Mono runtime cannot load (Microsoft.CodeAnalysis.dll). Skipping; " +
                    "on editors with no Mono-loadable Roslyn (Unity 6000.x) install the IL-only " +
                    "fallback via execute_csharp {\"setup_roslyn\":true} or the Unity menu " +
                    "'Tools > Unity Open MCP Bridge - Install Roslyn Fallback'.";
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

                // The downloaded NuGet fallback carries its own dependency
                // closure (System.Collections.Immutable 7.0 etc.) at assembly
                // versions newer than the editor Mono's built-ins. The eager
                // sibling LoadFrom loop above covers most bindings; a scoped
                // AssemblyResolve hook catches names Mono binds lazily later.
                if (string.Equals(
                        Path.GetFullPath(roslynDir),
                        Path.GetFullPath(RoslynFallback.RoslynFallbackConfig.InstallDir),
                        StringComparison.OrdinalIgnoreCase))
                    RegisterFallbackResolver(roslynDir);

                return true;
            }
            catch (Exception e)
            {
                _ca = null;
                _cacs = null;
                // Same rule as the R2R skip above: a candidate that fails to load
                // is only worth reporting when no later candidate succeeds.
                // Initialize() emits LastInitError in exactly that case.
                LastInitError = $"Roslyn init failed for {roslynDir}: {e.Message}";
                return false;
            }
        }

        private static bool _fallbackResolverRegistered;
        // Stored so Reinitialize can detach the prior handler before
        // re-registering against a (possibly different) roslynDir.
        private static ResolveEventHandler _fallbackResolveHandler;

        /// <summary>
        /// Scoped AppDomain.AssemblyResolve backstop for the downloaded Roslyn
        /// fallback dir: responds only for simple names that exist as
        /// <c>&lt;dir&gt;/&lt;name&gt;.dll</c>, so it can never hijack unrelated binds.
        /// Registered once per domain, only after the fallback dir actually
        /// loaded. Reinitialize() detaches a previously registered handler so a
        /// re-install to a different dir re-binds to the fresh path.
        /// </summary>
        private static void RegisterFallbackResolver(string roslynDir)
        {
            if (_fallbackResolverRegistered) return;
            _fallbackResolverRegistered = true;

            _fallbackResolveHandler = (_, args) =>
            {
                try
                {
                    var simpleName = new AssemblyName(args.Name).Name;
                    var candidate = Path.Combine(roslynDir, simpleName + ".dll");
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                }
                catch
                {
                    return null;
                }
            };
            AppDomain.CurrentDomain.AssemblyResolve += _fallbackResolveHandler;
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

        // True when `asm` can contribute a metadata reference: a normal
        // file-backed assembly. Dynamic assemblies have no PE image at all, and
        // byte-loaded assemblies (Assembly.Load(byte[]) — every execute_csharp
        // snippet) report an empty Location, so neither can be referenced.
        private static bool IsReferencableAssembly(Assembly asm)
        {
            if (asm == null || asm.IsDynamic) return false;
            try { return !string.IsNullOrEmpty(asm.Location); }
            catch { return false; }
        }

        private static List<object> BuildMetadataReferences(Type metadataRefType)
        {
            // The editor-fd-exhaustion fix. Two independent bugs made this
            // method the dominant descriptor leak — measured at +679 open fds
            // per execute_csharp call on a ~400-assembly editor, tripping
            // Mono's ~1024 IOSelector ceiling ("Could not register to wait for
            // file descriptor N") on the SECOND call of a session:
            //
            //   1. CreateFromAssembly / CreateFromFile keep the referenced PE
            //      file open for the lifetime of the metadata object, and
            //      nothing ever disposed the built list — up to two
            //      descriptors per referenced assembly, held until domain
            //      reload.
            //   2. The cache key was AppDomain.GetAssemblies().Length, which
            //      every DISTINCT snippet bumps (Assembly.Load(byte[]) adds an
            //      assembly to the domain). The cache therefore never hit
            //      across snippets: every call rebuilt all ~400 references and
            //      abandoned the previous set's open descriptors.
            //
            // Fixed by (1) building references through
            // AssemblyMetadata.CreateFromStream + PEStreamOptions.
            // PrefetchMetadata — the PE's metadata section (all a compilation
            // reference ever needs) is copied into memory during the call and
            // the FileStream closes before it returns, so a reference holds NO
            // file descriptor (per-assembly fallback to the old file-backed
            // factories when the API is unavailable); and (2) keying the cache
            // on the count of REFERENCABLE assemblies (IsReferencableAssembly)
            // instead of the raw domain count. Snippet assemblies are
            // byte-loaded (empty Location) and can never be referenced, so
            // they are outside the key — and since assemblies cannot unload
            // without a domain reload, the referencable set only grows, making
            // the count an exact key. The previous list's prefetched metadata
            // is disposed before a rebuild; safe because rebuilds only happen
            // between compiles (Compile runs synchronously on the main
            // thread), so no compilation can still hold the old references.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int referencable = 0;
            foreach (var asm in assemblies)
                if (IsReferencableAssembly(asm)) referencable++;

            if (_cachedReferences != null && _cachedReferenceAssemblyCount == referencable)
                return _cachedReferences;

            bool useStreamFactory = TryResolveMetadataStreamFactory();

            // Legacy file-backed factories — the per-assembly fallback, and the
            // only path on a Roslyn without the AssemblyMetadata stream API.
            var createFromAssembly = FindStaticMethodMinimal(metadataRefType, "CreateFromAssembly", typeof(Assembly));
            var createFromFile = FindStaticMethod(metadataRefType, "CreateFromFile", typeof(string));
            if (!useStreamFactory && createFromAssembly == null && createFromFile == null)
                return null;

            DisposeCachedReferences();
            ReferenceBuildCount++;

            var references = new List<object>(referencable);
            var owned = new List<IDisposable>(referencable);
            foreach (var asm in assemblies)
            {
                if (!IsReferencableAssembly(asm)) continue;

                object reference = null;
                if (useStreamFactory)
                    reference = TryCreateReferenceFromMetadataStream(asm.Location, owned);

                if (reference == null && createFromAssembly != null)
                {
                    try { reference = InvokeWithOptionalDefaults(createFromAssembly, null, asm); }
                    catch { }
                }

                if (reference == null && createFromFile != null)
                {
                    try { reference = InvokeWithOptionalDefaults(createFromFile, null, asm.Location); }
                    catch { }
                }

                if (reference != null)
                    references.Add(reference);
            }

            _cachedReferences = references;
            _cachedReferenceMetadata = owned;
            _cachedReferenceAssemblyCount = referencable;
            return references;
        }

        // Build one PortableExecutableReference for a file-backed assembly
        // WITHOUT keeping the file open: PEStreamOptions.PrefetchMetadata makes
        // the PEReader copy the metadata section into memory inside the
        // CreateFromStream call, so the FileStream can close before the
        // reference is ever used. The created AssemblyMetadata is added to
        // `owned` so a cache rebuild can dispose the prefetched blob. Returns
        // null on any failure (file deleted since load, exotic PE) — the
        // caller falls back to the file-backed factories for that assembly.
        private static object TryCreateReferenceFromMetadataStream(string location, List<IDisposable> owned)
        {
            object metadata = null;
            try
            {
                using (var fs = new FileStream(location, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    metadata = _metadataCreateFromStream.Invoke(null, new object[] { fs, _prefetchMetadataFlag });
                }
                if (metadata == null) return null;

                var reference = InvokeWithOptionalDefaults(_metadataGetReference, metadata);
                if (reference == null)
                {
                    (metadata as IDisposable)?.Dispose();
                    return null;
                }

                if (metadata is IDisposable disposable)
                    owned.Add(disposable);
                return reference;
            }
            catch
            {
                try { (metadata as IDisposable)?.Dispose(); } catch { }
                return null;
            }
        }

        // Resolve AssemblyMetadata.CreateFromStream(Stream, PEStreamOptions),
        // the PrefetchMetadata flag, and AssemblyMetadata.GetReference(...)
        // once per domain. Returns false (latched) when this Roslyn does not
        // expose the API — BuildMetadataReferences then uses the file-backed
        // factories exactly as before the fix.
        private static bool TryResolveMetadataStreamFactory()
        {
            if (_streamFactoryProbed)
                return _metadataCreateFromStream != null && _metadataGetReference != null;
            _streamFactoryProbed = true;
            try
            {
                var metadataType = _ca.GetType("Microsoft.CodeAnalysis.AssemblyMetadata");
                if (metadataType == null) return false;

                foreach (var m in metadataType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "CreateFromStream") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2) continue;
                    if (!typeof(Stream).IsAssignableFrom(ps[0].ParameterType)) continue;
                    if (!ps[1].ParameterType.IsEnum
                        || ps[1].ParameterType.Name != "PEStreamOptions") continue;
                    _prefetchMetadataFlag = Enum.Parse(ps[1].ParameterType, "PrefetchMetadata");
                    _metadataCreateFromStream = m;
                    break;
                }
                if (_metadataCreateFromStream == null) return false;

                // GetReference(documentation = null, aliases = default,
                // embedInteropTypes = false, filePath = null, display = null) —
                // every parameter optional on every Roslyn this host loads.
                _metadataGetReference = metadataType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "GetReference"
                                && m.GetParameters().All(p => p.IsOptional))
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (_metadataGetReference == null)
                {
                    _metadataCreateFromStream = null;
                    _prefetchMetadataFlag = null;
                    return false;
                }
                return true;
            }
            catch
            {
                _metadataCreateFromStream = null;
                _prefetchMetadataFlag = null;
                _metadataGetReference = null;
                return false;
            }
        }

        // Test seam: drop the cached reference list (disposing owned metadata)
        // so a test can force and observe a full rebuild.
        internal static void ResetReferenceCacheForTests() => DisposeCachedReferences();

        // Drop the cached reference list and dispose the prefetched metadata it
        // owned. Safe only between compiles (Compile is synchronous on the main
        // thread): a disposed AssemblyMetadata throws on any later use, so this
        // must never run while a compilation still holds the references.
        private static void DisposeCachedReferences()
        {
            var owned = _cachedReferenceMetadata;
            _cachedReferenceMetadata = null;
            _cachedReferences = null;
            _cachedReferenceAssemblyCount = -1;
            if (owned == null) return;
            foreach (var metadata in owned)
            {
                try { metadata?.Dispose(); } catch { }
            }
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
