#!/usr/bin/env node

import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, extname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const failures = [];

function collectMarkdown(entry, output = []) {
  const absolute = resolve(repoRoot, entry);
  if (!existsSync(absolute)) return output;
  if (statSync(absolute).isFile()) {
    if (extname(absolute) === ".md") output.push(absolute);
    return output;
  }
  for (const name of readdirSync(absolute).sort()) {
    collectMarkdown(relative(repoRoot, join(absolute, name)), output);
  }
  return output;
}

function visibleLines(markdown) {
  let fenced = false;
  return markdown.split(/\r?\n/).map((line) => {
    if (/^\s*(```|~~~)/.test(line)) {
      fenced = !fenced;
      return "";
    }
    return fenced ? "" : line;
  });
}

// Blank out inline code spans (same-length backtick runs) so link-like text
// inside them is not validated. Lengths are preserved for readability only.
function stripInlineCode(line) {
  return line.replace(/(`+)(?!`)[\s\S]*?(?<!`)\1(?!`)/g, (span) => " ".repeat(span.length));
}

function headingText(line) {
  const match = /^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$/.exec(line);
  return match ? { level: match[1].length, text: match[2] } : null;
}

// GitHub heading-anchor semantics (github-slugger): render the heading to text
// (links -> label, code/emphasis markers and HTML tags dropped), lowercase,
// drop everything except letters, marks, numbers, spaces, "-" and "_", then
// turn each space into "-". Repeated hyphens are NOT collapsed.
function slug(text) {
  return text
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/!?\[([^\]]*)\]\[[^\]]*\]/g, "$1")
    .replace(/<[^>]+>/g, "")
    .replace(/`/g, "")
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{M}\p{N} _-]/gu, "")
    .replace(/ /g, "-");
}

class Slugger {
  constructor() {
    this.occurrences = new Map();
  }

  slug(text) {
    const original = slug(text);
    let result = original;
    while (this.occurrences.has(result)) {
      const next = this.occurrences.get(original) + 1;
      this.occurrences.set(original, next);
      result = `${original}-${next}`;
    }
    this.occurrences.set(result, 0);
    return result;
  }
}

const anchorCache = new Map();

function anchorsFor(file) {
  if (anchorCache.has(file)) return anchorCache.get(file);
  const slugger = new Slugger();
  const anchors = new Set();
  for (const line of visibleLines(readFileSync(file, "utf8"))) {
    const heading = headingText(line);
    if (heading) anchors.add(slugger.slug(heading.text));
    for (const match of stripInlineCode(line).matchAll(/<a\s[^>]*\b(?:id|name)=["']([^"']+)["']/gi)) {
      anchors.add(match[1].toLowerCase());
    }
  }
  anchorCache.set(file, anchors);
  return anchors;
}

function decode(source, value, target) {
  try {
    return decodeURIComponent(value);
  } catch {
    failures.push(`${relative(repoRoot, source)}: malformed URL encoding in ${target}`);
    return null;
  }
}

// Returns the resolved absolute path of a local target, or null for external,
// same-page, or invalid targets.
function resolveTarget(source, rawTarget) {
  const target = rawTarget.trim().replace(/^<|>$/g, "");
  if (!target || /^[a-z][a-z0-9+.-]*:/i.test(target) || target.startsWith("//")) return null;
  const hashIndex = target.indexOf("#");
  const beforeHash = hashIndex >= 0 ? target.slice(0, hashIndex) : target;
  const fragment = hashIndex >= 0 ? target.slice(hashIndex + 1) : "";
  const pathPart = beforeHash.split("?", 1)[0];
  return { target, pathPart, fragment };
}

function validateLink(source, rawTarget) {
  const parsed = resolveTarget(source, rawTarget);
  if (!parsed) return;
  const { target, pathPart, fragment } = parsed;
  let absolute = source;
  if (pathPart) {
    const decoded = decode(source, pathPart, target);
    if (decoded === null) return;
    absolute = decoded.startsWith("/") ? join(repoRoot, decoded) : resolve(dirname(source), decoded);
    if (!existsSync(absolute)) {
      failures.push(`${relative(repoRoot, source)}: missing local target ${target}`);
      return;
    }
  }
  if (fragment && extname(absolute) === ".md" && statSync(absolute).isFile()) {
    const decoded = decode(source, fragment, target);
    if (decoded === null) return;
    if (!anchorsFor(absolute).has(decoded.toLowerCase())) {
      failures.push(`${relative(repoRoot, source)}: missing anchor ${target}`);
    }
  }
}

// Every local link target in the fence- and code-span-stripped text: inline
// links/images, reference definitions, and HTML href/src/srcset attributes.
function linkTargets(lines) {
  const targets = [];
  const text = lines.map(stripInlineCode).join("\n");
  for (const match of text.matchAll(/\]\(\s*(<[^>\n]*>|[^)\s]+)(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?\s*\)/g)) {
    targets.push(match[1]);
  }
  for (const match of text.matchAll(/^ {0,3}\[[^\]]+\]:\s*(<[^>\n]*>|\S+)/gm)) {
    targets.push(match[1]);
  }
  for (const match of text.matchAll(/<(?:a|img|source)\s[^>]*>/gi)) {
    const tag = match[0];
    for (const attr of tag.matchAll(/\b(href|src|srcset)\s*=\s*(?:"([^"]*)"|'([^']*)')/gi)) {
      const value = attr[2] ?? attr[3] ?? "";
      if (attr[1].toLowerCase() === "srcset") {
        for (const candidate of value.split(",")) {
          const url = candidate.trim().split(/\s+/, 1)[0];
          if (url) targets.push(url);
        }
      } else {
        targets.push(value);
      }
    }
  }
  return targets;
}

function validateHeadings(file, lines) {
  let previous = 0;
  let lastH2 = "";
  for (const [index, line] of lines.entries()) {
    const heading = headingText(line);
    if (!heading) continue;
    const { level } = heading;
    if (previous > 0 && level > previous + 1) {
      failures.push(`${relative(repoRoot, file)}:${index + 1}: heading jumps from h${previous} to h${level}`);
    }
    previous = level;
    if (level === 2) lastH2 = heading.text;
  }
  const terminalSections = ["Related docs", "Source references"];
  for (const terminal of terminalSections) {
    const index = lines.findIndex((line) => line === `## ${terminal}`);
    if (index >= 0 && lastH2 !== terminal) {
      failures.push(`${relative(repoRoot, file)}:${index + 1}: ${terminal} must be the final h2 section`);
    }
  }
}

function validateTables(file, lines) {
  for (let index = 0; index < lines.length; index += 1) {
    if (!/^\s*\|.*\|\s*$/.test(lines[index])) continue;
    const start = index;
    while (index + 1 < lines.length && /^\s*\|.*\|\s*$/.test(lines[index + 1])) index += 1;
    const block = lines.slice(start, index + 1);
    if (block.length < 2 || !/^\s*\|(?:\s*:?-+:?\s*\|)+\s*$/.test(block[1])) {
      failures.push(`${relative(repoRoot, file)}:${start + 1}: malformed Markdown table`);
    }
  }
}

// Counts links in `indexFile` whose resolved local path is `targetFile`.
function countLinksTo(indexFile, targetFile) {
  const lines = visibleLines(readFileSync(indexFile, "utf8"));
  let count = 0;
  for (const raw of linkTargets(lines)) {
    const parsed = resolveTarget(indexFile, raw);
    if (!parsed || !parsed.pathPart) continue;
    let decoded;
    try {
      decoded = decodeURIComponent(parsed.pathPart);
    } catch {
      continue;
    }
    if (resolve(dirname(indexFile), decoded) === targetFile) count += 1;
  }
  return count;
}

if (process.argv.includes("--self-test")) {
  const slugger = new Slugger();
  const cases = [
    ["One-shot release (trio + Hub)", "one-shot-release-trio--hub"],
    ["`mcpServers` (Cursor, Claude Code)", "mcpservers-cursor-claude-code"],
    ["`mcpServers`（Cursor 与大多数客户端）", "mcpserverscursor-与大多数客户端"],
    ["[Link](x.md) and **bold**", "link-and-bold"],
    ["Привет, мир!", "привет-мир"],
    ["Duplicate", "duplicate"],
    ["Duplicate", "duplicate-1"],
    ["Duplicate", "duplicate-2"],
    ["A — B", "a--b"],
    ["unity_open_mcp_ping", "unity_open_mcp_ping"],
  ];
  let ok = true;
  for (const [input, expected] of cases) {
    const actual = slugger.slug(input);
    if (actual !== expected) {
      ok = false;
      console.error(`slug(${JSON.stringify(input)}) = ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
    }
  }
  console.log(ok ? `Slug self-test passed (${cases.length} cases).` : "Slug self-test failed.");
  process.exit(ok ? 0 : 1);
}

const markdownFiles = [
  ...collectMarkdown("README.md"),
  ...collectMarkdown("README.ru.md"),
  ...collectMarkdown("README.zh-CN.md"),
  ...collectMarkdown("CONTRIBUTING.md"),
  ...collectMarkdown("scripts/README.md"),
  ...collectMarkdown("hub/README.md"),
  ...collectMarkdown("docs"),
];

for (const file of markdownFiles) {
  const lines = visibleLines(readFileSync(file, "utf8"));
  for (const target of linkTargets(lines)) validateLink(file, target);
  validateHeadings(file, lines);
  validateTables(file, lines);
}

const rootReadme = resolve(repoRoot, "README.md");
for (const name of readdirSync(resolve(repoRoot, "docs")).filter((name) => name.endsWith(".md")).sort()) {
  const occurrences = countLinksTo(rootReadme, resolve(repoRoot, "docs", name));
  if (occurrences !== 1) failures.push(`README.md: docs/${name} must be linked exactly once (found ${occurrences})`);
}

const apiIndex = resolve(repoRoot, "docs/api.md");
for (const name of readdirSync(resolve(repoRoot, "docs/api")).filter((name) => name.endsWith(".md")).sort()) {
  const occurrences = countLinksTo(apiIndex, resolve(repoRoot, "docs/api", name));
  if (occurrences !== 1) failures.push(`docs/api.md: api/${name} must be linked exactly once (found ${occurrences})`);
}

if (failures.length > 0) {
  console.error(`Documentation check failed (${failures.length}):`);
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}

console.log(`Documentation check passed (${markdownFiles.length} Markdown files).`);
