using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
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
