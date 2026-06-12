# Unity-Scanner Extraction Log

Extraction baseline for porting scanner algorithms into `packages/verify`.

## Source

| | |
|---|---|
| **Repository** | Unity-Scanner (local checkout) |
| **Path** | `/Users/alexeyperov/Projects/Unity-Scanner` |
| **Commit** | `4baafb0808fc758c45561c79a691c05bbe58c7c1` |
| **Date extracted** | 2026-06-13 |

## Ported files

| # | Unity-Scanner source | Verify destination | Notes |
|---|---|---|---|
| 1 | `Editor/Utilities/Serialization/USYamlUtilities.cs` | `Editor/Internals/Serialization/YamlUtilities.cs` | YAML file reading + system-reference filtering |
| 2 | `Editor/Utilities/RegexPatterns/USSharedRegex.cs` | `Editor/Internals/RegexPatterns/SharedRegex.cs` | Compiled regex patterns for GUID, fileID, script, layer, event parsing |
| 3 | `Editor/Utilities/AssetDatabase/USPathFilterUtilities.cs` | `Editor/Internals/AssetDatabase/PathFilterUtilities.cs` | Path filtering and ignore-pattern matching |
| 4 | `Editor/Utilities/AssetDatabase/USAssetTypeUtilities.cs` | `Editor/Internals/AssetDatabase/AssetTypeUtilities.cs` | Asset type validation and classification |
| 5 | `Editor/Categories/MissingReferences/MissingReferencesScanner.cs` | `Editor/Rules/MissingReferences/Scanner.cs` | YAML-based missing reference scanner, scoped to paths, no coroutine/yield |
| 6 | `Editor/Categories/MissingReferences/MissingReferencesIssueMapper.cs` | `Editor/Rules/MissingReferences/IssueMapper.cs` | Maps scanner results to VerifyIssue with aligned issue codes |
| 7 | `Editor/Categories/MissingReferences/MissingReferencesResultModels.cs` | `Editor/Rules/MissingReferences/Models.cs` | Reference data models (ExternalReferenceRegistry, AssetReferencesData, etc.) |

## Adapt-on-copy rules

- Namespace: `UnityAgentVerify.Internals.*`
- Header: `// Extracted from Unity-Scanner: <path>`
- Exclude: UI, orchestrator, MCP, batch, cache, tab drawers

## Not ported (M3 not required)

| Unity-Scanner piece | Reason |
|---|---|
| `Core/Categories/*` (IUnityScannerCategory, ScanContext, etc.) | Replaced by `IVerifyRule` / `VerifyScope` / `VerifyRunner` |
| `Core/Issues/*` (UnityScannerIssue, IssueSink) | Replaced by `VerifyIssue` / `VerifySeverity` |
| `Core/Settings/PlatformProfile` | Thresholds will be rule parameters, not shared profile objects |
| `Core/Results/*` | Replaced by `VerifyResult` |
| `UI/Controls/USItemDataBase` | UI layer; result models use plain data classes in verify |
| `Core/Export/USExportUtilities` | CSV/clipboard export is UI feature |
