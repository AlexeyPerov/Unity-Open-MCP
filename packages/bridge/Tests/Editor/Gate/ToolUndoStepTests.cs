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
            Create("Named");
            Assert.AreEqual("MCP gameobject_create", Undo.GetCurrentGroupName());
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
            Assert.AreEqual("MCP batch_execute", Undo.GetCurrentGroupName());

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
            var refused = Dispatch("unity_open_mcp_gameobject_modify",
                "{\"path\":\"" + Prefix + "Missing\",\"name\":\"x\"," + Scope + "}");
            Assert.IsFalse(refused.Mutation.Success);

            Undo.PerformUndo();
            Assert.IsFalse(Exists("BeforeRefusal"), "the undo reverts the call before the refusal");
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
