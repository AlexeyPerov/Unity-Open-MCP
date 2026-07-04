// M20 Plan 7 / T20.7.2 — VFX Graph embedded domain tools (compile-gated +
// auto-activating).
//
// Three typed tools for in-editor VFX Graph authoring:
//   - list: enumerate VisualEffectGraph (.vfx) assets (read-only)
//   - open: open a .vfx in the VFX Graph editor window (returns a summary)
//   - block_edit: narrow block-property patch (mutating; behind the gate)
//
// VFX Graph's editing API (UnityEditor.VFX: VFXGraph, VFXContext, VFXBlock,
// VFXSlot) is more internal/unstable than Shader Graph's — the competitor ships
// only list/open for the same reason. list and open work over the public
// runtime VisualEffectAsset type and the stable serialized file format, so they
// are version-stable. block_edit attempts a narrow property patch behind the
// VFXApi reflection helper; when the editor graph model cannot be reached (it
// requires the VFX Graph window to be open, with no stable public headless
// entry point), the tool returns a structured `vfx_block_edit_requires_editor_window`
// error and the agent falls back to manual editing in the window. This matches
// the execution plan §T20.7.2 fallback note.
//
// list is read-only (Gate = Off). open is a non-mutating window bring-up
// (Gate = Off). block_edit is mutating (Gate = Enforce, paths_hint = .vfx asset
// path, EditorSettle lifecycle). The gate + paths_hint contract on the mutating
// member is the documented advantage over the competitor's ungated surface.
//
// Compile-gate-only: when com.unity.visualeffectgraph is absent the tools are
// not compiled in and the capability surface reports the domain as
// `available: false (dependency missing: com.unity.visualeffectgraph)`. When
// the package IS present, the `vfx` group auto-activates for the session (no
// manual manage_tools call) — see T20.7.0.
//
// Naming: `unity_open_mcp_vfx_<action>` (snake_case domain prefix).
#if UNITY_OPEN_MCP_EXT_VFX
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;

namespace UnityOpenMcpBridge.Extensions.VFX
{
    [BridgeToolType]
    public static class VFXTools
    {
        // =====================================================================
        // list (read-only)
        // =====================================================================

        // List every VisualEffectGraph (.vfx) asset under Assets/. Read-only
        // (Gate = Off) — uses AssetDatabase.FindAssets + the public runtime
        // VisualEffectAsset type, so the path is stable across package
        // versions.
        [BridgeTool("unity_open_mcp_vfx_list",
            Title = "VFX Graph: List",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "vfx")]
        [System.ComponentModel.Description(
            "List every VisualEffectGraph (.vfx) asset under Assets/. Read-only " +
            "(Gate = Off). Returns each asset's path, name, and file size. " +
            "Optionally filter by name/path substring and cap results. " +
            "Requires the com.unity.visualeffectgraph package installed.")]
        public static string List(string filter = null, int max_results = 100)
        {
            if (max_results <= 0) max_results = 100;
            if (max_results > 500) max_results = 500;

            var assets = VFXApi.ListVfxAssets(filter, max_results);
            var sb = new StringBuilder(256);
            sb.Append("\"vfxList\":{");
            sb.Append("\"count\":").Append(assets.Count).Append(',');
            if (!string.IsNullOrEmpty(filter))
                sb.Append("\"filter\":").Append(VFXJson.Esc(filter)).Append(',');
            sb.Append("\"assets\":[");
            for (int i = 0; i < assets.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var a = assets[i];
                sb.Append('{');
                sb.Append("\"assetPath\":").Append(VFXJson.Esc(a.AssetPath)).Append(',');
                sb.Append("\"name\":").Append(VFXJson.Esc(a.Name)).Append(',');
                sb.Append("\"assetName\":").Append(VFXJson.Esc(a.AssetName ?? "")).Append(',');
                sb.Append("\"fileSizeBytes\":").Append(a.FileSizeBytes.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
            return VFXJson.Ok(sb.ToString());
        }

        // =====================================================================
        // open (read-only window bring-up + structured summary)
        // =====================================================================

        // Open a .vfx asset in the VFX Graph editor window and return a
        // structured summary (context/block counts + exposed properties).
        // Non-mutating state change (opens a window) — Gate = Off.
        [BridgeTool("unity_open_mcp_vfx_open",
            Title = "VFX Graph: Open",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "vfx")]
        [System.ComponentModel.Description(
            "Open a VisualEffectGraph (.vfx) asset in the VFX Graph editor " +
            "window and return a structured summary (context count, block " +
            "count, exposed property count, property names). Read-only window " +
            "bring-up — Gate is Off. Address the graph by asset_path " +
            "('Assets/.../*.vfx'). Requires com.unity.visualeffectgraph.")]
        public static string Open(string asset_path = null)
        {
            if (string.IsNullOrEmpty(asset_path))
                return VFXJson.Error("missing_parameter",
                    "'asset_path' is required ('Assets/.../*.vfx').");

            var asset = AssetDatabase.LoadAssetAtPath<Object>(asset_path);
            if (asset == null)
                return VFXJson.Error("asset_not_found",
                    $"No VisualEffectGraph asset found at '{asset_path}'.");

            if (!asset_path.EndsWith(".vfx"))
                return VFXJson.Error("invalid_parameter",
                    "'asset_path' must end with '.vfx'.");

            // Bring up the VFX Graph editor window for the asset. Reflection
            // over UnityEditor.VFX (the window type name is stable). Failure to
            // open the window is non-fatal — the summary is still returned.
            VFXApi.TryOpenInEditor(asset_path);

            // Structured summary: context/block counts + exposed properties,
            // read off the public runtime VisualEffectAsset / serialized file.
            VFXApi.BuildBlockSummary(asset_path,
                out int contextCount, out int blockCount,
                out int propertyCount, out string propertiesJson,
                out string parseWarning);

            var sb = new StringBuilder(256);
            sb.Append("\"vfxGraph\":{");
            sb.Append("\"assetPath\":").Append(VFXJson.Esc(asset_path)).Append(',');
            sb.Append("\"contextCount\":").Append(contextCount).Append(',');
            sb.Append("\"blockCount\":").Append(blockCount).Append(',');
            sb.Append("\"propertyCount\":").Append(propertyCount).Append(',');
            if (parseWarning != null)
                sb.Append("\"parseWarning\":").Append(VFXJson.Esc(parseWarning)).Append(',');
            sb.Append("\"properties\":").Append(propertiesJson ?? "[]");
            sb.Append('}');
            return VFXJson.Ok(sb.ToString());
        }

        // =====================================================================
        // block_edit (mutating — narrow property patch behind the gate)
        // =====================================================================

        // Attempt a narrow block-property patch on a .vfx asset. block_selector
        // names the target (by type-name fragment, e.g. "SetVelocity"); property
        // is the field; value_json is the new value. Mutating: paths_hint is the
        // .vfx asset path, EditorSettle lifecycle. VFX Graph's editor graph
        // model is internal and requires the VFX Graph window to be open; when
        // it is not, the tool returns a structured
        // `vfx_block_edit_requires_editor_window` error and the agent falls
        // back to manual editing.
        [BridgeTool("unity_open_mcp_vfx_block_edit",
            Title = "VFX Graph: Block Edit",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "vfx")]
        [System.ComponentModel.Description(
            "Patch a single property on a block in a VisualEffectGraph (.vfx). " +
            "block_selector names the target block (by type-name fragment, e.g. " +
            "'SetVelocity', 'SetColor'); property is the field name; value_json " +
            "is the new value. Mutating: runs the gate path; paths_hint is the " +
            ".vfx asset path. Requires com.unity.visualeffectgraph. VFX Graph's " +
            "editor graph model is internal and requires the VFX Graph window to " +
            "be open; when it is not, the tool returns a structured " +
            "vfx_block_edit_requires_editor_window error — open the graph in the " +
            "VFX Graph window (unity_open_mcp_vfx_open) and retry, or edit the " +
            "block manually.")]
        public static string BlockEdit(
            string asset_path = null,
            string block_selector = null,
            string property = null,
            string value_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return VFXJson.Error("paths_hint_required",
                    "vfx_block_edit is mutating; pass a non-empty paths_hint " +
                    "(the .vfx asset path).");

            if (string.IsNullOrEmpty(asset_path))
                return VFXJson.Error("missing_parameter",
                    "'asset_path' is required ('Assets/.../*.vfx').");

            if (!asset_path.EndsWith(".vfx"))
                return VFXJson.Error("invalid_parameter",
                    "'asset_path' must end with '.vfx'.");

            if (string.IsNullOrEmpty(block_selector))
                return VFXJson.Error("missing_parameter",
                    "'block_selector' is required (a type-name fragment, e.g. " +
                    "'SetVelocity', 'SetColor').");

            if (string.IsNullOrEmpty(property))
                return VFXJson.Error("missing_parameter",
                    "'property' is required (the block field to patch).");

            if (string.IsNullOrEmpty(value_json))
                return VFXJson.Error("missing_parameter",
                    "'value_json' is required (the new value).");

            if (!VFXApi.TryEditBlock(asset_path, block_selector, property, value_json,
                    out var error))
            {
                return VFXJson.Error(error ?? "vfx_api_unavailable",
                    $"Could not patch property '{property}' on block matching " +
                    $"'{block_selector}' in '{asset_path}'. VFX Graph's editor " +
                    "graph model is internal and requires the VFX Graph window " +
                    "to be open. Open the graph first " +
                    "(unity_open_mcp_vfx_open) and retry, or edit the block " +
                    "manually in the window.");
            }

            var sb = new StringBuilder(180);
            sb.Append("\"blockEdit\":{");
            sb.Append("\"applied\":true,");
            sb.Append("\"assetPath\":").Append(VFXJson.Esc(asset_path)).Append(',');
            sb.Append("\"blockSelector\":").Append(VFXJson.Esc(block_selector)).Append(',');
            sb.Append("\"property\":").Append(VFXJson.Esc(property)).Append(',');
            sb.Append("\"value\":").Append(VFXJson.Esc(value_json));
            sb.Append('}');
            return VFXJson.Ok(sb.ToString());
        }
    }
}
#endif
