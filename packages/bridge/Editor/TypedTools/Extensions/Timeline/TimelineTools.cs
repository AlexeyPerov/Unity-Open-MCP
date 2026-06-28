// M20 Plan 6 / T20.6.2 — Timeline embedded domain tools (compile-gated).
//
// Five typed tools for in-editor cutscene / sequence authoring:
//   - create: TimelineAsset (.playable) asset creation
//   - track_add: Animation / Activation / Audio / Signal / Control / Group track
//   - clip_add: add a clip to a track (animation / audio / activation / default)
//   - director_bind: bind a TimelineAsset to a scene PlayableDirector
//   - modify: reflective field-patch escape hatch for niche Timeline fields
//
// Track-type enum handling mirrors the upstream Unity-AI-Timeline reference
// pack's Helpers.cs (Animation / Activation / Audio / Signal / Control + Group
// / Playable).
//
// Tools that mutate a scene GameObject (director_bind) run the gate path with
// paths_hint scoped to the host's scene path. timeline_create produces a
// .playable asset — its paths_hint includes the asset path. timeline_modify
// touches either an asset or a scene object depending on what it targets.
//
// Compile-gate-only: com.unity.timeline has a single stable public API across
// 1.x (the reference pack wraps 1.8.12), so there is no reflection / version-
// detection layer. When the package is absent the tools are not compiled in
// and the capability surface reports the domain as `available: false
// (dependency missing: com.unity.timeline)`.
//
// Naming: `unity_open_mcp_timeline_<action>` (snake_case domain prefix — mirrors
// the kebab `timeline-*` ids in the upstream Unity-AI-Timeline reference pack).
// Reference: IvanMurzak/Unity-AI-Timeline (MIT).
#if UNITY_OPEN_MCP_EXT_TIMELINE
#pragma warning disable CS0618
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.Timeline
{
    [BridgeToolType]
    public static class TimelineTools
    {
        // =====================================================================
        // create
        // =====================================================================

        // Create a new empty TimelineAsset (.playable) at the given asset path.
        // The asset path is required — TimelineAsset is a project asset, not a
        // scene object. paths_hint includes the asset path.
        [BridgeTool("unity_open_mcp_timeline_create",
            Title = "Timeline: Create",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "timeline")]
        [System.ComponentModel.Description(
            "Create a new empty TimelineAsset (.playable) at the given asset " +
            "path. The asset_path is required ('Assets/.../Cutscene.playable'); " +
            "the parent folder must already exist. Mutating: runs the gate path; " +
            "paths_hint includes the new asset path. Requires the " +
            "com.unity.timeline package installed in the project.")]
        public static string Create(
            string asset_path = null,
            string frame_rate = null,
            int instance_id = 0,
            string path = null,
            string name = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TimelineJson.Error("paths_hint_required",
                    "timeline_create is mutating; pass a non-empty paths_hint " +
                    "that includes the new asset path.");

            if (string.IsNullOrEmpty(asset_path))
                return TimelineJson.Error("missing_parameter",
                    "'asset_path' is required (an 'Assets/.../*.playable' path).");

            if (!asset_path.EndsWith(".playable"))
                return TimelineJson.Error("invalid_parameter",
                    "'asset_path' must end with '.playable'.");

            if (AssetDatabase.LoadAssetAtPath<TimelineAsset>(asset_path) != null)
                return TimelineJson.Error("already_exists",
                    $"A TimelineAsset already exists at '{asset_path}'. " +
                    "Use a different path or modify the existing asset.");

            var asset = ScriptableObject.CreateInstance<TimelineAsset>();
            if (!string.IsNullOrEmpty(frame_rate) &&
                double.TryParse(frame_rate, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var fps) && fps > 0)
            {
                asset.editorSettings.frameRate = fps;
            }

            AssetDatabase.CreateAsset(asset, asset_path);
            EditorUtility.SetDirty(asset);

            var sb = new StringBuilder(160);
            sb.Append("\"timeline\":{");
            sb.Append("\"assetPath\":").Append(TimelineJson.Esc(asset_path)).Append(',');
            sb.Append("\"instanceId\":").Append(asset.GetInstanceID()).Append(',');
            sb.Append("\"frameRate\":").Append(asset.editorSettings.frameRate).Append(',');
            sb.Append("\"trackCount\":0");
            sb.Append('}');
            return TimelineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // track_add
        // =====================================================================

        // Add a track of the requested type to a TimelineAsset. Track types:
        // Animation / Activation / Audio / Signal / Control / Group / Playable.
        // Address the timeline by asset_path (preferred) or instance_id. An
        // optional parent_track_index nests the new track under a Group track.
        [BridgeTool("unity_open_mcp_timeline_track_add",
            Title = "Timeline: Add Track",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "timeline")]
        [System.ComponentModel.Description(
            "Add a track of the requested type to a TimelineAsset. track_type " +
            "is one of: Animation, Activation, Audio, Signal, Control, Group, " +
            "Playable. Address the timeline by asset_path (preferred) or " +
            "instance_id. parent_track_index optionally nests the new track " +
            "under an existing Group track. Returns the new track's index. " +
            "Mutating: runs the gate path; paths_hint is the timeline asset path.")]
        public static string TrackAdd(
            string asset_path = null,
            int instance_id = 0,
            string track_type = null,
            string track_name = null,
            int parent_track_index = -1,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TimelineJson.Error("paths_hint_required",
                    "timeline_track_add is mutating; pass a non-empty paths_hint " +
                    "(the timeline asset path).");

            if (string.IsNullOrEmpty(track_type))
                return TimelineJson.Error("missing_parameter",
                    "'track_type' is required (Animation / Activation / Audio / " +
                    "Signal / Control / Group / Playable).");

            var asset = ResolveTimeline(asset_path, instance_id);
            if (asset == null) return TimelineAssetNotFound();

            if (!TryResolveTrackType(track_type, out var trackSystemType, out var typeErr))
                return TimelineJson.Error("invalid_track_type", typeErr);

            TrackAsset parent = null;
            if (parent_track_index >= 0)
            {
                var roots = asset.GetRootTracks();
                if (parent_track_index >= roots.Count)
                    return TimelineJson.Error("track_not_found",
                        $"parent_track_index {parent_track_index} out of range " +
                        $"(timeline has {roots.Count} root track(s)).");
                parent = roots[parent_track_index];
                if (!(parent is GroupTrack))
                    return TimelineJson.Error("invalid_parent",
                        "parent_track_index must reference a Group track to nest under.");
            }

            Undo.RecordObject(asset, "Add Timeline track");
            var track = asset.CreateTrack(trackSystemType, parent, track_name);
            EditorUtility.SetDirty(asset);

            int index = -1;
            var allRoots = asset.GetRootTracks();
            for (int i = 0; i < allRoots.Count; i++)
                if (allRoots[i] == track || (parent != null && parent == allRoots[i])) { index = i; break; }

            var sb = new StringBuilder(160);
            sb.Append("\"track\":{");
            sb.Append("\"added\":true,");
            sb.Append("\"type\":").Append(TimelineJson.Esc(trackSystemType.Name)).Append(',');
            sb.Append("\"name\":").Append(TimelineJson.Esc(track != null ? track.name : "")).Append(',');
            sb.Append("\"rootIndex\":").Append(index);
            sb.Append('}');
            return TimelineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // clip_add
        // =====================================================================

        // Add a clip to a track. Address the timeline by asset_path /
        // instance_id, the track by index or name (name first match). clip_type
        // selects the clip kind when the track is generic; on a typed track the
        // clip kind follows the track (AnimationTrack → AnimationClip,
        // AudioTrack → Audio, ActivationTrack → Activation). start_time /
        // duration are optional (in seconds).
        [BridgeTool("unity_open_mcp_timeline_clip_add",
            Title = "Timeline: Add Clip",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "timeline")]
        [System.ComponentModel.Description(
            "Add a clip to a Timeline track. Address the timeline by asset_path " +
            "(preferred) or instance_id, the track by track_index or track_name " +
            "(name first match). start_time and duration are optional (seconds). " +
            "On a typed track (Animation / Activation / Audio) the clip kind " +
            "follows the track; on a Playable track, set clip_type to " +
            "'animation' / 'audio' / 'activation' / 'default'. Returns the new " +
            "clip's index + start. Mutating: runs the gate path; paths_hint is " +
            "the timeline asset path.")]
        public static string ClipAdd(
            string asset_path = null,
            int instance_id = 0,
            int track_index = -1,
            string track_name = null,
            string clip_type = null,
            string clip_name = null,
            double start_time = -1d,
            double duration = -1d,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TimelineJson.Error("paths_hint_required",
                    "timeline_clip_add is mutating; pass a non-empty paths_hint " +
                    "(the timeline asset path).");

            var asset = ResolveTimeline(asset_path, instance_id);
            if (asset == null) return TimelineAssetNotFound();

            var track = FindTrack(asset, track_index, track_name);
            if (track == null)
                return TimelineJson.Error("track_not_found",
                    "No track resolved. Provide track_index or track_name " +
                    "(track_name matches the first track by name).");

            Undo.RecordObject(asset, "Add Timeline clip");
            TimelineClip clip;
            // Typed tracks ship a default clip kind; on a generic PlayableTrack
            // the agent may request a specific kind via clip_type.
            if (track is AnimationTrack)
                clip = track.CreateClip<AnimationPlayableAsset>();
            else if (track is AudioTrack)
                clip = track.CreateClip<AudioPlayableAsset>();
            else if (track is ActivationTrack)
                clip = track.CreateClip<ActivationPlayableAsset>();
            else if (!string.IsNullOrEmpty(clip_type) &&
                     string.Equals(clip_type, "animation", System.StringComparison.OrdinalIgnoreCase))
                clip = track.CreateClip<AnimationPlayableAsset>();
            else if (!string.IsNullOrEmpty(clip_type) &&
                     string.Equals(clip_type, "audio", System.StringComparison.OrdinalIgnoreCase))
                clip = track.CreateClip<AudioPlayableAsset>();
            else if (!string.IsNullOrEmpty(clip_type) &&
                     string.Equals(clip_type, "activation", System.StringComparison.OrdinalIgnoreCase))
                clip = track.CreateClip<ActivationPlayableAsset>();
            else
                clip = track.CreateDefaultClip();

            if (clip != null)
            {
                if (!string.IsNullOrEmpty(clip_name)) clip.displayName = clip_name;
                if (start_time >= 0d) clip.start = start_time;
                if (duration > 0d) clip.duration = duration;
            }

            EditorUtility.SetDirty(asset);

            var sb = new StringBuilder(180);
            sb.Append("\"clip\":{");
            sb.Append("\"added\":true,");
            sb.Append("\"assetType\":").Append(TimelineJson.Esc(clip != null && clip.asset != null ? clip.asset.GetType().Name : "")).Append(',');
            sb.Append("\"displayName\":").Append(TimelineJson.Esc(clip != null ? clip.displayName : "")).Append(',');
            sb.Append("\"start\":").Append(clip != null ? clip.start : 0d).Append(',');
            sb.Append("\"duration\":").Append(clip != null ? clip.duration : 0d);
            sb.Append('}');
            return TimelineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // director_bind
        // =====================================================================

        // Bind a TimelineAsset to a scene PlayableDirector (adds the component
        // when missing, then assigns the asset). Scene mutation — paths_hint is
        // the host scene path.
        [BridgeTool("unity_open_mcp_timeline_director_bind",
            Title = "Timeline: Bind Director",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "timeline")]
        [System.ComponentModel.Description(
            "Bind a TimelineAsset to a scene PlayableDirector. Adds the " +
            "PlayableDirector component when missing, then assigns the asset. " +
            "Address the host GameObject by instance_id > path > name and the " +
            "TimelineAsset by asset_path (preferred) or instance_id. Mutating: " +
            "runs the gate path; paths_hint is the host scene path + the asset " +
            "path. Requires com.unity.timeline.")]
        public static string DirectorBind(
            int instance_id = 0,
            string path = null,
            string name = null,
            string asset_path = null,
            int asset_instance_id = 0,
            bool autoplay = false,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TimelineJson.Error("paths_hint_required",
                    "timeline_director_bind is mutating; pass a non-empty paths_hint.");

            var host = TimelineTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TimelineJson.Error("target_not_found",
                    "No GameObject resolved for the PlayableDirector host. " +
                    "Address by instance_id > path > name.");

            var asset = ResolveTimeline(asset_path, asset_instance_id);
            if (asset == null) return TimelineAssetNotFound();

            var director = host.GetComponent<PlayableDirector>();
            if (director == null)
            {
                Undo.RegisterCreatedObjectUndo(host, "Add PlayableDirector");
                director = Undo.AddComponent<PlayableDirector>(host);
            }
            else
            {
                Undo.RecordObject(director, "Bind Timeline");
            }

            director.playableAsset = asset;
            if (autoplay) director.playOnAwake = true;
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(160);
            sb.Append("\"director\":{");
            sb.Append("\"instanceId\":").Append(director.GetInstanceID()).Append(',');
            sb.Append("\"gameObjectPath\":").Append(TimelineJson.Esc(TimelineTargets.BuildPath(host))).Append(',');
            sb.Append("\"assetPath\":").Append(TimelineJson.Esc(AssetDatabase.GetAssetPath(asset))).Append(',');
            sb.Append("\"playOnAwake\":").Append(autoplay ? "true" : "false");
            sb.Append('}');
            return TimelineJson.Ok(sb.ToString());
        }

        // =====================================================================
        // modify (reflective field setter)
        // =====================================================================

        // Reflective field setter for TimelineAsset / TrackAsset / PlayableAsset.
        // Address the timeline by asset_path / instance_id. Each entry is
        // { field, value, type? } applied to the TimelineAsset root by default;
        // pass track_index / clip_index to target a track or clip asset instead.
        [BridgeTool("unity_open_mcp_timeline_modify",
            Title = "Timeline: Modify",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "timeline")]
        [System.ComponentModel.Description(
            "Set one or more serialized fields on a TimelineAsset, a TrackAsset, " +
            "or a clip's PlayableAsset. Address the timeline by asset_path " +
            "(preferred) or instance_id; pass track_index to target a track, or " +
            "track_index + clip_index to target a clip's asset. Each entry is " +
            "{ field, value, type? } where type is 'int' | 'float' | 'bool' | " +
            "'string' | 'vector' (default inferred). Mutating: runs the gate " +
            "path; paths_hint is the timeline asset path.")]
        public static string Modify(
            string asset_path = null,
            int instance_id = 0,
            int track_index = -1,
            int clip_index = -1,
            string fields_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return TimelineJson.Error("paths_hint_required",
                    "timeline_modify is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(fields_json))
                return TimelineJson.Error("missing_parameter",
                    "'fields_json' must be a JSON array of {field, value, type?} objects.");

            var asset = ResolveTimeline(asset_path, instance_id);
            if (asset == null) return TimelineAssetNotFound();

            Object target = asset;
            if (track_index >= 0)
            {
                var track = FindTrack(asset, track_index, null);
                if (track == null)
                    return TimelineJson.Error("track_not_found",
                        $"track_index {track_index} out of range.");
                target = track;
                if (clip_index >= 0)
                {
                    var clips = track.GetClips();
                    int i = 0;
                    TimelineClip found = null;
                    foreach (var c in clips)
                    {
                        if (i == clip_index) { found = c; break; }
                        i++;
                    }
                    if (found == null || found.asset == null)
                        return TimelineJson.Error("clip_not_found",
                            $"clip_index {clip_index} out of range or has no asset.");
                    target = found.asset;
                }
            }

            Undo.RecordObject(target, "Modify Timeline object");
            var applied = new StringBuilder(256);
            var errors = new StringBuilder(256);
            applied.Append('[');
            errors.Append('[');
            bool firstApplied = true;
            bool firstError = true;

            var entries = ParseFieldArray(fields_json);
            if (entries == null)
                return TimelineJson.Error("invalid_parameter",
                    "'fields_json' must be a JSON array of {field, value, type?} objects.");

            foreach (var entry in entries)
            {
                var result = SetField(target, entry);
                if (result.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append("{\"field\":").Append(TimelineJson.Esc(entry.Field)).Append(",\"applied\":true}");
                }
                else
                {
                    if (!firstError) errors.Append(',');
                    firstError = false;
                    errors.Append("{\"field\":").Append(TimelineJson.Esc(entry.Field)).Append(",\"error\":").Append(TimelineJson.Esc(result.Message)).Append('}');
                }
            }
            applied.Append(']');
            errors.Append(']');

            EditorUtility.SetDirty(target);
            return TimelineJson.Ok(
                "\"applied\":" + applied.ToString() + ',' +
                "\"errors\":" + errors.ToString());
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static TimelineAsset ResolveTimeline(string assetPath, int instanceId)
        {
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (instanceId != 0)
                return EditorUtility.InstanceIDToObject(instanceId) as TimelineAsset;
            return null;
        }

        private static TrackAsset FindTrack(TimelineAsset asset, int index, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var roots = asset.GetRootTracks();
                foreach (var t in roots)
                    if (t != null && t.name == name) return t;
                // Search nested (under Group tracks) too.
                foreach (var t in roots)
                    if (t is GroupTrack g)
                        foreach (var c in g.GetChildTracks())
                            if (c != null && c.name == name) return c as TrackAsset;
            }
            if (index >= 0)
            {
                var roots = asset.GetRootTracks();
                if (index < roots.Count) return roots[index];
            }
            return null;
        }

        private static string TimelineAssetNotFound()
            => TimelineJson.Error("asset_not_found",
                "No TimelineAsset resolved. Address it by asset_path " +
                "('Assets/.../*.playable') or instance_id.");

        // Track-type enum handling — mirrors the reference Helpers.cs. The
        // concrete types live in UnityEngine.Timeline.
        private static bool TryResolveTrackType(string s, out System.Type type, out string error)
        {
            error = null;
            switch (s.ToLowerInvariant())
            {
                case "animation": type = typeof(AnimationTrack); return true;
                case "activation": type = typeof(ActivationTrack); return true;
                case "audio": type = typeof(AudioTrack); return true;
                case "signal": type = typeof(SignalTrack); return true;
                case "control": type = typeof(ControlTrack); return true;
                case "group": type = typeof(GroupTrack); return true;
                case "playable": type = typeof(PlayableTrack); return true;
                default:
                    type = null;
                    error = $"Unknown track_type '{s}'. Valid: Animation, " +
                            "Activation, Audio, Signal, Control, Group, Playable.";
                    return false;
            }
        }

        // ----- Field-patch helpers (hand-rolled JSON, mirrors Splines modify) -

        struct FieldEntry
        {
            public string Field;
            public string RawValue;
            public string TypeHint;
        }

        struct FieldResult
        {
            public bool Ok;
            public string Message;
        }

        private static System.Collections.Generic.List<FieldEntry> ParseFieldArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return null;

            var entries = new System.Collections.Generic.List<FieldEntry>();
            int depth = 0;
            int objStart = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == '{')
                {
                    if (depth == 0) objStart = i + 1;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var objBody = trimmed.Substring(objStart, i - objStart);
                        entries.Add(ParseFieldEntry(objBody));
                        objStart = -1;
                    }
                }
            }
            return entries;
        }

        private static FieldEntry ParseFieldEntry(string objBody)
        {
            var entry = new FieldEntry();
            entry.Field = ExtractStringValue(objBody, "field");
            entry.TypeHint = ExtractStringValue(objBody, "type");
            entry.RawValue = ExtractRawValue(objBody, "value");
            return entry;
        }

        private static string ExtractStringValue(string objBody, string key)
        {
            var raw = ExtractRawValue(objBody, key);
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        private static string ExtractRawValue(string objBody, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = objBody.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = objBody.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            while (start < objBody.Length && char.IsWhiteSpace(objBody[start])) start++;
            if (start >= objBody.Length) return null;

            if (objBody[start] == '"')
            {
                var end = start + 1;
                while (end < objBody.Length)
                {
                    if (objBody[end] == '\\' && end + 1 < objBody.Length) { end += 2; continue; }
                    if (objBody[end] == '"') break;
                    end++;
                }
                return objBody.Substring(start, System.Math.Min(end + 1, objBody.Length) - start);
            }

            if (objBody[start] == '{' || objBody[start] == '[')
            {
                var open = objBody[start];
                var close = open == '{' ? '}' : ']';
                int d = 0;
                var end = start;
                while (end < objBody.Length)
                {
                    if (objBody[end] == open) d++;
                    else if (objBody[end] == close)
                    {
                        d--;
                        if (d == 0) { end++; break; }
                    }
                    end++;
                }
                return objBody.Substring(start, end - start);
            }

            var primitiveEnd = start;
            while (primitiveEnd < objBody.Length &&
                   objBody[primitiveEnd] != ',' &&
                   objBody[primitiveEnd] != '}')
                primitiveEnd++;
            return objBody.Substring(start, primitiveEnd - start).Trim();
        }

        private static FieldResult SetField(Object obj, FieldEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Field))
                return new FieldResult { Ok = false, Message = "field is required" };

            // SerializedObject is the canonical Unity editor path — handles
            // properties (including the [SerializeField] private fields most
            // Timeline assets expose) without binding to a concrete type.
            var so = new SerializedObject(obj);
            var prop = so.FindProperty(entry.Field);
            if (prop == null)
                return new FieldResult { Ok = false, Message = $"Unknown field '{entry.Field}' on {obj.GetType().Name}." };

            try
            {
                ApplyToProperty(prop, entry.RawValue, entry.TypeHint);
                so.ApplyModifiedPropertiesWithoutUndo();
                return new FieldResult { Ok = true };
            }
            catch (System.Exception e)
            {
                return new FieldResult { Ok = false, Message = e.Message };
            }
        }

        private static void ApplyToProperty(SerializedProperty prop, string raw, string typeHint)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(raw.Trim());
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(raw.Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = raw.Trim() == "true";
                    break;
                case SerializedPropertyType.String:
                    var s = raw;
                    if (s.StartsWith("\"") && s.EndsWith("\"") && s.Length >= 2)
                        s = s.Substring(1, s.Length - 2);
                    prop.stringValue = s;
                    break;
                case SerializedPropertyType.Enum:
                    var cleaned = raw.Trim('"');
                    if (int.TryParse(cleaned, out var intVal))
                        prop.enumValueIndex = intVal;
                    else
                        prop.enumValueIndex = System.Array.FindIndex(
                            prop.enumDisplayNames, n => string.Equals(n, cleaned, System.StringComparison.OrdinalIgnoreCase));
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = ParseVector3(raw, Vector3.zero);
                    break;
                default:
                    throw new System.NotSupportedException(
                        $"Unsupported property type {prop.propertyType} for field '{prop.name}'.");
            }
        }

        private static Vector3 ParseVector3(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }
    }
}
#endif
