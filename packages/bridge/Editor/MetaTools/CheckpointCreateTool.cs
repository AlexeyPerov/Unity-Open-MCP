using System;
using System.Linq;
using System.Text;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class CheckpointCreateTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var paths = JsonBody.GetStringArray(body, "paths");
            var label = JsonBody.GetString(body, "label");

            CheckpointFingerprint checkpoint;
            try
            {
                var ruleIds = paths != null && paths.Length > 0
                    ? VerifyGateAdapter.SelectRuleIds(paths)
                    : null;
                checkpoint = VerifyGateAdapter.CreateCheckpoint(
                    paths ?? Array.Empty<string>(), ruleIds);
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
                Paths = paths,
                Categories = checkpoint.Fingerprints?.Keys.ToArray() ?? Array.Empty<string>(),
                Fingerprint = checkpoint
            };
            CheckpointStore.Store(entry);

            return ToolDispatchResult.Ok(BuildResult(entry));
        }

        static string BuildResult(CheckpointStoreEntry entry)
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

        static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
