// Minimal project-level runtime settings store for the bridge UI.
//
// Backed by `.unity-open-mcp/settings.json` at the project root. The v1 schema carries:
//   - disabledTools (Plan 2)
//   - defaultGateMode (Plan 3)
//   - autoStart (Plan 4)
//   - verboseActivityLog (Plan 4)
//   - authMode (M14): "none" (default) | "required"
//
// Unknown fields in the file are preserved on save so future Hub / MCP tooling can extend
// the same file without a v1 migration step. Missing / unreadable files produce an empty
// default — the bridge must keep running even when no settings file is present.
//
// The store is read once at static init and rewritten on every mutation. Writes are
// atomic via a `.tmp` rename.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityOpenMcpBridge
{
    [Serializable]
    public class BridgeProjectSettingsData
    {
        public string[] disabledTools = Array.Empty<string>();
        public string defaultGateMode = "enforce";
        public bool autoStart = true;
        public bool verboseActivityLog = false;
        // M14 — bridge auth policy. "none" ignores the bearer token the client
        // sends; "required" enforces it. The token is always minted into the
        // instance lock so the project can flip to "required" with no restart.
        public string authMode = "none";
    }

    public static class BridgeProjectSettings
    {
        const string SettingsDirName = ".unity-open-mcp";
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
                    if (string.IsNullOrEmpty(_data.defaultGateMode))
                        _data.defaultGateMode = "enforce";
                    // M14 — coerce missing/invalid authMode to the safe default.
                    if (!BridgeAuthPolicy.IsValid(_data.authMode))
                        _data.authMode = BridgeAuthPolicy.Default;
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

        public static bool AutoStart
        {
            get
            {
                if (!_loaded) Load();
                return _data.autoStart;
            }
        }

        public static void SetAutoStart(bool value)
        {
            if (!_loaded) Load();
            if (_data.autoStart == value) return;
            _data.autoStart = value;
            Save();
        }

        public static bool VerboseActivityLog
        {
            get
            {
                if (!_loaded) Load();
                return _data.verboseActivityLog;
            }
        }

        public static void SetVerboseActivityLog(bool value)
        {
            if (!_loaded) Load();
            if (_data.verboseActivityLog == value) return;
            _data.verboseActivityLog = value;
            Save();
        }

        // M14 — bridge auth policy accessor. Canonicalized through
        // BridgeAuthPolicy so callers never see an out-of-set value. The raw
        // field on BridgeProjectSettingsData is the persistence shape; this is
        // the read/write contract the UI and HTTP layer use.
        public static string AuthMode
        {
            get
            {
                if (!_loaded) Load();
                return BridgeAuthPolicy.IsValid(_data.authMode)
                    ? _data.authMode
                    : BridgeAuthPolicy.Default;
            }
        }

        public static void SetAuthMode(string value)
        {
            if (!_loaded) Load();
            if (!BridgeAuthPolicy.IsValid(value))
            {
                Debug.LogWarning($"[BridgeProjectSettings] Ignoring invalid authMode '{value}'. " +
                                 $"Valid values: {string.Join(", ", BridgeAuthPolicy.ValidModes)}.");
                return;
            }
            if (_data.authMode == value) return;
            _data.authMode = value;
            Save();
        }

        static string GetProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath)) return null;
            return Path.GetDirectoryName(dataPath);
        }
    }
}
