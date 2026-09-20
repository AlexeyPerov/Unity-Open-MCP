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
    }
}
