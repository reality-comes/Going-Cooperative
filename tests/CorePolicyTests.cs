using System;
using System.Globalization;
using GoingCooperative.Core;

internal static class CorePolicyTests
{
    private static int failures;

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!Equals(expected, actual))
        {
            Console.Error.WriteLine("FAIL " + name + " expected=" + expected + " actual=" + actual);
            failures++;
        }
    }

    private static bool CanReadCombatPresentationPayload(string payload)
    {
        return LockstepCommandPayloads.TryReadCombatPresentationPayload(
            payload,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);
    }

    public static int Main()
    {
        var pause = LockstepCommandPayloads.CreatePausePayload(true);
        Equal(true, LockstepCommandPayloads.TryReadPausePayload(pause, out var paused), "pause payload runtime-safe parse");
        Equal(true, paused, "pause payload value");
        var normalSpeed = LockstepCommandPayloads.CreateSetSpeedNormalPayload();
        Equal(true, LockstepCommandPayloads.TryReadSpeedPayload(normalSpeed, out var normalSpeedIndex, out var normalSpeedAction), "normal speed payload runtime-safe parse");
        Equal(LockstepCommandPayloads.NormalSpeedIndex, normalSpeedIndex, "normal speed index");
        Equal(LockstepCommandPayloads.SetSpeedNormalAction, normalSpeedAction, "normal speed action");

        var buildBatchRecords = new[]
        {
            "N,10,5,-4,90",
            "N,11,5,-4,90",
            "R,20,7,30,180,4,2,8;20,7,30;21,7,30"
        };
        var buildBatch = LockstepCommandPayloads.CreateBuildBatchPayload(
            "wood_floor\"variant",
            "Floor",
            "Player",
            false,
            buildBatchRecords);
        Equal(true, LockstepCommandPayloads.TryReadBuildBatchPayload(
            buildBatch,
            out var buildBatchBlueprint,
            out var buildBatchType,
            out var buildBatchFaction,
            out var buildBatchAfterLoading,
            out var parsedBuildBatchRecords), "build batch payload parses");
        Equal("wood_floor\"variant", buildBatchBlueprint, "build batch blueprint escaping");
        Equal("Floor", buildBatchType, "build batch type");
        Equal("Player", buildBatchFaction, "build batch faction");
        Equal(false, buildBatchAfterLoading, "build batch after-loading");
        Equal(buildBatchRecords.Length, parsedBuildBatchRecords.Length, "build batch record count");
        Equal(buildBatchRecords[2], parsedBuildBatchRecords[2], "build batch roof topology roundtrip");
        var epochBuildBatch = LockstepCommandPayloads.CreateBuildBatchPayload(
            "wood_floor",
            "Floor",
            "Player",
            false,
            buildBatchRecords,
            7L);
        Equal(true, LockstepCommandPayloads.TryReadBuildBatchEpoch(epochBuildBatch, out var buildBatchEpoch), "build batch epoch parses");
        Equal(7L, buildBatchEpoch, "build batch epoch roundtrip");
        Equal(false, LockstepCommandPayloads.TryReadBuildBatchEpoch(buildBatch, out _), "legacy build batch has no epoch");
        Equal(false, LockstepCommandPayloads.TryReadBuildBatchPayload(
            LockstepCommandPayloads.CreateBuildBatchPayload("wood_floor", "Floor", "Player", false, Array.Empty<string>()),
            out _, out _, out _, out _, out _), "build batch rejects empty placements");
        Equal(false, LockstepCommandPayloads.TryReadBuildBatchPayload(
            LockstepCommandPayloads.CreateBuildBatchPayload("wood_floor", "Floor", "Player", false, new string[513]),
            out _, out _, out _, out _, out _), "build batch rejects placement over cap");
        Equal(false, LockstepCommandPayloads.TryReadBuildBatchPayload(
            buildBatch.Replace("PlaceBlueprintBatch", "PlaceBlueprint"),
            out _, out _, out _, out _, out _), "build batch rejects legacy action");
        var oversizedBuildRecords = new string[9];
        for (var i = 0; i < oversizedBuildRecords.Length; i++)
        {
            oversizedBuildRecords[i] = new string('1', 16384);
        }
        Equal(false, LockstepCommandPayloads.TryReadBuildBatchPayload(
            LockstepCommandPayloads.CreateBuildBatchPayload(
                "wood_floor",
                "Floor",
                "Player",
                false,
                oversizedBuildRecords),
            out _, out _, out _, out _, out _), "build batch rejects payload beyond bidirectional chunk budget");

        Equal(false, ReplicationOrderingPolicy.ShouldReplaceQueuedAbsoluteState(102, 101), "absolute-state coalescing keeps newer queued packet");
        Equal(true, ReplicationOrderingPolicy.ShouldReplaceQueuedAbsoluteState(102, 103), "absolute-state coalescing accepts newer packet");
        Equal(false, ReplicationOrderingPolicy.IsStaleAppliedAbsoluteState(-1, 1), "absolute-state high-water accepts first packet");
        Equal(true, ReplicationOrderingPolicy.IsStaleAppliedAbsoluteState(102, 101), "absolute-state high-water rejects delayed older packet");
        Equal(true, ReplicationOrderingPolicy.IsStaleAppliedAbsoluteState(102, 102), "absolute-state high-water rejects repeated packet");
        Equal(false, ReplicationOrderingPolicy.IsStaleAppliedAbsoluteState(102, 103), "absolute-state high-water accepts newer packet");
        Equal(103L, ReplicationOrderingPolicy.AdvanceAppliedAbsoluteState(102, 103), "absolute-state high-water advances");
        Equal(103L, ReplicationOrderingPolicy.AdvanceAppliedAbsoluteState(103, 102), "absolute-state high-water never regresses");
        Equal(false, ReplicationOrderingPolicy.IsStaleSnapshot(-1, 1), "snapshot high-water accepts first checkpoint");
        Equal(true, ReplicationOrderingPolicy.IsStaleSnapshot(5, 4), "snapshot high-water rejects older checkpoint");
        Equal(false, ReplicationOrderingPolicy.IsStaleSnapshot(5, 5), "snapshot high-water accepts current checkpoint rows");
        Equal(false, ReplicationOrderingPolicy.IsStaleSnapshot(5, 6), "snapshot high-water accepts newer checkpoint");
        Equal(6L, ReplicationOrderingPolicy.AdvanceSnapshot(5, 6), "snapshot high-water advances");
        Equal(6L, ReplicationOrderingPolicy.AdvanceSnapshot(6, 5), "snapshot high-water never regresses");
        Equal(false, ReplicationOrderingPolicy.IsSnapshotComplete(false, 2, 2), "snapshot rows before End remain pending");
        Equal(false, ReplicationOrderingPolicy.IsSnapshotComplete(true, 2, 1), "snapshot End before final row remains pending");
        Equal(true, ReplicationOrderingPolicy.IsSnapshotComplete(true, 2, 2), "snapshot final row after End completes checkpoint");
        Equal(false, ReplicationOrderingPolicy.IsSnapshotComplete(true, 2, 3), "snapshot over-count does not falsely complete");
        Equal(false, ReplicationOrderingPolicy.ShouldAcceptBuildBatch(1, 128), "build batch 1/128 partial commit is rejected");
        Equal(false, ReplicationOrderingPolicy.ShouldAcceptBuildBatch(127, 128), "build batch 127/128 partial commit is rejected");
        Equal(true, ReplicationOrderingPolicy.ShouldAcceptBuildBatch(128, 128), "build batch 128/128 exact commits remain accepted after a post-commit throw");
        Equal(false, ReplicationOrderingPolicy.ShouldAcceptBuildBatch(0, 0), "build batch empty request is rejected");
        Equal(false, ReplicationOrderingPolicy.ShouldLatchBuildBatchRecovery(0, 128, true, true), "proven full rollback does not latch recovery");
        Equal(true, ReplicationOrderingPolicy.ShouldLatchBuildBatchRecovery(1, 128, true, false), "partial rollback failure latches full-save recovery");
        Equal(false, ReplicationOrderingPolicy.ShouldLatchBuildBatchRecovery(128, 128, false, false), "fully committed post-commit throw does not become rollback recovery");
        Equal(false, ReplicationOrderingPolicy.ShouldEscalateBuildBatchReplay(7, 8), "build batch replay remains bounded below failure cap");
        Equal(true, ReplicationOrderingPolicy.ShouldEscalateBuildBatchReplay(8, 8), "build batch replay escalates at failure cap");
        Equal(false, ReplicationOrderingPolicy.ShouldEscalateBuildBatchReplay(8, 0), "build batch replay rejects invalid failure cap");
        Equal(false, ReplicationOrderingPolicy.IsValidBuildRoofPositionCount(0, 512), "roof topology rejects empty positions");
        Equal(true, ReplicationOrderingPolicy.IsValidBuildRoofPositionCount(1, 512), "roof topology accepts one committed position");
        Equal(true, ReplicationOrderingPolicy.IsValidBuildRoofPositionCount(512, 512), "roof topology accepts exact cap");
        Equal(false, ReplicationOrderingPolicy.IsValidBuildRoofPositionCount(513, 512), "roof topology rejects over cap");
        Equal(0, ReplicationOrderingPolicy.GetBuildRepairDependencyRank(true, false), "building repair sends exact supports before dependent roofs");
        Equal(1, ReplicationOrderingPolicy.GetBuildRepairDependencyRank(true, true), "building repair sends exact roofs after supports");
        Equal(2, ReplicationOrderingPolicy.GetBuildRepairDependencyRank(false, false), "building repair sends unreplayable diagnostics last");
        Equal(256, ReplicationOrderingPolicy.GetBoundedSnapshotPageCount(600, 0, 256), "building repair first page is bounded");
        Equal(256, ReplicationOrderingPolicy.GetBoundedSnapshotPageCount(600, 256, 256), "building repair middle page is bounded");
        Equal(88, ReplicationOrderingPolicy.GetBoundedSnapshotPageCount(600, 512, 256), "building repair final page covers remainder");
        Equal(256, ReplicationOrderingPolicy.AdvanceSnapshotPageOffset(600, 0, 256), "building repair advances first page");
        Equal(600, ReplicationOrderingPolicy.AdvanceSnapshotPageOffset(600, 512, 88), "building repair reaches total count");
        Equal(false, ReplicationOrderingPolicy.IsFinalSnapshotPage(600, 256, 256), "building repair middle page is not final");
        Equal(true, ReplicationOrderingPolicy.IsFinalSnapshotPage(600, 512, 88), "building repair final page is terminal");
        Equal(true, ReplicationOrderingPolicy.ShouldPruneCheckpointEvent(false, 101, 102), "checkpoint prunes event older than Begin boundary");
        Equal(false, ReplicationOrderingPolicy.ShouldPruneCheckpointEvent(false, 103, 102), "checkpoint preserves event newer than Begin boundary");
        Equal(false, ReplicationOrderingPolicy.ShouldPruneCheckpointEvent(true, 101, 102), "checkpoint preserves seen event");
        Equal(true, ReplicationOrderingPolicy.IsValidGameTime(10, 0.5f, 2f), "game time accepts finite calendar state");
        Equal(false, ReplicationOrderingPolicy.IsValidGameTime(-1, 0.5f, 2f), "game time rejects negative minutes");
        Equal(false, ReplicationOrderingPolicy.IsValidGameTime(10, float.NaN, 2f), "game time rejects NaN fraction");
        Equal(false, ReplicationOrderingPolicy.IsValidGameTime(10, 1f, 2f), "game time rejects out-of-range fraction");
        Equal(false, ReplicationOrderingPolicy.IsValidGameTime(10, 0.5f, float.PositiveInfinity), "game time rejects infinite diagnostic time");

        var chunkSource = new TransportEnvelope(
            TransportMessageKind.ReplicationIntent,
            42,
            "client",
            new string('x', 2400));
        var chunks = TransportChunkCodec.CreateChunks(chunkSource, "chunk-test", 100);
        var reassembler = new TransportChunkReassembler();
        TransportEnvelope? reassembled = null;
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            Equal(true, reassembler.TryAddChunk(chunks[i], out var candidate, out _), "chunk reassembly accepts reverse-order part " + i.ToString(CultureInfo.InvariantCulture));
            if (candidate != null) reassembled = candidate;
        }
        Equal(true, reassembled != null, "chunk reassembly completes");
        Equal(chunkSource.Payload, reassembled?.Payload, "chunk reassembly payload roundtrip");
        var oversizedParts = chunks[0].Payload.Split(new[] { '|' }, StringSplitOptions.None);
        oversizedParts[3] = "513";
        var oversizedChunk = new TransportEnvelope(
            TransportMessageKind.Chunk,
            chunks[0].Tick,
            chunks[0].SenderId,
            string.Join("|", oversizedParts));
        Equal(false, reassembler.TryAddChunk(oversizedChunk, out _, out _), "chunk reassembly rejects excessive chunk count");
        var oversizedChunkCreationRejected = false;
        try
        {
            TransportChunkCodec.CreateChunks(
                new TransportEnvelope(
                    TransportMessageKind.ReplicationIntent,
                    43,
                    "client",
                    new string('x', 40000)),
                "chunk-create-over-cap",
                100);
        }
        catch (InvalidOperationException)
        {
            oversizedChunkCreationRejected = true;
        }
        Equal(true, oversizedChunkCreationRejected, "chunk sender rejects envelope receiver cannot reassemble");

        var eventChoice = LockstepCommandPayloads.CreateGameEventOptionChosenPayload(
            7,
            "session:event:\"one\n",
            12,
            "dialog branch \"A\"",
            3,
            2,
            "request:abc-123");
        Equal(true, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(
            eventChoice,
            out var eventEpoch,
            out var eventId,
            out var eventRevision,
            out var eventDialogId,
            out var eventDialogIndex,
            out var eventOptionIndex,
            out var eventRequestId), "event choice payload parses");
        Equal(7L, eventEpoch, "event choice epoch");
        Equal("session:event:\"one\n", eventId, "event choice event identity escaping");
        Equal(12L, eventRevision, "event choice revision");
        Equal("dialog branch \"A\"", eventDialogId, "event choice dialog identity escaping");
        Equal(3, eventDialogIndex, "event choice dialog index");
        Equal(2, eventOptionIndex, "event choice option index");
        Equal("request:abc-123", eventRequestId, "event choice request identity");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(eventChoice.Replace("\"epoch\":7", "\"epoch\":-1"), out _, out _, out _, out _, out _, out _, out _), "event choice rejects negative epoch");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(eventChoice.Replace("\"eventRevision\":12", "\"eventRevision\":-1"), out _, out _, out _, out _, out _, out _, out _), "event choice rejects negative revision");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(eventChoice.Replace("\"optionIndex\":2", "\"optionIndex\":32"), out _, out _, out _, out _, out _, out _, out _), "event choice rejects option outside protocol bound");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(eventChoice.Replace("GameEventOptionChosen", "CombatAttack"), out _, out _, out _, out _, out _, out _, out _), "event choice rejects other action");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(LockstepCommandPayloads.CreateGameEventOptionChosenPayload(0, new string('e', 257), 0, string.Empty, -1, 0, "request"), out _, out _, out _, out _, out _, out _, out _), "event choice rejects oversized event identity");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(LockstepCommandPayloads.CreateGameEventOptionChosenPayload(0, "event", 0, new string('d', 257), -1, 0, "request"), out _, out _, out _, out _, out _, out _, out _), "event choice rejects oversized dialog identity");
        Equal(false, LockstepCommandPayloads.TryReadGameEventOptionChosenPayload(LockstepCommandPayloads.CreateGameEventOptionChosenPayload(0, "event", 0, string.Empty, -1, 0, new string('r', 129)), out _, out _, out _, out _, out _, out _, out _), "event choice rejects oversized request identity");

        Equal(5, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.Building, true, 5, 15), "building uses map Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.MapResource, true, 5, 15), "map resource uses selection Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.ResourcePile, true, 5, 15), "pile contextual action uses selection Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.Stockpile, true, 5, 15), "stockpile region uses selection Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.Building, false, 0, 15), "building safely falls back");

        var research = LockstepCommandPayloads.CreateResearchActivatePayload("research_\"one");
        Equal(true, LockstepCommandPayloads.TryReadResearchActivatePayload(research, out var node), "research payload parses");
        Equal("research_\"one", node, "research payload roundtrip");

        var production = LockstepCommandPayloads.CreateProductionQueuePayload("SetCount", 10, 5, 12, 2, "meal_stew", 20);
        Equal(true, LockstepCommandPayloads.TryReadProductionQueuePayload(production, out var operation, out var x, out var y, out var z, out var index, out var blueprint, out var value), "production payload parses");
        Equal("SetCount", operation, "production operation");
        Equal(5, y, "production map Y");
        Equal(2, index, "production ticket index");
        Equal("meal_stew", blueprint, "production blueprint");
        Equal(20, value, "production value");

        var policy = LockstepCommandPayloads.CreateManagementPolicyPayload("WorkerSchedule", "uid:-3", "Sleep", 7, 2, true);
        Equal(true, LockstepCommandPayloads.TryReadManagementPolicyPayload(policy, out var policyKind, out var targetId, out var policyKey, out var policyIndex, out var policyValue, out var policyEnabled), "management policy parses");
        Equal("WorkerSchedule", policyKind, "management policy kind");
        var hunt = LockstepCommandPayloads.CreateManagementPolicyPayload("AnimalOrder", "uid:42", "Hunt", 0, 1, true);
        Equal(true, LockstepCommandPayloads.TryReadManagementPolicyPayload(hunt, out var huntPolicy, out var huntTarget, out var huntOrder, out _, out var huntValue, out _), "hunt payload parses");
        Equal("AnimalOrder", huntPolicy, "hunt policy kind");
        Equal("uid:42", huntTarget, "hunt target identity");
        Equal("Hunt", huntOrder, "hunt order name");
        Equal(1, huntValue, "hunt native order value");
        var tame = LockstepCommandPayloads.CreateManagementPolicyPayload("AnimalOrder", "uid:43", "Tame", 0, 2, true);
        Equal(true, LockstepCommandPayloads.TryReadManagementPolicyPayload(tame, out var tamePolicy, out var tameTarget, out var tameOrder, out _, out var tameValue, out _), "tame payload parses");
        Equal("AnimalOrder", tamePolicy, "tame policy kind");
        Equal("uid:43", tameTarget, "tame target identity");
        Equal("Tame", tameOrder, "tame order name");
        Equal(2, tameValue, "tame native order value");
        var slaughter = LockstepCommandPayloads.CreateManagementPolicyPayload("AnimalOrder", "uid:44", "Slaughter", 0, 4, true);
        Equal(true, LockstepCommandPayloads.TryReadManagementPolicyPayload(slaughter, out var slaughterPolicy, out var slaughterTarget, out var slaughterOrder, out _, out var slaughterValue, out _), "slaughter payload parses");
        Equal("AnimalOrder", slaughterPolicy, "slaughter policy kind");
        Equal("uid:44", slaughterTarget, "slaughter target identity");
        Equal("Slaughter", slaughterOrder, "slaughter order name");
        Equal(4, slaughterValue, "slaughter native order value");
        Equal("uid:-3", targetId, "management target");
        Equal(7, policyIndex, "management index");
        Equal(2, policyValue, "management value");
        Equal(true, policyEnabled, "management enabled");

        var draftState = LockstepCommandPayloads.CreateDraftStatePayload("uid:7 player \"one\"\n", true, "Aggressive stance");
        Equal(true, LockstepCommandPayloads.TryReadDraftStatePayload(draftState, out var draftedEntityId, out var drafted, out var draftMode), "draft state parses");
        Equal("uid:7 player \"one\"\n", draftedEntityId, "draft state entity escaping");
        Equal(true, drafted, "draft state value");
        Equal("Aggressive stance", draftMode, "draft state mode preserves spaces");
        Equal(false, LockstepCommandPayloads.TryReadDraftStatePayload(draftState.Replace("DraftState", "CombatAttack"), out _, out _, out _), "draft state rejects other action");
        Equal(false, LockstepCommandPayloads.TryReadDraftStatePayload("{\"action\":\"DraftState\",\"entityId\":\"\",\"drafted\":true,\"combatMode\":\"Default\"}", out _, out _, out _), "draft state rejects empty entity");

        var draftMove = LockstepCommandPayloads.CreateDraftMovePayload(
            new[] { "uid:1", "worker, two", "worker\\\"three" },
            -12,
            5,
            91,
            "Run and attack");
        Equal(true, LockstepCommandPayloads.TryReadDraftMovePayload(draftMove, out var movingEntities, out var moveX, out var moveY, out var moveZ, out var moveMode), "draft move parses");
        Equal(3, movingEntities.Length, "draft move entity count");
        Equal("uid:1", movingEntities[0], "draft move first entity");
        Equal("worker, two", movingEntities[1], "draft move comma-safe entity");
        Equal("worker\\\"three", movingEntities[2], "draft move escaped entity");
        Equal(-12, moveX, "draft move target X");
        Equal(5, moveY, "draft move target Y");
        Equal(91, moveZ, "draft move target Z");
        Equal("Run and attack", moveMode, "draft move mode");
        Equal(false, LockstepCommandPayloads.TryReadDraftMovePayload("{\"action\":\"DraftMove\",\"entityIds\":[],\"targetX\":1,\"targetY\":2,\"targetZ\":3,\"combatMode\":\"Run\"}", out _, out _, out _, out _, out _), "draft move rejects empty selection");

        var attack = LockstepCommandPayloads.CreateCombatAttackPayload(
            new[] { "uid:8", "uid:9" },
            "Grid",
            string.Empty,
            41,
            6,
            -23);
        Equal(true, LockstepCommandPayloads.TryReadCombatAttackPayload(attack, out var attackers, out var attackTargetKind, out var attackTargetId, out var attackX, out var attackY, out var attackZ), "combat attack parses");
        Equal(2, attackers.Length, "combat attack attacker count");
        Equal("Grid", attackTargetKind, "combat attack target kind");
        Equal(string.Empty, attackTargetId, "combat attack optional target identity");
        Equal(41, attackX, "combat attack target X");
        Equal(6, attackY, "combat attack target Y");
        Equal(-23, attackZ, "combat attack target Z");
        Equal(false, LockstepCommandPayloads.TryReadCombatAttackPayload("{\"action\":\"CombatAttack\",\"attackerEntityIds\":[\"\"],\"targetKind\":\"Entity\",\"targetId\":\"uid:4\",\"targetX\":1,\"targetY\":2,\"targetZ\":3}", out _, out _, out _, out _, out _, out _), "combat attack rejects blank attacker");

        var cancel = LockstepCommandPayloads.CreateCombatCancelPayload(new[] { "uid:8", "uid:9" });
        Equal(true, LockstepCommandPayloads.TryReadCombatCancelPayload(cancel, out var cancelAttackers), "combat cancel parses");
        Equal(2, cancelAttackers.Length, "combat cancel attacker count");
        Equal("uid:9", cancelAttackers[1], "combat cancel attacker identity");
        Equal(false, LockstepCommandPayloads.TryReadCombatCancelPayload("{\"action\":\"CombatCancel\",\"attackerEntityIds\":[]}", out _), "combat cancel rejects empty selection");

        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var outcome = LockstepCommandPayloads.CreateCombatOutcomePayload(
                "combat:999:3",
                9876543210,
                "uid:8",
                "Entity",
                "uid:42",
                17,
                4,
                29,
                "DamageApplied",
                10.6875,
                "left arm",
                "pierced_lung",
                true);
            Equal(true, outcome.IndexOf("\"amount\":10.6875", StringComparison.Ordinal) >= 0, "combat outcome uses invariant decimal");
            Equal(true, LockstepCommandPayloads.TryReadCombatOutcomePayload(outcome, out var outcomeId, out var outcomeTick, out var outcomeAttacker, out var outcomeTargetKind, out var outcomeTargetId, out var outcomeX, out var outcomeY, out var outcomeZ, out var outcomeType, out var outcomeAmount, out var bodyPart, out var effectId, out var lethal), "combat outcome parses");
            Equal("combat:999:3", outcomeId, "combat outcome identity");
            Equal(9876543210, outcomeTick, "combat outcome authoritative tick");
            Equal("uid:8", outcomeAttacker, "combat outcome attacker");
            Equal("Entity", outcomeTargetKind, "combat outcome target kind");
            Equal("uid:42", outcomeTargetId, "combat outcome target identity");
            Equal(17, outcomeX, "combat outcome X");
            Equal(4, outcomeY, "combat outcome Y");
            Equal(29, outcomeZ, "combat outcome Z");
            Equal("DamageApplied", outcomeType, "combat outcome type");
            Equal(10.6875, outcomeAmount, "combat outcome amount");
            Equal("left arm", bodyPart, "combat outcome body part");
            Equal("pierced_lung", effectId, "combat outcome effect");
            Equal(true, lethal, "combat outcome lethal");

            var chargeStart = LockstepCommandPayloads.CreateCombatPresentationPayload(
                "charge:\"alpha\n",
                9876543211,
                "uid:8 archer\n",
                LockstepCommandPayloads.CombatPresentationChargeStartPhase,
                LockstepCommandPayloads.CombatPresentationAttackKindRanged,
                1.375,
                "Shoot Bow \"charged\"\n",
                -7,
                2147483647,
                LockstepCommandPayloads.CombatPresentationEndReasonNone);
            Equal(true, chargeStart.IndexOf("\"durationSeconds\":1.375", StringComparison.Ordinal) >= 0, "combat presentation uses invariant duration");
            Equal(true, LockstepCommandPayloads.TryReadCombatPresentationPayload(chargeStart, out var startChargeId, out var startTick, out var startAttacker, out var startPhase, out var startAttackKind, out var startDuration, out var startAnimation, out var startWeaponType, out var startAttackRnd, out var startEndReason), "combat charge start parses");
            Equal("charge:\"alpha\n", startChargeId, "combat charge start identity escaping");
            Equal(9876543211, startTick, "combat charge start authoritative tick");
            Equal("uid:8 archer\n", startAttacker, "combat charge start attacker escaping");
            Equal(LockstepCommandPayloads.CombatPresentationChargeStartPhase, startPhase, "combat charge start phase");
            Equal(LockstepCommandPayloads.CombatPresentationAttackKindRanged, startAttackKind, "combat charge start attack kind");
            Equal(1.375, startDuration, "combat charge start duration");
            Equal("Shoot Bow \"charged\"\n", startAnimation, "combat charge start animation escaping");
            Equal(-7, startWeaponType, "combat charge start weapon type");
            Equal(2147483647, startAttackRnd, "combat charge start attack random");
            Equal(LockstepCommandPayloads.CombatPresentationEndReasonNone, startEndReason, "combat charge start end reason");

            var weaponRelease = LockstepCommandPayloads.CreateCombatPresentationPayload(
                "charge:release",
                9876543212,
                "uid:8",
                LockstepCommandPayloads.CombatPresentationWeaponReleasePhase,
                LockstepCommandPayloads.CombatPresentationAttackKindUnknown,
                0,
                string.Empty,
                0,
                -19,
                LockstepCommandPayloads.CombatPresentationEndReasonNone);
            Equal(true, LockstepCommandPayloads.TryReadCombatPresentationPayload(weaponRelease, out var releaseChargeId, out var releaseTick, out var releaseAttacker, out var releasePhase, out var releaseAttackKind, out var releaseDuration, out var releaseAnimation, out var releaseWeaponType, out var releaseAttackRnd, out var releaseEndReason), "combat weapon release parses");
            Equal("charge:release", releaseChargeId, "combat weapon release identity");
            Equal(9876543212, releaseTick, "combat weapon release authoritative tick");
            Equal("uid:8", releaseAttacker, "combat weapon release attacker");
            Equal(LockstepCommandPayloads.CombatPresentationWeaponReleasePhase, releasePhase, "combat weapon release phase");
            Equal(LockstepCommandPayloads.CombatPresentationAttackKindUnknown, releaseAttackKind, "combat weapon release attack kind");
            Equal(0d, releaseDuration, "combat weapon release permits zero duration");
            Equal(string.Empty, releaseAnimation, "combat weapon release optional animation");
            Equal(0, releaseWeaponType, "combat weapon release weapon type");
            Equal(-19, releaseAttackRnd, "combat weapon release attack random");
            Equal(LockstepCommandPayloads.CombatPresentationEndReasonNone, releaseEndReason, "combat weapon release end reason");
            Equal(true, CanReadCombatPresentationPayload(weaponRelease.Replace(",\"animationToken\":\"\"", string.Empty)), "combat weapon release accepts omitted animation");
            Equal(false, CanReadCombatPresentationPayload(weaponRelease.Replace("\"animationToken\":\"\"", "\"animationToken\":17")), "combat weapon release rejects malformed optional animation");

            var chargeEnd = LockstepCommandPayloads.CreateCombatPresentationPayload(
                "charge:end",
                9876543213,
                "uid:9",
                LockstepCommandPayloads.CombatPresentationChargeEndPhase,
                LockstepCommandPayloads.CombatPresentationAttackKindMelee,
                0.25,
                "Melee Release",
                12,
                -5,
                LockstepCommandPayloads.CombatPresentationEndReasonStreamEnded);
            Equal(true, LockstepCommandPayloads.TryReadCombatPresentationPayload(chargeEnd, out var endChargeId, out var endTick, out var endAttacker, out var endPhase, out var endAttackKind, out var endDuration, out var endAnimation, out var endWeaponType, out var endAttackRnd, out var endReason), "combat charge end parses");
            Equal("charge:end", endChargeId, "combat charge end identity");
            Equal(9876543213, endTick, "combat charge end authoritative tick");
            Equal("uid:9", endAttacker, "combat charge end attacker");
            Equal(LockstepCommandPayloads.CombatPresentationChargeEndPhase, endPhase, "combat charge end phase");
            Equal(LockstepCommandPayloads.CombatPresentationAttackKindMelee, endAttackKind, "combat charge end attack kind");
            Equal(0.25, endDuration, "combat charge end duration");
            Equal("Melee Release", endAnimation, "combat charge end optional animation");
            Equal(12, endWeaponType, "combat charge end weapon type");
            Equal(-5, endAttackRnd, "combat charge end attack random");
            Equal(LockstepCommandPayloads.CombatPresentationEndReasonStreamEnded, endReason, "combat charge end reason");

            Equal(true, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:completed", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeEndPhase, LockstepCommandPayloads.CombatPresentationAttackKindUnknown, 0, string.Empty, 0, 0, LockstepCommandPayloads.CombatPresentationEndReasonCompleted)), "combat presentation accepts completed reason");
            Equal(true, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:cancelled", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeEndPhase, LockstepCommandPayloads.CombatPresentationAttackKindUnknown, 0, string.Empty, 0, 0, LockstepCommandPayloads.CombatPresentationEndReasonCancelled)), "combat presentation accepts cancelled reason");
            Equal(true, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:watchdog", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeEndPhase, LockstepCommandPayloads.CombatPresentationAttackKindUnknown, 0, string.Empty, 0, 0, LockstepCommandPayloads.CombatPresentationEndReasonWatchdog)), "combat presentation accepts watchdog reason");

            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload(string.Empty, 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, 1, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects blank charge identity");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", -1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, 1, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects negative authoritative tick");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, " ", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, 1, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects blank attacker");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", "ChargePause", LockstepCommandPayloads.CombatPresentationAttackKindRanged, 1, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects unknown phase");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, "magic", 1, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects unknown attack kind");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, 0, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects zero start duration");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationWeaponReleasePhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, -0.25, string.Empty, 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects negative release duration");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, double.PositiveInfinity, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects infinite duration");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, double.NaN, "Shoot", 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone)), "combat presentation rejects NaN duration");
            var blankStartAnimation = LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, 1, string.Empty, 1, 1, LockstepCommandPayloads.CombatPresentationEndReasonNone);
            Equal(false, CanReadCombatPresentationPayload(blankStartAnimation), "combat presentation rejects blank start animation");
            Equal(false, CanReadCombatPresentationPayload(blankStartAnimation.Replace(",\"animationToken\":\"\"", string.Empty)), "combat presentation rejects omitted start animation");
            Equal(false, CanReadCombatPresentationPayload(LockstepCommandPayloads.CreateCombatPresentationPayload("charge:invalid", 1, "uid:1", LockstepCommandPayloads.CombatPresentationChargeEndPhase, LockstepCommandPayloads.CombatPresentationAttackKindRanged, 0, string.Empty, 1, 1, "interrupted")), "combat presentation rejects unknown end reason");
            Equal(false, CanReadCombatPresentationPayload(chargeStart.Replace("CombatPresentation", "CombatOutcome")), "combat presentation rejects other action");
            Equal(false, CanReadCombatPresentationPayload("{\"action\":\"CombatPresentation\",\"chargeId\":\"missing-fields\"}"), "combat presentation rejects incomplete payload");
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }

        Equal(
            CombatPresentationEndDisposition.ApplyActive,
            CombatPresentationOrderingPolicy.ResolveEnd("charge:active", 10, null, "charge:active", 11),
            "combat end applies to active charge");
        Equal(
            CombatPresentationEndDisposition.ApplyCompleted,
            CombatPresentationOrderingPolicy.ResolveEnd(null, long.MinValue, "charge:completed", "charge:completed", 12),
            "combat stream end applies to completed charge");
        Equal(
            CombatPresentationEndDisposition.SupersedeActive,
            CombatPresentationOrderingPolicy.ResolveEnd("charge:old", 10, null, "charge:new", 11),
            "newer combat end supersedes older active charge");
        Equal(
            CombatPresentationEndDisposition.WaitForStart,
            CombatPresentationOrderingPolicy.ResolveEnd(null, long.MinValue, null, "charge:reordered", 13),
            "reordered combat end waits for start");
        Equal(
            CombatPresentationEndDisposition.WaitForStart,
            CombatPresentationOrderingPolicy.ResolveEnd("charge:new", 15, null, "charge:old", 14),
            "older unmatched combat end cannot supersede active charge");

        Equal(false, LockstepCommandPayloads.TryReadCombatOutcomePayload("{\"action\":\"CombatOutcome\",\"outcomeId\":\"missing-fields\"}", out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _), "combat outcome rejects incomplete payload");

        failures += BuildingReplicationV2PolicyTests.Run();
        Console.WriteLine(failures == 0 ? "PASS CorePolicyTests" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }
}
