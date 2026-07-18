using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static ulong replicationLastCapturedLocalCommandHash;
        private static float replicationLastCapturedLocalCommandRealtime;

        private void TryInstallReplicationCommandCapture(Harmony harmonyInstance)
        {
            TryLoadReplicationConfig(this);
            if (!replicationConfigEnabled && !replicationConfigMultiplayerMenuEnabled)
            {
                return;
            }

            var speedPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGameSpeedCommandPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var speedButtonPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGameSpeedButtonPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var speedIndexPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGameSpeedIndexPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var instancePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGameSpeedManagerInstancePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patchedCount = 0;
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, instancePostfix, "NSMedieval.Manager.GameSpeedManager", "Start", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, instancePostfix, "NSMedieval.Manager.GameSpeedManager", "OnUIInitComplete", Type.EmptyTypes);
            patchedCount += TryInstallReplicationManagementCapture(harmonyInstance);

            if (replicationConfigHostMode && !replicationConfigMultiplayerMenuEnabled)
            {
                var hostRegionPatchCount = TryInstallReplicationRegionCommandCapture(harmonyInstance);
                LogReplicationInfo("Going Cooperative replication host command/runtime capture patches="
                    + (patchedCount + hostRegionPatchCount).ToString(CultureInfo.InvariantCulture)
                    + " gameSpeedInstancePatches="
                    + patchedCount.ToString(CultureInfo.InvariantCulture));
                return;
            }

            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, speedButtonPostfix, "NSMedieval.Manager.GameSpeedManager", "OnUIButtonClicked", new[] { typeof(int) });
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, speedPostfix, "NSMedieval.Manager.GameSpeedManager", "SetSpeedPause", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, speedPostfix, "NSMedieval.Manager.GameSpeedManager", "SetSpeedNormal", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, speedPostfix, "NSMedieval.Manager.GameSpeedManager", "SetSpeedFast", Type.EmptyTypes);
            patchedCount += TryPatchReplicationCommandCaptureMethod(harmonyInstance, speedPostfix, "NSMedieval.Manager.GameSpeedManager", "SetSpeedFaster", Type.EmptyTypes);
            try
            {
                var speedManagerType = AccessTools.TypeByName("NSMedieval.Manager.GameSpeedManager");
                var speedIndexSetter = speedManagerType == null ? null : AccessTools.PropertySetter(speedManagerType, "CurrentSpeedIndex");
                if (speedIndexSetter != null)
                {
                    harmonyInstance.Patch(speedIndexSetter, postfix: speedIndexPostfix);
                    replicationEventSpeedHookReady = true;
                    patchedCount++;
                }
                else
                {
                    replicationEventSpeedHookReady = false;
                    LogReplicationWarning("Going Cooperative game-speed index capture setter missing; event speed authority will fail closed at runtime.");
                }
            }
            catch (Exception ex)
            {
                replicationEventSpeedHookReady = false;
                LogReplicationWarning("Going Cooperative game-speed index capture hook failed error=" + FormatReflectionExceptionDetail(ex));
            }
            patchedCount += TryInstallReplicationBuildCommandCapture(harmonyInstance);
            patchedCount += TryInstallReplicationRegionCommandCapture(harmonyInstance);
            patchedCount += TryInstallReplicationEquipmentCommandCapture(harmonyInstance);

            LogReplicationInfo("Going Cooperative replication command capture patches="
                + patchedCount.ToString(CultureInfo.InvariantCulture));
        }

        private static void ReplicationGameSpeedManagerInstancePostfix(MethodBase __originalMethod, object __instance)
        {
            if (__instance == null)
            {
                return;
            }

            gameSpeedManagerInstance = __instance;
            var current = instance;
            if (!ReferenceEquals(current, null))
            {
                current.UpdateReplicationRuntime();
            }
        }

        private static void ReplicationGameSpeedButtonPostfix(MethodBase __originalMethod, object __instance, int __0)
        {
            gameSpeedManagerInstance = __instance;
            SendReplicationLocalSpeedIntent(__0, "button:" + __0.ToString(CultureInfo.InvariantCulture));
        }

        private static void ReplicationGameSpeedCommandPostfix(MethodBase __originalMethod, object __instance)
        {
            gameSpeedManagerInstance = __instance;
            if (!TryGetReplicationSpeedIndexFromMethod(__originalMethod.Name, out var speedIndex))
            {
                return;
            }

            if (replicationConfigHostMode)
            {
                return;
            }
            if (replicationEventApplicationDepth > 0)
            {
                return;
            }
            SendReplicationLocalSpeedIntent(speedIndex, __originalMethod.Name);
        }

        private static void ReplicationGameSpeedIndexPostfix(object __instance, object __0)
        {
            gameSpeedManagerInstance = __instance;
            if (!replicationConfigHostMode || __0 == null) return;
            try
            {
                SendHostReplicationEventSpeedStateIfEnabled(
                    Convert.ToInt32(__0, CultureInfo.InvariantCulture),
                    "CurrentSpeedIndex");
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative event speed index capture failed error=" + FormatReflectionExceptionDetail(ex));
            }
        }

        private static bool TryGetReplicationSpeedIndexFromMethod(string methodName, out int speedIndex)
        {
            switch (methodName)
            {
                case "SetSpeedPause":
                    speedIndex = 0;
                    return true;
                case "SetSpeedNormal":
                    speedIndex = 1;
                    return true;
                case "SetSpeedFast":
                    speedIndex = 2;
                    return true;
                case "SetSpeedFaster":
                    speedIndex = 3;
                    return true;
                default:
                    speedIndex = -1;
                    return false;
            }
        }

        private static void SendReplicationLocalSpeedIntent(int speedIndex, string source)
        {
            if (IsReplicationCaptureModeOff(replicationConfigCommandCaptureMode))
            {
                return;
            }

            if (!ShouldSendReplicationLocalCommandIntent())
            {
                return;
            }

            if (speedIndex < 0 || speedIndex > 3)
            {
                instance?.LogReplicationWarning("Going Cooperative replication local speed intent ignored unsupported speedIndex="
                    + speedIndex.ToString(CultureInfo.InvariantCulture)
                    + " source="
                    + source);
                return;
            }

            var command = new LockstepCommand(
                ReplicationClientPeerId,
                ++replicationIntentSequence,
                0L,
                CommandKind.Speed,
                LockstepCommandPayloads.CreateSpeedIndexPayload(speedIndex));
            if (ShouldSkipDuplicateReplicationLocalCommand(command))
            {
                return;
            }

            if (IsReplicationCaptureModeShadow(replicationConfigCommandCaptureMode))
            {
                LogReplicationLocalCommandShadow(command, source);
                return;
            }

            SendReplicationLocalCommandIntent(command, source);
        }

        private static bool ShouldSendReplicationLocalCommandIntent()
        {
            return replicationConfigEnabled
                && !replicationConfigHostMode
                && replicationRuntimeStarted
                && replicationRemoteHelloReceived
                && replicationTransport != null
                && replicationSnapshotsReceived > 0
                && replicationLastSnapshotEntities > 0;
        }

        private static bool ShouldSkipDuplicateReplicationLocalCommand(LockstepCommand command)
        {
            var hash = command.PayloadHash;
            var now = Time.realtimeSinceStartup;
            var duplicateWindowSeconds = command.Kind == CommandKind.RegionOrder
                ? ReplicationRegionOrderDuplicateWindowSeconds
                : ReplicationLocalCommandDuplicateWindowSeconds;
            if (replicationLastCapturedLocalCommandHash == hash
                && now - replicationLastCapturedLocalCommandRealtime < duplicateWindowSeconds)
            {
                return true;
            }

            replicationLastCapturedLocalCommandHash = hash;
            replicationLastCapturedLocalCommandRealtime = now;
            return false;
        }

        private static bool IsReplicationCaptureModeOff(string mode)
        {
            return string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReplicationCaptureModeShadow(string mode)
        {
            return string.Equals(mode, "shadow", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReplicationCaptureModeAuthoritative(string mode)
        {
            return string.Equals(mode, "authoritative", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReplicationCaptureModeSendEnabled(string mode)
        {
            return string.Equals(mode, "send", StringComparison.OrdinalIgnoreCase)
                || IsReplicationCaptureModeAuthoritative(mode);
        }

        private static void LogReplicationLocalCommandShadow(LockstepCommand command, string source)
        {
            replicationLastIntentSummary = "shadow source="
                + source
                + " "
                + FormatRuntimeCommandSummary(command);
            instance?.LogReplicationInfo("Going Cooperative replication local command shadow source="
                + source
                + " "
                + FormatRuntimeCommandSummary(command));
        }

        private static void SendReplicationLocalCommandIntent(LockstepCommand command, string source)
        {
            PendingReplicationCommandIntent? pendingIntent = null;
            var pendingIntentKey = string.Empty;
            if (IsReplicationWorkerScheduleUpdateCommand(command))
            {
                command = CoalesceReplicationPendingWorkerScheduleIntent(command);
            }
            if (IsReplicationWorkerJobsUpdateCommand(command))
            {
                command = CoalesceReplicationPendingWorkerJobsIntent(command);
            }
            if (IsReplicationBuildBatchCommand(command)
                || IsReplicationWorkerScheduleUpdateCommand(command)
                || IsReplicationWorkerJobsUpdateCommand(command))
            {
                pendingIntentKey = BuildReplicationCommandIntentKey(command);
                var now = Time.realtimeSinceStartup;
                pendingIntent = new PendingReplicationCommandIntent(
                    command,
                    source,
                    now,
                    now - ReplicationCommandIntentRetrySeconds,
                    0);
                ReplicationPendingCommandIntents[pendingIntentKey] = pendingIntent;
            }

            try
            {
                if (replicationTransport == null)
                {
                    throw new InvalidOperationException("Replication transport is not connected.");
                }

                replicationTransport.Send(ReplicationPayloadCodec.ForCommandIntent(ReplicationClientPeerId, new ReplicationCommandIntent(command)));
                if (pendingIntent != null)
                {
                    pendingIntent.LastSentRealtime = Time.realtimeSinceStartup;
                    pendingIntent.SendCount = 1;
                }
                replicationIntentsSent++;
                replicationLastIntentSummary = "captured source="
                    + source
                    + " "
                    + FormatRuntimeCommandSummary(command);
                instance?.LogReplicationInfo("Going Cooperative replication local command intent sent source="
                    + source
                    + " "
                    + FormatRuntimeCommandSummary(command));
            }
            catch (Exception ex)
            {
                if (pendingIntent != null)
                {
                    // A throwing transport is still an attempted delivery. BuildBatch
                    // uses this count for its rollback cap; durable management intents
                    // use it to enter their low-rate retry cadence.
                    pendingIntent.LastSentRealtime = Time.realtimeSinceStartup;
                    pendingIntent.SendCount = Math.Max(1, pendingIntent.SendCount + 1);
                }
                instance?.LogReplicationWarning("Going Cooperative replication local command intent send failed source="
                    + source
                    + " attempts="
                    + (pendingIntent?.SendCount ?? 0).ToString(CultureInfo.InvariantCulture)
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private static void SendPendingReplicationCommandIntentsIfDue()
        {
            if (ReplicationPendingCommandIntents.Count == 0 || replicationTransport == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            var completed = new List<string>();
            var recoveryReason = string.Empty;
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                var pending = pair.Value;
                var pendingBuildBatch = IsReplicationBuildBatchCommand(pending.Command);
                var pendingWorkerSchedule = IsReplicationWorkerScheduleUpdateCommand(pending.Command);
                var pendingWorkerJobs = IsReplicationWorkerJobsUpdateCommand(pending.Command);
                var pendingDurableManagement = pendingWorkerSchedule || pendingWorkerJobs;
                var durableManagementAttempts = pending.SendCount + pending.AwaitingResultSendCount;
                if (pendingDurableManagement
                    && (!replicationRemoteHelloReceived
                        || now - replicationLastRemoteHelloRealtime > ReplicationRemoteHelloFreshSeconds))
                {
                    // Keep the coalesced desired row, but do not create an offline
                    // retry storm. A fresh hello resumes delivery automatically.
                    continue;
                }
                var resultRequestWindowExpired = pendingBuildBatch
                    && pending.HostResponded
                    && now - pending.AwaitingResultStartedRealtime >= ReplicationBuildBatchResultRequestWindowSeconds;
                var retrySeconds = pendingBuildBatch && pending.HostResponded
                    ? resultRequestWindowExpired
                        ? ReplicationBuildBatchResultDormantRetrySeconds
                        : ReplicationBuildBatchResultRequestRetrySeconds
                    : pendingDurableManagement
                        && durableManagementAttempts >= ReplicationCommandIntentMaxSends
                        ? ReplicationManagementIntentDormantRetrySeconds
                        : ReplicationCommandIntentRetrySeconds;
                if (resultRequestWindowExpired)
                {
                    if (!pending.ResultRequestWindowExpiredLogged)
                    {
                        pending.ResultRequestWindowExpiredLogged = true;
                        instance?.LogReplicationWarning("Going Cooperative replication build batch result request window expired key="
                            + pair.Key
                            + " requests="
                            + pending.AwaitingResultSendCount.ToString(CultureInfo.InvariantCulture)
                            + " retained=yes rollback=no lowRateRequests=yes source="
                            + pending.Source);
                    }

                    // A host-accepted transaction must never be rolled back merely because
                    // its exact commit manifest was delayed. Keep requesting the host's
                    // immutable manifest at a low rate for the session lifetime; otherwise
                    // the pending receipt and provisional registrations become permanently
                    // stuck after this diagnostic window.
                }

                if (now - pending.LastSentRealtime < retrySeconds)
                {
                    continue;
                }

                if (pendingBuildBatch
                    && !pending.HostResponded
                    && pending.SendCount >= ReplicationCommandIntentMaxSends)
                {
                    var rolledBack = RollbackReplicationProvisionalBuildViews(
                        pending.Command,
                        "retry-limit");
                    var remaining = CountReplicationProvisionalBuildViews(pending.Command.Sequence);
                    if (remaining == 0)
                    {
                        completed.Add(pair.Key);
                    }
                    else
                    {
                        pending.LastSentRealtime = now;
                        recoveryReason = "retry-limit-rollback-incomplete";
                    }
                    instance?.LogReplicationWarning("Going Cooperative replication build batch retry limit key="
                        + pair.Key
                        + " sends="
                        + pending.SendCount.ToString(CultureInfo.InvariantCulture)
                        + " rolledBack="
                        + rolledBack.ToString(CultureInfo.InvariantCulture)
                        + " remaining="
                        + remaining.ToString(CultureInfo.InvariantCulture)
                        + " source="
                        + pending.Source);
                    if (recoveryReason.Length > 0)
                    {
                        break;
                    }
                    continue;
                }

                try
                {
                    replicationTransport.Send(ReplicationPayloadCodec.ForCommandIntent(
                        ReplicationClientPeerId,
                        new ReplicationCommandIntent(pending.Command)));
                    pending.LastSentRealtime = now;
                    if (pending.HostResponded)
                    {
                        pending.AwaitingResultSendCount++;
                    }
                    else
                    {
                        pending.SendCount++;
                    }
                    replicationIntentsSent++;
                }
                catch (Exception ex)
                {
                    pending.LastSentRealtime = now;
                    if (pending.HostResponded)
                    {
                        pending.AwaitingResultSendCount++;
                    }
                    else
                    {
                        // Failed transport attempts count toward the same bounded retry cadence
                        // as successful sends that receive no acknowledgement.
                        pending.SendCount++;
                    }
                    instance?.LogReplicationWarning("Going Cooperative replication command intent retry failed key="
                        + pair.Key
                        + " schedule=" + (pendingWorkerSchedule ? "yes" : "no")
                        + " jobs=" + (pendingWorkerJobs ? "yes" : "no")
                        + " buildBatch=" + (pendingBuildBatch ? "yes" : "no")
                        + " attempts="
                        + (pending.HostResponded
                            ? pending.AwaitingResultSendCount
                            : pending.SendCount).ToString(CultureInfo.InvariantCulture)
                        + " error="
                        + ex.GetType().Name
                        + ":"
                        + ex.Message);
                }
            }

            for (var i = 0; i < completed.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(completed[i]);
            }

            if (recoveryReason.Length > 0)
            {
                ScheduleReplicationBuildBatchRecovery(recoveryReason);
            }
        }

        private const float ReplicationCommandIntentRetrySeconds = 0.5f;
        private const int ReplicationCommandIntentMaxSends = 12;
        private const float ReplicationManagementIntentDormantRetrySeconds = 5f;
        private const int ReplicationPendingWorkerScheduleIntentTargetLimit = 128;
        private const int ReplicationPendingWorkerJobsIntentTargetLimit = 128;
        private const float ReplicationBuildBatchResultRequestRetrySeconds = 2f;
        private const float ReplicationBuildBatchResultRequestWindowSeconds = 120f;
        private const float ReplicationBuildBatchResultDormantRetrySeconds = 15f;

        private static bool IsReplicationBuildBatchCommand(LockstepCommand command)
        {
            return command.Kind == CommandKind.Build
                && LockstepCommandPayloads.TryReadBuildBatchPayload(
                    command.PayloadJson,
                    out _,
                    out _,
                    out _,
                    out _,
                    out _);
        }

        private static bool IsReplicationWorkerScheduleUpdateCommand(LockstepCommand command)
        {
            return command.Kind == CommandKind.Custom
                && LockstepCommandPayloads.TryReadWorkerScheduleUpdatePayload(
                    command.PayloadJson,
                    out _,
                    out _,
                    out _);
        }

        private static bool IsReplicationWorkerJobsUpdateCommand(LockstepCommand command)
        {
            return command.Kind == CommandKind.Custom
                && LockstepCommandPayloads.TryReadWorkerJobsUpdatePayload(
                    command.PayloadJson,
                    out _,
                    out _,
                    out _,
                    out _);
        }

        private static LockstepCommand CoalesceReplicationPendingWorkerScheduleIntent(
            LockstepCommand newest)
        {
            if (!LockstepCommandPayloads.TryReadWorkerScheduleUpdatePayload(
                    newest.PayloadJson,
                    out var targetId,
                    out var newestHours,
                    out var newestValues))
            {
                return newest;
            }

            var prior = new List<KeyValuePair<string, PendingReplicationCommandIntent>>();
            var schedulePendingCount = 0;
            string? oldestOtherKey = null;
            PendingReplicationCommandIntent? oldestOther = null;
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                if (!LockstepCommandPayloads.TryReadWorkerScheduleUpdatePayload(
                        pair.Value.Command.PayloadJson,
                        out var pendingTargetId,
                        out _,
                        out _))
                {
                    continue;
                }

                schedulePendingCount++;
                if (string.Equals(targetId, pendingTargetId, StringComparison.Ordinal))
                {
                    prior.Add(pair);
                    continue;
                }

                if (oldestOther == null
                    || pair.Value.CreatedRealtime < oldestOther.CreatedRealtime
                    || (Math.Abs(pair.Value.CreatedRealtime - oldestOther.CreatedRealtime) < 0.0001f
                        && pair.Value.Command.Sequence < oldestOther.Command.Sequence))
                {
                    oldestOtherKey = pair.Key;
                    oldestOther = pair.Value;
                }
            }

            prior.Sort((left, right) =>
                left.Value.Command.Sequence.CompareTo(right.Value.Command.Sequence));
            var valuesByHour = new SortedDictionary<int, int>();
            for (var i = 0; i < prior.Count; i++)
            {
                if (!LockstepCommandPayloads.TryReadWorkerScheduleUpdatePayload(
                        prior[i].Value.Command.PayloadJson,
                        out _,
                        out var hours,
                        out var values))
                {
                    continue;
                }

                for (var cell = 0; cell < hours.Length; cell++)
                {
                    valuesByHour[hours[cell]] = values[cell];
                }
            }
            for (var cell = 0; cell < newestHours.Length; cell++)
            {
                valuesByHour[newestHours[cell]] = newestValues[cell];
            }

            for (var i = 0; i < prior.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(prior[i].Key);
            }

            if (schedulePendingCount - prior.Count >= ReplicationPendingWorkerScheduleIntentTargetLimit
                && oldestOtherKey != null)
            {
                ReplicationPendingCommandIntents.Remove(oldestOtherKey);
                instance?.LogReplicationWarning(
                    "Going Cooperative worker-schedule pending target cap reached; "
                    + "discarded oldest unresolved target sequence="
                    + oldestOther!.Command.Sequence.ToString(CultureInfo.InvariantCulture));
            }

            var mergedHours = new int[valuesByHour.Count];
            var mergedValues = new int[valuesByHour.Count];
            var index = 0;
            foreach (var pair in valuesByHour)
            {
                mergedHours[index] = pair.Key;
                mergedValues[index] = pair.Value;
                index++;
            }

            return new LockstepCommand(
                newest.PlayerId,
                newest.Sequence,
                newest.TargetTick,
                newest.Kind,
                LockstepCommandPayloads.CreateWorkerScheduleUpdatePayload(
                    targetId,
                    mergedHours,
                    mergedValues),
                newest.TargetStableId,
                newest.MapX,
                newest.MapY,
                newest.MapZ);
        }

        private static LockstepCommand CoalesceReplicationPendingWorkerJobsIntent(
            LockstepCommand newest)
        {
            if (!LockstepCommandPayloads.TryReadWorkerJobsUpdatePayload(
                    newest.PayloadJson,
                    out var targetId,
                    out var newestJobTypes,
                    out var newestPriorities,
                    out var newestActive))
            {
                return newest;
            }

            var prior = new List<KeyValuePair<string, PendingReplicationCommandIntent>>();
            var jobsPendingCount = 0;
            string? oldestOtherKey = null;
            PendingReplicationCommandIntent? oldestOther = null;
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                if (!LockstepCommandPayloads.TryReadWorkerJobsUpdatePayload(
                        pair.Value.Command.PayloadJson,
                        out var pendingTargetId,
                        out _,
                        out _,
                        out _))
                {
                    continue;
                }

                jobsPendingCount++;
                if (string.Equals(targetId, pendingTargetId, StringComparison.Ordinal))
                {
                    prior.Add(pair);
                    continue;
                }

                if (oldestOther == null
                    || pair.Value.CreatedRealtime < oldestOther.CreatedRealtime
                    || (Math.Abs(pair.Value.CreatedRealtime - oldestOther.CreatedRealtime) < 0.0001f
                        && pair.Value.Command.Sequence < oldestOther.Command.Sequence))
                {
                    oldestOtherKey = pair.Key;
                    oldestOther = pair.Value;
                }
            }

            prior.Sort((left, right) =>
                left.Value.Command.Sequence.CompareTo(right.Value.Command.Sequence));
            var valuesByJob = new SortedDictionary<int, ReplicationWorkerJobValue>();
            for (var i = 0; i < prior.Count; i++)
            {
                if (!LockstepCommandPayloads.TryReadWorkerJobsUpdatePayload(
                        prior[i].Value.Command.PayloadJson,
                        out _,
                        out var jobTypes,
                        out var priorities,
                        out var active))
                {
                    continue;
                }

                for (var cell = 0; cell < jobTypes.Length; cell++)
                {
                    valuesByJob[jobTypes[cell]] = new ReplicationWorkerJobValue(
                        priorities[cell],
                        active[cell]);
                }
            }
            for (var cell = 0; cell < newestJobTypes.Length; cell++)
            {
                valuesByJob[newestJobTypes[cell]] = new ReplicationWorkerJobValue(
                    newestPriorities[cell],
                    newestActive[cell]);
            }

            for (var i = 0; i < prior.Count; i++)
            {
                ReplicationPendingCommandIntents.Remove(prior[i].Key);
            }

            if (jobsPendingCount - prior.Count >= ReplicationPendingWorkerJobsIntentTargetLimit
                && oldestOtherKey != null)
            {
                ReplicationPendingCommandIntents.Remove(oldestOtherKey);
                instance?.LogReplicationWarning(
                    "Going Cooperative worker-jobs pending target cap reached; "
                    + "discarded oldest unresolved target sequence="
                    + oldestOther!.Command.Sequence.ToString(CultureInfo.InvariantCulture));
            }

            var mergedTypes = new int[valuesByJob.Count];
            var mergedPriorities = new int[valuesByJob.Count];
            var mergedActive = new bool[valuesByJob.Count];
            var index = 0;
            foreach (var pair in valuesByJob)
            {
                mergedTypes[index] = pair.Key;
                mergedPriorities[index] = pair.Value.Priority;
                mergedActive[index] = pair.Value.Active;
                index++;
            }

            return new LockstepCommand(
                newest.PlayerId,
                newest.Sequence,
                newest.TargetTick,
                newest.Kind,
                LockstepCommandPayloads.CreateWorkerJobsUpdatePayload(
                    targetId,
                    mergedTypes,
                    mergedPriorities,
                    mergedActive),
                newest.TargetStableId,
                newest.MapX,
                newest.MapY,
                newest.MapZ);
        }

        private static bool CompleteReplicationBuildBatchPendingIntent(string playerId, long commandSequence)
        {
            var commandKey = playerId + ":" + commandSequence.ToString(CultureInfo.InvariantCulture);
            if (ReplicationPendingCommandIntents.TryGetValue(commandKey, out var exact)
                && IsReplicationBuildBatchCommand(exact.Command))
            {
                ReplicationPendingCommandIntents.Remove(commandKey);
                return true;
            }

            // Player identifiers are formatted as detail tokens on the wire. Sequence is
            // unique for this two-player client lane, so retain a safe fallback for any
            // identifier whose whitespace was normalized in the diagnostic detail format.
            var fallbackKey = string.Empty;
            foreach (var pair in ReplicationPendingCommandIntents)
            {
                if (pair.Value.Command.Sequence == commandSequence
                    && IsReplicationBuildBatchCommand(pair.Value.Command))
                {
                    fallbackKey = pair.Key;
                    break;
                }
            }

            return fallbackKey.Length > 0 && ReplicationPendingCommandIntents.Remove(fallbackKey);
        }

        private sealed class PendingReplicationCommandIntent
        {
            public PendingReplicationCommandIntent(
                LockstepCommand command,
                string source,
                float createdRealtime,
                float lastSentRealtime,
                int sendCount)
            {
                Command = command;
                Source = source;
                CreatedRealtime = createdRealtime;
                LastSentRealtime = lastSentRealtime;
                SendCount = sendCount;
            }

            public LockstepCommand Command { get; }
            public string Source { get; }
            public float CreatedRealtime { get; }
            public float LastSentRealtime { get; set; }
            public int SendCount { get; set; }
            public bool HostResponded { get; private set; }
            public bool HostAccepted { get; private set; }
            public float AwaitingResultStartedRealtime { get; private set; }
            public int AwaitingResultSendCount { get; set; }
            public bool ResultRequestWindowExpiredLogged { get; set; }

            public void MarkHostResponded(bool accepted, float now)
            {
                HostAccepted = accepted;
                if (HostResponded)
                {
                    return;
                }

                HostResponded = true;
                AwaitingResultStartedRealtime = now;
                LastSentRealtime = now;
            }
        }

        private int TryPatchReplicationCommandCaptureMethod(
            Harmony harmonyInstance,
            HarmonyMethod postfix,
            string typeName,
            string methodName,
            Type[] parameterTypes)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication command capture type missing: " + typeName);
                    return 0;
                }

                var method = AccessTools.Method(type, methodName, parameterTypes);
                if (method == null)
                {
                    AppendPluginLog("Replication command capture method missing: "
                        + typeName
                        + "."
                        + methodName);
                    return 0;
                }

                harmonyInstance.Patch(method, postfix: postfix);
                AppendPluginLog("Replication command capture patched: "
                    + type.FullName
                    + "."
                    + method.Name);
                return 1;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication command capture patch failed: "
                    + typeName
                    + "."
                    + methodName
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return 0;
            }
        }
    }
}
