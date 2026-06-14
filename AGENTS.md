# Agent rules

- **Specs (`specs/`).** Local working docs only — `specs/` is gitignored. Do not `git add`, commit, or push anything under `specs/`. You may read and edit files there when helpful (e.g. `specs/changelog.md`, execution plans, backlog files), but keep those changes out of version control.

- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. The app is in active development and is not used by real users yet; prefer simplifying storage and codecs over backward compatibility.

- **No internal references in user-visible surfaces.** User-visible docs (checked-in markdown, READMEs, help text) and UI strings (labels, tooltips, helpboxes, wizard copy) must never reference internal data such as `specs/` paths, milestone IDs (e.g. M1, M4, M4.5-3, M1.5-16), execution-plan task numbers, or questions-file citations (e.g. "questions-4 Q9 = A"). These are internal working artifacts; end users should never see them. Source-code comments may still reference specs for developer context, but rendered strings and shipped documentation must be clean.
