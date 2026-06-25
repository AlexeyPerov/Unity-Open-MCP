/**
 * Shared design tokens for the Validation Suite UI.
 *
 * Mirrors the Hub's token names (`--hub-*`) and dark/light palettes so
 * the shell widgets ported from `hub/` (Button, Select, section panels,
 * StatusDrawer) render identically. The Hub's `BRAND_COLORS` live here
 * too as a typed export for any JS-side color needs.
 */

export const APP_NAME = "Validation Suite";

export const STATUS_COLORS = {
  ok: "#2f6f4a",
  warn: "#8a6d2b",
  missing: "#7a4a1a",
  running: "#2a5a8a",
} as const;

export const BRAND_COLORS = {
  bg: "#14151a",
  surface: "#1e1f26",
  card: "#24252c",
  border: "#34353f",
  borderLight: "#3f4150",
  borderHover: "#474957",
  accent: "#5c7cfa",
  text: "#e9e9ef",
  textMuted: "#8b8d9a",
  textDim: "#a1a3b0",
  textBright: "#f2f3f7",
  error: "#de3576",
  warning: "#c9a227",
  success: "#2f6f4a",
} as const;
