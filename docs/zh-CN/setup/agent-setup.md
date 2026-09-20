[English](../../setup/agent-setup.md) · 简体中文

# Agent 安装

**面向 AI 智能体。** 使用 setup CLI，把 Unity Open MCP 安装到一个 Unity
项目。请自行完成智能体步骤，仅在 **USER ACTION（用户操作）**处停下。

对人类用户而言，此路径仍是实验性的；也可使用[手动安装](manual-setup.md)
或[安装向导](wizard-setup.md)。

如果项目已安装并在 Unity 中打开，不要仅为切换版本而重新运行首次安装。
请使用 **Tools → Unity Open MCP Bridge → Status → Updates**，或先以默认的
`dry_run: true` 预览 `unity_open_mcp_upgrade`；两者都会在审核计划后统一更新
包、配置与智能体文档中的版本锁定。

## 硬性规则

1. 每次重新获取本流程，或从本地检出读取；不要靠记忆中的片段安装。
2. 所有版本锁定均由正在运行的 `unity-open-mcp` 包决定。先尝试 CLI，
   不要编造版本或手写锁定。
3. CLI 会逐字节复制 npm 包内置的 `SKILL.md`。安装期间不要重写技能，
   也不要调用 `unity_open_mcp_generate_skill`。
4. 只安装到检测到的一个客户端，不要填充所有客户端目录。
5. 除非用户明确要求，否则不要添加可选领域包。

## 1）确定项目和客户端

找到 Unity 项目的绝对根目录，即包含 `Assets/`、`Packages/` 和
`ProjectSettings/` 的目录，并去掉末尾斜杠。

| 客户端 | `--client` | 写入的项目配置 |
|---|---|---|
| Cursor | `cursor` | `.cursor/mcp.json` |
| Claude Code / 项目级 Claude | `claude` | `.mcp.json` |
| OpenCode | `opencode` | `opencode.json` |
| 通用智能体 | `agents` | `.mcp.json` |

如果无法确定客户端，只询问用户一次。其他技能 id 虽可识别，但命令会
拒绝执行，因为无法安全写入其 MCP 配置；这些客户端请使用
[手动配置目录](client-configuration.md)。

运行 `node --version` 确认 Node.js 18 或更高版本。如果缺失或过旧，
请用户安装 Node LTS 并重启终端/客户端。

## 2）运行 setup

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/UnityProject \
  --client cursor
```

下载的包会把三个组件都锁定到**自身版本**。该命令会：

- 将 bridge 和 verify 的 Git 锁定合并到 `Packages/manifest.json`；
- 将 `unity-open-mcp` 服务器合并到项目级 MCP 配置，并保留其他服务器
  和额外环境变量；
- 把内置核心技能逐字节复制到所选客户端路径；
- 不启动 Unity、不连接 bridge，也不安装领域包。

选项：`--dry-run` 只报告而不写文件；`--skip-skill` 不修改技能；
`--json` 输出稳定的机器可读报告；`setup --help` 无需其他必填参数。
退出码 `0` 表示成功，`2` 表示项目/客户端用法错误，`1` 表示文件读取、
JSON 解析或写入失败。

## 3）检查报告

确认报告包含 `VERSION`、同版本的两个 UPM 锁定、MCP 配置路径与
`unity-open-mcp@VERSION`，以及一个技能路径和字节数（未指定
`--skip-skill` 时）。

## 4）USER ACTION（用户操作）

请用户：

1. 用同一路径打开 Unity 项目，并等待编译完成。
2. 重启 MCP / AI 客户端，使其重新加载配置。
3. 可选：打开 **Tools → Unity Open MCP Bridge** 检查状态。

用户确认后，若工具可见，调用 `unity_open_mcp_capabilities` 或
`unity_open_mcp_ping`。

## 手动备用方案

若客户端不受支持，请使用 [MCP 客户端配置](client-configuration.md)、
[手动安装](manual-setup.md)或[安装向导](wizard-setup.md)。所有锁定必须使用
同一版本；不要编造版本，也不要重写技能。

安装后若遇到问题，请参阅[故障排查](../../troubleshooting.md)。
