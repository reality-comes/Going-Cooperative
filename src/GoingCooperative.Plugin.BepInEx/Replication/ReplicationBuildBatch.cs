using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using GoingCooperative.Core;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const int ReplicationBuildBatchMaxPlacements = 512;
        // A signed coordinate can consume eight characters. At 512 positions the
        // canonical roof record remains below the shared 16 KiB record ceiling even
        // at worst-case coordinate widths; 513+ must be rejected, never truncated.
        private const int ReplicationBuildRoofMaxPositions = 512;
        private const string ReplicationBuildingBlueprintBatchPlacedDeltaKind = "BuildingBlueprintBatchPlaced";
        private const string ReplicationBuildingBlueprintBatchResultDeltaKind = "BuildingBlueprintBatchResult";
        private static bool replicationBuildBatchReplayDisabled;
        private static BuildingTransactionApplyLedger replicationBuildingTransactionApplyLedger =
            new BuildingTransactionApplyLedger(65536);

        private static long GetReplicationBuildBatchEpoch()
        {
            var current = instance;
            return ReferenceEquals(current, null)
                ? 0L
                : Math.Max(0, current.multiplayerSaveTransfer.Epoch);
        }

        private static void ResetReplicationBuildTransactionLedger()
        {
            replicationBuildingTransactionApplyLedger = new BuildingTransactionApplyLedger(65536);
        }

        private static bool TryBeginReplicationBuildTransaction(
            string transactionId,
            out bool alreadyApplied,
            out string detail)
        {
            alreadyApplied = false;
            var disposition = replicationBuildingTransactionApplyLedger.Begin(
                GetReplicationBuildBatchEpoch(),
                transactionId);
            if (disposition == BuildingTransactionBeginDisposition.Apply)
            {
                detail = "ok";
                return true;
            }

            if (disposition == BuildingTransactionBeginDisposition.AlreadyApplied)
            {
                alreadyApplied = true;
                detail = "ok building-transaction-already-applied transaction=" + transactionId;
                return true;
            }

            detail = "building-transaction-begin-rejected disposition="
                + disposition.ToString()
                + " transaction="
                + transactionId;
            return false;
        }

        private static bool CommitReplicationBuildTransaction(string transactionId)
        {
            var disposition = replicationBuildingTransactionApplyLedger.Commit(
                GetReplicationBuildBatchEpoch(),
                transactionId);
            return disposition == BuildingTransactionCommitDisposition.Committed
                || disposition == BuildingTransactionCommitDisposition.AlreadyCommitted;
        }

        private static void AbortReplicationBuildTransaction(string transactionId)
        {
            replicationBuildingTransactionApplyLedger.Abort(
                GetReplicationBuildBatchEpoch(),
                transactionId);
        }

        private static string CreateReplicationBuildBatchWirePayload(
            string blueprintId,
            string buildingType,
            string faction,
            IList<ReplicationCapturedBuildPlacement> placements)
        {
            var records = new string[placements.Count];
            for (var i = 0; i < placements.Count; i++)
            {
                records[i] = FormatReplicationBuildPlacementRecord(placements[i].Record);
            }

            return LockstepCommandPayloads.CreateBuildBatchPayload(
                blueprintId,
                buildingType,
                faction,
                afterLoading: false,
                records,
                GetReplicationBuildBatchEpoch());
        }

        private static string FormatReplicationBuildBatchIds(IList<ReplicationCapturedBuildPlacement> placements)
        {
            var value = new StringBuilder(Math.Max(16, placements.Count * 12));
            for (var i = 0; i < placements.Count; i++)
            {
                if (i > 0)
                {
                    value.Append(',');
                }

                value.Append(placements[i].UniqueId.ToString(CultureInfo.InvariantCulture));
            }

            return value.ToString();
        }

        private static bool TryParseReplicationBuildBatchIds(
            string value,
            int expectedCount,
            bool requirePositive,
            out long[] ids)
        {
            ids = Array.Empty<long>();
            var values = value.Split(new[] { ',' }, StringSplitOptions.None);
            if (expectedCount <= 0 || values.Length != expectedCount || expectedCount > ReplicationBuildBatchMaxPlacements)
            {
                return false;
            }

            ids = new long[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (!long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out ids[i])
                    || (requirePositive ? ids[i] <= 0L : ids[i] < 0L))
                {
                    ids = Array.Empty<long>();
                    return false;
                }
            }

            return true;
        }

        private static string FormatReplicationBuildPlacementRecord(ReplicationBuildPlacementRecord record)
        {
            var value = new StringBuilder(record.IsRoof ? 128 : 48);
            value.Append(record.IsRoof ? 'R' : 'N');
            value.Append(',').Append(record.X.ToString(CultureInfo.InvariantCulture));
            value.Append(',').Append(record.Y.ToString(CultureInfo.InvariantCulture));
            value.Append(',').Append(record.Z.ToString(CultureInfo.InvariantCulture));
            value.Append(',').Append(record.AngleY.ToString(CultureInfo.InvariantCulture));
            if (!record.IsRoof)
            {
                return value.ToString();
            }

            value.Append(',').Append(record.ScaleX.ToString(CultureInfo.InvariantCulture));
            value.Append(',').Append(record.ScaleY.ToString(CultureInfo.InvariantCulture));
            value.Append(',').Append(record.ScaleZ.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < record.Positions.Count; i++)
            {
                var position = record.Positions[i];
                value.Append(';').Append(position.X.ToString(CultureInfo.InvariantCulture));
                value.Append(',').Append(position.Y.ToString(CultureInfo.InvariantCulture));
                value.Append(',').Append(position.Z.ToString(CultureInfo.InvariantCulture));
            }

            return value.ToString();
        }

        private static string FormatReplicationCanonicalBuildPlacementRecord(ReplicationBuildPlacementRecord record)
        {
            if (!record.IsRoof || record.Positions.Count < 2)
            {
                return FormatReplicationBuildPlacementRecord(record);
            }

            var positions = new List<ReplicationBuildGridPosition>(record.Positions);
            positions.Sort(CompareReplicationBuildGridPositions);
            return FormatReplicationBuildPlacementRecord(new ReplicationBuildPlacementRecord(
                true,
                record.X,
                record.Y,
                record.Z,
                record.AngleY,
                record.ScaleX,
                record.ScaleY,
                record.ScaleZ,
                positions));
        }

        private static bool ReplicationBuildPlacementRecordsMatch(
            ReplicationBuildPlacementRecord left,
            ReplicationBuildPlacementRecord right)
        {
            return string.Equals(
                FormatReplicationCanonicalBuildPlacementRecord(left),
                FormatReplicationCanonicalBuildPlacementRecord(right),
                StringComparison.Ordinal);
        }

        private static int CompareReplicationBuildGridPositions(
            ReplicationBuildGridPosition left,
            ReplicationBuildGridPosition right)
        {
            var compare = left.X.CompareTo(right.X);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.Y.CompareTo(right.Y);
            return compare != 0 ? compare : left.Z.CompareTo(right.Z);
        }

        private static bool TryParseReplicationBuildPlacementRecord(
            string value,
            out ReplicationBuildPlacementRecord? record,
            out string detail)
        {
            record = null;
            detail = string.Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 16384)
            {
                detail = "build-record-empty-or-oversized";
                return false;
            }

            var segments = value.Split(new[] { ';' }, StringSplitOptions.None);
            var header = segments[0].Split(new[] { ',' }, StringSplitOptions.None);
            var isRoof = header.Length == 8 && string.Equals(header[0], "R", StringComparison.Ordinal);
            if ((!isRoof && (header.Length != 5 || !string.Equals(header[0], "N", StringComparison.Ordinal)))
                || !TryParseReplicationBuildRecordInt(header[1], out var x)
                || !TryParseReplicationBuildRecordInt(header[2], out var y)
                || !TryParseReplicationBuildRecordInt(header[3], out var z)
                || !TryParseReplicationBuildRecordInt(header[4], out var angleY))
            {
                detail = "build-record-header-invalid";
                return false;
            }

            var scaleX = 1;
            var scaleY = 1;
            var scaleZ = 1;
            if (isRoof
                && (!TryParseReplicationBuildRecordInt(header[5], out scaleX)
                    || !TryParseReplicationBuildRecordInt(header[6], out scaleY)
                    || !TryParseReplicationBuildRecordInt(header[7], out scaleZ)
                    || (scaleX == 0 && scaleY == 0 && scaleZ == 0)))
            {
                detail = "build-record-roof-scale-invalid";
                return false;
            }

            var positions = new List<ReplicationBuildGridPosition>(Math.Max(0, segments.Length - 1));
            if (isRoof
                && !ReplicationOrderingPolicy.IsValidBuildRoofPositionCount(
                    segments.Length - 1,
                    ReplicationBuildRoofMaxPositions))
            {
                detail = segments.Length <= 1
                    ? "build-record-roof-topology-empty"
                    : "build-record-roof-positions-over-cap";
                return false;
            }

            for (var i = 1; i < segments.Length; i++)
            {
                var coordinates = segments[i].Split(new[] { ',' }, StringSplitOptions.None);
                if (coordinates.Length != 3
                    || !TryParseReplicationBuildRecordInt(coordinates[0], out var positionX)
                    || !TryParseReplicationBuildRecordInt(coordinates[1], out var positionY)
                    || !TryParseReplicationBuildRecordInt(coordinates[2], out var positionZ))
                {
                    detail = "build-record-roof-position-invalid index=" + (i - 1).ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                positions.Add(new ReplicationBuildGridPosition(positionX, positionY, positionZ));
            }

            if (!isRoof && positions.Count != 0)
            {
                detail = "build-record-normal-has-roof-positions";
                return false;
            }

            record = new ReplicationBuildPlacementRecord(
                isRoof,
                x,
                y,
                z,
                NormalizeReplicationBuildAngle(angleY),
                scaleX,
                scaleY,
                scaleZ,
                positions);
            detail = "ok";
            return true;
        }

        private static bool TryParseReplicationBuildPlacementRecords(
            string[] values,
            out List<ReplicationBuildPlacementRecord> records,
            out string detail)
        {
            records = new List<ReplicationBuildPlacementRecord>();
            detail = string.Empty;
            if (values == null || values.Length == 0 || values.Length > ReplicationBuildBatchMaxPlacements)
            {
                detail = "build-batch-count-invalid";
                return false;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Length; i++)
            {
                if (!TryParseReplicationBuildPlacementRecord(values[i], out var record, out var recordDetail) || record == null)
                {
                    detail = "build-batch-record-invalid index="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + recordDetail;
                    return false;
                }

                var key = record.IsRoof.ToString()
                    + "|"
                    + record.X.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + record.Y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + record.Z.ToString(CultureInfo.InvariantCulture)
                    + "|"
                    + record.AngleY.ToString(CultureInfo.InvariantCulture);
                if (!keys.Add(key))
                {
                    detail = "build-batch-record-duplicate index=" + i.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                records.Add(record);
            }

            detail = "ok count=" + records.Count.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static int NormalizeReplicationBuildAngle(int angleY)
        {
            var normalized = angleY % 360;
            return normalized < 0 ? normalized + 360 : normalized;
        }

        private static bool TryParseReplicationBuildRecordInt(string value, out int number)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                && number >= -1048576
                && number <= 1048576;
        }

        private static bool TryApplyReplicationBuildBatchAuthoritative(
            string blueprintId,
            string factionOwnership,
            List<ReplicationBuildPlacementRecord> records,
            out string detail)
        {
            detail = string.Empty;
            replicationLastAuthoritativeBuildApplyCapture = null;
            if (replicationBuildBatchReplayDisabled)
            {
                detail = "build-batch-replay-disabled-after-placement-state-restore-failure";
                return false;
            }

            if (records == null || records.Count == 0 || records.Count > ReplicationBuildBatchMaxPlacements)
            {
                detail = "build-batch-count-invalid";
                return false;
            }

            try
            {
                var placementManagerType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingPlacementManager");
                var buildingsPoolType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsPool");
                var blueprintType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingBlueprint");
                var factionOwnershipType = AccessTools.TypeByName("NSMedieval.Village.FactionOwnership");
                var vec3IntType = AccessTools.TypeByName("NSMedieval.Vec3Int");
                if (placementManagerType == null
                    || buildingsPoolType == null
                    || blueprintType == null
                    || factionOwnershipType == null
                    || vec3IntType == null)
                {
                    detail = "build-batch-required-type-missing";
                    return false;
                }

                var placementManager = buildingPlacementManagerInstance ?? ResolveReplicationUnityManagerInstance(placementManagerType);
                var buildingsPool = buildingsPoolInstance ?? ResolveReplicationUnityManagerInstance(buildingsPoolType);
                if (placementManager == null || buildingsPool == null)
                {
                    detail = "build-batch-manager-missing";
                    return false;
                }

                var getBuildableBase = AccessTools.Method(buildingsPoolType, "GetBuildableBase", new[] { typeof(string) });
                var spawnFromPool = AccessTools.Method(
                    placementManagerType,
                    "SpawnFromPool",
                    new[] { blueprintType, vec3IntType, typeof(int), factionOwnershipType });
                var mouseUpSpawn = AccessTools.Method(
                    placementManagerType,
                    "MouseUpSpawnInitializeBuildings",
                    new[] { typeof(int) });
                var spawnRoofAutoTesting = AccessTools.Method(
                    placementManagerType,
                    "SpawnRoofAutoTesting",
                    new[]
                    {
                        blueprintType,
                        vec3IntType,
                        typeof(int),
                        vec3IntType,
                        typeof(List<>).MakeGenericType(vec3IntType)
                    });
                if (getBuildableBase == null || spawnFromPool == null || mouseUpSpawn == null || spawnRoofAutoTesting == null)
                {
                    detail = "build-batch-required-method-missing get="
                        + (getBuildableBase != null)
                        + " spawn="
                        + (spawnFromPool != null)
                        + " finalize="
                        + (mouseUpSpawn != null)
                        + " roof="
                        + (spawnRoofAutoTesting != null);
                    return false;
                }

                var blueprint = getBuildableBase.Invoke(buildingsPool, new object[] { blueprintId });
                if (blueprint == null)
                {
                    detail = "build-batch-blueprint-missing id=" + blueprintId;
                    return false;
                }

                if (IsUnsupportedReplicationBuildBlueprint(blueprint, out var unsupportedCategory))
                {
                    detail = "build-batch-unsupported-semantic-category=" + unsupportedCategory;
                    return false;
                }

                if (!TryParseReplicationBuildFactionOwnership(
                        factionOwnershipType,
                        factionOwnership,
                        out var parsedFactionOwnership,
                        out var factionDetail)
                    || parsedFactionOwnership == null)
                {
                    detail = factionDetail;
                    return false;
                }

                using (var resultCapture = new ReplicationAuthoritativeBuildApplyCapture(blueprintId, records))
                using (var state = new ReplicationPlacementManagerStateScope(placementManager))
                {
                    if (!state.IsolateCollection("buildingsDictionary", out var buildingsDictionary)
                        // roofPositionView contains RoofViewComponent aliases for the
                        // BaseBuildingViewComponent values owned by buildingsDictionary.
                        // Tracking both identities makes committed roofs look uncommitted
                        // during cleanup and can dispose a successful native commit.
                        || !state.IsolateCollection("roofPositionView", out _, trackValuesForCleanup: false)
                        || !state.IsolateCollection("buildingsToAutoConstruct", out _)
                        // MouseUpSpawnInitializeBuildings performs a live raycast to sort
                        // dragged views. Give it a transaction-owned output buffer so the
                        // current interactive placement's hit cache remains untouched.
                        || !state.IsolateArray("raycastHits", out _)
                        || !state.Capture("roofComponentBlueprint")
                        || !state.Capture("previewAngle")
                        || !state.Capture("raycastGridStart")
                        || !state.Capture("raycastGridCurrent")
                        || !state.Capture("raycastGridPrevious")
                        || !state.Capture("ray")
                        || !state.Capture("hit")
                        || !state.Capture("tempSide")
                        || !state.Capture("hitSide")
                        || !state.Capture("adjustedWorldPosition")
                        || !state.Capture("showCantDigTopLayer")
                        || !state.Capture("pooledObjectLayer"))
                    {
                        detail = "build-batch-isolation-collection-missing";
                        return false;
                    }

                    if (!state.Set("autoconstruct", false)
                        || !state.Set("userForceMerlonRotation", false)
                        || !state.Set("baseBuildingBlueprint", blueprint))
                    {
                        detail = "build-batch-isolation-state-missing";
                        return false;
                    }
                    var placed = 0;
                    var rejected = 0;

                    var normalGroups = new Dictionary<int, List<int>>();
                    var normalGroupOrder = new List<int>();
                    for (var i = 0; i < records.Count; i++)
                    {
                        var record = records[i];
                        if (record.IsRoof)
                        {
                            continue;
                        }

                        if (!normalGroups.TryGetValue(record.AngleY, out var group))
                        {
                            group = new List<int>();
                            normalGroups.Add(record.AngleY, group);
                            normalGroupOrder.Add(record.AngleY);
                        }

                        group.Add(i);
                    }

                    for (var groupIndex = 0; groupIndex < normalGroupOrder.Count; groupIndex++)
                    {
                        ((IDictionary)buildingsDictionary).Clear();
                        var angleY = normalGroupOrder[groupIndex];
                        var group = normalGroups[angleY];
                        var firstPosition = CreateReplicationBuildVec3Int(vec3IntType, records[group[0]]);
                        var lastPosition = firstPosition;
                        state.Set("baseBuildingBlueprint", blueprint);
                        state.Set("previewAngle", angleY);
                        state.Set("raycastGridStart", firstPosition);
                        state.Set("raycastGridCurrent", firstPosition);
                        state.Set("raycastGridPrevious", firstPosition);

                        var groupSpawned = 0;
                        for (var i = 0; i < group.Count; i++)
                        {
                            var itemIndex = group[i];
                            var position = CreateReplicationBuildVec3Int(vec3IntType, records[itemIndex]);
                            lastPosition = position;
                            // This exact native overload TryAdds position -> view to the
                            // placement manager's current buildingsDictionary. Because
                            // that field is isolated above, the native finalize call sees
                            // precisely this transaction. Do not add the view a second time.
                            var view = spawnFromPool.Invoke(
                                placementManager,
                                new[] { blueprint, position, (object)angleY, parsedFactionOwnership });
                            if (view == null)
                            {
                                rejected++;
                                continue;
                            }

                            resultCapture.TrackSpawnedView(itemIndex, view);
                            groupSpawned++;
                        }

                        if (groupSpawned > 0)
                        {
                            state.Set("raycastGridCurrent", lastPosition);
                            mouseUpSpawn.Invoke(placementManager, new object[] { angleY });
                            placed += groupSpawned;
                        }
                    }

                    ((IDictionary)buildingsDictionary).Clear();
                    for (var i = 0; i < records.Count; i++)
                    {
                        var record = records[i];
                        if (!record.IsRoof)
                        {
                            continue;
                        }

                        var position = CreateReplicationBuildVec3Int(vec3IntType, record);
                        var scale = CreateReplicationBuildVec3Int(
                            vec3IntType,
                            record.ScaleX,
                            record.ScaleY,
                            record.ScaleZ);
                        var positions = Activator.CreateInstance(typeof(List<>).MakeGenericType(vec3IntType));
                        if (!(positions is IList positionList))
                        {
                            rejected++;
                            continue;
                        }

                        for (var positionIndex = 0; positionIndex < record.Positions.Count; positionIndex++)
                        {
                            var roofPosition = record.Positions[positionIndex];
                            positionList.Add(CreateReplicationBuildVec3Int(
                                vec3IntType,
                                roofPosition.X,
                                roofPosition.Y,
                                roofPosition.Z));
                        }

                        state.Set("baseBuildingBlueprint", blueprint);
                        resultCapture.SetCurrentRoofItemIndex(i);
                        try
                        {
                            spawnRoofAutoTesting.Invoke(
                                placementManager,
                                new[] { blueprint, position, (object)record.AngleY, scale, positions });
                        }
                        finally
                        {
                            resultCapture.ClearCurrentRoofItemIndex();
                        }

                        placed++;
                    }

                    resultCapture.TrackUncommittedViews(buildingsDictionary);
                    // Restore every isolated manager field before deciding atomic
                    // success or rolling anything back. Each state restorer first adds
                    // transaction-owned values from its isolated collection to the
                    // capture, so rollback covers objects reachable outside the primary
                    // buildings dictionary as well.
                    state.Dispose();
                    resultCapture.Dispose();
                    var verified = resultCapture.CommittedCount;
                    var fullyCommitted = ReplicationOrderingPolicy.ShouldAcceptBuildBatch(
                        verified,
                        records.Count);
                    var rollbackDetail = "not-required";
                    if (!fullyCommitted)
                    {
                        var rollbackComplete = resultCapture.TryRollbackAllTransactionViews(out rollbackDetail);
                        if (ReplicationOrderingPolicy.ShouldLatchBuildBatchRecovery(
                                verified,
                                records.Count,
                                rollbackAttempted: true,
                                rollbackProven: rollbackComplete))
                        {
                            LatchReplicationBuildBatchRollbackFailure(
                                "authoritative-batch-partial-rollback-failed",
                                rollbackDetail);
                        }
                    }

                    detail = "build-batch-applied requested="
                        + records.Count.ToString(CultureInfo.InvariantCulture)
                        + " spawned="
                        + placed.ToString(CultureInfo.InvariantCulture)
                        + " verified="
                        + verified.ToString(CultureInfo.InvariantCulture)
                        + " rejected="
                        + rejected.ToString(CultureInfo.InvariantCulture)
                        + " fullyCommitted="
                        + (fullyCommitted ? "yes" : "no")
                        + " rollback="
                        + FormatReplicationWorldObjectDetailToken(rollbackDetail)
                        + (!fullyCommitted && !resultCapture.RollbackProven
                            ? " fullResyncRequired=yes"
                            : string.Empty)
                        + " blueprintId="
                        + blueprintId;
                    return fullyCommitted;
                }
            }
            catch (Exception ex)
            {
                var restoreFailure = ex.ToString().IndexOf(
                    "build-batch-placement-state-restore-failed",
                    StringComparison.Ordinal) >= 0;
                if (restoreFailure)
                {
                    replicationBuildBatchReplayDisabled = true;
                }

                var applyCapture = replicationLastAuthoritativeBuildApplyCapture;
                var committed = applyCapture?.CommittedCount ?? 0;
                var requested = applyCapture?.RequestedCount ?? records.Count;
                var fullyCommitted = ReplicationOrderingPolicy.ShouldAcceptBuildBatch(
                    committed,
                    requested);
                var rollbackDetail = "not-required";
                var rollbackProven = true;
                if (!fullyCommitted && applyCapture != null)
                {
                    rollbackProven = applyCapture.TryRollbackAllTransactionViews(out rollbackDetail);
                    if (ReplicationOrderingPolicy.ShouldLatchBuildBatchRecovery(
                            committed,
                            requested,
                            rollbackAttempted: true,
                            rollbackProven: rollbackProven))
                    {
                        LatchReplicationBuildBatchRollbackFailure(
                            "authoritative-batch-exception-rollback-failed",
                            rollbackDetail);
                    }
                }
                detail = "build-batch-error "
                    + FormatReflectionExceptionDetail(ex)
                    + " committed="
                    + committed.ToString(CultureInfo.InvariantCulture)
                    + "/"
                    + requested.ToString(CultureInfo.InvariantCulture)
                    + " fullyCommitted="
                    + (fullyCommitted ? "yes" : "no")
                    + " rollback="
                    + FormatReplicationWorldObjectDetailToken(rollbackDetail)
                    + (!rollbackProven ? " fullResyncRequired=yes" : string.Empty)
                    + (restoreFailure ? " replayDisabled=yes" : string.Empty);

                // Acceptance is whole-transaction truth. A throw after every exact
                // canonical commit remains accepted; a partial native mutation is first
                // rolled back and is rejected even though the durable result manifest is
                // still published for client reconciliation/recovery.
                return fullyCommitted;
            }
        }

        private static void LatchReplicationBuildBatchRollbackFailure(string reason, string detail)
        {
            replicationBuildBatchReplayDisabled = true;
            ScheduleReplicationBuildBatchRecovery(reason);
            instance?.LogReplicationWarning("Going Cooperative BuildBatch rollback could not be proven reason="
                + reason
                + " detail="
                + FormatReplicationWorldObjectDetailToken(detail)
                + " replayDisabled=yes fullResyncRequired=yes");
        }

        private static object CreateReplicationBuildVec3Int(Type vec3IntType, ReplicationBuildPlacementRecord record)
        {
            return CreateReplicationBuildVec3Int(vec3IntType, record.X, record.Y, record.Z);
        }

        private static object CreateReplicationBuildVec3Int(Type vec3IntType, int x, int y, int z)
        {
            return Activator.CreateInstance(vec3IntType, new object[] { x, y, z })
                ?? throw new InvalidOperationException("Vec3Int construction returned null.");
        }

        private static bool TryResolveReplicationBuildBlueprintIsRoof(string blueprintId)
        {
            try
            {
                var buildingsPoolType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsPool");
                var pool = buildingsPoolInstance
                    ?? (buildingsPoolType == null ? null : ResolveReplicationUnityManagerInstance(buildingsPoolType));
                var getBuildableBase = buildingsPoolType == null
                    ? null
                    : AccessTools.Method(buildingsPoolType, "GetBuildableBase", new[] { typeof(string) });
                var blueprint = pool == null || getBuildableBase == null
                    ? null
                    : getBuildableBase.Invoke(pool, new object[] { blueprintId });
                if (blueprint == null)
                {
                    return false;
                }

                if (TryReadInstanceMemberValue(blueprint, "ConstructableBaseCategory", out var category)
                    && category != null
                    && string.Equals(category.ToString(), "Roof", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return TryReadInstanceMemberValue(blueprint, "PlacementType", out var placementType)
                    && placementType != null
                    && string.Equals(placementType.ToString(), "Roof", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private sealed class ReplicationPlacementManagerStateScope : IDisposable
        {
            private readonly object manager;
            private readonly List<ReplicationPlacementStateRestorer> restorers = new List<ReplicationPlacementStateRestorer>();
            private readonly HashSet<FieldInfo> captured = new HashSet<FieldInfo>();
            private bool restored;

            public ReplicationPlacementManagerStateScope(object manager)
            {
                this.manager = manager;
            }

            public bool IsolateCollection(
                string fieldName,
                out object collection,
                bool trackValuesForCleanup = true)
            {
                collection = null!;
                var field = GetCachedInstanceField(manager.GetType(), fieldName);
                if (field == null)
                {
                    return false;
                }

                var value = field.GetValue(manager);
                if (value == null)
                {
                    return false;
                }

                object? isolated;
                try
                {
                    isolated = Activator.CreateInstance(value.GetType());
                }
                catch
                {
                    return false;
                }

                if (isolated == null
                    || (value is IDictionary && !(isolated is IDictionary))
                    || (value is IList && !(isolated is IList))
                    || (!(value is IDictionary) && !(value is IList)))
                {
                    return false;
                }

                var applyCapture = replicationActiveAuthoritativeBuildApplyCapture;
                Capture(
                    field,
                    trackValuesForCleanup
                        ? () => applyCapture?.TrackUncommittedViews(isolated)
                        : null);
                field.SetValue(manager, isolated);
                collection = isolated;
                return true;
            }

            public bool IsolateArray(string fieldName, out Array array)
            {
                array = null!;
                var field = GetCachedInstanceField(manager.GetType(), fieldName);
                if (field == null || !(field.GetValue(manager) is Array original) || original.Rank != 1)
                {
                    return false;
                }

                var elementType = original.GetType().GetElementType();
                if (elementType == null)
                {
                    return false;
                }

                var isolated = Array.CreateInstance(elementType, original.Length);
                Capture(field);
                field.SetValue(manager, isolated);
                array = isolated;
                return true;
            }

            public bool Capture(string fieldName)
            {
                var field = GetCachedInstanceField(manager.GetType(), fieldName);
                if (field == null)
                {
                    return false;
                }

                Capture(field);
                return true;
            }

            public bool Set(string fieldName, object? value)
            {
                var field = GetCachedInstanceField(manager.GetType(), fieldName);
                if (field == null)
                {
                    return false;
                }

                Capture(field);
                field.SetValue(manager, value);
                return true;
            }

            public void Dispose()
            {
                if (restored)
                {
                    return;
                }

                restored = true;
                var failures = new List<string>();
                for (var i = restorers.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        restorers[i].Restore();
                    }
                    catch (Exception ex)
                    {
                        failures.Add(restorers[i].FieldName
                            + ":"
                            + ex.GetType().Name
                            + ":"
                            + ex.Message);
                    }
                }

                if (failures.Count > 0)
                {
                    throw new InvalidOperationException(
                        "build-batch-placement-state-restore-failed fields="
                        + string.Join(",", failures.ToArray()));
                }
            }

            private void Capture(FieldInfo field)
            {
                Capture(field, null);
            }

            private void Capture(FieldInfo field, Action? beforeRestore)
            {
                if (captured.Add(field))
                {
                    var value = field.GetValue(manager);
                    restorers.Add(new ReplicationPlacementStateRestorer(
                        field.Name,
                        () =>
                        {
                            Exception? beforeRestoreFailure = null;
                            try
                            {
                                beforeRestore?.Invoke();
                            }
                            catch (Exception ex)
                            {
                                beforeRestoreFailure = ex;
                            }

                            // Restoring the manager reference is mandatory even when
                            // transaction-owned cleanup capture itself has a fault.
                            field.SetValue(manager, value);
                            if (beforeRestoreFailure != null)
                            {
                                throw new InvalidOperationException(
                                    "placement-state-pre-restore-capture-failed",
                                    beforeRestoreFailure);
                            }
                        }));
                }
            }

            private sealed class ReplicationPlacementStateRestorer
            {
                private readonly Action restore;

                public ReplicationPlacementStateRestorer(string fieldName, Action restore)
                {
                    FieldName = fieldName;
                    this.restore = restore;
                }

                public string FieldName { get; }

                public void Restore()
                {
                    restore();
                }
            }
        }

        private sealed class ReplicationBuildPlacementRecord
        {
            public ReplicationBuildPlacementRecord(
                bool isRoof,
                int x,
                int y,
                int z,
                int angleY,
                int scaleX,
                int scaleY,
                int scaleZ,
                List<ReplicationBuildGridPosition>? positions = null)
            {
                IsRoof = isRoof;
                X = x;
                Y = y;
                Z = z;
                AngleY = angleY;
                ScaleX = scaleX;
                ScaleY = scaleY;
                ScaleZ = scaleZ;
                Positions = positions ?? new List<ReplicationBuildGridPosition>();
            }

            public bool IsRoof { get; }
            public int X { get; }
            public int Y { get; }
            public int Z { get; }
            public int AngleY { get; }
            public int ScaleX { get; }
            public int ScaleY { get; }
            public int ScaleZ { get; }
            public List<ReplicationBuildGridPosition> Positions { get; }
        }

        private readonly struct ReplicationBuildGridPosition
        {
            public ReplicationBuildGridPosition(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public int X { get; }
            public int Y { get; }
            public int Z { get; }
        }
    }
}
