// playtest_wait: condition parsing and refusals, each condition type firing at
// the right frame on synthetic scheduler ticks (log by the frame it was logged
// in, even across the longest poll interval; state conditions by the frame that
// changed them, delays by the frame they elapsed at; realtime `ms` by the wall
// clock), all / any, poll_every_frames, timeout with the last observed
// state, fail_on exception with its stack, time_scale applied once and restored
// on every exit path (completion, timeout, cancel, exit play, reload) unless the
// game changed it, the exclusive time-scale resource, the project-command
// predicate, registration and the batch refusal. The player loop is never
// touched; real play-mode waits on the demo fixture are the manual checklist.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityOpenMcpBridge.MetaTools;
using UnityOpenMcpBridge.Playtest;

namespace UnityOpenMcpBridge.Tests.Playtest
{
    public class PlaytestWaitTests
    {
        private const string Id = "wait-0000-0001";
        private float fakeScale = 1f;
        private readonly List<GameObject> created = new List<GameObject>();

        public static string Done(int at = 3) => "{\"cleared\":" + (calls++ >= at ? "true" : "false") + "}";
        public static string Mutates() => "true";
        private static int calls;

        [SetUp]
        public void SetUp()
        {
            PlayModeScheduler.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = () => true;
            PlayModeScheduler.SuppressPlayerLoop = true;
            fakeScale = 1f;
            calls = 0;
        }

        [TearDown]
        public void TearDown()
        {
            PlayModeScheduler.EndAll(PlayModeEndReason.Cancelled);
            PlayModeScheduler.ResetForTests();
            PlayModeScheduler.IsPlayingOverride = null;
            PlayModeScheduler.SuppressPlayerLoop = false;
            foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            BridgeToolRegistry.Scan();
        }

        // frame N at 100 ms per frame, play time = N * 0.1 s scaled.
        private static void Tick(int frame, float timeScale = 1f)
            => PlayModeScheduler.TickAll(frame, 0.1f * timeScale, 0.1f, timeScale, 1000, 800, frame * 0.1 * timeScale);

        private PlaytestWaitOperation Wait(string condition, int timeoutMs = 30000, int pollEvery = 1, float? timeScale = null,
            string time = "gameplay", string[] failOn = null, double? wallMs = null)
        {
            var op = PlaytestWaitTools.Create(condition, timeoutMs, pollEvery, timeScale, time, failOn, out var refusal);
            Assert.IsNotNull(op, refusal);
            op.ReadTimeScale = () => fakeScale;
            op.WriteTimeScale = v => fakeScale = v;
            if (wallMs.HasValue) op.WallClockOverride = () => wallMs.Value;
            Assert.IsNotNull(PlayModeScheduler.Start(op, Id, out var error), error);
            return op;
        }

        private static string Status() => PlayModeScheduler.Status(Id);

        // ---- parsing -------------------------------------------------------

        [TestCase("{\"log\":{\"pattern\":\"done\"}}", "log")]
        [TestCase("{\"ui\":{\"text_contains\":\"Victory\",\"matches\":false}}", "ui")]
        [TestCase("{\"object\":{\"path\":\"A/B\",\"state\":\"destroyed\"}}", "object")]
        [TestCase("{\"frames\":10}", "frames")]
        [TestCase("{\"ms\":250.5}", "ms")]
        [TestCase("{\"any\":[{\"ms\":5},{\"frames\":2}]}", "any")]
        [TestCase("{\"all\":[{\"ms\":5},{\"log\":{\"pattern\":\"x\",\"level\":\"error\"}}]}", "all")]
        public void Parse_AcceptsEveryShape(string condition, string type)
        {
            if (type == "ui" && PlaytestProbes.Ui == null) Assert.Ignore("uGUI is not compiled in.");
            var root = WaitConditionParser.Parse(condition, "condition", out var error);
            Assert.IsNotNull(root, error);
            Assert.AreEqual(type, root.Type);
        }

        [TestCase("{}", "exactly one key")]
        [TestCase("{\"ms\":1,\"frames\":2}", "exactly one key")]
        [TestCase("{\"sleep\":1}", "unknown condition")]
        [TestCase("{\"any\":[{\"all\":[{\"ms\":1}]}]}", "nest one level")]
        [TestCase("{\"any\":[]}", "1..16")]
        [TestCase("{\"log\":{\"pattern\":\"(\"}}", "not a valid regex")]
        [TestCase("{\"log\":{\"pattern\":\"x\",\"level\":\"fatal\"}}", "level must be")]
        [TestCase("{\"log\":{\"pattern\":\"x\",\"color\":1}}", "is unknown")]
        [TestCase("{\"object\":{\"state\":\"active\"}}", "exactly one of path or object_id")]
        [TestCase("{\"object\":{\"path\":\"A\",\"object_id\":\"5\"}}", "exactly one of path or object_id")]
        [TestCase("{\"object\":{\"path\":\"A\",\"state\":\"gone\"}}", "state must be")]
        [TestCase("{\"frames\":1.5}", "integer")]
        [TestCase("{\"ms\":-1}", "0..")]
        public void Parse_RefusesBadShapes(string condition, string message)
        {
            Assert.IsNull(WaitConditionParser.Parse(condition, "condition", out var error));
            StringAssert.Contains(message, error);
        }

        [Test]
        public void Create_ValidatesArguments()
        {
            const string ok = "{\"frames\":1}";
            Assert.IsNotNull(PlaytestWaitTools.Create(ok, null, 1, null, null, null, out _));
            void Refused(string why, string condition, int? timeout = 1000, int poll = 1, float? scale = null, string time = "gameplay", string[] failOn = null)
            {
                Assert.IsNull(PlaytestWaitTools.Create(condition, timeout, poll, scale, time, failOn, out var refusal), why);
                StringAssert.Contains("invalid_arguments", refusal, why);
            }
            Refused("no condition", null);
            Refused("timeout 0", ok, timeout: 0);
            Refused("timeout too long", ok, timeout: PlaytestWaitTools.MaxTimeoutMs + 1);
            Refused("poll 0", ok, poll: 0);
            Refused("poll too large", ok, poll: PlaytestWaitTools.MaxPollEveryFrames + 1);
            Refused("scale 0", ok, scale: 0f);
            Refused("scale > 100", ok, scale: 101f);
            Refused("time", ok, time: "wall");
            Refused("fail_on", ok, failOn: new[] { "warning" });
        }

        [Test]
        public void ProjectCommand_UnknownOrMutatingCommandsRefuseWithStableCodes()
        {
            Assert.IsNull(PlaytestWaitTools.Create("{\"project_command\":{\"id\":\"project.tests.missing\"}}", 1000, 1, null, null, null, out var refusal));
            StringAssert.Contains("command_unavailable", refusal);

            Register("Mutates", mutating: true);
            Assert.IsNull(PlaytestWaitTools.Create("{\"project_command\":{\"id\":\"project.tests.wait\"}}", 1000, 1, null, null, null, out refusal));
            StringAssert.Contains("command_not_allowed", refusal);
        }

        [TestCase("true", null, true)]
        [TestCase(" true ", null, true)]
        [TestCase("false", null, false)]
        [TestCase("{\"cleared\":true}", "cleared", true)]
        [TestCase("{\"cleared\":false}", "cleared", false)]
        [TestCase("{\"other\":true}", "cleared", false)]
        [TestCase("{\"cleared\":true}", null, false)]
        public void ProjectCommand_TruthRule(string output, string field, bool expected)
            => Assert.AreEqual(expected, ProjectCommandCondition.IsTrue(output, field));

        // ---- firing ----------------------------------------------------------

        [Test]
        public void Frames_FireAfterThatManyFrames()
        {
            Wait("{\"frames\":3}");
            for (int f = 10; f <= 12; f++) { Tick(f); StringAssert.Contains("\"state\":\"running\"", Status()); }
            Tick(13);
            var status = Status();
            StringAssert.Contains("\"state\":\"succeeded\"", status);
            StringAssert.Contains("\"outcome\":\"fired\"", status);
            StringAssert.Contains("\"fired\":{\"path\":\"condition\",\"type\":\"frames\",\"frame\":13", status);
        }

        [Test]
        public void Ms_CountsGameplayTimeIncludingTheTimeScale()
        {
            // 100 ms frames at time_scale 4 = 400 ms of game time per frame.
            Wait("{\"ms\":1000}", timeScale: 4f);
            Tick(1, 4f); Tick(2, 4f); Tick(3, 4f);
            StringAssert.Contains("\"state\":\"running\"", Status());
            Tick(4, 4f);
            StringAssert.Contains("\"outcome\":\"fired\"", Status());
        }

        [Test]
        public void Log_FiresAtTheFrameItWasLoggedIn()
        {
            var op = Wait("{\"log\":{\"pattern\":\"Wave \\\\d+ cleared\"}}");
            Tick(20); Tick(21);
            op.Watcher.Record(new LoggedMessage { Message = "Wave 2 started", Type = LogType.Log, Frame = 21, PlayTime = 2.1 });
            Tick(22);
            StringAssert.Contains("\"state\":\"running\"", Status());
            // Logged during frame 22's Update, seen by the tick at the start of 23.
            op.Watcher.Record(new LoggedMessage { Message = "Wave 2 cleared", Type = LogType.Log, Frame = 22, PlayTime = 2.2 });
            Tick(23);
            var status = Status();
            StringAssert.Contains("\"fired\":{\"path\":\"condition\",\"type\":\"log\",\"frame\":22,\"observed_frame\":23,\"play_time\":2.2", status);
            StringAssert.Contains("\"message\":\"Wave 2 cleared\"", status);
        }

        [Test]
        public void Log_KeepsItsFrameTimeAcrossTheLongestPollInterval()
        {
            var op = Wait("{\"log\":{\"pattern\":\"boss\"}}", pollEvery: PlaytestWaitTools.MaxPollEveryFrames);
            Tick(0); Tick(1);
            op.Watcher.Record(new LoggedMessage { Message = "boss spawned", Type = LogType.Log, Frame = 1, PlayTime = 0.1 });
            for (int f = 2; f <= PlaytestWaitTools.MaxPollEveryFrames; f++) Tick(f);
            StringAssert.Contains("\"type\":\"log\",\"frame\":1,\"observed_frame\":600,\"play_time\":0.1,\"elapsed_ms\":100", Status());
        }

        [Test]
        public void Ms_RealtimeIsTheWallClock()
        {
            double wall = 0;
            var op = PlaytestWaitTools.Create("{\"ms\":900}", 30000, 1, null, "realtime", null, out var refusal);
            Assert.IsNotNull(op, refusal);
            op.WallClockOverride = () => wall;
            PlayModeScheduler.Start(op, Id, out _);
            // One 1 s frame: the scheduler's per-frame clamped realtime would count 250 ms.
            PlayModeScheduler.TickAll(1, 1f, 1f, 1f, 1000, 800, 1);
            wall = 1000;
            PlayModeScheduler.TickAll(2, 1f, 1f, 1f, 1000, 800, 2);
            StringAssert.Contains("\"fired\":{\"path\":\"condition\",\"type\":\"ms\",\"frame\":2", Status());
        }

        [Test]
        public void Log_LevelFilterSkipsOtherTypes()
        {
            var op = Wait("{\"log\":{\"pattern\":\"boom\",\"level\":\"error\"}}");
            Tick(1);
            op.Watcher.Record(new LoggedMessage { Message = "boom", Type = LogType.Warning, Frame = 1 });
            Tick(2);
            StringAssert.Contains("\"state\":\"running\"", Status());
            op.Watcher.Record(new LoggedMessage { Message = "boom", Type = LogType.Error, Frame = 2 });
            Tick(3);
            StringAssert.Contains("\"outcome\":\"fired\"", Status());
            StringAssert.Contains("\"errors\":[\"Error: boom\"]", Status());
        }

        [Test]
        public void Object_DestroyedReportsTheFrameThatDestroyedIt()
        {
            var go = new GameObject("WaitTest_Enemy_" + System.Guid.NewGuid().ToString("N").Substring(0, 6));
            created.Add(go);
            Wait("{\"object\":{\"path\":\"" + go.name + "\",\"state\":\"destroyed\"}}");
            Tick(40); Tick(41);
            StringAssert.Contains("\"state\":\"running\"", Status());
            Object.DestroyImmediate(go);   // destroyed during frame 41
            Tick(42);
            var status = Status();
            StringAssert.Contains("\"fired\":{\"path\":\"condition\",\"type\":\"object\",\"frame\":41,\"observed_frame\":42", status);
            StringAssert.Contains("\"ever_found\":true", status);
        }

        [Test]
        public void Object_ActiveAndInactiveFollowActiveInHierarchy()
        {
            var go = new GameObject("WaitTest_Panel_" + System.Guid.NewGuid().ToString("N").Substring(0, 6));
            created.Add(go);
            go.SetActive(false);
            Wait("{\"object\":{\"path\":\"" + go.name + "\",\"state\":\"active\"}}");
            Tick(1); Tick(2);
            StringAssert.Contains("\"state\":\"running\"", Status());
            go.SetActive(true);
            Tick(3);
            var status = Status();
            StringAssert.Contains("\"outcome\":\"fired\"", status);
            StringAssert.Contains("\"found\":true,\"active\":true", status);
        }

        [Test]
        public void ProjectCommand_PolledUntilItsFieldIsTrue()
        {
            Register("Done");
            Wait("{\"project_command\":{\"id\":\"project.tests.wait\",\"args\":{\"at\":2},\"field\":\"cleared\"}}");
            Tick(1); Tick(2);
            StringAssert.Contains("\"state\":\"running\"", Status());
            Tick(3);   // third call returns cleared:true
            var status = Status();
            StringAssert.Contains("\"type\":\"project_command\",\"frame\":2,\"observed_frame\":3", status);
            StringAssert.Contains("\"calls\":3", status);
        }

        [Test]
        public void Any_NamesTheChildThatFired_AllWaitsForEveryLeaf()
        {
            Wait("{\"any\":[{\"frames\":50},{\"frames\":2}]}");
            Tick(1); Tick(2); Tick(3);
            StringAssert.Contains("\"fired\":{\"path\":\"condition.any[1]\",\"type\":\"frames\"", Status());

            PlayModeScheduler.ResetForTests();
            Wait("{\"all\":[{\"frames\":1},{\"frames\":3}]}");
            Tick(1); Tick(2); Tick(3);
            StringAssert.Contains("\"state\":\"running\"", Status());
            Tick(4);
            var status = Status();
            StringAssert.Contains("\"fired\":{\"path\":\"condition.all\",\"type\":\"all\",\"frame\":4", status);
        }

        [Test]
        public void PollEveryFrames_EvaluatesOnlyEveryNthTick()
        {
            Wait("{\"frames\":100}", pollEvery: 5, timeoutMs: 1000, wallMs: 0);
            for (int f = 0; f < 12; f++) Tick(f);
            PlayModeScheduler.Cancel(Id);
            StringAssert.Contains("\"polls\":3,\"poll_every_frames\":5", Status());
        }

        [Test]
        public void Timeout_FailsWithTimedOutAndTheLastObservedState()
        {
            double wall = 0;
            var op = PlaytestWaitTools.Create("{\"frames\":1000}", 500, 1, null, null, null, out _);
            op.WallClockOverride = () => wall;
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1); wall = 400; Tick(2);
            StringAssert.Contains("\"state\":\"running\"", Status());
            wall = 501; Tick(3);
            var status = Status();
            StringAssert.Contains("\"state\":\"failed\"", status);
            StringAssert.Contains("\"outcome\":\"timed_out\",\"error_code\":\"timed_out\"", status);
            StringAssert.Contains("\"fired\":null", status);
            StringAssert.Contains("\"last\":{\"frames\":1000,\"elapsed_frames\":2}", status);
        }

        [Test]
        public void FailOnException_StopsWithTheMessageAndStack()
        {
            var op = Wait("{\"frames\":1000}", failOn: new[] { "exception" });
            Tick(1);
            op.Watcher.Record(new LoggedMessage { Message = "handled warning", Type = LogType.Warning, Frame = 1 });
            op.Watcher.Record(new LoggedMessage { Message = "an error log", Type = LogType.Error, Frame = 1 });
            Tick(2);
            StringAssert.Contains("\"state\":\"running\"", Status());   // errors do not stop fail_on exception
            op.Watcher.Record(new LoggedMessage
            {
                Message = "InvalidOperationException: boom", Type = LogType.Exception, Frame = 2, PlayTime = 0.2,
                Stack = "Game.Shop.Buy () (at Assets/Shop.cs:42)\nUnityEngine.Events.InvokableCall.Invoke ()",
            });
            Tick(3);
            var status = Status();
            StringAssert.Contains("\"state\":\"failed\"", status);
            StringAssert.Contains("\"error_code\":\"exception_thrown\"", status);
            StringAssert.Contains("\"exception\":{\"type\":\"Exception\",\"message\":\"InvalidOperationException: boom\",\"stack\":\"Game.Shop.Buy () (at Assets/Shop.cs:42)", status);
            StringAssert.Contains("\"errors\":[\"Error: an error log\",\"Exception: InvalidOperationException: boom @ Game.Shop.Buy () (at Assets/Shop.cs:42)\"]", status);
        }

        [Test]
        public void FailOnError_StopsOnAnErrorLog()
        {
            var op = Wait("{\"frames\":1000}", failOn: new[] { "error" });
            Tick(1);
            op.Watcher.Record(new LoggedMessage { Message = "missing sprite", Type = LogType.Error, Frame = 1 });
            Tick(2);
            StringAssert.Contains("\"error_code\":\"error_logged\"", Status());
        }

        // ---- time scale --------------------------------------------------------

        [TestCase("Completed")]
        [TestCase("Cancelled")]
        [TestCase("PlayModeExited")]
        [TestCase("DomainReload")]
        [TestCase("EditorQuit")]
        public void TimeScale_AppliedOnTheFirstTickAndRestoredOnEveryExitPath(string exitPath)
        {
            var reason = (PlayModeEndReason)System.Enum.Parse(typeof(PlayModeEndReason), exitPath);
            fakeScale = 0.75f;
            Wait(reason == PlayModeEndReason.Completed ? "{\"frames\":2}" : "{\"frames\":1000}", timeScale: 4f);
            Assert.AreEqual(0.75f, fakeScale, "not applied before the first tick");
            Tick(1);
            Assert.AreEqual(4f, fakeScale);
            Tick(2, 4f);
            if (reason == PlayModeEndReason.Completed) Tick(3, 4f);
            else if (reason == PlayModeEndReason.Cancelled) PlayModeScheduler.Cancel(Id);
            else PlayModeScheduler.EndAll(reason);
            Assert.AreEqual(0.75f, fakeScale, "restored");
            StringAssert.Contains("\"time_scale\":{\"requested\":4,\"applied\":true,\"previous\":0.75,\"restored\":true,\"final\":0.75}", Status());
        }

        [Test]
        public void TimeScale_RestoredOnTimeout()
        {
            double wall = 0;
            var op = PlaytestWaitTools.Create("{\"frames\":1000}", 100, 1, 2f, null, null, out _);
            op.ReadTimeScale = () => fakeScale;
            op.WriteTimeScale = v => fakeScale = v;
            op.WallClockOverride = () => wall;
            PlayModeScheduler.Start(op, Id, out _);
            Tick(1);
            Assert.AreEqual(2f, fakeScale);
            wall = 200; Tick(2, 2f);
            StringAssert.Contains("\"error_code\":\"timed_out\"", Status());
            Assert.AreEqual(1f, fakeScale);
        }

        [Test]
        public void TimeScale_ChangedByTheGameIsKeptAndReported()
        {
            Wait("{\"frames\":1000}", timeScale: 3f);
            Tick(1);
            fakeScale = 0f;   // the game paused itself
            PlayModeScheduler.Cancel(Id);
            Assert.AreEqual(0f, fakeScale);
            var status = Status();
            StringAssert.Contains("\"restored\":false,\"final\":0", status);
            StringAssert.Contains("Game code changed Time.timeScale", status);
        }

        [Test]
        public void TimeScale_IsAnExclusiveResource_PlainWaitsShare()
        {
            Wait("{\"frames\":1000}", timeScale: 2f);
            var second = PlaytestWaitTools.Create("{\"frames\":1000}", 1000, 1, 3f, null, null, out _);
            Assert.IsNull(PlayModeScheduler.Start(second, "wait-0000-0002", out var error));
            StringAssert.Contains("playmode_operation_busy", error);
            var plain = PlaytestWaitTools.Create("{\"frames\":1000}", 1000, 1, null, null, null, out _);
            Assert.IsNotNull(PlayModeScheduler.Start(plain, "wait-0000-0003", out error), error);
        }

        // ---- registration, batch, selector scope ---------------------------

        [Test]
        public void Registry_WaitIsGateFreePlaytest()
        {
            if (!BridgeToolRegistry.Contains("unity_open_mcp_inputsim_touch"))
                Assert.Ignore("Tool registry not scanned in this Editor session (batch -runTests).");
            Assert.IsTrue(BridgeToolRegistry.TryGet(PlaytestWaitTools.ToolName, out var info));
            Assert.IsFalse(info.IsMutating);
            Assert.AreEqual(GateMode.Off, info.Gate);
            Assert.AreEqual("playtest", info.Group);
        }

        [Test]
        public void Batch_WaitIsRefusedAsServerPolled()
        {
            var result = BatchExecuteTool.Execute("{\"commands\":[{\"tool\":\"unity_open_mcp_playtest_wait\",\"params\":{\"condition\":{\"frames\":1}}}]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("batch_step_requires_server_poll", result.ErrorCode);
        }

        [Test]
        public void EditMode_RefusesWithPlayModeRequired()
        {
            if (UnityEditor.EditorApplication.isPlaying) Assert.Ignore("Runs in edit mode.");
            StringAssert.Contains("play_mode_required", PlaytestWaitTools.Wait("{\"frames\":1}"));
        }

        [Test]
        public void SelectorScope_ConditionPayloadNeverShadowsTopLevelArguments()
        {
            // A project command argument named like a top-level parameter must
            // not be read as that parameter (registry argument extraction).
            var body = "{\"condition\":{\"project_command\":{\"id\":\"project.x.y\",\"args\":{\"time\":\"realtime\",\"timeout_ms\":5}}},\"time\":\"gameplay\",\"timeout_ms\":9000}";
            var scope = JsonBody.SelectorScope(body);
            Assert.AreEqual("\"gameplay\"", JsonBody.GetRawValue(scope, "time"));
            Assert.AreEqual("9000", JsonBody.GetRawValue(scope, "timeout_ms"));
            Assert.IsTrue(JsonBody.IsPatchArrayKey("condition"));
            Assert.IsTrue(JsonBody.IsPatchArrayKey("start_on"));
        }

        private static void Register(string method, bool mutating = false)
        {
            var entry = ProjectCommandCatalog.Create(typeof(PlaytestWaitTests).GetMethod(method),
                new ProjectCommandAttribute("project.tests.wait")
                {
                    Title = "Wait test", Description = "Wait predicate test", Package = "tests", IsMutating = mutating,
                    Lifecycle = mutating ? LifecyclePolicy.EditorSettle : LifecyclePolicy.None,
                    PathsHint = mutating ? new[] { "Assets/Tests" } : System.Array.Empty<string>(),
                });
            Assert.IsNull(entry.Code, entry.Message);
            ProjectCommandCatalog.Publish(new[] { entry });
        }
    }
}
