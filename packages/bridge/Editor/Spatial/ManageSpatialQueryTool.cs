using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityOpenMcpBridge.Spatial
{
    // M10 Plan 3 T3.5 — Spatial query meta-tool (non-mutating).
    //
    // A single read-only tool exposes 3D reasoning primitives so an agent can
    // answer geometric questions about the current scene instead of inferring
    // them from raw transforms:
    //   unity_agent_spatial_query
    //     action: raycast | overlap | bounds | ground_check | nearest
    //
    // Physics queries (raycast / overlap / ground_check) hit colliders only —
    // objects without a Collider are invisible to them. 'bounds' and 'nearest'
    // fall back to renderer bounds and transform positions respectively, so
    // they also see render-only objects.
    //
    // Targets may be addressed three ways, tried in priority order:
    //   1. instance_id (int)   — canonical live handle; changes on domain reload.
    //   2. path (string)       — hierarchy path "Root/Child/Leaf".
    //   3. name (string)       — first GameObject whose name matches.
    //
    // Vectors are passed as "x,y,z" strings (the bridge reflection dispatcher
    // only binds primitive params). Physics transforms are synced first so
    // queries observe the latest scene edits.
    //
    // Logic is adapted from UCP's SpatialController/ObjectLocator (references/),
    // translated from MiniJson dictionaries to hand-rolled StringBuilder JSON
    // and from the UCP command router to the bridge [BridgeTool] convention.
    //
    // Read-only (Gate = Off, ReadOnlyHint = true) and token-bounded via max /
    // max_distance to protect the agent's context budget.
    [BridgeToolType]
    public class Tool_Spatial
    {
        const int DefaultNearestMax = 5;

        // ============================ dispatch ============================

        [BridgeTool("unity_agent_spatial_query", Title = "Spatial Query",
            IsMutating = false, ReadOnlyHint = true, Gate = GateMode.Off)]
        [System.ComponentModel.Description(
            "Physics-based spatial queries against the current scene state: " +
            "raycast, overlap, bounds, ground_check, nearest. Requires a loaded " +
            "scene (live-only). raycast/overlap/ground_check hit Colliders only; " +
            "bounds/nearest also see render-only objects. Address targets by " +
            "instance_id (canonical), path (\"Root/Child\"), or name. Vectors are " +
            "\"x,y,z\" strings.")]
        public string SpatialQuery(
            string action = "",
            // raycast
            string origin = null,
            string direction = null,
            float max_distance = 0f,
            // overlap
            string shape = null,
            string center = null,
            float radius = 0f,
            string half_extents = null,
            string end = null,
            // shared physics
            string layer = null,
            bool query_triggers = false,
            // bounds / ground_check / nearest target
            int instance_id = 0,
            string path = null,
            string name = null,
            bool include_children = true,
            // ground_check probe
            string point = null,
            // nearest
            int max = DefaultNearestMax,
            string component = null,
            string tag = null)
        {
            try
            {
                var act = (action ?? "").Trim().ToLowerInvariant();
                switch (act)
                {
                    case "raycast":
                        return DoRaycast(origin, direction, max_distance, layer, query_triggers);
                    case "overlap":
                        return DoOverlap(shape, center, radius, half_extents, end, layer, query_triggers);
                    case "bounds":
                        return DoBounds(instance_id, path, name, include_children);
                    case "ground_check":
                        return DoGround(instance_id, path, name, point, direction, max_distance, layer);
                    case "nearest":
                        return DoNearest(instance_id, path, name, point, max, component, tag);
                    default:
                        return ErrorJson("unknown_action",
                            "Unknown or missing 'action'. Expected one of: " +
                            "raycast, overlap, bounds, ground_check, nearest.");
                }
            }
            catch (Exception e)
            {
                return ErrorJson("execution_error", e.Message);
            }
        }

        // ============================ raycast ============================

        string DoRaycast(string originStr, string directionStr, float maxDistance,
            string layerName, bool queryTriggers)
        {
            Physics.SyncTransforms();

            if (string.IsNullOrEmpty(originStr))
                return ErrorJson("missing_parameter", "'origin' (\"x,y,z\") is required for raycast.");
            if (string.IsNullOrEmpty(directionStr))
                return ErrorJson("missing_parameter", "'direction' (\"x,y,z\") is required for raycast.");

            var origin = ParseVec3(originStr, "origin");
            var dir = ParseVec3(directionStr, "direction");
            if (dir.sqrMagnitude < 1e-8f)
                return ErrorJson("bad_parameter", "'direction' must not be the zero vector.");

            float dist = maxDistance > 0f ? maxDistance : Mathf.Infinity;
            int mask = ResolveLayerMask(layerName);
            var trigger = queryTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

            if (Physics.Raycast(origin, dir.normalized, out var hit, dist, mask, trigger))
            {
                var go = hit.collider.gameObject;
                var sb = new StringBuilder(512);
                sb.Append('{');
                sb.Append("\"action\":\"raycast\",");
                sb.Append("\"hit\":true,");
                sb.Append("\"point\":").Append(Vec3Json(hit.point)).Append(',');
                sb.Append("\"normal\":").Append(Vec3Json(hit.normal)).Append(',');
                sb.Append("\"distance\":").Append(Num(hit.distance)).Append(',');
                sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
                sb.Append("\"gameObject\":").Append(Esc(go.name)).Append(',');
                sb.Append("\"path\":").Append(Esc(GetPath(go))).Append(',');
                sb.Append("\"collider\":").Append(Esc(hit.collider.GetType().Name));
                sb.Append('}');
                return sb.ToString();
            }

            return "{\"action\":\"raycast\",\"hit\":false}";
        }

        // ============================ overlap ============================

        string DoOverlap(string shapeStr, string centerStr, float radiusVal,
            string halfExtentsStr, string endStr, string layerName, bool queryTriggers)
        {
            Physics.SyncTransforms();

            var shape = (string.IsNullOrEmpty(shapeStr) ? "sphere" : shapeStr).ToLowerInvariant();
            if (string.IsNullOrEmpty(centerStr))
                return ErrorJson("missing_parameter", "'center' (\"x,y,z\") is required for overlap.");
            var center = ParseVec3(centerStr, "center");

            int mask = ResolveLayerMask(layerName);
            var trigger = queryTriggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore;

            Collider[] hits;
            switch (shape)
            {
                case "sphere":
                    hits = Physics.OverlapSphere(center, radiusVal > 0f ? radiusVal : 1f, mask, trigger);
                    break;
                case "box":
                    {
                        var half = string.IsNullOrEmpty(halfExtentsStr)
                            ? Vector3.one * 0.5f
                            : ParseVec3(halfExtentsStr, "half_extents");
                        hits = Physics.OverlapBox(center, half, Quaternion.identity, mask, trigger);
                        break;
                    }
                case "capsule":
                    {
                        var end0 = string.IsNullOrEmpty(endStr) ? center : ParseVec3(endStr, "end");
                        hits = Physics.OverlapCapsule(center, end0, radiusVal > 0f ? radiusVal : 1f, mask, trigger);
                        break;
                    }
                default:
                    return ErrorJson("bad_parameter",
                        "'shape' must be 'sphere', 'box', or 'capsule'.");
            }

            return BuildOverlapResult(shape, center, hits);
        }

        string BuildOverlapResult(string shape, Vector3 center, Collider[] hits)
        {
            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"action\":\"overlap\",");
            sb.Append("\"shape\":").Append(Esc(shape)).Append(',');
            sb.Append("\"count\":").Append(hits.Length).Append(',');
            sb.Append("\"hits\":[");
            int shown = 0;
            for (int i = 0; i < hits.Length; i++)
            {
                var c = hits[i];
                if (c == null) continue;
                if (shown > 0) sb.Append(',');
                shown++;
                var go = c.gameObject;
                sb.Append('{');
                sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
                sb.Append("\"gameObject\":").Append(Esc(go.name)).Append(',');
                sb.Append("\"path\":").Append(Esc(GetPath(go))).Append(',');
                sb.Append("\"collider\":").Append(Esc(c.GetType().Name)).Append(',');
                sb.Append("\"distance\":").Append(Num(Vector3.Distance(center, c.bounds.center)));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ============================ bounds ============================

        string DoBounds(int instanceId, string pathStr, string nameStr, bool includeChildren)
        {
            Physics.SyncTransforms();

            var go = ResolveTarget(instanceId, pathStr, nameStr);
            if (go == null)
                return ErrorJson("target_not_found",
                    "No GameObject matched the given instance_id/path/name.");

            var bounds = ComputeWorldBounds(go, includeChildren, out bool hasBounds);
            if (!hasBounds)
                bounds = new Bounds(go.transform.position, Vector3.zero);

            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"action\":\"bounds\",");
            sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
            sb.Append("\"name\":").Append(Esc(go.name)).Append(',');
            sb.Append("\"path\":").Append(Esc(GetPath(go))).Append(',');
            sb.Append("\"includeChildren\":").Append(includeChildren ? "true" : "false").Append(',');
            sb.Append("\"empty\":").Append(hasBounds ? "false" : "true").Append(',');
            sb.Append("\"center\":").Append(Vec3Json(bounds.center)).Append(',');
            sb.Append("\"extents\":").Append(Vec3Json(bounds.extents)).Append(',');
            sb.Append("\"size\":").Append(Vec3Json(bounds.size)).Append(',');
            sb.Append("\"min\":").Append(Vec3Json(bounds.min)).Append(',');
            sb.Append("\"max\":").Append(Vec3Json(bounds.max));
            sb.Append('}');
            return sb.ToString();
        }

        // ============================ ground_check ============================

        string DoGround(int instanceId, string pathStr, string nameStr, string pointStr,
            string directionStr, float maxDistance, string layerName)
        {
            Physics.SyncTransforms();

            // Two modes: probe below an object, or probe an explicit point.
            GameObject go = null;
            Vector3 origin;
            bool hasObject = instanceId != 0 || !string.IsNullOrEmpty(pathStr) || !string.IsNullOrEmpty(nameStr);
            if (hasObject)
            {
                go = ResolveTarget(instanceId, pathStr, nameStr);
                if (go == null)
                    return ErrorJson("target_not_found",
                        "No GameObject matched the given instance_id/path/name.");
                origin = go.transform.position;
            }
            else
            {
                if (string.IsNullOrEmpty(pointStr))
                    return ErrorJson("missing_parameter",
                        "ground_check needs a target (instance_id/path/name) or a 'point' (\"x,y,z\").");
                origin = ParseVec3(pointStr, "point");
            }

            var dir = string.IsNullOrEmpty(directionStr)
                ? Vector3.down
                : ParseVec3(directionStr, "direction");
            if (dir.sqrMagnitude < 1e-8f)
                return ErrorJson("bad_parameter", "'direction' must not be the zero vector.");
            dir.Normalize();

            float dist = maxDistance > 0f ? maxDistance : Mathf.Infinity;
            int mask = ResolveLayerMask(layerName);

            // Offset the ray start slightly against the cast direction so an object
            // already resting on / overlapping the surface still registers a hit.
            var start = origin - dir * 0.01f;
            if (!Physics.Raycast(start, dir, out var hit, dist, mask, QueryTriggerInteraction.Ignore))
                return "{\"action\":\"ground_check\",\"hit\":false}";

            var surface = hit.collider.gameObject;
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"action\":\"ground_check\",");
            sb.Append("\"hit\":true,");
            sb.Append("\"point\":").Append(Vec3Json(hit.point)).Append(',');
            sb.Append("\"normal\":").Append(Vec3Json(hit.normal)).Append(',');
            sb.Append("\"distance\":").Append(Num(hit.distance)).Append(',');
            sb.Append("\"surface\":").Append(Esc(surface.name)).Append(',');
            sb.Append("\"surfaceId\":").Append(surface.GetInstanceID()).Append(',');
            sb.Append("\"surfacePath\":").Append(Esc(GetPath(surface)));
            sb.Append('}');
            return sb.ToString();
        }

        // ============================ nearest ============================

        string DoNearest(int instanceId, string pathStr, string nameStr, string pointStr,
            int maxCount, string componentFilter, string tagFilter)
        {
            Vector3 from;
            GameObject self = null;
            bool hasObject = instanceId != 0 || !string.IsNullOrEmpty(pathStr) || !string.IsNullOrEmpty(nameStr);
            if (hasObject)
            {
                self = ResolveTarget(instanceId, pathStr, nameStr);
                if (self == null)
                    return ErrorJson("target_not_found",
                        "No GameObject matched the given instance_id/path/name.");
                from = self.transform.position;
            }
            else
            {
                if (string.IsNullOrEmpty(pointStr))
                    return ErrorJson("missing_parameter",
                        "nearest needs a target (instance_id/path/name) or a 'point' (\"x,y,z\").");
                from = ParseVec3(pointStr, "point");
            }

            int cap = maxCount > 0 ? maxCount : DefaultNearestMax;

            var candidates = new List<(GameObject go, float dist)>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    CollectNearest(root, from, self, componentFilter, tagFilter, candidates);
            }

            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            var sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"action\":\"nearest\",");
            sb.Append("\"count\":").Append(Math.Min(candidates.Count, cap)).Append(',');
            sb.Append("\"objects\":[");
            int shown = 0;
            foreach (var (go, dist) in candidates)
            {
                if (shown >= cap) break;
                if (shown > 0) sb.Append(',');
                shown++;
                sb.Append('{');
                sb.Append("\"instanceId\":").Append(go.GetInstanceID()).Append(',');
                sb.Append("\"name\":").Append(Esc(go.name)).Append(',');
                sb.Append("\"path\":").Append(Esc(GetPath(go))).Append(',');
                sb.Append("\"distance\":").Append(Num(dist)).Append(',');
                sb.Append("\"position\":").Append(Vec3Json(go.transform.position));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        static void CollectNearest(GameObject go, Vector3 from, GameObject self,
            string componentFilter, string tagFilter, List<(GameObject, float)> outList)
        {
            var include = go != self;
            if (include && !string.IsNullOrEmpty(componentFilter) && go.GetComponent(componentFilter) == null)
                include = false;
            if (include && !string.IsNullOrEmpty(tagFilter) && !go.CompareTag(tagFilter))
                include = false;
            if (include)
                outList.Add((go, Vector3.Distance(from, go.transform.position)));

            foreach (Transform child in go.transform)
                CollectNearest(child.gameObject, from, self, componentFilter, tagFilter, outList);
        }

        // ============================ target resolution ============================

        static GameObject ResolveTarget(int instanceId, string pathStr, string nameStr)
        {
            if (instanceId != 0)
            {
                var byId = FindByInstanceId(instanceId);
                if (byId != null) return byId;
            }

            if (!string.IsNullOrEmpty(pathStr))
            {
                var byPath = FindByPath(pathStr);
                if (byPath != null) return byPath;
            }

            if (!string.IsNullOrEmpty(nameStr))
            {
                var byName = FindByName(nameStr);
                if (byName != null) return byName;
            }

            return null;
        }

        static GameObject FindByInstanceId(int instanceId)
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = FindInHierarchyById(root, instanceId);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static GameObject FindInHierarchyById(GameObject go, int instanceId)
        {
            if (go.GetInstanceID() == instanceId) return go;
            foreach (Transform child in go.transform)
            {
                var found = FindInHierarchyById(child.gameObject, instanceId);
                if (found != null) return found;
            }
            return null;
        }

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var trimmed = path.Trim('/');
            var segments = trimmed.Split('/');
            if (segments.Length == 0) return null;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != segments[0]) continue;
                    var resolved = WalkPath(root.transform, segments, 1);
                    if (resolved != null) return resolved;
                }
            }
            return null;
        }

        static GameObject WalkPath(Transform current, string[] segments, int index)
        {
            if (index >= segments.Length) return current.gameObject;
            foreach (Transform child in current)
            {
                if (child.name == segments[index])
                {
                    var resolved = WalkPath(child, segments, index + 1);
                    if (resolved != null) return resolved;
                }
            }
            return null;
        }

        static GameObject FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = FindInHierarchyByName(root, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static GameObject FindInHierarchyByName(GameObject go, string name)
        {
            if (go.name == name) return go;
            foreach (Transform child in go.transform)
            {
                var found = FindInHierarchyByName(child.gameObject, name);
                if (found != null) return found;
            }
            return null;
        }

        // ============================ bounds helper ============================

        static Bounds ComputeWorldBounds(GameObject target, bool includeChildren, out bool hasBounds)
        {
            hasBounds = false;
            var bounds = new Bounds(target.transform.position, Vector3.zero);

            var renderers = includeChildren
                ? target.GetComponentsInChildren<Renderer>()
                : target.GetComponents<Renderer>();
            foreach (var r in renderers)
            {
                if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
                else bounds.Encapsulate(r.bounds);
            }

            var colliders = includeChildren
                ? target.GetComponentsInChildren<Collider>()
                : target.GetComponents<Collider>();
            foreach (var c in colliders)
            {
                if (!hasBounds) { bounds = c.bounds; hasBounds = true; }
                else bounds.Encapsulate(c.bounds);
            }

            return bounds;
        }

        // ============================ path + vector helpers ============================

        static string GetPath(GameObject go)
        {
            if (go == null) return "";
            var t = go.transform;
            if (t.parent == null) return go.name;
            var sb = new StringBuilder(64);
            sb.Append(go.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, '/');
                sb.Insert(0, p.name);
                p = p.parent;
            }
            return sb.ToString();
        }

        static Vector3 ParseVec3(string s, string fieldName)
        {
            if (string.IsNullOrEmpty(s))
                throw new ArgumentException($"'{fieldName}' (\"x,y,z\") is required.");
            var parts = s.Split(',');
            if (parts.Length < 3)
                throw new ArgumentException($"'{fieldName}' must be three comma-separated numbers, got '{s}'.");
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                throw new ArgumentException($"'{fieldName}' must be numeric, got '{s}'.");
            return new Vector3(x, y, z);
        }

        static int ResolveLayerMask(string layerName)
        {
            // A single layer name, else everything.
            if (!string.IsNullOrEmpty(layerName))
            {
                var layer = LayerMask.NameToLayer(layerName);
                if (layer < 0)
                    throw new ArgumentException($"Unknown layer name '{layerName}'.");
                return 1 << layer;
            }
            return ~0;
        }

        // ============================ formatting helpers ============================

        static string Vec3Json(Vector3 v)
        {
            var sb = new StringBuilder(48);
            sb.Append('[');
            sb.Append(Num(v.x)).Append(',');
            sb.Append(Num(v.y)).Append(',');
            sb.Append(Num(v.z));
            sb.Append(']');
            return sb.ToString();
        }

        static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

        static string Num(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        static string ErrorJson(string code, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"error\":{\"code\":").Append(Esc(code));
            sb.Append(",\"message\":").Append(Esc(message));
            sb.Append("}}");
            return sb.ToString();
        }

        static string Esc(string s)
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
    }
}
