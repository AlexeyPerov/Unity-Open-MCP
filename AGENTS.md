# Agent rules

## Rule scope and precedence

- Ancestor `AGENTS.md` rules always apply. A deeper file may add requirements or narrow an allowed choice for its subtree. It may contradict an ancestor only when that ancestor explicitly grants an exception.
- For a cross-package change, satisfy the union of the rules governing every touched file; sibling rule files are not inherited automatically.
- Example: root permits tracked-doc updates and a package may narrow that to one owning API page. A package may not allow persisted-data migrations because root forbids them unless explicitly requested.
- Current local layers:
  - Root (`AGENTS.md`) — repository-wide safety, documentation, and maintainer process.
  - `packages/bridge/AGENTS.md` — bridge transport, shipped embedded domains, registration, gates, and Editor UI.
  - `packages/verify/AGENTS.md` — verify rules, fixes, issue codes, and capability catalog sync.
  - `packages/extensions/AGENTS.md` — community/third-party extension packs and their template.
  - `mcp-server/AGENTS.md` — MCP tools, routing, capabilities, and offline reads.
  - `hub/AGENTS.md` — SvelteKit/Tauri UI, state, data, and platform neutrality.
  - `scripts/AGENTS.md` — version sync, token-estimate codegen, and MCP test suites.
  - `skills/AGENTS.md` — agent playbooks and client install-path manifest.
  - `demo/AGENTS.md` — Unity integration fixture, manifest/`testables` sync.
  - `validation-suite/AGENTS.md` — guided manual-validation app and scenarios.

## Universal contributor rules

- **Specs (`specs/`).** This optional maintainer working tree is gitignored. Never `git add`, commit, or push anything under `specs/`. Its absence in a public clone is not a blocker.
- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. Prefer simplifying storage and codecs over backward compatibility.
- **No internal references in public surfaces.** README files, `docs/`, UI strings, and shipped skills must not expose `specs/` paths, milestone/spec identifiers, execution-plan task numbers, or internal question citations. `AGENTS.md` files and source comments may use an internal identifier only when it materially helps maintainers; prefer stable contract names even there.
- **No reference-project attribution outside `specs/`.** Names, repository paths, handles, and attribution to projects under `/references` must not appear in source comments, checked-in docs, README files, skills, or UI strings. State contracts on their own merits. The only tracked exceptions are the deliberate comparison page `docs/mcp-tools-comparison.md` and provenance log `packages/verify/EXTRACTION.md`.
- **Docs are part of done.** If a change affects public behavior, API contracts, architecture boundaries, or developer workflows, update tracked docs in `README.md` and/or `docs/` in the same task.
- **Agent skill sync.** `skills/unity-open-mcp/SKILL.md` is agent-facing guidance. The MCP package owns its synchronization with tool/capability/routing changes and the owning page indexed by `docs/api/mcp-tools.md`; follow [MCP server rules — Agent skill sync](mcp-server/AGENTS.md#agent-skill-sync). Skill install paths come from `skills/client-paths.json`, not per-package constants.
- **Docs layout and ownership.**
  - Root `README.md` stays short: intro, current feature set, quick links, and a **Documentation** section that is the docs index (no separate `docs/README.md`).
  - `README.md`'s Documentation section is the docs index and must link every top-level doc, **without duplicating** links already provided by other README sections (Quick setup, Key features, Unity Hub Pro, Contributing). When a new top-level doc is added, link it from this section in the same task.
  - `docs/architecture.md` covers repo structure and cross-package boundaries.
  - `docs/api.md` is the index for externally relevant API interfaces and contracts.
- **Doc update scope rule.** Edit only docs that match the changed area; avoid unrelated rewrites. If a new docs domain is introduced, add it to `README.md`'s Documentation section in the same task.
- **When docs updates can be skipped.** Typos, formatting-only edits, comments-only changes, and internal refactors that do not alter behavior or contracts.
- **Agent reporting requirement.** If code changes but tracked docs are not updated, explicitly state why docs were not needed in the final handoff message.

## Maintainer process when `specs/` is available

- You may read and edit gitignored working docs under `specs/`, but keep them out of version control.
- **Milestone validation checklists.** Apply this only while executing a milestone plan under `specs/execution/`. During the final validation pass, create or update its manual checklist following `specs/execution/manual-checklist-convention.md`. Cover every human-driven Done criterion, link it from the milestone plan/spec, and do not mark the milestone DONE until a representative walkthrough passes. A Validation Suite checklist may be an index/sign-off artifact as defined by the convention.
- **MCP-experience feedback.** This is a maintainer process obligation, not a CI-enforced check. If this task used `unity_open_mcp_*` or `unity_senses_*` and `specs/` is available, review the calls before handoff. Record each genuine error, unexpected result, retry, friction point, or improvement idea in `specs/feedback.md` (create it if needed); write nothing for a clean session.
  - **Do not duplicate:** before adding an entry, check whether `specs/feedback.md` already has an entry for the same tool/issue; if so, append a `+1 / reproduces on <date>` note to that entry instead of creating a new one.
  - **Entry format:**
    ```
    - **Date:** YYYY-MM-DD
    - **Tool/sense:** unity_open_mcp_<name> (or unity_senses_<name>)
    - **What happened:** <observed behavior, with the error code / message if any>
    - **Expected:** <correct behavior>
    - **Severity:** bug | friction | suggestion
    - **Suggested fix:** <idea or existing internal-plan pointer>
    ```
  - Only log genuine issues. Do not log expected/working behavior, successful calls, or personal preference rants. Keep entries concrete and reproducible (include the error code, the args that triggered it, the route taken).

