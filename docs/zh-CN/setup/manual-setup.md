[English](../../setup/manual-setup.md) · 简体中文

# 手动安装

本文说明如何通过命令行进行 Unity Open MCP 的常规安装。

## 适用人群

这是**自己动手**的路径。如果你满足以下情况就很合适：

- 已经会手工编辑 Unity 的 `manifest.json` 和 MCP 客户端配置文件，或
- 想精确控制安装与锁定的内容，或
- 处于无头/CI 机器上，无法使用 Hub GUI。

想交给 AI 智能体来安装？参见 [agent-setup.md](agent-setup.md)
（把 README 中的提示词粘贴到你的 AI 客户端）。

如果你从未打开过终端或编辑过 JSON，**向导**路径会容易得多 — 见
[wizard-setup.md](wizard-setup.md)。它会自动完成下面的一切，并解释每一步。

## Unity Open MCP 如何组合在一起（两个部分）

Unity Open MCP 由两个相互独立的部分组成，它们通过你本机的回环网络通信：

| 部分 | 位于 | 安装来源 | 作用 |
|---|---|---|---|
| **AI 侧** | 一个小的 Node 程序（MCP 服务器） | **npm**（`npx`） | 你的 AI 客户端（Cursor、Claude 等）启动它；它暴露 MCP 工具。 |
| **Unity 侧** | 两个 Unity 编辑器包（bridge + verify） | **Unity Package Manager**（Git URL） | 在 Unity 内运行，执行每一次工具调用。 |

两个部分**都需要**。AI 侧从不直接触碰 Unity — 它通过 HTTP 向 Unity 侧请求。下面的步骤依次安装每个部分。

若要在包本身上工作（本地检出、构建 MCP 服务器、贡献者与维护者工作流），请见 [development-setup.md](development-setup.md)。

## 环境要求

- **Unity 2022.3 LTS 或更高版本**。
- **Node.js 18 或更高版本** — 这*仅*因为 MCP 服务器是一个由 AI 客户端在后台启动的小型
  Node 程序才需要。你**不会**编写 JavaScript，也不会交互式运行它。
  - 没有 Node？从 <https://nodejs.org/> 安装（点 **LTS** 按钮即可）。之后重启终端，使 `node`/`npx` 命令进入
    PATH。用 `node --version` 验证。
- **一个支持 stdio MCP 服务器的 MCP 客户端** — Cursor、Claude Desktop、Claude Code、OpenCode、ZCode、Cline、Codex、VS Code Copilot、Gemini CLI，或任何兼容的 MCP 客户端。Hub 向导可一键配置全部；下面的代码片段覆盖最常见的几种手动形态。

## 1) 添加 Unity 包（Unity 侧）

打开你 Unity 项目中的 `Packages/manifest.json`（位于项目文件夹根目录，例如
`MyGame/Packages/manifest.json`），在 `dependencies` 对象中加入两个条目。

### Git 安装（推荐）

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.7.0",
    "com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.7.0"
  }
}
```

### 可选 Unity 领域依赖

领域工具随 bridge 打包。部分工具在编译进来之前需要匹配的 Unity 包，且大多数需要显式的会话激活。权威依赖目录、激活表与包示例请见
[扩展](../../extensions.md)。

### 可选依赖（编辑器内）

bridge 安装后，你可以无需手工编辑清单来增删 Unity 领域依赖。打开 **Tools → Unity Open MCP Bridge → Extensions**，使用 **Optional Unity dependencies** 面板：每个领域一行，显示已安装/缺失状态，每个 UPM 包都有一键式的 **Install…** / **Remove…** 操作。Unity 会重新导入清单并重新编译；内嵌工具在下次域重载时注册（或停止编译）。Unity Hub Pro 的项目设置弹窗以只读状态面板的形式镜像了此功能。

## 2) 配置 MCP 客户端（AI 侧）

现在把你的 AI 客户端指向 MCP 服务器。使用
[MCP 客户端配置](client-configuration.md)查阅权威的客户端专属路径与 JSON/TOML/CLI 配置结构。把文档中的 `unity-open-mcp` 条目合并进你的客户端，不要替换无关设置。

这意味着：

- `npx -y unity-open-mcp@0.7.0` 从 npm 下载并启动该精确版本的 MCP 服务器。`-y` 接受首次运行的提示；锁定版本使服务器与 bridge、verify 包保持一致（它们共用同一个版本号）。要升级到新版本，请同时更新此处的版本与 Unity `manifest.json` — 见
  [版本管理](../../versioning.md)。
- **首次运行可能需要 10–60 秒**下载包 — 这是正常的，不是卡死。后续启动很快。
- 想一次性安装？`npm install -g unity-open-mcp` 把它放到 PATH，然后用 `"command": "unity-open-mcp"`（无 `npx`/`args`）。你用 `npm update -g unity-open-mcp` 手动更新。

> ⚠️ **`UNITY_PROJECT_PATH` 是头号安装陷阱。** 它必须是 Unity 项目根目录（即包含 `Assets/`、`Packages/`、`ProjectSettings/` 的文件夹）的**绝对**路径。缺失时服务器会立即退出；路径错误意味着 AI 可能驱动与你当前打开的不同的另一个 Unity 项目。

核心项目、端口与批量环境变量汇总在共享的客户端参考文档中。完整的启动对话框环境变量矩阵与安全策略仅在[对话框策略](../../dialog-policy.md)中。

## 3) 启动 Unity 并校验

1. 在编辑器中打开**同一个** Unity 项目（即 `UNITY_PROJECT_PATH` 指向的项目）。
2. 等待脚本编译完成 — 关注右下角状态栏。
3. 重启 MCP 客户端（使其重新读取第 2 步的配置）。
4. 最简单的检查：在 Unity 中打开 **Tools → Unity Open MCP Bridge** — 窗口应显示 **connected** 状态。如果是，就完成了。
5. 喜欢终端？确认 bridge 可达：

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

端口会根据你的项目路径自动推导；如果你设置了
`UNITY_OPEN_MCP_BRIDGE_PORT`，则使用该值。你也可以在第 4 步的 bridge 窗口中读取它。成功的 ping 返回包含 `"connected": true` 的 JSON。

最后，让 AI 客户端运行任意 Unity Open MCP 工具（例如让它列出可用能力）— 如果它以 Unity 数据回应，两个部分就接通了。

## 4) 可选 CLI（CI 与自动化）

```bash
npx -y unity-open-mcp@0.7.0 wait-for-ready --project /path/to/MyGame
npx -y unity-open-mcp@0.7.0 run-tool unity_open_mcp_capabilities --project /path/to/MyGame --json
```

在无人值守的机器上，通过
[对话框策略](../../dialog-policy.md)配置启动模态框处理（`UNITY_OPEN_MCP_DIALOG_POLICY` 及相关环境变量）。

## 故障排查

针对本路径，先确认 Unity 已在同一个绝对路径 `UNITY_PROJECT_PATH` 上打开、编译完成、且 MCP 客户端已重启。连接、监听器、模态框与 bridge 失效恢复请遵循完整的
[故障排查](../../troubleshooting.md)指南。模态框策略与 macOS 辅助功能细节见
[对话框策略](../../dialog-policy.md)。

## 相关文档

- [Agent 安装](agent-setup.md) — 让 AI 智能体执行此安装
- [MCP 客户端配置](client-configuration.md) — 客户端路径与配置结构
- [故障排查](../../troubleshooting.md) — bridge 启动失败与连接恢复
- [对话框策略](../../dialog-policy.md)
- [向导安装](wizard-setup.md)
- [开发安装](development-setup.md) — 本地检出、贡献者与维护者工作流。
- [Unity Hub Pro](../../unity-hub-pro.md)
- [Bridge HTTP API](../../api/bridge-http.md)
- [MCP 工具 API](../../api/mcp-tools.md)
