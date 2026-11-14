// M20 Plan 6 / T20.6.1 — Cinemachine embedded domain tools.
//
// REFLECTION-GATED (the canonical version-split case named in M18 Plan 1
// T18.1.1 task 5). Unlike the compile-gated packs (Splines, Timeline, Tilemap),
// this assembly ALWAYS compiles: the sub-asmdef has no `defineConstraints` and
// no Cinemachine package reference. Cinemachine types are resolved at call time
// via reflection (Type.GetType / AppDomain scan). The detection layer
// distinguishes:
//
//   - Cinemachine 3.x (Unity.Cinemachine.CinemachineCamera + CinemachineBrain,
//     component-pipeline: Body / Aim / Noise components) — supported.
//   - Cinemachine 2.x (Cinemachine.CinemachineVirtualCamera + CinemachineBrain,
//     component pipeline is identical but the camera class differs) —
//     rejected with a clear "3.x required" error.
//   - Package absent — rejected with a clear "package missing" error.
//
// The 3.x public API is targeted on Unity 6; when 2.x is detected, tools
// surface the install/upgrade guidance error rather than attempting the 2.x
// pipeline. Per the bridge AGENTS.md §Embedded domain tools, reflection is
// reserved for version-split APIs — Cinemachine is exactly that case.
using System.Text;
using UnityEngine;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.CinemachineExt
{
    // Shared helpers for the Cinemachine reflection-gated domain tools.
    //
    // JSON envelope builders + the float/vector parsing shared with every
    // domain pack. Mirrors the SplinesJson / ProBuilderJson helper shape so
    // the packs read consistently.
    //
    // Naming: tool ids follow `unity_open_mcp_cinemachine_<action>`
    // (snake_case domain prefix).
    internal static class CinemachineJson
    {
        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // A-R4 — render floats with invariant culture. Bare interpolation
        // ($"{v.x}") uses the OS culture, so de/fr/ru locales emit "1,5" and
        // the vector array parses as valid JSON with the wrong element count.
        public static string Vec3(Vector3 v)
            => "[" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + "]";

        public static string Num(float v)
            => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Target resolver for Cinemachine tools. Mirrors the bridge's GameObject
    // addressing convention (instance_id > path > name).
    internal static class CinemachineTargets
    {
        // Unity 6.5 made Object.GetInstanceID() / InstanceIDToObject a CS0619
        // error and widened EntityId to 8 BYTES — values like
        // 568105589213726936 no longer fit in int. Route resolution through
        // the bridge's central InstanceId helper (long-wide, with the
        // UNITY_6000_5_OR_NEWER dance in one audited place) instead of a local
        // pair: the old (int)ToULong / FromULong((uint)id) round-trip silently
        // truncated 8-byte EntityIds on Unity 6000.5+. Emission goes through
        // InstanceId.ToJson at the call sites (the canonical quoted-string
        // wire form).
        public static Object ObjectForId(long id) => InstanceId.ToObject(id);

        // The shared resolver (TypedTargets): loaded scenes and the open
        // prefab stage, scene_path / paths_hint narrowing, refused instance ids
        // (ambiguous_path and friends: see TypedTargets.RefusalJson); a
        // one-segment path also matches a nested GameObject of that name.
        public static GameObject Resolve(long instanceId, string path, string name)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(instanceId, path, name);

        public static GameObject FindByPath(string path)
            => UnityOpenMcpBridge.TypedTools.TypedTargets.ResolveLenient(0, path, null);

        // Canonical: relative to the prefab root for a prefab-stage object.
        public static string BuildPath(GameObject go) => UnityOpenMcpBridge.TypedTools.TypedTargets.HierarchyPath(go);
    }

    // Cinemachine 2.x/3.x version detection — the reflection layer. Resolves
    // the package presence + which major shipped, surfacing clean error
    // envelopes when the supported shape is not available. Cached after the
    // first successful resolve (Cinemachine presence does not change at
    // runtime in an Editor session).
    internal static class CinemachineVersion
    {
        // The 3.x camera type. `Type.GetType` needs the assembly-qualified name
        // for types outside the executing assembly; we scan AppDomain for
        // robustness (the assembly name is `Unity.Cinemachine`).
        private static System.Type s_CameraType;
        private static System.Type s_BrainType;
        private static bool s_Absent;        // package definitely missing
        private static bool s_Resolved;

        private static void ResolveTypes()
        {
            if (s_Resolved) return;
            s_Resolved = true;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name == "Unity.Cinemachine")
                {
                    s_CameraType = asm.GetType("Unity.Cinemachine.CinemachineCamera");
                    s_BrainType = asm.GetType("Unity.Cinemachine.CinemachineBrain");
                }
                else if (name == "Cinemachine")
                {
                    // 2.x assembly — present but unsupported. Mark absent so
                    // callers see the 3.x-required error rather than attempting
                    // the 2.x pipeline.
                    if (s_CameraType == null) s_Absent = true;
                }
            }
            if (s_CameraType == null && s_BrainType == null) s_Absent = true;
        }

        /// <summary>The 3.x CinemachineCamera type, or null when unsupported.</summary>
        public static System.Type CameraType
        {
            get { ResolveTypes(); return s_CameraType; }
        }

        /// <summary>The 3.x CinemachineBrain type, or null when unsupported.</summary>
        public static System.Type BrainType
        {
            get { ResolveTypes(); return s_BrainType; }
        }

        /// <summary>True when Cinemachine (any version) is installed.</summary>
        public static bool Installed
        {
            get { ResolveTypes(); return !s_Absent; }
        }

        /// <summary>True when the supported 3.x surface is available.</summary>
        public static bool Supported
        {
            get { ResolveTypes(); return s_CameraType != null; }
        }

        /// <summary>Clear the cached detection (used by EditMode tests).</summary>
        public static void ResetForTests()
        {
            s_CameraType = null;
            s_BrainType = null;
            s_Absent = false;
            s_Resolved = false;
        }

        // Surface the canonical "package missing" error envelope.
        public static string PackageMissingError()
            => CinemachineJson.Error("cinemachine_package_required",
                "Cinemachine package not found. Install `com.unity.cinemachine` " +
                "≥ 3.x via the Package Manager (Window > Package Manager).");

        // Surface the canonical "2.x detected" error envelope.
        public static string VersionTooOldError()
            => CinemachineJson.Error("cinemachine_3x_required",
                "Cinemachine 3.x required — Cinemachine 2.x is installed but " +
                "uses a different camera API (CinemachineVirtualCamera). Upgrade " +
                "`com.unity.cinemachine` to 3.x or later.");
    }
}
