using NUnit.Framework;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    // The reload / compile handshake the MCP server's editor_status wait reads:
    // the compile-request record (lock `compile` block, /compile-state
    // `pending`, the gate envelope's `compile` block), /ping `transition`, the
    // well-formed editor_reloading answers, and the wait call every hint names.
    public class BridgeReloadHandshakeTests
    {
        private int _savedRequest;
        private string _savedRequestedAt;

        [SetUp]
        public void SaveRequest()
        {
            _savedRequest = BridgeCompileState.RequestedAtGenerationForTest;
            _savedRequestedAt = BridgeCompileState.RequestedAtUtcForTest;
        }

        [TearDown]
        public void RestoreRequest()
        {
            BridgeCompileState.RestoreRequestForTest(_savedRequest, _savedRequestedAt);
            BridgeSession.RefreshVolatileState();
        }

        [Test]
        public void NoteRequested_PublishesAPendingCompile_InTheLockAndCompileStateAndEnvelope()
        {
            BridgeCompileState.NoteRequested();
            Assert.IsTrue(BridgeCompileState.Pending);
            var generation = BridgeCompileState.Generation;

            var lockJson = BridgeCompileState.LockJson();
            Assert.IsTrue(BridgeJson.IsCompleteJson(lockJson), lockJson);
            StringAssert.Contains("\"generation\":" + generation, lockJson);
            StringAssert.Contains("\"pending\":true", lockJson);
            StringAssert.Contains("\"requestedAt\":\"", lockJson);

            var state = BridgeCompileState.BuildJson();
            Assert.IsTrue(BridgeJson.IsCompleteJson(state), state);
            StringAssert.Contains("\"pending\":true", state);

            var envelope = BridgeCompileState.EnvelopeJson(generation);
            Assert.IsTrue(BridgeJson.IsCompleteJson(envelope), envelope);
            StringAssert.StartsWith("{\"generationAtStart\":" + generation + ",\"generation\":" + generation, envelope);
            StringAssert.Contains("\"pending\":true", envelope);

            BridgeCompileState.RestoreRequestForTest(-1, "");
            Assert.IsFalse(BridgeCompileState.Pending);
            StringAssert.Contains("\"pending\":false", BridgeCompileState.LockJson());
            StringAssert.Contains("\"requestedAt\":null", BridgeCompileState.LockJson());
        }

        [Test]
        public void NoteDispatchEnd_WithNothingDue_RecordsNothing()
        {
            // The test runs with no compile requested or running.
            BridgeCompileState.RestoreRequestForTest(-1, "");
            BridgeCompileState.NoteDispatchEnd();
            Assert.IsFalse(BridgeCompileState.Pending);
        }

        [Test]
        public void Transition_FollowsCompileAndPlayModeFlags()
        {
            try
            {
                BridgeSession.SetCompilingForTest(false);
                BridgeSession.SetPlayingForTest(false);
                BridgeSession.SetWillChangePlaymodeForTest(false);
                Assert.IsNull(BridgeSession.Transition);
                StringAssert.Contains("\"transition\":null", BridgeJson.BuildPingJson());

                BridgeSession.SetWillChangePlaymodeForTest(true);
                Assert.AreEqual("entering_playmode", BridgeSession.Transition);
                StringAssert.Contains("\"transition\":\"entering_playmode\"", BridgeJson.BuildPingJson());

                BridgeSession.SetPlayingForTest(true);
                Assert.IsNull(BridgeSession.Transition, "playing (or paused) is settled");

                BridgeSession.SetWillChangePlaymodeForTest(false);
                Assert.AreEqual("exiting_playmode", BridgeSession.Transition);

                BridgeSession.SetCompilingForTest(true);
                Assert.AreEqual("compiling", BridgeSession.Transition);
            }
            finally
            {
                BridgeSession.RefreshVolatileState();
            }
        }

        [Test]
        public void LockState_ADomainReloadedInPlayMode_IsPlaying_NeverIdle()
        {
            // Reloaded in play mode: isPlaying and isPlayingOrWillChangePlaymode
            // are both true at the new domain's first lock write.
            Assert.AreEqual(BridgeInstanceLock.StatePlaying, BridgeHeartbeat.DeriveState(false, false, true, true));
            // Reloaded while entering play mode: not playing yet, on the way.
            Assert.AreEqual(BridgeInstanceLock.StateEnteringPlaymode, BridgeHeartbeat.DeriveState(false, false, false, true));
            Assert.AreEqual(BridgeInstanceLock.StateExitingPlaymode, BridgeHeartbeat.DeriveState(false, false, true, false));
            Assert.AreEqual(BridgeInstanceLock.StateCompiling, BridgeHeartbeat.DeriveState(false, true, true, true));
            Assert.AreEqual(BridgeInstanceLock.StateReloading, BridgeHeartbeat.DeriveState(true, false, true, true));
            Assert.AreEqual(BridgeInstanceLock.StateIdle, BridgeHeartbeat.DeriveState(false, false, false, false));
        }

        [Test]
        public void EditorReloadingJson_IsWellFormed_AndSaysWhetherTheCallRan()
        {
            var notRun = BridgeJson.BuildEditorReloadingJson("unity_open_mcp_gameobject_create", false, "entering_playmode");
            Assert.IsTrue(BridgeJson.IsCompleteJson(notRun), notRun);
            StringAssert.Contains("\"code\":\"editor_reloading\"", notRun);
            StringAssert.Contains("\"dispatched\":false", notRun);
            StringAssert.Contains("\"transition\":\"entering_playmode\"", notRun);
            StringAssert.Contains("\"retryAfterMs\":", notRun);
            StringAssert.Contains("retry the call", notRun);

            var ran = BridgeJson.BuildEditorReloadingJson("unity_open_mcp_recompile_scripts", true, "reloading", 41);
            Assert.IsTrue(BridgeJson.IsCompleteJson(ran), ran);
            StringAssert.Contains("\"dispatched\":true", ran);
            StringAssert.Contains("\"compile\":{\"generationAtStart\":41}", ran);
            StringAssert.Contains("\\\"after_generation\\\":41", ran);
            StringAssert.Contains("verify", ran);
        }

        [Test]
        public void WaitCall_NamesTheEditorStatusWait()
        {
            Assert.AreEqual("unity_open_mcp_editor_status {\"wait_until\":\"idle\"}", BridgeJson.WaitCall("idle", null));
            Assert.AreEqual("unity_open_mcp_editor_status {\"wait_until\":\"idle\",\"after_generation\":7}", BridgeJson.WaitCall("idle", 7));
            StringAssert.Contains("{\"wait_until\":\"idle\",\"after_generation\":7}", BridgeJson.CompilePendingStepFor(7));
            StringAssert.Contains("PRE-compile state", BridgeJson.CompilePendingStep, "the envelope writer's de-dup key");
        }

        [Test]
        public void GateEnvelope_CarriesTheCompileBlock_OnlyWhenSet()
        {
            var result = new GateDispatchResult { Mutation = ToolDispatchResult.Ok("{\"refreshed\":true}"), Outcome = GateOutcome.Skipped };
            var without = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.EditorSettle);
            StringAssert.DoesNotContain("\"compile\":", without);

            result.CompileJson = "{\"generationAtStart\":3,\"generation\":3,\"pending\":true,\"running\":false}";
            var with = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.EditorSettle);
            Assert.IsTrue(BridgeJson.IsCompleteJson(with), with);
            StringAssert.Contains("\"compile\":{\"generationAtStart\":3,", with);
        }

        [Test]
        public void EditorSetState_PlayAndStop_NameTheWaitForTheirMode_PauseNamesNone()
        {
            var play = EditorConsoleSelectionTools.BuildStateEnvelope("play", null);
            Assert.IsTrue(BridgeJson.IsCompleteJson(play), play);
            StringAssert.Contains("\"transition\":\"entering_playmode\"", play);
            StringAssert.Contains("{\\\"wait_until\\\":\\\"playing\\\"}", play);
            StringAssert.DoesNotContain("Poll", play);

            var stop = EditorConsoleSelectionTools.BuildStateEnvelope("stop", null);
            StringAssert.Contains("{\\\"wait_until\\\":\\\"edit\\\"}", stop);

            var pause = EditorConsoleSelectionTools.BuildStateEnvelope("pause", null);
            StringAssert.Contains("\"agentNextSteps\":[]", pause);
        }

        [Test]
        public void ReloadRequest_IsReadableOnThisUnity_AndNotRequestedInASettledEditor()
        {
            var requested = BridgeReloadRequest.IsRequested();
            Assert.IsTrue(requested.HasValue, "InternalEditorUtility.IsScriptReloadRequested must resolve on this Unity version");
            Assert.IsFalse(requested.Value);
            Assert.IsFalse(BridgeReloadRequest.Due);
        }

        [Test]
        public void ReloadRequest_HeldUntilTheResponse_IsPublishedAsDue_ThenReleased()
        {
            try
            {
                BridgeSession.SetCompilingForTest(false);
                BridgeSession.SetPlayingForTest(false);
                BridgeSession.SetWillChangePlaymodeForTest(false);

                // Edit mode: held until the response is written.
                Assert.AreEqual(BridgeReloadRequest.TimingAfterResponse, BridgeReloadRequest.Hold());
                Assert.AreEqual(1, BridgeReloadRequest.HoldsForTest);
                Assert.IsTrue(BridgeReloadRequest.Due);
                // The next call waits for it: /ping, and mutations are refused.
                Assert.AreEqual("reloading", BridgeSession.Transition);
                StringAssert.Contains("\"transition\":\"reloading\"", BridgeJson.BuildPingJson());
                Assert.AreEqual("reloading", BridgeHttpServer.DispatchBlockingTransition());

                var step = BridgeReloadRequest.NextStep(BridgeReloadRequest.TimingAfterResponse);
                StringAssert.Contains("unity_open_mcp_editor_status {\"wait_until\":\"idle\"}", step);
                StringAssert.Contains("right after this response", step);
                StringAssert.Contains("Recompile After Finished Playing",
                    BridgeReloadRequest.NextStep(BridgeReloadRequest.TimingAfterPlayMode));

                // No release yet: the tick keeps the hold.
                BridgeReloadRequest.TickForTest();
                Assert.AreEqual(1, BridgeReloadRequest.HoldsForTest);

                // The response is written: the next tick unlocks the reload.
                BridgeReloadRequest.Release();
                BridgeReloadRequest.TickForTest();
                Assert.AreEqual(0, BridgeReloadRequest.HoldsForTest);
                Assert.IsTrue(BridgeReloadRequest.Due, "due until the reload starts (or its grace runs out)");
            }
            finally
            {
                BridgeReloadRequest.ResetForTest();
                BridgeSession.RefreshVolatileState();
            }
            Assert.IsFalse(BridgeReloadRequest.Due);
            Assert.IsNull(BridgeHttpServer.DispatchBlockingTransition());
        }

        [Test]
        public void GateEnvelope_CarriesReloadRequested_OnlyWhenTheCallRequestedOne()
        {
            var result = new GateDispatchResult { Mutation = ToolDispatchResult.Ok("1"), Outcome = GateOutcome.Skipped };
            var without = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.RestartThenSettle);
            StringAssert.DoesNotContain("reloadRequested", without);

            result.ReloadTiming = BridgeReloadRequest.TimingAfterResponse;
            var with = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.RestartThenSettle);
            Assert.IsTrue(BridgeJson.IsCompleteJson(with), with);
            StringAssert.Contains("\"output\":1", with);
            StringAssert.Contains("\"reloadRequested\":true,\"reloadTiming\":\"after_response\"", with);
        }

        [Test]
        public void DispatchGuard_IsQuietWhenTheEditorIsSettled()
        {
            // EditMode tests run in a settled Editor: nothing compiling, no
            // play-mode change requested.
            Assert.IsNull(BridgeHttpServer.DispatchBlockingTransition());
        }
    }
}
