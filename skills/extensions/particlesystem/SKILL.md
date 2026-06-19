# Unity Open MCP — Particle System Extension

Skill for AI agents driving Unity Particle System components in a project through the `unity-open-mcp` MCP server + the **Particle System extension pack** (`com.alexeyperov.unity-open-mcp-ext-particlesystem`).

> This pack is **opt-in**. Its tools only resolve when the project's `Packages/manifest.json` includes the particlesystem extension package. ParticleSystem is a built-in Unity module — no extra Unity package is needed. If a tool returns `tool_not_found`, the pack is not installed — surface the manifest line from the bridge window's Extensions tab or the Hub AI Setup wizard.

## Preconditions

- Unity Editor is open with the target project.
- `unity_open_mcp_ping` returns `connected: true`.
- The particlesystem extension pack is installed (see the bridge window's **Extensions** tab; `particle_system_get` returns `tool_not_found` otherwise).

## Tool prefix

All tools in this pack use `unity_open_mcp_particle_system_*`. ParticleSystem is a scene component — the lone mutator (`particle_system_modify`) runs the full gate path with `paths_hint` scoped to the host's scene path; the read-only tool (`particle_system_get`) is gate-free.

## Always get before you modify

`particle_system_get` is cheap (gate-free, read-only). Call it first to:

- confirm the target actually has a `ParticleSystem` component (returns `component_not_found` otherwise),
- see the runtime state (`isPlaying`, `particleCount`, `time`),
- read the current value of any module so you can decide what to patch.

## Module surface (modify)

`particle_system_modify` takes a `module` discriminator + a `fields_json` JSON object of `{field: value}` entries to apply to that module. One module per call — chain multiple calls for multi-module edits.

Only the documented fields per module are accepted. Unknown fields land in `unknownFields` (and are skipped); the call still succeeds so the agent can iterate on the valid subset. Bad-typed values land in `errors`.

| Module | Fields (scalar types) |
|---|---|
| `main` | `duration`(read-only) / `loop`(bool) / `prewarm`(bool) / `startDelay`(float) / `startLifetime`(float) / `startSpeed`(float) / `startSize`(float) / `startSize3D`(bool) / `startRotation3D`(bool) / `startRotation`(float, radians) / `gravityModifier`(float) / `playOnAwake`(bool) / `maxParticles`(int) / `simulationSpace`(Local/World/Custom) / `scalingMode`(Hierarchy/Local/Shape) |
| `emission` | `enabled`(bool) / `rateOverTime`(float) / `rateOverDistance`(float) |
| `shape` | `enabled`(bool) / `shapeType`(enum — see `particle_system_get`) / `radius`(float) / `radiusThickness`(float 0-1) / `angle`(float degrees) / `arc`(float degrees) / `position`("x,y,z") / `rotation`("x,y,z") / `scale`("x,y,z") |
| `color_over_lifetime` | `enabled`(bool) — gradient editing is out of scope; use execute_csharp for fine-grained gradient authoring |
| `size_over_lifetime` | `enabled`(bool) / `separateAxes`(bool) |
| `rotation_over_lifetime` | `enabled`(bool) / `separateAxes`(bool) / `x`(float rad/s) / `y`(float rad/s) / `z`(float rad/s) |
| `noise` | `enabled`(bool) / `strength`(float) / `frequency`(float) / `scrollSpeed`(float) / `damping`(bool) / `octaveCount`(int) / `quality`(Low/Medium/High) |
| `collision` | `enabled`(bool) / `type`(Planes/World) / `dampen`(float 0-1) / `bounce`(float) / `lifetimeLoss`(float 0-1) |
| `trails` | `enabled`(bool) / `ratio`(float 0-1) / `lifetime`(float) |
| `renderer` | `renderMode`(Billboard/Mesh/HorizontalBillboard/VerticalBillboard/Stretch3D) / `alignment`(View/World/Facing/Velocity) / `sortMode`(None/Distance/OldestInFront/YoungestInFront) / `maskInteraction`(None/VisibleInsideMask/VisibleOutsideMask) |

> The modify tool accepts **scalar** values only (it sets the constant mode of any MinMaxCurve / MinMaxGradient implicitly). For curve / gradient authoring, drop down to `execute_csharp` and edit the `AnimationCurve` / `Gradient` directly.

## Canonical workflows

### Inspect a particle effect

1. **Address the host** — by `instance_id` (best) or `path` / `name`.
2. **Read** — `unity_open_mcp_particle_system_get` with `include_all: true` for a full snapshot, or toggle the per-module flags for a focused read.

### Tune an existing effect

1. **Read** — `unity_open_mcp_particle_system_get` with `include_main: true, include_emission: true`.
2. **Patch the main module** — `unity_open_mcp_particle_system_modify` with `module: "main"`, `fields_json: "{\"maxParticles\": 5000, \"loop\": false}"`.
3. **Patch emission** — another `particle_system_modify` call with `module: "emission"`, `fields_json: "{\"rateOverTime\": 42.0}"`.
4. **Re-read** — `particle_system_get` to confirm the change persisted.

### Common recipes

- **Fire**: emission `rateOverTime: 20-40`, shape `shapeType: Cone, angle: 25, radius: 0.1`, main `startLifetime: 1.5, startSpeed: 3, startSize: 0.5`, color_over_lifetime enabled (use execute_csharp for the orange→red→smoke gradient).
- **Smoke**: emission `rateOverTime: 10`, main `startLifetime: 4, startSpeed: 0.5, startSize: 1`, noise enabled `strength: 1, frequency: 0.5`.
- **Burst**: emission `rateOverTime: 0` + use execute_csharp to add a Burst entry to `emission.bursts`.

## Error codes

| Code | Meaning |
|---|---|
| `paths_hint_required` | `particle_system_modify` called with no `paths_hint`. |
| `target_not_found` | No GameObject resolved by `instance_id` / `path` / `name`. |
| `component_not_found` | Target has no `ParticleSystem` (or no `ParticleSystemRenderer` for the `renderer` module). |
| `missing_parameter` | Missing `module` or `fields_json`. |
| `invalid_module` | Unknown `module` value (see the table above). |
| `invalid_fields_json` | `fields_json` is not a flat JSON object of scalars. |

Per-field type mismatches and unknown field names do **not** fail the call — they land in the response's `errors` and `unknownFields` arrays respectively, and the valid subset still applies.

## Tool reference

| Tool | Mutating | Destructive | Lifecycle | Notes |
|---|---|---|---|---|
| `particle_system_get` | no | no | none | Runtime state + opt-in module data. |
| `particle_system_modify` | yes | no | editor_settle | Per-module field patch (scalars only). |

Address every target by `instance_id` > `path` > `name` (same model as `gameobject_*` / `component_*`). `particle_system_modify` requires a non-empty `paths_hint` scoped to the host's scene path — the gate has no whole-project fallback.
