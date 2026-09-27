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
- `UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH=1` — opt in to opening a project with a different Unity Editor; without it the Non-Matching Editor dialog is blocked without a click
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
| `ignore` (default) | Ignore | **blocked** unless mismatch opt-in | **blocked** (never auto-confirm) | OK | Reload/Revert | **blocked** (destructive) |
| `auto` | Ignore | blocked unless mismatch opt-in | blocked unless opt-in | OK | Reload/Revert | blocked unless opt-in |
| `recover` | Enter Safe Mode | blocked unless mismatch opt-in | blocked unless opt-in | OK | Reload/Revert | blocked unless opt-in |
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

**Non-Matching Editor is also fail-closed.** Unity may resolve packages and
rewrite project metadata before the bridge starts, so the default policies do
not click the dialog. Set `UNITY_OPEN_MCP_ALLOW_VERSION_MISMATCH=1` only for an
intentional upgrade or compatibility run; then `auto`/`ignore`/`recover`
select Continue. The explicit `safe-mode` and `cancel` policies still select
Quit.

`UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` is the hard kill-switch: it
disables all OS clicks regardless of policy (equivalent to `manual`, but
reported distinctly so you can tell "operator turned the feature off entirely"
from "operator chose manual for this run").

## Cross-platform notes

Every platform recognises a dialog by its window title reduced to lowercase
letters and digits, so "Enter Safe Mode?" matches as `entersafemode`. The main
Editor window is never treated as a dialog: its title
(`<Project> - <Scene> - <Platform> - Unity <version>`) holds project and scene
names, and "SAFE MODE" while the Editor runs in Safe Mode, any of which can
contain a dialog keyword. Unsaved-scene sheets attached to the main window are
still handled (see below).

Windows performs precise per-button selection (Win32 `BM_CLICK` on the
policy-chosen button).

macOS and Linux/X11 are focus-driven: pressing Return (`key code 36` /
`xdotool key Return`) activates the dialog's **focused** (default) button, so
Return is pressed only when the policy's first choice *is* that button. On
macOS the window must also show a button with that label (for example Ignore
on the Safe Mode prompt); a matching window without it, such as the
button-less "Hold On" progress window, is left alone. On Linux the title
returned by `xdotool getwindowname` is checked again before Return is sent.
When the policy picks any other button:

- **macOS** clicks the named button instead — the first button whose label,
  normalized to letters and digits, equals one of the policy's choices in
  priority order (for example Enter Safe Mode under `safe-mode`, or
  Quit/Cancel under `cancel`). If no such button is found it reports the
  dialog as `blocked`; it never falls back to pressing Return.
- **Linux/X11** cannot target a named button, so it reports the dialog as
  `blocked` for a human to close.

| Dialog | Focused button | Return press (macOS and Linux) | Otherwise, macOS | Otherwise, Linux |
| --- | --- | --- | --- | --- |
| launch-errors | Ignore | `auto`, `ignore` | named click: Enter Safe Mode (`recover`, `safe-mode`), Quit/Cancel (`cancel`) | blocked |
| Non-Matching Editor | Continue | `auto`/`ignore`/`recover` with the mismatch opt-in | named click: Quit/Cancel (`safe-mode`, `cancel`) | blocked |
| Project Upgrade | Confirm | Linux only: `auto`/`ignore`/`recover` with the upgrade opt-in | always blocked, even with the opt-in | blocked |
| Auto Graphics API | OK | `auto`, `ignore`, `recover` | named click: Quit/Cancel (`cancel`) | blocked |
| Scene modified externally | Reload | `auto`, `ignore`, `recover` | named click: Quit/Cancel (`cancel`) | blocked |
| Unsaved scene changes | — (not relied on) | none, even with the opt-in | named click with the opt-in | blocked |

Declined cells in the policy matrix stay declined on every platform: the
dialog is left alone and polling continues. Unsaved scene changes uses its own
named-button selection on macOS when the opt-in is set (see
[macOS Accessibility](#macos-accessibility-required-for-auto-dismiss)). Linux
requires `xdotool` (X11 only — Wayland is unsupported).

## macOS Accessibility (required for auto-dismiss)

On macOS, dialog auto-dismiss drives Unity modals through **AppleScript**
(`osascript` → System Events). **System Settings → Privacy & Security →
Accessibility** must allow the process that actually runs the MCP server — the
host that executes `node` / `unity-open-mcp`, for example:

| Host | What to enable in Accessibility |
| --- | --- |
| Terminal / iTerm / Warp | That terminal app |
| Cursor, VS Code, Zed, or another IDE | That IDE (hosts the MCP client, which spawns `node`) |
| Claude Code, OpenCode, or other CLI agents | The terminal or wrapper app that launches the agent |
| CI or custom scripts | The runner app or `node` binary, if macOS prompts for it |

This is **not Cursor-specific** — any MCP client or shell that spawns the server
needs the grant for OS-level clicks to work.

**Without Accessibility:** the dismiss loop logs *"Not authorized to send Apple
events to System Events"* (or similar). Launch-error and steady-state modals
must be dismissed by hand; agents see **`main_thread_blocked`** until you click
the dialog.

**One-time setup:** add the host app → toggle on → restart the agent or MCP
client so the next `node` child inherits the permission context.

**Applies when:** default launch-error dismiss is on (always, unless
`UNITY_OPEN_MCP_NO_AUTO_DISMISS_LAUNCH_ERRORS=1` or `manual` policy), or when
`UNITY_OPEN_MCP_ALLOW_UNSAVED_SCENE_DISMISS=1` is set for unsaved-scene modals.

## Related docs

- [Agent setup](setup/agent-setup.md) — let an AI agent install MCP + Unity packages
- [MCP client configuration](setup/client-configuration.md) — environment placement
- [Routing and lifecycle](api/routing-lifecycle.md) — `modal-dialog` lifecycle
