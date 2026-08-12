// Target + screen-point + interactability resolution for the uGUI pointer tool.
//
// Resolution model (target-first, per the tool contract), with the honesty fixes
// from feedback-input.md:
//   - `object_id` > `target` > `screen_x`/`screen_y`. object_id resolves via
//     InstanceId.ToObject (long, Unity 6000.5-safe); target resolves by name or
//     slash-path with duplicate detection (ambiguous_target) and partial-path
//     matching; screen-point raycasts through EventSystem.current.
//   - `ComputeInteractable` walks Selectable + ancestor CanvasGroup chain so the
//     response reports whether a click was actually possible (P2). The tool still
//     dispatches either way (occlusion-skipping is a feature), but no longer
//     reports a disabled/occluded click as an unqualified ok.
//   - `ScreenPointOf` returns the rect CENTER, not the pivot (P4) — so the
//     reported point is inside the element's visible art and safe to feed back.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal static class PointerTargets
    {
        // -----------------------------------------------------------------
        // Resolution
        // -----------------------------------------------------------------

        // Resolve by InstanceId (long — EntityId-safe on Unity 6000.5+). Wins
        // over target/screen. Returns null when the id is absent/unmapped.
        public static GameObject FindByInstanceId(long objectId)
        {
            if (objectId == 0) return null;
            var obj = InstanceId.ToObject(objectId);
            return obj as GameObject;
        }

        // Resolve a target by name or slash-separated hierarchy path, with
        // duplicate detection (P5) and partial-path matching. `GameObject.Find`
        // returns an arbitrary first match with no ambiguity signal; this walks
        // active transforms explicitly so it can report when a name is shared.
        //
        // Path semantics (feedback S7 — root-anchored precedence):
        //   - "Canvas/Panel/Button" — a root-anchored exact match is tried FIRST.
        //     Only when that yields nothing does the trailing-segment walk run,
        //     so a fully-qualified path that is unique at the scene root stays
        //     unique instead of also matching nested prefab instances whose
        //     hierarchy ends in "Panel/Button".
        //   - "Button" — single name; if >1 active object shares it, returns null
        //     and fills `candidates` for the ambiguous_target response.
        public static GameObject FindByPath(string target, List<string> candidates = null)
        {
            if (string.IsNullOrEmpty(target)) return null;
            candidates?.Clear();

            var parts = target.Split('/');
            bool isPath = parts.Length > 1;

            // Collect both strategies in one pass. S7: the root-anchored match
            // takes precedence over the trailing-segment walk when it yields any
            // result, so a unique fully-qualified path does not regress.
            var matches = new List<GameObject>();
            List<GameObject> anchored = isPath ? new List<GameObject>() : null;
            foreach (var t in SceneQuery.FindActiveTransforms())
            {
                if (IsMatch(t, parts, isPath))
                    matches.Add(t.gameObject);
                if (isPath && IsRootAnchoredMatch(t, parts))
                    anchored.Add(t.gameObject);
            }

            if (isPath && anchored != null && anchored.Count > 0)
                matches = anchored;

            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];

            // Ambiguous — collect candidate paths for the error. Caller decides
            // whether duplicates of a multi-segment path count (they usually do:
            // two prefabs instantiated with the same internal hierarchy).
            FillCandidates(candidates, matches);
            return null;
        }

        private static void FillCandidates(List<string> candidates, List<GameObject> matches)
        {
            if (candidates == null) return;
            foreach (var m in matches)
            {
                if (candidates.Count >= 12) break; // cap to keep payload sane
                candidates.Add(BuildPath(m));
            }
        }

        // Does `t` match the requested path/name via the trailing-segment walk?
        // For multi-segment paths this matches anywhere in the hierarchy (so a
        // path inside an instantiated prefab resolves without the scene root).
        private static bool IsMatch(Transform t, string[] parts, bool isPath)
        {
            if (!isPath)
                return t.gameObject.name == parts[0];

            // Trailing-segment match: walk up from t and compare parts in reverse.
            var cur = t;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (cur == null) return false;
                if (cur.gameObject.name != parts[i]) return false;
                cur = cur.parent;
            }
            return true;
        }

        // Root-anchored exact match: the trailing walk AND the topmost matched
        // node must be a scene root (no parent). After walking all parts in
        // reverse, `cur` is the parent of the parts[0] node — null iff parts[0]
        // is a root. (feedback S7)
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

        // -----------------------------------------------------------------
        // Screen-point computation (P4: rect center, not pivot)
        // -----------------------------------------------------------------

        public static Vector2 ScreenPointOf(GameObject go)
        {
            if (go == null) return Vector2.zero;
            var cam = CameraFor(go);
            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                // rect.center, transformed to world space, THEN to screen — the
                // pivot's world position (the old behavior) sits in a corner for
                // off-pivot RectTransforms and can fall outside the visible art.
                Vector3 worldCenter = rt.TransformPoint(rt.rect.center);
                return RectTransformUtility.WorldToScreenPoint(cam, worldCenter);
            }
            if (cam != null)
                return cam.WorldToScreenPoint(go.transform.position);
            return new Vector2(go.transform.position.x, go.transform.position.y);
        }

        // The camera that resolves this object's canvas / world. Overlay canvas
        // → null (RectTransformUtility treats null as overlay). Screen-camera /
        // world-space → canvas.worldCamera, falling back to Camera.main. Non-UI
        // → Camera.main.
        public static Camera CameraFor(GameObject go)
        {
            var canvas = go.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                return canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            }
            return Camera.main;
        }

        // -----------------------------------------------------------------
        // Raycast (screen-point fallback path)
        // -----------------------------------------------------------------

        public static GameObject RaycastTop(Vector2 screenPoint, out List<RaycastResult> results)
        {
            results = null;
            var es = EventSystem.current;
            if (es == null) return null;

            var ped = new PointerEventData(es) { position = screenPoint };
            results = new List<RaycastResult>();
            es.RaycastAll(ped, results);
            return results.Count > 0 ? results[0].gameObject : null;
        }

        // Is `other` the same as `target` or a descendant of it? Used to decide
        // whether a raycast top-hit occludes the target (a hit on the target or
        // its child is not occlusion).
        public static bool IsSameOrDescendant(GameObject ancestor, GameObject other)
        {
            if (ancestor == null || other == null) return false;
            if (ancestor == other) return true;
            var t = other.transform.parent;
            while (t != null)
            {
                if (t.gameObject == ancestor) return true;
                t = t.parent;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Interactability (P2)
        // -----------------------------------------------------------------

        // Is the target interactable, considering the Selectable it dispatches to
        // and the CanvasGroup chain? A disabled Button has a Selectable (so
        // HasAnyPointerHandler returns true) but IsInteractable()==false;
        // ExecuteHierarchy then silently no-ops the click. This surfaces that as a
        // response field. (feedback S5 — walk the Selectable chain that
        // ExecuteHierarchy reaches, and respect CanvasGroup.ignoreParentGroups.)
        public static bool ComputeInteractable(GameObject go)
        {
            if (go == null) return false;

            // blocksRaycasts walk: a CanvasGroup with blocksRaycasts==false makes
            // the raycast miss the target entirely. ignoreParentGroups stops the
            // walk (the group opts out of its parents' settings), so a group that
            // explicitly opts out is not treated as blocked by its parents.
            var t = go.transform;
            while (t != null)
            {
                var grp = t.GetComponent<CanvasGroup>();
                if (grp != null)
                {
                    if (!grp.blocksRaycasts) return false;
                    if (grp.ignoreParentGroups) break;
                }
                t = t.parent;
            }

            // Selectable walk: ExecuteHierarchy bubbles up, so a click on a child
            // (e.g. a Text under a Button) dispatches to the nearest ancestor
            // Selectable. Evaluate IsInteractable() there — it already walks the
            // CanvasGroup chain for the `interactable` flag (including
            // ignoreParentGroups), so we delegate rather than re-checking it.
            var selT = go.transform;
            while (selT != null)
            {
                var sel = selT.GetComponent<Selectable>();
                if (sel != null)
                    return sel.IsInteractable();
                selT = selT.parent;
            }

            return true;
        }

        // Does this GameObject (or any ancestor up to the canvas) implement any
        // pointer/drag/submit handler? Drives the `hasHandler` field. A handler
        // existing does NOT imply the click was possible — see ComputeInteractable.
        public static bool HasAnyPointerHandler(GameObject go)
        {
            if (go == null) return false;
            var t = go.transform;
            while (t != null)
            {
                if (IsPointerHandler(t.gameObject)) return true;
                t = t.parent;
            }
            return false;
        }

        private static bool IsPointerHandler(GameObject g)
        {
            var comps = g.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c is IPointerClickHandler
                    || c is IPointerDownHandler
                    || c is IPointerUpHandler
                    || c is IPointerEnterHandler
                    || c is IPointerExitHandler
                    || c is IBeginDragHandler
                    || c is IDragHandler
                    || c is IEndDragHandler
                    || c is IDropHandler
                    || c is IScrollHandler
                    || c is ISubmitHandler
                    || c is ISelectHandler
                    || c is Selectable)
                    return true;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // Hierarchy path
        // -----------------------------------------------------------------

        public static string BuildPath(GameObject go)
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
    }
}
