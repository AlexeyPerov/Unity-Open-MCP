using System;
using System.Linq;
using System.Text;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Batch;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class CheckpointCreateTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            var label = JsonBody.GetString(body, "label");

            // B24 — an empty/null `paths` is documented as "whole project
            // summary" (mcp-server/src/tools/checkpoint-create.ts), but
            // seven of eight rules bail on a `scope.Paths.Length == 0`
            // guard (only project_health expands it internally), so a
            // literal empty array records an all-zero fingerprint and
            // every later delta then reports `passed:true` regardless of
            // project state.
            //
            // B-N4 — the previous fix expanded the whole-project case to
            // EVERY asset path under Assets/ and ran the FULL rule set from
            // the live Editor. Two rules make that pathological there:
            // MissingReferences loads every asset via AssetDatabase with no
            // extension filter, and ScenePrefabHealth additively opens and
            // closes every .unity in the project — synchronously on the
            // Unity main thread, freezing the Editor and blowing the 30 s
            // dispatch timeout. The expanded scope is stored on the entry,
            // so every subsequent delta repeats it.
            //
            // The honest contract for a live whole-project checkpoint is
            // the cheap whole-project rule only: project_health (orphan
            // .meta, duplicate_guid, invalid_layer — concerns not bound to a
            // file extension, scanned from the file tree without loading
            // assets). Pass an EMPTY scope and the project_health rule id
            // explicitly; project_health is gated to VerifyRunMode.Full so
            // it contributes nothing in checkpoint mode either, but the
            // intent is unambiguous and the fingerprint stays cheap. The
            // per-asset rules (missing_references, scene_prefab_health, …)
            // require an explicit `paths` set — an agent that wants them in
            // a checkpoint must scope to the relevant folders/files.
            //
            // A5 note (preserved): passing ruleIds = null DOES NOT mean "run
            // all rules" — VerifyGateAdapter.CreateCheckpoint rewrites null
            // to SelectRuleIds(paths), which derives rules from extensions
            // and has no arm for project_health. The explicit rule set here
            // avoids that.
            var isWholeProject = paths == null || paths.Length == 0;
            string[] effectivePaths;
            string[] ruleIds;
            if (isWholeProject)
            {
                effectivePaths = System.Array.Empty<string>();
                ruleIds = new[] { "project_health" };
            }
            else
            {
                effectivePaths = paths;
                ruleIds = VerifyGateAdapter.SelectRuleIds(paths);
            }

            CheckpointFingerprint checkpoint;
            try
            {
                checkpoint = VerifyGateAdapter.CreateCheckpoint(effectivePaths, ruleIds);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("checkpoint_error", e.Message);
            }

            var entry = new CheckpointStoreEntry
            {
                CheckpointId = checkpoint.CheckpointId,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Label = label,
                Paths = effectivePaths,
                Categories = checkpoint.Fingerprints?.Keys.ToArray() ?? Array.Empty<string>(),
                Fingerprint = checkpoint
            };
            CheckpointStore.Store(entry);

            return ToolDispatchResult.Ok(BuildResult(entry));
        }

        private static string BuildResult(CheckpointStoreEntry entry)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"checkpointId\":\"").Append(Esc(entry.CheckpointId)).Append("\"");
            sb.Append(",\"timestamp\":\"").Append(Esc(entry.Timestamp)).Append("\"");
            sb.Append(",\"fingerprint\":{");

            var fp = entry.Fingerprint;
            if (fp != null && fp.Fingerprints != null)
            {
                var first = true;
                foreach (var kvp in fp.Fingerprints)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Esc(kvp.Key)).Append("\":{");
                    sb.Append("\"errors\":").Append(kvp.Value.Errors);
                    sb.Append(",\"warnings\":").Append(kvp.Value.Warnings);
                    sb.Append(",\"issueKeys\":[");
                    if (kvp.Value.IssueKeys != null)
                    {
                        var keyFirst = true;
                        foreach (var key in kvp.Value.IssueKeys)
                        {
                            if (!keyFirst) sb.Append(',');
                            keyFirst = false;
                            sb.Append('"').Append(Esc(key)).Append('"');
                        }
                    }
                    sb.Append("]}");
                }
            }

            sb.Append("}}");
            return sb.ToString();
        }

        // Single source of truth for JSON string-content escaping is BridgeJson
        // (T30.5). Returns escaped CONTENT (no surrounding quotes), matching the
        // call sites here; preserves the `null ⇒ ""` contract.
        private static string Esc(string s) => BridgeJson.EscapeStringContent(s);
    }
}
