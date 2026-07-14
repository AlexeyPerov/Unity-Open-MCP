# Unity Open MCP — Audio Extension

Skill for AI agents driving Unity audio (AudioSource / AudioListener / AudioMixer)
in a project through the `unity-open-mcp` MCP server.

> This domain is **embedded** in the bridge and **opt-in**. Its tools use the
> built-in audio module (`AudioSource` / `AudioListener` / `AudioMixer` /
> `AudioMixerGroup` from `UnityEngine.AudioModule`) — no Unity package install is
> required, and they compile into every bridge build. Its tool group is
> **hidden** from `ListTools` until the connected session activates it.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The `audio` tool group is activated — call
  `unity_open_mcp_manage_tools(action="activate", group="audio")` before
  invoking any audio tool.
  Fresh sessions start with five default-on groups: `core`, `gate-and-verify`,
  `asset-intelligence`, `typed-editor`, and `diagnostics`.
  Because audio is built-in, `capabilities` always reports the `audio` group
  as `available: true` (no `domainDefine`).

## Tool prefixes

Two prefixes share the `audio` group:

- `unity_open_mcp_audio_source_*` — AudioSource add + typed modify.
- `unity_open_mcp_audio_mixer_*` — AudioMixer exposed-parameter set + get.
- `unity_open_mcp_audio_listener_get` — AudioListener read (read-only).

Mutating tools accept the standard `paths_hint` and run the full gate path;
read-only tools (`audio_listener_get`, `audio_mixer_get_parameter`) are
gate-free.

## Canonical workflow: an audio source

1. **Discover** — `unity_open_mcp_component_list_all` (filter `AudioSource`) or
   `unity_open_mcp_gameobject_find` to locate existing sources. Address any host
   by `instance_id` > `path` > `name`.
2. **Add a source** — `unity_open_mcp_audio_source_add` on a GameObject. Set
   `clip_path` (`Assets/.../*.wav`), `volume` (0-1, default 1), `pitch`
   (default 1), `loop` (default true), `play_on_awake` (default true),
   `spatial_blend` (0=2D, 1=3D, default 0), `spatialize`, and 3D
   `min_distance` / `max_distance`. Returns the source state. Idempotent —
   re-using an existing AudioSource reports `added:false`.
3. **Tune it** — `unity_open_mcp_audio_source_modify` for the common fields
   (`clip_path`, `volume`, `pitch`, `loop`, `play_on_awake`, `spatial_blend`,
   `spatialize`, `min_distance`, `max_distance`, `doppler_level`, `spread`).
   Each field is optional — omit to leave unchanged.
4. **Route to a mixer group** — pass `mixer_group_path` (an `Assets/.../*.mix`
   asset path) to `audio_source_modify` to bind
   `AudioSource.outputAudioMixerGroup` to the mixer's first group. Pass
   `clear_mixer_group: true` to unbind.

## AudioMixer parameters (the documented advantage)

`unity_open_mcp_audio_mixer_set_parameter` sets a float on an `AudioMixer`
asset's exposed parameter:

- `mixer_path` — `Assets/.../*.mix` asset path.
- `parameter_name` — the exposed parameter name. **Expose it first** in the
  Audio Mixer window (`Edit > Project Settings > Audio` is not the same); a
  name that is not exposed returns `parameter_not_exposed`.
- `value` — raw float (dB for volume params).
- `normalize` (default false) — maps a 0-1 slider onto the -80..0 dB range so a
  friendly volume level can be passed directly.

The mixer asset is marked dirty — call `assets_refresh` / `scene_save` to
commit. Read the value back with `unity_open_mcp_audio_mixer_get_parameter`
(gate-free) to confirm the round-trip. This set+read-back loop is the
documented advantage over per-source-only audio tools.

## AudioListener read

`unity_open_mcp_audio_listener_get` (gate-free) reports every `AudioListener`
in the open scene(s): host name, hierarchy path, `enabled` flag, instance id,
plus an `enabledCount` and a `duplicateWarning` flag. Unity allows at most one
**enabled** `AudioListener` at runtime — when `duplicateWarning: true`, disable
the extra listener via `unity_open_mcp_component_get` / object mutation before
entering Play Mode.

## Common recipes

### 2D music source

1. `audio_source_add` on an empty GameObject named "Music" with
   `clip_path: "Assets/Audio/music.wav"`, `volume: 0.8`, `loop: true`,
   `spatial_blend: 0` (2D), `play_on_awake: true`.
2. (Optional) `audio_source_modify` with `mixer_group_path:
   "Assets/Audio/MasterMixer.mix"` to route through the Master group.

### 3D positional sound

1. `audio_source_add` on the emitter GameObject with `spatial_blend: 1.0`,
   `min_distance: 1.0`, `max_distance: 20.0`, `volume: 1.0`.
2. `audio_source_modify` to set `doppler_level: 1.0`, `spread: 60`.

### Mixer volume slider

1. `audio_mixer_get_parameter` with `mixer_path`, `parameter_name: "masterVolume"`
   to confirm the param is exposed and read the current dB.
2. `audio_mixer_set_parameter` with `value: 0.7`, `normalize: true` to set a
   friendly volume level (0.7 → ~-2.5 dB).

### Check listener state before Play Mode

1. `audio_listener_get`. If `duplicateWarning: true`, there is more than one
   enabled listener — disable the extras before playing.

## Agent-sense pairing

- `unity_senses_screenshot` (view: `"game"`) visually confirms a scene; pair
  with a Play Mode check to confirm audible sources are wired. (Audio itself is
  not captured by screenshots — verify via the source/mixer state.)
- `unity_open_mcp_component_get` reads the raw AudioSource fields after a
  mutate (complementary to the structured source state returned by the audio
  tools).

## Tool reference

| Tool | Mutating | Lifecycle | Notes |
|---|---|---|---|
| `audio_source_add` | yes | editor_settle | Idempotent — re-using reports `added:false`. |
| `audio_source_modify` | yes | editor_settle | Typed fields; each optional. `mixer_group_path` binds the mixer's first group. |
| `audio_mixer_set_parameter` | yes | editor_settle | Exposed-parameter set on a `.mix` asset; `normalize` maps 0-1 → dB. |
| `audio_listener_get` | no | none | Listener roster + `duplicateWarning` (read-only). |
| `audio_mixer_get_parameter` | no | none | Exposed-parameter read (read-only). |

Address every target by `instance_id` > `path` > `name` (same model as
`gameobject_*` / `component_*`). Every mutating tool requires a non-empty
`paths_hint` scoped to the host's scene path (for `audio_source_*`) or the
mixer asset path (for `audio_mixer_set_parameter`) — the gate has no
whole-project fallback.
