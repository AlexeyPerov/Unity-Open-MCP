using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    public static class BridgeStartRecoveryTests
    {
        [Test]
        public static void IsPortInUseError_MatchesCommonMessages()
        {
            Assert.IsTrue(BridgeStartRecovery.IsPortInUseError("Failed to start listener: Address already in use"));
            Assert.IsTrue(BridgeStartRecovery.IsPortInUseError("address is already in use"));
            Assert.IsTrue(BridgeStartRecovery.IsPortInUseError("ADDRESS ALREADY IN USE"));
        }

        [Test]
        public static void IsPortInUseError_RejectsOtherFailures()
        {
            Assert.IsFalse(BridgeStartRecovery.IsPortInUseError(null));
            Assert.IsFalse(BridgeStartRecovery.IsPortInUseError(""));
            Assert.IsFalse(BridgeStartRecovery.IsPortInUseError("Access denied"));
        }

        [Test]
        public static void IsPortInUseError_MatchesWindowsSocketMessage()
        {
            Assert.IsTrue(BridgeStartRecovery.IsPortInUseError(
                "Only one usage of each socket address (protocol/network address/port) is normally permitted"));
        }

        [Test]
        public static void ForceStopListener_IsIdempotentWhenNothingRunning()
        {
            Assert.DoesNotThrow(() =>
                BridgeHttpServer.ForceStopListener(releaseLock: false, logStopped: false));
        }

        // The interactive EditMode test runner is not batch mode, so the worker
        // guard is false here. Documents the contract that the listener only
        // starts in the interactive Editor (AssetImportWorker children and
        // BridgeBatchEntry headless runs are skipped — they used to bind the
        // project port and block the main Editor).
        [Test]
        public static void IsWorkerOrBatchProcess_FalseInInteractiveEditor()
        {
            Assert.IsFalse(BridgeHttpServer.IsWorkerOrBatchProcess);
        }

        [Test]
        public static void ReadNameArg_ReturnsNameTokenValue()
        {
            Assert.AreEqual(
                "AssetImportWorkerHW1",
                BridgeHttpServer.ReadNameArg(new[]
                {
                    "Unity", "-adb2", "-batchMode", "-name", "AssetImportWorkerHW1", "-parentPid", "1199"
                }));
        }

        [Test]
        public static void ReadNameArg_FallsBackWhenAbsent()
        {
            Assert.AreEqual("batch", BridgeHttpServer.ReadNameArg(new[] { "Unity", "-batchMode" }));
            Assert.AreEqual("batch", BridgeHttpServer.ReadNameArg(null));
        }
    }
}
