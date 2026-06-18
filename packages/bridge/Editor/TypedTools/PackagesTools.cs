using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
// NOTE: do NOT add `using UnityEditor;` here. In Unity 6 the root
// UnityEditor namespace exposes a `PackageInfo` type that makes the
// PackageManager.PackageInfo references below ambiguous (CS0104). This file
// uses only UnityEditor.PackageManager (+ .Requests) types plus
// UnityEngine.Application.dataPath, all of which resolve unambiguously
// without the root UnityEditor import. Mirrors UCP PackagesController's
// import set.

namespace UnityOpenMcpBridge.TypedTools
{
    // M16 Plan 4 — typed Unity Package Manager (UPM) tools. Covers list /
    // search / add / remove / get_info / get_dependencies / check.
    //
    // Mutating members (add / remove) write Packages/manifest.json (and may
    // rewrite Packages/packages-lock.json), so paths_hint MUST be scoped to
    // "Packages/manifest.json" (the lock file is touched implicitly — see the
    // tool descriptions). Read-only members (list / search / get_info /
    // get_dependencies / check) are gate-free.
    //
    // UPM requests (Client.List / Client.Add / Client.Search) are async — the
    // Request<T>.IsCompleted flag flips on a package-manager worker thread.
    // The dispatcher runs each tool body synchronously on the main thread, so
    // we poll IsCompleted with a short sleep up to a deadline. This mirrors
    // UCP's WaitForRequest pattern; PackageInfo can be read from any thread
    // once IsCompleted is true.
    //
    // These tools are NOT registry-discovered: they are wired into
    // BridgeHttpServer.DispatchTool alongside the other M16 typed tools so
    // their snake_case schemas parse the same way.
    public static class PackagesTools
    {
        // UPM operations can take a while (registry round-trip, domain
        // reload on add/remove). Generous cap; the dispatcher still enforces
        // the request-level timeout_ms.
        const int RequestTimeoutMs = 90_000;
        const int PollIntervalMs = 25;

        // The canonical manifest path. Relative to the project root (the
        // folder containing Assets/). Used by get_dependencies and check so
        // they can answer without spinning up a UPM request.
        static string ManifestPath => Path.Combine(
            Directory.GetParent(UnityEngine.Application.dataPath).FullName,
            "Packages", "manifest.json");

        // --------------------------- reads --------------------------------

        // List installed UPM packages. Gate-free. Token-bounded by max_results.
        // Folds UMCP package-list + UCP packages/list. Supports offline mode
        // (cached resolution only) and an indirect-dependency toggle.
        public static ToolDispatchResult List(string body)
        {
            bool offline = JsonBody.GetBool(body, "offline", true);
            bool includeIndirect = JsonBody.GetBool(body, "include_indirect", false);
            int maxResults = JsonBody.GetInt(body, "max_results", 200);
            if (maxResults < 1) maxResults = 1;

            var sourceFilter = ParseSourceFilter(JsonBody.GetString(body, "source"));
            var nameFilter = JsonBody.GetString(body, "name_filter");
            // direct_dependencies_only mirrors UCP includeIndirect=false and
            // UMCP directDependenciesOnly — only entries from manifest.json.
            bool directOnly = JsonBody.GetBool(body, "direct_dependencies_only", false);

            ListRequest request;
            try
            {
                // List(offline, includeIndirect) — when includeIndirect is
                // false, only direct manifest dependencies are returned.
                request = Client.List(offline, includeIndirect);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("list_request_failed", e.Message);
            }

            if (!WaitForRequest(request))
                return Timeout("list");

            if (request.Status == StatusCode.Failure)
                return Fail("list_failed",
                    request.Error?.message ?? "Package Manager list request failed.");

            var direct = directOnly ? ReadManifestDependencies() : null;

            var packages = new List<PackageInfo>();
            foreach (var pkg in request.Result)
            {
                if (sourceFilter.HasValue && pkg.source != sourceFilter.Value) continue;
                if (!MatchName(pkg, nameFilter)) continue;
                if (directOnly && direct != null && !direct.ContainsKey(pkg.name)) continue;
                packages.Add(pkg);
            }

            // Sort: exact-name → exact-displayName → name-substring →
            // displayName-substring → description-substring, then by name.
            if (!string.IsNullOrEmpty(nameFilter))
            {
                packages.Sort((a, b) =>
                {
                    int pa = MatchPriority(a, nameFilter);
                    int pb = MatchPriority(b, nameFilter);
                    if (pa != pb) return pa.CompareTo(pb);
                    return string.CompareOrdinal(a.name, b.name);
                });
            }
            else
            {
                packages.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            }

            int total = packages.Count;
            if (packages.Count > maxResults) packages.RemoveRange(maxResults, packages.Count - maxResults);

            var sb = new StringBuilder(256 + packages.Count * 96);
            sb.Append("{\"status\":\"ok\",\"offline\":").Append(offline ? "true" : "false");
            sb.Append(",\"includeIndirect\":").Append(includeIndirect ? "true" : "false");
            sb.Append(",\"count\":").Append(packages.Count);
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"truncated\":").Append(total - packages.Count);
            sb.Append(",\"packages\":[");
            for (int i = 0; i < packages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(SerializePackage(packages[i], direct));
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Search UPM registry + cached packages. Gate-free. Token-bounded by
        // max_results. Folds UMCP package-search + UCP packages/search.
        public static ToolDispatchResult Search(string body)
        {
            var query = JsonBody.GetString(body, "query");
            if (string.IsNullOrWhiteSpace(query))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'query' is required and must be a non-empty string.");

            bool offline = JsonBody.GetBool(body, "offline", true);
            int maxResults = JsonBody.GetInt(body, "max_results", 20);
            if (maxResults < 1) maxResults = 1;

            // Resolve installed packages first so each search result can
            // report installed:true + installedVersion.
            Dictionary<string, PackageInfo> installed = null;
            try
            {
                var listReq = Client.List(true);
                if (WaitForRequest(listReq) && listReq.Status == StatusCode.Success)
                {
                    installed = new Dictionary<string, PackageInfo>();
                    foreach (var p in listReq.Result) installed[p.name] = p;
                }
            }
            catch
            {
                // Non-fatal — installed-status fields simply come back null.
            }

            var results = new List<PackageInfo>();
            var seen = new HashSet<string>();

            // Live exact-match pass (online only). Cached substring pass
            // fills the remainder in both modes.
            if (!offline)
            {
                SearchRequest onlineReq;
                try { onlineReq = Client.Search(query, false); }
                catch (System.Exception e)
                {
                    return Fail("search_request_failed", e.Message);
                }
                if (WaitForRequest(onlineReq) && onlineReq.Status == StatusCode.Success)
                {
                    foreach (var pkg in onlineReq.Result)
                    {
                        if (seen.Add(pkg.name) && MatchPriority(pkg, query) > 0)
                            results.Add(pkg);
                    }
                }
            }

            if (results.Count < maxResults)
            {
                // Client.SearchAll(bool offline) returns a SearchRequest in
                // modern Unity (the legacy SearchAllRequest type was removed).
                // Same Request<T> shape, so WaitForRequest works unchanged.
                SearchRequest cacheReq;
                try { cacheReq = Client.SearchAll(true); }
                catch (System.Exception e)
                {
                    return Fail("search_request_failed", e.Message);
                }
                if (WaitForRequest(cacheReq) && cacheReq.Status == StatusCode.Success)
                {
                    foreach (var pkg in cacheReq.Result)
                    {
                        if (results.Count >= maxResults) break;
                        if (seen.Add(pkg.name) && MatchPriority(pkg, query) > 0)
                            results.Add(pkg);
                    }
                }
            }

            // Sort by match priority (best first), then by name.
            results.Sort((a, b) =>
            {
                int pa = MatchPriority(a, query);
                int pb = MatchPriority(b, query);
                if (pa != pb) return pb.CompareTo(pa);
                return string.CompareOrdinal(a.name, b.name);
            });

            int total = results.Count;
            if (results.Count > maxResults) results.RemoveRange(maxResults, results.Count - maxResults);

            var sb = new StringBuilder(128 + results.Count * 128);
            sb.Append("{\"status\":\"ok\",\"query\":\"").Append(Esc(query));
            sb.Append("\",\"offline\":").Append(offline ? "true" : "false");
            sb.Append(",\"count\":").Append(results.Count);
            sb.Append(",\"total\":").Append(total);
            sb.Append(",\"truncated\":").Append(total - results.Count);
            sb.Append(",\"results\":[");
            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var pkg = results[i];
                // Split the TryGetValue from the null check so the compiler's
                // definite-assignment analysis can see installedPkg is always
                // assigned before use (CS0165 with the combined && expression).
                PackageInfo installedPkg = null;
                bool isInstalled = installed != null && installed.TryGetValue(pkg.name, out installedPkg);
                sb.Append("{\"name\":\"").Append(Esc(pkg.name));
                sb.Append("\",\"displayName\":\"").Append(Esc(pkg.displayName ?? pkg.name));
                sb.Append("\",\"version\":\"").Append(Esc(pkg.version));
                sb.Append("\",\"description\":\"").Append(Esc(Truncate(pkg.description, 200)));
                sb.Append("\",\"source\":\"").Append(Esc(pkg.source.ToString()));
                sb.Append("\",\"installed\":").Append(isInstalled ? "true" : "false");
                if (isInstalled)
                {
                    sb.Append(",\"installedVersion\":\"").Append(Esc(installedPkg.version)).Append("\"");
                }
                var versions = pkg.versions?.compatible;
                sb.Append(",\"availableVersions\":[");
                if (versions != null)
                {
                    int n = 0;
                    foreach (var v in versions)
                    {
                        if (n >= 5) break;
                        if (n > 0) sb.Append(',');
                        sb.Append('"').Append(Esc(v)).Append('"');
                        n++;
                    }
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Inspect one package by name (installed or registry). Gate-free.
        // Folds UCP packages/info.
        public static ToolDispatchResult GetInfo(string body)
        {
            var name = JsonBody.GetString(body, "name");
            if (string.IsNullOrWhiteSpace(name))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'name' is required and must be a non-empty string (package id or displayName).");

            bool offline = JsonBody.GetBool(body, "offline", true);

            // Installed-package path: Client.List then match by name/id.
            ListRequest listReq;
            try { listReq = Client.List(offline, true); }
            catch (System.Exception e)
            {
                return Fail("list_request_failed", e.Message);
            }
            if (!WaitForRequest(listReq)) return Timeout("get_info");
            if (listReq.Status == StatusCode.Success)
            {
                foreach (var pkg in listReq.Result)
                {
                    if (MatchesPackage(pkg, name))
                    {
                        var sb = new StringBuilder(256);
                        sb.Append("{\"status\":\"ok\",\"installed\":true,");
                        sb.Append("\"package\":").Append(SerializePackage(pkg, ReadManifestDependencies()));
                        sb.Append('}');
                        return ToolDispatchResult.Ok(sb.ToString());
                    }
                }
            }

            // Not installed — fall back to a registry search (online only).
            if (offline)
                return ToolDispatchResult.Fail("package_not_found",
                    $"Package '{name}' is not installed. Pass offline:false to look it up in the registry.");

            SearchRequest searchReq;
            try { searchReq = Client.Search(name, false); }
            catch (System.Exception e)
            {
                return Fail("search_request_failed", e.Message);
            }
            if (!WaitForRequest(searchReq)) return Timeout("get_info");
            if (searchReq.Status == StatusCode.Success)
            {
                foreach (var pkg in searchReq.Result)
                {
                    if (MatchesPackage(pkg, name))
                    {
                        var sb = new StringBuilder(256);
                        sb.Append("{\"status\":\"ok\",\"installed\":false,");
                        sb.Append("\"package\":").Append(SerializePackage(pkg, null));
                        sb.Append('}');
                        return ToolDispatchResult.Ok(sb.ToString());
                    }
                }
            }

            return ToolDispatchResult.Fail("package_not_found",
                $"Package '{name}' not found in installed packages or registry.");
        }

        // Read manifest.json dependency list. Gate-free. Folds UCP
        // packages/dependencies — no UPM request, just reads the file.
        public static ToolDispatchResult GetDependencies(string body)
        {
            string manifestPath;
            string manifestJson;
            try
            {
                manifestPath = ManifestPath;
                if (!File.Exists(manifestPath))
                    return ToolDispatchResult.Fail("manifest_not_found",
                        $"Packages/manifest.json not found at '{manifestPath}'.");
                manifestJson = File.ReadAllText(manifestPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("manifest_read_failed", e.Message);
            }

            var deps = ExtractDependencies(manifestJson);

            var sb = new StringBuilder(64 + deps.Count * 48);
            sb.Append("{\"status\":\"ok\",\"manifestPath\":\"Packages/manifest.json\",");
            sb.Append("\"count\":").Append(deps.Count);
            sb.Append(",\"dependencies\":[");
            bool first = true;
            foreach (var dep in deps)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":\"").Append(Esc(dep.Key));
                sb.Append("\",\"reference\":\"").Append(Esc(dep.Value)).Append("\"}");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Presence + version check for a packageId against manifest.json.
        // Gate-free. Folds UUMCP package_check.
        public static ToolDispatchResult Check(string body)
        {
            var packageId = JsonBody.GetString(body, "package_id");
            if (string.IsNullOrWhiteSpace(packageId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'package_id' is required and must be a non-empty string.");

            // Accept "name@version" input — check on the name half only.
            var name = packageId.Contains("@")
                ? packageId.Substring(0, packageId.IndexOf('@'))
                : packageId;

            string manifestPath;
            string manifestJson;
            try
            {
                manifestPath = ManifestPath;
                if (!File.Exists(manifestPath))
                    return ToolDispatchResult.Fail("manifest_not_found",
                        $"Packages/manifest.json not found at '{manifestPath}'.");
                manifestJson = File.ReadAllText(manifestPath);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("manifest_read_failed", e.Message);
            }

            var deps = ExtractDependencies(manifestJson);
            bool installed = deps.TryGetValue(name, out var reference);

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"packageId\":\"").Append(Esc(packageId));
            sb.Append("\",\"name\":\"").Append(Esc(name));
            sb.Append("\",\"installed\":").Append(installed ? "true" : "false");
            sb.Append(",\"reference\":").Append(installed ? "\"" + Esc(reference) + "\"" : "null");
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // --------------------------- mutators -----------------------------

        // Install a UPM package. Mutating: writes Packages/manifest.json and
        // triggers package resolution (may domain-reload). Folds UMCP
        // package-add + UCP packages/add.
        public static ToolDispatchResult Add(string body)
        {
            var packageId = JsonBody.GetString(body, "package_id");
            if (string.IsNullOrWhiteSpace(packageId))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'package_id' is required. Formats: 'com.unity.textmeshpro', " +
                    "'com.unity.textmeshpro@3.0.6', 'https://github.com/user/repo.git', " +
                    "'https://github.com/user/repo.git#v1.0.0', 'file:../MyPackage'.");

            AddRequest request;
            try
            {
                request = Client.Add(packageId);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("add_request_failed", e.Message);
            }

            if (!WaitForRequest(request))
                return Timeout("add");

            if (request.Status == StatusCode.Failure)
                return Fail("add_failed",
                    request.Error?.message ?? $"Client.Add failed for '{packageId}'.");

            var pkg = request.Result;
            var sb = new StringBuilder(128);
            sb.Append("{\"status\":\"ok\",\"action\":\"added\",");
            sb.Append("\"packageId\":\"").Append(Esc(packageId)).Append("\",");
            sb.Append("\"package\":").Append(SerializePackage(pkg, ReadManifestDependencies()));
            sb.Append('}');
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // Remove a UPM package. Mutating: writes Packages/manifest.json and
        // triggers resolution. Refuses packages that are not installed.
        // Folds UMCP package-remove + UCP packages/remove.
        public static ToolDispatchResult Remove(string body)
        {
            var packageIdRaw = JsonBody.GetString(body, "package_id");
            if (string.IsNullOrWhiteSpace(packageIdRaw))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'package_id' is required and must be the package name " +
                    "(e.g. 'com.unity.textmeshpro'). A trailing '@version' is stripped.");

            // Strip accidental @version suffix — Client.Remove wants the bare name.
            var packageId = packageIdRaw.Contains("@")
                ? packageIdRaw.Substring(0, packageIdRaw.IndexOf('@'))
                : packageIdRaw;

            // Verify the package is installed before attempting removal so
            // the error message is actionable.
            var installed = CollectInstalled();
            if (installed != null && !installed.ContainsKey(packageId))
                return ToolDispatchResult.Fail("package_not_found",
                    $"Package '{packageId}' is not installed. " +
                    "Use unity_open_mcp_package_list to enumerate installed packages.");

            RemoveRequest request;
            try
            {
                request = Client.Remove(packageId);
            }
            catch (System.Exception e)
            {
                return ToolDispatchResult.Fail("remove_request_failed", e.Message);
            }

            if (!WaitForRequest(request))
                return Timeout("remove");

            if (request.Status == StatusCode.Failure)
                return Fail("remove_failed",
                    request.Error?.message ?? $"Client.Remove failed for '{packageId}'.");

            var sb = new StringBuilder(96);
            sb.Append("{\"status\":\"ok\",\"action\":\"removed\",");
            sb.Append("\"packageId\":\"").Append(Esc(packageId)).Append("\"}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ----------------------------- helpers ----------------------------

        static PackageSource? ParseSourceFilter(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return raw.ToLowerInvariant() switch
            {
                "registry" => PackageSource.Registry,
                "embedded" => PackageSource.Embedded,
                "local" => PackageSource.Local,
                "git" => PackageSource.Git,
                "builtin" => PackageSource.BuiltIn,
                "localtarball" => PackageSource.LocalTarball,
                _ => null,
            };
        }

        // Polling wait for a UPM request. Returns true when the request
        // completed within the timeout, false on timeout. Package-manager
        // worker progress advances independently of the polling thread.
        static bool WaitForRequest<T>(T request) where T : Request
        {
            var deadline = System.DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
            while (!request.IsCompleted)
            {
                if (System.DateTime.UtcNow > deadline) return false;
                System.Threading.Thread.Sleep(PollIntervalMs);
            }
            return true;
        }

        static ToolDispatchResult Timeout(string op) =>
            ToolDispatchResult.Fail("timeout",
                $"Package Manager {op} request timed out after {RequestTimeoutMs} ms.");

        static ToolDispatchResult Fail(string code, string message) =>
            ToolDispatchResult.Fail(code, message);

        // Name filter: case-insensitive substring over name/displayName/
        // description. Empty filter accepts everything.
        static bool MatchName(PackageInfo pkg, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return MatchPriority(pkg, filter) > 0;
        }

        // Match priority: 5 exact name, 4 exact displayName, 3 name substring,
        // 2 displayName substring, 1 description substring, 0 no match.
        // Higher = better. Mirrors UMCP GetSearchPriority.
        static int MatchPriority(PackageInfo pkg, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return 0;
            var name = pkg.name ?? "";
            var display = pkg.displayName ?? "";
            var desc = pkg.description ?? "";
            if (string.Equals(name, filter, System.StringComparison.OrdinalIgnoreCase)) return 5;
            if (string.Equals(display, filter, System.StringComparison.OrdinalIgnoreCase)) return 4;
            if (name.ToLowerInvariant().Contains(filter.ToLowerInvariant())) return 3;
            if (display.ToLowerInvariant().Contains(filter.ToLowerInvariant())) return 2;
            if (desc.ToLowerInvariant().Contains(filter.ToLowerInvariant())) return 1;
            return 0;
        }

        // A package "matches" a query if the query equals or is a substring
        // of the package's name, packageId, or displayName.
        static bool MatchesPackage(PackageInfo pkg, string query)
        {
            if (string.Equals(pkg.name, query, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pkg.packageId)
                && string.Equals(pkg.packageId, query, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pkg.displayName)
                && string.Equals(pkg.displayName, query, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static Dictionary<string, PackageInfo> CollectInstalled()
        {
            var req = Client.List(true);
            if (!WaitForRequest(req) || req.Status != StatusCode.Success) return null;
            var dict = new Dictionary<string, PackageInfo>();
            foreach (var p in req.Result) dict[p.name] = p;
            return dict;
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 3) + "...";
        }

        // ------------------------- JSON building --------------------------

        public static string SerializePackage(PackageInfo pkg, Dictionary<string, string> directDependencies)
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"name\":\"").Append(Esc(pkg.name));
            sb.Append("\",\"displayName\":\"").Append(Esc(pkg.displayName ?? pkg.name));
            sb.Append("\",\"version\":\"").Append(Esc(pkg.version));
            sb.Append("\",\"packageId\":\"").Append(Esc(pkg.packageId ?? ""));
            sb.Append("\",\"source\":\"").Append(Esc(pkg.source.ToString()));
            sb.Append("\",\"resolvedPath\":\"").Append(Esc(pkg.resolvedPath ?? ""));
            sb.Append("\",\"description\":\"").Append(Esc(Truncate(pkg.description, 300)));
            if (!string.IsNullOrEmpty(pkg.category))
                sb.Append("\",\"category\":\"").Append(Esc(pkg.category));
            if (directDependencies != null)
            {
                sb.Append("\",\"directDependency\":")
                  .Append(directDependencies.ContainsKey(pkg.name) ? "true" : "false");
            }
            // versions block (recommended + latestCompatible) when available.
            if (pkg.versions != null)
            {
                sb.Append(",\"versions\":{\"recommended\":\"")
                  .Append(Esc(pkg.versions.recommended ?? ""));
                sb.Append("\",\"latestCompatible\":\"")
                  .Append(Esc(pkg.versions.latestCompatible ?? ""));
                sb.Append("\"}");
            }
            // registry block when available.
            if (pkg.registry != null)
            {
                sb.Append(",\"registry\":{\"name\":\"")
                  .Append(Esc(pkg.registry.name ?? ""));
                sb.Append("\",\"url\":\"").Append(Esc(pkg.registry.url ?? ""));
                sb.Append("\",\"isDefault\":").Append(pkg.registry.isDefault ? "true" : "false");
                sb.Append("}");
            }
            // dependencies block.
            sb.Append(",\"dependencies\":[");
            if (pkg.dependencies != null)
            {
                int i = 0;
                foreach (var dep in pkg.dependencies)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(Esc(dep.name));
                    sb.Append("\",\"version\":\"").Append(Esc(dep.version)).Append("\"}");
                    i++;
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ------------------------- manifest parsing -----------------------
        //
        // We avoid a JSON serializer dependency in the bridge (per
        // packages/bridge/AGENTS.md §Transport). The manifest dependencies
        // block is a flat "dependencies": { "name": "ref", ... } object —
        // parse it directly by scanning for the brace-delimited block.

        static Dictionary<string, string> ReadManifestDependencies()
        {
            try
            {
                var path = ManifestPath;
                if (!File.Exists(path)) return new Dictionary<string, string>();
                return ExtractDependencies(File.ReadAllText(path));
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        // Extract the "dependencies" object from manifest.json as a name→ref
        // dictionary. Case-insensitive on names so manifest reads match UPM
        // package-name comparison. Returns an empty dict when the block is
        // missing or unparseable.
        public static Dictionary<string, string> ExtractDependencies(string manifestJson)
        {
            var result = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(manifestJson)) return result;

            // Locate the top-level "dependencies" key and its object body.
            var block = ExtractObjectField(manifestJson, "dependencies");
            if (block == null) return result;

            // Walk the object body emitting key/value pairs. The grammar of a
            // manifest dependencies block is simple: string : string entries
            // separated by commas inside braces.
            int i = 0;
            while (i < block.Length)
            {
                // Skip whitespace + commas.
                while (i < block.Length && (block[i] == ',' || char.IsWhiteSpace(block[i]))) i++;
                if (i >= block.Length) break;
                if (block[i] != '"') { i++; continue; }

                var key = ReadJsonString(block, ref i);
                while (i < block.Length && (char.IsWhiteSpace(block[i]) || block[i] == ':')) i++;
                if (i >= block.Length || block[i] != '"') continue;

                var value = ReadJsonString(block, ref i);
                if (!string.IsNullOrEmpty(key)) result[key] = value ?? "";
            }
            return result;
        }

        // Extract the inner body of an object-valued field from a JSON string
        // (the substring inside the field's {...}). Returns null when the
        // field is missing or not an object.
        static string ExtractObjectField(string json, string fieldName)
        {
            var pattern = "\"" + fieldName + "\"";
            var idx = json.IndexOf(pattern, System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return null;
            var i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;

            int depth = 1;
            var start = i + 1;
            i++;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '"')
                {
                    i++;
                    while (i < json.Length)
                    {
                        if (json[i] == '\\') { i += 2; continue; }
                        if (json[i] == '"') { i++; break; }
                        i++;
                    }
                    continue;
                }
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                i++;
            }
            return json.Substring(start, i - start - 1);
        }

        static string ReadJsonString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') { i++; return ""; }
            i++;
            var sb = new StringBuilder(32);
            while (i < json.Length)
            {
                var c = json[i++];
                if (c == '\\')
                {
                    if (i >= json.Length) break;
                    var e = json[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 3 < json.Length)
                            {
                                sb.Append((char)System.Convert.ToUInt16(json.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else if (c == '"') return sb.ToString();
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Escape a string for inline JSON (mirrors AssetsTools.Esc /
        // TypedTargets.Esc so the package responses are byte-identical in
        // style to the rest of the typed surface).
        public static string Esc(string s)
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
