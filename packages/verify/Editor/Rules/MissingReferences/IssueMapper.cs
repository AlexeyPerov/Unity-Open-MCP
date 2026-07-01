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
                        sink.Add(MakeIssue(asset, CodeMissingGuid,
                            $"Broken PPtr reference: GUID '{extRef.Guid}' does not resolve to a loadable asset at line {extRef.Line}",
                            VerifySeverity.Error,
                            Evidence("guid", extRef.Guid, extRef.Line)));
                    }
                    else if (extRef.FileIDValid && !extRef.FileIDExistsInTargetAsset)
                    {
                        sink.Add(MakeIssue(asset, CodeMissingFileID,
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
                        sink.Add(MakeIssue(asset, CodeMissingLocalFileID,
                            $"Missing local FileID ({localRef.Id}) at line {localRef.Line}",
                            VerifySeverity.Warning,
                            Evidence("fileID", localRef.Id.ToString(), localRef.Line)));
                    }
                }

                foreach (var empty in refs.EmptyFileIDs)
                {
                    sink.Add(MakeIssue(asset, CodeEmptyLocalRef,
                        $"Empty local fileID reference at line {empty.Line}",
                        VerifySeverity.Warning,
                        Evidence("line", empty.Line.ToString(), empty.Line)));
                }

                foreach (var method in refs.MissingMethods)
                {
                    sink.Add(MakeIssue(asset, CodeMissingMethod,
                        $"Missing method {method.MethodName} on {method.ClassName} at line {method.Line}",
                        VerifySeverity.Warning,
                        Evidence("className", method.ClassName, method.Line,
                            ("methodName", method.MethodName))));
                }

                foreach (var mismatch in refs.TypeMismatches)
                {
                    sink.Add(MakeIssue(asset, CodeTypeMismatch,
                        $"Type mismatch: unresolvable type {mismatch.TypeName} at line {mismatch.Line}",
                        VerifySeverity.Warning,
                        Evidence("typeName", mismatch.TypeName, mismatch.Line)));
                }

                foreach (var script in refs.MissingScripts)
                {
                    sink.Add(MakeIssue(asset, CodeMissingScript,
                        $"Missing script GUID {script.ScriptGuid} at line {script.Line}",
                        VerifySeverity.Error,
                        Evidence("scriptGuid", script.ScriptGuid, script.Line)));
                }

                foreach (var dup in refs.DuplicateComponents)
                {
                    sink.Add(MakeIssue(asset, CodeDuplicateComponent,
                        $"Duplicate component {dup.ComponentType} ({dup.Count}x) on '{dup.GameObjectName}'",
                        VerifySeverity.Warning,
                        Evidence("componentType", dup.ComponentType, null,
                            ("count", dup.Count.ToString()),
                            ("gameObject", dup.GameObjectName))));
                }

                foreach (var layer in refs.InvalidLayers)
                {
                    sink.Add(MakeIssue(asset, CodeInvalidLayer,
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

