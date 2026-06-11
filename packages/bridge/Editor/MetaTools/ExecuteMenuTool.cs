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

        static readonly HashSet<string> ReadOnlyMenuAllowlist = new(StringComparer.OrdinalIgnoreCase)
        {
            "Assets/Refresh",
            "Assets/Reimport All",
            "Assets/Reveal in Finder",
            "Assets/Show in Explorer",
            "Edit/Selection",
            "Edit/Project Settings",
            "File/Open Scene",
            "File/Open Project",
            "GameObject/Align with View",
            "GameObject/Move to View",
            "Window/General/Hierarchy",
            "Window/General/Inspector",
            "Window/General/Project",
            "Window/General/Console",
            "Window/General/Scene",
            "Window/General/Game",
            "Window/Layouts"
        };

        public static bool IsReadOnlyMenu(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath)) return false;
            foreach (var allowed in ReadOnlyMenuAllowlist)
            {
                if (menuPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

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
