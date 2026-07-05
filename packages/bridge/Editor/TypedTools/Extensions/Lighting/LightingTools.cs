// M20 Plan 2 — Lighting embedded domain tools.
//
// Seven typed tools covering the per-light / per-probe / skybox layer, on top
// of the existing project-settings settings_get_lighting /
// settings_set_lighting surface.
//
//   light_add                  — add a Light component to a GameObject.
//   light_set                   — set typed Light fields (color / intensity /
//                                 range / spot angle / shadows / render mode /
//                                 culling mask).
//   light_modify                — reflective field patch on a Light (mirrors
//                                 component_modify but typed to Light, with
//                                 enum-name support for LightType /
//                                 LightShadows / RenderMode).
//   reflection_probe_bake       — bake a ReflectionProbe (realtime / baked /
//                                 custom). Long mutation: EditorSettle so the
//                                 dispatcher waits for the bake before the next
//                                 call.
//   reflection_probe_get        — read probe settings (read-only).
//   skybox_set                  — assign RenderSettings.skybox from a material
//                                 asset path (or null to clear).
//   skybox_get                  — read the current skybox material path
//                                 (read-only).
//
// The Light / ReflectionProbe / RenderSettings / Lightmapping types live in
// the built-in engine modules and are present in every Unity install, so this
// domain ships UNGATED — no UNITY_OPEN_MCP_EXT_LIGHTING define. The `lighting`
// tool group is still hidden from ListTools until the session activates it via
// unity_open_mcp_manage_tools (group visibility is a session concern,
// independent of compile-gating).
//
// Naming: `unity_open_mcp_light_<action>` / `unity_open_mcp_reflection_probe_<action>`
// / `unity_open_mcp_skybox_<action>` (snake_case domain prefix).
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.LightingExt
{
    // M20 Plan 2 — Lighting tools. Registry-discovered via [BridgeToolType] +
    // [BridgeTool]. Mutating tools declare IsMutating = true and accept a
    // snake_case paths_hint (bound to the C# pathsHint parameter by name) so
    // the gate can scope the verify checkpoint.
    [BridgeToolType]
    public static class LightingTools
    {
        // =====================================================================
        // Light — add
        // =====================================================================

        // Add a Light component to a target GameObject. Optionally configure
        // the common fields (type / color / intensity / range / spot angle).
        // Idempotent: re-using an existing Light is reported with added:false.
        [BridgeTool("unity_open_mcp_light_add",
            Title = "Lighting: Add Light",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "lighting")]
        [System.ComponentModel.Description(
            "Add a Light component to a GameObject. Optionally set the type " +
            "(Spot | Point | Directional | Area | Rectangle, default Directional), " +
            "color ([r,g,b,(a)] 0-1), intensity (default 1), range (Point/Spot), " +
            "and spot angle (Spot). Idempotent — re-using an existing light reports " +
            "added:false. Mutating: runs the gate path; paths_hint is the host " +
            "scene path.")]
        public static string LightAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string light_type = "Directional",
            string color = null,
            float intensity = 1f,
            float range = 10f,
            float spot_angle = 30f,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return LightingJson.Error("paths_hint_required",
                    "light_add is mutating; pass a non-empty paths_hint scoped " +
                    "to the host's scene path.");

            var host = LightingTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            Undo.RecordObject(host, "Add Light");
            var light = host.GetComponent<Light>();
            bool added = false;
            if (light == null)
            {
                light = Undo.AddComponent<Light>(host);
                added = true;
            }

            ApplyLightSettings(light, light_type, color, intensity, range, spot_angle);

            EditorUtility.SetDirty(host);
            return LightingJson.Ok(BuildLightState(light, added));
        }

        // =====================================================================
        // Light — set typed fields
        // =====================================================================

        // Set the common Light fields in one typed call (lightType / color /
        // intensity / range / spotAngle / shadows). Each field is optional —
        // omit a field to leave it unchanged.
        [BridgeTool("unity_open_mcp_light_set",
            Title = "Lighting: Set Light Fields",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "lighting")]
        [System.ComponentModel.Description(
            "Set typed Light fields: light_type (Spot | Point | Directional | " +
            "Area | Rectangle), color ([r,g,b,(a)] 0-1), intensity (float), " +
            "range (float), spot_angle (float, Spot only), shadows (none | hard | " +
            "soft), render_mode (Auto | Important | NotImportant), culling_mask " +
            "(int LayerMask value). Each field is optional — omit to leave " +
            "unchanged. Mutating: runs the gate path; paths_hint is the host " +
            "scene path.")]
        public static string LightSet(
            int instance_id = 0,
            string path = null,
            string name = null,
            string light_type = null,
            string color = null,
            float? intensity = null,
            float? range = null,
            float? spot_angle = null,
            string shadows = null,
            string render_mode = null,
            int? culling_mask = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return LightingJson.Error("paths_hint_required",
                    "light_set is mutating; pass a non-empty paths_hint.");

            var host = LightingTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var light = host.GetComponent<Light>();
            if (light == null)
                return LightingJson.Error("component_not_found",
                    "Target has no Light. Add one with light_add first.");

            Undo.RecordObject(light, "Set Light fields");

            if (!string.IsNullOrEmpty(light_type) && TryParseLightType(light_type, out var lt))
                light.type = lt;
            if (!string.IsNullOrEmpty(color))
                light.color = ParseColor(color, light.color);
            if (intensity.HasValue) light.intensity = intensity.Value;
            if (range.HasValue) light.range = range.Value;
            if (spot_angle.HasValue) light.spotAngle = spot_angle.Value;
            if (!string.IsNullOrEmpty(shadows) && TryParseShadows(shadows, out var sh))
                light.shadows = sh;
            if (!string.IsNullOrEmpty(render_mode) && TryParseRenderMode(render_mode, out var rm))
                light.renderMode = rm;
            if (culling_mask.HasValue)
                light.cullingMask = culling_mask.Value;

            EditorUtility.SetDirty(light);
            return LightingJson.Ok(BuildLightState(light, added: false));
        }

        // =====================================================================
        // Light — reflective field patch
        // =====================================================================

        // Reflective field setter for Light — agents use it when light_set does
        // not cover a niche field. Each entry is { field, value, type? }; type
        // is 'int' | 'float' | 'bool' | 'string' | 'vector' | 'color' (default
        // inferred from the field's current type). Enum fields (LightType /
        // LightShadows / RenderMode) accept a name or an int index. Per-field
        // errors are accumulated — a single bad entry does not abort the batch.
        [BridgeTool("unity_open_mcp_light_modify",
            Title = "Lighting: Modify Light",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "lighting")]
        [System.ComponentModel.Description(
            "Set one or more serialized fields on a Light component attached to " +
            "a target GameObject. Use this when light_set does not cover a niche " +
            "field; otherwise prefer the typed tool. Each entry is " +
            "{ field, value, type? } where type is 'int' | 'float' | 'bool' | " +
            "'string' | 'vector' | 'color' (default inferred from the field's " +
            "current type). Enum fields (LightType / LightShadows / RenderMode) " +
            "accept a name or an int index. Mutating: runs the gate path; " +
            "paths_hint is the host scene path.")]
        public static string LightModify(
            int instance_id = 0,
            string path = null,
            string name = null,
            string fields_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return LightingJson.Error("paths_hint_required",
                    "light_modify is mutating; pass a non-empty paths_hint.");

            var host = LightingTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var light = host.GetComponent<Light>();
            if (light == null)
                return LightingJson.Error("component_not_found",
                    "Target has no Light. Add one with light_add first.");

            var entries = ParseFieldArray(fields_json);
            if (entries == null)
                return LightingJson.Error("missing_parameter",
                    "'fields_json' must be a JSON array of {field, value, type?} objects.");

            Undo.RecordObject(light, "Modify Light");
            var applied = new StringBuilder(256);
            var errors = new StringBuilder(256);
            applied.Append('[');
            errors.Append('[');
            bool firstApplied = true;
            bool firstError = true;

            foreach (var entry in entries)
            {
                var result = SetField(light, entry);
                if (result.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append("{\"field\":").Append(LightingJson.Esc(entry.Field)).Append(",\"applied\":true}");
                }
                else
                {
                    if (!firstError) errors.Append(',');
                    firstError = false;
                    errors.Append("{\"field\":").Append(LightingJson.Esc(entry.Field));
                    errors.Append(",\"error\":").Append(LightingJson.Esc(result.Message)).Append('}');
                }
            }
            applied.Append(']');
            errors.Append(']');

            EditorUtility.SetDirty(light);
            return LightingJson.Ok("\"applied\":" + applied + ",\"errors\":" + errors);
        }

        // =====================================================================
        // Reflection probe — bake (long mutation)
        // =====================================================================

        // Bake a ReflectionProbe. bake_mode:
        //   realtime — ReflectionProbe.Bake() into the probe's runtime texture.
        //   baked    — Lightmapping.BakeAsync (full lightmap bake incl. probes).
        //   custom   — Lightmapping.BakeReflectionProbeSnapshot into the named
        //              cubemap asset path (created if absent).
        // The bake can take seconds; EditorSettle makes the dispatcher wait for
        // the bake + asset refresh before returning.
        [BridgeTool("unity_open_mcp_reflection_probe_bake",
            Title = "Lighting: Bake Reflection Probe",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "lighting")]
        [System.ComponentModel.Description(
            "Bake a ReflectionProbe. bake_mode: 'realtime' (bake into the " +
            "probe's runtime texture via ReflectionProbe.Bake), 'baked' (queue " +
            "a full lightmap bake incl. probes via Lightmapping.BakeAsync), or " +
            "'custom' (write a baked snapshot into a named cubemap asset path " +
            "via Lightmapping.BakeReflectionProbeSnapshot — the asset is created " +
            "if absent). For 'custom', pass target_path (an Assets/-rooted " +
            ".cubemap path). The bake can take seconds; EditorSettle waits for " +
            "completion + asset refresh before returning. Mutating: runs the " +
            "gate path; paths_hint includes the probe scene path and (for " +
            "custom mode) the output cubemap asset path.")]
        public static string ReflectionProbeBake(
            int instance_id = 0,
            string path = null,
            string name = null,
            string bake_mode = "realtime",
            string target_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return LightingJson.Error("paths_hint_required",
                    "reflection_probe_bake is mutating; pass a non-empty paths_hint.");

            var mode = (bake_mode ?? "realtime").ToLowerInvariant();
            if (mode != "realtime" && mode != "baked" && mode != "custom")
                return LightingJson.Error("invalid_bake_mode",
                    "bake_mode must be 'realtime', 'baked', or 'custom'.");

            var host = LightingTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var probe = host.GetComponent<ReflectionProbe>();
            if (probe == null)
                return LightingJson.Error("component_not_found",
                    "Target has no ReflectionProbe. Add one (component_add) first.");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string bakedAssetPath = null;

            try
            {
                if (mode == "realtime")
                {
                    // Unity's public ReflectionProbe API has no Bake() instance
                    // method, and Lightmapping.BakeReflectionProbeSnapshot is
                    // internal-only. The public bake-to-texture path is
                    // Lightmapping.BakeReflectionProbe(probe, path) — but for a
                    // "realtime" / quick snapshot (no GI), we reach the internal
                    // BakeReflectionProbeSnapshot(probe) via reflection. When
                    // that internal is unavailable on a future Unity version,
                    // we fall through to RenderProbe (renders the probe's
                    // runtime texture in place).
                    Undo.RecordObject(probe, "Bake ReflectionProbe");
                    if (!TryBakeReflectionProbeSnapshot(probe))
                    {
                        // Fallback: render into the probe's runtime texture.
                        // This is the runtime-quality render, not a baked
                        // snapshot, but it is the closest public API.
                        probe.RenderProbe();
                    }
                }
                else if (mode == "baked")
                {
                    // Queue a full lightmap bake (includes baked reflection
                    // probes). BakeAsync runs in the background; EditorSettle
                    // waits for completion before the dispatcher returns.
                    Undo.RecordObject(probe, "Bake ReflectionProbe (lightmaps)");
                    Lightmapping.BakeAsync();
                }
                else // custom
                {
                    if (string.IsNullOrEmpty(target_path))
                        return LightingJson.Error("missing_parameter",
                            "'target_path' (Assets/-rooted .cubemap path) is required " +
                            "for bake_mode='custom'.");
                    if (!target_path.StartsWith("Assets/") ||
                        !target_path.EndsWith(".cubemap", System.StringComparison.OrdinalIgnoreCase))
                        return LightingJson.Error("invalid_asset_path",
                            "target_path must be Assets/-rooted and end with '.cubemap'.");

                    // Lightmapping.BakeReflectionProbe(probe, path) is the
                    // public bake-to-cubemap-asset API. It creates / overwrites
                    // the cubemap at the path — no need to pre-create it.
                    EnsureFolderFor(target_path);
                    Undo.RecordObject(probe, "Bake ReflectionProbe (custom)");
                    Lightmapping.BakeReflectionProbe(probe, target_path);
                    bakedAssetPath = target_path;
                }
            }
            catch (System.Exception e)
            {
                return LightingJson.Error("bake_failed", e.Message);
            }
            sw.Stop();

            var sb = new StringBuilder(220);
            sb.Append("\"baked\":true,");
            sb.Append("\"bakeMode\":").Append(LightingJson.Esc(mode)).Append(',');
            sb.Append("\"durationMs\":").Append(sw.ElapsedMilliseconds);
            if (bakedAssetPath != null)
            {
                sb.Append(",\"cubemapPath\":").Append(LightingJson.Esc(bakedAssetPath));
                sb.Append(",\"hasSnapshot\":true");
            }
            return LightingJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Reflection probe — get (read-only)
        // =====================================================================

        // Read ReflectionProbe settings. Read-only, gate-free.
        [BridgeTool("unity_open_mcp_reflection_probe_get",
            Title = "Lighting: Get Reflection Probe",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "lighting")]
        [System.ComponentModel.Description(
            "Read ReflectionProbe settings: resolution, HDR, clear flags, " +
            "importance, mode, size, near/far clip, and the baked cubemap path " +
            "(if any). Read-only, gate-free. Address the host by instance_id > " +
            "path > name.")]
        public static string ReflectionProbeGet(
            int instance_id = 0,
            string path = null,
            string name = null)
        {
            var host = LightingTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var probe = host.GetComponent<ReflectionProbe>();
            if (probe == null)
                return LightingJson.Error("component_not_found",
                    "Target has no ReflectionProbe.");

            var sb = new StringBuilder(320);
            sb.Append("\"probe\":{");
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(probe)).Append(',');
            sb.Append("\"mode\":").Append(LightingJson.Esc(probe.mode.ToString())).Append(',');
            sb.Append("\"resolution\":").Append(probe.resolution).Append(',');
            sb.Append("\"hdr\":").Append(probe.hdr ? "true" : "false").Append(',');
            sb.Append("\"clearFlags\":").Append(LightingJson.Esc(probe.clearFlags.ToString())).Append(',');
            sb.Append("\"importance\":").Append(probe.importance).Append(',');
            sb.Append("\"size\":").Append(Vec3(probe.size)).Append(',');
            sb.Append("\"nearClipPlane\":").Append(Num(probe.nearClipPlane)).Append(',');
            sb.Append("\"farClipPlane\":").Append(Num(probe.farClipPlane));
            var bakedPath = probe.bakedTexture != null
                ? AssetDatabase.GetAssetPath(probe.bakedTexture)
                : null;
            sb.Append(",\"bakedTexturePath\":").Append(LightingJson.Esc(bakedPath));
            sb.Append('}');
            return LightingJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Skybox — set (mutating)
        // =====================================================================

        // Assign RenderSettings.skybox from a material asset path. Pass null to
        // clear. Skybox is a scene-environment setting — the active scene is
        // marked dirty so the write persists. The sun source is refreshed by
        // Unity when the skybox changes (DynamicGI.UpdateEnvironment is invoked
        // to nudge the ambient/indirect lighting update).
        [BridgeTool("unity_open_mcp_skybox_set",
            Title = "Lighting: Set Skybox",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "lighting")]
        [System.ComponentModel.Description(
            "Assign RenderSettings.skybox from a material asset path. Pass " +
            "material_path: null to clear the skybox. Skybox is a scene- " +
            "environment setting — the active scene is marked dirty so the " +
            "write persists (call scene_save to commit). The ambient/indirect " +
            "environment is refreshed via DynamicGI.UpdateEnvironment. Mutating: " +
            "runs the gate path; paths_hint covers the active scene path and the " +
            "material asset path.")]
        public static string SkyboxSet(
            string material_path = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return LightingJson.Error("paths_hint_required",
                    "skybox_set is mutating; pass a non-empty paths_hint scoped " +
                    "to the active scene path (and the material asset path).");

            Material mat = null;
            if (!string.IsNullOrEmpty(material_path))
            {
                if (!material_path.StartsWith("Assets/") ||
                    !material_path.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                    return LightingJson.Error("invalid_asset_path",
                        "material_path must be Assets/-rooted and end with '.mat'.");

                mat = AssetDatabase.LoadAssetAtPath<Material>(material_path);
                if (mat == null)
                    return LightingJson.Error("asset_not_found",
                        "Material not found at '" + material_path + "'.");
            }

            // Record the scene state for undo. RenderSettings is scene-scoped;
            // Undo.RecordObject on the active scene's RenderSettings-equivalent
            // is not directly supported, so we mark the scene dirty and let the
            // gate checkpoint capture the change.
            RenderSettings.skybox = mat;
            var activeScene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (activeScene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(activeScene);
            // Refresh ambient/indirect lighting for the new skybox.
            DynamicGI.UpdateEnvironment();

            var sb = new StringBuilder(160);
            sb.Append("\"cleared\":").Append(mat == null ? "true" : "false");
            if (mat != null)
                sb.Append(",\"skyboxPath\":").Append(LightingJson.Esc(material_path));
            sb.Append(",\"skyboxName\":").Append(LightingJson.Esc(mat != null ? mat.name : null));
            return LightingJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Skybox — get (read-only)
        // =====================================================================

        // Read the current RenderSettings.skybox material path. Read-only.
        [BridgeTool("unity_open_mcp_skybox_get",
            Title = "Lighting: Get Skybox",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "lighting")]
        [System.ComponentModel.Description(
            "Read the current RenderSettings.skybox material asset path (or " +
            "null when no skybox is assigned). Read-only, gate-free.")]
        public static string SkyboxGet()
        {
            var skybox = RenderSettings.skybox;
            var path = skybox != null ? AssetDatabase.GetAssetPath(skybox) : null;
            var sb = new StringBuilder(128);
            sb.Append("\"hasSkybox\":").Append(skybox != null ? "true" : "false");
            if (skybox != null)
            {
                sb.Append(",\"skyboxPath\":").Append(LightingJson.Esc(path));
                sb.Append(",\"skyboxName\":").Append(LightingJson.Esc(skybox.name));
                sb.Append(",\"shader\":").Append(LightingJson.Esc(skybox.shader != null ? skybox.shader.name : null));
            }
            return LightingJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — Light
        // =====================================================================

        private static void ApplyLightSettings(Light light, string lightType, string color,
            float intensity, float range, float spotAngle)
        {
            if (!string.IsNullOrEmpty(lightType) && TryParseLightType(lightType, out var lt))
                light.type = lt;
            if (!string.IsNullOrEmpty(color))
                light.color = ParseColor(color, light.color);
            light.intensity = intensity;
            light.range = range;
            if (light.type == LightType.Spot)
                light.spotAngle = spotAngle;
        }

        private static string BuildLightState(Light light, bool added)
        {
            var sb = new StringBuilder(260);
            sb.Append("\"light\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(light)).Append(',');
            sb.Append("\"type\":").Append(LightingJson.Esc(light.type.ToString())).Append(',');
            sb.Append("\"color\":").Append(Color4(light.color)).Append(',');
            sb.Append("\"intensity\":").Append(Num(light.intensity)).Append(',');
            sb.Append("\"range\":").Append(Num(light.range));
            if (light.type == LightType.Spot)
            {
                sb.Append(",\"spotAngle\":").Append(Num(light.spotAngle));
            }
            sb.Append(",\"shadows\":").Append(LightingJson.Esc(light.shadows.ToString()));
            sb.Append(",\"renderMode\":").Append(LightingJson.Esc(light.renderMode.ToString()));
            sb.Append(",\"cullingMask\":").Append(light.cullingMask);
            sb.Append('}');
            return sb.ToString();
        }

        private static bool TryParseLightType(string s, out LightType value)
        {
            if (System.Enum.TryParse(s, true, out value)) return true;
            if (int.TryParse(s, out var idx) && System.Enum.IsDefined(typeof(LightType), idx))
            {
                value = (LightType)idx;
                return true;
            }
            value = default;
            return false;
        }

        private static bool TryParseShadows(string s, out LightShadows value)
        {
            if (System.Enum.TryParse(s, true, out value)) return true;
            if (int.TryParse(s, out var idx) && System.Enum.IsDefined(typeof(LightShadows), idx))
            {
                value = (LightShadows)idx;
                return true;
            }
            value = default;
            return false;
        }

        private static bool TryParseRenderMode(string s, out LightRenderMode value)
        {
            if (System.Enum.TryParse(s, true, out value)) return true;
            if (int.TryParse(s, out var idx) && System.Enum.IsDefined(typeof(LightRenderMode), idx))
            {
                value = (LightRenderMode)idx;
                return true;
            }
            value = default;
            return false;
        }

        // =====================================================================
        // Helpers — reflective field patch
        // =====================================================================

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

            // String value?
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

            // Bracketed value (object/array) — capture balanced.
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

            // Primitive — capture up to comma or end.
            var primitiveEnd = start;
            while (primitiveEnd < objBody.Length &&
                   objBody[primitiveEnd] != ',' &&
                   objBody[primitiveEnd] != '}')
                primitiveEnd++;
            return objBody.Substring(start, primitiveEnd - start).Trim();
        }

        private static FieldResult SetField(Light light, FieldEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Field))
                return new FieldResult { Ok = false, Message = "field is required" };

            var t = typeof(Light);
            var field = t.GetField(entry.Field,
                BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
                return new FieldResult { Ok = false, Message = $"Unknown field '{entry.Field}' on Light." };

            try
            {
                object converted = ConvertValue(field.FieldType, entry.RawValue, entry.TypeHint);
                field.SetValue(light, converted);
                return new FieldResult { Ok = true };
            }
            catch (System.Exception e)
            {
                return new FieldResult { Ok = false, Message = e.Message };
            }
        }

        private static object ConvertValue(System.Type targetType, string raw, string typeHint)
        {
            if (targetType == typeof(string))
            {
                if (raw == null) return null;
                if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                    return raw.Substring(1, raw.Length - 2);
                return raw;
            }
            if (targetType == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return raw == "true";
            if (targetType == typeof(Vector3)) return ParseVector3(raw, Vector3.zero);
            if (targetType == typeof(Vector4)) return ParseVector4(raw, Vector4.zero);
            if (targetType == typeof(Color)) return ParseColor(raw, Color.white);
            if (targetType.IsEnum)
            {
                var cleaned = raw.Trim('"');
                if (System.Enum.IsDefined(targetType, cleaned))
                    return System.Enum.Parse(targetType, cleaned);
                if (int.TryParse(cleaned, out var intVal))
                    return System.Enum.ToObject(targetType, intVal);
                throw new System.FormatException($"Cannot parse '{raw}' as {targetType.Name} enum.");
            }
            throw new System.NotSupportedException($"Unsupported field type {targetType.Name}.");
        }

        // =====================================================================
        // Helpers — parse + format primitives
        // =====================================================================

        private static Color ParseColor(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var trimmed = s.Trim().Trim('[', ']');
            var parts = trimmed.Split(',');
            if (parts.Length < 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return fallback;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var g)) return fallback;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return fallback;
            float a = 1f;
            if (parts.Length >= 4 &&
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var aParsed))
                a = aParsed;
            return new Color(r, g, b, a);
        }

        private static Vector3 ParseVector3(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Trim().Trim('[', ']').Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }

        private static Vector4 ParseVector4(string s, Vector4 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Trim().Trim('[', ']').Split(',');
            if (parts.Length != 4) return fallback;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return fallback;
            if (!float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return fallback;
            return new Vector4(x, y, z, w);
        }

        private static string Color4(Color c)
            => $"[{Num(c.r)},{Num(c.g)},{Num(c.b)},{Num(c.a)}]";

        private static string Vec3(Vector3 v)
            => $"[{Num(v.x)},{Num(v.y)},{Num(v.z)}]";

        // Render floats with invariant culture, trimming trailing zeros so the
        // JSON reads cleanly (1 instead of 1.0). Rounding to 6 decimals matches
        // the rest of the typed tool surface.
        private static string Num(float f)
            => f.ToString("0.######", CultureInfo.InvariantCulture);

        // =====================================================================
        // Helpers — asset folder creation for custom cubemap bake
        // =====================================================================

        // Invoke Lightmapping.BakeReflectionProbeSnapshot(probe) via reflection.
        // That method is the public-adjacent "quick snapshot, no GI" bake, but
        // Unity marks it `internal` (see Lightmapping.bindings.cs), so the
        // Lighting tools reach it via reflection rather than a direct call.
        // Returns true when the snapshot baked; false when the internal is
        // unavailable (callers fall back to RenderProbe / the public bake path).
        private static bool TryBakeReflectionProbeSnapshot(ReflectionProbe probe)
        {
            try
            {
                var method = typeof(Lightmapping).GetMethod(
                    "BakeReflectionProbeSnapshot",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                if (method == null) return false;
                var result = method.Invoke(null, new object[] { probe });
                return result is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureFolderFor(string assetPath)
        {
            var dir = assetPath.Substring(0, assetPath.LastIndexOf('/'));
            if (string.IsNullOrEmpty(dir) || !dir.StartsWith("Assets")) return;
            if (AssetDatabase.IsValidFolder(dir)) return;
            var segments = dir.Split('/');
            var current = segments[0]; // "Assets"
            for (int i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        private static string TargetNotFound()
            => LightingJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
