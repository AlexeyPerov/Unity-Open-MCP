using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityOpenMcpVerify.Internals.AssetDatabase;

namespace UnityOpenMcpVerify.Rules.AnimationAnalysis
{
    public static class Scanner
    {
        private static readonly Regex[] CurveGroupMarkers =
        {
            new Regex(@"^\s*m_RotationCurves:", RegexOptions.Compiled),
            new Regex(@"^\s*m_CompressedRotationCurves:", RegexOptions.Compiled),
            new Regex(@"^\s*m_EulerCurves:", RegexOptions.Compiled),
            new Regex(@"^\s*m_PositionCurves:", RegexOptions.Compiled),
            new Regex(@"^\s*m_ScaleCurves:", RegexOptions.Compiled),
            new Regex(@"^\s*m_FloatCurves:", RegexOptions.Compiled),
            new Regex(@"^\s*m_PPtrCurves:", RegexOptions.Compiled),
        };

        private static readonly Regex ParamRefRegex = new Regex(
            @"(?:animator|_animator|Animator)\s*\.\s*(?:Set(?:Float|Bool|Integer|Int|Trigger)|Get(?:Float|Bool|Integer|Int))\s*\(\s*""(\w+)""",
            RegexOptions.Compiled);

        private static readonly HashSet<string> SpecialStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Entry", "Exit", "Any State",
        };

        public static void ScanPaths(
            string[] paths,
            AnimationAnalysisScanSettings settings,
            List<AnimatorData> animators,
            List<AnimationClipData> clips,
            bool fullScan)
        {
            if (paths == null || paths.Length == 0) return;

            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (!AssetTypeUtilities.IsTextSerializedYaml(path)) continue;
                if (!File.Exists(path)) continue;

                if (path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                {
                    var data = AnalyzeController(path, settings);
                    if (data != null) animators.Add(data);
                }
                else if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    var data = AnalyzeClip(path, settings);
                    if (data != null) clips.Add(data);
                }
            }

            if (fullScan && settings.DetectDuplicateClips)
                DetectDuplicateClips(clips);
        }

        // -------------------------------------------------------------------
        // Controller analysis (ported)
        // -------------------------------------------------------------------

        private static AnimatorData AnalyzeController(string path, AnimationAnalysisScanSettings settings)
        {
            RuntimeAnimatorController runtime;
            try { runtime = AssetDatabase.LoadMainAssetAtPath(path) as RuntimeAnimatorController; }
            catch { return null; }
            if (runtime == null) return null;

            var data = new AnimatorData(path)
            {
                Name = Path.GetFileName(path),
            };

            var ac = runtime as AnimatorController;
            if (ac == null)
            {
                // A RuntimeAnimatorController override without a backing
                // AnimatorController — we can still report it loaded.
                return data;
            }

            data.LayerCount = ac.layers != null ? ac.layers.Length : 0;

            var allStates = new List<AnimatorState>();
            var entryStates = new HashSet<string>(StringComparer.Ordinal);
            var transitions = new List<KeyValuePair<string, string>>();
            var anyStateTargets = new List<string>();

            if (ac.layers != null)
            {
                foreach (var layer in ac.layers)
                {
                    if (layer == null || layer.stateMachine == null) continue;
                    CollectStatesAndTransitions(layer.stateMachine, allStates, entryStates, transitions, anyStateTargets);
                }
            }

            data.StateCount = allStates.Count;
            data.AnyStateTransitionCount = anyStateTargets.Count;
            data.TransitionCount = transitions.Count + anyStateTargets.Count;

            // Missing motion / clip.
            if (settings.DetectMissingClip)
            {
                foreach (var state in allStates)
                {
                    if (state.motion != null) continue;
                    if (state.name == null) continue;
                    if (state.name.EndsWith("(Placeholder)", StringComparison.Ordinal)) continue;
                    if (state.name == "Empty" || state.name == "empty") continue;
                    data.MissingReferences.Add($"State '{state.name}' has no motion/clip assigned.");
                }
            }

            // Unreachable states.
            if (settings.DetectUnreachableStates && allStates.Count > 0)
            {
                var reachable = BuildReachabilityMap(allStates, transitions, anyStateTargets, entryStates);
                foreach (var state in allStates)
                {
                    if (reachable.Contains(state.name)) continue;
                    if (IsSpecialState(state.name)) continue;
                    data.UnreachableStates.Add(state.name);
                }
            }

            // Parameter mismatches (regex over MonoScripts in the controller's folder).
            if (settings.DetectParameterMismatches)
            {
                DetectParameterMismatches(path, ac, data);
            }

            return data;
        }

        // Ported: walk the state machine recursively collecting states,
        // entry/default seeds, transitions, and anyState transitions.
        private static void CollectStatesAndTransitions(
            AnimatorStateMachine sm,
            List<AnimatorState> allStates,
            HashSet<string> entryStates,
            List<KeyValuePair<string, string>> transitions,
            List<string> anyStateTargets)
        {
            if (sm.states != null)
            {
                foreach (var child in sm.states)
                {
                    // ChildAnimatorState is a struct; only its `state` field can be null.
                    if (child.state == null) continue;
                    allStates.Add(child.state);
                    if (child.state.transitions != null)
                    {
                        foreach (var t in child.state.transitions)
                        {
                            if (t == null || t.destinationState == null) continue;
                            transitions.Add(new KeyValuePair<string, string>(child.state.name, t.destinationState.name));
                        }
                    }
                }
            }

            if (sm.entryTransitions != null)
            {
                foreach (var et in sm.entryTransitions)
                {
                    if (et == null || et.destinationState == null) continue;
                    entryStates.Add(et.destinationState.name);
                }
            }

            if (sm.defaultState != null)
                entryStates.Add(sm.defaultState.name);

            if (sm.anyStateTransitions != null)
            {
                foreach (var t in sm.anyStateTransitions)
                {
                    if (t == null || t.destinationState == null) continue;
                    anyStateTargets.Add(t.destinationState.name);
                    transitions.Add(new KeyValuePair<string, string>("*", t.destinationState.name));
                }
            }

            if (sm.stateMachines != null)
            {
                foreach (var childSm in sm.stateMachines)
                {
                    // ChildAnimatorStateMachine is a struct; only its `stateMachine` field can be null.
                    if (childSm.stateMachine == null) continue;
                    CollectStatesAndTransitions(childSm.stateMachine, allStates, entryStates, transitions, anyStateTargets);
                }
            }
        }

        // Ported BFS: anyState transitions ("*") expand to every state.
        private static HashSet<string> BuildReachabilityMap(
            List<AnimatorState> allStates,
            List<KeyValuePair<string, string>> transitions,
            List<string> anyStateTargets,
            HashSet<string> entryStates)
        {
            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var state in allStates)
            {
                if (!adjacency.ContainsKey(state.name))
                    adjacency[state.name] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var edge in transitions)
            {
                if (edge.Key == "*")
                {
                    // AnyState: expands to every state's adjacency.
                    foreach (var state in allStates)
                    {
                        if (!adjacency.ContainsKey(state.name))
                            adjacency[state.name] = new HashSet<string>(StringComparer.Ordinal);
                        adjacency[state.name].Add(edge.Value);
                    }
                }
                else
                {
                    if (!adjacency.TryGetValue(edge.Key, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        adjacency[edge.Key] = set;
                    }
                    set.Add(edge.Value);
                }
            }

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(entryStates);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!reachable.Add(current)) continue;
                if (adjacency.TryGetValue(current, out var neighbors))
                {
                    foreach (var n in neighbors)
                        if (!reachable.Contains(n)) queue.Enqueue(n);
                }
            }
            return reachable;
        }

        private static bool IsSpecialState(string name)
        {
            return SpecialStates.Contains(name);
        }

        // Ported: regex-scan MonoScripts in the controller's own folder (cap 50).
        private static void DetectParameterMismatches(string controllerPath, AnimatorController ac, AnimatorData data)
        {
            if (ac.parameters == null || ac.parameters.Length == 0) return;
            var controllerParams = new HashSet<string>(ac.parameters.Select(p => p.name));

            var folder = Path.GetDirectoryName(controllerPath);
            if (string.IsNullOrEmpty(folder)) return;

            string[] scriptGuids;
            try { scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { folder }); }
            catch { return; }
            if (scriptGuids == null) return;

            foreach (var scriptGuid in scriptGuids.Take(50))
            {
                var scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuid);
                if (string.IsNullOrEmpty(scriptPath) || !scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                string text;
                try { text = File.ReadAllText(scriptPath); }
                catch { continue; }

                foreach (Match match in ParamRefRegex.Matches(text))
                {
                    var paramName = match.Groups[1].Value;
                    if (!controllerParams.Contains(paramName))
                    {
                        data.ParameterMismatches.Add(
                            $"Script '{Path.GetFileName(scriptPath)}' references parameter '{paramName}' not found in controller.");
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // Clip analysis (ported curve density + my empty-curve check)
        // -------------------------------------------------------------------

        private static AnimationClipData AnalyzeClip(string path, AnimationAnalysisScanSettings settings)
        {
            AnimationClip clip;
            try { clip = AssetDatabase.LoadMainAssetAtPath(path) as AnimationClip; }
            catch { return null; }
            if (clip == null) return null;

            var data = new AnimationClipData(path)
            {
                Name = clip.name,
                Duration = clip.length,
            };

            try { data.FileSizeBytes = new FileInfo(path).Length; }
            catch { }

            if (settings.DetectExpensiveCurves)
            {
                EditorCurveBinding[] bindings;
                try { bindings = AnimationUtility.GetCurveBindings(clip); }
                catch { bindings = Array.Empty<EditorCurveBinding>(); }

                data.CurveCount = bindings != null ? bindings.Length : 0;
                var totalKeyframes = 0;
                if (bindings != null)
                {
                    foreach (var binding in bindings)
                    {
                        AnimationCurve curve;
                        try { curve = AnimationUtility.GetEditorCurve(clip, binding); }
                        catch { continue; }
                        if (curve != null && curve.keys != null)
                            totalKeyframes += curve.keys.Length;
                    }
                }
                data.TotalKeyframes = totalKeyframes;
                data.KeyframeDensity = clip.length > 0.01f
                    ? (int)(totalKeyframes / clip.length)
                    : totalKeyframes;
            }

            // Empty-clip check (verify-package addition): a .anim that declares
            // curve groups but every one is empty.
            data.HasNoCurves = CheckEmptyCurves(path);

            return data;
        }

        // Reads the .anim YAML and reports whether every curve group is empty.
        private static bool CheckEmptyCurves(string path)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { return false; }

            var hasAnyCurveGroup = false;
            var hasAnyNonEmptyGroup = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var markerFound = false;
                foreach (var marker in CurveGroupMarkers)
                {
                    if (marker.IsMatch(line))
                    {
                        markerFound = true;
                        break;
                    }
                }
                if (!markerFound) continue;

                hasAnyCurveGroup = true;
                if (line.Contains("[]")) continue;

                // Look ahead for a list entry.
                for (var j = i + 1; j < Math.Min(lines.Length, i + 4); j++)
                {
                    var next = lines[j].TrimStart();
                    if (next.Length == 0) continue;
                    if (next.StartsWith("- ", StringComparison.Ordinal))
                    {
                        hasAnyNonEmptyGroup = true;
                        break;
                    }
                    if (next.StartsWith("m_", StringComparison.Ordinal)) break;
                }
            }

            return hasAnyCurveGroup && !hasAnyNonEmptyGroup;
        }

        // -------------------------------------------------------------------
        // Duplicate clips (ported: byte-size collision grouping)
        // -------------------------------------------------------------------

        private static void DetectDuplicateClips(List<AnimationClipData> clips)
        {
            var groups = new Dictionary<long, List<AnimationClipData>>();
            foreach (var clip in clips)
            {
                if (clip.FileSizeBytes <= 0) continue;
                if (!groups.TryGetValue(clip.FileSizeBytes, out var list))
                {
                    list = new List<AnimationClipData>();
                    groups[clip.FileSizeBytes] = list;
                }
                list.Add(clip);
            }

            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                foreach (var clip in group)
                {
                    clip.IsDuplicate = true;
                    clip.DuplicatePaths.Clear();
                    clip.DuplicatePaths.AddRange(group.Where(c => c != clip).Select(c => c.Path));
                }
            }
        }
    }
}
