# Agent rules

- **Specs (`specs/`).** Local working docs only — `specs/` is gitignored. Do not `git add`, commit, or push anything under `specs/`. You may read and edit files there when helpful (e.g. `specs/changelog.md`, execution plans, backlog files), but keep those changes out of version control.

- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. The app is in active development and is not used by real users yet; prefer simplifying storage and codecs over backward compatibility.
