using UnityEngine.InputSystem;
using UnityEngine.LowLevel;
using UnityOpenMcpBridge.Extensions.InputSimulation;

if (!UnityEditor.EditorApplication.isPlaying) throw new Exception("Play mode required");
var original = PlayerLoop.GetCurrentPlayerLoop();
var paused = UnityEditor.EditorApplication.isPaused;
var inputMode = InputSystem.settings.updateMode;
var keyboard = InputSystem.AddDevice<Keyboard>();
var touch = InputSystem.AddDevice<Touchscreen>();
var mode = "";
var pressed = new List<int>();
var held = new List<int>();
var touchPositions = new List<float>();
var touchDeltas = new List<float>();
var touchFrames = new List<int>();
var touchBegan = new List<int>();
var checks = new List<string>();
Action<bool, string> check = (ok, label) => { if (!ok) throw new Exception(label); checks.Add(label); };
try
{
    // Sample after ScriptRunBehaviourUpdate in the real player loop. This is
    // deliberately not onAfterUpdate: that would pass for the broken eager
    // manual InputSystem.Update path without proving gameplay can poll it.
    var loop = original;
    var systems = loop.subSystemList.ToArray();
    for (int i = 0; i < systems.Length; i++)
    {
        if (systems[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
        var update = systems[i];
        var subs = update.subSystemList.ToList();
        subs.Add(new PlayerLoopSystem { type = typeof(Keyboard), updateDelegate = () =>
        {
            if (mode == "key")
            {
                if (keyboard.spaceKey.wasPressedThisFrame) pressed.Add(Time.frameCount);
                if (keyboard.spaceKey.isPressed) held.Add(Time.frameCount);
            }
            if (mode == "touch")
            {
                if (touch.primaryTouch.press.wasPressedThisFrame) touchBegan.Add(Time.frameCount);
                if (touch.primaryTouch.press.isPressed)
                {
                    touchFrames.Add(Time.frameCount);
                    touchPositions.Add(touch.primaryTouch.position.ReadValue().x);
                    touchDeltas.Add(touch.primaryTouch.delta.ReadValue().x);
                }
            }
        }});
        update.subSystemList = subs.ToArray();
        systems[i] = update;
    }
    loop.subSystemList = systems;
    PlayerLoop.SetPlayerLoop(loop);
    foreach (var initialPaused in new[] { true, false })
    {
        UnityEditor.EditorApplication.isPaused = initialPaused;
        var before = Time.frameCount;
        StepTools.Step(3);
        check(Time.frameCount - before == 3, "step three frames paused=" + initialPaused);
        check(UnityEditor.EditorApplication.isPaused == initialPaused, "pause restored=" + initialPaused);
    }
    UnityEditor.EditorApplication.isPaused = true;
    var zeroBefore = Time.frameCount;
    StepTools.Step(0);
    check(Time.frameCount == zeroBefore, "zero frames does not step");
    mode = "key";
    InputSystemDeviceTools.Key("tap", "Space", advance_frames: 3);
    check(pressed.Count == 1 && held.Count == 3, "tap press edge reaches gameplay exactly once: pressed=" + string.Join(",", pressed) + " held=" + string.Join(",", held));
    check(!keyboard.spaceKey.isPressed, "tap releases key");
    check(UnityEditor.EditorApplication.isPaused, "key restores paused state");
    pressed.Clear(); held.Clear();
    InputSystemDeviceTools.Key("tap", "Space", advance_frames: 0);
    check(pressed.Count == 0 && held.Count == 0, "zero-frame tap does not run gameplay");
    InputSystemDeviceTools.Key("down", "W");
    InputSystemDeviceTools.Key("down", "Space", shift: true);
    InputSystemDeviceTools.Key("up", "Space");
    check(keyboard.wKey.isPressed && keyboard.leftShiftKey.isPressed && !keyboard.spaceKey.isPressed,
        "named release preserves W and unrequested Shift");
    InputSystemDeviceTools.Key("up", "W", shift: true);
    check(!keyboard.wKey.isPressed && !keyboard.leftShiftKey.isPressed, "explicit modifiers released");
    var endpointResult = InputSystemDeviceTools.Touch("swipe", from_x: 100, from_y: 100,
        to_target: "__MissingInputReplayTarget", to_x: 140, to_y: 100);
    check(endpointResult.Contains("target_not_found"), "touch end target precedes coordinates");
    endpointResult = InputSystemDeviceTools.Touch("swipe", from_target: "__MissingInputReplayTarget",
        from_x: 100, from_y: 100, to_x: 140, to_y: 100);
    check(endpointResult.Contains("target_not_found"), "touch start target precedes coordinates");
    mode = "touch";
    InputSystemDeviceTools.Touch("swipe", from_x: 100, from_y: 100, to_x: 140, to_y: 100,
        steps: 4, advance_frames: 1);
    check(touchBegan.Count == 1, "touch began reaches gameplay");
    check(touchPositions.SequenceEqual(new float[] { 100, 110, 120, 130, 140 }),
        "swipe visits began and four positions in gameplay");
    check(touchDeltas.Skip(1).All(d => Math.Abs(d - 10) < 0.01), "swipe delta survives until gameplay");
    check(touchFrames.Distinct().Count() == 5, "swipe phases occupy distinct frames");
    check(!touch.primaryTouch.press.isPressed, "swipe ends released");
    touchBegan.Clear(); touchFrames.Clear(); touchPositions.Clear(); touchDeltas.Clear();
    InputSystemDeviceTools.Touch("tap", screen_x: 200, screen_y: 100, advance_frames: 2);
    check(touchBegan.Count == 1 && touchFrames.Count == 2, "touch tap remains pressed across frames");
    check(!touch.primaryTouch.press.isPressed, "touch tap ends released");
    check(UnityEditor.EditorApplication.isPaused, "touch restores paused state");
    check(InputSystem.settings.updateMode == inputMode, "input update mode restored");
    var afterLoop = PlayerLoop.GetCurrentPlayerLoop();
    check(!afterLoop.subSystemList.SelectMany(s => s.subSystemList ?? new PlayerLoopSystem[0])
        .Any(s => s.type == typeof(InputSystemDeviceTools)), "temporary input callback removed");
    return checks;
}
finally
{
    PlayerLoop.SetPlayerLoop(original);
    InputSystem.RemoveDevice(touch);
    InputSystem.RemoveDevice(keyboard);
    UnityEditor.EditorApplication.isPaused = paused;
}
