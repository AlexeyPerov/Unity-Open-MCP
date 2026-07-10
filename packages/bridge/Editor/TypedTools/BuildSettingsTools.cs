using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 9 — typed build pipeline + project-settings tools so agents
    // drive CI build prep, define-symbol management, and settings audits
    // without ad-hoc execute_csharp.
    //
    // Tool map:
    //   - build_get_targets       (read-only)
    //   - build_get_active_target (read-only)
    //   - build_set_target        (mutating — may recompile)
    //   - build_get_scenes        (read-only)
    //   - build_set_scenes        (mutating — rewrites EditorBuildSettings)
    //   - build_start             (mutating — destructive; deny heuristic +
    //                                     gate + confirm_bypass for BuildPlayer)
    //   - build_get_defines       (read-only)
    //   - build_set_defines       (mutating — rewrites ProjectSettings)
    //   - settings_get_player     (read-only)
    //   - settings_set_player     (mutating — ProjectSettings/ProjectSettings.asset)
    //   - settings_get_quality    (read-only)
    //   - settings_set_quality    (mutating — ProjectSettings/QualitySettings.asset)
    //   - settings_get_physics    (read-only)
    //   - settings_set_physics    (mutating — ProjectSettings/DynamicsManager.asset)
    //   - settings_get_lighting   (read-only)
    //   - settings_set_lighting   (mutating — render/lighting settings asset)
    //
    // Gate routing (see BridgeHttpServer KnownTools / DirectResponseTools /
    // MutatingTools):
    //   - The eight *_get_* members are gate-free direct-response reads.
    //   - build_set_target / build_set_scenes / build_set_defines + the four
    //     settings_set_* members run the full gate path with paths_hint scoped
    //     to the touched ProjectSettings asset (see each tool's docstring).
    //   - build_start runs the deny heuristic FIRST (BuildPipeline.BuildPlayer
    //     is on the default deny list — bypass only via gate: "off" +
    //     confirm_bypass: true), then the full gate path.
    //
    // JSON is hand-rolled (no serializer dependency in the bridge — see
    // packages/bridge/AGENTS.md §Transport). Read tools are token-bounded.
    //
    // NOT registry-discovered: wired into BridgeHttpServer.DispatchTool so the
    // snake_case schemas parse the same way as the other M16 typed tools.
    public static class BuildSettingsTools
    {
        // Cap for the build-target enumeration so a future Unity version with a
        // very large BuildTarget enum does not blow the response budget.
        private const int MaxTargets = 64;

        // ============================ Build reads =========================

        // Read-only: enumerate BuildTarget values that resolve to a known group
        // (Unknown is skipped). Gate-free.
        public static ToolDispatchResult GetTargets(string body)
        {
            try
            {
                var values = Enum.GetValues(typeof(BuildTarget));
                var active = EditorUserBuildSettings.activeBuildTarget;
                var sb = new StringBuilder(256 + values.Length * 64);
                sb.Append("{\"status\":\"ok\",\"active\":\"")
                  .Append(EscName(active.ToString()))
                  .Append("\",\"activeGroup\":\"")
                  .Append(EscName(EditorUserBuildSettings.selectedBuildTargetGroup.ToString()))
                  .Append("\",\"targets\":[");
                int emitted = 0;
                // Enum.GetValues returns values in declared order, which mixes
                // active + inactive targets. We sort by name for stable output.
                var names = Enum.GetNames(typeof(BuildTarget));
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    if (emitted >= MaxTargets) break;
                    if (!Enum.TryParse<BuildTarget>(name, out var bt)) continue;
                    if ((int)bt < 0) continue;
                    var group = BuildPipeline.GetBuildTargetGroup(bt);
                    if (group == BuildTargetGroup.Unknown) continue;

                    bool installed;
                    try { installed = BuildPipeline.IsBuildTargetSupported(group, bt); }
                    catch { installed = false; }

                    if (emitted > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(EscName(bt.ToString()));
                    sb.Append("\",\"group\":\"").Append(EscName(group.ToString()));
                    sb.Append("\",\"installed\":").Append(installed ? "true" : "false");
                    sb.Append(",\"isActive\":").Append(bt == active ? "true" : "false");
                    sb.Append('}');
                    emitted++;
                }
                sb.Append("],\"count\":").Append(emitted);
                sb.Append(",\"truncated\":").Append(Math.Max(0, names.Length - emitted));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: active build target + group. Gate-free.
        public static ToolDispatchResult GetActiveTarget(string body)
        {
            try
            {
                var sb = new StringBuilder(96);
                sb.Append("{\"status\":\"ok\",\"target\":\"")
                  .Append(EscName(EditorUserBuildSettings.activeBuildTarget.ToString()));
                sb.Append("\",\"group\":\"")
                  .Append(EscName(EditorUserBuildSettings.selectedBuildTargetGroup.ToString()));
                sb.Append("\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: scenes currently in EditorBuildSettings. Gate-free.
        public static ToolDispatchResult GetScenes(string body)
        {
            try
            {
                var scenes = EditorBuildSettings.scenes;
                var sb = new StringBuilder(64 + scenes.Length * 96);
                sb.Append("{\"status\":\"ok\",\"count\":").Append(scenes.Length);
                sb.Append(",\"scenes\":[");
                for (int i = 0; i < scenes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var s = scenes[i];
                    sb.Append("{\"path\":\"").Append(EscName(s.path ?? ""));
                    sb.Append("\",\"enabled\":").Append(s.enabled ? "true" : "false");
                    sb.Append(",\"guid\":\"").Append(EscName(s.guid.ToString()));
                    sb.Append("\"}");
                }
                sb.Append("]}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: scripting define symbols for the active build target
        // group. Gate-free.
        public static ToolDispatchResult GetDefines(string body)
        {
            try
            {
                var namedTarget = ResolveNamedBuildTarget();
                var group = EditorUserBuildSettings.selectedBuildTargetGroup;
                var raw = PlayerSettings.GetScriptingDefineSymbols(namedTarget);
                var list = SplitDefines(raw);
                var sb = new StringBuilder(64 + list.Count * 32);
                sb.Append("{\"status\":\"ok\",\"group\":\"")
                  .Append(EscName(group.ToString()));
                sb.Append("\",\"namedTarget\":\"")
                  .Append(EscName(namedTarget.TargetName));
                sb.Append("\",\"defines\":\"").Append(EscName(raw ?? ""));
                sb.Append("\",\"list\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscName(list[i])).Append('"');
                }
                sb.Append("]}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ============================ Build mutators ======================

        // Mutating: switch the active build target. May trigger a recompile +
        // domain reload. paths_hint scope is the ProjectSettings folder (target
        // switch rewrites ProjectSettings/*.asset
        // files — Library/BuildPlayerAsset is not an asset).
        public static ToolDispatchResult SetTarget(string body)
        {
            var targetStr = JsonBody.GetString(body, "target");
            if (string.IsNullOrWhiteSpace(targetStr))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'target' is required (a BuildTarget name, e.g. 'StandaloneWindows64').");

            if (!Enum.TryParse<BuildTarget>(targetStr.Trim(), true, out var target))
                return ToolDispatchResult.Fail("unknown_target",
                    $"Unknown build target: '{targetStr}'. Use build_get_targets to enumerate valid BuildTarget names.");

            try
            {
                var group = BuildPipeline.GetBuildTargetGroup(target);
                if (group == BuildTargetGroup.Unknown)
                    return ToolDispatchResult.Fail("unknown_target",
                        $"Build target '{target}' has no known group.");
                bool success = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
                var sb = new StringBuilder(128);
                sb.Append("{\"status\":").Append(success ? "\"ok\"" : "\"failed\"");
                sb.Append(",\"success\":").Append(success ? "true" : "false");
                sb.Append(",\"target\":\"").Append(EscName(target.ToString()));
                sb.Append("\",\"group\":\"").Append(EscName(group.ToString()));
                sb.Append("\",\"note\":\"Switching the active build target can trigger a " +
                          "recompile / domain reload; poll editor_status or compile_check " +
                          "to confirm.\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Mutating: replace the EditorBuildSettings scene list. Folds UCP
        // build/set-scenes. Accepts scene entries as `{path, enabled}` objects
        // or bare path strings (treated as enabled). paths_hint scope is
        // ProjectSettings (EditorBuildSettings.asset is under ProjectSettings/).
        public static ToolDispatchResult SetScenes(string body)
        {
            // Parse the scenes[] array ourselves: entries may be objects or
            // bare strings, and JsonBody.GetObjectArray drops bare-string
            // entries. We walk the raw array and dispatch each entry by its
            // leading character.
            var rawArray = JsonBody.GetRawValue(body, "scenes");
            if (string.IsNullOrWhiteSpace(rawArray) || rawArray.Trim() == "null")
                return ToolDispatchResult.Fail("missing_parameter",
                    "'scenes' is required and must be a non-empty array of " +
                    "{path, enabled?} objects or bare path strings.");

            var entries = ParseArrayEntries(rawArray);
            if (entries.Count == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'scenes' is required and must be a non-empty array of " +
                    "{path, enabled?} objects or bare path strings.");

            try
            {
                var list = new List<EditorBuildSettingsScene>();
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    var trimmedEntry = entry.Trim();
                    string path;
                    bool enabled = true;
                    if (trimmedEntry.StartsWith("{"))
                    {
                        // Object entry — extract path + enabled. GetObjectArray
                        // would have stripped the braces; here we keep them so
                        // JsonBody.GetString / GetBool see a full object.
                        path = JsonBody.GetString(trimmedEntry, "path");
                        enabled = JsonBody.GetBool(trimmedEntry, "enabled", true);
                    }
                    else
                    {
                        // Bare string entry — strip surrounding quotes.
                        path = trimmedEntry.Trim('"');
                    }
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    list.Add(new EditorBuildSettingsScene(path, enabled));
                }

                if (list.Count == 0)
                    return ToolDispatchResult.Fail("invalid_scenes",
                        "No valid scene entries parsed. Each entry must be " +
                        "{path, enabled?} or a bare path string.");

                EditorBuildSettings.scenes = list.ToArray();

                var sb = new StringBuilder(64 + list.Count * 16);
                sb.Append("{\"status\":\"ok\",\"action\":\"set_scenes\",\"count\":")
                  .Append(list.Count);
                sb.Append(",\"note\":\"EditorBuildSettings rewritten. A reimport of the " +
                          "scene list may follow; poll assets_refresh if downstream " +
                          "tools need it.\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Walk a JSON array and return each top-level element verbatim (the
        // substring inside the surrounding brackets is split on top-level
        // commas, with strings/objects/arrays skipped over so commas inside
        // them do not split). Unlike JsonBody.GetObjectArray, this preserves
        // bare-string entries alongside object entries.
        private static List<string> ParseArrayEntries(string rawArray)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(rawArray)) return result;
            var v = rawArray.Trim();
            if (!v.StartsWith("[")) return result;
            // Skip the leading '['.
            int i = 1;
            while (i < v.Length)
            {
                while (i < v.Length && char.IsWhiteSpace(v[i])) i++;
                if (i >= v.Length || v[i] == ']') break;

                int start = i;
                // Advance to the next top-level comma.
                if (v[i] == '"')
                {
                    i++;
                    while (i < v.Length)
                    {
                        if (v[i] == '\\') { i += 2; continue; }
                        if (v[i] == '"') { i++; break; }
                        i++;
                    }
                }
                else if (v[i] == '{' || v[i] == '[')
                {
                    var open = v[i];
                    var close = open == '{' ? '}' : ']';
                    int depth = 1;
                    i++;
                    while (i < v.Length && depth > 0)
                    {
                        if (v[i] == '"')
                        {
                            i++;
                            while (i < v.Length)
                            {
                                if (v[i] == '\\') { i += 2; continue; }
                                if (v[i] == '"') { i++; break; }
                                i++;
                            }
                            continue;
                        }
                        if (v[i] == open) depth++;
                        else if (v[i] == close) depth--;
                        i++;
                    }
                }
                else
                {
                    while (i < v.Length && v[i] != ',' && v[i] != ']') i++;
                }
                result.Add(v.Substring(start, i - start).Trim());
                while (i < v.Length && (v[i] == ',' || char.IsWhiteSpace(v[i]))) i++;
            }
            return result;
        }

        // Mutating: set scripting define symbols for the active build target
        // group. Accepts an array (joined with ';') or a pre-joined ';' string.
        // An empty array (or "") CLEARS the defines.
        // paths_hint scope is ProjectSettings (ProjectSettings/ProjectSettings.asset
        // holds the defines block).
        public static ToolDispatchResult SetDefines(string body)
        {
            // Distinguish "field absent" (missing_parameter) from "field present
            // but empty" (clear). JsonBody returns null/empty for both, so we
            // probe the raw body for the key presence first.
            if (!HasField(body, "defines"))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'defines' is required — either an array of symbols or a " +
                    "';'-joined string. Pass an empty array (or \"\") to clear.");

            var definesArray = JsonBody.GetStringArray(body, "defines");
            string definesStr = JsonBody.GetString(body, "defines");

            string defines;
            if (definesArray != null && definesArray.Length > 0)
            {
                defines = string.Join(";", definesArray
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim()));
            }
            else
            {
                // Empty array OR a (possibly empty) pre-joined string. An empty
                // value clears the defines.
                defines = definesStr ?? "";
            }

            try
            {
                var namedTarget = ResolveNamedBuildTarget();
                var group = EditorUserBuildSettings.selectedBuildTargetGroup;
                PlayerSettings.SetScriptingDefineSymbols(namedTarget, defines);

                var sb = new StringBuilder(96);
                sb.Append("{\"status\":\"ok\",\"action\":\"set_defines\"");
                sb.Append(",\"group\":\"").Append(EscName(group.ToString()));
                sb.Append("\",\"namedTarget\":\"").Append(EscName(namedTarget.TargetName));
                sb.Append("\",\"defines\":\"").Append(EscName(defines));
                sb.Append("\",\"note\":\"Changing scripting define symbols triggers a " +
                          "recompile; poll compile_check / editor_status to confirm.\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Mutating + destructive: trigger a player build via
        // BuildPipeline.BuildPlayer. BuildPlayer is on the default deny list
        // (BridgeDenyList.DefaultCSharpPatterns), so this typed tool mirrors the
        // destructive-menu pattern: it refuses UNLESS the request passes
        // gate: "off" + confirm_bypass: true (BridgeDenyBypass.IsRequestedFromBody).
        // When bypassed, the full gate path still runs so the response carries
        // the post-build asset-reference fallout.
        //
        // paths_hint scope: the destination build output path (an asset-adjacent
        // folder) plus any ProjectSettings assets touched by the build pipeline.
        // Agents typically scope this to the build output folder.
        public static ToolDispatchResult StartBuild(string body)
        {
            // Deny heuristic — same bypass contract as execute_csharp /
            // execute_menu. The typed tool itself is the "scoped typed tool"
            // alternative the deny message points at, but a player build is
            // still destructive (multi-minute, writes a binary player) so we
            // require the explicit bypass.
            var bypass = BridgeDenyBypass.IsRequestedFromBody(body);
            if (!bypass)
                return ToolDispatchResult.Fail("build_confirmation_required",
                    "build_start triggers BuildPipeline.BuildPlayer, which is destructive " +
                    "(multi-minute, writes a binary player outside Assets/). The deny " +
                    "heuristic blocks it by default. Retry with gate: \"off\" AND " +
                    "confirm_bypass: true to proceed and accept the risk. " +
                    "Matched pattern: BuildPipeline.BuildPlayer.");

            var outputPath = JsonBody.GetString(body, "output_path");
            bool development = JsonBody.GetBool(body, "development", false);
            bool allowDebugging = JsonBody.GetBool(body, "allow_debugging", false);

            try
            {
                var scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                    .Select(s => s.path)
                    .ToArray();

                if (scenes.Length == 0)
                    return ToolDispatchResult.Fail("no_scenes",
                        "No enabled scenes in build settings. Use build_set_scenes " +
                        "to populate the build scene list first.");

                if (string.IsNullOrWhiteSpace(outputPath))
                    outputPath = "Builds/" + EditorUserBuildSettings.activeBuildTarget + "/Build";

                var options = BuildOptions.None;
                if (development) options |= BuildOptions.Development;
                if (allowDebugging) options |= BuildOptions.AllowDebugging;

                var buildOptions = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = EditorUserBuildSettings.activeBuildTarget,
                    targetGroup = EditorUserBuildSettings.selectedBuildTargetGroup,
                    options = options,
                };

                var report = BuildPipeline.BuildPlayer(buildOptions);
                return ToolDispatchResult.Ok(BuildReportJson(report, outputPath, options));
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("build_failed", e.Message);
            }
        }

        // ============================ Settings reads ======================

        // Read-only: PlayerSettings snapshot for the active build target.
        // Gate-free.
        public static ToolDispatchResult SettingsGetPlayer(string body)
        {
            try
            {
                var namedTarget = ResolveNamedBuildTarget();
                var activeTarget = EditorUserBuildSettings.activeBuildTarget;
                var graphicsApis = PlayerSettings.GetGraphicsAPIs(activeTarget);
                var firstApi = graphicsApis != null && graphicsApis.Length > 0
                    ? graphicsApis[0].ToString()
                    : "Unknown";

                var sb = new StringBuilder(384);
                sb.Append("{\"status\":\"ok\",\"namedTarget\":\"")
                  .Append(EscName(namedTarget.TargetName)).Append("\"");
                sb.Append(",\"companyName\":").Append(Q(PlayerSettings.companyName));
                sb.Append(",\"productName\":").Append(Q(PlayerSettings.productName));
                sb.Append(",\"bundleVersion\":").Append(Q(PlayerSettings.bundleVersion));
                sb.Append(",\"defaultIsNativeResolution\":").Append(PlayerSettings.defaultIsNativeResolution ? "true" : "false");
                sb.Append(",\"runInBackground\":").Append(PlayerSettings.runInBackground ? "true" : "false");
                sb.Append(",\"colorSpace\":").Append(Q(PlayerSettings.colorSpace.ToString()));
                sb.Append(",\"graphicsApi\":").Append(Q(firstApi));
                sb.Append(",\"scriptingBackend\":").Append(Q(PlayerSettings.GetScriptingBackend(namedTarget).ToString()));
                sb.Append(",\"apiCompatibilityLevel\":").Append(Q(PlayerSettings.GetApiCompatibilityLevel(namedTarget).ToString()));
                int inputHandler = ReadSerializedPlayerInt("activeInputHandler");
                sb.Append(",\"activeInputHandler\":").Append(inputHandler);
                sb.Append(",\"activeInputHandlerName\":").Append(Q(DescribeActiveInputHandler(inputHandler)));
                sb.Append(",\"targetFrameRate\":").Append(Application.targetFrameRate);
                sb.Append(",\"defaultScreenWidth\":").Append(PlayerSettings.defaultScreenWidth);
                sb.Append(",\"defaultScreenHeight\":").Append(PlayerSettings.defaultScreenHeight);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: QualitySettings snapshot. Gate-free. Folds UCP
        // settings/quality.
        public static ToolDispatchResult SettingsGetQuality(string body)
        {
            try
            {
                var names = QualitySettings.names;
                var current = QualitySettings.GetQualityLevel();
                var sb = new StringBuilder(128 + names.Length * 48);
                sb.Append("{\"status\":\"ok\",\"currentLevel\":").Append(current);
                sb.Append(",\"currentName\":").Append(Q(names.Length > 0 && current >= 0 && current < names.Length ? names[current] : ""));
                sb.Append(",\"levels\":[");
                for (int i = 0; i < names.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"index\":").Append(i);
                    sb.Append(",\"name\":").Append(Q(names[i]));
                    sb.Append(",\"isCurrent\":").Append(i == current ? "true" : "false");
                    sb.Append('}');
                }
                sb.Append("],\"shadowDistance\":").Append(Num(QualitySettings.shadowDistance));
                sb.Append(",\"shadowCascades\":").Append(QualitySettings.shadowCascades);
                sb.Append(",\"antiAliasing\":").Append(QualitySettings.antiAliasing);
                sb.Append(",\"vSyncCount\":").Append(QualitySettings.vSyncCount);
                sb.Append(",\"pixelLightCount\":").Append(QualitySettings.pixelLightCount);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: Physics + Physics2D settings. Gate-free. Folds UCP
        // settings/physics.
        public static ToolDispatchResult SettingsGetPhysics(string body)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("{\"status\":\"ok\",\"gravity\":[")
                  .Append(Num(Physics.gravity.x)).Append(',')
                  .Append(Num(Physics.gravity.y)).Append(',')
                  .Append(Num(Physics.gravity.z)).Append(']');
                sb.Append(",\"defaultSolverIterations\":").Append(Physics.defaultSolverIterations);
                sb.Append(",\"defaultSolverVelocityIterations\":").Append(Physics.defaultSolverVelocityIterations);
                sb.Append(",\"bounceThreshold\":").Append(Num(Physics.bounceThreshold));
                sb.Append(",\"sleepThreshold\":").Append(Num(Physics.sleepThreshold));
                sb.Append(",\"defaultContactOffset\":").Append(Num(Physics.defaultContactOffset));
                sb.Append(",\"simulationMode\":").Append(Q(Physics.simulationMode.ToString()));
                sb.Append(",\"physics2DGravity\":[")
                  .Append(Num(Physics2D.gravity.x)).Append(',')
                  .Append(Num(Physics2D.gravity.y)).Append(']');
                sb.Append(",\"physics2DSimulationMode\":").Append(Q(Physics2D.simulationMode.ToString()));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: Render/Lighting settings snapshot. Gate-free. Folds UCP
        // settings/lighting. RenderSettings is scene-scoped in the live Editor
        // — reads reflect the currently-active scene's lighting setup.
        public static ToolDispatchResult SettingsGetLighting(string body)
        {
            try
            {
                var c = RenderSettings.ambientLight;
                var fc = RenderSettings.fogColor;
                var sb = new StringBuilder(320);
                sb.Append("{\"status\":\"ok\",\"ambientMode\":").Append(Q(RenderSettings.ambientMode.ToString()));
                sb.Append(",\"ambientIntensity\":").Append(Num(RenderSettings.ambientIntensity));
                sb.Append(",\"ambientColor\":[").Append(Num(c.r)).Append(',').Append(Num(c.g)).Append(',').Append(Num(c.b)).Append(',').Append(Num(c.a)).Append(']');
                sb.Append(",\"fog\":").Append(RenderSettings.fog ? "true" : "false");
                sb.Append(",\"fogMode\":").Append(Q(RenderSettings.fogMode.ToString()));
                sb.Append(",\"fogDensity\":").Append(Num(RenderSettings.fogDensity));
                sb.Append(",\"fogColor\":[").Append(Num(fc.r)).Append(',').Append(Num(fc.g)).Append(',').Append(Num(fc.b)).Append(',').Append(Num(fc.a)).Append(']');
                sb.Append(",\"fogStartDistance\":").Append(Num(RenderSettings.fogStartDistance));
                sb.Append(",\"fogEndDistance\":").Append(Num(RenderSettings.fogEndDistance));
                var skybox = RenderSettings.skybox;
                sb.Append(",\"skybox\":").Append(skybox != null ? Q(skybox.name) : "null");
                var sun = RenderSettings.sun;
                sb.Append(",\"sunSource\":").Append(sun != null && sun.gameObject != null ? Q(sun.gameObject.name) : "null");
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ============================ Settings mutators ===================

        // Mutating: set PlayerSettings fields by key/value pairs. Folds UCP
        // settings/player-set. paths_hint scope: ProjectSettings/ProjectSettings.asset.
        // Writes are batched; per-key failures are accumulated and the batch
        // still applies the valid keys.
        public static ToolDispatchResult SettingsSetPlayer(string body)
        {
            var patches = JsonBody.GetObjectArray(body, "fields");
            if (patches == null || patches.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'fields' is required and must be a non-empty array of " +
                    "{key, value} patches. See the tool description for supported keys.");

            try
            {
                var applied = new List<string>();
                var warnings = new List<string>();
                foreach (var raw in patches)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    var key = JsonBody.GetString(raw, "key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        warnings.Add("Skipped a patch with no 'key'.");
                        continue;
                    }
                    // value may be a string / number / boolean / null. We read
                    // it raw so the registry setter can parse it appropriately.
                    var valueRaw = JsonBody.GetRawValue(raw, "value");
                    var warn = ApplyPlayerSetting(key, valueRaw);
                    if (warn != null) warnings.Add(warn);
                    else applied.Add(key);
                }

                if (applied.Count == 0)
                    return ToolDispatchResult.Fail("no_applicable_keys",
                        "No PlayerSettings keys were applied. " +
                        (warnings.Count > 0 ? string.Join(" ", warnings) : ""));

                AssetDatabase.SaveAssets();
                var sb = new StringBuilder(128 + applied.Count * 24 + warnings.Count * 64);
                sb.Append("{\"status\":\"ok\",\"action\":\"set_player\"");
                sb.Append(",\"applied\":[");
                for (int i = 0; i < applied.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscName(applied[i])).Append('"');
                }
                sb.Append(']');
                AppendWarnings(sb, warnings);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Mutating: set QualitySettings fields by key/value pairs. Folds UCP
        // settings/quality-set. paths_hint scope: ProjectSettings/QualitySettings.asset.
        public static ToolDispatchResult SettingsSetQuality(string body)
        {
            return ApplyKeyedSettings(body, "set_quality", ApplyQualitySetting);
        }

        // Mutating: set Physics / Physics2D fields by key/value pairs. Folds UCP
        // settings/physics-set. paths_hint scope: ProjectSettings/DynamicsManager.asset.
        public static ToolDispatchResult SettingsSetPhysics(string body)
        {
            return ApplyKeyedSettings(body, "set_physics", ApplyPhysicsSetting);
        }

        // Mutating: set render/lighting fields by key/value pairs. Folds UCP
        // settings/lighting-set. paths_hint scope: the render/lighting settings
        // asset (RenderSettings is scene-scoped, so the active scene is marked
        // dirty rather than a single ProjectSettings asset written — agents
        // should also scope paths_hint to the active scene path).
        public static ToolDispatchResult SettingsSetLighting(string body)
        {
            return ApplyKeyedSettings(body, "set_lighting", ApplyLightingSetting);
        }

        // ============================ Settings remainder (T20.9.3) ===========
        //
        // Time, Render Pipeline (read-only), Quality level. These close the
        // last Project Settings parity gap. Time + quality-level write
        // ProjectSettings assets (full gate path); render_pipeline is a
        // read-only probe (no setter — switching SRP is a package operation).

        // Read-only: TimeManager snapshot (fixedDeltaTime / timeScale /
        // maximumDeltaTime / captureFramerate via Time + the TimeManager
        // singleton). Gate-free. The runtime Time values reflect the editor's
        // current TimeManager.asset.
        public static ToolDispatchResult SettingsGetTime(string body)
        {
            try
            {
                var sb = new StringBuilder(192);
                sb.Append("{\"status\":\"ok\",\"timeScale\":").Append(Num(Time.timeScale));
                sb.Append(",\"fixedDeltaTime\":").Append(Num(Time.fixedDeltaTime));
                sb.Append(",\"maximumDeltaTime\":").Append(Num(Time.maximumDeltaTime));
                sb.Append(",\"smoothDeltaTime\":").Append(Num(Time.smoothDeltaTime));
                sb.Append(",\"captureFramerate\":").Append(Time.captureFramerate);
                // TimeManager.asset-backed fields are surfaced via the
                // SerializedObject so the on-disk values are reported (the
                // runtime Time.* reads above are the live mirror).
                var tm = LoadTimeManager();
                if (tm != null)
                {
                    using (var so = new SerializedObject(tm))
                    {
                        sb.Append(",\"fixedDeltaTimeSetting\":").Append(Num(so.FindProperty("Fixed Timestep")?.floatValue ?? Time.fixedDeltaTime));
                        sb.Append(",\"timeScaleSetting\":").Append(Num(so.FindProperty("Time Scale")?.floatValue ?? Time.timeScale));
                        sb.Append(",\"maximumDeltaTimeSetting\":").Append(Num(so.FindProperty("Maximum Allowed Timestep")?.floatValue ?? Time.maximumDeltaTime));
                    }
                }
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Mutating: patch the TimeManager fields. paths_hint scope:
        // ProjectSettings/TimeManager.asset. Writes the asset then reloads the
        // singleton so Time.* reflects the change in-editor.
        public static ToolDispatchResult SettingsSetTime(string body)
        {
            var patches = JsonBody.GetObjectArray(body, "fields");
            if (patches == null || patches.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'fields' is required and must be a non-empty array of " +
                    "{key, value} patches. Supported keys: fixedDeltaTime, " +
                    "timeScale, maximumDeltaTime, captureFramerate.");

            try
            {
                var tm = LoadTimeManager();
                if (tm == null)
                    return ToolDispatchResult.Fail("asset_not_found",
                        "ProjectSettings/TimeManager.asset not found.");

                var applied = new List<string>();
                var warnings = new List<string>();
                using (var so = new SerializedObject(tm))
                {
                    foreach (var raw in patches)
                    {
                        if (string.IsNullOrEmpty(raw)) continue;
                        var key = JsonBody.GetString(raw, "key");
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            warnings.Add("Skipped a patch with no 'key'.");
                            continue;
                        }
                        var valueRaw = JsonBody.GetRawValue(raw, "value");
                        var warn = ApplyTimeSetting(so, key, valueRaw);
                        if (warn != null) warnings.Add(warn);
                        else applied.Add(key);
                    }
                    if (applied.Count > 0) so.ApplyModifiedProperties();
                }

                if (applied.Count == 0)
                    return ToolDispatchResult.Fail("no_applicable_keys",
                        "No TimeManager keys were applied. " +
                        (warnings.Count > 0 ? string.Join(" ", warnings) : ""));

                AssetDatabase.SaveAssets();

                var sb = new StringBuilder(96 + applied.Count * 24 + warnings.Count * 64);
                sb.Append("{\"status\":\"ok\",\"action\":\"set_time\"");
                sb.Append(",\"applied\":[");
                for (int i = 0; i < applied.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscName(applied[i])).Append('"');
                }
                sb.Append(']');
                AppendWarnings(sb, warnings);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Read-only: detect the active Scriptable Render Pipeline (Built-in /
        // URP / HDRP) by inspecting GraphicsSettings.currentRenderPipeline +
        // the default pipeline asset type. Gate-free. No setter — switching
        // SRP is a package-level operation (package_add / package_remove); an
        // agent that needs it should install/swap the render pipeline package,
        // not tweak a settings asset.
        public static ToolDispatchResult SettingsGetRenderPipeline(string body)
        {
            try
            {
                string pipeline;
                string assetPath;
                string shaderName;
                var current = GraphicsSettings.currentRenderPipeline;
                if (current != null)
                {
                    var t = current.GetType().FullName ?? current.GetType().Name;
                    if (t.Contains("HD") || t.Contains("HighDefinition"))
                    {
                        pipeline = "HDRP";
                        shaderName = "HDRP/Lit";
                    }
                    else if (t.Contains("Universal") || t.Contains("URP"))
                    {
                        pipeline = "URP";
                        shaderName = "Universal Render Pipeline/Lit";
                    }
                    else
                    {
                        pipeline = t;
                        shaderName = t;
                    }
                    assetPath = AssetDatabase.GetAssetPath(current);
                }
                else
                {
                    pipeline = "Built-in";
                    shaderName = "Standard";
                    assetPath = "";
                }

                var sb = new StringBuilder(160);
                sb.Append("{\"status\":\"ok\",\"pipeline\":").Append(Q(pipeline));
                sb.Append(",\"defaultShader\":").Append(Q(shaderName));
                sb.Append(",\"renderPipelineAsset\":").Append(string.IsNullOrEmpty(assetPath) ? "null" : Q(assetPath));
                sb.Append(",\"hasSetter\":false");
                sb.Append(",\"note\":\"Switching SRP is a package-level operation — use package_add / package_remove to install or swap the render pipeline package (com.unity.render-pipelines.universal / com.unity.render-pipelines.high-definition).\"");
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Mutating: set the active quality level (optionally per-platform).
        // paths_hint scope: ProjectSettings/Quality.asset. quality_level may be
        // a name or an index; platform (omit = all platforms) is a build-target
        // name.
        public static ToolDispatchResult SettingsSetQualityLevel(string body)
        {
            var levelRaw = JsonBody.GetRawValue(body, "quality_level");
            if (string.IsNullOrWhiteSpace(levelRaw))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'quality_level' is required (a level name or index).");
            var platform = JsonBody.GetString(body, "platform");

            try
            {
                var names = QualitySettings.names;
                int level = ResolveQualityLevel(levelRaw, names);
                if (level < 0)
                    return ToolDispatchResult.Fail("invalid_quality_level",
                        $"quality_level '{AsString(levelRaw)}' did not match a level name or index. " +
                        $"Available: {string.Join(", ", names)}.");

                if (string.IsNullOrWhiteSpace(platform))
                {
                    // No platform → switch the active level globally.
                    QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
                    AssetDatabase.SaveAssets();
                    var after = QualitySettings.GetQualityLevel();
                    var sbg = new StringBuilder(128);
                    sbg.Append("{\"status\":\"ok\",\"action\":\"set_quality_level\"");
                    sbg.Append(",\"requestedLevel\":").Append(level);
                    sbg.Append(",\"requestedName\":").Append(Q(level < names.Length ? names[level] : ""));
                    sbg.Append(",\"activeLevel\":").Append(after);
                    sbg.Append(",\"activeName\":").Append(Q(names.Length > 0 && after >= 0 && after < names.Length ? names[after] : ""));
                    sbg.Append(",\"platform\":null}");
                    return ToolDispatchResult.Ok(sbg.ToString());
                }

                // Per-platform. Unity's public QualitySettings API does not
                // expose a per-platform active-level setter (the per-platform
                // quality is encoded in Quality.asset and toggled via the
                // editor UI). We validate the platform name, set the global
                // level, and report the requested platform scope so the agent
                // knows the intent — an unrecognized platform surfaces a
                // warning. For precise per-platform control, agents should use
                // execute_csharp against the internal QualitySettings API.
                bool recognized = Enum.TryParse(platform, true, out BuildTarget _);
                QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
                AssetDatabase.SaveAssets();
                var active = QualitySettings.GetQualityLevel();
                var sbp = new StringBuilder(192);
                sbp.Append("{\"status\":\"ok\",\"action\":\"set_quality_level\"");
                sbp.Append(",\"requestedLevel\":").Append(level);
                sbp.Append(",\"requestedName\":").Append(Q(level < names.Length ? names[level] : ""));
                sbp.Append(",\"activeLevel\":").Append(active);
                sbp.Append(",\"activeName\":").Append(Q(names.Length > 0 && active >= 0 && active < names.Length ? names[active] : ""));
                sbp.Append(",\"platform\":").Append(Q(platform));
                if (!recognized)
                {
                    sbp.Append(",\"warnings\":[\"Unrecognized platform '").Append(EscName(platform));
                    sbp.Append("'; applied the level globally.\"]}");
                }
                else
                {
                    sbp.Append(",\"note\":\"Per-platform active-level setter is not exposed in the public API; the global level was set. Use execute_csharp for precise per-platform control.\"}");
                }
                return ToolDispatchResult.Ok(sbp.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        private static int ResolveQualityLevel(string levelRaw, string[] names)
        {
            var s = AsString(levelRaw).Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                if (idx >= 0 && idx < names.Length) return idx;
            }
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], s, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // Apply one TimeManager patch to the SerializedObject. Returns null on
        // success, a warning string on failure. The per-key mapping lives in
        // TimeSetters (one delegate per key, aliases registered explicitly).
        // captureFramerate is a runtime-only Time value (not serialized in
        // TimeManager.asset) — its setter ignores the SerializedObject and
        // applies via the Time static API directly.
        private static string ApplyTimeSetting(SerializedObject so, string key, string valueRaw)
        {
            if (TimeSetters.TryGetValue(key, out var setter))
            {
                try { return setter(so, valueRaw); }
                catch (Exception e) { return $"Could not apply time setting '{key}': {e.Message}"; }
            }
            return $"Unknown time setting key: '{key}'. Supported: fixedDeltaTime, timeScale, maximumDeltaTime, captureFramerate.";
        }

        // key → (SerializedObject, valueRaw) → null/warning. Float fields share
        // SetTimeFloat; captureFramerate ignores the SerializedObject.
        private delegate string TimeSetter(SerializedObject so, string valueRaw);

        private static readonly Dictionary<string, TimeSetter> TimeSetters =
            new Dictionary<string, TimeSetter>(StringComparer.Ordinal)
            {
                ["fixedDeltaTime"]              = (so, v) => SetTimeFloat(so, "Fixed Timestep", v),
                ["Fixed Timestep"]              = (so, v) => SetTimeFloat(so, "Fixed Timestep", v),
                ["timeScale"]                   = (so, v) => SetTimeFloat(so, "Time Scale", v),
                ["Time Scale"]                  = (so, v) => SetTimeFloat(so, "Time Scale", v),
                ["maximumDeltaTime"]            = (so, v) => SetTimeFloat(so, "Maximum Allowed Timestep", v),
                ["Maximum Allowed Timestep"]    = (so, v) => SetTimeFloat(so, "Maximum Allowed Timestep", v),
                ["captureFramerate"]            = (so, v) => { Time.captureFramerate = AsInt(v); return null; },
            };

        // Write a TimeManager float property. Returns null on success, a warning
        // when the property is absent on this Unity version.
        private static string SetTimeFloat(SerializedObject so, string propName, string valueRaw)
        {
            var prop = so.FindProperty(propName);
            if (prop == null) return $"TimeManager property '{propName}' not found on this Unity version.";
            prop.floatValue = AsFloat(valueRaw);
            return null;
        }

        // Load the TimeManager.asset (ProjectSettings). Returns null when absent.
        private static UnityEngine.Object LoadTimeManager()
        {
            const string path = "ProjectSettings/TimeManager.asset";
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) return asset;
            // Fallback: some installs only expose it via the internal API.
            return null;
        }

        // ----------------------- keyed settings helper -------------------

        // Shared body for the settings_set_* mutators: parse the fields[] array,
        // dispatch each entry to the per-domain applier, accumulate applied keys
        // and per-key warnings, and persist via AssetDatabase.SaveAssets (or
        // mark the scene dirty for lighting). Returns a uniform ok envelope.
        private delegate string KeyedApplier(string key, string valueRaw);

        private static ToolDispatchResult ApplyKeyedSettings(string body, string action, KeyedApplier applier)
        {
            var patches = JsonBody.GetObjectArray(body, "fields");
            if (patches == null || patches.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'fields' is required and must be a non-empty array of " +
                    "{key, value} patches. See the tool description for supported keys.");

            try
            {
                var applied = new List<string>();
                var warnings = new List<string>();
                foreach (var raw in patches)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    var key = JsonBody.GetString(raw, "key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        warnings.Add("Skipped a patch with no 'key'.");
                        continue;
                    }
                    var valueRaw = JsonBody.GetRawValue(raw, "value");
                    var warn = applier(key, valueRaw);
                    if (warn != null) warnings.Add(warn);
                    else applied.Add(key);
                }

                if (applied.Count == 0)
                    return ToolDispatchResult.Fail("no_applicable_keys",
                        "No keys were applied. " +
                        (warnings.Count > 0 ? string.Join(" ", warnings) : ""));

                AssetDatabase.SaveAssets();
                var sb = new StringBuilder(128 + applied.Count * 24 + warnings.Count * 64);
                sb.Append("{\"status\":\"ok\",\"action\":\"").Append(action).Append("\"");
                sb.Append(",\"applied\":[");
                for (int i = 0; i < applied.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscName(applied[i])).Append('"');
                }
                sb.Append(']');
                AppendWarnings(sb, warnings);
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // Returns null on success, an error/warning string on failure. The
        // per-key mapping lives in PlayerSetters (one delegate per key).
        private static string ApplyPlayerSetting(string key, string valueRaw)
        {
            if (PlayerSetters.TryGetValue(key, out var setter))
            {
                try { return setter(valueRaw); }
                catch (Exception e) { return $"Could not apply player setting '{key}': {e.Message}"; }
            }
            return $"Unknown player setting key: '{key}'.";
        }

        // key → valueRaw → null/warning. One-line registration per key;
        // aliases (activeInputHandler / activeInputHandling / inputHandling)
        // share the same handler.
        private delegate string PlayerSetter(string valueRaw);

        private static string SetPlayerActiveInputHandler(string valueRaw)
        {
            var parsed = ParseActiveInputHandler(valueRaw, out var inputErr);
            if (inputErr != null) return inputErr;
            WriteSerializedPlayerInt("activeInputHandler", parsed);
            return null;
        }

        private static readonly Dictionary<string, PlayerSetter> PlayerSetters =
            new Dictionary<string, PlayerSetter>(StringComparer.Ordinal)
            {
                ["companyName"]                = v => { PlayerSettings.companyName = AsString(v); return null; },
                ["productName"]                = v => { PlayerSettings.productName = AsString(v); return null; },
                ["bundleVersion"]              = v => { PlayerSettings.bundleVersion = AsString(v); return null; },
                ["runInBackground"]            = v => { PlayerSettings.runInBackground = AsBool(v); return null; },
                ["defaultIsNativeResolution"]  = v => { PlayerSettings.defaultIsNativeResolution = AsBool(v); return null; },
                ["defaultScreenWidth"]         = v => { PlayerSettings.defaultScreenWidth = AsInt(v); return null; },
                ["defaultScreenHeight"]        = v => { PlayerSettings.defaultScreenHeight = AsInt(v); return null; },
                ["colorSpace"]                 = SetPlayerColorSpace,
                ["activeInputHandler"]         = SetPlayerActiveInputHandler,
                ["activeInputHandling"]        = SetPlayerActiveInputHandler,
                ["inputHandling"]              = SetPlayerActiveInputHandler,
            };

        private static string SetPlayerColorSpace(string valueRaw)
        {
            if (Enum.TryParse<ColorSpace>(AsString(valueRaw), true, out var cs))
            {
                PlayerSettings.colorSpace = cs;
                return null;
            }
            return $"colorSpace value '{valueRaw}' did not parse to a ColorSpace.";
        }

        private static string ApplyQualitySetting(string key, string valueRaw)
        {
            if (QualitySetters.TryGetValue(key, out var setter))
            {
                try { return setter(valueRaw); }
                catch (Exception e) { return $"Could not apply quality setting '{key}': {e.Message}"; }
            }
            return $"Unknown quality setting key: '{key}'.";
        }

        private delegate string QualitySetter(string valueRaw);

        private static readonly Dictionary<string, QualitySetter> QualitySetters =
            new Dictionary<string, QualitySetter>(StringComparer.Ordinal)
            {
                ["level"]               = v => { QualitySettings.SetQualityLevel(AsInt(v)); return null; },
                ["shadowDistance"]      = v => { QualitySettings.shadowDistance = AsFloat(v); return null; },
                ["shadowCascades"]      = v => { QualitySettings.shadowCascades = AsInt(v); return null; },
                ["antiAliasing"]        = v => { QualitySettings.antiAliasing = AsInt(v); return null; },
                ["vSyncCount"]          = v => { QualitySettings.vSyncCount = AsInt(v); return null; },
                ["pixelLightCount"]     = v => { QualitySettings.pixelLightCount = AsInt(v); return null; },
            };

        private static string ApplyPhysicsSetting(string key, string valueRaw)
        {
            if (PhysicsSetters.TryGetValue(key, out var setter))
            {
                try { return setter(valueRaw); }
                catch (Exception e) { return $"Could not apply physics setting '{key}': {e.Message}"; }
            }
            return $"Unknown physics setting key: '{key}'.";
        }

        private delegate string PhysicsSetter(string valueRaw);

        private static string SetPhysicsGravity(string valueRaw)
        {
            var g = AsVector3(valueRaw);
            if (g == null) return "gravity expects [x,y,z].";
            Physics.gravity = g.Value;
            return null;
        }

        private static string SetPhysics2DGravity(string valueRaw)
        {
            var g2 = AsVector2(valueRaw);
            if (g2 == null) return "physics2DGravity expects [x,y].";
            Physics2D.gravity = g2.Value;
            return null;
        }

        private static readonly Dictionary<string, PhysicsSetter> PhysicsSetters =
            new Dictionary<string, PhysicsSetter>(StringComparer.Ordinal)
            {
                ["gravity"]                         = SetPhysicsGravity,
                ["defaultSolverIterations"]         = v => { Physics.defaultSolverIterations = AsInt(v); return null; },
                ["defaultSolverVelocityIterations"] = v => { Physics.defaultSolverVelocityIterations = AsInt(v); return null; },
                ["bounceThreshold"]                 = v => { Physics.bounceThreshold = AsFloat(v); return null; },
                ["sleepThreshold"]                  = v => { Physics.sleepThreshold = AsFloat(v); return null; },
                ["defaultContactOffset"]            = v => { Physics.defaultContactOffset = AsFloat(v); return null; },
                ["physics2DGravity"]                = SetPhysics2DGravity,
            };

        private static string ApplyLightingSetting(string key, string valueRaw)
        {
            if (LightingSetters.TryGetValue(key, out var setter))
            {
                try { return setter(valueRaw); }
                catch (Exception e) { return $"Could not apply lighting setting '{key}': {e.Message}"; }
            }
            return $"Unknown lighting setting key: '{key}'.";
        }

        private delegate string LightingSetter(string valueRaw);

        // Every lighting setter ends with MarkSceneDirty (RenderSettings is
        // scene-scoped — writes only persist when the active scene is marked
        // dirty). Enum-parsing keys (ambientMode / fogMode) get their own
        // method so a parse miss returns a precise message.
        private static string SetLightingAmbientMode(string valueRaw)
        {
            if (Enum.TryParse<AmbientMode>(AsString(valueRaw), true, out var am))
            {
                RenderSettings.ambientMode = am;
                MarkSceneDirty();
                return null;
            }
            return $"ambientMode value '{valueRaw}' did not parse to an AmbientMode.";
        }

        private static string SetLightingAmbientColor(string valueRaw)
        {
            var ac = AsColor(valueRaw);
            if (ac == null) return "ambientColor expects [r,g,b,(a)].";
            RenderSettings.ambientLight = ac.Value;
            MarkSceneDirty();
            return null;
        }

        private static string SetLightingFogMode(string valueRaw)
        {
            if (Enum.TryParse<FogMode>(AsString(valueRaw), true, out var fm))
            {
                RenderSettings.fogMode = fm;
                MarkSceneDirty();
                return null;
            }
            return $"fogMode value '{valueRaw}' did not parse to a FogMode.";
        }

        private static string SetLightingFogColor(string valueRaw)
        {
            var fc = AsColor(valueRaw);
            if (fc == null) return "fogColor expects [r,g,b,(a)].";
            RenderSettings.fogColor = fc.Value;
            MarkSceneDirty();
            return null;
        }

        private static readonly Dictionary<string, LightingSetter> LightingSetters =
            new Dictionary<string, LightingSetter>(StringComparer.Ordinal)
            {
                ["ambientMode"]         = SetLightingAmbientMode,
                ["ambientIntensity"]    = v => { RenderSettings.ambientIntensity = AsFloat(v); MarkSceneDirty(); return null; },
                ["ambientColor"]        = SetLightingAmbientColor,
                ["fog"]                 = v => { RenderSettings.fog = AsBool(v); MarkSceneDirty(); return null; },
                ["fogMode"]             = SetLightingFogMode,
                ["fogDensity"]          = v => { RenderSettings.fogDensity = AsFloat(v); MarkSceneDirty(); return null; },
                ["fogColor"]            = SetLightingFogColor,
                ["fogStartDistance"]    = v => { RenderSettings.fogStartDistance = AsFloat(v); MarkSceneDirty(); return null; },
                ["fogEndDistance"]      = v => { RenderSettings.fogEndDistance = AsFloat(v); MarkSceneDirty(); return null; },
            };

        // RenderSettings is scene-scoped — writes only persist when the active
        // scene is marked dirty. MarkSceneDirty covers that without forcing a
        // save (the agent calls scene_save when ready).
        private static void MarkSceneDirty()
        {
            try { UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene()); }
            catch { /* best-effort — non-fatal */ }
        }

        // ----------------------------- helpers ----------------------------

        // JsonBody.GetString returns null when the key is missing AND when it
        // is present-but-empty, so we need a presence check to distinguish
        // "field absent" from "field present but empty". Mirrors
        // ProfilerSessionTools.HasField.
        private static bool HasField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;
            return json.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0;
        }

        private static NamedBuildTarget ResolveNamedBuildTarget()
            => NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);

        private static List<string> SplitDefines(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(raw)) return result;
            foreach (var part in raw.Split(';'))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) result.Add(trimmed);
            }
            return result;
        }

        // Read an int field off the PlayerSettings serialized object. Some
        // PlayerSettings knobs (e.g. activeInputHandler) are not exposed as
        // public properties and have to go through SerializedObject.
        private static int ReadSerializedPlayerInt(string propertyName)
        {
            var so = new SerializedObject(GetPlayerSettingsAsset());
            var prop = so.FindProperty(propertyName);
            var value = prop != null ? prop.intValue : 0;
            so.Dispose();
            return value;
        }

        private static void WriteSerializedPlayerInt(string propertyName, int value)
        {
            var so = new SerializedObject(GetPlayerSettingsAsset());
            var prop = so.FindProperty(propertyName);
            if (prop != null)
            {
                prop.intValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            so.Dispose();
        }

        private static UnityEngine.Object GetPlayerSettingsAsset()
        {
            var asset = Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings");
            if (asset == null)
                throw new InvalidOperationException(
                    "Unable to resolve PlayerSettings serialized asset.");
            return asset;
        }

        private static int ParseActiveInputHandler(string valueRaw, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(valueRaw))
            {
                error = "activeInputHandler value cannot be null.";
                return 0;
            }
            var trimmed = valueRaw.Trim().Trim('"');
            if (int.TryParse(trimmed, out var numeric))
            {
                if (numeric < 0 || numeric > 2)
                {
                    error = "activeInputHandler must be 0 (Old), 1 (Input System), or 2 (Both).";
                    return 0;
                }
                return numeric;
            }
            switch (trimmed.ToLowerInvariant())
            {
                case "old":
                case "legacy":
                case "inputmanager": return 0;
                case "new":
                case "inputsystem":
                case "inputsystempackage": return 1;
                case "both": return 2;
                default:
                    error = "activeInputHandler must be one of: old, inputsystem, both, 0, 1, 2.";
                    return 0;
            }
        }

        private static string DescribeActiveInputHandler(int value)
        {
            switch (value)
            {
                case 0: return "Old";
                case 1: return "InputSystemPackage";
                case 2: return "Both";
                default: return $"Unknown({value})";
            }
        }

        // Value coercion helpers. valueRaw is the raw JSON value (already
        // trimmed of surrounding whitespace by JsonBody.GetRawValue). Strings
        // arrive with surrounding quotes; numbers / bools do not.
        private static string AsString(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return "";
            var v = valueRaw.Trim();
            if (v == "null") return "";
            if (v.StartsWith("\"") && v.EndsWith("\"") && v.Length >= 2)
                return v.Substring(1, v.Length - 2);
            return v;
        }

        private static bool AsBool(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return false;
            var v = valueRaw.Trim();
            if (v == "true") return true;
            if (v == "false") return false;
            // A non-empty quoted string is truthy.
            return !string.IsNullOrEmpty(AsString(valueRaw));
        }

        private static int AsInt(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return 0;
            if (int.TryParse(valueRaw.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v)) return v;
            // Float-looking input → truncated.
            if (float.TryParse(valueRaw.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var f)) return (int)f;
            return 0;
        }

        private static float AsFloat(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return 0f;
            if (float.TryParse(valueRaw.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var v)) return v;
            return 0f;
        }

        // Parse a JSON array of numbers as a Vector3. Returns null on shape
        // mismatch so the applier can surface a per-key warning.
        private static Vector3? AsVector3(string valueRaw)
        {
            var parts = ParseNumberArray(valueRaw);
            if (parts == null || parts.Length < 3) return null;
            return new Vector3(parts[0], parts[1], parts[2]);
        }

        private static Vector2? AsVector2(string valueRaw)
        {
            var parts = ParseNumberArray(valueRaw);
            if (parts == null || parts.Length < 2) return null;
            return new Vector2(parts[0], parts[1]);
        }

        private static Color? AsColor(string valueRaw)
        {
            var parts = ParseNumberArray(valueRaw);
            if (parts == null || parts.Length < 3) return null;
            return new Color(parts[0], parts[1], parts[2],
                parts.Length >= 4 ? parts[3] : 1f);
        }

        private static float[] ParseNumberArray(string valueRaw)
        {
            if (string.IsNullOrEmpty(valueRaw)) return null;
            var v = valueRaw.Trim();
            if (!v.StartsWith("[") || !v.EndsWith("]")) return null;
            var inner = v.Substring(1, v.Length - 2);
            var tokens = inner.Split(',');
            var result = new float[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!float.TryParse(tokens[i].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }

        // ------------------------- JSON building --------------------------

        private static string BuildReportJson(BuildReport report, string outputPath, BuildOptions options)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"action\":\"build\"");
            sb.Append(",\"result\":").Append(Q(report.summary.result.ToString()));
            sb.Append(",\"totalTimeSeconds\":").Append(Num(report.summary.totalTime.TotalSeconds));
            sb.Append(",\"totalSize\":").Append(report.summary.totalSize);
            sb.Append(",\"totalErrors\":").Append(report.summary.totalErrors);
            sb.Append(",\"totalWarnings\":").Append(report.summary.totalWarnings);
            sb.Append(",\"outputPath\":").Append(Q(report.summary.outputPath ?? outputPath));
            sb.Append(",\"options\":\"").Append(options.ToString()).Append("\"");
            sb.Append(",\"steps\":[");
            var steps = report.steps;
            for (int i = 0; i < steps.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Q(steps[i].name));
                sb.Append(",\"durationSeconds\":").Append(Num(steps[i].duration.TotalSeconds));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendWarnings(StringBuilder sb, List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;
            sb.Append(",\"warnings\":[");
            for (int i = 0; i < warnings.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscName(warnings[i])).Append('"');
            }
            sb.Append(']');
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        // Quote a string for inline JSON (with surrounding quotes).
        private static string Q(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + EscName(s) + "\"";
        }

        // Escape a string body for inline JSON (no surrounding quotes).
        private static string EscName(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
