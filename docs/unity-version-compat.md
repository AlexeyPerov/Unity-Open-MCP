# Unity version compatibility

Which Unity Editor versions the Unity Open MCP packages support, how the code
branches between them, and what the wizard does at each version.

## Supported range

| Tier | Unity version | Package behaviour | Wizard behaviour |
|---|---|---|---|
| **Minimum** | **2022.3 LTS** | Both packages declare `"unity": "2022.3"` in their manifests. UPM refuses to resolve them below this floor. | Hard block — the Continue button is disabled and a red error is shown. |
| **Recommended** | **Unity 6 (6000.0+)** | Native Unity 6 toolbar element (`MainToolbarElement`). | Green "Unity 6+" chip; no warning. |
| Between | 2022.3 LTS ≤ v < Unity 6 | Installs and runs. Toolbar uses the legacy fallback (see below). | Amber warning (never a block). |
| Below min | < 2022.3 (e.g. 2021.3 LTS, 2019.4) | UPM refuses to resolve. | Hard block. |

Older-than-2022.3 editors are not claimed as supported. 2021.3 LTS in
particular would need additional C# 9 / .NET guards and is end-of-life. They may
happen to compile, but nothing in this repo tests or guarantees it.

## Version-gated APIs

Every Unity-6-only API in the bridge is wrapped in `#if UNITY_6000_0_OR_NEWER`
with a working `#else` fallback. The single file that does this is
[`packages/bridge/Editor/UI/BridgeToolbarToggle.cs`](../packages/bridge/Editor/UI/BridgeToolbarToggle.cs):

| Unity 6 API (guarded) | 2022.3 LTS fallback (`#else`) |
|---|---|
| `using UnityEditor.Toolbars;` | `using UnityEngine.UIElements;` |
| `[MainToolbarElement(...)]` + `MainToolbarElement Create()` factory that registers the green/gray "MCP" toggle in the native Unity 6 toolbar. | `[InitializeOnLoad]` static constructor polls `EditorApplication.update`, reflects the internal `UnityEditor.Toolbar` `EditorWindow`, and injects a `UnityEngine.UIElements.Button` into the first available toolbar zone (`ToolbarZonePlayModes` / `ToolbarZoneMiddleAlign` / …). |
| `MainToolbar.Refresh(ToolbarId)` to repaint. | `ApplyLegacyVisual()` sets inline `style.backgroundColor` / `style.color` on the injected button. |

Everything else in `packages/bridge/Editor` and `packages/verify/Editor` uses
only APIs available on both 2022.3 LTS and Unity 6. Notable non-gated call:

- `GraphicsSettings.currentRenderPipeline` in
  `packages/bridge/Editor/Profiler/ManageProfilerTool.cs`. This member exists
  since Unity **2021.2**, so it is present on both supported versions and needs
  no `#if` guard. It is wrapped in `try/catch` with a `QualitySettings`
  fallback.

The verify asmdef (`com.alexeyperov.unity-open-mcp-verify.Editor.asmdef`) has
empty `defineConstraints` / `versionDefines` — its guards rely on Unity's
built-in `UNITY_6000_0_OR_NEWER` scripting-define symbol, which is set
automatically by Unity 6.0 and newer.

The bridge root asmdef
(`com.alexeyperov.unity-open-mcp-bridge.Editor.asmdef`) populates
`versionDefines` to drive the **embedded domain model** (M18 Plan 1): each
entry maps a Unity domain package/module to a `UNITY_OPEN_MCP_EXT_<DOMAIN>`
symbol when the dependency resolves. These defines gate the per-domain
sub-asmdefs under `Editor/TypedTools/Extensions/` — they are independent of
the Unity-version guards and add no manual Player Settings symbol writes. See
[Extensions](extensions.md) §Embedded domain model.

## Wizard behaviour

The Hub AI Setup wizard reads `ProjectSettings/ProjectVersion.txt` and computes
two flags (Rust: `hub/src-tauri/src/config/wizard.rs`,
`meets_min_unity_version` / `meets_recommended_unity_version`; TypeScript mirror
in `hub/src/lib/services/config.ts`):

- **Below the minimum** → Step 2 shows a red error block and disables Continue.
  The package would not resolve anyway, so the wizard surfaces the same floor.
- **Meets the minimum but below recommended** → Step 2 shows an amber warning
  block (non-blocking) explaining the toolbar fallback. The Done screen shows a
  "supported (legacy toolbar)" chip instead of the green "Unity 6+" chip.
- **Meets recommended (Unity 6+)** → Step 2 shows a green success hint and the
  Done screen shows a green "Unity 6+" chip.

## CI coverage

`.github/workflows/unity-open-mcp-verify-demo.yml` runs the package Test Runner
(`-runTests` against both `*.Editor.Tests` assemblies) on a matrix of supported
editors. Each leg resolves its Unity binary from a repo variable:

| Matrix leg | Repo variable | Notes |
|---|---|---|
| `unity-6` | `UNITY_PATH_UNITY_6` (legacy `UNITY_PATH` fallback) | Full regression check + scan + tests. The checked-in baseline is a Unity 6 artifact. |
| `unity-2022-lts` | `UNITY_PATH_UNITY_2022_LTS` | Compile/import smoke + full EditMode test run. Proves both packages resolve and their tests pass on the previous LTS. |

A leg whose variable is unset is reported as **skipped**, not failed, so a runner
with only one editor installed still produces a green run for the editor it has.
To enable the second leg, set the matching repo variable under
**Settings → Secrets and variables → Actions → Variables** on a self-hosted
runner labelled `unity` with that editor installed and licensed.

## Related docs

- [Manual setup](manual-setup.md) — install the packages without the wizard.
- [Wizard setup](wizard-setup.md) — guided install in Unity Hub Pro.
- [Architecture](architecture.md) — bridge / verify / MCP server boundaries.
