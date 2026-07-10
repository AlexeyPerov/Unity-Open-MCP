using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityOpenMcpBridge.Config
{
    /// <summary>
    /// In-Unity mirror of the Hub wizard's MCP client catalog (M27 Plan 5).
    ///
    /// The Hub wizard (Rust) is the canonical writer; this catalog exists so
    /// the bridge window can render a "Configure AI client" panel that
    /// generates the same JSON/TOML snippet an operator would get from the
    /// wizard Step 4, without leaving Unity. The bytes produced here match
    /// the Rust envelope builders for every Tier A client.
    ///
    /// Adding a client is a two-step change: add a row to <see cref="Clients"/>
    /// and implement its envelope in <see cref="BuildEntryFields"/>. The skill
    /// install path comes from <c>skills/client-paths.json</c> (single source
    /// of truth for the Hub + mcp-server); this catalog only owns the config
    /// envelope + target path.
    /// </summary>
    internal static class McpClientCatalog
    {
        /// <summary>
        /// One row per supported MCP client. Order is the display order in the
        /// bridge window dropdown. <see cref="Id"/> is the stable wire key
        /// matching the Hub Rust <c>McpClientId</c> camelCase serialization
        /// and the <c>skills/client-paths.json</c> <c>mcpClientMapping</c>.
        /// </summary>
        public static readonly ClientEntry[] Clients =
        {
            new ClientEntry("cursor", "Cursor", "mcpServers", Envelope.McpServersStdio, Scope.Global, "$HOME/.cursor/mcp.json"),
            new ClientEntry("cursor-project", "Cursor (project)", "mcpServers", Envelope.McpServersStdio, Scope.Project, ".cursor/mcp.json"),
            new ClientEntry("claudeDesktop", "Claude Desktop", "mcpServers", Envelope.McpServersStdio, Scope.Global, null),
            new ClientEntry("cline", "Cline (VS Code)", "mcpServers", Envelope.McpServersStdio, Scope.Global, null),
            new ClientEntry("gemini", "Gemini CLI", "mcpServers", Envelope.McpServersStdio, Scope.Project, ".gemini/settings.json"),
            new ClientEntry("githubCopilotCli", "GitHub Copilot CLI", "mcpServers", Envelope.GithubCopilotCli, Scope.Project, ".mcp.json"),
            new ClientEntry("kiloCode", "Kilo Code", "mcpServers", Envelope.McpServersStdio, Scope.Project, ".kilocode/mcp.json"),
            new ClientEntry("rider", "Rider (Junie)", "mcpServers", Envelope.McpServersStdio, Scope.Project, ".junie/mcp/mcp.json"),
            new ClientEntry("unityAi", "Unity AI", "mcpServers", Envelope.McpServersStdio, Scope.Project, "UserSettings/mcp.json"),
            new ClientEntry("zoocode", "ZooCode", "mcpServers", Envelope.McpServersStdio, Scope.Project, ".roo/mcp.json"),
            new ClientEntry("antigravity", "Antigravity", "mcpServers", Envelope.Antigravity, Scope.Global, "$HOME/.gemini/antigravity/mcp_config.json"),
            new ClientEntry("vscodeCopilot", "VS Code Copilot", "servers", Envelope.McpServersStdio, Scope.Project, ".vscode/mcp.json"),
            new ClientEntry("vsCopilot", "Visual Studio Copilot", "servers", Envelope.McpServersStdio, Scope.Project, ".vs/mcp.json"),
            new ClientEntry("opencodeGlobal", "OpenCode (global)", "mcp", Envelope.OpenCode, Scope.Global, "$HOME/.config/opencode/opencode.json"),
            new ClientEntry("opencodeProject", "OpenCode (project)", "mcp", Envelope.OpenCode, Scope.Project, "opencode.json"),
            new ClientEntry("zcodeGlobal", "ZCode (global)", "mcp.servers", Envelope.ZcodeStdio, Scope.Global, "$HOME/.zcode/cli/config.json"),
            new ClientEntry("zcodeProject", "ZCode (project)", "mcp.servers", Envelope.ZcodeStdio, Scope.Project, ".zcode/cli/config.json"),
            new ClientEntry("codex", "Codex", "mcp_servers", Envelope.Codex, Scope.Project, ".codex/config.toml"),
            new ClientEntry("claudeCode", "Claude Code (CLI)", "", Envelope.CliOnly, Scope.None, null),
            new ClientEntry("manual", "Manual / copy JSON", "", Envelope.Manual, Scope.None, null),
        };

        public const string ServerKey = "unity-open-mcp";

        public enum Scope { Global, Project, None }

        public enum Envelope
        {
            McpServersStdio,
            OpenCode,
            ZcodeStdio,
            GithubCopilotCli,
            Antigravity,
            Codex,
            CliOnly,
            Manual,
        }

        public readonly struct ClientEntry
        {
            public readonly string Id;
            public readonly string DisplayName;
            public readonly string MergeKey;
            public readonly Envelope EnvelopeKind;
            public readonly Scope ScopeKind;
            /// <summary>Relative or <c>$HOME</c>-prefixed path template;
            /// <c>null</c> for CLI/clipboard-only clients and for
            /// OS-specific global paths resolved by the Hub wizard.</summary>
            public readonly string PathTemplate;

            public ClientEntry(string id, string displayName, string mergeKey, Envelope envelope, Scope scope, string pathTemplate)
            {
                Id = id;
                DisplayName = displayName;
                MergeKey = mergeKey;
                EnvelopeKind = envelope;
                ScopeKind = scope;
                PathTemplate = pathTemplate;
            }

            public bool IsFileBacked => EnvelopeKind != Envelope.CliOnly && EnvelopeKind != Envelope.Manual;
            public bool IsCliOnly => EnvelopeKind == Envelope.CliOnly;
        }

        /// <summary>
        /// Build the config snippet (JSON or TOML) for a client against the
        /// given project + port + launch command. Mirrors the Hub Rust
        /// envelope builders so the bytes match the wizard's output.
        /// </summary>
        public static string BuildSnippet(ClientEntry client, string unityProjectPath, int bridgePort, string command, IReadOnlyList<string> args)
        {
            if (client.EnvelopeKind == Envelope.CliOnly)
            {
                return ClaudeMcpAddCommand(unityProjectPath, bridgePort, command, args);
            }
            if (client.EnvelopeKind == Envelope.Codex)
            {
                // Codex snippet is a full TOML table block.
                return CodexTomlEntry(command, args, unityProjectPath, bridgePort);
            }
            // Build the entry as a dictionary tree, then wrap it under the
            // merge key (handling nested keys like "mcp.servers") and
            // serialize once. Manual clients emit just the bare entry.
            var entryFields = BuildEntryFields(client, unityProjectPath, bridgePort, command, args);
            if (client.EnvelopeKind == Envelope.Manual || string.IsNullOrEmpty(client.MergeKey))
            {
                return SerializeJson(entryFields);
            }
            var segments = client.MergeKey.Split('.');
            return BuildNestedJson(segments, ServerKey, entryFields);
        }

        /// <summary>Build the entry as a dictionary tree (the leaf object).</summary>
        private static Dictionary<string, object> BuildEntryFields(ClientEntry client, string project, int port, string command, IReadOnlyList<string> args)
        {
            var env = EnvMap(project, port);
            var argsList = new List<object>();
            foreach (var a in args) argsList.Add(a);
            switch (client.EnvelopeKind)
            {
                case Envelope.McpServersStdio:
                    return new Dictionary<string, object>
                    {
                        { "type", "stdio" },
                        { "command", command },
                        { "args", argsList },
                        { "env", env },
                    };
                case Envelope.GithubCopilotCli:
                    return new Dictionary<string, object>
                    {
                        { "command", command },
                        { "args", argsList },
                        { "tools", new List<object> { "*" } },
                        { "env", env },
                    };
                case Envelope.Antigravity:
                    return new Dictionary<string, object>
                    {
                        { "disabled", false },
                        { "command", command },
                        { "args", argsList },
                        { "env", env },
                    };
                case Envelope.OpenCode:
                {
                    var cmdArray = new List<object> { command };
                    foreach (var a in args) cmdArray.Add(a);
                    return new Dictionary<string, object>
                    {
                        { "type", "local" },
                        { "command", cmdArray },
                        { "enabled", true },
                        { "environment", env },
                    };
                }
                case Envelope.ZcodeStdio:
                    return new Dictionary<string, object>
                    {
                        { "type", "stdio" },
                        { "command", command },
                        { "args", argsList },
                        { "env", env },
                    };
                default:
                    return new Dictionary<string, object>
                    {
                        { "command", command },
                        { "args", argsList },
                        { "env", env },
                    };
            }
        }

        /// <summary>
        /// Render the <c>claude mcp add</c> CLI command for the Claude Code
        /// client. Matches the Hub Rust <c>claude_mcp_add_command</c>.
        /// </summary>
        public static string ClaudeMcpAddCommand(string unityProjectPath, int bridgePort, string command, IReadOnlyList<string> args)
        {
            var invocation = new StringBuilder();
            invocation.Append(command);
            foreach (var a in args)
            {
                invocation.Append(' ').Append(a);
            }
            return string.Format(
                "claude mcp add {0} --env {1}={2} --env {3}={4} -- {5}",
                ServerKey,
                BridgeConstants.ProjectPathEnvVar, unityProjectPath,
                BridgeConstants.PortEnvVar, bridgePort,
                invocation);
        }

        /// <summary>
        /// Resolve the on-disk target path for display. Global paths with
        /// <c>$HOME</c> are resolved against the user profile dir.
        /// Returns <c>null</c> for CLI/clipboard-only clients.
        /// </summary>
        public static string ResolveDisplayPath(ClientEntry client, string projectPath)
        {
            if (client.PathTemplate == null) return null;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var p = client.PathTemplate.Replace("$HOME", home);
            if (client.ScopeKind == Scope.Project)
            {
                return Path.Combine(projectPath, p).Replace('\\', '/');
            }
            return p.Replace('\\', '/');
        }

        // --- envelope helpers -------------------------------------------------

        private static Dictionary<string, string> EnvMap(string project, int port)
        {
            return new Dictionary<string, string>
            {
                { BridgeConstants.ProjectPathEnvVar, project },
                { BridgeConstants.PortEnvVar, port.ToString() },
            };
        }

        /// <summary>
        /// Build a nested JSON object from a merge key + leaf entry fields.
        /// Constructs the object tree then serializes once.
        /// </summary>
        private static string BuildNestedJson(string[] mergeKeySegments, string leafKey, Dictionary<string, object> entryFields)
        {
            var root = new Dictionary<string, object>();
            var node = root;
            foreach (var seg in mergeKeySegments)
            {
                var child = new Dictionary<string, object>();
                node[seg] = child;
                node = child;
            }
            node[leafKey] = entryFields;
            return SerializeJson(root);
        }

        /// <summary>Pretty-print an arbitrary object tree (dict / list / scalar).</summary>
        private static string SerializeJson(object value)
        {
            var sb = new StringBuilder();
            SerializeJsonInto(sb, value, 0);
            return sb.ToString();
        }

        private static void SerializeJsonInto(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case string s:
                    AppendJsonString(sb, s);
                    break;
                case Dictionary<string, object> dict:
                {
                    sb.Append("{\n");
                    var pad = new string(' ', (indent + 1) * 2);
                    var closePad = new string(' ', indent * 2);
                    var i = 0;
                    foreach (var kv in dict)
                    {
                        sb.Append(pad).Append('"').Append(kv.Key).Append("\": ");
                        SerializeJsonInto(sb, kv.Value, indent + 1);
                        if (++i < dict.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(closePad).Append('}');
                    break;
                }
                case Dictionary<string, string> smap:
                {
                    sb.Append("{\n");
                    var pad = new string(' ', (indent + 1) * 2);
                    var closePad = new string(' ', indent * 2);
                    var i = 0;
                    foreach (var kv in smap)
                    {
                        sb.Append(pad).Append('"').Append(kv.Key).Append("\": ");
                        AppendJsonString(sb, kv.Value);
                        if (++i < smap.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(closePad).Append('}');
                    break;
                }
                case IList<object> oarr:
                {
                    if (oarr.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    var pad = new string(' ', (indent + 1) * 2);
                    var closePad = new string(' ', indent * 2);
                    for (var i = 0; i < oarr.Count; i++)
                    {
                        sb.Append(pad);
                        SerializeJsonInto(sb, oarr[i], indent + 1);
                        if (i + 1 < oarr.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(closePad).Append(']');
                    break;
                }
                default:
                    AppendJsonString(sb, value?.ToString() ?? "");
                    break;
            }
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string CodexTomlEntry(string command, IReadOnlyList<string> args, string project, int port)
        {
            var sb = new StringBuilder();
            sb.Append("[mcp_servers.").Append(ServerKey).Append("]\n");
            sb.Append("enabled = true\n");
            sb.Append("command = ").Append(TomlString(command)).Append('\n');
            sb.Append("args = [");
            for (var i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TomlString(args[i]));
            }
            sb.Append("]\n\n");
            sb.Append("[mcp_servers.").Append(ServerKey).Append(".env]\n");
            sb.Append(BridgeConstants.ProjectPathEnvVar).Append(" = ").Append(TomlString(project)).Append('\n');
            sb.Append(BridgeConstants.PortEnvVar).Append(" = ").Append(TomlString(port.ToString())).Append('\n');
            return sb.ToString();
        }

        private static string TomlString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
