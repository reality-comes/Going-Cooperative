using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core.Replication
{
    public static class ReplicationPayloadCodec
    {
        // Building snapshot scheduling changed in REPL-2. Keep this a hard wire
        // compatibility boundary: an older peer can otherwise accept a session yet
        // disagree about when authoritative building state is allowed to replay.
        public const string ProtocolVersion = "GCOOP-REPL-2";

        public static TransportEnvelope ForHello(string senderId, ReplicationHello hello)
        {
            if (hello == null)
            {
                throw new ArgumentNullException(nameof(hello));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationHello,
                0L,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    EncodeText(hello.PeerId),
                    EncodeText(hello.Mode),
                    EncodeText(hello.ProtocolVersion),
                    EncodeText(hello.BuildHash)
                }));
        }

        public static bool TryReadHello(TransportEnvelope envelope, out ReplicationHello? hello, out string error)
        {
            hello = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationHello)
            {
                error = "envelope is not a replication hello";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 5)
            {
                error = "expected hello payload with 5 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !TryDecodeText(parts[1], out var peerId, out error)
                || !TryDecodeText(parts[2], out var mode, out error)
                || !TryDecodeText(parts[3], out var protocolVersion, out error)
                || !TryDecodeText(parts[4], out var buildHash, out error))
            {
                return false;
            }

            hello = new ReplicationHello(peerId, mode, protocolVersion, buildHash);
            return true;
        }

        public static TransportEnvelope ForCommandIntent(string senderId, ReplicationCommandIntent intent)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            var command = intent.Command;
            return new TransportEnvelope(
                TransportMessageKind.ReplicationIntent,
                command.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    EncodeText(command.PlayerId),
                    command.Sequence.ToString(CultureInfo.InvariantCulture),
                    command.TargetTick.ToString(CultureInfo.InvariantCulture),
                    ((int)command.Kind).ToString(CultureInfo.InvariantCulture),
                    EncodeText(command.PayloadJson),
                    EncodeText(command.TargetStableId ?? string.Empty),
                    command.MapX.HasValue ? command.MapX.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    command.MapY.HasValue ? command.MapY.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    command.MapZ.HasValue ? command.MapZ.Value.ToString(CultureInfo.InvariantCulture) : string.Empty
                }));
        }

        public static bool TryReadCommandIntent(
            TransportEnvelope envelope,
            out ReplicationCommandIntent? intent,
            out string error)
        {
            intent = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationIntent)
            {
                error = "envelope is not a replication command intent";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 10)
            {
                error = "expected command intent payload with 10 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !TryDecodeText(parts[1], out var playerId, out error)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetTick)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kindValue)
                || !TryDecodeText(parts[5], out var payloadJson, out error)
                || !TryDecodeText(parts[6], out var targetStableId, out error)
                || !TryParseOptionalInt(parts[7], out var mapX)
                || !TryParseOptionalInt(parts[8], out var mapY)
                || !TryParseOptionalInt(parts[9], out var mapZ))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid command intent payload";
                }

                return false;
            }

            intent = new ReplicationCommandIntent(new LockstepCommand(
                playerId,
                sequence,
                targetTick,
                (CommandKind)kindValue,
                payloadJson,
                string.IsNullOrEmpty(targetStableId) ? null : targetStableId,
                mapX,
                mapY,
                mapZ));
            return true;
        }

        public static TransportEnvelope ForCommandAck(string senderId, ReplicationCommandAck ack)
        {
            if (ack == null)
            {
                throw new ArgumentNullException(nameof(ack));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationCommandAck,
                ack.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    EncodeText(ack.PlayerId),
                    ack.Sequence.ToString(CultureInfo.InvariantCulture),
                    ack.Accepted ? "1" : "0",
                    ack.Duplicate ? "1" : "0",
                    EncodeText(ack.Detail)
                }));
        }

        public static bool TryReadCommandAck(
            TransportEnvelope envelope,
            out ReplicationCommandAck? ack,
            out string error)
        {
            ack = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationCommandAck)
            {
                error = "envelope is not a replication command ack";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 6)
            {
                error = "expected command ack payload with 6 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !TryDecodeText(parts[1], out var playerId, out error)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !TryParseBool01(parts[3], out var accepted)
                || !TryParseBool01(parts[4], out var duplicate)
                || !TryDecodeText(parts[5], out var detail, out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid command ack payload";
                }

                return false;
            }

            ack = new ReplicationCommandAck(playerId, sequence, accepted, duplicate, detail);
            return true;
        }

        public static TransportEnvelope ForRegionOrderState(string senderId, ReplicationRegionOrderState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationRegionOrderState,
                state.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    state.Sequence.ToString(CultureInfo.InvariantCulture),
                    state.SentRealtime.ToString("R", CultureInfo.InvariantCulture),
                    EncodeText(state.OrderType),
                    state.StartX.ToString(CultureInfo.InvariantCulture),
                    state.StartY.ToString(CultureInfo.InvariantCulture),
                    state.StartZ.ToString(CultureInfo.InvariantCulture),
                    state.EndX.ToString(CultureInfo.InvariantCulture),
                    state.EndY.ToString(CultureInfo.InvariantCulture),
                    state.EndZ.ToString(CultureInfo.InvariantCulture),
                    EncodeText(state.AllowType),
                    EncodeText(state.AreaType),
                    EncodeText(state.SubType),
                    EncodeText(state.Detail)
                }));
        }

        public static bool TryReadRegionOrderState(
            TransportEnvelope envelope,
            out ReplicationRegionOrderState? state,
            out string error)
        {
            state = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationRegionOrderState)
            {
                error = "envelope is not a replication region order state";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 14)
            {
                error = "expected region order state payload with 14 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sentRealtime)
                || !TryDecodeText(parts[3], out var orderType, out error)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startX)
                || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startY)
                || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startZ)
                || !int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endX)
                || !int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endY)
                || !int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var endZ)
                || !TryDecodeText(parts[10], out var allowType, out error)
                || !TryDecodeText(parts[11], out var areaType, out error)
                || !TryDecodeText(parts[12], out var subType, out error)
                || !TryDecodeText(parts[13], out var detail, out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid region order state payload";
                }

                return false;
            }

            state = new ReplicationRegionOrderState(
                sequence,
                sentRealtime,
                orderType,
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                allowType,
                areaType,
                subType,
                detail);
            return true;
        }

        public static TransportEnvelope ForWorldObjectDelta(string senderId, ReplicationWorldObjectDelta delta)
        {
            if (delta == null)
            {
                throw new ArgumentNullException(nameof(delta));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationWorldObjectDelta,
                delta.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    delta.Sequence.ToString(CultureInfo.InvariantCulture),
                    delta.SentRealtime.ToString("R", CultureInfo.InvariantCulture),
                    EncodeText(delta.DeltaKind),
                    delta.UniqueId.ToString(CultureInfo.InvariantCulture),
                    EncodeText(delta.BlueprintId),
                    delta.GridX.ToString(CultureInfo.InvariantCulture),
                    delta.GridY.ToString(CultureInfo.InvariantCulture),
                    delta.GridZ.ToString(CultureInfo.InvariantCulture),
                    EncodeText(delta.Detail)
                }));
        }

        public static bool TryReadWorldObjectDelta(
            TransportEnvelope envelope,
            out ReplicationWorldObjectDelta? delta,
            out string error)
        {
            delta = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationWorldObjectDelta)
            {
                error = "envelope is not a replication world object delta";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 10)
            {
                error = "expected world object delta payload with 10 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sentRealtime)
                || !TryDecodeText(parts[3], out var deltaKind, out error)
                || !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uniqueId)
                || !TryDecodeText(parts[5], out var blueprintId, out error)
                || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridX)
                || !int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridY)
                || !int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridZ)
                || !TryDecodeText(parts[9], out var detail, out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid world object delta payload";
                }

                return false;
            }

            delta = new ReplicationWorldObjectDelta(
                sequence,
                sentRealtime,
                deltaKind,
                uniqueId,
                blueprintId,
                gridX,
                gridY,
                gridZ,
                detail);
            return true;
        }

        public static TransportEnvelope ForWorldObjectDeltaAck(string senderId, ReplicationWorldObjectDeltaAck ack)
        {
            if (ack == null)
            {
                throw new ArgumentNullException(nameof(ack));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationWorldObjectDeltaAck,
                ack.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    ack.Sequence.ToString(CultureInfo.InvariantCulture),
                    ack.Applied ? "1" : "0",
                    ack.Duplicate ? "1" : "0",
                    EncodeText(ack.Detail)
                }));
        }

        public static bool TryReadWorldObjectDeltaAck(
            TransportEnvelope envelope,
            out ReplicationWorldObjectDeltaAck? ack,
            out string error)
        {
            ack = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationWorldObjectDeltaAck)
            {
                error = "envelope is not a replication world object delta ack";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 5)
            {
                error = "expected world object delta ack payload with 5 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !TryParseBool01(parts[2], out var applied)
                || !TryParseBool01(parts[3], out var duplicate)
                || !TryDecodeText(parts[4], out var detail, out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid world object delta ack payload";
                }

                return false;
            }

            ack = new ReplicationWorldObjectDeltaAck(sequence, applied, duplicate, detail);
            return true;
        }

        public static TransportEnvelope ForResyncControl(string senderId, ReplicationResyncControl control)
        {
            if (control == null)
            {
                throw new ArgumentNullException(nameof(control));
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationResyncControl,
                control.Sequence,
                senderId,
                string.Join("|", new[]
                {
                    ProtocolVersion,
                    control.Sequence.ToString(CultureInfo.InvariantCulture),
                    control.SentRealtime.ToString("R", CultureInfo.InvariantCulture),
                    EncodeText(control.Phase),
                    EncodeText(control.RequestId),
                    EncodeText(control.SaveId),
                    EncodeText(control.Hash),
                    control.ByteLength.ToString(CultureInfo.InvariantCulture),
                    EncodeText(control.Detail)
                }));
        }

        public static bool TryReadResyncControl(
            TransportEnvelope envelope,
            out ReplicationResyncControl? control,
            out string error)
        {
            control = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationResyncControl)
            {
                error = "envelope is not a replication resync control";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 9)
            {
                error = "expected resync control payload with 9 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sentRealtime)
                || !TryDecodeText(parts[3], out var phase, out error)
                || !TryDecodeText(parts[4], out var requestId, out error)
                || !TryDecodeText(parts[5], out var saveId, out error)
                || !TryDecodeText(parts[6], out var hash, out error)
                || !long.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteLength)
                || !TryDecodeText(parts[8], out var detail, out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid resync control payload";
                }

                return false;
            }

            control = new ReplicationResyncControl(sequence, sentRealtime, phase, requestId, saveId, hash, byteLength, detail);
            return true;
        }

        public static TransportEnvelope ForTransformSnapshot(string senderId, ReplicationTransformSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            var builder = new StringBuilder(256 + snapshot.Entities.Count * 96);
            builder.Append(ProtocolVersion)
                .Append('|')
                .Append(snapshot.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(snapshot.SentRealtime.ToString("R", CultureInfo.InvariantCulture))
                .Append('|')
                .Append(snapshot.Entities.Count.ToString(CultureInfo.InvariantCulture))
                .Append('|');

            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(';');
                }

                AppendEntity(builder, snapshot.Entities[i]);
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationTransformSnapshot,
                snapshot.Sequence,
                senderId,
                builder.ToString());
        }

        public static bool TryReadTransformSnapshot(
            TransportEnvelope envelope,
            out ReplicationTransformSnapshot? snapshot,
            out string error)
        {
            snapshot = null;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.ReplicationTransformSnapshot)
            {
                error = "envelope is not a replication transform snapshot";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 5)
            {
                error = "expected transform snapshot payload with 5 fields";
                return false;
            }

            if (!ValidateProtocol(parts[0], out error)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sentRealtime)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedCount))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid transform snapshot header";
                }

                return false;
            }

            var entities = new List<ReplicationEntityTransform>(Math.Max(0, expectedCount));
            if (parts[4].Length > 0)
            {
                var entityParts = parts[4].Split(new[] { ';' }, StringSplitOptions.None);
                for (var i = 0; i < entityParts.Length; i++)
                {
                    if (!TryReadEntity(entityParts[i], out var entity, out error) || entity == null)
                    {
                        error = "invalid entity " + i.ToString(CultureInfo.InvariantCulture) + ": " + error;
                        return false;
                    }

                    entities.Add(entity);
                }
            }

            if (entities.Count != expectedCount)
            {
                error = "entity count mismatch";
                return false;
            }

            snapshot = new ReplicationTransformSnapshot(sequence, sentRealtime, entities);
            return true;
        }

        public static TransportEnvelope ForResourceContainerBatch(string senderId, ReplicationResourceContainerBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            var builder = new StringBuilder(256 + batch.Containers.Count * 128);
            builder.Append(ProtocolVersion)
                .Append('|').Append(batch.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(batch.SentRealtime.ToString("R", CultureInfo.InvariantCulture))
                .Append('|').Append(batch.Checkpoint ? "1" : "0")
                .Append('|').Append(batch.Containers.Count.ToString(CultureInfo.InvariantCulture))
                .Append('|');

            for (var i = 0; i < batch.Containers.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(';');
                }

                var container = batch.Containers[i];
                builder.Append(EncodeText(container.ContainerId)).Append(',')
                    .Append(EncodeText(container.ContainerKind)).Append(',')
                    .Append(EncodeText(container.OwnerId)).Append(',')
                    .Append(container.Revision.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(container.GridX.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(container.GridY.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(container.GridZ.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(container.Entries.Count.ToString(CultureInfo.InvariantCulture)).Append(',');
                for (var entryIndex = 0; entryIndex < container.Entries.Count; entryIndex++)
                {
                    if (entryIndex > 0)
                    {
                        builder.Append('~');
                    }

                    var entry = container.Entries[entryIndex];
                    builder.Append(EncodeText(entry.BlueprintId)).Append(':')
                        .Append(entry.Amount.ToString(CultureInfo.InvariantCulture));
                }
            }

            return new TransportEnvelope(
                TransportMessageKind.ReplicationResourceContainerBatch,
                batch.Sequence,
                senderId,
                builder.ToString());
        }

        public static bool TryReadResourceContainerBatch(
            TransportEnvelope envelope,
            out ReplicationResourceContainerBatch? batch,
            out string error)
        {
            batch = null;
            error = string.Empty;
            if (envelope.Kind != TransportMessageKind.ReplicationResourceContainerBatch)
            {
                error = "envelope is not a resource container batch";
                return false;
            }

            var parts = envelope.Payload.Split(new[] { '|' }, StringSplitOptions.None);
            if (parts.Length != 6
                || !ValidateProtocol(parts[0], out error)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sentRealtime)
                || !TryParseBool01(parts[3], out var checkpoint)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedCount)
                || expectedCount < 0)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid resource container batch header";
                }

                return false;
            }

            var containers = new List<ReplicationResourceContainerState>(expectedCount);
            if (parts[5].Length > 0)
            {
                var encodedContainers = parts[5].Split(new[] { ';' }, StringSplitOptions.None);
                for (var i = 0; i < encodedContainers.Length; i++)
                {
                    var fields = encodedContainers[i].Split(new[] { ',' }, StringSplitOptions.None);
                    if (fields.Length != 9
                        || !TryDecodeText(fields[0], out var containerId, out error)
                        || !TryDecodeText(fields[1], out var containerKind, out error)
                        || !TryDecodeText(fields[2], out var ownerId, out error)
                        || !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
                        || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridX)
                        || !int.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridY)
                        || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gridZ)
                        || !int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedEntries)
                        || expectedEntries < 0)
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "invalid resource container " + i.ToString(CultureInfo.InvariantCulture);
                        }

                        return false;
                    }

                    var entries = new List<ReplicationResourceContainerEntry>(expectedEntries);
                    if (fields[8].Length > 0)
                    {
                        var encodedEntries = fields[8].Split(new[] { '~' }, StringSplitOptions.None);
                        for (var entryIndex = 0; entryIndex < encodedEntries.Length; entryIndex++)
                        {
                            var entryFields = encodedEntries[entryIndex].Split(new[] { ':' }, StringSplitOptions.None);
                            if (entryFields.Length != 2
                                || !TryDecodeText(entryFields[0], out var blueprintId, out error)
                                || !int.TryParse(entryFields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
                                || amount <= 0)
                            {
                                if (string.IsNullOrEmpty(error))
                                {
                                    error = "invalid resource container entry";
                                }

                                return false;
                            }

                            entries.Add(new ReplicationResourceContainerEntry(blueprintId, amount));
                        }
                    }

                    if (entries.Count != expectedEntries)
                    {
                        error = "resource container entry count mismatch";
                        return false;
                    }

                    containers.Add(new ReplicationResourceContainerState(
                        containerId,
                        containerKind,
                        ownerId,
                        revision,
                        gridX,
                        gridY,
                        gridZ,
                        entries));
                }
            }

            if (containers.Count != expectedCount)
            {
                error = "resource container count mismatch";
                return false;
            }

            batch = new ReplicationResourceContainerBatch(sequence, sentRealtime, checkpoint, containers);
            return true;
        }

        private static void AppendEntity(StringBuilder builder, ReplicationEntityTransform entity)
        {
            builder.Append(EncodeText(entity.EntityId))
                .Append(',')
                .Append(EncodeText(entity.Kind))
                .Append(',')
                .Append(entity.PositionX.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(entity.PositionY.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(entity.PositionZ.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(entity.RotationX.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(entity.RotationY.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(entity.RotationZ.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(entity.RotationW.ToString("R", CultureInfo.InvariantCulture));

            var motion = entity.Motion;
            if (!motion.HasValue)
            {
                return;
            }

            var motionValue = motion.Value;
            var flags = (motionValue.IsMoving ? 1 : 0)
                | (motionValue.IsRunning ? 2 : 0)
                | (motionValue.IsSwimming ? 4 : 0)
                | (motionValue.IsClimbing ? 8 : 0);
            builder.Append(',').Append(ReplicationEntityMotionMetadata.WireToken).Append(',')
                .Append(motionValue.VelocityX.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(motionValue.VelocityY.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(motionValue.VelocityZ.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(motionValue.MovementSpeed.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(((int)motionValue.Gait).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(flags.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(motionValue.ClimbDirection.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(motionValue.AnimatorSpeed.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(motionValue.PathRevision.ToString(CultureInfo.InvariantCulture));
        }

        private static bool TryReadEntity(string encoded, out ReplicationEntityTransform? entity, out string error)
        {
            entity = null;
            error = string.Empty;
            var parts = encoded.Split(new[] { ',' }, StringSplitOptions.None);
            if (parts.Length != 9 && parts.Length != 19)
            {
                error = "expected 9 legacy entity fields or semantic motion metadata";
                return false;
            }

            if (!TryDecodeText(parts[0], out var entityId, out error)
                || !TryDecodeText(parts[1], out var kind, out error)
                || !TryParseFloat(parts[2], out var positionX, out error)
                || !TryParseFloat(parts[3], out var positionY, out error)
                || !TryParseFloat(parts[4], out var positionZ, out error)
                || !TryParseFloat(parts[5], out var rotationX, out error)
                || !TryParseFloat(parts[6], out var rotationY, out error)
                || !TryParseFloat(parts[7], out var rotationZ, out error)
                || !TryParseFloat(parts[8], out var rotationW, out error))
            {
                return false;
            }

            ReplicationEntityMotionMetadata? motion = null;
            if (parts.Length > 9)
            {
                if (!string.Equals(parts[9], ReplicationEntityMotionMetadata.WireToken, StringComparison.Ordinal)
                    || !TryParseFloat(parts[10], out var velocityX, out error)
                    || !TryParseFloat(parts[11], out var velocityY, out error)
                    || !TryParseFloat(parts[12], out var velocityZ, out error)
                    || !TryParseFloat(parts[13], out var movementSpeed, out error)
                    || movementSpeed < 0
                    || !int.TryParse(parts[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gaitValue)
                    || !Enum.IsDefined(typeof(ReplicationAgentLocomotionGait), gaitValue)
                    || !int.TryParse(parts[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags)
                    || flags < 0
                    || (flags & ~15) != 0
                    || !int.TryParse(parts[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out var climbDirection)
                    || !TryParseFloat(parts[17], out var animatorSpeed, out error)
                    || animatorSpeed < 0
                    || !long.TryParse(parts[18], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pathRevision)
                    || pathRevision < 0)
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "invalid semantic entity motion metadata";
                    }

                    return false;
                }

                if (!IsFinite(velocityX)
                    || !IsFinite(velocityY)
                    || !IsFinite(velocityZ)
                    || !IsFinite(movementSpeed)
                    || !IsFinite(animatorSpeed))
                {
                    error = "semantic entity motion values must be finite";
                    return false;
                }

                motion = new ReplicationEntityMotionMetadata(
                    velocityX,
                    velocityY,
                    velocityZ,
                    movementSpeed,
                    (ReplicationAgentLocomotionGait)gaitValue,
                    (flags & 1) != 0,
                    (flags & 2) != 0,
                    (flags & 4) != 0,
                    (flags & 8) != 0,
                    climbDirection,
                    animatorSpeed,
                    pathRevision);
            }

            entity = new ReplicationEntityTransform(
                entityId,
                kind,
                positionX,
                positionY,
                positionZ,
                rotationX,
                rotationY,
                rotationZ,
                rotationW,
                motion);
            return true;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool ValidateProtocol(string version, out string error)
        {
            if (string.Equals(version, ProtocolVersion, StringComparison.Ordinal))
            {
                error = string.Empty;
                return true;
            }

            error = "unsupported replication payload version";
            return false;
        }

        private static bool TryParseFloat(string text, out float value, out string error)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                error = string.Empty;
                return true;
            }

            error = "invalid float";
            return false;
        }

        private static bool TryParseOptionalInt(string text, out int? value)
        {
            value = null;
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }

            return false;
        }

        private static bool TryParseBool01(string text, out bool value)
        {
            if (string.Equals(text, "1", StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (string.Equals(text, "0", StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            value = false;
            return false;
        }

        private static string EncodeText(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static bool TryDecodeText(string encoded, out string text, out string error)
        {
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                error = string.Empty;
                return true;
            }
            catch (FormatException ex)
            {
                text = string.Empty;
                error = "invalid base64 text: " + ex.Message;
                return false;
            }
        }
    }
}
