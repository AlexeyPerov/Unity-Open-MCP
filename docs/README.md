# Documentation

Use this index to find setup guides, product docs, and API contracts.

## Start here

- [Wizard setup](wizard-setup.md) — recommended onboarding with Unity Hub Pro.
- [Manual setup](manual-setup.md) — direct MCP setup and client config snippets.
- [Unity Hub Pro](unity-hub-pro.md) — app capabilities, workflows, and maintainer tools.
- [Extensions](extensions.md) — embedded domain tools (compile-gated) and legacy/community packs.
- [Skills](skills.md) — agent playbooks (`SKILL.md`) that ship into a project and guide the AI on how/when to call tools.
- [Unity version compatibility](unity-version-compat.md) — supported versions and CI coverage.
- [MCP tools for Unity comparison](mcp-tools-comparison.md) — side-by-side feature matrix of Unity Open MCP vs. other MCP tools and AI assistants.

## Architecture and APIs

- [Architecture](architecture.md) — repository boundaries and runtime flow.
- [API index](api.md) — contract documentation map.
- [Bridge HTTP API](api/bridge-http.md) — bridge endpoints and envelopes.
- [MCP tools API](api/mcp-tools.md) — tool catalog and route policy.
- [MCP resources API](api/resources.md) — resource URIs and payloads.
- [Code conventions](code-conventions.md) — non-obvious C# decisions (instance IDs, namespace aliasing).
- [Validation Suite](../validation-suite/README.md) — standalone Tauri app for guided milestone manual validation; ships runnable scenario packs (e.g. the `hexa-sort` build-and-validate pack under `scenarios/unity/hexa-sort/`).

## Fast lookup

- Route behavior (live/batch/offline): [api/mcp-tools.md](api/mcp-tools.md)
- Bridge connectivity and `/ping`: [api/bridge-http.md](api/bridge-http.md)
- CI and automation CLI usage: [manual-setup.md](manual-setup.md)
- Package boundaries: [architecture.md](architecture.md)
