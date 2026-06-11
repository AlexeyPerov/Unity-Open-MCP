using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityAgentBridge.MetaTools
{
    public static class ExecuteMenuTool
    {
        static readonly HashSet<string> BlockedMenus = new(StringComparer.OrdinalIgnoreCase)
        {
            "File/Quit"
        };

        public static ToolDispatchResult Execute(string body)
        {
            var menuPath = JsonBody.GetString(body, "menu_path");
            if (string.IsNullOrEmpty(menuPath))
                return ToolDispatchResult.Fail("validation_error",
                    "Field 'menu_path' is required and must be non-empty. " +
                    "Example: 'Assets/Refresh', 'File/Save Project'.");

            if (IsBlocked(menuPath))
                return ToolDispatchResult.Fail("menu_blocked",
                    $"Menu '{menuPath}' is blocked for safety reasons. " +
                    "Destructive menus cannot be executed through this tool.");

            try
            {
                EditorApplication.ExecuteMenuItem(menuPath);
                return ToolDispatchResult.Ok("\"ok\"");
            }
            catch (ArgumentNullException)
            {
                return ToolDispatchResult.Fail("menu_not_found",
                    $"Menu item '{menuPath}' not found. " +
                    "Verify the menu path is correct and matches the Editor menu hierarchy exactly.");
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error",
                    $"Failed to execute menu '{menuPath}': {e.Message}");
            }
        }

        static bool IsBlocked(string menuPath)
        {
            return BlockedMenus.Contains(menuPath);
        }
    }
}
