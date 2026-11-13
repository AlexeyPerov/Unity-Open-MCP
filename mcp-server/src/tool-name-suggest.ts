/**
 * Close-match lookup for tool names an agent got slightly wrong
 * (`unity_senses_console_clear` for `unity_open_mcp_console_clear`,
 * `component_add` for `unity_open_mcp_component_add`). Used by the
 * `unknown_tool` answers so the caller can retry with the real name.
 */

const PREFIXES = ["unity_open_mcp_", "unity_senses_"];

/** The name without its `unity_open_mcp_` / `unity_senses_` prefix. */
export function toolStem(name: string): string {
  const lower = name.trim().toLowerCase();
  for (const prefix of PREFIXES) if (lower.startsWith(prefix)) return lower.slice(prefix.length);
  return lower;
}

function editDistance(a: string, b: string): number {
  if (a === b) return 0;
  if (!a.length) return b.length;
  if (!b.length) return a.length;
  let previous = Array.from({ length: b.length + 1 }, (_, i) => i);
  for (let i = 1; i <= a.length; i++) {
    const current = [i];
    for (let j = 1; j <= b.length; j++) {
      current[j] = Math.min(
        previous[j] + 1,
        current[j - 1] + 1,
        previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1),
      );
    }
    previous = current;
  }
  return previous[b.length];
}

/**
 * Up to `limit` candidate names closest to `name`: an exact stem match first
 * (the prefix was wrong), then stems containing / contained in the query
 * stem, then small edit distances. Returns `[]` when nothing is close.
 */
export function closeToolNames(name: string, candidates: Iterable<string>, limit = 3): string[] {
  const stem = toolStem(name);
  if (!stem) return [];
  const scored: { name: string; score: number }[] = [];
  for (const candidate of candidates) {
    const candidateStem = toolStem(candidate);
    let score: number;
    if (candidateStem === stem) score = 0;
    else if (candidateStem.includes(stem) || stem.includes(candidateStem)) score = 1 + Math.abs(candidateStem.length - stem.length) / 100;
    else {
      const distance = editDistance(stem, candidateStem);
      if (distance > Math.max(2, Math.floor(stem.length / 4))) continue;
      score = 2 + distance;
    }
    scored.push({ name: candidate, score });
  }
  return scored
    .sort((a, b) => a.score - b.score || a.name.localeCompare(b.name))
    .slice(0, limit)
    .map((entry) => entry.name);
}
