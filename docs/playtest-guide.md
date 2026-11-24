# Playtest guide

How an agent runs a smoke playtest of a Unity game through Unity Open MCP the way
a QA engineer would — boot the game, read the screen, tap and drag like a finger,
wait for things to happen, look at the frame on a target device — and hands back
a report a human can review: every step, the frame it showed, the errors it
caused and a verdict — or lets a crawler walk every screen and button on its own
and report what looks wrong ([Let the crawler explore](#let-the-crawler-explore)).

The tool contracts are in [Playtest tools](api/playtest.md) and
[Input simulation](api/input-simulation.md); this page is the workflow. The same
playbook ships to agents in the root skill (`skills/unity-open-mcp/SKILL.md`,
section "Playtest").

## What you need

- The project open in the Editor with the bridge online (Unity 6 for `game_view`
  and `capture_series`).
- The `playtest` and `input-simulation` groups:
  `manage_tools(action: "activate_for", intent: "playtest the game")` brings both.
- Ideally a handful of [project commands](api/project-commands.md) the game
  exposes for playtests (below). Without them an agent can still play, but it
  cannot boot past dev screens or isolate the save without guessing.

## Project commands a game should expose

Four small Editor-side commands make a game playtestable without the agent
reflecting into game classes. They live in an Editor folder of the game (so they
compile into `Assembly-CSharp-Editor` and call game code directly), and the
bridge discovers them from attributes — no MCP server change:

| Command | Kind | Does |
|---|---|---|
| `project.<owner>.playtest.boot` | play mode | Skips dev launchers / splash / login and lands on the first real screen; returns the game state |
| `project.<owner>.playtest.state_dump` | read-only | What the player sees and owns, as JSON (screen, currency, level, flags) — also usable as a `playtest_wait` `project_command` condition |
| `project.<owner>.playtest.begin` | edit or play mode | Switches the game to an isolated save (a test profile, a separate slot or storage scope) so the session cannot spend the developer's progress |
| `project.<owner>.playtest.end` | edit or play mode | Drops that save and switches back |

Agents find them with `project_commands(action: "list", query: "playtest")`.
`begin` / `end` that write only the game's own player data (PlayerPrefs, files
under `persistentDataPath`) are not project mutations — declare them without
`IsMutating`, and without `ReadOnlyHint` either.

The demo project carries a working example:
`demo/Assets/Fixtures/Playtest/Editor/PlaytestFixtureCommands.cs` (commands
`project.demo.playtest.boot`, `.state_dump`, `.begin`, `.end`) driving the small
wave game in `demo/Assets/Fixtures/Playtest/PlaytestFixtureGame.cs` (scene
`PlaytestFixture.unity`). Its shape:

```csharp
using UnityEditor;
using UnityOpenMcpBridge;

[BridgeToolType]
public static class PlaytestCommands
{
    [ProjectCommand("project.mygame.playtest.boot", Title = "Boot for a playtest",
        Description = "Play mode only: skip the dev launcher and land on the main menu.",
        Package = "mygame.playtest", Group = "playtest", Tags = new[] { "playtest" },
        IdempotentHint = true, Gate = GateMode.Off)]
    public static string Boot()
    {
        if (!EditorApplication.isPlaying)
            throw new System.InvalidOperationException("Enter play mode first.");
        Game.Instance.SkipLauncher();          // the game's own boot path
        return Game.Instance.StateJson();
    }

    [ProjectCommand("project.mygame.playtest.begin", Title = "Begin an isolated save",
        Description = "Switch the game to the 'playtest' save slot; the developer's slot is not touched.",
        Package = "mygame.playtest", Group = "playtest", Tags = new[] { "playtest" },
        Gate = GateMode.Off)]
    public static string Begin(bool reset = true)
    {
        SaveSystem.UseSlot("playtest", reset); // the game's own isolation
        return "{\"slot\":\"playtest\"}";
    }
}
```

A thrown exception reaches the agent as `execution_error` with the message, so
"enter play mode first" is a useful refusal.

## The playbook

### 1. Prepare

1. **Device first.** `game_view get` (note the state to put back), then
   `game_view set_device` (a Device Simulator phone + orientation) or
   `set_resolution`; continue only on `settled: true`. Every later capture names
   the device it was taken on (`capturedAt`).
2. **Isolate the state.** Prefer the project's hook: invoke `playtest.begin`.
   Otherwise let the session snapshot it (next step) — or call
   [`player_state`](api/playtest.md#unity_open_mcp_player_state) `snapshot`
   yourself. Never play a live session against the developer's save.
3. **Start recording.**
   `playtest_session {"action":"start","name":"Shop smoke","device":"iPhone 13 portrait","snapshot":true}`
   (drop `snapshot` when the project hook isolates the save). From here every
   input / wait / capture / project-command call you make is logged with its
   frames and console errors.
4. **Read the memory.** The start answers `memory`: the project's playtest notes
   (how to boot, the screen map, dangerous controls, known issues) and a summary
   of its profile — read them before you play; see
   [Keep what a playtest learned](#keep-what-a-playtest-learned).

### 2. Boot

Enter play mode (`editor_set_state play`, then `editor_status {"wait_until":"playing"}`),
then boot through the project's command (`playtest.boot`). Do not reflect into
launchers, singletons or save systems with `execute_csharp` — that tests the
reflection, not the game, and a recorded playtest should only contain what a
player could do.

### 3. Loop: read → act → wait → look → mark

1. **Read** the screen: `ui_snapshot` (compact; page if needed).
2. **Act** like a player: `inputsim_pointer` / `inputsim_touch` with
   `target_text: "Buy"` (`index` / `under` when the label repeats),
   `inputsim_gesture` for drags, swipes and holds over real frames, or
   `inputsim_text {"text":"Alex","target_text":"Nickname"}` for a text field
   (its own validation decides; read `typed.rejected`; `end: "submit"` for Enter;
   `secret: true` for a password the report must not show).
3. **Wait** for the game, never with a loop of calls: one `playtest_wait` with a
   condition — a log line, a `ui` query (`matches: false` for "the popup closed"),
   an object, the project's own read-only predicate (`state_dump` with `field`),
   `frames` / `ms` — and a `timeout_ms`. `time_scale` fast-forwards for the wait
   only. To see how a value moves (HP, a cooldown, a speed), start a
   `playtest_probe` on it before acting and `read` its buffer after — not a
   per-frame hook from `execute_csharp`.
4. **Look**: animated content needs a series —
   `capture_series {"start_on":{"log":{"pattern":"Popup opening"}},"at_ms":[0,100,200,300],"contact_sheet":true}`;
   a static screen needs one `unity_senses_screenshot {"view":"composed"}`.
5. **Mark** the step:
   `playtest_session {"action":"mark","label":"Buy the starter pack","verdict":"ok","note":"…"}`.
   Use `bug` for wrong behaviour you saw, `suspicious` for something a human
   should look at. The answer's `since_last_mark.errors` counts the console
   errors the step caused.

### 4. Findings

Treat every exception or error in the console as a finding, attributed to the
step that caused it — the recorder does the attribution (during a call, or right
after it), the mark tells you the count, the report lists each error once with
its first occurrence and stack. `playtest_wait` with `fail_on: ["exception"]`
stops a wait at the first exception instead of timing out.

### 5. Finish

1. `playtest_session {"action":"end"}` — writes the report, diffs the snapshot
   against the current state, and returns the report path with a compact summary
   (verdicts, findings, first occurrence of each error, lost calls).
2. Stop play mode. Put the state back: invoke `playtest.end`, or
   `player_state restore` (dry run first — read `prefs.removed` /
   `files.removed` — then `confirm: true` with the dry run's `plan`), and `player_state delete` the
   session's snapshot.
3. Put the Game view back (`game_view set_resolution` with the `size_label` from
   the first `get`).
4. Hand the report path to the human, with the bug / suspicious steps and the
   errors from the summary.
5. Write back what the next session needs:
   [Keep what a playtest learned](#keep-what-a-playtest-learned).

## Let the crawler explore

For "go through the game and tell me what is wrong" on menu-driven and mobile UI,
let [`playtest_explore`](api/playtest.md#unity_open_mcp_playtest_explore) do the
tapping: it walks every screen and button on its own, with no model call per
step, and files what it finds into the session. The agent's job is the setup and
the triage.

1. **Prepare** as above: the device first (`game_view set_device` — the safe-area
   check needs a Simulator device with insets), then `playtest_session start` with
   `snapshot: true` or the project's `playtest.begin`. The crawler refuses an
   unisolated session (`not_isolated`): it taps through the game for real.
2. **Boot** to the first real screen (play mode, the project's `playtest.boot`).
3. **Crawl.** `playtest_explore {}` — or with `deny` for project-specific danger
   (`"deny":["Prestige","Reset progress"]`), and `restart: "command"`,
   `restart_command: "project.<owner>.playtest.boot"` when the game has a boot
   command (faster and steadier than re-entering play mode). It returns a
   `job_id`: `jobs wait` it until terminal; don't touch the game meanwhile.
4. **Triage.** The result lists `screens` (with a frame each), `findings` (bug
   first), `denied` and `untried` controls and the `stop_reason`. Open the frame
   of every finding before calling it a bug — a `dead_button` may play a sound, a
   `small_touch_target` may be decoration. For a screen you reach by hand,
   `playtest_session {"action":"check"}` runs the same detectors and records them.
5. **Finish** as above: `playtest_session end` — the report has an Exploration
   section (screens with thumbnails, the findings table, the taps, what was never
   tapped).

Text fields get `fill_text` (default `Test`) before the screen's buttons; pass a
`fill_text` the forms accept, or fill a specific form by hand first. What it does
not cover yet: sliders, scrolling to reveal more, world-space taps and UI Toolkit
panels — drive those by hand in the same session.
Money, destructive and leaving controls (buy, purchase, delete, reset, log out,
rate, share, quit, prices…) are never tapped; `allow` exempts one you need.

On the demo: open `Assets/Fixtures/Playtest/ExploreFixture.unity`, enter play
mode, `playtest_session {"action":"start","name":"Explore fixture","snapshot":true}`,
`playtest_explore {}` → 8 screens, the planted overflow, console error, small
touch target, placeholder text, dead button and dead end; "Buy Gems", "Quit" and
"$1.99 Starter Pack" never tapped.

## Check on several devices

Layout bugs live on particular devices. Instead of switching the Simulator by
hand, let [`playtest_devices`](api/playtest.md#unity_open_mcp_playtest_devices)
walk a list:

1. Open a session (`snapshot: true` when it will crawl) and bring the game to the
   screen to check — or to its first real screen for a crawl.
2. `playtest_devices {}` checks the current screen on the `common` preset (the
   smallest phone, the tallest, the deepest notch, the smallest tablet) in
   portrait; add `"orientations":["portrait","landscape"]`, pick `preset`
   (`phones`, `tablets`, `notch`, `all`) or name `devices`. `screens: "explore"`
   crawls the whole UI on each device instead (`explore` takes the crawler's
   options; the game restarts between devices). `jobs wait` the job id.
3. Read `matrix`: each finding with the devices that show it. A finding on the
   notch devices only is a safe-area problem; one on the small phone only is a
   size problem; one everywhere is the screen's own. Open the device frames.
4. `playtest_session end` — the report's Devices section has a row and a frame per
   device. The Game view is put back by the run itself.

On the demo fixture (menu screen): the "i" button sits under the notch of the
notch phones, the menu buttons overlap in landscape on the tall foldable, and the
small classic phone's buttons fall under 44 dp — each listed with its devices.

## Replay a flow after a change

A flow found once should not need a model to check again.
[`playtest_replay`](api/playtest.md#unity_open_mcp_playtest_replay) turns it into
a scenario file in the project and replays it as a regression check:

1. **Export once.** After a crawl, `playtest_replay {"action":"export","source":"crawl","scenario":"menu-tour"}`
   saves every tap of it — or `"to_screen":"S5"` only the shortest way to one
   screen. The crawl's screens are the baseline. To keep a flow you drove yourself
   (tap by label, typing, waits, project commands, checks), export
   `"source":"calls"` from your session instead, then run it once with
   `"record":true` to store the baseline. Passwords and PINs you typed become
   secret variables (`vars`).
2. **Review the file.** It is `Playtests/Scenarios/<name>.json` in the project,
   next to `Assets/` and versioned with the code. Read it like code: steps name
   controls by hierarchy path and label, `expect.texts` adds what must be on
   screen, `settings.ignore_errors` exempts a known console message. Commit it so
   the team runs the same check.
3. **Replay after a change.** In a session with an isolated save, run
   `playtest_replay {"scenario":"menu-tour"}` and `jobs wait` the job id. Each
   step finds its control, taps or types like a player, waits until the screen
   matches the expected one and checks it.
4. **Read the verdict.** `passed`; `failed` names the step, the `reason`
   (`target_not_found` with the controls on screen instead, `screen_changed` with
   what was added and removed, `console_error`, `new_finding`…) and its frame;
   `flaky` means a failure that did not repeat on the retry — report it as flaky,
   not broken. `drift` lists changes that still passed: a renamed button, a moved
   control, a relabelled screen.
5. **Accept an intended change** by running again with `"record":true` (it writes
   the new baseline into the file) and showing the file's diff in the review.
6. **Check how screens look, too** (optional): run once with
   `"record":true,"visual":true` on the device the team checks on. Every step's
   frame is stored beside the scenario (`<name>.visual/<platform-device>/`) and
   later runs compare each screen as a picture: a moved button, a missing icon, a
   wrong colour or a broken background fails `visual_changed` with where it
   changed, the controls there, and a baseline · now · diff strip in the report.
   Mask what moves on its own in `settings.visual.mask` (a banner, a particle
   area); counters with digits are masked already. Record per platform and
   device: a run on another one reports the pictures as unbaselined.

On the demo fixture: export a crawl of the explore scene, replay it (it passes),
then set PlayerPrefs `ExploreFixture.Variant` to `regression` and replay again —
"Shop" reads "Store" (drift) and Credits no longer opens: the replay fails at that
step with `screen_changed`, the same in both attempts. With a visual baseline the
same variant fails already on the menu (0.09 % changed around "Store"), and the
`restyle` variant — only the Settings panel repainted, the same controls and
texts — fails at the Settings step, which only the picture check can see.

## Keep what a playtest learned

Every session otherwise starts from zero: how to boot, which screens exist, which
buttons wipe the save, which findings the team already accepted.
[`playtest_memory`](api/playtest.md#unity_open_mcp_playtest_memory) keeps that in
the project, next to the scenarios, for the next session and the rest of the team:

- **Notes** (`Playtests/NOTES.md`) — `playtest_session start` returns them, so
  the next agent reads them first. Add a fact a later session can act on with
  `playtest_memory {"action":"note","section":"Boot","text":"…"}` (sections such
  as Boot, Dangerous controls, Known issues, Flaky spots; a date is added). After
  a crawl, `{"action":"map"}` writes its screen map — each screen and the labels
  tapped to reach it — as a section the next map replaces.
- **Profile** (`Playtests/profile.json`) — settings the tools apply on their own,
  so you stop repeating them: a project deny-list
  (`{"action":"add","profile":{"explore":{"deny":["Reset progress"]}}}`), the boot
  command as the crawler's restart, the team's devices, a stricter touch size,
  localised back labels (`"back_labels":["Назад"]`), console noise to ignore, and
  findings that are not bugs (`"known":[{"kind":"small_touch_target","path":"Settings/Close","reason":"…"}]`
  — listed as known with the reason, not counted, and never a replay's new
  finding). A call's own arguments still win; lists add up.

Both are plain files: review and commit them with the code. A profile with a
mistake (a bad regex, an unknown key) makes the tools refuse with
`invalid_profile` rather than crawl without its deny-list; `set` / `add` check a
change before they write it.

On the demo: `demo/Playtests/NOTES.md` describes the fixtures (boot, the planted
problems, the variants). Add a profile
(`{"action":"add","profile":{"known":[{"kind":"dead_button","path":"Settings/HelpButton","reason":"planted"}]}}`)
and crawl the explore fixture again: the dead button is listed under known, the
counts drop by one.

## Reading the report

`report.md` sits in the session folder
(`<app data>/unity-open-mcp/playtest-sessions/<company>/<product>/<session>/report/`)
with `frames/` (full-size PNGs) and `thumbs/` next to it, so the folder can be
zipped or attached as is. In order: a header (device, snapshot, isolation hook,
totals, verdicts), **Findings** (verdicts, detector findings, errors), the
**Exploration** of a crawl, the **Devices** of a device run, the **Replays** (a step table per run with the failing step's frame) and the **Screen checks** when they ran, one **Step** per mark (its calls with arguments,
results and durations; the errors it caused; thumbnails linking to the frames), the
**Errors** list with stacks, the **State diff**, and the **Editor events**
(play-mode transitions, domain reloads). Secrets in arguments, notes and console
text are redacted. See [the report contract](api/playtest.md#report).

## Known traps

- **One frame lies about animation.** Tweens, popups and "breathing" effects
  need a series (or `inputsim_gesture` `capture_at_ms` for a tap and what
  follows); a single screenshot catches an arbitrary moment.
- **Captures name their device.** Set the device or resolution before capturing;
  a hidden Game view tab does not render (`render_target_unavailable`, or stale
  frames), and the game falls back to a default size.
- **Never mutate project assets during a playtest.** No scene saves, prefab
  edits or writing `execute_csharp` snippets mid-session: they change what is
  being tested and are not player actions.
- **Paid and one-time actions.** Purchases, first-clear rewards, tutorials and
  unlocks change the save — isolate the state before the first tap.
- **A domain reload in play mode** (a script change, `RequestScriptReload`)
  can reset game statics; the session and its log survive (the report shows the
  reload under Editor events). Check the game state after it.
- **Long waits become jobs.** A wait or series over 40 s — or one that runs
  slower than its schedule, such as a `gameplay` series at a low
  `Time.timeScale` — returns a `job_id` (`status: "running"`, with the frames so
  far); `jobs wait` it, do not start it again — the recorder logs the job's result
  when it is terminal. Only zero progress for 15 s (a paused Editor,
  `Time.timeScale` 0) ends it as `capture_series_stalled` / `wait_stalled`, with
  `error.reason` and the frames captured so far.
- **The MCP server restarted.** The session is on disk: `playtest_session
  status` lists it under `orphaned`; `end` (or `resume`) with `session: "<id>"`
  takes it over, and the report names the call that was in flight.
- **One playtest per Editor.** The console is shared: errors are matched to your
  calls by time, so another agent driving the same Editor shows up in your report.

## A complete run on the demo

The demo walkthrough, as an agent issues it (open
`Assets/Fixtures/Playtest/PlaytestFixture.unity` first):

```text
playtest_session   {"action":"start","name":"Demo smoke","device":"Game view, Free Aspect","snapshot":true}
project_commands   {"action":"invoke","command_id":"project.demo.playtest.begin","args":{}}
playtest_session   {"action":"mark","label":"Isolate the save","verdict":"ok"}
editor_set_state   {"state":"play"}                       → editor_status {"wait_until":"playing"}
project_commands   {"action":"invoke","command_id":"project.demo.playtest.boot","args":{}}
ui_snapshot        {}
playtest_session   {"action":"mark","label":"Boot to the main menu","verdict":"ok"}
inputsim_pointer   {"action":"click","target_text":"Start Wave"}
playtest_wait      {"condition":{"log":{"pattern":"Wave 1 cleared"}},"timeout_ms":15000}
capture_series     {"start_on":{"log":{"pattern":"Reward popup opening"}},"at_ms":[0,100,200,300,400,500],"contact_sheet":true,"scale":0.5}
playtest_session   {"action":"mark","label":"Clear wave 1","verdict":"ok"}
inputsim_pointer   {"action":"click","target_text":"Report Bug"}   → the fixture throws on purpose
playtest_session   {"action":"mark","label":"Tap Report Bug","verdict":"bug","note":"Throws InvalidOperationException"}
playtest_session   {"action":"end"}                       → report path + summary
editor_set_state   {"state":"stop"}
project_commands   {"action":"invoke","command_id":"project.demo.playtest.end","args":{}}
player_state       {"action":"diff","name":"playtest-<session>"} → identical: true; then delete it
```

The report has one step per mark, the series' contact sheet, the exception under
the Report Bug step with its stack, and the state diff (the isolated slot's keys
added; the developer's slot untouched — after `playtest.end` the diff against the
snapshot is identical).
