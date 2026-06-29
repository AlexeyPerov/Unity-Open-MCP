using System;

namespace UnityOpenMcpBridge
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class BridgeToolAttribute : Attribute
    {
        public string Name { get; }
        public string Title { get; set; }
        public bool IsMutating { get; set; }
        public GateMode Gate { get; set; } = GateMode.Enforce;
        public bool ReadOnlyHint { get; set; }
        public bool IdempotentHint { get; set; }
        public bool DestructiveHint { get; set; }
        // M13 T4.1 — lifecycle policy. Defaults to None (read-only, no settle).
        // Mutating tools should declare EditorSettle / RestartThenSettle /
        // CustomConfirmation so the dispatcher knows how long to wait before
        // returning and whether a domain reload is expected. See ToolLifecycle.
        public LifecyclePolicy Lifecycle { get; set; } = LifecyclePolicy.None;
        public bool Enabled { get; set; } = true;

        // M18 Plan 2 / T18.2 — tool group assignment. Drives per-session
        // tool-group visibility: a connected MCP session starts with only the
        // `core` group enabled and activates other groups on demand via the
        // `unity_open_mcp_manage_tools` meta-tool. Null means "always visible"
        // (server meta-tools such as manage_tools itself, capabilities, ping).
        // Group ids are stable lowercase identifiers owned by the canonical
        // catalog in mcp-server/src/capabilities/tool-groups.ts; this string
        // MUST match one of those ids exactly so the bridge-side group→tools
        // mapping reconciles with the MCP server.
        public string Group { get; set; }

        public BridgeToolAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
}
