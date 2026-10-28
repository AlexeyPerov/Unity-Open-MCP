# Input Simulation domain

Play-mode input simulation tools for testing a game in the Unity Editor. This is
the surface that lets an agent **interact with the running game** — click a
button, drag an item, swipe, press a key — complementing the existing play-mode
controls (`editor_set_state`), screenshot, and console-read surface.

Seven tools share the `input-simulation` group:

| Tool | Reach | Compiles when |
|---|---|---|
| `inputsim_pointer` | uGUI — `routing: dispatch` (EventSystem + ExecuteEvents) on a named GameObject, object_id, or screen point; `routing: raycast_real` injects a real touch so the project's own input module decides what is hit | `com.unity.ugui` present |
| `inputsim_pointer3d` | 3D / legacy — `Physics.Raycast` + `OnMouseDown`/`OnMouseUp`/`OnMouseDrag` SendMessage | `com.unity.ugui` present |
| `inputsim_step` | Advance N play-mode frames (`EditorApplication.Step`) | `com.unity.ugui` present |
| `inputsim_probe` | List active uGUI interactables (paths, ids, rects, interactable, occluded) | `com.unity.ugui` present |
| `inputsim_touch` | Backend-aware touch / swipe: legacy Input Manager touches (`legacy_touch`) and/or Input System `Touchscreen` (`input_system`) | always (bridge root) |
| `inputsim_gesture` | Keyframed finger paths over real play-mode frames on the shared play-mode scheduler | always (bridge root) |
| `inputsim_key` | Input System keyboard device events (`Keyboard.current`) | `com.unity.inputsystem` present |

Layout:

- `Device/` (no asmdef — part of the bridge root assembly): backend routing
  (`InputRouting`), the version-checked legacy touch adapter
  (`EditorInputAdapter`), target resolution, the framed sequence runner, and the
  touch + gesture tools. It compiles in every project, including legacy-only ones.
- `Core/` (uGUI sub-asmdef): pointer / pointer3d / step / probe, `raycast_real`,
  and the uGUI top-hit probe used by gesture traces.
- `../InputSystem/` (Input System sub-asmdef): the keyboard tool and the
  `Touchscreen` backend that registers into `InputRouting`.

The shared play-mode scheduler the gesture runs on lives in
`Editor/PlayMode/` (bridge root) so later play-mode tools reuse it.

## Scope and limits

- **Most tools are play-mode-only** (refuse with `play_mode_required`). `probe`
  is read-only and works in edit mode too — use it to build a click plan before
  entering play mode.
- **Gate-free (`IsMutating=false`).** Input writes no assets — the asset-integrity
  gate does not apply (same model as `editor_set_state`).
- **Input surfaces.** This domain covers uGUI (`ExecuteEvents` or a real
  touch through the input module), legacy 3D (`OnMouseDown` + `Physics.Raycast`),
  legacy Input Manager touches (and the legacy mouse API through
  `Input.simulateMouseWithTouches`), and Input System device events. Legacy
  `UnityEngine.Input.GetKey*` and legacy hover without a press are **not
  covered** — use `invoke_method`/`execute_csharp`.
- **Backends are reported.** Touch, gesture and `raycast_real` report `backend`
  as an array and refuse with `input_backend_unavailable`,
  `legacy_multitouch_unsupported` or `unsupported_editor_version` instead of a
  silent ok.
- **Callback vs polling matters for `key`/`touch`.** A `tap`/`hold`/`swipe`
  without `advance_frames` processes down+up within one dispatch —
  invisible to polling code (`wasPressedThisFrame`, per-frame touch delta). Pass
  `advance_frames ≥ 1`, or split into `down` → `inputsim_step` → `up` for held-state polling.
  Framed tap/swipe processes input immediately before gameplay Update,
  preserving press edges and touch deltas for gameplay. Named-key `up` preserves
  other held keys and releases only explicit modifiers.
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
- Gameplay reads `Input.GetTouch` / `Input.mousePosition` or `Touchscreen.current`
  → **`touch`** (taps/swipes) or **`gesture`** (holds and drags over real frames).
- A button must be reachable the way a finger would reach it → **`pointer`** with
  `routing: raycast_real`.
- Let tweens/animations settle before a screenshot → **`step`**.

See `skills/extensions/input-simulation/SKILL.md` for the full
`probe → click → step → screenshot` playbook.
