# Unity Open MCP — CI templates

Provider-agnostic CI templates for running Unity health checks, verify scans, and regression gates in standard CI. These templates target a **game project** that has Unity Open MCP installed — they are meant to be copied into your game repo and adjusted, not run from this toolkit repo.

## What the CLI surface gives you

The `unity-open-mcp` CLI ships six automation commands (see `unity-open-mcp --help`):

| Command | Purpose | Exit codes |
|---|---|---|
| `wait-for-ready` | Poll the bridge until Unity is responsive | 0 ready / 3 timeout |
| `run-tool <name>` | Invoke any MCP tool from CI | 0 ok / 1 tool error |
| `stream-events` | Tap bridge console logs to the CI log | 0 ok / 3 unreachable |
| `verify [paths]` | Run a verify scan (scan_paths / validate_edit / scan_all) | 0 / 1 warnings / 2 errors / 3 timeout |
| `baseline create\|update` | Create or refresh the regression baseline | 0 ok / 2 error / 3 timeout |
| `regression check` | Compare current scan against the baseline | 0 ok / 2 regression / 3 timeout |

### Exit-code contract

| Code | Meaning | CI behavior |
|---|---|---|
| 0 | Success — no issues / no regression | continue |
| 1 | Warnings only — below the fail threshold | continue (advisory) |
| 2 | Errors — issues at/above threshold, or a regression | **fail the job** |
| 3 | Timeout — bridge unreachable or a call timed out | **fail the job** |

## Prerequisites

1. A Unity Editor installed on the runner (self-hosted or a Unity-licensed cloud runner). The bridge can run against an already-open Editor, or the CLI falls back to headless batch mode for scan/regression tools.
2. `UNITY_PROJECT_PATH` pointing at your game project, and `UNITY_PATH` pointing at the Unity Editor executable (for batch fallback).
3. The `unity-open-mcp` package available — either install it (`npm i -D unity-open-mcp`) or use `npx`.

## Templates

- [`github-actions/unity-verify.yml`](github-actions/unity-verify.yml) — a GitHub Actions workflow with three jobs: health check, verify-on-PR, regression-on-main.
- [`gitlab-ci/unity-verify.yml`](gitlab-ci/unity-verify.yml) — the same three jobs as a GitLab CI pipeline.

Copy the relevant file into your repo, adjust the project path / Unity version, and wire it to your trigger of choice.

## Typical pipeline shape

```text
┌─ pull request ─────────────────────────────┐
│  wait-for-ready  →  verify (changed paths) │   exit 2 fails the PR
└─────────────────────────────────────────────┘

┌─ push to main ──────────────────────────────────────────────┐
│  wait-for-ready  →  regression check  →  baseline update    │   keeps the baseline fresh
└──────────────────────────────────────────────────────────────┘
```

The baseline file (`CI/unity-open-mcp-baseline.json` by default) is committed to your repo. `baseline update` runs on main after a regression check passes, so the next PR is compared against the known-good state.
