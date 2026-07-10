using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.UI.Controls;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// Shared constants for the bridge Editor window. Kept in a partial
    /// class so every tab view reads the same tooltip / URL wording
    /// without duplicating strings. Tooltip wording in particular must
    /// stay identical across tabs (bridge AGENTS.md: shared tooltip
    /// text lives in <c>const string Tooltip*</c> fields and is reused).
    /// </summary>
    public partial class UnityOpenMcpBridgeWindow
    {
        private const string BindAddress = "127.0.0.1";

        // Canonical repo URL — single source for every Info-tab link and any
        // in-window reference to the project.
        private const string RepoUrl = "https://github.com/AlexeyPerov/Unity-Open-MCP";

        // Shared hover-tooltip text for cell values / labels that surface
        // internal bridge concepts. Centralised so wording stays consistent
        // across the Tools tab, details foldout, and Activity rows.
        private const string TooltipMutating =
            "Mutating tool: changes Unity state (scene, assets, settings). " +
            "Runs the gate safety flow (checkpoint → mutate → validate → delta) when the gate mode is enforce/warn.";
        private const string TooltipReadOnly =
            "Read-only tool: does not change Unity state. Skips the gate flow.";
        private const string TooltipGateEnforce =
            "Gate mode 'enforce': the tool runs the checkpoint → mutate → validate flow and the MCP call fails if the mutation introduces new compile errors.";
        private const string TooltipGateWarn =
            "Gate mode 'warn': the gate still runs, but new compile errors surface as warnings rather than failing the call.";
        private const string TooltipGateOff =
            "Gate mode 'off': no checkpoint/validate — the mutation runs without the safety flow. Opt-in only.";
        private const string TooltipGateNa =
            "Read-only tool — the gate does not apply.";
        private const string TooltipSourceRegistry =
            "Registry tool: discovered via the [BridgeTool] attribute on a method. Its declaring type and parameter schema are reflected at load time.";
        private const string TooltipSourceHardcoded =
            "Built-in tool: dispatched by a hardcoded path in the bridge (not attribute-discovered). The parameter schema is mirrored from the MCP server definitions.";
        private const string TooltipEnabledToggle =
            "When unchecked, POST /tools/<name> is blocked and returns a 'tool_disabled' error before dispatch. State persists in settings.json.";
        private const string TooltipListener =
            "The local HTTP listener that MCP clients connect to. Green = running, yellow = compiling, red = stopped.";
        private const string TooltipBindUrl =
            "The URL MCP clients use to reach this bridge. Copy this into your MCP client config (or let the MCP server auto-discover it via the instance lock).";
        private const string TooltipPing =
            "Send a GET /ping to the local listener to confirm it is responding. Useful after Start or when an agent reports a connection failure.";

        private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }
}
