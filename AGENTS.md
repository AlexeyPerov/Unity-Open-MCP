# Agent rules

- **Changelog.** Log all changes to `specs/changelog.md`. Use dated (with time) entries or an existing section style already used in that file.

- **Migrations.** Do not implement data migrations, compatibility shims, or upgrade paths for persisted data unless explicitly requested. The app is in active development and is not used by real users yet; prefer simplifying storage and codecs over backward compatibility.
