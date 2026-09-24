using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace UnityOpenMcpBridge.Update
{
    /// <summary>Plans and applies one project's version-pin upgrade.</summary>
    [InitializeOnLoad]
    internal static class ProjectUpgradeRunner
    {
        internal const string RepositoryUrl = "https://github.com/AlexeyPerov/unity-open-mcp.git";
        private const string ReportKey = "UnityOpenMcp.Upgrade.LastReport";
        private const string PendingTargetKey = "UnityOpenMcp.Upgrade.PendingTarget";
        private static AddAndRemoveRequest _upmRequest;

        static ProjectUpgradeRunner()
        {
            EditorUpdateOnce.Schedule(RecoverPendingState);
        }

        internal readonly struct Options
        {
            public readonly bool Upm;
            public readonly bool ProjectConfigs;
            public readonly bool HomeConfigs;
            public readonly bool Prose;

            public Options(bool upm, bool projectConfigs, bool homeConfigs, bool prose)
            {
                Upm = upm;
                ProjectConfigs = projectConfigs;
                HomeConfigs = homeConfigs;
                Prose = prose;
            }

            public static Options All => new Options(true, true, true, true);
        }

        internal sealed class FilePlan
        {
            public string Path;
            public UpgradeScanner.CandidateKind Kind;
            public string OriginalBody;
            public string UpdatedBody;
            public VersionPinRewriter.RewriteResult Rewrite;
            public string SkipReason;
            public bool WillWrite => string.IsNullOrEmpty(SkipReason) && Rewrite.Changed;
            public bool WillUpdateViaUpm => Kind == UpgradeScanner.CandidateKind.UpmManifest
                && !string.IsNullOrEmpty(SkipReason)
                && SkipReason.StartsWith("Unity Package Manager", StringComparison.Ordinal)
                && Rewrite.Changed;
        }

        internal sealed class PlanResult
        {
            public string ProjectPath;
            public string TargetVersion;
            public Options Selected;
            public readonly List<FilePlan> Files = new List<FilePlan>();
            public bool UpmEnabled;
            public string UpmReason;
            public string Error;

            public int WriteCount
            {
                get
                {
                    var count = 0;
                    foreach (var file in Files) if (file.WillWrite && file.Kind != UpgradeScanner.CandidateKind.UpmManifest) count++;
                    return count;
                }
            }

            public bool HasChanges => WriteCount > 0 || UpmEnabled;
        }

        internal readonly struct ApplyResult
        {
            public readonly bool Success;
            public readonly int FilesWritten;
            public readonly bool UpmScheduled;
            public readonly string Message;

            public ApplyResult(bool success, int filesWritten, bool upmScheduled, string message)
            {
                Success = success;
                FilesWritten = filesWritten;
                UpmScheduled = upmScheduled;
                Message = message;
            }
        }

        internal static string LastReport => SessionState.GetString(ReportKey, "");

        internal static PlanResult Plan(string projectPath, string targetVersion, Options options)
        {
            var plan = new PlanResult
            {
                ProjectPath = projectPath,
                TargetVersion = VersionPinRewriter.NormalizeVersion(targetVersion),
                Selected = options,
            };
            if (string.IsNullOrEmpty(projectPath))
            {
                plan.Error = "No Unity project is open.";
                return plan;
            }
            if (!VersionPinRewriter.IsVersion(plan.TargetVersion))
            {
                plan.Error = "Target version must be a plain X.Y.Z.";
                return plan;
            }

            ResolveUpmGuard(options.Upm, out plan.UpmEnabled, out plan.UpmReason);
            var scanOptions = new UpgradeScanner.ScanOptions(
                options.ProjectConfigs, options.HomeConfigs, options.Prose, options.Upm);
            foreach (var candidate in UpgradeScanner.Collect(projectPath, scanOptions))
            {
                try
                {
                    var body = File.ReadAllText(candidate.Path);
                    var rewrite = VersionPinRewriter.Rewrite(
                        body, plan.TargetVersion, VersionPinRewriter.IsPackagesLockPath(candidate.Path));
                    if (!rewrite.HasPin) continue;
                    var file = new FilePlan
                    {
                        Path = candidate.Path,
                        Kind = candidate.Kind,
                        OriginalBody = body,
                        UpdatedBody = rewrite.Body,
                        Rewrite = rewrite,
                    };
                    if (candidate.Kind == UpgradeScanner.CandidateKind.ProjectConfig
                        || candidate.Kind == UpgradeScanner.CandidateKind.HomeConfig)
                    {
                        var scope = UpgradeEntryScope.Classify(body, projectPath);
                        if (!UpgradeEntryScope.ShouldRewrite(scope, candidate.IsHomeScoped))
                            file.SkipReason = scope.SkipReason;
                    }
                    if (candidate.Kind == UpgradeScanner.CandidateKind.UpmManifest)
                    {
                        file.SkipReason = plan.UpmEnabled
                            ? "Unity Package Manager will rewrite this file atomically"
                            : plan.UpmReason;
                    }
                    plan.Files.Add(file);
                }
                catch (Exception e)
                {
                    plan.Files.Add(new FilePlan
                    {
                        Path = candidate.Path,
                        Kind = candidate.Kind,
                        SkipReason = "could not read: " + e.Message,
                    });
                }
            }
            return plan;
        }

        internal static ApplyResult Apply(PlanResult plan, bool scheduleUpm = true)
        {
            if (plan == null || !string.IsNullOrEmpty(plan.Error))
                return new ApplyResult(false, 0, false, plan?.Error ?? "No preview is available.");

            var written = 0;
            try
            {
                // A preview is an optimistic transaction boundary. Refuse the
                // whole apply before its first write if any planned file changed
                // since review; otherwise an editor/client save between Preview
                // and Apply could be silently overwritten with the stale body.
                foreach (var file in plan.Files)
                {
                    if (!file.WillWrite || file.Kind == UpgradeScanner.CandidateKind.UpmManifest) continue;
                    if (!string.Equals(File.ReadAllText(file.Path), file.OriginalBody, StringComparison.Ordinal))
                        throw new IOException($"'{file.Path}' changed since preview; preview again before applying");
                }
                foreach (var file in plan.Files)
                {
                    if (!file.WillWrite || file.Kind == UpgradeScanner.CandidateKind.UpmManifest) continue;
                    var backup = file.Path + ".bak";
                    if (!File.Exists(backup)) File.Copy(file.Path, backup);
                    File.WriteAllText(file.Path, file.UpdatedBody);
                    written++;
                }
            }
            catch (Exception e)
            {
                var failed = $"Upgrade stopped after writing {written} file(s): {e.Message}";
                SessionState.SetString(ReportKey, failed);
                return new ApplyResult(false, written, false, failed);
            }

            var scheduled = scheduleUpm && plan.UpmEnabled;
            var message = $"Updated {written} config/prose file(s).";
            if (scheduled)
            {
                message += " Unity Package Manager update scheduled; the Editor may reload.";
                var version = plan.TargetVersion;
                // Not delayCall: the MCP-driven apply runs in an Editor nobody
                // is repainting, and delayCall can sit pending there forever.
                EditorUpdateOnce.Schedule(() => StartUpmUpdate(version));
            }
            else if (!string.IsNullOrEmpty(plan.UpmReason))
            {
                message += " Package step skipped: " + plan.UpmReason;
            }
            message += " Restart MCP clients so they reload their configuration.";
            SessionState.SetString(ReportKey, message);
            return new ApplyResult(true, written, scheduled, message);
        }

        internal static string FormatReport(PlanResult plan)
        {
            if (plan == null) return "No preview.";
            if (!string.IsNullOrEmpty(plan.Error)) return plan.Error;
            var sb = new StringBuilder(512);
            sb.Append("Target ").Append(plan.TargetVersion).Append('\n');
            foreach (var file in plan.Files)
            {
                sb.Append(file.WillWrite ? "UPDATE " : file.WillUpdateViaUpm ? "UPM    " : "SKIP   ")
                    .Append(file.Path);
                if (!string.IsNullOrEmpty(file.SkipReason))
                {
                    sb.Append(" — ").Append(file.SkipReason);
                    if (file.WillUpdateViaUpm) AppendChanges(sb, file, plan.TargetVersion);
                }
                else if (file.Rewrite.FloatingPins > 0 && !file.Rewrite.Changed)
                    sb.Append(" — no pin (tracks latest)");
                else if (!file.Rewrite.Changed) sb.Append(" — already current");
                else AppendChanges(sb, file, plan.TargetVersion, prefix: " — ");
                sb.Append('\n');
            }
            sb.Append(plan.UpmEnabled ? "UPM    update bridge + verify atomically"
                : "UPM    skipped — " + (plan.UpmReason ?? "not selected"));
            return sb.ToString();
        }

        internal static string ToJson(PlanResult plan, string phase, ApplyResult? applied = null)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"ok\",\"phase\":").Append(BridgeJson.EscapeString(phase));
            sb.Append(",\"targetVersion\":").Append(BridgeJson.EscapeString(plan.TargetVersion));
            sb.Append(",\"upmEnabled\":").Append(plan.UpmEnabled ? "true" : "false");
            sb.Append(",\"upmReason\":").Append(BridgeJson.EscapeString(plan.UpmReason ?? ""));
            sb.Append(",\"files\":[");
            for (var i = 0; i < plan.Files.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var file = plan.Files[i];
                sb.Append("{\"path\":").Append(BridgeJson.EscapeString(file.Path));
                var action = file.WillWrite ? "update" : file.WillUpdateViaUpm ? "upm_update" : "skip";
                sb.Append(",\"action\":").Append(BridgeJson.EscapeString(action));
                sb.Append(",\"pinsFound\":").Append(file.Rewrite.PinsFound);
                sb.Append(",\"floatingPins\":").Append(file.Rewrite.FloatingPins);
                sb.Append(",\"reason\":").Append(BridgeJson.EscapeString(file.SkipReason ??
                    (file.Rewrite.Changed ? "" : "already current"))).Append('}');
            }
            sb.Append(']');
            if (applied.HasValue)
            {
                sb.Append(",\"filesWritten\":").Append(applied.Value.FilesWritten);
                sb.Append(",\"upmScheduled\":").Append(applied.Value.UpmScheduled ? "true" : "false");
                sb.Append(",\"message\":").Append(BridgeJson.EscapeString(applied.Value.Message));
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendChanges(StringBuilder sb, FilePlan file, string target, string prefix = "; ")
        {
            sb.Append(prefix);
            for (var i = 0; i < file.Rewrite.Changes.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var change = file.Rewrite.Changes[i];
                sb.Append(change.Label).Append(' ')
                    .Append(string.Join("/", change.From)).Append(" → ").Append(target);
            }
        }

        private static void ResolveUpmGuard(bool selected, out bool enabled, out string reason)
        {
            enabled = false;
            reason = selected ? null : "not selected";
            if (!selected) return;
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                    typeof(ProjectUpgradeRunner).Assembly);
                if (package == null)
                {
                    reason = "bridge package information is unavailable";
                    return;
                }
                if (package.source != PackageSource.Git)
                {
                    reason = $"bridge is installed from {package.source}, not Git; preserving the development install";
                    return;
                }
                enabled = true;
            }
            catch (Exception e)
            {
                reason = "could not inspect bridge package source: " + e.Message;
            }
        }

        private static void StartUpmUpdate(string version)
        {
            try
            {
                var verify = RepositoryUrl + "?path=packages/verify#verify-v" + version;
                var bridge = RepositoryUrl + "?path=packages/bridge#bridge-v" + version;
                SessionState.SetString(PendingTargetKey, version);
                _upmRequest = Client.AddAndRemove(new[] { verify, bridge }, new string[0]);
                EditorApplication.update -= PollUpmRequest;
                EditorApplication.update += PollUpmRequest;
                SessionState.SetString(ReportKey,
                    $"Requested bridge + verify {version} from Unity Package Manager. Waiting for resolution/reload. " +
                    "Restart MCP clients after Unity settles.");
            }
            catch (Exception e)
            {
                SessionState.EraseString(PendingTargetKey);
                SessionState.SetString(ReportKey, "Unity Package Manager update could not start: " + e.Message);
            }
        }

        private static void PollUpmRequest()
        {
            if (_upmRequest == null || !_upmRequest.IsCompleted) return;
            EditorApplication.update -= PollUpmRequest;
            var target = SessionState.GetString(PendingTargetKey, "");
            if (_upmRequest.Status == StatusCode.Failure)
            {
                SessionState.SetString(ReportKey, "Unity Package Manager update failed: " +
                    (_upmRequest.Error?.message ?? "unknown Package Manager error"));
            }
            else
            {
                SessionState.SetString(ReportKey,
                    $"Completed bridge + verify update to {target}. Restart MCP clients so they reload configuration.");
            }
            SessionState.EraseString(PendingTargetKey);
            _upmRequest = null;
        }

        private static void RecoverPendingState()
        {
            var target = SessionState.GetString(PendingTargetKey, "");
            if (string.IsNullOrEmpty(target)) return;
            if (!string.Equals(BridgeSession.BridgeVersion, target, StringComparison.Ordinal)) return;
            SessionState.SetString(ReportKey,
                $"Completed bridge + verify update to {target}. Restart MCP clients so they reload configuration.");
            SessionState.EraseString(PendingTargetKey);
        }
    }
}
