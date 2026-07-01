using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityOpenMcpVerify.Cache;

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

        // M14 T5.2 / T5.3 — deny heuristic patterns for the power tools. null
        // ⇒ use the built-in defaults; an empty array is the explicit "turned
        // off" signal. Each entry is a regex matched against the submitted
        // snippet / menu_path. Invalid regexes are dropped at compile time.
        public string[] csharpDenyPatterns;
        public string[] menuDenyPatterns;

        // M14 T5.4 — listener bind address. "127.0.0.1" (loopback only) is the
        // safe default; "0.0.0.0" enables remote access and is refused at start
        // unless authMode is "required". Any other value coerces to 127.0.0.1.
        public string bindAddress = BridgeBindAddress.Loopback;

        // M14 T5.5 — opt-in on-disk audit log. When true, every gate mutation
        // (pass / fail / warn) is appended to a rolling JSON-lines file under
        // .unity-open-mcp/audit/. Survives domain reload and editor restart.
        public bool auditLogEnabled = false;

        // TTL (seconds) for the in-memory verify health snapshot cache
        // (VerifyCacheService). Governs how stale the `health/summary` MCP
        // resource and the `gate_budget_estimate` "cache" mode consider the
        // last scan/validate/gate result. Clamped to [15, 3600] on load/write;
        // default 60s matches VerifyCacheService.DefaultTtl.
        public int verifyCacheTtlSeconds = 60;

        // Compile-settle wait caps (ms). After an EditorSettle /
        // RestartThenSettle mutation the dispatcher blocks on the worker
        // thread polling isCompiling until it flips false OR the cap elapses.
        // editorSettleCapMs covers asset refresh / import settle (default 5s);
        // restartSettleCapMs covers a real domain reload (default 60s).
        // Clamped to [1000, 120000] on load. See EditorSettleWait.
        public int editorSettleCapMs = 5000;
        public int restartSettleCapMs = 60000;

        // Fair round-robin queue tunables. When ≥2 agents (distinct X-Agent-Id
        // headers) share one bridge, requests are scheduled read-batch(N)/
        // write-serialize(1) per Editor frame so a write-heavy agent cannot
        // starve read-heavy agents. fairQueueEnabled lets an operator force-
        // disable the queue (single-agent bypass is always on regardless).
        // fairQueueReadsPerFrame is clamped to [1, 50]; default 5. See
        // BridgeRequestQueue.
        public bool fairQueueEnabled = true;
        public int fairQueueReadsPerFrame = 5;
    }

    public static class BridgeProjectSettings
    {
        private const string SettingsDirName = ".unity-open-mcp";
        private const string SettingsFileName = "settings.json";
        private const string TempSuffix = ".tmp";

        private static BridgeProjectSettingsData _data = new BridgeProjectSettingsData();
        private static bool _loaded;

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
                    // M14 T5.2 / T5.3 — deny pattern arrays are kept as-is
                    // (null ⇒ defaults, empty ⇒ explicitly disabled). The
                    // evaluator canonicalizes them; we only guard against a
                    // JsonUtility quirk where a missing field can deserialize
                    // to a 1-element array with a null entry.
                    _data.csharpDenyPatterns = StripNullEntries(_data.csharpDenyPatterns);
                    _data.menuDenyPatterns = StripNullEntries(_data.menuDenyPatterns);
                    // M14 T5.4 — coerce invalid bind addresses to loopback.
                    if (!BridgeBindAddress.IsValid(_data.bindAddress))
                        _data.bindAddress = BridgeBindAddress.Loopback;
                    // Clamp the verify cache TTL into range; out-of-range values
                    // on disk (e.g. hand-edited settings.json) fall back to 60s.
                    _data.verifyCacheTtlSeconds = ClampVerifyCacheTtl(_data.verifyCacheTtlSeconds);
                    // Clamp the compile-settle wait caps + fair-queue tunables.
                    _data.editorSettleCapMs = ClampSettleCap(_data.editorSettleCapMs, DefaultEditorSettleCapMs);
                    _data.restartSettleCapMs = ClampSettleCap(_data.restartSettleCapMs, DefaultRestartSettleCapMs);
                    _data.fairQueueReadsPerFrame = ClampFairQueueReads(_data.fairQueueReadsPerFrame);
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
                // Push the persisted TTL into the runtime cache service so the
                // editor honors it on every load / domain reload. Done in finally
                // so a fresh default data object also seeds the default TTL.
                ApplyRuntimeSettings();
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

        // M14 T5.2 / T5.3 — deny heuristic patterns. null ⇒ built-in defaults
        // (the evaluator handles the fallback); an explicit empty array is the
        // "turned off" signal. Returning the raw stored array (not a clone) is
        // intentional — the deny-list cache keys on reference equality, so a
        // caller that mutates the array in place would corrupt the cache. The
        // setters below always replace the array reference, which is the only
        // safe mutation path.
        public static string[] CSharpDenyPatterns
        {
            get
            {
                if (!_loaded) Load();
                return _data.csharpDenyPatterns;
            }
        }

        public static void SetCSharpDenyPatterns(IEnumerable<string> patterns)
        {
            if (!_loaded) Load();
            _data.csharpDenyPatterns = NormalizePatternArray(patterns);
            Save();
            try { Changed?.Invoke(); } catch { }
        }

        public static string[] MenuDenyPatterns
        {
            get
            {
                if (!_loaded) Load();
                return _data.menuDenyPatterns;
            }
        }

        public static void SetMenuDenyPatterns(IEnumerable<string> patterns)
        {
            if (!_loaded) Load();
            _data.menuDenyPatterns = NormalizePatternArray(patterns);
            Save();
            try { Changed?.Invoke(); } catch { }
        }

        // M14 T5.4 — listener bind address. Canonicalized through
        // BridgeBindAddress so callers never see an out-of-set value.
        public static string BindAddress
        {
            get
            {
                if (!_loaded) Load();
                return BridgeBindAddress.IsValid(_data.bindAddress)
                    ? _data.bindAddress
                    : BridgeBindAddress.Loopback;
            }
        }

        public static void SetBindAddress(string value)
        {
            if (!_loaded) Load();
            if (!BridgeBindAddress.IsValid(value))
            {
                Debug.LogWarning($"[BridgeProjectSettings] Ignoring invalid bindAddress '{value}'. " +
                                 $"Valid values: {string.Join(", ", BridgeBindAddress.ValidAddresses)}.");
                return;
            }
            if (_data.bindAddress == value) return;
            _data.bindAddress = value;
            Save();
            try { Changed?.Invoke(); } catch { }
        }

        // M14 T5.5 — on-disk audit log opt-in.
        public static bool AuditLogEnabled
        {
            get
            {
                if (!_loaded) Load();
                return _data.auditLogEnabled;
            }
        }

        public static void SetAuditLogEnabled(bool value)
        {
            if (!_loaded) Load();
            if (_data.auditLogEnabled == value) return;
            _data.auditLogEnabled = value;
            Save();
            try { Changed?.Invoke(); } catch { }
        }

        // Verify cache TTL (seconds). Controls how long the in-memory verify
        // health snapshot (VerifyCacheService) is considered fresh — drives the
        // `health/summary` MCP resource and `gate_budget_estimate` cache mode.
        // Clamped to [MinVerifyCacheTtlSeconds, MaxVerifyCacheTtlSeconds].
        public const int MinVerifyCacheTtlSeconds = 15;
        public const int MaxVerifyCacheTtlSeconds = 3600;
        public const int DefaultVerifyCacheTtlSeconds = 60;

        // Compile-settle wait cap clamps (ms). See EditorSettleWait.
        public const int MinSettleCapMs = 1000;
        public const int MaxSettleCapMs = 120000;
        public const int DefaultEditorSettleCapMs = 5000;
        public const int DefaultRestartSettleCapMs = 60000;

        // Fair-queue read-batch clamps. See BridgeRequestQueue.
        public const int MinFairQueueReadsPerFrame = 1;
        public const int MaxFairQueueReadsPerFrame = 50;
        public const int DefaultFairQueueReadsPerFrame = 5;

        public static int VerifyCacheTtlSeconds
        {
            get
            {
                if (!_loaded) Load();
                return ClampVerifyCacheTtl(_data.verifyCacheTtlSeconds);
            }
        }

        public static void SetVerifyCacheTtlSeconds(int value)
        {
            if (!_loaded) Load();
            var clamped = ClampVerifyCacheTtl(value);
            if (_data.verifyCacheTtlSeconds == clamped)
            {
                // Still apply the runtime side effect in case the in-memory TTL
                // drifted (e.g. a test set VerifyCacheService.Ttl directly).
                ApplyVerifyCacheTtl(clamped);
                return;
            }
            _data.verifyCacheTtlSeconds = clamped;
            Save();
            ApplyVerifyCacheTtl(clamped);
            try { Changed?.Invoke(); } catch { }
        }

        // Clamp an arbitrary int into the allowed TTL range. Values outside the
        // range (0, negative, huge, or non-sensical hand-edited values) fall back
        // to the default rather than being rejected outright.
        private static int ClampVerifyCacheTtl(int value)
        {
            if (value < MinVerifyCacheTtlSeconds) return DefaultVerifyCacheTtlSeconds;
            if (value > MaxVerifyCacheTtlSeconds) return MaxVerifyCacheTtlSeconds;
            return value;
        }

        /// Clamp a settle-window cap into [MinSettleCapMs, MaxSettleCapMs],
        /// falling back to the per-cap default when below the minimum.
        private static int ClampSettleCap(int value, int defaultCap)
        {
            if (value < MinSettleCapMs) return defaultCap;
            if (value > MaxSettleCapMs) return MaxSettleCapMs;
            return value;
        }

        /// Clamp the fair-queue reads-per-frame into range.
        private static int ClampFairQueueReads(int value)
        {
            if (value < MinFairQueueReadsPerFrame) return DefaultFairQueueReadsPerFrame;
            if (value > MaxFairQueueReadsPerFrame) return MaxFairQueueReadsPerFrame;
            return value;
        }

        // Compile-settle wait caps (ms). EditorSettleWait reads these instead
        // of its old hardcoded consts so an operator can tune the settle window
        // via .unity-open-mcp/settings.json without a code change.
        public static int EditorSettleCapMs
        {
            get
            {
                if (!_loaded) Load();
                return ClampSettleCap(_data.editorSettleCapMs, DefaultEditorSettleCapMs);
            }
            set
            {
                if (!_loaded) Load();
                _data.editorSettleCapMs = ClampSettleCap(value, DefaultEditorSettleCapMs);
            }
        }

        public static int RestartSettleCapMs
        {
            get
            {
                if (!_loaded) Load();
                return ClampSettleCap(_data.restartSettleCapMs, DefaultRestartSettleCapMs);
            }
            set
            {
                if (!_loaded) Load();
                _data.restartSettleCapMs = ClampSettleCap(value, DefaultRestartSettleCapMs);
            }
        }

        // Fair-queue tunables. BridgeRequestQueue reads these to decide whether
        // to activate the scheduler and how many reads to batch per frame.
        public static bool FairQueueEnabled
        {
            get
            {
                if (!_loaded) Load();
                return _data.fairQueueEnabled;
            }
            set
            {
                if (!_loaded) Load();
                _data.fairQueueEnabled = value;
            }
        }

        public static int FairQueueReadsPerFrame
        {
            get
            {
                if (!_loaded) Load();
                return ClampFairQueueReads(_data.fairQueueReadsPerFrame);
            }
            set
            {
                if (!_loaded) Load();
                _data.fairQueueReadsPerFrame = ClampFairQueueReads(value);
            }
        }

        // Push the persisted TTL into the runtime cache service. Called on Load
        // and on SetVerifyCacheTtlSeconds so the editor and the cache stay in
        // sync without each reader having to re-resolve the setting.
        private static void ApplyVerifyCacheTtl(int seconds)
        {
            try
            {
                VerifyCacheService.Ttl = TimeSpan.FromSeconds(seconds);
            }
            catch (Exception e)
            {
                // The cache service is in the verify assembly; never let a wiring
                // failure here break settings load.
                Debug.LogWarning($"[BridgeProjectSettings] Failed to apply verify cache TTL: {e.Message}");
            }
        }

        // Single wiring point for runtime-bound settings (values that live in a
        // static service in addition to the persisted JSON). Called from Load().
        private static void ApplyRuntimeSettings()
        {
            ApplyVerifyCacheTtl(ClampVerifyCacheTtl(_data.verifyCacheTtlSeconds));
        }

        // Keep the array shape consistent: null when no patterns supplied (so
        // the evaluator applies built-in defaults), otherwise a de-duplicated
        // array with empties/whitespace/invalid entries stripped. This is also
        // the single place that validates each regex syntactically — a bad
        // pattern is logged and dropped here rather than failing the cache
        // compile at request time.
        //
        // Note: JsonUtility serializes null arrays as [] on disk, so the
        // null-vs-empty distinction is lost across a save/load round-trip. The
        // deny-list evaluator (BridgeDenyList.Resolve*) treats both as "use
        // defaults"; callers wanting the list fully off pass a custom pattern
        // that matches nothing or use the per-request bypass.
        private static string[] NormalizePatternArray(IEnumerable<string> patterns)
        {
            if (patterns == null) return null;
            var set = new List<string>();
            foreach (var p in patterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    // Compile-and-discard to validate syntax. Timeout matches
                    // the runtime evaluator's per-pattern budget.
                    _ = new System.Text.RegularExpressions.Regex(p,
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    Debug.LogWarning($"[BridgeProjectSettings] Dropping invalid deny pattern: {p}");
                    continue;
                }
                if (!set.Contains(p)) set.Add(p);
            }
            return set.ToArray();
        }

        private static string[] StripNullEntries(string[] arr)
        {
            if (arr == null) return null;
            if (arr.Length == 0) return arr;
            var cleaned = new List<string>(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                if (!string.IsNullOrEmpty(arr[i])) cleaned.Add(arr[i]);
            }
            return cleaned.Count == arr.Length ? arr : cleaned.ToArray();
        }

        private static string GetProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath)) return null;
            return Path.GetDirectoryName(dataPath);
        }
    }
}
