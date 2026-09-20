using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityOpenMcpBridge.Extensions.InputSimulation;

if (!UnityEditor.EditorApplication.isPlaying) throw new Exception("Play mode required");
var root = new GameObject("__InputReplay", typeof(Canvas), typeof(GraphicRaycaster));
var oldEvents = EventSystem.current;
var events = new GameObject("__InputReplayEvents", typeof(EventSystem));
var log = new List<string>();
var checks = new List<string>();
Action<bool, string> check = (ok, label) => { if (!ok) throw new Exception(label); checks.Add(label); };
Func<string, float, GameObject> make = (name, x) =>
{
    var go = new GameObject(name, typeof(RectTransform), typeof(Image));
    go.transform.SetParent(root.transform, false);
    var rect = (RectTransform)go.transform;
    rect.sizeDelta = new Vector2(100, 100);
    rect.anchoredPosition = new Vector2(x, 0);
    return go;
};
Action<GameObject, EventTriggerType, string> listen = (go, type, label) =>
{
    var trigger = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
    var entry = new EventTrigger.Entry { eventID = type };
    entry.callback.AddListener(data =>
    {
        var ped = (PointerEventData)data;
        log.Add(label);
        if (type == EventTriggerType.Drop || type == EventTriggerType.EndDrag)
            if (!ped.dragging || ped.pointerDrag == null) throw new Exception("incomplete drag event data");
        if (type == EventTriggerType.PointerClick)
            if (ped.pointerId != -1 || ped.pointerPress == null || ped.pointerPressRaycast.module == null)
                throw new Exception("incomplete click event data");
    });
    trigger.triggers.Add(entry);
};
try
{
    EventSystem.current = events.GetComponent<EventSystem>();
    var canvas = root.GetComponent<Canvas>();
    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
    canvas.sortingOrder = 32767;
    var source = make("Source", -100);
    var target = make("Target", 100);
    listen(source, EventTriggerType.PointerDown, "down");
    listen(source, EventTriggerType.InitializePotentialDrag, "initialize");
    listen(source, EventTriggerType.BeginDrag, "begin");
    listen(source, EventTriggerType.Drag, "drag");
    listen(source, EventTriggerType.PointerUp, "up");
    listen(source, EventTriggerType.EndDrag, "end");
    listen(source, EventTriggerType.PointerClick, "click");
    listen(source, EventTriggerType.PointerEnter, "enter");
    listen(source, EventTriggerType.PointerExit, "exit");
    listen(target, EventTriggerType.Drop, "drop");
    Canvas.ForceUpdateCanvases();
    var json = PointerTools.Pointer("drag", from_target: "__InputReplay/Source", to_target: "__InputReplay/Target", drag_steps: 2);
    check(log.SequenceEqual(new[] { "down", "initialize", "begin", "drag", "drag", "up", "drop", "end" }), "live drag release order");
    check(json.Contains("\"dropLanded\":true"), "live drop delivered");
    var noHandler = make("NoHandler", 250);
    json = PointerTools.Pointer("drag", from_target: "__InputReplay/Source", to_target: "__InputReplay/NoHandler");
    check(json.Contains("\"dropLanded\":false"), "no-handler destination reports false");
    log.Clear();
    PointerTools.Pointer("hover", target: "__InputReplay/Source");
    StepTools.Step(2);
    check(log.SequenceEqual(new[] { "enter" }), "hover survives stepped frames");
    PointerTools.Pointer("hover_exit", target: "__InputReplay/Source");
    check(log.SequenceEqual(new[] { "enter", "exit" }), "hover exit explicit");
    log.Clear();
    PointerTools.Pointer("click", object_id: source.GetInstanceID(), target: "missing", screen_x: -10000, screen_y: -10000);
    check(log.SequenceEqual(new[] { "down", "up", "click" }), "object ID overrides name and coordinates");
    json = PointerTools.Pointer("drag", from_target: "__InputReplay/Source", from_x: 1, from_y: 1, to_target: "__InputReplay/Target");
    check(json.Contains("both_endpoint_forms"), "conflicting drag endpoints rejected");
    json = PointerTools.Pointer("drag", from_x: -10000, from_y: -10000, to_x: 0, to_y: 0);
    check(json.Contains("no_hit"), "empty screen start refuses");
    var duplicate = make("Source", 300);
    json = PointerTools.Pointer("click", target: "Source");
    check(json.Contains("ambiguous_target"), "duplicate target names refused");
    UnityEngine.Object.DestroyImmediate(duplicate);
    var blocker = make("Blocker", -100);
    Canvas.ForceUpdateCanvases();
    StepTools.Step(1);
    json = PointerTools.Pointer("click", target: "__InputReplay/Source");
    check(json.Contains("\"blockedBy\":\"__InputReplay/Blocker\""), "occlusion truth is reported: " + json);
    UnityEngine.Object.DestroyImmediate(blocker);
    source.AddComponent<Button>().interactable = false;
    json = PointerTools.Pointer("click", target: "__InputReplay/Source");
    check(json.Contains("\"interactable\":false"), "disabled selectable truth is reported");
    json = ProbeTools.Probe(page_size: 200);
    check(json.Contains("__InputReplay/Source"), "probe discovers fixture");
    return checks;
}
finally
{
    UnityEngine.Object.DestroyImmediate(root);
    UnityEngine.Object.DestroyImmediate(events);
    if (oldEvents != null) EventSystem.current = oldEvents;
}
