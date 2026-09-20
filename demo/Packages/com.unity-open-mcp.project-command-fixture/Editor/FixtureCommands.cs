using UnityOpenMcpBridge;

namespace ProjectCommandFixture
{
    public enum ReportDetail { Compact, Full }

    [BridgeToolType]
    public static class FixtureCommands
    {
        [ProjectCommand("project.demo.catalog_fixture", Title = "Catalog fixture", Description = "Read-only project command demonstrating typed discovery.",
            Package = "demo.project-commands", Group = "examples", Tags = new[] { "demo", "validation" },
            ReadOnlyHint = true, IdempotentHint = true, Gate = GateMode.Off,
            DeprecatedAliases = new[] { "project.demo.old_catalog_fixture" })]
        public static string Report(
            [ProjectCommandParameter(Description = "Labels (may be null)", Examples = new[] { "[\"example\"]" })] string[] labels,
            ReportDetail detail = ReportDetail.Compact,
            [ProjectCommandParameter(Description = "Maximum entries", Minimum = 1, Maximum = 10, Examples = new[] { "3" })] int limit = 3,
            int? seed = null) => "{\"fixture\":true}";

        [ProjectCommand("project.demo.long_write", Title = "Long write fixture", Description = "Prepare over time, then write one disposable asset; cooperatively cancellable between steps.",
            Package = "demo.project-commands", IsMutating = true, Async = true, Cancellable = true,
            Lifecycle = LifecyclePolicy.EditorSettle, PathsHint = new[] { "Assets/_ValidationSuite/ProjectCommands" })]
        public static async System.Threading.Tasks.Task<string> LongWrite(ProjectCommandContext context,
            [ProjectCommandParameter(Minimum = 1, Maximum = 120)] int seconds = 60)
        {
            for (int i = 0; i < seconds; i++)
            {
                context.ReportPhase("preparing step " + (i + 1) + " of " + seconds);
                await System.Threading.Tasks.Task.Delay(1000, context.CancellationToken);
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportPhase("writing asset");
            return Write("Async project command completed.");
        }

        [ProjectCommand("project.demo.partial_output", Title = "Partial output fixture", Description = "Write disposable output, then fail to exercise terminal validation.",
            Package = "demo.project-commands", IsMutating = true, Async = true,
            Lifecycle = LifecyclePolicy.EditorSettle, PathsHint = new[] { "Assets/_ValidationSuite/ProjectCommands" })]
        public static async System.Threading.Tasks.Task<string> PartialOutput(ProjectCommandContext context, bool invalidJson = true)
        {
            await System.Threading.Tasks.Task.Delay(50);
            context.ReportPhase("writing before failure");
            Write("Partial output requires inspection.");
            if (invalidJson) return "{broken";
            throw new System.InvalidOperationException("Fixture failed after writing.");
        }

        [ProjectCommand("project.demo.reload_fixture", Title = "Reload fixture", Description = "Request compilation for lifecycle validation.",
            Package = "demo.project-commands", IsMutating = true, Lifecycle = LifecyclePolicy.RestartThenSettle,
            PathsHint = new[] { "Packages/com.unity-open-mcp.project-command-fixture" })]
        public static string Reload()
        {
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            return "{\"requested\":true}";
        }

        [ProjectCommand("project.demo.write_fixture", Title = "Write fixture", Description = "Write a disposable validation asset.",
            Package = "demo.project-commands", IsMutating = true, Lifecycle = LifecyclePolicy.EditorSettle,
            PathsHint = new[] { "Assets/_ValidationSuite/ProjectCommands" })]
        public static string Write(string text)
        {
            const string folder = "Assets/_ValidationSuite/ProjectCommands";
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(folder + "/invocation.txt", text);
            UnityEditor.AssetDatabase.ImportAsset(folder + "/invocation.txt");
            return "{\"written\":true}";
        }
    }
}
