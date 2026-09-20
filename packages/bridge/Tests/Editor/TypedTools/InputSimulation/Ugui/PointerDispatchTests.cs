#if UNITY_OPEN_MCP_EXT_INPUTSIM_UGUI
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    // Exercise the dispatch core in EditMode; the public guard is covered in
    // PointerToolsTests. Live frame/input checks are indexed by the suite.
    public class PointerDispatchTests
    {
        private GameObject root, source, destination, events;
        private EventSystem previous;
        private PointerRecorder recorder;

        [SetUp]
        public void SetUp()
        {
            previous = EventSystem.current;
            events = new GameObject("DispatchEvents", typeof(EventSystem));
            // EventSystem does not run OnEnable automatically in EditMode.
            typeof(EventSystem).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(events.GetComponent<EventSystem>(), null);
            EventSystem.current = events.GetComponent<EventSystem>();
            root = new GameObject("DispatchCanvas", typeof(Canvas), typeof(GraphicRaycaster));
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            source = new GameObject("Source", typeof(RectTransform), typeof(Image), typeof(PointerRecorder));
            source.transform.SetParent(root.transform, false);
            destination = new GameObject("Destination", typeof(RectTransform));
            destination.transform.SetParent(root.transform, false);
            recorder = source.GetComponent<PointerRecorder>();
            PointerRecorder.Events.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            typeof(EventSystem).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(events.GetComponent<EventSystem>(), null);
            Object.DestroyImmediate(events);
            if (previous != null) EventSystem.current = previous;
        }

        private string Single(string action) => (string)typeof(PointerTools)
            .GetMethod("DoSingle", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { action, PointerEventData.InputButton.Left,
                (long?)source.GetInstanceID(), null, null, null });

        private string Drag(int steps = 2) => (string)typeof(PointerTools)
            .GetMethod("DoDrag", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { PointerEventData.InputButton.Left, steps,
                (long?)source.GetInstanceID(), null, null, null,
                "DispatchCanvas/Destination", null, null });

        [Test]
        public void DoubleClick_PopulatesDataAndIncrementsCount()
        {
            Single("double_click");
            CollectionAssert.AreEqual(new[] { "down", "up", "click:1", "down", "up", "click:2" }, PointerRecorder.Events);
            Assert.AreEqual(source, recorder.Last.pointerPress);
            Assert.AreEqual(source, recorder.Last.rawPointerPress);
            Assert.AreEqual(-1, recorder.Last.pointerId);
            Assert.IsTrue(recorder.Last.eligibleForClick);
            Assert.AreEqual(source, recorder.Last.pointerPressRaycast.gameObject);
            Assert.AreEqual(root.GetComponent<GraphicRaycaster>(), recorder.Last.pointerPressRaycast.module);
            Assert.AreEqual(recorder.Last.position, recorder.Last.pressPosition);
        }

        [Test]
        public void CameraCanvas_PressEventCameraComesFromRaycaster()
        {
            var camera = new GameObject("EventCamera", typeof(Camera));
            try
            {
                var canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera.GetComponent<Camera>();
                Single("click");
                Assert.AreSame(canvas.worldCamera, recorder.Last.pressEventCamera);
            }
            finally { Object.DestroyImmediate(camera); }
        }

        [Test]
        public void Drag_DeliversDropBeforeEndDragWithDraggingState()
        {
            destination.AddComponent<PointerRecorder>();
            var json = Drag();
            CollectionAssert.AreEqual(new[] { "down", "initialize", "begin", "drag", "drag", "up", "drop", "end" }, PointerRecorder.Events);
            Assert.IsTrue(destination.GetComponent<PointerRecorder>().DraggingAtDrop);
            Assert.IsTrue(recorder.DraggingAtEnd);
            Assert.AreSame(source, recorder.Last.pointerDrag);
            StringAssert.Contains("\"dropLanded\":true", json);
        }

        [Test]
        public void Drag_WithoutDropHandlerDoesNotClaimDelivery()
        {
            var json = Drag();
            StringAssert.Contains("\"dropLanded\":false", json);
            StringAssert.DoesNotContain("\"drop\"", json);
        }

        [Test]
        public void Drag_ReportsAncestorThatActuallyReceivedDrop()
        {
            root.AddComponent<PointerRecorder>();
            var json = Drag();
            StringAssert.Contains("\"dropTarget\":\"DispatchCanvas\"", json);
        }

        [TestCase(0, 1)]
        [TestCase(101, 100)]
        public void Drag_ClampsStepCount(int requested, int expected)
        {
            Drag(requested);
            Assert.AreEqual(expected, PointerRecorder.Events.FindAll(e => e == "drag").Count);
        }

        [Test]
        public void Hover_RemainsEnteredUntilExplicitExit()
        {
            Single("hover");
            CollectionAssert.AreEqual(new[] { "enter" }, PointerRecorder.Events);
            Single("hover_exit");
            CollectionAssert.AreEqual(new[] { "enter", "exit" }, PointerRecorder.Events);
        }

        [Test]
        public void Interactable_DisabledComponentAndNonSelectableGroupAreFalse()
        {
            var button = source.AddComponent<Button>();
            button.enabled = false;
            Assert.IsFalse(PointerTargets.ComputeInteractable(source));
            Object.DestroyImmediate(button);
            root.AddComponent<CanvasGroup>().interactable = false;
            Assert.IsFalse(PointerTargets.ComputeInteractable(source));
            source.AddComponent<CanvasGroup>().ignoreParentGroups = true;
            Assert.IsTrue(PointerTargets.ComputeInteractable(source));
        }
    }

    public class PointerRecorder : MonoBehaviour, IPointerDownHandler, IPointerUpHandler,
        IPointerClickHandler, IInitializePotentialDragHandler, IBeginDragHandler,
        IDragHandler, IDropHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public static readonly List<string> Events = new List<string>();
        public PointerEventData Last;
        public bool DraggingAtDrop, DraggingAtEnd;
        private void Record(string name, PointerEventData data) { Events.Add(name); Last = data; }
        public void OnPointerDown(PointerEventData e) => Record("down", e);
        public void OnPointerUp(PointerEventData e) => Record("up", e);
        public void OnPointerClick(PointerEventData e) => Record("click:" + e.clickCount, e);
        public void OnInitializePotentialDrag(PointerEventData e) => Record("initialize", e);
        public void OnBeginDrag(PointerEventData e) => Record("begin", e);
        public void OnDrag(PointerEventData e) => Record("drag", e);
        public void OnDrop(PointerEventData e) { DraggingAtDrop = e.dragging; Record("drop", e); }
        public void OnEndDrag(PointerEventData e) { DraggingAtEnd = e.dragging; Record("end", e); }
        public void OnPointerEnter(PointerEventData e) => Record("enter", e);
        public void OnPointerExit(PointerEventData e) => Record("exit", e);
    }
}
#endif
