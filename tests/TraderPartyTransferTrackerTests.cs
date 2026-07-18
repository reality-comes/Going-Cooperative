using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using GoingCooperative.Core;

internal static class TraderPartyTransferTrackerTests
{
    private static int failures;

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (Equals(expected, actual)) return;
        Console.Error.WriteLine("FAIL " + name + " expected=" + expected + " actual=" + actual);
        failures++;
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string name)
    {
        if (expected.Length == actual.Length)
        {
            var equal = true;
            for (var i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) equal = false;
            if (equal) return;
        }
        Console.Error.WriteLine("FAIL " + name + " byte arrays differ");
        failures++;
    }

    public static int Main()
    {
        EveryRecordOrderCompletesExactlyOnce();
        DuplicateAndConflictingRecordsAreDistinct();
        DuplicateBeginWithNewTransferIdPreservesActiveTransfer();
        HashCountAndSizeAreValidatedAtomically();
        GenerationAndManifestRevisionHighWaterAreMonotonic();
        MemberAndPartyTombstonesDominateStaleTransfers();
        MembershipTombstoneBootstrapRestartConvergesInBothOrders();
        RuntimePolicySeparatesDetachmentAndBootstrapRefresh();
        PendingStorageIsStrictlyBounded();
        IdleTransfersExpireWithoutDuplicateKeepAlive();
        InputsAndCompletionAreImmutableCopies();

        if (failures != 0) return 1;
        Console.WriteLine("PASS TraderPartyTransferTrackerTests");
        return 0;
    }

    private static void EveryRecordOrderCompletesExactlyOnce()
    {
        var records = new[] { 0, 1, 2, 3 };
        VisitPermutations(records, 0, order =>
        {
            var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
            var tracker = NewTracker();
            for (var i = 0; i < order.Length; i++)
            {
                TraderPartyTransferRecordResult result;
                switch (order[i])
                {
                    case 0:
                        result = tracker.RecordBegin(Manifest("transfer-a", "event-a", 1, 1, payload, 2), i);
                        break;
                    case 1:
                        result = tracker.RecordChunk(new TraderPartyTransferChunk("transfer-a", 0, Slice(payload, 0, 3)), i);
                        break;
                    case 2:
                        result = tracker.RecordChunk(new TraderPartyTransferChunk("transfer-a", 1, Slice(payload, 3, 3)), i);
                        break;
                    default:
                        result = tracker.RecordCommit(Commit("transfer-a", "event-a", 1, 1, payload, 2), i);
                        break;
                }
                Equal(TraderPartyTransferRecordResult.Accepted, result,
                    "permutation record accepted order=" + string.Join(",", order));
            }

            Equal(true, tracker.TryTakeComplete("transfer-a", out var completion),
                "permutation becomes complete order=" + string.Join(",", order));
            BytesEqual(payload, completion?.GetPayloadCopy() ?? Array.Empty<byte>(),
                "permutation preserves payload order=" + string.Join(",", order));
            Equal(false, tracker.TryTakeComplete("transfer-a", out _),
                "completion can be taken once order=" + string.Join(",", order));
            Equal(0, tracker.PendingTransferCount, "taken transfer releases buffer");
            Equal(0L, tracker.BufferedByteCount, "taken transfer releases bytes");
        });
    }

    private static void DuplicateAndConflictingRecordsAreDistinct()
    {
        var payload = new byte[] { 10, 11, 12, 13 };
        var tracker = NewTracker();
        var manifest = Manifest("transfer-b", "event-b", 1, 1, payload, 1);
        Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordBegin(manifest, 0), "first manifest accepted");
        Equal(TraderPartyTransferRecordResult.Duplicate, tracker.RecordBegin(manifest, 1), "exact manifest duplicate");
        Equal(TraderPartyTransferRecordResult.Conflict,
            tracker.RecordBegin(new TraderPartyTransferManifest(
                "transfer-b", "event-b", 1, 1, 1, payload.Length,
                new[] { "event-b:1:trader:0", "event-b:1:guard:0" }, Hash(new byte[] { 1, 2, 3, 4 })), 2),
            "same revision with another hash conflicts");

        var first = new TraderPartyTransferChunk("transfer-b", 0, payload);
        Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordChunk(first, 3), "first chunk accepted");
        Equal(TraderPartyTransferRecordResult.Duplicate, tracker.RecordChunk(first, 4), "exact chunk duplicate");
        Equal(TraderPartyTransferRecordResult.Conflict,
            tracker.RecordChunk(new TraderPartyTransferChunk("transfer-b", 0, new byte[] { 10, 11, 12, 99 }), 5),
            "same index with different bytes conflicts");

        var commit = Commit("transfer-b", "event-b", 1, 1, payload, 1);
        Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordCommit(commit, 6), "first commit accepted");
        Equal(TraderPartyTransferRecordResult.Duplicate, tracker.RecordCommit(commit, 7), "exact commit duplicate");
        Equal(true, tracker.TryTakeComplete("transfer-b", out _), "duplicate traffic still completes once");
        Equal(TraderPartyTransferRecordResult.Duplicate,
            tracker.RecordBegin(Manifest("replay-b", "event-b", 1, 1, payload, 1), 8),
            "applied revision/hash replay is semantic duplicate");
    }

    private static void DuplicateBeginWithNewTransferIdPreservesActiveTransfer()
    {
        var payload = new byte[] { 14, 15, 16, 17, 18, 19 };
        var tracker = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordBegin(Manifest("active-transfer", "event-active", 1, 1, payload, 2), 0),
            "active transfer Begin accepted");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordChunk(new TraderPartyTransferChunk("active-transfer", 0, Slice(payload, 0, 3)), 1),
            "active transfer retains its first chunk");

        Equal(TraderPartyTransferRecordResult.Duplicate,
            tracker.RecordBegin(Manifest("duplicate-transfer", "event-active", 1, 1, payload, 2), 2),
            "same revision and identity under a new transfer ID is a duplicate");
        Equal(1, tracker.PendingTransferCount,
            "duplicate Begin under a new ID does not replace the active transfer");
        Equal(1, tracker.BufferedChunkCount,
            "duplicate Begin under a new ID does not discard active chunks");
        Equal(3L, tracker.BufferedByteCount,
            "duplicate Begin under a new ID does not discard active bytes");
        Equal(TraderPartyTransferRecordResult.Stale,
            tracker.RecordChunk(new TraderPartyTransferChunk("duplicate-transfer", 0, Slice(payload, 0, 3)), 3),
            "duplicate transfer ID is terminal and cannot steal later chunks");

        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordCommit(Commit("active-transfer", "event-active", 1, 1, payload, 2), 4),
            "original active transfer still accepts its Commit");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordChunk(new TraderPartyTransferChunk("active-transfer", 1, Slice(payload, 3, 3)), 5),
            "original active transfer still accepts its remaining chunk");
        Equal(true, tracker.TryTakeComplete("active-transfer", out var completion),
            "original active transfer completes after duplicate Begin");
        BytesEqual(payload, completion?.GetPayloadCopy() ?? Array.Empty<byte>(),
            "original active transfer payload survives duplicate Begin");
    }

    private static void HashCountAndSizeAreValidatedAtomically()
    {
        var payload = new byte[] { 20, 21, 22, 23 };

        var wrongCount = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted,
            wrongCount.RecordBegin(Manifest("count", "event-count", 1, 1, payload, 1), 0),
            "count manifest accepted");
        Equal(TraderPartyTransferRecordResult.Conflict,
            wrongCount.RecordCommit(new TraderPartyTransferCommit(
                "count", "event-count", 1, 1, 2, payload.Length,
                Members("event-count", 1), Hash(payload)), 1),
            "commit chunk count must equal manifest");

        var wrongSize = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted,
            wrongSize.RecordBegin(Manifest("size", "event-size", 1, 1, payload, 1), 0),
            "size manifest accepted");
        Equal(TraderPartyTransferRecordResult.Conflict,
            wrongSize.RecordCommit(new TraderPartyTransferCommit(
                "size", "event-size", 1, 1, 1, payload.Length + 1,
                Members("event-size", 1), Hash(payload)), 1),
            "commit payload size must equal manifest");

        var wrongMembers = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted,
            wrongMembers.RecordBegin(Manifest("members", "event-members", 1, 1, payload, 1), 0),
            "member manifest accepted");
        Equal(TraderPartyTransferRecordResult.Conflict,
            wrongMembers.RecordCommit(new TraderPartyTransferCommit(
                "members", "event-members", 1, 1, 1, payload.Length,
                new[] { "event-members:1:trader:0" }, Hash(payload)), 1),
            "commit member count and identities must equal manifest");

        var wrongHash = NewTracker();
        var advertised = new byte[] { 30, 31, 32, 33 };
        Equal(TraderPartyTransferRecordResult.Accepted,
            wrongHash.RecordCommit(Commit("hash", "event-hash", 1, 1, advertised, 1), 0),
            "commit may arrive before data and Begin");
        Equal(TraderPartyTransferRecordResult.Accepted,
            wrongHash.RecordBegin(Manifest("hash", "event-hash", 1, 1, advertised, 1), 1),
            "matching Begin follows early commit");
        Equal(TraderPartyTransferRecordResult.Conflict,
            wrongHash.RecordChunk(new TraderPartyTransferChunk("hash", 0, new byte[] { 30, 31, 32, 99 }), 2),
            "actual assembled SHA-256 must equal advertised and supplied hash");
        Equal(false, wrongHash.TryTakeComplete("hash", out _), "hash failure cannot expose partial completion");
        Equal(0, wrongHash.PendingTransferCount, "hash failure discards transfer atomically");
        Equal(0L, wrongHash.BufferedByteCount, "hash failure releases bytes");
    }

    private static void GenerationAndManifestRevisionHighWaterAreMonotonic()
    {
        var payload = new byte[] { 40, 41, 42 };
        var tracker = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordBegin(Manifest("g2-r1", "event-generation", 2, 1, payload, 1), 0),
            "new party generation accepted");
        Equal(TraderPartyTransferRecordResult.Stale,
            tracker.RecordBegin(Manifest("g1-r9", "event-generation", 1, 9, payload, 1), 1),
            "older party generation is terminal stale");
        Equal(TraderPartyTransferRecordResult.Conflict,
            tracker.RecordBegin(new TraderPartyTransferManifest(
                "g2-r1-conflict", "event-generation", 2, 1, 1, payload.Length,
                Members("event-generation", 2), Hash(new byte[] { 1, 1, 1 })), 2),
            "same party generation and revision cannot change immutable hash");

        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordBegin(Manifest("g2-r2", "event-generation", 2, 2, payload, 1), 3),
            "higher semantic manifest revision accepted");
        Equal(TraderPartyTransferRecordResult.Stale,
            tracker.RecordCommit(Commit("g2-r1", "event-generation", 2, 1, payload, 1), 4),
            "older manifest revision becomes stale");
        Equal(1, tracker.PendingTransferCount, "higher revision evicts older incomplete transfer");

        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordCommit(Commit("g2-r2", "event-generation", 2, 2, payload, 1), 5),
            "higher revision commit accepted");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordChunk(new TraderPartyTransferChunk("g2-r2", 0, payload), 6),
            "higher revision data accepted");
        Equal(true, tracker.TryTakeComplete("g2-r2", out _), "higher revision applies");
        Equal(TraderPartyTransferRecordResult.Duplicate,
            tracker.RecordCommit(Commit("replay-r2", "event-generation", 2, 2, payload, 1), 7),
            "same applied revision/hash is idempotent across transfer IDs");
    }

    private static void MemberAndPartyTombstonesDominateStaleTransfers()
    {
        var payload = new byte[] { 50, 51 };
        var tracker = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordBegin(Manifest("member-old", "event-member", 1, 1, payload, 1), 0),
            "pre-tombstone member manifest accepted");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordMemberTombstone("event-member", 1, "event-member:1:trader:0", 2),
            "member tombstone accepted");
        Equal(0, tracker.PendingTransferCount, "member tombstone removes dominated pending manifest");
        Equal(true, tracker.IsMemberTombstoned(
            "event-member", 1, "event-member:1:trader:0", 2), "member tombstone query observes high-water");
        Equal(TraderPartyTransferRecordResult.Stale,
            tracker.RecordBegin(Manifest("member-stale", "event-member", 1, 1, payload, 1), 1),
            "older manifest containing tombstoned member is stale");
        Equal(TraderPartyTransferRecordResult.Conflict,
            tracker.RecordBegin(Manifest("member-resurrect", "event-member", 1, 3, payload, 1), 2),
            "newer manifest cannot resurrect same terminal member ID");

        var remainingMember = new[] { "event-member:1:guard:0" };
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordBegin(new TraderPartyTransferManifest(
                "member-new", "event-member", 1, 3, 1, payload.Length, remainingMember, Hash(payload)), 3),
            "newer manifest that omits tombstoned member is accepted");

        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordPartyTombstone("event-party", 1, 2), "party tombstone accepted before manifest");
        Equal(TraderPartyTransferRecordResult.Duplicate,
            tracker.RecordPartyTombstone("event-party", 1, 2), "exact party tombstone duplicate");
        Equal(TraderPartyTransferRecordResult.Stale,
            tracker.RecordBegin(Manifest("party-stale", "event-party", 1, 1, payload, 1), 4),
            "party tombstone dominates older manifest revision");
        Equal(TraderPartyTransferRecordResult.Conflict,
            tracker.RecordBegin(Manifest("party-resurrect", "event-party", 1, 3, payload, 1), 5),
            "retired party incarnation cannot be resurrected by later revision");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordBegin(Manifest("party-next", "event-party", 2, 1, payload, 1), 6),
            "new party incarnation advances beyond tombstone");
    }

    private static void MembershipTombstoneBootstrapRestartConvergesInBothOrders()
    {
        foreach (var tombstoneFirst in new[] { false, true })
        {
            var tracker = NewTracker();
            var eventId = tombstoneFirst ? "restart-tombstone-first" : "restart-begin-first";
            var removedMember = eventId + ":1:guard:0";
            var survivingMembers = new[] { eventId + ":1:trader:0" };
            var oldPayload = new byte[] { 70, 71 };
            var revisedPayload = new byte[] { 72, 73, 74 };
            var oldManifest = Manifest("old-" + eventId, eventId, 1, 1, oldPayload, 1);

            if (!tombstoneFirst)
                Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordBegin(oldManifest, 0),
                    "old Begin accepted before membership tombstone");
            Equal(TraderPartyTransferRecordResult.Accepted,
                tracker.RecordMemberTombstone(eventId, 1, removedMember, 3),
                "membership tombstone accepts before revised bootstrap order=" + tombstoneFirst);
            if (tombstoneFirst)
                Equal(TraderPartyTransferRecordResult.Stale, tracker.RecordBegin(oldManifest, 1),
                    "delayed old Begin is stale after tombstone");
            else
                Equal(0, tracker.PendingTransferCount,
                    "tombstone cancels already-started old bootstrap");

            var revisedManifest = new TraderPartyTransferManifest(
                "revised-" + eventId, eventId, 1, 2, 1, revisedPayload.Length,
                survivingMembers, Hash(revisedPayload));
            var revisedCommit = new TraderPartyTransferCommit(
                "revised-" + eventId, eventId, 1, 2, 1, revisedPayload.Length,
                survivingMembers, Hash(revisedPayload));
            Equal(TraderPartyTransferRecordResult.Accepted,
                tracker.RecordBegin(revisedManifest, 2),
                "revised manifest omitting tombstoned member is accepted order=" + tombstoneFirst);
            Equal(TraderPartyTransferRecordResult.Accepted,
                tracker.RecordChunk(new TraderPartyTransferChunk("revised-" + eventId, 0, revisedPayload), 3),
                "revised survivor payload accepted order=" + tombstoneFirst);
            Equal(TraderPartyTransferRecordResult.Accepted,
                tracker.RecordCommit(revisedCommit, 4),
                "revised survivor commit accepted order=" + tombstoneFirst);
            Equal(true, tracker.TryTakeComplete("revised-" + eventId, out var completion),
                "revised surviving roster converges order=" + tombstoneFirst);
            Equal(1, completion?.Manifest.MemberNetworkIds.Count ?? 0,
                "revised roster excludes removed member order=" + tombstoneFirst);
            Equal(survivingMembers[0], completion?.Manifest.MemberNetworkIds[0] ?? string.Empty,
                "revised roster retains the surviving trader order=" + tombstoneFirst);
        }
    }

    private static void RuntimePolicySeparatesDetachmentAndBootstrapRefresh()
    {
        Equal(false,
            TraderPartyRuntimePolicy.IsDetached(TraderPartyOwnerClassification.SameEventTrader),
            "same-event trader ownership remains attached");
        Equal(true,
            TraderPartyRuntimePolicy.IsDetached(TraderPartyOwnerClassification.GenericWorldOwner),
            "ordinary settlement ownership is detached from merchant event");
        Equal(true,
            TraderPartyRuntimePolicy.IsDetached(TraderPartyOwnerClassification.None),
            "ownerless purchased stock is detached from merchant event");

        Equal(TraderPartyBootstrapDisposition.SupersedeUnappliedBootstrap,
            TraderPartyRuntimePolicy.DecideBootstrapDisposition(false, true, true, false),
            "membership removal supersedes an unapplied initial bootstrap");
        Equal(TraderPartyBootstrapDisposition.CurrentPeerSemanticOnly,
            TraderPartyRuntimePolicy.DecideBootstrapDisposition(true, true, true, false),
            "membership removal after bootstrap remains semantic-only for current peer");
        Equal(TraderPartyBootstrapDisposition.RebuildForNewPeer,
            TraderPartyRuntimePolicy.DecideBootstrapDisposition(true, false, true, true),
            "dirty bootstrap rebuilds for a newly connected peer");
        Equal(TraderPartyBootstrapDisposition.CurrentPeerSemanticOnly,
            TraderPartyRuntimePolicy.DecideBootstrapDisposition(true, false, true, false),
            "ordinary live trade never hot-merges FV into current peer");
    }

    private static void PendingStorageIsStrictlyBounded()
    {
        var limits = new TraderPartyTransferLimits(
            2, 2, 2, 3, 2, 5L, 1, 2, 10L);
        var tracker = new TraderPartyTransferTracker(limits);

        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordChunk(new TraderPartyTransferChunk("orphan-a", 0, new byte[] { 1, 2, 3 }), 0),
            "first orphan consumes bounded storage");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordChunk(new TraderPartyTransferChunk("orphan-b", 0, new byte[] { 4, 5 }), 0),
            "second orphan reaches byte/chunk/transfer bounds");
        Equal(TraderPartyTransferRecordResult.CapacityExceeded,
            tracker.RecordChunk(new TraderPartyTransferChunk("orphan-c", 0, new byte[] { 6 }), 0),
            "pending transfer bound rejects third orphan");
        Equal(TraderPartyTransferRecordResult.CapacityExceeded,
            tracker.RecordChunk(new TraderPartyTransferChunk("orphan-a", 1, new byte[] { 6 }), 0),
            "aggregate chunk and byte bounds reject more data");
        Equal(TraderPartyTransferRecordResult.CapacityExceeded,
            tracker.RecordChunk(new TraderPartyTransferChunk("orphan-a", 2, new byte[] { 6 }), 0),
            "per-transfer chunk index bound enforced");
        Equal(TraderPartyTransferRecordResult.CapacityExceeded,
            tracker.RecordChunk(new TraderPartyTransferChunk("orphan-a", 1, new byte[] { 6, 7, 8, 9 }), 0),
            "per-chunk byte bound enforced");
        Equal(2, tracker.PendingTransferCount, "pending transfer count bounded");
        Equal(2, tracker.BufferedChunkCount, "buffered chunk count bounded");
        Equal(5L, tracker.BufferedByteCount, "buffered byte count bounded");

        var eventPayload = new byte[] { 1 };
        var eventTracker = new TraderPartyTransferTracker(limits);
        Equal(TraderPartyTransferRecordResult.Accepted,
            eventTracker.RecordBegin(Manifest("event-a-x", "bounded-event-a", 1, 1, eventPayload, 1), 0),
            "first tracked event accepted");
        Equal(TraderPartyTransferRecordResult.Accepted,
            eventTracker.RecordBegin(Manifest("event-b-x", "bounded-event-b", 1, 1, eventPayload, 1), 0),
            "second tracked event accepted");
        Equal(TraderPartyTransferRecordResult.CapacityExceeded,
            eventTracker.RecordBegin(Manifest("event-c-x", "bounded-event-c", 1, 1, eventPayload, 1), 0),
            "event high-water store is bounded");

        var tombstoneTracker = new TraderPartyTransferTracker(limits);
        Equal(TraderPartyTransferRecordResult.Accepted,
            tombstoneTracker.RecordMemberTombstone("tombstones", 1, "member-a", 1),
            "first member tombstone accepted");
        Equal(TraderPartyTransferRecordResult.CapacityExceeded,
            tombstoneTracker.RecordMemberTombstone("tombstones", 1, "member-b", 1),
            "member tombstone store is bounded");
    }

    private static void IdleTransfersExpireWithoutDuplicateKeepAlive()
    {
        var limits = new TraderPartyTransferLimits(
            4, 4, 4, 16, 8, 64L, 8, 8, 10L);
        var tracker = new TraderPartyTransferTracker(limits);
        var chunk = new TraderPartyTransferChunk("expiry", 0, new byte[] { 1, 2 });
        Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordChunk(chunk, 0), "idle transfer starts");
        Equal(TraderPartyTransferRecordResult.Duplicate, tracker.RecordChunk(chunk, 9), "duplicate retry recognized");
        Equal(1, tracker.Expire(10), "duplicate retry does not extend idle expiry");
        Equal(0, tracker.PendingTransferCount, "expiry releases pending slot");
        Equal(0L, tracker.BufferedByteCount, "expiry releases bytes");
        Equal(TraderPartyTransferRecordResult.Stale, tracker.RecordChunk(chunk, 11),
            "expired transfer ID stays a bounded terminal no-op");

        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordChunk(new TraderPartyTransferChunk("expiry-new", 0, new byte[] { 1, 2 }), 12),
            "new transfer ID can retry after expiration");
        tracker.Reset();
        Equal(0, tracker.PendingTransferCount, "reset releases all pending state");
        Equal(0, tracker.TrackedEventCount, "reset releases generation high-water state");
    }

    private static void InputsAndCompletionAreImmutableCopies()
    {
        var payload = new byte[] { 60, 61, 62 };
        var originalPayload = (byte[])payload.Clone();
        var members = Members("immutable", 1);
        var manifest = new TraderPartyTransferManifest(
            "immutable", "immutable", 1, 1, 1, payload.Length, members, Hash(payload).ToUpperInvariant());
        members[0] = "mutated-member";
        var chunk = new TraderPartyTransferChunk("immutable", 0, payload);
        payload[0] = 99;

        var tracker = NewTracker();
        Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordChunk(chunk, 0), "immutable chunk accepted");
        Equal(TraderPartyTransferRecordResult.Accepted, tracker.RecordBegin(manifest, 1), "immutable manifest accepted");
        Equal(TraderPartyTransferRecordResult.Accepted,
            tracker.RecordCommit(Commit("immutable", "immutable", 1, 1, originalPayload, 1), 2),
            "immutable commit completes");
        Equal(true, tracker.TryTakeComplete("immutable", out var completion), "immutable transfer completes");
        Equal("immutable:1:trader:0", completion?.Manifest.MemberNetworkIds[0], "manifest copied member IDs");
        var firstCopy = completion?.GetPayloadCopy() ?? Array.Empty<byte>();
        firstCopy[0] = 88;
        BytesEqual(originalPayload, completion?.GetPayloadCopy() ?? Array.Empty<byte>(), "completion payload remains immutable");
    }

    private static TraderPartyTransferTracker NewTracker()
    {
        return new TraderPartyTransferTracker(new TraderPartyTransferLimits(
            8, 16, 8, 1024, 32, 8192L, 64, 32, 1000L));
    }

    private static TraderPartyTransferManifest Manifest(
        string transferId,
        string eventId,
        long partyGeneration,
        long manifestRevision,
        byte[] payload,
        int chunkCount)
    {
        return new TraderPartyTransferManifest(
            transferId,
            eventId,
            partyGeneration,
            manifestRevision,
            chunkCount,
            payload.Length,
            Members(eventId, partyGeneration),
            Hash(payload));
    }

    private static TraderPartyTransferCommit Commit(
        string transferId,
        string eventId,
        long partyGeneration,
        long manifestRevision,
        byte[] payload,
        int chunkCount)
    {
        return new TraderPartyTransferCommit(
            transferId,
            eventId,
            partyGeneration,
            manifestRevision,
            chunkCount,
            payload.Length,
            Members(eventId, partyGeneration),
            Hash(payload));
    }

    private static string[] Members(string eventId, long partyGeneration)
    {
        return new[]
        {
            eventId + ":" + partyGeneration + ":trader:0",
            eventId + ":" + partyGeneration + ":guard:0"
        };
    }

    private static byte[] Slice(byte[] source, int offset, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(source, offset, result, 0, count);
        return result;
    }

    private static string Hash(byte[] payload)
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
}
