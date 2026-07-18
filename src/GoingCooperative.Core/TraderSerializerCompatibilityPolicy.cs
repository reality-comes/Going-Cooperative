namespace GoingCooperative.Core
{
    public enum TraderSerializerCompatibilityResult
    {
        NotRequired,
        Compatible,
        LocalIdentityMissing,
        RemoteIdentityMissing,
        AssemblyMismatch
    }

    /// <summary>
    /// Guards native FV serializer payloads from crossing game-assembly versions.
    /// Other replication lanes intentionally retain their existing compatibility
    /// behavior when trader serialization is not active.
    /// </summary>
    public static class TraderSerializerCompatibilityPolicy
    {
        public static TraderSerializerCompatibilityResult Evaluate(
            bool required,
            string localAssemblyIdentity,
            string remoteAssemblyIdentity)
        {
            if (!required) return TraderSerializerCompatibilityResult.NotRequired;
            if (string.IsNullOrWhiteSpace(localAssemblyIdentity))
                return TraderSerializerCompatibilityResult.LocalIdentityMissing;
            if (string.IsNullOrWhiteSpace(remoteAssemblyIdentity))
                return TraderSerializerCompatibilityResult.RemoteIdentityMissing;
            return string.Equals(localAssemblyIdentity, remoteAssemblyIdentity, System.StringComparison.Ordinal)
                ? TraderSerializerCompatibilityResult.Compatible
                : TraderSerializerCompatibilityResult.AssemblyMismatch;
        }
    }
}
