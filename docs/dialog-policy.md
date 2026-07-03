# Dialog policy

Unity raises native modal dialogs at startup that block the bridge from coming
up — the "Enter Safe Mode?" / compile-errors prompt, "Opening Project in
Non-Matching Editor Installation", "Project Upgrade Required", and "Auto
Graphics API Notice". The MCP server probes the desktop for these while it
waits for bridge readiness and clicks the appropriate button so unattended
flows (CI, agents) are not stuck behind a modal no one is watching.

The same loop also recognises two **steady-state** modals that can surface at
any time while the editor is running: "Scene has been modified externally"
(triggered when an external process like `git checkout` or codegen rewrites a
scene file Unity has open) and "Unsaved changes to scene" (triggered when a
mutating tool leaves a scene dirty and Unity's native save prompt fires). The
former is auto-dismissed under `auto`/`ignore`/`recover` (Reload/Revert — the
disk rewrite was intentional). The latter is **destructive under every policy**
("Don't Save" loses work, "Save" persists unwanted state) and is blocked unless
the dedicated opt-in `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` is set; the
dismiss loop reports it as `blocked` with an audit line so the stall surfaces
with a clear diagnosis instead of a silent timeout.

Set these in your MCP client config (`env` block) or in the shell when running
CLI commands such as `wait-for-ready`.

## Environment variables

- `UNITY_OPEN_MCP_DIALOG_POLICY=auto|manual|ignore|recover|safe-mode|cancel` (default `ignore`)
- `UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1` — opt in to auto-confirming the irreversible Project Upgrade dialog; off by default
- `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` — opt in to auto-dismissing the "Unsaved changes to scene" modal (destructive under every policy; off by default)
- `UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` — kill-switch; disables all OS clicks
- `UNITY_OPEN_MCP_DISMISS_TIMEOUT_MS` (default 30000)
- `UNITY_OPEN_MCP_DISMISS_INTERVAL_MS` (default 1500)

## Policy matrix

`UNITY_OPEN_MCP_DIALOG_POLICY` selects which button to click on each dialog.
The default `ignore` preserves the long-standing behaviour (Ignore on the
compile-errors prompt) while also dismissing the two safe lower-frequency
dialogs and **never** auto-confirming a project upgrade:

| Policy | launch-errors | Non-Matching Editor | Project Upgrade | Auto Graphics API | Scene modified externally | Unsaved scene changes |
| --- | --- | --- | --- | --- | --- | --- |
| `ignore` (default) | Ignore | Continue | **blocked** (never auto-confirm) | OK | Reload/Revert | **blocked** (destructive) |
| `auto` | Ignore | Continue | blocked unless opt-in | OK | Reload/Revert | blocked unless opt-in |
| `recover` | Enter Safe Mode | Continue | blocked unless opt-in | OK | Reload/Revert | blocked unless opt-in |
| `safe-mode` | Enter Safe Mode | Quit | blocked | (declined) | (declined) | blocked |
| `cancel` | Quit | Quit | Quit | Quit | Quit | Don't Save |
| `manual` | — (no clicks at all) | — | — | — | — | — |

**Project Upgrade is irreversible** (it rewrites project metadata; recoverable
only via VCS revert). No policy value auto-confirms it. Set
`UNITY_OPEN_MCP_ALLOW_PROJECT_UPGRADE=1` to opt in — then `auto`/`ignore`/
`recover` will click Confirm. **Unsaved scene changes is destructive** (data
loss either way); set `UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` to opt in
— then `auto`/`ignore`/`recover` will click Save (preserve work). Both opt-ins
are audited: each dismissal (or block) is logged once to the MCP server's
stderr with the dialog kind, button, and policy.

`UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` is the hard kill-switch: it
disables all OS clicks regardless of policy (equivalent to `manual`, but
reported distinctly so you can tell "operator turned the feature off entirely"
from "operator chose manual for this run").

## Cross-platform notes

Windows performs precise per-button selection (Win32 `BM_CLICK` on the
policy-chosen button). macOS and Linux/X11 press the **focused** (default)
button via `key code 36` / `Return`, which under the default policies is the
safe choice; both platforms detect and **block** a project-upgrade dialog
rather than clicking. Linux requires `xdotool` (X11 only — Wayland is
unsupported); macOS requires an Accessibility permission for the terminal /
`node` binary (System Settings → Privacy & Security → Accessibility).

## Related docs

- [Manual setup](manual-setup.md) — MCP client config and CI CLI
- [Wizard setup](wizard-setup.md) — guided install and launch verification
- [Development setup](development-setup.md) — local checkout and contributor workflows
- [MCP tools API](api/mcp-tools.md) — tool lifecycle classes (`modal-dialog`)
