# Project commands

`unity_open_mcp_project_commands` is always visible, independent of active tool
groups. It reads the live Editor catalog without an MCP server release, reconnect,
or dynamic tool registration. Synchronous invocation uses the built-in safety pipeline.
Explicitly async commands execute through [jobs](jobs.md).

## Discovery

- `action: "list"`: compact entries sorted by exact id and declaring method.
  Optional `query` matches a substring of id, title or description, ignoring case.
  `tags` matches all tags exactly; `group` and `package` match exactly.
  `offset` defaults to 0; `limit` defaults to 20 and must be 1–100.
  The response contains `catalogVersion: 1`, `total`, `offset`, `limit`,
  `nextOffset` (null at the end), and `commands`. Repeat with `nextOffset` to page.
  A reload can change the catalog between pages; restart paging after recompiling.
- `action: "describe", id: "project.demo.catalog_fixture"`: one exact lookup
  returns full `inputSchema`, safety/lifecycle metadata, deprecated aliases,
  async/cancellation declarations, `schemaVersion`, declaring assembly/type,
  declared `pathsHint`, and `invocationSupported: true`.
  Filters and paging are list-only; `id` is describe-only; invocation uses `command_id`. Aliases are informational
  and are not resolved as exact ids.

Each entry reports `available` for declaration validity/enabled state. This does
not imply cancellability; inspect `async` and `cancellable`. Invalid and disabled entries remain discoverable,
with method-qualified diagnostics. Duplicate ids or aliases make all affected
commands unavailable, regardless of assembly load order. Exact lookup of a
colliding id returns `duplicate_command_id`; an unknown id returns
`command_not_found`. A missing Editor returns `catalog_unavailable`; an older
bridge returns `catalog_unsupported`. No batch Unity process is started.

## Authoring

Use an Editor-only assembly referencing
`com.alexeyperov.unity-open-mcp-bridge.Editor`. Mark the class `[BridgeToolType]`
and a public static method returning a JSON string with `[ProjectCommand]`:

```csharp
using UnityOpenMcpBridge;

[BridgeToolType]
public static class Reports
{
    [ProjectCommand("project.content.report", Title = "Content report",
        Description = "Summarize project content", Package = "content-tools",
        Group = "reports", Tags = new[] { "content" },
        ReadOnlyHint = true, IdempotentHint = true, Gate = GateMode.Off)]
    public static string Report(
        [ProjectCommandParameter(Description = "Maximum entries",
            Minimum = 1, Maximum = 50, Examples = new[] { "10" })] int limit = 10)
        => "{\"count\":0}";
}
```

IDs and deprecated command aliases must match
`project.<owner>.<command>` using lowercase ASCII letters, digits, underscores
and hyphens (dots also allowed inside the command portion). Built-in
`unity_open_mcp_*` and `unity_senses_*` names are reserved. Title, Description,
and Package are required. Group is a project-owned category, independent of MCP
session groups. Tags are deduplicated and sorted. Test assemblies referencing
NUnit are excluded from normal discovery.

The demo embeds a fixture package at
`demo/Packages/com.unity-open-mcp.project-command-fixture`, containing only
project-side C# and a bridge assembly reference. Recompile that package to see
its command in the catalog; no MCP code changes are necessary.

## Parameter and safety contract

JSON Schema draft 2020-12 is generated from the reflected signature. Property
names preserve C# parameter spelling exactly; unknown properties are forbidden.
Every parameter without a C# default is required, including nullable parameters.
A default makes a parameter optional. Requiredness and nullability are separate.

| CLR shape | JSON representation |
|---|---|
| `string` | string or null |
| `bool` | boolean |
| `int` | integer in signed 32-bit range |
| `long` | decimal string, preserving Int64/Unity instance-id precision |
| `float`, `double` | number |
| non-flags enum | enum name string; names sorted ordinally |
| `Nullable<T>` | underlying supported value type or null |
| one-dimensional array of a supported scalar | array or null; typed items |

Objects, dictionaries, generic methods, instance methods, ref/out parameters,
flags enums, multidimensional and jagged arrays, and other CLR types are rejected
at registration. There is no fallback schema pretending these values are strings.

`ProjectCommandParameter` adds descriptions, numeric Minimum/Maximum for
int/float/double, JSON-value strings in Examples, and DeprecatedAliases metadata.
`System.ComponentModel.Description` is also supported. Alias annotations do not
permit alternate argument keys in this release. Defaults and enum values are
serialized deterministically using invariant culture.

`ProjectCommand` inherits IsMutating, Gate, Lifecycle, ReadOnlyHint,
IdempotentHint, DestructiveHint and Enabled from `BridgeTool`. Mutating commands
cannot declare ReadOnlyHint. `Async` opts into jobs and requires the signature
below; `Cancellable` requires Async and cooperative token handling.
Mutating declarations must choose a non-None Lifecycle; read-only declarations
must use None. `PathsHint` optionally declares fixed project-relative mutation
paths. Commands remain outside direct per-command bridge dispatch; invocation
uses the stable meta-tool. Built-in registry duplicate ids likewise reject all colliding
registrations instead of selecting an assembly-order winner.

## Bridge transport

Authenticated `GET /tools?catalog=project_commands&action=list` serves the same
versioned catalog. Use repeated `tag` query parameters for tags. Describe uses
`action=describe&id=<exact-id>`. This is a read-only metadata endpoint and does
not enqueue Unity work. Plain `GET /tools` retains its lightweight `tools` and
`groups` shape for compiled-state and compile-verification consumers.

## Invocation

```json
{
  "action": "invoke",
  "command_id": "project.demo.catalog_fixture",
  "args": { "labels": ["example"], "limit": 3 }
}
```

The server fetches a fresh description and validates unknown, missing, wrong-type,
array, enum and range arguments before sending a POST. The bridge repeats strict
validation and CLR range checks before scheduling any command. Parameter aliases
remain informational. Defaults come from the C# signature; nullable required
parameters must still be present. Transport options belong outside `args`.
Project parameters do not inherit built-in GameObject selector conventions.

Optional `schema_version` pins a description's `schemaVersion`. The version is a
SHA-256 fingerprint of the schema, declaring module/method and execution policy;
it changes after rebuilding the declaring assembly or changing the contract.
The MCP server always forwards the version it validated. A change between describe
and dispatch yields `command_schema_changed` without executing stale arguments.

Mutations use the built-in request queue and `GatePolicy.Execute`. Scope is the
union of declared `PathsHint` and explicit `paths_hint`; callers cannot narrow a
declaration. Without declared paths, explicit scope is mandatory even with
`gate: "off"`. Paths must be below `Assets/` or `Packages/`, without traversal,
absolute paths, empty segments, or a broad root fallback. Declarations describe
trusted project code; scope is a validation boundary, not a filesystem sandbox.

`gate` follows the built-in precedence: valid request value, then the project
setting. Attribute Gate is a recommendation. Read-only commands need neither a
scope nor a gate and report `effectiveReadOnly`. Declared lifecycle controls the
usual dirty-scene guard (including unsaved additive scenes), optional project
auto-save and settle waits;
`ignore_scene_dirty` is the explicit top-level opt-out. Arguments cannot override
mutability, lifecycle or transport options. `timeout_ms` uses the usual host-safe
55-second maximum. A timeout or lost reload response can mean the command already
ran: inspect post-state before retrying. Ambiguous project-command POST failures
are never automatically replayed; a pre-dispatch compiling/503 response may be
retried against the same pinned version.

Project settings accept `projectCommandDenyPatterns`, an optional array of regular
expressions matched against the exact command id. Omitted/empty means no denied
commands. Matching uses the shared bounded deny matcher; a match or regex timeout
refuses execution. Bypass requires both effective `gate: "off"` and
`confirm_bypass: true`. Disabled declarations/tool toggles cannot be bypassed.
This policy does not inspect compiled method bodies or sandbox their Unity APIs.
Existing C# and menu deny policies continue to apply to their own tools.

Successful results use `mutation.output.result`, normalized through the shared
bounded serializer (depth 4, 100 collection entries, input JSON at most 1 MiB).
Collection truncation follows that serializer's existing representation. Invalid
JSON or exceptions produce an execution error with an intact envelope. A mutating
command that throws or returns invalid output is treated as possibly partially
committed, so validation and lifecycle settling still run.

The built-in gate/lifecycle/next-step envelope additionally carries
`projectCommand`: exact id, schemaVersion, declaringAssembly, declaringType,
effective mutability, lifecycle, gate, scoped paths and `jobId` (null for synchronous
execution). Opt-in audit records include this identity/policy plus duration,
outcome and the usual gate delta. Preflight refusals and timeouts are recorded too. A `started` record is written
before user code, so a reload cannot erase evidence of dispatch; without a terminal
record the outcome is unknown, and duration 0 on the start record is not execution time.
No command argument values are added to audit records.

Synchronous invocation of an async declaration returns `async_not_supported`
without starting work; use jobs instead. CustomConfirmation lifecycle returns
`lifecycle_not_supported`. Project commands
cannot be nested in `batch_execute`; use a top-level invocation.

Authenticated `POST /tools/unity_open_mcp_project_commands` accepts the invocation
shape above and the transport options. Direct HTTP callers receive the same
bridge validation and safety pipeline. Discovery remains on the read-only GET
catalog endpoint; no client tool-list refresh or reconnect is required.


## Asynchronous authoring

An async command returns `Task<string>` containing JSON and accepts exactly one
injected `ProjectCommandContext`. The context is omitted from the JSON Schema;
all other parameters use the same supported types and validation as synchronous
commands. Async commands must declare `Lifecycle.None` for reads or
`Lifecycle.EditorSettle` for mutations. Reloading user code cannot be resumed.

```csharp
using System.Threading.Tasks;
using UnityOpenMcpBridge;

[BridgeToolType]
public static class AsyncCommands
{
    [ProjectCommand("project.example.prepare", Title = "Prepare report",
        Description = "Prepare a report cooperatively.", Package = "example.tools",
        Async = true, Cancellable = true, ReadOnlyHint = true, Gate = GateMode.Off)]
    public static async Task<string> Prepare(ProjectCommandContext context)
    {
        context.ReportPhase("preparing report");
        await Task.Delay(1000, context.CancellationToken);
        context.CancellationToken.ThrowIfCancellationRequested();
        return "{\"prepared\":true}";
    }
}
```

Keep Unity API calls on the captured Editor context. Do not use `Task.Run`,
`ConfigureAwait(false)`, blocking sleeps, or long work before the first yield for
Unity operations. Cancellation is acknowledged by throwing
`OperationCanceledException` after the context token is cancelled. Honor the
token only at boundaries where it is safe to stop; partial mutations still get
terminal validation and are not automatically rolled back. Report actual phases,
not estimated percentages. The demo fixture `project.demo.long_write` demonstrates
a 60-second preparation followed by a scoped asset write.

Authenticated `POST /project-command-jobs` is the private bridge adapter transport.
It accepts `action: start | status | cancel`, a UUID `job_id`, and `invocation`
(the ordinary invoke envelope) on start. Records are scoped to `X-Agent-Id`;
unknown owners and lost records return `job_not_found`. Normal MCP callers use
`unity_open_mcp_jobs`, which owns scheduling, idempotency and retained results.

## Assembly setup and verification

Place the example above in `Editor/Reports.cs`, beside this
`Editor/ContentTools.Editor.asmdef` (use a unique assembly name):

```json
{
  "name": "ContentTools.Editor",
  "references": ["com.alexeyperov.unity-open-mcp-bridge.Editor"],
  "includePlatforms": ["Editor"]
}
```

Install the bridge and verify packages first. The Editor-only platform constraint
keeps declarations and their bridge dependency out of player builds. After a
clean compile, list with `query: "project.content"`, describe the exact
`project.content.report` id, then invoke with `args: {"limit": 10}` and the
returned `schema_version`. Expect `mutation.output.result.count: 0` and a skipped
read-only gate. Try `limit: 51` and an unknown property: both must fail before
user code. Recompile after changing the signature and confirm the old version
is refused. Do not hardcode a schema fingerprint across builds.

For mutations, test a disposable declared folder with gate enforce, missing or
traversing scope, denied/disabled declarations, and partial failure. For async
commands also test status immediately after start, a short observation timeout,
same-key retries, cancellation before the first write, and terminal gate retention.
The [demo fixture](../../demo/Packages/com.unity-open-mcp.project-command-fixture/README.md)
provides executable read, write, long-running, and partial-output examples.

## Security boundaries

Commands execute trusted Editor code with the Editor's filesystem privileges.
Review the declaring assembly/type as well as the exact id; duplicate identities
fail closed. Read-only and cancellation annotations are author promises, not a
sandbox. Keep secrets out of returned JSON, exception messages and progress text:
the shared serializer bounds output but does not discover or redact arbitrary
secrets in project-authored strings. Audit deliberately omits argument and result
payloads, recording identity, scope, timing and gate outcome instead. The existing
opt-in audit rotation bounds retained files; jobs additionally bound arguments,
results and phase text as described in [job limits](jobs.md#identity-limits-and-retention).

The authenticated loopback bridge and Editor-only assembly are the execution
boundary. Agent/project/port ownership isolates job observations within the MCP
process; agent ids are trusted routing metadata, not separate user credentials.
Neither gate off nor a caller-provided scope enables disabled declarations,
bypasses schema validation, or grants access to another owner's job.
