// M22 Plan 1 / T22.1.5 — Rich server instructions.
//
// The MCP `initialize` response carries an `instructions` string that clients
// may inject into the system prompt. This module builds that string: a concise,
// agent-facing briefing on how to drive Unity through this server cheaply and
// correctly. Three concerns, in priority order:
//
//   1. Payload sizing & paging — start compact, expand on demand, bound with
//      page_size. The single biggest agent mistake is pulling a full verbose
//      dump into context when a folded overview + drill-down would do.
//   2. Unity-API verification workflow — reflect before writing C#. LLM
//      training data frequently contains incorrect or outdated Unity APIs;
//      the server exposes the real types/members so an agent can verify.
//   3. Mutate → gate → fix — every mutation runs a scoped gate; the delta is
//      inline in the response, so no separate read_console is needed.
//
// AGENTS.md §"No internal references" applies: the instructions are a
// user-visible surface, so they MUST stay clean of internal IDs (no milestone
// numbers, no specs paths, no execution-plan task references, no `/references`
// project names). State the contract on its own merits.
//
// Pure string builder — no I/O, no cross-file runtime imports — so it loads
// cleanly under `node --experimental-strip-types` in tests.

// Banned substrings — the instructions must never reference internal working
// artifacts. Checked at test time so a future edit cannot regress this.
//
// Kept narrow on purpose: milestone IDs (M<n>), specs/ paths, /references
// project handles, and execution-plan task IDs (T<n>.<n>.<n>). Plain English
// ("Plan", "milestone", "task") is allowed and used naturally.
const BANNED_INSTRUCTION_PATTERNS: RegExp[] = [
  /\bM\d+(\.\d+)*\b/, // M1, M4.5, M22.1, ...
  /\bT\d+\.\d+\.\d+\b/, // T22.1.5, T2.4, ...
  /specs\//i,
  /\/references\//i,
  /\bIvanMurzak\b/i,
  /\bAnkleBreaker\b/i,
  /\bUnityLauncherPro\b/i,
  /\bunity-scanner\b/i,
  /\bUnity-MCP\b/i,
  /\bunity-mcp-beta\b/i,
  /\bCoplay\b/i,
  /\bUCP\b/,
];

/**
 * Build the server instructions string. Memoized at the module level so
 * repeated `Server` constructions (tests, multi-project) do not re-stringify.
 */
export function buildServerInstructions(): string {
  return [
    "This server drives the Unity Editor. Tool prefixes signal routing:",
    "`unity_open_mcp_*` tools route through the live bridge (or fall back to a",
    "headless batch Unity / offline disk parsers); `unity_senses_*` tools are",
    "live-only and never batch. Call `unity_open_mcp_capabilities` first to",
    "learn the full surface, then activate the tools you need. Prefer the",
    "intent path `unity_open_mcp_manage_tools(action=\"activate_for\",",
    "intent=\"…\")` over hand-picking group ids — state what you are about to",
    "do and the server brings the right groups online in one call (use",
    "action=\"suggest\" with the same intent/tags to preview without changing",
    "state). `action=\"list_groups\"` lists every group with its active flag,",
    "compiled-state availability, and tool roster.",
    "",
    "PAYLOAD SIZING & PAGING (avoid flooding context)",
    "- The heavy tools (`read_asset`, `search_assets`, `scene_get_data`,",
    "  `find_references`, `validate_edit`, `scan_paths`) return variable-size",
    "  payloads. Start with the default `compact` profile and expand on demand.",
    "- `profile`: `compact` (default) | `balanced` | `full`. compact is the",
    "  folded/counts shape; balanced is the inline-detail shape; full is the",
    "  verbose tree. An explicit `profile` wins over the legacy `detail` alias.",
    "- `page_size` + `cursor` / `next_cursor`: uniform paging. Set `page_size`",
    "  to bound any profile; follow `pagination.next_cursor` to resume. Omit it",
    "  to receive the whole (profile-shaped) payload in one response.",
    "- Recommended starting page sizes: ~25 for `search_assets` / `validate_edit`",
    "  / `scan_paths`, ~40 for `read_asset`, ~50 for `scene_get_data` /",
    "  `find_references`. See the `costHints` block on `capabilities` for",
    "  per-tool profile cost bands.",
    "- Prefer drill-down over re-fetching: `read_asset` exposes `component`,",
    "  `path`, `id`, `override` flags so a compact response can be expanded",
    "  without re-reading raw YAML.",
    "",
    "UNITY-API VERIFICATION (reflect before you write C#)",
    "- LLM training data frequently contains incorrect, outdated, or",
    "  hallucinated Unity APIs. Verify before answering Unity-API questions or",
    "  writing C#.",
    "- Workflow: `search_assets` (find the actual shader/material/script in the",
    "  project) → `find_members` (real type + member signatures) →",
    "  `type_schema` (fields/properties on a type) → only then",
    "  `execute_csharp` / `invoke_method` with the verified API.",
    "- Common hallucination areas: shaders/materials (always search assets for",
    "  real shader names), package-specific APIs (Input System, Cinemachine,",
    "  ProBuilder, NavMesh, URP/HDRP), and APIs that changed between Unity",
    "  versions. The bridge reflects the *actually installed* Unity + packages.",
    "",
    "MUTATE → GATE → FIX",
    "- Mutating tools require non-empty `paths_hint` (empty fails with",
    "  `paths_hint_required`). Scope it to the assets you touch.",
    "- Every mutation runs a scoped gate (checkpoint → mutate → validate →",
    "  delta) and returns the delta inline (`gate.delta`, `agentNextSteps`).",
    "  Read it — mutation success ≠ project safe.",
    "- Every response also carries a `logs[]` array: Unity console entries",
    "  captured *during that call*. You do NOT need to poll `read_console`",
    "  after a mutation to learn what happened — read `logs` first; escalate to",
    "  `read_console` only for the global console buffer with stack traces.",
    "- On gate failure, prefer `apply_fix` with `dry_run: true` first; review",
    "  the preview, then apply. Some fixes are unsafe (need a `target_guid`).",
    "",
    "ROUTING & OFFLINE",
    "- `list_assets`, `search_assets`, `find_references`, `read_asset`, and",
    "  `read_compile_errors` work without a live bridge (offline disk parsers).",
    "  Most other tools need the bridge running.",
    "- If a tool returns `bridge_offline` or `bridge_compile_failed`, an",
    "  offline bridge after a C# edit is frequently a *symptom* of a failed",
    "  compile, not \"Unity isn't running.\" Call `read_compile_errors` — it",
    "  reads Editor.log offline and survives a dead bridge.",
    "- One test run at a time: never start a second `unity_senses_run_tests`",
    "  before the first resolves. Never launch a second Unity instance for the",
    "  same project (Unity holds a per-project lock).",
    "",
    "PATH CONVENTIONS",
    "- Unless specified otherwise, all asset paths are relative to the",
    "  project's `Assets/` folder and use forward slashes.",
  ].join("\n");
}

let cached: string | null = null;

/**
 * Return the server instructions, memoized. The string is constant for the
 * lifetime of the process, so a single computation is sufficient.
 */
export function getServerInstructions(): string {
  if (cached === null) {
    cached = buildServerInstructions();
  }
  return cached;
}

/**
 * Test hook: assert the instructions carry no internal IDs / reference-project
 * handles. Exposed so the test suite can call it without duplicating the
 * banned-pattern list.
 *
 * @returns an array of offending patterns (empty when clean).
 */
export function findBannedInstructionsReferences(text: string): string[] {
  const offenders: string[] = [];
  for (const pattern of BANNED_INSTRUCTION_PATTERNS) {
    const match = pattern.exec(text);
    if (match) offenders.push(match[0]);
  }
  return offenders;
}
