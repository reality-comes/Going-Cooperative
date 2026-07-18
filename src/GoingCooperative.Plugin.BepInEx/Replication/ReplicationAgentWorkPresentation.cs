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
        private const float ReplicationSemanticWorkDigLeaseSeconds = 1f;
        private const float ReplicationSemanticWorkBufferedTerminalSeconds = 30f;
        private const int ReplicationSemanticWorkBufferedTerminalLimit = 128;
        private const float ReplicationSemanticWorkVisualHoldSeconds = 0.5f;
        private const float ReplicationSemanticWorkTriggerCheckSeconds = 0.1f;
        private const float ReplicationSemanticWorkTriggerFrameDelaySeconds = 0.05f;
        private const float ReplicationSemanticWorkTriggerClipFraction = 0.9f;
        private const float ReplicationSemanticWorkTriggerMinimumSeconds = 0.5f;
        private const float ReplicationSemanticWorkTriggerMaximumSeconds = 3f;
        private const float ReplicationSemanticWorkDigFallbackSeconds = 1.25f;
        private const float ReplicationSemanticWorkHarvestFallbackSeconds = 1.15f;
        private const float ReplicationSemanticWorkAdvisoryDurationSeconds = 5f;

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
            public float NextCyclePulseRealtime;
        }

        private sealed class ReplicationSemanticClientWorkState
        {
            public string EntityId = string.Empty;
            public string WorkKind = string.Empty;
            public string AnimationToken = string.Empty;
            public string AnimatorStateDetail = string.Empty;
            public string HandItemId = string.Empty;
            public long ActivityId;
            public float ExpiresRealtime;
            public float NextHoldRealtime;
            public float NextTriggerMaintenanceRealtime;
            public float PendingTriggerRealtime;
            public float ObservedClipLength;
            public string ObservedClipName = string.Empty;
            public string TriggerMaintenanceReason = "fallback";
            public bool TriggerMaintenanceConfigured;
            public bool TriggerMaintenancePending;
            public bool WorkClipObserved;
            public bool ClipProbeLogged;
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
        private static long replicationSemanticWorkVisualCorrectionsApplied;
        private static long replicationSemanticWorkAnimatorAnchorsSuppressed;
        private static long replicationSemanticWorkStartConfirmations;
        private static long replicationSemanticWorkStartConfirmationRearms;
        private static long replicationSemanticWorkLocalCorrectionsApplied;
        private static long replicationSemanticWorkTriggersRearmed;
        private static long replicationSemanticWorkTriggerFallbacks;
        private static long replicationSemanticWorkForceQuits;
        private static long replicationSemanticWorkCyclePulsesSent;
        private static long replicationSemanticWorkCyclePulsesApplied;
        private static long replicationSemanticWorkAdvisoryDurations;
        private static long replicationSemanticWorkTerminalsBuffered;
        private static long replicationSemanticWorkTerminalsReplayed;
        private static long replicationSemanticWorkFallbacks;
        private static long replicationSemanticWorkWatchdogs;

        private static bool IsReplicationSemanticMigratedWork(string goalId, string actionId)
        {
            return AgentWorkPresentationPolicy.TryResolve(goalId, actionId, out _);
        }

        private static bool IsReplicationSemanticMigratedWorkGoal(string goalId)
        {
            return AgentWorkPresentationPolicy.IsMigratedGoal(goalId);
        }

        private static bool IsReplicationSemanticWorkCandidateAction(string actionId)
        {
            return AgentWorkPresentationPolicy.IsCandidateAction(actionId);
        }

        private static bool IsReplicationSemanticWorkTriggerMaintenanceEnabled(string workKind)
        {
            return replicationConfigSemanticWorkCycleDriver
                && AgentWorkPresentationPolicy.UsesClientTriggerMaintenance(workKind);
        }

        private static bool IsReplicationSemanticWorkCyclePulseEnabled(string workKind)
        {
            return replicationConfigSemanticWorkCycleDriver
                && AgentWorkPresentationPolicy.UsesHostCyclePulse(workKind);
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

            if (ReplicationSemanticHostWorkByAction.TryGetValue(action, out var active))
            {
                if (IsReplicationSemanticWorkCyclePulseEnabled(active.WorkKind)
                    && string.Equals(active.AnimationToken, trigger.Trim(), StringComparison.Ordinal))
                {
                    // A native repeat, when present, may advance the next lease sample.
                    // The lease itself is periodic because Dig normally remains in one
                    // host-owned animator state without emitting another trigger.
                    active.NextCyclePulseRealtime = Mathf.Min(
                        active.NextCyclePulseRealtime,
                        Time.realtimeSinceStartup + ReplicationSemanticWorkInitialVisualSampleSeconds);
                }

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
                if (!AgentWorkPresentationPolicy.TryResolve(goalId, actionId, out var descriptor))
                {
                    return;
                }

                pending = new ReplicationSemanticPendingHostWorkState
                {
                    Action = action,
                    SourceMethod = sourceMethod,
                    AnimationToken = descriptor.DefaultAnimationToken,
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

                if (IsReplicationSemanticWorkCyclePulseEnabled(active.WorkKind))
                {
                    // Preserve an observed controller transition as an early lease
                    // sample, but do not depend on it: vanilla Dig generally loops
                    // under the host GOAP action without another controller callback.
                    active.NextCyclePulseRealtime = Mathf.Min(
                        active.NextCyclePulseRealtime,
                        Time.realtimeSinceStartup + ReplicationSemanticWorkInitialVisualSampleSeconds);
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
                || !AgentWorkPresentationPolicy.TryResolve(goalId, actionId, out var descriptor)
                || !TryResolveReplicationSemanticWorkTarget(action, ref targetId, ref targetX, ref targetY, ref targetZ)
                || !TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _)
                || view == null
                || !TryGetReplicationTransform(view, out var transform)
                || transform == null)
            {
                replicationSemanticWorkFallbacks++;
                return false;
            }

            TryReadReplicationSemanticWorkDuration(action, out var durationSeconds, out var durationSource);

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
                ? descriptor.DefaultAnimationToken
                : pending.AnimationToken;
            var handItemId = !string.IsNullOrWhiteSpace(pending.HandItemId)
                ? pending.HandItemId
                : TryResolveReplicationActiveHandItemId(action, entityId, out var resolvedHandItemId, out _)
                    ? resolvedHandItemId
                    : string.Empty;
            ReplicationAgentWorkPresentation.Start(
                new ReplicationAgentPresentationStamp(epoch, entityId, activityId, 1L, Math.Max(0, Time.frameCount)),
                descriptor.WorkKind,
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
                WorkKind = descriptor.WorkKind,
                ActivityId = activityId,
                Revision = 1L,
                StartedRealtime = Time.realtimeSinceStartup,
                DurationSeconds = durationSeconds,
                NextAnchorRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkAnchorSeconds,
                NextVisualSampleRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkInitialVisualSampleSeconds,
                NextCyclePulseRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkDigLeaseSeconds,
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
                    + " durationSource=" + durationSource
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

        private static bool SendReplicationSemanticWorkCyclePulse(
            ReplicationSemanticHostWorkState state,
            float now)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return false;
            }

            // Dig is held by a real GOAP action on the host, while the client is only
            // a visual puppet. Refresh that host-owned state as a bounded lease so a
            // client-side animation exit is repaired without running the full view tick.
            var animatorStateDetail = CaptureReplicationActiveActionAnimatorState(state.EntityId);
            if (string.IsNullOrWhiteSpace(animatorStateDetail))
            {
                return false;
            }

            state.AnimatorStateDetail = animatorStateDetail;
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
                    + " phase=Pulse"
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " activityId=" + state.ActivityId.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                    + " animationB64=" + EncodeReplicationDetailBase64(state.AnimationToken)
                    + " animatorStateB64=" + EncodeReplicationDetailBase64(animatorStateDetail)
                    + " source=semantic-work-dig-lease"));
            replicationSemanticWorkCyclePulsesSent++;
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

        private static bool TryReadReplicationSemanticWorkDuration(
            object action,
            out float durationSeconds,
            out string source)
        {
            durationSeconds = 0f;
            source = "unknown";
            if (TryReadInstanceMemberValue(action, "Duration", out var actionDuration)
                && TryConvertReplicationSemanticWorkDuration(actionDuration, out durationSeconds))
            {
                source = "action-duration";
                return true;
            }

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
                            source = "closure-total-time";
                            return true;
                        }

                        if (closure != null
                            && TryReadInstanceMemberValue(closure, "time", out var time)
                            && TryConvertReplicationSemanticWorkDuration(time, out durationSeconds))
                        {
                            source = "closure-time";
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
                source = "target-build-time";
                return true;
            }

            // Repair is health-bound rather than duration-bound, and future
            // game updates may move duration calculation to a callback we do
            // not reflect. Duration is advisory presentation metadata; the
            // reliable Start/Anchor/End lifecycle remains authoritative.
            durationSeconds = ReplicationSemanticWorkAdvisoryDurationSeconds;
            source = "anchored-advisory";
            replicationSemanticWorkAdvisoryDurations++;
            return true;
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
                && !string.Equals(phase, "Pulse", StringComparison.Ordinal)
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
                    : AgentWorkPresentationPolicy.GetDefaultAnimationToken(delta.BlueprintId);
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
                    AnimatorStateDetail = animatorStateDetail,
                    HandItemId = handItemId,
                    ActivityId = activityId,
                    ExpiresRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkNetworkSilenceSeconds,
                    NextHoldRealtime = Time.realtimeSinceStartup
                        + (IsReplicationSemanticWorkTriggerMaintenanceEnabled(delta.BlueprintId)
                            ? ReplicationSemanticWorkTriggerCheckSeconds
                            : ReplicationSemanticWorkVisualHoldSeconds)
                };
                ReplicationSemanticClientWorkOrderingByEntityId[entityId] = decision.NextState;
                replicationSemanticWorkStartsApplied++;
                detail = "ok semantic-work-start entityId=" + entityId + " visual=" + visualDetail;
                ReplayReplicationSemanticBufferedClientWorkDelta(entityId, epoch, activityId, ref detail);
                return true;
            }

            if (string.Equals(phase, "Pulse", StringComparison.Ordinal))
            {
                ReplicationSemanticClientWorkOrderingByEntityId[entityId] = decision.NextState;
                if (!ReplicationSemanticClientWorkByEntityId.TryGetValue(entityId, out var active)
                    || active.ActivityId != activityId
                    || !IsReplicationSemanticWorkCyclePulseEnabled(active.WorkKind))
                {
                    detail = "semantic-work-pulse-inactive entityId=" + entityId;
                    return true;
                }

                active.ExpiresRealtime = Time.realtimeSinceStartup + ReplicationSemanticWorkNetworkSilenceSeconds;
                var animatorStateDetail = TryReadReplicationWorldObjectDetailToken(delta.Detail, "animatorStateB64", out var animatorStateB64)
                    && TryDecodeReplicationDetailBase64(animatorStateB64, out var decodedAnimatorState)
                    ? decodedAnimatorState
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(animatorStateDetail)
                    || !TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var pulseView, out _)
                    || pulseView == null)
                {
                    detail = "semantic-work-pulse-visual-missing entityId=" + entityId;
                    return false;
                }

                active.AnimatorStateDetail = animatorStateDetail;
                if (TryResolveReplicationAnimator(pulseView, out var pulseAnimator) && pulseAnimator != null)
                {
                    ResetReplicationSemanticLocomotion(pulseAnimator);
                }

                var pulseVisualDetail = ApplyReplicationAnimatorStateDetailIfDiverged(
                    pulseView,
                    animatorStateDetail,
                    out var corrected);
                if (corrected)
                {
                    replicationSemanticWorkVisualCorrectionsApplied++;
                }
                replicationSemanticWorkCyclePulsesApplied++;
                detail = "ok semantic-work-pulse entityId=" + entityId + " visual=" + pulseVisualDetail;
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
                    if (!string.IsNullOrWhiteSpace(animatorStateDetail))
                    {
                        active.AnimatorStateDetail = animatorStateDetail;
                    }

                    if (!string.IsNullOrWhiteSpace(animatorStateDetail)
                        && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _)
                        && view != null)
                    {
                        var initialVisualSample = TryReadReplicationWorldObjectDetailToken(delta.Detail, "visualSample", out var visualSample)
                            && string.Equals(visualSample, "initial", StringComparison.Ordinal);
                        if (initialVisualSample)
                        {
                            if (IsReplicationSemanticWorkTriggerMaintenanceEnabled(active.WorkKind))
                            {
                                // Harvest uses the vanilla interrupt trigger lifecycle.
                                visualDetail = ApplyReplicationSemanticWorkTriggerAnchorParameters(
                                    view,
                                    animatorStateDetail);
                                visualDetail += " maintenance=" + ConfigureReplicationSemanticWorkTriggerMaintenance(
                                    active,
                                    view);
                            }
                            else if (IsReplicationSemanticWorkCyclePulseEnabled(active.WorkKind))
                            {
                                // Establish Dig from the host's real post-transition
                                // state. Later cycles arrive as authoritative pulses.
                                visualDetail = ApplyReplicationAnimatorStateDetail(view, animatorStateDetail);
                            }
                            else
                            {
                                // The semantic Start event has already invoked the native
                                // work trigger.  Replaying the sampled Animator state here
                                // can immediately undo that trigger: in particular the host
                                // commonly samples Use=false while BuildDown is entering the
                                // hammer state.  This lane owns the work visual, so retain
                                // the trigger and use the delayed anchor only as a liveness
                                // acknowledgement.  Harvest and Dig deliberately retain
                                // their scoped paths above because their cycle drivers need
                                // their respective native state samples.
                                visualDetail = "Animator.StateSuppressed(active-semantic-work)";
                                replicationSemanticWorkAnimatorAnchorsSuppressed++;
                                visualDetail += " " + ConfirmReplicationSemanticWorkStart(
                                    active,
                                    view,
                                    animatorStateDetail);
                            }
                        }
                        else if (IsReplicationSemanticWorkTriggerMaintenanceEnabled(active.WorkKind)
                            || IsReplicationSemanticWorkCyclePulseEnabled(active.WorkKind))
                        {
                            // Periodic anchors remain parameter/liveness corrections;
                            // trigger maintenance owns the work animation lifecycle.
                            visualDetail = ApplyReplicationSemanticWorkTriggerAnchorParameters(
                                view,
                                animatorStateDetail);
                        }
                        else
                        {
                            // A periodic anchor is a liveness/correction message, not
                            // an instruction to restart every animation layer. Repair
                            // only state-hash drift and ignore expected phase skew.
                            visualDetail = ApplyReplicationAnimatorStateDetailIfDiverged(
                                view,
                                animatorStateDetail,
                                out var corrected);
                            if (corrected)
                            {
                                replicationSemanticWorkVisualCorrectionsApplied++;
                            }
                        }

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

        private static string ConfigureReplicationSemanticWorkTriggerMaintenance(
            ReplicationSemanticClientWorkState state,
            object view)
        {
            state.TriggerMaintenanceConfigured = false;
            if (!AgentWorkPresentationPolicy.UsesClientTriggerMaintenance(state.WorkKind))
            {
                return "out-of-scope";
            }

            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return "animator-missing";
            }

            var now = Time.realtimeSinceStartup;
            state.TriggerMaintenanceConfigured = true;
            state.TriggerMaintenancePending = false;
            state.WorkClipObserved = false;
            state.ClipProbeLogged = false;
            state.ObservedClipName = string.Empty;
            state.ObservedClipLength = 0f;
            state.TriggerMaintenanceReason = "fallback";
            state.NextTriggerMaintenanceRealtime = now + GetReplicationSemanticWorkTriggerFallbackSeconds(state.WorkKind);

            var clipDetail = FormatReplicationSemanticWorkClipSummary(animator);
            instance?.LogReplicationInfo("Going Cooperative semantic work trigger maintenance configured entityId="
                + state.EntityId
                + " workKind=" + state.WorkKind
                + " animation=" + FormatReplicationWorldObjectDetailToken(state.AnimationToken)
                + " fallbackSeconds=" + GetReplicationSemanticWorkTriggerFallbackSeconds(state.WorkKind).ToString("0.###", CultureInfo.InvariantCulture)
                + " " + clipDetail);
            return "trigger " + clipDetail;
        }

        private static string ApplyReplicationSemanticWorkTriggerAnchorParameters(
            object view,
            string animatorStateDetail)
        {
            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return "trigger-anchor=animator-missing";
            }

            var applied = ApplyReplicationAnimatorParameterStateDetail(animator, animatorStateDetail);
            return "trigger-anchor=parameters(count=" + applied.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private static string ConfirmReplicationSemanticWorkStart(
            ReplicationSemanticClientWorkState state,
            object view,
            string animatorStateDetail)
        {
            if (!AgentWorkPresentationPolicy.UsesClientStartConfirmation(state.WorkKind)
                || !TryReadReplicationWorldObjectDetailInt(animatorStateDetail, "animState0", out var expectedStateHash)
                || expectedStateHash == 0
                || !TryResolveReplicationAnimator(view, out var animator)
                || animator == null)
            {
                return "start-confirm=not-applicable";
            }

            replicationSemanticWorkStartConfirmations++;
            if (animator.IsInTransition(0))
            {
                // The client accepted the trigger and is still entering its work
                // state. Treat that as healthy rather than restarting it mid-blend.
                return "start-confirm=transition";
            }

            var clientStateHash = animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (clientStateHash == expectedStateHash)
            {
                return "start-confirm=matched";
            }

            // This is intentionally a single retry tied to the one delayed host
            // sample. It repairs a trigger consumed before the client Animator was
            // ready, but cannot become a periodic animation restart loop.
            var triggerDetail = InvokeReplicationSemanticWorkMaintainedTrigger(view, state.AnimationToken);
            replicationSemanticWorkStartConfirmationRearms++;
            return "start-confirm=rearmed expected="
                + expectedStateHash.ToString(CultureInfo.InvariantCulture)
                + " actual=" + clientStateHash.ToString(CultureInfo.InvariantCulture)
                + " result=" + triggerDetail;
        }

        private static float GetReplicationSemanticWorkTriggerFallbackSeconds(string workKind)
        {
            return string.Equals(workKind, "Harvest", StringComparison.Ordinal)
                ? ReplicationSemanticWorkHarvestFallbackSeconds
                : ReplicationSemanticWorkDigFallbackSeconds;
        }

        private static bool IsReplicationSemanticWorkClip(string workKind, string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName))
            {
                return false;
            }

            if (string.Equals(workKind, "Dig", StringComparison.Ordinal))
            {
                return clipName.IndexOf("mine", StringComparison.OrdinalIgnoreCase) >= 0
                    || clipName.IndexOf("mining", StringComparison.OrdinalIgnoreCase) >= 0
                    || clipName.IndexOf("dig", StringComparison.OrdinalIgnoreCase) >= 0
                    || clipName.IndexOf("pickaxe", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return string.Equals(workKind, "Harvest", StringComparison.Ordinal)
                && (clipName.IndexOf("harvest", StringComparison.OrdinalIgnoreCase) >= 0
                    || clipName.IndexOf("gather", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool TryObserveReplicationSemanticWorkClip(
            Animator animator,
            string workKind,
            out string clipName,
            out float clipLength)
        {
            clipName = string.Empty;
            clipLength = 0f;
            var layerCount = Math.Min(animator.layerCount, 4);
            for (var layer = 0; layer < layerCount; layer++)
            {
                try
                {
                    var clips = animator.GetCurrentAnimatorClipInfo(layer);
                    if (clips == null)
                    {
                        continue;
                    }

                    for (var clipIndex = 0; clipIndex < clips.Length; clipIndex++)
                    {
                        var clip = clips[clipIndex].clip;
                        if (clip != null && IsReplicationSemanticWorkClip(workKind, clip.name))
                        {
                            clipName = clip.name;
                            clipLength = clip.length;
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static string FormatReplicationSemanticWorkClipSummary(Animator animator)
        {
            var layers = new List<string>(4);
            var layerCount = Math.Min(animator.layerCount, 4);
            for (var layer = 0; layer < layerCount; layer++)
            {
                var stateInfo = animator.GetCurrentAnimatorStateInfo(layer);
                var clipName = "<none>";
                var clipLength = stateInfo.length;
                try
                {
                    var clips = animator.GetCurrentAnimatorClipInfo(layer);
                    if (clips != null && clips.Length > 0 && clips[0].clip != null)
                    {
                        clipName = clips[0].clip.name;
                        clipLength = clips[0].clip.length;
                    }
                }
                catch
                {
                }

                layers.Add("layer" + layer.ToString(CultureInfo.InvariantCulture)
                    + "=" + stateInfo.fullPathHash.ToString(CultureInfo.InvariantCulture)
                    + ":" + FormatReplicationWorldObjectDetailToken(clipName)
                    + ":len=" + clipLength.ToString("0.###", CultureInfo.InvariantCulture)
                    + ":norm=" + stateInfo.normalizedTime.ToString("0.###", CultureInfo.InvariantCulture)
                    + ":transition=" + animator.IsInTransition(layer));
            }

            return "clips=[" + string.Join(",", layers.ToArray()) + "]";
        }

        private static string InvokeReplicationSemanticWorkMaintainedTrigger(
            object view,
            string trigger)
        {
            applyingRuntimeCommandDepth++;
            try
            {
                return InvokeReplicationSemanticWorkAnimationTrigger(view, trigger, string.Empty);
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static void MaintainReplicationSemanticWorkTrigger(
            ReplicationSemanticClientWorkState state,
            object view,
            float now)
        {
            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return;
            }

            ResetReplicationSemanticLocomotion(animator);
            if (!state.TriggerMaintenanceConfigured
                || !AgentWorkPresentationPolicy.UsesClientTriggerMaintenance(state.WorkKind))
            {
                return;
            }

            if (state.TriggerMaintenancePending)
            {
                if (now < state.PendingTriggerRealtime)
                {
                    return;
                }

                var triggerDetail = InvokeReplicationSemanticWorkMaintainedTrigger(view, state.AnimationToken);
                state.TriggerMaintenancePending = false;
                state.WorkClipObserved = false;
                state.ClipProbeLogged = false;
                state.NextTriggerMaintenanceRealtime = now + GetReplicationSemanticWorkTriggerFallbackSeconds(state.WorkKind);
                replicationSemanticWorkTriggersRearmed++;
                instance?.LogReplicationInfo("Going Cooperative semantic work trigger rearmed entityId="
                    + state.EntityId
                    + " workKind=" + state.WorkKind
                    + " animation=" + FormatReplicationWorldObjectDetailToken(state.AnimationToken)
                    + " reason=" + state.TriggerMaintenanceReason
                    + " result=" + triggerDetail);
                return;
            }

            if (!state.WorkClipObserved
                && TryObserveReplicationSemanticWorkClip(animator, state.WorkKind, out var clipName, out var clipLength))
            {
                state.WorkClipObserved = true;
                state.ObservedClipName = clipName;
                state.ObservedClipLength = clipLength;
                state.TriggerMaintenanceReason = "clip-duration";
                var interval = Mathf.Clamp(
                    clipLength * ReplicationSemanticWorkTriggerClipFraction,
                    ReplicationSemanticWorkTriggerMinimumSeconds,
                    ReplicationSemanticWorkTriggerMaximumSeconds);
                state.NextTriggerMaintenanceRealtime = now + interval;
                instance?.LogReplicationInfo("Going Cooperative semantic work trigger clip observed entityId="
                    + state.EntityId
                    + " workKind=" + state.WorkKind
                    + " clip=" + FormatReplicationWorldObjectDetailToken(clipName)
                    + " length=" + clipLength.ToString("0.###", CultureInfo.InvariantCulture)
                    + " rearmSeconds=" + interval.ToString("0.###", CultureInfo.InvariantCulture));
            }
            else if (!state.WorkClipObserved && !state.ClipProbeLogged)
            {
                state.ClipProbeLogged = true;
                instance?.LogReplicationInfo("Going Cooperative semantic work trigger awaiting clip entityId="
                    + state.EntityId
                    + " workKind=" + state.WorkKind
                    + " " + FormatReplicationSemanticWorkClipSummary(animator));
            }

            if (now < state.NextTriggerMaintenanceRealtime)
            {
                return;
            }

            state.TriggerMaintenanceReason = state.WorkClipObserved ? "clip-duration" : "fallback";
            var forceQuitDetail = InvokeReplicationSemanticWorkMaintainedTrigger(view, "ForceQuit");
            state.TriggerMaintenancePending = true;
            state.PendingTriggerRealtime = now + ReplicationSemanticWorkTriggerFrameDelaySeconds;
            state.NextTriggerMaintenanceRealtime = now + GetReplicationSemanticWorkTriggerFallbackSeconds(state.WorkKind);
            replicationSemanticWorkForceQuits++;
            if (!state.WorkClipObserved)
            {
                replicationSemanticWorkTriggerFallbacks++;
            }

            instance?.LogReplicationInfo("Going Cooperative semantic work trigger force-quit entityId="
                + state.EntityId
                + " workKind=" + state.WorkKind
                + " reason=" + state.TriggerMaintenanceReason
                + " clip=" + FormatReplicationWorldObjectDetailToken(state.ObservedClipName)
                + " clipLength=" + state.ObservedClipLength.ToString("0.###", CultureInfo.InvariantCulture)
                + " result=" + forceQuitDetail);
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
                    else if (IsReplicationSemanticWorkCyclePulseEnabled(state.WorkKind)
                        && now >= state.NextCyclePulseRealtime)
                    {
                        if (SendReplicationSemanticWorkCyclePulse(state, now))
                        {
                            state.NextCyclePulseRealtime = now + ReplicationSemanticWorkDigLeaseSeconds;
                        }
                        else
                        {
                            state.NextCyclePulseRealtime = now + ReplicationSemanticWorkInitialVisualSampleSeconds;
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

                var triggerMaintenanceEnabled = IsReplicationSemanticWorkTriggerMaintenanceEnabled(state.WorkKind);
                var cyclePulseEnabled = IsReplicationSemanticWorkCyclePulseEnabled(state.WorkKind);
                state.NextHoldRealtime = now + (triggerMaintenanceEnabled || cyclePulseEnabled
                    ? ReplicationSemanticWorkTriggerCheckSeconds
                    : ReplicationSemanticWorkVisualHoldSeconds);
                if (TryFindReplicationAnimatedAgentViewByEntityId(pair.Key, out var view, out _) && view != null)
                {
                    if (triggerMaintenanceEnabled)
                    {
                        MaintainReplicationSemanticWorkTrigger(state, view, now);
                        continue;
                    }

                    // Do not retrigger the animation or force the legacy attack-style
                    // parameter bundle. Authoritative anchor snapshots own the work
                    // state; this pump only prevents locomotion from competing with it.
                    if (TryResolveReplicationAnimator(view, out var animator) && animator != null)
                    {
                        ResetReplicationSemanticLocomotion(animator);
                    }

                    if (cyclePulseEnabled)
                    {
                        // A lease pulse refreshes the authoritative state once per
                        // second. Between pulses, repair only an actual state exit;
                        // matching hashes are left running to avoid visible restarts.
                        if (!string.IsNullOrWhiteSpace(state.AnimatorStateDetail))
                        {
                            ApplyReplicationAnimatorStateDetailIfDiverged(
                                view,
                                state.AnimatorStateDetail,
                                out var corrected);
                            if (corrected)
                            {
                                replicationSemanticWorkLocalCorrectionsApplied++;
                            }
                        }

                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(state.AnimatorStateDetail))
                    {
                        ApplyReplicationAnimatorStateDetailIfDiverged(
                            view,
                            state.AnimatorStateDetail,
                            out var corrected);
                        if (corrected)
                        {
                            replicationSemanticWorkLocalCorrectionsApplied++;
                        }
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
            replicationSemanticWorkVisualCorrectionsApplied = 0L;
            replicationSemanticWorkAnimatorAnchorsSuppressed = 0L;
            replicationSemanticWorkStartConfirmations = 0L;
            replicationSemanticWorkStartConfirmationRearms = 0L;
            replicationSemanticWorkLocalCorrectionsApplied = 0L;
            replicationSemanticWorkTriggersRearmed = 0L;
            replicationSemanticWorkTriggerFallbacks = 0L;
            replicationSemanticWorkForceQuits = 0L;
            replicationSemanticWorkCyclePulsesSent = 0L;
            replicationSemanticWorkCyclePulsesApplied = 0L;
            replicationSemanticWorkAdvisoryDurations = 0L;
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
                + " semanticWorkVisualCorrectionsApplied=" + replicationSemanticWorkVisualCorrectionsApplied
                + " semanticWorkAnimatorAnchorsSuppressed=" + replicationSemanticWorkAnimatorAnchorsSuppressed
                + " semanticWorkStartConfirmations=" + replicationSemanticWorkStartConfirmations
                + " semanticWorkStartConfirmationRearms=" + replicationSemanticWorkStartConfirmationRearms
                + " semanticWorkLocalCorrectionsApplied=" + replicationSemanticWorkLocalCorrectionsApplied
                + " semanticWorkCycleDriver=" + replicationConfigSemanticWorkCycleDriver
                + " semanticWorkTriggersRearmed=" + replicationSemanticWorkTriggersRearmed
                + " semanticWorkTriggerFallbacks=" + replicationSemanticWorkTriggerFallbacks
                + " semanticWorkForceQuits=" + replicationSemanticWorkForceQuits
                + " semanticWorkCyclePulsesSent=" + replicationSemanticWorkCyclePulsesSent
                + " semanticWorkCyclePulsesApplied=" + replicationSemanticWorkCyclePulsesApplied
                + " semanticWorkAdvisoryDurations=" + replicationSemanticWorkAdvisoryDurations
                + " semanticWorkBuffered=" + replicationSemanticWorkTerminalsBuffered
                + " semanticWorkReplayed=" + replicationSemanticWorkTerminalsReplayed
                + " semanticWorkFallbacks=" + replicationSemanticWorkFallbacks
                + " semanticWorkWatchdogs=" + replicationSemanticWorkWatchdogs;
        }
    }
}
