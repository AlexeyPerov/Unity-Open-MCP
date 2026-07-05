// M20 Plan 9 / T20.9.1 — 2D art pipeline: SpriteAtlas tools.
//
// Seven typed tools covering the SpriteAtlas authoring surface:
//
//   spriteatlas_create         — create a new SpriteAtlas asset at
//                                Assets/.../*.spriteatlas.
//   spriteatlas_get            — read packables + packing/texture/platform
//                                settings (read-only).
//   spriteatlas_add_packable   — add sprites/textures/folders to a SpriteAtlas.
//   spriteatlas_remove_packable— remove a packable by asset path.
//   spriteatlas_modify         — patch packing/texture/include-in-build
//                                settings (structured patch; unknown fields
//                                reported, not fatal).
//   spriteatlas_delete         — delete the .spriteatlas asset.
//   spriteatlas_list           — list SpriteAtlas assets (read-only,
//                                offline-routeable in principle).
//
// The SpriteAtlas / SpriteAtlasAsset / SpriteAtlasPackingSettings /
// SpriteAtlasTextureSettings types live in the built-in 2D module
// (UnityEngine.U2D / UnityEditor.U2D in CoreModule) and are present in every
// Unity install, so this domain ships UNGATED — no UNITY_OPEN_MCP_EXT_2D
// define. The `2d` tool group is still hidden from ListTools until the session
// activates it via unity_open_mcp_manage_tools (group visibility is a session
// concern, independent of compile-gating).
//
// The .spriteatlas file stores a SpriteAtlasAsset (the authoring object); the
// runtime SpriteAtlas is a packed artifact. We author via
// SpriteAtlasAsset.Load / Save and mutate packables via Add / Remove. Packables
// are Object references (Sprite / Texture / DefaultAsset folder); we resolve
// them by Assets/-rooted path and report them back by path + type so agents
// can round-trip without instance ids.
//
// Naming: `unity_open_mcp_spriteatlas_<action>` (snake_case domain prefix).
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;
using U2D = UnityEditor.U2D;
using UnityOpenMcpBridge.ObjectRefs;

// SpriteAtlasAsset's settings getters/setters (SetIncludeInBuild,
// GetPackingSettings, GetTextureSettings, GetPlatformSettings, ...) are
// deprecated in Unity 6 in favor of SpriteAtlasImporter. The importer path is
// preferred; the Asset methods survive only as a fallback for older Unity and
// emit CS0618 there. Suppress that warning for the fallback call sites.
#pragma warning disable CS0618
namespace UnityOpenMcpBridge.Extensions.SpriteAtlasExt
{
    // M20 Plan 9 / T20.9.1 — SpriteAtlas tools. Registry-discovered via
    // [BridgeToolType] + [BridgeTool]. Mutating tools declare IsMutating = true
    // and accept a snake_case paths_hint (bound to the C# pathsHint parameter
    // by name) so the gate can scope the verify checkpoint to the asset path.
    [BridgeToolType]
    public static class SpriteAtlasTools
    {
        // =====================================================================
        // create
        // =====================================================================

        // Create a new SpriteAtlas asset at Assets/.../*.spriteatlas. Creates
        // intermediate folders. include_in_build defaults to true.
        [BridgeTool("unity_open_mcp_spriteatlas_create",
            Title = "SpriteAtlas: Create",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Create a new SpriteAtlas asset at an Assets/-rooted .spriteatlas " +
            "path. Intermediate folders are created if missing. include_in_build " +
            "defaults to true (the atlas ships with the player build). Mutating: " +
            "runs the full gate path; paths_hint is the new asset path.")]
        public static string Create(
            string asset_path,
            bool include_in_build = true,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_create is mutating; pass a non-empty paths_hint " +
                    "scoped to the new .spriteatlas asset path.");

            var normalized = NormalizeAtlasPath(asset_path);
            if (normalized.Error != null) return normalized.Error;

            // Ensure parent folders exist.
            EnsureFolderFor(normalized.Path);

            // Create via the SpriteAtlasAsset authoring API: new instance →
            // Internal_Create (binds the native backing) → Save to disk.
            var asset = new U2D.SpriteAtlasAsset();
            var internalCreate = typeof(U2D.SpriteAtlasAsset).GetMethod(
                "Internal_Create",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (internalCreate == null)
                return SpriteAtlasJson.Error("api_unavailable",
                    "SpriteAtlasAsset.Internal_Create is unavailable on this Unity version.");
            internalCreate.Invoke(null, new object[] { asset });

            asset.name = System.IO.Path.GetFileNameWithoutExtension(normalized.Path);

            U2D.SpriteAtlasAsset.Save(asset, normalized.Path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // include_in_build moved from SpriteAtlasAsset to SpriteAtlasImporter
            // (the Asset method is deprecated). Apply after save so the importer
            // exists for the asset path.
            var importer = UnityEditor.AssetImporter.GetAtPath(normalized.Path) as UnityEditor.U2D.SpriteAtlasImporter;
            if (importer != null)
            {
                importer.includeInBuild = include_in_build;
                importer.SaveAndReimport();
            }

            var loaded = AssetDatabase.LoadAssetAtPath<UnityEngine.U2D.SpriteAtlas>(normalized.Path);
            long instanceId = loaded != null ? InstanceId.Of(loaded) : 0;

            var sb = new StringBuilder(160);
            sb.Append("\"path\":").Append(SpriteAtlasJson.Esc(normalized.Path));
            sb.Append(",\"name\":").Append(SpriteAtlasJson.Esc(asset.name));
            sb.Append(",\"includeInBuild\":").Append(include_in_build ? "true" : "false");
            sb.Append(",\"instanceId\":").Append(instanceId);
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // get (read-only)
        // =====================================================================

        // Read packables + packing/texture/default-platform settings. Read-only.
        [BridgeTool("unity_open_mcp_spriteatlas_get",
            Title = "SpriteAtlas: Get",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Read-only: SpriteAtlas snapshot — packables (asset paths + type), " +
            "include-in-build, is-variant, packing settings (blockOffset / " +
            "padding / enableRotation / enableTightPacking / enableAlphaDilation), " +
            "texture settings (maxTextureSize / anisoLevel / filterMode / " +
            "generateMipMaps / readable / sRGB), and the default platform " +
            "settings (maxTextureSize / format / textureCompression). Gate-free.")]
        public static string Get(string asset_path)
        {
            var asset = LoadAtlasAsset(asset_path);
            if (asset.Error != null) return asset.Error;
            var sb = new StringBuilder(512);
            BuildAtlasState(asset.Asset, asset.Path, sb);
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // add_packable
        // =====================================================================

        // Add packables (sprites / textures / DefaultAsset folders) by
        // Assets/-rooted path. Resolves each path to its main asset Object.
        // Per-path errors are accumulated — a single bad path does not abort
        // the batch.
        [BridgeTool("unity_open_mcp_spriteatlas_add_packable",
            Title = "SpriteAtlas: Add Packable",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Add packables (sprites / textures / DefaultAsset folders) to a " +
            "SpriteAtlas by Assets/-rooted path. Each path resolves to its main " +
            "asset Object. Per-path errors are accumulated — a single bad path " +
            "does not abort the batch. Mutating: runs the full gate path; " +
            "paths_hint is the .spriteatlas asset path.")]
        public static string AddPackable(
            string asset_path,
            string[] packable_paths,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_add_packable is mutating; pass a non-empty paths_hint.");
            if (packable_paths == null || packable_paths.Length == 0)
                return SpriteAtlasJson.Error("missing_parameter",
                    "'packable_paths' (Assets/-rooted array) is required.");

            var asset = LoadAtlasAsset(asset_path);
            if (asset.Error != null) return asset.Error;

            var added = new StringBuilder(256);
            var errors = new StringBuilder(256);
            added.Append('[');
            errors.Append('[');
            bool firstAdded = true;
            bool firstError = true;
            var toAdd = new List<Object>();

            foreach (var rawPath in packable_paths)
            {
                var p = rawPath?.Replace('\\', '/').Trim();
                if (string.IsNullOrEmpty(p))
                {
                    AppendError(errors, ref firstError, "", "empty packable path");
                    continue;
                }
                var obj = AssetDatabase.LoadMainAssetAtPath(p);
                if (obj == null)
                {
                    AppendError(errors, ref firstError, p, "asset not found at path");
                    continue;
                }
                toAdd.Add(obj);
                if (!firstAdded) added.Append(',');
                firstAdded = false;
                added.Append("{\"path\":").Append(SpriteAtlasJson.Esc(p));
                added.Append(",\"type\":").Append(SpriteAtlasJson.Esc(obj.GetType().Name));
                added.Append('}');
            }
            added.Append(']');
            errors.Append(']');

            if (toAdd.Count > 0)
            {
                asset.Asset.Add(toAdd.ToArray());
                U2D.SpriteAtlasAsset.Save(asset.Asset, asset.Path);
                AssetDatabase.SaveAssets();
            }

            var sb = new StringBuilder(320);
            sb.Append("\"added\":").Append(added);
            sb.Append(",\"errors\":").Append(errors);
            sb.Append(",\"packableCount:after\":").Append(CountPackables(asset.Asset));
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // remove_packable
        // =====================================================================

        // Remove packables by Assets/-rooted path. Per-path errors accumulated.
        [BridgeTool("unity_open_mcp_spriteatlas_remove_packable",
            Title = "SpriteAtlas: Remove Packable",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Remove packables from a SpriteAtlas by Assets/-rooted path. Each " +
            "path resolves to its main asset Object and is removed if present in " +
            "the packables list. Per-path errors are accumulated. Mutating: runs " +
            "the full gate path; paths_hint is the .spriteatlas asset path.")]
        public static string RemovePackable(
            string asset_path,
            string[] packable_paths,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_remove_packable is mutating; pass a non-empty paths_hint.");
            if (packable_paths == null || packable_paths.Length == 0)
                return SpriteAtlasJson.Error("missing_parameter",
                    "'packable_paths' (Assets/-rooted array) is required.");

            var asset = LoadAtlasAsset(asset_path);
            if (asset.Error != null) return asset.Error;

            var current = GetPackables(asset.Asset);
            var removed = new StringBuilder(256);
            var errors = new StringBuilder(256);
            removed.Append('[');
            errors.Append('[');
            bool firstRemoved = true;
            bool firstError = true;
            var toRemove = new List<Object>();

            foreach (var rawPath in packable_paths)
            {
                var p = rawPath?.Replace('\\', '/').Trim();
                if (string.IsNullOrEmpty(p))
                {
                    AppendError(errors, ref firstError, "", "empty packable path");
                    continue;
                }
                // Match by asset path among the current packables.
                Object match = null;
                foreach (var entry in current)
                {
                    if (entry.Path == p) { match = entry.Object; break; }
                }
                if (match == null)
                {
                    AppendError(errors, ref firstError, p, "not a packable of this atlas");
                    continue;
                }
                toRemove.Add(match);
                if (!firstRemoved) removed.Append(',');
                firstRemoved = false;
                removed.Append("{\"path\":").Append(SpriteAtlasJson.Esc(p)).Append('}');
            }
            removed.Append(']');
            errors.Append(']');

            if (toRemove.Count > 0)
            {
                asset.Asset.Remove(toRemove.ToArray());
                U2D.SpriteAtlasAsset.Save(asset.Asset, asset.Path);
                AssetDatabase.SaveAssets();
            }

            var sb = new StringBuilder(320);
            sb.Append("\"removed\":").Append(removed);
            sb.Append(",\"errors\":").Append(errors);
            sb.Append(",\"packableCount:after\":").Append(CountPackables(asset.Asset));
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // modify (structured patch)
        // =====================================================================

        // Patch include_in_build + packing/texture settings. Each setting is a
        // JSON object passed as a raw JSON string (settings_json). Unknown
        // fields are reported, not fatal.
        [BridgeTool("unity_open_mcp_spriteatlas_modify",
            Title = "SpriteAtlas: Modify Settings",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Patch SpriteAtlas settings: include_in_build (bool), packing " +
            "(blockOffset / padding / enableRotation / enableTightPacking / " +
            "enableAlphaDilation), and texture (anisoLevel / filterMode / " +
            "generateMipMaps / readable / sRGB). settings_json is a JSON object " +
            "with three optional sub-objects: " +
            "{include_in_build, packing:{...}, texture:{...}}. Unknown fields " +
            "are reported in `unknownFields`, not fatal. NOTE: in this Unity " +
            "version the packing/texture settings are applied to the in-memory " +
            "SpriteAtlasAsset (they take effect for the next pack) but are NOT " +
            "written to the .spriteatlas file's serialized form — Unity manages " +
            "them via the internal Sprite Atlas packing pipeline, not the public " +
            "Save path. Mutating: runs the full gate path; paths_hint is the " +
            ".spriteatlas asset path.")]
        public static string Modify(
            string asset_path,
            string settings_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_modify is mutating; pass a non-empty paths_hint.");
            if (string.IsNullOrWhiteSpace(settings_json))
                return SpriteAtlasJson.Error("missing_parameter",
                    "'settings_json' is required (e.g. " +
                    "{\"include_in_build\":false,\"packing\":{\"padding\":4}}).");

            var asset = LoadAtlasAsset(asset_path);
            if (asset.Error != null) return asset.Error;

            // SpriteAtlasAsset's settings getters/setters are deprecated in
            // favor of SpriteAtlasImporter (Unity 6). Resolve the importer once;
            // fall back to the Asset methods when the importer is unavailable
            // (older Unity).
            var importer = UnityEditor.AssetImporter.GetAtPath(asset.Path) as UnityEditor.U2D.SpriteAtlasImporter;

            var applied = new StringBuilder(256);
            var unknown = new StringBuilder(256);
            applied.Append('[');
            unknown.Append('[');
            bool firstApplied = true;
            bool firstUnknown = true;

            // include_in_build
            var includeRaw = ExtractValue(settings_json, "include_in_build");
            if (includeRaw != null)
            {
                if (TryParseBool(includeRaw, out var b))
                {
                    if (importer != null) importer.includeInBuild = b;
                    else asset.Asset.SetIncludeInBuild(b);
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append("{\"field\":\"include_in_build\",\"value\":").Append(b ? "true" : "false").Append('}');
                }
                else
                {
                    if (!firstUnknown) unknown.Append(',');
                    firstUnknown = false;
                    unknown.Append("{\"field\":\"include_in_build\",\"reason\":\"invalid bool\"}");
                }
            }

            // packing
            var packingJson = ExtractObject(settings_json, "packing");
            if (!string.IsNullOrEmpty(packingJson))
            {
                var packing = importer != null
                    ? importer.packingSettings
                    : asset.Asset.GetPackingSettings();
                var (packingOutcomes, packingBoxed) = PatchStruct(packingJson, packing, new Dictionary<string, string>
                {
                    { "blockOffset", "int" },
                    { "padding", "int" },
                    { "enableRotation", "bool" },
                    { "enableTightPacking", "bool" },
                    { "enableAlphaDilation", "bool" },
                });
                // Unbox the (possibly mutated) struct back from the boxed copy
                // PatchStruct reflected into. Without this, the original `packing`
                // value-type variable keeps its pre-patch values.
                packing = (UnityEditor.U2D.SpriteAtlasPackingSettings)packingBoxed;
                foreach (var entry in packingOutcomes)
                {
                    if (entry.Ok)
                    {
                        if (!firstApplied) applied.Append(',');
                        firstApplied = false;
                        applied.Append("{\"field\":\"packing.").Append(SpriteAtlasJson.Esc(entry.Field));
                        applied.Append("\",\"value\":").Append(entry.RawValue).Append('}');
                    }
                    else
                    {
                        if (!firstUnknown) unknown.Append(',');
                        firstUnknown = false;
                        unknown.Append("{\"field\":\"packing.").Append(SpriteAtlasJson.Esc(entry.Field));
                        unknown.Append("\",\"reason\":").Append(SpriteAtlasJson.Esc(entry.Reason)).Append('}');
                    }
                }
                if (HasAnyPackingEntry(packingJson))
                {
                    if (importer != null) importer.packingSettings = packing;
                    else asset.Asset.SetPackingSettings(packing);
                }
            }

            // texture
            var textureJson = ExtractObject(settings_json, "texture");
            if (!string.IsNullOrEmpty(textureJson))
            {
                var texture = importer != null
                    ? importer.textureSettings
                    : asset.Asset.GetTextureSettings();
                var (textureOutcomes, textureBoxed) = PatchStruct(textureJson, texture, new Dictionary<string, string>
                {
                    // maxTextureSize is intentionally absent — it has no C#
                    // setter on SpriteAtlasTextureSettings in this Unity version
                    // (read-only property). It is controlled via the platform
                    // settings (SetPlatformSettings), not the texture settings.
                    { "anisoLevel", "int" },
                    { "filterMode", "enum:FilterMode" },
                    { "generateMipMaps", "bool" },
                    { "readable", "bool" },
                    { "sRGB", "bool" },
                });
                texture = (UnityEditor.U2D.SpriteAtlasTextureSettings)textureBoxed;
                foreach (var entry in textureOutcomes)
                {
                    if (entry.Ok)
                    {
                        if (!firstApplied) applied.Append(',');
                        firstApplied = false;
                        applied.Append("{\"field\":\"texture.").Append(SpriteAtlasJson.Esc(entry.Field));
                        applied.Append("\",\"value\":").Append(entry.RawValue).Append('}');
                    }
                    else
                    {
                        if (!firstUnknown) unknown.Append(',');
                        firstUnknown = false;
                        unknown.Append("{\"field\":\"texture.").Append(SpriteAtlasJson.Esc(entry.Field));
                        unknown.Append("\",\"reason\":").Append(SpriteAtlasJson.Esc(entry.Reason)).Append('}');
                    }
                }
                if (HasAnyTextureEntry(textureJson))
                {
                    if (importer != null) importer.textureSettings = texture;
                    else asset.Asset.SetTextureSettings(texture);
                }
            }

            applied.Append(']');
            unknown.Append(']');

            // When the importer path was used, SaveAndReimport persists the
            // settings changes (importer mutations are not written by
            // SpriteAtlasAsset.Save). Fall back to the Asset save otherwise.
            if (importer != null)
            {
                importer.SaveAndReimport();
            }
            else
            {
                U2D.SpriteAtlasAsset.Save(asset.Asset, asset.Path);
                AssetDatabase.SaveAssets();
            }

            var sb = new StringBuilder(360);
            sb.Append("\"applied\":").Append(applied);
            sb.Append(",\"unknownFields\":").Append(unknown);
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // delete
        // =====================================================================

        // Delete the .spriteatlas asset. Refuses if the path does not point at
        // a SpriteAtlas.
        [BridgeTool("unity_open_mcp_spriteatlas_delete",
            Title = "SpriteAtlas: Delete",
            IsMutating = true,
            Gate = GateMode.Enforce,
            DestructiveHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Delete a SpriteAtlas asset (.spriteatlas). Refuses when the path " +
            "does not point at a SpriteAtlas. Mutating (destructive): runs the " +
            "full gate path; paths_hint is the asset path. There is no undo " +
            "across a Unity restart.")]
        public static string Delete(
            string asset_path,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_delete is mutating; pass a non-empty paths_hint.");

            var normalized = NormalizeAtlasPath(asset_path);
            if (normalized.Error != null) return normalized.Error;

            var loaded = AssetDatabase.LoadAssetAtPath<UnityEngine.U2D.SpriteAtlas>(normalized.Path);
            if (loaded == null)
                return SpriteAtlasJson.Error("asset_not_found",
                    $"SpriteAtlas not found at '{normalized.Path}'.");

            if (!AssetDatabase.DeleteAsset(normalized.Path))
                return SpriteAtlasJson.Error("delete_failed",
                    $"AssetDatabase.DeleteAsset returned false for '{normalized.Path}'.");

            var sb = new StringBuilder(96);
            sb.Append("\"path\":").Append(SpriteAtlasJson.Esc(normalized.Path));
            sb.Append(",\"deleted\":true");
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // list (read-only)
        // =====================================================================

        // List SpriteAtlas assets under a folder (default: whole project).
        // Read-only, offline-routeable in principle.
        [BridgeTool("unity_open_mcp_spriteatlas_list",
            Title = "SpriteAtlas: List",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Read-only: list SpriteAtlas (.spriteatlas) asset paths under a " +
            "folder (omit folder to search the whole project). Each entry " +
            "reports path + name. Cap 200; truncated count reported. Gate-free.")]
        public static string List(string folder = null)
        {
            var searchFolder = string.IsNullOrWhiteSpace(folder) ? "Assets" : folder.Trim();
            var guids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { searchFolder });
            int max = 200;
            int truncated = guids.Length > max ? guids.Length - max : 0;
            int emit = System.Math.Min(guids.Length, max);

            var sb = new StringBuilder(emit * 80 + 32);
            sb.Append("\"folder\":").Append(SpriteAtlasJson.Esc(searchFolder));
            sb.Append(",\"count\":").Append(emit);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"atlases\":[");
            for (int i = 0; i < emit; i++)
            {
                if (i > 0) sb.Append(',');
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                sb.Append("{\"path\":").Append(SpriteAtlasJson.Esc(path));
                sb.Append(",\"name\":").Append(SpriteAtlasJson.Esc(name)).Append('}');
            }
            sb.Append(']');
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — asset load
        // =====================================================================

        struct AtlasAssetResult
        {
            public U2D.SpriteAtlasAsset Asset;
            public string Path;
            public string Error;
        }

        private static AtlasAssetResult LoadAtlasAsset(string assetPath)
        {
            var normalized = NormalizeAtlasPath(assetPath);
            if (normalized.Error != null)
                return new AtlasAssetResult { Error = normalized.Error };

            var asset = U2D.SpriteAtlasAsset.Load(normalized.Path);
            if (asset == null)
                return new AtlasAssetResult
                {
                    Error = SpriteAtlasJson.Error("asset_not_found",
                        $"SpriteAtlas not found at '{normalized.Path}'. Has the asset finished importing?")
                };
            return new AtlasAssetResult { Asset = asset, Path = normalized.Path };
        }

        struct PathResult
        {
            public string Path;
            public string Error;
        }

        private static PathResult NormalizeAtlasPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return new PathResult { Error = SpriteAtlasJson.Error("missing_parameter",
                    "'asset_path' is required.") };
            var normalized = assetPath.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("Assets/"))
                return new PathResult { Error = SpriteAtlasJson.Error("invalid_asset_path",
                    $"asset_path must start with 'Assets/': '{normalized}'.") };
            if (!normalized.EndsWith(".spriteatlas", System.StringComparison.OrdinalIgnoreCase))
                return new PathResult { Error = SpriteAtlasJson.Error("invalid_asset_path",
                    "asset_path must end with '.spriteatlas'.") };
            return new PathResult { Path = normalized };
        }

        // =====================================================================
        // Helpers — packable enumeration
        // =====================================================================

        struct PackableEntry
        {
            public Object Object;
            public string Path;
            public string TypeName;
        }

        // Packables are stored on the SpriteAtlasAsset under the
        // m_ImporterData.packables serialized array. Enumerate via
        // SerializedObject so we get the stable field name + ObjectReference.
        private static List<PackableEntry> GetPackables(U2D.SpriteAtlasAsset asset)
        {
            var result = new List<PackableEntry>();
            var so = new SerializedObject(asset);
            var arr = so.FindProperty("m_ImporterData.packables.Array");
            if (arr == null) return result;
            int size = arr.arraySize;
            for (int i = 0; i < size; i++)
            {
                var elem = arr.GetArrayElementAtIndex(i);
                var obj = elem.objectReferenceValue;
                if (obj == null) continue;
                result.Add(new PackableEntry
                {
                    Object = obj,
                    Path = AssetDatabase.GetAssetPath(obj),
                    TypeName = obj.GetType().Name,
                });
            }
            return result;
        }

        private static int CountPackables(U2D.SpriteAtlasAsset asset)
            => GetPackables(asset).Count;

        // =====================================================================
        // Helpers — state serialization
        // =====================================================================

        private static void BuildAtlasState(U2D.SpriteAtlasAsset asset, string path, StringBuilder sb)
        {
            // SpriteAtlasAsset's settings getters are deprecated in favor of
            // SpriteAtlasImporter (Unity 6). Resolve the importer; fall back to
            // the Asset methods on older Unity where the importer is absent.
            var importer = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.U2D.SpriteAtlasImporter;

            sb.Append("\"path\":").Append(SpriteAtlasJson.Esc(path));
            sb.Append(",\"name\":").Append(SpriteAtlasJson.Esc(asset.name));
            sb.Append(",\"isVariant\":").Append(asset.isVariant ? "true" : "false");
            sb.Append(",\"includeInBuild\":").Append(
                (importer != null ? importer.includeInBuild : asset.IsIncludeInBuild()) ? "true" : "false");

            // Packables
            var packables = GetPackables(asset);
            sb.Append(",\"packables\":[");
            for (int i = 0; i < packables.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"path\":").Append(SpriteAtlasJson.Esc(packables[i].Path));
                sb.Append(",\"type\":").Append(SpriteAtlasJson.Esc(packables[i].TypeName)).Append('}');
            }
            sb.Append(']');
            sb.Append(",\"packableCount\":").Append(packables.Count);

            // Packing
            var ps = importer != null ? importer.packingSettings : asset.GetPackingSettings();
            sb.Append(",\"packing\":{");
            sb.Append("\"blockOffset\":").Append(ps.blockOffset).Append(',');
            sb.Append("\"padding\":").Append(ps.padding).Append(',');
            sb.Append("\"enableRotation\":").Append(ps.enableRotation ? "true" : "false").Append(',');
            sb.Append("\"enableTightPacking\":").Append(ps.enableTightPacking ? "true" : "false").Append(',');
            sb.Append("\"enableAlphaDilation\":").Append(ps.enableAlphaDilation ? "true" : "false");
            sb.Append('}');

            // Texture
            var ts = importer != null ? importer.textureSettings : asset.GetTextureSettings();
            sb.Append(",\"texture\":{");
            sb.Append("\"maxTextureSize\":").Append(ts.maxTextureSize).Append(',');
            sb.Append("\"anisoLevel\":").Append(ts.anisoLevel).Append(',');
            sb.Append("\"filterMode\":").Append(SpriteAtlasJson.Esc(ts.filterMode.ToString())).Append(',');
            sb.Append("\"generateMipMaps\":").Append(ts.generateMipMaps ? "true" : "false").Append(',');
            sb.Append("\"readable\":").Append(ts.readable ? "true" : "false").Append(',');
            sb.Append("\"sRGB\":").Append(ts.sRGB ? "true" : "false");
            sb.Append('}');

            // Default platform settings
            var pls = importer != null ? importer.GetPlatformSettings("") : asset.GetPlatformSettings("");
            sb.Append(",\"platformDefault\":{");
            sb.Append("\"overridden\":").Append(pls.overridden ? "true" : "false").Append(',');
            sb.Append("\"maxTextureSize\":").Append(pls.maxTextureSize).Append(',');
            sb.Append("\"format\":").Append(SpriteAtlasJson.Esc(pls.format.ToString())).Append(',');
            sb.Append("\"textureCompression\":").Append(SpriteAtlasJson.Esc(pls.textureCompression.ToString()));
            sb.Append('}');
        }

        // =====================================================================
        // Helpers — folder creation
        // =====================================================================

        private static void EnsureFolderFor(string assetPath)
        {
            var lastSlash = assetPath.LastIndexOf('/');
            if (lastSlash <= 0) return;
            var dir = assetPath.Substring(0, lastSlash);
            if (string.IsNullOrEmpty(dir) || !dir.StartsWith("Assets")) return;
            var segments = dir.Split('/');
            var current = segments[0]; // "Assets"
            for (int i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[i]);
                current = next;
            }
        }

        // =====================================================================
        // Helpers — JSON value extraction (hand-rolled; no Newtonsoft in bridge)
        // =====================================================================

        private static void AppendError(StringBuilder errors, ref bool firstError, string path, string reason)
        {
            if (!firstError) errors.Append(',');
            firstError = false;
            errors.Append("{\"path\":").Append(SpriteAtlasJson.Esc(path));
            errors.Append(",\"reason\":").Append(SpriteAtlasJson.Esc(reason)).Append('}');
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(raw)) return false;
            var s = raw.Trim().Trim('"');
            if (s == "true") { value = true; return true; }
            if (s == "false") { value = false; return true; }
            return false;
        }

        // Extract the raw JSON value for a top-level key (string, number,
        // bool, object, or array). Returns null when absent.
        private static string ExtractValue(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            return ReadValueAt(json, colon + 1);
        }

        // Extract a top-level object value (returns the inner object body
        // including braces, or null when absent / not an object).
        private static string ExtractObject(string json, string key)
        {
            var raw = ExtractValue(json, key);
            if (string.IsNullOrEmpty(raw)) return null;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("{")) return trimmed;
            return null;
        }

        // Read a JSON value starting at/after the given index, skipping
        // whitespace. Handles strings, numbers, bools/null, objects, arrays.
        private static string ReadValueAt(string json, int start)
        {
            int i = start;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length) return null;

            char c = json[i];
            if (c == '"')
            {
                int end = i + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\' && end + 1 < json.Length) { end += 2; continue; }
                    if (json[end] == '"') break;
                    end++;
                }
                return json.Substring(i, System.Math.Min(end + 1, json.Length) - i);
            }
            if (c == '{' || c == '[')
            {
                char open = c;
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                int end = i;
                while (end < json.Length)
                {
                    if (json[end] == '"')
                    {
                        // skip string content
                        end++;
                        while (end < json.Length)
                        {
                            if (json[end] == '\\' && end + 1 < json.Length) { end += 2; continue; }
                            if (json[end] == '"') break;
                            end++;
                        }
                    }
                    else if (json[end] == open) depth++;
                    else if (json[end] == close)
                    {
                        depth--;
                        if (depth == 0) { end++; break; }
                    }
                    end++;
                }
                return json.Substring(i, end - i);
            }
            // primitive — capture to comma or }/]
            int pEnd = i;
            while (pEnd < json.Length && json[pEnd] != ',' && json[pEnd] != '}' && json[pEnd] != ']')
                pEnd++;
            return json.Substring(i, pEnd - i).Trim();
        }

        struct PatchOutcome
        {
            public bool Ok;
            public string Field;
            public string RawValue; // set on success
            public string Reason;   // set on failure
        }

        // Parse the known top-level keys of an object body and return a
        // PatchOutcome per known key (success carries the raw value; failure
        // carries a reason). Unknown keys are skipped (the caller reports the
        // applied set; the JSON body may contain unrelated fields).
        //
        // IMPORTANT: the settings structs (SpriteAtlasPackingSettings /
        // SpriteAtlasTextureSettings) are VALUE TYPES. Passing one as `object`
        // boxes it, and PropertyInfo.SetValue writes into the boxed copy — the
        // caller's original struct variable is NOT updated. We therefore return
        // the (possibly mutated) object so the caller can unbox it back.
        private static (List<PatchOutcome> outcomes, object target) PatchStruct(
            string objectBody, object target,
            Dictionary<string, string> knownFields)
        {
            var results = new List<PatchOutcome>();
            var t = target.GetType();
            foreach (var kv in knownFields)
            {
                var rawValue = ExtractValue(objectBody, kv.Key);
                if (rawValue == null) continue;

                var prop = t.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null)
                {
                    results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "property missing" });
                    continue;
                }

                try
                {
                    object converted;
                    var typeHint = kv.Value;
                    if (typeHint == "bool")
                    {
                        if (!TryParseBool(rawValue, out var b))
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid bool" });
                            continue;
                        }
                        converted = b;
                    }
                    else if (typeHint == "int")
                    {
                        var s = rawValue.Trim().Trim('"');
                        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid int" });
                            continue;
                        }
                        converted = n;
                    }
                    else if (typeHint.StartsWith("enum:"))
                    {
                        var enumName = typeHint.Substring("enum:".Length);
                        var enumType = System.Type.GetType("UnityEngine." + enumName + ", UnityEngine.CoreModule", false, true)
                                       ?? System.Type.GetType("UnityEngine." + enumName + ", UnityEngine", false, true);
                        if (enumType == null)
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "enum type missing" });
                            continue;
                        }
                        var cleaned = rawValue.Trim().Trim('"');
                        object enumValue;
                        if (System.Enum.IsDefined(enumType, cleaned))
                            enumValue = System.Enum.Parse(enumType, cleaned);
                        else if (int.TryParse(cleaned, out var idx) && System.Enum.IsDefined(enumType, idx))
                            enumValue = System.Enum.ToObject(enumType, idx);
                        else
                        {
                            results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "invalid enum value" });
                            continue;
                        }
                        converted = enumValue;
                    }
                    else
                    {
                        results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = "unsupported type hint" });
                        continue;
                    }

                    prop.SetValue(target, converted);
                    results.Add(new PatchOutcome { Ok = true, Field = kv.Key, RawValue = rawValue });
                }
                catch (System.Exception e)
                {
                    results.Add(new PatchOutcome { Ok = false, Field = kv.Key, Reason = e.Message });
                }
            }
            return (results, target);
        }

        private static bool HasAnyPackingEntry(string packingJson)
            => HasAnyKey(packingJson, "blockOffset", "padding", "enableRotation",
                         "enableTightPacking", "enableAlphaDilation");

        private static bool HasAnyTextureEntry(string textureJson)
            => HasAnyKey(textureJson, "anisoLevel", "filterMode",
                         "generateMipMaps", "readable", "sRGB");

        private static bool HasAnyKey(string json, params string[] keys)
        {
            foreach (var k in keys)
                if (json.IndexOf("\"" + k + "\"", System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
