[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=blue 'Unity')](https://unity.com/releases/editor/archive)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

<p align="center">
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="MCP for Unity" width="250">
</p>

# Unity Open MCP

Unity Open MCP connects AI agents to Unity projects with a bridge + gate workflow: make changes, run validation, inspect results, and iterate safely.

Requires **Unity 2022.3 LTS or newer**.

The MCP server consists of a total of **191** tools.

Current tool surface from `mcp-server/src/tools/index.ts`:

- Core + gate + validation tools: **16**
- Asset intelligence + senses + discovery + diagnostics + meta tools: **23**
- Typed editor/project tools (core package): **103**
- Optional extension-pack tools: **55**

## Quick setup

Use any of this options:

1. Install Unity Hub Pro and use its **AI Setup wizard** (simplest, no console needed): see [Wizard setup](docs/wizard-setup.md).

![plot](./screenshots/hub-wizard-7.png)

2. If you prefer manual setup: see [Manual setup](docs/manual-setup.md).
3. If prefer cloning this repo and working with it directly: see [Development setup](docs/development-setup.md). Fits contributor workflow.

Optional: install extension packs for domain-specific workflows: [Extensions](docs/extensions.md).

## Key features

- Safe mutation workflow with gate validation, checkpoints, deltas, regression checks, and targeted fixes.
- Asset intelligence tools including reserialize, structured asset read/search, and reference analysis.
- Live + fallback routing (live Unity bridge, batch mode for supported tools, offline readers where possible).
- Typed Unity tool surface for scenes, GameObjects, components, packages, build settings, profiler controls, and project settings.
- Bundled domain tool groups for Navigation, Input System, ProBuilder, Particle System, Animation, Splines, Lighting, Audio, UI, Constraints & LOD, and Terrain — embedded in the bridge, compiled in automatically when the matching Unity package is present (or unconditionally for built-in modules like Lighting, Audio, UI, Constraints & LOD, and Terrain), and surfaced per session via tool groups.
- Unity Hub Pro wizard for guided setup and maintainer workflows.

For the full catalog and contracts, see [docs/api/mcp-tools.md](docs/api/mcp-tools.md).

> Would like to see other MCP options? See the [MCP tools for Unity comparison](docs/mcp-tools-comparison.md) — a side-by-side feature matrix of Unity Open MCP and the other MCP tools / AI assistants in the space.

## Documentation

- [Architecture](docs/architecture.md) — repository boundaries and runtime flow.
- [Skills](docs/skills.md) — agent playbooks (`SKILL.md`) shipped into a project.
- [API index](docs/api.md) — contract documentation map.
- [Bridge HTTP API](docs/api/bridge-http.md) — bridge endpoints and envelopes.
- [MCP resources API](docs/api/resources.md) — resource URIs and payloads.
- [Code conventions](docs/code-conventions.md) — non-obvious C# decisions (instance IDs, namespace aliasing).
- [Versioning](docs/versioning.md) — how the shared server/bridge/verify version and the Hub app version are managed, bumped, and kept in sync; the runtime compatibility check.

## Unity Hub Pro

Unity Hub Pro is the desktop companion app for Unity Open MCP. It helps you manage projects, run the AI Setup wizard, and handle maintainer workflows from one UI.
[See docs for details.](docs/unity-hub-pro.md)

## Contributing

- Feel free to open issues for bugs, feature requests, and documentation improvements.
- PRs are welcome. Start with the docs above, then package-level READMEs for local development details.

Helpful resources for contributors or those who would like to work on their own forks:
- [Validation Suite](validation-suite/README.md) — app for guided manual validation; ships runnable scenario packs.

**License:** MIT — see [LICENSE](LICENSE).