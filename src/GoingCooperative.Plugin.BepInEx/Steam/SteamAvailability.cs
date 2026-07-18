using System;
using System.Runtime.CompilerServices;
using Steamworks;

namespace GoingCooperative.Plugin.BepInEx.Steam
{
    /// <summary>
    /// Probes whether the Steam API is usable inside the current game process.
    /// The game itself initializes SteamAPI; this type never calls Init.
    /// All Steamworks type references are isolated behind non-inlined methods so
    /// a missing com.rlabrecque.steamworks.net.dll (GOG builds) surfaces as an
    /// availability failure instead of a plugin crash.
    /// </summary>
    internal static class SteamAvailability
    {
        private static bool probed;
        private static bool available;
        private static ulong localSteamId;
        private static string localPersonaName = string.Empty;
        private static string detail = "Steam has not been checked yet.";

        public static bool IsAvailable(out string statusDetail)
        {
            if (!probed)
            {
                probed = true;
                try
                {
                    Probe();
                }
                catch (Exception ex)
                {
                    available = false;
                    detail = "Steam is unavailable: " + ex.GetType().Name + ".";
                }
            }

            statusDetail = detail;
            return available;
        }

        public static ulong LocalSteamId
        {
            get
            {
                IsAvailable(out _);
                return localSteamId;
            }
        }

        public static string LocalPersonaName
        {
            get
            {
                IsAvailable(out _);
                return localPersonaName;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Probe()
        {
            if (!SteamAPI.IsSteamRunning())
            {
                available = false;
                detail = "Steam is not running.";
                return;
            }

            var id = SteamUser.GetSteamID();
            if (!id.IsValid())
            {
                available = false;
                detail = "The Steam user is not signed in.";
                return;
            }

            localSteamId = id.m_SteamID;
            localPersonaName = SteamFriends.GetPersonaName();
            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
            }
            catch (Exception ex)
            {
                // Relay warm-up is best effort; direct P2P may still work.
                detail = "Steam ready (relay warm-up failed: " + ex.GetType().Name + ").";
                available = true;
                return;
            }

            available = true;
            detail = "Steam ready as " + localPersonaName + " (" + localSteamId + ").";
        }
    }
}
