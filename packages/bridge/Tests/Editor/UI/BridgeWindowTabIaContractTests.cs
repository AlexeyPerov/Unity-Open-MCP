using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M29 Plan 3 — pins the tab IA cleanup so the removed peer tabs (Batch,
    // Info) cannot silently come back and the prefs migration maps legacy
    // saved indices onto the new enum. The contract has three halves, all
    // expressed as compile-time structure rather than runtime GUI behavior
    // (an EditorWindow host cannot be laid out in EditMode):
    //
    //   1. ENUM SHAPE — BridgeWindowTab has exactly the six peer tabs
    //      {Status, Tools, Gate, Activity, Settings, Extensions}. Re-adding
    //      Batch or Info as a peer tab (or any new value) breaks the toolbar
    //      layout / prefs migration assumptions and must be a deliberate
    //      change.
    //
    //   2. DISPATCH SHAPE — DrawBatchTab / DrawInfoTab are gone (Batch is now
    //      an Activity section; Info is a toolbar About foldout). The About
    //      foldout entry point (DrawAboutFoldout) and the prefs migration
    //      helper (MigrateSelectedTab) must exist.
    //
    //   3. PREFS MIGRATION — a saved legacy index (0–7 from the old 8-value
    //      enum) must map onto a valid new tab so an upgrade never lands an
    //      operator on the wrong tab.
    //
    // If these tests fail, the tab IA was regressed: a removed peer tab came
    // back, the About foldout entry point was renamed, or the prefs migration
    // drifted from the legacy 8-value enum layout.
    [TestFixture]
    public class BridgeWindowTabIaContractTests
    {
        private const BindingFlags InstanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private const BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        [Test]
        public void EnumHasExactlyTheSixPeerTabs()
        {
            var names = Enum.GetNames(typeof(BridgeWindowTab));
            Assert.That(
                names,
                Is.EqualTo(new[] { "Status", "Tools", "Gate", "Activity", "Settings", "Extensions" }),
                "BridgeWindowTab must be exactly {Status, Tools, Gate, Activity, Settings, " +
                "Extensions}. Batch is an Activity section and Info is a toolbar About foldout " +
                "(M29 Plan 3) — re-adding either as a peer tab breaks the toolbar layout and " +
                "the prefs migration.");
        }

        [Test]
        public void RemovedEnumValuesAreGone()
        {
            var names = Enum.GetNames(typeof(BridgeWindowTab));
            Assert.IsFalse(names.Contains("Batch"),
                "Batch must not be a BridgeWindowTab value — it is an Activity section now.");
            Assert.IsFalse(names.Contains("Info"),
                "Info must not be a BridgeWindowTab value — it is a toolbar About foldout now.");
        }

        [Test]
        public void RemovedTabDispatchMethodsAreGone()
        {
            var windowType = typeof(UnityOpenMcpBridgeWindow);
            Assert.IsNull(
                windowType.GetMethod("DrawBatchTab", InstanceFlags),
                "DrawBatchTab is back. Batch must render as an Activity section, not a peer tab.");
            Assert.IsNull(
                windowType.GetMethod("DrawInfoTab", InstanceFlags),
                "DrawInfoTab is back. Info must render as the toolbar About foldout.");
        }

        [Test]
        public void AboutFoldoutEntryPointExists()
        {
            Assert.IsNotNull(
                typeof(UnityOpenMcpBridgeWindow).GetMethod("DrawAboutFoldout", InstanceFlags),
                "DrawAboutFoldout is missing. The toolbar '?' button toggles the About foldout, " +
                "which must render the docs / repo / quick-reference links the old Info tab carried.");
        }

        [Test]
        public void ActivityBatchSectionEntryPointExists()
        {
            Assert.IsNotNull(
                typeof(UnityOpenMcpBridgeWindow).GetMethod("DrawActivityBatchSection", InstanceFlags),
                "DrawActivityBatchSection is missing. Batch is now a section under the Activity " +
                "tab and must render BridgeBatchPanel.Draw() under a foldout.");
        }

        [Test]
        public void SettingsGateModeIsReadOnly()
        {
            // The single interactive gate-default control lives on the Gate tab.
            // Settings must surface a read-only echo (DrawGlobalGateModeReadOnly),
            // not the interactive DrawGlobalGateModeControl popup.
            var windowType = typeof(UnityOpenMcpBridgeWindow);
            Assert.IsNotNull(
                windowType.GetMethod("DrawGlobalGateModeReadOnly", StaticFlags),
                "DrawGlobalGateModeReadOnly is missing. The Settings tab must echo the project " +
                "default gate mode read-only and point at the Gate tab for the single interactive " +
                "control (no duplicate interactive popup).");
        }

        [Test]
        public void InternalTermTooltipConstantsExistAndAreNonEmpty()
        {
            var requiredTooltipFields = new[]
            {
                "TooltipMutating",
                "TooltipReadOnly",
                "TooltipGateEnforce",
                "TooltipGateWarn",
                "TooltipGateOff",
                "TooltipGateNa",
                "TooltipSourceRegistry",
                "TooltipSourceHardcoded",
                "TooltipEnabledToggle",
            };

            var windowType = typeof(UnityOpenMcpBridgeWindow);
            foreach (var fieldName in requiredTooltipFields)
            {
                var field = windowType.GetField(fieldName, StaticFlags);
                Assert.IsNotNull(field,
                    $"{fieldName} is missing. Internal bridge terms must use shared tooltip wording.");
                Assert.IsNotEmpty(field.GetRawConstantValue() as string,
                    $"{fieldName} must provide non-empty hover help.");
            }
        }

        // ---------- prefs migration ----------

        [Test]
        public void MigrateSelectedTabMapsLegacyIndicesToValidTabs()
        {
            // Legacy 8-value enum layout was:
            //   0 Status, 1 Tools, 2 Gate, 3 Activity, 4 Batch,
            //   5 Settings, 6 Extensions, 7 Info.
            var method = typeof(UnityOpenMcpBridgeWindow).GetMethod(
                "MigrateSelectedTab", StaticFlags);
            Assert.IsNotNull(method,
                "MigrateSelectedTab is missing — the prefs migration helper must exist.");

            Assert.AreEqual(BridgeWindowTab.Status, InvokeMigrate(method, 0),
                "Legacy Status (0) must stay Status.");
            Assert.AreEqual(BridgeWindowTab.Tools, InvokeMigrate(method, 1),
                "Legacy Tools (1) must stay Tools.");
            Assert.AreEqual(BridgeWindowTab.Gate, InvokeMigrate(method, 2),
                "Legacy Gate (2) must stay Gate.");
            Assert.AreEqual(BridgeWindowTab.Activity, InvokeMigrate(method, 3),
                "Legacy Activity (3) must stay Activity.");
            Assert.AreEqual(BridgeWindowTab.Activity, InvokeMigrate(method, 4),
                "Legacy Batch (4) must land on Activity (Batch is now an Activity section).");
            Assert.AreEqual(BridgeWindowTab.Settings, InvokeMigrate(method, 5),
                "Legacy Settings (5) must land on Settings.");
            Assert.AreEqual(BridgeWindowTab.Extensions, InvokeMigrate(method, 6),
                "Legacy Extensions (6) must land on Extensions.");
            Assert.AreEqual(BridgeWindowTab.Status, InvokeMigrate(method, 7),
                "Legacy Info (7) must land on Status (Info is now a toolbar About foldout).");
        }

        [Test]
        public void MigrateSelectedTabFallsBackToStatusForOutOfRangeIndices()
        {
            var method = typeof(UnityOpenMcpBridgeWindow).GetMethod(
                "MigrateSelectedTab", StaticFlags);
            Assert.IsNotNull(method);

            // Negative / far-out-of-range saved values (corrupt prefs) must
            // never produce an invalid enum cast — they fall back to Status.
            Assert.AreEqual(BridgeWindowTab.Status, InvokeMigrate(method, -1));
            Assert.AreEqual(BridgeWindowTab.Status, InvokeMigrate(method, 42));
            Assert.AreEqual(BridgeWindowTab.Status, InvokeMigrate(method, 100));
        }

        private static BridgeWindowTab InvokeMigrate(MethodInfo method, int oldIndex)
        {
            return (BridgeWindowTab)method.Invoke(null, new object[] { oldIndex });
        }
    }
}
