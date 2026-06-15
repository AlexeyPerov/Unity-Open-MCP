# Verify package rules

## Scope

Rules for `packages/verify/` — the scoped health-check package (`com.alexeyperov.unity-open-mcp-verify`). Inherits root `AGENTS.md`; deeper rules win on overlap.

## Package shape

- Editor-only Unity package. All code lives under `Editor/` and uses the `UnityOpenMcpVerify` namespace.
- No dependency on the bridge package — verify must stay usable standalone (the bridge depends on verify, not the reverse). Do not add a reference to `com.alexeyperov.unity-open-mcp-bridge`.
- `Tests/` holds EditMode tests.

## Verify rules

- Every rule implements `IVerifyRule` (`Editor/Core/IVerifyRule.cs`) and lives in its own folder under `Editor/Rules/{RuleName}/`.
- **Every rule must declare a stable `Id`** (e.g. `missing_references`, `scene_prefab_health`) — this is the canonical identifier surfaced in MCP tool responses, the capability catalog, and the gate delta.
- **Every `VerifyIssue` must carry an `IssueCode`** (e.g. `missing_script`, `missing_guid`). Issue codes are the link key between rules and fixes (`FixProviderRegistry.TryGetFixInfo` matches on `ruleId|severity|path|issueCode`). A rule that emits issues without an issue code breaks the fix-linkage contract.
- Severity (`Error` / `Warning`) is set per-issue, not per-rule. The gate delta treats Errors as failures; Warnings are informational. Choose deliberately.

## Fixes

- Every fix implements `IFixProvider` (`Editor/Fixes/FixProviderRegistry.cs`) and registers via `RegisterDefaults` or `Register`.
- Every fix must declare a `FixId` and implement `CanFix(issueId)` so the registry can link rules → fixes bidirectionally.
- `Safe: true` fixes are the only ones the gate will auto-suggest. Mark a fix `Safe: false` if it can destroy data or has side effects beyond the target issue.

## Capability catalog sync

- The MCP-side rule catalog (`mcp-server/src/capabilities/rule-catalog.ts`) mirrors the implemented rules and their issue codes/severities. When you add, remove, rename, or change a rule or issue code, update the MCP catalog in the same task so `unity_open_mcp_capabilities` stays accurate.
- Planned-but-unbuilt rules (the `SelectRuleIds` stubs: `materials`, `shader_analysis`, etc.) are listed as `implemented: false` with guidance. Do not remove a planned stub from the catalog unless the rule is implemented or explicitly dropped.

## Verification

- C# changes: add or update the narrowest EditMode test in `Tests/`.
- Rule changes: verify the issue → fix linkage both ways (rule issue `fixIds` ↔ fix `issueCodes`) in tests and via the capability catalog tests (`build-capabilities.test.ts`).
