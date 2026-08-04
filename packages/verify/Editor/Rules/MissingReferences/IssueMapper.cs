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

                // feedback-fable-31-07 §7 + feedback-04-08-opus §4/§5/§7 — key
                // each empty-local-ref issue by the most content-addressed
                // identity available, in priority order:
                //   1. transform PATH + property (§4) — survives a prefab rebuild
                //      that renumbers every anchor. This is what makes a delta
                //      mean something: two consecutive idempotent builds of the
                //      same prefab now produce STABLE keys instead of 729/729
                //      churn. Resolved by the scanner's EnrichEmptyRefs post-pass.
                //   2. anchor + property (fable §7 fallback) — when a transform
                //      path could not be resolved (the owning GameObject or its
                //      transform chain is missing from the anchor metadata).
                //   3. line (last resort) — malformed/legacy YAML where neither
                //      anchor nor property could be determined.
                //
                // §7 — array-element properties: a `{fileID: 0}` inside a YAML
                // list (`  - m_Target: {fileID: 0}`) previously keyed as
                // `:<property>#<dupOrdinal>`, colliding with the dedup suffix and
                // producing unreadable `:#1`..`:#N` keys. The dedup ordinal stays
                // for genuine same-path+property duplicates, but the property
                // segment is normalized: never empty, and array indices ride on
                // the property as `[N]` when the scanner recorded them.
                //
                // §5 — severity is split by field OWNER via EmptyRefClassifier:
                // a user-script field (m_Script guid under Assets/) stays a
                // Warning; a known-optional built-in field (m_SelectOn*, sprite
                // swaps, TMP material/style) is demoted to Info so the ~98 % noise
                // floor stops burying the real bugs.
                var emptyKeyCounts = new Dictionary<string, int>();
                foreach (var empty in refs.EmptyFileIDs)
                {
                    var severity = EmptyRefClassifier.Classify(empty);
                    var prop = NormalizeEmptyRefProperty(empty.Property);
                    var hasProp = !string.IsNullOrEmpty(prop);

                    string identity;
                    string identityLabel;
                    if (!string.IsNullOrEmpty(empty.TransformPath))
                    {
                        // §4 — transform path + property is the primary key.
                        identity = empty.TransformPath + ":" + (hasProp ? IssueKey.SanitizeComponent(prop) : "");
                        identityLabel = $"path '{empty.TransformPath}', property '{prop}'";
                    }
                    else
                    {
                        var hasAnchor = empty.Anchor != 0;
                        if (hasAnchor || hasProp)
                        {
                            identity = (hasAnchor ? empty.Anchor.ToString() : "0") + ":"
                                 + (hasProp ? IssueKey.SanitizeComponent(prop) : "");
                            identityLabel = $"anchor {empty.Anchor}, property '{prop}'";
                        }
                        else
                        {
                            identity = "line:" + empty.Line;
                            identityLabel = null;
                        }
                    }

                    // De-duplicate identical identities with a stable 1-based
                    // suffix (the common case has no collision, so most keys are
                    // suffix-free). This handles two empty refs on the SAME
                    // path+property (same property nulled twice on one object).
                    if (!emptyKeyCounts.TryGetValue(identity, out var dup)) dup = 0;
                    emptyKeyCounts[identity] = dup + 1;
                    var key = dup == 0 ? identity : identity + "#" + dup;

                    sink.Add(MakeIssue(asset, CodeEmptyLocalRef + ":" + key,
                        $"Empty local fileID reference at line {empty.Line}"
                        + (identityLabel != null ? $" ({identityLabel})" : ""),
                        severity,
                        Evidence("line", empty.Line.ToString(), empty.Line,
                            ("anchor", empty.Anchor.ToString()),
                            ("property", empty.Property ?? ""),
                            ("transformPath", empty.TransformPath ?? ""),
                            ("ownerScriptGuid", empty.OwnerScriptGuid ?? ""))));
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

        // feedback-04-08-opus §7 — normalize an empty-ref property for use as a
        // key segment. Returns the property verbatim when it is a real field
        // name, "" when the scanner could not determine one (the caller then
        // falls back to the line). Never emits a bare array index as the whole
        // segment: Unity serializes list elements as `- <key>: {fileID: 0}` where
        // <key> is the real field (e.g. `m_Target`); the PropertyKeyBeforeFileId
        // regex already captures that key. When the recorded property looks like
        // a bare index (`#N` or a pure integer), it is folded into the previous
        // segment's notation as `[N]` — but since we key on a single property
        // here, a bare-index property collapses to "" so the path/anchor
        // identity still uniquely identifies the site via the dedup suffix.
        private static string NormalizeEmptyRefProperty(string property)
        {
            if (string.IsNullOrEmpty(property)) return "";
            var p = property.Trim();
            if (p.Length == 0) return "";
            // A bare array index ("#3", "3") is not a readable property; emit
            // empty so the dedup ordinal carries disambiguation instead of a
            // garbage segment like ":#3". Real Unity field names never start
            // with '#' and never consist solely of digits.
            if (p[0] == '#') return "";
            if (IsDigitsOnly(p)) return "";
            return p;
        }

        private static bool IsDigitsOnly(string s)
        {
            foreach (var c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return s.Length > 0;
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

