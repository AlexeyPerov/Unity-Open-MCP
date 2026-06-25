// Tauri doesn't have a Node.js server to do proper SSR
// so we use adapter-static with a fallback to index.html to put the site in SPA mode.
// See: https://v2.tauri.app/start/frontend/sveltekit/
export const ssr = false;
