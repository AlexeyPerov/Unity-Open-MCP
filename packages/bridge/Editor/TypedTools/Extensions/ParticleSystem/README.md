# ParticleSystem — embedded domain tools

Particle System typed tools (`unity_open_mcp_particle_system_*`), embedded
inside the bridge. Two tools on a scene `ParticleSystem` component: get
(runtime state + module reads) and modify (per-module field patch).

Ported in M18 Plan 3 from the former standalone extension pack at
`packages/extensions/particlesystem/` (now frozen). Logic, tool IDs, JSON
schema, and gate contracts are unchanged from the legacy pack — only the
namespace moved (`UnityOpenMcpExtensions.ParticlesExt` →
`UnityOpenMcpBridge.Extensions.ParticlesExt`).

## Compile gate

UNGATED. `UnityEngine.ParticleSystem` is a core engine module present in
every Unity install, so this domain ships unconditionally (like Audio /
Constraints / Lighting / Terrain). The former `UNITY_OPEN_MCP_EXT_PARTICLESYSTEM`
compile-gate never resolved — `versionDefines` cannot match a bare engine
module name like `UnityEngine.ParticleSystemModule` — so the gate was removed
and the source compiles directly.

The C# namespace is `UnityOpenMcpBridge.Extensions.ParticlesExt`. The
`Ext` suffix avoids colliding with the `UnityEngine.ParticleSystem` type for
unqualified references and IDE autocomplete. The assembly name keeps
`ParticleSystem` for tool-id stability (`unity_open_mcp_particle_system_*`).

## Tool group

Both tools belong to the `particle-system` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
