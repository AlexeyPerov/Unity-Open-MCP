// Combined test-environment registration for Node's test runner:
//   1. The $state/$derived/$effect shim (so Svelte-rune store classes construct).
//   2. The $lib → src/lib module resolver (so store modules that import
//      "$lib/..." resolve under plain Node, outside SvelteKit/Vite).
//
// Registered via `--import ./src/lib/state/_register-test-env.mjs` from the
// npm test scripts. Both hooks are no-ops for code that doesn't use runes or
// `$lib`, so this is safe to layer over the full test suite.
import { register } from "node:module";

const here = import.meta.url;

// 1. Load the rune shim eagerly (side-effect import defines globals on
//    globalThis). Plain JS so it loads without --experimental-strip-types.
await import(new URL("./_test-shim.mjs", here).href);

// 2. Register the $lib resolver as a custom ESM resolve hook.
register(new URL("./_lib-resolver.mjs", here).href, here);
