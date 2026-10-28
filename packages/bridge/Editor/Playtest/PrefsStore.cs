// Playtest — read the platform PlayerPrefs / EditorPrefs store (all keys, typed
// values) for unity_open_mcp_player_state.
//
// Unity has no API to enumerate PlayerPrefs keys, so the snapshot reads the
// store the Editor persists them to:
//
//   macOS    — CFPreferences domain `unity.<company>.<product>`
//              (~/Library/Preferences/unity.<company>.<product>.plist), read
//              with `defaults export <domain> -` so the answer comes from the
//              preferences daemon rather than a plist file it may not have
//              rewritten yet. <integer> = int, <real> = float, <string> = string.
//   Windows  — registry HKCU\Software\Unity\UnityEditor\<company>\<product>.
//              Value names are `<key>_h<hash>` (djb2-xor over the key's UTF-8
//              bytes); ints are REG_DWORD (4 bytes), floats are tagged REG_DWORD
//              but carry an 8-byte IEEE double, strings are REG_BINARY UTF-8
//              with a trailing NUL.
//   Linux    — XML file ~/.config/unity3d/<company>/<product>/prefs
//              (`<pref name=".." type="int|float|string">`, strings base64).
//
// EditorPrefs (opt-in by key prefix) use the same encodings in the shared
// Editor store: macOS `com.unity3d.UnityEditor5.x`, Windows
// HKCU\Software\Unity Technologies\Unity Editor 5.x, Linux
// ~/.local/share/unity3d/prefs.
//
// The running Editor keeps PlayerPrefs in memory and writes the store on
// PlayerPrefs.Save() (and on quit): callers flush with PlayerPrefs.Save()
// before every read so the store holds what the game actually set. The store
// is never written here — restore goes through the PlayerPrefs API.
//
// macOS is verified live; the Windows and Linux readers are covered by unit
// tests on synthetic data only (their decoders are pure functions below).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using UnityEngine;

namespace UnityOpenMcpBridge.Playtest
{
    internal enum PrefType { Int, Float, String }

    // One typed preference value. Floats compare by bit pattern (NaN-safe).
    internal sealed class PrefEntry
    {
        public string Key;
        public PrefType Type;
        public int IntValue;
        public float FloatValue;
        public string StringValue;

        public static PrefEntry OfInt(string key, int v) => new PrefEntry { Key = key, Type = PrefType.Int, IntValue = v };
        public static PrefEntry OfFloat(string key, float v) => new PrefEntry { Key = key, Type = PrefType.Float, FloatValue = v };
        public static PrefEntry OfString(string key, string v) => new PrefEntry { Key = key, Type = PrefType.String, StringValue = v ?? "" };

        public bool SameValue(PrefEntry other)
        {
            if (other == null || other.Type != Type) return false;
            switch (Type)
            {
                case PrefType.Int: return IntValue == other.IntValue;
                case PrefType.Float: return FloatBits(FloatValue) == FloatBits(other.FloatValue);
                default: return string.Equals(StringValue, other.StringValue, StringComparison.Ordinal);
            }
        }

        public static string TypeName(PrefType t) => t == PrefType.Int ? "int" : t == PrefType.Float ? "float" : "string";

        public static bool TryParseType(string s, out PrefType t)
        {
            switch (s)
            {
                case "int": t = PrefType.Int; return true;
                case "float": t = PrefType.Float; return true;
                case "string": t = PrefType.String; return true;
                default: t = PrefType.String; return false;
            }
        }

        public static int FloatBits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
        public static float FromBits(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
    }

    // What a store read produced: the typed entries, keys whose stored type the
    // PlayerPrefs API cannot represent (reported, never silently dropped), and a
    // human description of where the data came from.
    internal sealed class PrefsReadResult
    {
        public readonly List<PrefEntry> Entries = new List<PrefEntry>();
        public readonly List<string> Unsupported = new List<string>();
        public string Source;
        public string Error;
    }

    internal enum PrefsPlatform { MacOS, Windows, Linux, Unsupported }

    internal static class PrefsStore
    {
        internal const string MacEditorPrefsDomain = "com.unity3d.UnityEditor5.x";
        internal const string WindowsEditorPrefsKey = @"Software\Unity Technologies\Unity Editor 5.x";

        internal static PrefsPlatform Current
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXEditor: return PrefsPlatform.MacOS;
                    case RuntimePlatform.WindowsEditor: return PrefsPlatform.Windows;
                    case RuntimePlatform.LinuxEditor: return PrefsPlatform.Linux;
                    default: return PrefsPlatform.Unsupported;
                }
            }
        }

        internal static string MacPlayerPrefsDomain(string company, string product) => "unity." + company + "." + product;
        internal static string WindowsPlayerPrefsKey(string company, string product) => @"Software\Unity\UnityEditor\" + company + @"\" + product;
        internal static string LinuxPlayerPrefsFile(string home, string company, string product) =>
            Path.Combine(home, ".config", "unity3d", company, product, "prefs");
        internal static string LinuxEditorPrefsFile(string home) => Path.Combine(home, ".local", "share", "unity3d", "prefs");

        internal static string Describe(PrefsPlatform platform, string company, string product)
        {
            switch (platform)
            {
                case PrefsPlatform.MacOS: return "macOS preferences domain '" + MacPlayerPrefsDomain(company, product) + "'";
                case PrefsPlatform.Windows: return @"registry HKCU\" + WindowsPlayerPrefsKey(company, product);
                case PrefsPlatform.Linux: return "file ~/.config/unity3d/" + company + "/" + product + "/prefs";
                default: return "unsupported platform";
            }
        }

        // ------------------------------------------------------------------
        // Live readers (caller flushes PlayerPrefs.Save() first)
        // ------------------------------------------------------------------

        internal static PrefsReadResult ReadPlayerPrefs(string company, string product)
        {
            var platform = Current;
            PrefsReadResult result;
            try
            {
                switch (platform)
                {
                    case PrefsPlatform.MacOS:
                        result = ParsePlistXml(RunDefaultsExport(MacPlayerPrefsDomain(company, product)));
                        break;
                    case PrefsPlatform.Windows:
                        result = ReadRegistry(WindowsPlayerPrefsKey(company, product));
                        break;
                    case PrefsPlatform.Linux:
                        result = ReadLinuxFile(LinuxPlayerPrefsFile(HomeDirectory(), company, product));
                        break;
                    default:
                        result = new PrefsReadResult { Error = "PlayerPrefs store reading is not supported on " + Application.platform + "." };
                        break;
                }
            }
            catch (Exception e)
            {
                result = new PrefsReadResult { Error = e.GetType().Name + ": " + e.Message };
            }
            result.Source = Describe(platform, company, product);
            return result;
        }

        // Every EditorPrefs key that starts with one of the prefixes (ordinal).
        internal static PrefsReadResult ReadEditorPrefs(IList<string> prefixes)
        {
            PrefsReadResult all;
            var platform = Current;
            try
            {
                switch (platform)
                {
                    case PrefsPlatform.MacOS:
                        all = ParsePlistXml(RunDefaultsExport(MacEditorPrefsDomain));
                        all.Source = "macOS preferences domain '" + MacEditorPrefsDomain + "'";
                        break;
                    case PrefsPlatform.Windows:
                        all = ReadRegistry(WindowsEditorPrefsKey);
                        all.Source = @"registry HKCU\" + WindowsEditorPrefsKey;
                        break;
                    case PrefsPlatform.Linux:
                        all = ReadLinuxFile(LinuxEditorPrefsFile(HomeDirectory()));
                        all.Source = "file ~/.local/share/unity3d/prefs";
                        break;
                    default:
                        all = new PrefsReadResult { Error = "EditorPrefs store reading is not supported on " + Application.platform + "." };
                        break;
                }
            }
            catch (Exception e)
            {
                all = new PrefsReadResult { Error = e.GetType().Name + ": " + e.Message };
            }
            return FilterByPrefix(all, prefixes);
        }

        internal static PrefsReadResult FilterByPrefix(PrefsReadResult all, IList<string> prefixes)
        {
            var filtered = new PrefsReadResult { Source = all.Source, Error = all.Error };
            if (prefixes == null || prefixes.Count == 0) return filtered;
            foreach (var e in all.Entries)
                if (HasPrefix(e.Key, prefixes)) filtered.Entries.Add(e);
            foreach (var u in all.Unsupported)
                if (HasPrefix(u, prefixes)) filtered.Unsupported.Add(u);
            return filtered;
        }

        internal static bool HasPrefix(string key, IList<string> prefixes)
        {
            if (key == null) return false;
            foreach (var p in prefixes)
                if (!string.IsNullOrEmpty(p) && key.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string HomeDirectory() =>
            Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ------------------------------------------------------------------
        // macOS: `defaults export <domain> -` → XML property list
        // ------------------------------------------------------------------

        private const int DefaultsTimeoutMs = 10_000;

        private static string RunDefaultsExport(string domain)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/defaults",
                Arguments = "export " + QuoteArgument(domain) + " -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using (var p = new System.Diagnostics.Process { StartInfo = psi })
            {
                var stderr = new StringBuilder();
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                if (!p.Start()) throw new IOException("Could not start /usr/bin/defaults.");
                p.BeginErrorReadLine();
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(DefaultsTimeoutMs))
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2_000); } catch { }
                    throw new TimeoutException("`defaults export " + domain + "` did not finish in " + DefaultsTimeoutMs + " ms.");
                }
                p.WaitForExit();
                var stdout = stdoutTask.Result;
                if (p.ExitCode != 0)
                    throw new IOException("`defaults export " + domain + "` exited " + p.ExitCode + ": " + stderr.ToString().Trim());
                return stdout;
            }
        }

        // Windows-style argument quoting (what Mono's Process parses on macOS):
        // wrap in quotes, escape embedded quotes and the backslashes before them.
        internal static string QuoteArgument(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            int backslashes = 0;
            foreach (var c in s)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
                sb.Append('\\', backslashes); backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static XmlReaderSettings SafeXml() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        // Parse an XML property list whose root <dict> maps pref keys to values.
        internal static PrefsReadResult ParsePlistXml(string xml)
        {
            var result = new PrefsReadResult();
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml ?? ""), SafeXml()))
                doc.Load(reader);
            XmlElement dict = null;
            var plist = doc.DocumentElement;
            if (plist != null && plist.Name == "plist")
            {
                foreach (XmlNode n in plist.ChildNodes)
                    if (n is XmlElement el) { dict = el; break; }
            }
            if (dict == null || dict.Name != "dict")
            {
                result.Error = "Property list has no root <dict>.";
                return result;
            }

            string pendingKey = null;
            foreach (XmlNode node in dict.ChildNodes)
            {
                if (!(node is XmlElement el)) continue;
                if (el.Name == "key")
                {
                    pendingKey = el.InnerText;
                    continue;
                }
                if (pendingKey == null) continue;
                var key = pendingKey;
                pendingKey = null;
                switch (el.Name)
                {
                    case "integer":
                        // PlayerPrefs ints are 32-bit; a wider integer was not
                        // written through the PlayerPrefs API.
                        if (int.TryParse(el.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                            result.Entries.Add(PrefEntry.OfInt(key, i));
                        else
                            result.Unsupported.Add(key);
                        break;
                    case "real":
                        if (TryParseReal(el.InnerText.Trim(), out var d))
                            result.Entries.Add(PrefEntry.OfFloat(key, (float)d));
                        else
                            result.Unsupported.Add(key);
                        break;
                    case "string":
                        result.Entries.Add(PrefEntry.OfString(key, el.InnerText));
                        break;
                    default:
                        // <data>, <true/>, <false/>, <date>, <dict>, <array>: not a
                        // PlayerPrefs type — reported, never restored.
                        result.Unsupported.Add(key);
                        break;
                }
            }
            return result;
        }

        private static bool TryParseReal(string s, out double d)
        {
            switch (s.ToLowerInvariant())
            {
                case "nan": d = double.NaN; return true;
                case "inf": case "+inf": case "infinity": d = double.PositiveInfinity; return true;
                case "-inf": case "-infinity": d = double.NegativeInfinity; return true;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        // ------------------------------------------------------------------
        // Linux: ~/.config/unity3d/<company>/<product>/prefs (XML)
        // ------------------------------------------------------------------

        private static PrefsReadResult ReadLinuxFile(string path)
        {
            if (!File.Exists(path)) return new PrefsReadResult();
            return ParseLinuxPrefsXml(File.ReadAllText(path, Encoding.UTF8));
        }

        internal static PrefsReadResult ParseLinuxPrefsXml(string xml)
        {
            var result = new PrefsReadResult();
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml ?? ""), SafeXml()))
                doc.Load(reader);
            var root = doc.DocumentElement;
            if (root == null)
            {
                result.Error = "Prefs file has no root element.";
                return result;
            }
            foreach (XmlNode node in root.ChildNodes)
            {
                if (!(node is XmlElement el) || el.Name != "pref") continue;
                var key = el.GetAttribute("name");
                var type = el.GetAttribute("type");
                var text = el.InnerText ?? "";
                switch (type)
                {
                    case "int":
                        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                            result.Entries.Add(PrefEntry.OfInt(key, i));
                        else result.Unsupported.Add(key);
                        break;
                    case "float":
                        if (TryParseReal(text.Trim(), out var d)) result.Entries.Add(PrefEntry.OfFloat(key, (float)d));
                        else result.Unsupported.Add(key);
                        break;
                    case "string":
                        result.Entries.Add(PrefEntry.OfString(key, DecodeLinuxString(text)));
                        break;
                    default:
                        result.Unsupported.Add(key);
                        break;
                }
            }
            return result;
        }

        // Strings are stored base64-encoded (UTF-8). A value that is not valid
        // base64 is kept verbatim rather than guessed at.
        internal static string DecodeLinuxString(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return "";
            try
            {
                var bytes = Convert.FromBase64String(t);
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (FormatException) { return text; }
            catch (ArgumentException) { return text; }
        }

        // ------------------------------------------------------------------
        // Windows: registry value names `<key>_h<hash>` + typed raw data
        // ------------------------------------------------------------------

        internal const uint RegSz = 1, RegBinary = 3, RegDword = 4, RegQword = 11;

        // djb2 with XOR over the key's UTF-8 bytes — the hash Unity appends to
        // every registry value name (`<key>_h<hash>`).
        internal static uint UnityKeyHash(string key)
        {
            uint h = 5381;
            foreach (var b in Encoding.UTF8.GetBytes(key ?? ""))
                h = unchecked(h * 33u) ^ b;
            return h;
        }

        // Split `<key>_h<hash>` and check the hash; a name without a matching
        // hash suffix is not a Unity pref and is skipped.
        internal static bool TrySplitValueName(string valueName, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(valueName)) return false;
            int at = valueName.LastIndexOf("_h", StringComparison.Ordinal);
            if (at < 0) return false;
            var digits = valueName.Substring(at + 2);
            if (digits.Length == 0 || !uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var hash))
                return false;
            var candidate = valueName.Substring(0, at);
            if (UnityKeyHash(candidate) != hash) return false;
            key = candidate;
            return true;
        }

        // Decode one raw registry value into a typed pref. Returns false for a
        // value name that is not a Unity pref (no hash suffix); `unsupported` is
        // set for a Unity pref whose data cannot be represented.
        internal static bool TryDecodeRegistryValue(string valueName, uint regType, byte[] data, int length,
            out PrefEntry entry, out bool unsupported)
        {
            entry = null;
            unsupported = false;
            if (!TrySplitValueName(valueName, out var key)) return false;
            if (data == null) { data = Array.Empty<byte>(); length = 0; }
            length = Math.Min(Math.Max(length, 0), data.Length);
            switch (regType)
            {
                case RegDword when length == 4:
                    entry = PrefEntry.OfInt(key, BitConverter.ToInt32(data, 0));
                    return true;
                case RegDword when length == 8:
                case RegQword when length == 8:
                    entry = PrefEntry.OfFloat(key, (float)BitConverter.ToDouble(data, 0));
                    return true;
                case RegBinary:
                {
                    int n = length;
                    while (n > 0 && data[n - 1] == 0) n--;
                    entry = PrefEntry.OfString(key, Encoding.UTF8.GetString(data, 0, n));
                    return true;
                }
                case RegSz:
                {
                    var s = Encoding.Unicode.GetString(data, 0, length);
                    entry = PrefEntry.OfString(key, s.TrimEnd('\0'));
                    return true;
                }
                default:
                    unsupported = true;
                    entry = new PrefEntry { Key = key };
                    return true;
            }
        }

        private static readonly UIntPtr HkeyCurrentUser = new UIntPtr(0x80000001u);
        private const int KeyRead = 0x20019;
        private const int ErrorSuccess = 0, ErrorFileNotFound = 2, ErrorMoreData = 234, ErrorNoMoreItems = 259;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
        private static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, uint options, int samDesired, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumValueW")]
        private static extern int RegEnumValue(IntPtr hKey, uint index, StringBuilder valueName, ref uint valueNameChars,
            IntPtr reserved, out uint type, byte[] data, ref uint dataBytes);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        private static PrefsReadResult ReadRegistry(string subKey)
        {
            var result = new PrefsReadResult();
            int open = RegOpenKeyEx(HkeyCurrentUser, subKey, 0, KeyRead, out var hKey);
            if (open == ErrorFileNotFound) return result;
            if (open != ErrorSuccess) throw new IOException("RegOpenKeyEx failed (" + open + ") for HKCU\\" + subKey + ".");
            try
            {
                // 16383 characters is the registry's value-name limit.
                var name = new StringBuilder(16384);
                var data = new byte[4096];
                uint index = 0;
                while (true)
                {
                    uint nameChars = (uint)name.Capacity;
                    uint dataBytes = (uint)data.Length;
                    name.Length = 0;
                    int rc = RegEnumValue(hKey, index, name, ref nameChars, IntPtr.Zero, out var type, data, ref dataBytes);
                    if (rc == ErrorMoreData)
                    {
                        // Same index again with a buffer of the size it asked for.
                        data = new byte[Math.Max(dataBytes, (uint)data.Length * 2)];
                        continue;
                    }
                    if (rc == ErrorNoMoreItems) break;
                    if (rc != ErrorSuccess) throw new IOException("RegEnumValue failed (" + rc + ") for HKCU\\" + subKey + ".");
                    index++;
                    var valueName = name.ToString(0, (int)Math.Min(nameChars, (uint)name.Length));
                    // A value name without PlayerPrefs' `_h<hash>` suffix is still
                    // removed by DeleteAll: report it so restore lists it.
                    if (!TryDecodeRegistryValue(valueName, type, data, (int)dataBytes, out var entry, out var unsupported))
                    {
                        result.Unsupported.Add(valueName);
                        continue;
                    }
                    if (unsupported) result.Unsupported.Add(entry.Key);
                    else result.Entries.Add(entry);
                }
            }
            finally
            {
                RegCloseKey(hKey);
            }
            return result;
        }
    }
}
