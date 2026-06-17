using System;

namespace UnityOpenMcpBridge
{
    // M13 T4.1 — Centralized lifecycle policy taxonomy.
    //
    // Different operations have different "settle" requirements. A read-only
    // inspection needs nothing; an asset import needs an editor settle; a
    // package install or recompile survives a domain reload; a build waits for
    // an external completion signal. The policy is declared once per tool
    // (BridgeToolAttribute.Lifecycle for registry tools, ToolLifecycle.Map for
    // the legacy hardcoded tools) and consumed by the bridge dispatcher to pick
    // the right wait/retry behaviour — never reimplemented per command.
    //
    // The snake_case wire token returned by ToWireString is the value agents
    // see in the response envelope / capabilities discovery.
    public enum LifecyclePolicy
    {
        // Read-only: no settle needed (ping, find_members, read_console).
        None,
        // Wait for asset refresh + serialization to finish (apply_fix, reserialize).
        EditorSettle,
        // Survives a domain reload; dispatcher waits for the compile to settle
        // and the bridge re-pings automatically (execute_csharp, invoke_method,
        // execute_menu, compile_check).
        RestartThenSettle,
        // Waits for an external completion signal (play-mode test run uses a
        // file-handoff poll the MCP server reads back).
        CustomConfirmation
    }

    public static class LifecyclePolicyExtensions
    {
        public static string ToWireString(this LifecyclePolicy policy)
        {
            return policy switch
            {
                LifecyclePolicy.None => "none",
                LifecyclePolicy.EditorSettle => "editor_settle",
                LifecyclePolicy.RestartThenSettle => "restart_then_settle",
                LifecyclePolicy.CustomConfirmation => "custom_confirmation",
                _ => "none"
            };
        }

        public static LifecyclePolicy ParseWireString(string value)
        {
            if (string.IsNullOrEmpty(value)) return LifecyclePolicy.None;
            return value switch
            {
                "none" => LifecyclePolicy.None,
                "editor_settle" => LifecyclePolicy.EditorSettle,
                "restart_then_settle" => LifecyclePolicy.RestartThenSettle,
                "custom_confirmation" => LifecyclePolicy.CustomConfirmation,
                _ => LifecyclePolicy.None
            };
        }
    }
}
