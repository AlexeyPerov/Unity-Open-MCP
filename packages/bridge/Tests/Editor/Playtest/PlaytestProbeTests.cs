// playtest_probe: the probe registry on synthetic scheduler ticks — sampling
// every N frames with frame / time, the incremental read cursor, every end
// (max_samples, until, stop, exiting play mode, a domain reload, repeated
// errors, an over-budget sample) removing the scheduler hook and keeping the
// buffer, the caps (running probes, max_samples, retained records), member
// path binding (instance, static, private, indexed, refused getters), the
// bounded value serialisation, the `probe` wait condition, argument checks
// and registration. The player loop is never touched; real play-mode probes
// on the demo are the manual checklist.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.Playtest;

namespace UnityOpenMcpBridge.Tests.Playtest
{
    public class PlaytestProbeTests
    {
        private readonly List<GameObject> created = new List<GameObject>();
        private int counter;

        [SetUp]
        public void SetUp()
        {
            PlayModeScheduler.ResetForTests();
            ProbeRegistry.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = () => true;
            PlayModeScheduler.SuppressPlayerLoop = true;
            counter = 0;
        }

        [TearDown]
        public void TearDown()
        {
            PlayModeScheduler.EndAll(PlayModeEndReason.Cancelled);
            PlayModeScheduler.ResetForTests();
            ProbeRegistry.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = null;
            PlayModeScheduler.SuppressPlayerLoop = false;
            foreach (var go in created) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            created.Clear();
        }

        // frame N at 100 ms per frame; play time = N * 0.1 s.
        private static void Tick(int frame) => PlayModeScheduler.TickAll(frame, 0.1f, 0.1f, 1f, 1000, 800, frame * 0.1);

        private static void Ticks(int from, int to) { for (int f = from; f <= to; f++) Tick(f); }

        private ProbeRecord Record(string id, Func<object> read = null, int every = 1, int max = 100, float budget = 5f, string until = null)
        {
            var record = PlaytestProbeTools.Validate(id, null, "value", null, 0, null, null, every, max, until, "gameplay", budget, out var refusal);
            Assert.IsNotNull(record, refusal?.ErrorMessage);
            record.Source = new ProbeSource { Kind = "member", Text = "value", Read = read ?? (() => counter++) };
            return record;
        }

        private static ProbeOperation Start(ProbeRecord record)
        {
            var op = ProbeRegistry.Start(record, out var error);
            Assert.IsNotNull(op, error);
            return op;
        }

        private static int Hooks => PlayModeScheduler.RunningCount(PlaytestProbeTools.ToolName);

        private static string Output(ToolDispatchResult result)
        {
            Assert.IsTrue(result.Success, result.ErrorCode + ": " + result.ErrorMessage);
            return result.Output;
        }

        // ---- sampling and reading ----------------------------------------

        [Test]
        public void Samples_EveryNFrames_UntilMaxSamples()
        {
            var record = Record("hp", every: 5, max: 4);
            Start(record);
            Assert.AreEqual(1, Hooks);
            Ticks(10, 40);
            Assert.AreEqual(ProbeState.MaxSamplesReached, record.State);
            Assert.AreEqual("max_samples_reached", record.ReasonCode);
            CollectionAssert.AreEqual(new[] { 10, 15, 20, 25 }, record.Samples.ConvertAll(s => s.Frame));
            Assert.AreEqual(2.5, record.Samples[3].Time, 1e-6);
            Assert.AreEqual("3", record.Samples[3].ValueJson);
            Assert.AreEqual(0, Hooks, "reaching max_samples removes the scheduler hook");
            StringAssert.Contains("\"numeric\":{\"count\":4,\"min\":0,\"max\":3,\"mean\":1.5}", Output(PlaytestProbeTools.Stop("hp")));
        }

        [Test]
        public void Read_IsIncrementalAndNeverClears()
        {
            Start(Record("inc", every: 1, max: 100));
            Ticks(1, 5);
            var first = Output(PlaytestProbeTools.Read("inc", 0, 3));
            StringAssert.Contains("\"next_cursor\":3", first);
            StringAssert.Contains("\"has_more\":true", first);
            StringAssert.Contains("\"seq\":2", first);
            Ticks(6, 7);
            var next = Output(PlaytestProbeTools.Read("inc", 3, null));
            StringAssert.DoesNotContain("\"seq\":2,", next);
            StringAssert.Contains("\"seq\":3,", next);
            StringAssert.Contains("\"seq\":6,", next);
            StringAssert.Contains("\"next_cursor\":7", next);
            StringAssert.Contains("\"has_more\":false", next);
            StringAssert.Contains("\"samples\":[]", Output(PlaytestProbeTools.Read("inc", 7, null)));
            StringAssert.Contains("\"seq\":0,", Output(PlaytestProbeTools.Read("inc", 0, 1)), "reading does not clear");
            Assert.AreEqual("invalid_arguments", PlaytestProbeTools.Read("inc", -1, null).ErrorCode);
            Assert.AreEqual("invalid_arguments", PlaytestProbeTools.Read("inc", 0, ProbeRegistry.MaxReadMax + 1).ErrorCode);
        }

        // ---- every end removes the hook and keeps the buffer -------------

        [Test]
        public void Stop_RemovesTheHookAndKeepsTheBuffer()
        {
            var record = Record("stop");
            Start(record);
            Ticks(1, 3);
            var stopped = Output(PlaytestProbeTools.Stop("stop"));
            StringAssert.Contains("\"stopped\":true", stopped);
            StringAssert.Contains("\"code\":\"stop_requested\"", stopped);
            StringAssert.Contains("\"hooked\":false", stopped);
            Assert.AreEqual(ProbeState.Stopped, record.State);
            Assert.AreEqual(0, Hooks);
            Ticks(4, 9);
            Assert.AreEqual(3, record.Samples.Count, "no sample after stop");
            Assert.IsNull(record.Source.Read, "the source let go of its reader");
            StringAssert.Contains("\"stopped\":false", Output(PlaytestProbeTools.Stop("stop")));
            StringAssert.Contains("\"seq\":2,", Output(PlaytestProbeTools.Read("stop", 0, null)));
        }

        [Test]
        public void ExitingPlayMode_EndsPlayModeExited()
        {
            var record = Record("exit");
            Start(record);
            Ticks(1, 4);
            PlayModeScheduler.EndAll(PlayModeEndReason.PlayModeExited);
            Assert.AreEqual(ProbeState.PlayModeExited, record.State);
            Assert.AreEqual("play_mode_exited", record.ReasonCode);
            Assert.AreEqual(0, Hooks);
            Ticks(5, 8);
            Assert.AreEqual(4, record.Samples.Count, "no sample after exit");
            PlayModeScheduler.IsPlayingOverride = () => false;
            StringAssert.Contains("\"state\":\"play_mode_exited\"", Output(PlaytestProbeTools.Read("exit", 0, null)));
            StringAssert.Contains("\"hooks\":{\"scheduler_probe_operations\":0", Output(PlaytestProbeTools.List()));
            Assert.AreEqual("play_mode_required", PlaytestProbeTools.Probe("start", "exit2", member: "UnityEngine.Time.frameCount").ErrorCode);
        }

        [Test]
        public void DomainReload_EndsAndReleases()
        {
            var record = Record("reload");
            Start(record);
            Tick(1);
            PlayModeScheduler.EndAll(PlayModeEndReason.DomainReload);
            Assert.AreEqual(ProbeState.Stopped, record.State);
            Assert.AreEqual("domain_reload", record.ReasonCode);
            Assert.IsNull(record.Source.Read);
            Assert.AreEqual(0, Hooks);
        }

        [Test]
        public void Cancel_ThroughTheOperation_EndsStopped()
        {
            var record = Record("cancel");
            var op = Start(record);
            Tick(1);
            PlayModeScheduler.Cancel(op.Id);
            Assert.AreEqual(ProbeState.Stopped, record.State);
            Assert.AreEqual("cancelled", record.ReasonCode);
        }

        [Test]
        public void ProbeNeverStalls()
        {
            var record = Record("idle");
            Start(record);
            double now = 0;
            PlayModeScheduler.ClockOverride = () => now;
            try
            {
                Tick(1);
                for (int i = 0; i < 40; i++) { now += 1000; PlayModeScheduler.CheckStalls(); }
                Assert.IsTrue(record.Running, "a paused Editor does not end a probe");
            }
            finally { PlayModeScheduler.ClockOverride = null; }
        }

        [Test]
        public void Until_EndsUntilMetWithTheFiredLeaf()
        {
            var record = Record("until", every: 2, until: "{\"any\":[{\"frames\":4},{\"ms\":60000}]}");
            Start(record);
            Ticks(1, 20);
            Assert.AreEqual(ProbeState.UntilMet, record.State);
            StringAssert.Contains("\"type\":\"frames\"", record.FiredJson);
            Assert.AreEqual(3, record.Samples.Count, "samples at ticks 0, 2, 4; until polled at each");
            Assert.AreEqual(0, Hooks);
        }

        // ---- errors and cost ---------------------------------------------

        [Test]
        public void ConsecutiveErrors_EndInError_AnIntermittentOneDoesNot()
        {
            var record = Record("throws", () => throw new InvalidOperationException("boom"));
            Start(record);
            Ticks(1, 10);
            Assert.AreEqual(ProbeState.Error, record.State);
            Assert.AreEqual("sample_errors", record.ReasonCode);
            Assert.AreEqual(ProbeRegistry.MaxConsecutiveErrors, record.Samples.Count);
            Assert.AreEqual("InvalidOperationException: boom", record.Samples[0].Error);
            Assert.AreEqual(0, Hooks);

            int n = 0;
            var flaky = Record("flaky", () => (n++ % 2 == 0) ? throw new InvalidOperationException("odd") : (object)n);
            Start(flaky);
            Ticks(11, 40);
            Assert.IsTrue(flaky.Running);
            Assert.AreEqual(15, flaky.Errors);
        }

        [Test]
        public void OverBudget_EndsAfterThreeInARow_OrOnceAtTenTimes()
        {
            double cost = 6;
            ProbeRegistry.ConfigureForTests = op => op.CostOverrideMs = () => cost;
            var record = Record("slow", budget: 5f);
            Start(record);
            Ticks(1, 2);
            Assert.IsTrue(record.Running);
            Assert.IsTrue(record.Samples[0].OverBudget);
            StringAssert.Contains("\"cost_ms\":6", Output(PlaytestProbeTools.Read("slow", 0, null)));
            Tick(3);
            Assert.AreEqual(ProbeState.Error, record.State);
            Assert.AreEqual("over_budget", record.ReasonCode);
            Assert.AreEqual(0, Hooks);

            cost = 50;
            var spike = Record("spike", budget: 5f);
            Start(spike);
            Tick(4);
            Assert.AreEqual("over_budget", spike.ReasonCode);
            Assert.AreEqual(1, spike.Samples.Count);

            cost = 4;
            var fine = Record("fine", budget: 5f);
            Start(fine);
            Ticks(5, 20);
            Assert.IsTrue(fine.Running);
            Assert.AreEqual(0, fine.OverBudget);
        }

        [Test]
        public void DestroyedTarget_IsASampleError()
        {
            var go = new GameObject("ProbeTarget");
            created.Add(go);
            var record = Record("target", () => 1);
            record.Source.Target = go;
            record.Source.TargetJson = "{}";
            Start(record);
            Tick(1);
            UnityEngine.Object.DestroyImmediate(go);
            Tick(2);
            Assert.AreEqual("MissingReferenceException: The probed object was destroyed.", record.Samples[1].Error);
        }

        // ---- caps --------------------------------------------------------

        [Test]
        public void Caps_RunningProbesAndRunningIds()
        {
            for (int i = 0; i < ProbeRegistry.MaxRunning; i++) Start(Record("cap" + i));
            StringAssert.Contains("probe_capacity", ProbeRegistry.RefuseStart("cap-extra"));
            Assert.IsNull(ProbeRegistry.Start(Record("cap-extra"), out var error));
            StringAssert.Contains("probe_capacity", error);
            Output(PlaytestProbeTools.Stop("cap0"));
            StringAssert.Contains("probe_running", ProbeRegistry.RefuseStart("cap1"));
            Assert.IsNull(ProbeRegistry.RefuseStart("cap0"), "a finished id may start again");
            var again = Record("cap0");
            Start(again);
            Assert.AreSame(again, ProbeRegistry.Get("cap0"), "the new start replaces the finished record");
        }

        [Test]
        public void Caps_RetainedRecords()
        {
            for (int i = 0; i < ProbeRegistry.MaxRetained + 5; i++)
            {
                Start(Record("r" + i, max: 1));
                Tick(i + 1);
            }
            Assert.AreEqual(ProbeRegistry.MaxRetained, ProbeRegistry.All.Count);
            Assert.IsNull(ProbeRegistry.Get("r0"), "the oldest finished record made room");
            Assert.IsNotNull(ProbeRegistry.Get("r" + (ProbeRegistry.MaxRetained + 4)));
        }

        [TestCase(0, 10, 5f, "every_frames")]
        [TestCase(601, 10, 5f, "every_frames")]
        [TestCase(1, 0, 5f, "max_samples")]
        [TestCase(1, 5001, 5f, "max_samples")]
        [TestCase(1, 10, 0f, "budget_ms")]
        [TestCase(1, 10, 101f, "budget_ms")]
        public void Validate_RefusesOutOfRange(int every, int max, float budget, string field)
        {
            Assert.IsNull(PlaytestProbeTools.Validate("p", null, "x", null, 0, null, null, every, max, null, null, budget, out var refusal));
            Assert.AreEqual("invalid_arguments", refusal.ErrorCode);
            StringAssert.Contains(field, refusal.ErrorMessage);
        }

        [Test]
        public void Validate_RefusesBadShapes()
        {
            void Refused(string why, string code, string id = "p", string expression = null, string member = "x", string path = null,
                long instanceId = 0, string component = null, string[] usings = null, string until = null, string time = null)
            {
                Assert.IsNull(PlaytestProbeTools.Validate(id, expression, member, path, instanceId, component, usings, 1, 10, until, time, null,
                    out var refusal), why);
                Assert.AreEqual(code, refusal.ErrorCode, why + ": " + refusal.ErrorMessage);
            }
            Refused("no id", "invalid_arguments", id: null);
            Refused("bad id", "invalid_arguments", id: "has space");
            Refused("both sources", "invalid_arguments", expression: "1");
            Refused("no source", "invalid_arguments", member: null);
            Refused("two selectors", "invalid_arguments", path: "A", instanceId: 5);
            Refused("component without target", "invalid_arguments", component: "UnityEngine.Rigidbody");
            Refused("usings on a member", "invalid_arguments", usings: new[] { "System" });
            Refused("bad time", "invalid_arguments", time: "wall");
            Refused("bad until", "invalid_arguments", until: "{\"sleep\":1}");
            Refused("until on a missing probe", "probe_not_found", until: "{\"probe\":{\"id\":\"nope\"}}");
            Assert.AreEqual("probe_not_found", PlaytestProbeTools.Read("nope", 0, null).ErrorCode);
            Assert.AreEqual("probe_not_found", PlaytestProbeTools.Stop("nope").ErrorCode);
            Assert.AreEqual("invalid_arguments", PlaytestProbeTools.Probe("jump").ErrorCode);
        }

        // ---- member paths ------------------------------------------------

        private class Inner { public int hp = 7; }
        private class Holder
        {
            private float secret = 1.5f;
            public List<Inner> waves = new List<Inner> { new Inner(), new Inner { hp = 9 } };
            public object boxed = new Inner { hp = 3 };
            public float Secret => secret;
        }

        private static object ReadMember(object root, string path)
        {
            var steps = ProbeSourceBinder.ParsePath(path, out var error);
            Assert.IsNotNull(steps, error);
            return ProbeSourceBinder.BindMember(root, null, steps)();
        }

        [Test]
        public void MemberPath_ReadsPrivateIndexedAndRuntimeTypedMembers()
        {
            var holder = new Holder();
            Assert.AreEqual(1.5f, ReadMember(holder, "secret"));
            Assert.AreEqual(1.5f, ReadMember(holder, "Secret"));
            Assert.AreEqual(9, ReadMember(holder, "waves[1].hp"));
            Assert.AreEqual(3, ReadMember(holder, "boxed.hp"), "members resolve on the runtime type");
            var ex = Assert.Throws<ProbeMemberException>(() => ReadMember(holder, "health"));
            Assert.AreEqual("member_not_found", ex.Code);
            StringAssert.Contains("waves", ex.Message);
            Assert.Throws<IndexOutOfRangeException>(() => ReadMember(holder, "waves[5].hp"));
            Assert.IsNull(ProbeSourceBinder.ParsePath("a..b", out _));
            Assert.IsNull(ProbeSourceBinder.ParsePath("a[x]", out _));
        }

        [Test]
        public void MemberPath_StaticRootAndRefusedGetters()
        {
            var type = ProbeSourceBinder.ResolveStaticRoot("UnityEngine.Time.frameCount", out var rest);
            Assert.AreEqual(typeof(Time), type);
            Assert.AreEqual("frameCount", rest);
            var steps = ProbeSourceBinder.ParsePath(rest, out _);
            Assert.IsInstanceOf<int>(ProbeSourceBinder.BindMember(null, type, steps)());
            Assert.IsNull(ProbeSourceBinder.ResolveStaticRoot("NoSuchType.Member", out _));

            var go = new GameObject("ProbeRenderer", typeof(MeshRenderer));
            created.Add(go);
            var ex = Assert.Throws<ProbeMemberException>(() => ReadMember(go.GetComponent<MeshRenderer>(), "material"));
            Assert.AreEqual("member_not_readable", ex.Code);
            StringAssert.Contains("sharedMaterial", ex.Message);
        }

        [Test]
        public void Bind_ResolvesTheTargetAndItsComponent()
        {
            var go = new GameObject("ProbeBindTarget", typeof(BoxCollider));
            created.Add(go);
            go.GetComponent<BoxCollider>().size = new Vector3(2, 3, 4);
            var source = PlaytestProbeTools.Bind("bind", null, "size.y", "ProbeBindTarget", 0, "UnityEngine.BoxCollider", null, out var refusal);
            Assert.IsNotNull(source, refusal?.ErrorMessage);
            Assert.AreEqual(3f, source.Read());
            StringAssert.Contains("\"component_type\":\"UnityEngine.BoxCollider\"", source.TargetJson);

            Assert.IsNull(PlaytestProbeTools.Bind("bind", null, "size", "ProbeBindTarget", 0, "UnityEngine.Rigidbody", null, out refusal));
            Assert.AreEqual("component_not_found", refusal.ErrorCode);
            Assert.IsNull(PlaytestProbeTools.Bind("bind", null, "size", "ProbeBindTarget", 0, "NoSuchComponent", null, out refusal));
            Assert.AreEqual("type_not_found", refusal.ErrorCode);
            Assert.IsNull(PlaytestProbeTools.Bind("bind", null, "nothing", "ProbeBindTarget", 0, "UnityEngine.BoxCollider", null, out refusal));
            Assert.AreEqual("member_not_found", refusal.ErrorCode);
            Assert.IsNull(PlaytestProbeTools.Bind("bind", null, "x", "NoSuchObject-ProbeTests", 0, null, null, out refusal));
            Assert.AreEqual("gameobject_not_found", refusal.ErrorCode);
            Assert.IsNull(PlaytestProbeTools.Bind("bind", null, "NoSuchType.member", null, 0, null, null, out refusal));
            Assert.AreEqual("type_not_found", refusal.ErrorCode);
        }

        [Test]
        public void Bind_RefusesAWritingExpressionBeforeCompiling()
        {
            Assert.IsNull(PlaytestProbeTools.Bind("w", "UnityEngine.Time.timeScale = 0", null, null, 0, null, null, out var refusal));
            Assert.AreEqual("expression_not_read_only", refusal.ErrorCode);
            StringAssert.Contains("assigns", refusal.ErrorMessage);
            Assert.IsNull(PlaytestProbeTools.Bind("w", "UnityEditor.AssetDatabase.Refresh()", null, null, 0, null, null, out refusal));
            Assert.AreEqual("expression_not_read_only", refusal.ErrorCode);
        }

        [Test]
        public void Bind_CompilesAnExpressionWithItsTarget()
        {
            if (!RoslynHost.Initialize()) Assert.Ignore("Roslyn is not available in this Editor.");
            var go = new GameObject("ProbeExprTarget", typeof(BoxCollider));
            created.Add(go);
            var source = PlaytestProbeTools.Bind("expr", "target.size.x * 2 + UnityEngine.Mathf.Abs(-1)", null, null,
                UnityOpenMcpBridge.ObjectRefs.InstanceId.Of(go), "UnityEngine.BoxCollider", null, out var refusal);
            Assert.IsNotNull(source, refusal?.ErrorMessage);
            Assert.AreEqual(3f, source.Read());
            source.Release();
            Assert.IsNull(PlaytestProbeTools.Bind("expr", "UnityEngine.Time.frameCount +", null, null, 0, null, null, out refusal));
            Assert.AreEqual("compilation_error", refusal.ErrorCode);
        }

        // ---- values ------------------------------------------------------

        private enum Mood { Calm, Angry }

        [Test]
        public void Values_AreBoundedJson()
        {
            string J(object v) => ProbeValue.ToJson(v, out _);
            Assert.AreEqual("null", J(null));
            Assert.AreEqual("42", J(42));
            Assert.AreEqual("1.5", J(1.5f));
            Assert.AreEqual("\"NaN\"", J(float.NaN));
            Assert.AreEqual("true", J(true));
            Assert.AreEqual("\"Angry\"", J(Mood.Angry));
            Assert.AreEqual("{\"x\":1,\"y\":2,\"z\":3}", J(new Vector3(1, 2, 3)));
            Assert.AreEqual("{\"r\":1,\"g\":0,\"b\":0,\"a\":1}", J(Color.red));
            Assert.AreEqual("[1,2,3]", J(new List<int> { 1, 2, 3 }));
            var many = new List<int>();
            for (int i = 0; i < 40; i++) many.Add(i);
            StringAssert.EndsWith("],\"$count\":40}", J(many));
            Assert.AreEqual("{\"a\":1}", J(new Dictionary<string, int> { ["a"] = 1 }));
            Assert.AreEqual("{\"hp\":3,\"pos\":{\"x\":0,\"y\":0,\"z\":0}}", J(new { hp = 3, pos = Vector3.zero }));
            Assert.AreEqual("{\"$type\":\"Inner\",\"hp\":7}", J(new Inner()));
            StringAssert.EndsWith("…\"", J(new string('a', 400)));
            var huge = new List<string>();
            for (int i = 0; i < 16; i++) huge.Add(new string('b', 200));
            var marker = J(huge);
            StringAssert.StartsWith("{\"$truncated\":true,\"$type\":\"List`1\"", marker);
            StringAssert.Contains("\"preview\":", marker);
            Assert.LessOrEqual(marker.Length, ProbeValue.MaxChars);
            ProbeValue.ToJson(7L, out var number);
            Assert.AreEqual(7.0, number);
            ProbeValue.ToJson("7", out number);
            Assert.IsNull(number);
        }

        // ---- the probe wait condition ------------------------------------

        [Test]
        public void WaitCondition_HoldsWhenTheProbeEnds()
        {
            var record = Record("waited", every: 1, max: 3);
            Start(record);
            var root = WaitConditionParser.Parse("{\"probe\":{\"id\":\"waited\"}}", "condition", out var error);
            Assert.IsNotNull(root, error);
            var running = WaitConditionParser.Parse("{\"probe\":{\"id\":\"waited\",\"state\":\"running\"}}", "condition", out error);
            var poll = new ConditionPoll { Now = new ConditionTick { Frame = 1 } };
            root.Evaluate(poll);
            running.Evaluate(poll);
            Assert.IsFalse(root.Met);
            Assert.IsTrue(running.Met);
            Ticks(1, 3);
            root.Evaluate(poll);
            Assert.IsTrue(root.Met);
            Assert.IsNull(WaitConditionParser.Parse("{\"probe\":{\"id\":\"waited\",\"state\":\"gone\"}}", "condition", out error));
            StringAssert.Contains("state must be", error);
        }

        // ---- registration --------------------------------------------------

        [Test]
        public void Registry_ProbeIsGateFreeReadOnlyPlaytest()
        {
            if (!BridgeToolRegistry.Contains("unity_open_mcp_inputsim_touch"))
                Assert.Ignore("Tool registry not scanned in this Editor session (batch -runTests).");
            Assert.IsTrue(BridgeToolRegistry.TryGet(PlaytestProbeTools.ToolName, out var info));
            Assert.IsFalse(info.IsMutating);
            Assert.AreEqual(GateMode.Off, info.Gate);
            Assert.AreEqual("playtest", info.Group);
            Assert.IsTrue(JsonBody.IsPatchArrayKey("until"), "until is a condition tree, never a selector source");
        }
    }
}
