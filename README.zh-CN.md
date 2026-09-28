# Unity Open MCP

[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/unity-open-mcp/)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=white 'Unity')](https://unity.com/releases/editor/archive)
[![](https://img.shields.io/badge/Node.js-339933?style=flat&logo=nodedotjs&logoColor=white 'Node.js')](https://nodejs.org/en/download/)
[![](https://img.shields.io/github/stars/AlexeyPerov/Unity-Open-MCP 'Stars')](https://github.com/AlexeyPerov/Unity-Open-MCP/stargazers)
[![](https://img.shields.io/github/last-commit/AlexeyPerov/Unity-Open-MCP 'Last Commit')](https://github.com/AlexeyPerov/Unity-Open-MCP/commits/master)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

| [🇺🇸 English](README.md) | [🇨🇳 简体中文](README.zh-CN.md) | [🇷🇺 Русский](README.ru.md) |
|-------------------------|--------------------------------|------------------------------|

<p align="center">
  <img src="assets/brand/openmcp-symbol.svg" alt="" width="86">
  &nbsp;&nbsp;
  <img src="assets/brand/openmcp-wordmark-color.svg" alt="Open MCP" width="341">
</p>

Unity Open MCP 是覆盖面最广的开源 Unity MCP 实现之一，在面向生产环境的
自动化栈中提供 **270+ 个类型化工具**。安全门禁变更、内置校验以及实时、
无头和离线执行，使其能够可靠地服务于真实项目——从资源智能分析和编辑器创作，
到闭环游戏测试、诊断、CI 和依赖包门控的 Unity 领域。

---
Open MCP 工具集的一部分
---
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

## 核心特性

### 安全的类型化创作

编辑 GameObject、场景、预制体、材质、包，以及 NavMesh、Input System、
Cinemachine、Timeline、Shader Graph 等依赖包门控领域。变更按
`checkpoint → mutate → validate → delta` 执行，并带有回归检查与定向修复。

> **用户：**删除那个预制体。<br>
> **智能体：**门禁预览发现 `Level1` 和 `SpawnPoint` 会新增 missing references，
> 因此我在项目损坏前停止了操作。

### 测试与观察

运行 Edit/Play Mode 测试，读取控制台，采集截图、性能分析器与内存数据，
并拉取事件。输入模拟可闭合游戏测试循环：探测交互项、点击 / 拖动 / 滑动、
推进帧，然后通过视觉结果验证。

### 实时、批处理或离线

优先使用实时 Editor；受支持的工具可回退到精确版本的无头 Editor；Unity
关闭时仍可从磁盘读取资源与编译诊断。结构化搜索、重新序列化以及引用 /
依赖分析会在对应路由支持时继续可用。

### 扩展与自动化

Unity 项目无需发布新的 MCP 服务器版本，即可公开类型化、可发现的命令。
显式异步操作会作为可观察作业运行，并提供进度、幂等性、保留结果与真实的取消状态。
CLI 与 CI 流程还提供健康检查、verify 基线和回归门禁。

详见[项目命令](docs/api/project-commands.md)与[异步作业](docs/api/jobs.md)（英文）。

### 只发现所需工具

默认仅显示 `core` 与 `gate-and-verify`；其他领域按需激活。运行时 capabilities
提供精确 schema、路由与可用性，项目技能则指导智能体执行 mutate → gate → fix 循环。

### 安装与维护

一条命令即可安装匹配的包版本、MCP 配置与智能体技能。团队和 monorepo
布局默认生成可提交、与机器无关的配置。协调更新流程覆盖 MCP 服务器、Unity
包以及可选的 [Unity Hub Pro](docs/unity-hub-pro.md) 桌面应用。

更多示例提示词：[docs/api/mcp-tools.md](docs/api/mcp-tools.md#example-prompts)（英文）。
完整工具目录与契约：[docs/api/mcp-tools.md](docs/api/mcp-tools.md)（英文）。

## 快速开始

需要 **Unity 2022.3 LTS 或更高版本**，MCP 服务器还需要 **Node.js 18+**。

1. **CLI — 推荐：**在 Unity 项目中打开终端并运行：

   ```bash
   npx -y unity-open-mcp@latest setup --client cursor
   ```

   其他受支持的写入器可使用 `claude`、`zcode`、`vscode`、`codex`、
   `opencode` 或 `agents`。命令会安装匹配的版本锁定、项目配置与内置技能；
   详见[手动安装](docs/zh-CN/setup/manual-setup.md)。
2. **Unity Hub Pro：** 使用图形化流程，参见
   [向导安装](docs/zh-CN/setup/wizard-setup.md)。
3. **手动安装：**从 [MCP 客户端配置](docs/zh-CN/setup/client-configuration.md)
   复制包与客户端配置。
4. **本地检出：** 构建并运行本仓库，参见
   [开发安装](docs/zh-CN/setup/development-setup.md)。
5. **实验性 — AI 智能体：**把下面的提示词粘贴到你的 AI 客户端。

<details>
<summary>智能体安装提示词</summary>

```text
按照
https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/agent-setup.md
的说明，在这个 Unity 项目中安装 Unity Open MCP（重新获取该流程；不要凭记忆即兴安装）。
确定此 Unity 项目的绝对根目录和我的客户端，然后运行
npx -y unity-open-mcp@latest setup --project <绝对项目路径> --client <id>。
让该包决定所有版本锁定并复制其内置 SKILL.md；不要编造版本、重写技能或调用 generate_skill。
自行完成所有智能体步骤，只在需要用户操作时停下。如果 monorepo 已在本地打开，
请从磁盘读取 docs/setup/agent-setup.md。
```

</details>

完整流程见 [Agent 安装](docs/zh-CN/setup/agent-setup.md)。
团队与 monorepo 可使用[可移植 MCP 配置](docs/zh-CN/setup/portable-config.md)，
无需写入每位开发者的绝对路径即可提交到仓库。

## 文档

面向用户：

- [API 索引](docs/api.md)（英文）— MCP、桥接、资源、路由与自动化契约。
- [扩展](docs/extensions.md)（英文）— 嵌入式领域、依赖与工具组激活。
- [故障排查](docs/troubleshooting.md)（英文）— 连接与恢复指南。
- [对话框策略](docs/dialog-policy.md)（英文）— 启动模态框处理与自动化。
- [技能](docs/skills.md)（英文）— 安装到 Unity 项目中的智能体操作手册。
- [版本兼容](docs/zh-CN/versioning.md) — 版本匹配与不一致时的恢复。
- [更新](docs/updating.md)（英文）— Hub、MCP 与 Unity 包的更新流程，包括离线环境。

面向贡献者：

- [架构](docs/architecture.md)（英文）— 仓库边界与运行时流程。
- [代码规范](docs/code-conventions.md)（英文）— 非显而易见的 C# 契约。

> 想看看其他 MCP 方案？参见 [Unity MCP 工具对比](docs/mcp-tools-comparison.md)（英文）— Unity Open MCP 与业内其他 MCP 工具 / AI 助手的功能矩阵并排对比。

> 注：除本 README、`docs/zh-CN/setup/` 下的安装文档与版本兼容页面外，其余文档目前仅有英文版。

## 贡献

- 在提 issue 或 pull request 之前请先阅读
  [CONTRIBUTING.md](CONTRIBUTING.md)（英文）。
- [贡献者故障排查](docs/troubleshooting-contributors.md)（英文）涵盖本地测试、桥接与自动化失败。
- [维护者版本与发布](docs/contributing/versioning.md)（英文）— 同步、标签与发布工作流。

**许可证：** MIT — 见 [LICENSE](LICENSE)。
