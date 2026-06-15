# Unity Open MCP (Beta)

MCP/server infrastructure and tooling for Unity projects.
Includes a Unity Hub Pro launcher with wizard setup for this MCP.

## Current features

- Unity Hub Pro desktop app scaffold under `hub/`.
- Bridge package and tests under `packages/bridge/`.
- MCP server workspace under `mcp-server/`.
- Demo and templates for local experimentation and automation.

## Repository layout

- `hub/` - desktop application.
- `packages/` - shared packages and Unity-side bridge code.
- `mcp-server/` - server-side MCP implementation.
- `demo/` - demo fixtures and local validation helpers.
- `templates/` - reusable templates (for example CI workflows).
- `references/` - external reference snapshots used for research.

## Documentation

- Docs index: `docs/README.md`
- Architecture: `docs/architecture.md`
- Tools and dependencies: `docs/tools.md`
- API and protocol contracts: `docs/api.md`
- Bridge HTTP contract: `docs/api/bridge-http.md`
- MCP tool catalog and routing: `docs/api/mcp-tools.md`
- MCP resources: `docs/api/resources.md`

## Local development

Start from the component you are working on:

- Hub app: see `hub/README.md`
- Demo setup: see `demo/README.md`
- Templates usage: see `templates/github-actions/README.md`
