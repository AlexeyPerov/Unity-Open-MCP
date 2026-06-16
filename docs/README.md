# Documentation Index

Use this folder for version-controlled documentation that should stay aligned with the current codebase and provide fast lookup for agents and contributors.

## Core docs

| Document | Purpose |
|---|---|
| [manual-setup.md](manual-setup.md) | Manual MCP setup without Unity Hub Pro (packages, client config, verify). |
| [wizard-setup.md](wizard-setup.md) | Step-by-step setup using the AI Setup wizard in Unity Hub Pro. |
| [architecture.md](architecture.md) | Repo structure, package boundaries, runtime interactions, and routing flows. |
| [tools.md](tools.md) | Toolchain, scripts, and runtime environment requirements by workspace area. |
| [api.md](api.md) | Index for HTTP/MCP/API contracts and protocol surfaces. |

## API and protocol docs

| Document | Purpose |
|---|---|
| `api/bridge-http.md` | Unity bridge HTTP endpoints, request/response envelopes, and error behavior. |
| `api/mcp-tools.md` | MCP tool catalog, route behavior (live/batch/offline), and tool families. |
| `api/resources.md` | MCP resource URIs, payload shapes, and fallback/no-data behavior. |

## Where to look quickly

| Question | Primary doc |
|---|---|
| How does routing choose live vs batch/offline? | `api/mcp-tools.md` |
| Which HTTP endpoints does the Unity bridge expose? | `api/bridge-http.md` |
| What MCP resources are available and what do they return? | `api/resources.md` |
| What env vars are required to run the MCP server? | `tools.md` |
| Which package owns a feature area? | `architecture.md` |
| How do I run tests locally, and what does CI run? | `tools.md` (Testing) |

## Maintenance rules

- Keep docs concise and code-aligned.
- Update only docs affected by the current change.
- If adding a new top-level doc, add it to this index in the same task.
