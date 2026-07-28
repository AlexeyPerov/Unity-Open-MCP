using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules.MissingReferences
{
    public static class IssueMapper
    {
        public const string CodeMissingFileIDAndGuid = "missing_fileid_and_guid";
        public const string CodeMissingGuid = "missing_guid";
        public const string CodeMissingFileID = "missing_fileid";
        public const string CodeMissingLocalFileID = "missing_local_fileid";
        public const string CodeEmptyLocalRef = "empty_local_ref";
        public const string CodeMissingMethod = "missing_method";
        public const string CodeTypeMismatch = "type_mismatch";
        public const string CodeMissingScript = "missing_script";
        public const string CodeDuplicateComponent = "duplicate_component";
        public const string CodeInvalidLayer = "invalid_layer";

        public static void MapToIssues(List<AssetData> assets, List<VerifyIssue> sink)
        {
            foreach (var asset in assets)
            {
                var refs = asset.RefsData;

                foreach (var extRef in refs.ExternalReferences)
                {
                    if (!extRef.GuidValid)
                        continue;

                    if (!extRef.GuidExistsInAssets)
                    {
                        sink.Add(MakeIssue(asset, CodeMissingGuid + ":" + extRef.Guid,
                            $"Broken PPtr reference: GUID '{extRef.Guid}' does not resolve to a loadable asset at line {extRef.Line}",
                            VerifySeverity.Error,
                            Evidence("guid", extRef.Guid, extRef.Line)));
                    }
                    else if (extRef.FileIDValid && !extRef.FileIDExistsInTargetAsset)
                    {
                        // V8: discriminator = guid + fileID so the gate delta
                        // sees every distinct broken-fileID reference. Without
                        // it, two broken FileIDs on the same asset collapse to
                        // one key and ComputeDelta cannot tell that a second
                        // one was added.
                        sink.Add(MakeIssue(asset, CodeMissingFileID + ":" + extRef.Guid + ":" + extRef.FileID,
                            $"Broken PPtr reference: FileID {extRef.FileID} not found in target asset '{extRef.GuidAssetPath}' (guid {extRef.Guid}) at line {extRef.Line}",
                            VerifySeverity.Error,
                            Evidence("guid", extRef.Guid, extRef.Line,
                                ("fileID", extRef.FileID.ToString()),
                                ("targetAssetPath", extRef.GuidAssetPath))));
                    }
                }

                foreach (var localRef in refs.LocalReferences)
                {
                    if (localRef.IdValid && localRef.LocalUsagesCount == 0 && !localRef.ExistsInAssets)
                    {
                        // C2: discriminator = the dangling local fileID itself
                        // — the most stable field the scanner record carries
                        // (mirrors missing_fileid's guid+fileID choice). The
                        // line is deliberately excluded (B-N13: it shifts on
                        // any unrelated edit above) and stays in evidence.
                        sink.Add(MakeIssue(asset, CodeMissingLocalFileID + ":" + localRef.Id,
                            $"Missing local FileID ({localRef.Id}) at line {localRef.Line}",
                            VerifySeverity.Warning,
                            Evidence("fileID", localRef.Id.ToString(), localRef.Line)));
                    }
                }

                // C2: the scanner record for an empty local ref
                // (EmptyLocalFileIDRegistry) carries ONLY the line — a
                // `{fileID: 0}` has no identifying payload by definition, and
                // the line is unstable (B-N13). The most stable discriminator
                // available is the ORDINAL among the asset's empty refs: it is
                // untouched by unrelated edits (only adding/removing empty
                // refs earlier in the file renumbers, and that IS a change to
                // this issue population), and it makes a count change visible
                // to the gate delta as exactly the added/removed keys — e.g.
                // 2 empty refs → keys :0,:1; a third appears → :2 is the one
                // new key, nothing spuriously "resolved".
                for (var emptyIdx = 0; emptyIdx < refs.EmptyFileIDs.Count; emptyIdx++)
                {
                    var empty = refs.EmptyFileIDs[emptyIdx];
                    sink.Add(MakeIssue(asset, CodeEmptyLocalRef + ":" + emptyIdx,
                        $"Empty local fileID reference at line {empty.Line}",
                        VerifySeverity.Warning,
                        Evidence("line", empty.Line.ToString(), empty.Line)));
                }

                foreach (var method in refs.MissingMethods)
                {
                    // C2: discriminator = class + method (the stable identity
                    // of the broken UnityEvent binding), sanitized like
                    // duplicate_component's user-controlled strings — the
                    // YAML-sourced names could carry '|'. Two listeners
                    // binding the SAME missing class:method intentionally
                    // share one key (the duplicate_component stance); the
                    // line stays in evidence only.
                    sink.Add(MakeIssue(asset,
                        CodeMissingMethod + ":"
                            + IssueKey.SanitizeComponent(method.ClassName) + ":"
                            + IssueKey.SanitizeComponent(method.MethodName),
                        $"Missing method {method.MethodName} on {method.ClassName} at line {method.Line}",
                        VerifySeverity.Warning,
                        Evidence("className", method.ClassName, method.Line,
                            ("methodName", method.MethodName))));
                }

                foreach (var mismatch in refs.TypeMismatches)
                {
                    // C2: discriminator = the unresolvable type name (stable;
                    // sanitized because it is read from asset YAML). Distinct
                    // broken argument types on one asset no longer collapse
                    // to a single key; the line stays in evidence only.
                    sink.Add(MakeIssue(asset,
                        CodeTypeMismatch + ":" + IssueKey.SanitizeComponent(mismatch.TypeName),
                        $"Type mismatch: unresolvable type {mismatch.TypeName} at line {mismatch.Line}",
                        VerifySeverity.Warning,
                        Evidence("typeName", mismatch.TypeName, mismatch.Line)));
                }

                foreach (var script in refs.MissingScripts)
                {
                    // V8: discriminator = scriptGuid so two distinct missing
                    // scripts (different deleted GUIDs) on the same prefab do
                    // not collapse to one key. Same-GUID duplicates on one
                    // asset are intentional (two components referencing the
                    // same deleted script share the issue) — they would be
                    // removed together by remove_missing_script anyway.
                    sink.Add(MakeIssue(asset, CodeMissingScript + ":" + script.ScriptGuid,
                        $"Missing script GUID {script.ScriptGuid} at line {script.Line}",
                        VerifySeverity.Error,
                        Evidence("scriptGuid", script.ScriptGuid, script.Line)));
                }

                foreach (var dup in refs.DuplicateComponents)
                {
                    // V8: discriminator = componentType + gameObject so the
                    // gate delta can see a second duplicate-component type
                    // appear on the same prefab.
                    //
                    // B-N7 — sanitize the user-controlled strings (component
                    // type, GameObject name) before concatenation: Unity
                    // permits '|' in names (`UI|Header`, `Wall|Left`), and a
                    // raw '|' in the discriminator makes IssueKey.Build throw
                    // ArgumentException (uncaught by the gate's
                    // FormatException handler) — aborting baseline_create and
                    // throwing post-mutation in the gate path. The original
                    // values stay in `evidence`/`description`.
                    sink.Add(MakeIssue(asset,
                        CodeDuplicateComponent + ":"
                            + IssueKey.SanitizeComponent(dup.ComponentType) + ":"
                            + IssueKey.SanitizeComponent(dup.GameObjectName),
                        $"Duplicate component {dup.ComponentType} ({dup.Count}x) on '{dup.GameObjectName}'",
                        VerifySeverity.Warning,
                        Evidence("componentType", dup.ComponentType, null,
                            ("count", dup.Count.ToString()),
                            ("gameObject", dup.GameObjectName))));
                }

                foreach (var layer in refs.InvalidLayers)
                {
                    // V8: discriminator = layerIndex so two invalid layer indices
                    // on one asset do not collapse to one key.
                    //
                    // B-N13 — the discriminator previously ALSO carried
                    // `layer.Line` (a line index into the asset YAML). That made
                    // the key deterministic only for identical bytes: any edit
                    // ABOVE the line (adding a component, a Unity reserialize, a
                    // .meta reformat) shifted it, so a pre-existing invalid_layer
                    // surfaced in the gate delta as one resolvedWarnings PLUS one
                    // newWarnings purely from an unrelated change. The line is
                    // still in `evidence` for human inspection; the key now
                    // depends only on the stable layerIndex, matching the
                    // missing_fileid discriminator's stable guid+fileID choice.
                    sink.Add(MakeIssue(asset, CodeInvalidLayer + ":" + layer.LayerIndex,
                        $"Invalid layer index {layer.LayerIndex} at line {layer.Line}",
                        VerifySeverity.Warning,
                        Evidence("layerIndex", layer.LayerIndex.ToString(), layer.Line)));
                }
            }
        }

        private static VerifyIssue MakeIssue(
            AssetData asset, string code, string description,
            VerifySeverity severity,
            IReadOnlyDictionary<string, string> evidence = null)
        {
            return new VerifyIssue("missing_references", severity, asset.Path, code, description, evidence);
        }

        // Evidence builders — keep the per-instance payload small and flat.
        // Each call returns a fresh dictionary so issues never share mutable
        // state. line is folded in as a string when present.
        private static IReadOnlyDictionary<string, string> Evidence(string key, string value, int? line, params (string, string)[] extra)
        {
            var dict = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(key) && value != null)
                dict[key] = value;
            foreach (var (k, v) in extra)
            {
                if (!string.IsNullOrEmpty(k) && v != null)
                    dict[k] = v;
            }
            if (line.HasValue)
                dict["line"] = line.Value.ToString();
            return dict;
        }
    }
}

