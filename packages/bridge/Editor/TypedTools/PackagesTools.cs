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
// without the root UnityEditor import.

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
    // we poll IsCompleted with a short sleep up to a deadline;
    // PackageInfo can be read from any thread once IsCompleted is true.
    //
    // These tools are NOT registry-discovered: they are wired into
    // BridgeHttpServer.DispatchTool alongside the other M16 typed tools so
    // their snake_case schemas parse the same way.
    public static class PackagesTools
    {
        // UPM operations can take a while (registry round-trip, domain
        // reload on add/remove). Generous cap; the dispatcher still enforces
        // the request-level timeout_ms.
        private const int RequestTimeoutMs = 90_000;
        private const int PollIntervalMs = 25;

        // The canonical manifest path. Relative to the project root (the
        // folder containing Assets/). Used by get_dependencies and check so
        // they can answer without spinning up a UPM request.
        private static string ManifestPath => Path.Combine(
            Directory.GetParent(UnityEngine.Application.dataPath).FullName,
            "Packages", "manifest.json");

        // --------------------------- reads --------------------------------

        // List installed UPM packages. Gate-free. Token-bounded by max_results.
        // Supports offline mode (cached resolution only) and an indirect-
        // dependency toggle.
        public static ToolDispatchResult List(string body)
        {
            bool offline = JsonBody.GetBool(body, "offline", true);
            bool includeIndirect = JsonBody.GetBool(body, "include_indirect", false);
            int maxResults = JsonBody.GetInt(body, "max_results", 200);
            if (maxResults < 1) maxResults = 1;

            var sourceFilter = ParseSourceFilter(JsonBody.GetString(body, "source"));
            var nameFilter = JsonBody.GetString(body, "name_filter");
            // direct_dependencies_only — when false, only entries from
            // manifest.json are returned (no transitive deps).
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
        // max_results.
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
        // Gate-free.
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
        // triggers package resolution (may domain-reload).
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

        // Force a reimport of a LOCAL (file:-linked) package's source so Unity
        // recompiles the package assembly. Mutating: triggers a recompile /
        // domain reload (RestartThenSettle).
        //
        // Background (specs/feedback.md): when a local package's source lives
        // outside Assets/, AssetDatabase.Refresh no-ops on it (Unity's
        // incremental compiler sees no import change). assets_refresh /
        // execute_csharp(RequestScriptCompilation) therefore cannot reliably
        // force a recompile of a file: package while a live Editor holds the
        // project. This tool resolves the package, force-reimports every .cs +
        // .asmdef under its resolved source root via the Packages/<id> import
        // view, nudges a script recompile, and — crucially — reports the
        // newest matching ScriptAssemblies DLL mtime before AND after so an
        // agent can DETECT when the recompile was a no-op and fall back to a
        // standalone Roslyn compile.
        //
        // Non-local packages (registry / git / embedded) have nothing outside
        // Assets/ to reimport here: the tool returns not_local_package and
        // points the agent at assets_refresh.
        public static ToolDispatchResult ReimportPackage(string body)
        {
            var query = JsonBody.GetString(body, "package_id");
            if (string.IsNullOrWhiteSpace(query))
                return ToolDispatchResult.Fail("missing_parameter",
                    "'package_id' is required (the package name, packageId, or " +
                    "displayName — e.g. 'com.alexeyperov.unity-open-mcp-bridge').");

            var installed = CollectInstalled();
            if (installed == null)
                return Fail("list_request_failed",
                    "Client.List failed; cannot resolve the package. " +
                    "Retry, or call unity_open_mcp_read_compile_errors to inspect compile state.");

            // Resolve by name (exact) first, then by MatchesPackage (name /
            // packageId / displayName). Mirrors the resolution the other
            // package tools use.
            PackageInfo pkg = null;
            var trimmed = query.Contains("@")
                ? query.Substring(0, query.IndexOf('@'))
                : query;
            if (installed.TryGetValue(trimmed, out var exact))
            {
                pkg = exact;
            }
            else
            {
                foreach (var p in installed.Values)
                {
                    if (MatchesPackage(p, query)) { pkg = p; break; }
                }
            }
            if (pkg == null)
                return Fail("package_not_found",
                    $"No installed package matches '{query}'. Use " +
                    "unity_open_mcp_package_list to enumerate installed packages.");

            // Only local packages have source outside Assets/ worth reimporting.
            if (pkg.source != PackageSource.Local)
            {
                var nlSb = new StringBuilder(160);
                nlSb.Append("{\"status\":\"not_local_package\",");
                nlSb.Append("\"packageId\":\"").Append(Esc(pkg.name)).Append("\",");
                nlSb.Append("\"source\":\"").Append(Esc(pkg.source.ToString())).Append("\",");
                nlSb.Append("\"agentNextSteps\":[");
                nlSb.Append("\"Only local (file:-linked) packages have source outside Assets/ to reimport; this package is ")
                  .Append(Esc(pkg.source.ToString())).Append(".\",");
                nlSb.Append("\"For a generic asset reimport, call unity_open_mcp_assets_refresh.\",");
                nlSb.Append("\"To pull a newer version of a registry/git package, use unity_open_mcp_package_add with the desired specifier.\"");
                nlSb.Append("]}");
                return ToolDispatchResult.Ok(nlSb.ToString());
            }

            var resolvedPath = pkg.resolvedPath;
            if (string.IsNullOrEmpty(resolvedPath) || !Directory.Exists(resolvedPath))
                return Fail("resolved_path_unavailable",
                    $"Resolved path for '{pkg.name}' is unavailable or missing " +
                    $"('{resolvedPath}'). The package may not be fully resolved; " +
                    "retry, or call unity_open_mcp_package_get_info.");

            // Snapshot the matching ScriptAssemblies DLL(s) BEFORE the reimport
            // so we can detect whether Unity actually rebuilt the assembly.
            // Mapping: read each .asmdef under the package root, take its
            // assembly name, map to Library/ScriptAssemblies/<name>.dll.
            var assemblyNames = CollectPackageAssemblyNames(resolvedPath);
            var dllDir = Path.Combine(
                Directory.GetParent(UnityEngine.Application.dataPath).FullName,
                "Library", "ScriptAssemblies");
            long mtimeBefore = NewestAssemblyMtime(dllDir, assemblyNames);

            // Enumerate source files (.cs + .asmdef) under the resolved root and
            // force-reimport each through its Packages/<id>/... import path
            // (AssetDatabase understands that form for local packages; the
            // physical resolvedPath is outside Assets/ and not importable
            // directly). Mirror AssemblyDefinitionTools.WriteAsmdef's
            // ImportAsset(ForceUpdate) + RestartThenSettle contract.
            var packageImportRoot = "Packages/" + pkg.name;
            int reimported = 0;
            var reimportErrors = new List<string>();
            try
            {
                foreach (var relPath in EnumeratePackageSourcePaths(resolvedPath, packageImportRoot))
                {
                    try
                    {
                        UnityEditor.AssetDatabase.ImportAsset(relPath, UnityEditor.ImportAssetOptions.ForceUpdate);
                        reimported++;
                    }
                    catch (System.Exception e)
                    {
                        reimportErrors.Add(relPath + ": " + e.Message);
                    }
                }
            }
            catch (System.Exception e)
            {
                return Fail("enumerate_failed",
                    $"Failed to enumerate source under '{resolvedPath}': {e.Message}");
            }

            // Belt-and-suspenders: explicitly request a script compilation. The
            // dispatcher's RestartThenSettle window (set via ToolLifecycle) then
            // blocks until the compile settles before this method returns.
            try
            {
                UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            }
            catch
            {
                // RequestScriptCompilation throws if a compile is already in
                // flight; the settle wait below covers either case. Ignore.
            }

            // Block briefly for the compile to settle on this (main) thread so
            // the AFTER mtime reflects the recompiled DLL. The dispatcher's own
            // RestartThenSettle wait covers the long tail; this short inline
            // poll lets us report a meaningful dllMtimeAfter without relying
            // solely on the envelope's settleMs.
            SpinWaitForCompileSettle();

            long mtimeAfter = NewestAssemblyMtime(dllDir, assemblyNames);
            bool recompiled = mtimeAfter > mtimeBefore;

            var sb = new StringBuilder(256);
            sb.Append("{\"status\":\"ok\",");
            sb.Append("\"packageId\":\"").Append(Esc(pkg.name)).Append("\",");
            sb.Append("\"source\":\"").Append(Esc(pkg.source.ToString())).Append("\",");
            sb.Append("\"resolvedPath\":\"").Append(Esc(resolvedPath)).Append("\",");
            sb.Append("\"reimportedFiles\":").Append(reimported).Append(',');
            sb.Append("\"assemblyCount\":").Append(assemblyNames.Count).Append(',');
            sb.Append("\"dllMtimeBefore\":").Append(mtimeBefore).Append(',');
            sb.Append("\"dllMtimeAfter\":").Append(mtimeAfter).Append(',');
            sb.Append("\"recompiled\":").Append(recompiled ? "true" : "false").Append(',');
            sb.Append("\"isCompiling\":").Append(UnityEditor.EditorApplication.isCompiling ? "true" : "false");
            if (reimportErrors.Count > 0)
            {
                sb.Append(",\"reimportErrors\":[");
                for (int i = 0; i < reimportErrors.Count && i < 20; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Esc(reimportErrors[i])).Append('"');
                }
                sb.Append(']');
            }
            sb.Append(",\"agentNextSteps\":[");
            if (recompiled)
            {
                sb.Append("\"The package assembly was rebuilt (dllMtimeAfter > dllMtimeBefore). To confirm there are no compile errors, call unity_open_mcp_read_compile_errors.\"");
            }
            else
            {
                sb.Append("\"Unity's incremental compiler did NOT rebuild the package assembly (dllMtimeAfter == dllMtimeBefore). This is a known no-op case for local file: packages.\",");
                sb.Append("\"For real syntax/type validation, do a standalone Roslyn compile of the changed .cs files against netstandard.dll + the UnityEngine/* module DLLs (csc.dll under <Unity>/<version>/Editor/Data/NetStandard/DotNetSdkRoslyn), filtering reference-assembly artifacts that need the full Unity ref set.\",");
                sb.Append("\"Or call unity_open_mcp_read_compile_errors to inspect Unity's Editor.log for compile errors.\",");
                sb.Append("\"As a last resort, a no-op unity_open_mcp_package_add with the same package id, or refocusing the Editor window, can nudge Unity into recompiling.\"");
            }
            sb.Append("]}");
            return ToolDispatchResult.Ok(sb.ToString());
        }

        // ----------------------------- helpers ----------------------------

        private static PackageSource? ParseSourceFilter(string raw)
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
        private static bool WaitForRequest<T>(T request) where T : Request
        {
            var deadline = System.DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
            while (!request.IsCompleted)
            {
                if (System.DateTime.UtcNow > deadline) return false;
                System.Threading.Thread.Sleep(PollIntervalMs);
            }
            return true;
        }

        private static ToolDispatchResult Timeout(string op) =>
            ToolDispatchResult.Fail("timeout",
                $"Package Manager {op} request timed out after {RequestTimeoutMs} ms.");

        private static ToolDispatchResult Fail(string code, string message) =>
            ToolDispatchResult.Fail(code, message);

        // Name filter: case-insensitive substring over name/displayName/
        // description. Empty filter accepts everything.
        private static bool MatchName(PackageInfo pkg, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return MatchPriority(pkg, filter) > 0;
        }

        // Match priority: 5 exact name, 4 exact displayName, 3 name substring,
        // 2 displayName substring, 1 description substring, 0 no match.
        // Higher = better.
        private static int MatchPriority(PackageInfo pkg, string filter)
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
        private static bool MatchesPackage(PackageInfo pkg, string query)
        {
            if (string.Equals(pkg.name, query, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pkg.packageId)
                && string.Equals(pkg.packageId, query, System.StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pkg.displayName)
                && string.Equals(pkg.displayName, query, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Dictionary<string, PackageInfo> CollectInstalled()
        {
            var req = Client.List(true);
            if (!WaitForRequest(req) || req.Status != StatusCode.Success) return null;
            var dict = new Dictionary<string, PackageInfo>();
            foreach (var p in req.Result) dict[p.name] = p;
            return dict;
        }

        private static string Truncate(string s, int max)
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

        private static Dictionary<string, string> ReadManifestDependencies()
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
        private static string ExtractObjectField(string json, string fieldName)
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

        private static string ReadJsonString(string json, ref int i)
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

        // --------------------- reimport_package helpers ---------------------

        // Read the assembly name from a .asmdef JSON blob without depending
        // on AsmdefJson (which lives in AssemblyDefinitionTools and is
        // internal to a different sub-concern). We only need the top-level
        // "name" field; a lightweight scan is enough and avoids coupling.
        private static string ReadAsmdefName(string asmdefJson)
        {
            if (string.IsNullOrEmpty(asmdefJson)) return null;
            // Match `"name"` followed by a colon and a quoted string. Cheap and
            // sufficient for asmdef (Unity writes the field canonically).
            var idx = asmdefJson.IndexOf("\"name\"", System.StringComparison.Ordinal);
            while (idx >= 0)
            {
                int i = idx + 6;
                while (i < asmdefJson.Length && char.IsWhiteSpace(asmdefJson[i])) i++;
                if (i < asmdefJson.Length && asmdefJson[i] == ':')
                {
                    i++;
                    while (i < asmdefJson.Length && char.IsWhiteSpace(asmdefJson[i])) i++;
                    if (i < asmdefJson.Length && asmdefJson[i] == '"')
                    {
                        i++;
                        int start = i;
                        while (i < asmdefJson.Length && asmdefJson[i] != '"')
                        {
                            if (asmdefJson[i] == '\\' && i + 1 < asmdefJson.Length) i++;
                            i++;
                        }
                        return asmdefJson.Substring(start, i - start);
                    }
                }
                idx = asmdefJson.IndexOf("\"name\"", idx + 6, System.StringComparison.Ordinal);
            }
            return null;
        }

        // Walk the package source root for .asmdef files and collect their
        // declared assembly names. These map to Library/ScriptAssemblies/
        // <AssemblyName>.dll (Unity compiles each asmdef into one assembly).
        private static List<string> CollectPackageAssemblyNames(string resolvedPath)
        {
            var names = new List<string>();
            string[] asmdefs;
            try { asmdefs = Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories); }
            catch { return names; }
            foreach (var asmdef in asmdefs)
            {
                try
                {
                    var name = ReadAsmdefName(File.ReadAllText(asmdef));
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
                catch { /* skip unreadable asmdef */ }
            }
            return names;
        }

        // Newest write time (UTC ticks) among the package's assemblies under
        // Library/ScriptAssemblies/. Returns long.MinValue when no DLL matches
        // (so the recompiled flag stays false rather than spuriously true).
        // When assemblyNames is empty, falls back to the newest DLL in the dir
        // so the agent still gets SOME signal.
        private static long NewestAssemblyMtime(string scriptAssembliesDir, List<string> assemblyNames)
        {
            if (string.IsNullOrEmpty(scriptAssembliesDir) || !Directory.Exists(scriptAssembliesDir))
                return System.DateTime.MinValue.Ticks;

            string[] candidates;
            try { candidates = Directory.GetFiles(scriptAssembliesDir, "*.dll"); }
            catch { return System.DateTime.MinValue.Ticks; }

            long newest = System.DateTime.MinValue.Ticks;
            bool useAll = assemblyNames == null || assemblyNames.Count == 0;
            foreach (var dll in candidates)
            {
                string fileName = Path.GetFileNameWithoutExtension(dll);
                bool match = useAll;
                if (!match)
                {
                    foreach (var n in assemblyNames)
                    {
                        if (string.Equals(fileName, n, System.StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                }
                if (!match) continue;
                try
                {
                    var t = System.IO.File.GetLastWriteTimeUtc(dll).Ticks;
                    if (t > newest) newest = t;
                }
                catch { /* file vanished mid-scan */ }
            }
            return newest;
        }

        // Enumerate the package's source files (.cs + .asmdef) under the
        // physical resolvedPath and yield their Packages/<id>/... import-view
        // paths (the form AssetDatabase.ImportAsset understands for local
        // packages whose real source is outside Assets/).
        private static IEnumerable<string> EnumeratePackageSourcePaths(string resolvedPath, string packageImportRoot)
        {
            string[] files;
            try
            {
                var list = new List<string>();
                list.AddRange(Directory.GetFiles(resolvedPath, "*.cs", SearchOption.AllDirectories));
                list.AddRange(Directory.GetFiles(resolvedPath, "*.asmdef", SearchOption.AllDirectories));
                files = list.ToArray();
            }
            catch { yield break; }

            string resolvedFull = Path.GetFullPath(resolvedPath).TrimEnd('/', '\\');
            foreach (var abs in files)
            {
                string rel;
                try { rel = Path.GetFullPath(abs).Substring(resolvedFull.Length).TrimStart('/', '\\').Replace('\\', '/'); }
                catch { continue; }
                if (string.IsNullOrEmpty(rel)) continue;
                yield return packageImportRoot + "/" + rel;
            }
        }

        // Short inline poll (runs on the main thread, before the dispatcher's
        // own RestartThenSettle wait) so the AFTER mtime reflects the freshly
        // compiled DLL. Caps at a few seconds; the dispatcher's long-cap wait
        // covers any remaining tail. We tick EditorApplication.update manually
        // is NOT needed — CompilationPipeline.RequestScriptCompilation flips
        // isCompiling synchronously enough that a direct poll suffices.
        private static void SpinWaitForCompileSettle()
        {
            const int capMs = 4000;
            const int tickMs = 100;
            int elapsed = 0;
            // Give Unity a beat to START compiling before we poll for it to
            // FINISH — RequestScriptCompilation may not flip isCompiling within
            // the same frame.
            System.Threading.Thread.Sleep(tickMs);
            elapsed += tickMs;
            while (elapsed < capMs)
            {
                if (!UnityEditor.EditorApplication.isCompiling) break;
                System.Threading.Thread.Sleep(tickMs);
                elapsed += tickMs;
            }
        }
    }
}
