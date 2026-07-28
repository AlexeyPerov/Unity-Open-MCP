using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpVerify;
using UnityOpenMcpVerify.Rules.MissingReferences;

namespace UnityOpenMcpVerify.Tests
{
    // Focused unit tests for MissingReferences.IssueMapper — the pure
    // AssetData → VerifyIssue mapping that produces the wire `issueCode` (and
    // the IssueKey discriminator). The IssueMapper is fully testable without a
    // live AssetDatabase: we synthesize an AssetData + AssetReferencesData with
    // the desired entries and assert on the emitted issues' IssueCode / Evidence.
    [TestFixture]
    public class IssueMapperTests
    {
        // -------------------------------------------------------------------
        // B-N13 — invalid_layer discriminator must NOT carry the YAML line.
        // The line is deterministic only for identical bytes; any edit above it
        // (a component add, a reserialize, a .meta reformat) shifts it, so a
        // pre-existing invalid_layer surfaced in the gate delta as one
        // resolvedWarnings PLUS one newWarnings purely from an unrelated
        // change. The discriminator now keys on the stable layerIndex only;
        // the line stays in `evidence` for human inspection.
        // -------------------------------------------------------------------

        [Test]
        public void InvalidLayer_Discriminator_OmitsLine_KeyesOnLayerIndex()
        {
            var data = new AssetReferencesData();
            data.InvalidLayers.Add(new InvalidLayerEntry(layerIndex: 7, line: 42));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            IssueMapper.MapToIssues(new List<AssetData> { asset }, sink);

            var issue = AssertSingle(sink, "invalid_layer");
            // The discriminator is layerIndex only — no trailing :line.
            Assert.AreEqual("invalid_layer:7", issue.IssueCode);
            // The bare code (what the wire `code` field emits) is still clean.
            Assert.AreEqual("invalid_layer", IssueKey.BareIssueCode(issue.IssueCode));
            // The line is preserved in evidence for human inspection.
            Assert.IsNotNull(issue.Evidence, "evidence must carry the line for inspection");
            Assert.IsTrue(issue.Evidence.ContainsKey("line"), "evidence must carry the line key");
            Assert.AreEqual("42", issue.Evidence["line"]);
            Assert.AreEqual("7", issue.Evidence["layerIndex"]);
        }

        [Test]
        public void InvalidLayer_SameIndex_DifferentLines_CollapsesToOneKey()
        {
            // Two invalid-layer entries for the SAME index at DIFFERENT lines
            // (e.g. a component was added above, shifting the line) must map to
            // the SAME issueCode so the gate delta does not churn. The pre-fix
            // discriminator (layerIndex:line) produced two distinct keys.
            var data = new AssetReferencesData();
            data.InvalidLayers.Add(new InvalidLayerEntry(layerIndex: 7, line: 42));
            // Same index, shifted line (an unrelated edit moved it).
            data.InvalidLayers.Add(new InvalidLayerEntry(layerIndex: 7, line: 88));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            IssueMapper.MapToIssues(new List<AssetData> { asset }, sink);

            // Both entries emit the SAME issueCode now (stable key).
            var codes = sink.Where(i => IssueKey.BareIssueCode(i.IssueCode) == "invalid_layer")
                            .Select(i => i.IssueCode).Distinct().ToList();
            Assert.AreEqual(1, codes.Count,
                $"two invalid_layer entries for the same index must share one issueCode, got: {string.Join(", ", codes)}");
            Assert.AreEqual("invalid_layer:7", codes[0]);
        }

        // -------------------------------------------------------------------
        // B-N7 regression guard — duplicate_component sanitizes user-controlled
        // GameObject names so a '|' cannot reach IssueKey.Build.
        // -------------------------------------------------------------------

        [Test]
        public void DuplicateComponent_SanitizesPipeInGameObjectName()
        {
            var data = new AssetReferencesData();
            data.DuplicateComponents.Add(
                new DuplicateComponentEntry("CanvasRenderer", 2, "UI|Header"));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            // Must not throw (the raw '|' would have made IssueKey.Build throw
            // ArgumentException when the issue was later keyed).
            Assert.DoesNotThrow(() => IssueMapper.MapToIssues(new List<AssetData> { asset }, sink));

            var issue = AssertSingle(sink, "duplicate_component");
            // The '|' is sanitized to '_' in the discriminator.
            Assert.AreEqual("duplicate_component:CanvasRenderer:UI_Header", issue.IssueCode);
            // The original name is preserved in evidence for inspection.
            Assert.AreEqual("UI|Header", issue.Evidence["gameObject"]);
        }

        // -------------------------------------------------------------------
        // B-N14 — the wire discriminator forms are well-defined for each code.
        // Documents the contract a `code ===`-matching agent relies on.
        // -------------------------------------------------------------------

        [Test]
        public void MissingGuid_Discriminator_CarriesGuid_BareCodeIsClean()
        {
            var data = new AssetReferencesData();
            data.ExternalReferences.Add(new ExternalReferenceRegistry(
                fileIdValid: true, guidValid: true,
                fileId: 12345, guid: "deadbeefdeadbeefdeadbeefdeadbeef", line: 10)
            {
                GuidExistsInAssets = false,
            });
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            IssueMapper.MapToIssues(new List<AssetData> { asset }, sink);

            var issue = AssertSingle(sink, "missing_guid");
            StringAssert.StartsWith("missing_guid:", issue.IssueCode);
            Assert.AreEqual("missing_guid", IssueKey.BareIssueCode(issue.IssueCode));
        }

        // -------------------------------------------------------------------
        // C2 — the four previously discriminator-less codes now carry stable
        // sanitized discriminators, so two distinct instances on one asset no
        // longer collapse to a single issue key (which hid the second one from
        // the gate delta). The wire `code` field stays bare via BareIssueCode.
        // -------------------------------------------------------------------

        [Test]
        public void MissingMethod_Discriminator_IsClassAndMethod_Sanitized()
        {
            var data = new AssetReferencesData();
            data.MissingMethods.Add(new MissingMethodEntry("Game.Ui.MenuController", "OnClick", 12));
            // A second, DIFFERENT missing binding on the same asset — must not
            // collapse onto the first one's key.
            data.MissingMethods.Add(new MissingMethodEntry("Game.Ui.MenuController", "OnClose", 30));
            // User-controlled names can carry '|' (read from asset YAML) —
            // must be sanitized, not throw at IssueKey.Build time.
            data.MissingMethods.Add(new MissingMethodEntry("Weird|Class", "Do|Thing", 44));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            Assert.DoesNotThrow(() => IssueMapper.MapToIssues(new List<AssetData> { asset }, sink));

            var codes = sink.Where(i => IssueKey.BareIssueCode(i.IssueCode) == "missing_method")
                            .Select(i => i.IssueCode).ToList();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "missing_method:Game.Ui.MenuController:OnClick",
                    "missing_method:Game.Ui.MenuController:OnClose",
                    "missing_method:Weird_Class:Do_Thing",
                },
                codes);
            // Raw (unsanitized) names stay in evidence for inspection.
            var weird = sink.Single(i => i.IssueCode == "missing_method:Weird_Class:Do_Thing");
            Assert.AreEqual("Weird|Class", weird.Evidence["className"]);
            Assert.AreEqual("Do|Thing", weird.Evidence["methodName"]);
        }

        [Test]
        public void TypeMismatch_Discriminator_IsTypeName_Sanitized()
        {
            var data = new AssetReferencesData();
            data.TypeMismatches.Add(new TypeMismatchEntry("Game.MissingArgType", 9));
            data.TypeMismatches.Add(new TypeMismatchEntry("Other.MissingArgType", 21));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            IssueMapper.MapToIssues(new List<AssetData> { asset }, sink);

            var codes = sink.Where(i => IssueKey.BareIssueCode(i.IssueCode) == "type_mismatch")
                            .Select(i => i.IssueCode).ToList();
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "type_mismatch:Game.MissingArgType",
                    "type_mismatch:Other.MissingArgType",
                },
                codes);
        }

        [Test]
        public void MissingLocalFileID_Discriminator_IsFileId_NotLine()
        {
            var data = new AssetReferencesData();
            // IdValid requires Id > 0; LocalUsagesCount == 0 and
            // !ExistsInAssets are the defaults, which is the issue shape.
            data.LocalReferences.Add(new LocalReferenceRegistry(4100001, 17));
            data.LocalReferences.Add(new LocalReferenceRegistry(4100002, 90));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            IssueMapper.MapToIssues(new List<AssetData> { asset }, sink);

            var codes = sink.Where(i => IssueKey.BareIssueCode(i.IssueCode) == "missing_local_fileid")
                            .Select(i => i.IssueCode).ToList();
            CollectionAssert.AreEquivalent(
                new[] { "missing_local_fileid:4100001", "missing_local_fileid:4100002" },
                codes);
            // The (unstable) line is evidence-only, never part of the key.
            Assert.IsFalse(codes.Any(c => c.Contains(":17") || c.Contains(":90")),
                "line numbers must not leak into the discriminator");
        }

        [Test]
        public void EmptyLocalRef_Discriminator_IsOrdinal_CountVisibleToDelta()
        {
            // The scanner record carries only the line (a {fileID: 0} has no
            // identifying payload), so the key uses the ordinal among the
            // asset's empty refs: two instances → two distinct keys, and a
            // third appearing surfaces as exactly one NEW key (:2) in the
            // gate delta instead of collapsing into the existing one.
            var data = new AssetReferencesData();
            data.EmptyFileIDs.Add(new EmptyLocalFileIDRegistry(5));
            data.EmptyFileIDs.Add(new EmptyLocalFileIDRegistry(99));
            var asset = new AssetData("Assets/A.prefab", typeof(object), "Prefab", "guid", data);

            var sink = new List<VerifyIssue>();
            IssueMapper.MapToIssues(new List<AssetData> { asset }, sink);

            var codes = sink.Where(i => IssueKey.BareIssueCode(i.IssueCode) == "empty_local_ref")
                            .Select(i => i.IssueCode).ToList();
            CollectionAssert.AreEquivalent(
                new[] { "empty_local_ref:0", "empty_local_ref:1" },
                codes);
        }

        // ---- helpers ----

        private static VerifyIssue AssertSingle(List<VerifyIssue> sink, string bareCode)
        {
            var matches = sink.Where(i => IssueKey.BareIssueCode(i.IssueCode) == bareCode).ToList();
            Assert.AreEqual(1, matches.Count,
                $"expected exactly one {bareCode} issue, got {sink.Count} total: " +
                string.Join(", ", sink.Select(i => i.IssueCode)));
            return matches[0];
        }
    }
}
