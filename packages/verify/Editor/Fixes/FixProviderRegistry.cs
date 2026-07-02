using System.Collections.Generic;
using System.Linq;

namespace UnityOpenMcpVerify.Fixes
{
    public class FixDescription
    {
        public string FixId;
        public string IssueId;
        public string AssetPath;
        public string Description;
        public bool Safe;
    }

    public class FixResult
    {
        public bool Success;
        public string Description;
        public string[] TouchedPaths;
    }

    // M25 Plan 3 — a fix candidate the gate can advertise alongside an issue so
    // an agent sees every option (safe vs unsafe) in one pass, not just the
    // first match TryGetFixInfo returns. Safe mirrors the provider's Describe().
    public class FixCandidate
    {
        public string FixId;
        public bool Safe;
    }

    public interface IFixProvider
    {
        string FixId { get; }
        bool CanFix(string issueId);
        FixDescription Describe(string issueId);
        FixResult Apply(string issueId);
    }

    public static class FixProviderRegistry
    {
        private static readonly List<IFixProvider> _providers = new();

        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterDefaults()
        {
            if (_providers.Count == 0)
            {
                _providers.Add(new RemoveMissingScriptFix());
                _providers.Add(new RelinkBrokenGuidFix());
                // M25 Plan 2 — fix-providers remainder. remove_orphan_meta +
                // fix_duplicate_guid now have real C# providers (previously
                // catalog-only; the offline TS scanner emitted the codes but
                // apply_fix routed to a bridge with no provider). The two
                // materials fixes link to the Plan 1 materials rule.
                _providers.Add(new RemoveOrphanMetaFix());
                _providers.Add(new FixDuplicateGuidFix());
                _providers.Add(new ReassignMissingTextureFix());
                _providers.Add(new ReassignMissingShaderFix());
            }
        }

        public static void Register(IFixProvider provider)
        {
            if (!_providers.Exists(p => p.FixId == provider.FixId))
                _providers.Add(provider);
        }

        public static IFixProvider Find(string fixId)
        {
            return _providers.FirstOrDefault(p => p.FixId == fixId);
        }

        // Returns the first fix matching a rule+issue pair plus the provider's
        // real Safe flag. The synthetic key carries a placeholder asset path —
        // providers' CanFix only inspects ruleId+issueCode, so this matches the
        // same set of providers that would respond to a real issue id.
        //
        // Safe is taken from Describe() so unsafe providers (e.g. relink_broken_guid)
        // are surfaced accurately in validate_edit / scan_paths envelopes. The
        // previous implementation hardwired Safe=true and masked every fix as
        // auto-applyable.
        public static bool TryGetFixInfo(string ruleId, string issueCode, out string fixId, out bool safe)
        {
            fixId = null;
            safe = false;

            var testKey = $"{ruleId}|ERROR|__test__.prefab|{issueCode}";
            foreach (var provider in _providers)
            {
                if (!provider.CanFix(testKey)) continue;

                fixId = provider.FixId;
                try
                {
                    safe = provider.Describe(testKey).Safe;
                }
                catch
                {
                    // If Describe throws for any reason, default to unsafe so
                    // the gate never auto-applies something it cannot reason about.
                    safe = false;
                }
                return true;
            }

            return false;
        }

        // Every fix that can resolve a given issue id. Unlike TryGetFixInfo
        // (first match) this returns the full set so apply_fix can advertise
        // all available fixes per issue — agents then choose safe vs unsafe.
        public static string[] FixesForIssue(string issueId)
        {
            if (string.IsNullOrEmpty(issueId)) return System.Array.Empty<string>();
            return _providers.Where(p => p.CanFix(issueId)).Select(p => p.FixId).ToArray();
        }

        // M25 Plan 3 — every fix candidate for a rule+issue pair, each with its
        // real Safe flag from Describe(). Used by scan_paths / validate_edit to
        // emit a fixCandidates[] block so agents see safe vs unsafe options up
        // front. Like TryGetFixInfo this builds a synthetic key — providers'
        // CanFix only inspect ruleId+issueCode.
        public static FixCandidate[] CandidatesForIssue(string ruleId, string issueCode)
        {
            if (string.IsNullOrEmpty(ruleId) || string.IsNullOrEmpty(issueCode))
                return System.Array.Empty<FixCandidate>();

            var testKey = $"{ruleId}|ERROR|__test__.prefab|{issueCode}";
            var result = new List<FixCandidate>();
            foreach (var provider in _providers)
            {
                if (!provider.CanFix(testKey)) continue;
                bool safe;
                try
                {
                    safe = provider.Describe(testKey).Safe;
                }
                catch
                {
                    safe = false;
                }
                result.Add(new FixCandidate { FixId = provider.FixId, Safe = safe });
            }
            return result.ToArray();
        }

        public static string[] AvailableFixIds()
        {
            return _providers.Select(p => p.FixId).ToArray();
        }

        public static void Clear()
        {
            _providers.Clear();
        }
    }
}
