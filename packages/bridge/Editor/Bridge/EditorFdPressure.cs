using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityOpenMcpBridge.Config;

namespace UnityOpenMcpBridge
{
    // Self-probe of THIS Editor process's open file-descriptor count, and the
    // warn/critical advisory execute_csharp attaches to its responses. The
    // Editor hangs mid-build once a descriptor number crosses Mono's internal
    // ~1024 IOSelector ceiling ("Could not register to wait for file
    // descriptor N"), so an agent hammering execute_csharp should see the wall
    // coming in-band instead of discovering it when a compile wedges.
    //
    // Thresholds mirror mcp-server/src/process-diagnostics.ts
    // (FD_CEILING_DEFAULT / FD_WARN_RATIO / FD_CRITICAL_RATIO) and the ceiling
    // override mirrors mcp-server/src/project-settings.ts (readFdCeiling —
    // same `.unity-open-mcp/settings.json` key, same FD_CEILING_MIN /
    // FD_CEILING_MAX clamp). Both sides MUST resolve the same ceiling for the
    // same project: this advisory fires on every execute_csharp response, so a
    // one-sided 1024 would contradict `resource_pressure` on every call for
    // exactly the Unity 6 / CoreCLR projects the override exists for. The
    // parity test in mcp-server/src/constants.parity.test.ts pins the
    // constants; the reader below is pinned by EditorFdPressureTests.
    //
    // The server's resource_pressure tool remains the authoritative operator
    // surface (trend detection, sample ring); this advisory is a coarse
    // in-band tripwire.
    internal static class EditorFdPressure
    {
        internal const int MonoFdCeiling = 1024;
        internal const double WarnRatio = 0.8;
        internal const double CriticalRatio = 0.9;

        /// <summary>Below this a configured ceiling is rejected → default.
        /// Mirrors FD_CEILING_MIN in mcp-server/src/project-settings.ts.</summary>
        internal const int FdCeilingMin = 64;

        /// <summary>Above this a configured ceiling is clamped down. Mirrors
        /// FD_CEILING_MAX in mcp-server/src/project-settings.ts.</summary>
        internal const int FdCeilingMax = 1000000;

        // `"resourcePressure": { … }`. The member's span is closed with a
        // string-aware brace walk (shared with BridgeProjectSettings) so a
        // nested object before fdCeiling — e.g. "trend" — cannot hide the
        // value, and a fdCeiling nested INSIDE a sibling object is not
        // mistaken for the real one: JSON.parse semantics, as on the TS side
        // (readFdCeiling). A regex reader rather than JsonUtility because
        // BridgeProjectSettingsData deliberately does not model this slice
        // (JsonUtility would have to round-trip it on every settings write,
        // and a null nested object serializes as a zeroed one — it would
        // start WRITING a bogus `fdCeiling: 0` into every project). The slice
        // is owned by the MCP server; the bridge only reads it —
        // BridgeProjectSettings.Save carries the raw member across writes.
        private static readonly Regex ResourcePressureOpener = new Regex(
            "\"resourcePressure\"\\s*:\\s*\\{");

        // A fdCeiling VALUE: a full JSON number literal (integer, fraction,
        // exponent) followed by a value delimiter, so `4096abc` — a file
        // JSON.parse would reject whole — does not read as 4096 here.
        private static readonly Regex FdCeilingPattern = new Regex(
            "\"fdCeiling\"\\s*:\\s*"
            + "(-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)"
            + "(?=[\\s,}])");

        // Resolved ceiling + the settings-file stamp it was read from. The
        // advisory runs on EVERY execute_csharp response, so the file is
        // re-read only when its mtime/size changes — an operator can still
        // edit the setting without restarting the Editor.
        private static readonly object CeilingLock = new object();
        private static int _cachedCeiling = MonoFdCeiling;
        private static long _cachedStamp = long.MinValue;
        private static bool _ceilingLoaded;

        // Count the open file descriptors of the current process. macOS:
        // /dev/fd; Linux: /proc/self/fd (on Linux /dev/fd symlinks there) —
        // one directory entry per open descriptor, minus one for the
        // enumeration's own transient handle. Uses no Unity APIs, so it is
        // safe on the dispatcher's worker thread. Windows: there is no cheap
        // fd equivalent — Process.HandleCount covers kernel/GDI/user handles,
        // which routinely exceed 1024 on a perfectly healthy process, so an
        // advisory keyed on it would cry wolf; this returns false there and
        // the advisory stays silent (resource_pressure covers Windows with an
        // explicit `approximate` flag).
        internal static bool TryCountOpenFds(out int count)
        {
            count = -1;
            try
            {
                string fdDir = null;
                if (Directory.Exists("/dev/fd")) fdDir = "/dev/fd";
                else if (Directory.Exists("/proc/self/fd")) fdDir = "/proc/self/fd";
                if (fdDir == null) return false;

                var entries = Directory.GetFileSystemEntries(fdDir);
                count = Math.Max(0, entries.Length - 1);
                return true;
            }
            catch
            {
                count = -1;
                return false;
            }
        }

        /// <summary>
        /// The fd ceiling for THIS project: `resourcePressure.fdCeiling` from
        /// <c>.unity-open-mcp/settings.json</c> when it is present and in
        /// range, else <see cref="MonoFdCeiling"/>. Never throws — an absent,
        /// unreadable or malformed file resolves to the default, exactly like
        /// the server's readFdCeiling. Safe on a worker thread: the project
        /// path comes from BridgeSession's cached string, not Application.
        /// </summary>
        internal static int ResolveCeiling()
        {
            var path = SettingsPath();
            if (path == null) return MonoFdCeiling;

            long stamp;
            try
            {
                var info = new FileInfo(path);
                stamp = info.Exists
                    ? info.LastWriteTimeUtc.Ticks ^ (info.Length << 1)
                    : long.MinValue + 1;
            }
            catch
            {
                return MonoFdCeiling;
            }

            lock (CeilingLock)
            {
                if (_ceilingLoaded && stamp == _cachedStamp) return _cachedCeiling;
                _cachedCeiling = ReadCeilingFromFile(path);
                _cachedStamp = stamp;
                _ceilingLoaded = true;
                return _cachedCeiling;
            }
        }

        /// <summary>Clamp a configured ceiling the way the server's
        /// resolveConfigured does: non-finite (an exponent overflowing the
        /// double range — JSON.parse yields Infinity and isFinite rejects it)
        /// or below the min is treated as "not configured" (→ default), above
        /// the max clamps down, in-range floors. Split out so a test can pin
        /// the policy without a file.</summary>
        internal static int ClampCeiling(double raw)
        {
            if (double.IsNaN(raw) || double.IsInfinity(raw)) return MonoFdCeiling;
            if (raw < FdCeilingMin) return MonoFdCeiling;
            if (raw > FdCeilingMax) return FdCeilingMax;
            return (int)Math.Floor(raw);
        }

        // The advisory line for agentNextSteps, or null when pressure is below
        // the warn threshold or unknown. Silence below warn is deliberate —
        // the specs/feedback.md R2R-log lesson: a per-call line restating an
        // already-fine fact costs tokens on every response for the rest of the
        // session.
        internal static string BuildAdvisory()
        {
            if (!TryCountOpenFds(out var count)) return null;
            return BuildAdvisory(count, ResolveCeiling());
        }

        // Pure formatting half, split out for tests.
        internal static string BuildAdvisory(int count, int ceiling)
        {
            if (ceiling <= 0 || count < 0) return null;
            var ratio = (double)count / ceiling;
            if (ratio < WarnRatio) return null;

            var state = ratio >= CriticalRatio ? "CRITICAL" : "elevated";
            return
                "Editor file-descriptor pressure is " + state + ": " + count +
                " open fds against Mono's ~" + ceiling + " IOSelector ceiling (" +
                Math.Round(ratio * 100) + "%). Crossing it hangs script " +
                "compilation (\"Could not register to wait for file descriptor N\") " +
                "until the Editor is restarted. Finish the current step, then " +
                "trigger a domain reload (unity_open_mcp_recompile_scripts, or any " +
                "script recompile) to release leaked descriptors; confirm with " +
                "unity_open_mcp_resource_pressure, and if pressure stays high, save " +
                "work and restart the Editor.";
        }

        private static string SettingsPath()
        {
            var root = BridgeSession.ProjectPath;
            if (string.IsNullOrEmpty(root)) return null;
            try
            {
                return Path.Combine(root, BridgeConstants.SettingsDirName, "settings.json");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Read + clamp the ceiling out of one settings file. Public
        /// to the test assembly so the resolution policy can be pinned against
        /// a fixture without touching the real project's settings or the
        /// process-wide cache.</summary>
        internal static int ReadCeilingFromFile(string path)
        {
            string body;
            try
            {
                if (!File.Exists(path)) return MonoFdCeiling;
                body = File.ReadAllText(path);
            }
            catch
            {
                return MonoFdCeiling;
            }

            var member = FindResourcePressureMember(body);
            if (member == null) return MonoFdCeiling;
            var literal = FindTopLevelFdCeiling(member);
            if (literal == null) return MonoFdCeiling;

            double raw;
            if (!double.TryParse(
                    literal, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out raw))
            {
                return MonoFdCeiling;
            }
            return ClampCeiling(raw);
        }

        // The LAST "resourcePressure" member's text, from its opening brace
        // to the matching close (JSON.parse keeps the last duplicate key), or
        // null when no balanced member exists.
        private static string FindResourcePressureMember(string body)
        {
            var openers = ResourcePressureOpener.Matches(body);
            if (openers.Count == 0) return null;
            var opener = openers[openers.Count - 1];
            var openBrace = opener.Index + opener.Length - 1;
            var closeBrace = BridgeProjectSettings.FindMatchingBrace(body, openBrace);
            if (closeBrace < 0) return null;
            return body.Substring(openBrace, closeBrace - openBrace + 1);
        }

        // The fdCeiling number literal DIRECTLY inside the member (not nested
        // in a sibling object such as "trend"), last duplicate wins — the
        // value JSON.parse would surface as resourcePressure.fdCeiling. Null
        // when absent.
        private static string FindTopLevelFdCeiling(string member)
        {
            var matches = FdCeilingPattern.Matches(member);
            if (matches.Count == 0) return null;

            var literal = null as string;
            var depth = 0;
            var inString = false;
            var cursor = 1; // skip the member's opening brace
            foreach (Match m in matches)
            {
                while (cursor < m.Index)
                {
                    var c = member[cursor];
                    if (inString)
                    {
                        if (c == '\\') cursor++; // skip the escaped character
                        else if (c == '"') inString = false;
                    }
                    else if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}') depth--;
                    cursor++;
                }
                if (depth == 0) literal = m.Groups[1].Value;
            }
            return literal;
        }
    }
}
