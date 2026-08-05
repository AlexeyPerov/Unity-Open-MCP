#!/usr/bin/env node
// release.mjs — one-shot maintainer release for the shared trio + Hub app.
//
// Orchestrates the existing sync / token-estimate / tag tools into a single
// flow. Deliberately a thin wrapper: version rewriting and tag creation stay
// owned by sync-version.mjs; this script only sequences them with a clean-tree
// gate, commit, and push.
//
// Usage:
//   node scripts/release.mjs 0.8.0              # set both lines, commit, tag, push
//   node scripts/release.mjs                    # prompt for X.Y.Z
//   node scripts/release.mjs 0.8.0 --dry-run    # print the plan; mutate nothing
//   node scripts/release.mjs 0.8.0 --yes        # skip the push confirmation
//   node scripts/release.mjs 0.8.0 --trio-only  # shared trio only (no Hub)
//   node scripts/release.mjs 0.8.0 --hub-only   # Hub app only (no trio)
//
// Requires a clean working tree. Pushing v* / hub-v* tags triggers the
// irreversible npm-publish / hub-release workflows — review before confirming.
//
// GitHub does not emit tag push/create webhook events when more than three
// tags are pushed in one command, so release tags are pushed in batches of
// ≤3 (trio, then Hub). See:
// https://docs.github.com/en/webhooks/webhook-events-and-payloads#push
//
// Requires Node 18+ (node: builtins only).

import { createInterface } from "node:readline";
import { execFileSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { stdin as input, stdout as output } from "node:process";

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");

/** @param {string[]} args @param {object} [opts] */
function run(args, opts = {}) {
  const { capture = false, env } = opts;
  return execFileSync(args[0], args.slice(1), {
    cwd: REPO_ROOT,
    stdio: capture ? ["ignore", "pipe", "pipe"] : "inherit",
    encoding: "utf8",
    env: env ? { ...process.env, ...env } : process.env,
  });
}

/** @param {string[]} args */
function git(args) {
  return run(["git", ...args], { capture: true }).trim();
}

/** @param {string} question */
async function ask(question) {
  const rl = createInterface({ input, output });
  try {
    const answer = await rl.question(question);
    return typeof answer === "string" ? answer.trim() : "";
  } finally {
    rl.close();
  }
}

/** @param {string} raw */
function parseVersion(raw) {
  if (typeof raw !== "string" || !/^v?\d+\.\d+\.\d+$/.test(raw)) {
    return undefined;
  }
  return raw.replace(/^v/, "");
}

function usage() {
  console.error(`Usage: node scripts/release.mjs [X.Y.Z] [--dry-run] [--yes] [--trio-only|--hub-only]

  X.Y.Z       plain major.minor.patch (leading "v" tolerated)
  --dry-run   print the plan without mutating git or files
  --yes       skip the interactive push confirmation
  --trio-only release the shared trio only (version.json)
  --hub-only  release the Hub app only (hub/version.json)`);
}

const argv = process.argv.slice(2);
const DRY_RUN = argv.includes("--dry-run");
const YES = argv.includes("--yes");
const TRIO_ONLY = argv.includes("--trio-only");
const HUB_ONLY = argv.includes("--hub-only");
const HELP = argv.includes("--help") || argv.includes("-h");

if (HELP) {
  usage();
  process.exit(0);
}

// Reject unknown "--" flags instead of silently dropping them (mirrors
// switch-project-version.mjs). Anything unrecognized used to fall through the
// exact includes() checks above AND the positional filter below, so a typo
// like `--trio-onl --yes` released BOTH the trio and the Hub — pushing the
// irreversible hub tag with confirmation suppressed.
const KNOWN_FLAGS = new Set(["--dry-run", "--yes", "--trio-only", "--hub-only", "--help"]);
const unknownFlags = argv.filter((a) => a.startsWith("--") && !KNOWN_FLAGS.has(a));
if (unknownFlags.length > 0) {
  console.error(`Unknown option(s): ${unknownFlags.join(", ")}`);
  usage();
  process.exit(1);
}

if (TRIO_ONLY && HUB_ONLY) {
  console.error("--trio-only and --hub-only are mutually exclusive.");
  process.exit(2);
}

const doTrio = !HUB_ONLY;
const doHub = !TRIO_ONLY;

const positional = argv.filter((a) => !a.startsWith("--"));
if (positional.length > 1) {
  usage();
  process.exit(2);
}

let version = positional[0] ? parseVersion(positional[0]) : undefined;
if (positional[0] && version === undefined) {
  console.error(`Invalid version "${positional[0]}" — expected X.Y.Z`);
  process.exit(2);
}

async function main() {
  if (!version) {
    if (!input.isTTY) {
      console.error(
        "✖ stdin is not interactive — pass the version as an argument: node scripts/release.mjs X.Y.Z",
      );
      process.exit(2);
    }
    const answered = await ask("Release version (X.Y.Z): ");
    version = parseVersion(answered);
    if (!version) {
      console.error(`Invalid version "${answered}" — expected X.Y.Z`);
      process.exit(2);
    }
  }

  const status = git(["status", "--porcelain"]);
  if (status) {
    console.error("✖ Working tree is not clean. Commit or stash changes before releasing.");
    console.error(status);
    process.exit(1);
  }

  const branch = git(["rev-parse", "--abbrev-ref", "HEAD"]);
  const trioTags = [`v${version}`, `bridge-v${version}`, `verify-v${version}`];
  const hubTags = [`hub-v${version}`];
  const tags = [...(doTrio ? trioTags : []), ...(doHub ? hubTags : [])];

  /** @param {string} name */
  function tagExists(name) {
    try {
      git(["rev-parse", "--verify", "--quiet", `refs/tags/${name}`]);
      return true;
    } catch {
      return false;
    }
  }
  const existingTags = tags.filter(tagExists);
  if (existingTags.length > 0) {
    console.error(
      `✖ Refusing to release — tags already exist: ${existingTags.join(", ")}`,
    );
    process.exit(1);
  }

  console.log(`Release plan for ${version}:`);
  console.log(`  branch:          ${branch}`);
  console.log(`  token estimates: regenerate`);
  if (doTrio) console.log(`  trio:            set version.json → ${version}`);
  if (doHub) console.log(`  hub:             set hub/version.json → ${version}`);
  console.log(`  commit:          chore: release ${version}`);
  console.log(`  tags:            ${tags.join(", ")}`);
  // GitHub drops tag webhook events when >3 tags are pushed at once; keep
  // trio (3) and Hub (1) as separate pushes even when releasing both.
  const pushPlan = [
    "HEAD → origin",
    ...(doTrio ? [`trio tags (${trioTags.join(", ")})`] : []),
    ...(doHub ? [`Hub tag (${hubTags.join(", ")})`] : []),
  ];
  console.log(`  push:            ${pushPlan.join("; ")}`);
  if (DRY_RUN) {
    console.log("\n--dry-run: no changes made.");
    process.exit(0);
  }

  if (!YES) {
    if (!input.isTTY) {
      console.error(
        "✖ stdin is not interactive — pass --yes to confirm the push, or run in a real terminal.",
      );
      process.exit(1);
    }
    const confirm = await ask(
      `\nPushing these tags triggers publish/release workflows. Continue? [y/N] `,
    );
    if (!/^y(es)?$/i.test(confirm)) {
      console.log("Aborted.");
      process.exit(1);
    }
  }

  console.log("\n→ Regenerating token estimates…");
  run(["node", "--experimental-strip-types", "scripts/generate-token-estimates.mjs"]);

  if (doTrio) {
    console.log(`\n→ Setting shared trio to ${version}…`);
    run(["node", "scripts/sync-version.mjs", "set", version]);
  }
  if (doHub) {
    console.log(`\n→ Setting Hub app to ${version}…`);
    run(["node", "scripts/sync-version.mjs", "set", version, "--hub"]);
  }

  const afterSet = git(["status", "--porcelain"]);
  if (!afterSet) {
    console.error(
      "✖ Version set produced no file changes — sources may already be at this version. Aborting before an empty commit.",
    );
    process.exit(1);
  }

  console.log("\n→ Committing…");
  run(["git", "add", "-A"]);
  run(["git", "commit", "-m", `chore: release ${version}`]);

  if (doTrio) {
    console.log(`\n→ Creating trio tags…`);
    run(["node", "scripts/sync-version.mjs", "tags", version]);
  }
  if (doHub) {
    console.log(`\n→ Creating Hub tag…`);
    run(["node", "scripts/sync-version.mjs", "tags", version, "--hub"]);
  }

  console.log("\n→ Pushing branch…");
  run(["git", "push", "origin", "HEAD"]);

  // Push ≤3 tags per command. GitHub docs: "Events will not be created for
  // tags when more than three tags are pushed at once." A combined
  // trio+Hub push (4 tags) silently skips npm-publish / hub-release.
  if (doTrio) {
    console.log("\n→ Pushing trio tags…");
    run(["git", "push", "origin", ...trioTags]);
  }
  if (doHub) {
    console.log("\n→ Pushing Hub tag…");
    run(["git", "push", "origin", ...hubTags]);
  }

  console.log(`\n✔ Released ${version}.`);
  console.log(`  Tags pushed: ${tags.join(", ")}`);
}

main().catch((err) => {
  console.error(err && err.message ? err.message : err);
  process.exit(1);
});
