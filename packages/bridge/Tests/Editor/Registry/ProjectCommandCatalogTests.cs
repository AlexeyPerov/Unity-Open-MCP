using System;
using System.Linq;
using NUnit.Framework;

namespace UnityOpenMcpBridge.Tests
{
    public class ProjectCommandCatalogTests
    {
        [BridgeToolType]
        public static class DiscoveredFixture
        {
            [ProjectCommand("project.tests.discovered", Title = "Discovered", Description = "Scan fixture", Package = "tests")]
            public static string Read(int value = 1) => "{}";
            [BridgeTool("project.tests.reserved")]
            public static string WrongAttribute() => "{}";
        }
        [Test] public void RegistryScanUsesProjectDeclarationsButNeverAddsThemToDirectDispatch()
        {
            BridgeToolRegistry.Scan(includeTestAssemblies: true);
            Assert.IsTrue(ProjectCommandCatalog.TryGet("project.tests.discovered", out var entry));
            Assert.AreEqual("Read", entry.Method.Name);
            Assert.IsFalse(BridgeToolRegistry.Contains("project.tests.discovered"));
            Assert.IsFalse(BridgeToolRegistry.Contains("project.tests.reserved"));
            BridgeToolRegistry.Scan();
            Assert.IsFalse(ProjectCommandCatalog.TryGet("project.tests.discovered", out _));
        }
        public enum Choice { Zebra, Apple }
        public static string Contract(string text, bool flag, int count, long instanceId, float weight, double precision,
            Choice choice, int? nullable, string[] strings, bool[] flags, int[] counts, long[] ids, float[] weights,
            double[] doubles, Choice[] choices, int?[] nullables,
            [ProjectCommandParameter(Description = "Bounded count", Minimum = 1, Maximum = 4, Examples = new[] { "2" }, DeprecatedAliases = new[] { "oldCount" })] int bounded = 2,
            string optional = null) => "{}";
        public static string Unsupported(System.Collections.Generic.Dictionary<string, int> data) => "{}";
        public static string Jagged(int[][] data) => "{}";
        public static string Ref(ref int value) => "{}";
        [Flags] public enum Bits { One = 1, Two = 2 }
        public static string Flags(Bits value) => "{}";
        public static string Matrix(int[,] values) => "{}";
        public static string BadDefault([ProjectCommandParameter(Minimum = 1)] int value = 0) => "{}";
        public static string Empty() => "{}";

        private static ProjectCommandCatalog.Entry Entry(string method = "Contract", string id = "project.tests.contract", bool enabled = true, string[] aliases = null)
            => ProjectCommandCatalog.Create(typeof(ProjectCommandCatalogTests).GetMethod(method), new ProjectCommandAttribute(id)
            { Title = "Contract", Description = "Schema contract", Package = "tests", Group = "fixtures", Tags = new[] { "test" },
                Enabled = enabled, ReadOnlyHint = true, Gate = GateMode.Off, DeprecatedAliases = aliases ?? Array.Empty<string>() });

        [TearDown] public void Restore() => BridgeToolRegistry.Scan();

        public static string Snapshot(int count = 2) => "{}";
        [Test] public void MinimalSchemaSnapshotPinsPublicVocabulary()
        {
            Assert.AreEqual("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"x-project-command\":true,\"additionalProperties\":false,\"properties\":{\"count\":{\"allOf\":[{\"type\":\"integer\",\"minimum\":-2147483648,\"maximum\":2147483647}],\"default\":2}},\"required\":[]}",
                Entry("Snapshot").Schema);
        }
        [Test] public void EverySupportedShapeHasDeterministicSchema()
        {
            var e = Entry();
            Assert.IsNull(e.Code, e.Message);
            Assert.IsTrue(BridgeJson.IsCompleteJson(e.Schema));
            Assert.AreEqual(e.Schema, Entry().Schema);
            var properties = JsonBody.GetRawValue(e.Schema, "properties");
            CollectionAssert.AreEquivalent(typeof(ProjectCommandCatalogTests).GetMethod("Contract").GetParameters().Select(p => p.Name), JsonBody.GetObjectKeys(properties));
            var required = JsonBody.GetStringArray(e.Schema, "required");
            Assert.AreEqual(16, required.Length);
            CollectionAssert.Contains(required, "nullable");
            CollectionAssert.DoesNotContain(required, "optional");
            StringAssert.Contains("\"enum\":[\"Apple\",\"Zebra\"]", e.Schema);
            StringAssert.Contains("\"default\":2", e.Schema);
            StringAssert.Contains("\"default\":null", e.Schema);
            StringAssert.Contains("\"minimum\":1", e.Schema);
            StringAssert.Contains("\"examples\":[2]", e.Schema);
            StringAssert.Contains("oldCount", e.Schema);
            foreach (var type in new[] { typeof(string), typeof(bool), typeof(int), typeof(long), typeof(float), typeof(double), typeof(Choice), typeof(int?) })
            {
                Assert.IsTrue(BridgeJson.IsCompleteJson(ProjectCommandSchema.TypeSchema(type)));
                StringAssert.Contains(ProjectCommandSchema.TypeSchema(type), ProjectCommandSchema.TypeSchema(type.MakeArrayType()));
            }
        }
        [TestCase("Unsupported")][TestCase("Jagged")][TestCase("Ref")][TestCase("Flags")][TestCase("Matrix")][TestCase("BadDefault")]
        public void UnsupportedSignaturesHaveMethodDiagnostics(string method)
        {
            ProjectCommandCatalog.Publish(new[] { Entry(method) });
            var result = ProjectCommandCatalog.Query("describe", "project.tests.contract");
            StringAssert.Contains("invalid_command_declaration", result);
            StringAssert.Contains(method, result);
            StringAssert.Contains("\"available\":false", result);
        }
        [Test] public void CollisionsAndAliasesRejectAllCandidatesRegardlessOfOrder()
        {
            var a = Entry("Empty", "project.tests.a", aliases: new[] { "project.tests.contract" });
            var b = Entry();
            ProjectCommandCatalog.Publish(new[] { a, b });
            var first = ProjectCommandCatalog.Query("list");
            StringAssert.Contains("duplicate_command_id", first);
            StringAssert.DoesNotContain("\"available\":true", first);
            ProjectCommandCatalog.Publish(new[] { b, a });
            Assert.AreEqual(first, ProjectCommandCatalog.Query("list"));
            Assert.IsFalse(BridgeToolRegistry.Contains("project.tests.contract"));
        }
        [Test] public void ListIsBoundedFilteredAndDescribeIsExact()
        {
            ProjectCommandCatalog.Publish(new[] { Entry("Empty", "project.tests.b", false), Entry() });
            var page = ProjectCommandCatalog.Query("list", query: "SCHEMA", tags: new[] { "test" }, group: "fixtures", package: "tests", limit: 1);
            StringAssert.Contains("\"total\":2", page);
            StringAssert.Contains("\"nextOffset\":1", page);
            StringAssert.DoesNotContain("inputSchema", page);
            StringAssert.Contains("command_disabled", ProjectCommandCatalog.Query("describe", "project.tests.b"));
            StringAssert.Contains("command_not_found", ProjectCommandCatalog.Query("describe", "project.tests"));
            StringAssert.Contains("invalid_arguments", ProjectCommandCatalog.Query("list", limit: 101));
            StringAssert.Contains("\"total\":0", ProjectCommandCatalog.Query("list", tags: new[] { "absent" }));
            StringAssert.Contains("inputSchema", ProjectCommandCatalog.Query("describe", "project.tests.contract"));
        }
        [Test] public void ExactDuplicateIdHasNoResolvableWinner()
        {
            ProjectCommandCatalog.Publish(new[] { Entry(), Entry("Empty") });
            Assert.IsFalse(ProjectCommandCatalog.TryGet("project.tests.contract", out _));
            StringAssert.Contains("duplicate_command_id", ProjectCommandCatalog.Query("describe", "project.tests.contract"));
        }
        [TestCase(typeof(string), "string")][TestCase(typeof(bool), "boolean")]
        [TestCase(typeof(int), "integer")][TestCase(typeof(long), "string")]
        [TestCase(typeof(float), "number")][TestCase(typeof(double), "number")][TestCase(typeof(Choice), "string")]
        public void WireTypeAndArrayItemsMatchEachSupportedScalar(Type type, string wireType)
        {
            StringAssert.Contains("\"" + wireType + "\"", ProjectCommandSchema.TypeSchema(type));
            StringAssert.Contains("\"items\":" + ProjectCommandSchema.TypeSchema(type), ProjectCommandSchema.TypeSchema(type.MakeArrayType()));
            if (type.IsValueType)
            {
                var nullable = typeof(Nullable<>).MakeGenericType(type);
                StringAssert.Contains("\"anyOf\":[" + ProjectCommandSchema.TypeSchema(type), ProjectCommandSchema.TypeSchema(nullable));
                StringAssert.Contains(ProjectCommandSchema.TypeSchema(nullable), ProjectCommandSchema.TypeSchema(nullable.MakeArrayType()));
            }
        }
        [Test] public void BuiltInNamespaceIsReserved()
        {
            Assert.AreEqual("invalid_command_declaration", Entry(id: "unity_open_mcp_ping").Code);
        }
    }
}
