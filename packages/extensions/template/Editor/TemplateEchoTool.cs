using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpExtensions.Template
{
    // M16 Plan 10 — extension pack template.
    //
    // Demonstrates the contract every extension typed tool follows:
    //
    //   1. [BridgeToolType] on the class so BridgeToolRegistry.Scan picks it up
    //      automatically. NO core bridge edits are needed per pack — the
    //      registry sweeps every loaded assembly.
    //   2. [BridgeTool("unity_open_mcp_<domain>_<action>", ...)] on each
    //      method. The snake_case id is the MCP tool name; keep names lowercase
    //      and prefixed with the domain (template_echo here).
    //   3. The method returns a hand-rolled JSON string. Reuse the bridge's
    //      JsonBody helpers (no Newtonsoft dependency).
    //   4. Mutating tools MUST declare IsMutating = true and accept a
    //      `string[] pathsHint` parameter (the snake_case `paths_hint` field
    //      is bound by name). Read-only tools set ReadOnlyHint = true,
    //      Gate = GateMode.Off.
    //
    // Copy this class, rename the namespace, change the tool id, and put your
    // domain code inside the method body. See packages/extensions/navigation
    // for a full worked example (11 tools + EditMode tests).
    [BridgeToolType]
    public class TemplateEchoTool
    {
        // Read-only, gate-free echo. The simplest possible tool shape — returns
        // the input wrapped in a structured JSON envelope. Real read-only tools
        // replace the body with the actual read logic.
        [BridgeTool("unity_open_mcp_template_echo",
            Title = "Template Echo",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Template reference tool. Echoes a message back as structured " +
            "JSON. Used by the extension pack scaffolding tests to prove " +
            "BridgeToolRegistry discovers tools in extension assemblies.")]
        public string Echo(string message = null)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"echo\":").Append(Esc(message ?? "")).Append(',');
            sb.Append("\"pack\":\"template\"");
            sb.Append('}');
            return sb.ToString();
        }

        // Mutating example with the paths_hint contract. The tool does nothing
        // destructive here (template), but it shows the parameter shape every
        // mutating extension tool must follow so GatePolicy.Execute can scope
        // the verify checkpoint / delta pass.
        [BridgeTool("unity_open_mcp_template_touch",
            Title = "Template Touch",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle)]
        [System.ComponentModel.Description(
            "Template reference mutating tool. Declares the paths_hint " +
            "contract (mandatory scope for every mutating tool) but does " +
            "no real work — replace the body with your domain logic.")]
        public string Touch(string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return ErrorJson("paths_hint_required",
                    "Mutating tools require a non-empty paths_hint so the gate " +
                    "can scope the verify checkpoint. There is no whole-project " +
                    "fallback.");

            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"scope\":").Append(Esc(string.Join(", ", paths_hint)));
            sb.Append('}');
            return sb.ToString();
        }

        private static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        private static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
