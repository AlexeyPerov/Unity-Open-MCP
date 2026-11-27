// C# compiler-error extraction, shared between the batch compile_check path
// (batch-spawn.ts) and the offline read_compile_errors tool (unity-log.ts /
// read-compile-errors.ts). Both consume raw Unity compiler output that looks
// like:
//
//   Assets/Path.cs(line,col): error CSxxxx: message
//
// Unity formats every diagnostic this way regardless of where it is emitted —
// stdout of a -batchmode Unity, the platform Editor.log, or the Editor
// console. A single extractor therefore serves both the "second Unity we
// spawned" and "the live Editor's log file" sources.
//
// The raw-line extractor (extractCompilerErrors) returns the matched
// substrings as-is and is what batch-spawn uses for its best-effort fallback
// when the batch entry point never printed JSON markers. The structured
// extractor (extractStructuredCompilerErrors) additionally parses out the
// leading asset locator (file, line, column) and the CS code, which is what
// an agent needs to fix the offending file.

/** Maximum number of distinct errors we surface. Bounded so a giant wall of
 *  errors can't blow up the tool response; the agent can fix-and-recheck. */
export const MAX_COMPILER_ERRORS = 50;

// Match the full diagnostic line, capturing:
//   group 1: asset locator — `path(line,col)` or `path(line,line)` (Unity
//             sometimes emits a range). The path is everything up to the last
//             `(` before `):`.
//   group 2: file path (the part of the locator before the paren)
//   group 3: line number
//   group 4: column (absent when the locator has only a line)
//   group 5: CS error code (CSxxxx)
//   group 6: message
// `error CSxxxx:` may appear mid-line (Unity prints the asset locator first),
// so we do not anchor to line start. Lines are matched on a single-line basis
// (no [^\n]* prefix that would run across newlines).
const COMPILER_ERROR_RE =
  /(([^()\r\n]+)\((\d+)(?:,(\d+))?[^\)]*\)):\s*error\s+(CS\d{4}):\s*([^\r\n]+)/g;

// The raw-substring regex retained for the legacy extractCompilerErrors API
// (batch-spawn). It matches the tail `error CSxxxx: ...` without the asset
// locator; the structured extractor above supersedes it for new callers.
const COMPILER_ERROR_TAIL_RE = /error\s+CS\d{4}:[^\n]+/g;

/** A single compiler diagnostic, parsed from a Unity diagnostic line. */
export interface CompilerError {
  /** The full original line, e.g.
   *  `Assets/Foo.cs(10,14): error CS0246: ...`. */
  raw: string;
  /** Asset-relative file path, e.g. `Assets/Foo.cs`. Empty when the line had
   *  no parseable locator. */
  file: string;
  /** 1-based line number, or 0 when unparseable. */
  line: number;
  /** 1-based column, or 0 when the locator had none. */
  column?: number;
  /** Error code, e.g. `CS0246`. */
  code: string;
  /** The message after the code. */
  message: string;
}

/**
 * Extract CSxxxx diagnostics as structured records, deduped by raw line, in
 * first-seen order, capped at MAX_COMPILER_ERRORS. Lines that don't match the
 * full `path(line,col): error CSxxxx: message` shape (rare — Unity is
 * consistent) still contribute via a best-effort record with empty file/line.
 * A diagnostic glued to a fragment of a compiler command line (two build
 * outputs interleaved in Editor.log) is folded into its clean copy
 * ({@link dedupeCompilerErrors}).
 */
export function extractStructuredCompilerErrors(
  output: string,
): CompilerError[] {
  if (!output) return [];
  const seen = new Set<string>();
  const errors: CompilerError[] = [];
  COMPILER_ERROR_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = COMPILER_ERROR_RE.exec(output)) !== null) {
    const raw = m[0].trim();
    if (!raw || seen.has(raw)) continue;
    seen.add(raw);
    errors.push({
      raw,
      file: (m[2] ?? "").trim(),
      line: m[3] ? parseInt(m[3], 10) : 0,
      ...(m[4] ? { column: parseInt(m[4], 10) } : {}),
      code: m[5] ?? "",
      message: (m[6] ?? "").trim(),
    });
    // Collect past the cap: a garbled copy may sit before its clean one.
    if (errors.length >= MAX_COMPILER_ERRORS * 2) break;
  }
  return dedupeCompilerErrors(errors).slice(0, MAX_COMPILER_ERRORS);
}

// A compiler command-line fragment: a quoted argument (`-r:"Library/…`,
// `/additionalfile:"…`) or a Bee build-artifact path.
const COMMAND_LINE_FRAGMENT_RE = /"|^[-/][A-Za-z]+:|Library\/Bee\//;

function isGarbled(text: string): boolean {
  return COMMAND_LINE_FRAGMENT_RE.test(text);
}

/**
 * One record per diagnostic. When parallel compiler invocations interleave
 * their output, a diagnostic can be logged a second time with the start of a
 * command line glued before its path (`-r:"Library/Bee/…/Engin/Users/…/X.cs`)
 * or a quoted argument glued after its message. Such a copy has the same line,
 * column and code as the clean one, a path that ends with the clean path, and
 * a message that starts with the clean message: it is dropped, keeping the
 * clean record. A garbled record with no clean copy stays as it is.
 */
export function dedupeCompilerErrors<T extends { file: string; line: number; column?: number; code: string; message: string }>(errors: T[]): T[] {
  const kept: T[] = [];
  for (const error of errors) {
    const twin = kept.findIndex(k => sameDiagnostic(k, error));
    if (twin < 0) { kept.push(error); continue; }
    if (cleanliness(error) > cleanliness(kept[twin])) kept[twin] = error;
  }
  return kept;
}

function sameDiagnostic(a: { file: string; line: number; column?: number; code: string; message: string },
  b: { file: string; line: number; column?: number; code: string; message: string }): boolean {
  if (a.line !== b.line || a.code !== b.code || (a.column ?? 0) !== (b.column ?? 0)) return false;
  if (!isGarbled(a.file + a.message) && !isGarbled(b.file + b.message)) return false;
  const files = a.file === b.file || a.file.endsWith(b.file) || b.file.endsWith(a.file);
  const messages = a.message === b.message || a.message.startsWith(b.message) || b.message.startsWith(a.message);
  return files && messages && a.file.length > 0 && b.file.length > 0;
}

/** Higher is cleaner: no command-line fragment, then the shorter path and message. */
function cleanliness(e: { file: string; message: string }): number {
  return (isGarbled(e.file) ? 0 : 2) + (isGarbled(e.message) ? 0 : 1) - (e.file.length + e.message.length) / 1e6;
}

/**
 * Legacy raw-substring extractor. Returns the matched `error CSxxxx: ...`
 * substrings (without the leading asset locator), deduped, capped. Kept for
 * the batch-spawn fallback path and its existing tests.
 */
export function extractCompilerErrors(output: string): string[] {
  if (!output) return [];
  const seen = new Set<string>();
  const errors: string[] = [];
  COMPILER_ERROR_TAIL_RE.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = COMPILER_ERROR_TAIL_RE.exec(output)) !== null) {
    const line = m[0].trim();
    if (line && !seen.has(line)) {
      seen.add(line);
      errors.push(line);
      if (errors.length >= MAX_COMPILER_ERRORS) break;
    }
  }
  return errors;
}
