[English](README.md) · 简体中文

[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/unity-open-mcp/)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=blue 'Unity')](https://unity.com/releases/editor/archive)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

<p align="center">
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="MCP for Unity" width="250">
</p>

# Unity Open MCP

Unity Open MCP 为 AI 智能体提供了一个类型化、带安全门禁的工具接口，用于操作 Unity 项目。

该 MCP 服务器暴露了 **250+ 个工具**，覆盖类型化编辑器工作流、门禁与校验、资源智能分析、诊断，以及嵌入式领域工具组。

## 核心特性

- 安全变更：自动校验、检查点、差异对比、回归检查，以及针对性修复。
- 结构化资源搜索、检视、重新序列化，以及引用分析。
- 实时 Unity 桥接、受支持工具的批量回退，以及离线读取器。
- 类型化编辑器与嵌入式领域工作流，通过按会话划分的工具组呈现。
- Unity Hub Pro：提供引导式安装与维护者工作流。

完整的工具目录与契约请参见 [docs/api/mcp-tools.md](docs/api/mcp-tools.md)（英文）。

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
