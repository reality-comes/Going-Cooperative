using System;
using System.Collections.Generic;

namespace GoingCooperative.Core.Replication
{
    public enum ReplicationAgentLocomotionGait
    {
        Unknown = 0,
        Idle = 1,
        Walk = 2,
        Run = 3,
        Sprint = 4,
        Fly = 5,
        Swim = 6,
        Climb = 7
    }

    public enum ReplicationAgentMotionPhase
    {
        Begin = 0,
        PathChanged = 1,
        End = 2,
        Teleport = 3
    }

    public enum ReplicationAgentWorkPhase
    {
        Start = 0,
        Anchor = 1,
        Commit = 2,
        End = 3
    }

    public enum ReplicationAgentPresentationEndReason
    {
        None = 0,
        Arrived = 1,
        Completed = 2,
        Cancelled = 3,
        Replaced = 4,
        Failed = 5,
        Watchdog = 6
    }

    public readonly struct ReplicationAgentPresentationStamp
    {
        public ReplicationAgentPresentationStamp(
            long epoch,
            string entityId,
            long activityId,
            long revision,
            long eventTick)
        {
            if (epoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch));
            }

            if (string.IsNullOrWhiteSpace(entityId))
            {
                throw new ArgumentException("An agent presentation event requires an entity identity.", nameof(entityId));
            }

            if (activityId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(activityId));
            }

            if (revision <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revision));
            }

            if (eventTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(eventTick));
            }

            Epoch = epoch;
            EntityId = entityId;
            ActivityId = activityId;
            Revision = revision;
            EventTick = eventTick;
        }

        public long Epoch { get; }
        public string EntityId { get; }
        public long ActivityId { get; }
        public long Revision { get; }
        public long EventTick { get; }
    }

    public readonly struct ReplicationAgentPresentationVector3
    {
        public ReplicationAgentPresentationVector3(float x, float y, float z)
        {
            ReplicationAgentPresentationValidation.RequireFinite(x, nameof(x));
            ReplicationAgentPresentationValidation.RequireFinite(y, nameof(y));
            ReplicationAgentPresentationValidation.RequireFinite(z, nameof(z));
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
    }

    public readonly struct ReplicationAgentPresentationQuaternion
    {
        public ReplicationAgentPresentationQuaternion(float x, float y, float z, float w)
        {
            ReplicationAgentPresentationValidation.RequireFinite(x, nameof(x));
            ReplicationAgentPresentationValidation.RequireFinite(y, nameof(y));
            ReplicationAgentPresentationValidation.RequireFinite(z, nameof(z));
            ReplicationAgentPresentationValidation.RequireFinite(w, nameof(w));

            var magnitudeSquared = (x * x) + (y * y) + (z * z) + (w * w);
            if (magnitudeSquared <= 0.000001f)
            {
                throw new ArgumentException("An agent presentation rotation cannot be a zero quaternion.");
            }

            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float W { get; }
    }

    public readonly struct ReplicationAgentPresentationGridPoint
    {
        public ReplicationAgentPresentationGridPoint(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
    }

    public sealed class ReplicationAgentMotionPresentation
    {
        public const int MaximumPathPoints = 4;

        private static readonly IReadOnlyList<ReplicationAgentPresentationVector3> EmptyPath =
            Array.AsReadOnly(new ReplicationAgentPresentationVector3[0]);

        private ReplicationAgentMotionPresentation(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentMotionPhase phase,
            ReplicationAgentPresentationVector3 position,
            ReplicationAgentPresentationQuaternion rotation,
            float desiredSpeed,
            ReplicationAgentLocomotionGait gait,
            IReadOnlyList<ReplicationAgentPresentationVector3> path,
            string targetEntityId,
            ReplicationAgentPresentationGridPoint? targetGrid,
            ReplicationAgentPresentationEndReason endReason)
        {
            Stamp = stamp;
            Phase = phase;
            Position = position;
            Rotation = rotation;
            DesiredSpeed = desiredSpeed;
            Gait = gait;
            Path = path;
            TargetEntityId = targetEntityId;
            TargetGrid = targetGrid;
            EndReason = endReason;
        }

        public ReplicationAgentPresentationStamp Stamp { get; }
        public ReplicationAgentMotionPhase Phase { get; }
        public ReplicationAgentPresentationVector3 Position { get; }
        public ReplicationAgentPresentationQuaternion Rotation { get; }
        public float DesiredSpeed { get; }
        public ReplicationAgentLocomotionGait Gait { get; }
        public IReadOnlyList<ReplicationAgentPresentationVector3> Path { get; }
        public string TargetEntityId { get; }
        public ReplicationAgentPresentationGridPoint? TargetGrid { get; }
        public ReplicationAgentPresentationEndReason EndReason { get; }

        public static ReplicationAgentMotionPresentation Begin(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentPresentationVector3 position,
            ReplicationAgentPresentationQuaternion rotation,
            float desiredSpeed,
            ReplicationAgentLocomotionGait gait,
            IReadOnlyList<ReplicationAgentPresentationVector3> path,
            string? targetEntityId = null,
            ReplicationAgentPresentationGridPoint? targetGrid = null)
        {
            return CreatePathEvent(
                stamp,
                ReplicationAgentMotionPhase.Begin,
                position,
                rotation,
                desiredSpeed,
                gait,
                path,
                targetEntityId,
                targetGrid);
        }

        public static ReplicationAgentMotionPresentation PathChanged(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentPresentationVector3 position,
            ReplicationAgentPresentationQuaternion rotation,
            float desiredSpeed,
            ReplicationAgentLocomotionGait gait,
            IReadOnlyList<ReplicationAgentPresentationVector3> path,
            string? targetEntityId = null,
            ReplicationAgentPresentationGridPoint? targetGrid = null)
        {
            return CreatePathEvent(
                stamp,
                ReplicationAgentMotionPhase.PathChanged,
                position,
                rotation,
                desiredSpeed,
                gait,
                path,
                targetEntityId,
                targetGrid);
        }

        public static ReplicationAgentMotionPresentation End(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentPresentationVector3 position,
            ReplicationAgentPresentationQuaternion rotation,
            ReplicationAgentPresentationEndReason reason)
        {
            ReplicationAgentPresentationValidation.RequireEndReason(reason, nameof(reason));
            return new ReplicationAgentMotionPresentation(
                stamp,
                ReplicationAgentMotionPhase.End,
                position,
                rotation,
                0,
                ReplicationAgentLocomotionGait.Idle,
                EmptyPath,
                string.Empty,
                null,
                reason);
        }

        public static ReplicationAgentMotionPresentation Teleport(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentPresentationVector3 position,
            ReplicationAgentPresentationQuaternion rotation)
        {
            return new ReplicationAgentMotionPresentation(
                stamp,
                ReplicationAgentMotionPhase.Teleport,
                position,
                rotation,
                0,
                ReplicationAgentLocomotionGait.Idle,
                EmptyPath,
                string.Empty,
                null,
                ReplicationAgentPresentationEndReason.None);
        }

        private static ReplicationAgentMotionPresentation CreatePathEvent(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentMotionPhase phase,
            ReplicationAgentPresentationVector3 position,
            ReplicationAgentPresentationQuaternion rotation,
            float desiredSpeed,
            ReplicationAgentLocomotionGait gait,
            IReadOnlyList<ReplicationAgentPresentationVector3> path,
            string? targetEntityId,
            ReplicationAgentPresentationGridPoint? targetGrid)
        {
            ReplicationAgentPresentationValidation.RequireFinite(desiredSpeed, nameof(desiredSpeed));
            if (desiredSpeed <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(desiredSpeed), "A moving activity requires a positive desired speed.");
            }

            if (!ReplicationAgentPresentationValidation.IsMovingGait(gait))
            {
                throw new ArgumentOutOfRangeException(nameof(gait), "A moving activity requires an explicit moving gait.");
            }

            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (path.Count == 0 || path.Count > MaximumPathPoints)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(path),
                    "A motion path must contain between one and " + MaximumPathPoints + " look-ahead points.");
            }

            var pathCopy = new ReplicationAgentPresentationVector3[path.Count];
            for (var i = 0; i < path.Count; i++)
            {
                pathCopy[i] = path[i];
            }

            return new ReplicationAgentMotionPresentation(
                stamp,
                phase,
                position,
                rotation,
                desiredSpeed,
                gait,
                Array.AsReadOnly(pathCopy),
                targetEntityId ?? string.Empty,
                targetGrid,
                ReplicationAgentPresentationEndReason.None);
        }
    }

    public sealed class ReplicationAgentWorkPresentation
    {
        private ReplicationAgentWorkPresentation(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentWorkPhase phase,
            string workKind,
            string targetEntityId,
            ReplicationAgentPresentationGridPoint? targetGrid,
            ReplicationAgentPresentationVector3 workPosition,
            ReplicationAgentPresentationQuaternion facing,
            double durationSeconds,
            double normalizedProgress,
            double remainingSeconds,
            string animationToken,
            string toolToken,
            string commitId,
            ReplicationAgentPresentationEndReason endReason)
        {
            Stamp = stamp;
            Phase = phase;
            WorkKind = workKind;
            TargetEntityId = targetEntityId;
            TargetGrid = targetGrid;
            WorkPosition = workPosition;
            Facing = facing;
            DurationSeconds = durationSeconds;
            NormalizedProgress = normalizedProgress;
            RemainingSeconds = remainingSeconds;
            AnimationToken = animationToken;
            ToolToken = toolToken;
            CommitId = commitId;
            EndReason = endReason;
        }

        public ReplicationAgentPresentationStamp Stamp { get; }
        public ReplicationAgentWorkPhase Phase { get; }
        public string WorkKind { get; }
        public string TargetEntityId { get; }
        public ReplicationAgentPresentationGridPoint? TargetGrid { get; }
        public ReplicationAgentPresentationVector3 WorkPosition { get; }
        public ReplicationAgentPresentationQuaternion Facing { get; }
        public double DurationSeconds { get; }
        public double NormalizedProgress { get; }
        public double RemainingSeconds { get; }
        public string AnimationToken { get; }
        public string ToolToken { get; }
        public string CommitId { get; }
        public ReplicationAgentPresentationEndReason EndReason { get; }

        public static ReplicationAgentWorkPresentation Start(
            ReplicationAgentPresentationStamp stamp,
            string workKind,
            string? targetEntityId,
            ReplicationAgentPresentationGridPoint? targetGrid,
            ReplicationAgentPresentationVector3 workPosition,
            ReplicationAgentPresentationQuaternion facing,
            double durationSeconds,
            string animationToken,
            string? toolToken = null)
        {
            if (string.IsNullOrWhiteSpace(workKind))
            {
                throw new ArgumentException("A work activity requires a semantic work kind.", nameof(workKind));
            }

            if (string.IsNullOrWhiteSpace(targetEntityId) && !targetGrid.HasValue)
            {
                throw new ArgumentException("A work activity requires a stable target entity or target grid.", nameof(targetEntityId));
            }

            ReplicationAgentPresentationValidation.RequireFinite(durationSeconds, nameof(durationSeconds));
            if (durationSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            }

            if (string.IsNullOrWhiteSpace(animationToken))
            {
                throw new ArgumentException("A work activity requires an explicit animation token.", nameof(animationToken));
            }

            return new ReplicationAgentWorkPresentation(
                stamp,
                ReplicationAgentWorkPhase.Start,
                workKind,
                targetEntityId ?? string.Empty,
                targetGrid,
                workPosition,
                facing,
                durationSeconds,
                0,
                durationSeconds,
                animationToken,
                toolToken ?? string.Empty,
                string.Empty,
                ReplicationAgentPresentationEndReason.None);
        }

        public static ReplicationAgentWorkPresentation Anchor(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentPresentationVector3 workPosition,
            ReplicationAgentPresentationQuaternion facing,
            double normalizedProgress,
            double remainingSeconds)
        {
            ReplicationAgentPresentationValidation.RequireNormalized(normalizedProgress, nameof(normalizedProgress));
            ReplicationAgentPresentationValidation.RequireFinite(remainingSeconds, nameof(remainingSeconds));
            if (remainingSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(remainingSeconds));
            }

            return new ReplicationAgentWorkPresentation(
                stamp,
                ReplicationAgentWorkPhase.Anchor,
                string.Empty,
                string.Empty,
                null,
                workPosition,
                facing,
                0,
                normalizedProgress,
                remainingSeconds,
                string.Empty,
                string.Empty,
                string.Empty,
                ReplicationAgentPresentationEndReason.None);
        }

        public static ReplicationAgentWorkPresentation Commit(
            ReplicationAgentPresentationStamp stamp,
            string commitId,
            ReplicationAgentPresentationVector3 workPosition,
            ReplicationAgentPresentationQuaternion facing,
            double normalizedProgress)
        {
            if (string.IsNullOrWhiteSpace(commitId))
            {
                throw new ArgumentException("A work commit requires an identity for idempotent correlation.", nameof(commitId));
            }

            ReplicationAgentPresentationValidation.RequireNormalized(normalizedProgress, nameof(normalizedProgress));
            return new ReplicationAgentWorkPresentation(
                stamp,
                ReplicationAgentWorkPhase.Commit,
                string.Empty,
                string.Empty,
                null,
                workPosition,
                facing,
                0,
                normalizedProgress,
                0,
                string.Empty,
                string.Empty,
                commitId,
                ReplicationAgentPresentationEndReason.None);
        }

        public static ReplicationAgentWorkPresentation End(
            ReplicationAgentPresentationStamp stamp,
            ReplicationAgentPresentationVector3 workPosition,
            ReplicationAgentPresentationQuaternion facing,
            double normalizedProgress,
            ReplicationAgentPresentationEndReason reason)
        {
            ReplicationAgentPresentationValidation.RequireNormalized(normalizedProgress, nameof(normalizedProgress));
            ReplicationAgentPresentationValidation.RequireEndReason(reason, nameof(reason));
            return new ReplicationAgentWorkPresentation(
                stamp,
                ReplicationAgentWorkPhase.End,
                string.Empty,
                string.Empty,
                null,
                workPosition,
                facing,
                0,
                normalizedProgress,
                0,
                string.Empty,
                string.Empty,
                string.Empty,
                reason);
        }
    }

    internal static class ReplicationAgentPresentationValidation
    {
        public static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, "Agent presentation values must be finite.");
            }
        }

        public static void RequireFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, "Agent presentation values must be finite.");
            }
        }

        public static void RequireNormalized(double value, string parameterName)
        {
            RequireFinite(value, parameterName);
            if (value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(parameterName, "Normalized progress must be between zero and one.");
            }
        }

        public static void RequireEndReason(ReplicationAgentPresentationEndReason reason, string parameterName)
        {
            if (!Enum.IsDefined(typeof(ReplicationAgentPresentationEndReason), reason)
                || reason == ReplicationAgentPresentationEndReason.None)
            {
                throw new ArgumentOutOfRangeException(parameterName, "An end event requires a known non-empty reason.");
            }
        }

        public static bool IsMovingGait(ReplicationAgentLocomotionGait gait)
        {
            return gait == ReplicationAgentLocomotionGait.Walk
                || gait == ReplicationAgentLocomotionGait.Run
                || gait == ReplicationAgentLocomotionGait.Sprint
                || gait == ReplicationAgentLocomotionGait.Fly
                || gait == ReplicationAgentLocomotionGait.Swim
                || gait == ReplicationAgentLocomotionGait.Climb;
        }
    }
}
