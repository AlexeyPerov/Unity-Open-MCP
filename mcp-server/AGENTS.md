# MCP server rules

## Scope

Rules for `mcp-server/` — stdio MCP server (`unity-open-mcp`). Root
`AGENTS.md` also applies. Owns tool schemas, session visibility, routing,
offline reads, capabilities mirroring, and agent skill sync. Bridge owns C#
registration, transport, gate execution, and the port formula —
[`packages/bridge/AGENTS.md`](../packages/bridge/AGENTS.md).

## Package shape

- TypeScript ESM. Source `src/` → `dist/`. Node 18+.
- Keep production internal modules statically resolvable; no dynamic
  `import()` for ordinary wiring. Allowed: test isolation, justified Node
  built-ins, documented exceptional lazy-loading.
- No deps beyond `@modelcontextprotocol/sdk` without strong justification.

## Tool definitions

1. Define in `src/tools/{name}.ts`, export from `index.ts`, add to the right
   domain/tool array, and ensure that array is in `ALL_TOOLS`.
2. Names: `unity_open_mcp_*` or `unity_senses_*`.
3. Each tool: `name`, `description`, `inputSchema`, handler.
4. Schema changes → same-task bridge C# handler update (args by key name).
5. Legacy typed tools → bridge `KnownTools` (+ read/mutate set) +
   `DispatchTool` case
   ([registration](../packages/bridge/AGENTS.md#tool-registration)).
6. Domains →
   [end-to-end checklist](../docs/contributing/extensions.md#end-to-end-domain-checklist).

## Routing

- `tool-router.ts`: **live** / **batch** / **offline** / **local**.
- Local handlers may use an optional read-only `GET /tools` probe; it must
  fail open so offline Unity never makes the tool unusable.
- `capabilities`, `generate_skill`, `manage_tools` are local-only.
- `bridge_status`, `restart_editor`, and `resource_pressure` are local-only
  operator surfaces that act on the OS process (instance lock + /ping;
  process kill; fd-count probe), not the bridge. Like `read_compile_errors`
  they stay reachable when the bridge is down — by design, since the bridge
  is the thing that dies in the failure modes they recover from.
- New route types update `docs/architecture.md` and
  `docs/api/routing-lifecycle.md`.

## Tool-group session visibility

- Catalog: `src/capabilities/tool-groups.ts`. Session state:
  `tool-session-state.ts` (ephemeral). Bridge does not see activate/deactivate.
- `ListTools` filters via `filterVisibleTools`; meta-tools use
  `ALWAYS_VISIBLE_TOOLS`. Visibility changes emit `tools/list_changed`.
- Compiled-state `available` from `LiveClient.listBridgeTools()` (`null` offline).

## Instance discovery

- Order in `instance-discovery.ts`: env override → live lock → shared
  deterministic port.
- Bridge owns the
  [three-way port contract](../packages/bridge/AGENTS.md#multi-instance-port-and-discovery).
  Hashing/normalization/range/fallback changes update C#, this TS mirror, Hub
  Rust, and all three fixtures together.
- MCP is read-only on locks. Preserve `projectPath` for
  `classifyInstance` → `bridge_compile_failed` on stale heartbeat + live PID.
- Keep `InstanceLock` / `authToken` aligned with the bridge.

## Offline reads and capabilities

- No persistent disk caches in offline/compressible paths. Only allowed cache:
  session `AssetModelCache` (LRU 8).
- `src/capabilities/` mirrors verify — see
  [catalog sync](../packages/verify/AGENTS.md#capability-catalog-sync).

## Verification

1. `npm run typecheck` and `npm test`.
2. Tool contracts → owning page linked from `docs/api/mcp-tools.md`.
3. Capability changes → `build-capabilities.test.ts`.

## Agent skill sync

This package owns skill sync with tool/capability/routing changes.

| Surface | Role |
|---|---|
| `skills/unity-open-mcp/SKILL.md` | Root agent playbook |
| `skills/extensions/<domain>/SKILL.md` | Domain playbook |
| Pages under `docs/api/mcp-tools.md` | Human/contributor reference |

Same task: always update the owning API page; update the root skill when
workflow/routing guidance changes; update the domain skill for domain
behavior; pure schema tweaks need only the API doc. Keep skills lean; no
full API tables or client-install JSON (paths:
`skills/client-paths.json`).
