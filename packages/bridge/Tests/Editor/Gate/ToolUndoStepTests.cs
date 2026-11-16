using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.Tests
{
    // One mutating bridge call is one undo step, as one user action is.
    // Bridge calls arrive with no input event between them, so before the
    // dispatch opened a group per call, every call since the last click shared
    // Unity's current group and one Undo reverted all of them.
    [TestFixture]
    public class ToolUndoStepTests
    {
        private const string Prefix = "__MCPTest_UndoStep_";
        private const string Scope = "\"paths_hint\":[\"Assets/__ToolUndoStepTest.unity\"]";

        [OneTimeSetUp]
        public void EnsureTypedToolRegistry()
        {
            if (BridgeToolRegistry.Count == 0) BridgeToolRegistry.Scan();
        }

        [TearDown]
        public void DestroyFixtures()
        {
            foreach (var go in SceneQuery.FindAllOfType<GameObject>(SceneQuery.Inactive.Include))
                if (go != null && go.name.StartsWith(Prefix)) Object.DestroyImmediate(go);
        }

        // The top-level gated dispatch (what an HTTP tool call runs, with the
        // body's paths_hint already extracted).
        private static GateDispatchResult Dispatch(string tool, string body) => (GateDispatchResult)
            typeof(BridgeHttpServer).GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { tool, body, "off", new[] { "Assets/__ToolUndoStepTest.unity" } });

        private static void Create(string name)
        {
            var r = Dispatch("unity_open_mcp_gameobject_create", "{\"name\":\"" + Prefix + name + "\"," + Scope + "}");
            Assert.IsTrue(r.Mutation.Success, r.Mutation.ErrorMessage);
        }

        private static bool Exists(string name) => GameObject.Find(Prefix + name) != null;

        // The undo steps by name, most recent first, as editor_undo_history
        // lists them.
        private static List<string> UndoSteps()
        {
            var all = new List<string>();
            Assert.IsTrue(UndoStack.TryReadSteps(all, out int cursor), "this Editor lists undo steps");
            var undo = new List<string>();
            for (int i = cursor; i >= 0; i--) undo.Add(all[i]);
            return undo;
        }

        // The redo steps by name, next to redo first.
        private static List<string> RedoSteps()
        {
            var all = new List<string>();
            Assert.IsTrue(UndoStack.TryReadSteps(all, out int cursor), "this Editor lists undo steps");
            return all.GetRange(cursor + 1, all.Count - cursor - 1);
        }

        [Test]
        public void ConsecutiveCalls_OneUndo_RevertsOnlyTheLastCall_RedoRestoresIt()
        {
            Create("A");
            Create("B");
            var rename = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"path\":\"" + Prefix + "A\",\"name\":\"" + Prefix + "A2\"," + Scope + "}");
            Assert.IsTrue(rename.Mutation.Success, rename.Mutation.ErrorMessage);

            Undo.PerformUndo();
            Assert.IsTrue(Exists("A") && !Exists("A2"), "the rename is undone");
            Assert.IsTrue(Exists("B"), "the call before it is not");

            Undo.PerformUndo();
            Assert.IsFalse(Exists("B"), "the next undo reverts the create of B");
            Assert.IsTrue(Exists("A"), "and only that");

            Undo.PerformRedo();
            Assert.IsTrue(Exists("B"));
            Assert.IsTrue(Exists("A"));
        }

        [Test]
        public void MutatingCall_NamesItsUndoStepAfterTheTool()
        {
            // A step recorded first leaves nothing to redo (an earlier test's
            // undo would), so the next call is named up front.
            Create("BeforeNamed");
            Create("Named");
            Assert.AreEqual("MCP gameobject_create", UndoSteps()[0]);
        }

        [Test]
        public void ReadOnlyCall_OpensNoUndoGroup()
        {
            Create("Read");
            int group = Undo.GetCurrentGroup();
            var find = Dispatch("unity_open_mcp_gameobject_find", "{\"name\":\"" + Prefix + "Read\"}");
            Assert.IsTrue(find.Mutation.Success, find.Mutation.ErrorMessage);
            Assert.IsTrue(find.EffectiveReadOnly);
            Assert.AreEqual(group, Undo.GetCurrentGroup());

            Undo.PerformUndo();
            Assert.IsFalse(Exists("Read"), "the undo still reverts the last mutating call");
        }

        [Test]
        public void Batch_IsOneUndoStep_SeparateFromTheCallBefore()
        {
            Create("BeforeBatch");
            var batch = Dispatch("unity_open_mcp_batch_execute",
                "{\"commands\":[" +
                "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + Prefix + "B1\"}}," +
                "{\"tool\":\"unity_open_mcp_gameobject_create\",\"params\":{\"name\":\"" + Prefix + "B2\"}}" +
                "]," + Scope + "}");
            Assert.IsTrue(batch.Mutation.Success, batch.Mutation.ErrorMessage);
            Assert.AreEqual("MCP batch_execute", UndoSteps()[0]);
            Assert.AreEqual("MCP gameobject_create", UndoSteps()[1]);

            Undo.PerformUndo();
            Assert.IsFalse(Exists("B1"), "one undo reverts the whole batch");
            Assert.IsFalse(Exists("B2"));
            Assert.IsTrue(Exists("BeforeBatch"), "but not the call before it");
        }

        [Test]
        public void Duplicate_IsOneUndoStep_ThroughTheSharedPath()
        {
            Create("Dup");
            var dup = Dispatch("unity_open_mcp_gameobject_duplicate",
                "{\"path\":\"" + Prefix + "Dup\"," + Scope + "}");
            Assert.IsTrue(dup.Mutation.Success, dup.Mutation.ErrorMessage);
            Assert.AreEqual(2, CountNamed("Dup"));

            Undo.PerformUndo();
            Assert.AreEqual(1, CountNamed("Dup"), "one undo removes the copy (and the rename it joined)");
        }

        [Test]
        public void RefusedCall_LeavesNoUndoStep()
        {
            Create("BeforeRefusal");
            var before = UndoSteps();
            var refused = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"path\":\"" + Prefix + "Missing\",\"name\":\"x\"," + Scope + "}");
            Assert.IsFalse(refused.Mutation.Success);
            CollectionAssert.AreEqual(before, UndoSteps(), "the history lists no step for the refusal");

            Undo.PerformUndo();
            Assert.IsFalse(Exists("BeforeRefusal"), "the undo reverts the call before the refusal");
        }

        // Unity's group name is a record of its own; a group holding only it
        // was listed as a step, and one Undo dropped it and reverted the step
        // before it as well.
        [Test]
        public void CallRecordingNothing_LeavesNoStep()
        {
            Create("BeforeNoop");
            var before = UndoSteps();
            ToolUndoStep.Run("unity_open_mcp_noop_probe", () => 0);
            CollectionAssert.AreEqual(before, UndoSteps(), "no step for a call that recorded nothing");

            Undo.PerformUndo();
            Assert.IsFalse(Exists("BeforeNoop"), "one undo reverts the call before, and only it");
        }

        [Test]
        public void CallRecordingNothing_KeepsTheRedoEntries()
        {
            Create("RA");
            Create("RB");
            Undo.PerformUndo();
            Assert.IsFalse(Exists("RB"));

            var refused = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"path\":\"" + Prefix + "Missing\",\"name\":\"x\"," + Scope + "}");
            Assert.IsFalse(refused.Mutation.Success);
            Assert.AreEqual("MCP gameobject_create", RedoSteps()[0], "the refusal left the redo entry in place");

            Undo.PerformRedo();
            Assert.IsTrue(Exists("RB"));
        }

        // With something to redo the step is not named up front (the name
        // would clear the redo entries even for a call that records nothing):
        // it reads its first record's name.
        [Test]
        public void CallAfterAnUndo_IsOneStep_NamedAfterItsFirstRecord()
        {
            Create("UA");
            Create("UB");
            Undo.PerformUndo();
            Create("UC");
            Assert.AreEqual(0, RedoSteps().Count);
            Assert.AreEqual("MCP Create GameObject", UndoSteps()[0]);

            Undo.PerformUndo();
            Assert.IsFalse(Exists("UC"));
            Assert.IsTrue(Exists("UA"), "the undo reverts only the last call");
        }

        [Test]
        public void CallOpeningGroupsOfItsOwn_IsOneStep()
        {
            Create("OuterGroups");
            ToolUndoStep.Run("unity_open_mcp_groups_probe", () =>
            {
                Undo.IncrementCurrentGroup();
                var a = new GameObject(Prefix + "G1");
                Undo.RegisterCreatedObjectUndo(a, "Create G1");
                Undo.IncrementCurrentGroup();
                var b = new GameObject(Prefix + "G2");
                Undo.RegisterCreatedObjectUndo(b, "Create G2");
                return 0;
            });

            Undo.PerformUndo();
            Assert.IsFalse(Exists("G1") || Exists("G2"), "one undo reverts every group the call opened");
            Assert.IsTrue(Exists("OuterGroups"));
        }

        // RecordObject edits become records when flushed; the step closes
        // with the call, so they are flushed into it.
        [Test]
        public void RecordObjectEdit_JoinsTheCallsStep()
        {
            Create("Recorded");
            var go = GameObject.Find(Prefix + "Recorded");
            ToolUndoStep.Run("unity_open_mcp_record_probe", () =>
            {
                Undo.RecordObject(go.transform, "Move");
                go.transform.position = new Vector3(5f, 0f, 0f);
                return 0;
            });
            Assert.AreEqual("MCP record_probe", UndoSteps()[0]);

            Undo.PerformUndo();
            Assert.AreEqual(Vector3.zero, go.transform.position);
            Assert.IsTrue(Exists("Recorded"), "the step before is untouched");
        }

        private static int CountNamed(string name)
        {
            int n = 0;
            foreach (var go in SceneQuery.FindAllOfType<GameObject>())
                if (go.name == Prefix + name) n++;
            return n;
        }
    }
}
