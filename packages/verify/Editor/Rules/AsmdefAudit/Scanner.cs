using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityOpenMcpVerify.Internals.RegexPatterns;

namespace UnityOpenMcpVerify.Rules.AsmdefAudit
{
    public static class Scanner
    {
        public static void ScanPaths(string[] paths, AsmdefScanSettings settings, List<AsmdefData> sink)
        {
            if (paths == null || paths.Length == 0) return;

            // Lazy-built resolution caches for the broken-reference check (the
            // source scanner does not resolve refs; this is a verify-package
            // addition). Bare-name references resolve when a matching compiled
            // assembly exists.
            HashSet<string> compiledNames = null;
            HashSet<string> compiledSimpleNames = null;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!IsAsmdefPath(path)) continue;
                if (!File.Exists(path)) continue;
                // Packages/ asmdefs are excluded — they are not agent-editable.
                if (path.StartsWith("Packages/", StringComparison.Ordinal)) continue;

                string json;
                try { json = File.ReadAllText(path); }
                catch (Exception e)
                {
                    sink.Add(new AsmdefData(path)
                    {
                        ParseFailed = true,
                        ParseError = e.Message,
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    sink.Add(new AsmdefData(path)
                    {
                        ParseFailed = true,
                        ParseError = "empty file",
                    });
                    continue;
                }

                var data = ParseAsmDef(path, json);
                if (data == null)
                {
                    sink.Add(new AsmdefData(path)
                    {
                        ParseFailed = true,
                        ParseError = "parse error",
                    });
                    continue;
                }

                if (settings.CheckBrokenReferences)
                {
                    if (compiledNames == null)
                        BuildAssemblyNameIndex(out compiledNames, out compiledSimpleNames);
                    ResolveReferences(data, compiledNames, compiledSimpleNames);
                }

                sink.Add(data);
            }
        }

        // -------------------------------------------------------------------
        // JSON parsing — ported verbatim from the source scanner.
        //
        // Unity's asmdef JSON is a single object with string / string-array
        // fields. The source deliberately avoids a JSON library and uses
        // hand-rolled string extraction; the parsers below are a verbatim port
        // (including the no-escape-handling quirk) so behaviour matches.
        // -------------------------------------------------------------------

        private static AsmdefData ParseAsmDef(string assetPath, string json)
        {
            try
            {
                var data = new AsmdefData(assetPath);
                data.Name = ExtractStringField(json, "name");
                data.RootNamespace = ExtractStringField(json, "rootNamespace");
                data.AutoReferenced = ExtractBoolField(json, "autoReferenced", defaultIfMissing: true);
                data.AnyPlatform = ExtractBoolField(json, "anyPlatform", defaultIfMissing: true);
                ExtractStringArray(json, "references", data.References);
                ExtractStringArray(json, "includePlatforms", data.IncludePlatforms);
                ExtractStringArray(json, "excludePlatforms", data.ExcludePlatforms);
                ParseVersionDefines(json, data);
                data.IsEditorOnly = DeriveIsEditorOnly(data);
                return data;
            }
            catch
            {
                return null;
            }
        }

        // Ported quirk: this derivation mirrors the source exactly.
        //   IsEditorOnly =
        //       IncludePlatforms.Contains("Editor")
        //    OR (IncludePlatforms.Count == 0 AND ExcludePlatforms.Count > 0 AND !AnyPlatform)
        //    OR (!AnyPlatform AND ExcludePlatforms.Count == 0 AND IncludePlatforms.Count == 0)
        private static bool DeriveIsEditorOnly(AsmdefData data)
        {
            if (data.IncludePlatforms.Contains("Editor")) return true;
            if (data.IncludePlatforms.Count == 0 && data.ExcludePlatforms.Count > 0 && !data.AnyPlatform) return true;
            if (!data.AnyPlatform && data.ExcludePlatforms.Count == 0 && data.IncludePlatforms.Count == 0) return true;
            return false;
        }

        private static string ExtractStringField(string json, string fieldName)
        {
            var marker = "\"" + fieldName + "\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return "";
            idx = json.IndexOf(':', idx + marker.Length);
            if (idx < 0) return "";
            var start = idx + 1;
            var quoteStart = json.IndexOf('"', start);
            if (quoteStart < 0) return "";
            var quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return "";
            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }

        private static bool ExtractBoolField(string json, string fieldName, bool defaultIfMissing)
        {
            var marker = "\"" + fieldName + "\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return defaultIfMissing;
            idx = json.IndexOf(':', idx + marker.Length);
            if (idx < 0) return defaultIfMissing;
            var start = idx + 1;
            // Skip whitespace.
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\r' || json[start] == '\n'))
                start++;
            var rest = json.Substring(start);
            if (rest.StartsWith("true", StringComparison.Ordinal)) return true;
            if (rest.StartsWith("false", StringComparison.Ordinal)) return false;
            return defaultIfMissing;
        }

        private static void ExtractStringArray(string json, string fieldName, List<string> target)
        {
            target.Clear();
            var marker = "\"" + fieldName + "\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;
            var openBracket = json.IndexOf('[', idx + marker.Length);
            if (openBracket < 0) return;
            // Bracket-match the array (asmdef reference arrays contain only
            // string literals, so depth never exceeds 1 — but match anyway).
            var depth = 0;
            var closeBracket = -1;
            for (var i = openBracket; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0) { closeBracket = i; break; }
                }
            }
            if (closeBracket < 0) return;
            var inner = json.Substring(openBracket + 1, closeBracket - openBracket - 1);
            var parts = inner.Split(',');
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length < 2) continue;
                var firstQuote = trimmed.IndexOf('"');
                var lastQuote = trimmed.LastIndexOf('"');
                if (firstQuote < 0 || lastQuote <= firstQuote) continue;
                target.Add(trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1));
            }
        }

        private static void ParseVersionDefines(string json, AsmdefData data)
        {
            var marker = "\"versionDefines\"";
            var idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;
            var openBracket = json.IndexOf('[', idx + marker.Length);
            if (openBracket < 0) return;
            foreach (var obj in SplitObjects(json, openBracket))
            {
                var package = ExtractStringField(obj, "name");
                var expression = ExtractStringField(obj, "expression");
                var symbol = ExtractStringField(obj, "define");
                data.VersionDefines.Add(new VersionDefineData
                {
                    Package = package,
                    Expression = expression,
                    Symbol = symbol,
                });
            }
        }

        // Splits a JSON array body into top-level {...} object substrings,
        // respecting brace depth. Ported verbatim.
        private static IEnumerable<string> SplitObjects(string json, int startSearch)
        {
            var objects = new List<string>();
            var i = startSearch;
            while (i < json.Length)
            {
                var open = json.IndexOf('{', i);
                if (open < 0) break;
                var depth = 0;
                var close = -1;
                for (var j = open; j < json.Length; j++)
                {
                    if (json[j] == '{') depth++;
                    else if (json[j] == '}')
                    {
                        depth--;
                        if (depth == 0) { close = j; break; }
                    }
                }
                if (close < 0) break;
                objects.Add(json.Substring(open, close - open + 1));
                i = close + 1;
            }
            return objects;
        }

        // -------------------------------------------------------------------
        // Broken-reference resolution (verify-package addition).
        // -------------------------------------------------------------------

        private static void ResolveReferences(AsmdefData data, HashSet<string> compiledNames, HashSet<string> compiledSimpleNames)
        {
            var selfGuid = AssetDatabase.AssetPathToGUID(data.Path);
            data.ResolvedReferences.Clear();
            for (var i = 0; i < data.References.Count; i++)
            {
                var reference = data.References[i];
                var resolves = ReferenceResolves(reference, selfGuid, compiledNames, compiledSimpleNames);
                data.ResolvedReferences.Add(new AsmdefReference(reference, i + 1, resolves));
            }
        }

        private static bool ReferenceResolves(string reference, string selfAsmdefGuid, HashSet<string> compiledNames, HashSet<string> compiledSimpleNames)
        {
            if (string.IsNullOrEmpty(reference)) return false;

            if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            {
                var guid = reference.Substring(5).Trim();
                if (!SharedRegex.Guid32Hex.IsMatch(guid)) return false;
                if (guid == selfAsmdefGuid) return true;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                return !string.IsNullOrEmpty(path);
            }

            if (reference == "UnityEngine" || reference.StartsWith("UnityEngine.", StringComparison.Ordinal))
                return true;
            if (reference == "UnityEditor" || reference.StartsWith("UnityEditor.", StringComparison.Ordinal))
                return true;
            if (reference == "System" || reference.StartsWith("System.", StringComparison.Ordinal))
                return true;

            if (compiledNames.Contains(reference)) return true;
            if (compiledSimpleNames.Contains(reference)) return true;

            return false;
        }

        private static void BuildAssemblyNameIndex(out HashSet<string> fullNames, out HashSet<string> simpleNames)
        {
            fullNames = new HashSet<string>(StringComparer.Ordinal);
            simpleNames = new HashSet<string>(StringComparer.Ordinal);

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
