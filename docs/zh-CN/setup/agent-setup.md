[English](../../setup/agent-setup.md) · 简体中文

# Agent 安装

**受众：AI 智能体。** 按照本流程将 Unity Open MCP 安装到一个 Unity 项目中。请自己完成每一个智能体步骤。只有当出现 **USER ACTION** 块时，才停下来告知人类。

人类用户：把仓库 [README](../../../README.zh-CN.md#快速开始) 中的提示词粘贴到你的 AI 客户端，或者自行按照[手动安装](manual-setup.md)操作。

## 目标

为**某一个** Unity 项目安装 Unity Open MCP 的两个部分：

| 部分 | 安装内容 |
|---|---|
| **Unity 侧** | `Packages/manifest.json` 中的 bridge + verify 包（Git URL 锁定） |
| **AI 侧** | 启动 `npx -y unity-open-mcp@0.7.0` 并带 `UNITY_PROJECT_PATH` 的 MCP 客户端配置 |

随后安装/更新核心智能体技能，把重启交给人类处理，并在工具可用时尽力做一次校验。

**不要**安装可选的 Unity 领域包（NavMesh、Input System 等），除非人类明确要求。引导他们查看[扩展](../../extensions.md)。

## 前置条件清单

在编辑任何内容之前：

1. **确定 Unity 项目根目录** — 一个包含 `Assets/`、`Packages/` 和 `ProjectSettings/` 的目录。必要时从工作区当前目录向上查找。
   如果找不到，**停下**并向人类询问项目的绝对路径。
2. **确定项目的绝对路径** — 末尾不带斜杠。它将成为
   `UNITY_PROJECT_PATH`。
3. **检查 Node.js** — 运行 `node --version`。要求 **18+**。
   - 如果缺失或版本过低 → **USER ACTION：** 从
     <https://nodejs.org/> 安装 Node LTS，重启终端 / AI 客户端使 `node`/`npx` 出现在
     `PATH` 中，然后请人类重新运行此安装。
4. **检测 MCP 客户端**（自动检测；仅在模糊时询问一次）：
   - Cursor → 存在 `.cursor/`，或有 Cursor 特有的环境/界面线索
   - Claude Code → `claude` CLI / Claude Code 会话
   - Claude Desktop → Claude Desktop 应用配置路径
   - VS Code Copilot → `.vscode/` + Copilot MCP
   - Visual Studio Copilot → `.vs/`
   - OpenCode → `opencode` / `opencode.json`
   - ZCode → `.zcode/` / `.agents/`
   - Codex → `.codex/`
   - 否则询问：“我应该配置哪个 AI 客户端？”

## 第 1 步 — 合并 Unity 包（`Packages/manifest.json`）

读取 `Packages/manifest.json`。在 `dependencies` 下，设置（仅创建或覆盖这两个键 — 其他依赖一律保持不变）：

```json
"com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v0.7.0",
"com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v0.7.0"
```

**幂等规则：**

- 如果键缺失 → 添加。
- 如果已存在但 URL/标签/`file:` 路径不同 → **更新**为上面的锁定版本（已发布的 Git 安装是 agent 安装路径）。
- 绝不删除无关包。
- 写入合法的 JSON（尽量保留格式；优先最小改动）。

## 第 2 步 — 配置 MCP 客户端（幂等合并）

只要客户端支持，**优先使用项目级配置**。仅当人类明确要求时才使用全局配置。

按 [MCP 客户端配置](client-configuration.md) 操作：在表格中找到所检测的客户端，复制其片段，并将 `/absolute/path/to/project` 替换为前置条件中的绝对路径。如果你是从网络获取本流程的，请获取
`https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/client-configuration.md`。
使用其中所示的锁定命令 `npx -y unity-open-mcp@0.7.0`。

**当 `unity-open-mcp` 已存在时的幂等合并规则：**

1. 将 `command` / `args`（或等价字段）更新为锁定的 `npx -y unity-open-mcp@0.7.0` 形式。
2. 将 `UNITY_PROJECT_PATH` 设置为**当前**项目的绝对路径。
3. 保留条目上已有的其他环境变量键。
4. 其他 MCP 服务器 / 无关配置键一律保持不变。
5. 如父目录缺失则创建。如果文件不存在，用该客户端正确的顶层结构创建它。

不要凭记忆猜测配置形态：共享参考文档拥有客户端路径与 JSON/TOML/CLI 片段。如果 Claude Desktop 的 OS 全局文件无法定位，请向人类询问其路径。

## 第 3 步 — 安装 / 更新核心技能

始终为检测到的客户端安装或覆盖随附的核心操作手册。

1. 获取模板：
   `https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/skills/unity-open-mcp/SKILL.md`
   （如果本 monorepo 已经在本地打开，则改为从
   `skills/unity-open-mcp/SKILL.md` 复制。）
2. 写入下面的客户端技能路径（按需创建目录）。
   如文件已存在则覆盖原路径。

| 客户端家族 | 技能路径（位于项目根下） |
|---|---|
| Cursor | `.cursor/skills/unity-open-mcp/SKILL.md` |
| Claude (Desktop / Code) | `.claude/skills/unity-open-mcp/SKILL.md` |
| OpenCode | `.opencode/skills/unity-open-mcp/SKILL.md` |
| ZCode / Codex / 通用智能体 | `.agents/skills/unity-open-mcp/SKILL.md` |
| Cline | `.cline/skills/unity-open-mcp/SKILL.md` |
| Gemini | `.gemini/skills/unity-open-mcp/SKILL.md` |
| Kilo Code | `.kilocode/skills/unity-open-mcp/SKILL.md` |
| ZooCode (Roo) | `.roo/skills/unity-open-mcp/SKILL.md` |
| Antigravity | `.agent/skills/unity-open-mcp/SKILL.md` |
| Rider (Junie) | `.junie/skills/unity-open-mcp/SKILL.md` |
| VS Code | `.vscode/skills/unity-open-mcp/SKILL.md` |
| Visual Studio | `.vs/skills/unity-open-mcp/SKILL.md` |
| GitHub Copilot CLI | `.github/skills/unity-open-mcp/SKILL.md` |

如果客户端映射不明确，写入 Cursor + Claude + OpenCode + agents
路径（安全默认集合）。

## 第 4 步 — USER ACTION 交接（必需）

向人类打印这份清单，并**停止改动**，直到他们确认：

1. **打开 Unity** 并加载**同一个**项目（`UNITY_PROJECT_PATH`）。
2. 等待脚本编译完成（编辑器状态栏）。
3. **重启你的 MCP / AI 客户端**，使其重新加载第 2 步的配置。
   （大多数客户端仅在启动时读取 MCP 配置。）
4. 可选：在 Unity 中打开 **Tools → Unity Open MCP Bridge**，确认状态正常。

首次 `npx` 启动可能需要 10–60 秒（npm 下载该包），这是正常的。

## 第 5 步 — 尽力校验

在人类确认 Unity 已打开且客户端已重启后：

- 如果 Unity Open MCP 工具在当前会话中可见，调用
  `unity_open_mcp_capabilities` 和/或 `unity_open_mcp_ping`。
  - 成功 → 报告安装完成。
  - 失败 → 打印下面的简短故障排查清单；不要无限循环。
- 如果工具**还不可见** → 告诉人类配置/技能/清单已就位，他们仍需重启客户端（或开启一个会加载
  MCP 的新智能体会话）。可选地，他们可以运行：

```bash
npx -y unity-open-mcp@0.7.0 wait-for-ready --project /absolute/path/to/project
npx -y unity-open-mcp@0.7.0 run-tool unity_open_mcp_capabilities --project /absolute/path/to/project --json
```

## 简短故障排查

- **bridge 不可用 / 连接被拒：** Unity 必须在 `UNITY_PROJECT_PATH` 指定的同一绝对项目路径上打开。
- **找不到 `npx` / `node`：** Node 不在 `PATH` 上 — 重装 Node LTS，重启 AI 客户端。
- **改了配置后仍没有工具：** 重启 MCP 客户端。
- **驱动了错误的项目：** `UNITY_PROJECT_PATH` 必须是绝对路径，且指向包含 `Assets/`、`Packages/`、`ProjectSettings/` 的目录。

更多细节：[故障排查](../../troubleshooting.md)、[对话框策略](../../dialog-policy.md)。

## 相关文档

- [MCP 客户端配置](client-configuration.md) — 客户端路径与可复制片段
- [手动安装](manual-setup.md) — 人类自助安装流程
- [开发安装](development-setup.md) — 本地检出 / 贡献者路径
- [技能](../../skills.md) — 安装后操作手册涵盖的内容
- [扩展](../../extensions.md) — 可选领域包（本流程跳过）
- [版本管理](../../versioning.md) — 锁定版本如何与发布保持同步
