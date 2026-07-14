# Scripts

Small Node utilities for repo maintenance and MCP validation. All scripts are dependency-free (Node builtins only) and run from the repo root.

## Prerequisites

Most MCP test scripts call the built CLI at `mcp-server/dist/index.js`. Build first:

```bash
cd mcp-server && npm run build
```

Live suites need a Unity Editor open on the target project with the bridge running. Pass `--project` as an **absolute** path when targeting a project other than `./demo`.

## Version & codegen

| Script | Purpose |
|--------|---------|
| [`sync-version.mjs`](sync-version.mjs) | Keeps version strings in sync from `version.json` (MCP server + bridge + verify trio) or `hub/version.json` (Hub app). Supports `--check`, `bump`, `set`, and `tags`. |
| [`generate-token-estimates.mjs`](generate-token-estimates.mjs) | Generates `packages/bridge/Editor/UI/BridgeToolTokenEstimates.cs` from live MCP tool schemas. `--check` is advisory in CI (`continue-on-error`). |
| [`gen-mcp-coverage-matrix.mjs`](gen-mcp-coverage-matrix.mjs) | Regenerates the per-tool coverage matrix under `specs/execution/M27/`. Fails if any registered tool has no suite owner. |

## MCP test suites

These drive tools through `unity-open-mcp run-tool` (one fresh process per call), exercising the same router stack an MCP client uses.

| Script | Suite | When to run |
|--------|-------|-------------|
| [`mcp-smoke.mjs`](mcp-smoke.mjs) | Quick smoke | Fast health check: bridge ping, read-only probes, a few safe mutations with cleanup (~23 steps). |
| [`mcp-full-test.mjs`](mcp-full-test.mjs) | **S0** — full coverage | Every registered tool at least once; temp fixture under `Assets/MCP_FullTest/`, always cleaned up. |
| [`mcp-behavior.mjs`](mcp-behavior.mjs) | **S1** — behavioral | Strict happy-path tests for tools S0 only reaches via `tolerate` / `reachable`, plus tools absent from S0. |
| [`mcp-headless.mjs`](mcp-headless.mjs) | **S2** — headless | Batch/offline paths with the Editor **closed** on the target project. |
| [`mcp-protocol.mjs`](mcp-protocol.mjs) | **S3** — protocol | Stdio MCP transport: `initialize`, `tools/list`, `tools/call`. Local portion runs without Unity; use `--skip-live` to skip bridge calls. |
| [`mcp-extensions.mjs`](mcp-extensions.mjs) | **S4** — extensions | End-to-end chains per compiled extension pack (NavMesh, Input System, ProBuilder, …). Uncompiled groups skip; compiled groups must pass. |
| [`mcp-sandbox.mjs`](mcp-sandbox.mjs) | **S5** — sandbox | Destructive lifecycle (packages, Hub mutators, builds) on a disposable clone of `demo/`. Editor must not be open on the sandbox. |

Shared helpers for S0–S5 live in [`mcp-test-lib.mjs`](mcp-test-lib.mjs) (not run directly).

### Common flags

Most suites support:

```bash
node scripts/<suite>.mjs --list                  # list steps, don't run
node scripts/<suite>.mjs --project /abs/path   # target project (default: ./demo)
node scripts/<suite>.mjs --only needle         # run steps matching a label substring
node scripts/<suite>.mjs --band A,B            # run named bands only (where applicable)
node scripts/<suite>.mjs --json-out report.json
```

`mcp-smoke.mjs` also accepts `--readonly` to skip mutation steps.

### Suggested order

1. **Smoke** — quick sanity check with the Editor open.
2. **S0 → S1 → S4** — full live coverage with the Editor open.
3. **S3** — protocol layer (`--skip-live` if Unity is down).
4. **S2** — quit the Editor first.
5. **S5** — last; clones the project and runs destructive steps.
