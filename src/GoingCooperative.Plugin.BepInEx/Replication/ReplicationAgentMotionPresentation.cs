using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const string ReplicationAgentMotionPresentationDeltaKind = "AgentMotionPresentation";
        private const int ReplicationSemanticMotionMaximumScannedNodes = 12;
        private const int ReplicationSemanticMotionMaximumCorners = ReplicationAgentMotionPresentation.MaximumPathPoints;
        private const float ReplicationSemanticMotionCollinearDot = 0.985f;
        private const float ReplicationSemanticMotionPointEpsilon = 0.05f;
        private const float ReplicationSemanticMotionClientSilenceSeconds = 3f;
        private const float ReplicationSemanticMotionInterpolationStateMaxAgeSeconds = 0.75f;
        private const float ReplicationSemanticMotionInterpolationBehindDot = -0.2f;
        private const float ReplicationSemanticMotionBufferedEventSeconds = 30f;
        private const int ReplicationSemanticMotionBufferedEventLimit = 128;

        private sealed class ReplicationSemanticHostMotionState
        {
            public bool Active;
            public long ActivityId;
            public long Revision;
            public long CornerSignature;
            public object? PathIdentity;
            public int CurrentNodeIndex;
            public int FinalDestinationHash;
            public int SpeedBucket;
            public ReplicationAgentLocomotionGait Gait;
            public bool CaptureKeyInitialized;
            public float LastSeenRealtime;
            public Vector3 LastPosition;
            public Quaternion LastRotation;
        }

        private sealed class ReplicationSemanticClientMotionState
        {
            public long ActivityId;
            public float DesiredSpeed;
            public ReplicationAgentLocomotionGait Gait;
            public Vector3[] Corners = Array.Empty<Vector3>();
            public int NextCornerIndex;
            public float LastObservedSnapshotRealtime;
        }

        private sealed class ReplicationSemanticBufferedClientMotionDelta
        {
            public ReplicationWorldObjectDelta Delta = null!;
            public long Epoch;
            public long ActivityId;
            public long Revision;
            public long EventTick;
            public float ExpiresRealtime;
        }

        private sealed class ReplicationSemanticMotionPointAccessors
        {
            public MemberInfo? X;
            public MemberInfo? Y;
            public MemberInfo? Z;
            public MemberInfo? NestedPoint;
        }

        private static readonly Dictionary<string, ReplicationSemanticHostMotionState> ReplicationSemanticHostMotionByEntityId =
            new Dictionary<string, ReplicationSemanticHostMotionState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationSemanticClientMotionState> ReplicationSemanticClientMotionByEntityId =
            new Dictionary<string, ReplicationSemanticClientMotionState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AgentPresentationOrderingState> ReplicationSemanticClientMotionOrderingByEntityId =
            new Dictionary<string, AgentPresentationOrderingState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationSemanticBufferedClientMotionDelta> ReplicationSemanticBufferedClientMotionByEntityId =
            new Dictionary<string, ReplicationSemanticBufferedClientMotionDelta>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, ReplicationSemanticMotionPointAccessors> ReplicationSemanticMotionPointAccessorsByType =
            new Dictionary<Type, ReplicationSemanticMotionPointAccessors>();
        private static readonly Dictionary<Type, MemberInfo?> ReplicationSemanticMotionNodePathMemberByType =
            new Dictionary<Type, MemberInfo?>();
        private static readonly Vector3[] ReplicationSemanticMotionRawPointBuffer =
            new Vector3[ReplicationSemanticMotionMaximumScannedNodes + 1];
        private static readonly Vector3[] ReplicationSemanticMotionCornerBuffer =
            new Vector3[ReplicationSemanticMotionMaximumCorners];
        private static readonly string[] ReplicationSemanticMotionPointMemberNames =
        {
            "PrecisePosition", "precisePosition",
            "WorldPosition", "worldPosition",
            "Position", "position",
            "GridPosition", "gridPosition",
            "Coordinates", "coordinates"
        };
        private static readonly List<string> ReplicationSemanticExpiredMotionEntityIds = new List<string>();
        private static readonly StringBuilder ReplicationSemanticMotionCornerBuilder = new StringBuilder(192);

        private static long replicationSemanticMotionActivitySequence;
        private static long replicationSemanticMotionBeginsSent;
        private static long replicationSemanticMotionChangesSent;
        private static long replicationSemanticMotionEndsSent;
        private static long replicationSemanticMotionBeginsApplied;
        private static long replicationSemanticMotionChangesApplied;
        private static long replicationSemanticMotionEndsApplied;
        private static long replicationSemanticMotionEventsBuffered;
        private static long replicationSemanticMotionEventsReplayed;
        private static long replicationSemanticMotionGuidedFrames;
        private static long replicationSemanticMotionInterpolationGuidedFrames;
        private static long replicationSemanticMotionExtractionFallbacks;
        private static float replicationNextSemanticMotionPruneRealtime;

        private static void CaptureReplicationSemanticAgentMotionPresentation(
            object driver,
            ReplicationSemanticMotionDriverAccessors accessors,
            string entityId,
            bool active,
            float desiredSpeed,
            ReplicationAgentLocomotionGait gait,
            Transform transform)
        {
            if (!CanUseReplicationSemanticHostPresentation()
                || IsReplicationWorldObjectDeltaModeOff()
                || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            try
            {
                ReplicationSemanticHostMotionByEntityId.TryGetValue(entityId, out var state);
                if (!active || desiredSpeed <= 0.001f || !IsReplicationSemanticMovingGait(gait))
                {
                    if (state != null && state.Active)
                    {
                        SendReplicationSemanticAgentMotionEnd(
                            entityId,
                            state,
                            transform,
                            active
                                ? ReplicationAgentPresentationEndReason.Replaced
                                : ReplicationAgentPresentationEndReason.Arrived);
                    }
                    else
                    {
                        ReplicationSemanticHostMotionByEntityId.Remove(entityId);
                    }

                    return;
                }

                if (state == null)
                {
                    state = new ReplicationSemanticHostMotionState();
                    ReplicationSemanticHostMotionByEntityId[entityId] = state;
                }

                var now = Time.realtimeSinceStartup;
                state.LastSeenRealtime = now;
                state.LastPosition = transform.position;
                state.LastRotation = transform.rotation;
                object? pathIdentity = null;
                try
                {
                    pathIdentity = accessors.CurrentPath?.GetValue(driver, null);
                }
                catch
                {
                }

                var currentNodeIndex = ReadReplicationSemanticMotionInt(accessors.CurrentNodeIndex, driver);
                var finalDestinationHash = GetReplicationSemanticMotionObjectHash(
                    accessors.FinalDestinationPrecise ?? accessors.FinalDestination,
                    driver);
                var speedBucket = Mathf.RoundToInt(desiredSpeed * 2f);
                if (state.CaptureKeyInitialized
                    && ReferenceEquals(state.PathIdentity, pathIdentity)
                    && state.CurrentNodeIndex == currentNodeIndex
                    && state.FinalDestinationHash == finalDestinationHash
                    && state.Gait == gait
                    && Math.Abs(state.SpeedBucket - speedBucket) <= 1)
                {
                    return;
                }

                state.CaptureKeyInitialized = true;
                state.PathIdentity = pathIdentity;
                state.CurrentNodeIndex = currentNodeIndex;
                state.FinalDestinationHash = finalDestinationHash;
                state.SpeedBucket = speedBucket;
                state.Gait = gait;

                if (!TryCollectReplicationSemanticMotionCorners(
                        driver,
                        accessors,
                        pathIdentity,
                        currentNodeIndex,
                        transform.position,
                        out var cornerCount,
                        out var cornerSignature)
                    || cornerCount <= 0)
                {
                    if (state.Active)
                    {
                        SendReplicationSemanticAgentMotionEnd(
                            entityId,
                            state,
                            transform,
                            ReplicationAgentPresentationEndReason.Replaced);
                    }

                    return;
                }

                unchecked
                {
                    cornerSignature = cornerSignature * 31L + (int)gait;
                    cornerSignature = cornerSignature * 31L + speedBucket;
                }

                if (!state.Active)
                {
                    state.Active = true;
                    state.ActivityId = ++replicationSemanticMotionActivitySequence;
                    state.Revision = 1L;
                    state.CornerSignature = cornerSignature;
                    SendReplicationSemanticAgentMotionPath(
                        entityId,
                        state,
                        "Begin",
                        desiredSpeed,
                        gait,
                        transform,
                        cornerCount);
                    replicationSemanticMotionBeginsSent++;
                    return;
                }

                if (state.CornerSignature == cornerSignature)
                {
                    return;
                }

                state.CornerSignature = cornerSignature;
                state.Revision++;
                SendReplicationSemanticAgentMotionPath(
                    entityId,
                    state,
                    "PathChanged",
                    desiredSpeed,
                    gait,
                    transform,
                    cornerCount);
                replicationSemanticMotionChangesSent++;
            }
            catch (Exception ex)
            {
                replicationSemanticMotionExtractionFallbacks++;
                instance?.LogReplicationInfo("Going Cooperative semantic motion path fell back entityId="
                    + entityId
                    + " error="
                    + ex.GetType().Name
                    + " message="
                    + TrimFingerprintText(ex.Message, 240));
            }
        }

        private static bool IsReplicationSemanticMovingGait(ReplicationAgentLocomotionGait gait)
        {
            return gait == ReplicationAgentLocomotionGait.Walk
                || gait == ReplicationAgentLocomotionGait.Run
                || gait == ReplicationAgentLocomotionGait.Sprint;
        }

        private static bool TryCollectReplicationSemanticMotionCorners(
            object driver,
            ReplicationSemanticMotionDriverAccessors accessors,
            object? currentPath,
            int currentNodeIndex,
            Vector3 origin,
            out int cornerCount,
            out long signature)
        {
            cornerCount = 0;
            signature = 0L;
            var rawCount = 0;
            var reachedDestinationNode = false;
            if (currentPath != null
                && TryReadReplicationSemanticMotionNodePath(currentPath, out var nodes)
                && nodes.Count > 0)
            {
                var highestUsableIndex = nodes.Count > 1 ? nodes.Count - 2 : 0;
                var startIndex = currentNodeIndex >= 0 && currentNodeIndex < nodes.Count
                    ? Math.Min(currentNodeIndex, highestUsableIndex)
                    : highestUsableIndex;
                var scanned = 0;
                for (var nodeIndex = startIndex;
                     nodeIndex >= 0 && scanned < ReplicationSemanticMotionMaximumScannedNodes;
                     nodeIndex--, scanned++)
                {
                    reachedDestinationNode |= nodeIndex == 0;
                    var node = nodes[nodeIndex];
                    if (node != null
                        && TryReadReplicationSemanticMotionWorldPoint(node, 0, out var point)
                        && IsReplicationSemanticMotionPointUsable(point)
                        && (rawCount == 0
                            || (point - ReplicationSemanticMotionRawPointBuffer[rawCount - 1]).sqrMagnitude
                                > ReplicationSemanticMotionPointEpsilon * ReplicationSemanticMotionPointEpsilon))
                    {
                        ReplicationSemanticMotionRawPointBuffer[rawCount++] = point;
                    }
                }
            }

            var finalDestination = accessors.FinalDestinationPrecise ?? accessors.FinalDestination;
            object? finalValue = null;
            try
            {
                finalValue = finalDestination?.GetValue(driver, null);
            }
            catch
            {
            }

            if (reachedDestinationNode
                && finalValue != null
                && TryReadReplicationSemanticMotionWorldPoint(finalValue, 0, out var finalPoint)
                && IsReplicationSemanticMotionPointUsable(finalPoint)
                && (rawCount == 0
                    || (finalPoint - ReplicationSemanticMotionRawPointBuffer[rawCount - 1]).sqrMagnitude
                        > ReplicationSemanticMotionPointEpsilon * ReplicationSemanticMotionPointEpsilon))
            {
                ReplicationSemanticMotionRawPointBuffer[Math.Min(rawCount, ReplicationSemanticMotionRawPointBuffer.Length - 1)] = finalPoint;
                rawCount = Math.Min(rawCount + 1, ReplicationSemanticMotionRawPointBuffer.Length);
            }

            if (rawCount == 0)
            {
                return false;
            }

            var pathWorldY = ReplicationSemanticMotionRawPointBuffer[0].y;
            for (var i = 1; i < rawCount; i++)
            {
                if (Mathf.Abs(ReplicationSemanticMotionRawPointBuffer[i].y - pathWorldY) > 0.15f)
                {
                    // MapNode.WorldPosition does not include the driver's ramp, ladder,
                    // water, or collider-height destination adjustments. Those paths
                    // stay on authoritative snapshots until exact destination sampling
                    // is available.
                    return false;
                }
            }

            for (var i = 0; i < rawCount; i++)
            {
                var point = ReplicationSemanticMotionRawPointBuffer[i];
                point.y = origin.y;
                ReplicationSemanticMotionRawPointBuffer[i] = point;
            }

            var previous = origin;
            var turnLimit = ReplicationSemanticMotionMaximumCorners - 1;
            for (var i = 0; i < rawCount - 1 && cornerCount < turnLimit; i++)
            {
                var current = ReplicationSemanticMotionRawPointBuffer[i];
                var next = ReplicationSemanticMotionRawPointBuffer[i + 1];
                var incoming = current - previous;
                var outgoing = next - current;
                var incomingHorizontal = new Vector2(incoming.x, incoming.z);
                var outgoingHorizontal = new Vector2(outgoing.x, outgoing.z);
                var isTurn = incomingHorizontal.sqrMagnitude <= 0.0001f
                    || outgoingHorizontal.sqrMagnitude <= 0.0001f
                    || Vector2.Dot(incomingHorizontal.normalized, outgoingHorizontal.normalized) < ReplicationSemanticMotionCollinearDot;
                if (isTurn)
                {
                    ReplicationSemanticMotionCornerBuffer[cornerCount++] = current;
                    previous = current;
                }
            }

            var last = ReplicationSemanticMotionRawPointBuffer[rawCount - 1];
            if (cornerCount == 0
                || (last - ReplicationSemanticMotionCornerBuffer[cornerCount - 1]).sqrMagnitude
                    > ReplicationSemanticMotionPointEpsilon * ReplicationSemanticMotionPointEpsilon)
            {
                ReplicationSemanticMotionCornerBuffer[Math.Min(cornerCount, ReplicationSemanticMotionMaximumCorners - 1)] = last;
                cornerCount = Math.Min(cornerCount + 1, ReplicationSemanticMotionMaximumCorners);
            }

            unchecked
            {
                signature = 17L;
                signature = signature * 31L + cornerCount;
                for (var i = 0; i < cornerCount; i++)
                {
                    var point = ReplicationSemanticMotionCornerBuffer[i];
                    var precision = !reachedDestinationNode && i == cornerCount - 1 ? 0.5f : 10f;
                    signature = signature * 31L + Mathf.RoundToInt(point.x * precision);
                    signature = signature * 31L + Mathf.RoundToInt(point.y * precision);
                    signature = signature * 31L + Mathf.RoundToInt(point.z * precision);
                }
            }

            return cornerCount > 0;
        }

        private static bool TryReadReplicationSemanticMotionNodePath(object currentPath, out IList nodes)
        {
            var type = currentPath.GetType();
            if (!ReplicationSemanticMotionNodePathMemberByType.TryGetValue(type, out var member))
            {
                member = FindReplicationSemanticMotionMember(type, "NodePath");
                ReplicationSemanticMotionNodePathMemberByType[type] = member;
            }

            nodes = ReadReplicationSemanticMotionMember(member, currentPath) as IList ?? null!;
            return nodes != null;
        }

        private static bool TryReadReplicationSemanticMotionWorldPoint(object value, int depth, out Vector3 point)
        {
            point = default;
            if (value == null || depth > 2)
            {
                return false;
            }

            if (value is Vector3 vector)
            {
                point = vector;
                return IsReplicationSemanticMotionPointUsable(point);
            }

            var type = value.GetType();
            var accessors = GetReplicationSemanticMotionPointAccessors(type);
            if (TryReadReplicationSemanticMotionNumber(accessors.X, value, out var x)
                && TryReadReplicationSemanticMotionNumber(accessors.Y, value, out var y)
                && TryReadReplicationSemanticMotionNumber(accessors.Z, value, out var z))
            {
                point = new Vector3((float)x, (float)y, (float)z);
                return IsReplicationSemanticMotionPointUsable(point);
            }

            var nested = ReadReplicationSemanticMotionMember(accessors.NestedPoint, value);
            return nested != null
                && !ReferenceEquals(nested, value)
                && TryReadReplicationSemanticMotionWorldPoint(nested, depth + 1, out point);
        }

        private static ReplicationSemanticMotionPointAccessors GetReplicationSemanticMotionPointAccessors(Type type)
        {
            if (ReplicationSemanticMotionPointAccessorsByType.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var accessors = new ReplicationSemanticMotionPointAccessors
            {
                X = FindReplicationSemanticMotionMember(type, "x"),
                Y = FindReplicationSemanticMotionMember(type, "y"),
                Z = FindReplicationSemanticMotionMember(type, "z")
            };
            for (var i = 0; i < ReplicationSemanticMotionPointMemberNames.Length && accessors.NestedPoint == null; i++)
            {
                accessors.NestedPoint = FindReplicationSemanticMotionMember(type, ReplicationSemanticMotionPointMemberNames[i]);
            }

            ReplicationSemanticMotionPointAccessorsByType[type] = accessors;
            return accessors;
        }

        private static MemberInfo? FindReplicationSemanticMotionMember(Type type, string name)
        {
            while (type != null && type != typeof(object))
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property;
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static object? ReadReplicationSemanticMotionMember(MemberInfo? member, object owner)
        {
            try
            {
                return member is PropertyInfo property
                    ? property.GetValue(owner, null)
                    : member is FieldInfo field
                        ? field.GetValue(owner)
                        : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryReadReplicationSemanticMotionNumber(MemberInfo? member, object owner, out double value)
        {
            value = 0d;
            var raw = ReadReplicationSemanticMotionMember(member, owner);
            if (raw == null)
            {
                return false;
            }

            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsReplicationSemanticMotionPointUsable(Vector3 point)
        {
            return !float.IsNaN(point.x)
                && !float.IsNaN(point.y)
                && !float.IsNaN(point.z)
                && !float.IsInfinity(point.x)
                && !float.IsInfinity(point.y)
                && !float.IsInfinity(point.z);
        }

        private static void SendReplicationSemanticAgentMotionPath(
            string entityId,
            ReplicationSemanticHostMotionState state,
            string phase,
            float desiredSpeed,
            ReplicationAgentLocomotionGait gait,
            Transform transform,
            int cornerCount)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var epoch = Math.Max(0, current.multiplayerSaveTransfer.Epoch);
            var eventTick = Math.Max(0, Time.frameCount);
            var stamp = new ReplicationAgentPresentationStamp(epoch, entityId, state.ActivityId, state.Revision, eventTick);
            var contractCorners = new ReplicationAgentPresentationVector3[cornerCount];
            for (var i = 0; i < cornerCount; i++)
            {
                var corner = ReplicationSemanticMotionCornerBuffer[i];
                contractCorners[i] = new ReplicationAgentPresentationVector3(corner.x, corner.y, corner.z);
            }

            var position = transform.position;
            var rotation = transform.rotation;
            if (string.Equals(phase, "Begin", StringComparison.Ordinal))
            {
                ReplicationAgentMotionPresentation.Begin(
                    stamp,
                    new ReplicationAgentPresentationVector3(position.x, position.y, position.z),
                    new ReplicationAgentPresentationQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                    desiredSpeed,
                    gait,
                    contractCorners);
            }
            else
            {
                ReplicationAgentMotionPresentation.PathChanged(
                    stamp,
                    new ReplicationAgentPresentationVector3(position.x, position.y, position.z),
                    new ReplicationAgentPresentationQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                    desiredSpeed,
                    gait,
                    contractCorners);
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationAgentMotionPresentationDeltaKind,
                uniqueId,
                phase,
                0,
                0,
                0,
                "entityId=" + entityId
                    + " phase=" + phase
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " activityId=" + state.ActivityId.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                    + " speed=" + desiredSpeed.ToString("R", CultureInfo.InvariantCulture)
                    + " gait=" + ((int)gait).ToString(CultureInfo.InvariantCulture)
                    + " cornersB64=" + EncodeReplicationDetailBase64(FormatReplicationSemanticMotionCorners(cornerCount))
                    + " source=semantic-motion-path"));
        }

        private static void SendReplicationSemanticAgentMotionEnd(
            string entityId,
            ReplicationSemanticHostMotionState state,
            Transform transform,
            ReplicationAgentPresentationEndReason reason)
        {
            SendReplicationSemanticAgentMotionEnd(
                entityId,
                state,
                transform.position,
                transform.rotation,
                reason);
        }

        private static void SendReplicationSemanticAgentMotionEnd(
            string entityId,
            ReplicationSemanticHostMotionState state,
            Vector3 position,
            Quaternion rotation,
            ReplicationAgentPresentationEndReason reason)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                state.Active = false;
                ReplicationSemanticHostMotionByEntityId.Remove(entityId);
                return;
            }

            state.Revision++;
            var epoch = Math.Max(0, current.multiplayerSaveTransfer.Epoch);
            var eventTick = Math.Max(0, Time.frameCount);
            ReplicationAgentMotionPresentation.End(
                new ReplicationAgentPresentationStamp(epoch, entityId, state.ActivityId, state.Revision, eventTick),
                new ReplicationAgentPresentationVector3(position.x, position.y, position.z),
                new ReplicationAgentPresentationQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                reason);

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                ReplicationAgentMotionPresentationDeltaKind,
                uniqueId,
                "End",
                0,
                0,
                0,
                "entityId=" + entityId
                    + " phase=End"
                    + " epoch=" + epoch.ToString(CultureInfo.InvariantCulture)
                    + " activityId=" + state.ActivityId.ToString(CultureInfo.InvariantCulture)
                    + " revision=" + state.Revision.ToString(CultureInfo.InvariantCulture)
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                    + " reason=" + reason
                    + " source=semantic-motion-end"));
            state.Active = false;
            ReplicationSemanticHostMotionByEntityId.Remove(entityId);
            replicationSemanticMotionEndsSent++;
        }

        private static string FormatReplicationSemanticMotionCorners(int cornerCount)
        {
            ReplicationSemanticMotionCornerBuilder.Clear();
            for (var i = 0; i < cornerCount; i++)
            {
                if (i > 0)
                {
                    ReplicationSemanticMotionCornerBuilder.Append(';');
                }

                var point = ReplicationSemanticMotionCornerBuffer[i];
                ReplicationSemanticMotionCornerBuilder
                    .Append(point.x.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.y.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(point.z.ToString("0.###", CultureInfo.InvariantCulture));
            }

            return ReplicationSemanticMotionCornerBuilder.ToString();
        }

        private static bool TryApplyReplicationSemanticAgentMotionDelta(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            try
            {
                return TryApplyReplicationSemanticAgentMotionDeltaCore(delta, out detail);
            }
            catch (Exception ex)
            {
                detail = "semantic-motion-apply-error=" + ex.GetType().Name + " message=" + TrimFingerprintText(ex.Message, 240);
                return false;
            }
        }

        private static bool TryApplyReplicationSemanticAgentMotionDeltaCore(
            ReplicationWorldObjectDelta delta,
            out string detail)
        {
            if (!replicationConfigSemanticAgentPresentation
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out var phase)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "epoch", out var epoch)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "activityId", out var activityText)
                || !long.TryParse(activityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var activityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "revision", out var revisionText)
                || !long.TryParse(revisionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "eventTick", out var eventTick)
                || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "semantic-motion-invalid detail=" + delta.Detail;
                return false;
            }

            var role = string.Equals(phase, "Begin", StringComparison.Ordinal)
                ? AgentPresentationEventRole.Start
                : string.Equals(phase, "PathChanged", StringComparison.Ordinal)
                    ? AgentPresentationEventRole.Update
                    : string.Equals(phase, "End", StringComparison.Ordinal)
                        ? AgentPresentationEventRole.End
                        : AgentPresentationEventRole.Instant;
            if (!string.Equals(phase, "Begin", StringComparison.Ordinal)
                && !string.Equals(phase, "PathChanged", StringComparison.Ordinal)
                && !string.Equals(phase, "End", StringComparison.Ordinal))
            {
                detail = "semantic-motion-phase-unsupported=" + phase;
                return false;
            }

            if (!ReplicationSemanticClientMotionOrderingByEntityId.TryGetValue(entityId, out var ordering))
            {
                ordering = AgentPresentationOrderingState.Empty(entityId);
            }

            var stamp = new ReplicationAgentPresentationStamp(epoch, entityId, activityId, revision, eventTick);
            var decision = AgentPresentationOrderingPolicy.Evaluate(ordering, stamp, role);
            if (decision.Disposition != AgentPresentationOrderingDisposition.Apply)
            {
                if (decision.Disposition == AgentPresentationOrderingDisposition.BufferUntilStart)
                {
                    BufferReplicationSemanticClientMotionDelta(entityId, delta, epoch, activityId, revision, eventTick);
                    detail = "semantic-motion-buffered entityId=" + entityId;
                    return true;
                }

                detail = "semantic-motion-ordering=" + decision.Disposition + " entityId=" + entityId;
                return true;
            }

            if (string.Equals(phase, "End", StringComparison.Ordinal))
            {
                ReplicationSemanticClientMotionByEntityId.Remove(entityId);
                ReplicationSemanticClientMotionOrderingByEntityId[entityId] = decision.NextState;
                replicationSemanticMotionEndsApplied++;
                detail = "ok semantic-motion-end entityId=" + entityId;
                return true;
            }

            if (!TryReadReplicationWorldObjectDetailFloat(delta.Detail, "speed", out var desiredSpeed)
                || desiredSpeed <= 0f
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "gait", out var gaitValue)
                || !Enum.IsDefined(typeof(ReplicationAgentLocomotionGait), gaitValue)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "cornersB64", out var cornersB64)
                || !TryDecodeReplicationDetailBase64(cornersB64, out var cornersText)
                || !TryParseReplicationSemanticMotionCorners(cornersText, out var corners))
            {
                detail = "semantic-motion-path-invalid entityId=" + entityId;
                return false;
            }

            var gait = (ReplicationAgentLocomotionGait)gaitValue;
            var contractCorners = new ReplicationAgentPresentationVector3[corners.Length];
            for (var i = 0; i < corners.Length; i++)
            {
                contractCorners[i] = new ReplicationAgentPresentationVector3(corners[i].x, corners[i].y, corners[i].z);
            }

            var origin = corners[0];
            var identityRotation = new ReplicationAgentPresentationQuaternion(0, 0, 0, 1);
            if (string.Equals(phase, "Begin", StringComparison.Ordinal))
            {
                ReplicationAgentMotionPresentation.Begin(
                    stamp,
                    new ReplicationAgentPresentationVector3(origin.x, origin.y, origin.z),
                    identityRotation,
                    desiredSpeed,
                    gait,
                    contractCorners);
            }
            else
            {
                ReplicationAgentMotionPresentation.PathChanged(
                    stamp,
                    new ReplicationAgentPresentationVector3(origin.x, origin.y, origin.z),
                    identityRotation,
                    desiredSpeed,
                    gait,
                    contractCorners);
            }

            ReplicationSemanticClientMotionByEntityId[entityId] = new ReplicationSemanticClientMotionState
            {
                ActivityId = activityId,
                DesiredSpeed = desiredSpeed,
                Gait = gait,
                Corners = corners,
                NextCornerIndex = 0,
                LastObservedSnapshotRealtime = Time.realtimeSinceStartup
            };
            ReplicationSemanticClientMotionOrderingByEntityId[entityId] = decision.NextState;
            if (string.Equals(phase, "Begin", StringComparison.Ordinal))
            {
                replicationSemanticMotionBeginsApplied++;
            }
            else
            {
                replicationSemanticMotionChangesApplied++;
            }

            detail = "ok semantic-motion-" + phase + " entityId=" + entityId + " corners=" + corners.Length;
            ReplayReplicationSemanticBufferedClientMotionDelta(entityId, epoch, activityId, ref detail);
            return true;
        }

        private static bool TryParseReplicationSemanticMotionCorners(string value, out Vector3[] corners)
        {
            corners = Array.Empty<Vector3>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var pointTokens = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (pointTokens.Length == 0 || pointTokens.Length > ReplicationSemanticMotionMaximumCorners)
            {
                return false;
            }

            var parsed = new Vector3[pointTokens.Length];
            for (var i = 0; i < pointTokens.Length; i++)
            {
                var components = pointTokens[i].Split(new[] { ',' }, StringSplitOptions.None);
                if (components.Length != 3
                    || !float.TryParse(components[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(components[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    || !float.TryParse(components[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                    || !IsReplicationSemanticMotionPointUsable(new Vector3(x, y, z)))
                {
                    return false;
                }

                parsed[i] = new Vector3(x, y, z);
            }

            corners = parsed;
            return true;
        }

        private static void BufferReplicationSemanticClientMotionDelta(
            string entityId,
            ReplicationWorldObjectDelta delta,
            long epoch,
            long activityId,
            long revision,
            long eventTick)
        {
            if (ReplicationSemanticBufferedClientMotionByEntityId.TryGetValue(entityId, out var existing)
                && (existing.Epoch > epoch
                    || existing.Epoch == epoch && existing.ActivityId > activityId
                    || existing.Epoch == epoch && existing.ActivityId == activityId && existing.Revision > revision
                    || existing.Epoch == epoch && existing.ActivityId == activityId && existing.Revision == revision && existing.EventTick >= eventTick))
            {
                return;
            }

            if (!ReplicationSemanticBufferedClientMotionByEntityId.ContainsKey(entityId)
                && ReplicationSemanticBufferedClientMotionByEntityId.Count >= ReplicationSemanticMotionBufferedEventLimit)
            {
                string? oldestEntityId = null;
                var oldestExpiry = float.MaxValue;
                foreach (var pair in ReplicationSemanticBufferedClientMotionByEntityId)
                {
                    if (pair.Value.ExpiresRealtime < oldestExpiry)
                    {
                        oldestEntityId = pair.Key;
                        oldestExpiry = pair.Value.ExpiresRealtime;
                    }
                }

                if (oldestEntityId != null)
                {
                    ReplicationSemanticBufferedClientMotionByEntityId.Remove(oldestEntityId);
                }
            }

            ReplicationSemanticBufferedClientMotionByEntityId[entityId] = new ReplicationSemanticBufferedClientMotionDelta
            {
                Delta = delta,
                Epoch = epoch,
                ActivityId = activityId,
                Revision = revision,
                EventTick = eventTick,
                ExpiresRealtime = Time.realtimeSinceStartup + ReplicationSemanticMotionBufferedEventSeconds
            };
            replicationSemanticMotionEventsBuffered++;
        }

        private static void ReplayReplicationSemanticBufferedClientMotionDelta(
            string entityId,
            long epoch,
            long activityId,
            ref string detail)
        {
            if (!ReplicationSemanticBufferedClientMotionByEntityId.TryGetValue(entityId, out var buffered)
                || buffered.Epoch != epoch
                || buffered.ActivityId != activityId)
            {
                return;
            }

            ReplicationSemanticBufferedClientMotionByEntityId.Remove(entityId);
            if (TryApplyReplicationSemanticAgentMotionDelta(buffered.Delta, out var replayDetail))
            {
                replicationSemanticMotionEventsReplayed++;
                detail += " replay=" + replayDetail;
            }
        }

        private static void ObserveReplicationSemanticAgentMotionSnapshot(
            string entityId,
            ReplicationEntityMotionMetadata? motion)
        {
            if (!replicationConfigSemanticAgentPresentation
                || replicationConfigHostMode
                || !ReplicationSemanticClientMotionByEntityId.TryGetValue(entityId, out var state))
            {
                return;
            }

            if (!motion.HasValue
                || !motion.Value.IsMoving && !motion.Value.IsSwimming && !motion.Value.IsClimbing)
            {
                ReplicationSemanticClientMotionByEntityId.Remove(entityId);
                return;
            }

            state.LastObservedSnapshotRealtime = Time.realtimeSinceStartup;
        }

        private static void ClearReplicationSemanticAgentMotionGuidance(string entityId)
        {
            if (!replicationConfigSemanticAgentPresentation || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            ReplicationSemanticClientMotionByEntityId.Remove(entityId);
            ReplicationSemanticBufferedClientMotionByEntityId.Remove(entityId);
        }

        private static bool TryGuideReplicationSemanticAgentMotion(
            string entityId,
            Vector3 origin,
            Vector3 velocity,
            float extrapolationSeconds,
            out Vector3 position,
            out Vector3 facingDirection)
        {
            position = origin;
            facingDirection = velocity;
            if (!replicationConfigSemanticAgentPresentation
                || extrapolationSeconds <= 0f
                || !ReplicationSemanticClientMotionByEntityId.TryGetValue(entityId, out var state)
                || state.Corners.Length == 0)
            {
                return false;
            }

            var velocityHorizontal = new Vector2(velocity.x, velocity.z);
            while (state.NextCornerIndex < state.Corners.Length)
            {
                var delta = state.Corners[state.NextCornerIndex] - origin;
                var horizontal = new Vector2(delta.x, delta.z);
                if (delta.sqrMagnitude <= 0.25f * 0.25f
                    || velocityHorizontal.sqrMagnitude > 0.0001f
                        && horizontal.sqrMagnitude < 4f
                        && Vector2.Dot(velocityHorizontal.normalized, horizontal.normalized) < -0.3f)
                {
                    state.NextCornerIndex++;
                    continue;
                }

                break;
            }

            if (state.NextCornerIndex >= state.Corners.Length)
            {
                return false;
            }

            var measuredSpeed = velocity.magnitude;
            var speed = measuredSpeed > 0.01f ? measuredSpeed : state.DesiredSpeed;
            var remainingDistance = Mathf.Max(0f, speed * extrapolationSeconds);
            var cursor = origin;
            var firstDirectionSet = false;
            for (var i = state.NextCornerIndex; i < state.Corners.Length && remainingDistance > 0f; i++)
            {
                var segment = state.Corners[i] - cursor;
                var length = segment.magnitude;
                if (length <= ReplicationSemanticMotionPointEpsilon)
                {
                    cursor = state.Corners[i];
                    continue;
                }

                var direction = segment / length;
                if (!firstDirectionSet)
                {
                    facingDirection = direction;
                    firstDirectionSet = true;
                }

                if (remainingDistance < length)
                {
                    cursor += direction * remainingDistance;
                    remainingDistance = 0f;
                    break;
                }

                cursor = state.Corners[i];
                remainingDistance -= length;
            }

            if (remainingDistance > 0f && velocity.sqrMagnitude > 0.0001f)
            {
                cursor += velocity.normalized * remainingDistance;
            }

            position = cursor;
            replicationSemanticMotionGuidedFrames++;
            return true;
        }

        private static bool TryGetReplicationSemanticAgentMotionInterpolationGuidance(
            string entityId,
            Vector3 from,
            Vector3 to,
            ReplicationAgentLocomotionGait expectedGait,
            float now,
            out Vector3 fromDirection,
            out Vector3 toDirection)
        {
            var segment = to - from;
            fromDirection = segment;
            toDirection = segment;
            if (!replicationConfigSemanticAgentPresentation
                || expectedGait == ReplicationAgentLocomotionGait.Unknown
                || expectedGait == ReplicationAgentLocomotionGait.Idle
                || !ReplicationSemanticClientMotionByEntityId.TryGetValue(entityId, out var state)
                || state.Corners.Length == 0
                || now - state.LastObservedSnapshotRealtime > ReplicationSemanticMotionInterpolationStateMaxAgeSeconds
                || state.Gait != ReplicationAgentLocomotionGait.Unknown && state.Gait != expectedGait)
            {
                return false;
            }

            var fromGuided = TryPeekReplicationSemanticAgentMotionDirection(
                state,
                from,
                segment,
                out var peekedFromDirection,
                out var fromMismatch);
            var toGuided = TryPeekReplicationSemanticAgentMotionDirection(
                state,
                to,
                segment,
                out var peekedToDirection,
                out var toMismatch);
            if (fromMismatch || toMismatch || !fromGuided && !toGuided)
            {
                return false;
            }

            var segmentHorizontal = new Vector3(segment.x, 0f, segment.z);
            if (segmentHorizontal.sqrMagnitude <= 0.0001f)
            {
                return false;
            }

            segmentHorizontal.Normalize();
            // A newly received live path can be newer than the buffered transform pair
            // currently being rendered. If both peeks describe a wholly different
            // heading, preserve the raw snapshot interpolation for this old segment.
            if (fromGuided
                && toGuided
                && Mathf.Max(
                    Vector3.Dot(segmentHorizontal, peekedFromDirection),
                    Vector3.Dot(segmentHorizontal, peekedToDirection)) < 0.25f)
            {
                return false;
            }

            fromDirection = fromGuided ? peekedFromDirection : segmentHorizontal;
            toDirection = toGuided ? peekedToDirection : segmentHorizontal;
            return true;
        }

        private static bool TryPeekReplicationSemanticAgentMotionDirection(
            ReplicationSemanticClientMotionState state,
            Vector3 origin,
            Vector3 fallbackDirection,
            out Vector3 direction,
            out bool mismatch)
        {
            direction = fallbackDirection;
            mismatch = false;
            var fallbackHorizontal = new Vector2(fallbackDirection.x, fallbackDirection.z);
            var index = state.NextCornerIndex;
            while (index < state.Corners.Length)
            {
                var delta = state.Corners[index] - origin;
                var horizontal = new Vector2(delta.x, delta.z);
                if (delta.sqrMagnitude <= 0.25f * 0.25f
                    || fallbackHorizontal.sqrMagnitude > 0.0001f
                        && horizontal.sqrMagnitude < 4f
                        && Vector2.Dot(fallbackHorizontal.normalized, horizontal.normalized) < -0.3f)
                {
                    index++;
                    continue;
                }

                if (horizontal.sqrMagnitude <= 0.0001f)
                {
                    index++;
                    continue;
                }

                var candidate = new Vector3(horizontal.x, 0f, horizontal.y).normalized;
                if (fallbackHorizontal.sqrMagnitude > 0.0001f
                    && Vector2.Dot(fallbackHorizontal.normalized, horizontal.normalized)
                        < ReplicationSemanticMotionInterpolationBehindDot)
                {
                    mismatch = true;
                    return false;
                }

                direction = candidate;
                return true;
            }

            return false;
        }

        private static void ProcessReplicationSemanticAgentMotionPresentation()
        {
            if (!replicationConfigSemanticAgentPresentation)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now < replicationNextSemanticMotionPruneRealtime)
            {
                return;
            }

            replicationNextSemanticMotionPruneRealtime = now + 1f;
            ReplicationSemanticExpiredMotionEntityIds.Clear();
            if (replicationConfigHostMode)
            {
                foreach (var pair in ReplicationSemanticHostMotionByEntityId)
                {
                    if (now - pair.Value.LastSeenRealtime > ReplicationSemanticMotionClientSilenceSeconds)
                    {
                        ReplicationSemanticExpiredMotionEntityIds.Add(pair.Key);
                    }
                }

                for (var i = 0; i < ReplicationSemanticExpiredMotionEntityIds.Count; i++)
                {
                    var entityId = ReplicationSemanticExpiredMotionEntityIds[i];
                    if (!ReplicationSemanticHostMotionByEntityId.TryGetValue(entityId, out var state))
                    {
                        continue;
                    }

                    if (state.Active && CanUseReplicationSemanticHostPresentation())
                    {
                        SendReplicationSemanticAgentMotionEnd(
                            entityId,
                            state,
                            state.LastPosition,
                            state.LastRotation,
                            ReplicationAgentPresentationEndReason.Cancelled);
                    }
                    else
                    {
                        ReplicationSemanticHostMotionByEntityId.Remove(entityId);
                    }
                }

                return;
            }

            foreach (var pair in ReplicationSemanticClientMotionByEntityId)
            {
                if (now - pair.Value.LastObservedSnapshotRealtime > ReplicationSemanticMotionClientSilenceSeconds)
                {
                    ReplicationSemanticExpiredMotionEntityIds.Add(pair.Key);
                }
            }

            foreach (var pair in ReplicationSemanticBufferedClientMotionByEntityId)
            {
                if (now >= pair.Value.ExpiresRealtime)
                {
                    ReplicationSemanticExpiredMotionEntityIds.Add(pair.Key);
                }
            }

            for (var i = 0; i < ReplicationSemanticExpiredMotionEntityIds.Count; i++)
            {
                var entityId = ReplicationSemanticExpiredMotionEntityIds[i];
                ReplicationSemanticClientMotionByEntityId.Remove(entityId);
                ReplicationSemanticBufferedClientMotionByEntityId.Remove(entityId);
            }
        }

        private static void ResetReplicationSemanticAgentMotionPresentation()
        {
            ReplicationSemanticHostMotionByEntityId.Clear();
            ReplicationSemanticClientMotionByEntityId.Clear();
            ReplicationSemanticClientMotionOrderingByEntityId.Clear();
            ReplicationSemanticBufferedClientMotionByEntityId.Clear();
            ReplicationSemanticMotionPointAccessorsByType.Clear();
            ReplicationSemanticMotionNodePathMemberByType.Clear();
            ReplicationSemanticExpiredMotionEntityIds.Clear();
            ReplicationSemanticMotionCornerBuilder.Clear();
            replicationSemanticMotionActivitySequence = 0L;
            replicationSemanticMotionBeginsSent = 0L;
            replicationSemanticMotionChangesSent = 0L;
            replicationSemanticMotionEndsSent = 0L;
            replicationSemanticMotionBeginsApplied = 0L;
            replicationSemanticMotionChangesApplied = 0L;
            replicationSemanticMotionEndsApplied = 0L;
            replicationSemanticMotionEventsBuffered = 0L;
            replicationSemanticMotionEventsReplayed = 0L;
            replicationSemanticMotionGuidedFrames = 0L;
            replicationSemanticMotionInterpolationGuidedFrames = 0L;
            replicationSemanticMotionExtractionFallbacks = 0L;
            replicationNextSemanticMotionPruneRealtime = 0f;
        }

        private static string FormatReplicationSemanticAgentMotionStatus()
        {
            return "semanticPaths=" + ReplicationSemanticClientMotionByEntityId.Count
                + " pathBeginsSent=" + replicationSemanticMotionBeginsSent
                + " pathChangesSent=" + replicationSemanticMotionChangesSent
                + " pathEndsSent=" + replicationSemanticMotionEndsSent
                + " pathBeginsApplied=" + replicationSemanticMotionBeginsApplied
                + " pathChangesApplied=" + replicationSemanticMotionChangesApplied
                + " pathEndsApplied=" + replicationSemanticMotionEndsApplied
                + " pathBuffered=" + replicationSemanticMotionEventsBuffered
                + " pathReplayed=" + replicationSemanticMotionEventsReplayed
                + " pathGuidedFrames=" + replicationSemanticMotionGuidedFrames
                + " pathInterpolationGuided=" + replicationSemanticMotionInterpolationGuidedFrames
                + " pathFallbacks=" + replicationSemanticMotionExtractionFallbacks;
        }
    }
}
