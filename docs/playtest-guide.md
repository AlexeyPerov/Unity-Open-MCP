# Playtest guide

How an agent runs a smoke playtest of a Unity game through Unity Open MCP the way
a QA engineer would — boot the game, read the screen, tap and drag like a finger,
wait for things to happen, look at the frame on a target device — and hands back
a report a human can review: every step, the frame it showed, the errors it
caused and a verdict.

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

### 2. Boot

Enter play mode (`editor_set_state play`, then `editor_status` until playing),
then boot through the project's command (`playtest.boot`). Do not reflect into
launchers, singletons or save systems with `execute_csharp` — that tests the
reflection, not the game, and a recorded playtest should only contain what a
player could do.

### 3. Loop: read → act → wait → look → mark

1. **Read** the screen: `ui_snapshot` (compact; page if needed).
2. **Act** like a player: `inputsim_pointer` / `inputsim_touch` with
   `target_text: "Buy"` (`index` / `under` when the label repeats), or
   `inputsim_gesture` for drags, swipes and holds over real frames.
3. **Wait** for the game, never with a loop of calls: one `playtest_wait` with a
   condition — a log line, a `ui` query (`matches: false` for "the popup closed"),
   an object, the project's own read-only predicate (`state_dump` with `field`),
   `frames` / `ms` — and a `timeout_ms`. `time_scale` fast-forwards for the wait
   only.
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

## Reading the report

`report.md` sits in the session folder
(`<app data>/unity-open-mcp/playtest-sessions/<company>/<product>/<session>/report/`)
with `frames/` (full-size PNGs) and `thumbs/` next to it, so the folder can be
zipped or attached as is. In order: a header (device, snapshot, isolation hook,
totals, verdicts), **Findings**, one **Step** per mark (its calls with arguments,
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
- **Long waits become jobs.** A wait or series over 40 s returns a `job_id`;
  `jobs wait` it — the recorder logs the job's result when it is terminal.
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
editor_set_state   {"state":"play"}                       → editor_status until playing
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
