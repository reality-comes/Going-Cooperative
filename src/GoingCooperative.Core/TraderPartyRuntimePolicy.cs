namespace GoingCooperative.Core
{
    public enum TraderPartyOwnerClassification
    {
        None,
        SameEventTrader,
        GenericWorldOwner
    }

    public enum TraderPartyBootstrapDisposition
    {
        ReusePreparedBootstrap,
        SupersedeUnappliedBootstrap,
        RebuildForNewPeer,
        CurrentPeerSemanticOnly
    }

    public static class TraderPartyRuntimePolicy
    {
        public static bool IsDetached(TraderPartyOwnerClassification owner)
        {
            return owner != TraderPartyOwnerClassification.SameEventTrader;
        }

        public static TraderPartyBootstrapDisposition DecideBootstrapDisposition(
            bool bootstrapSuccessfullyApplied,
            bool membershipRemoved,
            bool bootstrapDirty,
            bool newPeerRefresh)
        {
            if (membershipRemoved && !bootstrapSuccessfullyApplied)
                return TraderPartyBootstrapDisposition.SupersedeUnappliedBootstrap;
            if (bootstrapSuccessfullyApplied && bootstrapDirty && newPeerRefresh)
                return TraderPartyBootstrapDisposition.RebuildForNewPeer;
            if (bootstrapSuccessfullyApplied)
                return TraderPartyBootstrapDisposition.CurrentPeerSemanticOnly;
            return TraderPartyBootstrapDisposition.ReusePreparedBootstrap;
        }
    }
}
