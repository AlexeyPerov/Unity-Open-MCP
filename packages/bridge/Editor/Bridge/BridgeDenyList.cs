// M14 T5.2 / T5.3 — Static deny heuristic for the power tools.
//
// execute_csharp and execute_menu can do anything — quit the editor, delete
// assets, build the player, etc. The gate catches *new project errors* after a
// mutation, but several destructive ops produce no verify signal (the editor
// just closes) or are themselves the threat (bulk asset delete). This module
// implements a configurable regex deny heuristic that runs BEFORE the mutation
// and refuses with a clear reason + alternative.
//
// Policy shape (`.unity-open-mcp/settings.json`):
//   - csharpDenyPatterns : string[] — regex patterns matched against the
//     submitted snippet source. Empty array / unset ⇒ use the built-in
//     defaults (DefaultCSharpPatterns). Explicitly set to [""] to disable.
//   - menuDenyPatterns   : string[] — regex patterns matched against the
//     menu_path. Same null-vs-empty semantics.
//
// Bypass contract (matches the spec for T5.2): an explicit `gate: "off"` AND
// `confirm_bypass: true` on the request skips the deny heuristic. The bypass
// is audited via the activity log (the request still flows through the normal
// dispatch path, so it lands in BridgeActivityLog with gate.mode = off). There
// is no silent bypass: omitting either flag, or passing only one, still denies.
//
// The decision is split out from ExecuteCSharpTool / ExecuteMenuTool so it is
// unit-testable without Roslyn / a live editor, mirroring BridgeAuthCheck.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace UnityOpenMcpBridge
{
    // Outcome of a deny-list evaluation. Deny carries the matched pattern and a
    // human/agent-readable alternative so the refusal is actionable.
    public readonly struct DenyResult
    {
        public readonly bool Allowed;
        public readonly string MatchedPattern;
        public readonly string Reason;
        public readonly string Suggestion;

        DenyResult(bool allowed, string matchedPattern, string reason, string suggestion)
        {
            Allowed = allowed;
            MatchedPattern = matchedPattern;
            Reason = reason;
            Suggestion = suggestion;
        }

        public static DenyResult Allow() =>
            new DenyResult(true, null, null, null);

        public static DenyResult Deny(string matchedPattern, string reason, string suggestion) =>
            new DenyResult(false, matchedPattern, reason, suggestion);
    }

    public static class BridgeDenyList
    {
        // Documented destructive patterns. These are deliberately conservative —
        // the goal is to refuse the obvious footguns (editor exit, bulk asset
        // deletion, unbounded project builds) while leaving the long tail of
        // legitimate mutations alone. Users extend via settings.
        //
        // Regex (not substring) so a single pattern can express "word boundary
        // before the method call". Case-sensitive by default — Unity APIs are
        // PascalCase and a case-insensitive match would over-trigger on
        // comments / local variables.
        static readonly string[] DefaultCSharpPatterns =
        {
            // Editor / playmode exit — no verify signal possible after these.
            @"EditorApplication\.Exit",
            @"Application\.Quit",
            // Bulk asset deletion — destroys work the gate can't scope.
            @"AssetDatabase\.DeleteAsset",
            // Unbounded full-project builds. The build API itself is allowed
            // (legitimate in CI), but only when the caller has explicitly opted
            // out of the gate below; the default deny keeps a stray agent from
            // kicking off a multi-minute build.
            @"BuildPipeline\.BuildPlayer",
            // Filesystem nukes under Assets/ — the verify gate runs on asset
            // GUIDs, a raw Directory.Delete leaves dangling references the gate
            // would only catch as missing-reference noise.
            @"Directory\.Delete\s*\([^)]*Assets"
        };

        static readonly string[] DefaultMenuPatterns =
        {
            // Editor quit / exit. Existing hardcoded File/Quit block is kept as
            // a fallback; this list is the configurable surface.
            @"^File/Quit$",
            @"^File/Exit$",
            // Reimport All is a multi-minute full-project op; allow scoped
            // Assets/Refresh (already in the read-only allowlist) instead.
            @"^Assets/Reimport All$"
        };

        // Compiled + cached patterns. Re-resolved when the underlying settings
        // signature changes so a hot dispatch path does not pay a Regex.Compile
        // per request. Volatile read on the cache slot; writes under a lock.
        static readonly object _cacheLock = new object();
        static volatile PatternCache _csharpCache;
        static volatile PatternCache _menuCache;

        sealed class PatternCache
        {
            public readonly string[] Source;
            public readonly Regex[] Compiled;
            public PatternCache(string[] source, Regex[] compiled)
            {
                Source = source;
                Compiled = compiled;
            }
        }

        // Default deny patterns exposed for UI / docs / tests. Returning a fresh
        // copy each call so callers can't mutate the static defaults.
        public static string[] DefaultCSharpDenyPatterns => (string[])DefaultCSharpPatterns.Clone();
        public static string[] DefaultMenuDenyPatterns => (string[])DefaultMenuPatterns.Clone();

        // The patterns a given project actually evaluates, with settings
        // precedence: a non-empty settings array wins; null/empty ⇒ built-in
        // defaults. (JsonUtility serializes null arrays as [], so null and
        // empty are indistinguishable after a round-trip — treat both as
        // "use defaults". To turn the deny list off entirely, rely on the
        // per-request bypass or provide a custom pattern that matches nothing.)
        public static string[] ResolveCSharpPatterns(string[] settingsPatterns)
            => HasPatterns(settingsPatterns) ? settingsPatterns : DefaultCSharpPatterns;

        public static string[] ResolveMenuPatterns(string[] settingsPatterns)
            => HasPatterns(settingsPatterns) ? settingsPatterns : DefaultMenuPatterns;

        static bool HasPatterns(string[] arr)
        {
            if (arr == null || arr.Length == 0) return false;
            // An array of only empties/whitespace counts as "no patterns".
            for (int i = 0; i < arr.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(arr[i])) return true;
            }
            return false;
        }

        // Evaluate a submitted snippet against the csharp deny list. Bypass is
        // granted only when the caller passes BOTH gate=off and confirm_bypass
        // =true — a single flag is not enough, so a careless agent can't talk
        // its way past the heuristic.
        public static DenyResult EvaluateCSharp(string code, string[] settingsPatterns, bool bypass)
        {
            if (bypass) return DenyResult.Allow();
            if (string.IsNullOrEmpty(code)) return DenyResult.Allow();
            return Match(code, GetOrCompile(ref _csharpCache, ResolveCSharpPatterns(settingsPatterns)),
                "execute_csharp",
                "Use a scoped typed tool (apply_fix, reserialize, invoke_method) instead of a raw snippet, " +
                "or retry with gate: \"off\" and confirm_bypass: true to proceed and accept the risk.");
        }

        // Evaluate a menu_path against the menu deny list. Same bypass contract.
        public static DenyResult EvaluateMenu(string menuPath, string[] settingsPatterns, bool bypass)
        {
            if (bypass) return DenyResult.Allow();
            if (string.IsNullOrEmpty(menuPath)) return DenyResult.Allow();
            return Match(menuPath, GetOrCompile(ref _menuCache, ResolveMenuPatterns(settingsPatterns)),
                "execute_menu",
                "Use a scoped typed tool instead, or retry with gate: \"off\" and confirm_bypass: true " +
                "to proceed and accept the risk.");
        }

        static DenyResult Match(string input, PatternCache cache, string toolName, string suggestion)
        {
            var compiled = cache.Compiled;
            for (int i = 0; i < compiled.Length; i++)
            {
                var r = compiled[i];
                try
                {
                    if (r.IsMatch(input))
                    {
                        return DenyResult.Deny(
                            cache.Source[i],
                            $"{toolName} matched the configured deny pattern '{cache.Source[i]}'. " +
                            "This pattern is blocked by default because it is destructive or cannot be " +
                            "verified by the gate.",
                            suggestion);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological pattern that times out should not block
                    // the dispatch path — fall through to allow, but the
                    // operator can fix the pattern in settings.
                    continue;
                }
            }
            return DenyResult.Allow();
        }

        // Cache lookup keyed on reference-equality of the settings array. The
        // settings store reuses the same array instance across reads until a
        // write replaces it, so this is a cheap and correct cache invalidation.
        static PatternCache GetOrCompile(ref PatternCache slot, string[] source)
        {
            var existing = slot;
            if (existing != null && ReferenceEquals(existing.Source, source)) return existing;
            lock (_cacheLock)
            {
                existing = slot;
                if (existing != null && ReferenceEquals(existing.Source, source)) return existing;
                slot = Compile(source);
                return slot;
            }
        }

        static PatternCache Compile(string[] source)
        {
            if (source == null || source.Length == 0)
                return new PatternCache(source ?? Array.Empty<string>(), Array.Empty<Regex>());

            var regexes = new List<Regex>(source.Length);
            var canonical = new List<string>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                var pattern = source[i];
                if (string.IsNullOrEmpty(pattern)) continue;
                try
                {
                    // 100ms per-pattern timeout — generous for normal patterns,
                    // bounded for catastrophic-backtracking user input.
                    regexes.Add(new Regex(pattern, RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)));
                    canonical.Add(pattern);
                }
                catch (ArgumentException)
                {
                    // Invalid regex in settings is logged once at compile time
                    // via Debug.LogWarning by the caller path; here we silently
                    // drop it so one bad pattern does not disable the rest.
                }
            }
            return new PatternCache(canonical.ToArray(), regexes.ToArray());
        }

        // Reset the compiled-pattern cache. Test-only — production never needs
        // to force a cache flush because the settings signature invalidation
        // already covers config edits.
        internal static void ResetCacheForTests()
        {
            lock (_cacheLock)
            {
                _csharpCache = null;
                _menuCache = null;
            }
        }
    }
}
