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

            Debug.LogWarning($"[Unity Agent Bridge] {LastInitError}");
            return false;
        }

        static IEnumerable<string> GetRoslynDirectoryCandidates(string contentsPath)
        {
            yield return Path.Combine(contentsPath, "Resources", "Scripting", "MonoBleedingEdge", "lib", "mono", "msbuild", "Current", "bin", "Roslyn");
            yield return Path.Combine(contentsPath, "DotNetSdkRoslyn");
            yield return Path.Combine(contentsPath, "Tools", "roslyn");
        }

        static bool TryLoadRoslyn(string roslynDir)
        {
            if (!Directory.Exists(roslynDir))
                return false;

            var codeAnalysisPath = Path.Combine(roslynDir, "Microsoft.CodeAnalysis.dll");
            var codeAnalysisCSharpPath = Path.Combine(roslynDir, "Microsoft.CodeAnalysis.CSharp.dll");
            if (!File.Exists(codeAnalysisPath) || !File.Exists(codeAnalysisCSharpPath))
                return false;

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
                Debug.LogWarning($"[Unity Agent Bridge] {LastInitError}");
                return false;
            }
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

        static (byte[] pe, string errors) CompileInternal(string source)
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

            var compilation = InvokeWithOptionalDefaults(createMethod, null, "UnityAgentSnippet");

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

        static (object syntaxTree, string error) ParseSyntaxTree(string source, Type syntaxTreeType)
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

        static List<object> BuildMetadataReferences(Type metadataRefType)
        {
            var createFromAssembly = FindStaticMethodMinimal(metadataRefType, "CreateFromAssembly", typeof(Assembly));
            var createFromFile = FindStaticMethod(metadataRefType, "CreateFromFile", typeof(string));
            if (createFromAssembly == null && createFromFile == null)
                return null;

            var references = new List<object>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
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

            return references;
        }

        static MethodInfo FindStaticMethod(Type type, string name, Type firstParamType)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name &&
                            m.GetParameters().Length >= 1 &&
                            ParameterTypeMatches(m.GetParameters()[0].ParameterType, firstParamType))
                .OrderByDescending(m => m.GetParameters().Count(p => p.IsOptional))
                .ThenBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        static MethodInfo FindStaticMethodMinimal(Type type, string name, Type firstParamType)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name &&
                            m.GetParameters().Length >= 1 &&
                            ParameterTypeMatches(m.GetParameters()[0].ParameterType, firstParamType))
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        static MethodInfo FindInstanceMethod(Type type, string name, Func<ParameterInfo, bool> firstParamMatches)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name &&
                            m.GetParameters().Length >= 1 &&
                            firstParamMatches(m.GetParameters()[0]))
                .OrderByDescending(m => m.GetParameters().Count(p => p.IsOptional))
                .ThenBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        static bool ParameterTypeMatches(Type parameterType, Type expectedType)
        {
            return parameterType == expectedType ||
                   string.Equals(parameterType.FullName, expectedType.FullName, StringComparison.Ordinal);
        }

        static object InvokeWithOptionalDefaults(MethodBase method, object target, params object[] providedArgs)
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
