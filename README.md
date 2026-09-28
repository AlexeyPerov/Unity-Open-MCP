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

Unity Open MCP is one of the broadest open-source MCP implementations for Unity,
with **270+ typed tools** in a production-oriented automation stack. Safety-gated
mutations, built-in validation, and live, headless, and offline execution support
reliable work on real projects—from asset intelligence and editor authoring to
closed-loop game testing, diagnostics, CI, and package-gated Unity domains.

---
Part of Open MCP toolset
---
[![Unity Open MCP](https://img.shields.io/badge/Unity-Open%20MCP-000000?style=flat&logo=unity&logoColor=white)](https://github.com/AlexeyPerov/Unity-Open-MCP) [![Unreal Open MCP](https://img.shields.io/badge/Unreal-Open%20MCP-0E1128?style=flat&logo=unrealengine&logoColor=white)](https://github.com/AlexeyPerov/Unreal-Open-MCP) [![Godot Open MCP](https://img.shields.io/badge/Godot-Open%20MCP-478CBF?style=flat&logo=godotengine&logoColor=white)](https://github.com/AlexeyPerov/Godot-Open-MCP)
---

## Key features

### Safe typed authoring

Edit GameObjects, scenes, prefabs, materials, packages, and package-gated
domains such as NavMesh, Input System, Cinemachine, Timeline, and Shader Graph.
Mutations run `checkpoint → mutate → validate → delta`, with regression checks
and targeted fixes.

> **User:** Remove that prefab.<br>
> **Agent:** The gate preview found new missing references in `Level1` and
> `SpawnPoint`, so I stopped before committing a broken project.

### Test and observe

Run Edit/Play Mode tests, inspect the console, capture screenshots, profiler and
memory data, and pull events. Input simulation closes the gameplay loop: probe
interactables, click / drag / swipe, advance frames, then verify visually.

### Live, batch, or offline

Prefer the live Editor, fall back to an exact-version headless Editor for
supported tools, and read assets or compile diagnostics from disk when Unity is
closed. Structured search, reserialization, and reference / dependency analysis
remain available where their route supports it.

### Extend and automate

Unity projects can expose typed, discoverable commands without an MCP server
release. Explicitly async operations run as observable jobs with progress,
idempotency, retained results, and truthful cancellation. The CLI and CI flows
add health checks, verify baselines, and regression gates.

See [Project commands](docs/api/project-commands.md) and
[Asynchronous jobs](docs/api/jobs.md).

### Discover only what you need

Only `core` and `gate-and-verify` are visible by default; activate the other
domains on demand. Runtime capabilities provide exact schemas, routes, and
availability, while project skills teach agents the mutate → gate → fix loop.

### Install and maintain

One command installs matching package pins, MCP configuration, and the agent
skill. Team and monorepo layouts default to committable, machine-independent
configuration. Coordinated update flows cover the MCP server, Unity packages,
and the optional [Unity Hub Pro](docs/unity-hub-pro.md) desktop app.

More example prompts: [docs/api/mcp-tools.md](docs/api/mcp-tools.md#example-prompts).
Full catalog and contracts: [docs/api/mcp-tools.md](docs/api/mcp-tools.md).

## Quick setup

Requires **Unity 2022.3 LTS or newer** and **Node.js 18+** for the MCP server.

1. **CLI — recommended:** open a terminal in the Unity project and run:

   ```bash
   npx -y unity-open-mcp@latest setup --client cursor
   ```

   Use `claude`, `zcode`, `vscode`, `codex`, `opencode`, or `agents` for another
   supported writer. The command installs matching pins, project config, and the
   bundled skill; see [Manual setup](docs/setup/manual-setup.md) for details.
2. **Unity Hub Pro:** use the graphical flow in
   [Wizard setup](docs/setup/wizard-setup.md).
3. **Manual:** copy the package and client configuration yourself from
   [MCP client configuration](docs/setup/client-configuration.md).
4. **Local checkout:** build and run the repository with
   [Development setup](docs/setup/development-setup.md).
5. **Experimental — AI agent:** paste the prompt below into your AI client.

<details>
<summary>Agent installation prompt</summary>

```text
Install Unity Open MCP in this Unity project by following
https://raw.githubusercontent.com/AlexeyPerov/Unity-Open-MCP/master/docs/setup/agent-setup.md
exactly (fetch it fresh; do not improvise from memory).
Resolve this project's absolute Unity root and my client, then run
npx -y unity-open-mcp@latest setup --project <absolute-project> --client <id>.
Let that package choose all version pins and copy its bundled SKILL.md; do not
invent versions, rewrite the skill, or call generate_skill. Do every agent step
yourself and stop only when human action is required. If this monorepo is already
open locally, read docs/setup/agent-setup.md from disk instead of fetching it.
```

</details>

Full procedure: [Agent setup](docs/setup/agent-setup.md).
For teams and monorepos, use a
[portable MCP configuration](docs/setup/portable-config.md) that can be committed
without per-developer absolute paths.

## Documentation

For users:

- [API index](docs/api.md) — MCP, bridge, resource, routing, and automation contracts.
- [Extensions](docs/extensions.md) — embedded domains, dependencies, and tool-group activation.
- [Troubleshooting](docs/troubleshooting.md) — connectivity and recovery guidance.
- [Dialog policy](docs/dialog-policy.md) — startup modal handling and automation.
- [Skills](docs/skills.md) — agent playbooks installed into Unity projects.
- [Version compatibility](docs/versioning.md) — version matching and mismatch recovery.
- [Updating](docs/updating.md) — Hub, MCP, Unity package, and air-gapped update paths.

For contributors:

- [Architecture](docs/architecture.md) — repository boundaries and runtime flow.
- [Code conventions](docs/code-conventions.md) — non-obvious C# contracts.

> Would like to see other MCP options? See the [MCP tools for Unity comparison](docs/mcp-tools-comparison.md) — a side-by-side feature matrix of Unity Open MCP and the other MCP tools / AI assistants in the space.

## Contributing

- Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening an issue or pull
  request.
- [Contributor troubleshooting](docs/troubleshooting-contributors.md) covers
  local test, bridge, and automation failures.
- [Maintainer versioning and releases](docs/contributing/versioning.md) — synchronization, tags, and release workflows.

**License:** MIT — see [LICENSE](LICENSE).
