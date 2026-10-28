// Gesture plan: parse, validate, resolve coordinates, and sample finger paths.
//
// Pure apart from target resolution (injected), so the timing model is unit
// tested without play mode.
//
// Keyframe phases (per finger):
//   down  — first keyframe (implicit when omitted): the finger touches here.
//   move  — default for intermediate keyframes: linear interpolation from the
//           previous keyframe.
//   hold  — the finger stays at the previous keyframe's point until at_ms, then
//           is at this keyframe's point (x/y may be omitted = same point).
//   up    — last keyframe (implicit when omitted): the finger is interpolated to
//           this point and lifts at at_ms.
// at_ms is strictly increasing per finger; one contact per finger per gesture.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityOpenMcpBridge.Extensions.InputSimulation
{
    internal sealed class GestureKeyframe
    {
        public double AtMs;
        public string Phase;      // down | move | hold | up
        public bool HasX, HasY;
        public double X, Y;       // raw (pixels, 0..1, or target offset in pixels)
        public string Target;
        public Vector2 Point;     // resolved Screen pixels
    }

    internal sealed class GestureFinger
    {
        public int Id;
        public readonly List<GestureKeyframe> Path = new List<GestureKeyframe>();
        public double StartMs => Path[0].AtMs;
        public double EndMs => Path[Path.Count - 1].AtMs;
    }

    internal enum FingerState { NotStarted, Down, Up }

    internal struct FingerSample
    {
        public FingerState State;
        public Vector2 Point;
        public int Keyframe;      // index of the last keyframe reached (-1 before the first)
    }

    internal sealed class GesturePlan
    {
        internal const int MaxFingers = 10;
        internal const int MaxKeyframes = 256;
        internal const double MaxDurationMs = 300000;

        public readonly List<GestureFinger> Fingers = new List<GestureFinger>();
        public string Coords = "screen";
        public PlayModeTimeBase Time = PlayModeTimeBase.Realtime;
        public double DurationMs;

        public List<int> FingerIds()
        {
            var ids = new List<int>(Fingers.Count);
            foreach (var f in Fingers) ids.Add(f.Id);
            return ids;
        }

        // Returns null on success, else an error (code, message).
        internal static GesturePlan Parse(string fingersJson, string coords, string time, out string code, out string message)
        {
            code = message = null;
            var plan = new GesturePlan();
            coords = string.IsNullOrEmpty(coords) ? "screen" : coords;
            if (coords != "screen" && coords != "normalized" && coords != "target")
                return Fail("invalid_arguments", "coords must be screen, normalized or target.", out code, out message);
            plan.Coords = coords;
            time = string.IsNullOrEmpty(time) ? "realtime" : time;
            if (time != "realtime" && time != "gameplay")
                return Fail("invalid_arguments", "time must be realtime or gameplay.", out code, out message);
            plan.Time = time == "gameplay" ? PlayModeTimeBase.Gameplay : PlayModeTimeBase.Realtime;

            if (string.IsNullOrWhiteSpace(fingersJson) || !fingersJson.TrimStart().StartsWith("["))
                return Fail("invalid_arguments", "fingers must be a non-empty array.", out code, out message);
            var fingerValues = JsonBody.GetArrayRawValues(fingersJson);
            if (fingerValues.Count == 0 || fingerValues.Count > MaxFingers)
                return Fail("invalid_arguments", "fingers must hold 1-" + MaxFingers + " entries.", out code, out message);

            var seen = new HashSet<int>();
            for (int f = 0; f < fingerValues.Count; f++)
            {
                var raw = fingerValues[f].Trim();
                if (!raw.StartsWith("{")) return Fail("invalid_arguments", "fingers[" + f + "] must be an object.", out code, out message);
                var finger = new GestureFinger();
                var idRaw = JsonBody.GetTopLevelRawValue(raw, "id");
                if (idRaw == null) finger.Id = f;
                else if (!int.TryParse(idRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out finger.Id) || finger.Id < 0 || finger.Id > 9)
                    return Fail("invalid_arguments", "fingers[" + f + "].id must be an integer 0-9.", out code, out message);
                if (!seen.Add(finger.Id))
                    return Fail("invalid_arguments", "Finger id " + finger.Id + " appears twice.", out code, out message);

                var pathRaw = JsonBody.GetTopLevelRawValue(raw, "path");
                var keyValues = pathRaw == null ? new List<string>() : JsonBody.GetArrayRawValues(pathRaw.Trim());
                if (keyValues.Count < 2 || keyValues.Count > MaxKeyframes)
                    return Fail("invalid_arguments", "fingers[" + f + "].path must hold 2-" + MaxKeyframes + " keyframes (down … up).", out code, out message);

                for (int k = 0; k < keyValues.Count; k++)
                {
                    var kraw = keyValues[k].Trim();
                    var where = "fingers[" + f + "].path[" + k + "]";
                    if (!kraw.StartsWith("{")) return Fail("invalid_arguments", where + " must be an object.", out code, out message);
                    var key = new GestureKeyframe();
                    if (!TryNumber(kraw, "at_ms", out key.AtMs, out var hasAt) || !hasAt || key.AtMs < 0 || double.IsNaN(key.AtMs))
                        return Fail("invalid_arguments", where + ".at_ms must be a number >= 0.", out code, out message);
                    if (!TryNumber(kraw, "x", out key.X, out key.HasX) || !TryNumber(kraw, "y", out key.Y, out key.HasY))
                        return Fail("invalid_arguments", where + ".x/.y must be numbers.", out code, out message);
                    if (key.HasX != key.HasY)
                        return Fail("invalid_arguments", where + " needs both x and y, or neither.", out code, out message);
                    var phaseRaw = JsonBody.GetTopLevelRawValue(kraw, "phase");
                    key.Phase = phaseRaw == null ? null : JsonBody.GetString("{\"v\":" + phaseRaw + "}", "v");
                    var targetRaw = JsonBody.GetTopLevelRawValue(kraw, "target");
                    key.Target = targetRaw == null ? null : JsonBody.GetString("{\"v\":" + targetRaw + "}", "v");

                    bool first = k == 0, last = k == keyValues.Count - 1;
                    if (key.Phase == null) key.Phase = first ? "down" : last ? "up" : "move";
                    if (key.Phase != "down" && key.Phase != "move" && key.Phase != "hold" && key.Phase != "up")
                        return Fail("invalid_arguments", where + ".phase must be down, move, hold or up.", out code, out message);
                    if (first && key.Phase != "down")
                        return Fail("invalid_arguments", where + ": the first keyframe must be phase down.", out code, out message);
                    if (last && key.Phase != "up")
                        return Fail("invalid_arguments", where + ": the last keyframe must be phase up.", out code, out message);
                    if (!first && !last && (key.Phase == "down" || key.Phase == "up"))
                        return Fail("invalid_arguments", where + ": down/up are only allowed on the first/last keyframe (one contact per finger).", out code, out message);
                    if (k > 0 && key.AtMs <= finger.Path[k - 1].AtMs)
                        return Fail("invalid_arguments", where + ".at_ms must be greater than the previous keyframe's.", out code, out message);

                    bool mayInherit = key.Phase == "hold";
                    if (coords == "target")
                    {
                        if (string.IsNullOrEmpty(key.Target) && !mayInherit)
                            return Fail("invalid_arguments", where + " needs target (coords: target); x/y are pixel offsets from its center.", out code, out message);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(key.Target))
                            return Fail("invalid_arguments", where + ".target is only valid with coords: target.", out code, out message);
                        if (!key.HasX && !mayInherit)
                            return Fail("invalid_arguments", where + " needs x and y.", out code, out message);
                        if (coords == "normalized" && key.HasX && (key.X < 0 || key.X > 1 || key.Y < 0 || key.Y > 1))
                            return Fail("invalid_arguments", where + ": normalized x/y must be within 0..1.", out code, out message);
                    }
                    finger.Path.Add(key);
                }
                if (finger.EndMs > MaxDurationMs)
                    return Fail("invalid_arguments", "Gesture duration is capped at " + MaxDurationMs + " ms.", out code, out message);
                plan.DurationMs = Math.Max(plan.DurationMs, finger.EndMs);
                plan.Fingers.Add(finger);
            }
            return plan;
        }

        // Resolve every keyframe to Screen pixels. `resolveTarget` maps a target
        // name/path to its center; `screen` is the game's Screen size.
        internal bool ResolvePoints(Vector2 screen, Func<string, (bool Ok, Vector2 Point, string Code, string Message)> resolveTarget,
            out string code, out string message)
        {
            code = message = null;
            foreach (var finger in Fingers)
            {
                for (int k = 0; k < finger.Path.Count; k++)
                {
                    var key = finger.Path[k];
                    var previous = k > 0 ? finger.Path[k - 1].Point : Vector2.zero;
                    switch (Coords)
                    {
                        case "screen":
                            key.Point = key.HasX ? new Vector2((float)key.X, (float)key.Y) : previous;
                            break;
                        case "normalized":
                            key.Point = key.HasX ? new Vector2((float)(key.X * screen.x), (float)(key.Y * screen.y)) : previous;
                            break;
                        default:
                            if (string.IsNullOrEmpty(key.Target))
                            {
                                key.Point = previous + (key.HasX ? new Vector2((float)key.X, (float)key.Y) : Vector2.zero);
                                break;
                            }
                            var r = resolveTarget(key.Target);
                            if (!r.Ok)
                            {
                                code = r.Code;
                                message = "fingers id " + finger.Id + " keyframe " + k + ": " + r.Message;
                                return false;
                            }
                            key.Point = r.Point + (key.HasX ? new Vector2((float)key.X, (float)key.Y) : Vector2.zero);
                            break;
                    }
                }
            }
            return true;
        }

        // Where a finger is at `elapsedMs`.
        internal static FingerSample Sample(GestureFinger finger, double elapsedMs)
        {
            var path = finger.Path;
            if (elapsedMs < path[0].AtMs)
                return new FingerSample { State = FingerState.NotStarted, Point = path[0].Point, Keyframe = -1 };
            if (elapsedMs >= path[path.Count - 1].AtMs)
                return new FingerSample { State = FingerState.Up, Point = path[path.Count - 1].Point, Keyframe = path.Count - 1 };
            int k = 0;
            while (k + 1 < path.Count && path[k + 1].AtMs <= elapsedMs) k++;
            var a = path[k];
            var b = path[k + 1];
            Vector2 point;
            if (b.Phase == "hold") point = a.Point;
            else
            {
                double t = (elapsedMs - a.AtMs) / (b.AtMs - a.AtMs);
                point = Vector2.Lerp(a.Point, b.Point, (float)Math.Max(0, Math.Min(1, t)));
            }
            return new FingerSample { State = FingerState.Down, Point = point, Keyframe = k };
        }

        private static bool TryNumber(string obj, string key, out double value, out bool present)
        {
            value = 0;
            var raw = JsonBody.GetTopLevelRawValue(obj, key);
            present = raw != null && raw.Trim() != "null";
            if (!present) return true;
            return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   && !double.IsInfinity(value) && !double.IsNaN(value);
        }

        private static GesturePlan Fail(string c, string m, out string code, out string message)
        {
            code = c;
            message = m;
            return null;
        }
    }
}
