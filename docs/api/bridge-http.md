# Bridge HTTP API

Unity bridge endpoints are served by `packages/bridge/Editor/Bridge/BridgeHttpServer.cs`.

Default bind is loopback (`127.0.0.1`).

## Endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/ping` | `GET` | Bridge and editor health snapshot. |
| `/instance` | `GET` | Runtime instance metadata snapshot. |
| `/events` | `GET` | SSE event stream (console/editor-state events). |
| `/events/poll` | `GET` | Pull-style event drain endpoint. |

Subscriber lifecycle on both event endpoints: a client-supplied `subscriber`
id persists across polls/reconnects and keeps its cursor; an id the bridge
mints (no `subscriber` param) is retired with the request/stream so anonymous
clients don't accumulate subscriber state.
| `/compile-state` | `GET` | Read-only main-thread CompilationPipeline generation, source-content match, current errors, and before/after assembly mtimes. Unconfirmed generations return `indeterminate`. |
| `/tools` | `GET` | Compiled-state tool inventory + group→tools map (used by capabilities / manage_tools for per-group availability). |
| `/tools/{toolName}` | `POST` | Execute one bridge tool. |
| `/resources` | `GET` | List bridge resources. |
| `/resources/{route}` | `GET` | Read one bridge resource payload. |

## Port and discovery

- Default port is deterministic per project path.
- Optional override:
  - `UNITY_OPEN_MCP_BRIDGE_PORT`
  - Unity arg `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>`
- Running bridge writes an instance lock file under `~/.unity-open-mcp/instances/`.
- MCP server uses that lock for project-to-port resolution.

## Authentication and bind mode

- `authMode: "none"` (default): requests accepted without bearer token.
- `authMode: "required"`: requests must send `Authorization: Bearer <token>`.
- Remote bind (`bindAddress: "0.0.0.0"`) is intended for controlled environments and should be paired with `authMode: "required"`.
- If remote access is needed, terminate TLS in front of the bridge (reverse proxy or tunnel).

## Tool execution envelopes

`POST /tools/{toolName}` responses typically include:

- `ok`: success marker
- `result`: tool payload (on success)
- `error`: structured error object (on failure)
- optional lifecycle/settle metadata for mutating tools

## Request limits

`POST /tools/{toolName}` request bodies are capped at 64 MB. A body that
exceeds the cap — whether declared via `Content-Length` or streamed (chunked) —
is rejected with HTTP `413` and the standard error envelope:

```json
{ "error": { "code": "request_too_large", "message": "Request body of N bytes exceeds the 67108864-byte limit." } }
```

Legitimate JSON tool arguments are kilobytes; the cap exists so a caller cannot
OOM the Editor with an unbounded body. In `authMode: "none"` (local dev) any
caller on the bind address can reach this path.

## Gate policy

For a mutating tool, the bridge selects the effective gate mode in this order:

1. A valid request-level `gate` value (`"enforce"`, `"warn"`, or `"off"`).
2. The project default in `.unity-open-mcp/settings.json`.

An omitted, malformed, or unknown request value falls back to the project
default. The gate value declared by a registry tool is catalog/recommendation
metadata; it does not override that project default during dispatch.

`GatePolicy.Execute` then applies the selected mode to the
checkpoint → mutate → validate → delta flow. Changing this precedence or its
fallback behavior is a bridge API contract change.

Two honesty rules govern the gate's response:

- `gate.delta` is `null` (never a zeroed object) whenever no delta was
  computed — the checkpoint failed, the mutation failed before validation, or
  the validate scan could not complete. A zeroed-looking delta always means
  "computed and clean".
- When a verify rule **throws** during the checkpoint or validate scan, the
  gate reports `gate.outcome: "validate_scan_failed"` with
  `gate.rulesFailed: [<ruleId>, …]` and withholds the delta: a failed rule
  contributes no issues, so its pre-existing problems would read as phantom
  "new"/"resolved" counts. The mutation itself still committed — verify health
  manually with `validate_edit` / `scan_paths`.

`paths_hint` is mandatory for every mutating call and is **not** waived by
`gate: "off"`. It is the declared mutation scope recorded in the audit trail,
not only the gate's validation scope, and there is no whole-project fallback.
The check runs before dispatch, so a missing `paths_hint` returns
`error.code: "paths_hint_required"` with `gate.skippedReason:
"request_rejected"` — nothing ran and no gate was evaluated. Tools that expose
`read_only` (today: `execute_csharp`) waive the requirement for inspection-only
requests; known disruptive calls keep it. A read-only `execute_menu` path and
an all-read batch waive it too.

Rule auto-selection maps the `paths_hint` extensions to rule families. Only
**registered** rule families are ever auto-selected: image (`.png`, `.jpg`,
`.jpeg`, `.tga`) and audio (`.wav`, `.mp3`, `.ogg`) paths fall back to
`missing_references` + `dependencies`, because the `textures`,
`sprite_2d_analysis`, and `audio_analysis` families are planned-but-not-shipped
and selecting them would run zero rules (a vacuous gate pass).

## Health check example

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

Use `/ping` to confirm:

- bridge reachability
- compile/play state
- readiness before running mutating tools
- the installed bridge's wire-contract revision

The response body carries `bridgeVersion` (the package semver) **and**
`wireContract` (an integer bumped for every observable change to the
request/response contract — accepted parameter keys, error codes, envelope
fields, declared schema ceilings). The semver does not move for a wire-contract
fix, so `wireContract` is what distinguishes a stale install from a regression:
`unity_open_mcp_bridge_status` compares it against the revision its MCP server
was built against and reports `wireContract.stale`. A bridge old enough to omit
the field is, by definition, older than revision 1.

## Related docs

- [Routing and lifecycle](routing-lifecycle.md)
- [MCP client configuration](../setup/client-configuration.md)
- [Architecture](../architecture.md)

### Request-scoped JSON responses

Wire contract revision 2 adds `GET /compile-state` and `X-Request-Id` echoing.
JSON responses are fully validated before headers are committed. Each request
can send at most one envelope, with explicit UTF-8 content length and connection
closure; a failed write aborts the response rather than appending another error.
A late timed-out dispatch cannot send into the next request. Invalid JSON is
replaced by HTTP 500 `invalid_response_json`.

Per-call console logs use Unity Console mode flags: scripting/import/compiler
warnings retain `warning` severity, compiler errors and exceptions retain `error`,
and ordinary managed logs remain `log`.

### Effective read-only requests and gate outcomes

The bridge derives request mutability before scope enforcement, queueing, and
checkpoint/undo setup. Inspection-only `execute_csharp(read_only:true)`, explicitly
marked `[BridgeReadOnlyMenu]` verifier menu handlers, and all-read batches skip
the gate even when the caller supplies `paths_hint`. Read-only snippets and
non-disruptive menus also skip dirty-scene checks and settle waits. The annotation
is a caller/author assertion, not a sandbox: known disruptive snippet calls keep
the conservative lifecycle; indirect calls cannot be proven safe.

`batch_execute` preflights every nested command using generated MCP schema
constraints and live tool/lifecycle availability before any dispatch. All-read
batches require no scope; mixed/mutating batches require the explicit union
`paths_hint`, even with `gate: "off"`. Invalid steps are reported together.

A failed mutation before validation has `gate.outcome: "skipped"`,
`skippedReason: "mutation_failed"`, `skipped: true`, and null `validation` and
`delta`. A completed checkpoint ID remains available. Gate failure counters do
not count such failures; activity still records the failed operation. A partial
batch validates committed mutations and reports the actual validation outcome
independently of `mutation.success`. Existing delta and next-step fields remain.
`effectiveReadOnly` is additive response metadata; audit records also include it
and `skippedReason`. `request_rejected`, `gate_off`, `no_scope`, `read_only`, and
`play_mode` identify other skip causes.

### Published argument schemas

Requests for shipped tools are validated against the generated MCP schemas
before dispatch. Unknown keys, selector conflicts, invalid types, bounds and
enums return HTTP 400 with `invalid_arguments`. Dispatcher-owned transport
fields remain valid at the outer request boundary; nested batch commands keep
their stricter scope/lifecycle preflight.

Canonical locator keys and their declared deprecated aliases map structurally
to handler arguments, including component patch `property_path`. Arbitrary
patch values are not rewritten. Direct HTTP alias use is reported in the
`X-Unity-Open-MCP-Deprecations` response header. See the
[locator contract](mcp-tools.md#locator-and-argument-contract) for examples.

## Project command metadata

`GET /tools?catalog=project_commands` selects the bounded, versioned
[project command catalog](project-commands.md#bridge-transport). Plain `GET /tools`
keeps the existing compiled inventory response. Duplicate built-in registry ids
are rejected for every candidate and reported in registration diagnostics.


### Project-command jobs

Authenticated `POST /project-command-jobs` is the domain-local execution endpoint
behind the MCP [jobs API](jobs.md). Start carries a UUID `job_id` and the ordinary
project-command `invocation` envelope; status/cancel carry that id. The bridge
checks the same declaration, parameter schema, scope, deny rules, and gate default
as synchronous invocation. It returns immediately, then runs the command on the
Editor context. Checkpoint and terminal validation share the normal gate policy.
Only one project job executes at a time; other bridge mutations and test starts
are refused while it owns the Editor scope. Job records are owned by `X-Agent-Id`
and lost on domain reload, which the MCP adapter reports as an unknown outcome.
