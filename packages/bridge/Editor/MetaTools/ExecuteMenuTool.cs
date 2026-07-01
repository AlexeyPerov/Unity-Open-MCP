using System;
using System.Collections.Generic;
using UnityEditor;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class ExecuteMenuTool
    {
        private static readonly HashSet<string> BlockedMenus = new(StringComparer.OrdinalIgnoreCase)
        {
            "File/Quit"
        };

        private static readonly HashSet<string> ReadOnlyMenuAllowlist = new(StringComparer.OrdinalIgnoreCase)
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

        // Batch-viable menu allow-list (M26 Plan 3). Most Editor menus open a
        // window or dialog and fail under -batchmode (no UI). This is the
        // conservative subset whose [MenuItem] handlers perform pure
        // AssetDatabase / project work that does not require the Editor UI, so
        // they succeed headless. Everything else is rejected with
        // menu_not_viable_in_batchmode by the batch entry point so an agent
        // gets a clear signal to connect a live Editor instead of a hung spawn.
        private static readonly HashSet<string> BatchViableMenuAllowlist = new(StringComparer.OrdinalIgnoreCase)
        {
            "Assets/Refresh",
            "Assets/Reimport All",
            "File/Save Project",
            "File/Save Scenes",
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

        /// <summary>
        /// M26 Plan 3 — is <paramref name="menuPath"/> on the batch-viable
        /// allow-list? Only the conservative subset of Editor menus whose
        /// handlers do pure AssetDatabase/project work (no window/dialog) are
        /// permitted under -batchmode. The batch entry point gates on this and
        /// returns <c>menu_not_viable_in_batchmode</c> for everything else.
        /// </summary>
        public static bool IsBatchViable(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath)) return false;
            foreach (var allowed in BatchViableMenuAllowlist)
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

            // M14 T5.3 — configurable regex deny heuristic. Bypass contract is
            // the same as execute_csharp: gate: "off" + confirm_bypass: true.
            var bypass = BridgeDenyBypass.IsRequestedFromBody(body);
            var deny = BridgeDenyList.EvaluateMenu(
                menuPath, BridgeProjectSettings.MenuDenyPatterns, bypass);
            if (!deny.Allowed)
            {
                return ToolDispatchResult.Fail("menu_blocked",
                    $"{deny.Reason} Suggestion: {deny.Suggestion} " +
                    $"Matched pattern: {deny.MatchedPattern}.");
            }

            // Hardcoded last-resort block. The regex list is configurable and
            // can be disabled (empty array); this set is not, so a destructive
            // menu can never reach ExecuteMenuItem even if the operator wiped
            // the configurable list. File/Quit is the canonical entry here.
            if (IsHardBlocked(menuPath))
                return ToolDispatchResult.Fail("menu_blocked",
                    $"Menu '{menuPath}' is hard-blocked for safety reasons. " +
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

        private static bool IsHardBlocked(string menuPath)
        {
            return BlockedMenus.Contains(menuPath);
        }
    }
}
