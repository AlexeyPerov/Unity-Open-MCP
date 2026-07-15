# Demo project rules

## Scope

Rules for `demo/` — the default Unity integration fixture for local MCP suites,
EditMode package tests, and Validation Suite runs. Root `AGENTS.md` also
applies. User quick start: [`README.md`](README.md).

## Fixture role

- Treat `demo/` as a CI/integration fixture, not a product sample. Avoid
  shipping product-specific game content, branding, or one-off workflows that
  only serve a single title.
- Default target for `scripts/mcp-*.mjs` when `--project` is omitted.

## Package and testables sync

1. Keep `Packages/manifest.json` and `Packages/packages-lock.json` aligned when
   adding or removing local packages or optional Unity domain dependencies.
2. List packages whose EditMode tests must run in CI under `testables`
   (bridge and verify at minimum; community packs when their tests are
   required).
3. Domain or pack work that needs an optional dependency compiled in for CI
   updates this fixture in the same task — see the
   [end-to-end domain checklist](../docs/contributing/extensions.md#end-to-end-domain-checklist).

## Hygiene

- Do not commit Validation Suite runtime paths
  (`Assets/_ValidationSuite/`, `UserSettings/ValidationSuite/`).
- Prefer disposable fixture folders under `Assets/` for suite scratch data.
