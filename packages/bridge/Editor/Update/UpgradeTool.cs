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
                var version = VersionPinRewriter.NormalizeVersion(target_version);
                if (string.IsNullOrEmpty(version))
                {
                    var latest = LatestVersionCheck.CheckAsync().GetAwaiter().GetResult();
                    if (!latest.Success)
                        return Error("latest_version_failed", latest.Error);
                    version = latest.Version;
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

                // Confirm that the release actually carries the UPM tag before
                // scheduling Package Manager. Config-only updates need no GitHub call.
                if (plan.UpmEnabled)
                {
                    var tag = LatestVersionCheck.ConfirmBridgeTagAsync(version).GetAwaiter().GetResult();
                    if (!tag.Success) return Error("release_tag_unavailable", tag.Error);
                }

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
