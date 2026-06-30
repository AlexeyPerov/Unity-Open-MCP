using System.Collections.Generic;
using System.Text;
using UnityOpenMcpBridge.Console;

namespace UnityOpenMcpBridge
{
    // Hand-rolled JSON helpers shared across the bridge. The bridge deliberately
    // has no Newtonsoft/serializer dependency (see packages/bridge/AGENTS.md
    // §Transport): every response envelope is assembled with these escape +
    // build primitives. Centralized here so the gate/fault/timeout/ping shapes
    // stay in one place rather than scattered through BridgeHttpServer.

    internal static class BridgeJson
    {
        // --- JSON string escaping -------------------------------------------------

        internal static string EscapeString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            EscapeStringContentTo(sb, s);
            sb.Append('"');
            return sb.ToString();
        }

        internal static string EscapeStringContent(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            EscapeStringContentTo(sb, s);
            return sb.ToString();
        }

        internal static void EscapeStringContentTo(StringBuilder sb, string s)
        {
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
                        if (c < 32)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
        }

        // --- Gate dispatch response envelopes -------------------------------------
        // Every mutating dispatch returns one of these shapes: the success/failure
        // mutation result wrapped with the gate (mode/checkpoint/validation/delta)
        // and lifecycle telemetry, or one of the short-circuit error envelopes
        // (timeout / execution fault / paths_hint_required). They are emitted with
        // HTTP 200 so the MCP server can surface the structured error rather than
        // treating it as transport failure.

        internal static string BuildGateEnvelope(GateDispatchResult result, string gateMode, LifecyclePolicy lifecycle)
        {
            var sb = new StringBuilder(1024);

            sb.Append("{\"mutation\":{\"success\":");
            sb.Append(result.Mutation.Success ? "true" : "false");
            sb.Append(",\"output\":");
            sb.Append(result.Mutation.Output ?? "null");
            if (result.Mutation.ErrorCode != null)
            {
                sb.Append(",\"error\":{\"code\":\"").Append(EscapeStringContent(result.Mutation.ErrorCode));
                sb.Append("\",\"message\":\"").Append(EscapeStringContent(result.Mutation.ErrorMessage ?? ""));
                sb.Append("\"}");
            }
            else
            {
                sb.Append(",\"error\":null");
            }
            sb.Append('}');

            sb.Append(",\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\"");

            if (!result.GateRan)
            {
                if (result.CheckpointId != null)
                    sb.Append(",\"checkpointId\":\"").Append(EscapeStringContent(result.CheckpointId)).Append("\"");
                sb.Append(",\"skipped\":true,\"validation\":null,\"delta\":null");
            }
            else
            {
                sb.Append(",\"checkpointId\":\"").Append(EscapeStringContent(result.CheckpointId));
                sb.Append("\",\"skipped\":false");
                sb.Append(",\"validation\":{\"passed\":").Append(result.Outcome == GateOutcome.Passed ? "true" : "false");
                sb.Append(",\"categoriesRun\":[");
                if (result.CategoriesRun != null)
                {
                    for (int i = 0; i < result.CategoriesRun.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.CategoriesRun[i])).Append('"');
                    }
                }
                sb.Append("],\"durationMs\":").Append(result.ValidationDurationMs);
                sb.Append('}');

                sb.Append(",\"delta\":{\"newErrors\":").Append(result.Delta?.NewErrors ?? 0);
                sb.Append(",\"newWarnings\":").Append(result.Delta?.NewWarnings ?? 0);
                sb.Append(",\"resolvedErrors\":").Append(result.Delta?.ResolvedErrors ?? 0);
                sb.Append(",\"resolvedWarnings\":").Append(result.Delta?.ResolvedWarnings ?? 0);
                sb.Append(",\"newIssues\":[");
                if (result.Delta?.NewIssueKeys != null)
                {
                    for (int i = 0; i < result.Delta.NewIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.Delta.NewIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("],\"resolvedIssues\":[");
                if (result.Delta?.ResolvedIssueKeys != null)
                {
                    for (int i = 0; i < result.Delta.ResolvedIssueKeys.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.Delta.ResolvedIssueKeys[i])).Append('"');
                    }
                }
                sb.Append("]}");
            }

            sb.Append('}');

            // M13 T4.1 — lifecycle hint + settle telemetry. Emitted on every
            // gate envelope so agents know whether the op was read-only, may
            // have settled, or survived a domain reload. settleMs is the time
            // the bridge blocked waiting for the editor to finish compiling.
            sb.Append(",\"lifecycle\":\"").Append(lifecycle.ToWireString()).Append("\"");
            sb.Append(",\"settleMs\":").Append(result.SettleMs);

            // M13 T4.2 — dirty-scene paths when the op was refused by the
            // active-scene guard. Omitted (null) when allowed.
            if (result.DirtyScenePaths != null && result.DirtyScenePaths.Length > 0)
            {
                sb.Append(",\"dirtyScenes\":[");
                for (int i = 0; i < result.DirtyScenePaths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeStringContent(result.DirtyScenePaths[i])).Append('"');
                }
                sb.Append("]");
            }
            else
            {
                sb.Append(",\"dirtyScenes\":null");
            }

            // M22 T22.1.3 — per-call `logs`: console entries captured during
            // this dispatch. Always emitted (empty [] when none) so the field is
            // present on every tool response; agents read it inline instead of
            // polling read_console after a mutation. Stacks are omitted here
            // (compact) — read_console stays the verbose path with stacks.
            sb.Append(",\"logs\":");
            sb.Append(BuildLogsJson(result.Logs));

            sb.Append(",\"agentNextSteps\":[");
            var steps = result.AgentNextSteps;
            if (steps != null)
            {
                for (int i = 0; i < steps.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeStringContent(steps[i])).Append('"');
                }
            }
            sb.Append("]}");

            return sb.ToString();
        }

        // Render the `logs` array for a captured entry list. Null/empty both
        // produce "[]" so the field round-trips deterministically.
        internal static string BuildLogsJson(List<LogEntryInfo> logs)
        {
            if (logs == null || logs.Count == 0) return "[]";
            var sb = new StringBuilder(logs.Count * 64);
            sb.Append('[');
            for (int i = 0; i < logs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = logs[i];
                sb.Append("{\"severity\":\"").Append(EscapeStringContent(LogEntriesReader.Classify(e.Mode)));
                sb.Append("\",\"message\":\"").Append(EscapeStringContent(e.Message ?? ""));
                sb.Append("\",\"source\":\"unity\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Splice a `logs` field into a flat JSON object string (the
        // direct-response tool path: success bodies are raw tool JSON like
        // {"status":"ok",...}). Inserts `,"logs":[...]` before the final
        // top-level '}' so the logs appear as a sibling of the tool's own
        // fields. Non-object / unbalanced JSON is returned unchanged.
        internal static string SpliceLogsField(string json, List<LogEntryInfo> logs)
        {
            if (string.IsNullOrEmpty(json) || logs == null || logs.Count == 0) return json;

            // Walk respecting strings to find the last top-level closing brace.
            int depth = 0;
            int lastTopBrace = -1;
            bool inStr = false;
            bool esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) lastTopBrace = i;
                }
            }
            if (lastTopBrace < 0) return json;

            var logsJson = BuildLogsJson(logs);
            return json.Substring(0, lastTopBrace) + ",\"logs\":" + logsJson + json.Substring(lastTopBrace);
        }

        internal static string BuildTimeoutEnvelope(string toolName, string gateMode, int timeoutMs)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"timeout\",\"message\":\"Tool '");
            sb.Append(EscapeStringContent(toolName));
            sb.Append("' timed out after ");
            sb.Append(timeoutMs);
            sb.Append("ms\"}},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            // M22 T22.1.3 — logs present (empty) on every envelope. Timeout
            // short-circuits before capture completes, so there is nothing to
            // report; the field still round-trips for shape consistency.
            sb.Append(",\"logs\":[]");
            sb.Append(",\"agentNextSteps\":[\"Tool execution timed out. Consider increasing timeout_ms or simplifying the operation.\"]}");
            return sb.ToString();
        }

        internal static string BuildFaultEnvelope(System.Exception e, string gateMode)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"execution_error\",\"message\":\"");
            sb.Append(EscapeStringContent(e.Message));
            sb.Append("\"}},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"logs\":[]");
            sb.Append(",\"agentNextSteps\":[\"Tool execution failed with an unexpected error.\"]}");
            return sb.ToString();
        }

        internal static string BuildPathsHintErrorEnvelope(string toolName, string gateMode)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"paths_hint_required\",\"message\":\"");
            sb.Append("Mutating tool '");
            sb.Append(EscapeStringContent(toolName));
            sb.Append("' requires a non-empty 'paths_hint' array. ");
            sb.Append("Provide asset paths likely to be affected (e.g. [\\\"Assets/Prefabs/Player.prefab\\\"]) so the gate can scope validation correctly. ");
            sb.Append("There is no whole-project fallback — explicit paths are mandatory.");
            sb.Append("\"}},\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"logs\":[]");
            sb.Append(",\"agentNextSteps\":[\"Add 'paths_hint' with at least one asset path before retrying.\"]}");
            return sb.ToString();
        }

        // Error JSON for gate-free (direct-response) tools — flat { error: {...} }
        // without the gate envelope, since these bypass the checkpoint/validate flow.
        internal static string BuildDirectToolErrorJson(ToolDispatchResult result)
        {
            return $"{{\"error\":{{\"code\":\"{EscapeStringContent(result.ErrorCode)}\",\"message\":\"{EscapeStringContent(result.ErrorMessage)}\"}}}}";
        }

        // M13 T4.2 — agent-facing next-steps when a mutating op is refused because
        // the active scene has unsaved changes.
        internal static string[] BuildSceneDirtyNextSteps(string[] dirtyPaths)
        {
            var list = new List<string>(3);
            if (dirtyPaths == null || dirtyPaths.Length == 0)
            {
                list.Add("Save or discard the active scene's unsaved changes, then retry.");
            }
            else
            {
                list.Add("Save or discard changes to the dirty scene(s) before retrying: "
                    + string.Join(", ", dirtyPaths) + ".");
            }
            list.Add("To save via the bridge: unity_open_mcp_execute_csharp with " +
                     "EditorSceneManager.SaveScene(EditorSceneManager.GetSceneManagerSetup()[0].path) " +
                     "(or MarkSceneDirty + SaveScene for an unsaved scene).");
            list.Add("To discard: EditorSceneManager.RestoreSavedSceneState(), or retry the " +
                     "original call with ignore_scene_dirty: true to proceed and accept the risk.");
            return list.ToArray();
        }

        // /ping response body — live bridge status snapshot.
        internal static string BuildPingJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"connected\":").Append(BridgeSession.Connected && BridgeSession.IsInitialized ? "true" : "false").Append(',');
            sb.Append("\"projectPath\":").Append(EscapeString(BridgeSession.ProjectPath)).Append(',');
            sb.Append("\"unityVersion\":").Append(EscapeString(BridgeSession.UnityVersion)).Append(',');
            sb.Append("\"bridgeVersion\":").Append(EscapeString(BridgeSession.BridgeVersion)).Append(',');
            sb.Append("\"mode\":").Append(EscapeString(BridgeSession.Mode)).Append(',');
            sb.Append("\"compiling\":").Append(BridgeSession.IsCompiling ? "true" : "false").Append(',');
            sb.Append("\"isPlaying\":").Append(BridgeSession.IsPlaying ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }
    }
}
