# Unity Agent Verify — CI Templates

Copy-paste GitHub Actions workflow for running Unity Agent Verify **batch** scans, baselines, and regression checks without an open Unity Editor.

See also: [packages/verify.md](../../specs/packages/verify.md) §VerifyBatchEntry, [packages/mcp-server.md](../../specs/packages/mcp-server.md), [architecture/mcp-tools.md](../../specs/architecture/mcp-tools.md) §M5.

## Files

| File | Purpose |
|---|---|
| `unity-agent-verify.yml` | Reusable workflow template: `scan_all`, `baseline_create`, `regression_check` jobs. |

## Quick start

1. Copy `unity-agent-verify.yml` into your project repo's `.github/workflows/`.
2. Set `UNITY_PROJECT_PATH` to your Unity project root (if it's a subdirectory).
3. Set `UNITY_PATH` to the Unity Editor executable on the runner.
4. Commit a baseline at `CI/unity-agent-baseline.json` (run `baseline_create` once via `workflow_dispatch`).
5. PRs will now run regression checks and full scans automatically.

## Required environment

| Variable | Required | Description |
|---|---|---|
| `UNITY_PROJECT_PATH` | yes | Target Unity project root (relative or absolute). |
| `UNITY_PATH` | yes | Unity Editor executable path for batch mode. |
| `UNITY_LICENSE` | hosted only | Unity license file content (game-ci strategy B). |
| `UNITY_EMAIL` | hosted only | Unity account email (game-ci license activation). |
| `UNITY_PASSWORD` | hosted only | Unity account password (game-ci license activation). |

## How batch verification works

All three M5 tools invoke the same headless entry point:

```bash
Unity -batchmode -quit -nographics \
  -projectPath "$UNITY_PROJECT_PATH" \
  -executeMethod UnityAgentVerify.Batch.VerifyBatchEntry.Run \
  -- <operation> [--flag value ...]
```

| Operation | Key flags | Exit code |
|---|---|---|
| `scan_all` | `--fail-on-severity`, `--platform-profile`, `--output-path` | `0` pass / `1` threshold exceeded |
| `baseline_create` | `--baseline-path`, `--platform-profile` | `0` success / `1` failure |
| `regression_check` | `--baseline-path`, `--regression-threshold`, `--platform-profile` | `0` no regression / `1` regression |

CI shells gate on exit status — no JSON parsing required. The JSON report (machine-readable, written between `---UNITY_AGENT_VERIFY_JSON_BEGIN---` / `---UNITY_AGENT_VERIFY_JSON_END---` markers in stdout) is available for deeper triage via `--output-path`.

The verify package (`com.alexeyperov.unity-agent-verify`) must be installed in the target project for the `-executeMethod` call to resolve.

## Prerequisite: install the verify package

Add to your project's `Packages/manifest.json`:

```json
{
  "com.alexeyperov.unity-agent-verify": "https://github.com/AlexeyPerov/Unity-AI-Hub.git?path=packages/verify#verify-v1.0.0"
}
```

See [packages/verify.md](../../specs/packages/verify.md) §Install for details.

---

## Runner strategies

### Strategy A — Self-hosted runner (recommended)

**Best for:** private teams, on-premise CI, teams with an existing Unity build machine.

A self-hosted runner with a local Unity installation and license is the lowest-friction path. No license secrets need to be stored in GitHub.

**Setup:**

1. Install Unity 6 on the runner machine and activate a license normally (open Unity once, sign in).
2. Register a [self-hosted runner](https://docs.github.com/en/actions/hosting-your-own-runners/managing-self-hosted-runners-with-github-actions) on your repo or org.
3. Set `UNITY_PATH` to the Unity executable:

   | OS | Typical path |
   |---|---|
   | macOS | `/Applications/Unity/Hub/Editor/6000.0.23f1/Unity.app/Contents/MacOS/Unity` |
   | Windows | `C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe` |
   | Linux | `/usr/share/unityhub/Unity/6000.0.23f1/Unity` |

4. Set `runs-on: self-hosted` (or your custom runner label) in the workflow.

**Advantages:**
- No license secret management — Unity is activated locally.
- Faster cold starts (project Library cache persists between runs).
- No docker image version churn.

**Limitations:**
- Runner machine must be online during CI runs.
- You are responsible for Unity version updates and disk cleanup.

### Strategy B — GitHub-hosted runner + game-ci (reference)

**Best for:** open-source repos, teams without on-premise infrastructure, public CI visibility.

Uses the [game-ci](https://game.ci) docker images with Unity pre-installed. Requires Unity license secrets stored as GitHub Actions secrets.

**Setup:**

1. Store `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` as repo or org secrets.
2. Uncomment the `container` and license-activate blocks in the workflow.
3. Set `runs-on: ubuntu-latest`.
4. Set the `UNITY_PATH` to the in-container path (`/unity/Unity`).

**Advantages:**
- No infrastructure to maintain.
- Reproducible, ephemeral runners.
- Works for public open-source repos.

**Limitations:**
- **Unity license friction:** Personal licenses tied to a machine hash require regeneration; the license activation step can be flaky.
- **Slower cold starts:** No persistent Library cache — Unity reimports the project each run.
- **Secret management:** License credentials live in GitHub secrets.
- **Docker image pinning:** You must track `unityci/editor` image tags matching your Unity version.

### Recommendation

Use **Strategy A** (self-hosted) as the default. Use **Strategy B** (hosted) when you need public CI visibility or have no on-premise option. The template defaults to self-hosted with hosted blocks commented and ready to uncomment.

---

## Baseline workflow

The committed baseline file (`CI/unity-agent-baseline.json`) is the regression reference. The lifecycle is:

1. **Initial baseline:** trigger `baseline-update` via `workflow_dispatch` after installing verify and confirming a clean project state.
2. **PR checks:** `regression-check` runs on every PR and fails when new errors exceed `--regression-threshold`.
3. **Baseline refresh:** re-run `baseline-update` after intentionally accepting new issues (e.g., bulk asset imports, rule additions). Review the diff before merging.

The baseline JSON includes `schemaVersion: 1` per [packages/verify.md](../../specs/packages/verify.md) §Baseline JSON schema v1.

## Failure triage

When a job fails, the workflow uploads:

| Artifact | Content |
|---|---|
| `*-unity-log` | Full Unity Editor batch log (retained 7 days). |
| `scan-report` | JSON scan result from `scan_all --output-path`. |

The job summary also prints the last 80 lines of the Unity log inline via `::group::` annotations for quick root-cause analysis without downloading artifacts.
