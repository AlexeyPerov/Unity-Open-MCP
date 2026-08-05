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
                // feedback-04-08-opus §1 — a .unity scene is NOT an asset
                // container for LoadAllAssetsAtPath: calling it on a scene path
                // logs a Unity console ERROR ("Do not use ReadObjectThreaded on
                // scene objects!") at checkpoint creation AND validation, twice
                // per gated call. The error lands in the console as a real red
                // entry — the exact signal an agent uses to decide it broke
                // something — so a clean gated op leaves two red herrings. The
                // scene's fileIDs are authoritative via DeclaredFileIDs (parsed
                // from YAML anchors, glm #10), which is OR'd into ExistsInAssets
                // resolution; LoadAllAssetsAtPath adds nothing but noise for a
                // scene. Only the SceneAsset's own main-asset fileID is safe to
                // load here.
                if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath);
                    if (sceneAsset != null
                        && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sceneAsset, out _, out long sceneFileId))
                    {
                        scopedFileIDs.Add(sceneFileId);
                    }
                    continue;
                }

                var assetObject = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (assetObject == null) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out _, out long fileId))
                    scopedFileIDs.Add(fileId);

                // feedback-fable-31-07 §6 — also index every SUB-asset fileID of
                // each scanned asset. A prefab's root GameObject (fileID
                // 100100000), embedded Materials, and stripped/instance sub-
                // objects are all sub-assets whose fileID differs from the main
                // asset's. Without this, a local-reference pointing at any of
                // them read as missing (the core missing_local_fileid false
                // positive on prefab-instance constructs). The same
                // LoadAllAssetsAtPath enumeration backs the external-ref path
                // via GetFileIdsForPath; reusing it here makes local refs and
                // external refs resolve against the same fileID universe.
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (subAssets != null)
                {
                    foreach (var sub in subAssets)
                    {
                        if (sub == null || sub == assetObject) continue;
                        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sub, out _, out long subFileId))
                            scopedFileIDs.Add(subFileId);
                    }
                }
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
                // Explicit `long` — the discard makes the call ambiguous on
                // 2022.3, where the deprecated (out int) overload still exists.
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assetObject, out var guid, out long _))
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

                // feedback-04-08-opus §4 / §5 — resolve each empty-ref site's
                // transform path (content-addressed, survives renumbering) and
                // script owner (user script vs built-in) from a per-asset anchor
                // metadata map. The anchor-keyed scheme still churned 729/729 on
                // an idempotent prefab rebuild because a rebuild renumbers every
                // anchor; the transform path + property is stable. The owner
                // classification lets real user-script empties stay Warning while
                // built-in empty-by-default fields are demoted to Info.
                EnrichEmptyRefs(lines, refsData);

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
            // feedback-fable-31-07 §6 — track nesting inside PrefabInstance
            // modification / source-prefab blocks. The `target:`/`value:`/
            // `correspondingSourceObject:` references inside these blocks point
            // at the SOURCE prefab's internal/stripped fileIDs, which are valid
            // by construction (Unity resolves them at load time) but are not
            // main-asset fileIDs the scanner indexes — producing systematic
            // missing_fileid:<guid>:<id> false positives on every nested prefab
            // instance. Block depth is measured by YAML indentation: a block
            // opens at "m_Modification:" / "m_SourcePrefab:" and closes when a
            // line dedents back to the opener's level (or a new top-level
            // object begins). `modBlockIndent` = -1 means "not in a block".
            int modBlockIndent = -1;
            int modBlockOpenIndent = -1;
            // The fileID of the most recent top-level YAML object header
            // (`--- !u!T &NNN`). Used to key empty_local_ref issues by owning
            // anchor so deltas are stable (feedback-fable-31-07 §7).
            long currentAnchor = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (YamlUtilities.IsSystemReference(line, YamlUtilities.KeyWordsToIgnore)) continue;
                if (isScene && YamlUtilities.IsSystemReference(line, YamlUtilities.KeyWordsToIgnoreInSceneAsset)) continue;

                // Track modification-block entry/exit. A new top-level YAML
                // object ("--- !u!...") always closes any open block and
                // updates the current owning anchor.
                if (line.StartsWith("---", StringComparison.Ordinal))
                {
                    modBlockIndent = -1;
                    modBlockOpenIndent = -1;
                    var anchorMatch = SharedRegex.ObjectHeaderAnchor.Match(line);
                    if (anchorMatch.Success && long.TryParse(anchorMatch.Groups[1].Value, out var anchor))
                    {
                        currentAnchor = anchor;
                        // feedback-01-08-glm §10 — record the declared anchor so
                        // the fileID "exists in the file" regardless of whether
                        // AssetDatabase surfaces it. The regex captures -?\d+, so
                        // negative (stripped) ids are declared too. This is the
                        // authoritative source for ExistsInAssets resolution.
                        refsData.DeclaredFileIDs.Add(anchor);
                    }
                }
                else if (modBlockIndent >= 0)
                {
                    // Close the block when indentation returns to or above the
                    // opener's level (blank lines and the opener itself excluded).
                    var ind = LeadingIndent(line);
                    if (ind >= 0 && ind <= modBlockOpenIndent && !string.IsNullOrWhiteSpace(line))
                    {
                        modBlockIndent = -1;
                        modBlockOpenIndent = -1;
                    }
                }

                var isPrefabModRef = modBlockIndent >= 0;

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

                        // feedback-fable-31-07 §6 (a) — the prefab-root fileID
                        // 100100000 is Unity's conventional root-GameObject id
                        // inside prefabs. It is rarely returned by
                        // LoadAllAssetsAtPath, so an external ref to it read as
                        // missing_fileid:<guid>:100100000 on every nested
                        // prefab instance. It always resolves in the editor
                        // WHEN the target prefab exists, so only the fileID
                        // legs are whitelisted (ResolveReferences forces them
                        // to "exists").
                        //
                        // feedback-fable-31-07 §6 (b) — same for external refs
                        // inside a PrefabInstance m_Modification /
                        // m_SourcePrefab block. These target:/value:/source:
                        // refs point at the source prefab's stripped objects;
                        // their fileIDs are valid by construction (Unity
                        // resolves them at load) and are not main-asset ids,
                        // so emitting fileID issues for them only ever produced
                        // false positives.
                        //
                        // Both whitelists suppress ONLY the fileID legs. The
                        // reference itself stays in the scan so the GUID leg is
                        // still validated below — the original whole-reference
                        // `continue` dropped `{fileID: 100100000, guid: <g>}`
                        // and modification-block refs entirely, so a DELETED
                        // source prefab produced zero missing_guid issues where
                        // v0.7.0 reported an Error.
                        var suppressFileIdChecks = isPrefabModRef
                            || (guidValid && localIdValid && localFileID == PrefabFileIds.RootGameObject);

                        var referenceData = new ExternalReferenceRegistry(localIdValid, guidValid, localFileID, externalGuid, i)
                        {
                            SuppressFileIDChecks = suppressFileIdChecks
                        };

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
                    // Same modification-block skip for local refs (stripped
                    // object ids referenced only inside the block).
                    if (!isPrefabModRef)
                    {
                        var localMatches = regexFileID.Matches(line);
                        foreach (Match match in localMatches)
                        {
                            var idStr = match.Value;
                            var digitsOnly = idStr.Replace("{fileID: ", "").Replace("}", "").Trim();

                            if (digitsOnly == "0")
                            {
                                // Capture the owning anchor (already tracked)
                                // and the property key on this line so the
                                // mapper can key empty_local_ref by
                                // anchor+property instead of an unstable ordinal
                                // (feedback-fable-31-07 §7).
                                var propMatch = SharedRegex.PropertyKeyBeforeFileId.Match(line);
                                var prop = propMatch.Success ? propMatch.Groups[1].Value : "";
                                refsData.EmptyFileIDs.Add(new EmptyLocalFileIDRegistry(i, currentAnchor, prop));
                            }
                            else if (long.TryParse(digitsOnly, out var localId))
                            {
                                refsData.LocalReferences.Add(new LocalReferenceRegistry(localId, i));
                            }
                        }
                    }
                }

                // Open a modification block on the opener line. The opener
                // itself (e.g. "  m_Modification:") has no fileID ref, so it was
                // already passed by the branches above.
                if (modBlockIndent < 0 && IsPrefabModificationOpener(line, out var openerIndent))
                {
                    modBlockIndent = openerIndent;
                    modBlockOpenIndent = openerIndent;
                }
            }
        }

        // Count leading spaces (tabs not expected in Unity YAML; treat any tab
        // as one space for the purposes of block-nesting comparison). Returns
        // -1 for blank/whitespace-only lines so they never close a block.
        private static int LeadingIndent(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return -1;
            var n = 0;
            foreach (var c in line)
            {
                if (c == ' ') n++;
                else if (c == '\t') n++;
                else break;
            }
            return n;
        }

        // True when the line opens a PrefabInstance m_Modification block or a
        // m_SourcePrefab block — the two YAML structures whose contents
        // reference the source prefab's stripped/internal fileIDs. `indent` is
        // set to the opener's leading-space count for block-exit comparison.
        private static bool IsPrefabModificationOpener(string line, out int indent)
        {
            indent = LeadingIndent(line);
            var trimmed = line.AsSpan().TrimStart();
            return trimmed.SequenceEqual("m_Modification:".AsSpan())
                || trimmed.SequenceEqual("m_SourcePrefab:".AsSpan());
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
                    registry.ExistsInAssets = scopedFileIDs.Contains(registry.Id)
                        || asset.RefsData.DeclaredFileIDs.Contains(registry.Id);

                foreach (var registry in asset.RefsData.ExternalReferences)
                {
                    // feedback-fable-31-07 §6 — whitelisted fileID legs
                    // (prefab-root 100100000 / modification-block refs): force
                    // both fileID resolutions to "exists" so no missing_fileid*
                    // classification fires. The GUID leg was already resolved
                    // during parse and still drives missing_guid.
                    if (registry.SuppressFileIDChecks)
                    {
                        registry.FileIDExistsInAssets = true;
                        registry.FileIDExistsInTargetAsset = true;
                        continue;
                    }

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

            // feedback-04-08-opus §1 — same scene-path guard as the scoped
            // loop above. An external PPtr can target a scene (a prefab or
            // ScriptableObject holding a SceneAsset reference); LoadAllAssetsAtPath
            // on that .unity path logs the "Do not use ReadObjectThreaded on
            // scene objects!" error. A scene is not an asset container here, so
            // return an empty set (the caller treats null-target as "fileID
            // exists" already; an empty set makes a non-existent fileID surface
            // cleanly instead of erroring).
            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                cache[assetPath] = null;
                return null;
            }

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

        // feedback-04-08-opus §4 / §5 — resolve transform path + script owner
        // for every empty-ref site from a per-asset anchor metadata map, so the
        // mapper can key on path+property (content-addressed, survives rebuild
        // renumbering) and split severity by field owner (user script vs
        // built-in). No-op when there are no empty refs.
        private static void EnrichEmptyRefs(string[] lines, AssetReferencesData refsData)
        {
            if (refsData.EmptyFileIDs.Count == 0) return;

            var map = EmptyRefMetadata.Build(lines, refsData.DeclaredFileIDs);
            if (map.Count == 0) return;

            foreach (var empty in refsData.EmptyFileIDs)
            {
                if (empty.Anchor == 0) continue;
                if (!map.TryGetValue(empty.Anchor, out var meta)) continue;

                empty.TransformPath = EmptyRefMetadata.ResolveTransformPath(empty.Anchor, map);
                empty.OwnerScriptGuid = meta.ScriptGuid;
            }
        }
    }
}
