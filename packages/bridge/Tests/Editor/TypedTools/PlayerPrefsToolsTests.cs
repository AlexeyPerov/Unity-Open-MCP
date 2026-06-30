using NUnit.Framework;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;

namespace UnityOpenMcpBridge.Tests
{
    // M20 Plan 9 / T20.9.2 — KV preferences (PlayerPrefs + EditorPrefs) round-
    // trip + classification tests. PlayerPrefs + EditorPrefs write to the
    // registry / Library/PlayerPreferences (NOT project assets), so they route
    // as gate-free direct-response mutators like editor_undo — the test
    // exercises the typed get/set/delete surface and the classification tables.
    public class PlayerPrefsToolsTests
    {
        // Use unique keys per fixture so concurrent / repeated runs do not
        // collide with real project preferences. Cleaned up in [TearDown].
        private const string PlayerKey = "__uom_test_playerprefs_key__";
        private const string EditorKey = "__uom_test_editorprefs_key__";

        [TearDown]
        public void TearDown()
        {
            UnityEngine.PlayerPrefs.DeleteKey(PlayerKey);
            UnityEditor.EditorPrefs.DeleteKey(EditorKey);
        }

        // ----------------------- PlayerPrefs round-trip -------------------

        [Test]
        public void PlayerPrefsSet_ThenGet_RoundTripsInt()
        {
            var setResult = PlayerPrefsTools.PlayerPrefsSet(
                $"{{\"key\":\"{PlayerKey}\",\"value\":42,\"type\":\"int\"}}");
            Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", setResult.Output);
            StringAssert.Contains("\"store\":\"playerprefs\"", setResult.Output);
            StringAssert.Contains("\"type\":\"int\"", setResult.Output);
            StringAssert.Contains("\"value\":42", setResult.Output);
            StringAssert.Contains("\"saved\":true", setResult.Output);

            var getResult = PlayerPrefsTools.PlayerPrefsGet(
                $"{{\"key\":\"{PlayerKey}\"}}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"int\"", getResult.Output);
            StringAssert.Contains("\"value\":42", getResult.Output);
        }

        [Test]
        public void PlayerPrefsSet_ThenGet_RoundTripsFloat()
        {
            var setResult = PlayerPrefsTools.PlayerPrefsSet(
                $"{{\"key\":\"{PlayerKey}\",\"value\":3.14,\"type\":\"float\"}}");
            Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"float\"", setResult.Output);

            var getResult = PlayerPrefsTools.PlayerPrefsGet(
                $"{{\"key\":\"{PlayerKey}\"}}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"float\"", getResult.Output);
            StringAssert.Contains("\"value\":3.14", getResult.Output);
        }

        [Test]
        public void PlayerPrefsSet_ThenGet_RoundTripsString()
        {
            var setResult = PlayerPrefsTools.PlayerPrefsSet(
                $"{{\"key\":\"{PlayerKey}\",\"value\":\"hello\",\"type\":\"string\"}}");
            Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"string\"", setResult.Output);

            var getResult = PlayerPrefsTools.PlayerPrefsGet(
                $"{{\"key\":\"{PlayerKey}\"}}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"string\"", getResult.Output);
            StringAssert.Contains("\"value\":\"hello\"", getResult.Output);
        }

        [Test]
        public void PlayerPrefsDelete_RemovesKey_AndReportsExisted()
        {
            PlayerPrefsTools.PlayerPrefsSet(
                $"{{\"key\":\"{PlayerKey}\",\"value\":1,\"type\":\"int\"}}");

            var deleteResult = PlayerPrefsTools.PlayerPrefsDelete(
                $"{{\"key\":\"{PlayerKey}\"}}");
            Assert.IsTrue(deleteResult.Success, deleteResult.ErrorMessage);
            StringAssert.Contains("\"deleted\":true", deleteResult.Output);
            StringAssert.Contains("\"existed\":true", deleteResult.Output);

            // Follow-up get fails with key_not_found.
            var getResult = PlayerPrefsTools.PlayerPrefsGet(
                $"{{\"key\":\"{PlayerKey}\"}}");
            Assert.IsFalse(getResult.Success);
            Assert.AreEqual("key_not_found", getResult.ErrorCode);
        }

        // ----------------------- EditorPrefs round-trip -------------------

        [Test]
        public void EditorPrefsSet_ThenGet_RoundTripsInt()
        {
            var setResult = PlayerPrefsTools.EditorPrefsSet(
                $"{{\"key\":\"{EditorKey}\",\"value\":7,\"type\":\"int\"}}");
            Assert.IsTrue(setResult.Success, setResult.ErrorMessage);
            StringAssert.Contains("\"store\":\"editorprefs\"", setResult.Output);
            StringAssert.Contains("\"type\":\"int\"", setResult.Output);
            StringAssert.Contains("\"value\":7", setResult.Output);

            var getResult = PlayerPrefsTools.EditorPrefsGet(
                $"{{\"key\":\"{EditorKey}\"}}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"int\"", getResult.Output);
            StringAssert.Contains("\"value\":7", getResult.Output);
        }

        [Test]
        public void EditorPrefsSet_ThenGet_RoundTripsFloat()
        {
            PlayerPrefsTools.EditorPrefsSet(
                $"{{\"key\":\"{EditorKey}\",\"value\":1.5,\"type\":\"float\"}}");

            var getResult = PlayerPrefsTools.EditorPrefsGet(
                $"{{\"key\":\"{EditorKey}\"}}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"float\"", getResult.Output);
            StringAssert.Contains("\"value\":1.5", getResult.Output);
        }

        [Test]
        public void EditorPrefsSet_ThenGet_RoundTripsString()
        {
            PlayerPrefsTools.EditorPrefsSet(
                $"{{\"key\":\"{EditorKey}\",\"value\":\"world\",\"type\":\"string\"}}");

            var getResult = PlayerPrefsTools.EditorPrefsGet(
                $"{{\"key\":\"{EditorKey}\"}}");
            Assert.IsTrue(getResult.Success, getResult.ErrorMessage);
            StringAssert.Contains("\"type\":\"string\"", getResult.Output);
            StringAssert.Contains("\"value\":\"world\"", getResult.Output);
        }

        [Test]
        public void EditorPrefsDelete_RemovesKey_AndReportsExisted()
        {
            PlayerPrefsTools.EditorPrefsSet(
                $"{{\"key\":\"{EditorKey}\",\"value\":1,\"type\":\"int\"}}");

            var deleteResult = PlayerPrefsTools.EditorPrefsDelete(
                $"{{\"key\":\"{EditorKey}\"}}");
            Assert.IsTrue(deleteResult.Success, deleteResult.ErrorMessage);
            StringAssert.Contains("\"deleted\":true", deleteResult.Output);
            StringAssert.Contains("\"existed\":true", deleteResult.Output);

            var getResult = PlayerPrefsTools.EditorPrefsGet(
                $"{{\"key\":\"{EditorKey}\"}}");
            Assert.IsFalse(getResult.Success);
            Assert.AreEqual("key_not_found", getResult.ErrorCode);
        }

        // ----------------------- Validation -------------------------------

        [Test]
        public void PlayerPrefsGet_MissingKey_ReturnsMissingParameter()
        {
            var result = PlayerPrefsTools.PlayerPrefsGet("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void PlayerPrefsSet_MissingValue_ReturnsMissingParameter()
        {
            var result = PlayerPrefsTools.PlayerPrefsSet(
                $"{{\"key\":\"{PlayerKey}\",\"type\":\"int\"}}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        [Test]
        public void PlayerPrefsSet_InvalidType_ReturnsInvalidType()
        {
            var result = PlayerPrefsTools.PlayerPrefsSet(
                $"{{\"key\":\"{PlayerKey}\",\"value\":1,\"type\":\"bool\"}}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_type", result.ErrorCode);
        }

        // ----------------------- Dispatch wiring --------------------------
        //
        // KnownTools / DirectResponseTools membership is the contract that lets
        // the dispatcher route a prefs tool. Prefs write no project assets, so
        // ALL six members (get/set/delete × 2) are gate-free direct-response
        // tools — mirrors editor_undo. We assert both sets so a future edit
        // that forgets to wire a new prefs tool fails loudly here. We also
        // assert the lifecycle (None — no settle, no dirty guard).

        [Test]
        public void KnownTools_ContainsAllSixPrefsTools()
        {
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_playerprefs_get"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_playerprefs_set"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_playerprefs_delete"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_editorprefs_get"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_editorprefs_set"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_editorprefs_delete"));
        }

        [Test]
        public void DirectResponseTools_ContainsAllSixPrefsTools_GateFree()
        {
            // Prefs write to the registry / Library, NOT project assets — the
            // gate has nothing to validate. All six route as gate-free direct-
            // response tools (the mutating nature is still visible in the
            // catalog via KnownTools membership).
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_playerprefs_get"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_playerprefs_set"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_playerprefs_delete"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_editorprefs_get"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_editorprefs_set"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_editorprefs_delete"));
        }

        [Test]
        public void MutatingTools_DoesNotContainPrefsTools()
        {
            // Confirms prefs are NOT in the gate-path mutator set — they do not
            // require paths_hint and do not run the checkpoint/validate/delta
            // gate (no asset scope to bind).
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_playerprefs_set"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_playerprefs_delete"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_editorprefs_set"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_editorprefs_delete"));
        }

        [Test]
        public void Lifecycle_AllPrefsToolsResolveToNone_NoSettle()
        {
            // Prefs write no assets and trigger no recompile — None (no settle,
            // no dirty guard), like editor_undo.
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_playerprefs_get"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_playerprefs_set"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_playerprefs_delete"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editorprefs_get"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editorprefs_set"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_editorprefs_delete"));
        }

        [Test]
        public void DirtyGuard_NeverPreflightsPrefsTools()
        {
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_playerprefs_set", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_playerprefs_delete", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_editorprefs_set", "{}"));
            Assert.IsFalse(SceneDirtyGuard.AppliesTo("unity_open_mcp_editorprefs_delete", "{}"));
        }
    }
}
