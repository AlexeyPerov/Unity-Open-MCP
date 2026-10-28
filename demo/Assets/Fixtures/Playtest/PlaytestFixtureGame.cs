using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityOpenMcpDemo.Fixtures
{
    // A tiny "game" for the playtest session walkthrough. It builds its own uGUI
    // (legacy UI.Text, readable by ui_snapshot) and plays like a minimal wave
    // game, all on the game clock:
    //   launcher  — a dev launcher screen the project's boot command skips;
    //   menu      — "Coins: N", "Start Wave", "Report Bug" (throws on purpose);
    //   wave      — enemies PlaytestEnemy_1..N disappear every enemyInterval s;
    //               when the last one goes: "Wave N cleared" is logged, the coins
    //               reward is saved to PlayerPrefs in the active save slot, and
    //               rewardDelay s later the reward popup tweens its scale 0 → 1
    //               over rewardPopupSeconds ("Reward popup opening" is logged);
    //   "Collect" closes the popup back to the menu.
    // The save slot (PlayerPrefs playtest_fixture.slot, default "main") is what
    // the project's playtest.begin / playtest.end commands switch, so a playtest
    // never touches the developer's own save. State and references are
    // serialized so a domain reload in play mode keeps the game running; button
    // listeners are re-wired in OnEnable.
    public sealed class PlaytestFixtureGame : MonoBehaviour
    {
        public const string SlotKey = "playtest_fixture.slot";
        public const string DefaultSlot = "main";
        public const string EnemyPrefix = "PlaytestEnemy";

        public static string Key(string slot, string name) => "playtest_fixture." + slot + "." + name;
        public static string ActiveSlot => PlayerPrefs.GetString(SlotKey, DefaultSlot);

        public static PlaytestFixtureGame Instance { get; private set; }

        public int enemiesPerWave = 3;
        public float enemyInterval = 0.5f;
        public int waveReward = 10;
        public float rewardDelay = 0.5f;
        public float rewardPopupSeconds = 0.4f;

        [SerializeField] private string screen = "launcher";
        [SerializeField] private string slot = DefaultSlot;
        [SerializeField] private int coins;
        [SerializeField] private int wave;
        [SerializeField] private int alive;
        [SerializeField] private float waveTimer;
        [SerializeField] private float rewardTimer = -1f;
        [SerializeField] private float tween = -1f;
        [SerializeField] private bool built;

        [SerializeField] private GameObject launcherPanel, menuPanel, wavePanel, rewardPanel;
        [SerializeField] private Text coinsLabel, waveLabel, enemiesLabel, statusLabel, rewardLabel;
        [SerializeField] private RectTransform rewardPopup;
        [SerializeField] private GameObject[] enemies;
        [SerializeField] private Button devMenuButton, startWaveButton, reportBugButton, collectButton;

        public string Screen => screen;
        public string Slot => slot;
        public int Coins => coins;
        public int Wave => wave;
        public int Alive => alive;

        private void Awake()
        {
            if (!built) Build();
            UseSlot(ActiveSlot);
            Show("launcher");
        }

        private void OnEnable()
        {
            Instance = this;
            Wire();
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        // ---- the project's own entry points (called by its project commands) ----

        public void Boot()
        {
            Show("menu");
            Debug.Log("[PlaytestFixture] Booted to the main menu (slot " + slot + ", coins " + coins + ")");
        }

        public void UseSlot(string newSlot)
        {
            slot = string.IsNullOrEmpty(newSlot) ? DefaultSlot : newSlot;
            coins = PlayerPrefs.GetInt(Key(slot, "coins"), 0);
            Refresh();
        }

        public string StateJson()
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"playing\":true,\"screen\":\"").Append(screen).Append("\",\"slot\":\"").Append(slot)
              .Append("\",\"coins\":").Append(coins).Append(",\"wave\":").Append(wave).Append(",\"alive\":").Append(alive)
              .Append(",\"reward_popup\":").Append(rewardPanel != null && rewardPanel.activeSelf ? "true" : "false").Append('}');
            return sb.ToString();
        }

        // ---- gameplay ----

        public void StartWave()
        {
            wave++;
            alive = enemiesPerWave;
            waveTimer = 0f;
            for (int i = 0; i < enemies.Length; i++) enemies[i].SetActive(i < alive);
            Show("wave");
            Debug.Log("[PlaytestFixture] Wave " + wave + " started with " + alive + " enemies");
        }

        public void Collect()
        {
            Show("menu");
            Debug.Log("[PlaytestFixture] Reward collected");
        }

        // A deliberate game exception (the "Report Bug" button).
        public void ReportBug()
        {
            throw new System.InvalidOperationException("[PlaytestFixture] Deliberate exception from the Report Bug button");
        }

        private void Update()
        {
            if (screen == "wave" && alive > 0)
            {
                waveTimer += Time.deltaTime;
                int left = Mathf.Max(0, enemiesPerWave - Mathf.FloorToInt(waveTimer / enemyInterval));
                if (left != alive)
                {
                    alive = left;
                    for (int i = 0; i < enemies.Length; i++) enemies[i].SetActive(i < alive);
                    Refresh();
                    if (alive == 0) ClearWave();
                }
            }
            if (rewardTimer >= 0f)
            {
                rewardTimer += Time.deltaTime;
                if (rewardTimer >= rewardDelay)
                {
                    rewardTimer = -1f;
                    tween = 0f;
                    rewardPopup.localScale = Vector3.zero;
                    Show("reward");
                    Debug.Log("[PlaytestFixture] Reward popup opening");
                }
            }
            if (tween >= 0f)
            {
                tween += Time.deltaTime;
                float s = Mathf.Clamp01(tween / rewardPopupSeconds);
                rewardPopup.localScale = new Vector3(s, s, 1f);
                if (s >= 1f) tween = -1f;
            }
        }

        private void ClearWave()
        {
            coins += waveReward;
            PlayerPrefs.SetInt(Key(slot, "coins"), coins);
            PlayerPrefs.SetInt(Key(slot, "best_wave"), Mathf.Max(wave, PlayerPrefs.GetInt(Key(slot, "best_wave"), 0)));
            statusLabel.text = "Wave " + wave + " cleared";
            rewardLabel.text = "Reward +" + waveReward + " coins";
            rewardTimer = 0f;
            Refresh();
            Debug.Log("[PlaytestFixture] Wave " + wave + " cleared (+" + waveReward + " coins, slot " + slot + ")");
        }

        private void Show(string next)
        {
            screen = next;
            launcherPanel.SetActive(next == "launcher");
            menuPanel.SetActive(next == "menu");
            wavePanel.SetActive(next == "wave" || next == "reward");
            rewardPanel.SetActive(next == "reward");
            if (next == "wave") statusLabel.text = "Fight!";
            Refresh();
        }

        private void Refresh()
        {
            if (coinsLabel == null) return;
            coinsLabel.text = "Coins: " + coins;
            waveLabel.text = "Wave " + Mathf.Max(1, wave);
            enemiesLabel.text = "Enemies left: " + alive;
        }

        // ---- UI construction ----

        private void Wire()
        {
            if (!built) return;
            Bind(devMenuButton, () => Debug.Log("[PlaytestFixture] Dev launcher button pressed"));
            Bind(startWaveButton, StartWave);
            Bind(reportBugButton, ReportBug);
            Bind(collectButton, Collect);
        }

        private static void Bind(Button button, UnityEngine.Events.UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        private void Build()
        {
            built = true;
            if (FindFirstObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var canvasGo = new GameObject("PlaytestCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;

            launcherPanel = Panel(root, "Launcher", new Color(0.12f, 0.12f, 0.16f));
            Label(launcherPanel.transform, "LauncherTitle", "Dev Launcher", 0.62f, 72);
            Label(launcherPanel.transform, "LauncherHint", "Boot skips this screen", 0.54f, 40);
            devMenuButton = MakeButton(launcherPanel.transform, "DevMenuButton", "Open Dev Menu", 0.42f);

            menuPanel = Panel(root, "Menu", new Color(0.10f, 0.20f, 0.30f));
            Label(menuPanel.transform, "Title", "Playtest Fixture", 0.72f, 80);
            coinsLabel = Label(menuPanel.transform, "CoinsLabel", "Coins: 0", 0.62f, 56);
            startWaveButton = MakeButton(menuPanel.transform, "StartWaveButton", "Start Wave", 0.48f);
            reportBugButton = MakeButton(menuPanel.transform, "ReportBugButton", "Report Bug", 0.34f);

            wavePanel = Panel(root, "Wave", new Color(0.18f, 0.10f, 0.10f));
            waveLabel = Label(wavePanel.transform, "WaveLabel", "Wave 1", 0.80f, 72);
            enemiesLabel = Label(wavePanel.transform, "EnemiesLabel", "Enemies left: 0", 0.72f, 48);
            statusLabel = Label(wavePanel.transform, "StatusLabel", "Fight!", 0.64f, 48);
            enemies = new GameObject[Mathf.Max(1, enemiesPerWave)];
            for (int i = 0; i < enemies.Length; i++)
            {
                var enemy = new GameObject(EnemyPrefix + "_" + (i + 1), typeof(RectTransform), typeof(Image));
                enemy.transform.SetParent(wavePanel.transform, false);
                enemy.GetComponent<Image>().color = new Color(0.9f, 0.25f, 0.2f);
                var rt = (RectTransform)enemy.transform;
                float x = (i + 1f) / (enemies.Length + 1f);
                rt.anchorMin = rt.anchorMax = new Vector2(x, 0.45f);
                rt.sizeDelta = new Vector2(140, 140);
                enemies[i] = enemy;
            }

            rewardPanel = new GameObject("RewardPopup", typeof(RectTransform), typeof(Image));
            rewardPanel.transform.SetParent(root, false);
            rewardPanel.GetComponent<Image>().color = new Color(0.95f, 0.8f, 0.2f);
            rewardPopup = (RectTransform)rewardPanel.transform;
            rewardPopup.anchorMin = rewardPopup.anchorMax = new Vector2(0.5f, 0.45f);
            rewardPopup.sizeDelta = new Vector2(760, 520);
            rewardLabel = Label(rewardPanel.transform, "RewardLabel", "Reward +0 coins", 0.70f, 56, Color.black);
            collectButton = MakeButton(rewardPanel.transform, "CollectButton", "Collect", 0.25f);
        }

        private static Font UiFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static GameObject Panel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return go;
        }

        private static Text Label(Transform parent, string name, string text, float y, int size, Color? color = null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = UiFont;
            label.text = text;
            label.fontSize = size;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = color ?? Color.white;
            label.raycastTarget = false;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, y);
            rt.sizeDelta = new Vector2(900, size * 1.6f);
            return label;
        }

        private static Button MakeButton(Transform parent, string name, string text, float y)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.25f, 0.55f, 0.95f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, y);
            rt.sizeDelta = new Vector2(560, 140);
            var label = (RectTransform)Label(go.transform, "Label", text, 0.5f, 52).transform;
            label.anchorMin = Vector2.zero;
            label.anchorMax = Vector2.one;
            label.offsetMin = label.offsetMax = Vector2.zero;
            return go.GetComponent<Button>();
        }
    }
}
