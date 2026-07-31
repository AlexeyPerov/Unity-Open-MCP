using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.TypedTools
{
    // Shared resolver for object-reference field values used by the mutating
    // typed tools (object_modify via reflection, component_modify via
    // SerializedObject, and the jsonPatches path on gameobject_modify).
    //
    // Centralizing this fixes two field-report bugs (feedback-fable-31-07 §1/§1b):
    //
    //   1. A {"path": "..."} value pointing at a SCENE HIERARCHY GameObject
    //      (e.g. "Canvas/SafeArea/.../Tab_Shop") was fed only to
    //      AssetDatabase.LoadAssetAtPath, which returns null for non-asset
    //      paths. The null was then silently written as {fileID: 0} and the
    //      tool reported success — corrupting the target. Now a scene path is
    //      resolved via the hierarchy walker, and when the field type is a
    //      Component the matching component is pulled off the resolved GO.
    //
    //   2. The documented {"instance_id": N} value form was rejected by the
    //      very error message that named it, because the old parsers wrapped
    //      the raw value as {"v": <raw>} before looking up "instance_id" — a
    //      structured object never exposes a bare "v". Here the structured
    //      keys are read directly off the raw fragment.
    //
    // Resolution never silently writes null: on any failure the caller is
    // handed a per-field error and the serialized value is left untouched.
    public static class ObjectRefValue
    {
        // Accepted value shapes:
        //   null                                            -> null (clear)
        //   {"path": "..."} / {"asset_path": "..."}         -> asset at path, else scene GO at path, else component on that GO
        //   {"instance_id": N} / bare N / quoted "N"        -> live instance by id
        //
        // `rawValue` is the verbatim JSON fragment for the field's value
        // (as produced by JsonBody.GetRawValue). `targetType` is the field's
        // declared Type (a UnityEngine.Object subtype). On success returns the
        // resolved Object; on failure returns null and sets `error` so the
        // caller can surface a per-field error WITHOUT assigning.
        public static Object Resolve(string rawValue, System.Type targetType, out string error)
        {
            error = null;

            // null / missing / empty -> explicit clear. Treat the bare JSON
            // token "null" and an empty/whitespace fragment the same way.
            var trimmed = rawValue == null ? null : rawValue.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == "null")
                return null;

            // 1) path / asset_path — try the asset database first (assets and
            //    prefabs), then fall back to the live scene hierarchy (the
            //    common case for wiring references between scene objects). When
            //    the field wants a Component, resolve the GO and GetComponent;
            //    when it wants a GameObject, return the GO itself.
            var path = JsonBody.GetString(rawValue, "path");
            if (string.IsNullOrEmpty(path)) path = JsonBody.GetString(rawValue, "asset_path");
            // A bare JSON string value: {"value": "Assets/Foo.prefab"} or
            // {"value": "Canvas/.../Tab_Shop"}. Wrap-and-read via the same
            // GetString helper the old code used.
            if (string.IsNullOrEmpty(path)) path = JsonBody.GetString("{\"v\":" + rawValue + "}", "v");
            if (!string.IsNullOrEmpty(path))
            {
                var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (obj == null)
                {
                    // Not an asset — try the live scene hierarchy.
                    var go = TypedTargets.FindByPath(path);
                    if (go != null)
                        obj = CoerceToTarget(go, targetType, path, out error);
                    else
                        error = $"object_reference path '{path}' resolved to neither an asset nor a live scene GameObject.";
                }
                else
                {
                    // Asset loaded — still validate it is assignable to the
                    // field type (e.g. a Material field given a Texture path).
                    if (!IsAssignable(obj, targetType))
                        error = $"object_reference path '{path}' (type {obj.GetType().Name}) is not assignable to field type {targetType.Name}.";
                }
                return error == null ? obj : null;
            }

            // 2) instance_id — read the structured key directly off the
            //    fragment (NOT via the {"v": <raw>} wrap, which can't see a
            //    nested key). Accept {"instance_id": N}; a bare numeric value
            //    is also accepted via the wrap fallback for back-compat.
            var idRaw = JsonBody.GetRawValue(rawValue, "instance_id");
            if (string.IsNullOrEmpty(idRaw))
                idRaw = JsonBody.GetRawValue("{\"v\":" + rawValue + "}", "v");
            if (!string.IsNullOrEmpty(idRaw))
            {
                var id = InstanceId.Parse(StripQuotes(idRaw));
                if (id == 0)
                {
                    error = "object_reference instance_id parsed to 0 (missing or unparseable).";
                    return null;
                }
                var obj = InstanceId.ToObject(id);
                if (obj == null)
                {
                    error = $"object_reference instance_id {id} does not resolve to a live Object (it may have been unloaded or the id changed after a domain reload).";
                    return null;
                }
                if (!IsAssignable(obj, targetType))
                {
                    error = $"object_reference instance_id {id} (type {obj.GetType().Name}) is not assignable to field type {targetType.Name}.";
                    return null;
                }
                return obj;
            }

            error = "object_reference value must be {\"path\": \"...\"}, {\"asset_path\": \"...\"}, {\"instance_id\": N}, or null.";
            return null;
        }

        // Given a resolved GameObject, return the Component the field wants
        // (or the GO itself for GameObject-typed fields). Sets `error` when
        // the requested component is absent so the caller does not silently
        // write null.
        private static Object CoerceToTarget(GameObject go, System.Type targetType, string path, out string error)
        {
            error = null;
            if (typeof(GameObject).IsAssignableFrom(targetType))
                return go;
            if (typeof(Component).IsAssignableFrom(targetType))
            {
                var c = go.GetComponent(targetType);
                if (c == null)
                    error = $"object_reference path '{path}' resolved to GameObject '{go.name}' but it has no component of type {targetType.Name}.";
                return c;
            }
            // Some other UnityEngine.Object subtype (e.g. a derived asset type)
            // — a scene GO is not a valid value for it.
            error = $"object_reference path '{path}' resolved to a scene GameObject, which is not assignable to field type {targetType.Name}.";
            return null;
        }

        private static bool IsAssignable(Object obj, System.Type targetType)
        {
            if (obj == null) return false;
            return targetType.IsAssignableFrom(obj.GetType());
        }

        private static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
