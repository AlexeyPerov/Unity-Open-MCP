using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityOpenMcpBridge.MetaTools
{
    // Script inputs written or deleted on disk outside Unity that the
    // AssetDatabase has not caught up with yet (an agent writes files while the
    // Editor is unfocused, so Unity's auto-refresh never runs).
    // RequestScriptCompilation compiles each assembly's KNOWN source list from
    // disk: an edited file is picked up, but a NEW file is in no assembly until
    // it is imported (every use of its types fails with CS0103 / CS0246), and a
    // DELETED file is still listed (the compile fails on the missing file).
    // An EDITED .asmdef / .asmref is not picked up either: the compile reads
    // the definition from its imported artifact, not from disk, so references,
    // defines and constraints edited on disk would compile as before.
    // recompile_scripts brings them in before it requests the compile: new
    // files are imported in place (the topmost folder Unity does not know,
    // recursively, or the file itself), an edited assembly definition is
    // re-imported (its text on disk differs from the imported one), and a
    // deletion takes one AssetDatabase.Refresh — there is no scoped import of a
    // file that is gone (the refresh also brings in every edit).
    // Either can run asset postprocessors and requests a compile itself, which
    // is the compile recompile_scripts asks for anyway; nothing reloads before
    // the call returns. The refresh goes through OpenSceneDiskSync: an open
    // scene that changed on disk never leaves Unity's native dialog behind.
    // Scope: Assets/ and local / embedded packages, the mutable roots
    // (registry and git packages are immutable).
    internal static class ScriptImportSync
    {
        private static readonly string[] Extensions = { ".cs", ".asmdef", ".asmref" };
        private const int MaxListed = 20;

        internal sealed class Plan
        {
            internal readonly List<string> NewPaths = new List<string>();
            internal readonly List<string> ModifiedPaths = new List<string>();
            internal readonly List<string> DeletedPaths = new List<string>();
        }

        internal sealed class Outcome
        {
            internal Plan Plan = new Plan();
            /// <summary>none | imported | refreshed | refused | failed</summary>
            internal string Action = "none";
            internal string Error;
            /// <summary>The refresh a deletion needs, around the open scenes that changed on disk.</summary>
            internal OpenSceneDiskSync.Outcome Refresh;

            internal string ToJson()
            {
                var sb = new StringBuilder(128);
                sb.Append("{\"action\":").Append(BridgeJson.EscapeString(Action));
                AppendList(sb, "newScripts", Plan.NewPaths);
                sb.Append(",\"newCount\":").Append(Plan.NewPaths.Count);
                AppendList(sb, "modifiedAssemblyDefinitions", Plan.ModifiedPaths);
                sb.Append(",\"modifiedCount\":").Append(Plan.ModifiedPaths.Count);
                AppendList(sb, "deletedScripts", Plan.DeletedPaths);
                sb.Append(",\"deletedCount\":").Append(Plan.DeletedPaths.Count);
                if (Refresh != null && Refresh.Ran) sb.Append(',').Append(Refresh.MembersJson());
                if (Error != null) sb.Append(",\"error\":").Append(BridgeJson.EscapeString(Error));
                return sb.Append('}').ToString();
            }

            private static void AppendList(StringBuilder sb, string key, List<string> paths)
            {
                sb.Append(",\"").Append(key).Append("\":[");
                for (int i = 0; i < paths.Count && i < MaxListed; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(BridgeJson.EscapeString(paths[i]));
                }
                sb.Append(']');
            }
        }

        // An asset-path prefix ("Assets", "Packages/<name>") and the directory it maps to.
        internal struct Root
        {
            internal string Asset;
            internal string Disk;
        }

        /// <summary>Detect and import (main thread). Never throws.</summary>
        internal static Outcome Run()
        {
            var outcome = new Outcome();
            try
            {
                var roots = CaptureRoots();
                outcome.Plan = Detect(
                    EnumerateScripts(roots),
                    path => AssetDatabase.GetMainAssetTypeAtPath(path) != null,
                    CompiledInputs(),
                    path => Owned(path, roots),
                    path => File.Exists(ToDisk(path, roots)),
                    path => ImportedTextDiffers(path, roots));
                if (outcome.Plan.DeletedPaths.Count > 0)
                {
                    // An open scene that changed on disk is reloaded around the
                    // refresh, or the refresh is refused (and nothing compiled)
                    // when a reload would lose edits.
                    outcome.Refresh = OpenSceneDiskSync.Run(() => AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport));
                    outcome.Action = outcome.Refresh.Ran ? "refreshed" : "refused";
                }
                else if (outcome.Plan.NewPaths.Count > 0 || outcome.Plan.ModifiedPaths.Count > 0)
                {
                    foreach (var target in ImportTargets(outcome.Plan.NewPaths, AssetDatabase.IsValidFolder))
                    {
                        AssetDatabase.ImportAsset(target.Path, target.Recursive
                            ? ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ImportRecursive
                            : ImportAssetOptions.ForceSynchronousImport);
                    }
                    foreach (var path in outcome.Plan.ModifiedPaths)
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    outcome.Action = "imported";
                }
            }
            catch (Exception e)
            {
                outcome.Action = "failed";
                outcome.Error = e.Message;
            }
            return outcome;
        }

        /// <summary>
        /// Pure: the on-disk scripts the AssetDatabase does not know, the known
        /// assembly definitions (.asmdef / .asmref) whose disk text differs from
        /// the imported one, and the compiled inputs (sources, asmdefs) under a
        /// mutable root that are gone.
        /// </summary>
        internal static Plan Detect(
            IEnumerable<string> onDisk,
            Func<string, bool> known,
            IEnumerable<string> compiledInputs,
            Func<string, bool> owned,
            Func<string, bool> exists,
            Func<string, bool> changedOnDisk = null)
        {
            var plan = new Plan();
            foreach (var path in onDisk.Distinct(StringComparer.Ordinal))
            {
                if (!known(path)) plan.NewPaths.Add(path);
                else if (changedOnDisk != null && IsAssemblyDefinition(path) && changedOnDisk(path)) plan.ModifiedPaths.Add(path);
            }
            foreach (var path in compiledInputs.Distinct(StringComparer.Ordinal))
                if (owned(path) && !exists(path)) plan.DeletedPaths.Add(path);
            plan.NewPaths.Sort(StringComparer.Ordinal);
            plan.ModifiedPaths.Sort(StringComparer.Ordinal);
            plan.DeletedPaths.Sort(StringComparer.Ordinal);
            return plan;
        }

        internal static bool IsAssemblyDefinition(string path) =>
            path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);

        // An imported assembly definition is a TextAsset holding the file text
        // it was imported from; a different text on disk is an edit Unity has
        // not imported. A BOM is not a difference. Unreadable: not reported.
        private static bool ImportedTextDiffers(string assetPath, List<Root> roots)
        {
            try
            {
                var imported = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(assetPath);
                if (imported == null) return false;
                var disk = File.ReadAllText(ToDisk(assetPath, roots));
                return !string.Equals(imported.text.TrimStart('\uFEFF'), disk.TrimStart('\uFEFF'), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// Pure: one import per new file, or per topmost folder Unity does not
        /// know yet (imported recursively, so the folder and everything in it
        /// arrive together); nothing inside an already-listed folder twice.
        /// </summary>
        internal static List<(string Path, bool Recursive)> ImportTargets(IEnumerable<string> newPaths, Func<string, bool> isKnownFolder)
        {
            var targets = new List<(string Path, bool Recursive)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in newPaths)
            {
                string topUnknown = null;
                for (var dir = ParentOf(path); dir != null && !isKnownFolder(dir); dir = ParentOf(dir))
                    topUnknown = dir;
                var target = topUnknown ?? path;
                if (seen.Add(target)) targets.Add((target, topUnknown != null));
            }
            return targets
                .Where(t => !targets.Any(o => o.Recursive && o.Path != t.Path && t.Path.StartsWith(o.Path + "/", StringComparison.Ordinal)))
                .ToList();
        }

        private static string ParentOf(string assetPath)
        {
            var slash = assetPath.LastIndexOf('/');
            return slash > 0 ? assetPath.Substring(0, slash) : null;
        }

        private static List<Root> CaptureRoots()
        {
            var roots = new List<Root> { new Root { Asset = "Assets", Disk = Path.GetFullPath("Assets") } };
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (package.source != UnityEditor.PackageManager.PackageSource.Local
                    && package.source != UnityEditor.PackageManager.PackageSource.Embedded) continue;
                if (string.IsNullOrEmpty(package.resolvedPath) || string.IsNullOrEmpty(package.assetPath)) continue;
                roots.Add(new Root { Asset = package.assetPath.TrimEnd('/'), Disk = Path.GetFullPath(package.resolvedPath) });
            }
            return roots;
        }

        // Unity skips these names when it scans for assets.
        private static bool IsIgnored(string name) =>
            name.StartsWith(".", StringComparison.Ordinal)
            || name.EndsWith("~", StringComparison.Ordinal)
            || string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

        private static bool IsScript(string path) =>
            Extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<string> EnumerateScripts(List<Root> roots)
        {
            foreach (var root in roots)
            {
                var top = root.Disk.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(top)) continue;
                var pending = new Stack<string>();
                pending.Push(top);
                while (pending.Count > 0)
                {
                    var dir = pending.Pop();
                    string[] files, dirs;
                    try
                    {
                        files = Directory.GetFiles(dir);
                        dirs = Directory.GetDirectories(dir);
                    }
                    catch { continue; }
                    foreach (var file in files)
                    {
                        if (!IsScript(file) || IsIgnored(Path.GetFileName(file))) continue;
                        yield return root.Asset + file.Substring(top.Length).Replace('\\', '/');
                    }
                    foreach (var sub in dirs)
                        if (!IsIgnored(Path.GetFileName(sub))) pending.Push(sub);
                }
            }
        }

        private static IEnumerable<string> CompiledInputs()
        {
            foreach (var assembly in CompilationPipeline.GetAssemblies())
            {
                foreach (var source in assembly.sourceFiles) yield return source;
                var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                if (!string.IsNullOrEmpty(asmdef)) yield return asmdef;
            }
        }

        private static bool Owned(string assetPath, List<Root> roots) =>
            roots.Any(r => assetPath.StartsWith(r.Asset + "/", StringComparison.Ordinal));

        private static string ToDisk(string assetPath, List<Root> roots)
        {
            foreach (var r in roots)
                if (assetPath.StartsWith(r.Asset + "/", StringComparison.Ordinal))
                    return Path.Combine(r.Disk, assetPath.Substring(r.Asset.Length + 1));
            return assetPath;
        }
    }
}
