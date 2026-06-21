// M18 Plan 3 — Particle System (UnityEngine.ParticleSystemModule, built-in)
// embedded domain tools.
//
// Compile-gated by UNITY_OPEN_MCP_EXT_PARTICLESYSTEM. The owning sub-asmdef
// (com.alexeyperov.unity-open-mcp-bridge.ParticleSystem.Editor) carries
// `defineConstraints: ["UNITY_OPEN_MCP_EXT_PARTICLESYSTEM"]`; the bridge root
// asmdef sets the define via `versionDefines` when the built-in module is
// present. Ported verbatim (logic, tool ids, JSON schema, gate contracts)
// from the former standalone extension pack at
// packages/extensions/particlesystem — only the namespace changed.
#if UNITY_OPEN_MCP_EXT_PARTICLESYSTEM
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Extensions.ParticleSystem
{
    // M16 Plan 10 / T6.6.9 → M18 Plan 3 — Particle System embedded tools.
    //
    // Two typed tools on a scene ParticleSystem component:
    //   - get:    read runtime state + the well-known modules the agent most
    //             often needs (main / emission / shape / color-over-lifetime /
    //             size-over-lifetime / noise / collision / trails / renderer).
    //   - modify: apply a per-module field patch via the module discriminator +
    //             a hand-rolled fields_json object. Each module is a read/write
    //             struct on the ParticleSystem — assignments back to the
    //             component are required for the change to persist.
    //
    // The upstream reference pack (IvanMurzak/Unity-AI-ParticleSystem, MIT)
    // uses an opaque ReflectorNet SerializedMember payload per module. That is
    // too unstructured for an agent that has to guess field names; we instead
    // expose a fixed, documented field surface per module so the modify tool
    // can validate field names up front and reject typos with a structured
    // error (no thrown exceptions to MCP).
    //
    // Mutating tool runs the gate path with paths_hint scoped to the host's
    // scene path (same model as ProBuilder). Naming:
    // `unity_open_mcp_particle_system_<action>` (snake_case domain prefix).
    [BridgeToolType]
    public static class ParticleSystemTools
    {
        // =====================================================================
        // Get (read-only)
        // =====================================================================

        // Read runtime state + opt-in well-known module data. include_all
        // overrides the per-module toggles. Read-only, gate-free.
        [BridgeTool("unity_open_mcp_particle_system_get",
            Title = "Particle System: Get",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "particle-system")]
        [System.ComponentModel.Description(
            "Inspect a ParticleSystem component on a scene GameObject — runtime " +
            "state (isPlaying / isPaused / isEmitting / isStopped / particleCount / " +
            "time) plus opt-in data for the well-known modules (main / emission / " +
            "shape / color_over_lifetime / size_over_lifetime / rotation_over_lifetime / " +
            "noise / collision / trails / renderer). include_main defaults to true; " +
            "everything else defaults to false. Set include_all to emit every module. " +
            "Read-only, gate-free. Use this to discover valid module + field names " +
            "for particle_system_modify.")]
        public static string Get(
            int instance_id = 0,
            string path = null,
            string name = null,
            bool include_main = true,
            bool include_emission = false,
            bool include_shape = false,
            bool include_color_over_lifetime = false,
            bool include_size_over_lifetime = false,
            bool include_rotation_over_lifetime = false,
            bool include_noise = false,
            bool include_collision = false,
            bool include_trails = false,
            bool include_renderer = false,
            bool include_all = false)
        {
            var ps = ResolvePs(instance_id, path, name, out var resolveError);
            if (ps == null) return resolveError;

            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"ok\",\"particleSystem\":{");
            sb.Append("\"name\":").Append(ParticleSystemJson.Esc(ps.gameObject.name)).Append(',');
            sb.Append("\"instanceId\":").Append(ps.gameObject.GetInstanceID()).Append(',');
            AppendRuntimeState(sb, ps);

            // Emit an optional module. Each call seeds a leading comma so the
            // runtime block above is closed cleanly when no modules follow.
            void Emit(string key, System.Action<StringBuilder> appender)
            {
                sb.Append(',').Append(key);
                appender(sb);
            }

            if (include_all || include_main)
                Emit("\"main\":", sb2 => AppendMain(sb2, ps.main));
            if (include_all || include_emission)
                Emit("\"emission\":", sb2 => AppendEmission(sb2, ps.emission));
            if (include_all || include_shape)
                Emit("\"shape\":", sb2 => AppendShape(sb2, ps.shape));
            if (include_all || include_color_over_lifetime)
                Emit("\"colorOverLifetime\":", sb2 => AppendColorOverLifetime(sb2, ps.colorOverLifetime));
            if (include_all || include_size_over_lifetime)
                Emit("\"sizeOverLifetime\":", sb2 => AppendSizeOverLifetime(sb2, ps.sizeOverLifetime));
            if (include_all || include_rotation_over_lifetime)
                Emit("\"rotationOverLifetime\":", sb2 => AppendRotationOverLifetime(sb2, ps.rotationOverLifetime));
            if (include_all || include_noise)
                Emit("\"noise\":", sb2 => AppendNoise(sb2, ps.noise));
            if (include_all || include_collision)
                Emit("\"collision\":", sb2 => AppendCollision(sb2, ps.collision));
            if (include_all || include_trails)
                Emit("\"trails\":", sb2 => AppendTrails(sb2, ps.trails));
            if (include_all || include_renderer)
            {
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer != null)
                    Emit("\"renderer\":", sb2 => AppendRenderer(sb2, renderer));
            }

            sb.Append("}}");
            return sb.ToString();
        }

        // =====================================================================
        // Modify (mutating, field patch)
        // =====================================================================

        // Apply a per-module field patch. The module discriminator picks which
        // struct to mutate; fields_json is a JSON object of {field: value}
        // entries to set on that module. Only the documented fields for each
        // module are accepted; unknown fields are reported in `unknownFields`
        // and skipped (the call still applies the valid ones). One module per
        // call — chain multiple calls for multi-module edits.
        [BridgeTool("unity_open_mcp_particle_system_modify",
            Title = "Particle System: Modify",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "particle-system")]
        [System.ComponentModel.Description(
            "Apply a per-module field patch to a ParticleSystem component. module " +
            "is one of 'main' / 'emission' / 'shape' / 'color_over_lifetime' / " +
            "'size_over_lifetime' / 'rotation_over_lifetime' / 'noise' / " +
            "'collision' / 'trails' / 'renderer'. fields_json is a JSON object of " +
            "{field: value} entries to set on that module (only the documented " +
            "fields per module are accepted; unknown fields are skipped and " +
            "reported). Use particle_system_get first to discover valid module + " +
            "field names. Mutating: runs the gate path; paths_hint is the host " +
            "scene path.")]
        public static string Modify(
            int instance_id = 0,
            string path = null,
            string name = null,
            string module = null,
            string fields_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return PathRequired();

            if (string.IsNullOrWhiteSpace(module))
                return ParticleSystemJson.Error("missing_parameter",
                    "'module' is required. Valid: main, emission, shape, " +
                    "color_over_lifetime, size_over_lifetime, rotation_over_lifetime, " +
                    "noise, collision, trails, renderer.");

            if (string.IsNullOrWhiteSpace(fields_json))
                return ParticleSystemJson.Error("missing_parameter",
                    "'fields_json' is required (a JSON object of {field: value} entries).");

            var fields = ParseFieldsObject(fields_json);
            if (fields == null)
                return ParticleSystemJson.Error("invalid_fields_json",
                    "'fields_json' must be a JSON object of {field: value} entries.");

            var ps = ResolvePs(instance_id, path, name, out var resolveError);
            if (ps == null) return resolveError;

            var applied = new List<string>();
            var unknown = new List<string>();
            var errors = new List<string>();

            // Each module is a read-only struct property on the ParticleSystem
            // (e.g. ps.main), but the struct carries an internal ref to the
            // system — so mutating a field on the local writes through to the
            // live component directly. Do NOT try to assign the module back to
            // ps (the property has no setter; CS0200). Fields are dispatched by
            // name; unknown fields are collected (not fatal).
            var normalized = module.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "main":
                {
                    var m = ps.main;
                    ApplyMainFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "emission":
                {
                    var m = ps.emission;
                    ApplyEmissionFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "shape":
                {
                    var m = ps.shape;
                    ApplyShapeFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "color_over_lifetime":
                {
                    var m = ps.colorOverLifetime;
                    ApplyColorOverLifetimeFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "size_over_lifetime":
                {
                    var m = ps.sizeOverLifetime;
                    ApplySizeOverLifetimeFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "rotation_over_lifetime":
                {
                    var m = ps.rotationOverLifetime;
                    ApplyRotationOverLifetimeFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "noise":
                {
                    var m = ps.noise;
                    ApplyNoiseFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "collision":
                {
                    var m = ps.collision;
                    ApplyCollisionFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "trails":
                {
                    var m = ps.trails;
                    ApplyTrailsFields(m, fields, applied, unknown, errors);
                    break;
                }
                case "renderer":
                {
                    var renderer = ps.GetComponent<ParticleSystemRenderer>();
                    if (renderer == null)
                        return ParticleSystemJson.Error("component_not_found",
                            "Target has no ParticleSystemRenderer (cannot patch the 'renderer' module).");
                    ApplyRendererFields(renderer, fields, applied, unknown, errors);
                    EditorUtility.SetDirty(renderer);
                    break;
                }
                default:
                    return ParticleSystemJson.Error("invalid_module",
                        $"Unknown module '{module}'. Valid: main, emission, shape, " +
                        "color_over_lifetime, size_over_lifetime, rotation_over_lifetime, " +
                        "noise, collision, trails, renderer.");
            }

            EditorUtility.SetDirty(ps);
            EditorUtility.SetDirty(ps.gameObject);

            var sb = new StringBuilder(256);
            sb.Append("\"modify\":{");
            sb.Append("\"module\":").Append(ParticleSystemJson.Esc(normalized)).Append(',');
            sb.Append("\"appliedFields\":").Append(JsonArray(applied)).Append(',');
            sb.Append("\"unknownFields\":").Append(JsonArray(unknown)).Append(',');
            sb.Append("\"errors\":").Append(JsonArray(errors));
            sb.Append('}');
            return ParticleSystemJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Resolve helpers
        // =====================================================================

        private static ParticleSystem ResolvePs(int instanceId, string path, string name, out string errorEnvelope)
        {
            errorEnvelope = null;
            GameObject host = null;
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject goById) host = goById;
            }
            if (host == null && !string.IsNullOrEmpty(path))
                host = FindByPath(path);
            if (host == null && !string.IsNullOrEmpty(name))
                host = FindByName(name);

            if (host == null)
            {
                errorEnvelope = ParticleSystemJson.Error("target_not_found",
                    "No GameObject resolved. Address by instance_id > path > name.");
                return null;
            }

            var ps = host.GetComponent<ParticleSystem>();
            if (ps == null)
            {
                errorEnvelope = ParticleSystemJson.Error("component_not_found",
                    "Target has no ParticleSystem. Add one in the Editor first " +
                    "(or use component_add with 'ParticleSystem').");
                return null;
            }
            return ps;
        }

        private static string PathRequired()
            => ParticleSystemJson.Error("paths_hint_required",
                "particle_system_modify is mutating; pass a non-empty paths_hint " +
                "scoped to the host scene path.");

        private static GameObject FindByPath(string path)
        {
            var parts = path.Split('/');
            var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
            foreach (var root in roots)
            {
                if (root.gameObject.name == parts[0])
                {
                    var current = root.gameObject;
                    bool match = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var child = current.transform.Find(parts[i]);
                        if (child == null) { match = false; break; }
                        current = child.gameObject;
                    }
                    if (match) return current;
                }
            }
            return null;
        }

        private static GameObject FindByName(string name)
        {
            var roots = Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
            foreach (var root in roots)
                if (root.gameObject.name == name) return root.gameObject;
            return null;
        }

        // =====================================================================
        // Module serializers (read side)
        // =====================================================================

        private static void AppendRuntimeState(StringBuilder sb, ParticleSystem ps)
        {
            // No trailing comma — module emitters add a leading comma before
            // their own key, so the runtime block can be the last entry when
            // no modules are requested.
            sb.Append("\"runtime\":{");
            sb.Append("\"isPlaying\":").Append(ps.isPlaying ? "true" : "false").Append(',');
            sb.Append("\"isPaused\":").Append(ps.isPaused ? "true" : "false").Append(',');
            sb.Append("\"isEmitting\":").Append(ps.isEmitting ? "true" : "false").Append(',');
            sb.Append("\"isStopped\":").Append(ps.isStopped ? "true" : "false").Append(',');
            sb.Append("\"particleCount\":").Append(ps.particleCount).Append(',');
            sb.Append("\"time\":").Append(ps.time.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static void AppendMain(StringBuilder sb, ParticleSystem.MainModule m)
        {
            sb.Append('{');
            sb.Append("\"duration\":").Append(m.duration.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"loop\":").Append(m.loop ? "true" : "false").Append(',');
            sb.Append("\"prewarm\":").Append(m.prewarm ? "true" : "false").Append(',');
            sb.Append("\"startDelay\":").Append(EscMinMaxCurve(m.startDelay)).Append(',');
            sb.Append("\"startLifetime\":").Append(EscMinMaxCurve(m.startLifetime)).Append(',');
            sb.Append("\"startSpeed\":").Append(EscMinMaxCurve(m.startSpeed)).Append(',');
            sb.Append("\"startSize3D\":").Append(m.startSize3D ? "true" : "false").Append(',');
            sb.Append("\"startSize\":").Append(EscMinMaxCurve(m.startSize)).Append(',');
            sb.Append("\"startRotation3D\":").Append(m.startRotation3D ? "true" : "false").Append(',');
            sb.Append("\"startRotation\":").Append(EscMinMaxCurve(m.startRotation)).Append(',');
            sb.Append("\"startColor\":").Append(EscMinMaxGradient(m.startColor)).Append(',');
            sb.Append("\"gravityModifier\":").Append(EscMinMaxCurve(m.gravityModifier)).Append(',');
            sb.Append("\"simulationSpace\":").Append(ParticleSystemJson.Esc(m.simulationSpace.ToString())).Append(',');
            sb.Append("\"scalingMode\":").Append(ParticleSystemJson.Esc(m.scalingMode.ToString())).Append(',');
            sb.Append("\"playOnAwake\":").Append(m.playOnAwake ? "true" : "false").Append(',');
            sb.Append("\"maxParticles\":").Append(m.maxParticles);
            sb.Append('}');
        }

        private static void AppendEmission(StringBuilder sb, ParticleSystem.EmissionModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"rateOverTime\":").Append(EscMinMaxCurve(m.rateOverTime)).Append(',');
            sb.Append("\"rateOverDistance\":").Append(EscMinMaxCurve(m.rateOverDistance)).Append(',');
            sb.Append("\"burstCount\":").Append(m.burstCount);
            sb.Append('}');
        }

        private static void AppendShape(StringBuilder sb, ParticleSystem.ShapeModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"shapeType\":").Append(ParticleSystemJson.Esc(m.shapeType.ToString())).Append(',');
            sb.Append("\"radius\":").Append(m.radius.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"radiusThickness\":").Append(m.radiusThickness.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"angle\":").Append(m.angle.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"arc\":").Append(m.arc.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"position\":").Append(ParticleSystemJson.Vec3(m.position)).Append(',');
            sb.Append("\"rotation\":").Append(ParticleSystemJson.Vec3(m.rotation)).Append(',');
            sb.Append("\"scale\":").Append(ParticleSystemJson.Vec3(m.scale));
            sb.Append('}');
        }

        private static void AppendColorOverLifetime(StringBuilder sb, ParticleSystem.ColorOverLifetimeModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"color\":").Append(EscMinMaxGradient(m.color));
            sb.Append('}');
        }

        private static void AppendSizeOverLifetime(StringBuilder sb, ParticleSystem.SizeOverLifetimeModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"size\":").Append(EscMinMaxCurve(m.size)).Append(',');
            sb.Append("\"separateAxes\":").Append(m.separateAxes ? "true" : "false");
            sb.Append('}');
        }

        private static void AppendRotationOverLifetime(StringBuilder sb, ParticleSystem.RotationOverLifetimeModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"separateAxes\":").Append(m.separateAxes ? "true" : "false").Append(',');
            sb.Append("\"x\":").Append(EscMinMaxCurve(m.x)).Append(',');
            sb.Append("\"y\":").Append(EscMinMaxCurve(m.y)).Append(',');
            sb.Append("\"z\":").Append(EscMinMaxCurve(m.z));
            sb.Append('}');
        }

        private static void AppendNoise(StringBuilder sb, ParticleSystem.NoiseModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"strength\":").Append(EscMinMaxCurve(m.strength)).Append(',');
            sb.Append("\"frequency\":").Append(m.frequency.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"scrollSpeed\":").Append(EscMinMaxCurve(m.scrollSpeed)).Append(',');
            sb.Append("\"damping\":").Append(m.damping ? "true" : "false").Append(',');
            sb.Append("\"octaveCount\":").Append(m.octaveCount).Append(',');
            sb.Append("\"quality\":").Append(ParticleSystemJson.Esc(m.quality.ToString()));
            sb.Append('}');
        }

        private static void AppendCollision(StringBuilder sb, ParticleSystem.CollisionModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"type\":").Append(ParticleSystemJson.Esc(m.type.ToString())).Append(',');
            sb.Append("\"dampen\":").Append(EscMinMaxCurve(m.dampen)).Append(',');
            sb.Append("\"bounce\":").Append(EscMinMaxCurve(m.bounce)).Append(',');
            sb.Append("\"lifetimeLoss\":").Append(EscMinMaxCurve(m.lifetimeLoss));
            sb.Append('}');
        }

        private static void AppendTrails(StringBuilder sb, ParticleSystem.TrailModule m)
        {
            sb.Append('{');
            sb.Append("\"enabled\":").Append(m.enabled ? "true" : "false").Append(',');
            sb.Append("\"ratio\":").Append(EscMinMaxCurve(m.ratio)).Append(',');
            sb.Append("\"lifetime\":").Append(EscMinMaxCurve(m.lifetime)).Append(',');
            sb.Append("\"widthOverTrail\":").Append(EscMinMaxCurve(m.widthOverTrail)).Append(',');
            sb.Append("\"colorOverLifetime\":").Append(EscMinMaxGradient(m.colorOverLifetime)).Append(',');
            sb.Append("\"colorOverTrail\":").Append(EscMinMaxGradient(m.colorOverTrail));
            sb.Append('}');
        }

        private static void AppendRenderer(StringBuilder sb, ParticleSystemRenderer r)
        {
            sb.Append('{');
            sb.Append("\"renderMode\":").Append(ParticleSystemJson.Esc(r.renderMode.ToString())).Append(',');
            sb.Append("\"alignment\":").Append(ParticleSystemJson.Esc(r.alignment.ToString())).Append(',');
            sb.Append("\"sortMode\":").Append(ParticleSystemJson.Esc(r.sortMode.ToString())).Append(',');
            sb.Append("\"maskInteraction\":").Append(ParticleSystemJson.Esc(r.maskInteraction.ToString())).Append(',');
            sb.Append("\"material\":").Append(r.sharedMaterial == null
                ? "null"
                : ParticleSystemJson.Esc(r.sharedMaterial.name));
            sb.Append('}');
        }

        // =====================================================================
        // Per-module field application (write side)
        // =====================================================================
        //
        // Each module mutator walks the parsed field object and dispatches known
        // field names. Scalar parsing via Parse*() returns false on a bad
        // value, which lands in the `errors` list with the field name.

        private static void ApplyMainFields(ParticleSystem.MainModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "duration": // read-only; report
                        errors.Add($"main.duration is read-only.");
                        break;
                    case "loop":
                        if (TryBool(kv.Value, out var loop)) { m.loop = loop; applied.Add(kv.Key); }
                        else errors.Add($"main.loop expects a bool, got '{kv.Value}'.");
                        break;
                    case "prewarm":
                        if (TryBool(kv.Value, out var prewarm)) { m.prewarm = prewarm; applied.Add(kv.Key); }
                        else errors.Add($"main.prewarm expects a bool, got '{kv.Value}'.");
                        break;
                    case "startDelay":
                        if (TryFloat(kv.Value, out var delay)) { m.startDelay = delay; applied.Add(kv.Key); }
                        else errors.Add($"main.startDelay expects a float, got '{kv.Value}'.");
                        break;
                    case "startLifetime":
                        if (TryFloat(kv.Value, out var life)) { m.startLifetime = life; applied.Add(kv.Key); }
                        else errors.Add($"main.startLifetime expects a float, got '{kv.Value}'.");
                        break;
                    case "startSpeed":
                        if (TryFloat(kv.Value, out var speed)) { m.startSpeed = speed; applied.Add(kv.Key); }
                        else errors.Add($"main.startSpeed expects a float, got '{kv.Value}'.");
                        break;
                    case "startSize":
                        if (TryFloat(kv.Value, out var size)) { m.startSize = size; applied.Add(kv.Key); }
                        else errors.Add($"main.startSize expects a float, got '{kv.Value}'.");
                        break;
                    case "startSize3D":
                        if (TryBool(kv.Value, out var s3d)) { m.startSize3D = s3d; applied.Add(kv.Key); }
                        else errors.Add($"main.startSize3D expects a bool, got '{kv.Value}'.");
                        break;
                    case "startRotation3D":
                        if (TryBool(kv.Value, out var r3d)) { m.startRotation3D = r3d; applied.Add(kv.Key); }
                        else errors.Add($"main.startRotation3D expects a bool, got '{kv.Value}'.");
                        break;
                    case "startRotation":
                        if (TryFloat(kv.Value, out var rot)) { m.startRotation = rot; applied.Add(kv.Key); }
                        else errors.Add($"main.startRotation expects a float (radians), got '{kv.Value}'.");
                        break;
                    case "gravityModifier":
                        if (TryFloat(kv.Value, out var grav)) { m.gravityModifier = grav; applied.Add(kv.Key); }
                        else errors.Add($"main.gravityModifier expects a float, got '{kv.Value}'.");
                        break;
                    case "playOnAwake":
                        if (TryBool(kv.Value, out var poa)) { m.playOnAwake = poa; applied.Add(kv.Key); }
                        else errors.Add($"main.playOnAwake expects a bool, got '{kv.Value}'.");
                        break;
                    case "maxParticles":
                        if (TryInt(kv.Value, out var max)) { m.maxParticles = max; applied.Add(kv.Key); }
                        else errors.Add($"main.maxParticles expects an int, got '{kv.Value}'.");
                        break;
                    case "simulationSpace":
                        if (TryEnum(kv.Value, out ParticleSystemSimulationSpace space)) { m.simulationSpace = space; applied.Add(kv.Key); }
                        else errors.Add($"main.simulationSpace invalid: '{kv.Value}'. Valid: Local, World, Custom.");
                        break;
                    case "scalingMode":
                        if (TryEnum(kv.Value, out ParticleSystemScalingMode sm)) { m.scalingMode = sm; applied.Add(kv.Key); }
                        else errors.Add($"main.scalingMode invalid: '{kv.Value}'. Valid: Hierarchy, Local, Shape.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyEmissionFields(ParticleSystem.EmissionModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"emission.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "rateOverTime":
                        if (TryFloat(kv.Value, out var rTime)) { m.rateOverTime = rTime; applied.Add(kv.Key); }
                        else errors.Add($"emission.rateOverTime expects a float, got '{kv.Value}'.");
                        break;
                    case "rateOverDistance":
                        if (TryFloat(kv.Value, out var rDist)) { m.rateOverDistance = rDist; applied.Add(kv.Key); }
                        else errors.Add($"emission.rateOverDistance expects a float, got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyShapeFields(ParticleSystem.ShapeModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"shape.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "shapeType":
                        if (TryEnum(kv.Value, out ParticleSystemShapeType st)) { m.shapeType = st; applied.Add(kv.Key); }
                        else errors.Add($"shape.shapeType invalid: '{kv.Value}' (see particle_system_get for valid values).");
                        break;
                    case "radius":
                        if (TryFloat(kv.Value, out var radius)) { m.radius = radius; applied.Add(kv.Key); }
                        else errors.Add($"shape.radius expects a float, got '{kv.Value}'.");
                        break;
                    case "radiusThickness":
                        if (TryFloat(kv.Value, out var rt)) { m.radiusThickness = rt; applied.Add(kv.Key); }
                        else errors.Add($"shape.radiusThickness expects a float 0-1, got '{kv.Value}'.");
                        break;
                    case "angle":
                        if (TryFloat(kv.Value, out var angle)) { m.angle = angle; applied.Add(kv.Key); }
                        else errors.Add($"shape.angle expects a float (degrees), got '{kv.Value}'.");
                        break;
                    case "arc":
                        if (TryFloat(kv.Value, out var arc)) { m.arc = arc; applied.Add(kv.Key); }
                        else errors.Add($"shape.arc expects a float (degrees), got '{kv.Value}'.");
                        break;
                    case "position":
                        if (TryVec3(kv.Value, out var pos)) { m.position = pos; applied.Add(kv.Key); }
                        else errors.Add($"shape.position expects 'x,y,z', got '{kv.Value}'.");
                        break;
                    case "rotation":
                        if (TryVec3(kv.Value, out var rot)) { m.rotation = rot; applied.Add(kv.Key); }
                        else errors.Add($"shape.rotation expects 'x,y,z', got '{kv.Value}'.");
                        break;
                    case "scale":
                        if (TryVec3(kv.Value, out var scale)) { m.scale = scale; applied.Add(kv.Key); }
                        else errors.Add($"shape.scale expects 'x,y,z', got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyColorOverLifetimeFields(ParticleSystem.ColorOverLifetimeModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"color_over_lifetime.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
                // color gradient editing is intentionally out of scope —
                // agents should use execute_csharp for fine-grained gradient
                // authoring; the get/read side still reports the current value.
            }
        }

        private static void ApplySizeOverLifetimeFields(ParticleSystem.SizeOverLifetimeModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"size_over_lifetime.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "separateAxes":
                        if (TryBool(kv.Value, out var sep)) { m.separateAxes = sep; applied.Add(kv.Key); }
                        else errors.Add($"size_over_lifetime.separateAxes expects a bool, got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyRotationOverLifetimeFields(ParticleSystem.RotationOverLifetimeModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"rotation_over_lifetime.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "separateAxes":
                        if (TryBool(kv.Value, out var sep)) { m.separateAxes = sep; applied.Add(kv.Key); }
                        else errors.Add($"rotation_over_lifetime.separateAxes expects a bool, got '{kv.Value}'.");
                        break;
                    case "x":
                        if (TryFloat(kv.Value, out var x)) { m.x = x; applied.Add(kv.Key); }
                        else errors.Add($"rotation_over_lifetime.x expects a float (rad/s), got '{kv.Value}'.");
                        break;
                    case "y":
                        if (TryFloat(kv.Value, out var y)) { m.y = y; applied.Add(kv.Key); }
                        else errors.Add($"rotation_over_lifetime.y expects a float (rad/s), got '{kv.Value}'.");
                        break;
                    case "z":
                        if (TryFloat(kv.Value, out var z)) { m.z = z; applied.Add(kv.Key); }
                        else errors.Add($"rotation_over_lifetime.z expects a float (rad/s), got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyNoiseFields(ParticleSystem.NoiseModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"noise.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "strength":
                        if (TryFloat(kv.Value, out var strength)) { m.strength = strength; applied.Add(kv.Key); }
                        else errors.Add($"noise.strength expects a float, got '{kv.Value}'.");
                        break;
                    case "frequency":
                        if (TryFloat(kv.Value, out var freq)) { m.frequency = freq; applied.Add(kv.Key); }
                        else errors.Add($"noise.frequency expects a float, got '{kv.Value}'.");
                        break;
                    case "scrollSpeed":
                        if (TryFloat(kv.Value, out var speed)) { m.scrollSpeed = speed; applied.Add(kv.Key); }
                        else errors.Add($"noise.scrollSpeed expects a float, got '{kv.Value}'.");
                        break;
                    case "damping":
                        if (TryBool(kv.Value, out var damp)) { m.damping = damp; applied.Add(kv.Key); }
                        else errors.Add($"noise.damping expects a bool, got '{kv.Value}'.");
                        break;
                    case "octaveCount":
                        if (TryInt(kv.Value, out var oct)) { m.octaveCount = oct; applied.Add(kv.Key); }
                        else errors.Add($"noise.octaveCount expects an int, got '{kv.Value}'.");
                        break;
                    case "quality":
                        if (TryEnum(kv.Value, out ParticleSystemNoiseQuality q)) { m.quality = q; applied.Add(kv.Key); }
                        else errors.Add($"noise.quality invalid: '{kv.Value}'. Valid: Low, Medium, High.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyCollisionFields(ParticleSystem.CollisionModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"collision.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "type":
                        if (TryEnum(kv.Value, out ParticleSystemCollisionType t)) { m.type = t; applied.Add(kv.Key); }
                        else errors.Add($"collision.type invalid: '{kv.Value}'. Valid: Planes, World.");
                        break;
                    case "dampen":
                        if (TryFloat(kv.Value, out var damp)) { m.dampen = damp; applied.Add(kv.Key); }
                        else errors.Add($"collision.dampen expects a float 0-1, got '{kv.Value}'.");
                        break;
                    case "bounce":
                        if (TryFloat(kv.Value, out var bounce)) { m.bounce = bounce; applied.Add(kv.Key); }
                        else errors.Add($"collision.bounce expects a float, got '{kv.Value}'.");
                        break;
                    case "lifetimeLoss":
                        if (TryFloat(kv.Value, out var loss)) { m.lifetimeLoss = loss; applied.Add(kv.Key); }
                        else errors.Add($"collision.lifetimeLoss expects a float 0-1, got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyTrailsFields(ParticleSystem.TrailModule m,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "enabled":
                        if (TryBool(kv.Value, out var en)) { m.enabled = en; applied.Add(kv.Key); }
                        else errors.Add($"trails.enabled expects a bool, got '{kv.Value}'.");
                        break;
                    case "ratio":
                        if (TryFloat(kv.Value, out var ratio)) { m.ratio = ratio; applied.Add(kv.Key); }
                        else errors.Add($"trails.ratio expects a float 0-1, got '{kv.Value}'.");
                        break;
                    case "lifetime":
                        if (TryFloat(kv.Value, out var life)) { m.lifetime = life; applied.Add(kv.Key); }
                        else errors.Add($"trails.lifetime expects a float, got '{kv.Value}'.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        private static void ApplyRendererFields(ParticleSystemRenderer r,
            Dictionary<string, string> fields,
            List<string> applied, List<string> unknown, List<string> errors)
        {
            foreach (var kv in fields)
            {
                switch (kv.Key)
                {
                    case "renderMode":
                        if (TryEnum(kv.Value, out ParticleSystemRenderMode rm)) { r.renderMode = rm; applied.Add(kv.Key); }
                        else errors.Add($"renderer.renderMode invalid: '{kv.Value}'. Valid: Billboard, Mesh, HorizontalBillboard, VerticalBillboard, Stretch3D.");
                        break;
                    case "alignment":
                        if (TryEnum(kv.Value, out ParticleSystemRenderSpace al)) { r.alignment = al; applied.Add(kv.Key); }
                        else errors.Add($"renderer.alignment invalid: '{kv.Value}'. Valid: View, World, Local, Facing.");
                        break;
                    case "sortMode":
                        if (TryEnum(kv.Value, out ParticleSystemSortMode sm)) { r.sortMode = sm; applied.Add(kv.Key); }
                        else errors.Add($"renderer.sortMode invalid: '{kv.Value}'. Valid: None, Distance, OldestInFront, YoungestInFront.");
                        break;
                    case "maskInteraction":
                        if (TryEnum(kv.Value, out SpriteMaskInteraction mi)) { r.maskInteraction = mi; applied.Add(kv.Key); }
                        else errors.Add($"renderer.maskInteraction invalid: '{kv.Value}'. Valid: None, VisibleInsideMask, VisibleOutsideMask.");
                        break;
                    default:
                        unknown.Add(kv.Key);
                        break;
                }
            }
        }

        // =====================================================================
        // JSON parsing helpers (no external deps)
        // =====================================================================

        // Parse a flat JSON object of {key: value} into a Dictionary. Values
        // are kept as their raw JSON tokens (string scalars quoted, numbers /
        // bools bare). The apply routines interpret each token via the typed
        // Try* helpers.
        private static Dictionary<string, string> ParseFieldsObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return null;

            var result = new Dictionary<string, string>();
            // Strip the outer braces.
            var body = trimmed.Substring(1, trimmed.Length - 2).Trim();
            int i = 0;
            while (i < body.Length)
            {
                // Skip leading commas / whitespace.
                while (i < body.Length && (body[i] == ',' || char.IsWhiteSpace(body[i]))) i++;
                if (i >= body.Length) break;

                // Read the key (must be a string).
                if (body[i] != '"') return null;
                int keyStart = i + 1;
                int keyEnd = keyStart;
                while (keyEnd < body.Length)
                {
                    if (body[keyEnd] == '\\' && keyEnd + 1 < body.Length) { keyEnd += 2; continue; }
                    if (body[keyEnd] == '"') break;
                    keyEnd++;
                }
                if (keyEnd >= body.Length) return null;
                var key = UnescapeString(body.Substring(keyStart, keyEnd - keyStart));
                i = keyEnd + 1;

                // Skip whitespace + colon.
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length || body[i] != ':') return null;
                i++;
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length) return null;

                // Read the value token.
                string valueToken;
                if (body[i] == '"')
                {
                    int vStart = i + 1;
                    int vEnd = vStart;
                    while (vEnd < body.Length)
                    {
                        if (body[vEnd] == '\\' && vEnd + 1 < body.Length) { vEnd += 2; continue; }
                        if (body[vEnd] == '"') break;
                        vEnd++;
                    }
                    if (vEnd >= body.Length) return null;
                    valueToken = "\"" + body.Substring(vStart, vEnd - vStart) + "\"";
                    i = vEnd + 1;
                }
                else if (body[i] == '{' || body[i] == '[')
                {
                    // Skip nested structures — modify accepts scalars only.
                    return null;
                }
                else
                {
                    int vStart = i;
                    while (i < body.Length && body[i] != ',' && body[i] != '}') i++;
                    valueToken = body.Substring(vStart, i - vStart).Trim();
                }

                result[key] = valueToken;
            }
            return result;
        }

        private static string UnescapeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static bool TryBool(string token, out bool value)
        {
            value = false;
            if (token == "true") { value = true; return true; }
            if (token == "false") { value = false; return true; }
            return false;
        }

        private static bool TryFloat(string token, out float value)
        {
            value = 0f;
            // Allow JSON-quoted scalars too.
            var raw = Unquote(token);
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryInt(string token, out int value)
        {
            value = 0;
            var raw = Unquote(token);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryVec3(string token, out Vector3 value)
        {
            value = Vector3.zero;
            var raw = Unquote(token);
            var parts = raw.Split(',');
            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryEnum<T>(string token, out T value) where T : struct
        {
            value = default;
            var raw = Unquote(token);
            return System.Enum.TryParse(raw, true, out value);
        }

        private static string Unquote(string token)
        {
            if (token != null && token.Length >= 2 &&
                token[0] == '"' && token[token.Length - 1] == '"')
                return UnescapeString(token.Substring(1, token.Length - 2));
            return token;
        }

        private static string JsonArray(List<string> items)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ParticleSystemJson.Esc(items[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // =====================================================================
        // MinMaxCurve / MinMaxGradient serializers
        // =====================================================================

        // Summarize a MinMaxCurve without serializing the AnimationCurve. The
        // agent-facing surface only accepts constant scalars on write; the read
        // side reports the mode + constant so the agent can reason about the
        // current state. Curve / random-between-two values are reported as
        // their constantMin / constantMax (which is meaningful for
        // TwoConstants; for curves we fall back to the curve constant).
        private static string EscMinMaxCurve(ParticleSystem.MinMaxCurve c)
        {
            var sb = new StringBuilder(64);
            sb.Append("{\"mode\":").Append(ParticleSystemJson.Esc(c.mode.ToString())).Append(',');
            sb.Append("\"constant\":").Append(c.constant.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"constantMin\":").Append(c.constantMin.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"constantMax\":").Append(c.constantMax.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"curveMin\":").Append("null").Append(',');
            sb.Append("\"curveMax\":").Append("null");
            sb.Append('}');
            return sb.ToString();
        }

        private static string EscMinMaxGradient(ParticleSystem.MinMaxGradient g)
        {
            var sb = new StringBuilder(64);
            sb.Append("{\"mode\":").Append(ParticleSystemJson.Esc(g.mode.ToString())).Append(',');
            sb.Append("\"color\":").Append(ColorJson(g.color)).Append(',');
            sb.Append("\"colorMin\":").Append(ColorJson(g.colorMin)).Append(',');
            sb.Append("\"colorMax\":").Append(ColorJson(g.colorMax));
            sb.Append('}');
            return sb.ToString();
        }

        private static string ColorJson(Color c)
            => $"[{c.r},{c.g},{c.b},{c.a}]";
    }
}
#endif
