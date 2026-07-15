using System.Collections.Generic;
using System.Globalization;
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
        // --- JSON validity check --------------------------------------------------

        // Lightweight top-level JSON VALUE validator. Returns true only when
        // `json` is a single, balanced JSON value — object, array, string,
        // number, or one of true/false/null — with no trailing garbage and no
        // dangling/truncated structure. Strings, escapes, and both brace kinds
        // ({}, []) are respected so braces/quotes inside string literals don't
        // fool the walker.
        //
        // Used by ExecuteCSharpTool to guard against OutputSerializer producing
        // unbalanced JSON when a snippet result throws mid-walk (e.g. a
        // TypeLoadException escaping the per-member try/catch). Without this
        // guard, the malformed output is interpolated raw into the gate envelope
        // at `result.Mutation.Output`, corrupting the whole body — and the MCP
        // server's res.json() then rejects it.
        //
        // Why a VALUE validator and not just an object validator: OutputSerializer
        // legitimately emits bare scalars for primitive returns (`return 42;` →
        // `"42"`, `return "hi";` → `"\"hi\""`) and bare arrays for array returns
        // (`return new[]{1,2,3};` → `"[1,2,3]"`). These are valid to interpolate
        // into `"output":<value>`. A guard that required a `{...}` object would
        // reject every primitive/array return as malformed — a regression. The
        // only failure to catch is truncation/unbalance (a `{` or `[` with no
        // matching close, or a structural token stranded outside its container),
        // which is exactly what an exception mid-walk produces. The validator is
        // deliberately minimal (no schema, no value-type strictness): its only
        // job is to refuse to trust a string as JSON when its containers don't
        // balance or its string escapes don't terminate.
        internal static bool IsValidJsonObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var depth = 0;          // tracks BOTH {} and [] containers together
            var inStr = false;
            var esc = false;
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
                if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth < 0) return false; // closing before opening
                }
            }
            // Balanced containers (depth 0) and no dangling string. Bare scalars
            // (numbers, true/false/null, quoted strings) have depth 0 throughout
            // and either end cleanly (inStr false) or — for a truncated string —
            // are caught by the inStr check. Objects/arrays balance to depth 0.
            // OutputSerializer serializes exactly one value, so there is no
            // trailing-garbage failure mode to guard against here; the only
            // corruption a mid-walk exception produces is truncation, which the
            // depth/inStr checks catch.
            return depth == 0 && !inStr;
        }

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

        // --- JSON value appenders (T30.5 shared JSON builder helper) --------------
        //
        // Typed tools assemble response JSON with `StringBuilder` (no serializer
        // dependency — see packages/bridge/AGENTS.md §Transport). The two
        // recurring bug classes that escape that discipline:
        //
        //   1. **Split strings** — closing a string literal in one Append and
        //      reopening it in another (`sb.Append("\"note\":\"half..."); ...
        //      sb.Append("...rest\"}")`) leaves the second half as a bare token
        //      → the whole body is rejected as `bridge_response_unparsable`
        //      (see M30 Plan 1 — profiler Start/Stop `note` bug).
        //   2. **Bool casing** — `sb.Append(someBool)` emits C# `True`/`False`,
        //      not JSON `true`/`false` (historical asmdef_list autoReferenced
        //      bug; tests-feedback §6.4).
        //
        // These appenders emit a complete, valid JSON value token in one call,
        // so a contributor cannot accidentally close a string across appends.
        // New hand-rolled JSON across the bridge MUST use them instead of
        // re-implementing the escape switch or `Append(bool)` — see the
        // contributor note in packages/bridge/AGENTS.md §Transport.

        // Append a complete JSON string value (`"..."`), escaped, including the
        // surrounding quotes. `null` is emitted as the bare JSON keyword `null`
        // (callers that want `""` for null should pass an empty string).
        // Returns the buffer so it composes fluently with `Append(':')` etc.
        internal static StringBuilder AppendJsonString(StringBuilder sb, string s)
        {
            if (s == null) return sb.Append("null");
            sb.Append('"');
            EscapeStringContentTo(sb, s);
            sb.Append('"');
            return sb;
        }

        // Append a JSON boolean literal (`true` / `false`). Never use
        // `sb.Append(bool)` for JSON — that emits C# `True`/`False`.
        internal static StringBuilder AppendJsonBool(StringBuilder sb, bool b)
            => sb.Append(b ? "true" : "false");

        // Append a JSON number. Goes through InvariantCulture so decimals are
        // never rendered with a locale comma (a naive ToString would mis-emit
        // "1,5" in de-DE). long overloads avoid any fractional formatting;
        // double rounds to 3dp for stable wire sizes (matches the profiler
        // family's Num()).
        internal static StringBuilder AppendJsonNumber(StringBuilder sb, long n)
            => sb.Append(n.ToString(CultureInfo.InvariantCulture));

        internal static StringBuilder AppendJsonNumber(StringBuilder sb, double d)
            => sb.Append(d.ToString("0.###", CultureInfo.InvariantCulture));

        internal static StringBuilder AppendJsonNumber(StringBuilder sb, float f)
            => sb.Append(f.ToString("0.###", CultureInfo.InvariantCulture));

        // Append a complete `"key":value` pair for a string value. The most
        // common typed-tool shape: `sb.Append(",\"name\":"); sb.Append(Esc(x));`
        // becomes `BridgeJson.AppendJsonStringField(sb, "name", x);`. Keeps the
        // key and the escaped value in one call so neither half can dangle.
        internal static StringBuilder AppendJsonStringField(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            return AppendJsonString(sb, value);
        }

        internal static StringBuilder AppendJsonBoolField(StringBuilder sb, string key, bool value)
        {
            sb.Append('"').Append(key).Append("\":");
            return AppendJsonBool(sb, value);
        }

        internal static StringBuilder AppendJsonNumberField(StringBuilder sb, string key, long value)
        {
            sb.Append('"').Append(key).Append("\":");
            return AppendJsonNumber(sb, value);
        }

        internal static StringBuilder AppendJsonNumberField(StringBuilder sb, string key, double value)
        {
            sb.Append('"').Append(key).Append("\":");
            return AppendJsonNumber(sb, value);
        }

        // --- Shared pagination block ----------------------------------------------
        // Typed tools that page a result window (component_get today; search_assets
        // and scene_get_data when they gain bridge-side paging) emit this block so
        // the {page_size, cursor, next_cursor, truncated} shape stays identical
        // across tools. The cursor is an opaque token of the form
        // "<toolPrefix>:<offset>"; callers pass the tool prefix (e.g.
        // "component_get") and the current offset, and this helper builds the
        // echoed cursor + next_cursor (null when no more pages). `remaining` is
        // the count of entries after the current page — emitted as the block's
        // `truncated` so agents know how many more pages remain.
        internal static StringBuilder AppendPaginationBlock(
            StringBuilder sb, string toolPrefix, int offset, int pageSize, int remaining)
        {
            sb.Append(",\"pagination\":{");
            sb.Append("\"page_size\":").Append(pageSize);
            sb.Append(",\"cursor\":");
            if (offset > 0)
            {
                sb.Append('"').Append(toolPrefix).Append(':')
                  .Append(offset.ToString(CultureInfo.InvariantCulture)).Append('"');
            }
            else
            {
                sb.Append("null");
            }
            if (remaining > 0)
            {
                sb.Append(",\"next_cursor\":\"").Append(toolPrefix).Append(':')
                  .Append((offset + pageSize).ToString(CultureInfo.InvariantCulture)).Append('"');
            }
            else
            {
                sb.Append(",\"next_cursor\":null");
            }
            sb.Append(",\"truncated\":").Append(remaining);
            sb.Append('}');
            return sb;
        }


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

            // M25 Plan 2 — safe auto-fix rollback. Only emitted when a non-
            // dry-run apply_fix was rolled back (fix failed or introduced new
            // errors under enforce). Omitted on every other dispatch and on
            // apply_fix runs that did not need rolling back, so existing
            // consumers see no change.
            if (result.RolledBack)
            {
                sb.Append(",\"rollback\":{\"rolledBack\":true");
                sb.Append(",\"reason\":\"").Append(EscapeStringContent(result.RollbackReason ?? "")).Append("\"");
                sb.Append(",\"restoredPaths\":[");
                if (result.RestoredPaths != null)
                {
                    for (int i = 0; i < result.RestoredPaths.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(EscapeStringContent(result.RestoredPaths[i])).Append('"');
                    }
                }
                sb.Append("]}");
            }

            // M30-polish Plan 5 / T5.1 — gate:"off" on a non-dry-run apply_fix
            // commits the fix with no auto-rollback. Surface a structured
            // warning so the agent knows the mutation is permanent and should
            // verify health manually. Emitted only when the runner flagged it.
            if (result.RollbackDisabled)
            {
                sb.Append(",\"rollbackDisabled\":true");
            }

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
            // execute_csharp runs the snippet synchronously inline on Unity's
            // main thread (ExecuteCSharpTool → method.Invoke, dispatched from
            // BridgeRequestQueue.ProcessQueue on EditorApplication.update). If
            // the snippet blocks the main thread, the worker-thread timeout
            // fires but cannot unwind it — no further editor tick runs, so no
            // cancel/probe/self-heal is possible and the editor stays wedged
            // until externally killed. Steer the agent away from the obvious
            // (and harmful) "raise timeout_ms" reflex and toward diagnosis +
            // the non-blocking test path.
            sb.Append(",\"agentNextSteps\":[");
            if (toolName == "unity_open_mcp_execute_csharp")
            {
                sb.Append("\"execute_csharp runs on Unity's main thread; a timeout usually means the snippet blocked it "
                    + "(e.g. waiting on a callback, WaitOne, .Result, Thread.Sleep, or an infinite loop). "
                    + "The editor may be wedged and unreachable — the HTTP timeout cannot self-heal a stuck main thread. "
                    + "Check editor_status / bridge_status before retrying, and do NOT simply raise timeout_ms. "
                    + "For test execution use unity_senses_run_tests (it is async and does not block).\"");
            }
            else
            {
                sb.Append("\"Tool execution timed out. Consider increasing timeout_ms or simplifying the operation.\"");
            }
            sb.Append("]}");
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

        // specs/feedback.md 2026-07-03 — a Unity modal (unsaved-changes,
        // scene-modified-externally, safe mode) blocked the main thread for the
        // entire per-call window, so the queued dispatch never started. Distinct
        // from BuildTimeoutEnvelope (the work started but ran long): the fix is
        // NOT to raise timeout_ms — it's to dismiss the modal or save/restart.
        // The envelope carries agentNextSteps pointing at the recovery paths so
        // an agent can react instead of burning another 30s.
        internal static string BuildMainThreadBlockedEnvelope(string toolName, string gateMode, int timeoutMs)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"main_thread_blocked\"");
            sb.Append(",\"message\":\"Tool '");
            sb.Append(EscapeStringContent(toolName));
            sb.Append("' could not run — the Unity main thread did not pick up the dispatch within ");
            sb.Append(timeoutMs);
            sb.Append("ms, which means a Unity modal dialog (unsaved changes, scene modified externally, ");
            sb.Append("safe mode) or a long editor operation is blocking it. Do NOT raise timeout_ms — ");
            sb.Append("the main thread is wedged, not slow.\"}}");
            sb.Append(",\"gate\":{\"mode\":\"").Append(EscapeStringContent(gateMode));
            sb.Append("\",\"skipped\":true,\"validation\":null,\"delta\":null}");
            sb.Append(",\"logs\":[]");
            sb.Append(",\"agentNextSteps\":[");
            sb.Append("\"A Unity modal dialog is almost certainly open. Recovery options: \"");
            sb.Append(",\"1. If a scene is dirty, call unity_open_mcp_scene_save (idempotent, never guarded) then retry — ");
            sb.Append("mutating tools that leave a scene dirty can trigger Unity's native save modal on the next reload.\"");
            sb.Append(",\"2. Check editor_status / bridge_status — if it reports bridge_compile_failed or a long stall, ");
            sb.Append("a popup is wedging the editor.\"");
            sb.Append(",\"3. The dismiss loop (UNITY_OPEN_MCP_DIALOG_POLICY) auto-clicks launch-errors / safe-mode / ");
            sb.Append("scene-modified-externally modals during compile-wait; for the unsaved-changes modal set ");
            sb.Append("UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1 to opt in (destructive).\"");
            sb.Append(",\"4. If unreachable, restart the Unity Editor (the popup cannot be dismissed programmatically ");
            sb.Append("from the bridge when the main thread is fully blocked).\"");
            sb.Append("]}");
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
