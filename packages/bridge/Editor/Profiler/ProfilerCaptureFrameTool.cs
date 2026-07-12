using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityOpenMcpBridge.ProfilerExt
{
    // M20 Plan 1 / T20.1.4 — single-frame deep profiler capture.
    //
    // Unlike the existing per-module profiler_get_* stats (aggregate / rolling),
    // this returns one (or a few) frame's full sample hierarchy for the
    // requested modules: deeper than the existing surface, more tokens, but the
    // right tool when an agent needs to inspect a specific frame's call tree
    // (e.g. a spike). The cost is documented in the description and the default
    // frame_count is 1.
    //
    // Reuses the same ProfilerDriver / HierarchyFrameDataView machinery as
    // Tool_Profiler.ProfilerCapture but exposes a different ergonomics surface
    // oriented at "give me the sample tree for these modules for this frame":
    //   - frame_count (default 1) — how many recent frames to walk back from
    //   - modules (optional) — filter by Profiler category name (CPU/GPU/…)
    //   - thread_index — which profiler thread to read (0 = main)
    //   - max_depth / max_items — bounds to protect the token budget
    //
    // Read-only: no editor state is changed beyond what the profiler was
    // already doing. If the profiler is disabled, the tool temporarily enables
    // it for one frame and reports that it did so in the response so agents
    // know they left a state change.
    [BridgeToolType]
    public class Tool_ProfilerCaptureFrame
    {
        [BridgeTool("unity_senses_profiler_capture_frame", Title = "Profiler Capture Frame",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None,
            Group = "agent-senses")]
        [System.ComponentModel.Description(
            "Capture a single frame's deep profiler sample tree for the " +
            "requested modules. Deeper than profiler_get_script_stats / " +
            "profiler_get_status (those report aggregate / rolling stats); this " +
            "returns the full sample hierarchy for one frame so an agent can " +
            "inspect a specific frame's call tree (e.g. a spike). The cost grows " +
            "with frame_count, max_depth, and max_items — defaults (frame_count " +
            "= 1, max_depth = 8, max_items = 200) bound the token budget. If the " +
            "Profiler is disabled, the tool enables it for one frame and reports " +
            "profilerWasEnabled in the response so the agent knows Editor state " +
            "may have changed. Requires a live Unity Editor connection.")]
        public string ProfilerCaptureFrame(
            int frame_count = 1,
            string modules = null,
            int thread_index = 0,
            int max_depth = 8,
            int max_items = 200)
        {
            try
            {
                if (frame_count <= 0) frame_count = 1;
                if (frame_count > 10) frame_count = 10;
                if (max_depth <= 0) max_depth = 8;
                if (max_depth > 64) max_depth = 64;
                if (max_items <= 0) max_items = 200;
                if (max_items > 2000) max_items = 2000;

                // Parse the optional module filter (comma-separated Profiler
                // category names, e.g. "CPU,Rendering,Memory"). When null/empty
                // the tool walks the whole hierarchy.
                var moduleFilter = ParseModuleFilter(modules);

                // Ensure the profiler is on so at least one frame exists. If we
                // had to flip it on, wait a beat for a frame to land. The
                // response reports profilerWasEnabled so the agent knows it left
                // editor state changed.
                bool profilerWasEnabled = false;
                if (!ProfilerDriver.enabled)
                {
                    ProfilerDriver.enabled = true;
                    profilerWasEnabled = true;
                    // Allow the profiler to capture at least one frame before we
                    // read. ProfilerDriver.lastFrameIndex becomes valid after the
                    // first recorded frame; we poll briefly so a cold capture
                    // still returns data.
                    WaitForFirstFrame();
                }

                int lastFrame = ProfilerDriver.lastFrameIndex;
                int firstFrame = ProfilerDriver.firstFrameIndex;
                if (lastFrame < 0 || firstFrame < 0)
                    return ErrorJson("profiler_empty",
                        "Profiler captured no frames yet. Enable the Profiler in " +
                        "Unity (Window > Analysis > Profiler > Record) and let it " +
                        "capture at least one frame before retrying.");

                // Clamp the requested range to what the profiler actually has.
                int fromFrame = Math.Max(firstFrame, lastFrame - frame_count + 1);
                int toFrame = lastFrame;
                int resolvedFrameCount = toFrame - fromFrame + 1;

                var frames = new List<string>(resolvedFrameCount);
                int totalTruncated = 0;
                for (int fi = fromFrame; fi <= toFrame; fi++)
                {
                    var (frameJson, truncated) = BuildFrameTree(
                        fi, thread_index, moduleFilter, max_depth, max_items);
                    frames.Add(frameJson);
                    totalTruncated += truncated;
                }

                return BuildSuccessJson(
                    fromFrame, toFrame, resolvedFrameCount, thread_index,
                    modules, max_depth, max_items, frames, totalTruncated,
                    profilerWasEnabled);
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ per-frame tree ============================

        private (string json, int truncated) BuildFrameTree(
            int frameIndex, int threadIndex, HashSet<string> moduleFilter,
            int maxDepth, int maxItems)
        {
            int sortColumn = HierarchyFrameDataView.columnTotalTime;

            using var frameData = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex, threadIndex,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                sortColumn, false);

            if (frameData == null || !frameData.valid)
                return (FrameSkippedJson(frameIndex, "no profiler data for this frame/thread"), 0);

            int rootId = frameData.GetRootItemID();
            // Unity lazy-initializes the tree — traverse from root first.
            var rootChildIds = new List<int>();
            frameData.GetItemChildren(rootId, rootChildIds);

            var ctx = new BuildContext
            {
                FrameData = frameData,
                ModuleFilter = moduleFilter,
                MaxDepth = maxDepth,
                MaxItems = maxItems,
                Shown = 0,
                Truncated = 0,
            };

            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"frame\":").Append(frameIndex).Append(',');
            sb.Append("\"threadIndex\":").Append(threadIndex).Append(',');
            // Root category name (the profiler thread's top-level category).
            sb.Append("\"rootName\":").Append(Esc(SafeItemName(frameData, rootId))).Append(',');
            sb.Append("\"children\":[");
            bool first = true;
            foreach (var childId in rootChildIds)
            {
                // Cap the top-level list at MaxItems across the whole frame so
                // token budgets hold even when the frame has thousands of roots.
                if (ctx.Shown >= ctx.MaxItems) { ctx.Truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                AppendItem(sb, ref ctx, childId, 1);
            }
            sb.Append("]}");

            return (sb.ToString(), ctx.Truncated);
        }

        private struct BuildContext
        {
            public HierarchyFrameDataView FrameData;
            public HashSet<string> ModuleFilter;
            public int MaxDepth;
            public int MaxItems;
            public int Shown;
            public int Truncated;
        }

        private void AppendItem(StringBuilder sb, ref BuildContext ctx, int itemId, int depth)
        {
            ctx.Shown++;

            var name = SafeItemName(ctx.FrameData, itemId);
            var totalTime = ctx.FrameData.GetItemColumnDataAsFloat(itemId, HierarchyFrameDataView.columnTotalTime);
            var selfTime = ctx.FrameData.GetItemColumnDataAsFloat(itemId, HierarchyFrameDataView.columnSelfTime);
            var calls = (long)ctx.FrameData.GetItemColumnDataAsFloat(itemId, HierarchyFrameDataView.columnCalls);

            // Resolve the item's category name when available. The category
            // drives the modules filter below. Unity exposes categories as a
            // ushort index (GetItemCategoryIndex) resolved through
            // GetCategoryInfo — wrap both in try/catch so the tool degrades
            // gracefully on builds where either accessor is absent.
            string categoryName = ResolveCategoryName(ctx.FrameData, itemId);

            sb.Append('{');
            sb.Append("\"itemId\":").Append(itemId).Append(',');
            sb.Append("\"name\":").Append(Esc(name)).Append(',');
            sb.Append("\"totalMs\":").Append(Num(totalTime)).Append(',');
            sb.Append("\"selfMs\":").Append(Num(selfTime)).Append(',');
            sb.Append("\"calls\":").Append(calls);
            if (categoryName != null)
                sb.Append(",\"category\":").Append(Esc(categoryName));

            // Recurse into children when within depth and item budget.
            if (depth < ctx.MaxDepth)
            {
                var childIds = new List<int>();
                ctx.FrameData.GetItemChildren(itemId, childIds);

                // Apply the module filter at every level: when the caller named
                // specific modules, prune children whose category doesn't match.
                var filtered = FilterChildren(ctx, childIds);
                if (filtered.Count > 0)
                {
                    sb.Append(",\"children\":[");
                    bool first = true;
                    foreach (var cid in filtered)
                    {
                        if (ctx.Shown >= ctx.MaxItems) { ctx.Truncated++; break; }
                        if (!first) sb.Append(',');
                        first = false;
                        AppendItem(sb, ref ctx, cid, depth + 1);
                    }
                    sb.Append(']');
                }
            }

            sb.Append('}');
        }

        private List<int> FilterChildren(BuildContext ctx, List<int> childIds)
        {
            if (ctx.ModuleFilter == null || ctx.ModuleFilter.Count == 0)
                return childIds;

            var kept = new List<int>(childIds.Count);
            foreach (var cid in childIds)
            {
                var cat = ResolveCategoryName(ctx.FrameData, cid);
                if (cat == null || ctx.ModuleFilter.Contains(cat))
                    kept.Add(cid);
            }
            return kept;
        }

        // Resolve a sample's Profiler category name from its hierarchy id.
        //
        // Unity exposes categories as a ushort index (HierarchyFrameDataView.
        // GetItemCategoryIndex) resolved to a name via GetCategoryInfo (which
        // returns a ProfilerCategoryInfo struct whose name is exposed as the
        // `name` field). Both accessors are wrapped in try/catch: on Unity
        // builds where either is absent (or the index is unknown), the helper
        // returns null and the caller simply omits the category from the
        // response / includes the sample in the filter pass.
        private static string ResolveCategoryName(HierarchyFrameDataView fd, int itemId)
        {
            try
            {
                ushort idx = fd.GetItemCategoryIndex(itemId);
                object info = fd.GetCategoryInfo(idx);
                if (info == null) return null;
                var type = info.GetType();
                // ProfilerCategoryInfo exposes `name` as a public field; some
                // Unity builds surface it as a property instead — probe both.
                var field = type.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(info) as string;
                var prop = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                       ?? type.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(info) as string;
            }
            catch { /* category resolution unavailable on this Unity build */ }
            return null;
        }

        // ============================ helpers ============================

        private static HashSet<string> ParseModuleFilter(string modules)
        {
            if (string.IsNullOrWhiteSpace(modules)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in modules.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) set.Add(trimmed);
            }
            return set.Count == 0 ? null : set;
        }

        private static void WaitForFirstFrame()
        {
            // Poll ProfilerDriver.lastFrameIndex for a short window so a cold
            // capture (profiler was off) still has a frame to read. Bounded so
            // the tool returns promptly even in a headless EditMode context.
            int sleepMs = 50;
            int budgetMs = 1000;
            int elapsed = 0;
            while (ProfilerDriver.lastFrameIndex < 0 && elapsed < budgetMs)
            {
                System.Threading.Thread.Sleep(sleepMs);
                elapsed += sleepMs;
            }
        }

        private static string SafeItemName(HierarchyFrameDataView fd, int itemId)
        {
            try { return fd.GetItemName(itemId) ?? "(unknown)"; }
            catch { return "(unknown)"; }
        }

        private static string FrameSkippedJson(int frameIndex, string reason)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append("\"frame\":").Append(frameIndex).Append(',');
            sb.Append("\"skipped\":true,");
            sb.Append("\"reason\":").Append(Esc(reason));
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildSuccessJson(
            int fromFrame, int toFrame, int frameCount, int threadIndex,
            string modules, int maxDepth, int maxItems,
            List<string> frames, int totalTruncated, bool profilerWasEnabled)
        {
            var sb = new StringBuilder(8192);
            sb.Append('{');
            sb.Append("\"status\":\"ok\",");
            sb.Append("\"fromFrame\":").Append(fromFrame).Append(',');
            sb.Append("\"toFrame\":").Append(toFrame).Append(',');
            sb.Append("\"frameCount\":").Append(frameCount).Append(',');
            sb.Append("\"threadIndex\":").Append(threadIndex).Append(',');
            sb.Append("\"modules\":").Append(Esc(modules ?? string.Empty)).Append(',');
            sb.Append("\"maxDepth\":").Append(maxDepth).Append(',');
            sb.Append("\"maxItems\":").Append(maxItems).Append(',');
            sb.Append("\"truncated\":").Append(totalTruncated).Append(',');
            sb.Append("\"profilerWasEnabled\":").Append(profilerWasEnabled ? "true" : "false").Append(',');
            sb.Append("\"frames\":[");
            for (int i = 0; i < frames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(frames[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        private static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        // Single source of truth for escaping a JSON string value lives in
        // BridgeJson (T30.5). Preserves this file's `null ⇒ ""` contract
        // (BridgeJson.AppendJsonString would emit `null` for null — field
        // names here are never expected to be null, so we normalize).
        private static string Esc(string s) => s == null ? "\"\"" : BridgeJson.EscapeString(s);
    }
}
