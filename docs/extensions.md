# Extension domains

Domain-specific typed MCP tools for Unity — NavMesh, Input System, ProBuilder,
lighting, terrain, Shader Graph, and more.

Shipped domains are **embedded** in the bridge package. They compile in when
their Unity dependency is present. You do **not** install a separate MCP
extension UPM package for shipped domains.

## Quick start

1. **Install the Unity package** for the domain (Hub AI Setup wizard or
   `Packages/manifest.json`). Built-in modules (lighting, audio, terrain, …)
   need no manifest entry.
2. **Activate the tool group** for the session — unless the group
   **auto-activates** when its package is installed (see table below).
3. **Check availability** — call `unity_open_mcp_capabilities` or
   `unity_open_mcp_manage_tools(action="list_groups")` to see which groups are
   compiled in (`available`) and active (`active`).

```text
unity_open_mcp_manage_tools(action="activate", group="navigation")
```

Fresh MCP sessions start with five groups visible: `core`, `gate-and-verify`,
`asset-intelligence`, `typed-editor`, and `diagnostics`. Installing a Unity
package makes its embedded domain available to the bridge; most domain groups
still require session activation before they appear in `ListTools`. Domain
tools use the naming pattern `unity_open_mcp_<domain>_<action>` (for example
`unity_open_mcp_navigation_surface_add`).

For group visibility, auto-activation rules, and tool contracts, see
[MCP tools API](api/mcp-tools.md).

## Domain catalog

| Domain | Tool group | Unity dependency | Session activation |
|---|---|---|---|
| Navigation (NavMesh) | `navigation` | `com.unity.ai.navigation` | Manual |
| Input System | `input-system` | `com.unity.inputsystem` | Manual |
| ProBuilder | `probuilder` | `com.unity.probuilder` | Manual |
| Particle System | `particle-system` | Built-in module | Manual |
| Animation | `animation` | Built-in animation module | Manual |
| Splines | `splines` | `com.unity.splines` | Manual |
| Lighting | `lighting` | Built-in | Manual |
| Audio | `audio` | Built-in | Manual |
| UI (uGUI) | `ui` | `com.unity.ugui` | Manual |
| Constraints & LOD | `constraints` | Built-in | Manual |
| Terrain | `terrain` | Built-in | Manual |
| SpriteAtlas + Texture | `sprite2d` | Built-in | Manual |
| Cinemachine | `cinemachine` | `com.unity.cinemachine` ≥ 3.x | Manual |
| Timeline | `timeline` | `com.unity.timeline` | Manual |
| Tilemap | `tilemap` | `com.unity.2d.tilemap` (+ `com.unity.2d.tilemap.extras` for RuleTile) | Manual |
| Shader Graph | `shadergraph` | `com.unity.shadergraph` | **Auto** when package installed |
| VFX Graph | `vfx` | `com.unity.visualeffectgraph` | **Auto** when package installed |
| Memory Profiler | `memoryprofiler` | `com.unity.memoryprofiler` | **Auto** when package installed |

**Manual** — call `manage_tools(action="activate", group="…")` before the
group's tools appear in `ListTools`.

**Auto** — the group activates for the session when the Unity package is
installed; no manual call needed. You can still deactivate or re-activate via
`manage_tools`.

When a dependency is missing, `capabilities` reports
`available: false (dependency missing: …)` for that group.

### Notes

- **SpriteAtlas + Texture** share the `sprite2d` group — activate once for the
  whole 2D art pipeline.
- **Cinemachine** requires Cinemachine 3.x. The bridge detects the installed
  version at call time; 2.x returns `cinemachine_3x_required`.
- **Tilemap RuleTile** (`tilemap_create_rule_tile`) needs
  `com.unity.2d.tilemap.extras`; otherwise the tool returns
  `tilemap_extras_required`.
- **UI** — TextMesh Pro (`TMP_Text`) is optional; when absent,
  `ui_element_add` returns `tmp_package_required`.
- **Terrain** heightmap / splat writes cap at 513×513 per call — use tiled
  region writes for large edits.

Per-domain agent playbooks ship as skills under `skills/extensions/<domain>/`.
See [Skills](skills.md).

## Advanced — community packs

Shipped domains (Nav, Input, ProBuilder, Particles, Animation) are **embedded
in the bridge** and have no separate UPM package. The former standalone
`com.alexeyperov.unity-open-mcp-ext-*` packs for these domains were removed —
they were duplicates of the embedded tools. If a pinned manifest still
references one, drop that entry; the embedded bridge tools provide the same
surface with no separate install.

**Third-party / community** domain packs still live under
`packages/extensions/` as separate UPM packages. See
[Contributing — extensions](contributing/extensions.md) for the authoring
contract and install example.

## Related docs

- [MCP tools API](api/mcp-tools.md) — full tool catalog, groups, auto-activation.
- [Contributing — extensions](contributing/extensions.md) — embedded domain
  gates, wiring checklist, community packs.
- [Architecture](architecture.md) — bridge / verify / MCP server boundaries.
