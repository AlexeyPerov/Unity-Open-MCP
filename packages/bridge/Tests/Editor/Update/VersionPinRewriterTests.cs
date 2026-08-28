using System;
using NUnit.Framework;
using UnityOpenMcpBridge.Update;
using PinKind = UnityOpenMcpBridge.Update.VersionPinRewriter.PinKind;

namespace UnityOpenMcpBridge.Tests
{
    // M32 Plan 4 / T32.4.1 — pins the pure rewriter the project updater runs
    // over every candidate file. The interesting behaviour is not "does regex
    // replace work" but the policy around it: which literals count as a pin,
    // which are deliberately left alone, and that a second pass is a no-op
    // (the updater is expected to be safe to re-run).
    //
    // The same patterns exist in scripts/switch-project-version.mjs for the
    // maintainer-side script. If a case here changes, that script changes too.
    [TestFixture]
    public class VersionPinRewriterTests
    {
        private const string Target = "1.2.3";

        // ---- npm pins (JSON + TOML) ---------------------------------------

        [Test]
        public void Rewrite_NpmPin_InJsonArgs_MovesToTarget()
        {
            var body = "{\n  \"args\": [\"-y\", \"unity-open-mcp@1.1.0\"]\n}";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.IsTrue(result.Body.Contains("unity-open-mcp@1.2.3"));
            Assert.IsFalse(result.Body.Contains("1.1.0"));
            Assert.IsTrue(result.Changed);
            Assert.AreEqual(1, result.Changes.Length);
            Assert.AreEqual(PinKind.Npm, result.Changes[0].Kind);
            Assert.AreEqual(new[] { "1.1.0" }, result.Changes[0].From);
        }

        [Test]
        public void Rewrite_NpmPin_InTomlArgs_MovesToTarget()
        {
            // Codex writes TOML; the pin is the same literal, which is why the
            // rewriter is format-agnostic rather than parser-based.
            var body = "[mcp_servers.unity-open-mcp]\nargs = [ \"-y\", \"unity-open-mcp@0.8.4\" ]\n";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.IsTrue(result.Body.Contains("\"unity-open-mcp@1.2.3\""));
            Assert.AreEqual(1, result.Changes.Length);
            Assert.AreEqual(new[] { "0.8.4" }, result.Changes[0].From);
        }

        [Test]
        public void Rewrite_NpmPin_InProse_MovesToTarget()
        {
            // Agent-facing markdown is rewritten for the same reason a config
            // is: an agent reads the command out of it and runs that version.
            var body = "Run `npx -y unity-open-mcp@1.0.0 status` before asking for tools.";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.AreEqual(
                "Run `npx -y unity-open-mcp@1.2.3 status` before asking for tools.", result.Body);
        }

        [Test]
        public void Rewrite_FloatingPin_IsLeftAloneAndReported()
        {
            // `@latest` is an update policy, not a stale version. Converting it
            // to a fixed pin would silently freeze a project that opted out.
            var body = "{\n  \"args\": [\"-y\", \"unity-open-mcp@latest\"]\n}";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.AreEqual(body, result.Body);
            Assert.IsFalse(result.Changed);
            Assert.AreEqual(1, result.FloatingPins);
            Assert.IsTrue(result.HasPin, "A floating pin still makes the file relevant to the report.");
        }

        [Test]
        public void Rewrite_BarePackageName_IsNotAPin()
        {
            var body = "npm install -g unity-open-mcp";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.AreEqual(body, result.Body);
            Assert.IsFalse(result.HasPin);
        }

        // ---- UPM git tags + the lock's verify dependency -------------------

        [Test]
        public void Rewrite_UpmTags_MoveBothPackages()
        {
            var body =
                "{\n" +
                "  \"com.alexeyperov.unity-open-mcp-bridge\": \"https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/bridge#bridge-v1.1.0\",\n" +
                "  \"com.alexeyperov.unity-open-mcp-verify\": \"https://github.com/AlexeyPerov/unity-open-mcp.git?path=packages/verify#verify-v1.1.0\"\n" +
                "}";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.IsTrue(result.Body.Contains("#bridge-v1.2.3"));
            Assert.IsTrue(result.Body.Contains("#verify-v1.2.3"));
            Assert.AreEqual(1, result.Changes.Length);
            Assert.AreEqual(PinKind.UpmTag, result.Changes[0].Kind);
            Assert.AreEqual(2, result.Changes[0].Count, "Both tags counted, one distinct version.");
        }

        [Test]
        public void Rewrite_VerifyDependencyPin_InLockFile_MovesToTarget()
        {
            // packages-lock.json records the bridge's dependency on verify as a
            // plain version. It must track the trio or a git-URL install of the
            // pair stops resolving.
            var body =
                "{\n  \"com.alexeyperov.unity-open-mcp-bridge\": {\n" +
                "    \"dependencies\": {\n" +
                "      \"com.alexeyperov.unity-open-mcp-verify\": \"1.1.0\"\n" +
                "    }\n  }\n}";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.IsTrue(result.Body.Contains("\"com.alexeyperov.unity-open-mcp-verify\": \"1.2.3\""));
            Assert.AreEqual(1, result.Changes.Length);
            Assert.AreEqual(PinKind.VerifyDependency, result.Changes[0].Kind);
        }

        [Test]
        public void Rewrite_PackageIdWithoutVersion_IsNotRewritten()
        {
            // The manifest names the same package id with a git URL, not a
            // version — only the `"id": "X.Y.Z"` shape is a dependency pin.
            var body = "{\n  \"com.alexeyperov.unity-open-mcp-verify\": \"file:../../packages/verify\"\n}";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.AreEqual(body, result.Body);
            Assert.IsFalse(result.HasPin);
        }

        // ---- Idempotency and reporting ------------------------------------

        [Test]
        public void Rewrite_IsIdempotent()
        {
            var body = "unity-open-mcp@1.1.0 and #bridge-v1.1.0";
            var once = VersionPinRewriter.Rewrite(body, Target);
            var twice = VersionPinRewriter.Rewrite(once.Body, Target);

            Assert.AreEqual(once.Body, twice.Body);
            Assert.IsFalse(twice.Changed, "A file already on the target reports no change.");
        }

        [Test]
        public void Rewrite_AlreadyCurrent_StillReportsThePin()
        {
            // "no change" and "no pin" are different states: the first keeps
            // the file in the report as verified-current, the second drops it.
            var result = VersionPinRewriter.Rewrite("unity-open-mcp@1.2.3", Target);

            Assert.IsFalse(result.Changed);
            Assert.IsTrue(result.HasPin);
            Assert.AreEqual(1, result.PinsFound);
        }

        [Test]
        public void Rewrite_MixedStaleVersions_ReportsEachDistinctOnce()
        {
            var body = "unity-open-mcp@0.9.0 then unity-open-mcp@1.0.0 then unity-open-mcp@0.9.0";
            var result = VersionPinRewriter.Rewrite(body, Target);

            Assert.AreEqual(new[] { "0.9.0", "1.0.0" }, result.Changes[0].From);
            Assert.AreEqual(3, result.Changes[0].Count);
            Assert.IsFalse(result.Body.Contains("0.9.0"));
            Assert.IsFalse(result.Body.Contains("1.0.0"));
        }

        [Test]
        public void Rewrite_EmptyBody_IsSafe()
        {
            var result = VersionPinRewriter.Rewrite("", Target);
            Assert.AreEqual("", result.Body);
            Assert.IsFalse(result.HasPin);
        }

        // ---- Version validation -------------------------------------------

        [Test]
        public void Rewrite_RejectsNonTripleVersion()
        {
            // Validated once, when the target is resolved — not per file. A
            // typo reaching a git tag would fail the UPM step much later.
            Assert.Throws<ArgumentException>(() => VersionPinRewriter.Rewrite("unity-open-mcp@1.0.0", "1.2"));
            Assert.Throws<ArgumentException>(() => VersionPinRewriter.Rewrite("unity-open-mcp@1.0.0", "v1.2.3"));
            Assert.Throws<ArgumentException>(() => VersionPinRewriter.Rewrite("unity-open-mcp@1.0.0", ""));
        }

        [Test]
        public void NormalizeVersion_StripsTagPrefix()
        {
            Assert.AreEqual("1.2.3", VersionPinRewriter.NormalizeVersion("v1.2.3"));
            Assert.AreEqual("1.2.3", VersionPinRewriter.NormalizeVersion(" 1.2.3 "));
            Assert.IsTrue(VersionPinRewriter.IsVersion(VersionPinRewriter.NormalizeVersion("v1.2.3")));
            Assert.IsFalse(VersionPinRewriter.IsVersion("1.2"));
            Assert.IsFalse(VersionPinRewriter.IsVersion("1.2.3-rc.1"));
        }
    }
}
