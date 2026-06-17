using System;
using System.IO;
using UnityEngine;

namespace UnityOpenMcpVerify.Batch
{
    // Project-level settings reader for the verify package.
    //
    // The verify package must stay usable standalone (no dependency on the
    // bridge), so it reads its own slice of the shared settings file at
    // `<project>/.unity-open-mcp/settings.json` rather than going through the
    // bridge's `BridgeProjectSettings`. Today the verify-owned slice carries a
    // single field:
    //
    //   {
    //     "verify": { "severityThreshold": "error" | "warning" | "info" }
    //   }
    //
    // `severityThreshold` is the project default for what counts as a gate
    // failure — it flows into scan_paths / validate_edit `passed` flags and the
    // batch fail-on-severity decision. An explicit per-call `fail_on_severity`
    // argument always wins.
    //
    // The file is the same one the bridge reads; both readers tolerate each
    // other's fields because unknown JSON keys are ignored. Missing / unreadable
    // files produce a sane default (`error`) so verify keeps working even when
    // no settings file is present.
    [Serializable]
    public class VerifyProjectSettingsData
    {
        public VerifySettingsSlice verify = new VerifySettingsSlice();
    }

    [Serializable]
    public class VerifySettingsSlice
    {
        // Stored lowercase ("error" / "warning" / "info") to match the
        // fail_on_severity enum surface the MCP tools already accept.
        public string severityThreshold = "error";
    }

    public static class VerifyProjectSettings
    {
        const string SettingsDirName = ".unity-open-mcp";
        const string SettingsFileName = "settings.json";

        // The verify default mirrors the historical gate behaviour: a single
        // Error fails the gate. Projects that want to count warnings as failures
        // opt in via `warning`; `info` is a "fail on anything non-clean" mode.
        public const string DefaultSeverityThreshold = "error";

        static VerifyProjectSettingsData _data;
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

        /// <summary>
        /// Project-default fail-on-severity value. Always one of the strings in
        /// <see cref="SeverityThreshold.ValidValues"/>. Falls back to
        /// <see cref="DefaultSeverityThreshold"/> when the file is absent or the
        /// stored value is unparseable.
        /// </summary>
        public static string SeverityThreshold
        {
            get
            {
                if (!_loaded) Load();
                return Normalize(_data?.verify?.severityThreshold);
            }
        }

        public static void Load()
        {
            _data = new VerifyProjectSettingsData();
            var path = SettingsPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _loaded = true;
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonUtility.FromJson<VerifyProjectSettingsData>(json);
                if (parsed != null)
                {
                    _data = parsed;
                    if (_data.verify == null)
                        _data.verify = new VerifySettingsSlice();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    $"[VerifyProjectSettings] Failed to read '{path}': {e.Message}. Using default severity threshold '{DefaultSeverityThreshold}'.");
                _data = new VerifyProjectSettingsData();
            }
            finally
            {
                _loaded = true;
            }
        }

        /// <summary>Reset the cached settings. Used by tests.</summary>
        public static void Reload()
        {
            _loaded = false;
            Load();
        }

        /// <summary>
        /// Force the cached settings for tests. Avoids touching the disk when a
        /// test just wants to exercise the threshold-resolution path.
        /// </summary>
        public static void OverrideForTests(string severityThreshold)
        {
            _data = new VerifyProjectSettingsData
            {
                verify = new VerifySettingsSlice
                {
                    severityThreshold = string.IsNullOrEmpty(severityThreshold)
                        ? DefaultSeverityThreshold
                        : severityThreshold
                }
            };
            _loaded = true;
        }

        static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return DefaultSeverityThreshold;

            // Accept both the fail_on_severity spelling ("warn") and the spec's
            // spelling ("warning" / "info" / "error") so the settings file
            // reads naturally to humans.
            switch (raw.ToLowerInvariant())
            {
                case "error":
                case "err":
                    return "error";
                case "warning":
                case "warn":
                    return "warn";
                case "info":
                    return "info";
                case "verbose":
                    return "verbose";
                case "never":
                case "off":
                    return "never";
                default:
                    return DefaultSeverityThreshold;
            }
        }

        static string GetProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath)) return null;
            return Path.GetDirectoryName(dataPath);
        }
    }
}
