using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using GoingCooperative.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly MultiplayerMenuModel MultiplayerMenu = new MultiplayerMenuModel();
        private static readonly MultiplayerTransferTestState MultiplayerTransferTest = new MultiplayerTransferTestState();
        private GameObject? multiplayerCanvasRoot;
        private GameObject? multiplayerCanvasPanel;
        private GameObject? multiplayerCanvasContent;
        private GameObject? multiplayerCanvasEventSystem;
        private GameObject? multiplayerCanvasInGameHud;
        private GameObject? multiplayerResyncOverlay;
        private Text? multiplayerResyncPhaseText;
        private Text? multiplayerResyncDetailText;
        private Image? multiplayerResyncProgressFill;
        private Text? multiplayerCanvasConnectionText;
        private Text? multiplayerCanvasStatusValuesText;
        private Text? multiplayerCanvasMessageText;
        private Text? multiplayerCanvasTransferPhaseText;
        private Text? multiplayerCanvasTransferDetailText;
        private TextMeshProUGUI? multiplayerCanvasHudStatusText;
        private Image? multiplayerCanvasTransferFill;
        private Font? multiplayerCanvasFont;
        private TMP_FontAsset? multiplayerCanvasGameFont;
        private Button? multiplayerCanvasLauncherButton;
        private InputField? multiplayerCanvasHostInput;
        private InputField? multiplayerCanvasPortInput;
        private InputField? multiplayerCanvasSessionInput;
        private MultiplayerMenuPage multiplayerCanvasPage = MultiplayerMenuPage.Home;
        private bool multiplayerCanvasFailureLogged;
        private bool multiplayerCanvasOpenedAtUsableResolution;
        private int multiplayerCanvasScreenWidth;
        private int multiplayerCanvasScreenHeight;

        private static readonly Color MultiplayerCanvasBackdrop = new Color(0.035f, 0.043f, 0.05f, 0.97f);
        private static readonly Color MultiplayerCanvasPanel = new Color(0.075f, 0.087f, 0.095f, 1f);
        private static readonly Color MultiplayerCanvasCard = new Color(0.115f, 0.128f, 0.135f, 1f);
        private static readonly Color MultiplayerCanvasAccent = new Color(0.58f, 0.39f, 0.16f, 1f);
        private static readonly Color MultiplayerCanvasText = new Color(0.93f, 0.92f, 0.88f, 1f);
        private static readonly Color MultiplayerCanvasMuted = new Color(0.68f, 0.7f, 0.7f, 1f);

        private void UpdateMultiplayerCanvasGuiSafely()
        {
            try
            {
                UpdateMultiplayerCanvasGui();
            }
            catch (Exception ex)
            {
                if (!multiplayerCanvasFailureLogged)
                {
                    multiplayerCanvasFailureLogged = true;
                    LogReplicationWarning("Going Cooperative multiplayer Canvas UI failed error="
                        + ex.GetType().Name + ":" + ex.Message);
                }

                DestroyMultiplayerCanvasGui();
            }
        }

        private void UpdateMultiplayerCanvasGui()
        {
            if (!replicationConfigMultiplayerMenuEnabled)
            {
                DestroyMultiplayerCanvasGui();
                return;
            }

            if (multiplayerCanvasRoot != null
                && Screen.width >= 640
                && (multiplayerCanvasScreenWidth != Screen.width || multiplayerCanvasScreenHeight != Screen.height))
            {
                LogReplicationInfo("Going Cooperative multiplayer Canvas UI rebuilding for resolution="
                    + Screen.width.ToString(CultureInfo.InvariantCulture)
                    + "x" + Screen.height.ToString(CultureInfo.InvariantCulture)
                    + " previous=" + multiplayerCanvasScreenWidth.ToString(CultureInfo.InvariantCulture)
                    + "x" + multiplayerCanvasScreenHeight.ToString(CultureInfo.InvariantCulture));
                DestroyMultiplayerCanvasGui();
            }

            UpdateMultiplayerSteamRuntime();
            EnsureMultiplayerCanvasGui();
            EnsureMultiplayerCanvasEventSystem();
            UpdateMultiplayerSaveWorkflow();
            MultiplayerTransferTest.Update(Time.realtimeSinceStartup);
            UpdateMultiplayerTransferTestUi();
            UpdateMultiplayerInGameHud();
            UpdateMultiplayerResyncOverlay();
            if (!multiplayerCanvasOpenedAtUsableResolution && Screen.width >= 640 && Screen.height >= 480)
            {
                multiplayerCanvasOpenedAtUsableResolution = true;
                SetMultiplayerCanvasOpen(true);
                LogReplicationInfo("Going Cooperative multiplayer Canvas UI opened at usable resolution.");
            }
            if (Input.GetKeyDown(KeyCode.F8))
            {
                SetMultiplayerCanvasOpen(multiplayerCanvasPanel == null || !multiplayerCanvasPanel.activeSelf);
            }

            if (Input.GetKeyDown(KeyCode.Escape) && multiplayerCanvasPanel != null && multiplayerCanvasPanel.activeSelf)
            {
                if (multiplayerCanvasPage == MultiplayerMenuPage.Home)
                {
                    SetMultiplayerCanvasOpen(false);
                }
                else
                {
                    ShowMultiplayerCanvasPage(MultiplayerMenuPage.Home);
                }
            }

            if (multiplayerCanvasConnectionText != null)
            {
                multiplayerCanvasConnectionText.text = GetMultiplayerConnectionLabel();
            }

            if (multiplayerCanvasStatusValuesText != null)
            {
                multiplayerCanvasStatusValuesText.text = BuildMultiplayerCanvasStatusValues();
            }
        }

        private void EnsureMultiplayerCanvasGui()
        {
            if (replicationConfigUiV2)
            {
                EnsureMultiplayerCanvasGuiV2();
                return;
            }

            if (multiplayerCanvasRoot != null)
            {
                return;
            }

            // Unity 2022 removed Arial.ttf from the built-in resources. LegacyRuntime.ttf
            // is the supported runtime font and avoids a first-frame ArgumentException.
            multiplayerCanvasFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            multiplayerCanvasRoot = new GameObject("Going Cooperative Multiplayer Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            multiplayerCanvasScreenWidth = Screen.width;
            multiplayerCanvasScreenHeight = Screen.height;
            DontDestroyOnLoad(multiplayerCanvasRoot);
            var canvas = multiplayerCanvasRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var scaler = multiplayerCanvasRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var launcher = CreateMultiplayerCanvasButton(multiplayerCanvasRoot.transform, "Launcher", "MULTIPLAYER  [F8]", () => SetMultiplayerCanvasOpen(true), MultiplayerCanvasAccent);
            multiplayerCanvasLauncherButton = launcher;
            SetMultiplayerCanvasRect(launcher.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-278f, -66f), new Vector2(-24f, -22f));

            CreateMultiplayerInGameHud();

            multiplayerCanvasPanel = CreateMultiplayerCanvasImage(multiplayerCanvasRoot.transform, "Menu", MultiplayerCanvasBackdrop);
            SetMultiplayerCanvasRect(multiplayerCanvasPanel.GetComponent<RectTransform>(), new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.92f), Vector2.zero, Vector2.zero);

            var header = CreateMultiplayerCanvasImage(multiplayerCanvasPanel.transform, "Header", MultiplayerCanvasPanel);
            SetMultiplayerCanvasRect(header.GetComponent<RectTransform>(), new Vector2(0f, 0.88f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(header.transform, "Title", "GOING COOPERATIVE", 28, FontStyle.Bold, TextAnchor.MiddleLeft, MultiplayerCanvasText, new Vector2(0.03f, 0f), new Vector2(0.55f, 1f));
            multiplayerCanvasConnectionText = CreateMultiplayerCanvasText(header.transform, "Connection", GetMultiplayerConnectionLabel(), 15, FontStyle.Bold, TextAnchor.MiddleRight, MultiplayerCanvasText, new Vector2(0.58f, 0f), new Vector2(0.87f, 1f));
            var close = CreateMultiplayerCanvasButton(header.transform, "Close", "CLOSE", () => SetMultiplayerCanvasOpen(false), MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(close.GetComponent<RectTransform>(), new Vector2(0.89f, 0.22f), new Vector2(0.98f, 0.78f), Vector2.zero, Vector2.zero);

            var nav = CreateMultiplayerCanvasImage(multiplayerCanvasPanel.transform, "Navigation", MultiplayerCanvasPanel);
            SetMultiplayerCanvasRect(nav.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0.21f, 0.88f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasNavButton(nav.transform, "OVERVIEW", MultiplayerMenuPage.Home, 0.84f);
            CreateMultiplayerCanvasNavButton(nav.transform, "HOST GAME", MultiplayerMenuPage.Host, 0.71f);
            CreateMultiplayerCanvasNavButton(nav.transform, "JOIN GAME", MultiplayerMenuPage.Join, 0.58f);
            CreateMultiplayerCanvasNavButton(nav.transform, "STATUS", MultiplayerMenuPage.Status, 0.45f);
            CreateMultiplayerCanvasNavButton(nav.transform, "SETTINGS", MultiplayerMenuPage.Settings, 0.32f);
            CreateMultiplayerCanvasText(nav.transform, "Hint", "F8 opens or closes this menu.", 12, FontStyle.Normal, TextAnchor.LowerLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.04f), new Vector2(0.92f, 0.18f));

            multiplayerCanvasContent = CreateMultiplayerCanvasImage(multiplayerCanvasPanel.transform, "Content", MultiplayerCanvasBackdrop);
            SetMultiplayerCanvasRect(multiplayerCanvasContent.GetComponent<RectTransform>(), new Vector2(0.21f, 0f), new Vector2(1f, 0.88f), Vector2.zero, Vector2.zero);
            multiplayerCanvasPanel.SetActive(false);
            ShowMultiplayerCanvasPage(MultiplayerMenuPage.Home);
            CreateMultiplayerResyncOverlay();
            RunMultiplayerCanvasSmokeTest(launcher, nav, close);
            LogReplicationInfo("Going Cooperative multiplayer Canvas UI created screen=" + Screen.width.ToString(CultureInfo.InvariantCulture) + "x" + Screen.height.ToString(CultureInfo.InvariantCulture));
        }

        private void RunMultiplayerCanvasSmokeTest(Button launcher, GameObject navigation, Button close)
        {
            launcher.onClick.Invoke();
            if (multiplayerCanvasPanel == null || !multiplayerCanvasPanel.activeSelf)
            {
                throw new InvalidOperationException("launcher-did-not-open-menu");
            }

            InvokeMultiplayerCanvasSmokeNavigation(navigation, "HOST GAME", MultiplayerMenuPage.Host);
            if (multiplayerCanvasPortInput == null)
            {
                throw new InvalidOperationException("host-page-controls-missing");
            }
            if (multiplayerCanvasTransferFill == null)
            {
                throw new InvalidOperationException("host-transfer-progress-missing");
            }

            InvokeMultiplayerCanvasSmokeNavigation(navigation, "JOIN GAME", MultiplayerMenuPage.Join);
            if (multiplayerCanvasHostInput == null || multiplayerCanvasPortInput == null)
            {
                throw new InvalidOperationException("join-page-controls-missing");
            }

            InvokeMultiplayerCanvasSmokeNavigation(navigation, "STATUS", MultiplayerMenuPage.Status);
            if (multiplayerCanvasStatusValuesText == null)
            {
                throw new InvalidOperationException("status-page-values-missing");
            }
            if (multiplayerCanvasTransferFill == null)
            {
                throw new InvalidOperationException("status-resync-progress-missing");
            }

            InvokeMultiplayerCanvasSmokeNavigation(navigation, "SETTINGS", MultiplayerMenuPage.Settings);
            if (multiplayerCanvasHostInput == null || multiplayerCanvasPortInput == null)
            {
                throw new InvalidOperationException("settings-page-controls-missing");
            }

            close.onClick.Invoke();
            if (multiplayerCanvasPanel.activeSelf)
            {
                throw new InvalidOperationException("close-did-not-hide-menu");
            }

            if (multiplayerCanvasInGameHud == null
                || multiplayerCanvasInGameHud.transform.Find("Resync") == null)
            {
                throw new InvalidOperationException("in-game-hud-controls-missing");
            }

            if (!MultiplayerTransferTestState.RunSmokeTest(out var transferDetail))
            {
                throw new InvalidOperationException("transfer-state-smoke-test-failed " + transferDetail);
            }

            ShowMultiplayerCanvasPage(MultiplayerMenuPage.Home);
            LogReplicationInfo("Going Cooperative multiplayer Canvas UI smoke test passed pages=home,host,join,status,settings inputFields=ready navigation=ready transferProgress=ready inGameHud=ready.");
        }

        private void InvokeMultiplayerCanvasSmokeNavigation(GameObject navigation, string buttonName, MultiplayerMenuPage expectedPage)
        {
            var buttonTransform = navigation.transform.Find(buttonName);
            var button = buttonTransform == null ? null : buttonTransform.GetComponent<Button>();
            if (button == null)
            {
                throw new InvalidOperationException("navigation-button-missing " + buttonName);
            }

            button.onClick.Invoke();
            if (multiplayerCanvasPage != expectedPage)
            {
                throw new InvalidOperationException("navigation-page-mismatch " + buttonName);
            }
        }

        private void EnsureMultiplayerCanvasEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            if (multiplayerCanvasEventSystem == null)
            {
                multiplayerCanvasEventSystem = new GameObject(
                    "Going Cooperative Multiplayer Event System",
                    typeof(EventSystem),
                    typeof(StandaloneInputModule));
                DontDestroyOnLoad(multiplayerCanvasEventSystem);
                LogReplicationInfo("Going Cooperative multiplayer Canvas UI created fallback EventSystem.");
            }
        }

        private void CreateMultiplayerCanvasNavButton(Transform parent, string label, MultiplayerMenuPage page, float top)
        {
            var button = CreateMultiplayerCanvasButton(parent, label, label, () => ShowMultiplayerCanvasPage(page), MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(button.GetComponent<RectTransform>(), new Vector2(0.08f, top - 0.09f), new Vector2(0.92f, top), Vector2.zero, Vector2.zero);
        }

        private void ShowMultiplayerCanvasPage(MultiplayerMenuPage page)
        {
            if (replicationConfigUiV2)
            {
                ShowMultiplayerCanvasPageV2(page);
                return;
            }

            if (multiplayerCanvasContent == null)
            {
                return;
            }

            multiplayerCanvasPage = page;
            for (var i = multiplayerCanvasContent.transform.childCount - 1; i >= 0; i--)
            {
                Destroy(multiplayerCanvasContent.transform.GetChild(i).gameObject);
            }

            multiplayerCanvasHostInput = null;
            multiplayerCanvasPortInput = null;
            multiplayerCanvasSessionInput = null;
            multiplayerCanvasMessageText = null;
            multiplayerCanvasStatusValuesText = null;
            multiplayerCanvasTransferPhaseText = null;
            multiplayerCanvasTransferDetailText = null;
            multiplayerCanvasTransferFill = null;
            switch (page)
            {
                case MultiplayerMenuPage.Host:
                    BuildMultiplayerCanvasHostPage();
                    break;
                case MultiplayerMenuPage.Join:
                    BuildMultiplayerCanvasJoinPage();
                    break;
                case MultiplayerMenuPage.Status:
                    BuildMultiplayerCanvasStatusPage();
                    break;
                case MultiplayerMenuPage.Settings:
                    BuildMultiplayerCanvasSettingsPage();
                    break;
                default:
                    BuildMultiplayerCanvasHomePage();
                    break;
            }
        }

        private void BuildMultiplayerCanvasHomePage()
        {
            CreateMultiplayerCanvasHeading("Multiplayer", "Host a settlement or connect directly to another Going Cooperative player.");
            var hostCard = CreateMultiplayerCanvasImage(multiplayerCanvasContent!.transform, "Host Card", MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(hostCard.GetComponent<RectTransform>(), new Vector2(0.06f, 0.28f), new Vector2(0.47f, 0.7f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(hostCard.transform, "Title", "HOST A SETTLEMENT", 20, FontStyle.Bold, TextAnchor.UpperLeft, MultiplayerCanvasText, new Vector2(0.07f, 0.55f), new Vector2(0.93f, 0.9f));
            CreateMultiplayerCanvasText(hostCard.transform, "Body", "Run the authoritative simulation and accept a direct client connection.", 14, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.07f, 0.28f), new Vector2(0.93f, 0.6f));
            var hostButton = CreateMultiplayerCanvasButton(hostCard.transform, "Host", "HOST GAME", () => ShowMultiplayerCanvasPage(MultiplayerMenuPage.Host), MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(hostButton.GetComponent<RectTransform>(), new Vector2(0.07f, 0.08f), new Vector2(0.93f, 0.25f), Vector2.zero, Vector2.zero);

            var joinCard = CreateMultiplayerCanvasImage(multiplayerCanvasContent.transform, "Join Card", MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(joinCard.GetComponent<RectTransform>(), new Vector2(0.53f, 0.28f), new Vector2(0.94f, 0.7f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(joinCard.transform, "Title", "JOIN A SETTLEMENT", 20, FontStyle.Bold, TextAnchor.UpperLeft, MultiplayerCanvasText, new Vector2(0.07f, 0.55f), new Vector2(0.93f, 0.9f));
            CreateMultiplayerCanvasText(joinCard.transform, "Body", "Connect using the host computer's reachable LAN or VPN address.", 14, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.07f, 0.28f), new Vector2(0.93f, 0.6f));
            var joinButton = CreateMultiplayerCanvasButton(joinCard.transform, "Join", "JOIN GAME", () => ShowMultiplayerCanvasPage(MultiplayerMenuPage.Join), MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(joinButton.GetComponent<RectTransform>(), new Vector2(0.07f, 0.08f), new Vector2(0.93f, 0.25f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(multiplayerCanvasContent.transform, "Notice", "TEST VERSION · Both computers must load compatible copies of the same save.", 13, FontStyle.Normal, TextAnchor.MiddleCenter, MultiplayerCanvasMuted, new Vector2(0.08f, 0.12f), new Vector2(0.92f, 0.22f));
        }

        private void BuildMultiplayerCanvasHostPage()
        {
            EnsureDirectHostSessionCode();
            CreateMultiplayerCanvasHeading("Host Multiplayer Game", "Select the load that will be transferred to the joining player.");
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Selected Save", "SELECT LOAD:  " + GetSelectedMultiplayerSaveLabel(), 16, FontStyle.Bold, TextAnchor.MiddleCenter, MultiplayerCanvasText, new Vector2(0.12f, 0.68f), new Vector2(0.76f, 0.78f));
            var previousSave = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Previous Save", "<", SelectPreviousMultiplayerSave, MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(previousSave.GetComponent<RectTransform>(), new Vector2(0.78f, 0.68f), new Vector2(0.84f, 0.78f), Vector2.zero, Vector2.zero);
            var nextSave = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Next Save", ">", SelectNextMultiplayerSave, MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(nextSave.GetComponent<RectTransform>(), new Vector2(0.85f, 0.68f), new Vector2(0.91f, 0.78f), Vector2.zero, Vector2.zero);
            multiplayerCanvasPortInput = CreateMultiplayerCanvasInput("Listen port", MultiplayerMenu.PortText, 0.54f);
            if (replicationConfigDirectTransportSecurityV1)
            {
                multiplayerCanvasSessionInput = CreateMultiplayerCanvasInput("Session code", MultiplayerMenu.DirectSessionCode, 0.44f);
            }
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Preflight", "Share this address: " + GetMultiplayerLanAddressSummary() + ":" + MultiplayerMenu.PortText
                + "\nPlugin " + GoingCooperativeConstants.Version + "  ·  Protocol " + GetMultiplayerProtocolLabel()
                + "\nThe selected load will be checksummed, transferred, verified, and staged separately on the client.", 14, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.27f), new Vector2(0.92f, 0.42f));
            var start = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Start", "HOST", StartMultiplayerCanvasHost, MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(start.GetComponent<RectTransform>(), new Vector2(0.08f, 0.17f), new Vector2(0.34f, 0.26f), Vector2.zero, Vector2.zero);
            CreateMultiplayerTransferProgressUi(0.04f, 0.14f);
            CreateMultiplayerCanvasMessage();
        }

        private void BuildMultiplayerCanvasJoinPage()
        {
            CreateMultiplayerCanvasHeading("Join Multiplayer Game", "Enter the host computer's reachable address.");
            multiplayerCanvasHostInput = CreateMultiplayerCanvasInput("Host address", MultiplayerMenu.HostAddress, 0.7f);
            multiplayerCanvasPortInput = CreateMultiplayerCanvasInput("Port", MultiplayerMenu.PortText, 0.54f);
            if (replicationConfigDirectTransportSecurityV1)
            {
                multiplayerCanvasSessionInput = CreateMultiplayerCanvasInput("Session code", MultiplayerMenu.DirectSessionCode, 0.44f);
            }
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Compatibility", replicationConfigDirectTransportSecurityV1
                ? "A matching protocol, plugin build, and session code must respond before the session becomes Connected."
                : "A matching protocol and plugin build must respond before the session becomes Connected.", 14, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.29f), new Vector2(0.92f, 0.42f));
            var connect = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Connect", "CONNECT", StartMultiplayerCanvasJoin, MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(connect.GetComponent<RectTransform>(), new Vector2(0.08f, 0.17f), new Vector2(0.31f, 0.26f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasMessage();
        }

        private void BuildMultiplayerCanvasStatusPage()
        {
            CreateMultiplayerCanvasHeading("Session Status", "Live replication and compatibility state.");
            var status = "Connection\nRole\nEndpoint\nProtocol\nPlugin\nHandshake";
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Labels", status, 15, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.33f), new Vector2(0.3f, 0.72f));
            multiplayerCanvasStatusValuesText = CreateMultiplayerCanvasText(multiplayerCanvasContent.transform, "Values", BuildMultiplayerCanvasStatusValues(), 15, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasText, new Vector2(0.31f, 0.33f), new Vector2(0.92f, 0.72f));
            var disconnect = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Disconnect", "DISCONNECT", () => { StopMultiplayerSession(); ShowMultiplayerCanvasPage(MultiplayerMenuPage.Status); }, MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(disconnect.GetComponent<RectTransform>(), new Vector2(0.08f, 0.17f), new Vector2(0.31f, 0.26f), Vector2.zero, Vector2.zero);
            var play = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Play", "PLAY", MarkMultiplayerReadyToPlay, MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(play.GetComponent<RectTransform>(), new Vector2(0.34f, 0.17f), new Vector2(0.58f, 0.26f), Vector2.zero, Vector2.zero);
            if (!replicationConfigHostMode)
            {
                var resync = CreateMultiplayerCanvasButton(multiplayerCanvasContent.transform, "Resync", "FULL RESYNC", RequestMultiplayerCanvasResync, MultiplayerCanvasAccent);
                SetMultiplayerCanvasRect(resync.GetComponent<RectTransform>(), new Vector2(0.61f, 0.17f), new Vector2(0.87f, 0.26f), Vector2.zero, Vector2.zero);
            }
            CreateMultiplayerTransferProgressUi(0.04f, 0.14f);
        }

        private void BuildMultiplayerCanvasSettingsPage()
        {
            CreateMultiplayerCanvasHeading("Settings", "Session values are applied in memory; replication.cfg remains the startup fallback.");
            multiplayerCanvasHostInput = CreateMultiplayerCanvasInput("Default host address", MultiplayerMenu.HostAddress, 0.7f);
            multiplayerCanvasPortInput = CreateMultiplayerCanvasInput("Default port", MultiplayerMenu.PortText, 0.54f);
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Smoothing Label", "CLIENT PAWN MOTION", 14, FontStyle.Normal, TextAnchor.MiddleLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.4f), new Vector2(0.32f, 0.48f));
            var smoothing = CreateMultiplayerCanvasButton(
                multiplayerCanvasContent.transform,
                "Smoothing",
                replicationConfigSmoothReplicatedMovement ? "SMOOTHED (CLICK FOR LEGACY)" : "LEGACY DIRECT (CLICK TO SMOOTH)",
                ToggleMultiplayerCanvasMovementSmoothing,
                replicationConfigSmoothReplicatedMovement ? MultiplayerCanvasAccent : MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(smoothing.GetComponent<RectTransform>(), new Vector2(0.34f, 0.4f), new Vector2(0.86f, 0.48f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(multiplayerCanvasContent.transform, "Needs Label", "HUNGER & SLEEP", 14, FontStyle.Normal, TextAnchor.MiddleLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.29f), new Vector2(0.32f, 0.37f));
            var needs = CreateMultiplayerCanvasButton(
                multiplayerCanvasContent.transform,
                "Needs",
                replicationConfigNeedsReplication ? "ENABLED (CLICK TO DISABLE)" : "DISABLED (CLICK TO ENABLE)",
                ToggleMultiplayerCanvasNeedsReplication,
                replicationConfigNeedsReplication ? MultiplayerCanvasAccent : MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(needs.GetComponent<RectTransform>(), new Vector2(0.34f, 0.29f), new Vector2(0.86f, 0.37f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(multiplayerCanvasContent.transform, "Gate", "Rollback either system here, or use smoothReplicatedMovement=false / needsReplication=false in replication.cfg.", 13, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.08f, 0.11f), new Vector2(0.92f, 0.25f));
        }

        private void ToggleMultiplayerCanvasNeedsReplication()
        {
            replicationConfigNeedsReplication = !replicationConfigNeedsReplication;
            ClearReplicationNeedsState();
            LogReplicationInfo("Going Cooperative hunger and sleep replication changed enabled="
                + replicationConfigNeedsReplication
                + " source=multiplayer-settings");
            ShowMultiplayerCanvasPage(MultiplayerMenuPage.Settings);
        }

        private void ToggleMultiplayerCanvasMovementSmoothing()
        {
            replicationConfigSmoothReplicatedMovement = !replicationConfigSmoothReplicatedMovement;
            ClearReplicationPresentationSmoothing();
            LogReplicationInfo("Going Cooperative client pawn motion mode changed smooth="
                + replicationConfigSmoothReplicatedMovement
                + " source=multiplayer-settings");
            ShowMultiplayerCanvasPage(MultiplayerMenuPage.Settings);
        }

        private void CreateMultiplayerCanvasHeading(string title, string subtitle)
        {
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Page Title", title, 26, FontStyle.Bold, TextAnchor.UpperLeft, MultiplayerCanvasText, new Vector2(0.06f, 0.78f), new Vector2(0.94f, 0.94f));
            CreateMultiplayerCanvasText(multiplayerCanvasContent.transform, "Page Subtitle", subtitle, 14, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.06f, 0.72f), new Vector2(0.94f, 0.82f));
        }

        private InputField CreateMultiplayerCanvasInput(string label, string value, float top)
        {
            CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, label + " Label", label, 14, FontStyle.Normal, TextAnchor.MiddleLeft, MultiplayerCanvasMuted, new Vector2(0.08f, top), new Vector2(0.32f, top + 0.08f));
            var inputObject = CreateMultiplayerCanvasImage(multiplayerCanvasContent.transform, label + " Input", MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(inputObject.GetComponent<RectTransform>(), new Vector2(0.34f, top), new Vector2(0.86f, top + 0.08f), Vector2.zero, Vector2.zero);
            var input = inputObject.AddComponent<InputField>();
            var text = CreateMultiplayerCanvasText(inputObject.transform, "Text", value, 15, FontStyle.Normal, TextAnchor.MiddleLeft, MultiplayerCanvasText, new Vector2(0.04f, 0f), new Vector2(0.96f, 1f));
            input.textComponent = text;
            input.text = value;
            return input;
        }

        private void StartMultiplayerCanvasHost()
        {
            if (replicationConfigDirectTransportSecurityV1) MultiplayerMenu.DirectSessionCode = multiplayerCanvasSessionInput?.text ?? MultiplayerMenu.DirectSessionCode;
            MultiplayerMenu.PortText = multiplayerCanvasPortInput?.text ?? MultiplayerMenu.PortText;
            TryStartMultiplayerHost(MultiplayerMenu.PortText, out var detail);
            MultiplayerMenu.StatusMessage = detail;
            if (!string.Equals(multiplayerSaveTransfer.Phase, "Idle", StringComparison.Ordinal)
                && !string.Equals(multiplayerSaveTransfer.Phase, "Failed", StringComparison.Ordinal)) ShowMultiplayerCanvasPage(MultiplayerMenuPage.Status); else SetMultiplayerCanvasMessage(detail);
        }

        private void StartMultiplayerCanvasJoin()
        {
            MultiplayerMenu.HostAddress = multiplayerCanvasHostInput?.text ?? MultiplayerMenu.HostAddress;
            MultiplayerMenu.PortText = multiplayerCanvasPortInput?.text ?? MultiplayerMenu.PortText;
            if (replicationConfigDirectTransportSecurityV1) MultiplayerMenu.DirectSessionCode = multiplayerCanvasSessionInput?.text ?? MultiplayerMenu.DirectSessionCode;
            TryJoinMultiplayerHost(MultiplayerMenu.HostAddress, MultiplayerMenu.PortText, out var detail);
            MultiplayerMenu.StatusMessage = detail;
            if (!string.Equals(multiplayerSaveTransfer.Phase, "Idle", StringComparison.Ordinal)
                && !string.Equals(multiplayerSaveTransfer.Phase, "Failed", StringComparison.Ordinal)) ShowMultiplayerCanvasPage(MultiplayerMenuPage.Status); else SetMultiplayerCanvasMessage(detail);
        }

        private void StartMultiplayerTransferTest(MultiplayerTransferTestKind kind)
        {
            MultiplayerTransferTest.Start(kind, Time.realtimeSinceStartup);
            UpdateMultiplayerTransferTestUi();
            LogReplicationInfo("Going Cooperative multiplayer "
                + (kind == MultiplayerTransferTestKind.Resync ? "resync" : "save-transfer")
                + " visual test started; no save files will be modified.");
        }

        private void RequestMultiplayerCanvasResync()
        {
            if (replicationConfigHostMode || !replicationRuntimeStarted)
            {
                MultiplayerMenu.StatusMessage = "Resync unavailable: connect as a client first.";
                SetMultiplayerCanvasMessage(MultiplayerMenu.StatusMessage);
                return;
            }

            RequestFullMultiplayerResync();
        }

        private void CreateMultiplayerTransferProgressUi(float bottom, float top)
        {
            if (multiplayerCanvasContent == null)
            {
                return;
            }

            var background = CreateMultiplayerCanvasImage(multiplayerCanvasContent.transform, "Transfer Progress", MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(background.GetComponent<RectTransform>(), new Vector2(0.08f, bottom), new Vector2(0.92f, top), Vector2.zero, Vector2.zero);
            var fillObject = CreateMultiplayerCanvasImage(background.transform, "Fill", MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(fillObject.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0.32f), new Vector2(4f, 4f), new Vector2(-4f, -2f));
            multiplayerCanvasTransferFill = fillObject.GetComponent<Image>();
            multiplayerCanvasTransferFill.type = Image.Type.Filled;
            multiplayerCanvasTransferFill.fillMethod = Image.FillMethod.Horizontal;
            multiplayerCanvasTransferFill.fillOrigin = 0;
            multiplayerCanvasTransferPhaseText = CreateMultiplayerCanvasText(background.transform, "Phase", MultiplayerTransferTest.Phase, 13, FontStyle.Bold, TextAnchor.MiddleLeft, MultiplayerCanvasText, new Vector2(0.03f, 0.38f), new Vector2(0.4f, 1f));
            multiplayerCanvasTransferDetailText = CreateMultiplayerCanvasText(background.transform, "Detail", MultiplayerTransferTest.Detail, 12, FontStyle.Normal, TextAnchor.MiddleRight, MultiplayerCanvasMuted, new Vector2(0.38f, 0.38f), new Vector2(0.97f, 1f));
            UpdateMultiplayerTransferTestUi();
        }

        private void UpdateMultiplayerTransferTestUi()
        {
            if (!string.Equals(multiplayerSaveTransfer.Phase, "Idle", StringComparison.Ordinal))
            {
                if (multiplayerCanvasTransferFill != null) multiplayerCanvasTransferFill.fillAmount = multiplayerSaveTransfer.Progress;
                if (multiplayerCanvasTransferPhaseText != null) multiplayerCanvasTransferPhaseText.text = multiplayerSaveTransfer.Phase + "  " + (multiplayerSaveTransfer.Progress * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
                if (multiplayerCanvasTransferDetailText != null) multiplayerCanvasTransferDetailText.text = multiplayerSaveTransfer.Detail;
                return;
            }
            if (multiplayerCanvasTransferFill != null)
            {
                multiplayerCanvasTransferFill.fillAmount = MultiplayerTransferTest.Progress;
            }

            if (multiplayerCanvasTransferPhaseText != null)
            {
                multiplayerCanvasTransferPhaseText.text = MultiplayerTransferTest.Phase
                    + "  " + (MultiplayerTransferTest.Progress * 100f).ToString("0", CultureInfo.InvariantCulture) + "%";
            }

            if (multiplayerCanvasTransferDetailText != null)
            {
                var networkDetail = MultiplayerTransferTest.Kind == MultiplayerTransferTestKind.Resync
                    && !string.IsNullOrWhiteSpace(replicationLastResyncSummary)
                        ? " · Network: " + TrimFingerprintText(replicationLastResyncSummary, 48)
                        : string.Empty;
                multiplayerCanvasTransferDetailText.text = MultiplayerTransferTest.Detail + networkDetail;
            }
        }

        private void CreateMultiplayerInGameHud()
        {
            if (multiplayerCanvasRoot == null)
            {
                return;
            }

            multiplayerCanvasGameFont = FindMultiplayerGameFont();
            multiplayerCanvasInGameHud = CreateMultiplayerCanvasImage(multiplayerCanvasRoot.transform, "In-Game Multiplayer HUD", Color.clear);
            SetMultiplayerCanvasRect(multiplayerCanvasInGameHud.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-300f, -58f), new Vector2(300f, -6f));

            multiplayerCanvasHudStatusText = CreateMultiplayerGameText(multiplayerCanvasInGameHud.transform, "Status", "MULTIPLAYER", 15f, TextAlignmentOptions.MidlineLeft, MultiplayerCanvasText);
            SetMultiplayerCanvasRect(multiplayerCanvasHudStatusText.rectTransform, new Vector2(0f, 0f), new Vector2(0.66f, 1f), new Vector2(8f, 0f), new Vector2(-10f, 0f));

            var resync = CreateMultiplayerGameButton(multiplayerCanvasInGameHud.transform, "Resync", "FULL RESYNC", RequestMultiplayerCanvasResync, new Color(0.58f, 0.39f, 0.16f, 0.92f));
            SetMultiplayerCanvasRect(resync.GetComponent<RectTransform>(), new Vector2(0.66f, 0.08f), new Vector2(1f, 0.92f), new Vector2(4f, 0f), new Vector2(-4f, 0f));
            multiplayerCanvasInGameHud.SetActive(false);
        }

        private TMP_FontAsset? FindMultiplayerGameFont()
        {
            var counts = new Dictionary<TMP_FontAsset, int>();
            var labels = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (var i = 0; i < labels.Length; i++)
            {
                var font = labels[i].font;
                if (font == null || labels[i].transform.IsChildOf(multiplayerCanvasRoot!.transform)) continue;
                counts.TryGetValue(font, out var count);
                counts[font] = count + 1;
            }

            TMP_FontAsset? result = null;
            var bestCount = 0;
            foreach (var entry in counts)
            {
                if (entry.Value <= bestCount) continue;
                result = entry.Key;
                bestCount = entry.Value;
            }
            return result;
        }

        private Button CreateMultiplayerGameButton(Transform parent, string name, string label, Action action, Color color)
        {
            var result = CreateMultiplayerCanvasImage(parent, name, color);
            var button = result.AddComponent<Button>();
            button.targetGraphic = result.GetComponent<Image>();
            button.onClick.AddListener(() => action());
            var text = CreateMultiplayerGameText(result.transform, "Label", label, 14f, TextAlignmentOptions.Center, MultiplayerCanvasText);
            SetMultiplayerCanvasRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(8f, 2f), new Vector2(-8f, -2f));
            return button;
        }

        private TextMeshProUGUI CreateMultiplayerGameText(Transform parent, string name, string value, float size, TextAlignmentOptions alignment, Color color)
        {
            var result = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            result.transform.SetParent(parent, false);
            var text = result.GetComponent<TextMeshProUGUI>();
            if (multiplayerCanvasGameFont != null) text.font = multiplayerCanvasGameFont;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = FontStyles.Normal;
            text.alignment = alignment;
            text.color = color;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }

        private void CreateMultiplayerResyncOverlay()
        {
            if (multiplayerCanvasRoot == null || multiplayerResyncOverlay != null) return;

            multiplayerResyncOverlay = CreateMultiplayerCanvasImage(multiplayerCanvasRoot.transform, "Full Resync Overlay", new Color(0.025f, 0.03f, 0.034f, 1f));
            SetMultiplayerCanvasRect(multiplayerResyncOverlay.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var card = CreateMultiplayerCanvasImage(multiplayerResyncOverlay.transform, "Resync Card", MultiplayerCanvasPanel);
            SetMultiplayerCanvasRect(card.GetComponent<RectTransform>(), new Vector2(0.25f, 0.31f), new Vector2(0.75f, 0.69f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(card.transform, "Title", "FULL SESSION RESYNC", 30, FontStyle.Bold, TextAnchor.MiddleCenter, MultiplayerCanvasText, new Vector2(0.08f, 0.72f), new Vector2(0.92f, 0.94f));
            multiplayerResyncPhaseText = CreateMultiplayerCanvasText(card.transform, "Phase", "Preparing", 20, FontStyle.Bold, TextAnchor.MiddleCenter, MultiplayerCanvasText, new Vector2(0.08f, 0.5f), new Vector2(0.92f, 0.72f));
            multiplayerResyncDetailText = CreateMultiplayerCanvasText(card.transform, "Detail", "Synchronizing the authoritative host checkpoint.", 14, FontStyle.Normal, TextAnchor.UpperCenter, MultiplayerCanvasMuted, new Vector2(0.1f, 0.25f), new Vector2(0.9f, 0.5f));

            var progress = CreateMultiplayerCanvasImage(card.transform, "Progress", MultiplayerCanvasCard);
            SetMultiplayerCanvasRect(progress.GetComponent<RectTransform>(), new Vector2(0.1f, 0.12f), new Vector2(0.9f, 0.2f), Vector2.zero, Vector2.zero);
            var fill = CreateMultiplayerCanvasImage(progress.transform, "Fill", MultiplayerCanvasAccent);
            SetMultiplayerCanvasRect(fill.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -4f));
            multiplayerResyncProgressFill = fill.GetComponent<Image>();
            multiplayerResyncProgressFill.type = Image.Type.Filled;
            multiplayerResyncProgressFill.fillMethod = Image.FillMethod.Horizontal;
            multiplayerResyncProgressFill.fillOrigin = 0;
            multiplayerResyncOverlay.SetActive(false);
            multiplayerResyncOverlay.transform.SetAsLastSibling();
        }

        private void UpdateMultiplayerResyncOverlay()
        {
            if (multiplayerResyncOverlay == null) return;
            var phase = multiplayerSaveTransfer.Phase;
            var active = multiplayerSaveTransfer.Epoch > 0
                && !string.Equals(phase, "Playing", StringComparison.Ordinal)
                && !string.Equals(phase, "Failed", StringComparison.Ordinal);
            active = active
                || string.Equals(phase, "Requesting Resync", StringComparison.Ordinal)
                || string.Equals(phase, "Capturing Checkpoint", StringComparison.Ordinal)
                || string.Equals(phase, "Transferring Resync", StringComparison.Ordinal)
                || string.Equals(phase, "Receiving Resync", StringComparison.Ordinal)
                || multiplayerWaitingForHomeScene;

            if (!active)
            {
                multiplayerResyncOverlay.SetActive(false);
                return;
            }

            multiplayerResyncOverlay.SetActive(true);
            multiplayerResyncOverlay.transform.SetAsLastSibling();
            if (multiplayerCanvasPanel != null) multiplayerCanvasPanel.SetActive(false);
            if (multiplayerCanvasInGameHud != null) multiplayerCanvasInGameHud.SetActive(false);
            if (multiplayerCanvasLauncherButton != null) multiplayerCanvasLauncherButton.gameObject.SetActive(false);

            var displayPhase = multiplayerWaitingForHomeScene ? "RESETTING WORLD" : phase.ToUpperInvariant();
            var displayDetail = multiplayerWaitingForHomeScene
                ? "Safely unloading the current settlement before loading the host checkpoint."
                : multiplayerSaveTransfer.Detail;
            if (multiplayerResyncPhaseText != null) multiplayerResyncPhaseText.text = displayPhase;
            if (multiplayerResyncDetailText != null) multiplayerResyncDetailText.text = displayDetail;
            if (multiplayerResyncProgressFill != null)
            {
                var value = multiplayerWaitingForHomeScene ? 0.78f : multiplayerSaveTransfer.Progress;
                multiplayerResyncProgressFill.fillAmount = Mathf.Clamp01(value);
            }
        }

        private void UpdateMultiplayerInGameHud()
        {
            if (multiplayerCanvasInGameHud == null)
            {
                return;
            }

            var visible = replicationRuntimeStarted && !multiplayerMainMenuActive;
            multiplayerCanvasInGameHud.SetActive(visible);
            if (multiplayerCanvasHudStatusText != null)
            {
                multiplayerCanvasHudStatusText.text = "MULTIPLAYER · " + GetMultiplayerConnectionLabel().ToUpperInvariant()
                    + " (" + (replicationConfigHostMode ? "HOST" : "CLIENT") + ")"
                    + (MultiplayerTransferTest.IsActive ? " · " + MultiplayerTransferTest.Phase : string.Empty);
            }
        }

        private void CreateMultiplayerCanvasMessage()
        {
            multiplayerCanvasMessageText = CreateMultiplayerCanvasText(multiplayerCanvasContent!.transform, "Message", MultiplayerMenu.StatusMessage, 13, FontStyle.Normal, TextAnchor.UpperLeft, MultiplayerCanvasMuted, new Vector2(0.38f, 0.13f), new Vector2(0.92f, 0.27f));
        }

        private void SetMultiplayerCanvasMessage(string message)
        {
            if (multiplayerCanvasMessageText != null) multiplayerCanvasMessageText.text = message;
        }

        private void SetMultiplayerCanvasOpen(bool open)
        {
            if (multiplayerCanvasPanel == null) return;
            multiplayerCanvasPanel.SetActive(open);
            if (open) ShowMultiplayerCanvasPage(replicationRuntimeStarted ? MultiplayerMenuPage.Status : multiplayerCanvasPage);
        }

        internal void OpenMultiplayerCanvasFromNativeMenu()
        {
            SetMultiplayerCanvasOpen(true);
        }

        internal void SetMultiplayerCanvasLauncherVisible(bool visible)
        {
            if (multiplayerCanvasLauncherButton != null)
            {
                multiplayerCanvasLauncherButton.gameObject.SetActive(visible);
            }
        }

        private void DestroyMultiplayerCanvasGui()
        {
            if (multiplayerCanvasRoot != null) Destroy(multiplayerCanvasRoot);
            if (multiplayerCanvasEventSystem != null) Destroy(multiplayerCanvasEventSystem);
            multiplayerCanvasRoot = null;
            multiplayerCanvasPanel = null;
            multiplayerCanvasContent = null;
            multiplayerCanvasEventSystem = null;
            multiplayerCanvasInGameHud = null;
            multiplayerResyncOverlay = null;
            multiplayerResyncPhaseText = null;
            multiplayerResyncDetailText = null;
            multiplayerResyncProgressFill = null;
            multiplayerCanvasStatusValuesText = null;
            multiplayerCanvasLauncherButton = null;
            multiplayerCanvasHudStatusText = null;
            multiplayerEventPresentationPanel = null;
            multiplayerEventPresentationKey = string.Empty;
            multiplayerEventWarningPanel = null;
            multiplayerEventWarningKey = string.Empty;
            v2Window = null;
            v2TabBackgrounds.Clear();
        }

        private string BuildMultiplayerCanvasStatusValues()
        {
            return GetMultiplayerConnectionLabel() + "\n"
                + (replicationConfigHostMode ? "Host" : "Client") + "\n"
                + replicationConfigHost + ":" + replicationConfigPort.ToString(CultureInfo.InvariantCulture) + "\n"
                + GetMultiplayerProtocolLabel() + "\n"
                + GoingCooperativeConstants.Version + "\n"
                + (replicationRemoteHelloReceived
                    ? "Compatible peer verified"
                    : replicationRemoteCompatibilityRefused ? "Refused" : "Pending");
        }

        private static string GetMultiplayerLanAddressSummary()
        {
            try
            {
                var addresses = Dns.GetHostAddresses(Dns.GetHostName());
                string? fallback = null;
                foreach (var address in addresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address)
                        && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        var text = address.ToString();
                        if (text.StartsWith("192.168.", StringComparison.Ordinal))
                        {
                            return text;
                        }

                        fallback ??= text;
                    }
                }

                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    return fallback!;
                }
            }
            catch
            {
                // The host can still share an address manually if DNS enumeration fails.
            }

            return "LAN-IP";
        }

        private GameObject CreateMultiplayerCanvasImage(Transform parent, string name, Color color)
        {
            var result = new GameObject(name, typeof(RectTransform), typeof(Image));
            result.transform.SetParent(parent, false);
            result.GetComponent<Image>().color = color;
            return result;
        }

        private Button CreateMultiplayerCanvasButton(Transform parent, string name, string label, Action action, Color color)
        {
            var result = CreateMultiplayerCanvasImage(parent, name, color);
            var button = result.AddComponent<Button>();
            button.targetGraphic = result.GetComponent<Image>();
            button.onClick.AddListener(() => action());
            CreateMultiplayerCanvasText(result.transform, "Label", label, 14, FontStyle.Bold, TextAnchor.MiddleCenter, MultiplayerCanvasText, Vector2.zero, Vector2.one);
            return button;
        }

        private Text CreateMultiplayerCanvasText(Transform parent, string name, string value, int size, FontStyle style, TextAnchor alignment, Color color, Vector2 anchorMin, Vector2 anchorMax)
        {
            var result = new GameObject(name, typeof(RectTransform), typeof(Text));
            result.transform.SetParent(parent, false);
            var text = result.GetComponent<Text>();
            text.font = multiplayerCanvasFont;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            SetMultiplayerCanvasRect(result.GetComponent<RectTransform>(), anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            return text;
        }

        private static void SetMultiplayerCanvasRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }
    }
}
