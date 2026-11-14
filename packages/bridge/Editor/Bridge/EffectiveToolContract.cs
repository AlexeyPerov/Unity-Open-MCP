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
        /// <summary>Refused with <c>job_busy</c> while an asynchronous project command runs: every
        /// mutation, plus the gate-free Editor-state writes and test starts in <c>JobConflictingTools</c>
        /// and the self-asserted <c>read_only</c> tools, whether called directly or as a batch step.</summary>
        internal bool ConflictsWithJob => IsMutating
            || ConflictsWithJobByName(ToolName)
            || BatchPlan != null && BatchPlan.Steps.Exists(step => ConflictsWithJobByName(step.Tool));

        // A caller's read_only flag waives scope and the gate, but the bridge
        // cannot verify that arbitrary code writes nothing, so it never admits
        // such a call into a running job's checkpoint/validation interval.
        internal static bool ConflictsWithJobByName(string tool) =>
            BridgeToolClassification.JobConflictingTools.Contains(tool)
            || BridgeToolClassification.ExposesReadOnlyParam(tool);
        /// <summary>The request (or one of its batch steps) passed <c>read_only: true</c> to a tool
        /// exposing that flag. Only shapes the <c>job_busy</c> refusal wording.</summary>
        internal bool ReadOnlyAsserted;
        /// <summary>execute_csharp only: the snippet saves scenes, so the dirty-scene guard narrows to
        /// untitled scenes — saving one opens Unity's native save-file dialog.</summary>
        internal bool SavesScenes;
        /// <summary>execute_csharp only: why the snippet keeps the guarded lifecycle
        /// (<see cref="SnippetVerdict.Reason"/>); null otherwise. Shapes the scene_dirty next steps.</summary>
        internal string GuardReason;
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
        private const string ExecuteCSharp = "unity_open_mcp_execute_csharp";

        // The snippet's one classification (SnippetClassifier); everything
        // below that depends on what a snippet does reads this verdict.
        private static SnippetVerdict Snippet(string body) =>
            SnippetClassifier.Classify(JsonBody.GetString(JsonBody.TopLevelField(body ?? "{}", "code"), "code"));

        private static bool Flag(string body, string key) =>
            body != null && JsonBody.GetBool(JsonBody.TopLevelField(body, key), key);

        // A snippet the classifier knows to write or disrupt: read_only cannot
        // waive its scope. An opaque one is left to the caller's assertion.
        private static bool IsKnownWrite(SnippetEffect effect) =>
            effect == SnippetEffect.Disruptive || effect == SnippetEffect.SavesScenes;

        /// <summary>
        /// Why a snippet's <c>read_only: true</c> was overridden, or null when it was
        /// honoured (or not asserted): the call the snippet makes (and what it
        /// does), or setup_roslyn. Shapes the paths_hint_required wording only.
        /// </summary>
        internal static string ReadOnlyOverrideReason(string tool, string body)
        {
            if (tool != ExecuteCSharp || !Flag(body, "read_only")) return null;
            if (Flag(body, "setup_roslyn")) return "setup_roslyn installs the Roslyn fallback (a write)";
            var verdict = Snippet(body);
            return IsKnownWrite(verdict.Effect) ? verdict.Reason : null;
        }

        // The first asset path literal in a snippet ("Assets/…" / "Packages/…"),
        // offered as the paths_hint to send when read_only was overridden.
        private static readonly Regex AssetPathLiteral = new Regex(
            "\"((?:Assets|Packages)/[^\"\\\\]+)\"", RegexOptions.CultureInvariant);

        internal static string SuggestedScope(string body)
        {
            var match = AssetPathLiteral.Match(JsonBody.GetString(JsonBody.TopLevelField(body ?? "{}", "code"), "code") ?? "");
            return match.Success ? match.Groups[1].Value : null;
        }

        internal static bool IsReadOnlySnippet(string body) =>
            Flag(body, "read_only") && !Flag(body, "setup_roslyn") && !IsKnownWrite(Snippet(body).Effect);

        // The lifecycle of one snippet. A read is None. A write the classifier
        // can read — an asset edit, a scene save — only settles the asset
        // database. Everything else keeps the tool's declared class: a call
        // that can compile, reload, import, switch scenes or change play mode,
        // a member resolved by a computed name, an empty snippet, and
        // setup_roslyn.
        private static LifecyclePolicy SnippetLifecycle(string body)
        {
            if (IsReadOnlySnippet(body)) return LifecyclePolicy.None;
            if (Flag(body, "setup_roslyn")) return ToolLifecycle.Resolve(ExecuteCSharp);
            var effect = Snippet(body).Effect;
            return effect == SnippetEffect.Plain || effect == SnippetEffect.SavesScenes
                ? LifecyclePolicy.EditorSettle
                : ToolLifecycle.Resolve(ExecuteCSharp);
        }

        // An upgrade preview (dry_run defaults true) only reads the pins it
        // would rewrite; the apply alone writes configs and schedules the UPM
        // re-pin that can reload the domain.
        private static bool IsUpgradePreview(string tool, string body) =>
            tool == "unity_open_mcp_upgrade"
            && JsonBody.GetBool(JsonBody.TopLevelField(body, "dry_run"), "dry_run", true);

        internal static bool IsMutating(string tool, string body)
        {
            if (tool == ProjectCommandInvocation.ToolName)
                return !ProjectCommandInvocation.Resolve(body, out var command) || command.Attribute.IsMutating;
            if (tool == ExecuteCSharp && IsReadOnlySnippet(body)) return false;
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsReadOnlyMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path"))) return false;
            if (tool == "unity_open_mcp_apply_fix" && JsonBody.GetBool(JsonBody.TopLevelField(body, "dry_run"), "dry_run", true)) return false;
            if (IsUpgradePreview(tool, body)) return false;
            // Snapshot / diff / list / delete and a restore dry run are read-only
            // for the project; only an applied restore writes player state.
            if (tool == Playtest.PlayerStateTools.ToolName) return Playtest.PlayerStateTools.IsMutatingRequest(body);
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
                    ReadOnlyAsserted = plan != null && plan.Steps.Exists(step => AssertsReadOnly(step.Tool, step.ParamsBody)),
                };
            }
            var contract = new ToolRequestContract
            {
                ToolName = tool, IsMutating = IsMutating(tool, body), Lifecycle = Lifecycle(tool, body),
                ReadOnlyAsserted = AssertsReadOnly(tool, body),
            };
            if (tool == ExecuteCSharp && !Flag(body, "setup_roslyn"))
            {
                var verdict = Snippet(body);
                contract.SavesScenes = verdict.Effect == SnippetEffect.SavesScenes;
                if (contract.Lifecycle == LifecyclePolicy.RestartThenSettle) contract.GuardReason = verdict.Reason;
            }
            return contract;
        }

        private static bool AssertsReadOnly(string tool, string body) =>
            BridgeToolClassification.ExposesReadOnlyParam(tool)
            && JsonBody.GetBool(JsonBody.TopLevelField(body ?? "{}", "read_only"), "read_only");

        internal static LifecyclePolicy Lifecycle(string tool, string body)
        {
            if (tool == ProjectCommandInvocation.ToolName)
                return ProjectCommandInvocation.Resolve(body, out var command) ? command.Attribute.Lifecycle : LifecyclePolicy.None;
            if (tool == ExecuteCSharp) return SnippetLifecycle(body);
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsNonDisruptiveReadOnlyMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path"))) return LifecyclePolicy.None;
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsRefreshMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path")))
                return ToolLifecycle.Resolve("unity_open_mcp_assets_refresh");
            if (tool == "unity_open_mcp_batch_execute" && !IsMutating(tool, body)) return LifecyclePolicy.None;
            if (IsUpgradePreview(tool, body)) return LifecyclePolicy.None;
            return ToolLifecycle.Resolve(tool);
        }
    }
}
