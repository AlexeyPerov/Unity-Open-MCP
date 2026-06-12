using System;
using System.Reflection;

namespace UnityAgentBridge
{
    public class BridgeResourceEntry
    {
        public string Name { get; }
        public string Route { get; }
        public string MimeType { get; }
        public string Description { get; }
        public bool Enabled { get; }
        public MethodInfo Method { get; }
        public Type DeclaringType { get; }

        object _instance;

        public BridgeResourceEntry(
            string name,
            string route,
            string mimeType,
            string description,
            bool enabled,
            MethodInfo method)
        {
            Name = name;
            Route = route;
            MimeType = mimeType;
            Description = description;
            Enabled = enabled;
            Method = method;
            DeclaringType = method.DeclaringType;
        }

        public object GetInstance()
        {
            if (Method.IsStatic) return null;
            if (_instance == null)
                _instance = Activator.CreateInstance(DeclaringType);
            return _instance;
        }
    }
}
