# MCP server rules

## Scope

Rules for `mcp-server/` — the stdio MCP server (`unity-open-mcp`). Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- TypeScript ESM project (`"type": "module"`). Source under `src/`, built to `dist/`.
- Node 18+. Type-stripping (`--experimental-strip-types`) is used for tests; keep imports statically resolvable — no dynamic `import()` for internal modules.
- No runtime dependencies beyond `@modelcontextprotocol/sdk`. Do not add dependencies without strong justification.

## Tool definitions

- Every MCP tool is defined in `src/tools/{tool-name}.ts` and exported from `src/tools/index.ts`.
- Tool names follow the `unity_open_mcp_*` (bridge-routed) or `unity_agent_*` (standalone) convention. The prefix signals routing to agents.
- Every tool definition includes: `name`, `description`, `inputSchema` (JSON Schema), and a handler. Add the tool to the appropriate export array (`M11_TOOLS`, etc.) in `tools/index.ts`.
- When a tool's schema changes, the bridge-side C# handler (`packages/bridge/`) must stay in sync in the same task — the bridge parses args by key name, not by schema validation.

## Routing

- `src/tool-router.ts` selects live / batch / offline / local per tool call. Route policies:
  - **live** — requires the bridge running; routes to `POST /tools/{name}`.
  - **batch** — headless Unity fallback when live is unavailable.
  - **offline** — local disk parsers, no Unity needed.
  - **local** — never hits Unity (capabilities, skill generation).
- Do not add a new route type without updating `docs/architecture.md` and the route-policy table in `docs/api/mcp-tools.md`.
- Capability-discovery (`unity_open_mcp_capabilities`) and skill generation (`unity_agent_generate_skill`) are **local-only** — they must never depend on the live bridge or batch Unity.

## Offline reads

- The offline-read path (`src/offline.ts`, `src/compressible-router.ts`) deliberately avoids persistent on-disk caches. GUID indices and YAML parses are rebuilt per request. Do not add a disk cache without explicit approval — the no-cache philosophy is documented in `docs/architecture.md`.
- The session-scoped in-memory `AssetModelCache` (LRU, last 8) is the only allowed cached state, and only for drill-down reuse.

## Capabilities

- `src/capabilities/` holds the rule/fix catalog and the `buildCapabilities` transform. These mirror the C# verify package state — keep them in sync when rules/fixes change (see `packages/verify/AGENTS.md` §Capability catalog sync).

## Verification

- Run `npm run typecheck` and `npm test` after changes. Tests use `node --test` with type stripping.
- Tool contract changes: update the tool catalog in `docs/api/mcp-tools.md` in the same task.
- Capability changes: verify `build-capabilities.test.ts` covers the new rule/fix/surface.
