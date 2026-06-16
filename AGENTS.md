# Agent rules

- **Layered AGENTS.md (deepest rule wins).** `AGENTS.md` files are co-located with the code they govern. Precedence flows root → package → subtree: a deeper file may add or narrow rules for its subtree, but never silently contradicts a root rule unless that root rule explicitly allows an exception. On overlap, the deepest applicable rule is most specific. Current layers:
  - Root (`AGENTS.md`) — cross-cutting rules (specs/ is gitignored, no migrations, docs ownership).
  - `packages/bridge/AGENTS.md` — bridge transport, tool registration, gate policy.
  - `packages/verify/AGENTS.md` — verify rules (must declare issue codes), fixes, capability catalog sync.
  - `mcp-server/AGENTS.md` — tool definitions, routing, offline-read no-cache philosophy.
  - `hub/AGENTS.md` — SvelteKit/Tauri UI, state/data, platform neutrality.

- **Specs (`specs/`).** Local working docs only — `specs/` is gitignored. Do not `git add`, commit, or push anything under `specs/`. You may read and edit files there when helpful (e.g. `specs/changelog.md`, execution plans, backlog files), but keep those changes out of version control.

- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. The app is in active development and is not used by real users yet; prefer simplifying storage and codecs over backward compatibility.

- **No internal references in user-visible surfaces.** User-visible docs (checked-in markdown, READMEs, help text) and UI strings (labels, tooltips, helpboxes, wizard copy) must never reference internal data such as `specs/` paths, milestone IDs (e.g. M1, M4, M4.5-3, M1.5-16), execution-plan task numbers, or questions-file citations (e.g. "questions-4 Q9 = A"). These are internal working artifacts; end users should never see them. Source-code comments may still reference specs for developer context, but rendered strings and shipped documentation must be clean.

- **Manual validation checklists (M9+).** When finishing validation work on milestones **M9 and later**, create or update `specs/execution/M{n}/m{n}-manual-checklist.md` following [specs/execution/manual-checklist-convention.md](specs/execution/manual-checklist-convention.md). Do this during the final validation pass for the milestone (or as features become manually testable). The checklist must cover every **Done when** item that needs human-driven verification. Link it from the milestone `execution-plan.md` and milestone spec. Do not mark the milestone **DONE** until the checklist exists and a representative walkthrough has passed. Reference: [specs/execution/done/M4.5/m4.5-manual-checklist.md](specs/execution/done/M4.5/m4.5-manual-checklist.md).

- **Docs are part of done.** If a change affects public behavior, API contracts, architecture boundaries, or developer workflows, update tracked docs in `README.md` and/or `docs/` in the same task.

- **Agent skill sync.** `skills/unity-open-mcp/SKILL.md` is agent-facing guidance (the playbook agents see inside a game project). The MCP package owns keeping it in sync with tool/capability/routing changes alongside `docs/api/mcp-tools.md` — see `mcp-server/AGENTS.md` §Agent skill sync for the rule. Skill install paths come from the single-source `skills/client-paths.json` manifest, not from per-package constants.

- **Docs layout and ownership.**
  - Root `README.md` stays short: intro, current feature set, quick links.
  - `docs/README.md` is the docs index and must link every top-level doc.
  - `docs/architecture.md` covers repo structure and cross-package boundaries.
  - `docs/tools.md` lists major tools/dependencies and their purpose.
  - `docs/api.md` documents externally relevant interfaces and contracts.

- **Doc update scope rule.** Edit only docs that match the changed area; avoid unrelated rewrites. If a new docs domain is introduced, add it to `docs/README.md` in the same task.

- **When docs updates can be skipped.** Typos, formatting-only edits, comments-only changes, and internal refactors that do not alter behavior or contracts.

- **Agent reporting requirement.** If code changes but tracked docs are not updated, explicitly state why docs were not needed in the final handoff message.
