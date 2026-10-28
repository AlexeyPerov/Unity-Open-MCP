// Target → screen point resolution for the device-input tools (touch, gesture).
//
// Mirrors inputsim_pointer's PointerTargets.FindByPath (root-anchored precedence,
// then trailing-segment match) without depending on uGUI: GameObject.Find returns
// an arbitrary first match, so this walks active transforms and reports 0 →
// target_not_found, >1 → ambiguous_target with candidate paths. RectTransforms
// resolve to their visual center (not the pivot); other objects project through
// Camera.main. Results are Screen pixels (bottom-left origin).
using System.Collections.Generic;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal static class DeviceTargets
    {
        internal static bool TryResolveTarget(
            string target, out Vector2 point, out (string Code, string Message) err)
        {
            point = default;
            err = default;
            if (string.IsNullOrEmpty(target))
            {
                err = ("no_target_or_screen_point",
                    "Provide either `target` (name/path) or both `screen_x` and `screen_y`.");
                return false;
            }

            var parts = target.Split('/');
            bool isPath = parts.Length > 1;
            var matches = new List<GameObject>();
            List<GameObject> anchored = isPath ? new List<GameObject>() : null;
            foreach (var t in SceneQuery.FindActiveTransforms())
            {
                if (t == null) continue;
                if (IsPathMatch(t, parts, isPath)) matches.Add(t.gameObject);
                if (isPath && IsRootAnchoredMatch(t, parts)) anchored.Add(t.gameObject);
            }
            if (isPath && anchored != null && anchored.Count > 0) matches = anchored;

            if (matches.Count == 0)
            {
                err = ("target_not_found", $"No active GameObject found for target '{target}'.");
                return false;
            }
            if (matches.Count > 1)
            {
                var candidates = new List<string>(System.Math.Min(matches.Count, 12));
                foreach (var m in matches)
                {
                    if (candidates.Count >= 12) break;
                    candidates.Add(BuildPath(m));
                }
                err = ("ambiguous_target",
                    $"Target '{target}' matches {matches.Count} active GameObjects: " +
                    string.Join(", ", candidates) + ". Pass the full path or screen coordinates.");
                return false;
            }
            point = ScreenPointOf(matches[0]);
            return true;
        }

        // target-first, else explicit screen coordinates.
        internal static bool ResolvePoint(
            string target, float? sx, float? sy, out Vector2 point, out (string Code, string Message) err)
        {
            point = default;
            err = default;
            if (!string.IsNullOrEmpty(target)) return TryResolveTarget(target, out point, out err);
            if (sx.HasValue && sy.HasValue)
            {
                point = new Vector2(sx.Value, sy.Value);
                return true;
            }
            err = ("no_target_or_screen_point",
                "Provide either `target` (name/path) or both `screen_x` and `screen_y`.");
            return false;
        }

        private static bool IsPathMatch(Transform t, string[] parts, bool isPath)
        {
            if (!isPath) return t.gameObject.name == parts[0];
            var cur = t;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (cur == null) return false;
                if (cur.gameObject.name != parts[i]) return false;
                cur = cur.parent;
            }
            return true;
        }

        private static bool IsRootAnchoredMatch(Transform t, string[] parts)
        {
            var cur = t;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (cur == null) return false;
                if (cur.gameObject.name != parts[i]) return false;
                cur = cur.parent;
            }
            return cur == null;
        }

        internal static string BuildPath(GameObject go)
        {
            if (go == null) return "";
            var sb = new System.Text.StringBuilder();
            var t = go.transform;
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        internal static Vector2 ScreenPointOf(GameObject go)
        {
            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                var canvas = go.GetComponentInParent<Canvas>();
                Camera cam = null;
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                return RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
            }
            var mainCam = Camera.main;
            return mainCam != null
                ? mainCam.WorldToScreenPoint(go.transform.position)
                : new Vector2(go.transform.position.x, go.transform.position.y);
        }
    }
}
