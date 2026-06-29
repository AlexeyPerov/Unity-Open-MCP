using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityOpenMcpBridge;

namespace UnityOpenMcpExtensions.InputSystem
{
    // M16 Plan 10 — shared helpers for the Input System extension pack.
    //
    // Asset load / save + JSON envelope builders. The Input System serializes
    // InputActionAsset as JSON inside a `.inputactions` file — every mutator
    // ends with File.WriteAllText(asset.ToJson()) + AssetDatabase.ImportAsset
    // so the on-disk file reflects the in-memory state (SaveAsset pattern).
    //
    // Naming: tool ids follow `unity_open_mcp_inputsystem_<action>` (snake_case
    // domain prefix).
    static class InputSystemJson
    {
        public const string AssetExtension = ".inputactions";

        public static string Ok(string body)
            => "{\"status\":\"ok\"," + (body ?? "") + "}";

        public static string Error(string code, string message)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
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
            sb.Append('"');
            return sb.ToString();
        }

        // Normalize an asset path: forward slashes only.
        public static string Normalize(string path)
            => string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

        // Validate an 'Assets/'-rooted .inputactions path. Returns the
        // normalized path or null + sets outError when invalid. We return
        // instead of throwing so the typed tool can surface a structured
        // JSON error instead of an execution_error envelope.
        public static bool ValidateAssetPath(string assetPath, out string normalized, out string error)
        {
            normalized = null;
            error = null;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = "'asset_path' is required and must end in '.inputactions'.";
                return false;
            }
            normalized = Normalize(assetPath);
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                !normalized.EndsWith(AssetExtension, StringComparison.OrdinalIgnoreCase))
            {
                error = "'asset_path' must be an 'Assets/'-rooted path ending in '.inputactions' " +
                        "(e.g. 'Assets/Input/Player.inputactions').";
                return false;
            }
            return true;
        }

        // Load an existing InputActionAsset from disk. Returns null + sets
        // outError when the asset does not exist (or path is invalid).
        public static InputActionAsset LoadAsset(string assetPath, out string error)
        {
            error = null;
            if (!ValidateAssetPath(assetPath, out var normalized, out var pathError))
            {
                error = pathError;
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(normalized);
            if (asset == null)
            {
                error = $"No InputActionAsset at '{normalized}'. Create it with inputsystem_asset_create first.";
                return null;
            }
            return asset;
        }

        // Persist the in-memory InputActionAsset back to its `.inputactions`
        // file as JSON and re-import so AssetDatabase reflects the change.
        public static void SaveAsset(InputActionAsset asset)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path))
                throw new Exception("InputActionAsset is not backed by a file on disk; cannot save.");

            File.WriteAllText(path, ToSafeJson(asset));
            EditorUtility.SetDirty(asset);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
        }

        // Serialize an InputActionAsset to JSON, guarding the InputSystem 1.x
        // bug where ToJson() throws inside WriteFileJson.FromMaps when the asset
        // has no ActionMaps (its internal map array is null). Emits minimal
        // valid JSON in that case.
        //
        // Note: actionMaps is a ReadOnlyArray<InputActionMap> (a struct), so it
        // cannot be compared to null with == — check Count instead. The wrapper
        // returns an empty ReadOnlyArray when the backing field is null.
        private static string ToSafeJson(InputActionAsset asset)
        {
            if (asset.actionMaps.Count == 0)
                return "{\n    \"name\": \"" + asset.name + "\",\n    \"maps\": [],\n    \"controlSchemes\": []\n}";
            return asset.ToJson();
        }
    }
}
