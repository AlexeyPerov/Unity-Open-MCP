# Agent rules

- **Layered AGENTS.md (deepest rule wins).** `AGENTS.md` files are co-located with the code they govern. Precedence flows root → package → subtree: a deeper file may add or narrow rules for its subtree, but never silently contradicts a root rule unless that root rule explicitly allows an exception. On overlap, the deepest applicable rule is most specific. Current layers:
  - Root (`AGENTS.md`) — cross-cutting rules (specs/ is gitignored, no migrations, docs ownership).
  - `packages/bridge/AGENTS.md` — bridge transport, tool registration, gate policy.
  - `packages/verify/AGENTS.md` — verify rules (must declare issue codes), fixes, capability catalog sync.
  - `packages/extensions/AGENTS.md` — optional domain extension packs (NavMesh, Input System, …).
  - `mcp-server/AGENTS.md` — tool definitions, routing, offline-read no-cache philosophy.
  - `hub/AGENTS.md` — SvelteKit/Tauri UI, state/data, platform neutrality.
- **Specs (`specs/`).** Local working docs only — `specs/` is gitignored. Do not `git add`, commit, or push anything under `specs/`. You may read and edit files there when helpful (e.g. `specs/changelog.md`, execution plans, backlog files), but keep those changes out of version control.
- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. Prefer simplifying storage and codecs over backward compatibility.
- **No internal references in user-visible surfaces.** User-visible docs (checked-in markdown, READMEs, help text) and UI strings (labels, tooltips, helpboxes, wizard copy) must never reference internal data such as `specs/` paths, milestone IDs (e.g. M1, M4, M4.5-3, M1.5-16), execution-plan task numbers, or questions-file citations (e.g. "questions-4 Q9 = A"). These are internal working artifacts; end users should never see them. Source-code comments may still reference specs for developer context, but rendered strings and shipped documentation must be clean.
- **No references to `/references` projects outside `specs/`.** The `/references` directory holds third-party projects studied for inspiration/porting. Those project names, repo paths, author handles, and attribution lines (e.g. `IvanMurzak/Unity-AI-*`, `UnityLauncherPro`, `unity-scanner`, `Unity-MCP`, `AnkleBreaker`, `Coplay`, `UCP`, `references/unity-mcp-beta-codev/...`, "ported from", "mirrors X's", "Ivan-breadth", "competitor ships") must **never** appear in code comments, checked-in docs, READMEs, skill files, or UI strings. Only files under `specs/` may name these projects. State the contract on its own merits instead — e.g. write "param shape: width / length / height" rather than "mirrors AnkleBreaker's unity_terrain_create". The only approved tracked-file exceptions are `docs/mcp-tools-comparison.md` (a deliberate public competitive-comparison page) and `packages/verify/EXTRACTION.md` (a Unity-Scanner provenance log).

- **Manual validation checklists (M9+).** When finishing validation work on milestones **M9 and later**, create or update `specs/execution/M{n}/m{n}-manual-checklist.md` following [specs/execution/manual-checklist-convention.md](specs/execution/manual-checklist-convention.md). Do this during the final validation pass for the milestone (or as features become manually testable). The checklist must cover every **Done when** item that needs human-driven verification. Link it from the milestone `execution-plan.md` and milestone spec. Do not mark the milestone **DONE** until the checklist exists and a representative walkthrough has passed. Reference: [specs/execution/done/M4.5/m4.5-manual-checklist.md](specs/execution/done/M4.5/m4.5-manual-checklist.md). For milestones validated with the **Validation Suite** (`validation-suite/`), the checklist may be an **index + sign-off artifact**: a scenario-index table mapping checklist sections to stable suite scenario IDs (`m{n}-{slug}`) + a representative run summary pasted from the suite's Export. Full operator steps (prompts, expected outcomes, setup/reset) live in the scenario JSON shipped with the suite, not duplicated in the markdown. See the convention doc §"Validation Suite index model" for the shape.
- **Docs are part of done.** If a change affects public behavior, API contracts, architecture boundaries, or developer workflows, update tracked docs in `README.md` and/or `docs/` in the same task.
- **Agent skill sync.** `skills/unity-open-mcp/SKILL.md` is agent-facing guidance (the playbook agents see inside a game project). The MCP package owns keeping it in sync with tool/capability/routing changes alongside the owning page indexed by `docs/api/mcp-tools.md` — see `mcp-server/AGENTS.md` §Agent skill sync for the rule. Skill install paths come from the single-source `skills/client-paths.json` manifest, not from per-package constants.
- **Docs layout and ownership.**
  - Root `README.md` stays short: intro, current feature set, quick links, and a **Documentation** section that is the docs index (no separate `docs/README.md`).
  - `README.md`'s Documentation section is the docs index and must link every top-level doc, **without duplicating** links already provided by other README sections (Quick setup, Key features, Unity Hub Pro, Contributing). When a new top-level doc is added, link it from this section in the same task.
  - `docs/architecture.md` covers repo structure and cross-package boundaries.
  - `docs/api.md` documents externally relevant interfaces and contracts.
- **Doc update scope rule.** Edit only docs that match the changed area; avoid unrelated rewrites. If a new docs domain is introduced, add it to `README.md`'s Documentation section in the same task.
- **When docs updates can be skipped.** Typos, formatting-only edits, comments-only changes, and internal refactors that do not alter behavior or contracts.
- **Agent reporting requirement.** If code changes but tracked docs are not updated, explicitly state why docs were not needed in the final handoff message.
- **Agent MCP-experience feedback loop.** Any agent that uses MCP tools or senses (`unity_open_mcp_*`, `unity_senses_*`) during a session must, **before finishing its turn/task**, scan its own tool/sense usage for problems: errors, wrong/unexpected results, retries, friction, or improvement ideas. For each issue found, append a dated entry to `specs/feedback.md` (create the file if it does not exist). This makes transport bugs, tool contract gaps, and UX friction self-capture into a running backlog instead of only surfacing when a human happens to notice. A clean session with no issues writes nothing.
  - `specs/feedback.md` is gitignored (a working artifact) — this is a process change in `AGENTS.md` only, no tracked-doc churn.
  - **Do not duplicate:** before adding an entry, check whether `specs/feedback.md` already has an entry for the same tool/issue; if so, append a `+1 / reproduces on <date>` note to that entry instead of creating a new one.
  - **Entry format:**
    ```
    - **Date:** YYYY-MM-DD
    - **Tool/sense:** unity_open_mcp_<name> (or unity_senses_<name>)
    - **What happened:** <observed behavior, with the error code / message if any>
    - **Expected:** <correct behavior>
    - **Severity:** bug | friction | suggestion
    - **Suggested fix:** <idea, or "see specs/execution/M20/execution-plan-4-5-bug-fixes.md T-fix-N" if a fix is already tracked>
    ```
  - Only log genuine issues. Do not log expected/working behavior, successful calls, or personal preference rants. Keep entries concrete and reproducible (include the error code, the args that triggered it, the route taken).

