// Tauri doesn't have a Node.js server to do proper SSR
// so we use adapter-static with a fallback to index.html to put the site in SPA mode.
// Mirrors the Hub stack (Tauri 2 + SvelteKit + Svelte 5).
// See: https://v2.tauri.app/start/frontend/sveltekit/
import adapter from "@sveltejs/adapter-static";
import { vitePreprocess } from "@sveltejs/vite-plugin-svelte";

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    adapter: adapter({
      fallback: "index.html",
    }),
    alias: {
      // Local engine-neutral core package (scenario DTOs, loader, state).
      // Resolves to the TS source directly — no build step needed.
      "@validation-suite/core": "packages/core/src/index.ts",
    },
  },
};

export default config;
