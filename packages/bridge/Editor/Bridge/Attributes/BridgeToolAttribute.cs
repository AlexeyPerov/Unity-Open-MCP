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

        public BridgeToolAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
}
