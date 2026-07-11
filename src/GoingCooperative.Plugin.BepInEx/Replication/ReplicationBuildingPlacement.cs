using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static bool TryApplyReplicationBuildPlacementSafe(
            string blueprintId,
            int x,
            int y,
            int z,
            int angleY,
            string factionOwnership,
            out string detail)
        {
            try
            {
                var placementManagerType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingPlacementManager");
                var buildingsPoolType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsPool");
                var blueprintType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingBlueprint");
                var viewComponentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
                var buildingsManagerMainType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsManagerMain");
                var factionOwnershipType = AccessTools.TypeByName("NSMedieval.Village.FactionOwnership");
                var relocateBuildingType = AccessTools.TypeByName("NSMedieval.MovableBuildings.RelocateBuilding");
                var vec3IntType = AccessTools.TypeByName("NSMedieval.Vec3Int");
                if (placementManagerType == null)
                {
                    detail = "safe-build-placement-manager-type-missing";
                    return false;
                }

                if (buildingsPoolType == null)
                {
                    detail = "safe-build-buildings-pool-type-missing";
                    return false;
                }

                if (blueprintType == null)
                {
                    detail = "safe-build-base-building-blueprint-type-missing";
                    return false;
                }

                if (viewComponentType == null)
                {
                    detail = "safe-build-base-building-view-component-type-missing";
                    return false;
                }

                if (buildingsManagerMainType == null)
                {
                    detail = "safe-build-buildings-manager-main-type-missing";
                    return false;
                }

                if (factionOwnershipType == null)
                {
                    detail = "safe-build-faction-ownership-type-missing";
                    return false;
                }

                if (relocateBuildingType == null)
                {
                    detail = "safe-build-relocate-building-type-missing";
                    return false;
                }

                if (vec3IntType == null)
                {
                    detail = "safe-build-vec3int-type-missing";
                    return false;
                }

                var placementManager = buildingPlacementManagerInstance ?? ResolveReplicationUnityManagerInstance(placementManagerType);
                if (placementManager == null)
                {
                    detail = "safe-build-building-placement-manager-missing";
                    return false;
                }

                var buildingsPool = buildingsPoolInstance ?? ResolveReplicationUnityManagerInstance(buildingsPoolType);
                if (buildingsPool == null)
                {
                    detail = "safe-build-buildings-pool-missing";
                    return false;
                }

                var buildingsManagerMain = ResolveReplicationBuildingsManagerMain(buildingsManagerMainType, out var managerDetail);
                if (buildingsManagerMain == null)
                {
                    detail = "safe-build-buildings-manager-main-missing " + managerDetail;
                    return false;
                }

                if (!buildingsManagerMainType.IsInstanceOfType(buildingsManagerMain))
                {
                    detail = "safe-build-buildings-manager-main-type-unexpected type=" + buildingsManagerMain.GetType().FullName;
                    return false;
                }

                var getBuildableBase = AccessTools.Method(buildingsPoolType, "GetBuildableBase", new[] { typeof(string) });
                if (getBuildableBase == null)
                {
                    detail = "safe-build-get-buildable-base-method-missing";
                    return false;
                }

                var spawnFromPool = AccessTools.Method(placementManagerType, "SpawnFromPool", new[] { blueprintType, vec3IntType, typeof(int), factionOwnershipType });
                if (spawnFromPool == null)
                {
                    detail = "safe-build-spawn-from-pool-method-missing";
                    return false;
                }

                var spawnBaseBuildingViewComponent = AccessTools.Method(placementManagerType, "SpawnBaseBuildingViewComponent", new[] { blueprintType, vec3IntType, typeof(int) });

                var initializeBuilding = AccessTools.Method(placementManagerType, "InitializeBuilding", new[] { typeof(string), relocateBuildingType });
                if (initializeBuilding == null)
                {
                    detail = "safe-build-initialize-building-method-missing";
                    return false;
                }

                var objectPlacedOnMap = AccessTools.Method(placementManagerType, "ObjectPlacedOnMap", new[] { viewComponentType });
                if (objectPlacedOnMap == null)
                {
                    detail = "safe-build-object-placed-on-map-method-missing";
                    return false;
                }

                var createAndBind = AccessTools.Method(
                    buildingsManagerMainType,
                    "CreateBuildingInstanceAndBindToView",
                    new[] { blueprintType, viewComponentType, typeof(Vector3), typeof(int) });
                if (createAndBind == null)
                {
                    detail = "safe-build-create-building-instance-and-bind-to-view-method-missing";
                    return false;
                }

                var blueprint = getBuildableBase.Invoke(buildingsPool, new object[] { blueprintId });
                if (blueprint == null)
                {
                    detail = "safe-build-blueprint-missing id=" + blueprintId;
                    return false;
                }

                if (!TryCreateVec3Int(x, y, z, out var position, out var vecDetail))
                {
                    detail = "safe-build-vec3int-create-failed " + vecDetail;
                    return false;
                }

                if (!TryParseReplicationBuildFactionOwnership(factionOwnershipType, factionOwnership, out var parsedFactionOwnership, out var factionDetail)
                    || parsedFactionOwnership == null)
                {
                    detail = factionDetail;
                    return false;
                }

                var worldPosition = GetReplicationBuildWorldPosition(position, x, y, z, out var positionDetail);
                initializeBuilding.Invoke(placementManager, new object?[] { blueprintId, null });
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "baseBuildingBlueprint", blueprint);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "raycastGridCurrent", position);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "raycastGridStart", position);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "raycastGridPrevious", position);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "previewAngle", angleY);
                SetInstanceFieldIfPresent(placementManager, placementManagerType, "hasSelectedItem", true);

                var spawnDetail = "spawn=SpawnFromPool";
                var viewComponent = spawnFromPool.Invoke(placementManager, new[] { blueprint, position, (object)angleY, parsedFactionOwnership });
                if (viewComponent == null)
                {
                    if (spawnBaseBuildingViewComponent != null)
                    {
                        viewComponent = spawnBaseBuildingViewComponent.Invoke(placementManager, new[] { blueprint, position, (object)angleY });
                        spawnDetail = "spawn=SpawnBaseBuildingViewComponent fallbackFrom=SpawnFromPoolNull";
                    }

                    if (viewComponent == null)
                    {
                        detail = "safe-build-spawn-returned-null spawnFromPool=null fallback="
                            + (spawnBaseBuildingViewComponent == null ? "missing" : "null");
                        return false;
                    }
                }

                if (!viewComponentType.IsInstanceOfType(viewComponent))
                {
                    detail = "safe-build-spawn-return-type-unexpected " + spawnDetail + " type=" + viewComponent.GetType().FullName;
                    return false;
                }

                var finalizeDetail = "finalize=ObjectPlacedOnMap";
                objectPlacedOnMap.Invoke(placementManager, new[] { viewComponent });
                var placementCleanupDetail = ClearReplicationBuildingPlacementState(placementManager, placementManagerType);

                TryResolveReplicationBuildingCandidateInstance(viewComponent, out var buildingInstance, out _);
                if (buildingInstance == null)
                {
                    buildingInstance = createAndBind.Invoke(buildingsManagerMain, new[] { blueprint, viewComponent, (object)worldPosition, (object)angleY });
                    if (buildingInstance == null)
                    {
                        TryResolveReplicationBuildingCandidateInstance(viewComponent, out buildingInstance, out _);
                    }
                }

                var refreshDetail = RefreshReplicationPlacedBuilding(buildingsManagerMain, buildingInstance, viewComponent);
                detail = "ok via=SpawnFromPool+CreateBuildingInstanceAndBindToView blueprintId="
                    + blueprintId
                    + " position=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ") angleY="
                    + angleY.ToString(CultureInfo.InvariantCulture)
                    + " factionOwnership="
                    + Convert.ToString(parsedFactionOwnership, CultureInfo.InvariantCulture)
                    + " "
                    + positionDetail
                    + " "
                    + spawnDetail
                    + " "
                    + finalizeDetail
                    + " "
                    + placementCleanupDetail
                    + " "
                    + refreshDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "safe-build-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationBuildPlacement(
            string blueprintId,
            int x,
            int y,
            int z,
            int angleY,
            string factionOwnership,
            out string detail)
        {
            if (TryInvokeBuildingPlacementPlaceBlueprint(blueprintId, x, y, z, angleY, out var vanillaDetail))
            {
                if (TryFindReplicationBuildingBlueprintCandidate(
                    blueprintId,
                    x,
                    y,
                    z,
                    out _,
                    out var verifyDetail))
                {
                    detail = "ok via=vanilla-replay " + vanillaDetail + " " + verifyDetail;
                    return true;
                }

                vanillaDetail += " verify-missing " + verifyDetail;
            }

            if (TryApplyReplicationBuildPlacementSafe(blueprintId, x, y, z, angleY, factionOwnership, out var safeDetail))
            {
                detail = safeDetail + " vanillaReplayFailed=" + vanillaDetail.Replace(" ", "_");
                return true;
            }

            detail = "vanilla-replay-failed "
                + vanillaDetail
                + " safe-build-placement-failed "
                + safeDetail;
            return false;
        }

        private static bool TryApplyReplicationBuildPlacementAuthoritative(
            string blueprintId,
            int x,
            int y,
            int z,
            int angleY,
            string factionOwnership,
            out string detail)
        {
            if (TryApplyReplicationBuildPlacementSafe(blueprintId, x, y, z, angleY, factionOwnership, out var safeDetail)
                && TryFindReplicationBuildingBlueprintCandidate(blueprintId, x, y, z, out _, out var safeVerifyDetail))
            {
                detail = safeDetail + " " + safeVerifyDetail;
                return true;
            }

            if (TryInvokeBuildingPlacementPlaceBlueprint(blueprintId, x, y, z, angleY, out var vanillaDetail))
            {
                if (TryFindReplicationBuildingBlueprintCandidate(blueprintId, x, y, z, out _, out var vanillaVerifyDetail))
                {
                    detail = "ok via=authoritative-vanilla-replay safe="
                        + safeDetail.Replace(" ", "_")
                        + " "
                        + vanillaDetail
                        + " "
                        + vanillaVerifyDetail;
                    return true;
                }

                vanillaDetail += " verify-missing " + vanillaVerifyDetail;
            }

            detail = "authoritative-build-placement-failed safe="
                + safeDetail
                + " vanilla="
                + vanillaDetail;
            return false;
        }

        private static string RefreshReplicationPlacedBuilding(object buildingsManagerMain, object? buildingInstance, object viewComponent)
        {
            var detail = "postPlacementRefresh=";
            var count = 0;
            count += TryInvokeReplicationNoArgMethod(buildingsManagerMain, "RefreshPlacedBlueprints") ? 1 : 0;
            count += TryInvokeReplicationNoArgMethod(buildingsManagerMain, "ResourceChangedRefreshBlueprints") ? 1 : 0;
            count += TryInvokeReplicationNoArgMethod(buildingsManagerMain, "ResourceDeliveredRefreshBlueprint") ? 1 : 0;
            count += TryInvokeReplicationNoArgMethod(buildingsManagerMain, "RefreshBuilding") ? 1 : 0;
            if (buildingInstance != null)
            {
                count += TryInvokeReplicationNoArgMethod(buildingInstance, "RefreshBuilding") ? 1 : 0;
                count += TryInvokeReplicationNoArgMethod(buildingInstance, "Refresh") ? 1 : 0;
                count += TryInvokeReplicationNoArgMethod(buildingInstance, "RefreshWalkableCollider") ? 1 : 0;
            }

            count += TryInvokeReplicationNoArgMethod(viewComponent, "RefreshBuilding") ? 1 : 0;
            count += TryInvokeReplicationNoArgMethod(viewComponent, "Refresh") ? 1 : 0;
            count += TryInvokeReplicationNoArgMethod(viewComponent, "TryShowWorkPositionMarkers") ? 1 : 0;
            return detail + count.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryInvokeReplicationNoArgMethod(object target, string methodName)
        {
            try
            {
                var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null || method.GetParameters().Length != 0)
                {
                    return false;
                }

                method.Invoke(target, Array.Empty<object>());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? ResolveReplicationBuildingsManagerMain(Type buildingsManagerMainType, out string detail)
        {
            var manager = ResolveReplicationUnityManagerInstance(buildingsManagerMainType);
            if (manager != null)
            {
                detail = "source=manager-instance";
                return manager;
            }

            string activeVillageDetail;
            if (TryGetReplicationActiveVillage(out var activeVillage, out activeVillageDetail) && activeVillage != null)
            {
                if (TryReadInstanceMemberValue(activeVillage, "BuildingsManagerMain", out manager) && manager != null)
                {
                    detail = "source=activeVillage.BuildingsManagerMain " + activeVillageDetail;
                    return manager;
                }

                if (TryReadInstanceMemberValue(activeVillage, "buildingsManagerMain", out manager) && manager != null)
                {
                    detail = "source=activeVillage.buildingsManagerMain " + activeVillageDetail;
                    return manager;
                }

                if ((TryReadInstanceMemberValue(activeVillage, "Map", out var villageMap) && villageMap != null)
                    || (TryReadInstanceMemberValue(activeVillage, "map", out villageMap) && villageMap != null))
                {
                    if (TryReadInstanceMemberValue(villageMap, "BuildingsManagerMain", out manager) && manager != null)
                    {
                        detail = "source=activeVillage.Map.BuildingsManagerMain " + activeVillageDetail;
                        return manager;
                    }

                    if (TryReadInstanceMemberValue(villageMap, "buildingsManagerMain", out manager) && manager != null)
                    {
                        detail = "source=activeVillage.Map.buildingsManagerMain " + activeVillageDetail;
                        return manager;
                    }
                }
            }

            detail = activeVillageDetail;
            return null;
        }

        private static bool TryGetReplicationActiveVillage(out object? activeVillage, out string detail)
        {
            activeVillage = null;
            try
            {
                var villageManagerType = AccessTools.TypeByName("NSMedieval.Village.VillageManager")
                    ?? AccessTools.TypeByName("NSMedieval.VillageManager");
                if (villageManagerType == null)
                {
                    detail = "village-manager-type-missing";
                    return false;
                }

                var activeVillageProperty = villageManagerType.GetProperty("ActiveVillage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? villageManagerType.GetProperty("activeVillage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (activeVillageProperty != null && activeVillageProperty.GetIndexParameters().Length == 0)
                {
                    activeVillage = activeVillageProperty.GetValue(null, null);
                }

                if (activeVillage == null)
                {
                    var activeVillageMethod = villageManagerType.GetMethod("get_ActiveVillage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (activeVillageMethod != null && activeVillageMethod.GetParameters().Length == 0)
                    {
                        activeVillage = activeVillageMethod.Invoke(null, null);
                    }
                }

                if (activeVillage == null)
                {
                    detail = "active-village-null";
                    return false;
                }

                detail = "activeVillageType=" + activeVillage.GetType().FullName;
                return true;
            }
            catch (Exception ex)
            {
                detail = "active-village-error=" + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryParseReplicationBuildFactionOwnership(Type factionOwnershipType, string factionOwnership, out object? parsed, out string detail)
        {
            parsed = null;
            var normalizedFactionOwnership = string.IsNullOrWhiteSpace(factionOwnership) ? "Player" : factionOwnership.Trim();
            try
            {
                parsed = Enum.Parse(factionOwnershipType, normalizedFactionOwnership, ignoreCase: true);
                detail = "ok";
                return true;
            }
            catch
            {
                detail = "safe-build-faction-ownership-parse-failed factionOwnership=" + (factionOwnership ?? string.Empty);
                return false;
            }
        }

        private static Vector3 GetReplicationBuildWorldPosition(object gridPosition, int x, int y, int z, out string detail)
        {
            var fallback = new Vector3(x, y, z);
            try
            {
                var gridUtilsType = AccessTools.TypeByName("NSMedieval.GridUtils");
                if (gridUtilsType != null)
                {
                    var worldPositionMethod = AccessTools.Method(gridUtilsType, "GetWorldPosition", new[] { gridPosition.GetType() });
                    if (worldPositionMethod != null && worldPositionMethod.Invoke(null, new[] { gridPosition }) is Vector3 worldPosition)
                    {
                        detail = "worldPosition=GridUtils.GetWorldPosition(Vec3Int) value=" + FormatUnityVector(worldPosition);
                        return worldPosition;
                    }
                }

                detail = "worldPosition=fallback-vector value=" + FormatUnityVector(fallback);
                return fallback;
            }
            catch (Exception ex)
            {
                detail = "worldPosition=fallback-vector " + FormatReflectionExceptionDetail(ex) + " value=" + FormatUnityVector(fallback);
                return fallback;
            }
        }
    }
}
