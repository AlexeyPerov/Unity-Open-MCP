using System;

namespace UnityOpenMcpBridge
{
    // User-facing recovery copy for bridge Start failures (Status tab).
    internal static class BridgeStartRecovery
    {
        public static bool IsPortInUseError(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;
            return message.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("address is already in use", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string FormatPortInUseRecovery(string projectPath, int port)
        {
            var lockPath = string.IsNullOrEmpty(projectPath)
                ? "~/.unity-open-mcp/instances/<project-hash>.json"
                : InstancePortResolver.LockPath(projectPath);

            return
                "The bridge port is already in use — usually a zombie HTTP listener in this Editor " +
                "after a recompile or an unclean quit.\n\n" +
                "The bridge automatically force-stops any in-process listener and retries the bind " +
                "a few times. If Start still fails, recover manually:\n" +
                "1. Quit Unity completely (File → Exit / Cmd+Q).\n" +
                "2. Confirm the port is free — macOS/Linux: lsof -i :" + port +
                " — Windows: netstat -ano | findstr :" + port + "\n" +
                "3. If a process still holds the port after Unity quit, terminate it.\n" +
                "4. Optional: delete the stale instance lock (only when Unity is quit and the port is free):\n" +
                "   " + lockPath + "\n" +
                "5. Reopen the project — the bridge auto-starts unless disabled in Settings.\n" +
                "6. Do not click Start if the listener already shows Running. Use Ping to verify.\n\n" +
                "Full guide: docs/troubleshooting.md (also linked from the toolbar ? button).";
        }
    }
}
