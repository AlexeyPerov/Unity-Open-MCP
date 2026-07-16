[English](../../setup/client-configuration.md) · 简体中文

# MCP 客户端配置

本文是将一个 MCP 客户端连接到某个 Unity 项目的权威参考。各安装指南在此查阅客户端特有的文件位置与配置结构。

## 在配置客户端之前

- 安装 Node.js 18 或更高版本。
- 确定 Unity 项目根目录的绝对路径：即包含 `Assets/`、`Packages/` 和 `ProjectSettings/` 的文件夹。
- 只要客户端支持，优先使用项目级配置。
- 使用与 bridge 和 verify 包锁定版本一致的服务器版本。在单独改动某一侧之前，请先阅读[版本管理](../../versioning.md)。

标准的服务器条目如下：

```json
{
  "command": "npx",
  "args": ["-y", "unity-open-mcp@0.7.0"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

`npx` 会下载并启动该精确版本。首次启动可能需要 10–60 秒；后续启动使用 npm 缓存。升级时，需同时更改 npm 版本锁定以及 bridge/verify 包锁定。

## 客户端文件与配置结构

| 客户端 | 首选配置位置 | 配置结构 |
|---|---|---|
| Cursor | `<project>/.cursor/mcp.json` | `mcpServers` |
| Claude Desktop | OS 全局配置 | `mcpServers` |
| Claude Code | CLI 注册 | `claude mcp add` |
| VS Code Copilot | `<project>/.vscode/mcp.json` | `servers` with `type: "stdio"` |
| Visual Studio Copilot | `<project>/.vs/mcp.json` | `servers` with `type: "stdio"` |
| OpenCode | `<project>/opencode.json` | `mcp`；命令数组；`environment` |
| ZCode | `<project>/.zcode/cli/config.json` | `mcp.servers` with `type: "stdio"` |
| Codex | `<project>/.codex/config.toml` | TOML `mcp_servers` 表 |
| Cline | 客户端全局 MCP 设置 | `mcpServers` |
| Gemini CLI | `<project>/.gemini/settings.json` | `mcpServers` |
| GitHub Copilot CLI | `<project>/.mcp.json` | `mcpServers` |
| Kilo Code | `<project>/.kilocode/mcp.json` | `mcpServers` |
| Rider (Junie) | `<project>/.junie/mcp/mcp.json` | `mcpServers` |
| Unity AI | `<project>/UserSettings/mcp.json` | `mcpServers` |
| ZooCode | `<project>/.roo/mcp.json` | `mcpServers` |
| Antigravity | 全局 Antigravity MCP 配置 | `mcpServers` |

如果配置已存在，合并 `unity-open-mcp` 条目时不要替换无关设置或其他 MCP 服务器。

### `mcpServers` 类客户端

对 Cursor、Claude Desktop、Cline、Gemini CLI、GitHub Copilot CLI、Kilo Code、Rider、Unity AI、ZooCode 和 Antigravity 使用此结构：

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

如果服务器已注册，当命令、版本锁定或项目路径需要变更时，请先移除再重新添加。

## 环境变量

| 变量 | 是否必需 | 用途 |
|---|---|---|
| `UNITY_PROJECT_PATH` | 是 | Unity 项目根目录的绝对路径。 |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | 否 | 固定一个 bridge 端口，而非基于路径的自动发现。 |
| `UNITY_PATH` | 否 | 当自动发现不可用时，为批量回退指定明确的 Unity 可执行文件。 |

启动与稳态模态框处理还有额外的环境变量。完整的列表、默认值、安全开关与策略矩阵见[对话框策略](../../dialog-policy.md)。

## 其他服务器命令

全局安装：

```bash
npm install -g unity-open-mcp
```

使用 `"command": "unity-open-mcp"` 且不带参数（或 OpenCode 对应的命令数组）。用
`npm update -g unity-open-mcp` 显式更新。

对于本地检出，构建 `mcp-server/` 并把 `npx` 条目替换为：

```json
{
  "command": "node",
  "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

完整的贡献者工作流见[开发安装](development-setup.md)。

## 编辑配置之后

重启 MCP 客户端使其重新加载文件，打开同一个 Unity 项目，并等待编译完成。然后调用 `unity_open_mcp_ping` 或
`unity_open_mcp_capabilities`。

连接与 bridge 恢复请见[故障排查](../../troubleshooting.md)。
