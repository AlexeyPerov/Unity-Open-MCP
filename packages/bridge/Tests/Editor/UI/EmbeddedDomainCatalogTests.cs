using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityOpenMcpBridge;

namespace UnityOpenMcpBridge.Tests
{
    // M18 Plan 4 T18.4.2 — locks the EmbeddedDomainCatalog shape so the
    // bridge UI panel, the TS mirror (`hub/src/lib/services/extensions.ts`
    // `EMBEDDED_DOMAINS`), the MCP tool-group catalog, and the bridge root
    // asmdef `versionDefines` all stay aligned. When a domain ships or its
    // UPM id changes, update all four sources in the same change.
    public static class EmbeddedDomainCatalogTests
    {
        [Test]
        public static void Catalog_ListsFiveShippedDomains()
        {
            var domains = EmbeddedDomainCatalog.Domains.Select(d => d.Domain).OrderBy(x => x).ToArray();
            Assert.AreEqual(
                new[] { "animation", "inputsystem", "navigation", "particle_system", "probuilder" },
                domains);
        }

        [Test]
        public static void Catalog_EveryEntryCarriesMinimumMetadata()
        {
            foreach (var d in EmbeddedDomainCatalog.Domains)
            {
                Assert.IsFalse(string.IsNullOrEmpty(d.Group), $"{d.Domain} must declare a tool-group id");
                Assert.IsFalse(string.IsNullOrEmpty(d.DisplayName));
                Assert.IsFalse(string.IsNullOrEmpty(d.Description));
                Assert.IsFalse(string.IsNullOrEmpty(d.TypeProbe), $"{d.Domain} must declare a type probe");
            }
        }

        [Test]
        public static void Catalog_GroupIdsMatchCanonicalToolGroupCatalog()
        {
            // Mirrors the ids in mcp-server/src/capabilities/tool-groups.ts.
            var expected = new HashSet<string>
            {
                "navigation",
                "input-system",
                "probuilder",
                "particle-system",
                "animation",
            };
            var actual = EmbeddedDomainCatalog.Domains.Select(d => d.Group);
            Assert.IsTrue(actual.All(g => expected.Contains(g)),
                $"unexpected group id(s): {string.Join(", ", actual.Except(expected))}");
        }

        [Test]
        public static void Catalog_InstallableDomainsCarryUpmIds()
        {
            var installable = EmbeddedDomainCatalog.Installable().ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "navigation", "inputsystem", "probuilder" },
                installable.Select(d => d.Domain));
            foreach (var d in installable)
            {
                Assert.IsFalse(d.Builtin);
                Assert.IsTrue(d.UpmDependency.StartsWith("com.unity."),
                    $"{d.Domain} installable dep must be a com.unity.* package");
            }
        }

        [Test]
        public static void Catalog_BuiltinDomainsHaveNoUpmId()
        {
            var builtin = EmbeddedDomainCatalog.Builtin().ToArray();
            CollectionAssert.AreEquivalent(
                new[] { "particle_system", "animation" },
                builtin.Select(d => d.Domain));
            foreach (var d in builtin)
            {
                Assert.IsTrue(d.Builtin);
                Assert.AreEqual("", d.UpmDependency);
            }
        }
    }
}
