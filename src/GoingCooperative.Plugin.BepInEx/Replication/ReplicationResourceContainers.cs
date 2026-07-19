using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Dictionary<string, ReplicationHostResourceContainerState> ReplicationHostResourceContainers =
            new Dictionary<string, ReplicationHostResourceContainerState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationClientResourceContainerRevisions =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Queue<PendingReplicationResourceContainerApply> ReplicationPendingResourceContainerApplies =
            new Queue<PendingReplicationResourceContainerApply>();
        private static UnityEngine.Object[] replicationResourceContainerCachedViews = Array.Empty<UnityEngine.Object>();
        private static float replicationResourceContainerNextViewRefreshRealtime;
        private static float replicationResourceContainerNextCollectRealtime;
        private static float replicationResourceContainerNextCheckpointRealtime;
        private static long replicationResourceContainerBatchSequence;
        private static long replicationResourceContainerBatchesSent;
        private static long replicationResourceContainerBatchesReceived;
        private static long replicationResourceContainersApplied;
        private static bool replicationResourceContainerHostBaselineEstablished;

        private const float ReplicationResourceContainerCollectSeconds = 0.5f;
        private const float ReplicationResourceContainerCheckpointSeconds = 20f;
        private const float ReplicationResourceContainerViewRefreshSeconds = 2f;
        private const int ReplicationResourceContainerBatchMaxContainers = 4;
        private const int ReplicationResourceContainerApplyMaxPerFrame = 3;
        private const int ReplicationResourceContainerApplyQueueMax = 512;
        private const int ReplicationResourceContainerApplyMaxAttempts = 120;

        private void SendHostReplicationResourceContainersIfDue()
        {
            if (!replicationConfigResourceContainerReplication
                || !replicationConfigEnabled
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || replicationLastCollectedEntities <= 0
                || Time.realtimeSinceStartup < replicationResourceContainerNextCollectRealtime)
            {
                return;
            }

            replicationResourceContainerNextCollectRealtime =
                Time.realtimeSinceStartup + ReplicationResourceContainerCollectSeconds;
            var checkpoint = replicationResourceContainerHostBaselineEstablished
                && Time.realtimeSinceStartup >= replicationResourceContainerNextCheckpointRealtime;
            if (checkpoint)
            {
                replicationResourceContainerNextCheckpointRealtime =
                    Time.realtimeSinceStartup + ReplicationResourceContainerCheckpointSeconds;
            }

            if (!TryCollectReplicationResourceContainers(out var current, out var collectDetail))
            {
                if (replicationConfigCarryDiagnostics)
                {
                    LogReplicationInfo("Going Cooperative replication resource containers skipped " + collectDetail);
                }

                return;
            }

            var changed = new List<ReplicationResourceContainerState>();
            var currentKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < current.Count; i++)
            {
                var collected = current[i];
                currentKeys.Add(collected.ContainerId);
                var signature = ComputeReplicationResourceContainerSignature(collected);
                var revision = 1L;
                if (ReplicationHostResourceContainers.TryGetValue(collected.ContainerId, out var previous))
                {
                    revision = previous.Signature == signature ? previous.Revision : previous.Revision + 1L;
                    if (previous.Signature == signature
                        && (!checkpoint || string.Equals(collected.ContainerKind, "pile", StringComparison.Ordinal)))
                    {
                        continue;
                    }
                }

                var state = CopyReplicationResourceContainerWithRevision(collected, revision);
                ReplicationHostResourceContainers[collected.ContainerId] =
                    new ReplicationHostResourceContainerState(state, signature, revision);
                if (!replicationResourceContainerHostBaselineEstablished
                    && string.Equals(state.ContainerKind, "pile", StringComparison.Ordinal))
                {
                    continue;
                }

                changed.Add(state);
                if (replicationConfigCarryDiagnostics
                    && state.ContainerKind.StartsWith("agent-", StringComparison.Ordinal))
                {
                    LogReplicationInfo("Going Cooperative replication resource container host-state id="
                        + state.ContainerId
                        + " revision="
                        + state.Revision.ToString(CultureInfo.InvariantCulture)
                        + " entries="
                        + FormatReplicationResourceContainerEntries(state.Entries));
                }
            }

            var removedPileKeys = new List<string>();
            foreach (var pair in ReplicationHostResourceContainers)
            {
                if (!pair.Value.State.ContainerKind.Equals("pile", StringComparison.Ordinal)
                    || currentKeys.Contains(pair.Key)
                    || pair.Value.State.Entries.Count == 0)
                {
                    continue;
                }

                var previous = pair.Value.State;
                var tombstone = new ReplicationResourceContainerState(
                    previous.ContainerId,
                    previous.ContainerKind,
                    previous.OwnerId,
                    pair.Value.Revision + 1L,
                    previous.GridX,
                    previous.GridY,
                    previous.GridZ,
                    Array.Empty<ReplicationResourceContainerEntry>());
                changed.Add(tombstone);
                removedPileKeys.Add(pair.Key);
            }

            for (var i = 0; i < removedPileKeys.Count; i++)
            {
                var key = removedPileKeys[i];
                var tombstone = changed.Find(state => string.Equals(state.ContainerId, key, StringComparison.Ordinal));
                if (tombstone != null)
                {
                    ReplicationHostResourceContainers[key] = new ReplicationHostResourceContainerState(
                        tombstone,
                        ComputeReplicationResourceContainerSignature(tombstone),
                        tombstone.Revision);
                }
            }

            if (!replicationResourceContainerHostBaselineEstablished)
            {
                replicationResourceContainerHostBaselineEstablished = true;
                replicationResourceContainerNextCheckpointRealtime =
                    Time.realtimeSinceStartup + ReplicationResourceContainerCheckpointSeconds;
            }

            if (changed.Count == 0)
            {
                return;
            }

            for (var offset = 0; offset < changed.Count; offset += ReplicationResourceContainerBatchMaxContainers)
            {
                var count = Math.Min(ReplicationResourceContainerBatchMaxContainers, changed.Count - offset);
                var chunk = new List<ReplicationResourceContainerState>(count);
                for (var i = 0; i < count; i++)
                {
                    chunk.Add(changed[offset + i]);
                }

                var batch = new ReplicationResourceContainerBatch(
                    ++replicationResourceContainerBatchSequence,
                    Time.realtimeSinceStartup,
                    checkpoint,
                    chunk);
                replicationTransport.Send(ReplicationPayloadCodec.ForResourceContainerBatch(ReplicationHostPeerId, batch));
                replicationResourceContainerBatchesSent++;
            }

            if (replicationConfigCarryDiagnostics || checkpoint)
            {
                LogReplicationInfo("Going Cooperative replication resource containers sent batches="
                    + ((changed.Count + ReplicationResourceContainerBatchMaxContainers - 1) / ReplicationResourceContainerBatchMaxContainers).ToString(CultureInfo.InvariantCulture)
                    + " containers="
                    + changed.Count.ToString(CultureInfo.InvariantCulture)
                    + " checkpoint="
                    + checkpoint
                    + " "
                    + collectDetail);
            }
        }

        private void HandleReplicationResourceContainerBatch(TransportEnvelope envelope)
        {
            if (replicationConfigHostMode || !replicationConfigResourceContainerReplication)
            {
                return;
            }

            if (!ReplicationPayloadCodec.TryReadResourceContainerBatch(envelope, out var batch, out var error)
                || batch == null)
            {
                LogReplicationWarning("Going Cooperative replication resource container decode failed error=" + error);
                return;
            }

            replicationResourceContainerBatchesReceived++;
            for (var i = 0; i < batch.Containers.Count; i++)
            {
                var state = batch.Containers[i];
                if (ReplicationClientResourceContainerRevisions.TryGetValue(state.ContainerId, out var appliedRevision)
                    && appliedRevision >= state.Revision)
                {
                    continue;
                }

                while (ReplicationPendingResourceContainerApplies.Count >= ReplicationResourceContainerApplyQueueMax)
                {
                    ReplicationPendingResourceContainerApplies.Dequeue();
                }

                ReplicationPendingResourceContainerApplies.Enqueue(new PendingReplicationResourceContainerApply(state));
            }
        }

        private static void ProcessPendingReplicationResourceContainerApplies()
        {
            if (!replicationConfigResourceContainerReplication || replicationConfigHostMode)
            {
                return;
            }

            var processed = 0;
            var initialCount = ReplicationPendingResourceContainerApplies.Count;
            while (processed < ReplicationResourceContainerApplyMaxPerFrame
                && processed < initialCount
                && ReplicationPendingResourceContainerApplies.Count > 0)
            {
                var pending = ReplicationPendingResourceContainerApplies.Dequeue();
                var state = pending.State;
                if (ReplicationClientResourceContainerRevisions.TryGetValue(state.ContainerId, out var appliedRevision)
                    && appliedRevision >= state.Revision)
                {
                    processed++;
                    continue;
                }

                applyingRuntimeCommandDepth++;
                bool applied;
                string detail;
                try
                {
                    applied = TryApplyReplicationResourceContainer(state, out detail);
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }

                if (applied)
                {
                    ReplicationClientResourceContainerRevisions[state.ContainerId] = state.Revision;
                    replicationResourceContainersApplied++;
                    if (replicationConfigCarryDiagnostics)
                    {
                        instance?.LogReplicationInfo("Going Cooperative replication resource container applied id="
                            + state.ContainerId
                            + " revision="
                            + state.Revision.ToString(CultureInfo.InvariantCulture)
                            + " "
                            + detail);
                    }
                }
                else if (++pending.Attempts < ReplicationResourceContainerApplyMaxAttempts)
                {
                    ReplicationPendingResourceContainerApplies.Enqueue(pending);
                }
                else
                {
                    instance?.LogReplicationWarning("Going Cooperative replication resource container abandoned id="
                        + state.ContainerId
                        + " revision="
                        + state.Revision.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + detail);
                }

                processed++;
            }
        }

        private static bool TryCollectReplicationResourceContainers(
            out List<ReplicationResourceContainerState> states,
            out string detail)
        {
            states = new List<ReplicationResourceContainerState>();
            var agentContainers = 0;
            var pileContainers = 0;
            var productionContainers = 0;
            var skipped = 0;

            if (Time.realtimeSinceStartup >= replicationResourceContainerNextViewRefreshRealtime
                || replicationResourceContainerCachedViews.Length == 0)
            {
                replicationResourceContainerCachedViews = FindReplicationAnimatedAgentViews();
                replicationResourceContainerNextViewRefreshRealtime =
                    Time.realtimeSinceStartup + ReplicationResourceContainerViewRefreshSeconds;
            }

            for (var i = 0; i < replicationResourceContainerCachedViews.Length; i++)
            {
                var view = replicationResourceContainerCachedViews[i];
                if (view == null
                    || !TryGetReplicationViewEntityId(view, out var entityId)
                    || !TryClassifyReplicationView(view, out var entityKind)
                    || !TryResolveReplicationAgentOwnerFromView(view, out var owner, out _)
                    || owner == null)
                {
                    skipped++;
                    continue;
                }

                object? behaviourOwner = null;
                TryResolveReplicationBehaviourOwner(owner, out behaviourOwner);
                CollectReplicationAgentStorageContainer(states, entityId, owner, behaviourOwner, "Storage", "agent-haul", ref agentContainers);
                if (string.Equals(entityKind, "worker", StringComparison.OrdinalIgnoreCase))
                {
                    CollectReplicationAgentStorageContainer(states, entityId, owner, behaviourOwner, "FoodStorage", "agent-food", ref agentContainers);
                    CollectReplicationAgentStorageContainer(states, entityId, owner, behaviourOwner, "MedicineStorage", "agent-medicine", ref agentContainers);
                }
            }

            if (TryCollectReplicationResourcePileStates(out var piles, out var pileDetail))
            {
                for (var i = 0; i < piles.Count; i++)
                {
                    var pile = piles[i];
                    var key = "pile:"
                        + pile.GridX.ToString(CultureInfo.InvariantCulture) + ":"
                        + pile.GridY.ToString(CultureInfo.InvariantCulture) + ":"
                        + pile.GridZ.ToString(CultureInfo.InvariantCulture) + ":"
                        + pile.BlueprintId;
                    states.Add(new ReplicationResourceContainerState(
                        key,
                        "pile",
                        pile.BlueprintId,
                        0L,
                        pile.GridX,
                        pile.GridY,
                        pile.GridZ,
                        new[] { new ReplicationResourceContainerEntry(pile.BlueprintId, pile.Amount) }));
                    pileContainers++;
                }
            }
            else
            {
                pileDetail = "pile-collect-failed " + pileDetail;
            }

            CollectReplicationProductionStorageContainers(states, ref productionContainers);

            states.Sort((left, right) => string.Compare(left.ContainerId, right.ContainerId, StringComparison.Ordinal));
            detail = "agents="
                + agentContainers.ToString(CultureInfo.InvariantCulture)
                + " piles="
                + pileContainers.ToString(CultureInfo.InvariantCulture)
                + " production="
                + productionContainers.ToString(CultureInfo.InvariantCulture)
                + " skippedViews="
                + skipped.ToString(CultureInfo.InvariantCulture)
                + " "
                + pileDetail;
            return states.Count > 0;
        }

        private static void CollectReplicationAgentStorageContainer(
            List<ReplicationResourceContainerState> states,
            string entityId,
            object owner,
            object? behaviourOwner,
            string memberName,
            string kind,
            ref int count)
        {
            object? storage = null;
            if ((!TryReadInstanceMemberValue(owner, memberName, out storage) || storage == null)
                && (behaviourOwner == null
                    || !TryReadInstanceMemberValue(behaviourOwner, memberName, out storage)
                    || storage == null))
            {
                return;
            }

            if (!TryReadReplicationStorageEntries(storage, out var entries, out _))
            {
                return;
            }

            states.Add(new ReplicationResourceContainerState(
                "agent:" + entityId + ":" + kind.Substring("agent-".Length),
                kind,
                entityId,
                0L,
                0,
                0,
                0,
                entries));
            count++;
        }

        private static void CollectReplicationProductionStorageContainers(
            List<ReplicationResourceContainerState> states,
            ref int count)
        {
            var viewType = HarmonyLib.AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            var componentType = HarmonyLib.AccessTools.TypeByName("NSMedieval.BuildingComponents.ProductionComponentInstance");
            if (viewType == null || componentType == null) return;

            var views = Resources.FindObjectsOfTypeAll(viewType);
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || !TryResolveReplicationBuildingCandidateInstance(view, out var building, out _) || building == null
                    || !TryGetReplicationProductionSystem(building, componentType, out var system) || system == null
                    || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _)
                    || !TryGetListMember(system, "productions", out var queue))
                {
                    continue;
                }

                for (var ticketIndex = 0; ticketIndex < queue.Count; ticketIndex++)
                {
                    var ticket = queue[ticketIndex];
                    if (ticket == null) continue;
                    CollectReplicationProductionStorageContainer(states, ticket, "Storage", "production-primary", x, y, z, ticketIndex, ref count);
                    CollectReplicationProductionStorageContainer(states, ticket, "SecondaryIngredientStorage", "production-secondary", x, y, z, ticketIndex, ref count);
                }
            }
        }

        private static void CollectReplicationProductionStorageContainer(
            List<ReplicationResourceContainerState> states,
            object ticket,
            string memberName,
            string kind,
            int x,
            int y,
            int z,
            int ticketIndex,
            ref int count)
        {
            if (!TryReadInstanceMemberValue(ticket, memberName, out var storage) || storage == null
                || !TryReadReplicationStorageEntries(storage, out var entries, out _))
            {
                return;
            }

            states.Add(new ReplicationResourceContainerState(
                "production:" + x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture) + ":" + z.ToString(CultureInfo.InvariantCulture)
                    + ":" + ticketIndex.ToString(CultureInfo.InvariantCulture) + ":" + kind,
                kind,
                ticketIndex.ToString(CultureInfo.InvariantCulture),
                0L,
                x, y, z,
                entries));
            count++;
        }

        private static bool TryReadReplicationStorageEntries(
            object storage,
            out List<ReplicationResourceContainerEntry> entries,
            out string detail)
        {
            entries = new List<ReplicationResourceContainerEntry>();
            object? resources = null;
            if ((!TryReadInstanceMemberValue(storage, "Resources", out resources) || resources == null)
                && (!TryReadInstanceMemberValue(storage, "currentResources", out resources) || resources == null)
                && (!TryInvokeReplicationObjectMethod(storage, "GetResourcesWithoutLock", out resources) || resources == null))
            {
                detail = "resources-missing storage=" + FormatShortTypeName(storage.GetType());
                return false;
            }

            if (!(resources is IEnumerable enumerable))
            {
                detail = "resources-not-enumerable storage=" + FormatShortTypeName(storage.GetType());
                return false;
            }

            var amounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var resourceInstance in enumerable)
            {
                if (resourceInstance == null
                    || !TryReadReplicationWorldObjectIntMember(resourceInstance, "Amount", "amount", out var amount)
                    || amount <= 0)
                {
                    continue;
                }

                var blueprintId = string.Empty;
                if ((!TryReadReplicationWorldObjectStringMember(resourceInstance, "BlueprintId", "blueprintId", out blueprintId)
                        || string.IsNullOrWhiteSpace(blueprintId))
                    && (!TryExtractReplicationResourceId(resourceInstance, out blueprintId)
                        || string.IsNullOrWhiteSpace(blueprintId)))
                {
                    object? blueprint = null;
                    if ((TryReadInstanceMemberValue(resourceInstance, "Blueprint", out blueprint) && blueprint != null)
                        || (TryReadInstanceMemberValue(resourceInstance, "blueprint", out blueprint) && blueprint != null))
                    {
                        TryExtractReplicationResourceId(blueprint, out blueprintId);
                    }
                }

                object? resource = null;
                if (string.IsNullOrWhiteSpace(blueprintId)
                    && ((!TryReadInstanceMemberValue(resourceInstance, "Resource", out resource) || resource == null)
                        && (!TryReadInstanceMemberValue(resourceInstance, "resource", out resource) || resource == null)))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    TryExtractReplicationResourceId(resource!, out blueprintId);
                }

                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    continue;
                }

                amounts.TryGetValue(blueprintId, out var existing);
                amounts[blueprintId] = existing + amount;
            }

            foreach (var pair in amounts)
            {
                entries.Add(new ReplicationResourceContainerEntry(pair.Key, pair.Value));
            }

            entries.Sort((left, right) => string.Compare(left.BlueprintId, right.BlueprintId, StringComparison.Ordinal));
            detail = "entries=" + entries.Count.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryApplyReplicationResourceContainer(
            ReplicationResourceContainerState state,
            out string detail)
        {
            if (string.Equals(state.ContainerKind, "pile", StringComparison.Ordinal))
            {
                return TryApplyReplicationPileContainer(state, out detail);
            }

            if (state.ContainerKind.StartsWith("agent-", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentStorageContainer(state, out detail);
            }

            if (state.ContainerKind.StartsWith("production-", StringComparison.Ordinal))
            {
                return TryApplyReplicationProductionStorageContainer(state, out detail);
            }

            detail = "unsupported-kind=" + state.ContainerKind;
            return false;
        }

        private static bool TryApplyReplicationPileContainer(
            ReplicationResourceContainerState state,
            out string detail)
        {
            var blueprintId = state.Entries.Count > 0 ? state.Entries[0].BlueprintId : state.OwnerId;
            if (state.Entries.Count == 0)
            {
                var disposeDelta = new ReplicationWorldObjectDelta(
                    0L,
                    Time.realtimeSinceStartup,
                    "ResourcePileDisposed",
                    0L,
                    blueprintId,
                    state.GridX,
                    state.GridY,
                    state.GridZ,
                    "source=resource-container");
                if (TryApplyReplicationResourcePileDisposed(disposeDelta, out detail))
                {
                    return true;
                }

                return detail.IndexOf("lookup-failed", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var amount = state.Entries[0].Amount;
            var syncDelta = new ReplicationWorldObjectDelta(
                0L,
                Time.realtimeSinceStartup,
                "ResourcePileAmountAdded",
                0L,
                blueprintId,
                state.GridX,
                state.GridY,
                state.GridZ,
                "amount=" + amount.ToString(CultureInfo.InvariantCulture)
                    + " pileAmount=" + amount.ToString(CultureInfo.InvariantCulture)
                    + " source=resource-container");
            return TryApplyReplicationResourcePileAmountAdded(syncDelta, out detail);
        }

        private static bool TryApplyReplicationAgentStorageContainer(
            ReplicationResourceContainerState state,
            out string detail)
        {
            var ownerDetail = "owner-not-read";
            if (!TryFindReplicationAgentOwnerByEntityId(state.OwnerId, out var owner, out ownerDetail)
                || owner == null)
            {
                detail = "agent-owner-unresolved " + ownerDetail;
                return false;
            }

            // Owner data is authoritative; a view is only needed to refresh a
            // visual.  Trader-party imports can receive container state in the
            // narrow window before their manager view is attached.
            var hasView = TryFindReplicationAnimatedAgentViewByEntityId(state.OwnerId, out var view, out var viewDetail)
                && view != null;

            var memberName = string.Equals(state.ContainerKind, "agent-food", StringComparison.Ordinal)
                ? "FoodStorage"
                : string.Equals(state.ContainerKind, "agent-medicine", StringComparison.Ordinal)
                    ? "MedicineStorage"
                    : "Storage";
            object? behaviourOwner = null;
            TryResolveReplicationBehaviourOwner(owner, out behaviourOwner);
            object? storage = null;
            if ((!TryReadInstanceMemberValue(owner, memberName, out storage) || storage == null)
                && (behaviourOwner == null
                    || !TryReadInstanceMemberValue(behaviourOwner, memberName, out storage)
                    || storage == null))
            {
                detail = "storage-unresolved member=" + memberName + " " + ownerDetail;
                return false;
            }

            if (!TryReadReplicationStorageEntries(storage, out var localEntries, out var localDetail))
            {
                detail = localDetail;
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

                if (!TryMutateReplicationStorage(storage, pair.Key, pair.Value - desiredAmount, add: false, out var mutationDetail))
                {
                    detail = "consume-failed blueprint=" + pair.Key + " " + mutationDetail;
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

                if (!TryMutateReplicationStorage(storage, pair.Key, pair.Value - localAmount, add: true, out var mutationDetail))
                {
                    detail = "add-failed blueprint=" + pair.Key + " " + mutationDetail;
                    return false;
                }

                changed++;
            }

            if (string.Equals(state.ContainerKind, "agent-haul", StringComparison.Ordinal))
            {
                if (state.Entries.Count > 0)
                {
                    ReplicationAgentCarryResourceBlueprintByEntityId[state.OwnerId] = state.Entries[0].BlueprintId;
                    ReplicationAgentCarryResourceAmountByEntityId[state.OwnerId] = state.Entries[0].Amount;
                    if (TryResolveReplicationResourceModel(state.Entries[0].BlueprintId, out var resource, out _)
                        && resource != null)
                    {
                        if (hasView)
                            TryNotifyReplicationAgentStorageChanged(view!, resource, state.Entries[0].Amount, out _);
                    }
                }
                else
                {
                    ReplicationAgentCarryResourceBlueprintByEntityId.Remove(state.OwnerId);
                    ReplicationAgentCarryResourceAmountByEntityId.Remove(state.OwnerId);
                    ClearReplicationAgentCarryResourceVisual(state.OwnerId);
                }
            }

            var refreshDetail = "refresh=unchanged";
            if (changed > 0 && hasView)
            {
                TryInvokeReplicationObjectVoidMethod(view!, "UpdateViewImmediate", out refreshDetail);
            }
            else if (changed > 0)
            {
                refreshDetail = "refresh=view-unavailable " + viewDetail;
            }
            detail = "ok kind="
                + state.ContainerKind
                + " changed="
                + changed.ToString(CultureInfo.InvariantCulture)
                + " entries="
                + state.Entries.Count.ToString(CultureInfo.InvariantCulture)
                + " "
                + refreshDetail
                + " "
                + ownerDetail
                + " "
                + localDetail;
            return true;
        }

        private static bool TryApplyReplicationProductionStorageContainer(ReplicationResourceContainerState state, out string detail)
        {
            var systemDetail = "ticket-index-invalid";
            if (!int.TryParse(state.OwnerId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticketIndex))
            {
                detail = "production-unresolved " + systemDetail;
                return false;
            }
            if (!TryFindProductionSystemAtGrid(state.GridX, state.GridY, state.GridZ, out var system, out systemDetail)
                || system == null
                || !TryGetListMember(system, "productions", out var queue)
                || ticketIndex < 0 || ticketIndex >= queue.Count
                || queue[ticketIndex] == null)
            {
                detail = "production-unresolved " + systemDetail;
                return false;
            }

            var ticket = queue[ticketIndex]!;
            var memberName = string.Equals(state.ContainerKind, "production-secondary", StringComparison.Ordinal)
                ? "SecondaryIngredientStorage"
                : "Storage";
            var localDetail = "storage-member-missing";
            if (!TryReadInstanceMemberValue(ticket, memberName, out var storage) || storage == null
                || !TryReadReplicationStorageEntries(storage, out var localEntries, out localDetail))
            {
                detail = "production-storage-unresolved member=" + memberName + " " + localDetail;
                return false;
            }

            var desired = ToReplicationResourceAmountMap(state.Entries);
            var local = ToReplicationResourceAmountMap(localEntries);
            var changed = 0;
            foreach (var pair in local)
            {
                desired.TryGetValue(pair.Key, out var desiredAmount);
                if (pair.Value > desiredAmount && TryMutateReplicationStorage(storage, pair.Key, pair.Value - desiredAmount, false, out _)) changed++;
            }
            foreach (var pair in desired)
            {
                local.TryGetValue(pair.Key, out var localAmount);
                if (pair.Value > localAmount && TryMutateReplicationStorage(storage, pair.Key, pair.Value - localAmount, true, out _)) changed++;
            }

            RefreshReplicationProductionTicketViews(system, ticket, state.GridX, state.GridY, state.GridZ, ticketIndex);
            detail = "ok production-storage kind=" + state.ContainerKind + " changed=" + changed.ToString(CultureInfo.InvariantCulture)
                + " entries=" + state.Entries.Count.ToString(CultureInfo.InvariantCulture) + " " + localDetail;
            return true;
        }

        private static void RefreshReplicationProductionTicketViews(object system, object ticket, int x, int y, int z, int ticketIndex)
        {
            var viewType = HarmonyLib.AccessTools.TypeByName("NSMedieval.UI.ProductionLayoutItemView");
            if (viewType == null) return;
            var views = Resources.FindObjectsOfTypeAll(viewType);
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || !TryReadInstanceMemberValue(view, "production", out var candidate) || !ReferenceEquals(candidate, ticket)) continue;
                TryInvokeReplicationObjectVoidMethod(view, "UpdateProductionData", out _);
            }
        }

        private static string FormatReplicationResourceContainerEntries(
            IReadOnlyList<ReplicationResourceContainerEntry> entries)
        {
            if (entries.Count == 0)
            {
                return "empty";
            }

            var parts = new string[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                parts[i] = entries[i].BlueprintId
                    + ":"
                    + entries[i].Amount.ToString(CultureInfo.InvariantCulture);
            }

            return string.Join(",", parts);
        }

        private static Dictionary<string, int> ToReplicationResourceAmountMap(
            IReadOnlyList<ReplicationResourceContainerEntry> entries)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++)
            {
                result[entries[i].BlueprintId] = entries[i].Amount;
            }

            return result;
        }

        private static bool TryMutateReplicationStorage(
            object storage,
            string blueprintId,
            int amount,
            bool add,
            out string detail)
        {
            if (amount <= 0)
            {
                detail = "no-change";
                return true;
            }

            if (!TryResolveReplicationResourceModel(blueprintId, out var resource, out var resourceDetail)
                || resource == null)
            {
                detail = resourceDetail;
                return false;
            }

            if (!add)
            {
                var consumeMethod = FindReplicationInstanceMethod(storage.GetType(), "Consume", new[] { resource.GetType(), typeof(int) });
                if (consumeMethod == null)
                {
                    detail = "consume-method-missing storage=" + FormatShortTypeName(storage.GetType());
                    return false;
                }

                try
                {
                    consumeMethod.Invoke(storage, new object[] { resource, amount });
                    detail = "consumed=" + amount.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex)
                {
                    detail = FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            if (!TryCreateReplicationResourceInstance(resource, amount, out var resourceInstance, out var instanceDetail)
                || resourceInstance == null)
            {
                detail = instanceDetail;
                return false;
            }

            var addMethod = FindReplicationStorageAddMethod(storage.GetType(), resourceInstance, out var args, out var signature);
            if (addMethod == null)
            {
                detail = "add-method-missing storage=" + FormatShortTypeName(storage.GetType());
                return false;
            }

            try
            {
                addMethod.Invoke(storage, args);
                detail = "added=" + amount.ToString(CultureInfo.InvariantCulture) + " via=" + signature;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static ReplicationResourceContainerState CopyReplicationResourceContainerWithRevision(
            ReplicationResourceContainerState state,
            long revision)
        {
            return new ReplicationResourceContainerState(
                state.ContainerId,
                state.ContainerKind,
                state.OwnerId,
                revision,
                state.GridX,
                state.GridY,
                state.GridZ,
                state.Entries);
        }

        private static ulong ComputeReplicationResourceContainerSignature(ReplicationResourceContainerState state)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                AddReplicationSignatureString(ref hash, state.ContainerId);
                AddReplicationSignatureString(ref hash, state.ContainerKind);
                AddReplicationSignatureString(ref hash, state.OwnerId);
                AddReplicationSignatureInt(ref hash, state.GridX);
                AddReplicationSignatureInt(ref hash, state.GridY);
                AddReplicationSignatureInt(ref hash, state.GridZ);
                AddReplicationSignatureInt(ref hash, state.Entries.Count);
                for (var i = 0; i < state.Entries.Count; i++)
                {
                    AddReplicationSignatureString(ref hash, state.Entries[i].BlueprintId);
                    AddReplicationSignatureInt(ref hash, state.Entries[i].Amount);
                }

                return hash;
            }
        }

        private static void ClearReplicationResourceContainerState()
        {
            ReplicationHostResourceContainers.Clear();
            ReplicationClientResourceContainerRevisions.Clear();
            ReplicationPendingResourceContainerApplies.Clear();
            replicationResourceContainerCachedViews = Array.Empty<UnityEngine.Object>();
            replicationResourceContainerNextViewRefreshRealtime = 0f;
            replicationResourceContainerNextCollectRealtime = 0f;
            replicationResourceContainerNextCheckpointRealtime = 0f;
            replicationResourceContainerBatchSequence = 0L;
            replicationResourceContainerBatchesSent = 0L;
            replicationResourceContainerBatchesReceived = 0L;
            replicationResourceContainersApplied = 0L;
            replicationResourceContainerHostBaselineEstablished = false;
        }

        private sealed class ReplicationHostResourceContainerState
        {
            public ReplicationHostResourceContainerState(
                ReplicationResourceContainerState state,
                ulong signature,
                long revision)
            {
                State = state;
                Signature = signature;
                Revision = revision;
            }

            public ReplicationResourceContainerState State { get; }
            public ulong Signature { get; }
            public long Revision { get; }
        }

        private sealed class PendingReplicationResourceContainerApply
        {
            public PendingReplicationResourceContainerApply(ReplicationResourceContainerState state)
            {
                State = state;
            }

            public ReplicationResourceContainerState State { get; }
            public int Attempts { get; set; }
        }
    }
}
