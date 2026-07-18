using System;

namespace GoingCooperative.Core
{
    public static class ReplicationOrderingPolicy
    {
        public static bool ShouldReplaceQueuedAbsoluteState(long queuedSequence, long incomingSequence)
        {
            return incomingSequence > queuedSequence;
        }

        public static bool IsStaleAppliedAbsoluteState(long appliedSequenceHighWater, long incomingSequence)
        {
            return appliedSequenceHighWater >= 0L
                && incomingSequence <= appliedSequenceHighWater;
        }

        public static long AdvanceAppliedAbsoluteState(long appliedSequenceHighWater, long appliedSequence)
        {
            return Math.Max(appliedSequenceHighWater, appliedSequence);
        }

        public static bool IsStaleSnapshot(long latestSnapshotId, long incomingSnapshotId)
        {
            return latestSnapshotId > 0L
                && incomingSnapshotId > 0L
                && incomingSnapshotId < latestSnapshotId;
        }

        public static long AdvanceSnapshot(long latestSnapshotId, long incomingSnapshotId)
        {
            return Math.Max(latestSnapshotId, incomingSnapshotId);
        }

        public static bool IsSnapshotComplete(bool endReceived, int expectedCount, int seenCount)
        {
            return endReceived
                && expectedCount >= 0
                && seenCount == expectedCount;
        }

        public static bool ShouldAcceptBuildBatch(int committedCount, int requestedCount)
        {
            return requestedCount > 0 && committedCount == requestedCount;
        }

        public static bool ShouldLatchBuildBatchRecovery(
            int committedCount,
            int requestedCount,
            bool rollbackAttempted,
            bool rollbackProven)
        {
            return !ShouldAcceptBuildBatch(committedCount, requestedCount)
                && rollbackAttempted
                && !rollbackProven;
        }

        public static bool ShouldEscalateBuildBatchReplay(int failureCount, int maximumFailures)
        {
            return maximumFailures > 0 && failureCount >= maximumFailures;
        }

        public static bool IsValidBuildRoofPositionCount(int positionCount, int maximumPositionCount)
        {
            return positionCount > 0
                && maximumPositionCount > 0
                && positionCount <= maximumPositionCount;
        }

        public static int GetBuildRepairDependencyRank(bool hasExactReplay, bool isRoof)
        {
            if (!hasExactReplay)
            {
                return 2;
            }

            return isRoof ? 1 : 0;
        }

        public static int GetBoundedSnapshotPageCount(int totalCount, int pageOffset, int maximumPageCount)
        {
            if (totalCount < 0
                || pageOffset < 0
                || pageOffset > totalCount
                || maximumPageCount <= 0)
            {
                return 0;
            }

            return Math.Min(maximumPageCount, totalCount - pageOffset);
        }

        public static int AdvanceSnapshotPageOffset(int totalCount, int pageOffset, int pageCount)
        {
            if (totalCount <= 0 || pageOffset < 0 || pageCount < 0)
            {
                return 0;
            }

            return Math.Min(totalCount, pageOffset + pageCount);
        }

        public static bool IsFinalSnapshotPage(int totalCount, int pageOffset, int pageCount)
        {
            return totalCount >= 0
                && pageOffset >= 0
                && pageCount >= 0
                && pageOffset + pageCount >= totalCount;
        }

        public static bool ShouldPruneCheckpointEvent(
            bool seenInCheckpoint,
            long stateSequence,
            long checkpointBeginSequence)
        {
            return !seenInCheckpoint && stateSequence < checkpointBeginSequence;
        }

        public static bool IsValidGameTime(int minutesTotal, float minuteFraction, float unityTime)
        {
            return minutesTotal >= 0
                && !float.IsNaN(minuteFraction)
                && !float.IsInfinity(minuteFraction)
                && minuteFraction >= 0f
                && minuteFraction < 1f
                && !float.IsNaN(unityTime)
                && !float.IsInfinity(unityTime);
        }
    }
}
