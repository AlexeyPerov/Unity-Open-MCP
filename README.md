# Unity Open MCP

[![Docs](https://img.shields.io/badge/Docs-unity--mcp-4f46e5)](https://alexeyperov.github.io/unity-open-mcp/)
[![](https://badge.mcpx.dev?status=on 'MCP Enabled')](https://modelcontextprotocol.io/introduction)
[![](https://img.shields.io/badge/Unity-000000?style=flat&logo=unity&logoColor=white 'Unity')](https://unity.com/releases/editor/archive)
[![](https://img.shields.io/badge/Node.js-339933?style=flat&logo=nodedotjs&logoColor=white 'Node.js')](https://nodejs.org/en/download/)
[![](https://img.shields.io/github/stars/AlexeyPerov/Unity-Open-MCP 'Stars')](https://github.com/AlexeyPerov/Unity-Open-MCP/stargazers)
[![](https://img.shields.io/github/last-commit/AlexeyPerov/Unity-Open-MCP 'Last Commit')](https://github.com/AlexeyPerov/Unity-Open-MCP/commits/master)
[![](https://img.shields.io/badge/License-MIT-red.svg 'MIT License')](https://opensource.org/licenses/MIT)

---
### Open MCP Tools
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

| [🇺🇸 English](README.md) | [🇨🇳 简体中文](README.zh-CN.md) | [🇷🇺 Русский](README.ru.md) |
|-------------------------|--------------------------------|------------------------------|

<p align="center">
  <img src="hub/src-tauri/icons/Square310x310Logo.png" alt="Unity Open MCP" width="250">
</p>

Unity Open MCP gives AI agents a typed, safety-gated tool surface for Unity
projects.

The MCP server exposes **250+ tools** across typed editor workflows, gate and
validation, asset intelligence, diagnostics, and embedded domain groups.

## Key features

### Asset intelligence

Structured search, inspection, reserialization, and reference / dependency
analysis — including offline readers when Unity is closed.

> **Example:** "Find all Prefabs that reference `PlayerController` and summarize
> inbound dependencies."

### Live bridge, batch fallback, offline reads

Prefer the live Editor; fall back to headless batch for supported tools; read
assets and compile errors from disk when needed.

> **Example:** "Bridge is offline — show me the latest compile errors from the
> Editor log."

### Typed editor + embedded domains

GameObjects, scenes, prefabs, materials, packages, plus package-gated domains
(NavMesh, Input System, Cinemachine, Timeline, Shader Graph, and more).

> **Example:** "Activate `cinemachine` and create a follow camera for the Player."

### Agent senses and diagnostics

Test runner, screenshots, console, profiler / memory snapshots, and event pull
for closed-loop agent workflows.

> **Example:** "Run Play Mode tests for the Combat assembly and capture a
> Game-view screenshot on failure."

### Session tool groups and skills

Default groups stay small; activate domains on demand. Project skills teach
agents the mutate → gate → fix loop.

> **Example:** "Reset tool groups, then activate only `core` and
> `gate-and-verify`."

### CI, baselines, and regression

Scan projects, create verify baselines, and fail automation when new blocking
issues appear — live or via batch/CLI.

> **Example:** "Create a verify baseline for this project, then report any new
> blocking issues since the last baseline."

### Unity Hub Pro

Optional desktop app for project management, AI Setup wizard, and maintainer
actions — not required for the MCP path.

> **Example:** use the Hub **AI** action on a project row (see
> [Unity Hub Pro](docs/unity-hub-pro.md)).

### Safety-gated mutations

Mutations run `checkpoint → mutate → validate → delta`, with regression checks
and targeted fixes — so agents can stop before a “successful” edit leaves the
project broken.

> **User:** Remove that prefab.  
> **Agent:** Checking impact for `Enemy`…  
> **Gate:** Deleting it would introduce new missing references on `Level1` and
> `SpawnPoint`.  
> **Agent:** Unity Open MCP flagged that regression in the gate preview. I am
> **not** removing the prefab without your confirmation — those scenes would
> break. Want me to delete it and then fix the references, or leave it?

More example prompts: [docs/api/mcp-tools.md](docs/api/mcp-tools.md#example-prompts).
Full catalog and contracts: [docs/api/mcp-tools.md](docs/api/mcp-tools.md).

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
