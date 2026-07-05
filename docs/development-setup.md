# Development Setup

Work on the Unity Open MCP packages themselves — local checkouts, building the
MCP server from source, contributor/community-pack workflows, and the
maintainer publish flow for the `unity-open-mcp` npm package.

For installing as a user (npm + Git URL), see
[manual-setup.md](manual-setup.md). For guided setup, see
[wizard-setup.md](wizard-setup.md).

## Requirements

- Unity 2022.3 LTS or newer (Unity 6 recommended)
- Node.js 18 or newer
- MCP client that supports stdio MCP servers

## 1) Local `file:` install (monorepo checkout)

Clone `unity-open-mcp` and point your Unity project's `Packages/manifest.json`
at the local package folders via `file:` URLs. The Hub wizard's **Use local
checkout** (Step 2) + **Use local packages** (Step 3) toggles automate this;
the manual equivalent is:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../unity-open-mcp/packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../unity-open-mcp/packages/verify"
  }
}
```

For monorepo projects whose Unity project lives alongside the package source,
the same-shorthand form works:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
  }
}
```

Edit source under `packages/bridge/Editor/` and Unity recompiles on focus.

## 2) Build the MCP server from source

Build the TypeScript MCP server once so the wizard's Step 2 fingerprint check
passes and `npx`/local CLI runs use a freshly built `dist/`:

```bash
cd mcp-server
npm install
npm run build
```

Point your MCP client at the local checkout instead of the published npm
package by replacing the `npx` command with a direct node launch:

```json
{
  "mcpServers": {
    "unity-open-mcp": {
      "command": "node",
      "args": ["/absolute/path/to/unity-open-mcp/mcp-server/dist/index.js"],
      "env": {
        "UNITY_PROJECT_PATH": "/absolute/path/to/project"
      }
    }
  }
}
```

Optional startup-dialog env vars (`UNITY_OPEN_MCP_DIALOG_POLICY`, project-upgrade
and unsaved-scene opt-ins, dismiss timeouts) apply the same way — see
[Dialog policy](dialog-policy.md). On **macOS**, auto-dismiss also needs a
one-time **Accessibility** grant for the app that runs `node` (Terminal, IDE,
CI runner, etc.) — see
[Dialog policy → macOS Accessibility](dialog-policy.md#macos-accessibility-required-for-auto-dismiss).

## 3) Optional Unity domain dependencies

Domain tools (NavMesh, Input System, ProBuilder, Particle System, Animation)
are **bundled with the bridge** — they activate automatically once the matching
Unity package is present. Add the Unity dependencies you want under
`dependencies`:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
    "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify",
    "com.unity.ai.navigation": "2.0.0",
    "com.unity.inputsystem": "1.7.0",
    "com.unity.probuilder": "6.0.9"
  }
}
```

Particle System and Animation are built-in Unity modules — no manifest entry is
needed, the tools compile in as soon as the module is enabled in the Editor.
See [extensions.md](extensions.md) for the domain catalog and activation steps.

## 4) Launch Unity and verify

1. Open the same Unity project in the Editor.
2. Wait for scripts to compile.
3. Restart your MCP client.
4. Validate bridge status:

```bash
curl -s "http://127.0.0.1:<port>/ping"
```

## Contributor / community-pack workflow

Two advanced workflows exist for contributors and third-party pack authors.

### Community domain packs (third-party)

`packages/extensions/` is the home for **third-party / community** domain packs
that are not shipped with the bridge. The shipped domains (Nav, Input,
ProBuilder, Particles, Animation) are embedded in the bridge and **must not**
also be installed as separate `com.alexeyperov.unity-open-mcp-ext-*` packs —
that would double-register tool IDs. See
[contributing/extensions.md](contributing/extensions.md) for the community-pack
contract.

To install a community pack, add its UPM id under `dependencies`:

```json
{
  "dependencies": {
    "com.example.my-mcp-ext": "file:../../my-mcp-ext"
  }
}
```

## Maintainer publish flow

The `unity-open-mcp` npm package is published from `mcp-server/`. There are two
publish channels — use CI for releases, the Hub maintainer panel for local
workflows. Either way, only the `files` whitelist (`dist/`, `README.md`,
`LICENSE`) ships; `src/`, `tsconfig*.json`, and tests never reach the registry.

| Channel | When | Who authenticates |
|---|---|---|
| **CI** (`.github/workflows/npm-publish.yml` on `v*` tags) | Production releases | the `NPM_TOKEN` repository secret (automation token on the publishing account) |
| **Hub maintainer panel** | First manual publish, emergency republish, local dry-runs | the maintainer's own `npm login` on the machine |

### CI publish (tag-triggered)

The standard release path. The npm MCP server, the bridge Unity package, and the
verify Unity package share **one version** — bumping it is a single command from
the repo root, not a per-package `npm version`:

```bash
node scripts/sync-version.mjs bump patch    # or minor / major
git add -A
git commit -m "Bump to vX.Y.Z"
git tag vX.Y.Z
git push origin vX.Y.Z
```

`bump` updates the single source (`version.json`) **and** rewrites every
generated target (the server + bridge + verify `package.json`, plus the two C#
version constants the bridge reports over `/ping`). See
[docs/versioning.md](versioning.md) for the full policy, the drift gate, and how
the Hub app (which has its own independent version) is bumped.

The `npm-publish.yml` workflow then runs `npm ci && npm publish --access public`
on the tag (the `prepublishOnly` script rebuilds `dist/` first, so the tarball
is always fresh). It refuses to publish when the tag version does not match
`mcp-server/package.json` **or** when the trio has drifted out of sync — a
maintainer who hand-edited one file gets a clear failure instead of a
mismatched tarball.

First publish only: the name must be claimed with one manual `npm publish`
before CI can publish to it (npm requires the first publish of a name to come
from a logged-in account). Use the Hub panel or a local `npm publish` for that
one-time claim, then switch to the CI flow.

### Hub maintainer panel

When a folder is tracked as an Open-MCP repository (the Hub detects
`mcp-server/` plus a root `package.json` marker), the project's settings popup
becomes a maintainer panel:

- **Info header** — package name + local version from
  `mcp-server/package.json`, the published version from `npm view`, and the
  `npm whoami` result.
- **Build / Test** — `npm run build` / `npm test` with cwd pinned to
  `mcp-server/`.
- **Version bump** — `npm version patch|minor|major --no-git-tag-version` (the
  Hub never creates git tags).
- **Publish dry-run** — `npm publish --dry-run --access public` preflight.
- **Publish** — real publish behind a confirmation dialog.

The Hub does **not** store npm credentials — authenticate with `npm login` on
the machine (npm reads its own credentials/`.npmrc`). Use this channel for the
first-time name claim, an emergency republish, or a local dry-run before
tagging.

### Publishing a fork

To publish under your own name (a fork): change `name` in
`mcp-server/package.json`, `npm login` with your account, and run the Hub
publish flow or `npm publish --access public` from `mcp-server/`. The CI
workflow is keyed off your fork's tags + `NPM_TOKEN` secret, so a fork with its
own token publishes on its own `v*` tags with no further change. Users of the
fork then point their clients at the forked package name
(`npx -y <your-name>@latest`).

## Related docs

- [Dialog policy](dialog-policy.md)
- [Manual setup](manual-setup.md)
- [Wizard setup](wizard-setup.md)
- [Extensions](extensions.md)
- [Contributing — extensions](contributing/extensions.md)
- [Contributor troubleshooting](troubleshooting-contributors.md) — test suites, worker-listener collisions, InitTestScene modals
- [Bridge HTTP API](api/bridge-http.md)
- [MCP tools API](api/mcp-tools.md)
