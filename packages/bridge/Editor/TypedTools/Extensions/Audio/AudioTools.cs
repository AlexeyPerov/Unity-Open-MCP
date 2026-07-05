// M20 Plan 3 / T20.3.1 — Audio embedded domain tools.
//
// Five typed tools covering the AudioSource / AudioListener / AudioMixer
// layer.
//
//   audio_source_add              — add an AudioSource to a GameObject.
//   audio_source_modify           — typed patch on an AudioSource (volume /
//                                   pitch / loop / spatial blend / mixer group
//                                   via AudioMixerGroup asset path / 3D
//                                   min+max distance / doppler / spread).
//   audio_mixer_set_parameter     — set a float on an AudioMixer asset's
//                                   exposed parameter (mutating asset write).
//   audio_listener_get            — read AudioListener state (read-only).
//                                   Warns when more than one listener is
//                                   enabled — Unity errors on multiple active
//                                   listeners at runtime.
//   audio_mixer_get_parameter     — read an exposed float parameter (read-only).
//
// The AudioSource / AudioListener / AudioMixer / AudioMixerGroup types live in
// the built-in engine audio module (UnityEngine.AudioModule) and are present in
// every Unity install, so this domain ships UNGATED — no
// UNITY_OPEN_MCP_EXT_AUDIO define. The `audio` tool group is still hidden from
// ListTools until the session activates it via unity_open_mcp_manage_tools
// (group visibility is a session concern, independent of compile-gating).
//
// Naming: `unity_open_mcp_audio_<action>` / `unity_open_mcp_audio_mixer_<action>`
// (snake_case domain prefix). The two mixer tools share the `audio` group with
// the source/listener tools.
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.AudioExt
{
    // M20 Plan 3 / T20.3.1 — Audio tools. Registry-discovered via
    // [BridgeToolType] + [BridgeTool]. Mutating tools declare IsMutating = true
    // and accept a snake_case paths_hint (bound to the C# pathsHint parameter
    // by name) so the gate can scope the verify checkpoint. Read-only tools
    // set Gate = Off and ReadOnlyHint = true.
    [BridgeToolType]
    public static class AudioTools
    {
        // =====================================================================
        // AudioSource — add
        // =====================================================================

        // Add an AudioSource component to a target GameObject and configure
        // the common fields (clipPath / volume / pitch / loop / playOnAwake /
        // spatialBlend) + adds min/max distance. Idempotent: re-using an
        // existing AudioSource is reported with added:false.
        [BridgeTool("unity_open_mcp_audio_source_add",
            Title = "Audio: Add AudioSource",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "audio")]
        [System.ComponentModel.Description(
            "Add an AudioSource component to a GameObject. Optionally assign an " +
            "AudioClip (clip_path, Assets/-rooted), and set volume (0-1, default 1), " +
            "pitch (default 1), loop (default true), play_on_awake (default true), " +
            "spatial_blend (0=2D, 1=3D, default 0), spatialize (default false), and " +
            "3D min/max distance. Idempotent — re-using an existing AudioSource reports " +
            "added:false. Mutating: runs the gate path; paths_hint is the host scene path.")]
        public static string AudioSourceAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string clip_path = null,
            float volume = 1f,
            float pitch = 1f,
            bool loop = true,
            bool play_on_awake = true,
            float spatial_blend = 0f,
            bool spatialize = false,
            float min_distance = 1f,
            float max_distance = 500f,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return AudioJson.Error("paths_hint_required",
                    "audio_source_add is mutating; pass a non-empty paths_hint scoped " +
                    "to the host's scene path.");

            var host = AudioTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            AudioClip clip = null;
            if (!string.IsNullOrEmpty(clip_path))
            {
                clip = LoadAudioClip(clip_path);
                if (clip == null)
                    return AudioJson.Error("asset_not_found",
                        "AudioClip not found at '" + clip_path + "'.");
            }

            Undo.RecordObject(host, "Add AudioSource");
            var source = host.GetComponent<AudioSource>();
            bool added = false;
            if (source == null)
            {
                source = Undo.AddComponent<AudioSource>(host);
                added = true;
            }

            ApplySourceSettings(source, clip, volume, pitch, loop, play_on_awake,
                spatial_blend, spatialize, min_distance, max_distance);

            EditorUtility.SetDirty(host);
            return AudioJson.Ok(BuildSourceState(source, added));
        }

        // =====================================================================
        // AudioSource — typed modify
        // =====================================================================

        // Typed patch on an AudioSource. Each field is optional — omit to leave
        // unchanged. mixer_group_path assigns an AudioMixerGroup from an
        // Assets/-rooted .mix asset path (resolved via the asset's FindMatching
        // Groups — the first group is used when the .mix exposes several).
        [BridgeTool("unity_open_mcp_audio_source_modify",
            Title = "Audio: Modify AudioSource",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "audio")]
        [System.ComponentModel.Description(
            "Set typed AudioSource fields: clip_path (Assets/-rooted AudioClip), " +
            "volume (0-1), pitch, loop, play_on_awake, spatial_blend (0=2D, 1=3D), " +
            "spatialize, min_distance, max_distance, doppler_level, spread, " +
            "mixer_group_path (Assets/-rooted .mix — assigns the first AudioMixerGroup " +
            "exposed by the mixer; pass null to clear), output_mixer_group via the mixer. " +
            "Each field is optional — omit to leave unchanged. Mutating: runs the gate " +
            "path; paths_hint is the host scene path (and the mixer asset path when " +
            "mixer_group_path is set).")]
        public static string AudioSourceModify(
            int instance_id = 0,
            string path = null,
            string name = null,
            string clip_path = null,
            float? volume = null,
            float? pitch = null,
            bool? loop = null,
            bool? play_on_awake = null,
            float? spatial_blend = null,
            bool? spatialize = null,
            float? min_distance = null,
            float? max_distance = null,
            float? doppler_level = null,
            float? spread = null,
            string mixer_group_path = null,
            bool clear_mixer_group = false,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return AudioJson.Error("paths_hint_required",
                    "audio_source_modify is mutating; pass a non-empty paths_hint.");

            var host = AudioTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var source = host.GetComponent<AudioSource>();
            if (source == null)
                return AudioJson.Error("component_not_found",
                    "Target has no AudioSource. Add one with audio_source_add first.");

            AudioClip clip = null;
            if (!string.IsNullOrEmpty(clip_path))
            {
                clip = LoadAudioClip(clip_path);
                if (clip == null)
                    return AudioJson.Error("asset_not_found",
                        "AudioClip not found at '" + clip_path + "'.");
            }

            AudioMixerGroup group = null;
            if (clear_mixer_group)
            {
                // Explicit clear — fall through to assign null below.
            }
            else if (!string.IsNullOrEmpty(mixer_group_path))
            {
                group = LoadMixerGroup(mixer_group_path);
                if (group == null)
                    return AudioJson.Error("asset_not_found",
                        "AudioMixer (or its first group) not found at '" + mixer_group_path + "'.");
            }

            Undo.RecordObject(source, "Modify AudioSource");

            if (clip != null) source.clip = clip;
            if (volume.HasValue) source.volume = volume.Value;
            if (pitch.HasValue) source.pitch = pitch.Value;
            if (loop.HasValue) source.loop = loop.Value;
            if (play_on_awake.HasValue) source.playOnAwake = play_on_awake.Value;
            if (spatial_blend.HasValue) source.spatialBlend = spatial_blend.Value;
            if (spatialize.HasValue) source.spatialize = spatialize.Value;
            if (min_distance.HasValue) source.minDistance = min_distance.Value;
            if (max_distance.HasValue) source.maxDistance = max_distance.Value;
            if (doppler_level.HasValue) source.dopplerLevel = doppler_level.Value;
            if (spread.HasValue) source.spread = spread.Value;
            // clear_mixer_group wins over a resolved group when both are passed.
            if (clear_mixer_group || group != null)
                source.outputAudioMixerGroup = group;

            EditorUtility.SetDirty(source);
            return AudioJson.Ok(BuildSourceState(source, added: false));
        }

        // =====================================================================
        // AudioMixer — set exposed float parameter (mutating asset write)
        // =====================================================================

        // Set a float value on an AudioMixer asset's exposed parameter. The
        // mixer is an asset — LoadAssetAtPath + SetFloat + SetDirty. normalize
        // (default false) maps a 0-1 input onto the typical -80..0 dB range so
        // agents can pass a friendly volume slider without computing dB.
        [BridgeTool("unity_open_mcp_audio_mixer_set_parameter",
            Title = "Audio: Set Mixer Parameter",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "audio")]
        [System.ComponentModel.Description(
            "Set a float value on an AudioMixer asset's exposed parameter. " +
            "mixer_path is an Assets/-rooted .mix asset path; parameter_name is the " +
            "exposed parameter name; value is the raw float (dB for volume params). " +
            "normalize (default false) maps a 0-1 input onto the -80..0 dB range so a " +
            "friendly volume slider can be passed directly. The mixer asset is marked " +
            "dirty — call assets_refresh / scene_save to commit. Mutating: runs the gate " +
            "path; paths_hint is the mixer asset path.")]
        public static string AudioMixerSetParameter(
            string mixer_path = null,
            string parameter_name = null,
            float value = 0f,
            bool normalize = false,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return AudioJson.Error("paths_hint_required",
                    "audio_mixer_set_parameter is mutating; pass a non-empty paths_hint " +
                    "scoped to the mixer asset path.");

            if (string.IsNullOrEmpty(mixer_path))
                return AudioJson.Error("missing_parameter",
                    "'mixer_path' (Assets/-rooted .mix path) is required.");
            if (string.IsNullOrEmpty(parameter_name))
                return AudioJson.Error("missing_parameter",
                    "'parameter_name' (exposed parameter name) is required.");

            if (!mixer_path.StartsWith("Assets/") ||
                !mixer_path.EndsWith(".mix", System.StringComparison.OrdinalIgnoreCase))
                return AudioJson.Error("invalid_asset_path",
                    "mixer_path must be Assets/-rooted and end with '.mix'.");

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(mixer_path);
            if (mixer == null)
                return AudioJson.Error("asset_not_found",
                    "AudioMixer not found at '" + mixer_path + "'.");

            // normalize maps 0..1 → -80..0 dB (the typical exposed-volume range).
            float resolved = normalize ? Mathf.Lerp(-80f, 0f, Mathf.Clamp01(value)) : value;

            // GetFloat throws nothing but returns false when the name is not
            // exposed — surface that as a structured error.
            if (!mixer.GetFloat(parameter_name, out _))
                return AudioJson.Error("parameter_not_exposed",
                    "Parameter '" + parameter_name + "' is not exposed on this AudioMixer. " +
                    "Expose it in the Audio Mixer window first.");

            if (!mixer.SetFloat(parameter_name, resolved))
                return AudioJson.Error("parameter_set_failed",
                    "SetFloat failed for '" + parameter_name + "'.");

            EditorUtility.SetDirty(mixer);

            // Read back the stored value so the caller can confirm.
            mixer.GetFloat(parameter_name, out var stored);

            var sb = new StringBuilder(200);
            sb.Append("\"mixer\":{");
            sb.Append("\"path\":").Append(AudioJson.Esc(mixer_path)).Append(',');
            sb.Append("\"name\":").Append(AudioJson.Esc(mixer.name)).Append(',');
            sb.Append("\"parameter\":").Append(AudioJson.Esc(parameter_name));
            sb.Append('}');
            sb.Append(",\"value\":").Append(Num(stored));
            sb.Append(",\"normalized\":").Append(normalize ? "true" : "false");
            return AudioJson.Ok(sb.ToString());
        }

        // =====================================================================
        // AudioListener — get (read-only, warns on duplicates)
        // =====================================================================

        // Read the AudioListener state across the open scene(s). Unity allows at
        // most one enabled AudioListener at runtime — report the count and flag
        // duplicates as a warning so an agent can clean up before Play Mode.
        [BridgeTool("unity_open_mcp_audio_listener_get",
            Title = "Audio: Get Listener",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "audio")]
        [System.ComponentModel.Description(
            "Read AudioListener state across the open scene(s). Reports each " +
            "listener's host, enabled flag, instance id, and path, plus an enabled " +
            "count. Unity allows at most one enabled AudioListener at runtime — when " +
            "more than one is enabled, a `duplicateWarning` field is set so an agent " +
            "can disable the extra listener before entering Play Mode. Read-only, " +
            "gate-free.")]
        public static string AudioListenerGet()
        {
            var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include);
            int enabledCount = 0;
            foreach (var l in listeners)
                if (l.enabled) enabledCount++;

            var sb = new StringBuilder(320);
            sb.Append("\"listeners\":[");
            for (int i = 0; i < listeners.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var l = listeners[i];
                sb.Append('{');
                sb.Append("\"instanceId\":").Append(InstanceId.ToJson(l)).Append(',');
                sb.Append("\"name\":").Append(AudioJson.Esc(l.gameObject.name)).Append(',');
                sb.Append("\"path\":").Append(AudioJson.Esc(AudioTargets.BuildPath(l.gameObject))).Append(',');
                sb.Append("\"enabled\":").Append(l.enabled ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("],");
            sb.Append("\"count\":").Append(listeners.Length).Append(',');
            sb.Append("\"enabledCount\":").Append(enabledCount).Append(',');
            sb.Append("\"duplicateWarning\":").Append(enabledCount > 1 ? "true" : "false");
            return AudioJson.Ok(sb.ToString());
        }

        // =====================================================================
        // AudioMixer — get exposed float parameter (read-only)
        // =====================================================================

        // Read a float on an AudioMixer asset's exposed parameter. Read-only.
        [BridgeTool("unity_open_mcp_audio_mixer_get_parameter",
            Title = "Audio: Get Mixer Parameter",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "audio")]
        [System.ComponentModel.Description(
            "Read a float value on an AudioMixer asset's exposed parameter. " +
            "mixer_path is an Assets/-rooted .mix asset path; parameter_name is the " +
            "exposed parameter name. Returns the current value, or a " +
            "`parameter_not_exposed` error when the name is not exposed. Read-only, " +
            "gate-free.")]
        public static string AudioMixerGetParameter(
            string mixer_path = null,
            string parameter_name = null)
        {
            if (string.IsNullOrEmpty(mixer_path))
                return AudioJson.Error("missing_parameter",
                    "'mixer_path' (Assets/-rooted .mix path) is required.");
            if (string.IsNullOrEmpty(parameter_name))
                return AudioJson.Error("missing_parameter",
                    "'parameter_name' (exposed parameter name) is required.");

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(mixer_path);
            if (mixer == null)
                return AudioJson.Error("asset_not_found",
                    "AudioMixer not found at '" + mixer_path + "'.");

            if (!mixer.GetFloat(parameter_name, out var value))
                return AudioJson.Error("parameter_not_exposed",
                    "Parameter '" + parameter_name + "' is not exposed on this AudioMixer.");

            var sb = new StringBuilder(160);
            sb.Append("\"mixer\":{");
            sb.Append("\"path\":").Append(AudioJson.Esc(mixer_path)).Append(',');
            sb.Append("\"name\":").Append(AudioJson.Esc(mixer.name));
            sb.Append('}');
            sb.Append(",\"parameter\":").Append(AudioJson.Esc(parameter_name));
            sb.Append(",\"value\":").Append(Num(value));
            return AudioJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — apply + state
        // =====================================================================

        private static void ApplySourceSettings(AudioSource source, AudioClip clip,
            float volume, float pitch, bool loop, bool playOnAwake,
            float spatialBlend, bool spatialize, float minDistance, float maxDistance)
        {
            if (clip != null) source.clip = clip;
            source.volume = volume;
            source.pitch = pitch;
            source.loop = loop;
            source.playOnAwake = playOnAwake;
            source.spatialBlend = spatialBlend;
            source.spatialize = spatialize;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
        }

        private static string BuildSourceState(AudioSource source, bool added)
        {
            var sb = new StringBuilder(320);
            sb.Append("\"source\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(source)).Append(',');
            sb.Append("\"clip\":").Append(source.clip != null
                ? AudioJson.Esc(source.clip.name) : "\"\"").Append(',');
            sb.Append("\"clipPath\":").Append(source.clip != null
                ? AudioJson.Esc(AssetDatabase.GetAssetPath(source.clip)) : "\"\"").Append(',');
            sb.Append("\"volume\":").Append(Num(source.volume)).Append(',');
            sb.Append("\"pitch\":").Append(Num(source.pitch)).Append(',');
            sb.Append("\"loop\":").Append(source.loop ? "true" : "false").Append(',');
            sb.Append("\"playOnAwake\":").Append(source.playOnAwake ? "true" : "false").Append(',');
            sb.Append("\"spatialBlend\":").Append(Num(source.spatialBlend)).Append(',');
            sb.Append("\"spatialize\":").Append(source.spatialize ? "true" : "false").Append(',');
            sb.Append("\"minDistance\":").Append(Num(source.minDistance)).Append(',');
            sb.Append("\"maxDistance\":").Append(Num(source.maxDistance)).Append(',');
            sb.Append("\"dopplerLevel\":").Append(Num(source.dopplerLevel)).Append(',');
            sb.Append("\"spread\":").Append(Num(source.spread));
            var group = source.outputAudioMixerGroup;
            sb.Append(",\"mixerGroup\":").Append(group != null
                ? AudioJson.Esc(group.name) : "\"\"");
            if (group != null)
                sb.Append(",\"mixerGroupPath\":").Append(
                    AudioJson.Esc(AssetDatabase.GetAssetPath(group)));
            sb.Append('}');
            return sb.ToString();
        }

        // =====================================================================
        // Helpers — asset loading
        // =====================================================================

        private static AudioClip LoadAudioClip(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            return AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
        }

        // Load the first AudioMixerGroup exposed by a .mix asset. AudioMixer
        // assets expose their groups via FindMatchingGroups(string) — passing
        // the mixer's own name returns every top-level group; we take the first
        // (an AudioSource can only bind one group at a time anyway). Returns
        // null when the asset is absent or exposes no groups.
        private static AudioMixerGroup LoadMixerGroup(string mixerPath)
        {
            if (string.IsNullOrEmpty(mixerPath)) return null;
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(mixerPath);
            if (mixer == null) return null;
            var groups = mixer.FindMatchingGroups(System.IO.Path.GetFileNameWithoutExtension(mixerPath));
            if (groups == null || groups.Length == 0)
            {
                // Fall back to the full mixer scan — some mixers name child
                // groups differently than the asset.
                groups = mixer.FindMatchingGroups(string.Empty);
            }
            return (groups != null && groups.Length > 0) ? groups[0] : null;
        }

        // =====================================================================
        // Helpers — number formatting
        // =====================================================================

        // Render floats with invariant culture, trimming trailing zeros so the
        // JSON reads cleanly (1 instead of 1.0). Rounding to 6 decimals matches
        // the rest of the typed tool surface.
        private static string Num(float f)
            => f.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        private static string TargetNotFound()
            => AudioJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
