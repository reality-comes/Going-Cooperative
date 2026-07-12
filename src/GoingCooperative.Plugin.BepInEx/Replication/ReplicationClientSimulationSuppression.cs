using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static long replicationClientSimulationSuppressedCalls;
        private static long replicationClientSimulationNextSuppressedLog = 1L;
        private static bool replicationBasicDeterminismAssistLogged;

        private void TryInstallReplicationClientSimulationSuppression(Harmony harmonyInstance)
        {
            TryLoadReplicationConfig(this);
            if ((!replicationConfigEnabled && !replicationConfigMultiplayerMenuEnabled)
                || (!replicationConfigMultiplayerMenuEnabled
                    && (replicationConfigHostMode || !replicationConfigSuppressClientSimulation)))
            {
                return;
            }

            var prefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationClientSimulationPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patchedCount = 0;

            patchedCount += TryPatchReplicationClientSimulationMethodsByName(
                harmonyInstance,
                prefix,
                "NSMedieval.Village.Map.Pathfinding.PathfinderAgentDriver",
                "Update");
            patchedCount += TryPatchReplicationClientSimulationMethodsByName(
                harmonyInstance,
                prefix,
                "NSMedieval.Village.Map.Pathfinding.PathfinderAgentDriver",
                "LateUpdate");
            patchedCount += TryPatchReplicationClientSimulationMethodsByName(
                harmonyInstance,
                prefix,
                "NSMedieval.Manager.CreatureManager",
                "Update");
            patchedCount += TryPatchReplicationClientSimulationMethodsByName(
                harmonyInstance,
                prefix,
                "NSMedieval.Manager.CreatureManager",
                "LateUpdate");

            LogReplicationInfo("Going Cooperative replication client simulation suppression patches="
                + patchedCount.ToString(CultureInfo.InvariantCulture));
        }

        private static bool ReplicationClientSimulationPrefix(MethodBase __originalMethod, object __instance)
        {
            var current = instance;
            if (!ReferenceEquals(current, null))
            {
                current.UpdateReplicationRuntime();
            }

            if (!ShouldSuppressReplicationClientSimulation(__originalMethod))
            {
                return true;
            }

            LogReplicationClientSimulationSuppression(__originalMethod);
            return false;
        }

        private static bool ShouldSuppressReplicationClientSimulation(MethodBase originalMethod)
        {
            if (multiplayerLoadingInProgress
                || !replicationConfigEnabled
                || replicationConfigHostMode
                || !replicationConfigApplySnapshots
                || !replicationConfigSuppressClientSimulation)
            {
                return false;
            }

            if (replicationConfigBasicDeterminismAssist)
            {
                return !ShouldAllowBasicDeterminismAssistSimulation(originalMethod);
            }

            return true;
        }

        private static bool ShouldAllowBasicDeterminismAssistSimulation(MethodBase originalMethod)
        {
            var declaringTypeName = originalMethod.DeclaringType?.FullName ?? string.Empty;
            if (declaringTypeName.IndexOf("PathfinderAgentDriver", StringComparison.Ordinal) >= 0)
            {
                if (replicationConfigBasicDeterminismAssistPathing)
                {
                    LogReplicationBasicDeterminismAssistActive("pathing");
                    return true;
                }

                return false;
            }

            if (declaringTypeName.IndexOf("CreatureManager", StringComparison.Ordinal) >= 0)
            {
                if (replicationConfigBasicDeterminismAssistCreatureManager)
                {
                    LogReplicationBasicDeterminismAssistActive("creature-manager");
                    return true;
                }

                return false;
            }

            return false;
        }

        private static void LogReplicationBasicDeterminismAssistActive(string domain)
        {
            if (replicationBasicDeterminismAssistLogged)
            {
                return;
            }

            replicationBasicDeterminismAssistLogged = true;
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            current.AppendPluginLog("Replication basic determinism assist active: client simulation suppression bypassed; host replication remains authoritative.");
            current.AppendPluginLog("Replication basic determinism assist domains allowed="
                + domain
                + " creatureManager="
                + replicationConfigBasicDeterminismAssistCreatureManager
                + " pathing="
                + replicationConfigBasicDeterminismAssistPathing);
        }

        private int TryPatchReplicationClientSimulationMethodsByName(
            Harmony harmonyInstance,
            HarmonyMethod prefix,
            string typeName,
            string methodName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication client simulation suppression type missing: " + typeName);
                    return 0;
                }

                var patched = 0;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.ContainsGenericParameters)
                    {
                        continue;
                    }

                    harmonyInstance.Patch(method, prefix: prefix);
                    AppendPluginLog("Replication client simulation suppression patched: "
                        + type.FullName
                        + "."
                        + method.Name);
                    patched++;
                }

                if (patched == 0)
                {
                    AppendPluginLog("Replication client simulation suppression method missing: "
                        + typeName
                        + "."
                        + methodName);
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication client simulation suppression patch failed: "
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

        private static void LogReplicationClientSimulationSuppression(MethodBase originalMethod)
        {
            var suppressed = ++replicationClientSimulationSuppressedCalls;
            if (suppressed < replicationClientSimulationNextSuppressedLog)
            {
                return;
            }

            replicationClientSimulationNextSuppressedLog = suppressed < 16L ? suppressed + 1L : suppressed * 2L;
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            current.AppendPluginLog("Replication client simulation suppressed count="
                + suppressed.ToString(CultureInfo.InvariantCulture)
                + " method="
                + ((originalMethod.DeclaringType?.FullName ?? "<unknown>")
                    + "."
                    + originalMethod.Name));
        }

    }
}
