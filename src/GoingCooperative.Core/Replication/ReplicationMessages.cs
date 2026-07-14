using System;
using System.Collections.Generic;

namespace GoingCooperative.Core.Replication
{
    public sealed class ReplicationHello
    {
        public ReplicationHello(string peerId, string mode, string protocolVersion, string buildHash)
        {
            PeerId = peerId ?? throw new ArgumentNullException(nameof(peerId));
            Mode = mode ?? throw new ArgumentNullException(nameof(mode));
            ProtocolVersion = protocolVersion ?? throw new ArgumentNullException(nameof(protocolVersion));
            BuildHash = buildHash ?? throw new ArgumentNullException(nameof(buildHash));
        }

        public string PeerId { get; }
        public string Mode { get; }
        public string ProtocolVersion { get; }
        public string BuildHash { get; }
    }

    public sealed class ReplicationCommandIntent
    {
        public ReplicationCommandIntent(LockstepCommand command)
        {
            Command = command ?? throw new ArgumentNullException(nameof(command));
        }

        public LockstepCommand Command { get; }
    }

    public sealed class ReplicationCommandAck
    {
        public ReplicationCommandAck(string playerId, long sequence, bool accepted, bool duplicate, string detail)
        {
            PlayerId = playerId ?? throw new ArgumentNullException(nameof(playerId));
            Sequence = sequence;
            Accepted = accepted;
            Duplicate = duplicate;
            Detail = detail ?? string.Empty;
        }

        public string PlayerId { get; }
        public long Sequence { get; }
        public bool Accepted { get; }
        public bool Duplicate { get; }
        public string Detail { get; }
    }

    public sealed class ReplicationRegionOrderState
    {
        public ReplicationRegionOrderState(
            long sequence,
            float sentRealtime,
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            string areaType,
            string subType,
            string detail)
        {
            Sequence = sequence;
            SentRealtime = sentRealtime;
            OrderType = orderType ?? throw new ArgumentNullException(nameof(orderType));
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            EndX = endX;
            EndY = endY;
            EndZ = endZ;
            AllowType = allowType ?? string.Empty;
            AreaType = areaType ?? string.Empty;
            SubType = subType ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        public long Sequence { get; }
        public float SentRealtime { get; }
        public string OrderType { get; }
        public int StartX { get; }
        public int StartY { get; }
        public int StartZ { get; }
        public int EndX { get; }
        public int EndY { get; }
        public int EndZ { get; }
        public string AllowType { get; }
        public string AreaType { get; }
        public string SubType { get; }
        public string Detail { get; }
    }

    public sealed class ReplicationWorldObjectDelta
    {
        public ReplicationWorldObjectDelta(
            long sequence,
            float sentRealtime,
            string deltaKind,
            long uniqueId,
            string blueprintId,
            int gridX,
            int gridY,
            int gridZ,
            string detail)
        {
            Sequence = sequence;
            SentRealtime = sentRealtime;
            DeltaKind = deltaKind ?? throw new ArgumentNullException(nameof(deltaKind));
            UniqueId = uniqueId;
            BlueprintId = blueprintId ?? string.Empty;
            GridX = gridX;
            GridY = gridY;
            GridZ = gridZ;
            Detail = detail ?? string.Empty;
        }

        public long Sequence { get; }
        public float SentRealtime { get; }
        public string DeltaKind { get; }
        public long UniqueId { get; }
        public string BlueprintId { get; }
        public int GridX { get; }
        public int GridY { get; }
        public int GridZ { get; }
        public string Detail { get; }
    }

    public sealed class ReplicationWorldObjectDeltaAck
    {
        public ReplicationWorldObjectDeltaAck(long sequence, bool applied, bool duplicate, string detail)
        {
            Sequence = sequence;
            Applied = applied;
            Duplicate = duplicate;
            Detail = detail ?? string.Empty;
        }

        public long Sequence { get; }
        public bool Applied { get; }
        public bool Duplicate { get; }
        public string Detail { get; }
    }

    public sealed class ReplicationResyncControl
    {
        public ReplicationResyncControl(long sequence, float sentRealtime, string phase, string requestId, string saveId, string hash, long byteLength, string detail)
        {
            Sequence = sequence;
            SentRealtime = sentRealtime;
            Phase = phase ?? throw new ArgumentNullException(nameof(phase));
            RequestId = requestId ?? string.Empty;
            SaveId = saveId ?? string.Empty;
            Hash = hash ?? string.Empty;
            ByteLength = byteLength;
            Detail = detail ?? string.Empty;
        }

        public long Sequence { get; }
        public float SentRealtime { get; }
        public string Phase { get; }
        public string RequestId { get; }
        public string SaveId { get; }
        public string Hash { get; }
        public long ByteLength { get; }
        public string Detail { get; }
    }

    public readonly struct ReplicationEntityMotionMetadata
    {
        public const string WireVersion = "agent-presentation-v1";
        public const string WireToken = "m1";

        public ReplicationEntityMotionMetadata(
            float velocityX,
            float velocityY,
            float velocityZ,
            float movementSpeed,
            ReplicationAgentLocomotionGait gait,
            bool isMoving,
            bool isRunning,
            bool isSwimming,
            bool isClimbing,
            int climbDirection,
            float animatorSpeed,
            long pathRevision)
        {
            RequireFinite(velocityX, nameof(velocityX));
            RequireFinite(velocityY, nameof(velocityY));
            RequireFinite(velocityZ, nameof(velocityZ));
            RequireFinite(movementSpeed, nameof(movementSpeed));
            RequireFinite(animatorSpeed, nameof(animatorSpeed));
            if (movementSpeed < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(movementSpeed));
            }

            if (animatorSpeed < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(animatorSpeed));
            }

            if (!Enum.IsDefined(typeof(ReplicationAgentLocomotionGait), gait))
            {
                throw new ArgumentOutOfRangeException(nameof(gait));
            }

            if (pathRevision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pathRevision));
            }

            VelocityX = velocityX;
            VelocityY = velocityY;
            VelocityZ = velocityZ;
            MovementSpeed = movementSpeed;
            Gait = gait;
            IsMoving = isMoving;
            IsRunning = isRunning;
            IsSwimming = isSwimming;
            IsClimbing = isClimbing;
            ClimbDirection = climbDirection;
            AnimatorSpeed = animatorSpeed;
            PathRevision = pathRevision;
        }

        public float VelocityX { get; }
        public float VelocityY { get; }
        public float VelocityZ { get; }
        public float MovementSpeed { get; }
        public ReplicationAgentLocomotionGait Gait { get; }
        public bool IsMoving { get; }
        public bool IsRunning { get; }
        public bool IsSwimming { get; }
        public bool IsClimbing { get; }
        public int ClimbDirection { get; }
        public float AnimatorSpeed { get; }
        public long PathRevision { get; }

        private static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    public sealed class ReplicationEntityTransform
    {
        public ReplicationEntityTransform(
            string entityId,
            string kind,
            float positionX,
            float positionY,
            float positionZ,
            float rotationX,
            float rotationY,
            float rotationZ,
            float rotationW)
            : this(
                entityId,
                kind,
                positionX,
                positionY,
                positionZ,
                rotationX,
                rotationY,
                rotationZ,
                rotationW,
                null)
        {
        }

        public ReplicationEntityTransform(
            string entityId,
            string kind,
            float positionX,
            float positionY,
            float positionZ,
            float rotationX,
            float rotationY,
            float rotationZ,
            float rotationW,
            ReplicationEntityMotionMetadata? motion)
        {
            EntityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            RotationX = rotationX;
            RotationY = rotationY;
            RotationZ = rotationZ;
            RotationW = rotationW;
            Motion = motion;
        }

        public string EntityId { get; }
        public string Kind { get; }
        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public float RotationX { get; }
        public float RotationY { get; }
        public float RotationZ { get; }
        public float RotationW { get; }
        public ReplicationEntityMotionMetadata? Motion { get; }
    }

    public sealed class ReplicationTransformSnapshot
    {
        public ReplicationTransformSnapshot(long sequence, float sentRealtime, IReadOnlyList<ReplicationEntityTransform> entities)
        {
            Sequence = sequence;
            SentRealtime = sentRealtime;
            Entities = entities ?? throw new ArgumentNullException(nameof(entities));
        }

        public long Sequence { get; }
        public float SentRealtime { get; }
        public IReadOnlyList<ReplicationEntityTransform> Entities { get; }
    }

    public sealed class ReplicationResourceContainerEntry
    {
        public ReplicationResourceContainerEntry(string blueprintId, int amount)
        {
            BlueprintId = blueprintId ?? throw new ArgumentNullException(nameof(blueprintId));
            Amount = amount;
        }

        public string BlueprintId { get; }
        public int Amount { get; }
    }

    public sealed class ReplicationResourceContainerState
    {
        public ReplicationResourceContainerState(
            string containerId,
            string containerKind,
            string ownerId,
            long revision,
            int gridX,
            int gridY,
            int gridZ,
            IReadOnlyList<ReplicationResourceContainerEntry> entries)
        {
            ContainerId = containerId ?? throw new ArgumentNullException(nameof(containerId));
            ContainerKind = containerKind ?? throw new ArgumentNullException(nameof(containerKind));
            OwnerId = ownerId ?? string.Empty;
            Revision = revision;
            GridX = gridX;
            GridY = gridY;
            GridZ = gridZ;
            Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        }

        public string ContainerId { get; }
        public string ContainerKind { get; }
        public string OwnerId { get; }
        public long Revision { get; }
        public int GridX { get; }
        public int GridY { get; }
        public int GridZ { get; }
        public IReadOnlyList<ReplicationResourceContainerEntry> Entries { get; }
    }

    public sealed class ReplicationResourceContainerBatch
    {
        public ReplicationResourceContainerBatch(
            long sequence,
            float sentRealtime,
            bool checkpoint,
            IReadOnlyList<ReplicationResourceContainerState> containers)
        {
            Sequence = sequence;
            SentRealtime = sentRealtime;
            Checkpoint = checkpoint;
            Containers = containers ?? throw new ArgumentNullException(nameof(containers));
        }

        public long Sequence { get; }
        public float SentRealtime { get; }
        public bool Checkpoint { get; }
        public IReadOnlyList<ReplicationResourceContainerState> Containers { get; }
    }
}
