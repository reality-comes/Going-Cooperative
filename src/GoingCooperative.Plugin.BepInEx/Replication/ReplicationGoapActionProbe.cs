using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static long replicationGoapProbeMovementOverrides;
        private static long replicationGoapProbeMovementPassthroughs;
        private static float replicationGoapProbeNextMovementLogRealtime;

        private void TryInstallReplicationGoapActionProbe(Harmony harmonyInstance)
        {
            if (!replicationConfigGoapActionProbe)
            {
                return;
            }

            var agentPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGoapActionProbeAgentPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var actionPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGoapActionProbeActionPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patched = 0;
            patched += TryPatchReplicationGoapActionProbeMethods(harmonyInstance, "NSMedieval.Goap.Agent", agentPostfix,
                "StartNextGoal", "StartGoalInitSequence", "GoalInitSequenceFailed", "OnGoalEnded");
            patched += TryPatchReplicationGoapActionProbeMethods(harmonyInstance, "NSMedieval.Goap.GoapAction", actionPostfix,
                "Init", "Complete");
            LogReplicationInfo("Going Cooperative replication GOAP action probe patches="
                + patched.ToString(CultureInfo.InvariantCulture)
                + " entityId="
                + replicationConfigGoapActionProbeEntityId);
        }

        private int TryPatchReplicationGoapActionProbeMethods(Harmony harmonyInstance, string typeName, HarmonyMethod postfix, params string[] methodNames)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                AppendPluginLog("Going Cooperative replication GOAP action probe type missing: " + typeName);
                return 0;
            }

            var patched = 0;
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (var i = 0; i < methodNames.Length; i++)
            {
                for (var j = 0; j < methods.Length; j++)
                {
                    var method = methods[j];
                    if (!string.Equals(method.Name, methodNames[i], StringComparison.Ordinal) || method.ContainsGenericParameters)
                    {
                        continue;
                    }

                    harmonyInstance.Patch(method, postfix: postfix);
                    patched++;
                }
            }

            return patched;
        }

        private static void ReplicationGoapActionProbeAgentPostfix(object __instance, MethodBase __originalMethod)
        {
            if (!TryGetReplicationStableEntityId(__instance, out var entityId) || !ShouldLogReplicationGoapActionProbe(entityId))
            {
                return;
            }

            var currentGoal = ReadReplicationGoapProbeString(__instance, "get_CurrentGoalName");
            var preparingGoal = ReadReplicationGoapProbeString(__instance, "get_PreparingGoalName");
            var isPreparing = ReadReplicationGoapProbeString(__instance, "get_IsGoalPreparing");
            LogReplicationGoapActionProbe(entityId, "agent-" + __originalMethod.Name,
                "current=" + currentGoal + " preparing=" + isPreparing + ":" + preparingGoal);
        }

        private static void ReplicationGoapActionProbeActionPostfix(object __instance, MethodBase __originalMethod)
        {
            if (!TryGetReplicationGoapActionEntityId(__instance, out var entityId, out var entityDetail)
                || !ShouldLogReplicationGoapActionProbe(entityId))
            {
                return;
            }

            var actionId = ReadReplicationGoapProbeMember(__instance, "Id", "id");
            var goal = ReadReplicationGoapProbeMember(__instance, "Goal", "goal");
            LogReplicationGoapActionProbe(entityId, "action-" + __originalMethod.Name,
                "action=" + actionId + " goal=" + goal + " " + entityDetail);
        }

        private static void RecordReplicationGoapActionProbeMovement(string entityId, Vector3 requested, Vector3 applied, string mode)
        {
            if (!ShouldLogReplicationGoapActionProbe(entityId))
            {
                return;
            }

            if (string.Equals(mode, "override", StringComparison.Ordinal))
            {
                replicationGoapProbeMovementOverrides++;
            }
            else
            {
                replicationGoapProbeMovementPassthroughs++;
            }

            if (Time.realtimeSinceStartup < replicationGoapProbeNextMovementLogRealtime)
            {
                return;
            }

            replicationGoapProbeNextMovementLogRealtime = Time.realtimeSinceStartup + 0.5f;
            LogReplicationGoapActionProbe(entityId, "movement-" + mode,
                "requested=" + FormatReplicationPosition(requested)
                + " applied=" + FormatReplicationPosition(applied)
                + " overrides=" + replicationGoapProbeMovementOverrides.ToString(CultureInfo.InvariantCulture)
                + " passthroughs=" + replicationGoapProbeMovementPassthroughs.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ShouldLogReplicationGoapActionProbe(string entityId)
        {
            return replicationConfigGoapActionProbe
                && !string.IsNullOrWhiteSpace(entityId)
                && !string.IsNullOrWhiteSpace(replicationConfigGoapActionProbeEntityId)
                && string.Equals(entityId, replicationConfigGoapActionProbeEntityId, StringComparison.Ordinal);
        }

        private static void LogReplicationGoapActionProbe(string entityId, string phase, string detail)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            current.AppendPluginLog("Going Cooperative replication GOAP action probe side="
                + (replicationConfigHostMode ? "host" : "client")
                + " entityId=" + entityId + " phase=" + phase + " " + detail);
        }

        private static string ReadReplicationGoapProbeString(object target, string methodName)
        {
            return TryInvokeReplicationObjectMethod(target, methodName, out var value) && value != null
                ? value.ToString() ?? "<null>"
                : "<unavailable>";
        }

        private static string ReadReplicationGoapProbeMember(object target, params string[] names)
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (TryReadInstanceMemberValue(target, names[i], out var value) && value != null)
                {
                    return value.ToString() ?? "<null>";
                }
            }

            return "<unavailable>";
        }
    }
}
