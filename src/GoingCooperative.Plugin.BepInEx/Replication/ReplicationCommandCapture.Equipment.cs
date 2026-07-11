using System;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private int TryInstallReplicationEquipmentCommandCapture(Harmony harmonyInstance)
        {
            var equipOrderPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationInventoryAddEquipOrderPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));

            return TryPatchReplicationCommandCaptureMethodsByName(
                harmonyInstance,
                equipOrderPostfix,
                "NSMedieval.State.InventoryInstance",
                "AddEquipOrder");
        }

        private static void ReplicationInventoryAddEquipOrderPostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode)
                || !ShouldSendReplicationLocalCommandIntent())
            {
                return;
            }

            if (!TryCreateReplicationEquipOrderCommand(__instance, __args, out var command, out var detail) || command == null)
            {
                instance?.LogReplicationInfo("Going Cooperative replication equip order capture skipped source="
                    + (__originalMethod.DeclaringType?.FullName ?? string.Empty)
                    + "."
                    + __originalMethod.Name
                    + " detail="
                    + detail);
                return;
            }

            var source = "InventoryInstance.AddEquipOrder " + detail;
            if (ShouldSkipDuplicateReplicationLocalCommand(command))
            {
                return;
            }

            if (IsReplicationCaptureModeShadow(replicationConfigCommandCaptureMode))
            {
                LogReplicationLocalCommandShadow(command, source);
                return;
            }

            SendReplicationLocalCommandIntent(command, source);
        }

        private static bool TryCreateReplicationEquipOrderCommand(object inventory, object[]? args, out LockstepCommand? command, out string detail)
        {
            command = null;
            if (inventory == null)
            {
                detail = "inventory-null";
                return false;
            }

            if (!TryFindReplicationEntityIdByInventory(inventory, out var entityId, out var entityDetail))
            {
                detail = entityDetail;
                return false;
            }

            if (args == null || args.Length == 0 || args[0] == null)
            {
                detail = "equip-order-target-missing entityId=" + entityId + " " + entityDetail;
                return false;
            }

            var pile = args[0];
            if (!TryGetReplicationPileStoredResource(pile, out var storedResource, out var storedDetail) || storedResource == null)
            {
                detail = "equip-order-stored-resource-missing entityId=" + entityId + " " + storedDetail + " " + entityDetail;
                return false;
            }

            if (!TryReadReplicationResourcePileBlueprintId(pile, storedResource, out var blueprintId, out var blueprintDetail)
                || string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "equip-order-blueprint-missing entityId=" + entityId + " " + blueprintDetail + " " + storedDetail + " " + entityDetail;
                return false;
            }

            if (!TryReadReplicationWorldObjectGridPosition(pile, out var x, out var y, out var z))
            {
                detail = "equip-order-grid-missing entityId=" + entityId + " blueprintId=" + blueprintId + " " + storedDetail + " " + entityDetail;
                return false;
            }

            command = new LockstepCommand(
                ReplicationClientPeerId,
                ++replicationIntentSequence,
                0L,
                CommandKind.Custom,
                LockstepCommandPayloads.CreateEquipOrderPayload(entityId, blueprintId, x, y, z),
                "equip-order:" + entityId + ":" + blueprintId,
                x,
                y,
                z);
            detail = "entityId="
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
                + blueprintDetail
                + " "
                + storedDetail
                + " "
                + entityDetail;
            return true;
        }

        private static bool TryFindReplicationEntityIdByInventory(object inventory, out string entityId, out string detail)
        {
            entityId = string.Empty;
            var views = FindReplicationAnimatedAgentViews();
            var scanned = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null || !TryGetReplicationViewEntityId(view, out var candidateEntityId))
                {
                    continue;
                }

                scanned++;
                if (TryResolveReplicationAgentInventory(view, out var candidateInventory, out _)
                    && ReferenceEquals(candidateInventory, inventory))
                {
                    entityId = candidateEntityId;
                    detail = "matched-inventory scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            detail = "inventory-entity-not-found scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private int TryPatchReplicationCommandCaptureMethodsByName(
            Harmony harmonyInstance,
            HarmonyMethod postfix,
            string typeName,
            string methodName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication command capture type missing: " + typeName);
                    return 0;
                }

                var patched = 0;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.ContainsGenericParameters)
                    {
                        continue;
                    }

                    harmonyInstance.Patch(method, postfix: postfix);
                    AppendPluginLog("Replication command capture patched: "
                        + type.FullName
                        + "."
                        + method.Name
                        + " returns="
                        + FormatShortTypeName(method.ReturnType));
                    patched++;
                }

                if (patched == 0)
                {
                    AppendPluginLog("Replication command capture method missing: " + typeName + "." + methodName);
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication command capture patch failed: "
                    + typeName
                    + "."
                    + methodName
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return 0;
            }
        }
    }
}
