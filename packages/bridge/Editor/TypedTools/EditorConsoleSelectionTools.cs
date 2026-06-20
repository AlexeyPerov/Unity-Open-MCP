// Deliberate use of deprecated GetInstanceID() / EditorUtility.InstanceIDToObject() — see docs/code-conventions.md §Instance IDs.
#pragma warning disable CS0618
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 5 — typed console / editor state / selection / undo / tags /
    // layers tools. Covers console_clear / console_log / editor_set_state /
    // selection_get / selection_set / editor_undo / editor_redo /
    // editor_get_tags / editor_get_layers / editor_add_tag / editor_add_layer.
    //
    // Gate routing (see BridgeHttpServer DirectResponseTools / MutatingTools):
    //   - console_clear / console_log / editor_set_state / selection_get /
    //     selection_set / editor_undo / editor_redo mutate editor state but
    //     write NO assets, so the gate (which validates asset-reference
    //     fallout) has nothing to validate. They route as gate-free direct-
    //     response tools. editor_set_state additionally runs the active-scene
    //     dirty guard inline (entering play mode can trigger Unity's native
    //     save modal).
    //   - editor_get_tags / editor_get_layers are read-only and gate-free.
    //   - editor_add_tag / editor_add_layer write ProjectSettings/TagManager
    //     (.asset) and run the full gate path with paths_hint scoped to it;
    //     EditorSettle lifecycle so AssetDatabase.Refresh settles before return.
    //
    // Complements (do NOT duplicate): unity_senses_read_console (read+clear),
    // unity_open_mcp_editor_status (state read). These tools only add the
    // control / write / structured-selection surface those reads lack.
    //
    // NOT registry-discovered: wired into BridgeHttpServer.DispatchTool
    // alongside the other M16 typed tools so the snake_case schemas parse the
    // same way.
    public static class EditorConsoleSelectionTools
    {
        // ============================ Console =============================

        // Clear the Editor console. Mirrors LogEntries.Clear via the same
        // reflection path ReadConsoleTool uses (the type is internal). A direct
        // reflection call keeps this file free of the Console namespace.
        public static ToolDispatchResult ConsoleClear(string body)
        {
            bool cleared;
            try
            {
                cleared = LogEntriesClearViaReflection();
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("clear_failed", e.Message);
            }

            var sb = new StringBuilder(48);
            sb.Append("{\"status\":\"ok\",\"cleared\":").Append(cleared ? "true" : "false");
            sb.Append(",\"note\":\"Use unity_senses_read_console to read entries before clearing when you need them.\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Write a log/warning/error to the console from the agent. Folds UUMCP
        // console_log. Optional context (instance_id or asset_path) attaches a
        // UnityEngine.Object so the Console pings it on click.
        public static ToolDispatchResult ConsoleLog(string body)
        {
            var message = JsonBody.GetString(body, "message");
            if (string.IsNullOrEmpty(message))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'message' is required and must be a non-empty string.");

            var level = ParseLogLevel(JsonBody.GetString(body, "level"), LogLevel.Log);

            // Context resolution: asset_path wins (asset on disk), else
            // instance_id (scene object / Component). Omit both for a plain log.
            Object context = null;
            var assetPath = JsonBody.GetString(body, "context_asset_path");
            if (!string.IsNullOrEmpty(assetPath))
            {
                context = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (context == null)
                    return ToolDispatchResult.Fail("context_not_found",
                        $"No asset at '{assetPath}' to attach as log context.");
            }
            else
            {
                int instanceId = JsonBody.GetInt(body, "context_instance_id", 0);
                if (instanceId != 0)
                {
                    context = EditorUtility.InstanceIDToObject(instanceId);
                    if (context == null)
                        return ToolDispatchResult.Fail("context_not_found",
                            $"No live object with instance_id={instanceId} to attach as log context.");
                }
            }

            try
            {
                switch (level)
                {
                    case LogLevel.Warning:
                        Debug.LogWarning(message, context);
                        break;
                    case LogLevel.Error:
                        Debug.LogError(message, context);
                        break;
                    default:
                        Debug.Log(message, context);
                        break;
                }
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("log_failed", e.Message);
            }

            var sb = new StringBuilder(80);
            sb.Append("{\"status\":\"ok\",\"logged\":true,\"level\":\"").Append(Esc(LogLevelToWire(level)));
            sb.Append("\",\"message\":\"").Append(Esc(message)).Append("\"");
            if (context != null)
            {
                sb.Append(",\"context\":{\"name\":\"").Append(Esc(context.name));
                sb.Append("\",\"instanceId\":").Append(context.GetInstanceID());
                sb.Append(",\"type\":\"").Append(Esc(context.GetType().Name)).Append("\"}");
            }
            sb.Append(",\"note\":\"Surface in the next unity_senses_read_console / pull_events call.\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ========================= Editor state ===========================

        // Set play / pause / stop. Entering play mode is disruptive: a dirty
        // scene can trigger Unity's native save modal. The dispatcher treats
        // this tool as non-mutating (gate-free), so the centralized
        // SceneDirtyGuard (which only runs on RestartThenSettle lifecycle) does
        // not preflight it — we run the same guard inline here so the contract
        // holds without inventing a new lifecycle bucket.
        public static ToolDispatchResult EditorSetState(string body)
        {
            var stateRaw = JsonBody.GetString(body, "state");
            var state = ParseEditorState(stateRaw);
            if (state == EditorStateTarget.Unknown)
                return ToolDispatchResult.Fail("invalid_parameter",
                    $"'state' must be one of: play, pause, stop. Got '{stateRaw}'.");

            bool force = JsonBody.GetBool(body, "force", false);
            bool ignoreDirty = JsonBody.GetBool(body, "ignore_scene_dirty", false);

            // Idempotent guards (unless force).
            switch (state)
            {
                case EditorStateTarget.Play:
                    if (!force && EditorApplication.isPlaying)
                        return ToolDispatchResult.Ok(BuildStateEnvelope("play_noop",
                            "Already in play mode. Pass force: true to re-enter."));
                    break;
                case EditorStateTarget.Pause:
                    if (!EditorApplication.isPlaying)
                        return ToolDispatchResult.Ok(BuildStateEnvelope("pause_noop",
                            "Not in play mode; nothing to pause."));
                    break;
                case EditorStateTarget.Stop:
                    if (!EditorApplication.isPlaying)
                        return ToolDispatchResult.Ok(BuildStateEnvelope("stop_noop",
                            "Not in play mode; nothing to stop."));
                    break;
            }

            // Dirty guard — entering play mode especially. Mirror SceneDirtyGuard
            // semantics so the same dirty-scene refusal + ignore_scene_dirty opt-
            // out the restart_then_settle tools use applies here too.
            if (state == EditorStateTarget.Play && !ignoreDirty)
            {
                var guard = SceneDirtyGuard.Check();
                if (!guard.Allowed)
                    return ToolDispatchResult.Fail("scene_dirty", guard.RefusalMessage);
            }

            try
            {
                switch (state)
                {
                    case EditorStateTarget.Play:
                        EditorApplication.isPlaying = true;
                        break;
                    case EditorStateTarget.Pause:
                        EditorApplication.isPaused = !EditorApplication.isPaused;
                        break;
                    case EditorStateTarget.Stop:
                        EditorApplication.isPlaying = false;
                        break;
                }
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("state_change_failed", e.Message);
            }

            var action = state == EditorStateTarget.Play ? "play"
                : state == EditorStateTarget.Pause ? "pause"
                : "stop";
            return ToolDispatchResult.Ok(BuildStateEnvelope(action, null));
        }

        // =========================== Selection ============================

        // Read the current selection (active object + full selection array).
        // Gate-free read.
        public static ToolDispatchResult SelectionGet(string body)
        {
            int maxResults = JsonBody.GetInt(body, "max_results", 50);
            if (maxResults < 1) maxResults = 1;

            var active = Selection.activeObject;
            var all = Selection.objects;
            int total = all != null ? all.Length : 0;
            int emitted = 0;
            int truncated = 0;

            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",\"active\":");
            sb.Append(SerializeSelectionEntry(active));
            sb.Append(",\"selection\":[");
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    if (emitted >= maxResults) { truncated = all.Length - i; break; }
                    if (emitted > 0) sb.Append(',');
                    sb.Append(SerializeSelectionEntry(all[i]));
                    emitted++;
                }
            }
            sb.Append("],\"count\":").Append(emitted);
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"truncated\":").Append(truncated).Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Set the selection. Resolve targets by instance_id (scene object) /
        // path (hierarchy) / name / asset_path (asset on disk). Supports single
        // target shorthand or a multi-target array. Undo-recorded so a human
        // can Ctrl+Z it.
        public static ToolDispatchResult SelectionSet(string body)
        {
            // Clear shortcut.
            if (JsonBody.GetBool(body, "clear", false))
            {
                Selection.objects = System.Array.Empty<Object>();
                return ToolDispatchResult.Ok(BuildSelectionResult(0, 0, "cleared"));
            }

            var targetObjects = ResolveTargets(body);
            if (targetObjects == null)
                return ToolDispatchResult.Fail("target_not_found",
                    "None of the provided targets resolved to a live object or asset. " +
                    "Resolve by instance_id (scene object), path (hierarchy), name (scene), or asset_path.");

            // Selection.objects fails when an entry is null; we filtered those
            // out in ResolveTargets, so this is safe.
            Selection.objects = targetObjects;

            int count = targetObjects.Length;
            return ToolDispatchResult.Ok(BuildSelectionResult(count, count,
                count == 0 ? "cleared" : "set"));
        }

        // ========================= Undo / Redo ===========================

        // Perform N undo steps. Folds UUMCP editor_undo.
        public static ToolDispatchResult EditorUndo(string body)
        {
            int steps = JsonBody.GetInt(body, "steps", 1);
            if (steps < 1) steps = 1;

            for (int i = 0; i < steps; i++)
                Undo.PerformUndo();

            return ToolDispatchResult.Ok(BuildUndoResult("undo", steps));
        }

        // Perform N redo steps. Folds UUMCP editor_redo.
        public static ToolDispatchResult EditorRedo(string body)
        {
            int steps = JsonBody.GetInt(body, "steps", 1);
            if (steps < 1) steps = 1;

            for (int i = 0; i < steps; i++)
                Undo.PerformRedo();

            return ToolDispatchResult.Ok(BuildUndoResult("redo", steps));
        }

        // ========================= Tags / Layers =========================

        // List every configured tag (built-in + user). Gate-free read.
        public static ToolDispatchResult EditorGetTags(string body)
        {
            var tags = InternalEditorUtility.tags;
            var sb = new StringBuilder(128 + tags.Length * 16);
            sb.Append("{\"status\":\"ok\",\"count\":").Append(tags.Length);
            sb.Append(",\"tags\":[");
            for (int i = 0; i < tags.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Esc(tags[i])).Append('"');
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // List every non-empty layer slot (index + name). Gate-free read.
        // Includes slot indices so gameobject_modify (layer) callers know which
        // integer to pass.
        public static ToolDispatchResult EditorGetLayers(string body)
        {
            var layers = InternalEditorUtility.layers;
            var resolved = new List<(int index, string name)>(layers.Length);
            foreach (var name in layers)
            {
                int idx = LayerMask.NameToLayer(name);
                resolved.Add((idx, name));
            }

            var sb = new StringBuilder(128 + resolved.Count * 32);
            sb.Append("{\"status\":\"ok\",\"count\":").Append(resolved.Count);
            sb.Append(",\"layers\":[");
            for (int i = 0; i < resolved.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(resolved[i].index);
                sb.Append(",\"name\":\"").Append(Esc(resolved[i].name)).Append("\"}");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Add a user tag to the TagManager and save the asset. Mutating: runs
        // through the gate envelope with paths_hint scoped to TagManager.asset.
        // Idempotent — adding an existing tag is a no-op. Folds UCP
        // settings/add-tag.
        public static ToolDispatchResult EditorAddTag(string body)
        {
            var tagRaw = JsonBody.GetString(body, "tag");
            if (string.IsNullOrWhiteSpace(tagRaw))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'tag' is required and must be a non-empty string.");
            var tag = tagRaw.Trim();

            if (IsReservedTag(tag))
                return ToolDispatchResult.Fail("reserved_tag",
                    $"'{tag}' is a reserved Unity built-in tag and cannot be added.");

            var so = LoadTagManagerSerialized();
            if (so == null)
                return ToolDispatchResult.Fail("tag_manager_unavailable",
                    "Could not load ProjectSettings/TagManager.asset.");
            try
            {
                var tagsProp = so.FindProperty("tags");
                if (tagsProp == null)
                    return ToolDispatchResult.Fail("tag_manager_unavailable",
                        "TagManager has no 'tags' serialized property in this Unity version.");

                for (int i = 0; i < tagsProp.arraySize; i++)
                {
                    if (tagsProp.GetArrayElementAtIndex(i).stringValue == tag)
                    {
                        var noop = new StringBuilder(80);
                        noop.Append("{\"status\":\"ok\",\"saved\":false,\"tag\":\"").Append(Esc(tag));
                        noop.Append("\",\"note\":\"Tag already exists; no write performed.\"}");
                        return ToolDispatchResult.Ok(noop.ToString());
                    }
                }

                tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
                so.ApplyModifiedProperties();
                SaveTagManagerAsset();
            }
            finally { so?.Dispose(); }

            var sb = new StringBuilder(80);
            sb.Append("{\"status\":\"ok\",\"saved\":true,\"tag\":\"").Append(Esc(tag)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Add a user layer to the TagManager and save the asset. Mutating: runs
        // through the gate envelope with paths_hint scoped to TagManager.asset.
        // By default picks the first empty slot (8–31); pass `slot` to assign a
        // specific index. Folds UCP settings/add-layer.
        public static ToolDispatchResult EditorAddLayer(string body)
        {
            var layerRaw = JsonBody.GetString(body, "layer");
            if (string.IsNullOrWhiteSpace(layerRaw))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'layer' is required and must be a non-empty string.");
            var layer = layerRaw.Trim();

            if (IsReservedLayer(layer))
                return ToolDispatchResult.Fail("reserved_layer",
                    $"'{layer}' is a reserved Unity built-in layer name.");

            var so = LoadTagManagerSerialized();
            if (so == null)
                return ToolDispatchResult.Fail("tag_manager_unavailable",
                    "Could not load ProjectSettings/TagManager.asset.");

            int? requestedSlot = TryGetSlot(body);
            int targetSlot;
            try
            {
                var layersProp = so.FindProperty("layers");
                if (layersProp == null || layersProp.arraySize < 32)
                    return ToolDispatchResult.Fail("tag_manager_unavailable",
                        "TagManager has no usable 'layers' serialized property in this Unity version.");

                // If the name is already assigned, this is idempotent only when
                // the caller did not request a different slot.
                for (int i = 0; i < 32; i++)
                {
                    if (layersProp.GetArrayElementAtIndex(i).stringValue == layer)
                    {
                        if (requestedSlot.HasValue && requestedSlot.Value != i)
                            return ToolDispatchResult.Fail("layer_in_use",
                                $"Layer name '{layer}' is already assigned to slot {i}; cannot move it to slot {requestedSlot.Value} via this tool.");
                        var noop = new StringBuilder(96);
                        noop.Append("{\"status\":\"ok\",\"saved\":false,\"layer\":\"").Append(Esc(layer));
                        noop.Append("\",\"slot\":").Append(i);
                        noop.Append(",\"note\":\"Layer already exists; no write performed.\"}");
                        return ToolDispatchResult.Ok(noop.ToString());
                    }
                }

                if (requestedSlot.HasValue)
                {
                    targetSlot = requestedSlot.Value;
                    if (targetSlot < 8 || targetSlot > 31)
                        return ToolDispatchResult.Fail("invalid_parameter",
                            $"'slot' must be in 8..31 (0..7 are reserved for built-ins). Got {targetSlot}.");
                    if (!string.IsNullOrEmpty(layersProp.GetArrayElementAtIndex(targetSlot).stringValue))
                        return ToolDispatchResult.Fail("slot_occupied",
                            $"Slot {targetSlot} is already occupied by layer " +
                            $"'{layersProp.GetArrayElementAtIndex(targetSlot).stringValue}'.");
                }
                else
                {
                    targetSlot = -1;
                    for (int i = 8; i <= 31; i++)
                    {
                        if (string.IsNullOrEmpty(layersProp.GetArrayElementAtIndex(i).stringValue))
                        {
                            targetSlot = i;
                            break;
                        }
                    }
                    if (targetSlot < 0)
                        return ToolDispatchResult.Fail("no_free_slot",
                            "All user layer slots (8..31) are occupied. Remove a layer first.");
                }

                layersProp.GetArrayElementAtIndex(targetSlot).stringValue = layer;
                so.ApplyModifiedProperties();
                SaveTagManagerAsset();
            }
            finally { so?.Dispose(); }

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"saved\":true,\"layer\":\"").Append(Esc(layer));
            sb.Append("\",\"slot\":").Append(targetSlot).Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // =========================== helpers ==============================

        enum LogLevel { Log, Warning, Error }

        static LogLevel ParseLogLevel(string raw, LogLevel fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return raw.ToLowerInvariant() switch
            {
                "log" => LogLevel.Log,
                "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => fallback,
            };
        }

        static string LogLevelToWire(LogLevel l) => l switch
        {
            LogLevel.Log => "log",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            _ => "log",
        };

        enum EditorStateTarget { Unknown, Play, Pause, Stop }

        static EditorStateTarget ParseEditorState(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return EditorStateTarget.Unknown;
            return raw.ToLowerInvariant() switch
            {
                "play" => EditorStateTarget.Play,
                "pause" => EditorStateTarget.Pause,
                "stop" => EditorStateTarget.Stop,
                _ => EditorStateTarget.Unknown,
            };
        }

        static string BuildStateEnvelope(string action, string note)
        {
            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"action\":\"").Append(Esc(action)).Append("\"");
            sb.Append(",\"isPlaying\":").Append(EditorApplication.isPlaying ? "true" : "false");
            sb.Append(",\"isPaused\":").Append(EditorApplication.isPaused ? "true" : "false");
            if (!string.IsNullOrEmpty(note))
                sb.Append(",\"note\":\"").Append(Esc(note)).Append("\"");
            sb.Append(",\"agentNextSteps\":[\"Poll unity_open_mcp_editor_status to confirm the transition settled.\"]}");
            return sb.ToString();
        }

        // Resolve the targets for selection_set into an Object[] (nulls
        // filtered). Returns null when NOTHING resolved (so the caller can
        // surface target_not_found distinctly from "clear the selection").
        // Empty input + no clear flag returns an empty array (valid — clears).
        static Object[] ResolveTargets(string body)
        {
            var targets = JsonBody.GetObjectArray(body, "targets");
            if (targets != null)
            {
                var resolved = new List<Object>(targets.Length);
                bool anyProvided = false;
                foreach (var entry in targets)
                {
                    anyProvided = true;
                    var obj = ResolveOneTarget(entry);
                    if (obj != null) resolved.Add(obj);
                }
                if (!anyProvided) return System.Array.Empty<Object>();
                return resolved.Count == 0 ? null : resolved.ToArray();
            }

            // Single-target shorthand.
            var single = ResolveOneTarget(body);
            if (single == null)
            {
                // If the caller supplied none of the resolver fields at all,
                // treat it as "clear" (empty array); otherwise it's a failed
                // resolution (null).
                int instanceId = JsonBody.GetInt(body, "instance_id", 0);
                var path = JsonBody.GetString(body, "path");
                var name = JsonBody.GetString(body, "name");
                var assetPath = JsonBody.GetString(body, "asset_path");
                if (instanceId == 0 && string.IsNullOrEmpty(path)
                    && string.IsNullOrEmpty(name) && string.IsNullOrEmpty(assetPath))
                    return System.Array.Empty<Object>();
                return null;
            }
            return new[] { single };
        }

        static Object ResolveOneTarget(string body)
        {
            var assetPath = JsonBody.GetString(body, "asset_path");
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.LoadAssetAtPath<Object>(assetPath);

            int instanceId = JsonBody.GetInt(body, "instance_id", 0);
            if (instanceId != 0)
                return EditorUtility.InstanceIDToObject(instanceId);

            var path = JsonBody.GetString(body, "path");
            if (!string.IsNullOrEmpty(path))
            {
                var go = TypedTargets.FindByPath(path);
                if (go != null) return go;
            }

            var name = JsonBody.GetString(body, "name");
            if (!string.IsNullOrEmpty(name))
            {
                var go = TypedTargets.FindByName(name);
                if (go != null) return go;
            }
            return null;
        }

        static string BuildSelectionResult(int count, int total, string action)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"action\":\"").Append(Esc(action)).Append("\"");
            sb.Append(",\"count\":").Append(count);
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"agentNextSteps\":[\"Call unity_open_mcp_selection_get to confirm the new selection.\"]}");
            return sb.ToString();
        }

        static string SerializeSelectionEntry(Object obj)
        {
            if (obj == null) return "null";
            var sb = new StringBuilder(96);
            sb.Append('{');
            sb.Append("\"name\":\"").Append(Esc(obj.name ?? "")).Append("\"");
            sb.Append(",\"instanceId\":").Append(obj.GetInstanceID());
            sb.Append(",\"type\":\"").Append(Esc(obj.GetType().Name)).Append("\"");
            sb.Append(",\"fullName\":\"").Append(Esc(obj.GetType().FullName ?? "")).Append("\"");
            var path = AssetDatabase.GetAssetPath(obj);
            bool isAsset = !string.IsNullOrEmpty(path) && path != "Library/unity editor resources";
            sb.Append(",\"isAsset\":").Append(isAsset ? "true" : "false");
            if (isAsset) sb.Append(",\"assetPath\":\"").Append(Esc(path)).Append("\"");
            // Scene GameObjects get a hierarchy path for downstream typed tools.
            if (obj is GameObject go)
                sb.Append(",\"path\":\"").Append(Esc(TypedTargets.HierarchyPath(go))).Append("\"");
            sb.Append('}');
            return sb.ToString();
        }

        static string BuildUndoResult(string action, int steps)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"action\":\"").Append(Esc(action)).Append("\"");
            sb.Append(",\"steps\":").Append(steps);
            // Surface the new active selection so the agent knows what the undo
            // landed on (Undo.PerformUndo restores the selection as a side-effect).
            var active = Selection.activeObject;
            sb.Append(",\"activeSelection\":").Append(SerializeSelectionEntry(active));
            sb.Append('}');
            return sb.ToString();
        }

        // Reflect UnityEditor.LogEntries.Clear() — the type is internal, so we
        // can't bind to it directly. Mirrors the reflection used by
        // ReadConsoleTool (LogEntriesReader) for cross-version safety.
        static bool LogEntriesClearViaReflection()
        {
            var t = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
            if (t == null) return false;
            var m = t.GetMethod("Clear",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (m == null) return false;
            m.Invoke(null, null);
            return true;
        }

        // =================== TagManager helpers =========================

        // Load the TagManager as a SerializedObject. UCP / unity-cli both use
        // AssetDatabase.LoadMainAssetAtPath on the well-known ProjectSettings
        // path; this returns the live TagManager asset Unity keeps loaded.
        static SerializedObject LoadTagManagerSerialized()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");
            if (asset == null) return null;
            return new SerializedObject(asset);
        }

        static void SaveTagManagerAsset()
        {
            AssetDatabase.SaveAssetIfDirty(
                AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
            AssetDatabase.Refresh();
        }

        static readonly HashSet<string> ReservedTags = new HashSet<string>
        {
            "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController"
        };

        static bool IsReservedTag(string tag) => ReservedTags.Contains(tag);

        static readonly HashSet<string> ReservedLayers = new HashSet<string>
        {
            "Default", "TransparentFX", "IgnoreRaycast", "Water", "UI"
        };

        static bool IsReservedLayer(string layer) => ReservedLayers.Contains(layer);

        static int? TryGetSlot(string body)
        {
            var raw = JsonBody.GetRawValue(body, "slot");
            if (string.IsNullOrEmpty(raw)) return null;
            if (int.TryParse(raw.Trim(), out var slot)) return slot;
            return null;
        }

        // Escape a string for inline JSON (mirrors PackagesTools.Esc /
        // TypedTargets.Esc so responses are byte-identical in style).
        static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 4);
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
            return sb.ToString();
        }
    }
}
