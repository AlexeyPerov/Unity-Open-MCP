// Playtest — frame probe (unity_open_mcp_playtest_probe): sample a value every
// N play-mode frames into a bounded buffer that later calls read
// incrementally. start / read / stop / list; see ProbeRegistry.cs for the
// lifecycle and caps, ProbeSources.cs for what can be sampled.
//
// Only `start` needs play mode (play_mode_required otherwise); read / stop /
// list answer in edit mode too, so a buffer stays readable after play mode
// ends. Gate-free and not a project mutation: a probe reads values and its
// expression must classify as read-only.
using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Playtest
{
    [BridgeToolType]
    public static class PlaytestProbeTools
    {
        internal const string ToolName = "unity_open_mcp_playtest_probe";
        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

        [BridgeTool(ToolName,
            Title = "Playtest: Frame Probe",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = true,
            IdempotentHint = false,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "playtest")]
        [System.ComponentModel.Description(
            "Sample a value every N play-mode frames into a bounded buffer owned by the bridge: a member path " +
            "(GameObject / component field or property, or a static Type.Member) or a read-only C# expression. " +
            "Actions start / read (incremental cursor) / stop / list. Ends on until (playtest_wait conditions), " +
            "max_samples, repeated errors, an over-budget sample, stop or play-mode exit. start is play-mode only.")]
        public static ToolDispatchResult Probe(
            string action = null,
            string id = null,
            string expression = null,
            string member = null,
            string game_object_path = null,
            long instance_id = 0,
            string component_type = null,
            string[] usings = null,
            int every_frames = 1,
            int? max_samples = null,
            string until = null,
            string time = "gameplay",
            float? budget_ms = null,
            int since = 0,
            int? max = null)
        {
            switch (action)
            {
                case "start":
                    return Start(id, expression, member, game_object_path, instance_id, component_type, usings, every_frames,
                        max_samples, until, time, budget_ms);
                case "read": return Read(id, since, max);
                case "stop": return Stop(id);
                case "list": return List();
                default:
                    return ToolDispatchResult.Fail("invalid_arguments", "action must be start, read, stop or list.");
            }
        }

        // ---- start --------------------------------------------------------

        internal static ToolDispatchResult Start(string id, string expression, string member, string gameObjectPath, long instanceId,
            string componentType, string[] usings, int everyFrames, int? maxSamples, string until, string time, float? budgetMs)
        {
            if (!PlayModeScheduler.IsPlayingNow)
                return Refuse("play_mode_required",
                    "A probe samples real play-mode frames. Call unity_open_mcp_editor_set_state(state=\"play\") first.");

            var record = Validate(id, expression, member, gameObjectPath, instanceId, componentType, usings, everyFrames,
                maxSamples, until, time, budgetMs, out var refusal);
            if (record == null) return refusal;

            var busy = ProbeRegistry.RefuseStart(record.Id);
            if (busy != null) return ToolDispatchResult.FromRefusalBody(busy);

            var source = Bind(record.Id, expression, member, gameObjectPath, instanceId, componentType, usings, out refusal);
            if (source == null) return refusal;
            record.Source = source;

            var op = ProbeRegistry.Start(record, out var error);
            if (op == null)
            {
                source.Release();
                return ToolDispatchResult.FromRefusalBody(error) ?? ToolDispatchResult.Fail("probe_not_started", error ?? "");
            }

            var sb = new StringBuilder(768);
            sb.Append("{\"status\":\"ok\",\"probe\":");
            record.AppendInfo(sb);
            sb.Append(",\"agentNextSteps\":[").Append(BridgeJson.EscapeString(
                "The probe samples from the next frame on. Read new samples with {\"action\":\"read\",\"id\":\"" + record.Id +
                "\",\"since\":<next_cursor>}; wait for it to end with playtest_wait {\"condition\":{\"probe\":{\"id\":\"" + record.Id +
                "\"}}}; stop it with {\"action\":\"stop\",\"id\":\"" + record.Id + "\"}.")).Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Argument checks and the until condition (no binding, no scheduler —
        // unit-tested). Null + `refusal` on bad input.
        internal static ProbeRecord Validate(string id, string expression, string member, string gameObjectPath, long instanceId,
            string componentType, string[] usings, int everyFrames, int? maxSamples, string until, string time, float? budgetMs,
            out ToolDispatchResult refusal)
        {
            refusal = null;
            if (!ValidId(id, out refusal)) return null;
            bool hasExpression = !string.IsNullOrWhiteSpace(expression), hasMember = !string.IsNullOrWhiteSpace(member);
            if (hasExpression == hasMember)
                return Invalid(out refusal, "Give exactly one of expression (a read-only C# expression) or member (a field / property path).");
            bool hasPath = !string.IsNullOrWhiteSpace(gameObjectPath), hasId = instanceId != 0;
            if (hasPath && hasId)
                return Invalid(out refusal, "Give one target selector: game_object_path or instance_id.");
            if (!string.IsNullOrWhiteSpace(componentType) && !hasPath && !hasId)
                return Invalid(out refusal, "component_type needs a target GameObject (game_object_path or instance_id).");
            if (usings != null && usings.Length > 0 && !hasExpression)
                return Invalid(out refusal, "usings apply to an expression probe only.");
            if (everyFrames < 1 || everyFrames > ProbeRegistry.MaxEveryFrames)
                return Invalid(out refusal, "every_frames must be 1.." + ProbeRegistry.MaxEveryFrames + ".");
            int samples = maxSamples ?? ProbeRegistry.DefaultMaxSamples;
            if (samples < 1 || samples > ProbeRegistry.MaxSamplesCap)
                return Invalid(out refusal, "max_samples must be 1.." + ProbeRegistry.MaxSamplesCap +
                    " (the buffer's upper bound; reaching it ends the probe max_samples_reached).");
            double budget = budgetMs ?? ProbeRegistry.DefaultBudgetMs;
            if (!(budget >= ProbeRegistry.MinBudgetMs && budget <= ProbeRegistry.MaxBudgetMs))
                return Invalid(out refusal, "budget_ms must be " + ProbeRegistry.MinBudgetMs + ".." + ProbeRegistry.MaxBudgetMs + ".");
            time = string.IsNullOrEmpty(time) ? "gameplay" : time;
            if (time != "gameplay" && time != "realtime")
                return Invalid(out refusal, "time must be gameplay or realtime.");

            WaitCondition root = null;
            if (!string.IsNullOrWhiteSpace(until) && until.Trim() != "null")
            {
                root = WaitConditionParser.Parse(until, "until", out var parseError);
                if (root == null)
                {
                    var code = WaitConditionParser.SplitCode(parseError, out var message);
                    refusal = ToolDispatchResult.Fail(code, message);
                    return null;
                }
            }
            return new ProbeRecord
            {
                Id = id,
                EveryFrames = everyFrames,
                MaxSamples = samples,
                BudgetMs = budget,
                GameplayTime = time == "gameplay",
                Until = root,
                Watcher = root == null ? null : new ConditionWatcher(root, everyFrames),
            };
        }

        // Resolve the target and bind the source. Null + `refusal` when the
        // target, the member path or the expression is refused.
        internal static ProbeSource Bind(string id, string expression, string member, string gameObjectPath, long instanceId,
            string componentType, string[] usings, out ToolDispatchResult refusal)
        {
            refusal = null;
            GameObject go = null;
            Component component = null;
            if (!string.IsNullOrWhiteSpace(gameObjectPath) || instanceId != 0)
            {
                go = ProbeSourceBinder.ResolveTarget(gameObjectPath, instanceId, out refusal);
                if (go == null) return null;
                if (!string.IsNullOrWhiteSpace(componentType))
                {
                    var type = ComponentsTools.ResolveComponentType(componentType.Trim());
                    if (type == null || !typeof(Component).IsAssignableFrom(type))
                    {
                        refusal = ToolDispatchResult.Fail("type_not_found", "'" + componentType + "' is not a loaded component type. " +
                            "Use the full name (Namespace.Type); unity_open_mcp_component_get lists a GameObject's components.");
                        return null;
                    }
                    component = go.GetComponent(type);
                    if (component == null)
                    {
                        refusal = ToolDispatchResult.Fail("component_not_found",
                            PlaytestObjects.PathOf(go) + " has no " + type.FullName + " component.");
                        return null;
                    }
                }
            }
            UnityEngine.Object target = component != null ? (UnityEngine.Object)component : go;
            var targetJson = go == null ? null : ProbeSourceBinder.TargetJson(go, component);

            if (!string.IsNullOrWhiteSpace(member))
                return BindMember(member.Trim(), target, targetJson, out refusal);
            return BindExpression(id, expression.Trim(), target, targetJson, usings, out refusal);
        }

        private static ProbeSource BindMember(string member, UnityEngine.Object target, string targetJson, out ToolDispatchResult refusal)
        {
            refusal = null;
            Type staticType = null;
            string path = member;
            if (target == null)
            {
                staticType = ProbeSourceBinder.ResolveStaticRoot(member, out path);
                if (staticType == null)
                {
                    refusal = ToolDispatchResult.Fail("type_not_found",
                        "member '" + member + "' names no loaded type before its last segment. Without a target (game_object_path / " +
                        "instance_id) member is a static path, Namespace.Type.Member (e.g. UnityEngine.Time.frameCount).");
                    return null;
                }
            }
            var steps = ProbeSourceBinder.ParsePath(path, out var parseError);
            if (steps == null) { refusal = ToolDispatchResult.Fail("invalid_arguments", parseError); return null; }
            var read = ProbeSourceBinder.BindMember(staticType == null ? (object)target : null, staticType, steps);
            // One read now: a path that names nothing is refused here; a value
            // that is null or throws for now is the samples' business.
            try { read(); }
            catch (ProbeMemberException ex)
            {
                refusal = ToolDispatchResult.Fail(ex.Code, ex.Message);
                return null;
            }
            catch (Exception) { /* a runtime failure is recorded per sample */ }
            return new ProbeSource
            {
                Kind = "member", Text = member, Read = read, Target = target, TargetJson = targetJson,
                StaticType = staticType?.FullName,
            };
        }

        private static ProbeSource BindExpression(string id, string expression, UnityEngine.Object target, string targetJson,
            string[] usings, out ToolDispatchResult refusal)
        {
            refusal = null;
            var verdict = SnippetClassifier.ClassifyExpression(expression);
            if (verdict.Effect != SnippetEffect.Plain)
            {
                refusal = ToolDispatchResult.FailWithDetail("expression_not_read_only",
                    "A probe expression must only read: " + (verdict.Reason ?? "the expression is empty") + ".",
                    "{\"classification\":" + BridgeJson.EscapeString(verdict.Effect.ToString().ToLowerInvariant()) + "}",
                    new[]
                    {
                        "Sample a member path instead (member + game_object_path / component_type, or a static Type.Member), " +
                        "or reduce the expression to a pure read (no assignment, ++/--, ; or write call).",
                    });
                return null;
            }
            var deny = BridgeDenyList.EvaluateCSharp(expression, BridgeProjectSettings.CSharpDenyPatterns, false);
            if (!deny.Allowed)
            {
                refusal = ToolDispatchResult.Fail("denied_by_policy", deny.Reason + " Matched pattern: " + deny.MatchedPattern + ".");
                return null;
            }
            var targetType = target == null ? null : target.GetType();
            var body = ProbeSourceBinder.ExpressionBody(expression, targetType, id);
            var read = ProbeSourceBinder.CompileExpression(body, usings, target, out var refsField, out refusal);
            if (read == null) return null;
            return new ProbeSource
            {
                Kind = "expression", Text = expression, Read = read, Target = target, TargetJson = targetJson, RefsField = refsField,
            };
        }

        // ---- read / stop / list ------------------------------------------

        internal static ToolDispatchResult Read(string id, int since, int? max)
        {
            if (!Find(id, out var record, out var refusal)) return refusal;
            int limit = max ?? ProbeRegistry.DefaultReadMax;
            if (limit < 1 || limit > ProbeRegistry.MaxReadMax)
                return Refuse("invalid_arguments", "max must be 1.." + ProbeRegistry.MaxReadMax + ".");
            if (since < 0) return Refuse("invalid_arguments", "since must be >= 0 (a next_cursor from an earlier read).");
            int count = record.Samples.Count;
            int from = Math.Min(since, count);
            int to = Math.Min(count, from + limit);
            var sb = new StringBuilder(256 + (to - from) * 96);
            sb.Append("{\"probe\":");
            record.AppendInfo(sb);
            sb.Append(",\"samples\":[");
            for (int i = from; i < to; i++)
            {
                if (i > from) sb.Append(',');
                record.Samples[i].AppendJson(sb);
            }
            sb.Append("],\"since\":").Append(from);
            sb.Append(",\"next_cursor\":").Append(to);
            sb.Append(",\"has_more\":").Append(to < count ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        internal static ToolDispatchResult Stop(string id)
        {
            if (!Find(id, out var record, out var refusal)) return refusal;
            bool wasRunning = record.Running;
            ProbeRegistry.Stop(record);
            var sb = new StringBuilder(1024);
            sb.Append("{\"stopped\":").Append(wasRunning ? "true" : "false");
            sb.Append(",\"probe\":");
            record.AppendInfo(sb);
            sb.Append(",\"summary\":");
            record.AppendSummary(sb);
            sb.Append(",\"buffer\":").Append(BridgeJson.EscapeString(
                "Readable with read until the next start of '" + record.Id + "' or a domain reload."));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        internal static ToolDispatchResult List()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"probes\":[");
            var all = ProbeRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                var r = all[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(BridgeJson.EscapeString(r.Id));
                sb.Append(",\"state\":").Append(BridgeJson.EscapeString(ProbeRecord.StateName(r.State)));
                sb.Append(",\"reason\":").Append(r.ReasonCode == null ? "null" : BridgeJson.EscapeString(r.ReasonCode));
                sb.Append(",\"kind\":").Append(BridgeJson.EscapeString(r.Source.Kind));
                sb.Append(",\"source\":").Append(BridgeJson.EscapeString(PlaytestJson.Clip(r.Source.Text, 120)));
                sb.Append(",\"samples\":").Append(r.Samples.Count);
                sb.Append(",\"errors\":").Append(r.Errors);
                sb.Append(",\"hooked\":").Append(r.Running ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],\"running\":").Append(ProbeRegistry.RunningCount);
            sb.Append(",\"max_running\":").Append(ProbeRegistry.MaxRunning);
            // The hooks as the scheduler sees them: probe operations it still
            // ticks, and whether its tick is in the player loop at all (other
            // play-mode operations keep it there too).
            sb.Append(",\"hooks\":{\"scheduler_probe_operations\":").Append(PlayModeScheduler.RunningCount(ToolName));
            sb.Append(",\"scheduler_tick_installed\":").Append(PlayModeScheduler.TickInstalled ? "true" : "false").Append('}');
            sb.Append(",\"playing\":").Append(PlayModeScheduler.IsPlayingNow ? "true" : "false");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ---- helpers -----------------------------------------------------

        private static bool Find(string id, out ProbeRecord record, out ToolDispatchResult refusal)
        {
            record = null;
            if (!ValidId(id, out refusal)) return false;
            record = ProbeRegistry.Get(id);
            if (record != null) return true;
            refusal = ToolDispatchResult.Fail("probe_not_found",
                "No probe '" + id + "'. Probes live until a domain reload (entering play mode or a recompile drops them); list shows the current ones.");
            return false;
        }

        private static bool ValidId(string id, out ToolDispatchResult refusal)
        {
            refusal = null;
            if (!string.IsNullOrEmpty(id) && IdPattern.IsMatch(id)) return true;
            refusal = ToolDispatchResult.Fail("invalid_arguments", "id is required: 1-64 of [A-Za-z0-9_.-], starting with a letter or digit.");
            return false;
        }

        private static ProbeRecord Invalid(out ToolDispatchResult refusal, string message)
        {
            refusal = ToolDispatchResult.Fail("invalid_arguments", message);
            return null;
        }

        private static ToolDispatchResult Refuse(string code, string message) => ToolDispatchResult.Fail(code, message);
    }
}
