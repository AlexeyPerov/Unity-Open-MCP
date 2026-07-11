using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M29 Plan 5 — bulk toggle policy (SetEnabled / SetGroupEnabled). These
    // cover the pure reconciliation logic that the Tools-tab bulk actions and
    // per-group bulk actions drive: the disabled list is rewritten in one
    // settings.json write + one Changed event, and tools NOT in the filtered
    // set keep their state (so a search-narrowed bulk is surgical). The IMGUI
    // confirm dialog itself is a native EditorUtility call and is not
    // unit-tested here; the policy is the testable seam.
    public class BridgeToolTogglePolicyBulkTests
    {
        private List<string> _restoreDisabled;

        [SetUp]
        public void SetUp()
        {
            _restoreDisabled = BridgeProjectSettings.DisabledTools.ToList();
            BridgeProjectSettings.SetDisabledTools(System.Array.Empty<string>());
        }

        [TearDown]
        public void TearDown()
        {
            BridgeProjectSettings.SetDisabledTools(_restoreDisabled);
        }

        // -------------------------------------------------------------------
        // SetEnabled — disable a filtered set
        // -------------------------------------------------------------------

        [Test]
        public void SetEnabled_Disable_AddsAllToDisabledList()
        {
            var filtered = new[] { "alpha", "beta", "gamma" };
            var changed = BridgeToolTogglePolicy.SetEnabled(filtered, enabled: false);

            Assert.IsTrue(changed, "disabling a non-empty set must report a change.");
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("alpha"));
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("beta"));
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("gamma"));
        }

        [Test]
        public void SetEnabled_Disable_IsDedupedAgainstExistingDisabled()
        {
            BridgeProjectSettings.SetDisabledTools(new[] { "alpha" });

            // "alpha" is already disabled; disabling {alpha, beta} must not
            // duplicate alpha and must add beta exactly once.
            BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "beta" }, enabled: false);

            var disabled = BridgeProjectSettings.DisabledTools.ToList();
            Assert.AreEqual(1, disabled.Count(x => x == "alpha"));
            Assert.AreEqual(1, disabled.Count(x => x == "beta"));
        }

        [Test]
        public void SetEnabled_PreservesToolsOutsideTheFilteredSet()
        {
            // delta is disabled but NOT in the filtered set — a bulk disable of
            // {alpha, beta} must leave delta's state untouched.
            BridgeProjectSettings.SetDisabledTools(new[] { "delta" });

            BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "beta" }, enabled: false);

            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("delta"),
                "a tool outside the filtered set must keep its state.");
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("alpha"));
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("beta"));
        }

        // -------------------------------------------------------------------
        // SetEnabled — enable a filtered set
        // -------------------------------------------------------------------

        [Test]
        public void SetEnabled_Enable_RemovesOnlyFilteredTools()
        {
            BridgeProjectSettings.SetDisabledTools(new[] { "alpha", "beta", "gamma" });

            var changed = BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "gamma" }, enabled: true);

            Assert.IsTrue(changed);
            Assert.IsFalse(BridgeToolTogglePolicy.IsDisabled("alpha"),
                "alpha is in the filtered set and must be re-enabled.");
            Assert.IsFalse(BridgeToolTogglePolicy.IsDisabled("gamma"));
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("beta"),
                "beta is NOT in the filtered set and must stay disabled.");
        }

        [Test]
        public void SetEnabled_Enable_NoopWhenNothingChanges()
        {
            // Nothing disabled → enabling a set changes nothing.
            var changed = BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "beta" }, enabled: true);
            Assert.IsFalse(changed, "enabling tools that are already enabled must report no change.");
        }

        [Test]
        public void SetEnabled_Disable_NoopWhenAlreadyDisabled()
        {
            BridgeProjectSettings.SetDisabledTools(new[] { "alpha", "beta" });

            // Disabling tools that are already all disabled must report no
            // change (and must not rewrite settings.json).
            var changed = BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "beta" }, enabled: false);
            Assert.IsFalse(changed);
        }

        // -------------------------------------------------------------------
        // Edge cases
        // -------------------------------------------------------------------

        [Test]
        public void SetEnabled_NullFiltered_ReturnsFalseNoChange()
        {
            var changed = BridgeToolTogglePolicy.SetEnabled(null, enabled: false);
            Assert.IsFalse(changed);
        }

        [Test]
        public void SetEnabled_EmptyFiltered_ReturnsFalseNoChange()
        {
            var changed = BridgeToolTogglePolicy.SetEnabled(System.Array.Empty<string>(), enabled: false);
            Assert.IsFalse(changed, "an empty filtered set must be a no-op.");
        }

        [Test]
        public void SetEnabled_EmptyAndWhitespaceNamesSkipped()
        {
            var changed = BridgeToolTogglePolicy.SetEnabled(new[] { "", "  ", "alpha" }, enabled: false);
            Assert.IsTrue(changed);
            Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled("alpha"));
            // empty/whitespace entries must not be persisted as disabled ids.
            Assert.IsFalse(BridgeToolTogglePolicy.IsDisabled(""));
            Assert.IsFalse(BridgeToolTogglePolicy.IsDisabled("  "));
        }

        [Test]
        public void SetEnabled_FiresChangedEventExactlyOnce()
        {
            var fireCount = 0;
            BridgeToolTogglePolicy.Changed += OnChanged;
            try
            {
                BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "beta", "gamma" }, enabled: false);
            }
            finally
            {
                BridgeToolTogglePolicy.Changed -= OnChanged;
            }
            Assert.AreEqual(1, fireCount,
                "a bulk action must fire Changed exactly once (one settings.json write), not once per tool.");

            void OnChanged() => fireCount++;
        }

        [Test]
        public void SetEnabled_NoopDoesNotFireChanged()
        {
            var fireCount = 0;
            BridgeToolTogglePolicy.Changed += OnChanged;
            try
            {
                // Nothing to disable → no change → no event.
                BridgeToolTogglePolicy.SetEnabled(new[] { "alpha", "beta" }, enabled: true);
            }
            finally
            {
                BridgeToolTogglePolicy.Changed -= OnChanged;
            }
            Assert.AreEqual(0, fireCount);

            void OnChanged() => fireCount++;
        }

        // -------------------------------------------------------------------
        // SetGroupEnabled — thin wrapper over SetEnabled; verify the wiring.
        // -------------------------------------------------------------------

        [Test]
        public void SetGroupEnabled_DisablesAllNamesInGroup()
        {
            var groupNames = new[] { "unity_open_mcp_gameobject_create", "unity_open_mcp_gameobject_find" };
            var changed = BridgeToolTogglePolicy.SetGroupEnabled(groupNames, enabled: false);

            Assert.IsTrue(changed);
            foreach (var n in groupNames)
                Assert.IsTrue(BridgeToolTogglePolicy.IsDisabled(n));
        }

        [Test]
        public void SetGroupEnabled_EnablesAllNamesInGroup()
        {
            var groupNames = new[] { "unity_open_mcp_gameobject_create", "unity_open_mcp_gameobject_find" };
            BridgeProjectSettings.SetDisabledTools(groupNames);

            BridgeToolTogglePolicy.SetGroupEnabled(groupNames, enabled: true);

            foreach (var n in groupNames)
                Assert.IsFalse(BridgeToolTogglePolicy.IsDisabled(n));
        }
    }
}
