// Project assembly-definition discovery for read_compile_errors' partial-compile
// signal (specs/feedback.md 2026-08-12). Unity compiles assemblies in dependency
// order; when an early assembly fails the pipeline may not reach dependent ones,
// so the CSxxxx errors in Editor.log can be a SUBSET of the true error set. This
// module counts the project's own compile assemblies and maps an error's source
// file to its owning assembly, so read_compile_errors can surface
// `partialCompileLikely` + `assembliesWithErrors`/`asmdefCount` instead of letting
// an agent treat `errorCount: N` as the complete picture.
//
// Scope: Assets/ + Packages/ + `file:`-referenced local packages from
// manifest.json. Registry packages (Library/PackageCache) are precompiled and
// excluded — they rarely carry compile errors and would inflate the count.
import { readdirSync, readFileSync, existsSync, statSync } from "node:fs";
import { join, dirname, resolve, relative } from "node:path";

const ASMDEF_RE = /\.asmdef$/i;

// Directories never descended into when walking for asmdefs (build output,
// caches, VCS, editor state).
const SKIP_DIRS = new Set([
  "Library",
  "Temp",
 "obj",
  "node_modules",
  ".git",
  "Logs",
  "UserSettings",
  "Build",
  "Builds",
]);

/** Maximum total asmdefs to collect (bounds the walk on huge projects). */
const MAX_ASMDEFS = 500;

/** Maximum parent-directory hops when locating an error file's owning asmdef. */
const MAX_UP_WALK = 32;

export interface AsmdefCount {
  /** Number of distinct compile assemblies in the project's own source. */
  count: number;
  /** A few sample asmdef paths (project-relative) for context/debugging. */
  sample: string[];
}

/**
 * Find the nearest `.asmdef` at or above `filePath`'s directory. Returns the
 * asmdef's absolute path, or null when none is found before the filesystem
 * root / hop limit. Maps any source file (absolute or relative) to its owning
 * assembly so the caller can count how many distinct assemblies a set of errors
 * spans.
 */
export function nearestAsmdef(filePath: string): string | null {
  if (!filePath) return null;
  let dir = dirname(resolve(filePath));
  for (let guard = 0; guard < MAX_UP_WALK; guard++) {
    let entries: string[];
    try {
      entries = readdirSync(dir);
    } catch {
      return null; // unreadable dir — can't walk further reliably
    }
    const asmdef = entries.find((e) => ASMDEF_RE.test(e));
    if (asmdef) return join(dir, asmdef);
    const parent = dirname(dir);
    if (parent === dir) break; // filesystem root
    dir = parent;
  }
  return null;
}

/** Walk a directory tree collecting `.asmdef` paths, skipping build/cache dirs. */
function walkAsmdefs(root: string, out: Set<string>): void {
  const stack: string[] = [root];
  while (stack.length && out.size < MAX_ASMDEFS) {
    const dir = stack.pop()!;
    let entries: string[];
    try {
      entries = readdirSync(dir);
    } catch {
      continue;
    }
    for (const name of entries) {
      if (out.size >= MAX_ASMDEFS) break;
      const full = join(dir, name);
      let st: ReturnType<typeof statSync>;
      try {
        st = statSync(full);
      } catch {
        continue;
      }
      if (st.isDirectory()) {
        if (!SKIP_DIRS.has(name)) stack.push(full);
      } else if (ASMDEF_RE.test(name)) {
        out.add(full);
      }
    }
  }
}

/**
 * Count the project's own compile assemblies: `Assets/` + `Packages/` + any
 * `file:`-referenced local packages from `Packages/manifest.json`. Registry
 * packages (Library/PackageCache) are precompiled and excluded. Returns the
 * distinct count and a small sample of project-relative paths.
 */
export function countProjectAsmdefs(projectPath: string | null | undefined): AsmdefCount {
  const found = new Set<string>();
  if (!projectPath) return { count: 0, sample: [] };

  const assetsDir = join(projectPath, "Assets");
  const packagesDir = join(projectPath, "Packages");
  if (existsSync(assetsDir)) walkAsmdefs(assetsDir, found);
  if (existsSync(packagesDir)) walkAsmdefs(packagesDir, found);

  // file: packages referenced in manifest.json may live outside the project
  // (e.g. "file:../../packages/bridge"). Unity resolves these relative to the
  // Packages/ directory.
  const manifestPath = join(packagesDir, "manifest.json");
  if (existsSync(manifestPath)) {
    try {
      const manifest = JSON.parse(readFileSync(manifestPath, "utf8")) as {
        dependencies?: Record<string, string>;
      };
      const deps = manifest.dependencies ?? {};
      for (const v of Object.values(deps)) {
        if (typeof v === "string" && v.startsWith("file:")) {
          const pkgPath = resolve(packagesDir, v.slice("file:".length));
          if (existsSync(pkgPath)) walkAsmdefs(pkgPath, found);
        }
      }
    } catch {
      // malformed/unreadable manifest — skip; Assets/ + Packages/ still counted.
    }
  }

  const list = [...found];
  return {
    count: list.length,
    sample: list.slice(0, 8).map((p) => {
      const rel = relative(projectPath, p);
      return rel || p;
    }),
  };
}

/**
 * Distinct assemblies spanned by a set of compiler-error file paths. Returns
 * the count of unique owning asmdefs (via {@link nearestAsmdef}); errors whose
 * file does not map to an asmdef are dropped from the count.
 */
export function countAssembliesWithErrors(files: string[]): number {
  const set = new Set<string>();
  for (const f of files) {
    const a = nearestAsmdef(f);
    if (a) set.add(a);
  }
  return set.size;
}
