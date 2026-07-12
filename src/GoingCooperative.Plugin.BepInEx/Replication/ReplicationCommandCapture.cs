using System;
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

            SendReplicationLocalSpeedIntent(speedIndex, __originalMethod.Name);
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
            try
            {
                replicationTransport?.Send(ReplicationPayloadCodec.ForCommandIntent(ReplicationClientPeerId, new ReplicationCommandIntent(command)));
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
                instance?.LogReplicationWarning("Going Cooperative replication local command intent send failed source="
                    + source
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
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
