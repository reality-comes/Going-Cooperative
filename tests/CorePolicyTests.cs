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

        Console.WriteLine(failures == 0 ? "PASS CorePolicyTests" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }
}
