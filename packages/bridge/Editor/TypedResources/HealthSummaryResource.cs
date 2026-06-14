using System.Text;
using UnityOpenMcpVerify.Cache;

namespace UnityOpenMcpBridge
{
    [BridgeResourceType]
    public class HealthResources
    {
        [BridgeResource(
            "Health summary",
            "unity-open-mcp://health/summary",
            Description = "Cached verify health summary (error/warn/info counts) from the last scan or gate validation")]
        public string HealthSummary()
        {
            var snapshot = VerifyCacheService.GetSnapshot();
            var sb = new StringBuilder(256);
            sb.Append('{');

            if (snapshot.Status == HealthSummaryStatus.Ok && snapshot.Summary != null)
            {
                sb.Append("\"status\":\"ok\",");
                sb.Append("\"asOf\":").Append(Escape(snapshot.AsOf)).Append(',');
                sb.Append("\"summary\":{");
                sb.Append("\"error\":").Append(snapshot.Summary.error).Append(',');
                sb.Append("\"warn\":").Append(snapshot.Summary.warn).Append(',');
                sb.Append("\"info\":").Append(snapshot.Summary.info);
                sb.Append("},");
                sb.Append("\"source\":").Append(Escape(snapshot.Source));
            }
            else
            {
                sb.Append("\"status\":\"no_data\",");
                sb.Append("\"asOf\":null,");
                sb.Append("\"summary\":null,");
                sb.Append("\"nextStep\":\"Run unity_open_mcp_scan_paths or a gated mutation to populate the cache.\"");
            }

            sb.Append('}');
            return sb.ToString();
        }

        static string Escape(string s)
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
                        if (c < 32)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
