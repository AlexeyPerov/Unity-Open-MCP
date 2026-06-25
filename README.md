# Unity Open MCP

Unity Open MCP connects AI agents to Unity projects with a bridge + gate workflow: make changes, run validation, inspect results, and iterate safely.

The MCP server is published to npm as [`unity-open-mcp`](https://www.npmjs.com/package/unity-open-mcp). Most users install with `npx -y unity-open-mcp@latest` and do not need to clone this repo.

## Key features

- Safe mutation workflow with gate validation, checkpoints, deltas, regression checks, and targeted fixes.
- Asset intelligence tools including reserialize, structured asset read/search, and reference analysis.
- Live + fallback routing (live Unity bridge, batch mode for supported tools, offline readers where possible).
- Typed Unity tool surface for scenes, GameObjects, components, packages, build settings, profiler controls, and project settings.
- Bundled domain tool groups for Navigation, Input System, ProBuilder, Particle System, and Animation — embedded in the bridge, compiled in automatically when the matching Unity package is present, and surfaced per session via tool groups.
- Unity Hub Pro wizard for guided setup and maintainer workflows.

## MCP tools at a glance

Current tool surface from `mcp-server/src/tools/index.ts`:

- Core + gate + validation tools: **16**
- Asset intelligence + senses + discovery + diagnostics tools: **16**
- Typed editor/project tools (core package): **97**
- Optional extension-pack tools: **31**
- Total MCP tools: **160**

For the full catalog and contracts, see [docs/api/mcp-tools.md](docs/api/mcp-tools.md).

## Quick setup

1. Use the **AI Setup wizard** in Unity Hub Pro (recommended): [Wizard setup](docs/wizard-setup.md).
2. If you prefer manual setup and client config snippets: [Manual setup](docs/manual-setup.md).
3. Optional: install extension packs for domain-specific workflows: [Extensions](docs/extensions.md).

## Documentation

- [Docs index](docs/README.md)
- [Architecture](docs/architecture.md)
- [Unity Hub Pro](docs/unity-hub-pro.md)
- [Wizard setup](docs/wizard-setup.md)
- [Manual setup](docs/manual-setup.md)
- [Extensions](docs/extensions.md)
- [API index](docs/api.md)
- [Bridge HTTP API](docs/api/bridge-http.md)
- [MCP tools API](docs/api/mcp-tools.md)
- [MCP resources API](docs/api/resources.md)
- [Validation Suite](validation-suite/README.md) — standalone app for guided milestone manual validation.
- [Benchmarks](benchmarks/README.md) — release-quality prompt-template benchmark suite.

## Contributing

- Open issues for bugs, feature requests, and documentation improvements.
- PRs are welcome for core packages, extension packs, and docs.
- Start with the docs above, then package-level READMEs for local development details.