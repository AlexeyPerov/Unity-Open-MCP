import { defineConfig } from "vite";
import { sveltekit } from "@sveltejs/kit/vite";

const host = process.env.TAURI_DEV_HOST;

// Vite options tailored for Tauri development and only applied in
// `tauri dev` or `tauri build`. Mirrors the Hub frontend config so the
// dev server shares the same Tauri-friendly port behavior.
export default defineConfig({
  plugins: [sveltekit()],
  clearScreen: false,
  server: {
    port: 1430,
    strictPort: true,
    host: host || false,
    hmr: host
      ? {
          protocol: "ws",
          host,
          port: 1431,
        }
      : undefined,
    watch: {
      ignored: ["**/src-tauri/**"],
    },
  },
});
