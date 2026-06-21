# ParticleSystem — embedded domain tools

Particle System typed tools (`unity_open_mcp_particle_system_*`), embedded
inside the bridge. Two tools on a scene `ParticleSystem` component: get
(runtime state + module reads) and modify (per-module field patch).

Ported in M18 Plan 3 from the former standalone extension pack at
`packages/extensions/particlesystem/` (now frozen). Logic, tool IDs, JSON
schema, and gate contracts are unchanged from the legacy pack — only the
namespace moved (`UnityOpenMcpExtensions.ParticleSystemExt` →
`UnityOpenMcpBridge.Extensions.ParticleSystem`).

## Compile gate

Two-layer gate (see `docs/extensions.md` §Embedded domain model):

1. The bridge root asmdef
   (`packages/bridge/Editor/com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`)
   sets `UNITY_OPEN_MCP_EXT_PARTICLESYSTEM` via `versionDefines` when the
   built-in `UnityEngine.ParticleSystemModule` is present.
2. This folder's sub-asmdef carries
   `defineConstraints: ["UNITY_OPEN_MCP_EXT_PARTICLESYSTEM"]`. The
   ParticleSystem API is a built-in engine module, so no separate package
   reference is needed.

Each source file additionally wraps its body in
`#if UNITY_OPEN_MCP_EXT_PARTICLESYSTEM` as a belt-and-suspenders guard.

## Tool group

Both tools belong to the `particle-system` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
