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

function slug(text) {
  return text
    .toLowerCase()
    .replace(/<[^>]+>/g, "")
    .replace(/[^\p{L}\p{N}\s_-]/gu, "")
    .trim()
    .replace(/\s+/g, "-")
    .replace(/-+/g, "-");
}

function anchorsFor(markdown) {
  const counts = new Map();
  const anchors = new Set();
  for (const line of visibleLines(markdown)) {
    const match = /^(#{1,6})\s+(.+?)\s*$/.exec(line);
    if (!match) continue;
    const base = slug(match[2]);
    const count = counts.get(base) ?? 0;
    counts.set(base, count + 1);
    anchors.add(count === 0 ? base : `${base}-${count}`);
  }
  return anchors;
}

function validateLink(source, rawTarget) {
  const target = rawTarget.replace(/^<|>$/g, "");
  if (!target || /^(?:https?:|mailto:|#)/i.test(target)) return;
  const [pathPart, fragment] = target.split("#", 2);
  const absolute = resolve(dirname(source), decodeURIComponent(pathPart));
  if (!existsSync(absolute)) {
    failures.push(`${relative(repoRoot, source)}: missing local target ${target}`);
    return;
  }
  if (fragment && extname(absolute) === ".md") {
    const anchors = anchorsFor(readFileSync(absolute, "utf8"));
    const expected = decodeURIComponent(fragment).toLowerCase();
    if (!anchors.has(expected)) {
      failures.push(`${relative(repoRoot, source)}: missing anchor ${target}`);
    }
  }
}

function validateHeadings(file, lines) {
  let previous = 0;
  let lastH2 = "";
  for (const [index, line] of lines.entries()) {
    const match = /^(#{1,6})\s+(.+?)\s*$/.exec(line);
    if (!match) continue;
    const level = match[1].length;
    if (previous > 0 && level > previous + 1) {
      failures.push(`${relative(repoRoot, file)}:${index + 1}: heading jumps from h${previous} to h${level}`);
    }
    previous = level;
    if (level === 2) lastH2 = match[2];
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

const markdownFiles = [
  ...collectMarkdown("README.md"),
  ...collectMarkdown("README.ru.md"),
  ...collectMarkdown("README.zh-CN.md"),
  ...collectMarkdown("docs"),
];

for (const file of markdownFiles) {
  const markdown = readFileSync(file, "utf8");
  const lines = visibleLines(markdown);
  const visible = lines.join("\n");
  for (const match of visible.matchAll(/!?\[[^\]]*\]\(([^)\s]+)(?:\s+["'][^"']*["'])?\)/g)) {
    validateLink(file, match[1]);
  }
  for (const match of visible.matchAll(/<img\s+[^>]*src=["']([^"']+)["'][^>]*>/gi)) {
    validateLink(file, match[1]);
  }
  validateHeadings(file, lines);
  validateTables(file, lines);
}

const rootReadme = readFileSync(resolve(repoRoot, "README.md"), "utf8");
for (const name of readdirSync(resolve(repoRoot, "docs")).filter((name) => name.endsWith(".md")).sort()) {
  const occurrences = rootReadme.split(`docs/${name}`).length - 1;
  if (occurrences !== 1) failures.push(`README.md: docs/${name} must be linked exactly once (found ${occurrences})`);
}

const apiIndex = readFileSync(resolve(repoRoot, "docs/api.md"), "utf8");
for (const name of readdirSync(resolve(repoRoot, "docs/api")).filter((name) => name.endsWith(".md")).sort()) {
  const occurrences = apiIndex.split(`api/${name}`).length - 1;
  if (occurrences !== 1) failures.push(`docs/api.md: api/${name} must be linked exactly once (found ${occurrences})`);
}

if (failures.length > 0) {
  console.error(`Documentation check failed (${failures.length}):`);
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}

console.log(`Documentation check passed (${markdownFiles.length} Markdown files).`);
