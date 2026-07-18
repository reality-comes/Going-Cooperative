using System;

namespace GoingCooperative.Plugin.BepInEx
{
    internal enum MultiplayerMenuPage
    {
        Home,
        Host,
        Join,
        Status,
        Settings
    }

    internal enum MultiplayerConnectionMode
    {
        Direct,
        Steam
    }

    internal sealed class MultiplayerMenuModel
    {
        public MultiplayerConnectionMode ConnectionMode { get; set; } = MultiplayerConnectionMode.Direct;

        public string SteamHostIdText { get; set; } = string.Empty;

        public bool IsOpen { get; set; }

        public bool IsInitialized { get; set; }

        public MultiplayerMenuPage Page { get; set; } = MultiplayerMenuPage.Home;

        public string HostAddress { get; set; } = "127.0.0.1";

        public string PortText { get; set; } = "47692";

        public string SessionName { get; set; } = Environment.UserName + "'s Settlement";

        public string StatusMessage { get; set; } = "Choose Host or Join to begin.";

        public bool ShowAdvanced { get; set; }

        public void Open(MultiplayerMenuPage page = MultiplayerMenuPage.Home)
        {
            Page = page;
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
        }
    }
}
