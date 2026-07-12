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
        private const string ActivityPrivacyNote =
            "Default capture is metadata only (no request/response bodies). " +
            "Verbose mode adds a truncated request snippet for debugging.";

        private void DrawActivityTab()
        {
            // Page scroll is owned by the shell (DrawContent).
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Activity log (in-memory ring buffer)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Session-scoped ring buffer (capacity " + BridgeActivityLog.Capacity + ") of bridge HTTP events. " +
                "In-memory only — cleared on domain reload or Editor restart. " +
                ActivityPrivacyNote,
                MessageType.None);

            DrawActivityControls();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityFilters();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityList();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawActivityBatchSection();
        }

        private void DrawActivityControls()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();

            var prevVerbose = BridgeActivityLog.Verbose;
            var newVerbose = EditorGUILayout.ToggleLeft(
                "Verbose mode (captures truncated request snippet, ≤ " + BridgeActivityLog.SnippetMaxChars + " chars)",
                prevVerbose, GUILayout.Width(420));
            if (newVerbose != prevVerbose)
            {
                BridgeActivityLog.Verbose = newVerbose;
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button(new GUIContent("Clear", "Empty the in-memory activity buffer. Not persisted, so this only affects the current session."), EditorStyles.miniButton, GUILayout.Width(80)))
            {
                BridgeActivityLog.Clear();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                $"buffer: {BridgeActivityLog.Count} / {BridgeActivityLog.Capacity}    " +
                $"recorded: {BridgeActivityLog.TotalRecorded}    trimmed: {BridgeActivityLog.TotalDroppedTrim}",
                EditorStyles.miniLabel);
        }

        private void DrawActivityFilters()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Filters", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            _activityFilterToolRequests = DrawActivityFilterToggle(
                _activityFilterToolRequests, "Tool requests", 120,
                "Show successful / in-flight tool dispatch calls (POST /tools/<name>).");
            _activityFilterDisabled = DrawActivityFilterToggle(
                _activityFilterDisabled, "Tool disabled", 110,
                "Show calls rejected because the tool is toggled off (tool_disabled error).");
            _activityFilterErrors = DrawActivityFilterToggle(
                _activityFilterErrors, "Errors", 80,
                "Show failed dispatches, unknown paths, and resource errors.");
            _activityFilterPing = DrawActivityFilterToggle(
                _activityFilterPing, "/ping", 70,
                "Show connectivity checks (GET /ping) the MCP server sends.");
            _activityFilterResources = DrawActivityFilterToggle(
                _activityFilterResources, "Resources", 90,
                "Show resource reads (GET /resources/...).");
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(new GUIContent("Search", "Filter events by tool name, error code/message, or gate mode."), GUILayout.Width(50));
            var newSearch = EditorGUILayout.TextField(_activitySearch ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(160));
            if (newSearch != _activitySearch)
            {
                _activitySearch = newSearch ?? "";
            }
            EditorGUILayout.EndHorizontal();
        }

        // Filter toggle with a hover tooltip. EditorGUILayout.ToggleLeft does
        // not take a GUIContent, so we reserve the rect and use GUI.Toggle.
        private static bool DrawActivityFilterToggle(bool value, string label, int width, string tooltip)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(label, tooltip), EditorStyles.label, GUILayout.Width(width));
            return GUI.Toggle(rect, value, new GUIContent(label, tooltip));
        }

        private void DrawActivityList()
        {
            var all = BridgeActivityLog.Events;
            if (all == null || all.Count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No activity captured in this session yet. Trigger a /ping or tool call to populate.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            // Display most-recent first.
            var search = (_activitySearch ?? "").Trim();
            int shown = 0;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var evt = all[i];
                if (evt == null) continue;
                if (!PassesActivityFilter(evt)) continue;
                if (!string.IsNullOrEmpty(search) && !MatchesActivitySearch(evt, search)) continue;
                DrawActivityRow(evt);
                shown++;
            }

            if (shown == 0)
            {
                EditorGUILayout.Space(4);
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No events match the current filter / search.",
                    new Color(0.7f, 0.7f, 0.7f));
            }
        }

        private bool PassesActivityFilter(BridgeActivityEvent evt)
        {
            return evt.Kind switch
            {
                BridgeActivityKind.ToolRequest => _activityFilterToolRequests,
                BridgeActivityKind.ToolDisabled => _activityFilterDisabled,
                BridgeActivityKind.ToolError => _activityFilterErrors,
                BridgeActivityKind.Ping => _activityFilterPing,
                BridgeActivityKind.ResourceRequest => _activityFilterResources,
                BridgeActivityKind.ResourceError => _activityFilterResources || _activityFilterErrors,
                BridgeActivityKind.UnknownPath => _activityFilterErrors,
                _ => true
            };
        }

        private bool MatchesActivitySearch(BridgeActivityEvent evt, string search)
        {
            if (evt == null || string.IsNullOrEmpty(search)) return true;
            if (Contains(evt.ToolName, search)) return true;
            if (Contains(evt.ErrorCode, search)) return true;
            if (Contains(evt.ErrorMessage, search)) return true;
            if (Contains(evt.GateMode, search)) return true;
            return false;
        }

        private void DrawActivityRow(BridgeActivityEvent evt)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(evt.Timestamp.ToString("HH:mm:ss"), GUILayout.Width(70));
            BridgeGUIUtilities.DrawColoredLabel(ActivityKindLabel(evt.Kind), ActivityKindColor(evt.Kind), 110);
            if (!string.IsNullOrEmpty(evt.ToolName))
            {
                EditorGUILayout.LabelField(evt.ToolName, EditorStyles.boldLabel, GUILayout.Width(220));
            }
            else
            {
                EditorGUILayout.LabelField("-", GUILayout.Width(220));
            }
            if (!string.IsNullOrEmpty(evt.GateMode))
            {
                EditorGUILayout.LabelField(
                    new GUIContent($"gate: {evt.GateMode}", GateModeTooltip(evt.GateMode)),
                    GUILayout.Width(110));
            }
            BridgeGUIUtilities.DrawColoredLabel(
                ActivityOutcomeLabel(evt.Outcome),
                ActivityOutcomeColor(evt.Outcome), 80);
            EditorGUILayout.LabelField($"HTTP {evt.HttpStatus}", GUILayout.Width(70));
            EditorGUILayout.LabelField($"{evt.DurationMs} ms", GUILayout.Width(80));
            if (evt.RequestBodyLength > 0)
            {
                EditorGUILayout.LabelField($"body: {evt.RequestBodyLength}b", GUILayout.Width(90));
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(evt.ErrorCode) || !string.IsNullOrEmpty(evt.ErrorMessage))
            {
                var msg = string.IsNullOrEmpty(evt.ErrorCode)
                    ? evt.ErrorMessage
                    : $"{evt.ErrorCode}: {evt.ErrorMessage}";
                EditorGUILayout.HelpBox(msg, evt.Outcome == BridgeActivityOutcome.Success ? MessageType.None : MessageType.Warning);
            }

            if (BridgeActivityLog.Verbose && !string.IsNullOrEmpty(evt.RequestSnippet))
            {
                EditorGUILayout.LabelField("Request snippet", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(
                    evt.RequestSnippet, EditorStyles.textArea,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight * 2));
            }

            EditorGUILayout.EndVertical();
        }

        private static string ActivityKindLabel(BridgeActivityKind kind)
        {
            return kind switch
            {
                BridgeActivityKind.Ping => "/ping",
                BridgeActivityKind.ToolRequest => "tool",
                BridgeActivityKind.ToolDisabled => "disabled",
                BridgeActivityKind.ToolError => "tool-err",
                BridgeActivityKind.ResourceRequest => "resource",
                BridgeActivityKind.ResourceError => "resource-err",
                BridgeActivityKind.UnknownPath => "404",
                _ => kind.ToString()
            };
        }

        private static Color ActivityKindColor(BridgeActivityKind kind)
        {
            return kind switch
            {
                BridgeActivityKind.Ping => new Color(0.7f, 0.85f, 1f),
                BridgeActivityKind.ToolRequest => new Color(0.7f, 0.85f, 1f),
                BridgeActivityKind.ToolDisabled => new Color(1f, 0.9f, 0.4f),
                BridgeActivityKind.ToolError => new Color(1f, 0.5f, 0.5f),
                BridgeActivityKind.ResourceRequest => new Color(0.7f, 0.7f, 0.7f),
                BridgeActivityKind.ResourceError => new Color(1f, 0.5f, 0.5f),
                BridgeActivityKind.UnknownPath => new Color(0.85f, 0.55f, 0.55f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        private static string ActivityOutcomeLabel(BridgeActivityOutcome outcome)
        {
            return outcome switch
            {
                BridgeActivityOutcome.Success => "ok",
                BridgeActivityOutcome.Failed => "fail",
                BridgeActivityOutcome.Timeout => "timeout",
                BridgeActivityOutcome.Skipped => "skip",
                _ => "-"
            };
        }

        private static Color ActivityOutcomeColor(BridgeActivityOutcome outcome)
        {
            return outcome switch
            {
                BridgeActivityOutcome.Success => new Color(0.6f, 0.9f, 0.6f),
                BridgeActivityOutcome.Failed => new Color(1f, 0.5f, 0.5f),
                BridgeActivityOutcome.Timeout => new Color(1f, 0.9f, 0.4f),
                BridgeActivityOutcome.Skipped => new Color(0.7f, 0.7f, 0.7f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        // ---------- Batch section (M29 Plan 3) ----------
        //
        // Batch is no longer a peer tab. The read-only batch-run panel
        // (BridgeBatchPanel) now lives here as a section under Activity so an
        // operator sees live batch progress and per-entry results alongside
        // the HTTP event log. The panel keeps its own bounded list scrolls
        // (active / completed) — those are nested list regions, not competing
        // full-page scrolls, so the single-scroll-owner contract still holds.
        // The foldout auto-opens when there is an active or completed run so a
        // batch in flight is discoverable without expanding it by hand.
        [NonSerialized] private bool _activityBatchFoldout;

        private void DrawActivityBatchSection()
        {
            // Auto-expand once a run exists (active or recently completed).
            if (!_activityBatchFoldout &&
                (BridgeBatchRunHistory.Active != null || BridgeBatchRunHistory.CompletedCount > 0))
            {
                _activityBatchFoldout = true;
            }

            EditorGUILayout.Space(4);
            _activityBatchFoldout = EditorGUILayout.Foldout(
                _activityBatchFoldout,
                $"Batch runs  —  active: {(BridgeBatchRunHistory.Active != null ? 1 : 0)}  completed: {BridgeBatchRunHistory.CompletedCount}",
                true);
            if (!_activityBatchFoldout) return;

            // BridgeBatchPanel renders its own header + bounded list scrolls.
            BridgeBatchPanel.Draw();
        }

    }
}
