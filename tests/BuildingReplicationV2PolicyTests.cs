using System;
using GoingCooperative.Core;

internal static class BuildingReplicationV2PolicyTests
{
    private static int failures;

    public static int Run()
    {
        TransactionLedgerAppliesExactlyOnce();
        TransactionLedgerFailsClosedAtCapacity();
        BuildingRevisionsAdvanceOnlyAfterCommit();
        RecoveryBaselineNeverOverwritesNewerLiveState();
        RecoveryAndLiveDeliveryOrdersConverge();
        CapabilitySelectionFailsClosed();
        CapabilityWireTokenRoundTripsStrictly();

        Console.WriteLine(failures == 0
            ? "PASS BuildingReplicationV2PolicyTests"
            : "FAILED BuildingReplicationV2PolicyTests " + failures);
        return failures;
    }

    private static void TransactionLedgerAppliesExactlyOnce()
    {
        var ledger = new BuildingTransactionApplyLedger(4);
        Equal(BuildingTransactionBeginDisposition.Apply, ledger.Begin(7, "client:42"), "first transaction begins");
        Equal(BuildingTransactionBeginDisposition.AlreadyInProgress, ledger.Begin(7, "client:42"), "in-flight retry cannot place twice");
        Equal(BuildingTransactionCommitDisposition.Committed, ledger.Commit(7, "client:42"), "successful native apply commits");
        Equal(true, ledger.IsApplied(7, "client:42"), "committed transaction is retained");
        Equal(BuildingTransactionBeginDisposition.AlreadyApplied, ledger.Begin(7, "client:42"), "committed retry is acknowledged without placement");
        Equal(BuildingTransactionCommitDisposition.AlreadyCommitted, ledger.Commit(7, "client:42"), "commit is idempotent");
        Equal(false, ledger.Abort(7, "client:42"), "applied tombstone cannot be aborted");

        Equal(BuildingTransactionBeginDisposition.Apply, ledger.Begin(7, "client:43"), "second transaction begins");
        Equal(true, ledger.Abort(7, "client:43"), "proven native rollback releases in-flight transaction");
        Equal(BuildingTransactionBeginDisposition.Apply, ledger.Begin(7, "client:43"), "rolled-back transaction can be retried");

        Equal(true, ledger.AdvanceEpoch(8), "new loaded-world epoch clears exact-once scope");
        Equal(BuildingTransactionBeginDisposition.Apply, ledger.Begin(8, "client:42"), "same transaction identity is valid in new epoch");
        Equal(BuildingTransactionBeginDisposition.StaleEpoch, ledger.Begin(7, "client:99"), "old-world transaction cannot return");
        Equal(BuildingTransactionCommitDisposition.StaleEpoch, ledger.Commit(7, "client:42"), "old-world commit cannot mutate current epoch");
    }

    private static void TransactionLedgerFailsClosedAtCapacity()
    {
        var ledger = new BuildingTransactionApplyLedger(2);
        Equal(BuildingTransactionBeginDisposition.Apply, ledger.Begin(1, "tx-1"), "capacity transaction one begins");
        Equal(BuildingTransactionCommitDisposition.Committed, ledger.Commit(1, "tx-1"), "capacity transaction one commits");
        Equal(BuildingTransactionBeginDisposition.Apply, ledger.Begin(1, "tx-2"), "capacity transaction two begins");
        Equal(BuildingTransactionBeginDisposition.CapacityExceeded, ledger.Begin(1, "tx-3"), "ledger never evicts exact-once tombstones inside epoch");
        Equal(BuildingTransactionBeginDisposition.AlreadyApplied, ledger.Begin(1, "tx-1"), "known duplicate remains recognizable at capacity");
    }

    private static void BuildingRevisionsAdvanceOnlyAfterCommit()
    {
        var ledger = new BuildingRevisionLedger(4);
        Equal(BuildingRevisionDisposition.Apply, ledger.EvaluateLiveDelta(3, "building-a", 1), "first building revision evaluates for apply");
        Equal(BuildingRevisionDisposition.Apply, ledger.EvaluateLiveDelta(3, "building-a", 1), "evaluation alone does not consume revision");
        Equal(BuildingRevisionDisposition.Apply, ledger.CommitLiveDelta(3, "building-a", 1, 10), "successful native lifecycle apply advances revision");
        Equal(BuildingRevisionDisposition.Duplicate, ledger.EvaluateLiveDelta(3, "building-a", 1), "same building revision is duplicate");
        Equal(BuildingRevisionDisposition.StaleRevision, ledger.EvaluateLiveDelta(3, "building-a", 0), "older building revision is stale");
        Equal(BuildingRevisionDisposition.Apply, ledger.EvaluateLiveDelta(3, "building-a", 2), "newer building revision applies");
        Equal(BuildingRevisionDisposition.Apply, ledger.CommitLiveDelta(3, "building-a", 2, 11), "newer revision commits");

        Equal(BuildingRevisionDisposition.Apply, ledger.CommitLiveDelta(3, "building-b", 0, 12), "different building owns independent revision cursor");
        Equal(true, ledger.TryGetCursor("building-a", out var buildingA), "first cursor retained");
        Equal(2L, buildingA.Revision, "first building cursor is unaffected by second building");
        Equal(11L, buildingA.LatestLiveSequence, "cursor retains newest live world sequence");

        Equal(true, ledger.AdvanceEpoch(4), "revision ledger advances loaded-world epoch");
        Equal(BuildingRevisionDisposition.Apply, ledger.EvaluateLiveDelta(4, "building-a", 0), "new epoch starts fresh identity space");
        Equal(BuildingRevisionDisposition.StaleEpoch, ledger.EvaluateLiveDelta(3, "building-a", 99), "old epoch cannot override new world");
    }

    private static void RecoveryBaselineNeverOverwritesNewerLiveState()
    {
        var ledger = new BuildingRevisionLedger(8);
        Equal(BuildingRevisionDisposition.Apply, ledger.CommitLiveDelta(5, "wall-1", 4, 101), "live revision newer than recovery baseline commits");
        Equal(
            BuildingRevisionDisposition.SupersededByNewerLiveDelta,
            ledger.EvaluateRecoveryRow(5, "wall-1", 3, 100),
            "stale recovery row loses to newer live delta");
        Equal(
            BuildingRevisionDisposition.SupersededByNewerLiveDelta,
            ledger.EvaluateRecoveryRow(5, "wall-1", 5, 100),
            "even a suspicious higher snapshot revision cannot cross newer-live baseline fence");
        Equal(
            BuildingRevisionDisposition.Duplicate,
            ledger.EvaluateRecoveryRow(5, "wall-1", 4, 101),
            "recovery at current baseline recognizes already-applied revision");
        Equal(
            BuildingRevisionDisposition.Apply,
            ledger.CommitRecoveryRow(5, "wall-1", 5, 101),
            "recovery may advance when no post-baseline live delta exists");
        Equal(true, ledger.TryGetCursor("wall-1", out var cursor), "recovery cursor retained");
        Equal(5L, cursor.Revision, "recovery advances authoritative revision");
        Equal(101L, cursor.LatestLiveSequence, "recovery does not pretend to be a live delta");
    }

    private static void RecoveryAndLiveDeliveryOrdersConverge()
    {
        var snapshotFirst = new BuildingRevisionLedger(8);
        Equal(BuildingRevisionDisposition.Apply, snapshotFirst.CommitRecoveryRow(9, "roof-7", 3, 200), "snapshot-first row applies");
        Equal(BuildingRevisionDisposition.Apply, snapshotFirst.CommitLiveDelta(9, "roof-7", 4, 201), "newer live delta applies after snapshot");
        Equal(
            BuildingRevisionDisposition.SupersededByNewerLiveDelta,
            snapshotFirst.EvaluateRecoveryRow(9, "roof-7", 3, 200),
            "snapshot retry cannot regress live result");

        var liveFirst = new BuildingRevisionLedger(8);
        Equal(BuildingRevisionDisposition.Apply, liveFirst.CommitLiveDelta(9, "roof-7", 4, 201), "live-first delta applies");
        Equal(
            BuildingRevisionDisposition.SupersededByNewerLiveDelta,
            liveFirst.CommitRecoveryRow(9, "roof-7", 3, 200),
            "late snapshot row cannot overwrite live-first result");

        Equal(true, snapshotFirst.TryGetCursor("roof-7", out var snapshotFirstCursor), "snapshot-first cursor exists");
        Equal(true, liveFirst.TryGetCursor("roof-7", out var liveFirstCursor), "live-first cursor exists");
        Equal(snapshotFirstCursor.Revision, liveFirstCursor.Revision, "delivery permutations converge on revision");
        Equal(snapshotFirstCursor.LatestLiveSequence, liveFirstCursor.LatestLiveSequence, "delivery permutations converge on live sequence");

        var revisionWinsWithinBaseline = new BuildingRevisionLedger(8);
        Equal(BuildingRevisionDisposition.Apply, revisionWinsWithinBaseline.CommitLiveDelta(9, "floor-2", 4, 199), "pre-baseline live delta applies");
        Equal(
            BuildingRevisionDisposition.StaleRevision,
            revisionWinsWithinBaseline.EvaluateRecoveryRow(9, "floor-2", 3, 200),
            "revision high-water protects state even when live sequence predates baseline");
    }

    private static void CapabilitySelectionFailsClosed()
    {
        var v2 = new BuildingReplicationCapability(
            BuildingReplicationMode.TransactionLifecycleV2,
            BuildingReplicationCapability.CurrentTransactionSchemaVersion,
            true);
        var v2Peer = new BuildingReplicationCapability(
            BuildingReplicationMode.TransactionLifecycleV2,
            BuildingReplicationCapability.CurrentTransactionSchemaVersion,
            false);
        var decision = BuildingReplicationCompatibilityPolicy.Evaluate(v2, v2Peer);
        Equal(true, decision.IsCompatible, "matching V2 schema connects");
        Equal(BuildingReplicationMode.TransactionLifecycleV2, decision.SelectedMode, "V2 remains selected");

        var futureSchema = new BuildingReplicationCapability(
            BuildingReplicationMode.TransactionLifecycleV2,
            BuildingReplicationCapability.CurrentTransactionSchemaVersion + 1,
            true);
        Equal(
            BuildingReplicationCompatibilityDisposition.TransactionSchemaMismatch,
            BuildingReplicationCompatibilityPolicy.Evaluate(v2, futureSchema).Disposition,
            "unknown building transaction schema fails closed");

        var legacy = new BuildingReplicationCapability(
            BuildingReplicationMode.LegacySnapshots,
            BuildingReplicationCapability.CurrentTransactionSchemaVersion,
            true);
        Equal(
            BuildingReplicationCompatibilityDisposition.SelectedModeMismatch,
            BuildingReplicationCompatibilityPolicy.Evaluate(v2, legacy).Disposition,
            "legacy support never silently downgrades a V2-selected peer");
        Equal(
            BuildingReplicationCompatibilityDisposition.Compatible,
            BuildingReplicationCompatibilityPolicy.Evaluate(legacy, legacy).Disposition,
            "explicit rollback selection is compatible on both peers");

        var legacyUnavailable = new BuildingReplicationCapability(
            BuildingReplicationMode.LegacySnapshots,
            BuildingReplicationCapability.CurrentTransactionSchemaVersion,
            false);
        Equal(
            BuildingReplicationCompatibilityDisposition.LegacyRollbackUnavailable,
            BuildingReplicationCompatibilityPolicy.Evaluate(legacy, legacyUnavailable).Disposition,
            "rollback refuses a peer without legacy implementation");
    }

    private static void CapabilityWireTokenRoundTripsStrictly()
    {
        var expected = new BuildingReplicationCapability(
            BuildingReplicationMode.TransactionLifecycleV2,
            BuildingReplicationCapability.CurrentTransactionSchemaVersion,
            true);
        Equal(true, BuildingReplicationCapability.TryParseWireToken(expected.ToWireToken(), out var parsed), "capability token parses");
        Equal(expected.SelectedMode, parsed.SelectedMode, "capability mode roundtrips");
        Equal(expected.TransactionSchemaVersion, parsed.TransactionSchemaVersion, "capability schema roundtrips");
        Equal(expected.LegacyRollbackSupported, parsed.LegacyRollbackSupported, "rollback support roundtrips");
        Equal(false, BuildingReplicationCapability.TryParseWireToken(null, out _), "missing capability is rejected");
        Equal(false, BuildingReplicationCapability.TryParseWireToken("building-replication-v2:2:1", out _), "truncated capability is rejected");
        Equal(false, BuildingReplicationCapability.TryParseWireToken("building-replication-v2:02:1:1", out _), "noncanonical mode number is rejected");
        Equal(false, BuildingReplicationCapability.TryParseWireToken("building-replication-v2:2:1:yes", out _), "noncanonical rollback bit is rejected");
        Equal(false, BuildingReplicationCapability.TryParseWireToken("building-replication-v1:2:1:1", out _), "old capability prefix is rejected");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!Equals(expected, actual))
        {
            Console.Error.WriteLine("FAIL BuildingReplicationV2 " + name + " expected=" + expected + " actual=" + actual);
            failures++;
        }
    }
}
