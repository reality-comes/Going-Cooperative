using System;
using System.Globalization;
using Steamworks;

namespace GoingCooperative.Plugin.BepInEx.Steam
{
    /// <summary>
    /// Owns the Steam lobby used as the multiplayer meeting point. The host
    /// creates a friends-only lobby and can open the overlay invite dialog; a
    /// client that accepts an invite joins the lobby and reads the host's
    /// SteamID from the lobby owner. All members are pumped by the game's own
    /// SteamAPI.RunCallbacks loop; every handler runs on the Unity main thread.
    /// </summary>
    internal sealed class SteamLobbySession : IDisposable
    {
        public const string LobbyDataVersionKey = "gc_version";
        public const string LobbyDataSessionKey = "gc_session";

        private readonly Action<string> log;
        private Callback<GameLobbyJoinRequested_t>? joinRequestedCallback;
        private CallResult<LobbyCreated_t>? createResult;
        private CallResult<LobbyEnter_t>? enterResult;
        private CSteamID lobbyId;
        private bool hosting;

        public SteamLobbySession(Action<string> logSink)
        {
            log = logSink;
        }

        /// <summary>Raised on the client when the local player accepts a Steam invite
        /// or uses "Join Game"; the argument is the lobby to join.</summary>
        public event Action<ulong>? InviteAccepted;

        /// <summary>Raised when a lobby join completes and the host SteamID is known.</summary>
        public event Action<ulong>? HostResolved;

        public ulong LobbyId => lobbyId.m_SteamID;

        public bool HasLobby => lobbyId.IsValid();

        public bool IsHosting => hosting && HasLobby;

        public string StatusText { get; private set; } = "No Steam lobby.";

        public void ListenForInvites()
        {
            joinRequestedCallback ??= Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
        }

        public void CreateFriendsLobby(string sessionName, string version)
        {
            LeaveLobby();
            hosting = true;
            StatusText = "Creating a friends-only Steam lobby.";
            createResult ??= CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
            createResult.Set(call);
            pendingSessionName = sessionName;
            pendingVersion = version;
        }

        public void JoinLobby(ulong lobby)
        {
            LeaveLobby();
            hosting = false;
            StatusText = "Joining the Steam lobby.";
            enterResult ??= CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            var call = SteamMatchmaking.JoinLobby(new CSteamID(lobby));
            enterResult.Set(call);
        }

        public void OpenInviteDialog()
        {
            if (!HasLobby)
            {
                StatusText = "Host a lobby before inviting friends.";
                return;
            }

            SteamFriends.ActivateGameOverlayInviteDialog(lobbyId);
        }

        public bool IsLobbyMember(ulong steamId)
        {
            if (!HasLobby)
            {
                return false;
            }

            var count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
            for (var i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i).m_SteamID == steamId)
                {
                    return true;
                }
            }

            return false;
        }

        public void LeaveLobby()
        {
            if (HasLobby)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(lobbyId);
                }
                catch (Exception ex)
                {
                    log("Going Cooperative Steam lobby leave failed error=" + ex.GetType().Name + ":" + ex.Message);
                }
            }

            lobbyId = CSteamID.Nil;
            hosting = false;
            StatusText = "No Steam lobby.";
        }

        public void Dispose()
        {
            LeaveLobby();
            joinRequestedCallback?.Dispose();
            joinRequestedCallback = null;
            createResult?.Dispose();
            createResult = null;
            enterResult?.Dispose();
            enterResult = null;
        }

        private string pendingSessionName = string.Empty;
        private string pendingVersion = string.Empty;

        private void OnLobbyCreated(LobbyCreated_t data, bool ioFailure)
        {
            if (ioFailure || data.m_eResult != EResult.k_EResultOK)
            {
                hosting = false;
                StatusText = "Steam lobby creation failed (" + data.m_eResult + ").";
                log("Going Cooperative Steam lobby create failed ioFailure=" + ioFailure + " result=" + data.m_eResult);
                return;
            }

            lobbyId = new CSteamID(data.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(lobbyId, LobbyDataVersionKey, pendingVersion);
            SteamMatchmaking.SetLobbyData(lobbyId, LobbyDataSessionKey, pendingSessionName);
            StatusText = "Steam lobby ready. Invite a friend from the overlay.";
            log("Going Cooperative Steam lobby created id=" + data.m_ulSteamIDLobby.ToString(CultureInfo.InvariantCulture));
        }

        private void OnLobbyEntered(LobbyEnter_t data, bool ioFailure)
        {
            if (ioFailure || data.m_EChatRoomEnterResponse != 1)
            {
                StatusText = "Could not enter the Steam lobby (response " + data.m_EChatRoomEnterResponse + ").";
                log("Going Cooperative Steam lobby enter failed ioFailure=" + ioFailure + " response=" + data.m_EChatRoomEnterResponse);
                return;
            }

            lobbyId = new CSteamID(data.m_ulSteamIDLobby);
            var owner = SteamMatchmaking.GetLobbyOwner(lobbyId);
            StatusText = "Steam lobby joined. Host " + owner.m_SteamID.ToString(CultureInfo.InvariantCulture) + ".";
            log("Going Cooperative Steam lobby entered id=" + data.m_ulSteamIDLobby.ToString(CultureInfo.InvariantCulture)
                + " owner=" + owner.m_SteamID.ToString(CultureInfo.InvariantCulture));
            HostResolved?.Invoke(owner.m_SteamID);
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            log("Going Cooperative Steam invite accepted lobby=" + data.m_steamIDLobby.m_SteamID.ToString(CultureInfo.InvariantCulture)
                + " friend=" + data.m_steamIDFriend.m_SteamID.ToString(CultureInfo.InvariantCulture));
            InviteAccepted?.Invoke(data.m_steamIDLobby.m_SteamID);
        }
    }
}
