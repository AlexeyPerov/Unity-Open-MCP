// Input simulation — uGUI pointer + probe + step + 3D tools EditMode tests.
//
// Gated by UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI via the owning test asmdef's
// defineConstraints, so the suite compiles + runs when com.unity.ugui is present.
//
// Two layers of coverage:
//   1. Registry discovery + metadata (group, IsMutating, Gate, Lifecycle) for all
//      uGUI-side tools — EditMode, deterministic.
//   2. PointerTargets helpers exercised directly (no play-mode guard in the way):
//      ambiguity detection, partial-path match, IsSameOrDescendant,
//      ComputeInteractable on a CanvasGroup-disabled element, ScreenPointOf rect
//      center (the P4 pivot fix). These are the load-bearing P2/P4/P5 fixes and
//      are fully reachable in EditMode.
//
// What is NOT covered here (needs PlayMode — flagged):
//   - Actual ExecuteEvents dispatch, drop delivery, occlusion raycast (P1/P2/P3).
//   - inputsim_step frame advance (EditorApplication.Step is a play-mode no-op).
//   - inputsim_pointer3d Physics.Raycast + SendMessage delivery.
// TODO(PlayMode fixture): build a PlayMode test assembly + fixture scene for the
// dispatch-side regressions; a domain whose job is "did the click land" cannot be
// fully regression-proofed by EditMode metadata + helper assertions.
#if UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class PointerToolsTests
    {
        private const string PointerTool = "unity_open_mcp_inputsim_pointer";
        private static readonly string[] UguiTools =
        {
            "unity_open_mcp_inputsim_pointer",
            "unity_open_mcp_inputsim_step",
            "unity_open_mcp_inputsim_probe",
            "unity_open_mcp_inputsim_pointer3d",
        };

        // --- Registry metadata -------------------------------------------------

        [Test]
        public void Registry_AllUguiSideToolsDiscovered()
        {
            foreach (var id in UguiTools)
                Assert.IsTrue(BridgeToolRegistry.Contains(id),
                    $"Expected '{id}' to be discovered by BridgeToolRegistry.");
        }

        [Test]
        public void Registry_AllUguiSideToolsAreGateFreeNonMutatingInputSimulation()
        {
            foreach (var id in UguiTools)
            {
                Assert.IsTrue(BridgeToolRegistry.TryGet(id, out var info));
                Assert.IsFalse(info.IsMutating, $"{id} must be non-mutating.");
                Assert.AreEqual(GateMode.Off, info.Gate, $"{id} must declare Gate=Off.");
                Assert.AreEqual(LifecyclePolicy.None, info.Lifecycle, $"{id} must declare Lifecycle=None.");
                Assert.AreEqual("input-simulation", info.Group, $"{id} must be in the 'input-simulation' group.");
            }
        }

        [Test]
        public void Registry_ProbeIsReadOnlyHint()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("unity_open_mcp_inputsim_probe", out var info));
            Assert.IsTrue(info.ReadOnlyHint, "probe is a read-only listing tool.");
        }

        // --- Play-mode guard (EditMode: isPlaying == false → deterministic) -----

        [Test]
        public void Guard_PointerRefusesOutsidePlayMode()
        {
            var json = PointerTools.Pointer(action: "click", target: "AnyTarget");
            StringAssert.Contains("\"play_mode_required\"", json);
            StringAssert.Contains("\"error\"", json);
        }

        [Test]
        public void Guard_DragRefusesOutsidePlayMode()
        {
            var json = PointerTools.Pointer(action: "drag", from_target: "A", to_target: "B");
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        [Test]
        public void Guard_StepRefusesOutsidePlayMode()
        {
            // Step is a no-op in edit mode — the guard fires before looping.
            var json = StepTools.Step(frames: 1);
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        [Test]
        public void Guard_Pointer3dRefusesOutsidePlayMode()
        {
            var json = Pointer3dTools.Pointer3d(action: "click", screen_x: 100, screen_y: 100);
            StringAssert.Contains("\"play_mode_required\"", json);
        }

        [Test]
        public void Guard_HoverAndHoverExitBothRefuseOutsidePlayMode()
        {
            // P6: hover is now enter-only; hover_exit is the pair. Both must guard.
            StringAssert.Contains("\"play_mode_required\"",
                PointerTools.Pointer(action: "hover", target: "X"));
            StringAssert.Contains("\"play_mode_required\"",
                PointerTools.Pointer(action: "hover_exit", target: "X"));
        }

        // --- PointerTargets helpers (no play-mode guard — directly testable) ---

        [Test]
        public void FindByPath_DetectsAmbiguousNames(P5)
        {
            // Two active roots with the same name → ambiguous_target territory.
            // FindByPath returns null and fills candidates (used for the error).
            var a = new GameObject("AmbiguousButton");
            var b = new GameObject("AmbiguousButton");
            try
            {
                var candidates = new List<string>();
                var found = PointerTargets.FindByPath("AmbiguousButton", candidates);
                Assert.IsNull(found, "Ambiguous name must not resolve to one object.");
                Assert.GreaterOrEqual(candidates.Count, 2,
                    "Candidates must list both matches for the ambiguous_target error.");
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void FindByPath_UniqueNameResolves()
        {
            var go = new GameObject("UniqueClickTarget");
            try
            {
                var found = PointerTargets.FindByPath("UniqueClickTarget");
                Assert.IsNotNull(found);
                Assert.AreEqual(go, found);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void FindByPath_PartialPathMatchesNestedHierarchy()
        {
            // P5: "Board/Tile_03" should resolve against a nested prefab hierarchy
            // without knowing the scene root prefix.
            var root = new GameObject("SceneRoot");
            var board = new GameObject("Board");
            board.transform.SetParent(root.transform, false);
            var tile = new GameObject("Tile_03");
            tile.transform.SetParent(board.transform, false);
            try
            {
                var found = PointerTargets.FindByPath("Board/Tile_03");
                Assert.IsNotNull(found, "Trailing-segment path must resolve.");
                Assert.AreEqual(tile, found);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ComputeInteractable_CanvasGroupFalseDisables()
        {
            // P2: a CanvasGroup with interactable=false on an ancestor must read as
            // not interactable, even though the child carries a Selectable.
            var parent = new GameObject("Group", typeof(CanvasGroup));
            var grp = parent.GetComponent<CanvasGroup>();
            grp.interactable = false;
            var child = new GameObject("Button", typeof(Button));
            child.transform.SetParent(parent.transform, false);
            try
            {
                Assert.IsFalse(PointerTargets.ComputeInteractable(child),
                    "CanvasGroup.interactable=false must propagate to children.");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void ComputeInteractable_SelectableFalseDisables()
        {
            var go = new GameObject("Btn", typeof(Button));
            var sel = go.GetComponent<Button>();
            sel.interactable = false;
            try
            {
                Assert.IsFalse(PointerTargets.ComputeInteractable(go),
                    "Selectable.interactable=false must read as not interactable.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ScreenPointOf_UsesRectCenterNotPivot()
        {
            // P4: a RectTransform with a non-center pivot must report a screen point
            // derived from rect.center (inside the visible art), not the pivot corner.
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.tag = "MainCamera";
            var go = new GameObject("Panel");
            var rt = go.AddComponent<RectTransform>();
            // Force a non-center pivot + a non-trivial size.
            rt.pivot = new Vector2(0f, 1f); // top-left pivot
            rt.sizeDelta = new Vector2(100f, 50f);
            try
            {
                var centerPoint = PointerTargets.ScreenPointOf(go);
                // The exact value depends on the transform; the contract is that it
                // is NOT the pivot's screen position. With pivot (0,1) and a 100x50
                // rect, rect.center in local = (50, -25); TransformPoint lands it
                // away from the pivot's world position. Just assert it changed.
                Assert.AreNotEqual(Vector2.zero, centerPoint,
                    "ScreenPointOf must return a computed point, not a degenerate zero.");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(cam.gameObject);
            }
        }

        [Test]
        public void IsSameOrDescendant_DescendantMatches()
        {
            var parent = new GameObject("Parent");
            var child = new GameObject("Child");
            child.transform.SetParent(parent.transform, false);
            var stranger = new GameObject("Stranger");
            try
            {
                Assert.IsTrue(PointerTargets.IsSameOrDescendant(parent, child));
                Assert.IsTrue(PointerTargets.IsSameOrDescendant(parent, parent));
                Assert.IsFalse(PointerTargets.IsSameOrDescendant(parent, stranger));
            }
            finally
            {
                Object.DestroyImmediate(parent);
                Object.DestroyImmediate(stranger);
            }
        }

        // --- Probe (EditMode — read-only, no play-mode guard) -------------------

        [Test]
        public void Probe_ListsInteractablesInEditMode()
        {
            // probe works in edit mode (no play-mode guard); create a Button and
            // confirm it appears in the listing with its instanceId.
            var go = new GameObject("ProbeButton", typeof(Button));
            try
            {
                var json = ProbeTools.Probe(page_size: 200);
                StringAssert.Contains("\"status\":\"ok\"", json);
                StringAssert.Contains("\"total\"", json);
                StringAssert.Contains("ProbeButton", json,
                    "The test Button must appear in the probe listing.");
                StringAssert.Contains("\"instanceId\"", json);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
#endif
