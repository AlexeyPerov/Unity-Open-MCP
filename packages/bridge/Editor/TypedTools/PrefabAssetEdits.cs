using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityOpenMcpBridge.TypedTools
{
    /// <summary>What one load / edit / save / unload cycle of an in_prefab
    /// edit did: whether the asset was written, and why not.</summary>
    internal sealed class PrefabAssetEditOutcome
    {
        internal string AssetPath;

        /// <summary>A mutating tool succeeded on the loaded contents.</summary>
        internal bool Mutating;

        internal bool Saved;

        /// <summary>A failed or refused step dropped the run's unsaved edits.</summary>
        internal bool Discarded;

        /// <summary>Why the asset was not written (a step failed, nothing but
        /// reads ran, the save failed); null when it was.</summary>
        internal string Reason;

        /// <summary>prefab_save_failed when the save itself failed.</summary>
        internal string ErrorCode;

        /// <summary>The batch steps that shared this cycle; null for a single call.</summary>
        internal List<int> Steps;

        /// <summary>",variantOf…,overrides…" members of the edit, or "".</summary>
        internal string Detail = "";

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"path\":").Append(BridgeJson.EscapeString(AssetPath));
            if (Steps != null)
            {
                sb.Append(",\"steps\":[");
                for (int i = 0; i < Steps.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Steps[i]);
                }
                sb.Append(']');
            }
            sb.Append(",\"saved\":").Append(Saved ? "true" : "false");
            if (Reason != null) sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(Reason));
            if (ErrorCode != null) sb.Append(",\"errorCode\":").Append(BridgeJson.EscapeString(ErrorCode));
            sb.Append(Detail);
            if (Mutating) sb.Append(",\"undo\":false");
            sb.Append('}');
        }
    }

    /// <summary>One prefab asset's contents, loaded with
    /// PrefabUtility.LoadPrefabContents into a preview scene of their own.</summary>
    internal sealed class PrefabAssetEdit
    {
        private readonly List<string> _overrideInstances = new List<string>();
        private readonly StringBuilder _overrides = new StringBuilder();

        private PrefabAssetEdit(string assetPath, GameObject root, string variantOf)
        {
            AssetPath = assetPath;
            Root = root;
            VariantOf = variantOf;
            Container = TargetContainer.ForPrefabAsset(assetPath, root);
        }

        internal string AssetPath { get; }
        internal GameObject Root { get; private set; }
        internal TargetContainer Container { get; }

        /// <summary>The base prefab's path when the asset is a variant.</summary>
        internal string VariantOf { get; }

        /// <summary>A mutating tool succeeded on the contents since the load.</summary>
        internal bool Mutated;

        internal static PrefabAssetEdit Load(PrefabAssetEdits.Target target, out ToolDispatchResult failure)
        {
            failure = null;
            GameObject root;
            try
            {
                root = PrefabUtility.LoadPrefabContents(target.AssetPath);
            }
            catch (Exception e)
            {
                failure = ToolDispatchResult.Fail(PrefabAssetEdits.NotEditable,
                    $"Could not load the contents of '{target.AssetPath}': {e.Message}");
                return null;
            }
            if (root == null)
            {
                failure = ToolDispatchResult.Fail(PrefabAssetEdits.NotEditable,
                    $"PrefabUtility.LoadPrefabContents returned nothing for '{target.AssetPath}'.");
                return null;
            }
            return new PrefabAssetEdit(target.AssetPath, root, target.VariantOf);
        }

        /// <summary>Records that a call addressed <paramref name="go"/>: when
        /// it belongs to a prefab instance inside the contents (the variant's
        /// base, or a nested prefab), the edit is stored as an override on
        /// that instance in this asset, and the result says so.</summary>
        internal void NoteTarget(GameObject go)
        {
            if (go == null || Root == null || !PrefabUtility.IsPartOfPrefabInstance(go)) return;
            var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (outer == null) return;
            var instancePath = TypedTargets.HierarchyPath(outer);
            if (_overrideInstances.Contains(instancePath)) return;
            _overrideInstances.Add(instancePath);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(outer);
            var sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : "";
            if (_overrides.Length > 0) _overrides.Append(',');
            _overrides.Append("{\"instancePath\":").Append(BridgeJson.EscapeString(instancePath));
            _overrides.Append(",\"source\":").Append(BridgeJson.EscapeString(sourcePath));
            _overrides.Append(",\"kind\":\"").Append(outer == Root ? "variant_base" : "nested_prefab").Append("\"}");
        }

        /// <summary>",variantOf":…,"overrides":[…]" — which asset the edit
        /// lands in and the prefab instances it overrides there ("" when
        /// neither applies).</summary>
        internal string Detail()
        {
            if (VariantOf == null && _overrides.Length == 0) return "";
            var sb = new StringBuilder(96 + _overrides.Length);
            if (VariantOf != null) sb.Append(",\"variantOf\":").Append(BridgeJson.EscapeString(VariantOf));
            if (_overrides.Length > 0) sb.Append(",\"overrides\":[").Append(_overrides).Append(']');
            return sb.ToString();
        }

        /// <summary>The "prefabAsset" result member: what was edited and how.</summary>
        internal void AppendBlock(StringBuilder sb, PrefabAssetEditOutcome outcome)
        {
            sb.Append("{\"path\":").Append(BridgeJson.EscapeString(AssetPath));
            if (outcome.Mutating || outcome.ErrorCode != null)
                sb.Append(",\"saved\":").Append(outcome.Saved ? "true" : "false");
            sb.Append(Detail());
            // The contents were unloaded when the call returned: their ids
            // resolve to nothing, and Unity dropped the edit's undo records.
            sb.Append(",\"instanceIds\":\"ephemeral\"");
            if (outcome.Mutating) sb.Append(",\"undo\":false");
            sb.Append('}');
        }

        internal bool Save(out string error)
        {
            error = null;
            try
            {
                PrefabUtility.SaveAsPrefabAsset(Root, AssetPath, out bool ok);
                if (!ok) error = $"PrefabUtility.SaveAsPrefabAsset could not write '{AssetPath}' (see logs).";
                return ok;
            }
            catch (Exception e)
            {
                error = $"Saving '{AssetPath}' failed: {e.Message}";
                return false;
            }
        }

        /// <summary>Releases the contents (always, saved or not). Unity drops
        /// the undo records of the preview scene with it.</summary>
        internal void Unload()
        {
            var root = Root;
            Root = null;
            if (root != null)
            {
                PrefabUtility.UnloadPrefabContents(root);
                return;
            }
            var scene = Container.Scene;
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    /// <summary>
    /// Typed GameObject tools edit a prefab asset in place when the call names
    /// it with in_prefab: the bridge loads the asset's contents, runs the tool
    /// against them (paths from the prefab root; the contents are the only
    /// container the call reaches), saves the asset when a mutating tool
    /// succeeded, and always unloads. A failed or refused call writes nothing.
    /// No prefab stage is opened. The edit is not on the Editor undo stack,
    /// and the loaded objects' instance ids die with the contents.
    ///
    /// In a batch, consecutive steps that name the same in_prefab share one
    /// load: the asset is saved once, when the next step names something else
    /// or the batch ends. A step that fails discards the unsaved edits of its
    /// run; the next step on that prefab loads it again from disk.
    /// </summary>
    internal static class PrefabAssetEdits
    {
        /// <summary>The request key naming the prefab asset to edit.</summary>
        internal const string Key = "in_prefab";

        internal const string NotFound = "prefab_asset_not_found";
        internal const string NotEditable = "prefab_asset_not_editable";
        internal const string StageUnsaved = "prefab_stage_unsaved";
        internal const string SaveFailed = "prefab_save_failed";

        /// <summary>The prefab asset loaded for the tool running now, or null.</summary>
        internal static PrefabAssetEdit Current { get; private set; }

        private static BatchEdits s_Batch;

        internal struct Target
        {
            public string AssetPath;
            public string VariantOf;
        }

        internal static bool SamePath(string a, string b) =>
            a != null && b != null &&
            string.Equals(TargetContainer.Normalize(a), TargetContainer.Normalize(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>The in_prefab the body names (top level only), or null.</summary>
        internal static string RequestedPath(string body)
        {
            if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{")) return null;
            var raw = JsonBody.GetString(JsonBody.TopLevelField(body, Key), Key);
            return string.IsNullOrWhiteSpace(raw) ? null : TargetContainer.Normalize(raw);
        }

        // ------------------------------------------------------------------
        // Dispatch.
        // ------------------------------------------------------------------

        /// <summary>Runs one tool call: unchanged when the body names no
        /// in_prefab; otherwise load → tool → save (mutating tool succeeded)
        /// → unload, or, inside a batch, the batch's shared load.</summary>
        internal static ToolDispatchResult Dispatch(string tool, string body, Func<ToolDispatchResult> run)
        {
            var path = RequestedPath(body);
            if (path == null) return run();
            if (s_Batch != null) return s_Batch.Run(tool, body, path, run);

            var refusal = StepRefusal(tool, body, path);
            if (refusal != null) return refusal;
            refusal = Preflight(path, out var target);
            if (refusal != null) return refusal;
            var edit = PrefabAssetEdit.Load(target, out var loadFailure);
            if (edit == null) return loadFailure;

            var mutating = EffectiveToolContract.IsMutating(tool, body);
            var outcome = new PrefabAssetEditOutcome { AssetPath = edit.AssetPath };
            ToolDispatchResult result;
            try
            {
                Current = edit;
                try
                {
                    result = run();
                }
                finally
                {
                    Current = null;
                }

                if (result.Success && mutating)
                {
                    outcome.Mutating = true;
                    if (edit.Save(out var saveError))
                    {
                        outcome.Saved = true;
                    }
                    else
                    {
                        outcome.Reason = saveError;
                        outcome.ErrorCode = SaveFailed;
                        result = ToolDispatchResult.Fail(SaveFailed,
                            saveError + " The call's edit was discarded; the asset on disk is unchanged.");
                    }
                }
                else if (!result.Success)
                {
                    outcome.Reason = "the call failed";
                }
                result = Decorate(result, edit, outcome, mutating);
            }
            finally
            {
                edit.Unload();
            }
            if (mutating) TargetScope.NotePrefabAsset(outcome);
            return result;
        }

        // A success carries "prefabAsset" next to the tool's own output; a
        // failed mutation says in its error detail that nothing was written.
        private static ToolDispatchResult Decorate(ToolDispatchResult result, PrefabAssetEdit edit,
            PrefabAssetEditOutcome outcome, bool mutating)
        {
            if (result.Success)
            {
                var block = new StringBuilder(160);
                edit.AppendBlock(block, outcome);
                return ToolDispatchResult.Ok(SpliceMember(result.Output, "prefabAsset", block.ToString()));
            }
            if (!mutating) return result;
            var detail = "{\"prefabAsset\":{\"path\":" + BridgeJson.EscapeString(edit.AssetPath) + ",\"saved\":false}}";
            return ToolDispatchResult.FailWithDetail(result.ErrorCode, result.ErrorMessage,
                MergeObjects(result.ErrorDetailJson, detail), result.NextSteps);
        }

        // ------------------------------------------------------------------
        // Preflight.
        // ------------------------------------------------------------------

        // What a call must get right before anything is loaded: a tool that
        // declares in_prefab, and in_prefab as its only container.
        private static ToolDispatchResult StepRefusal(string tool, string body, string path)
        {
            if (!DeclaresKey(tool))
                return ToolDispatchResult.Fail(ResolveFailure.InPrefabUnsupported,
                    $"{tool} does not edit a prefab asset in place (it takes no in_prefab). Open the prefab with " +
                    "unity_open_mcp_prefab_open and address the stage object instead.");
            var scenePath = JsonBody.GetString(JsonBody.TopLevelField(body, "scene_path"), "scene_path");
            var sceneName = JsonBody.GetString(JsonBody.TopLevelField(body, "scene_name"), "scene_name");
            if (!string.IsNullOrWhiteSpace(scenePath) || !string.IsNullOrWhiteSpace(sceneName))
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"in_prefab '{path}' and scene_path / scene_name name two containers; pass one. in_prefab edits the " +
                    "prefab asset in place; scene_path names a loaded scene or the open prefab stage.");
            return null;
        }

        private static bool DeclaresKey(string tool)
        {
            if (!BridgeBatchSchemas.ByTool.TryGetValue(tool, out var schema)) return false;
            var properties = JsonBody.GetTopLevelRawValue(schema, "properties");
            return properties != null && JsonBody.GetTopLevelRawValue(properties, Key) != null;
        }

        /// <summary>Whether the asset can be loaded and written now: an
        /// editable prefab (not a model, not in a read-only package), not open
        /// in a prefab stage with unsaved changes. The stage of the same prefab
        /// without unsaved changes is fine: Unity reloads it from the saved
        /// file on a later Editor update.</summary>
        internal static ToolDispatchResult Preflight(string path, out Target target)
        {
            target = default;
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                return ToolDispatchResult.Fail(NotFound,
                    $"in_prefab must name a .prefab asset; got '{path}'. Use scene_path for a scene.");
            var asset = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
            if (asset == null)
                return ToolDispatchResult.FailWithDetail(NotFound, $"No prefab asset at '{path}'.", null,
                    new[] { "Find the prefab with unity_open_mcp_search_assets {\"type\":\"Prefab\",\"name\":\"…\"} and pass its asset path." });
            var assetPath = AssetDatabase.GetAssetPath(asset);
            var type = PrefabUtility.GetPrefabAssetType(asset);
            if (type == PrefabAssetType.Model)
                return ToolDispatchResult.Fail(NotEditable,
                    $"'{assetPath}' is a model prefab, imported from its model file; it cannot be saved as a prefab. " +
                    "Edit a prefab variant of it instead.");
            if (type != PrefabAssetType.Regular && type != PrefabAssetType.Variant)
                return ToolDispatchResult.Fail(NotEditable, $"'{assetPath}' is not an editable prefab asset ({type}).");
            if (PrefabUtility.IsPartOfImmutablePrefab(asset))
                return ToolDispatchResult.Fail(NotEditable,
                    $"'{assetPath}' is read-only (an immutable package asset); it cannot be saved.");

            var stage = TargetContainers.OpenStage();
            if (stage != null && SamePath(stage.assetPath, assetPath) && stage.scene.isDirty)
                return ToolDispatchResult.FailWithDetail(StageUnsaved,
                    $"'{assetPath}' is open in a prefab stage with unsaved changes. Editing the asset in place now would " +
                    "set the stage and the file apart, and a later stage save would overwrite this edit.",
                    "{\"assetPath\":" + BridgeJson.EscapeString(assetPath) + "}",
                    new[]
                    {
                        "Save the stage first (unity_open_mcp_prefab_save, or unity_open_mcp_prefab_close {\"save\":true}), " +
                        "or discard it (unity_open_mcp_prefab_close {\"save\":false}), then retry with in_prefab.",
                        "Or edit through the stage: pass scene_path: \"" + assetPath + "\" instead of in_prefab (the edit " +
                        "then stays in the stage until it is saved).",
                    });

            string variantOf = null;
            if (type == PrefabAssetType.Variant)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(asset);
                if (source != null) variantOf = AssetDatabase.GetAssetPath(source);
            }
            target = new Target { AssetPath = assetPath, VariantOf = variantOf };
            return null;
        }

        // ------------------------------------------------------------------
        // Gate scope and next steps.
        // ------------------------------------------------------------------

        /// <summary>The gate's scope for a call: paths_hint plus every
        /// in_prefab it does not already cover, so the gate validates the
        /// asset the call writes. Unchanged without a paths_hint (the caller
        /// refuses such a mutation) or an in_prefab.</summary>
        internal static string[] GateScope(string[] pathsHint, IEnumerable<string> bodies)
        {
            if (pathsHint == null || pathsHint.Length == 0 || bodies == null) return pathsHint;
            List<string> extra = null;
            foreach (var body in bodies)
            {
                var path = RequestedPath(body);
                if (path == null || Covers(pathsHint, path)) continue;
                extra ??= new List<string>();
                bool listed = false;
                foreach (var e in extra) if (SamePath(e, path)) { listed = true; break; }
                if (!listed) extra.Add(path);
            }
            if (extra == null) return pathsHint;
            var scope = new string[pathsHint.Length + extra.Count];
            Array.Copy(pathsHint, scope, pathsHint.Length);
            for (int i = 0; i < extra.Count; i++) scope[pathsHint.Length + i] = extra[i];
            return scope;
        }

        private static bool Covers(string[] hints, string path)
        {
            var own = TargetContainer.Normalize(path);
            foreach (var raw in hints)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var hint = TargetContainer.Normalize(raw);
                if (string.Equals(hint, own, StringComparison.OrdinalIgnoreCase)) return true;
                if (own.StartsWith(hint + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The next step after a call that edited prefab assets in
        /// place: what was written, that undo does not cover it, and that the
        /// result's instance ids are gone. Null when none was edited.</summary>
        internal static string Advisory(IReadOnlyList<PrefabAssetEditOutcome> outcomes)
        {
            if (outcomes == null || outcomes.Count == 0) return null;
            var saved = new List<string>();
            var unsaved = new List<string>();
            foreach (var o in outcomes)
            {
                // A run of reads wrote nothing and lost nothing.
                if (!o.Saved && o.ErrorCode == null && !(o.Discarded && o.Mutating)) continue;
                var list = o.Saved ? saved : unsaved;
                var name = "'" + o.AssetPath + "'";
                if (!list.Contains(name)) list.Add(name);
            }
            if (saved.Count == 0 && unsaved.Count == 0) return null;
            var sb = new StringBuilder(320);
            if (saved.Count > 0)
                sb.Append("Saved the prefab asset ").Append(string.Join(", ", saved))
                  .Append(" in place (loaded, edited and unloaded within this call, no stage). Editor undo does not cover ")
                  .Append("it: revert with another edit. Instance ids the result reports for its objects no longer resolve; ")
                  .Append("address them with in_prefab and their path from the prefab root.");
            if (unsaved.Count > 0)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append("Nothing was written to ").Append(string.Join(", ", unsaved))
                  .Append(" where an edit was discarded (see prefabAssets / prefabAsset).");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Batch.
        // ------------------------------------------------------------------

        /// <summary>Opens the batch scope: until it is disposed, steps that
        /// name in_prefab share loads (see <see cref="BatchEdits"/>).</summary>
        internal static BatchEdits BeginBatch() => s_Batch = new BatchEdits(s_Batch);

        internal sealed class BatchEdits : IDisposable
        {
            private readonly BatchEdits _outer;
            private PrefabAssetEdit _open;
            private PrefabAssetEditOutcome _openOutcome;
            private int _step = -1;
            private bool _disposed;

            internal BatchEdits(BatchEdits outer)
            {
                _outer = outer;
            }

            /// <summary>Every load / save cycle the batch ran, in order.</summary>
            internal readonly List<PrefabAssetEditOutcome> Outcomes = new List<PrefabAssetEditOutcome>();

            /// <summary>Called before step <paramref name="index"/> runs: the
            /// open prefab is saved and unloaded unless the step names it too.</summary>
            internal void BeforeStep(int index, string stepBody)
            {
                _step = index;
                if (_open == null) return;
                var path = RequestedPath(stepBody);
                if (path != null && SamePath(path, _open.AssetPath)) return;
                Close();
            }

            /// <summary>Saves (when a step changed it) and unloads the open
            /// prefab; returns every cycle's outcome.</summary>
            internal List<PrefabAssetEditOutcome> Finish()
            {
                Close();
                return Outcomes;
            }

            internal ToolDispatchResult Run(string tool, string body, string path, Func<ToolDispatchResult> run)
            {
                var refusal = StepRefusal(tool, body, path);
                if (_open != null && !SamePath(_open.AssetPath, path)) Close();
                if (refusal != null)
                {
                    if (_open != null) Discard($"step {_step} ({Short(tool)}) was refused ({refusal.ErrorCode})");
                    return refusal;
                }
                if (_open == null)
                {
                    refusal = Preflight(path, out var target);
                    if (refusal != null) return refusal;
                    _open = PrefabAssetEdit.Load(target, out var loadFailure);
                    if (_open == null) return loadFailure;
                    _openOutcome = new PrefabAssetEditOutcome { AssetPath = _open.AssetPath, Steps = new List<int>() };
                }
                _openOutcome.Steps.Add(_step);

                var mutating = EffectiveToolContract.IsMutating(tool, body);
                ToolDispatchResult result;
                Current = _open;
                try
                {
                    result = run();
                }
                catch
                {
                    Current = null;
                    Discard($"step {_step} ({Short(tool)}) threw");
                    throw;
                }
                Current = null;

                if (!result.Success)
                {
                    Discard($"step {_step} ({Short(tool)}) failed ({result.ErrorCode})");
                    return result;
                }
                if (mutating)
                {
                    _open.Mutated = true;
                    _openOutcome.Mutating = true;
                }
                // The save comes after the run (batch.prefabAssets[] reports it).
                var block = new StringBuilder(160);
                block.Append("{\"path\":").Append(BridgeJson.EscapeString(_open.AssetPath));
                if (mutating) block.Append(",\"save\":\"after_run\"");
                block.Append(",\"instanceIds\":\"ephemeral\"}");
                return ToolDispatchResult.Ok(SpliceMember(result.Output, "prefabAsset", block.ToString()));
            }

            // Save (when a step changed it) and unload the open prefab.
            private void Close()
            {
                if (_open == null) return;
                var edit = _open;
                var outcome = _openOutcome;
                _open = null;
                _openOutcome = null;
                outcome.Detail = edit.Detail();
                try
                {
                    if (!edit.Mutated)
                    {
                        outcome.Reason = "no step changed it";
                    }
                    else if (edit.Save(out var error))
                    {
                        outcome.Saved = true;
                    }
                    else
                    {
                        outcome.Reason = error;
                        outcome.ErrorCode = SaveFailed;
                    }
                }
                finally
                {
                    edit.Unload();
                    Outcomes.Add(outcome);
                    TargetScope.NotePrefabAsset(outcome);
                }
            }

            // Unload the open prefab without saving it.
            private void Discard(string reason)
            {
                if (_open == null) return;
                var edit = _open;
                var outcome = _openOutcome;
                _open = null;
                _openOutcome = null;
                outcome.Detail = edit.Detail();
                outcome.Discarded = true;
                outcome.Reason = reason + "; nothing of this run was written";
                try
                {
                    edit.Unload();
                }
                finally
                {
                    Outcomes.Add(outcome);
                    TargetScope.NotePrefabAsset(outcome);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    Discard("the batch stopped before the prefab was saved");
                }
                finally
                {
                    Current = null;
                    s_Batch = _outer;
                }
            }

            private static string Short(string tool) =>
                tool != null && tool.StartsWith("unity_open_mcp_") ? tool.Substring("unity_open_mcp_".Length) : tool;
        }

        // ------------------------------------------------------------------
        // JSON helpers.
        // ------------------------------------------------------------------

        // Adds "key":value to a JSON object output; an output that is not an
        // object (or none) becomes {"key":value}.
        private static string SpliceMember(string output, string key, string valueJson)
        {
            var member = BridgeJson.EscapeString(key) + ":" + valueJson;
            var trimmed = output?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                return "{" + member + "}";
            var inner = trimmed.Substring(1, trimmed.Length - 2).Trim();
            return "{" + inner + (inner.Length > 0 ? "," : "") + member + "}";
        }

        // The members of two JSON objects in one object (callers pass
        // disjoint keys).
        private static string MergeObjects(string first, string second)
        {
            var a = first?.Trim();
            if (string.IsNullOrEmpty(a) || a.Length < 2 || a[0] != '{') return second;
            var innerA = a.Substring(1, a.Length - 2).Trim();
            var innerB = second.Trim().Substring(1, second.Trim().Length - 2).Trim();
            if (innerA.Length == 0) return second;
            return "{" + innerA + (innerB.Length > 0 ? "," + innerB : "") + "}";
        }
    }
}
