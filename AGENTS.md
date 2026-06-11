# Agent rules

- **Changelog.** Log all changes to `specs/changelog.md`. Use dated (with time) entries or an existing section style already used in that file.

- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. The app is in active development and is not used by real users yet; prefer simplifying storage and codecs over backward compatibility.

- **Backlog.** Record deferred scope explicitly:
  - Hub-facing deferrals go to `specs/hub/backlog.md`.
  - Package/MCP/bridge/demo deferrals go to `specs/packages/backlog.md`.
  - When deferred work is pulled into an active milestone, remove it from backlog and update the relevant spec/execution plan.
