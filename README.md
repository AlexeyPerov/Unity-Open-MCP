# Unity Open MCP (Beta)

MCP/server infrastructure and tooling for Unity projects.

The MCP server is published to npm as [`unity-open-mcp`](https://www.npmjs.com/package/unity-open-mcp) — most users point their AI client at `npx -y unity-open-mcp@latest` and never clone this repo. See [Manual setup](docs/manual-setup.md) for the config snippets.

## Current features

- MCP server workspace under [mcp-server/](mcp-server/), published to npm as `unity-open-mcp` (`npx -y unity-open-mcp@latest`).
- Bridge package and tests under [packages/](packages/).
- Unity Hub Pro desktop app scaffold under [hub/](hub/). The hub manages Unity projects, UPM packages, Open-MCP repositories, and arbitrary folders — each classified automatically and given a type-specific settings popup, git status view, and line counter. The Open-MCP settings popup doubles as a maintainer panel (build / test / version bump / publish dry-run / publish).
- Demo and templates for local experimentation and automation under [demo/](demo/) and [templates/](templates/).

## Repository layout

- [hub/](hub/) — desktop application.
- [packages/](packages/) — shared packages and Unity-side bridge code.
- [mcp-server/](mcp-server/) — server-side MCP implementation.
- [demo/](demo/) — demo fixtures and local validation helpers.
- [templates/](templates/) — reusable templates (for example CI workflows).
- [references/](references/) — external reference snapshots used for research.

## Documentation

- [Manual setup (no Hub)](docs/manual-setup.md) — install packages and MCP config by hand.
- [Wizard setup (Unity Hub Pro)](docs/wizard-setup.md) — guided AI Setup wizard walkthrough.
- [Docs index](docs/README.md)
- [Architecture](docs/architecture.md)
- [Tools and dependencies](docs/tools.md)
- [API and protocol contracts](docs/api.md)
- [Bridge HTTP contract](docs/api/bridge-http.md)
- [MCP tool catalog and routing](docs/api/mcp-tools.md)
- [MCP resources](docs/api/resources.md)

## Local development

Start from the component you are working on:

- Hub app: see [hub/README.md](hub/README.md)
- Demo setup: see [demo/README.md](demo/README.md)
- Templates usage: see [templates/github-actions/README.md](templates/github-actions/README.md)