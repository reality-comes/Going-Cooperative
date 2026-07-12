using System;
using System.Globalization;
using GoingCooperative.Core.Replication;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private bool TryStartMultiplayerHost(string portText, out string detail)
        {
            if (!TryParseMultiplayerPort(portText, out var port, out detail))
            {
                return false;
            }

            ApplyMultiplayerSessionOptions(hostMode: true, "0.0.0.0", port);
            if (!StartMultiplayerSaveHost(port, out detail))
            {
                return false;
            }
            return true;
        }

        private bool TryJoinMultiplayerHost(string host, string portText, out string detail)
        {
            host = (host ?? string.Empty).Trim();
            if (host.Length == 0)
            {
                detail = "Enter a host name or IP address.";
                return false;
            }

            if (host.Length > 253)
            {
                detail = "The host name is too long.";
                return false;
            }

            if (!TryParseMultiplayerPort(portText, out var port, out detail))
            {
                return false;
            }

            ApplyMultiplayerSessionOptions(hostMode: false, host, port);
            if (!StartMultiplayerSaveClient(host, port, out detail))
            {
                return false;
            }
            return true;
        }

        private void ApplyMultiplayerSessionOptions(bool hostMode, string host, int port)
        {
            StopReplicationRuntime();
            replicationConfigEnabled = true;
            replicationConfigHostMode = hostMode;
            replicationConfigHost = host;
            replicationConfigPort = port;
            replicationConfigApplySnapshots = !hostMode;
            replicationConfigSuppressClientSimulation = !hostMode;
            // Preserve the original, proven lifecycle: native save loading completes
            // before replication or client authority suppression becomes active.
            replicationConfigEnabled = false;
            replicationRemoteCompatibilityRefused = false;
            replicationRemoteHelloReceived = false;
            LogReplicationInfo("Going Cooperative multiplayer menu applying session role="
                + (hostMode ? "host" : "client")
                + " endpoint=" + host + ":" + port.ToString(CultureInfo.InvariantCulture));
        }

        private void StopMultiplayerSession()
        {
            multiplayerSaveTransfer.Stop();
            StopReplicationRuntime();
            LogReplicationInfo("Going Cooperative multiplayer session stopped from menu.");
        }

        private static bool TryParseMultiplayerPort(string text, out int port, out string detail)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port < 1
                || port > 65535)
            {
                detail = "Port must be a number from 1 to 65535.";
                return false;
            }

            detail = string.Empty;
            return true;
        }

        private string GetMultiplayerConnectionLabel()
        {
            if (!multiplayerSaveTransfer.TransferComplete
                && !string.Equals(multiplayerSaveTransfer.Phase, "Idle", StringComparison.Ordinal))
            {
                return multiplayerSaveTransfer.Phase;
            }

            if (multiplayerSaveTransfer.TransferComplete && !replicationRuntimeStarted)
            {
                return "Connected - ready to load";
            }

            if (!replicationRuntimeStarted)
            {
                return "Disconnected";
            }

            if (replicationRemoteCompatibilityRefused)
            {
                return "Compatibility refused";
            }

            if (replicationRemoteHelloReceived)
            {
                return "Connected";
            }

            return replicationConfigHostMode ? "Waiting for player" : "Connecting";
        }

        private static string GetMultiplayerProtocolLabel()
        {
            return ReplicationPayloadCodec.ProtocolVersion;
        }
    }
}
