# Version compatibility

Unity Open MCP keeps the npm server, bridge, and verify package compatible by
releasing them as one versioned set. This page explains what users install and
how to recover from a mismatch.

Maintainers: repository synchronization, bumps, tags, and release workflows
live in [Maintainer versioning and releases](contributing/versioning.md).
For the end-to-end Hub, MCP, and Unity package procedure—including offline
installation—see [Updating](updating.md).

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

For a normal upgrade, start with `unity-open-mcp update` and follow the complete
[update order](updating.md#update-everything). The manual pins below remain the
fallback when you need a specific version.

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

## Switch a whole project to a release

### Update the open project from the bridge window

For one project that is already open in Unity, choose **Tools → Unity Open MCP
Bridge → Status → Updates**. Click **Check latest**, review **Preview**, then
**Apply**. The preview lists every selected project config, home-scoped config,
agent-facing document/example, and the bridge + verify package step before any
file changes.

Home-scoped files are updated only when their `UNITY_PROJECT_PATH` (or
deterministic bridge port) identifies this project and the file does not also
configure another project. Config and prose files receive a one-time `.bak`
before their first write. The package step uses one Unity Package Manager
request for verify and bridge; it is disabled for embedded or `file:`
development installs so the updater cannot replace a checkout. After applying,
wait for Unity to reload, restart the MCP/AI client, and run `status` or `ping`.

Agents can preview the identical plan with `unity_open_mcp_upgrade` in the
`typed-editor` group. `dry_run` defaults to `true`; set it to `false` only after
the report has been reviewed. A hand-entered `target_version` selects an older
published release without asking the npm registry for the latest version.

### Update another or multiple projects from a checkout

A project pins the version in more than one place: once per AI client config and
once per UPM package. From a clone of this repository, one command moves all of
them together. Preview first:

```bash
node scripts/switch-project-version.mjs /path/to/my-game 0.9.0 --dry-run
```

Then apply:

```bash
node scripts/switch-project-version.mjs /path/to/my-game 0.9.0
```

Pass the folder your AI client is opened on. The Unity project does not have to
sit there — for a repository whose Unity project is in `Client/`, the same
command updates the client configs at the top and the UPM pins below:

```
Unity Open MCP → 0.9.0
  project: /path/to/my-game
  Unity project: Client

  .cursor/mcp.json                    npm pin                     0.8.4 → 0.9.0
  .codex/config.toml                  npm pin                     0.8.4 → 0.9.0
  Client/Packages/manifest.json       UPM git pin                 0.8.4 → 0.9.0 ×2
  Client/Packages/packages-lock.json  UPM git pin                 0.8.4 → 0.9.0 ×2
                                      verify dependency pin       0.8.4 → 0.9.0
                                      stale resolved hash dropped removed ×2
```

Then restart the AI client (most read MCP config only at startup) and reopen
Unity so Package Manager re-resolves the two packages. `--dry-run`, `--up`,
`--depth`, `--keep-lock`, and `--json` are documented in
[`scripts/README.md`](../scripts/README.md#switch-a-consuming-project-onto-a-release);
`-h` prints the same list.

Rewrites are idempotent, so running it twice changes nothing the second time. The
script only edits config files under the path you pass: a client config in your
home directory (`~/.cursor/mcp.json`) is a machine-wide surface and stays yours
to update. Use the bridge window for the current open project's safely scoped
home configs and prose; keep this script for maintainer and multi-project work.

## Suppress an intentional warning

```bash
export UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1
```

Set this in the environment that launches the MCP server. It suppresses only
the warning; it does not make incompatible versions compatible.

## Unity Editor compatibility

The bridge and verify package manifests require Unity 2022.3 LTS or newer,
including Unity 6. APIs that only exist on later Unity 6 minors are
version-gated in source (`MainToolbarElement` → `UNITY_6000_3_OR_NEWER`;
`EntityId.ToULong` / hard InstanceID removal → `UNITY_6000_5_OR_NEWER`;
`FindObjectsByType` no-sort overload → `UNITY_6000_5_OR_NEWER`, with the
`FindObjectsSortMode.None` two-arg form on 2023.1–6000.4) so 6000.0–6000.4
still compile. The Node-based MCP server itself does not depend on a Unity
Editor version.

## Unity Hub Pro

Unity Hub Pro is a separate desktop app with an independent version and release
cadence. It does not need to match the server/bridge/verify set. Installers are
published through `hub-v*` GitHub Releases. Official Hub builds check that
release line in the background (at most once per hour) and show a user-initiated
installer action when a newer version is available. Installing the Hub does not
mutate MCP configuration or project package pins.
