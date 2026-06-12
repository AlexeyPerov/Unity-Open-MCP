# Unity Agent Verify CI Template

Template workflow for M5 batch verification.

## Files

- `unity-agent-verify.yml` — example GitHub Actions workflow for baseline/regression checks.

## Runner notes

- Public GitHub-hosted runners require Unity license secrets to run batch Unity jobs.
- Many teams should prefer a self-hosted runner with a local Unity installation/license and `UNITY_PATH` configured.

## Required environment

- `UNITY_PROJECT_PATH` (project root, typically `demo`)
- `UNITY_PATH` (Unity executable path for batch fallback)

## Usage

Copy `unity-agent-verify.yml` into `.github/workflows/` in your repository and adjust project path, Unity version, and secrets for your environment.
