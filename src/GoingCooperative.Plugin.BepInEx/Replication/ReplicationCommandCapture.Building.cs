using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using GoingCooperative.Core;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Dictionary<string, float> ReplicationRecentBuildIntentKeys = new Dictionary<string, float>(64);
        private static readonly Dictionary<string, float> ReplicationRecentRawBuildCaptureKeys = new Dictionary<string, float>(64);
        private static readonly List<ReplicationPendingBuildIntent> ReplicationPendingBuildIntents = new List<ReplicationPendingBuildIntent>(64);
        private static readonly HashSet<string> ReplicationPendingBuildIntentKeys = new HashSet<string>(StringComparer.Ordinal);
        private const float ReplicationBuildIntentDedupeSeconds = 1.5f;
        private const float ReplicationRawBuildCaptureDedupeSeconds = 0.25f;
        private const float ReplicationPendingBuildIntentSeconds = 8f;

        private static readonly string[] ReplicationBuildBlueprintIdMemberNames =
        {
            "BlueprintId",
            "blueprintId",
            "ObjectId",
            "objectId",
            "ProtoId",
            "protoId",
            "GroupIdentifier",
            "groupIdentifier",
            "Id",
            "id",
            "ID",
            "Name",
            "name"
        };

        private static readonly string[] ReplicationBuildTypeMemberNames =
        {
            "BuildingType",
            "buildingType",
            "BuildingTypes",
            "buildingTypes",
            "Type",
            "type"
        };

        private int TryInstallReplicationBuildCommandCapture(Harmony harmonyInstance)
        {
            if (IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode))
            {
                return 0;
            }

            var spawnFromPoolPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildSpawnFromPoolPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var mouseUpPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationBuildMouseUpSpawnInitializeBuildingsPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));

            var patched = 0;
            patched += TryPatchReplicationCommandCaptureMethodByTypeNames(
                harmonyInstance,
                spawnFromPoolPostfix,
                "NSMedieval.BuildingComponents.BuildingPlacementManager",
                "SpawnFromPool",
                "NSMedieval.BuildingComponents.BaseBuildingBlueprint",
                "NSMedieval.Vec3Int",
                "System.Int32",
                "NSMedieval.Village.FactionOwnership");
            patched += TryPatchReplicationCommandCaptureMethodByTypeNames(
                harmonyInstance,
                mouseUpPostfix,
                "NSMedieval.BuildingComponents.BuildingPlacementManager",
                "MouseUpSpawnInitializeBuildings",
                "System.Int32");
            return patched;
        }

        private static void ReplicationBuildSpawnFromPoolPostfix(MethodBase __originalMethod, object __result, object __0, object __1, int __2, object __3)
        {
            if (!ShouldObserveReplicationBuildCommands())
            {
                return;
            }

            if (__result == null)
            {
                return;
            }

            if (!TryReadReplicationVec3Int(__1, out var x, out var y, out var z))
            {
                instance?.LogReplicationWarning("Going Cooperative replication build intent ignored position-unreadable source="
                    + __originalMethod.Name);
                return;
            }

            if (ShouldSkipDuplicateReplicationRawBuildCapture(__0, x, y, z, __2))
            {
                return;
            }

            if (!TryExtractReplicationBuildPayloadFields(
                __0,
                __3,
                out var blueprintId,
                out var buildingType,
                out var factionOwnership,
                out var detail))
            {
                instance?.LogReplicationWarning("Going Cooperative replication build intent ignored payload-unreadable source="
                    + __originalMethod.Name
                    + " "
                    + detail);
                return;
            }

            BufferReplicationBuildIntent(
                blueprintId,
                x,
                y,
                z,
                __2,
                buildingType,
                factionOwnership,
                "build:" + __originalMethod.Name);
        }

        private static void ReplicationBuildMouseUpSpawnInitializeBuildingsPostfix(MethodBase __originalMethod, int __0)
        {
            if (!ShouldObserveReplicationBuildCommands())
            {
                ClearStaleReplicationPendingBuildIntents(Time.realtimeSinceStartup);
                return;
            }

            FlushReplicationPendingBuildIntents("build:" + __originalMethod.Name);
        }

        private static void BufferReplicationBuildIntent(
            string blueprintId,
            int x,
            int y,
            int z,
            int angleY,
            string buildingType,
            string factionOwnership,
            string source)
        {
            var now = Time.realtimeSinceStartup;
            ClearStaleReplicationPendingBuildIntents(now);

            var key = FormatReplicationBuildIntentKey(blueprintId, x, y, z, angleY);
            if (!ReplicationPendingBuildIntentKeys.Add(key))
            {
                return;
            }

            ReplicationPendingBuildIntents.Add(new ReplicationPendingBuildIntent(
                blueprintId,
                x,
                y,
                z,
                angleY,
                buildingType,
                factionOwnership,
                now));

            instance?.LogReplicationInfo("Going Cooperative replication build intent buffered source="
                + source
                + " count="
                + ReplicationPendingBuildIntents.Count.ToString(CultureInfo.InvariantCulture)
                + " blueprintId="
                + blueprintId
                + " grid=Vec3Int("
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + ")");
        }

        private static void FlushReplicationPendingBuildIntents(string source)
        {
            ClearStaleReplicationPendingBuildIntents(Time.realtimeSinceStartup);
            if (ReplicationPendingBuildIntents.Count == 0)
            {
                return;
            }

            var pending = ReplicationPendingBuildIntents.ToArray();
            ReplicationPendingBuildIntents.Clear();
            ReplicationPendingBuildIntentKeys.Clear();

            var sent = 0;
            for (var i = 0; i < pending.Length; i++)
            {
                var intent = pending[i];
                if (CaptureReplicationBuildIntent(
                    intent.BlueprintId,
                    intent.X,
                    intent.Y,
                    intent.Z,
                    intent.AngleY,
                    intent.BuildingType,
                    intent.FactionOwnership,
                    source))
                {
                    sent++;
                }
            }

            instance?.LogReplicationInfo("Going Cooperative replication build intent flush source="
                + source
                + " buffered="
                + pending.Length.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + sent.ToString(CultureInfo.InvariantCulture));
        }

        private static void ClearStaleReplicationPendingBuildIntents(float now)
        {
            if (ReplicationPendingBuildIntents.Count == 0)
            {
                return;
            }

            var oldestAllowed = now - ReplicationPendingBuildIntentSeconds;
            if (ReplicationPendingBuildIntents[ReplicationPendingBuildIntents.Count - 1].CapturedRealtime >= oldestAllowed)
            {
                return;
            }

            ReplicationPendingBuildIntents.Clear();
            ReplicationPendingBuildIntentKeys.Clear();
        }

        private static bool CaptureReplicationBuildIntent(
            string blueprintId,
            int x,
            int y,
            int z,
            int angleY,
            string buildingType,
            string factionOwnership,
            string source)
        {
            if (IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode)
                || !ShouldSendReplicationLocalCommandIntent())
            {
                return false;
            }

            if (ShouldSkipDuplicateReplicationBuildIntent(blueprintId, x, y, z, angleY))
            {
                return false;
            }

            var command = new LockstepCommand(
                ReplicationClientPeerId,
                replicationIntentSequence + 1,
                0L,
                CommandKind.Build,
                LockstepCommandPayloads.CreateBuildPayload(blueprintId, x, y, z, angleY, buildingType, factionOwnership, afterLoading: false),
                null,
                x,
                y,
                z);

            if (ShouldSkipDuplicateReplicationLocalCommand(command))
            {
                return false;
            }

            if (IsReplicationCaptureModeShadow(replicationConfigCommandCaptureMode))
            {
                LogReplicationLocalCommandShadow(command, source);
                return false;
            }

            if (!IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode))
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
            return true;
        }

        private static bool ShouldSkipDuplicateReplicationRawBuildCapture(object blueprint, int x, int y, int z, int angleY)
        {
            var now = Time.realtimeSinceStartup;
            CleanupRecentReplicationRawBuildCaptureKeys(now);

            var key = RuntimeHelpers.GetHashCode(blueprint).ToString(CultureInfo.InvariantCulture)
                + "|"
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + "|"
                + angleY.ToString(CultureInfo.InvariantCulture);

            if (ReplicationRecentRawBuildCaptureKeys.TryGetValue(key, out var lastSeen)
                && now - lastSeen < ReplicationRawBuildCaptureDedupeSeconds)
            {
                return true;
            }

            ReplicationRecentRawBuildCaptureKeys[key] = now;
            return false;
        }

        private static void CleanupRecentReplicationRawBuildCaptureKeys(float now)
        {
            if (ReplicationRecentRawBuildCaptureKeys.Count < 256)
            {
                return;
            }

            var expired = new List<string>();
            foreach (var pair in ReplicationRecentRawBuildCaptureKeys)
            {
                if (now - pair.Value >= ReplicationRawBuildCaptureDedupeSeconds)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ReplicationRecentRawBuildCaptureKeys.Remove(expired[i]);
            }
        }

        private static bool ShouldSkipDuplicateReplicationBuildIntent(string blueprintId, int x, int y, int z, int angleY)
        {
            var now = Time.realtimeSinceStartup;
            CleanupRecentReplicationBuildIntentKeys(now);

            var key = blueprintId
                + "|"
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + "|"
                + angleY.ToString(CultureInfo.InvariantCulture);

            if (ReplicationRecentBuildIntentKeys.TryGetValue(key, out var lastSeen)
                && now - lastSeen < ReplicationBuildIntentDedupeSeconds)
            {
                return true;
            }

            ReplicationRecentBuildIntentKeys[key] = now;
            return false;
        }

        private static string FormatReplicationBuildIntentKey(string blueprintId, int x, int y, int z, int angleY)
        {
            return blueprintId
                + "|"
                + x.ToString(CultureInfo.InvariantCulture)
                + ","
                + y.ToString(CultureInfo.InvariantCulture)
                + ","
                + z.ToString(CultureInfo.InvariantCulture)
                + "|"
                + angleY.ToString(CultureInfo.InvariantCulture);
        }

        private static void CleanupRecentReplicationBuildIntentKeys(float now)
        {
            if (ReplicationRecentBuildIntentKeys.Count < 128)
            {
                return;
            }

            var expired = new List<string>();
            foreach (var pair in ReplicationRecentBuildIntentKeys)
            {
                if (now - pair.Value >= ReplicationBuildIntentDedupeSeconds)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ReplicationRecentBuildIntentKeys.Remove(expired[i]);
            }
        }

        private static bool ShouldObserveReplicationBuildCommands()
        {
            return !IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode)
                && replicationConfigEnabled
                && !replicationConfigHostMode
                && applyingRuntimeCommandDepth <= 0;
        }

        private static bool TryExtractReplicationBuildPayloadFields(
            object blueprint,
            object factionOwnership,
            out string blueprintId,
            out string buildingType,
            out string faction,
            out string detail)
        {
            blueprintId = string.Empty;
            buildingType = string.Empty;
            faction = string.Empty;
            detail = string.Empty;

            if (blueprint == null)
            {
                detail = "blueprint-null";
                return false;
            }

            if (!TryResolveReplicationBuildBlueprintId(blueprint, out blueprintId))
            {
                detail = "blueprint-id-missing blueprintType=" + (blueprint.GetType().FullName ?? blueprint.GetType().Name);
                return false;
            }

            if (!TryResolveReplicationBuildType(blueprint, out buildingType))
            {
                detail = "building-type-missing blueprintId=" + blueprintId + " blueprintType=" + (blueprint.GetType().FullName ?? blueprint.GetType().Name);
                return false;
            }

            if (!TryResolveReplicationBuildFaction(factionOwnership, out faction))
            {
                detail = "faction-missing blueprintId=" + blueprintId;
                return false;
            }

            return true;
        }

        private static bool TryResolveReplicationBuildBlueprintId(object blueprint, out string blueprintId)
        {
            if (TryInvokeReplicationStringMethod(blueprint, "GetID", out blueprintId)
                || TryInvokeReplicationStringMethod(blueprint, "GetId", out blueprintId)
                || TryInvokeReplicationStringMethod(blueprint, "GetIdentifier", out blueprintId))
            {
                blueprintId = blueprintId.Trim();
                return blueprintId.Length > 0;
            }

            return TryReadReplicationBuildStringMember(blueprint, ReplicationBuildBlueprintIdMemberNames, out blueprintId);
        }

        private static bool TryResolveReplicationBuildType(object blueprint, out string buildingType)
        {
            if (TryReadReplicationBuildStringMember(blueprint, ReplicationBuildTypeMemberNames, out buildingType))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(blueprint, "BuildingType", out var upperValue) && upperValue != null)
            {
                buildingType = upperValue.ToString() ?? string.Empty;
            }
            else if (TryReadInstanceMemberValue(blueprint, "buildingType", out var lowerValue) && lowerValue != null)
            {
                buildingType = lowerValue.ToString() ?? string.Empty;
            }

            buildingType = buildingType.Trim();
            return buildingType.Length > 0;
        }

        private static bool TryResolveReplicationBuildFaction(object factionOwnership, out string faction)
        {
            faction = string.Empty;
            if (factionOwnership == null)
            {
                return false;
            }

            var memberNames = new[] { "Faction", "faction", "FactionOwnership", "factionOwnership", "Owner", "owner", "Type", "type" };
            if (TryReadReplicationBuildStringMember(factionOwnership, memberNames, out faction))
            {
                return true;
            }

            faction = factionOwnership.ToString() ?? string.Empty;
            faction = faction.Trim();
            return faction.Length > 0;
        }

        private static bool TryReadReplicationBuildStringMember(object owner, string[] memberNames, out string value)
        {
            value = string.Empty;
            for (var i = 0; i < memberNames.Length; i++)
            {
                if (!TryReadInstanceMemberValue(owner, memberNames[i], out var memberValue) || memberValue == null)
                {
                    continue;
                }

                var candidate = Convert.ToString(memberValue, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    value = candidate.Trim();
                    return true;
                }
            }

            return false;
        }

        private sealed class ReplicationPendingBuildIntent
        {
            public ReplicationPendingBuildIntent(string blueprintId, int x, int y, int z, int angleY, string buildingType, string factionOwnership, float capturedRealtime)
            {
                BlueprintId = blueprintId;
                X = x;
                Y = y;
                Z = z;
                AngleY = angleY;
                BuildingType = buildingType;
                FactionOwnership = factionOwnership;
                CapturedRealtime = capturedRealtime;
            }

            public string BlueprintId { get; }

            public int X { get; }

            public int Y { get; }

            public int Z { get; }

            public int AngleY { get; }

            public string BuildingType { get; }

            public string FactionOwnership { get; }

            public float CapturedRealtime { get; }
        }
    }
}
