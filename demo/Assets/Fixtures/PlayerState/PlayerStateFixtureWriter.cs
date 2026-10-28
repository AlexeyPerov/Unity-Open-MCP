using System.IO;
using UnityEngine;

namespace UnityOpenMcpDemo.Fixtures
{
    // Player-state fixture for unity_open_mcp_player_state: on Start it records
    // what the previous session left behind (so a restore can be proven by the
    // next play session reading the original values), then changes it the way a
    // game session does — an int, a float and a string PlayerPref and one JSON
    // save file in persistentDataPath. Like most games it does not call
    // PlayerPrefs.Save(): the values live in the Editor's in-memory cache until
    // something flushes them.
    public sealed class PlayerStateFixtureWriter : MonoBehaviour
    {
        public const string CoinsKey = "fixture.coins";
        public const string VolumeKey = "fixture.volume";
        public const string LastLevelKey = "fixture.last_level";
        public const string SaveFileName = "fixture_save.json";

        public int coinsReward = 50;
        public float volume = 0.75f;
        public string lastLevel = "level_3";

        // What this session read at Start (the state the previous one left).
        public int startCoins;
        public bool startHadCoins;
        public float startVolume;
        public bool startHadVolume;
        public string startLastLevel;
        public bool startHadLastLevel;
        public bool startSaveFileExisted;
        public string startSaveFile;
        public int writtenFrame = -1;

        private void Start()
        {
            startHadCoins = PlayerPrefs.HasKey(CoinsKey);
            startCoins = PlayerPrefs.GetInt(CoinsKey, 0);
            startHadVolume = PlayerPrefs.HasKey(VolumeKey);
            startVolume = PlayerPrefs.GetFloat(VolumeKey, -1f);
            startHadLastLevel = PlayerPrefs.HasKey(LastLevelKey);
            startLastLevel = PlayerPrefs.GetString(LastLevelKey, "");
            var path = Path.Combine(Application.persistentDataPath, SaveFileName);
            startSaveFileExisted = File.Exists(path);
            startSaveFile = startSaveFileExisted ? File.ReadAllText(path) : null;
            Debug.Log("[PlayerStateFixture] start coins=" + (startHadCoins ? startCoins.ToString() : "absent") +
                      " volume=" + (startHadVolume ? startVolume.ToString("R") : "absent") +
                      " last_level=" + (startHadLastLevel ? startLastLevel : "absent") +
                      " save_file=" + (startSaveFileExisted ? "present" : "absent"));

            PlayerPrefs.SetInt(CoinsKey, startCoins + coinsReward);
            PlayerPrefs.SetFloat(VolumeKey, volume);
            PlayerPrefs.SetString(LastLevelKey, lastLevel);
            writtenFrame = Time.frameCount;
            File.WriteAllText(path,
                "{\"coins\":" + (startCoins + coinsReward) + ",\"level\":\"" + lastLevel + "\",\"session\":" +
                (startSaveFileExisted ? 2 : 1) + "}");
            Debug.Log("[PlayerStateFixture] wrote coins=" + (startCoins + coinsReward) + " volume=" + volume.ToString("R") +
                      " last_level=" + lastLevel + " file=" + SaveFileName + " at frame " + writtenFrame);
        }
    }
}
