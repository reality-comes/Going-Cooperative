using System;
using System.Collections.Generic;
using System.Globalization;
using GoingCooperative.Core;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GoingCooperative.Plugin.BepInEx
{
    /// <summary>
    /// Redesigned (v2) multiplayer menu. Selected by ui=v2 in replication.cfg
    /// (the default); ui=classic restores the original menu untouched. The v2
    /// builders populate the same field slots the shared per-frame updaters
    /// (connection label, status values, transfer progress, resync overlay,
    /// in-game HUD) already maintain, so session behavior is identical.
    /// </summary>
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Color V2Dim = new Color(0f, 0f, 0f, 0.66f);
        private static readonly Color V2Panel = new Color(0.145f, 0.114f, 0.078f, 0.995f);
        private static readonly Color V2Header = new Color(0.114f, 0.086f, 0.059f, 1f);
        private static readonly Color V2Card = new Color(0.208f, 0.165f, 0.112f, 1f);
        private static readonly Color V2Field = new Color(0.098f, 0.078f, 0.055f, 1f);
        private static readonly Color V2Accent = new Color(0.702f, 0.525f, 0.216f, 1f);
        private static readonly Color V2AccentDark = new Color(0.447f, 0.329f, 0.137f, 1f);
        private static readonly Color V2Border = new Color(0.545f, 0.427f, 0.208f, 0.85f);
        private static readonly Color V2TextColor = new Color(0.922f, 0.882f, 0.784f, 1f);
        private static readonly Color V2Muted = new Color(0.702f, 0.647f, 0.549f, 1f);
        private static readonly Color V2Disabled = new Color(0.302f, 0.271f, 0.220f, 1f);

        private readonly Dictionary<MultiplayerMenuPage, Image> v2TabBackgrounds = new Dictionary<MultiplayerMenuPage, Image>();
        private GameObject? v2Window;

        private void EnsureMultiplayerCanvasGuiV2()
        {
            if (multiplayerCanvasRoot != null)
            {
                return;
            }

            v2TabBackgrounds.Clear();
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

            multiplayerCanvasGameFont = FindMultiplayerGameFont();

            var launcher = CreateV2Button(multiplayerCanvasRoot.transform, "Launcher", "MULTIPLAYER  [F8]", () => SetMultiplayerCanvasOpen(true), V2Card, 15f);
            multiplayerCanvasLauncherButton = launcher;
            SetMultiplayerCanvasRect(launcher.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-286f, -66f), new Vector2(-24f, -22f));
            CreateV2BorderFrame(launcher.transform);

            CreateMultiplayerInGameHud();

            multiplayerCanvasPanel = CreateMultiplayerCanvasImage(multiplayerCanvasRoot.transform, "Menu", V2Dim);
            SetMultiplayerCanvasRect(multiplayerCanvasPanel.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            v2Window = CreateMultiplayerCanvasImage(multiplayerCanvasPanel.transform, "Window", V2Panel);
            SetMultiplayerCanvasRect(v2Window.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-430f, -330f), new Vector2(430f, 330f));
            CreateV2BorderFrame(v2Window.transform);

            var header = CreateMultiplayerCanvasImage(v2Window.transform, "Header", V2Header);
            SetMultiplayerCanvasRect(header.GetComponent<RectTransform>(), new Vector2(0f, 0.905f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            var title = CreateV2Heading(header.transform, "Title", "GOING COOPERATIVE", 25f, TextAlignmentOptions.MidlineLeft);
            SetMultiplayerCanvasRect(title.rectTransform, new Vector2(0.025f, 0f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
            multiplayerCanvasConnectionText = CreateMultiplayerCanvasText(header.transform, "Connection", GetMultiplayerConnectionLabel(), 14, FontStyle.Bold, TextAnchor.MiddleRight, V2Accent, new Vector2(0.5f, 0f), new Vector2(0.925f, 1f));
            var close = CreateV2Button(header.transform, "Close", "X", () => SetMultiplayerCanvasOpen(false), V2Header, 17f);
            SetMultiplayerCanvasRect(close.GetComponent<RectTransform>(), new Vector2(0.945f, 0.2f), new Vector2(0.99f, 0.8f), Vector2.zero, Vector2.zero);

            var tabs = CreateMultiplayerCanvasImage(v2Window.transform, "Tabs", V2Header);
            SetMultiplayerCanvasRect(tabs.GetComponent<RectTransform>(), new Vector2(0f, 0.83f), new Vector2(1f, 0.905f), Vector2.zero, Vector2.zero);
            CreateV2TabButton(tabs.transform, "OVERVIEW", MultiplayerMenuPage.Home, 0.015f, 0.195f);
            CreateV2TabButton(tabs.transform, "HOST", MultiplayerMenuPage.Host, 0.215f, 0.395f);
            CreateV2TabButton(tabs.transform, "JOIN", MultiplayerMenuPage.Join, 0.415f, 0.595f);
            CreateV2TabButton(tabs.transform, "STATUS", MultiplayerMenuPage.Status, 0.615f, 0.795f);
            CreateV2TabButton(tabs.transform, "SETTINGS", MultiplayerMenuPage.Settings, 0.815f, 0.985f);

            multiplayerCanvasContent = CreateMultiplayerCanvasImage(v2Window.transform, "Content", Color.clear);
            SetMultiplayerCanvasRect(multiplayerCanvasContent.GetComponent<RectTransform>(), new Vector2(0.025f, 0.03f), new Vector2(0.975f, 0.82f), Vector2.zero, Vector2.zero);

            multiplayerCanvasPanel.SetActive(false);
            ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Home);
            CreateMultiplayerResyncOverlay();
            RunMultiplayerCanvasSmokeTestV2(launcher, close);
            LogReplicationInfo("Going Cooperative multiplayer Canvas UI v2 created screen="
                + Screen.width.ToString(CultureInfo.InvariantCulture) + "x" + Screen.height.ToString(CultureInfo.InvariantCulture));
        }

        private void ShowMultiplayerCanvasPageV2(MultiplayerMenuPage page)
        {
            if (multiplayerCanvasContent == null)
            {
                return;
            }

            multiplayerCanvasPage = page;
            foreach (var entry in v2TabBackgrounds)
            {
                if (entry.Value != null)
                {
                    entry.Value.color = entry.Key == page ? V2AccentDark : V2Header;
                }
            }

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
                    BuildV2HostPage();
                    break;
                case MultiplayerMenuPage.Join:
                    BuildV2JoinPage();
                    break;
                case MultiplayerMenuPage.Status:
                    BuildV2StatusPage();
                    break;
                case MultiplayerMenuPage.Settings:
                    BuildV2SettingsPage();
                    break;
                default:
                    BuildV2HomePage();
                    break;
            }
        }

        private void BuildV2HomePage()
        {
            var content = multiplayerCanvasContent!.transform;
            BuildV2PageHeading("Play together", "Host your settlement or join a friend. The host runs the world; the client plays inside it.");
            BuildV2ModeSelector(content, 0.60f, 0.76f);

            var hostCard = BuildV2Card(content, "Host Card", new Vector2(0f, 0.18f), new Vector2(0.49f, 0.56f));
            var hostTitle = CreateV2Heading(hostCard.transform, "Title", "HOST A SETTLEMENT", 18f, TextAlignmentOptions.TopLeft);
            SetMultiplayerCanvasRect(hostTitle.rectTransform, new Vector2(0.06f, 0.6f), new Vector2(0.94f, 0.92f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(hostCard.transform, "Body", "Pick a save and run the authoritative world for both players.", 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.06f, 0.32f), new Vector2(0.94f, 0.62f));
            var hostButton = CreateV2Button(hostCard.transform, "Host", "HOST GAME", () => ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Host), V2Accent, 15f);
            SetMultiplayerCanvasRect(hostButton.GetComponent<RectTransform>(), new Vector2(0.06f, 0.08f), new Vector2(0.94f, 0.28f), Vector2.zero, Vector2.zero);

            var joinCard = BuildV2Card(content, "Join Card", new Vector2(0.51f, 0.18f), new Vector2(1f, 0.56f));
            var joinTitle = CreateV2Heading(joinCard.transform, "Title", "JOIN A SETTLEMENT", 18f, TextAlignmentOptions.TopLeft);
            SetMultiplayerCanvasRect(joinTitle.rectTransform, new Vector2(0.06f, 0.6f), new Vector2(0.94f, 0.92f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(joinCard.transform, "Body", MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam
                ? "Accept a Steam invite, or enter the host's SteamID."
                : "Connect to the host's reachable LAN or VPN address.", 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.06f, 0.32f), new Vector2(0.94f, 0.62f));
            var joinButton = CreateV2Button(joinCard.transform, "Join", "JOIN GAME", () => ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Join), V2Accent, 15f);
            SetMultiplayerCanvasRect(joinButton.GetComponent<RectTransform>(), new Vector2(0.06f, 0.08f), new Vector2(0.94f, 0.28f), Vector2.zero, Vector2.zero);

            CreateMultiplayerCanvasText(content, "Notice", "Experimental build · both computers need the same game version and the same Going Cooperative release.", 12, FontStyle.Normal, TextAnchor.MiddleCenter, V2Muted, new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.12f));
        }

        private void BuildV2ModeSelector(Transform content, float bottom, float top)
        {
            CreateMultiplayerCanvasText(content, "Mode Label", "CONNECTION", 13, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, bottom + 0.045f), new Vector2(0.14f, top - 0.02f));
            var steamOffered = IsMultiplayerSteamOffered(out var steamDetail);
            var direct = CreateV2Button(content, "Mode Direct", "DIRECT (IP)", () => SetV2ConnectionMode(MultiplayerConnectionMode.Direct),
                MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Direct ? V2AccentDark : V2Card, 13f);
            SetMultiplayerCanvasRect(direct.GetComponent<RectTransform>(), new Vector2(0.15f, bottom + 0.03f), new Vector2(0.33f, top - 0.01f), Vector2.zero, Vector2.zero);
            var steam = CreateV2Button(content, "Mode Steam", "STEAM", () => SetV2ConnectionMode(MultiplayerConnectionMode.Steam),
                MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam ? V2AccentDark : (steamOffered ? V2Card : V2Disabled), 13f);
            SetMultiplayerCanvasRect(steam.GetComponent<RectTransform>(), new Vector2(0.35f, bottom + 0.03f), new Vector2(0.53f, top - 0.01f), Vector2.zero, Vector2.zero);
            var hint = replicationConfigSteamNetworking
                ? steamDetail
                : "Steam mode is available after setting steamNetworking=true in replication.cfg.";
            CreateMultiplayerCanvasText(content, "Mode Hint", hint, 12, FontStyle.Normal, TextAnchor.MiddleLeft, V2Muted, new Vector2(0.56f, bottom + 0.02f), new Vector2(1f, top));
        }

        private void SetV2ConnectionMode(MultiplayerConnectionMode mode)
        {
            if (mode == MultiplayerConnectionMode.Steam && !IsMultiplayerSteamOffered(out var detail))
            {
                MultiplayerMenu.StatusMessage = detail;
                ShowMultiplayerCanvasPageV2(multiplayerCanvasPage);
                return;
            }

            MultiplayerMenu.ConnectionMode = mode;
            ShowMultiplayerCanvasPageV2(multiplayerCanvasPage);
        }

        private void BuildV2HostPage()
        {
            EnsureDirectHostSessionCode();
            var content = multiplayerCanvasContent!.transform;
            var steamMode = MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam;
            BuildV2PageHeading("Host a game", steamMode
                ? "The selected save is sent to your friend through Steam. No port forwarding needed."
                : "The selected save is sent to the joining player over your LAN or VPN.");
            BuildV2ModeSelector(content, 0.72f, 0.88f);

            var saveCard = BuildV2Card(content, "Save Card", new Vector2(0f, 0.52f), new Vector2(1f, 0.70f));
            CreateMultiplayerCanvasText(saveCard.transform, "Save Label", "WORLD TO HOST", 12, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0.03f, 0.55f), new Vector2(0.6f, 0.95f));
            CreateMultiplayerCanvasText(saveCard.transform, "Selected Save", GetSelectedMultiplayerSaveLabel(), 15, FontStyle.Bold, TextAnchor.MiddleLeft, V2TextColor, new Vector2(0.03f, 0.08f), new Vector2(0.82f, 0.6f));
            var previousSave = CreateV2Button(saveCard.transform, "Previous Save", "<", SelectPreviousMultiplayerSave, V2Field, 14f);
            SetMultiplayerCanvasRect(previousSave.GetComponent<RectTransform>(), new Vector2(0.84f, 0.15f), new Vector2(0.905f, 0.85f), Vector2.zero, Vector2.zero);
            var nextSave = CreateV2Button(saveCard.transform, "Next Save", ">", SelectNextMultiplayerSave, V2Field, 14f);
            SetMultiplayerCanvasRect(nextSave.GetComponent<RectTransform>(), new Vector2(0.92f, 0.15f), new Vector2(0.99f, 0.85f), Vector2.zero, Vector2.zero);

            if (steamMode)
            {
                multiplayerCanvasPortInput = CreateV2Input("Listen port", MultiplayerMenu.PortText, 0.40f);
                CreateMultiplayerCanvasText(content, "Steam Info", GetMultiplayerSteamStatusLine()
                    + "\nHOST first, then invite a friend from the Steam overlay. Their game connects through Steam relay.",
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.22f), new Vector2(0.66f, 0.38f));
                var invite = CreateV2Button(content, "Invite", "INVITE FRIENDS", OpenV2SteamInviteDialog, V2Card, 13f);
                SetMultiplayerCanvasRect(invite.GetComponent<RectTransform>(), new Vector2(0.68f, 0.27f), new Vector2(0.98f, 0.37f), Vector2.zero, Vector2.zero);
            }
            else
            {
                multiplayerCanvasPortInput = CreateV2Input("Listen port", MultiplayerMenu.PortText, 0.40f);
                if (replicationConfigDirectTransportSecurityV1)
                {
                    multiplayerCanvasSessionInput = CreateV2Input("Session code", MultiplayerMenu.DirectSessionCode, 0.30f);
                }
                CreateMultiplayerCanvasText(content, "Preflight", "Share this address: " + GetMultiplayerLanAddressSummary() + ":" + MultiplayerMenu.PortText
                    + "   ·   Plugin " + GoingCooperativeConstants.Version + "   ·   Protocol " + GetMultiplayerProtocolLabel(),
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.235f), new Vector2(1f, 0.29f));
            }

            var start = CreateV2Button(content, "Start", steamMode ? "HOST OVER STEAM" : "HOST", StartMultiplayerCanvasHostV2, V2Accent, 15f);
            SetMultiplayerCanvasRect(start.GetComponent<RectTransform>(), new Vector2(0f, 0.13f), new Vector2(0.28f, 0.235f), Vector2.zero, Vector2.zero);
            CreateV2TransferProgress(0.0f, 0.105f);
            multiplayerCanvasMessageText = CreateMultiplayerCanvasText(content, "Message", MultiplayerMenu.StatusMessage, 12, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.31f, 0.13f), new Vector2(1f, 0.235f));
        }

        private void BuildV2JoinPage()
        {
            var content = multiplayerCanvasContent!.transform;
            var steamMode = MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam;
            BuildV2PageHeading("Join a game", steamMode
                ? "Accept a Steam invite and this connects automatically — or enter the host's SteamID64."
                : "Enter the address the host shared with you.");
            BuildV2ModeSelector(content, 0.72f, 0.88f);

            if (steamMode)
            {
                multiplayerCanvasHostInput = CreateV2Input("Host SteamID64", MultiplayerMenu.SteamHostIdText, 0.54f);
                CreateMultiplayerCanvasText(content, "Steam Hint", GetMultiplayerSteamStatusLine()
                    + "\nAccepting a Steam invite fills this in and connects on its own; the field is only needed for manual joins between friends.",
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.28f), new Vector2(1f, 0.5f));
            }
            else
            {
                multiplayerCanvasHostInput = CreateV2Input("Host address", MultiplayerMenu.HostAddress, 0.54f);
                multiplayerCanvasPortInput = CreateV2Input("Port", MultiplayerMenu.PortText, 0.40f);
                if (replicationConfigDirectTransportSecurityV1)
                {
                    multiplayerCanvasSessionInput = CreateV2Input("Session code", MultiplayerMenu.DirectSessionCode, 0.30f);
                }
                CreateMultiplayerCanvasText(content, "Compatibility", replicationConfigDirectTransportSecurityV1
                    ? "A matching plugin build, protocol, and session code must respond before the session becomes Connected."
                    : "A matching plugin build and protocol must respond before the session becomes Connected.",
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.235f), new Vector2(1f, 0.29f));
            }

            var connect = CreateV2Button(content, "Connect", steamMode ? "CONNECT VIA STEAM" : "CONNECT", StartMultiplayerCanvasJoinV2, V2Accent, 15f);
            SetMultiplayerCanvasRect(connect.GetComponent<RectTransform>(), new Vector2(0f, 0.13f), new Vector2(0.28f, 0.235f), Vector2.zero, Vector2.zero);
            multiplayerCanvasMessageText = CreateMultiplayerCanvasText(content, "Message", MultiplayerMenu.StatusMessage, 12, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.31f, 0.13f), new Vector2(1f, 0.235f));
        }

        private void BuildV2StatusPage()
        {
            var content = multiplayerCanvasContent!.transform;
            BuildV2PageHeading("Session status", "Live replication and compatibility state.");
            var card = BuildV2Card(content, "Status Card", new Vector2(0f, 0.32f), new Vector2(1f, 0.76f));
            CreateMultiplayerCanvasText(card.transform, "Labels", "Connection\nRole\nEndpoint\nProtocol\nPlugin\nHandshake", 14, FontStyle.Bold, TextAnchor.UpperLeft, V2Muted, new Vector2(0.03f, 0.08f), new Vector2(0.25f, 0.92f));
            multiplayerCanvasStatusValuesText = CreateMultiplayerCanvasText(card.transform, "Values", BuildMultiplayerCanvasStatusValues(), 14, FontStyle.Normal, TextAnchor.UpperLeft, V2TextColor, new Vector2(0.26f, 0.08f), new Vector2(0.97f, 0.92f));
            if (replicationConfigSteamNetworking)
            {
                CreateMultiplayerCanvasText(content, "Steam Status", GetMultiplayerSteamStatusLine(), 12, FontStyle.Normal, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, 0.25f), new Vector2(1f, 0.31f));
            }

            var disconnect = CreateV2Button(content, "Disconnect", "DISCONNECT", () => { StopMultiplayerSession(); ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Status); }, V2Card, 14f);
            SetMultiplayerCanvasRect(disconnect.GetComponent<RectTransform>(), new Vector2(0f, 0.13f), new Vector2(0.24f, 0.235f), Vector2.zero, Vector2.zero);
            var play = CreateV2Button(content, "Play", "PLAY", MarkMultiplayerReadyToPlay, V2Accent, 14f);
            SetMultiplayerCanvasRect(play.GetComponent<RectTransform>(), new Vector2(0.26f, 0.13f), new Vector2(0.5f, 0.235f), Vector2.zero, Vector2.zero);
            if (!replicationConfigHostMode)
            {
                var resync = CreateV2Button(content, "Resync", "FULL RESYNC", RequestMultiplayerCanvasResync, V2AccentDark, 14f);
                SetMultiplayerCanvasRect(resync.GetComponent<RectTransform>(), new Vector2(0.52f, 0.13f), new Vector2(0.76f, 0.235f), Vector2.zero, Vector2.zero);
            }

            CreateV2TransferProgress(0.0f, 0.105f);
        }

        private void BuildV2SettingsPage()
        {
            var content = multiplayerCanvasContent!.transform;
            BuildV2PageHeading("Settings", "Session values apply in memory; replication.cfg remains the startup fallback.");
            multiplayerCanvasHostInput = CreateV2Input("Default host address", MultiplayerMenu.HostAddress, 0.62f);
            multiplayerCanvasPortInput = CreateV2Input("Default port", MultiplayerMenu.PortText, 0.48f);

            CreateMultiplayerCanvasText(content, "Smoothing Label", "CLIENT PAWN MOTION", 13, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, 0.35f), new Vector2(0.24f, 0.43f));
            var smoothing = CreateV2Button(content, "Smoothing",
                replicationConfigSmoothReplicatedMovement ? "SMOOTHED (CLICK FOR LEGACY)" : "LEGACY DIRECT (CLICK TO SMOOTH)",
                ToggleMultiplayerCanvasMovementSmoothing,
                replicationConfigSmoothReplicatedMovement ? V2AccentDark : V2Card, 13f);
            SetMultiplayerCanvasRect(smoothing.GetComponent<RectTransform>(), new Vector2(0.26f, 0.35f), new Vector2(0.72f, 0.43f), Vector2.zero, Vector2.zero);

            CreateMultiplayerCanvasText(content, "Needs Label", "HUNGER & SLEEP", 13, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, 0.24f), new Vector2(0.24f, 0.32f));
            var needs = CreateV2Button(content, "Needs",
                replicationConfigNeedsReplication ? "ENABLED (CLICK TO DISABLE)" : "DISABLED (CLICK TO ENABLE)",
                ToggleMultiplayerCanvasNeedsReplication,
                replicationConfigNeedsReplication ? V2AccentDark : V2Card, 13f);
            SetMultiplayerCanvasRect(needs.GetComponent<RectTransform>(), new Vector2(0.26f, 0.24f), new Vector2(0.72f, 0.32f), Vector2.zero, Vector2.zero);

            CreateMultiplayerCanvasText(content, "Gate", "Rollback: smoothReplicatedMovement=false / needsReplication=false in replication.cfg. The previous menu is available with ui=classic.",
                12, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.08f), new Vector2(1f, 0.2f));
        }

        private void BuildV2PageHeading(string title, string subtitle)
        {
            var content = multiplayerCanvasContent!.transform;
            var heading = CreateV2Heading(content, "Page Title", title, 23f, TextAlignmentOptions.TopLeft);
            SetMultiplayerCanvasRect(heading.rectTransform, new Vector2(0f, 0.88f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(content, "Page Subtitle", subtitle, 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.815f), new Vector2(1f, 0.9f));
        }

        private void StartMultiplayerCanvasHostV2()
        {
            MultiplayerMenu.PortText = multiplayerCanvasPortInput?.text ?? MultiplayerMenu.PortText;
            string detail;
            if (MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam)
            {
                TryStartMultiplayerSteamHost(MultiplayerMenu.PortText, out detail);
            }
            else
            {
                if (replicationConfigDirectTransportSecurityV1) MultiplayerMenu.DirectSessionCode = multiplayerCanvasSessionInput?.text ?? MultiplayerMenu.DirectSessionCode;
                TryStartMultiplayerHost(MultiplayerMenu.PortText, out detail);
            }

            MultiplayerMenu.StatusMessage = detail;
            if (!string.Equals(multiplayerSaveTransfer.Phase, "Idle", StringComparison.Ordinal)
                && !string.Equals(multiplayerSaveTransfer.Phase, "Failed", StringComparison.Ordinal))
            {
                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Status);
            }
            else
            {
                SetMultiplayerCanvasMessage(detail);
            }
        }

        private void StartMultiplayerCanvasJoinV2()
        {
            string detail;
            if (MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam)
            {
                MultiplayerMenu.SteamHostIdText = multiplayerCanvasHostInput?.text ?? MultiplayerMenu.SteamHostIdText;
                TryJoinMultiplayerSteamHost(MultiplayerMenu.SteamHostIdText, out detail);
            }
            else
            {
                MultiplayerMenu.HostAddress = multiplayerCanvasHostInput?.text ?? MultiplayerMenu.HostAddress;
                MultiplayerMenu.PortText = multiplayerCanvasPortInput?.text ?? MultiplayerMenu.PortText;
                if (replicationConfigDirectTransportSecurityV1) MultiplayerMenu.DirectSessionCode = multiplayerCanvasSessionInput?.text ?? MultiplayerMenu.DirectSessionCode;
                TryJoinMultiplayerHost(MultiplayerMenu.HostAddress, MultiplayerMenu.PortText, out detail);
            }

            MultiplayerMenu.StatusMessage = detail;
            if (!string.Equals(multiplayerSaveTransfer.Phase, "Idle", StringComparison.Ordinal)
                && !string.Equals(multiplayerSaveTransfer.Phase, "Failed", StringComparison.Ordinal))
            {
                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Status);
            }
            else
            {
                SetMultiplayerCanvasMessage(detail);
            }
        }

        private void OpenV2SteamInviteDialog()
        {
            if (!IsMultiplayerSteamOffered(out var detail))
            {
                SetMultiplayerCanvasMessage(detail);
                return;
            }

            try
            {
                OpenV2SteamInviteDialogCore(out detail);
            }
            catch (Exception ex)
            {
                detail = "Steam invite dialog failed: " + FormatReflectionExceptionDetail(ex);
                LogReplicationWarning("Going Cooperative " + detail);
            }

            SetMultiplayerCanvasMessage(detail);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void OpenV2SteamInviteDialogCore(out string detail)
        {
            var lobby = Steam.SteamSessionState.Lobby;
            if (lobby == null || !lobby.IsHosting)
            {
                detail = "Press HOST OVER STEAM first, then invite friends.";
                return;
            }

            lobby.OpenInviteDialog();
            detail = "Steam overlay invite dialog opened.";
        }

        private GameObject BuildV2Card(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax)
        {
            var card = CreateMultiplayerCanvasImage(parent, name, V2Card);
            SetMultiplayerCanvasRect(card.GetComponent<RectTransform>(), anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            CreateV2BorderFrame(card.transform);
            return card;
        }

        private void CreateV2TabButton(Transform parent, string label, MultiplayerMenuPage page, float left, float right)
        {
            var button = CreateV2Button(parent, label, label, () => ShowMultiplayerCanvasPageV2(page), V2Header, 13f);
            SetMultiplayerCanvasRect(button.GetComponent<RectTransform>(), new Vector2(left, 0.08f), new Vector2(right, 0.92f), Vector2.zero, Vector2.zero);
            v2TabBackgrounds[page] = button.GetComponent<Image>();
        }

        private Button CreateV2Button(Transform parent, string name, string label, Action action, Color color, float fontSize)
        {
            var result = CreateMultiplayerCanvasImage(parent, name, color);
            var button = result.AddComponent<Button>();
            button.targetGraphic = result.GetComponent<Image>();
            button.onClick.AddListener(() => action());
            var colors = button.colors;
            colors.highlightedColor = new Color(1.16f, 1.16f, 1.16f, 1f);
            colors.pressedColor = new Color(0.86f, 0.86f, 0.86f, 1f);
            button.colors = colors;
            var text = CreateMultiplayerGameText(result.transform, "Label", label, fontSize, TextAlignmentOptions.Center, V2TextColor);
            SetMultiplayerCanvasRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(6f, 2f), new Vector2(-6f, -2f));
            return button;
        }

        private TextMeshProUGUI CreateV2Heading(Transform parent, string name, string value, float size, TextAlignmentOptions alignment)
        {
            var text = CreateMultiplayerGameText(parent, name, value, size, alignment, V2TextColor);
            return text;
        }

        private InputField CreateV2Input(string label, string value, float top)
        {
            var content = multiplayerCanvasContent!.transform;
            CreateMultiplayerCanvasText(content, label + " Label", label.ToUpperInvariant(), 13, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, top), new Vector2(0.24f, top + 0.09f));
            var inputObject = CreateMultiplayerCanvasImage(content, label + " Input", V2Field);
            SetMultiplayerCanvasRect(inputObject.GetComponent<RectTransform>(), new Vector2(0.26f, top), new Vector2(0.72f, top + 0.09f), Vector2.zero, Vector2.zero);
            CreateV2BorderFrame(inputObject.transform);
            var input = inputObject.AddComponent<InputField>();
            var text = CreateMultiplayerCanvasText(inputObject.transform, "Text", value, 15, FontStyle.Normal, TextAnchor.MiddleLeft, V2TextColor, new Vector2(0.03f, 0f), new Vector2(0.97f, 1f));
            input.textComponent = text;
            input.text = value;
            return input;
        }

        private void CreateV2TransferProgress(float bottom, float top)
        {
            if (multiplayerCanvasContent == null)
            {
                return;
            }

            var background = CreateMultiplayerCanvasImage(multiplayerCanvasContent.transform, "Transfer Progress", V2Field);
            SetMultiplayerCanvasRect(background.GetComponent<RectTransform>(), new Vector2(0f, bottom), new Vector2(1f, top), Vector2.zero, Vector2.zero);
            CreateV2BorderFrame(background.transform);
            var fillObject = CreateMultiplayerCanvasImage(background.transform, "Fill", V2Accent);
            SetMultiplayerCanvasRect(fillObject.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(1f, 0.3f), new Vector2(3f, 3f), new Vector2(-3f, -1f));
            multiplayerCanvasTransferFill = fillObject.GetComponent<Image>();
            multiplayerCanvasTransferFill.type = Image.Type.Filled;
            multiplayerCanvasTransferFill.fillMethod = Image.FillMethod.Horizontal;
            multiplayerCanvasTransferFill.fillOrigin = 0;
            multiplayerCanvasTransferPhaseText = CreateMultiplayerCanvasText(background.transform, "Phase", MultiplayerTransferTest.Phase, 12, FontStyle.Bold, TextAnchor.MiddleLeft, V2TextColor, new Vector2(0.015f, 0.34f), new Vector2(0.4f, 1f));
            multiplayerCanvasTransferDetailText = CreateMultiplayerCanvasText(background.transform, "Detail", MultiplayerTransferTest.Detail, 11, FontStyle.Normal, TextAnchor.MiddleRight, V2Muted, new Vector2(0.4f, 0.34f), new Vector2(0.985f, 1f));
            UpdateMultiplayerTransferTestUi();
        }

        private void CreateV2BorderFrame(Transform parent)
        {
            CreateV2BorderEdge(parent, "Border Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -2f), new Vector2(0f, 0f));
            CreateV2BorderEdge(parent, "Border Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 2f));
            CreateV2BorderEdge(parent, "Border Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(2f, 0f));
            CreateV2BorderEdge(parent, "Border Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-2f, 0f), new Vector2(0f, 0f));
        }

        private void CreateV2BorderEdge(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var edge = CreateMultiplayerCanvasImage(parent, name, V2Border);
            var rect = edge.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            edge.GetComponent<Image>().raycastTarget = false;
        }

        private void RunMultiplayerCanvasSmokeTestV2(Button launcher, Button close)
        {
            var previousMode = MultiplayerMenu.ConnectionMode;
            MultiplayerMenu.ConnectionMode = MultiplayerConnectionMode.Direct;
            try
            {
                launcher.onClick.Invoke();
                if (multiplayerCanvasPanel == null || !multiplayerCanvasPanel.activeSelf)
                {
                    throw new InvalidOperationException("v2-launcher-did-not-open-menu");
                }

                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Host);
                if (multiplayerCanvasPortInput == null || multiplayerCanvasTransferFill == null)
                {
                    throw new InvalidOperationException("v2-host-page-controls-missing");
                }

                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Join);
                if (multiplayerCanvasHostInput == null || multiplayerCanvasPortInput == null)
                {
                    throw new InvalidOperationException("v2-join-page-controls-missing");
                }

                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Status);
                if (multiplayerCanvasStatusValuesText == null || multiplayerCanvasTransferFill == null)
                {
                    throw new InvalidOperationException("v2-status-page-values-missing");
                }

                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Settings);
                if (multiplayerCanvasHostInput == null || multiplayerCanvasPortInput == null)
                {
                    throw new InvalidOperationException("v2-settings-page-controls-missing");
                }

                close.onClick.Invoke();
                if (multiplayerCanvasPanel.activeSelf)
                {
                    throw new InvalidOperationException("v2-close-did-not-hide-menu");
                }

                if (multiplayerCanvasInGameHud == null
                    || multiplayerCanvasInGameHud.transform.Find("Resync") == null)
                {
                    throw new InvalidOperationException("v2-in-game-hud-controls-missing");
                }

                if (!MultiplayerTransferTestState.RunSmokeTest(out var transferDetail))
                {
                    throw new InvalidOperationException("v2-transfer-state-smoke-test-failed " + transferDetail);
                }

                ShowMultiplayerCanvasPageV2(MultiplayerMenuPage.Home);
                LogReplicationInfo("Going Cooperative multiplayer Canvas UI v2 smoke test passed pages=home,host,join,status,settings transferProgress=ready inGameHud=ready.");
            }
            finally
            {
                MultiplayerMenu.ConnectionMode = previousMode;
            }
        }
    }
}
