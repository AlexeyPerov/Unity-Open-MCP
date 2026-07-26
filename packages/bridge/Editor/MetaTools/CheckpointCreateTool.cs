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
            // project state. Expand to the full Assets/ set (the same
            // helper the batch CLI uses) so every registered rule runs and
            // the fingerprint reflects reality. SelectRuleIds(paths) is
            // skipped in the whole-project case so all rules run. The
            // expanded scope is stored on the entry so DeltaTool re-validates
            // the SAME scope (otherwise the delta would re-bail and always
            // pass).
            var isWholeProject = paths == null || paths.Length == 0;
            var effectivePaths = isWholeProject
                ? VerifyBatchEntry.WholeProjectScope()
                : paths;
            var ruleIds = isWholeProject ? null : VerifyGateAdapter.SelectRuleIds(paths);

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
