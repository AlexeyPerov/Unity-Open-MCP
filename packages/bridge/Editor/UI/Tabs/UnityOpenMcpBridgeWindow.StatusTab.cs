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
        private void DrawStatusTab()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bridge runtime", EditorStyles.boldLabel);

            var running = BridgeHttpServer.IsRunning;
            var statusColor = BridgeGUIUtilities.GetStateColor(running, BridgeSession.IsCompiling);
            var statusText = running
                ? (BridgeSession.IsCompiling ? "Running (compiling)" : "Running")
                : "Stopped";

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Listener", TooltipListener, 120);
            var prev = GUI.color;
            GUI.color = statusColor;
            GUILayout.Label(statusText, EditorStyles.boldLabel);
            GUI.color = prev;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Bind URL", TooltipBindUrl, 120);
            EditorGUILayout.SelectableLabel($"http://{BindAddress}:{BridgeHttpServer.Port}/", EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel("Mode", "Editor session mode reported to MCP clients (live = a real Editor process).", 120);
            EditorGUILayout.LabelField(BridgeSession.Mode);
            EditorGUILayout.EndHorizontal();

            BridgeGUIUtilities.HorizontalLine(8, 6);
            EditorGUILayout.LabelField("Controls", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Start / Stop control the local HTTP listener that MCP clients connect to.\n" +
                "• Start — launch the listener so agents can call tools. Auto-starts on Editor load unless disabled in Settings.\n" +
                "• Stop — shut the listener down. Any connected agent loses MCP connectivity until you Start again (two-click confirm).",
                MessageType.None);
            DrawRuntimeControls();
            DrawLocalPing();

            if (!string.IsNullOrEmpty(_lastPingResult))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_lastPingResult, _lastPingMessageType);
            }

            DrawPortInUseRecoveryIfNeeded();

            BridgeGUIUtilities.HorizontalLine(8, 6);

            DrawMcpConnectivitySection();

            BridgeGUIUtilities.HorizontalLine(8, 6);

            DrawConfigureClientSection();

            BridgeGUIUtilities.HorizontalLine(8, 6);

            // Editor state + Project are diagnostic fields — collapsed by default.
            _statusEditorStateFoldout = EditorGUILayout.Foldout(
                _statusEditorStateFoldout, "Editor state (diagnostics)", true);
            if (_statusEditorStateFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Compiling", "Whether Unity is currently recompiling scripts. During compile, tool dispatch is paused.", 120);
                var compileColor = BridgeSession.IsCompiling ? Color.yellow : new Color(0.6f, 0.9f, 0.6f);
                var prevC = GUI.color;
                GUI.color = compileColor;
                GUILayout.Label(BridgeSession.IsCompiling ? "Yes" : "No", EditorStyles.boldLabel);
                GUI.color = prevC;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Play mode", "Whether the Editor is in Play mode. Some tools behave differently (or are gated) while playing.", 120);
                var playColor = BridgeSession.IsPlaying ? new Color(0.5f, 0.8f, 1f) : new Color(0.7f, 0.7f, 0.7f);
                prevC = GUI.color;
                GUI.color = playColor;
                GUILayout.Label(BridgeSession.IsPlaying ? "Playing" : "Edit", EditorStyles.boldLabel);
                GUI.color = prevC;
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            _statusProjectFoldout = EditorGUILayout.Foldout(
                _statusProjectFoldout, "Project (diagnostics)", true);
            if (_statusProjectFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Project path", "Absolute path to the Unity project root (the folder containing Assets/).", 120);
                EditorGUILayout.SelectableLabel(BridgeSession.ProjectPath ?? "(unknown)", EditorStyles.textField);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Unity version", "Unity Editor version this bridge is running in.", 120);
                EditorGUILayout.LabelField(BridgeSession.UnityVersion ?? "(unknown)");
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Bridge version", "Version of the bridge package loaded in this Editor.", 120);
                EditorGUILayout.LabelField(BridgeSession.BridgeVersion);
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }
        }

        private void DrawRuntimeControls()
        {
            var running = BridgeHttpServer.IsRunning;
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(running);
            if (GUILayout.Button(new GUIContent("Start", "Launch the local HTTP listener so MCP clients (agents) can connect and call tools."), GUILayout.Width(110)))
            {
                try
                {
                    BridgeHttpServer.Start();
                    if (!BridgeHttpServer.IsRunning && !string.IsNullOrEmpty(BridgeHttpServer.LastStartError))
                    {
                        _lastPingResult = $"Start failed: {BridgeHttpServer.LastStartError}";
                        _lastPingMessageType = MessageType.Error;
                    }
                }
                catch (Exception e)
                {
                    _lastPingResult = $"Start failed: {e.Message}";
                    _lastPingMessageType = MessageType.Error;
                }
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!running);
            var buttonLabel = _stopConfirmPending ? "Confirm Stop" : "Stop";
            var buttonTooltip = _stopConfirmPending
                ? "Click again to confirm: stops the listener and drops MCP connectivity for any active agent."
                : "Shut the listener down. Requires a two-click confirm because it disconnects any active agent.";
            if (GUILayout.Button(new GUIContent(buttonLabel, buttonTooltip), GUILayout.Width(110)))
            {
                if (!_stopConfirmPending)
                {
                    _stopConfirmPending = true;
                    _stopConfirmDeadline = EditorApplication.timeSinceStartup + 5.0;
                    _lastPingResult = "Press 'Confirm Stop' within 5 seconds to stop the bridge listener.\n" +
                                      "Warning: stopping the listener will drop MCP connectivity for any active agent.";
                    _lastPingMessageType = MessageType.Warning;
                    Repaint();
                }
                else
                {
                    try
                    {
                        BridgeHttpServer.Stop();
                        _lastPingResult = "Bridge listener stopped. MCP clients will no longer reach the bridge until you Start again.";
                        _lastPingMessageType = MessageType.Info;
                    }
                    catch (Exception e)
                    {
                        _lastPingResult = $"Stop failed: {e.Message}";
                        _lastPingMessageType = MessageType.Error;
                    }
                    finally
                    {
                        _stopConfirmPending = false;
                    }
                }
            }
            EditorGUI.EndDisabledGroup();

            if (_stopConfirmPending)
            {
                var prev = GUI.color;
                GUI.color = Color.yellow;
                GUILayout.Label($"Confirm within {Mathf.Max(0f, (float)(_stopConfirmDeadline - EditorApplication.timeSinceStartup)):0.0}s");
                GUI.color = prev;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPortInUseRecoveryIfNeeded()
        {
            if (BridgeHttpServer.IsRunning)
                return;

            var startError = BridgeHttpServer.LastStartError;
            if (!BridgeStartRecovery.IsPortInUseError(startError))
                return;

            EditorGUILayout.Space(4);
            var projectPath = BridgeSession.ProjectPath ?? BridgeHttpServer.GetProjectPathForPort();
            EditorGUILayout.HelpBox(
                BridgeStartRecovery.FormatPortInUseRecovery(projectPath, BridgeHttpServer.Port),
                MessageType.Info);
        }

        private void DrawLocalPing()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_pingInFlight || !BridgeHttpServer.IsRunning);
            if (GUILayout.Button(new GUIContent("Ping", TooltipPing), GUILayout.Width(110)))
            {
                _ = RunLocalPingAsync();
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.Label("Local ping verifies the listener responds on the configured bind URL.", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private async Task RunLocalPingAsync()
        {
            _pingInFlight = true;
            try
            {
                var url = $"http://{BindAddress}:{BridgeHttpServer.Port}/ping";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var response = await SharedHttpClient.GetAsync(url, cts.Token).ConfigureAwait(true);
                sw.Stop();
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                _lastPingResult = $"HTTP {(int)response.StatusCode} in {sw.ElapsedMilliseconds} ms\n{body}";
                _lastPingMessageType = response.IsSuccessStatusCode ? MessageType.Info : MessageType.Warning;
            }
            catch (Exception e)
            {
                _lastPingResult = $"Ping failed: {e.Message}";
                _lastPingMessageType = MessageType.Error;
            }
            finally
            {
                _pingInFlight = false;
                Repaint();
            }
        }

        // ---------- MCP connectivity panel (read-only diagnostics) ----------
        //
        // The bridge runs inside Unity and has no handle on the external Node MCP
        // server process (it is spawned by the MCP client via `npx`). This panel
        // surfaces the same signals the MCP server uses to discover this bridge
        // (derived port + instance lock), plus an opt-in probe that shells out to
        // `npx unity-open-mcp status` so the operator can self-diagnose end-to-end
        // connectivity without leaving Unity. It is strictly read-only — there is
        // no start/stop of the MCP server here (that's the MCP client's job).

        private void DrawMcpConnectivitySection()
        {
            _mcpConnectivityFoldout = EditorGUILayout.Foldout(
                _mcpConnectivityFoldout, "MCP connectivity", true);
            if (!_mcpConnectivityFoldout) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "The bridge runs inside Unity; the MCP server is a separate Node process the MCP client " +
                "(Cursor/Claude) spawns via `npx`. This panel shows the discovery signals the MCP server " +
                "uses to reach this bridge, and lets you probe end-to-end connectivity.",
                MessageType.None);

            var projectPath = BridgeSession.ProjectPath;

            // Derived port — what the MCP server computes for this project.
            var hasProject = !string.IsNullOrEmpty(projectPath);
            var derivedPort = hasProject ? InstancePortResolver.ComputePort(projectPath) : 0;
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Derived port",
                "Port the MCP server derives for this project (20000 + sha256(path) % 10000). " +
                "UNITY_OPEN_MCP_BRIDGE_PORT env / -UNITY_OPEN_MCP_BRIDGE_PORT CLI override this on the server side.",
                120);
            EditorGUILayout.SelectableLabel(
                hasProject ? derivedPort.ToString() : "(unknown project path)",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            // Instance lock — the discovery/heartbeat file the MCP server reads.
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Instance lock",
                "Lock + heartbeat file at ~/.unity-open-mcp/instances/<hash>.json. The MCP server reads it " +
                "to find this bridge's port + auth token without an HTTP round-trip.",
                120);
            EditorGUILayout.SelectableLabel(
                hasProject ? InstancePortResolver.LockPath(projectPath) : "(unknown project path)",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            // Live lock state parsed from the on-disk file.
            var lockAcquired = BridgeInstanceLock.IsAcquired;
            var lockJson = BridgeInstanceLock.ReadCurrentJson();
            var snap = BridgeInstanceLock.TryParseSnapshot(lockJson);
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Lock state",
                "Whether this bridge has acquired the instance lock and the last heartbeat written.",
                120);
            var prevColor = GUI.color;
            GUI.color = lockAcquired ? new Color(0.6f, 0.9f, 0.6f) : new Color(1f, 0.5f, 0.5f);
            GUILayout.Label(lockAcquired ? "acquired" : "not acquired", EditorStyles.boldLabel);
            GUI.color = prevColor;
            EditorGUILayout.EndHorizontal();

            if (snap.Valid)
            {
                if (!string.IsNullOrEmpty(snap.State))
                {
                    EditorGUILayout.BeginHorizontal();
                    BridgeGUIUtilities.FieldLabel("Heartbeat state", "Last editor state written to the lock.", 120);
                    EditorGUILayout.LabelField(snap.State);
                    EditorGUILayout.EndHorizontal();
                }
                if (!string.IsNullOrEmpty(snap.HeartbeatAt))
                {
                    EditorGUILayout.BeginHorizontal();
                    BridgeGUIUtilities.FieldLabel("Heartbeat at", "Timestamp of the last heartbeat write (UTC).", 120);
                    EditorGUILayout.LabelField(snap.HeartbeatAt);
                    EditorGUILayout.EndHorizontal();
                }
            }

            // Expected launch command for the MCP client — what the operator
            // (or their MCP client config) should run to connect.
            EditorGUILayout.BeginHorizontal();
            BridgeGUIUtilities.FieldLabel(
                "Client launch",
                "The command the MCP client runs to start the MCP server for this project. " +
                "The MCP server auto-discovers the bridge port via the instance lock.",
                120);
            EditorGUILayout.SelectableLabel(
                hasProject ? $"UNITY_PROJECT_PATH=\"{projectPath}\" npx -y unity-open-mcp@latest" : "(unknown project path)",
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            // Opt-in probe. Shells out to the MCP server's own status command.
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_mcpProbeInFlight || !hasProject);
            if (GUILayout.Button(
                new GUIContent(_mcpProbeInFlight ? "Probing…" : "Probe MCP server",
                    "Runs `npx -y unity-open-mcp@latest status` to check end-to-end discovery + bridge reachability. " +
                    "Requires Node/npx on PATH. Does not start a long-running server."),
                GUILayout.Width(160)))
            {
                _ = RunMcpProbeAsync(projectPath);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_mcpProbeResult))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_mcpProbeResult, _mcpProbeMessageType);
            }

            EditorGUI.indentLevel--;
        }

        // ---------- Configure AI client (M27 Plan 5) ----------
        //
        // Generates the MCP client config snippet for the selected client
        // against the current project, so an operator can copy it without
        // leaving Unity. The Hub wizard (Rust) remains the canonical one-click
        // writer; this panel mirrors the envelope shapes so the bytes match.
        // See McpClientCatalog for the catalog + envelope builders.

        private void DrawConfigureClientSection()
        {
            _configureClientFoldout = EditorGUILayout.Foldout(
                _configureClientFoldout, "Configure AI client", true);
            if (!_configureClientFoldout) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "Copy the MCP client config snippet for this project into your AI " +
                "client's config file. The Hub wizard (Unity Hub Pro) writes the file " +
                "for you in one click; this panel is for configuring a client without " +
                "leaving Unity. The launch command is `npx -y unity-open-mcp@latest` " +
                "(or `node <root>/mcp-server/dist/index.js` for a local checkout).",
                MessageType.None);

            var projectPath = BridgeSession.ProjectPath;
            var hasProject = !string.IsNullOrEmpty(projectPath);
            if (!hasProject)
            {
                EditorGUILayout.HelpBox("Open a Unity project to configure a client.", MessageType.Warning);
                EditorGUI.indentLevel--;
                return;
            }

            // Clamp the index so a catalog change never produces an out-of-range.
            var catalog = UnityOpenMcpBridge.Config.McpClientCatalog.Clients;
            if (_configureClientIndex < 0 || _configureClientIndex >= catalog.Length)
            {
                _configureClientIndex = 0;
            }
            var displayNames = new string[catalog.Length];
            for (var i = 0; i < catalog.Length; i++) displayNames[i] = catalog[i].DisplayName;
            _configureClientIndex = EditorGUILayout.Popup("Client", _configureClientIndex, displayNames);
            var client = catalog[_configureClientIndex];

            // Resolve the launch command + port for the snippet.
            var port = InstancePortResolver.ComputePort(projectPath);
            // The in-Unity panel defaults to the npm launch (no toolkit root
            // required); a local checkout should be configured via the Hub
            // wizard where the toolkit root is validated.
            var command = "npx";
            var args = new[] { "-y", "unity-open-mcp@latest" };

            // Regenerate the snippet + target path on every repaint so it
            // always reflects the current client + project.
            _configureClientSnippet =
                UnityOpenMcpBridge.Config.McpClientCatalog.BuildSnippet(client, projectPath, port, command, args);
            _configureClientTargetPath =
                UnityOpenMcpBridge.Config.McpClientCatalog.ResolveDisplayPath(client, projectPath) ?? "";

            // Configured-state check: read the target file (when file-backed)
            // and look for our server key under the client's merge key.
            _configureClientConfigured = ComputeClientConfigured(client, projectPath);

            if (client.IsFileBacked && !string.IsNullOrEmpty(_configureClientTargetPath))
            {
                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel(
                    "Target",
                    "Config file this snippet should be merged into. The Hub wizard writes " +
                    "this file for you; from here, copy the snippet and paste it under the " +
                    "merge key shown in the snippet.",
                    120);
                EditorGUILayout.SelectableLabel(
                    _configureClientTargetPath,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                BridgeGUIUtilities.FieldLabel("Configured", "Whether the target file already has a unity-open-mcp entry.", 120);
                var prev = GUI.color;
                GUI.color = _configureClientConfigured ? new Color(0.6f, 0.9f, 0.6f) : new Color(1f, 0.85f, 0.5f);
                GUILayout.Label(_configureClientConfigured ? "yes" : "no", EditorStyles.boldLabel);
                GUI.color = prev;
                EditorGUILayout.EndHorizontal();
            }
            else if (client.IsCliOnly)
            {
                EditorGUILayout.HelpBox(
                    "Claude Code is CLI-only — copy the `claude mcp add` command below and run it " +
                    "in a terminal (no config file is written).",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Manual / custom — copy the snippet below and paste it into your client's " +
                    "config under the appropriate merge key.",
                    MessageType.Info);
            }

            EditorGUILayout.LabelField("Snippet", EditorStyles.miniBoldLabel);
            EditorGUILayout.SelectableLabel(
                _configureClientSnippet,
                EditorStyles.textArea,
                GUILayout.ExpandHeight(true),
                GUILayout.MinHeight(110));

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy to clipboard", GUILayout.Width(160)))
            {
                GUIUtility.systemCopyBuffer = _configureClientSnippet;
            }
            if (GUILayout.Button("Open in Unity Hub Pro", GUILayout.Width(180)))
            {
                Application.OpenURL("https://github.com/AlexeyPerov/Unity-AI-Hub");
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "Unity Hub Pro writes this config in one click with merge-safe backups, " +
                "scope toggles, and a full 6-step onboarding wizard.",
                MessageType.None);

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Best-effort <c>IsConfigured</c> heuristic: returns <c>true</c> when the
        /// client's target file exists and contains a <c>unity-open-mcp</c> entry
        /// under the client's merge key (or, for TOML/Codex, the
        /// <c>[mcp_servers.unity-open-mcp]</c> table). File-read failures default
        /// to <c>false</c> so a missing or unreadable file never falsely reports
        /// as configured.
        /// </summary>
        private static bool ComputeClientConfigured(UnityOpenMcpBridge.Config.McpClientCatalog.ClientEntry client, string projectPath)
        {
            var path = UnityOpenMcpBridge.Config.McpClientCatalog.ResolveDisplayPath(client, projectPath);
            if (string.IsNullOrEmpty(path)) return false;
            if (!File.Exists(path)) return false;
            try
            {
                var body = File.ReadAllText(path);
                if (client.EnvelopeKind == UnityOpenMcpBridge.Config.McpClientCatalog.Envelope.Codex)
                {
                    return body.Contains("[mcp_servers.unity-open-mcp]");
                }
                // JSON clients: a substring match on the server key is a
                // good-enough signal (the wizard writes the key verbatim).
                return body.Contains("\"unity-open-mcp\"");
            }
            catch
            {
                return false;
            }
        }

        // Probe the MCP server end-to-end by shelling out to its status command.
        // Runs the process on a background thread (npx can take several seconds
        // to resolve the package) and marshals the result back to the UI thread
        // for display. Failures (npx missing, non-zero exit, timeout) are
        // reported clearly and never fatal.
        private async Task RunMcpProbeAsync(string projectPath)
        {
            _mcpProbeInFlight = true;
            _mcpProbeResult = "Probing… (running `npx -y unity-open-mcp@latest status`)";
            _mcpProbeMessageType = MessageType.Info;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "npx",
                    Arguments = $"-y unity-open-mcp@latest status --project \"{projectPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                var stdout = await ReadProcessAsync(psi, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                var trimmed = (stdout.Stdout ?? "").Trim();
                var trimmedErr = (stdout.Stderr ?? "").Trim();
                var exit = stdout.ExitCode;

                if (exit == 0)
                {
                    _mcpProbeResult = string.IsNullOrEmpty(trimmed)
                        ? "Probe completed (exit 0, no output)."
                        : trimmed;
                    _mcpProbeMessageType = MessageType.Info;
                }
                else
                {
                    var msg = $"Probe exited with code {exit}.";
                    if (!string.IsNullOrEmpty(trimmedErr)) msg += $"\n{trimmedErr}";
                    if (!string.IsNullOrEmpty(trimmed)) msg += $"\n--- stdout ---\n{trimmed}";
                    _mcpProbeResult = msg;
                    _mcpProbeMessageType = MessageType.Warning;
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                // Most common: `npx` not found on PATH.
                _mcpProbeResult =
                    $"Could not start `npx` ({e.Message}). Install Node.js (includes npx) or add it to PATH " +
                    "to use the probe. The bridge itself does not require Node.";
                _mcpProbeMessageType = MessageType.Warning;
            }
            catch (Exception e)
            {
                _mcpProbeResult = $"Probe failed: {e.Message}";
                _mcpProbeMessageType = MessageType.Error;
            }
            finally
            {
                _mcpProbeInFlight = false;
                Repaint();
            }
        }

        // Run a process to completion off the main thread, with a hard timeout.
        // Returns merged stdout/stderr + exit code. Captures-but-discards the
        // process on timeout so it can't outlive the probe.
        private static async Task<ProcessOutput> ReadProcessAsync(
            System.Diagnostics.ProcessStartInfo psi, TimeSpan timeout)
        {
            return await Task.Run(() =>
            {
                using var p = new System.Diagnostics.Process { StartInfo = psi };
                var stdoutBuilder = new System.Text.StringBuilder();
                var stderrBuilder = new System.Text.StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

                if (!p.Start()) return new ProcessOutput("", "Failed to start process.", -1);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch { }
                    return new ProcessOutput(stdoutBuilder.ToString(), "(timed out)", -1);
                }
                // WaitForExit(int) can return before async streams flush; this
                // no-arg overload blocks until the readers close.
                p.WaitForExit();
                return new ProcessOutput(stdoutBuilder.ToString(), stderrBuilder.ToString(), p.ExitCode);
            }).ConfigureAwait(true);
        }

        private readonly struct ProcessOutput
        {
            public readonly string Stdout;
            public readonly string Stderr;
            public readonly int ExitCode;
            public ProcessOutput(string stdout, string stderr, int exitCode)
            {
                Stdout = stdout;
                Stderr = stderr;
                ExitCode = exitCode;
            }
        }
    }
}
