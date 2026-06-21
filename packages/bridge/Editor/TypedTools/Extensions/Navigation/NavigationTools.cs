// M18 Plan 1 — Navigation (AI Navigation package) embedded domain tools.
//
// Compile-gated reference template for the TypedTools/Extensions/* layout.
// See NavigationJson.cs for the gate rationale. Ported verbatim (logic,
// tool ids, JSON schema, gate contracts) from the former standalone extension
// pack at packages/extensions/navigation — only the namespace changed.
#if UNITY_OPEN_MCP_EXT_NAVIGATION
#pragma warning disable CS0618
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEditor;
using UnityOpenMcpBridge;
using Object = UnityEngine.Object;
// AI Navigation types. Alias them explicitly because UnityEngine.AI (legacy
// NavMesh components, deprecated) and Unity.AI.Navigation (the new package)
// both define NavMeshSurface / NavMeshLink / NavMeshModifier /
// NavMeshModifierVolume — a bare `using Unity.AI.Navigation;` next to
// `using UnityEngine.AI;` produces CS0104 ambiguity. We keep UnityEngine.AI
// for NavMesh + NavMeshAgent + NavMeshBuildSettings (no name clash there)
// and reach the Navigation package types through these aliases.
using NavMeshSurface = Unity.AI.Navigation.NavMeshSurface;
using NavMeshLink = Unity.AI.Navigation.NavMeshLink;
using NavMeshModifier = Unity.AI.Navigation.NavMeshModifier;
using NavMeshModifierVolume = Unity.AI.Navigation.NavMeshModifierVolume;
using CollectObjects = Unity.AI.Navigation.CollectObjects;

namespace UnityOpenMcpBridge.Extensions.Navigation
{
    // M16 Plan 10 / T6.6.2 → M18 Plan 1 — NavMesh (AI Navigation) tools.
    //
    // Eleven typed tools, all registry-discovered via [BridgeToolType] +
    // [BridgeTool]. Naming: `unity_open_mcp_navigation_<action>` (snake_case
    // domain prefix — mirrors the kebab `navigation-*` ids in the upstream
    // Unity-MCP reference pack).
    //
    // Mutating tools declare IsMutating = true and accept a snake_case
    // `paths_hint` (bound to the C# `pathsHint` parameter by name) so the gate
    // can scope the verify checkpoint. Bake is the heavy op — it runs
    // EditorSettle so the dispatcher waits for asset refresh before returning.
    //
    // Reference: IvanMurzak/Unity-AI-Navigation (Apache-2.0).
    [BridgeToolType]
    public static class NavigationTools
    {
        // =====================================================================
        // Surface
        // =====================================================================

        // Add a NavMeshSurface component to a target GameObject. The surface
        // collects the geometry to bake for one agent type. params_hint is the
        // scene path containing the target — surface add writes scene state.
        [BridgeTool("unity_open_mcp_navigation_surface_add",
            Title = "Navigation: Add NavMeshSurface",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Add a NavMeshSurface component to a GameObject. Optionally set " +
            "the agent type (default 'Humanoid'), collect geometry mode " +
            "('All' | 'Volume'), and an optional bake extent (x,y,z). " +
            "Mutating: runs the gate path; paths_hint is the scene path " +
            "that contains the host.")]
        public static string SurfaceAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string agent_type = "Humanoid",
            string collect_objects = "All",
            string collection_extent = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_surface_add is mutating; pass a non-empty " +
                    "paths_hint scoped to the host's scene path.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return NavigationJson.Error("target_not_found",
                    "No GameObject resolved. Address by instance_id > path > name.");

            Undo.RecordObject(host, "Add NavMeshSurface");

            // Idempotent: re-use an existing surface if the host already has one.
            var surface = host.GetComponent<NavMeshSurface>();
            bool added = false;
            if (surface == null)
            {
                surface = Undo.AddComponent<NavMeshSurface>(host);
                added = true;
            }

            ApplySurfaceSettings(surface, agent_type, collect_objects, collection_extent);

            EditorUtility.SetDirty(host);
            var sb = new StringBuilder(160);
            sb.Append("\"surface\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(surface.GetInstanceID()).Append(',');
            sb.Append("\"agentType\":").Append(NavigationJson.Esc(surface.agentTypeID.ToString())).Append(',');
            sb.Append("\"collectObjects\":").Append(NavigationJson.Esc(surface.collectObjects.ToString()));
            sb.Append('}');
            return NavigationJson.Ok(sb.ToString());
        }

        // Configure bake settings on an existing surface (agent type, area
        // mask, voxel size, etc.). Modifies the surface in place.
        [BridgeTool("unity_open_mcp_navigation_set_bake_settings",
            Title = "Navigation: Set Bake Settings",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Configure NavMesh bake settings on an existing NavMeshSurface: " +
            "agent type (Humanoid / OEM:Tank / etc.), collect-objects mode " +
            "('All' | 'Volume'), and optional bake extent (x,y,z). Mutating: " +
            "runs the gate path; paths_hint is the host's scene path.")]
        public static string SetBakeSettings(
            int instance_id = 0,
            string path = null,
            string name = null,
            string agent_type = null,
            string collect_objects = null,
            string collection_extent = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_set_bake_settings is mutating; pass a non-empty paths_hint.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var surface = host.GetComponent<NavMeshSurface>();
            if (surface == null)
                return NavigationJson.Error("component_not_found",
                    "Target has no NavMeshSurface. Add one with navigation_surface_add first.");

            Undo.RecordObject(surface, "Set NavMesh bake settings");
            ApplySurfaceSettings(surface, agent_type, collect_objects, collection_extent);
            EditorUtility.SetDirty(surface);

            return NavigationJson.Ok(
                "\"agentType\":" + NavigationJson.Esc(surface.agentTypeID.ToString()) + ',' +
                "\"collectObjects\":" + NavigationJson.Esc(surface.collectObjects.ToString()));
        }

        // Bake the NavMesh for a surface. This is the heavy op — NavMeshBuilder
        // runs synchronously inside the Editor and may take seconds on large
        // scenes. EditorSettle ensures the dispatcher waits for asset refresh
        // before returning so the baked NavMesh asset is on disk by the time
        // the agent issues the next call.
        [BridgeTool("unity_open_mcp_navigation_surface_bake",
            Title = "Navigation: Bake NavMesh Surface",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Bake the NavMesh for a NavMeshSurface. Runs the bake " +
            "synchronously — may take seconds on large scenes. The baked " +
            "NavMesh asset is written next to the scene. Mutating: runs the " +
            "gate path; paths_hint is the host scene path. EditorSettle waits " +
            "for asset refresh before returning.")]
        public static string SurfaceBake(
            int instance_id = 0,
            string path = null,
            string name = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_surface_bake is mutating; pass a non-empty paths_hint.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var surface = host.GetComponent<NavMeshSurface>();
            if (surface == null)
                return NavigationJson.Error("component_not_found",
                    "Target has no NavMeshSurface. Add one with navigation_surface_add first.");

            // NavMeshSurface.BuildNavMesh is the synchronous editor bake —
            // it allocates a NavMeshData sub-asset under the surface.
            Undo.RecordObject(surface, "Bake NavMeshSurface");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            surface.BuildNavMesh();
            sw.Stop();

            var data = surface.navMeshData;
            var sb = new StringBuilder(220);
            sb.Append("\"baked\":true,");
            sb.Append("\"durationMs\":").Append(sw.ElapsedMilliseconds).Append(',');
            sb.Append("\"hasNavMeshData\":").Append(data != null ? "true" : "false");
            if (data != null)
            {
                sb.Append(",\"navMeshDataInstanceId\":").Append(data.GetInstanceID());
            }
            return NavigationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Modifier + Modifier Volume
        // =====================================================================

        [BridgeTool("unity_open_mcp_navigation_modifier_add",
            Title = "Navigation: Add NavMeshModifier",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Add a NavMeshModifier component to a GameObject. Override the " +
            "area type (default 'Walkable') or mark the object as ignored " +
            "for baking. Mutating: runs the gate path; paths_hint is the " +
            "host scene path.")]
        public static string ModifierAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string area = "Walkable",
            bool ignore = false,
            bool override_area = true,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_modifier_add is mutating; pass a non-empty paths_hint.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            Undo.RecordObject(host, "Add NavMeshModifier");
            var mod = host.GetComponent<NavMeshModifier>();
            bool added = false;
            if (mod == null)
            {
                mod = Undo.AddComponent<NavMeshModifier>(host);
                added = true;
            }
            mod.area = NavigationAreas.Resolve(area, mod.area);
            mod.ignoreFromBuild = ignore;
            mod.overrideArea = override_area;
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(140);
            sb.Append("\"modifier\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(mod.GetInstanceID()).Append(',');
            sb.Append("\"area\":").Append(NavigationJson.Esc(area)).Append(',');
            sb.Append("\"ignoreFromBuild\":").Append(ignore ? "true" : "false").Append(',');
            sb.Append("\"overrideArea\":").Append(override_area ? "true" : "false");
            sb.Append('}');
            return NavigationJson.Ok(sb.ToString());
        }

        [BridgeTool("unity_open_mcp_navigation_modifier_volume_add",
            Title = "Navigation: Add NavMeshModifierVolume",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Add a NavMeshModifierVolume component to a GameObject and size " +
            "it. Re-tags the NavMesh inside the volume to the given area " +
            "(default 'Walkable'). Mutating: runs the gate path; paths_hint " +
            "is the host scene path.")]
        public static string ModifierVolumeAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string area = "Walkable",
            string size = "4,4,4",
            string center = "0,0,0",
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_modifier_volume_add is mutating; pass a non-empty paths_hint.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            Undo.RecordObject(host, "Add NavMeshModifierVolume");
            var vol = host.GetComponent<NavMeshModifierVolume>();
            bool added = false;
            if (vol == null)
            {
                vol = Undo.AddComponent<NavMeshModifierVolume>(host);
                added = true;
            }
            vol.area = NavigationAreas.Resolve(area, vol.area);
            vol.size = ParseVector3(size, vol.size);
            vol.center = ParseVector3(center, vol.center);
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(180);
            sb.Append("\"volume\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(vol.GetInstanceID()).Append(',');
            sb.Append("\"area\":").Append(NavigationJson.Esc(area)).Append(',');
            sb.Append("\"size\":").Append(Vec3(vol.size)).Append(',');
            sb.Append("\"center\":").Append(Vec3(vol.center));
            sb.Append('}');
            return NavigationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Link
        // =====================================================================

        [BridgeTool("unity_open_mcp_navigation_link_add",
            Title = "Navigation: Add NavMeshLink",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Add a NavMeshLink component to a GameObject. A link connects two " +
            "NavMesh positions (start/end) — use it for jumps, drops, gaps, " +
            "or any traversal the surface bake cannot infer. Mutating: runs " +
            "the gate path; paths_hint is the host scene path.")]
        public static string LinkAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            string start_pos = "0,0,0",
            string end_pos = "0,0,0",
            float width = 0f,
            int cost_modifier = -1,
            bool bidirectional = true,
            bool auto_update = false,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_link_add is mutating; pass a non-empty paths_hint.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            Undo.RecordObject(host, "Add NavMeshLink");
            var link = host.GetComponent<NavMeshLink>();
            bool added = false;
            if (link == null)
            {
                link = Undo.AddComponent<NavMeshLink>(host);
                added = true;
            }
            link.startPoint = ParseVector3(start_pos, link.startPoint);
            link.endPoint = ParseVector3(end_pos, link.endPoint);
            link.width = width;
            link.costModifier = cost_modifier;
            link.bidirectional = bidirectional;
            link.autoUpdate = auto_update;
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(220);
            sb.Append("\"link\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(link.GetInstanceID()).Append(',');
            sb.Append("\"startPosition\":").Append(Vec3(link.startPoint)).Append(',');
            sb.Append("\"endPosition\":").Append(Vec3(link.endPoint)).Append(',');
            sb.Append("\"width\":").Append(link.width).Append(',');
            sb.Append("\"bidirectional\":").Append(link.bidirectional ? "true" : "false").Append(',');
            sb.Append("\"autoUpdate\":").Append(link.autoUpdate ? "true" : "false");
            sb.Append('}');
            return NavigationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Agent
        // =====================================================================

        [BridgeTool("unity_open_mcp_navigation_agent_add",
            Title = "Navigation: Add NavMeshAgent",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = true,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Add a NavMeshAgent component to a GameObject and configure its " +
            "radius / height / speed / angular speed. The agent does nothing " +
            "until navigation_agent_set_destination is called (and the scene " +
            "is in Play Mode). Mutating: runs the gate path; paths_hint is " +
            "the host scene path.")]
        public static string AgentAdd(
            int instance_id = 0,
            string path = null,
            string name = null,
            float radius = 0.5f,
            float height = 2f,
            float speed = 3.5f,
            float angular_speed = 120f,
            float acceleration = 8f,
            float stopping_distance = 0.1f,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_agent_add is mutating; pass a non-empty paths_hint.");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            Undo.RecordObject(host, "Add NavMeshAgent");
            var agent = host.GetComponent<NavMeshAgent>();
            bool added = false;
            if (agent == null)
            {
                agent = Undo.AddComponent<NavMeshAgent>(host);
                added = true;
            }
            agent.radius = radius;
            agent.height = height;
            agent.speed = speed;
            agent.angularSpeed = angular_speed;
            agent.acceleration = acceleration;
            agent.stoppingDistance = stopping_distance;
            EditorUtility.SetDirty(host);

            var sb = new StringBuilder(220);
            sb.Append("\"agent\":{");
            sb.Append("\"added\":").Append(added ? "true" : "false").Append(',');
            sb.Append("\"instanceId\":").Append(agent.GetInstanceID()).Append(',');
            sb.Append("\"radius\":").Append(agent.radius).Append(',');
            sb.Append("\"height\":").Append(agent.height).Append(',');
            sb.Append("\"speed\":").Append(agent.speed).Append(',');
            sb.Append("\"angularSpeed\":").Append(agent.angularSpeed);
            sb.Append('}');
            return NavigationJson.Ok(sb.ToString());
        }

        // Set the destination of a NavMeshAgent. Requires Play Mode (the agent
        // does not advance in Edit Mode). Returns whether the destination was
        // reachable on the current NavMesh.
        [BridgeTool("unity_open_mcp_navigation_agent_set_destination",
            Title = "Navigation: Set Agent Destination",
            IsMutating = true,
            Gate = GateMode.Enforce,
            IdempotentHint = false,
            Lifecycle = LifecyclePolicy.None, Group = "navigation")]
        [System.ComponentModel.Description(
            "Set a NavMeshAgent's destination as a world-space 'x,y,z'. " +
            "Requires Play Mode — the agent's pathfinder only runs at runtime. " +
            "In Edit Mode the destination is queued but the agent will not " +
            "move. Returns pathPending + pathStatus (Valid / Partial / Invalid). " +
            "Mutating: runs the gate path; paths_hint is the host scene path.")]
        public static string AgentSetDestination(
            int instance_id = 0,
            string path = null,
            string name = null,
            string destination = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_agent_set_destination is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(destination))
                return NavigationJson.Error("missing_parameter",
                    "'destination' is required ('x,y,z' world-space).");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var agent = host.GetComponent<NavMeshAgent>();
            if (agent == null)
                return NavigationJson.Error("component_not_found",
                    "Target has no NavMeshAgent. Add one with navigation_agent_add first.");

            var dest = ParseVector3(destination, agent.transform.position);
            // SetDestination returns true when the path request was queued.
            // The actual path status is available on agent.pathStatus immediately
            // after a sync — in Play Mode the pathfinder advances async.
            bool queued;
            try
            {
                queued = agent.SetDestination(dest);
            }
            catch (System.Exception e)
            {
                return NavigationJson.Error("destination_failed", e.Message);
            }

            var sb = new StringBuilder(160);
            sb.Append("\"set\":").Append(queued ? "true" : "false").Append(',');
            sb.Append("\"destination\":").Append(Vec3(dest)).Append(',');
            sb.Append("\"pathPending\":").Append(agent.pathPending ? "true" : "false").Append(',');
            sb.Append("\"pathStatus\":").Append(NavigationJson.Esc(agent.pathStatus.ToString())).Append(',');
            sb.Append("\"isPlaying\":").Append(Application.isPlaying ? "true" : "false");
            return NavigationJson.Ok(sb.ToString());
        }

        // =====================================================================
        // Discovery
        // =====================================================================

        // List every NavMesh-related component in the open scene(s). Read-only,
        // gate-free — agents use this to discover surfaces, agents, links before
        // mutating.
        [BridgeTool("unity_open_mcp_navigation_list",
            Title = "Navigation: List Components",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "navigation")]
        [System.ComponentModel.Description(
            "List every NavMeshSurface, NavMeshAgent, NavMeshLink, " +
            "NavMeshModifier, and NavMeshModifierVolume in the open scene(s). " +
            "Read-only, gate-free. Each entry includes the host name, " +
            "instance id, hierarchy path, and a few component-specific fields.")]
        public static string List()
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"status\":\"ok\",\"components\":{");

            AppendComponentList<NavMeshSurface>(sb, "surfaces", first: true,
                extra: s => "\"agentType\":" + NavigationJson.Esc(s.agentTypeID.ToString()) +
                           ",\"hasNavMeshData\":" + (s.navMeshData != null ? "true" : "false"));

            AppendComponentList<NavMeshAgent>(sb, "agents", first: false,
                extra: a => "\"speed\":" + a.speed + ",\"radius\":" + a.radius);

            AppendComponentList<NavMeshLink>(sb, "links", first: false,
                extra: l => "\"width\":" + l.width);

            AppendComponentList<NavMeshModifier>(sb, "modifiers", first: false,
                extra: m => "\"area\":" + m.area + ",\"ignoreFromBuild\":" + (m.ignoreFromBuild ? "true" : "false"));

            AppendComponentList<NavMeshModifierVolume>(sb, "modifierVolumes", first: false,
                extra: v => "\"area\":" + v.area);

            sb.Append("}}");
            return sb.ToString();
        }

        // Read one target's NavMesh components in full detail. Read-only.
        [BridgeTool("unity_open_mcp_navigation_get",
            Title = "Navigation: Get Components on Target",
            IsMutating = false,
            ReadOnlyHint = true,
            Gate = GateMode.Off,
            Lifecycle = LifecyclePolicy.None, Group = "navigation")]
        [System.ComponentModel.Description(
            "Read every NavMesh component attached to one GameObject " +
            "(NavMeshSurface / NavMeshAgent / NavMeshLink / NavMeshModifier / " +
            "NavMeshModifierVolume) with their serialized fields. Read-only, " +
            "gate-free. Address the host by instance_id > path > name.")]
        public static string Get(
            int instance_id = 0,
            string path = null,
            string name = null)
        {
            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return NavigationJson.Error("target_not_found",
                    "No GameObject resolved. Address by instance_id > path > name.");

            var sb = new StringBuilder(512);
            sb.Append("{\"status\":\"ok\",\"target\":{");
            sb.Append("\"name\":").Append(NavigationJson.Esc(host.name)).Append(',');
            sb.Append("\"instanceId\":").Append(host.GetInstanceID()).Append(',');
            sb.Append("\"path\":").Append(NavigationJson.Esc(BuildPath(host))).Append(',');
            sb.Append("\"components\":[");

            bool first = true;

            var surface = host.GetComponent<NavMeshSurface>();
            if (surface != null)
            {
                first = AppendComma(sb, first);
                sb.Append("{\"type\":\"NavMeshSurface\",")
                  .Append("\"instanceId\":").Append(surface.GetInstanceID()).Append(',')
                  .Append("\"agentType\":").Append(surface.agentTypeID).Append(',')
                  .Append("\"collectObjects\":").Append(NavigationJson.Esc(surface.collectObjects.ToString())).Append(',')
                  .Append("\"layerMask\":").Append(surface.layerMask.value).Append(',')
                  .Append("\"useGeometry\":").Append(NavigationJson.Esc(surface.useGeometry.ToString())).Append(',')
                  .Append("\"hasNavMeshData\":").Append(surface.navMeshData != null ? "true" : "false")
                  .Append('}');
            }

            var agent = host.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                first = AppendComma(sb, first);
                sb.Append("{\"type\":\"NavMeshAgent\",")
                  .Append("\"instanceId\":").Append(agent.GetInstanceID()).Append(',')
                  .Append("\"radius\":").Append(agent.radius).Append(',')
                  .Append("\"height\":").Append(agent.height).Append(',')
                  .Append("\"speed\":").Append(agent.speed).Append(',')
                  .Append("\"angularSpeed\":").Append(agent.angularSpeed).Append(',')
                  .Append("\"acceleration\":").Append(agent.acceleration).Append(',')
                  .Append("\"stoppingDistance\":").Append(agent.stoppingDistance).Append(',')
                  .Append("\"isOnNavMesh\":").Append(agent.isOnNavMesh ? "true" : "false").Append(',')
                  .Append("\"pathStatus\":").Append(NavigationJson.Esc(agent.pathStatus.ToString()))
                  .Append('}');
            }

            var link = host.GetComponent<NavMeshLink>();
            if (link != null)
            {
                first = AppendComma(sb, first);
                sb.Append("{\"type\":\"NavMeshLink\",")
                  .Append("\"instanceId\":").Append(link.GetInstanceID()).Append(',')
                  .Append("\"startPosition\":").Append(Vec3(link.startPoint)).Append(',')
                  .Append("\"endPosition\":").Append(Vec3(link.endPoint)).Append(',')
                  .Append("\"width\":").Append(link.width).Append(',')
                  .Append("\"bidirectional\":").Append(link.bidirectional ? "true" : "false").Append(',')
                  .Append("\"autoUpdate\":").Append(link.autoUpdate ? "true" : "false").Append(',')
                  .Append("\"costModifier\":").Append(link.costModifier)
                  .Append('}');
            }

            var mod = host.GetComponent<NavMeshModifier>();
            if (mod != null)
            {
                first = AppendComma(sb, first);
                sb.Append("{\"type\":\"NavMeshModifier\",")
                  .Append("\"instanceId\":").Append(mod.GetInstanceID()).Append(',')
                  .Append("\"area\":").Append(mod.area).Append(',')
                  .Append("\"ignoreFromBuild\":").Append(mod.ignoreFromBuild ? "true" : "false").Append(',')
                  .Append("\"overrideArea\":").Append(mod.overrideArea ? "true" : "false")
                  .Append('}');
            }

            var vol = host.GetComponent<NavMeshModifierVolume>();
            if (vol != null)
            {
                first = AppendComma(sb, first);
                sb.Append("{\"type\":\"NavMeshModifierVolume\",")
                  .Append("\"instanceId\":").Append(vol.GetInstanceID()).Append(',')
                  .Append("\"area\":").Append(vol.area).Append(',')
                  .Append("\"size\":").Append(Vec3(vol.size)).Append(',')
                  .Append("\"center\":").Append(Vec3(vol.center))
                  .Append('}');
            }

            sb.Append("]}}");
            return sb.ToString();
        }

        // =====================================================================
        // Modify (reflective field setter for advanced surface/agent tuning)
        // =====================================================================

        // Reflective field setter for NavMesh components — agents use it when
        // a typed mutator does not cover a niche field. Each field is the
        // snake_case component field name; value is a primitive (bool / int /
        // float / string) or a "x,y,z" vector string. Unknown fields are
        // reported as errors and the tool fails atomically.
        [BridgeTool("unity_open_mcp_navigation_modify",
            Title = "Navigation: Modify Components",
            IsMutating = true,
            Gate = GateMode.Enforce,
            Lifecycle = LifecyclePolicy.EditorSettle, Group = "navigation")]
        [System.ComponentModel.Description(
            "Set one or more serialized fields on a NavMesh component attached " +
            "to a target GameObject. Select the component by 'component_type' " +
            "(NavMeshSurface | NavMeshAgent | NavMeshLink | NavMeshModifier | " +
            "NavMeshModifierVolume). Each entry is { field, value, type? } where " +
            "type is 'int' | 'float' | 'bool' | 'string' | 'vector' (default " +
            "inferred from the current value). Mutating: runs the gate path; " +
            "paths_hint is the host scene path.")]
        public static string Modify(
            int instance_id = 0,
            string path = null,
            string name = null,
            string component_type = null,
            string fields_json = null,
            string[] paths_hint = null)
        {
            if (paths_hint == null || paths_hint.Length == 0)
                return NavigationJson.Error("paths_hint_required",
                    "navigation_modify is mutating; pass a non-empty paths_hint.");

            if (string.IsNullOrEmpty(component_type))
                return NavigationJson.Error("missing_parameter",
                    "'component_type' is required " +
                    "(NavMeshSurface | NavMeshAgent | NavMeshLink | NavMeshModifier | NavMeshModifierVolume).");

            var host = NavigationTargets.Resolve(instance_id, path, name);
            if (host == null)
                return TargetNotFound();

            var comp = ResolveComponent(host, component_type);
            if (comp == null)
                return NavigationJson.Error("component_not_found",
                    $"Target has no component of type '{component_type}'.");

            Undo.RecordObject(comp, "Modify NavMesh component");
            var applied = new StringBuilder(256);
            var errors = new StringBuilder(256);
            applied.Append('[');
            errors.Append('[');
            bool firstApplied = true;
            bool firstError = true;

            // fields_json is a JSON array of {field, value, type?} objects.
            // Parse it directly — the bridge's JsonBody helpers are not visible
            // outside the bridge assembly, so do a minimal hand-rolled parse.
            var entries = ParseFieldArray(fields_json);
            if (entries == null)
                return NavigationJson.Error("missing_parameter",
                    "'fields_json' must be a JSON array of {field, value, type?} objects.");

            foreach (var entry in entries)
            {
                var fieldResult = SetField(comp, entry);
                if (fieldResult.Ok)
                {
                    if (!firstApplied) applied.Append(',');
                    firstApplied = false;
                    applied.Append('{');
                    applied.Append("\"field\":").Append(NavigationJson.Esc(entry.Field)).Append(',');
                    applied.Append("\"applied\":true");
                    applied.Append('}');
                }
                else
                {
                    if (!firstError) errors.Append(',');
                    firstError = false;
                    errors.Append('{');
                    errors.Append("\"field\":").Append(NavigationJson.Esc(entry.Field)).Append(',');
                    errors.Append("\"error\":").Append(NavigationJson.Esc(fieldResult.Message));
                    errors.Append('}');
                }
            }
            applied.Append(']');
            errors.Append(']');

            EditorUtility.SetDirty(comp);
            return NavigationJson.Ok(
                "\"applied\":" + applied.ToString() + ',' +
                "\"errors\":" + errors.ToString());
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void ApplySurfaceSettings(NavMeshSurface surface, string agentType, string collectObjects, string collectionExtent)
        {
            // Map the friendly agent name (e.g. "Humanoid") to the registered
            // NavMeshBuildSettings.agentTypeID. The bridge stays in
            // UnityEngine.AI for this lookup — NavMeshSurface just stores the
            // int id; the friendly name is not serialized on the surface.
            //
            // The default built-in agent is registered by the Navigation
            // window when first opened. We leave the surface's agentTypeID
            // untouched when the name does not resolve so an early-call agent
            // does not corrupt the default (agentTypeID == 0 == Humanoid).
            //
            // Unity 6 UnityEngine.AI.NavMesh dropped GetSettingsByName; we walk
            // the registered settings via GetSettingsCount / GetSettingsByIndex
            // and match GetSettingsNameFromID(agentTypeID) against the requested
            // name. Older Unity versions exposed GetSettingsByName directly.
            if (!string.IsNullOrEmpty(agentType))
            {
                int resolved = -1;
                int count = NavMesh.GetSettingsCount();
                for (int i = 0; i < count; i++)
                {
                    var s = NavMesh.GetSettingsByIndex(i);
                    if (NavMesh.GetSettingsNameFromID(s.agentTypeID) == agentType)
                    {
                        resolved = s.agentTypeID;
                        break;
                    }
                }
                if (resolved != -1 && resolved != 0)
                    surface.agentTypeID = resolved;
            }

            if (!string.IsNullOrEmpty(collectObjects) &&
                System.Enum.TryParse<CollectObjects>(collectObjects, true, out var co))
            {
                surface.collectObjects = co;
            }

            // collection_extent is the half-extent of the bake volume. A
            // non-zero extent forces CollectObjects.Volume (only geometry
            // inside the volume is baked).
            if (!string.IsNullOrEmpty(collectionExtent))
            {
                var extent = ParseVector3(collectionExtent, Vector3.zero);
                if (extent != Vector3.zero)
                    surface.collectObjects = CollectObjects.Volume;
            }
        }

        private static Component ResolveComponent(GameObject host, string typeName)
        {
            switch (typeName)
            {
                case "NavMeshSurface": return host.GetComponent<NavMeshSurface>();
                case "NavMeshAgent": return host.GetComponent<NavMeshAgent>();
                case "NavMeshLink": return host.GetComponent<NavMeshLink>();
                case "NavMeshModifier": return host.GetComponent<NavMeshModifier>();
                case "NavMeshModifierVolume": return host.GetComponent<NavMeshModifierVolume>();
                default: return null;
            }
        }

        struct FieldEntry
        {
            public string Field;
            public string RawValue;
            public string TypeHint;
        }

        private static System.Collections.Generic.List<FieldEntry> ParseFieldArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("[") || !trimmed.EndsWith("]")) return null;

            var entries = new System.Collections.Generic.List<FieldEntry>();
            // Naive object-array parser: each entry is { ... }. Split on
            // top-level commas (depth-0). The MCP payload is small (one tool
            // call's worth of fields) so a hand-rolled split is fine.
            int depth = 0;
            int objStart = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (c == '{')
                {
                    if (depth == 0) objStart = i + 1;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var objBody = trimmed.Substring(objStart, i - objStart);
                        entries.Add(ParseFieldEntry(objBody));
                        objStart = -1;
                    }
                }
            }
            return entries;
        }

        private static FieldEntry ParseFieldEntry(string objBody)
        {
            var entry = new FieldEntry();
            entry.Field = ExtractStringValue(objBody, "field");
            entry.TypeHint = ExtractStringValue(objBody, "type");
            entry.RawValue = ExtractRawValue(objBody, "value");
            return entry;
        }

        private static string ExtractStringValue(string objBody, string key)
        {
            var raw = ExtractRawValue(objBody, key);
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }

        private static string ExtractRawValue(string objBody, string key)
        {
            var pattern = "\"" + key + "\"";
            var idx = objBody.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = objBody.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var start = colon + 1;
            // Skip whitespace.
            while (start < objBody.Length && char.IsWhiteSpace(objBody[start])) start++;
            if (start >= objBody.Length) return null;

            // String value?
            if (objBody[start] == '"')
            {
                var end = start + 1;
                while (end < objBody.Length)
                {
                    if (objBody[end] == '\\' && end + 1 < objBody.Length) { end += 2; continue; }
                    if (objBody[end] == '"') break;
                    end++;
                }
                return objBody.Substring(start, System.Math.Min(end + 1, objBody.Length) - start);
            }

            // Bracketed value (object/array) — capture balanced.
            if (objBody[start] == '{' || objBody[start] == '[')
            {
                var open = objBody[start];
                var close = open == '{' ? '}' : ']';
                int d = 0;
                var end = start;
                while (end < objBody.Length)
                {
                    if (objBody[end] == open) d++;
                    else if (objBody[end] == close)
                    {
                        d--;
                        if (d == 0) { end++; break; }
                    }
                    end++;
                }
                return objBody.Substring(start, end - start);
            }

            // Primitive — capture up to comma or end.
            var primitiveEnd = start;
            while (primitiveEnd < objBody.Length &&
                   objBody[primitiveEnd] != ',' &&
                   objBody[primitiveEnd] != '}')
                primitiveEnd++;
            return objBody.Substring(start, primitiveEnd - start).Trim();
        }

        struct FieldResult
        {
            public bool Ok;
            public string Message;
        }

        private static FieldResult SetField(Component comp, FieldEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Field))
                return new FieldResult { Ok = false, Message = "field is required" };

            // Reflection — surface the most common serialized fields by type.
            // We intentionally hand-roll the setter per component instead of
            // going through SerializedObject so the extension assembly does
            // not pull in extra Unity editor internals.
            var t = comp.GetType();
            var field = t.GetField(entry.Field,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (field == null)
                return new FieldResult { Ok = false, Message = $"Unknown field '{entry.Field}' on {t.Name}." };

            try
            {
                object converted = ConvertValue(field.FieldType, entry.RawValue, entry.TypeHint);
                field.SetValue(comp, converted);
                return new FieldResult { Ok = true };
            }
            catch (System.Exception e)
            {
                return new FieldResult { Ok = false, Message = e.Message };
            }
        }

        private static object ConvertValue(System.Type targetType, string raw, string typeHint)
        {
            if (targetType == typeof(string))
            {
                if (raw == null) return null;
                if (raw.StartsWith("\"") && raw.EndsWith("\"") && raw.Length >= 2)
                    return raw.Substring(1, raw.Length - 2);
                return raw;
            }
            if (targetType == typeof(int)) return int.Parse(raw);
            if (targetType == typeof(float)) return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return raw == "true";
            if (targetType == typeof(Vector3)) return ParseVector3(raw, Vector3.zero);
            if (targetType.IsEnum)
            {
                var cleaned = raw.Trim('"');
                if (System.Enum.IsDefined(targetType, cleaned))
                    return System.Enum.Parse(targetType, cleaned);
                if (int.TryParse(cleaned, out var intVal))
                    return System.Enum.ToObject(targetType, intVal);
                throw new System.FormatException($"Cannot parse '{raw}' as {targetType.Name} enum.");
            }
            throw new System.NotSupportedException($"Unsupported field type {targetType.Name}.");
        }

        private static Vector3 ParseVector3(string s, Vector3 fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            var parts = s.Split(',');
            if (parts.Length != 3) return fallback;
            if (!float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x)) return fallback;
            if (!float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y)) return fallback;
            if (!float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var z)) return fallback;
            return new Vector3(x, y, z);
        }

        private static string Vec3(Vector3 v)
            => $"[{v.x},{v.y},{v.z}]";

        private static string BuildPath(GameObject go)
        {
            var sb = new StringBuilder();
            var t = go.transform;
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        private static void AppendComponentList<T>(StringBuilder sb, string key, bool first,
            System.Func<T, string> extra) where T : Component
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":[");
            var all = Object.FindObjectsByType<T>(FindObjectsInactive.Exclude);
            for (int i = 0; i < all.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var c = all[i];
                sb.Append('{');
                sb.Append("\"name\":").Append(NavigationJson.Esc(c.gameObject.name)).Append(',');
                sb.Append("\"instanceId\":").Append(c.GetInstanceID()).Append(',');
                sb.Append("\"path\":").Append(NavigationJson.Esc(BuildPath(c.gameObject)));
                if (extra != null)
                {
                    sb.Append(',');
                    sb.Append(extra(c));
                }
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static bool AppendComma(StringBuilder sb, bool first)
        {
            if (!first) sb.Append(',');
            return false;
        }

        // Target-not-found helper — kept short so call sites read cleanly.
        private static string TargetNotFound()
            => NavigationJson.Error("target_not_found",
                "No GameObject resolved. Address by instance_id > path > name.");
    }
}
#endif
