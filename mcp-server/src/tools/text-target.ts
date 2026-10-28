// Label targeting (`target_text`) shared by inputsim_pointer, inputsim_touch
// and inputsim_gesture keyframes. The bridge resolves it over the same uGUI
// screen model ui_snapshot reports: the VISIBLE element whose text matches —
// a control matches on its label (descendant texts) — tapped at the centre of
// its visible rect. Several visible matches refuse with ambiguous_target
// (candidates + matches[] with index/path/text/rect) unless `index` picks one;
// none visible refuses with target_not_found (hidden_matches lists invisible
// ones). No schema default on text_match: an omitted value means exact.

export const TEXT_MATCH_MODES = ["exact", "contains", "regex"] as const;

export function textTargetProps(subject: string) {
  return {
    target_text: {
      type: "string",
      minLength: 1,
      description:
        `${subject} by its on-screen text instead of a name: the visible uGUI element (UI.Text / ` +
        "TextMeshPro) whose text matches; a button matches on its label. Default match is exact after " +
        "normalisation (trim, whitespace collapse, rich-text tags removed, case-insensitive). Ambiguous " +
        "→ ambiguous_target with matches[]; pick with `index` or narrow with `under`. The result " +
        "reports the resolution (`textTarget`; gestures list `textTargets`). Requires com.unity.ugui.",
    },
    under: {
      type: "string",
      description: "target_text only: search the subtree of this hierarchy path or trailing path suffix ('Shop').",
    },
    index: {
      type: "integer",
      minimum: 0,
      description: "target_text only: pick the n-th visible match (hierarchy order, 0-based; see matches[].index).",
    },
    text_match: {
      type: "string",
      enum: [...TEXT_MATCH_MODES],
      description:
        "target_text only: exact (default when omitted) | contains (case-insensitive substring) | " +
        "regex (.NET, case-insensitive, matched against the normalised text).",
    },
  } as const;
}
