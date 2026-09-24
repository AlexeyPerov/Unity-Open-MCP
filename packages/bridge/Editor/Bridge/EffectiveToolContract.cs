using System;
using System.Text.RegularExpressions;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge
{
    // One request's resolved dispatch contract. HandleToolDispatch resolves it
    // once and threads it through the gate path, so tools with request-derived
    // scope, preflight or envelope identity (project commands, batch_execute)
    // plug in here instead of as name checks scattered through the dispatcher.
    internal sealed class ToolRequestContract
    {
        internal string ToolName;
        internal bool IsMutating;
        internal LifecyclePolicy Lifecycle;
        /// <summary>Preflight refusal; null when the request may dispatch.</summary>
        internal ToolDispatchResult Refusal;
        /// <summary>Request-derived mutation scope; null means "read paths_hint and the per-tool fallbacks".</summary>
        internal string[] ScopedPaths;
        /// <summary>Preflight output the batch gate runner reuses; null for every other tool.</summary>
        internal BatchExecuteTool.BatchPlan BatchPlan;
        /// <summary>Dispatch that reuses this contract's preflight output (bound arguments); null means
        /// the plain <c>DispatchTool(toolName, body)</c> path.</summary>
        internal Func<ToolDispatchResult> Invoke;
        /// <summary>Attaches tool identity (projectCommand, duration, bypass) to any envelope; no-op for plain tools.</summary>
        internal Action<GateDispatchResult, long> Decorate = (_, __) => { };

        /// <summary>Failure envelopes built outside the gate path (timeout, fault,
        /// main-thread block) still carry the tool identity when the contract has one.</summary>
        internal string WrapFailure(string json, string code, string message, string gateMode, long durationMs)
        {
            var failed = GatePolicy.Skipped(ToolDispatchResult.Fail(code, message), "request_rejected");
            Decorate(failed, durationMs);
            if (failed.ProjectCommandJson == null) return json;
            BridgeAuditRecorder.RecordGateRun(ToolName, gateMode, failed, ScopedPaths);
            return "{\"projectCommand\":" + failed.ProjectCommandJson + "," + json.Substring(1);
        }
    }

    // Request-level classification. Catalog flags remain conservative defaults.
    internal static class EffectiveToolContract
    {
        private static readonly Regex DisruptiveSnippet = new Regex(
            @"\b(RequestScriptCompilation|Refresh|ImportAsset|OpenScene|NewScene|CloseScene|LoadScene|LoadSceneAsync|UnloadSceneAsync|EnterPlaymode|ExitPlaymode|ExecuteMenuItem|BuildPlayer|SwitchActiveBuildTarget|SetScriptingDefineSymbols|RequestReload|ReloadAssembly|LockReloadAssemblies|UnlockReloadAssemblies)\b|\bisPlaying\s*=(?!=)",
            RegexOptions.CultureInvariant);

        internal static bool IsReadOnlySnippet(string body) =>
            JsonBody.GetBool(JsonBody.TopLevelField(body, "read_only"), "read_only")
            && !JsonBody.GetBool(JsonBody.TopLevelField(body, "setup_roslyn"), "setup_roslyn")
            && !DisruptiveSnippet.IsMatch(JsonBody.GetString(JsonBody.TopLevelField(body, "code"), "code") ?? "");

        internal static bool IsMutating(string tool, string body)
        {
            if (tool == ProjectCommandInvocation.ToolName)
                return !ProjectCommandInvocation.Resolve(body, out var command) || command.Attribute.IsMutating;
            if (tool == "unity_open_mcp_execute_csharp" && IsReadOnlySnippet(body)) return false;
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsReadOnlyMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path"))) return false;
            if (tool == "unity_open_mcp_apply_fix" && JsonBody.GetBool(JsonBody.TopLevelField(body, "dry_run"), "dry_run", true)) return false;
            if (tool == "unity_open_mcp_batch_execute")
                return BatchExecuteTool.Preflight(body, out var mutating) != null || mutating;
            return BridgeToolClassification.MutatingTools.Contains(tool)
                || (BridgeToolRegistry.TryGet(tool, out var entry) && entry.IsMutating);
        }

        // `envelopeValidated`: the caller (the HTTP handler) already validated
        // the body against the tool's request schema, so the preflights below
        // do not repeat that pass. Direct/test callers leave it false.
        internal static ToolRequestContract Resolve(string tool, string body, bool envelopeValidated = false)
        {
            if (tool == ProjectCommandInvocation.ToolName)
            {
                var refusal = ProjectCommandInvocation.Preflight(body, out var entry, out var values, envelopeValidated: envelopeValidated);
                return new ToolRequestContract
                {
                    ToolName = tool,
                    Refusal = refusal,
                    IsMutating = entry == null || entry.Attribute.IsMutating,
                    Lifecycle = entry?.Attribute.Lifecycle ?? LifecyclePolicy.None,
                    ScopedPaths = ProjectCommandInvocation.ScopedPaths(body),
                    Decorate = (result, durationMs) => ProjectCommandInvocation.Decorate(result, body, durationMs),
                    // Reuse the bound arguments: the gate must not preflight twice.
                    Invoke = refusal == null ? () => ProjectCommandInvocation.Execute(body, entry, values) : null,
                };
            }
            if (tool == "unity_open_mcp_batch_execute")
            {
                var refusal = BatchExecuteTool.Preflight(body, out var mutating, out var plan, envelopeValidated);
                return new ToolRequestContract
                {
                    ToolName = tool,
                    Refusal = refusal,
                    IsMutating = refusal != null || mutating,
                    // All-read batches skip the settle wait; a refused batch never dispatches.
                    Lifecycle = refusal == null && mutating ? LifecyclePolicy.EditorSettle : LifecyclePolicy.None,
                    BatchPlan = plan,
                };
            }
            return new ToolRequestContract { ToolName = tool, IsMutating = IsMutating(tool, body), Lifecycle = Lifecycle(tool, body) };
        }

        internal static LifecyclePolicy Lifecycle(string tool, string body)
        {
            if (tool == ProjectCommandInvocation.ToolName)
                return ProjectCommandInvocation.Resolve(body, out var command) ? command.Attribute.Lifecycle : LifecyclePolicy.None;
            if (tool == "unity_open_mcp_execute_csharp" && IsReadOnlySnippet(body)) return LifecyclePolicy.None;
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsNonDisruptiveReadOnlyMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path"))) return LifecyclePolicy.None;
            if (tool == "unity_open_mcp_batch_execute" && !IsMutating(tool, body)) return LifecyclePolicy.None;
            return ToolLifecycle.Resolve(tool);
        }
    }
}
