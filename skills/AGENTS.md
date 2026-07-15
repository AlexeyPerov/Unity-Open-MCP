# Skills rules

## Scope

Rules for `skills/` — agent-facing playbooks installed into game projects.
Root `AGENTS.md` also applies. Human catalog: [`docs/skills.md`](../docs/skills.md).

## Install-path source

- `skills/client-paths.json` is the single source of truth for per-client skill
  destinations and MCP-client mapping. Schema:
  `skills/client-paths.schema.json`.
- Keep `mcp-server/src/skill/client-paths.ts` `BUNDLED_MANIFEST` in sync (unit
  test enforces it). Do not hardcode client skill paths elsewhere.

## Ownership

| Skill | Owner of sync with tool changes |
|---|---|
| `skills/unity-open-mcp/SKILL.md` | MCP package — see [Agent skill sync](../mcp-server/AGENTS.md#agent-skill-sync) |
| `skills/extensions/<domain>/SKILL.md` | MCP package for domain workflow changes; bridge/extensions when domain behavior is authored there |

Root skill = core agent playbook. Domain skills = one folder per domain under
`extensions/`. Do not put user install/MCP JSON tables in skills.

## Generated and installed copies

- `unity_open_mcp_generate_skill` may write project-specific sections into
  client skill folders. Those installed copies are project-local; edit the
  toolkit templates here, not copies under `demo/` or other projects, unless
  intentionally refreshing the fixture.
- Demo-installed skill trees under client folders are fixtures — regenerate or
  refresh deliberately; do not treat them as canonical sources.

## Verification

- After skill content changes that affect agent workflow, confirm the owning
  API page under `docs/api/mcp-tools.md` still matches.
- After `client-paths.json` edits, run the MCP skill client-paths tests.
