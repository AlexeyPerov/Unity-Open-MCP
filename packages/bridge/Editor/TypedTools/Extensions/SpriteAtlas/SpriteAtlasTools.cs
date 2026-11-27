// M20 Plan 9 / T20.9.1 — 2D art pipeline: SpriteAtlas tools.
//
// Seven typed tools covering the SpriteAtlas authoring surface:
//
//   spriteatlas_create         — create a new SpriteAtlas asset at
//                                Assets/.../*.spriteatlasv2 or *.spriteatlas.
//   spriteatlas_get            — read packables + packing/texture/platform
//                                settings (read-only).
//   spriteatlas_add_packable   — add sprites/textures/folders to a SpriteAtlas.
//   spriteatlas_remove_packable— remove a packable by asset path.
//   spriteatlas_modify         — patch packing/texture/include-in-build
//                                settings (structured patch; unknown fields
//                                reported, not fatal).
//   spriteatlas_delete         — delete the atlas asset.
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
// Two file formats, chosen by the extension, each authored the way Unity
// imports it:
//   - .spriteatlasv2 (Sprite Atlas V2): the file holds the authoring
//     SpriteAtlasAsset (SpriteAtlasAsset.Load / Save, packables via Add /
//     Remove); SpriteAtlasImporter imports it and owns include-in-build and
//     the packing / texture / platform settings.
//   - .spriteatlas (Sprite Atlas V1): the file holds a SpriteAtlas, authored
//     through SpriteAtlasExtensions (packables and settings on the object).
// Which one Unity packs depends on Editor Settings > Sprite Packer Mode; the
// answers report the mode and warn when the file's format is not the one it
// packs. Packables are Object references (Sprite / Texture / DefaultAsset
// folder); we resolve them by Assets/-rooted path and report them back by path
// + type so agents can round-trip without instance ids.
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

// Some SpriteAtlasAsset / SpriteAtlasExtensions members are marked obsolete on
// newer Unity in favor of SpriteAtlasImporter; the V2 settings go through the
// importer, and the V1 format has no other API.
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
        internal const string V2Extension = ".spriteatlasv2";
        internal const string V1Extension = ".spriteatlas";

        // =====================================================================
        // create
        // =====================================================================

        // Create a new SpriteAtlas asset. The extension picks the format
        // (.spriteatlasv2 → V2, .spriteatlas → V1). Creates intermediate
        // folders. include_in_build defaults to true.
        [BridgeTool("unity_open_mcp_spriteatlas_create",
            Title = "SpriteAtlas: Create",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Create a new SpriteAtlas asset at an Assets/-rooted path. The " +
            "extension picks the format: .spriteatlasv2 (Sprite Atlas V2, " +
            "imported by SpriteAtlasImporter) or .spriteatlas (Sprite Atlas V1). " +
            "Use the one your Sprite Packer Mode packs (V2 modes: .spriteatlasv2); " +
            "the answer reports packerMode and a warning on a mismatch. " +
            "Intermediate folders are created if missing. include_in_build " +
            "defaults to true. Mutating: runs the full gate path; paths_hint is " +
            "the new asset path.")]
        public static string Create(
            string asset_path,
            bool include_in_build = true,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_create is mutating; pass a non-empty paths_hint " +
                    "scoped to the new atlas asset path.");

            var normalized = NormalizeAtlasPath(asset_path);
            if (normalized.Error != null) return normalized.Error;
            if (AssetDatabase.GetMainAssetTypeAtPath(normalized.Path) != null
                || System.IO.File.Exists(normalized.Path))
                return SpriteAtlasJson.Error("asset_exists",
                    $"An asset already exists at '{normalized.Path}'.");

            EnsureFolderFor(normalized.Path);
            var name = System.IO.Path.GetFileNameWithoutExtension(normalized.Path);

            if (normalized.V2)
            {
                // The SpriteAtlasAsset constructor binds the native object on
                // Unity 6; an older one needs Internal_Create.
                var asset = new U2D.SpriteAtlasAsset();
                if (asset == null)
                {
                    var internalCreate = typeof(U2D.SpriteAtlasAsset).GetMethod(
                        "Internal_Create",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (internalCreate == null)
                        return SpriteAtlasJson.Error("api_unavailable",
                            "SpriteAtlasAsset.Internal_Create is unavailable on this Unity version.");
                    internalCreate.Invoke(null, new object[] { asset });
                }
                asset.name = name;
                U2D.SpriteAtlasAsset.Save(asset, normalized.Path);
                // Import just the new file (a project-wide refresh would also
                // run any pending script compile and reload inside this call).
                AssetDatabase.ImportAsset(normalized.Path, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(normalized.Path) as U2D.SpriteAtlasImporter;
                if (importer == null)
                    return SpriteAtlasJson.Error("import_failed",
                        $"'{normalized.Path}' was written, but no SpriteAtlasImporter imported it.");
                if (importer.includeInBuild != include_in_build)
                {
                    importer.includeInBuild = include_in_build;
                    importer.SaveAndReimport();
                }
            }
            else
            {
                var atlas = new UnityEngine.U2D.SpriteAtlas { name = name };
                U2D.SpriteAtlasExtensions.SetIncludeInBuild(atlas, include_in_build);
                AssetDatabase.CreateAsset(atlas, normalized.Path);
                AssetDatabase.SaveAssetIfDirty(atlas);
            }

            // No instanceId: every atlas tool takes the path.
            var sb = new StringBuilder(200);
            sb.Append("\"path\":").Append(SpriteAtlasJson.Esc(normalized.Path));
            sb.Append(",\"name\":").Append(SpriteAtlasJson.Esc(name));
            sb.Append(",\"format\":").Append(SpriteAtlasJson.Esc(normalized.V2 ? "v2" : "v1"));
            sb.Append(",\"includeInBuild\":").Append(include_in_build ? "true" : "false");
            AppendPackerMode(sb, normalized.Path, normalized.V2);
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
            "Read-only: SpriteAtlas snapshot — format (v2 / v1), packables " +
            "(asset paths + type), include-in-build, is-variant, packing settings " +
            "(blockOffset / padding / enableRotation / enableTightPacking / " +
            "enableAlphaDilation), texture settings (maxTextureSize / anisoLevel / " +
            "filterMode / generateMipMaps / readable / sRGB), and the default " +
            "platform settings (maxTextureSize / format / textureCompression). " +
            "Gate-free.")]
        public static string Get(string asset_path)
        {
            var atlas = LoadAtlas(asset_path);
            if (atlas.Error != null) return atlas.Error;
            var sb = new StringBuilder(512);
            BuildAtlasState(atlas, sb);
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
            "paths_hint is the atlas asset path.")]
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

            var atlas = LoadAtlas(asset_path);
            if (atlas.Error != null) return atlas.Error;

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
                if (atlas.V2)
                {
                    atlas.Authoring.Add(toAdd.ToArray());
                    SaveAuthoring(atlas);
                }
                else
                {
                    U2D.SpriteAtlasExtensions.Add(atlas.Legacy, toAdd.ToArray());
                    SaveLegacy(atlas);
                }
            }

            var sb = new StringBuilder(320);
            sb.Append("\"added\":").Append(added);
            sb.Append(",\"errors\":").Append(errors);
            sb.Append(",\"packableCount:after\":").Append(GetPackables(atlas).Count);
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
            "the full gate path; paths_hint is the atlas asset path.")]
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

            var atlas = LoadAtlas(asset_path);
            if (atlas.Error != null) return atlas.Error;

            var current = GetPackables(atlas);
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
                if (atlas.V2)
                {
                    atlas.Authoring.Remove(toRemove.ToArray());
                    SaveAuthoring(atlas);
                }
                else
                {
                    U2D.SpriteAtlasExtensions.Remove(atlas.Legacy, toRemove.ToArray());
                    SaveLegacy(atlas);
                }
            }

            var sb = new StringBuilder(320);
            sb.Append("\"removed\":").Append(removed);
            sb.Append(",\"errors\":").Append(errors);
            sb.Append(",\"packableCount:after\":").Append(GetPackables(atlas).Count);
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
            "are reported in `unknownFields`, not fatal. The settings are saved " +
            "with the atlas (a V2 atlas: its SpriteAtlasImporter; a V1 atlas: " +
            "the SpriteAtlas object) and take effect at the next pack. " +
            "Mutating: runs the full gate path; paths_hint is the atlas asset path.")]
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

            var atlas = LoadAtlas(asset_path);
            if (atlas.Error != null) return atlas.Error;

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
                    if (atlas.V2) atlas.Importer.includeInBuild = b;
                    else U2D.SpriteAtlasExtensions.SetIncludeInBuild(atlas.Legacy, b);
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
                var packing = atlas.V2
                    ? atlas.Importer.packingSettings
                    : U2D.SpriteAtlasExtensions.GetPackingSettings(atlas.Legacy);
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
                packing = (U2D.SpriteAtlasPackingSettings)packingBoxed;
                AppendOutcomes(packingOutcomes, "packing.", applied, ref firstApplied, unknown, ref firstUnknown);
                if (HasAnyPackingEntry(packingJson))
                {
                    if (atlas.V2) atlas.Importer.packingSettings = packing;
                    else U2D.SpriteAtlasExtensions.SetPackingSettings(atlas.Legacy, packing);
                }
            }

            // texture
            var textureJson = ExtractObject(settings_json, "texture");
            if (!string.IsNullOrEmpty(textureJson))
            {
                var texture = atlas.V2
                    ? atlas.Importer.textureSettings
                    : U2D.SpriteAtlasExtensions.GetTextureSettings(atlas.Legacy);
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
                texture = (U2D.SpriteAtlasTextureSettings)textureBoxed;
                AppendOutcomes(textureOutcomes, "texture.", applied, ref firstApplied, unknown, ref firstUnknown);
                if (HasAnyTextureEntry(textureJson))
                {
                    if (atlas.V2) atlas.Importer.textureSettings = texture;
                    else U2D.SpriteAtlasExtensions.SetTextureSettings(atlas.Legacy, texture);
                }
            }

            applied.Append(']');
            unknown.Append(']');

            // A V2 atlas keeps its settings on the importer; a V1 atlas on the
            // SpriteAtlas object in the file.
            if (atlas.V2) atlas.Importer.SaveAndReimport();
            else SaveLegacy(atlas);

            var sb = new StringBuilder(360);
            sb.Append("\"applied\":").Append(applied);
            sb.Append(",\"unknownFields\":").Append(unknown);
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        private static void AppendOutcomes(List<PatchOutcome> outcomes, string prefix,
            StringBuilder applied, ref bool firstApplied, StringBuilder unknown, ref bool firstUnknown)
        {
            foreach (var entry in outcomes)
            {
                if (entry.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append("{\"field\":").Append(SpriteAtlasJson.Esc(prefix + entry.Field));
                    applied.Append(",\"value\":").Append(entry.RawValue).Append('}');
                }
                else
                {
                    if (!firstUnknown) unknown.Append(',');
                    firstUnknown = false;
                    unknown.Append("{\"field\":").Append(SpriteAtlasJson.Esc(prefix + entry.Field));
                    unknown.Append(",\"reason\":").Append(SpriteAtlasJson.Esc(entry.Reason)).Append('}');
                }
            }
        }

        // =====================================================================
        // delete
        // =====================================================================

        // Delete the atlas asset. Refuses if the path does not point at a
        // SpriteAtlas.
        [BridgeTool("unity_open_mcp_spriteatlas_delete",
            Title = "SpriteAtlas: Delete",
            IsMutating = true,
            Gate = GateMode.Enforce,
            DestructiveHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "sprite2d")]
        [System.ComponentModel.Description(
            "Delete a SpriteAtlas asset (.spriteatlasv2 / .spriteatlas). Refuses " +
            "when the path does not point at a SpriteAtlas. Mutating " +
            "(destructive): runs the full gate path; paths_hint is the asset " +
            "path. There is no undo across a Unity restart.")]
        public static string Delete(
            string asset_path,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return SpriteAtlasJson.Error("paths_hint_required",
                    "spriteatlas_delete is mutating; pass a non-empty paths_hint.");

            // Any atlas file, including a .spriteatlas that holds a
            // SpriteAtlasAsset (neither format, so LoadAtlas refuses it).
            var normalized = NormalizeAtlasPath(asset_path);
            if (normalized.Error != null) return normalized.Error;
            var mainType = AssetDatabase.GetMainAssetTypeAtPath(normalized.Path);
            if (mainType == null)
                return SpriteAtlasJson.Error("asset_not_found",
                    $"SpriteAtlas not found at '{normalized.Path}'.");
            if (mainType != typeof(UnityEngine.U2D.SpriteAtlas) && mainType != typeof(U2D.SpriteAtlasAsset))
                return SpriteAtlasJson.Error("not_a_sprite_atlas",
                    $"'{normalized.Path}' is a {mainType.Name}, not a SpriteAtlas.");

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
            "Read-only: list SpriteAtlas asset paths (.spriteatlasv2 and " +
            ".spriteatlas) under a folder (omit folder to search the whole " +
            "project). Each entry reports path + name + format (v2 / v1). Cap " +
            "200; truncated count reported. Gate-free.")]
        public static string List(string folder = null)
        {
            var searchFolder = string.IsNullOrWhiteSpace(folder) ? "Assets" : folder.Trim();
            // Match the file extensions: both formats import as SpriteAtlas.
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { searchFolder }))
            {
                var candidate = AssetDatabase.GUIDToAssetPath(guid);
                if ((candidate.EndsWith(V2Extension, System.StringComparison.OrdinalIgnoreCase)
                        || candidate.EndsWith(V1Extension, System.StringComparison.OrdinalIgnoreCase))
                    && !paths.Contains(candidate))
                    paths.Add(candidate);
            }
            paths.Sort(System.StringComparer.Ordinal);
            int max = 200;
            int truncated = paths.Count > max ? paths.Count - max : 0;
            int emit = System.Math.Min(paths.Count, max);

            var sb = new StringBuilder(emit * 96 + 32);
            sb.Append("\"folder\":").Append(SpriteAtlasJson.Esc(searchFolder));
            sb.Append(",\"count\":").Append(emit);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"atlases\":[");
            for (int i = 0; i < emit; i++)
            {
                if (i > 0) sb.Append(',');
                var path = paths[i];
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                sb.Append("{\"path\":").Append(SpriteAtlasJson.Esc(path));
                sb.Append(",\"name\":").Append(SpriteAtlasJson.Esc(name));
                sb.Append(",\"format\":").Append(SpriteAtlasJson.Esc(
                    path.EndsWith(V2Extension, System.StringComparison.OrdinalIgnoreCase) ? "v2" : "v1")).Append('}');
            }
            sb.Append(']');
            return SpriteAtlasJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Helpers — atlas load / save
        // =====================================================================

        // One atlas in either format: V2 (authoring SpriteAtlasAsset + its
        // importer) or V1 (the SpriteAtlas object in the file).
        sealed class Atlas
        {
            public string Path;
            public bool V2;
            public U2D.SpriteAtlasAsset Authoring;
            public U2D.SpriteAtlasImporter Importer;
            public UnityEngine.U2D.SpriteAtlas Legacy;
            public string Error;
        }

        private static Atlas LoadAtlas(string assetPath)
        {
            var normalized = NormalizeAtlasPath(assetPath);
            if (normalized.Error != null) return new Atlas { Error = normalized.Error };
            var path = normalized.Path;
            if (AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                return new Atlas
                {
                    Error = SpriteAtlasJson.Error("asset_not_found",
                        $"SpriteAtlas not found at '{path}'. Has the asset finished importing?")
                };

            if (normalized.V2)
            {
                var authoring = U2D.SpriteAtlasAsset.Load(path);
                var importer = AssetImporter.GetAtPath(path) as U2D.SpriteAtlasImporter;
                if (authoring == null || importer == null)
                    return new Atlas
                    {
                        Error = SpriteAtlasJson.Error("not_a_sprite_atlas",
                            $"'{path}' is not a Sprite Atlas V2 asset imported by SpriteAtlasImporter.")
                    };
                return new Atlas { Path = path, V2 = true, Authoring = authoring, Importer = importer };
            }

            var legacy = AssetDatabase.LoadAssetAtPath<UnityEngine.U2D.SpriteAtlas>(path);
            if (legacy == null)
                return new Atlas
                {
                    Error = SpriteAtlasJson.Error("not_a_sprite_atlas",
                        $"'{path}' holds no SpriteAtlas (main asset: {AssetDatabase.GetMainAssetTypeAtPath(path)?.Name}). " +
                        "A .spriteatlas written as a SpriteAtlasAsset is neither format: delete it " +
                        "(spriteatlas_delete) and create the atlas again (.spriteatlasv2 for Sprite Atlas V2).")
                };
            return new Atlas { Path = path, V2 = false, Legacy = legacy };
        }

        private static void SaveAuthoring(Atlas atlas)
        {
            U2D.SpriteAtlasAsset.Save(atlas.Authoring, atlas.Path);
            AssetDatabase.ImportAsset(atlas.Path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static void SaveLegacy(Atlas atlas)
        {
            EditorUtility.SetDirty(atlas.Legacy);
            AssetDatabase.SaveAssetIfDirty(atlas.Legacy);
        }

        struct PathResult
        {
            public string Path;
            public bool V2;
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
            if (normalized.EndsWith(V2Extension, System.StringComparison.OrdinalIgnoreCase))
                return new PathResult { Path = normalized, V2 = true };
            if (normalized.EndsWith(V1Extension, System.StringComparison.OrdinalIgnoreCase))
                return new PathResult { Path = normalized, V2 = false };
            return new PathResult { Error = SpriteAtlasJson.Error("invalid_asset_path",
                "asset_path must end with '.spriteatlasv2' (Sprite Atlas V2) or '.spriteatlas' (V1).") };
        }

        // Sprite Packer Mode values (EditorSettings.spritePackerMode): 0
        // Disabled, 1/2 the retired legacy packer, 3/4 Sprite Atlas V1, 5/6
        // Sprite Atlas V2. Compared as ints so no obsolete member is named.
        internal static bool IsV2Mode(int mode) => mode >= 5;
        internal static bool IsV1Mode(int mode) => mode == 3 || mode == 4;

        // packerMode + a warning when the atlas's format is not the one the
        // mode packs (null when it is, or when nothing is packed at all).
        public static string FormatWarning(int mode, bool v2, string path)
        {
            if (IsV2Mode(mode) && !v2)
                return "Sprite Atlas V2 is enabled (Sprite Packer Mode), and it packs .spriteatlasv2 atlases, not this " +
                       ".spriteatlas. Create '" + path + "v2' instead.";
            if (IsV1Mode(mode) && v2)
                return "Sprite Packer Mode is a Sprite Atlas V1 mode, which does not pack this .spriteatlasv2. Switch " +
                       "Edit > Project Settings > Editor > Sprite Packer Mode to Sprite Atlas V2, or use a .spriteatlas.";
            return null;
        }

        private static void AppendPackerMode(StringBuilder sb, string path, bool v2)
        {
            var mode = EditorSettings.spritePackerMode;
            sb.Append(",\"packerMode\":").Append(SpriteAtlasJson.Esc(mode.ToString()));
            var warning = FormatWarning((int)mode, v2, path);
            if (warning != null) sb.Append(",\"warning\":").Append(SpriteAtlasJson.Esc(warning));
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

        // V2: the SpriteAtlasAsset's m_ImporterData.packables serialized array
        // (no public getter). V1: SpriteAtlasExtensions.GetPackables.
        private static List<PackableEntry> GetPackables(Atlas atlas)
        {
            var result = new List<PackableEntry>();
            void Add(Object obj)
            {
                if (obj == null) return;
                result.Add(new PackableEntry
                {
                    Object = obj,
                    Path = AssetDatabase.GetAssetPath(obj),
                    TypeName = obj.GetType().Name,
                });
            }
            if (!atlas.V2)
            {
                foreach (var obj in U2D.SpriteAtlasExtensions.GetPackables(atlas.Legacy)) Add(obj);
                return result;
            }
            // SerializedObject is IDisposable — scope it with `using` so the
            // native SerializedFile handle is freed after the enumeration.
            using (var so = new SerializedObject(atlas.Authoring))
            {
                var arr = so.FindProperty("m_ImporterData.packables.Array");
                if (arr == null) return result;
                int size = arr.arraySize;
                for (int i = 0; i < size; i++)
                    Add(arr.GetArrayElementAtIndex(i).objectReferenceValue);
            }
            return result;
        }

        // =====================================================================
        // Helpers — state serialization
        // =====================================================================

        private static void BuildAtlasState(Atlas atlas, StringBuilder sb)
        {
            var v2 = atlas.V2;
            sb.Append("\"path\":").Append(SpriteAtlasJson.Esc(atlas.Path));
            sb.Append(",\"name\":").Append(SpriteAtlasJson.Esc(v2 ? atlas.Authoring.name : atlas.Legacy.name));
            sb.Append(",\"format\":").Append(SpriteAtlasJson.Esc(v2 ? "v2" : "v1"));
            AppendPackerMode(sb, atlas.Path, v2);
            sb.Append(",\"isVariant\":").Append((v2 ? atlas.Authoring.isVariant : atlas.Legacy.isVariant) ? "true" : "false");
            sb.Append(",\"includeInBuild\":").Append(
                (v2 ? atlas.Importer.includeInBuild : U2D.SpriteAtlasExtensions.IsIncludeInBuild(atlas.Legacy)) ? "true" : "false");

            // Packables
            var packables = GetPackables(atlas);
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
            var ps = v2 ? atlas.Importer.packingSettings : U2D.SpriteAtlasExtensions.GetPackingSettings(atlas.Legacy);
            sb.Append(",\"packing\":{");
            sb.Append("\"blockOffset\":").Append(ps.blockOffset).Append(',');
            sb.Append("\"padding\":").Append(ps.padding).Append(',');
            sb.Append("\"enableRotation\":").Append(ps.enableRotation ? "true" : "false").Append(',');
            sb.Append("\"enableTightPacking\":").Append(ps.enableTightPacking ? "true" : "false").Append(',');
            sb.Append("\"enableAlphaDilation\":").Append(ps.enableAlphaDilation ? "true" : "false");
            sb.Append('}');

            // Texture
            var ts = v2 ? atlas.Importer.textureSettings : U2D.SpriteAtlasExtensions.GetTextureSettings(atlas.Legacy);
            sb.Append(",\"texture\":{");
            sb.Append("\"maxTextureSize\":").Append(ts.maxTextureSize).Append(',');
            sb.Append("\"anisoLevel\":").Append(ts.anisoLevel).Append(',');
            sb.Append("\"filterMode\":").Append(SpriteAtlasJson.Esc(ts.filterMode.ToString())).Append(',');
            sb.Append("\"generateMipMaps\":").Append(ts.generateMipMaps ? "true" : "false").Append(',');
            sb.Append("\"readable\":").Append(ts.readable ? "true" : "false").Append(',');
            sb.Append("\"sRGB\":").Append(ts.sRGB ? "true" : "false");
            sb.Append('}');

            // Default platform settings
            var pls = v2
                ? atlas.Importer.GetPlatformSettings("DefaultTexturePlatform")
                : U2D.SpriteAtlasExtensions.GetPlatformSettings(atlas.Legacy, "DefaultTexturePlatform");
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
