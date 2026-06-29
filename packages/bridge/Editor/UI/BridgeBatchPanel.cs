using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.UI.Controls;

namespace UnityOpenMcpBridge
{
    // T20.7.5.1 — the in-Editor batch-run details panel.
    //
    // Read-only view of BridgeBatchRunHistory: renders the active run's live
    // progress (pending / running / done / failed) and per-entry results, plus a
    // compact list of recently completed runs. The panel does NOT start or
    // mutate batches — it only observes the state an in-Editor batch source
    // writes to (the future M26 / T26.3 batch_* flow, or any operator-driven
    // bulk run that funnels progress through BridgeBatchRunHistory).
    //
    // Re-render is driven by BridgeBatchRunHistory.Changed → the host window's
    // RepaintTick (subscribed in OnEnable), so progress updates without a manual
    // refresh.
    public static class BridgeBatchPanel
    {
        // Host-window-side scroll state. Kept here (not on the EditorWindow)
        // because the panel is a static helper, mirroring OptionalDependenciesPanel.
        private static Vector2 _activeScroll;
        private static Vector2 _completedScroll;
        private static bool _completedFoldout = true;

        public static void Draw()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Batch runs (in-memory, read-only)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Live view of in-Editor batch runs. An active batch shows progress and per-entry " +
                "results as it runs; completed runs are retained in a session ring buffer. " +
                "This panel observes batch state — it does not start or stop batches. " +
                "Batch execution is driven from the MCP batch surface or the Hub; " +
                "use the Gate tab's Manual validate for ad-hoc scoped scans.",
                MessageType.None);

            DrawActiveRun();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawCompletedRuns();
        }

        private static void DrawActiveRun()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Active run", EditorStyles.miniBoldLabel);

            var active = BridgeBatchRunHistory.Active;
            if (active == null)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No active batch run. Start one from the MCP batch surface or the Hub.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            DrawRunHeader(active);

            if (active.TotalCount == 0)
            {
                EditorGUILayout.LabelField("(no entries yet)", EditorStyles.miniLabel);
                return;
            }

            DrawProgressSummary(active);

            _activeScroll = EditorGUILayout.BeginScrollView(_activeScroll, GUILayout.MinHeight(120));
            for (int i = 0; i < active.Entries.Count; i++)
            {
                DrawEntryRow(active.Entries[i]);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawCompletedRuns()
        {
            EditorGUILayout.Space(4);
            _completedFoldout = EditorGUILayout.Foldout(
                _completedFoldout,
                $"Recently completed ({BridgeBatchRunHistory.CompletedCount})",
                true);
            if (!_completedFoldout) return;

            var completed = BridgeBatchRunHistory.Completed;
            if (completed == null || completed.Count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No completed batch runs in this session yet.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            EditorGUILayout.LabelField(
                $"Retained: {BridgeBatchRunHistory.CompletedCount} / {BridgeBatchRunHistory.Capacity}    " +
                $"total this session: {BridgeBatchRunHistory.TotalRunsRecorded}",
                EditorStyles.miniLabel);

            _completedScroll = EditorGUILayout.BeginScrollView(_completedScroll, GUILayout.MinHeight(80));
            // Most-recent first.
            for (int i = completed.Count - 1; i >= 0; i--)
            {
                var run = completed[i];
                if (run == null) continue;
                DrawCompletedRunSummary(run);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void DrawRunHeader(BridgeBatchRun run)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(run.Label ?? "(untitled run)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Run id", "Opaque id supplied by the batch source.", 70);
            EditorGUILayout.SelectableLabel(run.RunId ?? "(none)", EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Source", "Who started the batch (mcp, hub, manual, ...).", 70);
            EditorGUILayout.LabelField(run.Source ?? "(unknown)");
            GUILayout.FlexibleSpace();
            BridgeGUIUtilities.FieldLabel("Started", "When the run started (local time).", 60);
            EditorGUILayout.LabelField(run.StartedAt.ToString("HH:mm:ss"));
            if (run.CompletedAt.HasValue)
            {
                BridgeGUIUtilities.FieldLabel("Finished", "When the run completed (local time).", 60);
                EditorGUILayout.LabelField(run.CompletedAt.Value.ToString("HH:mm:ss"));
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static void DrawProgressSummary(BridgeBatchRun run)
        {
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.DrawColoredLabel($"pending: {run.PendingCount}", new Color(0.7f, 0.7f, 0.7f), 90,
                "Entries queued but not yet started.");
            BridgeGUIUtilities.DrawColoredLabel($"running: {run.RunningCount}", new Color(0.7f, 0.85f, 1f), 90,
                "Entries currently executing.");
            BridgeGUIUtilities.DrawColoredLabel($"done: {run.DoneCount}", new Color(0.6f, 0.9f, 0.6f), 80,
                "Entries that completed successfully.");
            BridgeGUIUtilities.DrawColoredLabel($"failed: {run.FailedCount}", new Color(1f, 0.5f, 0.5f), 80,
                "Entries that failed.");
            if (run.SkippedCount > 0)
            {
                BridgeGUIUtilities.DrawColoredLabel($"skipped: {run.SkippedCount}", new Color(1f, 0.9f, 0.4f), 90,
                    "Entries skipped (e.g. disabled tool, gate refused).");
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{run.DoneCount + run.FailedCount + run.SkippedCount} / {run.TotalCount}",
                EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawEntryRow(BridgeBatchRunEntry entry)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.DrawColoredLabel(
                EntryStatusLabel(entry.Status), EntryStatusColor(entry.Status), 80,
                EntryStatusTooltip(entry.Status));
            if (!string.IsNullOrEmpty(entry.ToolName))
            {
                EditorGUILayout.LabelField(entry.ToolName, EditorStyles.boldLabel, GUILayout.Width(240));
            }
            else
            {
                EditorGUILayout.LabelField("-", GUILayout.Width(240));
            }
            GUILayout.FlexibleSpace();
            if (entry.DurationMs > 0)
            {
                EditorGUILayout.LabelField($"{entry.DurationMs} ms", GUILayout.Width(80));
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(entry.ArgsSummary))
            {
                EditorGUILayout.LabelField(entry.ArgsSummary, EditorStyles.wordWrappedMiniLabel);
            }

            if (!string.IsNullOrEmpty(entry.ErrorCode) || !string.IsNullOrEmpty(entry.ErrorMessage))
            {
                var msg = string.IsNullOrEmpty(entry.ErrorCode)
                    ? entry.ErrorMessage
                    : $"{entry.ErrorCode}: {entry.ErrorMessage}";
                EditorGUILayout.HelpBox(msg, MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawCompletedRunSummary(BridgeBatchRun run)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            var outcomeColor = run.FailedCount > 0
                ? new Color(1f, 0.5f, 0.5f)
                : new Color(0.6f, 0.9f, 0.6f);
            var prev = GUI.color;
            GUI.color = outcomeColor;
            GUILayout.Label(run.FailedCount > 0 ? "FAIL" : "PASS", EditorStyles.boldLabel, GUILayout.Width(50));
            GUI.color = prev;
            EditorGUILayout.LabelField(run.Label ?? "(untitled run)", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                $"{run.DoneCount} done   {run.FailedCount} failed   {run.SkippedCount} skipped   / {run.TotalCount}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"source: {run.Source ?? "?"}", EditorStyles.miniLabel, GUILayout.Width(160));
            EditorGUILayout.LabelField(
                $"started {run.StartedAt:HH:mm:ss}" +
                (run.CompletedAt.HasValue ? $" → finished {run.CompletedAt:HH:mm:ss}" : ""),
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static string EntryStatusLabel(BridgeBatchEntryStatus status)
        {
            return status switch
            {
                BridgeBatchEntryStatus.Pending => "pending",
                BridgeBatchEntryStatus.Running => "running",
                BridgeBatchEntryStatus.Done => "done",
                BridgeBatchEntryStatus.Failed => "failed",
                BridgeBatchEntryStatus.Skipped => "skipped",
                _ => status.ToString()
            };
        }

        private static Color EntryStatusColor(BridgeBatchEntryStatus status)
        {
            return status switch
            {
                BridgeBatchEntryStatus.Pending => new Color(0.7f, 0.7f, 0.7f),
                BridgeBatchEntryStatus.Running => new Color(0.7f, 0.85f, 1f),
                BridgeBatchEntryStatus.Done => new Color(0.6f, 0.9f, 0.6f),
                BridgeBatchEntryStatus.Failed => new Color(1f, 0.5f, 0.5f),
                BridgeBatchEntryStatus.Skipped => new Color(1f, 0.9f, 0.4f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        private static string EntryStatusTooltip(BridgeBatchEntryStatus status)
        {
            return status switch
            {
                BridgeBatchEntryStatus.Pending => "Entry queued but not yet started.",
                BridgeBatchEntryStatus.Running => "Entry currently executing.",
                BridgeBatchEntryStatus.Done => "Entry completed successfully.",
                BridgeBatchEntryStatus.Failed => "Entry failed (see error below).",
                BridgeBatchEntryStatus.Skipped => "Entry skipped (e.g. disabled tool, gate refused).",
                _ => null
            };
        }
    }
}
