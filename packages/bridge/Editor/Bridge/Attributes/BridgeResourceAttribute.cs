using System;

namespace UnityOpenMcpBridge
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class BridgeResourceAttribute : Attribute
    {
        public string Name { get; }
        public string Route { get; }
        public string MimeType { get; set; } = "application/json";
        public string Description { get; set; }
        public bool Enabled { get; set; } = true;

        public BridgeResourceAttribute(string name, string route)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Route = route ?? throw new ArgumentNullException(nameof(route));
        }
    }
}
