// Surgical edits to a Codex `config.toml`.
//
// `setup --client codex` owns exactly one server entry: the
// `[mcp_servers.<key>]` table and its `.env` sub-table (or an inline
// `env = { … }`). Everything else in the file — other servers, their
// comments, API tokens, formatting — must survive byte for byte, so this is a
// line-level editor rather than a parse-and-reserialize: the package ships
// with one runtime dependency and a TOML serializer would reformat the whole
// file anyway.
//
// The editor recognizes the shapes Codex documents and the Hub / bridge
// window write: a `[mcp_servers.<key>]` header (bare or quoted segments), plain
// `key = value` lines (multi-line arrays and strings included), an `.env`
// sub-table or a one-line inline `env` table, comments, and CRLF files. Any
// other way of spelling the same entry (dotted keys from a parent table, an
// inline server table, arrays of tables) is refused with `CodexTomlError`
// instead of guessed at — a wrong guess would corrupt a file that usually
// holds credentials.

export class CodexTomlError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "CodexTomlError";
  }
}

export interface CodexServerEntry {
  command: string;
  args: string[];
  /** Env keys this run sets. */
  env: Record<string, string>;
  /** Env keys this run removes when present (a stale machine path). */
  removeEnv: string[];
}

/**
 * Merge `entry` into `body` under `[mcp_servers.<serverKey>]`. Keys the entry
 * does not own (`enabled`, timeouts, extra env vars, `.tools.*` tables) are
 * left exactly as they are. Idempotent: merging the result again returns it
 * unchanged.
 */
export function mergeCodexServer(body: string, serverKey: string, entry: CodexServerEntry): string {
  const eol = body.includes("\r\n") ? "\r\n" : "\n";
  const lines = body.split(/\r?\n/);
  const items = scan(lines);
  const main = ["mcp_servers", serverKey];
  const envPath = [...main, "env"];
  const label = `[${renderKeyPath(main)}]`;

  for (const item of items) {
    if (item.kind === "array-header" && startsWith(item.path, main)) {
      throw refuse(label, "is declared as an array of tables");
    }
    if (item.kind === "kv") {
      const full = [...item.table, ...item.key];
      if (startsWith(full, main) && item.table.length < main.length) {
        throw refuse(label, "is spelled with dotted keys or an inline table");
      }
      if (samePath(item.table, main) && item.key.length > 1 && item.key[0] === "env") {
        throw refuse(label, "sets env through dotted keys");
      }
    }
  }

  const headers = items.filter((i): i is HeaderItem => i.kind === "header");
  const mainHeaders = headers.filter((h) => samePath(h.path, main));
  const envHeaders = headers.filter((h) => samePath(h.path, envPath));
  if (mainHeaders.length > 1 || envHeaders.length > 1) {
    throw refuse(label, "is declared more than once");
  }
  const mainHeader = mainHeaders[0];
  const envHeader = envHeaders[0];

  if (!mainHeader) {
    if (envHeader) throw refuse(label, "has an env table but no server table");
    return appendBlock(body, eol, renderCodexServer(serverKey, entry, eol));
  }

  const edits: Edit[] = [];
  const mainKvs = kvsIn(items, main);
  const find = (key: string) => mainKvs.find((kv) => kv.key.length === 1 && kv.key[0] === key);

  const missing: string[] = [];
  for (const [key, rendered] of [
    ["command", renderString(entry.command)],
    ["args", renderStringArray(entry.args)],
  ] as const) {
    const kv = find(key);
    if (!kv) missing.push(`${key} = ${rendered}`);
    else replaceValue(edits, kv, rendered);
  }
  if (missing.length > 0) edits.push({ start: mainHeader.line + 1, end: mainHeader.line, lines: missing });

  const inlineEnv = find("env");
  if (inlineEnv && envHeader) throw refuse(label, "defines env both inline and as a table");
  if (inlineEnv) {
    if (inlineEnv.endLine !== inlineEnv.line) throw refuse(label, "has a multi-line inline env table");
    replaceValue(edits, inlineEnv, mergeInlineTable(inlineEnv.value, entry, label));
  } else if (envHeader) {
    const envKvs = kvsIn(items, envPath);
    const inserts: string[] = [];
    for (const [key, value] of Object.entries(entry.env)) {
      const kv = envKvs.find((e) => e.key.length === 1 && e.key[0] === key);
      if (kv) replaceValue(edits, kv, renderString(value));
      else inserts.push(`${renderKey(key)} = ${renderString(value)}`);
    }
    for (const key of entry.removeEnv) {
      if (key in entry.env) continue;
      const kv = envKvs.find((e) => e.key.length === 1 && e.key[0] === key);
      if (kv) edits.push({ start: kv.line, end: kv.endLine, lines: [] });
    }
    if (inserts.length > 0) edits.push({ start: envHeader.line + 1, end: envHeader.line, lines: inserts });
  } else if (Object.keys(entry.env).length > 0) {
    // After the last line of the server table, before any `.tools.*` table.
    const last = Math.max(mainHeader.line, ...mainKvs.map((kv) => kv.endLine));
    edits.push({
      start: last + 1,
      end: last,
      lines: ["", `[${renderKeyPath(envPath)}]`, ...envLines(entry.env)],
    });
  }

  return applyEdits(lines, edits).join(eol);
}

/** A fresh `[mcp_servers.<key>]` block (plus `.env` when there is any). */
export function renderCodexServer(serverKey: string, entry: CodexServerEntry, eol = "\n"): string {
  const main = ["mcp_servers", serverKey];
  const out = [
    `[${renderKeyPath(main)}]`,
    "enabled = true",
    `command = ${renderString(entry.command)}`,
    `args = ${renderStringArray(entry.args)}`,
  ];
  if (Object.keys(entry.env).length > 0) {
    out.push("", `[${renderKeyPath([...main, "env"])}]`, ...envLines(entry.env));
  }
  return out.join(eol) + eol;
}

// ---------------------------------------------------------------------------
// Line scanner
// ---------------------------------------------------------------------------

interface HeaderItem {
  kind: "header" | "array-header";
  line: number;
  path: string[];
}

interface KvItem {
  kind: "kv";
  line: number;
  endLine: number;
  /** Table the pair sits in. */
  table: string[];
  key: string[];
  /** Text before the value: indentation, key, `=`, spacing. */
  prefix: string;
  /** Raw value text (may span lines). */
  value: string;
  /** Text after the value on its last line (spacing and comment). */
  trailing: string;
}

type Item = HeaderItem | KvItem;

function scan(lines: string[]): Item[] {
  const items: Item[] = [];
  let table: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trimStart();
    if (trimmed === "" || trimmed.startsWith("#")) continue;
    if (trimmed.startsWith("[")) {
      const header = parseHeader(line);
      if (header) {
        items.push({ kind: header.array ? "array-header" : "header", line: i, path: header.path });
        table = header.path;
        continue;
      }
    }
    const key = parseKeyPath(line, 0);
    if (!key) continue;
    let pos = skipSpaces(line, key.end);
    if (line[pos] !== "=") continue;
    pos = skipSpaces(line, pos + 1);
    const end = scanValue(lines, i, pos);
    const value =
      end.line === i
        ? line.slice(pos, end.col)
        : [line.slice(pos), ...lines.slice(i + 1, end.line), lines[end.line].slice(0, end.col)].join("\n");
    items.push({
      kind: "kv",
      line: i,
      endLine: end.line,
      table,
      key: key.path,
      prefix: line.slice(0, pos),
      value,
      trailing: lines[end.line].slice(end.col),
    });
    i = end.line;
  }
  return items;
}

function parseHeader(line: string): { path: string[]; array: boolean } | null {
  let pos = skipSpaces(line, 0);
  const array = line.startsWith("[[", pos);
  pos += array ? 2 : 1;
  const key = parseKeyPath(line, pos);
  if (!key) return null;
  pos = skipSpaces(line, key.end);
  if (!line.startsWith(array ? "]]" : "]", pos)) return null;
  pos = skipSpaces(line, pos + (array ? 2 : 1));
  if (pos < line.length && line[pos] !== "#") return null;
  return { path: key.path, array };
}

/** Dotted key (`a.b`, `"a b".c`, `'lit'`) starting at `start` (after spaces). */
function parseKeyPath(line: string, start: number): { path: string[]; end: number } | null {
  const path: string[] = [];
  let pos = skipSpaces(line, start);
  for (;;) {
    const ch = line[pos];
    let segment: string;
    if (ch === '"') {
      let out = "";
      pos++;
      while (pos < line.length && line[pos] !== '"') {
        if (line[pos] === "\\" && pos + 1 < line.length) {
          out += line[pos + 1];
          pos += 2;
        } else {
          out += line[pos++];
        }
      }
      if (line[pos] !== '"') return null;
      pos++;
      segment = out;
    } else if (ch === "'") {
      const close = line.indexOf("'", pos + 1);
      if (close < 0) return null;
      segment = line.slice(pos + 1, close);
      pos = close + 1;
    } else {
      const match = /^[A-Za-z0-9_-]+/.exec(line.slice(pos));
      if (!match) return null;
      segment = match[0];
      pos += segment.length;
    }
    path.push(segment);
    const next = skipSpaces(line, pos);
    if (line[next] !== ".") return { path, end: pos };
    pos = skipSpaces(line, next + 1);
  }
}

type ScanState = "normal" | "basic" | "literal" | "ml-basic" | "ml-literal";

/**
 * Where the value that begins at (`line`, `col`) ends: strings (single and
 * multi-line, basic and literal) and bracket nesting may carry it across
 * lines. Returns the position just past the value on its last line, before
 * any trailing spaces or comment.
 */
function scanValue(lines: string[], line: number, col: number): { line: number; col: number } {
  let state: ScanState = "normal";
  let depth = 0;
  for (let l = line; l < lines.length; l++) {
    const text = lines[l];
    let lastValueEnd = l === line ? col : 0;
    for (let c = l === line ? col : 0; c < text.length; c++) {
      const ch = text[c];
      if (state === "normal") {
        if (ch === "#") break;
        if (text.startsWith('"""', c)) {
          state = "ml-basic";
          c += 2;
        } else if (text.startsWith("'''", c)) {
          state = "ml-literal";
          c += 2;
        } else if (ch === '"') state = "basic";
        else if (ch === "'") state = "literal";
        else if (ch === "[" || ch === "{") depth++;
        else if (ch === "]" || ch === "}") depth--;
        if (ch !== " " && ch !== "\t") lastValueEnd = c + 1;
      } else if (state === "basic" || state === "ml-basic") {
        if (ch === "\\") {
          c++;
        } else if (state === "basic" && ch === '"') {
          state = "normal";
        } else if (state === "ml-basic" && text.startsWith('"""', c)) {
          state = "normal";
          c += 2;
        }
        lastValueEnd = c + 1;
      } else {
        if (state === "literal" && ch === "'") state = "normal";
        else if (state === "ml-literal" && text.startsWith("'''", c)) {
          state = "normal";
          c += 2;
        }
        lastValueEnd = c + 1;
      }
    }
    // A single-line string cannot run past its line; treat it as closed.
    if (state === "basic" || state === "literal") state = "normal";
    if (state === "normal" && depth <= 0) return { line: l, col: lastValueEnd };
  }
  return { line: lines.length - 1, col: lines[lines.length - 1].length };
}

// ---------------------------------------------------------------------------
// Edits
// ---------------------------------------------------------------------------

/** Replace lines `start..end` (inclusive; `end < start` inserts before `start`). */
interface Edit {
  start: number;
  end: number;
  lines: string[];
}

function replaceValue(edits: Edit[], kv: KvItem, rendered: string): void {
  if (kv.value === rendered) return;
  // Same strings spelled differently (a multi-line array, literal quotes):
  // nothing to change, so the user's formatting stays.
  const current = parseStrings(kv.value);
  const next = parseStrings(rendered);
  if (current && next && current.kind === next.kind && sameStrings(current.values, next.values)) return;
  edits.push({ start: kv.line, end: kv.endLine, lines: [`${kv.prefix}${rendered}${kv.trailing}`] });
}

function applyEdits(lines: string[], edits: Edit[]): string[] {
  const out = [...lines];
  // Bottom-up so earlier line numbers stay valid; an insert at the same line
  // as a replacement lands after it.
  const ordered = [...edits].sort((a, b) => b.start - a.start || b.end - a.end);
  for (const edit of ordered) {
    out.splice(edit.start, Math.max(0, edit.end - edit.start + 1), ...edit.lines);
  }
  return out;
}

function appendBlock(body: string, eol: string, block: string): string {
  if (body.trim() === "") return block;
  let out = body.endsWith("\n") ? body : body + eol;
  const lines = out.split(/\r?\n/);
  const lastContent = lines.length >= 2 ? lines[lines.length - 2] : "";
  if (lastContent.trim() !== "") out += eol;
  return out + block;
}

/** `{ A = "1", B = "2" }` with this run's keys set in place and removals dropped. */
function mergeInlineTable(raw: string, entry: CodexServerEntry, label: string): string {
  const inner = raw.trim();
  if (!inner.startsWith("{") || !inner.endsWith("}")) {
    throw refuse(label, "has an env value that is not a table");
  }
  const pairs = splitTopLevel(inner.slice(1, -1));
  const kept: string[] = [];
  const seen = new Set<string>();
  for (const pair of pairs) {
    const key = parseKeyPath(pair, 0);
    if (!key) throw refuse(label, "has an env table this editor cannot read");
    const name = key.path.length === 1 ? key.path[0] : undefined;
    if (name !== undefined && name in entry.env) {
      kept.push(`${pair.slice(0, skipSpaces(pair, skipSpaces(pair, key.end) + 1))}${renderString(entry.env[name])}`);
      seen.add(name);
    } else if (name !== undefined && entry.removeEnv.includes(name)) {
      continue;
    } else {
      kept.push(pair);
    }
  }
  for (const [key, value] of Object.entries(entry.env)) {
    if (!seen.has(key)) kept.push(`${renderKey(key)} = ${renderString(value)}`);
  }
  return kept.length === 0 ? "{}" : `{ ${kept.join(", ")} }`;
}

/** Split `a = 1, b = [2, 3]` on commas outside strings and brackets; trimmed, empties dropped. */
function splitTopLevel(text: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let quote: string | null = null;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quote) {
      if (quote === '"' && ch === "\\") i++;
      else if (ch === quote) quote = null;
    } else if (ch === '"' || ch === "'") quote = ch;
    else if (ch === "[" || ch === "{") depth++;
    else if (ch === "]" || ch === "}") depth--;
    else if (ch === "," && depth === 0) {
      parts.push(text.slice(start, i));
      start = i + 1;
    }
  }
  parts.push(text.slice(start));
  return parts.map((p) => p.trim()).filter((p) => p.length > 0);
}

/**
 * A string or an array of strings, read back from raw TOML; `null` for any
 * other value. Only used to decide that a value is already what we would write.
 */
function parseStrings(raw: string): { kind: "string" | "array"; values: string[] } | null {
  const text = raw.trim();
  const readString = (pos: number): { value: string; end: number } | null => {
    const quote = text[pos];
    if (quote === "'") {
      const close = text.indexOf("'", pos + 1);
      return close < 0 ? null : { value: text.slice(pos + 1, close), end: close + 1 };
    }
    if (quote !== '"') return null;
    let value = "";
    for (let i = pos + 1; i < text.length; i++) {
      const ch = text[i];
      if (ch === '"') return { value, end: i + 1 };
      if (ch !== "\\") {
        value += ch;
        continue;
      }
      const esc = text[++i];
      const simple: Record<string, string> = { n: "\n", t: "\t", r: "\r", '"': '"', "\\": "\\" };
      if (esc in simple) value += simple[esc];
      else if (esc === "u" || esc === "U") {
        const len = esc === "u" ? 4 : 8;
        value += String.fromCodePoint(parseInt(text.slice(i + 1, i + 1 + len), 16));
        i += len;
      } else return null;
    }
    return null;
  };
  if (!text.startsWith("[")) {
    const single = readString(0);
    return single && single.end === text.length ? { kind: "string", values: [single.value] } : null;
  }
  const values: string[] = [];
  let pos = 1;
  for (;;) {
    while (pos < text.length && /[\s,]/.test(text[pos])) pos++;
    if (text[pos] === "#") {
      while (pos < text.length && text[pos] !== "\n") pos++;
      continue;
    }
    if (text[pos] === "]") return pos === text.length - 1 ? { kind: "array", values } : null;
    const item = readString(pos);
    if (!item) return null;
    values.push(item.value);
    pos = item.end;
  }
}

function sameStrings(a: string[], b: string[]): boolean {
  return a.length === b.length && a.every((value, i) => value === b[i]);
}

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

function envLines(env: Record<string, string>): string[] {
  return Object.entries(env).map(([key, value]) => `${renderKey(key)} = ${renderString(value)}`);
}

function renderKey(key: string): string {
  return /^[A-Za-z0-9_-]+$/.test(key) ? key : renderString(key);
}

function renderKeyPath(path: string[]): string {
  return path.map(renderKey).join(".");
}

function renderStringArray(values: string[]): string {
  return `[${values.map(renderString).join(", ")}]`;
}

/** TOML basic string. */
export function renderString(value: string): string {
  let out = '"';
  for (const ch of value) {
    const code = ch.codePointAt(0)!;
    if (ch === "\\") out += "\\\\";
    else if (ch === '"') out += '\\"';
    else if (ch === "\n") out += "\\n";
    else if (ch === "\r") out += "\\r";
    else if (ch === "\t") out += "\\t";
    else if (code < 0x20 || code === 0x7f) out += `\\u${code.toString(16).padStart(4, "0")}`;
    else out += ch;
  }
  return out + '"';
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function kvsIn(items: Item[], table: string[]): KvItem[] {
  return items.filter((i): i is KvItem => i.kind === "kv" && samePath(i.table, table));
}

function samePath(a: string[], b: string[]): boolean {
  return a.length === b.length && a.every((segment, i) => segment === b[i]);
}

function startsWith(path: string[], prefix: string[]): boolean {
  return path.length >= prefix.length && prefix.every((segment, i) => path[i] === segment);
}

function skipSpaces(text: string, pos: number): number {
  while (pos < text.length && (text[pos] === " " || text[pos] === "\t")) pos++;
  return pos;
}

function refuse(label: string, why: string): CodexTomlError {
  return new CodexTomlError(
    "unsupported_codex_config",
    `${label} ${why}; setup only edits the plain table form and leaves this file unchanged.`,
  );
}
