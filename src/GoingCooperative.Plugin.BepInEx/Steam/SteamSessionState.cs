namespace GoingCooperative.Plugin.BepInEx.Steam
{
    /// <summary>
    /// Holds the live Steam session objects. Kept out of the plugin class so
    /// that GoingCooperativePlugin never declares fields of Steamworks-backed
    /// types; this type only loads when Steam code paths actually run.
    /// </summary>
    internal static class SteamSessionState
    {
        public static SteamTunnel? Tunnel;
        public static SteamLobbySession? Lobby;
        public static ulong PendingHostId;
    }
}
