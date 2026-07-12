# MCP server rules

## Scope

Rules for `mcp-server/` — the stdio MCP server (`unity-open-mcp`). Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- TypeScript ESM project (`"type": "module"`). Source under `src/`, built to `dist/`.
- Node 18+. Type-stripping (`--experimental-strip-types`) is used for tests; keep imports statically resolvable — no dynamic `import()` for internal modules.
- No runtime dependencies beyond `@modelcontextprotocol/sdk`. Do not add dependencies without strong justification.

## Tool definitions

- Every MCP tool is defined in `src/tools/{tool-name}.ts` and exported from `src/tools/index.ts`.
- Tool names follow the `unity_open_mcp_*` (bridge-routed) or `unity_senses_*` (standalone) convention. The prefix signals routing to agents.
- Every tool definition includes: `name`, `description`, `inputSchema` (JSON Schema), and a handler. Add the tool to the appropriate export array (`M11_TOOLS`, etc.) in `tools/index.ts`.
- When a tool's schema changes, the bridge-side C# handler (`packages/bridge/`) must stay in sync in the same task — the bridge parses args by key name, not by schema validation.

## Routing

- `src/tool-router.ts` selects live / batch / offline / local per tool call. Route policies:
  - **live** — requires the bridge running; routes to `POST /tools/{name}`.
  - **batch** — headless Unity fallback when live is unavailable.
  - **offline** — local disk parsers, no Unity needed.
  - **local** — never hits Unity (capabilities, manage_tools, skill generation).
- Do not add a new route type without updating `docs/architecture.md` and the route-policy table in `docs/api/mcp-tools.md`.
- Capability-discovery (`unity_open_mcp_capabilities`), skill generation (`unity_open_mcp_generate_skill`), and `unity_open_mcp_manage_tools` are **local-only** — they must never depend on the live bridge or batch Unity. (`capabilities` and `manage_tools(list_groups)` may probe a live bridge via `GET /tools` for compiled-state availability, but they remain local-route — the probe is a read-only fetch, not a route classification change.)

## Tool-group visibility (M18 Plan 2)

- Canonical group catalog: `src/capabilities/tool-groups.ts` (single source of truth for group ids, descriptions, default-enabled flag, domain defines, and the per-tool group assignment). Adding/removing a group or moving a tool between groups happens there.
- Per-session state: `src/tool-session-state.ts` (`ToolSessionState`). Ephemeral, in-memory, per stdio server process. Resets to `core`-only on restart.
- `ListTools` filters via `filterVisibleTools(ALL_TOOLS, sessionState)` in `index.ts`. Always-visible meta-tools (capabilities, manage_tools, ping, …) bypass the filter via the `ALWAYS_VISIBLE_TOOLS` allow-list.
- `manage_tools` mutates the session state from `tool-router.ts#routeManageTools`. The bridge does NOT see these calls.
- When activate/deactivate/reset actually changes the visible tool set, the MCP server emits `notifications/tools/list_changed` (declares `tools.listChanged: true` in server capabilities). Clients should re-issue `tools/list` to refresh descriptors mid-session.
- Compiled-state availability (`available` per group) is probed via `LiveClient.listBridgeTools()` (`GET /tools`). `null` (unknown) when the bridge is offline; `true/false` when reachable. Capabilities and manage_tools share this probe.
- Domain groups (navigation, input-system, probuilder, particle-system, animation) carry `domainDefine` so an absent Unity package surfaces as `available: false (dependency missing: <package>)`.

## Instance discovery (M13)

- Bridge port resolution lives in `src/instance-discovery.ts`. Precedence: `UNITY_OPEN_MCP_BRIDGE_PORT` env var → `~/.unity-open-mcp/instances/<sha256(projectPath)>.json` lock file (when its `pid` is alive) → deterministic hash `20000 + (sha256(path) % 10000)`.
- The hash formula must stay byte-for-byte identical to the bridge mirror at `packages/bridge/Editor/Bridge/InstancePortResolver.cs` (`ComputePort`) **and** the Hub mirror at `hub/src-tauri/src/config/bridge_port.rs` (`compute_port`). The Hub writes the port into generated MCP client configs and pins it for the Step 5 Unity launch, so all three sides (bridge C#, MCP server TS, Hub Rust) must agree. Cross-side consistency is pinned by `instance-discovery.test.ts`, `InstancePortResolverTests.cs`, and the `bridge_port` Rust tests (they share fixtures including the demo path) — if any side changes the formula, update all three in the same task.
- The MCP server is **read-only** on the lock file. Stale-lock cleanup (PID-liveness sweep) is the bridge's job on its next `Acquire`; never delete or rewrite locks from this package.
- No new runtime deps: the module uses only `node:crypto`, `node:fs`, `node:os`, `node:path`.
- M14 — the same lock file carries an `authToken`. `resolveAuthToken` reads it and the token is threaded into `LiveClient` and `BridgeEventStream`, which attach `Authorization: Bearer <token>` to every request. When no token is discoverable (older bridge, dead-pid lock, or an explicit `UNITY_OPEN_MCP_BRIDGE_PORT` override that skips the lock read) no header is sent and the bridge must be in `authMode "none"`. The `InstanceLock` interface must mirror the bridge's lock schema; adding/removing a lock field is a cross-side change.
- **Dead-bridge detection.** `classifyInstance(lock)` reads the lock mid-session to distinguish a recoverable reload from a dead bridge assembly: a live PID with a stale heartbeat (`>= HEARTBEAT_STALE_MS`) means Unity is still running but the bridge's `[InitializeOnLoad]` never re-ran after a compile failure — `/ping` will not recover. `LiveClient` threads `projectPath` (from `index.ts`) so its `ensureReady`/`waitForCompile` paths can call this and return a structured `bridge_compile_failed` error immediately instead of hanging on the 120s compile-wait. The error points the agent at `unity_open_mcp_read_compile_errors`, the only error channel that works in this state (it reads `Editor.log` offline; `read_console` and `compile_check` are both dead with the broken bridge assembly). The bridge keeps the lock on disk during a domain reload specifically to make this signature available — see `packages/bridge/AGENTS.md` §Instance discovery.

## Offline reads

- The offline-read path (`src/offline.ts`, `src/compressible-router.ts`) deliberately avoids persistent on-disk caches. GUID indices and YAML parses are rebuilt per request. Do not add a disk cache without explicit approval — the no-cache philosophy is documented in `docs/architecture.md`.
- The session-scoped in-memory `AssetModelCache` (LRU, last 8) is the only allowed cached state, and only for drill-down reuse.

## Capabilities

- `src/capabilities/` holds the rule/fix catalog and the `buildCapabilities` transform. These mirror the C# verify package state — keep them in sync when rules/fixes change (see `packages/verify/AGENTS.md` §Capability catalog sync).

## Verification

- Run `npm run typecheck` and `npm test` after changes. Tests use `node --test` with type stripping.
- Tool contract changes: update the tool catalog in `docs/api/mcp-tools.md` in the same task.
- Capability changes: verify `build-capabilities.test.ts` covers the new rule/fix/surface.

## Agent skill sync (`skills/unity-open-mcp/SKILL.md`)

The agent skill is the **agent-facing** counterpart of the human/contributor docs. Two surfaces must stay in sync with tool/capability/routing changes — they serve different audiences:

- `skills/unity-open-mcp/SKILL.md` — **agent playbook**: workflows, principles, short routing narrative, pointers to `unity_open_mcp_capabilities`. Shipped into a game project's `.claude/skills/` / `.cursor/skills/` / `.opencode/skills/` / `.agents/skills/` (paths from `skills/client-paths.json`). Agents working in a game project see this file, not `docs/api/`.
- `docs/api/mcp-tools.md` — **human/contributor reference**: full tool catalog, route-policy table, batch support table, response-shape examples. Lives in the toolkit repo; not assumed visible to agents in a downstream game project.

When an MCP tool, capability, route policy, batch behavior, or agent-senses tool changes, update **both**:

- **Always** update `docs/api/mcp-tools.md` (tool catalog, route/batch tables, response shapes).
- **When the change affects agent workflow or routing guidance**, also update `skills/unity-open-mcp/SKILL.md`. Examples that require a SKILL edit: a new agent-senses tool, a new batch-eligible tool, a route-policy change, a new gate workflow, a new verify rule that changes the mutate→gate→fix loop.
- Pure schema tweaks (renaming an optional field, adding a non-workflow detail) only need the api doc.

Rules for the SKILL file:

- Keep it lean (~150–200 lines): workflows, principles, one canonical example per concept, pointers to `unity_open_mcp_capabilities` for details. Do **not** copy the full `docs/api/mcp-tools.md` tables into the skill.
- The `routing` object on the capabilities response is the machine-readable routing source — the SKILL only summarizes it.
- Do **not** put MCP client config JSON, install path tables, or `Setup without Hub` content in the skill. Those live in `docs/setup/agent-setup.md` / `docs/setup/manual-setup.md` / `docs/setup/wizard-setup.md`.
- The `unity_open_mcp_generate_skill` output stays a **project inventory** (packages, types, tool list from catalog). Do not reintroduce install/MCP JSON into generated skills.

The MCP package (this directory) owns SKILL sync with tool changes — same obligation as the api doc sync above.
