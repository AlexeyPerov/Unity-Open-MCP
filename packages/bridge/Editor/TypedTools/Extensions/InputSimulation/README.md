# Input Simulation domain

Play-mode input simulation tools for testing a game in the Unity Editor. This is
the surface that lets an agent **interact with the running game** — click a
button, drag an item, swipe, press a key — complementing the existing play-mode
controls (`editor_set_state`), screenshot, and console-read surface.

Six tools share the `input-simulation` group:

| Tool | Reach | Compiles when |
|---|---|---|
| `inputsim_pointer` | uGUI (`EventSystem` + `ExecuteEvents`) — click/drag/hover/submit on a named GameObject, object_id, or screen point | `com.unity.ugui` present |
| `inputsim_pointer3d` | 3D / legacy — `Physics.Raycast` + `OnMouseDown`/`OnMouseUp`/`OnMouseDrag` SendMessage | `com.unity.ugui` present |
| `inputsim_step` | Advance N play-mode frames (`EditorApplication.Step`) | `com.unity.ugui` present |
| `inputsim_probe` | List active uGUI interactables (paths, ids, rects, interactable, occluded) | `com.unity.ugui` present |
| `inputsim_key` | Input System keyboard device events (`Keyboard.current`) | `com.unity.inputsystem` present |
| `inputsim_touch` | Input System touch / swipe device events (`Touchscreen.current`) | `com.unity.inputsystem` present |

The uGUI half (`pointer`/`pointer3d`/`step`/`probe`) and the Input System half
(`key`/`touch`) ship as **independent sub-asmdefs** so a project gets whichever
half its packages allow.

## Scope and limits

- **Most tools are play-mode-only** (refuse with `play_mode_required`). `probe`
  is read-only and works in edit mode too — use it to build a click plan before
  entering play mode.
- **Gate-free (`IsMutating=false`).** Input writes no assets — the asset-integrity
  gate does not apply (same model as `editor_set_state`).
- **Four input surfaces exist.** This domain covers uGUI (`ExecuteEvents`),
  legacy 3D (`OnMouseDown` + `Physics.Raycast`), and the new Input System (device
  events). Games reading legacy `UnityEngine.Input.GetKey*` directly (not via
  `OnMouseDown`) are **not covered** — use `invoke_method`/`execute_csharp`.
- **Callback vs polling matters for `key`/`touch`.** A `tap`/`hold`/`swipe`
  without `advance_frames` processes down+up in a single `InputSystem.Update` —
  invisible to polling code (`wasPressedThisFrame`, per-frame touch delta). Pass
  `advance_frames ≥ 1`, or split into `down` → `inputsim_step` → `up`.
- **Resolution precedence (`pointer`):** `object_id` (InstanceId, EntityId-safe)
  > `target` (name or slash-path, with duplicate detection + partial-path match)
  > `screen_x`/`screen_y` (raycasts through `EventSystem`).
- **Honesty fields.** `pointer`'s response reports `interactable`, `blockedBy`,
  and (for drag) `dropTarget`/`dropLanded` — so a click on a disabled button or
  behind a modal is never a silent ok.

## When to use which

- Discover what to click → **`probe`** (lists paths + ids).
- Click/drag a uGUI element → **`pointer`** (use `object_id` from probe).
- Click a 3D world object / `OnMouseDown` gameplay → **`pointer3d`**.
- Gameplay reads `Keyboard.current` → **`key`** (`advance_frames` for polling).
- Gameplay reads `Touchscreen.current` → **`touch`** (`advance_frames` for polling).
- Let tweens/animations settle before a screenshot → **`step`**.

See `skills/extensions/input-simulation/SKILL.md` for the full
`probe → click → step → screenshot` playbook.
