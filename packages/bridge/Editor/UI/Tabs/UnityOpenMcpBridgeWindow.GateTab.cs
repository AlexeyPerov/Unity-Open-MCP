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
        private void DrawGateTab()
        {
            // Page scroll is owned by the shell (DrawContent). Bounded result
            // snippets below keep their own MinHeight scrolls.
            DrawGateDefaultPolicySection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawGateLatestResultSection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawGateCheckpointHistorySection();
            BridgeGUIUtilities.HorizontalLine(2, 4);
            DrawGateManualValidateSection();
        }

        private void DrawGateDefaultPolicySection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Project default gate mode", EditorStyles.boldLabel);
            DrawGlobalGateModeControl(showStorageHint: true);
        }

        // Shared, highlighted rendering of the project-wide gate control, used by
        // both the Gate tab and the Settings tab so there is one visual source of
        // truth. This is the global gate on/off: setting it to `off` disables the
        // checkpoint→mutate→validate safety flow project-wide for any mutating
        // call that does not send an explicit request-level `gate`. The control is
        // tinted yellow to stand out, and the dangerous states (`off`, `warn`)
        // get explicit warning text so the operator can't miss them.
        private static void DrawGlobalGateModeControl(bool showStorageHint)
        {
            var current = BridgeGateDefaultPolicy.GetDefault();

            // Soft yellow tint over the whole control so the global safety knob is
            // visually distinct from the per-tool rows around it.
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.96f, 0.7f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            try
            {
                EditorGUILayout.LabelField(
                    "This is the global gate on/off control for this project.",
                    EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Default mode", GUILayout.Width(120));
                var newMode = EditorGUILayout.Popup(IndexOfMode(current), ModeLabels());
                EditorGUILayout.EndHorizontal();
                if (newMode != IndexOfMode(current))
                {
                    BridgeGateDefaultPolicy.SetDefault(BridgeGateDefaultPolicy.ValidModes[newMode]);
                    current = BridgeGateDefaultPolicy.GetDefault();
                }

                EditorGUILayout.LabelField("Effective policy", ModeDescriptor(current), EditorStyles.miniLabel);

                // State-specific callouts so the dangerous modes are unmissable.
                if (current == BridgeGateDefaultPolicy.Off)
                {
                    EditorGUILayout.HelpBox(
                        "Gate is OFF project-wide. Mutating tools run WITHOUT the checkpoint → validate safety flow, " +
                        "so a mutation that introduces compile errors will not fail the MCP call. " +
                        "This is the global turn-off — re-enable with `enforce` or `warn`.",
                        MessageType.Error);
                }
                else if (current == BridgeGateDefaultPolicy.Warn)
                {
                    EditorGUILayout.HelpBox(
                        "Gate is in `warn` mode project-wide: mutating tools still run the safety flow, but new " +
                        "compile errors surface as warnings instead of failing the MCP call.",
                        MessageType.Warning);
                }

                EditorGUILayout.LabelField("Precedence", BridgeGateDefaultPolicy.DescribePrecedence(), EditorStyles.miniLabel);
                if (showStorageHint)
                {
                    EditorGUILayout.LabelField(
                        "Storage",
                        BridgeProjectSettings.SettingsPath ?? "(no project root)",
                        EditorStyles.miniLabel);
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }

        private static GUIContent[] ModeLabels()
        {
            var labels = new GUIContent[BridgeGateDefaultPolicy.ValidModes.Length];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = new GUIContent(ModeDescriptor(BridgeGateDefaultPolicy.ValidModes[i]));
            }
            return labels;
        }

        // M29 Plan 3 — read-only echo of the project default gate mode. The
        // single interactive control lives on the Gate tab; the Settings tab
        // shows this echo + a pointer instead of a second interactive popup,
        // so there is one primary control and no divergent help text. Reuses
        // the same yellow-tinted visual treatment for consistency with the
        // primary control, but renders the mode as a non-interactive label.
        private static void DrawGlobalGateModeReadOnly()
        {
            var current = BridgeGateDefaultPolicy.GetDefault();

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.96f, 0.7f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = prevBg;

            try
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Default mode", GUILayout.Width(120));
                EditorGUILayout.LabelField(ModeDescriptor(current), EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField("Effective policy", ModeDescriptor(current), EditorStyles.miniLabel);

                if (current == BridgeGateDefaultPolicy.Off)
                {
                    EditorGUILayout.HelpBox(
                        "Gate is OFF project-wide — mutating tools run WITHOUT the checkpoint → validate safety flow.",
                        MessageType.Error);
                }
                else if (current == BridgeGateDefaultPolicy.Warn)
                {
                    EditorGUILayout.HelpBox(
                        "Gate is in `warn` mode — new compile errors surface as warnings, not MCP errors.",
                        MessageType.Warning);
                }

                EditorGUILayout.LabelField(
                    "Change the project default gate mode on the Gate tab.",
                    EditorStyles.miniLabel);
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }

        private static int IndexOfMode(string mode)
        {
            for (int i = 0; i < BridgeGateDefaultPolicy.ValidModes.Length; i++)
            {
                if (BridgeGateDefaultPolicy.ValidModes[i] == mode) return i;
            }
            return 0;
        }

        private static string ModeDescriptor(string mode)
        {
            return mode switch
            {
                "enforce" => "enforce  (default — MCP errors on new errors)",
                "warn"    => "warn  (new errors surface as warnings, not MCP errors)",
                "off"     => "off  (no checkpoint/validate — explicit opt-in only)",
                _ => mode ?? BridgeGateDefaultPolicy.Default
            };
        }

        private void DrawGateLatestResultSection()
        {
            EditorGUILayout.Space(6);
            _gateLatestFoldout = EditorGUILayout.Foldout(_gateLatestFoldout, "Latest gate result (session, in-memory)", true);
            if (!_gateLatestFoldout) return;

            var latest = BridgeGateRunHistory.Latest;
            if (latest == null)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No gate results captured in this session yet. Trigger a mutating tool call to populate.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            _gateLatestScroll = EditorGUILayout.BeginScrollView(_gateLatestScroll, GUILayout.MinHeight(80));

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(
                new GUIContent("Tool", "The mutating tool call that triggered this gate run."),
                new GUIContent(latest.ToolName ?? "(unknown)"), EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Mode",
                "The effective gate mode that ran for this call (request-level gate overrides the project default).",
                120);
            EditorGUILayout.LabelField(latest.EffectiveMode ?? "(unknown)");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Outcome",
                "Result of the gate flow. Passed = no new errors. Warned = errors downgraded to warnings. Failed = new errors blocked the call. Skipped = gate off.",
                120);
            var outcomeColor = OutcomeColor(latest.Outcome);
            var prev = GUI.color;
            GUI.color = outcomeColor;
            EditorGUILayout.LabelField(OutcomeLabel(latest.Outcome), EditorStyles.boldLabel);
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(latest.MutationError))
            {
                EditorGUILayout.HelpBox($"Mutation error: {latest.MutationError}", MessageType.Error);
            }

            BridgeGUIUtilities.HorizontalLine(2, 4);

            EditorGUILayout.LabelField(
                new GUIContent("Delta",
                    "Change in console errors/warnings between the pre-mutation checkpoint and the post-mutation validate. 'new' = introduced by the call; 'resolved' = fixed by it."),
                new GUIContent($"new errors: {latest.NewErrors}    new warnings: {latest.NewWarnings}    resolved errors: {latest.ResolvedErrors}    resolved warnings: {latest.ResolvedWarnings}"));

            EditorGUILayout.LabelField(
                new GUIContent("Durations (ms)",
                    "Time spent in each gate phase: snapshotting the error state (checkpoint), re-validating after the mutation (validation), and the total."),
                new GUIContent($"checkpoint: {latest.CheckpointDurationMs}    validation: {latest.ValidationDurationMs}    total: {latest.TotalGateDurationMs}"));

            if (latest.CategoriesRun != null && latest.CategoriesRun.Length > 0)
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Categories", "Verify rule categories that ran during the post-mutation validate pass."),
                    new GUIContent(string.Join(", ", latest.CategoriesRun)));
            }
            else
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Categories", "Verify rule categories that ran during the post-mutation validate pass."),
                    new GUIContent("(none — gate skipped or off)"));
            }

            if (latest.AgentNextSteps != null && latest.AgentNextSteps.Length > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Next steps", EditorStyles.miniBoldLabel);
                foreach (var t in latest.AgentNextSteps)
                {
                    EditorGUILayout.LabelField($"  • {t}", EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.LabelField("Captured", latest.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), EditorStyles.miniLabel);

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                BridgeGateRunHistory.Clear();
            }
            EditorGUILayout.LabelField("In-memory only — not persisted to disk in v1.", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private static string OutcomeLabel(GateOutcome outcome)
        {
            return outcome switch
            {
                GateOutcome.Passed => "Passed",
                GateOutcome.Failed => "Failed",
                GateOutcome.Warned => "Warned",
                GateOutcome.Skipped => "Skipped (no gate ran)",
                // T5.3 — mutation committed but validate scan could not run.
                GateOutcome.ValidateScanFailed => "Validate scan failed",
                _ => outcome.ToString()
            };
        }

        private static Color OutcomeColor(GateOutcome outcome)
        {
            return outcome switch
            {
                GateOutcome.Passed => new Color(0.6f, 0.9f, 0.6f),
                GateOutcome.Warned => new Color(1f, 0.9f, 0.4f),
                GateOutcome.Failed => new Color(1f, 0.5f, 0.5f),
                // T5.3 — same warning-tier color as Warned: the mutation is in
                // place but needs a manual health check.
                GateOutcome.ValidateScanFailed => new Color(1f, 0.9f, 0.4f),
                _ => new Color(0.7f, 0.7f, 0.7f)
            };
        }

        private void DrawGateCheckpointHistorySection()
        {
            EditorGUILayout.Space(6);
            _gateCheckpointFoldout = EditorGUILayout.Foldout(_gateCheckpointFoldout, "Checkpoint history (in-memory ring buffer)", true);
            if (!_gateCheckpointFoldout) return;

            EditorGUILayout.HelpBox(
                "Session-scoped ring buffer (capacity " + BridgeGateRunHistory.Capacity + "). " +
                "Populated by gate runs and by the `unity_open_mcp_checkpoint_create` tool. " +
                "In-memory only — no on-disk persistence in v1.",
                MessageType.None);

            // Item C — let the operator reclaim the session checkpoint memory
            // explicitly. Checkpoints are not persisted, so this touches nothing
            // on disk; gate-run history (BridgeGateRunHistory) is left intact.
            // Two-click confirm mirrors the listener stop pattern so an
            // accidental click can't drop a baseline an agent is still using.
            var count = CheckpointStore.Count;
            if (count > 0)
            {
                var pending = _checkpointClearPending;
                var label = pending ? "Confirm clear" : "Clear history";
                var tooltip = pending
                    ? "Click again to confirm: removes all session checkpoints from the in-memory ring buffer."
                    : "Removes all session checkpoints from the in-memory ring buffer. " +
                      "Checkpoints are not persisted to disk, so nothing on disk is affected. " +
                      "Gate-run history is left intact. Re-capture with the unity_open_mcp_checkpoint_create tool.";
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(label, tooltip), EditorStyles.miniButton, GUILayout.Width(120)))
                {
                    if (pending)
                    {
                        CheckpointStore.Clear();
                        _checkpointClearPending = false;
                        Repaint();
                    }
                    else
                    {
                        _checkpointClearPending = true;
                        Repaint();
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                _checkpointClearPending = false;
            }
            if (count == 0)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No checkpoints captured in this session yet.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            _gateCheckpointScroll = EditorGUILayout.BeginScrollView(_gateCheckpointScroll, GUILayout.MinHeight(80));
            var recent = CheckpointStore.Recent;
            // Display most-recent first for readability.
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var entry = recent[i];
                if (entry == null) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(entry.CheckpointId ?? "(no id)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Captured", entry.Timestamp ?? "(no timestamp)", EditorStyles.miniLabel);
                if (entry.Paths != null && entry.Paths.Length > 0)
                {
                    EditorGUILayout.LabelField("Paths", string.Join(", ", entry.Paths), EditorStyles.wordWrappedMiniLabel);
                }
                if (entry.Categories != null && entry.Categories.Length > 0)
                {
                    EditorGUILayout.LabelField("Categories", string.Join(", ", entry.Categories), EditorStyles.miniLabel);
                }
                if (entry.Fingerprint?.Fingerprints != null)
                {
                    int totalErrors = 0, totalWarnings = 0;
                    foreach (var fp in entry.Fingerprint.Fingerprints.Values)
                    {
                        if (fp == null) continue;
                        totalErrors += fp.Errors;
                        totalWarnings += fp.Warnings;
                    }
                    EditorGUILayout.LabelField("Fingerprint", $"errors: {totalErrors}    warnings: {totalWarnings}", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawGateManualValidateSection()
        {
            EditorGUILayout.Space(6);
            _gateManualFoldout = EditorGUILayout.Foldout(_gateManualFoldout, "Manual validate (scoped, non-mutating)", true);
            if (!_gateManualFoldout) return;

            EditorGUILayout.HelpBox(
                "Run a scoped verify pass without dispatching through the MCP server. " +
                "Default source is the current Project window selection; the text area accepts " +
                "comma/newline separated `Assets/...` paths. No mutation occurs, no checkpoint " +
                "is created, and gate defaults are not consulted.",
                MessageType.None);

            var selection = BridgeManualVerifyRunner.GetSelectionAssetPaths();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Selection paths", GUILayout.Width(110));
            EditorGUILayout.LabelField(selection.Length == 0 ? "(no assets selected)" : string.Join(", ", selection), EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Custom paths (optional, one per line)");
            _manualValidateInput = EditorGUILayout.TextArea(_manualValidateInput ?? "", GUILayout.MinHeight(48));

            var customPaths = BridgeManualVerifyRunner.ParsePathList(_manualValidateInput);
            var effectivePaths = selection.Length > 0 ? selection : customPaths;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_validateInFlight);
            if (GUILayout.Button(_validateInFlight ? "Validating…" : "Validate selection / paths", GUILayout.Width(220)))
            {
                _lastManualResult = BridgeManualVerifyRunner.Run(effectivePaths);
                Repaint();
            }
            if (GUILayout.Button("Use selection", GUILayout.Width(110)))
            {
                _manualValidateInput = "";
            }
            if (GUILayout.Button("Clear result", EditorStyles.miniButton, GUILayout.Width(90)))
            {
                _lastManualResult = null;
                _manualAssetFoldoutExpanded.Clear();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (effectivePaths.Length == 0)
            {
                EditorGUILayout.HelpBox("Select assets in the Project window or enter paths above to validate.", MessageType.None);
            }

            DrawGateManualResult();
        }

        private void DrawGateManualResult()
        {
            var result = _lastManualResult;
            if (result == null)
            {
                BridgeGUIUtilities.DrawLabelAtCenterHorizontally(
                    "No manual validate run yet.",
                    new Color(0.7f, 0.7f, 0.7f));
                return;
            }

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                EditorGUILayout.HelpBox($"Validate run failed: {result.ErrorMessage}", MessageType.Error);
                return;
            }

            if (!result.Ran)
            {
                EditorGUILayout.HelpBox("No paths supplied — provide selection or custom paths to validate.", MessageType.None);
                return;
            }

            var passColor = result.TotalErrors == 0
                ? new Color(0.6f, 0.9f, 0.6f)
                : new Color(1f, 0.5f, 0.5f);
            var prev = GUI.color;
            GUI.color = passColor;
            EditorGUILayout.LabelField(
                result.TotalErrors == 0 ? "PASS  " : "FAIL  ",
                EditorStyles.boldLabel);
            GUI.color = prev;

            EditorGUILayout.LabelField(
                $"paths: {result.InputPaths.Length}    assets with issues: {result.TotalAssets}    " +
                $"errors: {result.TotalErrors}    warnings: {result.TotalWarnings}    duration: {result.DurationMs} ms");

            if (result.CategoriesRun != null && result.CategoriesRun.Length > 0)
                EditorGUILayout.LabelField("Categories run", string.Join(", ", result.CategoriesRun), EditorStyles.miniLabel);

            if (result.Groups == null || result.Groups.Count == 0)
            {
                EditorGUILayout.HelpBox("No issues detected for the supplied paths.", MessageType.Info);
                return;
            }

            _gateManualResultScroll = EditorGUILayout.BeginScrollView(_gateManualResultScroll, GUILayout.MinHeight(120));
            foreach (var group in result.Groups)
            {
                if (group == null) continue;
                DrawManualAssetGroup(group);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawManualAssetGroup(BridgeManualAssetGroup group)
        {
            var expanded = _manualAssetFoldoutExpanded.Contains(group.AssetPath);
            var header = $"{group.AssetPath}    errors: {group.ErrorCount}    warnings: {group.WarningCount}";

            Color headerColor;
            if (group.ErrorCount > 0) headerColor = new Color(1f, 0.55f, 0.55f);
            else if (group.WarningCount > 0) headerColor = new Color(1f, 0.9f, 0.4f);
            else headerColor = new Color(0.7f, 0.85f, 1f);

            var prev = GUI.color;
            GUI.color = headerColor;
            var nowExpanded = EditorGUILayout.Foldout(expanded, header, true);
            GUI.color = prev;

            if (nowExpanded != expanded)
            {
                if (nowExpanded) _manualAssetFoldoutExpanded.Add(group.AssetPath);
                else _manualAssetFoldoutExpanded.Remove(group.AssetPath);
            }

            if (nowExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (var issue in group.Issues)
                {
                    if (issue == null) continue;
                    Color sevColor = issue.Severity == "error"
                        ? new Color(1f, 0.55f, 0.55f)
                        : new Color(1f, 0.9f, 0.4f);
                    var sprev = GUI.color;
                    GUI.color = sevColor;
                    EditorGUILayout.LabelField(
                        $"[{issue.Severity.ToUpperInvariant()}] {issue.RuleId} / {issue.IssueCode}",
                        EditorStyles.boldLabel);
                    GUI.color = sprev;
                    if (!string.IsNullOrEmpty(issue.Description))
                        EditorGUILayout.LabelField(issue.Description, EditorStyles.wordWrappedMiniLabel);
                }
                EditorGUI.indentLevel--;
                BridgeGUIUtilities.HorizontalLine(1, 2);
            }
        }

        // ---------- Activity tab (M4.5-10) ----------
    }
}
