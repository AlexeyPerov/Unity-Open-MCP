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
    public partial class UnityOpenMcpBridgeWindow
    {
        private const string SettingsPersistenceNote =
            "Project-level runtime settings persist in `.unity-open-mcp/settings.json` at the project root. " +
            "Changes are saved immediately. v1 surface only — no Project Settings provider.";

        private void DrawSettingsTab()
        {
            _settingsTabScroll = EditorGUILayout.BeginScrollView(_settingsTabScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bridge runtime settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(SettingsPersistenceNote, MessageType.None);

            DrawAutoStartSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawDefaultGateModeSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawAuthSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawBindAddressSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawDenyListsSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawAuditLogSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityLogSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawVerifyCacheSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawBatchLimitsSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawSettingsStorageSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawSettingsPassiveBatchHint();

            EditorGUILayout.EndScrollView();
        }

        private void DrawAutoStartSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Auto-start bridge listener", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "When ON (default for new projects), the bridge listener auto-starts on Editor load. " +
                "Turn OFF to require a manual Start from the Status tab. Effective on the next " +
                "Editor domain reload / restart.",
                MessageType.None);

            var prev = BridgeProjectSettings.AutoStart;
            var next = EditorGUILayout.ToggleLeft(
                "Auto-start bridge HTTP listener on Editor load", prev);
            if (next != prev)
            {
                BridgeProjectSettings.SetAutoStart(next);
            }
        }

        private void DrawDefaultGateModeSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Project default gate mode", EditorStyles.miniBoldLabel);
            DrawGlobalGateModeControl(showStorageHint: false);
        }

        private void DrawAuthSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Bridge auth", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Controls whether the bridge HTTP listener requires a bearer token. " +
                "A per-session token is always minted into the instance lock and auto-discovered " +
                "by the MCP server, so enabling `required` needs no client-side config change. " +
                "`required` is mandatory for remote bind (see Listener bind below) and recommended " +
                "on shared machines.",
                MessageType.None);

            var current = BridgeAuthPolicy.GetDefault();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Auth mode", GUILayout.Width(120));
            var newIndex = EditorGUILayout.Popup(IndexOfAuthMode(current), AuthModeLabels());
            EditorGUILayout.EndHorizontal();
            if (newIndex != IndexOfAuthMode(current))
            {
                BridgeAuthPolicy.SetDefault(BridgeAuthPolicy.ValidModes[newIndex]);
            }
        }

        private void DrawBindAddressSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Listener bind", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Address the HTTP listener binds. Loopback (127.0.0.1, default) is reachable " +
                "only from this machine. Remote (0.0.0.0) exposes the bridge to the network and " +
                "is refused at start unless Auth mode is `required` — remote access without token " +
                "auth is unsafe. Effective on the next listener start (bridge restart / domain reload).",
                MessageType.None);

            var current = BridgeProjectSettings.BindAddress;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Bind address", GUILayout.Width(120));
            var newIndex = EditorGUILayout.Popup(IndexOfBindAddress(current), BindAddressLabels());
            EditorGUILayout.EndHorizontal();
            if (newIndex != IndexOfBindAddress(current))
            {
                var next = BridgeBindAddress.ValidAddresses[newIndex];
                // Surface the remote-requires-auth rule immediately so the
                // operator knows why the next start may refuse.
                var decision = BridgeBindAddress.Decide(next, BridgeAuthPolicy.GetDefault());
                if (!decision.Allowed)
                {
                    EditorGUILayout.HelpBox(
                        "Remote bind will be refused at start: set Auth mode to `required` first. " +
                        decision.RefusalReason,
                        MessageType.Warning);
                }
                BridgeProjectSettings.SetBindAddress(next);
            }
        }

        private static GUIContent[] BindAddressLabels()
        {
            var labels = new GUIContent[BridgeBindAddress.ValidAddresses.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent(BindAddressDescriptor(BridgeBindAddress.ValidAddresses[i]));
            }
            return labels;
        }

        private static int IndexOfBindAddress(string address)
        {
            for (int i = 0; i < BridgeBindAddress.ValidAddresses.Length; i++)
            {
                if (BridgeBindAddress.ValidAddresses[i] == address) return i;
            }
            return 0;
        }

        private static string BindAddressDescriptor(string address)
        {
            return address switch
            {
                BridgeBindAddress.Loopback => "127.0.0.1  (loopback only — default)",
                BridgeBindAddress.Remote   => "0.0.0.0  (remote — requires auth)",
                _ => address ?? BridgeBindAddress.Default
            };
        }

        private void DrawDenyListsSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Power-tool deny lists", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Regex patterns that block destructive execute_csharp snippets and execute_menu " +
                "paths BEFORE they run. Built-in defaults block editor exit, bulk asset deletion, " +
                "and unbounded builds. Override with your own list, or set the field to empty to " +
                "disable. Bypass per-request via gate: \"off\" + confirm_bypass: true (audited). " +
                "Edit the patterns directly in the settings file shown under Storage.",
                MessageType.None);

            var csharpDefaults = BridgeDenyList.DefaultCSharpDenyPatterns;
            var menuDefaults = BridgeDenyList.DefaultMenuDenyPatterns;
            var csharpActive = BridgeProjectSettings.CSharpDenyPatterns;
            var menuActive = BridgeProjectSettings.MenuDenyPatterns;

            DrawDenyListSummary("execute_csharp", csharpActive, csharpDefaults);
            DrawDenyListSummary("execute_menu", menuActive, menuDefaults);
        }

        private static void DrawDenyListSummary(string tool, string[] active, string[] defaults)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(tool, GUILayout.Width(120));
            // null/empty both mean "built-in defaults" (JsonUtility serializes
            // null as [] on disk, so the distinction is lost across reload).
            var hasCustom = active != null && active.Length > 0;
            var summary = !hasCustom ? $"defaults ({defaults.Length} patterns)" : $"{active.Length} custom pattern(s)";
            EditorGUILayout.LabelField(summary);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawAuditLogSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("On-disk audit log", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "When ON, every gate mutation (pass / fail / warn) and deny-list refusal is appended " +
                "to a rolling JSON-lines file under ~/.unity-open-mcp/audit/. Survives domain reload and " +
                "editor restart. Off by default — opt in for security-sensitive contexts.",
                MessageType.None);

            var prev = BridgeProjectSettings.AuditLogEnabled;
            var next = EditorGUILayout.ToggleLeft(
                "Persist gate-run audit records to disk", prev);
            if (next != prev)
            {
                BridgeProjectSettings.SetAuditLogEnabled(next);
            }

            if (prev || next)
            {
                EditorGUILayout.LabelField("Audit dir", BridgeAuditLog.AuditDir, EditorStyles.miniLabel);
            }
        }

        private static GUIContent[] AuthModeLabels()
        {
            var labels = new GUIContent[BridgeAuthPolicy.ValidModes.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent(AuthModeDescriptor(BridgeAuthPolicy.ValidModes[i]));
            }
            return labels;
        }

        private static int IndexOfAuthMode(string mode)
        {
            for (int i = 0; i < BridgeAuthPolicy.ValidModes.Length; i++)
            {
                if (BridgeAuthPolicy.ValidModes[i] == mode) return i;
            }
            return 0;
        }

        private static string AuthModeDescriptor(string mode)
        {
            return mode switch
            {
                "none"     => "none  (default — accept any loopback request)",
                "required" => "required  (require Authorization: Bearer <token>)",
                _ => mode ?? BridgeAuthPolicy.Default
            };
        }

        private void DrawActivityLogSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Activity log", EditorStyles.miniBoldLabel);
            var prev = BridgeActivityLog.Verbose;
            var next = EditorGUILayout.ToggleLeft(
                "Verbose mode (truncated request snippet, ≤ " + BridgeActivityLog.SnippetMaxChars + " chars)",
                prev);
            if (next != prev)
            {
                BridgeActivityLog.Verbose = next;
            }
            EditorGUILayout.HelpBox(
                "Default mode captures metadata only (tool name, gate mode, outcome, duration, HTTP status, body byte count). " +
                "Verbose mode additionally stores a truncated request body snippet for debugging. " +
                "Response bodies are never captured in v1.",
                MessageType.None);
        }

        private void DrawVerifyCacheSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Verify cache", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Time-to-live for the in-memory verify health snapshot. Drives how fresh the " +
                "`health/summary` MCP resource and the `gate_budget_estimate` \"cache\" mode are — " +
                "within the TTL they reuse the last scan/validate/gate result instead of re-running. " +
                "Shorter = fresher but more work; longer = faster but staler. Range " +
                $"{BridgeProjectSettings.MinVerifyCacheTtlSeconds}–{BridgeProjectSettings.MaxVerifyCacheTtlSeconds}s, " +
                $"default {BridgeProjectSettings.DefaultVerifyCacheTtlSeconds}s.",
                MessageType.None);

            var prev = BridgeProjectSettings.VerifyCacheTtlSeconds;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("TTL (seconds)", GUILayout.Width(120));
            var next = EditorGUILayout.IntField(prev);
            EditorGUILayout.EndHorizontal();
            // IntField returns 0 for empty input and allows arbitrary values;
            // the setter clamps, but we also surface the clamped delta visually.
            if (next != prev)
            {
                BridgeProjectSettings.SetVerifyCacheTtlSeconds(next);
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Effective", GUILayout.Width(120));
            EditorGUILayout.LabelField(
                $"{BridgeProjectSettings.VerifyCacheTtlSeconds}s  " +
                $"(snapshot {(VerifyCacheService.HasData ? "present" : "empty")}" +
                $"{(VerifyCacheService.HasData && VerifyCacheService.IsStale() ? ", stale" : "")})",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Invalidate now", EditorStyles.miniButton))
            {
                VerifyCacheService.Clear();
            }
        }

        // M27 Plan 4 — batch_execute nested-command cap. Exposes the
        // batchExecuteMaxCommands project setting (default 25, hard max 100)
        // so an operator can tune it from the Settings tab. Mirrors the
        // Coplay parity knob (configurable in the Editor UI).
        private void DrawBatchLimitsSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Batch execute", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Maximum number of nested tool calls one `unity_open_mcp_batch_execute` invocation " +
                "may carry. One HTTP round trip runs the sequence sequentially inside the open Editor, " +
                "wrapped in a single batch-level gate + undo group. Range " +
                $"{BridgeProjectSettings.MinBatchExecuteMaxCommands}–" +
                $"{BridgeProjectSettings.MaxBatchExecuteMaxCommands}, " +
                $"default {BridgeProjectSettings.DefaultBatchExecuteMaxCommands}.",
                MessageType.None);

            var prev = BridgeProjectSettings.BatchExecuteMaxCommands;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Max commands per batch", GUILayout.Width(160));
            var next = EditorGUILayout.IntField(prev);
            EditorGUILayout.EndHorizontal();
            // IntField allows arbitrary values; the setter clamps.
            if (next != prev)
            {
                BridgeProjectSettings.BatchExecuteMaxCommands = next;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Effective", GUILayout.Width(160));
            EditorGUILayout.LabelField(
                $"{BridgeProjectSettings.BatchExecuteMaxCommands}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettingsStorageSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Storage", EditorStyles.miniBoldLabel);

            var path = BridgeProjectSettings.SettingsPath ?? "(no project root)";
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Settings file", GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(path, EditorStyles.textField);
            if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(70)))
            {
                RevealSettingsFile();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "v1 schema (`.unity-open-mcp/settings.json`):\n" +
                "  - disabledTools: string[]\n" +
                "  - defaultGateMode: \"enforce\" | \"warn\" | \"off\"\n" +
                "  - autoStart: bool\n" +
                "  - verboseActivityLog: bool\n" +
                "  - authMode: \"none\" | \"required\"\n" +
                "  - bindAddress: \"127.0.0.1\" | \"0.0.0.0\"\n" +
                "  - csharpDenyPatterns: string[] (regex; non-empty overrides defaults)\n" +
                "  - menuDenyPatterns: string[] (regex; non-empty overrides defaults)\n" +
                "  - auditLogEnabled: bool\n" +
                "  - verifyCacheTtlSeconds: int (15–3600; verify health snapshot TTL)\n" +
                "  - batchExecuteMaxCommands: int (1–100; nested-command cap for batch_execute)\n" +
                "Future fields can extend this schema in place without breaking v1 readers.",
                MessageType.None);
        }

        private void RevealSettingsFile()
        {
            var path = BridgeProjectSettings.SettingsPath;
            if (string.IsNullOrEmpty(path)) return;
            if (!System.IO.File.Exists(path))
            {
                // Persist current defaults so the file is created, then reveal.
                BridgeProjectSettings.Save();
            }
            if (!System.IO.File.Exists(path)) return;
            EditorUtility.RevealInFinder(path);
        }

        // Passive batch hint shared between the Activity and Settings tabs (M4.5-12).
        private void DrawSettingsPassiveBatchHint()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Batch workflows", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                "Batch scan / baseline / regression workflows land their live progress and " +
                "per-entry results in the Batch tab (read-only). Batch execution itself is driven " +
                "from the MCP batch surface or the Hub — no batch execution controls are exposed " +
                "in this window.",
                MessageType.None);
        }

    }
}
