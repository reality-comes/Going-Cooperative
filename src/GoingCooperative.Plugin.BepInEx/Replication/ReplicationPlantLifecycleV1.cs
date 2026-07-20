using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationPlantLifecycleSpawnDeltaKind = "PlantLifecycleSpawnV1";
        private const float ReplicationPlantLifecycleReconcileSeconds = 0.5f;
        private const int ReplicationPlantLifecycleMaxSpawnsPerPass = 64;
        private static readonly HashSet<string> ReplicationObservedHostCropTiles = new HashSet<string>(StringComparer.Ordinal);
        private static float replicationNextPlantLifecycleReconcileRealtime;
        private static bool replicationPlantLifecycleBaselineCollected;

        private void TryInstallReplicationPlantLifecycleV1Hooks(Harmony harmonyInstance)
        {
            if (!replicationConfigPlantLifecycleReplication)
            {
                return;
            }

            LogReplicationInfo("Going Cooperative plant lifecycle v1 mode=targeted-cropfield-reconciliation");
        }

        private void SendHostReplicationPlantLifecycleIfDue()
        {
            if (!replicationConfigPlantLifecycleReplication
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || Time.realtimeSinceStartup < replicationNextPlantLifecycleReconcileRealtime)
            {
                return;
            }

            replicationNextPlantLifecycleReconcileRealtime = Time.realtimeSinceStartup
                + ReplicationPlantLifecycleReconcileSeconds;
            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
                var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
                if (manager == null
                    || !TryReadInstanceMemberValue(manager, "cropfields", out var cropfieldsValue)
                    || !(cropfieldsValue is IEnumerable cropfields))
                {
                    return;
                }

                var currentTiles = new HashSet<string>(StringComparer.Ordinal);
                var sent = 0;
                foreach (var cropfield in cropfields)
                {
                    if (cropfield == null) continue;
                    var cropsProperty = AccessTools.Property(cropfield.GetType(), "Crops");
                    if (!(cropsProperty?.GetValue(cropfield, null) is IDictionary crops)) continue;

                    foreach (DictionaryEntry entry in crops)
                    {
                        if (entry.Key == null
                            || entry.Value == null
                            || !TryReadReplicationVec3Int(entry.Key, out var gridX, out var gridY, out var gridZ))
                        {
                            continue;
                        }

                        var tileKey = gridX.ToString(CultureInfo.InvariantCulture) + ":"
                            + gridY.ToString(CultureInfo.InvariantCulture) + ":"
                            + gridZ.ToString(CultureInfo.InvariantCulture);
                        currentTiles.Add(tileKey);
                        if (!replicationPlantLifecycleBaselineCollected
                            || ReplicationObservedHostCropTiles.Contains(tileKey)
                            || sent >= ReplicationPlantLifecycleMaxSpawnsPerPass)
                        {
                            continue;
                        }

                        TryReadReplicationWorldObjectLongMember(entry.Value, "UniqueId", "uniqueId", out var uniqueId);
                        TryReadReplicationWorldObjectStringMember(entry.Value, "BlueprintId", "blueprintId", out var blueprintId);
                        var delta = new ReplicationWorldObjectDelta(
                            ++replicationWorldObjectDeltaSequence,
                            Time.realtimeSinceStartup,
                            ReplicationPlantLifecycleSpawnDeltaKind,
                            uniqueId,
                            blueprintId ?? string.Empty,
                            gridX,
                            gridY,
                            gridZ,
                            "plant-lifecycle-v1 source=targeted-cropfield-reconciliation");
                        SendReplicationWorldObjectDelta(delta);
                        ReplicationObservedHostCropTiles.Add(tileKey);
                        sent++;
                        LogReplicationInfo("Going Cooperative plant lifecycle spawn sent uid="
                            + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                            + " blueprint=" + delta.BlueprintId
                            + " grid=Vec3Int(" + delta.GridX.ToString(CultureInfo.InvariantCulture) + ","
                            + delta.GridY.ToString(CultureInfo.InvariantCulture) + ","
                            + delta.GridZ.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                }

                if (!replicationPlantLifecycleBaselineCollected)
                {
                    ReplicationObservedHostCropTiles.UnionWith(currentTiles);
                    replicationPlantLifecycleBaselineCollected = true;
                    LogReplicationInfo("Going Cooperative plant lifecycle baseline collected crops="
                        + currentTiles.Count.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    // Forget harvested/removed tiles so replanting the same grid
                    // produces a fresh spawn semantic on a later pass.
                    ReplicationObservedHostCropTiles.IntersectWith(currentTiles);
                }
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative plant lifecycle reconciliation failed reason="
                    + FormatReflectionExceptionDetail(ex));
            }
        }

        private static void ResetReplicationPlantLifecycleV1State()
        {
            ReplicationObservedHostCropTiles.Clear();
            replicationNextPlantLifecycleReconcileRealtime = 0f;
            replicationPlantLifecycleBaselineCollected = false;
        }

        private static bool TryApplyReplicationPlantLifecycleV1(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            detail = string.Empty;
            if (!replicationConfigPlantLifecycleReplication)
            {
                detail = "plant-lifecycle-v1-disabled";
                return false;
            }

            try
            {
                var managerType = AccessTools.TypeByName("NSMedieval.Crops.CropsManager");
                var vecType = AccessTools.TypeByName("NSMedieval.Vec3Int");
                var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
                if (managerType == null || vecType == null || manager == null
                    || !TryCreateVec3Int(delta.GridX, delta.GridY, delta.GridZ, out var grid, out detail))
                {
                    detail = "plant-lifecycle-cropfield-manager-unavailable " + detail;
                    return false;
                }

                object? cropfield = null;
                var byNodeField = AccessTools.Field(managerType, "cropfieldsByNodeId");
                if (byNodeField?.GetValue(manager) is IDictionary byNode && byNode.Contains(grid))
                {
                    cropfield = byNode[grid];
                }

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
                                && TryReadReplicationVec3Int(position, out var x, out var y, out var z)
                                && x == delta.GridX && y == delta.GridY && z == delta.GridZ)
                            {
                                cropfield = candidate;
                                break;
                            }
                        }
                        if (cropfield != null) break;
                    }
                }

                if (cropfield == null)
                {
                    detail = "plant-lifecycle-owning-cropfield-missing grid=Vec3Int("
                        + delta.GridX.ToString(CultureInfo.InvariantCulture) + ","
                        + delta.GridY.ToString(CultureInfo.InvariantCulture) + ","
                        + delta.GridZ.ToString(CultureInfo.InvariantCulture) + ")";
                    return false;
                }

                var cropsProperty = AccessTools.Property(cropfield.GetType(), "Crops");
                var crops = cropsProperty?.GetValue(cropfield, null) as IDictionary;
                if (crops != null && crops.Contains(grid))
                {
                    var existing = crops[grid];
                    if (existing != null && delta.UniqueId > 0)
                    {
                        RegisterReplicationHostIdentity(delta.UniqueId, existing, "plant-lifecycle-existing");
                    }
                    detail = "plant-lifecycle-already-present";
                    return true;
                }

                var plantCrop = AccessTools.Method(cropfield.GetType(), "PlantCrop", new[] { vecType });
                if (plantCrop == null)
                {
                    detail = "plant-lifecycle-plant-crop-method-missing";
                    return false;
                }

                plantCrop.Invoke(cropfield, new[] { grid });
                crops = cropsProperty?.GetValue(cropfield, null) as IDictionary;
                if (crops == null || !crops.Contains(grid) || crops[grid] == null)
                {
                    detail = "plant-lifecycle-native-spawn-readback-failed";
                    return false;
                }

                var planted = crops[grid]!;
                if (delta.UniqueId > 0)
                {
                    RegisterReplicationHostIdentity(delta.UniqueId, planted, "plant-lifecycle-spawn");
                }

                detail = "ok plant-lifecycle-v1 spawned blueprint=" + delta.BlueprintId
                    + " grid=Vec3Int(" + delta.GridX.ToString(CultureInfo.InvariantCulture) + ","
                    + delta.GridY.ToString(CultureInfo.InvariantCulture) + ","
                    + delta.GridZ.ToString(CultureInfo.InvariantCulture) + ")";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }
    }
}
