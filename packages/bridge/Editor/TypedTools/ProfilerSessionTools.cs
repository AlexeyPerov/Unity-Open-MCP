using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Profiling; // ProfilerCategory (Unity 2019+).
using UnityOpenMcpBridge.MetaTools;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityProfiler = UnityEngine.Profiling.Profiler;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 7 — typed profiler session / diagnostics tools. Covers:
    //   - profiler_start           (mutating editor state, no asset writes)
    //   - profiler_stop            (mutating editor state, no asset writes)
    //   - profiler_get_status      (read-only)
    //   - profiler_get_config      (read-only)
    //   - profiler_set_config      (mutating editor state, no asset writes)
    //   - profiler_list_modules    (read-only)
    //   - profiler_enable_module   (mutating local bookkeeping, no asset writes)
    //   - profiler_clear_data      (mutating editor state, no asset writes)
    //   - profiler_save_data       (mutating; writes a .json snapshot — gate path)
    //   - profiler_load_data       (read-only file read)
    //   - profiler_get_script_stats (read-only)
    //
    // Gate routing (see BridgeHttpServer DirectResponseTools / MutatingTools):
    //   - All members EXCEPT profiler_save_data are gate-free direct-response
    //     tools (they mutate editor state or local bookkeeping but write NO
    //     assets, so the gate's asset-reference validation has nothing to do).
    //   - profiler_save_data writes a .json snapshot to disk and runs the full
    //     gate path with paths_hint scoped to the destination .json path.
    //
    // Complements (do NOT duplicate M10 baseline reads):
    //   - unity_senses_profiler_capture — per-frame hierarchy + averaging
    //   - unity_senses_profiler_memory  — live allocator bytes
    //   - unity_senses_profiler_rendering — GPU + QualitySettings batch
    // This surface is the runtime/session layer: enabled flag, modules,
    // config knobs, buffered-frames clear, snapshot save/load, script timing.
    //
    // NOT registry-discovered: wired into BridgeHttpServer.DispatchTool
    // alongside the other M16 typed tools so the snake_case schemas parse the
    // same way.
    public static class ProfilerSessionTools
    {
        // Canonical Profiler window module names this surface understands.
        // Kept as a constant list so the wrapper is independent of the optional
        // com.unity.profiling.core package — the core tool relies on built-in
        // Unity APIs only.
        private static readonly string[] AvailableModules =
        {
            "CPU", "GPU", "Rendering", "Memory", "Audio", "Video",
            "Physics", "Physics2D", "NetworkMessages", "NetworkOperations",
            "UI", "UIDetails", "GlobalIllumination", "VirtualTexturing"
        };

        // Default-enabled subset. Unity's runtime Profiler API does not expose
        // per-module enable/disable, so this is purely local bookkeeping
        // consumed by GetStatus / ListModules /
        // EnableModule; actual module visibility is controlled from the
        // Profiler window.
        private static readonly HashSet<string> EnabledModules = new HashSet<string>
        {
            "CPU", "GPU", "Rendering", "Memory", "Audio", "Video",
            "Physics", "Physics2D", "UI"
        };

        // Editor-version reflection cache for ProfilerDriver knobs that are
        // not part of the public API surface on every Unity version. Try once
        // at first use, cache null when unavailable.
        private static readonly Type ProfilerDriverType =
            Type.GetType("UnityEditorInternal.ProfilerDriver, UnityEditor");
        private static readonly PropertyInfo DriverEnabledProperty =
            ProfilerDriverType?.GetProperty("enabled",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly PropertyInfo ProfileEditorProperty =
            ProfilerDriverType?.GetProperty("profileEditor",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly PropertyInfo DeepProfilingProperty =
            ProfilerDriverType?.GetProperty("deepProfiling",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo ClearAllFramesMethod =
            ProfilerDriverType?.GetMethod("ClearAllFrames",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        // ProfilerCategory reflection cache — Profiler.GetCategoriesCount /
        // GetAllCategories / IsCategoryEnabled / SetCategoryEnabled are public
        // statics on UnityEngine.Profiling.Profiler in 2020+, but we resolve
        // them via reflection so the tool fails closed instead of refusing to
        // compile on older targets.
        private static readonly MethodInfo GetCategoriesCountMethod =
            typeof(UnityProfiler).GetMethod("GetCategoriesCount",
                BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
        private static readonly MethodInfo GetAllCategoriesMethod =
            typeof(UnityProfiler).GetMethod("GetAllCategories",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(ProfilerCategory[]) }, null);
        private static readonly MethodInfo IsCategoryEnabledMethod =
            typeof(UnityProfiler).GetMethod("IsCategoryEnabled",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(ProfilerCategory) }, null);
        private static readonly MethodInfo SetCategoryEnabledMethod =
            typeof(UnityProfiler).GetMethod("SetCategoryEnabled",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(ProfilerCategory), typeof(bool) }, null);

        // Cap to keep an accidental call against a huge file from OOMing the
        // Editor. Snapshots written by SaveData are typically a few KB; 10 MB
        // leaves headroom for future schema expansion. Mirrors UMCP.
        private const long MaxLoadFileSizeBytes = 10L * 1024L * 1024L;

        // Safe-editor memory budget clamps (mirror UCP). maxUsedMemory is an
        // int (bytes) on Unity's API; we clamp before assigning to avoid
        // negative / overflowed values.
        private const long MinimumProfilerMemoryBytes = 16L * 1024L * 1024L;
        private const long DefaultProfilerMemoryBytes = 128L * 1024L * 1024L;
        private const long HeavyProfilerMemoryBytes = 64L * 1024L * 1024L;
        private const long AbsoluteProfilerMemoryBytes = 256L * 1024L * 1024L;
        private const long AbsoluteHeavyProfilerMemoryBytes = 128L * 1024L * 1024L;

        // ============================ Start =============================

        // Enable the runtime profiler and optionally open the Profiler window.
        // Idempotent. Mutates editor state only — gate-free.
        public static ToolDispatchResult Start(string body)
        {
            bool openWindow = JsonBody.GetBool(body, "open_window", true);
            try
            {
                UnityProfiler.enabled = true;
                bool menuOpened = false;
                if (openWindow)
                {
                    menuOpened = EditorApplication.ExecuteMenuItem("Window/Analysis/Profiler");
                    if (!menuOpened)
                        Debug.LogWarning(
                            "[ProfilerSessionTools] Could not open menu 'Window/Analysis/Profiler'. " +
                            "The runtime profiler is still enabled.");
                }
                var sb = new StringBuilder(128);
                sb.Append("{\"status\":\"ok\",\"enabled\":")
                  .Append(UnityProfiler.enabled ? "true" : "false")
                  .Append(",\"windowOpened\":").Append(menuOpened ? "true" : "false");
                sb.Append(",\"note\":\"Profiler.enabled flipped. Recording starts at the next frame; \"")
                  .Append("poll profiler_get_status / unity_senses_profiler_capture to confirm.\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ============================ Stop ==============================

        // Disable the runtime profiler. Idempotent. Mutates editor state only
        // — gate-free.
        public static ToolDispatchResult Stop(string body)
        {
            try
            {
                UnityProfiler.enabled = false;
                var sb = new StringBuilder(96);
                sb.Append("{\"status\":\"ok\",\"enabled\":")
                  .Append(UnityProfiler.enabled ? "true" : "false");
                sb.Append(",\"note\":\"Buffered frames stay in memory. Use profiler_clear_data to \"")
                  .Append("discard them, or profiler_save_data to persist a snapshot first.\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ========================= Get status ===========================

        // Read-only runtime status snapshot. Does NOT duplicate M10 capture /
        // memory / rendering — this is the runtime flag + module bookkeeping
        // surface.
        public static ToolDispatchResult GetStatus(string body)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("{\"enabled\":").Append(UnityProfiler.enabled ? "true" : "false");
                sb.Append(",\"supported\":").Append(UnityProfiler.supported ? "true" : "false");
                // Divide in double precision then cast — the raw value is bytes.
                sb.Append(",\"maxUsedMemoryBytes\":").Append(UnityProfiler.maxUsedMemory);
                sb.Append(",\"maxUsedMemoryMB\":").Append(Num(UnityProfiler.maxUsedMemory / 1048576.0));
                sb.Append(",\"activeModules\":[");
                var sorted = EnabledModules.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(sorted[i])).Append('"');
                }
                sb.Append("]}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ========================= Get config ===========================

        // Read-only ProfilerDriver / Profiler knob snapshot. Some knobs are
        // editor-version gated and return false / empty when the underlying
        // API is unavailable.
        public static ToolDispatchResult GetConfig(string body)
        {
            try
            {
                var warnings = new List<string>();
                var sb = new StringBuilder(512);
                sb.Append('{');
                sb.Append("\"driverEnabled\":").Append(GetDriverEnabled() ? "true" : "false");
                if (DriverEnabledProperty == null)
                    warnings.Add("ProfilerDriver.enabled is unavailable on this Unity version.");
                sb.Append(",\"profileEditor\":").Append(GetProfileEditor() ? "true" : "false");
                if (ProfileEditorProperty == null)
                    warnings.Add("ProfilerDriver.profileEditor is unavailable on this Unity version.");
                sb.Append(",\"deepProfile\":").Append(GetDeepProfiling() ? "true" : "false");
                if (DeepProfilingProperty == null)
                    warnings.Add("ProfilerDriver.deepProfiling is unavailable on this Unity version.");
                sb.Append(",\"allocationCallstacks\":").Append(UnityProfiler.enableAllocationCallstacks ? "true" : "false");
                sb.Append(",\"binaryLog\":").Append(UnityProfiler.enableBinaryLog ? "true" : "false");
                var outputPath = NormalizePath(UnityProfiler.logFile) ?? "";
                sb.Append(",\"outputPath\":").Append(Esc(outputPath));
                sb.Append(",\"maxUsedMemory\":").Append(UnityProfiler.maxUsedMemory);

                var available = GetAvailableCategories();
                var enabledCats = new List<string>();
                foreach (var cat in available)
                    if (IsCategoryEnabled(cat))
                        enabledCats.Add(cat.Name);
                enabledCats.Sort(StringComparer.OrdinalIgnoreCase);

                sb.Append(",\"availableCategories\":[");
                var sortedAvail = available.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < sortedAvail.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(sortedAvail[i])).Append('"');
                }
                sb.Append("],\"enabledCategories\":[");
                for (int i = 0; i < enabledCats.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(enabledCats[i])).Append('"');
                }
                sb.Append("]");

                if (GetCategoriesCountMethod == null || GetAllCategoriesMethod == null)
                    warnings.Add("Profiler category enumeration is unavailable on this Unity version.");

                sb.Append(",\"warnings\":[");
                for (int i = 0; i < warnings.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(warnings[i])).Append('"');
                }
                sb.Append("]}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ========================= Set config ===========================

        // Update one or more profiler runtime knobs in a single call. Mutates
        // editor state only — gate-free. Only the knobs Unity's runtime API
        // exposes for in-place writes are honored; the rest are reported as
        // warnings (the request is recorded, the value is not applied).
        public static ToolDispatchResult SetConfig(string body)
        {
            try
            {
                var warnings = new List<string>();

                // mode → edit vs play targeting.
                var mode = JsonBody.GetString(body, "mode");
                if (!string.IsNullOrEmpty(mode))
                {
                    var normalized = NormalizeMode(mode);
                    if (!SetProfileEditor(normalized == "edit"))
                        warnings.Add("Edit/play target selection is unavailable on this Unity version; the requested value was recorded but not applied.");
                    if (normalized == "play" && !EditorApplication.isPlaying)
                        EditorApplication.isPlaying = true;
                    else if (normalized == "edit" && EditorApplication.isPlaying)
                        EditorApplication.isPlaying = false;
                }

                // deep_profile (optional bool via GetRawValue — JsonBody.GetBool
                // returns a default, so we use HasField).
                if (HasField(body, "deep_profile"))
                {
                    var requested = JsonBody.GetBool(body, "deep_profile", false);
                    if (!SetDeepProfiling(requested))
                        warnings.Add("Deep profiling automation is unavailable on this Unity version; the requested value was recorded but not applied.");
                }

                if (HasField(body, "allocation_callstacks"))
                {
                    var requested = JsonBody.GetBool(body, "allocation_callstacks", false);
                    UnityProfiler.enableAllocationCallstacks = requested;
                    if (requested)
                        warnings.Add("Allocation callstacks add noticeable profiler overhead and can trigger frame drops or heavy editor memory pressure during longer captures.");
                }

                if (HasField(body, "binary_log"))
                {
                    var requested = JsonBody.GetBool(body, "binary_log", false);
                    UnityProfiler.enableBinaryLog = requested;
                    if (requested && !UnityProfiler.enableBinaryLog)
                        warnings.Add("Unity Editor keeps Profiler.enableBinaryLog disabled at runtime. Raw file capture requires a player build or manual Profiler export.");
                }

                var output = JsonBody.GetString(body, "output");
                if (!string.IsNullOrEmpty(output))
                {
                    try
                    {
                        var abs = NormalizeAbsolutePath(output);
                        var dir = Path.GetDirectoryName(abs);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        UnityProfiler.logFile = abs;
                    }
                    catch (Exception e)
                    {
                        warnings.Add($"Could not apply profiler output path '{output}': {e.Message}");
                    }
                }

                if (HasField(body, "max_used_memory"))
                {
                    var requested = JsonBody.GetLong(body, "max_used_memory", 0);
                    warnings.AddRange(ApplySafeMemoryBudget(
                        requested,
                        GetDeepProfiling(),
                        UnityProfiler.enableAllocationCallstacks));
                }

                var enableCats = JsonBody.GetStringArray(body, "enable_categories");
                if (enableCats != null)
                    foreach (var name in enableCats)
                        if (!string.IsNullOrEmpty(name))
                            warnings.AddRange(SetCategoryEnabled(name, true));

                var disableCats = JsonBody.GetStringArray(body, "disable_categories");
                if (disableCats != null)
                    foreach (var name in disableCats)
                        if (!string.IsNullOrEmpty(name))
                            warnings.AddRange(SetCategoryEnabled(name, false));

                // Build the response — include the post-write config (same
                // shape as GetConfig) plus the accumulated warnings. We
                // re-emit the warnings rather than re-reading them from the
                // config snapshot so we keep both in one payload.
                var configResult = GetConfig(body);
                if (!configResult.Success)
                    return configResult;

                // Splice warnings into the config JSON. The GetConfig payload
                // already ends with a warnings[] array of its own; we replace
                // that with the union of both lists so the agent sees every
                // non-applicable knob.
                var configJson = configResult.Output;
                var lastWarn = configJson.LastIndexOf(",\"warnings\":[", StringComparison.Ordinal);
                if (lastWarn > 0)
                {
                    // Truncate at the warnings section; we will close after.
                    var head = configJson.Substring(0, lastWarn);
                    var sb = new StringBuilder(head.Length + warnings.Count * 64 + 32);
                    sb.Append(head);
                    sb.Append(",\"warnings\":[");
                    for (int i = 0; i < warnings.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(OutputSerializer.EscapeJsonString(warnings[i])).Append('"');
                    }
                    sb.Append("]}");
                    return ToolDispatchResult.Ok(sb.ToString());
                }
                // Fall through: prepend warnings as a sibling field.
                {
                    var sb = new StringBuilder(configJson.Length + warnings.Count * 64 + 16);
                    sb.Append("{\"config\":").Append(configJson);
                    sb.Append(",\"warnings\":[");
                    for (int i = 0; i < warnings.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(OutputSerializer.EscapeJsonString(warnings[i])).Append('"');
                    }
                    sb.Append("]}");
                    return ToolDispatchResult.Ok(sb.ToString());
                }
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ======================= List modules ==========================

        // Read-only module list with local enabled bookkeeping flags.
        public static ToolDispatchResult ListModules(string body)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("{\"modules\":[");
                for (int i = 0; i < AvailableModules.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(AvailableModules[i]));
                    sb.Append("\",\"enabled\":").Append(EnabledModules.Contains(AvailableModules[i]) ? "true" : "false");
                    sb.Append('}');
                }
                sb.Append("],\"count\":").Append(AvailableModules.Length);
                sb.Append(",\"note\":\"Bookkeeping only — Unity's runtime API does not expose programmatic per-module toggling; use the Profiler window for actual module visibility.\"}");
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ======================= Enable module =========================

        // Toggle the local 'enabled' bookkeeping flag for a named module.
        // Mutates local state only — gate-free.
        public static ToolDispatchResult EnableModule(string body)
        {
            var moduleName = JsonBody.GetString(body, "module");
            if (string.IsNullOrWhiteSpace(moduleName))
                return ToolDispatchResult.Fail("missing_parameter", "'module' is required.");
            moduleName = moduleName.Trim();

            if (!AvailableModules.Contains(moduleName))
                return ToolDispatchResult.Fail("unknown_module",
                    $"Unknown profiler module: '{moduleName}'. Available modules: {string.Join(", ", AvailableModules)}");

            var enabled = JsonBody.GetBool(body, "enabled", true);
            if (enabled) EnabledModules.Add(moduleName);
            else EnabledModules.Remove(moduleName);

            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"module\":\"")
              .Append(OutputSerializer.EscapeJsonString(moduleName));
            sb.Append("\",\"enabled\":").Append(enabled ? "true" : "false");
            sb.Append(",\"note\":\"Bookkeeping only — Unity's runtime API does not expose programmatic per-module toggling; use the Profiler window for actual module visibility.\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ========================= Clear data ==========================

        // Discard all frames currently buffered by the Editor Profiler.
        // Idempotent. Mutates editor state only — gate-free.
        public static ToolDispatchResult ClearData(string body)
        {
            try
            {
                if (ClearAllFramesMethod == null)
                    return ToolDispatchResult.Fail("clear_unavailable",
                        "Clearing buffered profiler frames is unavailable on this Unity version.");
                ClearAllFramesMethod.Invoke(null, null);
                return ToolDispatchResult.Ok("{\"status\":\"ok\",\"cleared\":true,\"note\":\"Profiler window frame history is now empty.\"}");
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ========================== Save data ==========================

        // Save a structured JSON snapshot to disk. Composed from the read
        // surfaces already in this tool family (status / memory / rendering /
        // script / frame) so the shape stays in sync. Mutating: writes the
        // file (parent dirs created) — runs the full gate path with
        // paths_hint scoped to the destination .json path.
        public static ToolDispatchResult SaveData(string body)
        {
            var filePath = JsonBody.GetString(body, "file_path");
            if (string.IsNullOrWhiteSpace(filePath))
                return ToolDispatchResult.Fail("missing_parameter", "'file_path' is required.");

            var resolvedPath = ResolveSnapshotPath(filePath, out var pathError);
            if (resolvedPath == null) return pathError;

            try
            {
                // Compose the snapshot by re-emitting the same payloads the
                // read tools return so the saved shape stays in sync as those
                // tools evolve. Direct field reads here would silently drift.
                var status = ExtractPayload(GetStatus(body));
                var rendering = InvokeRendering();
                var script = ExtractPayload(GetScriptStats(body));

                var sb = new StringBuilder(2048);
                sb.Append('{');
                sb.Append("\"savedAt\":").Append(Esc(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
                sb.Append(",\"status\":").Append(status ?? "null");
                sb.Append(",\"rendering\":").Append(rendering ?? "null");
                sb.Append(",\"script\":").Append(script ?? "null");
                sb.Append('}');

                var dir = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(resolvedPath, sb.ToString());

                var result = new StringBuilder(160);
                result.Append("{\"status\":\"ok\",\"action\":\"save\",\"filePath\":\"")
                      .Append(OutputSerializer.EscapeJsonString(filePath));
                result.Append("\",\"bytesWritten\":").Append(sb.Length);
                result.Append(",\"note\":\"Structured snapshot (no raw binary frames). Read back via profiler_load_data.\"}");
                return ToolDispatchResult.Ok(result.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("save_failed", e.Message);
            }
        }

        // ========================== Load data ==========================

        // Read back a previously-saved snapshot. Read-only file access —
        // gate-free. Optionally calls Profiler.AddFramesFromFile so the
        // frames are browsable in the Profiler window (no-op for non-binary
        // JSON snapshots, surfaces a warning).
        public static ToolDispatchResult LoadData(string body)
        {
            var filePath = JsonBody.GetString(body, "file_path");
            if (string.IsNullOrWhiteSpace(filePath))
                return ToolDispatchResult.Fail("missing_parameter", "'file_path' is required.");

            var resolvedPath = ResolveSnapshotPath(filePath, out var pathError);
            if (resolvedPath == null) return pathError;

            try
            {
                if (!File.Exists(resolvedPath))
                    return ToolDispatchResult.Fail("file_not_found", $"No file at '{filePath}'.");

                var info = new FileInfo(resolvedPath);
                if (info.Length > MaxLoadFileSizeBytes)
                    return ToolDispatchResult.Fail("file_too_large",
                        $"File '{filePath}' is {info.Length} bytes, exceeding the {MaxLoadFileSizeBytes}-byte cap.");

                var content = File.ReadAllText(resolvedPath);

                var addToProfiler = JsonBody.GetBool(body, "add_to_profiler", false);
                bool added = false;
                var warnings = new List<string>();
                if (addToProfiler)
                {
                    try
                    {
                        UnityProfiler.AddFramesFromFile(resolvedPath);
                        added = true;
                    }
                    catch (Exception e)
                    {
                        warnings.Add($"Profiler.AddFramesFromFile refused the file (it only accepts raw binary captures, not JSON snapshots): {e.Message}");
                    }
                }

                var sb = new StringBuilder(content.Length + 96);
                sb.Append("{\"status\":\"ok\",\"filePath\":\"")
                  .Append(OutputSerializer.EscapeJsonString(filePath));
                sb.Append("\",\"addedToProfiler\":").Append(added ? "true" : "false");
                sb.Append(",\"content\":").Append(content);
                if (warnings.Count > 0)
                {
                    sb.Append(",\"warnings\":[");
                    for (int i = 0; i < warnings.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(OutputSerializer.EscapeJsonString(warnings[i])).Append('"');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("load_failed", e.Message);
            }
        }

        // ======================= Get script stats ======================

        // Read-only single-frame snapshot of script timing + Mono/GC memory.
        public static ToolDispatchResult GetScriptStats(string body)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append("{\"frameTimeMs\":").Append(Num(Time.deltaTime * 1000f));
                sb.Append(",\"fixedDeltaTimeMs\":").Append(Num(Time.fixedDeltaTime * 1000f));
                sb.Append(",\"timeScale\":").Append(Num(Time.timeScale));
                sb.Append(",\"totalFrameCount\":").Append(Time.frameCount);
                sb.Append(",\"realtimeSinceStartup\":").Append(Num(Time.realtimeSinceStartup));
                sb.Append(",\"monoMemoryUsageBytes\":").Append(UnityProfiler.GetMonoUsedSizeLong());
                sb.Append(",\"monoMemoryUsageMB\":").Append(Num(UnityProfiler.GetMonoUsedSizeLong() / 1048576.0));
                sb.Append(",\"gcMemoryUsageBytes\":").Append(GC.GetTotalMemory(false));
                sb.Append(",\"gcMemoryUsageMB\":").Append(Num(GC.GetTotalMemory(false) / 1048576.0));
                sb.Append('}');
                return ToolDispatchResult.Ok(sb.ToString());
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        // ----------------------------- helpers -----------------------------

        // Returns the rendering payload by delegating to the M10 rendering
        // tool (sibling registry tool). We dispatch through the registry so
        // the snapshot shape stays in sync with the live read tool.
        private static string InvokeRendering()
        {
            try
            {
                var result = BridgeToolRegistry.TryDispatch("unity_senses_profiler_rendering", "{}");
                return result?.Success == true ? result.Output : null;
            }
            catch { return null; }
        }

        // A read tool's Ok output is `{"...fields..."}`. When composing a
        // snapshot we want just that inner object, not the gate envelope. The
        // direct-response read tools already return the bare payload, so we
        // can pass it through. We guard against null / failed dispatch.
        private static string ExtractPayload(ToolDispatchResult result)
        {
            if (result == null || !result.Success || string.IsNullOrEmpty(result.Output))
                return null;
            return result.Output;
        }

        // Apply a memory-budget clamp that prevents editor memory bloat.
        // Heavy-capture (deep profiling or allocation callstacks) gets a
        // tighter cap. Returns warnings for any clamping that happened.
        private static List<string> ApplySafeMemoryBudget(long? requestedBytes,
            bool deepProfile, bool allocationCallstacks)
        {
            var warnings = new List<string>();
            var heavyCapture = deepProfile || allocationCallstacks;
            var recommendedBudget = heavyCapture ? HeavyProfilerMemoryBytes : DefaultProfilerMemoryBytes;
            var hardCap = heavyCapture ? AbsoluteHeavyProfilerMemoryBytes : AbsoluteProfilerMemoryBytes;
            long currentBudget = UnityProfiler.maxUsedMemory;

            long effectiveBudget;
            if (requestedBytes.HasValue)
            {
                effectiveBudget = Math.Min(hardCap, Math.Max(MinimumProfilerMemoryBytes, requestedBytes.Value));
                if (requestedBytes.Value != effectiveBudget)
                    warnings.Add($"Profiler buffer memory was clamped to {effectiveBudget / (1024L * 1024L)} MiB to prevent editor memory bloat.");
            }
            else
            {
                effectiveBudget = Math.Min(currentBudget, recommendedBudget);
                if (effectiveBudget < MinimumProfilerMemoryBytes)
                    effectiveBudget = MinimumProfilerMemoryBytes;
                if (currentBudget != effectiveBudget)
                    warnings.Add($"Profiler buffer memory was reduced to {effectiveBudget / (1024L * 1024L)} MiB for a safer live-editor session.");
            }

            UnityProfiler.maxUsedMemory = (int)Math.Min(int.MaxValue,
                Math.Max(MinimumProfilerMemoryBytes, effectiveBudget));
            return warnings;
        }

        private static List<string> SetCategoryEnabled(string categoryName, bool enabled)
        {
            var warnings = new List<string>();
            var cat = ResolveCategory(categoryName);
            if (!cat.HasValue)
            {
                warnings.Add($"Unknown profiler category: {categoryName}");
                return warnings;
            }
            if (SetCategoryEnabledMethod == null)
            {
                warnings.Add("Profiler category toggling is unavailable on this Unity version.");
                return warnings;
            }
            try
            {
                SetCategoryEnabledMethod.Invoke(null, new object[] { cat.Value, enabled });
                warnings.Add("Unity's open Profiler window can override category settings to match active charts.");
            }
            catch (Exception e)
            {
                warnings.Add($"Could not toggle category '{categoryName}': {e.Message}");
            }
            return warnings;
        }

        private static ProfilerCategory? ResolveCategory(string name)
        {
            foreach (var c in GetAvailableCategories())
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }

        private static ProfilerCategory[] GetAvailableCategories()
        {
            if (GetCategoriesCountMethod == null || GetAllCategoriesMethod == null)
                return Array.Empty<ProfilerCategory>();
            try
            {
                var count = Convert.ToInt32(GetCategoriesCountMethod.Invoke(null, null));
                if (count <= 0) return Array.Empty<ProfilerCategory>();
                var cats = new ProfilerCategory[count];
                GetAllCategoriesMethod.Invoke(null, new object[] { cats });
                return cats;
            }
            catch { return Array.Empty<ProfilerCategory>(); }
        }

        private static bool IsCategoryEnabled(ProfilerCategory category)
        {
            if (IsCategoryEnabledMethod == null) return false;
            try { return Convert.ToBoolean(IsCategoryEnabledMethod.Invoke(null, new object[] { category })); }
            catch { return false; }
        }

        private static bool GetDriverEnabled()
        {
            if (DriverEnabledProperty == null) return false;
            try { return Convert.ToBoolean(DriverEnabledProperty.GetValue(null)); }
            catch { return false; }
        }

        private static bool GetProfileEditor()
        {
            if (ProfileEditorProperty == null) return false;
            try { return Convert.ToBoolean(ProfileEditorProperty.GetValue(null)); }
            catch { return false; }
        }

        private static bool SetProfileEditor(bool value)
        {
            if (ProfileEditorProperty == null) return false;
            try { ProfileEditorProperty.SetValue(null, value); return true; }
            catch { return false; }
        }

        private static bool GetDeepProfiling()
        {
            if (DeepProfilingProperty == null) return false;
            try { return Convert.ToBoolean(DeepProfilingProperty.GetValue(null)); }
            catch { return false; }
        }

        private static bool SetDeepProfiling(bool value)
        {
            if (DeepProfilingProperty == null) return false;
            try { DeepProfilingProperty.SetValue(null, value); return true; }
            catch { return false; }
        }

        private static string NormalizeMode(string mode)
            => string.Equals(mode, "edit", StringComparison.OrdinalIgnoreCase) ? "edit" : "play";

        private static string NormalizePath(string path)
            => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

        private static string NormalizeAbsolutePath(string path)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        }

        // Resolve a project-relative .json snapshot path. Refuses paths that
        // escape the project root, aren't .json, or contain '..'. The
        // returned path is OS-normalized; `inputPath` is preserved for the
        // response message. Mirrors ReflectionScriptsObjectsTools.ResolveScriptPath.
        private static string ResolveSnapshotPath(string inputPath, out ToolDispatchResult error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                error = ToolDispatchResult.Fail("invalid_path", "Path is empty.");
                return null;
            }
            var normalized = inputPath.Replace('\\', '/').TrimStart('/');
            if (!normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolDispatchResult.Fail("invalid_path", $"Path '{inputPath}' must end in .json.");
                return null;
            }
            if (normalized.Contains(".."))
            {
                error = ToolDispatchResult.Fail("invalid_path", $"Path '{inputPath}' must not contain '..'.");
                return null;
            }

            var projectRoot = Application.dataPath != null
                ? Directory.GetParent(Application.dataPath)?.FullName
                : null;
            if (string.IsNullOrEmpty(projectRoot))
            {
                error = ToolDispatchResult.Fail("invalid_path", "Could not resolve the project root.");
                return null;
            }

            var absolute = Path.GetFullPath(Path.Combine(projectRoot, normalized));
            var rootFull = Path.GetFullPath(projectRoot);
            if (!absolute.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                error = ToolDispatchResult.Fail("invalid_path", $"Path '{inputPath}' escapes the project root.");
                return null;
            }
            return absolute;
        }

        // JsonBody.GetBool returns a default when the key is missing, so we
        // need a presence check to distinguish "not provided" from "false".
        private static bool HasField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;
            return json.IndexOf("\"" + key + "\"", StringComparison.Ordinal) >= 0;
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Esc(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + OutputSerializer.EscapeJsonString(s) + "\"";
        }
    }
}
