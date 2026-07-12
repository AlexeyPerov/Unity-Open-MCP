using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityOpenMcpBridge.ProfilerExt
{
    // M10 Plan 3 T3.4 — Profiler meta-tools (non-mutating).
    //
    // Three read-only tools surface Unity Profiler data to agents for
    // performance work:
    //   unity_senses_profiler_capture   — frame hierarchy with drill-down +
    //                                    multi-frame averaging
    //   unity_senses_profiler_memory    — live memory allocator stats
    //   unity_senses_profiler_rendering — rendering environment stats batch
    //
    // Hierarchy logic emits hand-rolled StringBuilder JSON (no Newtonsoft
    // dependency) and uses the bridge [BridgeTool] method-parameter convention.
    //
    // All tools are read-only (Gate = Off, ReadOnlyHint = true) and
    // token-bounded via max_items / depth / min_ms to protect the agent's
    // context budget.
    [BridgeToolType]
    public class Tool_Profiler
    {
        // ============================ capture ============================

        [BridgeTool("unity_senses_profiler_capture", Title = "Profiler Capture",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Capture the Unity Profiler frame hierarchy with drill-down and " +
            "multi-frame averaging. Requires the Profiler to be enabled and to " +
            "have captured at least one frame. Use parent / root / depth / " +
            "min_ms / max_items to bound output and protect the token budget. " +
            "itemId values returned are only valid within the same frame; " +
            "drill into them via 'parent' in the same call sequence.")]
        public string ProfilerCapture(
            int frame = -1,
            int from_frame = -1,
            int to_frame = -1,
            int frames = 0,
            int thread = 0,
            int parent = -1,
            string root = null,
            float min_ms = 0f,
            string sort = "total",
            int max_items = 30,
            int depth = 1)
        {
            try
            {
                if (!ProfilerDriver.enabled && ProfilerDriver.lastFrameIndex < 0)
                    return ErrorJson("profiler_empty",
                        "Profiler has no captured data. Enable the Profiler in Unity " +
                        "first (Window > Analysis > Profiler > Record).");

                var sortBy = (sort ?? "total").ToLowerInvariant();
                if (sortBy != "total" && sortBy != "self" && sortBy != "calls")
                    sortBy = "total";

                if (max_items <= 0) max_items = 30;
                var effDepth = depth <= 0 ? 999 : depth;

                // Range averaging path.
                if (from_frame >= 0 || to_frame >= 0)
                {
                    int f = from_frame < 0 ? ProfilerDriver.firstFrameIndex : from_frame;
                    int t = to_frame < 0 ? ProfilerDriver.lastFrameIndex : to_frame;
                    return AveragedHierarchy(f, t, thread, root, min_ms, sortBy, max_items, effDepth, depth);
                }
                if (frames > 1)
                {
                    int t = ProfilerDriver.lastFrameIndex;
                    int f = t - frames + 1;
                    return AveragedHierarchy(f, t, thread, root, min_ms, sortBy, max_items, effDepth, depth);
                }

                // Single-frame path.
                int frameIndex = frame < 0 ? ProfilerDriver.lastFrameIndex : frame;
                if (frameIndex < ProfilerDriver.firstFrameIndex || frameIndex > ProfilerDriver.lastFrameIndex)
                    return ErrorJson("frame_out_of_range",
                        $"Frame {frameIndex} out of range " +
                        $"[{ProfilerDriver.firstFrameIndex}..{ProfilerDriver.lastFrameIndex}].");

                int sortColumn = GetSortColumn(sortBy);

                using var frameData = ProfilerDriver.GetHierarchyFrameDataView(
                    frameIndex, thread,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    sortColumn, false);

                if (frameData == null || !frameData.valid)
                    return ErrorJson("no_frame_data",
                        $"No profiler data for frame {frameIndex}, thread {thread}.");

                // Must traverse from root first — Unity lazy-initializes the tree.
                int rootId = frameData.GetRootItemID();
                var rootChildIds = new List<int>();
                frameData.GetItemChildren(rootId, rootChildIds);

                int parentId = rootId;
                string parentName = "(root)";

                // --root: find by name substring (recursive).
                if (!string.IsNullOrEmpty(root))
                {
                    int found = FindItemByName(frameData, rootId, root);
                    if (found < 0)
                        return ErrorJson("root_not_found",
                            $"No profiler item matching '{root}' found in frame {frameIndex}.");
                    parentId = found;
                    parentName = frameData.GetItemName(found);
                }
                // --parent: drill into a known itemId from a previous response.
                else if (parent >= 0)
                {
                    parentId = parent;
                    try { parentName = frameData.GetItemName(parentId); }
                    catch { parentName = "(unknown)"; }
                }

                var children = BuildChildren(frameData, parentId, min_ms, max_items, effDepth);

                var sb = new StringBuilder(4096);
                sb.Append('{');
                sb.Append("\"mode\":\"single\",");
                sb.Append("\"frame\":").Append(frameIndex).Append(',');
                sb.Append("\"threadIndex\":").Append(thread).Append(',');
                sb.Append("\"parent\":").Append(parentId).Append(',');
                sb.Append("\"parentName\":").Append(Esc(parentName)).Append(',');
                sb.Append("\"sort\":").Append(Esc(sortBy)).Append(',');
                sb.Append("\"depth\":").Append(depth <= 0 ? 0 : depth).Append(',');
                sb.Append("\"minMs\":").Append(Num(min_ms)).Append(',');
                sb.Append("\"returnedCount\":").Append(children.Count).Append(',');
                sb.Append("\"children\":").Append(children.Json);
                sb.Append('}');
                return sb.ToString();
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ memory ============================

        [BridgeTool("unity_senses_profiler_memory", Title = "Profiler Memory",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Snapshot live Unity memory allocator stats: total allocated, reserved, " +
            "unused reserved, temp allocator size, and the managed (GC) heap. " +
            "Set gc_collect=true to run a full GC first for a stable baseline.")]
        public string ProfilerMemory(bool gc_collect = false)
        {
            try
            {
                if (gc_collect)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                // Fully qualified: the unqualified 'Profiler' identifier resolves
                // to this file's own namespace (UnityOpenMcpBridge.ProfilerExt), not
                // the static UnityEngine.Profiling.Profiler type, so it must be
                // written out in full to avoid CS0234.
                long allocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                long reserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
                long unusedReserved = UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong();
                long tempAllocator = 0;
                try { tempAllocator = UnityEngine.Profiling.Profiler.GetTempAllocatorSize(); } catch { }
                long managed = GC.GetTotalMemory(false);

                var sb = new StringBuilder(512);
                sb.Append('{');
                sb.Append("\"gcCollected\":").Append(gc_collect ? "true" : "false").Append(',');
                sb.Append("\"allocatedBytes\":").Append(allocated).Append(',');
                sb.Append("\"reservedBytes\":").Append(reserved).Append(',');
                sb.Append("\"unusedReservedBytes\":").Append(unusedReserved).Append(',');
                sb.Append("\"tempAllocatorBytes\":").Append(tempAllocator).Append(',');
                sb.Append("\"managedHeapBytes\":").Append(managed).Append(',');
                sb.Append("\"humanReadable\":{");
                sb.Append("\"allocated\":").Append(Esc(HumanBytes(allocated))).Append(',');
                sb.Append("\"reserved\":").Append(Esc(HumanBytes(reserved))).Append(',');
                sb.Append("\"unusedReserved\":").Append(Esc(HumanBytes(unusedReserved))).Append(',');
                sb.Append("\"managedHeap\":").Append(Esc(HumanBytes(managed)));
                sb.Append("}}");
                return sb.ToString();
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ rendering ============================

        [BridgeTool("unity_senses_profiler_rendering", Title = "Profiler Rendering",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off, Lifecycle = LifecyclePolicy.None)]
        [System.ComponentModel.Description(
            "Snapshot the rendering environment: GPU / SystemInfo, active render " +
            "pipeline, QualitySettings, screen resolution, target frame rate, and " +
            "Time stats. For per-frame batch / draw-call counts use " +
            "unity_senses_profiler_capture.")]
        public string ProfilerRendering()
        {
            try
            {
                var sb = new StringBuilder(2048);
                sb.Append('{');

                // ---- SystemInfo (GPU / host) ----
                sb.Append("\"system\":{");
                sb.Append("\"gpu\":").Append(Esc(SystemInfo.graphicsDeviceName)).Append(',');
                sb.Append("\"gpuVendor\":").Append(Esc(SystemInfo.graphicsDeviceVendor)).Append(',');
                sb.Append("\"gpuVersion\":").Append(Esc(SystemInfo.graphicsDeviceVersion)).Append(',');
                sb.Append("\"deviceType\":").Append(Esc(SystemInfo.deviceType.ToString())).Append(',');
                sb.Append("\"vramMb\":").Append(SystemInfo.graphicsMemorySize).Append(',');
                sb.Append("\"processorType\":").Append(Esc(SystemInfo.processorType)).Append(',');
                sb.Append("\"processorCount\":").Append(SystemInfo.processorCount).Append(',');
                sb.Append("\"operatingSystem\":").Append(Esc(SystemInfo.operatingSystem));
                sb.Append("},");

                // ---- Render pipeline ----
                sb.Append("\"renderPipeline\":").Append(Esc(DetectRenderPipeline())).Append(',');

                // ---- Screen / resolution ----
                sb.Append("\"screen\":{");
                sb.Append("\"width\":").Append(Screen.width).Append(',');
                sb.Append("\"height\":").Append(Screen.height).Append(',');
                sb.Append("\"dpi\":").Append(Num(Screen.dpi)).Append(',');
                sb.Append("\"fullScreen\":").Append(Screen.fullScreen ? "true" : "false").Append(',');
                var res = Screen.currentResolution;
                sb.Append("\"currentWidth\":").Append(res.width).Append(',');
                sb.Append("\"currentHeight\":").Append(res.height).Append(',');
                sb.Append("\"refreshRate\":").Append(Num(res.refreshRateRatio.value));
                sb.Append("},");

                // ---- Quality / Application ----
                sb.Append("\"quality\":{");
                sb.Append("\"qualityLevel\":").Append(QualitySettings.GetQualityLevel());
                try
                {
                    var names = QualitySettings.names;
                    var lvl = QualitySettings.GetQualityLevel();
                    if (lvl >= 0 && lvl < names.Length)
                        sb.Append(",\"qualityName\":").Append(Esc(names[lvl]));
                }
                catch { }
                sb.Append(",\"vSyncCount\":").Append(QualitySettings.vSyncCount).Append(',');
                sb.Append("\"pixelLightCount\":").Append(QualitySettings.pixelLightCount).Append(',');
                sb.Append("\"antiAliasing\":").Append(QualitySettings.antiAliasing).Append(',');
                sb.Append("\"shadowCascades\":").Append(QualitySettings.shadowCascades).Append(',');
                sb.Append("\"softShadows\":").Append(QualitySettings.shadows == ShadowQuality.All ? "true" : "false");
                sb.Append("},");

                sb.Append("\"application\":{");
                sb.Append("\"targetFrameRate\":").Append(Application.targetFrameRate).Append(',');
                sb.Append("\"runInBackground\":").Append(Application.runInBackground ? "true" : "false").Append(',');
                sb.Append("\"isPlaying\":").Append(Application.isPlaying ? "true" : "false").Append(',');
                sb.Append("\"unityVersion\":").Append(Esc(Application.unityVersion));
                sb.Append("},");

                // ---- Time ----
                sb.Append("\"time\":{");
                sb.Append("\"frameCount\":").Append(Time.frameCount).Append(',');
                sb.Append("\"renderedFrameCount\":").Append(Time.renderedFrameCount).Append(',');
                sb.Append("\"timeScale\":").Append(Num(Time.timeScale)).Append(',');
                sb.Append("\"realtimeSinceStartup\":").Append(Num(Time.realtimeSinceStartup));
                sb.Append('}');

                sb.Append('}');
                return sb.ToString();
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ hierarchy helpers ============================

        private string AveragedHierarchy(int fromFrame, int toFrame, int threadIndex, string rootName,
            float minTime, string sortBy, int maxItems, int effDepth, int rawDepth)
        {
            int firstAvail = ProfilerDriver.firstFrameIndex;
            int lastAvail = ProfilerDriver.lastFrameIndex;
            fromFrame = Math.Max(fromFrame, firstAvail);
            toFrame = Math.Min(toFrame, lastAvail);
            int frameCount = toFrame - fromFrame + 1;
            if (frameCount <= 0)
                return ErrorJson("no_frames_in_range",
                    $"No frames in range [{fromFrame}..{toFrame}]. " +
                    $"Available: [{firstAvail}..{lastAvail}].");

            int sortColumn = GetSortColumn(sortBy);

            // name -> (totalMs, selfMs, calls, appearances)
            var acc = new Dictionary<string, (double total, double self, long calls, int count)>();

            for (int fi = fromFrame; fi <= toFrame; fi++)
            {
                using var fd = ProfilerDriver.GetHierarchyFrameDataView(
                    fi, threadIndex,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    sortColumn, false);
                if (fd == null || !fd.valid) continue;

                int rid = fd.GetRootItemID();
                var rcl = new List<int>();
                fd.GetItemChildren(rid, rcl);

                int pid = rid;
                if (!string.IsNullOrEmpty(rootName))
                {
                    int found = FindItemByName(fd, rid, rootName);
                    if (found >= 0) pid = found;
                }

                CollectFlat(fd, pid, effDepth, acc);
            }

            // Build, filter, sort, cap.
            var rows = new List<(string name, double avgTotal, double avgSelf, double avgCalls, int appeared)>(
                acc.Count);
            foreach (var kv in acc)
            {
                double avgTotal = kv.Value.total / kv.Value.count;
                if (avgTotal < minTime) continue;
                rows.Add((
                    kv.Key,
                    avgTotal,
                    kv.Value.self / kv.Value.count,
                    (double)kv.Value.calls / kv.Value.count,
                    kv.Value.count));
            }

            rows.Sort((a, b) =>
                (sortBy == "self" ? b.avgSelf : sortBy == "calls" ? b.avgCalls : b.avgTotal)
                .CompareTo(sortBy == "self" ? a.avgSelf : sortBy == "calls" ? a.avgCalls : a.avgTotal));

            int capped = Math.Min(rows.Count, maxItems);
            string rootLabel = string.IsNullOrEmpty(rootName) ? "(root)" : rootName;

            var sb = new StringBuilder(4096);
            sb.Append('{');
            sb.Append("\"mode\":\"averaged\",");
            sb.Append("\"frameCount\":").Append(frameCount).Append(',');
            sb.Append("\"fromFrame\":").Append(fromFrame).Append(',');
            sb.Append("\"toFrame\":").Append(toFrame).Append(',');
            sb.Append("\"threadIndex\":").Append(threadIndex).Append(',');
            sb.Append("\"root\":").Append(Esc(rootLabel)).Append(',');
            sb.Append("\"sort\":").Append(Esc(sortBy)).Append(',');
            sb.Append("\"depth\":").Append(rawDepth <= 0 ? 0 : rawDepth).Append(',');
            sb.Append("\"minMs\":").Append(Num(minTime)).Append(',');
            sb.Append("\"returnedCount\":").Append(capped).Append(',');
            sb.Append("\"totalUniqueNames\":").Append(acc.Count).Append(',');
            sb.Append("\"items\":[");
            for (int i = 0; i < capped; i++)
            {
                if (i > 0) sb.Append(',');
                var r = rows[i];
                sb.Append('{');
                sb.Append("\"name\":").Append(Esc(r.name)).Append(',');
                sb.Append("\"avgTotalMs\":").Append(Num(Math.Round(r.avgTotal, 3))).Append(',');
                sb.Append("\"avgSelfMs\":").Append(Num(Math.Round(r.avgSelf, 3))).Append(',');
                sb.Append("\"avgCalls\":").Append(Num(Math.Round(r.avgCalls, 1))).Append(',');
                sb.Append("\"appearedIn\":").Append(r.appeared).Append(',');
                sb.Append("\"missedFrames\":").Append(frameCount - r.appeared);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private void CollectFlat(HierarchyFrameDataView fd, int parentId, int remainingDepth,
            Dictionary<string, (double total, double self, long calls, int count)> acc)
        {
            var childIds = new List<int>();
            fd.GetItemChildren(parentId, childIds);

            foreach (var cid in childIds)
            {
                var name = fd.GetItemName(cid);
                var totalMs = (double)fd.GetItemColumnDataAsFloat(cid, HierarchyFrameDataView.columnTotalTime);
                var selfMs = (double)fd.GetItemColumnDataAsFloat(cid, HierarchyFrameDataView.columnSelfTime);
                var calls = (long)fd.GetItemColumnDataAsFloat(cid, HierarchyFrameDataView.columnCalls);

                if (acc.TryGetValue(name, out var ex))
                    acc[name] = (ex.total + totalMs, ex.self + selfMs, ex.calls + calls, ex.count + 1);
                else
                    acc[name] = (totalMs, selfMs, calls, 1);

                if (remainingDepth > 1)
                    CollectFlat(fd, cid, remainingDepth - 1, acc);
            }
        }

        private int FindItemByName(HierarchyFrameDataView fd, int parentId, string name)
        {
            var childIds = new List<int>();
            fd.GetItemChildren(parentId, childIds);

            foreach (var cid in childIds)
            {
                var itemName = fd.GetItemName(cid);
                if (itemName != null && itemName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return cid;

                int found = FindItemByName(fd, cid, name);
                if (found >= 0) return found;
            }
            return -1;
        }

        private static int GetSortColumn(string sortBy)
        {
            switch (sortBy)
            {
                case "self": return HierarchyFrameDataView.columnSelfTime;
                case "calls": return HierarchyFrameDataView.columnCalls;
                default: return HierarchyFrameDataView.columnTotalTime;
            }
        }

        struct ChildrenResult { public StringBuilder Json; public int Count; }

        private ChildrenResult BuildChildren(HierarchyFrameDataView fd, int parentId, float minTime, int maxItems, int remainingDepth)
        {
            var childIds = new List<int>();
            fd.GetItemChildren(parentId, childIds);

            var sb = new StringBuilder(2048);
            sb.Append('[');
            int shown = 0;
            foreach (var cid in childIds)
            {
                var totalTime = fd.GetItemColumnDataAsFloat(cid, HierarchyFrameDataView.columnTotalTime);
                if (totalTime < minTime) continue;
                if (shown >= maxItems) break;
                if (shown > 0) sb.Append(',');
                shown++;

                var selfTime = fd.GetItemColumnDataAsFloat(cid, HierarchyFrameDataView.columnSelfTime);
                var calls = (int)fd.GetItemColumnDataAsFloat(cid, HierarchyFrameDataView.columnCalls);

                sb.Append('{');
                sb.Append("\"itemId\":").Append(cid).Append(',');
                sb.Append("\"name\":").Append(Esc(fd.GetItemName(cid))).Append(',');
                sb.Append("\"totalMs\":").Append(Num(Math.Round(totalTime, 3))).Append(',');
                sb.Append("\"selfMs\":").Append(Num(Math.Round(selfTime, 3))).Append(',');
                sb.Append("\"calls\":").Append(calls);

                if (remainingDepth > 1)
                {
                    var sub = BuildChildren(fd, cid, minTime, maxItems, remainingDepth - 1);
                    if (sub.Count > 0)
                        sb.Append(",\"children\":").Append(sub.Json);
                }

                sb.Append('}');
            }
            sb.Append(']');
            return new ChildrenResult { Json = sb, Count = shown };
        }

        // ============================ formatting helpers ============================

        private static string DetectRenderPipeline()
        {
            try
            {
                // Unity 2021.2+: GraphicsSettings.currentRenderPipeline holds the
                // active RenderPipelineAsset (URP/HDRP) or null for the Built-in RP.
                var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                if (rp != null) return rp.GetType().Name;
            }
            catch { }
            try
            {
                var qrp = QualitySettings.renderPipeline;
                if (qrp != null) return qrp.GetType().Name;
            }
            catch { }
            return "Built-in Render Pipeline";
        }

        private static string HumanBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            double gb = mb / 1024.0;
            return gb.ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

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
        // BridgeJson (T30.5). This thin wrapper preserves the `null ⇒ ""`
        // contract this file already had (BridgeJson.AppendJsonString emits
        // `null` for null — profiler field names are never expected to be null,
        // so we normalize here to avoid changing wire shape).
        private static string Esc(string s) => s == null ? "\"\"" : BridgeJson.EscapeString(s);
    }
}
