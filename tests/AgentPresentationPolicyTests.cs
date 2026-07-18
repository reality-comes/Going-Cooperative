using System;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;

internal static class AgentPresentationPolicyTests
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

    private static void Throws<TException>(Action action, string name)
        where TException : Exception
    {
        try
        {
            action();
            Console.Error.WriteLine("FAIL " + name + " expected=" + typeof(TException).Name + " actual=no exception");
            failures++;
        }
        catch (TException)
        {
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + name + " expected=" + typeof(TException).Name + " actual=" + exception.GetType().Name);
            failures++;
        }
    }

    public static int Main()
    {
        TestContracts();
        TestOrderedLifecycle();
        TestEndBeforeStart();
        TestEpochAndEntityIsolation();
        TestTeleportLifecycle();
        TestSemanticWorkMappings();
        TestSemanticAnimatorPolicy();

        Console.WriteLine(failures == 0 ? "PASS AgentPresentationPolicyTests" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void TestSemanticWorkMappings()
    {
        AssertWork("ChopTreeGoal", "StartObtaining", "Chop", "Mining");
        AssertWork("ConstructBuildingGoal", "ConstructAction", "Build", "Build");
        AssertWork("DigGoal", "StartObtaining", "Dig", "Mining");
        AssertWork("HarvestGoal", "StartObtaining", "Harvest", "Harvest");
        AssertWork("FollowCutPlantOrderGoal", "EnemyCutPlant", "CutPlant", "Mining");
        AssertWork("DeconstructGoal", "DeconstructAction", "Deconstruct", "Build");
        AssertWork("RepairBuildingGoal", "RepairAction", "Repair", "Build");
        AssertWork("UninstallBuildingGoal", "UninstallAction", "Uninstall", "Build");
        AssertWork("PlantCropsGoal", "PlantCropsAction", "Plant", "Planting");

        Equal(false, AgentWorkPresentationPolicy.TryResolve("DigGoal", "ConstructAction", out _), "semantic work rejects mismatched goal and action");
        Equal(false, AgentWorkPresentationPolicy.IsCandidateAction("GoToTarget"), "navigation is not semantic work");
        Equal(false, AgentWorkPresentationPolicy.IsMigratedGoal("AttackGoal"), "combat remains outside semantic work");
    }

    private static void AssertWork(
        string goalId,
        string actionId,
        string expectedWorkKind,
        string expectedAnimationToken)
    {
        Equal(true, AgentWorkPresentationPolicy.IsCandidateAction(actionId), goalId + " action is a semantic candidate");
        Equal(true, AgentWorkPresentationPolicy.IsMigratedGoal(goalId), goalId + " is a migrated semantic goal");
        Equal(true, AgentWorkPresentationPolicy.TryResolve(goalId, actionId, out var descriptor), goalId + " mapping resolves");
        Equal(expectedWorkKind, descriptor.WorkKind, goalId + " work kind");
        Equal(expectedAnimationToken, descriptor.DefaultAnimationToken, goalId + " default animation");
        Equal(expectedAnimationToken, AgentWorkPresentationPolicy.GetDefaultAnimationToken(expectedWorkKind), goalId + " client animation fallback");
    }

    private static void TestSemanticAnimatorPolicy()
    {
        Equal(true, AgentWorkPresentationPolicy.ShouldReplayAnimatorLayer(true, 101, 101), "initial anchor establishes matching state once");
        Equal(false, AgentWorkPresentationPolicy.ShouldReplayAnimatorLayer(false, 101, 101), "periodic anchor leaves aligned loop running");
        Equal(true, AgentWorkPresentationPolicy.ShouldReplayAnimatorLayer(false, 101, 202), "periodic anchor repairs divergent state");
        Equal(false, AgentWorkPresentationPolicy.ShouldReplayAnimatorLayer(false, 0, 202), "empty authoritative layer is never replayed");

        Equal(false, AgentWorkPresentationPolicy.UsesClientTriggerMaintenance("Chop"), "chop remains on the proven semantic presentation path");
        Equal(false, AgentWorkPresentationPolicy.UsesClientTriggerMaintenance("Dig"), "dig never uses fixed-rate trigger maintenance");
        Equal(true, AgentWorkPresentationPolicy.UsesClientTriggerMaintenance("Harvest"), "harvest receives bounded trigger maintenance");
        Equal(true, AgentWorkPresentationPolicy.UsesHostCyclePulse("Dig"), "dig follows authoritative host cycle pulses");
        Equal(false, AgentWorkPresentationPolicy.UsesHostCyclePulse("Harvest"), "harvest does not emit redundant host cycle pulses");
        Equal(true, AgentWorkPresentationPolicy.UsesClientStartConfirmation("Build"), "build receives one-shot start confirmation");
        Equal(false, AgentWorkPresentationPolicy.UsesClientStartConfirmation("Chop"), "chop does not receive unproven start confirmation");
        Equal(false, AgentWorkPresentationPolicy.UsesClientStartConfirmation("Dig"), "dig keeps its host-owned cycle path");
        Equal(false, AgentWorkPresentationPolicy.IsCycleDriverScopedWork("Build"), "building remains outside initial cycle-driver scope");
    }

    private static void TestContracts()
    {
        var stamp = Stamp(1, "uid:worker-1", 7, 1, 100);
        var position = Vector(1, 2, 3);
        var rotation = Rotation();
        var mutablePath = new[]
        {
            Vector(2, 2, 3),
            Vector(4, 2, 3)
        };
        var begin = ReplicationAgentMotionPresentation.Begin(
            stamp,
            position,
            rotation,
            2.5f,
            ReplicationAgentLocomotionGait.Run,
            mutablePath,
            "uid:tree-9");
        mutablePath[0] = Vector(99, 99, 99);

        Equal(ReplicationAgentMotionPhase.Begin, begin.Phase, "motion begin phase");
        Equal(2, begin.Path.Count, "motion begin bounded path copied");
        Equal(2f, begin.Path[0].X, "motion path does not retain mutable source");
        Equal(ReplicationAgentLocomotionGait.Run, begin.Gait, "motion carries explicit gait");
        Equal("uid:tree-9", begin.TargetEntityId, "motion carries semantic target");
        Equal(AgentPresentationEventRole.Start, AgentPresentationOrderingPolicy.GetRole(begin.Phase), "motion begin ordering role");
        Equal(AgentPresentationEventRole.Update, AgentPresentationOrderingPolicy.GetRole(ReplicationAgentMotionPhase.PathChanged), "path change ordering role");
        Equal(AgentPresentationEventRole.End, AgentPresentationOrderingPolicy.GetRole(ReplicationAgentMotionPhase.End), "motion end ordering role");
        Equal(AgentPresentationEventRole.Instant, AgentPresentationOrderingPolicy.GetRole(ReplicationAgentMotionPhase.Teleport), "teleport ordering role");

        var climb = ReplicationAgentMotionPresentation.PathChanged(
            Stamp(1, "uid:worker-1", 7, 2, 101),
            position,
            rotation,
            1.25f,
            ReplicationAgentLocomotionGait.Climb,
            mutablePath);
        Equal(ReplicationAgentLocomotionGait.Climb, climb.Gait, "motion preserves explicit climb gait");

        Throws<ArgumentOutOfRangeException>(
            () => ReplicationAgentMotionPresentation.Begin(
                stamp,
                position,
                rotation,
                0,
                ReplicationAgentLocomotionGait.Walk,
                mutablePath),
            "motion rejects zero desired speed");
        Throws<ArgumentOutOfRangeException>(
            () => ReplicationAgentMotionPresentation.Begin(
                stamp,
                position,
                rotation,
                1,
                ReplicationAgentLocomotionGait.Idle,
                mutablePath),
            "motion rejects idle moving gait");
        Throws<ArgumentOutOfRangeException>(
            () => ReplicationAgentMotionPresentation.Begin(
                stamp,
                position,
                rotation,
                1,
                ReplicationAgentLocomotionGait.Walk,
                new ReplicationAgentPresentationVector3[0]),
            "motion rejects empty path");
        Throws<ArgumentOutOfRangeException>(
            () => ReplicationAgentMotionPresentation.End(
                stamp,
                position,
                rotation,
                ReplicationAgentPresentationEndReason.None),
            "motion end requires reason");

        var workStart = ReplicationAgentWorkPresentation.Start(
            stamp,
            "Chop",
            "uid:tree-9",
            null,
            position,
            rotation,
            4.25,
            "Mining",
            "Axe");
        Equal(ReplicationAgentWorkPhase.Start, workStart.Phase, "work start phase");
        Equal("Chop", workStart.WorkKind, "work carries semantic kind");
        Equal(4.25, workStart.RemainingSeconds, "work start initializes local timer");
        Equal("Mining", workStart.AnimationToken, "work carries exact animation token");
        Equal(AgentPresentationEventRole.Start, AgentPresentationOrderingPolicy.GetRole(workStart.Phase), "work start ordering role");
        Equal(AgentPresentationEventRole.Update, AgentPresentationOrderingPolicy.GetRole(ReplicationAgentWorkPhase.Anchor), "work anchor ordering role");
        Equal(AgentPresentationEventRole.Commit, AgentPresentationOrderingPolicy.GetRole(ReplicationAgentWorkPhase.Commit), "work commit ordering role");
        Equal(AgentPresentationEventRole.End, AgentPresentationOrderingPolicy.GetRole(ReplicationAgentWorkPhase.End), "work end ordering role");

        var gridTargetWork = ReplicationAgentWorkPresentation.Start(
            stamp,
            "Build",
            null,
            new ReplicationAgentPresentationGridPoint(4, 5, 6),
            position,
            rotation,
            8,
            "Build");
        Equal(true, gridTargetWork.TargetGrid.HasValue, "work accepts stable grid target");
        Throws<ArgumentException>(
            () => ReplicationAgentWorkPresentation.Start(
                stamp,
                "Chop",
                null,
                null,
                position,
                rotation,
                2,
                "Mining"),
            "work rejects unidentifiable target");
        Throws<ArgumentException>(
            () => ReplicationAgentWorkPresentation.Start(
                stamp,
                "Chop",
                "uid:tree-9",
                null,
                position,
                rotation,
                2,
                string.Empty),
            "work rejects missing animation token");
        Throws<ArgumentOutOfRangeException>(
            () => ReplicationAgentWorkPresentation.Anchor(stamp, position, rotation, 1.1, 0),
            "work anchor rejects progress above one");
        Throws<ArgumentException>(
            () => ReplicationAgentWorkPresentation.Commit(stamp, string.Empty, position, rotation, 1),
            "work commit requires idempotency identity");
        Throws<ArgumentOutOfRangeException>(
            () => ReplicationAgentWorkPresentation.End(
                stamp,
                position,
                rotation,
                1,
                ReplicationAgentPresentationEndReason.None),
            "work end requires reason");

        Throws<ArgumentOutOfRangeException>(
            () => Stamp(1, "uid:worker-1", 0, 1, 0),
            "stamp rejects sentinel activity identity");
        Throws<ArgumentException>(
            () => Stamp(1, " ", 1, 1, 0),
            "stamp rejects blank entity identity");
        Throws<ArgumentOutOfRangeException>(
            () => new ReplicationAgentPresentationVector3(float.NaN, 0, 0),
            "vector rejects NaN");
    }

    private static void TestOrderedLifecycle()
    {
        var state = AgentPresentationOrderingState.Empty("uid:worker-1");
        var start = Stamp(1, "uid:worker-1", 7, 1, 100);
        var decision = AgentPresentationOrderingPolicy.Evaluate(state, start, AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "start applies");
        Equal(AgentPresentationActivityStatus.Active, decision.NextState.Status, "start opens activity");
        Equal(false, decision.CompletesActivity, "start remains active");
        state = decision.NextState;

        decision = AgentPresentationOrderingPolicy.Evaluate(state, start, AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.IgnoreDuplicate, decision.Disposition, "exact repeat is duplicate");

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(1, "uid:worker-1", 7, 1, 99),
            AgentPresentationEventRole.Update);
        Equal(AgentPresentationOrderingDisposition.IgnoreStale, decision.Disposition, "older event tick is stale");

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(1, "uid:worker-1", 7, 2, 110),
            AgentPresentationEventRole.Update);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "path or progress revision applies");
        state = decision.NextState;

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(1, "uid:worker-1", 7, 3, 120),
            AgentPresentationEventRole.Commit);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "commit applies while active");
        Equal(AgentPresentationActivityStatus.Active, decision.NextState.Status, "commit does not end presentation");
        state = decision.NextState;

        var end = Stamp(1, "uid:worker-1", 7, 4, 130);
        decision = AgentPresentationOrderingPolicy.Evaluate(state, end, AgentPresentationEventRole.End);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "end applies to active activity");
        Equal(true, decision.CompletesActivity, "end completes activity");
        Equal(AgentPresentationActivityStatus.Completed, decision.NextState.Status, "end records completion tombstone");
        state = decision.NextState;

        decision = AgentPresentationOrderingPolicy.Evaluate(state, end, AgentPresentationEventRole.End);
        Equal(AgentPresentationOrderingDisposition.IgnoreDuplicate, decision.Disposition, "duplicate end is idempotent");
        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(1, "uid:worker-1", 7, 5, 140),
            AgentPresentationEventRole.Update);
        Equal(AgentPresentationOrderingDisposition.IgnoreCompleted, decision.Disposition, "late update cannot resurrect completed activity");
        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(1, "uid:worker-1", 6, 99, 999),
            AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.IgnoreStale, decision.Disposition, "older activity stays stale despite later tick");
    }

    private static void TestEndBeforeStart()
    {
        var empty = AgentPresentationOrderingState.Empty("uid:worker-1");
        var pendingEnd = Stamp(1, "uid:worker-1", 2, 3, 30);
        var decision = AgentPresentationOrderingPolicy.Evaluate(empty, pendingEnd, AgentPresentationEventRole.End);
        Equal(AgentPresentationOrderingDisposition.BufferUntilStart, decision.Disposition, "end before first start is buffered");
        Equal(AgentPresentationActivityStatus.Empty, decision.NextState.Status, "buffer does not invent active state");

        var state = AgentPresentationOrderingPolicy.Evaluate(
            empty,
            Stamp(1, "uid:worker-1", 1, 1, 10),
            AgentPresentationEventRole.Start).NextState;
        decision = AgentPresentationOrderingPolicy.Evaluate(state, pendingEnd, AgentPresentationEventRole.End);
        Equal(AgentPresentationOrderingDisposition.BufferUntilStart, decision.Disposition, "new activity end waits while old activity remains active");
        Equal(1L, decision.NextState.ActivityId, "buffered end does not terminate unrelated active activity");

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(1, "uid:worker-1", 2, 1, 20),
            AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "matching newer start applies");
        Equal(true, decision.SupersedeActive, "new activity start supersedes old presentation");
        state = decision.NextState;

        decision = AgentPresentationOrderingPolicy.Evaluate(state, pendingEnd, AgentPresentationEventRole.End);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "buffered end applies after its start");
        Equal(AgentPresentationActivityStatus.Completed, decision.NextState.Status, "replayed buffered end completes activity");
    }

    private static void TestEpochAndEntityIsolation()
    {
        var state = AgentPresentationOrderingPolicy.Evaluate(
            AgentPresentationOrderingState.Empty("uid:worker-1"),
            Stamp(4, "uid:worker-1", 8, 1, 100),
            AgentPresentationEventRole.Start).NextState;

        var decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(3, "uid:worker-1", 999, 1, 999),
            AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.IgnoreStale, decision.Disposition, "older epoch is stale regardless of activity");

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(5, "uid:worker-1", 1, 2, 120),
            AgentPresentationEventRole.End);
        Equal(AgentPresentationOrderingDisposition.BufferUntilStart, decision.Disposition, "new epoch end waits for epoch start");
        Equal(4L, decision.NextState.Epoch, "new epoch buffer preserves current cursor");

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(5, "uid:worker-1", 1, 1, 110),
            AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "new epoch start applies");
        Equal(true, decision.ResetEpoch, "new epoch start requests presentation reset");
        Equal(true, decision.SupersedeActive, "new epoch start clears prior active presentation");

        decision = AgentPresentationOrderingPolicy.Evaluate(
            state,
            Stamp(4, "uid:animal-2", 9, 1, 110),
            AgentPresentationEventRole.Start);
        Equal(AgentPresentationOrderingDisposition.RejectEntity, decision.Disposition, "entity streams cannot cross-contaminate");
    }

    private static void TestTeleportLifecycle()
    {
        var empty = AgentPresentationOrderingState.Empty("uid:animal-2");
        var teleport = Stamp(1, "uid:animal-2", 1, 1, 10);
        var decision = AgentPresentationOrderingPolicy.Evaluate(empty, teleport, AgentPresentationEventRole.Instant);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "teleport applies without begin");
        Equal(true, decision.CompletesActivity, "teleport is self-contained");
        Equal(AgentPresentationActivityStatus.Completed, decision.NextState.Status, "teleport leaves tombstone");

        var active = AgentPresentationOrderingPolicy.Evaluate(
            AgentPresentationOrderingState.Empty("uid:animal-2"),
            Stamp(1, "uid:animal-2", 2, 1, 20),
            AgentPresentationEventRole.Start).NextState;
        decision = AgentPresentationOrderingPolicy.Evaluate(
            active,
            Stamp(1, "uid:animal-2", 2, 2, 21),
            AgentPresentationEventRole.Instant);
        Equal(AgentPresentationOrderingDisposition.Apply, decision.Disposition, "teleport corrects active motion immediately");
        Equal(true, decision.SupersedeActive, "teleport tells caller to clear active interpolation");
    }

    private static ReplicationAgentPresentationStamp Stamp(
        long epoch,
        string entityId,
        long activityId,
        long revision,
        long eventTick)
    {
        return new ReplicationAgentPresentationStamp(epoch, entityId, activityId, revision, eventTick);
    }

    private static ReplicationAgentPresentationVector3 Vector(float x, float y, float z)
    {
        return new ReplicationAgentPresentationVector3(x, y, z);
    }

    private static ReplicationAgentPresentationQuaternion Rotation()
    {
        return new ReplicationAgentPresentationQuaternion(0, 0, 0, 1);
    }
}
