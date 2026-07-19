[English](../../setup/development-setup.md) · 简体中文

# 开发安装

本文说明如何为本地检出配置开发环境。常规安装见[手动安装](manual-setup.md)与[Agent 安装](agent-setup.md)。

## 环境要求

- Unity 2022.3 LTS 或更高版本
- Node.js 18 或更高版本
- 一个支持 stdio 服务器的 MCP 客户端

## 1. 安装本地 Unity 包

将目标 Unity 项目的 `Packages/manifest.json` 指向本地检出：

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../unity-open-mcp/packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../unity-open-mcp/packages/verify"
  }
}
```

根据你的目录布局调整相对路径。位于本 monorepo 内部的 Unity 项目通常可以使用：

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
  }
}
```

Unity 在刷新/获得焦点后重新编译包源码。各包的本地校验要求见各自包的 `AGENTS.md` 与 README。

## 2. 构建并配置 MCP 服务器

```bash
cd mcp-server
npm install
npm run build
```

在 [MCP 客户端配置](client-configuration.md)中使用本地检出命令，并保留你客户端对应的配置结构：

```json
{
  "command": "node",
  "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

启动对话框策略与 macOS 辅助功能要求由[对话框策略](../../dialog-policy.md)维护。

## 3. 可选嵌入式领域

领域工具随 bridge 一起发布。匹配的 Unity 依赖让按包门控的领域编译进来；会话激活控制大多数工具组是否对 MCP 客户端可见。

权威依赖目录、清单示例与激活表请见[扩展](../../extensions.md)。社区包编写在[贡献 — 扩展](../../contributing/extensions.md)中有独立的契约。

## 4. 启动并校验

1. 打开目标 Unity 项目。
2. 等待脚本编译完成。
3. 重启 MCP 客户端，使其重新加载本地命令。
4. 调用 `unity_open_mcp_ping` 或 `unity_open_mcp_capabilities`。

bridge、监听器、编译与 test-worker 的恢复请见[贡献者故障排查](../../troubleshooting-contributors.md)。

## 社区包工作流

`packages/extensions/` 留作独立的社区领域包。随附领域内嵌于 bridge，不使用独立的扩展包条目。

本地社区包可以用其自己的 UPM id 安装：

```json
{
  "dependencies": {
    "com.example.my-mcp-ext": "file:../../my-mcp-ext"
  }
}
```

编译门禁、工具注册、测试与文档归属请遵循[贡献 — 扩展](../../contributing/extensions.md)。

## 维护者发布

npm 包从 `mcp-server/` 发布；bridge 和 verify 通过 git 标签消费。Unity Hub Pro 独立发布。

[维护者版本与发布](../../contributing/versioning.md)是以下内容的权威来源：

- 版本来源与生成目标；
- 同步与 CI 漂移检查；
- 共享三件套与 Hub 版本提升命令；
- 标签命名空间与发布工作流；
- GitHub Release 对发布说明的所有权。

Hub 维护者面板可运行构建/测试、npm dry-run/publish 以及共享版本同步脚本。它使用维护者现有的 npm 凭证，不创建提交或标签。

## 相关文档

- [MCP 客户端配置](client-configuration.md)
- [向导安装](wizard-setup.md)
- [架构](../../architecture.md)
- [MCP 工具 API](../../api/mcp-tools.md)
