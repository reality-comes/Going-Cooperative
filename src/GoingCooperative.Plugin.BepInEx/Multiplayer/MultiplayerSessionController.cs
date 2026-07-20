using System;
using System.Globalization;
using GoingCooperative.Core.Replication;
using GoingCooperative.Core;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static bool replicationDirectSecurityActive;
        private static string replicationDirectSessionCode = string.Empty;

        private bool TryStartMultiplayerHost(string portText, out string detail)
        {
            if (!TryParseMultiplayerPort(portText, out var port, out detail))
            {
                return false;
            }

            if (!ValidateDirectSessionSecurity(hostMode: true, out detail)) return false;

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

            if (!ValidateDirectSessionSecurity(hostMode: false, out detail)) return false;

            ApplyMultiplayerSessionOptions(hostMode: false, host, port);
            if (!StartMultiplayerSaveClient(host, port, out detail))
            {
                return false;
            }
            return true;
        }

        private void ApplyMultiplayerSessionOptions(bool hostMode, string host, int port)
        {
            StopReplicationRuntime(ReplicationTraderPartyResetContext.ScopeChangedSameWorld);
            replicationConfigEnabled = true;
            replicationConfigHostMode = hostMode;
            replicationConfigHost = host;
            replicationConfigPort = port;
            replicationConfigApplySnapshots = !hostMode;
            replicationConfigSuppressClientSimulation = !hostMode;
            replicationDirectSecurityActive = replicationConfigDirectTransportSecurityV1
                && MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Direct;
            replicationDirectSessionCode = replicationDirectSecurityActive
                ? DirectTransportSecurity.NormalizeCode(MultiplayerMenu.DirectSessionCode)
                : string.Empty;
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
            StopMultiplayerSteamSession();
            multiplayerSaveTransfer.Stop();
            StopReplicationRuntime(ReplicationTraderPartyResetContext.StopInPlace);
            replicationDirectSecurityActive = false;
            replicationDirectSessionCode = string.Empty;
            MultiplayerMenu.DirectSessionCode = string.Empty;
            LogReplicationInfo("Going Cooperative multiplayer session stopped from menu.");
        }

        private bool ValidateDirectSessionSecurity(bool hostMode, out string detail)
        {
            if (!replicationConfigDirectTransportSecurityV1 || MultiplayerMenu.ConnectionMode != MultiplayerConnectionMode.Direct)
            {
                detail = string.Empty;
                return true;
            }
            if (hostMode && string.IsNullOrWhiteSpace(MultiplayerMenu.DirectSessionCode))
            {
                MultiplayerMenu.DirectSessionCode = DirectTransportSecurity.GenerateSessionCode();
            }
            if (!DirectTransportSecurity.TryDeriveKey(MultiplayerMenu.DirectSessionCode, out _, out detail)) return false;
            return true;
        }

        private void EnsureDirectHostSessionCode()
        {
            if (replicationConfigDirectTransportSecurityV1
                && MultiplayerMenu.ConnectionMode == MultiplayerConnectionMode.Direct
                && string.IsNullOrWhiteSpace(MultiplayerMenu.DirectSessionCode))
            {
                MultiplayerMenu.DirectSessionCode = DirectTransportSecurity.GenerateSessionCode();
            }
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
