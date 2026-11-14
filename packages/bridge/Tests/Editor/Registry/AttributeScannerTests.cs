using System;
using System.Reflection;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    namespace TestFixtureA
    {
        [BridgeToolType]
        public class Tool_Single
        {
            [BridgeTool("test_single_tool", Enabled = true)]
            public string DoWork() => "ok";
        }
    }

    namespace TestFixtureB
    {
        [BridgeToolType]
        public partial class Tool_Partial
        {
        }

        public partial class Tool_Partial
        {
            [BridgeTool("test_partial_tool_a")]
            public string MethodA() => "a";
        }

        public partial class Tool_Partial
        {
            [BridgeTool("test_partial_tool_b")]
            public string MethodB() => "b";
        }
    }

    namespace TestFixtureC
    {
        [BridgeToolType]
        public class Tool_CollisionFirst
        {
            [BridgeTool("test_collision_tool")]
            public string First() => "first";
        }

        [BridgeToolType]
        public class Tool_CollisionSecond
        {
            [BridgeTool("test_collision_tool")]
            public string Second() => "second";
        }
    }

    namespace TestFixtureD
    {
        [BridgeToolType]
        public class Tool_Disabled
        {
            [BridgeTool("test_disabled_tool", Enabled = false)]
            public string Disabled() => "should not appear";
        }
    }

    namespace TestFixtureE
    {
        [BridgeResourceType]
        public class Resource_Single
        {
            [BridgeResource("Test Resource", "unity-open-mcp://test/resource")]
            public string GetResource() => "{\"test\":true}";
        }
    }

    namespace TestFixtureF
    {
        [BridgeToolType]
        public class Tool_WithParams
        {
            [BridgeTool("test_params_tool")]
            public string DoWork(string name, int count) => $"name={name},count={count}";
        }
    }

    namespace TestFixtureG
    {
        [BridgeResourceType]
        public class Resource_Disabled
        {
            [BridgeResource("Disabled Resource", "unity-open-mcp://test/disabled", Enabled = false)]
            public string GetDisabled() => "should not appear";
        }
    }

    namespace TestFixtureH
    {
        [BridgeToolType]
        public class Tool_Mutating
        {
            [BridgeTool("test_mutating_tool", IsMutating = true, Gate = GateMode.Enforce)]
            public string Mutate() => "mutated";
        }

        [BridgeToolType]
        public class Tool_MutatingWarn
        {
            [BridgeTool("test_mutating_warn_tool", IsMutating = true, Gate = GateMode.Warn)]
            public string MutateWarn() => "warned";
        }
    }

    namespace TestFixtureI
    {
        // M13 T4.1 — verifies [BridgeTool(Lifecycle = ...)] flows through the
        // registry scanner onto the BridgeToolEntry so the dispatcher and dirty
        // guard see the declared policy.
        [BridgeToolType]
        public class Tool_WithLifecycle
        {
            [BridgeTool("test_lifecycle_settle_tool",
                IsMutating = true, Gate = GateMode.Enforce,
                Lifecycle = LifecyclePolicy.EditorSettle)]
            public string Settle() => "settled";

            [BridgeTool("test_lifecycle_restart_tool",
                IsMutating = true, Gate = GateMode.Enforce,
                Lifecycle = LifecyclePolicy.RestartThenSettle)]
            public string Restart() => "restarted";
        }
    }

    namespace TestFixtureJ
    {
        // M18 Plan 2 / T18.2.1 — verifies [BridgeTool(Group = ...)] flows
        // through the registry scanner onto the BridgeToolEntry and the
        // GroupToTools() mapping. Tools without a Group assignment must
        // expose Group = null and be omitted from the mapping.
        [BridgeToolType]
        public class Tool_WithGroup
        {
            [BridgeTool("test_grouped_navigation_tool", Group = "navigation")]
            public string NavTool() => "nav";

            [BridgeTool("test_grouped_core_tool", Group = "core")]
            public string CoreTool() => "core";

            [BridgeTool("test_ungrouped_tool")]
            public string UngroupedTool() => "ungrouped";
        }
    }

    namespace TestFixtureK
    {
        // M18 Plan 6 / T18.6.2 — models the "embedded bridge copy + legacy
        // extension pack present" duplicate-registration scenario: two
        // [BridgeToolType] classes in different assemblies/declaring the SAME
        // tool id. The registry must reject all conflicting entries and
        // surface the collision via DuplicateCount / DuplicateToolNames so the
        // guard is observable without log capture. (The real-world case is the
        // embedded navigation tools vs. the legacy
        // com.alexeyperov.unity-open-mcp-ext-navigation pack; this fixture
        // reproduces the shape in the ungated test assembly.)
        [BridgeToolType]
        public class Tool_DuplicateFirst
        {
            [BridgeTool("test_duplicate_guard_tool")]
            public string First() => "first";
        }

        [BridgeToolType]
        public class Tool_DuplicateSecond
        {
            [BridgeTool("test_duplicate_guard_tool")]
            public string Second() => "second";
        }
    }

    namespace TestFixtureL
    {
        // String-returning tools (every embedded domain and extension pack)
        // report a refusal as the JSON body {"error":{"code":…,"message":…}}.
        [BridgeToolType]
        public class Tool_Refusing
        {
            internal const string Plain =
                "{\"error\":{\"code\":\"play_mode_required\",\"message\":\"Enter play mode first.\"}}";
            internal const string Detailed =
                "{\"error\":{\"code\":\"ambiguous_target\",\"message\":\"2 matches\",\"candidates\":[\"A/Play\",\"B/Play\"]}," +
                "\"agentNextSteps\":[\"Pass target_index.\"]}";
            internal const string Fine = "{\"status\":\"ok\",\"errors\":[],\"last\":{\"error\":null}}";

            internal static int MutationCalls;

            [BridgeTool("test_refusing_tool")]
            public string Refuse(string kind = "plain")
                => kind == "detailed" ? Detailed : kind == "ok" ? Fine : Plain;

            [BridgeTool("test_refusing_mutation_tool", IsMutating = true, Gate = GateMode.Enforce)]
            public string RefuseMutation(string[] paths_hint = null)
            {
                MutationCalls++;
                return Detailed;
            }
        }
    }

    public class AttributeScannerTests
    {
        // The [BridgeTool] / [BridgeResource] fixtures above live in this test
        // assembly, which references nunit.framework. The production
        // BridgeToolRegistry.Scan() / BridgeResourceRegistry.Scan() now EXCLUDE
        // test assemblies so test_* ids never leak into the Tools tab,
        // GET /tools, or the group→tools map. These tests opt back into
        // scanning their own assembly via the includeTestAssemblies overload so
        // the scanner behavior is still exercised end-to-end.
        [OneTimeSetUp]
        public static void ScanWithTestFixtures()
        {
            BridgeToolRegistry.Scan(includeTestAssemblies: true);
            BridgeResourceRegistry.Scan(includeTestAssemblies: true);
        }

        [Test]
        public static void Scan_FindsSingleTool()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("test_single_tool"),
                "test_single_tool should be discovered");
        }

        [Test]
        public static void Scan_PartialClass_BothMethodsFound()
        {
            Assert.IsTrue(BridgeToolRegistry.Contains("test_partial_tool_a"),
                "test_partial_tool_a from partial class should be discovered");
            Assert.IsTrue(BridgeToolRegistry.Contains("test_partial_tool_b"),
                "test_partial_tool_b from partial class should be discovered");
        }

        [Test]
        public static void Scan_Collision_RejectsAll()
        {
            Assert.IsFalse(BridgeToolRegistry.Contains("test_collision_tool"));
            Assert.IsNull(BridgeToolRegistry.TryDispatch("test_collision_tool", "{}"));
        }

        // M18 Plan 6 / T18.6.2 — the duplicate-registration guard. When two
        // [BridgeToolType] classes declare the same id (the "embedded + legacy
        // pack present" shape, see TestFixtureK), the registry must:
        //   (a) record the collision in DuplicateToolNames / DuplicateCount,
        //   (b) reject all conflicting candidates.
        // This is the EditMode half of the M18 Plan 6 duplicate guard; the CI
        // half (compile the bridge with 0 domain packages) is the
        // compile-matrix leg.
        //
        // DuplicateCount is a global aggregate across every colliding id in
        // the loaded assemblies (TestFixtureC's test_collision_tool collides
        // too), so these tests assert containment + non-zero rather than an
        // exact count — robust against other fixtures and any live embedded +
        // legacy co-presence in the host project.
        [Test]
        public static void Scan_Duplicate_RecordsCollidingId()
        {
            Assert.GreaterOrEqual(BridgeToolRegistry.DuplicateCount, 1,
                "At least one duplicate tool id must be recorded after Scan.");
            CollectionAssert.Contains(
                BridgeToolRegistry.DuplicateToolNames(),
                "test_duplicate_guard_tool",
                "The colliding id declared by TestFixtureK must be recorded.");
        }

        [Test]
        public static void Scan_Duplicate_CollisionDoesNotRegisterTwice()
        {
            Assert.IsFalse(BridgeToolRegistry.Contains("test_duplicate_guard_tool"));
            Assert.IsFalse(BridgeToolRegistry.TryGet("test_duplicate_guard_tool", out _));
        }

        [Test]
        public static void Scan_DisabledTool_Excluded()
        {
            Assert.IsFalse(BridgeToolRegistry.Contains("test_disabled_tool"),
                "disabled tool should not be registered");
        }

        [Test]
        public static void Scan_ResourceDiscovered()
        {
            var entry = BridgeResourceRegistry.FindByRoute("unity-open-mcp://test/resource");
            Assert.IsNotNull(entry, "test resource should be discovered");
            Assert.AreEqual("Test Resource", entry.Name);
        }

        [Test]
        public static void Scan_DisabledResource_Excluded()
        {
            var entry = BridgeResourceRegistry.FindByRoute("unity-open-mcp://test/disabled");
            Assert.IsNull(entry, "disabled resource should not be registered");
        }

        [Test]
        public static void TryDispatch_WithParams_ExtractsCorrectly()
        {
            var body = "{\"name\":\"hello\",\"count\":42}";
            var result = BridgeToolRegistry.TryDispatch("test_params_tool", body);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            Assert.AreEqual("name=hello,count=42", result.Output);
        }

        [Test]
        public static void TryDispatch_MissingRequiredParam_ReturnsError()
        {
            // Missing a required (non-nullable, no default) parameter must surface as a
            // non-null failed result carrying the missing_parameter code. Returning null
            // is reserved by TryDispatch for unknown-tool only (the HTTP layer maps null
            // -> tool_not_found, and a missing_parameter Fail -> the gate error envelope).
            var body = "{\"name\":\"hello\"}";
            var result = BridgeToolRegistry.TryDispatch("test_params_tool", body);
            Assert.IsNotNull(result, "Missing required parameter must not map to unknown-tool (null)");
            Assert.IsFalse(result.Success, "Dispatch must fail when a required parameter is missing");
            Assert.AreEqual("missing_parameter", result.ErrorCode);
        }

        // A refusal body returned by a string tool is a failed dispatch, so the
        // gate envelope, a batch step, the activity log and the audit record all
        // see a failure — not an error object inside a success.
        [Test]
        public static void TryDispatch_RefusalBody_IsFailedDispatch()
        {
            var result = BridgeToolRegistry.TryDispatch("test_refusing_tool", "{}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success, "a returned {\"error\":{code,…}} body must not read as success");
            Assert.AreEqual("play_mode_required", result.ErrorCode);
            Assert.AreEqual("Enter play mode first.", result.ErrorMessage);
            Assert.IsNull(result.Output);
            Assert.IsFalse(result.PartialCommit);
        }

        [Test]
        public static void TryDispatch_RefusalBody_KeepsDetailAndNextSteps()
        {
            var result = BridgeToolRegistry.TryDispatch("test_refusing_tool", "{\"kind\":\"detailed\"}");
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("ambiguous_target", result.ErrorCode);
            Assert.AreEqual("{\"candidates\":[\"A/Play\",\"B/Play\"]}", result.ErrorDetailJson);
            CollectionAssert.AreEqual(new[] { "Pass target_index." }, result.NextSteps);
            // Gate-free tools answer with the body exactly as the tool wrote it.
            Assert.AreEqual(TestFixtureL.Tool_Refusing.Detailed, result.RefusalBody);
        }

        [Test]
        public static void TryDispatch_OutputMentioningErrors_StaysSuccess()
        {
            var result = BridgeToolRegistry.TryDispatch("test_refusing_tool", "{\"kind\":\"ok\"}");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(TestFixtureL.Tool_Refusing.Fine, result.Output);
        }

        [Test]
        public static void GateDispatch_RefusedReadOnlyTool_IsFailedMutationWithItsNextSteps()
        {
            var result = DispatchWithGate("test_refusing_tool", "{\"kind\":\"detailed\"}", "enforce", null);
            Assert.IsFalse(result.Mutation.Success);
            Assert.AreEqual("ambiguous_target", result.Mutation.ErrorCode);
            Assert.IsFalse(result.GateRan);
            Assert.AreEqual("mutation_failed", result.SkippedReason);
            CollectionAssert.AreEqual(new[] { "Pass target_index." }, result.AgentNextSteps);

            var json = BridgeJson.BuildGateEnvelope(result, "enforce", LifecyclePolicy.None);
            StringAssert.Contains(
                "\"mutation\":{\"success\":false,\"output\":null,\"error\":{\"code\":\"ambiguous_target\",\"message\":\"2 matches\",\"candidates\":[\"A/Play\",\"B/Play\"]}}",
                json);
            StringAssert.Contains("\"agentNextSteps\":[\"Pass target_index.\"]", json);
        }

        // A refused mutating tool validates nothing: the gate reports the
        // mutation failure and the tool's own next steps on every gate path.
        [TestCase("off", true)]
        [TestCase("enforce", false)]
        public static void GateDispatch_RefusedMutatingTool_RunsNoGate(string gateMode, bool scoped)
        {
            TestFixtureL.Tool_Refusing.MutationCalls = 0;
            var result = DispatchWithGate("test_refusing_mutation_tool", "{}", gateMode,
                scoped ? new[] { "Assets/__mcp_refusal_probe__" } : null);
            Assert.AreEqual(1, TestFixtureL.Tool_Refusing.MutationCalls);
            Assert.IsFalse(result.Mutation.Success);
            Assert.AreEqual("ambiguous_target", result.Mutation.ErrorCode);
            Assert.IsFalse(result.GateRan);
            Assert.IsFalse(result.GateFailed);
            Assert.IsNull(result.Delta);
            Assert.AreEqual("mutation_failed", result.SkippedReason);
            CollectionAssert.AreEqual(new[] { "Pass target_index." }, result.AgentNextSteps);
        }

        private static GateDispatchResult DispatchWithGate(string tool, string body, string gateMode, string[] pathsHint)
            => (GateDispatchResult)typeof(BridgeHttpServer)
                .GetMethod("DispatchWithGateCore", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { tool, body, gateMode, pathsHint });

        // feedback (2026-08-17, editor_status uncallable) — the transport
        // envelope keys (gate / timeout_ms / ignore_scene_dirty /
        // confirm_bypass) are consumed by the dispatcher off the raw body for
        // EVERY tool, so they must be accepted even on a zero-parameter
        // registry tool. Without the exemption, the MCP server's
        // schema-default injection (timeout_ms:30000 on editor_status,
        // gate:"enforce" on every extension tool) made those tools uncallable
        // with default args.
        [Test]
        public static void TryDispatch_TransportEnvelopeKeys_AcceptedOnParamlessTool()
        {
            var body =
                "{\"gate\":\"enforce\",\"timeout_ms\":30000," +
                "\"ignore_scene_dirty\":false,\"confirm_bypass\":false}";
            var result = BridgeToolRegistry.TryDispatch("test_single_tool", body);
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Success,
                "Transport envelope keys must not fail validation on a zero-parameter tool. " +
                (result.Success ? "" : $"Got {result.ErrorCode}: {result.ErrorMessage}"));
            Assert.AreEqual("ok", result.Output);
        }

        [Test]
        public static void TryDispatch_UnknownKeyOnParamlessTool_ReportsNoneAllowed()
        {
            // feedback (2026-08-17) — an empty allow-list rendered as
            // "Allowed: ." (an unreadable bare dot). It must read "(none)".
            var body = "{\"foo\":\"bar\"}";
            var result = BridgeToolRegistry.TryDispatch("test_single_tool", body);
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success);
            Assert.AreEqual("validation_error", result.ErrorCode);
            StringAssert.Contains("Allowed: (none).", result.ErrorMessage,
                "A zero-parameter tool's allow-list must render as '(none)', not an empty list.");
        }

        [Test]
        public static void TryDispatch_EnvelopeKeysDoNotMaskRealTypos()
        {
            // The exemption must stay narrow: a genuinely unknown key is still
            // rejected even when envelope keys are present alongside it.
            var body = "{\"gate\":\"enforce\",\"timeout_ms\":30000,\"name\":\"x\",\"count\":1,\"camalCase\":\"typo\"}";
            var result = BridgeToolRegistry.TryDispatch("test_params_tool", body);
            Assert.IsNotNull(result);
            Assert.IsFalse(result.Success, "A misspelled parameter key must still be rejected");
            Assert.AreEqual("validation_error", result.ErrorCode);
            StringAssert.Contains("camalCase", result.ErrorMessage,
                "The unknown-key list must name the misspelled key, not the envelope keys.");
        }

        // M13 T4.1 — [BridgeTool(Lifecycle = ...)] must flow onto the entry so
        // the dispatcher and dirty guard see the declared policy.
        [Test]
        public static void Scan_LifecycleAttribute_FlowsToEntry()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_lifecycle_settle_tool", out var settleEntry));
            Assert.AreEqual(LifecyclePolicy.EditorSettle, settleEntry.Lifecycle);

            Assert.IsTrue(BridgeToolRegistry.TryGet("test_lifecycle_restart_tool", out var restartEntry));
            Assert.AreEqual(LifecyclePolicy.RestartThenSettle, restartEntry.Lifecycle);
        }

        [Test]
        public static void Scan_LifecycleDefault_IsNoneWhenOmitted()
        {
            // Tools that don't declare a lifecycle must default to None (read-
            // only safe default), not be inferred from IsMutating.
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_mutating_tool", out var mutatingEntry));
            Assert.AreEqual(LifecyclePolicy.None, mutatingEntry.Lifecycle,
                "Omitted lifecycle defaults to None even on mutating tools — policy is explicit.");
        }

        // M18 Plan 2 / T18.2.1 — [BridgeTool(Group = ...)] flows onto the entry
        // and the GroupToTools() mapping. Ungrouped tools stay null and are
        // omitted from the mapping.
        [Test]
        public static void Scan_GroupAttribute_FlowsToEntry()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_grouped_navigation_tool", out var navEntry));
            Assert.AreEqual("navigation", navEntry.Group);

            Assert.IsTrue(BridgeToolRegistry.TryGet("test_grouped_core_tool", out var coreEntry));
            Assert.AreEqual("core", coreEntry.Group);
        }

        [Test]
        public static void Scan_OmittedGroup_DefaultsToNull()
        {
            Assert.IsTrue(BridgeToolRegistry.TryGet("test_ungrouped_tool", out var entry));
            Assert.IsNull(entry.Group, "Omitted Group defaults to null (always visible).");
        }

        [Test]
        public static void GroupToTools_IncludesAssignedTools()
        {
            var map = BridgeToolRegistry.GroupToTools();
            // The two grouped fixtures from TestFixtureJ land in their groups.
            Assert.IsTrue(map.ContainsKey("navigation"));
            Assert.Contains("test_grouped_navigation_tool", map["navigation"]);
            Assert.IsTrue(map.ContainsKey("core"));
            Assert.Contains("test_grouped_core_tool", map["core"]);
        }

        [Test]
        public static void GroupToTools_OmitsUngroupedTools()
        {
            var map = BridgeToolRegistry.GroupToTools();
            // test_ungrouped_tool has Group = null — it should NOT appear in
            // any group roster (it's always visible, so the group system
            // ignores it).
            foreach (var kv in map)
            {
                Assert.IsFalse(kv.Value.Contains("test_ungrouped_tool"),
                    $"ungrouped tool must not appear in group '{kv.Key}'");
            }
        }

        [Test]
        public static void GroupToTools_RostersAreSorted()
        {
            var map = BridgeToolRegistry.GroupToTools();
            foreach (var kv in map)
            {
                for (int i = 1; i < kv.Value.Count; i++)
                {
                    Assert.IsTrue(
                        string.CompareOrdinal(kv.Value[i - 1], kv.Value[i]) <= 0,
                        $"group '{kv.Key}' roster must be sorted (got {kv.Value[i - 1]} before {kv.Value[i]})");
                }
            }
        }

        // The production scan entry point MUST exclude test assemblies: the
        // test_* fixtures declared above are scanner exercisers, not real tools,
        // and must never reach the Tools tab, GET /tools, or the group→tools
        // capability map. Run the parameterless Scan() (the one production
        // calls from BridgeHttpServer) and assert no test_* id survives.
        [Test]
        public static void DefaultScan_ExcludesTestAssemblies()
        {
            BridgeToolRegistry.Scan(); // production entry point
            try
            {
                foreach (var entry in BridgeToolRegistry.All())
                {
                    Assert.IsFalse(entry.Name.StartsWith("test_"),
                        $"production registry must not contain test fixtures, found: {entry.Name}");
                }
                Assert.IsFalse(BridgeToolRegistry.Contains("test_single_tool"),
                    "test_single_tool must NOT be registered by the default (production) Scan().");

                // The group→tools map must not carry any test_* ids either.
                foreach (var kv in BridgeToolRegistry.GroupToTools())
                {
                    foreach (var id in kv.Value)
                    {
                        Assert.IsFalse(id.StartsWith("test_"),
                            $"group '{kv.Key}' must not contain test fixtures, found: {id}");
                    }
                }
            }
            finally
            {
                // Restore the test-inclusive scan so the rest of this fixture's
                // tests still see their fixtures registered.
                BridgeToolRegistry.Scan(includeTestAssemblies: true);
                BridgeResourceRegistry.Scan(includeTestAssemblies: true);
            }
        }

        // Mirrors the tool test above for the resource registry: the
        // unity-open-mcp://test/* fixtures must not survive the production scan.
        [Test]
        public static void DefaultResourceScan_ExcludesTestAssemblies()
        {
            BridgeResourceRegistry.Scan(); // production entry point
            try
            {
                Assert.IsNull(BridgeResourceRegistry.FindByRoute("unity-open-mcp://test/resource"),
                    "test resource must NOT be registered by the default (production) resource Scan().");
                foreach (var entry in BridgeResourceRegistry.All())
                {
                    Assert.IsFalse(entry.Route.StartsWith("unity-open-mcp://test/"),
                        $"production resource registry must not contain test fixtures, found: {entry.Route}");
                }
            }
            finally
            {
                BridgeResourceRegistry.Scan(includeTestAssemblies: true);
                BridgeToolRegistry.Scan(includeTestAssemblies: true);
            }
        }
    }
}
