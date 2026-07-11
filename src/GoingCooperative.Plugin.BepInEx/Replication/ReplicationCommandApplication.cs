using System;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        bool IRuntimeCommandActions.ApplyRegionOrder(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            string areaType,
            string subType,
            out string detail)
        {
            if (string.Equals(orderType, "Digging", StringComparison.Ordinal))
            {
                return TryInvokeGroundManagerOrderDigVoxel(startX, startY, startZ, endX, endY, endZ, out detail);
            }

            if (IsReplicationPlantRegionOrder(orderType))
            {
                return TryInvokePlantResourceManagerRegionOrder(orderType, startX, startY, startZ, endX, endY, endZ, out detail);
            }

            if (IsReplicationAllowForbidRegionOrder(orderType))
            {
                return TryInvokeAllowForbidRegionOrder(orderType, startX, startY, startZ, endX, endY, endZ, allowType, out detail);
            }

            if (IsReplicationStockpileRegionOrder(orderType))
            {
                return TryInvokeStockpileSelectionManagerRegionOrderViaSelectionManager(startX, startY, startZ, endX, endY, endZ, subType, out detail);
            }

            detail = "region-order-apply-shadow-only orderType=" + (orderType ?? string.Empty);
            return false;
        }

        bool IRuntimeCommandActions.ApplyCustom(string payloadJson, out string detail)
        {
            if (LockstepCommandPayloads.TryReadEquipOrderPayload(
                payloadJson,
                out var entityId,
                out var blueprintId,
                out var x,
                out var y,
                out var z))
            {
                return TryApplyReplicationEquipOrder(entityId, blueprintId, x, y, z, out detail);
            }

            detail = "custom-payload-unsupported";
            return false;
        }

        private static bool IsReplicationPlantRegionOrder(string orderType)
        {
            return string.Equals(orderType, "Chopping", StringComparison.Ordinal)
                || string.Equals(orderType, "CutAllVegetation", StringComparison.Ordinal)
                || string.Equals(orderType, "Harvesting", StringComparison.Ordinal);
        }

        private static bool IsReplicationDigRegionOrder(string orderType)
        {
            return string.Equals(orderType, "Digging", StringComparison.Ordinal);
        }

        private static bool IsReplicationStockpileRegionOrder(string orderType)
        {
            return string.Equals(orderType, "Stockpile", StringComparison.Ordinal);
        }

        private static bool IsReplicationAllowForbidRegionOrder(string orderType)
        {
            return string.Equals(orderType, "Allow", StringComparison.Ordinal)
                || string.Equals(orderType, "Forbid", StringComparison.Ordinal);
        }

        private static bool IsReplicationRegionOrderStateSupported(string orderType)
        {
            return IsReplicationDigRegionOrder(orderType)
                || IsReplicationPlantRegionOrder(orderType)
                || IsReplicationAllowForbidRegionOrder(orderType)
                || IsReplicationStockpileRegionOrder(orderType);
        }

        private static bool IsReplicationMarkerRegionOrder(string orderType)
        {
            return IsReplicationDigRegionOrder(orderType)
                || IsReplicationPlantRegionOrder(orderType)
                || IsReplicationAllowForbidRegionOrder(orderType);
        }

        private static bool TryApplyReplicationEquipOrder(
            string entityId,
            string blueprintId,
            int x,
            int y,
            int z,
            out string detail)
        {
            if (string.IsNullOrWhiteSpace(entityId))
            {
                detail = "equip-order-entity-empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "equip-order-blueprint-empty entityId=" + entityId;
                return false;
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "equip-order-view-missing entityId=" + entityId + " " + viewDetail;
                return false;
            }

            if (!TryResolveReplicationAgentInventory(view, out var inventory, out var inventoryDetail) || inventory == null)
            {
                detail = "equip-order-inventory-missing entityId=" + entityId + " " + inventoryDetail + " " + viewDetail;
                return false;
            }

            var lookupDelta = new ReplicationWorldObjectDelta(
                0,
                0f,
                "EquipOrderLookup",
                0,
                blueprintId,
                x,
                y,
                z,
                string.Empty);
            if (!TryFindReplicationResourcePile(lookupDelta, out var pile, out var pileDetail) || pile == null)
            {
                detail = "equip-order-pile-missing entityId="
                    + entityId
                    + " blueprintId="
                    + blueprintId
                    + " grid=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ") "
                    + pileDetail
                    + " "
                    + inventoryDetail
                    + " "
                    + viewDetail;
                return false;
            }

            var method = FindReplicationInstanceMethod(inventory.GetType(), "AddEquipOrder", new[] { pile.GetType() });
            if (method == null)
            {
                method = FindCompatibleReplicationInstanceMethod(inventory.GetType(), "AddEquipOrder", pile.GetType());
            }

            if (method == null)
            {
                detail = "equip-order-method-missing inventoryType="
                    + (inventory.GetType().FullName ?? inventory.GetType().Name)
                    + " pileType="
                    + (pile.GetType().FullName ?? pile.GetType().Name)
                    + " "
                    + pileDetail
                    + " "
                    + inventoryDetail;
                return false;
            }

            try
            {
                method.Invoke(inventory, new[] { pile });
                detail = "ok via=InventoryInstance.AddEquipOrder entityId="
                    + entityId
                    + " blueprintId="
                    + blueprintId
                    + " grid=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ") "
                    + pileDetail
                    + " "
                    + inventoryDetail
                    + " "
                    + viewDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "equip-order-invoke-failed " + FormatReflectionExceptionDetail(ex) + " " + pileDetail + " " + inventoryDetail;
                return false;
            }
        }

        private static bool TryResolveReplicationAgentInventory(object view, out object? inventory, out string detail)
        {
            inventory = null;
            if (!TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out var owner) || owner == null)
            {
                detail = "agent-owner-missing";
                return false;
            }

            if (TryReadInstanceMemberValue(owner, "Inventory", out inventory) && inventory != null)
            {
                detail = "owner=" + FormatShortTypeName(owner.GetType()) + " inventory=Inventory";
                return true;
            }

            if (TryReadInstanceMemberValue(owner, "inventory", out inventory) && inventory != null)
            {
                detail = "owner=" + FormatShortTypeName(owner.GetType()) + " inventory=inventory";
                return true;
            }

            if (TryInvokeReplicationObjectMethod(owner, "get_Inventory", out inventory) && inventory != null)
            {
                detail = "owner=" + FormatShortTypeName(owner.GetType()) + " inventory=get_Inventory";
                return true;
            }

            detail = "inventory-missing owner=" + FormatShortTypeName(owner.GetType());
            return false;
        }

        private static MethodInfo? FindCompatibleReplicationInstanceMethod(Type type, string methodName, Type argumentType)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(argumentType))
                    {
                        return method;
                    }
                }
            }

            return null;
        }
    }
}
