[English](../../setup/manual-setup.md) · 简体中文

# 手动安装

在不使用 Unity Hub Pro 的情况下安装 Unity Open MCP。

## 适用人群

这是最常见的终端安装方式：添加 Unity 包，再把 MCP 客户端指向服务器。

更想用 GUI？走 **向导** 路径：[wizard-setup.md](wizard-setup.md)。
想尝试实验性的 AI 智能体安装？见 [agent-setup.md](agent-setup.md)。
在本仓库上开发？见 [development-setup.md](development-setup.md)。

Unity Open MCP 有两边都需要安装：**Unity 侧**（编辑器中的 bridge + verify 包）和
**AI 侧**（由客户端启动的小型 Node MCP 服务器）。下面按顺序安装每一侧。

## 环境要求

- **Unity 2022.3 LTS 或更高版本**。
- **Node.js 18 或更高版本** — 仅用于让 MCP 客户端启动服务器（`npx`）。从
  <https://nodejs.org/>（LTS）安装，重启终端，并用 `node --version` 验证。
- **支持 stdio MCP 服务器的 MCP 客户端** — Cursor、Claude Desktop、Claude Code、
  OpenCode、ZCode、Cline、Codex、VS Code Copilot、Gemini CLI，或任何兼容客户端。
  可复制片段见 [MCP 客户端配置](client-configuration.md)。

## 快速路径：一条命令（实验性）

setup 命令会替你完成第 1、2 步。在 Unity 项目文件夹（包含 `Assets/`、`Packages/`
和 `ProjectSettings/` 的文件夹）中打开终端并运行：

```bash
npx -y unity-open-mcp@1.3.0 setup --client cursor
```

其他客户端请把 `cursor` 换成 `claude`、`vscode`、`codex`、`opencode` 或 `agents`。
若 Unity 项目是仓库的子文件夹，请在仓库根目录运行并指明该文件夹：
`npx -y unity-open-mcp@1.3.0 setup --client cursor --unity-subpath Client`。
建议先加 `--dry-run` 查看它将修改的所有文件。

该命令把 bridge、verify 与服务器锁定到同一版本，合并 MCP 条目而不改动其他服务器，
并复制智能体技能。当该文件夹是 git 仓库根目录或使用了 `--unity-subpath` 时，它会写入
[可移植形式](portable-config.md)，可以直接提交。随后请继续[第 3 步](#3-打开-unity-并校验)。所有选项见
[智能体安装](agent-setup.md#2运行-setup)。其他客户端或想手动编辑文件时，请按第 1–2 步操作。

## 1) 添加 Unity 包

打开 Unity 项目中的 `Packages/manifest.json`（例如
`MyGame/Packages/manifest.json`），在 `dependencies` 中加入：

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.3.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.3.0"
  }
}
```

在第 2 步为 MCP 服务器锁定同一版本。升级时见
[版本管理](../../versioning.md)。

可选领域包（NavMesh、Input System 等）以及编辑器内安装面板见
[扩展](../../extensions.md) — 基础安装不需要它们。

## 2) 配置 MCP 客户端

1. 打开 [MCP 客户端配置](client-configuration.md)。
2. 在表格中找到你的客户端，记下配置文件路径。
3. 复制该客户端的片段。
4. 将 `/absolute/path/to/project` 替换为 Unity 项目根目录的绝对路径（包含
   `Assets/`、`Packages/`、`ProjectSettings/` 的文件夹）。
5. 保存文件（若已有其他 MCP 服务器，只添加 `unity-open-mcp` 条目）。

这个文件会提交到仓库吗？那就跳过第 4 步，改用[可移植形式](portable-config.md)：
它不含本机路径，每位同事检出后即可直接使用。

## 3) 打开 Unity 并校验

1. 在编辑器中打开**同一个** Unity 项目（`UNITY_PROJECT_PATH`）。
2. 等待脚本编译完成（右下角状态栏）。
3. 重启 MCP 客户端，使其重新读取第 2 步的配置。
4. 在 Unity 中打开 **Tools → Unity Open MCP Bridge** — 应显示 **connected**。
   若如此，即完成。

让 AI 客户端运行任意 Unity Open MCP 工具（例如列出可用能力）。若返回 Unity
数据，则两侧已连通。

## 可选后续步骤

- 领域包与激活 — [扩展](../../extensions.md)
- CI / CLI 自动化 — [CLI 与自动化](../../api/cli-automation.md)
- 无人值守机器上的启动模态框 — [对话框策略](../../dialog-policy.md)

## 故障排查

先确认 Unity 已在同一绝对路径 `UNITY_PROJECT_PATH` 上打开、编译完成、且 MCP
客户端已重启。然后遵循 [故障排查](../../troubleshooting.md)。模态框策略与
macOS 辅助功能细节见 [对话框策略](../../dialog-policy.md)。

## 相关文档

- [Agent 安装](agent-setup.md)
- [MCP 客户端配置](client-configuration.md)
- [向导安装](wizard-setup.md)
- [开发安装](development-setup.md)
- [Unity Hub Pro](../../unity-hub-pro.md)
