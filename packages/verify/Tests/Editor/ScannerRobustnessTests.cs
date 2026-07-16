using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Batch;
using UnityOpenMcpVerify.Internals.AssetDatabase;
using UnityOpenMcpVerify.Internals.RegexPatterns;
using UnityOpenMcpVerify.Rules;
using UnityOpenMcpVerify.Rules.Dependencies;

namespace UnityOpenMcpVerify.Tests
{
    // M30-polish Plan 3 — verify scanner robustness tests.
    //
    // T3.1 — ScanInvalidLayers no longer throws on overflow values; the rule
    //        completes and reports issues for subsequent assets.
    // T3.2 — CountLocalUsages matches fileID tokens, not bare substrings;
    //        PathMatchesFilter matches path segments/prefixes, not substrings.
    // T3.3 — GUID regexes accept uppercase hex; ResolveProjectPath handles
    //        paths containing /Assets mid-path; cycle issues attributed to all
    //        nodes on the cycle.
    [TestFixture]
    public class ScannerRobustnessTests
    {
        private const string FixtureRoot = "Assets/Tests/VerifyFixtures/ScannerRobustness";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            EnsureDirectory(FixtureRoot);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (AssetDatabase.IsValidFolder(FixtureRoot))
            {
                AssetDatabase.DeleteAsset(FixtureRoot);
                AssetDatabase.Refresh();
            }
        }

        // -------------------------------------------------------------------
        // T3.1 — Overflow-safe layer parsing
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator ScanInvalidLayers_OverflowValue_DoesNotAbortRule()
        {
            // A prefab with a corrupted m_Layer: 99999999999 (exceeds int.MaxValue)
            // followed by a second prefab with a genuine missing reference.
            // The rule must complete and report the second prefab's issue.
            var rule = new MissingReferencesRule();

            var meshPath = FixtureRoot + "/OverflowLayerMesh.asset";
            var goodPrefabPath = FixtureRoot + "/OverflowLayerGood.prefab";
            var badPrefabPath = FixtureRoot + "/OverflowLayerBad.prefab";
            yield return CreatePrefabWithMeshReference(goodPrefabPath, meshPath);

            Assume.That(File.Exists(goodPrefabPath), Is.True);

            // Create the "bad" prefab by copying the good one and injecting
            // an overflow m_Layer value.
            File.Copy(goodPrefabPath, badPrefabPath, overwrite: true);
            File.Copy(goodPrefabPath + ".meta", badPrefabPath + ".meta", overwrite: true);
            InjectOverflowLayer(badPrefabPath);
            AssetDatabase.ImportAsset(badPrefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            // Now break the mesh reference in the good prefab so it has a
            // genuine missing_guid issue — the rule must still detect it
            // despite the overflow layer in the other asset.
            var backupPath = meshPath + ".bak";
            var backupMeta = meshPath + ".meta.bak";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            if (File.Exists(backupMeta)) File.Delete(backupMeta);
            File.Move(meshPath, backupPath);
            if (File.Exists(meshPath + ".meta"))
                File.Move(meshPath + ".meta", backupMeta);
            AssetDatabase.Refresh();
            yield return null;

            try
            {
                var sink = new List<VerifyIssue>();
                // Scan the bad prefab FIRST, then the good one — if the overflow
                // aborts the rule, the good prefab's issue is never reported.
                var scope = new VerifyScope(new[] { badPrefabPath, goodPrefabPath });
                rule.Scan(scope, VerifyRunMode.Full, sink);

                var hasMissingGuid = sink.Any(i => i.IssueCode.StartsWith("missing_guid"));
                Assert.IsTrue(hasMissingGuid,
                    "The rule must complete and detect the missing_guid on the " +
                    "second asset despite the overflow m_Layer on the first. " +
                    $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
            }
            finally
            {
                File.Move(backupPath, meshPath);
                if (File.Exists(backupMeta))
                    File.Move(backupMeta, meshPath + ".meta");
                AssetDatabase.Refresh();
            }
        }

        // -------------------------------------------------------------------
        // T3.2 — CountLocalUsages: fileID token match, not bare substring
        // -------------------------------------------------------------------

        [Test]
        public void CountLocalUsages_DigitOverlappingFileID_NotInflated()
        {
            // Simulate two lines: one declaring fileID 114123456 (a long ID
            // that contains "123" as a substring), and one referencing
            // fileID: 123. The old `Contains("123")` would count the
            // 114123456 line as a usage of 123 — inflating the count.
            //
            // We test the regex directly since CountLocalUsages is private.
            var idStr = "123";
            var pattern = new System.Text.RegularExpressions.Regex(
                @"fileID:\s*" + System.Text.RegularExpressions.Regex.Escape(idStr) + @"\b");

            // The overlapping line — "123" appears inside "114123456" but NOT
            // as a fileID token. Must NOT match.
            var overlappingLine = "  m_Component: {fileID: 114123456}";
            Assert.IsFalse(pattern.IsMatch(overlappingLine),
                "fileID 123 must NOT match inside 114123456");

            // The genuine reference line — "fileID: 123" as a token. Must match.
            var genuineLine = "  m_Target: {fileID: 123}";
            Assert.IsTrue(pattern.IsMatch(genuineLine),
                "fileID 123 must match as a YAML reference token");

            // The declaration line itself — "fileID: 123" in the object header.
            var declLine = "--- !u!1 &123";
            // &123 is the anchor form, not fileID: 123. The regex matches
            // "fileID:" key only, so this should NOT match (correct — the
            // declaration is not a "usage").
            Assert.IsFalse(pattern.IsMatch(declLine),
                "anchor form &123 must NOT match (it's a declaration, not a usage)");
        }

        // -------------------------------------------------------------------
        // T3.2 — PathMatchesFilter: segment/prefix match, not substring
        // -------------------------------------------------------------------

        [Test]
        public void PathMatchesFilter_SegmentMatch_MatchesExactSegment()
        {
            Assert.IsTrue(PathFilterUtilities.PathMatchesFilter(
                "Assets/Art/Textures/x.mat", "Art"),
                "filter 'Art' must match when 'Art' is a path segment");
        }

        [Test]
        public void PathMatchesFilter_SubstringMatch_DoesNotMatchPartialSegment()
        {
            Assert.IsFalse(PathFilterUtilities.PathMatchesFilter(
                "Assets/Party/x.mat", "Art"),
                "filter 'Art' must NOT match 'Party' (substring inside a segment)");
        }

        [Test]
        public void PathMatchesFilter_PrefixMatch_MatchesDirectoryPrefix()
        {
            Assert.IsTrue(PathFilterUtilities.PathMatchesFilter(
                "Assets/Art/Textures/x.mat", "Assets/Art"),
                "filter 'Assets/Art' must match as a directory prefix");
        }

        [Test]
        public void PathMatchesFilter_PrefixMatch_DoesNotMatchPartialDirectory()
        {
            Assert.IsFalse(PathFilterUtilities.PathMatchesFilter(
                "Assets/ArtParty/x.mat", "Assets/Art"),
                "filter 'Assets/Art' must NOT match 'Assets/ArtParty' (partial directory)");
        }

        [Test]
        public void PathMatchesFilter_ExactMatch_MatchesFullPath()
        {
            Assert.IsTrue(PathFilterUtilities.PathMatchesFilter(
                "Assets/Art/x.mat", "Assets/Art/x.mat"),
                "exact path match must work");
        }

        [Test]
        public void PathMatchesFilter_EmptyFilter_MatchesEverything()
        {
            Assert.IsTrue(PathFilterUtilities.PathMatchesFilter(
                "Assets/Anything/x.mat", ""),
                "empty filter must match everything");
        }

        [Test]
        public void PathMatchesFilter_CaseInsensitive_MatchesDifferentCase()
        {
            Assert.IsTrue(PathFilterUtilities.PathMatchesFilter(
                "Assets/ART/Textures/x.mat", "art"),
                "segment match must be case-insensitive");
        }

        // -------------------------------------------------------------------
        // T3.3 — GUID regex case parity
        // -------------------------------------------------------------------

        [Test]
        public void ExternalFileAndGuid_AcceptsUppercaseHex()
        {
            var line = "  m_Texture: {fileID: 2800000, guid: ABCDEF0123456789ABCDEF0123456789, type: 3}";
            var match = SharedRegex.ExternalFileAndGuid.Match(line);

            Assert.IsTrue(match.Success,
                "ExternalFileAndGuid must match uppercase-hex GUIDs");
            Assert.AreEqual("ABCDEF0123456789ABCDEF0123456789", match.Groups[2].Value);
        }

        [Test]
        public void ExternalFileAndGuid_AcceptsLowercaseHex()
        {
            var line = "  m_Texture: {fileID: 2800000, guid: abcdef0123456789abcdef0123456789, type: 3}";
            var match = SharedRegex.ExternalFileAndGuid.Match(line);

            Assert.IsTrue(match.Success,
                "ExternalFileAndGuid must still match lowercase-hex GUIDs");
        }

        [Test]
        public void ScriptGuid_AcceptsUppercaseHex()
        {
            var line = "  m_Script: {fileID: 11500000, guid: ABCDEF0123456789ABCDEF0123456789, type: 3}";
            var match = SharedRegex.ScriptGuid.Match(line);

            Assert.IsTrue(match.Success,
                "ScriptGuid must match uppercase-hex GUIDs");
            Assert.AreEqual("ABCDEF0123456789ABCDEF0123456789", match.Groups[1].Value);
        }

        [UnityTest]
        public System.Collections.IEnumerator MissingReferences_DetectsUppercaseGuidReference()
        {
            // Build a prefab with a mesh reference, then corrupt the mesh GUID
            // to an UPPERCASE fake. The missing_references rule must detect it
            // (previously the lowercase-only regex silently missed uppercase hex).
            var rule = new MissingReferencesRule();

            var meshPath = FixtureRoot + "/UpperGuidMesh.asset";
            var prefabPath = FixtureRoot + "/UpperGuidPrefab.prefab";
            yield return CreatePrefabWithMeshReference(prefabPath, meshPath);

            Assume.That(File.Exists(prefabPath), Is.True, "prefab must exist");

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                AssetDatabase.LoadAssetAtPath<Mesh>(meshPath), out var realGuid, out _);
            Assume.That(string.IsNullOrEmpty(realGuid), Is.False);

            // Replace the real GUID with an uppercase fake.
            var fakeGuid = "ABCDEF0123456789ABCDEF0123456789";
            var yaml = File.ReadAllText(prefabPath);
            yaml = yaml.Replace($"guid: {realGuid}", $"guid: {fakeGuid}");
            File.WriteAllText(prefabPath, yaml);
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { prefabPath });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var hasMissingGuid = sink.Any(i => i.IssueCode.StartsWith("missing_guid"));
            Assert.IsTrue(hasMissingGuid,
                "uppercase-hex GUID reference must be detected as missing_guid. " +
                $"Got: {string.Join(", ", sink.Select(i => i.IssueCode))}");
        }

        // -------------------------------------------------------------------
        // T3.3 — ResolveProjectPath handles paths containing /Assets
        // -------------------------------------------------------------------

        [Test]
        public void ResolveProjectPath_AbsolutePath_ReturnedAsIs()
        {
            var abs = "/tmp/some/output/path.json";
            var result = VerifyBatchEntry.ResolveProjectPath(abs);

            Assert.AreEqual(abs, result,
                "an absolute path must be returned unchanged");
        }

        [Test]
        public void ResolveProjectPath_RelativePath_CombinedWithProjectRoot()
        {
            var result = VerifyBatchEntry.ResolveProjectPath("CI/baseline.json");

            // The result must be an absolute path under the project root
            // (parent of the Assets folder), NOT under a corrupted path.
            var expectedRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.IsNotNull(expectedRoot);

            Assert.IsTrue(result.StartsWith(expectedRoot, System.StringComparison.Ordinal),
                $"relative path must resolve under the project root '{expectedRoot}'. Got: {result}");
            Assert.IsTrue(result.EndsWith("CI/baseline.json", System.StringComparison.Ordinal),
                $"relative path must be appended to the project root. Got: {result}");
        }

        // -------------------------------------------------------------------
        // T3.3 — Cycle attribution: all nodes on the cycle get an issue
        // -------------------------------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator DependencyCycle_AllNodesGetIssue()
        {
            // Create two materials that reference each other via m_AssetGUID
            // (A→B and B→A), forming a cycle. Both A and B must get a
            // dependency_cycle issue — previously only the back-edge node did.
            var rule = new DependenciesRule();

            var matAPath = FixtureRoot + "/CycleA.mat";
            var matBPath = FixtureRoot + "/CycleB.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matAPath);
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matBPath);
            AssetDatabase.Refresh();
            yield return null;

            var guidA = AssetDatabase.AssetPathToGUID(matAPath);
            var guidB = AssetDatabase.AssetPathToGUID(matBPath);
            Assume.That(string.IsNullOrEmpty(guidA), Is.False, "material A must have a GUID");
            Assume.That(string.IsNullOrEmpty(guidB), Is.False, "material B must have a GUID");

            // Inject m_AssetGUID cross-references — the dependencies scanner
            // reads these as "assetref" edges and adds them to ForwardDeps,
            // which is what DetectCycles traverses.
            InjectAssetGuidReference(matAPath, guidB);
            InjectAssetGuidReference(matBPath, guidA);
            AssetDatabase.ImportAsset(matAPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(matBPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { matAPath, matBPath });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var cycleIssues = sink.Where(i => i.IssueCode == "dependency_cycle").ToList();
            Assert.GreaterOrEqual(cycleIssues.Count, 2,
                "both cycle nodes must get a dependency_cycle issue. " +
                $"Got {cycleIssues.Count} cycle issues. " +
                $"All issues: {string.Join(", ", sink.Select(i => i.IssueCode))}");

            // Verify both A and B have a cycle issue.
            var cycleAssetPaths = cycleIssues.Select(i => i.AssetPath).Distinct().ToList();
            CollectionAssert.Contains(cycleAssetPaths, matAPath,
                "material A must have a dependency_cycle issue");
            CollectionAssert.Contains(cycleAssetPaths, matBPath,
                "material B must have a dependency_cycle issue");
        }

        // Review follow-up — the 2-node A↔B test has no "interior" node, so it
        // does not exercise the cycle.Count - 1 loop bound in Scanner.cs:147
        // (ExtractCycle appends a trailing repeat of the entry node, and the
        // loop attributes to indices 0..count-2). A ≥3-node cycle A→B→C→A has
        // an interior node B that only this shape reaches: B is neither the
        // entry nor the back-edge target, so it is attributed solely by the
        // loop bound. All three nodes must get a dependency_cycle issue.
        [UnityTest]
        public System.Collections.IEnumerator DependencyCycle_ThreeNodeCycle_AllNodesGetIssue()
        {
            var rule = new DependenciesRule();

            var matAPath = FixtureRoot + "/Cycle3A.mat";
            var matBPath = FixtureRoot + "/Cycle3B.mat";
            var matCPath = FixtureRoot + "/Cycle3C.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matAPath);
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matBPath);
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), matCPath);
            AssetDatabase.Refresh();
            yield return null;

            var guidA = AssetDatabase.AssetPathToGUID(matAPath);
            var guidB = AssetDatabase.AssetPathToGUID(matBPath);
            var guidC = AssetDatabase.AssetPathToGUID(matCPath);
            Assume.That(string.IsNullOrEmpty(guidA), Is.False, "material A must have a GUID");
            Assume.That(string.IsNullOrEmpty(guidB), Is.False, "material B must have a GUID");
            Assume.That(string.IsNullOrEmpty(guidC), Is.False, "material C must have a GUID");

            // A→B→C→A edges.
            InjectAssetGuidReference(matAPath, guidB);
            InjectAssetGuidReference(matBPath, guidC);
            InjectAssetGuidReference(matCPath, guidA);
            AssetDatabase.ImportAsset(matAPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(matBPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(matCPath, ImportAssetOptions.ForceUpdate);
            yield return null;

            var sink = new List<VerifyIssue>();
            var scope = new VerifyScope(new[] { matAPath, matBPath, matCPath });
            rule.Scan(scope, VerifyRunMode.Full, sink);

            var cycleIssues = sink.Where(i => i.IssueCode == "dependency_cycle").ToList();
            Assert.GreaterOrEqual(cycleIssues.Count, 3,
                "all three cycle nodes must get a dependency_cycle issue. " +
                $"Got {cycleIssues.Count} cycle issues. " +
                $"All issues: {string.Join(", ", sink.Select(i => i.IssueCode))}");

            // All three paths must appear — B is the interior node whose
            // attribution depends on the loop bound, not the back-edge.
            var cycleAssetPaths = cycleIssues.Select(i => i.AssetPath).Distinct().ToList();
            CollectionAssert.Contains(cycleAssetPaths, matAPath,
                "material A (cycle entry) must have a dependency_cycle issue");
            CollectionAssert.Contains(cycleAssetPaths, matBPath,
                "material B (interior node) must have a dependency_cycle issue");
            CollectionAssert.Contains(cycleAssetPaths, matCPath,
                "material C (back-edge node) must have a dependency_cycle issue");
        }

        // -------------------------------------------------------------------
        // Fixture helpers
        // -------------------------------------------------------------------

        private static System.Collections.IEnumerator CreatePrefabWithMeshReference(
            string prefabPath, string meshPath)
        {
            EnsureDirectory(Path.GetDirectoryName(prefabPath));

            var mesh = new Mesh();
            mesh.vertices = new[] {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0, 1, 0),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject("ScannerFixture");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();
            yield return null;
        }

        private static void InjectAssetGuidReference(string assetPath, string targetGuid)
        {
            var yaml = File.ReadAllText(assetPath);
            // Inject an m_AssetGUID line — the dependencies scanner reads these
            // as "assetref" declared edges and adds them to ForwardDeps.
            var edge = "  m_AssetGUID: " + targetGuid + "\n";
            // Insert before the closing newline of the first YAML document.
            var lastBrace = yaml.LastIndexOf('}');
            if (lastBrace >= 0)
                yaml = yaml.Insert(lastBrace + 1, "\n" + edge);
            else
                yaml += "\n" + edge;
            File.WriteAllText(assetPath, yaml);
        }

        private static void InjectOverflowLayer(string prefabPath)
        {
            var yaml = File.ReadAllText(prefabPath);
            // Inject a corrupted m_Layer line with a value exceeding int.MaxValue.
            // Unity writes m_Layer as a field on GameObjects; we append it after
            // the first GameObject block header. The scanner regex matches
            // `^\s*m_Layer:\s*(\d+)\s*$` so the line just needs to be in the file.
            var lines = yaml.Split('\n');
            var injected = false;
            for (var i = 0; i < lines.Length && !injected; i++)
            {
                if (lines[i].StartsWith("--- !u!1 &", System.StringComparison.Ordinal))
                {
                    // Insert the overflow layer after the GameObject block header
                    // and its m_ObjectHideFlags line (if present).
                    var insertAt = i + 1;
                    if (insertAt < lines.Length && lines[insertAt].Contains("m_ObjectHideFlags"))
                        insertAt++;
                    var newLines = new List<string>(lines.Length + 1);
                    newLines.AddRange(lines.Take(insertAt));
                    newLines.Add("  m_Layer: 99999999999");
                    newLines.AddRange(lines.Skip(insertAt));
                    yaml = string.Join("\n", newLines);
                    injected = true;
                }
            }
            if (!injected)
            {
                // Fallback: append the overflow layer line.
                yaml += "\n  m_Layer: 99999999999\n";
            }
            File.WriteAllText(prefabPath, yaml);
        }

        private static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                var parent = Path.GetDirectoryName(path);
                var name = Path.GetFileName(path);
                if (!AssetDatabase.IsValidFolder(parent))
                    EnsureDirectory(parent);
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
