using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityOpenMcpBridge.ObjectRefs;
using UnityOpenMcpBridge.TypedTools;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class InvokeMethodTool
    {
        public static ToolDispatchResult Execute(string body)
        {
            var typeName = JsonBody.GetString(body, "type_name");
            var methodName = JsonBody.GetString(body, "method_name");
            var isStatic = JsonBody.GetBool(body, "is_static", false);
            var assemblyName = JsonBody.GetString(body, "assembly_name");
            // Selector aliasing (feedback-fable-31-07 §8): every tool that
            // EMITS object handles labels the id "objectId" (camelCase), while
            // this tool historically read "object_id" (snake_case) — forcing the
            // agent to rename on every chain. Accept all three spellings; prefer
            // the long-backed InstanceId form so ids > int.MaxValue (Unity
            // 6000.5+) resolve. instance_id is checked first as the canonical
            // selector name across the typed surface, then objectId (what other
            // tools emit), then object_id (legacy).
            long objectId = JsonBody.GetLongFlexible(body, "instance_id", 0);
            if (objectId == 0) objectId = JsonBody.GetLongFlexible(body, ObjectHandle.ObjectIdKey, 0);
            if (objectId == 0) objectId = JsonBody.GetLongFlexible(body, "object_id", 0);

            if (string.IsNullOrEmpty(typeName))
                return ToolDispatchResult.Fail("validation_error", "Field 'type_name' is required and must be non-empty");
            if (string.IsNullOrEmpty(methodName))
                return ToolDispatchResult.Fail("validation_error", "Field 'method_name' is required and must be non-empty");

            var type = FindType(typeName, assemblyName);
            if (type == null)
            {
                var hint = assemblyName != null ? $" in assembly '{assemblyName}'" : "";
                return ToolDispatchResult.Fail("type_not_found", $"Type '{typeName}' not found{hint}. " +
                    "Use fully qualified name including namespace. " +
                    "Use 'unity_open_mcp_find_members' to discover available types.");
            }

            // feedback #1 (2026-08-06) — reflection lookup uses BOTH Static and
            // Instance flags, regardless of the is_static selector. Previously
            // the flags were mutually exclusive (Static only when is_static was
            // explicitly true), so invoking a static method on a static class
            // without is_static resolved with Instance-only flags, missed every
            // static member, and reported "Available methods: Equals,
            // GetHashCode, GetType, ToString". The is_static flag is still
            // consulted below to decide whether to instantiate/resolve a target
            // — it no longer gates which methods reflection can see.
            var bindingFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            // M16 Plan 6 — overload + generic-arg resolution. Legacy callers
            // pass neither generic_arg_types nor arg_type_names; the previous
            // single-GetMethod path then runs unchanged so existing calls keep
            // working. When arg_type_names is supplied we disambiguate by
            // parameter type names; when generic_arg_types is supplied we
            // MakeGenericMethod so GetComponent<Rigidbody>() style calls work.
            var genericArgTypeNames = JsonBody.GetStringArray(body, "generic_arg_types");
            var argTypeNames = JsonBody.GetStringArray(body, "arg_type_names");

            MethodInfo method;
            if (argTypeNames != null && argTypeNames.Length > 0)
            {
                method = ResolveOverload(type, methodName, bindingFlags, argTypeNames, genericArgTypeNames);
                if (method == null)
                    return ToolDispatchResult.Fail("method_not_found",
                        $"No overload of '{methodName}' on '{type.FullName}' matches arg_type_names " +
                        $"[{string.Join(", ", argTypeNames)}]. Use find_members with kind:method to list overloads.");
            }
            else
            {
                // Combined Static|Instance flags can make GetMethod throw
                // AmbiguousMatchException when a name exists in both static and
                // instance form; resolve explicitly by selecting the candidate
                // that matches the is_static selector, falling back to the
                // first. (feedback #1 — static methods must be resolvable
                // without the caller setting is_static.)
                try
                {
                    method = type.GetMethod(methodName, bindingFlags);
                }
                catch (AmbiguousMatchException)
                {
                    method = ResolveByName(type, methodName, bindingFlags, isStatic);
                }
                if (method == null)
                    return ToolDispatchResult.Fail("method_not_found",
                        $"Method '{methodName}' not found on type '{type.FullName}'. " +
                        $"Available methods: {string.Join(", ", type.GetMethods(bindingFlags).Select(m => (m.IsStatic ? "static " : "") + m.Name).Distinct().Take(10))}" +
                        (type.GetMethods(bindingFlags).Select(m => m.Name).Distinct().Count() > 10 ? "..." : ""));

                // Generic method with explicit type args: bind them now so the
                // parameter types resolve correctly for arg conversion below.
                if (genericArgTypeNames != null && genericArgTypeNames.Length > 0)
                {
                    if (!method.IsGenericMethod)
                        return ToolDispatchResult.Fail("generic_arg_mismatch",
                            $"Method '{methodName}' is not generic, but generic_arg_types were supplied.");
                    if (method.GetGenericArguments().Length != genericArgTypeNames.Length)
                        return ToolDispatchResult.Fail("generic_arg_mismatch",
                            $"Method '{methodName}' has {method.GetGenericArguments().Length} generic parameter(s) " +
                            $"but {genericArgTypeNames.Length} generic_arg_types were supplied.");
                    method = BindGenericMethod(method, genericArgTypeNames);
                    if (method == null)
                        return ToolDispatchResult.Fail("generic_arg_not_found",
                            "One or more generic_arg_types could not be resolved. " +
                            "Use fully qualified type names.");
                }
            }

            var args = JsonBody.ParseArgsArray(body, "args");
            var parameters = method.GetParameters();

            object target = null;
            if (!isStatic)
            {
                if (objectId != 0)
                {
                    target = ObjectHandle.Resolve(objectId, type.FullName, null, null, null, null,
                        out var resolveError);
                    if (target == null)
                        return ToolDispatchResult.Fail("object_not_found",
                            $"Could not resolve object_id {objectId} as target for instance method: {resolveError}");
                    if (!type.IsInstanceOfType(target))
                        return ToolDispatchResult.Fail("type_mismatch",
                            $"Resolved object (type '{target.GetType().FullName}') is not assignable to '{type.FullName}'.");
                }
                else
                {
                    try
                    {
                        target = Activator.CreateInstance(type);
                    }
                    catch (Exception e)
                    {
                        return ToolDispatchResult.Fail("instantiation_error",
                            $"Cannot create instance of '{type.FullName}': {e.Message}. " +
                            "Use is_static: true for static methods, or pass object_id to target a live object.");
                    }
                }
            }

            try
            {
                // feedback-01-08-glm §2 — arg conversion now resolves Object/
                // Scene/Vector selectors and throws precise messages on a miss.
                // Keep it inside the try so those surface as invocation_error
                // (not an uncaught dispatch crash).
                var invokeArgs = ConvertArgs(args, parameters);
                var result = method.Invoke(target, invokeArgs);
                var output = OutputSerializer.Serialize(result, BuildSerializeOptions(body));
                return ToolDispatchResult.Ok(output);
            }
            catch (TargetInvocationException tie)
            {
                return ToolDispatchResult.Fail("invocation_error", tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        private static SerializeOptions BuildSerializeOptions(string body)
        {
            var maxDepth = JsonBody.GetInt(body, "max_depth", 4);
            var maxItems = JsonBody.GetInt(body, "max_items", 100);
            return new SerializeOptions
            {
                MaxDepth = maxDepth <= 0 ? 4 : maxDepth,
                MaxListItems = maxItems <= 0 ? 100 : maxItems,
            };
        }

        private static Type FindType(string typeName, string assemblyName)
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
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == typeName)
                            return t;
                    }
                }
                catch { }
            }

            return null;
        }

        // feedback #1 — disambiguate by name when GetMethod throws
        // AmbiguousMatchException (a method name exists in both static and
        // instance form under the combined Static|Instance flags). Prefer the
        // candidate whose static-ness matches the is_static selector; fall back
        // to the first match so the call is still deterministic.
        private static MethodInfo ResolveByName(Type type, string methodName, BindingFlags bindingFlags, bool isStatic)
        {
            MethodInfo[] candidates;
            try { candidates = type.GetMethods(bindingFlags); }
            catch { return null; }
            MethodInfo fallback = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Name != methodName) continue;
                if (candidates[i].IsStatic == isStatic) return candidates[i];
                fallback = fallback ?? candidates[i];
            }
            return fallback;
        }

        // M16 Plan 6 — pick the overload whose parameter types match
        // arg_type_names (full name or simple name), then optionally bind
        // generic type arguments when the method is generic. Returns null when
        // no overload matches.
        private static MethodInfo ResolveOverload(Type type, string methodName, BindingFlags bindingFlags,
            string[] argTypeNames, string[] genericArgTypeNames)
        {
            MethodInfo[] candidates;
            try { candidates = type.GetMethods(bindingFlags); }
            catch { return null; }

            MethodInfo fallback = null;
            foreach (var m in candidates)
            {
                if (m.Name != methodName) continue;
                var parms = m.GetParameters();
                if (parms.Length != argTypeNames.Length) continue;

                bool match = true;
                for (int i = 0; i < parms.Length; i++)
                {
                    if (!TypeNameMatches(parms[i].ParameterType, argTypeNames[i]))
                    {
                        match = false;
                        break;
                    }
                }
                if (!match) continue;

                // Prefer the non-generic match when generic args weren't
                // requested; otherwise look for the generic one we can bind.
                if (genericArgTypeNames == null || genericArgTypeNames.Length == 0)
                {
                    if (!m.IsGenericMethod) return m;
                    fallback ??= m;
                }
                else
                {
                    if (!m.IsGenericMethod) continue;
                    if (m.GetGenericArguments().Length != genericArgTypeNames.Length) continue;
                    var bound = BindGenericMethod(m, genericArgTypeNames);
                    if (bound != null) return bound;
                }
            }
            return fallback;
        }

        private static bool TypeNameMatches(Type paramType, string requestedName)
        {
            if (string.IsNullOrEmpty(requestedName)) return false;
            // Full-name match first; fall back to simple name (matching FindType).
            if (!string.IsNullOrEmpty(paramType.FullName)
                && paramType.FullName == requestedName) return true;
            if (paramType.Name == requestedName) return true;
            // Accept common CLR aliases (int/Int32) so agents can use either form.
            if (ClrAliases.TryGetValue(requestedName, out var aliasName)
                && (paramType.Name == aliasName || paramType.FullName == aliasName)) return true;
            return false;
        }

        private static MethodInfo BindGenericMethod(MethodInfo method, string[] genericArgTypeNames)
        {
            var typeArgs = new Type[genericArgTypeNames.Length];
            for (int i = 0; i < genericArgTypeNames.Length; i++)
            {
                typeArgs[i] = FindType(genericArgTypeNames[i], null);
                if (typeArgs[i] == null) return null;
            }
            try { return method.MakeGenericMethod(typeArgs); }
            catch (ArgumentException)
            {
                // Constraint violation — the type args don't satisfy the
                // method's generic constraints. Surface as not-found so the
                // caller gets a discoverable error.
                return null;
            }
        }

        private static readonly Dictionary<string, string> ClrAliases = new()
        {
            { "int", "Int32" },
            { "uint", "UInt32" },
            { "long", "Int64" },
            { "ulong", "UInt64" },
            { "short", "Int16" },
            { "ushort", "UInt16" },
            { "byte", "Byte" },
            { "sbyte", "SByte" },
            { "float", "Single" },
            { "double", "Double" },
            { "decimal", "Decimal" },
            { "bool", "Boolean" },
            { "char", "Char" },
            { "string", "String" },
            { "object", "Object" },
        };

        private static object[] ConvertArgs(List<object> args, ParameterInfo[] parameters)
        {
            if (args == null || args.Count == 0) return Array.Empty<object>();
            if (parameters == null || parameters.Length == 0) return Array.Empty<object>();

            var count = Math.Min(args.Count, parameters.Length);
            var result = new object[count];
            for (var i = 0; i < count; i++)
                result[i] = ConvertArg(args[i], parameters[i].ParameterType);
            return result;
        }

        private static object ConvertArg(object value, Type targetType)
        {
            if (value == null)
            {
                if (targetType.IsValueType)
                    return Activator.CreateInstance(targetType);
                return null;
            }

            // feedback-01-08-glm §2 — UnityEngine.Object parameters. Previously
            // only handle JSON containing an "objectId" key was resolved (the
            // ObjectHandle.LooksLikeHandle predicate); a bare integer id (parsed
            // by ReadJsonValue as `long`) or a selector like {"path":...} /
            // {"instance_id":N} fell through to `return value;` and
            // method.Invoke rejected it ("Object of type 'System.Int64' cannot
            // be converted to type 'UnityEngine.GameObject'"). ResolveJson
            // already handles bare integers, {"objectId":N}, and the
            // {"instance_id"/"object_id"/"path"/"name"/...} selector aliases,
            // so route every Object-typed arg through it.
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                // Bare integer id (long from ReadJsonValue, or int/double) →
                // resolve via the instance-id path directly.
                if (value is long ll)
                {
                    var byId = InstanceId.ToObject(ll);
                    if (byId != null) return byId;
                    throw new ArgumentException(
                        $"No live UnityEngine.Object found for instance id {ll}. Instance IDs change " +
                        "on domain reload; re-acquire the object and retry.");
                }
                if (value is int ii)
                {
                    var byId = InstanceId.ToObject(ii);
                    if (byId != null) return byId;
                    throw new ArgumentException(
                        $"No live UnityEngine.Object found for instance id {ii}. Instance IDs change " +
                        "on domain reload; re-acquire the object and retry.");
                }
                // Any JSON-object string (handle with objectId, or a {"path":..}
                // / {"name":..} / {"instance_id":N} selector) → resolve via the
                // shared handle resolver that backs the instance target.
                if (value is string sv && sv.TrimStart().StartsWith("{"))
                {
                    var resolved = ObjectHandle.ResolveJson(sv, out var error);
                    if (resolved != null) return resolved;
                    throw new ArgumentException(error ?? $"Could not resolve object handle: {sv}");
                }
            }

            // feedback-01-08-glm §2 — Scene struct parameter. Unity's
            // SceneManager.SetActiveScene(Scene) / MoveGameObjectToScene(GO,
            // Scene) take a Scene as a POSITIONAL arg, which method.Invoke
            // cannot reconstruct from a JSON scalar. Accept a scene selector
            // and resolve it against the loaded scenes.
            if (targetType == typeof(Scene))
                return ResolveSceneArg(value);

            if (targetType == typeof(string))
                return value is string s ? s : value.ToString();
            if (targetType == typeof(int))
                return value is long l ? (int)l : value is double d ? (int)d : Convert.ToInt32(value);
            if (targetType == typeof(float))
                return Convert.ToSingle(value);
            if (targetType == typeof(double))
                return Convert.ToDouble(value);
            if (targetType == typeof(bool))
                return value is bool b ? b : Convert.ToBoolean(value);
            if (targetType == typeof(long))
                return Convert.ToInt64(value);
            if (targetType == typeof(byte))
                return Convert.ToByte(value);
            if (targetType == typeof(short))
                return Convert.ToInt16(value);
            if (targetType == typeof(uint))
                return Convert.ToUInt32(value);
            if (targetType == typeof(ulong))
                return Convert.ToUInt64(value);
            if (targetType.IsEnum)
                return Enum.Parse(targetType, value.ToString(), true);

            // Vector / Color / Quaternion — mirror ReflectionScriptsObjectsTools
            // so invoke_method can drive APIs taking these structs (e.g. physics
            // helpers). Accept both [x,y,z] and {"x":..} forms via the shared
            // MaterialTools.ParseFloatArray parser.
            var vecColor = TryConvertVectorColor(value, targetType);
            if (vecColor.success) return vecColor.value;

            // Terminal fallback: IConvertible scalar conversion so a long/double
            // arg reaches an IConvertible target without leaking through to the
            // raw-value return (which method.Invoke would reject).
            if (typeof(IConvertible).IsAssignableFrom(targetType))
                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);

            return value;
        }

        /// <summary>
        /// Resolve a <see cref="Scene"/> argument from a selector. Accepts a
        /// scene path/name/object form: <c>{"path":"..."}</c>,
        /// <c>{"scene_path":"..."}</c>, <c>{"scene_name":"..."}</c>,
        /// <c>{"name":"..."}</c>, or a bare string path/name. Throws a clear
        /// ArgumentException (surfaced as invocation_error) when the scene is
        /// not loaded — exactly the static APIs (SetActiveScene /
        /// MoveGameObjectToScene) the field report was blocked on.
        /// </summary>
        private static Scene ResolveSceneArg(object value)
        {
            string path = null;
            string name = null;
            if (value is string raw)
            {
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("{"))
                {
                    path = JsonBody.GetString(trimmed, "path")
                        ?? JsonBody.GetString(trimmed, "scene_path")
                        ?? JsonBody.GetString(trimmed, "scenePath");
                    name = JsonBody.GetString(trimmed, "scene_name")
                        ?? JsonBody.GetString(trimmed, "sceneName")
                        ?? JsonBody.GetString(trimmed, "name");
                }
                else
                {
                    // Bare string: treat as a path if it contains '/', else a name.
                    if (trimmed.IndexOf('/') >= 0) path = trimmed;
                    else name = trimmed;
                }
            }
            else if (value is long || value is int || value is double)
            {
                // A numeric Scene arg has no meaningful mapping; surface a clear
                // error rather than a default(Scene) silently.
                throw new ArgumentException(
                    $"Scene argument must be a scene path/name selector ({{\"path\":\"...\"}} or " +
                    $"{{\"scene_name\":\"...\"}}); got a numeric value '{value}'.");
            }

            if (!string.IsNullOrEmpty(path))
            {
                var sceneByPath = EditorSceneManager.GetSceneByPath(path);
                if (sceneByPath.IsValid() && sceneByPath.isLoaded)
                    return sceneByPath;
            }
            if (!string.IsNullOrEmpty(name))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded && s.name == name)
                        return s;
                }
            }

            var described = !string.IsNullOrEmpty(path) ? $"path '{path}'"
                : !string.IsNullOrEmpty(name) ? $"name '{name}'"
                : "<empty>";
            throw new ArgumentException(
                $"Scene {described} is not loaded. Load it first via unity_open_mcp_scene_open " +
                "(OpenSceneMode.Additive) so it can be passed as a Scene argument.");
        }

        private static (bool success, object value) TryConvertVectorColor(object value, Type targetType)
        {
            if (targetType != typeof(Vector2) && targetType != typeof(Vector3)
                && targetType != typeof(Vector4) && targetType != typeof(Color)
                && targetType != typeof(Quaternion))
                return (false, null);

            // ParseFloatArray accepts both "[x,y,z]" and "{x:..,y:..}" string
            // forms. ReadJsonValue returns raw substrings for those, so a vector
            // arg arrives here as a string; numbers/doubles are not vectors.
            string raw = value as string;
            if (raw == null && (value is long || value is double || value is bool))
                return (false, null);
            if (raw == null) raw = value.ToString();

            var p = MaterialTools.ParseFloatArray(raw);
            if (p == null)
                throw new FormatException($"{targetType.Name} value must be [x,y(,z(,w))] or {{x:..}}; got '{raw}'.");

            if (targetType == typeof(Vector2))
            {
                if (p.Length < 2) throw new FormatException("Vector2 value must be [x,y].");
                return (true, new Vector2(p[0], p[1]));
            }
            if (targetType == typeof(Vector3))
            {
                if (p.Length < 3) throw new FormatException("Vector3 value must be [x,y,z].");
                return (true, new Vector3(p[0], p[1], p[2]));
            }
            if (targetType == typeof(Vector4))
            {
                if (p.Length < 4) throw new FormatException("Vector4 value must be [x,y,z,w].");
                return (true, new Vector4(p[0], p[1], p[2], p[3]));
            }
            if (targetType == typeof(Color))
            {
                if (p.Length < 3) throw new FormatException("Color value must be [r,g,b] or [r,g,b,a].");
                float a = p.Length >= 4 ? p[3] : 1f;
                return (true, new Color(p[0], p[1], p[2], a));
            }
            // Quaternion
            if (p.Length >= 4) return (true, new Quaternion(p[0], p[1], p[2], p[3]));
            if (p.Length >= 3) return (true, Quaternion.Euler(p[0], p[1], p[2]));
            throw new FormatException("Quaternion value must be [x,y,z,w] or euler [x,y,z].");
        }
    }
}
