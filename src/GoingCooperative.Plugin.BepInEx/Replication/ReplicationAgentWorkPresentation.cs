using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationAgentWorkPresentationDeltaKind = "AgentWorkPresentation";
        private const float ReplicationSemanticWorkNetworkSilenceSeconds = 60f;
        private const float ReplicationSemanticWorkAnchorSeconds = 5f;
        private const float ReplicationSemanticWorkInitialVisualSampleSeconds = 0.2f;
        private const float ReplicationSemanticWorkBufferedTerminalSeconds = 30f;
        private const int ReplicationSemanticWorkBufferedTerminalLimit = 128;
        private const float ReplicationSemanticWorkVisualHoldSeconds = 0.5f;

        private sealed class ReplicationSemanticPendingHostWorkState
        {
            public object Action = null!;
            public MethodBase SourceMethod = null!;
            public string EntityId = string.Empty;
            public string AnimationToken = string.Empty;
            public string AnimationMode = "None";
            public string AnimatorStateDetail = string.Empty;
            public string HandItemId = string.Empty;
            public bool IsSequenced;
            public bool HasDeferredAnimation;
            public bool IsClaimed;
            public float CreatedRealtime;
        }

        private sealed class ReplicationSemanticHostWorkState
        {
            public object Action = null!;
            public string EntityId = string.Empty;
            public string WorkKind = string.Empty;
            public long ActivityId;
            public long Revision;
            public float StartedRealtime;
            public float DurationSeconds;
            public float NextAnchorRealtime;
            public float NextVisualSampleRealtime;
            public string AnimationToken = string.Empty;
            public string AnimatorStateDetail = string.Empty;
            public string HandItemId = string.Empty;
            public bool InitialVisualSampleSent;
        }

        private sealed class ReplicationSemanticClientWorkState
        {
            public string EntityId = string.Empty;
            public string WorkKind = string.Empty;
            public string AnimationToken = string.Empty;
            public string HandItemId = string.Empty;
            public long ActivityId;
            public float ExpiresRealtime;
            public float NextHoldRealtime;
        }

        private sealed class ReplicationSemanticBufferedClientWorkDelta
        {
            public ReplicationWorldObjectDelta Delta = null!;
            public long Epoch;
            public long ActivityId;
            public long Revision;
            public long EventTick;
            public float ExpiresRealtime;
        }

        private static readonly Dictionary<object, ReplicationSemanticPendingHostWorkState> ReplicationSemanticPendingHostWorkByAction =
            new Dictionary<object, ReplicationSemanticPendingHostWorkState>();
        private static readonly Dictionary<string, ReplicationSemanticPendingHostWorkState> ReplicationSemanticPendingHostWorkByEntityId =
            new Dictionary<string, ReplicationSemanticPendingHostWorkState>(StringComparer.Ordinal);
        private static readonly Dictionary<object, ReplicationSemanticHostWorkState> ReplicationSemanticHostWorkByAction =
            new Dictionary<object, ReplicationSemanticHostWorkState>();
        private static readonly Dictionary<string, ReplicationSemanticHostWorkState> ReplicationSemanticHostWorkByEntityId =
            new Dictionary<string, ReplicationSemanticHostWorkState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationSemanticClientWorkState> ReplicationSemanticClientWorkByEntityId =
            new Dictionary<string, ReplicationSemanticClientWorkState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AgentPresentationOrderingState> ReplicationSemanticClientWorkOrderingByEntityId =
            new Dictionary<string, AgentPresentationOrderingState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationSemanticBufferedClientWorkDelta> ReplicationSemanticBufferedClientWorkByEntityId =
            new Dictionary<string, ReplicationSemanticBufferedClientWorkDelta>(StringComparer.Ordinal);
        private static readonly List<string> ReplicationSemanticExpiredClientWorkEntityIds = new List<string>();
        private static readonly List<string> ReplicationSemanticExpiredBufferedWorkEntityIds = new List<string>();

        private static long replicationSemanticWorkActivitySequence;
        private static long replicationSemanticWorkStartsSent;
        private static long replicationSemanticWorkEndsSent;
        private static long replicationSemanticWorkStartsApplied;
        private static long replicationSemanticWorkEndsApplied;
        private static long replicationSemanticWorkAnchorsSent;
        private static long replicationSemanticWorkAnchorsApplied;
        private static long replicationSemanticWorkVisualAnchorsSent;
        private static long replicationSemanticWorkVisualAnchorsApplied;
        private static long replicationSemanticWorkTerminalsBuffered;
        private static long replicationSemanticWorkTerminalsReplayed;
        private static long replicationSemanticWorkFallbacks;
        private static long replicationSemanticWorkWatchdogs;

        private static bool IsReplicationSemanticMigratedWork(string goalId, string actionId)
        {
            return string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal)
                    && string.Equals(actionId, "StartObtaining", StringComparison.Ordinal)
                || string.Equals(goalId, "ConstructBuildingGoal", StringComparison.Ordinal)
                    && string.Equals(actionId, "ConstructAction", StringComparison.Ordinal);
        }

        private static bool IsReplicationSemanticMigratedWorkGoal(string goalId)
        {
            return string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "ConstructBuildingGoal", StringComparison.Ordinal);
        }

        private static bool IsReplicationSemanticWorkCandidateAction(string actionId)
        {
            return string.Equals(actionId, "StartObtaining", StringComparison.Ordinal)
                || string.Equals(actionId, "ConstructAction", StringComparison.Ordinal);
        }

        private static bool CanUseReplicationSemanticHostPresentation()
        {
            return replicationConfigSemanticAgentPresentation
                && replicationConfigEnabled
                && replicationConfigHostMode
                && replicationRuntimeStarted
                && replicationRemoteHelloReceived
                && replicationTransport != null;
        }

        private static bool TryCaptureReplicationSemanticWorkAnimation(
            object action,
            string trigger,
            string animationMode,
            bool isSequenced,
            MethodBase sourceMethod)
        {
            if (!CanUseReplicationSemanticHostPresentation() || string.IsNullOrWhiteSpace(trigger))
            {
                return false;
            }

            if (ReplicationSemanticHostWorkByAction.ContainsKey(action))
            {
                return true;
            }

            TryReadReplicationGoapActionInfo(action, out var actionId, out _, out _, out _, out _, out _, out _);
            if (!IsReplicationSemanticWorkCandidateAction(actionId))
            {
                return false;
            }

            if (!ReplicationSemanticPendingHostWorkByAction.TryGetValue(action, out var pending))
            {
                pending = new ReplicationSemanticPendingHostWorkState
                {
                    Action = action,
                    SourceMethod = sourceMethod,
                    CreatedRealtime = Time.realtimeSinceStartup
                };
                ReplicationSemanticPendingHostWorkByAction[action] = pending;
            }

            pending.AnimationToken = trigger.Trim();
            pending.AnimationMode = string.IsNullOrWhiteSpace(animationMode) ? "None" : animationMode;
            pending.IsSequenced = isSequenced;
            pending.HasDeferredAnimation = true;
            pending.SourceMethod = sourceMethod;
            return true;
        }

        private static bool TryRecordReplicationSemanticWorkPhase(
            object action,
            string phase,
            object[]? lifecycleArguments,
            MethodBase lifecycleMethod)
        {
            if (!replicationConfigSemanticAgentPresentation)
            {
                return false;
            }

            if (phase == "SetGoal")
            {
                TryClaimReplicationSemanticWork(action, lifecycleMethod);
                return false;
            }

            if (phase == "Init")
            {
                if (ReplicationSemanticHostWorkByAction.ContainsKey(action))
                {
                    return true;
                }

                if (!ReplicationSemanticPendingHostWorkByAction.TryGetValue(action, out var pending)
                    || !pending.IsClaimed)
                {
                    return false;
                }

                if (TryBeginReplicationSemanticWork(action, pending, "GoapAction.Init"))
                {
                    ReleaseReplicationSemanticPendingHostWork(pending);
                    return true;
                }

                FlushReplicationSemanticPendingHostWorkToLegacy(pending, "semantic-start-fallback");
                ReleaseReplicationSemanticPendingHostWork(pending);
                return false;
            }

            if (phase != "Complete")
            {
                return false;
            }

            if (!ReplicationSemanticHostWorkByAction.TryGetValue(action, out var state))
            {
                if (ReplicationSemanticPendingHostWorkByAction.TryGetValue(action, out var pending))
                {
                    ReleaseReplicationSemanticPendingHostWork(pending);
                }

                return false;
            }

            ResolveReplicationSemanticWorkCompletion(
                lifecycleArguments,
                out var endReason,
                out var progress,
                out var completionStatus);
            SendReplicationSemanticWorkEnd(
                state,
                endReason,
                progress,
                "GoapAction.Complete-" + FormatReplicationWorldObjectDetailToken(completionStatus));
            return true;
        }

        private static void TryClaimReplicationSemanticWork(object action, MethodBase sourceMethod)
        {
            if (!CanUseReplicationSemanticHostPresentation()
                || !TryReadReplicationGoapActionInfo(
                    action,
                    out var actionId,
                    out var goalId,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _)
                || !IsReplicationSemanticMigratedWork(goalId, actionId)
                || !TryGetReplicationGoapActionEntityId(action, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId))
            {
                if (ReplicationSemanticPendingHostWorkByAction.TryGetValue(action, out var rejected))
                {
                    FlushReplicationSemanticPendingHostWorkToLegacy(rejected, "semantic-claim-rejected");
                    ReleaseReplicationSemanticPendingHostWork(rejected);
                }

                return;
            }

            if (!ReplicationSemanticPendingHostWorkByAction.TryGetValue(action, out var pending))
            {
                pending = new ReplicationSemanticPendingHostWorkState
                {
                    Action = action,
                    SourceMethod = sourceMethod,
                    AnimationToken = string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal) ? "Mining" : "Build",
                    CreatedRealtime = Time.realtimeSinceStartup
                };
                ReplicationSemanticPendingHostWorkByAction[action] = pending;
            }

            if (ReplicationSemanticPendingHostWorkByEntityId.TryGetValue(entityId, out var replaced)
                && !ReferenceEquals(replaced, pending))
            {
                ReleaseReplicationSemanticPendingHostWork(replaced);
            }

            pending.EntityId = entityId;
            pending.IsClaimed = true;
            ReplicationSemanticPendingHostWorkByEntityId[entityId] = pending;
        }

        private static bool TryCaptureReplicationSemanticWorkControllerAnimation(
            string entityId,
            string animationToken,
            string animatorStateDetail,
            string handItemId)
        {
            if (!CanUseReplicationSemanticHostPresentation()
                || string.IsNullOrWhiteSpace(entityId)
                || string.IsNullOrWhiteSpace(animationToken))
            {
                return false;
            }

            if (ReplicationSemanticPendingHostWorkByEntityId.TryGetValue(entityId, out var pending)
                && pending.IsClaimed
                && (string.IsNullOrWhiteSpace(pending.AnimationToken)
                    || string.Equals(pending.AnimationToken, animationToken, StringComparison.Ordinal)))
            {
                pending.AnimationToken = animationToken;
                pending.AnimatorStateDetail = animatorStateDetail ?? string.Empty;
                pending.HandItemId = handItemId ?? string.Empty;
                return true;
            }

            if (ReplicationSemanticHostWorkByEntityId.TryGetValue(entityId, out var active)
                && string.Equals(active.AnimationToken, animationToken, StringComparison.Ordinal))
            {
                active.AnimatorStateDetail = animatorStateDetail ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(handItemId))
                {
                    active.HandItemId = handItemId;
                }

                return true;
            }

            return false;
        }

        private static void FlushReplicationSemanticPendingHostWorkToLegacy(
            ReplicationSemanticPendingHostWorkState pending,
            string reason)
        {
            if (!pending.HasDeferredAnimation
                || ReferenceEquals(instance, null)
                || pending.SourceMethod == null)
            {
                replicationSemanticWorkFallbacks++;
                return;
            }

            var resolved = TryGetReplicationGoapActionEntityId(pending.Action, out var entityId, out var entityDetail);
            if ((!resolved || string.IsNullOrWhiteSpace(entityId)) && !string.IsNullOrWhiteSpace(pending.EntityId))
            {
                entityId = pending.EntityId;
                entityDetail = "semantic-pending-entity-cache";
                resolved = true;
            }

            if (!resolved || string.IsNullOrWhiteSpace(entityId))
            {
                replicationSemanticWorkFallbacks++;
                return;
            }

            RecordReplicationActionAnimationDelta(
                instance!,
                pending.SourceMethod,
                entityId,
                pending.AnimationToken,
                pending.AnimationMode,
                pending.IsSequenced,
                entityDetail + " fallback=" + reason);
            replicationSemanticWorkFallbacks++;
        }

        private static void ReleaseReplicationSemanticPendingHostWork(ReplicationSemanticPendingHostWorkState pending)
        {
            ReplicationSemanticPendingHostWorkByAction.Remove(pending.Action);
            if (!string.IsNullOrWhiteSpace(pending.EntityId)
                && ReplicationSemanticPendingHostWorkByEntityId.TryGetValue(pending.EntityId, out var current)
                && ReferenceEquals(current, pending))
            {
                ReplicationSemanticPendingHostWorkByEntityId.Remove(pending.EntityId);
            }
        }

        private static void ResolveReplicationSemanticWorkCompletion(
            object[]? lifecycleArguments,
            out ReplicationAgentPresentationEndReason reason,
            out float progress,
            out string status)
        {
            status = lifecycleArguments != null && lifecycleArguments.Length > 0 && lifecycleArguments[0] != null
                ? lifecycleArguments[0].ToString() ?? string.Empty
                : "Success";
            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
            {
                reason = ReplicationAgentPresentationEndReason.Completed;
                progress = 1f;
                return;
            }

            if (string.Equals(status, "Jump", StringComparison.OrdinalIgnoreCase))
            {
                reason = ReplicationAgentPresentationEndReason.Replaced;
                progress = 0f;
                return;
            }

            reason = string.Equals(status, "None", StringComparison.OrdinalIgnoreCase)
                ? ReplicationAgentPresentationEndReason.Cancelled
                : ReplicationAgentPresentationEndReason.Failed;
            progress = 0f;
        }

        private static bool TryBeginReplicationSemanticWork(
            object action,
            ReplicationSemanticPendingHostWorkState pending,
            string source)
        {
            try
            {
                return TryBeginReplicationSemanticWorkCore(action, pending, source);
            }
            catch (Exception ex)
            {
                replicationSemanticWorkFallbacks++;
                instance?.LogReplicationInfo("Going Cooperative semantic work start fell back source="
                    + FormatReplicationWorldObjectDetailToken(source)
                    + " error="
                    + ex.GetType().Name
                    + " message="
                    + TrimFingerprintText(ex.Message, 300));
                return false;
            }
        }

        private static bool TryBeginReplicationSemanticWorkCore(
            object action,
            ReplicationSemanticPendingHostWorkState pending,
            string source)
        {
            if (!CanUseReplicationSemanticHostPresentation()
                || !TryGetReplicationGoapActionEntityId(action, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId)
                || !TryReadReplicationGoapActionInfo(
                    action,
                    out var actionId,
                    out var goalId,
                    out var targetId,
                    out _,
                    out var targetX,
                    out var targetY,
                    out var targetZ)
                || !IsReplicationSemanticMigratedWork(goalId, actionId)
                || !TryResolveReplicationSemanticWorkTarget(action, ref targetId, ref targetX, ref targetY, ref targetZ)
                || !TryReadReplicationSemanticWorkDuration(action, out var durationSeconds)
                || durationSeconds <= 0.01f
                || !TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _)
                || view == null
                || !TryGetReplicationTransform(view, out var transform)
                || transform == null)
            {
                replicationSemanticWorkFallbacks++;
                return false;
            }

            if (ReplicationSemanticHostWorkByEntityId.TryGetValue(entityId, out var replaced))
            {
                SendReplicationSemanticWorkEnd(
                    replaced,
                    ReplicationAgentPresentationEndReason.Replaced,
                    0f,
                    "semantic-work-replaced");
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                replicationSemanticWorkFallbacks++;
                return false;
            }

            var activityId = ++replicationSemanticWorkActivitySequence;
            var epoch = Math.Max(0, current.multiplayerSaveTransfer.Epoch);
            var targetEntityId = targetId == 0L
                ? null
                : "uid:" + targetId.ToString(CultureInfo.InvariantCulture);
            var position = transform.position;
            var rotation = transform.rotation;
            var animationToken = string.IsNullOrWhiteSpace(pending.AnimationToken)
                ? string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal) ? "Mining" : "Build"
                : pending.AnimationToken;
            var handItemId = !string.IsNullOrWhiteSpace(pending.HandItemId)
                ? pending.HandItemId
                : TryResolveReplicationActiveHandItemId(action, entityId, out var resolvedHandItemId, out _)
                    ? resolvedHandItemId
                    : string.Empty;
            ReplicationAgentWorkPresentation.Start(
                new ReplicationAgentPresentationStamp(epoch, entityId, activityId, 1L, Math.Max(0, Time.frameCount)),
                string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal) ? "Chop" : "Build",
                targetEntityId,
                new ReplicationAgentPresentationGridPoint(targetX, targetY, targetZ),
                new ReplicationAgentPresentationVector3(position.x, position.y, position.z),
                new ReplicationAgentPresentationQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                durationSeconds,
                animationToken,
                handItemId);

            var state = new ReplicationSemanticHostWorkState
            {
                Action = action,
                EntityId = entityId,
                WorkKind = string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal) ? "Chop" : "Build",
                ActivityId = activityId,
                Revision = 1L,
                StartedRealtime = Time.realtimeSinceStartup,
                DurationSeconds = durationSeconds,
                NextAnchorRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkAnchorSeconds,
                NextVisualSampleRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkInitialVisualSampleSeconds,
                AnimationToken = animationToken,
                AnimatorStateDetail = pending.AnimatorStateDetail,
                HandItemId = handItemId
            };
            ReplicationSemanticHostWorkByAction[action] = state;
            ReplicationSemanticHostWorkByEntityId[entityId] = state;

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationAgentWorkPresentationDeltaKind,
                uniqueId,
                state.WorkKind,
                targetX,
                targetY,
                targetZ,
                "entityId=" + entityId
                    + " phase=Start"
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " activityId=" + activityId.ToString(CultureInfo.InvariantCulture)
                    + " revision=1"
                    + " eventTick=" + Math.Max(0, Time.frameCount).ToString(CultureInfo.InvariantCulture)
                    + " durationMs=" + Mathf.Max(1, Mathf.RoundToInt(durationSeconds * 1000f)).ToString(CultureInfo.InvariantCulture)
                    + " workKind=" + state.WorkKind
                    + " targetId=" + targetId.ToString(CultureInfo.InvariantCulture)
                    + " animationB64=" + EncodeReplicationDetailBase64(animationToken)
                    + " handItemB64=" + EncodeReplicationDetailBase64(handItemId)
                    + " animatorStateB64=" + EncodeReplicationDetailBase64(pending.AnimatorStateDetail)
                    + " source=" + FormatReplicationWorldObjectDetailToken(source)));
            replicationSemanticWorkStartsSent++;
            return true;
        }

        private static void SendReplicationSemanticWorkEnd(
            ReplicationSemanticHostWorkState state,
            ReplicationAgentPresentationEndReason reason,
            float progress,
            string source)
        {
            ReplicationSemanticHostWorkByAction.Remove(state.Action);
            if (ReplicationSemanticHostWorkByEntityId.TryGetValue(state.EntityId, out var currentState)
                && ReferenceEquals(currentState, state))
            {
                ReplicationSemanticHostWorkByEntityId.Remove(state.EntityId);
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var epoch = Math.Max(0, current.multiplayerSaveTransfer.Epoch);
            var revision = ++state.Revision;
            var eventTick = Math.Max(0, Time.frameCount);
            var clampedProgress = Mathf.Clamp01(progress);
            var uniqueId = TryParseReplicationEntityNumericId(state.EntityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationAgentWorkPresentationDeltaKind,
                uniqueId,
                state.WorkKind,
                0,
                0,
                0,
                "entityId=" + state.EntityId
                    + " phase=End"
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " activityId=" + state.ActivityId.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                    + " progressPermille=" + Mathf.RoundToInt(clampedProgress * 1000f).ToString(CultureInfo.InvariantCulture)
                    + " reason=" + reason
                    + " source=" + FormatReplicationWorldObjectDetailToken(source)));
            replicationSemanticWorkEndsSent++;
        }

        private static bool SendReplicationSemanticWorkAnchor(
            ReplicationSemanticHostWorkState state,
            float now,
            bool initialVisualSample)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return false;
            }

            // The vanilla trigger is consumed before Unity's Animator enters the work state.
            // Sample on a later frame and send that authoritative state rather than guessing
            // which controller state the trigger should have selected on the client.
            var animatorStateDetail = CaptureReplicationActiveActionAnimatorState(state.EntityId);
            if (initialVisualSample && string.IsNullOrWhiteSpace(animatorStateDetail))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(animatorStateDetail))
            {
                state.AnimatorStateDetail = animatorStateDetail;
            }

            var revision = ++state.Revision;
            var eventTick = Math.Max(0, Time.frameCount);
            var epoch = Math.Max(0, current.multiplayerSaveTransfer.Epoch);
            var uniqueId = TryParseReplicationEntityNumericId(state.EntityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                now,
                ReplicationAgentWorkPresentationDeltaKind,
                uniqueId,
                state.WorkKind,
                0,
                0,
                0,
                "entityId=" + state.EntityId
                    + " phase=Anchor"
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " activityId=" + state.ActivityId.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                    + " animatorStateB64=" + EncodeReplicationDetailBase64(state.AnimatorStateDetail)
                    + " visualSample=" + (initialVisualSample ? "initial" : "periodic")
                    + " source=semantic-work-anchor"));
            state.NextAnchorRealtime = now + ReplicationSemanticWorkAnchorSeconds;
            replicationSemanticWorkAnchorsSent++;
            if (!string.IsNullOrWhiteSpace(state.AnimatorStateDetail))
            {
                replicationSemanticWorkVisualAnchorsSent++;
            }

            return true;
        }

        private static void TryEndReplicationSemanticWorkForEntity(string entityId, string goalId, bool hasStarted)
        {
            if (!replicationConfigSemanticAgentPresentation
                || hasStarted
                || !IsReplicationSemanticMigratedWorkGoal(goalId))
            {
                return;
            }

            if (ReplicationSemanticPendingHostWorkByEntityId.TryGetValue(entityId, out var pending))
            {
                ReleaseReplicationSemanticPendingHostWork(pending);
            }

            if (!ReplicationSemanticHostWorkByEntityId.TryGetValue(entityId, out var state))
            {
                return;
            }

            SendReplicationSemanticWorkEnd(state, ReplicationAgentPresentationEndReason.Cancelled, 0f, "WorkerBehaviour.GoalUpdated");
        }

        private static bool TryResolveReplicationSemanticWorkTarget(
            object action,
            ref long targetId,
            ref int targetX,
            ref int targetY,
            ref int targetZ)
        {
            if (!TryGetReplicationGoapSelectedTargetFromAction(action, out var target) || target == null)
            {
                return false;
            }

            var ignoredBlueprintId = string.Empty;
            ReadReplicationGoapTargetIdentity(
                target,
                ref targetId,
                ref ignoredBlueprintId,
                ref targetX,
                ref targetY,
                ref targetZ);
            var resolved = targetId != 0L;
            if (TryReadVec3IntLikeValue(target, out var directX, out var directY, out var directZ)
                || TryReadReplicationWorldObjectGridPosition(target, out directX, out directY, out directZ))
            {
                targetX = directX;
                targetY = directY;
                targetZ = directZ;
                resolved = true;
            }

            var nestedNames = new[]
            {
                "ObjectInstance", "objectInstance",
                "ReachablePosition", "reachablePosition",
                "PrecisePosition", "precisePosition"
            };
            for (var i = 0; i < nestedNames.Length; i++)
            {
                if (!TryReadInstanceMemberValue(target, nestedNames[i], out var nested) || nested == null)
                {
                    continue;
                }

                ReadReplicationGoapTargetIdentity(
                    nested,
                    ref targetId,
                    ref ignoredBlueprintId,
                    ref targetX,
                    ref targetY,
                    ref targetZ);
                resolved |= targetId != 0L;
                if (TryReadVec3IntLikeValue(nested, out var nestedX, out var nestedY, out var nestedZ)
                    || TryReadReplicationWorldObjectGridPosition(nested, out nestedX, out nestedY, out nestedZ))
                {
                    targetX = nestedX;
                    targetY = nestedY;
                    targetZ = nestedZ;
                    resolved = true;
                }
            }

            return resolved;
        }

        private static bool TryReadReplicationSemanticWorkDuration(object action, out float durationSeconds)
        {
            durationSeconds = 0f;
            var type = action.GetType();
            while (type != null && type != typeof(object))
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < fields.Length; i++)
                {
                    if (!typeof(Delegate).IsAssignableFrom(fields[i].FieldType))
                    {
                        continue;
                    }

                    Delegate? callback;
                    try
                    {
                        callback = fields[i].GetValue(action) as Delegate;
                    }
                    catch
                    {
                        continue;
                    }

                    if (callback == null)
                    {
                        continue;
                    }

                    var invocations = callback.GetInvocationList();
                    for (var invocationIndex = 0; invocationIndex < invocations.Length; invocationIndex++)
                    {
                        var closure = invocations[invocationIndex].Target;
                        if (closure != null
                            && TryReadInstanceMemberValue(closure, "totalTime", out var totalTime)
                            && TryConvertReplicationSemanticWorkDuration(totalTime, out durationSeconds))
                        {
                            return true;
                        }
                    }
                }

                type = type.BaseType;
            }

            if (TryGetReplicationGoapSelectedTargetFromAction(action, out var target)
                && TryReadInstanceMemberValue(target, "ObjectInstance", out var objectInstance)
                && objectInstance != null
                && TryReadInstanceMemberValue(objectInstance, "totalWorkerBuildTime", out var buildTime)
                && TryConvertReplicationSemanticWorkDuration(buildTime, out durationSeconds))
            {
                return true;
            }

            return false;
        }

        private static bool TryConvertReplicationSemanticWorkDuration(object? value, out float durationSeconds)
        {
            durationSeconds = 0f;
            if (value == null)
            {
                return false;
            }

            try
            {
                durationSeconds = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return !float.IsNaN(durationSeconds) && !float.IsInfinity(durationSeconds) && durationSeconds > 0.01f;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetReplicationGoapSelectedTargetFromAction(object action, out object target)
        {
            target = null!;
            if ((!TryInvokeReplicationObjectMethod(action, "get_Goal", out var goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "Goal", out goal) || goal == null))
            {
                return false;
            }

            return TryGetReplicationGoapSelectedTarget(goal, out target);
        }

        private static bool TryApplyReplicationSemanticWorkDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigSemanticAgentPresentation
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out var phase)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "epoch", out var epoch)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "activityId", out var activityText)
                || !long.TryParse(activityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var activityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "revision", out var revisionText)
                || !long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "eventTick", out var eventTick)
                || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "semantic-work-invalid detail=" + delta.Detail;
                return false;
            }

            if (!string.Equals(phase, "Start", StringComparison.Ordinal)
                && !string.Equals(phase, "Anchor", StringComparison.Ordinal)
                && !string.Equals(phase, "End", StringComparison.Ordinal))
            {
                detail = "semantic-work-phase-unsupported=" + phase;
                return false;
            }

            if (!ReplicationSemanticClientWorkOrderingByEntityId.TryGetValue(entityId, out var ordering))
            {
                ordering = AgentPresentationOrderingState.Empty(entityId);
            }

            var stamp = new ReplicationAgentPresentationStamp(epoch, entityId, activityId, revision, eventTick);
            var role = string.Equals(phase, "Start", StringComparison.Ordinal)
                ? AgentPresentationEventRole.Start
                : string.Equals(phase, "End", StringComparison.Ordinal)
                    ? AgentPresentationEventRole.End
                    : AgentPresentationEventRole.Update;
            var decision = AgentPresentationOrderingPolicy.Evaluate(ordering, stamp, role);
            if (decision.Disposition != AgentPresentationOrderingDisposition.Apply)
            {
                if (decision.Disposition == AgentPresentationOrderingDisposition.BufferUntilStart)
                {
                    BufferReplicationSemanticClientWorkDelta(
                        entityId,
                        delta,
                        epoch,
                        activityId,
                        revision,
                        eventTick);
                    detail = "semantic-work-buffered-until-start entityId=" + entityId;
                    return true;
                }

                detail = "semantic-work-ordering=" + decision.Disposition + " entityId=" + entityId;
                return true;
            }

            if (string.Equals(phase, "Start", StringComparison.Ordinal))
            {
                if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
                {
                    detail = "semantic-work-view-missing entityId=" + entityId + " " + viewDetail;
                    return false;
                }

                var animationToken = TryReadReplicationWorldObjectDetailToken(delta.Detail, "animationB64", out var animationB64)
                    && TryDecodeReplicationDetailBase64(animationB64, out var decodedAnimation)
                    ? decodedAnimation
                    : string.Equals(delta.BlueprintId, "Chop", StringComparison.Ordinal) ? "Mining" : "Build";
                var handItemId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "handItemB64", out var handItemB64)
                    && TryDecodeReplicationDetailBase64(handItemB64, out var decodedHandItem)
                    ? decodedHandItem
                    : string.Empty;
                var animatorStateDetail = TryReadReplicationWorldObjectDetailToken(delta.Detail, "animatorStateB64", out var animatorStateB64)
                    && TryDecodeReplicationDetailBase64(animatorStateB64, out var decodedAnimatorState)
                    ? decodedAnimatorState
                    : string.Empty;

                if (decision.SupersedeActive || decision.ResetEpoch)
                {
                    ReplicationSemanticClientWorkByEntityId.Remove(entityId);
                    ClearReplicationPuppetActionVisual(entityId);
                }

                if (TryResolveReplicationAnimator(view, out var animator) && animator != null)
                {
                    ResetReplicationSemanticLocomotion(animator);
                }

                var visualDetail = ApplyReplicationPuppetActionVisual(
                    entityId,
                    animationToken,
                    animatorStateDetail,
                    handItemId,
                    force: true,
                    triggerAnimation: true,
                    semanticWorkPresentation: true);
                ReplicationSemanticClientWorkByEntityId[entityId] = new ReplicationSemanticClientWorkState
                {
                    EntityId = entityId,
                    WorkKind = delta.BlueprintId,
                    AnimationToken = animationToken,
                    HandItemId = handItemId,
                    ActivityId = activityId,
                    ExpiresRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkNetworkSilenceSeconds,
                    NextHoldRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkVisualHoldSeconds
                };
                ReplicationSemanticClientWorkOrderingByEntityId[entityId] = decision.NextState;
                replicationSemanticWorkStartsApplied++;
                detail = "ok semantic-work-start entityId=" + entityId + " visual=" + visualDetail;
                ReplayReplicationSemanticBufferedClientWorkDelta(entityId, epoch, activityId, ref detail);
                return true;
            }

            if (string.Equals(phase, "Anchor", StringComparison.Ordinal))
            {
                ReplicationSemanticClientWorkOrderingByEntityId[entityId] = decision.NextState;
                var visualDetail = string.Empty;
                if (ReplicationSemanticClientWorkByEntityId.TryGetValue(entityId, out var active)
                    && active.ActivityId == activityId)
                {
                    active.ExpiresRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkNetworkSilenceSeconds;
                    var animatorStateDetail = TryReadReplicationWorldObjectDetailToken(delta.Detail, "animatorStateB64", out var animatorStateB64)
                        && TryDecodeReplicationDetailBase64(animatorStateB64, out var decodedAnimatorState)
                        ? decodedAnimatorState
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(animatorStateDetail)
                        && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _)
                        && view != null)
                    {
                        visualDetail = ApplyReplicationAnimatorStateDetail(view, animatorStateDetail);
                        ApplyReplicationPuppetActionAnimatorParameters(view, active.AnimationToken);
                        replicationSemanticWorkVisualAnchorsApplied++;
                    }
                }

                replicationSemanticWorkAnchorsApplied++;
                detail = "ok semantic-work-anchor entityId=" + entityId
                    + (string.IsNullOrWhiteSpace(visualDetail) ? string.Empty : " visual=" + visualDetail);
                return true;
            }

            if (string.Equals(phase, "End", StringComparison.Ordinal))
            {
                ReplicationSemanticClientWorkByEntityId.Remove(entityId);
                ReplicationSemanticClientWorkOrderingByEntityId[entityId] = decision.NextState;
                var clearDetail = ClearReplicationPuppetActionVisual(entityId);
                replicationSemanticWorkEndsApplied++;
                detail = "ok semantic-work-end entityId=" + entityId + " clear=" + clearDetail;
                return true;
            }

            detail = "semantic-work-unreachable-phase=" + phase;
            return true;
        }

        private static void BufferReplicationSemanticClientWorkDelta(
            string entityId,
            ReplicationWorldObjectDelta delta,
            long epoch,
            long activityId,
            long revision,
            long eventTick)
        {
            if (ReplicationSemanticBufferedClientWorkByEntityId.TryGetValue(entityId, out var existing)
                && (existing.Epoch > epoch
                    || existing.Epoch == epoch && existing.ActivityId > activityId
                    || existing.Epoch == epoch && existing.ActivityId == activityId && existing.Revision > revision
                    || existing.Epoch == epoch && existing.ActivityId == activityId && existing.Revision == revision && existing.EventTick >= eventTick))
            {
                return;
            }

            if (!ReplicationSemanticBufferedClientWorkByEntityId.ContainsKey(entityId)
                && ReplicationSemanticBufferedClientWorkByEntityId.Count >= ReplicationSemanticWorkBufferedTerminalLimit)
            {
                string? oldestEntityId = null;
                var oldestExpiry = float.MaxValue;
                foreach (var pair in ReplicationSemanticBufferedClientWorkByEntityId)
                {
                    if (pair.Value.ExpiresRealtime < oldestExpiry)
                    {
                        oldestEntityId = pair.Key;
                        oldestExpiry = pair.Value.ExpiresRealtime;
                    }
                }

                if (oldestEntityId != null)
                {
                    ReplicationSemanticBufferedClientWorkByEntityId.Remove(oldestEntityId);
                }
            }

            ReplicationSemanticBufferedClientWorkByEntityId[entityId] = new ReplicationSemanticBufferedClientWorkDelta
            {
                Delta = delta,
                Epoch = epoch,
                ActivityId = activityId,
                Revision = revision,
                EventTick = eventTick,
                ExpiresRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkBufferedTerminalSeconds
            };
            replicationSemanticWorkTerminalsBuffered++;
        }

        private static void ReplayReplicationSemanticBufferedClientWorkDelta(
            string entityId,
            long epoch,
            long activityId,
            ref string detail)
        {
            if (!ReplicationSemanticBufferedClientWorkByEntityId.TryGetValue(entityId, out var buffered)
                || buffered.Epoch != epoch
                || buffered.ActivityId != activityId)
            {
                return;
            }

            ReplicationSemanticBufferedClientWorkByEntityId.Remove(entityId);
            if (TryApplyReplicationSemanticWorkDelta(buffered.Delta, out var replayDetail))
            {
                replicationSemanticWorkTerminalsReplayed++;
                detail += " replay=" + replayDetail;
            }
        }

        private static void ProcessReplicationSemanticAgentWorkPresentation()
        {
            if (!replicationConfigSemanticAgentPresentation)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (replicationConfigHostMode)
            {
                if (!CanUseReplicationSemanticHostPresentation())
                {
                    return;
                }

                foreach (var pair in ReplicationSemanticHostWorkByEntityId)
                {
                    var state = pair.Value;
                    if (!state.InitialVisualSampleSent && now >= state.NextVisualSampleRealtime)
                    {
                        if (SendReplicationSemanticWorkAnchor(state, now, initialVisualSample: true))
                        {
                            state.InitialVisualSampleSent = true;
                        }
                        else
                        {
                            state.NextVisualSampleRealtime = now + ReplicationSemanticWorkInitialVisualSampleSeconds;
                        }
                    }
                    else if (now >= state.NextAnchorRealtime)
                    {
                        SendReplicationSemanticWorkAnchor(state, now, initialVisualSample: false);
                    }
                }

                return;
            }

            if (ReplicationSemanticClientWorkByEntityId.Count == 0
                && ReplicationSemanticBufferedClientWorkByEntityId.Count == 0)
            {
                return;
            }

            ReplicationSemanticExpiredClientWorkEntityIds.Clear();
            foreach (var pair in ReplicationSemanticClientWorkByEntityId)
            {
                var state = pair.Value;
                if (now >= state.ExpiresRealtime)
                {
                    ReplicationSemanticExpiredClientWorkEntityIds.Add(pair.Key);
                    continue;
                }

                if (now < state.NextHoldRealtime)
                {
                    continue;
                }

                state.NextHoldRealtime = now + ReplicationSemanticWorkVisualHoldSeconds;
                if (TryFindReplicationAnimatedAgentViewByEntityId(pair.Key, out var view, out _) && view != null)
                {
                    ApplyReplicationPuppetActionAnimatorParameters(view, state.AnimationToken);
                    if (TryResolveReplicationAnimator(view, out var animator) && animator != null)
                    {
                        ResetReplicationSemanticLocomotion(animator);
                    }
                }
            }

            for (var i = 0; i < ReplicationSemanticExpiredClientWorkEntityIds.Count; i++)
            {
                var entityId = ReplicationSemanticExpiredClientWorkEntityIds[i];
                ReplicationSemanticClientWorkByEntityId.Remove(entityId);
                ClearReplicationPuppetActionVisual(entityId);
                replicationSemanticWorkWatchdogs++;
            }

            ReplicationSemanticExpiredBufferedWorkEntityIds.Clear();
            foreach (var pair in ReplicationSemanticBufferedClientWorkByEntityId)
            {
                if (now >= pair.Value.ExpiresRealtime)
                {
                    ReplicationSemanticExpiredBufferedWorkEntityIds.Add(pair.Key);
                }
            }

            for (var i = 0; i < ReplicationSemanticExpiredBufferedWorkEntityIds.Count; i++)
            {
                ReplicationSemanticBufferedClientWorkByEntityId.Remove(ReplicationSemanticExpiredBufferedWorkEntityIds[i]);
            }
        }

        private static bool IsReplicationSemanticWorkPresentationActive(string entityId)
        {
            return replicationConfigSemanticAgentPresentation
                && ReplicationSemanticClientWorkByEntityId.ContainsKey(entityId);
        }

        private static void ResetReplicationSemanticAgentWorkPresentation()
        {
            if (!replicationConfigHostMode)
            {
                var active = new List<string>(ReplicationSemanticClientWorkByEntityId.Keys);
                for (var i = 0; i < active.Count; i++)
                {
                    ClearReplicationPuppetActionVisual(active[i]);
                }
            }

            ReplicationSemanticPendingHostWorkByAction.Clear();
            ReplicationSemanticPendingHostWorkByEntityId.Clear();
            ReplicationSemanticHostWorkByAction.Clear();
            ReplicationSemanticHostWorkByEntityId.Clear();
            ReplicationSemanticClientWorkByEntityId.Clear();
            ReplicationSemanticClientWorkOrderingByEntityId.Clear();
            ReplicationSemanticBufferedClientWorkByEntityId.Clear();
            ReplicationSemanticExpiredClientWorkEntityIds.Clear();
            ReplicationSemanticExpiredBufferedWorkEntityIds.Clear();
            replicationSemanticWorkActivitySequence = 0L;
            replicationSemanticWorkStartsSent = 0L;
            replicationSemanticWorkEndsSent = 0L;
            replicationSemanticWorkStartsApplied = 0L;
            replicationSemanticWorkEndsApplied = 0L;
            replicationSemanticWorkAnchorsSent = 0L;
            replicationSemanticWorkAnchorsApplied = 0L;
            replicationSemanticWorkVisualAnchorsSent = 0L;
            replicationSemanticWorkVisualAnchorsApplied = 0L;
            replicationSemanticWorkTerminalsBuffered = 0L;
            replicationSemanticWorkTerminalsReplayed = 0L;
            replicationSemanticWorkFallbacks = 0L;
            replicationSemanticWorkWatchdogs = 0L;
        }

        private static string FormatReplicationSemanticWorkStatus()
        {
            return "semanticWorkActive=" + ReplicationSemanticClientWorkByEntityId.Count
                + " semanticWorkStartsSent=" + replicationSemanticWorkStartsSent
                + " semanticWorkEndsSent=" + replicationSemanticWorkEndsSent
                + " semanticWorkStartsApplied=" + replicationSemanticWorkStartsApplied
                + " semanticWorkEndsApplied=" + replicationSemanticWorkEndsApplied
                + " semanticWorkAnchorsSent=" + replicationSemanticWorkAnchorsSent
                + " semanticWorkAnchorsApplied=" + replicationSemanticWorkAnchorsApplied
                + " semanticWorkVisualAnchorsSent=" + replicationSemanticWorkVisualAnchorsSent
                + " semanticWorkVisualAnchorsApplied=" + replicationSemanticWorkVisualAnchorsApplied
                + " semanticWorkBuffered=" + replicationSemanticWorkTerminalsBuffered
                + " semanticWorkReplayed=" + replicationSemanticWorkTerminalsReplayed
                + " semanticWorkFallbacks=" + replicationSemanticWorkFallbacks
                + " semanticWorkWatchdogs=" + replicationSemanticWorkWatchdogs;
        }
    }
}
