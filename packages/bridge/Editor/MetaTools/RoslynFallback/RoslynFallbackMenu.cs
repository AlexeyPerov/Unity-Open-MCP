using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.MetaTools.RoslynFallback
{
    /// <summary>
    /// Human-facing install surface for the Roslyn fallback: a Tools menu
    /// item (flat sibling of the bridge window entry — the window path is a
    /// leaf item, so a submenu under it would conflict) plus a shared
    /// confirm-and-poll driver the bridge window's Extensions tab reuses.
    /// Mirrors the OptionalDependenciesPanel UX: confirm dialog, progress bar
    /// polled on EditorApplication.update, completion dialog.
    /// </summary>
    internal static class RoslynFallbackMenu
    {
        private const string MenuPath = "Tools/Unity Open MCP Bridge - Install Roslyn Fallback";

        [MenuItem(MenuPath)]
        private static void InstallFromMenu() => ConfirmAndInstall();

        [MenuItem(MenuPath, isValidateFunction: true)]
        private static bool ValidateInstallFromMenu() =>
            !RoslynFallbackInstaller.IsInstalled &&
            RoslynFallbackInstaller.Status.State != RoslynFallbackInstaller.InstallState.Installing;

        /// <summary>
        /// Shared entry for the menu item and the bridge window button.
        /// Shows the consent dialog (skipped in batch mode, where the install
        /// runs without UI), starts the install, and drives a progress bar
        /// until it finishes.
        /// </summary>
        internal static void ConfirmAndInstall()
        {
            if (RoslynFallbackInstaller.IsInstalled)
            {
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("Roslyn Fallback",
                        "The Roslyn fallback is already installed at\n" +
                        RoslynFallbackConfig.InstallDir, "OK");
                return;
            }

            if (!Application.isBatchMode)
            {
                var proceed = EditorUtility.DisplayDialog(
                    "Install Roslyn Fallback",
                    "This Unity version ships no Roslyn the editor's Mono runtime can " +
                    "load, so execute_csharp / script validation are unavailable.\n\n" +
                    "Download the IL-only Roslyn " + RoslynFallbackConfig.RoslynVersion +
                    " compiler (~15 MB, 10 SHA-256-pinned packages from nuget.org) to\n" +
                    RoslynFallbackConfig.InstallDir + "?",
                    "Download and Install", "Cancel");
                if (!proceed) return;
            }

            RoslynFallbackInstaller.ResetFailure();
            RoslynFallbackInstaller.StartInstall();

            if (Application.isBatchMode) return; // status is observable via the tool path

            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                var status = RoslynFallbackInstaller.Status;
                switch (status.State)
                {
                    case RoslynFallbackInstaller.InstallState.Installing:
                        EditorUtility.DisplayProgressBar("Roslyn Fallback",
                            string.IsNullOrEmpty(status.Step) ? "installing…" : status.Step,
                            status.Progress);
                        return;

                    case RoslynFallbackInstaller.InstallState.Installed:
                        EditorApplication.update -= poll;
                        EditorUtility.ClearProgressBar();
                        EditorUtility.DisplayDialog("Roslyn Fallback",
                            "Installed. execute_csharp and script validation are now available.",
                            "OK");
                        return;

                    default: // Failed or unexpectedly Idle
                        EditorApplication.update -= poll;
                        EditorUtility.ClearProgressBar();
                        EditorUtility.DisplayDialog("Roslyn Fallback",
                            "Install failed: " + (status.Error ?? "unknown error") +
                            "\n\nSee the Console for details; docs/troubleshooting.md " +
                            "describes a manual offline install.",
                            "OK");
                        return;
                }
            };
            EditorApplication.update += poll;
        }
    }
}
