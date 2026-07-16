using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityOpenMcpBridge.MetaTools;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 6 — typed reflection / scripts / object data tools. Covers:
    //   - type_schema        (read-only)  structured member schema for a type.
    //   - script_read        (read-only)  .cs file read with line slicing.
    //   - script_write       (mutating)   Roslyn-validated create/overwrite.
    //   - script_delete      (mutating)   .cs (+.meta) removal.
    //   - object_get_data    (read-only)  reflective field/property walk.
    //   - object_modify      (mutating)   reflective public field/property set.
    //
    // Gate routing (see BridgeHttpServer DirectResponseTools / MutatingTools):
    //   - type_schema / script_read / object_get_data are gate-free direct-
    //     response tools.
    //   - script_write / script_delete / object_modify run the full gate path
    //     with paths_hint scoped to the affected .cs path / asset / scene.
    //
    // Complements (do NOT duplicate): find_members (member search),
    // invoke_method (reflection call), execute_csharp (snippet run),
    // component_get / component_modify (SerializedObject surface for one
    // Component). The reflection here uses System.Reflection directly for
    // Object graphs that are not Components.
    //
    // NOT registry-discovered: wired into BridgeHttpServer.DispatchTool
    // alongside the other M16 typed tools so the snake_case schemas parse the
    // same way.
    public static class ReflectionScriptsObjectsTools
    {
        // =========================== Type schema ===========================

        // Generate a JSON-schema-style description of one resolved type's
        // public members. Read-only, gate-free. Each member carries a flat
        // signature string AND structured fields (returnType, parameters[],
        // isStatic, isGeneric, genericParameters[] for methods; propertyType,
        // canRead, canWrite for properties) so an agent can pick a specific
        // overload and call it via invoke_method without trial-and-error.
        public static ToolDispatchResult TypeSchema(string body)
        {
            var typeName = JsonBody.GetString(body, "type_name");
            if (string.IsNullOrWhiteSpace(typeName))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'type_name' is required (full name preferred, class-name fallback).");

            var assemblyName = JsonBody.GetString(body, "assembly_name");
            var type = ResolveType(typeName, assemblyName);
            if (type == null)
                return ToolDispatchResult.Fail("type_not_found",
                    $"Type '{typeName}' not found" + (assemblyName != null ? $" in assembly '{assemblyName}'" : "") +
                    ". Use unity_open_mcp_find_members to discover available types.");

            bool includeFields = JsonBody.GetBool(body, "include_fields", true);
            bool includeProperties = JsonBody.GetBool(body, "include_properties", true);
            bool includeMethods = JsonBody.GetBool(body, "include_methods", false);
            bool includeCtors = JsonBody.GetBool(body, "include_constructors", false);
            int maxMembers = ClampPositive(JsonBody.GetInt(body, "max_members", 100));

            const BindingFlags InstanceStaticPublic =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

            var sb = new StringBuilder(2048);
            sb.Append('{');
            AppendTypeInfo(sb, type);
            sb.Append(",\"fields\":");
            int emitted = 0, truncated = 0;
            if (includeFields) AppendMemberSection(sb, GetFieldsSafe(type, InstanceStaticPublic),
                maxMembers, ref emitted, ref truncated, "field");
            else sb.Append("[]");
            sb.Append(",\"properties\":");
            if (includeProperties) AppendMemberSection(sb, GetPropertiesSafe(type, InstanceStaticPublic),
                maxMembers, ref emitted, ref truncated, "property");
            else sb.Append("[]");
            sb.Append(",\"methods\":");
            if (includeMethods) AppendMethodSection(sb, GetMethodsSafe(type, InstanceStaticPublic),
                maxMembers, ref emitted, ref truncated);
            else sb.Append("[]");
            sb.Append(",\"constructors\":");
            if (includeCtors) AppendCtorSection(sb, GetConstructorsSafe(type, InstanceStaticPublic),
                maxMembers, ref emitted, ref truncated);
            else sb.Append("[]");
            sb.Append(",\"emitted\":").Append(emitted);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        private static void AppendTypeInfo(StringBuilder sb, Type type)
        {
            sb.Append("\"fullName\":\"").Append(OutputSerializer.EscapeJsonString(type.FullName ?? type.Name));
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(type.Name));
            sb.Append("\",\"namespace\":\"").Append(OutputSerializer.EscapeJsonString(type.Namespace ?? ""));
            var asmName = type.Assembly.GetName().Name;
            sb.Append("\",\"assembly\":\"").Append(OutputSerializer.EscapeJsonString(asmName ?? ""));
            sb.Append("\",\"isEnum\":").Append(type.IsEnum ? "true" : "false");
            sb.Append(",\"isClass\":").Append(type.IsClass ? "true" : "false");
            sb.Append(",\"isValueType\":").Append(type.IsValueType ? "true" : "false");
            sb.Append(",\"isInterface\":").Append(type.IsInterface ? "true" : "false");
            sb.Append(",\"baseType\":").Append(type.BaseType != null
                ? "\"" + OutputSerializer.EscapeJsonString(type.BaseType.FullName ?? type.BaseType.Name) + "\""
                : "null");

            if (type.IsEnum)
            {
                sb.Append(",\"enumValues\":[");
                var names = type.GetEnumNames();
                var values = type.GetEnumValues();
                for (int i = 0; i < names.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(names[i].ToString()));
                    sb.Append("\",\"value\":").Append(Convert.ToInt64(values.GetValue(i), CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture)).Append('}');
                }
                sb.Append(']');
            }
        }

        private static void AppendMemberSection<T>(StringBuilder sb, IEnumerable<T> members,
            int maxMembers, ref int emitted, ref int truncated, string kind) where T : MemberInfo
        {
            sb.Append('[');
            bool first = true;
            foreach (var m in members)
            {
                if (emitted >= maxMembers) { truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                AppendFieldOrProperty(sb, m, kind);
                emitted++;
            }
            sb.Append(']');
        }

        private static void AppendFieldOrProperty(StringBuilder sb, MemberInfo m, string kind)
        {
            // m is FieldInfo or PropertyInfo under the public instance+static
            // binding flags. Emit shared metadata plus kind-specific fields.
            sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(m.Name)).Append('"');

            Type memberType; bool isStatic; bool isInitOnly = false; bool canRead = false, canWrite = false;
            if (m is FieldInfo f)
            {
                memberType = f.FieldType;
                isStatic = f.IsStatic;
                isInitOnly = f.IsInitOnly;
            }
            else if (m is PropertyInfo p)
            {
                memberType = p.PropertyType;
                isStatic = p.GetMethod != null && p.GetMethod.IsStatic;
                canRead = p.CanRead;
                canWrite = p.CanWrite;
            }
            else return;

            sb.Append(",\"kind\":\"").Append(kind);
            sb.Append("\",\"memberType\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(memberType)));
            sb.Append("\",\"memberTypeFullName\":\"").Append(OutputSerializer.EscapeJsonString(memberType.FullName ?? memberType.Name));
            sb.Append("\",\"declaringType\":\"").Append(OutputSerializer.EscapeJsonString(m.DeclaringType?.FullName ?? m.DeclaringType?.Name ?? ""));
            sb.Append("\",\"isStatic\":").Append(isStatic ? "true" : "false");

            if (m is FieldInfo)
            {
                sb.Append(",\"isInitOnly\":").Append(isInitOnly ? "true" : "false");
            }
            else
            {
                sb.Append(",\"canRead\":").Append(canRead ? "true" : "false");
                sb.Append(",\"canWrite\":").Append(canWrite ? "true" : "false");
            }

            // Flat signature kept for compatibility with find_members output.
            sb.Append(",\"signature\":\"")
              .Append(OutputSerializer.EscapeJsonString(TypeDisplayName(memberType))).Append(' ')
              .Append(OutputSerializer.EscapeJsonString(m.Name)).Append('"');
            sb.Append('}');
        }

        private static void AppendMethodSection(StringBuilder sb, IEnumerable<MethodInfo> methods,
            int maxMembers, ref int emitted, ref int truncated)
        {
            sb.Append('[');
            bool first = true;
            foreach (var method in methods)
            {
                if (emitted >= maxMembers) { truncated++; continue; }
                // Skip property backing getters/setters and operators — they
                // add noise without being callable through invoke_method.
                if (method.IsSpecialName) { truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                AppendMethod(sb, method);
                emitted++;
            }
            sb.Append(']');
        }

        private static void AppendMethod(StringBuilder sb, MethodInfo method)
        {
            sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(method.Name));
            sb.Append("\",\"kind\":\"method");
            sb.Append("\",\"returnType\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(method.ReturnType)));
            sb.Append("\",\"returnTypeFullName\":\"").Append(OutputSerializer.EscapeJsonString(method.ReturnType.FullName ?? method.ReturnType.Name));
            sb.Append("\",\"declaringType\":\"").Append(OutputSerializer.EscapeJsonString(method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? ""));
            sb.Append("\",\"isStatic\":").Append(method.IsStatic ? "true" : "false");
            sb.Append(",\"isGeneric\":").Append(method.IsGenericMethod ? "true" : "false");

            var genericArgs = method.GetGenericArguments();
            sb.Append(",\"genericParameters\":[");
            for (int i = 0; i < genericArgs.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(genericArgs[i].Name)).Append('"');
                sb.Append(",\"constraints\":[");
                var constraints = genericArgs[i].GetGenericParameterConstraints();
                for (int c = 0; c < constraints.Length; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(TypeDisplayName(constraints[c]))).Append('"');
                }
                sb.Append("]}");
            }
            sb.Append(']');

            var parms = method.GetParameters();
            sb.Append(",\"parameters\":[");
            for (int i = 0; i < parms.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(parms[i].Name ?? ""));
                sb.Append("\",\"type\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(parms[i].ParameterType)));
                sb.Append("\",\"typeFullName\":\"").Append(OutputSerializer.EscapeJsonString(parms[i].ParameterType.FullName ?? parms[i].ParameterType.Name));
                sb.Append("\",\"hasDefault\":").Append(parms[i].HasDefaultValue ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');

            // Flat signature kept for compatibility with find_members output.
            sb.Append(",\"signature\":\"").Append(OutputSerializer.EscapeJsonString(BuildMethodSignature(method))).Append('"');
            sb.Append('}');
        }

        private static void AppendCtorSection(StringBuilder sb, IEnumerable<ConstructorInfo> ctors,
            int maxMembers, ref int emitted, ref int truncated)
        {
            sb.Append('[');
            bool first = true;
            foreach (var ctor in ctors)
            {
                if (emitted >= maxMembers) { truncated++; continue; }
                if (ctor.IsStatic) { truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":\".ctor\",\"kind\":\"constructor");
                sb.Append("\",\"isStatic\":false");
                var parms = ctor.GetParameters();
                sb.Append(",\"parameters\":[");
                for (int i = 0; i < parms.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(OutputSerializer.EscapeJsonString(parms[i].Name ?? ""));
                    sb.Append("\",\"type\":\"").Append(OutputSerializer.EscapeJsonString(TypeDisplayName(parms[i].ParameterType)));
                    sb.Append("\",\"typeFullName\":\"").Append(OutputSerializer.EscapeJsonString(parms[i].ParameterType.FullName ?? parms[i].ParameterType.Name));
                    sb.Append("\",\"hasDefault\":").Append(parms[i].HasDefaultValue ? "true" : "false");
                    sb.Append('}');
                }
                sb.Append("]}");
                emitted++;
            }
            sb.Append(']');
        }

        // =========================== Script read ===========================

        // Read a .cs file from disk with optional line slicing. Read-only,
        // gate-free. The path must live under the project root and end in .cs.
        public static ToolDispatchResult ScriptRead(string body)
        {
            var filePath = JsonBody.GetString(body, "file_path");
            if (string.IsNullOrWhiteSpace(filePath))
                return ToolDispatchResult.Fail("missing_parameter", "'file_path' is required.");

            var resolvedPath = ResolveScriptPath(filePath, out var pathError);
            if (resolvedPath == null) return pathError;

            if (!File.Exists(resolvedPath))
                return ToolDispatchResult.Fail("file_not_found", $"No file at '{filePath}'.");

            int startLine = ClampPositive(JsonBody.GetInt(body, "start_line", 1));
            int endLine = JsonBody.GetInt(body, "end_line", 0);
            int maxLines = ClampPositive(JsonBody.GetInt(body, "max_lines", 2000));

            string[] allLines;
            try { allLines = File.ReadAllLines(resolvedPath); }
            catch (Exception e) { return ToolDispatchResult.Fail("read_failed", e.Message); }

            int totalLines = allLines.Length;
            if (startLine > totalLines) startLine = totalLines > 0 ? totalLines : 1;
            if (endLine <= 0 || endLine > totalLines) endLine = totalLines;
            if (endLine < startLine) endLine = startLine;

            int requested = endLine - startLine + 1;
            int capped = Math.Min(requested, maxLines);
            int returned = Math.Min(capped, totalLines - startLine + 1);
            if (returned < 0) returned = 0;
            int truncated = (totalLines - startLine + 1) - returned;

            var sb = new StringBuilder(64 + returned * 64);
            sb.Append("{\"filePath\":\"").Append(OutputSerializer.EscapeJsonString(filePath));
            sb.Append("\",\"totalLines\":").Append(totalLines);
            sb.Append(",\"startLine\":").Append(startLine);
            sb.Append(",\"endLine\":").Append(startLine + returned - 1);
            sb.Append(",\"count\":").Append(returned);
            sb.Append(",\"truncated\":").Append(truncated > 0 ? truncated : 0);
            sb.Append(",\"lines\":[");
            for (int i = 0; i < returned; i++)
            {
                if (i > 0) sb.Append(',');
                var line = allLines[startLine - 1 + i];
                sb.Append("{\"number\":").Append(startLine + i);
                sb.Append(",\"text\":\"").Append(OutputSerializer.EscapeJsonString(line)).Append("\"}");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // =========================== Script write ==========================

        // Create or overwrite a .cs file under the project, after optional
        // Roslyn pre-write validation. Mutating: writes the file and refreshes
        // AssetDatabase (which may trigger a recompile / domain reload). The
        // validation result is exposed as a return field rather than a
        // separate tool — a failed parse returns validation_failed and the
        // file is not written.
        public static ToolDispatchResult ScriptWrite(string body)
        {
            var filePath = JsonBody.GetString(body, "file_path");
            if (string.IsNullOrWhiteSpace(filePath))
                return ToolDispatchResult.Fail("missing_parameter", "'file_path' is required.");

            var content = JsonBody.GetString(body, "content");
            if (string.IsNullOrEmpty(content))
                return ToolDispatchResult.Fail("missing_parameter", "'content' is required and must be non-empty.");

            var overwrite = JsonBody.GetBool(body, "overwrite", false);
            var validate = JsonBody.GetBool(body, "validate", true);

            var resolvedPath = ResolveScriptPath(filePath, out var pathError);
            if (resolvedPath == null) return pathError;

            var fileExists = File.Exists(resolvedPath);
            if (fileExists && !overwrite)
                return ToolDispatchResult.Fail("file_exists",
                    $"File '{filePath}' already exists. Pass overwrite:true to overwrite.");

            string validationDiagnostics = null;
            bool validationPassed = true;
            if (validate)
            {
                var (ok, diag) = ValidateScript(content);
                validationPassed = ok;
                validationDiagnostics = diag;
                if (!ok)
                    return ToolDispatchResult.Fail("validation_failed",
                        "Roslyn pre-write validation failed. The file was not written. Diagnostics:\n" +
                        (diag ?? "Unknown compilation error."));
            }

            try
            {
                var dir = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(resolvedPath, content);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("write_failed", e.Message);
            }

            // Refresh so Unity picks up the new/changed script. Use
            // ImportAsset (scoped) rather than a whole-project refresh — the
            // path is known and we already bound it via paths_hint.
            AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceUpdate);

            var sb = new StringBuilder(160);
            sb.Append("{\"status\":\"ok\",\"filePath\":\"").Append(OutputSerializer.EscapeJsonString(filePath));
            sb.Append("\",\"action\":").Append(fileExists ? "\"overwrite\"" : "\"create\"");
            sb.Append(",\"bytesWritten\":").Append(content.Length);
            sb.Append(",\"validated\":").Append(validate ? "true" : "false");
            sb.Append(",\"validationPassed\":").Append(validationPassed ? "true" : "false");
            if (!string.IsNullOrEmpty(validationDiagnostics))
            {
                sb.Append(",\"validationDiagnostics\":\"")
                  .Append(OutputSerializer.EscapeJsonString(validationDiagnostics)).Append('"');
            }
            sb.Append(",\"note\":\"AssetDatabase.ImportAsset queued; a recompile / domain reload may follow. Poll editor_status / compile_check to confirm.\"");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // =========================== Script delete =========================

        // Delete one or more .cs files (and their .meta). Mutating: removes
        // the file and refreshes AssetDatabase. Per-file errors are
        // accumulated and do not abort the batch.
        public static ToolDispatchResult ScriptDelete(string body)
        {
            var filePaths = JsonBody.GetStringArray(body, "file_paths");
            if (filePaths == null || filePaths.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'file_paths' is required and must be a non-empty array of .cs paths.");

            var deleted = new List<string>();
            var errors = new List<string>();
            var refreshPaths = new List<string>();

            foreach (var rawPath in filePaths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                var resolved = ResolveScriptPath(rawPath, out var err);
                if (resolved == null)
                {
                    errors.Add(err.ErrorMessage ?? $"Invalid path '{rawPath}'.");
                    continue;
                }
                if (!File.Exists(resolved))
                {
                    errors.Add($"No file at '{rawPath}'.");
                    continue;
                }
                try
                {
                    File.Delete(resolved);
                    var meta = resolved + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    deleted.Add(rawPath);
                    refreshPaths.Add(rawPath);
                }
                catch (Exception e)
                {
                    errors.Add($"Delete '{rawPath}' failed: {e.Message}");
                }
            }

            if (refreshPaths.Count > 0)
            {
                foreach (var p in refreshPaths) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            }

            return ToolDispatchResult.Ok(BuildOpResult(deleted, errors, "deleted", null));
        }

        // ========================= Object get data =========================

        // Token-bounded reflective read of any live UnityEngine.Object. Uses
        // the depth-limited OutputSerializer walker (the same engine
        // invoke_method uses for its return value) so the shape is consistent.
        public static ToolDispatchResult ObjectGetData(string body)
        {
            var target = ResolveObject(body);
            if (target == null)
                return ToolDispatchResult.Fail("object_not_found",
                    "Could not resolve a UnityEngine.Object — pass instance_id (live instance) or asset_path (asset on disk).");

            var options = new SerializeOptions
            {
                MaxDepth = ClampPositive(JsonBody.GetInt(body, "max_depth", 4)),
                MaxListItems = ClampPositive(JsonBody.GetInt(body, "max_items", 100)),
                IncludeFields = JsonBody.GetBool(body, "include_fields", true),
                IncludeProperties = JsonBody.GetBool(body, "include_properties", true),
            };

            var sb = new StringBuilder(256);
            sb.Append("{\"object\":{");
            sb.Append("\"name\":\"").Append(OutputSerializer.EscapeJsonString(target.name));
            sb.Append("\",\"type\":\"").Append(OutputSerializer.EscapeJsonString(target.GetType().FullName));
            sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(target));
            var assetPath = AssetDatabase.GetAssetPath(target);
            sb.Append(",\"assetPath\":").Append(string.IsNullOrEmpty(assetPath) ? "null"
                : "\"" + OutputSerializer.EscapeJsonString(assetPath) + "\"");
            sb.Append("},\"data\":");

            var serialized = OutputSerializer.Serialize(target, options);
            sb.Append(serialized ?? "null");
            sb.Append(",\"options\":{");
            sb.Append("\"maxDepth\":").Append(options.MaxDepth);
            sb.Append(",\"maxListItems\":").Append(options.MaxListItems);
            sb.Append(",\"includeFields\":").Append(options.IncludeFields ? "true" : "false");
            sb.Append(",\"includeProperties\":").Append(options.IncludeProperties ? "true" : "false");
            sb.Append("}}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ========================= Object modify ===========================

        // Modify public fields/properties on any live UnityEngine.Object via
        // reflection. Explicit per-field scope; safe by default (refuses
        // static / init-only fields unless allow_static). Does NOT invoke
        // methods. For Component Inspector fields prefer component_modify
        // (SerializedObject round-trips the Inspector more accurately).
        public static ToolDispatchResult ObjectModify(string body)
        {
            var target = ResolveObject(body);
            if (target == null)
                return ToolDispatchResult.Fail("object_not_found",
                    "Could not resolve a UnityEngine.Object — pass instance_id (live instance) or asset_path (asset on disk).");

            var entries = JsonBody.GetObjectArray(body, "fields");
            if (entries == null || entries.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'fields' is required and must be a non-empty array of { name, value } patches.");

            var allowStatic = JsonBody.GetBool(body, "allow_static", false);
            var modified = new List<string>();
            var errors = new List<string>();

            // Delegates to the same reflection + ConvertValue path used by
            // scriptableobject_create (ApplyFieldPatches) so member resolution,
            // static/init-only guarding, and value conversion live in one place.
            ApplyFieldPatches(target, entries, allowStatic, modified, errors);

            if (modified.Count > 0)
            {
                EditorUtility.SetDirty(target);
                var assetPath = AssetDatabase.GetAssetPath(target);
                if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.SaveAssetIfDirty(target);
            }

            return ToolDispatchResult.Ok(BuildOpResult(modified, errors, "modified", null));
        }

        // ===================== ScriptableObject create =====================

        // M20 Plan 5 / T20.5.1 — typed ScriptableObject create. Mutating: runs
        // the full gate path with paths_hint scoped to the asset path. Resolves
        // the type via the same resolver used by type_schema / invoke_method,
        // instantiates it via ScriptableObject.CreateInstance, applies optional
        // initial field patches (same value shape + ConvertValue path as
        // object_modify — no reimplementation), and writes the .asset via
        // AssetDatabase.CreateAsset. Read/info access stays on object_get_data;
        // arbitrary field edits stay on object_modify — this tool is the clean
        // gate-integrated create path.
        public static ToolDispatchResult ScriptableObjectCreate(string body)
        {
            var typeName = JsonBody.GetString(body, "type_name");
            if (string.IsNullOrWhiteSpace(typeName))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'type_name' is required (full name preferred, class-name fallback). " +
                    "Use unity_open_mcp_find_members / type_schema to discover available types.");

            var assetPath = JsonBody.GetString(body, "asset_path");
            if (string.IsNullOrWhiteSpace(assetPath))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'asset_path' is required and must start with 'Assets/' and end with '.asset'.");

            var normalized = assetPath.Replace('\\', '/').Trim('/');
            if (!normalized.StartsWith("Assets/"))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must start with 'Assets/': '{normalized}'.");
            if (!normalized.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must end with '.asset': '{normalized}'.");
            if (normalized.Contains(".."))
                return ToolDispatchResult.Fail("invalid_paths",
                    $"asset_path must not contain '..': '{normalized}'.");

            var assemblyName = JsonBody.GetString(body, "assembly_name");
            var type = ResolveType(typeName, assemblyName);
            if (type == null)
                return ToolDispatchResult.Fail("type_not_found",
                    $"Type '{typeName}' not found" + (assemblyName != null ? $" in assembly '{assemblyName}'" : "") +
                    ". Use unity_open_mcp_find_members / type_schema to discover available types.");

            // Must be a ScriptableObject (or a subclass). Refuse abstract types
            // and non-instantiable definitions with a precise error.
            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                return ToolDispatchResult.Fail("type_not_scriptableobject",
                    $"Type '{type.FullName}' does not inherit from UnityEngine.ScriptableObject. " +
                    "scriptableobject_create only accepts ScriptableObject subclasses.");
            if (type.IsAbstract)
                return ToolDispatchResult.Fail("type_abstract",
                    $"Type '{type.FullName}' is abstract and cannot be instantiated.");

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized) != null)
                return ToolDispatchResult.Fail("asset_exists",
                    $"An asset already exists at '{normalized}'. Use object_modify to edit it, " +
                    "or choose a different asset_path.");

            // Create intermediate folders if missing so the create never fails
            // on a missing parent directory.
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var dir = normalized.Substring(0, lastSlash);
                MaterialTools.EnsureFolderRecursive(dir);
            }

            ScriptableObject instance;
            try
            {
                instance = (ScriptableObject)Activator.CreateInstance(type);
                instance.name = System.IO.Path.GetFileNameWithoutExtension(normalized);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("instantiate_failed",
                    $"Failed to instantiate '{type.FullName}': {e.Message}");
            }

            // Apply optional initial field patches (same value shape + conversion
            // path as object_modify). Per-entry errors are accumulated and do NOT
            // abort the create — the asset is still written with whatever fields
            // succeeded. Static / init-only writes require allow_static, matching
            // object_modify's safety contract.
            var modified = new List<string>();
            var fieldErrors = new List<string>();
            var allowStatic = JsonBody.GetBool(body, "allow_static", false);
            var entries = JsonBody.GetObjectArray(body, "fields");
            if (entries != null && entries.Length > 0)
            {
                ApplyFieldPatches(instance, entries, allowStatic, modified, fieldErrors);
            }

            try
            {
                AssetDatabase.CreateAsset(instance, normalized);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(normalized, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (System.Exception e)
            {
                // The instance is not a tracked asset until CreateAsset succeeds;
                // if it threw, destroy the orphan so we don't leak a runtime SO.
                if (AssetDatabase.GetAssetPath(instance) == "")
                    UnityEngine.Object.DestroyImmediate(instance);
                return ToolDispatchResult.Fail("create_error",
                    $"Failed to create ScriptableObject asset at '{normalized}': {e.Message}");
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"assetPath\":\"").Append(OutputSerializer.EscapeJsonString(normalized));
            sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(instance.name));
            sb.Append("\",\"type\":\"").Append(OutputSerializer.EscapeJsonString(type.FullName));
            sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(instance));
            sb.Append(",\"fieldsApplied\":").Append(modified.Count);
            sb.Append(",\"applied\":");
            AppendStringArray(sb, modified);
            if (fieldErrors.Count > 0)
            {
                sb.Append(",\"fieldErrors\":");
                AppendStringArray(sb, fieldErrors);
            }
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ================ List assets of type (read-only) =================

        // M20 Plan 5 / T20.5.1 — typed asset listing by type. Read-only and
        // gate-free. Resolves the type via the same resolver used by
        // type_schema, then enumerates AssetDatabase.FindAssets("t:<TypeName>")
        // under `folder` (default Assets). Offline-routeable in principle — the
        // offline YAML/GUID index can answer t:<Type> filter queries without a
        // live Editor (noted in the tool description); the live path here is the
        // authoritative implementation.
        public static ToolDispatchResult ListAssetsOfType(string body)
        {
            var typeName = JsonBody.GetString(body, "type_name");
            if (string.IsNullOrWhiteSpace(typeName))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'type_name' is required (full name preferred, class-name fallback). " +
                    "Use unity_open_mcp_find_members / type_schema to discover available types.");

            var folder = JsonBody.GetString(body, "folder");
            if (string.IsNullOrWhiteSpace(folder)) folder = "Assets";

            bool includeIndirect = JsonBody.GetBool(body, "include_indirect", false);
            int maxResults = ClampPositive(JsonBody.GetInt(body, "max_results", 200));

            // Resolve the type so we can do an IsAssignableFrom membership check.
            // If the type can't be resolved we still fall back to a name-based
            // t:<Name> filter — agents commonly list by the class name only.
            var assemblyName = JsonBody.GetString(body, "assembly_name");
            var type = ResolveType(typeName, assemblyName);

            var searchType = type != null ? type.Name : typeName;
            // FindAssets supports t:<Type> which matches the Unity asset-type
            // label. We pass includeFolders:false so only files come back.
            var guids = AssetDatabase.FindAssets("t:" + searchType, new[] { folder });

            // The t:<Type> label filter only matches types Unity classifies as
            // asset types. Custom ScriptableObject types (especially ones defined
            // in a test/runtime assembly) are not always registered as asset-type
            // labels, so the filter can return empty even when matching .asset
            // files exist. Fall back to enumerating every asset in the folder and
            // checking the loaded type via IsAssignableFrom.
            if (guids.Length == 0 && type != null)
            {
                var allGuids = AssetDatabase.FindAssets("", new[] { folder });
                var filtered = new System.Collections.Generic.List<string>();
                foreach (var g in allGuids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(p) || !p.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    var loaded = AssetDatabase.LoadMainAssetAtPath(p);
                    if (loaded != null && type.IsAssignableFrom(loaded.GetType()))
                        filtered.Add(g);
                }
                guids = filtered.ToArray();
            }
            // When includeIndirect is false (default), FindAssets already returns
            // only top-level results; the flag is a no-op here but kept for
            // schema parity with list_assets.

            var sb = new StringBuilder(2048);
            sb.Append("{\"type\":\"").Append(OutputSerializer.EscapeJsonString(typeName));
            sb.Append("\",\"resolvedType\":");
            if (type != null)
                sb.Append('"').Append(OutputSerializer.EscapeJsonString(type.FullName)).Append('"');
            else
                sb.Append("null");
            sb.Append(",\"folder\":\"").Append(OutputSerializer.EscapeJsonString(folder));
            sb.Append("\",\"assets\":[");

            int emitted = 0;
            int truncated = 0;
            for (int i = 0; i < guids.Length; i++)
            {
                if (emitted >= maxResults) { truncated++; continue; }
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path)) continue;

                var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
                var assetType = mainAsset != null ? mainAsset.GetType().FullName : "";

                if (emitted > 0) sb.Append(',');
                sb.Append("{\"path\":\"").Append(OutputSerializer.EscapeJsonString(path));
                sb.Append("\",\"name\":\"").Append(OutputSerializer.EscapeJsonString(
                    System.IO.Path.GetFileNameWithoutExtension(path)));
                sb.Append("\",\"type\":\"").Append(OutputSerializer.EscapeJsonString(assetType));
                sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(mainAsset));
                sb.Append('}');
                emitted++;
            }
            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append(",\"maxResults\":").Append(maxResults);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ----------------------------- helpers -----------------------------

        // Resolve a UnityEngine.Object by instance_id (preferred) or
        // asset_path. Returns null when neither resolves. Used by
        // object_get_data and object_modify.
        private static UnityEngine.Object ResolveObject(string body)
        {
            var instanceId = JsonBody.GetLongFlexible(body, "instance_id", 0);
            if (instanceId != 0)
            {
                var obj = InstanceId.ToObject(instanceId);
                if (obj != null) return obj;
            }
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            return null;
        }

        // Resolve a project-relative .cs path to an absolute path inside the
        // project root. Refuses paths that escape the project or aren't .cs.
        // The returned path is normalized to the OS separator; `inputPath` is
        // the original (forward-slash) form for AssetDatabase calls.
        private static string ResolveScriptPath(string inputPath, out ToolDispatchResult error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                error = ToolDispatchResult.Fail("invalid_path", "Path is empty.");
                return null;
            }
            var normalized = inputPath.Replace('\\', '/').TrimStart('/');
            if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = ToolDispatchResult.Fail("invalid_path", $"Path '{inputPath}' must end in .cs.");
                return null;
            }
            // Refuse parent-traversal escapes — combined with the project-root
            // containment check below this blocks path traversal.
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
                error = ToolDispatchResult.Fail("invalid_path",
                    $"Path '{inputPath}' escapes the project root.");
                return null;
            }
            return absolute;
        }

        // Roslyn-validate a script source. Returns (true, null) on success,
        // (false, diagnostics) on parse/compile failure. Mirrors the compile
        // path ExecuteCSharpTool uses so error messages line up.
        private static (bool ok, string diagnostics) ValidateScript(string content)
        {
            if (!RoslynHost.Initialize())
                // When Roslyn is unavailable we cannot validate — refuse to
                // write rather than silently skip validation.
                return (false, "Roslyn compiler assemblies are not available; cannot validate. " +
                    (RoslynHost.LastInitError ?? ""));

            // Compile-only validation: we never load/execute the assembly.
            var (_, errors) = RoslynHost.Compile(content);
            if (string.IsNullOrEmpty(errors)) return (true, null);
            return (false, errors);
        }

        // Resolve a type by full name (preferred) or class name fallback.
        // Mirrors InvokeMethodTool.FindType so the behavior is consistent.
        // Skips execute_csharp snippet assemblies — they are transient scratch
        // assemblies whose Snippet type must never resolve as a project type.
        private static Type ResolveType(string typeName, string assemblyName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (!string.IsNullOrEmpty(assemblyName))
            {
                var asm = assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName);
                return asm?.GetType(typeName);
            }
            foreach (var asm in assemblies)
            {
                if (ExecuteCSharpTool.IsSnippetAssembly(asm)) continue;
                var type = asm.GetType(typeName);
                if (type != null) return type;
            }
            foreach (var asm in assemblies)
            {
                if (ExecuteCSharpTool.IsSnippetAssembly(asm)) continue;
                try
                {
                    foreach (var t in SafeGetTypes(asm))
                        if (t.Name == typeName) return t;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types != null ? FilterNull(e.Types) : Array.Empty<Type>();
            }
        }

        private static IEnumerable<Type> FilterNull(Type[] types)
        {
            foreach (var t in types) if (t != null) yield return t;
        }

        private static IEnumerable<FieldInfo> GetFieldsSafe(Type type, BindingFlags flags)
        {
            try { return type.GetFields(flags); }
            catch { return Array.Empty<FieldInfo>(); }
        }

        private static IEnumerable<PropertyInfo> GetPropertiesSafe(Type type, BindingFlags flags)
        {
            try { return type.GetProperties(flags); }
            catch { return Array.Empty<PropertyInfo>(); }
        }

        private static IEnumerable<MethodInfo> GetMethodsSafe(Type type, BindingFlags flags)
        {
            try { return type.GetMethods(flags); }
            catch { return Array.Empty<MethodInfo>(); }
        }

        private static IEnumerable<ConstructorInfo> GetConstructorsSafe(Type type, BindingFlags flags)
        {
            try { return type.GetConstructors(flags); }
            catch { return Array.Empty<ConstructorInfo>(); }
        }

        private static string TypeDisplayName(Type type)
        {
            // Match the C#-style name find_members already uses
            // (int, string, Vector3, List<T>) so the two tools read the same.
            if (type == null) return "null";
            var name = type.Name;
            if (type.IsGenericType)
            {
                var tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                var args = type.GetGenericArguments();
                name += "<" + string.Join(", ", args.Select(TypeDisplayName)) + ">";
            }
            return name;
        }

        private static string BuildMethodSignature(MethodInfo method)
        {
            var sb = new StringBuilder(64);
            sb.Append(TypeDisplayName(method.ReturnType)).Append(' ').Append(method.Name);
            if (method.IsGenericMethod)
            {
                sb.Append('<');
                sb.Append(string.Join(", ", method.GetGenericArguments().Select(t => t.Name)));
                sb.Append('>');
            }
            sb.Append('(');
            var parms = method.GetParameters();
            for (int i = 0; i < parms.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(TypeDisplayName(parms[i].ParameterType)).Append(' ').Append(parms[i].Name);
            }
            sb.Append(')');
            return sb.ToString();
        }

        // Convert a raw JSON value string into the target CLR type. Handles
        // the scalar/vector/object-reference shapes component_modify already
        // supports so the same value payloads work across both tools.
        private static object ConvertValue(string raw, Type targetType)
        {
            if (raw == null) raw = "null";
            raw = raw.Trim();
            if (raw == "null")
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return Activator.CreateInstance(targetType);
                return null;
            }

            if (targetType == typeof(string))
                return StripQuotes(raw);
            if (targetType == typeof(int)) return ParseInt(raw);
            if (targetType == typeof(float)) return ParseFloat(raw);
            if (targetType == typeof(double)) return double.Parse(StripQuotes(raw), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return StripQuotes(raw) == "true";
            if (targetType == typeof(long)) return long.Parse(StripQuotes(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (targetType.IsEnum)
            {
                var unquoted = StripQuotes(raw);

                // [Flags] enums legitimately take combined numeric values (e.g.
                // 5 for A|B where A=1,B=4) that match no single declared
                // constant. Detect the attribute once and accept any numeric
                // fitting the underlying type in that case; otherwise apply the
                // strict declared-constant validation below (which rejects
                // garbage ints like 42 on a 3-value enum).
                bool isFlags = targetType.GetCustomAttribute<FlagsAttribute>() != null;

                // Numeric form FIRST — Enum.TryParse accepts ANY numeric value
                // that fits the underlying type, even when no declared enum
                // constant has that value (e.g. "42" on a 3-value enum returns
                // true with garbage). So we must validate numeric input against
                // the declared constants ourselves before accepting it; a value
                // that matches no constant is rejected with a clear error. For
                // [Flags] enums any in-range numeric is valid (a combination),
                // so the declared-constant check is skipped.
                if (long.TryParse(unquoted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
                {
                    if (isFlags)
                        return Enum.ToObject(targetType, numeric);

                    foreach (var defined in Enum.GetValues(targetType))
                    {
                        if (Convert.ToInt64(defined, CultureInfo.InvariantCulture) == numeric)
                            return Enum.ToObject(targetType, numeric);
                    }
                    var numAllowed = string.Join(", ", Enum.GetNames(targetType));
                    throw new ArgumentException(
                        $"value '{unquoted}' is not a valid {targetType.Name}; allowed: [{numAllowed}]");
                }

                // Name form (case-insensitive). Enum.TryParse returns false for
                // unknown names instead of throwing the cryptic ArgumentException
                // that Enum.Parse emits, so we can surface a clear error here.
                // It also accepts comma-separated combined names ("A, B") for
                // [Flags] enums, so the flags case needs no special handling here.
                if (Enum.TryParse(targetType, unquoted, ignoreCase: true, out object enumValue))
                    return enumValue;

                var allowed = string.Join(", ", Enum.GetNames(targetType));
                throw new ArgumentException(
                    $"value '{unquoted}' is not a valid {targetType.Name}; allowed: [{allowed}]");
            }

            // Vector / Color — reuse MaterialTools float-array parser.
            if (targetType == typeof(Vector2))
            {
                var p = MaterialTools.ParseFloatArray(raw);
                if (p == null || p.Length < 2) throw new FormatException("Vector2 value must be [x,y].");
                return new Vector2(p[0], p[1]);
            }
            if (targetType == typeof(Vector3))
            {
                var p = MaterialTools.ParseFloatArray(raw);
                if (p == null || p.Length < 3) throw new FormatException("Vector3 value must be [x,y,z].");
                return new Vector3(p[0], p[1], p[2]);
            }
            if (targetType == typeof(Vector4))
            {
                var p = MaterialTools.ParseFloatArray(raw);
                if (p == null || p.Length < 4) throw new FormatException("Vector4 value must be [x,y,z,w].");
                return new Vector4(p[0], p[1], p[2], p[3]);
            }
            if (targetType == typeof(Color))
            {
                var p = MaterialTools.ParseFloatArray(raw);
                if (p == null || p.Length < 3) throw new FormatException("Color value must be [r,g,b] or [r,g,b,a].");
                float a = p.Length >= 4 ? p[3] : 1f;
                return new Color(p[0], p[1], p[2], a);
            }
            if (targetType == typeof(Quaternion))
            {
                var p = MaterialTools.ParseFloatArray(raw);
                if (p != null && p.Length >= 4) return new Quaternion(p[0], p[1], p[2], p[3]);
                if (p != null && p.Length >= 3) return Quaternion.Euler(p[0], p[1], p[2]);
                throw new FormatException("Quaternion value must be [x,y,z,w] or euler [x,y,z].");
            }

            // Object reference — accept {"path": ...}, {"instance_id": N}, or null.
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                var path = JsonBody.GetString("{\"v\":" + raw + "}", "v");
                if (string.IsNullOrEmpty(path)) path = JsonBody.GetString(raw, "path");
                if (string.IsNullOrEmpty(path)) path = JsonBody.GetString(raw, "asset_path");
                if (!string.IsNullOrEmpty(path))
                    return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                // instance_id fallback — long-backed via InstanceId.Parse so
                // IDs > int.MaxValue resolve on Unity 6000.5+ (the 8-byte
                // EntityId no longer fits in an int). Accepts both bare numeric
                // and JSON-string wire forms.
                var idRaw = JsonBody.GetRawValue("{\"v\":" + raw + "}", "v");
                if (!string.IsNullOrEmpty(idRaw))
                {
                    var id = InstanceId.Parse(StripQuotes(idRaw));
                    if (id != 0) return InstanceId.ToObject(id);
                }
                throw new FormatException("object_reference value must be {\"path\": \"...\"}, {\"asset_path\": \"...\"}, {\"instance_id\": N}, or null.");
            }

            // Fallback: hand the JSON fragment to Convert.ChangeType for
            // IConvertible targets (numbers/strings). Anything else surfaces a
            // clear error rather than silently dropping the patch.
            if (typeof(IConvertible).IsAssignableFrom(targetType))
                return Convert.ChangeType(StripQuotes(raw), targetType, CultureInfo.InvariantCulture);

            throw new NotSupportedException(
                $"Cannot convert value to {targetType.FullName}. Use a typed tool (component_modify) for complex fields.");
        }

        private static int ParseInt(string raw)
        {
            if (!int.TryParse(StripQuotes(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                throw new FormatException($"Could not parse int from '{raw}'.");
            return n;
        }

        private static float ParseFloat(string raw)
        {
            if (!float.TryParse(StripQuotes(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                throw new FormatException($"Could not parse float from '{raw}'.");
            return f;
        }

        private static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private static int ClampPositive(int n) => n < 1 ? 1 : n;

        // Op-result builder matching the AssetsTools/ComponentsTools shape so
        // the response keys are consistent across mutating typed tools.
        private static string BuildOpResult(List<string> done, List<string> errors, string doneLabel,
            Action<StringBuilder> extra)
        {
            var sb = new StringBuilder(128 + done.Count * 32 + errors.Count * 48);
            sb.Append("{\"status\":").Append(done.Count > 0 ? "\"ok\"" : "\"error\"");
            sb.Append(",\"").Append(doneLabel).Append("\":[");
            for (int i = 0; i < done.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(OutputSerializer.EscapeJsonString(done[i])).Append('"');
            }
            sb.Append("],\"count\":").Append(done.Count);
            if (errors.Count > 0)
            {
                sb.Append(",\"errors\":[");
                for (int i = 0; i < errors.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(OutputSerializer.EscapeJsonString(errors[i])).Append('"');
                }
                sb.Append("],\"errorCount\":").Append(errors.Count);
            }
            extra?.Invoke(sb);
            sb.Append('}');
            return sb.ToString();
        }

        // Apply a batch of {name, value} field patches to an object via the
        // shared reflection + ConvertValue path. Single source of truth for
        // member resolution + writes — used by object_modify,
        // scriptableobject_create, and the three-surface gameobject_modify
        // jsonPatches surface (T22.1.4). Per-entry errors are accumulated in
        // `errors` and do not abort the batch; successfully-written field names
        // land in `modified`.
        internal static void ApplyFieldPatches(UnityEngine.Object target, string[] entries,
            bool allowStatic, List<string> modified, List<string> errors)
        {
            var type = target.GetType();

            // Record the target's pre-patch state once so a later
            // Undo.PerformUndo() reverts every reflection write in this batch.
            // Reflection SetValue is NOT auto-undoable — without this record a
            // Ctrl+Z would silently skip reflection patches, leaving
            // object_modify and the jsonPatchesPerGameObject surface of
            // gameobject_modify half-undoable (root diff undoable, component
            // patches not). NOTE: this only covers INSTANCE members — Unity's
            // Undo system does not snapshot static-field writes, so static
            // patches (which already require allow_static:true) remain
            // non-undoable by design.
            Undo.RecordObject(target, "MCP object_modify");

            foreach (var entry in entries)
            {
                var name = JsonBody.GetString(entry, "name");
                if (string.IsNullOrEmpty(name))
                {
                    errors.Add("Skipping entry with empty 'name'.");
                    continue;
                }

                // Resolve the member once (handles FieldInfo / PropertyInfo).
                // ResolveSettableMember returns null with a precise error in
                // `resolveError` for read-only / missing members.
                var (member, memberType, isStatic, isInitOnly, resolveError) =
                    ResolveSettableMember(type, name);
                if (resolveError != null)
                {
                    errors.Add(resolveError);
                    continue;
                }

                if ((isStatic || isInitOnly) && !allowStatic)
                {
                    var reason = isStatic ? "static" : "init-only/readonly";
                    errors.Add($"Refusing to write {reason} member '{name}' without allow_static:true.");
                    continue;
                }

                var valueRaw = JsonBody.GetRawValue(entry, "value");
                try
                {
                    var converted = ConvertValue(valueRaw, memberType);
                    SetMemberValue(member, target, converted);
                    modified.Add(name);
                }
                catch (Exception e)
                {
                    errors.Add($"Set '{name}' failed: {e.Message}");
                }
            }
        }

        // Resolve a public instance+static field or property by name. Returns
        // the member, its declared type, static/init-only flags, and a precise
        // error string (non-null when the member is absent or read-only).
        // Centralized so the FieldInfo/PropertyInfo branching lives once —
        // object_modify and scriptableobject_create both go through here.
        private static (MemberInfo member, Type memberType, bool isStatic, bool isInitOnly, string error)
            ResolveSettableMember(Type type, string name)
        {
            const BindingFlags PublicInstanceStatic =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

            var field = type.GetField(name, PublicInstanceStatic);
            if (field != null)
                return (field, field.FieldType, field.IsStatic, field.IsInitOnly, null);

            var prop = type.GetProperty(name, PublicInstanceStatic);
            if (prop == null)
                return (null, null, false, false,
                    $"No public field or property '{name}' on {type.Name}. Use type_schema to discover members.");

            if (prop.GetSetMethod(nonPublic: true) == null && prop.GetSetMethod() == null)
                return (null, null, false, false,
                    $"Property '{name}' on {type.Name} has no setter.");

            bool isStatic = prop.GetSetMethod() != null && prop.GetSetMethod().IsStatic;
            return (prop, prop.PropertyType, isStatic, false, null);
        }

        // Write a converted value to a FieldInfo or PropertyInfo. The single
        // member-type branch — replaces the copy-pasted `is FieldInfo … else
        // if PropertyInfo` blocks that lived in both object_modify and
        // scriptableobject_create.
        private static void SetMemberValue(MemberInfo member, object target, object value)
        {
            switch (member)
            {
                case FieldInfo f: f.SetValue(target, value); break;
                case PropertyInfo p: p.SetValue(target, value); break;
            }
        }

        // Append a JSON string array with proper escaping. Used by the SO tools
        // so field-applied / field-error lists render consistently.
        private static void AppendStringArray(StringBuilder sb, List<string> items)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(OutputSerializer.EscapeJsonString(items[i])).Append('"');
            }
            sb.Append(']');
        }
    }
}
