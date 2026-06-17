using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M13 T4.3 — Instance lock file + stale-lock cleanup.
    //
    // Lock I/O is sandboxed to a temp dir via InstancePortResolver.InstancesDirOverride
    // so tests never touch the real ~/.unity-agent/instances. We can't easily
    // fake a different PID (Acquire writes the current Editor's PID), so the
    // stale-lock cleanup test plants a fake lock JSON with a guaranteed-dead
    // PID before calling Acquire, then verifies it disappeared.
    public static class BridgeInstanceLockTests
    {
        const string TestProjectPath = "/test/MyGame";
        const string OtherProjectPath = "/test/OtherGame";

        string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "unity-open-mcp-tests-" + Guid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            InstancePortResolver.InstancesDirOverride = _tempDir;
            // Reset internal state so a prior test's Acquire doesn't leak.
            ForceReleaseForTest();
        }

        [TearDown]
        public void TearDown()
        {
            ForceReleaseForTest();
            InstancePortResolver.InstancesDirOverride = null;
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        static Guid Guid()
        {
            // Local helper so the test name doesn't shadow System.Guid.
            return System.Guid.NewGuid();
        }

        // Forcing release without touching the real file: just clear the
        // _acquired flag so Acquire can run again. The on-disk lock (if any)
        // from a prior test was written to the previous temp dir, which is
        // already gone.
        static void ForceReleaseForTest()
        {
            try { BridgeInstanceLock.Release(); } catch { }
        }

        [Test]
        public void Acquire_WritesLockFile_WithExpectedShape()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            var lockPath = InstancePortResolver.LockPath(TestProjectPath);
            Assert.IsTrue(File.Exists(lockPath), "Lock file should exist after Acquire");
            var json = File.ReadAllText(lockPath);

            // Required fields present. Field names mirror the TS-side
            // InstanceLock type in instance-discovery.ts.
            foreach (var field in new[]
            {
                "\"pid\"", "\"port\"", "\"authToken\"", "\"projectPath\"", "\"projectHash\"",
                "\"startedAt\"", "\"updatedAt\"", "\"heartbeatAt\"",
                "\"state\"", "\"isPlaying\"", "\"isCompiling\"",
                "\"bridgeVersion\"", "\"unityVersion\""
            })
            {
                Assert.IsTrue(json.Contains(field),
                    $"Lock JSON missing field {field}. Got: {json}");
            }

            Assert.IsTrue(json.Contains("\"port\":22028"), "Port must be written into the lock");
            StringAssert.Contains("\"projectPath\":\"/test/MyGame\"", json);
            StringAssert.Contains("\"state\":\"idle\"", json, "Initial state is idle");
            StringAssert.Contains($"\"projectHash\":\"{InstancePortResolver.ProjectHash(TestProjectPath)}\"", json);
        }

        [Test]
        public void Acquire_IsIdempotent_OverwritesSameProjectLock()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var first = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            // Re-acquire with a new port (simulates the bridge restarting on
            // the same project). The lock should be replaced atomically,
            // not appended to or rejected.
            BridgeInstanceLock.Acquire(TestProjectPath, 22029);
            var second = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            Assert.AreNotEqual(first, second, "Lock should reflect the new port");
            StringAssert.Contains("\"port\":22029", second);
        }

        // M14 — the per-session bearer token is written into the lock so the
        // MCP server can discover it. Mirror the TS-side InstanceLock.authToken
        // field. Token is always minted regardless of authMode.
        [Test]
        public void Acquire_WritesAuthToken_OfExpectedShape()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            var json = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));

            // Field present and a 64-char hex value (32 bytes hex-encoded).
            var match = Regex.Match(json, "\"authToken\":\"([0-9a-f]+)\"");
            Assert.IsTrue(match.Success,
                $"Lock JSON should carry a hex authToken. Got: {json}");
            Assert.AreEqual(BridgeAuthToken.HexLength, match.Groups[1].Value.Length,
                $"authToken must be {BridgeAuthToken.HexLength} hex chars (256-bit). Got: {match.Groups[1].Value}");

            // The in-memory accessor must match the on-disk value so the HTTP
            // auth check compares against what the client discovered.
            Assert.AreEqual(match.Groups[1].Value, BridgeInstanceLock.AuthToken);
        }

        [Test]
        public void Acquire_MintsFreshToken_OnEachAcquire()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var firstToken = BridgeInstanceLock.AuthToken;
            Assert.IsNotNull(firstToken);

            BridgeInstanceLock.Acquire(TestProjectPath, 22029);
            var secondToken = BridgeInstanceLock.AuthToken;

            Assert.AreNotEqual(firstToken, secondToken,
                "A fresh Acquire must mint a fresh token so a bridge restart " +
                "invalidates any previously discovered token.");
        }

        [Test]
        public void Release_ClearsAuthToken()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            Assert.IsNotNull(BridgeInstanceLock.AuthToken);

            BridgeInstanceLock.Release();

            Assert.IsNull(BridgeInstanceLock.AuthToken,
                "Release must clear the in-memory token so a stale handle " +
                "can't be used to authorize post-shutdown traffic.");
        }

        [Test]
        public void UpdateState_RewritesLock_WithFreshHeartbeat()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            BridgeInstanceLock.UpdateState(BridgeInstanceLock.StateCompiling, isPlaying: false, isCompiling: true);

            var json = File.ReadAllText(InstancePortResolver.LockPath(TestProjectPath));
            StringAssert.Contains("\"state\":\"compiling\"", json);
            StringAssert.Contains("\"isCompiling\":true", json);
        }

        [Test]
        public void UpdateState_NoOp_BeforeAcquire()
        {
            // No Acquire yet → UpdateState must not write anything.
            Assert.DoesNotThrow(() => BridgeInstanceLock.UpdateState(BridgeInstanceLock.StateIdle, false, false));
            Assert.IsFalse(File.Exists(InstancePortResolver.LockPath(TestProjectPath)));
        }

        [Test]
        public void Release_DeletesLock()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var path = InstancePortResolver.LockPath(TestProjectPath);
            Assert.IsTrue(File.Exists(path));

            BridgeInstanceLock.Release();
            Assert.IsFalse(File.Exists(path), "Lock must be deleted on Release");
        }

        [Test]
        public void Release_Idempotent()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            Assert.DoesNotThrow(() => BridgeInstanceLock.Release());
            // Second release is a no-op (already not acquired).
            Assert.DoesNotThrow(() => BridgeInstanceLock.Release());
        }

        [Test]
        public void ReadCurrentJson_ReturnsLockContent_WhenAcquired()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            var json = BridgeInstanceLock.ReadCurrentJson();
            Assert.IsNotNull(json);
            StringAssert.Contains("\"port\":22028", json);
        }

        [Test]
        public void ReadCurrentJson_ReturnsNull_WhenNotAcquired()
        {
            Assert.IsNull(BridgeInstanceLock.ReadCurrentJson());
        }

        // ----- stale-lock cleanup -----

        [Test]
        public void Acquire_DeletesStaleLockForDeadPid()
        {
            // Plant a lock for a DIFFERENT project with a guaranteed-dead PID.
            // (999_999_999 is effectively never a real OS pid.)
            var otherPath = InstancePortResolver.LockPath(OtherProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath));
            File.WriteAllText(otherPath,
                "{\"pid\":999999999,\"port\":25000,\"projectPath\":\"" +
                OtherProjectPath + "\",\"state\":\"idle\"}");

            Assert.IsTrue(File.Exists(otherPath), "Pre-condition: stale lock should exist");

            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            Assert.IsFalse(File.Exists(otherPath),
                "Stale lock for a dead PID must be cleaned up by Acquire");
        }

        [Test]
        public void Acquire_LeavesLockForLivePid()
        {
            // Plant a lock for a different project, using OUR own pid (which
            // is guaranteed alive — it's the test runner). This simulates
            // another live Unity instance holding its own lock.
            var otherPath = InstancePortResolver.LockPath(OtherProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath));
            var livePid = System.Diagnostics.Process.GetCurrentProcess().Id;
            File.WriteAllText(otherPath,
                "{\"pid\":" + livePid + ",\"port\":25000,\"projectPath\":\"" +
                OtherProjectPath + "\",\"state\":\"idle\"}");

            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            Assert.IsTrue(File.Exists(otherPath),
                "Lock for a live PID must NOT be cleaned up");
        }

        [Test]
        public void Acquire_LeavesMalformedLocks()
        {
            // A malformed lock (no parseable pid) is left alone — we don't
            // know whose it is, so deleting it would risk clobbering another
            // tool's instance file.
            var otherPath = InstancePortResolver.LockPath(OtherProjectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(otherPath));
            File.WriteAllText(otherPath, "{\"notPid\":\"oops\"}");

            BridgeInstanceLock.Acquire(TestProjectPath, 22028);

            Assert.IsTrue(File.Exists(otherPath),
                "Malformed locks (no pid) must be left in place");
        }

        [Test]
        public void LockFile_WriteIsAtomic_TmpFileDoesNotRemain()
        {
            BridgeInstanceLock.Acquire(TestProjectPath, 22028);
            BridgeInstanceLock.UpdateState(BridgeInstanceLock.StatePlaying, true, false);

            // The .tmp.<pid> scratch file must have been renamed into place;
            // no leftover temp files for this project's lock.
            var lockPath = InstancePortResolver.LockPath(TestProjectPath);
            var dir = Path.GetDirectoryName(lockPath);
            var leftovers = Directory.GetFiles(dir, Path.GetFileName(lockPath) + "*.tmp*");
            Assert.AreEqual(0, leftovers.Length,
                $"Expected no leftover temp files, got: {string.Join(", ", leftovers)}");
        }
    }
}
