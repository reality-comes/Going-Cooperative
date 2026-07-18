namespace GoingCooperative.Core
{
    /// <summary>
    /// Decides when a multiplayer client may safely reject a native trader event
    /// start. Unsupported event families, save loading, and pre-handshake runtime
    /// remain native so this narrow lane cannot silently remove their effects.
    /// </summary>
    public static class TraderEventAuthorityPolicy
    {
        public static bool ShouldSuppressClientNativeTraderStart(
            bool traderEvent,
            bool hostMode,
            bool traderAuthorityEnabled,
            bool replicationEnabled,
            bool runtimeStarted,
            bool compatiblePeerConnected,
            bool nativeLoadInProgress,
            bool applyingReplicatedEvent)
        {
            return traderEvent
                && !hostMode
                && traderAuthorityEnabled
                && replicationEnabled
                && runtimeStarted
                && compatiblePeerConnected
                && !nativeLoadInProgress
                && !applyingReplicatedEvent;
        }
    }
}
