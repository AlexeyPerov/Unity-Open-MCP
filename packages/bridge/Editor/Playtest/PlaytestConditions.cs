// Playtest — wait conditions shared by unity_open_mcp_playtest_wait and the
// `start_on` trigger of unity_senses_capture_series.
//
// A condition is a JSON object with exactly one key:
//   log             {pattern, level?}      a console message (logged after the
//                                          wait started) matches the regex
//   ui              {text_contains?, path?, visible?, matches?}
//                                          a uGUI screen-model query (the
//                                          ui_snapshot node model) matches /
//                                          stops matching
//   object          {path | object_id, state?}
//                                          a GameObject exists / is active /
//                                          is inactive / is destroyed
//   project_command {id, args?, field?}    a synchronous, read-only project
//                                          command returns true (the project's
//                                          own predicate — no arbitrary C#)
//   frames          N                      N frames have run
//   ms              N                      N ms have passed (the operation's
//                                          time base)
//   all / any       [leaf, …]              every / one of the leaves holds at
//                                          the same poll (one level deep)
//
// Event-like leaves (log) latch once seen; delays (frames, ms) are monotonic;
// state-like leaves (ui, object, project_command) are re-read at every poll.
//
// Frames. The scheduler ticks at the start of EarlyUpdate, so a poll at frame
// F+1 sees the state the game left after frame F. A state-like leaf that turns
// true there is reported as `frame: F` (the frame that changed it) with
// `observed_frame: F+1`; a log reports the frame it was logged in; a delay
// reports the frame it elapsed at. `play_time` is that frame's Time.time.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityOpenMcpBridge.ObjectRefs;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Playtest
{
    // Registered by the uGUI sub-assembly (com.unity.ugui present): the `ui`
    // condition runs the same screen model ui_snapshot reports.
    internal interface IUiConditionProbe
    {
        // Count the nodes whose display text contains `textContains` (normalised,
        // case-insensitive; null = any) and whose path equals `path` or ends with
        // "/" + path (null = any). visible: true counts on-screen nodes only,
        // false counts nodes that exist but are not on screen. `firstMatchJson`
        // describes the first counted node ({path, text, visibility}) or is null.
        int Count(string textContains, string path, bool visible, out string firstMatchJson);
    }

    internal static class PlaytestProbes
    {
        internal static IUiConditionProbe Ui;

        internal const string UiUnavailableMessage =
            "The ui condition needs uGUI (com.unity.ugui): the screen model is compiled with it. " +
            "Use an object, log or project_command condition instead.";
    }

    // One scheduler tick as conditions see it.
    internal struct ConditionTick
    {
        public int Frame;
        public int TickIndex;
        public double ElapsedMs;
        public double RealtimeMs;
        public double PlayTime;
    }

    // When a condition turned true.
    internal struct ConditionFire
    {
        public bool Set;
        public int Frame;           // the frame that made it true
        public int ObservedFrame;   // the frame at whose start the scheduler saw it
        public double PlayTime;     // Time.time of Frame (NaN when unknown)
        public double ElapsedMs;    // operation elapsed at Frame
        public double RealtimeMs;
        public bool AtStart;        // already true at the first poll

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"frame\":").Append(Frame);
            sb.Append(",\"observed_frame\":").Append(ObservedFrame);
            sb.Append(",\"play_time\":").Append(double.IsNaN(PlayTime) ? "null" : PlaytestJson.Num(PlayTime, 4));
            sb.Append(",\"elapsed_ms\":").Append(PlaytestJson.Num(ElapsedMs));
            sb.Append(",\"realtime_ms\":").Append(PlaytestJson.Num(RealtimeMs));
            if (AtStart) sb.Append(",\"at_start\":true");
            sb.Append('}');
        }
    }

    // A console message captured while a condition is armed.
    internal sealed class LoggedMessage
    {
        public string Message;
        public string Stack;
        public LogType Type;
        public int Frame;
        public double PlayTime;
    }

    // Per-poll inputs: this tick, the previous tick (the frame whose updates the
    // state reflects), messages logged since the last poll, and a lookup of the
    // operation's elapsed time at an earlier frame (for logs).
    internal sealed class ConditionPoll
    {
        public ConditionTick Now;
        public ConditionTick? Previous;
        public List<LoggedMessage> NewLogs = new List<LoggedMessage>();
        public Func<int, ConditionTick?> TickAt;

        // Fire record for a state that turned true between the previous poll and this one.
        internal ConditionFire StateFire()
        {
            if (Previous.HasValue)
            {
                var p = Previous.Value;
                return new ConditionFire { Set = true, Frame = p.Frame, ObservedFrame = Now.Frame, PlayTime = p.PlayTime, ElapsedMs = p.ElapsedMs, RealtimeMs = p.RealtimeMs };
            }
            return new ConditionFire { Set = true, Frame = Now.Frame, ObservedFrame = Now.Frame, PlayTime = Now.PlayTime, ElapsedMs = Now.ElapsedMs, RealtimeMs = Now.RealtimeMs, AtStart = true };
        }

        internal ConditionFire NowFire()
            => new ConditionFire { Set = true, Frame = Now.Frame, ObservedFrame = Now.Frame, PlayTime = Now.PlayTime, ElapsedMs = Now.ElapsedMs, RealtimeMs = Now.RealtimeMs };
    }

    internal abstract class WaitCondition
    {
        public string Type;
        public string Path;          // "condition", "condition.any[1]", "start_on"
        public bool Met;
        public ConditionFire Fire;
        public string Error;         // a leaf that cannot be evaluated (project command threw)

        // Evaluate at a poll; updates Met / Fire.
        internal abstract void Evaluate(ConditionPoll poll);
        // Last observed state, a JSON object.
        internal abstract void AppendState(StringBuilder sb);
        // True when this tree needs console messages.
        internal virtual bool WantsLogs => false;

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"path\":").Append(BridgeJson.EscapeString(Path));
            sb.Append(",\"type\":").Append(BridgeJson.EscapeString(Type));
            sb.Append(",\"met\":").Append(Met ? "true" : "false");
            if (Fire.Set) { sb.Append(",\"fired\":"); Fire.AppendJson(sb); }
            if (Error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(Error));
            sb.Append(",\"last\":");
            AppendState(sb);
            sb.Append('}');
        }

        // Leaves in evaluation order (a leaf returns itself).
        internal virtual IEnumerable<WaitCondition> Leaves() { yield return this; }

        protected void SetState(bool truth, ConditionPoll poll)
        {
            if (truth && !Met) Fire = poll.StateFire();
            if (!truth) Fire = default;
            Met = truth;
        }
    }

    internal sealed class LogCondition : WaitCondition
    {
        private readonly Regex pattern;
        private readonly string source;
        private readonly string level;
        private int matches;
        private LoggedMessage first;

        internal LogCondition(Regex pattern, string source, string level)
        {
            Type = "log";
            this.pattern = pattern;
            this.source = source;
            this.level = level;
        }

        internal override bool WantsLogs => true;

        internal static bool LevelMatches(string level, LogType type)
        {
            switch (level)
            {
                case "log": return type == LogType.Log;
                case "warning": return type == LogType.Warning;
                case "error": return type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
                case "exception": return type == LogType.Exception;
                default: return true;
            }
        }

        internal override void Evaluate(ConditionPoll poll)
        {
            foreach (var log in poll.NewLogs)
            {
                if (!LevelMatches(level, log.Type)) continue;
                bool hit;
                try { hit = pattern.IsMatch(log.Message ?? ""); }
                catch (RegexMatchTimeoutException) { hit = false; }
                if (!hit) continue;
                matches++;
                if (first != null) continue;
                first = log;
                var at = poll.TickAt?.Invoke(log.Frame);
                Fire = new ConditionFire
                {
                    Set = true,
                    Frame = log.Frame,
                    ObservedFrame = poll.Now.Frame,
                    PlayTime = log.PlayTime,
                    ElapsedMs = at?.ElapsedMs ?? poll.Now.ElapsedMs,
                    RealtimeMs = at?.RealtimeMs ?? poll.Now.RealtimeMs,
                };
                Met = true;
            }
        }

        internal override void AppendState(StringBuilder sb)
        {
            sb.Append("{\"pattern\":").Append(BridgeJson.EscapeString(source));
            sb.Append(",\"level\":").Append(BridgeJson.EscapeString(level));
            sb.Append(",\"matches\":").Append(matches);
            if (first != null)
            {
                sb.Append(",\"message\":").Append(BridgeJson.EscapeString(PlaytestJson.Clip(first.Message, 300)));
                sb.Append(",\"log_type\":").Append(BridgeJson.EscapeString(first.Type.ToString()));
            }
            sb.Append('}');
        }
    }

    internal sealed class UiCondition : WaitCondition
    {
        private readonly string textContains, path;
        private readonly bool visible, wantMatch;
        private int count = -1;
        private string firstJson;

        internal UiCondition(string textContains, string path, bool visible, bool wantMatch)
        {
            Type = "ui";
            this.textContains = textContains;
            this.path = path;
            this.visible = visible;
            this.wantMatch = wantMatch;
        }

        internal override void Evaluate(ConditionPoll poll)
        {
            var probe = PlaytestProbes.Ui;
            if (probe == null) { Error = PlaytestProbes.UiUnavailableMessage; return; }
            count = probe.Count(textContains, path, visible, out firstJson);
            SetState(wantMatch ? count > 0 : count == 0, poll);
        }

        internal override void AppendState(StringBuilder sb)
        {
            sb.Append("{\"query\":{");
            bool any = false;
            if (textContains != null) { sb.Append("\"text_contains\":").Append(BridgeJson.EscapeString(textContains)); any = true; }
            if (path != null) { if (any) sb.Append(','); sb.Append("\"path\":").Append(BridgeJson.EscapeString(path)); any = true; }
            if (any) sb.Append(',');
            sb.Append("\"visible\":").Append(visible ? "true" : "false");
            sb.Append(",\"matches\":").Append(wantMatch ? "true" : "false").Append('}');
            sb.Append(",\"count\":").Append(count < 0 ? "null" : count.ToString(CultureInfo.InvariantCulture));
            if (firstJson != null) sb.Append(",\"first\":").Append(firstJson);
            sb.Append('}');
        }
    }

    internal sealed class ObjectCondition : WaitCondition
    {
        internal static readonly string[] States = { "exists", "active", "inactive", "destroyed" };
        private readonly string path;
        private readonly long objectId;
        private readonly string state;
        private GameObject tracked;
        private bool everFound, found, active;
        private string foundPath;

        internal ObjectCondition(string path, long objectId, string state)
        {
            Type = "object";
            this.path = path;
            this.objectId = objectId;
            this.state = state;
        }

        internal override void Evaluate(ConditionPoll poll)
        {
            // Track the instance once found: `destroyed` then means that object
            // was destroyed (Unity null), not that the path names nothing. The
            // other states look the path up again after the tracked object is
            // gone (a respawned object counts).
            bool lost = everFound && tracked == null;
            if (tracked == null && !(lost && (state == "destroyed" || objectId != 0)))
            {
                tracked = objectId != 0 ? InstanceId.ToObject(objectId) as GameObject : PlaytestObjects.Find(path);
                if (tracked != null) { everFound = true; foundPath = PlaytestObjects.PathOf(tracked); }
            }
            found = tracked != null;
            active = found && tracked.activeInHierarchy;
            bool truth;
            switch (state)
            {
                case "active": truth = found && active; break;
                case "inactive": truth = found && !active; break;
                case "destroyed": truth = !found; break;
                default: truth = found; break;
            }
            SetState(truth, poll);
        }

        internal override void AppendState(StringBuilder sb)
        {
            sb.Append("{\"state\":").Append(BridgeJson.EscapeString(state));
            if (path != null) sb.Append(",\"path\":").Append(BridgeJson.EscapeString(path));
            if (objectId != 0) sb.Append(",\"object_id\":").Append(BridgeJson.EscapeString(objectId.ToString(CultureInfo.InvariantCulture)));
            sb.Append(",\"found\":").Append(found ? "true" : "false");
            sb.Append(",\"active\":").Append(active ? "true" : "false");
            sb.Append(",\"ever_found\":").Append(everFound ? "true" : "false");
            if (foundPath != null) sb.Append(",\"resolved_path\":").Append(BridgeJson.EscapeString(foundPath));
            sb.Append('}');
        }
    }

    internal sealed class ProjectCommandCondition : WaitCondition
    {
        private readonly string id;
        private readonly string field;
        private readonly ProjectCommandCatalog.Entry entry;
        private readonly object[] values;
        private string lastOutput;
        private int calls;

        internal ProjectCommandCondition(string id, string field, ProjectCommandCatalog.Entry entry, object[] values)
        {
            Type = "project_command";
            this.id = id;
            this.field = field;
            this.entry = entry;
            this.values = values;
        }

        internal override void Evaluate(ConditionPoll poll)
        {
            string raw;
            try
            {
                calls++;
                raw = (string)entry.Method.Invoke(null, values);
            }
            catch (Exception ex)
            {
                var cause = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                Error = "Project command " + id + " threw " + cause.GetType().Name + ": " + cause.Message;
                return;
            }
            lastOutput = raw;
            SetState(IsTrue(raw, field), poll);
        }

        // True when the output is JSON `true`, or (with `field`) a JSON object
        // whose top-level `field` is `true`.
        internal static bool IsTrue(string output, string field)
        {
            if (output == null) return false;
            var trimmed = output.Trim();
            if (string.IsNullOrEmpty(field)) return trimmed == "true";
            if (!trimmed.StartsWith("{")) return false;
            var value = JsonBody.GetTopLevelRawValue(trimmed, field);
            return value != null && value.Trim() == "true";
        }

        internal override void AppendState(StringBuilder sb)
        {
            sb.Append("{\"id\":").Append(BridgeJson.EscapeString(id));
            if (field != null) sb.Append(",\"field\":").Append(BridgeJson.EscapeString(field));
            sb.Append(",\"calls\":").Append(calls);
            sb.Append(",\"output\":").Append(lastOutput == null ? "null" : BridgeJson.EscapeString(PlaytestJson.Clip(lastOutput.Trim(), 300)));
            sb.Append('}');
        }
    }

    internal sealed class FramesCondition : WaitCondition
    {
        private readonly int frames;
        private int seen;

        internal FramesCondition(int frames) { Type = "frames"; this.frames = frames; }

        internal override void Evaluate(ConditionPoll poll)
        {
            seen = poll.Now.TickIndex;
            if (!Met && seen >= frames) { Met = true; Fire = poll.NowFire(); }
        }

        internal override void AppendState(StringBuilder sb)
            => sb.Append("{\"frames\":").Append(frames).Append(",\"elapsed_frames\":").Append(seen).Append('}');
    }

    internal sealed class MsCondition : WaitCondition
    {
        private readonly double ms;
        private double elapsed;

        internal MsCondition(double ms) { Type = "ms"; this.ms = ms; }

        internal override void Evaluate(ConditionPoll poll)
        {
            elapsed = poll.Now.ElapsedMs;
            if (!Met && elapsed >= ms) { Met = true; Fire = poll.NowFire(); }
        }

        internal override void AppendState(StringBuilder sb)
            => sb.Append("{\"ms\":").Append(PlaytestJson.Num(ms)).Append(",\"elapsed_ms\":").Append(PlaytestJson.Num(elapsed)).Append('}');
    }

    internal sealed class CompositeCondition : WaitCondition
    {
        internal readonly List<WaitCondition> Children = new List<WaitCondition>();
        private readonly bool all;

        internal CompositeCondition(bool all) { this.all = all; Type = all ? "all" : "any"; }

        internal override bool WantsLogs
        {
            get { foreach (var c in Children) if (c.WantsLogs) return true; return false; }
        }

        internal override IEnumerable<WaitCondition> Leaves()
        {
            foreach (var c in Children)
                foreach (var leaf in c.Leaves()) yield return leaf;
        }

        // The child that fired an `any` (lowest index), or null.
        internal WaitCondition FiredChild;

        internal override void Evaluate(ConditionPoll poll)
        {
            foreach (var c in Children)
            {
                c.Evaluate(poll);
                if (c.Error != null && Error == null) Error = c.Error;
            }
            bool truth = all;
            WaitCondition firedBy = null;
            foreach (var c in Children)
            {
                if (all && !c.Met) { truth = false; break; }
                if (!all && c.Met) { truth = true; firedBy = c; break; }
            }
            if (truth && !Met)
            {
                if (all)
                {
                    // The leaf that completed the set: the latest fire among the children.
                    var latest = default(ConditionFire);
                    foreach (var c in Children)
                        if (c.Fire.Set && (!latest.Set || c.Fire.ObservedFrame > latest.ObservedFrame ||
                                           (c.Fire.ObservedFrame == latest.ObservedFrame && c.Fire.Frame > latest.Frame)))
                            latest = c.Fire;
                    Fire = latest.Set ? latest : poll.StateFire();
                }
                else
                {
                    FiredChild = firedBy;
                    Fire = firedBy.Fire.Set ? firedBy.Fire : poll.NowFire();
                }
            }
            if (!truth) { Fire = default; FiredChild = null; }
            Met = truth;
        }

        internal override void AppendState(StringBuilder sb)
        {
            sb.Append("{\"children\":[");
            for (int i = 0; i < Children.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Children[i].AppendJson(sb);
            }
            sb.Append("]}");
        }
    }

    // Parsing (pure: no scene access except the project-command preflight).
    internal static class WaitConditionParser
    {
        internal const int MaxChildren = 16;
        internal const int MaxFrames = 1_000_000;
        internal const double MaxMs = 3_600_000;
        internal static readonly string[] LeafKeys = { "log", "ui", "object", "project_command", "frames", "ms" };
        private static readonly string[] Levels = { "any", "log", "warning", "error", "exception" };

        internal static WaitCondition Parse(string raw, string path, out string error)
            => Parse(raw, path, true, out error);

        private static WaitCondition Parse(string raw, string path, bool allowComposite, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw) || !BridgeJson.IsValidJsonObject(raw))
            {
                error = path + " must be a JSON object with one key: " + string.Join(", ", LeafKeys) + (allowComposite ? ", all, any" : "") + ".";
                return null;
            }
            var keys = JsonBody.GetObjectKeys(raw) ?? new List<string>();
            if (keys.Count != 1)
            {
                error = path + " must have exactly one key (" + string.Join(", ", LeafKeys) + (allowComposite ? ", all, any" : "") + "); got " + keys.Count + ".";
                return null;
            }
            var key = keys[0];
            var value = JsonBody.GetTopLevelRawValue(raw, key)?.Trim();
            WaitCondition result;
            switch (key)
            {
                case "all":
                case "any":
                    if (!allowComposite) { error = path + ": all/any nest one level only (a list of leaf conditions)."; return null; }
                    result = ParseComposite(key == "all", value, path + "." + key, out error);
                    break;
                case "log": result = ParseLog(value, path + ".log", out error); break;
                case "ui": result = ParseUi(value, path + ".ui", out error); break;
                case "object": result = ParseObject(value, path + ".object", out error); break;
                case "project_command": result = ParseProjectCommand(value, path + ".project_command", out error); break;
                case "frames":
                    if (!TryInt(value, out var frames) || frames < 0 || frames > MaxFrames)
                    { error = path + ".frames must be an integer 0.." + MaxFrames + "."; return null; }
                    result = new FramesCondition(frames);
                    break;
                case "ms":
                    if (!TryNumber(value, out var ms) || ms < 0 || ms > MaxMs)
                    { error = path + ".ms must be a number 0.." + MaxMs + "."; return null; }
                    result = new MsCondition(ms);
                    break;
                default:
                    error = path + ": unknown condition '" + key + "'. Use " + string.Join(", ", LeafKeys) + (allowComposite ? ", all or any" : "") + ".";
                    return null;
            }
            if (result == null) return null;
            if (result.Path == null) result.Path = path;
            return result;
        }

        private static WaitCondition ParseComposite(bool all, string value, string path, out string error)
        {
            error = null;
            if (value == null || !value.StartsWith("["))
            {
                error = path + " must be an array of conditions.";
                return null;
            }
            var items = JsonBody.GetArrayRawValues(value);
            if (items.Count < 1 || items.Count > MaxChildren)
            {
                error = path + " must hold 1.." + MaxChildren + " conditions.";
                return null;
            }
            var composite = new CompositeCondition(all) { Path = path };
            for (int i = 0; i < items.Count; i++)
            {
                var child = Parse(items[i], path + "[" + i + "]", false, out error);
                if (child == null) return null;
                composite.Children.Add(child);
            }
            return composite;
        }

        private static bool CheckKeys(string value, string path, string[] allowed, out string error)
        {
            error = null;
            if (value == null || !BridgeJson.IsValidJsonObject(value))
            {
                error = path + " must be an object.";
                return false;
            }
            foreach (var k in JsonBody.GetObjectKeys(value) ?? new List<string>())
                if (Array.IndexOf(allowed, k) < 0)
                {
                    error = path + "." + k + " is unknown; use " + string.Join(", ", allowed) + ".";
                    return false;
                }
            return true;
        }

        private static WaitCondition ParseLog(string value, string path, out string error)
        {
            if (!CheckKeys(value, path, new[] { "pattern", "level" }, out error)) return null;
            var pattern = Str(value, "pattern");
            if (string.IsNullOrEmpty(pattern)) { error = path + ".pattern (a regex) is required."; return null; }
            var level = Str(value, "level") ?? "any";
            if (Array.IndexOf(Levels, level) < 0) { error = path + ".level must be " + string.Join(", ", Levels) + "."; return null; }
            Regex rx;
            try { rx = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException ex) { error = path + ".pattern is not a valid regex: " + ex.Message; return null; }
            return new LogCondition(rx, pattern, level);
        }

        private static WaitCondition ParseUi(string value, string path, out string error)
        {
            if (!CheckKeys(value, path, new[] { "text_contains", "path", "visible", "matches" }, out error)) return null;
            var text = Str(value, "text_contains");
            var nodePath = Str(value, "path");
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(nodePath))
            {
                error = path + " needs text_contains and/or path.";
                return null;
            }
            if (!TryBool(value, "visible", true, out var visible) || !TryBool(value, "matches", true, out var matches))
            {
                error = path + ".visible / .matches must be booleans.";
                return null;
            }
            if (PlaytestProbes.Ui == null) { error = "ui_unavailable:" + PlaytestProbes.UiUnavailableMessage; return null; }
            return new UiCondition(string.IsNullOrEmpty(text) ? null : text, string.IsNullOrEmpty(nodePath) ? null : nodePath.Trim('/'), visible, matches);
        }

        private static WaitCondition ParseObject(string value, string path, out string error)
        {
            if (!CheckKeys(value, path, new[] { "path", "object_id", "state" }, out error)) return null;
            var objPath = Str(value, "path");
            long id = 0;
            var idRaw = JsonBody.GetTopLevelRawValue(value, "object_id");
            if (idRaw != null && idRaw.Trim() != "null")
            {
                id = InstanceId.Parse(idRaw.Trim().Trim('"'));
                if (id == 0) { error = path + ".object_id must be a non-zero instance id (number or numeric string)."; return null; }
            }
            if (string.IsNullOrEmpty(objPath) == (id == 0))
            {
                error = path + " needs exactly one of path or object_id.";
                return null;
            }
            var state = Str(value, "state") ?? "exists";
            if (Array.IndexOf(ObjectCondition.States, state) < 0)
            {
                error = path + ".state must be " + string.Join(", ", ObjectCondition.States) + ".";
                return null;
            }
            return new ObjectCondition(objPath, id, state);
        }

        private static WaitCondition ParseProjectCommand(string value, string path, out string error)
        {
            if (!CheckKeys(value, path, new[] { "id", "args", "field" }, out error)) return null;
            var id = Str(value, "id");
            if (string.IsNullOrEmpty(id)) { error = path + ".id (a project command id) is required."; return null; }
            var args = JsonBody.GetTopLevelRawValue(value, "args")?.Trim() ?? "{}";
            var field = Str(value, "field");
            if (!ProjectCommandCatalog.TryGet(id, out var catalogEntry))
            {
                error = "command_unavailable:" + path + ": unknown, disabled or conflicting project command '" + id + "'. List them with unity_open_mcp_project_commands.";
                return null;
            }
            if (catalogEntry.Attribute.IsMutating || catalogEntry.Attribute.Async)
            {
                error = "command_not_allowed:" + path + ": '" + id + "' must be a synchronous, read-only project command (IsMutating = false, Async = false) to be polled as a condition.";
                return null;
            }
            var body = "{\"action\":\"invoke\",\"command_id\":" + BridgeJson.EscapeString(id) + ",\"args\":" + args + "}";
            var refusal = ProjectCommandInvocation.Preflight(body, out var entry, out var values);
            if (refusal != null)
            {
                error = (refusal.ErrorCode ?? "invalid_arguments") + ":" + path + ": " + (refusal.ErrorMessage ?? "project command preflight refused.");
                return null;
            }
            return new ProjectCommandCondition(id, string.IsNullOrEmpty(field) ? null : field, entry, values);
        }

        // ---- raw JSON value helpers --------------------------------------

        internal static string Str(string obj, string key)
        {
            var raw = JsonBody.GetTopLevelRawValue(obj, key);
            if (raw == null) return null;
            raw = raw.Trim();
            if (raw == "null") return null;
            return raw.StartsWith("\"") ? JsonBody.GetString("{\"v\":" + raw + "}", "v") : raw;
        }

        internal static bool TryBool(string obj, string key, bool fallback, out bool value)
        {
            value = fallback;
            var raw = JsonBody.GetTopLevelRawValue(obj, key)?.Trim();
            if (raw == null || raw == "null") return true;
            if (raw == "true") { value = true; return true; }
            if (raw == "false") { value = false; return true; }
            return false;
        }

        internal static bool TryNumber(string raw, out double value)
        {
            value = 0;
            if (raw == null) return false;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool TryInt(string raw, out int value)
        {
            value = 0;
            if (!TryNumber(raw, out var d) || Math.Abs(d - Math.Round(d)) > 1e-9 || d < int.MinValue || d > int.MaxValue) return false;
            value = (int)Math.Round(d);
            return true;
        }

        // A parse error string may carry a stable code prefix ("code:message").
        internal static string SplitCode(string error, out string message)
        {
            message = error;
            if (error == null) return "invalid_arguments";
            int colon = error.IndexOf(':');
            if (colon > 0 && colon < 40 && error.Substring(0, colon).IndexOf(' ') < 0 && error.Substring(0, colon).IndexOf('.') < 0)
            {
                message = error.Substring(colon + 1);
                return error.Substring(0, colon);
            }
            return "invalid_arguments";
        }
    }

    // GameObject lookup for the object condition: a hierarchy path (plain names,
    // "/"-separated) or a bare name, in every loaded scene plus the
    // DontDestroyOnLoad scene in play mode, inactive objects included.
    internal static class PlaytestObjects
    {
        internal static GameObject Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segments = path.Trim('/').Split('/');
            foreach (var root in Roots())
            {
                if (segments.Length == 1)
                {
                    var byName = FindByName(root.transform, segments[0]);
                    if (byName != null) return byName;
                    continue;
                }
                if (root.name != segments[0]) continue;
                var walked = Walk(root.transform, segments, 1);
                if (walked != null) return walked;
            }
            return null;
        }

        private static IEnumerable<GameObject> Roots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) yield return root;
            }
            if (!EditorApplication.isPlaying) yield break;
            var ddol = DontDestroyOnLoadScene();
            if (ddol.IsValid() && ddol.isLoaded)
                foreach (var root in ddol.GetRootGameObjects()) yield return root;
        }

        private static Scene cachedDdol;
        private static int cachedDdolFrame = -1;

        // The DontDestroyOnLoad scene is not listed by SceneManager; it is the
        // scene of any object moved there. Cached per frame.
        private static Scene DontDestroyOnLoadScene()
        {
            if (cachedDdolFrame == Time.frameCount) return cachedDdol;
            cachedDdolFrame = Time.frameCount;
            cachedDdol = default;
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.parent != null) continue;
                var scene = t.gameObject.scene;
                if (scene.IsValid() && scene.name == "DontDestroyOnLoad") { cachedDdol = scene; break; }
            }
            return cachedDdol;
        }

        private static GameObject Walk(Transform current, string[] segments, int index)
        {
            if (index >= segments.Length) return current.gameObject;
            foreach (Transform child in current)
            {
                if (child.name != segments[index]) continue;
                var found = Walk(child, segments, index + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindByName(Transform t, string name)
        {
            if (t.name == name) return t.gameObject;
            foreach (Transform child in t)
            {
                var found = FindByName(child, name);
                if (found != null) return found;
            }
            return null;
        }

        internal static string PathOf(GameObject go)
        {
            if (go == null) return null;
            var sb = new StringBuilder(go.name);
            var t = go.transform.parent;
            while (t != null)
            {
                sb.Insert(0, t.name + "/");
                t = t.parent;
            }
            return sb.ToString();
        }
    }

    internal static class PlaytestJson
    {
        internal static string Num(double v, int digits = 2)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return Math.Round(v, digits).ToString(digits >= 4 ? "0.####" : "0.##", CultureInfo.InvariantCulture);
        }

        internal static string Clip(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        internal static string Error(string code, string message, string extra = null)
            => PlayModeScheduler.ErrorJson(code, message, extra);
    }
}
