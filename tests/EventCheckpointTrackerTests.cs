using System;
using GoingCooperative.Core;

internal static class EventCheckpointTrackerTests
{
    private static int failures;

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (Equals(expected, actual)) return;
        Console.Error.WriteLine("FAIL " + name + " expected=" + expected + " actual=" + actual);
        failures++;
    }

    public static int Main()
    {
        TraderStartAuthorityRequiresLiveCompatibleClient();
        TraderSerializerAssemblyIdentityMustMatch();
        EndBeforeBeginCompletesWithoutRetry();
        StatesMayArriveBeforeTheirEnvelope();
        IncompleteCheckpointWaitsForMissingState();
        NewerCompleteCheckpointSupersedesOlderPartialData();
        DuplicateAndConflictingRecordsAreDistinguished();
        EveryDeliveryPermutationCompletes();
        StateSequenceMustBeInsideCheckpointEnvelope();
        DuplicateEventIdMustRetainItsOriginalSequence();
        PendingWindowIsBounded();

        if (failures != 0) return 1;
        Console.WriteLine("PASS EventCheckpointTrackerTests");
        return 0;
    }

    private static void TraderStartAuthorityRequiresLiveCompatibleClient()
    {
        Equal(true, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, true, true, true, true, false, false), "connected client suppresses native trader starts");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            false, false, true, true, true, true, false, false), "unsupported event families stay native");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, true, true, true, true, true, false, false), "host always keeps native trader starts");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, false, true, true, true, false, false), "config false restores native client traders");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, true, false, true, true, false, false), "pseudo-home replication gate preserves native traders");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, true, true, false, true, false, false), "runtime must be started");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, true, true, true, false, false, false), "compatible handshake is required");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, true, true, true, true, true, false), "native save loading is never suppressed");
        Equal(false, TraderEventAuthorityPolicy.ShouldSuppressClientNativeTraderStart(
            true, false, true, true, true, true, false, true), "replicated trader application cannot self-suppress");
    }

    private static void TraderSerializerAssemblyIdentityMustMatch()
    {
        Equal(TraderSerializerCompatibilityResult.Compatible,
            TraderSerializerCompatibilityPolicy.Evaluate(true, "0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef"),
            "matching game assembly identity permits trader serializer");
        Equal(TraderSerializerCompatibilityResult.AssemblyMismatch,
            TraderSerializerCompatibilityPolicy.Evaluate(true, "0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210"),
            "different game assembly identity refuses trader serializer");
        Equal(TraderSerializerCompatibilityResult.RemoteIdentityMissing,
            TraderSerializerCompatibilityPolicy.Evaluate(true, "0123456789abcdef0123456789abcdef", string.Empty),
            "legacy peer without game assembly identity is refused");
        Equal(TraderSerializerCompatibilityResult.LocalIdentityMissing,
            TraderSerializerCompatibilityPolicy.Evaluate(true, string.Empty, "0123456789abcdef0123456789abcdef"),
            "missing local game assembly identity fails closed");
        Equal(TraderSerializerCompatibilityResult.NotRequired,
            TraderSerializerCompatibilityPolicy.Evaluate(false, "local", "different"),
            "non-trader lanes preserve legacy mismatch behavior");
    }

    private static void EndBeforeBeginCompletesWithoutRetry()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordEnd(1, 0, 12), "early End accepted");
        Equal(false, tracker.TryTakeNewestComplete(out _), "early End waits for Begin");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(1, 0, 10), "late Begin accepted");
        Equal(true, tracker.TryTakeNewestComplete(out var completion), "empty checkpoint completes after late Begin");
        Equal(1L, completion?.SnapshotId, "empty checkpoint id");
        Equal(10L, completion?.BeginSequence, "empty checkpoint Begin sequence");
        Equal(12L, completion?.EndSequence, "empty checkpoint End sequence");
        Equal(0, completion?.EventCount, "empty checkpoint membership");
    }

    private static void StatesMayArriveBeforeTheirEnvelope()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(2, "event-b", 24), "first early State accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(2, "event-a", 23), "second early State accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordEnd(2, 2, 25), "early End with states accepted");
        Equal(false, tracker.TryTakeNewestComplete(out _), "states and End wait for Begin");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(2, 2, 20), "late Begin after rows accepted");
        Equal(true, tracker.TryTakeNewestComplete(out var completion), "out-of-order populated checkpoint completes");
        Equal(true, completion?.ContainsEvent("event-a"), "completion contains event-a");
        Equal(true, completion?.ContainsEvent("event-b"), "completion contains event-b");
    }

    private static void IncompleteCheckpointWaitsForMissingState()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(3, 2, 30), "Begin accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(3, "event-a", 31), "first State accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordEnd(3, 2, 39), "End accepted while row missing");
        Equal(false, tracker.TryTakeNewestComplete(out _), "checkpoint does not prune while incomplete");
        Equal(EventCheckpointRecordResult.Duplicate, tracker.RecordState(3, "event-a", 31), "duplicate State is idempotent");
        Equal(false, tracker.TryTakeNewestComplete(out _), "duplicate State cannot satisfy distinct count");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(3, "event-b", 32), "missing State accepted");
        Equal(true, tracker.TryTakeNewestComplete(out var completion), "missing State completes buffered checkpoint");
        Equal(2, completion?.EventCount, "complete distinct membership count");
    }

    private static void NewerCompleteCheckpointSupersedesOlderPartialData()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(10, 1, 100), "older Begin accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(10, "old-event", 101), "older State accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordEnd(11, 0, 115), "newer End accepted first");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(11, 0, 110), "newer Begin accepted last");
        Equal(true, tracker.TryTakeNewestComplete(out var completion), "newer complete checkpoint finalizes");
        Equal(11L, completion?.SnapshotId, "newest complete checkpoint chosen");
        Equal(EventCheckpointRecordResult.Stale, tracker.RecordEnd(10, 1, 105), "older late End is terminal stale");
        Equal(EventCheckpointRecordResult.Stale, tracker.RecordState(11, "late-event", 112), "completed row cannot resurrect state");
    }

    private static void DuplicateAndConflictingRecordsAreDistinguished()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(20, 1, 200), "initial Begin accepted");
        Equal(EventCheckpointRecordResult.Duplicate, tracker.RecordBegin(20, 1, 200), "matching Begin duplicate");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordBegin(20, 2, 200), "conflicting Begin count rejected");
        tracker.AbandonThrough(20);
        Equal(EventCheckpointRecordResult.Superseded, tracker.RecordState(20, "event-a", 201), "abandoned checkpoint stays superseded");

        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordEnd(21, 1, 210), "initial End accepted");
        Equal(EventCheckpointRecordResult.Duplicate, tracker.RecordEnd(21, 1, 210), "matching End duplicate");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordEnd(21, 1, 211), "same checkpoint cannot change End sequence");

        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(22, 0, 220), "ordered Begin accepted");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordEnd(22, 0, 219), "End before Begin in host sequence is rejected");
    }

    private static void EveryDeliveryPermutationCompletes()
    {
        var records = new[] { 0, 1, 2, 3 };
        VisitPermutations(records, 0, order =>
        {
            var tracker = new EventCheckpointTracker(8, 32);
            for (var i = 0; i < order.Length; i++)
            {
                EventCheckpointRecordResult result;
                switch (order[i])
                {
                    case 0:
                        result = tracker.RecordBegin(25, 2, 250);
                        break;
                    case 1:
                        result = tracker.RecordState(25, "event-a", 251);
                        break;
                    case 2:
                        result = tracker.RecordState(25, "event-b", 252);
                        break;
                    default:
                        result = tracker.RecordEnd(25, 2, 253);
                        break;
                }
                Equal(EventCheckpointRecordResult.Accepted, result, "permutation record accepted order=" + string.Join(",", order));
            }
            Equal(true, tracker.TryTakeNewestComplete(out var completion), "permutation completes order=" + string.Join(",", order));
            Equal(2, completion?.EventCount, "permutation membership count order=" + string.Join(",", order));
        });
    }

    private static void VisitPermutations(int[] values, int index, Action<int[]> visitor)
    {
        if (index == values.Length)
        {
            visitor((int[])values.Clone());
            return;
        }
        for (var i = index; i < values.Length; i++)
        {
            var value = values[index];
            values[index] = values[i];
            values[i] = value;
            VisitPermutations(values, index + 1, visitor);
            value = values[index];
            values[index] = values[i];
            values[i] = value;
        }
    }

    private static void StateSequenceMustBeInsideCheckpointEnvelope()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(26, "before-begin-arrival", 259), "early delivery stores sender sequence");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordBegin(26, 1, 260), "late Begin rejects row emitted before Begin");

        tracker.Reset();
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(27, "after-end-arrival", 275), "early delivery before End stores sender sequence");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordEnd(27, 1, 274), "late End rejects row emitted after End");

        tracker.Reset();
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(28, 1, 280), "ordered envelope Begin accepted");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordState(28, "before-begin", 279), "row sequence before Begin rejected");

        tracker.Reset();
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordEnd(29, 1, 299), "ordered envelope End accepted first");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordState(29, "after-end", 300), "row sequence after End rejected");
    }

    private static void DuplicateEventIdMustRetainItsOriginalSequence()
    {
        var tracker = new EventCheckpointTracker(8, 32);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(30, "event-a", 301), "first event row accepted");
        Equal(EventCheckpointRecordResult.Duplicate, tracker.RecordState(30, "event-a", 301), "same event and sequence is a retry");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordState(30, "event-a", 302), "same event with another sequence conflicts");
    }

    private static void PendingWindowIsBounded()
    {
        var tracker = new EventCheckpointTracker(2, 2);
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(30, "a", 301), "first pending checkpoint accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(31, "b", 311), "second pending checkpoint accepted");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordState(32, "c", 321), "newest checkpoint evicts oldest");
        Equal(2, tracker.PendingCheckpointCount, "pending checkpoint window bounded");
        Equal(EventCheckpointRecordResult.Superseded, tracker.RecordBegin(30, 1, 300), "evicted checkpoint cannot return");
        Equal(EventCheckpointRecordResult.Accepted, tracker.RecordBegin(31, 1, 310), "retained checkpoint count accepted");
        Equal(EventCheckpointRecordResult.Conflict, tracker.RecordState(31, "overflow", 312), "advertised or ended count bounds extra distinct states");
        tracker.AbandonThrough(31);
        tracker.Reset();
        Equal(-1L, tracker.CompletedCheckpointId, "reset clears completion high-water");
        Equal(0, tracker.PendingCheckpointCount, "reset clears pending checkpoints");
    }
}
