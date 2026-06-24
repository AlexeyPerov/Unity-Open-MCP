using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityOpenMcpBridge.TypedTools;
using UnityOpenMcpBridge.UI.Controls;

namespace UnityOpenMcpBridge
{
    /// <summary>
    /// M18 Plan 4 T18.4.2 — the in-Editor "Optional Unity dependencies" panel.
    ///
    /// Mirrors the Coplay optional-dependencies pattern
    /// (<c>MCPForUnityEditorWindow.cs</c> <c>BuildDependenciesSection</c>):
    /// one row per embedded Unity domain showing installed / missing status,
    /// with a one-click UPM install / remove for installable domains and an
    /// "always-on" badge for built-in module domains.
    ///
    /// Status detection has two paths:
    ///  - Installable domains (NavMesh, Input System, ProBuilder) read
    ///    <c>Packages/manifest.json</c> directly — same fast path as Coplay's
    ///    <c>IsUpmPackageInstalled</c>. No async UPM list round-trip.
    ///  - Built-in module domains (Particle System, Animation) probe
    ///    <see cref="Type.GetType"/> on a domain type, mirroring how Coplay
    ///    detects ProBuilder / Cinemachine.
    ///
    /// Install / remove run <c>UnityEditor.PackageManager.Client.Add</c> /
    /// <c>Client.Remove</c> and poll completion on <c>EditorApplication.update</c>
    /// with a progress bar (the dispatcher cannot block on the package-manager
    /// worker thread from the GUI thread; the Coplay poll pattern is the
    /// established idiom). On completion Unity re-imports the manifest and the
    /// bridge asmdef <c>versionDefines</c> flip the embedded tools on/off at the
    /// next domain reload — this panel does NOT write scripting defines.
    /// </summary>
    public static class OptionalDependenciesPanel
    {
        // Status of a single domain row in the current repaint. Recomputed
        // cheaply each frame from the manifest + Type probes; the per-row
        // install/remove buttons gate on this.
        public enum DepStatus
        {
            Installed,
            Missing,
            BuiltIn,
        }

        // In-flight install / remove keyed by package id. Cleared when the
        // PackageManager request completes (success or failure). Drives the
        // per-row "Installing…" / "Removing…" disabled state.
        private static readonly Dictionary<string, string> _pending = new Dictionary<string, string>();

        // Cached type-probe results. Assembly membership is fixed for the
        // lifetime of a domain reload, so we resolve each probe once and
        // reuse the answer until the Editor recompiles (which tears down all
        // static state in this class anyway). Avoids a full
        // AppDomain.GetAssemblies scan on every repaint.
        private static readonly Dictionary<string, bool> _typeProbeCache = new Dictionary<string, bool>();

        // Cached manifest-dependency snapshot so a repaint does not re-read
        // the file per row. Invalidated by the file's lastWriteTime, which
        // catches install/remove mutations from this panel and from the
        // Package Manager window alike.
        private static Dictionary<string, string> _manifestCache;
        private static string _manifestCachePath;
        private static DateTime _manifestCacheAt;
        private static readonly object _manifestLock = new object();

        // ---- public draw surface ------------------------------------------------

        /// <summary>
        /// Draw the whole panel. Call from the bridge window's Extensions tab.
        /// </summary>
        public static void Draw()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Optional Unity dependencies", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each row maps a bundled domain tool group to a Unity package. " +
                "Install the Unity dependency to activate the embedded tools; " +
                "Unity recompiles and the tools register automatically on the next " +
                "domain reload. Built-in module domains (Particle System, Animation) " +
                "ship with the Editor and are always on — there is nothing to install.",
                MessageType.None);

            foreach (var domain in EmbeddedDomainCatalog.Domains)
            {
                DrawDomainRow(domain);
            }

            EditorGUILayout.Space(2);
            var installed = 0;
            var total = 0;
            foreach (var domain in EmbeddedDomainCatalog.Domains)
            {
                total++;
                if (ResolveStatus(domain) != DepStatus.Missing) installed++;
            }
            EditorGUILayout.LabelField(
                $"Active: {installed} / {total}",
                EditorStyles.miniLabel);
        }

        // ---- per-row draw -------------------------------------------------------

        private static void DrawDomainRow(EmbeddedDomain domain)
        {
            var status = ResolveStatus(domain);
            var inFlight = IsInFlight(domain);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            // Status dot — green installed, amber missing, gray built-in.
            Color dotColor;
            if (status == DepStatus.Installed) dotColor = new Color(0.6f, 0.9f, 0.6f);
            else if (status == DepStatus.Missing) dotColor = new Color(1f, 0.85f, 0.4f);
            else dotColor = new Color(0.7f, 0.7f, 0.7f);
            var prev = GUI.color;
            GUI.color = dotColor;
            GUILayout.Label("●", EditorStyles.boldLabel, GUILayout.Width(18));
            GUI.color = prev;

            EditorGUILayout.LabelField(domain.DisplayName, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            BridgeGUIUtilities.DrawColoredLabel(StatusLabel(status), dotColor, 110);

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(domain.Description, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            if (domain.Builtin || string.IsNullOrEmpty(domain.UpmDependency))
            {
                EditorGUILayout.LabelField("Unity dep", "(built-in module)", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("Unity dep", GUILayout.Width(70));
                EditorGUILayout.SelectableLabel(domain.UpmDependency, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            EditorGUILayout.EndHorizontal();

            DrawRowAction(domain, status, inFlight);

            EditorGUILayout.EndVertical();
        }

        private static void DrawRowAction(EmbeddedDomain domain, DepStatus status, bool inFlight)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (domain.Builtin)
            {
                // Built-in modules have no manifest entry — nothing to install
                // or remove. The status dot already conveys "always on".
                EditorGUI.BeginDisabledGroup(true);
                GUILayout.Button("Always on", EditorStyles.miniButton, GUILayout.Width(110));
                EditorGUI.EndDisabledGroup();
            }
            else if (inFlight)
            {
                EditorGUI.BeginDisabledGroup(true);
                GUILayout.Button(_pending[domain.UpmDependency], EditorStyles.miniButton, GUILayout.Width(130));
                EditorGUI.EndDisabledGroup();
            }
            else if (status == DepStatus.Installed)
            {
                if (GUILayout.Button("Remove…", EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    ConfirmAndRemove(domain);
                }
            }
            else
            {
                if (GUILayout.Button("Install…", EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    ConfirmAndInstall(domain);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ---- status resolution --------------------------------------------------

        private static DepStatus ResolveStatus(EmbeddedDomain domain)
        {
            // Secondary check: even when the manifest lists a UPM package, a
            // failed recompile or a stale install can leave the embedded
            // tools unloaded. Probing the domain's anchor type distinguishes
            // "manifest entry present" from "tools actually compiled in".
            if (!IsTypeProbeAvailable(domain))
            {
                // For built-in module domains this path never reports Missing
                // in practice (the probe type always resolves when the module
                // is enabled), but the guard keeps the panel honest if a
                // module is disabled in the Editor.
                return domain.Builtin ? DepStatus.BuiltIn : DepStatus.Missing;
            }

            if (domain.Builtin) return DepStatus.BuiltIn;
            return IsUpmPackageInstalled(domain.UpmDependency) ? DepStatus.Installed : DepStatus.Missing;
        }

        // `Type.GetType` with the simple name only searches the calling
        // assembly + mscorlib; we fall back to a full assembly scan so
        // engine/module types resolve without an assembly-qualified probe.
        // Results are cached per domain reload (assembly membership is fixed
        // until recompile).
        private static bool IsTypeProbeAvailable(EmbeddedDomain domain)
        {
            if (string.IsNullOrEmpty(domain.TypeProbe)) return true;
            if (_typeProbeCache.TryGetValue(domain.TypeProbe, out var cached)) return cached;

            bool found;
            if (Type.GetType(domain.TypeProbe) != null)
            {
                found = true;
            }
            else
            {
                var simple = domain.TypeProbe.Contains(",")
                    ? domain.TypeProbe.Substring(0, domain.TypeProbe.IndexOf(',')).Trim()
                    : domain.TypeProbe;
                found = false;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetType(simple, false) != null) { found = true; break; }
                }
            }
            _typeProbeCache[domain.TypeProbe] = found;
            return found;
        }

        private static string StatusLabel(DepStatus status)
        {
            return status switch
            {
                DepStatus.Installed => "installed",
                DepStatus.Missing => "missing",
                DepStatus.BuiltIn => "built-in",
                _ => status.ToString(),
            };
        }

        // ---- manifest presence check (fast path, mirrors Coplay) ---------------

        private static string ManifestPath
        {
            get
            {
                var dataParent = Directory.GetParent(Application.dataPath);
                return dataParent == null ? "" : Path.Combine(dataParent.FullName, "Packages", "manifest.json");
            }
        }

        // Reads manifest.json and answers "is this package id present in the
        // dependencies block". Caches by lastWriteTime so repeated repaints do
        // not re-read the file. The check is intentionally naive ("contains
        // \"<id>\"") to match the Coplay reference — the manifest dependencies
        // block is flat and the keys are package ids.
        public static bool IsUpmPackageInstalled(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            var deps = ReadManifestDependencies();
            return deps != null && deps.ContainsKey(packageId);
        }

        // Snapshot of the manifest dependencies block (package id → reference).
        // Returns an empty dict when the manifest is missing or unreadable.
        // Reuses the public, EditMode-tested `PackagesTools.ExtractDependencies`
        // parser so the dependency-block grammar stays in one place.
        private static Dictionary<string, string> ReadManifestDependencies()
        {
            var path = ManifestPath;
            if (string.IsNullOrEmpty(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            lock (_manifestLock)
            {
                DateTime writeTime;
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                if (_manifestCache != null
                    && _manifestCachePath == path
                    && _manifestCacheAt == writeTime)
                {
                    return _manifestCache;
                }

                Dictionary<string, string> result;
                try
                {
                    var json = File.ReadAllText(path);
                    result = PackagesTools.ExtractDependencies(json);
                }
                catch
                {
                    result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                _manifestCache = result;
                _manifestCachePath = path;
                _manifestCacheAt = writeTime;
                return result;
            }
        }

        // ---- install / remove (PackageManager async + poll) --------------------

        private static bool IsInFlight(EmbeddedDomain domain)
        {
            return !domain.Builtin
                && !string.IsNullOrEmpty(domain.UpmDependency)
                && _pending.ContainsKey(domain.UpmDependency);
        }

        private static void ConfirmAndInstall(EmbeddedDomain domain)
        {
            if (!EditorUtility.DisplayDialog(
                    "Install Unity dependency",
                    $"Install '{domain.UpmDependency}' via the Unity Package Manager?\n" +
                    "Unity will re-import the manifest and recompile. The embedded " +
                    $"{domain.DisplayName} tools register after the recompile completes.",
                    "Install", "Cancel"))
            {
                return;
            }
            InstallUpmPackage(domain.UpmDependency);
        }

        private static void ConfirmAndRemove(EmbeddedDomain domain)
        {
            if (!EditorUtility.DisplayDialog(
                    "Remove Unity dependency",
                    $"Remove '{domain.UpmDependency}' from this project?\n" +
                    $"The embedded {domain.DisplayName} tools will stop compiling in " +
                    "after the recompile. Other packages that depend on it may break.",
                    "Remove", "Cancel"))
            {
                return;
            }
            RemoveUpmPackage(domain.UpmDependency);
        }

        // Add the package (latest compatible version when the id carries no
        // @version, mirroring the Coplay install path). The request is polled
        // on EditorApplication.update; a progress bar shows while it runs.
        private static void InstallUpmPackage(string packageId)
        {
            _pending[packageId] = "Installing…";
            AddRequest request;
            try
            {
                request = Client.Add(packageId);
            }
            catch (Exception e)
            {
                _pending.Remove(packageId);
                ReportFailure("install", packageId, e.Message);
                return;
            }
            PollRequest(request, packageId, "install");
        }

        private static void RemoveUpmPackage(string packageId)
        {
            _pending[packageId] = "Removing…";
            RemoveRequest request;
            try
            {
                request = Client.Remove(packageId);
            }
            catch (Exception e)
            {
                _pending.Remove(packageId);
                ReportFailure("remove", packageId, e.Message);
                return;
            }
            PollRequest(request, packageId, "remove");
        }

        // Poll the PackageManager request on the Editor update loop and clear
        // the progress bar + pending entry when it completes. The request
        // advances on a package-manager worker thread; we must not block the
        // GUI thread, so we register a one-shot update callback (same idiom
        // as the Coplay PollUpmRequest).
        private static void PollRequest<T>(T request, string packageId, string verb) where T : Request
        {
            EditorUtility.DisplayProgressBar(
                verb == "install" ? "Installing package" : "Removing package",
                $"{verb} '{packageId}'…", 0.5f);

            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (request != null && !request.IsCompleted) return;
                EditorApplication.update -= poll;
                EditorUtility.ClearProgressBar();
                _pending.Remove(packageId);
                InvalidateManifestCache();

                if (request == null || request.Status != StatusCode.Success)
                {
                    var msg = "unknown error";
                    // Request.Error is request-type-specific; reflect defensively
                    // so this helper stays generic over Add / Remove requests.
                    var errProp = request?.GetType().GetProperty("Error");
                    if (errProp?.GetValue(request) is Error err && !string.IsNullOrEmpty(err.message))
                        msg = err.message;
                    ReportFailure(verb, packageId, msg);
                    return;
                }

                ReportSuccess(verb, packageId);
            };
            EditorApplication.update += poll;
        }

        private static void InvalidateManifestCache()
        {
            lock (_manifestLock)
            {
                _manifestCache = null;
                _manifestCachePath = null;
            }
        }

        private static void ReportSuccess(string verb, string packageId)
        {
            Debug.Log($"[Unity Open MCP] Optional dependency {verb} succeeded for '{packageId}'. " +
                      "Unity will recompile; the embedded tools register on the next domain reload.");
        }

        private static void ReportFailure(string verb, string packageId, string message)
        {
            Debug.LogError($"[Unity Open MCP] Optional dependency {verb} failed for '{packageId}': {message}");
            EditorUtility.DisplayDialog(
                "Package operation failed",
                $"{verb} '{packageId}' failed:\n{message}\n\n" +
                "Check the Unity console for details.",
                "OK");
        }
    }
}
