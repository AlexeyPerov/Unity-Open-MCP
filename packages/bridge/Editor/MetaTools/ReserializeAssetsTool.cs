using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace UnityOpenMcpBridge.MetaTools
{
    // M9 Plan 1 — reserialize round-trip. Wraps AssetDatabase.ForceReserializeAssets
    // so an agent can text-edit a .prefab/.unity/.asset/.mat/.controller/.anim and
    // normalize through Unity's own serializer to catch missing fields, wrong
    // indents, and stale fileIDs. Counts as a mutation — runs the full gate path
    // (checkpoint -> reserialize -> validate -> delta).
    //
    // Scope: explicit `paths` array only. Whole-project reserialize is intentionally
    // not exposed because the gate needs scoped paths_hint to validate the delta;
    // enumerating affected assets is the safe failure mode.
    public static class ReserializeAssetsTool
    {
        public static readonly string[] SupportedExtensions =
        {
            ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim"
        };

        public static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            if (paths == null || paths.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'paths' is required and must be a non-empty array of asset paths to reserialize. " +
                    "Whole-project reserialize is not supported via this tool — enumerate the assets you edited.");

            var normalized = NormalizePaths(paths, out var containmentInvalid);
            var invalid = containmentInvalid;
            CollectInvalidInto(normalized, invalid);
            if (invalid.Count > 0)
                return ToolDispatchResult.Fail("invalid_paths",
                    "One or more paths failed pre-flight checks: " + string.Join("; ", invalid));

            // Default: assets-only round-trip (ReserializeAssets) so a direct YAML
            // edit on the asset body does not churn the companion .meta with empty
            // importer-field whitespace (userData:/assetBundleName:). Callers that
            // need to round-trip importer metadata (upgrade workflows) opt in via
            // include_meta: true -> ReserializeAssetsAndMetadata.
            var includeMeta = JsonBody.GetBool(body, "include_meta", false);
            var options = ResolveOptions(includeMeta);

            try
            {
                AssetDatabase.ForceReserializeAssets(normalized, options);
                AssetDatabase.Refresh();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("reserialize_error",
                    $"AssetDatabase.ForceReserializeAssets threw: {e.Message}");
            }

            // feedback-01-08-glm §9 — when a reserialized path is a .unity that
            // is currently OPEN in the Hierarchy, the on-disk YAML was just
            // rewritten but the editor's in-memory hierarchy was NOT touched, so
            // memory and disk now disagree. Mark those opened scenes dirty so
            // the editor's dirty flag reflects the disk rewrite (and the agent's
            // next editor_status / dirtyScenes read tells it a reload is needed),
            // and report which opened scenes were touched.
            var touchedOpenScenes = MarkOpenedScenesForReserializedUnity(normalized);

            return ToolDispatchResult.Ok(BuildResult(normalized, includeMeta, touchedOpenScenes));
        }

        // For each reserialized .unity path that is currently open in the
        // Hierarchy, mark the matching opened scene dirty (so the editor's
        // memory-vs-disk flag reflects the disk rewrite). Returns the list of
        // opened scene paths that were touched, for the response.
        private static System.Collections.Generic.List<string> MarkOpenedScenesForReserializedUnity(
            System.Collections.Generic.List<string> reserializedPaths)
        {
            var touched = new System.Collections.Generic.List<string>();
            bool anyUnity = false;
            foreach (var p in reserializedPaths)
            {
                if (p.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase))
                {
                    anyUnity = true;
                    break;
                }
            }
            if (!anyUnity) return touched;

            var scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < scenes; i++)
            {
                var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (var p in reserializedPaths)
                {
                    if (!p.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (ScenePathMatches(s.path, p))
                    {
                        EditorSceneManager.MarkSceneDirty(s);
                        touched.Add(s.path);
                        break;
                    }
                }
            }
            return touched;
        }

        // Compare a scene's runtime path (e.g. "Assets/Scenes/Main.unity") to a
        // reserialized asset path, tolerating backslash + leading-slash diffs.
        private static bool ScenePathMatches(string scenePath, string assetPath)
        {
            if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(assetPath)) return false;
            var a = scenePath.Replace('\\', '/').TrimStart('/');
            var b = assetPath.Replace('\\', '/').TrimStart('/');
            return string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
        }

        // Maps the user-facing include_meta flag to Unity's ForceReserializeAssetsOptions.
        // Exposed as a pure function so the mapping can be unit-tested without driving
        // AssetDatabase from EditMode.
        public static ForceReserializeAssetsOptions ResolveOptions(bool includeMeta)
        {
            return includeMeta
                ? ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata
                : ForceReserializeAssetsOptions.ReserializeAssets;
        }

        // Normalize for AssetDatabase: forward slashes, no leading slash, rooted under Assets/.
        //
        // B20 — paths outside Assets/ are REJECTED, not silently prefixed. The
        // previous form did `p = "Assets/" + p` for any non-Assets-rooted input,
        // so `../ProjectSettings/ProjectSettings.asset` became
        // `Assets/../ProjectSettings/ProjectSettings.asset` — File.Exists
        // resolved the `..`, ForceReserializeAssets wrote through it, and the
        // same escaped string was reused verbatim as the gate's paths_hint. Now
        // we resolve each candidate against the project root and require it to
        // land inside Assets/; anything that escapes (or is absolute / outside)
        // is collected into `containmentInvalid` and the tool fails before any
        // mutation. Callers that legitimately want a non-Assets path must edit
        // it via a different mechanism — reserialize is scoped to Assets/ by
        // design (the comment two lines above always stated this).
        //
        // A7 — the gate scope (paths_hint) is built from the RAW request array
        // before Execute runs, so NormalizeForHint exposes the validated subset
        // (the same containment check, skipping the file-existence + extension
        // gates that would force a disk probe) for the checkpoint builder. This
        // keeps an unresolvable `..`/absolute path out of VerifyGateAdapter.
        // CreateCheckpoint without duplicating the containment logic at the call
        // site.
        public static string[] NormalizeForHint(string[] rawPaths)
        {
            if (rawPaths == null || rawPaths.Length == 0) return null;
            var normalized = NormalizePaths(rawPaths, out _);
            return normalized.Count == 0 ? null : normalized.ToArray();
        }

        private static System.Collections.Generic.List<string> NormalizePaths(
            string[] rawPaths, out System.Collections.Generic.List<string> containmentInvalid)
        {
            var result = new System.Collections.Generic.List<string>(rawPaths.Length);
            containmentInvalid = new System.Collections.Generic.List<string>();

            // Project root = parent of Assets (Application.dataPath). Resolve
            // once; both the Assets folder and each candidate are resolved
            // against it so `..` segments collapse before the containment check.
            var projectRoot = Directory.GetParent(UnityEngine.Application.dataPath)?.FullName;
            string assetsAbs = Path.GetFullPath(Path.Combine(projectRoot ?? "", "Assets"));

            foreach (var raw in rawPaths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var p = raw.Replace('\\', '/').Trim();

                // Reject absolute paths outright — they can never be a valid
                // Assets-relative asset path and are almost always a mistake
                // (or an attempt to reach outside the project).
                if (Path.IsPathRooted(p))
                {
                    containmentInvalid.Add($"{raw} (absolute paths are not allowed; pass an Assets/-relative path)");
                    continue;
                }

                // Strip any leading slash left after Trim() of backslash-converted input.
                p = p.TrimStart('/');

                // If not already rooted under Assets/, root it — then verify the
                // ROOTED form is genuinely contained (a `..` segment can still
                // escape after prefixing).
                if (!p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)
                    && !p.Equals("Assets", System.StringComparison.OrdinalIgnoreCase))
                {
                    p = "Assets/" + p;
                }

                if (!IsUnderAssets(p, projectRoot, assetsAbs))
                {
                    containmentInvalid.Add($"{raw} (escapes Assets/ — reserialize is scoped to Assets/)");
                    continue;
                }

                result.Add(p);
            }
            return result;
        }

        // True iff the canonical absolute resolution of `assetRelativePath`
        // (resolved against the project root) is `assetsAbs` itself or a
        // descendant of it. Collapses `..` and `.` segments via GetFullPath so
        // `Assets/../ProjectSettings/X.asset` resolves outside Assets/ and is
        // rejected. Returns false when resolution fails (e.g. path is null/empty
        // or the project root is unknown).
        internal static bool IsUnderAssets(string assetRelativePath, string projectRoot, string assetsAbs)
        {
            if (string.IsNullOrEmpty(assetRelativePath) || string.IsNullOrEmpty(projectRoot))
                return false;
            string resolved;
            try
            {
                resolved = Path.GetFullPath(Path.Combine(projectRoot, assetRelativePath.Replace('\\', '/')));
            }
            catch
            {
                return false;
            }
            // OrdinalIgnoreCase: on Windows the drive letter casing can differ,
            // and macOS/Linux paths are case-sensitive so an exact prefix is the
            // strict check. We compare with a trailing separator to avoid
            // `AssetsFoo` matching `Assets`.
            string withSep = assetsAbs.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString())
                ? assetsAbs
                : assetsAbs + System.IO.Path.DirectorySeparatorChar;
            return string.Equals(resolved, assetsAbs, System.StringComparison.OrdinalIgnoreCase)
                || resolved.StartsWith(withSep, System.StringComparison.OrdinalIgnoreCase);
        }

        private static void CollectInvalidInto(System.Collections.Generic.List<string> paths, System.Collections.Generic.List<string> invalid)
        {
            foreach (var p in paths)
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                bool extOk = false;
                foreach (var supported in SupportedExtensions)
                {
                    if (supported == ext) { extOk = true; break; }
                }
                if (!extOk)
                {
                    invalid.Add($"{p} (unsupported extension '{ext}'; supported: {string.Join(", ", SupportedExtensions)})");
                    continue;
                }

                var full = p;
                if (!File.Exists(full))
                {
                    invalid.Add($"{p} (file not found)");
                }
            }
        }

        private static string BuildResult(System.Collections.Generic.List<string> paths, bool includeMeta,
            System.Collections.Generic.List<string> touchedOpenScenes)
        {
            var sb = new StringBuilder(256 + paths.Count * 64);
            sb.Append("{\"reserialized\":[");
            for (int i = 0; i < paths.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(paths[i])).Append('"');
            }
            sb.Append("],\"totalCount\":").Append(paths.Count);
            sb.Append(",\"wholeProject\":false");
            sb.Append(",\"includeMeta\":").Append(includeMeta ? "true" : "false");
            // feedback-01-08-glm §9 — which opened scenes had their disk YAML
            // rewritten (and were therefore marked dirty) so the agent knows a
            // reload is needed to sync memory with the reserialized disk state.
            sb.Append(",\"touchedOpenScenes\":[");
            if (touchedOpenScenes != null)
            {
                for (int i = 0; i < touchedOpenScenes.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(touchedOpenScenes[i])).Append('"');
                }
            }
            sb.Append(']');
            sb.Append(',').Append(SceneDirtyState.BuildDirtyScenesJson());
            sb.Append('}');
            return sb.ToString();
        }

        // Single source of truth for JSON string-content escaping is BridgeJson
        // (T30.5). Returns escaped CONTENT (no surrounding quotes), matching the
        // call sites here; preserves the `null ⇒ ""` contract.
        private static string Esc(string s) => BridgeJson.EscapeStringContent(s);
    }
}
