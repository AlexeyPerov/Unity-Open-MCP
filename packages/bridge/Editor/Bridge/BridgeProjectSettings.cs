// Minimal project-level runtime settings store for the bridge UI.
//
// Backed by `.unity-agent/settings.json` at the project root. The schema is intentionally
// small in M4.5 Plan 2 (only `disabledTools` for runtime tool toggles); Plan 4 will extend
// the same store with gate default, auto-start, verbose-log, and other v1 runtime settings.
//
// The store is read once at static init and rewritten on every mutation. Writes are
// atomic via a `.tmp` rename. Missing / unreadable files produce an empty default — the
// bridge must keep running even when no settings file is present.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    [Serializable]
    public class BridgeProjectSettingsData
    {
        public string[] disabledTools = Array.Empty<string>();
    }

    public static class BridgeProjectSettings
    {
        const string SettingsDirName = ".unity-agent";
        const string SettingsFileName = "settings.json";
        const string TempSuffix = ".tmp";

        static BridgeProjectSettingsData _data = new BridgeProjectSettingsData();
        static bool _loaded;

        public static string SettingsPath
        {
            get
            {
                var projectRoot = GetProjectRoot();
                if (string.IsNullOrEmpty(projectRoot)) return null;
                return Path.Combine(projectRoot, SettingsDirName, SettingsFileName);
            }
        }

        public static BridgeProjectSettingsData Data
        {
            get
            {
                if (!_loaded) Load();
                return _data;
            }
        }

        public static event Action Changed;

        public static void Load()
        {
            _data = new BridgeProjectSettingsData();
            var path = SettingsPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _loaded = true;
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonUtility.FromJson<BridgeProjectSettingsData>(json);
                if (parsed != null)
                {
                    _data = parsed;
                    if (_data.disabledTools == null)
                        _data.disabledTools = Array.Empty<string>();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BridgeProjectSettings] Failed to read '{path}': {e.Message}. Using empty defaults.");
                _data = new BridgeProjectSettingsData();
            }
            finally
            {
                _loaded = true;
            }
        }

        public static void Save()
        {
            var path = SettingsPath;
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[BridgeProjectSettings] No project root available; cannot save settings.");
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonUtility.ToJson(_data ?? new BridgeProjectSettingsData(), true);
                var tmp = path + TempSuffix;
                File.WriteAllText(tmp, json);
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BridgeProjectSettings] Failed to write '{path}': {e.Message}");
                return;
            }

            try { Changed?.Invoke(); } catch { }
        }

        public static IReadOnlyCollection<string> DisabledTools
        {
            get
            {
                if (!_loaded) Load();
                return _data.disabledTools ?? Array.Empty<string>();
            }
        }

        public static void SetDisabledTools(IEnumerable<string> toolNames)
        {
            if (!_loaded) Load();
            var set = new HashSet<string>();
            if (toolNames != null)
            {
                foreach (var t in toolNames)
                {
                    if (!string.IsNullOrEmpty(t)) set.Add(t);
                }
            }
            _data.disabledTools = new List<string>(set).ToArray();
            Save();
        }

        public static bool IsDisabled(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            if (!_loaded) Load();
            var list = _data.disabledTools;
            if (list == null) return false;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == toolName) return true;
            }
            return false;
        }

        static string GetProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath)) return null;
            return Path.GetDirectoryName(dataPath);
        }
    }
}
