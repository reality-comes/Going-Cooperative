using System;
using System.Collections;
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
        private const string ReplicationProductionTicketIdentityV2DeltaKind = "ProductionTicketIdentityV2";
        private const float ReplicationProductionProgressDrainSeconds = 0.5f;
        private const float ReplicationProductionRecoverySeconds = 20f;
        private const int ReplicationProductionBootstrapViewsPerFrame = 16;
        private const int ReplicationProductionDirtyTicketsPerFrame = 8;
        private const int ReplicationProductionDirtyContainersPerFrame = 4;

        private sealed class ReplicationProductionTicketV2
        {
            public object Ticket = null!;
            public object System = null!;
            public long HostTicketId;
            public int GridX;
            public int GridY;
            public int GridZ;
            public int QueueIndex;
            public string BlueprintId = string.Empty;
            public string LastIdentityFingerprint = string.Empty;
            public string LastProgressFingerprint = string.Empty;
            public bool IdentityPublished;
            public bool HasAuthoritativeRuntimeState;
            public bool ForceContainerCheckpoint;
        }

        private static readonly Dictionary<object, ReplicationProductionTicketV2> ReplicationProductionTicketV2ByObject =
            new Dictionary<object, ReplicationProductionTicketV2>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<long, ReplicationProductionTicketV2> ReplicationProductionTicketV2ByHostId =
            new Dictionary<long, ReplicationProductionTicketV2>();
        private static readonly Dictionary<string, object> ReplicationProductionSystemV2ByGrid =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<string, object> ReplicationProductionComponentV2ByGrid =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<object, object> ReplicationProductionUiViewByTicket =
            new Dictionary<object, object>(ReferenceObjectComparer.Instance);
        private static readonly Queue<long> ReplicationProductionProgressDirtyQueue = new Queue<long>();
        private static readonly HashSet<long> ReplicationProductionProgressDirtySet = new HashSet<long>();
        private static readonly Queue<long> ReplicationProductionContainerDirtyQueue = new Queue<long>();
        private static readonly HashSet<long> ReplicationProductionContainerDirtySet = new HashSet<long>();
        private static readonly Dictionary<Type, MethodInfo?> ReplicationProductionGetComponentMethodByBuildingType =
            new Dictionary<Type, MethodInfo?>();

        private static Type? replicationProductionBuildingViewTypeV2;
        private static Type? replicationProductionComponentInstanceTypeV2;
        private static Type? replicationProductionVisualComponentTypeV2;
        private static PropertyInfo? replicationProductionSystemInstancePropertyV2;
        private static UnityEngine.Object[] replicationProductionBootstrapViewsV2 = Array.Empty<UnityEngine.Object>();
        private static int replicationProductionBootstrapIndexV2;
        private static bool replicationProductionBootstrapCollectedV2;
        private static bool replicationProductionBootstrapCompleteV2;
        private static long replicationProductionNextHostTicketIdV2 = 1L;
        private static float replicationProductionNextProgressDrainRealtimeV2;
        private static float replicationProductionNextRecoveryRealtimeV2;
        private static long replicationProductionSystemsRegisteredV2;
        private static long replicationProductionTicketsRegisteredV2;
        private static long replicationProductionIdentityDeltasSentV2;
        private static long replicationProductionIdentityDeltasAppliedV2;
        private static long replicationProductionProgressDeltasSentV2;
        private static long replicationProductionContainerStatesSentV2;

        private void TryInstallReplicationProductionStateV2Hooks(Harmony harmony)
        {
            if (!replicationConfigProductionStateV2)
            {
                return;
            }

            if (!TryBindReplicationProductionStateV2(out var bindDetail))
            {
                LogReplicationWarning("Going Cooperative production-v2 bindings unavailable " + bindDetail);
                return;
            }

            var systemPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationProductionSystemMutationV2Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            var ticketPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationProductionTicketMutationV2Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            var progressPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationProductionProgressMutationV2Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            var componentPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationProductionComponentLifecycleV2Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            var statePrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationProductionStateV2Prefix), BindingFlags.Static | BindingFlags.NonPublic));

            var patched = 0;
            patched += TryPatchReplicationProductionV2Methods(harmony, systemPostfix,
                "NSMedieval.State.ProductionSystemInstance",
                "AddNewProduction", "RemoveProduction", "ChangePriority", "SetCurrentActiveProduction",
                "OnProductionStateChanged", "OnLastStepCompleted");
            patched += TryPatchReplicationProductionV2Methods(harmony, ticketPostfix,
                "NSMedieval.State.ProductionInstance",
                "SetProductTargetCount", "SetMode", "SetOrder", "Cancel", "SetState", "StartStep",
                "EndCurrentStep", "StorageItemUpdated", "DeliverResource", "DropInvalidResources",
                "OnStepCompletedEvent", "LastStepCompleted", "ResetSteps");
            patched += TryPatchReplicationProductionV2Methods(harmony, progressPostfix,
                "NSMedieval.State.ProductionStepWorker", "Tick", "Reset", "Cancel", "OnStorageChanged");
            patched += TryPatchReplicationProductionV2Methods(harmony, progressPostfix,
                "NSMedieval.State.ProductionStepPassive", "OnTick", "Reset");
            patched += TryPatchReplicationProductionV2Methods(harmony, progressPostfix,
                "NSMedieval.State.ProductionStepCollect", "OnStorageChanged", "OnSecondaryResourceAdded", "Reset");
            patched += TryPatchReplicationProductionV2Methods(harmony, componentPostfix,
                "NSMedieval.BuildingComponents.ProductionComponent",
                "PreSpawnInitialization", "OnBaseBuildingEnterFinishedState", "OnCurrentProductionChanged");
            patched += TryPatchReplicationProductionV2Prefixes(harmony, statePrefix,
                "NSMedieval.State.ProductionInstance", "SetState");

            LogReplicationInfo("Going Cooperative production-v2 mutation hooks patched="
                + patched.ToString(CultureInfo.InvariantCulture));
        }

        private int TryPatchReplicationProductionV2Methods(
            Harmony harmony,
            HarmonyMethod postfix,
            string typeName,
            params string[] names)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                LogReplicationWarning("Going Cooperative production-v2 hook type missing type=" + typeName);
                return 0;
            }

            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            var count = 0;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!wanted.Contains(method.Name) || method.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    harmony.Patch(method, postfix: postfix);
                    count++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning("Going Cooperative production-v2 hook failed method="
                        + typeName + "." + method.Name + " error=" + FormatReflectionExceptionDetail(ex));
                }
            }

            return count;
        }

        private int TryPatchReplicationProductionV2Prefixes(
            Harmony harmony,
            HarmonyMethod prefix,
            string typeName,
            params string[] names)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) return 0;
            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            var count = 0;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!wanted.Contains(method.Name) || method.ContainsGenericParameters) continue;
                harmony.Patch(method, prefix: prefix);
                count++;
            }
            return count;
        }

        private static bool ReplicationProductionStateV2Prefix(object __instance)
        {
            return !replicationConfigProductionStateV2
                || replicationConfigHostMode
                || applyingRuntimeCommandDepth > 0
                || __instance == null
                || !ReplicationProductionTicketV2ByObject.TryGetValue(__instance, out var record)
                || !record.HasAuthoritativeRuntimeState;
        }

        private static void ReplicationProductionSystemMutationV2Postfix(object __instance)
        {
            if (!ShouldObserveReplicationProductionStateV2() || __instance == null)
            {
                return;
            }

            RegisterReplicationProductionSystemV2(__instance, "system-mutation", markAllDirty: true);
        }

        private static void ReplicationProductionTicketMutationV2Postfix(object __instance)
        {
            if (!ShouldObserveReplicationProductionStateV2() || __instance == null)
            {
                return;
            }

            var record = RegisterReplicationProductionTicketV2(__instance, null, "ticket-mutation");
            if (record != null)
            {
                MarkReplicationProductionTicketDirtyV2(record, progress: true, containers: true);
                RefreshReplicationProductionIdentityV2(record, sendIfChanged: replicationConfigHostMode);
            }
        }

        private static void ReplicationProductionProgressMutationV2Postfix(object __instance)
        {
            if (!ShouldObserveReplicationProductionStateV2() || __instance == null
                || !TryReadInstanceMemberValue(__instance, "ownerProductionInstance", out var ticket)
                || ticket == null)
            {
                return;
            }

            var record = RegisterReplicationProductionTicketV2(ticket, null, "step-mutation");
            if (record != null)
            {
                MarkReplicationProductionTicketDirtyV2(record, progress: true, containers: true);
            }
        }

        private static void ReplicationProductionComponentLifecycleV2Postfix(object __instance)
        {
            if (!ShouldObserveReplicationProductionStateV2() || __instance == null
                || !TryReadInstanceMemberValue(__instance, "componentInstance", out var componentInstance)
                || componentInstance == null
                || !TryReadInstanceMemberValue(componentInstance, "ProductionSystemInstance", out var system)
                || system == null
                || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _))
            {
                return;
            }

            ReplicationProductionComponentV2ByGrid[FormatReplicationProductionGridKeyV2(x, y, z)] = __instance;
            RegisterReplicationProductionSystemV2(system, "component-lifecycle", markAllDirty: replicationConfigHostMode);
        }

        private static bool ShouldObserveReplicationProductionStateV2()
        {
            // Lifecycle hooks must populate the registry while the synchronized save
            // is loading, before the replication runtime itself is allowed to start.
            return replicationConfigProductionStateV2;
        }

        private static bool TryBindReplicationProductionStateV2(out string detail)
        {
            if (replicationProductionBuildingViewTypeV2 != null
                && replicationProductionComponentInstanceTypeV2 != null
                && replicationProductionVisualComponentTypeV2 != null
                && replicationProductionSystemInstancePropertyV2 != null)
            {
                detail = "bindings=cached";
                return true;
            }

            replicationProductionBuildingViewTypeV2 ??=
                AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            replicationProductionComponentInstanceTypeV2 ??=
                AccessTools.TypeByName("NSMedieval.BuildingComponents.ProductionComponentInstance");
            replicationProductionVisualComponentTypeV2 ??=
                AccessTools.TypeByName("NSMedieval.BuildingComponents.ProductionComponent");
            replicationProductionSystemInstancePropertyV2 ??= replicationProductionComponentInstanceTypeV2 == null
                ? null
                : AccessTools.Property(replicationProductionComponentInstanceTypeV2, "ProductionSystemInstance");
            var bound = replicationProductionBuildingViewTypeV2 != null
                && replicationProductionComponentInstanceTypeV2 != null
                && replicationProductionVisualComponentTypeV2 != null
                && replicationProductionSystemInstancePropertyV2 != null;
            detail = bound ? "bindings=resolved" : "bindings=missing";
            return bound;
        }

        private static bool TryGetReplicationProductionSystemV2(
            object building,
            out object? system)
        {
            system = null;
            if (building == null || !TryBindReplicationProductionStateV2(out _)
                || replicationProductionComponentInstanceTypeV2 == null
                || replicationProductionSystemInstancePropertyV2 == null)
            {
                return false;
            }

            var buildingType = building.GetType();
            if (!ReplicationProductionGetComponentMethodByBuildingType.TryGetValue(buildingType, out var closedMethod))
            {
                closedMethod = null;
                foreach (var method in buildingType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (string.Equals(method.Name, "GetComponentInstance", StringComparison.Ordinal)
                        && method.IsGenericMethodDefinition
                        && method.GetGenericArguments().Length == 1
                        && method.GetParameters().Length == 0)
                    {
                        closedMethod = method.MakeGenericMethod(replicationProductionComponentInstanceTypeV2);
                        break;
                    }
                }

                ReplicationProductionGetComponentMethodByBuildingType[buildingType] = closedMethod;
            }

            if (closedMethod == null)
            {
                return false;
            }

            var component = closedMethod.Invoke(building, null);
            system = component == null ? null : replicationProductionSystemInstancePropertyV2.GetValue(component, null);
            return system != null;
        }

        private static ReplicationProductionTicketV2? RegisterReplicationProductionTicketV2(
            object ticket,
            object? knownSystem,
            string source)
        {
            if (ticket == null)
            {
                return null;
            }

            var system = knownSystem;
            if (system == null
                && (!TryReadInstanceMemberValue(ticket, "ownerSystem", out system) || system == null))
            {
                return null;
            }

            if (!TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _))
            {
                return null;
            }

            ReplicationProductionSystemV2ByGrid[FormatReplicationProductionGridKeyV2(x, y, z)] = system;
            if (!ReplicationProductionTicketV2ByObject.TryGetValue(ticket, out var record))
            {
                record = new ReplicationProductionTicketV2
                {
                    Ticket = ticket,
                    System = system,
                    GridX = x,
                    GridY = y,
                    GridZ = z,
                    HostTicketId = replicationConfigHostMode ? replicationProductionNextHostTicketIdV2++ : 0L
                };
                ReplicationProductionTicketV2ByObject[ticket] = record;
                if (record.HostTicketId > 0)
                {
                    ReplicationProductionTicketV2ByHostId[record.HostTicketId] = record;
                }

                replicationProductionTicketsRegisteredV2++;
            }

            record.System = system;
            record.GridX = x;
            record.GridY = y;
            record.GridZ = z;
            RefreshReplicationProductionIdentityV2(record, sendIfChanged: replicationConfigHostMode);
            return record;
        }

        private static void RegisterReplicationProductionSystemV2(object system, string source, bool markAllDirty)
        {
            if (system == null || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _))
            {
                return;
            }

            var gridKey = FormatReplicationProductionGridKeyV2(x, y, z);
            if (!ReplicationProductionSystemV2ByGrid.ContainsKey(gridKey))
            {
                replicationProductionSystemsRegisteredV2++;
            }
            ReplicationProductionSystemV2ByGrid[gridKey] = system;

            if (!TryGetListMember(system, "productions", out var queue))
            {
                return;
            }

            for (var i = 0; i < queue.Count; i++)
            {
                var ticket = queue[i];
                if (ticket == null) continue;
                var record = RegisterReplicationProductionTicketV2(ticket, system, source);
                if (record != null && markAllDirty)
                {
                    MarkReplicationProductionTicketDirtyV2(record, progress: true, containers: true);
                }
            }
        }

        private static void RefreshReplicationProductionIdentityV2(ReplicationProductionTicketV2 record, bool sendIfChanged)
        {
            if (!TryGetListMember(record.System, "productions", out var queue))
            {
                return;
            }

            record.QueueIndex = queue.IndexOf(record.Ticket);
            if (record.QueueIndex < 0)
            {
                // Keep the stable-id mapping long enough for an in-flight remove command
                // to resolve, but never publish or checkpoint a ticket that is no longer
                // present in its owning production queue.
                return;
            }
            TryResolveReplicationModelId(record.Ticket, out var blueprintId);
            record.BlueprintId = blueprintId;
            var fingerprint = record.GridX.ToString(CultureInfo.InvariantCulture) + ":"
                + record.GridY.ToString(CultureInfo.InvariantCulture) + ":"
                + record.GridZ.ToString(CultureInfo.InvariantCulture) + ":"
                + record.QueueIndex.ToString(CultureInfo.InvariantCulture) + ":" + record.BlueprintId;
            var changed = !string.Equals(record.LastIdentityFingerprint, fingerprint, StringComparison.Ordinal);
            record.LastIdentityFingerprint = fingerprint;
            if (sendIfChanged && record.HostTicketId > 0
                && replicationRuntimeStarted
                && replicationRemoteHelloReceived
                && (changed || !record.IdentityPublished)
                && (replicationConfigProductionTicketOrdersV2
                    || replicationConfigWorkstationRuntimePresentation
                    || replicationConfigResourceContainerReplication))
            {
                SendReplicationProductionTicketIdentityV2(record);
                record.IdentityPublished = true;
            }
        }

        private static void SendReplicationProductionTicketIdentityV2(ReplicationProductionTicketV2 record)
        {
            var current = instance;
            if (ReferenceEquals(current, null)) return;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationProductionTicketIdentityV2DeltaKind,
                record.HostTicketId,
                record.BlueprintId,
                record.GridX,
                record.GridY,
                record.GridZ,
                "ticketId=" + record.HostTicketId.ToString(CultureInfo.InvariantCulture)
                    + " ticketIndex=" + record.QueueIndex.ToString(CultureInfo.InvariantCulture)
                    + " blueprintId=" + FormatReplicationWorldObjectDetailToken(record.BlueprintId)
                    + " source=production-v2-registry"));
            replicationProductionIdentityDeltasSentV2++;
        }

        private static void MarkReplicationProductionTicketDirtyV2(
            ReplicationProductionTicketV2 record,
            bool progress,
            bool containers)
        {
            if (!replicationConfigHostMode || record.HostTicketId <= 0)
            {
                return;
            }

            if (progress && ReplicationProductionProgressDirtySet.Add(record.HostTicketId))
            {
                ReplicationProductionProgressDirtyQueue.Enqueue(record.HostTicketId);
            }
            if (containers && ReplicationProductionContainerDirtySet.Add(record.HostTicketId))
            {
                ReplicationProductionContainerDirtyQueue.Enqueue(record.HostTicketId);
            }
        }

        private void UpdateReplicationProductionStateV2()
        {
            if (!replicationConfigProductionStateV2 || !replicationRuntimeStarted)
            {
                return;
            }

            ProcessReplicationProductionBootstrapV2();
            if (!replicationConfigHostMode || !replicationRemoteHelloReceived)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (replicationProductionNextRecoveryRealtimeV2 <= 0f)
            {
                replicationProductionNextRecoveryRealtimeV2 = now + ReplicationProductionRecoverySeconds;
            }
            else if (now >= replicationProductionNextRecoveryRealtimeV2)
            {
                replicationProductionNextRecoveryRealtimeV2 = now + ReplicationProductionRecoverySeconds;
                foreach (var pair in ReplicationProductionTicketV2ByHostId)
                {
                    var record = pair.Value;
                    RefreshReplicationProductionIdentityV2(record, sendIfChanged: true);
                    if (record.QueueIndex < 0) continue;

                    // Recovery is deliberately a forced checkpoint. Dirty scheduling alone
                    // would be suppressed by the last-sent signatures and could not repair
                    // a transiently lost presentation/container state.
                    record.LastProgressFingerprint = string.Empty;
                    record.ForceContainerCheckpoint = true;
                    MarkReplicationProductionTicketDirtyV2(record, progress: true, containers: true);
                }
            }

            if (now >= replicationProductionNextProgressDrainRealtimeV2)
            {
                replicationProductionNextProgressDrainRealtimeV2 = now + ReplicationProductionProgressDrainSeconds;
                DrainReplicationProductionProgressV2();
            }
            DrainReplicationProductionContainersV2();
        }

        private static void ProcessReplicationProductionBootstrapV2()
        {
            if (replicationProductionBootstrapCompleteV2 || !TryBindReplicationProductionStateV2(out _)
                || replicationProductionVisualComponentTypeV2 == null)
            {
                return;
            }

            if (!replicationProductionBootstrapCollectedV2)
            {
                // ProductionComponent is the world-facing owner needed by the
                // overhead circle. Bootstrapping from it registers both the
                // visual and its ProductionSystemInstance in one heap walk.
                replicationProductionBootstrapViewsV2 = Resources.FindObjectsOfTypeAll(replicationProductionVisualComponentTypeV2);
                replicationProductionBootstrapCollectedV2 = true;
                replicationProductionBootstrapIndexV2 = 0;
            }

            var processed = 0;
            while (replicationProductionBootstrapIndexV2 < replicationProductionBootstrapViewsV2.Length
                && processed < ReplicationProductionBootstrapViewsPerFrame)
            {
                var view = replicationProductionBootstrapViewsV2[replicationProductionBootstrapIndexV2++];
                processed++;
                if (view == null
                    || !TryReadInstanceMemberValue(view, "componentInstance", out var componentInstance)
                    || componentInstance == null
                    || replicationProductionSystemInstancePropertyV2 == null
                    || replicationProductionSystemInstancePropertyV2.GetValue(componentInstance, null) is not object system
                    || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _))
                {
                    continue;
                }
                ReplicationProductionComponentV2ByGrid[FormatReplicationProductionGridKeyV2(x, y, z)] = view;
                RegisterReplicationProductionSystemV2(system, "bootstrap", markAllDirty: replicationConfigHostMode);
            }

            if (replicationProductionBootstrapIndexV2 >= replicationProductionBootstrapViewsV2.Length)
            {
                replicationProductionBootstrapViewsV2 = Array.Empty<UnityEngine.Object>();
                replicationProductionBootstrapCompleteV2 = true;
                instance?.LogReplicationInfo("Going Cooperative production-v2 bootstrap complete systems="
                    + replicationProductionSystemsRegisteredV2.ToString(CultureInfo.InvariantCulture)
                    + " tickets=" + replicationProductionTicketsRegisteredV2.ToString(CultureInfo.InvariantCulture)
                    + " components=" + ReplicationProductionComponentV2ByGrid.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void DrainReplicationProductionProgressV2()
        {
            if (!replicationConfigWorkstationRuntimePresentation)
            {
                ReplicationProductionProgressDirtyQueue.Clear();
                ReplicationProductionProgressDirtySet.Clear();
                return;
            }

            var processed = 0;
            while (processed < ReplicationProductionDirtyTicketsPerFrame && ReplicationProductionProgressDirtyQueue.Count > 0)
            {
                var ticketId = ReplicationProductionProgressDirtyQueue.Dequeue();
                ReplicationProductionProgressDirtySet.Remove(ticketId);
                processed++;
                if (!ReplicationProductionTicketV2ByHostId.TryGetValue(ticketId, out var record)) continue;
                RefreshReplicationProductionIdentityV2(record, sendIfChanged: true);
                if (record.QueueIndex < 0) continue;
                SendReplicationProductionProgressV2(record);
            }
        }

        private static void SendReplicationProductionProgressV2(ReplicationProductionTicketV2 record)
        {
            TryReadInstanceMemberValue(record.System, "CurrentProduction", out var currentProduction);
            if (!ReferenceEquals(currentProduction, record.Ticket))
            {
                return;
            }

            TryReadInstanceMemberValue(record.Ticket, "State", out var stateValue);
            var state = Convert.ToString(stateValue, CultureInfo.InvariantCulture) ?? "None";
            var stepIndex = ReadIntMember(record.Ticket, "CurrentStepIndex", "currentStepIndex");
            var progress = 0f;
            TryReadInstanceMemberValue(record.Ticket, "CurrentStep", out var step);
            if (step != null)
            {
                TryReadInstanceMemberValue(step, "Progress", out var raw);
                if (raw != null) progress = Mathf.Clamp01(Convert.ToSingle(raw, CultureInfo.InvariantCulture));
            }

            var permille = Mathf.RoundToInt(progress * 1000f);
            var fingerprint = state + ":" + stepIndex.ToString(CultureInfo.InvariantCulture) + ":" + permille.ToString(CultureInfo.InvariantCulture);
            if (string.Equals(record.LastProgressFingerprint, fingerprint, StringComparison.Ordinal)) return;
            record.LastProgressFingerprint = fingerprint;
            instance?.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationWorkstationRuntimeDeltaKind,
                record.HostTicketId,
                record.BlueprintId,
                record.GridX,
                record.GridY,
                record.GridZ,
                "ticketId=" + record.HostTicketId.ToString(CultureInfo.InvariantCulture)
                    + " x=" + record.GridX.ToString(CultureInfo.InvariantCulture)
                    + " y=" + record.GridY.ToString(CultureInfo.InvariantCulture)
                    + " z=" + record.GridZ.ToString(CultureInfo.InvariantCulture)
                    + " ticketIndex=" + record.QueueIndex.ToString(CultureInfo.InvariantCulture)
                    + " state=" + FormatReplicationWorldObjectDetailToken(state)
                    + " active=true stepIndex=" + stepIndex.ToString(CultureInfo.InvariantCulture)
                    + " progressPermille=" + permille.ToString(CultureInfo.InvariantCulture)
                    + " source=production-v2-dirty"));
            replicationProductionProgressDeltasSentV2++;
        }

        private static void DrainReplicationProductionContainersV2()
        {
            if (!replicationConfigResourceContainerReplication)
            {
                ReplicationProductionContainerDirtyQueue.Clear();
                ReplicationProductionContainerDirtySet.Clear();
                return;
            }

            var changed = new List<ReplicationResourceContainerState>();
            var processed = 0;
            while (processed < ReplicationProductionDirtyContainersPerFrame && ReplicationProductionContainerDirtyQueue.Count > 0)
            {
                var ticketId = ReplicationProductionContainerDirtyQueue.Dequeue();
                ReplicationProductionContainerDirtySet.Remove(ticketId);
                processed++;
                if (!ReplicationProductionTicketV2ByHostId.TryGetValue(ticketId, out var record)) continue;
                RefreshReplicationProductionIdentityV2(record, sendIfChanged: true);
                if (record.QueueIndex < 0) continue;
                var forceCheckpoint = record.ForceContainerCheckpoint;
                record.ForceContainerCheckpoint = false;
                CollectReplicationProductionContainerStateV2(record, "Storage", "production-v2-primary", forceCheckpoint, changed);
                CollectReplicationProductionContainerStateV2(record, "SecondaryIngredientStorage", "production-v2-secondary", forceCheckpoint, changed);
            }

            if (changed.Count == 0 || replicationTransport == null) return;
            for (var offset = 0; offset < changed.Count; offset += ReplicationResourceContainerBatchMaxContainers)
            {
                var count = Math.Min(ReplicationResourceContainerBatchMaxContainers, changed.Count - offset);
                var chunk = new List<ReplicationResourceContainerState>(count);
                for (var i = 0; i < count; i++) chunk.Add(changed[offset + i]);
                var batch = new ReplicationResourceContainerBatch(++replicationResourceContainerBatchSequence, Time.realtimeSinceStartup, false, chunk);
                replicationTransport.Send(ReplicationPayloadCodec.ForResourceContainerBatch(ReplicationHostPeerId, batch));
                replicationResourceContainerBatchesSent++;
            }
            replicationProductionContainerStatesSentV2 += changed.Count;
        }

        private static void CollectReplicationProductionContainerStateV2(
            ReplicationProductionTicketV2 record,
            string memberName,
            string kind,
            bool forceCheckpoint,
            List<ReplicationResourceContainerState> changed)
        {
            if (!TryReadInstanceMemberValue(record.Ticket, memberName, out var storage) || storage == null
                || !TryReadReplicationStorageEntries(storage, out var entries, out _)) return;
            var id = kind + ":" + record.HostTicketId.ToString(CultureInfo.InvariantCulture);
            var collected = new ReplicationResourceContainerState(id, kind,
                record.HostTicketId.ToString(CultureInfo.InvariantCulture) + ":"
                    + record.QueueIndex.ToString(CultureInfo.InvariantCulture), 0L,
                record.GridX, record.GridY, record.GridZ, entries);
            var signature = ComputeReplicationResourceContainerSignature(collected);
            var revision = 1L;
            if (ReplicationHostResourceContainers.TryGetValue(id, out var previous))
            {
                if (previous.Signature == signature && !forceCheckpoint) return;
                revision = previous.Revision + 1L;
            }
            var state = CopyReplicationResourceContainerWithRevision(collected, revision);
            ReplicationHostResourceContainers[id] = new ReplicationHostResourceContainerState(state, signature, revision);
            changed.Add(state);
        }

        private static bool TryApplyReplicationProductionTicketIdentityV2(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigProductionStateV2
                || (!replicationConfigProductionTicketOrdersV2
                    && !replicationConfigWorkstationRuntimePresentation
                    && !replicationConfigResourceContainerReplication))
            {
                detail = "production-v2-identity-gated-off";
                return true;
            }
            if (!TryReadReplicationWorldObjectDetailLong(delta.Detail, "ticketId", out var ticketId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "ticketIndex", out var ticketIndex)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "blueprintId", out var blueprintId))
            {
                detail = "production-v2-identity-invalid";
                return false;
            }
            var ticketDetail = "ticket-not-resolved";
            if (!TryFindProductionSystemAtGrid(delta.GridX, delta.GridY, delta.GridZ, out var system, out var systemDetail)
                || system == null || !TryGetProductionTicket(system, ticketIndex, blueprintId, out var ticket, out ticketDetail)
                || ticket == null)
            {
                detail = "production-v2-identity-pending " + systemDetail + " " + ticketDetail;
                return false;
            }
            var record = RegisterReplicationProductionTicketV2(ticket, system, "identity-apply");
            if (record == null)
            {
                detail = "production-v2-identity-register-failed";
                return false;
            }
            if (record.HostTicketId > 0) ReplicationProductionTicketV2ByHostId.Remove(record.HostTicketId);
            record.HostTicketId = ticketId;
            ReplicationProductionTicketV2ByHostId[ticketId] = record;
            replicationProductionIdentityDeltasAppliedV2++;
            detail = "ok production-v2-identity ticketId=" + ticketId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryGetReplicationProductionTicketV2(long ticketId, out ReplicationProductionTicketV2? record)
        {
            record = null;
            return ticketId > 0
                && ReplicationProductionTicketV2ByHostId.TryGetValue(ticketId, out record)
                && record != null;
        }

        private static bool TryResolveOrBindReplicationProductionTicketV2(
            long ticketId,
            int x,
            int y,
            int z,
            int ticketIndex,
            string blueprintId,
            out ReplicationProductionTicketV2? record,
            out string detail)
        {
            if (TryGetReplicationProductionTicketV2(ticketId, out record) && record != null)
            {
                detail = "production-v2-ticket-mapped";
                return true;
            }
            if (!TryFindReplicationProductionSystemV2AtGrid(x, y, z, out var system) || system == null
                || !TryGetListMember(system, "productions", out var queue)
                || ticketIndex < 0 || ticketIndex >= queue.Count || queue[ticketIndex] == null)
            {
                detail = "production-v2-ticket-pending";
                return false;
            }

            var ticket = queue[ticketIndex]!;
            TryResolveReplicationModelId(ticket, out var actualBlueprintId);
            if (!string.IsNullOrEmpty(blueprintId)
                && !string.Equals(actualBlueprintId, blueprintId, StringComparison.Ordinal))
            {
                detail = "production-v2-ticket-stale expected=" + blueprintId + " actual=" + actualBlueprintId;
                return false;
            }

            record = RegisterReplicationProductionTicketV2(ticket, system, "packet-self-bind");
            if (record == null)
            {
                detail = "production-v2-ticket-register-failed";
                return false;
            }
            if (record.HostTicketId > 0) ReplicationProductionTicketV2ByHostId.Remove(record.HostTicketId);
            record.HostTicketId = ticketId;
            ReplicationProductionTicketV2ByHostId[ticketId] = record;
            detail = "production-v2-ticket-self-bound";
            return true;
        }

        private static bool TryApplyReplicationProductionRuntimeStateV2(
            ReplicationProductionTicketV2 record,
            string stateName,
            int stepIndex,
            out string detail)
        {
            try
            {
                var ticketType = record.Ticket.GetType();
                var stateField = GetCachedInstanceField(ticketType, "state");
                var stepField = GetCachedInstanceField(ticketType, "currentStepIndex");
                if (stateField == null || !stateField.FieldType.IsEnum || stepField == null)
                {
                    detail = "production-v2-runtime-fields-missing";
                    return false;
                }
                var state = Enum.Parse(stateField.FieldType, stateName, true);
                applyingRuntimeCommandDepth++;
                try
                {
                    stateField.SetValue(record.Ticket, state);
                    stepField.SetValue(record.Ticket, stepIndex);
                    record.HasAuthoritativeRuntimeState = true;
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }
                detail = "production-v2-runtime-state=" + stateName
                    + " step=" + stepIndex.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = "production-v2-runtime-state-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryGetReplicationProductionTicketIdV2(object ticket, out long ticketId)
        {
            ticketId = 0L;
            return replicationConfigProductionStateV2
                && ReplicationProductionTicketV2ByObject.TryGetValue(ticket, out var record)
                && (ticketId = record.HostTicketId) > 0;
        }

        private static bool TryFindReplicationProductionSystemV2AtGrid(int x, int y, int z, out object? system)
        {
            return ReplicationProductionSystemV2ByGrid.TryGetValue(FormatReplicationProductionGridKeyV2(x, y, z), out system)
                && system != null;
        }

        private static bool TryFindReplicationProductionComponentV2AtGrid(int x, int y, int z, out object? component)
        {
            return ReplicationProductionComponentV2ByGrid.TryGetValue(FormatReplicationProductionGridKeyV2(x, y, z), out component)
                && component != null;
        }

        private static string FormatReplicationProductionGridKeyV2(int x, int y, int z)
        {
            return x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture);
        }

        private static void RegisterReplicationProductionTicketViewV2(object view, object ticket)
        {
            if (replicationConfigProductionStateV2 && view != null && ticket != null)
            {
                ReplicationProductionUiViewByTicket[ticket] = view;
            }
        }

        private static bool TryRefreshReplicationProductionTicketViewV2(object ticket)
        {
            if (!replicationConfigProductionStateV2
                || !ReplicationProductionUiViewByTicket.TryGetValue(ticket, out var view)
                || view == null) return false;
            TryInvokeReplicationObjectVoidMethod(view, "UpdateProductionData", out _);
            return true;
        }

        private static void ClearReplicationProductionStateV2()
        {
            ReplicationProductionTicketV2ByObject.Clear();
            ReplicationProductionTicketV2ByHostId.Clear();
            ReplicationProductionSystemV2ByGrid.Clear();
            ReplicationProductionComponentV2ByGrid.Clear();
            ReplicationProductionUiViewByTicket.Clear();
            ReplicationProductionProgressDirtyQueue.Clear();
            ReplicationProductionProgressDirtySet.Clear();
            ReplicationProductionContainerDirtyQueue.Clear();
            ReplicationProductionContainerDirtySet.Clear();
            replicationProductionBootstrapViewsV2 = Array.Empty<UnityEngine.Object>();
            replicationProductionBootstrapIndexV2 = 0;
            replicationProductionBootstrapCollectedV2 = false;
            replicationProductionBootstrapCompleteV2 = false;
            replicationProductionNextHostTicketIdV2 = 1L;
            replicationProductionNextProgressDrainRealtimeV2 = 0f;
            replicationProductionNextRecoveryRealtimeV2 = 0f;
            replicationProductionSystemsRegisteredV2 = 0L;
            replicationProductionTicketsRegisteredV2 = 0L;
            replicationProductionIdentityDeltasSentV2 = 0L;
            replicationProductionIdentityDeltasAppliedV2 = 0L;
            replicationProductionProgressDeltasSentV2 = 0L;
            replicationProductionContainerStatesSentV2 = 0L;
        }

        private static string FormatReplicationProductionStateV2Status()
        {
            return "productionV2=" + replicationConfigProductionStateV2
                + " productionSystems=" + ReplicationProductionSystemV2ByGrid.Count.ToString(CultureInfo.InvariantCulture)
                + " productionTickets=" + ReplicationProductionTicketV2ByObject.Count.ToString(CultureInfo.InvariantCulture)
                + " productionIdentitySent=" + replicationProductionIdentityDeltasSentV2.ToString(CultureInfo.InvariantCulture)
                + " productionIdentityApplied=" + replicationProductionIdentityDeltasAppliedV2.ToString(CultureInfo.InvariantCulture)
                + " productionProgressSent=" + replicationProductionProgressDeltasSentV2.ToString(CultureInfo.InvariantCulture)
                + " productionContainersSent=" + replicationProductionContainerStatesSentV2.ToString(CultureInfo.InvariantCulture);
        }
    }
}
