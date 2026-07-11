using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly object ReplicationResultLifecycleLock = new object();
        private static readonly Dictionary<string, long> ReplicationResultLifecycleCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        private void TryInstallReplicationResultLifecycleProbes(Harmony harmonyInstance)
        {
            if (!replicationConfigEnabled || !replicationConfigResultLifecycleProbes)
            {
                return;
            }

            var instancePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationResultLifecycleInstancePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var instanceResultPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationResultLifecycleInstanceResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var staticPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationResultLifecycleStaticPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var staticResultPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationResultLifecycleStaticResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var ctorPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationResultLifecycleConstructorPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));

            var patchedCount = 0;

            patchedCount += TryPatchReplicationResultLifecycleConstructorByTypeNames(harmonyInstance, ctorPostfix, "NSMedieval.CommanderAI.Orders.CutPlantOrder", "NSMedieval.State.PlantMapResourceInstance");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Goals.FollowCutPlantOrderGoal", "CanStartFollowingOrder");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Goals.FollowCutPlantOrderGoal", "PrepareData");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Goals.FollowCutPlantOrderGoal", "GetNextAction");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Goals.FollowCutPlantOrderGoal", "EndGoalWith");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Goals.FollowCutPlantOrderGoal", "FailIfUnderWater");

            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Actions.MapResourceActions", "SpawnHarvestedResources");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Actions.MapResourceActions", "SpawnObtainedResources");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Goap.Actions.MapResourceActions", "HandleMapResourceObtainActionFinished");

            PatchReplicationResultLifecyclePlantMethods(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, ref patchedCount);
            PatchReplicationResultLifecyclePileMethods(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, ref patchedCount);
            PatchReplicationResultLifecycleBuildingMethods(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, ref patchedCount);

            LogReplicationInfo("Going Cooperative replication result lifecycle probes patches="
                + patchedCount.ToString(CultureInfo.InvariantCulture)
                + " enabled="
                + replicationConfigResultLifecycleProbes);
        }

        private void PatchReplicationResultLifecyclePlantMethods(
            Harmony harmonyInstance,
            HarmonyMethod instancePostfix,
            HarmonyMethod instanceResultPostfix,
            HarmonyMethod staticPostfix,
            HarmonyMethod staticResultPostfix,
            ref int patchedCount)
        {
            var typeName = "NSMedieval.State.PlantMapResourceInstance";
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SetCurrentOrder");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SetCutPhase");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ForceCutPhase");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SetHarvestPhase");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ForceHarvestPhase");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "Harvested");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "OnHealthDepleted");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "HealthDepletedListener");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "DestroyByFire");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "Dispose");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "OnOrderFail");

            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.MapResourceInstance", "SetCurrentOrder");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.MapResourceInstance", "SetPlayerOrder");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.MapResourceInstance", "Dispose");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.MapResourceInstance", "OnOrderFail");
        }

        private void PatchReplicationResultLifecyclePileMethods(
            Harmony harmonyInstance,
            HarmonyMethod instancePostfix,
            HarmonyMethod instanceResultPostfix,
            HarmonyMethod staticPostfix,
            HarmonyMethod staticResultPostfix,
            ref int patchedCount)
        {
            var typeName = "NSMedieval.Manager.ResourcePileManager";
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SpawnPile");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SpawnNewPile");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SpawnResource");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SpawnPileAnimalHarvest");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "TryAddToResourcePile");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "AddResources");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ForceDisposePile");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "DisposePilesById");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "OnPileDisposed");

            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileFactory", "ProducePile");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileFactory", "ProducePileView");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, "NSMedieval.Map.ResourceSpawner", "PlaceResourcePile");
        }

        private void PatchReplicationResultLifecycleBuildingMethods(
            Harmony harmonyInstance,
            HarmonyMethod instancePostfix,
            HarmonyMethod instanceResultPostfix,
            HarmonyMethod staticPostfix,
            HarmonyMethod staticResultPostfix,
            ref int patchedCount)
        {
            var typeName = "NSMedieval.BuildingComponents.BaseBuildingInstance";
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ConstructionStarted");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ConstructionCompleted");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ConstructionFailed");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ConstructionPaused");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "SetConstructionPhase");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "BuildingDeconstructed");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "BuildingRemovedSpawnResources");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "DestroyBuildingStabilityZero");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "DropConstructionResources");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "ObjectPlacedOnMap");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "Dispose");
            patchedCount += TryPatchReplicationResultLifecycleMethodsByName(harmonyInstance, instancePostfix, instanceResultPostfix, staticPostfix, staticResultPostfix, typeName, "OnResourceAdded");
        }

        private int TryPatchReplicationResultLifecycleConstructorByTypeNames(Harmony harmonyInstance, HarmonyMethod postfix, string typeName, params string[] parameterTypeNames)
        {
            var parameterTypes = new Type[parameterTypeNames.Length];
            for (var i = 0; i < parameterTypeNames.Length; i++)
            {
                var parameterType = ResolveTypeForPatch(parameterTypeNames[i]);
                if (parameterType == null)
                {
                    AppendPluginLog("Replication result lifecycle probe constructor parameter type missing: "
                        + typeName
                        + " param="
                        + parameterTypeNames[i]);
                    return 0;
                }

                parameterTypes[i] = parameterType;
            }

            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication result lifecycle probe constructor type missing: " + typeName);
                    return 0;
                }

                var constructor = AccessTools.Constructor(type, parameterTypes);
                if (constructor == null || constructor.ContainsGenericParameters)
                {
                    AppendPluginLog("Replication result lifecycle probe constructor missing: " + typeName);
                    return 0;
                }

                harmonyInstance.Patch(constructor, postfix: postfix);
                AppendPluginLog("Replication result lifecycle probe patched: " + type.FullName + "..ctor");
                return 1;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication result lifecycle probe constructor patch failed: "
                    + typeName
                    + " "
                    + ex.GetType().Name
                    + " "
                    + ex.Message);
                return 0;
            }
        }

        private int TryPatchReplicationResultLifecycleMethodsByName(
            Harmony harmonyInstance,
            HarmonyMethod instancePostfix,
            HarmonyMethod instanceResultPostfix,
            HarmonyMethod staticPostfix,
            HarmonyMethod staticResultPostfix,
            string typeName,
            string methodName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication result lifecycle probe type missing: " + typeName);
                    return 0;
                }

                var patched = 0;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) || method.ContainsGenericParameters)
                    {
                        continue;
                    }

                    var postfix = method.IsStatic
                        ? method.ReturnType == typeof(void) ? staticPostfix : staticResultPostfix
                        : method.ReturnType == typeof(void) ? instancePostfix : instanceResultPostfix;
                    harmonyInstance.Patch(method, postfix: postfix);
                    AppendPluginLog("Replication result lifecycle probe patched: "
                        + type.FullName
                        + "."
                        + method.Name
                        + " static="
                        + method.IsStatic
                        + " returns="
                        + FormatShortTypeName(method.ReturnType));
                    patched++;
                }

                if (patched == 0)
                {
                    AppendPluginLog("Replication result lifecycle probe method missing: " + typeName + "." + methodName);
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication result lifecycle probe patch failed: "
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

        private static void ReplicationResultLifecycleConstructorPostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            RecordReplicationResultLifecycleProbe(__originalMethod, "ctor", __instance, __args, null, hasResult: false);
        }

        private static void ReplicationResultLifecycleInstancePostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            RecordReplicationResultLifecycleProbe(__originalMethod, "instance", __instance, __args, null, hasResult: false);
        }

        private static void ReplicationResultLifecycleInstanceResultPostfix(MethodBase __originalMethod, object __instance, object[] __args, object __result)
        {
            RecordReplicationResultLifecycleProbe(__originalMethod, "instance-result", __instance, __args, __result, hasResult: true);
        }

        private static void ReplicationResultLifecycleStaticPostfix(MethodBase __originalMethod, object[] __args)
        {
            RecordReplicationResultLifecycleProbe(__originalMethod, "static", null, __args, null, hasResult: false);
        }

        private static void ReplicationResultLifecycleStaticResultPostfix(MethodBase __originalMethod, object[] __args, object __result)
        {
            RecordReplicationResultLifecycleProbe(__originalMethod, "static-result", null, __args, __result, hasResult: true);
        }

        private static void RecordReplicationResultLifecycleProbe(
            MethodBase originalMethod,
            string stage,
            object? target,
            object[]? args,
            object? result,
            bool hasResult)
        {
            if (!replicationConfigEnabled || !replicationConfigResultLifecycleProbes)
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var declaringType = originalMethod.DeclaringType;
            var key = (declaringType?.FullName ?? "<unknown>") + "." + originalMethod.Name;
            long count;
            lock (ReplicationResultLifecycleLock)
            {
                ReplicationResultLifecycleCounts.TryGetValue(key, out count);
                count++;
                ReplicationResultLifecycleCounts[key] = count;
            }

            if (!ShouldLogReplicationResultLifecycleProbeSample(count))
            {
                return;
            }

            var builder = new StringBuilder(512);
            builder.Append("Going Cooperative replication result probe role=")
                .Append(replicationConfigHostMode ? "host" : "client")
                .Append(" event=")
                .Append(key)
                .Append(" stage=")
                .Append(stage)
                .Append(" count=")
                .Append(count.ToString(CultureInfo.InvariantCulture));

            if (target != null)
            {
                builder.Append(" target=").Append(TrimFingerprintText(FormatCommandSurfaceValue(target), 420));
            }

            builder.Append(" args=").Append(TrimFingerprintText(FormatReplicationResultLifecycleArgs(args), 520));
            if (hasResult)
            {
                builder.Append(" result=").Append(TrimFingerprintText(FormatCommandSurfaceValue(result), 320));
            }

            current.LogReplicationInfo(builder.ToString());
        }

        private static bool ShouldLogReplicationResultLifecycleProbeSample(long count)
        {
            return count <= 32
                || count == 64
                || count == 128
                || count == 256
                || count % 500 == 0;
        }

        private static string FormatReplicationResultLifecycleArgs(object[]? args)
        {
            if (args == null || args.Length == 0)
            {
                return "<none>";
            }

            var builder = new StringBuilder(180);
            for (var i = 0; i < args.Length && i < 8; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append("arg")
                    .Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append("=")
                    .Append(FormatCommandSurfaceValue(args[i]));
            }

            if (args.Length > 8)
            {
                builder.Append(",...");
            }

            return builder.ToString();
        }
    }
}
