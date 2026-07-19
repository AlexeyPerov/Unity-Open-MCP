# Development setup

Use a local checkout to work on the bridge, verify package, MCP server, Hub, or
community packs. User installation paths live in [Manual setup](manual-setup.md)
and [Agent setup](agent-setup.md).

## Requirements

- Unity 2022.3 LTS or newer
- Node.js 18 or newer
- An MCP client that supports stdio servers

## 1. Install local Unity packages

Point the target Unity project's `Packages/manifest.json` at the checkout:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../unity-open-mcp/packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../unity-open-mcp/packages/verify"
  }
}
```

Adjust the relative path for your layout. A Unity project inside this monorepo
can typically use:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
  }
}
```

Unity recompiles package source after refresh/focus. Package-local validation
requirements are documented in each package's `AGENTS.md` and README.

## 2. Build and configure the MCP server

```bash
cd mcp-server
npm install
npm run build
```

Use the local-checkout command in
[MCP client configuration](client-configuration.md), preserving the config shape
for your client:

```json
{
  "command": "node",
  "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
  "env": {
    "UNITY_PROJECT_PATH": "/absolute/path/to/project"
  }
}
```

Startup-dialog policy and macOS Accessibility requirements are owned by
[Dialog policy](../dialog-policy.md).

## 3. Optional embedded domains

Domain tools ship inside the bridge. Matching Unity dependencies make
package-gated domains compile in; session activation controls whether most
groups appear to an MCP client.

Use [Extensions](../extensions.md) for the canonical dependency catalog,
manifest examples, and activation table. Community pack authoring has a
separate contract in [Contributing — extensions](../contributing/extensions.md).

## 4. Launch and verify

1. Open the target Unity project.
2. Wait for scripts to compile.
3. Restart the MCP client so it reloads the local command.
4. Call `unity_open_mcp_ping` or `unity_open_mcp_capabilities`.

For bridge, listener, compile, and test-worker recovery, use
[Contributor troubleshooting](../troubleshooting-contributors.md).

## Community-pack workflow

`packages/extensions/` is reserved for separate community domain packs. Shipped
domains are embedded in the bridge and do not use standalone extension package
entries.

A local community pack can be installed with its own UPM id:

```json
{
  "dependencies": {
    "com.example.my-mcp-ext": "file:../../my-mcp-ext"
  }
}
```

Follow [Contributing — extensions](../contributing/extensions.md) for compile
gates, tool registration, tests, and documentation ownership.

## Maintainer releases

The npm package is published from `mcp-server/`; bridge and verify are consumed
through git tags. Unity Hub Pro releases independently.

[Maintainer versioning and releases](../contributing/versioning.md) is the
canonical owner for:

- version sources and generated targets;
- sync and CI drift checks;
- shared-trio and Hub bump commands;
- tag namespaces and release workflows;
- GitHub Release ownership of release notes.

The Hub maintainer panel can run build/test, npm dry-run/publish, and the shared
version-sync script. It uses the maintainer's existing npm authentication and
does not create commits or tags.

## Related docs

- [MCP client configuration](client-configuration.md)
- [Wizard setup](wizard-setup.md)
- [Architecture](../architecture.md)
- [MCP tools API](../api/mcp-tools.md)
