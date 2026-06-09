# Unity Agent Hub (`hub/`)

Greenfield desktop scaffold for Hub v1 using Tauri 2 + SvelteKit + Svelte 5.

## Local development

From the `hub/` directory:

```bash
npm install
npm run dev
npm run tauri dev
```

## Stack and pinned versions

Versions match `vibe-launcher` as required by M1 scaffold specs:

- Tauri: `^2` (`tauri`, `tauri-build`, `@tauri-apps/api`, `@tauri-apps/cli`)
- Svelte: `^5.0.0`
- SvelteKit: `^2.9.0`
- Vite: `^6.0.3`
- `@sveltejs/vite-plugin-svelte`: `^5.0.0`
- `@sveltejs/adapter-static`: `^3.0.6`

## Current scaffold scope

- Empty main window with branded app metadata (`Unity Agent Hub`).
- Baseline Tauri capabilities for upcoming M1 work:
  - `core:default`
  - `fs:default`
  - `shell:default`
  - `opener:default`
- No domain logic, project persistence, or Unity launch workflows yet.
