using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace GoingCooperative.Core
{
    public enum TraderPartyTransferRecordResult
    {
        Accepted,
        Duplicate,
        Stale,
        Conflict,
        CapacityExceeded
    }

    public sealed class TraderPartyTransferLimits
    {
        public TraderPartyTransferLimits(
            int maxPendingTransfers,
            int maxTrackedEvents,
            int maxChunksPerTransfer,
            int maxChunkBytes,
            int maxBufferedChunks,
            long maxBufferedBytes,
            int maxMemberTombstones,
            int maxTerminalTransferIds,
            long transferExpiryMilliseconds)
        {
            if (maxPendingTransfers <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingTransfers));
            if (maxTrackedEvents <= 0) throw new ArgumentOutOfRangeException(nameof(maxTrackedEvents));
            if (maxChunksPerTransfer <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunksPerTransfer));
            if (maxChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));
            if (maxBufferedChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedChunks));
            if (maxBufferedBytes <= 0L) throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            if (maxMemberTombstones <= 0) throw new ArgumentOutOfRangeException(nameof(maxMemberTombstones));
            if (maxTerminalTransferIds <= 0) throw new ArgumentOutOfRangeException(nameof(maxTerminalTransferIds));
            if (transferExpiryMilliseconds <= 0L) throw new ArgumentOutOfRangeException(nameof(transferExpiryMilliseconds));

            MaxPendingTransfers = maxPendingTransfers;
            MaxTrackedEvents = maxTrackedEvents;
            MaxChunksPerTransfer = maxChunksPerTransfer;
            MaxChunkBytes = maxChunkBytes;
            MaxBufferedChunks = maxBufferedChunks;
            MaxBufferedBytes = maxBufferedBytes;
            MaxMemberTombstones = maxMemberTombstones;
            MaxTerminalTransferIds = maxTerminalTransferIds;
            TransferExpiryMilliseconds = transferExpiryMilliseconds;
        }

        public int MaxPendingTransfers { get; }
        public int MaxTrackedEvents { get; }
        public int MaxChunksPerTransfer { get; }
        public int MaxChunkBytes { get; }
        public int MaxBufferedChunks { get; }
        public long MaxBufferedBytes { get; }
        public int MaxMemberTombstones { get; }
        public int MaxTerminalTransferIds { get; }
        public long TransferExpiryMilliseconds { get; }
    }

    public sealed class TraderPartyTransferManifest
    {
        private readonly string[] memberNetworkIds;
        private readonly IReadOnlyList<string> readOnlyMemberNetworkIds;

        public TraderPartyTransferManifest(
            string transferId,
            string eventId,
            long partyGeneration,
            long manifestRevision,
            int expectedChunkCount,
            int expectedPayloadByteCount,
            IEnumerable<string> memberNetworkIds,
            string expectedPayloadSha256)
        {
            TransferId = RequireIdentifier(transferId, nameof(transferId));
            EventId = RequireIdentifier(eventId, nameof(eventId));
            if (partyGeneration <= 0L) throw new ArgumentOutOfRangeException(nameof(partyGeneration));
            if (manifestRevision <= 0L) throw new ArgumentOutOfRangeException(nameof(manifestRevision));
            if (expectedChunkCount <= 0) throw new ArgumentOutOfRangeException(nameof(expectedChunkCount));
            if (expectedPayloadByteCount <= 0) throw new ArgumentOutOfRangeException(nameof(expectedPayloadByteCount));

            PartyGeneration = partyGeneration;
            ManifestRevision = manifestRevision;
            ExpectedChunkCount = expectedChunkCount;
            ExpectedPayloadByteCount = expectedPayloadByteCount;
            this.memberNetworkIds = CopyMemberIds(memberNetworkIds, nameof(memberNetworkIds));
            readOnlyMemberNetworkIds = Array.AsReadOnly(this.memberNetworkIds);
            ExpectedPayloadSha256 = NormalizeSha256(expectedPayloadSha256, nameof(expectedPayloadSha256));
        }

        public string TransferId { get; }
        public string EventId { get; }
        public long PartyGeneration { get; }
        public long ManifestRevision { get; }
        public int ExpectedChunkCount { get; }
        public int ExpectedPayloadByteCount { get; }
        public int ExpectedAgentCount => memberNetworkIds.Length;
        public IReadOnlyList<string> MemberNetworkIds => readOnlyMemberNetworkIds;
        public string ExpectedPayloadSha256 { get; }

        internal TraderPartyTransferTracker.TransferIdentity ToIdentity()
        {
            return new TraderPartyTransferTracker.TransferIdentity(
                EventId,
                PartyGeneration,
                ManifestRevision,
                ExpectedChunkCount,
                ExpectedPayloadByteCount,
                memberNetworkIds,
                ExpectedPayloadSha256);
        }

        internal static string RequireIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Identifier is required.", parameterName);
            return value;
        }

        internal static string[] CopyMemberIds(IEnumerable<string> values, string parameterName)
        {
            if (values == null) throw new ArgumentNullException(parameterName);
            var result = new List<string>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var memberId = RequireIdentifier(value, parameterName);
                if (!unique.Add(memberId)) throw new ArgumentException("Member network IDs must be unique.", parameterName);
                result.Add(memberId);
            }
            return result.ToArray();
        }

        internal static string NormalizeSha256(string value, string parameterName)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", parameterName);
            var characters = value.ToCharArray();
            for (var i = 0; i < characters.Length; i++)
            {
                var character = characters[i];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F')))
                {
                    throw new ArgumentException("SHA-256 contains a non-hexadecimal character.", parameterName);
                }
                if (character >= 'A' && character <= 'F') characters[i] = (char)(character + ('a' - 'A'));
            }
            return new string(characters);
        }
    }

    public sealed class TraderPartyTransferCommit
    {
        private readonly string[] memberNetworkIds;
        private readonly IReadOnlyList<string> readOnlyMemberNetworkIds;

        public TraderPartyTransferCommit(
            string transferId,
            string eventId,
            long partyGeneration,
            long manifestRevision,
            int actualChunkCount,
            int actualPayloadByteCount,
            IEnumerable<string> memberNetworkIds,
            string computedPayloadSha256)
        {
            TransferId = TraderPartyTransferManifest.RequireIdentifier(transferId, nameof(transferId));
            EventId = TraderPartyTransferManifest.RequireIdentifier(eventId, nameof(eventId));
            if (partyGeneration <= 0L) throw new ArgumentOutOfRangeException(nameof(partyGeneration));
            if (manifestRevision <= 0L) throw new ArgumentOutOfRangeException(nameof(manifestRevision));
            if (actualChunkCount <= 0) throw new ArgumentOutOfRangeException(nameof(actualChunkCount));
            if (actualPayloadByteCount <= 0) throw new ArgumentOutOfRangeException(nameof(actualPayloadByteCount));

            PartyGeneration = partyGeneration;
            ManifestRevision = manifestRevision;
            ActualChunkCount = actualChunkCount;
            ActualPayloadByteCount = actualPayloadByteCount;
            var copiedMemberNetworkIds = TraderPartyTransferManifest.CopyMemberIds(memberNetworkIds, nameof(memberNetworkIds));
            this.memberNetworkIds = copiedMemberNetworkIds;
            readOnlyMemberNetworkIds = Array.AsReadOnly(this.memberNetworkIds);
            ComputedPayloadSha256 = TraderPartyTransferManifest.NormalizeSha256(computedPayloadSha256, nameof(computedPayloadSha256));
        }

        public string TransferId { get; }
        public string EventId { get; }
        public long PartyGeneration { get; }
        public long ManifestRevision { get; }
        public int ActualChunkCount { get; }
        public int ActualPayloadByteCount { get; }
        public int ActualAgentCount => memberNetworkIds.Length;
        public IReadOnlyList<string> MemberNetworkIds => readOnlyMemberNetworkIds;
        public string ComputedPayloadSha256 { get; }

        internal TraderPartyTransferTracker.TransferIdentity ToIdentity()
        {
            return new TraderPartyTransferTracker.TransferIdentity(
                EventId,
                PartyGeneration,
                ManifestRevision,
                ActualChunkCount,
                ActualPayloadByteCount,
                memberNetworkIds,
                ComputedPayloadSha256);
        }
    }

    public sealed class TraderPartyTransferChunk
    {
        private readonly byte[] payload;

        public TraderPartyTransferChunk(string transferId, int index, byte[] payload)
        {
            TransferId = TraderPartyTransferManifest.RequireIdentifier(transferId, nameof(transferId));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0) throw new ArgumentException("Chunk payload cannot be empty.", nameof(payload));
            Index = index;
            this.payload = (byte[])payload.Clone();
        }

        public string TransferId { get; }
        public int Index { get; }
        public int PayloadLength => payload.Length;

        public byte[] GetPayloadCopy()
        {
            return (byte[])payload.Clone();
        }

        internal byte[] TakeInternalPayloadCopy()
        {
            return (byte[])payload.Clone();
        }
    }

    public sealed class TraderPartyTransferCompletion
    {
        private readonly byte[] payload;

        internal TraderPartyTransferCompletion(TraderPartyTransferManifest manifest, byte[] payload)
        {
            Manifest = manifest;
            this.payload = payload;
        }

        public TraderPartyTransferManifest Manifest { get; }
        public int PayloadLength => payload.Length;

        public byte[] GetPayloadCopy()
        {
            return (byte[])payload.Clone();
        }
    }

    /// <summary>
    /// Reassembles independently reliable trader-party transfer records without
    /// depending on game types. Transfer records may arrive in any order. A party
    /// generation identifies one external-party incarnation, while manifest
    /// revisions advance only when its serialized semantic state changes.
    /// </summary>
    public sealed class TraderPartyTransferTracker
    {
        private readonly object sync = new object();
        private readonly TraderPartyTransferLimits limits;
        private readonly Dictionary<string, PendingTransfer> pending =
            new Dictionary<string, PendingTransfer>(StringComparer.Ordinal);
        private readonly Dictionary<string, EventState> events =
            new Dictionary<string, EventState>(StringComparer.Ordinal);
        private readonly HashSet<string> terminalTransferIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> terminalTransferOrder = new List<string>();
        private long bufferedBytes;
        private int bufferedChunks;
        private int memberTombstoneCount;
        private long pendingOrdinal;

        public TraderPartyTransferTracker(TraderPartyTransferLimits limits)
        {
            this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }

        public int PendingTransferCount { get { lock (sync) return pending.Count; } }
        public int TrackedEventCount { get { lock (sync) return events.Count; } }
        public long BufferedByteCount { get { lock (sync) return bufferedBytes; } }
        public int BufferedChunkCount { get { lock (sync) return bufferedChunks; } }
        public int MemberTombstoneCount { get { lock (sync) return memberTombstoneCount; } }

        public TraderPartyTransferRecordResult RecordBegin(TraderPartyTransferManifest manifest, long nowMilliseconds)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            ValidateClock(nowMilliseconds);
            lock (sync)
            {
                ExpireLocked(nowMilliseconds);
                var identity = manifest.ToIdentity();
                var identityResult = AcceptIdentity(manifest.TransferId, identity, out var state);
                if (identityResult != TraderPartyTransferRecordResult.Accepted)
                {
                    if (identityResult == TraderPartyTransferRecordResult.Duplicate
                        || identityResult == TraderPartyTransferRecordResult.Stale)
                    {
                        RemovePending(manifest.TransferId, true);
                    }
                    return identityResult;
                }

                var createResult = GetOrCreatePending(manifest.TransferId, nowMilliseconds, out var transfer);
                if (createResult != TraderPartyTransferRecordResult.Accepted || transfer == null) return createResult;
                if (!BindIdentity(transfer, identity, state!)) return TraderPartyTransferRecordResult.Conflict;
                if (transfer.Manifest != null)
                {
                    return ManifestEquals(transfer.Manifest, manifest)
                        ? TraderPartyTransferRecordResult.Duplicate
                        : TraderPartyTransferRecordResult.Conflict;
                }
                if (!ExistingChunksFitManifest(transfer, identity))
                {
                    RemovePending(manifest.TransferId, true);
                    return TraderPartyTransferRecordResult.Conflict;
                }

                transfer.Manifest = manifest;
                transfer.LastTouchedMilliseconds = nowMilliseconds;
                return ValidateComplete(transfer);
            }
        }

        public TraderPartyTransferRecordResult RecordChunk(TraderPartyTransferChunk chunk, long nowMilliseconds)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            ValidateClock(nowMilliseconds);
            lock (sync)
            {
                ExpireLocked(nowMilliseconds);
                if (terminalTransferIds.Contains(chunk.TransferId)) return TraderPartyTransferRecordResult.Stale;
                if (chunk.Index >= limits.MaxChunksPerTransfer || chunk.PayloadLength > limits.MaxChunkBytes)
                    return TraderPartyTransferRecordResult.CapacityExceeded;

                var createResult = GetOrCreatePending(chunk.TransferId, nowMilliseconds, out var transfer);
                if (createResult != TraderPartyTransferRecordResult.Accepted || transfer == null) return createResult;
                var bytes = chunk.TakeInternalPayloadCopy();
                if (transfer.ReadyPayload != null)
                {
                    return ReadyChunkEquals(transfer, chunk.Index, bytes)
                        ? TraderPartyTransferRecordResult.Duplicate
                        : TraderPartyTransferRecordResult.Conflict;
                }
                if (transfer.Chunks.TryGetValue(chunk.Index, out var existing))
                {
                    return BytesEqual(existing, bytes)
                        ? TraderPartyTransferRecordResult.Duplicate
                        : TraderPartyTransferRecordResult.Conflict;
                }
                if (transfer.Identity != null)
                {
                    if (chunk.Index >= transfer.Identity.ChunkCount
                        || transfer.BufferedBytes + bytes.Length > transfer.Identity.PayloadByteCount)
                    {
                        return TraderPartyTransferRecordResult.Conflict;
                    }
                }
                if (bufferedChunks >= limits.MaxBufferedChunks
                    || bufferedBytes + bytes.Length > limits.MaxBufferedBytes)
                {
                    return TraderPartyTransferRecordResult.CapacityExceeded;
                }

                transfer.Chunks.Add(chunk.Index, bytes);
                transfer.BufferedBytes += bytes.Length;
                transfer.LastTouchedMilliseconds = nowMilliseconds;
                bufferedChunks++;
                bufferedBytes += bytes.Length;
                return ValidateComplete(transfer);
            }
        }

        public TraderPartyTransferRecordResult RecordCommit(TraderPartyTransferCommit commit, long nowMilliseconds)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            ValidateClock(nowMilliseconds);
            lock (sync)
            {
                ExpireLocked(nowMilliseconds);
                var identity = commit.ToIdentity();
                var identityResult = AcceptIdentity(commit.TransferId, identity, out var state);
                if (identityResult != TraderPartyTransferRecordResult.Accepted)
                {
                    if (identityResult == TraderPartyTransferRecordResult.Duplicate
                        || identityResult == TraderPartyTransferRecordResult.Stale)
                    {
                        RemovePending(commit.TransferId, true);
                    }
                    return identityResult;
                }

                var createResult = GetOrCreatePending(commit.TransferId, nowMilliseconds, out var transfer);
                if (createResult != TraderPartyTransferRecordResult.Accepted || transfer == null) return createResult;
                if (!BindIdentity(transfer, identity, state!)) return TraderPartyTransferRecordResult.Conflict;
                if (transfer.Commit != null)
                {
                    return CommitEquals(transfer.Commit, commit)
                        ? TraderPartyTransferRecordResult.Duplicate
                        : TraderPartyTransferRecordResult.Conflict;
                }
                if (!ExistingChunksFitManifest(transfer, identity))
                {
                    RemovePending(commit.TransferId, true);
                    return TraderPartyTransferRecordResult.Conflict;
                }

                transfer.Commit = commit;
                transfer.LastTouchedMilliseconds = nowMilliseconds;
                return ValidateComplete(transfer);
            }
        }

        public TraderPartyTransferRecordResult RecordMemberTombstone(
            string eventId,
            long partyGeneration,
            string networkId,
            long stateRevision)
        {
            eventId = TraderPartyTransferManifest.RequireIdentifier(eventId, nameof(eventId));
            networkId = TraderPartyTransferManifest.RequireIdentifier(networkId, nameof(networkId));
            ValidateGenerationAndRevision(partyGeneration, stateRevision);
            lock (sync)
            {
                var stateResult = GetOrAdvanceEvent(eventId, partyGeneration, out var state);
                if (stateResult != TraderPartyTransferRecordResult.Accepted || state == null) return stateResult;
                if (state.ManifestRevisionHighWater > stateRevision) return TraderPartyTransferRecordResult.Stale;
                if (state.PartyTombstoneRevision >= stateRevision) return TraderPartyTransferRecordResult.Stale;
                if (state.MemberTombstones.TryGetValue(networkId, out var existingRevision))
                {
                    if (stateRevision < existingRevision) return TraderPartyTransferRecordResult.Stale;
                    if (stateRevision == existingRevision) return TraderPartyTransferRecordResult.Duplicate;
                    state.MemberTombstones[networkId] = stateRevision;
                }
                else
                {
                    if (memberTombstoneCount >= limits.MaxMemberTombstones)
                        return TraderPartyTransferRecordResult.CapacityExceeded;
                    state.MemberTombstones.Add(networkId, stateRevision);
                    memberTombstoneCount++;
                }

                RemovePendingDominatedByMemberTombstone(eventId, partyGeneration, networkId, stateRevision);
                return TraderPartyTransferRecordResult.Accepted;
            }
        }

        public TraderPartyTransferRecordResult RecordPartyTombstone(
            string eventId,
            long partyGeneration,
            long stateRevision)
        {
            eventId = TraderPartyTransferManifest.RequireIdentifier(eventId, nameof(eventId));
            ValidateGenerationAndRevision(partyGeneration, stateRevision);
            lock (sync)
            {
                var stateResult = GetOrAdvanceEvent(eventId, partyGeneration, out var state);
                if (stateResult != TraderPartyTransferRecordResult.Accepted || state == null) return stateResult;
                if (state.ManifestRevisionHighWater > stateRevision) return TraderPartyTransferRecordResult.Stale;
                if (state.PartyTombstoneRevision > stateRevision) return TraderPartyTransferRecordResult.Stale;
                if (state.PartyTombstoneRevision == stateRevision) return TraderPartyTransferRecordResult.Duplicate;

                state.PartyTombstoneRevision = stateRevision;
                memberTombstoneCount -= state.MemberTombstones.Count;
                state.MemberTombstones.Clear();
                RemovePendingForParty(eventId, partyGeneration);
                return TraderPartyTransferRecordResult.Accepted;
            }
        }

        public bool IsMemberTombstoned(
            string eventId,
            long partyGeneration,
            string networkId,
            long stateRevision)
        {
            if (string.IsNullOrWhiteSpace(eventId)
                || string.IsNullOrWhiteSpace(networkId)
                || partyGeneration <= 0L
                || stateRevision <= 0L) return false;
            lock (sync)
            {
                if (!events.TryGetValue(eventId, out var state)) return false;
                if (partyGeneration < state.PartyGenerationHighWater) return true;
                if (partyGeneration > state.PartyGenerationHighWater) return false;
                if (state.PartyTombstoneRevision >= stateRevision) return true;
                return state.MemberTombstones.TryGetValue(networkId, out var tombstoneRevision)
                    && tombstoneRevision >= stateRevision;
            }
        }

        public bool TryTakeComplete(string transferId, out TraderPartyTransferCompletion? completion)
        {
            if (string.IsNullOrWhiteSpace(transferId))
            {
                completion = null;
                return false;
            }
            lock (sync)
            {
                if (!pending.TryGetValue(transferId, out var transfer)
                    || transfer.ReadyPayload == null
                    || transfer.Manifest == null
                    || transfer.Identity == null)
                {
                    completion = null;
                    return false;
                }

                var state = events[transfer.Identity.EventId];
                if (state.AppliedManifestRevision >= transfer.Identity.ManifestRevision)
                {
                    RemovePending(transferId, true);
                    completion = null;
                    return false;
                }

                var payload = transfer.ReadyPayload;
                completion = new TraderPartyTransferCompletion(transfer.Manifest, payload);
                state.AppliedManifestRevision = transfer.Identity.ManifestRevision;
                state.AppliedManifestSha256 = transfer.Identity.PayloadSha256;
                RemovePending(transferId, true);
                return true;
            }
        }

        public int Expire(long nowMilliseconds)
        {
            ValidateClock(nowMilliseconds);
            lock (sync) return ExpireLocked(nowMilliseconds);
        }

        public void Reset()
        {
            lock (sync)
            {
                pending.Clear();
                events.Clear();
                terminalTransferIds.Clear();
                terminalTransferOrder.Clear();
                bufferedBytes = 0L;
                bufferedChunks = 0;
                memberTombstoneCount = 0;
                pendingOrdinal = 0L;
            }
        }

        private TraderPartyTransferRecordResult AcceptIdentity(
            string transferId,
            TransferIdentity identity,
            out EventState? state)
        {
            if (identity.ChunkCount > limits.MaxChunksPerTransfer
                || identity.PayloadByteCount > limits.MaxBufferedBytes
                || identity.PayloadByteCount > (long)identity.ChunkCount * limits.MaxChunkBytes)
            {
                state = null;
                return TraderPartyTransferRecordResult.CapacityExceeded;
            }

            var stateResult = GetOrAdvanceEvent(identity.EventId, identity.PartyGeneration, out state);
            if (stateResult != TraderPartyTransferRecordResult.Accepted || state == null) return stateResult;
            if (state.PartyTombstoneRevision > 0L)
            {
                return identity.ManifestRevision <= state.PartyTombstoneRevision
                    ? TraderPartyTransferRecordResult.Stale
                    : TraderPartyTransferRecordResult.Conflict;
            }
            foreach (var memberId in identity.MemberNetworkIds)
            {
                if (!state.MemberTombstones.TryGetValue(memberId, out var tombstoneRevision)) continue;
                return identity.ManifestRevision <= tombstoneRevision
                    ? TraderPartyTransferRecordResult.Stale
                    : TraderPartyTransferRecordResult.Conflict;
            }

            if (identity.ManifestRevision < state.ManifestRevisionHighWater)
                return TraderPartyTransferRecordResult.Stale;
            if (identity.ManifestRevision == state.ManifestRevisionHighWater)
            {
                if (state.AcceptedIdentity == null || !state.AcceptedIdentity.SemanticallyEquals(identity))
                    return TraderPartyTransferRecordResult.Conflict;
                if (state.AppliedManifestRevision >= identity.ManifestRevision)
                    return TraderPartyTransferRecordResult.Duplicate;
                if (!string.IsNullOrEmpty(state.ActiveTransferId)
                    && !string.Equals(state.ActiveTransferId, transferId, StringComparison.Ordinal)
                    && pending.ContainsKey(state.ActiveTransferId))
                {
                    return TraderPartyTransferRecordResult.Duplicate;
                }
                state.ActiveTransferId = transferId;
                return TraderPartyTransferRecordResult.Accepted;
            }

            RemovePendingOlderRevision(identity.EventId, identity.PartyGeneration, identity.ManifestRevision);
            state.ManifestRevisionHighWater = identity.ManifestRevision;
            state.AcceptedIdentity = identity;
            state.ActiveTransferId = transferId;
            return TraderPartyTransferRecordResult.Accepted;
        }

        private TraderPartyTransferRecordResult GetOrAdvanceEvent(
            string eventId,
            long partyGeneration,
            out EventState? state)
        {
            if (!events.TryGetValue(eventId, out state))
            {
                if (events.Count >= limits.MaxTrackedEvents)
                    return TraderPartyTransferRecordResult.CapacityExceeded;
                state = new EventState(eventId, partyGeneration);
                events.Add(eventId, state);
                return TraderPartyTransferRecordResult.Accepted;
            }
            if (partyGeneration < state.PartyGenerationHighWater)
                return TraderPartyTransferRecordResult.Stale;
            if (partyGeneration == state.PartyGenerationHighWater)
                return TraderPartyTransferRecordResult.Accepted;

            RemovePendingForEvent(eventId);
            memberTombstoneCount -= state.MemberTombstones.Count;
            state.AdvancePartyGeneration(partyGeneration);
            return TraderPartyTransferRecordResult.Accepted;
        }

        private TraderPartyTransferRecordResult GetOrCreatePending(
            string transferId,
            long nowMilliseconds,
            out PendingTransfer? transfer)
        {
            if (terminalTransferIds.Contains(transferId))
            {
                transfer = null;
                return TraderPartyTransferRecordResult.Stale;
            }
            if (pending.TryGetValue(transferId, out transfer))
                return TraderPartyTransferRecordResult.Accepted;
            if (pending.Count >= limits.MaxPendingTransfers)
            {
                transfer = null;
                return TraderPartyTransferRecordResult.CapacityExceeded;
            }
            transfer = new PendingTransfer(transferId, ++pendingOrdinal, nowMilliseconds);
            pending.Add(transferId, transfer);
            return TraderPartyTransferRecordResult.Accepted;
        }

        private static bool BindIdentity(PendingTransfer transfer, TransferIdentity identity, EventState state)
        {
            if (transfer.Identity != null && !transfer.Identity.SemanticallyEquals(identity)) return false;
            transfer.Identity = identity;
            state.ActiveTransferId = transfer.TransferId;
            return true;
        }

        private TraderPartyTransferRecordResult ValidateComplete(PendingTransfer transfer)
        {
            if (transfer.ReadyPayload != null) return TraderPartyTransferRecordResult.Accepted;
            if (transfer.Manifest == null || transfer.Commit == null || transfer.Identity == null)
                return TraderPartyTransferRecordResult.Accepted;
            if (!transfer.Manifest.ToIdentity().SemanticallyEquals(transfer.Commit.ToIdentity()))
                return TraderPartyTransferRecordResult.Conflict;
            if (transfer.Chunks.Count < transfer.Identity.ChunkCount)
                return TraderPartyTransferRecordResult.Accepted;
            if (transfer.Chunks.Count != transfer.Identity.ChunkCount
                || transfer.BufferedBytes != transfer.Identity.PayloadByteCount)
            {
                RemovePending(transfer.TransferId, true);
                return TraderPartyTransferRecordResult.Conflict;
            }

            var payload = new byte[transfer.Identity.PayloadByteCount];
            var lengths = new int[transfer.Identity.ChunkCount];
            var offset = 0;
            for (var index = 0; index < transfer.Identity.ChunkCount; index++)
            {
                if (!transfer.Chunks.TryGetValue(index, out var chunk))
                {
                    RemovePending(transfer.TransferId, true);
                    return TraderPartyTransferRecordResult.Conflict;
                }
                Buffer.BlockCopy(chunk, 0, payload, offset, chunk.Length);
                lengths[index] = chunk.Length;
                offset += chunk.Length;
            }
            if (!string.Equals(ComputeSha256(payload), transfer.Identity.PayloadSha256, StringComparison.Ordinal))
            {
                RemovePending(transfer.TransferId, true);
                return TraderPartyTransferRecordResult.Conflict;
            }

            bufferedChunks -= transfer.Chunks.Count;
            transfer.Chunks.Clear();
            transfer.ReadyPayload = payload;
            transfer.ReadyChunkLengths = lengths;
            return TraderPartyTransferRecordResult.Accepted;
        }

        private bool ExistingChunksFitManifest(PendingTransfer transfer, TransferIdentity identity)
        {
            if (transfer.BufferedBytes > identity.PayloadByteCount || transfer.Chunks.Count > identity.ChunkCount)
                return false;
            foreach (var pair in transfer.Chunks)
                if (pair.Key < 0 || pair.Key >= identity.ChunkCount) return false;
            return true;
        }

        private static bool ReadyChunkEquals(PendingTransfer transfer, int index, byte[] candidate)
        {
            if (transfer.ReadyPayload == null
                || transfer.ReadyChunkLengths == null
                || index < 0
                || index >= transfer.ReadyChunkLengths.Length
                || transfer.ReadyChunkLengths[index] != candidate.Length) return false;
            var offset = 0;
            for (var i = 0; i < index; i++) offset += transfer.ReadyChunkLengths[i];
            for (var i = 0; i < candidate.Length; i++)
                if (transfer.ReadyPayload[offset + i] != candidate[i]) return false;
            return true;
        }

        private int ExpireLocked(long nowMilliseconds)
        {
            var remove = new List<string>();
            foreach (var pair in pending)
            {
                var touched = pair.Value.LastTouchedMilliseconds;
                if (nowMilliseconds >= touched
                    && nowMilliseconds - touched >= limits.TransferExpiryMilliseconds)
                {
                    remove.Add(pair.Key);
                }
            }
            for (var i = 0; i < remove.Count; i++) RemovePending(remove[i], true);
            return remove.Count;
        }

        private void RemovePendingOlderRevision(string eventId, long partyGeneration, long manifestRevision)
        {
            var remove = new List<string>();
            foreach (var pair in pending)
            {
                var identity = pair.Value.Identity;
                if (identity != null
                    && string.Equals(identity.EventId, eventId, StringComparison.Ordinal)
                    && identity.PartyGeneration == partyGeneration
                    && identity.ManifestRevision < manifestRevision)
                {
                    remove.Add(pair.Key);
                }
            }
            for (var i = 0; i < remove.Count; i++) RemovePending(remove[i], true);
        }

        private void RemovePendingDominatedByMemberTombstone(
            string eventId,
            long partyGeneration,
            string networkId,
            long stateRevision)
        {
            var remove = new List<string>();
            foreach (var pair in pending)
            {
                var identity = pair.Value.Identity;
                if (identity != null
                    && string.Equals(identity.EventId, eventId, StringComparison.Ordinal)
                    && identity.PartyGeneration == partyGeneration
                    && identity.ManifestRevision <= stateRevision
                    && identity.ContainsMember(networkId))
                {
                    remove.Add(pair.Key);
                }
            }
            for (var i = 0; i < remove.Count; i++) RemovePending(remove[i], true);
        }

        private void RemovePendingForParty(string eventId, long partyGeneration)
        {
            var remove = new List<string>();
            foreach (var pair in pending)
            {
                var identity = pair.Value.Identity;
                if (identity != null
                    && string.Equals(identity.EventId, eventId, StringComparison.Ordinal)
                    && identity.PartyGeneration == partyGeneration)
                {
                    remove.Add(pair.Key);
                }
            }
            for (var i = 0; i < remove.Count; i++) RemovePending(remove[i], true);
        }

        private void RemovePendingForEvent(string eventId)
        {
            var remove = new List<string>();
            foreach (var pair in pending)
            {
                var identity = pair.Value.Identity;
                if (identity != null && string.Equals(identity.EventId, eventId, StringComparison.Ordinal))
                    remove.Add(pair.Key);
            }
            for (var i = 0; i < remove.Count; i++) RemovePending(remove[i], true);
        }

        private void RemovePending(string transferId, bool rememberTerminal)
        {
            if (!pending.TryGetValue(transferId, out var transfer))
            {
                if (rememberTerminal) RememberTerminalTransfer(transferId);
                return;
            }
            pending.Remove(transferId);
            bufferedBytes -= transfer.BufferedBytes;
            bufferedChunks -= transfer.Chunks.Count;
            if (transfer.Identity != null
                && events.TryGetValue(transfer.Identity.EventId, out var state)
                && string.Equals(state.ActiveTransferId, transferId, StringComparison.Ordinal))
            {
                state.ActiveTransferId = string.Empty;
            }
            if (rememberTerminal) RememberTerminalTransfer(transferId);
        }

        private void RememberTerminalTransfer(string transferId)
        {
            if (string.IsNullOrWhiteSpace(transferId) || !terminalTransferIds.Add(transferId)) return;
            terminalTransferOrder.Add(transferId);
            while (terminalTransferOrder.Count > limits.MaxTerminalTransferIds)
            {
                var oldest = terminalTransferOrder[0];
                terminalTransferOrder.RemoveAt(0);
                terminalTransferIds.Remove(oldest);
            }
        }

        private static string ComputeSha256(byte[] payload)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(payload);
                var characters = new char[hash.Length * 2];
                const string hex = "0123456789abcdef";
                for (var i = 0; i < hash.Length; i++)
                {
                    characters[i * 2] = hex[hash[i] >> 4];
                    characters[(i * 2) + 1] = hex[hash[i] & 0x0f];
                }
                return new string(characters);
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static bool ManifestEquals(TraderPartyTransferManifest left, TraderPartyTransferManifest right)
        {
            return string.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal)
                && left.ToIdentity().SemanticallyEquals(right.ToIdentity());
        }

        private static bool CommitEquals(TraderPartyTransferCommit left, TraderPartyTransferCommit right)
        {
            return string.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal)
                && left.ToIdentity().SemanticallyEquals(right.ToIdentity());
        }

        private static void ValidateClock(long nowMilliseconds)
        {
            if (nowMilliseconds < 0L) throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
        }

        private static void ValidateGenerationAndRevision(long partyGeneration, long stateRevision)
        {
            if (partyGeneration <= 0L) throw new ArgumentOutOfRangeException(nameof(partyGeneration));
            if (stateRevision <= 0L) throw new ArgumentOutOfRangeException(nameof(stateRevision));
        }

        internal sealed class TransferIdentity
        {
            private readonly string[] memberNetworkIds;

            public TransferIdentity(
                string eventId,
                long partyGeneration,
                long manifestRevision,
                int chunkCount,
                int payloadByteCount,
                string[] memberNetworkIds,
                string payloadSha256)
            {
                EventId = eventId;
                PartyGeneration = partyGeneration;
                ManifestRevision = manifestRevision;
                ChunkCount = chunkCount;
                PayloadByteCount = payloadByteCount;
                this.memberNetworkIds = (string[])memberNetworkIds.Clone();
                PayloadSha256 = payloadSha256;
            }

            public string EventId { get; }
            public long PartyGeneration { get; }
            public long ManifestRevision { get; }
            public int ChunkCount { get; }
            public int PayloadByteCount { get; }
            public int AgentCount => memberNetworkIds.Length;
            public string PayloadSha256 { get; }
            public IEnumerable<string> MemberNetworkIds => memberNetworkIds;

            public bool ContainsMember(string networkId)
            {
                for (var i = 0; i < memberNetworkIds.Length; i++)
                    if (string.Equals(memberNetworkIds[i], networkId, StringComparison.Ordinal)) return true;
                return false;
            }

            public bool SemanticallyEquals(TransferIdentity other)
            {
                if (!string.Equals(EventId, other.EventId, StringComparison.Ordinal)
                    || PartyGeneration != other.PartyGeneration
                    || ManifestRevision != other.ManifestRevision
                    || ChunkCount != other.ChunkCount
                    || PayloadByteCount != other.PayloadByteCount
                    || !string.Equals(PayloadSha256, other.PayloadSha256, StringComparison.Ordinal)
                    || memberNetworkIds.Length != other.memberNetworkIds.Length) return false;
                for (var i = 0; i < memberNetworkIds.Length; i++)
                    if (!string.Equals(memberNetworkIds[i], other.memberNetworkIds[i], StringComparison.Ordinal)) return false;
                return true;
            }
        }

        private sealed class PendingTransfer
        {
            public PendingTransfer(string transferId, long ordinal, long nowMilliseconds)
            {
                TransferId = transferId;
                Ordinal = ordinal;
                LastTouchedMilliseconds = nowMilliseconds;
            }

            public string TransferId { get; }
            public long Ordinal { get; }
            public long LastTouchedMilliseconds;
            public long BufferedBytes;
            public TransferIdentity? Identity;
            public TraderPartyTransferManifest? Manifest;
            public TraderPartyTransferCommit? Commit;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
            public byte[]? ReadyPayload;
            public int[]? ReadyChunkLengths;
        }

        private sealed class EventState
        {
            public EventState(string eventId, long partyGeneration)
            {
                EventId = eventId;
                PartyGenerationHighWater = partyGeneration;
                ActiveTransferId = string.Empty;
                AppliedManifestSha256 = string.Empty;
            }

            public string EventId { get; }
            public long PartyGenerationHighWater;
            public long ManifestRevisionHighWater;
            public TransferIdentity? AcceptedIdentity;
            public string ActiveTransferId;
            public long AppliedManifestRevision;
            public string AppliedManifestSha256;
            public long PartyTombstoneRevision;
            public readonly Dictionary<string, long> MemberTombstones =
                new Dictionary<string, long>(StringComparer.Ordinal);

            public void AdvancePartyGeneration(long partyGeneration)
            {
                PartyGenerationHighWater = partyGeneration;
                ManifestRevisionHighWater = 0L;
                AcceptedIdentity = null;
                ActiveTransferId = string.Empty;
                AppliedManifestRevision = 0L;
                AppliedManifestSha256 = string.Empty;
                PartyTombstoneRevision = 0L;
                MemberTombstones.Clear();
            }
        }
    }
}
