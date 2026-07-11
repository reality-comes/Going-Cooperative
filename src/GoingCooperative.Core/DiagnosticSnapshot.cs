namespace GoingCooperative.Core
{
    public sealed class DiagnosticSnapshot
    {
        public DiagnosticSnapshot(
            long localTick,
            ulong localHash,
            ulong? remoteHash,
            int pendingCommands,
            string? lastAppliedCommand,
            string status)
        {
            LocalTick = localTick;
            LocalHash = localHash;
            RemoteHash = remoteHash;
            PendingCommands = pendingCommands;
            LastAppliedCommand = lastAppliedCommand;
            Status = status;
        }

        public long LocalTick { get; }
        public ulong LocalHash { get; }
        public ulong? RemoteHash { get; }
        public int PendingCommands { get; }
        public string? LastAppliedCommand { get; }
        public string Status { get; }

        public bool IsDesynced
        {
            get { return RemoteHash.HasValue && RemoteHash.Value != LocalHash; }
        }

        public override string ToString()
        {
            var remote = RemoteHash.HasValue ? DeterminismHash.Format(RemoteHash.Value) : "<none>";
            return $"tick={LocalTick} local={DeterminismHash.Format(LocalHash)} remote={remote} pending={PendingCommands} status={Status}";
        }
    }
}
