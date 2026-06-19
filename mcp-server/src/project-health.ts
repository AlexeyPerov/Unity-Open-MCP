// Project-health extraction from a Unity Editor.log tail.
//
// Sits next to compiler-errors.ts and shares its "parse Unity's plain-text log
// output" philosophy. Where compiler-errors.ts focuses on CSxxxx diagnostics,
// this module surfaces the package- and assembly-level red flags that the
// editor writes to the SAME log file but that do NOT match the CSxxxx shape:
//
//   - AssemblyResolutionException — e.g. when a package's compiled source
//     references an assembly that the installed version does not provide
//     (the classic ProBuilder-5.x-on-Unity-6 case: Burst's Cecil pass cannot
//     resolve Unity.ProBuilder.AddOns.Editor because ProBuilder 6 dropped it).
//     These come back as Mono.Cecil error stacks.
//   - Package deprecation notices — `[Package Manager] <id> is deprecated`.
//     Informational but worth surfacing because deprecated packages stop
//     receiving updates and often precede a forced upgrade.
//   - Package Manager errors / conflicts — anything tagged `[Package Manager]`
//     that contains "error", "conflict", or "cannot resolve".
//
// The output is a flat list of HealthIssue records, each tagged with a
// `kind` so an agent can branch on it ("assembly_resolution → check package
// versions", "package_deprecated → plan an upgrade"). The list is bounded so
// a giant wall of log lines cannot blow up the tool response.

import { extractStructuredCompilerErrors, type CompilerError } from "./compiler-errors.js";

/** Maximum number of non-compiler health issues we surface. */
export const MAX_HEALTH_ISSUES = 50;

export type HealthIssueKind =
  | "assembly_resolution"
  | "package_deprecated"
  | "package_manager_error";

export interface HealthIssue {
  /** Discriminator for branching in the agent. */
  kind: HealthIssueKind;
  /**
   * Short human-readable headline. For assembly_resolution this is the
   * unresolved assembly name; for package issues it is the package id (when
   * parseable).
   */
  summary: string;
  /**
   * The first raw log line that matched, included verbatim so the agent can
   * quote it. Bounded to one line — the surrounding stack trace is noise.
   */
  raw: string;
  /** Optional hint pointing at the most likely remediation. */
  hint?: string;
}

export interface ProjectHealth {
  /** Structured CSxxxx diagnostics (same shape as read_compile_errors). */
  compilerErrors: CompilerError[];
  /** Package- and assembly-level red flags from the same log tail. */
  issues: HealthIssue[];
  /**
   * Aggregate flag: true when anything in compilerErrors OR issues indicates
   * the project is not in a clean state. Agents check this first.
   */
  unhealthy: boolean;
  /**
   * One-line headline summarizing the worst signal, for agents that want a
   * quick triage without scanning the lists. Empty when unhealthy is false.
   */
  headline: string;
}

// ---------------------------------------------------------------------------
// Assembly resolution failures.
//
// Unity (via Burst's Cecil pass) emits a stack like:
//
//   Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly:
//   'Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0, Culture=neutral,
//   PublicKeyToken=null' ---> System.Exception: Failed to resolve assembly
//   'Unity.ProBuilder.AddOns.Editor, ...' in directories: ...
//
// We capture the assembly NAME (the simple name before the first comma) so an
// agent can correlate it to a package. The first comma is the version
// separator per the .NET assembly-name format.
// ---------------------------------------------------------------------------

const ASSEMBLY_RESOLUTION_RE =
  /Mono\.Cecil\.AssemblyResolutionException:\s*Failed to resolve assembly:\s*'([^']+)'/g;

// ---------------------------------------------------------------------------
// Package Manager notices. Two shapes:
//
//   [Package Manager] com.unity.ide.vscode is deprecated: ...
//   [Package Manager] <anything> error <...>
//   [Package Manager] <anything> conflict <...>
//
// The prefix tag is the same in both cases.
// ---------------------------------------------------------------------------

const PACKAGE_DEPRECATED_RE =
  /\[Package Manager\]\s+([\w.]+)\s+is deprecated:\s*([^\n]+)/g;

const PACKAGE_MANAGER_ERROR_RE =
  /\[Package Manager\][^\n]*(?:error|conflict|cannot resolve|incompatible)[^\n]*/gi;

/** Parse the simple assembly name out of a full assembly identity string. */
function simpleAssemblyName(identity: string): string {
  // "Unity.ProBuilder.AddOns.Editor, Version=0.0.0.0, ..." -> "Unity.ProBuilder.AddOns.Editor"
  const comma = identity.indexOf(",");
  return (comma >= 0 ? identity.slice(0, comma) : identity).trim();
}

/**
 * Extract package- and assembly-level health issues from a Unity Editor.log
 * tail. Pure function — no I/O. The caller (routeReadCompileErrors) reads the
 * log and hands the contents in.
 *
 * Dedupes by the headline summary, first-seen order, capped at
 * MAX_HEALTH_ISSUES.
 */
export function extractProjectHealthIssues(log: string): HealthIssue[] {
  if (!log) return [];
  const seen = new Set<string>();
  const issues: HealthIssue[] = [];

  // Assembly resolution failures.
  ASSEMBLY_RESOLUTION_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = ASSEMBLY_RESOLUTION_RE.exec(log)) !== null) {
    const identity = m[1];
    const name = simpleAssemblyName(identity);
    const summary = `Unresolved assembly: ${name}`;
    if (seen.has(summary)) continue;
    seen.add(summary);
    issues.push({
      kind: "assembly_resolution",
      summary,
      raw: m[0].trim(),
      hint:
        `An installed package compiled against '${name}' but the installed ` +
        "version does not provide it. This is a classic package-version / " +
        "Unity-version mismatch (e.g. a package built for an older Unity). " +
        "Check Packages/manifest.json for an outdated package that this " +
        "assembly belongs to and bump it to a version verified for the " +
        "current Editor.",
    });
    if (issues.length >= MAX_HEALTH_ISSUES) return issues;
  }

  // Package deprecation notices.
  PACKAGE_DEPRECATED_RE.lastIndex = 0;
  while ((m = PACKAGE_DEPRECATED_RE.exec(log)) !== null) {
    const pkgId = m[1];
    const detail = (m[2] ?? "").trim();
    const summary = `Deprecated package: ${pkgId}`;
    if (seen.has(summary)) continue;
    seen.add(summary);
    issues.push({
      kind: "package_deprecated",
      summary,
      raw: m[0].trim(),
      hint:
        `${pkgId} is deprecated (${detail}). Deprecated packages stop ` +
        "receiving updates and often stop compiling after a Unity upgrade. " +
        "Plan a replacement or removal from Packages/manifest.json.",
    });
    if (issues.length >= MAX_HEALTH_ISSUES) return issues;
  }

  // Package Manager errors / conflicts (catch-all after the deprecation pass).
  PACKAGE_MANAGER_ERROR_RE.lastIndex = 0;
  while ((m = PACKAGE_MANAGER_ERROR_RE.exec(log)) !== null) {
    const raw = m[0].trim();
    const summary = raw.length > 120 ? raw.slice(0, 117) + "..." : raw;
    if (seen.has(summary)) continue;
    seen.add(summary);
    issues.push({
      kind: "package_manager_error",
      summary,
      raw,
      hint:
        "The Unity Package Manager reported an error or conflict while " +
        "resolving dependencies. Open the Packages/manifest.json and check " +
        "for conflicting version constraints or packages that are not " +
        "available for the current Unity Editor.",
    });
    if (issues.length >= MAX_HEALTH_ISSUES) return issues;
  }

  return issues;
}

/**
 * Build the full ProjectHealth summary from a log tail. Combines the CSxxxx
 * compiler errors with the package/assembly health issues and produces a
 * single aggregate verdict + headline. Pure function.
 */
export function summarizeProjectHealth(log: string): ProjectHealth {
  const compilerErrors = extractStructuredCompilerErrors(log);
  const issues = extractProjectHealthIssues(log);
  const unhealthy = compilerErrors.length > 0 || issues.length > 0;

  let headline = "";
  if (unhealthy) {
    if (compilerErrors.length > 0 && issues.length > 0) {
      headline =
        `${compilerErrors.length} compiler error(s) and ` +
        `${issues.length} package/assembly issue(s) detected — the bridge ` +
        "will not reload until both are resolved.";
    } else if (compilerErrors.length > 0) {
      headline =
        `${compilerErrors.length} compiler error(s) detected — the bridge ` +
        "will not reload until the C# errors are fixed.";
    } else {
      // Only package/assembly issues. Find the worst kind for the headline.
      const hasAsmRes = issues.some((i) => i.kind === "assembly_resolution");
      if (hasAsmRes) {
        headline =
          `${issues.length} package/assembly issue(s) detected, including ` +
          "unresolved assembly references — a package version is likely " +
          "incompatible with the current Unity Editor. Compilation may fail.";
      } else {
        headline =
          `${issues.length} package manager notice(s) detected — the ` +
          "project may compile but some packages need attention.";
      }
    }
  }

  return { compilerErrors, issues, unhealthy, headline };
}
