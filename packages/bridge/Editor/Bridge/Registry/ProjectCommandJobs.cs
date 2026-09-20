using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge
{
    // Domain-local execution only. Losing this record is unknown outcome, never a restart signal.
    [InitializeOnLoad]
    internal static class ProjectCommandJobs
    {
        private sealed class Entry
        {
            internal string Id, Owner, Body, Phase = "accepted", State = "running", Result;
            internal DateTime Finished;
            internal bool Cancellable;
            internal CancellationTokenSource Cancellation = new CancellationTokenSource();
        }
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private static bool shuttingDown;
        static ProjectCommandJobs()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }
        private static void Shutdown()
        {
            shuttingDown = true;
            foreach (var entry in entries.Values)
            {
                try { entry.Cancellation.Cancel(); } catch { }
                entry.Cancellation.Dispose();
            }
        }
        internal static Func<bool> TestRunActive;
        internal static bool Active => entries.Values.Any(e => e.Result == null);
        internal static string Handle(string body, string owner)
        {
            if (!BridgeJson.IsValidJsonObject(body)) return Error("invalid_arguments", "JSON object required.");
            var action = ProjectCommandInvocation.Field(body, "action");
            var id = ProjectCommandInvocation.Field(body, "job_id");
            var keys = JsonBody.GetObjectKeys(body);
            var allowed = action == "start" ? new[] { "action", "job_id", "invocation" } : new[] { "action", "job_id" };
            if (keys.Any(k => !allowed.Contains(k)) || keys.Distinct().Count() != keys.Count)
                return Error("invalid_arguments", "Unexpected or duplicate job transport keys.");
            if (!Guid.TryParse(id, out _)) return Error("invalid_arguments", "job_id must be a UUID.");
            foreach (var key in entries.Where(p => p.Value.Result != null && DateTime.UtcNow - p.Value.Finished > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray())
            { entries[key].Cancellation.Dispose(); entries.Remove(key); }
            if (entries.TryGetValue(id, out var existing))
            {
                if (existing.Owner != owner) return Error("job_not_found", "Unknown job owner.");
                if (action == "start" && existing.Body != JsonBody.GetTopLevelRawValue(body, "invocation"))
                    return Error("idempotency_conflict", "Job id belongs to different arguments.");
                if (action == "cancel")
                {
                    if (!existing.Cancellable) return Error("not_cancellable", "Command has no cooperative cancellation contract.");
                    if (existing.Result == null)
                    { existing.State = "cancel_requested"; existing.Cancellation.Cancel(); }
                }
                else if (action != "status" && action != "start") return Error("invalid_arguments", "Use start, status or cancel.");
                return Snapshot(existing);
            }
            if (action != "start") return Error("job_not_found", "Job record lost or expired; do not restart blindly.");
            if (BridgeToolTogglePolicy.IsDisabled(ProjectCommandInvocation.ToolName)) return Error("tool_disabled", "Project commands are disabled.");
            if (TestRunActive?.Invoke() == true) return Error("editor_busy", "A test run owns the Editor.");
            if (Active) return Error("job_busy", "Another project command is still executing.");
            if (entries.Count >= 256) return Error("job_capacity", "Job retention capacity reached.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return Error("editor_busy", "Start project jobs in a settled EditMode Editor.");
            var invocation = JsonBody.GetTopLevelRawValue(body, "invocation");
            if (invocation == null || System.Text.Encoding.UTF8.GetByteCount(invocation) > 256 * 1024)
                return Error("invalid_arguments", "invocation must be an object within 256 KiB.");
            var entry = new Entry { Id = id, Owner = owner, Body = invocation };
            var context = new ProjectCommandContext(id, entry.Cancellation.Token, phase => entry.Phase = phase);
            var refusal = ProjectCommandInvocation.Preflight(invocation, out var command, out var values, context);
            if (refusal != null)
            {
                entry.Cancellation.Dispose();
                var rejected = GatePolicy.Skipped(refusal, "request_rejected");
                var gate = BridgeRequestBody.ExtractGateMode(invocation);
                ProjectCommandInvocation.Decorate(rejected, invocation, 0);
                BridgeAuditRecorder.RecordGateRun(ProjectCommandInvocation.ToolName, gate, rejected, ProjectCommandInvocation.ScopedPaths(invocation));
                return BridgeJson.BuildGateEnvelope(rejected, gate, LifecyclePolicy.None);
            }
            if (!command.Attribute.Async) { entry.Cancellation.Dispose(); return Error("job_operation_unsupported", "Command is synchronous."); }
            entry.Cancellable = command.Attribute.Cancellable;
            entries.Add(id, entry);
            // Let the start transport finish before checkpointing or invoking project code.
            EditorApplication.CallbackFunction begin = null;
            begin = () => { EditorApplication.update -= begin; _ = Run(entry, command, values); };
            EditorApplication.update += begin;
            return Snapshot(entry);
        }
        private static async Task Run(Entry entry, ProjectCommandCatalog.Entry command, object[] values)
        {
            var mode = BridgeRequestBody.ExtractGateMode(entry.Body);
            bool cancelled = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            async Task<ToolDispatchResult> Invoke()
            {
                try
                {
                    entry.Cancellation.Token.ThrowIfCancellationRequested();
                    entry.Phase = "executing";
                    ProjectCommandInvocation.RecordStarted(entry.Body, command, entry.Id);
                    var task = (Task<string>)command.Method.Invoke(null, values);
                    var raw = await task;
                    return ToolDispatchResult.Ok("{\"result\":" + OutputSerializer.SerializeJson(raw) + "}");
                }
                catch (Exception ex)
                {
                    var cause = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    cancelled = cause is OperationCanceledException && entry.Cancellation.IsCancellationRequested;
                    return command.Attribute.IsMutating
                        ? ToolDispatchResult.PartialFailure(cancelled ? "cancelled" : "execution_error", cause.Message, null)
                        : ToolDispatchResult.Fail(cancelled ? "cancelled" : "execution_error", cause.Message);
                }
                finally
                {
                    entry.Phase = "settling";
                    while (!shuttingDown && (EditorApplication.isCompiling || EditorApplication.isUpdating)) await Task.Delay(50);
                    if (shuttingDown) throw new OperationCanceledException("Editor domain ownership lost.");
                    entry.Phase = "validating";
                }
            }
            try
            {
                entry.Phase = "checkpoint";
                var result = command.Attribute.IsMutating
                    ? await GatePolicy.ExecuteAsync(GatePolicy.ParseMode(mode), ProjectCommandInvocation.ScopedPaths(entry.Body), Invoke)
                    : GatePolicy.Skipped(await Invoke(), "read_only");
                result.EffectiveReadOnly = !command.Attribute.IsMutating;
                ProjectCommandInvocation.Decorate(result, entry.Body, watch.ElapsedMilliseconds);
                result.ProjectCommandJson = result.ProjectCommandJson.Replace("\"jobId\":null", "\"jobId\":" + BridgeJson.EscapeString(entry.Id));
                BridgeAuditRecorder.RecordGateRun(ProjectCommandInvocation.ToolName, mode, result, ProjectCommandInvocation.ScopedPaths(entry.Body));
                entry.Result = BridgeJson.BuildGateEnvelope(result, mode, command.Attribute.Lifecycle);
                entry.State = cancelled ? "cancelled" : result.Mutation.Success && !result.GateFailed ? "succeeded" : "failed";
            }
            catch (Exception ex)
            { entry.State = "failed"; entry.Result = Error("execution_error", ex.Message); }
            entry.Phase = entry.State;
            entry.Finished = DateTime.UtcNow;
        }
        private static string Error(string code, string message) => ProjectCommandCatalog.Error(code, message);
        private static string Snapshot(Entry entry) => "{\"job_id\":" + BridgeJson.EscapeString(entry.Id)
            + ",\"state\":" + BridgeJson.EscapeString(entry.State) + ",\"phase\":" + BridgeJson.EscapeString(entry.Phase)
            + ",\"result\":" + (entry.Result ?? "null") + "}";
    }
}
