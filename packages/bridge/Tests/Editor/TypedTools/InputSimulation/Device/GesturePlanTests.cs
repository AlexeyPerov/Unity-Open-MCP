// Gesture plan parsing, coordinate resolution and per-frame sampling.
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.Extensions.InputSimulation;

namespace UnityOpenMcpBridge.Tests.Extensions.InputSimulation
{
    public class GesturePlanTests
    {
        private const string SpecExample =
            "[{\"id\":0,\"path\":[" +
            "{\"at_ms\":0,\"x\":540,\"y\":1200,\"phase\":\"down\"}," +
            "{\"at_ms\":400,\"x\":700,\"y\":1100}," +
            "{\"at_ms\":2000,\"x\":700,\"y\":1100,\"phase\":\"hold\"}," +
            "{\"at_ms\":2600,\"x\":400,\"y\":900,\"phase\":\"up\"}]}]";

        private static GesturePlan Parse(string fingers, string coords = "screen", string time = "realtime")
        {
            var plan = GesturePlan.Parse(fingers, coords, time, out var code, out var message);
            Assert.IsNotNull(plan, code + ": " + message);
            return plan;
        }

        private static string ParseError(string fingers, string coords = "screen", string time = "realtime")
        {
            var plan = GesturePlan.Parse(fingers, coords, time, out var code, out var message);
            Assert.IsNull(plan, "expected a refusal");
            Assert.AreEqual("invalid_arguments", code);
            return message;
        }

        [Test]
        public void ParsesTheSpecExample()
        {
            var plan = Parse(SpecExample);
            Assert.AreEqual(1, plan.Fingers.Count);
            Assert.AreEqual(2600, plan.DurationMs);
            var path = plan.Fingers[0].Path;
            Assert.AreEqual(new[] { "down", "move", "hold", "up" }, path.ConvertAll(k => k.Phase).ToArray());
            Assert.IsTrue(plan.ResolvePoints(new Vector2(1080, 1920), null, out _, out _));
            Assert.AreEqual(new Vector2(540, 1200), path[0].Point);
        }

        [Test]
        public void ImplicitDownAndUpPhases()
        {
            var plan = Parse("[{\"path\":[{\"at_ms\":0,\"x\":1,\"y\":2},{\"at_ms\":50,\"x\":3,\"y\":4},{\"at_ms\":100,\"x\":5,\"y\":6}]}]");
            var path = plan.Fingers[0].Path;
            Assert.AreEqual("down", path[0].Phase);
            Assert.AreEqual("move", path[1].Phase);
            Assert.AreEqual("up", path[2].Phase);
            Assert.AreEqual(0, plan.Fingers[0].Id, "a missing id defaults to the finger's index");
        }

        [Test]
        public void SamplingInterpolatesHoldsAndLifts()
        {
            var plan = Parse(SpecExample);
            plan.ResolvePoints(new Vector2(1080, 1920), null, out _, out _);
            var finger = plan.Fingers[0];

            var s = GesturePlan.Sample(finger, 0);
            Assert.AreEqual(FingerState.Down, s.State);
            Assert.AreEqual(new Vector2(540, 1200), s.Point);

            s = GesturePlan.Sample(finger, 200);
            Assert.AreEqual(FingerState.Down, s.State);
            Assert.AreEqual(620f, s.Point.x, 0.01f, "halfway between 540 and 700");
            Assert.AreEqual(1150f, s.Point.y, 0.01f);

            s = GesturePlan.Sample(finger, 1500);
            Assert.AreEqual(new Vector2(700, 1100), s.Point, "hold keeps the previous point");
            Assert.AreEqual(1, s.Keyframe);

            s = GesturePlan.Sample(finger, 2300);
            Assert.AreEqual(550f, s.Point.x, 0.01f, "the up segment interpolates toward the lift point");
            Assert.AreEqual(2, s.Keyframe);

            s = GesturePlan.Sample(finger, 2600);
            Assert.AreEqual(FingerState.Up, s.State);
            Assert.AreEqual(new Vector2(400, 900), s.Point);
            Assert.AreEqual(3, s.Keyframe);
        }

        [Test]
        public void HoldInheritsPointWhenXYOmitted()
        {
            var plan = Parse("[{\"path\":[{\"at_ms\":100,\"x\":10,\"y\":20},{\"at_ms\":500,\"phase\":\"hold\"},{\"at_ms\":600,\"x\":30,\"y\":40}]}]");
            plan.ResolvePoints(Vector2.one, null, out _, out _);
            Assert.AreEqual(new Vector2(10, 20), plan.Fingers[0].Path[1].Point);
            Assert.AreEqual(FingerState.NotStarted, GesturePlan.Sample(plan.Fingers[0], 50).State, "before the down keyframe");
        }

        [Test]
        public void NormalizedCoordinatesScaleToScreen()
        {
            var plan = Parse("[{\"path\":[{\"at_ms\":0,\"x\":0.5,\"y\":0.25},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "normalized");
            Assert.IsTrue(plan.ResolvePoints(new Vector2(2000, 1000), null, out _, out _));
            Assert.AreEqual(new Vector2(1000, 250), plan.Fingers[0].Path[0].Point);
            Assert.AreEqual(new Vector2(2000, 1000), plan.Fingers[0].Path[1].Point);
        }

        [Test]
        public void TargetCoordinatesUseResolverAndOffsets()
        {
            var plan = Parse("[{\"path\":[{\"at_ms\":0,\"target\":\"A\"},{\"at_ms\":10,\"target\":\"B\",\"x\":5,\"y\":-5}]}]", "target");
            Assert.IsTrue(plan.ResolvePoints(Vector2.zero,
                t => (true, t == "A" ? new Vector2(100, 100) : new Vector2(300, 300), null, null), out _, out _));
            Assert.AreEqual(new Vector2(100, 100), plan.Fingers[0].Path[0].Point);
            Assert.AreEqual(new Vector2(305, 295), plan.Fingers[0].Path[1].Point);

            Assert.IsFalse(plan.ResolvePoints(Vector2.zero, t => (false, Vector2.zero, "ambiguous_target", "two matches"), out var code, out var message));
            Assert.AreEqual("ambiguous_target", code);
            StringAssert.Contains("two matches", message);
        }

        [Test]
        public void TargetTextKeyframesUseTheLabelResolver()
        {
            var plan = Parse("[{\"path\":[{\"at_ms\":0,\"target_text\":\"Buy\",\"under\":\"Shop\",\"index\":1,\"text_match\":\"contains\"}," +
                             "{\"at_ms\":10,\"target\":\"B\",\"x\":2,\"y\":3}]}]", "target");
            var key = plan.Fingers[0].Path[0];
            Assert.AreEqual("Buy", key.TargetText);
            Assert.AreEqual("Shop", key.Under);
            Assert.AreEqual(1, key.Index);
            Assert.AreEqual("contains", key.TextMatch);
            string seenContext = null;
            Assert.IsTrue(plan.ResolvePoints(Vector2.zero,
                t => (true, new Vector2(300, 300), null, null),
                (k, context) => { seenContext = context; return (true, new Vector2(40, 50), "{\"path\":\"Shop/Btn\"}", null); },
                out _, out _, out var errorJson), errorJson);
            Assert.AreEqual(new Vector2(40, 50), key.Point);
            Assert.AreEqual("{\"path\":\"Shop/Btn\"}", key.TextTargetJson);
            Assert.AreEqual("fingers id 0 keyframe 0", seenContext);
            Assert.AreEqual(new Vector2(302, 303), plan.Fingers[0].Path[1].Point);

            // A resolver refusal surfaces its complete error body.
            Assert.IsFalse(plan.ResolvePoints(Vector2.zero, t => (true, Vector2.zero, null, null),
                (k, c) => (false, Vector2.zero, null, "{\"error\":{\"code\":\"ambiguous_target\"}}"),
                out var code, out _, out errorJson));
            StringAssert.Contains("ambiguous_target", errorJson);
            Assert.AreEqual("text_target_failed", code);

            // Without the uGUI resolver (old overload) a label keyframe refuses honestly.
            Assert.IsFalse(plan.ResolvePoints(Vector2.zero, t => (true, Vector2.zero, null, null), out code, out _));
            Assert.AreEqual("ui_text_unavailable", code);
        }

        [Test]
        public void TargetTextKeyframeArgumentsAreValidated()
        {
            var plan = GesturePlan.Parse("[{\"path\":[{\"at_ms\":0,\"target\":\"A\",\"target_text\":\"Buy\"},{\"at_ms\":1,\"target\":\"A\"}]}]",
                "target", "realtime", out var code, out var message);
            Assert.IsNull(plan);
            Assert.AreEqual("conflicting_targets", code, message);
            StringAssert.Contains("refine target_text",
                ParseError("[{\"path\":[{\"at_ms\":0,\"target\":\"A\",\"under\":\"Shop\"},{\"at_ms\":1,\"target\":\"A\"}]}]", "target"));
            StringAssert.Contains("only valid with coords: target",
                ParseError("[{\"path\":[{\"at_ms\":0,\"target_text\":\"Buy\",\"x\":0,\"y\":0},{\"at_ms\":1,\"x\":0,\"y\":0}]}]"));
            StringAssert.Contains("index must be an integer",
                ParseError("[{\"path\":[{\"at_ms\":0,\"target_text\":\"Buy\",\"index\":-1},{\"at_ms\":1,\"target\":\"A\"}]}]", "target"));
        }

        [Test]
        public void GameplayTimeIsRecorded()
        {
            Assert.AreEqual(PlayModeTimeBase.Gameplay, Parse(SpecExample, "screen", "gameplay").Time);
        }

        [TestCase("[]", "1-10 entries")]
        [TestCase("{}", "non-empty array")]
        [TestCase("[{\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1}]}]", "2-256 keyframes")]
        [TestCase("[{\"path\":[{\"at_ms\":10,\"x\":1,\"y\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "greater than the previous")]
        [TestCase("[{\"path\":[{\"at_ms\":0,\"x\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "both x and y")]
        [TestCase("[{\"path\":[{\"at_ms\":0},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "needs x and y")]
        [TestCase("[{\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1,\"phase\":\"move\"},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "first keyframe must be phase down")]
        [TestCase("[{\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1},{\"at_ms\":5,\"x\":1,\"y\":1,\"phase\":\"up\"},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "only allowed on the first/last")]
        [TestCase("[{\"path\":[{\"at_ms\":-1,\"x\":1,\"y\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "at_ms must be a number")]
        [TestCase("[{\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1},{\"at_ms\":400000,\"x\":1,\"y\":1}]}]", "capped")]
        [TestCase("[{\"id\":1,\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]},{\"id\":1,\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "appears twice")]
        [TestCase("[{\"id\":12,\"path\":[{\"at_ms\":0,\"x\":1,\"y\":1},{\"at_ms\":10,\"x\":1,\"y\":1}]}]", "integer 0-9")]
        public void RejectsInvalidPlans(string fingers, string expected)
        {
            StringAssert.Contains(expected, ParseError(fingers));
        }

        [Test]
        public void RejectsBadModes()
        {
            StringAssert.Contains("coords", ParseError(SpecExample, "pixels"));
            StringAssert.Contains("time", ParseError(SpecExample, "screen", "wallclock"));
            StringAssert.Contains("0..1", ParseError("[{\"path\":[{\"at_ms\":0,\"x\":2,\"y\":0},{\"at_ms\":1,\"x\":0,\"y\":0}]}]", "normalized"));
            StringAssert.Contains("only valid with coords: target", ParseError("[{\"path\":[{\"at_ms\":0,\"target\":\"A\",\"x\":0,\"y\":0},{\"at_ms\":1,\"x\":0,\"y\":0}]}]"));
            StringAssert.Contains("needs target", ParseError("[{\"path\":[{\"at_ms\":0,\"x\":0,\"y\":0},{\"at_ms\":1,\"target\":\"A\"}]}]", "target"));
        }
    }
}
