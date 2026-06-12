using System.Text;
using UnityAgentVerify;

namespace UnityAgentBridge.MetaTools
{
    public static class FindReferencesTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            var guid = JsonBody.GetString(body, "guid");
            var maxResults = JsonBody.GetInt(body, "max_results", 100);

            if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
                return ToolDispatchResult.Fail("missing_parameter",
                    "Either 'asset_path' or 'guid' is required.");

            var input = !string.IsNullOrEmpty(assetPath) ? assetPath : guid;

            FindReferencesResult result;
            try
            {
                result = VerifyGateAdapter.FindReferences(input, maxResults);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("reference_error", e.Message);
            }

            return ToolDispatchResult.Ok(BuildResult(result));
        }

        static string BuildResult(FindReferencesResult result)
        {
            var sb = new StringBuilder(1024);

            sb.Append("{\"queriedAssetPath\":");
            sb.Append(EscapeString(result.QueriedAssetPath));
            sb.Append(",\"queriedAssetGuid\":");
            sb.Append(EscapeString(result.QueriedAssetGuid));

            sb.Append(",\"referencedBy\":[");
            if (result.ReferencedBy != null)
            {
                for (int i = 0; i < result.ReferencedBy.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var entry = result.ReferencedBy[i];
                    sb.Append('{');
                    sb.Append("\"assetPath\":").Append(EscapeString(entry.AssetPath));
                    sb.Append(",\"guid\":").Append(EscapeString(entry.Guid));
                    sb.Append('}');
                }
            }
            sb.Append(']');

            sb.Append(",\"totalCount\":").Append(result.TotalCount);
            sb.Append('}');

            return sb.ToString();
        }

        static string EscapeString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
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
            sb.Append('"');
            return sb.ToString();
        }
    }
}
