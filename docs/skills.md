# Skills

Skills are markdown playbooks (`SKILL.md`) that give an AI agent **project-specific guidance** — when to call which tool, in what order, and what to watch for. They ship into your game project alongside the MCP client config so the agent always has the right playbook on hand.

> **Skills vs. tools.** *Tools* (documented in [MCP tools API](api/mcp-tools.md)) do things — they are the verbs the agent can call. *Skills* advise the agent on **how and when** to use those verbs. Every tool in this project is documented in the API reference; skills weave them into reliable workflows.

## What ships

| Skill | Audience | Activates when |
| ----- | -------- | -------------- |
| [`skills/unity-open-mcp/SKILL.md`](../skills/unity-open-mcp/SKILL.md) | Any agent driving a Unity project | Always — the core playbook |
| [`skills/extensions/navigation/SKILL.md`](../skills/extensions/navigation/SKILL.md) | Agents using NavMesh / AI Navigation | Navigation extension pack **and** `com.unity.ai.navigation` installed |
| [`skills/extensions/inputsystem/SKILL.md`](../skills/extensions/inputsystem/SKILL.md) | Agents using the Unity Input System | Input System extension pack **and** `com.unity.inputsystem` installed |
| [`skills/extensions/probuilder/SKILL.md`](../skills/extensions/probuilder/SKILL.md) | Agents using ProBuilder | ProBuilder extension pack **and** `com.unity.probuilder` installed |
| [`skills/extensions/particlesystem/SKILL.md`](../skills/extensions/particlesystem/SKILL.md) | Agents using Particle Systems | Particle System extension pack installed (built-in Unity module) |
| [`skills/extensions/animation/SKILL.md`](../skills/extensions/animation/SKILL.md) | Agents using AnimationClip / Animator | Animation extension pack installed (built-in Unity modules) |
| [`skills/extensions/splines/SKILL.md`](../skills/extensions/splines/SKILL.md) | Agents using the Splines package | `com.unity.splines` installed (embedded domain group) |
| [`skills/extensions/lighting/SKILL.md`](../skills/extensions/lighting/SKILL.md) | Agents using per-Light / ReflectionProbe / skybox tooling | `lighting` tool group activated (built-in lighting module — always compiled) |
| [`skills/extensions/audio/SKILL.md`](../skills/extensions/audio/SKILL.md) | Agents using AudioSource / AudioListener / AudioMixer tooling | `audio` tool group activated (built-in audio module — always compiled) |
| [`skills/extensions/ui/SKILL.md`](../skills/extensions/ui/SKILL.md) | Agents using uGUI (Canvas / elements / layout groups / element modify) | `ui` tool group activated (built-in UI module — always compiled; TMP_Text optional) |
| [`skills/extensions/constraints/SKILL.md`](../skills/extensions/constraints/SKILL.md) | Agents using animation constraints (Position/Rotation/Aim/Parent/Scale) and LODGroup | `constraints` tool group activated (built-in engine modules — always compiled) |
| [`skills/extensions/terrain/SKILL.md`](../skills/extensions/terrain/SKILL.md) | Agents authoring Unity Terrain (heightmaps, splatmaps, trees, neighbor stitching) | `terrain` tool group activated (built-in Terrain module — always compiled) |
| [`skills/extensions/cinemachine/SKILL.md`](../skills/extensions/cinemachine/SKILL.md) | Agents driving Cinemachine virtual cameras | `cinemachine` tool group activated **and** `com.unity.cinemachine` ≥ 3.x installed (reflection-gated) |
| [`skills/extensions/timeline/SKILL.md`](../skills/extensions/timeline/SKILL.md) | Agents authoring Timeline cutscenes / sequences | `timeline` tool group activated **and** `com.unity.timeline` installed (embedded domain group) |
| [`skills/extensions/tilemap/SKILL.md`](../skills/extensions/tilemap/SKILL.md) | Agents painting 2D Tilemaps (Grid, Tile assets, RuleTile) | `tilemap` tool group activated **and** `com.unity.2d.tilemap` installed (RuleTile additionally requires `com.unity.2d.tilemap.extras`) |
| [`skills/extensions/shadergraph/SKILL.md`](../skills/extensions/shadergraph/SKILL.md) | Agents authoring Shader Graphs (create, open, add nodes, connect ports) | `com.unity.shadergraph` installed — the `shadergraph` group **auto-activates** when the package is present (no manual opt-in) |
| [`skills/extensions/vfx/SKILL.md`](../skills/extensions/vfx/SKILL.md) | Agents authoring Visual Effect Graphs (list, open, block property patch) | `com.unity.visualeffectgraph` installed — the `vfx` group **auto-activates** when the package is present (no manual opt-in) |
| [`skills/extensions/memoryprofiler/SKILL.md`](../skills/extensions/memoryprofiler/SKILL.md) | Agents capturing Memory Profiler snapshots (`.snap` files) | `com.unity.memoryprofiler` installed — the `memoryprofiler` group **auto-activates** when the package is present (no manual opt-in) |

### Core playbook

`skills/unity-open-mcp/SKILL.md` is the playbook every agent reads first. It covers:

- **Preconditions** — what must be true before live tools work.
- **Non-negotiable rules** — discover first, scope every mutation, one test run at a time, read the gate.
- **Fast-start sequence** — the canonical `capabilities → manage_tools → ping → mutate` order.
- **Core loop** — mutate → gate → fix, with gate modes and the canonical failure shape.
- **Tool groups & session visibility** — activating only the group you need keeps the prompt small.
- **Typed tool catalog** — the preferred tools for assets, materials, GameObjects, components, prefabs, scenes, packages, profiler, build, settings.
- **Routing rules** — live vs. batch vs. offline reads.
- **Agent checklist** — the before/after list.

### Extension skills

Each `skills/extensions/<domain>/SKILL.md` is scoped to one domain pack. They follow the same shape as the core playbook but cover only that pack's tools: preconditions, tool prefix, face/selection model, and the pack-specific gate behavior. An extension skill is only useful when its pack is installed — if a domain tool returns `tool_not_found`, the pack is not in the project (check the bridge window's **Extensions** tab or the Hub AI Setup wizard).

## How skills get installed

The **Hub AI Setup wizard** writes skills into your project at the per-client path each MCP client expects. The destination paths are defined once in [`skills/client-paths.json`](../skills/client-paths.json) — the single source of truth consumed by both the Hub wizard and the runtime skill generator. You do not edit this file by hand.

| MCP client | Skill destination |
| ---------- | ----------------- |
| Cursor | `.cursor/skills/unity-open-mcp/SKILL.md` |
| Claude (Desktop / Code) | `.claude/skills/unity-open-mcp/SKILL.md` |
| OpenCode | `.opencode/skills/unity-open-mcp/SKILL.md` |
| ZCode / generic `agents` | `.agents/skills/unity-open-mcp/SKILL.md` |

For the step-by-step install flow, see [Wizard setup](wizard-setup.md) (recommended) or [Manual setup](manual-setup.md).

## Project-specific skill generation

The runtime tool `unity_open_mcp_generate_skill` (`{ "write": true }`) regenerates the core `SKILL.md` to reflect the **actual** project — Unity version, installed packages, available verify rules, key MonoBehaviour/ScriptableObject types. Pass `clients` (`cursor`/`claude`/`opencode`/`agents`) to write to one or more client skill folders. Regenerate after package or script changes so the playbook stays grounded.

## Relationship to the rest of the docs

- Skills are **agent-facing** — they live inside game projects and are read by the AI agent.
- These docs (`docs/`) are **human-facing** — read by you.
- For the full tool catalog and route policy, see [MCP tools API](api/mcp-tools.md). For domain catalog and activation, see [Extensions](extensions.md).
