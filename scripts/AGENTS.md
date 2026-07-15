# Scripts rules

## Scope

Rules for `scripts/` — version sync, token-estimate codegen, and MCP test
suites. Root `AGENTS.md` also applies. Human overview:
[`scripts/README.md`](README.md).

## Generated outputs

- Never hand-edit generated targets. Run the owning script instead.
- Version strings: `node scripts/sync-version.mjs` (shared trio) or
  `--hub` for Unity Hub Pro. Canonical contract:
  [Maintainer versioning](../docs/contributing/versioning.md).
- Token estimates: `node scripts/generate-token-estimates.mjs` writes
  `packages/bridge/Editor/UI/BridgeToolTokenEstimates.cs`. Regenerate after
  MCP tool schema, catalog, or group changes; `--check` is advisory in CI.
- Coverage matrix (`gen-mcp-coverage-matrix.mjs`) is internal/gitignored.

## MCP suite selection

| Suite | Script | Use when |
|---|---|---|
| Smoke | `mcp-smoke.mjs` | Fast live health check |
| S0 | `mcp-full-test.mjs` | Registration/reachability for every tool |
| S1 | `mcp-behavior.mjs` | Strict behavioral paths |
| S2 | `mcp-headless.mjs` | Batch/offline with Editor closed |
| S3 | `mcp-protocol.mjs` | Stdio protocol |
| S4 | `mcp-extensions.mjs` | Embedded-domain chains |
| S5 | `mcp-sandbox.mjs` | Destructive lifecycle on a disposable clone |

Shared helpers live in `mcp-test-lib.mjs` (not run directly). Catalog detail:
`docs/troubleshooting-contributors.md#mcp-test-suite-catalog`. Build
`mcp-server/` before live suites. Default project is `./demo`.

## Same-task mirrors

- Script behavior or flag changes that affect contributor workflows: update
  `scripts/README.md` and, when applicable,
  `docs/troubleshooting-contributors.md`.
- Suite ownership of a new tool: keep the coverage matrix regenerable (no
  orphan registered tools).
