# Unity Open MCP — Cinemachine Extension

Skill for AI agents driving the Unity Cinemachine package
(`com.unity.cinemachine` ≥ 3.x) in a Unity project through the `unity-open-mcp`
MCP server.

> This domain is **reflection-gated** and **opt-in**. Unlike other domain
> packs, the bridge assembly always compiles — but each tool detects
> Cinemachine 3.x presence at call time. When 3.x is absent (package missing
> **or** Cinemachine 2.x installed), every tool returns a clear install /
> upgrade error envelope. The tool group is **hidden** from `ListTools` until
> the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.cinemachine` ≥ 3.x installed. Cinemachine 2.x is
  **not** supported — the tools return `cinemachine_3x_required` when 2.x is
  detected. If `capabilities` reports the `cinemachine` group as compiled in
  but a tool returns `cinemachine_package_required`, install the package via
  the Package Manager.
- The `cinemachine` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="cinemachine")` before
  invoking any `cinemachine_*` tool. Fresh sessions start with only `core`
  visible.

## Tool prefix

All tools in this pack use `unity_open_mcp_cinemachine_*`. Mutating tools
accept the standard `paths_hint` (the host's scene path) and run the full gate
path; the read-only tool (`cinemachine_camera_list`) is gate-free.

## Vocabulary

A **CinemachineCamera** (3.x) is the virtual camera component. It composes a
camera shot from four pipeline slots:

- **Body** — position control (e.g. `CinemachineFollow`, `CinemachineFramingTransposer`,
  `CinemachineHardLockToTarget`).
- **Aim** — rotation control (e.g. `CinemachineRotationComposer`, `CinemachineHardLookAtTarget`).
- **Noise** — shake / handheld motion (e.g. `CinemachineBasicMultiChannelPerlin`).
- **Lens** — field of view, near/far clip, dutch (roll).

The active camera is selected by **priority** (higher wins the Brain's slot).

A **CinemachineBrain** lives on a Unity `Camera` and drives it from whichever
CinemachineCamera currently has the highest priority. The Brain is what blends
between cameras.

**Follow** and **Look At** are the two target slots on a camera — typically
the player Transform (Follow) and an aim target Transform (Look At).

## Canonical workflow: a third-person follow camera

1. **Ensure a Brain** — `unity_open_mcp_cinemachine_brain_ensure` on the main
   Camera. Adds `CinemachineBrain` when absent (idempotent when present).
2. **Create the camera** — `unity_open_mcp_cinemachine_create_camera` adds a
   new GameObject with `CinemachineCamera` to the active scene. Optionally set
   `priority` (higher than competing cameras) and seed `follow_*` / `look_at_*`
   targets. Returns the new GameObject's `instanceId`.
3. **Set the Follow body** — `unity_open_mcp_cinemachine_set_body` with
   `body_name="CinemachineFollow"` to make the camera trail its Follow target.
4. **Tune the lens** — `unity_open_mcp_cinemachine_set_lens` to set
   `field_of_view`, `near_clip`, `far_clip`, and `dutch`.
5. **Add shake (optional)** — `unity_open_mcp_cinemachine_set_noise` with
   `noise_name="CinemachineBasicMultiChannelPerlin"` for handheld / impact
   shake.

### Re-targeting at runtime

- `unity_open_mcp_cinemachine_set_targets` swaps Follow and/or Look At. Omit a
  target to leave it unchanged.

## Reflective notes (Cinemachine 3.x)

The Body/Aim/Noise pipeline in 3.x is **component-based** — `set_body` /
`set_noise` add or replace a `MonoBehaviour` on the camera GameObject. The
component type is passed by **unqualified name** (e.g. `CinemachineFollow`),
resolved under the `Unity.Cinemachine` namespace at call time. Unknown names
return `type_not_found`.

Lens values live in the `Lens` struct on the camera (FieldOfView /
NearClipPlane / FarClipPlane); Dutch is a top-level float.

## Common recipes

### Top-down strategy camera

1. `cinemachine_create_camera` with `priority: 10`, `position: "0,20,0"`,
   `rotation: "90,0,0"`.
2. Skip Body (the camera is hand-placed). Skip Follow.
3. `cinemachine_set_lens` with `field_of_view: 60`.

### Cutscene camera with shake

1. `cinemachine_create_camera` with `look_at_path: "Player"`.
2. `cinemachine_set_body` with `body_name: "CinemachineFramingTransposer"`.
3. `cinemachine_set_noise` with `noise_name: "CinemachineBasicMultiChannelPerlin"`.

## Agent-sense pairing

- `unity_senses_screenshot` (view: "game") visually confirms the camera shot.
- `unity_open_mcp_cinemachine_camera_list` enumerates every camera's instance
  id, priority, and Follow / Look At targets — use it before mutating.

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `cinemachine_create_camera` | yes | editor_settle | New GameObject + CinemachineCamera. |
| `cinemachine_set_targets` | yes | editor_settle | Set Follow / Look At (omit to leave unchanged). |
| `cinemachine_set_lens` | yes | editor_settle | FOV / near / far / dutch. |
| `cinemachine_set_body` | yes | editor_settle | Add or replace the Body component. |
| `cinemachine_set_noise` | yes | editor_settle | Add or replace the Noise component. |
| `cinemachine_brain_ensure` | yes | editor_settle | Idempotent Brain add on a Camera. |
| `cinemachine_camera_list` | no | none | Enumerate cameras + targets. |

Address every target by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). Every mutating tool requires a non-empty
`paths_hint` scoped to the host's scene path — the gate has no whole-project
fallback.
