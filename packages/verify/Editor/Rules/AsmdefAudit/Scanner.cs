using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Rules.AsmdefAudit
{
    public static class Scanner
    {
        public static void ScanPaths(string[] paths, List<AsmdefData> sink)
        {
            if (paths == null || paths.Length == 0) return;

            // Lazy-built resolution caches — built once per scan over the
            // current compilation assembly set + precompiled DLLs. Bare-name
            // references resolve when a matching compiled assembly exists.
            HashSet<string> compiledNames = null;
            HashSet<string> compiledSimpleNames = null;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!IsAsmdefPath(path)) continue;
                if (!File.Exists(path)) continue;

                var data = new AsmdefData(path);
                string json;
                try { json = File.ReadAllText(path); }
                catch (Exception e)
                {
                    data.ParseFailed = true;
                    data.ParseError = e.Message;
                    sink.Add(data);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    data.ParseFailed = true;
                    data.ParseError = "empty file";
                    sink.Add(data);
                    continue;
                }

                ParseAsmdefJson(path, json, data);
                if (data.ParseFailed)
                {
                    sink.Add(data);
                    continue;
                }

                if (compiledNames == null)
                {
                    BuildAssemblyNameIndex(out compiledNames, out compiledSimpleNames);
                }

                ResolveReferences(data, compiledNames, compiledSimpleNames);
                sink.Add(data);
            }
        }

        // -------------------------------------------------------------------
        // JSON parsing — minimal structural reader for the asmdef shape.
        //
        // Unity's asmdef JSON is single-object with `references` as a string
        // array. We avoid a full JSON parser dependency by walking the tokens
        // we care about: `name`, `rootNamespace`, and each `references` entry.
        // A real JSON library would be cleaner, but pulling one in just for
        // this rule is heavier than the line-based walk below. The walk is
        // tolerant of the formatting Unity emits (one key per line).
        // -------------------------------------------------------------------

        private static void ParseAsmdefJson(string assetPath, string json, AsmdefData data)
        {
            var lines = json.Replace("\r\n", "\n").Split('\n');
            var inReferences = false;
            var braceDepth = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var line = raw.Trim();

                // Track brace depth so we know when we leave the references
                // array's enclosing object. Unity emits one key per line, so
                // a depth counter on the trimmed line is sufficient.
                braceDepth += CountUnescaped(raw, '{') - CountUnescaped(raw, '}');

                if (line.StartsWith("\"references\"", StringComparison.Ordinal))
                {
                    inReferences = true;
                    // Same-line array close: "references": []
                    if (line.Contains("]")) inReferences = false;
                    continue;
                }

                if (inReferences)
                {
                    if (line.StartsWith("]", StringComparison.Ordinal) || line.StartsWith("}", StringComparison.Ordinal))
                    {
                        inReferences = false;
                        continue;
                    }

                    var entry = ExtractStringLiteral(line);
                    if (entry != null)
                    {
                        data.References.Add(new AsmdefReference(entry, i + 1, resolves: false));
                    }
                    continue;
                }

                if (line.StartsWith("\"name\"", StringComparison.Ordinal))
                {
                    data.Name = ExtractStringLiteral(line);
                    continue;
                }

                if (line.StartsWith("\"rootNamespace\"", StringComparison.Ordinal))
                {
                    data.RootNamespace = ExtractStringLiteral(line);
                    continue;
                }
            }

            // Validate the document shaped like an asmdef at all. Unity rejects
            // a top-level non-object; we mirror that with a parse-failed flag.
            if (braceDepth < 0)
            {
                data.ParseFailed = true;
                data.ParseError = "unbalanced braces";
            }
        }

        private static string ExtractStringLiteral(string line)
        {
            var start = line.IndexOf('"');
            if (start < 0) return null;
            var valueStart = start + 1;
            var end = line.IndexOf('"', valueStart);
            if (end < 0) return null;
            return line.Substring(valueStart, end - valueStart);
        }

        private static int CountUnescaped(string s, char c)
        {
            var count = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == c) count++;
            }
            return count;
        }

        // -------------------------------------------------------------------
        // Reference resolution
        // -------------------------------------------------------------------

        private static void ResolveReferences(AsmdefData data, HashSet<string> compiledNames, HashSet<string> compiledSimpleNames)
        {
            var asmdefGuid = AssetDatabase.AssetPathToGUID(data.Path);

            for (var i = 0; i < data.References.Count; i++)
            {
                var r = data.References[i];
                var resolves = ReferenceResolves(r.Reference, asmdefGuid, compiledNames, compiledSimpleNames);
                data.References[i] = new AsmdefReference(r.Reference, r.Line, resolves);
            }
        }

        private static bool ReferenceResolves(string reference, string selfAsmdefGuid, HashSet<string> compiledNames, HashSet<string> compiledSimpleNames)
        {
            if (string.IsNullOrEmpty(reference)) return false;

            // GUID form: "GUID:abc123..." — resolve via the asset DB.
            if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            {
                var guid = reference.Substring(5).Trim();
                if (!SharedRegex.Guid32Hex.IsMatch(guid)) return false;
                if (guid == selfAsmdefGuid) return true; // self-ref via GUID still resolves
                var path = AssetDatabase.GUIDToAssetPath(guid);
                return !string.IsNullOrEmpty(path);
            }

            // Built-in special references that always resolve.
            if (reference == "UnityEngine" || reference.StartsWith("UnityEngine.", StringComparison.Ordinal))
                return true;
            if (reference == "UnityEditor" || reference.StartsWith("UnityEditor.", StringComparison.Ordinal))
                return true;
            if (reference == "System" || reference.StartsWith("System.", StringComparison.Ordinal))
                return true;

            // Bare assembly name — resolve against the compiled set. Unity's
            // own compile graph is the authority; if it is not in
            // CompilationPipeline.GetAssemblies() and not a known precompiled
            // DLL, the asmdef will not compile.
            if (compiledNames.Contains(reference)) return true;
            if (compiledSimpleNames.Contains(reference)) return true;

            return false;
        }

        private static void BuildAssemblyNameIndex(out HashSet<string> fullNames, out HashSet<string> simpleNames)
        {
            fullNames = new HashSet<string>(StringComparer.Ordinal);
            simpleNames = new HashSet<string>(StringComparer.Ordinal);

            // Compiled assemblies (asmdef + package sources Unity built).
            try
            {
                foreach (var asm in UnityEditor.Compilation.CompilationPipeline.GetAssemblies())
                {
                    if (!string.IsNullOrEmpty(asm.name))
                    {
                        fullNames.Add(asm.name);
                        var simple = Path.GetFileNameWithoutExtension(asm.name);
                        if (!string.IsNullOrEmpty(simple)) simpleNames.Add(simple);
                    }
                }
            }
            catch { }

            // Precompiled DLLs under Assets/ + Packages/.
            foreach (var dllGuid in AssetDatabase.FindAssets("l:PreloadAssembly"))
            {
                var p = AssetDatabase.GUIDToAssetPath(dllGuid);
                if (string.IsNullOrEmpty(p)) continue;
                var simple = Path.GetFileNameWithoutExtension(p);
                if (!string.IsNullOrEmpty(simple)) simpleNames.Add(simple);
            }
        }

        private static bool IsAsmdefPath(string path)
        {
            return path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase);
        }
    }
}
