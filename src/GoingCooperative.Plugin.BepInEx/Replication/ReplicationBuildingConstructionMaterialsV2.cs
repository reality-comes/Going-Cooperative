using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationBuildingConstructionMaterialsKindV2 = "building-construction-v2";
        private const float ReplicationBuildingConstructionMaterialsRecoverySecondsV2 = 20f;
        private const int ReplicationBuildingConstructionMaterialsDirtyBudgetV2 = 8;
        private const int ReplicationBuildingConstructionMaterialsRecoveryBudgetV2 = 8;

        private static readonly Queue<long> ReplicationBuildingConstructionMaterialsDirtyQueueV2 =
            new Queue<long>();
        private static readonly HashSet<long> ReplicationBuildingConstructionMaterialsDirtySetV2 =
            new HashSet<long>();
        private static readonly HashSet<long> ReplicationBuildingConstructionMaterialsForcedSetV2 =
            new HashSet<long>();
        private static readonly HashSet<long> ReplicationBuildingConstructionMaterialsKnownSetV2 =
            new HashSet<long>();
        private static readonly List<long> ReplicationBuildingConstructionMaterialsKnownOrderV2 =
            new List<long>();

        private static float replicationBuildingConstructionMaterialsNextRecoveryRealtimeV2;
        private static int replicationBuildingConstructionMaterialsRecoveryCursorV2;
        private static int replicationBuildingConstructionMaterialsRecoveryRemainingV2;
        private static long replicationBuildingConstructionMaterialsStatesSentV2;
        private static long replicationBuildingConstructionMaterialsStatesAppliedV2;

        private void TryInstallReplicationBuildingConstructionMaterialsV2Hooks(Harmony harmony)
        {
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigBuildingConstructionMaterialsV2)
            {
                return;
            }

            var buildingType = AccessTools.TypeByName(
                "NSMedieval.BuildingComponents.BaseBuildingInstance");
            if (buildingType == null)
            {
                LogReplicationWarning(
                    "Going Cooperative building construction materials v2 native type missing");
                return;
            }

            var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildingConstructionMaterialsMutationV2Postfix),
                BindingFlags.NonPublic | BindingFlags.Static));
            var names = new HashSet<string>(new[]
            {
                "OnResourceAdded",
                "DropConstructionResources",
                "ConstructionCompleted",
                "BuildingCanceled",
                "BuildingDeconstructed"
            }, StringComparer.Ordinal);
            var patched = 0;
            var methods = buildingType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!names.Contains(methods[i].Name) || methods[i].ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    harmony.Patch(methods[i], postfix: postfix);
                    patched++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning(
                        "Going Cooperative building construction materials v2 hook failed method="
                        + methods[i].Name
                        + " error="
                        + FormatReflectionExceptionDetail(ex));
                }
            }

            LogReplicationInfo(
                "Going Cooperative building construction materials v2 hooks patched="
                + patched.ToString(CultureInfo.InvariantCulture));
        }

        private static void ReplicationBuildingConstructionMaterialsMutationV2Postfix(
            MethodBase __originalMethod,
            object __instance)
        {
            if (!replicationConfigBuildingConstructionMaterialsV2
                || !ShouldCaptureReplicationBuildingLifecycleV2()
                || __instance == null
                || !TryEnsureReplicationHostBuildingTrackerV2(
                    __instance,
                    out var tracked,
                    out _)
                || tracked == null)
            {
                return;
            }

            if (string.Equals(__originalMethod.Name, "ConstructionCompleted", StringComparison.Ordinal)
                || string.Equals(__originalMethod.Name, "BuildingCanceled", StringComparison.Ordinal)
                || string.Equals(__originalMethod.Name, "BuildingDeconstructed", StringComparison.Ordinal))
            {
                RetireReplicationBuildingConstructionMaterialsV2(tracked.HostId);
                return;
            }

            MarkReplicationBuildingConstructionMaterialsDirtyV2(
                tracked.HostId,
                forceCheckpoint: false);
        }

        private static void MarkReplicationBuildingConstructionMaterialsDirtyV2(
            long hostId,
            bool forceCheckpoint)
        {
            if (hostId <= 0L || !replicationConfigHostMode)
            {
                return;
            }

            if (ReplicationBuildingConstructionMaterialsKnownSetV2.Add(hostId))
            {
                ReplicationBuildingConstructionMaterialsKnownOrderV2.Add(hostId);
            }

            if (forceCheckpoint)
            {
                ReplicationBuildingConstructionMaterialsForcedSetV2.Add(hostId);
            }

            if (ReplicationBuildingConstructionMaterialsDirtySetV2.Add(hostId))
            {
                ReplicationBuildingConstructionMaterialsDirtyQueueV2.Enqueue(hostId);
            }
        }

        private static void UpdateReplicationBuildingConstructionMaterialsV2()
        {
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigBuildingConstructionMaterialsV2
                || !replicationConfigEnabled
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || !replicationConfigHostMode
                || replicationTransport == null)
            {
                return;
            }

            ScheduleReplicationBuildingConstructionMaterialsRecoveryV2();
            DrainReplicationBuildingConstructionMaterialsDirtyV2();
        }

        private static void ScheduleReplicationBuildingConstructionMaterialsRecoveryV2()
        {
            var now = Time.realtimeSinceStartup;
            if (replicationBuildingConstructionMaterialsNextRecoveryRealtimeV2 <= 0f)
            {
                replicationBuildingConstructionMaterialsNextRecoveryRealtimeV2 =
                    now + ReplicationBuildingConstructionMaterialsRecoverySecondsV2;
            }
            else if (now >= replicationBuildingConstructionMaterialsNextRecoveryRealtimeV2)
            {
                if (ReplicationBuildingConstructionMaterialsKnownOrderV2.Count > 64
                    && ReplicationBuildingConstructionMaterialsKnownOrderV2.Count
                        > ReplicationBuildingConstructionMaterialsKnownSetV2.Count * 2)
                {
                    ReplicationBuildingConstructionMaterialsKnownOrderV2.RemoveAll(
                        hostId => !ReplicationBuildingConstructionMaterialsKnownSetV2.Contains(hostId));
                    replicationBuildingConstructionMaterialsRecoveryCursorV2 = 0;
                }

                replicationBuildingConstructionMaterialsNextRecoveryRealtimeV2 =
                    now + ReplicationBuildingConstructionMaterialsRecoverySecondsV2;
                replicationBuildingConstructionMaterialsRecoveryRemainingV2 =
                    ReplicationBuildingConstructionMaterialsKnownOrderV2.Count;
            }

            var scheduled = 0;
            while (scheduled < ReplicationBuildingConstructionMaterialsRecoveryBudgetV2
                && replicationBuildingConstructionMaterialsRecoveryRemainingV2 > 0
                && ReplicationBuildingConstructionMaterialsKnownOrderV2.Count > 0)
            {
                if (replicationBuildingConstructionMaterialsRecoveryCursorV2
                    >= ReplicationBuildingConstructionMaterialsKnownOrderV2.Count)
                {
                    replicationBuildingConstructionMaterialsRecoveryCursorV2 = 0;
                }

                var hostId = ReplicationBuildingConstructionMaterialsKnownOrderV2[
                    replicationBuildingConstructionMaterialsRecoveryCursorV2++];
                replicationBuildingConstructionMaterialsRecoveryRemainingV2--;
                scheduled++;
                if (ReplicationBuildingConstructionMaterialsKnownSetV2.Contains(hostId)
                    && ReplicationTrackedHostBuildingsV2.ContainsKey(hostId))
                {
                    MarkReplicationBuildingConstructionMaterialsDirtyV2(
                        hostId,
                        forceCheckpoint: true);
                }
            }
        }

        private static void DrainReplicationBuildingConstructionMaterialsDirtyV2()
        {
            var changed = new List<ReplicationResourceContainerState>();
            var processed = 0;
            while (processed < ReplicationBuildingConstructionMaterialsDirtyBudgetV2
                && ReplicationBuildingConstructionMaterialsDirtyQueueV2.Count > 0)
            {
                var hostId = ReplicationBuildingConstructionMaterialsDirtyQueueV2.Dequeue();
                ReplicationBuildingConstructionMaterialsDirtySetV2.Remove(hostId);
                var forced = ReplicationBuildingConstructionMaterialsForcedSetV2.Remove(hostId);
                processed++;
                CollectReplicationBuildingConstructionMaterialsStateV2(
                    hostId,
                    forced,
                    changed);
            }

            if (changed.Count == 0 || replicationTransport == null)
            {
                return;
            }

            for (var offset = 0;
                offset < changed.Count;
                offset += ReplicationResourceContainerBatchMaxContainers)
            {
                var count = Math.Min(
                    ReplicationResourceContainerBatchMaxContainers,
                    changed.Count - offset);
                var chunk = new List<ReplicationResourceContainerState>(count);
                for (var i = 0; i < count; i++)
                {
                    chunk.Add(changed[offset + i]);
                }

                var batch = new ReplicationResourceContainerBatch(
                    ++replicationResourceContainerBatchSequence,
                    Time.realtimeSinceStartup,
                    false,
                    chunk);
                replicationTransport.Send(
                    ReplicationPayloadCodec.ForResourceContainerBatch(
                        ReplicationHostPeerId,
                        batch));
                replicationResourceContainerBatchesSent++;
            }

            replicationBuildingConstructionMaterialsStatesSentV2 += changed.Count;
        }

        private static void CollectReplicationBuildingConstructionMaterialsStateV2(
            long hostId,
            bool forceCheckpoint,
            List<ReplicationResourceContainerState> changed)
        {
            if (!ReplicationTrackedHostBuildingsV2.TryGetValue(hostId, out var tracked)
                || tracked.BuildingInstance.Target == null)
            {
                return;
            }

            var buildingInstance = tracked.BuildingInstance.Target;
            var entries = new List<ReplicationResourceContainerEntry>();
            if (!TryReadInstanceMemberValue(buildingInstance, "Storage", out var storage)
                || storage == null
                || !TryReadReplicationStorageEntries(storage, out entries, out _))
            {
                return;
            }

            var epoch = GetReplicationBuildBatchEpoch();
            var id = ReplicationBuildingConstructionMaterialsKindV2
                + ":"
                + epoch.ToString(CultureInfo.InvariantCulture)
                + ":"
                + hostId.ToString(CultureInfo.InvariantCulture);
            var collected = new ReplicationResourceContainerState(
                id,
                ReplicationBuildingConstructionMaterialsKindV2,
                epoch.ToString(CultureInfo.InvariantCulture)
                    + ":"
                    + hostId.ToString(CultureInfo.InvariantCulture),
                0L,
                tracked.GridX,
                tracked.GridY,
                tracked.GridZ,
                entries);
            var signature = ComputeReplicationResourceContainerSignature(collected);
            var revision = 1L;
            if (ReplicationHostResourceContainers.TryGetValue(id, out var previous))
            {
                if (previous.Signature == signature && !forceCheckpoint)
                {
                    return;
                }

                revision = previous.Revision + 1L;
            }

            var state = CopyReplicationResourceContainerWithRevision(collected, revision);
            ReplicationHostResourceContainers[id] =
                new ReplicationHostResourceContainerState(state, signature, revision);
            changed.Add(state);
        }

        private static void RetireReplicationBuildingConstructionMaterialsV2(long hostId)
        {
            if (hostId <= 0L)
            {
                return;
            }

            ReplicationBuildingConstructionMaterialsKnownSetV2.Remove(hostId);
            ReplicationBuildingConstructionMaterialsDirtySetV2.Remove(hostId);
            ReplicationBuildingConstructionMaterialsForcedSetV2.Remove(hostId);
            var id = ReplicationBuildingConstructionMaterialsKindV2
                + ":"
                + GetReplicationBuildBatchEpoch().ToString(CultureInfo.InvariantCulture)
                + ":"
                + hostId.ToString(CultureInfo.InvariantCulture);
            ReplicationHostResourceContainers.Remove(id);
        }

        private static bool TryApplyReplicationBuildingConstructionMaterialsV2(
            ReplicationResourceContainerState state,
            out string detail)
        {
            if (!replicationConfigBuildingReplicationV2
                || !replicationConfigBuildingConstructionMaterialsV2)
            {
                detail = "building-construction-materials-v2-gated-off";
                return true;
            }

            var owner = state.OwnerId.Split(':');
            if (owner.Length != 2
                || !long.TryParse(
                    owner[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var epoch)
                || !long.TryParse(
                    owner[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var hostId)
                || epoch < 0L
                || hostId <= 0L)
            {
                detail = "building-construction-materials-v2-owner-invalid";
                return false;
            }

            if (epoch != GetReplicationBuildBatchEpoch())
            {
                detail = "building-construction-materials-v2-stale-epoch";
                return true;
            }

            if (!TryResolveReplicationBuildingTargetV2(
                    hostId,
                    out var candidate,
                    out var buildingInstance,
                    out var targetDetail)
                || candidate == null
                || buildingInstance == null)
            {
                detail = "building-construction-materials-v2-target-pending " + targetDetail;
                return false;
            }

            if (TryReadReplicationBuildingPhase(buildingInstance, out var phase)
                && (phase.IndexOf("Finished", StringComparison.OrdinalIgnoreCase) >= 0
                    || phase.IndexOf("Built", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                detail = "building-construction-materials-v2-terminal phase=" + phase;
                return true;
            }

            if (!TryReadInstanceMemberValue(buildingInstance, "Storage", out var storage)
                || storage == null
                || !TryReadReplicationStorageEntries(
                    storage,
                    out var localEntries,
                    out var localDetail))
            {
                detail = "building-construction-materials-v2-storage-unresolved";
                return false;
            }

            var desired = ToReplicationResourceAmountMap(state.Entries);
            var local = ToReplicationResourceAmountMap(localEntries);
            var changed = 0;
            foreach (var pair in local)
            {
                desired.TryGetValue(pair.Key, out var desiredAmount);
                if (pair.Value <= desiredAmount)
                {
                    continue;
                }

                if (!TryMutateReplicationStorage(
                        storage,
                        pair.Key,
                        pair.Value - desiredAmount,
                        add: false,
                        out var mutationDetail))
                {
                    detail = "building-construction-materials-v2-consume-failed "
                        + mutationDetail;
                    return false;
                }

                changed++;
            }

            foreach (var pair in desired)
            {
                local.TryGetValue(pair.Key, out var localAmount);
                if (pair.Value <= localAmount)
                {
                    continue;
                }

                if (!TryMutateReplicationStorage(
                        storage,
                        pair.Key,
                        pair.Value - localAmount,
                        add: true,
                        out var mutationDetail))
                {
                    detail = "building-construction-materials-v2-add-failed "
                        + mutationDetail;
                    return false;
                }

                changed++;
            }

            replicationBuildingConstructionMaterialsStatesAppliedV2++;
            detail = "ok building-construction-materials-v2 changed="
                + changed.ToString(CultureInfo.InvariantCulture)
                + " entries="
                + state.Entries.Count.ToString(CultureInfo.InvariantCulture)
                + " "
                + localDetail
                + " "
                + targetDetail;
            return true;
        }

        private static void ResetReplicationBuildingConstructionMaterialsV2()
        {
            ReplicationBuildingConstructionMaterialsDirtyQueueV2.Clear();
            ReplicationBuildingConstructionMaterialsDirtySetV2.Clear();
            ReplicationBuildingConstructionMaterialsForcedSetV2.Clear();
            ReplicationBuildingConstructionMaterialsKnownSetV2.Clear();
            ReplicationBuildingConstructionMaterialsKnownOrderV2.Clear();
            replicationBuildingConstructionMaterialsNextRecoveryRealtimeV2 = 0f;
            replicationBuildingConstructionMaterialsRecoveryCursorV2 = 0;
            replicationBuildingConstructionMaterialsRecoveryRemainingV2 = 0;
            replicationBuildingConstructionMaterialsStatesSentV2 = 0L;
            replicationBuildingConstructionMaterialsStatesAppliedV2 = 0L;
        }

        private static string FormatReplicationBuildingConstructionMaterialsV2Status()
        {
            return " constructionMaterialsV2="
                + replicationConfigBuildingConstructionMaterialsV2
                + " constructionMaterialsTracked="
                + ReplicationBuildingConstructionMaterialsKnownSetV2.Count.ToString(
                    CultureInfo.InvariantCulture)
                + " constructionMaterialsSent="
                + replicationBuildingConstructionMaterialsStatesSentV2.ToString(
                    CultureInfo.InvariantCulture)
                + " constructionMaterialsApplied="
                + replicationBuildingConstructionMaterialsStatesAppliedV2.ToString(
                    CultureInfo.InvariantCulture);
        }
    }
}
