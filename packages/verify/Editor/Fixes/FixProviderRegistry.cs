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

            var testKey = $"{ruleId}|ERROR|__test__|{issueCode}";
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
