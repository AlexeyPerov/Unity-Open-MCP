using System;
using System.Collections.Generic;
using System.IO;
using UnityOpenMcpVerify;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// One asset (or folder) a gated call made from an existing one: a copy, a
    /// move / rename, or a save-as. Carries the issue keys the gate's rules
    /// reported for the source at the moment the destination was made, so the
    /// delta can tell the destination's inherited issues from new ones.
    /// </summary>
    public sealed class AssetDerivation
    {
        public const string Copy = "copy";
        public const string Move = "move";
        public const string SaveAs = "save_as";

        public string Source { get; }
        public string Destination { get; }
        public string Operation { get; }

        /// <summary>Keys (IssueKey format) of the source's issues when the
        /// destination was derived from it.</summary>
        internal HashSet<string> SourceKeys { get; }

        // A prefab's root GameObject takes the file's name, so a copied or
        // renamed prefab reports its object paths under the new root name
        // ("Dst/Child" where the source said "Src/Child").
        private readonly string _sourceRoot;
        private readonly string _destinationRoot;

        public AssetDerivation(string source, string destination, string operation, IEnumerable<string> sourceKeys)
        {
            Source = AssetLineage.Normalize(source);
            Destination = AssetLineage.Normalize(destination);
            Operation = operation;
            SourceKeys = sourceKeys == null ? new HashSet<string>() : new HashSet<string>(sourceKeys);
            if (IsPrefab(Source) && IsPrefab(Destination))
            {
                var s = Path.GetFileNameWithoutExtension(Source);
                var d = Path.GetFileNameWithoutExtension(Destination);
                if (!string.Equals(s, d, StringComparison.Ordinal)) { _sourceRoot = s; _destinationRoot = d; }
            }
        }

        internal bool IsMove => Operation == Move;

        /// <summary>True when <paramref name="destinationKey"/> is an issue at
        /// the destination that the source already had: same rule, severity
        /// and code (object path / property / target GUID), with the asset
        /// path — and a prefab's root name — mapped back to the source.</summary>
        internal bool Inherits(string destinationKey)
        {
            if (SourceKeys.Count == 0) return false;
            foreach (var candidate in Map(destinationKey, Destination, Source, _destinationRoot, _sourceRoot))
                if (SourceKeys.Contains(candidate)) return true;
            return false;
        }

        /// <summary>For a move: true when <paramref name="sourceKey"/>, an
        /// issue the checkpoint saw at the old path, is still present at the
        /// new path — the asset moved with it, it was not resolved.</summary>
        internal bool CarriedTo(string sourceKey, HashSet<string> afterKeys)
        {
            if (!IsMove) return false;
            foreach (var candidate in Map(sourceKey, Source, Destination, _sourceRoot, _destinationRoot))
                if (afterKeys.Contains(candidate)) return true;
            return false;
        }

        // The key re-addressed from `fromRoot` to `toRoot`: the path mapped,
        // and — for a renamed prefab file — the variant with the root
        // GameObject's name mapped too. Nothing when the key is not under
        // `fromRoot` or does not parse.
        private static IEnumerable<string> Map(string key, string fromPath, string toPath,
            string fromRootName, string toRootName)
        {
            if (!IssueKey.TryParse(key, out var rule, out var severity, out var path, out var code)) yield break;
            if (!AssetLineage.IsUnder(path, fromPath)) yield break;
            var mappedPath = toPath + path.Substring(fromPath.Length);
            string exact = null;
            try { exact = IssueKey.Build(rule, severity, mappedPath, code); }
            catch (ArgumentException) { }
            if (exact == null) yield break;
            yield return exact;

            if (fromRootName == null || path != fromPath) yield break;
            var renamed = RenameRootSegment(code, fromRootName, toRootName);
            if (renamed == code) yield break;
            string variant = null;
            try { variant = IssueKey.Build(rule, severity, mappedPath, renamed); }
            catch (ArgumentException) { }
            if (variant != null) yield return variant;
        }

        // Replace `from` where it stands as the first segment of an object
        // path inside an issue code: right after a ':' and followed by '/',
        // ':', '#' or the end ("empty_local_ref:Src/Child:target",
        // "duplicate_component:BoxCollider:Src"). Child names are preceded by
        // '/', so they are never touched.
        internal static string RenameRootSegment(string code, string from, string to)
        {
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(from)) return code;
            var sb = new System.Text.StringBuilder(code.Length + 16);
            var i = 0;
            while (i < code.Length)
            {
                if (code[i] == ':' && string.CompareOrdinal(code, i + 1, from, 0, from.Length) == 0)
                {
                    var end = i + 1 + from.Length;
                    if (end == code.Length || code[end] == '/' || code[end] == ':' || code[end] == '#')
                    {
                        sb.Append(':').Append(to);
                        i = end;
                        continue;
                    }
                }
                sb.Append(code[i]);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsPrefab(string path) =>
            path != null && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the delta did with a derivation: how many of the
    /// destination's issues it recognised as the source's.</summary>
    public sealed class DerivationSummary
    {
        public string Source;
        public string Destination;
        public string Operation;
        public int InheritedErrors;
        public int InheritedWarnings;
    }

    /// <summary>
    /// Collects the derivations a gated call makes. The gate opens a recorder
    /// around the mutation; a tool that makes an asset from another calls
    /// <see cref="Capture"/> just before it does (so a move's source is still
    /// on disk) and <see cref="Pending.Commit"/> once it succeeded. Capture
    /// scans the source with the gate's rules over the part of the gate's
    /// scope the destination covers; outside a gate it does nothing.
    /// </summary>
    internal static class AssetLineage
    {
        private static Recorder s_Current;

        internal sealed class Recorder : IDisposable
        {
            private readonly Recorder _outer;
            private bool _disposed;
            internal readonly string[] Scope;
            internal readonly string[] RuleIds;
            internal readonly List<AssetDerivation> Derivations = new List<AssetDerivation>();

            internal Recorder(string[] scope, string[] ruleIds, Recorder outer)
            {
                Scope = scope;
                RuleIds = ruleIds;
                _outer = outer;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (s_Current == this) s_Current = _outer;
            }
        }

        internal sealed class Pending
        {
            private readonly Recorder _recorder;
            private readonly AssetDerivation _derivation;

            internal Pending(Recorder recorder, AssetDerivation derivation)
            {
                _recorder = recorder;
                _derivation = derivation;
            }

            /// <summary>The operation happened: the derivation counts.</summary>
            internal void Commit() => _recorder.Derivations.Add(_derivation);
        }

        /// <summary>Start recording for one gate run. <paramref name="scope"/>
        /// is the gate's paths_hint, <paramref name="ruleIds"/> the rules its
        /// checkpoint and validation run.</summary>
        internal static Recorder Begin(string[] scope, string[] ruleIds)
        {
            var normalized = new List<string>();
            if (scope != null)
                foreach (var p in scope)
                {
                    var n = Normalize(p);
                    if (!string.IsNullOrEmpty(n)) normalized.Add(n);
                }
            s_Current = new Recorder(normalized.ToArray(), ruleIds, s_Current);
            return s_Current;
        }

        internal static Recorder Current => s_Current;

        /// <summary>Called right before <paramref name="destination"/> is made
        /// from <paramref name="source"/>. Null outside a gate, or when no
        /// part of the gate's scope covers the destination.</summary>
        internal static Pending Capture(string source, string destination, string operation)
        {
            var recorder = s_Current;
            if (recorder == null) return null;
            var src = Normalize(source);
            var dst = Normalize(destination);
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst) || src == dst) return null;

            var sourceScope = SourceScope(recorder.Scope, src, dst);
            if (sourceScope.Length == 0) return null;

            var keys = new HashSet<string>();
            try
            {
                var result = GatePolicy.ValidatePathsOverride != null
                    ? GatePolicy.ValidatePathsOverride(sourceScope, recorder.RuleIds, null)
                    : VerifyRunner.RunScoped(new VerifyScope(sourceScope), recorder.RuleIds, VerifyRunMode.Validate);
                if (result?.Issues != null)
                    foreach (var issue in result.Issues)
                    {
                        var key = IssueKey.TryBuild(issue);
                        if (key != null) keys.Add(key);
                    }
            }
            catch (Exception e)
            {
                // Without the source's issues nothing is inherited: the
                // destination's issues count as new, which is the safe side.
                UnityEngine.Debug.LogWarning($"[AssetLineage] Could not scan '{src}' for inherited issues: {e.Message}");
                keys.Clear();
            }
            return new Pending(recorder, new AssetDerivation(src, dst, operation, keys));
        }

        // The gate validates the scope entries; the ones that reach the
        // destination have source counterparts: an entry inside the
        // destination maps to the same place inside the source, and an entry
        // that contains the destination (a parent folder) covers the whole
        // source.
        internal static string[] SourceScope(string[] scope, string source, string destination)
        {
            var result = new List<string>();
            if (scope == null) return result.ToArray();
            foreach (var entry in scope)
            {
                string mapped = null;
                if (IsUnder(entry, destination)) mapped = source + entry.Substring(destination.Length);
                else if (IsUnder(destination, entry)) mapped = source;
                if (mapped != null && !result.Contains(mapped)) result.Add(mapped);
            }
            return result.ToArray();
        }

        internal static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            if (path.Length == root.Length) return string.Equals(path, root, StringComparison.Ordinal);
            return path.Length > root.Length
                && path[root.Length] == '/'
                && path.StartsWith(root, StringComparison.Ordinal);
        }

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Trim().Replace('\\', '/').TrimEnd('/');
        }
    }
}
