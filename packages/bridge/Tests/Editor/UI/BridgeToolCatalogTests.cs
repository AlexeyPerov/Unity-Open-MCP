using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // Locks the Tools-tab catalog (BridgeToolCatalog.Build) against the same
    // regression that hid the ~90 typed tools dispatched by BridgeHttpServer's
    // hardcoded switch. The catalog must union BridgeToolClassification.
    // KnownTools with the hardcoded meta-tools + registry, matching what
    // GET /tools (HandleToolsList) reports. If a typed tool is added to
    // KnownTools, it must show up here.
    public static class BridgeToolCatalogTests
    {
        // Force the production (test-excluding) registry state before the
        // fixture runs. AttributeScannerTests opts into
        // includeTestAssemblies:true and NUnit does not guarantee ordering
        // between fixtures, so the catalog tests must not depend on whatever
        // scan state a sibling fixture left behind. Build() reads the live
        // registry, so this pins it.
        [OneTimeSetUp]
        public static void ForceProductionScan()
        {
            BridgeToolRegistry.Scan();
        }

        [Test]
        public static void Build_IncludesRepresentativeTypedTools()
        {
            var names = BridgeToolCatalog.Build().Select(i => i.Name).ToHashSet();
            // These tools are dispatched via the hardcoded switch in
            // BridgeHttpServer.DispatchTool — no [BridgeTool] attribute, so
            // they only appear in the catalog via the KnownTools union.
            Assert.IsTrue(names.Contains("unity_open_mcp_gameobject_create"),
                "gameobject_create (KnownTools) must appear in the Tools catalog.");
            Assert.IsTrue(names.Contains("unity_open_mcp_scene_save"),
                "scene_save (KnownTools) must appear in the Tools catalog.");
            Assert.IsTrue(names.Contains("unity_open_mcp_material_set_property"),
                "material_set_property (KnownTools) must appear in the Tools catalog.");
            Assert.IsTrue(names.Contains("unity_open_mcp_prefab_instantiate"),
                "prefab_instantiate (KnownTools) must appear in the Tools catalog.");
            Assert.IsTrue(names.Contains("unity_open_mcp_build_start"),
                "build_start (KnownTools) must appear in the Tools catalog.");
        }

        [Test]
        public static void Build_CoversKnownToolsUnion()
        {
            // The catalog is the union of KnownTools ∪ registry ∪ the 10
            // hardcoded meta-tools (a subset of KnownTools). Every id in
            // KnownTools must be present so the Tools tab matches GET /tools.
            var catalogNames = BridgeToolCatalog.Build().Select(i => i.Name).ToHashSet();
            foreach (var known in BridgeToolClassification.KnownTools)
            {
                Assert.IsTrue(catalogNames.Contains(known),
                    $"KnownTools id '{known}' is missing from the Tools catalog — " +
                    "Build() must union KnownTools so the tab matches the dispatch surface.");
            }
        }

        [Test]
        public static void Build_ClassifiesMutatingVsReadOnly()
        {
            var byName = BridgeToolCatalog.Build().ToDictionary(i => i.Name);

            // Mutating tool from KnownTools -> mutating + enforce gate.
            Assert.IsTrue(byName.TryGetValue("unity_open_mcp_gameobject_create", out var create));
            Assert.AreEqual(BridgeToolMutability.Mutating, create.Mutability);
            Assert.AreEqual("enforce", create.GateMode);

            // Read-only tool from KnownTools -> read-only + n/a gate.
            Assert.IsTrue(byName.TryGetValue("unity_open_mcp_gameobject_find", out var find));
            Assert.AreEqual(BridgeToolMutability.ReadOnly, find.Mutability);
            Assert.AreEqual("n/a", find.GateMode);
        }

        [Test]
        public static void Build_DoesNotLeakTestFixtures()
        {
            // The test_* fixtures (AttributeScannerTests) live in this test
            // assembly; they must never reach the Tools catalog because
            // BridgeToolRegistry.Scan() excludes test assemblies. Pins the
            // test-assembly guard regression.
            var names = BridgeToolCatalog.Build().Select(i => i.Name);
            foreach (var name in names)
            {
                Assert.IsFalse(name.StartsWith("test_"),
                    $"Tools catalog must not contain test fixtures, found: {name}");
            }
        }
    }
}
