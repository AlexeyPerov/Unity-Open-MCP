using System.Collections.Generic;
using System.Linq;

namespace UnityAgentVerify.Fixes
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
        static readonly List<IFixProvider> _providers = new();

        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterDefaults()
        {
            if (_providers.Count == 0)
            {
                _providers.Add(new RemoveMissingScriptFix());
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

        public static bool TryGetFixInfo(string ruleId, string issueCode, out string fixId, out bool safe)
        {
            fixId = null;
            safe = false;

            foreach (var provider in _providers)
            {
                var testKey = $"{ruleId}|ERROR|__test__|{issueCode}";
                if (provider.CanFix(testKey))
                {
                    fixId = provider.FixId;
                    safe = true;
                    return true;
                }
            }

            return false;
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
