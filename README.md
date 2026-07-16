[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/unity-open-mcp/)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=blue 'Unity')](https://unity.com/releases/editor/archive)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

<p align="center">
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="MCP for Unity" width="250">
</p>

# Unity Open MCP

Unity Open MCP gives AI agents a typed, safety-gated tool surface for Unity
projects.

The MCP server exposes **250+ tools** across typed editor workflows, gate and
validation, asset intelligence, diagnostics, and embedded domain groups.

## Key features

- Safe mutations with automatic validation, checkpoints, deltas, regression
  checks, and targeted fixes.
- Structured asset search, inspection, reserialization, and reference analysis.
- Live Unity bridge, batch fallback for supported tools, and offline readers.
- Typed editor and embedded-domain workflows, surfaced through per-session tool
  groups.
- Unity Hub Pro for guided setup and maintainer workflows.

For the full catalog and contracts, see [docs/api/mcp-tools.md](docs/api/mcp-tools.md).

## Quick setup

Requires **Unity 2022.3 LTS or newer**.

1. **Easiest (AI agent):** paste this prompt into your AI client (Cursor, Claude, …) and let it install for you:

```text
Install Unity Open MCP in this Unity project by following
https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/agent-setup.md
exactly. Do every agent step yourself; stop and tell me only when a human action is required.
If this monorepo is already open locally, read docs/setup/agent-setup.md from disk instead of fetching.
```

Full procedure: [Agent setup](docs/setup/agent-setup.md).

2. **Manual:** edit the package and MCP client configuration yourself with
   [Manual setup](docs/setup/manual-setup.md).
3. **Local checkout:** build and run the repository with
   [Development setup](docs/setup/development-setup.md).
4. **Unity Hub Pro:** use the UI flow in
   [Wizard setup](docs/setup/wizard-setup.md).

## Documentation

简体中文文档：[README.zh-CN.md](README.zh-CN.md)（Chinese translation of this README and the setup guides).

For users:

- [API index](docs/api.md) — MCP, bridge, resource, routing, and automation contracts.
- [Extensions](docs/extensions.md) — embedded domains, dependencies, and tool-group activation.
- [Troubleshooting](docs/troubleshooting.md) — connectivity and recovery guidance.
- [Dialog policy](docs/dialog-policy.md) — startup modal handling and automation.
- [Skills](docs/skills.md) — agent playbooks installed into Unity projects.
- [Version compatibility](docs/versioning.md) — version matching and mismatch recovery.

For contributors:

- [Architecture](docs/architecture.md) — repository boundaries and runtime flow.
- [Code conventions](docs/code-conventions.md) — non-obvious C# contracts.

> Would like to see other MCP options? See the [MCP tools for Unity comparison](docs/mcp-tools-comparison.md) — a side-by-side feature matrix of Unity Open MCP and the other MCP tools / AI assistants in the space.

## Unity Hub Pro

Unity Hub Pro is the desktop companion app for Unity Open MCP. It helps you manage projects, run the AI Setup wizard, and handle maintainer workflows from one UI.
[See docs for details.](docs/unity-hub-pro.md)

## Contributing

- Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening an issue or pull
  request.
- [Contributor troubleshooting](docs/troubleshooting-contributors.md) covers
  local test, bridge, and automation failures.
- [Validation Suite](validation-suite/README.md) — app for guided manual validation; ships runnable scenario packs.
- [Maintainer versioning and releases](docs/contributing/versioning.md) — synchronization, tags, and release workflows.

**License:** MIT — see [LICENSE](LICENSE).