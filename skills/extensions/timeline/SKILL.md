# Unity Open MCP — Timeline Extension

Skill for AI agents driving the Unity Timeline package (`com.unity.timeline`)
in a Unity project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools compile
> in only when the project has `com.unity.timeline` installed (the bridge sets
> the `UNITY_OPEN_MCP_EXT_TIMELINE` define automatically — no manual
> scripting-define write). Its tool group is **hidden** from `ListTools` until
> the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The project has `com.unity.timeline` installed. If `capabilities` reports
  the `timeline` group as `available: false`, install the package and let the
  bridge recompile.
- The `timeline` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="timeline")` before
  invoking any `timeline_*` tool.
  Fresh sessions start with two default-on groups (`core` and `gate-and-verify`); activate the other groups you need on demand.

## Tool prefix

All tools in this pack use `unity_open_mcp_timeline_*`. All five tools are
mutating and run the full gate path; `paths_hint` is the timeline asset path
(or the host scene path + asset path for `timeline_director_bind`).

## Vocabulary

A **TimelineAsset** is a `.playable` asset — a project artifact, not a scene
object. It holds an ordered list of **tracks**, each of which holds an ordered
list of **clips**. A **PlayableDirector** is a scene component that drives a
TimelineAsset at runtime.

### Track types

| `track_type` | Unity class | Typical use |
|---|---|---|
| `Animation` | `AnimationTrack` | Animate a Transform / MonoBehaviour via an AnimationClip. |
| `Activation` | `ActivationTrack` | Enable / disable a GameObject for a time range. |
| `Audio` | `AudioTrack` | Play an AudioClip at a time. |
| `Signal` | `SignalTrack` | Fire a signal event at a marker. |
| `Control` | `ControlTrack` | Control a nested Timeline / ParticleSystem / Prefab. |
| `Group` | `GroupTrack` | Container for nesting other tracks. |
| `Playable` | `PlayableTrack` | Generic custom playable track. |

## Canonical workflow: a cutscene

1. **Create the asset** — `unity_open_mcp_timeline_create` writes an empty
   `TimelineAsset` at `asset_path` (an `Assets/.../*.playable` path). Returns
   the asset's instance id. Note it.
2. **Add tracks** — `unity_open_mcp_timeline_track_add` per track. Pass
   `track_type` (`Animation` / `Activation` / `Audio` / `Signal` / `Control` /
   `Group` / `Playable`) and optional `track_name`. Returns the new track's
   root index. Use `parent_track_index` to nest under a Group track.
3. **Add clips** — `unity_open_mcp_timeline_clip_add` per clip. Address the
   track by `track_index` or `track_name` (first match). On typed tracks the
   clip kind follows the track; on a generic Playable track, set `clip_type`.
   `start_time` / `duration` are in seconds.
4. **Bind a director** — `unity_open_mcp_timeline_director_bind` binds the
   asset to a scene `PlayableDirector`. Adds the component when missing.

### Tuning clip / track fields

- `unity_open_mcp_timeline_modify` is the reflective escape hatch. Pass
  `track_index` to target a track, or `track_index + clip_index` to target a
  clip's `PlayableAsset`. Each entry is `{ field, value, type? }` where type
  is `int | float | bool | string | vector` (default inferred). Per-field
  errors are accumulated, not thrown.

```json
{
  "asset_path": "Assets/Cutscenes/Intro.playable",
  "track_index": 0,
  "fields_json": "[{\"field\":\"m_Name\",\"value\":\"PlayerAnim\"}]",
  "paths_hint": ["Assets/Cutscenes/Intro.playable"]
}
```

## Common recipes

### Opening cinematic

1. `timeline_create` with `asset_path: "Assets/Cutscenes/Intro.playable"`,
   `frame_rate: "30"`.
2. `timeline_track_add` `track_type: "Animation"` → track 0.
3. `timeline_track_add` `track_type: "Activation"` → track 1.
4. `timeline_clip_add` to track 0 (the player's intro animation).
5. `timeline_clip_add` to track 1 (a "title card" GameObject that activates
   during the intro).
6. `timeline_director_bind` on the scene's `Director` GameObject.

### Reusing the same director for multiple timelines

A `PlayableDirector` holds one `playableAsset` at a time. To swap the playing
timeline at runtime, call `timeline_director_bind` again with a different
`asset_path` — the assignment is idempotent and overwrites the prior asset.

## Agent-sense pairing

- `unity_senses_screenshot` (view: "game") visually confirms the cutscene
  framing when the director is in play mode.
- `unity_open_mcp_execute_csharp` is the fallback for advanced Timeline
  operations not covered by the typed tools (e.g. setting clip blends, signal
  receivers, animation curves).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `timeline_create` | yes | editor_settle | New `.playable` asset. |
| `timeline_track_add` | yes | editor_settle | One of 7 track types. |
| `timeline_clip_add` | yes | editor_settle | Typed track → track's clip kind. |
| `timeline_director_bind` | yes | editor_settle | Bind asset to scene director. |
| `timeline_modify` | yes | editor_settle | Reflective field patcher. |

Address every timeline by `asset_path` (preferred) or `instance_id`. Every
mutating tool requires a non-empty `paths_hint` scoped to the timeline asset
path — the gate has no whole-project fallback.
