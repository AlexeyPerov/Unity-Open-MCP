using System.Collections.Generic;
using System.Text;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.References;
using UnityOpenMcpVerify.Rules.Dependencies;
using UnityEditor;

namespace UnityOpenMcpBridge.MetaTools
{
    // Forward + reverse dependency edges for a single asset, exposed as the
    // unity_open_mcp_dependencies typed tool. Reuses the same scanners the
    // `dependencies` verify rule and `find_references` tool run against — no
    // second dependency graph is built:
    //   - Forward edges: Dependencies.Scanner (packages/verify) — the same
    //     AssetDatabase.GetDependencies walk the verify rule uses internally.
    //   - Reverse edges: ReferenceGraph.Find (packages/verify) — the same
    //     reverse-lookup surface unity_open_mcp_find_references exposes.
    //
    // M22 Plan 3 / T-fix-1 — this tool lives in its own sub-asmdef
    // (com.alexeyperov.unity-open-mcp-bridge.Dependencies.Editor) that
    // references com.alexeyperov.unity-open-mcp-verify.Editor. The bridge
    // ROOT assembly no longer holds a compile-time reference to
    // UnityOpenMcpVerify.Rules.Dependencies — this was the sole root consumer.
    // When Unity enters Safe Mode and verify's Rules.* types cannot bind, the
    // whole sub-asmdef is simply absent rather than producing a CS0103 inside
    // the root bridge assembly (which previously kept the [InitializeOnLoad]
    // listener from ever recovering). Dispatch is via the [BridgeTool]
    // reflection registry (BridgeToolRegistry), mirroring every other
    // sub-asmdef (Cinemachine, Texture, ...).
    [BridgeToolType]
    public static class DependenciesTool
    {
        // Registry-discovered entry point. Typed parameters mirror the MCP
        // tool schema; the bridge reflection registry invokes this via
        // BridgeToolRegistry.TryDispatch. Delegates to Execute(body) so the
        // body-parsing + result-building path stays single-sourced and the
        // existing EditMode tests (which call Execute directly) keep passing.
        [BridgeTool("unity_open_mcp_dependencies",
            Title = "Dependencies",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = true,
            Lifecycle = LifecyclePolicy.None,
            Group = null)]
        [System.ComponentModel.Description(
            "Forward AND reverse dependency edges for a single asset in one " +
            "call, plus broken forward-edge GUIDs and dependency-cycle trails. " +
            "Reuses the same scanners as the `dependencies` verify rule and " +
            "find_references — no second dependency graph is built. Read-only, " +
            "gate-free. Live bridge only (the scanners call AssetDatabase).")]
        public static string Run(
            string asset_path = null,
            string guid = null,
            string detail = "normal",
            int max_results = 100)
        {
            var body = BuildBody(asset_path, guid, detail, max_results);
            var result = Execute(body);
            if (result.Success) return result.Output;
            // Flat error envelope matching the direct-response tool contract
            // (BuildDirectToolErrorJson shape): { "error": { "code", "message" } }.
            return "{\"error\":{\"code\":\"" + Escape(result.ErrorCode) +
                   "\",\"message\":\"" + Escape(result.ErrorMessage) + "\"}}";
        }

        // Body-driven entry point retained for EditMode tests + internal reuse.
        public static ToolDispatchResult Execute(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            var guid = JsonBody.GetString(body, "guid");
            // detail defaults to "normal" (full forward/reverse rosters); callers
            // who only want counts pass "summary".
            var detail = JsonBody.GetString(body, "detail") ?? "normal";
            var maxResults = JsonBody.GetInt(body, "max_results", 100);

            if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
                return ToolDispatchResult.Fail("missing_parameter",
                    "Either 'asset_path' or 'guid' is required.");

            // Resolve the queried asset to a canonical path + GUID. Either input
            // form is accepted (path OR guid); both are echoed back in the
            // response for parity with find_references.
            string resolvedPath;
            string resolvedGuid;
            if (!string.IsNullOrEmpty(guid))
            {
                resolvedGuid = guid;
                resolvedPath = AssetDatabase.GUIDToAssetPath(guid);
            }
            else
            {
                resolvedPath = assetPath;
                resolvedGuid = AssetDatabase.AssetPathToGUID(assetPath);
            }

            if (string.IsNullOrEmpty(resolvedPath))
            {
                // GUID (or path) does not resolve to a real asset — return an
                // explicit empty result rather than throwing so an agent can
                // branch on queriedAssetPath/QueriedAssetGuid being set but the
                // edge arrays being empty.
                return ToolDispatchResult.Ok(BuildEmpty(resolvedPath, resolvedGuid, detail));
            }

            AssetDependencyData forward;
            try
            {
                forward = ComputeForward(resolvedPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("dependency_error", e.Message);
            }

            List<string> reversePaths;
            try
            {
                reversePaths = ComputeReverse(resolvedPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("reference_error", e.Message);
            }

            return ToolDispatchResult.Ok(BuildResult(resolvedPath, resolvedGuid, forward, reversePaths, detail, maxResults));
        }

        // Forward edges via Dependencies.Scanner. The scanner takes a scoped
        // path set and returns AssetDependencyData with ForwardDeps populated
        // from AssetDatabase.GetDependencies + the m_AssetGUID edges Unity's
        // walk misses. Scanner returns no data for MonoScript/DefaultAsset
        // (the same gate rule behaviour) — in that case ForwardDeps is empty.
        private static AssetDependencyData ComputeForward(string assetPath)
        {
            var sink = new List<AssetDependencyData>();
            // Fully-qualified: the `Dependencies` sub-namespace name is also a
            // common identifier, and relying on the `using` import to resolve
            // the prefix is fragile across Roslyn/Unity versions. The
            // fully-qualified form removes any ambiguity.
            UnityOpenMcpVerify.Rules.Dependencies.Scanner.ScanPaths(new[] { assetPath }, sink);
            return sink.Count > 0 ? sink[0] : new AssetDependencyData(assetPath);
        }

        // Reverse edges via ReferenceGraph.Find (the same surface
        // unity_open_mcp_find_references uses). The default options walk every
        // project asset; for a single-asset reverse lookup that is the correct
        // scope — the result is every asset that references the queried asset.
        private static List<string> ComputeReverse(string assetPath)
        {
            var graph = ReferenceGraph.Find(assetPath, ReferenceGraphOptions.Default);
            return graph.ReferencedByPaths;
        }

        private static string BuildEmpty(string path, string guid, string detail)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            AppendQueried(sb, path, guid);
            sb.Append(",\"status\":\"asset_not_found\"");
            sb.Append(",\"forwardDependencies\":[]");
            sb.Append(",\"reverseDependencies\":[]");
            sb.Append(",\"forwardCount\":0");
            sb.Append(",\"reverseCount\":0");
            sb.Append(",\"detail\":").Append(EscapeString(detail));
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildResult(
            string path,
            string guid,
            AssetDependencyData forward,
            List<string> reversePaths,
            string detail,
            int maxResults)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            AppendQueried(sb, path, guid);

            // Forward edges — every resolved forward dependency the scanner
            // recorded. ForwardDeps is already deduped + self-edge filtered
            // by the scanner. Unresolved forward targets (broken_dependency)
            // are surfaced in the brokenForwardGuids array so agents see the
            // same edge set the dependencies verify rule reports.
            var forwardDeps = forward.ForwardDeps;
            var brokenGuids = CollectBrokenGuids(forward);

            sb.Append(",\"forwardDependencies\":[");
            if (detail != "summary")
            {
                for (int i = 0; i < forwardDeps.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    sb.Append("\"assetPath\":").Append(EscapeString(forwardDeps[i]));
                    sb.Append(",\"guid\":").Append(EscapeString(AssetDatabase.AssetPathToGUID(forwardDeps[i])));
                    sb.Append('}');
                }
            }
            sb.Append(']');

            sb.Append(",\"forwardCount\":").Append(forwardDeps.Count);

            // Unresolved forward edges (the broken_dependency set). Empty for
            // a healthy asset.
            sb.Append(",\"brokenForwardGuids\":[");
            for (int i = 0; i < brokenGuids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(EscapeString(brokenGuids[i]));
            }
            sb.Append(']');

            // Dependency cycles passing through this asset (the
            // dependency_cycle set). Empty for an acyclic graph.
            var cycles = forward.CyclesThrough;
            sb.Append(",\"cycles\":[");
            for (int i = 0; i < cycles.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[');
                var cycle = cycles[i];
                for (int j = 0; j < cycle.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(EscapeString(cycle[j]));
                }
                sb.Append(']');
            }
            sb.Append(']');

            // Reverse edges — every asset that references the queried asset.
            // Same shape as find_references' referencedBy but oriented the
            // other way. max_results caps the roster; totalCount reports the
            // untruncated length so an agent knows whether to follow up.
            var totalCount = reversePaths.Count;
            var truncated = 0;
            IList<string> display = reversePaths;
            if (detail != "summary")
            {
                if (maxResults > 0 && totalCount > maxResults)
                {
                    var slice = new List<string>(maxResults);
                    for (int i = 0; i < maxResults; i++) slice.Add(reversePaths[i]);
                    display = slice;
                    truncated = totalCount - maxResults;
                }
            }
            else
            {
                display = new List<string>();
            }

            sb.Append(",\"reverseDependencies\":[");
            for (int i = 0; i < display.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = display[i];
                sb.Append('{');
                sb.Append("\"assetPath\":").Append(EscapeString(p));
                sb.Append(",\"guid\":").Append(EscapeString(AssetDatabase.AssetPathToGUID(p)));
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append(",\"reverseCount\":").Append(totalCount);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"detail\":").Append(EscapeString(detail));

            sb.Append('}');
            return sb.ToString();
        }

        // Distinct unresolved forward-edge target GUIDs — the same set the
        // dependencies rule emits as broken_dependency issues.
        private static List<string> CollectBrokenGuids(AssetDependencyData data)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (var edge in data.DeclaredEdges)
            {
                if (edge.Resolves) continue;
                if (!seen.Add(edge.TargetGuid)) continue;
                result.Add(edge.TargetGuid);
            }
            return result;
        }

        private static void AppendQueried(StringBuilder sb, string path, string guid)
        {
            sb.Append("\"queriedAssetPath\":").Append(EscapeString(path));
            sb.Append(",\"queriedAssetGuid\":").Append(EscapeString(guid));
        }

        private static string BuildBody(string assetPath, string guid, string detail, int maxResults)
        {
            var sb = new StringBuilder(96);
            sb.Append('{');
            bool needComma = false;
            if (assetPath != null)
            {
                sb.Append("\"asset_path\":").Append(EscapeString(assetPath));
                needComma = true;
            }
            if (guid != null)
            {
                if (needComma) sb.Append(',');
                sb.Append("\"guid\":").Append(EscapeString(guid));
                needComma = true;
            }
            if (detail != null)
            {
                if (needComma) sb.Append(',');
                sb.Append("\"detail\":").Append(EscapeString(detail));
                needComma = true;
            }
            if (needComma) sb.Append(',');
            sb.Append("\"max_results\":").Append(maxResults);
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            return EscapeString(s);
        }

        private static string EscapeString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
