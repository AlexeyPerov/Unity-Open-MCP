using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpVerify.Internals.AssetDatabase;
using UnityOpenMcpVerify.Internals.Serialization;
using UnityOpenMcpVerify.Internals.RegexPatterns;
using Object = UnityEngine.Object;

namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    public static class Scanner
    {
        public static List<AssetData> ScanPaths(string[] paths, bool fullScan)
        {
            var results = new List<AssetData>();
            var scopedFileIDs = new HashSet<long>();
            var guidResolveCache = new Dictionary<string, bool>();
            var fileIdCache = new Dictionary<string, HashSet<long>>();

            foreach (var assetPath in paths)
            {
                if (string.IsNullOrEmpty(assetPath)) continue;
                var assetObject = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (assetObject == null) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out _, out long fileId))
                    scopedFileIDs.Add(fileId);
            }

            var regexFileAndGuid = SharedRegex.ExternalFileAndGuid;
            var regexFileID = SharedRegex.LocalFileId;
            var regexTypeStart = SharedRegex.FieldTypeStart;

            foreach (var assetPath in paths)
            {
                if (string.IsNullOrEmpty(assetPath)) continue;

                var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                if (!AssetTypeUtilities.IsValidType(assetPath, type)) continue;
                if (!AssetTypeUtilities.CanAnalyzeType(type)) continue;

                var lines = YamlUtilities.TryReadAllLines(assetPath);
                if (lines.Length == 0) continue;

                var assetObject = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (assetObject == null) continue;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out var guid, out _))
                    continue;

                var refsData = new AssetReferencesData();
                var isScene = type == typeof(SceneAsset);

                ParseReferences(lines, isScene, regexFileAndGuid, regexFileID, regexTypeStart, refsData, guidResolveCache);
                CountLocalUsages(lines, refsData);
                ScanMissingScripts(lines, refsData);

                if (fullScan)
                    ScanUnityEventReferences(lines, refsData);

                if (fullScan)
                    ScanDuplicateComponents(assetPath, type, refsData);

                if (fullScan)
                    ScanInvalidLayers(lines, refsData);

                var typeName = AssetTypeUtilities.GetReadableTypeName(type);
                results.Add(new AssetData(assetPath, type, typeName, guid, refsData));
            }

            ResolveReferences(results, scopedFileIDs, guidResolveCache, fileIdCache);
            return results;
        }

        private static void ParseReferences(
            string[] lines, bool isScene,
            Regex regexFileAndGuid, Regex regexFileID, Regex regexTypeStart,
            AssetReferencesData refsData, Dictionary<string, bool> guidResolveCache)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (YamlUtilities.IsSystemReference(line, YamlUtilities.KeyWordsToIgnore)) continue;
                if (isScene && YamlUtilities.IsSystemReference(line, YamlUtilities.KeyWordsToIgnoreInSceneAsset)) continue;

                if (line.Contains("guid:"))
                {
                    var matches = regexFileAndGuid.Matches(line);
                    foreach (Match match in matches)
                    {
                        long.TryParse(match.Groups[1].Value, out var localFileID);
                        var externalGuid = match.Groups[2].Value;

                        var guidValid = !externalGuid.StartsWith("0000000000");
                        var localIdValid = localFileID > 0;

                        if (!guidValid && !localIdValid) continue;

                        var referenceData = new ExternalReferenceRegistry(localIdValid, guidValid, localFileID, externalGuid, i);

                        if (guidValid)
                        {
                            referenceData.GuidAssetPath = AssetDatabase.GUIDToAssetPath(externalGuid);
                            referenceData.GuidExistsInAssets = VerifyGuidResolves(referenceData.GuidAssetPath, guidResolveCache);

                            if (!referenceData.GuidExistsInAssets)
                                RecordGuidPlaceData(i, lines, referenceData);
                            else
                                referenceData.Sample.Add(line);
                        }
                        else
                        {
                            referenceData.Sample.Add(line);
                        }

                        FindFieldType(regexTypeStart, i, lines, referenceData);
                        refsData.ExternalReferences.Add(referenceData);
                    }
                }
                else if (line.Contains("fileID:"))
                {
                    var localMatches = regexFileID.Matches(line);
                    foreach (Match match in localMatches)
                    {
                        var idStr = match.Value;
                        var digitsOnly = idStr.Replace("{fileID: ", "").Replace("}", "").Trim();

                        if (digitsOnly == "0")
                        {
                            refsData.EmptyFileIDs.Add(new EmptyLocalFileIDRegistry(i));
                        }
                        else if (long.TryParse(digitsOnly, out var localId))
                        {
                            refsData.LocalReferences.Add(new LocalReferenceRegistry(localId, i));
                        }
                    }
                }
            }
        }

        private static void CountLocalUsages(string[] lines, AssetReferencesData refsData)
        {
            // V9: previously this method was O(R × L) — for each LocalReference
            // it built a fresh uncompiled Regex (`new Regex(...)` per registry)
            // and ran IsMatch over every line. A real scene with R references
            // and L lines burned R×L interpreted matches inside the
            // VerifyRunMode.Checkpoint 2000 ms budget, so validate_edit
            // effectively hung the Editor.
            //
            // Single-pass replacement: walk every line once, find every
            // `fileID:\s*(\d+)` match, and record (per distinct fileID on the
            // line) the line index. Then for each registry the usage count is
            // the number of recorded lines that are NOT the registry's own
            // declaring line — exactly the semantics of the original
            // `if (j == registry.Line) continue; if (pattern.IsMatch(...)) usages++;`
            // (one increment per matching line, declaring line excluded).
            //
            // The lookup is keyed by long (the parsed fileID), not by the IdStr
            // token, so "123" no longer accidentally matches inside "114123456"
            // or inside a GUID — the same correctness property the original
            // `\b`-anchored regex had, without the per-reference cost.
            var linesById = new Dictionary<long, List<int>>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var index = 0;
                // Cheap pre-filter: skip lines that cannot contain a fileID ref
                // at all. Avoids running the regex on boilerplate lines.
                if (line.IndexOf("fileID:", StringComparison.Ordinal) < 0) continue;

                var seenOnLine = new HashSet<long>();
                while (index < line.Length)
                {
                    var at = line.IndexOf("fileID:", index, StringComparison.Ordinal);
                    if (at < 0) break;
                    // Parse the digits after `fileID:` (and any whitespace).
                    var p = at + "fileID:".Length;
                    while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
                    var start = p;
                    while (p < line.Length && line[p] >= '0' && line[p] <= '9') p++;
                    if (p > start)
                    {
                        // Match the original regex's `\b` boundary: the digit
                        // run must end at a word boundary. After digits (word
                        // chars), the next char must be a non-word char or EOL.
                        // In valid Unity YAML `fileID:` is always followed by
                        // `,`/`}`/whitespace, so this never rejects a real
                        // reference; it only rejects malformed input like
                        // `fileID: 123x` the same way the original `\b` did.
                        var boundaryOk = p >= line.Length || !IsWordChar(line[p]);
                        if (boundaryOk && long.TryParse(line.AsSpan(start, p - start), out var id))
                        {
                            if (seenOnLine.Add(id))
                            {
                                if (!linesById.TryGetValue(id, out var list))
                                {
                                    list = new List<int>();
                                    linesById[id] = list;
                                }
                                list.Add(i);
                            }
                        }
                    }
                    index = p > index ? p : at + 1;
                }
            }

            foreach (var registry in refsData.LocalReferences)
            {
                if (!linesById.TryGetValue(registry.Id, out var list) || list.Count == 0)
                {
                    registry.LocalUsagesCount = 0;
                    continue;
                }
                var usages = 0;
                foreach (var lineIndex in list)
                {
                    if (lineIndex == registry.Line) continue;
                    usages++;
                }
                registry.LocalUsagesCount = usages;
            }
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '_';
        }

        private static void ResolveReferences(List<AssetData> assets, HashSet<long> scopedFileIDs,
            Dictionary<string, bool> guidResolveCache, Dictionary<string, HashSet<long>> fileIdCache)
        {
            foreach (var asset in assets)
            {
                foreach (var registry in asset.RefsData.LocalReferences)
                    registry.ExistsInAssets = scopedFileIDs.Contains(registry.Id);

                foreach (var registry in asset.RefsData.ExternalReferences)
                {
                    if (registry.FileIDValid)
                    {
                        registry.FileIDExistsInAssets = scopedFileIDs.Contains(registry.FileID) ||
                            asset.RefsData.LocalReferences.Any(l => l.Id == registry.FileID);
                    }

                    if (registry.GuidValid && registry.GuidExistsInAssets && registry.FileIDValid)
                    {
                        var fileIds = GetFileIdsForPath(registry.GuidAssetPath, fileIdCache);
                        registry.FileIDExistsInTargetAsset = fileIds != null && fileIds.Contains(registry.FileID);
                    }
                    else
                    {
                        registry.FileIDExistsInTargetAsset = true;
                    }
                }

                asset.RefsData.CalculateCounters();

                foreach (var extRef in asset.RefsData.ExternalReferences)
                {
                    if (!string.IsNullOrEmpty(extRef.FieldType) && extRef.WarningLevel > 0)
                        asset.MissingFieldTypes.Add(extRef.FieldType);
                }
            }
        }

        private static bool VerifyGuidResolves(string guidPath, Dictionary<string, bool> cache)
        {
            if (string.IsNullOrEmpty(guidPath))
                return false;

            if (cache.TryGetValue(guidPath, out var cached))
                return cached;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(guidPath);
            var resolved = asset != null;
            cache[guidPath] = resolved;
            return resolved;
        }

        private static HashSet<long> GetFileIdsForPath(string assetPath, Dictionary<string, HashSet<long>> cache)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            if (cache.TryGetValue(assetPath, out var cached))
                return cached;

            HashSet<long> fileIds = null;
            var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (allAssets != null && allAssets.Length > 0)
            {
                fileIds = new HashSet<long>();
                foreach (var subAsset in allAssets)
                {
                    if (subAsset == null) continue;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(subAsset, out _, out long subFileId))
                        fileIds.Add(subFileId);
                }
            }

            cache[assetPath] = fileIds;
            return fileIds;
        }

        private static void FindFieldType(Regex regexTypeStart, int index, string[] lines, ExternalReferenceRegistry referenceData)
        {
            for (var j = index - 1; j >= 0; j--)
            {
                var line = lines[j];
                if (line.StartsWith("  ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)) continue;
                var match = regexTypeStart.Match(line);
                if (match.Success)
                {
                    referenceData.FieldType = line.Trim();
                    return;
                }
            }
        }

        private static void RecordGuidPlaceData(int index, string[] lines, ExternalReferenceRegistry referenceData)
        {
            for (var j = index - 1; j >= 0; j--)
            {
                var line = lines[j];
                if (line.Contains("m_Name:") || line.Contains("m_TagString:"))
                    continue;

                if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal))
                    continue;

                referenceData.HolderName = line.Trim().TrimEnd(':');
                break;
            }

            for (var j = Math.Max(0, index - 1); j <= Math.Min(lines.Length - 1, index + 2); j++)
                referenceData.Sample.Add(lines[j]);
        }

        private static void ScanMissingScripts(string[] lines, AssetReferencesData refsData)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("m_Script:")) continue;

                var match = SharedRegex.ScriptGuid.Match(line);
                if (!match.Success) continue;

                var guid = match.Groups[1].Value;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                    refsData.MissingScripts.Add(new MissingScriptEntry(guid, i));
            }
        }

        private static void ScanUnityEventReferences(string[] lines, AssetReferencesData refsData)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("m_TargetAssemblyTypeName:")) continue;

                var typeNameMatch = SharedRegex.UnityEventTargetType.Match(line);
                if (!typeNameMatch.Success) continue;

                var typeName = typeNameMatch.Groups[1].Value;

                string methodName = null;
                for (var j = i + 1; j < Math.Min(lines.Length, i + 10); j++)
                {
                    var methodMatch = SharedRegex.UnityEventMethodName.Match(lines[j]);
                    if (methodMatch.Success)
                    {
                        methodName = methodMatch.Groups[1].Value;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(methodName))
                {
                    var resolvedType = ResolveType(typeName);
                    if (resolvedType != null)
                    {
                        var method = resolvedType.GetMethod(methodName,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                        if (method == null)
                            refsData.MissingMethods.Add(new MissingMethodEntry(typeName, methodName, i));
                    }
                }

                for (var j = i + 1; j < Math.Min(lines.Length, i + 15); j++)
                {
                    var argTypeMatch = SharedRegex.UnityEventArgType.Match(lines[j]);
                    if (!argTypeMatch.Success) continue;

                    var argTypeName = argTypeMatch.Groups[1].Value;
                    var resolvedArgType = ResolveType(argTypeName);
                    if (resolvedArgType == null)
                        refsData.TypeMismatches.Add(new TypeMismatchEntry(argTypeName, j));

                    break;
                }
            }
        }

        private static Type ResolveType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(typeName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static void ScanDuplicateComponents(string assetPath, Type assetType, AssetReferencesData refsData)
        {
            if (assetType != typeof(GameObject)) return;

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go == null) return;

            var transforms = go.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                var components = t.GetComponents<Component>();
                var typeCounts = new Dictionary<Type, int>();

                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    var compType = comp.GetType();
                    typeCounts.TryGetValue(compType, out var count);
                    typeCounts[compType] = count + 1;
                }

                foreach (var kvp in typeCounts)
                {
                    if (kvp.Value > 1)
                        refsData.DuplicateComponents.Add(new DuplicateComponentEntry(
                            kvp.Key.Name, kvp.Value, t.gameObject.name));
                }
            }
        }

        private static void ScanInvalidLayers(string[] lines, AssetReferencesData refsData)
        {
            var validLayers = new HashSet<int>();
            for (var i = 0; i < 32; i++)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    validLayers.Add(i);
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var match = SharedRegex.LayerIndex.Match(lines[i]);
                if (!match.Success) continue;

                // int.TryParse instead of int.Parse — a corrupted m_Layer value
                // exceeding int.MaxValue (e.g. 99999999999) must not throw and
                // abort missing_references for all remaining assets. Skip the
                // line on parse failure; the regex already validated digits-only.
                if (!int.TryParse(match.Groups[1].Value, out var layerIndex))
                    continue;
                if (!validLayers.Contains(layerIndex))
                    refsData.InvalidLayers.Add(new InvalidLayerEntry(layerIndex, i));
            }
        }
    }
}
