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
        private Vector2 _infoTabScroll;

        // Single source of truth for the doc links surfaced in the Info tab.
        // Each entry is (label, relative path under docs/, tooltip). The repo
        // URL + branch prefix is prepended so links open the rendered markdown
        // on GitHub.
        private static readonly (string Label, string Path, string Tooltip)[] DocLinks =
        {
            ("README", "README.md", "Project overview, feature set, and quick links."),
            ("Wizard setup", "docs/wizard-setup.md", "Recommended onboarding flow via Unity Hub Pro."),
            ("Manual setup", "docs/manual-setup.md", "Direct MCP setup and client config snippets."),
            ("Troubleshooting", "docs/troubleshooting.md", "Bridge start failures, zombie listeners, and connectivity recovery."),
            ("Development setup", "docs/development-setup.md", "Local checkout, contributor and maintainer workflows."),
            ("Architecture", "docs/architecture.md", "Repository structure and cross-package boundaries."),
            ("Bridge HTTP API", "docs/api/bridge-http.md", "Bridge endpoints, envelopes, /ping, and remote bind."),
            ("MCP tools API", "docs/api/mcp-tools.md", "Tool catalog, route policy, and gate behavior."),
            ("MCP resources API", "docs/api/resources.md", "Resource URIs and payloads."),
            ("Extensions", "docs/extensions.md", "Domain catalog and activation (embedded tools + community packs)."),
            ("Skills", "docs/skills.md", "Agent playbooks shipped into a project."),
            ("Code conventions", "docs/code-conventions.md", "Non-obvious C# decisions (instance IDs, namespaces)."),
        };

        private void DrawInfoTab()
        {
            _infoTabScroll = EditorGUILayout.BeginScrollView(_infoTabScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Unity Open MCP Bridge", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unity Open MCP is an open MCP server for Unity — it exposes the Editor to " +
                "MCP-compatible AI clients (Claude, Cursor, …) over a local HTTP bridge so an " +
                "agent can drive scene editing, asset work, builds, and validation.\n\n" +
                "The links below open the latest documentation on GitHub.",
                MessageType.None);

            DrawInfoSection("Documentation", DocLinks, RepoUrl);

            BridgeGUIUtilities.HorizontalLine(2, 8);

            DrawInfoSection("Project", new[]
            {
                ("GitHub repository", "", "Source code, issues, and releases."),
                ("Issues", "issues", "Bug reports and feature requests."),
                ("Releases", "releases", "Version history and release notes."),
            }, RepoUrl);

            BridgeGUIUtilities.HorizontalLine(2, 8);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Quick references", EditorStyles.miniBoldLabel);
            DrawInfoLinkRow("Local bind URL",
                $"http://{BindAddress}:{BridgeHttpServer.Port}/",
                "The listener address MCP clients connect to (see Status tab).");
            DrawInfoLinkRow("Settings file",
                BridgeProjectSettings.SettingsPath ?? "(no project root)",
                "Per-project runtime settings (.unity-open-mcp/settings.json). Persistent; edit by hand or via the Settings tab.");
            DrawInfoLinkRow("Instance lock",
                "~/.unity-open-mcp/instances/<project-hash>.json",
                "Per-project lock file used for discovery + heartbeat by the MCP server. Regenerated each session.");
            DrawInfoLinkRow("Audit log",
                "~/.unity-open-mcp/audit/",
                "Optional on-disk audit of gate runs and deny-list refusals (enable in Settings).");

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(
                $"Bridge {BridgeSession.BridgeVersion}  •  Unity {BridgeSession.UnityVersion ?? "?"}  •  {BridgeSession.Mode} mode",
                EditorStyles.miniLabel);

            EditorGUILayout.EndScrollView();
        }

        // Renders a titled list of links. Each row: a label-style button that
        // opens the URL via Application.OpenURL, plus a tooltip. `urlPrefix`
        // is prepended to each entry's relative path (empty path ⇒ the prefix
        // itself, e.g. the repo root).
        private void DrawInfoSection(string title, IEnumerable<(string Label, string Path, string Tooltip)> links, string urlPrefix)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            foreach (var link in links)
            {
                var url = string.IsNullOrEmpty(link.Path) ? urlPrefix : $"{urlPrefix}/{link.Path}";
                DrawInfoLinkRow(link.Label, url, link.Tooltip);
            }
        }

        // Label column + selectable URL + an "Open" button that launches the
        // system browser. The URL is selectable so it can be copied.
        private void DrawInfoLinkRow(string label, string url, string tooltip)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(label, tooltip), GUILayout.Width(180));
            EditorGUILayout.SelectableLabel(url, EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button(new GUIContent("Open", "Open this link in your default browser."), GUILayout.Width(64)))
            {
                Application.OpenURL(url);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
