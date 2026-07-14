# Versioning

How versions are managed across the npm MCP server, the Unity bridge/verify packages, and the Unity Hub Pro app — and what happens when the running bridge and the MCP server don't match.

This document has two parts: **[For users](#for-users)** (you installed unity-open-mcp into a project) and **[For contributors](#for-contributors)** (you're changing or releasing this repo).

---

## Table of contents

**For users**
- [How versions are organized](#how-versions-are-organized)
- [Finding your version](#finding-your-version)
- [The compatibility warning](#the-compatibility-warning)
- [Resolving a mismatch](#resolving-a-mismatch)
- [The escape hatch](#the-escape-hatch)
- [Unity Editor version compatibility](#unity-editor-version-compatibility)
- [The Hub app](#the-hub-app)

**For contributors**
- [Where the version lives](#where-the-version-lives)
- [Bumping the shared version](#bumping-the-shared-version)
- [Bumping the Hub app](#bumping-the-hub-app)
- [The CI drift gate](#the-ci-drift-gate)
- [Release channels and tag namespaces](#release-channels-and-tag-namespaces)
- [Pre-1.0 semver convention](#pre-10-semver-convention)
- [Adding a new version surface](#adding-a-new-version-surface)
- [Runtime handshake internals](#runtime-handshake-internals)
- [Why no monorepo tooling](#why-no-monorepo-tooling)

---

# For users

## How versions are organized

unity-open-mcp ships as **three tightly-coupled artifacts that share one version number**:

| Artifact | What it is | Installed via |
|---|---|---|
| **MCP server** (`unity-open-mcp`) | The npm package your AI client (Claude, Cursor, …) launches | npm |
| **Bridge** | The Unity Editor package that exposes Unity to the server | Unity Package Manager (git URL) |
| **Verify** | The Unity Editor package for validation rules | Unity Package Manager (git URL) |

These three ship breaking changes **together** and always carry the same version
(if the server is `X.Y.Z`, the bridge and verify packages are also `X.Y.Z`).
The number is identical across all three by design.

The **Unity Hub Pro** desktop app is a separate, **optional** companion app on its **own independent version**. It is not coupled to the three above and doesn't need to match them.

## Finding your version

The easiest way is the CLI `status` command (ships with the npm package):

```bash
npx unity-open-mcp status
```

Output includes:

```
Bridge ver: X.Y.Z
Compat:   ok (server X.Y.Z / bridge X.Y.Z)
```

- **`Bridge ver`** — the version the running Unity Editor's bridge reports (from its `/ping`).
- **`Compat`** — `ok` when the server and bridge match, `ok (drift)` when they differ only by patch (compatible), `WARN` when they're considered incompatible. See [The compatibility warning](#the-compatibility-warning).

You can also see the server's own version with:

```bash
npx unity-open-mcp --version
# unity-open-mcp X.Y.Z
```

And the bridge version of an installed Unity package from Unity: **Window → Package Manager**, find *Unity Open MCP Bridge*, read the version column.

## The compatibility warning

When the running bridge and the MCP server are on **incompatible** versions, the server prints a one-time warning to stderr at the first successful connection — for example:

```
unity-open-mcp: INCOMPATIBLE versions — server 0.5.0, bridge 0.4.0.
  The bridge is older. To fix: In Unity: Window → Package Manager → update the
  bridge package (tag bridge-v0.5.0).
  (Set UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1 to suppress this warning.)
```

This is **advisory** — the connection proceeds and tools keep working. We warn rather than hard-fail so a mixed pair never silently misbehaves, but also never blocks a user mid-task. The warning fires **once per server process**, not on every request.

What counts as "incompatible" follows our [pre-1.0 convention](#pre-10-semver-convention): while the version starts with `0.`, a difference in the **middle** (minor) number is incompatible, while a difference in only the **last** (patch) number is compatible and produces a softer `ok (drift)` line instead of `WARN`. The values below are illustrative examples, not current release numbers.

| Server | Bridge | Compat | Why |
|---|---|---|---|
| `X.Y.Z` | `X.Y.Z` | `ok` | identical |
| `0.5.0` | `0.5.1` | `ok (drift)` | patch-only difference — compatible |
| `0.5.0` | `0.4.0` | `WARN` | minor differs — incompatible (pre-1.0) |
| `0.5.0` | `0.6.0` | `WARN` | minor differs — incompatible (pre-1.0) |

## Resolving a mismatch

The warning message tells you which side is older and the exact command. In general:

**Bridge older than server** — update the Unity package:
1. Open your Unity project.
2. **Window → Package Manager**.
3. Find *Unity Open MCP Bridge*, click it, then **Update** to the version the server reports (or reinstall from the git URL pinned to `bridge-v<version>`).
4. Do the same for *Unity Open MCP Verify* if present.

**Server older than bridge** — update the npm package:

```bash
npm install -g unity-open-mcp@<bridge-version>
# or, if you run it via npx:
npx unity-open-mcp@<bridge-version> ...
```

Use the version the bridge reports (`Bridge ver:` from `status`).

## The escape hatch

If you intentionally want to run a mixed pair (e.g. during development, or you've verified a specific combination works), silence the warning with an environment variable:

```bash
export UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1
```

Set it in the environment your AI client uses to launch the server. This **only suppresses the warning** — it does not make an incompatible pair actually compatible. If tools then misbehave, the version difference is still the likely cause.

## Unity Editor version compatibility

The bridge and verify Unity packages declare a minimum Unity version in their manifests (`"unity": "2022.3"`). They work on Unity 2022.3 LTS and newer, including Unity 6. The MCP server itself doesn't depend on a Unity version — it talks to whatever bridge is running.

## The hub app

Unity Hub Pro is a standalone desktop app (macOS / Windows) with its **own version number and release cadence**, independent of the server/bridge/verify trio. You do not need the Hub app to use unity-open-mcp, and the Hub app's version is unrelated to the versions above. The Hub app is distributed as signed installers from its GitHub releases (`hub-v*` tags).

---

# For contributors

## Where the version lives

There are **two independent single-source-of-truth files**. Every other version string in the repo is **generated** from one of them by `scripts/sync-version.mjs`:

| Source of truth | Governs | Independent? |
|---|---|---|
| `version.json` (repo root) | The shared **trio**: npm server + bridge Unity pkg + verify Unity pkg | — |
| `hub/version.json` | The **Unity Hub Pro** desktop app | independent cadence |

**Never hand-edit a generated target.** Bump the source file and run the sync script. The [CI drift gate](#the-ci-drift-gate) fails any PR where a generated target disagrees with its source.

The generated targets, mapped to their sources:

**From `version.json` →**

| Generated file | Field | Notes |
|---|---|---|
| `mcp-server/package.json` | `version` | read at runtime by the server |
| `packages/bridge/package.json` | `version` | what Unity Package Manager shows |
| `packages/bridge/package.json` | `dependencies[verify]` | bridge depends on verify at the same version so a git-URL install of both resolves |
| `packages/verify/package.json` | `version` | what Unity Package Manager shows |
| `packages/bridge/Editor/Bridge/BridgeSession.cs` | `BridgeVersion` constant | reported by `/ping` |
| `packages/bridge/Editor/Bridge/BridgeHttpServer.cs` | `/ping` 503 fallback literal | pre-init body, before `BridgeSession` is ready |
| `docs/setup/manual-setup.md`, `docs/setup/agent-setup.md` | `#bridge-v` / `#verify-v` git-URL pins | UPM install snippets reference the version tags; kept current by the sync script |
| `docs/setup/manual-setup.md`, `docs/setup/agent-setup.md`, `docs/setup/wizard-setup.md`, `mcp-server/README.md`, `docs/api/mcp-tools.md`, `docs/ci/**/*.yml` | `unity-open-mcp@<ver>` npm pins | the server shares the trio version, so every `npx … @<ver>` snippet is generated from `version.json` |

**From `hub/version.json` →**

| Generated file | Field |
|---|---|
| `hub/src-tauri/tauri.conf.json` | `version` |
| `hub/src-tauri/Cargo.toml` | `version` |
| `hub/package.json` | `version` |

The former standalone extension packages for shipped domains (`packages/extensions/{navigation,inputsystem,probuilder,particlesystem,animation}`) were **removed** — they were duplicates of the domain tools embedded in the bridge. Any remaining community packs under `packages/extensions/*` are independent third-party packages and are not part of the shared version trio. The root `package.json` is a private marker (`0.0.0`) and is also not touched.

## Bumping the shared version

This is the normal release flow for the npm server + bridge + verify together. From the repo root:

```bash
# 1. Bump. Updates version.json AND rewrites all five trio targets.
node scripts/sync-version.mjs bump patch    # or minor / major

# 2. Review what changed (the script prints each file it touched).
git diff

# 3. Commit.
git add -A
git commit -m "chore: bump to 0.X.Y"

# 4. Create the release tags. `tags` cross-checks the version against version.json,
#    then creates v*, bridge-v*, verify-v* on HEAD (annotated, matching existing tags).
node scripts/sync-version.mjs tags 0.X.Y

# 5. Push to publish.
git push origin v0.X.Y bridge-v0.X.Y verify-v0.X.Y   # v* triggers the npm-publish workflow
```

Pushing the `v*` tag triggers `.github/workflows/npm-publish.yml`, which:
1. Verifies the tag matches `mcp-server/package.json`.
2. Verifies the whole trio is in sync (`sync-version.mjs --check`).
3. Builds and publishes to npm.

The Unity packages have **no registry publish step** — they're consumed via git URL. Users pick up the new version by updating their Package Manager git URL to the new `bridge-v*` / `verify-v*` tag (or just hitting **Update** if they installed from a moving ref).

## Bumping the Hub app

The Hub is on its own version, so it has its own flag:

```bash
node scripts/sync-version.mjs bump patch --hub

git add -A
git commit -m "chore: hub bump to 0.X.Y"

node scripts/sync-version.mjs tags 0.X.Y --hub   # creates hub-v0.X.Y on HEAD

git push origin hub-v0.X.Y     # triggers the hub-release workflow
```

The `hub-v*` tag triggers `.github/workflows/hub-release.yml`, which first verifies the tag matches `hub/version.json` and that all Hub targets are in sync, then builds the macOS/Windows installers and creates a GitHub Release.

### From the Hub app (UI shortcut)

Both bump flows are also drivable from the Unity Hub Pro app itself: open an Open-MCP checkout's project settings and use the **Repo version sync** panel. It runs the same `scripts/sync-version.mjs` with the same grammar — `sync`, `check` (the drift gate), `bump <level>`, or `set <X.Y.Z>`, for either the trio or the Hub line — and streams the script's output live. It writes the same files; it does **not** commit or tag (the Hub never creates git tags — use the CLI `tags` command for that). The CLI examples above remain canonical.

## Setting an exact version

`bump` only increments. To jump to a specific version (e.g. to align the trio and Hub after they diverged, or to land a deliberate number), use `set`:

```bash
node scripts/sync-version.mjs set 0.2.0          # trio: version.json + all five trio targets
node scripts/sync-version.mjs set 0.2.0 --hub    # hub:  hub/version.json + all three hub targets
```

`set` behaves exactly like `bump` otherwise — it writes the source file and rewrites every generated target — then prints the same commit/tag hint. The target version must be plain `major.minor.patch` (a leading `v` is tolerated and stripped); pre-release/build metadata are not supported. From there, commit and create the release tags exactly as you would after a `bump`:

```bash
git add -A
git commit -m "chore: bump to 0.2.0"
node scripts/sync-version.mjs tags 0.2.0          # trio: v0.2.0 bridge-v0.2.0 verify-v0.2.0
#  or: node scripts/sync-version.mjs tags 0.2.0 --hub   # hub: hub-v0.2.0
git push origin v0.2.0 bridge-v0.2.0 verify-v0.2.0
```

## The CI drift gate

`.github/workflows/version-sync.yml` runs on every PR (and push to `main`/`master`) that touches any version-related file. It runs `sync-version.mjs --check` twice — once for the trio, once for the Hub — and **fails the PR** if any generated target has drifted from its source.

The same workflow also runs an **advisory** token-estimate drift check (`generate-token-estimates.mjs --check`, `continue-on-error`) when tool schemas change. It reports drift in the CI log but does not block the PR. Regenerate after schema changes — `sync-version.mjs bump` prints a reminder.

The fix for a failed gate is always: run the bump or sync script, never hand-edit a single file. Concretely:

```bash
node scripts/sync-version.mjs           # rewrite all trio targets from version.json
node scripts/sync-version.mjs --hub     # rewrite all Hub targets from hub/version.json
```

You can run `--check` locally before pushing to catch drift early:

```bash
node scripts/sync-version.mjs --check
node scripts/sync-version.mjs --check --hub
```

## Release channels and tag namespaces

| Tag pattern | What it releases | Workflow | Publishes to |
|---|---|---|---|
| `v*` (e.g. `vX.Y.Z`) | The shared trio (npm server is what gets pushed; bridge/verify move with it via git URL) | `npm-publish.yml` | npm registry **and** a GitHub Release (auto-generated notes) |
| `hub-v*` (e.g. `hub-v0.3.0`) | The Unity Hub Pro desktop app | `hub-release.yml` | GitHub Release (installers) |
| `bridge-v*` / `verify-v*` | (Convention) git-URL install pins for the Unity packages | — (no workflow) | n/a — users pin in their manifest |

The `bridge-v*`/`verify-v*` tags have no workflow because the Unity packages aren't published to a registry; they exist purely so users can pin a known-good version in their `Packages/manifest.json` git URL. A trio release needs **three tags on the same commit**: `vX.Y.Z` (publishes the npm server, which the `unity-open-mcp@<ver>` pins in the setup docs resolve to), plus `bridge-vX.Y.Z` and `verify-vX.Y.Z` (which the UPM git-URL pins resolve to). The `tags` subcommand creates all three in one call; the `bump`/`set` output prints the exact invocation. The setup docs reference these tags, and the sync script keeps them current with `version.json`, so a missing tag means the documented install fails to resolve.

## Creating release tags

The `tags` subcommand turns the "three tags on one commit" trio convention (and the single `hub-v*` hub tag) into one command:

```bash
node scripts/sync-version.mjs tags 0.X.Y          # trio: creates v0.X.Y, bridge-v0.X.Y, verify-v0.X.Y on HEAD
node scripts/sync-version.mjs tags 0.X.Y --hub    # hub:  creates hub-v0.X.Y on HEAD
```

It does three things:

1. **Cross-checks the version** against the source file (`version.json` for the trio, `hub/version.json` for the Hub) and refuses if they differ — so a tag can never point at a commit whose committed version disagrees with the tag name (which would fail the publish workflow's preflight anyway).
2. **Refuses if any tag already exists** — a partial trio tag set is usually a mistake, so it errors out naming the existing tag(s) rather than silently creating the rest.
3. **Creates annotated tags with empty message bodies** on `HEAD`, matching every existing tag in the repo (`git cat-file -t` → `tag`).

It does **not** push. Pushing `v*` triggers `npm-publish.yml` and pushing `hub-v*` triggers `hub-release.yml` — both irreversible (npm publish, GitHub Release) — so `tags` prints the exact `git push origin …` command for you to run after reviewing. The `bump` and `set` commands print the `tags` invocation as their final "Next" step.

## Pre-1.0 semver convention

While the major version is **0**, the **minor** digit is the breaking axis. This is the standard pre-1.0 reading of semver and it's what the runtime handshake enforces:

- `0.4.x` ↔ `0.5.x` → **incompatible** (minor differs) → `WARN`
- `0.5.0` ↔ `0.5.1` → **compatible** (patch differs) → `ok (drift)`
- `X.Y.Z` ↔ `X.Y.Z` → **identical** → `ok`

Once the project reaches **1.0**, the standard rule takes over: the **major** digit becomes the breaking axis (`1.x` ↔ `2.x` incompatible; `1.2` ↔ `1.9` compatible). The handshake code handles both regimes — see [`Runtime handshake internals`](#runtime-handshake-internals).

Practical implication for contributors: **treat a minor bump as a breaking change** while on `0.x`. Anything that would break an existing project's setup (a changed tool signature, a removed tool, a changed wire field) is a minor bump, not a patch.

## Adding a new version surface

If you introduce a new place that must carry the shared version (e.g. a new C# constant, a new generated manifest):

1. Open `scripts/sync-version.mjs`.
2. Add an entry to `TRIO_TARGETS` (or `HUB_TARGETS` for the Hub). Each entry is `{ file, kind, description, replace }` where `replace(body, version)` returns the file content with the version swapped in. Use a regex narrow enough to match only the version slot.
3. The `--check` and `bump` paths pick it up automatically — no other change needed.
4. Add the new file path to the `paths:` list in `.github/workflows/version-sync.yml` so PRs touching it run the gate.

Always prefer reading the version from the runtime source where possible (e.g. the MCP server reads its own `package.json` at runtime via `package-version.ts`; the bridge reports its constant over `/ping`). Only add a *generated* target when there's no way to read the source at runtime in that context.

## Runtime handshake internals

The compatibility check lives in `mcp-server/src/compat.ts`:

- `SHARED_VERSION` — the server's own version, read once at module load from `package.json` (via `readPackageVersion()`).
- `checkBridgeCompat(bridgeVersion)` — compares the bridge's reported version against the server's. Returns `{ ok, serverVersion, bridgeVersion, message }`. The rule:
  - If either side is unparseable → `ok: true` with an advisory message (forward-compatible with very old bridges that predate version reporting).
  - If equal → `ok: true`, empty message.
  - If the breaking axis differs (minor pre-1.0, major post-1.0) → `ok: false` with a message naming the older side and the exact upgrade command.
  - If only a non-breaking digit differs (patch) → `ok: true` with a softer advisory message.
- `isVersionCheckSuppressed()` — `true` when `UNITY_OPEN_MCP_SKIP_VERSION_CHECK=1`.

It is wired into two places in `mcp-server/src/live-client.ts`:

- `isLiveAvailable()` and `handlePing()` — both already parse the `/ping` body. After recording the ping, each calls `maybeWarnCompat(body.bridgeVersion)`, which is a **one-shot** (`compatWarned` flag) so the warning fires at most once per process. Suppressed entirely by the env var. The check is advisory: the connection proceeds regardless of the result.

The CLI `status` command (`mcp-server/src/cli/commands.ts`) computes `checkBridgeCompat` from the ping body and adds both a `compat` object to the JSON output and a `Compat:` line to the human output, so operators can see drift without parsing stderr.


## Related docs

- [Architecture](architecture.md) — repository boundaries and runtime flow.
- [MCP tools API](api/mcp-tools.md)
- [Unity Hub Pro](unity-hub-pro.md)
