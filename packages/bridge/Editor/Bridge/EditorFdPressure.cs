using System;
using System.IO;

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
    // (FD_CEILING_DEFAULT / FD_WARN_RATIO / FD_CRITICAL_RATIO) — keep the two
    // sides in sync by hand. The server's resource_pressure tool remains the
    // authoritative operator surface (configurable ceiling, trend detection);
    // this advisory is a coarse in-band tripwire.
    internal static class EditorFdPressure
    {
        internal const int MonoFdCeiling = 1024;
        internal const double WarnRatio = 0.8;
        internal const double CriticalRatio = 0.9;

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

        // The advisory line for agentNextSteps, or null when pressure is below
        // the warn threshold or unknown. Silence below warn is deliberate —
        // the specs/feedback.md R2R-log lesson: a per-call line restating an
        // already-fine fact costs tokens on every response for the rest of the
        // session.
        internal static string BuildAdvisory()
        {
            if (!TryCountOpenFds(out var count)) return null;
            return BuildAdvisory(count, MonoFdCeiling);
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
    }
}
