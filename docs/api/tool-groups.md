# Tool groups and session visibility

The full MCP catalog contains **250+ tools**, but each session exposes a smaller
active set through `ListTools`.

## Default visibility

Sessions start with five groups enabled:

- `core`
- `gate-and-verify`
- `asset-intelligence`
- `typed-editor`
- `diagnostics`

Every other group is hidden until activated with
`unity_open_mcp_manage_tools`, except package-detected auto-activating groups.

## Group catalog

| Group | Default | Description |
|---|---|---|
| `core` | on | Ping, C# execution, reflection, menu calls, method invocation, and editor status. |
| `gate-and-verify` | on | Validation, checkpoints, deltas, references, dependency analysis, scans, baselines, regression checks, and fixes. |
| `asset-intelligence` | on | Reserialize and structured asset read/search/list. |
| `typed-editor` | on | Assets, materials, shaders, prefabs, GameObjects, components, scenes, packages, console, selection, undo, tags/layers, scripts, objects, ScriptableObjects, and asmdefs. |
| `diagnostics` | on | Profiler session controls: start/stop, config, modules, save/load, and script stats. Per-frame senses live in `agent-senses`. |
| `gate-intelligence` | off | Impact preview, gate-budget estimate, and mutation explanation. |
| `build-settings` | off | Build pipeline, project settings, render/quality settings, and preferences. |
| `agent-senses` | off | Tests, screenshots, Frame Debugger, console, profiler captures, memory/rendering reads, and spatial queries. |
| `unity-hub-control` | off | Local Unity Hub editor discovery, install, module, and install-path tools. |
| `navigation` | off | NavMesh; package-gated on `com.unity.ai.navigation`. |
| `input-system` | off | Input System; package-gated on `com.unity.inputsystem`. |
| `probuilder` | off | ProBuilder; package-gated on `com.unity.probuilder`. |
| `particle-system` | off | Particle System; built-in module. |
| `animation` | off | AnimationClip and AnimatorController; built-in animation module. |
| `splines` | off | Splines; package-gated on `com.unity.splines`. |
| `lighting` | off | Lights, reflection probes, and skybox; built-in module. |
| `audio` | off | AudioSource, AudioMixer, and AudioListener; built-in module. |
| `ui` | off | uGUI canvas, elements, and layout; TMP is optional. |
| `constraints` | off | Animation constraints and LODGroup; built-in modules. |
| `terrain` | off | Terrain creation and editing; built-in module. |
| `sprite2d` | off | SpriteAtlas and texture import tools; built-in 2D module. |
| `cinemachine` | off | Cinemachine 3.x tools, reflection-gated at call time. |
| `timeline` | off | Timeline assets, tracks, clips, and bindings; package-gated. |
| `tilemap` | off | Grid, Tilemap, Tile, and RuleTile tools; package-gated. |
| `shadergraph` | auto | Shader Graph; auto-activates when its package is detected. |
| `vfx` | auto | VFX Graph; auto-activates when its package is detected. |
| `memoryprofiler` | auto | Memory Profiler snapshot capture; auto-activates when its package is detected. |

The canonical domain dependency and activation table is
[Extension domains](../extensions.md).

Always-visible meta-tools stay reachable regardless of session state:
`unity_open_mcp_capabilities`, `unity_open_mcp_list_rules`,
`unity_open_mcp_generate_skill`, `unity_open_mcp_manage_tools`,
`unity_open_mcp_pull_events` / `unity_senses_pull_events`,
`unity_open_mcp_read_compile_errors`, `unity_open_mcp_bridge_status`, and
`unity_open_mcp_ping`. Most of these have no group assignment; `ping` is an
exception — it lives in the `core` group but is pinned always-visible so an
agent that deactivates `core` (to slim its surface) can still probe the bridge
before re-activating. Deactivating a group therefore never hides an
always-visible tool.

## `manage_tools` actions

```json
{ "action": "list_groups" }
```

Lists every group with its active flag, availability, description, and tool
roster.

```json
{ "action": "activate", "group": "navigation" }
```

```json
{ "action": "deactivate", "group": "navigation" }
```

```json
{ "action": "reset" }
```

`reset` restores the five default-on groups.

## State lifecycle

- State is ephemeral, in memory, and scoped to one MCP server process.
- Restarting the server restores the five default groups.
- `reset` clears manual and auto activations beyond that baseline. A
  package-detected group can reappear after the next reconciliation.
- Concurrent server processes do not share activation state.
- When activation changes the visible surface, the server emits
  `notifications/tools/list_changed`; clients should refresh `tools/list`.
  No-op actions do not emit the notification.

## Availability versus activation

These are separate states:

- **Available** means the live bridge compiled or registered the group's tools.
  `capabilities.toolGroups[].available` and
  `manage_tools(list_groups).groups[].available` report it. The value is
  `null` when the bridge is offline and availability cannot be determined.
- **Active** means the current MCP session exposes the group through
  `ListTools`. `manage_tools(list_groups).groups[].active` reports it.

Activating an unavailable group can make descriptors visible, but calls fail
until the Unity dependency is present and the bridge has compiled the domain.
Use `unity_open_mcp_capabilities` as the authoritative availability source.

## Auto-activation

Most groups require manual activation. A group with `autoActivate: true` and a
Unity package dependency activates when the live bridge reports one of its
compiled tools. The shipped auto-activating groups are:

- `shadergraph`
- `vfx`
- `memoryprofiler`

Reconciliation runs lazily during `capabilities` and
`manage_tools(list_groups)`. Auto-activation is still ephemeral and produces
the same list-changed notification. The response identifies
`activationSource: "auto"`, `autoActivated: true`, and the package dependency.

A deliberate manual deactivation wins over automatic activation. Removing a
package only drops a group that was auto-activated; a group reactivated by hand
remains a manual session choice.
