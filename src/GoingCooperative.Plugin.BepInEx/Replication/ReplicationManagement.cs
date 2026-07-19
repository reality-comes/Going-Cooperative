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
        private const string ManagementDeltaKind = "ManagementState";
        private const string AnimalStateDeltaKind = "AnimalState";
        private const float ReplicationAnimalAppearanceSnapshotSeconds = 10f;
        private const float ReplicationHostManagementExactDuplicateSeconds = 0.05f;
        private static bool replicationApplyingProductionRemoval;
        private static int replicationWorkerManageAuthoritativeApplyDepth;
        private static string replicationLastHostManagementMutationPayload = string.Empty;
        private static float replicationLastHostManagementMutationRealtime;
        // Model mutations belonging to one native action commonly arrive as a short burst
        // (for example: decrement training attempts, then write trainer/result; or unrope,
        // change animal type, then clear the order).  Hold the animal reference only until
        // the host's next runtime pump so one authoritative, final state is sent.
        private static readonly Dictionary<object, string> ReplicationPendingAnimalStateByAnimal =
            new Dictionary<object, string>(ReferenceObjectComparer.Instance);
        private static readonly Dictionary<string, string> ReplicationLastAnimalAppearanceSignatureByEntityId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static float replicationNextAnimalAppearanceSnapshotRealtime;

        private int TryInstallReplicationManagementCapture(Harmony harmony)
        {
            var count = 0;
            count += PatchManagementMethod(harmony, "NSMedieval.UI.ResearchPanelManager", "Unlock", Type.EmptyTypes, nameof(ReplicationResearchUnlockPrefix), nameof(ReplicationResearchUnlockPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.SelectionExtraProduction", "AddNewProduction", new[] { typeof(string) }, nameof(ReplicationProductionAddPrefix), nameof(ReplicationProductionAddPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.ProductionLayoutItemView", "CancelProduction", Type.EmptyTypes, nameof(ReplicationProductionTicketPrefix), nameof(ReplicationProductionTicketPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.ProductionLayoutItemView", "OnModeChange", new[] { typeof(int) }, nameof(ReplicationProductionTicketPrefix), nameof(ReplicationProductionTicketPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.ProductionLayoutItemView", "ChangePriority", new[] { typeof(int) }, nameof(ReplicationProductionTicketPrefix), nameof(ReplicationProductionTicketPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.ProductionLayoutItemView", "ChangeQuantity", new[] { typeof(int) }, nameof(ReplicationProductionTicketPrefix), nameof(ReplicationProductionTicketPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.ProductionLayoutItemView", "OnTogglePauseProductionButtonPress", Type.EmptyTypes, nameof(ReplicationProductionTicketPrefix), nameof(ReplicationProductionTicketPostfix));
            count += PatchProductionRemovalMethod(harmony);
            count += PatchManagementMethodByArity(harmony, "NSMedieval.UI.SelectionExtraStockpile", "OnResourceToggleChangeCallback", 2, nameof(ReplicationStockpileResourcePrefix), nameof(ReplicationStockpilePolicyPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.SelectionExtraStockpile", "OnPriorityChanged", Type.EmptyTypes, nameof(ReplicationStockpilePriorityPrefix), nameof(ReplicationStockpilePolicyPostfix));
            var jobType = AccessTools.TypeByName("NSMedieval.State.WorkerJobs.JobType");
            if (jobType != null)
            {
                count += PatchManagementMethod(harmony, "NSMedieval.UI.WorkerJobManager", "OnPriorityAdd", new[] { jobType, typeof(int), typeof(bool) }, nameof(ReplicationWorkerJobPrefix), nameof(ReplicationManagementPolicyPostfix));
                count += PatchManagementMethod(harmony, "NSMedieval.State.WorkerBehaviour", "ModifyJobPriority", new[] { jobType, typeof(int), typeof(bool) }, nameof(ReplicationWorkerJobModelPrefix), nameof(ReplicationWorkerJobModelPostfix));
            }
            count += PatchManagementMethod(harmony, "NSMedieval.State.WorkerBehaviour", "SetSelfTendingAllowed", new[] { typeof(bool) }, nameof(ReplicationWorkerManageBooleanPrefix), nameof(ReplicationWorkerManageBooleanPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.State.WorkerBehaviour", "set_UseRallyPoints", new[] { typeof(bool) }, nameof(ReplicationWorkerManageBooleanPrefix), nameof(ReplicationWorkerManageBooleanPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.State.WorkerBehaviour", "UpdateSingleManagePreset", new[] { typeof(string), typeof(string), typeof(bool) }, nameof(ReplicationWorkerManagePresetPrefix), nameof(ReplicationWorkerManagePresetPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.WorkerManageRowItem", "OnSelfTendValueChange", new[] { typeof(bool) }, nameof(ReplicationWorkerManageUiPrefix), nameof(ReplicationWorkerManageBooleanUiPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.WorkerManageRowItem", "OnUseRallyPointsChange", new[] { typeof(bool) }, nameof(ReplicationWorkerManageUiPrefix), nameof(ReplicationWorkerManageBooleanUiPostfix));
            count += PatchManagementMethod(harmony, "NSMedieval.UI.WorkerManageRowItem", "ChangePreset", new[] { typeof(string), typeof(string) }, nameof(ReplicationWorkerManageUiPrefix), nameof(ReplicationWorkerManagePresetUiPostfix));
            var hourType = AccessTools.TypeByName("NSMedieval.Goap.HourType");
            var soundButton = AccessTools.TypeByName("NSEipix.View.UI.SoundButton");
            if (hourType != null && soundButton != null) count += PatchManagementMethod(harmony, "NSMedieval.UI.WorkerScheduleManager", "ChangeHourType", new[] { soundButton, hourType }, nameof(ReplicationWorkerScheduleButtonPrefix), nameof(ReplicationManagementPolicyPostfix));
            var animalOrderType = AccessTools.TypeByName("NSMedieval.Types.AnimalOrderType");
            var animalInstance = AccessTools.TypeByName("NSMedieval.State.AnimalInstance");
            if (animalOrderType != null && animalInstance != null)
            {
                count += PatchManagementMethod(harmony, "NSMedieval.Controllers.AnimalController", "MarkForOrder", new[] { animalOrderType, animalInstance }, nameof(ReplicationAnimalOrderPrefix), nameof(ReplicationAnimalOrderPostfix));
                count += PatchManagementMethod(harmony, "NSMedieval.State.AnimalInstance", "TrainingAttemptCompleted", Type.EmptyTypes, nameof(ReplicationAnimalStatePrefix), nameof(ReplicationAnimalTrainingAttemptPostfix));
                count += PatchManagementMethod(harmony, "NSMedieval.State.AnimalInstance", "ResetPetOwner", Type.EmptyTypes, nameof(ReplicationAnimalStatePrefix), nameof(ReplicationAnimalStateMutationPostfix));
                var animalType = AccessTools.TypeByName("NSMedieval.Types.AnimalType");
                if (animalType != null)
                {
                    count += PatchManagementMethod(harmony, "NSMedieval.State.AnimalInstance", "SetAnimalType", new[] { animalType }, nameof(ReplicationAnimalStatePrefix), nameof(ReplicationAnimalStateMutationPostfix));
                }
                var humanoidInstance = AccessTools.TypeByName("NSMedieval.State.HumanoidInstance");
                if (humanoidInstance != null)
                {
                    count += PatchManagementMethod(harmony, "NSMedieval.State.AnimalInstance", "SetLastTrainingAttemptInfo", new[] { humanoidInstance, typeof(bool) }, nameof(ReplicationAnimalStatePrefix), nameof(ReplicationAnimalStateMutationPostfix));
                }
            }
            LogReplicationInfo("Going Cooperative replication management capture patches=" + count.ToString(CultureInfo.InvariantCulture));
            return count;
        }

        private int PatchManagementMethodByArity(Harmony harmony, string typeName, string methodName, int arity, string prefixName, string postfixName)
        {
            var type = AccessTools.TypeByName(typeName);
            MethodInfo? method = null;
            if (type != null) foreach (var candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (candidate.Name == methodName && candidate.GetParameters().Length == arity) { method = candidate; break; }
            var prefix = typeof(GoingCooperativePlugin).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(GoingCooperativePlugin).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || prefix == null || postfix == null) { LogReplicationWarning("Going Cooperative replication management arity patch missing " + typeName + "." + methodName); return 0; }
            try { harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix)); return 1; }
            catch (Exception ex) { LogReplicationWarning("Going Cooperative replication management arity patch failed " + typeName + "." + methodName + " " + ex.Message); return 0; }
        }

        private static bool ReplicationWorkerJobPrefix(object __instance, object __0, int __1, bool __2, ref string? __state)
        {
            __state = null;
            if (IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryGetWorkerPanelHumanoid(__instance, out var humanoid) || humanoid == null
                || !TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _)) return true;
            __state = LockstepCommandPayloads.CreateManagementPolicyPayload("WorkerJob", entityId, __0.ToString() ?? string.Empty, 0, __1, __2);
            if (ShouldSendReplicationLocalCommandIntent()) SendReplicationManagementIntent(__state, "worker-job");
            return true;
        }

        private static void ReplicationWorkerJobModelPrefix(object __instance, object __0, int __1, bool __2, ref string? __state)
        {
            __state = null;
            if (!replicationConfigHostMode || IsReplicationRegionOrderStateCaptureSuppressed()) return;
            var humanoid = AccessTools.Property(__instance.GetType(), "Humanoid")?.GetValue(__instance, null);
            if (humanoid == null || !TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _)) return;
            __state = LockstepCommandPayloads.CreateManagementPolicyPayload(
                "WorkerJob",
                entityId,
                __0.ToString() ?? string.Empty,
                0,
                __1,
                __2);
        }

        private static void ReplicationWorkerJobModelPostfix(string? __state)
        {
            BroadcastHostManagementMutation(__state, "worker-job-model");
        }

        private static bool ReplicationWorkerManageBooleanPrefix(
            object __instance,
            bool __0,
            MethodBase __originalMethod,
            ref string? __state)
        {
            __state = null;
            if (replicationWorkerManageAuthoritativeApplyDepth > 0
                || (!replicationConfigHostMode && IsReplicationRegionOrderStateCaptureSuppressed())
                || !TryGetReplicationWorkerBehaviourEntityId(__instance, out var entityId, out _))
            {
                return true;
            }

            var policy = string.Equals(__originalMethod.Name, "SetSelfTendingAllowed", StringComparison.Ordinal)
                ? "WorkerSelfTend"
                : string.Equals(__originalMethod.Name, "set_UseRallyPoints", StringComparison.Ordinal)
                    ? "WorkerRallyPoints"
                    : string.Empty;
            if (policy.Length == 0)
            {
                return true;
            }

            __state = LockstepCommandPayloads.CreateManagementPolicyPayload(policy, entityId, string.Empty, 0, 0, __0);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "worker-manage:" + policy);
                // Keep the client model/UI at the requested absolute policy while the
                // host validates it. Blocking the native setter makes the row restore
                // its old toggle and fire an immediate inverse callback.
                return true;
            }

            return true;
        }

        private static void ReplicationWorkerManageBooleanPostfix(string? __state, MethodBase __originalMethod)
        {
            BroadcastHostManagementMutation(__state, "worker-manage-model:" + __originalMethod.Name);
        }

        private static bool ReplicationWorkerManagePresetPrefix(
            object __instance,
            string __0,
            string __1,
            ref bool __2,
            ref string? __state)
        {
            __state = null;
            if (replicationWorkerManageAuthoritativeApplyDepth > 0
                || (!replicationConfigHostMode && IsReplicationRegionOrderStateCaptureSuppressed())
                || string.IsNullOrWhiteSpace(__0)
                || string.IsNullOrWhiteSpace(__1)
                || !TryGetReplicationWorkerBehaviourEntityId(__instance, out var entityId, out _))
            {
                return true;
            }

            __state = LockstepCommandPayloads.CreateWorkerManagePresetPayload(entityId, __0, __1, __2);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "worker-manage-preset:" + __0);
                // Mirror only the selected policy locally. ForceAutoEquip schedules
                // authoritative GOAP work and therefore remains host-only.
                __2 = false;
                return true;
            }

            return true;
        }

        private static void ReplicationWorkerManagePresetPostfix(string? __state)
        {
            BroadcastHostManagementMutation(__state, "worker-manage-preset-model");
        }

        private static bool ReplicationWorkerManageUiPrefix()
        {
            return true;
        }

        private static void ReplicationWorkerManageBooleanUiPostfix(
            object __instance,
            bool __0,
            MethodBase __originalMethod)
        {
            if (!replicationConfigHostMode
                || replicationWorkerManageAuthoritativeApplyDepth > 0
                || !TryGetWorkerPanelHumanoid(__instance, out var humanoid)
                || humanoid == null
                || !TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _))
            {
                return;
            }

            var policy = string.Equals(__originalMethod.Name, "OnSelfTendValueChange", StringComparison.Ordinal)
                ? "WorkerSelfTend"
                : string.Equals(__originalMethod.Name, "OnUseRallyPointsChange", StringComparison.Ordinal)
                    ? "WorkerRallyPoints"
                    : string.Empty;
            if (policy.Length == 0)
            {
                return;
            }

            BroadcastHostManagementMutation(
                LockstepCommandPayloads.CreateManagementPolicyPayload(policy, entityId, string.Empty, 0, 0, __0),
                "worker-manage-ui:" + policy);
        }

        private static void ReplicationWorkerManagePresetUiPostfix(object __instance, string __0, string __1)
        {
            if (!replicationConfigHostMode
                || replicationWorkerManageAuthoritativeApplyDepth > 0
                || string.IsNullOrWhiteSpace(__0)
                || string.IsNullOrWhiteSpace(__1)
                || !TryGetWorkerPanelHumanoid(__instance, out var humanoid)
                || humanoid == null
                || !TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _))
            {
                return;
            }

            BroadcastHostManagementMutation(
                LockstepCommandPayloads.CreateWorkerManagePresetPayload(entityId, __0, __1, true),
                "worker-manage-preset-ui:" + __0);
        }

        private static bool ReplicationAnimalOrderPrefix(object __0, object __1, ref string? __state)
        {
            __state = null;
            if (IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            var order = __0?.ToString() ?? string.Empty;
            var orderValue = Convert.ToInt32(__0, CultureInfo.InvariantCulture);
            if (!IsReplicationSupportedAnimalOrder(order, orderValue)) return true;
            if (__1 == null || !TryGetReplicationAgentOwnerEntityId(__1, out var entityId, out _)) return true;
            __state = LockstepCommandPayloads.CreateManagementPolicyPayload("AnimalOrder", entityId, order, 0, orderValue, true);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "animal-order:" + order);
                return false;
            }
            return true;
        }

        private static void ReplicationAnimalOrderPostfix(string? __state)
        {
            BroadcastHostManagementMutation(__state, "animal-order-model");
        }

        private static bool ReplicationAnimalStatePrefix()
        {
            return true;
        }

        private static void ReplicationAnimalTrainingAttemptPostfix(object __instance)
        {
            MarkReplicationAnimalStateDirty(__instance, "training-attempt");
        }

        private static void ReplicationAnimalStateMutationPostfix(object __instance)
        {
            MarkReplicationAnimalStateDirty(__instance, "model-mutation");
        }

        private static void MarkReplicationAnimalStateDirty(object? animal, string reason)
        {
            if (animal == null || !replicationConfigHostMode || !replicationRuntimeStarted || !replicationRemoteHelloReceived)
            {
                return;
            }

            ReplicationPendingAnimalStateByAnimal[animal] = reason;
            instance?.LogReplicationInfo("Going Cooperative animal-state dirty reason=" + reason
                + " type=" + FormatShortTypeName(animal.GetType())
                + " pending=" + ReplicationPendingAnimalStateByAnimal.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ReplicationWorkerScheduleButtonPrefix(object __instance, object __0, object __1, ref string? __state)
        {
            __state = null;
            if (IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryGetWorkerPanelHumanoid(__instance, out var humanoid) || humanoid == null
                || !TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _)) return true;
            if (!TryGetListMember(__instance, "hourButtons", out var hourButtons)) return true;
            var hour = hourButtons.IndexOf(__0);
            if (hour < 0) return true;
            __state = LockstepCommandPayloads.CreateManagementPolicyPayload("WorkerSchedule", entityId, __1.ToString() ?? string.Empty, hour, Convert.ToInt32(__1, CultureInfo.InvariantCulture), true);
            if (ShouldSendReplicationLocalCommandIntent()) SendReplicationManagementIntent(__state, "worker-schedule");
            return true;
        }

        private static void ReplicationManagementPolicyPostfix(string? __state)
        {
            // Worker jobs retransmit from WorkerBehaviour.ModifyJobPriority so every
            // host mutation path is covered. Do not also echo the surrounding UI call.
            if (!string.IsNullOrWhiteSpace(__state)
                && LockstepCommandPayloads.TryReadManagementPolicyPayload(__state, out var policy, out _, out _, out _, out _, out _)
                && string.Equals(policy, "WorkerJob", StringComparison.Ordinal)) return;
            BroadcastHostManagementMutation(__state, "management-policy-local");
        }

        private static bool TryGetWorkerPanelHumanoid(object manager, out object? humanoid)
        {
            humanoid = AccessTools.Property(manager.GetType(), "Humanoid")?.GetValue(manager, null);
            if (humanoid != null) return true;
            for (var current = manager.GetType(); current != null; current = current.BaseType)
            {
                var property = current.GetProperty("Humanoid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && (humanoid = property.GetValue(manager, null)) != null) return true;
            }
            return false;
        }

        private static bool ReplicationStockpileResourcePrefix(object __instance, object __0, bool __1, ref string? __state)
        {
            __state = null;
            if (IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryGetSelectedStockpileObjectId(__instance, out var objectId, out _)
                || !TryResolveReplicationModelId(__0, out var resourceId)) return true;
            __state = LockstepCommandPayloads.CreateManagementPolicyPayload("StockpileResource", objectId, resourceId, 0, 0, __1);
            if (ShouldSendReplicationLocalCommandIntent()) SendReplicationManagementIntent(__state, "stockpile-resource:" + resourceId);
            return true;
        }

        private static bool ReplicationStockpilePriorityPrefix(object __instance, ref string? __state)
        {
            __state = null;
            if (IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryGetSelectedStockpileObjectId(__instance, out var objectId, out _)
                || !TryReadInstanceMemberValue(__instance, "priorityDropdown", out var dropdown) || dropdown == null
                || !TryReadInstanceMemberValue(dropdown, "value", out var raw) || raw == null) return true;
            var priority = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            __state = LockstepCommandPayloads.CreateManagementPolicyPayload("StockpilePriority", objectId, string.Empty, 0, priority, true);
            if (ShouldSendReplicationLocalCommandIntent()) SendReplicationManagementIntent(__state, "stockpile-priority");
            return true;
        }

        private static void ReplicationStockpilePolicyPostfix(string? __state)
        {
            BroadcastHostManagementMutation(__state, "stockpile-policy-local");
        }

        private int PatchProductionRemovalMethod(Harmony harmony)
        {
            var type = AccessTools.TypeByName("NSMedieval.State.ProductionSystemInstance");
            MethodInfo? method = null;
            if (type != null)
            {
                foreach (var candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (candidate.Name == "RemoveProduction" && candidate.GetParameters().Length == 1)
                    {
                        method = candidate;
                        break;
                    }
                }
            }

            var prefix = typeof(GoingCooperativePlugin).GetMethod(nameof(ReplicationProductionRemovalPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(GoingCooperativePlugin).GetMethod(nameof(ReplicationProductionRemovalPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || prefix == null || postfix == null)
            {
                LogReplicationWarning("Going Cooperative replication production removal model patch missing");
                return 0;
            }

            try
            {
                harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication production removal model patch failed " + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private int PatchManagementMethod(Harmony harmony, string typeName, string methodName, Type[] args, string prefixName, string postfixName)
        {
            var type = AccessTools.TypeByName(typeName);
            var method = type == null ? null : AccessTools.Method(type, methodName, args);
            var prefix = typeof(GoingCooperativePlugin).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(GoingCooperativePlugin).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || prefix == null || postfix == null)
            {
                LogReplicationWarning("Going Cooperative replication management patch missing " + typeName + "." + methodName);
                return 0;
            }

            try
            {
                harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication management patch failed " + typeName + "." + methodName + " " + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private static bool ReplicationResearchUnlockPrefix(object __instance, ref string? __state)
        {
            __state = null;
            if (!TryReadInstanceMemberValue(__instance, "currentNode", out var node) || node == null
                || !TryResolveReplicationModelId(node, out var nodeId))
            {
                return true;
            }

            __state = LockstepCommandPayloads.CreateResearchActivatePayload(nodeId);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "research:" + nodeId);
                return false;
            }

            return true;
        }

        private static void ReplicationResearchUnlockPostfix(object __instance, string? __state)
        {
            if (TryReadInstanceMemberValue(__instance, "currentNode", out var node) && node != null)
            {
                var state = Convert.ToString(AccessTools.Property(node.GetType(), "ResearchState")?.GetValue(node, null), CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.Equals(state, "Activated", StringComparison.OrdinalIgnoreCase))
                {
                    BroadcastHostManagementMutation(__state, "research-local");
                }
            }
        }

        private static bool ReplicationProductionAddPrefix(object __instance, string __0, ref string? __state)
        {
            __state = null;
            if (!TryResolveProductionSystemFromSelection(__instance, out var system, out _)
                || system == null
                || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out _))
            {
                return true;
            }

            __state = LockstepCommandPayloads.CreateProductionQueuePayload("Add", x, y, z, -1, __0 ?? string.Empty, 0);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "production:add");
                return false;
            }

            return true;
        }

        private static void ReplicationProductionAddPostfix(string? __state)
        {
            BroadcastHostManagementMutation(__state, "production-add-local");
        }

        private static bool ReplicationProductionTicketPrefix(object __instance, MethodBase __originalMethod, object[] __args, ref string? __state)
        {
            __state = null;
            if (!TryBuildProductionTicketPayload(__instance, __originalMethod.Name, __args, out var payload, out var detail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication production ticket capture skipped method=" + __originalMethod.Name + " detail=" + detail);
                return true;
            }

            __state = payload;
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(payload, "production:" + __originalMethod.Name);
                return false;
            }

            return true;
        }

        private static void ReplicationProductionTicketPostfix(string? __state, MethodBase __originalMethod)
        {
            if (string.Equals(__originalMethod.Name, "CancelProduction", StringComparison.Ordinal))
            {
                // ProductionSystemInstance.RemoveProduction owns host-local removal
                // retransmission so alternate UI paths are covered exactly once.
                return;
            }
            BroadcastHostManagementMutation(__state, "production-local:" + __originalMethod.Name);
        }

        private static void ReplicationProductionRemovalPrefix(object __instance, object __0, ref string? __state)
        {
            __state = null;
            if (replicationApplyingProductionRemoval
                || !replicationConfigHostMode
                || !TryResolveProductionSystemGrid(__instance, out var x, out var y, out var z, out _)
                || !TryGetListMember(__instance, "productions", out var queue))
            {
                return;
            }

            var index = queue.IndexOf(__0);
            if (index < 0 || !TryResolveReplicationModelId(__0, out var blueprintId))
            {
                return;
            }

            __state = LockstepCommandPayloads.CreateProductionQueuePayload("Remove", x, y, z, index, blueprintId, 0);
            if (replicationConfigProductionStateV2
                && replicationConfigProductionTicketOrdersV2
                && TryGetReplicationProductionTicketIdV2(__0, out var ticketId))
            {
                __state = LockstepCommandPayloads.CreateProductionQueueV2Payload(
                    "Remove", ticketId, x, y, z, index, blueprintId, 0);
            }
        }

        private static void ReplicationProductionRemovalPostfix(string? __state)
        {
            BroadcastHostManagementMutation(__state, "production-model:RemoveProduction");
        }

        private static void SendReplicationManagementIntent(string payload, string source)
        {
            var command = new LockstepCommand(ReplicationClientPeerId, ++replicationIntentSequence, 0L, CommandKind.Custom, payload);
            if (!ShouldSkipDuplicateReplicationLocalCommand(command))
            {
                SendReplicationLocalCommandIntent(command, source);
            }
        }

        private static void BroadcastHostManagementMutation(string? payload, string source)
        {
            var current = instance;
            if (payload == null
                || string.IsNullOrWhiteSpace(payload)
                || current == null
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (string.Equals(payload, replicationLastHostManagementMutationPayload, StringComparison.Ordinal)
                && now - replicationLastHostManagementMutationRealtime < ReplicationHostManagementExactDuplicateSeconds)
            {
                return;
            }

            replicationLastHostManagementMutationPayload = payload;
            replicationLastHostManagementMutationRealtime = now;
            current.SendReplicationManagementDelta(payload, source);
        }

        private void SendReplicationManagementDelta(string payload, string source)
        {
            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ManagementDeltaKind,
                0,
                string.Empty,
                0,
                0,
                0,
                payload));
            LogReplicationInfo("Going Cooperative replication management state sent source=" + source + " payload=" + payload);
        }

        private void SendReplicationManagementStateIfSupported(LockstepCommand command, RuntimeCommandResult result)
        {
            if (result.Invoked && command.Kind == CommandKind.Custom
                && (LockstepCommandPayloads.TryReadResearchActivatePayload(command.PayloadJson, out _)
                    || LockstepCommandPayloads.TryReadProductionQueuePayload(command.PayloadJson, out _, out _, out _, out _, out _, out _, out _)
                    || LockstepCommandPayloads.TryReadProductionQueueV2Payload(command.PayloadJson, out _, out _, out _, out _, out _, out _, out _, out _)
                    || LockstepCommandPayloads.TryReadWorkerManagePresetPayload(command.PayloadJson, out _, out _, out _, out _)
                    || LockstepCommandPayloads.TryReadManagementPolicyPayload(command.PayloadJson, out _, out _, out _, out _, out _, out _)))
            {
                // Command application is already wrapped in the dedicated Manage
                // apply guard, so its model postfix cannot duplicate this response.
                // Keep the proven direct echo path independent of UI-capture gates.
                SendReplicationManagementDelta(command.PayloadJson, "accepted-command");
            }
        }

        private static bool TryApplyReplicationManagementDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            BeginReplicationRegionOrderStateCaptureSuppression();
            try
            {
                if (LockstepCommandPayloads.TryReadResearchActivatePayload(delta.Detail, out var nodeId))
                {
                    return TryApplyReplicationResearchActivate(nodeId, out detail);
                }

                if (LockstepCommandPayloads.TryReadProductionQueuePayload(
                    delta.Detail,
                    out var operation,
                    out var buildingX,
                    out var buildingY,
                    out var buildingZ,
                    out var ticketIndex,
                    out var blueprintId,
                    out var value))
                {
                    return TryApplyReplicationProductionQueue(operation, buildingX, buildingY, buildingZ, ticketIndex, blueprintId, value, out detail);
                }

                if (LockstepCommandPayloads.TryReadProductionQueueV2Payload(
                    delta.Detail,
                    out var operationV2,
                    out var ticketIdV2,
                    out var buildingXV2,
                    out var buildingYV2,
                    out var buildingZV2,
                    out var ticketIndexV2,
                    out var blueprintIdV2,
                    out var valueV2))
                {
                    return TryApplyReplicationProductionQueueV2(
                        operationV2, ticketIdV2, buildingXV2, buildingYV2, buildingZV2,
                        ticketIndexV2, blueprintIdV2, valueV2, out detail);
                }

                if (LockstepCommandPayloads.TryReadWorkerManagePresetPayload(
                    delta.Detail,
                    out var workerTargetId,
                    out var groupId,
                    out var presetId,
                    out var forceAutoEquip))
                {
                    return TryApplyReplicationWorkerManagePreset(workerTargetId, groupId, presetId, forceAutoEquip, out detail);
                }

                if (LockstepCommandPayloads.TryReadManagementPolicyPayload(delta.Detail, out var policy, out var targetId, out var key, out var index, out var policyValue, out var enabled))
                {
                    return TryApplyReplicationManagementPolicy(policy, targetId, key, index, policyValue, enabled, out detail);
                }

                detail = "management-payload-unsupported";
                return false;
            }
            finally
            {
                EndReplicationRegionOrderStateCaptureSuppression();
            }
        }

        private static bool TryApplyReplicationManagementPolicy(string policy, string targetId, string key, int index, int value, bool enabled, out string detail)
        {
            if (string.Equals(policy, "AnimalOrder", StringComparison.Ordinal))
            {
                if (!IsReplicationSupportedAnimalOrder(key, value))
                { detail = "animal-order-invalid order=" + key + " value=" + value.ToString(CultureInfo.InvariantCulture); return false; }
                if (!TryFindReplicationAnimatedAgentViewByEntityId(targetId, out var view, out var animalLookup) || view == null)
                { detail = "animal-order-target-missing " + animalLookup; return false; }
                var animal = AccessTools.Property(view.GetType(), "AnimalInstance")?.GetValue(view, null)
                    ?? AccessTools.Field(view.GetType(), "animalInstance")?.GetValue(view);
                var controllerType = AccessTools.TypeByName("NSMedieval.Controllers.AnimalController");
                var controller = controllerType == null ? null : ResolveReplicationUnityManagerInstance(controllerType);
                var method = controllerType == null ? null : AccessTools.Method(controllerType, "MarkForOrder");
                if (animal == null || controller == null || method == null)
                { detail = "animal-order-surface-missing " + animalLookup; return false; }
                var nativeOrder = Enum.ToObject(method.GetParameters()[0].ParameterType, value);
                method.Invoke(controller, new[] { nativeOrder, animal });
                detail = "ok animal-order entityId=" + targetId + " order=" + key + " " + animalLookup;
                return true;
            }
            if (string.Equals(policy, "WorkerSchedule", StringComparison.Ordinal))
            {
                if (!TryFindReplicationAgentOwnerByEntityId(targetId, out var humanoid, out var workerLookup) || humanoid == null)
                { detail = "worker-schedule-target-missing " + workerLookup; return false; }
                var method = AccessTools.Method(humanoid.GetType(), "ChangeSchedule");
                if (method == null) { detail = "worker-schedule-method-missing"; return false; }
                var hourType = Enum.ToObject(method.GetParameters()[1].ParameterType, value);
                method.Invoke(humanoid, new[] { (object)index, hourType });
                detail = "ok worker-schedule entityId=" + targetId + " hour=" + index + " type=" + key; return true;
            }
            if (string.Equals(policy, "WorkerJob", StringComparison.Ordinal))
            {
                if (!TryFindReplicationAgentOwnerByEntityId(targetId, out var humanoid, out var workerLookup) || humanoid == null)
                { detail = "worker-job-target-missing " + workerLookup; return false; }
                var behaviour = AccessTools.Property(humanoid.GetType(), "WorkerBehaviour")?.GetValue(humanoid, null);
                var method = behaviour == null ? null : AccessTools.Method(behaviour.GetType(), "ModifyJobPriority");
                if (behaviour == null || method == null) { detail = "worker-job-behaviour-or-method-missing"; return false; }
                object jobType;
                try { jobType = Enum.Parse(method.GetParameters()[0].ParameterType, key, true); }
                catch { detail = "worker-job-type-invalid type=" + key; return false; }
                method.Invoke(behaviour, new[] { jobType, (object)value, (object)enabled });
                detail = "ok worker-job entityId=" + targetId + " type=" + key + " priority=" + value + " active=" + enabled; return true;
            }
            if (string.Equals(policy, "WorkerSelfTend", StringComparison.Ordinal)
                || string.Equals(policy, "WorkerRallyPoints", StringComparison.Ordinal))
            {
                if (!TryResolveReplicationWorkerBehaviour(targetId, out _, out var behaviour, out var workerLookup) || behaviour == null)
                {
                    detail = "worker-manage-target-missing " + workerLookup;
                    return false;
                }

                var methodName = string.Equals(policy, "WorkerSelfTend", StringComparison.Ordinal)
                    ? "SetSelfTendingAllowed"
                    : "set_UseRallyPoints";
                var method = AccessTools.Method(behaviour.GetType(), methodName, new[] { typeof(bool) });
                if (method == null)
                {
                    detail = "worker-manage-method-missing method=" + methodName;
                    return false;
                }

                replicationWorkerManageAuthoritativeApplyDepth++;
                try
                {
                    method.Invoke(behaviour, new object[] { enabled });
                    var uiDetail = RefreshReplicationWorkerManageUi(targetId);
                    detail = "ok worker-manage entityId=" + targetId
                        + " policy=" + policy
                        + " enabled=" + enabled.ToString().ToLowerInvariant()
                        + " ui=" + uiDetail;
                    return true;
                }
                finally
                {
                    replicationWorkerManageAuthoritativeApplyDepth--;
                }
            }
            if (!TryResolveReplicationStockpileInstanceByObjectId(targetId, out var stockpile, out var lookup) || stockpile == null)
            { detail = "stockpile-policy-target-missing " + lookup; return false; }
            if (string.Equals(policy, "StockpilePriority", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(stockpile.GetType(), "SetPriority");
                if (method == null) { detail = "stockpile-priority-method-missing"; return false; }
                method.Invoke(stockpile, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, value) });
                detail = "ok stockpile-priority objectId=" + targetId + " value=" + value; return true;
            }
            if (string.Equals(policy, "StockpileResource", StringComparison.Ordinal))
            {
                var resource = TryResolveRepositoryItem("NSMedieval.Repository.ResourceRepository", key);
                var method = resource == null ? null : AccessTools.Method(stockpile.GetType(), "AllowResource", new[] { resource.GetType(), typeof(bool) });
                if (resource == null || method == null) { detail = "stockpile-resource-or-method-missing resourceId=" + key; return false; }
                method.Invoke(stockpile, new[] { resource, (object)enabled });
                detail = "ok stockpile-resource objectId=" + targetId + " resourceId=" + key + " allowed=" + enabled; return true;
            }
            detail = "management-policy-unsupported policy=" + policy; return false;
        }

        private static bool TryApplyReplicationWorkerManagePreset(
            string targetId,
            string groupId,
            string presetId,
            bool forceAutoEquip,
            out string detail)
        {
            if (!TryResolveReplicationWorkerBehaviour(targetId, out _, out var behaviour, out var workerLookup) || behaviour == null)
            {
                detail = "worker-manage-preset-target-missing " + workerLookup;
                return false;
            }

            var method = AccessTools.Method(
                behaviour.GetType(),
                "UpdateSingleManagePreset",
                new[] { typeof(string), typeof(string), typeof(bool) });
            if (method == null)
            {
                detail = "worker-manage-preset-method-missing";
                return false;
            }

            try
            {
                // Only the authoritative host may schedule ForceAutoEquip GOAP. The
                // client mirrors policy/UI and receives resulting equipment normally.
                replicationWorkerManageAuthoritativeApplyDepth++;
                try
                {
                    method.Invoke(behaviour, new object[]
                    {
                        groupId,
                        presetId,
                        replicationConfigHostMode && forceAutoEquip
                    });
                    var uiDetail = RefreshReplicationWorkerManageUi(targetId);
                    detail = "ok worker-manage-preset entityId=" + targetId
                        + " groupId=" + FormatReplicationWorldObjectDetailToken(groupId)
                        + " presetId=" + FormatReplicationWorldObjectDetailToken(presetId)
                        + " hostAutoEquip=" + (replicationConfigHostMode && forceAutoEquip ? "yes" : "no")
                        + " ui=" + uiDetail;
                    return true;
                }
                finally
                {
                    replicationWorkerManageAuthoritativeApplyDepth--;
                }
            }
            catch (Exception ex)
            {
                detail = "worker-manage-preset-apply-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationWorkerBehaviour(
            string entityId,
            out object? humanoid,
            out object? behaviour,
            out string detail)
        {
            behaviour = null;
            if (!TryFindReplicationAgentOwnerByEntityId(entityId, out humanoid, out detail) || humanoid == null)
            {
                return false;
            }

            behaviour = AccessTools.Property(humanoid.GetType(), "WorkerBehaviour")?.GetValue(humanoid, null)
                ?? AccessTools.Field(humanoid.GetType(), "workerBehaviour")?.GetValue(humanoid);
            if (behaviour == null)
            {
                detail = "worker-behaviour-missing " + detail;
                return false;
            }

            return true;
        }

        private static string RefreshReplicationWorkerManageUi(string entityId)
        {
            var rowType = AccessTools.TypeByName("NSMedieval.UI.WorkerManageRowItem");
            var updateItems = rowType == null ? null : AccessTools.Method(rowType, "UpdateItems", Type.EmptyTypes);
            if (rowType == null || updateItems == null)
            {
                return "surface-missing";
            }

            var refreshed = 0;
            var rows = Resources.FindObjectsOfTypeAll(rowType);
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (row == null
                    || !TryGetWorkerPanelHumanoid(row, out var humanoid)
                    || humanoid == null
                    || !TryGetReplicationAgentOwnerEntityId(humanoid, out var rowEntityId, out _)
                    || !string.Equals(rowEntityId, entityId, StringComparison.Ordinal))
                {
                    continue;
                }

                updateItems.Invoke(row, null);
                refreshed++;
            }

            return "rows:" + refreshed.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsReplicationSupportedAnimalOrder(string order, int value)
        {
            return string.Equals(order, "None", StringComparison.Ordinal) && value == 0
                || string.Equals(order, "Hunt", StringComparison.Ordinal) && value == 1
                || string.Equals(order, "Tame", StringComparison.Ordinal) && value == 2
                || string.Equals(order, "Harvest", StringComparison.Ordinal) && value == 3
                || string.Equals(order, "Slaughter", StringComparison.Ordinal) && value == 4
                || string.Equals(order, "Train", StringComparison.Ordinal) && value == 5
                || string.Equals(order, "Release", StringComparison.Ordinal) && value == 6;
        }

        private void UpdateReplicationAnimalState()
        {
            if (!replicationConfigHostMode || !replicationRuntimeStarted || !replicationRemoteHelloReceived)
            {
                return;
            }

            QueueHostReplicationAnimalAppearanceSnapshotIfDue();
            if (ReplicationPendingAnimalStateByAnimal.Count == 0)
            {
                return;
            }

            var pending = new List<KeyValuePair<object, string>>(ReplicationPendingAnimalStateByAnimal);
            ReplicationPendingAnimalStateByAnimal.Clear();
            for (var i = 0; i < pending.Count; i++)
            {
                SendHostReplicationAnimalState(pending[i].Key, pending[i].Value);
            }
        }

        private static void QueueHostReplicationAnimalAppearanceSnapshotIfDue()
        {
            if (Time.realtimeSinceStartup < replicationNextAnimalAppearanceSnapshotRealtime)
            {
                return;
            }

            replicationNextAnimalAppearanceSnapshotRealtime = Time.realtimeSinceStartup + ReplicationAnimalAppearanceSnapshotSeconds;
            var views = FindReplicationAnimatedAgentViews();
            var queued = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryClassifyReplicationView(view, out var kind)
                    || !string.Equals(kind, "animal", StringComparison.OrdinalIgnoreCase)
                    || !TryGetReplicationViewEntityId(view, out var entityId)
                    || string.IsNullOrWhiteSpace(entityId))
                {
                    continue;
                }

                var animal = AccessTools.Property(view.GetType(), "AnimalInstance")?.GetValue(view, null)
                    ?? AccessTools.Field(view.GetType(), "animalInstance")?.GetValue(view);
                if (animal == null
                    || !TryReadReplicationAnimalAppearance(animal, out _, out _, out _, out var appearanceSignature, out _))
                {
                    continue;
                }

                if (ReplicationLastAnimalAppearanceSignatureByEntityId.TryGetValue(entityId, out var previousSignature)
                    && string.Equals(previousSignature, appearanceSignature, StringComparison.Ordinal))
                {
                    continue;
                }

                ReplicationLastAnimalAppearanceSignatureByEntityId[entityId] = appearanceSignature;
                ReplicationPendingAnimalStateByAnimal[animal] = "appearance-snapshot";
                queued++;
            }

            if (queued > 0)
            {
                instance?.LogReplicationInfo("Going Cooperative animal appearance snapshot queued="
                    + queued.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void SendHostReplicationAnimalState(object animal, string reason)
        {
            var hasEntityId = TryGetReplicationAnimalEntityId(animal, out var entityId, out var entityLookup);
            if (!replicationConfigHostMode || !replicationRuntimeStarted || !replicationRemoteHelloReceived
                || !hasEntityId)
            {
                LogReplicationWarning("Going Cooperative animal-state send skipped reason=" + reason
                    + " lookup=" + entityLookup);
                return;
            }

            var order = Convert.ToInt32(AccessTools.Property(animal.GetType(), "OrderType")?.GetValue(animal, null) ?? 0, CultureInfo.InvariantCulture);
            var animalType = Convert.ToInt32(AccessTools.Property(animal.GetType(), "AnimalType")?.GetValue(animal, null) ?? 0, CultureInfo.InvariantCulture);
            var trainingAttempts = Convert.ToInt32(AccessTools.Property(animal.GetType(), "CurrentTrainingAttemptsLeft")?.GetValue(animal, null) ?? 0, CultureInfo.InvariantCulture);
            var trainer = Convert.ToString(AccessTools.Property(animal.GetType(), "LastTrainerName")?.GetValue(animal, null), CultureInfo.InvariantCulture) ?? string.Empty;
            var trainingSuccess = Convert.ToBoolean(AccessTools.Property(animal.GetType(), "LastTrainingAttemptSuccessful")?.GetValue(animal, null) ?? false, CultureInfo.InvariantCulture);
            var hasTrainingStat = TryReadReplicationAnimalTrainingStat(animal, out _, out var trainingCurrent, out var trainingMax, out var trainingStatDetail);
            var hasAppearance = TryReadReplicationAnimalAppearance(
                animal,
                out var furColor,
                out var furColorName,
                out var furTexture,
                out var appearanceSignature,
                out var appearanceDetail);
            if (hasAppearance)
            {
                ReplicationLastAnimalAppearanceSignatureByEntityId[entityId] = appearanceSignature;
            }
            var petOwner = AccessTools.Property(animal.GetType(), "PetOwner")?.GetValue(animal, null);
            var petOwnerId = petOwner != null && TryGetReplicationAgentOwnerEntityId(petOwner, out var resolvedOwnerId, out _)
                ? resolvedOwnerId
                : string.Empty;
            var ropedTo = AccessTools.Method(animal.GetType(), "RopedTo", Type.EmptyTypes)?.Invoke(animal, null);
            var ropeTargetId = ropedTo != null && TryGetReplicationAgentOwnerEntityId(ropedTo, out var resolvedRopeId, out _)
                ? resolvedRopeId
                : string.Empty;
            var leaving = TryReadInstanceMemberValue(animal, "isLeavingMap", out var rawLeaving)
                && rawLeaving is bool leavingMap && leavingMap;
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                AnimalStateDeltaKind,
                uniqueId,
                string.Empty,
                0,
                0,
                0,
                "entityId=" + entityId
                    + " order=" + order.ToString(CultureInfo.InvariantCulture)
                    + " animalType=" + animalType.ToString(CultureInfo.InvariantCulture)
                    + " trainingAttempts=" + trainingAttempts.ToString(CultureInfo.InvariantCulture)
                    + " trainerB64=" + EncodeReplicationDetailBase64(trainer)
                    + " trainingSuccess=" + trainingSuccess.ToString().ToLowerInvariant()
                    + " trainingStat=" + hasTrainingStat.ToString().ToLowerInvariant()
                    + " trainingCurrent=" + trainingCurrent.ToString("R", CultureInfo.InvariantCulture)
                    + " trainingMax=" + trainingMax.ToString("R", CultureInfo.InvariantCulture)
                    + " appearance=" + hasAppearance.ToString().ToLowerInvariant()
                    + " furColorR=" + furColor.r.ToString("R", CultureInfo.InvariantCulture)
                    + " furColorG=" + furColor.g.ToString("R", CultureInfo.InvariantCulture)
                    + " furColorB=" + furColor.b.ToString("R", CultureInfo.InvariantCulture)
                    + " furColorA=" + furColor.a.ToString("R", CultureInfo.InvariantCulture)
                    + " furColorNameB64=" + EncodeReplicationDetailBase64(furColorName)
                    + " furTextureB64=" + EncodeReplicationDetailBase64(furTexture)
                    + " petOwnerB64=" + EncodeReplicationDetailBase64(petOwnerId)
                    + " ropeTargetB64=" + EncodeReplicationDetailBase64(ropeTargetId)
                    + " leaving=" + leaving.ToString().ToLowerInvariant()
                    + " reason=" + reason));
            LogReplicationInfo("Going Cooperative animal-state sent entityId=" + entityId
                + " reason=" + reason
                + " order=" + order.ToString(CultureInfo.InvariantCulture)
                + " animalType=" + animalType.ToString(CultureInfo.InvariantCulture)
                + " trainingAttempts=" + trainingAttempts.ToString(CultureInfo.InvariantCulture)
                + " trainingStat=" + trainingStatDetail
                + " appearance=" + appearanceDetail);
        }

        private static bool TryReadReplicationAnimalAppearance(
            object animal,
            out Color furColor,
            out string furColorName,
            out string furTexture,
            out string signature,
            out string detail)
        {
            furColor = Color.white;
            furColorName = string.Empty;
            furTexture = string.Empty;
            signature = string.Empty;
            if (!TryReadReplicationObjectState(animal, "FurColor", out var rawColor) || rawColor is not Color resolvedColor
                || !TryReadReplicationObjectState(animal, "FurColorName", out var rawColorName) || rawColorName == null
                || !TryReadReplicationObjectState(animal, "FurTexture", out var rawTexture) || rawTexture == null)
            {
                detail = "fur-state-missing";
                return false;
            }

            furColor = resolvedColor;
            furColorName = Convert.ToString(rawColorName, CultureInfo.InvariantCulture) ?? string.Empty;
            furTexture = Convert.ToString(rawTexture, CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(furTexture))
            {
                detail = "fur-texture-empty";
                return false;
            }

            signature = furTexture
                + "|" + furColorName
                + "|" + furColor.r.ToString("R", CultureInfo.InvariantCulture)
                + "|" + furColor.g.ToString("R", CultureInfo.InvariantCulture)
                + "|" + furColor.b.ToString("R", CultureInfo.InvariantCulture)
                + "|" + furColor.a.ToString("R", CultureInfo.InvariantCulture);
            detail = "texture=" + FormatReplicationWorldObjectDetailToken(furTexture)
                + ",colorName=" + FormatReplicationWorldObjectDetailToken(furColorName);
            return true;
        }

        private static bool TryApplyReplicationAnimalAppearance(
            object view,
            object animal,
            int animalType,
            Color furColor,
            string furColorName,
            string furTexture,
            out string detail)
        {
            var fields = 0;
            fields += TrySetInstanceMemberValue(animal, "furColor", furColor) ? 1 : 0;
            fields += TrySetInstanceMemberValue(animal, "furColorName", furColorName) ? 1 : 0;
            fields += TrySetInstanceMemberValue(animal, "furTexture", furTexture) ? 1 : 0;
            var setMaterial = AccessTools.Method(view.GetType(), "SetMaterialBasedOnType");
            if (fields != 3 || setMaterial == null || setMaterial.GetParameters().Length != 1)
            {
                detail = "appearance-surface-missing fields=" + fields.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            try
            {
                var typeValue = Enum.ToObject(setMaterial.GetParameters()[0].ParameterType, animalType);
                setMaterial.Invoke(view, new[] { typeValue });
                detail = "ok texture=" + FormatReplicationWorldObjectDetailToken(furTexture)
                    + ",colorName=" + FormatReplicationWorldObjectDetailToken(furColorName)
                    + ",fields=" + fields.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static void ResetReplicationAnimalStateRuntime()
        {
            ReplicationPendingAnimalStateByAnimal.Clear();
            ReplicationLastAnimalAppearanceSignatureByEntityId.Clear();
            replicationNextAnimalAppearanceSnapshotRealtime = 0f;
        }

        private static bool TryReadReplicationAnimalTrainingStat(
            object animal,
            out object? stat,
            out float current,
            out float max,
            out string detail)
        {
            stat = null;
            current = 0f;
            max = 0f;
            if (!TryReadReplicationObjectState(animal, "Stats", out var stats) || stats == null)
            {
                detail = "stats-missing";
                return false;
            }

            var statType = AccessTools.TypeByName("NSMedieval.StatsSystem.StatType");
            if (statType == null || !statType.IsEnum)
            {
                detail = "stat-type-missing";
                return false;
            }

            try
            {
                var animalUntrained = Enum.Parse(statType, "AnimalUntrained", true);
                var getStat = FindReplicationInstanceMethod(stats.GetType(), "GetStat", new[] { statType });
                stat = getStat?.Invoke(stats, new[] { animalUntrained });
                if (stat == null
                    || !TryReadReplicationObjectState(stat, "Current", out var rawCurrent) || rawCurrent == null
                    || !TryReadReplicationObjectState(stat, "Max", out var rawMax) || rawMax == null)
                {
                    detail = "animal-untrained-stat-missing";
                    return false;
                }

                current = Convert.ToSingle(rawCurrent, CultureInfo.InvariantCulture);
                max = Convert.ToSingle(rawMax, CultureInfo.InvariantCulture);
                if (float.IsNaN(current) || float.IsInfinity(current)
                    || float.IsNaN(max) || float.IsInfinity(max) || max <= 0f)
                {
                    detail = "animal-untrained-stat-invalid";
                    return false;
                }

                detail = "current=" + current.ToString("R", CultureInfo.InvariantCulture)
                    + ",max=" + max.ToString("R", CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationAnimalTrainingStat(
            object animal,
            float authoritativeCurrent,
            out float currentReadback,
            out int percentageReadback,
            out string detail)
        {
            currentReadback = 0f;
            percentageReadback = -1;
            if (!TryReadReplicationAnimalTrainingStat(animal, out var stat, out _, out var localMax, out detail) || stat == null)
            {
                return false;
            }

            try
            {
                var forceCurrent = FindReplicationInstanceMethod(stat.GetType(), "ForceCurrentValue", new[] { typeof(float) })
                    ?? FindReplicationInstanceMethod(stat.GetType(), "SetCurrent", new[] { typeof(float) });
                if (forceCurrent == null)
                {
                    detail = "animal-untrained-stat-setter-missing";
                    return false;
                }

                forceCurrent.Invoke(stat, new object[] { authoritativeCurrent });
                if (!TryReadReplicationObjectState(stat, "Current", out var rawReadback) || rawReadback == null)
                {
                    detail = "animal-untrained-stat-readback-missing";
                    return false;
                }

                currentReadback = Convert.ToSingle(rawReadback, CultureInfo.InvariantCulture);
                percentageReadback = Convert.ToInt32(
                    AccessTools.Method(animal.GetType(), "GetTrainedPercentage", Type.EmptyTypes)?.Invoke(animal, null) ?? -1,
                    CultureInfo.InvariantCulture);
                detail = "ok current=" + currentReadback.ToString("R", CultureInfo.InvariantCulture)
                    + ",max=" + localMax.ToString("R", CultureInfo.InvariantCulture)
                    + ",percent=" + percentageReadback.ToString(CultureInfo.InvariantCulture);
                return Math.Abs(currentReadback - authoritativeCurrent) <= 0.001f;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryGetReplicationAnimalEntityId(object animal, out string entityId, out string detail)
        {
            if (TryGetReplicationAgentOwnerEntityId(animal, out entityId, out detail))
            {
                return true;
            }

            var managerType = AccessTools.TypeByName("NSMedieval.Manager.AnimalManager");
            var manager = managerType == null
                ? null
                : AccessTools.Property(managerType, "Instance")?.GetValue(null, null)
                    ?? AccessTools.Field(managerType, "Instance")?.GetValue(null);
            if (manager == null)
            {
                detail = "animal-manager-missing; " + detail;
                entityId = string.Empty;
                return false;
            }

            MethodInfo? getView = null;
            var methods = manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < methods.Length; i++)
            {
                var candidate = methods[i];
                var parameters = candidate.GetParameters();
                if (candidate.Name == "GetView" && parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(animal))
                {
                    getView = candidate;
                    break;
                }
            }

            try
            {
                var view = getView?.Invoke(manager, new[] { animal });
                if (view != null && TryGetReplicationViewEntityId(view, out entityId))
                {
                    detail = "animal-manager-view";
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "animal-manager-view-failed " + ex.GetType().Name;
                entityId = string.Empty;
                return false;
            }

            detail = "animal-manager-view-unresolved; " + detail;
            entityId = string.Empty;
            return false;
        }

        private static bool TryApplyReplicationAnimalStateDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "order", out var order)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "animalType", out var animalType)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "trainingAttempts", out var trainingAttempts)
                || !TryReadReplicationWorldObjectDetailBool(delta.Detail, "trainingSuccess", out var trainingSuccess)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "trainerB64", out var trainerToken)
                || !TryDecodeReplicationOptionalDetailBase64(trainerToken, out var trainer)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "petOwnerB64", out var petOwnerToken)
                || !TryDecodeReplicationOptionalDetailBase64(petOwnerToken, out var petOwnerId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "ropeTargetB64", out var ropeTargetToken)
                || !TryDecodeReplicationOptionalDetailBase64(ropeTargetToken, out var ropeTargetId)
                || !TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var lookup)
                || view == null)
            {
                detail = "animal-state-invalid-or-missing";
                return false;
            }

            var animal = AccessTools.Property(view.GetType(), "AnimalInstance")?.GetValue(view, null)
                ?? AccessTools.Field(view.GetType(), "animalInstance")?.GetValue(view);
            if (animal == null)
            {
                detail = "animal-state-instance-missing " + lookup;
                return false;
            }

            var animalModelType = animal.GetType();
            var hasTrainingStat = TryReadReplicationWorldObjectDetailBool(delta.Detail, "trainingStat", out var trainingStatIncluded)
                && trainingStatIncluded;
            var trainingCurrent = 0f;
            var hostTrainingMax = 0f;
            if (hasTrainingStat
                && (!TryReadReplicationWorldObjectDetailFloat(delta.Detail, "trainingCurrent", out trainingCurrent)
                    || !TryReadReplicationWorldObjectDetailFloat(delta.Detail, "trainingMax", out hostTrainingMax)))
            {
                detail = "animal-state-training-stat-invalid";
                return false;
            }

            var hasAppearance = TryReadReplicationWorldObjectDetailBool(delta.Detail, "appearance", out var appearanceIncluded)
                && appearanceIncluded;
            var authoritativeFurColor = Color.white;
            var authoritativeFurColorName = string.Empty;
            var authoritativeFurTexture = string.Empty;
            if (hasAppearance
                && (!TryReadReplicationWorldObjectDetailFloat(delta.Detail, "furColorR", out authoritativeFurColor.r)
                    || !TryReadReplicationWorldObjectDetailFloat(delta.Detail, "furColorG", out authoritativeFurColor.g)
                    || !TryReadReplicationWorldObjectDetailFloat(delta.Detail, "furColorB", out authoritativeFurColor.b)
                    || !TryReadReplicationWorldObjectDetailFloat(delta.Detail, "furColorA", out authoritativeFurColor.a)
                    || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "furColorNameB64", out var furColorNameToken)
                    || !TryDecodeReplicationOptionalDetailBase64(furColorNameToken, out authoritativeFurColorName)
                    || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "furTextureB64", out var furTextureToken)
                    || !TryDecodeReplicationOptionalDetailBase64(furTextureToken, out authoritativeFurTexture)
                    || string.IsNullOrWhiteSpace(authoritativeFurTexture)))
            {
                detail = "animal-state-appearance-invalid";
                return false;
            }

            var setOrder = AccessTools.Method(animalModelType, "SetOrder");
            var setAnimalType = AccessTools.Method(animalModelType, "SetAnimalType");
            if (setOrder == null || setAnimalType == null)
            {
                detail = "animal-state-model-surface-missing " + lookup;
                return false;
            }

            setAnimalType.Invoke(animal, new[] { Enum.ToObject(setAnimalType.GetParameters()[0].ParameterType, animalType) });
            var orderValue = Enum.ToObject(setOrder.GetParameters()[0].ParameterType, order);
            var animalControllerType = AccessTools.TypeByName("NSMedieval.Controllers.AnimalController");
            var animalController = animalControllerType == null ? null : ResolveReplicationUnityManagerInstance(animalControllerType);
            var markForOrder = animalControllerType == null
                ? null
                : AccessTools.Method(animalControllerType, "MarkForOrder", new[] { orderValue.GetType(), animalModelType });
            var orderApply = "direct";
            if (animalController != null && markForOrder != null)
            {
                BeginReplicationRegionOrderStateCaptureSuppression();
                try
                {
                    markForOrder.Invoke(animalController, new[] { orderValue, animal });
                    orderApply = "controller";
                }
                finally
                {
                    EndReplicationRegionOrderStateCaptureSuppression();
                }
            }
            else
            {
                setOrder.Invoke(animal, new[] { orderValue });
            }

            var maxAttempts = TryReadReplicationStaticIntMember(animalModelType, "MaxDailyTrainingAttempts", out var configuredMaxAttempts)
                ? Math.Max(0, configuredMaxAttempts)
                : 1;
            var usedAttempts = Math.Max(0, maxAttempts - Math.Max(0, trainingAttempts));
            var fields = 0;
            fields += TrySetInstanceMemberValue(animal, "trainingAttemptsCount", usedAttempts) ? 1 : 0;
            fields += TrySetInstanceMemberValue(animal, "lastTrainerName", trainer) ? 1 : 0;
            fields += TrySetInstanceMemberValue(animal, "lastTrainingAttemptSuccessful", trainingSuccess) ? 1 : 0;
            var trainingStatApplied = !hasTrainingStat;
            var trainingCurrentReadback = 0f;
            var trainingPercentageReadback = -1;
            var trainingStatApplyDetail = "not-included";
            if (hasTrainingStat)
            {
                trainingStatApplied = TryApplyReplicationAnimalTrainingStat(
                    animal,
                    trainingCurrent,
                    out trainingCurrentReadback,
                    out trainingPercentageReadback,
                    out trainingStatApplyDetail);
            }
            var appearanceApplied = !hasAppearance;
            var appearanceApplyDetail = "not-included";
            if (hasAppearance)
            {
                appearanceApplied = TryApplyReplicationAnimalAppearance(
                    view,
                    animal,
                    animalType,
                    authoritativeFurColor,
                    authoritativeFurColorName,
                    authoritativeFurTexture,
                    out appearanceApplyDetail);
            }

            var resetOwner = AccessTools.Method(animalModelType, "ResetPetOwner", Type.EmptyTypes);
            var assignOwner = AccessTools.Method(animalModelType, "AssignPetOwner");
            if (string.IsNullOrWhiteSpace(petOwnerId))
            {
                resetOwner?.Invoke(animal, null);
            }
            else if (assignOwner != null && TryFindReplicationAgentOwnerByEntityId(petOwnerId, out var owner, out _) && owner != null)
            {
                assignOwner.Invoke(animal, new[] { owner });
            }

            var ropeTo = AccessTools.Method(animalModelType, "RopeTo");
            if (ropeTo != null)
            {
                object? ropeTarget = null;
                if (!string.IsNullOrWhiteSpace(ropeTargetId))
                {
                    TryFindReplicationAgentOwnerByEntityId(ropeTargetId, out ropeTarget, out _);
                }

                if (string.IsNullOrWhiteSpace(ropeTargetId) || ropeTarget != null)
                {
                    ropeTo.Invoke(animal, new[] { ropeTarget, (object)false });
                }
            }

            var orderGivenFromUi = animalControllerType == null
                ? null
                : AccessTools.Method(animalControllerType, "OrderGivenFromAnimalUI", new[] { animalModelType });
            var uiNotified = false;
            if (animalController != null && orderGivenFromUi != null)
            {
                orderGivenFromUi.Invoke(animalController, new[] { animal });
                uiNotified = true;
            }

            var effectiveTrainingAttempts = Convert.ToInt32(
                AccessTools.Property(animalModelType, "CurrentTrainingAttemptsLeft")?.GetValue(animal, null) ?? -1,
                CultureInfo.InvariantCulture);

            detail = "ok animal-state entityId=" + entityId
                + " order=" + order.ToString(CultureInfo.InvariantCulture)
                + " orderApply=" + orderApply
                + " animalType=" + animalType.ToString(CultureInfo.InvariantCulture)
                + " trainingAttempts=" + trainingAttempts.ToString(CultureInfo.InvariantCulture)
                + " trainingReadback=" + effectiveTrainingAttempts.ToString(CultureInfo.InvariantCulture)
                + " trainingProgress=" + trainingStatApplyDetail
                + " hostTrainingMax=" + hostTrainingMax.ToString("R", CultureInfo.InvariantCulture)
                + " trainingProgressApplied=" + (trainingStatApplied ? "yes" : "no")
                + " appearance=" + appearanceApplyDetail
                + " appearanceApplied=" + (appearanceApplied ? "yes" : "no")
                + " fields=" + fields.ToString(CultureInfo.InvariantCulture)
                + " owner=" + (string.IsNullOrWhiteSpace(petOwnerId) ? "cleared" : petOwnerId)
                + " uiNotified=" + (uiNotified ? "yes" : "no");
            return true;
        }

        private static bool TryDecodeReplicationOptionalDetailBase64(string token, out string value)
        {
            if (string.Equals(token, "_", StringComparison.Ordinal))
            {
                value = string.Empty;
                return true;
            }

            return TryDecodeReplicationDetailBase64(token, out value);
        }

        private static bool TryGetSelectedStockpileObjectId(object selection, out string objectId, out string detail)
        {
            objectId = string.Empty;
            if (!TryGetListMember(selection, "storageObjects", out var storages) || storages.Count != 1 || storages[0] == null)
            { detail = "stockpile-selection-not-single"; return false; }
            var storage = storages[0]!;
            var value = AccessTools.Property(storage.GetType(), "ObjectId")?.GetValue(storage, null);
            objectId = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            var start = AccessTools.Property(storage.GetType(), "Start")?.GetValue(storage, null);
            if (start != null && TryReadReplicationVec3Int(start, out var x, out var y, out var z))
                objectId += "@" + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture);
            detail = objectId.Length == 0 ? "stockpile-objectId-empty" : "ok";
            return objectId.Length > 0;
        }

        private static object? TryResolveRepositoryItem(string markerTypeName, string id)
        {
            var marker = AccessTools.TypeByName(markerTypeName);
            var model = markerTypeName.IndexOf("ResourceRepository", StringComparison.Ordinal) >= 0 ? AccessTools.TypeByName("NSMedieval.Model.Resource") : null;
            var definition = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
            if (marker == null || model == null || definition == null) return null;
            var type = definition.MakeGenericType(marker, model);
            var repo = AccessTools.Property(type, "Instance")?.GetValue(null, null);
            return repo == null ? null : AccessTools.Method(type, "GetByID", new[] { typeof(string) })?.Invoke(repo, new object[] { id });
        }

        private static bool TryApplyReplicationResearchActivate(string nodeId, out string detail)
        {
            var managerType = AccessTools.TypeByName("NSMedieval.Research.ResearchManager");
            var controllerType = AccessTools.TypeByName("NSMedieval.Research.ResearchController");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            var controller = controllerType == null ? null : ResolveReplicationUnityManagerInstance(controllerType);
            var getNode = managerType == null ? null : AccessTools.Method(managerType, "GetInstanceById", new[] { typeof(string) });
            if (manager == null || controller == null || getNode == null)
            {
                detail = "research-runtime-missing nodeId=" + nodeId;
                return false;
            }

            var node = getNode.Invoke(manager, new object[] { nodeId });
            if (node == null)
            {
                detail = "research-node-missing nodeId=" + nodeId;
                return false;
            }

            var state = Convert.ToString(AccessTools.Property(node.GetType(), "ResearchState")?.GetValue(node, null), CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.Equals(state, "Activated", StringComparison.OrdinalIgnoreCase))
            {
                detail = "ok research-already-activated nodeId=" + nodeId;
                return true;
            }

            var activate = AccessTools.Method(controllerType, "Activate", new[] { node.GetType(), typeof(bool) });
            if (activate == null)
            {
                detail = "research-activate-method-missing nodeId=" + nodeId;
                return false;
            }

            // The host performs the resource/parent validation. Client replay uses
            // the loading path so a transient resource-count mismatch cannot reject
            // an already accepted authoritative unlock.
            activate.Invoke(controller, new[] { node, (object)!replicationConfigHostMode });
            var updated = Convert.ToString(AccessTools.Property(node.GetType(), "ResearchState")?.GetValue(node, null), CultureInfo.InvariantCulture) ?? string.Empty;
            var applied = string.Equals(updated, "Activated", StringComparison.OrdinalIgnoreCase);
            detail = (applied ? "ok" : "research-rejected") + " nodeId=" + nodeId + " state=" + updated;
            return applied;
        }

        private static bool TryApplyReplicationProductionQueue(string operation, int x, int y, int z, int ticketIndex, string blueprintId, int value, out string detail)
        {
            if (!TryFindProductionSystemAtGrid(x, y, z, out var system, out detail) || system == null)
            {
                return false;
            }

            if (string.Equals(operation, "Add", StringComparison.Ordinal))
            {
                var blueprint = TryResolveProductionBlueprint(blueprintId);
                var add = blueprint == null ? null : FindCompatibleReplicationInstanceMethod(system.GetType(), "AddNewProduction", blueprint.GetType());
                if (blueprint == null || add == null)
                {
                    detail = "production-add-blueprint-or-method-missing blueprintId=" + blueprintId;
                    return false;
                }

                add.Invoke(system, new[] { blueprint });
                detail = "ok production-add blueprintId=" + blueprintId;
                return true;
            }

            if (!TryGetProductionTicket(system, ticketIndex, blueprintId, out var ticket, out detail) || ticket == null)
            {
                return false;
            }

            if (string.Equals(operation, "Remove", StringComparison.Ordinal))
            {
                var cancel = AccessTools.Method(ticket.GetType(), "Cancel", Type.EmptyTypes);
                if (cancel == null) { detail = "production-cancel-method-missing"; return false; }
                replicationApplyingProductionRemoval = true;
                try
                {
                    cancel.Invoke(ticket, null);
                }
                finally
                {
                    replicationApplyingProductionRemoval = false;
                }
            }
            else if (string.Equals(operation, "Move", StringComparison.Ordinal))
            {
                AccessTools.Method(system.GetType(), "ChangePriority", new[] { ticket.GetType(), typeof(int) })?.Invoke(system, new[] { ticket, (object)value });
            }
            else if (string.Equals(operation, "SetMode", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(ticket.GetType(), "SetMode");
                if (method == null) { detail = "production-set-mode-method-missing"; return false; }
                var setCount = AccessTools.Method(ticket.GetType(), "SetProductTargetCount", new[] { typeof(int), typeof(bool) });
                if (setCount == null) { detail = "production-set-count-method-missing"; return false; }
                setCount.Invoke(ticket, new object[] { CalculateProductionModeTarget(ticket, value), true });
                method.Invoke(ticket, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, value), (object)true });
            }
            else if (string.Equals(operation, "SetCount", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(ticket.GetType(), "SetProductTargetCount", new[] { typeof(int), typeof(bool) });
                if (method == null) { detail = "production-set-count-method-missing"; return false; }
                method.Invoke(ticket, new object[] { Math.Max(0, value), true });
            }
            else if (string.Equals(operation, "SetOrder", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(ticket.GetType(), "SetOrder");
                if (method == null) { detail = "production-set-order-method-missing"; return false; }
                method.Invoke(ticket, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, value), (object)true });
            }
            else
            {
                detail = "production-operation-unsupported operation=" + operation;
                return false;
            }

            detail = "ok production-" + operation + " ticketIndex=" + ticketIndex.ToString(CultureInfo.InvariantCulture) + " blueprintId=" + blueprintId;
            return true;
        }

        private static bool TryApplyReplicationProductionQueueV2(
            string operation,
            long ticketId,
            int x,
            int y,
            int z,
            int ticketIndex,
            string blueprintId,
            int value,
            out string detail)
        {
            if (!replicationConfigProductionStateV2 || !replicationConfigProductionTicketOrdersV2)
            {
                return TryApplyReplicationProductionQueue(operation, x, y, z, ticketIndex, blueprintId, value, out detail);
            }
            if (!TryGetReplicationProductionTicketV2(ticketId, out var record) || record == null)
            {
                detail = "production-v2-ticket-pending ticketId=" + ticketId.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            RefreshReplicationProductionIdentityV2(record, sendIfChanged: replicationConfigHostMode);
            return TryApplyReplicationProductionTicketOperationV2(record, operation, value, out detail);
        }

        private static bool TryApplyReplicationProductionTicketOperationV2(
            ReplicationProductionTicketV2 record,
            string operation,
            int value,
            out string detail)
        {
            var ticket = record.Ticket;
            var system = record.System;
            if (string.Equals(operation, "Remove", StringComparison.Ordinal))
            {
                var cancel = AccessTools.Method(ticket.GetType(), "Cancel", Type.EmptyTypes);
                if (cancel == null) { detail = "production-v2-cancel-method-missing"; return false; }
                replicationApplyingProductionRemoval = true;
                try { cancel.Invoke(ticket, null); }
                finally { replicationApplyingProductionRemoval = false; }
            }
            else if (string.Equals(operation, "Move", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(system.GetType(), "ChangePriority", new[] { ticket.GetType(), typeof(int) });
                if (method == null) { detail = "production-v2-move-method-missing"; return false; }
                method.Invoke(system, new[] { ticket, (object)value });
            }
            else if (string.Equals(operation, "SetMode", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(ticket.GetType(), "SetMode");
                var setCount = AccessTools.Method(ticket.GetType(), "SetProductTargetCount", new[] { typeof(int), typeof(bool) });
                if (method == null || setCount == null) { detail = "production-v2-mode-method-missing"; return false; }
                setCount.Invoke(ticket, new object[] { CalculateProductionModeTarget(ticket, value), true });
                method.Invoke(ticket, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, value), (object)true });
            }
            else if (string.Equals(operation, "SetCount", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(ticket.GetType(), "SetProductTargetCount", new[] { typeof(int), typeof(bool) });
                if (method == null) { detail = "production-v2-count-method-missing"; return false; }
                method.Invoke(ticket, new object[] { Math.Max(0, value), true });
            }
            else if (string.Equals(operation, "SetOrder", StringComparison.Ordinal))
            {
                var method = AccessTools.Method(ticket.GetType(), "SetOrder");
                if (method == null) { detail = "production-v2-order-method-missing"; return false; }
                method.Invoke(ticket, new[] { Enum.ToObject(method.GetParameters()[0].ParameterType, value), (object)true });
            }
            else
            {
                detail = "production-v2-operation-unsupported operation=" + operation;
                return false;
            }

            MarkReplicationProductionTicketDirtyV2(record, progress: true, containers: true);
            detail = "ok production-v2-" + operation + " ticketId=" + record.HostTicketId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryBuildProductionTicketPayload(object view, string methodName, object[] args, out string payload, out string detail)
        {
            payload = string.Empty;
            if (!TryReadInstanceMemberValue(view, "production", out var ticket) || ticket == null
                || !TryReadInstanceMemberValue(ticket, "ownerSystem", out var system) || system == null
                || !TryResolveProductionSystemGrid(system, out var x, out var y, out var z, out detail)
                || !TryGetListMember(system, "productions", out var queue))
            {
                detail = "production-ticket-context-missing";
                return false;
            }

            var index = queue.IndexOf(ticket);
            TryResolveReplicationModelId(ticket, out var blueprintId);
            var operation = string.Empty;
            var value = 0;
            if (methodName == "CancelProduction") operation = "Remove";
            else if (methodName == "ChangePriority") { operation = "Move"; value = Convert.ToInt32(args[0], CultureInfo.InvariantCulture); }
            else if (methodName == "ChangeQuantity")
            {
                operation = "SetCount";
                var direction = Convert.ToInt32(args[0], CultureInfo.InvariantCulture);
                if (Input.GetKey((KeyCode)304)) direction *= 100;
                else if (Input.GetKey((KeyCode)306)) direction *= 10;
                value = Math.Max(0, ReadIntMember(ticket, "ProductTargetCount", "productTargetCount") + direction);
            }
            else if (methodName == "OnModeChange") { operation = "SetMode"; value = Convert.ToInt32(args[0], CultureInfo.InvariantCulture); }
            else if (methodName == "OnTogglePauseProductionButtonPress") { operation = "SetOrder"; value = ReadEnumMember(ticket, "Order", "order") == 2 ? 1 : 2; }
            else { detail = "unsupported-ui-method=" + methodName; return false; }

            payload = replicationConfigProductionStateV2
                && replicationConfigProductionTicketOrdersV2
                && TryGetReplicationProductionTicketIdV2(ticket, out var ticketId)
                    ? LockstepCommandPayloads.CreateProductionQueueV2Payload(operation, ticketId, x, y, z, index, blueprintId, value)
                    : LockstepCommandPayloads.CreateProductionQueuePayload(operation, x, y, z, index, blueprintId, value);
            detail = "ok";
            return true;
        }

        private static bool TryResolveProductionSystemFromSelection(object selection, out object? system, out string detail)
        {
            system = null;
            var component = AccessTools.Property(selection.GetType(), "SelectedComponentInstance")?.GetValue(selection, null);
            if (component == null) { detail = "selected-component-missing"; return false; }
            system = AccessTools.Property(component.GetType(), "ProductionSystemInstance")?.GetValue(component, null);
            detail = system == null ? "production-system-missing" : "ok";
            return system != null;
        }

        private static bool TryResolveProductionSystemGrid(object system, out int x, out int y, out int z, out string detail)
        {
            x = y = z = 0;
            var owner = AccessTools.Property(system.GetType(), "Owner")?.GetValue(system, null);
            var building = owner == null ? null : AccessTools.Property(owner.GetType(), "OwnerBuilding")?.GetValue(owner, null);
            if (building != null && TryInvokeReplicationObjectMethod(building, "GetGridPosition", out var position) && position != null && TryReadVec3IntLikeValue(position, out x, out y, out z))
            {
                detail = "source=production-owner-building-grid";
                return true;
            }
            detail = "production-owner-grid-missing";
            return false;
        }

        private static bool TryFindProductionSystemAtGrid(int x, int y, int z, out object? system, out string detail)
        {
            if (replicationConfigProductionStateV2)
            {
                if (TryFindReplicationProductionSystemV2AtGrid(x, y, z, out system) && system != null)
                {
                    detail = "ok production-v2-registry-system-at-grid";
                    return true;
                }

                detail = "production-v2-registry-system-pending grid=Vec3Int("
                    + x + "," + y + "," + z + ")";
                return false;
            }

            system = null;
            var viewType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            if (viewType == null) { detail = "building-view-type-missing"; return false; }
            var views = Resources.FindObjectsOfTypeAll(viewType);
            foreach (var view in views)
            {
                if (view == null || !TryResolveReplicationBuildingCandidateInstance(view, out var building, out _) || building == null
                    || !TryInvokeReplicationObjectMethod(building, "GetGridPosition", out var position) || position == null
                    || !TryReadVec3IntLikeValue(position, out var bx, out var by, out var bz) || bx != x || by != y || bz != z)
                {
                    continue;
                }
                var getComponent = building.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var method in getComponent)
                {
                    if (method.Name != "GetComponentInstance" || !method.IsGenericMethodDefinition) continue;
                    var componentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.ProductionComponentInstance");
                    if (componentType == null) continue;
                    var component = method.MakeGenericMethod(componentType).Invoke(building, null);
                    system = component == null ? null : AccessTools.Property(componentType, "ProductionSystemInstance")?.GetValue(component, null);
                    if (system != null) { detail = "ok production-system-at-grid"; return true; }
                }
            }
            detail = "production-system-not-found grid=Vec3Int(" + x + "," + y + "," + z + ")";
            return false;
        }

        private static object? TryResolveProductionBlueprint(string blueprintId)
        {
            var repositoryType = AccessTools.TypeByName("NSMedieval.Repository.ProductionRepository");
            var repository = repositoryType == null ? null : ResolveReplicationUnityManagerInstance(repositoryType);
            return repository == null ? null : AccessTools.Method(repository.GetType(), "GetByID", new[] { typeof(string) })?.Invoke(repository, new object[] { blueprintId });
        }

        private static bool TryGetProductionTicket(object system, int index, string blueprintId, out object? ticket, out string detail)
        {
            ticket = null;
            if (!TryGetListMember(system, "productions", out var queue) || index < 0 || index >= queue.Count)
            { detail = "production-ticket-index-invalid index=" + index; return false; }
            ticket = queue[index];
            TryResolveReplicationModelId(ticket!, out var actual);
            if (!string.Equals(actual, blueprintId, StringComparison.Ordinal))
            { detail = "production-ticket-stale expected=" + blueprintId + " actual=" + actual; ticket = null; return false; }
            detail = "ok"; return true;
        }

        private static bool TryGetListMember(object owner, string name, out IList list)
        {
            list = Array.Empty<object>();
            return TryReadInstanceMemberValue(owner, name, out var value) && value is IList parsed && (list = parsed) != null;
        }

        private static bool TryResolveReplicationModelId(object value, out string id)
        {
            id = string.Empty;
            var blueprint = AccessTools.Property(value.GetType(), "Blueprint")?.GetValue(value, null);
            var source = blueprint ?? value;
            var method = AccessTools.Method(source.GetType(), "GetID", Type.EmptyTypes);
            id = method?.Invoke(source, null) as string ?? string.Empty;
            if (id.Length == 0 && TryReadInstanceMemberValue(value, "blueprintId", out var raw)) id = raw as string ?? string.Empty;
            return id.Length > 0;
        }

        private static int ReadIntMember(object value, string upper, string lower)
        {
            if ((!TryReadInstanceMemberValue(value, upper, out var raw) || raw == null) && (!TryReadInstanceMemberValue(value, lower, out raw) || raw == null)) return 0;
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }

        private static int ReadEnumMember(object value, string upper, string lower)
        {
            if ((!TryReadInstanceMemberValue(value, upper, out var raw) || raw == null) && (!TryReadInstanceMemberValue(value, lower, out raw) || raw == null)) return 0;
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }

        private static int CalculateProductionModeTarget(object ticket, int mode)
        {
            if (mode == 0) return 1;
            if (mode == 1) return 0;

            var blueprint = AccessTools.Property(ticket.GetType(), "Blueprint")?.GetValue(ticket, null);
            if (blueprint == null) return 10;
            var jobType = AccessTools.Property(blueprint.GetType(), "JobType")?.GetValue(blueprint, null);
            if (jobType != null && Convert.ToInt32(jobType, CultureInfo.InvariantCulture) == 4096
                && TryResolveReplicationModelId(blueprint, out var researchBlueprintId))
            {
                var researchManagerType = AccessTools.TypeByName("NSMedieval.Research.ResearchManager");
                var researchManager = researchManagerType == null ? null : ResolveReplicationUnityManagerInstance(researchManagerType);
                var getBooks = researchManagerType == null ? null : AccessTools.Method(researchManagerType, "GetAvailableBookCount", new[] { typeof(string) });
                if (researchManager != null && getBooks != null)
                {
                    return Convert.ToInt32(getBooks.Invoke(researchManager, new object[] { researchBlueprintId }), CultureInfo.InvariantCulture) + 10;
                }
            }
            var products = AccessTools.Method(blueprint.GetType(), "GetAllProductsCount", Type.EmptyTypes);
            var productCount = products == null ? 0 : Convert.ToInt32(products.Invoke(blueprint, null), CultureInfo.InvariantCulture);
            return Math.Max(1, productCount) + 10;
        }
    }
}
