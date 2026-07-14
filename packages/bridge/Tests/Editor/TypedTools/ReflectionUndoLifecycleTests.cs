// M30-polish Plan 4 — Undo / lifecycle EditMode tests for the reflection
// object_modify path and the jsonPatchesPerGameObject surface of
// gameobject_modify.
//
// Covers:
//   T4.3 — object_modify records Undo before reflection writes; a single
//          Undo.PerformUndo() reverts the patch. Also covers the half-undoable
//          gameobject_modify case (root diff undoable, component jsonPatches
//          were NOT before the fix).
//
// Reuses Plan5TestScriptableObject (defined in ScriptableObjectAsmdefToolsTests)
// as the reflection target — it has public float/string/int fields the
// ApplyFieldPatches path writes via reflection.
using NUnit.Framework;
using UnityEditor;
using UnityOpenMcpBridge.ObjectRefs;
using UnityOpenMcpBridge.TypedTools;
using UnityEngine;

namespace UnityOpenMcpBridge.Tests
{
    public class ReflectionUndoLifecycleTests
    {
        // T4.3 — object_modify on a ScriptableObject field must be undoable.
        // Before the fix ApplyFieldPatches called SetValue with no preceding
        // Undo.RecordObject, so Ctrl+Z silently skipped reflection patches.
        [Test]
        public void ObjectModify_RecordsUndo_PerformUndoRevertsPatch()
        {
            var so = ScriptableObject.CreateInstance<Plan5TestScriptableObject>();
            so.score = 1f;
            so.label = "before";
            try
            {
                var result = ReflectionScriptsObjectsTools.ObjectModify(
                    "{\"instance_id\":" + InstanceId.Of(so) +
                    ",\"fields\":[" +
                    "{\"name\":\"score\",\"value\":9.5}," +
                    "{\"name\":\"label\",\"value\":\"after\"}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(9.5f, so.score, 0.0001f);
                Assert.AreEqual("after", so.label);

                Undo.PerformUndo();

                // A single undo reverts BOTH reflection patches to their
                // pre-patch values.
                Assert.AreEqual(1f, so.score, 0.0001f,
                    "Undo.PerformUndo must revert the object_modify score patch.");
                Assert.AreEqual("before", so.label,
                    "Undo.PerformUndo must revert the object_modify label patch.");
            }
            finally { Object.DestroyImmediate(so); }
        }

        // T4.3 — gameobject_modify's jsonPatchesPerGameObject surface delegates
        // into ApplyFieldPatches. Before the fix the root diff was undoable but
        // the component jsonPatches were NOT (half-undoable). Now both the root
        // diff and the component patch revert together.
        [Test]
        public void GameObjectModify_JsonPatches_AreUndoable()
        {
            var go = new GameObject("__MCPTest_GO_JsonPatch_Undo");
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 1f;
            var preName = go.name;
            try
            {
                var result = GameObjectsTools.Modify(
                    "{\"instance_id\":" + InstanceId.Of(go) +
                    ",\"gameObjectDiffs\":{\"name\":\"__MCPTest_GO_JsonPatch_Undo_Renamed\"}" +
                    ",\"jsonPatchesPerGameObject\":{\"Rigidbody\":{\"mass\":7.5}}}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(7.5f, rb.mass, 0.0001f);
                Assert.AreEqual("__MCPTest_GO_JsonPatch_Undo_Renamed", go.name);

                // Undo reverts both surfaces. The number of undo steps depends
                // on how many RecordObject calls landed; loop a bounded number
                // of times to clear the root diff + the jsonPatch record.
                for (int i = 0; i < 4; i++)
                {
                    if (go.name == preName && Mathf.Approximately(rb.mass, 1f)) break;
                    Undo.PerformUndo();
                }

                Assert.AreEqual(preName, go.name,
                    "Undo must revert the root diff name change.");
                Assert.AreEqual(1f, rb.mass, 0.0001f,
                    "Undo must revert the jsonPatchesPerGameObject component patch.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // T4.6 — object_modify with an invalid enum name must surface a clear
        // error listing allowed values and NOT mutate the field. Before the fix
        // Enum.Parse threw a cryptic ArgumentException and numeric strings
        // silently coerced to out-of-range garbage.
        [Test]
        public void ObjectModify_EnumInvalidName_ReportsErrorNoMutation()
        {
            var so = ScriptableObject.CreateInstance<EnumTestScriptableObject>();
            so.mode = TestEnum.A;
            try
            {
                var result = ReflectionScriptsObjectsTools.ObjectModify(
                    "{\"instance_id\":" + InstanceId.Of(so) +
                    ",\"fields\":[{\"name\":\"mode\",\"value\":\"__DefinitelyNotAnEnumValue__\"}]}");
                // The per-entry error is accumulated; the dispatch succeeds.
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"errors\":", result.Output);
                StringAssert.Contains("TestEnum", result.Output,
                    "Error must name the enum type and list allowed values.");
                // The invalid value must NOT have mutated the field.
                Assert.AreEqual(TestEnum.A, so.mode,
                    "Invalid enum name must not mutate the field.");
            }
            finally { Object.DestroyImmediate(so); }
        }

        // T4.6 — object_modify with an out-of-range numeric enum value must be
        // rejected (not silently coerced to garbage).
        [Test]
        public void ObjectModify_EnumOutOfRangeNumeric_ReportsErrorNoMutation()
        {
            var so = ScriptableObject.CreateInstance<EnumTestScriptableObject>();
            so.mode = TestEnum.B;
            try
            {
                // 42 is out of range for a 3-value enum (A=0,B=1,C=2).
                var result = ReflectionScriptsObjectsTools.ObjectModify(
                    "{\"instance_id\":" + InstanceId.Of(so) +
                    ",\"fields\":[{\"name\":\"mode\",\"value\":42}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                StringAssert.Contains("\"errors\":", result.Output);
                Assert.AreEqual(TestEnum.B, so.mode,
                    "Out-of-range numeric enum value must not mutate the field.");
            }
            finally { Object.DestroyImmediate(so); }
        }

        // T4.6 — object_modify with a valid enum name sets the value correctly.
        [Test]
        public void ObjectModify_EnumValidName_SetsValue()
        {
            var so = ScriptableObject.CreateInstance<EnumTestScriptableObject>();
            so.mode = TestEnum.A;
            try
            {
                var result = ReflectionScriptsObjectsTools.ObjectModify(
                    "{\"instance_id\":" + InstanceId.Of(so) +
                    ",\"fields\":[{\"name\":\"mode\",\"value\":\"C\"}]}");
                Assert.IsTrue(result.Success, result.ErrorMessage);
                Assert.AreEqual(TestEnum.C, so.mode);
            }
            finally { Object.DestroyImmediate(so); }
        }
    }

    // Test fixture SO with an enum field exercising the ConvertValue enum path.
    public class EnumTestScriptableObject : ScriptableObject
    {
        public TestEnum mode = TestEnum.A;
    }

    public enum TestEnum
    {
        A = 0,
        B = 1,
        C = 2,
    }
}
