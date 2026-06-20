// Read the server's own version from package.json at runtime.
//
// We avoid a static `import pkg from "../package.json"` because TypeScript's
// `module: Node16` strips the JSON import attribute that Node 18.20+/20.10+/
// 22+ require (`with { type: "json" }`), which would crash the server at boot
// (`ERR_IMPORT_ATTRIBUTE_MISSING`). Resolving the path relative to `import.meta.url`
// and parsing with `JSON.parse` works under every supported Node version without
// a build-pipeline change.
//
// The helper falls back to a hard-coded constant only when the file cannot be
// read (e.g. an unusual packaging layout). That keeps `--version` usable even
// when package.json is not colocated with dist/, at the cost of potential drift
// — the maintainer-panel publish flow always runs from a real checkout, so this
// fallback is a safety net, not the happy path.

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const FALLBACK_VERSION = "0.0.0-unknown";

/**
 * Read and parse the `version` field from the package's own package.json.
 * The file is expected at `../package.json` relative to this compiled module
 * (`dist/package-version.js` → `package.json`), which holds for both a local
 * checkout and the published npm tarball (the `files` whitelist ships `dist/`
 * at the package root).
 */
export function readPackageVersion(
  moduleUrl: string = import.meta.url,
): string {
  try {
    const here = dirname(fileURLToPath(moduleUrl));
    const pkgPath = resolve(here, "..", "package.json");
    const body = readFileSync(pkgPath, "utf8");
    const parsed = JSON.parse(body) as { version?: unknown };
    if (typeof parsed.version === "string" && parsed.version.length > 0) {
      return parsed.version;
    }
  } catch {
    // Fall through to the hard-coded constant.
  }
  return FALLBACK_VERSION;
}
