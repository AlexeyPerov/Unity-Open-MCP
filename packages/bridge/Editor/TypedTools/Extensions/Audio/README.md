# Audio — embedded domain tools

Audio typed tools (`unity_open_mcp_audio_source_*` / `unity_open_mcp_audio_listener_*` /
`unity_open_mcp_audio_mixer_*`), embedded inside the bridge. Five tools cover the
AudioSource / AudioListener / AudioMixer layer:

- `audio_source_add` — add an `AudioSource` (clip / volume / pitch / loop /
  play_on_awake / spatial_blend / spatialize / min+max distance).
- `audio_source_modify` — typed patch on an `AudioSource` (volume / pitch / loop /
  play_on_awake / spatial_blend / spatialize / min+max distance / doppler / spread /
  `outputAudioMixerGroup` via an `AudioMixerGroup` asset path).
- `audio_mixer_set_parameter` — set a float on an `AudioMixer` asset's exposed
  parameter (`normalize` maps a 0-1 slider onto the -80..0 dB range).
- `audio_mixer_get_parameter` — read an exposed float parameter (read-only).
- `audio_listener_get` — read `AudioListener` state (read-only). Flags duplicate
  enabled listeners as a warning — Unity errors on multiple active listeners at
  runtime.

Added in M20 Plan 3 to close the Audio parity gap with the competitor
(AnkleBreaker ships a full audio category). The mixer-parameter round-trip
(set then read-back) is the documented advantage — AnkleBreaker's audio tool
touches per-source settings only.

## Compile gate

**None.** The `AudioSource`, `AudioListener`, `AudioMixer`, and
`AudioMixerGroup` types live in the built-in `UnityEngine.AudioModule` and are
present in every Unity install, so this domain ships ungated — no
`UNITY_OPEN_MCP_EXT_AUDIO` define and no sub-asmdef `defineConstraints`. The
owning sub-asmdef only references the bridge Editor asmdef.

## Tool group

All five tools belong to the `audio` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
