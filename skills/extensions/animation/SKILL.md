# Unity Open MCP — Animation Extension

Skill for AI agents driving Unity AnimationClip and AnimatorController assets in a project through the `unity-open-mcp` MCP server + the **Animation extension pack** (`com.alexeyperov.unity-open-mcp-ext-animation`).

> This pack is **opt-in**. Its tools only resolve when the project's `Packages/manifest.json` includes the animation extension package. AnimationClip + AnimatorController are built-in Unity modules — no extra Unity package is needed. If a tool returns `tool_not_found`, the pack is not installed — surface the manifest line from the bridge window's Extensions tab or the Hub AI Setup wizard.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The animation extension pack is installed (see the bridge window's **Extensions** tab; `animation_get_data` returns `tool_not_found` otherwise).

## Tool prefixes

- AnimationClip tools use `unity_open_mcp_animation_*`.
- AnimatorController tools use `unity_open_mcp_animator_*`.

All four mutators run the full gate path with `paths_hint` scoped to the asset path (`.anim` or `.controller`); the two read tools are gate-free. The two modify tools are DESTRUCTIVE — some modification types (ClearCurves / ClearEvents / RemoveCurve on clips, RemoveParameter / RemoveLayer / RemoveState / RemoveTransition on controllers) are irreversible without undo.

## Two separate authoring domains

The pack treats **clip authoring** and **controller (state-machine) authoring** as separate workflows:

| Workflow | Tools |
|---|---|
| AnimationClip authoring | `animation_create` → `animation_modify` (curves, events, frame rate, wrap mode) → `animation_get_data` |
| AnimatorController authoring | `animator_create` → `animator_modify` (parameters, layers, states, transitions) → `animator_get_data` |

The two are linked via `SetStateMotion` on the controller side — that's how a clip becomes the motion of a state.

## Always get before you modify

Both `*_get_data` tools are cheap (gate-free, read-only). Call them first to discover:

- **clips**: valid `(path, propertyName, type)` tuples for `SetCurve` / `RemoveCurve` entries (see `curveBindings` / `objectReferenceCurveBindings` in the response).
- **controllers**: valid layer / state / parameter names for the controller-side modifications.

## Modification batch format

Both modify tools accept `modifications_json` — a JSON **array** of modification entries dispatched by `type`. Per-entry errors are accumulated in the response's `errors` array and do **not** abort the batch; the valid subset still applies and is reported in `applied`.

### AnimationClip modification types

| type | required fields | notes |
|---|---|---|
| `SetCurve` | `componentType`, `propertyName`, `keyframes: [{time, value, inTangent?, outTangent?}]` | `relativePath` is optional (root if omitted). `componentType` may be a bare name (`Transform`) or a full name (`UnityEngine.Transform`). |
| `RemoveCurve` | `componentType`, `propertyName` | `relativePath` optional. |
| `ClearCurves` | — | Removes every curve. |
| `SetFrameRate` | `frameRate` | |
| `SetWrapMode` | `wrapMode` | Default / Once / Loop / PingPong / ClampForever. |
| `SetLegacy` | `legacy` (bool) | Toggles the legacy animation flag. |
| `AddEvent` | `time`, `functionName` | `floatParameter` / `intParameter` / `stringParameter` optional. |
| `ClearEvents` | — | Removes every event. |

### AnimatorController modification types

| type | required fields | notes |
|---|---|---|
| `AddParameter` | `parameterName`, `parameterType` (Float/Int/Bool/Trigger) | `defaultFloat` / `defaultInt` / `defaultBool` optional. |
| `RemoveParameter` | `parameterName` | |
| `AddLayer` | `layerName` | |
| `RemoveLayer` | `layerName` | |
| `AddState` | `layerName`, `stateName` | `motionAssetPath` optional (assigns the clip immediately). |
| `RemoveState` | `layerName`, `stateName` | |
| `SetDefaultState` | `layerName`, `stateName` | |
| `AddTransition` | `layerName`, `sourceStateName`, `destinationStateName` | `hasExitTime` / `exitTime` / `duration` / `hasFixedDuration` / `conditions: [{parameter, mode, threshold?}]` optional. |
| `RemoveTransition` | `layerName`, `sourceStateName`, `destinationStateName` | |
| `AddAnyStateTransition` | `layerName`, `destinationStateName` | Same optional fields as `AddTransition`. |
| `SetStateMotion` | `layerName`, `stateName`, `motionAssetPath` | `motionAssetPath` is an `Assets/`-rooted `.anim` path. |
| `SetStateSpeed` | `layerName`, `stateName`, `speed` | |

Condition `mode` values: `If` / `IfNot` (Trigger / Bool), `Greater` / `Less` (Float / Int), `Equals` / `NotEqual` (Int).

## Canonical workflows

### Author an AnimationClip curve

1. **Create** — `unity_open_mcp_animation_create` with `asset_paths: ["Assets/Anims/Idle.anim"]`. Capture the path.
2. **Inspect** — `unity_open_mcp_animation_get_data` (empty clip — `empty: true`).
3. **Set a curve** — `unity_open_mcp_animation_modify` with `modifications_json: "[{\"type\":\"SetCurve\",\"componentType\":\"UnityEngine.Transform\",\"propertyName\":\"m_LocalPosition.x\",\"keyframes\":[{\"time\":0,\"value\":0},{\"time\":1,\"value\":2}]}]"`.
4. **Re-read** — `animation_get_data` shows `curveBindings` with `keyframeCount: 2`.

### Build an AnimatorController

1. **Create** — `unity_open_mcp_animator_create` with `asset_paths: ["Assets/Animators/Player.controller"]`.
2. **Add parameters + states** — `unity_open_mcp_animator_modify` with a batch:
   ```json
   [
     {"type":"AddParameter","parameterName":"Speed","parameterType":"Float"},
     {"type":"AddState","layerName":"Base Layer","stateName":"Idle"},
     {"type":"AddState","layerName":"Base Layer","stateName":"Run","motionAssetPath":"Assets/Anims/Run.anim"},
     {"type":"SetDefaultState","layerName":"Base Layer","stateName":"Idle"},
     {"type":"AddTransition","layerName":"Base Layer","sourceStateName":"Idle","destinationStateName":"Run","hasExitTime":false,"conditions":[{"parameter":"Speed","mode":"Greater","threshold":0.1}]}
   ]
   ```
3. **Re-read** — `animator_get_data` shows the new states, default state, and transition with its condition.

### Common recipes

- **Looping clip**: `SetWrapMode` → `Loop`, plus `SetFrameRate` → `30`.
- **Footstep event**: `AddEvent` with `time: 0.5`, `functionName: "OnFootstep"`.
- **Blend tree stand-in**: a controller with two states (`Idle`, `Run`) + a `Float` parameter `Speed` + an `Idle→Run` transition conditioned on `Speed > 0.1` and a `Run→Idle` transition conditioned on `Speed < 0.1`.

## Error codes

| Code | Meaning |
|---|---|
| `paths_hint_required` | Mutating tool called with no `paths_hint`. |
| `missing_parameter` | Missing `asset_paths` (create) or `modifications_json` (modify). |
| `invalid_asset_path` | `asset_path` is not `Assets/`-rooted with the right extension. |
| `asset_not_found` | Asset does not exist at the path (create it first). |
| `invalid_modifications_json` | `modifications_json` is not a JSON array. |

Per-entry modification errors (unknown type, missing required field, bad enum value, unresolved component type, layer/state/parameter not found) do **not** fail the call — they land in the response's `errors` array (indexed `[i] type: message`), and the valid subset still applies.

## Tool reference

| Tool | Mutating | Destructive | Lifecycle | Notes |
|---|---|---|---|---|
| `animation_create` | yes | no | editor_settle | Empty AnimationClip assets. |
| `animation_get_data` | no | no | none | Clip metadata + curves + events. |
| `animation_modify` | yes | **yes** | editor_settle | Batch modification (8 types). |
| `animator_create` | yes | no | editor_settle | Empty AnimatorController assets. |
| `animator_get_data` | no | no | none | Controller name + parameters + layers + states + transitions. |
| `animator_modify` | yes | **yes** | editor_settle | Batch modification (12 types). |

Every mutating tool requires a non-empty `paths_hint` scoped to the asset path — the gate has no whole-project fallback.
