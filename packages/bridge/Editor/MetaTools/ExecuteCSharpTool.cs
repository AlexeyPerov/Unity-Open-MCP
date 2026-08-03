using System;
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
        // M30-polish T4.5 — snippet assembly lifecycle. Assembly.Load(byte[])
        // assemblies are tracked by the AppDomain and are NOT unloadable without
        // a collectible AssemblyLoadContext (the full fix, deferred to backlog).
        // Two problems arise from loading a fresh assembly every call:
        //   1. Accumulation — each call grows the AppDomain with a new
        //      UnityOpenMcpSnippet.Snippet assembly.
        //   2. Type-resolution ambiguity — ResolveComponentType /
        //      ObjectHandle.TryResolveType walk AppDomain.GetAssemblies() and
        //      return the FIRST UnityOpenMcpSnippet.Snippet they find, which is
        //      load-order dependent and may be a stale snippet.
        //
        // Minimal mitigation (this plan): keep a single static reference to the
        // most-recently-loaded snippet assembly + its compiled PE hash. When the
        // incoming PE is byte-identical to the last load (the common case — an
        // agent re-running the same snippet), reuse the existing assembly
        // instead of loading a new one, so repeated identical calls do NOT
        // accumulate. When the PE differs, load the new assembly and drop the
        // old static reference. The dropped assembly remains in the AppDomain
        // (Unity limitation) but is no longer reachable via our static handle,
        // and type lookups that prefer s_snippetType resolve the newest snippet.
        // True unload via collectible ALC is tracked in
        // specs/backlog/backlog-packages.md (P2 — Collectible ALC).
        private static Assembly s_snippetAssembly;
        private static byte[] s_snippetPeHash;
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

            var (pe, errors) = RoslynHost.Compile(source);
            if (pe == null)
                return ToolDispatchResult.Fail("compilation_error", errors ?? "Unknown compilation error");

            try
            {
                var assembly = LoadSnippetAssembly(pe);
                var type = assembly.GetType("UnityOpenMcpSnippet.Snippet");
                if (type == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet type not found");

                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    return ToolDispatchResult.Fail("execution_error", "Compiled snippet entry point not found");

                // Inject resolved object references so the snippet can access live objects.
                // B39 — always resolve the Refs field, even when this call has no
                // object_ids. The compiled snippet assembly is REUSED when the PE is
                // byte-identical (LoadSnippetAssembly), so a previous call that DID
                // pass object_ids leaves its Refs array on the static field. Re-running
                // the same snippet without object_ids would then hand it the previous
                // call's (possibly destroyed) objects. Explicitly null the field when no
                // refs were resolved this call so the reuse path is clean.
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

        // Resolve the snippet assembly for this call, reusing the previously
        // loaded assembly when the compiled PE is byte-identical (the common
        // case — an agent re-running the same snippet). This bounds identical-
        // call accumulation to one assembly instead of one-per-call. Distinct
        // snippets still load a new assembly (the old one is dropped from our
        // static handle); true unload requires a collectible AssemblyLoadContext
        // (backlog). Thread-safe via s_snippetLock — execute_csharp runs on the
        // main thread today, but the guard is cheap insurance against future
        // call sites.
        private static Assembly LoadSnippetAssembly(byte[] pe)
        {
            // B40 — SHA256.Create() returns an IDisposable hash algorithm (a
            // native crypto provider handle). Wrap in `using` so a throw from
            // ComputeHash (or the lock body) cannot leak it across calls.
            byte[] hash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                hash = sha.ComputeHash(pe);
            }
            lock (s_snippetLock)
            {
                if (s_snippetAssembly != null && s_snippetPeHash != null && BytesEqual(s_snippetPeHash, hash))
                    return s_snippetAssembly;

                s_snippetAssembly = Assembly.Load(pe);
                s_snippetPeHash = hash;
                return s_snippetAssembly;
            }
        }

        // Byte-for-byte hash comparison. Short-circuits on length mismatch.
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // internal so the EditMode suite can pin the B39 source-level contract
        // (Refs declared as a public static field) without a live Roslyn install.
        internal static string BuildSource(string code, string[] usings)
        {
            var sb = new StringBuilder(code.Length + usings.Length * 30 + 280);
            foreach (var u in usings)
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
            sb.AppendLine($"      {code}");
            sb.AppendLine("      return null;");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
