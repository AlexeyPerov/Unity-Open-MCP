[English](../../setup/client-configuration.md) · 简体中文

# MCP 客户端配置

将 MCP 客户端连接到一个 Unity 项目：找到客户端、复制片段、填入项目路径、保存文件、重启客户端。

## 按这些步骤做

1. 在[表格](#放在哪里)中找到你的客户端，记下配置文件路径。
2. 从[复制这些](#复制这些)复制对应片段。
3. 将 `/absolute/path/to/project` 替换为 Unity 项目根目录的绝对路径（包含
   `Assets/`、`Packages/`、`ProjectSettings/` 的文件夹）。
4. 写入该文件。若文件里已有其他 MCP 服务器，只添加 `unity-open-mcp` 条目 —
   不要清掉同级项。
5. 重启 MCP 客户端以重新加载配置。

服务器版本需与 bridge/verify 包锁定一致（下方为 `0.7.0`）。升级见
[版本管理](../../versioning.md)。首次 `npx` 启动可能需要 10–60 秒下载包；之后很快。

## 放在哪里

| 客户端 | 配置文件 | 片段 |
|---|---|---|
| Cursor | `<project>/.cursor/mcp.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Claude Desktop | OS 全局配置 | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Claude Code | CLI（无文件） | [`Claude Code`](#claude-code) |
| VS Code Copilot | `<project>/.vscode/mcp.json` | [VS Code](#vs-code-与-visual-studio-copilot) |
| Visual Studio Copilot | `<project>/.vs/mcp.json` | [VS Code](#vs-code-与-visual-studio-copilot) |
| OpenCode | `<project>/opencode.json` | [OpenCode](#opencode) |
| ZCode | `<project>/.zcode/cli/config.json` | [ZCode](#zcode) |
| Codex | `<project>/.codex/config.toml` | [Codex](#codex) |
| Cline | 客户端全局 MCP 设置 | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Gemini CLI | `<project>/.gemini/settings.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| GitHub Copilot CLI | `<project>/.mcp.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Kilo Code | `<project>/.kilocode/mcp.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Rider (Junie) | `<project>/.junie/mcp/mcp.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Unity AI | `<project>/UserSettings/mcp.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| ZooCode | `<project>/.roo/mcp.json` | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |
| Antigravity | 全局 Antigravity MCP 配置 | [`mcpServers`](#mcpservers-cursor-与大多数客户端) |

只要客户端支持，优先使用项目级路径。

## 复制这些

### `mcpServers`（Cursor 与大多数客户端）

适用于 Cursor、Claude Desktop、Cline、Gemini CLI、GitHub Copilot CLI、Kilo
Code、Rider、Unity AI、ZooCode 和 Antigravity：

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.7.0"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/project"
      }
    }
  }
}
```

### VS Code 与 Visual Studio Copilot

```json
{
  "servers": {
    "unity-open-mcp": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "unity-open-mcp@0.7.0"],
      "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@0.7.0"],
      "enabled": true,
      "environment": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
    }
  }
}
```

### ZCode

```json
{
  "mcp": {
    "servers": {
      "unity-open-mcp": {
        "type": "stdio",
        "command": "npx",
        "args": ["-y", "unity-open-mcp@0.7.0"],
        "env": { "UNITY_PROJECT_PATH": "/absolute/path/to/project" }
      }
    }
  }
}
```

### Codex

```toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "npx"
args = ["-y", "unity-open-mcp@0.7.0"]

[mcp_servers.unity-open-mcp.env]
UNITY_PROJECT_PATH = "/absolute/path/to/project"
```

### Claude Code

```sh
claude mcp add unity-open-mcp \
  --env UNITY_PROJECT_PATH=/absolute/path/to/project \
  -- npx -y unity-open-mcp@0.7.0
```

若服务器已注册，当命令、版本锁定或项目路径需要变更时，请先移除再重新添加。

## 可选

| 变量 | 是否必需 | 用途 |
|---|---|---|
| `UNITY_PROJECT_PATH` | 是 | Unity 项目根目录的绝对路径。 |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | 否 | 固定 bridge 端口，而非基于路径发现。 |
| `UNITY_PATH` | 否 | 批量回退时显式指定 Unity 可执行文件。 |

启动模态框相关环境变量：[对话框策略](../../dialog-policy.md)。

**全局安装**（代替 `npx`）：`npm install -g unity-open-mcp`，然后使用
`"command": "unity-open-mcp"` 且无 `args`。用 `npm update -g unity-open-mcp`
更新。

**本地检出：** 构建 `mcp-server/` 并指向
`node /absolute/path/to/unity-open-mcp/mcp-server/dist/index.js` — 见
[开发安装](development-setup.md)。

配置后的连接问题见 [故障排查](../../troubleshooting.md)。
