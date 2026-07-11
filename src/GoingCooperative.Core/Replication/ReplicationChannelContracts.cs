using System;

namespace GoingCooperative.Core.Replication
{
    public enum ReplicationChannelFrameKind
    {
        ReliableEvent = 1,
        SnapshotBegin = 2,
        SnapshotItem = 3,
        SnapshotEnd = 4,
        SnapshotAbort = 5
    }

    public sealed class ReplicationChannelFrameMetadata
    {
        public ReplicationChannelFrameMetadata(
            string channel,
            ReplicationChannelFrameKind kind,
            long sequence,
            long snapshotId,
            int index,
            int count,
            bool complete,
            ulong signature)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Kind = kind;
            Sequence = sequence;
            SnapshotId = snapshotId;
            Index = index;
            Count = count;
            Complete = complete;
            Signature = signature;
        }

        public string Channel { get; }

        public ReplicationChannelFrameKind Kind { get; }

        public long Sequence { get; }

        public long SnapshotId { get; }

        public int Index { get; }

        public int Count { get; }

        public bool Complete { get; }

        public ulong Signature { get; }
    }
}
