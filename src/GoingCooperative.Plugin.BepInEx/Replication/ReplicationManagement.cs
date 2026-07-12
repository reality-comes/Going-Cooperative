using System;
using System.Collections;
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
        private static bool replicationApplyingProductionRemoval;

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
            LogReplicationInfo("Going Cooperative replication management capture patches=" + count.ToString(CultureInfo.InvariantCulture));
            return count;
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
            if (string.IsNullOrWhiteSpace(payload) || current == null || !replicationConfigHostMode || !replicationRuntimeStarted || !replicationRemoteHelloReceived)
            {
                return;
            }

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
                    || LockstepCommandPayloads.TryReadProductionQueuePayload(command.PayloadJson, out _, out _, out _, out _, out _, out _, out _)))
            {
                SendReplicationManagementDelta(command.PayloadJson, "accepted-command");
            }
        }

        private static bool TryApplyReplicationManagementDelta(ReplicationWorldObjectDelta delta, out string detail)
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

            detail = "management-payload-unsupported";
            return false;
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

            payload = LockstepCommandPayloads.CreateProductionQueuePayload(operation, x, y, z, index, blueprintId, value);
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
