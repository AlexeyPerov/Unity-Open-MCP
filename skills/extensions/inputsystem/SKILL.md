# Unity Open MCP — Input System Extension

Skill for AI agents driving the Unity Input System in a project through the `unity-open-mcp` MCP server + the **Input System extension pack** (`com.alexeyperov.unity-open-mcp-ext-inputsystem`).

> This pack is **opt-in**. Its tools only resolve when the project's `Packages/manifest.json` includes the input system extension package AND the Unity project has `com.unity.inputsystem` installed. If a tool returns `tool_not_found`, the pack is not installed — surface the manifest line from the bridge window's Extensions tab or the Hub AI Setup wizard.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The input system extension pack is installed (see the bridge window's **Extensions** tab; `inputsystem_get` returns `tool_not_found` otherwise).
- The Unity project has `com.unity.inputsystem` available.

## Tool prefix

All tools in this pack use `unity_open_mcp_inputsystem_*`. Mutating tools target a `.inputactions` asset path — every mutator runs the full gate path with `paths_hint` scoped to that asset; the read-only tool (`inputsystem_get`) is gate-free.

## What is an InputActionAsset?

A `.inputactions` file is a JSON graph with four nested layers:

```
InputActionAsset
├── ActionMap[]           (group of related actions, e.g. 'Player', 'UI')
│   ├── Action[]          (a single input intent, e.g. 'Jump', 'Move')
│   │   └── InputBinding[]  (control paths, e.g. '<Keyboard>/space')
│   └── ...
└── InputControlScheme[]  (device sets, e.g. 'KeyboardMouse', 'Gamepad')
```

A binding's `groups` field ties it to one or more control schemes by name.

## Canonical workflow: a Player action map

1. **Discover** — `unity_open_mcp_inputsystem_get` on an existing `.inputactions` asset to read its maps / actions / bindings before mutating.
2. **Create the asset** — `unity_open_mcp_inputsystem_asset_create` with an `asset_path` ending in `.inputactions`. Pass `initial_action_map: "Player"` to seed the first map in one call. Create intermediate folders first with `assets_create_folder`.
3. **Add a control scheme** — `unity_open_mcp_inputsystem_controlscheme_add` with `scheme_name: "KeyboardMouse"` and `required_devices: ["<Keyboard>", "<Mouse>"]`. Bindings that should only fire under this scheme set `groups: "KeyboardMouse"`.
4. **Add actions** — `unity_open_mcp_inputsystem_action_add` per action. Pick `action_type`:
   - `Button` — edge-triggered (jump, fire).
   - `Value` — continuous analog (move, look). Set `expected_control_type: "Vector2"`.
   - `PassThrough` — raw state passthrough (rare).
5. **Add bindings** — `unity_open_mcp_inputsystem_binding_add` for a single key/button, OR `unity_open_mcp_inputsystem_binding_composite_add` for a multi-key composite (WASD).
6. **Verify** — `unity_open_mcp_inputsystem_get` reflects the resulting structure.

### Composite bindings (WASD move)

A `2DVector` composite synthesizes a `Vector2` from four part bindings:

```json
{
  "composite": "2DVector",
  "parts_json": "[{\"name\":\"up\",\"path\":\"<Keyboard>/w\"},{\"name\":\"down\",\"path\":\"<Keyboard>/s\"},{\"name\":\"left\",\"path\":\"<Keyboard>/a\"},{\"name\":\"right\",\"path\":\"<Keyboard>/d\"}]"
}
```

Composite types and their parts:

| Composite | Parts |
|---|---|
| `2DVector` | `up`, `down`, `left`, `right` |
| `1DAxis` | `negative`, `positive` |
| `Dpad` | `up`, `down`, `left`, `right` |
| `Axis` | `negative`, `positive` |

Each part is one entry in `parts_json` with `name`, `path`, and optional `groups`.

## Common recipes

### Keyboard + mouse player controller

1. `inputsystem_asset_create` → `Assets/Input/Player.inputactions` with `initial_action_map: "Player"`.
2. `inputsystem_controlscheme_add` → `KeyboardMouse` with `required_devices: ["<Keyboard>", "<Mouse>"]`.
3. `inputsystem_action_add` → `Move` (Value, `expected_control_type: "Vector2"`).
4. `inputsystem_binding_composite_add` → 2DVector WASD on `Move`.
5. `inputsystem_action_add` → `Look` (Value, `expected_control_type: "Vector2"`).
6. `inputsystem_binding_add` → `<Mouse>/delta` on `Look` with `groups: "KeyboardMouse"`.
7. `inputsystem_action_add` → `Fire` (Button).
8. `inputsystem_binding_add` → `<Mouse>/leftButton` on `Fire`.

### Gamepad + keyboard dual-bind

Add a `Gamepad` control scheme, then add bindings with `groups: "Gamepad"` alongside the keyboard bindings. A binding with no `groups` fires under every scheme.

## Error codes

| Code | Meaning |
|---|---|
| `paths_hint_required` | Mutating tool called with no `paths_hint`. |
| `invalid_asset_path` | `asset_path` is not `Assets/`-rooted or does not end in `.inputactions`. |
| `asset_already_exists` | `inputsystem_asset_create` over an existing path. |
| `asset_not_found` | No `.inputactions` asset at the path (or path invalid). |
| `actionmap_not_found` / `action_not_found` | Map or action name does not exist in the asset. |
| `actionmap_already_exists` / `action_already_exists` / `controlscheme_already_exists` | Duplicate name. |
| `missing_parameter` | A required parameter was empty. |

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `inputsystem_asset_create` | yes | editor_settle | Creates a new `.inputactions` asset. |
| `inputsystem_actionmap_add` | yes | editor_settle | Adds an ActionMap. |
| `inputsystem_action_add` | yes | editor_settle | Adds an Action + optional initial binding. |
| `inputsystem_binding_add` | yes | editor_settle | Adds a simple binding. |
| `inputsystem_binding_composite_add` | yes | editor_settle | Adds a composite binding (2DVector / 1DAxis / …). |
| `inputsystem_controlscheme_add` | yes | editor_settle | Adds a control scheme + device requirements. |
| `inputsystem_get` | no | none | Reads the full asset structure. |

Every mutating tool requires a non-empty `paths_hint` scoped to the `.inputactions` asset path — the gate has no whole-project fallback.
