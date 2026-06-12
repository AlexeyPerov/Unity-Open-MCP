using System;

namespace UnityAgentBridge
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
        public bool Enabled { get; set; } = true;

        public BridgeToolAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
}
