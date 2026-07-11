using System;

namespace GoingCooperative.Core
{
    public sealed class LockstepCommand : IComparable<LockstepCommand>
    {
        public LockstepCommand(
            string playerId,
            long sequence,
            long targetTick,
            CommandKind kind,
            string payloadJson,
            string? targetStableId = null,
            int? mapX = null,
            int? mapY = null,
            int? mapZ = null)
        {
            PlayerId = string.IsNullOrWhiteSpace(playerId) ? "unknown" : playerId;
            Sequence = sequence;
            TargetTick = targetTick;
            Kind = kind;
            PayloadJson = payloadJson ?? string.Empty;
            TargetStableId = targetStableId;
            MapX = mapX;
            MapY = mapY;
            MapZ = mapZ;
            PayloadHash = DeterminismHash.HashString(PayloadJson);
        }

        public string PlayerId { get; }
        public long Sequence { get; }
        public long TargetTick { get; }
        public CommandKind Kind { get; }
        public string PayloadJson { get; }
        public string? TargetStableId { get; }
        public int? MapX { get; }
        public int? MapY { get; }
        public int? MapZ { get; }
        public ulong PayloadHash { get; }

        public int CompareTo(LockstepCommand? other)
        {
            if (other == null)
            {
                return 1;
            }

            var tickCompare = TargetTick.CompareTo(other.TargetTick);
            if (tickCompare != 0)
            {
                return tickCompare;
            }

            var sequenceCompare = Sequence.CompareTo(other.Sequence);
            if (sequenceCompare != 0)
            {
                return sequenceCompare;
            }

            return string.Compare(PlayerId, other.PlayerId, StringComparison.Ordinal);
        }

        public ulong StableHash()
        {
            var hash = new DeterminismHash();
            hash.Add(PlayerId);
            hash.Add(Sequence);
            hash.Add(TargetTick);
            hash.Add((int)Kind);
            hash.Add(TargetStableId ?? string.Empty);
            hash.Add(MapX ?? int.MinValue);
            hash.Add(MapY ?? int.MinValue);
            hash.Add(MapZ ?? int.MinValue);
            hash.Add(PayloadHash);
            return hash.Value;
        }

        public override string ToString()
        {
            return $"{TargetTick}:{Sequence}:{PlayerId}:{Kind}:0x{PayloadHash:x16}";
        }
    }
}

