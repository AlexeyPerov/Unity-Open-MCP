using System;
using System.Reflection;
using System.ComponentModel;

namespace UnityOpenMcpBridge
{
    public class BridgeToolEntry
    {
        public string Name { get; }
        public string Title { get; }
        public bool IsMutating { get; }
        public GateMode Gate { get; }
        public bool ReadOnlyHint { get; }
        public bool IdempotentHint { get; }
        public bool DestructiveHint { get; }
        public bool Enabled { get; }
        public MethodInfo Method { get; }
        public ParameterInfo[] Parameters { get; }
        public Type DeclaringType { get; }

        object _instance;

        public BridgeToolEntry(
            string name,
            string title,
            bool isMutating,
            GateMode gate,
            bool readOnlyHint,
            bool idempotentHint,
            bool destructiveHint,
            bool enabled,
            MethodInfo method)
        {
            Name = name;
            Title = title;
            IsMutating = isMutating;
            Gate = gate;
            ReadOnlyHint = readOnlyHint;
            IdempotentHint = idempotentHint;
            DestructiveHint = destructiveHint;
            Enabled = enabled;
            Method = method;
            Parameters = method.GetParameters();
            DeclaringType = method.DeclaringType;
        }

        public object GetInstance()
        {
            if (Method.IsStatic) return null;
            if (_instance == null)
                _instance = Activator.CreateInstance(DeclaringType);
            return _instance;
        }

        public string GetParameterDescription(ParameterInfo param)
        {
            var desc = param.GetCustomAttribute<DescriptionAttribute>();
            return desc?.Description;
        }
    }
}
