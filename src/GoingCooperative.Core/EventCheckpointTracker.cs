using System;
using System.Collections.Generic;

namespace GoingCooperative.Core
{
    public enum EventCheckpointRecordResult
    {
        Accepted,
        Duplicate,
        Stale,
        Superseded,
        Conflict,
        CapacityExceeded
    }

    public sealed class EventCheckpointCompletion
    {
        private readonly HashSet<string> eventIds;

        internal EventCheckpointCompletion(
            long snapshotId,
            long beginSequence,
            long endSequence,
            HashSet<string> eventIds)
        {
            SnapshotId = snapshotId;
            BeginSequence = beginSequence;
            EndSequence = endSequence;
            this.eventIds = new HashSet<string>(eventIds, StringComparer.Ordinal);
        }

        public long SnapshotId { get; }
        public long BeginSequence { get; }
        public long EndSequence { get; }
        public int EventCount => eventIds.Count;

        public bool ContainsEvent(string eventId)
        {
            return eventIds.Contains(eventId);
        }
    }

    /// <summary>
    /// Collects independently reliable Begin/State/End event-checkpoint records.
    /// Records may arrive in any delivery order, but each State's original transport
    /// sequence must prove that the sender emitted it strictly between Begin and End.
    /// A newer complete checkpoint supersedes every older incomplete checkpoint,
    /// while stale retries remain terminal no-ops.
    /// </summary>
    public sealed class EventCheckpointTracker
    {
        private readonly int maxPendingCheckpoints;
        private readonly int maxEventsPerCheckpoint;
        private readonly SortedDictionary<long, PendingCheckpoint> pending =
            new SortedDictionary<long, PendingCheckpoint>();
        private long completedCheckpointId = -1L;
        private long supersededCheckpointId = -1L;

        public EventCheckpointTracker(int maxPendingCheckpoints, int maxEventsPerCheckpoint)
        {
            if (maxPendingCheckpoints <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingCheckpoints));
            if (maxEventsPerCheckpoint <= 0) throw new ArgumentOutOfRangeException(nameof(maxEventsPerCheckpoint));
            this.maxPendingCheckpoints = maxPendingCheckpoints;
            this.maxEventsPerCheckpoint = maxEventsPerCheckpoint;
        }

        public long CompletedCheckpointId => completedCheckpointId;
        public int PendingCheckpointCount => pending.Count;

        public EventCheckpointRecordResult RecordBegin(long snapshotId, int expectedCount, long beginSequence)
        {
            if (snapshotId <= 0L
                || expectedCount < 0
                || expectedCount > maxEventsPerCheckpoint
                || beginSequence < 0L)
                return EventCheckpointRecordResult.Conflict;
            var result = GetOrCreate(snapshotId, out var checkpoint);
            if (result != EventCheckpointRecordResult.Accepted || checkpoint == null) return result;
            if (checkpoint.HasBegin)
            {
                return checkpoint.ExpectedCount == expectedCount && checkpoint.BeginSequence == beginSequence
                    ? EventCheckpointRecordResult.Duplicate
                    : EventCheckpointRecordResult.Conflict;
            }
            if ((checkpoint.HasEnd && checkpoint.EndCount != expectedCount)
                || (checkpoint.HasEnd && beginSequence >= checkpoint.EndSequence)
                || checkpoint.EventSequences.Count > expectedCount
                || checkpoint.HasStateAtOrBefore(beginSequence))
            {
                return EventCheckpointRecordResult.Conflict;
            }
            checkpoint.HasBegin = true;
            checkpoint.ExpectedCount = expectedCount;
            checkpoint.BeginSequence = beginSequence;
            return EventCheckpointRecordResult.Accepted;
        }

        public EventCheckpointRecordResult RecordState(long snapshotId, string eventId, long stateSequence)
        {
            if (snapshotId <= 0L || string.IsNullOrWhiteSpace(eventId) || stateSequence < 0L)
                return EventCheckpointRecordResult.Conflict;
            var result = GetOrCreate(snapshotId, out var checkpoint);
            if (result != EventCheckpointRecordResult.Accepted || checkpoint == null) return result;
            if (checkpoint.EventSequences.TryGetValue(eventId, out var existingSequence))
            {
                return existingSequence == stateSequence
                    ? EventCheckpointRecordResult.Duplicate
                    : EventCheckpointRecordResult.Conflict;
            }
            if (checkpoint.EventSequences.Count >= maxEventsPerCheckpoint)
                return EventCheckpointRecordResult.CapacityExceeded;
            if ((checkpoint.HasBegin && checkpoint.EventSequences.Count >= checkpoint.ExpectedCount)
                || (checkpoint.HasEnd && checkpoint.EventSequences.Count >= checkpoint.EndCount)
                || (checkpoint.HasBegin && stateSequence <= checkpoint.BeginSequence)
                || (checkpoint.HasEnd && stateSequence >= checkpoint.EndSequence))
            {
                return EventCheckpointRecordResult.Conflict;
            }
            checkpoint.EventSequences.Add(eventId, stateSequence);
            return EventCheckpointRecordResult.Accepted;
        }

        public EventCheckpointRecordResult RecordEnd(long snapshotId, int endCount, long endSequence)
        {
            if (snapshotId <= 0L || endCount < 0 || endCount > maxEventsPerCheckpoint || endSequence < 0L)
                return EventCheckpointRecordResult.Conflict;
            var result = GetOrCreate(snapshotId, out var checkpoint);
            if (result != EventCheckpointRecordResult.Accepted || checkpoint == null) return result;
            if (checkpoint.HasEnd)
            {
                return checkpoint.EndCount == endCount && checkpoint.EndSequence == endSequence
                    ? EventCheckpointRecordResult.Duplicate
                    : EventCheckpointRecordResult.Conflict;
            }
            if ((checkpoint.HasBegin && checkpoint.ExpectedCount != endCount)
                || (checkpoint.HasBegin && checkpoint.BeginSequence >= endSequence)
                || checkpoint.EventSequences.Count > endCount
                || checkpoint.HasStateAtOrAfter(endSequence))
            {
                return EventCheckpointRecordResult.Conflict;
            }
            checkpoint.HasEnd = true;
            checkpoint.EndCount = endCount;
            checkpoint.EndSequence = endSequence;
            return EventCheckpointRecordResult.Accepted;
        }

        public bool TryTakeNewestComplete(out EventCheckpointCompletion? completion)
        {
            PendingCheckpoint? newest = null;
            foreach (var checkpoint in pending.Values)
            {
                if (checkpoint.HasBegin
                    && checkpoint.HasEnd
                    && checkpoint.ExpectedCount == checkpoint.EndCount
                    && checkpoint.EventSequences.Count == checkpoint.ExpectedCount)
                {
                    newest = checkpoint;
                }
            }
            if (newest == null)
            {
                completion = null;
                return false;
            }

            completion = new EventCheckpointCompletion(
                newest.SnapshotId,
                newest.BeginSequence,
                newest.EndSequence,
                new HashSet<string>(newest.EventSequences.Keys, StringComparer.Ordinal));
            completedCheckpointId = Math.Max(completedCheckpointId, newest.SnapshotId);
            RemoveThrough(newest.SnapshotId);
            return true;
        }

        public void AbandonThrough(long snapshotId)
        {
            if (snapshotId < 0L) return;
            supersededCheckpointId = Math.Max(supersededCheckpointId, snapshotId);
            RemoveThrough(snapshotId);
        }

        public void Reset()
        {
            pending.Clear();
            completedCheckpointId = -1L;
            supersededCheckpointId = -1L;
        }

        private EventCheckpointRecordResult GetOrCreate(long snapshotId, out PendingCheckpoint? checkpoint)
        {
            if (snapshotId <= completedCheckpointId)
            {
                checkpoint = null;
                return EventCheckpointRecordResult.Stale;
            }
            if (snapshotId <= supersededCheckpointId)
            {
                checkpoint = null;
                return EventCheckpointRecordResult.Superseded;
            }
            if (pending.TryGetValue(snapshotId, out checkpoint)) return EventCheckpointRecordResult.Accepted;

            if (pending.Count >= maxPendingCheckpoints)
            {
                var oldestId = FirstPendingId();
                if (snapshotId < oldestId)
                {
                    supersededCheckpointId = Math.Max(supersededCheckpointId, snapshotId);
                    checkpoint = null;
                    return EventCheckpointRecordResult.Superseded;
                }
                supersededCheckpointId = Math.Max(supersededCheckpointId, oldestId);
                pending.Remove(oldestId);
            }

            checkpoint = new PendingCheckpoint(snapshotId);
            pending.Add(snapshotId, checkpoint);
            return EventCheckpointRecordResult.Accepted;
        }

        private long FirstPendingId()
        {
            foreach (var id in pending.Keys) return id;
            return -1L;
        }

        private void RemoveThrough(long snapshotId)
        {
            var remove = new List<long>();
            foreach (var id in pending.Keys)
            {
                if (id > snapshotId) break;
                remove.Add(id);
            }
            for (var i = 0; i < remove.Count; i++) pending.Remove(remove[i]);
        }

        private sealed class PendingCheckpoint
        {
            public PendingCheckpoint(long snapshotId)
            {
                SnapshotId = snapshotId;
            }

            public long SnapshotId { get; }
            public bool HasBegin;
            public int ExpectedCount;
            public long BeginSequence;
            public bool HasEnd;
            public int EndCount;
            public long EndSequence;
            public readonly Dictionary<string, long> EventSequences =
                new Dictionary<string, long>(StringComparer.Ordinal);

            public bool HasStateAtOrBefore(long sequence)
            {
                foreach (var stateSequence in EventSequences.Values)
                {
                    if (stateSequence <= sequence) return true;
                }
                return false;
            }

            public bool HasStateAtOrAfter(long sequence)
            {
                foreach (var stateSequence in EventSequences.Values)
                {
                    if (stateSequence >= sequence) return true;
                }
                return false;
            }
        }
    }
}
