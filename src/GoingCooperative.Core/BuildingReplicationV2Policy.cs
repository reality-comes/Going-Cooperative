using System;
using System.Collections.Generic;
using System.Globalization;

namespace GoingCooperative.Core
{
    public enum BuildingTransactionBeginDisposition
    {
        Apply = 0,
        AlreadyApplied = 1,
        AlreadyInProgress = 2,
        StaleEpoch = 3,
        CapacityExceeded = 4
    }

    public enum BuildingTransactionCommitDisposition
    {
        Committed = 0,
        AlreadyCommitted = 1,
        NotBegun = 2,
        StaleEpoch = 3
    }

    /// <summary>
    /// Tracks transaction application for one loaded-world epoch. A caller must
    /// Begin before invoking native placement, Commit only after every requested
    /// item was applied, and Abort after a proven rollback. Applied transactions
    /// remain as tombstones for the complete epoch so a reliable retry can be
    /// acknowledged without placing the batch twice.
    /// </summary>
    public sealed class BuildingTransactionApplyLedger
    {
        private readonly int maximumTransactionsPerEpoch;
        private readonly Dictionary<string, TransactionState> transactions =
            new Dictionary<string, TransactionState>(StringComparer.Ordinal);
        private long currentEpoch = -1L;

        public BuildingTransactionApplyLedger(int maximumTransactionsPerEpoch)
        {
            if (maximumTransactionsPerEpoch <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTransactionsPerEpoch));
            }

            this.maximumTransactionsPerEpoch = maximumTransactionsPerEpoch;
        }

        public long CurrentEpoch => currentEpoch;

        public int TransactionCount => transactions.Count;

        public BuildingTransactionBeginDisposition Begin(long epoch, string transactionId)
        {
            ValidateEpochAndIdentity(epoch, transactionId);
            if (epoch < currentEpoch)
            {
                return BuildingTransactionBeginDisposition.StaleEpoch;
            }

            AdvanceEpochIfNeeded(epoch);
            if (transactions.TryGetValue(transactionId, out var state))
            {
                return state == TransactionState.Applied
                    ? BuildingTransactionBeginDisposition.AlreadyApplied
                    : BuildingTransactionBeginDisposition.AlreadyInProgress;
            }

            if (transactions.Count >= maximumTransactionsPerEpoch)
            {
                return BuildingTransactionBeginDisposition.CapacityExceeded;
            }

            transactions.Add(transactionId, TransactionState.Applying);
            return BuildingTransactionBeginDisposition.Apply;
        }

        public BuildingTransactionCommitDisposition Commit(long epoch, string transactionId)
        {
            ValidateEpochAndIdentity(epoch, transactionId);
            if (epoch != currentEpoch)
            {
                return epoch < currentEpoch
                    ? BuildingTransactionCommitDisposition.StaleEpoch
                    : BuildingTransactionCommitDisposition.NotBegun;
            }

            if (!transactions.TryGetValue(transactionId, out var state))
            {
                return BuildingTransactionCommitDisposition.NotBegun;
            }

            if (state == TransactionState.Applied)
            {
                return BuildingTransactionCommitDisposition.AlreadyCommitted;
            }

            transactions[transactionId] = TransactionState.Applied;
            return BuildingTransactionCommitDisposition.Committed;
        }

        public bool Abort(long epoch, string transactionId)
        {
            ValidateEpochAndIdentity(epoch, transactionId);
            if (epoch != currentEpoch
                || !transactions.TryGetValue(transactionId, out var state)
                || state != TransactionState.Applying)
            {
                return false;
            }

            return transactions.Remove(transactionId);
        }

        public bool IsApplied(long epoch, string transactionId)
        {
            ValidateEpochAndIdentity(epoch, transactionId);
            return epoch == currentEpoch
                && transactions.TryGetValue(transactionId, out var state)
                && state == TransactionState.Applied;
        }

        public bool AdvanceEpoch(long epoch)
        {
            if (epoch < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch));
            }

            if (epoch <= currentEpoch)
            {
                return false;
            }

            currentEpoch = epoch;
            transactions.Clear();
            return true;
        }

        private void AdvanceEpochIfNeeded(long epoch)
        {
            if (epoch > currentEpoch)
            {
                currentEpoch = epoch;
                transactions.Clear();
            }
        }

        private static void ValidateEpochAndIdentity(long epoch, string transactionId)
        {
            if (epoch < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch));
            }

            if (string.IsNullOrWhiteSpace(transactionId))
            {
                throw new ArgumentException("A building transaction identity cannot be blank.", nameof(transactionId));
            }
        }

        private enum TransactionState
        {
            Applying = 0,
            Applied = 1
        }
    }

    public enum BuildingRevisionDisposition
    {
        Apply = 0,
        Duplicate = 1,
        StaleRevision = 2,
        StaleEpoch = 3,
        SupersededByNewerLiveDelta = 4,
        CapacityExceeded = 5
    }

    public readonly struct BuildingRevisionCursor
    {
        internal BuildingRevisionCursor(long revision, long latestLiveSequence)
        {
            Revision = revision;
            LatestLiveSequence = latestLiveSequence;
        }

        public long Revision { get; }

        public long LatestLiveSequence { get; }
    }

    public static class BuildingRecoveryOrderingPolicy
    {
        public static BuildingRevisionDisposition EvaluateSnapshotRow(
            BuildingRevisionCursor cursor,
            long snapshotBaselineSequence,
            long snapshotRevision)
        {
            if (snapshotBaselineSequence < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshotBaselineSequence));
            }

            if (snapshotRevision < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshotRevision));
            }

            if (cursor.LatestLiveSequence > snapshotBaselineSequence)
            {
                return BuildingRevisionDisposition.SupersededByNewerLiveDelta;
            }

            return CompareRevision(cursor.Revision, snapshotRevision);
        }

        internal static BuildingRevisionDisposition CompareRevision(
            long appliedRevision,
            long incomingRevision)
        {
            if (incomingRevision < appliedRevision)
            {
                return BuildingRevisionDisposition.StaleRevision;
            }

            return incomingRevision == appliedRevision
                ? BuildingRevisionDisposition.Duplicate
                : BuildingRevisionDisposition.Apply;
        }
    }

    /// <summary>
    /// Stores per-building lifecycle high-water marks, including terminal
    /// building revisions. Call Evaluate before native application and Commit
    /// only after the native operation succeeds. Recovery rows are additionally
    /// fenced by the snapshot's world-sequence baseline so an older row cannot
    /// overwrite a live delta emitted after collection began.
    /// </summary>
    public sealed class BuildingRevisionLedger
    {
        private readonly int maximumBuildingsPerEpoch;
        private readonly Dictionary<string, BuildingRevisionCursor> cursors =
            new Dictionary<string, BuildingRevisionCursor>(StringComparer.Ordinal);
        private long currentEpoch = -1L;

        public BuildingRevisionLedger(int maximumBuildingsPerEpoch)
        {
            if (maximumBuildingsPerEpoch <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBuildingsPerEpoch));
            }

            this.maximumBuildingsPerEpoch = maximumBuildingsPerEpoch;
        }

        public long CurrentEpoch => currentEpoch;

        public int BuildingCount => cursors.Count;

        public BuildingRevisionDisposition EvaluateLiveDelta(
            long epoch,
            string buildingId,
            long revision)
        {
            ValidateStamp(epoch, buildingId, revision);
            if (epoch < currentEpoch)
            {
                return BuildingRevisionDisposition.StaleEpoch;
            }

            if (epoch > currentEpoch)
            {
                return BuildingRevisionDisposition.Apply;
            }

            if (!cursors.TryGetValue(buildingId, out var cursor))
            {
                return cursors.Count >= maximumBuildingsPerEpoch
                    ? BuildingRevisionDisposition.CapacityExceeded
                    : BuildingRevisionDisposition.Apply;
            }

            return BuildingRecoveryOrderingPolicy.CompareRevision(cursor.Revision, revision);
        }

        public BuildingRevisionDisposition CommitLiveDelta(
            long epoch,
            string buildingId,
            long revision,
            long worldSequence)
        {
            if (worldSequence < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(worldSequence));
            }

            var disposition = EvaluateLiveDelta(epoch, buildingId, revision);
            if (disposition != BuildingRevisionDisposition.Apply)
            {
                return disposition;
            }

            AdvanceEpochIfNeeded(epoch);
            cursors[buildingId] = new BuildingRevisionCursor(revision, worldSequence);
            return BuildingRevisionDisposition.Apply;
        }

        public BuildingRevisionDisposition EvaluateRecoveryRow(
            long epoch,
            string buildingId,
            long revision,
            long snapshotBaselineSequence)
        {
            ValidateStamp(epoch, buildingId, revision);
            if (snapshotBaselineSequence < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshotBaselineSequence));
            }

            if (epoch < currentEpoch)
            {
                return BuildingRevisionDisposition.StaleEpoch;
            }

            if (epoch > currentEpoch)
            {
                return BuildingRevisionDisposition.Apply;
            }

            if (!cursors.TryGetValue(buildingId, out var cursor))
            {
                return cursors.Count >= maximumBuildingsPerEpoch
                    ? BuildingRevisionDisposition.CapacityExceeded
                    : BuildingRevisionDisposition.Apply;
            }

            return BuildingRecoveryOrderingPolicy.EvaluateSnapshotRow(
                cursor,
                snapshotBaselineSequence,
                revision);
        }

        public BuildingRevisionDisposition CommitRecoveryRow(
            long epoch,
            string buildingId,
            long revision,
            long snapshotBaselineSequence)
        {
            var disposition = EvaluateRecoveryRow(
                epoch,
                buildingId,
                revision,
                snapshotBaselineSequence);
            if (disposition != BuildingRevisionDisposition.Apply)
            {
                return disposition;
            }

            AdvanceEpochIfNeeded(epoch);
            var latestLiveSequence = cursors.TryGetValue(buildingId, out var cursor)
                ? cursor.LatestLiveSequence
                : -1L;
            cursors[buildingId] = new BuildingRevisionCursor(revision, latestLiveSequence);
            return BuildingRevisionDisposition.Apply;
        }

        public bool TryGetCursor(string buildingId, out BuildingRevisionCursor cursor)
        {
            if (string.IsNullOrWhiteSpace(buildingId))
            {
                throw new ArgumentException("A building identity cannot be blank.", nameof(buildingId));
            }

            return cursors.TryGetValue(buildingId, out cursor);
        }

        public bool AdvanceEpoch(long epoch)
        {
            if (epoch < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch));
            }

            if (epoch <= currentEpoch)
            {
                return false;
            }

            currentEpoch = epoch;
            cursors.Clear();
            return true;
        }

        private void AdvanceEpochIfNeeded(long epoch)
        {
            if (epoch > currentEpoch)
            {
                currentEpoch = epoch;
                cursors.Clear();
            }
        }

        private static void ValidateStamp(long epoch, string buildingId, long revision)
        {
            if (epoch < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch));
            }

            if (string.IsNullOrWhiteSpace(buildingId))
            {
                throw new ArgumentException("A building identity cannot be blank.", nameof(buildingId));
            }

            if (revision < 0L)
            {
                throw new ArgumentOutOfRangeException(nameof(revision));
            }
        }
    }

    public enum BuildingReplicationMode
    {
        LegacySnapshots = 1,
        TransactionLifecycleV2 = 2
    }

    public readonly struct BuildingReplicationCapability
    {
        public const string WirePrefix = "building-replication-v2";
        // Schema 2 adds tagged BeamX/BeamZ/Socketable placement records. Peers on
        // schema 1 must fail the hello rather than flattening those records.
        public const int CurrentTransactionSchemaVersion = 2;

        public BuildingReplicationCapability(
            BuildingReplicationMode selectedMode,
            int transactionSchemaVersion,
            bool legacyRollbackSupported)
        {
            if (!Enum.IsDefined(typeof(BuildingReplicationMode), selectedMode))
            {
                throw new ArgumentOutOfRangeException(nameof(selectedMode));
            }

            if (transactionSchemaVersion < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(transactionSchemaVersion));
            }

            SelectedMode = selectedMode;
            TransactionSchemaVersion = transactionSchemaVersion;
            LegacyRollbackSupported = legacyRollbackSupported;
        }

        public BuildingReplicationMode SelectedMode { get; }

        public int TransactionSchemaVersion { get; }

        public bool LegacyRollbackSupported { get; }

        public string ToWireToken()
        {
            return WirePrefix
                + ":"
                + ((int)SelectedMode).ToString(CultureInfo.InvariantCulture)
                + ":"
                + TransactionSchemaVersion.ToString(CultureInfo.InvariantCulture)
                + ":"
                + (LegacyRollbackSupported ? "1" : "0");
        }

        public static bool TryParseWireToken(
            string? token,
            out BuildingReplicationCapability capability)
        {
            capability = default;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var parts = token!.Split(new[] { ':' }, StringSplitOptions.None);
            if (parts.Length != 4
                || !string.Equals(parts[0], WirePrefix, StringComparison.Ordinal)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var modeValue)
                || !Enum.IsDefined(typeof(BuildingReplicationMode), modeValue)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var schemaVersion)
                || schemaVersion < 0
                || (parts[3] != "0" && parts[3] != "1"))
            {
                return false;
            }

            var parsed = new BuildingReplicationCapability(
                (BuildingReplicationMode)modeValue,
                schemaVersion,
                parts[3] == "1");
            if (!string.Equals(parsed.ToWireToken(), token, StringComparison.Ordinal))
            {
                return false;
            }

            capability = parsed;
            return true;
        }
    }

    public enum BuildingReplicationCompatibilityDisposition
    {
        Compatible = 0,
        SelectedModeMismatch = 1,
        TransactionSchemaMismatch = 2,
        LegacyRollbackUnavailable = 3
    }

    public readonly struct BuildingReplicationCompatibilityDecision
    {
        internal BuildingReplicationCompatibilityDecision(
            BuildingReplicationCompatibilityDisposition disposition,
            BuildingReplicationMode selectedMode)
        {
            Disposition = disposition;
            SelectedMode = selectedMode;
        }

        public BuildingReplicationCompatibilityDisposition Disposition { get; }

        public BuildingReplicationMode SelectedMode { get; }

        public bool IsCompatible =>
            Disposition == BuildingReplicationCompatibilityDisposition.Compatible;
    }

    public static class BuildingReplicationCompatibilityPolicy
    {
        /// <summary>
        /// Requires peers to select the same mode before connecting. Supporting
        /// the legacy lane never permits an implicit downgrade from V2; rollback
        /// is compatible only when both peers explicitly select LegacySnapshots.
        /// </summary>
        public static BuildingReplicationCompatibilityDecision Evaluate(
            BuildingReplicationCapability local,
            BuildingReplicationCapability remote)
        {
            if (local.SelectedMode != remote.SelectedMode)
            {
                return new BuildingReplicationCompatibilityDecision(
                    BuildingReplicationCompatibilityDisposition.SelectedModeMismatch,
                    local.SelectedMode);
            }

            if (local.SelectedMode == BuildingReplicationMode.TransactionLifecycleV2)
            {
                if (local.TransactionSchemaVersion
                        != BuildingReplicationCapability.CurrentTransactionSchemaVersion
                    || remote.TransactionSchemaVersion
                        != BuildingReplicationCapability.CurrentTransactionSchemaVersion)
                {
                    return new BuildingReplicationCompatibilityDecision(
                        BuildingReplicationCompatibilityDisposition.TransactionSchemaMismatch,
                        local.SelectedMode);
                }

                return new BuildingReplicationCompatibilityDecision(
                    BuildingReplicationCompatibilityDisposition.Compatible,
                    local.SelectedMode);
            }

            if (!local.LegacyRollbackSupported || !remote.LegacyRollbackSupported)
            {
                return new BuildingReplicationCompatibilityDecision(
                    BuildingReplicationCompatibilityDisposition.LegacyRollbackUnavailable,
                    local.SelectedMode);
            }

            return new BuildingReplicationCompatibilityDecision(
                BuildingReplicationCompatibilityDisposition.Compatible,
                local.SelectedMode);
        }
    }
}
