using System.Net;
using System.Text;

namespace UnityOpenMcpBridge
{
    // HTTP response writers. The single primitive is SendJson (status + UTF-8
    // JSON body + close); the rest are convenience envelopes for the standard
    // error cases the router produces (tool not found, unknown endpoint,
    // generic { error: {...} }). All responses are application/json; no
    // endpoint serves HTML or plain text.

    internal static class BridgeHttpResponse
    {
        internal static void SendToolNotFound(HttpListenerContext context, string toolName)
        {
            var json = $"{{\"error\":{{\"code\":\"tool_not_found\",\"message\":\"Unknown tool: {BridgeJson.EscapeStringContent(toolName)}\"}}}}";
            SendJson(context, 404, json);
        }

        internal static void SendNotFound(HttpListenerContext context, string path)
        {
            var json = $"{{\"error\":{{\"code\":\"not_found\",\"message\":\"Unknown endpoint: {BridgeJson.EscapeStringContent(path)}\"}}}}";
            SendJson(context, 404, json);
        }

        internal static void SendJsonError(HttpListenerContext context, int statusCode, string code, string message)
        {
            var json = $"{{\"error\":{{\"code\":\"{BridgeJson.EscapeStringContent(code)}\",\"message\":\"{BridgeJson.EscapeStringContent(message)}\"}}}}";
            SendJson(context, statusCode, json);
        }

        internal static void SendJson(HttpListenerContext context, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
