using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpExtensions.Animation
{
    // M16 Plan 10 / T6.6.10 — AnimationClip half of the Animation extension
    // pack.
    //
    // Three typed tools on `.anim` assets:
    //   - create:  empty AnimationClip at an Assets/-rooted path.
    //   - get_data: read clip metadata (length / frameRate / wrapMode / flags)
    //              + float curve bindings + object-reference curve bindings +
    //              animation events.
    //   - modify:  apply a batch of modifications dispatched by `type`:
    //              SetCurve / RemoveCurve / ClearCurves / SetFrameRate /
    //              SetWrapMode / SetLegacy / AddEvent / ClearEvents.
    //
    // Each modification is dispatched by `type` from `modifications_json` (a
    // JSON array of { type, ... } entries). Per-entry errors are accumulated in
    // the response's `errors` array (no thrown exceptions to MCP). Naming:
    // `unity_open_mcp_animation_<action>` (snake_case domain prefix).
    [BridgeToolType]
    public static class AnimationClipTools
    {
        // =====================================================================
        // Create
        // =====================================================================

        // Create empty AnimationClip assets at one or more `.anim` paths.
        // Intermediate folders are created. Each path is validated
        // independently — bad entries land in `errors`, the rest still create.
        [BridgeTool("unity_open_mcp_animation_create",
            Title = "Animation: Create AnimationClip",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Create empty AnimationClip assets at one or more 'Assets/'-rooted " +
            ".anim paths. Intermediate folders are created. Each path is " +
            "validated independently — bad entries land in `errors`, the rest " +
            "still create. Pair with animation_modify to populate curves and " +
            "events afterwards. Mutating: runs the gate path; paths_hint is the " +
            "list of .anim paths being created.")]
        public static string Create(
            string[] asset_paths,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired();

            if (asset_paths == null || asset_paths.Length == 0)
                return AnimationJson.Error("missing_parameter",
                    "'asset_paths' is required (one or more .anim paths).");

            var errors = new List<string>();
            foreach (var raw in asset_paths)
            {
                if (!AnimationJson.ValidateAssetPath(raw, AnimationJson.ClipExtension,
                        out var path, out var pathError))
                {
                    errors.Add($"{raw}: {pathError}");
                    continue;
                }

                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                {
                    errors.Add($"{path}: an AnimationClip already exists at this path.");
                    continue;
                }

                AnimationJson.EnsureFolders(path);

                var clip = new AnimationClip
                {
                    name = Path.GetFileNameWithoutExtension(path)
                };
                AssetDatabase.CreateAsset(clip, path);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var sb = new StringBuilder(256);
            sb.Append("\"create\":{");
            sb.Append("\"createdPaths\":").Append(JsonStringArray(CreatedPaths(asset_paths, errors)));
            sb.Append(',');
            sb.Append("\"errorCount\":").Append(errors.Count);
            if (errors.Count > 0)
                sb.Append(',').Append("\"errors\":").Append(JsonStringArray(errors));
            sb.Append('}');
            return AnimationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Get data (read-only)
        // =====================================================================

        // Read clip metadata + float curves + object-reference curves +
        // events. Read-only, gate-free.
        [BridgeTool("unity_open_mcp_animation_get_data",
            Title = "Animation: Get AnimationClip Data",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Inspect an AnimationClip asset (.anim) — name, length, frame rate, " +
            "wrap mode, looping/legacy/humanMotion flags, plus the full set of " +
            "float curve bindings, object-reference curve bindings, and animation " +
            "events. Read-only, gate-free. Use this to discover valid " +
            "(path, propertyName, type) tuples for animation_modify SetCurve / " +
            "RemoveCurve entries.")]
        public static string GetData(string asset_path)
        {
            var clip = LoadClip(asset_path, out var loadError);
            if (clip == null) return loadError;

            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"ok\",\"clip\":{");
            sb.Append("\"name\":").Append(AnimationJson.Esc(clip.name)).Append(',');
            sb.Append("\"assetPath\":").Append(AnimationJson.Esc(AnimationJson.Normalize(asset_path))).Append(',');
            sb.Append("\"instanceId\":").Append(InstanceId.ToJson(clip)).Append(',');
            sb.Append("\"length\":").Append(clip.length.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"frameRate\":").Append(clip.frameRate.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"wrapMode\":").Append(AnimationJson.Esc(clip.wrapMode.ToString())).Append(',');
            sb.Append("\"isLooping\":").Append(clip.isLooping ? "true" : "false").Append(',');
            sb.Append("\"legacy\":").Append(clip.legacy ? "true" : "false").Append(',');
            sb.Append("\"humanMotion\":").Append(clip.humanMotion ? "true" : "false").Append(',');
            sb.Append("\"empty\":").Append(clip.empty ? "true" : "false");

            // Float curve bindings.
            sb.Append(",\"curveBindings\":[");
            var bindings = AnimationUtility.GetCurveBindings(clip);
            bool first = true;
            foreach (var binding in bindings)
            {
                if (!first) sb.Append(',');
                first = false;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                AppendBinding(sb, binding, curve?.length ?? 0);
            }
            sb.Append(']');

            // Object-reference curve bindings.
            sb.Append(",\"objectReferenceCurveBindings\":[");
            var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            first = true;
            foreach (var binding in objBindings)
            {
                if (!first) sb.Append(',');
                first = false;
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                AppendBinding(sb, binding, keyframes?.Length ?? 0);
            }
            sb.Append(']');

            // Animation events.
            sb.Append(",\"events\":[");
            var events = AnimationUtility.GetAnimationEvents(clip);
            for (int i = 0; i < events.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendEvent(sb, events[i]);
            }
            sb.Append("]}}");
            return sb.ToString();
        }

        // =====================================================================
        // Modify (batch)
        // =====================================================================

        // Apply a batch of modifications to an AnimationClip. modifications_json
        // is a JSON array of { type, ... } entries dispatched by `type`:
        //   SetCurve    { type, componentType, propertyName, relativePath?, keyframes: [{time, value, inTangent?, outTangent?}] }
        //   RemoveCurve { type, componentType, propertyName, relativePath? }
        //   ClearCurves { type }
        //   SetFrameRate { type, frameRate }
        //   SetWrapMode  { type, wrapMode }
        //   SetLegacy    { type, legacy }
        //   AddEvent     { type, time, functionName, floatParameter?, intParameter?, stringParameter? }
        //   ClearEvents  { type }
        [BridgeTool("unity_open_mcp_animation_modify",
            Title = "Animation: Modify AnimationClip",
            IsMutating = true,
            DestructiveHint = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Apply a batch of modifications to an AnimationClip asset (.anim). " +
            "modifications_json is a JSON array of entries dispatched by `type`: " +
            "SetCurve / RemoveCurve / ClearCurves / SetFrameRate / SetWrapMode / " +
            "SetLegacy / AddEvent / ClearEvents. Per-entry errors are accumulated " +
            "in `errors` and do not abort the batch. Use animation_get_data first " +
            "to discover valid (componentType, propertyName) tuples. Mutating: runs " +
            "the gate path; paths_hint is the .anim asset path.")]
        public static string Modify(
            string asset_path,
            string modifications_json,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired();

            if (string.IsNullOrWhiteSpace(modifications_json))
                return AnimationJson.Error("missing_parameter",
                    "'modifications_json' is required (a JSON array of modification entries).");

            var clip = LoadClip(asset_path, out var loadError);
            if (clip == null) return loadError;

            var mods = ModificationParser.ParseArray(modifications_json);
            if (mods == null)
                return AnimationJson.Error("invalid_modifications_json",
                    "'modifications_json' must be a JSON array of modification entries.");

            var applied = new List<string>();
            var errors = new List<string>();
            // Events are applied as a single rewrite at the end (matches the
            // upstream pack's behavior — AddEvent / ClearEvents mutate a local
            // list, then one SetAnimationEvents call persists the result).
            var events = new List<AnimationEvent>(AnimationUtility.GetAnimationEvents(clip));

            for (int i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                try
                {
                    ApplyModification(clip, mod, events, applied);
                }
                catch (System.Exception e)
                {
                    errors.Add($"[{i}] {mod.Type}: {e.Message}");
                }
            }

            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var sb = new StringBuilder(256);
            sb.Append("\"modify\":{");
            sb.Append("\"assetPath\":").Append(AnimationJson.Esc(AnimationJson.Normalize(asset_path))).Append(',');
            sb.Append("\"applied\":").Append(JsonStringArray(applied)).Append(',');
            sb.Append("\"errorCount\":").Append(errors.Count);
            if (errors.Count > 0)
                sb.Append(',').Append("\"errors\":").Append(JsonStringArray(errors));
            sb.Append('}');
            return AnimationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static string PathRequired()
            => AnimationJson.Error("paths_hint_required",
                "animation tool is mutating; pass a non-empty paths_hint scoped to the .anim asset path.");

        private static AnimationClip LoadClip(string assetPath, out string errorEnvelope)
        {
            errorEnvelope = null;
            if (!AnimationJson.ValidateAssetPath(assetPath, AnimationJson.ClipExtension,
                    out var path, out var pathError))
            {
                errorEnvelope = AnimationJson.Error("invalid_asset_path", pathError);
                return null;
            }

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                errorEnvelope = AnimationJson.Error("asset_not_found",
                    $"No AnimationClip at '{path}'. Create it with animation_create first.");
                return null;
            }
            return clip;
        }

        private static void ApplyModification(AnimationClip clip, Modification mod,
            List<AnimationEvent> events, List<string> applied)
        {
            switch (mod.Type)
            {
                case "SetCurve":
                    ApplySetCurve(clip, mod);
                    applied.Add($"SetCurve({mod.RelativePath ?? ""}, {mod.ComponentType}, {mod.PropertyName})");
                    break;
                case "RemoveCurve":
                    ApplyRemoveCurve(clip, mod);
                    applied.Add($"RemoveCurve({mod.RelativePath ?? ""}, {mod.ComponentType}, {mod.PropertyName})");
                    break;
                case "ClearCurves":
                    clip.ClearCurves();
                    applied.Add("ClearCurves");
                    break;
                case "SetFrameRate":
                    if (!mod.FrameRate.HasValue)
                        throw new System.Exception("frameRate is required for SetFrameRate.");
                    clip.frameRate = mod.FrameRate.Value;
                    applied.Add($"SetFrameRate({mod.FrameRate.Value})");
                    break;
                case "SetWrapMode":
                    if (!mod.WrapMode.HasValue)
                        throw new System.Exception("wrapMode is required for SetWrapMode.");
                    clip.wrapMode = mod.WrapMode.Value;
                    applied.Add($"SetWrapMode({mod.WrapMode.Value})");
                    break;
                case "SetLegacy":
                    if (!mod.Legacy.HasValue)
                        throw new System.Exception("legacy is required for SetLegacy.");
                    clip.legacy = mod.Legacy.Value;
                    applied.Add($"SetLegacy({mod.Legacy.Value})");
                    break;
                case "AddEvent":
                    ApplyAddEvent(events, mod);
                    applied.Add($"AddEvent({mod.FunctionName}@{mod.Time})");
                    break;
                case "ClearEvents":
                    events.Clear();
                    applied.Add("ClearEvents");
                    break;
                default:
                    throw new System.Exception($"Unknown modification type '{mod.Type}'.");
            }
        }

        private static void ApplySetCurve(AnimationClip clip, Modification mod)
        {
            if (string.IsNullOrEmpty(mod.ComponentType))
                throw new System.Exception("componentType is required for SetCurve.");
            if (string.IsNullOrEmpty(mod.PropertyName))
                throw new System.Exception("propertyName is required for SetCurve.");
            if (mod.Keyframes == null || mod.Keyframes.Count == 0)
                throw new System.Exception("keyframes (array of {time, value, ...}) is required for SetCurve.");

            var type = ResolveComponentType(mod.ComponentType);
            var curve = new AnimationCurve();
            foreach (var kf in mod.Keyframes)
            {
                var key = new Keyframe(kf.Time, kf.Value)
                {
                    inTangent = kf.InTangent,
                    outTangent = kf.OutTangent,
                };
                curve.AddKey(key);
            }
            clip.SetCurve(mod.RelativePath ?? string.Empty, type, mod.PropertyName, curve);
        }

        private static void ApplyRemoveCurve(AnimationClip clip, Modification mod)
        {
            if (string.IsNullOrEmpty(mod.ComponentType))
                throw new System.Exception("componentType is required for RemoveCurve.");
            if (string.IsNullOrEmpty(mod.PropertyName))
                throw new System.Exception("propertyName is required for RemoveCurve.");

            var type = ResolveComponentType(mod.ComponentType);
            var relativePath = mod.RelativePath ?? string.Empty;

            // EditorCurveBinding does not have a public Remove API; re-add all
            // bindings except the one to drop. Object-reference curves are
            // preserved too.
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var preserved = new List<KeyValuePair<EditorCurveBinding, AnimationCurve>>();
            foreach (var binding in bindings)
            {
                if (binding.path == relativePath && binding.type == type &&
                    binding.propertyName == mod.PropertyName) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve != null) preserved.Add(new KeyValuePair<EditorCurveBinding, AnimationCurve>(binding, curve));
            }

            var objBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            var preservedObj = new List<KeyValuePair<EditorCurveBinding, ObjectReferenceKeyframe[]>>();
            foreach (var binding in objBindings)
            {
                if (binding.path == relativePath && binding.type == type &&
                    binding.propertyName == mod.PropertyName) continue;
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keyframes != null && keyframes.Length > 0)
                    preservedObj.Add(new KeyValuePair<EditorCurveBinding, ObjectReferenceKeyframe[]>(binding, keyframes));
            }

            clip.ClearCurves();
            foreach (var pair in preserved)
                AnimationUtility.SetEditorCurve(clip, pair.Key, pair.Value);
            foreach (var pair in preservedObj)
                AnimationUtility.SetObjectReferenceCurve(clip, pair.Key, pair.Value);
        }

        private static void ApplyAddEvent(List<AnimationEvent> events, Modification mod)
        {
            if (!mod.Time.HasValue)
                throw new System.Exception("time is required for AddEvent.");
            if (string.IsNullOrEmpty(mod.FunctionName))
                throw new System.Exception("functionName is required for AddEvent.");

            events.Add(new AnimationEvent
            {
                time = mod.Time.Value,
                functionName = mod.FunctionName,
                stringParameter = mod.StringParameter ?? string.Empty,
                floatParameter = mod.FloatParameter ?? 0f,
                intParameter = mod.IntParameter ?? 0,
            });
        }

        // Resolve a System.Type by name. Accepts the full name (preferred) or
        // a bare name; falls back to a couple of common namespace prefixes so
        // agents can pass 'Transform' or 'UnityEngine.Transform' interchangeably.
        private static System.Type ResolveComponentType(string name)
        {
            var t = System.Type.GetType(name);
            if (t != null) return t;

            // Try common Unity namespaces.
            var candidates = new[]
            {
                "UnityEngine." + name,
                "UnityEngine.UI." + name,
            };
            foreach (var candidate in candidates)
            {
                t = System.Type.GetType(candidate);
                if (t != null) return t;
            }

            // Last resort: walk every loaded assembly for a matching full name.
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = asm.GetType(name);
                if (found != null) return found;
            }

            throw new System.Exception($"Could not resolve component type '{name}'. Pass the full name (e.g. 'UnityEngine.Transform').");
        }

        private static void AppendBinding(StringBuilder sb, EditorCurveBinding binding, int keyframeCount)
        {
            sb.Append('{');
            sb.Append("\"path\":").Append(AnimationJson.Esc(binding.path)).Append(',');
            sb.Append("\"propertyName\":").Append(AnimationJson.Esc(binding.propertyName)).Append(',');
            sb.Append("\"type\":").Append(AnimationJson.Esc(binding.type?.FullName ?? binding.type?.Name ?? "")).Append(',');
            sb.Append("\"isPPtrCurve\":").Append(binding.isPPtrCurve ? "true" : "false").Append(',');
            sb.Append("\"isDiscreteCurve\":").Append(binding.isDiscreteCurve ? "true" : "false").Append(',');
            sb.Append("\"keyframeCount\":").Append(keyframeCount);
            sb.Append('}');
        }

        private static void AppendEvent(StringBuilder sb, AnimationEvent evt)
        {
            sb.Append('{');
            sb.Append("\"time\":").Append(evt.time.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"functionName\":").Append(AnimationJson.Esc(evt.functionName)).Append(',');
            sb.Append("\"intParameter\":").Append(evt.intParameter).Append(',');
            sb.Append("\"floatParameter\":").Append(evt.floatParameter.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"stringParameter\":").Append(AnimationJson.Esc(evt.stringParameter));
            sb.Append('}');
        }

        // Collect the asset paths that actually got a clip created — we don't
        // track this inline above so we re-derive it from the input minus the
        // error entries. (errors entries are prefixed with the original path.)
        private static List<string> CreatedPaths(string[] assetPaths, List<string> errors)
        {
            var errorPaths = new HashSet<string>();
            foreach (var e in errors)
            {
                var colon = e.IndexOf(':');
                if (colon > 0) errorPaths.Add(e.Substring(0, colon).Trim());
            }
            var result = new List<string>();
            foreach (var raw in assetPaths)
            {
                if (!AnimationJson.ValidateAssetPath(raw, AnimationJson.ClipExtension,
                        out var path, out _)) continue;
                if (errorPaths.Contains(raw) || errorPaths.Contains(path)) continue;
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                    result.Add(path);
            }
            return result;
        }

        private static string JsonStringArray(List<string> items)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(AnimationJson.Esc(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
