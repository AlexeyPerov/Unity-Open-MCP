using System.Collections.Generic;

namespace UnityOpenMcpBridge
{
    // M13 T4.1 — Single source of truth for tool lifecycle policies.
    //
    // The bridge classifies every tool it dispatches. Registry tools declare
    // their policy via [BridgeTool(Lifecycle = ...)] and are read straight off
    // the entry; the legacy hardcoded meta-tools (dispatched via the switch in
    // BridgeHttpServer.DispatchTool, not the registry) are classified here so
    // the policy can never drift from what the dispatcher actually runs.
    //
    // The dispatcher consumes Resolve() to decide polling/settle behaviour and
    // to decide whether the active-scene dirty guard should preflight. Unknown
    // tools default to None — a read-only safe default — so a tool added
    // without an explicit policy never silently triggers a long settle wait or
    // a dirty-scene refusal.
    public static class ToolLifecycle
    {
        // Legacy hardcoded meta-tools. Registry tools carry their own policy on
        // the BridgeToolEntry and are NOT listed here (Resolve() checks the
        // registry first).
        static readonly Dictionary<string, LifecyclePolicy> HardcodedPolicies = new()
        {
            // ----- None: read-only, no settle -----
            { "unity_open_mcp_find_members",     LifecyclePolicy.None },
            { "unity_open_mcp_validate_edit",    LifecyclePolicy.None },
            { "unity_open_mcp_checkpoint_create",LifecyclePolicy.None },
            { "unity_open_mcp_delta",            LifecyclePolicy.None },
            { "unity_open_mcp_find_references",  LifecyclePolicy.None },
            { "unity_open_mcp_scan_paths",       LifecyclePolicy.None },
            { "unity_open_mcp_read_asset",       LifecyclePolicy.None },
            { "unity_open_mcp_search_assets",    LifecyclePolicy.None },

            // ----- EditorSettle: wait for asset refresh + serialization -----
            { "unity_open_mcp_apply_fix",        LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_reserialize",      LifecyclePolicy.EditorSettle },
            // M16 Plan 1 — typed asset/material/prefab mutators. They touch
            // Assets/ (write .mat/.prefab, refresh AssetDatabase, mutate scene
            // instances). None of them recompile editor scripts, so they wait
            // for asset refresh but do not need the restart-then-settle path.
            { "unity_open_mcp_assets_create_folder", LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_assets_copy",         LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_assets_move",         LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_assets_delete",       LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_assets_refresh",      LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_material_create",     LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_material_set_property",LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_material_set_keyword",LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_material_set_shader", LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_instantiate",  LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_create",       LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_open",         LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_close",        LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_save",         LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_apply",        LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_revert",       LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_prefab_unpack",       LifecyclePolicy.EditorSettle },
            // M16 Plan 2 — typed GameObject/component mutators. They mutate
            // live scene hierarchy (no asset writes, no script recompile),
            // so EditorSettle is enough — no domain-reload risk.
            { "unity_open_mcp_gameobject_create",   LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_gameobject_destroy",  LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_gameobject_duplicate",LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_gameobject_modify",   LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_gameobject_set_parent",LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_component_add",       LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_component_destroy",   LifecyclePolicy.EditorSettle },
            { "unity_open_mcp_component_modify",    LifecyclePolicy.EditorSettle },

            // ----- RestartThenSettle: may trigger a domain reload -----
            // execute_csharp / invoke_method can recompile; execute_menu can
            // hit any menu (scene switch, reimport, package op); compile_check
            // spawns a fresh Unity recompiling from scratch.
            { "unity_open_mcp_execute_csharp",   LifecyclePolicy.RestartThenSettle },
            { "unity_open_mcp_invoke_method",    LifecyclePolicy.RestartThenSettle },
            { "unity_open_mcp_execute_menu",     LifecyclePolicy.RestartThenSettle },

            // ----- CustomConfirmation: external completion signal -----
            // run_tests is async + domain-reload-safe via a file handoff the
            // MCP server polls; the dispatcher does NOT settle-wait on it.
            { "unity_senses_run_tests",           LifecyclePolicy.CustomConfirmation },
        };

        // Resolve the lifecycle policy for a dispatched tool.
        //
        // Registry tools win (their attribute is the authoritative declaration);
        // legacy hardcoded tools fall through to the table above; anything else
        // is treated as read-only (None). This keeps the safe default intact
        // for tools added without an explicit policy.
        public static LifecyclePolicy Resolve(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return LifecyclePolicy.None;

            if (BridgeToolRegistry.TryGet(toolName, out var entry))
                return entry.Lifecycle;

            return HardcodedPolicies.TryGetValue(toolName, out var hardcoded)
                ? hardcoded
                : LifecyclePolicy.None;
        }

        // Does resolving this policy require the dispatcher to settle-wait
        // before returning? EditorSettle + RestartThenSettle both block on
        // EditorApplication.isCompiling; CustomConfirmation hands off to an
        // external poller (the MCP server) and returns immediately.
        public static bool RequiresSettleWait(LifecyclePolicy policy)
        {
            return policy == LifecyclePolicy.EditorSettle
                || policy == LifecyclePolicy.RestartThenSettle;
        }

        // Should the active-scene dirty guard preflight this tool? Only ops that
        // can disrupt the editor (recompile, scene switch) are guarded —
        // mutating-but-settled ops (apply_fix, reserialize) never trigger
        // Unity's native save modal, so guarding them would just add friction.
        public static bool RequiresDirtyGuard(string toolName)
        {
            return Resolve(toolName) == LifecyclePolicy.RestartThenSettle;
        }
    }
}
