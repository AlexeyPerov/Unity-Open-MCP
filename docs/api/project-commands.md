# Project command catalog

`unity_open_mcp_project_commands` is always visible, independent of active tool
groups. It reads the live Editor catalog without an MCP server release, reconnect,
or dynamic tool registration. This release supports discovery only; invocation
and asynchronous jobs are not yet supported.

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
  reserved async/cancellation declarations and `invocationSupported: false`.
  Filters and paging are list-only; `id` is describe-only. Aliases are informational
  and are not resolved as exact ids.

Each entry reports `available` for declaration validity/enabled state. This does
not imply invocation support. Invalid and disabled entries remain discoverable,
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
cannot declare ReadOnlyHint. Async and Cancellable are reserved declarations;
Cancellable requires Async. They do not start jobs or promise cancellation.
Commands are kept out of direct bridge tool dispatch until safe invocation is
available. Built-in registry duplicate ids likewise reject all colliding
registrations instead of selecting an assembly-order winner.

## Bridge transport

Authenticated `GET /tools?catalog=project_commands&action=list` serves the same
versioned catalog. Use repeated `tag` query parameters for tags. Describe uses
`action=describe&id=<exact-id>`. This is a read-only metadata endpoint and does
not enqueue Unity work. Plain `GET /tools` retains its lightweight `tools` and
`groups` shape for compiled-state and compile-verification consumers.
