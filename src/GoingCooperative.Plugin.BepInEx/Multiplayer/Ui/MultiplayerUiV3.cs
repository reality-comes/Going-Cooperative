using System;
using System.Globalization;
using GoingCooperative.Core;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace GoingCooperative.Plugin.BepInEx
{
    /// <summary>
    /// Gated (v3) multiplayer menu. Selected by uiV3=true in replication.cfg
    /// (the default); uiV3=false rolls back to the v2 tab menu unchanged
    /// (and ui=classic still restores the original menu below that).
    ///
    /// v3 is a single fully gated flow instead of five switchable tabs:
    /// HOST / JOIN choice first, then only that path's options, then the
    /// session screen once a session starts. There is no settings page.
    /// The v3 builders populate the same field slots the shared per-frame
    /// updaters (connection label, status values, transfer progress, resync
    /// overlay, in-game HUD) already maintain, so session behavior is
    /// identical to v2 and classic.
    /// </summary>
    public sealed partial class GoingCooperativePlugin
    {
        private void EnsureMultiplayerCanvasGuiV3()
        {
            if (multiplayerCanvasRoot != null)
            {
                return;
            }

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

            var window = CreateMultiplayerCanvasImage(multiplayerCanvasPanel.transform, "Window", V2Panel);
            SetMultiplayerCanvasRect(window.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-410f, -300f), new Vector2(410f, 300f));
            CreateV2BorderFrame(window.transform);

            var header = CreateMultiplayerCanvasImage(window.transform, "Header", V2Header);
            SetMultiplayerCanvasRect(header.GetComponent<RectTransform>(), new Vector2(0f, 0.9f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            var title = CreateV2Heading(header.transform, "Title", "GOING COOPERATIVE", 24f, TextAlignmentOptions.MidlineLeft);
            SetMultiplayerCanvasRect(title.rectTransform, new Vector2(0.025f, 0f), new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero);
            multiplayerCanvasConnectionText = CreateMultiplayerCanvasText(header.transform, "Connection", GetMultiplayerConnectionLabel(), 14, FontStyle.Bold, TextAnchor.MiddleRight, V2Accent, new Vector2(0.5f, 0f), new Vector2(0.925f, 1f));
            var close = CreateV2Button(header.transform, "Close", "X", () => SetMultiplayerCanvasOpen(false), V2Header, 17f);
            SetMultiplayerCanvasRect(close.GetComponent<RectTransform>(), new Vector2(0.945f, 0.2f), new Vector2(0.99f, 0.8f), Vector2.zero, Vector2.zero);

            multiplayerCanvasContent = CreateMultiplayerCanvasImage(window.transform, "Content", Color.clear);
            SetMultiplayerCanvasRect(multiplayerCanvasContent.GetComponent<RectTransform>(), new Vector2(0.03f, 0.035f), new Vector2(0.97f, 0.88f), Vector2.zero, Vector2.zero);

            multiplayerCanvasPanel.SetActive(false);
            ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Home);
            CreateMultiplayerResyncOverlay();
            RunMultiplayerCanvasSmokeTestV3(launcher, close);
            LogReplicationInfo("Going Cooperative multiplayer Canvas UI v3 created screen="
                + Screen.width.ToString(CultureInfo.InvariantCulture) + "x" + Screen.height.ToString(CultureInfo.InvariantCulture));
        }

        private void ShowMultiplayerCanvasPageV3(MultiplayerMenuPage page)
        {
            if (multiplayerCanvasContent == null)
            {
                return;
            }

            // v3 has no settings page; any legacy request for it lands on Home.
            if (page == MultiplayerMenuPage.Settings)
            {
                page = MultiplayerMenuPage.Home;
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
                    BuildV3HostPage();
                    break;
                case MultiplayerMenuPage.Join:
                    BuildV3JoinPage();
                    break;
                case MultiplayerMenuPage.Status:
                    BuildV3SessionPage();
                    break;
                default:
                    BuildV3HomePage();
                    break;
            }
        }

        private void BuildV3HomePage()
        {
            var content = multiplayerCanvasContent!.transform;
            var heading = CreateV2Heading(content, "Page Title", "Play together", 23f, TextAlignmentOptions.TopLeft);
            SetMultiplayerCanvasRect(heading.rectTransform, new Vector2(0f, 0.88f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(content, "Page Subtitle", "The host runs the world; the friend plays inside it. Pick your role to continue.", 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.8f), new Vector2(1f, 0.9f));

            var hostCard = BuildV2Card(content, "Host Card", new Vector2(0f, 0.16f), new Vector2(0.49f, 0.74f));
            var hostTitle = CreateV2Heading(hostCard.transform, "Title", "HOST", 26f, TextAlignmentOptions.TopLeft);
            SetMultiplayerCanvasRect(hostTitle.rectTransform, new Vector2(0.08f, 0.68f), new Vector2(0.92f, 0.92f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(hostCard.transform, "Body", "Run your settlement for both players. Your save is sent to the joining player automatically.", 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.08f, 0.34f), new Vector2(0.92f, 0.66f));
            var hostButton = CreateV2Button(hostCard.transform, "Host", "HOST A GAME", () => ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Host), V2Accent, 16f);
            SetMultiplayerCanvasRect(hostButton.GetComponent<RectTransform>(), new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.28f), Vector2.zero, Vector2.zero);

            var joinCard = BuildV2Card(content, "Join Card", new Vector2(0.51f, 0.16f), new Vector2(1f, 0.74f));
            var joinTitle = CreateV2Heading(joinCard.transform, "Title", "JOIN", 26f, TextAlignmentOptions.TopLeft);
            SetMultiplayerCanvasRect(joinTitle.rectTransform, new Vector2(0.08f, 0.68f), new Vector2(0.92f, 0.92f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(joinCard.transform, "Body", "Play inside a friend's settlement. Accept a Steam invite or enter the address they share with you.", 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.08f, 0.34f), new Vector2(0.92f, 0.66f));
            var joinButton = CreateV2Button(joinCard.transform, "Join", "JOIN A GAME", () => ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Join), V2Accent, 16f);
            SetMultiplayerCanvasRect(joinButton.GetComponent<RectTransform>(), new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.28f), Vector2.zero, Vector2.zero);

            CreateMultiplayerCanvasText(content, "Notice", "Experimental build · both computers need the same game version and the same Going Cooperative release.", 12, FontStyle.Normal, TextAnchor.MiddleCenter, V2Muted, new Vector2(0.02f, 0.02f), new Vector2(0.98f, 0.12f));
        }

        private void BuildV3StepHeading(string title, string subtitle, MultiplayerMenuPage backPage)
        {
            var content = multiplayerCanvasContent!.transform;
            var back = CreateV2Button(content, "Back", "< BACK", () => ShowMultiplayerCanvasPageV3(backPage), V2Card, 12f);
            SetMultiplayerCanvasRect(back.GetComponent<RectTransform>(), new Vector2(0f, 0.9f), new Vector2(0.11f, 1f), Vector2.zero, Vector2.zero);
            var heading = CreateV2Heading(content, "Page Title", title, 21f, TextAlignmentOptions.MidlineLeft);
            SetMultiplayerCanvasRect(heading.rectTransform, new Vector2(0.13f, 0.9f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
            CreateMultiplayerCanvasText(content, "Page Subtitle", subtitle, 13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.815f), new Vector2(1f, 0.89f));
        }

        private void BuildV3ModeSelector(Transform content, float bottom, float top)
        {
            CreateMultiplayerCanvasText(content, "Mode Label", "CONNECTION", 13, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, bottom + 0.045f), new Vector2(0.14f, top - 0.02f));
            var steamOffered = IsMultiplayerSteamOffered(out var steamDetail);
            var direct = CreateV2Button(content, "Mode Direct", "DIRECT (IP)", () => SetV3ConnectionMode(MultiplayerConnectionMode.Direct),
                MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Direct ? V2AccentDark : V2Card, 13f);
            SetMultiplayerCanvasRect(direct.GetComponent<RectTransform>(), new Vector2(0.15f, bottom + 0.03f), new Vector2(0.33f, top - 0.01f), Vector2.zero, Vector2.zero);
            var steam = CreateV2Button(content, "Mode Steam", "STEAM", () => SetV3ConnectionMode(MultiplayerConnectionMode.Steam),
                MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam ? V2AccentDark : (steamOffered ? V2Card : V2Disabled), 13f);
            SetMultiplayerCanvasRect(steam.GetComponent<RectTransform>(), new Vector2(0.35f, bottom + 0.03f), new Vector2(0.53f, top - 0.01f), Vector2.zero, Vector2.zero);
            var hint = replicationConfigSteamNetworking
                ? steamDetail
                : "Steam mode is available after setting steamNetworking=true in replication.cfg.";
            CreateMultiplayerCanvasText(content, "Mode Hint", hint, 12, FontStyle.Normal, TextAnchor.MiddleLeft, V2Muted, new Vector2(0.56f, bottom + 0.02f), new Vector2(1f, top));
        }

        private void SetV3ConnectionMode(MultiplayerConnectionMode mode)
        {
            if (mode == MultiplayerConnectionMode.Steam && !IsMultiplayerSteamOffered(out var detail))
            {
                MultiplayerMenu.StatusMessage = detail;
                ShowMultiplayerCanvasPageV3(multiplayerCanvasPage);
                return;
            }

            MultiplayerMenu.ConnectionMode = mode;
            ShowMultiplayerCanvasPageV3(multiplayerCanvasPage);
        }

        private void BuildV3HostPage()
        {
            EnsureDirectHostSessionCode();
            var content = multiplayerCanvasContent!.transform;
            var steamMode = MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam;
            BuildV3StepHeading("Host a game", steamMode
                ? "The selected save is sent to your friend through Steam. No port forwarding needed."
                : "The selected save is sent to the joining player over your LAN or VPN.", MultiplayerMenuPage.Home);
            BuildV3ModeSelector(content, 0.66f, 0.8f);

            var saveCard = BuildV2Card(content, "Save Card", new Vector2(0f, 0.46f), new Vector2(1f, 0.64f));
            CreateMultiplayerCanvasText(saveCard.transform, "Save Label", "WORLD TO HOST", 12, FontStyle.Bold, TextAnchor.MiddleLeft, V2Muted, new Vector2(0.03f, 0.55f), new Vector2(0.6f, 0.95f));
            CreateMultiplayerCanvasText(saveCard.transform, "Selected Save", GetSelectedMultiplayerSaveLabel(), 15, FontStyle.Bold, TextAnchor.MiddleLeft, V2TextColor, new Vector2(0.03f, 0.08f), new Vector2(0.82f, 0.6f));
            var previousSave = CreateV2Button(saveCard.transform, "Previous Save", "<", SelectPreviousMultiplayerSave, V2Field, 14f);
            SetMultiplayerCanvasRect(previousSave.GetComponent<RectTransform>(), new Vector2(0.84f, 0.15f), new Vector2(0.905f, 0.85f), Vector2.zero, Vector2.zero);
            var nextSave = CreateV2Button(saveCard.transform, "Next Save", ">", SelectNextMultiplayerSave, V2Field, 14f);
            SetMultiplayerCanvasRect(nextSave.GetComponent<RectTransform>(), new Vector2(0.92f, 0.15f), new Vector2(0.99f, 0.85f), Vector2.zero, Vector2.zero);

            multiplayerCanvasPortInput = CreateV2Input("Listen port", MultiplayerMenu.PortText, 0.34f);
            if (steamMode)
            {
                CreateMultiplayerCanvasText(content, "Steam Info", GetMultiplayerSteamStatusLine()
                    + "\nHOST first, then invite a friend from the Steam overlay. Their game connects through Steam relay.",
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.16f), new Vector2(0.66f, 0.32f));
                var invite = CreateV2Button(content, "Invite", "INVITE FRIENDS", OpenV2SteamInviteDialog, V2Card, 13f);
                SetMultiplayerCanvasRect(invite.GetComponent<RectTransform>(), new Vector2(0.68f, 0.21f), new Vector2(0.98f, 0.31f), Vector2.zero, Vector2.zero);
            }
            else
            {
                if (replicationConfigDirectTransportSecurityV1)
                {
                    multiplayerCanvasSessionInput = CreateV2Input("Session code", MultiplayerMenu.DirectSessionCode, 0.23f);
                }

                CreateMultiplayerCanvasText(content, "Preflight", "Share this address: " + GetMultiplayerLanAddressSummary() + ":" + MultiplayerMenu.PortText
                    + "   ·   Plugin " + GoingCooperativeConstants.Version + "   ·   Protocol " + GetMultiplayerProtocolLabel(),
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.155f), new Vector2(1f, 0.21f));
            }

            var start = CreateV2Button(content, "Start", steamMode ? "HOST OVER STEAM" : "HOST", StartMultiplayerCanvasHostV3, V2Accent, 15f);
            SetMultiplayerCanvasRect(start.GetComponent<RectTransform>(), new Vector2(0f, 0.02f), new Vector2(0.28f, 0.125f), Vector2.zero, Vector2.zero);
            multiplayerCanvasMessageText = CreateMultiplayerCanvasText(content, "Message", MultiplayerMenu.StatusMessage, 12, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.31f, 0.02f), new Vector2(1f, 0.125f));
        }

        private void BuildV3JoinPage()
        {
            var content = multiplayerCanvasContent!.transform;
            var steamMode = MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Steam;
            BuildV3StepHeading("Join a game", steamMode
                ? "Accept a Steam invite and this connects automatically — or enter the host's SteamID64."
                : "Enter the address the host shared with you.", MultiplayerMenuPage.Home);
            BuildV3ModeSelector(content, 0.66f, 0.8f);

            if (steamMode)
            {
                multiplayerCanvasHostInput = CreateV2Input("Host SteamID64", MultiplayerMenu.SteamHostIdText, 0.5f);
                CreateMultiplayerCanvasText(content, "Steam Hint", GetMultiplayerSteamStatusLine()
                    + "\nAccepting a Steam invite fills this in and connects on its own; the field is only needed for manual joins between friends.",
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.24f), new Vector2(1f, 0.46f));
            }
            else
            {
                multiplayerCanvasHostInput = CreateV2Input("Host address", MultiplayerMenu.HostAddress, 0.5f);
                multiplayerCanvasPortInput = CreateV2Input("Port", MultiplayerMenu.PortText, 0.38f);
                if (replicationConfigDirectTransportSecurityV1)
                {
                    multiplayerCanvasSessionInput = CreateV2Input("Session code", MultiplayerMenu.DirectSessionCode, 0.26f);
                }

                CreateMultiplayerCanvasText(content, "Compatibility", replicationConfigDirectTransportSecurityV1
                    ? "A matching plugin build, protocol, and session code must respond before the session becomes Connected."
                    : "A matching plugin build and protocol must respond before the session becomes Connected.",
                    13, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0f, 0.155f), new Vector2(1f, 0.23f));
            }

            var connect = CreateV2Button(content, "Connect", steamMode ? "CONNECT VIA STEAM" : "CONNECT", StartMultiplayerCanvasJoinV3, V2Accent, 15f);
            SetMultiplayerCanvasRect(connect.GetComponent<RectTransform>(), new Vector2(0f, 0.02f), new Vector2(0.28f, 0.125f), Vector2.zero, Vector2.zero);
            multiplayerCanvasMessageText = CreateMultiplayerCanvasText(content, "Message", MultiplayerMenu.StatusMessage, 12, FontStyle.Normal, TextAnchor.UpperLeft, V2Muted, new Vector2(0.31f, 0.02f), new Vector2(1f, 0.125f));
        }

        private void BuildV3SessionPage()
        {
            var content = multiplayerCanvasContent!.transform;
            BuildV3StepHeading("Session", "Live replication and compatibility state.", MultiplayerMenuPage.Home);
            var card = BuildV2Card(content, "Status Card", new Vector2(0f, 0.3f), new Vector2(1f, 0.78f));
            CreateMultiplayerCanvasText(card.transform, "Labels", "Connection\nRole\nEndpoint\nProtocol\nPlugin\nHandshake", 14, FontStyle.Bold, TextAnchor.UpperLeft, V2Muted, new Vector2(0.03f, 0.08f), new Vector2(0.25f, 0.92f));
            multiplayerCanvasStatusValuesText = CreateMultiplayerCanvasText(card.transform, "Values", BuildMultiplayerCanvasStatusValues(), 14, FontStyle.Normal, TextAnchor.UpperLeft, V2TextColor, new Vector2(0.26f, 0.08f), new Vector2(0.97f, 0.92f));
            if (replicationConfigSteamNetworking)
            {
                CreateMultiplayerCanvasText(content, "Steam Status", GetMultiplayerSteamStatusLine(), 12, FontStyle.Normal, TextAnchor.MiddleLeft, V2Muted, new Vector2(0f, 0.23f), new Vector2(1f, 0.29f));
            }

            var disconnect = CreateV2Button(content, "Disconnect", "DISCONNECT", () =>
            {
                StopMultiplayerSession();
                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Home);
            }, V2Card, 14f);
            SetMultiplayerCanvasRect(disconnect.GetComponent<RectTransform>(), new Vector2(0f, 0.11f), new Vector2(0.24f, 0.215f), Vector2.zero, Vector2.zero);
            var play = CreateV2Button(content, "Play", "PLAY", MarkMultiplayerReadyToPlay, V2Accent, 14f);
            SetMultiplayerCanvasRect(play.GetComponent<RectTransform>(), new Vector2(0.26f, 0.11f), new Vector2(0.5f, 0.215f), Vector2.zero, Vector2.zero);
            if (!replicationConfigHostMode)
            {
                var resync = CreateV2Button(content, "Resync", "FULL RESYNC", RequestMultiplayerCanvasResync, V2AccentDark, 14f);
                SetMultiplayerCanvasRect(resync.GetComponent<RectTransform>(), new Vector2(0.52f, 0.11f), new Vector2(0.76f, 0.215f), Vector2.zero, Vector2.zero);
            }

            CreateV2TransferProgress(0.0f, 0.09f);
        }

        private void StartMultiplayerCanvasHostV3()
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
                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Status);
            }
            else
            {
                SetMultiplayerCanvasMessage(detail);
            }
        }

        private void StartMultiplayerCanvasJoinV3()
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
                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Status);
            }
            else
            {
                SetMultiplayerCanvasMessage(detail);
            }
        }

        private void RunMultiplayerCanvasSmokeTestV3(Button launcher, Button close)
        {
            var previousMode = MultiplayerMenu.ConnectionMode;
            MultiplayerMenu.ConnectionMode = MultiplayerConnectionMode.Direct;
            try
            {
                launcher.onClick.Invoke();
                if (multiplayerCanvasPanel == null || !multiplayerCanvasPanel.activeSelf)
                {
                    throw new InvalidOperationException("v3-launcher-did-not-open-menu");
                }

                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Home);
                if (multiplayerCanvasContent == null
                    || multiplayerCanvasContent.transform.Find("Host Card") == null
                    || multiplayerCanvasContent.transform.Find("Join Card") == null)
                {
                    throw new InvalidOperationException("v3-home-cards-missing");
                }

                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Host);
                if (multiplayerCanvasPortInput == null || multiplayerCanvasContent.transform.Find("Back") == null)
                {
                    throw new InvalidOperationException("v3-host-page-controls-missing");
                }

                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Join);
                if (multiplayerCanvasHostInput == null || multiplayerCanvasPortInput == null)
                {
                    throw new InvalidOperationException("v3-join-page-controls-missing");
                }

                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Status);
                if (multiplayerCanvasStatusValuesText == null || multiplayerCanvasTransferFill == null)
                {
                    throw new InvalidOperationException("v3-session-page-values-missing");
                }

                // The settings page is intentionally removed; requesting it must land on Home.
                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Settings);
                if (multiplayerCanvasPage != MultiplayerMenuPage.Home)
                {
                    throw new InvalidOperationException("v3-settings-page-not-gated-to-home");
                }

                close.onClick.Invoke();
                if (multiplayerCanvasPanel.activeSelf)
                {
                    throw new InvalidOperationException("v3-close-did-not-hide-menu");
                }

                if (multiplayerCanvasInGameHud == null
                    || multiplayerCanvasInGameHud.transform.Find("Resync") == null)
                {
                    throw new InvalidOperationException("v3-in-game-hud-controls-missing");
                }

                if (!MultiplayerTransferTestState.RunSmokeTest(out var transferDetail))
                {
                    throw new InvalidOperationException("v3-transfer-state-smoke-test-failed " + transferDetail);
                }

                ShowMultiplayerCanvasPageV3(MultiplayerMenuPage.Home);
                LogReplicationInfo("Going Cooperative multiplayer Canvas UI v3 smoke test passed pages=home,host,join,session settingsGatedToHome=true transferProgress=ready inGameHud=ready.");
            }
            finally
            {
                MultiplayerMenu.ConnectionMode = previousMode;
            }
        }
    }
}
