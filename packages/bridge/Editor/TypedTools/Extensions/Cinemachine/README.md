# Cinemachine — embedded domain tools (reflection-gated)

Cinemachine typed tools (`unity_open_mcp_cinemachine_*`), embedded inside the
bridge. Seven tools for in-editor virtual-camera authoring: create camera,
set targets, set lens, set body, set noise, ensure brain, list cameras.

Second Ivan-breadth domain shipped under M20 Plan 6 — closes the "Ivan has it,
we don't" parity gap for the Cinemachine row of the competitive matrix. This is
the **canonical reflection-gated** case named in M18 Plan 1 T18.1.1 task 5
(version-split API trigger): the only domain pack in the bridge that does NOT
compile-gate on its Unity dependency.

## Compile gate (reflection, not compile-gate)

Unlike Splines / Timeline / Tilemap (compile-gate packs), this assembly ALWAYS
compiles. The owning sub-asmdef has:

- no `defineConstraints`
- no Cinemachine package reference

Cinemachine types are resolved at call time via the `CinemachineVersion`
reflection layer (`CinemachineJson.cs`), which distinguishes:

- **Cinemachine 3.x** (`Unity.Cinemachine.CinemachineCamera` + `CinemachineBrain`
  + component pipeline) — supported.
- **Cinemachine 2.x** (`Cinemachine.CinemachineVirtualCamera`) — rejected with
  the `cinemachine_3x_required` error envelope.
- **Package absent** — rejected with the `cinemachine_package_required` error
  envelope (install guidance included).

Why reflection: Cinemachine's 2.x→3.x split changed the public camera class
itself, not just internal plumbing. A compile-gate symbol would force one API
to be selected at compile time; reflection lets the same code path target the
installed version and surface clean install/upgrade errors otherwise.

## Tool group

All seven tools belong to the `cinemachine` group (M18 Plan 2). Hidden from
`ListTools` until the session activates the group via
`unity_open_mcp_manage_tools`.
