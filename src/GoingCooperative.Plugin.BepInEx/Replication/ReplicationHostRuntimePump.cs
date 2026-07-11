using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private void TryInstallReplicationHostRuntimePump(Harmony harmonyInstance)
        {
            if (!replicationConfigEnabled || !replicationConfigHostMode)
            {
                return;
            }

            var creatureManagerType = AccessTools.TypeByName("NSMedieval.Manager.CreatureManager");
            var updateMethod = creatureManagerType?.GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (updateMethod == null)
            {
                AppendPluginLog("Going Cooperative replication host runtime pump target missing: NSMedieval.Manager.CreatureManager.Update");
                return;
            }

            var postfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationHostRuntimePumpPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            harmonyInstance.Patch(updateMethod, postfix: postfix);
            LogReplicationInfo("Going Cooperative replication host runtime pump patched: "
                + updateMethod.DeclaringType?.FullName
                + "."
                + updateMethod.Name
                + " parameters="
                + updateMethod.GetParameters().Length.ToString(CultureInfo.InvariantCulture));
        }

        private static void ReplicationHostRuntimePumpPostfix()
        {
            var current = instance;
            if (!ReferenceEquals(current, null))
            {
                // CreatureManager updates once per frame. UpdateReplicationRuntime has its
                // own frame guard, so this remains a single transport pump per frame.
                current.UpdateReplicationRuntime();
            }
        }
    }
}
