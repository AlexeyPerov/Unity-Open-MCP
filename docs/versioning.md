# Version compatibility

Unity Open MCP keeps the npm server, bridge, and verify package compatible by
releasing them as one versioned set. This page explains what users install and
how to recover from a mismatch.

Maintainers: repository synchronization, bumps, tags, and release workflows
live in [Maintainer versioning and releases](contributing/versioning.md).

## How versions are organized

| Artifact | Installed through | Version relationship |
|---|---|---|
| MCP server (`unity-open-mcp`) | npm / `npx` | Shared |
| Bridge | Unity Package Manager git URL | Shared |
| Verify | Unity Package Manager git URL | Shared |
| Unity Hub Pro | GitHub Release installer | Independent |

The first three artifacts ship breaking changes together and should use the
same `X.Y.Z`. Unity Hub Pro is optional and has its own release cadence.

## Find the running versions

```bash
npx unity-open-mcp status
```

The output includes the bridge version and a server/bridge compatibility
result. To print only the npm server version:

```bash
npx unity-open-mcp --version
```

Unity Package Manager also displays the installed bridge and verify versions.

## Compatibility warning

At the first successful connection, the server prints one advisory warning when
the running bridge is incompatible. It does not block the connection.

Before 1.0, the minor number is the breaking axis:

| Server | Bridge | Result |
|---|---|---|
| `X.Y.Z` | `X.Y.Z` | identical |
| `0.5.0` | `0.5.1` | compatible patch drift |
| `0.5.0` | `0.4.0` | incompatible |
| `0.5.0` | `0.6.0` | incompatible |

These are illustrative values, not current release numbers. After 1.0,
standard major-version compatibility applies.

## Resolve a mismatch

When the bridge is older:

1. Open **Window → Package Manager** in Unity.
2. Update Unity Open MCP Bridge to the version reported by the server.
3. Update Unity Open MCP Verify to the same version.

When the server is older, update its npm pin to the bridge version:

```bash
npx unity-open-mcp@<bridge-version> status
```

For a global installation:

```bash
npm install -g unity-open-mcp@<bridge-version>
```

Keep the MCP client command and both UPM pins aligned when moving to a new
release. [Manual setup](setup/manual-setup.md) shows the package pins, and
[MCP client configuration](setup/client-configuration.md) owns the client
command shapes.

## Suppress an intentional warning

```bash
export UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1
```

Set this in the environment that launches the MCP server. It suppresses only
the warning; it does not make incompatible versions compatible.

## Unity Editor compatibility

The bridge and verify package manifests require Unity 2022.3 LTS or newer,
including Unity 6. The Node-based MCP server itself does not depend on a Unity
Editor version.

## Unity Hub Pro

Unity Hub Pro is a separate desktop app with an independent version and release
cadence. It does not need to match the server/bridge/verify set. Installers are
published through `hub-v*` GitHub Releases.
