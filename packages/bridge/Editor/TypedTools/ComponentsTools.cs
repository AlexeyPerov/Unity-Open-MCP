using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;


namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 2 — typed component lifecycle tools. Covers add / destroy /
    // get / modify / list_all. Mutation tools run through the gate envelope
    // with paths_hint. `get` and `list_all` are gate-free reads.
    //
    // Components are addressed by type name + the GameObject they live on; an
    // optional instance_id targets a specific component instance (for types
    // that allow multiples, e.g. Collider or AudioSource).
    //
    // Serialize field/property values via SerializedObject — Unity's
    // canonical serialization surface — so MonoBehaviour/ScriptableObject
    // custom fields round-trip the same way they do in the Inspector.
    public static class ComponentsTools
    {
        public static ToolDispatchResult Add(string body)
        {
            var resolved = GameObjectsTools.ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            var typeNames = JsonBody.GetStringArray(body, "component_types");
            if (typeNames == null || typeNames.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'component_types' is required and must be a non-empty array of component type names " +
                    "(full name or class name).");

            var added = new List<string>();
            var errors = new List<string>();

            foreach (var rawType in typeNames)
            {
                if (string.IsNullOrWhiteSpace(rawType)) continue;
                var type = ResolveComponentType(rawType);
                if (type == null)
                {
                    errors.Add($"Type '{rawType}' not found. Use unity_open_mcp_component_list_all to discover attachable types.");
                    continue;
                }
                if (!typeof(Component).IsAssignableFrom(type))
                {
                    errors.Add($"Type '{rawType}' ({type.FullName}) is not a Component.");
                    continue;
                }

                Component comp;
                try
                {
                    comp = Undo.AddComponent(go, type);
                }
                catch (System.Exception e)
                {
                    errors.Add($"Add '{rawType}' failed: {e.Message}");
                    continue;
                }
                if (comp == null)
                {
                    errors.Add($"Component '{rawType}' was not added (it may disallow multiples and already exist).");
                    continue;
                }
                added.Add($"{type.FullName}#{InstanceId.Of(comp)}");
            }

            if (added.Count > 0)
            {
                EditorUtility.SetDirty(go);
                MarkActiveSceneDirty(go);
            }

            return ToolDispatchResult.Ok(BuildOpResult(added, errors, "added"));
        }

        public static ToolDispatchResult Destroy(string body)
        {
            var resolved = GameObjectsTools.ResolveInstance(body);
            if (!resolved.Ok) return resolved.Result;
            var go = resolved.GameObject;

            var typeNames = JsonBody.GetStringArray(body, "component_types");
            if (typeNames == null || typeNames.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'component_types' is required and must be a non-empty array of component type names to remove.");

            var removed = new List<string>();
            var errors = new List<string>();

            foreach (var rawType in typeNames)
            {
                if (string.IsNullOrWhiteSpace(rawType)) continue;
                var type = ResolveComponentType(rawType);
                if (type == null)
                {
                    errors.Add($"Type '{rawType}' not found; cannot destroy.");
                    continue;
                }
                var comp = go.GetComponent(type);
                if (comp == null)
                {
                    errors.Add($"No component of type '{rawType}' on GameObject '{go.name}'.");
                    continue;
                }
                try
                {
                    Undo.DestroyObjectImmediate(comp);
                    removed.Add(type.FullName);
                }
                catch (System.Exception e)
                {
                    errors.Add($"Destroy '{rawType}' failed: {e.Message}");
                }
            }

            if (removed.Count > 0)
            {
                EditorUtility.SetDirty(go);
                MarkActiveSceneDirty(go);
            }

            return ToolDispatchResult.Ok(BuildOpResult(removed, errors, "destroyed"));
        }

        // Component get returns the serialized field/property list of one
        // component on a GameObject, plus its type/enable state. Read-only,
        // gate-free. Token-bounded by max_fields. Use find / list_all to
        // discover component type names first.
        public static ToolDispatchResult Get(string body)
        {
            var resolved = ResolveComponent(body, out var component, out var resolveError);
            if (!resolved) return resolveError;

            int maxFields = JsonBody.GetInt(body, "max_fields", 100);
            if (maxFields < 1) maxFields = 1;
            var includeProperties = JsonBody.GetBool(body, "include_properties", true);

            var sb = new StringBuilder(1024);
            sb.Append("{\"type\":\"").Append(TypedTargets.Esc(component.GetType().FullName));
            sb.Append("\",\"name\":\"").Append(TypedTargets.Esc(component.GetType().Name));
            sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(component));
            sb.Append(",\"enabled\":").Append(IsEnabled(component) ? "true" : "false");

            var so = new SerializedObject(component);
            var prop = so.GetIterator();
            sb.Append(",\"fields\":[");

            int emitted = 0;
            int truncated = 0;
            bool first = true;
            for (var enter = prop.NextVisible(true); enter; enter = prop.NextVisible(false))
            {
                if (emitted >= maxFields) { truncated++; continue; }
                if (!first) sb.Append(',');
                first = false;
                AppendSerializedProperty(sb, prop);
                emitted++;
            }

            if (includeProperties)
            {
                sb.Append("],\"properties\":[");
                AppendPublicProperties(sb, component, ref emitted, maxFields, ref truncated);
            }
            else
            {
                sb.Append("],\"properties\":[]");
            }

            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Component modify updates serialized field(s) on a component by path
        // via SerializedObject. `fields` is an array of {path, value, type}
        // entries where `path` is the SerializedProperty.propertyPath (e.g.
        // "m_Color", "m_Color.r", "mesh[0]"). Each entry is applied in order;
        // per-entry errors are reported but do not abort the batch.
        public static ToolDispatchResult Modify(string body)
        {
            var resolved = ResolveComponent(body, out var component, out var resolveError);
            if (!resolved) return resolveError;

            var entries = JsonBody.GetObjectArray(body, "fields");
            if (entries == null || entries.Length == 0)
                return ToolDispatchResult.Fail("missing_parameter",
                    "'fields' is required and must be a non-empty array of { path, value, type? } patches. " +
                    "Use component_get first to discover valid paths.");

            var so = new SerializedObject(component);
            var modified = new List<string>();
            var errors = new List<string>();

            foreach (var entry in entries)
            {
                var path = JsonBody.GetString(entry, "path");
                if (string.IsNullOrEmpty(path))
                {
                    errors.Add("Skipping entry with empty 'path'.");
                    continue;
                }
                var sp = so.FindProperty(path);
                if (sp == null)
                {
                    errors.Add($"Property '{path}' not found on {component.GetType().Name}.");
                    continue;
                }

                var valueRaw = JsonBody.GetRawValue(entry, "value");
                var typeHint = JsonBody.GetString(entry, "type");
                try
                {
                    WriteSerializedProperty(sp, valueRaw, typeHint);
                    modified.Add(path);
                }
                catch (System.Exception e)
                {
                    errors.Add($"Set '{path}' failed: {e.Message}");
                }
            }

            if (modified.Count > 0)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(component);
                MarkActiveSceneDirty(component.gameObject);
            }

            return ToolDispatchResult.Ok(BuildOpResult(modified, errors, "modified"));
        }

        // Component list_all returns a token-bounded catalog of component
        // types that can be attached via AddComponent — built-in + project
        // MonoBehaviours. Read-only, gate-free. Use the optional query
        // substring to narrow by namespace/type-name (e.g. "Rigidbody",
        // "UnityEngine", "MyNamespace.MyBehaviour").
        public static ToolDispatchResult ListAll(string body)
        {
            int maxResults = JsonBody.GetInt(body, "max_results", 200);
            if (maxResults < 1) maxResults = 1;
            var query = JsonBody.GetString(body, "query");
            var includeBuiltIn = JsonBody.GetBool(body, "include_builtin", true);
            var includeProject = JsonBody.GetBool(body, "include_project", true);

            var seen = new HashSet<string>();
            var matches = new List<ComponentTypeEntry>();

            // Built-in components live across MANY Unity module assemblies
            // (PhysicsModule → Rigidbody/Colliders, AudioModule, ParticleSystem
            // modules, …), not just CoreModule. Scanning every loaded
            // UnityEngine.*/UnityEditor.* assembly avoids hard-coding — and
            // drift across Unity versions — while still surfacing physics types
            // the original 2-assembly scan silently dropped. Two passes keep
            // built-ins surfaced before project MonoBehaviours.
            if (includeBuiltIn)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (matches.Count >= maxResults) break;
                    var name = asm.GetName().Name;
                    if (!IsUnityFrameworkAssembly(name)) continue;
                    ScanAssembly(asm, query, seen, matches, maxResults);
                }
            }

            // Project MonoBehaviours — every NON-Unity loaded assembly that
            // defines types deriving from Component. Includes package assemblies.
            if (includeProject && matches.Count < maxResults)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (matches.Count >= maxResults) break;
                    if (IsUnityFrameworkAssembly(asm.GetName().Name)) continue;
                    ScanAssembly(asm, query, seen, matches, maxResults);
                }
            }

            int truncated = matches.Count > maxResults ? matches.Count - maxResults : 0;
            var limited = matches.Count > maxResults ? matches.GetRange(0, maxResults) : matches;

            var sb = new StringBuilder(256 + limited.Count * 96);
            sb.Append("{\"types\":[");
            for (int i = 0; i < limited.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var m = limited[i];
                sb.Append("{\"fullName\":\"").Append(TypedTargets.Esc(m.FullName));
                sb.Append("\",\"name\":\"").Append(TypedTargets.Esc(m.Name));
                sb.Append("\",\"namespace\":\"").Append(TypedTargets.Esc(m.Namespace ?? ""));
                sb.Append("\",\"assembly\":\"").Append(TypedTargets.Esc(m.Assembly));
                sb.Append("\",\"builtin\":").Append(m.BuiltIn ? "true" : "false").Append("}");
            }
            sb.Append("],\"count\":").Append(limited.Count);
            sb.Append(",\"truncated\":").Append(truncated);
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ----------------------------- helpers -----------------------------

        struct ComponentTypeEntry
        {
            public string FullName;
            public string Name;
            public string Namespace;
            public string Assembly;
            public bool BuiltIn;
        }

        // Resolve a component type by full name (preferred) or class name
        // fallback. Searches every loaded assembly so project MonoBehaviours
        // resolve as well as built-in components.
        public static System.Type ResolveComponentType(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return null;

            // Fast path: full-name exact match.
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(rawName);
                if (type != null) return type;
            }

            // Class-name fallback (no namespace). Returns the first match.
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in SafeGetTypes(asm))
                {
                    if (type.Name == rawName) return type;
                }
            }
            return null;
        }

        private static IEnumerable<System.Type> SafeGetTypes(System.Reflection.Assembly asm)
        {
            // Some assemblies throw on GetTypes — not only ReflectionTypeLoadException
            // (missing dependency in a package assembly) but also FileNotFoundException
            // / BadImageFormatException from tooling assemblies pulled into the editor
            // (e.g. Microsoft.CodeAnalysis.BuildTasks). Catch broadly so one broken
            // assembly never aborts the whole component catalog; for the reflection-
            // load case we still salvage the partial Types array.
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types != null ? FilterNull(e.Types) : System.Array.Empty<System.Type>();
            }
            catch (System.Exception)
            {
                return System.Array.Empty<System.Type>();
            }
        }

        private static IEnumerable<System.Type> FilterNull(System.Type[] types)
        {
            foreach (var t in types) if (t != null) yield return t;
        }

        // True for the Unity framework assembly roots (UnityEngine, UnityEditor)
        // and every module assembly under them (UnityEngine.PhysicsModule,
        // UnityEngine.AudioModule, …). Used to split the built-in vs project
        // scan passes in ListAll and to classify an entry as builtin.
        private static bool IsUnityFrameworkAssembly(string name)
            => name == "UnityEngine" || name == "UnityEditor"
                || name.StartsWith("UnityEngine.") || name.StartsWith("UnityEditor.");

        private static void ScanAssembly(System.Reflection.Assembly asm, string query,
            HashSet<string> seen, List<ComponentTypeEntry> sink, int maxResults)
        {
            var asmName = asm.GetName().Name;
            var isBuiltIn = IsUnityFrameworkAssembly(asmName);

            foreach (var type in SafeGetTypes(asm))
            {
                if (sink.Count >= maxResults) return;
                if (type == null) continue;
                // Touching a broken type's metadata (e.g. a Roslyn/MSBuild type
                // missing a transitive dependency) throws TypeLoadException at
                // the point of resolution — during enumeration, not during
                // GetTypes(). Isolate each type so one bad type never aborts
                // the whole catalog scan.
                ComponentTypeEntry? entry;
                try
                {
                    if (!typeof(Component).IsAssignableFrom(type)) continue;
                    if (type.IsAbstract) continue;
                    if (type.IsGenericType) continue;
                    // Skip internal Unity sub-types that can't be added directly.
                    if (type.ContainsGenericParameters) continue;
                    var fullName = type.FullName;
                    if (string.IsNullOrEmpty(fullName)) continue;
                    if (!seen.Add(fullName)) continue;
                    if (!string.IsNullOrEmpty(query))
                    {
                        if (fullName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0
                            && type.Name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }
                    entry = new ComponentTypeEntry
                    {
                        FullName = fullName,
                        Name = type.Name,
                        Namespace = type.Namespace,
                        Assembly = asmName,
                        BuiltIn = isBuiltIn
                    };
                }
                catch (System.Exception)
                {
                    // Type metadata could not be resolved — skip it and move on.
                    continue;
                }
                sink.Add(entry.Value);
            }
        }

        // Resolve a single component on a GameObject: by instance_id (a
        // specific component), or by type_name (first match).
        private static bool ResolveComponent(string body,
            out Component component, out ToolDispatchResult error)
        {
            component = null;
            error = null;

            var goResolved = GameObjectsTools.ResolveInstance(body);
            if (!goResolved.Ok) { error = goResolved.Result; return false; }
            var go = goResolved.GameObject;

            // component_instance_id is a separate key from instance_id so a
            // single body can resolve both the host GameObject (instance_id)
            // and a specific component on it (component_instance_id).
            var componentInstanceId = JsonBody.GetLongFlexible(body, "component_instance_id", 0);
            if (componentInstanceId != 0)
            {
                var found = InstanceId.ToObject(componentInstanceId) as Component;
                if (found == null || found.gameObject != go)
                {
                    error = ToolDispatchResult.Fail("component_not_found",
                        $"No component with component_instance_id={componentInstanceId} on GameObject '{go.name}'.");
                    return false;
                }
                component = found;
                return true;
            }

            var typeName = JsonBody.GetString(body, "type_name");
            if (string.IsNullOrEmpty(typeName))
            {
                error = ToolDispatchResult.Fail("missing_parameter",
                    "Provide 'type_name' (or 'component_instance_id') to identify the component.");
                return false;
            }

            var type = ResolveComponentType(typeName);
            if (type == null)
            {
                error = ToolDispatchResult.Fail("type_not_found",
                    $"Component type '{typeName}' not found. Use unity_open_mcp_component_list_all to discover attachable types.");
                return false;
            }

            component = go.GetComponent(type);
            if (component == null)
            {
                error = ToolDispatchResult.Fail("component_not_found",
                    $"GameObject '{go.name}' has no component of type '{typeName}'.");
                return false;
            }
            return true;
        }

        private static bool IsEnabled(Component component)
        {
            if (component is Behaviour b) return b.enabled;
            if (component is Collider c) return c.enabled;
            return true;
        }

        private static void AppendSerializedProperty(StringBuilder sb, SerializedProperty prop)
        {
            sb.Append("{\"path\":\"").Append(TypedTargets.Esc(prop.propertyPath));
            sb.Append("\",\"type\":\"").Append(prop.propertyType.ToString());
            sb.Append("\",\"value\":").Append(ReadSerializedValue(prop));
            sb.Append('}');
        }

        private static string ReadSerializedValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return prop.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return "\"" + TypedTargets.Esc(prop.stringValue) + "\"";
                case SerializedPropertyType.Color:
                    {
                        var c = prop.colorValue;
                        var sb = new StringBuilder(48);
                        sb.Append('[').Append(c.r.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.g.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.b.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.a.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var v = prop.vector2Value;
                        var sb = new StringBuilder(32);
                        sb.Append('[').Append(v.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.y.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var v = prop.vector3Value;
                        var sb = new StringBuilder(48);
                        sb.Append('[').Append(v.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.y.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.z.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var v = prop.vector4Value;
                        var sb = new StringBuilder(64);
                        sb.Append('[').Append(v.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.y.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.z.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v.w.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        var v = prop.quaternionValue.eulerAngles;
                        var sb = new StringBuilder(48);
                        sb.Append("{\"euler\":[");
                        sb.Append(v.x.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(v.y.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(v.z.ToString("R", CultureInfo.InvariantCulture)).Append("]}");
                        return sb.ToString();
                    }
                case SerializedPropertyType.Enum:
                    return "\"" + TypedTargets.Esc(prop.enumNames[prop.enumValueIndex]) + "\"";
                case SerializedPropertyType.ObjectReference:
                    {
                        var obj = prop.objectReferenceValue;
                        if (obj == null) return "null";
                        var sb = new StringBuilder(96);
                        sb.Append("{\"name\":\"").Append(TypedTargets.Esc(obj.name));
                        sb.Append("\",\"type\":\"").Append(TypedTargets.Esc(obj.GetType().FullName));
                        sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(obj)).Append("}");
                        return sb.ToString();
                    }
                case SerializedPropertyType.LayerMask:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Rect:
                    {
                        var r = prop.rectValue;
                        var sb = new StringBuilder(80);
                        sb.Append("{\"x\":").Append(r.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(",\"y\":").Append(r.y.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(",\"width\":").Append(r.width.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(",\"height\":").Append(r.height.ToString("R", CultureInfo.InvariantCulture)).Append("}");
                        return sb.ToString();
                    }
                case SerializedPropertyType.ArraySize:
                    return prop.intValue.ToString(CultureInfo.InvariantCulture);
                default:
                    // For nested containers, surface the path so the agent
                    // can drill in via a path-scoped get later.
                    return "{\"note\":\"unsupported_or_container\"}";
            }
        }

        private static void WriteSerializedProperty(SerializedProperty sp, string valueRaw, string typeHint)
        {
            if (valueRaw == null) valueRaw = "null";
            valueRaw = valueRaw.Trim();

            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer:
                    sp.intValue = ParseInt(valueRaw);
                    break;
                case SerializedPropertyType.Boolean:
                    sp.boolValue = valueRaw == "true";
                    break;
                case SerializedPropertyType.Float:
                    sp.floatValue = ParseFloat(valueRaw);
                    break;
                case SerializedPropertyType.String:
                    sp.stringValue = StripQuotes(valueRaw);
                    break;
                case SerializedPropertyType.Color:
                    {
                        var parts = MaterialTools.ParseFloatArray(valueRaw);
                        if (parts == null || parts.Length < 3)
                            throw new System.FormatException("color value must be [r,g,b] or [r,g,b,a].");
                        float a = parts.Length >= 4 ? parts[3] : 1f;
                        sp.colorValue = new Color(parts[0], parts[1], parts[2], a);
                        break;
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var parts = MaterialTools.ParseFloatArray(valueRaw);
                        if (parts == null || parts.Length < 2)
                            throw new System.FormatException("Vector2 value must be [x,y].");
                        sp.vector2Value = new Vector2(parts[0], parts[1]);
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var parts = MaterialTools.ParseFloatArray(valueRaw);
                        if (parts == null || parts.Length < 3)
                            throw new System.FormatException("Vector3 value must be [x,y,z].");
                        sp.vector3Value = new Vector3(parts[0], parts[1], parts[2]);
                        break;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var parts = MaterialTools.ParseFloatArray(valueRaw);
                        if (parts == null || parts.Length < 4)
                            throw new System.FormatException("Vector4 value must be [x,y,z,w].");
                        sp.vector4Value = new Vector4(parts[0], parts[1], parts[2], parts[3]);
                        break;
                    }
                case SerializedPropertyType.Quaternion:
                    {
                        var parts = MaterialTools.ParseFloatArray(valueRaw);
                        if (parts != null && parts.Length >= 4)
                        {
                            sp.quaternionValue = new Quaternion(parts[0], parts[1], parts[2], parts[3]);
                            break;
                        }
                        // Euler fallback [x,y,z] degrees.
                        if (parts != null && parts.Length >= 3)
                        {
                            sp.quaternionValue = Quaternion.Euler(parts[0], parts[1], parts[2]);
                            break;
                        }
                        throw new System.FormatException("Quaternion value must be [x,y,z,w] or euler [x,y,z].");
                    }
                case SerializedPropertyType.Enum:
                    {
                        if (!string.IsNullOrEmpty(typeHint) && typeHint.ToLowerInvariant() == "name")
                        {
                            var idx = System.Array.IndexOf(sp.enumNames, StripQuotes(valueRaw));
                            if (idx < 0)
                                throw new System.FormatException($"Enum value '{valueRaw}' not in [{string.Join(", ", sp.enumNames)}].");
                            sp.enumValueIndex = idx;
                        }
                        else
                        {
                            sp.enumValueIndex = ParseInt(valueRaw);
                        }
                        break;
                    }
                case SerializedPropertyType.ObjectReference:
                    {
                        if (valueRaw == "null" || string.IsNullOrEmpty(valueRaw))
                        {
                            sp.objectReferenceValue = null;
                            break;
                        }
                        var path = JsonBody.GetString("{\"v\":" + valueRaw + "}", "v");
                        if (string.IsNullOrEmpty(path))
                            path = JsonBody.GetString(valueRaw, "path");
                        if (string.IsNullOrEmpty(path))
                            path = JsonBody.GetString(valueRaw, "asset_path");
                        if (!string.IsNullOrEmpty(path))
                        {
                            var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                            sp.objectReferenceValue = obj;
                            break;
                        }
                        // instance_id fallback.
                        var idStr = JsonBody.GetRawValue("{\"v\":" + valueRaw + "}", "v");
                        if (!string.IsNullOrEmpty(idStr)
                            && int.TryParse(idStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        {
                            sp.objectReferenceValue = InstanceId.ToObject(id);
                            break;
                        }
                        throw new System.FormatException("object_reference value must be {\"path\": \"...\"}, {\"asset_path\": \"...\"}, {\"instance_id\": N}, or null.");
                    }
                case SerializedPropertyType.LayerMask:
                    sp.intValue = ParseInt(valueRaw);
                    break;
                case SerializedPropertyType.ArraySize:
                    sp.intValue = ParseInt(valueRaw);
                    break;
                default:
                    throw new System.NotSupportedException(
                        $"Property '{sp.propertyPath}' has unsupported type '{sp.propertyType}'. Use a path-scoped entry for the underlying leaf.");
            }
        }

        private static int ParseInt(string raw)
        {
            if (!int.TryParse(StripQuotes(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                throw new System.FormatException($"Could not parse int from '{raw}'.");
            return n;
        }

        private static float ParseFloat(string raw)
        {
            if (!float.TryParse(StripQuotes(raw), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                throw new System.FormatException($"Could not parse float from '{raw}'.");
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

        // Append non-serialized public properties that are commonly useful to
        // an agent (transform.position, Rigidbody.velocity, etc.). Strictly
        // read-only and bounded by max_fields.
        private static void AppendPublicProperties(StringBuilder sb, Component component,
            ref int emitted, int maxFields, ref int truncated)
        {
            bool first = true;
            foreach (var prop in component.GetType().GetProperties(
                BindingFlags.Instance | BindingFlags.Public))
            {
                if (emitted >= maxFields) { truncated++; continue; }
                // Skip indexers and write-only props.
                if (prop.GetIndexParameters().Length > 0) continue;
                if (prop.GetMethod == null) continue;
                // Skip heavy/allocating props that are unsafe to read on the
                // editor thread (GetComponent-like or animation-time props).
                if (prop.Name == "material" || prop.Name == "materials"
                    || prop.Name == "sharedMaterial" || prop.Name == "sharedMaterials")
                    continue;

                object value;
                try { value = prop.GetValue(component); }
                catch { continue; }

                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"path\":\"").Append(TypedTargets.Esc(prop.Name));
                sb.Append("\",\"type\":\"").Append(TypedTargets.Esc(prop.PropertyType.Name));
                sb.Append("\",\"value\":").Append(SerializeValue(value));
                emitted++;
            }
        }

        private static string SerializeValue(object value)
        {
            if (value == null) return "null";
            switch (value)
            {
                case bool b: return b ? "true" : "false";
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case string s: return "\"" + TypedTargets.Esc(s) + "\"";
                case Vector2 v2:
                    {
                        var sb = new StringBuilder(32);
                        sb.Append('[').Append(v2.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v2.y.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case Vector3 v3:
                    {
                        var sb = new StringBuilder(48);
                        sb.Append('[').Append(v3.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v3.y.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v3.z.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case Vector4 v4:
                    {
                        var sb = new StringBuilder(64);
                        sb.Append('[').Append(v4.x.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v4.y.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v4.z.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(v4.w.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case Color c:
                    {
                        var sb = new StringBuilder(48);
                        sb.Append('[').Append(c.r.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.g.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.b.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(',').Append(c.a.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                        return sb.ToString();
                    }
                case Object uo:
                    {
                        var sb = new StringBuilder(96);
                        sb.Append("{\"name\":\"").Append(TypedTargets.Esc(uo.name));
                        sb.Append("\",\"type\":\"").Append(TypedTargets.Esc(uo.GetType().FullName));
                        sb.Append("\",\"instanceId\":").Append(InstanceId.ToJson(uo)).Append("}");
                        return sb.ToString();
                    }
                default:
                    return "\"" + TypedTargets.Esc(value.ToString()) + "\"";
            }
        }

        private static string BuildOpResult(List<string> done, List<string> errors, string doneLabel)
            => AssetsTools.BuildFolderOpResult(done, errors, doneLabel);

        private static void MarkActiveSceneDirty(GameObject go)
        {
            var scene = go.scene;
            if (scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        }
    }
}
