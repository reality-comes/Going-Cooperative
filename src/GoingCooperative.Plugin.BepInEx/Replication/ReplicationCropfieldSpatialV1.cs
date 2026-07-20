using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static bool TryApplyReplicationCropfieldDeconstruct(int x, int y, int z, out string detail)
        {
            detail = string.Empty;
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
                var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
                var gridDetail = string.Empty;
                if (managerType == null
                    || manager == null
                    || !TryCreateVec3Int(x, y, z, out var grid, out gridDetail))
                {
                    detail = "cropfield-deconstruct-manager-unavailable " + gridDetail;
                    return false;
                }

                object? cropfield = null;
                var byNodeField = AccessTools.Field(managerType, "cropfieldsByNodeId");
                var byNode = byNodeField?.GetValue(manager) as IDictionary;
                if (byNode != null && byNode.Contains(grid))
                {
                    cropfield = byNode[grid];
                }

                var coordinateFallbackCount = 0;
                object? coordinateFallback = null;
                if (cropfield == null
                    && TryReadInstanceMemberValue(manager, "cropfields", out var cropfieldsValue)
                    && cropfieldsValue is IEnumerable cropfields)
                {
                    foreach (var candidate in cropfields)
                    {
                        if (candidate == null
                            || !TryReadInstanceMemberValue(candidate, "Positions", out var positionsValue)
                            || !(positionsValue is IEnumerable positions)) continue;
                        foreach (var position in positions)
                        {
                            if (position != null
                                && TryReadReplicationVec3Int(position, out var positionX, out var positionY, out var positionZ)
                                && positionX == x
                                && positionZ == z
                                && (positionY == y || positionY * 3 == y || y * 3 == positionY))
                            {
                                coordinateFallback = candidate;
                                coordinateFallbackCount++;
                                break;
                            }
                        }
                    }
                }

                if (cropfield == null && coordinateFallbackCount == 1)
                {
                    cropfield = coordinateFallback;
                }

                if (cropfield == null)
                {
                    detail = "cropfield-deconstruct-target-missing grid=Vec3Int("
                        + x.ToString(CultureInfo.InvariantCulture) + ","
                        + y.ToString(CultureInfo.InvariantCulture) + ","
                        + z.ToString(CultureInfo.InvariantCulture) + ") fallbackCount="
                        + coordinateFallbackCount.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                object? view = null;
                var instanceToView = AccessTools.Field(managerType, "instanceToView")?.GetValue(manager) as IDictionary;
                if (instanceToView != null && instanceToView.Contains(cropfield))
                {
                    view = instanceToView[cropfield];
                }

                var disposeTarget = view ?? cropfield;
                var dispose = AccessTools.Method(disposeTarget.GetType(), "Dispose", Type.EmptyTypes);
                if (dispose == null)
                {
                    detail = "cropfield-deconstruct-dispose-missing targetType="
                        + (disposeTarget.GetType().FullName ?? disposeTarget.GetType().Name);
                    return false;
                }

                dispose.Invoke(disposeTarget, null);
                if (byNodeField?.GetValue(manager) is IDictionary updatedByNode && updatedByNode.Contains(grid))
                {
                    detail = "cropfield-deconstruct-readback-failed grid=Vec3Int("
                        + x.ToString(CultureInfo.InvariantCulture) + ","
                        + y.ToString(CultureInfo.InvariantCulture) + ","
                        + z.ToString(CultureInfo.InvariantCulture) + ")";
                    return false;
                }

                detail = "ok cropfield-spatial-v1 deconstruct native="
                    + (view != null ? "CropView.Dispose" : "CropfieldInstance.Dispose")
                    + " grid=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture) + ","
                    + y.ToString(CultureInfo.InvariantCulture) + ","
                    + z.ToString(CultureInfo.InvariantCulture) + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = "cropfield-deconstruct-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static void ReplicationCropfieldZoneModifyPrefix(MethodBase __originalMethod, object __instance)
        {
            if (!replicationConfigCropfieldSpatialReplicationV1
                || !ShouldObserveReplicationRegionCommands()
                || __instance == null
                || !TryReadInstanceMemberValue(__instance, "cropfieldInstance", out var cropfield)
                || cropfield == null)
            {
                return;
            }

            replicationZoneModifyOperation = string.Equals(__originalMethod?.Name, "ShrinkCropfield", StringComparison.Ordinal)
                ? "ShrinkCropfield"
                : "ExpandCropfield";
            replicationZoneModifyId = FormatReplicationCropfieldTarget(cropfield);
            replicationZoneModifyRealtime = UnityEngine.Time.realtimeSinceStartup;
            instance?.LogReplicationInfo("Going Cooperative cropfield modify armed operation="
                + replicationZoneModifyOperation
                + " target="
                + replicationZoneModifyId);
        }

        private static string FormatReplicationCropfieldTarget(object cropfield)
        {
            long uniqueId = 0;
            if (TryReadInstanceMemberValue(cropfield, "UniqueId", out var uniqueIdValue) && uniqueIdValue != null)
            {
                try { uniqueId = Convert.ToInt64(uniqueIdValue, CultureInfo.InvariantCulture); }
                catch { uniqueId = 0; }
            }

            var blueprintId = string.Empty;
            if (TryReadInstanceMemberValue(cropfield, "Blueprint", out var blueprint) && blueprint != null)
            {
                TryResolveReplicationModelId(blueprint, out blueprintId);
            }

            var startText = "0,0,0";
            if (TryReadInstanceMemberValue(cropfield, "Start", out var start)
                && start != null
                && TryReadReplicationVec3Int(start, out var x, out var y, out var z))
            {
                startText = x.ToString(CultureInfo.InvariantCulture) + ","
                    + y.ToString(CultureInfo.InvariantCulture) + ","
                    + z.ToString(CultureInfo.InvariantCulture);
            }

            return uniqueId.ToString(CultureInfo.InvariantCulture) + "|" + blueprintId + "|" + startText;
        }

        private static void ReplicationCropfieldCreatePostfix(object __0, object __1, string __2)
        {
            var current = instance;
            if (current == null
                || !replicationConfigCropfieldSpatialReplicationV1
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || applyingRuntimeCommandDepth > 0
                || !TryReadReplicationVec3Int(__0, out var startX, out var startY, out var startZ)
                || !TryReadReplicationVec3Int(__1, out var endX, out var endY, out var endZ)
                || string.IsNullOrWhiteSpace(__2))
            {
                return;
            }

            current.SendReplicationRegionOrderState(
                "Crops",
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                "None",
                "Crops",
                __2.Trim(),
                "host-local cropfield-spatial-v1");
        }

        private static void ReplicationCropfieldModifyPostfix(object __0, object __1, object __2)
        {
            var current = instance;
            if (current == null
                || !replicationConfigCropfieldSpatialReplicationV1
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || applyingRuntimeCommandDepth > 0
                || !IsReplicationZoneModifyActive()
                || !TryReadReplicationVec3Int(__0, out var startX, out var startY, out var startZ)
                || !TryReadReplicationVec3Int(__1, out var endX, out var endY, out var endZ))
            {
                return;
            }

            var operation = string.Equals(__2?.ToString(), "ShrinkZone", StringComparison.Ordinal)
                ? "ShrinkCropfield"
                : "ExpandCropfield";
            current.SendReplicationRegionOrderState(
                operation,
                startX, startY, startZ,
                endX, endY, endZ,
                "None",
                "CropfieldModify",
                replicationZoneModifyId,
                "host-local cropfield-spatial-v1 modify");
        }

        private static bool TryInvokeCropfieldCreateRegionOrder(
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string cropfieldId,
            out string detail)
        {
            if (!replicationConfigCropfieldSpatialReplicationV1)
            {
                detail = "cropfield-spatial-v1-disabled";
                return false;
            }

            if (string.IsNullOrWhiteSpace(cropfieldId)
                || string.Equals(cropfieldId, "None", StringComparison.OrdinalIgnoreCase))
            {
                detail = "cropfield-id-missing";
                return false;
            }

            try
            {
                var controllerType = AccessTools.TypeByName("NSMedieval.Crops.CropsController");
                var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
                if (controllerType == null || managerType == null)
                {
                    detail = "cropfield-native-types-missing";
                    return false;
                }

                var controller = ResolveReplicationUnityManagerInstance(controllerType);
                var manager = ResolveReplicationUnityManagerInstance(managerType);
                if (controller == null || manager == null)
                {
                    detail = "cropfield-native-manager-missing";
                    return false;
                }

                if (!TryCreateVec3Int(startX, startY, startZ, out var start, out detail)
                    || !TryCreateVec3Int(endX, endY, endZ, out var end, out detail))
                {
                    return false;
                }

                var create = AccessTools.Method(
                    controllerType,
                    "CreateCropfield",
                    new[] { start!.GetType(), end!.GetType(), typeof(string) });
                if (create == null)
                {
                    detail = "cropfield-create-method-missing";
                    return false;
                }

                // The native spawn path consults this private UI latch. A stale latch
                // on the receiving peer could turn creation into expansion of an
                // unrelated field, so normal Create always runs with a cleared latch.
                var modifyLatch = AccessTools.Field(managerType, "cropfieldToModify");
                var previousLatch = modifyLatch?.GetValue(manager);
                try
                {
                    modifyLatch?.SetValue(manager, null);
                    create.Invoke(controller, new[] { start, end, cropfieldId.Trim() });
                }
                finally
                {
                    modifyLatch?.SetValue(manager, previousLatch);
                }

                detail = "ok cropfield-spatial-v1 create id="
                    + cropfieldId.Trim()
                    + " start=Vec3Int("
                    + startX.ToString(CultureInfo.InvariantCulture) + ","
                    + startY.ToString(CultureInfo.InvariantCulture) + ","
                    + startZ.ToString(CultureInfo.InvariantCulture) + ") end=Vec3Int("
                    + endX.ToString(CultureInfo.InvariantCulture) + ","
                    + endY.ToString(CultureInfo.InvariantCulture) + ","
                    + endZ.ToString(CultureInfo.InvariantCulture) + ")";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeCropfieldModifyRegionOrder(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string targetKey,
            out string detail)
        {
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
                var orderEnumType = AccessTools.TypeByName("NSMedieval.Types.OrderType");
                var vecType = AccessTools.TypeByName("NSMedieval.Vec3Int");
                var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
                if (managerType == null || orderEnumType == null || vecType == null || manager == null)
                {
                    detail = "cropfield-modify-native-surface-missing";
                    return false;
                }

                if (!TryResolveReplicationCropfieldTarget(manager, targetKey, out var cropfield, out var targetDetail)
                    || cropfield == null)
                {
                    detail = "cropfield-modify-target-missing " + targetDetail;
                    return false;
                }

                if (!TryCreateVec3Int(startX, startY, startZ, out var start, out detail)
                    || !TryCreateVec3Int(endX, endY, endZ, out var end, out detail))
                {
                    return false;
                }

                var modify = AccessTools.Method(managerType, "ModifyCropfield", new[] { vecType, vecType, orderEnumType });
                var latch = AccessTools.Field(managerType, "cropfieldToModify");
                if (modify == null || latch == null)
                {
                    detail = "cropfield-modify-method-or-latch-missing";
                    return false;
                }

                var nativeOrderName = string.Equals(orderType, "ShrinkCropfield", StringComparison.Ordinal)
                    ? "ShrinkZone"
                    : "ExpandZone";
                var nativeOrder = Enum.Parse(orderEnumType, nativeOrderName, true);
                var previous = latch.GetValue(manager);
                try
                {
                    latch.SetValue(manager, cropfield);
                    modify.Invoke(manager, new[] { start, end, nativeOrder });
                }
                finally
                {
                    latch.SetValue(manager, previous);
                }

                detail = "ok cropfield-spatial-v1 operation=" + orderType
                    + " target=" + targetDetail
                    + " start=Vec3Int(" + startX.ToString(CultureInfo.InvariantCulture) + ","
                    + startY.ToString(CultureInfo.InvariantCulture) + ","
                    + startZ.ToString(CultureInfo.InvariantCulture) + ") end=Vec3Int("
                    + endX.ToString(CultureInfo.InvariantCulture) + ","
                    + endY.ToString(CultureInfo.InvariantCulture) + ","
                    + endZ.ToString(CultureInfo.InvariantCulture) + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationCropfieldTarget(
            object manager,
            string targetKey,
            out object? cropfield,
            out string detail)
        {
            cropfield = null;
            detail = string.Empty;
            var parts = (targetKey ?? string.Empty).Split('|');
            long.TryParse(parts.Length > 0 ? parts[0] : string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wantedUid);
            var wantedBlueprint = parts.Length > 1 ? parts[1] : string.Empty;
            var wantedStart = parts.Length > 2 ? parts[2] : string.Empty;
            if (!TryReadInstanceMemberValue(manager, "cropfields", out var cropfieldsValue)
                || !(cropfieldsValue is System.Collections.IEnumerable cropfields))
            {
                detail = "cropfield-registry-missing";
                return false;
            }

            object? fallback = null;
            var fallbackCount = 0;
            foreach (var candidate in cropfields)
            {
                if (candidate == null) continue;
                var candidateKey = FormatReplicationCropfieldTarget(candidate);
                var candidateParts = candidateKey.Split('|');
                if (wantedUid > 0
                    && candidateParts.Length > 0
                    && long.TryParse(candidateParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var candidateUid)
                    && candidateUid == wantedUid)
                {
                    cropfield = candidate;
                    detail = "uid:" + wantedUid.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (candidateParts.Length > 2
                    && string.Equals(candidateParts[1], wantedBlueprint, StringComparison.Ordinal)
                    && string.Equals(candidateParts[2], wantedStart, StringComparison.Ordinal))
                {
                    fallback = candidate;
                    fallbackCount++;
                }
            }

            if (fallbackCount == 1)
            {
                cropfield = fallback;
                detail = "blueprint-start:" + wantedBlueprint + "@" + wantedStart;
                return true;
            }

            detail = "target=" + targetKey + " fallbackCount=" + fallbackCount.ToString(CultureInfo.InvariantCulture);
            return false;
        }
    }
}
