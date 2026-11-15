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
        /// <summary>An expression that assigns, increments, holds a statement, or calls a known
        /// object / state write. Only <see cref="SnippetClassifier.ClassifyExpression"/> reports it.</summary>
        Writes,
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

        private static readonly Regex AssetImportCall = new Regex(
            @"\b(?:Refresh|ImportAsset|ImportPackage|SaveAndReimport|ForceReserializeAssets|AllowAutoRefresh)\b",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Whether the snippet text calls an asset refresh or import (the
        /// "asset import" rule): it can reimport an open scene whose file
        /// changed on disk, which raises Unity's native dialog.
        /// </summary>
        internal static bool CallsAssetImport(string code) =>
            !string.IsNullOrEmpty(code) && AssetImportCall.IsMatch(code);

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

        // ------------------------------------------------------------------
        // Expressions (playtest_probe): a value read every N frames. On top of
        // Classify, an expression must not write: no assignment outside an
        // object / anonymous-type initializer, no ++ / --, no statement (a `;`
        // or a block-bodied lambda), and no call of a well-known write (object
        // lifecycle, transform / physics / animator / prefs setters, messages,
        // coroutines, collection mutators). Same nature as Classify: a deny-list
        // over the text that errs towards refusal; what a project method does
        // when the expression calls it stays invisible.
        // ------------------------------------------------------------------

        private static readonly Regex WriteCall = new Regex(
            @"\b(?:Destroy\w*|DontDestroyOnLoad|Instantiate\w*|Add(?:[A-Z]\w*)?|Remove\w*|Delete\w*|Clear\w*|Insert\w*|" +
            @"Set[A-Z]\w*|Reset[A-Z]?\w*|Save\w*|Write\w*|Translate|Rotate|RotateAround|LookAt|MovePosition|MoveRotation|" +
            @"Sleep|WakeUp|Simulate|SendMessage\w*|BroadcastMessage|Invoke\w*|CancelInvoke|StartCoroutine|Stop\w*|" +
            @"Play\w*|Pause|UnPause|Emit|Push|Pop|Enqueue|Dequeue|Sort|Unload\w*|Quit|Apply\w*|Dispose|Register\w*|" +
            @"Unregister\w*|Record\w*|Revert\w*)\s*(?:<[^()]*>)?\s*\(",
            RegexOptions.CultureInvariant);

        internal static SnippetVerdict ClassifyExpression(string expression)
        {
            var verdict = Classify(expression);
            if (verdict.Effect != SnippetEffect.Plain)
                return verdict.Reason != null && verdict.Reason.StartsWith("the snippet ", System.StringComparison.Ordinal)
                    ? new SnippetVerdict(verdict.Effect, "the expression " + verdict.Reason.Substring("the snippet ".Length))
                    : verdict;

            var code = CodeOnly(expression);
            int braces = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                char next = i + 1 < code.Length ? code[i + 1] : '\0';
                char prev = i > 0 ? code[i - 1] : '\0';
                switch (c)
                {
                    case ';':
                        return Writes("holds a statement (;): a probe samples one expression");
                    case '{':
                        if (PreviousToken(code, i) == "=>")
                            return Writes("has a block-bodied lambda (=> { … }): a probe samples one expression");
                        braces++;
                        break;
                    case '}':
                        if (braces > 0) braces--;
                        break;
                    case '+':
                    case '-':
                        if (next == c) return Writes("increments or decrements (" + c + c + ")");
                        break;
                    case '=':
                        if (next == '=' || next == '>') { i++; break; }
                        if (prev == '=' || prev == '!' || (prev == '<' && !(i > 1 && code[i - 2] == '<')) ||
                            (prev == '>' && !(i > 1 && code[i - 2] == '>')))
                            break;
                        // `a = b` inside new { … } / new T { … } initialises the new object.
                        bool compound = "+-*/%&|^<>?".IndexOf(prev) >= 0;
                        if (!compound && braces > 0) break;
                        bool doubled = prev == '<' || prev == '>' || (prev == '?' && i > 1 && code[i - 2] == '?');
                        return Writes("assigns (" + (compound ? (doubled ? prev.ToString() + prev : prev.ToString()) + "=" : "=") + ")");
                }
            }
            var call = WriteCall.Match(code);
            if (call.Success)
            {
                var name = Regex.Match(call.Value, @"\w+").Value;
                return Writes("calls " + name + " (a write)");
            }
            if (Regex.IsMatch(code, @"\bdelegate\b"))
                return Writes("declares an anonymous method (delegate): a probe samples one expression");
            return new SnippetVerdict(SnippetEffect.Plain, null);
        }

        private static SnippetVerdict Writes(string what) =>
            new SnippetVerdict(SnippetEffect.Writes, "the expression " + what);

        // The token before position `at` (two-char operators and words), or "".
        private static string PreviousToken(string code, int at)
        {
            int i = at - 1;
            while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
            if (i < 0) return "";
            if (i >= 1 && code[i] == '>' && code[i - 1] == '=') return "=>";
            return code[i].ToString();
        }

        // The expression with comments and the contents of string / char
        // literals blanked out (an interpolated string keeps its {holes}, which
        // are code), so operators inside text never count.
        internal static string CodeOnly(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int end = text.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    int stop = end < 0 ? text.Length : end + 2;
                    sb.Append(' ', stop - i);
                    i = stop;
                    continue;
                }
                if (c == '\'')
                {
                    sb.Append('\'');
                    i++;
                    while (i < text.Length && text[i] != '\'')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(' '); i++; }
                        sb.Append(' ');
                        i++;
                    }
                    if (i < text.Length) { sb.Append('\''); i++; }
                    continue;
                }
                // String prefixes: @"…", $"…", $@"…", @$"…".
                int p = i;
                bool verbatim = false, interpolated = false;
                while (p < text.Length && (text[p] == '@' || text[p] == '$') && p - i < 2)
                {
                    if (text[p] == '@') verbatim = true; else interpolated = true;
                    p++;
                }
                if (p < text.Length && text[p] == '"' && (p == i || verbatim || interpolated))
                {
                    sb.Append(text, i, p - i + 1);
                    i = SkipString(text, p + 1, verbatim, interpolated, sb);
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        // Copy a string literal's body as blanks (holes of an interpolated
        // string as code) up to and including its closing quote.
        private static int SkipString(string text, int i, bool verbatim, bool interpolated, System.Text.StringBuilder sb)
        {
            while (i < text.Length)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (!verbatim && c == '\\' && i + 1 < text.Length) { sb.Append("  "); i += 2; continue; }
                if (c == '"')
                {
                    if (verbatim && next == '"') { sb.Append("  "); i += 2; continue; }
                    sb.Append('"');
                    return i + 1;
                }
                if (interpolated && (c == '{' || c == '}') && next == c) { sb.Append("  "); i += 2; continue; }
                if (interpolated && c == '{')
                {
                    // A hole: code up to its matching close brace.
                    int depth = 0;
                    sb.Append('(');
                    i++;
                    while (i < text.Length)
                    {
                        char h = text[i];
                        if (h == '{') depth++;
                        else if (h == '}') { if (depth == 0) break; depth--; }
                        else if (h == '"')
                        {
                            sb.Append('"');
                            i = SkipString(text, i + 1, false, false, sb);
                            continue;
                        }
                        sb.Append(h);
                        i++;
                    }
                    sb.Append(')');
                    if (i < text.Length) i++;
                    continue;
                }
                sb.Append(' ');
                i++;
            }
            return i;
        }
    }
}
