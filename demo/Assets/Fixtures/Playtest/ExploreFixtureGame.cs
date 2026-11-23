using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityOpenMcpDemo.Fixtures
{
    // A small menu-driven "game" for the UI crawler (playtest_explore) and the
    // screen check (playtest_session check). It builds its own uGUI (legacy
    // UI.Text) with a known screen graph and known problems:
    //   Menu      Play → Level, Shop → Shop, Settings → Settings, "Buy Gems" (money:
    //             never tapped), Daily → a reward popup, News → News; Daily and News
    //             overlap (overlapping_controls); a tiny "i" button (touch size).
    //   Level     Pause → a pause popup over the level (Resume closes it; "Quit" is
    //             never tapped), Back → Menu.
    //   Shop      a price button "$1.99 Starter Pack" (never tapped), a product name
    //             that overflows its box (text_overflow), Close → Menu.
    //   Settings  a Vibration toggle, an unresolved-key text (placeholder_text),
    //             Help does nothing (dead_button), Report throws (console_error),
    //             Profile → Profile, Credits → Credits, Back → Menu.
    //   Profile   a Nickname field (12 characters; logs on end edit), a PIN field
    //             (digits only, 4, masked), Save → Settings only once a nickname
    //             is set (inputsim_text; the crawler fills fields first), Back.
    //   Credits   nothing to tap (dead_end).
    //   News      Close → Menu.
    // State and references are serialized so a domain reload in play mode keeps it;
    // listeners are re-wired in OnEnable.
    public sealed class ExploreFixtureGame : MonoBehaviour
    {
        [SerializeField] private bool built;
        [SerializeField] private string screen = "menu";
        [SerializeField] private GameObject menu, level, pausePopup, shop, settings, credits, news, dailyPopup, profile;
        [SerializeField] private Button play, shopButton, settingsButton, buyGems, daily, newsButton, info;
        [SerializeField] private Button pause, levelBack, resume, quit;
        [SerializeField] private Button starterPack, shopClose;
        [SerializeField] private Button help, report, creditsButton, settingsBack;
        [SerializeField] private Button newsClose, claim;
        [SerializeField] private Button profileButton, save, profileBack;
        [SerializeField] private InputField nickname, pin;

        public string Screen => screen;

        private void Awake()
        {
            if (!built) Build();
            Show("menu");
        }

        private void OnEnable() => Wire();

        public void Show(string next)
        {
            screen = next;
            menu.SetActive(next == "menu" || next == "daily");
            dailyPopup.SetActive(next == "daily");
            level.SetActive(next == "level" || next == "pause");
            pausePopup.SetActive(next == "pause");
            shop.SetActive(next == "shop");
            settings.SetActive(next == "settings");
            credits.SetActive(next == "credits");
            news.SetActive(next == "news");
            profile.SetActive(next == "profile");
        }

        private void Wire()
        {
            if (!built) return;
            Bind(play, () => Show("level"));
            Bind(shopButton, () => Show("shop"));
            Bind(settingsButton, () => Show("settings"));
            Bind(buyGems, () => Debug.LogWarning("[ExploreFixture] Buy Gems was tapped: the crawler's deny-list should have stopped it"));
            Bind(daily, () => Show("daily"));
            Bind(newsButton, () => Show("news"));
            Bind(info, () => Debug.Log("[ExploreFixture] Info"));
            Bind(pause, () => Show("pause"));
            Bind(levelBack, () => Show("menu"));
            Bind(resume, () => Show("level"));
            Bind(quit, () => Debug.LogWarning("[ExploreFixture] Quit was tapped: the crawler's deny-list should have stopped it"));
            Bind(starterPack, () => Debug.LogWarning("[ExploreFixture] A price button was tapped"));
            Bind(shopClose, () => Show("menu"));
            Bind(help, () => { });
            Bind(report, () => throw new System.InvalidOperationException("[ExploreFixture] Deliberate exception from the Report button"));
            Bind(creditsButton, () => Show("credits"));
            Bind(settingsBack, () => Show("menu"));
            Bind(newsClose, () => Show("menu"));
            Bind(claim, () => { Debug.Log("[ExploreFixture] Daily reward claimed"); Show("menu"); });
            Bind(profileButton, () => Show("profile"));
            Bind(save, () =>
            {
                if (string.IsNullOrEmpty(nickname.text)) return;
                Debug.Log("[ExploreFixture] Profile saved for " + nickname.text);
                Show("settings");
            });
            Bind(profileBack, () => Show("settings"));
            nickname.onEndEdit.RemoveAllListeners();
            nickname.onEndEdit.AddListener(value => Debug.Log("[ExploreFixture] Nickname set to " + value));
        }

        private static void Bind(Button button, UnityEngine.Events.UnityAction action)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        // ---- UI construction ----

        private void Build()
        {
            built = true;
            if (FindFirstObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var canvasGo = new GameObject("ExploreCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;

            menu = Panel(root, "Menu", new Color(0.10f, 0.20f, 0.30f));
            Label(menu.transform, "Title", "Explore Fixture", 0.86f, 80);
            play = MakeButton(menu.transform, "PlayButton", "Play", 0.72f);
            shopButton = MakeButton(menu.transform, "ShopButton", "Shop", 0.62f);
            settingsButton = MakeButton(menu.transform, "SettingsButton", "Settings", 0.52f);
            buyGems = MakeButton(menu.transform, "BuyGemsButton", "Buy Gems", 0.42f);
            daily = MakeButton(menu.transform, "DailyButton", "Daily", 0.26f, 0.40f, 300);
            newsButton = MakeButton(menu.transform, "NewsButton", "News", 0.26f, 0.55f, 300);
            info = MakeButton(menu.transform, "InfoButton", "i", 0.95f, 0.94f, 40, 40);

            dailyPopup = Popup(root, "DailyPopup");
            Label(dailyPopup.transform.GetChild(1), "Title", "Daily reward", 0.75f, 64, Color.black);
            claim = MakeButton(dailyPopup.transform.GetChild(1), "ClaimButton", "Claim", 0.30f);

            level = Panel(root, "Level", new Color(0.18f, 0.12f, 0.10f));
            Label(level.transform, "Title", "Level 1", 0.86f, 72);
            pause = MakeButton(level.transform, "PauseButton", "Pause", 0.60f);
            levelBack = MakeButton(level.transform, "BackButton", "Back", 0.20f);

            pausePopup = Popup(root, "PausePopup");
            Label(pausePopup.transform.GetChild(1), "Title", "Paused", 0.75f, 64, Color.black);
            resume = MakeButton(pausePopup.transform.GetChild(1), "ResumeButton", "Resume", 0.45f);
            quit = MakeButton(pausePopup.transform.GetChild(1), "QuitButton", "Quit", 0.20f);

            shop = Panel(root, "Shop", new Color(0.12f, 0.22f, 0.12f));
            Label(shop.transform, "Title", "Shop", 0.86f, 72);
            var product = Label(shop.transform, "ProductName", "Mega Bundle of Absolutely Everything Forever", 0.70f, 52);
            product.horizontalOverflow = HorizontalWrapMode.Wrap;
            product.verticalOverflow = VerticalWrapMode.Truncate;
            ((RectTransform)product.transform).sizeDelta = new Vector2(300, 70);
            starterPack = MakeButton(shop.transform, "StarterPackButton", "$1.99 Starter Pack", 0.50f);
            shopClose = MakeButton(shop.transform, "CloseButton", "Close", 0.20f);

            settings = Panel(root, "Settings", new Color(0.20f, 0.20f, 0.24f));
            Label(settings.transform, "Title", "Settings", 0.86f, 72);
            MakeToggle(settings.transform, "VibrationToggle", "Vibration", 0.74f);
            Label(settings.transform, "LanguageLabel", "settings_language_label", 0.66f, 44);
            help = MakeButton(settings.transform, "HelpButton", "Help", 0.56f);
            report = MakeButton(settings.transform, "ReportButton", "Report", 0.46f);
            creditsButton = MakeButton(settings.transform, "CreditsButton", "Credits", 0.36f);
            profileButton = MakeButton(settings.transform, "ProfileButton", "Profile", 0.26f);
            settingsBack = MakeButton(settings.transform, "BackButton", "Back", 0.16f);

            profile = Panel(root, "Profile", new Color(0.12f, 0.18f, 0.26f));
            Label(profile.transform, "Title", "Profile", 0.86f, 72);
            nickname = MakeField(profile.transform, "NicknameField", "Nickname", 0.68f);
            nickname.characterLimit = 12;
            pin = MakeField(profile.transform, "PinField", "PIN", 0.54f);
            pin.contentType = InputField.ContentType.Pin;
            pin.characterLimit = 4;
            save = MakeButton(profile.transform, "SaveButton", "Save", 0.36f);
            profileBack = MakeButton(profile.transform, "BackButton", "Back", 0.16f);

            credits = Panel(root, "Credits", new Color(0.05f, 0.05f, 0.05f));
            Label(credits.transform, "Title", "Credits", 0.80f, 72);
            Label(credits.transform, "Body", "Made by the demo team", 0.60f, 48);

            news = Panel(root, "News", new Color(0.25f, 0.15f, 0.30f));
            Label(news.transform, "Title", "News", 0.86f, 72);
            Label(news.transform, "Body", "Patch notes: crawler support", 0.60f, 44);
            newsClose = MakeButton(news.transform, "CloseButton", "Close", 0.20f);

            Wire();
        }

        private static Font UiFont => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static GameObject Panel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            Stretch((RectTransform)go.transform);
            return go;
        }

        // A modal popup: a full-screen dimmer that blocks taps behind it (child 0)
        // and a centered card (child 1).
        private static GameObject Popup(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Stretch((RectTransform)go.transform);
            var dim = new GameObject("Dimmer", typeof(RectTransform), typeof(Image));
            dim.transform.SetParent(go.transform, false);
            dim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            Stretch((RectTransform)dim.transform);
            var card = new GameObject("Card", typeof(RectTransform), typeof(Image));
            card.transform.SetParent(go.transform, false);
            card.GetComponent<Image>().color = new Color(0.95f, 0.85f, 0.35f);
            var rt = (RectTransform)card.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(800, 900);
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
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

        private static Button MakeButton(Transform parent, string name, string text, float y, float x = 0.5f, float width = 560, float height = 120)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.25f, 0.55f, 0.95f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(x, y);
            rt.sizeDelta = new Vector2(width, height);
            var label = (RectTransform)Label(go.transform, "Label", text, 0.5f, height < 80 ? 28 : 48).transform;
            Stretch(label);
            return go.GetComponent<Button>();
        }

        private static InputField MakeField(Transform parent, string name, string placeholderText, float y)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = Color.white;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, y);
            rt.sizeDelta = new Vector2(560, 100);
            var text = Label(go.transform, "Text", "", 0.5f, 44, Color.black);
            text.supportRichText = false;
            text.alignment = TextAnchor.MiddleLeft;
            Stretch((RectTransform)text.transform);
            var placeholder = Label(go.transform, "Placeholder", placeholderText, 0.5f, 44, new Color(0f, 0f, 0f, 0.45f));
            placeholder.alignment = TextAnchor.MiddleLeft;
            Stretch((RectTransform)placeholder.transform);
            var field = go.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            return field;
        }

        private static Toggle MakeToggle(Transform parent, string name, string text, float y)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Toggle));
            go.transform.SetParent(parent, false);
            var background = go.GetComponent<Image>();
            background.color = new Color(0.85f, 0.85f, 0.85f);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, y);
            rt.sizeDelta = new Vector2(560, 100);
            var check = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            check.transform.SetParent(go.transform, false);
            check.GetComponent<Image>().color = new Color(0.2f, 0.7f, 0.3f);
            var crt = (RectTransform)check.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.08f, 0.5f);
            crt.sizeDelta = new Vector2(60, 60);
            var label = (RectTransform)Label(go.transform, "Label", text, 0.5f, 44, Color.black).transform;
            Stretch(label);
            var toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = background;
            toggle.graphic = check.GetComponent<Image>();
            toggle.isOn = true;
            return toggle;
        }
    }
}
