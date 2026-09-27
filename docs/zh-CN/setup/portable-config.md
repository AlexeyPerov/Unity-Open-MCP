[English](../../setup/portable-config.md) · 简体中文

# 可移植 MCP 配置（可提交到仓库）

把一份 MCP 客户端配置放进仓库，让整个团队直接使用 —— 不需要每个人的绝对路径，
也不需要“复制示例文件再改第 6 行”。

本页讲可提交的形式。按机器配置的客户端路径与片段目录见
[MCP 客户端配置](client-configuration.md)。

## 两种布局

**布局 A —— Unity 项目就是仓库。** AI 客户端打开的就是包含 `Assets/`、
`Packages/`、`ProjectSettings/` 的文件夹。

```
my-game/                    <- 在这里打开 AI 客户端
  .cursor/mcp.json          <- 提交到仓库
  Assets/
  Packages/
  ProjectSettings/
```

**布局 B —— 单体仓库。** AI 客户端打开仓库根目录，Unity 项目是其中的子文件夹。

```
my-game/                    <- 在这里打开 AI 客户端，配置也放这里
  .cursor/mcp.json          <- 提交到仓库，指向 Client/
  Client/                   <- Unity 项目（Assets/、Packages/）
  Server/
```

下面的内容两种布局通用，区别只是布局 B 需要写出 Unity 子文件夹（`Client`）。

## 项目路径如何解析

服务器需要一个绝对的 Unity 项目根目录。它取下列第一个已设置的输入：

| 优先级 | 输入 | 结果 |
|---|---|---|
| 1 | `--project <path>`（仅 CLI） | 绝对路径，或相对于工作目录解析 |
| 2 | `UNITY_PROJECT_PATH` | 绝对路径，或相对于工作目录解析 |
| 3 | `--project-from-cwd` + 可选 `--unity-subpath <rel>` | 工作目录加上子文件夹 |
| 4 | 都没有 | 启动报错，并列出以上选项 |

`UNITY_PROJECT_PATH` 优先于 `--project-from-cwd`，所以开发者仍可用自己的环境
覆盖已提交的配置。

即使同事把客户端打开在 Unity 文件夹而不是仓库根目录，`--unity-subpath` 也能
工作：当 `<cwd>/<subpath>` 不是 Unity 项目、而工作目录本身是时，使用工作目录
（来源 `cwd`）。启动时解析结果会打到 stderr：

```
[unity-open-mcp] Unity project resolved to /Users/dev/my-game/Client (source: cwd+subpath)
```

如果解析出的文件夹不是 Unity 项目，服务器会明确报错并退出，而不是连到错误的桥。

## 客户端能力矩阵

按客户端能力分为三种可移植形式：

| 形式 | 原理 | 适用场景 |
|---|---|---|
| **变量插值** | 客户端在配置中展开 `${workspaceFolder}` | 客户端支持（Cursor、VS Code） |
| **命令参数** | 服务器按客户端启动它的目录解析路径 | 客户端在工作区根目录启动 MCP 服务器 |
| **包装脚本** | 提交到仓库的脚本按自身位置解析路径 | 以上两种都不可用 |

| 客户端 | 配置文件 | 布局 A | 布局 B |
|---|---|---|---|
| Cursor | `<workspace>/.cursor/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| Claude Code | `<workspace>/.mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| VS Code Copilot | `<workspace>/.vscode/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| Visual Studio Copilot | `<workspace>/.vs/mcp.json` | `${workspaceFolder}` | `${workspaceFolder}/Client` |
| OpenCode | `<workspace>/opencode.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| GitHub Copilot CLI | `<workspace>/.mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Gemini CLI | `<workspace>/.gemini/settings.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Kilo Code | `<workspace>/.kilocode/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Rider（Junie） | `<workspace>/.junie/mcp/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| ZooCode | `<workspace>/.roo/mcp.json` | `--project-from-cwd` | `--project-from-cwd --unity-subpath Client` |
| Unity AI | `<unity-project>/UserSettings/mcp.json` | `--project-from-cwd` | — （配置就在 Unity 项目内） |
| Codex | `<workspace>/.codex/config.toml` | [包装脚本](#包装脚本) | [包装脚本](#包装脚本) |
| ZCode | `<workspace>/.zcode/cli/config.json` | [包装脚本](#包装脚本) | [包装脚本](#包装脚本) |
| Claude Desktop | 操作系统全局配置 | 只能用绝对路径 | 只能用绝对路径 |
| Cline | 客户端全局 MCP 设置 | 只能用绝对路径 | 只能用绝对路径 |
| Antigravity | Antigravity 全局配置 | 只能用绝对路径 | 只能用绝对路径 |

全局配置没有工作区可供解析，因此始终写绝对路径。这正是绝对路径形式的用途：
单机回退方案，而不是团队默认方案。

## 复制这些

### Cursor —— `${workspaceFolder}`

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": ["-y", "unity-open-mcp@1.3.0"],
      "env": { "UNITY_PROJECT_PATH": "${workspaceFolder}/Client" }
    }
  }
}
```

布局 A 去掉 `/Client`。

### Claude Code 及其他“仅命令参数”的客户端

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "npx",
      "args": [
        "-y",
        "unity-open-mcp@1.3.0",
        "--project-from-cwd",
        "--unity-subpath",
        "Client"
      ],
      "env": {}
    }
  }
}
```

布局 A 去掉最后两个参数。

如果想让 Claude Code 自己写入该文件，请在仓库根目录运行下面的命令，并提交
生成的 `.mcp.json`：

```bash
claude mcp add --scope project unity-open-mcp -- npx -y unity-open-mcp@1.3.0 --project-from-cwd --unity-subpath Client
```

不加 `--scope project` 时，条目会写入用户级 Claude Code 配置，不会共享。

### OpenCode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-open-mcp": {
      "type": "local",
      "command": ["npx", "-y", "unity-open-mcp@1.3.0", "--project-from-cwd", "--unity-subpath", "Client"],
      "enabled": true,
      "environment": {}
    }
  }
}
```

### 包装脚本

对既不展开工作区变量、也不展开环境变量的客户端，提交一个小脚本并让客户端指向它：

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "bash",
      "args": ["scripts/mcp/unity-open-mcp.sh"]
    }
  }
}
```

```toml
# .codex/config.toml
[mcp_servers.unity-open-mcp]
enabled = true
command = "bash"
args = ["scripts/mcp/unity-open-mcp.sh"]
```

bridge 窗口可以替你写入该脚本（见[在 Unity 编辑器中](#在-unity-编辑器中)），
也可以用 setup CLI 生成，这样其中的版本号与你实际安装的包一致：

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/my-game/Client \
  --client cursor \
  --layout monorepo --unity-subpath Client --wrapper
```

单体仓库下脚本落在 `scripts/mcp/unity-open-mcp.sh`；当 Unity 项目本身就是仓库
时落在 `.unity-open-mcp/mcp-wrapper.sh`。脚本按自身位置解析 Unity 根目录，导出
`UNITY_PROJECT_PATH`，再执行固定版本的包 —— 客户端的工作目录不再重要。单次运行
可用 `UNITY_SUBPATH=OtherClient` 覆盖 Unity 子文件夹。

`setup` 没有 Codex 或 ZCode 的配置写入器，因此命令中使用 `--client cursor`：它会在
脚本旁边同时写入可移植的 Cursor 条目（`.cursor/mcp.json`）。团队使用 Cursor 就保留，
否则删除，并手动添加上面的 Codex/ZCode 条目。

## 用 setup CLI 写入

```bash
npx -y unity-open-mcp@latest setup \
  --project /absolute/path/to/my-game/Client \
  --client cursor \
  --layout monorepo --unity-subpath Client
```

- 客户端配置与技能写入**工作区根目录**（`my-game/`）；Unity 包版本固定始终写入
  **Unity 项目**（`my-game/Client/Packages/manifest.json`）。
- `--layout monorepo` 默认就是可移植配置。用 `--no-portable` 强制写绝对路径，
  用 `--portable` 让布局 A 的项目也得到可提交形式。
- `--workspace <abs>` 显式指定仓库根目录；不传时由 `--project` 减去
  `--unity-subpath` 推导。
- `--dry-run` 只打印将要写入的片段而不落盘 —— 提交前用它确认里面没有机器路径。

用不同参数重跑会就地改写该条目：绝对形式变为可移植形式，反之亦然，同一文件中的
其他 MCP 服务器会被保留。

## 在 Unity 编辑器中

**Tools → Unity Open MCP Bridge → Status → Configure AI client** 提供
**Commit-safe config** 开关。当 Unity 项目位于 git 仓库内时默认开启，目标文件
指向仓库根目录。开启后，片段中既没有本机路径，也没有 bridge 端口：

- 支持 `${workspaceFolder}` 的客户端（Cursor 项目配置、VS Code、Visual
  Studio）得到插值后的 `UNITY_PROJECT_PATH`；
- 仅命令参数的客户端得到 `--project-from-cwd [--unity-subpath Client]`；
  Claude Code 则显示 `claude mcp add --scope project …` 命令；
- Codex 与 ZCode（project）得到包装脚本形式，并提供 **Write** 按钮，在显示的
  路径创建该脚本。

只读取机器级配置的客户端（Claude Desktop、Cline、Antigravity，以及全局的
Cursor/OpenCode/ZCode）保持绝对路径形式，面板会给出说明。Hub 设置向导中的
**Commit-safe config** 选项在检测到 Unity 项目上方有仓库时同样默认开启。

## 保持版本号最新

已提交的条目仍然固定版本（`unity-open-mcp@1.3.0`），它必须与
`Packages/manifest.json` 中的 bridge 与 verify 版本一起更新。bridge 窗口的
**Updates** 流程把可移植条目视为所在仓库对应项目的条目：`${workspaceFolder}`
或相对路径形式的 `UNITY_PROJECT_PATH`、仅命令参数的条目，以及已提交的包装
脚本（`scripts/mcp/unity-open-mcp.sh` 或 `.unity-open-mcp/mcp-wrapper.sh`）
都会被改写。请把更新后的文件一起提交。

## 同事克隆之后

不需要修改任何与本机相关的内容。每位同事：

1. 安装 Node.js 18 或更新版本。
2. 打开 Unity 项目（单体仓库中为 `Client/`），让 Package Manager 按
   `Packages/manifest.json` 解析 bridge 与 verify 版本。
3. 在仓库根目录打开 AI 客户端，并在提示时批准项目的 MCP 服务器：Claude Code 与
   Cursor 会对仓库中定义的服务器询问一次，Codex 只为受信任的项目读取
   `.codex/config.toml`。

如果同事本地已有同一配置文件的未提交副本，需在 `git pull` 之前删除或重命名：
Git 不会用已跟踪的文件覆盖未跟踪的文件。

## 不要提交的内容

- **`UNITY_OPEN_MCP_BRIDGE_PORT`。** 默认端口由绝对项目路径推导，因此每台机器
  都不同。不要写死它，让服务器自己发现正在运行的桥。只在本地未提交的覆盖文件中
  固定端口。
- **`UNITY_PATH`。** Unity 编辑器位置因机器而异。
- **密钥。** 同一文件中的其他服务器应从环境变量读取（`${env:…}`），而不是写在
  已提交的 JSON 里。

## 不启动编辑器也能验证

在仓库根目录执行：

```bash
npx -y unity-open-mcp@1.3.0 ping --project-from-cwd --unity-subpath Client
```

启动行会显示哪个输入生效、解析出的绝对路径是什么。桥还没运行时返回非零退出码
是正常的；你要看的是那行解析结果。

各客户端路径与绝对路径片段见 [MCP 客户端配置](client-configuration.md)。
首次安装见[代理安装](agent-setup.md)或[手动安装](manual-setup.md)。
