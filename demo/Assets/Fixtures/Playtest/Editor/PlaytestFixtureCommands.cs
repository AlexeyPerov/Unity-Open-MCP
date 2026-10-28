using System;
using UnityEditor;
using UnityEngine;
using UnityOpenMcpBridge;

namespace UnityOpenMcpDemo.Fixtures.EditorCommands
{
    // The project-side half of an agent playtest: the commands a game exposes so
    // an agent boots it, reads its state and isolates its save through the
    // project's own code instead of reflecting into launchers or save systems.
    // They live in an Editor folder of the game (Assembly-CSharp-Editor), so they
    // call the game's types directly and the bridge discovers them from
    // [BridgeToolType] + [ProjectCommand] — no MCP server change. Copy the shape:
    //
    //   project.<owner>.playtest.boot        play mode: skip dev launchers, land on the first real screen
    //   project.<owner>.playtest.state_dump  read-only: what the player would see / own, as JSON
    //   project.<owner>.playtest.begin       switch the game to an isolated save (a test profile / slot)
    //   project.<owner>.playtest.end         drop that save and switch back to the developer's
    //
    // begin / end write only the game's own PlayerPrefs (no project asset), so
    // they are not project mutations — and not read-only either. Agents find
    // them with project_commands(action: "list", query: "playtest").
    [BridgeToolType]
    public static class PlaytestFixtureCommands
    {
        private const string Scene = "Assets/Fixtures/Playtest/PlaytestFixture.unity";
        private const string IsolatedSlot = "playtest";
        private static readonly string[] SlotKeys = { "coins", "best_wave" };

        [ProjectCommand("project.demo.playtest.boot", Title = "Boot the playtest fixture",
            Description = "Play mode only: skip the fixture's dev launcher and land on the main menu, the way the game's own boot path does. Returns the game state.",
            Package = "demo.playtest", Group = "playtest", Tags = new[] { "demo", "playtest" },
            IdempotentHint = true, Gate = GateMode.Off)]
        public static string Boot()
        {
            var game = RunningGame();
            game.Boot();
            return game.StateJson();
        }

        [ProjectCommand("project.demo.playtest.state_dump", Title = "Playtest fixture state",
            Description = "Read-only: the fixture's screen, save slot, coins, wave and enemies left (play mode), or the saved slot and coins (edit mode).",
            Package = "demo.playtest", Group = "playtest", Tags = new[] { "demo", "playtest" },
            ReadOnlyHint = true, IdempotentHint = true, Gate = GateMode.Off)]
        public static string StateDump()
        {
            var game = PlaytestFixtureGame.Instance;
            if (EditorApplication.isPlaying && game != null) return game.StateJson();
            var slot = PlaytestFixtureGame.ActiveSlot;
            return "{\"playing\":" + (EditorApplication.isPlaying ? "true" : "false") + ",\"slot\":\"" + slot
                + "\",\"coins\":" + PlayerPrefs.GetInt(PlaytestFixtureGame.Key(slot, "coins"), 0) + "}";
        }

        [ProjectCommand("project.demo.playtest.begin", Title = "Begin an isolated playtest save",
            Description = "Switch the fixture's save to the isolated 'playtest' slot (PlayerPrefs playtest_fixture.playtest.*), empty unless reset is false. The developer's 'main' slot is not touched. Edit or play mode.",
            Package = "demo.playtest", Group = "playtest", Tags = new[] { "demo", "playtest" },
            Gate = GateMode.Off)]
        public static string Begin(
            [ProjectCommandParameter(Description = "Start the isolated slot empty (default true)")] bool reset = true)
        {
            if (reset) DeleteSlot(IsolatedSlot);
            PlayerPrefs.SetString(PlaytestFixtureGame.SlotKey, IsolatedSlot);
            PlayerPrefs.Save();
            if (PlaytestFixtureGame.Instance != null) PlaytestFixtureGame.Instance.UseSlot(IsolatedSlot);
            return "{\"slot\":\"" + IsolatedSlot + "\",\"reset\":" + (reset ? "true" : "false") + "}";
        }

        [ProjectCommand("project.demo.playtest.end", Title = "End the isolated playtest save",
            Description = "Delete the isolated 'playtest' slot and switch the fixture back to the developer's 'main' slot. Edit or play mode.",
            Package = "demo.playtest", Group = "playtest", Tags = new[] { "demo", "playtest" },
            DestructiveHint = true, Gate = GateMode.Off)]
        public static string End()
        {
            var deleted = DeleteSlot(IsolatedSlot);
            PlayerPrefs.DeleteKey(PlaytestFixtureGame.SlotKey);
            PlayerPrefs.Save();
            if (PlaytestFixtureGame.Instance != null) PlaytestFixtureGame.Instance.UseSlot(PlaytestFixtureGame.DefaultSlot);
            return "{\"slot\":\"" + PlaytestFixtureGame.DefaultSlot + "\",\"deleted\":" + deleted + "}";
        }

        private static PlaytestFixtureGame RunningGame()
        {
            if (!EditorApplication.isPlaying)
                throw new InvalidOperationException("Play mode required: open " + Scene + " and enter play mode first.");
            var game = PlaytestFixtureGame.Instance;
            if (game == null)
                throw new InvalidOperationException("The playtest fixture is not running: open " + Scene + " before entering play mode.");
            return game;
        }

        private static int DeleteSlot(string slot)
        {
            int deleted = 0;
            foreach (var name in SlotKeys)
            {
                var key = PlaytestFixtureGame.Key(slot, name);
                if (!PlayerPrefs.HasKey(key)) continue;
                PlayerPrefs.DeleteKey(key);
                deleted++;
            }
            return deleted;
        }
    }
}
