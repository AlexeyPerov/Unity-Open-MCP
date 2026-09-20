using System.Text.RegularExpressions;
using UnityOpenMcpBridge.MetaTools;

namespace UnityOpenMcpBridge
{
    // Request-level classification. Catalog flags remain conservative defaults.
    internal static class EffectiveToolContract
    {
        private static readonly Regex DisruptiveSnippet = new Regex(
            @"\b(RequestScriptCompilation|Refresh|ImportAsset|OpenScene|NewScene|CloseScene|LoadScene|LoadSceneAsync|UnloadSceneAsync|EnterPlaymode|ExitPlaymode|ExecuteMenuItem|BuildPlayer|SwitchActiveBuildTarget|SetScriptingDefineSymbols|RequestReload|ReloadAssembly|LockReloadAssemblies|UnlockReloadAssemblies)\b|\bisPlaying\s*=(?!=)",
            RegexOptions.CultureInvariant);

        internal static bool IsReadOnlySnippet(string body) =>
            JsonBody.GetBool(JsonBody.TopLevelField(body, "read_only"), "read_only")
            && !JsonBody.GetBool(JsonBody.TopLevelField(body, "setup_roslyn"), "setup_roslyn")
            && !DisruptiveSnippet.IsMatch(JsonBody.GetString(JsonBody.TopLevelField(body, "code"), "code") ?? "");

        internal static bool IsMutating(string tool, string body)
        {
            if (tool == ProjectCommandInvocation.ToolName)
                return !ProjectCommandInvocation.Resolve(body, out var command) || command.Attribute.IsMutating;
            if (tool == "unity_open_mcp_execute_csharp" && IsReadOnlySnippet(body)) return false;
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsReadOnlyMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path"))) return false;
            if (tool == "unity_open_mcp_apply_fix" && JsonBody.GetBool(JsonBody.TopLevelField(body, "dry_run"), "dry_run", true)) return false;
            if (tool == "unity_open_mcp_batch_execute")
                return BatchExecuteTool.Preflight(body, out var mutating) != null || mutating;
            return BridgeToolClassification.MutatingTools.Contains(tool)
                || (BridgeToolRegistry.TryGet(tool, out var entry) && entry.IsMutating);
        }

        internal static LifecyclePolicy Lifecycle(string tool, string body)
        {
            if (tool == ProjectCommandInvocation.ToolName)
                return ProjectCommandInvocation.Resolve(body, out var command) ? command.Attribute.Lifecycle : LifecyclePolicy.None;
            if (tool == "unity_open_mcp_execute_csharp" && IsReadOnlySnippet(body)) return LifecyclePolicy.None;
            if (tool == "unity_open_mcp_execute_menu" && ExecuteMenuTool.IsNonDisruptiveReadOnlyMenu(
                JsonBody.GetString(JsonBody.TopLevelField(body, "menu_path"), "menu_path"))) return LifecyclePolicy.None;
            if (tool == "unity_open_mcp_batch_execute" && !IsMutating(tool, body)) return LifecyclePolicy.None;
            return ToolLifecycle.Resolve(tool);
        }
    }
}
