using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        bool IRuntimeCommandActions.ApplyPause(bool paused, out string detail)
        {
            return TryInvokeStoredGameSpeedManagerMethod(paused ? "SetSpeedPause" : "SetSpeedNormal", out detail);
        }
        bool IRuntimeCommandActions.ApplySpeedIndex(int speedIndex, string action, out string detail)
        {
            if (string.Equals(action, LockstepCommandPayloads.SetSpeedNormalAction, StringComparison.Ordinal))
            {
                return TryInvokeStoredGameSpeedManagerMethod("SetSpeedNormal", out detail);
            }

            switch (speedIndex)
            {
                case 0:
                    return TryInvokeStoredGameSpeedManagerMethod("SetSpeedPause", out detail);
                case 1:
                    return TryInvokeStoredGameSpeedManagerMethod("SetSpeedNormal", out detail);
                case 2:
                    return TryInvokeStoredGameSpeedManagerMethod("SetSpeedFast", out detail);
                case 3:
                    return TryInvokeStoredGameSpeedManagerMethod("SetSpeedFaster", out detail);
                default:
                    detail = "unsupported-speed-index=" + speedIndex.ToString(CultureInfo.InvariantCulture);
                    return false;
            }
        }
        bool IRuntimeCommandActions.ApplyDig(int startX, int startY, int startZ, int endX, int endY, int endZ, out string detail)
        {
            return TryInvokeGroundManagerOrderDigVoxel(startX, startY, startZ, endX, endY, endZ, out detail);
        }
        bool IRuntimeCommandActions.ApplyBuild(string blueprintId, int x, int y, int z, int angleY, string buildingType, string factionOwnership, bool afterLoading, out string detail)
        {
            if (replicationConfigEnabled)
            {
                var record = new ReplicationBuildPlacementRecord(
                    false,
                    x,
                    y,
                    z,
                    NormalizeReplicationBuildAngle(angleY),
                    1,
                    1,
                    1);
                return TryApplyReplicationBuildBatchAuthoritative(
                    blueprintId,
                    factionOwnership,
                    new List<ReplicationBuildPlacementRecord> { record },
                    out detail);
            }

            return TryInvokeBuildingPlacementPlaceBlueprint(blueprintId, x, y, z, angleY, out detail);
        }

        bool IRuntimeCommandActions.ApplyBuildBatch(
            string blueprintId,
            string buildingType,
            string factionOwnership,
            bool afterLoading,
            string[] placementRecords,
            out string detail)
        {
            if (!TryParseReplicationBuildPlacementRecords(placementRecords, out var records, out var parseDetail))
            {
                detail = parseDetail;
                return false;
            }

            if (!replicationConfigEnabled)
            {
                detail = "build-batch-requires-replication-runtime";
                return false;
            }

            return TryApplyReplicationBuildBatchAuthoritative(
                blueprintId,
                factionOwnership,
                records,
                out detail);
        }
        bool IRuntimeCommandActions.ApplyCutPlant(int uniqueId, string blueprintId, int x, int y, int z, int worldX, int worldY, int worldZ, out string detail)
        {
            return TryInvokePlantResourceManagerCutPlant(x, y, z, out detail);
        }
        private static bool TryInvokeStoredGameSpeedManagerMethod(string methodName, out string detail)
        {
            var target = gameSpeedManagerInstance;
            if (target == null)
            {
                TryCaptureGameSpeedManagerInstance("runtime-command-apply");
                target = gameSpeedManagerInstance;
            }

            if (target == null)
            {
                detail = "game-speed-manager-missing";
                return false;
            }

            try
            {
                var method = AccessTools.Method(target.GetType(), methodName, Type.EmptyTypes);
                if (method == null)
                {
                    detail = "method-missing";
                    return false;
                }

                method.Invoke(target, null);
                detail = "ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name;
                return false;
            }
        }
        private static bool TryInvokePlantResourceManagerCutPlant(int x, int y, int z, out string detail)
        {
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Manager.PlantResourceManager");
                if (managerType == null)
                {
                    detail = "plant-resource-manager-type-missing";
                    return false;
                }

                var target = plantResourceManagerInstance;
                if (target == null)
                {
                    var instanceProperty = AccessTools.Property(managerType, "Instance");
                    target = instanceProperty?.GetValue(null, null);
                }

                if (target == null)
                {
                    detail = "plant-resource-manager-missing";
                    return false;
                }

                if (!TryCreateVec3Int(x, y, z, out var position, out detail))
                {
                    return false;
                }

                var vec3IntType = position.GetType();
                var forceOrder = AccessTools.Method(managerType, "OnForceOrderOnResource", new[] { vec3IntType });
                if (forceOrder == null)
                {
                    detail = "on-force-order-on-resource-method-missing";
                    return false;
                }

                forceOrder.Invoke(target, new[] { position });
                detail = "ok via=OnForceOrderOnResource position=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokePlantResourceManagerRegionOrder(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            out string detail)
        {
            if (TryInvokePlantResourceManagerPlantOrderEvent(orderType, startX, startY, startZ, endX, endY, endZ, out detail))
            {
                return true;
            }

            var eventDetail = detail;
            var minX = Math.Min(startX, endX);
            var maxX = Math.Max(startX, endX);
            var minY = Math.Min(startY, endY);
            var maxY = Math.Max(startY, endY);
            var minZ = Math.Min(startZ, endZ);
            var maxZ = Math.Max(startZ, endZ);
            var attempted = 0;
            var applied = 0;
            var firstFailure = string.Empty;

            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    for (var z = minZ; z <= maxZ; z++)
                    {
                        attempted++;
                        if (attempted > 512)
                        {
                            detail = "plant-region-fallback-too-large orderType="
                                + (orderType ?? string.Empty)
                                + " attempted="
                                + attempted.ToString(CultureInfo.InvariantCulture)
                                + " eventDetail="
                                + eventDetail;
                            return applied > 0;
                        }

                        if (TryInvokePlantResourceManagerCutPlant(x, y, z, out var tileDetail))
                        {
                            applied++;
                        }
                        else if (firstFailure.Length == 0)
                        {
                            firstFailure = tileDetail;
                        }
                    }
                }
            }

            detail = "plant-region-fallback orderType="
                + (orderType ?? string.Empty)
                + " attempted="
                + attempted.ToString(CultureInfo.InvariantCulture)
                + " applied="
                + applied.ToString(CultureInfo.InvariantCulture)
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
                + ") firstFailure="
                + (firstFailure.Length == 0 ? "<none>" : firstFailure)
                + " eventDetail="
                + eventDetail;
            return applied > 0;
        }

        private static bool TryInvokePlantResourceManagerPlantOrderEvent(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            out string detail)
        {
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Manager.PlantResourceManager");
                var eventDataType = AccessTools.TypeByName("Managers.Selection.EventData.PlantOrderEventData");
                var orderTypeEnum = AccessTools.TypeByName("NSMedieval.Types.OrderType");
                var plantLifePhaseType = AccessTools.TypeByName("NSMedieval.Model.PlantLifePhaseType");
                var orderAllowType = AccessTools.TypeByName("NSMedieval.Types.OrderAllowType");
                if (managerType == null)
                {
                    detail = "plant-resource-manager-type-missing";
                    return false;
                }

                if (eventDataType == null)
                {
                    detail = "plant-order-event-data-type-missing";
                    return false;
                }

                if (orderTypeEnum == null || plantLifePhaseType == null || orderAllowType == null)
                {
                    detail = "plant-order-enum-type-missing";
                    return false;
                }

                var target = plantResourceManagerInstance;
                if (target == null)
                {
                    var instanceProperty = AccessTools.Property(managerType, "Instance");
                    target = instanceProperty?.GetValue(null, null);
                }

                if (target == null)
                {
                    detail = "plant-resource-manager-missing";
                    return false;
                }

                if (!TryParseEnumValue(orderTypeEnum, NormalizeReplicationPlantOrderType(orderType), out var orderTypeValue)
                    || orderTypeValue == null)
                {
                    detail = "plant-order-type-parse-failed orderType=" + (orderType ?? string.Empty);
                    return false;
                }

                if (!TryParseEnumValue(plantLifePhaseType, "None", out var plantLifePhaseValue) || plantLifePhaseValue == null)
                {
                    detail = "plant-life-phase-parse-failed";
                    return false;
                }

                if (!TryParseEnumValue(orderAllowType, "All", out var orderAllowValue) || orderAllowValue == null)
                {
                    detail = "order-allow-type-parse-failed";
                    return false;
                }

                var minPoint = new Vector2Int(Math.Min(startX, endX), Math.Min(startZ, endZ));
                var maxPoint = new Vector2Int(Math.Max(startX, endX), Math.Max(startZ, endZ));
                var worldSpaceY = (float)startY;
                var affectOnlyOneLayer = startY == endY;
                var constructor = AccessTools.Constructor(
                    eventDataType,
                    new[]
                    {
                        typeof(float),
                        typeof(Vector2Int),
                        typeof(Vector2Int),
                        orderTypeEnum,
                        typeof(bool),
                        plantLifePhaseType,
                        orderAllowType
                    });
                if (constructor == null)
                {
                    detail = "plant-order-event-data-constructor-missing";
                    return false;
                }

                var eventData = constructor.Invoke(
                    new[]
                    {
                        (object)worldSpaceY,
                        minPoint,
                        maxPoint,
                        orderTypeValue,
                        affectOnlyOneLayer,
                        plantLifePhaseValue,
                        orderAllowValue
                    });
                var method = AccessTools.Method(managerType, "OnOrderChopEvent", new[] { eventDataType });
                if (method == null)
                {
                    detail = "on-order-chop-event-method-missing";
                    return false;
                }

                method.Invoke(target, new[] { eventData });
                detail = "ok via=OnOrderChopEvent orderType="
                    + NormalizeReplicationPlantOrderType(orderType)
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
                    + ") minPoint=Vector2Int("
                    + minPoint.x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + minPoint.y.ToString(CultureInfo.InvariantCulture)
                    + ") maxPoint=Vector2Int("
                    + maxPoint.x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + maxPoint.y.ToString(CultureInfo.InvariantCulture)
                    + ") affectOnlyOneLayer="
                    + affectOnlyOneLayer;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string NormalizeReplicationPlantOrderType(string orderType)
        {
            if (string.Equals(orderType, "Deconstruct", StringComparison.Ordinal))
            {
                return "Deconstructing";
            }

            return orderType ?? string.Empty;
        }

        private static bool TryInvokeAllowForbidRegionOrder(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            out string detail)
        {
            try
            {
                var selectionManagerType = AccessTools.TypeByName("NSMedieval.Managers.Selection.SelectionManager");
                var eventDataType = AccessTools.TypeByName("Managers.Selection.EventData.OrderEventData");
                var orderTypeEnum = AccessTools.TypeByName("NSMedieval.Types.OrderType");
                var orderAllowTypeEnum = AccessTools.TypeByName("NSMedieval.Types.OrderAllowType");
                if (selectionManagerType == null)
                {
                    detail = "selection-manager-type-missing";
                    return false;
                }

                if (eventDataType == null)
                {
                    detail = "order-event-data-type-missing";
                    return false;
                }

                if (orderTypeEnum == null || orderAllowTypeEnum == null)
                {
                    detail = "allow-forbid-enum-type-missing";
                    return false;
                }

                var selectionManager = ResolveReplicationUnityManagerInstance(selectionManagerType);
                if (selectionManager == null)
                {
                    detail = "selection-manager-missing";
                    return false;
                }

                if (!TryParseEnumValue(orderTypeEnum, orderType, out var orderTypeValue) || orderTypeValue == null)
                {
                    detail = "allow-forbid-order-type-parse-failed orderType=" + (orderType ?? string.Empty);
                    return false;
                }

                var normalizedAllowType = NormalizeReplicationAllowForbidAllowType(allowType);
                if (!TryParseEnumValue(orderAllowTypeEnum, normalizedAllowType, out var orderAllowTypeValue) || orderAllowTypeValue == null)
                {
                    detail = "allow-forbid-allow-type-parse-failed allowType=" + (allowType ?? string.Empty);
                    return false;
                }

                var constructor = AccessTools.Constructor(
                    eventDataType,
                    new[] { typeof(float), typeof(Vector2Int), typeof(Vector2Int), orderTypeEnum, typeof(bool), orderAllowTypeEnum });
                if (constructor == null)
                {
                    detail = "order-event-data-constructor-missing";
                    return false;
                }

                var minPoint = new Vector2Int(Math.Min(startX, endX), Math.Min(startZ, endZ));
                var maxPoint = new Vector2Int(Math.Max(startX, endX), Math.Max(startZ, endZ));
                var affectOnlyOneLayer = startY == endY;
                var eventData = constructor.Invoke(
                    new[] { (object)(float)startY, minPoint, maxPoint, orderTypeValue, affectOnlyOneLayer, orderAllowTypeValue });

                var eventField = AccessTools.Field(selectionManagerType, "AllowOrForbidEvent");
                var allowOrForbidEvent = eventField?.GetValue(selectionManager);
                if (allowOrForbidEvent == null)
                {
                    detail = "allow-or-forbid-event-missing";
                    return false;
                }

                var invoke = AccessTools.Method(allowOrForbidEvent.GetType(), "Invoke", new[] { eventDataType })
                    ?? AccessTools.Method(allowOrForbidEvent.GetType(), "Invoke");
                if (invoke == null)
                {
                    detail = "allow-or-forbid-event-invoke-missing type=" + allowOrForbidEvent.GetType().FullName;
                    return false;
                }

                invoke.Invoke(allowOrForbidEvent, new[] { eventData });
                detail = "ok via=AllowOrForbidEvent orderType="
                    + orderType
                    + " allowType="
                    + normalizedAllowType
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
                    + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string NormalizeReplicationAllowForbidAllowType(string allowType)
        {
            if (string.Equals(allowType, "Piles", StringComparison.Ordinal)
                || string.Equals(allowType, "Blueprints", StringComparison.Ordinal)
                || string.Equals(allowType, "Foundations", StringComparison.Ordinal)
                || string.Equals(allowType, "All", StringComparison.Ordinal))
            {
                return allowType;
            }

            return "All";
        }

        private static bool TryInvokeStockpileSelectionManagerRegionOrderViaSelectionManager(
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string subType,
            out string detail)
        {
            try
            {
                var selectionManagerType = AccessTools.TypeByName("NSMedieval.Managers.Selection.SelectionManager");
                if (selectionManagerType == null)
                {
                    detail = "selection-manager-type-missing";
                    return false;
                }

                var selectionManager = ResolveReplicationUnityManagerInstance(selectionManagerType);
                if (selectionManager == null)
                {
                    detail = "selection-manager-missing";
                    return false;
                }

                if (!TryCreateVec3Int(startX, startY, startZ, out var start, out detail)
                    || !TryCreateVec3Int(endX, endY, endZ, out var end, out detail))
                {
                    return false;
                }

                var startField = AccessTools.Field(selectionManagerType, "startPoint");
                var endField = AccessTools.Field(selectionManagerType, "endPoint");
                if (startField == null || endField == null)
                {
                    detail = "selection-region-fields-missing";
                    return false;
                }

                var assign = AccessTools.Method(selectionManagerType, "OnAssignStockpileArea", Type.EmptyTypes)
                    ?? AccessTools.Method(selectionManagerType, "OnAssignStockpileArea");
                if (assign == null)
                {
                    detail = "on-assign-stockpile-area-method-missing";
                    return false;
                }

                var previousStart = startField.GetValue(selectionManager);
                var previousEnd = endField.GetValue(selectionManager);
                object? uiController = null;
                PropertyInfo? stockpileBlueprintProperty = null;
                object? previousBlueprint = null;
                var setBlueprint = false;

                try
                {
                    var uiControllerProperty = AccessTools.Property(selectionManagerType, "UIController");
                    uiController = uiControllerProperty?.GetValue(selectionManager, null)
                        ?? AccessTools.Field(selectionManagerType, "uiController")?.GetValue(selectionManager);

                    if (uiController != null
                        && !string.IsNullOrWhiteSpace(subType)
                        && !string.Equals(subType, "None", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(subType, "stockpile", StringComparison.OrdinalIgnoreCase))
                    {
                        stockpileBlueprintProperty = AccessTools.Property(uiController.GetType(), "StockpileBlueprint");
                        if (stockpileBlueprintProperty != null && stockpileBlueprintProperty.CanWrite)
                        {
                            previousBlueprint = stockpileBlueprintProperty.GetValue(uiController, null);
                            stockpileBlueprintProperty.SetValue(uiController, subType.Trim(), null);
                            setBlueprint = true;
                        }
                    }

                    startField.SetValue(selectionManager, start);
                    endField.SetValue(selectionManager, end);
                    assign.Invoke(selectionManager, null);
                }
                finally
                {
                    try
                    {
                        startField.SetValue(selectionManager, previousStart);
                        endField.SetValue(selectionManager, previousEnd);
                        if (setBlueprint && uiController != null && stockpileBlueprintProperty != null)
                        {
                            stockpileBlueprintProperty.SetValue(uiController, previousBlueprint, null);
                        }
                    }
                    catch
                    {
                    }
                }

                detail = "ok via=SelectionManager.OnAssignStockpileArea start=Vec3Int("
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
                    + ") subtype="
                    + (subType ?? string.Empty)
                    + " blueprintSet="
                    + setBlueprint;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeSelectionManagerRegionAction(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            out string detail)
        {
            try
            {
                var selectionManagerType = AccessTools.TypeByName("NSMedieval.Managers.Selection.SelectionManager");
                if (selectionManagerType == null)
                {
                    detail = "selection-manager-type-missing";
                    return false;
                }

                var selectionManager = ResolveReplicationUnityManagerInstance(selectionManagerType);
                if (selectionManager == null)
                {
                    detail = "selection-manager-missing";
                    return false;
                }

                var methodName = string.Equals(orderType, "Deconstruct", StringComparison.Ordinal)
                    ? "OnOrderDeconstruction"
                    : string.Equals(orderType, "Cancel", StringComparison.Ordinal)
                        ? "OnOrderCancel"
                        : string.Equals(orderType, "UrgentHaul", StringComparison.Ordinal)
                            ? "OnOrderUrgentHaul"
                            : string.Empty;
                var action = AccessTools.Method(selectionManagerType, methodName, Type.EmptyTypes);
                if (action == null)
                {
                    detail = "selection-action-method-missing orderType=" + (orderType ?? string.Empty) + " method=" + methodName;
                    return false;
                }

                if (!TryCreateVec3Int(startX, startY, startZ, out var start, out detail)
                    || !TryCreateVec3Int(endX, endY, endZ, out var end, out detail))
                {
                    return false;
                }

                var startField = AccessTools.Field(selectionManagerType, "startPoint");
                var endField = AccessTools.Field(selectionManagerType, "endPoint");
                if (startField == null || endField == null)
                {
                    detail = "selection-region-fields-missing orderType=" + (orderType ?? string.Empty);
                    return false;
                }

                var previousStart = startField.GetValue(selectionManager);
                var previousEnd = endField.GetValue(selectionManager);
                try
                {
                    startField.SetValue(selectionManager, start);
                    endField.SetValue(selectionManager, end);
                    action.Invoke(selectionManager, null);
                }
                finally
                {
                    try
                    {
                        startField.SetValue(selectionManager, previousStart);
                        endField.SetValue(selectionManager, previousEnd);
                    }
                    catch
                    {
                    }
                }

                detail = "ok via=SelectionManager."
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
                    + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeStockpileModifyRegionOrder(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string stockpileId,
            out string detail)
        {
            try
            {
                var stockpileManagerType = AccessTools.TypeByName("NSMedieval.Stockpiles.StockpileManager");
                var stockpileManager = stockpileManagerType == null ? null : ResolveReplicationUnityManagerInstance(stockpileManagerType);
                var armName = string.Equals(orderType, "ShrinkZone", StringComparison.Ordinal) ? "ShrinkZone" : "ExpandZone";
                if (stockpileManagerType == null || stockpileManager == null)
                {
                    detail = "stockpile-modify-manager-missing";
                    return false;
                }

                if (!TryResolveReplicationStockpileInstanceByObjectId(stockpileId, out var stockpileInstance, out var stockpileLookup)
                    || stockpileInstance == null)
                {
                    detail = "stockpile-modify-target-missing stockpileId=" + (stockpileId ?? string.Empty) + " " + stockpileLookup;
                    return false;
                }

                if (!TryCreateVec3Int(startX, startY, startZ, out var start, out detail)
                    || !TryCreateVec3Int(endX, endY, endZ, out var end, out detail))
                {
                    return false;
                }

                var shrinking = string.Equals(orderType, "ShrinkZone", StringComparison.Ordinal);
                var validName = shrinking ? "GetShrinkValidPositions" : "GetExpandValidPositions";
                var mutateName = shrinking ? "ShrinkStockpile" : "ExpandStockpile";
                var getValid = AccessTools.Method(stockpileManagerType, validName, new[] { start.GetType(), end.GetType() });
                var validPositions = getValid?.Invoke(stockpileManager, new[] { start, end });
                MethodInfo? mutate = null;
                foreach (var candidate in stockpileManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (candidate.Name == mutateName && candidate.GetParameters().Length == 4)
                    {
                        mutate = candidate;
                        break;
                    }
                }
                if (getValid == null || validPositions == null || mutate == null)
                {
                    detail = "stockpile-modify-direct-surface-missing operation=" + armName;
                    return false;
                }
                mutate.Invoke(stockpileManager, new[] { stockpileInstance, start, end, validPositions });

                detail = "ok via=StockpileManager."
                    + mutateName
                    + " valid=" + (validPositions is ICollection collection ? collection.Count.ToString(CultureInfo.InvariantCulture) : "unknown")
                    + " stockpileId="
                    + (stockpileId ?? string.Empty)
                    + " target=" + stockpileLookup
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
                    + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeFishingRegionOrder(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            out string detail)
        {
            try
            {
                var viewType = AccessTools.TypeByName("NSMedieval.View.Resources.FishMapResourceView");
                if (viewType == null)
                {
                    detail = "fishing-view-type-missing";
                    return false;
                }

                var cancelling = string.Equals(orderType, "Cancel", StringComparison.Ordinal);
                var methodName = cancelling ? "CancelOrder" : "GiveOrder";
                var orderTypeEnum = AccessTools.TypeByName("NSMedieval.Types.OrderType");
                var method = orderTypeEnum == null ? null : FindReplicationMethodOnTypeOrBase(viewType, methodName, new[] { orderTypeEnum });
                if (method == null || method.GetParameters().Length != 1 || !method.GetParameters()[0].ParameterType.IsEnum)
                {
                    detail = "fishing-order-method-missing method=" + methodName;
                    return false;
                }

                var nativeName = cancelling ? "Fishing" : "Fishing";
                object nativeOrder;
                try { nativeOrder = Enum.Parse(method.GetParameters()[0].ParameterType, nativeName, true); }
                catch
                {
                    detail = "fishing-native-order-missing";
                    return false;
                }

                var minX = Math.Min(startX, endX);
                var maxX = Math.Max(startX, endX);
                var minY = Math.Min(startY, endY);
                var maxY = Math.Max(startY, endY);
                var minMapY = Math.Min(NormalizePossibleWorldY(startY), NormalizePossibleWorldY(endY));
                var maxMapY = Math.Max(NormalizePossibleWorldY(startY), NormalizePossibleWorldY(endY));
                var minZ = Math.Min(startZ, endZ);
                var maxZ = Math.Max(startZ, endZ);
                var views = UnityEngine.Object.FindObjectsOfType(viewType);
                var matched = 0;
                var invoked = 0;
                for (var i = 0; i < views.Length; i++)
                {
                    var view = views[i];
                    var resource = AccessTools.Property(view.GetType(), "ResourceInstance")?.GetValue(view, null)
                        ?? AccessTools.Field(view.GetType(), "resourceInstance")?.GetValue(view);
                    if (resource == null || !TryGetListMember(resource, "Positions", out var positions)) continue;
                    var inside = false;
                    for (var positionIndex = 0; positionIndex < positions.Count; positionIndex++)
                    {
                        var position = positions[positionIndex];
                        if (position != null && TryReadReplicationVec3Int(position, out var x, out var y, out var z)
                            && x >= minX && x <= maxX
                            && ((y >= minY && y <= maxY) || (y >= minMapY && y <= maxMapY))
                            && z >= minZ && z <= maxZ)
                        {
                            inside = true;
                            break;
                        }
                    }
                    if (!inside) continue;
                    matched++;
                    method.Invoke(view, new[] { nativeOrder });
                    invoked++;
                }

                detail = "ok fishing operation=" + (cancelling ? "cancel" : "designate")
                    + " matched=" + matched.ToString(CultureInfo.InvariantCulture)
                    + " invoked=" + invoked.ToString(CultureInfo.InvariantCulture)
                    + " region=Vec3Int(" + minX.ToString(CultureInfo.InvariantCulture) + "," + minY.ToString(CultureInfo.InvariantCulture) + "," + minZ.ToString(CultureInfo.InvariantCulture) + ")-Vec3Int("
                    + maxX.ToString(CultureInfo.InvariantCulture) + "," + maxY.ToString(CultureInfo.InvariantCulture) + "," + maxZ.ToString(CultureInfo.InvariantCulture) + ")";
                return invoked > 0;
            }
            catch (Exception ex)
            {
                detail = "fishing-order-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationStockpileInstanceByObjectId(string objectId, out object? stockpileInstance, out string detail)
        {
            stockpileInstance = null;
            var identityParts = (objectId ?? string.Empty).Split(new[] { '@' }, StringSplitOptions.None);
            var expectedId = identityParts[0];
            var hasAnchor = identityParts.Length == 2;
            var expectedAnchor = hasAnchor ? identityParts[1] : string.Empty;
            var viewType = AccessTools.TypeByName("NSMedieval.Stockpiles.StockpileView");
            if (viewType == null) { detail = "stockpile-view-type-missing"; return false; }
            var views = UnityEngine.Object.FindObjectsOfType(viewType);
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                var candidate = AccessTools.Property(view.GetType(), "StockpileInstance")?.GetValue(view, null)
                    ?? AccessTools.Field(view.GetType(), "stockpileInstance")?.GetValue(view);
                if (candidate == null) continue;
                var candidateId = Convert.ToString(AccessTools.Property(candidate.GetType(), "ObjectId")?.GetValue(candidate, null), CultureInfo.InvariantCulture) ?? string.Empty;
                var anchor = string.Empty;
                if (TryReadInstanceMemberValue(candidate, "Start", out var start) && start != null
                    && TryReadReplicationVec3Int(start, out var x, out var y, out var z))
                    anchor = x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + z.ToString(CultureInfo.InvariantCulture);
                if (string.Equals(candidateId, expectedId, StringComparison.Ordinal)
                    && (!hasAnchor || string.Equals(anchor, expectedAnchor, StringComparison.Ordinal)))
                {
                    stockpileInstance = candidate;
                    detail = "matched-objectId scanned=" + (i + 1).ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }
            detail = "stockpile-objectId-not-found scanned=" + views.Length.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryApplyReplicationContextualPileAction(
            string actionId,
            int x,
            int y,
            int z,
            out string detail)
        {
            var lookup = new ReplicationWorldObjectDelta(
                0,
                0f,
                "ContextualPileAction",
                0,
                string.Empty,
                x,
                NormalizePossibleWorldY(y),
                z,
                string.Empty);
            if (!TryFindReplicationResourcePile(lookup, out var pile, out var lookupDetail) || pile == null)
            {
                detail = "contextual-pile-missing actionId=" + (actionId ?? string.Empty) + " " + lookupDetail;
                return false;
            }

            try
            {
                object actionTarget = pile;
                var pileViewType = AccessTools.TypeByName("NSMedieval.Views.Resources.ResourcePileView");
                if (pileViewType != null)
                {
                    var views = UnityEngine.Object.FindObjectsOfType(pileViewType);
                    for (var viewIndex = 0; viewIndex < views.Length; viewIndex++)
                    {
                        var view = views[viewIndex];
                        var instanceValue = AccessTools.Property(view.GetType(), "ResourcePileInstance")?.GetValue(view, null)
                            ?? AccessTools.Field(view.GetType(), "resourcePileInstance")?.GetValue(view);
                        if (ReferenceEquals(instanceValue, pile))
                        {
                            actionTarget = view;
                            break;
                        }
                    }
                }

                var methodName = string.Equals(actionId, "UrgentHaul", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actionId, "Cancel", StringComparison.OrdinalIgnoreCase)
                        ? "UrgentHaulPile"
                        : "ForbidPile";
                var value = string.Equals(actionId, "Forbid", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(actionId, "UrgentHaul", StringComparison.OrdinalIgnoreCase);
                var method = AccessTools.Method(actionTarget.GetType(), methodName, new[] { typeof(bool) })
                    ?? FindCompatibleReplicationInstanceMethod(actionTarget.GetType(), methodName, typeof(bool));
                if (method == null)
                {
                    detail = "contextual-pile-method-missing actionId="
                        + (actionId ?? string.Empty)
                        + " method="
                        + methodName
                        + " pileType="
                        + (actionTarget.GetType().FullName ?? actionTarget.GetType().Name)
                        + " "
                        + lookupDetail;
                    return false;
                }

                method.Invoke(actionTarget, new object[] { value });
                detail = "ok contextual-pile actionId="
                    + actionId
                    + " method="
                    + methodName
                    + " value="
                    + value
                    + " "
                    + lookupDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "contextual-pile-action-failed actionId="
                    + (actionId ?? string.Empty)
                    + " "
                    + FormatReflectionExceptionDetail(ex)
                    + " "
                    + lookupDetail;
                return false;
            }
        }

        private static bool TryInvokeStockpileSelectionManagerRegionOrder(
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string subType,
            out string detail)
        {
            try
            {
                var selectionManagerType = AccessTools.TypeByName("NSMedieval.Managers.Selection.SelectionManager");
                var gridDataIndexToolsType = AccessTools.TypeByName("NSMedieval.Tools.GridDataIndexTools");
                var worldType = AccessTools.TypeByName("NSMedieval.World");
                var managerType = AccessTools.TypeByName("NSMedieval.Stockpiles.StockpileManager");
                var stockpileType = AccessTools.TypeByName("NSMedieval.Stockpiles.Stockpile");
                if (selectionManagerType == null)
                {
                    detail = "selection-manager-type-missing";
                    return false;
                }

                if (gridDataIndexToolsType == null)
                {
                    detail = "grid-data-index-tools-type-missing";
                    return false;
                }

                if (worldType == null)
                {
                    detail = "world-type-missing";
                    return false;
                }

                if (managerType == null)
                {
                    detail = "stockpile-manager-type-missing";
                    return false;
                }

                if (stockpileType == null)
                {
                    detail = "stockpile-type-missing";
                    return false;
                }

                var minX = Math.Min(startX, endX);
                var minY = Math.Min(startY, endY);
                var minZ = Math.Min(startZ, endZ);
                var maxX = Math.Max(startX, endX);
                var maxY = Math.Max(startY, endY);
                var maxZ = Math.Max(startZ, endZ);

                if (!TryCreateVec3Int(minX, minY, minZ, out var min, out detail)
                    || !TryCreateVec3Int(maxX, maxY, maxZ, out var max, out detail))
                {
                    return false;
                }

                var vec3IntType = min.GetType();
                var forbiddenEdge = AccessTools.Method(gridDataIndexToolsType, "IsForbiddenEdge", new[] { vec3IntType });
                if (forbiddenEdge == null)
                {
                    detail = "stockpile-rejected reason=forbidden-edge-method-missing";
                    return false;
                }

                var minForbiddenEdge = Convert.ToBoolean(forbiddenEdge.Invoke(null, new[] { min }), CultureInfo.InvariantCulture);
                var maxForbiddenEdge = Convert.ToBoolean(forbiddenEdge.Invoke(null, new[] { max }), CultureInfo.InvariantCulture);
                if (minForbiddenEdge || maxForbiddenEdge)
                {
                    detail = "stockpile-rejected reason=forbidden-edge min="
                        + minForbiddenEdge
                        + " max="
                        + maxForbiddenEdge;
                    return false;
                }

                var clamp = AccessTools.Method(selectionManagerType, "ClampPositionMapEdge");
                if (clamp == null)
                {
                    detail = "stockpile-rejected reason=clamp-method-missing";
                    return false;
                }

                var clampMinArgs = new[] { min };
                var clampMaxArgs = new[] { max };
                clamp.Invoke(null, clampMinArgs);
                clamp.Invoke(null, clampMaxArgs);
                min = clampMinArgs[0];
                max = clampMaxArgs[0];
                if (!TryReadReplicationVec3Int(min, out var clampedMinX, out var clampedMinY, out var clampedMinZ)
                    || !TryReadReplicationVec3Int(max, out var clampedMaxX, out var clampedMaxY, out var clampedMaxZ))
                {
                    detail = "stockpile-rejected reason=clamped-position-read-failed";
                    return false;
                }

                minX = Math.Min(clampedMinX, clampedMaxX);
                minY = Math.Min(clampedMinY, clampedMaxY);
                minZ = Math.Min(clampedMinZ, clampedMaxZ);
                maxX = Math.Max(clampedMinX, clampedMaxX);
                maxY = Math.Max(clampedMinY, clampedMaxY);
                maxZ = Math.Max(clampedMinZ, clampedMaxZ);
                if (!TryCreateVec3Int(minX, minY, minZ, out min, out detail)
                    || !TryCreateVec3Int(maxX, maxY, maxZ, out max, out detail))
                {
                    return false;
                }

                if (!TryReadReplicationStaticIntMember(worldType, "MapBlockHeight", out var mapBlockHeight) || mapBlockHeight <= 0)
                {
                    detail = "stockpile-rejected reason=map-block-height-missing";
                    return false;
                }

                var isTopLayer = AccessTools.Method(gridDataIndexToolsType, "IsTopLayer", new[] { typeof(int) });
                if (isTopLayer == null)
                {
                    detail = "stockpile-rejected reason=top-layer-method-missing";
                    return false;
                }

                var minLayer = minY / mapBlockHeight;
                var maxLayer = maxY / mapBlockHeight;
                var minTopLayer = Convert.ToBoolean(isTopLayer.Invoke(null, new object[] { minLayer }), CultureInfo.InvariantCulture);
                var maxTopLayer = Convert.ToBoolean(isTopLayer.Invoke(null, new object[] { maxLayer }), CultureInfo.InvariantCulture);
                if (minTopLayer || maxTopLayer)
                {
                    detail = "stockpile-rejected reason=top-layer minLayer="
                        + minLayer.ToString(CultureInfo.InvariantCulture)
                        + " maxLayer="
                        + maxLayer.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (!TryResolveReplicationStockpileBlueprintFromRepository(stockpileType, subType, out var stockpile, out var stockpileDetail)
                    || stockpile == null)
                {
                    detail = stockpileDetail;
                    return false;
                }

                var manager = ResolveReplicationUnityManagerInstance(managerType);
                if (manager == null)
                {
                    detail = "stockpile-manager-missing";
                    return false;
                }

                var method = AccessTools.Method(managerType, "SpawnStockpile", new[] { stockpileType, vec3IntType, vec3IntType });
                if (method == null)
                {
                    detail = "spawn-stockpile-method-missing";
                    return false;
                }

                method.Invoke(manager, new[] { stockpile, min, max });
                var clamped = minX != Math.Min(startX, endX)
                    || minY != Math.Min(startY, endY)
                    || minZ != Math.Min(startZ, endZ)
                    || maxX != Math.Max(startX, endX)
                    || maxY != Math.Max(startY, endY)
                    || maxZ != Math.Max(startZ, endZ);
                detail = "ok via=validated-SpawnStockpile stockpile="
                    + stockpileDetail
                    + " min=Vec3Int("
                    + minX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + minY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + minZ.ToString(CultureInfo.InvariantCulture)
                    + ") max=Vec3Int("
                    + maxX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + maxY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + maxZ.ToString(CultureInfo.InvariantCulture)
                    + ") clamped="
                    + clamped;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeStockpileManagerRegionOrder(
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string subType,
            out string detail)
        {
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Stockpiles.StockpileManager");
                var stockpileType = AccessTools.TypeByName("NSMedieval.Stockpiles.Stockpile");
                if (managerType == null)
                {
                    detail = "stockpile-manager-type-missing";
                    return false;
                }

                if (stockpileType == null)
                {
                    detail = "stockpile-type-missing";
                    return false;
                }

                var manager = ResolveReplicationUnityManagerInstance(managerType);
                if (manager == null)
                {
                    detail = "stockpile-manager-missing";
                    return false;
                }

                if (!TryResolveReplicationStockpileBlueprint(manager, stockpileType, subType, out var stockpile, out var stockpileDetail)
                    || stockpile == null)
                {
                    detail = stockpileDetail;
                    return false;
                }

                if (!TryCreateVec3Int(startX, startY, startZ, out var start, out detail)
                    || !TryCreateVec3Int(endX, endY, endZ, out var end, out detail))
                {
                    return false;
                }

                var method = AccessTools.Method(managerType, "SpawnStockpile", new[] { stockpileType, start.GetType(), end.GetType() });
                if (method == null)
                {
                    detail = "spawn-stockpile-method-missing";
                    return false;
                }

                method.Invoke(manager, new[] { stockpile, start, end });
                detail = "ok via=SpawnStockpile stockpile="
                    + stockpileDetail
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
                    + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static object? ResolveReplicationUnityManagerInstance(Type managerType)
        {
            var instanceProperty = AccessTools.Property(managerType, "Instance");
            try
            {
                var instance = instanceProperty?.GetValue(null, null);
                if (instance != null)
                {
                    return instance;
                }
            }
            catch
            {
            }

            try
            {
                var objects = UnityEngine.Object.FindObjectsOfType(managerType);
                return objects != null && objects.Length > 0 ? objects[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo? FindReplicationMethodOnTypeOrBase(Type type, string methodName, Type[] parameterTypes)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var method = AccessTools.Method(current, methodName, parameterTypes);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static bool TryReadReplicationStaticIntMember(Type type, string memberName, out int value)
        {
            value = 0;
            try
            {
                var field = AccessTools.Field(type, memberName);
                if (field != null)
                {
                    value = Convert.ToInt32(field.GetValue(null), CultureInfo.InvariantCulture);
                    return true;
                }

                var property = AccessTools.Property(type, memberName);
                if (property != null)
                {
                    value = Convert.ToInt32(property.GetValue(null, null), CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryResolveReplicationStockpileBlueprintFromRepository(
            Type stockpileType,
            string subType,
            out object? stockpile,
            out string detail)
        {
            stockpile = null;
            var blueprintId = (subType ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(blueprintId)
                || string.Equals(blueprintId, "None", StringComparison.OrdinalIgnoreCase)
                || string.Equals(blueprintId, "stockpile", StringComparison.OrdinalIgnoreCase))
            {
                detail = "stockpile-rejected reason=blueprint-missing id="
                    + (blueprintId.Length == 0 ? "<empty>" : blueprintId);
                return false;
            }

            try
            {
                var repositoryMarkerType = AccessTools.TypeByName("NSMedieval.Stockpiles.StockpileRepository");
                var repositoryDefinitionType = AccessTools.TypeByName("NSEipix.Repository.Repository`2");
                if (repositoryMarkerType == null || repositoryDefinitionType == null)
                {
                    detail = "stockpile-rejected reason=repository-type-missing id=" + blueprintId;
                    return false;
                }

                var repositoryType = repositoryDefinitionType.MakeGenericType(repositoryMarkerType, stockpileType);
                var instance = AccessTools.Property(repositoryType, "Instance")?.GetValue(null, null);
                if (instance == null)
                {
                    detail = "stockpile-rejected reason=repository-instance-missing id=" + blueprintId;
                    return false;
                }

                var getById = AccessTools.Method(repositoryType, "GetByID", new[] { typeof(string) })
                    ?? FindReplicationMethodOnTypeOrBase(repositoryType, "GetByID", new[] { typeof(string) });
                if (getById == null)
                {
                    detail = "stockpile-rejected reason=repository-getbyid-missing id=" + blueprintId;
                    return false;
                }

                stockpile = getById.Invoke(instance, new object[] { blueprintId });
                if (stockpile == null)
                {
                    detail = "stockpile-rejected reason=blueprint-missing id=" + blueprintId;
                    return false;
                }

                detail = "id=" + blueprintId;
                return true;
            }
            catch (Exception ex)
            {
                detail = "stockpile-rejected reason=repository-error id="
                    + blueprintId
                    + " "
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationStockpileBlueprint(object manager, Type stockpileType, string subType, out object? stockpile, out string detail)
        {
            stockpile = null;
            var desired = (subType ?? string.Empty).Trim();
            object? fallback = null;
            string fallbackDetail = string.Empty;

            try
            {
                if (TryResolveReplicationStockpileBlueprintFromGraph(
                        manager,
                        stockpileType,
                        desired,
                        maxDepth: 4,
                        out stockpile,
                        out detail,
                        ref fallback,
                        ref fallbackDetail))
                {
                    return true;
                }

                try
                {
                    var objects = Resources.FindObjectsOfTypeAll(stockpileType);
                    if (objects != null)
                    {
                        for (var i = 0; i < objects.Length; i++)
                        {
                            var candidate = objects[i];
                            if (candidate == null)
                            {
                                continue;
                            }

                            var candidateDetail = FormatReplicationStockpileBlueprintCandidate(candidate);
                            if (IsReplicationStockpileBlueprintMatch(candidate, desired))
                            {
                                stockpile = candidate;
                                detail = "matched subtype=" + desired + " source=resources " + candidateDetail;
                                return true;
                            }

                            if (fallback == null)
                            {
                                fallback = candidate;
                                fallbackDetail = "fallback source=resources " + candidateDetail;
                            }
                        }
                    }
                }
                catch
                {
                }

                if (fallback != null)
                {
                    stockpile = fallback;
                    detail = fallbackDetail + " requested=" + (desired.Length == 0 ? "<empty>" : desired);
                    return true;
                }

                detail = "stockpile-blueprint-missing subtype=" + (desired.Length == 0 ? "<empty>" : desired);
                return false;
            }
            catch (Exception ex)
            {
                detail = "stockpile-blueprint-resolve-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationStockpileBlueprintFromGraph(
            object? root,
            Type stockpileType,
            string desired,
            int maxDepth,
            out object? stockpile,
            out string detail,
            ref object? fallback,
            ref string fallbackDetail)
        {
            stockpile = null;
            detail = string.Empty;
            var visited = new HashSet<object>();
            return TryResolveReplicationStockpileBlueprintFromGraph(
                root,
                stockpileType,
                desired,
                maxDepth,
                visited,
                out stockpile,
                out detail,
                ref fallback,
                ref fallbackDetail);
        }

        private static bool TryResolveReplicationStockpileBlueprintFromGraph(
            object? value,
            Type stockpileType,
            string desired,
            int remainingDepth,
            HashSet<object> visited,
            out object? stockpile,
            out string detail,
            ref object? fallback,
            ref string fallbackDetail)
        {
            stockpile = null;
            detail = string.Empty;
            if (value == null || remainingDepth < 0)
            {
                return false;
            }

            var valueType = value.GetType();
            if (valueType.IsPrimitive || value is string || valueType.IsEnum)
            {
                return false;
            }

            if (!visited.Add(value))
            {
                return false;
            }

            if (stockpileType.IsInstanceOfType(value))
            {
                var candidateDetail = FormatReplicationStockpileBlueprintCandidate(value);
                if (IsReplicationStockpileBlueprintMatch(value, desired))
                {
                    stockpile = value;
                    detail = "matched subtype=" + desired + " source=manager-graph " + candidateDetail;
                    return true;
                }

                if (fallback == null)
                {
                    fallback = value;
                    fallbackDetail = "fallback source=manager-graph " + candidateDetail;
                }
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (TryResolveReplicationStockpileBlueprintFromGraph(entry.Value, stockpileType, desired, remainingDepth - 1, visited, out stockpile, out detail, ref fallback, ref fallbackDetail)
                        || TryResolveReplicationStockpileBlueprintFromGraph(entry.Key, stockpileType, desired, remainingDepth - 1, visited, out stockpile, out detail, ref fallback, ref fallbackDetail))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (TryResolveReplicationStockpileBlueprintFromGraph(item, stockpileType, desired, remainingDepth - 1, visited, out stockpile, out detail, ref fallback, ref fallbackDetail))
                    {
                        return true;
                    }
                }

                return false;
            }

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var fields = valueType.GetFields(Flags);
            for (var i = 0; i < fields.Length; i++)
            {
                object? fieldValue;
                try
                {
                    fieldValue = fields[i].GetValue(fields[i].IsStatic ? null : value);
                }
                catch
                {
                    continue;
                }

                if (TryResolveReplicationStockpileBlueprintFromGraph(fieldValue, stockpileType, desired, remainingDepth - 1, visited, out stockpile, out detail, ref fallback, ref fallbackDetail))
                {
                    return true;
                }
            }

            var properties = valueType.GetProperties(Flags);
            for (var i = 0; i < properties.Length; i++)
            {
                if (properties[i].GetIndexParameters().Length != 0)
                {
                    continue;
                }

                object? propertyValue;
                try
                {
                    var getter = properties[i].GetGetMethod(nonPublic: true);
                    propertyValue = getter == null ? null : getter.Invoke(getter.IsStatic ? null : value, null);
                }
                catch
                {
                    continue;
                }

                if (TryResolveReplicationStockpileBlueprintFromGraph(propertyValue, stockpileType, desired, remainingDepth - 1, visited, out stockpile, out detail, ref fallback, ref fallbackDetail))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReplicationStockpileBlueprintMatch(object candidate, string desired)
        {
            if (string.IsNullOrWhiteSpace(desired) || string.Equals(desired, "None", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (candidate is UnityEngine.Object unityObject
                && string.Equals(unityObject.name, desired, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var memberNames = new[] { "BlueprintId", "blueprintId", "ObjectId", "objectId", "Id", "id", "ID", "Name", "name" };
            for (var i = 0; i < memberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(candidate, memberNames[i], out var value) && value != null)
                {
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (string.Equals(text, desired, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string FormatReplicationStockpileBlueprintCandidate(object candidate)
        {
            var id = string.Empty;
            var memberNames = new[] { "BlueprintId", "blueprintId", "ObjectId", "objectId", "Id", "id", "ID", "Name", "name" };
            for (var i = 0; i < memberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(candidate, memberNames[i], out var value) && value != null)
                {
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        id = text.Trim();
                        break;
                    }
                }
            }

            return "type="
                + candidate.GetType().FullName
                + " id="
                + (id.Length == 0 ? "<unknown>" : id)
                + " name="
                + SafeUnityObjectName(candidate as UnityEngine.Object);
        }

        private static bool TryParseEnumValue(Type enumType, string value, out object? parsed)
        {
            parsed = null;
            if (enumType == null || !enumType.IsEnum || string.IsNullOrEmpty(value))
            {
                return false;
            }

            try
            {
                parsed = Enum.Parse(enumType, value, ignoreCase: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvokeGroundManagerOrderDigVoxel(int startX, int startY, int startZ, int endX, int endY, int endZ, out string detail)
        {
            const string CanPlaceDigMarkerAtKey = "NSMedieval.Terrain.GroundManager.CanPlaceDigMarkerAt";
            const string OnOrderChangedKey = "NSMedieval.Resources.ResourceCommonController.OnOrderChanged";

            try
            {
                var groundManagerType = AccessTools.TypeByName("NSMedieval.Terrain.GroundManager");
                var vec3IntType = AccessTools.TypeByName("NSMedieval.Vec3Int");
                if (groundManagerType == null)
                {
                    detail = "ground-manager-type-missing";
                    return false;
                }

                if (vec3IntType == null)
                {
                    detail = "vec3int-type-missing";
                    return false;
                }

                var target = groundManagerInstance;
                if (target == null)
                {
                    var instanceProperty = AccessTools.Property(groundManagerType, "Instance");
                    target = instanceProperty?.GetValue(null, null);
                }

                if (target == null)
                {
                    detail = "ground-manager-missing";
                    return false;
                }

                var constructor = AccessTools.Constructor(vec3IntType, new[] { typeof(int), typeof(int), typeof(int) });
                if (constructor == null)
                {
                    detail = "vec3int-constructor-missing";
                    return false;
                }

                var start = constructor.Invoke(new object[] { startX, startY, startZ });
                var end = constructor.Invoke(new object[] { endX, endY, endZ });
                var method = AccessTools.Method(groundManagerType, "OrderDigVoxel", new[] { vec3IntType, vec3IntType });
                if (method == null)
                {
                    detail = "order-dig-voxel-method-missing";
                    return false;
                }

                var canPlaceCountBefore = GetPassiveCommandSurfaceCount(CanPlaceDigMarkerAtKey);
                var orderChangedCountBefore = GetPassiveCommandSurfaceCount(OnOrderChangedKey);
                var coordinateDetail = "start=Vec3Int("
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
                    + ")";

                try
                {
                    method.Invoke(target, new[] { start, end });
                    detail = "ok " + coordinateDetail;
                    return true;
                }
                catch (Exception ex) when (ex is TargetInvocationException)
                {
                    var canPlaceCountAfter = GetPassiveCommandSurfaceCount(CanPlaceDigMarkerAtKey);
                    var orderChangedCountAfter = GetPassiveCommandSurfaceCount(OnOrderChangedKey);
                    var canPlaceDelta = canPlaceCountAfter - canPlaceCountBefore;
                    var orderChangedDelta = orderChangedCountAfter - orderChangedCountBefore;

                    if (orderChangedDelta > 0)
                    {
                        detail = "ok via=OrderDigVoxel-side-effect-after-exception "
                            + coordinateDetail
                            + " canPlaceDigMarkerDelta="
                            + canPlaceDelta.ToString(CultureInfo.InvariantCulture)
                            + " onOrderChangedDelta="
                            + orderChangedDelta.ToString(CultureInfo.InvariantCulture)
                            + " exception="
                            + FormatReflectionExceptionDetail(ex);
                        return true;
                    }

                    detail = FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }
        private static bool TryInvokeBuildingPlacementPlaceBlueprint(string blueprintId, int x, int y, int z, int angleY, out string detail)
        {
            try
            {
                var placementManagerType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingPlacementManager");
                var buildingsPoolType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsPool");
                var blueprintType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingBlueprint");
                var viewComponentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
                var relocateBuildingType = AccessTools.TypeByName("NSMedieval.MovableBuildings.RelocateBuilding");
                var vec3IntType = AccessTools.TypeByName("NSMedieval.Vec3Int");
                if (placementManagerType == null)
                {
                    detail = "building-placement-manager-type-missing";
                    return false;
                }

                if (buildingsPoolType == null)
                {
                    detail = "buildings-pool-type-missing";
                    return false;
                }

                if (blueprintType == null)
                {
                    detail = "base-building-blueprint-type-missing";
                    return false;
                }

                if (viewComponentType == null)
                {
                    detail = "base-building-view-component-type-missing";
                    return false;
                }

                if (relocateBuildingType == null)
                {
                    detail = "relocate-building-type-missing";
                    return false;
                }

                if (vec3IntType == null)
                {
                    detail = "vec3int-type-missing";
                    return false;
                }

                var placementManager = buildingPlacementManagerInstance ?? ResolveReplicationUnityManagerInstance(placementManagerType);
                if (placementManager == null)
                {
                    detail = "building-placement-manager-missing";
                    return false;
                }

                var buildingsPool = buildingsPoolInstance ?? ResolveReplicationUnityManagerInstance(buildingsPoolType);
                if (buildingsPool == null)
                {
                    detail = "buildings-pool-missing";
                    return false;
                }

                var getBuildableBase = AccessTools.Method(buildingsPoolType, "GetBuildableBase", new[] { typeof(string) });
                if (getBuildableBase == null)
                {
                    detail = "get-buildable-base-method-missing";
                    return false;
                }

                var blueprint = getBuildableBase.Invoke(buildingsPool, new object[] { blueprintId });
                if (blueprint == null)
                {
                    detail = "blueprint-missing id=" + blueprintId;
                    return false;
                }

                var constructor = AccessTools.Constructor(vec3IntType, new[] { typeof(int), typeof(int), typeof(int) });
                if (constructor == null)
                {
                    detail = "vec3int-constructor-missing";
                    return false;
                }

                var position = constructor.Invoke(new object[] { x, y, z });

                var initializeBuilding = AccessTools.Method(placementManagerType, "InitializeBuilding", new[] { typeof(string), relocateBuildingType });
                if (initializeBuilding == null)
                {
                    detail = "initialize-building-method-missing";
                    return false;
                }

                initializeBuilding.Invoke(placementManager, new object?[] { blueprintId, null });
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "baseBuildingBlueprint", blueprint);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "raycastGridCurrent", position);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "raycastGridStart", position);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "raycastGridPrevious", position);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "previewAngle", angleY);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "hasSelectedItem", true);

                var mouseUpSpawn = AccessTools.Method(placementManagerType, "MouseUpSpawnInitializeBuildings", new[] { typeof(int) });
                if (mouseUpSpawn != null)
                {
                    string placementCleanupDetail;
                    mouseUpSpawn.Invoke(placementManager, new object[] { angleY });
                    placementCleanupDetail = ClearReplicationBuildingPlacementState(placementManager, placementManagerType);
                    detail = "ok via=MouseUpSpawnInitializeBuildings blueprintId="
                        + blueprintId
                        + " position=Vec3Int("
                        + x.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + y.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + z.ToString(CultureInfo.InvariantCulture)
                        + ") angleY="
                        + angleY.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + placementCleanupDetail;
                    return true;
                }

                var spawnMethod = AccessTools.Method(placementManagerType, "SpawnBaseBuildingViewComponent", new[] { blueprintType, vec3IntType, typeof(int) });
                if (spawnMethod == null)
                {
                    detail = "spawn-base-building-view-component-method-missing";
                    return false;
                }

                var viewComponent = spawnMethod.Invoke(placementManager, new[] { blueprint, position, angleY });
                if (viewComponent == null)
                {
                    detail = "spawn-base-building-view-component-returned-null";
                    return false;
                }

                var objectPlacedMethod = AccessTools.Method(placementManagerType, "ObjectPlacedOnMap", new[] { viewComponentType });
                if (objectPlacedMethod == null)
                {
                    detail = "object-placed-on-map-method-missing";
                    return false;
                }

                objectPlacedMethod.Invoke(placementManager, new[] { viewComponent });
                detail = "ok blueprintId="
                    + blueprintId
                    + " position=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ") angleY="
                    + angleY.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string ClearReplicationBuildingPlacementState(object placementManager, Type placementManagerType)
        {
            var fields = 0;
            fields += SetInstanceFieldIfPresent(placementManager, placementManagerType, "hasSelectedItem", false) ? 1 : 0;
            fields += SetInstanceFieldIfPresent(placementManager, placementManagerType, "baseBuildingBlueprint", null) ? 1 : 0;
            fields += SetInstanceFieldIfPresent(placementManager, placementManagerType, "moveBuilding", null) ? 1 : 0;
            fields += SetInstanceFieldIfPresent(placementManager, placementManagerType, "cancelPlacement", false) ? 1 : 0;
            fields += SetInstanceFieldIfPresent(placementManager, placementManagerType, "buildingInstance", null) ? 1 : 0;

            var collections = 0;
            collections += ClearReplicationPlacementCollectionField(placementManager, placementManagerType, "buildingsDictionary") ? 1 : 0;
            collections += ClearReplicationPlacementCollectionField(placementManager, placementManagerType, "buildingsToAutoConstruct") ? 1 : 0;

            var methods = 0;
            var resetPreviewGrids = AccessTools.Method(placementManagerType, "ResetPreviewGrids", Type.EmptyTypes);
            if (TryInvokeNoArgPlacementCleanupMethod(placementManager, resetPreviewGrids))
            {
                methods++;
            }

            var clearPileToInstall = AccessTools.Method(placementManagerType, "ClearPileToInstall", Type.EmptyTypes);
            if (TryInvokeNoArgPlacementCleanupMethod(placementManager, clearPileToInstall))
            {
                methods++;
            }

            return "placementCleanup=fields:"
                + fields.ToString(CultureInfo.InvariantCulture)
                + ",collections:"
                + collections.ToString(CultureInfo.InvariantCulture)
                + ",methods:"
                + methods.ToString(CultureInfo.InvariantCulture);
        }

        private static bool ClearReplicationPlacementCollectionField(object placementManager, Type placementManagerType, string fieldName)
        {
            var field = AccessTools.Field(placementManagerType, fieldName);
            if (field == null)
            {
                return false;
            }

            try
            {
                var value = field.GetValue(placementManager);
                if (value is IDictionary dictionary)
                {
                    dictionary.Clear();
                    return true;
                }

                if (value is IList list)
                {
                    list.Clear();
                    return true;
                }

                var clearMethod = value == null ? null : AccessTools.Method(value.GetType(), "Clear", Type.EmptyTypes);
                if (clearMethod != null)
                {
                    clearMethod.Invoke(value, null);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryInvokeNoArgPlacementCleanupMethod(object placementManager, MethodInfo? method)
        {
            if (method == null)
            {
                return false;
            }

            try
            {
                method.Invoke(placementManager, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
