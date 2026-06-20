# Extension Packs

Extension packs add optional typed MCP tools for specific Unity domains without expanding the base install.

Each pack is a separate UPM package. Install only what your project needs.

## Available packs

- Navigation (NavMesh)
- Input System
- ProBuilder
- Particle System
- Animation

## Install options

### A) Unity Hub Pro wizard

- Open AI Setup wizard
- Choose optional extension packs
- Apply the generated `Packages/manifest.json` changes

[[SCREENSHOT:EXTENSIONS-INSTALL-CHECKBOXES]]

### B) Manual manifest edit

Add package IDs under `dependencies` in `Packages/manifest.json`.

Example for local monorepo development:

```json
{
  "dependencies": {
    "com.alexeyperov.unity-open-mcp-ext-navigation": "file:../../packages/extensions/navigation"
  }
}
```

## Tool naming

Extension tools use:

```text
unity_open_mcp_<domain>_<action>
```

Example: `unity_open_mcp_navigation_surface_add`.

## Authoring a new pack (maintainers)

- Start from `packages/extensions/template/`.
- Keep extension code under `Editor/` with a dedicated asmdef.
- Register tools with bridge attributes and MCP tool definitions.
- Add tests under `Tests/Editor/`.
- Update extension catalogs used by bridge UI and Hub UI.
- Document tool usage in the extension skill and API docs.

## Related docs

- [MCP tools API](api/mcp-tools.md)
- [Manual setup](manual-setup.md)
- [Wizard setup](wizard-setup.md)
