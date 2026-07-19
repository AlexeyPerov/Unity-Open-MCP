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
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="Unity Open MCP" width="250">
</p>

Unity Open MCP 为 AI 智能体提供了一个类型化、带安全门禁的工具接口，用于操作 Unity 项目。

该 MCP 服务器暴露了 **250+ 个工具**，覆盖类型化编辑器工作流、门禁与校验、资源智能分析、诊断，以及嵌入式领域工具组。

---
Open MCP 工具集的一部分
---
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

## 核心特性

### 资源智能分析

结构化搜索、检视、重新序列化，以及引用 / 依赖分析——Unity 关闭时也可用离线读取。

> **示例：**「找出所有引用 `PlayerController` 的预制体，并汇总入站依赖。」

### 实时桥接、批处理回退与离线读取

优先使用实时 Editor；受支持的工具可回退到无头批处理；也可从磁盘读取资源与编译错误。

> **示例：**「桥接离线——从 Editor 日志中显示最近的编译错误。」

### 类型化编辑器与嵌入式领域

GameObject、场景、预制体、材质、包管理，以及依赖包门控的领域（NavMesh、Input System、Cinemachine、Timeline、Shader Graph 等）。

> **示例：**「激活 `cinemachine`，并为 Player 创建跟随相机。」

### 智能体感知与诊断

测试运行器、截图、控制台、性能分析器 / 内存快照，以及事件拉取，支持闭环智能体工作流。

> **示例：**「运行 Combat 程序集的 Play Mode 测试，失败时截取 Game 视图。」

### 会话工具组与技能

默认工具面保持精简；按需激活领域。项目技能指导智能体执行 mutate → gate → fix 循环。

> **示例：**「重置工具组，然后只激活 `core` 与 `gate-and-verify`。」

### CI、基线与回归

扫描项目、创建 verify 基线，并在出现新的阻塞性问题时让自动化失败——支持实时或批处理 / CLI。

> **示例：**「为此项目创建 verify 基线，并报告相对上一基线的新阻塞性问题。」

### Unity Hub Pro

可选桌面应用，用于项目管理、AI 安装向导与维护者操作——走 MCP 路径时并非必需。

> **示例：**在 Hub 项目行上使用 **AI** 操作（见 [Unity Hub Pro](docs/unity-hub-pro.md)，英文）。

### 带安全门禁的变更

变更按 `checkpoint → mutate → validate → delta` 执行，并配合回归检查与针对性修复——智能体可以在「看起来成功」的编辑把项目弄坏之前停下来。

> **用户：**删掉那个预制体。  
> **智能体：**正在检查删除 `Enemy` 的影响…  
> **门禁：**删除会在 `Level1` 和 `SpawnPoint` 上引入新的 missing references。  
> **智能体：**Unity Open MCP 在门禁预览里标出了这次回归。在你确认之前我**不会**删除该预制体——那些场景会坏掉。要我删除后再修好引用，还是先保留？

更多示例提示词：[docs/api/mcp-tools.md](docs/api/mcp-tools.md#example-prompts)（英文）。
完整工具目录与契约：[docs/api/mcp-tools.md](docs/api/mcp-tools.md)（英文）。

## 快速开始

需要 **Unity 2022.3 LTS 或更高版本**。

1. **最简单（AI 智能体）：** 把下面的提示词粘贴到你的 AI 客户端（Cursor、Claude 等），让它自动完成安装：

```text
按照
https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/agent-setup.md
的说明，在这个 Unity 项目中安装 Unity Open MCP。自己完成每一个智能体步骤；只有在需要人工操作时才停下来告诉我。
如果这个 monorepo 已经在本地打开，请从磁盘读取 docs/setup/agent-setup.md，而不要去网络获取。
```

完整流程见 [Agent 安装](docs/zh-CN/setup/agent-setup.md)。

2. **手动安装：** 自行编辑包和 MCP 客户端配置，参见
   [手动安装](docs/zh-CN/setup/manual-setup.md)。
3. **本地检出：** 构建并运行本仓库，参见
   [开发安装](docs/zh-CN/setup/development-setup.md)。
4. **Unity Hub Pro：** 使用 UI 流程，参见
   [向导安装](docs/zh-CN/setup/wizard-setup.md)。

## 文档

面向用户：

- [API 索引](docs/api.md)（英文）— MCP、桥接、资源、路由与自动化契约。
- [扩展](docs/extensions.md)（英文）— 嵌入式领域、依赖与工具组激活。
- [故障排查](docs/troubleshooting.md)（英文）— 连接与恢复指南。
- [对话框策略](docs/dialog-policy.md)（英文）— 启动模态框处理与自动化。
- [技能](docs/skills.md)（英文）— 安装到 Unity 项目中的智能体操作手册。
- [版本兼容](docs/versioning.md)（英文）— 版本匹配与不一致时的恢复。

面向贡献者：

- [架构](docs/architecture.md)（英文）— 仓库边界与运行时流程。
- [代码规范](docs/code-conventions.md)（英文）— 非显而易见的 C# 契约。

> 想看看其他 MCP 方案？参见 [Unity MCP 工具对比](docs/mcp-tools-comparison.md)（英文）— Unity Open MCP 与业内其他 MCP 工具 / AI 助手的功能矩阵并排对比。

> 注：除本 README 与 `docs/zh-CN/setup/` 下的安装文档外，其余文档目前仅有英文版。

## Unity Hub Pro

Unity Hub Pro 是 Unity Open MCP 的桌面配套应用。它帮助你管理项目、运行 AI 安装向导，并在一个界面中处理维护者工作流。
[详见文档（英文）](docs/unity-hub-pro.md)。

## 贡献

- 在提 issue 或 pull request 之前请先阅读
  [CONTRIBUTING.md](CONTRIBUTING.md)（英文）。
- [贡献者故障排查](docs/troubleshooting-contributors.md)（英文）涵盖本地测试、桥接与自动化失败。
- [Validation Suite](validation-suite/README.md)（英文）— 用于引导式人工验证的应用；附带可运行的场景包。
- [维护者版本与发布](docs/contributing/versioning.md)（英文）— 同步、标签与发布工作流。

**许可证：** MIT — 见 [LICENSE](LICENSE)。
