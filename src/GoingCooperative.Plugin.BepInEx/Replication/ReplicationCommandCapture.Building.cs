using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static ReplicationBuildCaptureTransaction? replicationActiveBuildCaptureTransaction;
        private static ReplicationAuthoritativeBuildApplyCapture? replicationActiveAuthoritativeBuildApplyCapture;
        private static ReplicationAuthoritativeBuildApplyCapture? replicationLastAuthoritativeBuildApplyCapture;
        private static readonly Dictionary<string, ReplicationProvisionalBuildView> ReplicationProvisionalBuildViews =
            new Dictionary<string, ReplicationProvisionalBuildView>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ReplicationBuildBatchReplayFailures =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static long replicationBuildCaptureTransactionSequence;
        private static bool replicationBuildBatchRecoveryRequested;
        private static bool replicationBuildBatchRecoveryAttempted;
        private static string replicationBuildBatchRecoveryReason = string.Empty;
        private const int ReplicationBuildBatchReplayMaxFailures = 8;

        private static readonly string[] ReplicationBuildBlueprintIdMemberNames =
        {
            "BlueprintId", "blueprintId", "ObjectId", "objectId", "ProtoId", "protoId",
            "GroupIdentifier", "groupIdentifier", "Id", "id", "ID", "Name", "name"
        };

        private static readonly string[] ReplicationBuildTypeMemberNames =
        {
            "BuildingType", "buildingType", "BuildingTypes", "buildingTypes", "Type", "type"
        };

        private int TryInstallReplicationBuildCommandCapture(Harmony harmonyInstance)
        {
            try
            {
                var placementManagerType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingPlacementManager");
                var viewComponentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
                var onLeftMouseUp = placementManagerType == null
                    ? null
                    : AccessTools.Method(placementManagerType, "OnLeftMouseUp", Type.EmptyTypes);
                var objectPlacedOnMap = placementManagerType == null || viewComponentType == null
                    ? null
                    : AccessTools.Method(placementManagerType, "ObjectPlacedOnMap", new[] { viewComponentType });
                if (placementManagerType == null || viewComponentType == null || onLeftMouseUp == null || objectPlacedOnMap == null)
                {
                    AppendPluginLog("Replication build transaction surfaces missing placementManager="
                        + (placementManagerType != null)
                        + " view="
                        + (viewComponentType != null)
                        + " onLeftMouseUp="
                        + (onLeftMouseUp != null)
                        + " objectPlaced="
                        + (objectPlacedOnMap != null));
                    return 0;
                }

                var patched = 0;
                if (!IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode))
                {
                    harmonyInstance.Patch(
                        onLeftMouseUp,
                        prefix: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                            nameof(ReplicationBuildOnLeftMouseUpPrefix),
                            BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                            nameof(ReplicationBuildOnLeftMouseUpPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                            nameof(ReplicationBuildOnLeftMouseUpFinalizer),
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    patched++;
                }
                harmonyInstance.Patch(
                    objectPlacedOnMap,
                    prefix: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                        nameof(ReplicationBuildObjectPlacedOnMapPrefix),
                        BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                        nameof(ReplicationBuildObjectPlacedOnMapPostfix),
                        BindingFlags.Static | BindingFlags.NonPublic)));

                patched++;
                AppendPluginLog("Replication build transaction surfaces patched: "
                    + placementManagerType.FullName
                    + ".OnLeftMouseUp + ObjectPlacedOnMap");
                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication build transaction capture patch failed "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return 0;
            }
        }

        private static void ReplicationBuildOnLeftMouseUpPrefix(out ReplicationBuildCaptureTransaction? __state)
        {
            __state = null;
            if (!ShouldCaptureReplicationBuildTransaction())
            {
                return;
            }

            CleanupReplicationProvisionalBuildViews(Time.realtimeSinceStartup);
            var transaction = new ReplicationBuildCaptureTransaction(++replicationBuildCaptureTransactionSequence);
            replicationActiveBuildCaptureTransaction = transaction;
            __state = transaction;
        }

        private static void ReplicationBuildObjectPlacedOnMapPrefix(object __0)
        {
            if (__0 == null)
            {
                return;
            }

            // ObjectPlacedOnMap mutates the world and can throw before its postfix.
            // Claim the exact view before that boundary so the outer drag finalizer
            // can still roll it back instead of stranding a client-only building.
            replicationActiveBuildCaptureTransaction?.TrackRawView(__0);
        }

        private static void ReplicationBuildObjectPlacedOnMapPostfix(object __0)
        {
            var transaction = replicationActiveBuildCaptureTransaction;
            var applyCapture = replicationActiveAuthoritativeBuildApplyCapture;
            if (__0 == null || (transaction == null && applyCapture == null))
            {
                return;
            }

            ReplicationCapturedBuildPlacement? placement;
            string detail;
            try
            {
                if (!TryCaptureReplicationCommittedBuilding(__0, out placement, out detail) || placement == null)
                {
                    transaction?.MarkCaptureFailed(detail);
                    instance?.LogReplicationWarning("Going Cooperative replication committed build ignored " + detail);
                    return;
                }
            }
            catch (Exception ex)
            {
                detail = "committed-build-capture-exception " + FormatReflectionExceptionDetail(ex);
                transaction?.MarkCaptureFailed(detail);
                instance?.LogReplicationWarning("Going Cooperative replication committed build ignored " + detail);
                return;
            }

            if (!placement.UnsupportedCategory)
            {
                applyCapture?.MarkCommitted(placement);
            }
            if (transaction != null)
            {
                if (placement.UnsupportedCategory)
                {
                    transaction.AddUnsupported(placement);
                }
                else
                {
                    transaction.Add(placement);
                }
            }
        }

        private static void ReplicationBuildOnLeftMouseUpPostfix(ReplicationBuildCaptureTransaction? __state)
        {
            if (__state == null)
            {
                return;
            }

            if (ReferenceEquals(replicationActiveBuildCaptureTransaction, __state))
            {
                replicationActiveBuildCaptureTransaction = null;
            }

            if (__state.HasCaptureFailure
                || __state.RawViews.Count != __state.Placements.Count + __state.UnsupportedPlacements.Count)
            {
                var rolledBack = RollbackReplicationRawBuildViews(__state, "build-capture-failed");
                if (rolledBack < __state.RawViews.Count)
                {
                    ScheduleReplicationBuildBatchRecovery("build-capture-rollback-incomplete");
                }
                ShowReplicationBuildPlacementFailedMessage();
                instance?.LogReplicationWarning("Going Cooperative replication build transaction capture failed id="
                    + __state.TransactionId.ToString(CultureInfo.InvariantCulture)
                    + " raw="
                    + __state.RawViews.Count.ToString(CultureInfo.InvariantCulture)
                    + " classified="
                    + (__state.Placements.Count + __state.UnsupportedPlacements.Count).ToString(CultureInfo.InvariantCulture)
                    + " rolledBack="
                    + rolledBack.ToString(CultureInfo.InvariantCulture)
                    + " detail="
                    + FormatReplicationWorldObjectDetailToken(__state.CaptureFailureDetail));
                __state.MarkFinalized();
                return;
            }

            var unsupportedRolledBack = RollbackUnsupportedReplicationBuildPlacements(__state);
            if (unsupportedRolledBack < __state.UnsupportedPlacements.Count)
            {
                RollbackReplicationRawBuildViews(__state, "unsupported-build-rollback-incomplete");
                ScheduleReplicationBuildBatchRecovery("unsupported-build-rollback-incomplete");
                ShowReplicationBuildPlacementFailedMessage();
                __state.MarkFinalized();
                return;
            }
            EmitReplicationCommittedBuildTransaction(__state);
            __state.MarkFinalized();
        }

        private static Exception? ReplicationBuildOnLeftMouseUpFinalizer(
            Exception? __exception,
            ReplicationBuildCaptureTransaction? __state)
        {
            if (__state != null && ReferenceEquals(replicationActiveBuildCaptureTransaction, __state))
            {
                replicationActiveBuildCaptureTransaction = null;
            }

            if (__state != null && __exception != null && !__state.IsFinalized)
            {
                var rolledBack = 0;
                try
                {
                    rolledBack = RollbackReplicationRawBuildViews(
                        __state,
                        "native-placement-exception");
                }
                catch (Exception rollbackException)
                {
                    instance?.LogReplicationWarning("Going Cooperative replication build transaction rollback failed id="
                        + __state.TransactionId.ToString(CultureInfo.InvariantCulture)
                        + " error="
                        + FormatReflectionExceptionDetail(rollbackException));
                }

                __state.MarkFinalized();
                ShowReplicationBuildPlacementFailedMessage();
                instance?.LogReplicationWarning("Going Cooperative replication build transaction aborted id="
                    + __state.TransactionId.ToString(CultureInfo.InvariantCulture)
                    + " captured="
                    + __state.RawViews.Count.ToString(CultureInfo.InvariantCulture)
                    + " rolledBack="
                    + rolledBack.ToString(CultureInfo.InvariantCulture)
                    + " reason=native-placement-exception error="
                    + FormatReflectionExceptionDetail(__exception));
            }

            return __exception;
        }

        private static bool TryCaptureReplicationCommittedBuilding(
            object view,
            out ReplicationCapturedBuildPlacement? placement,
            out string detail)
        {
            placement = null;
            detail = string.Empty;
            if (!TryResolveReplicationBuildingCandidateInstance(view, out var buildingInstance, out var instanceDetail)
                || buildingInstance == null)
            {
                detail = "committed-build-instance-missing " + instanceDetail;
                return false;
            }

            if (!TryReadInstanceMemberValue(buildingInstance, "Blueprint", out var blueprint) || blueprint == null)
            {
                detail = "committed-build-blueprint-missing";
                return false;
            }

            if (!TryExtractReplicationBuildPayloadFields(
                    blueprint,
                    "Player",
                    out var blueprintId,
                    out var buildingType,
                    out var faction,
                    out var payloadDetail)
                || !TryResolveReplicationBuildingCandidateGrid(view, out var x, out var y, out var z))
            {
                detail = "committed-build-fields-missing " + payloadDetail;
                return false;
            }

            var angleY = 0;
            if (view is Component component)
            {
                angleY = NormalizeReplicationBuildAngle(Mathf.RoundToInt(component.transform.eulerAngles.y));
            }
            else if (TryReadInstanceMemberValue(buildingInstance, "AdjustedAngle", out var adjustedAngle) && adjustedAngle != null)
            {
                angleY = NormalizeReplicationBuildAngle(Convert.ToInt32(adjustedAngle, CultureInfo.InvariantCulture));
            }

            var record = new ReplicationBuildPlacementRecord(false, x, y, z, angleY, 1, 1, 1);
            var unsupportedReason = string.Empty;
            if (TryCaptureReplicationRoofPlacement(view, record, out var roofRecord, out var roofDetail) && roofRecord != null)
            {
                record = roofRecord;
            }
            else if (roofDetail.StartsWith("roof-topology-", StringComparison.Ordinal))
            {
                unsupportedReason = roofDetail;
            }
            else if (!string.Equals(roofDetail, "not-roof", StringComparison.Ordinal))
            {
                detail = roofDetail;
                return false;
            }

            if (!TryReadReplicationWorldObjectLongMember(
                    buildingInstance,
                    "UniqueId",
                    "uniqueId",
                    out var uniqueId)
                || uniqueId <= 0L)
            {
                detail = "committed-build-unique-id-invalid";
                return false;
            }

            if (IsUnsupportedReplicationBuildBlueprint(blueprint, out var unsupportedCategory))
            {
                unsupportedReason = "semantic-category=" + unsupportedCategory;
            }
            placement = new ReplicationCapturedBuildPlacement(
                blueprintId,
                buildingType,
                faction,
                record,
                view,
                buildingInstance,
                uniqueId,
                unsupportedReason);
            detail = "ok";
            return true;
        }

        private static bool TryCaptureReplicationRoofPlacement(
            object view,
            ReplicationBuildPlacementRecord baseRecord,
            out ReplicationBuildPlacementRecord? roofRecord,
            out string detail)
        {
            roofRecord = null;
            detail = "not-roof";
            var roofViewType = AccessTools.TypeByName("NSMedieval.BuildingComponents.RoofViewComponent");
            if (roofViewType == null || !(view is Component component))
            {
                return false;
            }

            var roofView = component.GetComponent(roofViewType);
            if (roofView == null)
            {
                return false;
            }

            var scaleX = 1;
            var scaleY = 1;
            var scaleZ = 1;
            if (TryReadInstanceMemberValue(roofView, "GetScale", out var scaleValue) && scaleValue is Vector3 scale)
            {
                scaleX = Mathf.RoundToInt(scale.x);
                scaleY = Mathf.RoundToInt(scale.y);
                scaleZ = Mathf.RoundToInt(scale.z);
            }

            var positions = new List<ReplicationBuildGridPosition>();
            if (!TryReadInstanceMemberValue(roofView, "Positions", out var positionsValue)
                || !(positionsValue is IEnumerable enumerable))
            {
                detail = "roof-topology-unreadable";
                return false;
            }

            foreach (var position in enumerable)
            {
                if (position == null || !TryReadReplicationVec3Int(position, out var x, out var y, out var z))
                {
                    detail = "roof-topology-position-unreadable index="
                        + positions.Count.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (positions.Count >= ReplicationBuildRoofMaxPositions)
                {
                    detail = "roof-topology-over-cap max="
                        + ReplicationBuildRoofMaxPositions.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                positions.Add(new ReplicationBuildGridPosition(x, y, z));
            }

            if (!ReplicationOrderingPolicy.IsValidBuildRoofPositionCount(
                    positions.Count,
                    ReplicationBuildRoofMaxPositions))
            {
                detail = "roof-topology-empty";
                return false;
            }

            roofRecord = new ReplicationBuildPlacementRecord(
                true,
                baseRecord.X,
                baseRecord.Y,
                baseRecord.Z,
                baseRecord.AngleY,
                scaleX,
                scaleY,
                scaleZ,
                positions);
            detail = "ok roof positions=" + positions.Count.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static void EmitReplicationCommittedBuildTransaction(ReplicationBuildCaptureTransaction transaction)
        {
            if (transaction.Placements.Count == 0)
            {
                return;
            }

            if (replicationConfigHostMode
                && IsReplicationBuildingDurableBackpressured(out var durablePending))
            {
                var rolledBack = RollbackReplicationCapturedBuildPlacements(
                    transaction.Placements,
                    "building-v2-durable-backpressure");
                ShowReplicationBuildMessage(
                    "Going Cooperative: building is paused while the client catches up. No placement was committed; wait or use FULL RESYNC.");
                instance?.LogReplicationWarning(
                    "Going Cooperative host build rejected by durable backpressure pending="
                        + durablePending.ToString(CultureInfo.InvariantCulture)
                        + " rolledBack=" + rolledBack.ToString(CultureInfo.InvariantCulture));
                if (rolledBack < transaction.Placements.Count)
                {
                    ScheduleReplicationBuildBatchRecovery(
                        "host-building-backpressure-rollback-incomplete");
                }

                return;
            }

            if (!replicationConfigHostMode
                && IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode)
                && !ShouldSendReplicationLocalCommandIntent())
            {
                var rolledBack = RollbackReplicationCapturedBuildPlacements(
                    transaction.Placements,
                    "build-transport-not-ready");
                ShowReplicationBuildTransportNotReadyMessage();
                instance?.LogReplicationWarning("Going Cooperative multiplayer build transaction rejected while transport not ready id="
                    + transaction.TransactionId.ToString(CultureInfo.InvariantCulture)
                    + " captured="
                    + transaction.Placements.Count.ToString(CultureInfo.InvariantCulture)
                    + " rolledBack="
                    + rolledBack.ToString(CultureInfo.InvariantCulture)
                    + " reason=build-transport-not-ready");
                if (rolledBack < transaction.Placements.Count)
                {
                    ScheduleReplicationBuildBatchRecovery("build-transport-not-ready-rollback-incomplete");
                }
                return;
            }

            if (transaction.Placements.Count > ReplicationBuildBatchMaxPlacements)
            {
                var rolledBack = RollbackReplicationCapturedBuildPlacements(
                    transaction.Placements,
                    "build-transaction-over-cap");
                ShowReplicationBuildTransactionTooLargeMessage();
                instance?.LogReplicationWarning("Going Cooperative multiplayer build transaction rejected over cap requested="
                    + transaction.Placements.Count.ToString(CultureInfo.InvariantCulture)
                    + " cap="
                    + ReplicationBuildBatchMaxPlacements.ToString(CultureInfo.InvariantCulture)
                    + " rolledBack="
                    + rolledBack.ToString(CultureInfo.InvariantCulture));
                if (rolledBack < transaction.Placements.Count)
                {
                    ScheduleReplicationBuildBatchRecovery("build-over-cap-rollback-incomplete");
                }
                return;
            }

            var groups = new List<List<ReplicationCapturedBuildPlacement>>();
            var groupByKey = new Dictionary<string, List<ReplicationCapturedBuildPlacement>>(StringComparer.Ordinal);
            for (var i = 0; i < transaction.Placements.Count; i++)
            {
                var placement = transaction.Placements[i];
                var key = placement.BlueprintId + "|" + placement.BuildingType + "|" + placement.Faction;
                if (!groupByKey.TryGetValue(key, out var group))
                {
                    group = new List<ReplicationCapturedBuildPlacement>();
                    groupByKey.Add(key, group);
                    groups.Add(group);
                }

                group.Add(placement);
            }

            if (groups.Count != 1)
            {
                // OnLeftMouseUp is expected to commit one selected blueprint/faction.
                // If a future native branch produces mixed semantics, splitting it into
                // independently accepted commands would violate transaction atomicity.
                var rolledBack = RollbackReplicationCapturedBuildPlacements(
                    transaction.Placements,
                    "build-transaction-mixed-groups");
                ShowReplicationBuildPlacementFailedMessage();
                instance?.LogReplicationWarning("Going Cooperative multiplayer build transaction rejected mixed groups id="
                    + transaction.TransactionId.ToString(CultureInfo.InvariantCulture)
                    + " groups="
                    + groups.Count.ToString(CultureInfo.InvariantCulture)
                    + " rolledBack="
                    + rolledBack.ToString(CultureInfo.InvariantCulture));
                if (rolledBack < transaction.Placements.Count)
                {
                    ScheduleReplicationBuildBatchRecovery("build-mixed-groups-rollback-incomplete");
                }
                return;
            }

            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var first = group[0];
                var payload = CreateReplicationBuildBatchWirePayload(
                    first.BlueprintId,
                    first.BuildingType,
                    first.Faction,
                    group);
                var payloadBytes = Encoding.UTF8.GetByteCount(payload);
                if (payloadBytes <= LockstepCommandPayloads.BuildBatchPayloadMaxUtf8Bytes)
                {
                    continue;
                }

                // Validate every group before sending the first one. A native drag
                // must remain atomic even when its roof topology is too large for the
                // bidirectional command/result transport budget.
                var rolledBack = RollbackReplicationCapturedBuildPlacements(
                    transaction.Placements,
                    "build-transaction-wire-over-cap");
                ShowReplicationBuildTransactionWireTooLargeMessage();
                instance?.LogReplicationWarning("Going Cooperative multiplayer build transaction rejected wire over cap id="
                    + transaction.TransactionId.ToString(CultureInfo.InvariantCulture)
                    + " group="
                    + groupIndex.ToString(CultureInfo.InvariantCulture)
                    + " payloadBytes="
                    + payloadBytes.ToString(CultureInfo.InvariantCulture)
                    + " cap="
                    + LockstepCommandPayloads.BuildBatchPayloadMaxUtf8Bytes.ToString(CultureInfo.InvariantCulture)
                    + " rolledBack="
                    + rolledBack.ToString(CultureInfo.InvariantCulture));
                if (rolledBack < transaction.Placements.Count)
                {
                    ScheduleReplicationBuildBatchRecovery("build-wire-over-cap-rollback-incomplete");
                }
                return;
            }

            var emitted = 0;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                // One native drag is one semantic command. The transport layer owns
                // byte fragmentation/reassembly; splitting here would create multiple
                // independently accepted host transactions and break drag atomicity.
                if (replicationConfigHostMode)
                {
                    EmitHostLocalReplicationBuildPlacements(
                        group,
                        transaction.TransactionId,
                        groupIndex);
                }
                else
                {
                    EmitClientReplicationBuildBatch(group, transaction.TransactionId);
                }

                emitted += group.Count;
            }

            instance?.LogReplicationInfo("Going Cooperative replication build transaction committed id="
                + transaction.TransactionId.ToString(CultureInfo.InvariantCulture)
                + " placements="
                + transaction.Placements.Count.ToString(CultureInfo.InvariantCulture)
                + " emitted="
                + emitted.ToString(CultureInfo.InvariantCulture)
                + " groups="
                + groups.Count.ToString(CultureInfo.InvariantCulture)
                + " role="
                + (replicationConfigHostMode ? "host" : "client"));
        }

        private static void EmitClientReplicationBuildBatch(
            List<ReplicationCapturedBuildPlacement> placements,
            long transactionId)
        {
            if (placements.Count == 0 || !ShouldSendReplicationLocalCommandIntent())
            {
                return;
            }

            var first = placements[0];
            var records = new string[placements.Count];
            for (var i = 0; i < placements.Count; i++)
            {
                records[i] = FormatReplicationBuildPlacementRecord(placements[i].Record);
            }

            var command = new LockstepCommand(
                ReplicationClientPeerId,
                ++replicationIntentSequence,
                0L,
                CommandKind.Build,
                LockstepCommandPayloads.CreateBuildBatchPayload(
                    first.BlueprintId,
                    first.BuildingType,
                    first.Faction,
                    afterLoading: false,
                    records,
                    GetReplicationBuildBatchEpoch()),
                null,
                first.Record.X,
                first.Record.Y,
                first.Record.Z);

            if (IsReplicationCaptureModeShadow(replicationConfigCommandCaptureMode))
            {
                LogReplicationLocalCommandShadow(command, "build-transaction:" + transactionId.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (!IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode))
            {
                return;
            }

            for (var i = 0; i < placements.Count; i++)
            {
                ReplicationProvisionalBuildViews[BuildReplicationProvisionalBuildKey(command.Sequence, i)] =
                    new ReplicationProvisionalBuildView(placements[i].View, Time.realtimeSinceStartup);
            }

            SendReplicationLocalCommandIntent(
                command,
                "build-transaction:"
                    + transactionId.ToString(CultureInfo.InvariantCulture)
                    + " count="
                    + placements.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static void EmitHostLocalReplicationBuildPlacements(
            List<ReplicationCapturedBuildPlacement> placements,
            long transactionId,
            int groupIndex)
        {
            if (instance == null || !replicationRemoteHelloReceived)
            {
                return;
            }

            var first = placements[0];
            for (var i = 0; i < placements.Count; i++)
            {
                TrackReplicationBuildingLifecycleV2(placements[i], "host-local-building-v2");
            }

            var payload = CreateReplicationBuildBatchWirePayload(
                first.BlueprintId,
                first.BuildingType,
                first.Faction,
                placements);
            instance.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationBuildingBlueprintBatchPlacedDeltaKind,
                first.UniqueId,
                first.BlueprintId,
                first.Record.X,
                first.Record.Y,
                first.Record.Z,
                "payloadB64=" + EncodeReplicationDetailBase64(payload)
                    + " epoch=" + GetReplicationBuildBatchEpoch().ToString(CultureInfo.InvariantCulture)
                    + " transactionId=" + transactionId.ToString(CultureInfo.InvariantCulture)
                    + " group=" + groupIndex.ToString(CultureInfo.InvariantCulture)
                    + " ids=" + FormatReplicationBuildBatchIds(placements)
                    + " count=" + placements.Count.ToString(CultureInfo.InvariantCulture)
                    + " source=host-local-batch"));
        }

        private static bool TryGetReplicationProvisionalBuildView(long commandSequence, int itemIndex, out object? view)
        {
            view = null;
            var key = BuildReplicationProvisionalBuildKey(commandSequence, itemIndex);
            if (!ReplicationProvisionalBuildViews.TryGetValue(key, out var provisional))
            {
                return false;
            }

            var target = provisional.View.Target;
            if (IsReplicationBuildViewAlive(target))
            {
                view = target;
                return true;
            }

            ReplicationProvisionalBuildViews.Remove(key);
            return false;
        }

        private static void RemoveReplicationProvisionalBuildView(long commandSequence, int itemIndex)
        {
            ReplicationProvisionalBuildViews.Remove(BuildReplicationProvisionalBuildKey(commandSequence, itemIndex));
        }

        private static string BuildReplicationProvisionalBuildKey(long commandSequence, int itemIndex)
        {
            return commandSequence.ToString(CultureInfo.InvariantCulture)
                + "|"
                + itemIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static void CleanupReplicationProvisionalBuildViews(float now)
        {
            if (ReplicationProvisionalBuildViews.Count == 0)
            {
                return;
            }

            var expired = new List<string>();
            foreach (var pair in ReplicationProvisionalBuildViews)
            {
                var target = pair.Value.View.Target;
                if (!IsReplicationBuildViewAlive(target))
                {
                    expired.Add(pair.Key);
                    continue;
                }

                if (now - pair.Value.CapturedRealtime > 120f
                    && TryReadReplicationProvisionalBuildCommandSequence(pair.Key, out var commandSequence)
                    && !IsReplicationBuildBatchPending(commandSequence))
                {
                    // Never discard ownership of a live provisional view. If its command
                    // receipt vanished, only a full save reload can prove convergence.
                    ScheduleReplicationBuildBatchRecovery("live-provisional-without-receipt");
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ReplicationProvisionalBuildViews.Remove(expired[i]);
            }
        }

        private static bool IsReplicationBuildViewAlive(object? view)
        {
            if (ReferenceEquals(view, null))
            {
                return false;
            }

            // Unity retains a managed wrapper after its native object is destroyed.
            // A WeakReference can therefore resolve even though all view operations
            // would fail; respect Unity's overloaded null check as well.
            return view is not UnityEngine.Object unityObject || unityObject != null;
        }

        private static bool TryReadReplicationProvisionalBuildCommandSequence(string key, out long commandSequence)
        {
            commandSequence = 0L;
            var separator = key.IndexOf('|');
            return separator > 0
                && long.TryParse(
                    key.Substring(0, separator),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out commandSequence)
                && commandSequence > 0L;
        }

        private static bool IsReplicationBuildBatchPending(long commandSequence)
        {
            foreach (var pending in ReplicationPendingCommandIntents.Values)
            {
                if (pending.Command.Sequence == commandSequence
                    && IsReplicationBuildBatchCommand(pending.Command))
                {
                    return true;
                }
            }

            return false;
        }

        private static int CountReplicationProvisionalBuildViews(long commandSequence)
        {
            var count = 0;
            foreach (var key in ReplicationProvisionalBuildViews.Keys)
            {
                if (TryReadReplicationProvisionalBuildCommandSequence(key, out var parsedSequence)
                    && parsedSequence == commandSequence)
                {
                    count++;
                }
            }

            return count;
        }

        private static void ClearReplicationBuildBatchRuntimeState()
        {
            if (!replicationConfigHostMode && ReplicationPendingCommandIntents.Count > 0)
            {
                foreach (var pending in ReplicationPendingCommandIntents.Values)
                {
                    if (!pending.HostResponded && IsReplicationBuildBatchCommand(pending.Command))
                    {
                        RollbackReplicationProvisionalBuildViews(pending.Command, "runtime-stop");
                    }
                }
            }

            replicationActiveBuildCaptureTransaction = null;
            replicationActiveAuthoritativeBuildApplyCapture = null;
            replicationLastAuthoritativeBuildApplyCapture = null;
            replicationBuildBatchReplayDisabled = false;
            ReplicationProvisionalBuildViews.Clear();
            ReplicationBuildBatchReplayFailures.Clear();
            replicationBuildCaptureTransactionSequence = 0L;
            replicationBuildBatchRecoveryRequested = false;
            replicationBuildBatchRecoveryAttempted = false;
            replicationBuildBatchRecoveryReason = string.Empty;
        }

        private static int RollbackReplicationProvisionalBuildViews(LockstepCommand command, string reason)
        {
            if (!LockstepCommandPayloads.TryReadBuildBatchPayload(
                    command.PayloadJson,
                    out var blueprintId,
                    out _,
                    out _,
                    out _,
                    out var placementValues)
                || !TryParseReplicationBuildPlacementRecords(placementValues, out var placements, out _))
            {
                return 0;
            }

            var rolledBack = 0;
            for (var i = 0; i < placements.Count; i++)
            {
                if (!TryGetReplicationProvisionalBuildView(command.Sequence, i, out var provisionalView)
                    || provisionalView == null)
                {
                    RemoveReplicationProvisionalBuildView(command.Sequence, i);
                    continue;
                }

                var record = placements[i];
                var delta = new ReplicationWorldObjectDelta(
                    0L,
                    Time.realtimeSinceStartup,
                    "BuildingBlueprintRejected",
                    0L,
                    blueprintId,
                    record.X,
                    record.Y,
                    record.Z,
                    "rollback=" + FormatReplicationWorldObjectDetailToken(reason));
                try
                {
                    applyingRuntimeCommandDepth++;
                    try
                    {
                        if (TryRemoveReplicationBuildingCandidate(provisionalView, delta, out _)
                            || TryDisposeReplicationBuildingViewCandidate(provisionalView, out _))
                        {
                            RemoveReplicationProvisionalBuildView(command.Sequence, i);
                            rolledBack++;
                        }
                    }
                    finally
                    {
                        applyingRuntimeCommandDepth--;
                    }
                }
                catch (Exception ex)
                {
                    // Retain the registration when removal throws. The caller can
                    // retry the exact view or escalate to a save reload; deleting
                    // ownership here would silently strand a client-only building.
                    instance?.LogReplicationWarning("Going Cooperative provisional build rollback item failed reason="
                        + reason
                        + " commandSequence="
                        + command.Sequence.ToString(CultureInfo.InvariantCulture)
                        + " itemIndex="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " error="
                        + FormatReflectionExceptionDetail(ex));
                }
            }

            return rolledBack;
        }

        private static bool TryGetLastReplicationAuthoritativeBuildResult(
            string blueprintId,
            ReplicationBuildPlacementRecord record,
            int itemIndex,
            out ReplicationCapturedBuildPlacement? placement)
        {
            placement = null;
            var capture = replicationLastAuthoritativeBuildApplyCapture;
            return capture != null && capture.TryGetResult(blueprintId, record, itemIndex, out placement);
        }

        private static bool ShouldCaptureReplicationBuildTransaction()
        {
            return !IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode)
                && replicationConfigEnabled
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
                detail = "building-type-missing blueprintId=" + blueprintId;
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
            else
            {
                buildingType = string.Empty;
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

            faction = (factionOwnership.ToString() ?? string.Empty).Trim();
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

        private static bool IsUnsupportedReplicationBuildBlueprint(object blueprint, out string category)
        {
            category = string.Empty;
            if (!TryReadInstanceMemberValue(blueprint, "ConstructableBaseCategory", out var value) || value == null)
            {
                return false;
            }

            category = (value.ToString() ?? string.Empty).Trim();
            try
            {
                var numeric = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (numeric == 6 || numeric == 7)
                {
                    return true;
                }
            }
            catch
            {
            }

            return category.IndexOf("beam", StringComparison.OrdinalIgnoreCase) >= 0
                || category.IndexOf("socket", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int RollbackUnsupportedReplicationBuildPlacements(ReplicationBuildCaptureTransaction transaction)
        {
            if (transaction.UnsupportedPlacements.Count == 0)
            {
                return 0;
            }

            var rolledBack = 0;
            var oversizedRoof = false;
            var invalidRoofTopology = false;
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < transaction.UnsupportedPlacements.Count; i++)
            {
                var placement = transaction.UnsupportedPlacements[i];
                reasons.Add(placement.UnsupportedReason);
                oversizedRoof |= placement.UnsupportedReason.StartsWith("roof-topology-over-cap", StringComparison.Ordinal);
                invalidRoofTopology |= placement.UnsupportedReason.StartsWith("roof-topology-", StringComparison.Ordinal);
                try
                {
                    applyingRuntimeCommandDepth++;
                    try
                    {
                        var delta = new ReplicationWorldObjectDelta(
                            0L,
                            Time.realtimeSinceStartup,
                            "BuildingBlueprintRejected",
                            placement.UniqueId,
                            placement.BlueprintId,
                            placement.Record.X,
                            placement.Record.Y,
                            placement.Record.Z,
                            "reason=" + FormatReplicationWorldObjectDetailToken(placement.UnsupportedReason));
                        if (TryRemoveReplicationBuildingCandidate(placement.View, delta, out _)
                            || TryDisposeReplicationBuildingViewCandidate(placement.View, out _))
                        {
                            rolledBack++;
                        }
                    }
                    finally
                    {
                        applyingRuntimeCommandDepth--;
                    }
                }
                catch (Exception ex)
                {
                    instance?.LogReplicationWarning("Going Cooperative unsupported build rollback item failed index="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " blueprintId="
                        + placement.BlueprintId
                        + " error="
                        + FormatReflectionExceptionDetail(ex));
                }
            }

            ShowReplicationUnsupportedBuildMessage(oversizedRoof, invalidRoofTopology);
            instance?.LogReplicationWarning("Going Cooperative multiplayer placement blocked unsupported semantics count="
                + transaction.UnsupportedPlacements.Count.ToString(CultureInfo.InvariantCulture)
                + " rolledBack="
                + rolledBack.ToString(CultureInfo.InvariantCulture)
                + " reasons="
                + string.Join(",", new List<string>(reasons).ToArray()));
            return rolledBack;
        }

        private static int RollbackReplicationCapturedBuildPlacements(
            IList<ReplicationCapturedBuildPlacement> placements,
            string reason)
        {
            var rolledBack = 0;
            applyingRuntimeCommandDepth++;
            try
            {
                for (var i = 0; i < placements.Count; i++)
                {
                    var placement = placements[i];
                    try
                    {
                        var delta = new ReplicationWorldObjectDelta(
                            0L,
                            Time.realtimeSinceStartup,
                            "BuildingBlueprintRejected",
                            placement.UniqueId,
                            placement.BlueprintId,
                            placement.Record.X,
                            placement.Record.Y,
                            placement.Record.Z,
                            "reason=" + FormatReplicationWorldObjectDetailToken(reason));
                        if (TryRemoveReplicationBuildingCandidate(placement.View, delta, out _)
                            || TryDisposeReplicationBuildingViewCandidate(placement.View, out _))
                        {
                            rolledBack++;
                        }
                    }
                    catch (Exception ex)
                    {
                        instance?.LogReplicationWarning("Going Cooperative multiplayer build rollback item failed reason="
                            + reason
                            + " index="
                            + i.ToString(CultureInfo.InvariantCulture)
                            + " blueprintId="
                            + placement.BlueprintId
                            + " error="
                            + FormatReflectionExceptionDetail(ex));
                    }
                }
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }

            return rolledBack;
        }

        private static int RollbackReplicationRawBuildViews(
            ReplicationBuildCaptureTransaction transaction,
            string reason)
        {
            var rolledBack = 0;
            applyingRuntimeCommandDepth++;
            try
            {
                for (var i = 0; i < transaction.RawViews.Count; i++)
                {
                    var view = transaction.RawViews[i];
                    transaction.TryGetClassifiedPlacement(view, out var placement);
                    var x = placement?.Record.X ?? 0;
                    var y = placement?.Record.Y ?? 0;
                    var z = placement?.Record.Z ?? 0;
                    if (placement == null)
                    {
                        TryResolveReplicationBuildingCandidateGrid(view, out x, out y, out z);
                    }

                    var blueprintId = placement?.BlueprintId ?? string.Empty;
                    if (blueprintId.Length == 0)
                    {
                        TryResolveReplicationBuildingCandidateBlueprintId(view, out blueprintId);
                    }

                    var delta = new ReplicationWorldObjectDelta(
                        0L,
                        Time.realtimeSinceStartup,
                        "BuildingBlueprintRejected",
                        placement?.UniqueId ?? 0L,
                        blueprintId,
                        x,
                        y,
                        z,
                        "reason=" + FormatReplicationWorldObjectDetailToken(reason));
                    try
                    {
                        if (TryRemoveReplicationBuildingCandidate(view, delta, out _)
                            || TryDisposeReplicationBuildingViewCandidate(view, out _))
                        {
                            rolledBack++;
                        }
                    }
                    catch (Exception ex)
                    {
                        instance?.LogReplicationWarning("Going Cooperative raw build rollback item failed reason="
                            + reason
                            + " index="
                            + i.ToString(CultureInfo.InvariantCulture)
                            + " error="
                            + FormatReflectionExceptionDetail(ex));
                    }
                }
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }

            return rolledBack;
        }

        private static string BuildReplicationBuildBatchReplayFailureKey(long commandSequence, int itemIndex)
        {
            return commandSequence.ToString(CultureInfo.InvariantCulture)
                + "|"
                + itemIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static int GetReplicationBuildBatchReplayFailureCount(long commandSequence, int itemIndex)
        {
            return ReplicationBuildBatchReplayFailures.TryGetValue(
                BuildReplicationBuildBatchReplayFailureKey(commandSequence, itemIndex),
                out var failures)
                ? failures
                : 0;
        }

        private static int RecordReplicationBuildBatchReplayFailure(long commandSequence, int itemIndex)
        {
            var key = BuildReplicationBuildBatchReplayFailureKey(commandSequence, itemIndex);
            var failures = ReplicationBuildBatchReplayFailures.TryGetValue(key, out var existing)
                ? existing + 1
                : 1;
            ReplicationBuildBatchReplayFailures[key] = failures;
            return failures;
        }

        private static void ClearReplicationBuildBatchReplayFailure(long commandSequence, int itemIndex)
        {
            ReplicationBuildBatchReplayFailures.Remove(
                BuildReplicationBuildBatchReplayFailureKey(commandSequence, itemIndex));
        }

        private static void ScheduleReplicationBuildBatchRecovery(string reason)
        {
            if (replicationBuildBatchRecoveryRequested)
            {
                return;
            }

            replicationBuildBatchRecoveryRequested = true;
            replicationBuildBatchRecoveryReason = reason;
            ShowReplicationBuildMessage(
                "Going Cooperative: building synchronization could not converge. A full resync will start automatically.");
            instance?.LogReplicationWarning("Going Cooperative building recovery scheduled reason=" + reason);
        }

        private bool ProcessReplicationBuildBatchRecoveryRequest()
        {
            if (!replicationBuildBatchRecoveryRequested || replicationBuildBatchRecoveryAttempted)
            {
                return false;
            }

            replicationBuildBatchRecoveryAttempted = true;
            var reason = replicationBuildBatchRecoveryReason;
            if (TryRequestFullMultiplayerResync(out var error))
            {
                LogReplicationWarning("Going Cooperative building recovery started full resync reason=" + reason);
                return true;
            }

            ShowReplicationBuildMessage(
                "Going Cooperative: automatic building recovery could not start. Please press FULL RESYNC. " + error);
            LogReplicationWarning("Going Cooperative building recovery requires manual full resync reason="
                + reason
                + " error="
                + error);
            return false;
        }

        private static void ShowReplicationBuildTransportNotReadyMessage()
        {
            ShowReplicationBuildMessage(
                "Going Cooperative: multiplayer is not ready for building yet. The placement was rolled back; wait until both players are connected and loaded.");
        }

        private static void ShowReplicationBuildPlacementFailedMessage()
        {
            ShowReplicationBuildMessage(
                "Going Cooperative: placement failed and was rolled back to keep both players synchronized.");
        }

        private static void ShowReplicationBuildTransactionTooLargeMessage()
        {
            const string message = "Going Cooperative: this dragged placement is too large to replicate safely (maximum 512 pieces).";
            ShowReplicationBuildMessage(message);
        }

        private static void ShowReplicationBuildTransactionWireTooLargeMessage()
        {
            ShowReplicationBuildMessage(
                "Going Cooperative: this dragged placement is too complex to transfer safely as one transaction. Use a smaller drag.");
        }

        private static void ShowReplicationBuildMessage(string message)
        {
            try
            {
                var controllerType = AccessTools.TypeByName("NSMedieval.BlackBarMessageController");
                var controller = controllerType == null
                    ? null
                    : AccessTools.Property(controllerType, "Instance")?.GetValue(null, null);
                var show = controllerType == null
                    ? null
                    : AccessTools.Method(controllerType, "ShowBlackBarMessage", new[] { typeof(string) });
                show?.Invoke(controller, new object[] { message });
            }
            catch
            {
            }
        }

        private static void ShowReplicationUnsupportedBuildMessage(bool oversizedRoof, bool invalidRoofTopology)
        {
            var message = oversizedRoof
                ? "Going Cooperative: this roof is too large to replicate safely (maximum 512 cells)."
                : invalidRoofTopology
                    ? "Going Cooperative: this roof could not be replicated safely because its committed topology was unavailable."
                : "Going Cooperative: beam and wall-socket placement is not supported in multiplayer yet.";
            ShowReplicationBuildMessage(message);
        }

        private sealed class ReplicationBuildCaptureTransaction
        {
            private readonly HashSet<object> rawViewSet = new HashSet<object>(ReferenceObjectComparer.Instance);
            private readonly HashSet<object> classifiedViewSet = new HashSet<object>(ReferenceObjectComparer.Instance);
            private readonly Dictionary<object, ReplicationCapturedBuildPlacement> placementByView =
                new Dictionary<object, ReplicationCapturedBuildPlacement>(ReferenceObjectComparer.Instance);

            public ReplicationBuildCaptureTransaction(long transactionId)
            {
                TransactionId = transactionId;
            }

            public long TransactionId { get; }
            public List<object> RawViews { get; } = new List<object>();
            public List<ReplicationCapturedBuildPlacement> Placements { get; } = new List<ReplicationCapturedBuildPlacement>();
            public List<ReplicationCapturedBuildPlacement> UnsupportedPlacements { get; } = new List<ReplicationCapturedBuildPlacement>();
            public bool IsFinalized { get; private set; }
            public bool HasCaptureFailure { get; private set; }
            public string CaptureFailureDetail { get; private set; } = string.Empty;

            public void TrackRawView(object view)
            {
                if (rawViewSet.Add(view))
                {
                    RawViews.Add(view);
                }
            }

            public void MarkCaptureFailed(string detail)
            {
                HasCaptureFailure = true;
                if (CaptureFailureDetail.Length == 0)
                {
                    CaptureFailureDetail = detail;
                }
            }

            public void MarkFinalized()
            {
                IsFinalized = true;
            }

            public void Add(ReplicationCapturedBuildPlacement placement)
            {
                if (classifiedViewSet.Add(placement.View))
                {
                    Placements.Add(placement);
                    placementByView[placement.View] = placement;
                }
            }

            public void AddUnsupported(ReplicationCapturedBuildPlacement placement)
            {
                if (classifiedViewSet.Add(placement.View))
                {
                    UnsupportedPlacements.Add(placement);
                    placementByView[placement.View] = placement;
                }
            }

            public bool TryGetClassifiedPlacement(
                object view,
                out ReplicationCapturedBuildPlacement? placement)
            {
                return placementByView.TryGetValue(view, out placement);
            }
        }

        private sealed class ReplicationCapturedBuildPlacement
        {
            public ReplicationCapturedBuildPlacement(
                string blueprintId,
                string buildingType,
                string faction,
                ReplicationBuildPlacementRecord record,
                object view,
                object buildingInstance,
                long uniqueId,
                string unsupportedReason)
            {
                BlueprintId = blueprintId;
                BuildingType = buildingType;
                Faction = faction;
                Record = record;
                View = view;
                BuildingInstance = buildingInstance;
                UniqueId = uniqueId;
                UnsupportedReason = unsupportedReason;
            }

            public string BlueprintId { get; }
            public string BuildingType { get; }
            public string Faction { get; }
            public ReplicationBuildPlacementRecord Record { get; }
            public object View { get; }
            public object BuildingInstance { get; }
            public long UniqueId { get; }
            public bool UnsupportedCategory => UnsupportedReason.Length > 0;
            public string UnsupportedReason { get; }
        }

        private sealed class ReplicationProvisionalBuildView
        {
            public ReplicationProvisionalBuildView(object view, float capturedRealtime)
            {
                View = new WeakReference(view);
                CapturedRealtime = capturedRealtime;
            }

            public WeakReference View { get; }
            public float CapturedRealtime { get; }
        }

        private sealed class ReplicationAuthoritativeBuildApplyCapture : IDisposable
        {
            private readonly string blueprintId;
            private readonly List<ReplicationBuildPlacementRecord> records;
            private readonly ReplicationCapturedBuildPlacement?[] results;
            private readonly Dictionary<object, int> itemIndexByView =
                new Dictionary<object, int>(ReferenceObjectComparer.Instance);
            private readonly HashSet<object> spawnedViews =
                new HashSet<object>(ReferenceObjectComparer.Instance);
            private readonly HashSet<object> rolledBackViews =
                new HashSet<object>(ReferenceObjectComparer.Instance);
            private int currentRoofItemIndex = -1;
            private bool disposed;
            private bool rollbackAttempted;

            public ReplicationAuthoritativeBuildApplyCapture(
                string blueprintId,
                List<ReplicationBuildPlacementRecord> records)
            {
                this.blueprintId = blueprintId;
                this.records = new List<ReplicationBuildPlacementRecord>(records);
                results = new ReplicationCapturedBuildPlacement?[records.Count];
                replicationActiveAuthoritativeBuildApplyCapture = this;
            }

            public int CommittedCount { get; private set; }

            public int CleanupFailureCount { get; private set; }

            public int RequestedCount => records.Count;

            public bool RollbackProven { get; private set; }

            public void TrackSpawnedView(int itemIndex, object view)
            {
                if (itemIndex < 0 || itemIndex >= records.Count || view == null)
                {
                    return;
                }

                itemIndexByView[view] = itemIndex;
                spawnedViews.Add(view);
            }

            public void SetCurrentRoofItemIndex(int itemIndex)
            {
                currentRoofItemIndex = itemIndex;
            }

            public void ClearCurrentRoofItemIndex()
            {
                currentRoofItemIndex = -1;
            }

            public void TrackUncommittedViews(object collection)
            {
                if (collection is not IDictionary dictionary)
                {
                    return;
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value == null)
                    {
                        continue;
                    }

                    spawnedViews.Add(entry.Value);
                }
            }

            public void MarkCommitted(ReplicationCapturedBuildPlacement placement)
            {
                var itemIndex = itemIndexByView.TryGetValue(placement.View, out var trackedIndex)
                    ? trackedIndex
                    : currentRoofItemIndex;
                if (itemIndex < 0
                    || itemIndex >= records.Count
                    || results[itemIndex] != null
                    || placement.UniqueId <= 0L
                    || !Matches(blueprintId, records[itemIndex], placement))
                {
                    instance?.LogReplicationWarning("Going Cooperative replication build apply commit did not match input item="
                        + itemIndex.ToString(CultureInfo.InvariantCulture)
                        + " blueprintId="
                        + placement.BlueprintId);
                    return;
                }

                results[itemIndex] = placement;
                spawnedViews.Add(placement.View);
                CommittedCount++;
            }

            public bool TryGetResult(
                string expectedBlueprintId,
                ReplicationBuildPlacementRecord expectedRecord,
                int itemIndex,
                out ReplicationCapturedBuildPlacement? placement)
            {
                placement = null;
                if (!disposed
                    || !string.Equals(blueprintId, expectedBlueprintId, StringComparison.Ordinal)
                    || itemIndex < 0
                    || itemIndex >= records.Count
                    || !ReplicationBuildPlacementRecordsMatch(records[itemIndex], expectedRecord))
                {
                    return false;
                }

                placement = results[itemIndex];
                return placement != null;
            }

            public bool TryRollbackAllTransactionViews(out string detail)
            {
                rollbackAttempted = true;
                var failures = new List<string>();
                var removed = 0;
                var candidates = new List<object>(spawnedViews);
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (TryRollbackTransactionView(candidates[i], i, out var rollbackItemDetail))
                    {
                        removed++;
                    }
                    else
                    {
                        failures.Add(rollbackItemDetail);
                    }
                }

                CleanupFailureCount += failures.Count;
                RollbackProven = failures.Count == 0
                    && removed == candidates.Count
                    && CommittedCount == 0;
                detail = "attempted="
                    + candidates.Count.ToString(CultureInfo.InvariantCulture)
                    + " removed="
                    + removed.ToString(CultureInfo.InvariantCulture)
                    + " remainingCommitted="
                    + CommittedCount.ToString(CultureInfo.InvariantCulture)
                    + " failures="
                    + failures.Count.ToString(CultureInfo.InvariantCulture)
                    + (failures.Count > 0
                        ? " first=" + failures[0]
                        : string.Empty);
                return RollbackProven;
            }

            private bool TryRollbackTransactionView(object candidate, int itemIndex, out string detail)
            {
                try
                {
                    if (rolledBackViews.Contains(candidate))
                    {
                        detail = "already-rolled-back";
                        return true;
                    }

                    if (IsReplicationBuildTransactionViewAbsent(candidate, out var absentBeforeDetail))
                    {
                        MarkViewRolledBack(candidate);
                        detail = "already-absent " + absentBeforeDetail;
                        return true;
                    }

                    var cleanupX = 0;
                    var cleanupY = 0;
                    var cleanupZ = 0;
                    TryResolveReplicationBuildingCandidateGrid(
                        candidate,
                        out cleanupX,
                        out cleanupY,
                        out cleanupZ);
                    var cleanupDelta = new ReplicationWorldObjectDelta(
                        0L,
                        Time.realtimeSinceStartup,
                        "BuildingBlueprintRejected",
                        0L,
                        blueprintId,
                        cleanupX,
                        cleanupY,
                        cleanupZ,
                        "reason=authoritative-batch-atomic-rollback");
                    var removedByNative = TryRemoveReplicationBuildingCandidate(
                        candidate,
                        cleanupDelta,
                        out var removeDetail);
                    var verifiedAbsent = IsReplicationBuildTransactionViewAbsent(
                        candidate,
                        out var verifyDetail);
                    var disposeDetail = "not-required";
                    if (!verifiedAbsent)
                    {
                        var disposed = TryDisposeReplicationBuildingViewCandidate(
                            candidate,
                            out disposeDetail);
                        verifiedAbsent = disposed
                            && IsReplicationBuildTransactionViewAbsent(candidate, out verifyDetail);
                    }

                    if (!verifiedAbsent)
                    {
                        detail = "index="
                            + itemIndex.ToString(CultureInfo.InvariantCulture)
                            + ",native="
                            + (removedByNative ? "yes" : "no")
                            + ",remove="
                            + FormatReplicationWorldObjectDetailToken(removeDetail)
                            + ",dispose="
                            + FormatReplicationWorldObjectDetailToken(disposeDetail)
                            + ",verify="
                            + FormatReplicationWorldObjectDetailToken(verifyDetail)
                            + ",before="
                            + FormatReplicationWorldObjectDetailToken(absentBeforeDetail);
                        return false;
                    }

                    MarkViewRolledBack(candidate);
                    detail = "removed native="
                        + (removedByNative ? "yes" : "no")
                        + " verify="
                        + verifyDetail;
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "index="
                        + itemIndex.ToString(CultureInfo.InvariantCulture)
                        + ",rollback-exception="
                        + FormatReplicationWorldObjectDetailToken(
                            FormatReflectionExceptionDetail(ex));
                    return false;
                }
            }

            private void MarkViewRolledBack(object candidate)
            {
                if (!rolledBackViews.Add(candidate))
                {
                    return;
                }

                for (var i = 0; i < results.Length; i++)
                {
                    if (results[i] == null || !ReferenceEquals(results[i]!.View, candidate))
                    {
                        continue;
                    }

                    results[i] = null;
                    CommittedCount = Math.Max(0, CommittedCount - 1);
                }
            }

            private bool IsReplicationBuildTransactionViewAbsent(object candidate, out string detail)
            {
                try
                {
                    var unityObjectNull = candidate is UnityEngine.Object unityObject && unityObject == null;

                    var viewType = AccessTools.TypeByName(
                        "NSMedieval.BuildingComponents.BaseBuildingViewComponent");
                    if (viewType == null)
                    {
                        detail = "building-view-component-type-missing";
                        return false;
                    }

                    var liveViews = unityObjectNull
                        ? Array.Empty<UnityEngine.Object>()
                        : UnityEngine.Object.FindObjectsOfType(viewType);
                    for (var i = 0; i < liveViews.Length; i++)
                    {
                        if (ReferenceEquals(liveViews[i], candidate))
                        {
                            detail = "same-view-still-live";
                            return false;
                        }
                    }

                    ReplicationCapturedBuildPlacement? committedPlacement = null;
                    for (var i = 0; i < results.Length; i++)
                    {
                        if (results[i] != null && ReferenceEquals(results[i]!.View, candidate))
                        {
                            committedPlacement = results[i];
                            break;
                        }
                    }

                    if (committedPlacement != null)
                    {
                        if (!TryResolveReplicationBuildingsManagerMain(out var manager, out var managerDetail)
                            || manager == null
                            || !TryReadInstanceMemberValue(
                                manager,
                                "UniqueIdBuildingDictionary",
                                out var uniqueIdDictionaryValue)
                            || uniqueIdDictionaryValue is not IDictionary uniqueIdDictionary)
                        {
                            detail = "exact-view-absent-identity-unproven " + managerDetail;
                            return false;
                        }

                        foreach (DictionaryEntry entry in uniqueIdDictionary)
                        {
                            var sameId = false;
                            try
                            {
                                sameId = entry.Key != null
                                    && Convert.ToInt64(entry.Key, CultureInfo.InvariantCulture)
                                        == committedPlacement.UniqueId;
                            }
                            catch
                            {
                            }

                            if (sameId
                                || (entry.Value != null
                                    && ReferenceEquals(entry.Value, committedPlacement.BuildingInstance)))
                            {
                                detail = "exact-view-absent-building-still-registered uniqueId="
                                    + committedPlacement.UniqueId.ToString(CultureInfo.InvariantCulture);
                                return false;
                            }
                        }
                    }

                    detail = "exact-view-and-building-identity-absent unityNull="
                        + (unityObjectNull ? "yes" : "no")
                        + " scanned="
                        + liveViews.Length.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "exact-view-absence-check-failed " + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (ReferenceEquals(replicationActiveAuthoritativeBuildApplyCapture, this))
                {
                    replicationActiveAuthoritativeBuildApplyCapture = null;
                }

                // Publish immutable commit truth before cleanup. State restoration or
                // removal of an uncommitted pooled view can fault after native commits;
                // the host result manifest must still report those exact commits.
                replicationLastAuthoritativeBuildApplyCapture = this;
                var cleanupFailures = new List<string>();

                if (rollbackAttempted)
                {
                    if (!RollbackProven)
                    {
                        replicationBuildBatchReplayDisabled = true;
                    }
                    return;
                }

                foreach (var spawnedView in spawnedViews)
                {
                    var committed = false;
                    for (var i = 0; i < results.Length; i++)
                    {
                        if (results[i] != null && ReferenceEquals(results[i]!.View, spawnedView))
                        {
                            committed = true;
                            break;
                        }
                    }

                    if (committed)
                    {
                        continue;
                    }

                    try
                    {
                        var cleanupX = 0;
                        var cleanupY = 0;
                        var cleanupZ = 0;
                        TryResolveReplicationBuildingCandidateGrid(
                            spawnedView,
                            out cleanupX,
                            out cleanupY,
                            out cleanupZ);
                        var cleanupDelta = new ReplicationWorldObjectDelta(
                            0L,
                            Time.realtimeSinceStartup,
                            "BuildingBlueprintRejected",
                            0L,
                            blueprintId,
                            cleanupX,
                            cleanupY,
                            cleanupZ,
                            "reason=authoritative-batch-uncommitted-cleanup");
                        if (!TryRemoveReplicationBuildingCandidate(spawnedView, cleanupDelta, out var removeDetail)
                            && !TryDisposeReplicationBuildingViewCandidate(spawnedView, out var disposeDetail))
                        {
                            cleanupFailures.Add("remove="
                                + FormatReplicationWorldObjectDetailToken(removeDetail)
                                + ",dispose="
                                + FormatReplicationWorldObjectDetailToken(disposeDetail));
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(FormatReflectionExceptionDetail(ex));
                    }
                }

                CleanupFailureCount = cleanupFailures.Count;
                if (CleanupFailureCount > 0)
                {
                    // A transaction-owned preview escaped cleanup. Do not execute more
                    // remote placement against an unproven world until save recovery.
                    replicationBuildBatchReplayDisabled = true;
                    instance?.LogReplicationWarning("Going Cooperative authoritative BuildBatch cleanup failed count="
                        + CleanupFailureCount.ToString(CultureInfo.InvariantCulture)
                        + " first="
                        + cleanupFailures[0]
                        + " replayDisabled=yes");
                    if (replicationConfigHostMode)
                    {
                        ShowReplicationBuildMessage(
                            "Going Cooperative: host building placement could not clean up safely. Reconnect with a full resync before more client building.");
                    }
                    else
                    {
                        ScheduleReplicationBuildBatchRecovery("authoritative-batch-uncommitted-cleanup-failed");
                    }
                }
            }

            private static bool Matches(
                string expectedBlueprintId,
                ReplicationBuildPlacementRecord expectedRecord,
                ReplicationCapturedBuildPlacement placement)
            {
                return string.Equals(expectedBlueprintId, placement.BlueprintId, StringComparison.Ordinal)
                    && expectedRecord.IsRoof == placement.Record.IsRoof;
            }
        }
    }
}
