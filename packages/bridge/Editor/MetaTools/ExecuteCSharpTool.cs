using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge.ObjectRefs;

namespace UnityOpenMcpBridge.MetaTools
{
    public static class ExecuteCSharpTool
    {
        // M30-polish T4.5, extended by the fd-exhaustion fix — snippet assembly
        // lifecycle. Assembly.Load(byte[]) assemblies are tracked by the
        // AppDomain and are NOT unloadable without a collectible
        // AssemblyLoadContext (the full fix, deferred to backlog), so every
        // DISTINCT snippet necessarily grows the domain by one assembly until
        // the next reload. What is avoidable is re-compiling and re-loading
        // snippets this session has already seen: the cache below keys the
        // loaded assembly by the SHA256 of the fully generated compilation
        // unit (BuildSource output — usings + wrapper + body), so re-running
        // ANY previously executed snippet — not just the most recent one, as
        // the old single-slot last-PE cache did — reuses its assembly and
        // skips the Roslyn compile entirely. (Hashing the source instead of
        // the PE also dodges nondeterministic PE bytes such as the COFF
        // timestamp.) Entries live until domain reload; eviction would release
        // nothing, because the AppDomain pins the assembly regardless.
        //
        // Type-resolution ambiguity between accumulated snippet assemblies
        // (they all declare UnityOpenMcpSnippet.Snippet) is handled by
        // IsSnippetAssembly — type catalogs skip them all, and Execute always
        // resolves the type from the assembly the cache returned for THIS
        // call. True unload via collectible ALC is tracked in
        // specs/backlog/backlog-packages.md (P2 — Collectible ALC).
        private static readonly Dictionary<string, Assembly> s_snippetAssemblies =
            new Dictionary<string, Assembly>();
        private static readonly object s_snippetLock = new object();

        // The transient namespace + assembly-name prefix every compiled snippet
        // is emitted into (see BuildSource). Used by IsSnippetAssembly so type-
        // lookup helpers (ComponentsTools.ResolveComponentType,
        // ObjectHandle.TryResolveType) can skip snippet assemblies — they are
        // internal scratch assemblies that must never surface as resolvable
        // component/object types, and whose load-order-dependent presence
        // caused undefined type resolution.
        internal const string SnippetAssemblyName = "UnityOpenMcpSnippet";

        // True for assemblies produced by execute_csharp (named via
        // Assembly.Load's anonymous-name convention, which prefixes the simple
        // name with the namespace). Skipping these in type catalogs keeps the
        // snippet type out of agent-facing type resolution.
        internal static bool IsSnippetAssembly(System.Reflection.Assembly asm)
        {
            if (asm == null) return false;
            try
            {
                var name = asm.GetName().Name;
                return name != null && name.StartsWith(SnippetAssemblyName, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static readonly string[] DefaultUsings =
        {
            "System",
            "System.IO",
            "System.Linq",
            "System.Collections",
            "System.Collections.Generic",
            "UnityEngine",
            "UnityEditor"
        };

        public static ToolDispatchResult Execute(string body)
        {
            var code = JsonBody.GetString(body, "code");

            // Roslyn fallback setup flow (Unity 6000.x ships only R2R Roslyn
            // images — see RoslynHost.UnavailableHint). The explicit
            // setup_roslyn call is the consent act for the ~15 MB nuget.org
            // download; nothing downloads without it. Runs BEFORE the code
            // requirement so {"setup_roslyn":true} alone is a valid request.
            if (JsonBody.GetBool(body, "setup_roslyn"))
            {
                var setupResult = HandleSetupRoslyn(hasCode: !string.IsNullOrEmpty(code));
                if (setupResult != null)
                    return setupResult;
                // null → Roslyn is ready and the caller also sent code:
                // fall through and compile it in the same call.
            }

            if (string.IsNullOrEmpty(code))
                return ToolDispatchResult.Fail("validation_error",
                    "Field 'code' is required and must be non-empty");

            // M14 T5.2 — deny heuristic runs before compile. The bypass contract
            // (gate: "off" + confirm_bypass: true) is evaluated from the request
            // body so the heuristic fires even before the dispatcher has resolved
            // the effective gate mode. The dispatcher also records the bypass in
            // the audit log via the gate envelope.
            var bypass = BridgeDenyBypass.IsRequestedFromBody(body);
            var deny = BridgeDenyList.EvaluateCSharp(
                code, BridgeProjectSettings.CSharpDenyPatterns, bypass);
            if (!deny.Allowed)
            {
                return ToolDispatchResult.Fail("denied_by_policy",
                    $"{deny.Reason} Suggestion: {deny.Suggestion} " +
                    $"Matched pattern: {deny.MatchedPattern}.");
            }

            var extraUsings = JsonBody.GetStringArray(body, "usings");
            var allUsings = DefaultUsings
                .Concat(extraUsings ?? Array.Empty<string>())
                .Distinct()
                .ToArray();

            // Resolve object_ids to live objects before compiling so the snippet
            // can access them via Refs[index] or Ref<T>(index).
            var objectIdStrings = JsonBody.GetStringArray(body, "object_ids");
            UnityEngine.Object[] resolvedRefs = null;
            if (objectIdStrings != null && objectIdStrings.Length > 0)
            {
                resolvedRefs = new UnityEngine.Object[objectIdStrings.Length];
                for (var i = 0; i < objectIdStrings.Length; i++)
                {
                    var idStr = objectIdStrings[i];
                    if (string.IsNullOrEmpty(idStr)) continue;

                    // Accept bare integers (long-backed via InstanceId.Parse so
                    // IDs > int.MaxValue resolve on Unity 6000.5+, where the
                    // 8-byte EntityId no longer fits in an int) or full handle
                    // JSON. ResolveJson already uses the long path internally.
                    var resolved = ObjectHandle.ResolveJson(idStr, out _);
                    resolvedRefs[i] = resolved;
                }
            }

            if (!RoslynHost.Initialize())
            {
                // A completed install that Initialize hasn't seen (install
                // finished in another window / just now) is picked up here
                // rather than telling the agent to set up again.
                var installing = RoslynFallback.RoslynFallbackInstaller.Status;
                if (installing.State == RoslynFallback.RoslynFallbackInstaller.InstallState.Installing)
                    return ToolDispatchResult.Ok(BuildSetupStatusJson("installing", installing));

                return ToolDispatchResult.Fail("roslyn_unavailable", RoslynHost.UnavailableHint);
            }

            var source = BuildSource(code, allUsings);

            // fd-exhaustion fix — source-keyed snippet cache: a snippet this
            // session already compiled skips Roslyn (and the metadata-reference
            // path) entirely and reuses its loaded assembly.
            var sourceKey = ComputeSourceKey(source);
            var cachedAssembly = TryGetCachedSnippetAssembly(sourceKey);

            byte[] pe = null;
            if (cachedAssembly == null)
            {
                string errors;
                (pe, errors) = RoslynHost.Compile(source);
                if (pe == null)
                    return ToolDispatchResult.Fail("compilation_error",
                        AppendAccessibilityHint(errors ?? "Unknown compilation error"));
            }

            try
            {
                var assembly = cachedAssembly ?? LoadSnippetAssembly(sourceKey, pe);
                var type = assembly.GetType("UnityOpenMcpSnippet.Snippet");
                if (type == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet type not found");

                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet entry point not found");

                // Inject resolved object references so the snippet can access live objects.
                // B39 — always resolve the Refs field, even when this call has no
                // object_ids. The compiled snippet assembly is REUSED when the source
                // is identical (the source-keyed cache above), so a previous call that
                // DID pass object_ids leaves its Refs array on the static field.
                // Re-running the same snippet without object_ids would then hand it the
                // previous call's (possibly destroyed) objects. Explicitly null the
                // field when no refs were resolved this call so the reuse path is clean.
                var refsField = type.GetField("Refs", BindingFlags.Public | BindingFlags.Static);
                if (refsField != null)
                    refsField.SetValue(null, resolvedRefs);

                var result = method.Invoke(null, null);
                var output = OutputSerializer.Serialize(result, BuildSerializeOptions(body));

                // Defense-in-depth: OutputSerializer is per-member defensive
                // (each field/property access is try/catch'd), but an exception
                // can still escape mid-walk — e.g. a TypeLoadException when a
                // field references a missing assembly — leaving truncated /
                // unbalanced JSON. BuildGateEnvelope interpolates that output
                // raw into the gate envelope at `result.Mutation.Output`,
                // corrupting the whole response body; the MCP server's JSON
                // parser then rejects it (and without the matching server-side
                // guard, silently degrades to a fake success — see
                // specs/feedback.md entry 2026-07-03-c).
                //
                // Validate the serialized output is a balanced JSON object
                // before trusting it. A null output is LEGITIMATE and common
                // (the default `return null;` snippet tail) — the envelope
                // emits `"output":null`, which is valid JSON — so only a
                // non-null output that fails validation is treated as malformed.
                // Surface a structured execution_error built from primitives
                // (return type + likely cause, no object-graph walk) so the
                // mutation block is always well formed.
                if (output != null && !BridgeJson.IsValidJsonObject(output))
                {
                    var diag = result == null
                        ? "snippet returned null but serialization produced non-object JSON"
                        : $"snippet return type {result.GetType().FullName}: serialization produced malformed JSON (likely an exception during the reflective walk — e.g. a TypeLoadException on a field referencing a missing assembly). The result could not be safely serialized.";
                    return ToolDispatchResult.Fail("execution_error", diag);
                }

                return ToolDispatchResult.Ok(output);
            }
            catch (TargetInvocationException tie)
            {
                return ToolDispatchResult.Fail("execution_error", tie.InnerException?.Message ?? tie.Message);
            }
            catch (Exception e)
            {
                return ToolDispatchResult.Fail("execution_error", e.Message);
            }
        }

        /// <summary>
        /// setup_roslyn handling. Returns the response to send, or null when
        /// Roslyn is ready AND the caller also passed code (fall through to
        /// compile). Status envelope states: already_available / installing
        /// (re-call to poll) / installed; failures use roslyn_install_failed.
        /// </summary>
        private static ToolDispatchResult HandleSetupRoslyn(bool hasCode)
        {
            if (RoslynHost.Initialize())
                return hasCode ? null
                    : ToolDispatchResult.Ok("{\"status\":\"already_available\"}");

            var status = RoslynFallback.RoslynFallbackInstaller.Status;
            switch (status.State)
            {
                case RoslynFallback.RoslynFallbackInstaller.InstallState.Installing:
                    return ToolDispatchResult.Ok(BuildSetupStatusJson("installing", status));

                case RoslynFallback.RoslynFallbackInstaller.InstallState.Failed:
                    // Surface the error once, then clear it so the next
                    // setup_roslyn call can retry the install.
                    RoslynFallback.RoslynFallbackInstaller.ResetFailure();
                    return ToolDispatchResult.Fail("roslyn_install_failed",
                        "Roslyn fallback install failed: " + (status.Error ?? "unknown error") +
                        ". Re-call execute_csharp with {\"setup_roslyn\":true} to retry, " +
                        "or see docs/troubleshooting.md for the manual offline install.");

                default:
                    // Idle (or Installed but not yet loaded): a valid on-disk
                    // install just needs a re-probe; otherwise start one.
                    if (RoslynFallback.RoslynFallbackInstaller.IsInstalled)
                    {
                        RoslynHost.Reinitialize();
                        if (RoslynHost.IsAvailable)
                            return hasCode ? null
                                : ToolDispatchResult.Ok("{\"status\":\"installed\",\"roslynLoaded\":true}");
                        return ToolDispatchResult.Fail("roslyn_install_failed",
                            "Roslyn fallback is installed but failed to load: " +
                            (RoslynHost.LastInitError ?? "unknown error"));
                    }

                    RoslynFallback.RoslynFallbackInstaller.StartInstall();
                    return ToolDispatchResult.Ok(BuildSetupStatusJson(
                        "installing", RoslynFallback.RoslynFallbackInstaller.Status));
            }
        }

        private static string BuildSetupStatusJson(
            string status, RoslynFallback.RoslynFallbackInstaller.InstallStatus s)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"status\":").Append(BridgeJson.EscapeString(status));
            sb.Append(",\"progress\":").Append(
                s.Progress.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.Step))
                sb.Append(",\"step\":").Append(BridgeJson.EscapeString(s.Step));
            sb.Append(",\"hint\":\"re-call execute_csharp with setup_roslyn:true to poll until status is installed\"}");
            return sb.ToString();
        }

        private static SerializeOptions BuildSerializeOptions(string body)
        {
            var maxDepth = JsonBody.GetInt(body, "max_depth", 4);
            var maxItems = JsonBody.GetInt(body, "max_items", 100);
            return new SerializeOptions
            {
                MaxDepth = maxDepth <= 0 ? 4 : maxDepth,
                MaxListItems = maxItems <= 0 ? 100 : maxItems,
            };
        }

        // SHA256 of the generated compilation unit, lowercase hex — the
        // snippet-cache key.
        // B40 — SHA256.Create() returns an IDisposable hash algorithm (a
        // native crypto provider handle). Wrap in `using` so a throw from
        // ComputeHash cannot leak it across calls.
        private static string ComputeSourceKey(string source)
        {
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source));
            }
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        private static Assembly TryGetCachedSnippetAssembly(string sourceKey)
        {
            lock (s_snippetLock)
            {
                return s_snippetAssemblies.TryGetValue(sourceKey, out var cached) ? cached : null;
            }
        }

        // Load a freshly compiled snippet PE and cache it under its source key.
        // Double-checks under the lock so a hypothetical concurrent identical
        // call cannot load twice — execute_csharp runs on the main thread
        // today, but the guard is cheap insurance against future call sites.
        private static Assembly LoadSnippetAssembly(string sourceKey, byte[] pe)
        {
            lock (s_snippetLock)
            {
                if (s_snippetAssemblies.TryGetValue(sourceKey, out var existing))
                    return existing;

                var assembly = Assembly.Load(pe);
                s_snippetAssemblies[sourceKey] = assembly;
                return assembly;
            }
        }

        // Test seam: number of snippet assemblies cached this domain.
        internal static int CachedSnippetAssemblyCount
        {
            get { lock (s_snippetLock) { return s_snippetAssemblies.Count; } }
        }

        // specs/feedback.md 2026-08-14 — a snippet compiles into its OWN assembly
        // (UnityOpenMcpSnippet), so it sees only the `public` surface of the
        // project's assemblies. `internal` is the natural visibility for a
        // testable seam, and the project marks several helpers internal precisely
        // so tests can reach them — an agent verifying one of those hits this
        // routinely and reads the diagnostic as "the member does not exist".
        //
        // The diagnostics that show up are CS0117 ("does not contain a definition
        // for"), CS0122 ("inaccessible due to its protection level") and CS1061
        // ("no definition for ... and no accessible extension method"). All three
        // are ambiguous between a genuine typo and the assembly boundary, so name
        // the boundary and give the recipe that works today. (Compiling the
        // snippet WITH internals visibility would need Roslyn's IgnoreAccessibility
        // binder flag plus IgnoresAccessChecksTo honored by the editor's Mono
        // runtime; that is tracked separately — it cannot be verified from the
        // server side and a silent MethodAccessException at Invoke time would be
        // worse than this diagnostic.)
        private static readonly string[] AccessibilityDiagnosticCodes =
            { "CS0117", "CS0122", "CS1061" };

        internal static string AppendAccessibilityHint(string errors)
        {
            if (string.IsNullOrEmpty(errors)) return errors;
            var relevant = false;
            foreach (var code in AccessibilityDiagnosticCodes)
            {
                if (errors.IndexOf(code, StringComparison.Ordinal) >= 0) { relevant = true; break; }
            }
            if (!relevant) return errors;

            return errors + "\n\nNOTE — assembly boundary: this snippet compiles into its own assembly "
                + "(UnityOpenMcpSnippet), so it can only see the PUBLIC members of your project's "
                + "assemblies. If the member above exists but is declared 'internal' (or 'private'), "
                + "that is why it is not found, and the diagnostic is not a typo. Reach it by "
                + "reflection, e.g.:\n"
                + "  var m = typeof(YourType).GetMethod(\"YourMember\", "
                + "System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);\n"
                + "  return m.Invoke(null, new object[] { /* args */ });\n"
                + "(use BindingFlags.Instance | BindingFlags.NonPublic and pass the instance for "
                + "non-static members). Reflection loses compile-time checking, so prefer making the "
                + "seam public — or exercise it from a test assembly with InternalsVisibleTo via "
                + "unity_senses_run_tests — when you will call it more than once.";
        }

        // internal so the EditMode suite can pin the B39 source-level contract
        // (Refs declared as a public static field) without a live Roslyn install.
        //
        // feedback.md issue 1 — two fixes folded in:
        //   (a) Hoist leading `using X;` directives out of the body into the
        //       file-scope directive list. A snippet that opens with `using
        //       UnityEditor;` previously landed inside Run() and was parsed as a
        //       using STATEMENT (CS0210/CS0118), not a directive. Distinguish a
        //       directive from a using-statement by the absence of `(` before
        //       the `;`. Only LEADING using lines are hoisted — a `using` after
        //       the first real statement is a genuine using-statement and errors
        //       normally (correctly).
        //   (b) Emit `#line <N> "snippet"` immediately before the body and
        //       `#line default` after, so Roslyn reports (line,col) in the
        //       caller's snippet coordinates, not wrapper-relative. `<N>` is the
        //       1-based index of the first retained body line in the caller's
        //       source — i.e. it accounts for the using/blank lines the hoist
        //       stripped, so an error on the caller's first real statement
        //       reports at the caller's own line number, not line 1. (feedback S1)
        internal static string BuildSource(string code, string[] usings)
        {
            // (a) Hoist leading using directives. Returns the cleaned body and the
            // 1-based source line of its first retained line (for the #line map).
            var hoistedUsings = HoistLeadingUsings(code, out var cleanedBody, out var firstRetainedLine);

            // Merge: caller usings + hoisted, deduped (case-insensitive on the
            // namespace token), preserving first-seen order.
            var allUsings = new List<string>(usings.Length + hoistedUsings.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in usings)
                if (seen.Add(u)) allUsings.Add(u);
            foreach (var u in hoistedUsings)
                if (seen.Add(u)) allUsings.Add(u);

            var sb = new StringBuilder(cleanedBody.Length + allUsings.Count * 30 + 320);
            foreach (var u in allUsings)
                sb.AppendLine($"using {u};");
            sb.AppendLine();
            sb.AppendLine("namespace UnityOpenMcpSnippet {");
            sb.AppendLine("  public static class Snippet {");
            // Live object references injected from the object_ids parameter.
            // Access via Refs[i] or Ref<T>(i) in the snippet body.
            sb.AppendLine("    public static UnityEngine.Object[] Refs;");
            sb.AppendLine("    public static T Ref<T>(int index) where T : UnityEngine.Object {");
            sb.AppendLine("      if (Refs == null || index < 0 || index >= Refs.Length) return null;");
            sb.AppendLine("      return Refs[index] as T;");
            sb.AppendLine("    }");
            sb.AppendLine("    public static object Run() {");
            // (b) #line directive so compiler errors report snippet-relative
            // coordinates. `firstRetainedLine` is the caller's 1-based line for
            // the first line of cleanedBody, so the hoisted using/blank lines do
            // not shift reported errors toward 1. A `#line hidden` region after
            // the body keeps the wrapper tail (return null; + closing braces) out
            // of stack traces / the debugger; `"snippet"` names the body so a
            // debugger / stack trace shows "snippet", not the wrapper class.
            sb.AppendLine($"#line {firstRetainedLine} \"snippet\"");
            sb.AppendLine(cleanedBody);
            sb.AppendLine("#line hidden");
            sb.AppendLine("      return null;");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("#line default");
            return sb.ToString();
        }

        // Pull leading `using <ns>;` lines out of the body. A using DIRECTIVE has
        // no `(` between `using` and `;`; a using STATEMENT is `using ( ... )` or
        // `using var x = ...`. Only directives at the very top of the snippet
        // (before any other statement) are hoisted — matches what the caller
        // meant. Returns the namespace tokens (without the `using`/`;`), sets
        // `cleaned` to the body with those lines stripped, and `firstRetainedLine`
        // to the 1-based index of the first retained line in the caller's source
        // (so the #line directive in BuildSource maps errors to caller coordinates).
        private static List<string> HoistLeadingUsings(string code, out string cleaned, out int firstRetainedLine)
        {
            var hoisted = new List<string>();
            if (string.IsNullOrEmpty(code)) { cleaned = code ?? ""; firstRetainedLine = 1; return hoisted; }

            var lines = code.Replace("\r\n", "\n").Split('\n');
            int i = 0;
            for (; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue; // blank lines between usings are OK

                // `using <ns>;` directive — no `(` before the `;`.
                if (trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.EndsWith(";"))
                {
                    var inner = trimmed.Substring(6, trimmed.Length - 7).Trim(); // between "using " and ";"
                    // Reject using-statements: `using (...)` (contains `(`) and
                    // `using var x = ...` (a declaration). Everything else with a
                    // directive-shaped inner is hoisted and re-emitted verbatim as
                    // `using <inner>;`, which covers namespaces (`NS`), aliases
                    // (`Foo = Bar.Baz`), global qualifiers (`global::NS`), and
                    // `using static System.Math`.
                    if (inner.IndexOf('(') >= 0) break;
                    if (inner.StartsWith("var ", StringComparison.Ordinal)) break;
                    if (inner.Length == 0) break;
                    // A directive inner is namespace/type tokens plus the chars the
                    // alias / global / static forms need (`=`, `:`, space).
                    bool valid = true;
                    foreach (var c in inner)
                    {
                        if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_'
                              || c == '=' || c == ':' || c == ' '))
                        {
                            valid = false;
                            break;
                        }
                    }
                    if (!valid) break;
                    hoisted.Add(inner);
                }
                else
                {
                    break; // first non-using, non-blank line ends hoisting
                }
            }

            if (hoisted.Count == 0) { cleaned = code; firstRetainedLine = 1; return hoisted; }

            // Rebuild the body without the hoisted leading lines (keep trailing
            // blank lines that preceded the first real statement). Track the
            // 1-based index of the first retained line so the #line directive
            // reports caller-relative coordinates. (feedback S1)
            var rest = new StringBuilder(code.Length);
            bool seenReal = false;
            firstRetainedLine = 1;
            for (int j = i; j < lines.Length; j++)
            {
                if (!seenReal && lines[j].Trim().Length == 0) continue; // drop leading blanks
                if (!seenReal)
                {
                    seenReal = true;
                    firstRetainedLine = j + 1; // 1-based index of the first retained line
                }
                if (rest.Length > 0) rest.Append('\n');
                rest.Append(lines[j]);
            }
            cleaned = rest.ToString();
            return hoisted;
        }
    }
}
