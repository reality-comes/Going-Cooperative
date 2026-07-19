using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationBuildingLifecycleV2DeltaKind = "BuildingLifecycleV2";
        private const string ReplicationBuildingProgressV2DeltaKind = "BuildingProgressV2";
        private const string ReplicationBuildingRepairV2DeltaKind = "BuildingRepairV2";
        private const string ReplicationBuildingRecoveryRequiredV2DeltaKind = "BuildingRecoveryRequiredV2";
        private const string ReplicationBuildingProgressV2CommandPrefix = "building-progress-v2|";
        private const float ReplicationBuildingProgressV2RequestSeconds = 2f;
        private const float ReplicationBuildingRepairV2CooldownSeconds = 2f;
        private const float ReplicationBuildingPresentationStepSecondsV2 = 0.25f;
        private const int ReplicationBuildingPresentationApplyBudgetV2 = 16;

        private static readonly Dictionary<long, ReplicationTrackedBuildingV2> ReplicationTrackedHostBuildingsV2 =
            new Dictionary<long, ReplicationTrackedBuildingV2>();
        private static readonly Dictionary<long, bool> ReplicationClientBuildingProgressingV2 =
            new Dictionary<long, bool>();
        private static readonly Dictionary<long, long> ReplicationClientBuildingTerminalRevisionV2 =
            new Dictionary<long, long>();
        private static readonly Dictionary<long, ReplicationClientBuildingPresentationV2> ReplicationClientBuildingPresentationByHostIdV2 =
            new Dictionary<long, ReplicationClientBuildingPresentationV2>();
        private static readonly List<long> ReplicationClientBuildingPresentationOrderV2 =
            new List<long>();
        private static readonly Dictionary<long, float> ReplicationBuildingRepairLastSentRealtimeV2 =
            new Dictionary<long, float>();
        private static readonly HashSet<long> ReplicationBuildingRecoveryEscalatedSourceSequencesV2 =
            new HashSet<long>();
        private static BuildingRevisionLedger replicationClientBuildingRevisionLedgerV2 =
            new BuildingRevisionLedger(65536);
        private static long replicationBuildingLifecycleDeltasSentV2;
        private static long replicationBuildingLifecycleDeltasAppliedV2;
        private static long replicationBuildingLifecycleNativeEventsV2;
        private static long replicationBuildingRepairDeltasSentV2;
        private static long replicationBuildingRepairDeltasAppliedV2;
        private static long replicationClientSelectedBuildingHostIdV2;
        private static int replicationBuildingIdentityBootstrapEpochV2 = -1;
        private static int replicationBuildingIdentityBootstrapCountV2;
        private static float replicationNextBuildingIdentityBootstrapRealtimeV2;
        private static float replicationNextBuildingProgressRequestRealtimeV2;
        private static int replicationClientBuildingPresentationCursorV2;
        private static long replicationClientBuildingPresentationAppliesV2;
        private static long replicationBuildingLifecycleProgressStartFallbacksV2;
        private static string replicationBuildingLifecycleLastCaptureGateReasonV2 = string.Empty;

        [ThreadStatic]
        private static object? replicationBuildingTerminalRootTargetV2;

        [ThreadStatic]
        private static string? replicationBuildingTerminalRootReasonV2;

        [ThreadStatic]
        private static Dictionary<object, int>? replicationBuildingMutationDepthByTargetV2;

        private void TryInstallReplicationBuildingLifecycleV2Hooks(Harmony harmonyInstance)
        {
            var buildingType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingInstance");
            var managerType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsManagerMain");
            if (buildingType == null || managerType == null)
            {
                LogReplicationWarning("Going Cooperative building lifecycle v2 native types missing");
                return;
            }

            var prefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildingLifecycleV2MutationPrefix),
                BindingFlags.NonPublic | BindingFlags.Static));
            var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildingLifecycleV2MutationPostfix),
                BindingFlags.NonPublic | BindingFlags.Static));
            var finalizer = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildingLifecycleV2MutationFinalizer),
                BindingFlags.NonPublic | BindingFlags.Static));
            var progressGuardPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildingProgressMutationV2Prefix),
                BindingFlags.NonPublic | BindingFlags.Static));
            var progressObservedPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildingProgressObservedV2Postfix),
                BindingFlags.NonPublic | BindingFlags.Static));
            var patched = 0;
            var methodNames = new[]
            {
                "ConstructionStarted",
                "ConstructionPaused",
                "ConstructionFailed",
                "EnterFoundationState",
                "ConstructionCompleted",
                "EnterFinishedState",
                "SetConstructionPhase",
                "SetMarkedForDestruction",
                "BuildingCanceled",
                "BuildingDeconstructed"
            };
            for (var i = 0; i < methodNames.Length; i++)
            {
                var methods = buildingType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    if (!string.Equals(methods[methodIndex].Name, methodNames[i], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    patched += TryPatchReplicationBuildingLifecycleV2Method(
                        harmonyInstance,
                        methods[methodIndex],
                        prefix,
                        postfix,
                        finalizer);
                }
            }

            var forbidSetter = AccessTools.PropertySetter(buildingType, "IsForbidden");
            if (forbidSetter != null)
            {
                patched += TryPatchReplicationBuildingLifecycleV2Method(
                    harmonyInstance,
                    forbidSetter,
                    prefix,
                    postfix,
                    finalizer);
            }

            var remainingTimeSetter = AccessTools.Method(
                buildingType,
                "SetRemainingTime",
                new[] { typeof(float) });
            if (remainingTimeSetter != null)
            {
                try
                {
                    harmonyInstance.Patch(
                        remainingTimeSetter,
                        prefix: progressGuardPrefix,
                        postfix: progressObservedPostfix);
                    patched++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning("Going Cooperative building progress authority patch failed error="
                        + ex.GetType().Name
                        + ":"
                        + ex.Message);
                }
            }

            var managerMethods = managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < managerMethods.Length; i++)
            {
                if (string.Equals(managerMethods[i].Name, "DestroyBuilding", StringComparison.Ordinal)
                    && managerMethods[i].GetParameters().Length == 3)
                {
                    patched += TryPatchReplicationBuildingLifecycleV2Method(
                        harmonyInstance,
                        managerMethods[i],
                        prefix,
                        postfix,
                        finalizer);
                }
            }

            LogReplicationInfo("Going Cooperative building lifecycle v2 hooks patched="
                + patched.ToString(CultureInfo.InvariantCulture));
        }

        private int TryPatchReplicationBuildingLifecycleV2Method(
            Harmony harmonyInstance,
            MethodInfo method,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            HarmonyMethod finalizer)
        {
            try
            {
                harmonyInstance.Patch(
                    method,
                    prefix: prefix,
                    postfix: postfix,
                    finalizer: finalizer);
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative building lifecycle v2 patch failed method="
                    + method.DeclaringType?.FullName
                    + "."
                    + method.Name
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
                return 0;
            }
        }

        private static bool ReplicationBuildingLifecycleV2MutationPrefix(
            MethodBase __originalMethod,
            object __instance,
            object[] __args,
            out ReplicationBuildingMutationCaptureV2? __state)
        {
            __state = null;
            if (ShouldSuppressReplicationClientBuildingMutationV2())
            {
                return false;
            }

            if (!ShouldCaptureReplicationBuildingLifecycleV2())
            {
                return true;
            }

            var target = string.Equals(__originalMethod.Name, "DestroyBuilding", StringComparison.Ordinal)
                && __args.Length > 0
                ? __args[0]
                : __instance;
            if (target == null
                || !TryEnsureReplicationHostBuildingTrackerV2(target, out var tracked, out _)
                || tracked == null)
            {
                return true;
            }

            // The tracker already owns the last authoritative state. Capturing another
            // complete reflection snapshot in the prefix doubled lifecycle-event cost
            // and was never consumed by the postfix.
            var depthByTarget = replicationBuildingMutationDepthByTargetV2
                ??= new Dictionary<object, int>(ReferenceObjectComparer.Instance);
            var targetKey = tracked.BuildingInstance.Target ?? target;
            depthByTarget.TryGetValue(targetKey, out var mutationDepth);
            depthByTarget[targetKey] = mutationDepth + 1;
            __state = new ReplicationBuildingMutationCaptureV2(
                tracked,
                targetKey,
                mutationDepth == 0);
            if (string.Equals(__originalMethod.Name, "BuildingCanceled", StringComparison.Ordinal)
                || string.Equals(__originalMethod.Name, "BuildingDeconstructed", StringComparison.Ordinal))
            {
                replicationBuildingTerminalRootTargetV2 = tracked.BuildingInstance.Target;
                replicationBuildingTerminalRootReasonV2 = __originalMethod.Name;
            }

            return true;
        }

        private static bool ReplicationBuildingProgressMutationV2Prefix()
        {
            return !ShouldSuppressReplicationClientBuildingMutationV2();
        }

        // ConstructionStarted is the authoritative semantic edge, but the game can
        // enter an already-running construct action before the peer handshake is
        // complete. SetRemainingTime is then the first reliable host-side signal
        // after connection. Use it only to recover the missing *start* edge; it
        // must never become a per-tick progress replication lane.
        private static void ReplicationBuildingProgressObservedV2Postfix(
            object __instance,
            float __0)
        {
            if (!ShouldCaptureReplicationBuildingLifecycleV2())
            {
                TraceReplicationBuildingLifecycleCaptureGateV2();
                return;
            }

            if (__instance == null
                || __0 <= 0f
                || !TryEnsureReplicationHostBuildingTrackerV2(__instance, out var tracked, out _)
                || tracked == null
                || tracked.Progressing
                || !TryCaptureReplicationBuildingAbsoluteStateV2(tracked, out var state)
                || !state.Exists
                || !state.UnderConstruction
                || state.MarkedForDestruction)
            {
                return;
            }

            tracked.Progressing = true;
            if (!TryCaptureReplicationBuildingAbsoluteStateV2(tracked, out state))
            {
                tracked.Progressing = false;
                return;
            }

            EmitReplicationBuildingLifecycleV2(
                tracked,
                state,
                "SetRemainingTime-progress-start",
                force: false);
            replicationBuildingLifecycleProgressStartFallbacksV2++;
        }

        private static bool ShouldSuppressReplicationClientBuildingMutationV2()
        {
            return replicationConfigBuildingReplicationV2
                && replicationConfigEnabled
                && !replicationConfigHostMode
                && replicationRuntimeStarted
                && replicationRemoteHelloReceived
                && !multiplayerLoadingInProgress
                && applyingRuntimeCommandDepth <= 0
                && replicationActiveBuildCaptureTransaction == null;
        }

        private static void ReplicationBuildingLifecycleV2MutationPostfix(
            MethodBase __originalMethod,
            object[] __args,
            ReplicationBuildingMutationCaptureV2? __state)
        {
            if (__state == null)
            {
                return;
            }

            var shouldEmit = ReleaseReplicationBuildingMutationCaptureV2(__state);
            if (!shouldEmit || !ShouldCaptureReplicationBuildingLifecycleV2())
            {
                return;
            }

            var methodName = __originalMethod.Name;
            if ((string.Equals(methodName, "EnterFoundationState", StringComparison.Ordinal)
                    || string.Equals(methodName, "EnterFinishedState", StringComparison.Ordinal))
                && __args.Length > 0
                && __args[0] is bool afterLoading
                && afterLoading)
            {
                return;
            }

            var tracked = __state.Tracked;
            if (string.Equals(methodName, "ConstructionStarted", StringComparison.Ordinal))
            {
                tracked.Progressing = true;
            }
            else if (string.Equals(methodName, "ConstructionPaused", StringComparison.Ordinal)
                || string.Equals(methodName, "ConstructionFailed", StringComparison.Ordinal)
                || string.Equals(methodName, "ConstructionCompleted", StringComparison.Ordinal)
                || string.Equals(methodName, "EnterFinishedState", StringComparison.Ordinal))
            {
                tracked.Progressing = false;
            }

            var terminal = string.Equals(methodName, "DestroyBuilding", StringComparison.Ordinal)
                || string.Equals(methodName, "BuildingCanceled", StringComparison.Ordinal)
                || string.Equals(methodName, "BuildingDeconstructed", StringComparison.Ordinal);
            if (terminal)
            {
                var reason = methodName;
                if (string.Equals(methodName, "DestroyBuilding", StringComparison.Ordinal)
                    && ReferenceEquals(
                        replicationBuildingTerminalRootTargetV2,
                        tracked.BuildingInstance.Target)
                    && !string.IsNullOrWhiteSpace(replicationBuildingTerminalRootReasonV2))
                {
                    reason = replicationBuildingTerminalRootReasonV2!;
                }

                EmitReplicationBuildingLifecycleV2(
                    tracked,
                    ReplicationBuildingAbsoluteStateV2.Removed,
                    reason,
                    force: true);
                if (ReferenceEquals(replicationBuildingTerminalRootTargetV2, __state.TargetKey)
                    || ReferenceEquals(
                        replicationBuildingTerminalRootTargetV2,
                        tracked.BuildingInstance.Target))
                {
                    replicationBuildingTerminalRootTargetV2 = null;
                    replicationBuildingTerminalRootReasonV2 = null;
                }

                return;
            }

            if (!TryCaptureReplicationBuildingAbsoluteStateV2(tracked, out var current))
            {
                return;
            }

            EmitReplicationBuildingLifecycleV2(tracked, current, methodName, force: false);
        }

        private static Exception? ReplicationBuildingLifecycleV2MutationFinalizer(
            Exception? __exception,
            ReplicationBuildingMutationCaptureV2? __state)
        {
            if (__state != null)
            {
                ReleaseReplicationBuildingMutationCaptureV2(__state);
                if (__state.IsOutermost
                    && (ReferenceEquals(
                            replicationBuildingTerminalRootTargetV2,
                            __state.TargetKey)
                        || ReferenceEquals(
                            replicationBuildingTerminalRootTargetV2,
                            __state.Tracked.BuildingInstance.Target)))
                {
                    replicationBuildingTerminalRootTargetV2 = null;
                    replicationBuildingTerminalRootReasonV2 = null;
                }
            }

            return __exception;
        }

        private static bool ReleaseReplicationBuildingMutationCaptureV2(
            ReplicationBuildingMutationCaptureV2 capture)
        {
            if (capture.Released)
            {
                return false;
            }

            capture.Released = true;
            var depthByTarget = replicationBuildingMutationDepthByTargetV2;
            if (depthByTarget != null
                && depthByTarget.TryGetValue(capture.TargetKey, out var depth))
            {
                if (depth <= 1)
                {
                    depthByTarget.Remove(capture.TargetKey);
                }
                else
                {
                    depthByTarget[capture.TargetKey] = depth - 1;
                }
            }

            return capture.IsOutermost;
        }

        private static bool ShouldCaptureReplicationBuildingLifecycleV2()
        {
            return string.IsNullOrEmpty(GetReplicationBuildingLifecycleCaptureGateReasonV2());
        }

        private static string GetReplicationBuildingLifecycleCaptureGateReasonV2()
        {
            if (!replicationConfigBuildingReplicationV2) return "v2-disabled";
            if (!replicationConfigEnabled) return "replication-disabled";
            if (!replicationConfigHostMode) return "not-host";
            if (!replicationRuntimeStarted) return "runtime-not-started";
            if (!replicationRemoteHelloReceived) return "peer-hello-not-received";
            if (replicationActiveBuildCaptureTransaction != null) return "local-build-capture-active";
            if (replicationActiveAuthoritativeBuildApplyCapture != null) return "authoritative-build-apply-active";
            if (IsReplicationPluginReferenceMissingV2()) return "plugin-instance-missing";
            return string.Empty;
        }

        // The plugin deliberately keeps its static runtime alive across a Unity
        // scene unload. Unity overloads == null for a destroyed MonoBehaviour,
        // so this gate must test the managed reference rather than Unity object
        // lifetime. The retained runtime still owns the reliable transport.
        private static bool IsReplicationPluginReferenceMissingV2()
        {
            return ReferenceEquals(instance, null);
        }

        private static void TraceReplicationBuildingLifecycleCaptureGateV2()
        {
            var reason = GetReplicationBuildingLifecycleCaptureGateReasonV2();
            if (string.IsNullOrEmpty(reason)
                || string.Equals(
                    replicationBuildingLifecycleLastCaptureGateReasonV2,
                    reason,
                    StringComparison.Ordinal))
            {
                return;
            }

            replicationBuildingLifecycleLastCaptureGateReasonV2 = reason;
            instance?.LogReplicationInfo(
                "Going Cooperative building lifecycle v2 progress fallback gated reason=" + reason);
        }

        private static void TrackReplicationBuildingLifecycleV2(
            ReplicationCapturedBuildPlacement placement,
            string source)
        {
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigHostMode
                || placement == null
                || placement.UniqueId <= 0L)
            {
                return;
            }

            RegisterReplicationHostIdentity(placement.UniqueId, placement.View, source);
            if (ReplicationTrackedHostBuildingsV2.TryGetValue(
                    placement.UniqueId,
                    out var existing))
            {
                // A native lifecycle hook can observe this UID before the immutable
                // placement manifest is published. Upgrade its placement metadata
                // without ever resetting the per-building revision clock.
                existing.View.Target = placement.View;
                existing.BuildingInstance.Target = placement.BuildingInstance;
                existing.CanonicalRecord = placement.Record;
                if (!existing.HasLastSentState
                    && TryCaptureReplicationBuildingAbsoluteStateV2(existing, out var existingState))
                {
                    existing.LastSentState = existingState;
                    existing.HasLastSentState = true;
                }

                return;
            }

            var tracked = new ReplicationTrackedBuildingV2(
                placement.UniqueId,
                placement.BlueprintId,
                placement.Record.X,
                placement.Record.Y,
                placement.Record.Z,
                placement.View,
                placement.BuildingInstance,
                placement.Record);
            if (TryCaptureReplicationBuildingAbsoluteStateV2(tracked, out var initial))
            {
                tracked.LastSentState = initial;
                tracked.HasLastSentState = true;
            }

            ReplicationTrackedHostBuildingsV2[placement.UniqueId] = tracked;
        }

        private static bool TryEnsureReplicationHostBuildingTrackerV2(
            object candidate,
            out ReplicationTrackedBuildingV2? tracked,
            out string detail)
        {
            tracked = null;
            if (!TryResolveReplicationBuildingInstanceV2(candidate, out var buildingInstance, out detail)
                || buildingInstance == null
                || !TryReadReplicationWorldObjectLongMember(
                    buildingInstance,
                    "UniqueId",
                    "uniqueId",
                    out var hostId)
                || hostId <= 0L)
            {
                detail = "building-lifecycle-v2-host-identity-missing " + detail;
                return false;
            }

            if (ReplicationTrackedHostBuildingsV2.TryGetValue(hostId, out tracked))
            {
                return true;
            }

            if (!TryReadInstanceMemberValue(buildingInstance, "Blueprint", out var blueprint)
                || blueprint == null
                || !TryResolveReplicationBuildBlueprintId(blueprint, out var blueprintId)
                || !TryResolveReplicationBuildingGridV2(buildingInstance, out var gridX, out var gridY, out var gridZ))
            {
                detail = "building-lifecycle-v2-host-fields-missing";
                return false;
            }

            object? view = null;
            ReplicationBuildPlacementRecord? record = null;
            if (candidate is Component)
            {
                view = candidate;
                if (TryCaptureReplicationCommittedBuilding(candidate, out var captured, out _)
                    && captured != null)
                {
                    record = captured.Record;
                }
            }

            tracked = new ReplicationTrackedBuildingV2(
                hostId,
                blueprintId,
                gridX,
                gridY,
                gridZ,
                view,
                buildingInstance,
                record);
            if (TryCaptureReplicationBuildingAbsoluteStateV2(tracked, out var initial))
            {
                tracked.LastSentState = initial;
                tracked.HasLastSentState = true;
            }

            ReplicationTrackedHostBuildingsV2[hostId] = tracked;
            RegisterReplicationHostIdentity(hostId, buildingInstance, "building-lifecycle-v2-host-native-event");
            detail = "ok hostId=" + hostId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryResolveReplicationBuildingInstanceV2(
            object candidate,
            out object? buildingInstance,
            out string detail)
        {
            var buildingType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingInstance");
            if (buildingType != null && buildingType.IsInstanceOfType(candidate))
            {
                buildingInstance = candidate;
                detail = "instance=direct";
                return true;
            }

            return TryResolveReplicationBuildingCandidateInstance(candidate, out buildingInstance, out detail);
        }

        private static bool TryResolveReplicationBuildingGridV2(
            object buildingInstance,
            out int x,
            out int y,
            out int z)
        {
            if (TryResolveReplicationBuildingCandidateGrid(buildingInstance, out x, out y, out z))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(buildingInstance, "VoxelHolderPosition", out var voxelHolder)
                && voxelHolder != null
                && TryReadReplicationVec3Int(voxelHolder, out x, out y, out z))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(buildingInstance, "Positions", out var positionsValue)
                && positionsValue is IEnumerable positions)
            {
                foreach (var position in positions)
                {
                    if (position != null && TryReadReplicationVec3Int(position, out x, out y, out z))
                    {
                        return true;
                    }
                }
            }

            x = 0;
            y = 0;
            z = 0;
            return false;
        }

        private static bool TryCaptureReplicationBuildingAbsoluteStateV2(
            ReplicationTrackedBuildingV2 tracked,
            out ReplicationBuildingAbsoluteStateV2 state)
        {
            var buildingInstance = tracked.BuildingInstance.Target;
            if (buildingInstance == null)
            {
                state = ReplicationBuildingAbsoluteStateV2.Removed;
                return false;
            }

            var phase = TryReadReplicationBuildingPhase(buildingInstance, out var parsedPhase)
                ? parsedPhase
                : string.Empty;
            var remainingTimeMs = TryReadReplicationBuildingRemainingTimeMs(buildingInstance, out var parsedRemaining)
                ? Math.Max(0, parsedRemaining)
                : 0;
            var underConstruction = TryReadReplicationBoolMember(
                    buildingInstance,
                    "IsUnderConstruction",
                    "isUnderConstruction",
                    out var parsedUnderConstruction)
                && parsedUnderConstruction;
            var forbidden = TryReadReplicationBoolMember(
                    buildingInstance,
                    "IsForbidden",
                    "isForbidden",
                    out var parsedForbidden)
                && parsedForbidden;
            var marked = TryReadReplicationBoolMember(
                    buildingInstance,
                    "MarkedForDestruction",
                    "markedForDestruction",
                    out var parsedMarked)
                && parsedMarked;
            state = new ReplicationBuildingAbsoluteStateV2(
                true,
                phase,
                remainingTimeMs,
                underConstruction,
                tracked.Progressing,
                forbidden,
                marked);
            return true;
        }

        private static void EmitReplicationBuildingLifecycleV2(
            ReplicationTrackedBuildingV2 tracked,
            ReplicationBuildingAbsoluteStateV2 state,
            string lifecycle,
            bool force)
        {
            if (tracked.TerminalSent || !ShouldCaptureReplicationBuildingLifecycleV2())
            {
                return;
            }

            if (!force && tracked.HasLastSentState && tracked.LastSentState.Equals(state))
            {
                return;
            }

            tracked.Revision++;
            tracked.LastSentState = state;
            tracked.HasLastSentState = true;
            tracked.TerminalSent = !state.Exists;
            var detail = FormatReplicationBuildingLifecycleStateV2(
                tracked,
                state,
                lifecycle,
                baselineSequence: -1L,
                includeReplay: false);
            instance!.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationBuildingLifecycleV2DeltaKind,
                tracked.HostId,
                tracked.BlueprintId,
                tracked.GridX,
                tracked.GridY,
                tracked.GridZ,
                detail));
            replicationBuildingLifecycleDeltasSentV2++;
            replicationBuildingLifecycleNativeEventsV2++;
            if (!state.Exists)
            {
                RemoveReplicationHostIdentity(
                    tracked.HostId,
                    tracked.View.Target ?? tracked.BuildingInstance.Target,
                    "building-lifecycle-v2-terminal");
            }
        }

        private static string FormatReplicationBuildingLifecycleStateV2(
            ReplicationTrackedBuildingV2 tracked,
            ReplicationBuildingAbsoluteStateV2 state,
            string lifecycle,
            long baselineSequence,
            bool includeReplay)
        {
            var detail = "epoch=" + GetReplicationBuildBatchEpoch().ToString(CultureInfo.InvariantCulture)
                + " revision=" + tracked.Revision.ToString(CultureInfo.InvariantCulture)
                + (baselineSequence >= 0L
                    ? " baselineSequence=" + baselineSequence.ToString(CultureInfo.InvariantCulture)
                    : string.Empty)
                + " lifecycle=" + FormatReplicationWorldObjectDetailToken(lifecycle)
                + " state=" + (state.Exists ? "active" : "removed")
                + " phase=" + FormatReplicationWorldObjectDetailToken(state.Phase)
                + " remainingTimeMs=" + state.RemainingTimeMs.ToString(CultureInfo.InvariantCulture)
                + " underConstruction=" + (state.UnderConstruction ? "true" : "false")
                + " progressing=" + (state.Progressing ? "true" : "false")
                + " forbidden=" + (state.Forbidden ? "true" : "false")
                + " markedForDestruction=" + (state.MarkedForDestruction ? "true" : "false");
            if (!includeReplay)
            {
                return detail;
            }

            return tracked.CanonicalRecord != null
                ? detail
                    + " buildReplay=exact buildRecordBlueprintB64="
                    + EncodeReplicationDetailBase64(tracked.BlueprintId)
                    + " buildRecordB64="
                    + EncodeReplicationDetailBase64(
                        FormatReplicationCanonicalBuildPlacementRecord(tracked.CanonicalRecord))
                : detail + " buildReplay=resync-required";
        }

        private static bool TryApplyReplicationBuildingLifecycleV2(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            if (!TryReadReplicationBuildingLifecycleEnvelopeV2(
                    delta,
                    out var epoch,
                    out var revision,
                    out var state,
                    out var phase,
                    out var remainingTimeMs,
                    out var progressing,
                    out var forbidden,
                    out var markedForDestruction,
                    out detail))
            {
                return false;
            }

            var buildingKey = delta.UniqueId.ToString(CultureInfo.InvariantCulture);
            var disposition = replicationClientBuildingRevisionLedgerV2.EvaluateLiveDelta(
                epoch,
                buildingKey,
                revision);
            if (disposition != BuildingRevisionDisposition.Apply)
            {
                if (disposition == BuildingRevisionDisposition.Duplicate
                    || disposition == BuildingRevisionDisposition.StaleRevision
                    || disposition == BuildingRevisionDisposition.StaleEpoch
                    || disposition == BuildingRevisionDisposition.SupersededByNewerLiveDelta)
                {
                    detail = "ok building-lifecycle-v2-stale disposition=" + disposition.ToString();
                    return true;
                }

                detail = "building-lifecycle-v2-ledger-rejected disposition=" + disposition.ToString();
                return false;
            }

            if (!TryApplyReplicationBuildingNativeStateV2(
                    delta,
                    state,
                    phase,
                    remainingTimeMs,
                    progressing,
                    forbidden,
                    markedForDestruction,
                    allowExactRepairSeed: false,
                    out detail))
            {
                detail = "building-lifecycle-v2-repair-required hostId="
                    + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + FormatReplicationWorldObjectDetailToken(detail);
                return false;
            }

            var commit = replicationClientBuildingRevisionLedgerV2.CommitLiveDelta(
                epoch,
                buildingKey,
                revision,
                delta.Sequence);
            if (commit != BuildingRevisionDisposition.Apply)
            {
                detail = "building-lifecycle-v2-ledger-commit-failed disposition=" + commit.ToString();
                return false;
            }

            if (string.Equals(state, "removed", StringComparison.Ordinal))
            {
                ReplicationClientBuildingTerminalRevisionV2[delta.UniqueId] = revision;
                RemoveReplicationClientBuildingPresentationV2(delta.UniqueId);
            }
            else
            {
                ReplicationClientBuildingTerminalRevisionV2.Remove(delta.UniqueId);
                SetReplicationClientBuildingPresentationAnchorV2(
                    delta.UniqueId,
                    revision,
                    remainingTimeMs,
                    progressing);
            }

            replicationBuildingLifecycleDeltasAppliedV2++;
            detail = "ok building-lifecycle-v2 " + detail;
            return true;
        }

        private static bool TryReadReplicationBuildingLifecycleEnvelopeV2(
            ReplicationWorldObjectDelta delta,
            out long epoch,
            out long revision,
            out string state,
            out string phase,
            out int remainingTimeMs,
            out bool progressing,
            out bool forbidden,
            out bool markedForDestruction,
            out string detail)
        {
            epoch = -1L;
            revision = -1L;
            state = string.Empty;
            phase = string.Empty;
            remainingTimeMs = 0;
            progressing = false;
            forbidden = false;
            markedForDestruction = false;
            if (!replicationConfigBuildingReplicationV2
                || delta.UniqueId <= 0L
                || !TryReadReplicationWorldObjectDetailLong(delta.Detail, "epoch", out epoch)
                || epoch != GetReplicationBuildBatchEpoch()
                || !TryReadReplicationWorldObjectDetailLong(delta.Detail, "revision", out revision)
                || revision <= 0L
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "state", out state)
                || (!string.Equals(state, "active", StringComparison.Ordinal)
                    && !string.Equals(state, "removed", StringComparison.Ordinal))
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out phase)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "remainingTimeMs", out remainingTimeMs)
                || remainingTimeMs < 0
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "progressing", out progressing)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "forbidden", out forbidden)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "markedForDestruction", out markedForDestruction))
            {
                detail = "building-lifecycle-v2-malformed-or-epoch-mismatch expectedEpoch="
                    + GetReplicationBuildBatchEpoch().ToString(CultureInfo.InvariantCulture)
                    + " receivedEpoch=" + epoch.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            detail = "ok";
            return true;
        }

        private static bool TryApplyReplicationBuildingNativeStateV2(
            ReplicationWorldObjectDelta delta,
            string state,
            string phase,
            int remainingTimeMs,
            bool progressing,
            bool forbidden,
            bool markedForDestruction,
            bool allowExactRepairSeed,
            out string detail)
        {
            if (!TryResolveReplicationBuildingTargetV2(
                    delta.UniqueId,
                    out var candidate,
                    out var buildingInstance,
                    out var lookupDetail))
            {
                if (allowExactRepairSeed
                    && TryResolveReplicationBuildingRepairTargetV2(
                        delta,
                        state,
                        out candidate,
                        out buildingInstance,
                        out lookupDetail))
                {
                }
                else if (string.Equals(state, "removed", StringComparison.Ordinal))
                {
                    RemoveReplicationHostIdentity(
                        delta.UniqueId,
                        null,
                        "building-lifecycle-v2-already-removed");
                    detail = "already-removed " + lookupDetail;
                    return true;
                }
                else
                {
                    detail = "identity-missing " + lookupDetail;
                    return false;
                }
            }

            if (candidate == null || buildingInstance == null)
            {
                detail = "identity-resolved-without-instance";
                return false;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                if (string.Equals(state, "removed", StringComparison.Ordinal))
                {
                    if (!TryDestroyReplicationBuildingNativeV2(buildingInstance, out var destroyDetail))
                    {
                        detail = destroyDetail;
                        return false;
                    }

                    RemoveReplicationHostIdentity(
                        delta.UniqueId,
                        candidate,
                        "building-lifecycle-v2-removed");
                    ReplicationClientBuildingProgressingV2.Remove(delta.UniqueId);
                    detail = "removed " + destroyDetail;
                    return true;
                }

                var finishing = !progressing
                    && (phase.IndexOf("Finished", StringComparison.OrdinalIgnoreCase) >= 0
                        || phase.IndexOf("Built", StringComparison.OrdinalIgnoreCase) >= 0);
                var remainingApplied = true;
                var remainingDetail = "deferred-until-after-phase";
                if (finishing)
                {
                    // The progress event wiring can be disposed by completion. Apply
                    // the final authoritative baseline before that native transition.
                    remainingApplied = TryApplyReplicationBuildingRemainingTime(
                        buildingInstance,
                        remainingTimeMs,
                        out remainingDetail);
                }

                var phaseApplied = TryApplyReplicationBuildingPhaseNativeV2(
                    buildingInstance,
                    phase,
                    out var phaseDetail);
                bool progressingApplied;
                string progressingDetail;
                if (finishing)
                {
                    // ConstructionCompleted owns the terminal presentation transition.
                    // Calling ConstructionPaused afterwards can reopen jobs/visuals on
                    // some native branches, so only update the local presentation cursor.
                    ReplicationClientBuildingProgressingV2[delta.UniqueId] = false;
                    progressingApplied = true;
                    progressingDetail = "completed";
                }
                else
                {
                    progressingApplied = TryApplyReplicationBuildingProgressingNativeV2(
                        delta.UniqueId,
                        buildingInstance,
                        progressing,
                        out progressingDetail);
                }
                if (!finishing)
                {
                    remainingApplied = TryApplyReplicationBuildingRemainingTime(
                        buildingInstance,
                        remainingTimeMs,
                        out remainingDetail);
                }
                var forbiddenApplied = TryApplyReplicationBuildingForbiddenNativeV2(
                    buildingInstance,
                    forbidden,
                    out var forbidDetail);
                var markApplied = TryApplyReplicationBuildingDestructionMarkNativeV2(
                    buildingInstance,
                    markedForDestruction,
                    out var markDetail);
                if (!phaseApplied
                    || !progressingApplied
                    || !remainingApplied
                    || !forbiddenApplied
                    || !markApplied)
                {
                    detail = "native-state-apply-failed phase=" + phaseDetail
                        + " progressing=" + progressingDetail
                        + " remaining=" + remainingDetail
                        + " forbid=" + forbidDetail
                        + " mark=" + markDetail;
                    return false;
                }

                detail = lookupDetail
                    + " phase=" + phaseDetail
                    + " progressing=" + progressingDetail
                    + " remaining=" + remainingDetail
                    + " forbid=" + forbidDetail
                    + " mark=" + markDetail;
                return true;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool TryResolveReplicationBuildingTargetV2(
            long hostId,
            out object? candidate,
            out object? buildingInstance,
            out string detail)
        {
            candidate = null;
            buildingInstance = null;
            if (TryGetReplicationLocalObjectByHostId(hostId, out candidate, out var mapDetail)
                && candidate != null
                && TryResolveReplicationBuildingInstanceV2(candidate, out buildingInstance, out var instanceDetail)
                && buildingInstance != null)
            {
                detail = mapDetail + " " + instanceDetail;
                return true;
            }

            if (TryMapReplicationLocalBuildingByNativeUniqueIdV2(
                    hostId,
                    out candidate,
                    out buildingInstance,
                    out detail))
            {
                return true;
            }

            detail = "host-id-not-mapped hostId=" + hostId.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryMapReplicationLocalBuildingByNativeUniqueIdV2(
            long hostId,
            out object? candidate,
            out object? buildingInstance,
            out string detail)
        {
            candidate = null;
            buildingInstance = null;
            if (!TryResolveReplicationBuildingsManagerMain(out var manager, out var managerDetail)
                || manager == null
                || !TryReadInstanceMemberValue(
                    manager,
                    "UniqueIdBuildingDictionary",
                    out var dictionaryValue)
                || !(dictionaryValue is IDictionary dictionary))
            {
                detail = "native-building-dictionary-missing " + managerDetail;
                return false;
            }

            object? nativeValue = null;
            try
            {
                // The installed game exposes Dictionary<int, BaseBuildingInstance>.
                // Use its native key instead of scanning every building on a cache miss.
                if (hostId >= int.MinValue && hostId <= int.MaxValue)
                {
                    var intKey = (int)hostId;
                    if (dictionary.Contains(intKey))
                    {
                        nativeValue = dictionary[intKey];
                    }
                }

                if (nativeValue == null && dictionary.Contains(hostId))
                {
                    nativeValue = dictionary[hostId];
                }
            }
            catch (Exception ex)
            {
                detail = "native-id-lookup-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }

            if (nativeValue == null)
            {
                detail = "native-id-not-found hostId=" + hostId.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            candidate = nativeValue;
            if (!TryResolveReplicationBuildingInstanceV2(
                    candidate,
                    out buildingInstance,
                    out var instanceDetail)
                || buildingInstance == null)
            {
                detail = "native-building-instance-missing " + instanceDetail;
                return false;
            }

            RegisterReplicationHostIdentity(hostId, candidate, "building-lifecycle-v2-native-id-bootstrap");
            detail = "native-id-bootstrap " + managerDetail;
            return true;
        }

        private static bool TryApplyReplicationBuildingPhaseNativeV2(
            object buildingInstance,
            string phase,
            out string detail)
        {
            if (string.IsNullOrWhiteSpace(phase)
                || (TryReadReplicationBuildingPhase(buildingInstance, out var localPhase)
                    && string.Equals(localPhase, phase, StringComparison.OrdinalIgnoreCase)))
            {
                detail = "unchanged";
                return true;
            }

            try
            {
                MethodInfo? method;
                object[] arguments;
                if (phase.IndexOf("Finished", StringComparison.OrdinalIgnoreCase) >= 0
                    || phase.IndexOf("Built", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    method = FindReplicationInstanceMethod(
                        buildingInstance.GetType(),
                        "ConstructionCompleted",
                        Type.EmptyTypes);
                    arguments = Array.Empty<object>();
                }
                else if (phase.IndexOf("Foundation", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    method = FindReplicationInstanceMethod(
                        buildingInstance.GetType(),
                        "EnterFoundationState",
                        new[] { typeof(bool) });
                    arguments = new object[] { false };
                }
                else if (phase.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0
                    || phase.IndexOf("Preview", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    method = FindReplicationInstanceMethod(
                        buildingInstance.GetType(),
                        "ReturnToBlueprint",
                        Type.EmptyTypes);
                    arguments = Array.Empty<object>();
                }
                else
                {
                    method = null;
                    arguments = Array.Empty<object>();
                    var methods = buildingInstance.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    for (var i = 0; i < methods.Length; i++)
                    {
                        var parameters = methods[i].GetParameters();
                        if (!string.Equals(methods[i].Name, "SetConstructionPhase", StringComparison.Ordinal)
                            || parameters.Length != 2
                            || !parameters[0].ParameterType.IsEnum
                            || parameters[1].ParameterType != typeof(bool))
                        {
                            continue;
                        }

                        var enumValue = Enum.Parse(parameters[0].ParameterType, phase, ignoreCase: true);
                        method = methods[i];
                        arguments = new[] { enumValue, (object)false };
                        break;
                    }
                }

                if (method == null)
                {
                    detail = "method-missing phase=" + phase;
                    return false;
                }

                method.Invoke(buildingInstance, arguments);
                detail = method.Name;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationBuildingProgressingNativeV2(
            long hostId,
            object buildingInstance,
            bool progressing,
            out string detail)
        {
            if (ReplicationClientBuildingProgressingV2.TryGetValue(hostId, out var current)
                && current == progressing)
            {
                detail = "unchanged";
                return true;
            }

            if (!progressing && !ReplicationClientBuildingProgressingV2.ContainsKey(hostId))
            {
                ReplicationClientBuildingProgressingV2[hostId] = false;
                detail = "initial-paused";
                return true;
            }

            try
            {
                var methodName = progressing ? "ConstructionStarted" : "ConstructionPaused";
                var method = FindReplicationInstanceMethod(
                    buildingInstance.GetType(),
                    methodName,
                    Type.EmptyTypes);
                if (method == null)
                {
                    detail = "method-missing " + methodName;
                    return false;
                }

                method.Invoke(buildingInstance, null);
                ReplicationClientBuildingProgressingV2[hostId] = progressing;
                detail = methodName;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationBuildingForbiddenNativeV2(
            object buildingInstance,
            bool forbidden,
            out string detail)
        {
            if (TryReadReplicationBoolMember(
                    buildingInstance,
                    "IsForbidden",
                    "isForbidden",
                    out var current)
                && current == forbidden)
            {
                detail = "unchanged";
                return true;
            }

            try
            {
                var setter = AccessTools.PropertySetter(buildingInstance.GetType(), "IsForbidden");
                if (setter == null)
                {
                    detail = "setter-missing";
                    return false;
                }

                setter.Invoke(buildingInstance, new object[] { forbidden });
                detail = "set_IsForbidden";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationBuildingDestructionMarkNativeV2(
            object buildingInstance,
            bool marked,
            out string detail)
        {
            if (TryReadReplicationBoolMember(
                    buildingInstance,
                    "MarkedForDestruction",
                    "markedForDestruction",
                    out var current)
                && current == marked)
            {
                detail = "unchanged";
                return true;
            }

            try
            {
                var method = FindReplicationInstanceMethod(
                    buildingInstance.GetType(),
                    "SetMarkedForDestruction",
                    new[] { typeof(bool) });
                if (method == null)
                {
                    detail = "method-missing";
                    return false;
                }

                method.Invoke(buildingInstance, new object[] { marked });
                detail = "SetMarkedForDestruction";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryDestroyReplicationBuildingNativeV2(
            object buildingInstance,
            out string detail)
        {
            if (!TryResolveReplicationBuildingsManagerMain(out var manager, out var managerDetail)
                || manager == null)
            {
                detail = "manager-missing " + managerDetail;
                return false;
            }

            var methods = manager.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                var parameters = methods[i].GetParameters();
                if (!string.Equals(methods[i].Name, "DestroyBuilding", StringComparison.Ordinal)
                    || parameters.Length != 3
                    || !parameters[0].ParameterType.IsInstanceOfType(buildingInstance)
                    || parameters[1].ParameterType != typeof(bool)
                    || parameters[2].ParameterType != typeof(bool))
                {
                    continue;
                }

                try
                {
                    // The host emits one terminal delta for every stability-cascade
                    // dependent. Never let the client author a second local cascade.
                    methods[i].Invoke(manager, new[] { buildingInstance, (object)false, (object)true });
                    detail = "BuildingsManagerMain.DestroyBuilding(skipStabilityCheck=true)";
                    return true;
                }
                catch (Exception ex)
                {
                    detail = FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            detail = "DestroyBuilding-method-missing";
            return false;
        }

        private static bool TryApplyReplicationBuildingProgressV2(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            if (!replicationConfigBuildingReplicationV2
                || delta.UniqueId <= 0L
                || !TryReadReplicationWorldObjectDetailLong(delta.Detail, "epoch", out var epoch)
                || epoch != GetReplicationBuildBatchEpoch()
                || !TryReadReplicationWorldObjectDetailLong(delta.Detail, "revision", out var revision)
                || revision < 0L
                || !TryReadReplicationWorldObjectDetailInt(
                    delta.Detail,
                    "remainingTimeMs",
                    out var remainingTimeMs)
                || remainingTimeMs < 0
                || !TryResolveReplicationBuildingTargetV2(
                    delta.UniqueId,
                    out var candidate,
                    out var buildingInstance,
                    out var lookupDetail)
                || candidate == null
                || buildingInstance == null)
            {
                detail = "building-progress-v2-target-or-epoch-missing";
                return false;
            }

            var buildingKey = delta.UniqueId.ToString(CultureInfo.InvariantCulture);
            if (ReplicationClientBuildingTerminalRevisionV2.ContainsKey(delta.UniqueId))
            {
                detail = "ok building-progress-v2-terminal-skipped";
                return true;
            }

            if (replicationClientBuildingRevisionLedgerV2.TryGetCursor(buildingKey, out var cursor))
            {
                if (revision != cursor.Revision)
                {
                    detail = "ok building-progress-v2-revision-skipped revision="
                        + revision.ToString(CultureInfo.InvariantCulture)
                        + " applied="
                        + cursor.Revision.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }
            else if (revision != 0L)
            {
                // Progress is presentation-only and never advances the lifecycle
                // cursor. A correction cannot run ahead of its authoritative start.
                detail = "ok building-progress-v2-without-lifecycle-skipped revision="
                    + revision.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (TryReadReplicationBuildingPhase(buildingInstance, out var localPhase)
                && (localPhase.IndexOf("Finished", StringComparison.OrdinalIgnoreCase) >= 0
                    || localPhase.IndexOf("Built", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                detail = "ok building-progress-v2-finished-skipped";
                return true;
            }

            if (!TryResolveReplicationBuildingProgressPresentationV2(
                    candidate,
                    buildingInstance,
                    out var buildProgress,
                    out var updateProgressMethod)
                || buildProgress == null
                || updateProgressMethod == null)
            {
                detail = "building-progress-v2-presentation-missing " + lookupDetail;
                return false;
            }

            try
            {
                updateProgressMethod.Invoke(
                    buildProgress,
                    new object[] { remainingTimeMs / 1000f });

                detail = "ok building-progress-v2 remainingTimeMs="
                    + remainingTimeMs.ToString(CultureInfo.InvariantCulture)
                    + " " + lookupDetail
                    + " view=BuildProgress.UpdateProgress";
                CorrectReplicationClientBuildingPresentationV2(
                    delta.UniqueId,
                    revision,
                    remainingTimeMs);
                return true;
            }
            catch (Exception ex)
            {
                detail = "building-progress-v2-presentation-failed "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryHandleReplicationBuildingProgressRequestV2(
            LockstepCommand command,
            out RuntimeCommandResult result)
        {
            result = new RuntimeCommandResult(false, "not-building-progress-v2");
            if (!replicationConfigBuildingReplicationV2
                || command.Kind != CommandKind.Custom
                || string.IsNullOrWhiteSpace(command.PayloadJson)
                || !command.PayloadJson.StartsWith(
                    ReplicationBuildingProgressV2CommandPrefix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var values = command.PayloadJson.Substring(
                    ReplicationBuildingProgressV2CommandPrefix.Length)
                .Split(new[] { '|' }, StringSplitOptions.None);
            if (values.Length != 2
                || !long.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                || epoch != GetReplicationBuildBatchEpoch()
                || !long.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostId)
                || hostId <= 0L)
            {
                result = new RuntimeCommandResult(false, "building-progress-v2-request-malformed-or-epoch-mismatch");
                return true;
            }

            if (!ReplicationTrackedHostBuildingsV2.TryGetValue(hostId, out var tracked))
            {
                if (!TryMapReplicationLocalBuildingByNativeUniqueIdV2(
                        hostId,
                        out var candidate,
                        out _,
                        out _)
                    || candidate == null
                    || !TryEnsureReplicationHostBuildingTrackerV2(candidate, out tracked, out _)
                    || tracked == null)
                {
                    result = new RuntimeCommandResult(
                        false,
                        "building-progress-v2-host-target-missing hostId="
                            + hostId.ToString(CultureInfo.InvariantCulture));
                    return true;
                }
            }

            var current = instance;
            if (!TryCaptureReplicationBuildingAbsoluteStateV2(tracked, out var state)
                || !state.Exists
                || current is null)
            {
                result = new RuntimeCommandResult(
                    false,
                    "building-progress-v2-host-state-missing hostId="
                        + hostId.ToString(CultureInfo.InvariantCulture));
                return true;
            }

            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationBuildingProgressV2DeltaKind,
                tracked.HostId,
                tracked.BlueprintId,
                tracked.GridX,
                tracked.GridY,
                tracked.GridZ,
                "epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + tracked.Revision.ToString(CultureInfo.InvariantCulture)
                    + " remainingTimeMs=" + state.RemainingTimeMs.ToString(CultureInfo.InvariantCulture)
                    + " source=selected-building-request"));
            result = new RuntimeCommandResult(
                true,
                "building-progress-v2-sent hostId=" + hostId.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private static void SendClientSelectedBuildingProgressRequestV2IfDue()
        {
            if (Time.realtimeSinceStartup < replicationNextBuildingProgressRequestRealtimeV2
                || !ShouldSendReplicationLocalCommandIntent())
            {
                return;
            }

            replicationNextBuildingProgressRequestRealtimeV2 =
                Time.realtimeSinceStartup + ReplicationBuildingProgressV2RequestSeconds;
            if (!TryResolveReplicationSelectedBuildingHostIdV2(out var hostId))
            {
                replicationClientSelectedBuildingHostIdV2 = 0L;
                return;
            }

            replicationClientSelectedBuildingHostIdV2 = hostId;
            var command = new LockstepCommand(
                ReplicationClientPeerId,
                ++replicationIntentSequence,
                0L,
                CommandKind.Custom,
                ReplicationBuildingProgressV2CommandPrefix
                    + GetReplicationBuildBatchEpoch().ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + hostId.ToString(CultureInfo.InvariantCulture),
                null,
                null,
                null,
                null);
            SendReplicationLocalCommandIntent(command, "selected-building-progress-v2");
        }

        private static bool TryResolveReplicationSelectedBuildingHostIdV2(out long hostId)
        {
            hostId = 0L;
            try
            {
                var selectionManagerType = AccessTools.TypeByName(
                    "NSMedieval.Managers.Selection.SelectionManager");
                if (selectionManagerType == null)
                {
                    return false;
                }

                var selectionManager = ResolveReplicationUnityManagerInstance(selectionManagerType);
                if (selectionManager == null)
                {
                    return false;
                }

                object? selected = null;
                var methodNames = new[] { "GetFirstSelected", "GetCurrenlySelected" };
                for (var i = 0; i < methodNames.Length && selected == null; i++)
                {
                    var method = AccessTools.Method(selectionManagerType, methodNames[i], Type.EmptyTypes);
                    if (method != null)
                    {
                        selected = method.Invoke(selectionManager, null);
                    }
                }

                if (selected == null)
                {
                    var memberNames = new[]
                    {
                        "CurrentSelectedGameObject",
                        "currentSelectedGameObject",
                        "FirstSelectedGameObject",
                        "firstSelectedGameObject"
                    };
                    for (var i = 0; i < memberNames.Length && selected == null; i++)
                    {
                        TryReadInstanceMemberValue(selectionManager, memberNames[i], out selected);
                    }
                }

                return TryResolveReplicationSelectedObjectHostIdV2(selected, out hostId);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveReplicationSelectedObjectHostIdV2(
            object? selected,
            out long hostId)
        {
            hostId = 0L;
            if (selected == null)
            {
                return false;
            }

            if (TryGetReplicationHostIdForLocalObjectV2(selected, out hostId))
            {
                return true;
            }

            GameObject? gameObject = selected as GameObject;
            if (selected is Component component)
            {
                gameObject = component.gameObject;
            }

            if (gameObject == null)
            {
                return false;
            }

            var viewType = AccessTools.TypeByName(
                "NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            var view = viewType == null ? null : gameObject.GetComponent(viewType);
            if (view == null)
            {
                return false;
            }

            if (TryGetReplicationHostIdForLocalObjectV2(view, out hostId))
            {
                return true;
            }

            if (!TryResolveReplicationBuildingInstanceV2(view, out var buildingInstance, out _)
                || buildingInstance == null)
            {
                return false;
            }

            if (TryGetReplicationHostIdForLocalObjectV2(buildingInstance, out hostId))
            {
                return true;
            }

            if (TryReadReplicationWorldObjectLongMember(
                    buildingInstance,
                    "UniqueId",
                    "uniqueId",
                    out hostId)
                && hostId > 0L)
            {
                RegisterReplicationHostIdentity(
                    hostId,
                    buildingInstance,
                    "building-lifecycle-v2-selected-native-id");
                return true;
            }

            hostId = 0L;
            return false;
        }

        private static bool TryGetReplicationHostIdForLocalObjectV2(object candidate, out long hostId)
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                return ReplicationHostIdByLocalObject.TryGetValue(
                        GetReplicationLocalObjectKey(candidate),
                        out hostId)
                    && hostId > 0L;
            }
        }

        private static void EnsureReplicationBuildingIdentityBootstrapV2()
        {
            var epoch = (int)GetReplicationBuildBatchEpoch();
            if (replicationBuildingIdentityBootstrapEpochV2 == epoch)
            {
                return;
            }

            if (Time.realtimeSinceStartup < replicationNextBuildingIdentityBootstrapRealtimeV2)
            {
                return;
            }

            if (!TryResolveReplicationBuildingsManagerMain(out var manager, out _)
                || manager == null
                || !TryReadInstanceMemberValue(
                    manager,
                    "UniqueIdBuildingDictionary",
                    out var dictionaryValue)
                || !(dictionaryValue is IDictionary dictionary))
            {
                return;
            }

            if (dictionary.Count == 0)
            {
                // The manager can exist before checkpoint deserialization populates
                // it. Do not permanently certify an empty early observation.
                replicationNextBuildingIdentityBootstrapRealtimeV2 =
                    Time.realtimeSinceStartup + 1f;
                return;
            }

            var mapped = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key == null || entry.Value == null)
                {
                    continue;
                }

                try
                {
                    var hostId = Convert.ToInt64(entry.Key, CultureInfo.InvariantCulture);
                    if (hostId <= 0L)
                    {
                        continue;
                    }

                    RegisterReplicationHostIdentity(
                        hostId,
                        entry.Value,
                        "building-lifecycle-v2-save-bootstrap");
                    mapped++;
                }
                catch
                {
                }
            }

            replicationBuildingIdentityBootstrapEpochV2 = epoch;
            replicationBuildingIdentityBootstrapCountV2 = mapped;
            replicationNextBuildingIdentityBootstrapRealtimeV2 = 0f;
            instance?.LogReplicationInfo("Going Cooperative building lifecycle v2 identity bootstrap epoch="
                + epoch.ToString(CultureInfo.InvariantCulture)
                + " mapped="
                + mapped.ToString(CultureInfo.InvariantCulture));
        }

        private static bool TryValidateReplicationBuildingDeltaEpochV2(
            ReplicationWorldObjectDelta delta,
            out bool handled,
            out bool applied,
            out string detail)
        {
            handled = false;
            applied = false;
            detail = string.Empty;
            if (!string.Equals(delta.DeltaKind, ReplicationBuildingLifecycleV2DeltaKind, StringComparison.Ordinal)
                && !string.Equals(delta.DeltaKind, ReplicationBuildingProgressV2DeltaKind, StringComparison.Ordinal)
                && !string.Equals(delta.DeltaKind, ReplicationBuildingRepairV2DeltaKind, StringComparison.Ordinal)
                && !string.Equals(delta.DeltaKind, ReplicationBuildingRecoveryRequiredV2DeltaKind, StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryReadReplicationWorldObjectDetailLong(delta.Detail, "epoch", out var wireEpoch))
            {
                handled = true;
                detail = "building-v2-epoch-malformed";
                return true;
            }

            var currentEpoch = GetReplicationBuildBatchEpoch();
            if (wireEpoch < currentEpoch)
            {
                handled = true;
                applied = true;
                detail = "ok building-v2-stale-epoch expected="
                    + currentEpoch.ToString(CultureInfo.InvariantCulture)
                    + " received="
                    + wireEpoch.ToString(CultureInfo.InvariantCulture);
            }
            else if (wireEpoch > currentEpoch)
            {
                handled = true;
                detail = "building-v2-future-epoch expected="
                    + currentEpoch.ToString(CultureInfo.InvariantCulture)
                    + " received="
                    + wireEpoch.ToString(CultureInfo.InvariantCulture);
            }

            return true;
        }

        private static bool TryHandleReplicationBuildingLifecycleRepairAckV2(
            ReplicationWorldObjectDeltaAck ack)
        {
            if (ack.Applied
                || ack.Detail.IndexOf(
                    "building-lifecycle-v2-repair-required",
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            ReplicationWorldObjectDelta? lifecycleDelta = null;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (replicationPendingWorldObjectDeltas.TryGetValue(ack.Sequence, out var pending)
                    && string.Equals(
                        pending.Delta.DeltaKind,
                        ReplicationBuildingLifecycleV2DeltaKind,
                        StringComparison.Ordinal))
                {
                    lifecycleDelta = pending.Delta;
                }
            }

            return lifecycleDelta != null
                && TrySendReplicationBuildingRepairV2(lifecycleDelta, "negative-ack");
        }

        private static bool TrySendReplicationBuildingRecoveryRequiredV2(
            ReplicationWorldObjectDelta sourceDelta,
            string reason)
        {
            var current = instance;
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigHostMode
                || current is null
                || !ReplicationBuildingRecoveryEscalatedSourceSequencesV2.Add(sourceDelta.Sequence))
            {
                return false;
            }

            var epoch = GetReplicationBuildBatchEpoch();
            var pendingRecoveryKey = ReplicationBuildingRecoveryRequiredV2DeltaKind
                + "|epoch=" + epoch.ToString(CultureInfo.InvariantCulture);
            var forceRecovery = false;
            lock (ReplicationWorldObjectDeltaLock)
            {
                forceRecovery = ReplicationPendingSupersedableWorldDeltaSequenceByKey.TryGetValue(
                        pendingRecoveryKey,
                        out var pendingRecoverySequence)
                    && replicationPendingWorldObjectDeltas.ContainsKey(pendingRecoverySequence);
            }

            var detail = "epoch="
                + epoch.ToString(CultureInfo.InvariantCulture)
                + " sourceSequence=" + sourceDelta.Sequence.ToString(CultureInfo.InvariantCulture)
                + " sourceKind=" + FormatReplicationWorldObjectDetailToken(sourceDelta.DeltaKind)
                + " forceRecovery=" + (forceRecovery ? "true" : "false")
                + " reason=" + FormatReplicationWorldObjectDetailToken(reason);
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationBuildingRecoveryRequiredV2DeltaKind,
                sourceDelta.UniqueId,
                sourceDelta.BlueprintId,
                sourceDelta.GridX,
                sourceDelta.GridY,
                sourceDelta.GridZ,
                detail));
            current.LogReplicationWarning(
                "Going Cooperative building V2 requested client recovery " + detail);
            return true;
        }

        private static bool TryApplyReplicationBuildingRecoveryRequiredV2(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            if (!replicationConfigBuildingReplicationV2
                || !TryReadReplicationWorldObjectDetailLong(delta.Detail, "epoch", out var epoch)
                || epoch != GetReplicationBuildBatchEpoch()
                || !TryReadReplicationWorldObjectDetailLong(
                    delta.Detail,
                    "sourceSequence",
                    out var sourceSequence)
                || sourceSequence <= 0L
                || !TryReadReplicationWorldObjectDetailBool(
                    delta.Detail,
                    "forceRecovery",
                    out var forceRecovery)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "reason", out var reason))
            {
                detail = "building-recovery-required-v2-malformed-or-epoch-mismatch";
                return false;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!forceRecovery
                    && replicationClientAppliedWorldObjectDeltaSequences.Contains(sourceSequence))
                {
                    // Retry exhaustion can mean only that the ACK was lost. If the
                    // exact durable source already committed locally, acknowledge the
                    // escalation without needlessly reloading the whole save.
                    detail = "ok building-recovery-required-v2-source-already-applied sourceSequence="
                        + sourceSequence.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            ScheduleReplicationBuildBatchRecovery(
                "host-required sourceSequence="
                    + sourceSequence.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + reason);
            detail = "ok building-recovery-required-v2-scheduled sourceSequence="
                + sourceSequence.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static void CompleteReplicationBuildingLifecycleAcknowledgementV2(
            ReplicationWorldObjectDeltaAck ack,
            ReplicationWorldObjectDelta? acknowledgedDelta)
        {
            if (acknowledgedDelta == null
                || (!string.Equals(
                        acknowledgedDelta.DeltaKind,
                        ReplicationBuildingLifecycleV2DeltaKind,
                        StringComparison.Ordinal)
                    && !string.Equals(
                        acknowledgedDelta.DeltaKind,
                        ReplicationBuildingRepairV2DeltaKind,
                        StringComparison.Ordinal)))
            {
                return;
            }

            if (string.Equals(
                acknowledgedDelta.DeltaKind,
                ReplicationBuildingRepairV2DeltaKind,
                StringComparison.Ordinal))
            {
                ReplicationBuildingRepairLastSentRealtimeV2.Remove(acknowledgedDelta.UniqueId);
            }

            var positive = ack.Applied
                || (ack.Duplicate
                    && ack.Detail.IndexOf(
                        "duplicate-sequence-skipped",
                        StringComparison.OrdinalIgnoreCase) >= 0);
            if (!positive
                || !TryReadReplicationWorldObjectDetailToken(
                    acknowledgedDelta.Detail,
                    "state",
                    out var state)
                || !string.Equals(state, "removed", StringComparison.Ordinal))
            {
                return;
            }

            // Retain terminal topology until the exact terminal row/repair is known
            // applied. Afterwards it is dead state and would otherwise grow forever.
            ReplicationTrackedHostBuildingsV2.Remove(acknowledgedDelta.UniqueId);
            RetireReplicationBuildingConstructionMaterialsV2(acknowledgedDelta.UniqueId);
            ReplicationBuildingRepairLastSentRealtimeV2.Remove(acknowledgedDelta.UniqueId);
        }

        private static bool TrySendReplicationBuildingRepairV2(
            ReplicationWorldObjectDelta lifecycleDelta,
            string reason)
        {
            var current = instance;
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigHostMode
                || !ReplicationTrackedHostBuildingsV2.TryGetValue(
                    lifecycleDelta.UniqueId,
                    out var tracked)
                || current is null)
            {
                return false;
            }

            if (ReplicationBuildingRepairLastSentRealtimeV2.TryGetValue(
                    tracked.HostId,
                    out var lastSent)
                && Time.realtimeSinceStartup - lastSent < ReplicationBuildingRepairV2CooldownSeconds)
            {
                return true;
            }

            var state = tracked.HasLastSentState
                ? tracked.LastSentState
                : ReplicationBuildingAbsoluteStateV2.Removed;
            var detail = FormatReplicationBuildingLifecycleStateV2(
                    tracked,
                    state,
                    "Repair:" + reason,
                    lifecycleDelta.Sequence,
                    includeReplay: true)
                + " source=targeted-building-repair-v2";
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationBuildingRepairV2DeltaKind,
                tracked.HostId,
                tracked.BlueprintId,
                tracked.GridX,
                tracked.GridY,
                tracked.GridZ,
                detail));
            ReplicationBuildingRepairLastSentRealtimeV2[tracked.HostId] = Time.realtimeSinceStartup;
            replicationBuildingRepairDeltasSentV2++;
            return true;
        }

        private static bool TryApplyReplicationBuildingRepairV2(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            if (!TryReadReplicationBuildingLifecycleEnvelopeV2(
                    delta,
                    out var epoch,
                    out var revision,
                    out var state,
                    out var phase,
                    out var remainingTimeMs,
                    out var progressing,
                    out var forbidden,
                    out var markedForDestruction,
                    out detail)
                || !TryReadReplicationWorldObjectDetailLong(
                    delta.Detail,
                    "baselineSequence",
                    out var baselineSequence)
                || baselineSequence < 0L)
            {
                detail = "building-repair-v2-malformed " + detail;
                return false;
            }

            var buildingKey = delta.UniqueId.ToString(CultureInfo.InvariantCulture);
            var disposition = replicationClientBuildingRevisionLedgerV2.EvaluateRecoveryRow(
                epoch,
                buildingKey,
                revision,
                baselineSequence);
            if (disposition != BuildingRevisionDisposition.Apply)
            {
                if (disposition == BuildingRevisionDisposition.Duplicate
                    || disposition == BuildingRevisionDisposition.StaleRevision
                    || disposition == BuildingRevisionDisposition.StaleEpoch
                    || disposition == BuildingRevisionDisposition.SupersededByNewerLiveDelta)
                {
                    detail = "ok building-repair-v2-stale disposition=" + disposition.ToString();
                    return true;
                }

                detail = "building-repair-v2-ledger-rejected disposition=" + disposition.ToString();
                return false;
            }

            if (!TryApplyReplicationBuildingNativeStateV2(
                    delta,
                    state,
                    phase,
                    remainingTimeMs,
                    progressing,
                    forbidden,
                    markedForDestruction,
                    allowExactRepairSeed: true,
                    out detail))
            {
                detail = "building-repair-v2-apply-failed " + detail;
                return false;
            }

            var commit = replicationClientBuildingRevisionLedgerV2.CommitRecoveryRow(
                epoch,
                buildingKey,
                revision,
                baselineSequence);
            if (commit != BuildingRevisionDisposition.Apply)
            {
                detail = "building-repair-v2-ledger-commit-failed disposition=" + commit.ToString();
                return false;
            }

            if (string.Equals(state, "removed", StringComparison.Ordinal))
            {
                ReplicationClientBuildingTerminalRevisionV2[delta.UniqueId] = revision;
                RemoveReplicationClientBuildingPresentationV2(delta.UniqueId);
            }
            else
            {
                ReplicationClientBuildingTerminalRevisionV2.Remove(delta.UniqueId);
                SetReplicationClientBuildingPresentationAnchorV2(
                    delta.UniqueId,
                    revision,
                    remainingTimeMs,
                    progressing);
            }

            replicationBuildingRepairDeltasAppliedV2++;
            detail = "ok building-repair-v2 " + detail;
            return true;
        }

        private static bool TryResolveReplicationBuildingRepairTargetV2(
            ReplicationWorldObjectDelta delta,
            string state,
            out object? candidate,
            out object? buildingInstance,
            out string detail)
        {
            candidate = null;
            buildingInstance = null;
            if (TryFindReplicationBuildingBlueprintCandidate(delta, out candidate, out var lookupDetail)
                && candidate != null
                && TryResolveReplicationBuildingInstanceV2(
                    candidate,
                    out buildingInstance,
                    out var instanceDetail)
                && buildingInstance != null)
            {
                RegisterReplicationHostIdentity(
                    delta.UniqueId,
                    candidate,
                    "building-repair-v2-targeted-map");
                detail = lookupDetail + " " + instanceDetail;
                return true;
            }

            if (string.Equals(state, "removed", StringComparison.Ordinal))
            {
                detail = "target-already-removed " + lookupDetail;
                return false;
            }

            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "buildReplay", out var buildReplay)
                || !string.Equals(buildReplay, "exact", StringComparison.Ordinal)
                || !TryReadReplicationWorldObjectDetailToken(
                    delta.Detail,
                    "buildRecordBlueprintB64",
                    out var blueprintToken)
                || !TryDecodeReplicationDetailBase64(blueprintToken, out var blueprintId)
                || !string.Equals(blueprintId, delta.BlueprintId, StringComparison.Ordinal)
                || !TryReadReplicationWorldObjectDetailToken(
                    delta.Detail,
                    "buildRecordB64",
                    out var recordToken)
                || !TryDecodeReplicationDetailBase64(recordToken, out var recordValue)
                || !TryParseReplicationBuildPlacementRecord(recordValue, out var record, out _)
                || record == null
                || record.X != delta.GridX
                || record.Y != delta.GridY
                || record.Z != delta.GridZ)
            {
                detail = "exact-replay-unavailable " + lookupDetail;
                return false;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                if (!TryApplyReplicationBuildBatchAuthoritative(
                        delta.BlueprintId,
                        "Player",
                        new List<ReplicationBuildPlacementRecord> { record },
                        out var seedDetail)
                    || !TryGetLastReplicationAuthoritativeBuildResult(
                        delta.BlueprintId,
                        record,
                        0,
                        out var committed)
                    || committed == null)
                {
                    detail = "exact-seed-failed " + seedDetail;
                    return false;
                }

                candidate = committed.View;
                buildingInstance = committed.BuildingInstance;
                RegisterReplicationHostIdentity(
                    delta.UniqueId,
                    candidate,
                    "building-repair-v2-exact-seed");
                detail = "exact-seed " + seedDetail;
                return true;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private void UpdateReplicationBuildingLifecycleV2()
        {
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived)
            {
                return;
            }

            UpdateReplicationBuildingConstructionMaterialsV2();

            if (!replicationConfigHostMode)
            {
                EnsureReplicationBuildingIdentityBootstrapV2();
                ProcessReplicationClientBuildingPresentationV2();
                SendClientSelectedBuildingProgressRequestV2IfDue();
            }
        }

        private static void SetReplicationClientBuildingPresentationAnchorV2(
            long hostId,
            long revision,
            int remainingTimeMs,
            bool progressing)
        {
            if (!progressing || remainingTimeMs <= 0)
            {
                RemoveReplicationClientBuildingPresentationV2(hostId);
                return;
            }

            if (!TryResolveReplicationBuildingTargetV2(
                    hostId,
                    out var candidate,
                    out var buildingInstance,
                    out _)
                || candidate == null
                || buildingInstance == null
                || !TryResolveReplicationBuildingProgressPresentationV2(
                    candidate,
                    buildingInstance,
                    out var buildProgress,
                    out var updateProgressMethod))
            {
                RemoveReplicationClientBuildingPresentationV2(hostId);
                return;
            }

            if (!ReplicationClientBuildingPresentationByHostIdV2.TryGetValue(hostId, out var presentation))
            {
                presentation = new ReplicationClientBuildingPresentationV2(hostId);
                ReplicationClientBuildingPresentationByHostIdV2.Add(hostId, presentation);
                ReplicationClientBuildingPresentationOrderV2.Add(hostId);
            }

            presentation.BuildProgress.Target = buildProgress;
            presentation.UpdateProgressMethod = updateProgressMethod;
            presentation.Revision = revision;
            presentation.AnchorRemainingTimeMs = remainingTimeMs;
            presentation.AnchorUnityTime = ReadReplicationBuildingPresentationUnityTimeV2();
            presentation.NextApplyRealtime = Time.realtimeSinceStartup;
        }

        private static void CorrectReplicationClientBuildingPresentationV2(
            long hostId,
            long revision,
            int remainingTimeMs)
        {
            if (!ReplicationClientBuildingPresentationByHostIdV2.TryGetValue(hostId, out var presentation)
                || presentation.Revision != revision)
            {
                return;
            }

            presentation.AnchorRemainingTimeMs = remainingTimeMs;
            presentation.AnchorUnityTime = ReadReplicationBuildingPresentationUnityTimeV2();
            presentation.NextApplyRealtime = Time.realtimeSinceStartup + ReplicationBuildingPresentationStepSecondsV2;
        }

        private static void ProcessReplicationClientBuildingPresentationV2()
        {
            var count = ReplicationClientBuildingPresentationOrderV2.Count;
            if (count == 0)
            {
                replicationClientBuildingPresentationCursorV2 = 0;
                return;
            }

            var unityTime = ReadReplicationBuildingPresentationUnityTimeV2();
            var inspected = 0;
            var applied = 0;
            while (inspected < count
                && inspected < ReplicationBuildingPresentationApplyBudgetV2
                && applied < ReplicationBuildingPresentationApplyBudgetV2
                && ReplicationClientBuildingPresentationOrderV2.Count > 0)
            {
                if (replicationClientBuildingPresentationCursorV2 >= ReplicationClientBuildingPresentationOrderV2.Count)
                {
                    replicationClientBuildingPresentationCursorV2 = 0;
                }

                var hostId = ReplicationClientBuildingPresentationOrderV2[replicationClientBuildingPresentationCursorV2];
                replicationClientBuildingPresentationCursorV2++;
                inspected++;
                if (!ReplicationClientBuildingPresentationByHostIdV2.TryGetValue(hostId, out var presentation))
                {
                    RemoveReplicationClientBuildingPresentationV2(hostId);
                    continue;
                }

                if (Time.realtimeSinceStartup < presentation.NextApplyRealtime)
                {
                    continue;
                }

                var buildProgress = presentation.BuildProgress.Target;
                if (buildProgress == null
                    || presentation.UpdateProgressMethod == null
                    || ReplicationClientBuildingTerminalRevisionV2.ContainsKey(hostId))
                {
                    RemoveReplicationClientBuildingPresentationV2(hostId);
                    continue;
                }

                presentation.NextApplyRealtime = Time.realtimeSinceStartup + ReplicationBuildingPresentationStepSecondsV2;
                var elapsedGameSeconds = Math.Max(0f, unityTime - presentation.AnchorUnityTime);
                // Never author completion on the client. The shadow clock drives only
                // BuildProgress.UpdateProgress on the view; the host's terminal
                // lifecycle alone mutates the building or invokes completion.
                var shadowRemainingTimeMs = Math.Max(
                    1,
                    presentation.AnchorRemainingTimeMs
                        - Mathf.RoundToInt(elapsedGameSeconds * 1000f));
                try
                {
                    presentation.UpdateProgressMethod.Invoke(
                        buildProgress,
                        new object[] { shadowRemainingTimeMs / 1000f });
                    applied++;
                    replicationClientBuildingPresentationAppliesV2++;
                }
                catch
                {
                    RemoveReplicationClientBuildingPresentationV2(hostId);
                }
            }
        }

        private static bool TryResolveReplicationBuildingProgressPresentationV2(
            object candidate,
            object buildingInstance,
            out object? buildProgress,
            out MethodInfo? updateProgressMethod)
        {
            buildProgress = null;
            updateProgressMethod = null;
            var viewType = AccessTools.TypeByName(
                "NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            if (viewType == null)
            {
                return false;
            }

            object? view = viewType.IsInstanceOfType(candidate) ? candidate : null;
            if (view == null && candidate is Component candidateComponent)
            {
                view = candidateComponent.gameObject.GetComponent(viewType);
            }

            if (view == null && buildingInstance is Component buildingComponent)
            {
                view = buildingComponent.gameObject.GetComponent(viewType);
            }

            if (view == null
                && TryResolveReplicationBuildingsManagerMain(out var manager, out _)
                && manager != null
                && TryReadInstanceMemberValue(
                    buildingInstance,
                    "BuildingType",
                    out var buildingType)
                && buildingType != null
                && TryReadInstanceMemberValue(
                    manager,
                    "TypeInstanceView",
                    out var typeInstanceViewValue)
                && typeInstanceViewValue is IDictionary typeInstanceView
                && typeInstanceView.Contains(buildingType)
                && typeInstanceView[buildingType] is IDictionary instanceView
                && instanceView.Contains(buildingInstance))
            {
                // Save-loaded bootstrap identities point at BaseBuildingInstance,
                // which is a plain model rather than a Component. Resolve its view
                // through the manager's native O(1) instance index—never a scene scan.
                view = instanceView[buildingInstance];
            }

            if (view == null
                || !TryReadInstanceMemberValue(view, "BuildProgress", out buildProgress)
                || buildProgress == null)
            {
                return false;
            }

            updateProgressMethod = FindReplicationInstanceMethod(
                buildProgress.GetType(),
                "UpdateProgress",
                new[] { typeof(float) });
            return updateProgressMethod != null;
        }

        private static float ReadReplicationBuildingPresentationUnityTimeV2()
        {
            if (TryGetReplicationWorldTimeManager(out var manager, out _)
                && manager != null
                && (TryReadReplicationFloatMember(manager, "UnityTime", "unityTime", out var unityTime)
                    || TryReadReplicationStaticFloatMember(manager.GetType(), "UnityTime", "unityTime", out unityTime)))
            {
                return unityTime;
            }

            return Time.time;
        }

        private static void RemoveReplicationClientBuildingPresentationV2(long hostId)
        {
            ReplicationClientBuildingPresentationByHostIdV2.Remove(hostId);
            var index = ReplicationClientBuildingPresentationOrderV2.IndexOf(hostId);
            if (index < 0)
            {
                return;
            }

            ReplicationClientBuildingPresentationOrderV2.RemoveAt(index);
            if (index < replicationClientBuildingPresentationCursorV2)
            {
                replicationClientBuildingPresentationCursorV2--;
            }

            if (replicationClientBuildingPresentationCursorV2 < 0
                || replicationClientBuildingPresentationCursorV2 >= ReplicationClientBuildingPresentationOrderV2.Count)
            {
                replicationClientBuildingPresentationCursorV2 = 0;
            }
        }

        private static void ResetReplicationBuildingLifecycleV2()
        {
            ResetReplicationBuildingConstructionMaterialsV2();
            ReplicationTrackedHostBuildingsV2.Clear();
            ReplicationClientBuildingProgressingV2.Clear();
            ReplicationClientBuildingTerminalRevisionV2.Clear();
            ReplicationClientBuildingPresentationByHostIdV2.Clear();
            ReplicationClientBuildingPresentationOrderV2.Clear();
            ReplicationBuildingRepairLastSentRealtimeV2.Clear();
            ReplicationBuildingRecoveryEscalatedSourceSequencesV2.Clear();
            replicationClientBuildingRevisionLedgerV2 = new BuildingRevisionLedger(65536);
            replicationBuildingLifecycleDeltasSentV2 = 0L;
            replicationBuildingLifecycleDeltasAppliedV2 = 0L;
            replicationBuildingLifecycleNativeEventsV2 = 0L;
            replicationBuildingRepairDeltasSentV2 = 0L;
            replicationBuildingRepairDeltasAppliedV2 = 0L;
            replicationClientSelectedBuildingHostIdV2 = 0L;
            replicationBuildingIdentityBootstrapEpochV2 = -1;
            replicationBuildingIdentityBootstrapCountV2 = 0;
            replicationNextBuildingIdentityBootstrapRealtimeV2 = 0f;
            replicationNextBuildingProgressRequestRealtimeV2 = 0f;
            replicationClientBuildingPresentationCursorV2 = 0;
            replicationClientBuildingPresentationAppliesV2 = 0L;
            replicationBuildingLifecycleProgressStartFallbacksV2 = 0L;
            replicationBuildingLifecycleLastCaptureGateReasonV2 = string.Empty;
            replicationBuildingTerminalRootTargetV2 = null;
            replicationBuildingTerminalRootReasonV2 = null;
            replicationBuildingMutationDepthByTargetV2?.Clear();
            replicationBuildingMutationDepthByTargetV2 = null;
        }

        private static string FormatReplicationBuildingLifecycleV2Status()
        {
            return "buildingV2="
                + replicationConfigBuildingReplicationV2
                + " tracked="
                + ReplicationTrackedHostBuildingsV2.Count.ToString(CultureInfo.InvariantCulture)
                + " lifecycleEvents="
                + replicationBuildingLifecycleNativeEventsV2.ToString(CultureInfo.InvariantCulture)
                + " lifecycleSent="
                + replicationBuildingLifecycleDeltasSentV2.ToString(CultureInfo.InvariantCulture)
                + " lifecycleApplied="
                + replicationBuildingLifecycleDeltasAppliedV2.ToString(CultureInfo.InvariantCulture)
                + " repairSent="
                + replicationBuildingRepairDeltasSentV2.ToString(CultureInfo.InvariantCulture)
                + " repairApplied="
                + replicationBuildingRepairDeltasAppliedV2.ToString(CultureInfo.InvariantCulture)
                + " bootstrap="
                + replicationBuildingIdentityBootstrapCountV2.ToString(CultureInfo.InvariantCulture)
                + " lifecyclePolls=0 lifecyclePollMs=0"
                + " progressStartFallbacks="
                + replicationBuildingLifecycleProgressStartFallbacksV2.ToString(CultureInfo.InvariantCulture)
                + " presentationActive="
                + ReplicationClientBuildingPresentationByHostIdV2.Count.ToString(CultureInfo.InvariantCulture)
                + " presentationApplies="
                + replicationClientBuildingPresentationAppliesV2.ToString(CultureInfo.InvariantCulture)
                + FormatReplicationBuildingConstructionMaterialsV2Status()
                + " selectedHostId="
                + replicationClientSelectedBuildingHostIdV2.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class ReplicationBuildingMutationCaptureV2
        {
            public ReplicationBuildingMutationCaptureV2(
                ReplicationTrackedBuildingV2 tracked,
                object targetKey,
                bool isOutermost)
            {
                Tracked = tracked;
                TargetKey = targetKey;
                IsOutermost = isOutermost;
            }

            public ReplicationTrackedBuildingV2 Tracked { get; }

            public object TargetKey { get; }

            public bool IsOutermost { get; }

            public bool Released { get; set; }
        }

        private sealed class ReplicationClientBuildingPresentationV2
        {
            public ReplicationClientBuildingPresentationV2(long hostId)
            {
                HostId = hostId;
                BuildProgress = new WeakReference(null);
            }

            public long HostId { get; }

            public WeakReference BuildProgress { get; }

            public MethodInfo? UpdateProgressMethod { get; set; }

            public long Revision { get; set; }

            public int AnchorRemainingTimeMs { get; set; }

            public float AnchorUnityTime { get; set; }

            public float NextApplyRealtime { get; set; }
        }

        private sealed class ReplicationTrackedBuildingV2
        {
            public ReplicationTrackedBuildingV2(
                long hostId,
                string blueprintId,
                int gridX,
                int gridY,
                int gridZ,
                object? view,
                object buildingInstance,
                ReplicationBuildPlacementRecord? canonicalRecord)
            {
                HostId = hostId;
                BlueprintId = blueprintId;
                GridX = gridX;
                GridY = gridY;
                GridZ = gridZ;
                View = new WeakReference(view);
                BuildingInstance = new WeakReference(buildingInstance);
                CanonicalRecord = canonicalRecord;
            }

            public long HostId { get; }

            public string BlueprintId { get; }

            public int GridX { get; }

            public int GridY { get; }

            public int GridZ { get; }

            public WeakReference View { get; }

            public WeakReference BuildingInstance { get; }

            public ReplicationBuildPlacementRecord? CanonicalRecord { get; set; }

            public long Revision { get; set; }

            public bool Progressing { get; set; }

            public bool TerminalSent { get; set; }

            public bool HasLastSentState { get; set; }

            public ReplicationBuildingAbsoluteStateV2 LastSentState { get; set; }
        }

        private readonly struct ReplicationBuildingAbsoluteStateV2 : IEquatable<ReplicationBuildingAbsoluteStateV2>
        {
            public static ReplicationBuildingAbsoluteStateV2 Removed =>
                new ReplicationBuildingAbsoluteStateV2(
                    false,
                    string.Empty,
                    0,
                    false,
                    false,
                    false,
                    false);

            public ReplicationBuildingAbsoluteStateV2(
                bool exists,
                string phase,
                int remainingTimeMs,
                bool underConstruction,
                bool progressing,
                bool forbidden,
                bool markedForDestruction)
            {
                Exists = exists;
                Phase = phase ?? string.Empty;
                RemainingTimeMs = remainingTimeMs;
                UnderConstruction = underConstruction;
                Progressing = progressing;
                Forbidden = forbidden;
                MarkedForDestruction = markedForDestruction;
            }

            public bool Exists { get; }

            public string Phase { get; }

            public int RemainingTimeMs { get; }

            public bool UnderConstruction { get; }

            public bool Progressing { get; }

            public bool Forbidden { get; }

            public bool MarkedForDestruction { get; }

            public bool Equals(ReplicationBuildingAbsoluteStateV2 other)
            {
                // Remaining time is sampled only for a selected building. It must
                // never turn active construction into a periodic all-building stream.
                return Exists == other.Exists
                    && string.Equals(Phase, other.Phase, StringComparison.OrdinalIgnoreCase)
                    && UnderConstruction == other.UnderConstruction
                    && Progressing == other.Progressing
                    && Forbidden == other.Forbidden
                    && MarkedForDestruction == other.MarkedForDestruction;
            }

            public override bool Equals(object? obj)
            {
                return obj is ReplicationBuildingAbsoluteStateV2 other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = Exists ? 1 : 0;
                    hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Phase);
                    hash = (hash * 397) ^ (UnderConstruction ? 1 : 0);
                    hash = (hash * 397) ^ (Progressing ? 1 : 0);
                    hash = (hash * 397) ^ (Forbidden ? 1 : 0);
                    hash = (hash * 397) ^ (MarkedForDestruction ? 1 : 0);
                    return hash;
                }
            }
        }
    }
}
