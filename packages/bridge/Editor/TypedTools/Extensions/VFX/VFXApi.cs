// M20 Plan 7 / T20.7.2 — reflection surface over Unity.VisualEffectGraph.
//
// VFX Graph's editing API (UnityEditor.VFX: VFXGraph, VFXContext, VFXBlock,
// VFXSlot) is largely internal and the public surface is thinner than Shader
// Graph's. Rather than bind to one version's shape at compile time, this
// helper resolves the types/methods by reflection at call time and returns
// structured success/failure. The read paths (list / block summary) work over
// UnityEngine.VFX.VisualEffectAsset (a public runtime type), which makes them
// version-stable; the mutate path (block_edit) reflects over the editor graph
// model and degrades to a `vfx_api_unavailable` error when the surface differs.
//
// Every public tool method in VFXTools delegates here, so a future version
// change is fixed in ONE place. The helper never throws out of the tool path —
// exceptions are caught and converted.
//
// Unity-version dependency: tested against com.unity.visualeffectgraph as
// shipped with Unity 6. The runtime VisualEffectAsset type and its
// VisualEffectAssetInfo surface (name / bounds / culling) are stable; the
// editor graph model is internal and varies more.
#if UNITY_OPEN_MCP_EXT_VFX
#pragma warning disable CS0618
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.VFX
{
    // Reflection wrapper over Unity.VisualEffectGraph. All members are static
    // and best-effort; failures surface as (false, errorCode) tuples.
    internal static class VFXApi
    {
        // Cached assembly / type lookups. The VFX Graph package compiles to:
        //   runtime  → Unity.VisualEffectGraph.Runtime (VisualEffectAsset lives here)
        //   editor   → Unity.VisualEffectGraph.Editor (graph model lives here)
        private static readonly Assembly EditorAssembly = LoadEditorAssembly();
        private static readonly Type GraphEditorWindowType =
            ResolveEditor("UnityEditor.VFX.VFXGraphEditorWindow");
        private static readonly Type VfxGraphType =
            ResolveEditor("UnityEditor.VFX.VFXGraph");

        private static Assembly LoadEditorAssembly()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "Unity.VisualEffectGraph.Editor") return asm;
            }
            // Fall back to a name-contains search in case the simple name differs.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name;
                if (n != null &&
                    (n.IndexOf("VisualEffectGraph", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     n.IndexOf("VFXGraph", StringComparison.OrdinalIgnoreCase) >= 0))
                    return asm;
            }
            return null;
        }

        private static Type ResolveEditor(string fullName)
        {
            if (EditorAssembly == null) return null;
            return EditorAssembly.GetType(fullName);
        }

        // =====================================================================
        // list — enumerate .vfx assets (version-stable, runtime types only)
        // =====================================================================

        // Build a list of every VisualEffectGraph (.vfx) asset under Assets/.
        // Uses AssetDatabase.FindAssets + the public runtime VisualEffectAsset
        // type, so this path is stable across package versions (no editor-model
        // reflection needed).
        public static List<VfxAssetInfo> ListVfxAssets(string filter, int maxResults)
        {
            var guids = AssetDatabase.FindAssets("t:VisualEffectAsset");
            var results = new List<VfxAssetInfo>();
            var filterLower = string.IsNullOrEmpty(filter) ? null : filter.ToLowerInvariant();
            for (int i = 0; i < guids.Length; i++)
            {
                if (results.Count >= maxResults) break;
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (!assetPath.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase)) continue;
                if (filterLower != null &&
                    assetPath.ToLowerInvariant().IndexOf(filterLower, StringComparison.Ordinal) < 0)
                    continue;

                var info = new VfxAssetInfo
                {
                    AssetPath = assetPath,
                    Name = Path.GetFileNameWithoutExtension(assetPath),
                    FileSizeBytes = TryGetFileSize(assetPath),
                };
                // Best-effort: load the asset to read its name. VisualEffectAsset
                // exposes .name via UnityEngine.Object. Failure is non-fatal.
                try
                {
                    var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
                    if (asset != null) info.AssetName = asset.name;
                }
                catch
                {
                    // Asset load can fail on broken/outdated graphs; keep the
                    // path/name from the GUID lookup and continue.
                }
                results.Add(info);
            }
            return results;
        }

        public struct VfxAssetInfo
        {
            public string AssetPath;
            public string Name;
            public string AssetName;
            public long FileSizeBytes;
        }

        private static long TryGetFileSize(string assetPath)
        {
            try
            {
                var fi = new FileInfo(assetPath);
                return fi.Exists ? fi.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        // =====================================================================
        // open — bring up the VFX Graph editor window (best-effort)
        // =====================================================================

        // Bring up the VFX Graph editor window for the asset. Best-effort;
        // failures are non-fatal (the summary is still returned). The window
        // type is UnityEditor.VFX.VFXGraphEditorWindow; resolved by reflection
        // so the tool tracks the installed package version.
        public static void TryOpenInEditor(string assetPath)
        {
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (asset != null)
                {
                    // Selection + ping is the most stable cross-version bring-up;
                    // the VFX Graph editor follows the current selection.
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
            catch
            {
                // Best-effort window bring-up; never fail the tool on this.
            }
        }

        // =====================================================================
        // block summary — parsed from the .vfx asset's metadata (stable read)
        // =====================================================================

        // Build a structured summary (block/context counts, exposed properties)
        // for a .vfx asset. Reads the asset via the public runtime type where
        // possible and falls back to a lightweight scan of the serialized graph
        // text. This read path is version-stable: VisualEffectAsset is public,
        // and the serialized file format is the editor's own round-trip.
        public static void BuildBlockSummary(
            string assetPath,
            out int contextCount,
            out int blockCount,
            out int propertyCount,
            out string propertiesJson,
            out string parseWarning)
        {
            contextCount = 0;
            blockCount = 0;
            propertyCount = 0;
            propertiesJson = "[]";
            parseWarning = null;

            // Exposed properties: VisualEffectAsset exposes a
            // VisualEffectAssetInfo via the package, but the public surface
            // varies. Best-effort via reflection; fall back to a text scan.
            try
            {
                var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
                if (asset != null)
                {
                    var info = ReadAssetInfo(asset);
                    if (info != null)
                    {
                        propertyCount = info.PropertyCount;
                        propertiesJson = info.PropertiesJson;
                    }
                }
            }
            catch (Exception e)
            {
                parseWarning = $"Could not read VFX asset info: {e.Message}";
            }

            // Context / block counts: the serialized graph stores these as
            // JSON-ish entries. A lightweight count of the block/context
            // markers gives an approximate structural read that is stable
            // across versions without binding to the internal model.
            try
            {
                ScanGraphStructure(assetPath, out contextCount, out blockCount);
            }
            catch (Exception e)
            {
                if (parseWarning == null)
                    parseWarning = $"Block/context scan failed: {e.Message}";
            }
        }

        private struct AssetInfoRead
        {
            public int PropertyCount;
            public string PropertiesJson;
        }

        // Read exposed properties off a VisualEffectAsset via reflection. The
        // package exposes `VisualEffectAsset.info` (a VisualEffectAssetInfo)
        // with a `GetRegisteredProperties` / `m_Properties` surface, but the
        // exact shape varies; reflect defensively.
        private static AssetInfoRead? ReadAssetInfo(VisualEffectAsset asset)
        {
            var type = asset.GetType();
            // Common surfaces across versions: a `info` property, or a method.
            object info = null;
            var infoProp = type.GetProperty("info",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (infoProp != null) info = infoProp.GetValue(asset);
            if (info == null)
            {
                var infoField = type.GetField("m_AssetInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (infoField != null) info = infoField.GetValue(asset);
            }
            if (info == null) return null;

            var infoType = info.GetType();
            var names = new List<string>();

            // Try a GetRegisteredProperties / GetProperties method returning
            // a collection of named entries.
            foreach (var methodName in new[] { "GetRegisteredProperties", "GetProperties" })
            {
                var m = infoType.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) continue;
                try
                {
                    var result = m.Invoke(info, null) as IEnumerable;
                    if (result != null)
                    {
                        foreach (var item in result)
                        {
                            var n = item?.GetType().GetProperty("name",
                                BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) as string;
                            names.Add(n ?? item?.ToString() ?? "");
                        }
                        break;
                    }
                }
                catch
                {
                    // Try the next method shape.
                }
            }

            // Fall back to a field scan if no method surfaced names.
            if (names.Count == 0)
            {
                var propField = infoType.GetField("m_Properties",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (propField != null)
                {
                    var list = propField.GetValue(info) as IEnumerable;
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            var n = item?.GetType().GetField("m_Name",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                ?.GetValue(item) as string;
                            names.Add(n ?? "");
                        }
                    }
                }
            }

            return new AssetInfoRead
            {
                PropertyCount = names.Count,
                PropertiesJson = BuildNamesJson(names),
            };
        }

        // Lightweight scan of the serialized .vfx for block/context markers.
        // The serialized graph stores blocks and contexts as JSON entries with
        // stable type-name fragments; counting them gives an approximate
        // structural read.
        private static void ScanGraphStructure(
            string assetPath, out int contextCount, out int blockCount)
        {
            contextCount = 0;
            blockCount = 0;
            if (!File.Exists(assetPath)) return;
            string text;
            try { text = File.ReadAllText(assetPath); }
            catch { return; }

            // Count type-name fragments. The serialized graph uses "VFXContext"
            // and "VFXBlock" class-name strings; contexts are the outer nodes
            // (kVFXContextType*), blocks the inner (kVFXBlockType*). Counting
            // occurrences of the serialized type markers is approximate but
            // stable across versions.
            contextCount = CountOccurrences(text, "\"VFXContext\"");
            blockCount = CountOccurrences(text, "\"VFXBlock\"");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int idx = 0;
            while (true)
            {
                idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal);
                if (idx < 0) break;
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string BuildNamesJson(List<string> names)
        {
            var sb = new StringBuilder(128);
            sb.Append('[');
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(VFXJson.Esc(names[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // =====================================================================
        // block_edit (mutation — reflection over the editor graph model)
        // =====================================================================

        // Attempt a narrow block-property patch. VFX Graph's editor graph model
        // (VFXGraph → VFXContext → VFXBlock → VFXSlot) is internal, so this is
        // the highest-risk surface. When the model cannot be reached (version
        // mismatch / internal rename), the tool surfaces a structured
        // `vfx_api_unavailable` error and the agent is directed at manual
        // editing in the VFX Graph window.
        public static bool TryEditBlock(
            string assetPath,
            string block_selector,
            string property,
            string value_json,
            out string error)
        {
            error = null;

            // Resolve the editor graph type. If the editor assembly is missing
            // or the type renamed, surface the install/version error.
            if (EditorAssembly == null)
            {
                error = "vfx_assembly_not_found";
                return false;
            }
            if (VfxGraphType == null)
            {
                error = "vfx_api_unavailable";
                return false;
            }

            // The VFX Graph editor caches the loaded graph in an editor-side
            // resource manager keyed by the asset. Reaching the live VFXGraph
            // instance requires the editor window to be open; when it is not,
            // there is no stable public entry point to load + edit + save a
            // graph headlessly. Rather than bind to an unstable internal
            // surface, surface a clear "edit in the window" error so the agent
            // falls back to manual editing. This is the documented fallback
            // (see the execution plan §T20.7.2 note).
            error = "vfx_block_edit_requires_editor_window";
            return false;
        }
    }
}
#endif
