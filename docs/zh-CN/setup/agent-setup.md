[English](../../setup/agent-setup.md) · 简体中文

# Agent 安装

**受众：AI 智能体。** 按照本流程将 Unity Open MCP 安装到一个 Unity 项目中。请自己完成每一个智能体步骤。只有当出现 **USER ACTION** 块时，才停下来告知人类。

人类用户：此路径为**实验性**。优先使用[手动安装](manual-setup.md)或[向导安装](wizard-setup.md)。若仍想让智能体安装，把仓库 [README](../../../README.zh-CN.md#快速开始) 中的提示词粘贴到你的 AI 客户端。

## 硬性规则（不可跳过）

1. **绝不要编造版本号。** 不要从训练数据或先前对话中回忆 `0.x.y`。唯一允许的版本是你在[第 0 步](#第-0-步--解析发布版本)中解析得到的版本。
2. **重新获取本流程**（或从本地 monorepo 检出读取）。不要凭记忆即兴写另一套安装步骤。
3. **按字节复制技能**（`curl` / `cp`）。不要重写、摘要、扩写或“改进” `SKILL.md`。安装期间**不要**调用 `unity_open_mcp_generate_skill`。
4. **只写一个客户端技能路径**（检测到的客户端）。除非人类要求，否则不要写到所有客户端目录。

## 目标

为**某一个** Unity 项目安装 Unity Open MCP 的两个部分：

| 部分 | 安装内容 |
|---|---|
| **Unity 侧** | `Packages/manifest.json` 中的 bridge + verify 包（Git URL 锁定） |
| **AI 侧** | 启动 `npx -y unity-open-mcp@<VERSION>` 并带 `UNITY_PROJECT_PATH` 的 MCP 客户端配置 |

随后按字节复制核心智能体技能，把重启交给人类处理，并在工具可用时尽力做一次校验。

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

## 第 0 步 — 解析发布版本

在修改清单或 MCP 配置**之前**先读取发布锁定。

1. 获取（若本 monorepo 已打开则本地读取）：

   `https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/version.json`

   本地检出：仓库根目录的 `version.json`。
2. 解析 JSON，将 `VERSION` 设为 `version` 字段（例如 `"0.7.0"` →
   `VERSION=0.7.0`）。
3. 可选：与已发布的 npm 对照：

   ```bash
   npm view unity-open-mcp version
   ```

   若 npm 的 latest 与 `VERSION` 不同，**以 `version.json` 中的 `VERSION` 为准**
   （这是本流程的文档锁定），并告知人类。
4. 此后每一处锁定都使用同一个 `VERSION`：

   | 制品 | 锁定 |
   |---|---|
   | npm MCP 服务器 | `unity-open-mcp@<VERSION>` |
   | Bridge UPM | `…/packages/bridge#bridge-v<VERSION>` |
   | Verify UPM | `…/packages/verify#verify-v<VERSION>` |

若无法读取 `version.json`，**停下**。不要猜测。

## 第 1 步 — 合并 Unity 包（`Packages/manifest.json`）

读取 `Packages/manifest.json`。在 `dependencies` 下，设置（仅创建或覆盖这两个键 — 其他依赖一律保持不变），并代入第 0 步的 `VERSION`：

```json
"com.alexeyperov.unity-open-mcp-bridge": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v<VERSION>",
"com.alexeyperov.unity-open-mcp-verify": "https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v<VERSION>"
```

当 `VERSION=0.7.0` 时的示例：

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

按 [MCP 客户端配置](client-configuration.md) 操作：在表格中找到所检测的客户端，复制其片段，将 `/absolute/path/to/project` 替换为前置条件中的绝对路径，并将片段中的每个 npm 版本替换为**第 0 步的 `VERSION`**（共享文档可能落后于发布；以你解析的 `VERSION` 为准）。如果你是从网络获取本流程的，请获取
`https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/client-configuration.md`。

使用：`npx -y unity-open-mcp@<VERSION>`。

**当 `unity-open-mcp` 已存在时的幂等合并规则：**

1. 将 `command` / `args`（或等价字段）更新为 `npx -y unity-open-mcp@<VERSION>`。
2. 将 `UNITY_PROJECT_PATH` 设置为**当前**项目的绝对路径。
3. 保留条目上已有的其他环境变量键。
4. 其他 MCP 服务器 / 无关配置键一律保持不变。
5. 如父目录缺失则创建。如果文件不存在，用该客户端正确的顶层结构创建它。

不要凭记忆猜测配置形态：共享参考文档拥有客户端路径与 JSON/TOML/CLI 片段。如果 Claude Desktop 的 OS 全局文件无法定位，请向人类询问其路径。

## 第 3 步 — 复制核心技能（仅字节）

为**一个**检测到的客户端安装随附操作手册。该文件故意较大；智能体**不得**自己撰写。

1. 按需创建目标目录（见下表）。
2. 用 shell 工具复制 — **不要**粘贴或重新生成 markdown：

   ```bash
   # 远程（常见）：
   curl -fsSL \
     "https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/skills/unity-open-mcp/SKILL.md" \
     -o "<PROJECT_ROOT>/<client-skill-path>"

   # 本地 monorepo 检出时改用：
   # cp skills/unity-open-mcp/SKILL.md "<PROJECT_ROOT>/<client-skill-path>"
   ```

3. 若文件已存在则覆盖。
4. 确认写入的文件不是短 stub（大约数百行 / 数十 KB）。若只写了很短内容，删除后重新 `curl`/`cp`。
5. **安装期间禁止：** 重写技能、摘要、手工追加项目清单，或调用
   `unity_open_mcp_generate_skill`。项目专属再生成可在之后可选进行（见[技能](../../skills.md)）。

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

若询问一次后客户端映射仍不明确，**只**写入 `.agents/skills/unity-open-mcp/SKILL.md`（通用 agents 目录），不要写到所有客户端。

## 第 3b 步 — 交接前报告（必需）

在 USER ACTION 清单之前，精确打印：

1. 从 `version.json` 解析出的 `VERSION`
2. 你写入的两条 UPM 依赖字符串
3. 你写入的 MCP 启动命令（`npx -y unity-open-mcp@…`）
4. 唯一写入的技能路径，以及该文件的 `wc -l` / 大小
5. 确认你**没有**编造版本，也**没有**重写技能

若任何锁定不等于第 0 步的 `VERSION`，先修正再继续。

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
  MCP 的新智能体会话）。可选地，他们可以运行（替换 `VERSION` 与项目路径）：

```bash
npx -y unity-open-mcp@<VERSION> wait-for-ready --project /absolute/path/to/project
npx -y unity-open-mcp@<VERSION> run-tool unity_open_mcp_capabilities --project /absolute/path/to/project --json
```

## 简短故障排查

- **bridge 不可用 / 连接被拒：** Unity 必须在 `UNITY_PROJECT_PATH` 指定的同一绝对项目路径上打开。
- **找不到 `npx` / `node`：** Node 不在 `PATH` 上 — 重装 Node LTS，重启 AI 客户端。
- **改了配置后仍没有工具：** 重启 MCP 客户端。
- **驱动了错误的项目：** `UNITY_PROJECT_PATH` 必须是绝对路径，且指向包含 `Assets/`、`Packages/`、`ProjectSettings/` 的目录。
- **安装了错误/旧版本：** 重新读取第 0 步的 `version.json`，重写 UPM 锁定与 MCP `@<VERSION>`；不要保留记忆中的旧锁定。

更多细节：[故障排查](../../troubleshooting.md)、[对话框策略](../../dialog-policy.md)。

## 相关文档

- [MCP 客户端配置](client-configuration.md) — 客户端路径与可复制片段
- [手动安装](manual-setup.md) — 人类自助安装流程
- [开发安装](development-setup.md) — 本地检出 / 贡献者路径
- [技能](../../skills.md) — 安装后操作手册涵盖的内容
- [扩展](../../extensions.md) — 可选领域包（本流程跳过）
- [版本管理](../../versioning.md) — 锁定版本如何与发布保持同步
