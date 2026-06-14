# Unity Open MCP Demo Project

Minimal Unity project for testing the Unity Open MCP Bridge and MCP tools locally.

## Requirements

- **Unity 6** (6000.0.23f1 or later)
- **Node.js** 18+ (for MCP server)

## Quick Start

### 1. Open in Unity

Open this `demo/` folder as a Unity project. Unity will resolve local `file:` package references automatically:

- `com.alexeyperov.unity-open-mcp-bridge` → `../../packages/bridge`
- `com.alexeyperov.unity-open-mcp-verify` → `../../packages/verify`

The bridge HTTP listener starts automatically on `127.0.0.1:19120` when the Editor finishes loading.

### 2. Verify bridge is running

```bash
curl http://127.0.0.1:19120/ping
```

Expected response:

```json
{
  "connected": true,
  "projectPath": "/path/to/demo",
  "unityVersion": "6000.0.23f1",
  "bridgeVersion": "0.1.0",
  "mode": "live",
  "compiling": false,
  "isPlaying": false
}
```

### 3. Start MCP server

```bash
cd /path/to/unity-open-mcp/mcp-server
npm run build
node dist/index.js
```

Set environment variables:

| Variable | Value |
|---|---|
| `UNITY_PROJECT_PATH` | Absolute path to this `demo/` directory |
| `UNITY_OPEN_MCP_BRIDGE_PORT` | `19120` (default) |

### 4. Connect an AI client

Configure your MCP client (Cursor, Claude Desktop, or OpenCode) to use the MCP server. See the MCP server README (`mcp-server/README.md`) for configuration examples.

## Sample Assets

| Asset | Purpose |
|---|---|
| `Assets/Prefabs/GateTestCube.prefab` | Simple cube prefab for gate validation tests |
| `Assets/Scenes/Main.unity` | Minimal scene with a GateTestCube instance |

These assets are designed for controlled broken/fixed reference checks:

- **Break a reference**: Remove or rename the prefab file while the scene references it → `missing_references` rule detects the broken reference.
- **Fix a reference**: Restore the prefab file → gate delta shows `resolvedErrors: 1`.

## Gate Test Fixtures

Located in `Assets/Fixtures/`. These prefabs provide controlled break/fix scenarios for EditMode tests and manual E2E checklists.

| Fixture | Path | Scenario |
|---|---|---|
| **HealthyFixture** | `Assets/Fixtures/HealthyFixture.prefab` | Minimal prefab with no issues. Used as the "clean" baseline for gate pass checks and delta comparisons. |
| **MissingScriptFixture** | `Assets/Fixtures/MissingScriptFixture.prefab` | Prefab with a MonoBehaviour whose `m_Script` GUID (`deadbeef…`) points to a nonexistent script. Triggers `missing_references` / `missing_script` issue. Fixable via `apply_fix` (remove missing script). |
| **BrokenRefFixture** | `Assets/Fixtures/BrokenRefFixture.prefab` | Prefab with a MeshFilter whose `m_Mesh` GUID (`aaaaaaaa…`) points to a nonexistent asset. Triggers `missing_references` / `missing_guid` issue. |
| **RestorableRefFixture** | `Assets/Fixtures/RestorableRefFixture.prefab` | Healthy prefab referencing Unity built-in Cube mesh and default material. Edit the `m_Mesh` GUID to a fake value to break → gate fails; restore the original GUID (`0000000000000000e000000000000000`) → gate passes. Used for checkpoint → mutate → delta workflows. |

### Usage in tests

- **EditMode tests** in `packages/verify/Tests~/Editor/` reference these fixture paths for reproducible rule testing.
- **Manual E2E checklist** uses these paths in curl commands.
- **Bridge `file:` wiring**: demo `manifest.json` includes both `bridge` and `verify` packages so fixtures are scanned by M3 rules.

## Package References

The demo uses local `file:` references in `Packages/manifest.json`:

```json
{
  "com.alexeyperov.unity-open-mcp-bridge": "file:../../packages/bridge",
  "com.alexeyperov.unity-open-mcp-verify": "file:../../packages/verify"
}
```

Changes to bridge or verify source are reflected after Unity recompiles (domain reload).

## Port Override

Set `UNITY_OPEN_MCP_BRIDGE_PORT` environment variable or pass `-UNITY_OPEN_MCP_BRIDGE_PORT=<port>` as a Unity launch argument to override the default port `19120`.
