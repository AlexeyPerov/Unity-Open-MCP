using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge.MetaTools
{
    static class RoslynHost
    {
        static Assembly _ca;
        static Assembly _cacs;
        static bool _initAttempted;

        public static bool IsAvailable { get; private set; }

        public static bool Initialize()
        {
            if (IsAvailable) return true;
            if (_initAttempted) return false;
            _initAttempted = true;

            try
            {
                var contentsPath = EditorApplication.applicationContentsPath;
                var roslynDir = FindRoslynDirectory(contentsPath);
                if (roslynDir == null)
                {
                    Debug.LogWarning("[Unity Agent Bridge] Roslyn directory not found in Unity installation");
                    return false;
                }

                foreach (var dep in Directory.GetFiles(roslynDir, "System.*.dll"))
                {
                    try { Assembly.LoadFrom(dep); } catch { }
                }

                _ca = Assembly.LoadFrom(Path.Combine(roslynDir, "Microsoft.CodeAnalysis.dll"));
                _cacs = Assembly.LoadFrom(Path.Combine(roslynDir, "Microsoft.CodeAnalysis.CSharp.dll"));
                IsAvailable = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Unity Agent Bridge] Roslyn init failed: {e.Message}");
                return false;
            }
        }

        static string FindRoslynDirectory(string contentsPath)
        {
            var candidates = new[]
            {
                Path.Combine(contentsPath, "DotNetSdkRoslyn"),
                Path.Combine(contentsPath, "Tools", "roslyn"),
            };

            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir) &&
                    File.Exists(Path.Combine(dir, "Microsoft.CodeAnalysis.dll")) &&
                    File.Exists(Path.Combine(dir, "Microsoft.CodeAnalysis.CSharp.dll")))
                    return dir;
            }

            return null;
        }

        public static (byte[] pe, string errors) Compile(string source)
        {
            var syntaxTreeType = _ca.GetType("Microsoft.CodeAnalysis.SyntaxTree");
            var metadataRefType = _ca.GetType("Microsoft.CodeAnalysis.MetadataReference");
            var enumerableOfSyntaxTree = typeof(IEnumerable<>).MakeGenericType(syntaxTreeType);
            var enumerableOfMetadataRef = typeof(IEnumerable<>).MakeGenericType(metadataRefType);
            var cSharpCompilationOptionsType = _cacs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
            var outputKindType = _ca.GetType("Microsoft.CodeAnalysis.OutputKind");

            var sf = _cacs.GetType("Microsoft.CodeAnalysis.CSharp.SyntaxFactory");
            var parseText = sf.GetMethods()
                .FirstOrDefault(m => m.Name == "ParseText" &&
                                     m.GetParameters().Length >= 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(string));

            if (parseText == null)
                return (null, "Could not find SyntaxFactory.ParseText method");

            object syntaxTree;
            if (parseText.GetParameters().Length == 1)
                syntaxTree = parseText.Invoke(null, new object[] { source });
            else
            {
                var defaultParams = new object[parseText.GetParameters().Length];
                defaultParams[0] = source;
                for (int p = 1; p < defaultParams.Length; p++)
                    defaultParams[p] = Type.Missing;
                syntaxTree = parseText.Invoke(null, defaultParams);
            }

            var createFromFile = metadataRefType.GetMethod("CreateFromFile", new[] { typeof(string) });
            if (createFromFile == null)
                return (null, "Could not find MetadataReference.CreateFromFile method");

            var references = new List<object>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    if (string.IsNullOrEmpty(asm.Location)) continue;
                    references.Add(createFromFile.Invoke(null, new object[] { asm.Location }));
                }
                catch { }
            }

            var dllOutput = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            object options;
            var ctors = cSharpCompilationOptionsType.GetConstructors();
            var ctor = ctors.FirstOrDefault(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == outputKindType);
            if (ctor != null)
            {
                var cparams = new object[ctor.GetParameters().Length];
                cparams[0] = dllOutput;
                for (int p = 1; p < cparams.Length; p++)
                    cparams[p] = Type.Missing;
                options = ctor.Invoke(cparams);
            }
            else
            {
                options = Activator.CreateInstance(cSharpCompilationOptionsType);
            }

            var compType = _cacs.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            var createMethod = compType.GetMethod("Create", new[] { typeof(string) });
            if (createMethod == null)
                return (null, "Could not find CSharpCompilation.Create method");

            var compilation = createMethod.Invoke(null, new object[] { "UnityAgentSnippet" });

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

            var peStream = new MemoryStream();
            var emitMethod = compType.GetMethod("Emit", new[] { typeof(Stream) });
            if (emitMethod == null)
                return (null, "Could not find compilation Emit method");

            var emitResult = emitMethod.Invoke(compilation, new object[] { peStream });

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

                foreach (var d in diags)
                {
                    var severity = severityProp.GetValue(d);
                    if (severity.ToString() == "Error")
                    {
                        var toString = diagnosticType.GetProperty("ToString");
                        if (toString != null)
                        {
                            var msg = toString.GetValue(d);
                            errorMessages.Add(msg?.ToString() ?? "Unknown error");
                        }
                        else
                        {
                            var getter = diagnosticType.GetMethod("GetMessage", Type.EmptyTypes);
                            errorMessages.Add(getter?.Invoke(d, null)?.ToString() ?? "Unknown error");
                        }
                    }
                }

                return (null, string.Join("\n", errorMessages));
            }

            return (peStream.ToArray(), null);
        }
    }
}
