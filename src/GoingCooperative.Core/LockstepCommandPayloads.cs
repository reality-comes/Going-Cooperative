using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public static class LockstepCommandPayloads
    {
        public const int NormalSpeedIndex = 1;
        public const string SetPausedAction = "SetPaused";
        public const string SetSpeedIndexAction = "SetSpeedIndex";
        public const string SetSpeedNormalAction = "SetSpeedNormal";
        public const string DigVoxelAction = "DigVoxel";
        public const string PlaceBlueprintAction = "PlaceBlueprint";
        public const string PlaceBlueprintBatchAction = "PlaceBlueprintBatch";
        // A BuildBatch is base64-wrapped once by the command codec and a second time
        // by the transport envelope. The matching result manifest adds another
        // wrapping layer plus IDs/accepted bits. Keep the semantic payload bounded so
        // both directions remain below the transport's 512-chunk receive ceiling.
        public const int BuildBatchPayloadMaxUtf8Bytes = 131072;
        public const string CutPlantAction = "CutPlant";
        public const string RegionOrderAction = "RegionOrder";
        public const string EquipOrderAction = "EquipOrder";
        public const string ResearchActivateAction = "ResearchActivate";
        public const string ProductionQueueAction = "ProductionQueue";
        public const string ManagementPolicyAction = "ManagementPolicy";
        public const string WorkerScheduleUpdateAction = "WorkerScheduleUpdate";
        public const string WorkerScheduleStateAction = "WorkerScheduleState";
        public const int MaximumWorkerScheduleHours = 48;
        public const string WorkerManagePresetAction = "WorkerManagePreset";
        public const string DraftStateAction = "DraftState";
        public const string DraftMoveAction = "DraftMove";
        public const string CombatAttackAction = "CombatAttack";
        public const string CombatCancelAction = "CombatCancel";
        public const string CombatOutcomeAction = "CombatOutcome";
        public const string CombatPresentationAction = "CombatPresentation";
        public const string CombatPresentationChargeStartPhase = "ChargeStart";
        public const string CombatPresentationWeaponReleasePhase = "WeaponRelease";
        public const string CombatPresentationChargeEndPhase = "ChargeEnd";
        public const string CombatPresentationAttackKindUnknown = "unknown";
        public const string CombatPresentationAttackKindMelee = "melee";
        public const string CombatPresentationAttackKindRanged = "ranged";
        public const string CombatPresentationEndReasonNone = "none";
        public const string CombatPresentationEndReasonCompleted = "completed";
        public const string CombatPresentationEndReasonCancelled = "cancelled";
        public const string CombatPresentationEndReasonStreamEnded = "stream-ended";
        public const string CombatPresentationEndReasonWatchdog = "watchdog";
        public const string GameEventOptionChosenAction = "GameEventOptionChosen";

        public static string CreateGameEventOptionChosenPayload(
            long epoch,
            string eventId,
            long eventRevision,
            string dialogId,
            int dialogIndex,
            int optionIndex,
            string requestId)
        {
            return "{\"action\":\"" + GameEventOptionChosenAction
                + "\",\"epoch\":" + epoch.ToString(CultureInfo.InvariantCulture)
                + ",\"eventId\":\"" + EscapeJsonString(eventId)
                + "\",\"eventRevision\":" + eventRevision.ToString(CultureInfo.InvariantCulture)
                + ",\"dialogId\":\"" + EscapeJsonString(dialogId)
                + "\",\"dialogIndex\":" + dialogIndex.ToString(CultureInfo.InvariantCulture)
                + ",\"optionIndex\":" + optionIndex.ToString(CultureInfo.InvariantCulture)
                + ",\"requestId\":\"" + EscapeJsonString(requestId)
                + "\"}";
        }

        public static bool TryReadGameEventOptionChosenPayload(
            string payloadJson,
            out long epoch,
            out string eventId,
            out long eventRevision,
            out string dialogId,
            out int dialogIndex,
            out int optionIndex,
            out string requestId)
        {
            var normalized = Normalize(payloadJson);
            epoch = -1L;
            eventId = string.Empty;
            eventRevision = -1L;
            dialogId = string.Empty;
            dialogIndex = -1;
            optionIndex = -1;
            requestId = string.Empty;
            return HasAction(normalized, GameEventOptionChosenAction)
                && TryReadLongProperty(normalized, "epoch", out epoch)
                && epoch >= 0L
                && TryReadStringProperty(normalized, "eventId", out eventId)
                && !string.IsNullOrWhiteSpace(eventId)
                && eventId.Length <= 256
                && TryReadLongProperty(normalized, "eventRevision", out eventRevision)
                && eventRevision >= 0L
                && TryReadStringProperty(normalized, "dialogId", out dialogId)
                && dialogId.Length <= 256
                && TryReadIntProperty(normalized, "dialogIndex", out dialogIndex)
                && dialogIndex >= -1
                && TryReadIntProperty(normalized, "optionIndex", out optionIndex)
                && optionIndex >= 0
                && optionIndex <= 31
                && TryReadStringProperty(normalized, "requestId", out requestId)
                && !string.IsNullOrWhiteSpace(requestId)
                && requestId.Length <= 128;
        }

        public static string CreateDraftStatePayload(string entityId, bool drafted, string combatMode)
        {
            return "{\"action\":\"" + DraftStateAction
                + "\",\"entityId\":\"" + EscapeJsonString(entityId)
                + "\",\"drafted\":" + (drafted ? "true" : "false")
                + ",\"combatMode\":\"" + EscapeJsonString(combatMode)
                + "\"}";
        }

        public static bool TryReadDraftStatePayload(
            string payloadJson,
            out string entityId,
            out bool drafted,
            out string combatMode)
        {
            var normalized = Normalize(payloadJson);
            entityId = string.Empty;
            drafted = false;
            combatMode = string.Empty;
            return HasAction(normalized, DraftStateAction)
                && TryReadStringProperty(normalized, "entityId", out entityId)
                && !string.IsNullOrWhiteSpace(entityId)
                && TryReadBoolProperty(normalized, "drafted", out drafted)
                && TryReadStringProperty(normalized, "combatMode", out combatMode);
        }

        public static string CreateDraftMovePayload(
            string[] entityIds,
            int targetX,
            int targetY,
            int targetZ,
            string combatMode)
        {
            return "{\"action\":\"" + DraftMoveAction
                + "\",\"entityIds\":" + CreateJsonStringArray(entityIds)
                + ",\"targetX\":" + targetX.ToString(CultureInfo.InvariantCulture)
                + ",\"targetY\":" + targetY.ToString(CultureInfo.InvariantCulture)
                + ",\"targetZ\":" + targetZ.ToString(CultureInfo.InvariantCulture)
                + ",\"combatMode\":\"" + EscapeJsonString(combatMode)
                + "\"}";
        }

        public static bool TryReadDraftMovePayload(
            string payloadJson,
            out string[] entityIds,
            out int targetX,
            out int targetY,
            out int targetZ,
            out string combatMode)
        {
            var normalized = Normalize(payloadJson);
            entityIds = Array.Empty<string>();
            targetX = targetY = targetZ = 0;
            combatMode = string.Empty;
            return HasAction(normalized, DraftMoveAction)
                && TryReadStringArrayProperty(normalized, "entityIds", out entityIds)
                && HasNonEmptyEntityIds(entityIds)
                && TryReadIntProperty(normalized, "targetX", out targetX)
                && TryReadIntProperty(normalized, "targetY", out targetY)
                && TryReadIntProperty(normalized, "targetZ", out targetZ)
                && TryReadStringProperty(normalized, "combatMode", out combatMode);
        }

        public static string CreateCombatAttackPayload(
            string[] attackerEntityIds,
            string targetKind,
            string targetId,
            int targetX,
            int targetY,
            int targetZ)
        {
            return "{\"action\":\"" + CombatAttackAction
                + "\",\"attackerEntityIds\":" + CreateJsonStringArray(attackerEntityIds)
                + ",\"targetKind\":\"" + EscapeJsonString(targetKind)
                + "\",\"targetId\":\"" + EscapeJsonString(targetId)
                + "\",\"targetX\":" + targetX.ToString(CultureInfo.InvariantCulture)
                + ",\"targetY\":" + targetY.ToString(CultureInfo.InvariantCulture)
                + ",\"targetZ\":" + targetZ.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static bool TryReadCombatAttackPayload(
            string payloadJson,
            out string[] attackerEntityIds,
            out string targetKind,
            out string targetId,
            out int targetX,
            out int targetY,
            out int targetZ)
        {
            var normalized = Normalize(payloadJson);
            attackerEntityIds = Array.Empty<string>();
            targetKind = targetId = string.Empty;
            targetX = targetY = targetZ = 0;
            return HasAction(normalized, CombatAttackAction)
                && TryReadStringArrayProperty(normalized, "attackerEntityIds", out attackerEntityIds)
                && HasNonEmptyEntityIds(attackerEntityIds)
                && TryReadStringProperty(normalized, "targetKind", out targetKind)
                && !string.IsNullOrWhiteSpace(targetKind)
                && TryReadStringProperty(normalized, "targetId", out targetId)
                && TryReadIntProperty(normalized, "targetX", out targetX)
                && TryReadIntProperty(normalized, "targetY", out targetY)
                && TryReadIntProperty(normalized, "targetZ", out targetZ);
        }

        public static string CreateCombatCancelPayload(string[] attackerEntityIds)
        {
            return "{\"action\":\"" + CombatCancelAction
                + "\",\"attackerEntityIds\":" + CreateJsonStringArray(attackerEntityIds)
                + "}";
        }

        public static bool TryReadCombatCancelPayload(string payloadJson, out string[] attackerEntityIds)
        {
            var normalized = Normalize(payloadJson);
            attackerEntityIds = Array.Empty<string>();
            return HasAction(normalized, CombatCancelAction)
                && TryReadStringArrayProperty(normalized, "attackerEntityIds", out attackerEntityIds)
                && HasNonEmptyEntityIds(attackerEntityIds);
        }

        public static string CreateCombatOutcomePayload(
            string outcomeId,
            long authoritativeTick,
            string attackerEntityId,
            string targetKind,
            string targetId,
            int targetX,
            int targetY,
            int targetZ,
            string outcomeType,
            double amount,
            string bodyPart,
            string effectId,
            bool lethal)
        {
            return "{\"action\":\"" + CombatOutcomeAction
                + "\",\"outcomeId\":\"" + EscapeJsonString(outcomeId)
                + "\",\"authoritativeTick\":" + authoritativeTick.ToString(CultureInfo.InvariantCulture)
                + ",\"attackerEntityId\":\"" + EscapeJsonString(attackerEntityId)
                + "\",\"targetKind\":\"" + EscapeJsonString(targetKind)
                + "\",\"targetId\":\"" + EscapeJsonString(targetId)
                + "\",\"targetX\":" + targetX.ToString(CultureInfo.InvariantCulture)
                + ",\"targetY\":" + targetY.ToString(CultureInfo.InvariantCulture)
                + ",\"targetZ\":" + targetZ.ToString(CultureInfo.InvariantCulture)
                + ",\"outcomeType\":\"" + EscapeJsonString(outcomeType)
                + "\",\"amount\":" + amount.ToString("R", CultureInfo.InvariantCulture)
                + ",\"bodyPart\":\"" + EscapeJsonString(bodyPart)
                + "\",\"effectId\":\"" + EscapeJsonString(effectId)
                + "\",\"lethal\":" + (lethal ? "true" : "false")
                + "}";
        }

        public static bool TryReadCombatOutcomePayload(
            string payloadJson,
            out string outcomeId,
            out long authoritativeTick,
            out string attackerEntityId,
            out string targetKind,
            out string targetId,
            out int targetX,
            out int targetY,
            out int targetZ,
            out string outcomeType,
            out double amount,
            out string bodyPart,
            out string effectId,
            out bool lethal)
        {
            var normalized = Normalize(payloadJson);
            outcomeId = attackerEntityId = targetKind = targetId = outcomeType = bodyPart = effectId = string.Empty;
            authoritativeTick = 0;
            targetX = targetY = targetZ = 0;
            amount = 0;
            lethal = false;
            return HasAction(normalized, CombatOutcomeAction)
                && TryReadStringProperty(normalized, "outcomeId", out outcomeId)
                && !string.IsNullOrWhiteSpace(outcomeId)
                && TryReadLongProperty(normalized, "authoritativeTick", out authoritativeTick)
                && authoritativeTick >= 0
                && TryReadStringProperty(normalized, "attackerEntityId", out attackerEntityId)
                && TryReadStringProperty(normalized, "targetKind", out targetKind)
                && !string.IsNullOrWhiteSpace(targetKind)
                && TryReadStringProperty(normalized, "targetId", out targetId)
                && TryReadIntProperty(normalized, "targetX", out targetX)
                && TryReadIntProperty(normalized, "targetY", out targetY)
                && TryReadIntProperty(normalized, "targetZ", out targetZ)
                && TryReadStringProperty(normalized, "outcomeType", out outcomeType)
                && !string.IsNullOrWhiteSpace(outcomeType)
                && TryReadDoubleProperty(normalized, "amount", out amount)
                && !double.IsNaN(amount)
                && !double.IsInfinity(amount)
                && TryReadStringProperty(normalized, "bodyPart", out bodyPart)
                && TryReadStringProperty(normalized, "effectId", out effectId)
                && TryReadBoolProperty(normalized, "lethal", out lethal);
        }

        public static string CreateCombatPresentationPayload(
            string chargeId,
            long authoritativeTick,
            string attackerEntityId,
            string phase,
            string attackKind,
            double durationSeconds,
            string animationToken,
            int weaponType,
            int attackRnd,
            string endReason)
        {
            return "{\"action\":\"" + CombatPresentationAction
                + "\",\"chargeId\":\"" + EscapeJsonString(chargeId)
                + "\",\"authoritativeTick\":" + authoritativeTick.ToString(CultureInfo.InvariantCulture)
                + ",\"attackerEntityId\":\"" + EscapeJsonString(attackerEntityId)
                + "\",\"phase\":\"" + EscapeJsonString(phase)
                + "\",\"attackKind\":\"" + EscapeJsonString(attackKind)
                + "\",\"durationSeconds\":" + durationSeconds.ToString("R", CultureInfo.InvariantCulture)
                + ",\"animationToken\":\"" + EscapeJsonString(animationToken)
                + "\",\"weaponType\":" + weaponType.ToString(CultureInfo.InvariantCulture)
                + ",\"attackRnd\":" + attackRnd.ToString(CultureInfo.InvariantCulture)
                + ",\"endReason\":\"" + EscapeJsonString(endReason)
                + "\"}";
        }

        public static bool TryReadCombatPresentationPayload(
            string payloadJson,
            out string chargeId,
            out long authoritativeTick,
            out string attackerEntityId,
            out string phase,
            out string attackKind,
            out double durationSeconds,
            out string animationToken,
            out int weaponType,
            out int attackRnd,
            out string endReason)
        {
            var normalized = Normalize(payloadJson);
            chargeId = attackerEntityId = phase = attackKind = animationToken = endReason = string.Empty;
            authoritativeTick = 0;
            durationSeconds = 0;
            weaponType = attackRnd = 0;

            if (!HasAction(normalized, CombatPresentationAction)
                || !TryReadStringProperty(normalized, "chargeId", out chargeId)
                || string.IsNullOrWhiteSpace(chargeId)
                || !TryReadLongProperty(normalized, "authoritativeTick", out authoritativeTick)
                || authoritativeTick < 0
                || !TryReadStringProperty(normalized, "attackerEntityId", out attackerEntityId)
                || string.IsNullOrWhiteSpace(attackerEntityId)
                || !TryReadStringProperty(normalized, "phase", out phase)
                || !IsCombatPresentationPhase(phase)
                || !TryReadStringProperty(normalized, "attackKind", out attackKind)
                || !IsCombatPresentationAttackKind(attackKind)
                || !TryReadDoubleProperty(normalized, "durationSeconds", out durationSeconds)
                || double.IsNaN(durationSeconds)
                || double.IsInfinity(durationSeconds)
                || durationSeconds < 0
                || (string.Equals(phase, CombatPresentationChargeStartPhase, StringComparison.Ordinal)
                    && durationSeconds <= 0)
                || !TryReadIntProperty(normalized, "weaponType", out weaponType)
                || !TryReadIntProperty(normalized, "attackRnd", out attackRnd)
                || !TryReadStringProperty(normalized, "endReason", out endReason)
                || !IsCombatPresentationEndReason(endReason))
            {
                return false;
            }

            var animationTokenPresent = normalized.IndexOf("\"animationToken\":", StringComparison.Ordinal) >= 0;
            var hasAnimationToken = TryReadStringProperty(normalized, "animationToken", out animationToken);
            return (!animationTokenPresent || hasAnimationToken)
                && (!string.Equals(phase, CombatPresentationChargeStartPhase, StringComparison.Ordinal)
                    || (hasAnimationToken && !string.IsNullOrWhiteSpace(animationToken)));
        }

        private static bool IsCombatPresentationPhase(string phase)
        {
            return string.Equals(phase, CombatPresentationChargeStartPhase, StringComparison.Ordinal)
                || string.Equals(phase, CombatPresentationWeaponReleasePhase, StringComparison.Ordinal)
                || string.Equals(phase, CombatPresentationChargeEndPhase, StringComparison.Ordinal);
        }

        private static bool IsCombatPresentationAttackKind(string attackKind)
        {
            return string.Equals(attackKind, CombatPresentationAttackKindUnknown, StringComparison.Ordinal)
                || string.Equals(attackKind, CombatPresentationAttackKindMelee, StringComparison.Ordinal)
                || string.Equals(attackKind, CombatPresentationAttackKindRanged, StringComparison.Ordinal);
        }

        private static bool IsCombatPresentationEndReason(string endReason)
        {
            return string.Equals(endReason, CombatPresentationEndReasonCompleted, StringComparison.Ordinal)
                || string.Equals(endReason, CombatPresentationEndReasonCancelled, StringComparison.Ordinal)
                || string.Equals(endReason, CombatPresentationEndReasonStreamEnded, StringComparison.Ordinal)
                || string.Equals(endReason, CombatPresentationEndReasonWatchdog, StringComparison.Ordinal)
                || string.Equals(endReason, CombatPresentationEndReasonNone, StringComparison.Ordinal);
        }

        public static string CreateManagementPolicyPayload(string policy, string targetId, string key, int index, int value, bool enabled)
        {
            return "{\"action\":\"" + ManagementPolicyAction + "\",\"policy\":\"" + EscapeJsonString(policy)
                + "\",\"targetId\":\"" + EscapeJsonString(targetId) + "\",\"key\":\"" + EscapeJsonString(key)
                + "\",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"value\":" + value.ToString(CultureInfo.InvariantCulture)
                + ",\"enabled\":" + (enabled ? "true" : "false") + "}";
        }

        public static bool TryReadManagementPolicyPayload(string payloadJson, out string policy, out string targetId, out string key, out int index, out int value, out bool enabled)
        {
            var normalized = Normalize(payloadJson);
            policy = targetId = key = string.Empty;
            index = value = 0;
            enabled = false;
            return normalized.IndexOf("\"action\":\"" + ManagementPolicyAction + "\"", StringComparison.Ordinal) >= 0
                && TryReadStringProperty(normalized, "policy", out policy)
                && TryReadStringProperty(normalized, "targetId", out targetId)
                && TryReadStringProperty(normalized, "key", out key)
                && TryReadIntProperty(normalized, "index", out index)
                && TryReadIntProperty(normalized, "value", out value)
                && TryReadBoolProperty(normalized, "enabled", out enabled);
        }

        public static string CreateWorkerScheduleUpdatePayload(
            string targetId,
            int[] hours,
            int[] hourTypes)
        {
            var indices = hours ?? Array.Empty<int>();
            var values = hourTypes ?? Array.Empty<int>();
            var changes = new StringBuilder();
            var count = indices.Length == values.Length ? indices.Length : 0;
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    changes.Append(',');
                }
                changes.Append(indices[i].ToString(CultureInfo.InvariantCulture));
                changes.Append(':');
                changes.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            return "{\"action\":\"" + WorkerScheduleUpdateAction
                + "\",\"targetId\":\"" + EscapeJsonString(targetId)
                + "\",\"changes\":\"" + changes + "\"}";
        }

        public static bool TryReadWorkerScheduleUpdatePayload(
            string payloadJson,
            out string targetId,
            out int[] hours,
            out int[] hourTypes)
        {
            var normalized = Normalize(payloadJson);
            targetId = string.Empty;
            hours = Array.Empty<int>();
            hourTypes = Array.Empty<int>();
            if (!HasAction(normalized, WorkerScheduleUpdateAction)
                || !TryReadStringProperty(normalized, "targetId", out targetId)
                || string.IsNullOrWhiteSpace(targetId)
                || !TryReadStringProperty(normalized, "changes", out var encoded)
                || string.IsNullOrWhiteSpace(encoded))
            {
                return false;
            }

            var parts = encoded.Split(new[] { ',' }, StringSplitOptions.None);
            if (parts.Length == 0 || parts.Length > MaximumWorkerScheduleHours)
            {
                return false;
            }

            var parsedHours = new int[parts.Length];
            var parsedTypes = new int[parts.Length];
            var seenHours = new HashSet<int>();
            for (var i = 0; i < parts.Length; i++)
            {
                var separator = parts[i].IndexOf(':');
                if (separator <= 0
                    || separator >= parts[i].Length - 1
                    || !int.TryParse(parts[i].Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedHours[i])
                    || !int.TryParse(parts[i].Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedTypes[i])
                    || parsedHours[i] < 0
                    || parsedHours[i] >= MaximumWorkerScheduleHours
                    || !seenHours.Add(parsedHours[i]))
                {
                    return false;
                }
            }

            hours = parsedHours;
            hourTypes = parsedTypes;
            return true;
        }

        public static string CreateWorkerScheduleStatePayload(string targetId, int[] hourTypes)
        {
            var values = hourTypes ?? Array.Empty<int>();
            var hours = new StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    hours.Append(',');
                }
                hours.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }

            return "{\"action\":\"" + WorkerScheduleStateAction
                + "\",\"targetId\":\"" + EscapeJsonString(targetId)
                + "\",\"hourTypes\":\"" + hours + "\"}";
        }

        public static bool TryReadWorkerScheduleStatePayload(
            string payloadJson,
            out string targetId,
            out int[] hourTypes)
        {
            var normalized = Normalize(payloadJson);
            targetId = string.Empty;
            hourTypes = Array.Empty<int>();
            if (!HasAction(normalized, WorkerScheduleStateAction)
                || !TryReadStringProperty(normalized, "targetId", out targetId)
                || string.IsNullOrWhiteSpace(targetId)
                || !TryReadStringProperty(normalized, "hourTypes", out var encoded)
                || string.IsNullOrWhiteSpace(encoded))
            {
                return false;
            }

            var parts = encoded.Split(new[] { ',' }, StringSplitOptions.None);
            if (parts.Length == 0 || parts.Length > MaximumWorkerScheduleHours)
            {
                return false;
            }

            var parsed = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[i]))
                {
                    return false;
                }
            }

            hourTypes = parsed;
            return true;
        }

        public static string CreateWorkerManagePresetPayload(
            string targetId,
            string groupId,
            string presetId,
            bool forceAutoEquip)
        {
            return "{\"action\":\"" + WorkerManagePresetAction
                + "\",\"targetId\":\"" + EscapeJsonString(targetId)
                + "\",\"groupId\":\"" + EscapeJsonString(groupId)
                + "\",\"presetId\":\"" + EscapeJsonString(presetId)
                + "\",\"forceAutoEquip\":" + (forceAutoEquip ? "true" : "false") + "}";
        }

        public static bool TryReadWorkerManagePresetPayload(
            string payloadJson,
            out string targetId,
            out string groupId,
            out string presetId,
            out bool forceAutoEquip)
        {
            var normalized = Normalize(payloadJson);
            targetId = groupId = presetId = string.Empty;
            forceAutoEquip = false;
            return normalized.IndexOf("\"action\":\"" + WorkerManagePresetAction + "\"", StringComparison.Ordinal) >= 0
                && TryReadStringProperty(normalized, "targetId", out targetId)
                && TryReadStringProperty(normalized, "groupId", out groupId)
                && TryReadStringProperty(normalized, "presetId", out presetId)
                && TryReadBoolProperty(normalized, "forceAutoEquip", out forceAutoEquip)
                && !string.IsNullOrWhiteSpace(targetId)
                && !string.IsNullOrWhiteSpace(groupId)
                && !string.IsNullOrWhiteSpace(presetId);
        }

        public static string CreateResearchActivatePayload(string nodeId)
        {
            return "{\"action\":\"" + ResearchActivateAction + "\",\"nodeId\":\"" + EscapeJsonString(nodeId) + "\"}";
        }

        public static bool TryReadResearchActivatePayload(string payloadJson, out string nodeId)
        {
            var normalized = Normalize(payloadJson);
            nodeId = string.Empty;
            return normalized.IndexOf("\"action\":\"" + ResearchActivateAction + "\"", StringComparison.Ordinal) >= 0
                && TryReadStringProperty(normalized, "nodeId", out nodeId)
                && !string.IsNullOrWhiteSpace(nodeId);
        }

        public static string CreateProductionQueuePayload(
            string operation,
            int buildingX,
            int buildingY,
            int buildingZ,
            int ticketIndex,
            string blueprintId,
            int value)
        {
            return "{\"action\":\"" + ProductionQueueAction
                + "\",\"operation\":\"" + EscapeJsonString(operation)
                + "\",\"buildingX\":" + buildingX.ToString(CultureInfo.InvariantCulture)
                + ",\"buildingY\":" + buildingY.ToString(CultureInfo.InvariantCulture)
                + ",\"buildingZ\":" + buildingZ.ToString(CultureInfo.InvariantCulture)
                + ",\"ticketIndex\":" + ticketIndex.ToString(CultureInfo.InvariantCulture)
                + ",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"value\":" + value.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static bool TryReadProductionQueuePayload(
            string payloadJson,
            out string operation,
            out int buildingX,
            out int buildingY,
            out int buildingZ,
            out int ticketIndex,
            out string blueprintId,
            out int value)
        {
            var normalized = Normalize(payloadJson);
            operation = string.Empty;
            buildingX = 0;
            buildingY = 0;
            buildingZ = 0;
            ticketIndex = -1;
            blueprintId = string.Empty;
            value = 0;
            return normalized.IndexOf("\"action\":\"" + ProductionQueueAction + "\"", StringComparison.Ordinal) >= 0
                && TryReadStringProperty(normalized, "operation", out operation)
                && TryReadIntProperty(normalized, "buildingX", out buildingX)
                && TryReadIntProperty(normalized, "buildingY", out buildingY)
                && TryReadIntProperty(normalized, "buildingZ", out buildingZ)
                && TryReadIntProperty(normalized, "ticketIndex", out ticketIndex)
                && TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "value", out value);
        }

        public static string CreatePausePayload(bool paused)
        {
            return "{\"action\":\"" + SetPausedAction + "\",\"paused\":" + (paused ? "true" : "false") + "}";
        }

        public static string CreateSpeedIndexPayload(int speedIndex)
        {
            return "{\"action\":\"" + SetSpeedIndexAction + "\",\"speedIndex\":" + speedIndex.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string CreateSetSpeedNormalPayload()
        {
            return "{\"action\":\"" + SetSpeedNormalAction + "\",\"speedIndex\":" + NormalSpeedIndex.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string CreateDigPayload(int startX, int startY, int startZ, int endX, int endY, int endZ)
        {
            return "{\"action\":\"" + DigVoxelAction + "\",\"startX\":" + startX.ToString(CultureInfo.InvariantCulture)
                + ",\"startY\":" + startY.ToString(CultureInfo.InvariantCulture)
                + ",\"startZ\":" + startZ.ToString(CultureInfo.InvariantCulture)
                + ",\"endX\":" + endX.ToString(CultureInfo.InvariantCulture)
                + ",\"endY\":" + endY.ToString(CultureInfo.InvariantCulture)
                + ",\"endZ\":" + endZ.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static string CreateBuildPayload(string blueprintId, int x, int y, int z, int angleY, string buildingType, string factionOwnership, bool afterLoading)
        {
            return "{\"action\":\"" + PlaceBlueprintAction + "\",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture)
                + ",\"angleY\":" + angleY.ToString(CultureInfo.InvariantCulture)
                + ",\"buildingType\":\"" + EscapeJsonString(buildingType)
                + "\",\"faction\":\"" + EscapeJsonString(factionOwnership)
                + "\",\"afterLoading\":" + (afterLoading ? "true" : "false")
                + "}";
        }

        public static string CreateBuildBatchPayload(
            string blueprintId,
            string buildingType,
            string factionOwnership,
            bool afterLoading,
            string[] placementRecords,
            long epoch = -1L)
        {
            return "{\"action\":\"" + PlaceBlueprintBatchAction
                + "\",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"buildingType\":\"" + EscapeJsonString(buildingType)
                + "\",\"faction\":\"" + EscapeJsonString(factionOwnership)
                + "\",\"afterLoading\":" + (afterLoading ? "true" : "false")
                + (epoch >= 0L
                    ? ",\"epoch\":" + epoch.ToString(CultureInfo.InvariantCulture)
                    : string.Empty)
                + ",\"placements\":" + CreateJsonStringArray(placementRecords)
                + "}";
        }

        public static bool TryReadBuildBatchEpoch(string payloadJson, out long epoch)
        {
            epoch = -1L;
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return false;
            }

            var normalized = Normalize(payloadJson);
            return HasAction(normalized, PlaceBlueprintBatchAction)
                && TryReadLongProperty(normalized, "epoch", out epoch)
                && epoch >= 0L;
        }

        public static string CreateCutPlantPayload(int uniqueId, string blueprintId, int x, int y, int z, int worldX, int worldY, int worldZ)
        {
            return "{\"action\":\"" + CutPlantAction + "\",\"uniqueId\":" + uniqueId.ToString(CultureInfo.InvariantCulture)
                + ",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture)
                + ",\"worldX\":" + worldX.ToString(CultureInfo.InvariantCulture)
                + ",\"worldY\":" + worldY.ToString(CultureInfo.InvariantCulture)
                + ",\"worldZ\":" + worldZ.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static string CreateRegionOrderPayload(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            string areaType,
            string subType)
        {
            return "{\"action\":\"" + RegionOrderAction
                + "\",\"orderType\":\"" + EscapeJsonString(orderType)
                + "\",\"startX\":" + startX.ToString(CultureInfo.InvariantCulture)
                + ",\"startY\":" + startY.ToString(CultureInfo.InvariantCulture)
                + ",\"startZ\":" + startZ.ToString(CultureInfo.InvariantCulture)
                + ",\"endX\":" + endX.ToString(CultureInfo.InvariantCulture)
                + ",\"endY\":" + endY.ToString(CultureInfo.InvariantCulture)
                + ",\"endZ\":" + endZ.ToString(CultureInfo.InvariantCulture)
                + ",\"allowType\":\"" + EscapeJsonString(allowType)
                + "\",\"areaType\":\"" + EscapeJsonString(areaType)
                + "\",\"subType\":\"" + EscapeJsonString(subType)
                + "\"}";
        }

        public static string CreateEquipOrderPayload(string entityId, string blueprintId, int x, int y, int z)
        {
            return "{\"action\":\"" + EquipOrderAction
                + "\",\"entityId\":\"" + EscapeJsonString(entityId)
                + "\",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static bool TryReadPausePayload(string payloadJson, out bool paused)
        {
            paused = false;
            var normalized = Normalize(payloadJson);
            if (normalized.IndexOf("\"paused\":true", StringComparison.Ordinal) >= 0)
            {
                paused = true;
                return true;
            }

            if (normalized.IndexOf("\"paused\":false", StringComparison.Ordinal) >= 0)
            {
                paused = false;
                return true;
            }

            return false;
        }

        public static bool TryReadSpeedPayload(string payloadJson, out int speedIndex, out string action)
        {
            speedIndex = NormalSpeedIndex;
            action = string.Empty;
            var normalized = Normalize(payloadJson);

            if (normalized.IndexOf("\"action\":\"" + SetSpeedNormalAction + "\"", StringComparison.Ordinal) >= 0)
            {
                action = SetSpeedNormalAction;
                speedIndex = NormalSpeedIndex;
                return true;
            }

            if (TryReadIntProperty(normalized, "speedIndex", out speedIndex)
                || TryReadIntProperty(normalized, "speed", out speedIndex))
            {
                action = SetSpeedIndexAction;
                return true;
            }

            return false;
        }

        public static bool TryReadDigPayload(string payloadJson, out int startX, out int startY, out int startZ, out int endX, out int endY, out int endZ)
        {
            startX = 0;
            startY = 0;
            startZ = 0;
            endX = 0;
            endY = 0;
            endZ = 0;

            var normalized = Normalize(payloadJson);
            if (normalized.IndexOf("\"action\":\"" + DigVoxelAction + "\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            return TryReadIntProperty(normalized, "startX", out startX)
                && TryReadIntProperty(normalized, "startY", out startY)
                && TryReadIntProperty(normalized, "startZ", out startZ)
                && TryReadIntProperty(normalized, "endX", out endX)
                && TryReadIntProperty(normalized, "endY", out endY)
                && TryReadIntProperty(normalized, "endZ", out endZ);
        }

        public static bool TryReadBuildPayload(
            string payloadJson,
            out string blueprintId,
            out int x,
            out int y,
            out int z,
            out int angleY,
            out string buildingType,
            out string factionOwnership,
            out bool afterLoading)
        {
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;
            angleY = 0;
            buildingType = string.Empty;
            factionOwnership = string.Empty;
            afterLoading = false;

            var normalized = Normalize(payloadJson);
            if (normalized.IndexOf("\"action\":\"" + PlaceBlueprintAction + "\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            return TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "x", out x)
                && TryReadIntProperty(normalized, "y", out y)
                && TryReadIntProperty(normalized, "z", out z)
                && TryReadIntProperty(normalized, "angleY", out angleY)
                && TryReadStringProperty(normalized, "buildingType", out buildingType)
                && TryReadStringProperty(normalized, "faction", out factionOwnership)
                && TryReadBoolProperty(normalized, "afterLoading", out afterLoading);
        }

        public static bool TryReadBuildBatchPayload(
            string payloadJson,
            out string blueprintId,
            out string buildingType,
            out string factionOwnership,
            out bool afterLoading,
            out string[] placementRecords)
        {
            blueprintId = string.Empty;
            buildingType = string.Empty;
            factionOwnership = string.Empty;
            afterLoading = false;
            placementRecords = Array.Empty<string>();

            if (string.IsNullOrWhiteSpace(payloadJson)
                || Encoding.UTF8.GetByteCount(payloadJson) > BuildBatchPayloadMaxUtf8Bytes)
            {
                return false;
            }

            var normalized = Normalize(payloadJson);
            if (!HasAction(normalized, PlaceBlueprintBatchAction)
                || !TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                || string.IsNullOrWhiteSpace(blueprintId)
                || blueprintId.Length > 256
                || !TryReadStringProperty(normalized, "buildingType", out buildingType)
                || buildingType.Length > 128
                || !TryReadStringProperty(normalized, "faction", out factionOwnership)
                || string.IsNullOrWhiteSpace(factionOwnership)
                || factionOwnership.Length > 128
                || !TryReadBoolProperty(normalized, "afterLoading", out afterLoading)
                || !TryReadStringArrayProperty(normalized, "placements", out placementRecords)
                || placementRecords.Length == 0
                || placementRecords.Length > 512)
            {
                placementRecords = Array.Empty<string>();
                return false;
            }

            for (var i = 0; i < placementRecords.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(placementRecords[i]) || placementRecords[i].Length > 16384)
                {
                    placementRecords = Array.Empty<string>();
                    return false;
                }
            }

            return true;
        }

        public static bool TryReadCutPlantPayload(
            string payloadJson,
            out int uniqueId,
            out string blueprintId,
            out int x,
            out int y,
            out int z,
            out int worldX,
            out int worldY,
            out int worldZ)
        {
            uniqueId = 0;
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;
            worldX = 0;
            worldY = 0;
            worldZ = 0;

            var normalized = Normalize(payloadJson);
            if (normalized.IndexOf("\"action\":\"" + CutPlantAction + "\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            return TryReadIntProperty(normalized, "uniqueId", out uniqueId)
                && TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "x", out x)
                && TryReadIntProperty(normalized, "y", out y)
                && TryReadIntProperty(normalized, "z", out z)
                && TryReadIntProperty(normalized, "worldX", out worldX)
                && TryReadIntProperty(normalized, "worldY", out worldY)
                && TryReadIntProperty(normalized, "worldZ", out worldZ);
        }

        public static bool TryReadRegionOrderPayload(
            string payloadJson,
            out string orderType,
            out int startX,
            out int startY,
            out int startZ,
            out int endX,
            out int endY,
            out int endZ,
            out string allowType,
            out string areaType,
            out string subType)
        {
            orderType = string.Empty;
            startX = 0;
            startY = 0;
            startZ = 0;
            endX = 0;
            endY = 0;
            endZ = 0;
            allowType = string.Empty;
            areaType = string.Empty;
            subType = string.Empty;

            var normalized = Normalize(payloadJson);
            if (normalized.IndexOf("\"action\":\"" + RegionOrderAction + "\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            return TryReadStringProperty(normalized, "orderType", out orderType)
                && TryReadIntProperty(normalized, "startX", out startX)
                && TryReadIntProperty(normalized, "startY", out startY)
                && TryReadIntProperty(normalized, "startZ", out startZ)
                && TryReadIntProperty(normalized, "endX", out endX)
                && TryReadIntProperty(normalized, "endY", out endY)
                && TryReadIntProperty(normalized, "endZ", out endZ)
                && TryReadStringProperty(normalized, "allowType", out allowType)
                && TryReadStringProperty(normalized, "areaType", out areaType)
                && TryReadStringProperty(normalized, "subType", out subType);
        }

        public static bool TryReadEquipOrderPayload(
            string payloadJson,
            out string entityId,
            out string blueprintId,
            out int x,
            out int y,
            out int z)
        {
            entityId = string.Empty;
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;

            var normalized = Normalize(payloadJson);
            if (normalized.IndexOf("\"action\":\"" + EquipOrderAction + "\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            return TryReadStringProperty(normalized, "entityId", out entityId)
                && TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "x", out x)
                && TryReadIntProperty(normalized, "y", out y)
                && TryReadIntProperty(normalized, "z", out z);
        }

        private static string EscapeJsonString(string value)
        {
            var source = value ?? string.Empty;
            var escaped = new StringBuilder(source.Length);
            foreach (var ch in source)
            {
                switch (ch)
                {
                    case '\"':
                        escaped.Append("\\\"");
                        break;
                    case '\\':
                        escaped.Append("\\\\");
                        break;
                    case '\b':
                        escaped.Append("\\b");
                        break;
                    case '\f':
                        escaped.Append("\\f");
                        break;
                    case '\n':
                        escaped.Append("\\n");
                        break;
                    case '\r':
                        escaped.Append("\\r");
                        break;
                    case '\t':
                        escaped.Append("\\t");
                        break;
                    default:
                        if (ch < ' ')
                        {
                            escaped.Append("\\u");
                            escaped.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            escaped.Append(ch);
                        }

                        break;
                }
            }

            return escaped.ToString();
        }

        private static string Normalize(string payloadJson)
        {
            var source = payloadJson ?? string.Empty;
            var normalized = new StringBuilder(source.Length);
            var inString = false;
            var escaped = false;
            foreach (var ch in source)
            {
                if (inString)
                {
                    normalized.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '\"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '\"')
                {
                    inString = true;
                    normalized.Append(ch);
                }
                else if (!char.IsWhiteSpace(ch))
                {
                    normalized.Append(ch);
                }
            }

            return normalized.ToString();
        }

        private static bool HasAction(string normalizedJson, string action)
        {
            return TryReadStringProperty(normalizedJson, "action", out var actualAction)
                && string.Equals(actualAction, action, StringComparison.Ordinal);
        }

        private static string CreateJsonStringArray(string[] values)
        {
            var result = new StringBuilder("[");
            var items = values ?? Array.Empty<string>();
            for (var i = 0; i < items.Length; i++)
            {
                if (i > 0)
                {
                    result.Append(',');
                }

                result.Append('\"');
                result.Append(EscapeJsonString(items[i]));
                result.Append('\"');
            }

            result.Append(']');
            return result.ToString();
        }

        private static bool HasNonEmptyEntityIds(string[] entityIds)
        {
            if (entityIds.Length == 0)
            {
                return false;
            }

            foreach (var entityId in entityIds)
            {
                if (string.IsNullOrWhiteSpace(entityId))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadStringArrayProperty(string normalizedJson, string propertyName, out string[] values)
        {
            values = Array.Empty<string>();
            var marker = "\"" + propertyName + "\":[";
            var position = normalizedJson.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0)
            {
                return false;
            }

            position += marker.Length;
            var items = new List<string>();
            if (position < normalizedJson.Length && normalizedJson[position] == ']')
            {
                values = items.ToArray();
                return true;
            }

            while (position < normalizedJson.Length)
            {
                if (normalizedJson[position] != '\"')
                {
                    return false;
                }

                position++;
                var start = position;
                var escaped = false;
                while (position < normalizedJson.Length)
                {
                    var ch = normalizedJson[position];
                    if (escaped)
                    {
                        escaped = false;
                        position++;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        position++;
                        continue;
                    }

                    if (ch == '\"')
                    {
                        if (!TryUnescapeJsonString(normalizedJson.Substring(start, position - start), out var item))
                        {
                            return false;
                        }

                        items.Add(item);
                        position++;
                        break;
                    }

                    position++;
                }

                if (position >= normalizedJson.Length)
                {
                    return false;
                }

                if (normalizedJson[position] == ']')
                {
                    values = items.ToArray();
                    return true;
                }

                if (normalizedJson[position] != ',')
                {
                    return false;
                }

                position++;
            }

            return false;
        }

        private static bool TryReadIntProperty(string normalizedJson, string propertyName, out int value)
        {
            value = 0;
            var marker = "\"" + propertyName + "\":";
            var start = normalizedJson.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = start;
            while (end < normalizedJson.Length && (char.IsDigit(normalizedJson[end]) || normalizedJson[end] == '-'))
            {
                end++;
            }

            return end > start
                && int.TryParse(normalizedJson.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadLongProperty(string normalizedJson, string propertyName, out long value)
        {
            value = 0;
            if (!TryReadNumberToken(normalizedJson, propertyName, out var token))
            {
                return false;
            }

            return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadDoubleProperty(string normalizedJson, string propertyName, out double value)
        {
            value = 0;
            if (!TryReadNumberToken(normalizedJson, propertyName, out var token))
            {
                return false;
            }

            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadNumberToken(string normalizedJson, string propertyName, out string token)
        {
            token = string.Empty;
            var marker = "\"" + propertyName + "\":";
            var start = normalizedJson.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = start;
            while (end < normalizedJson.Length)
            {
                var ch = normalizedJson[end];
                if (!(char.IsDigit(ch) || ch == '-' || ch == '+' || ch == '.' || ch == 'e' || ch == 'E'))
                {
                    break;
                }

                end++;
            }

            if (end <= start)
            {
                return false;
            }

            token = normalizedJson.Substring(start, end - start);
            return true;
        }

        private static bool TryReadBoolProperty(string normalizedJson, string propertyName, out bool value)
        {
            value = false;
            var trueMarker = "\"" + propertyName + "\":true";
            if (normalizedJson.IndexOf(trueMarker, StringComparison.Ordinal) >= 0)
            {
                value = true;
                return true;
            }

            var falseMarker = "\"" + propertyName + "\":false";
            if (normalizedJson.IndexOf(falseMarker, StringComparison.Ordinal) >= 0)
            {
                value = false;
                return true;
            }

            return false;
        }

        private static bool TryReadStringProperty(string normalizedJson, string propertyName, out string value)
        {
            value = string.Empty;
            var marker = "\"" + propertyName + "\":\"";
            var start = normalizedJson.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = start;
            var escaped = false;
            while (end < normalizedJson.Length)
            {
                var ch = normalizedJson[end];
                if (escaped)
                {
                    escaped = false;
                    end++;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    end++;
                    continue;
                }

                if (ch == '"')
                {
                    return TryUnescapeJsonString(normalizedJson.Substring(start, end - start), out value);
                }

                end++;
            }

            return false;
        }

        private static bool TryUnescapeJsonString(string escapedValue, out string value)
        {
            var result = new StringBuilder(escapedValue.Length);
            for (var i = 0; i < escapedValue.Length; i++)
            {
                var ch = escapedValue[i];
                if (ch != '\\')
                {
                    if (ch < ' ')
                    {
                        value = string.Empty;
                        return false;
                    }

                    result.Append(ch);
                    continue;
                }

                if (++i >= escapedValue.Length)
                {
                    value = string.Empty;
                    return false;
                }

                switch (escapedValue[i])
                {
                    case '"': result.Append('"'); break;
                    case '\\': result.Append('\\'); break;
                    case '/': result.Append('/'); break;
                    case 'b': result.Append('\b'); break;
                    case 'f': result.Append('\f'); break;
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case 'u':
                        if (i + 4 >= escapedValue.Length
                            || !int.TryParse(escapedValue.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
                        {
                            value = string.Empty;
                            return false;
                        }

                        result.Append((char)codePoint);
                        i += 4;
                        break;
                    default:
                        value = string.Empty;
                        return false;
                }
            }

            value = result.ToString();
            return true;
        }
    }
}
