# MCP server rules

## Scope

Rules for `mcp-server/` — the stdio MCP server (`unity-open-mcp`). Root `AGENTS.md` also applies.

## Package shape

- TypeScript ESM project (`"type": "module"`). Source under `src/`, built to `dist/`.
- Node 18+. Keep production internal modules statically resolvable; do not use dynamic `import()` for ordinary internal wiring. Dynamic imports are allowed for test isolation, justified Node built-ins, or a documented exceptional lazy-loading boundary.
- No runtime dependencies beyond `@modelcontextprotocol/sdk`. Do not add dependencies without strong justification.

## Tool definitions

- Every MCP tool is defined in `src/tools/{tool-name}.ts` and exported from `src/tools/index.ts`.
- Tool names follow the `unity_open_mcp_*` (bridge-routed) or `unity_senses_*` (standalone) convention. The prefix signals routing to agents.
- Every tool definition includes: `name`, `description`, `inputSchema` (JSON Schema), and a handler. Export it, add it to the appropriate domain/tool array in `tools/index.ts`, and ensure that array is spread into `ALL_TOOLS`.
- When a tool's schema changes, the bridge-side C# handler (`packages/bridge/`) must stay in sync in the same task — the bridge parses args by key name, not by schema validation.
- Legacy typed tools also require bridge registration in `packages/bridge/Editor/Bridge/BridgeToolClassification.cs` (`KnownTools`, plus the applicable read/mutate set) and a `BridgeHttpServer.DispatchTool` case. Registry-discovered tools do not use those hardcoded paths. Follow the bridge [tool registration rules](../packages/bridge/AGENTS.md#tool-registration).
- Domain additions and changes follow the canonical [end-to-end domain checklist](../docs/contributing/extensions.md#end-to-end-domain-checklist), including tool-group/category registration, domain skills, API docs, token estimates, catalogs where applicable, and routed tests.

## Routing

- `src/tool-router.ts` selects live / batch / offline / local per tool call. Route policies:
  - **live** — requires the bridge running; routes to `POST /tools/{name}`.
  - **batch** — headless Unity fallback when live is unavailable.
  - **offline** — local disk parsers, no Unity needed.
  - **local** — handler executes in the MCP server with no live/batch dispatch; an explicitly documented optional read-only probe may enrich its result.
- Do not add a new route type without updating `docs/architecture.md` and
  `docs/api/routing-lifecycle.md`.
- Capability discovery (`unity_open_mcp_capabilities`), skill generation (`unity_open_mcp_generate_skill`), and `unity_open_mcp_manage_tools` remain **local-route** and never dispatch through live or batch Unity. `capabilities` and `manage_tools(list_groups)` may make an optional read-only `GET /tools` probe to enrich compiled-state availability. The probe must fail open to unknown availability: an offline bridge must not make the local tool unusable.

## Tool-group visibility (M18 Plan 2)

- Canonical group catalog: `src/capabilities/tool-groups.ts` (single source of truth for group ids, descriptions, default-enabled flag, domain defines, and the per-tool group assignment). Adding/removing a group or moving a tool between groups happens there.
- Per-session state: `src/tool-session-state.ts` (`ToolSessionState`). Ephemeral, in-memory, per stdio server process. Resets to `core`-only on restart.
- `ListTools` filters via `filterVisibleTools(ALL_TOOLS, sessionState)` in `index.ts`. Always-visible meta-tools (capabilities, manage_tools, ping, …) bypass the filter via the `ALWAYS_VISIBLE_TOOLS` allow-list.
- `manage_tools` mutates the session state from `tool-router.ts#routeManageTools`. The bridge does NOT see these calls.
- When activate/deactivate/reset actually changes the visible tool set, the MCP server emits `notifications/tools/list_changed` (declares `tools.listChanged: true` in server capabilities). Clients should re-issue `tools/list` to refresh descriptors mid-session.
- Compiled-state availability (`available` per group) is probed via `LiveClient.listBridgeTools()` (`GET /tools`). `null` (unknown) when the bridge is offline; `true/false` when reachable. Capabilities and manage_tools share this probe.
- Domain groups (navigation, input-system, probuilder, particle-system, animation) carry `domainDefine` so an absent Unity package surfaces as `available: false (dependency missing: <package>)`.

## Instance discovery

- Bridge port resolution lives in `src/instance-discovery.ts`. Precedence: `UNITY_OPEN_MCP_BRIDGE_PORT` env var → `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` lock file (when its `pid` is alive) → the shared deterministic computation defined by the bridge contract.
- The bridge owns the detailed [three-way deterministic-port contract](../packages/bridge/AGENTS.md#multi-instance-port-and-discovery). If hashing, path normalization, range, or fallback behavior changes, update the bridge C#, this TypeScript mirror, the Hub Rust mirror, and all three fixture suites in the same task.
- The MCP server is **read-only** on the lock file. Stale-lock cleanup (PID-liveness sweep) is the bridge's job on its next `Acquire`; never delete or rewrite locks from this package.
- No new runtime deps: the module uses only `node:crypto`, `node:fs`, `node:os`, `node:path`.
- M14 — the same lock file carries an `authToken`. `resolveAuthToken` reads it and the token is threaded into `LiveClient` and `BridgeEventStream`, which attach `Authorization: Bearer <token>` to every request. When no token is discoverable (older bridge, dead-pid lock, or an explicit `UNITY_OPEN_MCP_BRIDGE_PORT` override that skips the lock read) no header is sent and the bridge must be in `authMode "none"`. The `InstanceLock` interface must mirror the bridge's lock schema; adding/removing a lock field is a cross-side change.
- **Dead-bridge detection.** Preserve `projectPath` through `LiveClient` readiness paths so `classifyInstance(lock)` can distinguish reload from a live PID with a stale heartbeat. Return `bridge_compile_failed` immediately for that signature and point the agent to offline `unity_open_mcp_read_compile_errors`; do not wait for `/ping` recovery. The bridge owns lock retention; see [bridge multi-instance discovery](../packages/bridge/AGENTS.md#multi-instance-port-and-discovery) and [contributor troubleshooting](../docs/troubleshooting-contributors.md#stale-heartbeat-vs-live-pid).

## Offline reads

- The offline-read path (`src/offline.ts`, `src/compressible-router.ts`) deliberately avoids persistent on-disk caches. GUID indices and YAML parses are rebuilt per request. Do not add a disk cache without explicit approval — the no-cache philosophy is documented in `docs/architecture.md`.
- The session-scoped in-memory `AssetModelCache` (LRU, last 8) is the only allowed cached state, and only for drill-down reuse.

## Capabilities

- `src/capabilities/` holds the rule/fix catalog and the `buildCapabilities` transform. These mirror the C# verify package state — keep them in sync when rules/fixes change (see [Verify capability catalog sync](../packages/verify/AGENTS.md#capability-catalog-sync)).

## Verification

- Run `npm run typecheck` and `npm test` after changes. Tests use `node --test` with type stripping.
- Tool contract changes: update the relevant page indexed by
  `docs/api/mcp-tools.md` in the same task.
- Capability changes: verify `mcp-server/src/capabilities/build-capabilities.test.ts` covers the new rule/fix/surface.

## Agent skill sync

Agent skills are the **agent-facing** counterpart of the human/contributor docs. Keep the applicable surfaces synchronized because they serve different audiences:

- `skills/unity-open-mcp/SKILL.md` — **agent playbook**: workflows, principles, short routing narrative, pointers to `unity_open_mcp_capabilities`. Shipped into a game project's `.claude/skills/` / `.cursor/skills/` / `.opencode/skills/` / `.agents/skills/` (paths from `skills/client-paths.json`). Agents working in a game project see this file, not `docs/api/`.
- `skills/extensions/<domain>/SKILL.md` — **domain playbook** for domain-specific tools and workflows.
- `docs/api/mcp-tools.md` and its focused API pages — **human/contributor
  reference**: tool-family index, group/session contract, route/lifecycle
  policy, CLI automation, and selected response shapes. They live in the
  toolkit repo and are not assumed visible to agents in a downstream project.

When an MCP tool, capability, route policy, batch behavior, or agent-senses tool changes, update the applicable agent-facing surfaces:

- **Always** update the owning API page linked from
  `docs/api/mcp-tools.md` (overview, groups, routing/lifecycle, CLI, or response
  shape).
- **When the change affects agent workflow or routing guidance**, also update `skills/unity-open-mcp/SKILL.md`. Examples that require a SKILL edit: a new agent-senses tool, a new batch-eligible tool, a route-policy change, a new gate workflow, a new verify rule that changes the mutate→gate→fix loop.
- **For a domain tool or workflow**, update its owning `skills/extensions/<domain>/SKILL.md` in the same task. The MCP package owns this synchronization even though the skill is stored outside `mcp-server/`.
- Pure schema tweaks (renaming an optional field, adding a non-workflow detail) only need the api doc.

Rules for the SKILL file:

- Keep it lean (~150–200 lines): workflows, principles, one canonical example per concept, pointers to `unity_open_mcp_capabilities` for details. Do **not** copy the full `docs/api/mcp-tools.md` tables into the skill.
- The `routing` object on the capabilities response is the machine-readable routing source — the SKILL only summarizes it.
- Do **not** put MCP client config JSON, install path tables, or setup content in
  the skill. Client paths and envelopes live in
  `docs/setup/client-configuration.md`; installation procedures live in the
  audience-specific setup guides.
- The `unity_open_mcp_generate_skill` output stays a **project inventory** (packages, types, tool list from catalog). Do not reintroduce install/MCP JSON into generated skills.

The MCP package (this directory) owns SKILL sync with tool changes — same obligation as the api doc sync above.
