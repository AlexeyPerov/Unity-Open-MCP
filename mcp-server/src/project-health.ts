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
//   - Package install failures — UPM's untagged "One or more packages could
//     not be added to the local file system:" block, one indented
//     `<package>: <reason>` line per package (ENOSPC on a full disk, a tar or
//     network error). The missing package then surfaces as CS0234/CS0246 in a
//     package that depends on it, so this is the root cause to report first.
//   - Editor file-descriptor exhaustion — `System.NotSupportedException: Could
//     not register to wait for file descriptor <N>` thrown from the Bee build
//     driver's named-pipe IPC. This is a Mono runtime / kqueue failure: after
//     many domain reloads in a long session the Editor leaks file-descriptor
//     numbers until one crosses Mono's internal ~1024 ceiling, and Mono refuses
//     to register it. The Editor then hangs mid-build and never recovers on its
//     own — restarting Unity is the only reliable fix. There are NO C# compile
//     errors to fix; surfacing this distinct kind stops an agent from chasing a
//     phantom CSxxxx problem and points it (and the operator) at a restart.
//
// The output is a flat list of HealthIssue records, each tagged with a
// `kind` so an agent can branch on it ("assembly_resolution → check package
// versions", "package_deprecated → plan an upgrade",
// "editor_fd_exhaustion → restart the Editor"). The list is bounded so
// a giant wall of log lines cannot blow up the tool response.

import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { extractStructuredCompilerErrors, type CompilerError } from "./compiler-errors.js";

/** Maximum number of non-compiler health issues we surface. */
export const MAX_HEALTH_ISSUES = 50;

export type HealthIssueKind =
  | "assembly_resolution"
  | "package_deprecated"
  | "package_manager_error"
  | "package_install_failed"
  | "editor_fd_exhaustion";

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
  /** package_install_failed only: the package that could not be installed. */
  package?: string;
  /** package_install_failed only: UPM's reason, verbatim (e.g. "TAR_ENTRY_ERROR: ENOSPC: ..."). */
  reason?: string;
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

// ---------------------------------------------------------------------------
// Editor file-descriptor exhaustion (Bee build driver / Mono IOSelector).
//
// After many domain reloads in a long session, the Editor's file-descriptor
// numbers climb until one crosses Mono's internal ~1024 ceiling, and Mono's
// IOSelector (kqueue/kevent on macOS/BSD) refuses to register it. The
// exception is thrown from the Bee build driver's named-pipe IPC and the
// Editor hangs mid-build. Unity prints the full stack to Editor.log:
//
//   Unhandled exception during build: System.NotSupportedException: Could not
//   register to wait for file descriptor 1194
//     at System.IOSelector.Add(...)
//     ... at System.IO.Pipes.NamedPipeServerStream.<WaitForConnectionAsync>...
//     ... at Bee.BeeDriver.BeeDriver_RunBackend.ReadEntireBinlogFromIpcAsync...
//
// We match on the signature line only (the number varies) — the surrounding
// stack is noise. This is a hard signal: the Editor cannot recover without a
// restart, and there is no C# error to fix.
// ---------------------------------------------------------------------------

const FD_EXHAUSTION_RE =
  /System\.NotSupportedException:\s*Could not register to wait for file descriptor \d+/g;

// ---------------------------------------------------------------------------
// Package install failures. UPM writes, with no `[Package Manager]` prefix:
//
//   An error occurred while resolving packages:
//     One or more packages could not be added to the local file system:
//       com.unity.burst: TAR_ENTRY_ERROR: ENOSPC: no space left on device, write
//       com.unity.shadergraph: TAR_ENTRY_ERROR: ENOSPC: no space left on device, write
//
// The header is matched anywhere; every following line that is a
// `<package id>: <reason>` pair belongs to it.
// ---------------------------------------------------------------------------

const PACKAGE_INSTALL_HEADER_RE =
  /One or more packages could not be added to the local file system:[^\n]*\n/g;

const PACKAGE_INSTALL_LINE_RE = /^\s+([a-z0-9][\w.-]*(?:@[^\s:]+)?):\s*(\S[^\n]*)$/i;

/** Remediation for one failed package install, keyed off UPM's reason text. */
export function packageInstallHint(pkg: string, reason: string): string {
  if (/ENOSPC|no space left/i.test(reason)) {
    return `${pkg} could not be unpacked because the disk is full. Free disk space, then reopen the ` +
      "project (or run unity_open_mcp_compile_check headless) so the Package Manager re-resolves it. " +
      "The CS0234/CS0246 errors in packages that depend on it are a consequence, not code to fix.";
  }
  if (/ECONN|ETIMEDOUT|ENOTFOUND|EAI_AGAIN|network|socket|proxy|certificate|\b(?:401|403|404|5\d\d)\b/i.test(reason)) {
    return `${pkg} could not be downloaded (network or registry error). Check the network, proxy and ` +
      "scoped-registry settings, then reopen the project so the Package Manager retries.";
  }
  return `${pkg} could not be added to Library/PackageCache. Delete Library/PackageCache/${pkg.split("@")[0]}@* ` +
    "if it exists, then reopen the project so the Package Manager re-installs it.";
}

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

  // Editor fd-exhaustion is scanned FIRST. It is the most severe signal (the
  // Editor is hung, not just unhealthy) and its remediation differs from every
  // other kind — so it must not be dropped by the MAX_HEALTH_ISSUES cap when a
  // noisy log tail has dozens of milder package notices ahead of it.
  FD_EXHAUSTION_RE.lastIndex = 0;
  if (FD_EXHAUSTION_RE.exec(log) !== null) {
    const summary = "Editor file-descriptor exhaustion (build driver hang)";
    seen.add(summary);
    issues.push({
      kind: "editor_fd_exhaustion",
      summary,
      raw: "System.NotSupportedException: Could not register to wait for file descriptor",
      hint:
        "The Unity Editor ran out of file descriptors (an internal Mono/Unity " +
        "limit hit during the Bee build after many domain reloads in a long " +
        "session). This is NOT a C# compile error — there is nothing to fix in " +
        "your source. The Editor is hung mid-build and will not recover on its " +
        "own: the operator must quit and relaunch Unity for this project " +
        "(save scene work first if the Editor UI is still responsive, then " +
        "restart). After relaunch the bridge reconnects automatically.",
    });
  }

  let m: RegExpExecArray | null;

  // Package install failures come before every other package notice: the
  // missing package is the root cause of the compile errors around it.
  PACKAGE_INSTALL_HEADER_RE.lastIndex = 0;
  while ((m = PACKAGE_INSTALL_HEADER_RE.exec(log)) !== null) {
    const lines = log.slice(m.index + m[0].length).split("\n");
    for (const line of lines) {
      const entry = PACKAGE_INSTALL_LINE_RE.exec(line.replace(/\r$/, ""));
      if (!entry) break;
      const pkg = entry[1];
      const reason = entry[2].trim();
      const summary = `Package install failed: ${pkg}`;
      if (seen.has(summary)) continue;
      seen.add(summary);
      issues.push({
        kind: "package_install_failed",
        summary,
        raw: line.trim(),
        package: pkg,
        reason,
        hint: packageInstallHint(pkg, reason),
      });
      if (issues.length >= MAX_HEALTH_ISSUES) return issues;
    }
  }

  // Assembly resolution failures.
  ASSEMBLY_RESOLUTION_RE.lastIndex = 0;
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

/** Lock-file sources UPM installs into Library/PackageCache. */
const CACHED_PACKAGE_SOURCES = new Set(["registry", "git"]);

/**
 * Cross-check Packages/packages-lock.json against Library/PackageCache: a
 * registry or git package with no `Library/PackageCache/<name>@*` folder was
 * never installed (a failed unpack whose log lines rotated away, a cache
 * cleared by hand). Skipped when the cache folder does not exist or holds no
 * package folder
 * (the project was never opened, or the Library was deleted), and fails open
 * to `[]` on any read or parse error.
 */
export function detectMissingCachedPackages(projectPath: string | undefined | null): HealthIssue[] {
  if (!projectPath) return [];
  try {
    const cacheDir = join(projectPath, "Library", "PackageCache");
    const lockPath = join(projectPath, "Packages", "packages-lock.json");
    if (!existsSync(cacheDir) || !existsSync(lockPath)) return [];
    // Package folders are `<name>@<version or hash>`; anything else (.DS_Store,
    // a stray file) says nothing about what is installed.
    const cached = readdirSync(cacheDir).filter((entry) => entry.includes("@"));
    if (cached.length === 0) return [];
    const cachedNames = new Set(cached.map((entry) => entry.split("@")[0]));
    const lock = JSON.parse(readFileSync(lockPath, "utf-8")) as { dependencies?: Record<string, { source?: unknown }> };
    const issues: HealthIssue[] = [];
    for (const [name, info] of Object.entries(lock.dependencies ?? {})) {
      if (!CACHED_PACKAGE_SOURCES.has(String(info?.source)) || cachedNames.has(name)) continue;
      const reason = "listed in Packages/packages-lock.json but missing from Library/PackageCache";
      issues.push({
        kind: "package_install_failed",
        summary: `Package install failed: ${name}`,
        raw: `${name}: ${reason}`,
        package: name,
        reason,
        hint: packageInstallHint(name, reason),
      });
      if (issues.length >= MAX_HEALTH_ISSUES) break;
    }
    return issues;
  } catch {
    return [];
  }
}

/**
 * Merge the PackageCache cross-check into log-derived issues: one issue per
 * package, the log's (more specific) reason wins.
 */
export function mergePackageCacheIssues(issues: HealthIssue[], cacheIssues: HealthIssue[]): HealthIssue[] {
  if (!cacheIssues.length) return issues;
  const known = new Set(issues.filter((i) => i.kind === "package_install_failed").map((i) => (i.package ?? "").split("@")[0]));
  const extra = cacheIssues.filter((i) => !known.has(i.package ?? ""));
  return extra.length ? [...extra, ...issues].slice(0, MAX_HEALTH_ISSUES) : issues;
}

/**
 * Build the full ProjectHealth summary from a log tail. Combines the CSxxxx
 * compiler errors with the package/assembly health issues and produces a
 * single aggregate verdict + headline. Pure over the log; with `projectPath`
 * it also reads Packages/packages-lock.json and lists Library/PackageCache
 * (detectMissingCachedPackages). `compilerErrors` replaces the log's own
 * extraction when the caller already separated current from historical errors.
 */
export function summarizeProjectHealth(
  log: string,
  projectPath?: string | null,
  compilerErrors: CompilerError[] = extractStructuredCompilerErrors(log),
): ProjectHealth {
  const issues = mergePackageCacheIssues(extractProjectHealthIssues(log), detectMissingCachedPackages(projectPath));
  const unhealthy = compilerErrors.length > 0 || issues.length > 0;

  let headline = "";
  const failedInstalls = issues.filter((i) => i.kind === "package_install_failed");
  if (failedInstalls.length > 0) {
    const names = failedInstalls.map((i) => i.package).join(", ");
    const diskFull = failedInstalls.some((i) => /ENOSPC|no space left/i.test(i.reason ?? ""));
    headline =
      `${failedInstalls.length} package(s) could not be installed (${names})` +
      (diskFull ? " — the disk is full" : "") +
      ". Fix the install first" +
      (compilerErrors.length > 0
        ? `; of the ${compilerErrors.length} compiler error(s), those in packages that depend on them are a consequence.`
        : ".");
  } else if (unhealthy) {
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
      // Only package/assembly issues. Pick the headline by the worst kind.
      // Editor fd-exhaustion is checked first — it is a hard, non-code failure
      // (the Editor is hung) whose fix (restart Unity) is different from every
      // other kind, so it must not be masked by a "packages need attention"
      // headline when it co-occurs with milder package notices.
      const hasFdExhaustion = issues.some(
        (i) => i.kind === "editor_fd_exhaustion",
      );
      const hasAsmRes = issues.some((i) => i.kind === "assembly_resolution");
      if (hasFdExhaustion) {
        headline =
          "Editor hung on file-descriptor exhaustion (internal Unity/Mono " +
          "limit hit during the build driver), NOT a compile error — there " +
          "is no C# to fix. Restart Unity to recover; the bridge reconnects " +
          "on relaunch.";
      } else if (hasAsmRes) {
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
