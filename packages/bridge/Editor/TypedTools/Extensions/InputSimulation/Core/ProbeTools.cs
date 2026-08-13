// Input simulation — interactable discovery (M2 in feedback-input.md).
//
// Turns the surface self-sufficient: the testing loop becomes
//   probe → click by path → step → screenshot
// instead of screenshot → guess a name → target_not_found → guess again. Lists
// active objects implementing uGUI pointer handlers (IPointer*Handler /
// IDragHandler / IDropHandler / ISubmitHandler / Selectable) with their hierarchy
// path, screen rect, interactable flag, and whether anything occludes their
// center — the same computation P2 added per-click, exposed proactively.
//
// Gate-free, but NOT play-mode-only: probing the interactable surface is useful
// in edit mode too (build the click plan before entering play mode). The screen
// rect is computed from RectTransforms where available; non-RT interactables
// report a degenerate rect at their screen point.
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    [BridgeToolType]
    public static class ProbeTools
    {
        private const string CursorPrefix = "inputsim_probe";
        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 200;

        [BridgeTool("unity_open_mcp_inputsim_probe",
            Title = "Input Simulation: Probe Interactables",
            IsMutating = false,
            Gate = GateMode.Off,
            ReadOnlyHint = true,
            IdempotentHint = true,
            DestructiveHint = false,
            Lifecycle = LifecyclePolicy.None,
            Group = "input-simulation")]
        [System.ComponentModel.Description(
            "List active uGUI interactables (IPointer*Handler / IDragHandler / " +
            "IDropHandler / ISubmitHandler / Selectable) with hierarchy path, screen " +
            "rect, interactable flag, and whether anything occludes their center. " +
            "Read-only. Works in edit mode and play mode (use it to build a click " +
            "plan before entering play mode).")]
        public static string Probe(
            int page_size = DefaultPageSize,
            string cursor = null,
            string scene = null)
        {
            if (page_size < 1) page_size = 1;
            if (page_size > MaxPageSize) page_size = MaxPageSize;
            int offset = ParseCursor(cursor);

            var all = CollectInteractables(scene);
            // Stable order: by path so paging is deterministic across calls.
            all.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

            int total = all.Count;
            int remaining = total - (offset + page_size);
            if (remaining < 0) remaining = 0;

            int end = Mathf.Min(offset + page_size, total);

            var sb = new StringBuilder(2048);
            sb.Append("{\"status\":\"ok\"");
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"interactables\":[");
            for (int i = offset; i < end; i++)
            {
                if (i > offset) sb.Append(',');
                AppendInteractable(sb, all[i]);
            }
            sb.Append(']');
            AppendPagination(sb, offset, page_size, remaining);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendInteractable(StringBuilder sb, InteractableInfo info)
        {
            sb.Append('{');
            sb.Append("\"path\":").Append(InputSimulationJson.Esc(info.Path));
            sb.Append(",\"name\":").Append(InputSimulationJson.Esc(info.Name));
            sb.Append(",\"interactable\":").Append(info.Interactable ? "true" : "false");
            sb.Append(",\"occluded\":").Append(info.Occluded ? "true" : "false");
            sb.Append(",\"center\":[").Append(Num(info.Center.x)).Append(',').Append(Num(info.Center.y)).Append(']');
            sb.Append(",\"rect\":{");
            sb.Append("\"x\":").Append(Num(info.RectX));
            sb.Append(",\"y\":").Append(Num(info.RectY));
            sb.Append(",\"w\":").Append(Num(info.RectW));
            sb.Append(",\"h\":").Append(Num(info.RectH));
            sb.Append('}');
            sb.Append(",\"instanceId\":").Append(info.InstanceId.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }

        private static void AppendPagination(StringBuilder sb, int offset, int pageSize, int remaining)
        {
            sb.Append(",\"pagination\":{");
            sb.Append("\"page_size\":").Append(pageSize);
            sb.Append(",\"cursor\":");
            if (offset > 0)
                sb.Append('"').Append(CursorPrefix).Append(':').Append(offset.ToString(CultureInfo.InvariantCulture)).Append('"');
            else
                sb.Append("null");
            sb.Append(",\"next_cursor\":");
            if (remaining > 0)
                sb.Append('"').Append(CursorPrefix).Append(':').Append((offset + pageSize).ToString(CultureInfo.InvariantCulture)).Append('"');
            else
                sb.Append("null");
            sb.Append(",\"truncated\":").Append(remaining);
            sb.Append('}');
        }

        private static int ParseCursor(string cursor)
        {
            if (string.IsNullOrEmpty(cursor)) return 0;
            int colon = cursor.LastIndexOf(':');
            if (colon < 0 || colon >= cursor.Length - 1) return 0;
            var tail = cursor.Substring(colon + 1);
            return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        }

        private static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // Walk every active transform; emit one entry per object that implements a
        // pointer/drag/drop/submit handler or carries a Selectable. Dedupes by
        // GameObject — Selectable implements several of the interfaces, so a naive
        // per-interface scan would list a Button multiple times.
        private static List<InteractableInfo> CollectInteractables(string sceneFilter)
        {
            var list = new List<InteractableInfo>();
            // Dedup by instance id (long — EntityId-safe on Unity 6000.5+ where
            // the int GetInstanceID API is a hard CS0619 error; go through the
            // version-gated InstanceId helper instead of the obsolete API).
            var seen = new HashSet<long>();

            foreach (var t in SceneQuery.FindActiveTransforms())
            {
                if (t == null) continue;
                var go = t.gameObject;

                // Optional scene filter — compare against the loaded-scene name.
                if (!string.IsNullOrEmpty(sceneFilter))
                {
                    var scenePath = go.scene.name;
                    if (scenePath != sceneFilter) continue;
                }

                // The handler check walks the hierarchy (ExecuteHierarchy bubbles),
                // but probe should report the FIRST object up the chain that actually
                // carries the handler component — that's what the agent would target.
                var handlerGo = FindHandlerOnThisOrAncestor(go);
                if (handlerGo == null) continue;

                long id = InstanceId.Of(handlerGo);
                if (!seen.Add(id)) continue;

                list.Add(BuildInfo(handlerGo));
            }
            return list;
        }

        // Walk up to find the nearest ancestor that has a pointer/drag/drop/submit
        // handler OR a Selectable. Returns null when the chain has none.
        private static GameObject FindHandlerOnThisOrAncestor(GameObject go)
        {
            var t = go.transform;
            while (t != null)
            {
                var g = t.gameObject;
                if (HasHandlerComponent(g)) return g;
                t = t.parent;
            }
            return null;
        }

        private static bool HasHandlerComponent(GameObject g)
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

        private static InteractableInfo BuildInfo(GameObject go)
        {
            var info = new InteractableInfo
            {
                Path = PointerTargets.BuildPath(go),
                Name = go.name,
                InstanceId = InstanceId.Of(go),
                Interactable = PointerTargets.ComputeInteractable(go),
                Center = PointerTargets.ScreenPointOf(go),
            };

            // Screen rect from the RectTransform (world corners → screen-space AABB).
            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                var cs = new Vector3[4];
                rt.GetWorldCorners(cs);
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                var cam = PointerTargets.CameraFor(go);
                for (int i = 0; i < 4; i++)
                {
                    // CameraFor already returns null for ScreenSpaceOverlay canvases
                    // (RectTransformUtility treats null as overlay), so the world
                    // corner maps straight to screen without an overlay branch.
                    var sp = RectTransformUtility.WorldToScreenPoint(cam, cs[i]);
                    if (sp.x < minX) minX = sp.x;
                    if (sp.y < minY) minY = sp.y;
                    if (sp.x > maxX) maxX = sp.x;
                    if (sp.y > maxY) maxY = sp.y;
                }
                info.RectX = minX; info.RectY = minY;
                info.RectW = maxX - minX; info.RectH = maxY - minY;
            }
            else
            {
                info.RectX = info.Center.x; info.RectY = info.Center.y;
                info.RectW = 0; info.RectH = 0;
            }

            // Occlusion: raycast the center in play mode only (edit mode has no
            // live EventSystem raycast in most scenes). When no EventSystem /
            // not playing, report occluded=false (unknown), not a hard failure.
            if (EditorApplication.isPlaying && EventSystem.current != null)
            {
                var top = PointerTargets.RaycastTop(info.Center, out _);
                info.Occluded = top != null
                    && top != go
                    && !PointerTargets.IsSameOrDescendant(go, top);
            }
            return info;
        }

        private sealed class InteractableInfo
        {
            public string Path;
            public string Name;
            public long InstanceId;
            public bool Interactable;
            public bool Occluded;
            public Vector2 Center;
            public float RectX, RectY, RectW, RectH;
        }
    }
}
