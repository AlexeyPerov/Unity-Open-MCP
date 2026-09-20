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
| [`sync-version.mjs`](sync-version.mjs) | Keeps version strings in sync from `version.json` (MCP server + bridge + verify trio) or `hub/version.json` (Hub app). Covers the English setup docs plus their `docs/ru/` and `docs/zh-CN/` mirrors (git-URL pins and npm pins). Supports `--check`, `bump`, `set`, and `tags`. |
| [`switch-project-version.mjs`](switch-project-version.mjs) | The consumer-side mirror of `sync-version.mjs`: moves **another** project's pins onto a release — every `unity-open-mcp@<ver>` npm pin in its agent/MCP client configs plus the `#bridge-v` / `#verify-v` UPM pins in `Packages/manifest.json` and `packages-lock.json`. Finds the Unity project below the path you pass, so a repo root with the Unity project in `Client/` works. Supports `--dry-run`, `--up`, `--depth`, `--keep-lock`, `--json`. |
| [`release.mjs`](release.mjs) | One-shot release: clean-tree gate → token estimates → set trio + Hub → commit → tags → push (trio and Hub tags in separate pushes, ≤3 tags each — GitHub drops tag webhooks above that). Supports `--dry-run`, `--yes`, `--trio-only`, `--hub-only`. See [Maintainer versioning](../docs/contributing/versioning.md#one-shot-release-trio--hub). |
| [`generate-token-estimates.mjs`](generate-token-estimates.mjs) | Generates `packages/bridge/Editor/UI/BridgeToolTokenEstimates.cs` from live MCP tool schemas. `--check` is advisory in CI (`continue-on-error`). |
| [`gen-mcp-coverage-matrix.mjs`](gen-mcp-coverage-matrix.mjs) | Regenerates the internal, gitignored per-tool coverage matrix. Fails if any registered tool has no suite owner. |

### Switch a consuming project onto a release

Preview first, then apply — every rewrite is idempotent, so re-running is a
no-op:

```bash
node scripts/switch-project-version.mjs ~/work/my-game 0.9.0 --dry-run
```

```bash
node scripts/switch-project-version.mjs ~/work/my-game 0.9.0
```

Pass the folder your AI client is opened on, not the Unity folder — the Unity
project is found below it (`my-game/Client/Packages/manifest.json`). If you point
at the Unity project itself, its client configs are one level up: the summary
lists them and `--up 2` includes them.

Markdown and other prose are deliberately left alone, and the script never
touches `$HOME`-scoped client configs. See
[Version compatibility](../docs/versioning.md#switch-a-whole-project-to-a-release).
This remains the maintainer/multi-project path. For the single project currently
open in Unity, the bridge window's **Status → Updates** flow also handles safely
owned home configs and allowlisted agent prose before an atomic UPM update.

## MCP test suites

These drive tools through `unity-open-mcp run-tool` (one fresh process per call), exercising the same router stack an MCP client uses.

| Script | Suite | When to run |
|--------|-------|-------------|
| [`mcp-smoke.mjs`](mcp-smoke.mjs) | Quick smoke | Fast health check: bridge ping, read-only probes, a few safe mutations with cleanup (~23 steps). |
| [`mcp-full-test.mjs`](mcp-full-test.mjs) | **S0** — full coverage | Every registered tool at least once; temp fixture under `Assets/MCP_FullTest/`, always cleaned up. |
| [`mcp-behavior.mjs`](mcp-behavior.mjs) | **S1** — behavioral | Strict happy-path tests for tools S0 only reaches via `tolerate` / `reachable`, plus tools absent from S0. |
| [`mcp-headless.mjs`](mcp-headless.mjs) | **S2** — headless | Batch/offline paths with the Editor **closed** on the target project. |
| [`mcp-protocol.mjs`](mcp-protocol.mjs) | **S3** — protocol | Stdio MCP transport: `initialize`, `tools/list`, `tools/call`. Local portion runs without Unity; use `--skip-live` to skip bridge calls. |
| [`mcp-extensions.mjs`](mcp-extensions.mjs) | **S4** — embedded domains | End-to-end chains per compiled embedded domain (NavMesh, Input System, ProBuilder, …). Uncompiled groups skip; compiled groups must pass. |
| [`mcp-sandbox.mjs`](mcp-sandbox.mjs) | **S5** — sandbox | Destructive lifecycle (packages, Hub mutators, builds) on a disposable clone of `demo/`. Editor must not be open on the sandbox. |

Shared helpers for S0–S5 live in [`mcp-test-lib.mjs`](mcp-test-lib.mjs) (not run directly). Every CLI invocation runs in its own process group and the whole group is killed when the call completes — a timed-out step takes its headless Unity child with it instead of orphaning an Editor that holds the project lock until its own 10-minute timeout.

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

### Nested batch schema generation

After changing MCP tool schemas, run `node scripts/generate-batch-schemas.mjs`
as well as `node scripts/generate-token-estimates.mjs`. The former derives
bridge-side preflight constraints from `ALL_TOOLS`; it does not execute tools.
`node scripts/generate-batch-schemas.mjs --check` detects generated drift.

### Live input simulation regression replay

With the demo Editor open and `mcp-server` built, run
`node scripts/mcp-input-simulation.mjs --fixture frames` and
`node scripts/mcp-input-simulation.mjs --fixture pointer`.
Use `--project <absolute-path>` for another project and `--json-out <path>` to
save the MCP envelopes. The first fixture samples the actual gameplay player
loop; the second checks uGUI event delivery and interaction reporting. Both
require uGUI and Input System. Run serially, without another live test session.
The replay temporarily enters play mode (including from an unsaved scene), never
saves scenes, and restores its initial play/edit state and disposable fixtures.

Project command catalog coverage: S0 `--band A --only project_commands` runs a
read-only catalog probe without scene cleanup; S3 band T checks bounded listing
and exact description over stdio. Use the demo fixture for a non-empty catalog.

The S3 project-command check lists and describes the live catalog. When the known
read-only demo fixture is present, it also invokes that fixture and checks missing
argument rejection in the same stdio session without refreshing the tool list.

The S0 `jobs_list` probe checks the local job surface without creating work.
Use `node scripts/mcp-full-test.mjs --only jobs_list` for that read-only probe.
Job state-machine and adapter tests run in the MCP package unit suite.
