using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityOpenMcpVerify.Internals.RegexPatterns;
using UnityOpenMcpVerify.Internals.Serialization;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpVerify.Fixes
{
    public class RelinkBrokenGuidFix : IFixProvider
    {
        public const string TargetGuidHintKey = "target_guid";

        public string FixId => "relink_broken_guid";

        public bool CanFix(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out var ruleId, out _, out _, out var issueCode))
                return false;

            // Accept both the bare code ("missing_guid") and the GUID-encoded
            // form ("missing_guid:<guid>") — the bare form is used by synthetic
            // keys (FixProviderRegistry.TryGetFixInfo / CandidatesForIssue),
            // while real scan-result issue keys carry the GUID suffix so the
            // fix provider rewrites exactly the right reference.
            var bareCode = IssueKey.BareIssueCode(issueCode);
            return (ruleId == "missing_references" && bareCode == "missing_guid")
                || (ruleId == "dependencies" && bareCode == "broken_dependency");
        }

        public FixDescription Describe(string issueId)
        {
            IssueKey.TryParse(issueId, out var ruleId, out _, out var assetPath, out var issueCode);
            var brokenGuid = ExtractBrokenGuidFromIssue(issueId);

            var candidates = brokenGuid != null
                ? FindCandidateAssets(brokenGuid, assetPath)
                : new List<GuidCandidate>(0);

            var desc = candidates.Count > 0
                ? $"Relink broken GUID reference in '{assetPath}'. "
                  + $"{candidates.Count} candidate target(s) found by name/type. "
                  + "Provide one via apply_fix with target_guid (the chosen replacement GUID) to apply."
                : $"Relink broken GUID reference in '{assetPath}'. No automatic candidates found — "
                  + "use unity_open_mcp_find_references to identify the intended target before applying.";

            return new FixDescription
            {
                FixId = FixId,
                IssueId = issueId,
                AssetPath = assetPath,
                Description = desc,
                // Mutates references and a wrong choice silently rewires the
                // asset graph — never auto-apply under enforce.
                Safe = false,
            };
        }

        // Safe is a static verdict (a wrong relink always rewires the asset
        // graph) — return it directly so CandidatesForIssue / TryGetFixInfo do
        // not have to run Describe()'s FindCandidateAssets sweep just to learn
        // the safety flag.
        public bool IsSafe(string issueId) => false;

        public FixResult Apply(string issueId)
        {
            // Apply without a chosen target is a no-op for this provider —
            // relinking requires picking one of the candidates. Callers go
            // through ApplyFixTool, which passes the chosen target_guid in.
            return Apply(issueId, targetGuid: null);
        }

        public FixResult Apply(string issueId, string targetGuid)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out _))
                return new FixResult
                {
                    Success = false,
                    Description = $"Cannot parse issue id: {issueId}",
                    TouchedPaths = null
                };

            if (string.IsNullOrEmpty(assetPath))
                return new FixResult
                {
                    Success = false,
                    Description = "Issue id contains empty asset path.",
                    TouchedPaths = null
                };

            // Argument validation first — a missing or malformed target_guid must
            // be reported before we touch the asset, so callers learn what to fix
            // without needing a resolvable asset on disk.
            if (string.IsNullOrEmpty(targetGuid))
            {
                var brokenForCandidates = ExtractBrokenGuidFromIssue(issueId);
                var candidates = !string.IsNullOrEmpty(brokenForCandidates)
                    ? FindCandidateAssets(brokenForCandidates, assetPath)
                    : new List<GuidCandidate>(0);
                return new FixResult
                {
                    Success = false,
                    Description = candidates.Count > 0
                        ? $"relink_broken_guid requires a chosen target_guid. Candidates: {FormatCandidates(candidates)}"
                        : "relink_broken_guid requires a chosen target_guid. No automatic candidates found — use unity_open_mcp_find_references to identify the intended target.",
                    TouchedPaths = null
                };
            }

            if (!SharedRegex.Guid32Hex.IsMatch(targetGuid))
                return new FixResult
                {
                    Success = false,
                    Description = $"target_guid '{targetGuid}' is not a valid 32-hex Unity GUID.",
                    TouchedPaths = null
                };

            var brokenGuid = ExtractBrokenGuidFromIssue(issueId);
            if (string.IsNullOrEmpty(brokenGuid))
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not determine the broken GUID to replace from issue id '{issueId}'.",
                    TouchedPaths = null
                };

            // Sanity: the chosen target must resolve to a loadable asset.
            var targetPath = AssetDatabase.GUIDToAssetPath(targetGuid);
            if (string.IsNullOrEmpty(targetPath))
                return new FixResult
                {
                    Success = false,
                    Description = $"target_guid '{targetGuid}' does not resolve to any asset in the project.",
                    TouchedPaths = null
                };

            // V5 / B-N21: refuse to edit a PREFAB the Editor currently has
            // open in a Prefab Stage, but EDIT-AND-RELOAD an open scene. The
            // previous guard hard-refused any open asset, so the documented
            // scan→apply_fix loop dead-ended on the most common interactive
            // case — the user almost always has the referencing scene open.
            // For an open scene we now rewrite the file on disk and reload the
            // open scene from disk (the `wasOpen` model RemoveMissingScriptFix
            // uses) so the in-memory copy picks up the relink instead of
            // reverting it on the next save. Prefab stages still refuse: a
            // text rewrite cannot safely update a prefab stage's in-memory
            // instance, so the user must close the prefab first.
            var openPrefab = CheckPrefabStageOpen(assetPath);
            if (openPrefab != null)
            {
                return new FixResult
                {
                    Success = false,
                    Description = openPrefab,
                    TouchedPaths = null
                };
            }

            bool sceneOpen = IsSceneOpen(assetPath);
            var rewrite = RewriteGuid(assetPath, brokenGuid, targetGuid, targetPath);
            if (!rewrite.Success || !sceneOpen) return rewrite;

            // B-N21 — the referencing scene is open in-memory. Reload it from
            // disk so the rewrite we just wrote wins instead of being silently
            // reverted when the user next saves. `OpenScene` with
            // `OpenSceneMode.Additive` would load a second copy; we need to
            // reload the EXISTING loaded scene, so look it up by path and
            // re-open that specific scene via the editor API. This mirrors
            // RemoveMissingScriptFix's stance that a fix touching an open
            // scene saves through Unity rather than expecting the in-memory
            // copy to be discarded.
            ReloadOpenScene(assetPath);
            return rewrite;
        }

        // B-N21 — true when the referencing scene at assetPath is loaded
        // (active or additively). Compared OrdinalIgnoreCase so a path that
        // differs only in case (macOS/Windows default to case-insensitive
        // filesystems) is still recognized as open.
        private static bool IsSceneOpen(string assetPath)
        {
            if (Path.GetExtension(assetPath ?? "").ToLowerInvariant() != ".unity") return false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                if (string.Equals(SceneManager.GetSceneAt(i).path, assetPath,
                    System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // B-N21 — reload an already-loaded scene from disk so an on-disk edit
        // (the GUID relink we just wrote) is reflected in the in-memory copy
        // instead of being reverted on the next save. We close the open scene
        // and reopen it additively: `OpenScene(path, Additive)` on an already-
        // loaded path is version-dependent (some Unity versions load a second
        // copy rather than refreshing in place), so the close-then-reopen pair
        // is the reliable way to make Unity re-read the file. Best-effort: if
        // Unity refuses (unsaved changes the user keeps, or the API throws),
        // the on-disk relink still landed and will win the next time the scene
        // is opened fresh — a reload failure is NOT a relink failure.
        private static void ReloadOpenScene(string assetPath)
        {
            try
            {
                var scene = SceneManager.GetSceneByPath(assetPath);
                if (!scene.IsValid() || !scene.isLoaded) return;
                // Close-then-reopen forces Unity to re-read the rewritten file.
                // `CloseScene(removeScene: true)` drops the in-memory copy;
                // `OpenScene(Additive)` re-loads it from disk. Unity prompts
                // before discarding unsaved changes in the closed scene,
                // matching the editor's normal reload UX.
                EditorSceneManager.CloseScene(scene, true);
                EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            }
            catch
            {
                // Best-effort: the on-disk rewrite is the source of truth.
            }
        }

        // V5 / B-N21: detect a prefab the Editor currently has open in a
        // Prefab Stage. Returns a non-null message describing why we refused,
        // or null if it is safe to edit the file on disk. (Open SCENES are no
        // longer refused — see IsSceneOpen / ReloadOpenScene for the
        // edit-and-reload path that handles them.)
        //
        // A16 — inspect the WHOLE stage stack, not just the focused stage.
        // PrefabStageUtility.GetCurrentPrefabStage() returns only the top of
        // the stack, but Unity keeps a stack of open stages (open Prefab A,
        // double-click nested Prefab B → two stages). With B focused, a relink
        // targeting A's `.prefab` would pass the focused-only guard,
        // File.WriteAllText would land, and A's in-memory stage copy would
        // revert it on the next save. The StageUtility.GetStages() API
        // (UnityEditor.SceneManagement, since 2020.1) returns every open stage
        // including the main scene stage; filter to prefab stages and compare
        // each one's asset path. Reflection keeps us compile-safe across Unity
        // versions where the API moved between namespaces.
        private static string CheckPrefabStageOpen(string assetPath)
        {
            try
            {
                var stageType = System.Type.GetType(
                    "UnityEditor.SceneManagement.PrefabStage, UnityEditor");
                var stageUtilType = System.Type.GetType(
                    "UnityEditor.SceneManagement.StageUtility, UnityEditor");
                if (stageType != null && stageUtilType != null)
                {
                    var getStages = stageUtilType.GetMethod(
                        "GetStages",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (getStages != null)
                    {
                        var stages = getStages.Invoke(null, null) as System.Collections.IEnumerable;
                        if (stages != null)
                        {
                            var prefabPathProp = stageType.GetProperty("prefabAssetPath");
                            // 2022+ exposes `assetPath` (prefabAssetPath is the
                            // older 2021/2019 name); fall back to either.
                            var assetPathProp = stageType.GetProperty("assetPath");
                            foreach (var stage in stages)
                            {
                                if (stage == null) continue;
                                // GetStages returns the main scene stage too
                                // (a SceneStage, not a PrefabStage). Invoking
                                // a PrefabStage-declared property on a non-
                                // PrefabStage instance throws TargetException,
                                // so skip any stage that is not assignable to
                                // PrefabStage. Only prefab stages carry an
                                // asset path; the main scene stage returns
                                // null/empty for it.
                                if (!stageType.IsInstanceOfType(stage)) continue;
                                var stagePath = prefabPathProp?.GetValue(stage) as string
                                                ?? assetPathProp?.GetValue(stage) as string;
                                if (!string.IsNullOrEmpty(stagePath)
                                    && string.Equals(stagePath, assetPath,
                                        System.StringComparison.OrdinalIgnoreCase))
                                {
                                    return $"Prefab '{assetPath}' is open in the Prefab Stage. " +
                                           "Apply the fix with the prefab closed so the in-memory stage copy " +
                                           "does not silently revert the relink on save.";
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Reflection against the prefab-stage API must never break
                // Apply; if we cannot determine the stage state, fall through
                // to the normal path (no false refusal).
            }

            return null;
        }

        // -------------------------------------------------------------------
        // Broken-GUID extraction
        // -------------------------------------------------------------------
        //
        // The issue id carries {ruleId|severity|assetPath|issueCode}. The
        // issueCode may encode the specific broken GUID as a suffix
        // ("missing_guid:<guid>" / "broken_dependency:<guid>") so the fix
        // rewrites exactly the reference the issue describes — critical when
        // an asset has multiple broken GUID references. When the suffix is
        // absent (e.g. a hand-transcribed key or a synthetic test key), we
        // fall back to scanning the asset for the first unresolved GUID.

        private static string ExtractBrokenGuidFromIssue(string issueId)
        {
            if (!IssueKey.TryParse(issueId, out _, out _, out var assetPath, out var issueCode))
                return null;

            // Preferred path: the GUID is encoded in the issueCode suffix.
            var guidFromCode = IssueKey.IssueCodeGuid(issueCode);
            if (!string.IsNullOrEmpty(guidFromCode))
                return guidFromCode;

            // Fallback: scan the asset for the first unresolved GUID. This is
            // the legacy path for keys without a suffix (backward compat).
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                return null;

            var lines = YamlUtilities.TryReadAllLines(assetPath);
            foreach (var line in lines)
            {
                var match = SharedRegex.ExternalFileAndGuid.Match(line);
                if (!match.Success) continue;
                var guid = match.Groups[2].Value;
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    continue; // resolves fine — not the broken one
                return guid;
            }

            return null;
        }

        // -------------------------------------------------------------------
        // Candidate discovery — name + type heuristics
        // -------------------------------------------------------------------

        private static List<GuidCandidate> FindCandidateAssets(string brokenGuid, string referencingAssetPath)
        {
            // Heuristic 1: the broken GUID may have been re-imported under a new
            // GUID but the same asset name. We cannot read the name from a GUID
            // that does not resolve, so the strongest signal we have is the
            // field name on the referencing line — but we don't carry it in the
            // issue id. Fall back to type inference from the referencing asset
            // extension (e.g. a Material referencing a missing texture).
            //
            // Practical candidate sources:
            //   * assets whose name matches a token derived from the broken GUID
            //     (last 8 hex chars are often unique enough to spot a typo).
            //   * recently added assets of plausible types.

            var candidates = new List<GuidCandidate>();
            var token = brokenGuid.Length >= 8 ? brokenGuid.Substring(brokenGuid.Length - 8) : brokenGuid;

            // Search asset paths for the token — catches copy/paste / truncation
            // mistakes where only part of the GUID was changed.
            var hits = AssetDatabase.FindAssets(token);
            var seen = new HashSet<string>();
            foreach (var hitGuid in hits)
            {
                if (hitGuid == brokenGuid) continue;
                var path = AssetDatabase.GUIDToAssetPath(hitGuid);
                if (string.IsNullOrEmpty(path) || path.StartsWith("Packages/")) continue;
                if (!seen.Add(path)) continue;
                candidates.Add(new GuidCandidate { Guid = hitGuid, AssetPath = path });
                if (candidates.Count >= 8) break;
            }

            return candidates;
        }

        private static string FormatCandidates(List<GuidCandidate> candidates)
        {
            var parts = candidates
                .Take(8)
                .Select(c => $"{c.Guid} ({c.AssetPath})");
            return string.Join(", ", parts);
        }

        // -------------------------------------------------------------------
        // Apply — rewrite the broken GUID in the asset YAML and re-import
        // -------------------------------------------------------------------

        private static FixResult RewriteGuid(string assetPath, string brokenGuid, string targetGuid, string targetPath)
        {
            if (!File.Exists(assetPath))
                return new FixResult
                {
                    Success = false,
                    Description = $"Asset file not found at '{assetPath}'.",
                    TouchedPaths = null
                };

            string contents;
            try
            {
                contents = File.ReadAllText(assetPath);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not read '{assetPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            // V4: rewrite the WHOLE PPtr triple (fileID + guid + type), not
            // just the GUID token. Unity's PPtr form for an external reference
            // is `{fileID: <local-id>, guid: <guid>, type: <type>}`. Swapping
            // only `guid:` leaves the OLD fileID pointing at the OLD asset's
            // local identifier — which is meaningless for the new target. The
            // next scan then reports `missing_fileid` instead of
            // `missing_guid`, and apply_fix reported Success while the
            // reference is still dangling. The scanner's
            // SharedRegex.ExternalFileAndGuid validates both legs, so a
            // half-rewritten triple re-surfaces as a new issue.
            //
            // A15: resolve the target asset's full set of valid local
            // fileIDs (main object + every sub-asset, via
            // `LoadAllAssetsAtPath` — the same approach the scanner's
            // `GetFileIdsForPath` uses). For each matched triple:
            //   - If the EXISTING fileID is already valid for the target
            //     (e.g. the broken GUID pointed at a specific sub-object of
            //     an .fbx and the target .fbx exposes a fileID with the same
            //     number), KEEP it — rewriting it to the main object's id
            //     would silently re-point the reference at the root.
            //   - Otherwise rewrite fileID to the target's MAIN object id
            //     (the previous behaviour, correct for a reference whose old
            //     fileID does not exist in the target at all).
            // The `type:` field is preserved as-is: it encodes the target's
            // storage kind (0/2/3), and there is no reliable Unity API to
            // compute the correct digit from the target asset. The scanner
            // validates fileID+guid but not type, so an invalid type is
            // re-flagged by the next scan rather than silently "fixed" away.
            // (The earlier comment's claim that "type:3 is the only value
            // Unity emits for an external reference" was false — this repo's
            // own demo/Assets carries type:0, type:2 and type:3 external
            // PPtrs — so the type is no longer assumed.)
            var validFileIds = ResolveValidFileIds(targetPath, out long? mainFileId);
            long? targetFileId = mainFileId;

            // Match every `guid: <brokenGuid>` occurrence on the asset — a single
            // broken GUID is typically referenced once, but the same target may
            // be wired into several PPtr fields. In .prefab/.unity/.mat YAML these
            // references live INLINE in flow-style PPtr maps
            // (`m_Mesh: {fileID: 10303, guid: <guid>, type: 2}`), so a line-start
            // anchor (`^\s*guid:`) would never match them. Instead we anchor on
            // the key boundary: `guid:` must NOT be preceded by a word character,
            // which excludes incidental substrings like `m_guid:` or
            // `second_guid:` (the preceding `_` is a `\w`) while still matching
            // both line-start (`guid:` at column 0 in a .meta) and inline
            // (`... guid: <guid> ...`) occurrences. This mirrors the key-scope of
            // the scanner's SharedRegex.ExternalFileAndGuid, not the .meta-only
            // FixDuplicateGuidFix pattern.
            //
            // When we can resolve the target's main fileID we match the entire
            // PPtr flow-style triple (`{fileID: n, guid: <brokenGuid>, type: m}`)
            // and replace it whole. The `(?<![\w])` negative-lookbehind guard is
            // preserved when matching just `guid:` on its own (the fallback for
            // .meta / non-PPtr contexts where we cannot resolve a fileID).
            // B-N12 — a single broken GUID can appear in MORE than one syntactic
            // shape on the same asset:
            //   1. A flow-style PPtr triple `{fileID: n, guid: <g>, type: m}`
            //      (the common inline reference in .prefab/.unity/.mat YAML).
            //   2. A non-triple occurrence the triple regex does not cover:
            //      a negative/omitted local id, a `guid:` key on its own line
            //      (a .meta Guid field, an Addressables-style `m_AssetGUID:`),
            //      or any other shape the scanner's ExternalFileAndGuid flags.
            // The previous code, when at least one TRIPLE matched, replaced only
            // triples and never fell back, so the non-triple occurrences were
            // left dangling while Success=true was reported — the next scan re-
            // flagged the same asset and a scan→apply_fix agent looped. The
            // rewrite now ALWAYS runs the bare-guid pass over the triple-rewritten
            // text (or the original when no triple matched / fileID was
            // unresolvable), so every `guid: <brokenGuid>` occurrence is updated.
            var triplePattern = new Regex(
                @"\{fileID:\s*(\d+),\s*guid:\s*" + Regex.Escape(brokenGuid) + @"\s*,\s*type:\s*(\d+)\s*\}",
                RegexOptions.Compiled);
            // Bare-guid key pattern — matches any `guid: <brokenGuid>` NOT
            // preceded by a word char (excludes `m_guid:` / `second_guid:`
            // substrings while matching both line-start and inline occurrences).
            // Used for the .meta / standalone-key path AND for the residual
            // non-triple occurrences after a triple rewrite.
            var guidPattern = new Regex(
                @"(?<![\w])guid:\s*" + Regex.Escape(brokenGuid) + @"\b",
                RegexOptions.Compiled);

            int tripleReplaced = 0;
            string working = contents;
            if (targetFileId.HasValue)
            {
                tripleReplaced = triplePattern.Matches(working).Count;
                if (tripleReplaced > 0)
                {
                    working = triplePattern.Replace(
                        working,
                        m =>
                        {
                            // A15: preserve the existing fileID when it is
                            // already valid for the target (a sub-object
                            // reference that survives the relink). Only fall
                            // back to the target's main object id when the old
                            // fileID does not exist in the target at all.
                            long existingId;
                            long.TryParse(m.Groups[1].Value, out existingId);
                            long fileId = validFileIds != null && validFileIds.Contains(existingId)
                                ? existingId
                                : targetFileId.Value;
                            return "{fileID: " + fileId + ", guid: " + targetGuid + ", type: " + m.Groups[2].Value + "}";
                        });
                }
            }

            // B-N12 — always run the bare-guid pass on the (possibly triple-
            // rewritten) text. A triple already rewrote its inline `guid:`
            // token to the target, so guidPattern naturally re-matches zero of
            // those; it ONLY catches the residual non-triple occurrences the
            // triple pass left in place. Counting is done AFTER the triple
            // rewrite so already-rewritten triples are not double-counted.
            int guidReplaced = guidPattern.Matches(working).Count;
            int replaced = tripleReplaced + guidReplaced;
            if (replaced == 0)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Broken GUID '{brokenGuid}' not found in '{assetPath}'. The issue may have already been resolved.",
                    TouchedPaths = null
                };
            }

            string newContents = guidReplaced > 0
                ? guidPattern.Replace(working, $"guid: {targetGuid}")
                : working;

            try
            {
                File.WriteAllText(assetPath, newContents);
            }
            catch (System.Exception e)
            {
                return new FixResult
                {
                    Success = false,
                    Description = $"Could not write '{assetPath}': {e.Message}",
                    TouchedPaths = null
                };
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            // A15/B-N12: the description reports how many occurrences were
            // rewritten and in which pass, so an agent can tell a triple-only
            // relink (fileID-aware) from one that also touched bare-guid keys
            // (.meta / Addressables / standalone guid: lines). When the target's
            // fileID set resolved, valid existing fileIDs (sub-object references)
            // are preserved and only invalid ones are rewritten to the main id.
            var fileIdNote = targetFileId.HasValue && tripleReplaced > 0
                ? $" (fileID rewritten to main object where the existing id was invalid for the target; valid sub-object ids preserved)"
                : (targetFileId.HasValue
                    ? " (no PPtr triples matched — only bare guid: keys were rewritten)"
                    : " (fileID not resolvable — only the guid: token was rewritten)");
            var countNote = replaced == 1
                ? " (1 occurrence"
                : $" ({replaced} occurrences";
            countNote += tripleReplaced > 0 && guidReplaced > 0
                ? $": {tripleReplaced} triple + {guidReplaced} bare-guid)"
                : (tripleReplaced > 0 ? " triple)" : " bare-guid)");

            return new FixResult
            {
                Success = true,
                Description = $"Relinked broken GUID '{brokenGuid}' -> '{targetGuid}' in '{assetPath}'{countNote}{fileIdNote}.",
                TouchedPaths = new[] { assetPath }
            };
        }

        // Resolve every local file identifier the target asset exposes — the
        // main object's id PLUS every sub-asset's id (an .fbx model carries
        // one fileID per imported mesh/material/animation). This mirrors the
        // scanner's `MissingReferences.Scanner.GetFileIdsForPath`, which uses
        // `LoadAllAssetsAtPath` for the same reason: a reference that points
        // at a specific sub-object is valid iff its fileID appears in this set.
        // `mainFileId` (out) is the main object's id, used as the rewrite
        // fallback when an existing fileID is NOT in the set.
        //
        // A15: the previous `ResolveMainLocalFileId` returned only the main
        // object's id, so N triples that referenced N distinct sub-objects of
        // one target all collapsed onto the main id — silently re-pointing
        // every sub-object reference at the root.
        //
        // Returns null (and mainFileId null) when the asset cannot be loaded;
        // Apply then falls back to a bare-guid rewrite.
        private static HashSet<long> ResolveValidFileIds(string assetPath, out long? mainFileId)
        {
            mainFileId = null;
            if (string.IsNullOrEmpty(assetPath)) return null;
            try
            {
                var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (mainAsset == null) return null;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mainAsset, out _, out long mainId))
                    mainFileId = mainId;

                // LoadAllAssetsAtPath returns the main object plus every
                // imported sub-asset; collect each one's local fileID.
                var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (allAssets == null || allAssets.Length == 0)
                {
                    return mainFileId.HasValue ? new HashSet<long> { mainFileId.Value } : null;
                }
                var fileIds = new HashSet<long>();
                foreach (var subAsset in allAssets)
                {
                    if (subAsset == null) continue;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(subAsset, out _, out long subFileId))
                        fileIds.Add(subFileId);
                }
                // Defensive: ensure the main id is present even if
                // LoadAllAssetsAtPath did not surface it as the first entry
                // (it always does in practice, but the contract does not
                // guarantee ordering).
                if (mainFileId.HasValue) fileIds.Add(mainFileId.Value);
                return fileIds.Count > 0 ? fileIds : null;
            }
            catch
            {
                return null;
            }
        }

        struct GuidCandidate
        {
            public string Guid;
            public string AssetPath;
        }
    }
}
