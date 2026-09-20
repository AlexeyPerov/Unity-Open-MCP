using System;
namespace UnityOpenMcpBridge
{
    /// <summary>Marks a MenuItem verifier as inspection-only: no writes, reload, scene switch, or play transition.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class BridgeReadOnlyMenuAttribute : Attribute { }
}
