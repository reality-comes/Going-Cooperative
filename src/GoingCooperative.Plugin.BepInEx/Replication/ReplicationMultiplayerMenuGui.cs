using System.Globalization;
using GoingCooperative.Core;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static int replicationMultiplayerMenuTab;
        private static string replicationMultiplayerMenuHostInput = string.Empty;
        private static string replicationMultiplayerMenuPortInput = string.Empty;

        private void DrawReplicationMultiplayerMenuGui()
        {
            TryLoadReplicationConfig(this);
            if (!replicationConfigMultiplayerMenuEnabled)
            {
                return;
            }

            EnsureReplicationMultiplayerMenuInputs();

            GUI.Box(new Rect(16, 16, 430, 350), "Going Cooperative Multiplayer");
            GUILayout.BeginArea(new Rect(28, 42, 406, 312));
            GUILayout.Label("Prototype menu enabled by replication.cfg multiplayerMenu=true");
            GUILayout.Label("Runtime: " + (replicationConfigEnabled ? "enabled" : "disabled")
                + " | role: " + (replicationConfigHostMode ? "host" : "client"));
            GUILayout.Label("Endpoint: " + replicationConfigHost + ":" + replicationConfigPort.ToString(CultureInfo.InvariantCulture));
            GUILayout.Label("Plugin: " + GoingCooperativeConstants.Version + " | transport: UDP");

            replicationMultiplayerMenuTab = GUILayout.SelectionGrid(
                replicationMultiplayerMenuTab,
                new[] { "Host", "Join", "Direct", "Status" },
                4);

            GUILayout.Space(8f);
            switch (replicationMultiplayerMenuTab)
            {
                case 0:
                    DrawReplicationMultiplayerHostTab();
                    break;
                case 1:
                    DrawReplicationMultiplayerJoinTab();
                    break;
                case 2:
                    DrawReplicationMultiplayerDirectTab();
                    break;
                default:
                    DrawReplicationMultiplayerStatusTab();
                    break;
            }

            GUILayout.EndArea();
        }

        private static void EnsureReplicationMultiplayerMenuInputs()
        {
            if (string.IsNullOrWhiteSpace(replicationMultiplayerMenuHostInput))
            {
                replicationMultiplayerMenuHostInput = replicationConfigHost;
            }

            if (string.IsNullOrWhiteSpace(replicationMultiplayerMenuPortInput))
            {
                replicationMultiplayerMenuPortInput = replicationConfigPort.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void DrawReplicationMultiplayerHostTab()
        {
            GUILayout.Label("Host session");
            GUILayout.Label("Current config controls hosting. UI start/stop is not wired yet.");
            GUILayout.Label("Mode required: mode=host");
            GUILayout.Label("Port: " + replicationConfigPort.ToString(CultureInfo.InvariantCulture));
            DrawReplicationMultiplayerDisabledButton("Start Hosting");
            DrawReplicationMultiplayerDisabledButton("Stop Hosting");
        }

        private static void DrawReplicationMultiplayerJoinTab()
        {
            GUILayout.Label("LAN discovery");
            GUILayout.Label("Discovery adapter is not wired in this build.");
            GUILayout.Label("No hosts are listed until real discovery replies are implemented.");
            DrawReplicationMultiplayerDisabledButton("Refresh");
            DrawReplicationMultiplayerDisabledButton("Join Selected");
        }

        private static void DrawReplicationMultiplayerDirectTab()
        {
            GUILayout.Label("Direct connect");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host", GUILayout.Width(44f));
            replicationMultiplayerMenuHostInput = GUILayout.TextField(replicationMultiplayerMenuHostInput, GUILayout.Width(210f));
            GUILayout.Label("Port", GUILayout.Width(36f));
            replicationMultiplayerMenuPortInput = GUILayout.TextField(replicationMultiplayerMenuPortInput, GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            GUILayout.Label("Preview config:");
            GUILayout.Label("mode=client host=" + replicationMultiplayerMenuHostInput + " port=" + replicationMultiplayerMenuPortInput);
            GUILayout.Label("Connection probe and Join stay disabled until hello probing is wired.");
            DrawReplicationMultiplayerDisabledButton("Test Connection");
            DrawReplicationMultiplayerDisabledButton("Join");
        }

        private void DrawReplicationMultiplayerStatusTab()
        {
            GUILayout.Label("Runtime status");
            GUILayout.Label("Role: " + (replicationConfigHostMode ? "Host" : "Client"));
            GUILayout.Label("Snapshots: " + (replicationConfigHostMode ? "send=" + replicationConfigSendSnapshots : "apply=" + replicationConfigApplySnapshots));
            GUILayout.Label("Client simulation suppression: " + replicationConfigSuppressClientSimulation);
            GUILayout.Label("World object deltas: " + replicationConfigWorldObjectDeltaMode);
            if (!replicationConfigHostMode)
            {
                if (GUILayout.Button("Resync From Host"))
                {
                    RequestReplicationHostSaveResync("multiplayer-menu");
                }

                GUILayout.Label(string.IsNullOrEmpty(replicationLastResyncSummary) ? "Resync: ready" : "Resync: " + TrimFingerprintText(replicationLastResyncSummary, 90));
            }
        }

        private static void DrawReplicationMultiplayerDisabledButton(string label)
        {
            var previousEnabled = GUI.enabled;
            GUI.enabled = false;
            GUILayout.Button(label);
            GUI.enabled = previousEnabled;
        }
    }
}
