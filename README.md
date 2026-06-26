<p align="center">
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="MCP for Unity" width="310">
</p>

| [English](README.md) | [简体中文](docs/i18n/README-zh.md) |
|----------------------|---------------------------------|


[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=blue 'Unity')](https://unity.com/releases/editor/archive)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

# Unity Open MCP

Unity Open MCP connects AI agents to Unity projects with a bridge + gate workflow: make changes, run validation, inspect results, and iterate safely.

The MCP server consist of a total of **160** tools.

Current tool surface from `mcp-server/src/tools/index.ts`:

- Core + gate + validation tools: **16**
- Asset intelligence + senses + discovery + diagnostics tools: **16**
- Typed editor/project tools (core package): **97**
- Optional extension-pack tools: **31**

## Quick setup

1. Use the **AI Setup wizard** in Unity Hub Pro (recommended): [Wizard setup](docs/wizard-setup.md).
2. If you prefer manual setup and client config snippets: [Manual setup](docs/manual-setup.md).
3. Optional: install extension packs for domain-specific workflows: [Extensions](docs/extensions.md).

## Key features

- Safe mutation workflow with gate validation, checkpoints, deltas, regression checks, and targeted fixes.
- Asset intelligence tools including reserialize, structured asset read/search, and reference analysis.
- Live + fallback routing (live Unity bridge, batch mode for supported tools, offline readers where possible).
- Typed Unity tool surface for scenes, GameObjects, components, packages, build settings, profiler controls, and project settings.
- Bundled domain tool groups for Navigation, Input System, ProBuilder, Particle System, and Animation — embedded in the bridge, compiled in automatically when the matching Unity package is present, and surfaced per session via tool groups.
- Unity Hub Pro wizard for guided setup and maintainer workflows.

For the full catalog and contracts, see [docs/api/mcp-tools.md](docs/api/mcp-tools.md).

> Looking at other options? See the [MCP tools for Unity comparison](docs/mcp-tools-comparison.md) — a side-by-side feature matrix of Unity Open MCP and the other MCP tools / AI assistants in the space.

## Documentation

- [Docs index](docs/README.md)
- [Architecture](docs/architecture.md)
- [Wizard setup](docs/wizard-setup.md)
- [Manual setup](docs/manual-setup.md)
- [Extensions](docs/extensions.md)
- [Skills](docs/skills.md)
- [API index](docs/api.md)
- [Bridge HTTP API](docs/api/bridge-http.md)
- [MCP tools API](docs/api/mcp-tools.md)
- [MCP resources API](docs/api/resources.md)
- [Unity Hub Pro](docs/unity-hub-pro.md)

## Contributing

- Open issues for bugs, feature requests, and documentation improvements.
- PRs are welcome for core packages, extension packs, and docs.
- Start with the docs above, then package-level READMEs for local development details.

Helpful resources for contributors or those who would liketo work on their own forls:
- [Validation Suite](validation-suite/README.md) — standalone app for guided manual validation; ships runnable scenario packs (e.g. the `hexa-sort` build-and-validate pack).

**License:** MIT — see [LICENSE](LICENSE).