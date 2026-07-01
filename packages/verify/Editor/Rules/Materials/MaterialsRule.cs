using System.Collections.Generic;

namespace UnityOpenMcpVerify.Rules
{
    public class MaterialsRule : IVerifyRule
    {
        public string Id => "materials";

        public void Scan(VerifyScope scope, VerifyRunMode mode, List<VerifyIssue> sink)
        {
            if (scope.Paths == null || scope.Paths.Length == 0) return;

            var settings = Materials.MaterialsScanSettings.Default();
            var materials = new List<Materials.MaterialData>();
            var renderers = new List<Materials.RendererData>();
            // Cross-asset detections (duplicates, unused, variants) need the
            // full material set as context — only run them in Full mode. Per-
            // asset detections (missing shader/texture, builtin, render-queue)
            // run in every mode so a scoped validate_edit still catches them.
            var fullScan = mode == VerifyRunMode.Full;
            Materials.Scanner.ScanPaths(scope.Paths, settings, materials, renderers, fullScan);
            Materials.IssueMapper.MapToIssues(materials, renderers, sink);
        }
    }
}
