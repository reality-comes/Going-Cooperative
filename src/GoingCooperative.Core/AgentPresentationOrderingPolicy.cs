using System;
using GoingCooperative.Core.Replication;

namespace GoingCooperative.Core
{
    public enum AgentPresentationEventRole
    {
        Start = 0,
        Update = 1,
        Commit = 2,
        End = 3,
        Instant = 4
    }

    public enum AgentPresentationActivityStatus
    {
        Empty = 0,
        Active = 1,
        Completed = 2
    }

    public enum AgentPresentationOrderingDisposition
    {
        Apply = 0,
        BufferUntilStart = 1,
        IgnoreDuplicate = 2,
        IgnoreStale = 3,
        IgnoreCompleted = 4,
        RejectEntity = 5
    }

    public readonly struct AgentPresentationOrderingState
    {
        internal AgentPresentationOrderingState(
            string? entityId,
            long epoch,
            long activityId,
            long revision,
            long latestEventTick,
            AgentPresentationActivityStatus status)
        {
            EntityId = entityId;
            Epoch = epoch;
            ActivityId = activityId;
            Revision = revision;
            LatestEventTick = latestEventTick;
            Status = status;
        }

        public string? EntityId { get; }
        public long Epoch { get; }
        public long ActivityId { get; }
        public long Revision { get; }
        public long LatestEventTick { get; }
        public AgentPresentationActivityStatus Status { get; }

        public static AgentPresentationOrderingState Empty(string? entityId = null)
        {
            if (entityId != null && string.IsNullOrWhiteSpace(entityId))
            {
                throw new ArgumentException("An ordering state identity cannot be blank.", nameof(entityId));
            }

            return new AgentPresentationOrderingState(
                entityId,
                -1,
                -1,
                -1,
                -1,
                AgentPresentationActivityStatus.Empty);
        }
    }

    public readonly struct AgentPresentationOrderingDecision
    {
        internal AgentPresentationOrderingDecision(
            AgentPresentationOrderingDisposition disposition,
            AgentPresentationOrderingState nextState,
            bool resetEpoch,
            bool supersedeActive,
            bool completesActivity)
        {
            Disposition = disposition;
            NextState = nextState;
            ResetEpoch = resetEpoch;
            SupersedeActive = supersedeActive;
            CompletesActivity = completesActivity;
        }

        public AgentPresentationOrderingDisposition Disposition { get; }
        public AgentPresentationOrderingState NextState { get; }
        public bool ResetEpoch { get; }
        public bool SupersedeActive { get; }
        public bool CompletesActivity { get; }
    }

    public static class AgentPresentationOrderingPolicy
    {
        public static AgentPresentationEventRole GetRole(ReplicationAgentMotionPhase phase)
        {
            switch (phase)
            {
                case ReplicationAgentMotionPhase.Begin:
                    return AgentPresentationEventRole.Start;
                case ReplicationAgentMotionPhase.PathChanged:
                    return AgentPresentationEventRole.Update;
                case ReplicationAgentMotionPhase.End:
                    return AgentPresentationEventRole.End;
                case ReplicationAgentMotionPhase.Teleport:
                    return AgentPresentationEventRole.Instant;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase));
            }
        }

        public static AgentPresentationEventRole GetRole(ReplicationAgentWorkPhase phase)
        {
            switch (phase)
            {
                case ReplicationAgentWorkPhase.Start:
                    return AgentPresentationEventRole.Start;
                case ReplicationAgentWorkPhase.Anchor:
                    return AgentPresentationEventRole.Update;
                case ReplicationAgentWorkPhase.Commit:
                    return AgentPresentationEventRole.Commit;
                case ReplicationAgentWorkPhase.End:
                    return AgentPresentationEventRole.End;
                default:
                    throw new ArgumentOutOfRangeException(nameof(phase));
            }
        }

        public static AgentPresentationOrderingDecision Evaluate(
            AgentPresentationOrderingState state,
            ReplicationAgentPresentationStamp incoming,
            AgentPresentationEventRole role)
        {
            if (!Enum.IsDefined(typeof(AgentPresentationEventRole), role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            if (!string.IsNullOrEmpty(state.EntityId)
                && !string.Equals(state.EntityId, incoming.EntityId, StringComparison.Ordinal))
            {
                return Keep(state, AgentPresentationOrderingDisposition.RejectEntity);
            }

            if (state.Status == AgentPresentationActivityStatus.Empty)
            {
                return IsOpeningRole(role)
                    ? Apply(state, incoming, role, false, false)
                    : Keep(state, AgentPresentationOrderingDisposition.BufferUntilStart);
            }

            if (incoming.Epoch < state.Epoch)
            {
                return Keep(state, AgentPresentationOrderingDisposition.IgnoreStale);
            }

            if (incoming.Epoch > state.Epoch)
            {
                return IsOpeningRole(role)
                    ? Apply(
                        state,
                        incoming,
                        role,
                        true,
                        state.Status == AgentPresentationActivityStatus.Active)
                    : Keep(state, AgentPresentationOrderingDisposition.BufferUntilStart);
            }

            if (incoming.ActivityId < state.ActivityId)
            {
                return Keep(state, AgentPresentationOrderingDisposition.IgnoreStale);
            }

            if (incoming.ActivityId > state.ActivityId)
            {
                return IsOpeningRole(role)
                    ? Apply(
                        state,
                        incoming,
                        role,
                        false,
                        state.Status == AgentPresentationActivityStatus.Active)
                    : Keep(state, AgentPresentationOrderingDisposition.BufferUntilStart);
            }

            var versionComparison = CompareVersion(
                incoming.Revision,
                incoming.EventTick,
                state.Revision,
                state.LatestEventTick);
            if (versionComparison < 0)
            {
                return Keep(state, AgentPresentationOrderingDisposition.IgnoreStale);
            }

            if (versionComparison == 0)
            {
                return Keep(state, AgentPresentationOrderingDisposition.IgnoreDuplicate);
            }

            if (state.Status == AgentPresentationActivityStatus.Completed)
            {
                return Keep(state, AgentPresentationOrderingDisposition.IgnoreCompleted);
            }

            return Apply(
                state,
                incoming,
                role,
                false,
                role == AgentPresentationEventRole.Instant);
        }

        private static bool IsOpeningRole(AgentPresentationEventRole role)
        {
            return role == AgentPresentationEventRole.Start || role == AgentPresentationEventRole.Instant;
        }

        private static int CompareVersion(long revision, long eventTick, long otherRevision, long otherEventTick)
        {
            if (revision != otherRevision)
            {
                return revision < otherRevision ? -1 : 1;
            }

            if (eventTick == otherEventTick)
            {
                return 0;
            }

            return eventTick < otherEventTick ? -1 : 1;
        }

        private static AgentPresentationOrderingDecision Apply(
            AgentPresentationOrderingState state,
            ReplicationAgentPresentationStamp incoming,
            AgentPresentationEventRole role,
            bool resetEpoch,
            bool supersedeActive)
        {
            var completesActivity = role == AgentPresentationEventRole.End
                || role == AgentPresentationEventRole.Instant;
            var nextState = new AgentPresentationOrderingState(
                incoming.EntityId,
                incoming.Epoch,
                incoming.ActivityId,
                incoming.Revision,
                incoming.EventTick,
                completesActivity
                    ? AgentPresentationActivityStatus.Completed
                    : AgentPresentationActivityStatus.Active);
            return new AgentPresentationOrderingDecision(
                AgentPresentationOrderingDisposition.Apply,
                nextState,
                resetEpoch,
                supersedeActive,
                completesActivity);
        }

        private static AgentPresentationOrderingDecision Keep(
            AgentPresentationOrderingState state,
            AgentPresentationOrderingDisposition disposition)
        {
            return new AgentPresentationOrderingDecision(disposition, state, false, false, false);
        }
    }
}
