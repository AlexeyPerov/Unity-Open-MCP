using System;

namespace UnityOpenMcpBridge.Update
{
    [BridgeToolType]
    public static class UpgradeTool
    {
        [BridgeTool("unity_open_mcp_upgrade",
            Title = "Upgrade this project",
            IsMutating = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.RestartThenSettle,
            Group = "typed-editor")]
        [System.ComponentModel.Description(
            "Preview or apply a coordinated version-pin update for the open project. " +
            "Updates selected project/home MCP configs and agent-facing prose, then " +
            "atomically re-pins the Git-installed bridge and verify packages. Dry-run defaults true.")]
        public static string Run(
            string target_version = null,
            bool dry_run = true,
            bool update_upm = true,
            bool update_project_configs = true,
            bool update_home_configs = true,
            bool update_prose = true,
            string[] paths_hint = null)
        {
            try
            {
                // This tool runs on the Editor main thread, so it performs NO
                // network I/O: a slow registry would freeze the Editor and the
                // whole bridge request queue. The two lookups the flow needs
                // run where they can be awaited instead — the MCP server
                // resolves the latest npm release and confirms the bridge
                // release tag before forwarding an apply, and the bridge
                // window's Status → Updates panel awaits both off-thread.
                var version = VersionPinRewriter.NormalizeVersion(target_version);
                if (string.IsNullOrEmpty(version))
                {
                    version = LatestVersionCheck.CachedVersion;
                    if (string.IsNullOrEmpty(version))
                        return Error("target_version_required",
                            "Pass target_version (X.Y.Z). The MCP server resolves the latest npm release " +
                            "automatically; a direct bridge caller can run Status → Updates → Check latest " +
                            "in the bridge window first.");
                }
                if (!VersionPinRewriter.IsVersion(version))
                    return Error("invalid_target_version", "target_version must be a plain X.Y.Z.");

                var options = new ProjectUpgradeRunner.Options(
                    update_upm, update_project_configs, update_home_configs, update_prose);
                var plan = ProjectUpgradeRunner.Plan(BridgeSession.ProjectPath, version, options);
                if (!string.IsNullOrEmpty(plan.Error))
                    return Error("upgrade_plan_failed", plan.Error);

                if (dry_run)
                    return ProjectUpgradeRunner.ToJson(plan, "preview");

                // A caller that skipped the server-side release-tag confirmation
                // still gets a nonexistent tag reported: Package Manager fails
                // the request and ProjectUpgradeRunner.LastReport records it.
                var applied = ProjectUpgradeRunner.Apply(plan);
                if (!applied.Success) return Error("upgrade_apply_failed", applied.Message);
                return ProjectUpgradeRunner.ToJson(plan, "applied", applied);
            }
            catch (Exception e)
            {
                return Error("upgrade_failed", e.Message);
            }
        }

        private static string Error(string code, string message)
        {
            return "{\"error\":{\"code\":" + BridgeJson.EscapeString(code) +
                   ",\"message\":" + BridgeJson.EscapeString(message ?? "Unknown error.") + "}}";
        }
    }
}
