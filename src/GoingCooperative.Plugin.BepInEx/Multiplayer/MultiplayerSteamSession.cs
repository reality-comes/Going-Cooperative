using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using GoingCooperative.Core;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        // The only Steam-related state on the plugin class itself. All
        // Steamworks-backed objects live in SteamSessionState so that a
        // missing com.rlabrecque.steamworks.net.dll (GOG installs) can only
        // fault inside the guarded trampolines below, never during plugin
        // startup or menu rendering.
        private bool multiplayerSteamInitFailed;

        private bool IsMultiplayerSteamOffered(out string statusDetail)
        {
            if (!replicationConfigSteamNetworking)
            {
                statusDetail = "Steam mode is disabled (steamNetworking=false in replication.cfg).";
                return false;
            }

            if (multiplayerSteamInitFailed)
            {
                statusDetail = "Steam support failed to initialize; see plugin.log.";
                return false;
            }

            try
            {
                return IsMultiplayerSteamOfferedCore(out statusDetail);
            }
            catch (Exception ex)
            {
                multiplayerSteamInitFailed = true;
                statusDetail = "Steam assemblies are unavailable (" + ex.GetType().Name + ").";
                LogReplicationWarning("Going Cooperative Steam availability probe failed error="
                    + FormatReflectionExceptionDetail(ex));
                return false;
            }
        }

        private void UpdateMultiplayerSteamRuntime()
        {
            if (!replicationConfigSteamNetworking || multiplayerSteamInitFailed)
            {
                return;
            }

            try
            {
                UpdateMultiplayerSteamRuntimeCore();
            }
            catch (Exception ex)
            {
                multiplayerSteamInitFailed = true;
                LogReplicationWarning("Going Cooperative Steam runtime pump failed error="
                    + FormatReflectionExceptionDetail(ex));
            }
        }

        private bool TryStartMultiplayerSteamHost(string portText, out string detail)
        {
            if (!IsMultiplayerSteamOffered(out detail))
            {
                return false;
            }

            try
            {
                return TryStartMultiplayerSteamHostCore(portText, out detail);
            }
            catch (Exception ex)
            {
                detail = "Steam host failed: " + FormatReflectionExceptionDetail(ex);
                LogReplicationWarning("Going Cooperative " + detail);
                return false;
            }
        }

        private bool TryJoinMultiplayerSteamHost(string steamIdText, out string detail)
        {
            if (!IsMultiplayerSteamOffered(out detail))
            {
                return false;
            }

            try
            {
                return TryJoinMultiplayerSteamHostCore(steamIdText, out detail);
            }
            catch (Exception ex)
            {
                detail = "Steam join failed: " + FormatReflectionExceptionDetail(ex);
                LogReplicationWarning("Going Cooperative " + detail);
                return false;
            }
        }

        private void StopMultiplayerSteamSession()
        {
            if (!replicationConfigSteamNetworking || multiplayerSteamInitFailed)
            {
                return;
            }

            try
            {
                StopMultiplayerSteamSessionCore();
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative Steam session stop failed error="
                    + FormatReflectionExceptionDetail(ex));
            }
        }

        private string GetMultiplayerSteamStatusLine()
        {
            if (!replicationConfigSteamNetworking)
            {
                return "Steam mode disabled in replication.cfg.";
            }

            if (!IsMultiplayerSteamOffered(out var availability))
            {
                return availability;
            }

            try
            {
                return GetMultiplayerSteamStatusLineCore(availability);
            }
            catch (Exception ex)
            {
                multiplayerSteamInitFailed = true;
                return "Steam status unavailable (" + ex.GetType().Name + ").";
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool IsMultiplayerSteamOfferedCore(out string statusDetail)
        {
            return Steam.SteamAvailability.IsAvailable(out statusDetail);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateMultiplayerSteamRuntimeCore()
        {
            if (Steam.SteamSessionState.Lobby == null && Steam.SteamAvailability.IsAvailable(out _))
            {
                var lobby = new Steam.SteamLobbySession(AppendPluginLog);
                lobby.ListenForInvites();
                lobby.InviteAccepted += OnMultiplayerSteamInviteAccepted;
                lobby.HostResolved += OnMultiplayerSteamHostResolved;
                Steam.SteamSessionState.Lobby = lobby;
            }

            Steam.SteamSessionState.Tunnel?.Pump();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryStartMultiplayerSteamHostCore(string portText, out string detail)
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

            var tunnel = Steam.SteamSessionState.Tunnel ??= new Steam.SteamTunnel(AppendPluginLog);
            tunnel.StartHost(port, IsMultiplayerSteamPeerAllowed);
            Steam.SteamSessionState.Lobby?.CreateFriendsLobby(MultiplayerMenu.SessionName, GoingCooperativeConstants.Version);
            detail = "Hosting over Steam. Invite a friend from the Steam overlay.";
            LogReplicationInfo("Going Cooperative Steam host session started port="
                + port.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryJoinMultiplayerSteamHostCore(string steamIdText, out string detail)
        {
            var trimmed = (steamIdText ?? string.Empty).Trim();
            ulong hostSteamId;
            if (trimmed.Length != 0
                && ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed != 0UL)
            {
                hostSteamId = parsed;
            }
            else if (Steam.SteamSessionState.PendingHostId != 0UL)
            {
                hostSteamId = Steam.SteamSessionState.PendingHostId;
            }
            else
            {
                detail = "Enter the host's SteamID64, or accept a Steam invite.";
                return false;
            }

            var tunnel = Steam.SteamSessionState.Tunnel ??= new Steam.SteamTunnel(AppendPluginLog);
            if (!tunnel.StartClient(hostSteamId, out var localPort, out detail))
            {
                return false;
            }

            if (!TryJoinMultiplayerHost("127.0.0.1", localPort.ToString(CultureInfo.InvariantCulture), out detail))
            {
                tunnel.Stop();
                return false;
            }

            detail = "Connecting to the host through Steam.";
            LogReplicationInfo("Going Cooperative Steam join started host="
                + hostSteamId.ToString(CultureInfo.InvariantCulture)
                + " bridgePort=" + localPort.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool IsMultiplayerSteamPeerAllowed(ulong steamId)
        {
            var lobby = Steam.SteamSessionState.Lobby;
            if (lobby != null && lobby.IsLobbyMember(steamId))
            {
                return true;
            }

            try
            {
                var relationship = Steamworks.SteamFriends.GetFriendRelationship(new Steamworks.CSteamID(steamId));
                if (relationship == Steamworks.EFriendRelationship.k_EFriendRelationshipFriend)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative Steam peer relationship check failed error="
                    + FormatReflectionExceptionDetail(ex));
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnMultiplayerSteamInviteAccepted(ulong lobbyId)
        {
            MultiplayerMenu.ConnectionMode = MultiplayerConnectionMode.Steam;
            MultiplayerMenu.StatusMessage = "Steam invite accepted. Joining the lobby.";
            Steam.SteamSessionState.Lobby?.JoinLobby(lobbyId);
            SetMultiplayerCanvasOpen(true);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void OnMultiplayerSteamHostResolved(ulong hostSteamId)
        {
            Steam.SteamSessionState.PendingHostId = hostSteamId;
            MultiplayerMenu.SteamHostIdText = hostSteamId.ToString(CultureInfo.InvariantCulture);
            if (TryJoinMultiplayerSteamHost(MultiplayerMenu.SteamHostIdText, out var detail))
            {
                MultiplayerMenu.StatusMessage = detail;
                ShowMultiplayerCanvasPage(MultiplayerMenuPage.Status);
            }
            else
            {
                MultiplayerMenu.StatusMessage = detail;
                SetMultiplayerCanvasMessage(detail);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StopMultiplayerSteamSessionCore()
        {
            Steam.SteamSessionState.PendingHostId = 0UL;
            Steam.SteamSessionState.Tunnel?.Stop();
            Steam.SteamSessionState.Lobby?.LeaveLobby();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string GetMultiplayerSteamStatusLineCore(string availability)
        {
            var text = availability;
            var lobby = Steam.SteamSessionState.Lobby;
            if (lobby != null && lobby.HasLobby)
            {
                text += "  ·  " + lobby.StatusText;
            }

            var tunnel = Steam.SteamSessionState.Tunnel;
            if (tunnel != null && tunnel.IsRunning)
            {
                text += "  ·  " + tunnel.Detail;
            }

            return text;
        }
    }
}
