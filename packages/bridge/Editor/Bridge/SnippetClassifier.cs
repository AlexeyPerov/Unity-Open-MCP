using System.Text.RegularExpressions;

namespace UnityOpenMcpBridge
{
    // What the text of an execute_csharp snippet says about its effect on the
    // Editor lifecycle.
    internal enum SnippetEffect
    {
        /// <summary>No known lifecycle effect: reads, and asset / object / preference writes.</summary>
        Plain,
        /// <summary>Saves scenes — a write, and the one that clears the dirty state. Unsafe only for a
        /// scene that was never saved: Unity then opens its native save-file dialog.</summary>
        SavesScenes,
        /// <summary>Resolves a member by a computed name, or has no code: the text does not show what runs.</summary>
        Opaque,
        /// <summary>Calls an API that can compile, reload the domain, import, switch the scene or stage,
        /// change play mode, run a menu item, build, or raise a native prompt.</summary>
        Disruptive,
    }

    internal readonly struct SnippetVerdict
    {
        internal readonly SnippetEffect Effect;
        /// <summary>"the snippet calls OpenScene (scene switch)"; null for Plain and for an empty snippet.</summary>
        internal readonly string Reason;

        internal SnippetVerdict(SnippetEffect effect, string reason)
        {
            Effect = effect;
            Reason = reason;
        }
    }

    // The one classification of a snippet. Whether read_only is honoured, the
    // lifecycle the call reports and settles under, and whether the dirty-scene
    // guard preflights it are all read off this verdict (EffectiveToolContract).
    //
    // It is a deny-list over raw text and errs towards Disruptive: a listed name
    // inside a string literal or a comment counts, which is what catches a
    // reflective lookup by literal name (GetMethod("OpenScene")); a lookup by a
    // computed name is Opaque and keeps the tool's guarded default. A name is
    // matched without its declaring type, so an unrelated member with the same
    // name is guarded too — the caller's exit is ignore_scene_dirty.
    //
    // It is a scope heuristic, not a safety boundary: what a project method does
    // when the snippet calls it is invisible here.
    internal static class SnippetClassifier
    {
        private sealed class Rule
        {
            internal readonly Regex Pattern;
            internal readonly string Kind;
            /// <summary>What the snippet does, after "the snippet "; null means "calls " + the matched name.</summary>
            internal readonly string Label;

            internal Rule(string pattern, string kind, string label)
            {
                Pattern = new Regex(pattern, RegexOptions.CultureInvariant);
                Kind = kind;
                Label = label;
            }
        }

        private static Rule Calls(string kind, string names) => new Rule(@"\b(?:" + names + @")\b", kind, null);

        private static readonly Rule[] DisruptiveRules =
        {
            Calls("scene switch",
                "OpenScene|NewScene|CloseScene|LoadScene|LoadSceneAsync|UnloadScene|UnloadSceneAsync|" +
                "LoadSceneInPlayMode|LoadSceneAsyncInPlayMode|RestoreSceneManagerSetup|LoadSceneManagerSetup|" +
                @"LoadLastSceneManagerSetup|ReloadScene|MergeScenes|NewEmptyScene|OpenSceneAdditive|LoadLevel\w*"),
            // Opening a scene asset is the double-click path: Unity asks to save modified scenes.
            Calls("asset open: a scene or prefab asset switches the scene or stage", "OpenAsset"),
            Calls("prefab stage switch", "OpenPrefab|GoToMainStage|GoBackToPreviousStage|GoToStage"),
            Calls("native save prompt", @"\w*IfUserWantsTo|EnsureUntitledSceneHasBeenSaved|SaveSceneAs"),
            Calls("recompile / domain reload",
                @"RequestScriptCompilation|RequestScriptReload|RequestReload|ReloadAssembly|ReloadAssemblies|" +
                @"LockReloadAssemblies|UnlockReloadAssemblies|SetScriptingDefineSymbols\w*|SetScriptingBackend|" +
                "SetApiCompatibilityLevel|SetAdditionalCompilerArguments|SetExecutionOrder"),
            // SaveAndReimport reloads the domain when the importer is a script's or a plugin's.
            Calls("asset import", "Refresh|ImportAsset|ImportPackage|SaveAndReimport|ForceReserializeAssets|AllowAutoRefresh"),
            // The path is not known here: it can be an open scene or a script.
            Calls("asset move / delete", "DeleteAsset|DeleteAssets|MoveAsset|MoveAssetToTrash|MoveAssetsToTrash|RenameAsset"),
            Calls("play mode change", "EnterPlaymode|ExitPlaymode"),
            new Rule(@"\bisPlaying\s*[|&^]?=(?!=)", "play mode change", "sets isPlaying"),
            new Rule(@"""isPlaying""(?=[\s\S]*\b(?:SetValue|SetMethod|GetSetMethod)\b)|\b(?:SetValue|SetMethod|GetSetMethod)\b(?=[\s\S]*""isPlaying"")",
                "play mode change", "sets isPlaying through reflection"),
            Calls("menu execution", "ExecuteMenuItem"),
            Calls("build / platform change", @"BuildPlayer\w*|BuildAssetBundles?|SwitchActiveBuildTarget\w*"),
            new Rule(@"\bPlayerSettings\b(?:\s*\.\s*\w+)*?\s*\.\s*(?:Set\w+\s*\(|\w+\s*[-+*/|&^]?=(?!=))",
                "player settings change, which can recompile", "writes PlayerSettings"),
            new Rule(@"\bClient\s*\.\s*(?:AddAndRemove|Add|Remove|Embed|Resolve)\b", "package change", "changes installed packages"),
            Calls("test run", "TestRunnerApi"),
            new Rule(@"\b(?:EditorApplication|Environment)\s*\.\s*Exit\b|\bOpenProject\b",
                "editor exit / project switch", "exits the Editor or opens another project"),
        };

        // A file write that names a compile input: the next import recompiles.
        private static readonly Regex CompileInputLiteral = new Regex(
            @"(?:\.(?:cs|asmdef|asmref|dll|rsp)|manifest\.json)""", RegexOptions.CultureInvariant);
        private static readonly Regex FileWrite = new Regex(
            @"\bFile\s*\.\s*(?:Write\w*|Append\w*|Copy|Move|Delete|Replace|Create\w*|OpenWrite)\b|" +
            @"\bDirectory\s*\.\s*(?:Move|Delete)\b|\b(?:StreamWriter|FileStream|CopyAsset|CreateScriptAsset\w*)\b",
            RegexOptions.CultureInvariant);

        private static readonly Regex SceneSave = new Regex(
            @"\b(?:SaveScene|SaveScenes|SaveOpenScenes)\b", RegexOptions.CultureInvariant);

        // A member looked up by name whose name argument is not one string
        // literal (or nameof): the lookup target cannot be read off the text.
        // Field lookups are left out — reading or writing a field runs no code.
        private static readonly Regex ComputedMemberName = new Regex(
            @"\b(GetMethod|GetProperty|GetMember|GetRuntimeMethod|GetRuntimeProperty|GetDeclaredMethod|GetDeclaredProperty|" +
            @"InvokeMember|CreateDelegate|SendMessage|SendMessageUpwards|BroadcastMessage)\s*\((?>\s*)" +
            @"(?!@?""(?:[^""\\]|\\.)*""\s*[,)]|nameof\s*\()",
            RegexOptions.CultureInvariant);
        // A member picked out of an enumeration and then run.
        private static readonly Regex EnumeratedMethods = new Regex(
            @"\b(GetMethods|GetMembers|GetRuntimeMethods|GetDeclaredMethods)\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex MethodInvoke = new Regex(
            @"\b(?:Invoke|DynamicInvoke|CreateDelegate)\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex EnumeratedProperties = new Regex(
            @"\b(GetProperties|GetRuntimeProperties|GetDeclaredProperties)\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex PropertyWrite = new Regex(
            @"\b(?:SetValue|SetMethod|GetSetMethod)\b", RegexOptions.CultureInvariant);

        internal static SnippetVerdict Classify(string code)
        {
            // Nothing to read: keep the tool's guarded default.
            if (string.IsNullOrWhiteSpace(code)) return new SnippetVerdict(SnippetEffect.Opaque, null);

            // Report the first disruptive call in source order.
            Rule firstRule = null;
            Match first = null;
            foreach (var rule in DisruptiveRules)
            {
                var match = rule.Pattern.Match(code);
                if (match.Success && (first == null || match.Index < first.Index))
                {
                    first = match;
                    firstRule = rule;
                }
            }
            if (first != null)
                return new SnippetVerdict(SnippetEffect.Disruptive,
                    "the snippet " + (firstRule.Label ?? "calls " + first.Value) + " (" + firstRule.Kind + ")");

            if (CompileInputLiteral.IsMatch(code) && FileWrite.IsMatch(code))
                return new SnippetVerdict(SnippetEffect.Disruptive,
                    "the snippet writes a compile input (.cs / .asmdef / .dll / manifest.json) (recompile / domain reload)");

            var computed = ComputedMemberName.Match(code);
            if (!computed.Success && MethodInvoke.IsMatch(code)) computed = EnumeratedMethods.Match(code);
            if (!computed.Success && PropertyWrite.IsMatch(code)) computed = EnumeratedProperties.Match(code);
            if (computed.Success)
                return new SnippetVerdict(SnippetEffect.Opaque,
                    "the snippet resolves a member by a computed name (" + computed.Groups[1].Value +
                    "), so its text does not show what it runs");

            var save = SceneSave.Match(code);
            if (save.Success)
                return new SnippetVerdict(SnippetEffect.SavesScenes, "the snippet calls " + save.Value + " (scene save)");

            return new SnippetVerdict(SnippetEffect.Plain, null);
        }
    }
}
