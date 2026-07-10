// Maps the SvelteKit `$lib/...` alias to the real filesystem path so that
// hub store modules (which use `$lib/...` imports) load under Node's test
// runner. Registered via `--import` from the test npm script. This only
// affects test runs — the SvelteKit/Vite dev server resolves `$lib` itself.
//
// `$lib` is an alias for `src/lib` in SvelteKit (see svelte.config.js).
// SvelteKit imports omit extensions and use `.svelte.ts`; Node ESM needs an
// explicit extension, so we append `.ts` / `.svelte.ts` when the bare path
// has no resolvable file.
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";

const LIB_PREFIX = "$lib/";
const SRC_LIB = new URL("../", import.meta.url); // → src/lib/

function resolveExtension(baseUrl) {
  // baseUrl is a file: URL. Check the exact path first.
  const exactPath = fileURLToPath(baseUrl);
  if (existsSync(exactPath)) return baseUrl;
  // Then try appending extensions (string concat — URL() replaces segments).
  for (const ext of [".ts", ".svelte.ts"]) {
    if (existsSync(exactPath + ext)) return new URL(baseUrl.href + ext);
  }
  return baseUrl; // let the default resolver surface the error
}

export function resolve(specifier, context, nextResolve) {
  if (specifier.startsWith(LIB_PREFIX)) {
    const rest = specifier.slice(LIB_PREFIX.length);
    const base = new URL(rest, SRC_LIB);
    const resolved = resolveExtension(base);
    return nextResolve(resolved.href, context);
  }
  return nextResolve(specifier, context);
}
