# Updating Unity Open MCP

Unity Open MCP has two release lines. Unity Hub Pro updates independently. The
MCP server and both Unity packages form a shared trio and should use the same
version.

| Release line | Source of truth | Artifacts |
|---|---|---|
| Shared trio | `version.json` | npm `unity-open-mcp`, Bridge UPM package, Verify UPM package |
| Hub | `hub/version.json` | Unity Hub Pro desktop app |

Maintainers propagate each source with `node scripts/sync-version.mjs` (shared
trio) or `node scripts/sync-version.mjs --hub` (Hub). Users do not need a
repository checkout for normal updates.

## Update everything

Use this order so each step can report any remaining version drift:

1. **Unity Hub Pro:** when the Hub shows an update notice, choose **Update** and
   complete the installer handoff. The Hub version does not have to match the
   trio version.
2. **MCP server:** check first if this is CI or you only want availability:

   ```bash
   unity-open-mcp update --check
   ```

   Apply interactively with:

   ```bash
   unity-open-mcp update
   ```

   A global or project-local npm installation is updated in place. An `npx`
   process cannot safely replace its own cache, so the command prints the exact
   version and tells you to update the `unity-open-mcp@X.Y.Z` pin in the MCP
   client configuration (or run with `@latest`). Restart the MCP client after
   changing its server install or pin.
3. **Unity project:** in **Tools → Unity Open MCP Bridge**, use the **Updates**
   section when it is present in the installed bridge. Review the preview, then
   move the Bridge and Verify packages, matching project and home-scoped client
   pins, and agent-facing version prose together.
   For an older bridge without that section, use the Hub AI Setup wizard, the
   bridge maintainer/install panel, or update both package entries manually in
   **Window → Package Manager**. With Unity closed, you may edit
   `Packages/manifest.json` directly. For several projects from a repository
   checkout, use `scripts/switch-project-version.mjs` as documented in
   [Version compatibility](versioning.md#switch-a-whole-project-to-a-release).

`unity-open-mcp update` deliberately changes only the npm installation. It
never rewrites a Unity project's manifest, an MCP client configuration, or
agent-facing project instructions.

After all three steps, reopen Unity, restart the MCP client, and check health:

```bash
unity-open-mcp ping --project /absolute/path/to/MyGame
unity-open-mcp status --project /absolute/path/to/MyGame
```

`status` shows the running server and bridge versions and reports incompatible
drift. Unity Package Manager shows the installed Bridge and Verify versions.

## Check exit codes

`update --check` is intended for scripts and CI:

| Code | Meaning |
|---:|---|
| `0` | The running MCP server is current. |
| `10` | A newer MCP server version is available. |
| `11` | Neither npm nor the GitHub Releases fallback could provide a version, or a returned version was invalid. No files changed. |

Applying an update also uses `12` when npm found a release but could not install
it. Network and registry failures are returned as normal command results rather
than uncaught CLI crashes. With `--json`, the result includes `status`,
`currentVersion`, `latestVersion` when known, and the release `source`.

## Air-gapped update

Once the artifacts have been copied onto the isolated machine, these steps need
no network access.

### 1. Prepare artifacts on a connected transfer machine

Choose one shared trio version and, independently, the Hub version you need.

1. Download the matching Unity Hub Pro installer from the `hub-vX.Y.Z` GitHub
   Release for the target operating system and architecture.
2. Download the MCP server tarball:

   ```bash
   npm pack unity-open-mcp@X.Y.Z
   ```

3. Download the repository source archive for `vX.Y.Z`, or clone that tag. Keep
   both `packages/bridge/` and `packages/verify/` from the same archive.
4. Copy the installer, npm `.tgz`, and the two package directories to approved
   removable media or your internal artifact mirror. Record the selected
   versions alongside them.

An internal npm mirror may replace the tarball, and an internal Git/UPM registry
may replace the vendored package folders. Mirror all three trio artifacts at the
same version.

### 2. Install on the isolated machine

1. Run the transferred Hub installer using the normal operating-system install
   flow.
2. Install the MCP tarball globally, without a registry request:

   ```bash
   npm install --global /media/transfer/unity-open-mcp-X.Y.Z.tgz
   ```

   A project-local install is also supported: run
   `npm install /media/transfer/unity-open-mcp-X.Y.Z.tgz` in the Node project
   that owns the MCP dependency. Configure the client to launch that installed
   binary; do not use an unpopulated `npx` cache offline.
3. Copy `bridge/` and `verify/` to a stable vendored location that will not be
   removed with the transfer media. In the Unity project's
   `Packages/manifest.json`, point both dependencies at those folders:

   ```json
   {
     "dependencies": {
       "com.alexeyperov.unity-open-mcp-bridge": "file:/opt/unity-open-mcp/X.Y.Z/bridge",
       "com.alexeyperov.unity-open-mcp-verify": "file:/opt/unity-open-mcp/X.Y.Z/verify"
     }
   }
   ```

   Relative `file:` paths or equivalent entries in a vendored UPM registry are
   fine. Keep both packages on the same trio version. Open Unity after the
   folders and manifest are in place so Package Manager resolves only local
   sources.
4. Restart the MCP client and use `unity-open-mcp --version`, `ping`, and
   `status` to verify the transferred install. Do not run `update --check` on a
   deliberately air-gapped machine; it is a network availability check.

For an offline first install, the same artifacts and local package entries work;
the detailed MCP client shapes remain in
[MCP client configuration](setup/client-configuration.md).
