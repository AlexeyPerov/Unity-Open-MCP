#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityOpenMcpBridge.MetaTools;
using UnityEditor;
using UnityEngine;

namespace UnityOpenMcpBridge.TypedTools
{
    // M20 Plan 5 / T20.5.2 — typed Assembly Definition (.asmdef) tools. Four
    // tools cover the .asmdef lifecycle:
    //
    //   - asmdef_list    (read-only)  enumerate .asmdef assets under a folder.
    //   - asmdef_get     (read-only)  parse one .asmdef into a full model.
    //   - asmdef_create  (mutating)   write a new .asmdef JSON + force reimport.
    //   - asmdef_modify  (mutating)   edit references / platforms / settings on
    //                                 an existing .asmdef.
    //
    // Advantage: create / modify use the RestartThenSettle lifecycle (creating
    // or editing an asmdef triggers a domain reload + recompile — the gate
    // waits for the settle window before the next mutation, and a verify
    // scan_paths can run after to catch broken references). An ungated asmdef
    // mutator triggers the recompile without that settle wait.
    //
    // JSON handling: .asmdef is JSON, but JsonUtility cannot parse it (nested
    // arrays), and the bridge has no Newtonsoft dependency (see AGENTS.md
    // §Transport). We parse / serialize with a tiny hand-rolled reader that
    // understands the asmdef schema (string + string[] fields) plus a tolerant
    // writer that re-emits the standard Unity-shaped object. Unknown keys are
    // preserved on read so a round-trip never silently drops user-authored
    // fields (e.g. versionDefines, optionalUnityReferences).
    //
    // NOT registry-discovered: wired into BridgeHttpServer.DispatchTool
    // alongside the other typed tools so the snake_case schemas parse the same
    // way.
    public static class AssemblyDefinitionTools
    {
        // ============================== List ===============================

        // Enumerate every .asmdef asset under `folder` (default Assets). When
        // include_packages is true (default false), also walks Packages/ since
        // package asmdefs are read-only and noisy. Read-only, gate-free. Returns
        // name, asset path, reference count, include/exclude platform counts,
        // and define-constraint count per asmdef — a summary, not the full model
        // (use asmdef_get for the full parsed object).
        public static ToolDispatchResult List(string body)
        {
            var folder = JsonBody.GetString(body, "folder");
            if (string.IsNullOrWhiteSpace(folder)) folder = "Assets";

            bool includePackages = JsonBody.GetBool(body, "include_packages", false);
            int maxResults = ClampPositive(JsonBody.GetInt(body, "max_results", 200));

            var folders = new List<string> { folder };
            if (includePackages && !folders.Contains("Packages")) folders.Add("Packages");

            var guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", folders.ToArray());

            var sb = new StringBuilder(4096);
            sb.Append("{\"folder\":\"").Append(OutputSerializer.EscapeJsonString(folder));
            sb.Append("\",\"includePackages\":").Append(includePackages ? "true" : "false");
            sb.Append(",\"asmdefs\":[");

            int emitted = 0;
            int truncated = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                if (emitted >= maxResults) { truncated++; continue; }
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;

                AsmdefModel model = null;
                string parseError = null;
                try
                {
                    var json = File.ReadAllText(PathToAbsolute(path));
                    model = AsmdefJson.Parse(json);
                }
                catch (Exception e) { parseError = e.Message; }

                if (emitted > 0) sb.Append(',');
                sb.Append("{\"assetPath\":\"").Append(OutputSerializer.EscapeJsonString(path));
                sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(
                    model != null && !string.IsNullOrEmpty(model.Name)
                        ? model.Name
                        : Path.GetFileNameWithoutExtension(path)));
                sb.Append("\",\"referenceCount\":").Append(model?.References?.Count ?? 0);
                sb.Append(",\"includePlatformCount\":").Append(model?.IncludePlatforms?.Count ?? 0);
                sb.Append(",\"excludePlatformCount\":").Append(model?.ExcludePlatforms?.Count ?? 0);
                sb.Append(",\"defineConstraintCount\":").Append(model?.DefineConstraints?.Count ?? 0);
                sb.Append(",\"autoReferenced\":").Append(model?.AutoReferenced ?? true);
                if (parseError != null)
                {
                    sb.Append(",\"parseError\":\"").Append(OutputSerializer.EscapeJsonString(parseError)).Append('"');
                }
                sb.Append('}');
                emitted++;
            }
            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"maxResults\":").Append(maxResults);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // =============================== Get ===============================

        // Parse one .asmdef into a full model and return it as JSON. Read-only,
        // gate-free. Offline-routeable in principle — the .asmdef is plain JSON
        // and the offline index can parse it without a live Editor (noted in the
        // tool description); the live path here is the authoritative reader.
        public static ToolDispatchResult Get(string body)
        {
            var resolved = ResolveAsmdefPath(body, out var pathError);
            if (resolved == null) return pathError;

            AsmdefModel model;
            try
            {
                var json = File.ReadAllText(PathToAbsolute(resolved));
                model = AsmdefJson.Parse(json);
            }
            catch (FileNotFoundException)
            {
                return ToolDispatchResult.Fail("asmdef_not_found",
                    $"No .asmdef file at '{resolved}'.");
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("parse_error",
                    $"Failed to parse '{resolved}': {e.Message}");
            }

            return ToolDispatchResult.Ok(model.ToJson());
        }

        // ============================== Create =============================

        // Write a new .asmdef. Mutating: RestartThenSettle (a new asmdef forces
        // a domain reload + recompile). The caller scopes paths_hint to the new
        // asset path. Builds the JSON from the typed params (name, references,
        // platforms, define constraints, root namespace, unsafe / auto-ref /
        // no-engine-ref flags) and writes it via File.WriteAllText + a forced
        // AssetDatabase.ImportAsset so Unity picks up the recompile immediately.
        public static ToolDispatchResult Create(string body)
        {
            var resolved = ResolveAsmdefPath(body, out var pathError);
            if (resolved == null) return pathError;

            if (File.Exists(PathToAbsolute(resolved)))
                return ToolDispatchResult.Fail("asmdef_exists",
                    $"An .asmdef already exists at '{resolved}'. Use asmdef_modify to edit it, " +
                    "or choose a different asset_path.");

            var name = JsonBody.GetString(body, "name");
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(resolved);

            var model = ReadModelFromBody(body);
            model.Name = name;

            return WriteAsmdef(resolved, model, isCreate: true);
        }

        // ============================== Modify =============================

        // Edit an existing .asmdef. Mutating: RestartThenSettle (editing
        // references / platforms / define constraints can force a recompile).
        // Loads the current JSON, applies the supplied params (add/remove
        // references, set include/exclude platforms, set define constraints,
        // toggle settings), and writes it back. Only supplied params mutate —
        // omitted params keep their current value.
        public static ToolDispatchResult Modify(string body)
        {
            var resolved = ResolveAsmdefPath(body, out var pathError);
            if (resolved == null) return pathError;

            if (!File.Exists(PathToAbsolute(resolved)))
                return ToolDispatchResult.Fail("asmdef_not_found",
                    $"No .asmdef file at '{resolved}'.");

            AsmdefModel model;
            try
            {
                var json = File.ReadAllText(PathToAbsolute(resolved));
                model = AsmdefJson.Parse(json);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("parse_error",
                    $"Failed to parse '{resolved}': {e.Message}");
            }

            // Optional name override.
            var newName = JsonBody.GetString(body, "name");
            if (!string.IsNullOrWhiteSpace(newName)) model.Name = newName;

            // References — additive. add_references merges in (dedup, keeps
            // order); remove_references filters them out.
            var addRefs = JsonBody.GetStringArray(body, "add_references");
            var removeRefs = JsonBody.GetStringArray(body, "remove_references");
            if (model.References == null) model.References = new List<string>();
            if (addRefs != null)
            {
                foreach (var r in addRefs)
                    if (!string.IsNullOrEmpty(r) && !model.References.Contains(r))
                        model.References.Add(r);
            }
            if (removeRefs != null)
            {
                foreach (var r in removeRefs)
                    model.References.RemoveAll(x => x == r);
            }

            // Optional full reference-list replacement.
            var setRefs = JsonBody.GetStringArray(body, "references");
            if (setRefs != null) model.References = new List<string>(setRefs);

            // Platforms — setting include clears exclude and vice versa.
            var includePlatforms = JsonBody.GetStringArray(body, "include_platforms");
            var excludePlatforms = JsonBody.GetStringArray(body, "exclude_platforms");
            if (includePlatforms != null)
            {
                model.IncludePlatforms = new List<string>(includePlatforms);
                model.ExcludePlatforms = new List<string>();
            }
            if (excludePlatforms != null)
            {
                model.ExcludePlatforms = new List<string>(excludePlatforms);
                model.IncludePlatforms = new List<string>();
            }

            // Define constraints — optional full replacement.
            var defines = JsonBody.GetStringArray(body, "define_constraints");
            if (defines != null) model.DefineConstraints = new List<string>(defines);

            ApplySettingOverrides(body, model);

            return WriteAsmdef(resolved, model, isCreate: false);
        }

        // ----------------------------- helpers -----------------------------

        // Read the full typed model from the request body. Used by create; the
        // modify path loads-then-merges instead. Null arrays become empty lists
        // so the writer emits clean `[]` JSON.
        private static AsmdefModel ReadModelFromBody(string body)
        {
            var model = new AsmdefModel();
            var refs = JsonBody.GetStringArray(body, "references");
            model.References = refs != null ? new List<string>(refs) : new List<string>();
            var include = JsonBody.GetStringArray(body, "include_platforms");
            model.IncludePlatforms = include != null ? new List<string>(include) : new List<string>();
            var exclude = JsonBody.GetStringArray(body, "exclude_platforms");
            model.ExcludePlatforms = exclude != null ? new List<string>(exclude) : new List<string>();
            var defines = JsonBody.GetStringArray(body, "define_constraints");
            model.DefineConstraints = defines != null ? new List<string>(defines) : new List<string>();
            var precompiled = JsonBody.GetStringArray(body, "precompiled_references");
            model.PrecompiledReferences = precompiled != null ? new List<string>(precompiled) : new List<string>();
            var optionalUnity = JsonBody.GetStringArray(body, "optional_unity_references");
            model.OptionalUnityReferences = optionalUnity != null ? new List<string>(optionalUnity) : new List<string>();
            ApplySettingOverrides(body, model);
            return model;
        }

        // Apply the scalar boolean / string settings that both create and modify
        // share. JsonBody.GetBool returns the model default when the key is
        // absent, so callers that omit a flag keep the current (modify) or
        // Unity-default (create) value.
        private static void ApplySettingOverrides(string body, AsmdefModel model)
        {
            // Only override when the key is actually present, so modify doesn't
            // clobber an existing value with the default. GetRawValue returns
            // null for an absent key.
            if (JsonBody.GetRawValue(body, "root_namespace") != null)
                model.RootNamespace = JsonBody.GetString(body, "root_namespace") ?? "";
            if (JsonBody.GetRawValue(body, "allow_unsafe") != null)
                model.AllowUnsafeCode = JsonBody.GetBool(body, "allow_unsafe", false);
            if (JsonBody.GetRawValue(body, "auto_referenced") != null)
                model.AutoReferenced = JsonBody.GetBool(body, "auto_referenced", true);
            if (JsonBody.GetRawValue(body, "no_engine_references") != null)
                model.NoEngineReferences = JsonBody.GetBool(body, "no_engine_references", false);
            if (JsonBody.GetRawValue(body, "override_references") != null)
                model.OverrideReferences = JsonBody.GetBool(body, "override_references", false);
        }

        // Write the model to disk and force Unity to reimport (recompile). The
        // gate's RestartThenSettle settle window covers the domain reload that
        // follows. Returns the standard ok envelope with the asset path + a
        // recompile note.
        private static ToolDispatchResult WriteAsmdef(string assetPath, AsmdefModel model, bool isCreate)
        {
            var json = AsmdefJson.Serialize(model);

            try
            {
                var absoluteDir = Path.GetDirectoryName(PathToAbsolute(assetPath));
                if (!string.IsNullOrEmpty(absoluteDir) && !Directory.Exists(absoluteDir))
                    Directory.CreateDirectory(absoluteDir);
                File.WriteAllText(PathToAbsolute(assetPath), json);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("write_failed",
                    $"Failed to write '{assetPath}': {e.Message}");
            }

            // Force the reimport so Unity recompiles immediately; the settle
            // window (RestartThenSettle) blocks the dispatcher until the
            // recompile finishes.
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"assetPath\":\"").Append(OutputSerializer.EscapeJsonString(assetPath));
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(model.Name));
            sb.Append("\",\"action\":").Append(isCreate ? "\"create\"" : "\"modify\"");
            sb.Append(",\"referenceCount\":").Append(model.References.Count);
            sb.Append(",\"includePlatformCount\":").Append(model.IncludePlatforms.Count);
            sb.Append(",\"excludePlatformCount\":").Append(model.ExcludePlatforms.Count);
            sb.Append(",\"defineConstraintCount\":").Append(model.DefineConstraints.Count);
            sb.Append(",\"note\":\"ImportAsset queued; a recompile / domain reload may follow. \"")
              .Append("Poll editor_status / compile_check to confirm.\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Validate the asset_path for an asmdef op: required, Assets/-rooted,
        // .asmdef extension, no parent-traversal escape. Returns the normalized
        // forward-slash path or null (+ an error result) when invalid.
        private static string ResolveAsmdefPath(string body, out ToolDispatchResult error)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = ToolDispatchResult.Fail("missing_parameter",
                    "'asset_path' is required and must start with 'Assets/' and end with '.asmdef'.");
                return null;
            }
            var normalized = assetPath.Replace('\\', '/').Trim('/');
            if (!normalized.StartsWith("Assets/"))
            {
                error = ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must start with 'Assets/': '{normalized}'.");
                return null;
            }
            if (!normalized.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must end with '.asmdef': '{normalized}'.");
                return null;
            }
            if (normalized.Contains(".."))
            {
                error = ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must not contain '..': '{normalized}'.");
                return null;
            }
            error = null;
            return normalized;
        }

        // Convert an Assets/-rooted path to an absolute OS path under the
        // project root. Mirrors ReflectionScriptsObjectsTools.ResolveScriptPath
        // but for arbitrary asset paths (the .asmdef lives under Assets/).
        private static string PathToAbsolute(string assetPath)
        {
            var normalized = assetPath.Replace('\\', '/').TrimStart('/');
            var projectRoot = Application.dataPath != null
                ? Directory.GetParent(Application.dataPath)?.FullName
                : null;
            if (string.IsNullOrEmpty(projectRoot)) return assetPath;
            return Path.GetFullPath(Path.Combine(projectRoot, normalized));
        }

        private static int ClampPositive(int n) => n < 1 ? 1 : n;
    }

    // -------------------------------------------------------------------------
    // AsmdefModel + AsmdefJson — the parsed .asmdef object and a tolerant
    // hand-rolled JSON reader/writer for it. Unity serializes .asmdef with a
    // stable, well-known key set; we model the documented fields explicitly and
    // stash unknown keys in Extra so a round-trip never drops user-authored
    // entries (versionDefines, optionalUnityReferences, etc.).
    // -------------------------------------------------------------------------

    internal sealed class AsmdefModel
    {
        public string Name { get; set; } = "";
        public string RootNamespace { get; set; } = "";
        public List<string> References { get; set; } = new List<string>();
        public List<string> IncludePlatforms { get; set; } = new List<string>();
        public List<string> ExcludePlatforms { get; set; } = new List<string>();
        public bool AllowUnsafeCode { get; set; } = false;
        public bool OverrideReferences { get; set; } = false;
        public List<string> PrecompiledReferences { get; set; } = new List<string>();
        public bool AutoReferenced { get; set; } = true;
        public List<string> DefineConstraints { get; set; } = new List<string>();
        public bool NoEngineReferences { get; set; } = false;
        // versionDefines is a nested array of objects; we preserve it verbatim
        // (the raw JSON text) so round-trips never lose user-authored entries.
        public string VersionDefinesRaw { get; set; }
        public List<string> OptionalUnityReferences { get; set; } = new List<string>();
        // Unknown top-level keys preserved verbatim (key → raw JSON value).
        public Dictionary<string, string> Extra { get; } = new Dictionary<string, string>();

        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"name\":\"").Append(OutputSerializer.EscapeJsonString(Name)).Append('"');
            sb.Append(",\"rootNamespace\":\"").Append(OutputSerializer.EscapeJsonString(RootNamespace ?? "")).Append('"');
            sb.Append(",\"references\":");
            AppendStringArray(sb, References);
            sb.Append(",\"includePlatforms\":");
            AppendStringArray(sb, IncludePlatforms);
            sb.Append(",\"excludePlatforms\":");
            AppendStringArray(sb, ExcludePlatforms);
            sb.Append(",\"allowUnsafeCode\":").Append(AllowUnsafeCode ? "true" : "false");
            sb.Append(",\"overrideReferences\":").Append(OverrideReferences ? "true" : "false");
            sb.Append(",\"precompiledReferences\":");
            AppendStringArray(sb, PrecompiledReferences);
            sb.Append(",\"autoReferenced\":").Append(AutoReferenced ? "true" : "false");
            sb.Append(",\"defineConstraints\":");
            AppendStringArray(sb, DefineConstraints);
            sb.Append(",\"versionDefines\":");
            if (!string.IsNullOrWhiteSpace(VersionDefinesRaw))
                sb.Append(VersionDefinesRaw);
            else
                sb.Append("[]");
            sb.Append(",\"noEngineReferences\":").Append(NoEngineReferences ? "true" : "false");
            if (OptionalUnityReferences.Count > 0)
            {
                sb.Append(",\"optionalUnityReferences\":");
                AppendStringArray(sb, OptionalUnityReferences);
            }
            foreach (var kv in Extra)
            {
                sb.Append(",\"").Append(OutputSerializer.EscapeJsonString(kv.Key)).Append("\":");
                sb.Append(kv.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendStringArray(StringBuilder sb, List<string> items)
        {
            sb.Append('[');
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(items[i] ?? "")).Append('"');
                }
            }
            sb.Append(']');
        }
    }

    // Tolerant hand-rolled JSON reader for .asmdef. Understands string + string[]
    // + bool fields (the asmdef schema) and preserves unknown keys + the nested
    // versionDefines array verbatim. No Newtonsoft dependency (AGENTS.md
    // §Transport forbids one in the bridge).
    internal static class AsmdefJson
    {
        public static AsmdefModel Parse(string json)
        {
            var model = new AsmdefModel();
            var reader = new JsonReader(json);
            reader.Expect('{');
            while (!reader.Peek('}'))
            {
                var key = reader.ReadString();
                reader.Expect(':');
                switch (key)
                {
                    case "name": model.Name = reader.ReadString() ?? ""; break;
                    case "rootNamespace": model.RootNamespace = reader.ReadString() ?? ""; break;
                    case "references": model.References = reader.ReadStringArray(); break;
                    case "includePlatforms": model.IncludePlatforms = reader.ReadStringArray(); break;
                    case "excludePlatforms": model.ExcludePlatforms = reader.ReadStringArray(); break;
                    case "allowUnsafeCode": model.AllowUnsafeCode = reader.ReadBool(); break;
                    case "overrideReferences": model.OverrideReferences = reader.ReadBool(); break;
                    case "precompiledReferences": model.PrecompiledReferences = reader.ReadStringArray(); break;
                    case "autoReferenced": model.AutoReferenced = reader.ReadBool(); break;
                    case "defineConstraints": model.DefineConstraints = reader.ReadStringArray(); break;
                    case "noEngineReferences": model.NoEngineReferences = reader.ReadBool(); break;
                    case "optionalUnityReferences": model.OptionalUnityReferences = reader.ReadStringArray(); break;
                    case "versionDefines":
                        model.VersionDefinesRaw = reader.ReadRawValue();
                        break;
                    default:
                        // Preserve unknown keys verbatim so round-trips never
                        // drop user-authored entries.
                        model.Extra[key] = reader.ReadRawValue();
                        break;
                }
                reader.SkipComma();
            }
            reader.Expect('}');
            return model;
        }

        // Serialize with Unity's canonical key order (matches what Unity writes
        // when it creates a fresh .asmdef). Empty arrays serialize as [].
        public static string Serialize(AsmdefModel model) => model.ToJson();
    }

    // Minimal cursor-based JSON reader for the asmdef schema. Handles whitespace,
    // quoted strings (with escapes), arrays of strings, booleans, and raw value
    // capture for passthrough fields. Throws on malformed input with a clear
    // message so the tool surfaces a parse_error.
    internal sealed class JsonReader
    {
        private readonly string _s;
        private int _i;

        public JsonReader(string s)
        {
            _s = s ?? "";
            _i = 0;
        }

        public void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        public bool Peek(char c)
        {
            SkipWs();
            return _i < _s.Length && _s[_i] == c;
        }

        public void Expect(char c)
        {
            SkipWs();
            if (_i >= _s.Length || _s[_i] != c)
                throw new FormatException($"Expected '{c}' at position {_i} but found '{(_i < _s.Length ? _s[_i] : '\0')}'.");
            _i++;
        }

        // Read a JSON string (with escape handling). Returns null for a null
        // literal.
        public string ReadString()
        {
            SkipWs();
            if (_i >= _s.Length) throw new FormatException("Unexpected end of JSON reading string.");
            if (_s[_i] == 'n')
            {
                _i += 4; // null
                return null;
            }
            if (_s[_i] != '"') throw new FormatException($"Expected '\"' at position {_i} but found '{_s[_i]}'.");
            _i++;
            var sb = new StringBuilder(64);
            while (_i < _s.Length)
            {
                var c = _s[_i++];
                if (c == '\\')
                {
                    if (_i >= _s.Length) break;
                    var e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_i + 3 < _s.Length)
                            {
                                sb.Append((char)Convert.ToUInt16(_s.Substring(_i, 4), 16));
                                _i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"') return sb.ToString();
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public bool ReadBool()
        {
            SkipWs();
            if (_i >= _s.Length) throw new FormatException("Unexpected end of JSON reading bool.");
            if (_s[_i] == 't') { _i += 4; return true; }
            if (_s[_i] == 'f') { _i += 5; return false; }
            throw new FormatException($"Expected bool at position {_i} but found '{_s[_i]}'.");
        }

        // Read a JSON array of strings. Returns an empty list for [] or null.
        public List<string> ReadStringArray()
        {
            SkipWs();
            if (_i >= _s.Length) throw new FormatException("Unexpected end of JSON reading array.");
            // null literal
            if (_s[_i] == 'n') { _i += 4; return new List<string>(); }
            if (_s[_i] != '[') throw new FormatException($"Expected '[' at position {_i} but found '{_s[_i]}'.");
            _i++;
            var list = new List<string>();
            SkipWs();
            if (Peek(']')) { _i++; return list; }
            while (true)
            {
                list.Add(ReadString() ?? "");
                SkipWs();
                if (_i >= _s.Length) throw new FormatException("Unterminated array in JSON.");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == ']') { _i++; break; }
                throw new FormatException($"Expected ',' or ']' at position {_i} but found '{_s[_i]}'.");
            }
            return list;
        }

        // Capture the next JSON value (string / number / bool / array / object)
        // verbatim as text. Used for passthrough fields (versionDefines, unknown
        // keys) so round-trips preserve them byte-for-byte.
        public string ReadRawValue()
        {
            SkipWs();
            if (_i >= _s.Length) throw new FormatException("Unexpected end of JSON reading value.");
            var start = _i;
            var c = _s[_i];
            if (c == '"') { ReadString(); return _s.Substring(start, _i - start); }
            if (c == '[' || c == '{') return ReadBracketed();
            // number / true / false / null — read until a structural delimiter.
            while (_i < _s.Length && _s[_i] != ',' && _s[_i] != '}' && _s[_i] != ']')
                _i++;
            return _s.Substring(start, _i - start).Trim();
        }

        private string ReadBracketed()
        {
            var open = _s[_i];
            var close = open == '[' ? ']' : '}';
            var depth = 1;
            var start = _i;
            _i++;
            while (_i < _s.Length && depth > 0)
            {
                if (_s[_i] == '"')
                {
                    _i++;
                    while (_i < _s.Length)
                    {
                        if (_s[_i] == '\\') { _i += 2; continue; }
                        if (_s[_i] == '"') { _i++; break; }
                        _i++;
                    }
                    continue;
                }
                if (_s[_i] == open) depth++;
                else if (_s[_i] == close) depth--;
                _i++;
            }
            return _s.Substring(start, _i - start);
        }

        // After reading a value, skip an optional trailing comma so the caller's
        // loop condition (Peek('}')) works whether or not a comma was present.
        public void SkipComma()
        {
            SkipWs();
            if (_i < _s.Length && _s[_i] == ',') _i++;
        }
    }
}
