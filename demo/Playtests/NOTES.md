# Playtest notes

What playtests of this project have learned. Agents read this file when a playtest session starts
(playtest_session start returns it) and add to it at the end (playtest_memory note / map); edit it freely.
Settings the tools apply on their own (deny-list, detector thresholds, accepted findings) are in profile.json next to it.

## Boot

- 2026-11-24 — Two fixture scenes: Assets/Fixtures/Playtest/PlaytestFixture.unity (wave game with a dev launcher; in play mode `project.demo.playtest.boot` skips the launcher to the main menu) and Assets/Fixtures/Playtest/ExploreFixture.unity (menu-driven UI for playtest_explore / playtest_replay; entering play mode lands on its Main Menu, no boot command needed).
- 2026-11-24 — Isolate the save with `project.demo.playtest.begin` (an empty 'playtest' slot) and finish with `project.demo.playtest.end`; or start the session with snapshot:true.

## Dangerous controls

- 2026-11-24 — ExploreFixture: "Buy Gems", "$1.99 Starter Pack" and the pause popup's "Quit" stand for money and leaving the game; the built-in deny-list skips them.

## Known issues

- 2026-11-24 — ExploreFixture has deliberate problems for the detectors: Daily and News overlap on narrow portrait screens (in a wide landscape Game view they touch by only ~8 %, under the 25 % threshold), the "i" button is under 44 dp, the Shop product name overflows, Settings shows an unresolved key, Help does nothing, Report throws "[ExploreFixture] Deliberate exception from the Report button", Credits is a dead end. They are expected findings, not regressions.
- 2026-11-24 — PlayerPrefs "ExploreFixture.Variant" = "regression" (Shop → "Store", Credits stops opening) or "restyle" (Settings painted red) switches the fixture into a changed build for replay checks; delete the key to go back.

## Flaky spots

## Screen map

<!-- Written from a crawl of the explore fixture; playtest_memory map replaces this section with a crawl's map. -->

9 screen(s) from the start screen 'Explore Fixture'. A path lists the labels tapped from the start screen.

- **Explore Fixture** — start; never tapped: 'Buy Gems'
- **Level 1** — start → 'Play'
- **Paused** — start → 'Play' → 'Pause'; never tapped: 'Quit'
- **Shop** — start → 'Shop'; never tapped: '$1.99 Starter Pack'
- **Settings** — start → 'Settings'
- **Credits** — start → 'Settings' → 'Credits'; dead end
- **Profile** — start → 'Settings' → 'Profile' (Save works once a nickname is typed)
- **Daily reward** — start → 'Daily' (a popup over the menu)
- **News** — start → 'News'
