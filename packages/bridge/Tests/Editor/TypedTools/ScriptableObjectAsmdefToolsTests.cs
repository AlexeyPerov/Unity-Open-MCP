// M20 Plan 5 / T20.5 — ScriptableObject + Assembly Definition EditMode tests.
//
// Covers the two core tool sets shipped in Plan 5:
//   - scriptableobject_create (mutating) + list_assets_of_type (read-only)
//   - asmdef_list / asmdef_get (read-only) + asmdef_create / asmdef_modify
//     (mutating, RestartThenSettle)
//
// The ScriptableObject create path needs a concrete SO type that is already
// compiled in. Rather than depend on a specific Unity built-in SO (whose base
// class can vary across versions), the suite defines its own
// Plan5TestScriptableObject below — the type resolver scans all loaded
// assemblies, so a type authored in the test assembly resolves by name. The
// asmdef tests write a real .asmdef under a temp folder, parse it back, and
// clean up in TearDown. Lifecycle + classification wiring is asserted against
// ToolLifecycle / BridgeToolClassification so the gate plumbing stays in sync.
using NUnit.Framework;
using UnityEditor;
using UnityOpenMcpBridge;
using UnityOpenMcpBridge.TypedTools;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    // Concrete ScriptableObject used by the create + list tests below. The
    // public fields exercise the initial-patch path (float + string + enum).
    public class Plan5TestScriptableObject : ScriptableObject
    {
        public float score = 0f;
        public string label = "";
        public int count = 0;
    }

    public class ScriptableObjectAsmdefToolsTests
    {
        private const string TmpRoot = "Assets/TmpPlan5Tests";
        private const string TestSoType = "UnityOpenMcpBridge.Tests.Plan5TestScriptableObject";

        [SetUp]
        public void EnsureTmpRoot()
        {
            if (!AssetDatabase.IsValidFolder(TmpRoot))
                AssetDatabase.CreateFolder("Assets", "TmpPlan5Tests");
        }

        [TearDown]
        public void CleanTmpRoot()
        {
            if (AssetDatabase.IsValidFolder(TmpRoot))
            {
                AssetDatabase.DeleteAsset(TmpRoot);
                AssetDatabase.Refresh();
            }
        }

        // -----------------------------------------------------------------
        // Classification + lifecycle wiring (keeps the gate plumbing in sync).
        // -----------------------------------------------------------------

        [Test]
        public void Classification_AllSixToolsKnown()
        {
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_scriptableobject_create"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_list_assets_of_type"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_asmdef_list"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_asmdef_get"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_asmdef_create"));
            Assert.IsTrue(BridgeToolClassification.KnownTools.Contains("unity_open_mcp_asmdef_modify"));
        }

        [Test]
        public void Classification_ReadOnlyToolsAreDirectResponse()
        {
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_list_assets_of_type"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_asmdef_list"));
            Assert.IsTrue(BridgeToolClassification.DirectResponseTools.Contains("unity_open_mcp_asmdef_get"));
        }

        [Test]
        public void Classification_MutatingToolsRequireGate()
        {
            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_scriptableobject_create"));
            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_asmdef_create"));
            Assert.IsTrue(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_asmdef_modify"));
            // Read-only tools must NOT be mutating.
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_list_assets_of_type"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_asmdef_list"));
            Assert.IsFalse(BridgeToolClassification.MutatingTools.Contains("unity_open_mcp_asmdef_get"));
        }

        [Test]
        public void Lifecycle_ScriptableObjectCreateIsEditorSettle()
        {
            Assert.AreEqual(LifecyclePolicy.EditorSettle,
                ToolLifecycle.Resolve("unity_open_mcp_scriptableobject_create"));
            Assert.IsTrue(ToolLifecycle.RequiresSettleWait(
                ToolLifecycle.Resolve("unity_open_mcp_scriptableobject_create")));
        }

        [Test]
        public void Lifecycle_AsmdefCreateModifyAreRestartThenSettle()
        {
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_asmdef_create"));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle,
                ToolLifecycle.Resolve("unity_open_mcp_asmdef_modify"));
            // RestartThenSettle ops are preflighted by the active-scene dirty guard.
            Assert.IsTrue(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_asmdef_create"));
            Assert.IsTrue(ToolLifecycle.RequiresDirtyGuard("unity_open_mcp_asmdef_modify"));
        }

        [Test]
        public void Lifecycle_ReadOnlyToolsAreNone()
        {
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_list_assets_of_type"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_asmdef_list"));
            Assert.AreEqual(LifecyclePolicy.None,
                ToolLifecycle.Resolve("unity_open_mcp_asmdef_get"));
        }

        // -----------------------------------------------------------------
        // ScriptableObject create — parameter validation.
        // -----------------------------------------------------------------

        [Test]
        public void ScriptableObjectCreate_MissingTypeName_ReturnsMissingParameter()
        {
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'type_name'", result.ErrorMessage);
        }

        [Test]
        public void ScriptableObjectCreate_MissingAssetPath_ReturnsMissingParameter()
        {
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'asset_path'", result.ErrorMessage);
        }

        [Test]
        public void ScriptableObjectCreate_NotAssetsRooted_ReturnsInvalidPaths()
        {
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"Foo/Foo.asset\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("'Assets/'", result.ErrorMessage);
        }

        [Test]
        public void ScriptableObjectCreate_NotDotAsset_ReturnsInvalidPaths()
        {
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"Assets/Foo.png\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("'.asset'", result.ErrorMessage);
        }

        [Test]
        public void ScriptableObjectCreate_UnknownType_ReturnsTypeNotFound()
        {
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"MyNamespace.DoesNotExist__\",\"asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("type_not_found", result.ErrorCode);
            StringAssert.Contains("find_members", result.ErrorMessage);
        }

        [Test]
        public void ScriptableObjectCreate_NonScriptableObjectType_ReturnsTypeNotScriptableObject()
        {
            // UnityEngine.Transform is NOT a ScriptableObject subclass.
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"UnityEngine.Transform\",\"asset_path\":\"Assets/Foo.asset\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("type_not_scriptableobject", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // ScriptableObject create — happy path.
        // -----------------------------------------------------------------

        [Test]
        public void ScriptableObjectCreate_TestSo_WritesAsset()
        {
            var path = TmpRoot + "/SO.asset";
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"assetPath\":\"" + path + "\"", result.Output);
            // The asset must exist on disk + be loadable as the test SO type.
            var loaded = AssetDatabase.LoadAssetAtPath<Plan5TestScriptableObject>(path);
            Assert.IsNotNull(loaded, "Created asset should load as the test ScriptableObject type.");
        }

        [Test]
        public void ScriptableObjectCreate_ExistingPath_ReturnsAssetExists()
        {
            var path = TmpRoot + "/Existing.asset";
            // First create succeeds.
            var first = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(first.Success, first.ErrorMessage);
            // Second create on the same path must refuse.
            var second = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"" + path + "\"}");
            Assert.IsFalse(second.Success);
            Assert.AreEqual("asset_exists", second.ErrorCode);
        }

        [Test]
        public void ScriptableObjectCreate_WithFields_AppliesInitialPatches()
        {
            var path = TmpRoot + "/WithFields.asset";
            // Apply float + int + string public fields at create time (same value
            // shape + ConvertValue path as object_modify).
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\"," +
                "\"asset_path\":\"" + path + "\"," +
                "\"fields\":[" +
                "{\"name\":\"score\",\"value\":0.5}," +
                "{\"name\":\"count\",\"value\":7}," +
                "{\"name\":\"label\",\"value\":\"hello\"}]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"fieldsApplied\":3", result.Output);
            var loaded = AssetDatabase.LoadAssetAtPath<Plan5TestScriptableObject>(path);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0.5f, loaded.score);
            Assert.AreEqual(7, loaded.count);
            Assert.AreEqual("hello", loaded.label);
        }

        [Test]
        public void ScriptableObjectCreate_BadFieldName_ReportsErrorButStillCreates()
        {
            var path = TmpRoot + "/BadField.asset";
            var result = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\"," +
                "\"asset_path\":\"" + path + "\"," +
                "\"fields\":[{\"name\":\"score\",\"value\":1},{\"name\":\"__nope\",\"value\":1}]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"fieldsApplied\":1", result.Output);
            // The bad field surfaces in fieldErrors without aborting the create.
            StringAssert.Contains("\"fieldErrors\"", result.Output);
        }

        // -----------------------------------------------------------------
        // list_assets_of_type — parameter validation + read.
        // -----------------------------------------------------------------

        [Test]
        public void ListAssetsOfType_MissingTypeName_ReturnsMissingParameter()
        {
            var result = ReflectionScriptsObjectsTools.ListAssetsOfType("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'type_name'", result.ErrorMessage);
        }

        [Test]
        public void ListAssetsOfType_FindsCreatedAsset()
        {
            var path = TmpRoot + "/ListTarget.asset";
            var created = ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(created.Success, created.ErrorMessage);

            var result = ReflectionScriptsObjectsTools.ListAssetsOfType(
                "{\"type_name\":\"" + TestSoType + "\",\"folder\":\"" + TmpRoot + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"path\":\"" + path + "\"", result.Output);
            StringAssert.Contains("\"count\":1", result.Output);
            StringAssert.Contains("\"resolvedType\":\"" + TestSoType + "\"", result.Output);
        }

        [Test]
        public void ListAssetsOfType_RespectsMaxResults()
        {
            // Create two test SOs so max_results:1 truncates the second.
            ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"" + TmpRoot + "/A.asset\"}");
            ReflectionScriptsObjectsTools.ScriptableObjectCreate(
                "{\"type_name\":\"" + TestSoType + "\",\"asset_path\":\"" + TmpRoot + "/B.asset\"}");

            var result = ReflectionScriptsObjectsTools.ListAssetsOfType(
                "{\"type_name\":\"" + TestSoType + "\",\"folder\":\"" + TmpRoot + "\",\"max_results\":1}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"count\":1", result.Output);
            StringAssert.Contains("\"truncated\":1", result.Output);
            StringAssert.Contains("\"maxResults\":1", result.Output);
        }

        // -----------------------------------------------------------------
        // asmdef — parameter validation.
        // -----------------------------------------------------------------

        [Test]
        public void AsmdefGet_MissingPath_ReturnsMissingParameter()
        {
            var result = AssemblyDefinitionTools.Get("{}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
            StringAssert.Contains("'asset_path'", result.ErrorMessage);
        }

        [Test]
        public void AsmdefGet_NotAsmdefExtension_ReturnsInvalidPaths()
        {
            var result = AssemblyDefinitionTools.Get("{\"asset_path\":\"Assets/Foo.cs\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
            StringAssert.Contains("'.asmdef'", result.ErrorMessage);
        }

        [Test]
        public void AsmdefCreate_NotAssetsRooted_ReturnsInvalidPaths()
        {
            var result = AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"Foo/Bar.asmdef\",\"name\":\"Bar\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_paths", result.ErrorCode);
        }

        [Test]
        public void AsmdefCreate_MissingPath_ReturnsMissingParameter()
        {
            var result = AssemblyDefinitionTools.Create("{\"name\":\"Bar\"}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // -----------------------------------------------------------------
        // asmdef — create / get / modify round-trip.
        // -----------------------------------------------------------------

        [Test]
        public void AsmdefCreate_WritesValidAsmdef()
        {
            var path = TmpRoot + "/RoundTrip.asmdef";
            var result = AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.RoundTrip\"," +
                "\"references\":[\"Test.Other\"],\"define_constraints\":[\"UNITY_EDITOR\"]," +
                "\"root_namespace\":\"Test\",\"allow_unsafe\":true}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"status\":\"ok\"", result.Output);
            StringAssert.Contains("\"name\":\"Test.RoundTrip\"", result.Output);
            StringAssert.Contains("\"referenceCount\":1", result.Output);
            StringAssert.Contains("\"defineConstraintCount\":1", result.Output);
            StringAssert.Contains("recompile", result.Output);
            Assert.IsTrue(AssetDatabase.LoadAssetAtPath<Object>(path) != null,
                "Created .asmdef should be a tracked asset.");
        }

        [Test]
        public void AsmdefGet_ReadsBackCreatedAsmdef()
        {
            var path = TmpRoot + "/ReadBack.asmdef";
            var created = AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.ReadBack\"," +
                "\"references\":[\"Test.Dependency\"],\"include_platforms\":[\"Editor\"]," +
                "\"define_constraints\":[\"UNITY_EDITOR\"],\"auto_referenced\":false}");
            Assert.IsTrue(created.Success, created.ErrorMessage);

            var result = AssemblyDefinitionTools.Get("{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"name\":\"Test.ReadBack\"", result.Output);
            StringAssert.Contains("\"Test.Dependency\"", result.Output);
            StringAssert.Contains("\"Editor\"", result.Output);
            StringAssert.Contains("\"UNITY_EDITOR\"", result.Output);
            StringAssert.Contains("\"autoReferenced\":false", result.Output);
        }

        [Test]
        public void AsmdefModify_AddsAndRemovesReferences()
        {
            var path = TmpRoot + "/Modify.asmdef";
            AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.Modify\",\"references\":[\"Test.Original\"]}");

            // Add a reference.
            var addResult = AssemblyDefinitionTools.Modify(
                "{\"asset_path\":\"" + path + "\",\"add_references\":[\"Test.Added\"]}");
            Assert.IsTrue(addResult.Success, addResult.ErrorMessage);

            // Read back — both references should be present.
            var getAfterAdd = AssemblyDefinitionTools.Get("{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(getAfterAdd.Success);
            StringAssert.Contains("\"Test.Original\"", getAfterAdd.Output);
            StringAssert.Contains("\"Test.Added\"", getAfterAdd.Output);

            // Remove the original reference.
            var removeResult = AssemblyDefinitionTools.Modify(
                "{\"asset_path\":\"" + path + "\",\"remove_references\":[\"Test.Original\"]}");
            Assert.IsTrue(removeResult.Success, removeResult.ErrorMessage);

            // Read back — only the added reference remains.
            var getAfterRemove = AssemblyDefinitionTools.Get("{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(getAfterRemove.Success);
            StringAssert.Contains("\"Test.Added\"", getAfterRemove.Output);
            Assert.IsFalse(getAfterRemove.Output.Contains("\"Test.Original\""),
                "Removed reference should not appear in the model.");
        }

        [Test]
        public void AsmdefModify_SetPlatformsClearsCounterpart()
        {
            var path = TmpRoot + "/Platforms.asmdef";
            AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.Platforms\"," +
                "\"include_platforms\":[\"Editor\"]}");

            // Setting exclude_platforms must clear include_platforms.
            var result = AssemblyDefinitionTools.Modify(
                "{\"asset_path\":\"" + path + "\",\"exclude_platforms\":[\"Android\"]}");
            Assert.IsTrue(result.Success, result.ErrorMessage);

            var get = AssemblyDefinitionTools.Get("{\"asset_path\":\"" + path + "\"}");
            Assert.IsTrue(get.Success);
            StringAssert.Contains("\"Android\"", get.Output);
            StringAssert.Contains("\"includePlatforms\":[]", get.Output);
        }

        [Test]
        public void AsmdefModify_NonexistentPath_ReturnsNotFound()
        {
            var result = AssemblyDefinitionTools.Modify(
                "{\"asset_path\":\"Assets/TmpPlan5Tests/Nope.asmdef\",\"add_references\":[\"X\"]}");
            Assert.IsFalse(result.Success);
            Assert.AreEqual("asmdef_not_found", result.ErrorCode);
        }

        [Test]
        public void AsmdefCreate_ExistingPath_ReturnsExists()
        {
            var path = TmpRoot + "/Dup.asmdef";
            AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.Dup\"}");
            var second = AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.Dup\"}");
            Assert.IsFalse(second.Success);
            Assert.AreEqual("asmdef_exists", second.ErrorCode);
        }

        // -----------------------------------------------------------------
        // asmdef list.
        // -----------------------------------------------------------------

        [Test]
        public void AsmdefList_FindsCreatedAsmdef()
        {
            var path = TmpRoot + "/List.asmdef";
            AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.List\"}");

            var result = AssemblyDefinitionTools.List("{\"folder\":\"" + TmpRoot + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains("\"assetPath\":\"" + path + "\"", result.Output);
            StringAssert.Contains("\"name\":\"Test.List\"", result.Output);
            StringAssert.Contains("\"count\":", result.Output);
        }

        // Regression: specs/feedback.md 2026-07-03 — asmdef_list returned
        // bridge_response_unparsable because `autoReferenced` was emitted as the
        // C# Boolean.ToString() form ("True"/"False"), which is invalid JSON.
        // Assert the field serializes as a lowercase JSON boolean so the bug
        // cannot silently return. (The bridge has no JSON lib in the test
        // assembly, so we assert the literal token shape directly — this is the
        // exact regression.)
        [Test]
        public void AsmdefList_AutoReferencedSerializesAsLowercaseJsonBoolean()
        {
            var path = TmpRoot + "/JsonValidity.asmdef";
            // auto_referenced:false exercises the nullable-bool serialization path
            // that regressed (model?.AutoReferenced ?? true boxed to Append(object)).
            AssemblyDefinitionTools.Create(
                "{\"asset_path\":\"" + path + "\",\"name\":\"Test.JsonValidity\"," +
                "\"auto_referenced\":false}");

            var result = AssemblyDefinitionTools.List("{\"folder\":\"" + TmpRoot + "\"}");
            Assert.IsTrue(result.Success, result.ErrorMessage);

            // Must be the JSON literal `false`, never the C# "False".
            Assert.IsTrue(result.Output.Contains("\"autoReferenced\":false"),
                "autoReferenced must serialize as lowercase JSON boolean 'false'");
            Assert.IsFalse(result.Output.Contains("\"autoReferenced\":False"),
                "autoReferenced must NOT serialize as Boolean.ToString() 'False'");
            Assert.IsFalse(result.Output.Contains("\"autoReferenced\":True"),
                "autoReferenced must NOT serialize as Boolean.ToString() 'True'");
        }
    }
}
