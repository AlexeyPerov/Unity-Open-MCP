[English](../versioning.md) · 简体中文

# 版本兼容性

npm 服务器、bridge 与 verify 包作为同一版本集合发布，应使用相同的
`X.Y.Z`。Unity Hub Pro 独立发布。包含离线安装在内的完整顺序见
[更新指南](../updating.md)。

## 查看运行版本

```bash
npx unity-open-mcp status
npx unity-open-mcp --version
```

服务器在首次成功连接时会提示不兼容，但不会阻止连接。常规升级请先运行
`unity-open-mcp update`，再统一更新 bridge、verify 与 MCP 客户端中的 npm 锁定。

## 从 bridge 窗口更新已打开的项目

在 Unity 中打开 **Tools → Unity Open MCP Bridge → Status → Updates**。
依次点击 **Check latest**、审核 **Preview**，然后点击 **Apply**。确认前不会
写入文件；预览会列出项目级与主目录 MCP 配置、智能体文档/示例，以及
bridge + verify 包步骤。

只有当 `UNITY_PROJECT_PATH`（或确定性 bridge 端口）唯一指向当前项目，且
同一文件未配置其他项目时，才会改写主目录配置。配置和文档首次写入前会
创建 `.bak`。bridge 与 verify 通过一次 Unity Package Manager 请求更新。
embedded/`file:` 开发安装会禁用包步骤，避免覆盖本地检出。应用后等待 Unity
重新加载，重启 MCP/AI 客户端，并运行 `status` 或 `ping`。

智能体可使用 `typed-editor` 组中的 `unity_open_mcp_upgrade` 预览同一计划。
`dry_run` 默认为 `true`；只有审核报告后才设为 `false`。显式传入
`target_version: "X.Y.Z"` 可选择已发布的旧版本，而无需查询 npm 最新版本。

## 从检出更新其他项目

维护多个项目时使用仓库脚本：

```bash
node scripts/switch-project-version.mjs /path/to/my-game 1.2.3 --dry-run
node scripts/switch-project-version.mjs /path/to/my-game 1.2.3
```

该脚本更新项目配置与 UPM 锁定，但有意不修改文档和 `$HOME` 配置。对于当前
打开的项目，请由 bridge 窗口安全处理这些位置。

## Unity 兼容性

bridge 与 verify 要求 Unity 2022.3 LTS 或更高版本（包括 Unity 6）。Node
服务器不依赖 Editor 版本。Unity Hub Pro 使用独立版本与发布节奏，其更新不会
修改 MCP 配置或 Unity 项目文件。
