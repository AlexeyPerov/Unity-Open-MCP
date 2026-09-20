using System;

namespace UnityOpenMcpBridge
{
    /// <summary>Project-owned catalog declaration. Discovery requires BridgeToolType on the class.
    /// IDs use project.&lt;owner&gt;.&lt;command&gt;; built-in tool names are reserved.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class ProjectCommandAttribute : BridgeToolAttribute
    {
        public string Description { get; set; }
        public string Package { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public string[] DeprecatedAliases { get; set; } = Array.Empty<string>();
        public bool Async { get; set; }
        public bool Cancellable { get; set; }
        public ProjectCommandAttribute(string id) : base(id) { }
    }

    /// <summary>Additional JSON Schema annotations. Requiredness comes from C# optional defaults;
    /// nullable value types and reference parameters accept explicit null.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ProjectCommandParameterAttribute : Attribute
    {
        public string Description { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public string[] Examples { get; set; } = Array.Empty<string>();
        public string[] DeprecatedAliases { get; set; } = Array.Empty<string>();
    }
}
