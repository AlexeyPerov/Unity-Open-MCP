using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UnityOpenMcpBridge
{
    // Built by the existing registry scan, then published as an immutable snapshot.
    // Catalog entries remain separate from direct tool dispatch.
    internal static class ProjectCommandCatalog
    {
        internal sealed class Entry
        {
            internal ProjectCommandAttribute Attribute;
            internal MethodInfo Method;
            internal string Schema;
            internal string SchemaVersion;
            internal string Code;
            internal string Message;
            internal string Id => Attribute.Name;
            internal string Source => Method.DeclaringType.FullName + "." + Method + " (" + Method.DeclaringType.Assembly.GetName().Name + ")";
        }
        private static volatile Entry[] snapshot = Array.Empty<Entry>();
        internal static void Publish(IEnumerable<Entry> candidates)
        {
            var entries = candidates.OrderBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Source, StringComparer.Ordinal).ToArray();
            var claims = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
            foreach (var entry in entries)
                foreach (var id in new[] { entry.Id }.Concat(entry.Attribute.DeprecatedAliases ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
                {
                    if (!claims.TryGetValue(id, out var owners)) claims[id] = owners = new List<Entry>();
                    owners.Add(entry);
                }
            foreach (var claim in claims.Where(c => c.Value.Count > 1))
                foreach (var entry in claim.Value)
                {
                    entry.Code = "duplicate_command_id";
                    entry.Message = "Conflicting id/alias '" + claim.Key + "': " + string.Join(", ", claim.Value.Select(e => e.Source));
                }
            snapshot = entries;
            foreach (var entry in entries.Where(e => e.Code != null))
                UnityEngine.Debug.LogWarning("[ProjectCommandCatalog] " + entry.Source + ": " + entry.Code + ": " + entry.Message);
        }
        internal static Entry Create(MethodInfo method, ProjectCommandAttribute attr)
        {
            var entry = new Entry { Method = method, Attribute = attr };
            try
            {
                foreach (var id in new[] { attr.Name }.Concat(attr.DeprecatedAliases ?? Array.Empty<string>()))
                    if (!Regex.IsMatch(id ?? "", @"^project\.[a-z0-9][a-z0-9_-]*\.[a-z0-9][a-z0-9_.-]*$"))
                        throw new ArgumentException("IDs and aliases must use project.<owner>.<command>.");
                if (string.IsNullOrWhiteSpace(attr.Title) || string.IsNullOrWhiteSpace(attr.Description) || string.IsNullOrWhiteSpace(attr.Package))
                    throw new ArgumentException("Title, Description and Package are required.");
                if (attr.Tags == null || attr.DeprecatedAliases == null || attr.Tags.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException("Tags and DeprecatedAliases must be arrays with non-empty values.");
                if (attr.DeprecatedAliases.Distinct(StringComparer.Ordinal).Count() != attr.DeprecatedAliases.Length || attr.DeprecatedAliases.Contains(attr.Name))
                    throw new ArgumentException("Aliases must be distinct from each other and the command id.");
                if (!Enum.IsDefined(typeof(GateMode), attr.Gate) || !Enum.IsDefined(typeof(LifecyclePolicy), attr.Lifecycle))
                    throw new ArgumentException("Unknown gate or lifecycle declaration.");
                if (attr.Cancellable && !attr.Async) throw new ArgumentException("Cancellable requires Async.");
                if (attr.IsMutating && attr.ReadOnlyHint) throw new ArgumentException("Mutating commands cannot declare ReadOnlyHint.");
                if (attr.PathsHint == null || attr.PathsHint.Any(p => !ProjectCommandInvocation.ValidScope(p)))
                    throw new ArgumentException("PathsHint must contain project-relative Assets/ or Packages/ paths without traversal.");
                if (attr.IsMutating && attr.Lifecycle == LifecyclePolicy.None)
                    throw new ArgumentException("Mutating commands must declare a lifecycle policy.");
                if (!attr.IsMutating && attr.Lifecycle != LifecyclePolicy.None)
                    throw new ArgumentException("Read-only commands must declare Lifecycle.None.");
                entry.Schema = ProjectCommandSchema.Build(method);
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    entry.SchemaVersion = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                        entry.Schema + "|" + method.Module.ModuleVersionId + "|" + entry.Source + "|" + attr.IsMutating + "|" + attr.Lifecycle + "|" + attr.Gate + "|" + attr.Async + "|" + attr.Cancellable + "|" + string.Join(";", attr.PathsHint)))).Replace("-", "").ToLowerInvariant();
                if (!attr.Enabled) { entry.Code = "command_disabled"; entry.Message = "Command is disabled by its declaration."; }
            }
            catch (Exception ex) { entry.Code = "invalid_command_declaration"; entry.Message = ex.Message; }
            return entry;
        }
        // Exact identity seam for the invocation/job pipeline. Unavailable entries never resolve.
        internal static bool TryGet(string id, out Entry entry)
        {
            entry = snapshot.FirstOrDefault(e => e.Id == id && e.Code == null);
            return entry != null;
        }
        internal static string Query(string action, string id = null, string query = null, string[] tags = null,
            string group = null, string package = null, int offset = 0, int limit = 20)
        {
            var current = snapshot;
            if (action == "describe")
            {
                var matches = current.Where(e => e.Id == id).ToArray();
                if (matches.Length == 0) return Error("command_not_found", "Unknown exact command id: " + id);
                if (matches.Length > 1) return Error("duplicate_command_id", string.Join("; ", matches.Select(e => e.Source)));
                return "{\"catalogVersion\":1,\"command\":" + Json(matches[0], true) + "}";
            }
            if (action != "list" || offset < 0 || limit < 1 || limit > 100)
                return Error("invalid_arguments", "Use list or describe; offset >= 0 and limit between 1 and 100.");
            var filtered = current.Where(e => (string.IsNullOrEmpty(query) || (e.Id + " " + e.Attribute.Title + " " + e.Attribute.Description).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                && (group == null || e.Attribute.Group == group) && (package == null || e.Attribute.Package == package)
                && (tags == null || tags.All(t => (e.Attribute.Tags ?? Array.Empty<string>()).Contains(t)))).ToArray();
            var page = filtered.Skip(offset).Take(limit).Select(e => Json(e, false));
            return "{\"catalogVersion\":1,\"total\":" + filtered.Length + ",\"offset\":" + offset + ",\"limit\":" + limit
                + ",\"nextOffset\":" + (offset < filtered.Length - limit ? (offset + limit).ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")
                + ",\"commands\":[" + string.Join(",", page) + "]}";
        }
        internal static string Error(string code, string message) => "{\"catalogVersion\":1,\"error\":{\"code\":" + BridgeJson.EscapeString(code) + ",\"message\":" + BridgeJson.EscapeString(message) + "}}";
        private static string Json(Entry e, bool full)
        {
            var a = e.Attribute;
            var s = "{\"id\":" + BridgeJson.EscapeString(e.Id) + ",\"title\":" + BridgeJson.EscapeString(a.Title)
                + ",\"description\":" + BridgeJson.EscapeString(a.Description) + ",\"group\":" + BridgeJson.EscapeString(a.Group)
                + ",\"package\":" + BridgeJson.EscapeString(a.Package) + ",\"tags\":" + ProjectCommandSchema.Strings((a.Tags ?? Array.Empty<string>()).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                + ",\"available\":" + (e.Code == null ? "true" : "false");
            if (e.Code != null) s += ",\"diagnostic\":{\"code\":" + BridgeJson.EscapeString(e.Code) + ",\"message\":" + BridgeJson.EscapeString(e.Source + ": " + e.Message) + "}";
            if (full) s += ",\"inputSchema\":" + (e.Schema ?? "null") + ",\"deprecatedAliases\":" + ProjectCommandSchema.Strings((a.DeprecatedAliases ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal))
                + ",\"isMutating\":" + Bool(a.IsMutating) + ",\"readOnlyHint\":" + Bool(a.ReadOnlyHint) + ",\"idempotentHint\":" + Bool(a.IdempotentHint)
                + ",\"destructiveHint\":" + Bool(a.DestructiveHint) + ",\"gate\":" + BridgeJson.EscapeString(a.Gate.ToString().ToLowerInvariant())
                + ",\"lifecycle\":" + BridgeJson.EscapeString(a.Lifecycle.ToWireString())
                + ",\"schemaVersion\":" + BridgeJson.EscapeString(e.SchemaVersion) + ",\"declaringAssembly\":" + BridgeJson.EscapeString(e.Method.DeclaringType.Assembly.GetName().Name)
                + ",\"declaringType\":" + BridgeJson.EscapeString(e.Method.DeclaringType.FullName) + ",\"pathsHint\":" + ProjectCommandSchema.Strings(a.PathsHint ?? Array.Empty<string>())
                + ",\"async\":" + Bool(a.Async) + ",\"cancellable\":" + Bool(a.Cancellable) + ",\"invocationSupported\":true";
            return s + "}";
        }
        private static string Bool(bool value) => value ? "true" : "false";
    }
}
