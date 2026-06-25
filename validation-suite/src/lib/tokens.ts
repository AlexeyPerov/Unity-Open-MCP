/**
 * Shared design tokens for the Validation Suite UI.
 *
 * Pure CSS custom properties consumed by components (mirrors the Hub's
 * token approach). Kept small + neutral; the suite is a focused runner,
 * not a themed launcher.
 */

export const TOKENS = `
:root {
  --bg: #0f1115;
  --bg-elev: #161a21;
  --bg-elev-2: #1d222b;
  --border: #2a313c;
  --border-strong: #3a424f;
  --text: #e6e9ef;
  --text-dim: #9aa3b2;
  --text-faint: #6b7382;
  --accent: #4f8cff;
  --accent-soft: rgba(79, 140, 255, 0.16);
  --ok: #3fb950;
  --ok-soft: rgba(63, 185, 80, 0.16);
  --warn: #d29922;
  --warn-soft: rgba(210, 153, 34, 0.16);
  --bad: #f85149;
  --bad-soft: rgba(248, 81, 73, 0.16);
  --radius: 8px;
  --radius-sm: 6px;
  --mono: ui-monospace, "SF Mono", "JetBrains Mono", Menlo, monospace;
}
` as const;
