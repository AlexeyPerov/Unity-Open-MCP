using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M14 T5.2 / T5.3 — Pure deny-decision tests for the power-tool heuristic.
    // ExecuteCSharpTool / ExecuteMenuTool call into BridgeDenyList before the
    // mutation runs; these pin the contract without spinning up Roslyn or the
    // live editor.
    public class BridgeDenyListTests
    {
        [SetUp]
        public void SetUp()
        {
            // The compiled-pattern cache keys on reference-equality of the
            // settings array, so reset it between tests to avoid leakage.
            BridgeDenyList.ResetCacheForTests();
        }

        // --- execute_csharp: default patterns ---

        [Test]
        public void CSharp_Defaults_BlockEditorExit()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "EditorApplication.Exit(0);", null, bypass: false);
            Assert.IsFalse(r.Allowed);
            Assert.That(r.MatchedPattern, Does.Contain("EditorApplication"));
        }

        [Test]
        public void CSharp_Defaults_BlockApplicationQuit()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "Application.Quit();", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_Defaults_BlockAssetDelete()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "AssetDatabase.DeleteAsset(\"Assets/Old.prefab\");", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_Defaults_BlockBuildPlayer()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "BuildPipeline.BuildPlayer(scenes, path, target, options);", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_Defaults_BlockDirectoryDeleteUnderAssets()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "System.IO.Directory.Delete(\"Assets/Generated\", true);", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_Defaults_AllowBenignSnippet()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "var go = new GameObject(); return go.name;", null, bypass: false);
            Assert.IsTrue(r.Allowed);
        }

        // --- execute_menu: default patterns ---

        [Test]
        public void Menu_Defaults_BlockFileQuit()
        {
            var r = BridgeDenyList.EvaluateMenu("File/Quit", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void Menu_Defaults_BlockFileExit()
        {
            var r = BridgeDenyList.EvaluateMenu("File/Exit", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void Menu_Defaults_BlockReimportAll()
        {
            var r = BridgeDenyList.EvaluateMenu("Assets/Reimport All", null, bypass: false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void Menu_Defaults_AllowRefresh()
        {
            var r = BridgeDenyList.EvaluateMenu("Assets/Refresh", null, bypass: false);
            Assert.IsTrue(r.Allowed);
        }

        // --- null/empty vs custom (settings precedence) ---

        [Test]
        public void CSharp_NullSettings_UsesDefaults()
        {
            var r = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);", null, false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_EmptySettings_UsesDefaults()
        {
            // null and empty are treated identically (JsonUtility serializes
            // null as []); both fall back to the built-in defaults.
            var r = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);",
                new string[0], false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_WhitespaceOnlySettings_UsesDefaults()
        {
            // An array of only whitespace entries also counts as "no patterns".
            var r = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);",
                new[] { "  ", "" }, false);
            Assert.IsFalse(r.Allowed);
        }

        [Test]
        public void CSharp_CustomPatterns_OverrideDefaults()
        {
            // A custom list replaces the defaults — the default patterns no
            // longer fire, only the custom ones do.
            var r1 = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);",
                new[] { @"MyInternal\.Danger" }, false);
            Assert.IsTrue(r1.Allowed);

            var r2 = BridgeDenyList.EvaluateCSharp("MyInternal.Danger();",
                new[] { @"MyInternal\.Danger" }, false);
            Assert.IsFalse(r2.Allowed);
        }

        [Test]
        public void CSharp_InvalidRegex_DroppedSilently()
        {
            // A syntactically-broken regex must not take the whole list down;
            // it is dropped and the remaining patterns still evaluate.
            var r = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);",
                new[] { "(unclosed", @"EditorApplication\.Exit" }, false);
            Assert.IsFalse(r.Allowed);
        }

        // --- bypass contract ---

        [Test]
        public void CSharp_Bypass_AllowsBlockedPattern()
        {
            var r = BridgeDenyList.EvaluateCSharp(
                "EditorApplication.Exit(0);", null, bypass: true);
            Assert.IsTrue(r.Allowed);
        }

        [Test]
        public void Menu_Bypass_AllowsBlockedPath()
        {
            var r = BridgeDenyList.EvaluateMenu("File/Quit", null, bypass: true);
            Assert.IsTrue(r.Allowed);
        }

        // --- reason + suggestion ---

        [Test]
        public void CSharp_Denial_IncludesReasonAndSuggestion()
        {
            var r = BridgeDenyList.EvaluateCSharp("EditorApplication.Exit(0);", null, false);
            Assert.IsFalse(r.Allowed);
            Assert.IsNotNull(r.Reason);
            Assert.IsNotNull(r.Suggestion);
            StringAssert.Contains("deny pattern", r.Reason);
            StringAssert.Contains("confirm_bypass", r.Suggestion);
        }

        [Test]
        public void CSharp_EmptyCode_Allowed()
        {
            // The deny heuristic is a no-op for empty input; the tool's own
            // validation error handles the missing-code case.
            Assert.IsTrue(BridgeDenyList.EvaluateCSharp("", null, false).Allowed);
            Assert.IsTrue(BridgeDenyList.EvaluateCSharp(null, null, false).Allowed);
        }

        [Test]
        public void Defaults_Exposed_AsFreshCopies()
        {
            var a = BridgeDenyList.DefaultCSharpDenyPatterns;
            var b = BridgeDenyList.DefaultCSharpDenyPatterns;
            Assert.AreNotSame(a, b);
            Assert.AreEqual(a.Length, b.Length);
        }
    }
}
