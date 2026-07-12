using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static string replicationLastRegionOrderType = string.Empty;
        private static float replicationLastRegionOrderRealtime;
        private static bool replicationLastRegionSelectionValid;
        private static int replicationLastRegionStartX;
        private static int replicationLastRegionStartY;
        private static int replicationLastRegionStartZ;
        private static int replicationLastRegionEndX;
        private static int replicationLastRegionEndY;
        private static int replicationLastRegionEndZ;
        private static bool replicationLastPrimaryPlantRegionValid;
        private static string replicationLastPrimaryPlantRegionOrderType = string.Empty;
        private static float replicationLastPrimaryPlantRegionRealtime;
        private static int replicationLastPrimaryPlantRegionStartX;
        private static int replicationLastPrimaryPlantRegionStartY;
        private static int replicationLastPrimaryPlantRegionStartZ;
        private static int replicationLastPrimaryPlantRegionEndX;
        private static int replicationLastPrimaryPlantRegionEndY;
        private static int replicationLastPrimaryPlantRegionEndZ;
        private static string replicationLastAreaOrderType = string.Empty;
        private static string replicationLastAreaOrderSubType = string.Empty;
        private static float replicationLastAreaOrderRealtime;
        private static float replicationNextRegionSelectionLogRealtime;
        private static string replicationZoneModifyOperation = string.Empty;
        private static string replicationZoneModifyId = string.Empty;
        private static float replicationZoneModifyRealtime;

        private int TryInstallReplicationRegionCommandCapture(Harmony harmonyInstance)
        {
            if (IsReplicationCaptureModeOff(replicationConfigRegionCommandMode))
            {
                return 0;
            }

            var orderPanelPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionOrderPanelPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var selectionPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionSelectionPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var selectionEventPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionSelectionEventPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var selectionActionPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionSelectionActionPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var digPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionDigPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var resourcePrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionResourcePrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var resourceSurfacePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionResourceSurfacePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var areaOrderPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionAreaOrderPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var areaOrderSetterPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionAreaOrderSetterPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var stockpilePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationRegionStockpileSpawnPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var zoneModifyPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationStockpileZoneModifyPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var contextualObjectPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationContextualObjectActionPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var contextualResourcePrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationContextualResourceGiveOrderPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var contextualBuildingPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationContextualBuildingDeconstructPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var infoPanelActionPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationInfoPanelActionPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));

            var patchedCount = 0;
            patchedCount += TryPatchReplicationCommandCaptureMethodByTypeNames(harmonyInstance, orderPanelPostfix, "NSMedieval.UI.OrdersPanelView", "OnOrderButtonClick", "NSMedieval.Types.OrderType");
            patchedCount += TryPatchReplicationCommandCaptureMethodByTypeNames(harmonyInstance, orderPanelPostfix, "NSMedieval.UI.OrdersPanelView", "OnOrderKeyPressed", "NSMedieval.Types.OrderType");
            patchedCount += TryPatchReplicationCommandCaptureMethodByTypeNames(harmonyInstance, selectionPostfix, "NSMedieval.Managers.Selection.SelectionManager", "SetupSelectionObject", "NSMedieval.Vec3Int", "NSMedieval.Vec3Int", "UnityEngine.GameObject", "System.Boolean");
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, selectionEventPostfix, "NSMedieval.Managers.Selection.SelectionManager", "OnGiveOrder", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, selectionEventPostfix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderDigVoxel", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, selectionEventPostfix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderCancel", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, selectionEventPostfix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderDeconstruction", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, selectionActionPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnChopOrder");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, selectionActionPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderCancel");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, selectionActionPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderDeconstruction");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, selectionActionPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderAllowForbid");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, selectionActionPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnOrderUrgentHaul");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, digPrefix, "NSMedieval.Terrain.GroundManager", "OrderDigVoxel", "NSMedieval.Vec3Int", "NSMedieval.Vec3Int");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, digPrefix, "NSMedieval.Terrain.GroundManager", "OrderDigVoxelRange", "NSMedieval.Vec3Int", "NSMedieval.Vec3Int");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, resourcePrefix, "NSMedieval.Managers.Selection.SelectionManager", "ForceOrderOnResource", "NSMedieval.Vec3Int");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, resourcePrefix, "NSMedieval.Manager.PlantResourceManager", "OnForceOrderOnResource", "NSMedieval.Vec3Int");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderSetterPrefix, "NSMedieval.Managers.Selection.SelectionManager", "set_AreaOrderType", "NSMedieval.Types.AreaType");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnAssignStockpileArea");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnAssignCropsArea");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnModifyZone");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderPrefix, "NSMedieval.Managers.Selection.SelectionManager", "ZoneSelectionFinished");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnAreaOrderTempHack");
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(harmonyInstance, areaOrderPrefix, "NSMedieval.Managers.Selection.SelectionManager", "OnSelectArea", "NSMedieval.Types.AreaType", "System.String");
            // ExpandZone/ShrinkZone only arm the vanilla selection mode. Sending
            // here commits the previous selection and creates a second command.
            // StockpileView records the mode; ZoneSelectionFinished sends once.
            patchedCount += TryPatchReplicationCommandCaptureMethodByTypeNames(harmonyInstance, stockpilePostfix, "NSMedieval.Stockpiles.StockpileManager", "SpawnStockpile", "NSMedieval.Stockpiles.Stockpile", "NSMedieval.Vec3Int", "NSMedieval.Vec3Int");
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, zoneModifyPrefix, "NSMedieval.Stockpiles.StockpileView", "ExpandStockpile", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, zoneModifyPrefix, "NSMedieval.Stockpiles.StockpileView", "ShrinkStockpile", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCapturePrefixMethodByTypeNames(
                harmonyInstance,
                infoPanelActionPrefix,
                "NSMedieval.UI.InfoPanelAction",
                "RunCurrentAction");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByName(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager.PlantResourceManager", "OnOrderChopEvent");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByName(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager.PlantResourceManager", "OnOrderResourceCollectionEventCallback");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByName(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager.MapResourceManager`3", "OnForceOrderOnResource");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByName(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager.MapResourceManager`3", "OnOrderResourceCollectionEvent");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByName(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager.MapResourceManager`3", "OnOrderResourceCollectionEventCallback");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByName(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Views.Resources.MapResourceView`1", "OnOrderResourceCollectionEvent");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByNameOnMatchingTypes(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager", "MapResourceManager", "OnForceOrderOnResource");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByNameOnMatchingTypes(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager", "MapResourceManager", "OnOrderResourceCollectionEvent");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByNameOnMatchingTypes(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Manager", "MapResourceManager", "OnOrderResourceCollectionEventCallback");
            patchedCount += TryPatchPassiveCommandSurfaceMethodsByNameOnMatchingTypes(harmonyInstance, resourceSurfacePostfix, "NSMedieval.Views.Resources", "MapResourceView", "OnOrderResourceCollectionEvent");
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, resourceSurfacePostfix, "NSMedieval.AdditionalMenuItems.PrioritiseHarvestMenuItem", "OnClickCallback", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, resourceSurfacePostfix, "NSMedieval.AdditionalMenuItems.PrioritiseChopMenuItem", "OnClickCallback", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, resourceSurfacePostfix, "NSMedieval.AdditionalMenuItems.PrioritiseMineMenuItem", "OnClickCallback", Type.EmptyTypes);

            return patchedCount;
        }

        private static void ReplicationRegionOrderPanelPostfix(MethodBase __originalMethod, object __0)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return;
            }

            replicationLastRegionOrderType = __0?.ToString() ?? string.Empty;
            replicationLastRegionOrderRealtime = Time.realtimeSinceStartup;
            instance?.LogReplicationInfo("Going Cooperative replication region command panel mode="
                + replicationConfigRegionCommandMode
                + " method="
                + __originalMethod.Name
                + " orderType="
                + replicationLastRegionOrderType
                + FormatReplicationLastRegionSelection());
        }

        private static void ReplicationRegionSelectionPostfix(MethodBase __originalMethod, object __0, object __1)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return;
            }

            if (!TryReadReplicationVec3Int(__0, out replicationLastRegionStartX, out replicationLastRegionStartY, out replicationLastRegionStartZ)
                || !TryReadReplicationVec3Int(__1, out replicationLastRegionEndX, out replicationLastRegionEndY, out replicationLastRegionEndZ))
            {
                replicationLastRegionSelectionValid = false;
                instance?.LogReplicationWarning("Going Cooperative replication region selection read failed method=" + __originalMethod.Name);
                return;
            }

            replicationLastRegionSelectionValid = true;
            if (ShouldLogReplicationRegionSelection())
            {
                instance?.LogReplicationInfo("Going Cooperative replication region selection mode="
                    + replicationConfigRegionCommandMode
                    + " method="
                    + __originalMethod.Name
                    + " orderType="
                    + (string.IsNullOrEmpty(replicationLastRegionOrderType) ? "<unknown>" : replicationLastRegionOrderType)
                    + FormatReplicationLastRegionSelection());
            }
        }

        private static void ReplicationRegionSelectionEventPostfix(MethodBase __originalMethod)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return;
            }

            instance?.LogReplicationInfo("Going Cooperative replication region selection event mode="
                + replicationConfigRegionCommandMode
                + " method="
                + __originalMethod.Name
                + " orderType="
                + (string.IsNullOrEmpty(replicationLastRegionOrderType) ? "<unknown>" : replicationLastRegionOrderType)
                + FormatReplicationLastRegionSelection());
        }

        private static bool ReplicationRegionSelectionActionPrefix(MethodBase __originalMethod, object __instance)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return true;
            }

            var methodName = __originalMethod?.Name ?? string.Empty;
            var orderType = ResolveReplicationSelectionOrderType(__instance, methodName);
            if (!TryResolveReplicationSelectionRegion(
                    __instance,
                    out var startX,
                    out var startY,
                    out var startZ,
                    out var endX,
                    out var endY,
                    out var endZ,
                    out var selectionDetail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication region selection action capture skipped method="
                    + methodName
                    + " orderType="
                    + orderType
                    + " reason="
                    + selectionDetail);
                return true;
            }

            var allowType = ResolveReplicationRegionAllowType(__instance, orderType);
            instance?.LogReplicationInfo("Going Cooperative replication region selection action mode="
                + replicationConfigRegionCommandMode
                + " method="
                + methodName
                + " orderType="
                + orderType
                + " allowType="
                + allowType
                + " "
                + selectionDetail);

            return !CaptureReplicationRegionOrderIntent(
                orderType,
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                allowType,
                "None",
                "None",
                "selection:" + methodName);
        }

        private static void ReplicationRegionAreaOrderSetterPrefix(MethodBase __originalMethod, object __0)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return;
            }

            replicationLastAreaOrderType = __0?.ToString() ?? string.Empty;
            replicationLastAreaOrderRealtime = Time.realtimeSinceStartup;
            instance?.LogReplicationInfo("Going Cooperative replication region area type mode="
                + replicationConfigRegionCommandMode
                + " method="
                + (__originalMethod?.Name ?? string.Empty)
                + " areaType="
                + replicationLastAreaOrderType
                + FormatReplicationLastRegionSelection());
        }

        private static bool ReplicationRegionAreaOrderPrefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return true;
            }

            var methodName = __originalMethod?.Name ?? string.Empty;
            if (string.Equals(methodName, "OnModifyZone", StringComparison.Ordinal))
            {
                // This opens the vanilla drag-selection UI. The committed operation is
                // captured once at ZoneSelectionFinished.
                return true;
            }
            var areaType = ResolveReplicationAreaOrderType(__instance, methodName, __args);
            var subType = ResolveReplicationAreaOrderSubType(__instance, methodName, __args);
            if (!TryResolveReplicationSelectionRegion(
                    __instance,
                    out var startX,
                    out var startY,
                    out var startZ,
                    out var endX,
                    out var endY,
                    out var endZ,
                    out var selectionDetail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication region area order capture skipped method="
                    + methodName
                    + " areaType="
                    + areaType
                    + " subType="
                    + subType
                    + " reason="
                    + selectionDetail);
                return true;
            }

            var orderType = ResolveReplicationAreaOrderCommandType(methodName, areaType);
            if (string.Equals(methodName, "ZoneSelectionFinished", StringComparison.Ordinal)
                && IsReplicationZoneModifyActive())
            {
                orderType = replicationZoneModifyOperation;
                areaType = "StockpileModify";
                subType = replicationZoneModifyId;
            }
            instance?.LogReplicationInfo("Going Cooperative replication region area order mode="
                + replicationConfigRegionCommandMode
                + " method="
                + methodName
                + " orderType="
                + orderType
                + " areaType="
                + areaType
                + " subType="
                + subType
                + " "
                + selectionDetail);

            if (replicationConfigHostMode)
            {
                return true;
            }

            if (IsReplicationStockpileAreaOrder(orderType)
                && !IsReplicationCommittedStockpileAreaMethod(methodName))
            {
                instance?.LogReplicationInfo("Going Cooperative replication region area order deferred method="
                    + methodName
                    + " orderType="
                    + orderType
                    + " areaType="
                    + areaType
                    + " subType="
                    + subType);
                return true;
            }

            var suppress = CaptureReplicationRegionOrderIntent(
                orderType,
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                "None",
                areaType,
                subType,
                "area:" + methodName);
            if (string.Equals(methodName, "ZoneSelectionFinished", StringComparison.Ordinal)
                && IsReplicationZoneModifyActive())
            {
                replicationZoneModifyOperation = string.Empty;
                replicationZoneModifyId = string.Empty;
                replicationZoneModifyRealtime = 0f;
            }

            return !suppress;
        }

        private static void ReplicationStockpileZoneModifyPrefix(MethodBase __originalMethod, object __instance)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return;
            }

            replicationZoneModifyOperation = string.Equals(__originalMethod?.Name, "ShrinkStockpile", StringComparison.Ordinal)
                ? "ShrinkZone"
                : "ExpandZone";
            replicationZoneModifyId = ResolveReplicationStockpileZoneId(__instance);
            replicationZoneModifyRealtime = Time.realtimeSinceStartup;
            instance?.LogReplicationInfo("Going Cooperative replication stockpile modify armed operation="
                + replicationZoneModifyOperation
                + " stockpileId="
                + (string.IsNullOrWhiteSpace(replicationZoneModifyId) ? "<none>" : replicationZoneModifyId));
        }

        private static bool ReplicationContextualObjectActionPrefix(MethodBase __originalMethod, object __0)
        {
            if (!ShouldObserveReplicationRegionCommands() || __0 == null)
            {
                return true;
            }

            if (!TryResolveReplicationContextualObjectGrid(__0, out var x, out var y, out var z, out var positionDetail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication contextual action target unresolved method="
                    + (__originalMethod?.Name ?? string.Empty)
                    + " targetType="
                    + (__0.GetType().FullName ?? __0.GetType().Name)
                    + " detail="
                    + positionDetail);
                return true;
            }

            var methodName = __originalMethod?.Name ?? string.Empty;
            var orderType = methodName.IndexOf("Deconstruct", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Deconstruct"
                : "Chopping";
            instance?.LogReplicationInfo("Going Cooperative replication contextual action captured method="
                + methodName
                + " orderType="
                + orderType
                + " targetType="
                + (__0.GetType().FullName ?? __0.GetType().Name)
                + " grid=Vec3Int("
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + ") detail="
                + positionDetail);

            return !CaptureReplicationRegionOrderIntent(
                orderType,
                x,
                y,
                z,
                x,
                y,
                z,
                "None",
                "ContextualObject",
                __0.GetType().FullName ?? __0.GetType().Name,
                "contextual:" + methodName);
        }

        private static bool ReplicationContextualResourceGiveOrderPrefix(MethodBase __originalMethod, object __instance, object __0)
        {
            if (!ShouldObserveReplicationRegionCommands() || __instance == null || __0 == null)
            {
                return true;
            }

            if (!TryResolveReplicationContextualObjectGrid(__instance, out var x, out var y, out var z, out var positionDetail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication contextual resource target unresolved type="
                    + (__instance.GetType().FullName ?? __instance.GetType().Name)
                    + " orderType="
                    + __0
                    + " detail="
                    + positionDetail);
                return true;
            }

            var orderType = NormalizeReplicationContextualResourceOrderType(__0.ToString() ?? string.Empty);
            instance?.LogReplicationInfo("Going Cooperative replication contextual resource captured method="
                + (__originalMethod?.Name ?? string.Empty)
                + " orderType="
                + orderType
                + " nativeOrderType="
                + __0
                + " targetType="
                + (__instance.GetType().FullName ?? __instance.GetType().Name)
                + " grid=Vec3Int("
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + ") detail="
                + positionDetail);

            return !CaptureReplicationRegionOrderIntent(
                orderType,
                x,
                y,
                z,
                x,
                y,
                z,
                "None",
                "ContextualResource",
                __instance.GetType().FullName ?? __instance.GetType().Name,
                "contextual-resource:GiveOrder");
        }

        private static bool ReplicationInfoPanelActionPrefix(object __instance)
        {
            if (!ShouldObserveReplicationRegionCommands() || __instance == null)
            {
                return true;
            }

            try
            {
                var actionType = __instance.GetType();
                var getCurrentData = AccessTools.Method(actionType, "GetCurrentActionData", Type.EmptyTypes);
                var actionData = getCurrentData?.Invoke(__instance, null);
                var actionId = actionData == null
                    ? string.Empty
                    : (AccessTools.Method(actionData.GetType(), "GetID", Type.EmptyTypes)?.Invoke(actionData, null)?.ToString() ?? string.Empty);
                if (!TryNormalizeReplicationInfoPanelOrderType(actionId, out var orderType))
                {
                    return true;
                }

                var objectActions = AccessTools.Property(actionType, "ObjectActions")?.GetValue(__instance, null)
                    ?? AccessTools.Field(actionType, "<ObjectActions>k__BackingField")?.GetValue(__instance);
                var currentIndexValue = AccessTools.Property(actionType, "CurrentIndex")?.GetValue(__instance, null)
                    ?? AccessTools.Field(actionType, "<CurrentIndex>k__BackingField")?.GetValue(__instance);
                var currentIndex = currentIndexValue == null
                    ? 0
                    : Convert.ToInt32(currentIndexValue, CultureInfo.InvariantCulture);
                if (!(objectActions is Array actionArray)
                    || currentIndex < 0
                    || currentIndex >= actionArray.Length)
                {
                    instance?.LogReplicationWarning("Going Cooperative replication info-panel action target unavailable actionId="
                        + actionId
                        + " reason=action-array-or-index");
                    return true;
                }

                var pair = actionArray.GetValue(currentIndex);
                var actionDelegate = pair == null
                    ? null
                    : AccessTools.Property(pair.GetType(), "Value")?.GetValue(pair, null) as Delegate;
                var delegateTarget = actionDelegate?.Target;
                if (!TryResolveReplicationInfoPanelDelegateTarget(delegateTarget, out var target, out var targetDetail)
                    || target == null
                    || !TryResolveReplicationContextualObjectGrid(target, out var x, out var y, out var z, out var positionDetail))
                {
                    instance?.LogReplicationWarning("Going Cooperative replication info-panel action target unresolved actionId="
                        + actionId
                        + " detail="
                        + targetDetail);
                    return true;
                }

                instance?.LogReplicationInfo("Going Cooperative replication info-panel action captured actionId="
                    + actionId
                    + " orderType="
                    + orderType
                    + " targetType="
                    + (target.GetType().FullName ?? target.GetType().Name)
                    + " grid=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ") targetDetail="
                    + targetDetail
                    + " positionDetail="
                    + positionDetail);

                return !CaptureReplicationRegionOrderIntent(
                    orderType,
                    x,
                    y,
                    z,
                    x,
                    y,
                    z,
                    "None",
                    "InfoPanelAction",
                    actionId,
                    "info-panel:" + actionId);
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative replication info-panel action capture failed "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return true;
            }
        }

        private static bool TryNormalizeReplicationInfoPanelOrderType(string actionId, out string orderType)
        {
            orderType = string.Empty;
            if (string.Equals(actionId, "Chopping", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionId, "CutAllVegetation", StringComparison.OrdinalIgnoreCase))
            {
                orderType = string.Equals(actionId, "CutAllVegetation", StringComparison.OrdinalIgnoreCase)
                    ? "CutAllVegetation"
                    : "Chopping";
                return true;
            }

            if (string.Equals(actionId, "Harvesting", StringComparison.OrdinalIgnoreCase))
            {
                orderType = "Harvesting";
                return true;
            }

            if (string.Equals(actionId, "Cancel", StringComparison.OrdinalIgnoreCase))
            {
                orderType = "Cancel";
                return true;
            }

            if (string.Equals(actionId, "Forbid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionId, "Allow", StringComparison.OrdinalIgnoreCase))
            {
                orderType = string.Equals(actionId, "Forbid", StringComparison.OrdinalIgnoreCase)
                    ? "Forbid"
                    : "Allow";
                return true;
            }

            if (string.Equals(actionId, "UrgentHaul", StringComparison.OrdinalIgnoreCase))
            {
                orderType = "UrgentHaul";
                return true;
            }

            if (actionId.IndexOf("Deconstruct", StringComparison.OrdinalIgnoreCase) >= 0
                || actionId.IndexOf("Demol", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                orderType = "Deconstruct";
                return true;
            }

            return false;
        }

        private static bool TryResolveReplicationInfoPanelDelegateTarget(
            object? delegateTarget,
            out object? target,
            out string detail)
        {
            target = null;
            if (delegateTarget == null)
            {
                detail = "delegate-target-null";
                return false;
            }

            if (delegateTarget is Component)
            {
                target = delegateTarget;
                detail = "delegate-target-component";
                return true;
            }

            var fields = delegateTarget.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var i = 0; i < fields.Length; i++)
            {
                object? value;
                try
                {
                    value = fields[i].GetValue(delegateTarget);
                }
                catch
                {
                    continue;
                }

                if (value is Component)
                {
                    target = value;
                    detail = "delegate-field=" + fields[i].Name;
                    return true;
                }

                if (value != null && TryReadReplicationWorldObjectGridPosition(value, out _, out _, out _))
                {
                    target = value;
                    detail = "delegate-grid-field=" + fields[i].Name;
                    return true;
                }
            }

            detail = "delegate-fields-no-target type=" + (delegateTarget.GetType().FullName ?? delegateTarget.GetType().Name);
            return false;
        }

        private static bool ReplicationContextualBuildingDeconstructPrefix(object __instance, object __0)
        {
            if (!ShouldObserveReplicationRegionCommands() || __instance == null)
            {
                return true;
            }

            if (!TryResolveReplicationContextualObjectGrid(__instance, out var x, out var y, out var z, out var positionDetail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication contextual building target unresolved type="
                    + (__instance.GetType().FullName ?? __instance.GetType().Name)
                    + " detail="
                    + positionDetail);
                return true;
            }

            instance?.LogReplicationInfo("Going Cooperative replication contextual building captured orderType=Deconstruct nativeOrderType="
                + (__0?.ToString() ?? string.Empty)
                + " targetType="
                + (__instance.GetType().FullName ?? __instance.GetType().Name)
                + " grid=Vec3Int("
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + ") detail="
                + positionDetail);

            return !CaptureReplicationRegionOrderIntent(
                "Deconstruct",
                x,
                y,
                z,
                x,
                y,
                z,
                "None",
                "ContextualBuilding",
                __0?.ToString() ?? string.Empty,
                "contextual-building:MarkForDeconstruction");
        }

        private static string NormalizeReplicationContextualResourceOrderType(string nativeOrderType)
        {
            if (nativeOrderType.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Harvesting";
            }

            if (nativeOrderType.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Cancel";
            }

            if (nativeOrderType.IndexOf("Fish", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Fishing";
            }

            return "Chopping";
        }

        private int TryPatchReplicationContextualGiveOrder(Harmony harmonyInstance, HarmonyMethod prefix)
        {
            try
            {
                var genericType = AccessTools.TypeByName("NSMedieval.Views.Resources.MapResourceView`1");
                if (genericType == null)
                {
                    AppendPluginLog("Replication contextual GiveOrder type missing");
                    return 0;
                }

                var patched = new HashSet<MethodBase>();
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (var assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
                {
                    Type[] types;
                    try
                    {
                        types = assemblies[assemblyIndex].GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        var loaded = new List<Type>();
                        var candidates = ex.Types;
                        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
                        {
                            if (candidates[candidateIndex] != null)
                            {
                                loaded.Add(candidates[candidateIndex]!);
                            }
                        }

                        types = loaded.ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                    {
                        var concreteType = types[typeIndex];
                        if (concreteType == null || concreteType.ContainsGenericParameters)
                        {
                            continue;
                        }

                        var derivesFromMapResource = false;
                        for (var current = concreteType; current != null; current = current.BaseType)
                        {
                            if (current.IsGenericType && current.GetGenericTypeDefinition() == genericType)
                            {
                                derivesFromMapResource = true;
                                break;
                            }
                        }

                        if (!derivesFromMapResource)
                        {
                            continue;
                        }

                        MethodInfo? method = null;
                        var methods = concreteType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        for (var methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                        {
                            if (string.Equals(methods[methodIndex].Name, "GiveOrder", StringComparison.Ordinal)
                                && methods[methodIndex].GetParameters().Length == 1)
                            {
                                method = methods[methodIndex];
                                break;
                            }
                        }

                        if (method == null || !patched.Add(method))
                        {
                            continue;
                        }

                        try
                        {
                            harmonyInstance.Patch(method, prefix: prefix);
                            AppendPluginLog("Replication contextual GiveOrder prefix patched concrete="
                                + (concreteType.FullName ?? concreteType.Name)
                                + " declaring="
                                + (method.DeclaringType?.FullName ?? string.Empty));
                        }
                        catch (Exception ex)
                        {
                            patched.Remove(method);
                            AppendPluginLog("Replication contextual GiveOrder concrete patch failed type="
                                + (concreteType.FullName ?? concreteType.Name)
                                + " "
                                + ex.GetType().Name
                                + " "
                                + ex.Message);
                        }
                    }
                }

                if (patched.Count == 0)
                {
                    AppendPluginLog("Replication contextual GiveOrder closed methods missing");
                }

                return patched.Count;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication contextual GiveOrder patch failed "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return 0;
            }
        }

        private static bool TryResolveReplicationContextualObjectGrid(
            object target,
            out int x,
            out int y,
            out int z,
            out string detail)
        {
            if (TryReadReplicationWorldObjectGridPosition(target, out x, out y, out z))
            {
                detail = "source=target-grid-member";
                return true;
            }

            // Building views are rendered in Unity world space with the map's
            // vertical offset applied (for example world Y=15 for map Y=5).
            // Region-order APIs expect the authoritative building grid position,
            // so never fall through to the view transform when an instance exists.
            if (TryResolveReplicationBuildingCandidateInstance(target, out var buildingInstance, out var instanceDetail)
                && buildingInstance != null)
            {
                if (TryReadReplicationWorldObjectGridPosition(buildingInstance, out x, out y, out z))
                {
                    detail = "source=building-instance-grid " + instanceDetail;
                    return true;
                }

                if (TryInvokeReplicationObjectMethod(buildingInstance, "GetGridPosition", out var gridPosition)
                    && gridPosition != null
                    && TryReadVec3IntLikeValue(gridPosition, out x, out y, out z))
                {
                    detail = "source=building-instance-GetGridPosition " + instanceDetail;
                    return true;
                }
            }

            if (target is Component component)
            {
                var position = component.transform.position;
                x = (int)Math.Round(position.x);
                // Region-order event payloads consume selection/world-space Y.
                // World-object snapshots use map-space Y, but converting 15 -> 5
                // here silently targets a different layer in PlantOrderEventData.
                y = (int)Math.Round(position.y);
                z = (int)Math.Round(position.z);
                detail = "source=component-transform world=("
                    + position.x.ToString("0.###", CultureInfo.InvariantCulture)
                    + ","
                    + position.y.ToString("0.###", CultureInfo.InvariantCulture)
                    + ","
                    + position.z.ToString("0.###", CultureInfo.InvariantCulture)
                    + ")";
                return true;
            }

            x = 0;
            y = 0;
            z = 0;
            detail = "no-grid-or-component";
            return false;
        }

        private int TryPatchReplicationContextualClosure(Harmony harmonyInstance, HarmonyMethod prefix, string methodName)
        {
            try
            {
                var closureType = AccessTools.TypeByName("NSMedieval.Manager.GlobalKeybindingManager+<>c");
                if (closureType == null)
                {
                    AppendPluginLog("Replication contextual closure type missing method=" + methodName);
                    return 0;
                }

                var method = closureType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    AppendPluginLog("Replication contextual closure method missing: " + methodName);
                    return 0;
                }

                harmonyInstance.Patch(method, prefix: prefix);
                AppendPluginLog("Replication contextual closure prefix patched: "
                    + (closureType.FullName ?? closureType.Name)
                    + "."
                    + methodName);
                return 1;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication contextual closure patch failed method="
                    + methodName
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return 0;
            }
        }

        private static bool IsReplicationZoneModifyActive()
        {
            return !string.IsNullOrEmpty(replicationZoneModifyOperation)
                && Time.realtimeSinceStartup - replicationZoneModifyRealtime < 30f;
        }

        private static string ResolveReplicationStockpileZoneId(object view)
        {
            object? stockpile = null;
            TryReadInstanceMemberValue(view, "StockpileInstance", out stockpile);
            if (stockpile == null)
            {
                TryReadInstanceMemberValue(view, "stockpileInstance", out stockpile);
            }

            if (stockpile != null)
            {
                var names = new[] { "UniqueId", "UniqueID", "ID", "Id", "id", "Guid", "guid" };
                for (var i = 0; i < names.Length; i++)
                {
                    if (TryReadInstanceMemberValue(stockpile, names[i], out var value)
                        && value != null
                        && !string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        return value.ToString() ?? string.Empty;
                    }
                }
            }

            return view is UnityEngine.Object unityObject
                ? unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static void ReplicationRegionStockpileSpawnPostfix(MethodBase __originalMethod, object __0, object __1, object __2)
        {
            if (!ShouldObserveReplicationRegionCommands()
                || !replicationConfigHostMode
                || !TryReadReplicationVec3Int(__1, out var startX, out var startY, out var startZ)
                || !TryReadReplicationVec3Int(__2, out var endX, out var endY, out var endZ))
            {
                return;
            }

            var subType = ResolveReplicationStockpileBlueprintId(__0);
            instance?.SendReplicationRegionOrderState(
                "Stockpile",
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                "None",
                "Stockpile",
                subType,
                "host-local source=apply:" + (__originalMethod?.Name ?? string.Empty));
        }

        private static bool ReplicationRegionDigPrefix(MethodBase __originalMethod, object __0, object __1)
        {
            if (!ShouldObserveReplicationRegionCommands()
                || !TryReadReplicationVec3Int(__0, out var startX, out var startY, out var startZ)
                || !TryReadReplicationVec3Int(__1, out var endX, out var endY, out var endZ))
            {
                return true;
            }

            return !CaptureReplicationRegionOrderIntent(
                "Digging",
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                "None",
                "None",
                __originalMethod.Name,
                "apply:" + __originalMethod.Name);
        }

        private static bool ReplicationRegionResourcePrefix(MethodBase __originalMethod, object __0)
        {
            if (!ShouldObserveReplicationRegionCommands()
                || IsReplicationZoneModifyActive()
                || !TryReadReplicationVec3Int(__0, out var x, out var y, out var z))
            {
                return true;
            }

            if (IsReplicationStockpileAreaSelectionActive())
            {
                return true;
            }

            var orderType = IsReplicationRecentRegionOrder("CutAllVegetation")
                ? "CutAllVegetation"
                : IsReplicationRecentRegionOrder("Harvesting")
                    ? "Harvesting"
                    : "Chopping";

            if (ShouldSuppressReplicationForceResourceCapture(orderType, x, y, z, x, y, z))
            {
                return true;
            }

            return !CaptureReplicationRegionOrderIntent(
                orderType,
                x,
                y,
                z,
                x,
                y,
                z,
                "None",
                "None",
                "None",
                "apply:" + __originalMethod.Name);
        }

        private static void ReplicationRegionResourceSurfacePostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (!ShouldObserveReplicationRegionCommands())
            {
                return;
            }

            if (IsReplicationZoneModifyActive())
            {
                return;
            }

            var methodName = __originalMethod?.Name ?? string.Empty;
            var typeName = __originalMethod?.DeclaringType?.FullName ?? string.Empty;
            if (IsReplicationStockpileAreaSelectionActive()
                && string.Equals(methodName, "OnForceOrderOnResource", StringComparison.Ordinal))
            {
                return;
            }

            var orderType = ResolveReplicationResourceSurfaceOrderType(typeName, methodName);
            if (!replicationConfigHostMode && IsReplicationDigRegionOrder(orderType))
            {
                return;
            }

            if (!TryResolveReplicationResourceSurfaceRegion(
                    __instance,
                    __args,
                    out var startX,
                    out var startY,
                    out var startZ,
                    out var endX,
                    out var endY,
                    out var endZ,
                    out var detail))
            {
                instance?.LogReplicationInfo("Going Cooperative replication region resource surface mode="
                    + replicationConfigRegionCommandMode
                    + " method="
                    + typeName
                    + "."
                    + methodName
                    + " orderType="
                    + orderType
                    + " position=<none> detail="
                    + detail
                    + " "
                    + FormatCommandSurfaceArgs(__instance, __args ?? Array.Empty<object>()));
                return;
            }

            if (string.Equals(methodName, "OnForceOrderOnResource", StringComparison.Ordinal)
                && ShouldSuppressReplicationForceResourceCapture(orderType, startX, startY, startZ, endX, endY, endZ))
            {
                return;
            }

            instance?.LogReplicationInfo("Going Cooperative replication region resource surface mode="
                + replicationConfigRegionCommandMode
                + " method="
                + typeName
                + "."
                + methodName
                    + " orderType="
                    + orderType
                    + " start=Vec3Int("
                    + startX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + startY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + startZ.ToString(CultureInfo.InvariantCulture)
                    + ") end=Vec3Int("
                    + endX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + endY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + endZ.ToString(CultureInfo.InvariantCulture)
                    + ") detail="
                    + detail);

            if (replicationConfigHostMode)
            {
                instance?.SendReplicationRegionOrderState(
                    orderType,
                    startX,
                    startY,
                    startZ,
                    endX,
                    endY,
                    endZ,
                    "None",
                    "None",
                    "None",
                    "host-local source=resource:" + methodName + " detail=" + detail);
                return;
            }

            CaptureReplicationRegionOrderIntent(
                orderType,
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                "None",
                "None",
                "None",
                "resource:" + methodName);
        }

        private static bool CaptureReplicationRegionOrderIntent(
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
            string source)
        {
            if (IsReplicationCaptureModeOff(replicationConfigRegionCommandMode)
                || !ShouldSendReplicationLocalCommandIntent())
            {
                return false;
            }

            var command = new LockstepCommand(
                ReplicationClientPeerId,
                replicationIntentSequence + 1,
                0L,
                CommandKind.RegionOrder,
                LockstepCommandPayloads.CreateRegionOrderPayload(orderType, startX, startY, startZ, endX, endY, endZ, allowType, areaType, subType),
                null,
                startX,
                startY,
                startZ);

            if (ShouldSkipDuplicateReplicationLocalCommand(command))
            {
                return false;
            }

            if (IsReplicationPrimaryPlantRegionCapture(orderType, source))
            {
                MarkReplicationPrimaryPlantRegionCapture(orderType, startX, startY, startZ, endX, endY, endZ);
            }

            if (IsReplicationCaptureModeShadow(replicationConfigRegionCommandMode))
            {
                LogReplicationLocalCommandShadow(command, source);
                return false;
            }

            if (!IsReplicationCaptureModeSendEnabled(replicationConfigRegionCommandMode))
            {
                return false;
            }

            var sentCommand = new LockstepCommand(
                command.PlayerId,
                ++replicationIntentSequence,
                command.TargetTick,
                command.Kind,
                command.PayloadJson,
                command.TargetStableId,
                command.MapX,
                command.MapY,
                command.MapZ);

            SendReplicationLocalCommandIntent(sentCommand, source);

            return IsReplicationCaptureModeAuthoritative(replicationConfigRegionCommandMode)
                && IsReplicationRegionOrderAuthoritativeSupported(orderType);
        }

        private static bool ShouldObserveReplicationRegionCommands()
        {
            return !IsReplicationCaptureModeOff(replicationConfigRegionCommandMode)
                && replicationConfigEnabled
                && applyingRuntimeCommandDepth <= 0;
        }

        private static bool ShouldLogReplicationRegionSelection()
        {
            if (Time.realtimeSinceStartup < replicationNextRegionSelectionLogRealtime)
            {
                return false;
            }

            replicationNextRegionSelectionLogRealtime = Time.realtimeSinceStartup + 1f;
            return true;
        }

        private static bool IsReplicationRecentRegionOrder(string orderType)
        {
            return string.Equals(replicationLastRegionOrderType, orderType, StringComparison.Ordinal)
                && Time.realtimeSinceStartup - replicationLastRegionOrderRealtime < 3f;
        }

        private static bool IsReplicationStockpileAreaOrder(string orderType)
        {
            return string.Equals(orderType, "Stockpile", StringComparison.Ordinal);
        }

        private static bool IsReplicationCommittedStockpileAreaMethod(string methodName)
        {
            return string.Equals(methodName, "ZoneSelectionFinished", StringComparison.Ordinal);
        }

        private static bool IsReplicationStockpileAreaSelectionActive()
        {
            return string.Equals(replicationLastAreaOrderType, "Stockpile", StringComparison.Ordinal)
                && Time.realtimeSinceStartup - replicationLastAreaOrderRealtime < 10f;
        }

        private static bool ShouldSuppressReplicationForceResourceCapture(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ)
        {
            if (!IsReplicationPlantRegionOrder(orderType))
            {
                return false;
            }

            if (replicationLastPrimaryPlantRegionValid
                && Time.realtimeSinceStartup - replicationLastPrimaryPlantRegionRealtime <= 3f
                && IsReplicationCompatiblePlantOrder(replicationLastPrimaryPlantRegionOrderType, orderType)
                && IsReplicationRegionInsideBounds(
                    startX,
                    startZ,
                    endX,
                    endZ,
                    replicationLastPrimaryPlantRegionStartX,
                    replicationLastPrimaryPlantRegionStartZ,
                    replicationLastPrimaryPlantRegionEndX,
                    replicationLastPrimaryPlantRegionEndZ))
            {
                return true;
            }

            return replicationLastRegionSelectionValid
                && Time.realtimeSinceStartup - replicationLastRegionOrderRealtime <= 3f
                && IsReplicationCompatiblePlantOrder(replicationLastRegionOrderType, orderType)
                && IsReplicationRegionInsideBounds(
                    startX,
                    startZ,
                    endX,
                    endZ,
                    replicationLastRegionStartX,
                    replicationLastRegionStartZ,
                    replicationLastRegionEndX,
                    replicationLastRegionEndZ);
        }

        private static bool IsReplicationCompatiblePlantOrder(string capturedOrderType, string candidateOrderType)
        {
            return string.Equals(capturedOrderType, candidateOrderType, StringComparison.Ordinal)
                || (string.Equals(capturedOrderType, "CutAllVegetation", StringComparison.Ordinal)
                    && string.Equals(candidateOrderType, "Chopping", StringComparison.Ordinal));
        }

        private static bool IsReplicationRegionInsideBounds(
            int startX,
            int startZ,
            int endX,
            int endZ,
            int boundsStartX,
            int boundsStartZ,
            int boundsEndX,
            int boundsEndZ)
        {
            var boundsMinX = Math.Min(boundsStartX, boundsEndX);
            var boundsMaxX = Math.Max(boundsStartX, boundsEndX);
            var boundsMinZ = Math.Min(boundsStartZ, boundsEndZ);
            var boundsMaxZ = Math.Max(boundsStartZ, boundsEndZ);
            var minX = Math.Min(startX, endX);
            var maxX = Math.Max(startX, endX);
            var minZ = Math.Min(startZ, endZ);
            var maxZ = Math.Max(startZ, endZ);
            return minX >= boundsMinX
                && maxX <= boundsMaxX
                && minZ >= boundsMinZ
                && maxZ <= boundsMaxZ;
        }

        private static bool IsReplicationPrimaryPlantRegionCapture(string orderType, string source)
        {
            if (!IsReplicationPlantRegionOrder(orderType))
            {
                return false;
            }

            return source.StartsWith("selection:", StringComparison.Ordinal)
                || string.Equals(source, "resource:OnOrderChopEvent", StringComparison.Ordinal)
                || string.Equals(source, "resource:OnOrderResourceCollectionEventCallback", StringComparison.Ordinal);
        }

        private static void MarkReplicationPrimaryPlantRegionCapture(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ)
        {
            replicationLastPrimaryPlantRegionValid = true;
            replicationLastPrimaryPlantRegionOrderType = orderType ?? string.Empty;
            replicationLastPrimaryPlantRegionRealtime = Time.realtimeSinceStartup;
            replicationLastPrimaryPlantRegionStartX = startX;
            replicationLastPrimaryPlantRegionStartY = startY;
            replicationLastPrimaryPlantRegionStartZ = startZ;
            replicationLastPrimaryPlantRegionEndX = endX;
            replicationLastPrimaryPlantRegionEndY = endY;
            replicationLastPrimaryPlantRegionEndZ = endZ;
        }

        private static bool IsReplicationRegionOrderAuthoritativeSupported(string orderType)
        {
            return IsReplicationRegionOrderStateSupported(orderType);
        }

        private static string ResolveReplicationSelectionOrderType(object instance, string methodName)
        {
            if (TryReadSelectionOrderType(instance, out var orderType)
                && !string.IsNullOrEmpty(orderType))
            {
                return orderType;
            }

            if (!string.IsNullOrEmpty(replicationLastRegionOrderType)
                && Time.realtimeSinceStartup - replicationLastRegionOrderRealtime < 3f)
            {
                return replicationLastRegionOrderType;
            }

            switch (methodName)
            {
                case "OnChopOrder":
                    return "Chopping";
                case "OnOrderCancel":
                    return "Cancel";
                case "OnOrderDeconstruction":
                    return "Deconstruct";
                case "OnOrderAllowForbid":
                    return "AllowForbid";
                case "OnOrderUrgentHaul":
                    return "UrgentHaul";
                default:
                    return string.IsNullOrEmpty(methodName) ? "Unknown" : methodName;
            }
        }

        private static string ResolveReplicationAreaOrderCommandType(string methodName, string areaType)
        {
            if (string.Equals(areaType, "Stockpile", StringComparison.Ordinal)
                || methodName.IndexOf("Stockpile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Stockpile";
            }

            if (string.Equals(areaType, "Crops", StringComparison.Ordinal)
                || string.Equals(areaType, "CropField", StringComparison.Ordinal)
                || methodName.IndexOf("Crops", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Crops";
            }

            if (methodName.IndexOf("Expand", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ExpandZone";
            }

            if (methodName.IndexOf("Shrink", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ShrinkZone";
            }

            if (methodName.IndexOf("Modify", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ModifyZone";
            }

            return string.IsNullOrEmpty(areaType) ? "AreaOrder" : areaType;
        }

        private static string ResolveReplicationAreaOrderType(object instance, string methodName, object[] args)
        {
            if (args != null && args.Length > 0 && args[0] != null && args[0].GetType().IsEnum)
            {
                var value = args[0].ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(value))
                {
                    replicationLastAreaOrderType = value;
                    replicationLastAreaOrderRealtime = Time.realtimeSinceStartup;
                    return value;
                }
            }

            if (TryReadInstanceMemberValue(instance, "AreaOrderType", out var areaOrderType) && areaOrderType != null)
            {
                var value = areaOrderType.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            if (!string.IsNullOrEmpty(replicationLastAreaOrderType)
                && Time.realtimeSinceStartup - replicationLastAreaOrderRealtime < 10f)
            {
                return replicationLastAreaOrderType;
            }

            if (methodName.IndexOf("Stockpile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Stockpile";
            }

            if (methodName.IndexOf("Crops", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Crops";
            }

            return "Unknown";
        }

        private static string ResolveReplicationAreaOrderSubType(object instance, string methodName, object[] args)
        {
            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] is string text && !string.IsNullOrEmpty(text))
                    {
                        replicationLastAreaOrderSubType = text;
                        return text;
                    }
                }
            }

            if (methodName.IndexOf("Stockpile", StringComparison.OrdinalIgnoreCase) >= 0
                && TryResolveReplicationCurrentStockpileBlueprintId(instance, out var stockpileBlueprintId))
            {
                replicationLastAreaOrderSubType = stockpileBlueprintId;
                replicationLastAreaOrderRealtime = Time.realtimeSinceStartup;
                return stockpileBlueprintId;
            }

            if (!string.IsNullOrEmpty(replicationLastAreaOrderSubType)
                && Time.realtimeSinceStartup - replicationLastAreaOrderRealtime < 10f)
            {
                return replicationLastAreaOrderSubType;
            }

            return methodName.IndexOf("Stockpile", StringComparison.OrdinalIgnoreCase) >= 0 ? "stockpile" : "None";
        }

        private static string ResolveReplicationStockpileBlueprintId(object? stockpile)
        {
            if (stockpile == null)
            {
                return "stockpile";
            }

            if (stockpile is string text)
            {
                return string.IsNullOrWhiteSpace(text) ? "stockpile" : text.Trim();
            }

            var memberNames = new[] { "BlueprintId", "blueprintId", "ObjectId", "objectId", "Id", "id", "ID", "Name", "name" };
            for (var i = 0; i < memberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(stockpile, memberNames[i], out var value) && value != null)
                {
                    var candidateText = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(candidateText))
                    {
                        return candidateText.Trim();
                    }
                }
            }

            return stockpile.GetType().Name;
        }

        private static bool TryResolveReplicationCurrentStockpileBlueprintId(object instance, out string stockpileBlueprintId)
        {
            stockpileBlueprintId = string.Empty;
            if (instance == null)
            {
                return false;
            }

            object? uiController = null;
            try
            {
                uiController = AccessTools.Property(instance.GetType(), "UIController")?.GetValue(instance, null)
                    ?? AccessTools.Field(instance.GetType(), "uiController")?.GetValue(instance);
            }
            catch
            {
            }

            if (uiController == null)
            {
                return false;
            }

            object? stockpileBlueprint = null;
            try
            {
                stockpileBlueprint = AccessTools.Property(uiController.GetType(), "StockpileBlueprint")?.GetValue(uiController, null)
                    ?? AccessTools.Field(uiController.GetType(), "StockpileBlueprint")?.GetValue(uiController)
                    ?? AccessTools.Field(uiController.GetType(), "stockpileBlueprint")?.GetValue(uiController);
            }
            catch
            {
            }

            var resolved = ResolveReplicationStockpileBlueprintId(stockpileBlueprint);
            if (string.IsNullOrWhiteSpace(resolved) || string.Equals(resolved, "stockpile", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            stockpileBlueprintId = resolved;
            return true;
        }

        private static string ResolveReplicationResourceSurfaceOrderType(string typeName, string methodName)
        {
            if (typeName.IndexOf("PrioritiseHarvest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Harvesting";
            }

            if (typeName.IndexOf("PrioritiseChop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Chopping";
            }

            if (typeName.IndexOf("PrioritiseMine", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Digging";
            }

            if (IsReplicationRecentRegionOrder("Digging"))
            {
                return "Digging";
            }

            if (IsReplicationRecentRegionOrder("CutAllVegetation"))
            {
                return "CutAllVegetation";
            }

            if (IsReplicationRecentRegionOrder("Harvesting"))
            {
                return "Harvesting";
            }

            if (IsReplicationRecentRegionOrder("Chopping"))
            {
                return "Chopping";
            }

            if (methodName.IndexOf("Chop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Chopping";
            }

            if (methodName.IndexOf("Mine", StringComparison.OrdinalIgnoreCase) >= 0
                || methodName.IndexOf("Dig", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Digging";
            }

            if (methodName.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) >= 0
                || methodName.IndexOf("Harvest", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Harvesting";
            }

            return "Chopping";
        }

        private static bool TryResolveReplicationResourceSurfaceRegion(
            object instance,
            object[] args,
            out int startX,
            out int startY,
            out int startZ,
            out int endX,
            out int endY,
            out int endZ,
            out string detail)
        {
            if (replicationLastRegionSelectionValid)
            {
                startX = replicationLastRegionStartX;
                startY = replicationLastRegionStartY;
                startZ = replicationLastRegionStartZ;
                endX = replicationLastRegionEndX;
                endY = replicationLastRegionEndY;
                endZ = replicationLastRegionEndZ;
                detail = "source=last-selection" + FormatReplicationLastRegionSelection();
                return true;
            }

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (arg != null && TryReadVec3IntLikeValue(arg, out startX, out startY, out startZ))
                    {
                        endX = startX;
                        endY = startY;
                        endZ = startZ;
                        detail = "source=arg" + i.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }

                    if (arg != null && TryReadFirstVec3IntFromCollectionObject(arg, out startX, out startY, out startZ, out var collectionDetail))
                    {
                        endX = startX;
                        endY = startY;
                        endZ = startZ;
                        detail = "source=arg" + i.ToString(CultureInfo.InvariantCulture) + "-collection " + collectionDetail;
                        return true;
                    }
                }
            }

            if (instance != null && TryReadVec3IntLikeValue(instance, out startX, out startY, out startZ))
            {
                endX = startX;
                endY = startY;
                endZ = startZ;
                detail = "source=instance";
                return true;
            }

            var selectionDetail = "selection-owner-missing";
            if (instance != null
                && TryResolveReplicationSelectionRegion(
                    instance,
                    out startX,
                    out startY,
                    out startZ,
                    out endX,
                    out endY,
                    out endZ,
                    out selectionDetail))
            {
                detail = selectionDetail;
                return true;
            }

            startX = 0;
            startY = 0;
            startZ = 0;
            endX = 0;
            endY = 0;
            endZ = 0;
            detail = selectionDetail;
            return false;
        }

        private static string ResolveReplicationRegionAllowType(string orderType)
        {
            if (string.Equals(orderType, "Allow", StringComparison.Ordinal)
                || string.Equals(orderType, "Forbid", StringComparison.Ordinal)
                || string.Equals(orderType, "AllowForbid", StringComparison.Ordinal))
            {
                return orderType;
            }

            return "None";
        }

        private static string ResolveReplicationRegionAllowType(object instance, string orderType)
        {
            if (TryReadReplicationSelectionAllowType(instance, out var allowType))
            {
                return allowType;
            }

            return ResolveReplicationRegionAllowType(orderType);
        }

        private static bool TryReadReplicationSelectionAllowType(object instance, out string allowType)
        {
            allowType = string.Empty;
            if (instance == null)
            {
                return false;
            }

            var names = new[] { "OrderAllowType", "orderAllowType", "currentOrderAllowType", "selectedOrderAllowType" };
            for (var i = 0; i < names.Length; i++)
            {
                if (TryReadInstanceMemberValue(instance, names[i], out var value) && value != null)
                {
                    allowType = value.ToString() ?? string.Empty;
                    return !string.IsNullOrEmpty(allowType);
                }
            }

            return false;
        }

        private static bool TryResolveReplicationSelectionRegion(
            object owner,
            out int startX,
            out int startY,
            out int startZ,
            out int endX,
            out int endY,
            out int endZ,
            out string detail)
        {
            if (replicationLastRegionSelectionValid)
            {
                startX = replicationLastRegionStartX;
                startY = replicationLastRegionStartY;
                startZ = replicationLastRegionStartZ;
                endX = replicationLastRegionEndX;
                endY = replicationLastRegionEndY;
                endZ = replicationLastRegionEndZ;
                detail = "source=last-selection" + FormatReplicationLastRegionSelection();
                return true;
            }

            if (TryReadFirstSelectionVec3Int(owner, out startX, out startY, out startZ, out var fallbackDetail))
            {
                endX = startX;
                endY = startY;
                endZ = startZ;
                detail = "source=selection-member start=Vec3Int("
                    + startX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + startY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + startZ.ToString(CultureInfo.InvariantCulture)
                    + ") end=Vec3Int("
                    + endX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + endY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + endZ.ToString(CultureInfo.InvariantCulture)
                    + ") detail="
                    + fallbackDetail;
                return true;
            }

            endX = 0;
            endY = 0;
            endZ = 0;
            detail = "selection-read-failed detail=" + fallbackDetail;
            return false;
        }

        private static string FormatReplicationLastRegionSelection()
        {
            if (!replicationLastRegionSelectionValid)
            {
                return " selection=<none>";
            }

            return " start=Vec3Int("
                + replicationLastRegionStartX.ToString(CultureInfo.InvariantCulture)
                + ","
                + replicationLastRegionStartY.ToString(CultureInfo.InvariantCulture)
                + ","
                + replicationLastRegionStartZ.ToString(CultureInfo.InvariantCulture)
                + ") end=Vec3Int("
                + replicationLastRegionEndX.ToString(CultureInfo.InvariantCulture)
                + ","
                + replicationLastRegionEndY.ToString(CultureInfo.InvariantCulture)
                + ","
                + replicationLastRegionEndZ.ToString(CultureInfo.InvariantCulture)
                + ")";
        }

        private static bool TryReadReplicationVec3Int(object value, out int x, out int y, out int z)
        {
            var hasX = TryReadReplicationIntMember(value, "x", "X", out x);
            var hasY = TryReadReplicationIntMember(value, "y", "Y", out y);
            var hasZ = TryReadReplicationIntMember(value, "z", "Z", out z);
            return hasX && hasY && hasZ;
        }

        private static bool TryReadReplicationIntMember(object value, string lowerName, string upperName, out int number)
        {
            number = 0;
            if (value == null)
            {
                return false;
            }

            try
            {
                if ((!TryReadInstanceMemberValue(value, lowerName, out var memberValue) || memberValue == null)
                    && (!TryReadInstanceMemberValue(value, upperName, out memberValue) || memberValue == null))
                {
                    return false;
                }

                number = Convert.ToInt32(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private int TryPatchReplicationCommandCaptureMethodByTypeNames(
            Harmony harmonyInstance,
            HarmonyMethod patch,
            string typeName,
            string methodName,
            params string[] parameterTypeNames)
        {
            var parameterTypes = new Type[parameterTypeNames.Length];
            for (var i = 0; i < parameterTypeNames.Length; i++)
            {
                var parameterType = AccessTools.TypeByName(parameterTypeNames[i]);
                if (parameterType == null)
                {
                    AppendPluginLog("Replication command capture parameter type missing: "
                        + typeName
                        + "."
                        + methodName
                        + " parameter="
                        + parameterTypeNames[i]);
                    return 0;
                }

                parameterTypes[i] = parameterType;
            }

            return TryPatchReplicationCommandCaptureMethod(harmonyInstance, patch, typeName, methodName, parameterTypes);
        }

        private int TryPatchReplicationCommandCapturePrefixMethodByTypeNames(
            Harmony harmonyInstance,
            HarmonyMethod prefix,
            string typeName,
            string methodName,
            params string[] parameterTypeNames)
        {
            var parameterTypes = new Type[parameterTypeNames.Length];
            for (var i = 0; i < parameterTypeNames.Length; i++)
            {
                var parameterType = AccessTools.TypeByName(parameterTypeNames[i]);
                if (parameterType == null)
                {
                    AppendPluginLog("Replication command capture parameter type missing: "
                        + typeName
                        + "."
                        + methodName
                        + " parameter="
                        + parameterTypeNames[i]);
                    return 0;
                }

                parameterTypes[i] = parameterType;
            }

            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication command capture type missing: " + typeName);
                    return 0;
                }

                var method = AccessTools.Method(type, methodName, parameterTypes);
                if (method == null)
                {
                    AppendPluginLog("Replication command capture method missing: "
                        + typeName
                        + "."
                        + methodName);
                    return 0;
                }

                harmonyInstance.Patch(method, prefix: prefix);
                AppendPluginLog("Replication command capture prefix patched: "
                    + type.FullName
                    + "."
                    + method.Name);
                return 1;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication command capture prefix patch failed: "
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
