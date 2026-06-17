using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace UnityOpenMcpBridge
{
    // M13 T4.3 / T4.7 — Instance lock + heartbeat file.
    //
    // Each running bridge instance owns a lock file at
    //   ~/.unity-agent/instances/<projectHash>.json
    // The file doubles as the heartbeat (T4.7): it carries the current editor
    // state (idle / compiling / reloading / playing / ...) and is rewritten
    // every 0.5s by BridgeHeartbeat plus on every forced state transition.
    // The MCP server reads it to discover the right port per project without
    // an HTTP round-trip and without sharing config with the bridge.
    //
    // Lifecycle:
    //   - Acquire()  : called once when the listener starts. Sweeps stale
    //                  locks (PID no longer alive) across ALL instances, then
    //                  writes this instance's lock.
    //   - UpdateState: called by BridgeHeartbeat every 0.5s and on transitions.
    //                  Atomic write (.tmp + rename, same pattern as
    //                  BridgeProjectSettings).
    //   - Release()  : deletes the lock on graceful shutdown. Best-effort —
    //                  a crashed Unity leaves a stale lock that the next
    //                  Acquire() cleans up.
    //
    // Thread-safety: Acquire/UpdateState/Release may be called from the main
    // thread (heartbeat tick) or the listener worker (Start/Stop). File I/O is
    // atomic via rename; concurrent UpdateState calls are last-writer-wins and
    // the heartbeat is the only steady-state writer, so this is safe in
    // practice. The .tmp file path is per-PID to avoid collisions.
    public static class BridgeInstanceLock
    {
        const string TempSuffix = ".tmp";

        // State values written into the lock. Mirror the TS-side parser in
        // mcp-server/src/instance-discovery.ts (InstanceState type).
        public const string StateIdle = "idle";
        public const string StateCompiling = "compiling";
        public const string StateReloading = "reloading";
        public const string StateEnteringPlaymode = "entering_playmode";
        public const string StatePlaying = "playing";
        public const string StateExitingPlaymode = "exiting_playmode";

        // Last-written snapshot, kept in memory so UpdateState can rewrite
        // only the fields that changed without re-reading the file. Volatile
        // read/written from main + worker threads.
        static volatile bool _acquired;
        static string _acquiredProjectPath;
        static int _acquiredPort;
        static string _acquiredProjectHash;
        static int _pid;
        static DateTime _startedAt;

        public static bool IsAcquired => _acquired;
        public static string CurrentProjectPath => _acquiredProjectPath;
        public static int CurrentPort => _acquiredPort;

        // Write the initial lock and sweep stale locks. Safe to call on the
        // listener worker thread (BridgeHttpServer.Start) — no Unity APIs.
        public static void Acquire(string projectPath, int port)
        {
            if (string.IsNullOrEmpty(projectPath))
            {
                UnityEngine.Debug.LogWarning(
                    "[BridgeInstanceLock] No project path available; skipping lock acquire.");
                return;
            }

            try
            {
                EnsureInstancesDir();
                SweepStaleLocks();
            }
            catch (Exception e)
            {
                // Sweep failure must not block the bridge start.
                UnityEngine.Debug.LogWarning(
                    $"[BridgeInstanceLock] Stale-lock sweep failed: {e.Message}");
            }

            _acquiredProjectPath = projectPath;
            _acquiredPort = port;
            _acquiredProjectHash = InstancePortResolver.ProjectHash(projectPath);
            _pid = Process.GetCurrentProcess().Id;
            _startedAt = DateTime.UtcNow;

            try
            {
                WriteLock(StateIdle, isPlaying: false, isCompiling: false, DateTime.UtcNow);
                _acquired = true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    $"[BridgeInstanceLock] Failed to write lock file: {e.Message}");
                _acquired = false;
            }
        }

        // Rewrite the lock with fresh state. Called by the heartbeat tick and
        // on forced transitions. No-op if Acquire has not run or failed.
        public static void UpdateState(string state, bool isPlaying, bool isCompiling)
        {
            if (!_acquired) return;
            try
            {
                WriteLock(state ?? StateIdle, isPlaying, isCompiling, DateTime.UtcNow);
            }
            catch (Exception e)
            {
                // Heartbeat write is best-effort; don't tear down the editor.
                UnityEngine.Debug.LogWarning(
                    $"[BridgeInstanceLock] Heartbeat write failed: {e.Message}");
            }
        }

        // Delete the lock on graceful shutdown. Best-effort.
        public static void Release()
        {
            if (!_acquired) return;
            var path = InstancePortResolver.LockPath(_acquiredProjectPath);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning(
                    $"[BridgeInstanceLock] Failed to delete lock file: {e.Message}");
            }
            finally
            {
                _acquired = false;
            }
        }

        // Snapshot of the current lock file content as a JSON string. Used by
        // the /instance HTTP endpoint so the MCP server can verify the live
        // bridge against the on-disk lock without trusting the file alone.
        // Returns null when no lock is held or the file can't be read.
        public static string ReadCurrentJson()
        {
            if (!_acquired) return null;
            var path = InstancePortResolver.LockPath(_acquiredProjectPath);
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        // ----- internals -----

        static void WriteLock(string state, bool isPlaying, bool isCompiling, DateTime now)
        {
            var path = InstancePortResolver.LockPath(_acquiredProjectPath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = BuildJson(state, isPlaying, isCompiling, now);
            var tmp = path + TempSuffix + "." + _pid;
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }

        static string BuildJson(string state, bool isPlaying, bool isCompiling, DateTime now)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"pid\":").Append(_pid).Append(',');
            sb.Append("\"port\":").Append(_acquiredPort).Append(',');
            sb.Append("\"projectPath\":").Append(Escape(NullToEmpty(_acquiredProjectPath))).Append(',');
            sb.Append("\"projectHash\":").Append(Escape(NullToEmpty(_acquiredProjectHash))).Append(',');
            sb.Append("\"startedAt\":").Append(Escape(IsoUtc(_startedAt))).Append(',');
            sb.Append("\"updatedAt\":").Append(Escape(IsoUtc(now))).Append(',');
            sb.Append("\"heartbeatAt\":").Append(Escape(IsoUtc(now))).Append(',');
            sb.Append("\"state\":").Append(Escape(state ?? StateIdle)).Append(',');
            sb.Append("\"isPlaying\":").Append(isPlaying ? "true" : "false").Append(',');
            sb.Append("\"isCompiling\":").Append(isCompiling ? "true" : "false").Append(',');
            sb.Append("\"bridgeVersion\":").Append(Escape(BridgeSession.BridgeVersion)).Append(',');
            sb.Append("\"unityVersion\":").Append(Escape(NullToEmpty(BridgeSession.UnityVersion)));
            sb.Append('}');
            return sb.ToString();
        }

        // JSON-encode a string value with surrounding quotes. null → "null".
        static string Escape(string raw)
        {
            if (raw == null) return "null";
            var sb = new StringBuilder(raw.Length + 8);
            sb.Append('"');
            AppendEscaped(sb, raw);
            sb.Append('"');
            return sb.ToString();
        }

        // BridgeSession.UnityVersion etc. are strings but may be null before
        // init; coerce to empty string so Escape produces "" not null.
        static string NullToEmpty(string s) => s ?? "";

        static void AppendEscaped(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
        }

        static string IsoUtc(DateTime dt)
        {
            // Round-trip ISO-8601 in UTC. Universal sortable pattern + Z is
            // the simplest form every JSON parser accepts.
            return dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        static void EnsureInstancesDir()
        {
            var dir = InstancePortResolver.InstancesDir;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        // Scan ~/.unity-agent/instances/*.json and delete any whose pid is no
        // longer alive. Treats parse errors and access errors as "leave it
        // alone" — a malformed lock for someone else's instance is not ours
        // to touch. We DO touch our own project's lock below in WriteLock
        // (overwrite), so a stale own-project lock gets replaced regardless.
        static void SweepStaleLocks()
        {
            var dir = InstancePortResolver.InstancesDir;
            if (!Directory.Exists(dir)) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch { return; }

            foreach (var file in files)
            {
                int pid;
                try
                {
                    var json = File.ReadAllText(file);
                    pid = ExtractPid(json);
                }
                catch
                {
                    continue;
                }

                if (pid <= 0) continue;

                if (IsPidAlive(pid)) continue;

                try { File.Delete(file); }
                catch
                {
                    // best-effort
                }
            }
        }

        // Minimal pid extractor: pull the integer value of "pid":N out of the
        // lock JSON without a JSON parser (bridge has no Newtonsoft).
        static int ExtractPid(string json)
        {
            const string key = "\"pid\"";
            var idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return -1;
            var colon = json.IndexOf(':', idx + key.Length);
            if (colon < 0) return -1;
            var start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            var end = start;
            while (end < json.Length && json[end] >= '0' && json[end] <= '9') end++;
            if (end == start) return -1;
            return int.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : -1;
        }

        // kill -0 equivalent. Process.GetProcessById throws on a dead pid on
        // all platforms; the returned Process is then immediately discarded.
        // The exception path is the common case for stale locks.
        static bool IsPidAlive(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                try { p.Dispose(); } catch { }
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            // An access-denied / permission error means the pid exists but we
            // can't introspect it — treat as alive so we never delete a lock
            // for a running instance we just can't see.
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }
}
