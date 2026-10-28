// Playtest session recorder — secret redaction.
//
// Everything the recorder writes (call arguments, notes, console messages and
// stacks, project-command results, the state diff) goes through here first,
// so a session log or report can be handed to a human without leaking a
// password, a token or an API key the agent passed to a tool or the game
// printed. Two layers:
//   1. by key — an object property whose name reads as a secret
//      (`password`, `api_key`, `authToken`, `client_secret`, …) has its value
//      replaced, at any depth; a PlayerPrefs diff entry whose `key` reads as a
//      secret has its `value` / `from` / `to` replaced;
//   2. by value — inside every string, `Bearer …`, `password=…`,
//      `"token": "…"`-style fragments and any known secret value (the bridge
//      auth token, secret-named environment variables) are masked.
// The redaction marker is fixed so tests and reviewers can find it.

export const REDACTED = "[redacted]";

const SECRET_SEGMENTS = new Set([
  "password", "passwd", "pwd", "passphrase", "secret", "secrets", "token", "tokens", "apikey", "auth",
  "authorization", "credential", "credentials", "cookie", "cookies", "bearer", "signature", "otp", "pin", "ssn",
  "privatekey", "jwt",
]);
const SECRET_JOINED = /(api_?key|access_?key|private_?key|client_?secret|session_?id|refresh_?token|access_?token|card_?number|cvv)/i;

/** Split `authToken`, `api_key`, `X-Api-Key` into lowercase segments. */
function segments(name: string): string[] {
  return name
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .split(/[^A-Za-z0-9]+/)
    .filter(Boolean)
    .map(s => s.toLowerCase());
}

/** Whether a property / PlayerPrefs key name reads as holding a secret. */
export function isSecretKey(name: string): boolean {
  if (!name) return false;
  const parts = segments(name);
  if (parts.some(p => SECRET_SEGMENTS.has(p))) return true;
  return SECRET_JOINED.test(parts.join("_"));
}

const KEY_WORDS = "password|passwd|pwd|passphrase|secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|apikey|access[_-]?key|client[_-]?secret|private[_-]?key|authorization|auth[_-]?token";
// "password": "…" / 'token': '…' inside serialized JSON or code.
const QUOTED_PAIR = new RegExp(`((?:"|')(?:[A-Za-z0-9_.-]*?)(?:${KEY_WORDS})(?:"|')\\s*:\\s*)("|')(?:\\\\.|(?!\\2).)*\\2`, "gi");
// password=… / token: … / api_key=…
const PLAIN_PAIR = new RegExp(`(\\b(?:[A-Za-z0-9_.-]*?)(?:${KEY_WORDS})\\b\\s*[=:]\\s*)(?!\\[redacted\\])[^\\s"',;&)}\\]]+`, "gi");
const BEARER = /\b(Bearer)\s+[A-Za-z0-9\-._~+/]{6,}=*/g;

export class Redactor {
  private readonly known: string[];

  /** `knownSecrets`: exact values to mask wherever they appear (≥ 6 characters). */
  constructor(knownSecrets: Iterable<string | undefined | null> = []) {
    this.known = [...new Set([...knownSecrets].filter((s): s is string => typeof s === "string" && s.length >= 6))]
      .sort((a, b) => b.length - a.length);
  }

  /** Mask secret fragments inside free text. */
  text(value: string): string {
    let out = value;
    for (const secret of this.known) if (out.includes(secret)) out = out.split(secret).join(REDACTED);
    out = out.replace(BEARER, `$1 ${REDACTED}`);
    out = out.replace(QUOTED_PAIR, (_m, prefix: string, quote: string) => `${prefix}${quote}${REDACTED}${quote}`);
    out = out.replace(PLAIN_PAIR, (_m, prefix: string) => `${prefix}${REDACTED}`);
    return out;
  }

  /** Deep copy of a JSON value with secret-named properties and secret fragments masked. */
  value<T>(input: T): T {
    return this.walk(input, 0) as T;
  }

  private walk(input: unknown, depth: number): unknown {
    if (typeof input === "string") return this.text(input);
    if (depth > 32 || input === null || typeof input !== "object") return input;
    if (Array.isArray(input)) return input.map(item => this.walk(item, depth + 1));
    const source = input as Record<string, unknown>;
    // A PlayerPrefs / settings entry: { key: "auth_token", value | from | to }.
    const entrySecret = typeof source.key === "string" && isSecretKey(source.key);
    const out: Record<string, unknown> = {};
    for (const [name, value] of Object.entries(source)) {
      if (isSecretKey(name) && value !== null && value !== undefined && typeof value !== "boolean") out[name] = REDACTED;
      else if (entrySecret && (name === "value" || name === "from" || name === "to")) out[name] = maskEntryValue(value);
      else out[name] = this.walk(value, depth + 1);
    }
    return out;
  }
}

// Keep the type of a diff value ({type, value}) and drop the content.
function maskEntryValue(value: unknown): unknown {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    const v = value as Record<string, unknown>;
    return { ...(typeof v.type === "string" ? { type: v.type } : {}), value: REDACTED };
  }
  return value === null || value === undefined ? value : REDACTED;
}

/** Secret-named environment variables of this process (values to mask). */
export function secretEnvValues(env: NodeJS.ProcessEnv = process.env): string[] {
  const out: string[] = [];
  for (const [name, value] of Object.entries(env)) {
    if (typeof value === "string" && value.length >= 8 && isSecretKey(name)) out.push(value);
  }
  return out;
}
