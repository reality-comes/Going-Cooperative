using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private static readonly object ReplicationWorldObjectDeltaLock = new object();
        private static readonly Dictionary<string, float> ReplicationWorldObjectDeltaLastSentAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationWorldObjectDeltaAppliedSpawnKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationWorldObjectDeltaRecentSpawnLocationAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<long, PendingReplicationWorldObjectDelta> replicationPendingWorldObjectDeltas = new Dictionary<long, PendingReplicationWorldObjectDelta>();
        private static readonly HashSet<long> replicationClientAppliedWorldObjectDeltaSequences = new HashSet<long>();
        private static readonly Queue<PendingReplicationClientWorldObjectDeltaApply> ReplicationClientPriorityWorldObjectDeltaApplies = new Queue<PendingReplicationClientWorldObjectDeltaApply>();
        private static readonly Queue<PendingReplicationClientWorldObjectDeltaApply> ReplicationClientPendingWorldObjectDeltaApplies = new Queue<PendingReplicationClientWorldObjectDeltaApply>();
        private static readonly Dictionary<string, PendingReplicationClientWorldObjectDeltaApply> ReplicationClientCoalescableWorldObjectDeltaApplies =
            new Dictionary<string, PendingReplicationClientWorldObjectDeltaApply>(StringComparer.Ordinal);
        private static readonly HashSet<long> ReplicationClientQueuedWorldObjectDeltaSequences = new HashSet<long>();
        private static readonly Dictionary<string, string> ReplicationAgentCarryResourceBlueprintByEntityId = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> ReplicationAgentCarryResourceAmountByEntityId = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GameObject> ReplicationAgentCarryResourceVisualByEntityId = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static readonly Dictionary<object, ReplicationAgentProgressOwner> ReplicationAgentProgressOwnerByBar = new Dictionary<object, ReplicationAgentProgressOwner>();
        private static readonly Dictionary<string, int> ReplicationAgentProgressLastPermilleByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationAgentProgressLoggedOwnerKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationAgentActionStatus> ReplicationAgentActionStatusByEntityId = new Dictionary<string, ReplicationAgentActionStatus>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationGoapActionPhase> ReplicationGoapActionPhaseByEntityId = new Dictionary<string, ReplicationGoapActionPhase>(StringComparer.Ordinal);
        private static readonly Queue<PendingReplicationActionAnimationOwner> ReplicationPendingActionAnimationOwners = new Queue<PendingReplicationActionAnimationOwner>();
        // Action animation callbacks can arrive after the game clears the action's owner/goal references.
        // Keep the identity observed during Init so concurrent workers remain distinguishable.
        private static readonly Dictionary<object, ReplicationGoapActionOwnerBinding> ReplicationGoapActionOwnerBindings = new Dictionary<object, ReplicationGoapActionOwnerBinding>();
        private static readonly Dictionary<string, ulong> ReplicationLastAgentCharacterStateSignatureByEntityId = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationAgentCharacterState> ReplicationClientAgentCharacterStateByEntityId = new Dictionary<string, ReplicationAgentCharacterState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationPuppetActionState> ReplicationPuppetActionStateByEntityId = new Dictionary<string, ReplicationPuppetActionState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ReplicationPuppetActionHandItemByEntityId = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GameObject> ReplicationPuppetActionHandPropByEntityId = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationPuppetActionHandPropCreatedByEntityId = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationAnimationDiagnosticLastLoggedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PendingReplicationCarryInference> ReplicationPendingCarryInferenceByBlueprintId = new Dictionary<string, PendingReplicationCarryInference>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PendingReplicationCarryDrop> ReplicationPendingCarryDropByEntityId = new Dictionary<string, PendingReplicationCarryDrop>(StringComparer.Ordinal);
        private static readonly Dictionary<long, ReplicationResourcePileStateSnapshotContext> ReplicationResourcePileStateSnapshotContexts = new Dictionary<long, ReplicationResourcePileStateSnapshotContext>();
        private static readonly Dictionary<long, ReplicationBuildingStateSnapshotContext> ReplicationBuildingStateSnapshotContexts = new Dictionary<long, ReplicationBuildingStateSnapshotContext>();
        private static readonly Queue<PendingReplicationResourcePileStateSnapshotApply> ReplicationPendingResourcePileStateSnapshotApplies = new Queue<PendingReplicationResourcePileStateSnapshotApply>();
        private static PendingReplicationResourcePileStateSnapshot? ReplicationPendingResourcePileStateSnapshot;
        private static bool replicationLastResourcePileStateSnapshotSignatureValid;
        private static ulong replicationLastResourcePileStateSnapshotSignature;
        private static bool replicationLastBuildingStateSnapshotSignatureValid;
        private static ulong replicationLastBuildingStateSnapshotSignature;
        private static float replicationClientResourcePileStateSnapshotApplyReadyRealtime;

        private void TryInstallReplicationWorldObjectDeltaCapture(Harmony harmonyInstance)
        {
            if ((!replicationConfigEnabled && !replicationConfigMultiplayerMenuEnabled)
                || (!replicationConfigMultiplayerMenuEnabled && !replicationConfigHostMode)
                || IsReplicationWorldObjectDeltaModeOff())
            {
                return;
            }

            var instancePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationWorldObjectDeltaInstancePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var staticPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationWorldObjectDeltaStaticPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var staticResultPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationWorldObjectDeltaStaticResultPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var terrainDigPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationTerrainDigCompletedPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var resourceCarryPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationResourceCarryActionPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var agentAnimationTriggerPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentAnimationTriggerPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var actionAnimationTriggerPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationActionAnimationTriggerPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var actionAnimationEventPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationActionAnimationEventPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var actionAnimationParameterPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationActionAnimationParameterPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var goapActionLifecyclePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationGoapActionLifecyclePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var agentAnimationOwnerOnlyPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentAnimationOwnerOnlyPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var agentProgressOwnerPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentProgressOwnerPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var agentProgressDestroyPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentProgressDestroyPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var agentProgressValuePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentProgressValuePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var agentActionStatusPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentActionStatusPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var workerSkillsExperiencePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationWorkerSkillsExperiencePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));

            var patchedCount = 0;
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.MapResourceInstance", "Dispose");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.PlantMapResourceInstance", "Dispose");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.State.ResourcePileInstance", "Dispose");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileManager", "SpawnNewPile");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileManager", "SpawnResource");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileManager", "TryAddToResourcePile");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileManager", "ForceDisposePile");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileManager", "DisposePilesById");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileManager", "OnPileDisposed");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.Manager.ResourcePileFactory", "ProducePile");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.View.HumanoidView", "EquipItem");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodsByName(harmonyInstance, instancePostfix, staticPostfix, staticResultPostfix, "NSMedieval.View.HumanoidView", "DropItem");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, terrainDigPostfix, "NSMedieval.Terrain.GroundManager", "OnDigActionCompleted", "NSMedieval.Vec3Int");
            patchedCount += TryPatchReplicationWorldObjectDeltaClosureMethod(harmonyInstance, resourceCarryPostfix, "NSMedieval.Goap.Actions.ResourceActions+<>c__DisplayClass9_0", "<StoreResourceOnStockpile>b__0");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentAnimationTriggerPostfix, "NSMedieval.Controllers.AnimationController", "TriggerAgentAnimation", "NSMedieval.Goap.IGoapAgentOwner", "System.String");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentAnimationTriggerPostfix, "NSMedieval.Controllers.AnimationController", "FailAgentAnimation", "NSMedieval.Goap.IGoapAgentOwner", "System.String");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionAnimationTriggerPostfix, "NSMedieval.Goap.ActionAnimationExtension", "TriggerAnimation", "NSMedieval.Goap.GoapAction", "System.String", "NSMedieval.Goap.ActionAnimationMode", "System.Boolean");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionAnimationEventPostfix, "NSMedieval.Goap.ActionAnimationExtension", "OnAnimationGoapEvent", "NSMedieval.Goap.GoapAction", "System.String", "System.Action");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionAnimationEventPostfix, "NSMedieval.Goap.ActionAnimationExtension", "CompleteActionOnAnimationGoapEvent", "NSMedieval.Goap.GoapAction", "System.String", "NSMedieval.Goap.ActionCompletionStatus");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionAnimationParameterPostfix, "NSMedieval.Goap.ActionAnimationExtension", "SetAnimationParameter", "NSMedieval.Goap.GoapAction", "System.String", "System.Boolean");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionAnimationParameterPostfix, "NSMedieval.Goap.ActionAnimationExtension", "SetAnimationParameter", "NSMedieval.Goap.GoapAction", "System.String", "System.Single");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionAnimationParameterPostfix, "NSMedieval.Goap.ActionAnimationExtension", "SetAnimationParameter", "NSMedieval.Goap.GoapAction", "System.String", "System.Int32");
            patchedCount += TryPatchReplicationGoapActionLifecycleMethods(harmonyInstance, goapActionLifecyclePostfix, "SetGoal", "Init", "Complete");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentAnimationOwnerOnlyPostfix, "NSMedieval.Controllers.AnimationController", "ResetTriggers", "NSMedieval.Goap.IGoapAgentOwner");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentAnimationOwnerOnlyPostfix, "NSMedieval.Controllers.AnimationController", "ForceQuitAgentAnimation", "NSMedieval.Goap.IGoapAgentOwner");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentProgressOwnerPostfix, "NSMedieval.State.HumanoidInstance", "GetProgressBar", "NSMedieval.FloatingOverlaySystem.OverlayProgressBarType");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentProgressDestroyPostfix, "NSMedieval.State.HumanoidInstance", "DestroyProgressBar", "NSMedieval.FloatingOverlaySystem.OverlayProgressBarType");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentProgressValuePostfix, "NSMedieval.FloatingOverlaySystem.ProgressBarFloatingElement", "UpdateValue", "System.Single");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, agentActionStatusPostfix, "NSMedieval.State.WorkerBehaviour", "GoalUpdated", "System.String", "System.Boolean");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, workerSkillsExperiencePostfix, "NSMedieval.Model.WorkerSkills", "AddExperience", "NSMedieval.StatsSystem.SkillType", "System.Single");

            LogReplicationInfo("Going Cooperative replication world object delta capture patches="
                + patchedCount.ToString(CultureInfo.InvariantCulture)
                + " mode="
                + replicationConfigWorldObjectDeltaMode);
        }

        private void TryInstallReplicationWorldObjectDeltaClientHooks(Harmony harmonyInstance)
        {
            if (!replicationConfigEnabled)
            {
                return;
            }

            var actionStatusUiPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationAgentActionStatusUiPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var localizedCurrentActionPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationLocalizedCurrentActionInfoPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patchedCount = TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, actionStatusUiPostfix, "NSMedieval.UI.WorkerEntryLayoutItemView", "DisplayCurrentAction");
            patchedCount += TryPatchReplicationWorldObjectDeltaMethodByTypeNames(harmonyInstance, localizedCurrentActionPostfix, "NSMedieval.UI.Utils.CreatureBaseUtils", "GetLocalizedCurrentActionInfo", "NSMedieval.State.CreatureBase");
            LogReplicationInfo("Going Cooperative replication world object delta client hooks patches="
                + patchedCount.ToString(CultureInfo.InvariantCulture));
        }

        private int TryPatchReplicationWorldObjectDeltaMethodsByName(
            Harmony harmonyInstance,
            HarmonyMethod instancePostfix,
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
                    AppendPluginLog("Replication world object delta type missing: " + typeName);
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
                        : instancePostfix;
                    harmonyInstance.Patch(method, postfix: postfix);
                    AppendPluginLog("Replication world object delta patched: "
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
                    AppendPluginLog("Replication world object delta method missing: " + typeName + "." + methodName);
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication world object delta patch failed: "
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

        private int TryPatchReplicationWorldObjectDeltaMethodByTypeNames(
            Harmony harmonyInstance,
            HarmonyMethod postfix,
            string typeName,
            string methodName,
            params string[] parameterTypeNames)
        {
            var parameterTypes = new Type[parameterTypeNames.Length];
            for (var i = 0; i < parameterTypeNames.Length; i++)
            {
                var parameterType = AccessTools.TypeByName(parameterTypeNames[i]);
                if (parameterType == null)
                {
                    AppendPluginLog("Replication world object delta parameter type missing: "
                        + typeName
                        + "."
                        + methodName
                        + " parameter="
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
                    AppendPluginLog("Replication world object delta type missing: " + typeName);
                    return 0;
                }

                var method = AccessTools.Method(type, methodName, parameterTypes);
                if (method == null || method.ContainsGenericParameters)
                {
                    AppendPluginLog("Replication world object delta method missing: " + typeName + "." + methodName);
                    return 0;
                }

                harmonyInstance.Patch(method, postfix: postfix);
                AppendPluginLog("Replication world object delta patched: "
                    + type.FullName
                    + "."
                    + method.Name
                    + " static="
                    + method.IsStatic
                    + " returns="
                    + FormatShortTypeName(method.ReturnType));
                return 1;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication world object delta patch failed: "
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

        private int TryPatchReplicationWorldObjectDeltaClosureMethod(
            Harmony harmonyInstance,
            HarmonyMethod postfix,
            string typeName,
            string methodName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication world object delta closure type missing: " + typeName);
                    return 0;
                }

                var method = AccessTools.Method(type, methodName);
                if (method == null || method.ContainsGenericParameters)
                {
                    AppendPluginLog("Replication world object delta closure method missing: " + typeName + "." + methodName);
                    return 0;
                }

                harmonyInstance.Patch(method, postfix: postfix);
                AppendPluginLog("Replication world object delta patched: "
                    + type.FullName
                    + "."
                    + method.Name
                    + " static="
                    + method.IsStatic
                    + " returns="
                    + FormatShortTypeName(method.ReturnType));
                return 1;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication world object delta closure patch failed: "
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

        private int TryPatchReplicationGoapActionLifecycleMethods(Harmony harmonyInstance, HarmonyMethod postfix, params string[] methodNames)
        {
            const string typeName = "NSMedieval.Goap.GoapAction";
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication GOAP action lifecycle type missing: " + typeName);
                    return 0;
                }

                var patched = 0;
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (method.ContainsGenericParameters || Array.IndexOf(methodNames, method.Name) < 0)
                    {
                        continue;
                    }

                    harmonyInstance.Patch(method, postfix: postfix);
                    AppendPluginLog("Replication GOAP action lifecycle patched: " + type.FullName + "." + method.Name);
                    patched++;
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication GOAP action lifecycle patch failed: " + ex.GetType().Name + " " + ex.Message);
                return 0;
            }
        }

        private static void ReplicationWorldObjectDeltaInstancePostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            RecordReplicationWorldObjectDelta(__originalMethod, __instance, __args, null, hasResult: false);
        }

        private static void ReplicationWorldObjectDeltaStaticPostfix(MethodBase __originalMethod, object[] __args)
        {
            RecordReplicationWorldObjectDelta(__originalMethod, null, __args, null, hasResult: false);
        }

        private static void ReplicationWorldObjectDeltaStaticResultPostfix(MethodBase __originalMethod, object[] __args, object __result)
        {
            RecordReplicationWorldObjectDelta(__originalMethod, null, __args, __result, hasResult: true);
        }

        private static void ReplicationTerrainDigCompletedPostfix(MethodBase __originalMethod, object __0, bool __result)
        {
            if (!__result
                || !replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !TryReadVec3IntLikeValue(__0, out var x, out var y, out var z))
            {
                return;
            }

            instance?.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "TerrainGroundDestroyed",
                0L,
                "terrain-ground",
                x,
                y,
                z,
                (__originalMethod.DeclaringType?.FullName ?? string.Empty) + "." + __originalMethod.Name));
        }

        private static void ReplicationResourceCarryActionPostfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff())
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var key = (__originalMethod.DeclaringType?.FullName ?? string.Empty) + "." + __originalMethod.Name;
            if (TryCreateReplicationResourceCarryDelta(key, __instance, __args, out var delta) && delta != null)
            {
                current.SendReplicationWorldObjectDelta(delta);
                current.LogReplicationInfo("Going Cooperative replication resource carry capture " + FormatReplicationWorldObjectDelta(delta));
            }
            else
            {
                current.LogReplicationInfo("Going Cooperative replication resource carry probe skipped source="
                    + key
                    + " closure="
                    + TrimFingerprintText(FormatCommandSurfaceValue(__instance, 1), 900)
                    + FormatReplicationResourceCarryArgs(__args));
            }
        }

        private static void ReplicationAgentAnimationTriggerPostfix(MethodBase __originalMethod, object __0, string __1)
        {
            RecordReplicationAgentAnimationDelta(__originalMethod, __0, __1);
        }

        private static void ReplicationActionAnimationTriggerPostfix(MethodBase __originalMethod, object[] __args)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || __args == null
                || __args.Length < 4
                || __args[0] == null)
            {
                return;
            }

            var trigger = __args[1] as string;
            trigger = string.IsNullOrWhiteSpace(trigger) ? string.Empty : trigger!.Trim();
            if (string.IsNullOrWhiteSpace(trigger))
            {
                return;
            }

            if (string.Equals(trigger, "Bored", StringComparison.Ordinal))
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var action = __args[0]!;
            var animationMode = __args[2]?.ToString() ?? "None";
            var isSequenced = __args[3] is bool parsedIsSequenced && parsedIsSequenced;
            if (TryCaptureReplicationSemanticWorkAnimation(
                    action,
                    trigger,
                    animationMode,
                    isSequenced,
                    __originalMethod))
            {
                return;
            }

            if (!TryGetReplicationGoapActionEntityId(action, trigger, out var entityId, out var entityDetail)
                || string.IsNullOrWhiteSpace(entityId))
            {
                current.LogReplicationInfo("Going Cooperative replication action animation trigger skipped trigger="
                    + FormatReplicationWorldObjectDetailToken(trigger)
                    + " source="
                    + (__originalMethod.DeclaringType?.FullName ?? "<unknown>")
                    + "."
                    + __originalMethod.Name
                    + " "
                    + entityDetail);
                return;
            }

            RecordReplicationActionAnimationDelta(current, __originalMethod, entityId, trigger, animationMode, isSequenced, entityDetail);
        }

        private static void ReplicationGoapActionLifecyclePostfix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || __instance == null)
            {
                return;
            }

            TryCacheReplicationGoapActionOwner(__instance, __originalMethod.Name);
            RecordReplicationGoapActionPhase(__instance, __originalMethod.Name, __args, __originalMethod);
        }

        private static void RecordReplicationGoapActionPhase(
            object action,
            string phase,
            object[]? lifecycleArguments,
            MethodBase lifecycleMethod)
        {
            if (TryRecordReplicationSemanticWorkPhase(action, phase, lifecycleArguments, lifecycleMethod))
            {
                return;
            }

            if (!replicationConfigActionPhaseReplication
                || (phase != "Init" && phase != "Complete")
                || !TryGetReplicationGoapActionEntityId(action, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId)
                || !TryReadReplicationGoapActionInfo(action, out var actionId, out var goalId, out var targetId, out var targetBlueprintId, out var targetX, out var targetY, out var targetZ))
            {
                return;
            }

            if (!string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal))
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var animatorStateDetail = CaptureReplicationAnimatorStateDetail(action, entityId);
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentActionPhase",
                uniqueId,
                actionId,
                targetX,
                targetY,
                targetZ,
                "entityId=" + entityId
                    + " phase=" + phase
                    + " actionB64=" + EncodeReplicationDetailBase64(actionId)
                    + " goalB64=" + EncodeReplicationDetailBase64(goalId)
                    + " targetId=" + targetId
                    + " targetBlueprintId=" + FormatReplicationWorldObjectDetailToken(targetBlueprintId)
                    + " targetGrid=" + targetX.ToString(CultureInfo.InvariantCulture) + "," + targetY.ToString(CultureInfo.InvariantCulture) + "," + targetZ.ToString(CultureInfo.InvariantCulture)
                    + (string.IsNullOrWhiteSpace(animatorStateDetail) ? string.Empty : " " + animatorStateDetail)
                    + " source=GoapAction." + phase));
        }

        private static void ReplicationActionAnimationEventPostfix(MethodBase __originalMethod, object __0, string __1, object __result)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || __0 == null)
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var eventName = string.IsNullOrWhiteSpace(__1) ? string.Empty : __1.Trim();
            var action = __result ?? __0;
            var resolved = TryGetReplicationGoapActionEntityId(action, out var entityId, out var entityDetail);
            current.LogReplicationInfo("Going Cooperative replication action animation event observed method="
                + (__originalMethod.DeclaringType?.FullName ?? "<unknown>")
                + "."
                + __originalMethod.Name
                + " entityId="
                + (resolved ? entityId : "<missing>")
                + " event="
                + FormatReplicationWorldObjectDetailToken(eventName)
                + " "
                + entityDetail);
        }

        private static void ReplicationActionAnimationParameterPostfix(MethodBase __originalMethod, object[] __args)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || !replicationConfigActionParameterReplication
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || __args == null
                || __args.Length < 3
                || __args[0] == null
                || !(__args[1] is string parameterName)
                || string.IsNullOrWhiteSpace(parameterName)
                || __args[2] == null)
            {
                return;
            }

            var action = __args[0];
            if (!TryReadReplicationGoapActionInfo(action, out _, out var goalId, out _, out _, out _, out _, out _)
                || !string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal)
                || !TryGetReplicationGoapActionEntityId(action, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            var value = __args[2];
            var valueKind = value is bool ? "bool" : value is float ? "float" : value is int ? "int" : string.Empty;
            if (string.IsNullOrWhiteSpace(valueKind))
            {
                return;
            }

            var valueText = value is bool boolValue
                ? (boolValue ? "true" : "false")
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(valueText))
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentAnimationParameter",
                uniqueId,
                parameterName,
                0,
                0,
                0,
                "entityId=" + entityId
                    + " goalB64=" + EncodeReplicationDetailBase64(goalId)
                    + " parameterB64=" + EncodeReplicationDetailBase64(parameterName)
                    + " valueKind=" + valueKind
                    + " value=" + valueText
                    + " source=" + (__originalMethod.DeclaringType?.FullName ?? "<unknown>") + "." + __originalMethod.Name));
        }

        private static void ReplicationAgentAnimationOwnerOnlyPostfix(MethodBase __originalMethod, object __0)
        {
            RecordReplicationAgentAnimationDelta(__originalMethod, __0, string.Empty);
        }

        private static void RecordReplicationActionAnimationDelta(
            GoingCooperativePlugin current,
            MethodBase originalMethod,
            string entityId,
            string trigger,
            string animationMode,
            bool isSequenced,
            string identityDetail)
        {
            if (replicationConfigCombatReplication
                && replicationConfigCombatPresentationReplication
                && string.Equals(trigger, "Attack", StringComparison.Ordinal))
            {
                // Attack is lifecycle-sensitive. The combat presentation contract
                // starts it only after the host has entered the real charge action.
                return;
            }

            var handItemId = string.Empty;
            var cacheUpdated = false;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationAgentActionStatusByEntityId.TryGetValue(entityId, out var status)
                    && status.HasStarted
                    && !IsReplicationIdleActionStatus(status.GoalId, status.StatusText))
                {
                    handItemId = status.ActionHandItemId;
                    ReplicationAgentActionStatusByEntityId[entityId] = new ReplicationAgentActionStatus(
                        status.GoalId,
                        status.StatusText,
                        status.HasStarted,
                        Time.realtimeSinceStartup,
                        trigger,
                        status.AnimatorStateDetail,
                        handItemId);
                    cacheUpdated = true;
                }
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentAnimationTriggered",
                uniqueId,
                trigger,
                0,
                0,
                0,
                "source="
                    + (originalMethod.DeclaringType?.FullName ?? "<unknown>")
                    + "."
                    + originalMethod.Name
                    + " entityId="
                    + entityId
                    + " trigger="
                    + trigger
                    + " animationMode="
                    + FormatReplicationWorldObjectDetailToken(animationMode)
                    + " isSequenced="
                    + (isSequenced ? "true" : "false")
                    + " animation="
                    + trigger
                    + " heartbeatAnimationCache="
                    + (cacheUpdated ? "updated" : "missing-status")
                    + " handItemB64="
                    + EncodeReplicationDetailBase64(handItemId)
                    + " "
                    + identityDetail));

            current.LogReplicationInfo("Going Cooperative replication action animation trigger sent entityId="
                + entityId
                + " trigger="
                + FormatReplicationWorldObjectDetailToken(trigger)
                + " cacheUpdated="
                + (cacheUpdated ? "yes" : "no")
                + " source="
                + (originalMethod.DeclaringType?.FullName ?? "<unknown>")
                + "."
                + originalMethod.Name
                + " "
                + identityDetail);
        }

        private static void ReplicationAgentProgressOwnerPostfix(object __instance, object __0, object __result)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || __instance == null
                || __result == null)
            {
                return;
            }

            if (!TryGetReplicationAgentOwnerEntityId(__instance, out var entityId, out var entityDetail)
                || string.IsNullOrWhiteSpace(entityId))
            {
                instance?.LogReplicationInfo("Going Cooperative replication agent progress owner skipped " + entityDetail);
                return;
            }

            var overlayType = __0?.ToString() ?? "Unknown";
            if (replicationConfigCombatReplication
                && replicationConfigCombatPresentationReplication
                && string.Equals(overlayType, "CombatCircle", StringComparison.Ordinal))
            {
                // The semantic charge event creates one client-local game Timer;
                // do not retain the bar or stream its value every frame. Remove a
                // stale pooled-bar mapping before the instance can emit an update.
                lock (ReplicationWorldObjectDeltaLock)
                {
                    ReplicationAgentProgressOwnerByBar.Remove(__result);
                }
                return;
            }
            var key = entityId + "|" + overlayType;
            var shouldLog = false;
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationAgentProgressOwnerByBar[__result] = new ReplicationAgentProgressOwner(entityId, overlayType);
                shouldLog = ReplicationAgentProgressLoggedOwnerKeys.Add(key);
            }

            if (shouldLog)
            {
                instance?.LogReplicationInfo("Going Cooperative replication agent progress owner mapped entityId="
                    + entityId
                    + " overlay="
                    + FormatReplicationWorldObjectDetailToken(overlayType)
                    + " bar="
                    + FormatShortTypeName(__result.GetType())
                    + " "
                    + entityDetail);
            }
        }

        private static void ReplicationAgentProgressDestroyPostfix(object __instance, object __0)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || __instance == null)
            {
                return;
            }

            if (!TryGetReplicationAgentOwnerEntityId(__instance, out var entityId, out var entityDetail)
                || string.IsNullOrWhiteSpace(entityId))
            {
                instance?.LogReplicationInfo("Going Cooperative replication agent progress destroy skipped " + entityDetail);
                return;
            }

            var overlayType = __0?.ToString() ?? "Unknown";
            var key = entityId + "|" + overlayType;
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationAgentProgressLastPermilleByKey.Remove(key);
                ReplicationAgentProgressLoggedOwnerKeys.Remove(key);
                RemoveReplicationAgentProgressOwnerMappingsUnderLock(entityId, overlayType);
            }
            if (replicationConfigCombatReplication
                && replicationConfigCombatPresentationReplication
                && string.Equals(overlayType, "CombatCircle", StringComparison.Ordinal))
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var handItemId = TryResolveReplicationActiveHandItemId(__instance, entityId, out var activeHandItemId, out _)
                ? activeHandItemId
                : string.Empty;
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentProgressCleared",
                uniqueId,
                overlayType,
                0,
                0,
                0,
                "entityId="
                    + entityId
                    + " overlay="
                    + FormatReplicationWorldObjectDetailToken(overlayType)
                    + " source=HumanoidInstance.DestroyProgressBar "
                    + entityDetail);
            current.SendReplicationWorldObjectDelta(delta);
        }

        private static void ReplicationAgentProgressValuePostfix(object __instance, float __0)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || __instance == null)
            {
                return;
            }

            ReplicationAgentProgressOwner owner;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationAgentProgressOwnerByBar.TryGetValue(__instance, out owner))
                {
                    return;
                }
            }

            if (replicationConfigCombatReplication
                && replicationConfigCombatPresentationReplication
                && string.Equals(owner.OverlayType, "CombatCircle", StringComparison.Ordinal))
            {
                return;
            }

            var clampedProgress = Mathf.Clamp01(__0);
            var progressPermille = Mathf.RoundToInt(clampedProgress * 1000f);
            var key = owner.EntityId + "|" + owner.OverlayType;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationAgentProgressLastPermilleByKey.TryGetValue(key, out var previousPermille)
                    && Math.Abs(previousPermille - progressPermille) < ReplicationAgentProgressMinPermilleStep
                    && progressPermille > 0
                    && progressPermille < 1000)
                {
                    return;
                }

                ReplicationAgentProgressLastPermilleByKey[key] = progressPermille;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var uniqueId = TryParseReplicationEntityNumericId(owner.EntityId, out var parsedId) ? parsedId : 0L;
            var delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentProgressUpdated",
                uniqueId,
                owner.OverlayType,
                0,
                0,
                0,
                "entityId="
                    + owner.EntityId
                    + " overlay="
                    + FormatReplicationWorldObjectDetailToken(owner.OverlayType)
                    + " progressPermille="
                    + progressPermille.ToString(CultureInfo.InvariantCulture)
                    + " progress="
                    + clampedProgress.ToString("0.###", CultureInfo.InvariantCulture)
                    + " source=ProgressBarFloatingElement.UpdateValue");
            current.SendReplicationWorldObjectDelta(delta);
        }

        private static void ReplicationAgentActionStatusPostfix(object __instance, string __0, bool __1)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || __instance == null)
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            if (!TryGetReplicationWorkerBehaviourEntityId(__instance, out var entityId, out var entityDetail)
                || string.IsNullOrWhiteSpace(entityId))
            {
                current.LogReplicationInfo("Going Cooperative replication agent action status skipped " + entityDetail);
                return;
            }

            var goalId = string.IsNullOrWhiteSpace(__0) ? "Idle" : __0.Trim();
            TryEndReplicationSemanticWorkForEntity(entityId, goalId, __1);
            var statusText = NormalizeReplicationAgentActionStatusText(goalId, __1);
            var animationToken = ResolveReplicationPuppetAnimationToken(goalId, statusText);
            var handItemId = TryResolveReplicationActiveHandItemId(__instance, entityId, out var activeHandItemId, out _)
                ? activeHandItemId
                : string.Empty;
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentActionStatus",
                uniqueId,
                FormatReplicationWorldObjectDetailToken(goalId),
                0,
                0,
                0,
                "entityId="
                    + entityId
                    + " goalB64="
                    + EncodeReplicationDetailBase64(goalId)
                    + " statusB64="
                    + EncodeReplicationDetailBase64(statusText)
                    + " hasStarted="
                    + (__1 ? "true" : "false")
                    + " animationB64="
                    + EncodeReplicationDetailBase64(animationToken)
                    + " handItemB64="
                    + EncodeReplicationDetailBase64(handItemId)
                    + " source=WorkerBehaviour.GoalUpdated "
                    + entityDetail);
            current.SendReplicationWorldObjectDelta(delta);
            RecordReplicationNeedsGoalStatus(entityId, goalId, __1);
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationAgentActionStatusByEntityId[entityId] = new ReplicationAgentActionStatus(
                    goalId,
                    statusText,
                    __1,
                    Time.realtimeSinceStartup,
                    animationToken,
                    string.Empty,
                    handItemId);
                if (__1 && !IsReplicationIdleActionStatus(goalId, statusText))
                {
                    PruneReplicationPendingActionAnimationOwners(Time.realtimeSinceStartup);
                    ReplicationPendingActionAnimationOwners.Enqueue(new PendingReplicationActionAnimationOwner(
                        entityId,
                        goalId,
                        statusText,
                        Time.realtimeSinceStartup));
                }
            }

            current.LogReplicationInfo("Going Cooperative replication agent action status sent entityId="
                + entityId
                + " goal="
                + FormatReplicationWorldObjectDetailToken(goalId)
                + " hasStarted="
                + (__1 ? "true" : "false")
                + " "
                + entityDetail);
            if (replicationConfigCharacterStateDiagnostics)
            {
                current.LogReplicationInfo("Going Cooperative replication character state diagnostic host "
                    + BuildReplicationCharacterActionProbe(__instance, entityId, goalId, statusText, __1));
            }
        }

        private static void ReplicationHumanoidExperiencePostfix(object __instance, object __0, float __1)
        {
            TrySendReplicationAgentSkillExperience(__instance, __0, __1, "HumanoidInstance.AddExperience");
        }

        private static void ReplicationWorkerSkillsExperiencePostfix(object __instance, object __0, float __1)
        {
            if (!TryFindReplicationAgentOwnerBySkills(__instance, out var owner, out var ownerDetail) || owner == null)
            {
                var current = instance;
                if (!ReferenceEquals(current, null) && replicationConfigCharacterStateDiagnostics)
                {
                    current.LogReplicationInfo("Going Cooperative replication character xp skipped skills-owner " + ownerDetail);
                }

                return;
            }

            TrySendReplicationAgentSkillExperience(owner, __0, __1, "WorkerSkills.AddExperience " + ownerDetail);
        }

        private static void TrySendReplicationAgentSkillExperience(object humanoid, object skillType, float amount, string source)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0
                || humanoid == null)
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            if (!TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out var entityDetail)
                || string.IsNullOrWhiteSpace(entityId))
            {
                if (replicationConfigCharacterStateDiagnostics)
                {
                    current.LogReplicationInfo("Going Cooperative replication character xp skipped " + entityDetail);
                }

                return;
            }

            if (!TryReadReplicationHumanoidSkillExperience(humanoid, skillType, out var totalExperience, out var skillDetail))
            {
                if (replicationConfigCharacterStateDiagnostics)
                {
                    current.LogReplicationInfo("Going Cooperative replication character xp skipped entityId="
                        + entityId
                        + " "
                        + skillDetail);
                }

                return;
            }

            var skillToken = FormatReplicationWorldObjectDetailToken(Convert.ToString(skillType, CultureInfo.InvariantCulture) ?? string.Empty);
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentSkillExperience",
                uniqueId,
                skillToken,
                0,
                0,
                0,
                "entityId="
                    + entityId
                    + " skill="
                    + skillToken
                    + " amount="
                    + amount.ToString("F3", CultureInfo.InvariantCulture)
                    + " totalXp="
                    + totalExperience.ToString("F3", CultureInfo.InvariantCulture)
                    + " source="
                    + source));

            current.LogReplicationInfo("Going Cooperative replication character xp sent entityId="
                + entityId
                + " skill="
                + skillToken
                + " amount="
                + amount.ToString("F3", CultureInfo.InvariantCulture)
                + " totalXp="
                + totalExperience.ToString("F3", CultureInfo.InvariantCulture));

            if (replicationConfigCharacterStateDiagnostics)
            {
                current.LogReplicationInfo("Going Cooperative replication character xp sent entityId="
                    + entityId
                    + " skill="
                    + skillToken
                    + " amount="
                    + amount.ToString("F2", CultureInfo.InvariantCulture)
                    + " totalXp="
                    + totalExperience.ToString("F2", CultureInfo.InvariantCulture)
                    + " "
                    + entityDetail
                    + " "
                    + skillDetail
                    + " source="
                    + source);
            }
        }

        private static string BuildReplicationCharacterActionProbe(object workerBehaviour, string entityId, string goalId, string statusText, bool hasStarted)
        {
            var builder = new StringBuilder(320);
            builder.Append("entityId=")
                .Append(entityId)
                .Append(" goal=")
                .Append(FormatReplicationWorldObjectDetailToken(goalId))
                .Append(" status=")
                .Append(FormatReplicationWorldObjectDetailToken(statusText))
                .Append(" hasStarted=")
                .Append(hasStarted ? "true" : "false");

            if (TryReadInstanceMemberValue(workerBehaviour, "IsIdle", out var isIdle) && isIdle != null
                || TryInvokeReplicationObjectMethod(workerBehaviour, "get_IsIdle", out isIdle) && isIdle != null)
            {
                builder.Append(" IsIdle=")
                    .Append(FormatReplicationWorldObjectDetailToken(Convert.ToString(isIdle, CultureInfo.InvariantCulture) ?? "<null>"));
            }

            if (TryReadInstanceMemberValue(workerBehaviour, "ActiveJobCombination", out var activeJob) && activeJob != null
                || TryInvokeReplicationObjectMethod(workerBehaviour, "get_ActiveJobCombination", out activeJob) && activeJob != null)
            {
                builder.Append(" ActiveJob=")
                    .Append(FormatReplicationWorldObjectDetailToken(Convert.ToString(activeJob, CultureInfo.InvariantCulture) ?? "<null>"));
            }

            if (TryReadInstanceMemberValue(workerBehaviour, "WorkerGoapAgent", out var goapAgent) && goapAgent != null
                || TryInvokeReplicationObjectMethod(workerBehaviour, "get_WorkerGoapAgent", out goapAgent) && goapAgent != null)
            {
                builder.Append(" GoapAgent=")
                    .Append(FormatShortTypeName(goapAgent.GetType()));
            }

            return TrimFingerprintText(builder.ToString(), 1200);
        }

        private static string BuildReplicationSkillSnapshotProbe(object humanoid, object skillType)
        {
            if ((!TryReadInstanceMemberValue(humanoid, "Skills", out var skills) || skills == null)
                && (!TryInvokeReplicationObjectMethod(humanoid, "get_Skills", out skills) || skills == null))
            {
                return "skills=missing";
            }

            var skill = TryInvokeReplicationObjectMethod(skills, "GetSkill", new[] { skillType }, out var resolvedSkill)
                ? resolvedSkill
                : null;
            if (skill == null)
            {
                return "skills=ok skill=missing skillsType=" + FormatShortTypeName(skills.GetType());
            }

            var builder = new StringBuilder(160);
            builder.Append("skills=ok skillType=")
                .Append(FormatShortTypeName(skill.GetType()));
            AppendReplicationProbeMember(builder, skill, "Id", "id");
            AppendReplicationProbeMember(builder, skill, "Level", "level");
            AppendReplicationProbeMember(builder, skill, "Experience", "xp");
            AppendReplicationProbeMember(builder, skill, "ExperienceAddedToday", "xpToday");
            return builder.ToString();
        }

        private static bool TryReadReplicationHumanoidSkillExperience(object humanoid, object skillType, out float experience, out string detail)
        {
            experience = 0f;
            if ((!TryReadInstanceMemberValue(humanoid, "Skills", out var skills) || skills == null)
                && (!TryInvokeReplicationObjectMethod(humanoid, "get_Skills", out skills) || skills == null))
            {
                detail = "skills=missing";
                return false;
            }

            var skill = TryInvokeReplicationObjectMethod(skills, "GetSkill", new[] { skillType }, out var resolvedSkill)
                ? resolvedSkill
                : null;
            if (skill == null)
            {
                detail = "skill=missing skillsType=" + FormatShortTypeName(skills.GetType());
                return false;
            }

            if ((TryReadInstanceMemberValue(skill, "Experience", out var experienceValue) && experienceValue != null)
                || (TryInvokeReplicationObjectMethod(skill, "get_Experience", out experienceValue) && experienceValue != null))
            {
                experience = Convert.ToSingle(experienceValue, CultureInfo.InvariantCulture);
                detail = "skill=ok xp=" + experience.ToString("F3", CultureInfo.InvariantCulture);
                return true;
            }

            detail = "experience=missing skillType=" + FormatShortTypeName(skill.GetType());
            return false;
        }

        private static void AppendReplicationProbeMember(StringBuilder builder, object value, string memberName, string label)
        {
            if ((TryReadInstanceMemberValue(value, memberName, out var memberValue) && memberValue != null)
                || (TryInvokeReplicationObjectMethod(value, "get_" + memberName, out memberValue) && memberValue != null))
            {
                builder.Append(" ")
                    .Append(label)
                    .Append("=")
                    .Append(FormatReplicationWorldObjectDetailToken(Convert.ToString(memberValue, CultureInfo.InvariantCulture) ?? "<null>"));
            }
        }

        private static bool TryInvokeReplicationObjectMethod(object owner, string methodName, object[] args, out object? value)
        {
            value = null;
            try
            {
                var methods = owner.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != args.Length)
                    {
                        continue;
                    }

                    value = method.Invoke(owner, args);
                    return value != null;
                }
            }
            catch
            {
                value = null;
            }

            return false;
        }

        private static void ReplicationAgentActionStatusUiPostfix(object __instance)
        {
            if (!replicationConfigEnabled
                || replicationConfigHostMode
                || __instance == null)
            {
                return;
            }

            if (!TryReadInstanceMemberValue(__instance, "HumanoidInstance", out var humanoid) || humanoid == null)
            {
                return;
            }

            if (!TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            ReplicationAgentActionStatus status;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationAgentActionStatusByEntityId.TryGetValue(entityId, out status)
                    || string.IsNullOrWhiteSpace(status.StatusText))
                {
                    return;
                }
            }

            ApplyReplicationAgentActionStatusToWorkerEntryUi(__instance, entityId, status.StatusText, "display-postfix", logSuccess: false);
        }

        private static void ReplicationLocalizedCurrentActionInfoPostfix(object __0, ref string __result)
        {
            if (!replicationConfigEnabled
                || replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || __0 == null)
            {
                return;
            }

            if (!TryGetReplicationAgentOwnerEntityId(__0, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            ReplicationAgentActionStatus status;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationAgentActionStatusByEntityId.TryGetValue(entityId, out status)
                    || string.IsNullOrWhiteSpace(status.StatusText)
                    || IsReplicationIdleActionStatus(status.GoalId, status.StatusText))
                {
                    return;
                }
            }

            __result = status.StatusText;
        }

        private void SendHostReplicationAgentActionHeartbeatIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRemoteHelloReceived)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now < replicationNextAgentActionHeartbeatRealtime)
            {
                return;
            }

            replicationNextAgentActionHeartbeatRealtime = now + ReplicationAgentActionHeartbeatSeconds;
            List<KeyValuePair<string, ReplicationAgentActionStatus>> activeStatuses = null!;
            lock (ReplicationWorldObjectDeltaLock)
            {
                activeStatuses = new List<KeyValuePair<string, ReplicationAgentActionStatus>>(ReplicationAgentActionStatusByEntityId.Count);
                foreach (var pair in ReplicationAgentActionStatusByEntityId)
                {
                    if (pair.Value.HasStarted
                        && !string.IsNullOrWhiteSpace(pair.Value.StatusText)
                        && !IsReplicationIdleActionStatus(pair.Value.GoalId, pair.Value.StatusText))
                    {
                        activeStatuses.Add(pair);
                    }
                }
            }

            for (var i = 0; i < activeStatuses.Count; i++)
            {
                var pair = activeStatuses[i];
                var entityId = pair.Key;
                var status = pair.Value;
                var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
                var animatorStateDetail = replicationConfigActionAnimatorStateSampling
                    ? CaptureReplicationActiveActionAnimatorState(entityId)
                    : string.Empty;
                SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    now,
                    "AgentActionHeartbeat",
                    uniqueId,
                    FormatReplicationWorldObjectDetailToken(status.GoalId),
                    0,
                    0,
                    0,
                    "entityId="
                        + entityId
                        + " goalB64="
                        + EncodeReplicationDetailBase64(status.GoalId)
                        + " statusB64="
                        + EncodeReplicationDetailBase64(status.StatusText)
                        + " hasStarted=true"
                    + " animationB64="
                    + EncodeReplicationDetailBase64(status.AnimationToken)
                    + " handItemB64="
                    + EncodeReplicationDetailBase64(status.ActionHandItemId)
                    + (!string.IsNullOrWhiteSpace(animatorStateDetail)
                        ? " " + animatorStateDetail
                        : string.Empty)
                    + " ttlMs="
                    + ReplicationPuppetActionTtlMs.ToString(CultureInfo.InvariantCulture)
                    + " source=agent-action-heartbeat"));
            }

            if (activeStatuses.Count > 0)
            {
                replicationLastWorldObjectDeltaSummary = "agent-action-heartbeat-sent count="
                    + activeStatuses.Count.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string CaptureReplicationActiveActionAnimatorState(string entityId)
        {
            return TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _)
                && view != null
                ? CaptureReplicationAnimatorStateDetail(view, entityId)
                : string.Empty;
        }

        private static void RecordReplicationAgentAnimationDelta(MethodBase originalMethod, object? agentOwner, string triggerName)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || applyingRuntimeCommandDepth > 0)
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var kind = ResolveReplicationAgentAnimationDeltaKind(originalMethod.Name);
            var normalizedTrigger = string.IsNullOrWhiteSpace(triggerName) ? string.Empty : triggerName.Trim();
            if (string.Equals(kind, "AgentAnimationTriggered", StringComparison.Ordinal)
                && !IsReplicationActionAnimationTrigger(normalizedTrigger))
            {
                return;
            }

            if (!string.Equals(kind, "AgentAnimationTriggered", StringComparison.Ordinal)
                && !IsReplicationActionAnimationTrigger(normalizedTrigger))
            {
                return;
            }

            if (!TryGetReplicationAgentOwnerEntityId(agentOwner, out var entityId, out var entityDetail)
                || string.IsNullOrWhiteSpace(entityId))
            {
                current.LogReplicationInfo("Going Cooperative replication agent animation skipped source="
                    + (originalMethod.DeclaringType?.FullName ?? "<unknown>")
                    + "."
                    + originalMethod.Name
                    + " "
                    + entityDetail);
                return;
            }

            var cacheUpdated = false;
            var animatorStateDetail = CaptureReplicationAnimatorStateDetail(agentOwner, entityId);
            var handItemId = TryResolveReplicationActiveHandItemId(agentOwner, entityId, out var activeHandItemId, out _)
                ? activeHandItemId
                : string.Empty;
            if (string.Equals(kind, "AgentAnimationTriggered", StringComparison.Ordinal)
                && TryCaptureReplicationSemanticWorkControllerAnimation(
                    entityId,
                    normalizedTrigger,
                    animatorStateDetail,
                    handItemId))
            {
                return;
            }

            if (replicationConfigAnimationDiagnostics)
            {
                current.LogReplicationInfo("Going Cooperative replication animation payload probe "
                    + BuildReplicationAnimatorCaptureProbe(agentOwner, entityId, normalizedTrigger, animatorStateDetail));
            }

            if (string.Equals(kind, "AgentAnimationTriggered", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(normalizedTrigger))
            {
                ReplicationAgentActionStatus status;
                lock (ReplicationWorldObjectDeltaLock)
                {
                    if (ReplicationAgentActionStatusByEntityId.TryGetValue(entityId, out status)
                        && status.HasStarted
                        && !IsReplicationIdleActionStatus(status.GoalId, status.StatusText))
                    {
                        if (!IsReplicationAnimationTriggerCompatibleWithGoal(status.GoalId, status.StatusText, normalizedTrigger))
                        {
                            current.LogReplicationInfo("Going Cooperative replication agent animation skipped incompatible entityId="
                                + entityId
                                + " goal="
                                + FormatReplicationWorldObjectDetailToken(status.GoalId)
                                + " status="
                                + FormatReplicationWorldObjectDetailToken(status.StatusText)
                                + " trigger="
                                + FormatReplicationWorldObjectDetailToken(normalizedTrigger));
                            return;
                        }

                        ReplicationAgentActionStatusByEntityId[entityId] = new ReplicationAgentActionStatus(
                            status.GoalId,
                            status.StatusText,
                            status.HasStarted,
                            Time.realtimeSinceStartup,
                            normalizedTrigger,
                            animatorStateDetail,
                            string.IsNullOrWhiteSpace(handItemId) ? status.ActionHandItemId : handItemId);
                        cacheUpdated = true;
                    }
                }
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var detail = "source="
                + (originalMethod.DeclaringType?.FullName ?? "<unknown>")
                + "."
                + originalMethod.Name
                + " entityId="
                + entityId
                + " trigger="
                + (string.IsNullOrWhiteSpace(normalizedTrigger) ? "<none>" : normalizedTrigger)
                + " heartbeatAnimationCache="
                + (cacheUpdated ? "updated" : "unchanged")
                + " handItemB64="
                + EncodeReplicationDetailBase64(handItemId)
                + " "
                + animatorStateDetail
                + " "
                + entityDetail;

            if (replicationConfigAnimationDiagnostics)
            {
                current.LogReplicationInfo("Going Cooperative replication animation diagnostic host "
                    + FormatReplicationAnimationDiagnostic(agentOwner, entityId, originalMethod.Name, normalizedTrigger, includeParameters: true));
            }

            var delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                kind,
                uniqueId,
                string.IsNullOrWhiteSpace(normalizedTrigger) ? "agent-animation" : normalizedTrigger,
                0,
                0,
                0,
                detail);
            current.SendReplicationWorldObjectDelta(delta);
        }

        private static string ResolveReplicationAgentAnimationDeltaKind(string methodName)
        {
            if (string.Equals(methodName, "ResetTriggers", StringComparison.Ordinal))
            {
                return "AgentAnimationReset";
            }

            if (string.Equals(methodName, "ForceQuitAgentAnimation", StringComparison.Ordinal)
                || string.Equals(methodName, "FailAgentAnimation", StringComparison.Ordinal))
            {
                return "AgentAnimationQuit";
            }

            return "AgentAnimationTriggered";
        }

        private static bool IsReplicationActionAnimationTrigger(string triggerName)
        {
            if (string.IsNullOrWhiteSpace(triggerName))
            {
                return false;
            }

            switch (triggerName.Trim())
            {
                case "Mining":
                case "Build":
                case "BuildDown":
                case "Harvest":
                case "Planting":
                case "PickUpPile":
                case "DropPile":
                case "ItemPickup":
                case "Producing":
                case "ResetTrap":
                case "Slaughter":
                case "Taming":
                case "TreatWounds":
                case "TreatSelf":
                case "Roping":
                case "StompFire":
                case "PourWaterOnFire":
                case "Shackling":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsReplicationAnimationTriggerCompatibleWithGoal(string goalId, string statusText, string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                return true;
            }

            if (string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal)
                || string.Equals(statusText, "Cutting", StringComparison.Ordinal))
            {
                return string.Equals(trigger, "Mining", StringComparison.Ordinal)
                    || string.Equals(trigger, "Harvest", StringComparison.Ordinal)
                    || string.Equals(trigger, "BuildDown", StringComparison.Ordinal);
            }

            if (string.Equals(goalId, "MineGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "MiningGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "DigGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "DiggingGoal", StringComparison.Ordinal))
            {
                return string.Equals(trigger, "Mining", StringComparison.Ordinal);
            }

            return true;
        }

        private static bool TryGetReplicationAgentOwnerEntityId(object? agentOwner, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "entity-source=missing-owner";
            if (agentOwner == null)
            {
                return false;
            }

            if (TryGetReplicationStableEntityId(agentOwner, out entityId))
            {
                detail = "entity-source=agentOwner owner=" + FormatShortTypeName(agentOwner.GetType());
                return true;
            }

            if (TryInvokeReplicationObjectMethod(agentOwner, "GetView", out var view)
                && view != null
                && TryGetReplicationViewEntityId(view, out entityId))
            {
                detail = "entity-source=agentOwner.GetView owner=" + FormatShortTypeName(agentOwner.GetType());
                return true;
            }

            detail = "entity-source=unresolved owner=" + TrimFingerprintText(FormatCommandSurfaceValue(agentOwner, 1), 300);
            return false;
        }

        private static bool TryGetReplicationWorkerBehaviourEntityId(object? workerBehaviour, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "worker-behaviour-source=missing";
            if (workerBehaviour == null)
            {
                return false;
            }

            if (TryGetReplicationAgentOwnerEntityId(workerBehaviour, out entityId, out detail))
            {
                detail = "workerBehaviour " + detail;
                return true;
            }

            if (TryInvokeReplicationObjectMethod(workerBehaviour, "get_WorkerGoapAgent", out var goapAgent)
                && goapAgent != null)
            {
                if (TryInvokeReplicationObjectMethod(goapAgent, "GetView", out var view)
                    && view != null
                    && TryGetReplicationViewEntityId(view, out entityId))
                {
                    detail = "workerBehaviour.WorkerGoapAgent.GetView agent=" + FormatShortTypeName(goapAgent.GetType());
                    return true;
                }

                if (TryInvokeReplicationObjectMethod(goapAgent, "get_AgentOwner", out var agentOwner)
                    && TryGetReplicationAgentOwnerEntityId(agentOwner, out entityId, out var ownerDetail))
                {
                    detail = "workerBehaviour.WorkerGoapAgent.AgentOwner " + ownerDetail;
                    return true;
                }
            }

            if (TryReadInstanceMemberValue(workerBehaviour, "WorkerGoapAgent", out goapAgent)
                && goapAgent != null
                && TryInvokeReplicationObjectMethod(goapAgent, "GetView", out var memberView)
                && memberView != null
                && TryGetReplicationViewEntityId(memberView, out entityId))
            {
                detail = "workerBehaviour.WorkerGoapAgent-field.GetView agent=" + FormatShortTypeName(goapAgent.GetType());
                return true;
            }

            detail = "worker-behaviour-source=unresolved behaviour=" + TrimFingerprintText(FormatCommandSurfaceValue(workerBehaviour, 1), 300);
            return false;
        }

        private static void RecordReplicationWorldObjectDelta(
            MethodBase originalMethod,
            object? target,
            object[]? args,
            object? result,
            bool hasResult)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff())
            {
                return;
            }

            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            var key = (originalMethod.DeclaringType?.FullName ?? string.Empty) + "." + originalMethod.Name;
            if (key.EndsWith(".TryAddToResourcePile", StringComparison.Ordinal)
                && (!hasResult || result is not bool added || added)
                && TryCreateReplicationWorldObjectDelta("ResourcePileAmountAdded", target, args, result, hasResult, key, out var addedDelta)
                && addedDelta != null)
            {
                current.SendReplicationWorldObjectDelta(addedDelta);
                RecordPendingReplicationCarryDrop(addedDelta);
                return;
            }

            if ((key.EndsWith(".SpawnNewPile", StringComparison.Ordinal)
                    || key.EndsWith(".SpawnResource", StringComparison.Ordinal)
                    || key.EndsWith(".ProducePile", StringComparison.Ordinal))
                && TryCreateReplicationWorldObjectDelta("ResourcePileSpawned", target, args, result, hasResult, key, out var pileDelta)
                && pileDelta != null)
            {
                current.SendReplicationWorldObjectDelta(pileDelta);
                RecordPendingReplicationCarryDrop(pileDelta);
                return;
            }

            if ((key.EndsWith(".ResourcePileInstance.Dispose", StringComparison.Ordinal)
                    || key.EndsWith(".ForceDisposePile", StringComparison.Ordinal)
                    || key.EndsWith(".DisposePilesById", StringComparison.Ordinal)
                    || key.EndsWith(".OnPileDisposed", StringComparison.Ordinal))
                && TryCreateReplicationWorldObjectDelta("ResourcePileDisposed", target, args, result, hasResult, key, out var disposedDelta)
                && disposedDelta != null)
            {
                current.SendReplicationWorldObjectDelta(disposedDelta);
                LogReplicationCarryProbeForWorldObjectDelta(disposedDelta);
                return;
            }

            if (key.EndsWith(".HumanoidView.EquipItem", StringComparison.Ordinal)
                && TryCreateReplicationAgentCarryDelta("AgentCarryEquipped", target, args, key, out var carryEquipDelta)
                && carryEquipDelta != null)
            {
                current.SendReplicationWorldObjectDelta(carryEquipDelta);
                return;
            }

            if (key.EndsWith(".HumanoidView.DropItem", StringComparison.Ordinal)
                && TryCreateReplicationAgentCarryDelta("AgentCarryCleared", target, args, key, out var carryDropDelta)
                && carryDropDelta != null)
            {
                current.SendReplicationWorldObjectDelta(carryDropDelta);
                return;
            }

            if (key.EndsWith(".Dispose", StringComparison.Ordinal))
            {
                if (TryCreateReplicationWorldObjectDelta("MapResourceDisposed", target, args, result, hasResult, key, out var delta) && delta != null)
                {
                    current.SendReplicationWorldObjectDelta(delta);
                }
            }
        }

        private static void RemoveReplicationAgentProgressOwnerMappingsUnderLock(string entityId, string overlayType)
        {
            List<object>? matchingBars = null;
            foreach (var pair in ReplicationAgentProgressOwnerByBar)
            {
                if (string.Equals(pair.Value.EntityId, entityId, StringComparison.Ordinal)
                    && string.Equals(pair.Value.OverlayType, overlayType, StringComparison.Ordinal))
                {
                    matchingBars ??= new List<object>();
                    matchingBars.Add(pair.Key);
                }
            }

            if (matchingBars == null) return;
            for (var i = 0; i < matchingBars.Count; i++)
                ReplicationAgentProgressOwnerByBar.Remove(matchingBars[i]);
        }

        private static bool TryCreateReplicationWorldObjectDelta(
            string deltaKind,
            object? target,
            object[]? args,
            object? result,
            bool hasResult,
            string source,
            out ReplicationWorldObjectDelta? delta)
        {
            delta = null;
            if (!TryExtractReplicationWorldObjectInfo(target, args, result, hasResult, out var uniqueId, out var blueprintId, out var x, out var y, out var z, out var detail))
            {
                return false;
            }

            var amountDetail = TryExtractReplicationWorldObjectAmount(target, args, result, hasResult, out var amount)
                ? " amount=" + amount.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                deltaKind,
                uniqueId,
                blueprintId,
                x,
                y,
                z,
                source + " " + detail + amountDetail);
            return true;
        }

        private static bool TryCreateReplicationAgentCarryDelta(
            string deltaKind,
            object? target,
            object[]? args,
            string source,
            out ReplicationWorldObjectDelta? delta)
        {
            delta = null;
            if (target == null || !TryGetReplicationViewEntityId(target, out var entityId))
            {
                return false;
            }

            var blueprintId = string.Empty;
            if (args != null && args.Length > 0 && args[0] != null)
            {
                TryReadReplicationWorldObjectStringMember(args[0], "ID", "id", out blueprintId);
                if (string.IsNullOrEmpty(blueprintId))
                {
                    TryInvokeReplicationStringMethod(args[0], "GetID", out blueprintId);
                }
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var x = 0;
            var y = 0;
            var z = 0;
            if (target is MonoBehaviour behaviour)
            {
                x = Mathf.RoundToInt(behaviour.transform.position.x);
                y = Mathf.RoundToInt(behaviour.transform.position.y / 3f);
                z = Mathf.RoundToInt(behaviour.transform.position.z);
            }

            delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                deltaKind,
                uniqueId,
                blueprintId,
                x,
                y,
                z,
                source + " entityId=" + entityId);
            return true;
        }

        private static bool TryCreateReplicationResourceCarryDelta(
            string source,
            object? closure,
            object[]? args,
            out ReplicationWorldObjectDelta? delta)
        {
            delta = null;
            if (closure == null)
            {
                return false;
            }

            var isStore = source.IndexOf("StoreResourceOnStockpile", StringComparison.Ordinal) >= 0;
            if (!TryReadInstanceMemberValue(closure, "action", out var action) || action == null)
            {
                return TryCreateReplicationPendingResourceCarryAmountDelta(source, args, out delta);
            }

            if (!TryGetReplicationGoapActionEntityId(action, out var entityId, out var entityDetail))
            {
                return TryCreateReplicationPendingResourceCarryAmountDelta(source, args, out delta);
            }

            var blueprintId = string.Empty;
            var amount = 0;
            var dropDetail = string.Empty;
            var gridX = 0;
            var gridY = 0;
            var gridZ = 0;
            if (isStore)
            {
                if (TryGetPendingReplicationCarryDrop(entityId, out var pendingDrop) && pendingDrop != null)
                {
                    blueprintId = pendingDrop.BlueprintId;
                    amount = pendingDrop.Amount;
                    gridX = pendingDrop.GridX;
                    gridY = pendingDrop.GridY;
                    gridZ = pendingDrop.GridZ;
                    dropDetail = " drop-amount-for="
                        + pendingDrop.SourceSequence.ToString(CultureInfo.InvariantCulture)
                        + " store-target=pending-spawn-pile";
                    ReplicationPendingCarryDropByEntityId.Remove(entityId);
                }
                else
                {
                    if (ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var cachedBlueprintId))
                    {
                        blueprintId = cachedBlueprintId;
                    }

                    if (ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var cachedAmount))
                    {
                        amount = Math.Max(1, cachedAmount);
                    }

                    if (TryResolveReplicationStoreTarget(action, closure, out var storeTarget, out var storeTargetSource)
                        && storeTarget != null)
                    {
                        var visited = new HashSet<int>();
                        if (TryExtractReplicationWorldObjectInfoFromObject(storeTarget, 0, visited, out _, out var targetBlueprintId, out var targetX, out var targetY, out var targetZ, out var targetDetail))
                        {
                            if (string.IsNullOrWhiteSpace(blueprintId))
                            {
                                blueprintId = targetBlueprintId;
                            }

                            gridX = targetX;
                            gridY = targetY;
                            gridZ = targetZ;
                            dropDetail += " store-target=" + targetDetail + " source=" + storeTargetSource;
                        }
                        else
                        {
                            dropDetail += " store-target-unresolved=" + TrimFingerprintText(FormatCommandSurfaceValue(storeTarget, 1), 300) + " source=" + storeTargetSource;
                        }
                    }
                }

                if (gridX == 0
                    && gridY == 0
                    && gridZ == 0
                    && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var storeView, out var storeViewDetail)
                    && storeView is MonoBehaviour storeBehaviour
                    && storeBehaviour.gameObject != null)
                {
                    var position = storeBehaviour.transform.position;
                    gridX = Mathf.RoundToInt(position.x);
                    gridY = Mathf.RoundToInt(position.y / 3f);
                    gridZ = Mathf.RoundToInt(position.z);
                    dropDetail += " store-target=fallback-worker-position "
                        + storeViewDetail
                        + " workerPosition="
                        + FormatReplicationPosition(position);
                }

                if (!string.IsNullOrWhiteSpace(blueprintId)
                    && (gridX != 0 || gridY != 0 || gridZ != 0)
                    && replicationConfigResourcePileStateSnapshots
                    && !TryGetReplicationPileAmountAt(blueprintId, gridX, gridY, gridZ, out _, out _)
                    && TryFindNearestReplicationResourcePileState(blueprintId, gridX, gridY, gridZ, out var nearestX, out var nearestY, out var nearestZ, out _, out var nearestDetail))
                {
                    gridX = nearestX;
                    gridY = nearestY;
                    gridZ = nearestZ;
                    dropDetail += " store-target=nearest-pile " + nearestDetail;
                }
            }
            else
            {
                TryExtractReplicationResourceCarryPayload(args, closure, out blueprintId, out amount);
                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    return false;
                }
            }

            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var carryAmount = UpdateReplicationHostCarryCache(entityId, blueprintId, amount, isStore);
            var pileStateDetail = isStore
                && replicationConfigResourcePileStateSnapshots
                && TryGetReplicationPileAmountAt(blueprintId, gridX, gridY, gridZ, out var pileAmount, out var pileAmountDetail)
                ? " pileAmount=" + pileAmount.ToString(CultureInfo.InvariantCulture) + " pileState=" + pileAmountDetail
                : string.Empty;
            delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                isStore ? "AgentCarryResourceCleared" : "AgentCarryResourceChanged",
                uniqueId,
                blueprintId,
                gridX,
                gridY,
                gridZ,
                source
                    + " entityId="
                    + entityId
                    + " amount="
                    + amount.ToString(CultureInfo.InvariantCulture)
                    + " carryAmount="
                    + carryAmount.ToString(CultureInfo.InvariantCulture)
                    + " "
                    + entityDetail
                    + dropDetail
                    + pileStateDetail
                    + FormatReplicationResourceCarryArgs(args));
            return true;
        }

        private static bool TryResolveReplicationStoreTarget(object action, object closure, out object? target, out string detail)
        {
            target = null;
            detail = "missing";
            if (!TryReadInstanceMemberValue(closure, "target", out var targetIndex) || targetIndex == null)
            {
                return false;
            }

            var visited = new HashSet<int>();
            if (TryExtractReplicationWorldObjectInfoFromObject(targetIndex, 0, visited, out _, out _, out _, out _, out _, out _))
            {
                target = targetIndex;
                detail = "closure.target";
                return true;
            }

            if ((!TryInvokeReplicationObjectMethod(action, "get_Goal", out var goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "Goal", out goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "goal", out goal) || goal == null))
            {
                detail = "goal-missing";
                return false;
            }

            var getTarget = FindReplicationInstanceMethod(goal.GetType(), "GetTarget", new[] { targetIndex.GetType() });
            if (getTarget == null)
            {
                detail = "get-target-missing targetIndex=" + FormatShortTypeName(targetIndex.GetType());
                return false;
            }

            try
            {
                target = getTarget.Invoke(goal, new[] { targetIndex });
                detail = target == null ? "goal-target-null" : "goal.GetTarget";
                return target != null;
            }
            catch (Exception ex)
            {
                detail = "goal.GetTarget " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryCreateReplicationPendingResourceCarryAmountDelta(
            string source,
            object[]? args,
            out ReplicationWorldObjectDelta? delta)
        {
            delta = null;
            if (!TryExtractReplicationResourceCarryPayload(args, new object(), out var blueprintId, out var amount)
                || string.IsNullOrWhiteSpace(blueprintId)
                || amount <= 0)
            {
                return false;
            }

            if (!ReplicationPendingCarryInferenceByBlueprintId.TryGetValue(blueprintId, out var pending)
                || Time.realtimeSinceStartup - pending.Realtime > ReplicationCarryPendingInferenceSeconds)
            {
                return false;
            }

            var carryAmount = UpdateReplicationHostCarryCache(pending.EntityId, blueprintId, amount, isStore: false);
            delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentCarryResourceChanged",
                pending.UniqueId,
                blueprintId,
                pending.GridX,
                pending.GridY,
                pending.GridZ,
                source
                    + " entityId="
                    + pending.EntityId
                    + " amount="
                    + amount.ToString(CultureInfo.InvariantCulture)
                    + " carryAmount="
                    + carryAmount.ToString(CultureInfo.InvariantCulture)
                    + " inferred-amount-for="
                    + pending.SourceSequence.ToString(CultureInfo.InvariantCulture)
                    + FormatReplicationResourceCarryArgs(args));
            ReplicationPendingCarryInferenceByBlueprintId.Remove(blueprintId);
            return true;
        }

        private static bool TryCreateReplicationInferredResourceCarryDelta(ReplicationWorldObjectDelta disposedDelta, out ReplicationWorldObjectDelta? delta)
        {
            delta = null;
            if (!string.Equals(disposedDelta.DeltaKind, "ResourcePileDisposed", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(disposedDelta.BlueprintId)
                || !TryFindNearestReplicationWorkerToDelta(disposedDelta, out var entityId, out var workerDetail))
            {
                return false;
            }

            var amount = TryReadReplicationWorldObjectDetailInt(disposedDelta.Detail, "amount", out var parsedAmount)
                ? parsedAmount
                : 0;
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentCarryResourceChanged",
                uniqueId,
                disposedDelta.BlueprintId,
                disposedDelta.GridX,
                disposedDelta.GridY,
                disposedDelta.GridZ,
                "inferred-from=" + disposedDelta.Sequence.ToString(CultureInfo.InvariantCulture)
                    + " entityId="
                    + entityId
                    + " amount="
                    + amount.ToString(CultureInfo.InvariantCulture)
                    + " "
                    + workerDetail);
            ReplicationPendingCarryInferenceByBlueprintId[disposedDelta.BlueprintId] = new PendingReplicationCarryInference(
                entityId,
                uniqueId,
                disposedDelta.GridX,
                disposedDelta.GridY,
                disposedDelta.GridZ,
                disposedDelta.Sequence,
                Time.realtimeSinceStartup);
            return true;
        }

        private static int UpdateReplicationHostCarryCache(string entityId, string blueprintId, int amount, bool isStore)
        {
            if (string.IsNullOrWhiteSpace(entityId))
            {
                return 0;
            }

            if (isStore)
            {
                if (amount > 0
                    && ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var currentStoreAmount)
                    && currentStoreAmount > amount)
                {
                    var remainingAmount = currentStoreAmount - amount;
                    ReplicationAgentCarryResourceAmountByEntityId[entityId] = remainingAmount;
                    if (!string.IsNullOrWhiteSpace(blueprintId))
                    {
                        ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = blueprintId;
                    }

                    return remainingAmount;
                }

                ReplicationAgentCarryResourceBlueprintByEntityId.Remove(entityId);
                ReplicationAgentCarryResourceAmountByEntityId.Remove(entityId);
                return 0;
            }

            if (string.IsNullOrWhiteSpace(blueprintId) || amount <= 0)
            {
                return 0;
            }

            if (ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var currentBlueprintId)
                && string.Equals(currentBlueprintId, blueprintId, StringComparison.Ordinal)
                && ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var currentAmount)
                && currentAmount > 1)
            {
                var totalAmount = currentAmount + amount;
                ReplicationAgentCarryResourceAmountByEntityId[entityId] = totalAmount;
                return totalAmount;
            }

            ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = blueprintId;
            ReplicationAgentCarryResourceAmountByEntityId[entityId] = amount;
            return amount;
        }

        private static void RecordPendingReplicationCarryDrop(ReplicationWorldObjectDelta spawnedDelta)
        {
            if (!string.Equals(spawnedDelta.DeltaKind, "ResourcePileSpawned", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(spawnedDelta.BlueprintId)
                || !TryReadReplicationWorldObjectDetailInt(spawnedDelta.Detail, "amount", out var amount)
                || amount <= 0
                || !TryFindNearestReplicationWorkerToDelta(spawnedDelta, out var entityId, out _))
            {
                return;
            }

            CleanupExpiredPendingReplicationCarryDrops();
            ReplicationPendingCarryDropByEntityId[entityId] = new PendingReplicationCarryDrop(
                spawnedDelta.BlueprintId,
                amount,
                spawnedDelta.GridX,
                spawnedDelta.GridY,
                spawnedDelta.GridZ,
                spawnedDelta.Sequence,
                Time.realtimeSinceStartup);
        }

        private static bool TryGetPendingReplicationCarryDrop(string entityId, out PendingReplicationCarryDrop? pendingDrop)
        {
            pendingDrop = null;
            CleanupExpiredPendingReplicationCarryDrops();
            if (!ReplicationPendingCarryDropByEntityId.TryGetValue(entityId, out var candidate)
                || Time.realtimeSinceStartup - candidate.Realtime > ReplicationCarryPendingDropSeconds)
            {
                ReplicationPendingCarryDropByEntityId.Remove(entityId);
                return false;
            }

            pendingDrop = candidate;
            return true;
        }

        private static void CleanupExpiredPendingReplicationCarryDrops()
        {
            if (ReplicationPendingCarryDropByEntityId.Count == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            var expiredEntityIds = new List<string>();
            foreach (var pair in ReplicationPendingCarryDropByEntityId)
            {
                if (now - pair.Value.Realtime > ReplicationCarryPendingDropSeconds)
                {
                    expiredEntityIds.Add(pair.Key);
                }
            }

            foreach (var entityId in expiredEntityIds)
            {
                ReplicationPendingCarryDropByEntityId.Remove(entityId);
            }
        }

        private void SendReplicationBuildBlueprintResultDeltaIfSupported(LockstepCommand command, RuntimeCommandResult result)
        {
            if (command.Kind != CommandKind.Build
                || !LockstepCommandPayloads.TryReadBuildPayload(
                    command.PayloadJson,
                    out var blueprintId,
                    out var x,
                    out var y,
                    out var z,
                    out var angleY,
                    out var buildingType,
                    out var factionOwnership,
                    out var afterLoading)
                || string.IsNullOrWhiteSpace(blueprintId))
            {
                return;
            }

            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                result.Invoked ? "BuildingBlueprintPlaced" : "BuildingBlueprintRejected",
                0L,
                blueprintId,
                x,
                y,
                z,
                "host-command player="
                    + command.PlayerId
                    + " commandSequence="
                    + command.Sequence.ToString(CultureInfo.InvariantCulture)
                    + " angleY="
                    + angleY.ToString(CultureInfo.InvariantCulture)
                    + " faction="
                    + FormatReplicationWorldObjectDetailToken(factionOwnership)
                    + " buildingType="
                    + FormatReplicationWorldObjectDetailToken(buildingType)
                    + " afterLoading="
                    + (afterLoading ? "true" : "false")
                    + " accepted="
                    + (result.Invoked ? "true" : "false")
                    + " result="
                    + FormatReplicationWorldObjectDetailToken(result.Detail)));
        }

        private void SendReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || ShouldSkipDuplicateReplicationWorldObjectDelta(delta))
            {
                return;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!IsTransientReplicationWorldObjectDelta(delta))
                {
                    replicationPendingWorldObjectDeltas[delta.Sequence] = new PendingReplicationWorldObjectDelta(delta);
                }
            }

            TrySendReplicationWorldObjectDelta(delta, isRetry: false);
        }

        private void SendReplicationWorldObjectSnapshotDelta(ReplicationWorldObjectDelta delta)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null)
            {
                return;
            }

            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForWorldObjectDelta(ReplicationHostPeerId, delta));
                replicationWorldObjectDeltasSent++;
                replicationLastWorldObjectDeltaSummary = "snapshot-sent " + FormatReplicationWorldObjectDelta(delta);
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication world object snapshot delta send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " "
                    + FormatReplicationWorldObjectDelta(delta));
            }
        }

        private void SendHostReplicationResourcePileStateSnapshotIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || replicationLastCollectedEntities <= 0
                || !replicationConfigResourcePileStateSnapshots)
            {
                return;
            }

            if (ReplicationPendingResourcePileStateSnapshot != null)
            {
                SendPendingReplicationResourcePileStateSnapshotChunk();
                return;
            }

            if (Time.realtimeSinceStartup < replicationNextResourcePileStateSnapshotRealtime)
            {
                return;
            }

            replicationNextResourcePileStateSnapshotRealtime = Time.realtimeSinceStartup + ReplicationResourcePileStateSnapshotSeconds;
            if (!TryCollectReplicationResourcePileStates(out var states, out var collectDetail))
            {
                replicationLastWorldObjectDeltaSummary = "pile-state-snapshot-skipped " + collectDetail;
                LogReplicationInfo("Going Cooperative replication pile state snapshot skipped " + collectDetail);
                return;
            }

            SortReplicationResourcePileStates(states);
            var signature = ComputeReplicationResourcePileStateSnapshotSignature(states);
            if (replicationLastResourcePileStateSnapshotSignatureValid
                && signature == replicationLastResourcePileStateSnapshotSignature)
            {
                replicationLastWorldObjectDeltaSummary = "pile-state-snapshot-skipped-unchanged count="
                    + states.Count.ToString(CultureInfo.InvariantCulture)
                    + " signature=0x"
                    + signature.ToString("x16", CultureInfo.InvariantCulture)
                    + " "
                    + collectDetail;
                return;
            }

            replicationLastResourcePileStateSnapshotSignatureValid = true;
            replicationLastResourcePileStateSnapshotSignature = signature;
            var snapshotId = ++replicationResourcePileStateSnapshotSequence;
            var count = states.Count;
            ReplicationPendingResourcePileStateSnapshot = new PendingReplicationResourcePileStateSnapshot(
                snapshotId,
                states,
                collectDetail
                    + " signature=0x"
                    + signature.ToString("x16", CultureInfo.InvariantCulture));
            SendPendingReplicationResourcePileStateSnapshotChunk();
        }

        private void SendPendingReplicationResourcePileStateSnapshotChunk()
        {
            var pending = ReplicationPendingResourcePileStateSnapshot;
            if (pending == null)
            {
                return;
            }

            var count = pending.States.Count;
            var snapshotId = pending.SnapshotId;
            if (!pending.BeginSent)
            {
                pending.BeginSent = true;
                SendReplicationWorldObjectSnapshotDelta(new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    Time.realtimeSinceStartup,
                    "ResourcePileStateSnapshotBegin",
                    0L,
                    "resource-pile-state",
                    0,
                    0,
                    0,
                    "snapshotId="
                        + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " count="
                        + count.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + pending.CollectDetail));
            }

            var sentThisFrame = 0;
            while (pending.NextIndex < count && sentThisFrame < ReplicationResourcePileStateSnapshotChunkMaxPiles)
            {
                var state = pending.States[pending.NextIndex];
                SendReplicationWorldObjectSnapshotDelta(new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    Time.realtimeSinceStartup,
                    "ResourcePileState",
                    state.UniqueId,
                    state.BlueprintId,
                    state.GridX,
                    state.GridY,
                    state.GridZ,
                    "snapshotId="
                        + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " index="
                        + pending.NextIndex.ToString(CultureInfo.InvariantCulture)
                        + " count="
                        + count.ToString(CultureInfo.InvariantCulture)
                        + " amount="
                        + state.Amount.ToString(CultureInfo.InvariantCulture)
                        + " pileAmount="
                        + state.Amount.ToString(CultureInfo.InvariantCulture)
                        + " source=authoritative-pile-snapshot"));
                pending.NextIndex++;
                sentThisFrame++;
            }

            if (pending.NextIndex < count)
            {
                replicationLastWorldObjectDeltaSummary = "pile-state-snapshot-streaming snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " sent="
                    + pending.NextIndex.ToString(CultureInfo.InvariantCulture)
                    + "/"
                    + count.ToString(CultureInfo.InvariantCulture);
                return;
            }

            SendReplicationWorldObjectSnapshotDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "ResourcePileStateSnapshotEnd",
                0L,
                "resource-pile-state",
                0,
                0,
                0,
                "snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " count="
                    + count.ToString(CultureInfo.InvariantCulture)
                    + " "
                    + pending.CollectDetail));

            replicationLastWorldObjectDeltaSummary = "pile-state-snapshot-sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + count.ToString(CultureInfo.InvariantCulture);
            LogReplicationInfo("Going Cooperative replication pile state snapshot sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + count.ToString(CultureInfo.InvariantCulture)
                + " "
                + pending.CollectDetail
                + " chunk="
                + ReplicationResourcePileStateSnapshotChunkMaxPiles.ToString(CultureInfo.InvariantCulture));
            ReplicationPendingResourcePileStateSnapshot = null;
        }

        private void SendHostReplicationAgentCarryStateSnapshotIfDue()
        {
            if (replicationConfigResourceContainerReplication
                || !replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || replicationLastSnapshotEntities <= 0
                || Time.realtimeSinceStartup < replicationNextAgentCarryStateSnapshotRealtime)
            {
                return;
            }

            replicationNextAgentCarryStateSnapshotRealtime = Time.realtimeSinceStartup + ReplicationAgentCarryStateSnapshotSeconds;
            if (!TryCollectReplicationAgentCarryStates(out var states, out var collectDetail))
            {
                replicationLastWorldObjectDeltaSummary = "agent-carry-state-snapshot-skipped " + collectDetail;
                LogReplicationInfo("Going Cooperative replication agent carry state snapshot skipped " + collectDetail);
                return;
            }

            var snapshotId = ++replicationAgentCarryStateSnapshotSequence;
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    Time.realtimeSinceStartup,
                    state.Amount > 0 ? "AgentCarryResourceChanged" : "AgentCarryResourceCleared",
                    state.UniqueId,
                    state.BlueprintId,
                    0,
                    0,
                    0,
                    "entityId="
                        + state.EntityId
                        + " amount="
                        + state.Amount.ToString(CultureInfo.InvariantCulture)
                        + " carryAmount="
                        + state.Amount.ToString(CultureInfo.InvariantCulture)
                        + " snapshotId="
                        + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " index="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " count="
                        + states.Count.ToString(CultureInfo.InvariantCulture)
                        + " source=authoritative-carry-snapshot"));
            }

            replicationLastWorldObjectDeltaSummary = "agent-carry-state-snapshot-sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + states.Count.ToString(CultureInfo.InvariantCulture);
            LogReplicationInfo("Going Cooperative replication agent carry state snapshot sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " "
                + collectDetail);
        }

        private void SendHostReplicationAgentCharacterStateSnapshotIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || replicationLastSnapshotEntities <= 0
                || Time.realtimeSinceStartup < replicationNextAgentCharacterStateSnapshotRealtime)
            {
                return;
            }

            replicationNextAgentCharacterStateSnapshotRealtime = Time.realtimeSinceStartup + ReplicationAgentCharacterStateSnapshotSeconds;
            if (!TryCollectReplicationAgentCharacterStates(out var states, out var collectDetail))
            {
                replicationLastWorldObjectDeltaSummary = "agent-character-state-snapshot-skipped " + collectDetail;
                LogReplicationInfo("Going Cooperative replication agent character state snapshot skipped " + collectDetail);
                return;
            }

            var snapshotId = ++replicationAgentCharacterStateSnapshotSequence;
            var sent = 0;
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                lock (ReplicationWorldObjectDeltaLock)
                {
                    if (ReplicationLastAgentCharacterStateSignatureByEntityId.TryGetValue(state.EntityId, out var lastSignature)
                        && lastSignature == state.Signature)
                    {
                        continue;
                    }

                    ReplicationLastAgentCharacterStateSignatureByEntityId[state.EntityId] = state.Signature;
                }

                sent++;
                var needsPayload = replicationConfigNeedsReplication
                    ? " hasHunger=" + FormatReplicationBool(state.HasHunger)
                        + " hungerCurrent=" + state.HungerCurrent.ToString("R", CultureInfo.InvariantCulture)
                        + " hasSleep=" + FormatReplicationBool(state.HasSleep)
                        + " sleepCurrent=" + state.SleepCurrent.ToString("R", CultureInfo.InvariantCulture)
                    : string.Empty;
                SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    Time.realtimeSinceStartup,
                    "AgentCharacterState",
                    state.UniqueId,
                    "character-state",
                    0,
                    0,
                    0,
                    "entityId="
                        + state.EntityId
                        + " snapshotId="
                        + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " index="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " count="
                        + states.Count.ToString(CultureInfo.InvariantCulture)
                        + " dead="
                        + FormatReplicationBool(state.HasDied)
                        + " fainted="
                        + FormatReplicationBool(state.HasFainted)
                        + " sleeping="
                        + FormatReplicationBool(state.IsSleeping)
                        + " wounded="
                        + FormatReplicationBool(state.IsWounded)
                        + " bleeding="
                        + FormatReplicationBool(state.IsBleeding)
                        + " beingCarried="
                        + FormatReplicationBool(state.IsBeingCarried)
                        + " idle="
                        + FormatReplicationBool(state.IsIdle)
                        + " resting="
                        + FormatReplicationBool(state.IsResting)
                        + " drafting="
                        + FormatReplicationBool(state.IsDrafting)
                        + " combatB64="
                        + EncodeReplicationDetailBase64(state.CombatMode)
                        + needsPayload
                        + " statsSig=0x"
                        + state.StatsSignature.ToString("X16", CultureInfo.InvariantCulture)
                        + " attrsSig=0x"
                        + state.AttributesSignature.ToString("X16", CultureInfo.InvariantCulture)
                        + " skillsSig=0x"
                        + state.SkillsSignature.ToString("X16", CultureInfo.InvariantCulture)
                        + " sig=0x"
                        + state.Signature.ToString("X16", CultureInfo.InvariantCulture)
                        + " source=authoritative-character-state"));
            }

            replicationLastWorldObjectDeltaSummary = "agent-character-state-snapshot-sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + sent.ToString(CultureInfo.InvariantCulture);
            if (sent > 0)
            {
                LogReplicationInfo("Going Cooperative replication agent character state snapshot sent snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " sent="
                    + sent.ToString(CultureInfo.InvariantCulture)
                    + " "
                    + collectDetail);
            }
        }

        private void SendHostReplicationBuildingStateSnapshotIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || replicationLastCollectedEntities <= 0
                || Time.realtimeSinceStartup < replicationNextBuildingStateSnapshotRealtime)
            {
                return;
            }

            replicationNextBuildingStateSnapshotRealtime = Time.realtimeSinceStartup + ReplicationBuildingStateSnapshotSeconds;
            if (!TryCollectReplicationBuildingStates(out var states, out var collectDetail))
            {
                replicationLastWorldObjectDeltaSummary = "building-state-snapshot-skipped " + collectDetail;
                LogReplicationInfo("Going Cooperative replication building state snapshot skipped " + collectDetail);
                return;
            }

            SortReplicationBuildingStates(states);
            var signature = ComputeReplicationBuildingStateSnapshotSignature(states);
            if (replicationLastBuildingStateSnapshotSignatureValid
                && signature == replicationLastBuildingStateSnapshotSignature)
            {
                replicationLastWorldObjectDeltaSummary = "building-state-snapshot-skipped-unchanged count="
                    + states.Count.ToString(CultureInfo.InvariantCulture)
                    + " signature=0x"
                    + signature.ToString("x16", CultureInfo.InvariantCulture)
                    + " "
                    + collectDetail;
                return;
            }

            replicationLastBuildingStateSnapshotSignatureValid = true;
            replicationLastBuildingStateSnapshotSignature = signature;
            var snapshotId = ++replicationBuildingStateSnapshotSequence;
            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "BuildingStateSnapshotBegin",
                0L,
                "building-state",
                0,
                0,
                0,
                "snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " count="
                    + states.Count.ToString(CultureInfo.InvariantCulture)
                    + " signature=0x"
                    + signature.ToString("x16", CultureInfo.InvariantCulture)
                    + " "
                    + collectDetail));

            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                    ++replicationWorldObjectDeltaSequence,
                    Time.realtimeSinceStartup,
                    "BuildingState",
                    state.UniqueId,
                    state.BlueprintId,
                    state.GridX,
                    state.GridY,
                    state.GridZ,
                    "snapshotId="
                        + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " index="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " count="
                        + states.Count.ToString(CultureInfo.InvariantCulture)
                        + " phase="
                        + FormatReplicationWorldObjectDetailToken(state.ConstructionPhase)
                        + " remainingTimeMs="
                        + state.RemainingTimeMs.ToString(CultureInfo.InvariantCulture)
                        + " underConstruction="
                        + (state.IsUnderConstruction ? "true" : "false")
                        + " forbidden="
                        + (state.IsForbidden ? "true" : "false")
                        + " markedForDestruction="
                        + (state.MarkedForDestruction ? "true" : "false")
                        + " source=authoritative-building-snapshot"));
            }

            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "BuildingStateSnapshotEnd",
                0L,
                "building-state",
                0,
                0,
                0,
                "snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " count="
                    + states.Count.ToString(CultureInfo.InvariantCulture)
                    + " signature=0x"
                    + signature.ToString("x16", CultureInfo.InvariantCulture)
                    + " "
                    + collectDetail));

            replicationLastWorldObjectDeltaSummary = "building-state-snapshot-sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " signature=0x"
                + signature.ToString("x16", CultureInfo.InvariantCulture)
                + " "
                + collectDetail;
            LogReplicationInfo("Going Cooperative replication building state snapshot sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " "
                + collectDetail);
        }

        private static bool ShouldSkipDuplicateReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            if (string.Equals(delta.DeltaKind, CombatStateDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatOutcomeDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatPresentationDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatHealthDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatDeathDeltaKind, StringComparison.Ordinal))
            {
                // Combat can generate several ordered transitions inside the generic
                // duplicate window; sequence/ack handling provides actual deduplication.
                return false;
            }

            if (string.Equals(delta.DeltaKind, ReplicationAgentWorkPresentationDeltaKind, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(delta.DeltaKind, ReplicationAgentMotionPresentationDeltaKind, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(delta.DeltaKind, "AgentNeedLifecycle", StringComparison.Ordinal))
            {
                // Lifecycle phases may legitimately transition several times inside the
                // generic 500 ms duplicate window (consume -> particles stop -> goal end).
                return false;
            }

            if (string.Equals(delta.DeltaKind, "AgentCarryResourceChanged", StringComparison.Ordinal)
                && delta.Detail.IndexOf("inferred-amount-for=", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            var key = delta.DeltaKind
                + "|"
                + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                + "|"
                + delta.BlueprintId
                + "|"
                + delta.GridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridZ.ToString(CultureInfo.InvariantCulture);
            var now = Time.realtimeSinceStartup;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationWorldObjectDeltaLastSentAt.TryGetValue(key, out var last) && now - last < 0.5f)
                {
                    return true;
                }

                ReplicationWorldObjectDeltaLastSentAt[key] = now;
                return false;
            }
        }

        private void SendPendingReplicationWorldObjectDeltasIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            var due = new List<ReplicationWorldObjectDelta>();
            lock (ReplicationWorldObjectDeltaLock)
            {
                foreach (var pending in replicationPendingWorldObjectDeltas.Values)
                {
                    if (now - pending.LastSentRealtime >= ReplicationWorldObjectDeltaRetrySeconds)
                    {
                        due.Add(pending.Delta);
                    }
                }
            }

            for (var i = 0; i < due.Count; i++)
            {
                if (ShouldDropReplicationWorldObjectDeltaAfterRetries(due[i]))
                {
                    continue;
                }

                TrySendReplicationWorldObjectDelta(due[i], isRetry: true);
            }
        }

        private void TrySendReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta, bool isRetry)
        {
            if (replicationTransport == null)
            {
                return;
            }

            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForWorldObjectDelta(ReplicationHostPeerId, delta));
                lock (ReplicationWorldObjectDeltaLock)
                {
                    if (replicationPendingWorldObjectDeltas.TryGetValue(delta.Sequence, out var pending))
                    {
                        pending.LastSentRealtime = Time.realtimeSinceStartup;
                        pending.SendCount++;
                    }
                }

                if (isRetry)
                {
                    replicationWorldObjectDeltaRetriesSent++;
                    replicationLastWorldObjectDeltaSummary = "retry " + FormatReplicationWorldObjectDelta(delta);
                    if (!IsNoisyReplicationWorldObjectDelta(delta))
                    {
                        LogReplicationInfo("Going Cooperative replication world object delta retry " + FormatReplicationWorldObjectDelta(delta));
                    }
                }
                else
                {
                    replicationWorldObjectDeltasSent++;
                    replicationLastWorldObjectDeltaSummary = "sent " + FormatReplicationWorldObjectDelta(delta);
                    if (!IsNoisyReplicationWorldObjectDelta(delta))
                    {
                        LogReplicationInfo("Going Cooperative replication world object delta sent " + FormatReplicationWorldObjectDelta(delta));
                    }
                }
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication world object delta send failed retry="
                    + (isRetry ? "yes" : "no")
                    + " error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " "
                    + FormatReplicationWorldObjectDelta(delta));
            }
        }

        private bool ShouldDropReplicationWorldObjectDeltaAfterRetries(ReplicationWorldObjectDelta delta)
        {
            PendingReplicationWorldObjectDelta? pending = null;
            lock (ReplicationWorldObjectDeltaLock)
            {
                replicationPendingWorldObjectDeltas.TryGetValue(delta.Sequence, out pending);
                if (pending == null || pending.SendCount < ReplicationWorldObjectDeltaMaxSends)
                {
                    return false;
                }

                replicationPendingWorldObjectDeltas.Remove(delta.Sequence);
            }

            replicationLastWorldObjectDeltaSummary = "drop-retry-limit sequence="
                + delta.Sequence.ToString(CultureInfo.InvariantCulture)
                + " sends="
                + pending.SendCount.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationWorldObjectDelta(delta);
            LogReplicationWarning("Going Cooperative replication world object delta drop retry-limit sequence="
                + delta.Sequence.ToString(CultureInfo.InvariantCulture)
                + " sends="
                + pending.SendCount.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationWorldObjectDelta(delta));
            return true;
        }

        private void HandleReplicationWorldObjectDelta(TransportEnvelope envelope)
        {
            if (!ReplicationPayloadCodec.TryReadWorldObjectDelta(envelope, out var delta, out var error) || delta == null)
            {
                LogReplicationWarning("Going Cooperative replication world object delta decode failed error=" + error);
                return;
            }

            replicationWorldObjectDeltasReceived++;
            if (replicationConfigHostMode)
            {
                replicationLastWorldObjectDeltaSummary = "ignored-on-host " + FormatReplicationWorldObjectDelta(delta);
                return;
            }

            if (IsReplicationWorldObjectDeltaModeOff())
            {
                replicationLastWorldObjectDeltaSummary = "ignored-mode-off " + FormatReplicationWorldObjectDelta(delta);
                SendReplicationWorldObjectDeltaAck(delta, applied: true, duplicate: false, detail: "ignored-mode-off");
                return;
            }

            if (IsReplicationWorldObjectDeltaModeShadow())
            {
                replicationLastWorldObjectDeltaSummary = "shadow " + FormatReplicationWorldObjectDelta(delta);
                lock (ReplicationWorldObjectDeltaLock)
                {
                    replicationClientAppliedWorldObjectDeltaSequences.Add(delta.Sequence);
                }

                SendReplicationWorldObjectDeltaAck(delta, applied: true, duplicate: false, detail: "shadow");
                LogReplicationInfo("Going Cooperative replication world object delta shadow " + FormatReplicationWorldObjectDelta(delta));
                return;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (replicationClientAppliedWorldObjectDeltaSequences.Contains(delta.Sequence))
                {
                    var duplicateDetail = "duplicate-sequence-skipped";
                    replicationLastWorldObjectDeltaSummary = "duplicate detail="
                        + duplicateDetail
                        + " "
                        + FormatReplicationWorldObjectDelta(delta);
                    SendReplicationWorldObjectDeltaAck(delta, applied: false, duplicate: true, detail: duplicateDetail);
                    if (!IsNoisyReplicationWorldObjectDelta(delta))
                    {
                        LogReplicationInfo("Going Cooperative replication world object delta duplicate detail="
                            + duplicateDetail
                            + " "
                            + FormatReplicationWorldObjectDelta(delta));
                    }
                    return;
                }
            }

            if (replicationConfigWorldObjectDeltaApplyBudgetPerFrame <= 0)
            {
                ApplyReplicationWorldObjectDeltaAndAck(delta);
                return;
            }

            var queued = false;
            var overflow = false;
            var queueCount = 0;
            PendingReplicationClientWorldObjectDeltaApply? replacedPending = null;
            var coalesceKey = FormatReplicationWorldObjectDeltaCoalesceKey(delta);
            var priority = IsReplicationPriorityWorldObjectDelta(delta);
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationClientQueuedWorldObjectDeltaSequences.Contains(delta.Sequence))
                {
                    var duplicateDetail = "duplicate-sequence-queued";
                    replicationLastWorldObjectDeltaSummary = "duplicate detail="
                        + duplicateDetail
                        + " "
                        + FormatReplicationWorldObjectDelta(delta);
                    SendReplicationWorldObjectDeltaAck(delta, applied: false, duplicate: true, detail: duplicateDetail);
                    if (!IsNoisyReplicationWorldObjectDelta(delta))
                    {
                        LogReplicationInfo("Going Cooperative replication world object delta duplicate detail="
                            + duplicateDetail
                            + " "
                            + FormatReplicationWorldObjectDelta(delta));
                    }

                    return;
                }

                if (ReplicationClientPriorityWorldObjectDeltaApplies.Count
                    + ReplicationClientPendingWorldObjectDeltaApplies.Count
                    >= replicationConfigWorldObjectDeltaApplyQueueMax)
                {
                    overflow = true;
                }
                else
                {
                    if (!string.IsNullOrEmpty(coalesceKey)
                        && ReplicationClientCoalescableWorldObjectDeltaApplies.TryGetValue(coalesceKey, out replacedPending))
                    {
                        replacedPending.IsStale = true;
                        ReplicationClientQueuedWorldObjectDeltaSequences.Remove(replacedPending.Delta.Sequence);
                        replicationClientAppliedWorldObjectDeltaSequences.Add(replacedPending.Delta.Sequence);
                    }

                    var pending = new PendingReplicationClientWorldObjectDeltaApply(delta, coalesceKey);
                    if (priority)
                    {
                        ReplicationClientPriorityWorldObjectDeltaApplies.Enqueue(pending);
                    }
                    else
                    {
                        ReplicationClientPendingWorldObjectDeltaApplies.Enqueue(pending);
                    }
                    ReplicationClientQueuedWorldObjectDeltaSequences.Add(delta.Sequence);
                    if (!string.IsNullOrEmpty(coalesceKey))
                    {
                        ReplicationClientCoalescableWorldObjectDeltaApplies[coalesceKey] = pending;
                    }

                    queueCount = ReplicationClientPriorityWorldObjectDeltaApplies.Count
                        + ReplicationClientPendingWorldObjectDeltaApplies.Count;
                    queued = true;
                }
            }

            if (replacedPending != null)
            {
                replicationWorldObjectDeltasCoalesced++;
                SendReplicationWorldObjectDeltaAck(
                    replacedPending.Delta,
                    applied: true,
                    duplicate: false,
                    detail: "coalesced-replaced key=" + replacedPending.CoalesceKey);
            }

            if (queued)
            {
                replicationLastWorldObjectDeltaSummary = "queued queue="
                    + queueCount.ToString(CultureInfo.InvariantCulture)
                    + (priority ? " priority=yes" : string.Empty)
                    + (string.IsNullOrEmpty(coalesceKey) ? string.Empty : " coalesceKey=" + coalesceKey)
                    + " "
                    + FormatReplicationWorldObjectDelta(delta);
                if (!IsNoisyReplicationWorldObjectDelta(delta))
                {
                    LogReplicationInfo("Going Cooperative replication world object delta queued queue="
                        + queueCount.ToString(CultureInfo.InvariantCulture)
                        + (priority ? " priority=yes" : string.Empty)
                        + (string.IsNullOrEmpty(coalesceKey) ? string.Empty : " coalesceKey=" + coalesceKey)
                        + " "
                        + FormatReplicationWorldObjectDelta(delta));
                }

                return;
            }

            if (overflow)
            {
                var detail = "client-apply-queue-full count="
                    + replicationConfigWorldObjectDeltaApplyQueueMax.ToString(CultureInfo.InvariantCulture);
                replicationLastWorldObjectDeltaSummary = "queue-full detail="
                    + detail
                    + " "
                    + FormatReplicationWorldObjectDelta(delta);
                SendReplicationWorldObjectDeltaAck(delta, applied: false, duplicate: false, detail);
                LogReplicationWarning("Going Cooperative replication world object delta queue full detail="
                    + detail
                    + " "
                    + FormatReplicationWorldObjectDelta(delta));
            }
        }

        private void ProcessPendingReplicationWorldObjectDeltaApplies()
        {
            if (replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || IsReplicationWorldObjectDeltaModeShadow())
            {
                return;
            }

            var budget = replicationConfigWorldObjectDeltaApplyBudgetPerFrame;
            if (budget <= 0)
            {
                return;
            }

            var startedTimestamp = Stopwatch.GetTimestamp();
            var budgetTicks = replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame <= 0f
                ? 0L
                : (long)Math.Ceiling(replicationConfigWorldObjectDeltaApplyBudgetMsPerFrame * Stopwatch.Frequency / 1000.0);
            var appliedBudgetCount = 0;
            while (appliedBudgetCount < budget)
            {
                if (appliedBudgetCount > 0 && budgetTicks > 0L && Stopwatch.GetTimestamp() - startedTimestamp >= budgetTicks)
                {
                    replicationWorldObjectDeltaApplyBudgetStops++;
                    return;
                }

                PendingReplicationClientWorldObjectDeltaApply? pending = null;
                lock (ReplicationWorldObjectDeltaLock)
                {
                    if (ReplicationClientPriorityWorldObjectDeltaApplies.Count == 0
                        && ReplicationClientPendingWorldObjectDeltaApplies.Count == 0)
                    {
                        return;
                    }

                    pending = ReplicationClientPriorityWorldObjectDeltaApplies.Count > 0
                        ? ReplicationClientPriorityWorldObjectDeltaApplies.Dequeue()
                        : ReplicationClientPendingWorldObjectDeltaApplies.Dequeue();
                    ReplicationClientQueuedWorldObjectDeltaSequences.Remove(pending.Delta.Sequence);
                    if (!string.IsNullOrEmpty(pending.CoalesceKey)
                        && ReplicationClientCoalescableWorldObjectDeltaApplies.TryGetValue(pending.CoalesceKey, out var current)
                        && ReferenceEquals(current, pending))
                    {
                        ReplicationClientCoalescableWorldObjectDeltaApplies.Remove(pending.CoalesceKey);
                    }
                }

                if (pending.IsStale)
                {
                    continue;
                }

                appliedBudgetCount++;
                ApplyReplicationWorldObjectDeltaAndAck(pending.Delta);
                if (budgetTicks > 0L && Stopwatch.GetTimestamp() - startedTimestamp >= budgetTicks)
                {
                    replicationWorldObjectDeltaApplyBudgetStops++;
                    return;
                }
            }
        }

        private void ApplyReplicationWorldObjectDeltaAndAck(ReplicationWorldObjectDelta delta)
        {
            var applied = TryApplyReplicationWorldObjectDelta(delta, out var detail);
            if (applied)
            {
                replicationWorldObjectDeltasApplied++;
                lock (ReplicationWorldObjectDeltaLock)
                {
                    replicationClientAppliedWorldObjectDeltaSequences.Add(delta.Sequence);
                }
            }

            SendReplicationWorldObjectDeltaAck(delta, applied, duplicate: false, detail);

            replicationLastWorldObjectDeltaSummary = "applied invoked="
                + (applied ? "yes" : "no")
                + " detail="
                + detail
                + " "
                + FormatReplicationWorldObjectDelta(delta);
            if (!IsReplicationResourcePileStateSnapshotDelta(delta)
                && !IsNoisyReplicationWorldObjectDelta(delta))
            {
                LogReplicationInfo("Going Cooperative replication world object delta applied invoked="
                    + (applied ? "yes" : "no")
                    + " detail="
                    + detail
                    + " "
                    + FormatReplicationWorldObjectDelta(delta));
            }
        }

        private static int GetPendingReplicationWorldObjectDeltaApplyCount()
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                return ReplicationClientPriorityWorldObjectDeltaApplies.Count
                    + ReplicationClientPendingWorldObjectDeltaApplies.Count;
            }
        }

        private static bool IsReplicationPriorityWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            // Presentation is latency-sensitive and cheap to apply. Keep its ordered
            // lifecycle from waiting behind expensive world lookups while retaining
            // the same global queue bound and per-frame/time budgets.
            return string.Equals(delta.DeltaKind, CombatPresentationDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, ReplicationAgentWorkPresentationDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, ReplicationAgentMotionPresentationDeltaKind, StringComparison.Ordinal);
        }

        private static int GetCoalescableReplicationWorldObjectDeltaApplyCount()
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                return ReplicationClientCoalescableWorldObjectDeltaApplies.Count;
            }
        }

        private void SendReplicationWorldObjectDeltaAck(ReplicationWorldObjectDelta delta, bool applied, bool duplicate, string detail)
        {
            if (replicationConfigHostMode || replicationTransport == null)
            {
                return;
            }

            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForWorldObjectDeltaAck(
                    ReplicationClientPeerId,
                    new ReplicationWorldObjectDeltaAck(delta.Sequence, applied, duplicate, detail)));
                replicationWorldObjectDeltaAcksSent++;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication world object delta ack send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " sequence="
                    + delta.Sequence.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void HandleReplicationWorldObjectDeltaAck(TransportEnvelope envelope)
        {
            if (!ReplicationPayloadCodec.TryReadWorldObjectDeltaAck(envelope, out var ack, out var error) || ack == null)
            {
                LogReplicationWarning("Going Cooperative replication world object delta ack decode failed error=" + error);
                return;
            }

            replicationWorldObjectDeltaAcksReceived++;
            if (!replicationConfigHostMode)
            {
                replicationLastWorldObjectDeltaSummary = "ack-ignored-nonhost sequence=" + ack.Sequence.ToString(CultureInfo.InvariantCulture);
                return;
            }

            var removed = false;
            if (ack.Applied || IsTerminalReplicationWorldObjectDeltaAck(ack.Detail))
            {
                lock (ReplicationWorldObjectDeltaLock)
                {
                    removed = replicationPendingWorldObjectDeltas.Remove(ack.Sequence);
                }
            }

            replicationLastWorldObjectDeltaSummary = "ack sequence="
                + ack.Sequence.ToString(CultureInfo.InvariantCulture)
                + " applied="
                + (ack.Applied ? "yes" : "no")
                + " duplicate="
                + (ack.Duplicate ? "yes" : "no")
                + " removed="
                + (removed ? "yes" : "no")
                + " detail="
                + ack.Detail;
            if (!IsReplicationResourcePileStateSnapshotAckDetail(ack.Detail))
            {
                if (!ack.Duplicate && !IsNoisyReplicationWorldObjectDeltaAckDetail(ack.Detail))
                {
                    LogReplicationInfo("Going Cooperative replication world object delta ack received sequence="
                        + ack.Sequence.ToString(CultureInfo.InvariantCulture)
                        + " applied="
                        + (ack.Applied ? "yes" : "no")
                        + " duplicate="
                        + (ack.Duplicate ? "yes" : "no")
                        + " removed="
                        + (removed ? "yes" : "no")
                        + " detail="
                        + ack.Detail);
                }
            }
        }

        private static bool IsTerminalReplicationWorldObjectDeltaAck(string detail)
        {
            return detail.IndexOf("plant-missing", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("unique-id-mismatch", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("unsupported-delta-kind", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("already-applied-or-no-ground", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsReplicationResourcePileStateSnapshotDelta(ReplicationWorldObjectDelta delta)
        {
            return string.Equals(delta.DeltaKind, "ResourcePileStateSnapshotBegin", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "ResourcePileState", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "ResourcePileStateSnapshotEnd", StringComparison.Ordinal);
        }

        private static bool IsTransientReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            return string.Equals(delta.DeltaKind, CombatOutcomeDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentActionHeartbeat", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentProgressUpdated", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationTriggered", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationReset", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationQuit", StringComparison.Ordinal);
        }

        private static bool IsNoisyReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            return IsTransientReplicationWorldObjectDelta(delta)
                || string.Equals(delta.DeltaKind, "AgentProgressCleared", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentActionStatus", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "GameTimeSnapshot", StringComparison.Ordinal)
                || IsReplicationResourcePileStateSnapshotDelta(delta);
        }

        private static bool IsNoisyReplicationWorldObjectDeltaAckDetail(string detail)
        {
            return detail.IndexOf("agent-action-status", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("agent-progress", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("agent-animation", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("duplicate-sequence-skipped", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsReplicationResourcePileStateSnapshotAckDetail(string detail)
        {
            return detail.IndexOf("pile-state-snapshot", StringComparison.OrdinalIgnoreCase) >= 0
                || detail.IndexOf("pile-state-authoritative", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsReplicationClientReadyForResourcePileStateSnapshotApply()
        {
            if (replicationConfigHostMode)
            {
                return true;
            }

            if (!replicationConfigApplySnapshots
                || replicationSnapshotsApplied <= 0
                || replicationLastSnapshotEntities <= 0)
            {
                replicationClientResourcePileStateSnapshotApplyReadyRealtime = 0f;
                return false;
            }

            if (replicationClientResourcePileStateSnapshotApplyReadyRealtime <= 0f)
            {
                replicationClientResourcePileStateSnapshotApplyReadyRealtime =
                    Time.realtimeSinceStartup + ReplicationResourcePileStateSnapshotClientApplyDelaySeconds;
                return false;
            }

            return Time.realtimeSinceStartup >= replicationClientResourcePileStateSnapshotApplyReadyRealtime;
        }

        private static bool TryApplyReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (string.Equals(delta.DeltaKind, CombatStateDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatOutcomeDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatPresentationDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatHealthDeltaKind, StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, CombatDeathDeltaKind, StringComparison.Ordinal))
            {
                return TryApplyReplicationCombatWorldDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, ManagementDeltaKind, StringComparison.Ordinal))
            {
                return TryApplyReplicationManagementDelta(delta, out detail);
            }

            if (!replicationConfigHostMode
                && IsReplicationResourcePileStateSnapshotDelta(delta))
            {
                return TryObserveReplicationResourcePileStateSnapshotDelta(delta, out detail);
            }

            if (IsReplicationResourcePileStateSnapshotDelta(delta)
                && !IsReplicationClientReadyForResourcePileStateSnapshotApply())
            {
                detail = "pile-state-snapshot-ignored-client-not-ready snapshotsApplied="
                    + replicationSnapshotsApplied.ToString(CultureInfo.InvariantCulture)
                    + " lastSnapshotEntities="
                    + replicationLastSnapshotEntities.ToString(CultureInfo.InvariantCulture)
                    + " readyIn="
                    + Math.Max(0f, replicationClientResourcePileStateSnapshotApplyReadyRealtime - Time.realtimeSinceStartup)
                        .ToString("0.###", CultureInfo.InvariantCulture);
                return true;
            }

            if (string.Equals(delta.DeltaKind, "MapResourceDisposed", StringComparison.Ordinal))
            {
                return TryApplyReplicationMapResourceDisposed(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileSpawned", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileSpawned(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileAmountAdded", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileAmountAdded(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileStateSnapshotBegin", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileStateSnapshotBegin(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileState", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileState(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileStateSnapshotEnd", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileStateSnapshotEnd(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileDisposed", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileDisposed(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "TerrainGroundDestroyed", StringComparison.Ordinal))
            {
                return TryApplyReplicationTerrainGroundDestroyed(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "BuildingBlueprintPlaced", StringComparison.Ordinal))
            {
                return TryApplyReplicationBuildingBlueprintPlaced(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "BuildingBlueprintRejected", StringComparison.Ordinal))
            {
                return TryApplyReplicationBuildingBlueprintRejected(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "BuildingStateSnapshotBegin", StringComparison.Ordinal))
            {
                return TryApplyReplicationBuildingStateSnapshotBegin(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "BuildingState", StringComparison.Ordinal))
            {
                return TryApplyReplicationBuildingState(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "BuildingStateSnapshotEnd", StringComparison.Ordinal))
            {
                return TryApplyReplicationBuildingStateSnapshotEnd(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentCarryEquipped", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentCarryCleared", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentCarryDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentCarryResourceChanged", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentCarryResourceCleared", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentCarryResourceDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, ReplicationAgentWorkPresentationDeltaKind, StringComparison.Ordinal))
            {
                return TryApplyReplicationSemanticWorkDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, ReplicationAgentMotionPresentationDeltaKind, StringComparison.Ordinal))
            {
                return TryApplyReplicationSemanticAgentMotionDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentAnimationTriggered", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationReset", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationQuit", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentAnimationDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentAnimationParameter", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentAnimationParameterDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentProgressUpdated", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentProgressCleared", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentProgressDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentActionStatus", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentActionHeartbeat", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentActionStatusDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentActionPhase", StringComparison.Ordinal))
            {
                return TryApplyReplicationGoapActionPhaseDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentSkillExperience", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentSkillExperienceDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentCharacterState", StringComparison.Ordinal))
            {
                return TryApplyReplicationAgentCharacterStateDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "AgentNeedLifecycle", StringComparison.Ordinal))
            {
                return TryApplyReplicationNeedsLifecycleDelta(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "GameTimeSnapshot", StringComparison.Ordinal))
            {
                return TryApplyReplicationGameTimeSnapshot(delta, out detail);
            }

            detail = "unsupported-delta-kind=" + delta.DeltaKind;
            return false;
        }

        private static bool TryObserveReplicationResourcePileStateSnapshotDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (string.Equals(delta.DeltaKind, "ResourcePileStateSnapshotBegin", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileStateSnapshotBegin(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileState", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileState(delta, out detail);
            }

            if (string.Equals(delta.DeltaKind, "ResourcePileStateSnapshotEnd", StringComparison.Ordinal))
            {
                return TryApplyReplicationResourcePileStateSnapshotEnd(delta, out detail);
            }

            detail = "pile-state-snapshot-observe-unsupported kind=" + delta.DeltaKind;
            return false;
        }

        private static bool TryApplyReplicationBuildingBlueprintPlaced(ReplicationWorldObjectDelta delta, out string detail)
        {
            var spawnKey = FormatReplicationWorldObjectDeltaSpawnKey(delta);
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationWorldObjectDeltaAppliedSpawnKeys.Contains(spawnKey))
                {
                    detail = "duplicate-building-blueprint-delta-skipped key=" + spawnKey;
                    return true;
                }
            }

            var angleY = TryReadReplicationWorldObjectDetailInt(delta.Detail, "angleY", out var parsedAngleY)
                ? parsedAngleY
                : 0;
            var factionOwnership = TryReadReplicationWorldObjectDetailToken(delta.Detail, "faction", out var parsedFactionOwnership)
                ? parsedFactionOwnership
                : "Player";
            var buildingTypeDetail = TryReadReplicationWorldObjectDetailToken(delta.Detail, "buildingType", out var buildingType)
                ? " buildingType=" + buildingType
                : string.Empty;

            if (TryFindExistingReplicationBuildingBlueprint(delta.BlueprintId, delta.GridX, delta.GridY, delta.GridZ, out var existingDetail))
            {
                if (TryFindReplicationBuildingBlueprintCandidate(delta, out var existingCandidate, out var existingIdentityDetail) && existingCandidate != null)
                {
                    existingDetail += " identity=" + existingIdentityDetail;
                }

                MarkReplicationWorldObjectDeltaSpawnApplied(delta, spawnKey);
                detail = "ok already-present " + existingDetail + buildingTypeDetail;
                return true;
            }

            applyingRuntimeCommandDepth++;
            bool applied;
            string applyDetail;
            try
            {
                applied = TryApplyReplicationBuildPlacement(
                    delta.BlueprintId,
                    delta.GridX,
                    delta.GridY,
                    delta.GridZ,
                    angleY,
                    factionOwnership,
                    out applyDetail);
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }

            if (!applied)
            {
                if (IsReplicationClientOriginBuildingDelta(delta))
                {
                    MarkReplicationWorldObjectDeltaSpawnApplied(delta, spawnKey);
                    detail = "ok client-origin-building-placement-deferred-to-state " + applyDetail + buildingTypeDetail;
                    return true;
                }

                detail = "safe-build-placement-failed " + applyDetail + buildingTypeDetail;
                return false;
            }

            MarkReplicationWorldObjectDeltaSpawnApplied(delta, spawnKey);
            if (TryFindReplicationBuildingBlueprintCandidate(delta, out var placedCandidate, out var placedIdentityDetail) && placedCandidate != null)
            {
                applyDetail += " identity=" + placedIdentityDetail;
            }

            detail = applyDetail + buildingTypeDetail;
            return true;
        }

        private static bool TryApplyReplicationBuildingBlueprintRejected(ReplicationWorldObjectDelta delta, out string detail)
        {
            var buildingTypeDetail = TryReadReplicationWorldObjectDetailToken(delta.Detail, "buildingType", out var buildingType)
                ? " buildingType=" + buildingType
                : string.Empty;
            var resultDetail = TryReadReplicationWorldObjectDetailToken(delta.Detail, "result", out var result)
                ? " hostResult=" + result
                : string.Empty;

            if (!TryFindReplicationBuildingBlueprintCandidate(
                delta,
                out var candidate,
                out var lookupDetail)
                || candidate == null)
            {
                detail = "ok rejected-building-not-present " + lookupDetail + buildingTypeDetail + resultDetail;
                return true;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                if (TryRemoveReplicationBuildingCandidate(candidate, delta, out var removeDetail))
                {
                    RemoveReplicationHostIdentity(delta.UniqueId, candidate, "building-rejected-remove");
                    detail = "ok rejected-building-removed " + removeDetail + " " + lookupDetail + buildingTypeDetail + resultDetail;
                    return true;
                }

                detail = "rejected-building-remove-failed " + removeDetail + " " + lookupDetail + buildingTypeDetail + resultDetail;
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool IsReplicationClientOriginBuildingDelta(ReplicationWorldObjectDelta delta)
        {
            return TryReadReplicationWorldObjectDetailToken(delta.Detail, "player", out var player)
                && string.Equals(player, ReplicationClientPeerId, StringComparison.Ordinal);
        }

        private static bool TryApplyReplicationBuildingStateSnapshotBegin(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "snapshotId", out var snapshotIdValue) || snapshotIdValue <= 0)
            {
                detail = "building-state-snapshot-begin-missing-id";
                return false;
            }

            var expectedCount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var parsedCount)
                ? Math.Max(0, parsedCount)
                : 0;
            var scanned = TryReadReplicationWorldObjectDetailInt(delta.Detail, "scanned", out var parsedScanned)
                ? Math.Max(0, parsedScanned)
                : -1;
            var sent = TryReadReplicationWorldObjectDetailInt(delta.Detail, "sent", out var parsedSent)
                ? Math.Max(0, parsedSent)
                : -1;
            var skipped = TryReadReplicationWorldObjectDetailInt(delta.Detail, "skipped", out var parsedSkipped)
                ? Math.Max(0, parsedSkipped)
                : -1;
            var cap = TryReadReplicationWorldObjectDetailInt(delta.Detail, "cap", out var parsedCap)
                ? Math.Max(0, parsedCap)
                : -1;
            var hasHostSignature = TryReadReplicationWorldObjectDetailHex(delta.Detail, "signature", out var hostSignature);
            lock (ReplicationWorldObjectDeltaLock)
            {
                var context = new ReplicationBuildingStateSnapshotContext(
                    expectedCount,
                    scanned,
                    sent,
                    skipped,
                    cap);
                if (hasHostSignature)
                {
                    context.HostSignatureValid = true;
                    context.HostSignature = hostSignature;
                }

                ReplicationBuildingStateSnapshotContexts[snapshotIdValue] = context;
                CleanupReplicationBuildingStateSnapshotContexts(snapshotIdValue);
            }

            detail = "ok building-state-snapshot-begin snapshotId="
                + snapshotIdValue.ToString(CultureInfo.InvariantCulture)
                + " count="
                + expectedCount.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationBuildingSnapshotCollectDetail(scanned, sent, skipped, cap);
            return true;
        }

        private static bool TryApplyReplicationBuildingState(ReplicationWorldObjectDelta delta, out string detail)
        {
            RecordReplicationBuildingStateSnapshotKey(delta);
            var phase = TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out var parsedPhase)
                ? parsedPhase
                : string.Empty;
            var remainingTimeMs = TryReadReplicationWorldObjectDetailInt(delta.Detail, "remainingTimeMs", out var parsedRemainingTimeMs)
                ? Math.Max(0, parsedRemainingTimeMs)
                : 0;
            var forbidden = TryReadReplicationWorldObjectDetailBool(delta.Detail, "forbidden", out var parsedForbidden)
                && parsedForbidden;
            var markedForDestruction = TryReadReplicationWorldObjectDetailBool(delta.Detail, "markedForDestruction", out var parsedMarked)
                && parsedMarked;

            if (!TryFindReplicationBuildingBlueprintCandidate(
                delta,
                out var candidate,
                out var lookupDetail)
                || candidate == null)
            {
                applyingRuntimeCommandDepth++;
                try
                {
                    if (!TryApplyReplicationBuildPlacement(
                        delta.BlueprintId,
                        delta.GridX,
                        delta.GridY,
                        delta.GridZ,
                        0,
                        "Player",
                        out var seedDetail))
                    {
                        detail = "ok building-state-seed-deferred " + seedDetail + " " + lookupDetail;
                        return true;
                    }
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }

                if (!TryFindReplicationBuildingBlueprintCandidate(
                    delta,
                    out candidate,
                    out lookupDetail)
                    || candidate == null)
                {
                    detail = "ok building-state-seed-lookup-deferred " + lookupDetail;
                    return true;
                }
            }

            if (!TryResolveReplicationBuildingCandidateInstance(candidate, out var buildingInstance, out var instanceDetail)
                || buildingInstance == null)
            {
                detail = "building-state-instance-missing " + instanceDetail + " " + lookupDetail;
                return false;
            }

            var phaseAlreadyApplied = !string.IsNullOrWhiteSpace(phase)
                && TryReadReplicationBuildingPhase(buildingInstance, out var localPhase)
                && string.Equals(localPhase, phase, StringComparison.OrdinalIgnoreCase);
            var changed = 0;
            var attempts = new StringBuilder(256);
            applyingRuntimeCommandDepth++;
            try
            {
                if (!string.IsNullOrWhiteSpace(phase)
                    && TryApplyReplicationBuildingConstructionPhase(buildingInstance, phase, out var phaseDetail))
                {
                    AppendReplicationAttemptDetail(attempts, null, phaseDetail);
                    changed++;
                }
                else if (!string.IsNullOrWhiteSpace(phase))
                {
                    AppendReplicationAttemptDetail(attempts, null, "phase-apply-failed phase=" + phase);
                }

                // Safe replicated placement does not always retain vanilla event wiring. Notify the
                // matching view once when the host moves a blueprint into a new visible phase.
                if (!phaseAlreadyApplied
                    && !string.IsNullOrWhiteSpace(phase)
                    && TryApplyReplicationBuildingViewPhase(candidate, phase, out var viewPhaseDetail))
                {
                    AppendReplicationAttemptDetail(attempts, null, viewPhaseDetail);
                    changed++;
                }
                else if (!phaseAlreadyApplied && !string.IsNullOrWhiteSpace(phase))
                {
                    AppendReplicationAttemptDetail(attempts, null, "view-phase-apply-failed phase=" + phase);
                }

                if (TryApplyReplicationBuildingRemainingTime(buildingInstance, remainingTimeMs, out var timeDetail))
                {
                    AppendReplicationAttemptDetail(attempts, null, timeDetail);
                    changed++;
                }
                else
                {
                    AppendReplicationAttemptDetail(attempts, null, "remaining-time-apply-failed ms=" + remainingTimeMs.ToString(CultureInfo.InvariantCulture));
                }

                if (TrySetInstanceMemberValue(buildingInstance, "IsForbidden", forbidden)
                    || TrySetInstanceMemberValue(buildingInstance, "isForbidden", forbidden))
                {
                    changed++;
                }

                if (TrySetInstanceMemberValue(buildingInstance, "MarkedForDestruction", markedForDestruction)
                    || TrySetInstanceMemberValue(buildingInstance, "markedForDestruction", markedForDestruction))
                {
                    changed++;
                }

                TryRefreshReplicationBuilding(candidate, buildingInstance, out var refreshDetail);
                detail = "ok building-state-applied changed="
                    + changed.ToString(CultureInfo.InvariantCulture)
                    + " phase="
                    + phase
                    + " remainingTimeMs="
                    + remainingTimeMs.ToString(CultureInfo.InvariantCulture)
                    + " forbidden="
                    + forbidden
                    + " markedForDestruction="
                    + markedForDestruction
                    + " "
                    + instanceDetail
                    + " "
                    + lookupDetail
                    + " attempts="
                    + attempts
                    + " "
                    + refreshDetail;
                return true;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool TryApplyReplicationBuildingStateSnapshotEnd(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "snapshotId", out var snapshotIdValue) || snapshotIdValue <= 0)
            {
                detail = "building-state-snapshot-end-missing-id";
                return false;
            }

            ReplicationBuildingStateSnapshotContext? context;
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationBuildingStateSnapshotContexts.TryGetValue(snapshotIdValue, out context);
            }

            if (context == null)
            {
                detail = "ok building-state-snapshot-end-missing-context snapshotId="
                    + snapshotIdValue.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            context.EndReceived = true;
            if (TryReadReplicationWorldObjectDetailHex(delta.Detail, "signature", out var hostSignature))
            {
                context.HostSignatureValid = true;
                context.HostSignature = hostSignature;
            }

            var states = new List<ReplicationBuildingState>(context.States.Values);
            SortReplicationBuildingStates(states);
            var localSignature = ComputeReplicationBuildingStateSnapshotSignature(states);
            RecordReplicationDriftSignature(
                "buildings",
                snapshotIdValue,
                context.ExpectedCount,
                context.SeenKeys.Count,
                context.HostSignatureValid,
                context.HostSignature,
                localSignature,
                "snapshot-end");

            var removed = RemoveReplicationBuildingsMissingFromSnapshot(context, out var removeDetail);
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationBuildingStateSnapshotContexts.Remove(snapshotIdValue);
                CleanupReplicationBuildingStateSnapshotContexts(snapshotIdValue);
            }

            detail = "ok building-state-snapshot-end snapshotId="
                + snapshotIdValue.ToString(CultureInfo.InvariantCulture)
                + " expected="
                + context.ExpectedCount.ToString(CultureInfo.InvariantCulture)
                + " seen="
                + context.SeenKeys.Count.ToString(CultureInfo.InvariantCulture)
                + " removed="
                + removed.ToString(CultureInfo.InvariantCulture)
                + " "
                + "hostSignature="
                + (context.HostSignatureValid ? "0x" + context.HostSignature.ToString("x16", CultureInfo.InvariantCulture) : "<missing>")
                + " localSignature=0x"
                + localSignature.ToString("x16", CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationBuildingSnapshotCollectDetail(context.Scanned, context.Sent, context.Skipped, context.Cap)
                + " "
                + removeDetail;
            return true;
        }

        private static void RecordReplicationBuildingStateSnapshotKey(ReplicationWorldObjectDelta delta)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "snapshotId", out var snapshotIdValue) || snapshotIdValue <= 0)
            {
                return;
            }

            var expectedCount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var parsedCount)
                ? Math.Max(0, parsedCount)
                : 0;
            var key = FormatReplicationWorldObjectDeltaLocationKey(delta);
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationBuildingStateSnapshotContexts.TryGetValue(snapshotIdValue, out var context))
                {
                    context = new ReplicationBuildingStateSnapshotContext(expectedCount, -1, -1, -1, -1);
                    ReplicationBuildingStateSnapshotContexts[snapshotIdValue] = context;
                }

                if (expectedCount > 0)
                {
                    context.ExpectedCount = expectedCount;
                }

                context.SeenKeys.Add(key);
                if (TryReadReplicationWorldObjectDetailHex(delta.Detail, "signature", out var hostSignature))
                {
                    context.HostSignatureValid = true;
                    context.HostSignature = hostSignature;
                }

                var phase = TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out var parsedPhase)
                    ? parsedPhase
                    : string.Empty;
                var remainingTimeMs = TryReadReplicationWorldObjectDetailInt(delta.Detail, "remainingTimeMs", out var parsedRemainingTimeMs)
                    ? Math.Max(0, parsedRemainingTimeMs)
                    : 0;
                var underConstruction = TryReadReplicationWorldObjectDetailBool(delta.Detail, "underConstruction", out var parsedUnderConstruction)
                    && parsedUnderConstruction;
                var forbidden = TryReadReplicationWorldObjectDetailBool(delta.Detail, "forbidden", out var parsedForbidden)
                    && parsedForbidden;
                var markedForDestruction = TryReadReplicationWorldObjectDetailBool(delta.Detail, "markedForDestruction", out var parsedMarked)
                    && parsedMarked;
                context.States[key] = new ReplicationBuildingState(
                    delta.UniqueId,
                    delta.BlueprintId,
                    delta.GridX,
                    delta.GridY,
                    delta.GridZ,
                    phase,
                    remainingTimeMs,
                    underConstruction,
                    forbidden,
                    markedForDestruction);
                CleanupReplicationBuildingStateSnapshotContexts(snapshotIdValue);
            }
        }

        private static int RemoveReplicationBuildingsMissingFromSnapshot(ReplicationBuildingStateSnapshotContext context, out string detail)
        {
            detail = "remove-scan-disabled incomplete-building-snapshots expected="
                + context.ExpectedCount.ToString(CultureInfo.InvariantCulture)
                + " seen="
                + context.SeenKeys.Count.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationBuildingSnapshotCollectDetail(context.Scanned, context.Sent, context.Skipped, context.Cap);
            return 0;
        }

        private static string FormatReplicationBuildingSnapshotCollectDetail(int scanned, int sent, int skipped, int cap)
        {
            return "hostCollect=scanned:"
                + (scanned >= 0 ? scanned.ToString(CultureInfo.InvariantCulture) : "unknown")
                + ",sent:"
                + (sent >= 0 ? sent.ToString(CultureInfo.InvariantCulture) : "unknown")
                + ",skipped:"
                + (skipped >= 0 ? skipped.ToString(CultureInfo.InvariantCulture) : "unknown")
                + ",cap:"
                + (cap >= 0 ? cap.ToString(CultureInfo.InvariantCulture) : "unknown");
        }

        private static void CleanupReplicationBuildingStateSnapshotContexts(long keepSnapshotId)
        {
            if (ReplicationBuildingStateSnapshotContexts.Count <= ReplicationBuildingStateSnapshotContextMaxCount)
            {
                return;
            }

            var expired = new List<long>();
            foreach (var pair in ReplicationBuildingStateSnapshotContexts)
            {
                if (pair.Key != keepSnapshotId)
                {
                    expired.Add(pair.Key);
                }
            }

            expired.Sort();
            while (ReplicationBuildingStateSnapshotContexts.Count > ReplicationBuildingStateSnapshotContextMaxCount && expired.Count > 0)
            {
                var removeId = expired[0];
                expired.RemoveAt(0);
                ReplicationBuildingStateSnapshotContexts.Remove(removeId);
            }
        }

        private static void MarkReplicationWorldObjectDeltaSpawnApplied(ReplicationWorldObjectDelta delta, string spawnKey)
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationWorldObjectDeltaAppliedSpawnKeys.Add(spawnKey);
                CleanupRecentReplicationWorldObjectSpawnLocations();
                ReplicationWorldObjectDeltaRecentSpawnLocationAt[FormatReplicationWorldObjectDeltaLocationKey(delta)] = Time.realtimeSinceStartup;
            }
        }

        private static bool TryFindExistingReplicationBuildingBlueprint(
            string blueprintId,
            int gridX,
            int gridY,
            int gridZ,
            out string detail)
        {
            detail = string.Empty;
            if (TryFindReplicationBuildingBlueprintCandidate(blueprintId, gridX, gridY, gridZ, out _, out detail))
            {
                return true;
            }

            return false;
        }

        private static bool TryReadReplicationBuildingPhase(object buildingInstance, out string phase)
        {
            phase = string.Empty;
            if (TryReadInstanceMemberValue(buildingInstance, "ConstructionPhase", out var value) && value != null)
            {
                phase = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return phase.Length > 0;
            }

            if (TryReadInstanceMemberValue(buildingInstance, "constructionPhase", out value) && value != null)
            {
                phase = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return phase.Length > 0;
            }

            return false;
        }

        private static bool TryReadReplicationBuildingRemainingTimeMs(object buildingInstance, out int remainingTimeMs)
        {
            remainingTimeMs = 0;
            if ((!TryReadInstanceMemberValue(buildingInstance, "RemainingTime", out var value) || value == null)
                && (!TryReadInstanceMemberValue(buildingInstance, "remainingTime", out value) || value == null))
            {
                return false;
            }

            try
            {
                var remainingSeconds = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                remainingTimeMs = Math.Max(0, (int)Math.Round(remainingSeconds * 1000f));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadReplicationBoolMember(object target, string upperName, string lowerName, out bool value)
        {
            value = false;
            if ((!TryReadInstanceMemberValue(target, upperName, out var memberValue) || memberValue == null)
                && (!TryReadInstanceMemberValue(target, lowerName, out memberValue) || memberValue == null))
            {
                return false;
            }

            try
            {
                value = Convert.ToBoolean(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyReplicationBuildingConstructionPhase(object buildingInstance, string phase, out string detail)
        {
            if (TryApplyReplicationBuildingPhaseLifecycle(buildingInstance, phase, out detail))
            {
                return true;
            }

            var attempts = new StringBuilder(256);
            for (var current = buildingInstance.GetType(); current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "SetConstructionPhase", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = methods[i].GetParameters();
                    if (parameters.Length != 1)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param-count=" + parameters.Length.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    if (!TryConvertReplicationBuildingPhaseArgument(phase, parameters[0].ParameterType, out var convertedPhase))
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param=" + (parameters[0].ParameterType.FullName ?? parameters[0].ParameterType.Name));
                        continue;
                    }

                    try
                    {
                        methods[i].Invoke(buildingInstance, new[] { convertedPhase });
                        detail = "phase=SetConstructionPhase signature=" + FormatReplicationMethodSignature(methods[i]) + " phase=" + phase;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], FormatReflectionExceptionDetail(ex));
                    }
                }
            }

            if (TrySetReplicationBuildingPhaseMember(buildingInstance, phase, out var setDetail))
            {
                detail = setDetail;
                return true;
            }

            detail = attempts.Length == 0
                ? "phase-method-missing " + setDetail
                : "phase-method-failed attempts=" + attempts + " " + setDetail;
            return false;
        }

        private static bool TryApplyReplicationBuildingPhaseLifecycle(object buildingInstance, string phase, out string detail)
        {
            var normalized = (phase ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                detail = "phase-lifecycle-empty";
                return false;
            }

            if (string.Equals(normalized, "Foundation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "UnderConstruction", StringComparison.OrdinalIgnoreCase))
            {
                return TryInvokeReplicationBuildingPhaseMethod(
                    buildingInstance,
                    normalized,
                    out detail,
                    "EnterFoundationState",
                    "EnterUnderConstructionState",
                    "ConstructionStarted");
            }

            if (string.Equals(normalized, "Finished", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Built", StringComparison.OrdinalIgnoreCase))
            {
                return TryInvokeReplicationBuildingPhaseMethod(
                    buildingInstance,
                    normalized,
                    out detail,
                    "EnterFinishedState",
                    "ConstructionCompleted");
            }

            if (string.Equals(normalized, "Preview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "Blueprint", StringComparison.OrdinalIgnoreCase))
            {
                return TryInvokeReplicationBuildingPhaseMethod(
                    buildingInstance,
                    normalized,
                    out detail,
                    "ReturnToBlueprint",
                    "EnterBlueprintState");
            }

            detail = "phase-lifecycle-unsupported phase=" + normalized;
            return false;
        }

        private static bool TryInvokeReplicationBuildingPhaseMethod(object buildingInstance, string phase, out string detail, params string[] methodNames)
        {
            var attempts = new StringBuilder(256);
            for (var i = 0; i < methodNames.Length; i++)
            {
                if (TryInvokeReplicationObjectVoidMethod(buildingInstance, methodNames[i], out var methodDetail))
                {
                    detail = "phase=lifecycle method=" + methodDetail + " phase=" + phase;
                    return true;
                }

                AppendReplicationAttemptDetail(attempts, null, methodDetail);
            }

            detail = "phase-lifecycle-methods-failed phase=" + phase + " attempts=" + attempts;
            return false;
        }

        private static bool TrySetReplicationBuildingPhaseMember(object buildingInstance, string phase, out string detail)
        {
            detail = "phase-member-not-set";
            var type = buildingInstance.GetType();
            var memberNames = new[] { "ConstructionPhase", "constructionPhase" };
            for (var i = 0; i < memberNames.Length; i++)
            {
                var property = AccessTools.Property(type, memberNames[i]);
                if (property != null
                    && property.CanWrite
                    && TryConvertReplicationBuildingPhaseArgument(phase, property.PropertyType, out var propertyValue))
                {
                    property.SetValue(buildingInstance, propertyValue, null);
                    detail = "phase=property name=" + memberNames[i] + " phase=" + phase;
                    return true;
                }

                var field = AccessTools.Field(type, memberNames[i]);
                if (field != null
                    && TryConvertReplicationBuildingPhaseArgument(phase, field.FieldType, out var fieldValue))
                {
                    field.SetValue(buildingInstance, fieldValue);
                    detail = "phase=field name=" + memberNames[i] + " phase=" + phase;
                    return true;
                }
            }

            return false;
        }

        private static bool TryConvertReplicationBuildingPhaseArgument(string phase, Type parameterType, out object convertedPhase)
        {
            convertedPhase = phase;
            var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (targetType == typeof(string))
            {
                return true;
            }

            if (targetType.IsEnum)
            {
                try
                {
                    convertedPhase = Enum.Parse(targetType, phase, ignoreCase: true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if ((targetType == typeof(int) || targetType == typeof(short) || targetType == typeof(byte))
                && int.TryParse(phase, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericPhase))
            {
                convertedPhase = Convert.ChangeType(numericPhase, targetType, CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        private static bool TryApplyReplicationBuildingViewPhase(object candidate, string phase, out string detail)
        {
            if (string.Equals(phase, "Foundation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, "UnderConstruction", StringComparison.OrdinalIgnoreCase))
            {
                return TryInvokeReplicationBuildingViewPhaseMethod(
                    candidate,
                    "OnBaseBuildingEnterFoundationState",
                    phase,
                    out detail);
            }

            if (string.Equals(phase, "Finished", StringComparison.OrdinalIgnoreCase)
                || string.Equals(phase, "Built", StringComparison.OrdinalIgnoreCase))
            {
                return TryInvokeReplicationBuildingViewPhaseMethod(
                    candidate,
                    "OnBaseBuildingEnterFinishedState",
                    phase,
                    out detail);
            }

            detail = "view-phase-unsupported phase=" + phase;
            return false;
        }

        private static bool TryInvokeReplicationBuildingViewPhaseMethod(
            object candidate,
            string methodName,
            string phase,
            out string detail)
        {
            for (var current = candidate.GetType(); current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != 1)
                    {
                        continue;
                    }

                    try
                    {
                        if (parameters[0].ParameterType != typeof(bool))
                        {
                            continue;
                        }

                        method.Invoke(candidate, new object[] { false });
                        detail = "viewPhase=" + FormatReplicationMethodSignature(method) + " phase=" + phase;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        detail = "viewPhase=" + FormatReplicationMethodSignature(method) + " " + FormatReflectionExceptionDetail(ex);
                        return false;
                    }
                }
            }

            detail = "view-phase-method-missing method=" + methodName + " candidate=" + FormatShortTypeName(candidate.GetType());
            return false;
        }

        private static bool TryApplyReplicationBuildingRemainingTime(object buildingInstance, int remainingTimeMs, out string detail)
        {
            var remainingSeconds = Math.Max(0f, remainingTimeMs / 1000f);
            var attempts = new StringBuilder(256);
            for (var current = buildingInstance.GetType(); current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, "SetRemainingTime", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = methods[i].GetParameters();
                    if (parameters.Length != 1)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param-count=" + parameters.Length.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    if (!TryConvertReplicationFloatArgument(remainingSeconds, parameters[0].ParameterType, out var convertedRemaining))
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param=" + (parameters[0].ParameterType.FullName ?? parameters[0].ParameterType.Name));
                        continue;
                    }

                    try
                    {
                        methods[i].Invoke(buildingInstance, new[] { convertedRemaining });
                        detail = "remainingTime=SetRemainingTime signature=" + FormatReplicationMethodSignature(methods[i]) + " ms=" + remainingTimeMs.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], FormatReflectionExceptionDetail(ex));
                    }
                }
            }

            if (TrySetInstanceMemberValue(buildingInstance, "RemainingTime", remainingSeconds)
                || TrySetInstanceMemberValue(buildingInstance, "remainingTime", remainingSeconds))
            {
                detail = "remainingTime=member ms=" + remainingTimeMs.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            detail = attempts.Length == 0
                ? "remaining-time-method-missing"
                : "remaining-time-method-failed attempts=" + attempts;
            return false;
        }

        private static bool TryConvertReplicationFloatArgument(float value, Type parameterType, out object convertedValue)
        {
            convertedValue = value;
            var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            try
            {
                if (targetType == typeof(float))
                {
                    convertedValue = value;
                    return true;
                }

                if (targetType == typeof(double))
                {
                    convertedValue = (double)value;
                    return true;
                }

                if (targetType == typeof(int))
                {
                    convertedValue = (int)Math.Round(value);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryRefreshReplicationBuilding(object candidate, object buildingInstance, out string detail)
        {
            if (TryInvokeReplicationObjectVoidMethod(buildingInstance, "RefreshBuilding", out var instanceDetail)
                || TryInvokeReplicationObjectVoidMethod(buildingInstance, "Refresh", out instanceDetail)
                || TryInvokeReplicationObjectVoidMethod(buildingInstance, "RefreshWalkableCollider", out instanceDetail)
                || TryInvokeReplicationObjectVoidMethod(candidate, "RefreshBuilding", out instanceDetail)
                || TryInvokeReplicationObjectVoidMethod(candidate, "Refresh", out instanceDetail)
                || TryInvokeReplicationObjectVoidMethod(candidate, "UpdateViewImmediate", out instanceDetail)
                || TryInvokeReplicationObjectVoidMethod(candidate, "RefreshVisibility", out instanceDetail))
            {
                detail = "refresh=" + instanceDetail;
                return true;
            }

            detail = "refresh-skipped " + instanceDetail;
            return false;
        }

        private static bool TryInvokeReplicationObjectVoidMethod(object owner, string methodName, out string detail)
        {
            detail = methodName + "-method-missing";
            try
            {
                var method = owner.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 0)
                {
                    detail = methodName + "-unsupported-param-count=" + parameters.Length.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                method.Invoke(owner, Array.Empty<object>());
                detail = FormatReplicationMethodSignature(method);
                return true;
            }
            catch (Exception ex)
            {
                detail = methodName + "-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryFindReplicationBuildingBlueprintCandidate(
            ReplicationWorldObjectDelta delta,
            out object? candidate,
            out string detail)
        {
            if (TryGetReplicationLocalObjectByHostId(delta.UniqueId, out var mappedCandidate, out var mappedDetail) && mappedCandidate != null)
            {
                candidate = mappedCandidate;
                detail = mappedDetail;
                return true;
            }

            if (TryFindReplicationBuildingBlueprintCandidate(
                delta.BlueprintId,
                delta.GridX,
                delta.GridY,
                delta.GridZ,
                out candidate,
                out detail))
            {
                RegisterReplicationHostIdentity(delta.UniqueId, candidate, "building-grid-fallback");
                return true;
            }

            return false;
        }

        private static bool TryFindReplicationBuildingBlueprintCandidate(
            string blueprintId,
            int gridX,
            int gridY,
            int gridZ,
            out object? candidate,
            out string detail)
        {
            candidate = null;
            detail = string.Empty;
            try
            {
                var viewComponentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
                if (viewComponentType == null)
                {
                    detail = "building-view-component-type-missing";
                    return false;
                }

                var candidates = UnityEngine.Object.FindObjectsOfType(viewComponentType);
                var scanned = 0;
                for (var i = 0; i < candidates.Length; i++)
                {
                    var candidateObject = candidates[i];
                    if (candidateObject == null)
                    {
                        continue;
                    }

                    scanned++;
                    if (!TryResolveReplicationBuildingCandidateGrid(candidateObject, out var x, out var y, out var z)
                        || x != gridX
                        || y != gridY
                        || z != gridZ)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(blueprintId)
                        && TryResolveReplicationBuildingCandidateBlueprintId(candidateObject, out var localBlueprintId)
                        && !string.IsNullOrWhiteSpace(localBlueprintId)
                        && !string.Equals(localBlueprintId, blueprintId, StringComparison.Ordinal))
                    {
                        detail = "existing-building-blueprint-mismatch scanned="
                            + scanned.ToString(CultureInfo.InvariantCulture)
                            + " localBlueprintId="
                            + localBlueprintId
                            + " expectedBlueprintId="
                            + blueprintId
                            + " grid=Vec3Int("
                            + gridX.ToString(CultureInfo.InvariantCulture)
                            + ","
                            + gridY.ToString(CultureInfo.InvariantCulture)
                            + ","
                            + gridZ.ToString(CultureInfo.InvariantCulture)
                            + ")";
                        return false;
                    }

                    TryResolveReplicationBuildingCandidateBlueprintId(candidateObject, out var matchedBlueprintId);
                    candidate = candidateObject;
                    detail = "matched-existing-building scanned="
                        + scanned.ToString(CultureInfo.InvariantCulture)
                        + " blueprintId="
                        + matchedBlueprintId
                        + " grid=Vec3Int("
                        + gridX.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + gridY.ToString(CultureInfo.InvariantCulture)
                        + ","
                        + gridZ.ToString(CultureInfo.InvariantCulture)
                        + ")";
                    return true;
                }

                detail = "no-existing-building scanned="
                    + scanned.ToString(CultureInfo.InvariantCulture)
                    + " blueprintId="
                    + blueprintId
                    + " grid=Vec3Int("
                    + gridX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + gridY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + gridZ.ToString(CultureInfo.InvariantCulture)
                    + ")";
                return false;
            }
            catch (Exception ex)
            {
                detail = "existing-building-lookup-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryRemoveReplicationBuildingCandidate(object candidate, ReplicationWorldObjectDelta delta, out string detail)
        {
            detail = string.Empty;
            if (!TryResolveReplicationBuildingCandidateInstance(candidate, out var buildingInstance, out var instanceDetail)
                || buildingInstance == null)
            {
                return TryDisposeReplicationBuildingViewCandidate(candidate, out detail);
            }

            var worldPosition = ResolveReplicationBuildingCandidateWorldPosition(candidate, delta, out var worldPositionDetail);
            var cancelMethod = FindReplicationInstanceMethod(buildingInstance.GetType(), "BuildingCanceled", new[] { typeof(Vector3) });
            if (cancelMethod != null)
            {
                try
                {
                    cancelMethod.Invoke(buildingInstance, new object[] { worldPosition });
                    detail = "remove=BaseBuildingInstance.BuildingCanceled " + instanceDetail + " " + worldPositionDetail;
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "remove=BaseBuildingInstance.BuildingCanceled " + FormatReflectionExceptionDetail(ex) + " " + instanceDetail + " " + worldPositionDetail;
                }
            }

            if (TryResolveReplicationBuildingsManagerMain(
                out var manager,
                out var managerDetail)
                && manager != null)
            {
                var destroyMethod = FindReplicationInstanceMethod(manager.GetType(), "DestroyBuilding", new[] { buildingInstance.GetType(), typeof(bool), typeof(bool) });
                if (destroyMethod != null)
                {
                    try
                    {
                        destroyMethod.Invoke(manager, new object[] { buildingInstance, false, false });
                        detail = "remove=BuildingsManagerMain.DestroyBuilding " + managerDetail + " " + instanceDetail;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        detail = detail + " destroy=" + FormatReflectionExceptionDetail(ex) + " " + managerDetail;
                    }
                }
            }

            if (TryDisposeReplicationBuildingViewCandidate(candidate, out var disposeDetail))
            {
                detail = "remove=view-fallback " + disposeDetail + " " + instanceDetail;
                return true;
            }

            detail = detail + " viewDispose=" + disposeDetail + " " + instanceDetail;
            return false;
        }

        private static bool TryResolveReplicationBuildingsManagerMain(out object? manager, out string detail)
        {
            manager = null;
            var buildingsManagerMainType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BuildingsManagerMain");
            if (buildingsManagerMainType == null)
            {
                detail = "buildings-manager-main-type-missing";
                return false;
            }

            manager = ResolveReplicationBuildingsManagerMain(buildingsManagerMainType, out detail);
            return manager != null;
        }

        private static bool TryResolveReplicationBuildingCandidateInstance(object candidate, out object? buildingInstance, out string detail)
        {
            if (TryReadInstanceMemberValue(candidate, "BaseBuildingInstance", out buildingInstance) && buildingInstance != null)
            {
                detail = "instance=BaseBuildingInstance";
                return true;
            }

            if (TryReadInstanceMemberValue(candidate, "baseBuildingInstance", out buildingInstance) && buildingInstance != null)
            {
                detail = "instance=baseBuildingInstance";
                return true;
            }

            if (TryInvokeReplicationObjectMethod(candidate, "get_BaseBuildingInstance", out buildingInstance) && buildingInstance != null)
            {
                detail = "instance=get_BaseBuildingInstance";
                return true;
            }

            buildingInstance = null;
            detail = "building-instance-missing candidateType=" + (candidate.GetType().FullName ?? candidate.GetType().Name);
            return false;
        }

        private static Vector3 ResolveReplicationBuildingCandidateWorldPosition(object candidate, ReplicationWorldObjectDelta delta, out string detail)
        {
            if (candidate is Component component)
            {
                detail = "worldPosition=candidate.transform value=" + FormatUnityVector(component.transform.position);
                return component.transform.position;
            }

            if (TryCreateVec3Int(delta.GridX, delta.GridY, delta.GridZ, out var gridPosition, out _))
            {
                return GetReplicationBuildWorldPosition(gridPosition, delta.GridX, delta.GridY, delta.GridZ, out detail);
            }

            var fallback = new Vector3(delta.GridX, delta.GridY, delta.GridZ);
            detail = "worldPosition=fallback-delta value=" + FormatUnityVector(fallback);
            return fallback;
        }

        private static bool TryDisposeReplicationBuildingViewCandidate(object candidate, out string detail)
        {
            var disposeMethod = FindReplicationInstanceMethod(candidate.GetType(), "Dispose", Type.EmptyTypes);
            if (disposeMethod != null)
            {
                try
                {
                    disposeMethod.Invoke(candidate, null);
                    detail = "remove=BaseBuildingViewComponent.Dispose candidateType=" + (candidate.GetType().FullName ?? candidate.GetType().Name);
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "view-dispose-failed " + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            if (candidate is UnityEngine.Object unityObject)
            {
                UnityEngine.Object.Destroy(unityObject);
                detail = "remove=UnityObject.Destroy candidateType=" + (candidate.GetType().FullName ?? candidate.GetType().Name);
                return true;
            }

            detail = "view-dispose-method-missing candidateType=" + (candidate.GetType().FullName ?? candidate.GetType().Name);
            return false;
        }

        private static bool TryResolveReplicationBuildingCandidateGrid(object candidate, out int x, out int y, out int z)
        {
            if (TryReadReplicationWorldObjectGridPosition(candidate, out x, out y, out z))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(candidate, "BaseBuildingInstance", out var buildingInstance)
                && buildingInstance != null
                && TryReadReplicationWorldObjectGridPosition(buildingInstance, out x, out y, out z))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(candidate, "baseBuildingInstance", out buildingInstance)
                && buildingInstance != null
                && TryReadReplicationWorldObjectGridPosition(buildingInstance, out x, out y, out z))
            {
                return true;
            }

            x = 0;
            y = 0;
            z = 0;
            return false;
        }

        private static bool TryResolveReplicationBuildingCandidateBlueprintId(object candidate, out string blueprintId)
        {
            if (TryResolveReplicationBuildingCandidateBlueprintIdDirect(candidate, out blueprintId))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(candidate, "BaseBuildingInstance", out var buildingInstance)
                && buildingInstance != null
                && TryResolveReplicationBuildingCandidateBlueprintIdDirect(buildingInstance, out blueprintId))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(candidate, "baseBuildingInstance", out buildingInstance)
                && buildingInstance != null
                && TryResolveReplicationBuildingCandidateBlueprintIdDirect(buildingInstance, out blueprintId))
            {
                return true;
            }

            blueprintId = string.Empty;
            return false;
        }

        private static bool TryResolveReplicationBuildingCandidateBlueprintIdDirect(object candidate, out string blueprintId)
        {
            if (TryReadInstanceMemberValue(candidate, "BaseBuildingBlueprint", out var blueprint)
                && blueprint != null
                && TryResolveReplicationBuildBlueprintId(blueprint, out blueprintId))
            {
                return true;
            }

            if (TryReadInstanceMemberValue(candidate, "baseBuildingBlueprint", out blueprint)
                && blueprint != null
                && TryResolveReplicationBuildBlueprintId(blueprint, out blueprintId))
            {
                return true;
            }

            if (TryResolveReplicationBuildBlueprintId(candidate, out blueprintId))
            {
                return true;
            }

            blueprintId = string.Empty;
            return false;
        }

        private static bool TryApplyReplicationResourcePileStateSnapshotBegin(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "snapshotId", out var snapshotIdValue) || snapshotIdValue <= 0)
            {
                detail = "pile-state-snapshot-begin-missing-id";
                return false;
            }

            var expectedCount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var parsedCount)
                ? Math.Max(0, parsedCount)
                : 0;
            var hasHostSignature = TryReadReplicationWorldObjectDetailHex(delta.Detail, "signature", out var hostSignature);
            var snapshotId = snapshotIdValue;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationResourcePileStateSnapshotContexts.TryGetValue(snapshotId, out var context))
                {
                    context.ExpectedCount = expectedCount;
                    if (hasHostSignature)
                    {
                        context.HostSignatureValid = true;
                        context.HostSignature = hostSignature;
                    }
                }
                else
                {
                    var newContext = new ReplicationResourcePileStateSnapshotContext(expectedCount);
                    if (hasHostSignature)
                    {
                        newContext.HostSignatureValid = true;
                        newContext.HostSignature = hostSignature;
                    }

                    ReplicationResourcePileStateSnapshotContexts[snapshotId] = newContext;
                }

                CleanupReplicationResourcePileStateSnapshotContexts(snapshotId);
            }

            detail = "ok pile-state-snapshot-begin snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " count="
                + expectedCount.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryApplyReplicationResourcePileState(ReplicationWorldObjectDelta delta, out string detail)
        {
            var snapshotDetail = RecordReplicationResourcePileStateSnapshotKey(delta);
            detail = "ok pile-state-snapshot-recorded " + snapshotDetail;
            return true;
        }

        private static bool TryApplyReplicationResourcePileStateSnapshotEnd(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "snapshotId", out var snapshotIdValue) || snapshotIdValue <= 0)
            {
                detail = "pile-state-snapshot-end-missing-id";
                return false;
            }

            var expectedCount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var parsedCount)
                ? Math.Max(0, parsedCount)
                : 0;
            var hasHostSignature = TryReadReplicationWorldObjectDetailHex(delta.Detail, "signature", out var hostSignature);
            var snapshotId = snapshotIdValue;
            ReplicationResourcePileStateSnapshotContext context;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationResourcePileStateSnapshotContexts.TryGetValue(snapshotId, out context))
                {
                    context = new ReplicationResourcePileStateSnapshotContext(expectedCount);
                    ReplicationResourcePileStateSnapshotContexts[snapshotId] = context;
                }

                context.ExpectedCount = expectedCount;
                if (hasHostSignature)
                {
                    context.HostSignatureValid = true;
                    context.HostSignature = hostSignature;
                }

                context.EndReceived = true;
            }

            if (context.SeenKeys.Count < context.ExpectedCount)
            {
                detail = "ok pile-state-snapshot-end-waiting snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " seen="
                    + context.SeenKeys.Count.ToString(CultureInfo.InvariantCulture)
                    + " expected="
                    + context.ExpectedCount.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            return TryFinalizeReplicationResourcePileStateSnapshot(snapshotId, context, out detail);
        }

        private static bool TryApplyReplicationResourcePileSpawned(ReplicationWorldObjectDelta delta, out string detail)
        {
            var spawnKey = FormatReplicationWorldObjectDeltaSpawnKey(delta);
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationWorldObjectDeltaAppliedSpawnKeys.Contains(spawnKey))
                {
                    detail = "duplicate-spawn-delta-skipped key=" + spawnKey;
                    return true;
                }
            }

            if (!TryResolveReplicationResourceModel(delta.BlueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = "resource-lookup-failed " + resourceDetail;
                return false;
            }

            var amount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var parsedAmount)
                ? Math.Max(1, parsedAmount)
                : 1;

            if (!TryCreateReplicationResourceInstance(resource, amount, out var resourceInstance, out var instanceDetail) || resourceInstance == null)
            {
                detail = "resource-instance-create-failed " + instanceDetail + " " + resourceDetail;
                return false;
            }

            if (!TryGetReplicationResourcePileManager(out var manager, out var managerDetail) || manager == null)
            {
                detail = "resource-pile-manager-missing " + managerDetail;
                return false;
            }

            var worldPosition = GetReplicationWorldPosition(delta.GridX, delta.GridY, delta.GridZ, out var positionDetail);
            if (!TrySpawnReplicationResourcePile(manager, resourceInstance, worldPosition, out var spawnDetail))
            {
                detail = "spawn-failed "
                    + spawnDetail
                    + " "
                    + resourceDetail
                    + " "
                    + instanceDetail
                    + " "
                    + positionDetail
                    + " amount="
                    + amount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationWorldObjectDeltaAppliedSpawnKeys.Add(spawnKey);
                CleanupRecentReplicationWorldObjectSpawnLocations();
                ReplicationWorldObjectDeltaRecentSpawnLocationAt[FormatReplicationWorldObjectDeltaLocationKey(delta)] = Time.realtimeSinceStartup;
            }

            var identityDetail = string.Empty;
            if (TryFindRecentReplicationResourcePile(manager, delta, ReplicationResourcePileRecentLookupMaxCandidates, out var recentPile, out var recentLookupDetail) && recentPile != null)
            {
                RegisterReplicationHostIdentity(delta.UniqueId, recentPile, "resource-pile-spawn-recent");
                RegisterReplicationResourcePileLocation(FormatReplicationWorldObjectDeltaLocationKey(delta), recentPile, "resource-pile-spawn-recent");
                identityDetail = " identity=" + recentLookupDetail;
            }
            else if (TryFindReplicationResourcePile(delta, out var spawnedPile, out var lookupDetail) && spawnedPile != null)
            {
                RegisterReplicationHostIdentity(delta.UniqueId, spawnedPile, "resource-pile-spawn");
                RegisterReplicationResourcePileLocation(FormatReplicationWorldObjectDeltaLocationKey(delta), spawnedPile, "resource-pile-spawn");
                identityDetail = " identity=" + lookupDetail;
            }

            detail = "ok "
                + spawnDetail
                + " "
                + resourceDetail
                + " "
                + instanceDetail
                + " "
                + positionDetail
                + " amount="
                + amount.ToString(CultureInfo.InvariantCulture)
                + identityDetail;
            return true;
        }

        private static bool TryApplyReplicationResourcePileAmountAdded(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryResolveReplicationResourceModel(delta.BlueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = "resource-lookup-failed " + resourceDetail;
                return false;
            }

            var amount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var parsedAmount)
                ? Math.Max(1, parsedAmount)
                : 1;
            var hasExactPileAmount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "pileAmount", out var exactPileAmount)
                && exactPileAmount > 0;
            var resourceInstanceAmount = hasExactPileAmount ? exactPileAmount : amount;

            if (!TryCreateReplicationResourceInstance(resource, resourceInstanceAmount, out var resourceInstance, out var instanceDetail) || resourceInstance == null)
            {
                detail = "resource-instance-create-failed " + instanceDetail + " " + resourceDetail;
                return false;
            }

            if (!TryGetReplicationResourcePileManager(out var manager, out var managerDetail) || manager == null)
            {
                detail = "resource-pile-manager-missing " + managerDetail;
                return false;
            }

            var worldPosition = GetReplicationWorldPosition(delta.GridX, delta.GridY, delta.GridZ, out var positionDetail);
            var managerAddDetail = "manager-add-skipped-authoritative";
            if (!hasExactPileAmount
                && TryInvokeReplicationResourcePileManagerAdd(manager, resourceInstance, delta, worldPosition, out managerAddDetail))
            {
                detail = "ok " + managerAddDetail + " " + resourceDetail + " " + instanceDetail + " " + positionDetail + " amount=" + amount.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            else if (hasExactPileAmount)
            {
                managerAddDetail = "manager-add-skipped-authoritative";
            }

            if (!TryFindReplicationResourcePile(delta, out var pile, out var lookupDetail) || pile == null)
            {
                if (TrySpawnReplicationResourcePile(manager, resourceInstance, worldPosition, out var spawnDetail))
                {
                    if (TryFindRecentReplicationResourcePile(manager, delta, ReplicationResourcePileRecentLookupMaxCandidates, out var recentPile, out var recentLookupDetail) && recentPile != null)
                    {
                        RegisterReplicationHostIdentity(delta.UniqueId, recentPile, "resource-pile-add-fallback-spawn-recent");
                        RegisterReplicationResourcePileLocation(FormatReplicationWorldObjectDeltaLocationKey(delta), recentPile, "resource-pile-add-fallback-spawn-recent");
                        spawnDetail += " identity=" + recentLookupDetail;
                    }
                    else if (TryFindReplicationResourcePile(delta, out var spawnedPile, out var spawnedLookupDetail) && spawnedPile != null)
                    {
                        RegisterReplicationHostIdentity(delta.UniqueId, spawnedPile, "resource-pile-add-fallback-spawn");
                        RegisterReplicationResourcePileLocation(FormatReplicationWorldObjectDeltaLocationKey(delta), spawnedPile, "resource-pile-add-fallback-spawn");
                        spawnDetail += " identity=" + spawnedLookupDetail;
                    }

                    detail = "ok add-target-missing-fallback-spawn " + spawnDetail + " lookup=" + lookupDetail + " managerAdd=" + managerAddDetail + " " + resourceDetail + " " + instanceDetail + " " + positionDetail + " amount=" + resourceInstanceAmount.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                detail = "resource-pile-add-target-missing lookup=" + lookupDetail + " managerAdd=" + managerAddDetail + " fallback-spawn-failed " + spawnDetail + " " + resourceDetail + " " + instanceDetail + " amount=" + resourceInstanceAmount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (hasExactPileAmount)
            {
                if (!TrySetReplicationExistingResourcePileAmount(pile, resourceInstance, exactPileAmount, out var pileSetDetail))
                {
                    detail = "resource-pile-set-failed " + pileSetDetail + " lookup=" + lookupDetail + " managerAdd=" + managerAddDetail + " " + resourceDetail + " " + instanceDetail + " pileAmount=" + exactPileAmount.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                detail = "ok authoritative-pile-state " + pileSetDetail + " lookup=" + lookupDetail + " managerAdd=" + managerAddDetail + " " + resourceDetail + " " + instanceDetail + " pileAmount=" + exactPileAmount.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (!TryAddReplicationAmountToExistingResourcePile(pile, resourceInstance, amount, out var pileAddDetail))
            {
                detail = "resource-pile-add-failed " + pileAddDetail + " lookup=" + lookupDetail + " managerAdd=" + managerAddDetail + " " + resourceDetail + " " + instanceDetail + " amount=" + amount.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            detail = "ok " + pileAddDetail + " lookup=" + lookupDetail + " managerAdd=" + managerAddDetail + " " + resourceDetail + " " + instanceDetail + " amount=" + amount.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryApplyReplicationResourcePileDisposed(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryFindReplicationResourcePile(delta, out var pile, out var lookupDetail) || pile == null)
            {
                detail = "resource-pile-lookup-failed " + lookupDetail;
                return false;
            }

            var disposed = TryDisposeReplicationResourcePile(pile, "lookup=" + lookupDetail, out detail);
            if (disposed)
            {
                RemoveReplicationHostIdentity(delta.UniqueId, pile, "resource-pile-dispose");
                RemoveReplicationResourcePileLocation(FormatReplicationWorldObjectDeltaLocationKey(delta), pile, "resource-pile-dispose");
            }

            return disposed;
        }

        private static bool TryApplyReplicationMapResourceDisposed(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (TryGetReplicationLocalObjectByHostId(delta.UniqueId, out var mappedPlant, out var mappedDetail) && mappedPlant != null)
            {
                var disposed = TryDisposeReplicationMapResource(mappedPlant, mappedDetail, out detail);
                if (disposed)
                {
                    RemoveReplicationHostIdentity(delta.UniqueId, mappedPlant, "map-resource-dispose");
                }

                return disposed;
            }

            if (delta.Detail.IndexOf("PlantMapResourceInstance.Dispose", StringComparison.Ordinal) >= 0
                && !TryGetPlantAt(delta.GridX, delta.GridY, delta.GridZ, out _, out var directPlantDetail)
                && string.Equals(directPlantDetail, "plant-missing", StringComparison.Ordinal))
            {
                // PlantResourceManager is the authoritative index for this captured
                // runtime type. Avoid a full scan of every map-resource dictionary
                // when its direct coordinate lookup already proves the delete done.
                detail = "ok already-disposed source=plant-manager " + directPlantDetail;
                return true;
            }

            if (!TryFindReplicationMapResourceAt(delta.GridX, delta.GridY, delta.GridZ, delta.BlueprintId, delta.UniqueId, out var plant, out var lookupDetail) || plant == null)
            {
                if (lookupDetail.StartsWith("map-resource-missing ", StringComparison.Ordinal)
                    && TryReadReplicationWorldObjectDetailInt(lookupDetail, "scannedManagers", out var scannedManagers)
                    && scannedManagers > 0
                    && TryReadReplicationWorldObjectDetailInt(lookupDetail, "scannedEntries", out var scannedEntries)
                    && scannedEntries > 0)
                {
                    // Dispose is idempotent. A completed manager scan proving the
                    // coordinate is already absent has reached the authoritative
                    // postcondition and must be acknowledged to prevent retry scans.
                    detail = "ok already-disposed " + lookupDetail;
                    return true;
                }

                detail = "map-resource-lookup-failed " + lookupDetail;
                return false;
            }

            if (TryReadReplicationWorldObjectLongMember(plant, "UniqueId", "uniqueId", out var localUniqueId)
                && localUniqueId != delta.UniqueId)
            {
                if (!TryReadReplicationWorldObjectStringMember(plant, "BlueprintId", "blueprintId", out var localBlueprintId)
                    || !string.Equals(localBlueprintId, delta.BlueprintId, StringComparison.Ordinal))
                {
                    detail = "unique-id-mismatch expected="
                        + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                        + " actual="
                        + localUniqueId.ToString(CultureInfo.InvariantCulture)
                        + " localBlueprintId="
                        + localBlueprintId
                        + " expectedBlueprintId="
                        + delta.BlueprintId;
                    return false;
                }

                detail = "unique-id-mismatch-accepted expected="
                    + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                    + " actual="
                    + localUniqueId.ToString(CultureInfo.InvariantCulture)
                    + " blueprintId="
                    + localBlueprintId
                    + " lookup="
                    + lookupDetail;
                var acceptedDisposed = TryDisposeReplicationMapResource(plant, detail, out detail);
                if (acceptedDisposed)
                {
                    RemoveReplicationHostIdentity(delta.UniqueId, plant, "map-resource-dispose-fallback");
                }

                return acceptedDisposed;
            }

            var gridDisposed = TryDisposeReplicationMapResource(plant, "lookup=" + lookupDetail, out detail);
            if (gridDisposed)
            {
                RemoveReplicationHostIdentity(delta.UniqueId, plant, "map-resource-dispose-grid");
            }

            return gridDisposed;
        }

        private static bool TryDisposeReplicationMapResource(object plant, string applyDetail, out string detail)
        {
            try
            {
                var method = AccessTools.Method(plant.GetType(), "Dispose", Type.EmptyTypes)
                    ?? AccessTools.Method(plant.GetType(), "Dispose");
                if (method == null)
                {
                    detail = "dispose-method-missing type=" + (plant.GetType().FullName ?? plant.GetType().Name);
                    return false;
                }

                method.Invoke(plant, null);
                detail = "ok via=Dispose " + applyDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryFindReplicationMapResourceAt(
            int gridX,
            int gridY,
            int gridZ,
            string blueprintId,
            long uniqueId,
            out object? resource,
            out string detail)
        {
            resource = null;
            if (TryGetPlantAt(gridX, gridY, gridZ, out var plant, out var plantDetail) && plant != null)
            {
                resource = plant;
                detail = "source=plant-manager " + plantDetail;
                return true;
            }

            if (!TryCreateVec3Int(gridX, gridY, gridZ, out var position, out var vecDetail) || position == null)
            {
                detail = "vec-missing " + vecDetail + " plant=" + plantDetail;
                return false;
            }

            var managerBaseType = AccessTools.TypeByName("NSMedieval.Manager.MapResourceManager`3");
            if (managerBaseType == null)
            {
                detail = "map-resource-manager-type-missing plant=" + plantDetail;
                return false;
            }

            MonoBehaviour[] behaviours;
            try
            {
                behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>() ?? Array.Empty<MonoBehaviour>();
            }
            catch (Exception ex)
            {
                detail = "map-resource-manager-scan-failed " + FormatReflectionExceptionDetail(ex) + " plant=" + plantDetail;
                return false;
            }

            object? gridMatch = null;
            string gridMatchDetail = string.Empty;
            object? blueprintMatch = null;
            string blueprintMatchDetail = string.Empty;
            object? uniqueMatch = null;
            string uniqueMatchDetail = string.Empty;
            var scannedManagers = 0;
            var scannedEntries = 0;
            for (var i = 0; i < behaviours.Length; i++)
            {
                var manager = behaviours[i];
                if (manager == null || !IsReplicationClosedGenericSubclass(manager.GetType(), managerBaseType))
                {
                    continue;
                }

                scannedManagers++;
                if (!TryReadInstanceMemberValue(manager, "PositionInstanceDictionary", out var dictionary) || dictionary == null)
                {
                    continue;
                }

                if (TryFindReplicationMapResourceInPositionDictionary(
                    dictionary,
                    position,
                    gridX,
                    gridY,
                    gridZ,
                    blueprintId,
                    uniqueId,
                    out var found,
                    out var foundDetail,
                    ref scannedEntries)
                    && found != null)
                {
                    resource = found;
                    detail = "source=position-dictionary manager="
                        + FormatShortTypeName(manager.GetType())
                        + " "
                        + foundDetail
                        + " scannedManagers="
                        + scannedManagers.ToString(CultureInfo.InvariantCulture)
                        + " scannedEntries="
                        + scannedEntries.ToString(CultureInfo.InvariantCulture)
                        + " plant="
                        + plantDetail;
                    return true;
                }

                if (TryFindReplicationMapResourceDictionaryCandidate(dictionary, gridX, gridY, gridZ, out var candidate, out var candidateDetail, ref scannedEntries)
                    && candidate != null)
                {
                    var hasUnique = TryReadReplicationWorldObjectLongMember(candidate, "UniqueId", "uniqueId", out var localUniqueId);
                    TryReadReplicationWorldObjectStringMember(candidate, "BlueprintId", "blueprintId", out var localBlueprintId);
                    var localBlueprintMatches = string.IsNullOrWhiteSpace(blueprintId)
                        || string.IsNullOrWhiteSpace(localBlueprintId)
                        || string.Equals(localBlueprintId, blueprintId, StringComparison.Ordinal);

                    if (hasUnique && localUniqueId == uniqueId)
                    {
                        uniqueMatch = candidate;
                        uniqueMatchDetail = candidateDetail + " uniqueId=matched";
                    }

                    if (localBlueprintMatches)
                    {
                        blueprintMatch = candidate;
                        blueprintMatchDetail = candidateDetail
                            + " blueprint=matched localBlueprintId="
                            + FormatReplicationWorldObjectDetailToken(localBlueprintId);
                    }

                    if (gridMatch == null)
                    {
                        gridMatch = candidate;
                        gridMatchDetail = candidateDetail
                            + " localBlueprintId="
                            + FormatReplicationWorldObjectDetailToken(localBlueprintId);
                    }
                }
            }

            if (uniqueMatch != null)
            {
                resource = uniqueMatch;
                detail = "source=map-resource-scan " + uniqueMatchDetail;
                return true;
            }

            if (blueprintMatch != null)
            {
                resource = blueprintMatch;
                detail = "source=map-resource-scan " + blueprintMatchDetail;
                return true;
            }

            if (gridMatch != null && string.IsNullOrWhiteSpace(blueprintId))
            {
                resource = gridMatch;
                detail = "source=map-resource-scan " + gridMatchDetail;
                return true;
            }

            detail = "map-resource-missing grid=Vec3Int("
                + gridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + gridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + gridZ.ToString(CultureInfo.InvariantCulture)
                + ") blueprintId="
                + FormatReplicationWorldObjectDetailToken(blueprintId)
                + " scannedManagers="
                + scannedManagers.ToString(CultureInfo.InvariantCulture)
                + " scannedEntries="
                + scannedEntries.ToString(CultureInfo.InvariantCulture)
                + " plant="
                + plantDetail;
            return false;
        }

        private static bool TryFindReplicationMapResourceInPositionDictionary(
            object dictionary,
            object position,
            int gridX,
            int gridY,
            int gridZ,
            string blueprintId,
            long uniqueId,
            out object? resource,
            out string detail,
            ref int scannedEntries)
        {
            resource = null;
            detail = string.Empty;

            var tryGetValue = FindReplicationInstanceMethod(dictionary.GetType(), "TryGetValue", new[] { position.GetType(), typeof(object).MakeByRefType() });
            if (tryGetValue != null)
            {
                try
                {
                    var args = new object?[] { position, null };
                    var result = tryGetValue.Invoke(dictionary, args);
                    if (result is bool found && found && args.Length > 1 && args[1] != null)
                    {
                        var candidate = args[1];
                        if (IsReplicationMapResourceCandidateAcceptable(candidate, blueprintId, uniqueId, out var acceptDetail))
                        {
                            resource = candidate;
                            detail = "lookup=TryGetValue " + acceptDetail;
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }

            return TryFindReplicationMapResourceDictionaryCandidate(dictionary, gridX, gridY, gridZ, out resource, out detail, ref scannedEntries)
                && resource != null
                && IsReplicationMapResourceCandidateAcceptable(resource, blueprintId, uniqueId, out detail);
        }

        private static bool TryFindReplicationMapResourceDictionaryCandidate(
            object dictionary,
            int gridX,
            int gridY,
            int gridZ,
            out object? resource,
            out string detail,
            ref int scannedEntries)
        {
            resource = null;
            detail = string.Empty;
            if (dictionary is not System.Collections.IEnumerable enumerable)
            {
                detail = "dictionary-not-enumerable type=" + FormatShortTypeName(dictionary.GetType());
                return false;
            }

            foreach (var entry in enumerable)
            {
                if (entry == null)
                {
                    continue;
                }

                scannedEntries++;
                if (!TryReadInstanceMemberValue(entry, "Key", out var key)
                    || key == null
                    || !TryReadVec3IntLikeValue(key, out var keyX, out var keyY, out var keyZ)
                    || keyX != gridX
                    || keyY != gridY
                    || keyZ != gridZ)
                {
                    continue;
                }

                if (!TryReadInstanceMemberValue(entry, "Value", out resource) || resource == null)
                {
                    continue;
                }

                detail = "lookup=enumerated key=Vec3Int("
                    + keyX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + keyY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + keyZ.ToString(CultureInfo.InvariantCulture)
                    + ") type="
                    + FormatShortTypeName(resource.GetType());
                return true;
            }

            detail = "dictionary-position-missing";
            return false;
        }

        private static bool IsReplicationMapResourceCandidateAcceptable(object? candidate, string blueprintId, long uniqueId, out string detail)
        {
            detail = string.Empty;
            if (candidate == null)
            {
                detail = "candidate-missing";
                return false;
            }

            var hasUniqueId = TryReadReplicationWorldObjectLongMember(candidate, "UniqueId", "uniqueId", out var localUniqueId);
            TryReadReplicationWorldObjectStringMember(candidate, "BlueprintId", "blueprintId", out var localBlueprintId);
            var blueprintMatches = string.IsNullOrWhiteSpace(blueprintId)
                || string.IsNullOrWhiteSpace(localBlueprintId)
                || string.Equals(localBlueprintId, blueprintId, StringComparison.Ordinal);
            if (hasUniqueId && localUniqueId == uniqueId)
            {
                detail = "uniqueId=matched localBlueprintId=" + FormatReplicationWorldObjectDetailToken(localBlueprintId);
                return true;
            }

            if (blueprintMatches)
            {
                detail = "blueprint=matched localUniqueId="
                    + (hasUniqueId ? localUniqueId.ToString(CultureInfo.InvariantCulture) : "missing")
                    + " localBlueprintId="
                    + FormatReplicationWorldObjectDetailToken(localBlueprintId);
                return true;
            }

            detail = "candidate-mismatch localUniqueId="
                + (hasUniqueId ? localUniqueId.ToString(CultureInfo.InvariantCulture) : "missing")
                + " expectedUniqueId="
                + uniqueId.ToString(CultureInfo.InvariantCulture)
                + " localBlueprintId="
                + FormatReplicationWorldObjectDetailToken(localBlueprintId)
                + " expectedBlueprintId="
                + FormatReplicationWorldObjectDetailToken(blueprintId);
            return false;
        }

        private static bool IsReplicationClosedGenericSubclass(Type type, Type openGenericType)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == openGenericType)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindReplicationResourcePile(ReplicationWorldObjectDelta delta, out object? pile, out string detail)
        {
            pile = null;
            if (TryGetReplicationLocalObjectByHostId(delta.UniqueId, out var mappedPile, out var mappedDetail) && mappedPile != null)
            {
                pile = mappedPile;
                detail = mappedDetail;
                return true;
            }

            var locationKey = FormatReplicationWorldObjectDeltaLocationKey(delta);
            if (TryGetReplicationResourcePileByLocationKey(locationKey, out var mappedLocationPile, out var mappedLocationDetail) && mappedLocationPile != null)
            {
                pile = mappedLocationPile;
                RegisterReplicationHostIdentity(delta.UniqueId, pile, "resource-pile-location-hit");
                detail = mappedLocationDetail;
                return true;
            }

            if (!TryGetReplicationResourcePileManager(out var manager, out var managerDetail) || manager == null)
            {
                detail = "manager-missing " + managerDetail;
                return false;
            }

            if (!TryReadInstanceMemberValue(manager, "SpawnedPileInstances", out var spawned)
                || spawned == null)
            {
                detail = "spawned-pile-list-missing " + managerDetail;
                return false;
            }

            if (!(spawned is System.Collections.IEnumerable enumerable))
            {
                detail = "spawned-pile-list-not-enumerable type=" + spawned.GetType().FullName;
                return false;
            }

            object? gridMatch = null;
            object? uniqueBlueprintMatch = null;
            string uniqueBlueprintDetail = string.Empty;
            object? unsafeUniqueMatch = null;
            string unsafeUniqueDetail = string.Empty;
            var scanned = 0;
            foreach (var candidate in enumerable)
            {
                if (candidate == null)
                {
                    continue;
                }

                scanned++;
                var hasUniqueId = TryReadReplicationWorldObjectLongMember(candidate, "UniqueId", "uniqueId", out var localUniqueId);
                TryReadReplicationWorldObjectStringMember(candidate, "BlueprintId", "blueprintId", out var localBlueprintId);
                var hasGrid = TryReadReplicationWorldObjectGridPosition(candidate, out var x, out var y, out var z);
                var blueprintMatches = string.IsNullOrEmpty(delta.BlueprintId)
                    || string.IsNullOrEmpty(localBlueprintId)
                    || string.Equals(localBlueprintId, delta.BlueprintId, StringComparison.Ordinal);

                if (hasGrid
                    && x == delta.GridX
                    && y == delta.GridY
                    && z == delta.GridZ
                    && blueprintMatches)
                {
                    gridMatch = candidate;
                    continue;
                }

                if (hasUniqueId && delta.UniqueId > 0 && localUniqueId == delta.UniqueId && blueprintMatches)
                {
                    uniqueBlueprintMatch = candidate;
                    uniqueBlueprintDetail = "matched=uniqueId+blueprint scanned="
                        + scanned.ToString(CultureInfo.InvariantCulture)
                        + " uniqueId="
                        + localUniqueId.ToString(CultureInfo.InvariantCulture)
                        + " blueprintId="
                        + localBlueprintId;
                }
                else if (hasUniqueId && delta.UniqueId > 0 && localUniqueId == delta.UniqueId)
                {
                    unsafeUniqueMatch = candidate;
                    unsafeUniqueDetail = "unique-id-blueprint-mismatch scanned="
                        + scanned.ToString(CultureInfo.InvariantCulture)
                        + " uniqueId="
                        + localUniqueId.ToString(CultureInfo.InvariantCulture)
                        + " localBlueprintId="
                        + localBlueprintId
                        + " expectedBlueprintId="
                        + delta.BlueprintId;
                }
            }

            if (gridMatch != null)
            {
                pile = gridMatch;
                RegisterReplicationHostIdentity(delta.UniqueId, pile, "resource-pile-grid-fallback");
                RegisterReplicationResourcePileLocation(locationKey, pile, "resource-pile-grid-fallback");
                detail = "matched=grid scanned="
                    + scanned.ToString(CultureInfo.InvariantCulture)
                    + " expectedUniqueId="
                    + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                    + " blueprintId="
                    + delta.BlueprintId;
                return true;
            }

            if (uniqueBlueprintMatch != null)
            {
                pile = uniqueBlueprintMatch;
                RegisterReplicationHostIdentity(delta.UniqueId, pile, "resource-pile-unique-fallback");
                RegisterReplicationResourcePileLocation(locationKey, pile, "resource-pile-unique-fallback");
                detail = uniqueBlueprintDetail;
                return true;
            }

            if (unsafeUniqueMatch != null)
            {
                detail = "not-found unsafe-unique-skipped "
                    + unsafeUniqueDetail
                    + " expectedGrid=Vec3Int("
                    + delta.GridX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + delta.GridY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + delta.GridZ.ToString(CultureInfo.InvariantCulture)
                    + ") "
                    + managerDetail;
                return false;
            }

            detail = "not-found scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " expectedUniqueId="
                + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                + " blueprintId="
                + delta.BlueprintId
                + " grid=Vec3Int("
                + delta.GridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridZ.ToString(CultureInfo.InvariantCulture)
                + ") "
                + managerDetail;
            return false;
        }

        private static bool TryFindRecentReplicationResourcePile(object manager, ReplicationWorldObjectDelta delta, int maxCandidates, out object? pile, out string detail)
        {
            pile = null;
            if (!TryReadInstanceMemberValue(manager, "SpawnedPileInstances", out var spawned)
                || spawned == null)
            {
                detail = "recent-spawned-pile-list-missing";
                return false;
            }

            if (!(spawned is System.Collections.IList list))
            {
                detail = "recent-spawned-pile-list-not-indexable type=" + spawned.GetType().FullName;
                return false;
            }

            var scanned = 0;
            for (var i = list.Count - 1; i >= 0 && scanned < maxCandidates; i--)
            {
                var candidate = list[i];
                if (candidate == null)
                {
                    continue;
                }

                scanned++;
                if (!TryReadReplicationWorldObjectGridPosition(candidate, out var x, out var y, out var z)
                    || x != delta.GridX
                    || y != delta.GridY
                    || z != delta.GridZ)
                {
                    continue;
                }

                TryReadReplicationWorldObjectStringMember(candidate, "BlueprintId", "blueprintId", out var localBlueprintId);
                if (!string.IsNullOrEmpty(delta.BlueprintId)
                    && !string.IsNullOrEmpty(localBlueprintId)
                    && !string.Equals(localBlueprintId, delta.BlueprintId, StringComparison.Ordinal))
                {
                    continue;
                }

                pile = candidate;
                detail = "matched=recent-grid scanned="
                    + scanned.ToString(CultureInfo.InvariantCulture)
                    + " expectedUniqueId="
                    + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                    + " blueprintId="
                    + delta.BlueprintId;
                return true;
            }

            detail = "not-found-recent scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " expectedUniqueId="
                + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                + " blueprintId="
                + delta.BlueprintId
                + " grid=Vec3Int("
                + delta.GridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridZ.ToString(CultureInfo.InvariantCulture)
                + ")";
            return false;
        }

        private static void ProcessReplicationResourcePileLocationIndex()
        {
            if (replicationConfigHostMode
                || !replicationRemoteHelloReceived
                || replicationResourcePileLocationIndexComplete
                || Time.realtimeSinceStartup < replicationNextResourcePileLocationIndexRealtime)
            {
                return;
            }

            replicationNextResourcePileLocationIndexRealtime = Time.realtimeSinceStartup + ReplicationResourcePileLocationIndexIntervalSeconds;
            if (!TryGetReplicationResourcePileManager(out var manager, out var managerDetail) || manager == null)
            {
                replicationLastHostIdentitySummary = "pile-location-index-waiting " + managerDetail;
                return;
            }

            if (!TryReadInstanceMemberValue(manager, "SpawnedPileInstances", out var spawned)
                || spawned == null)
            {
                replicationLastHostIdentitySummary = "pile-location-index-waiting spawned-pile-list-missing " + managerDetail;
                return;
            }

            if (!(spawned is System.Collections.IList list))
            {
                replicationResourcePileLocationIndexComplete = true;
                replicationLastHostIdentitySummary = "pile-location-index-skipped list-not-indexable type=" + spawned.GetType().FullName;
                return;
            }

            var processed = 0;
            var registered = 0;
            while (replicationResourcePileLocationIndexNextIndex < list.Count
                && processed < ReplicationResourcePileLocationIndexBudgetPerFrame)
            {
                var candidate = list[replicationResourcePileLocationIndexNextIndex++];
                processed++;
                if (candidate == null
                    || !TryReadReplicationWorldObjectGridPosition(candidate, out var x, out var y, out var z)
                    || !TryReadReplicationWorldObjectStringMember(candidate, "BlueprintId", "blueprintId", out var blueprintId)
                    || string.IsNullOrWhiteSpace(blueprintId))
                {
                    continue;
                }

                RegisterReplicationResourcePileLocation(
                    FormatReplicationWorldObjectDeltaLocationKey(blueprintId, x, y, z),
                    candidate,
                    "resource-pile-initial-index");
                registered++;
            }

            if (replicationResourcePileLocationIndexNextIndex >= list.Count)
            {
                replicationResourcePileLocationIndexComplete = true;
            }

            replicationLastHostIdentitySummary = "pile-location-index "
                + (replicationResourcePileLocationIndexComplete ? "complete" : "progress")
                + " next="
                + replicationResourcePileLocationIndexNextIndex.ToString(CultureInfo.InvariantCulture)
                + " total="
                + list.Count.ToString(CultureInfo.InvariantCulture)
                + " processed="
                + processed.ToString(CultureInfo.InvariantCulture)
                + " registered="
                + registered.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryGetReplicationPileAmountAt(string blueprintId, int gridX, int gridY, int gridZ, out int amount, out string detail)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(blueprintId)
                || (gridX == 0 && gridY == 0 && gridZ == 0))
            {
                detail = "pile-state-input-missing";
                return false;
            }

            var lookupDelta = new ReplicationWorldObjectDelta(
                0,
                0f,
                "ResourcePileStateLookup",
                0,
                blueprintId,
                gridX,
                gridY,
                gridZ,
                string.Empty);
            if (!TryFindReplicationResourcePile(lookupDelta, out var pile, out var lookupDetail) || pile == null)
            {
                detail = "pile-state-lookup-failed " + lookupDetail;
                return false;
            }

            if (!TryGetReplicationPileStoredResource(pile, out var storedResource, out var storedDetail) || storedResource == null)
            {
                detail = "pile-state-stored-resource-missing " + storedDetail + " lookup=" + lookupDetail;
                return false;
            }

            if (!TryReadReplicationWorldObjectIntMember(storedResource, "Amount", "amount", out amount))
            {
                detail = "pile-state-amount-read-failed " + storedDetail + " lookup=" + lookupDetail;
                return false;
            }

            detail = "lookup=" + lookupDetail + " " + storedDetail;
            return amount > 0;
        }

        private static bool TryCollectReplicationResourcePileStates(out List<ReplicationResourcePileState> states, out string detail)
        {
            states = new List<ReplicationResourcePileState>();
            if (!TryGetReplicationResourcePileManager(out var manager, out var managerDetail) || manager == null)
            {
                detail = "manager-missing " + managerDetail;
                return false;
            }

            if (!TryReadInstanceMemberValue(manager, "SpawnedPileInstances", out var spawned)
                || spawned == null
                || !(spawned is System.Collections.IEnumerable enumerable))
            {
                detail = "spawned-pile-list-missing " + managerDetail;
                return false;
            }

            var scanned = 0;
            var skipped = 0;
            foreach (var candidate in enumerable)
            {
                if (candidate == null)
                {
                    continue;
                }

                scanned++;
                if (states.Count >= ReplicationResourcePileStateSnapshotMaxPiles)
                {
                    skipped++;
                    continue;
                }

                if (!TryReadReplicationWorldObjectGridPosition(candidate, out var gridX, out var gridY, out var gridZ)
                    || !TryGetReplicationPileStoredResource(candidate, out var storedResource, out _)
                    || storedResource == null
                    || !TryReadReplicationResourcePileBlueprintId(candidate, storedResource, out var blueprintId, out _)
                    || !TryReadReplicationWorldObjectIntMember(storedResource, "Amount", "amount", out var amount)
                    || amount <= 0)
                {
                    skipped++;
                    continue;
                }

                var uniqueId = TryReadReplicationWorldObjectLongMember(candidate, "UniqueId", "uniqueId", out var parsedUniqueId)
                    ? parsedUniqueId
                    : 0L;
                states.Add(new ReplicationResourcePileState(uniqueId, blueprintId, gridX, gridY, gridZ, amount));
            }

            detail = "scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " skipped="
                + skipped.ToString(CultureInfo.InvariantCulture)
                + " cap="
                + ReplicationResourcePileStateSnapshotMaxPiles.ToString(CultureInfo.InvariantCulture)
                + " "
                + managerDetail;
            if (scanned == 0 && replicationLastCollectedEntities <= 0)
            {
                detail = "not-ready " + detail;
                return false;
            }

            return true;
        }

        private static void SortReplicationResourcePileStates(List<ReplicationResourcePileState> states)
        {
            states.Sort(CompareReplicationResourcePileStates);
        }

        private static int CompareReplicationResourcePileStates(ReplicationResourcePileState left, ReplicationResourcePileState right)
        {
            var compare = string.Compare(left.BlueprintId, right.BlueprintId, StringComparison.Ordinal);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.GridX.CompareTo(right.GridX);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.GridY.CompareTo(right.GridY);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.GridZ.CompareTo(right.GridZ);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.Amount.CompareTo(right.Amount);
            if (compare != 0)
            {
                return compare;
            }

            return left.UniqueId.CompareTo(right.UniqueId);
        }

        private static ulong ComputeReplicationResourcePileStateSnapshotSignature(List<ReplicationResourcePileState> states)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                AddReplicationSignatureString(ref hash, "ResourcePileStateSnapshot");
                AddReplicationSignatureInt(ref hash, states.Count);
                for (var i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    AddReplicationSignatureLong(ref hash, state.UniqueId);
                    AddReplicationSignatureString(ref hash, state.BlueprintId);
                    AddReplicationSignatureInt(ref hash, state.GridX);
                    AddReplicationSignatureInt(ref hash, state.GridY);
                    AddReplicationSignatureInt(ref hash, state.GridZ);
                    AddReplicationSignatureInt(ref hash, state.Amount);
                }

                return hash;
            }
        }

        private static bool TryCollectReplicationBuildingStates(out List<ReplicationBuildingState> states, out string detail)
        {
            states = new List<ReplicationBuildingState>();
            var viewComponentType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingViewComponent");
            if (viewComponentType == null)
            {
                detail = "building-view-component-type-missing";
                return false;
            }

            var candidates = UnityEngine.Object.FindObjectsOfType(viewComponentType);
            var scanned = 0;
            var skipped = 0;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null
                    || candidate is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy)
                {
                    continue;
                }

                scanned++;
                if (states.Count >= ReplicationBuildingStateSnapshotMaxBuildings)
                {
                    skipped++;
                    continue;
                }

                if (!TryResolveReplicationBuildingCandidateInstance(candidate, out var buildingInstance, out _)
                    || buildingInstance == null
                    || !TryResolveReplicationBuildingCandidateGrid(candidate, out var gridX, out var gridY, out var gridZ)
                    || !TryResolveReplicationBuildingCandidateBlueprintId(candidate, out var blueprintId)
                    || string.IsNullOrWhiteSpace(blueprintId))
                {
                    skipped++;
                    continue;
                }

                var uniqueId = TryReadReplicationWorldObjectLongMember(buildingInstance, "UniqueId", "uniqueId", out var parsedUniqueId)
                    ? parsedUniqueId
                    : 0L;
                var phase = TryReadReplicationBuildingPhase(buildingInstance, out var parsedPhase)
                    ? parsedPhase
                    : "Unknown";
                var remainingTimeMs = TryReadReplicationBuildingRemainingTimeMs(buildingInstance, out var parsedRemainingTimeMs)
                    ? parsedRemainingTimeMs
                    : 0;
                var underConstruction = TryReadReplicationBoolMember(buildingInstance, "IsUnderConstruction", "isUnderConstruction", out var parsedUnderConstruction)
                    && parsedUnderConstruction;
                var forbidden = TryReadReplicationBoolMember(buildingInstance, "IsForbidden", "isForbidden", out var parsedForbidden)
                    && parsedForbidden;
                var markedForDestruction = TryReadReplicationBoolMember(buildingInstance, "MarkedForDestruction", "markedForDestruction", out var parsedMarked)
                    && parsedMarked;

                states.Add(new ReplicationBuildingState(
                    uniqueId,
                    blueprintId,
                    gridX,
                    gridY,
                    gridZ,
                    phase,
                    remainingTimeMs,
                    underConstruction,
                    forbidden,
                    markedForDestruction));
            }

            detail = "scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " skipped="
                + skipped.ToString(CultureInfo.InvariantCulture)
                + " cap="
                + ReplicationBuildingStateSnapshotMaxBuildings.ToString(CultureInfo.InvariantCulture);
            return scanned > 0 || states.Count > 0;
        }

        private static void SortReplicationBuildingStates(List<ReplicationBuildingState> states)
        {
            states.Sort(CompareReplicationBuildingStates);
        }

        private static int CompareReplicationBuildingStates(ReplicationBuildingState left, ReplicationBuildingState right)
        {
            var compare = string.Compare(left.BlueprintId, right.BlueprintId, StringComparison.Ordinal);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.GridX.CompareTo(right.GridX);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.GridY.CompareTo(right.GridY);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.GridZ.CompareTo(right.GridZ);
            if (compare != 0)
            {
                return compare;
            }

            compare = string.Compare(left.ConstructionPhase, right.ConstructionPhase, StringComparison.Ordinal);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.RemainingTimeMs.CompareTo(right.RemainingTimeMs);
            if (compare != 0)
            {
                return compare;
            }

            return left.UniqueId.CompareTo(right.UniqueId);
        }

        private static ulong ComputeReplicationBuildingStateSnapshotSignature(List<ReplicationBuildingState> states)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                AddReplicationSignatureString(ref hash, "BuildingStateSnapshot");
                AddReplicationSignatureInt(ref hash, states.Count);
                for (var i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    AddReplicationSignatureLong(ref hash, state.UniqueId);
                    AddReplicationSignatureString(ref hash, state.BlueprintId);
                    AddReplicationSignatureInt(ref hash, state.GridX);
                    AddReplicationSignatureInt(ref hash, state.GridY);
                    AddReplicationSignatureInt(ref hash, state.GridZ);
                    AddReplicationSignatureString(ref hash, state.ConstructionPhase);
                    AddReplicationSignatureInt(ref hash, state.RemainingTimeMs);
                    AddReplicationSignatureInt(ref hash, state.IsUnderConstruction ? 1 : 0);
                    AddReplicationSignatureInt(ref hash, state.IsForbidden ? 1 : 0);
                    AddReplicationSignatureInt(ref hash, state.MarkedForDestruction ? 1 : 0);
                }

                return hash;
            }
        }

        private static void AddReplicationSignatureString(ref ulong hash, string value)
        {
            AddReplicationSignatureInt(ref hash, value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                AddReplicationSignatureInt(ref hash, value[i]);
            }
        }

        private static void AddReplicationSignatureInt(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
                hash ^= (uint)(value >> 16);
                hash *= 1099511628211UL;
            }
        }

        private static void AddReplicationSignatureLong(ref ulong hash, long value)
        {
            unchecked
            {
                AddReplicationSignatureInt(ref hash, (int)value);
                AddReplicationSignatureInt(ref hash, (int)(value >> 32));
            }
        }

        private static bool TryReadReplicationResourcePileBlueprintId(
            object pile,
            object? storedResource,
            out string blueprintId,
            out string detail)
        {
            if (TryReadReplicationWorldObjectStringMember(pile, "BlueprintId", "blueprintId", out blueprintId)
                && !string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "blueprint-source=pile";
                return true;
            }

            if (storedResource != null
                && TryReadReplicationWorldObjectStringMember(storedResource, "BlueprintId", "blueprintId", out blueprintId)
                && !string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "blueprint-source=stored-resource";
                return true;
            }

            detail = "blueprint-missing";
            blueprintId = string.Empty;
            return false;
        }

        private static bool TryCollectReplicationAgentCarryStates(out List<ReplicationAgentCarryState> states, out string detail)
        {
            states = new List<ReplicationAgentCarryState>();
            var views = FindReplicationAnimatedAgentViews();
            var scanned = 0;
            var skipped = 0;
            var carried = 0;
            var empty = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryClassifyReplicationView(view, out _))
                {
                    continue;
                }

                scanned++;
                if (states.Count >= ReplicationAgentCarryStateSnapshotMaxAgents)
                {
                    skipped++;
                    continue;
                }

                if (!TryGetReplicationViewEntityId(view, out var entityId) || string.IsNullOrWhiteSpace(entityId))
                {
                    skipped++;
                    continue;
                }

                var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId)
                    ? parsedId
                    : 0L;
                if (ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var blueprintId)
                    && !string.IsNullOrWhiteSpace(blueprintId)
                    && ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var amount)
                    && amount > 0)
                {
                    states.Add(new ReplicationAgentCarryState(entityId, uniqueId, blueprintId, amount));
                    carried++;
                }
                else
                {
                    states.Add(new ReplicationAgentCarryState(entityId, uniqueId, string.Empty, 0));
                    empty++;
                }
            }

            detail = "scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " carried="
                + carried.ToString(CultureInfo.InvariantCulture)
                + " empty="
                + empty.ToString(CultureInfo.InvariantCulture)
                + " skipped="
                + skipped.ToString(CultureInfo.InvariantCulture)
                + " cap="
                + ReplicationAgentCarryStateSnapshotMaxAgents.ToString(CultureInfo.InvariantCulture);
            return scanned > 0;
        }

        private static bool TryCollectReplicationAgentCharacterStates(out List<ReplicationAgentCharacterState> states, out string detail)
        {
            states = new List<ReplicationAgentCharacterState>();
            var views = FindReplicationAnimatedAgentViews();
            var scanned = 0;
            var skipped = 0;
            var workers = 0;
            var animals = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryClassifyReplicationView(view, out var kind))
                {
                    continue;
                }

                scanned++;
                if (states.Count >= ReplicationAgentCharacterStateSnapshotMaxAgents)
                {
                    skipped++;
                    continue;
                }

                if (!TryGetReplicationViewEntityId(view, out var entityId) || string.IsNullOrWhiteSpace(entityId))
                {
                    skipped++;
                    continue;
                }

                if (!TryResolveReplicationAgentOwnerFromView(view, out var owner, out _))
                {
                    skipped++;
                    continue;
                }

                var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId)
                    ? parsedId
                    : 0L;
                var agentOwner = owner!;
                var behaviourOwner = TryResolveReplicationBehaviourOwner(agentOwner, out var resolvedBehaviour)
                    ? resolvedBehaviour
                    : agentOwner;
                var statsOwner = TryResolveReplicationStatsOwner(agentOwner, behaviourOwner);
                var skillsOwner = TryResolveReplicationSkillsOwner(agentOwner, behaviourOwner);
                var state = BuildReplicationAgentCharacterState(entityId, uniqueId, kind, agentOwner, behaviourOwner, statsOwner, skillsOwner);
                states.Add(state);

                if (string.Equals(kind, "worker", StringComparison.OrdinalIgnoreCase))
                {
                    workers++;
                }
                else if (string.Equals(kind, "animal", StringComparison.OrdinalIgnoreCase))
                {
                    animals++;
                }
            }

            detail = "scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " workers="
                + workers.ToString(CultureInfo.InvariantCulture)
                + " animals="
                + animals.ToString(CultureInfo.InvariantCulture)
                + " skipped="
                + skipped.ToString(CultureInfo.InvariantCulture)
                + " cap="
                + ReplicationAgentCharacterStateSnapshotMaxAgents.ToString(CultureInfo.InvariantCulture);
            return scanned > 0;
        }

        private static ReplicationAgentCharacterState BuildReplicationAgentCharacterState(
            string entityId,
            long uniqueId,
            string kind,
            object owner,
            object? behaviourOwner,
            object? statsOwner,
            object? skillsOwner)
        {
            var hasDied = TryReadReplicationBooleanState(owner, "HasDied", out var deadValue) && deadValue;
            var hasFainted = TryReadReplicationBooleanState(owner, "HasFainted", out var faintedValue) && faintedValue;
            var isSleeping = TryReadReplicationBooleanState(owner, "IsSleeping", out var sleepingValue) && sleepingValue;
            var isWounded = TryReadReplicationBooleanState(owner, "IsWounded", out var woundedValue) && woundedValue;
            var isBleeding = TryReadReplicationBooleanState(owner, "IsBleeding", out var bleedingValue) && bleedingValue;
            var isBeingCarried = TryReadReplicationBooleanState(owner, "IsBeingCarried", out var carriedValue) && carriedValue;
            var isIdle = behaviourOwner != null && TryReadReplicationBooleanState(behaviourOwner, "IsIdle", out var idleValue) && idleValue;
            var isResting = behaviourOwner != null && TryReadReplicationBooleanState(behaviourOwner, "IsResting", out var restingValue) && restingValue;
            var isDrafting = behaviourOwner != null && TryReadReplicationBooleanState(behaviourOwner, "IsDrafting", out var draftingValue) && draftingValue;
            var combatMode = behaviourOwner != null && TryReadReplicationStringState(behaviourOwner, "CombatMode", out var parsedCombatMode)
                ? parsedCombatMode
                : string.Empty;
            var statsSignature = ComputeReplicationStatsCollectionSignature(statsOwner, "Stats");
            var attributesSignature = ComputeReplicationStatsCollectionSignature(statsOwner, "Attributes");
            var skillsSignature = ComputeReplicationSkillsSignature(skillsOwner);
            var hungerCurrent = 0f;
            var sleepCurrent = 0f;
            var hasHunger = replicationConfigNeedsReplication && TryReadReplicationNeedsStat(statsOwner, "Hunger", out hungerCurrent);
            var hasSleep = replicationConfigNeedsReplication && TryReadReplicationNeedsStat(statsOwner, "Sleep", out sleepCurrent);
            var signature = ComputeReplicationAgentCharacterStateSignature(
                kind,
                hasDied,
                hasFainted,
                isSleeping,
                isWounded,
                isBleeding,
                isBeingCarried,
                isIdle,
                isResting,
                isDrafting,
                combatMode,
                hasHunger,
                hasHunger ? hungerCurrent : 0f,
                hasSleep,
                hasSleep ? sleepCurrent : 0f,
                statsSignature,
                attributesSignature,
                skillsSignature);

            return new ReplicationAgentCharacterState(
                entityId,
                uniqueId,
                kind,
                hasDied,
                hasFainted,
                isSleeping,
                isWounded,
                isBleeding,
                isBeingCarried,
                isIdle,
                isResting,
                isDrafting,
                combatMode,
                hasHunger,
                hasHunger ? hungerCurrent : 0f,
                hasSleep,
                hasSleep ? sleepCurrent : 0f,
                statsSignature,
                attributesSignature,
                skillsSignature,
                signature);
        }

        private static bool TryResolveReplicationAgentOwnerFromView(object view, out object? owner, out string detail)
        {
            owner = null;
            if (TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out owner) && owner != null)
            {
                detail = "owner=GetAgentOwner";
                return true;
            }

            if (TryInvokeReplicationObjectMethod(view, "GetAgent", out var agent) && agent != null
                && TryInvokeReplicationObjectMethod(agent, "get_AgentOwner", out owner) && owner != null)
            {
                detail = "owner=GetAgent.AgentOwner";
                return true;
            }

            detail = "owner-missing";
            return false;
        }

        private static bool TryResolveReplicationBehaviourOwner(object? owner, out object? behaviourOwner)
        {
            behaviourOwner = null;
            if (owner == null)
            {
                return false;
            }

            var names = new[]
            {
                "ActiveBehaviour",
                "activeBehaviour",
                "WorkerBehaviour",
                "workerBehaviour",
                "Behaviour",
                "behaviour"
            };

            for (var i = 0; i < names.Length; i++)
            {
                if ((TryReadInstanceMemberValue(owner, names[i], out behaviourOwner) && behaviourOwner != null)
                    || (TryInvokeReplicationObjectMethod(owner, "get_" + names[i], out behaviourOwner) && behaviourOwner != null))
                {
                    return true;
                }
            }

            if (owner.GetType().FullName?.IndexOf("WorkerBehaviour", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                behaviourOwner = owner;
                return true;
            }

            return false;
        }

        private static object? TryResolveReplicationStatsOwner(object? owner, object? behaviourOwner)
        {
            if (TryReadReplicationObjectState(owner, "Stats", out var stats) && stats != null)
            {
                return stats;
            }

            if (TryReadReplicationObjectState(behaviourOwner, "Stats", out stats) && stats != null)
            {
                return stats;
            }

            return null;
        }

        private static object? TryResolveReplicationSkillsOwner(object? owner, object? behaviourOwner)
        {
            if (TryReadReplicationObjectState(owner, "Skills", out var skills) && skills != null)
            {
                return skills;
            }

            if (TryReadReplicationObjectState(behaviourOwner, "Skills", out skills) && skills != null)
            {
                return skills;
            }

            return null;
        }

        private static bool TryReadReplicationObjectState(object? owner, string memberName, out object? value)
        {
            value = null;
            return owner != null
                && ((TryReadInstanceMemberValue(owner, memberName, out value) && value != null)
                    || (TryReadInstanceMemberValue(owner, char.ToLowerInvariant(memberName[0]) + memberName.Substring(1), out value) && value != null)
                    || (TryInvokeReplicationObjectMethod(owner, "get_" + memberName, out value) && value != null));
        }

        private static bool TryReadReplicationBooleanState(object owner, string memberName, out bool value)
        {
            value = false;
            if (!TryReadReplicationObjectState(owner, memberName, out var raw) || raw == null)
            {
                return false;
            }

            try
            {
                value = Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadReplicationStringState(object owner, string memberName, out string value)
        {
            value = string.Empty;
            if (!TryReadReplicationObjectState(owner, memberName, out var raw) || raw == null)
            {
                return false;
            }

            value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            return value.Length > 0;
        }

        private static ulong ComputeReplicationStatsCollectionSignature(object? statsOwner, string collectionName)
        {
            if (!TryReadReplicationObjectState(statsOwner, collectionName, out var collection) || collection == null)
            {
                return 0UL;
            }

            var values = new List<string>();
            foreach (var rawItem in EnumerateReplicationCollection(collection))
            {
                var item = UnwrapReplicationCollectionItemValue(rawItem);
                if (item == null)
                {
                    continue;
                }

                values.Add(FormatReplicationCharacterStateStatsItem(item));
                if (values.Count >= ReplicationAgentCharacterStateCollectionMaxItems)
                {
                    break;
                }
            }

            values.Sort(StringComparer.Ordinal);
            return ComputeReplicationStringListSignature(collectionName, values);
        }

        private static ulong ComputeReplicationSkillsSignature(object? skillsOwner)
        {
            if (skillsOwner == null)
            {
                return 0UL;
            }

            object? collection = skillsOwner;
            if (TryReadReplicationObjectState(skillsOwner, "Skills", out var nestedSkills) && nestedSkills != null)
            {
                collection = nestedSkills;
            }

            var values = new List<string>();
            foreach (var rawItem in EnumerateReplicationCollection(collection))
            {
                var item = UnwrapReplicationCollectionItemValue(rawItem);
                if (item == null)
                {
                    continue;
                }

                values.Add(FormatReplicationCharacterStateSkillItem(item));
                if (values.Count >= ReplicationAgentCharacterStateCollectionMaxItems)
                {
                    break;
                }
            }

            values.Sort(StringComparer.Ordinal);
            return ComputeReplicationStringListSignature("Skills", values);
        }

        private static IEnumerable<object> EnumerateReplicationCollection(object collection)
        {
            if (collection is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }

        private static object? UnwrapReplicationCollectionItemValue(object item)
        {
            var type = item.GetType();
            if (type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)
                && TryReadInstanceMemberValue(item, "Value", out var value)
                && value != null)
            {
                return value;
            }

            return item;
        }

        private static string FormatReplicationCharacterStateStatsItem(object item)
        {
            var builder = new StringBuilder();
            AppendReplicationCharacterStateMember(builder, item, "Type");
            AppendReplicationCharacterStateMember(builder, item, "Current");
            AppendReplicationCharacterStateMember(builder, item, "Target");
            AppendReplicationCharacterStateMember(builder, item, "Min");
            AppendReplicationCharacterStateMember(builder, item, "Max");
            AppendReplicationCharacterStateMember(builder, item, "Value");
            AppendReplicationCharacterStateMember(builder, item, "BaseValue");
            AppendReplicationCharacterStateMember(builder, item, "Multiplier");
            AppendReplicationCharacterStateMember(builder, item, "IsDisabled");
            AppendReplicationCharacterStateMember(builder, item, "IsLocked");
            return builder.ToString();
        }

        private static string FormatReplicationCharacterStateSkillItem(object item)
        {
            var builder = new StringBuilder();
            AppendReplicationCharacterStateMember(builder, item, "Type");
            AppendReplicationCharacterStateMember(builder, item, "SkillType");
            AppendReplicationCharacterStateMember(builder, item, "Id");
            AppendReplicationCharacterStateMember(builder, item, "ID");
            AppendReplicationCharacterStateMember(builder, item, "Level");
            AppendReplicationCharacterStateMember(builder, item, "Experience");
            return builder.ToString();
        }

        private static void AppendReplicationCharacterStateMember(StringBuilder builder, object owner, string memberName)
        {
            if (!TryReadReplicationObjectState(owner, memberName, out var value) || value == null)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(",");
            }

            builder.Append(memberName)
                .Append("=")
                .Append(FormatReplicationWorldObjectDetailToken(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        }

        private static ulong ComputeReplicationAgentCharacterStateSignature(
            string kind,
            bool hasDied,
            bool hasFainted,
            bool isSleeping,
            bool isWounded,
            bool isBleeding,
            bool isBeingCarried,
            bool isIdle,
            bool isResting,
            bool isDrafting,
            string combatMode,
            bool hasHunger,
            float hungerCurrent,
            bool hasSleep,
            float sleepCurrent,
            ulong statsSignature,
            ulong attributesSignature,
            ulong skillsSignature)
        {
            var values = new List<string>
            {
                "kind=" + kind,
                "dead=" + FormatReplicationBool(hasDied),
                "fainted=" + FormatReplicationBool(hasFainted),
                "sleeping=" + FormatReplicationBool(isSleeping),
                "wounded=" + FormatReplicationBool(isWounded),
                "bleeding=" + FormatReplicationBool(isBleeding),
                "beingCarried=" + FormatReplicationBool(isBeingCarried),
                "idle=" + FormatReplicationBool(isIdle),
                "resting=" + FormatReplicationBool(isResting),
                "drafting=" + FormatReplicationBool(isDrafting),
                "combat=" + combatMode,
                "stats=0x" + statsSignature.ToString("X16", CultureInfo.InvariantCulture),
                "attrs=0x" + attributesSignature.ToString("X16", CultureInfo.InvariantCulture),
                "skills=0x" + skillsSignature.ToString("X16", CultureInfo.InvariantCulture)
            };
            if (replicationConfigNeedsReplication)
            {
                values.Add("hasHunger=" + FormatReplicationBool(hasHunger));
                values.Add("hunger=" + hungerCurrent.ToString("0.###", CultureInfo.InvariantCulture));
                values.Add("hasSleep=" + FormatReplicationBool(hasSleep));
                values.Add("sleep=" + sleepCurrent.ToString("0.###", CultureInfo.InvariantCulture));
            }
            return ComputeReplicationStringListSignature("AgentCharacterState", values);
        }

        private static ulong ComputeReplicationStringListSignature(string prefix, List<string> values)
        {
            var hash = ReplicationCharacterStateFnvOffset;
            AddReplicationCharacterStateHashString(ref hash, prefix);
            for (var i = 0; i < values.Count; i++)
            {
                AddReplicationCharacterStateHashString(ref hash, values[i]);
            }

            return hash;
        }

        private static void AddReplicationCharacterStateHashString(ref ulong hash, string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= ReplicationCharacterStateFnvPrime;
            }

            hash ^= 0xff;
            hash *= ReplicationCharacterStateFnvPrime;
        }

        private static string RecordReplicationResourcePileStateSnapshotKey(ReplicationWorldObjectDelta delta)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "snapshotId", out var snapshotIdValue) || snapshotIdValue <= 0)
            {
                return "snapshot=none";
            }

            var expectedCount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "count", out var parsedCount)
                ? Math.Max(0, parsedCount)
                : 0;
            var key = FormatReplicationWorldObjectDeltaLocationKey(delta);
            ReplicationResourcePileStateSnapshotContext? finalizeContext = null;
            var snapshotId = snapshotIdValue;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationResourcePileStateSnapshotContexts.TryGetValue(snapshotId, out var context))
                {
                    context = new ReplicationResourcePileStateSnapshotContext(expectedCount);
                    ReplicationResourcePileStateSnapshotContexts[snapshotId] = context;
                }

                if (expectedCount > 0)
                {
                    context.ExpectedCount = expectedCount;
                }

                if (TryReadReplicationWorldObjectDetailHex(delta.Detail, "signature", out var hostSignature))
                {
                    context.HostSignatureValid = true;
                    context.HostSignature = hostSignature;
                }

                context.SeenKeys.Add(key);
                var amount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var parsedAmount)
                    ? Math.Max(0, parsedAmount)
                    : 0;
                context.States[key] = new ReplicationResourcePileState(
                    delta.UniqueId,
                    delta.BlueprintId,
                    delta.GridX,
                    delta.GridY,
                    delta.GridZ,
                    amount);
                if (context.EndReceived && context.SeenKeys.Count >= context.ExpectedCount)
                {
                    finalizeContext = context;
                }
            }

            if (finalizeContext != null && TryFinalizeReplicationResourcePileStateSnapshot(snapshotId, finalizeContext, out var finalizeDetail))
            {
                return "snapshotId="
                    + snapshotId.ToString(CultureInfo.InvariantCulture)
                    + " seen="
                    + finalizeContext.SeenKeys.Count.ToString(CultureInfo.InvariantCulture)
                    + " "
                    + finalizeDetail;
            }

            return "snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " key="
                + key;
        }

        private static bool TryFinalizeReplicationResourcePileStateSnapshot(long snapshotId, ReplicationResourcePileStateSnapshotContext context, out string detail)
        {
            var states = new List<ReplicationResourcePileState>(context.States.Values);
            if (states.Count > ReplicationResourcePileStateSnapshotApplyMaxPiles)
            {
                states.RemoveRange(ReplicationResourcePileStateSnapshotApplyMaxPiles, states.Count - ReplicationResourcePileStateSnapshotApplyMaxPiles);
            }

            SortReplicationResourcePileStates(states);
            var localSignature = ComputeReplicationResourcePileStateSnapshotSignature(states);
            RecordReplicationDriftSignature(
                "resource-piles",
                snapshotId,
                context.ExpectedCount,
                context.SeenKeys.Count,
                context.HostSignatureValid,
                context.HostSignature,
                localSignature,
                "snapshot-finalize");

            lock (ReplicationWorldObjectDeltaLock)
            {
                while (ReplicationPendingResourcePileStateSnapshotApplies.Count >= ReplicationResourcePileStateSnapshotApplyMaxQueuedSnapshots)
                {
                    ReplicationPendingResourcePileStateSnapshotApplies.Dequeue();
                }

                ReplicationPendingResourcePileStateSnapshotApplies.Enqueue(new PendingReplicationResourcePileStateSnapshotApply(snapshotId, states));
                ReplicationResourcePileStateSnapshotContexts.Remove(snapshotId);
                CleanupReplicationResourcePileStateSnapshotContexts(snapshotId);
            }

            detail = "ok pile-state-snapshot-queued snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " expected="
                + context.ExpectedCount.ToString(CultureInfo.InvariantCulture)
                + " seen="
                + context.SeenKeys.Count.ToString(CultureInfo.InvariantCulture)
                + " queued="
                + states.Count.ToString(CultureInfo.InvariantCulture)
                + " hostSignature="
                + (context.HostSignatureValid ? "0x" + context.HostSignature.ToString("x16", CultureInfo.InvariantCulture) : "<missing>")
                + " localSignature=0x"
                + localSignature.ToString("x16", CultureInfo.InvariantCulture);
            return true;
        }

        private static void ProcessPendingReplicationResourcePileStateSnapshotApplies()
        {
            if (replicationConfigHostMode || IsReplicationWorldObjectDeltaModeOff())
            {
                return;
            }

            var processed = 0;
            var changed = 0;
            while (processed < ReplicationResourcePileStateSnapshotApplyFrameMaxPiles
                && changed < ReplicationResourcePileStateSnapshotApplyFrameMaxChanges)
            {
                PendingReplicationResourcePileStateSnapshotApply? pending;
                lock (ReplicationWorldObjectDeltaLock)
                {
                    if (ReplicationPendingResourcePileStateSnapshotApplies.Count == 0)
                    {
                        return;
                    }

                    pending = ReplicationPendingResourcePileStateSnapshotApplies.Peek();
                }

                if (pending.NextIndex >= pending.States.Count)
                {
                    FinishPendingReplicationResourcePileStateSnapshotApply(pending);
                    continue;
                }

                var state = pending.States[pending.NextIndex++];
                processed++;
                if (TryApplyReplicationResourcePileSnapshotStateAmount(state, pending.SnapshotId, out var applyDetail))
                {
                    pending.Synced++;
                    changed++;
                }
                else if (applyDetail.StartsWith("unchanged ", StringComparison.Ordinal))
                {
                    pending.Unchanged++;
                }
                else if (applyDetail.StartsWith("missing ", StringComparison.Ordinal))
                {
                    pending.Missing++;
                }
                else
                {
                    pending.Failed++;
                }

                if (pending.NextIndex >= pending.States.Count)
                {
                    FinishPendingReplicationResourcePileStateSnapshotApply(pending);
                }
            }
        }

        private static void FinishPendingReplicationResourcePileStateSnapshotApply(PendingReplicationResourcePileStateSnapshotApply pending)
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationPendingResourcePileStateSnapshotApplies.Count > 0
                    && ReferenceEquals(ReplicationPendingResourcePileStateSnapshotApplies.Peek(), pending))
                {
                    ReplicationPendingResourcePileStateSnapshotApplies.Dequeue();
                }
            }

            replicationLastWorldObjectDeltaSummary = "pile-state-snapshot-apply-complete snapshotId="
                + pending.SnapshotId.ToString(CultureInfo.InvariantCulture)
                + " total="
                + pending.States.Count.ToString(CultureInfo.InvariantCulture)
                + " synced="
                + pending.Synced.ToString(CultureInfo.InvariantCulture)
                + " unchanged="
                + pending.Unchanged.ToString(CultureInfo.InvariantCulture)
                + " missing="
                + pending.Missing.ToString(CultureInfo.InvariantCulture)
                + " failed="
                + pending.Failed.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryApplyReplicationResourcePileSnapshotStateAmount(
            ReplicationResourcePileState state,
            long snapshotId,
            out string detail)
        {
            var syncDelta = new ReplicationWorldObjectDelta(
                0,
                0f,
                "ResourcePileState",
                state.UniqueId,
                state.BlueprintId,
                state.GridX,
                state.GridY,
                state.GridZ,
                "amount=" + state.Amount.ToString(CultureInfo.InvariantCulture)
                    + " pileAmount=" + state.Amount.ToString(CultureInfo.InvariantCulture)
                    + " snapshotId=" + snapshotId.ToString(CultureInfo.InvariantCulture));

            if (!TryFindReplicationResourcePile(syncDelta, out var pile, out var lookupDetail) || pile == null)
            {
                detail = "missing " + lookupDetail;
                return false;
            }

            if (!TryGetReplicationPileStoredResource(pile, out var storedResource, out var storedDetail)
                || storedResource == null
                || !TryReadReplicationWorldObjectIntMember(storedResource, "Amount", "amount", out var localAmount))
            {
                detail = "read-failed " + storedDetail + " lookup=" + lookupDetail;
                return false;
            }

            if (localAmount == state.Amount)
            {
                detail = "unchanged amount=" + localAmount.ToString(CultureInfo.InvariantCulture) + " lookup=" + lookupDetail;
                return false;
            }

            if (!TryResolveReplicationResourceModel(state.BlueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = "resource-lookup-failed " + resourceDetail + " lookup=" + lookupDetail;
                return false;
            }

            if (!TryCreateReplicationResourceInstance(resource, state.Amount, out var resourceInstance, out var instanceDetail) || resourceInstance == null)
            {
                detail = "resource-instance-create-failed " + instanceDetail + " lookup=" + lookupDetail;
                return false;
            }

            if (!TrySetReplicationExistingResourcePileAmount(pile, resourceInstance, state.Amount, out var setDetail))
            {
                detail = "set-failed " + setDetail + " " + instanceDetail + " lookup=" + lookupDetail;
                return false;
            }

            detail = "synced " + setDetail + " lookup=" + lookupDetail;
            return true;
        }

        private static void CleanupReplicationResourcePileStateSnapshotContexts(long newestSnapshotId)
        {
            if (ReplicationResourcePileStateSnapshotContexts.Count <= ReplicationResourcePileStateSnapshotContextMaxCount)
            {
                return;
            }

            var expired = new List<long>();
            foreach (var pair in ReplicationResourcePileStateSnapshotContexts)
            {
                if (pair.Key + ReplicationResourcePileStateSnapshotContextMaxCount < newestSnapshotId)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ReplicationResourcePileStateSnapshotContexts.Remove(expired[i]);
            }
        }

        private static bool TryFindNearestReplicationResourcePileState(
            string blueprintId,
            int originX,
            int originY,
            int originZ,
            out int gridX,
            out int gridY,
            out int gridZ,
            out int amount,
            out string detail)
        {
            gridX = 0;
            gridY = 0;
            gridZ = 0;
            amount = 0;
            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "nearest-pile-input-missing";
                return false;
            }

            if (!TryGetReplicationResourcePileManager(out var manager, out var managerDetail) || manager == null)
            {
                detail = "manager-missing " + managerDetail;
                return false;
            }

            if (!TryReadInstanceMemberValue(manager, "SpawnedPileInstances", out var spawned)
                || spawned == null
                || !(spawned is System.Collections.IEnumerable enumerable))
            {
                detail = "spawned-pile-list-missing " + managerDetail;
                return false;
            }

            object? bestPile = null;
            var bestDistanceSq = float.MaxValue;
            var bestX = 0;
            var bestY = 0;
            var bestZ = 0;
            var scanned = 0;
            foreach (var candidate in enumerable)
            {
                if (candidate == null)
                {
                    continue;
                }

                scanned++;
                if (!TryReadReplicationWorldObjectGridPosition(candidate, out var candidateX, out var candidateY, out var candidateZ)
                    || !TryGetReplicationPileStoredResource(candidate, out var storedResource, out _)
                    || storedResource == null
                    || !TryReadReplicationResourcePileBlueprintId(candidate, storedResource, out var localBlueprintId, out _)
                    || !string.Equals(localBlueprintId, blueprintId, StringComparison.Ordinal))
                {
                    continue;
                }

                var dx = candidateX - originX;
                var dy = candidateY - originY;
                var dz = candidateZ - originZ;
                var distanceSq = (dx * dx) + (dy * dy * 9f) + (dz * dz);
                if (distanceSq > ReplicationCarryProbeMaxDistanceSq || distanceSq >= bestDistanceSq)
                {
                    continue;
                }

                if (!TryReadReplicationWorldObjectIntMember(storedResource, "Amount", "amount", out var candidateAmount)
                    || candidateAmount <= 0)
                {
                    continue;
                }

                bestPile = candidate;
                bestDistanceSq = distanceSq;
                bestX = candidateX;
                bestY = candidateY;
                bestZ = candidateZ;
                amount = candidateAmount;
            }

            if (bestPile == null)
            {
                detail = "nearest-pile-not-found scanned="
                    + scanned.ToString(CultureInfo.InvariantCulture)
                    + " blueprintId="
                    + blueprintId
                    + " origin=Vec3Int("
                    + originX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + originY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + originZ.ToString(CultureInfo.InvariantCulture)
                    + ") "
                    + managerDetail;
                return false;
            }

            gridX = bestX;
            gridY = bestY;
            gridZ = bestZ;
            detail = "nearest-pile-matched scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " blueprintId="
                + blueprintId
                + " grid=Vec3Int("
                + gridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + gridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + gridZ.ToString(CultureInfo.InvariantCulture)
                + ") amount="
                + amount.ToString(CultureInfo.InvariantCulture)
                + " distanceSq="
                + bestDistanceSq.ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool TryDisposeReplicationResourcePile(object pile, string applyDetail, out string detail)
        {
            try
            {
                var method = AccessTools.Method(pile.GetType(), "Dispose", Type.EmptyTypes)
                    ?? AccessTools.Method(pile.GetType(), "Dispose");
                if (method == null)
                {
                    detail = "resource-pile-dispose-method-missing type=" + (pile.GetType().FullName ?? pile.GetType().Name);
                    return false;
                }

                method.Invoke(pile, null);
                detail = "ok via=Dispose " + applyDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationTerrainGroundDestroyed(ReplicationWorldObjectDelta delta, out string detail)
        {
            try
            {
                var groundManagerType = AccessTools.TypeByName("NSMedieval.Terrain.GroundManager");
                if (groundManagerType == null)
                {
                    detail = "ground-manager-type-missing";
                    return false;
                }

                var target = groundManagerInstance;
                if (target == null)
                {
                    var instanceProperty = AccessTools.Property(groundManagerType, "Instance");
                    target = instanceProperty?.GetValue(null, null);
                }

                if (target == null)
                {
                    detail = "ground-manager-missing";
                    return false;
                }

                if (!TryCreateVec3Int(delta.GridX, delta.GridY, delta.GridZ, out var position, out var vecDetail))
                {
                    detail = vecDetail;
                    return false;
                }

                var completionMethod = AccessTools.Method(groundManagerType, "OnDigActionCompleted", new[] { position.GetType() });
                if (completionMethod == null)
                {
                    detail = "on-dig-action-completed-method-missing";
                    return false;
                }

                var groundExistsBefore = TryInvokeReplicationGroundExists(groundManagerType, target, position, out var existsBefore)
                    ? existsBefore
                    : (bool?)null;
                var result = completionMethod.Invoke(target, new[] { position });
                var applied = result is bool boolResult && boolResult;
                var queueDetail = string.Empty;
                if (!applied && groundExistsBefore == true)
                {
                    queueDetail = TryQueueReplicationGroundRemoval(groundManagerType, target, position, out var forcedQueueDetail)
                        ? " forcedRemoval=" + forcedQueueDetail
                        : " forcedRemovalFailed=" + forcedQueueDetail;
                }

                var updateDetail = TryInvokeReplicationGroundRemovalUpdate(groundManagerType, target, out var removalUpdateDetail)
                    ? " removalUpdate=" + removalUpdateDetail
                    : " removalUpdateFailed=" + removalUpdateDetail;
                var groundExistsAfter = TryInvokeReplicationGroundExists(groundManagerType, target, position, out var existsAfter)
                    ? existsAfter
                    : (bool?)null;
                detail = (applied ? "ok" : "completion-returned-false")
                    + " via=OnDigActionCompleted grid=Vec3Int("
                    + delta.GridX.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + delta.GridY.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + delta.GridZ.ToString(CultureInfo.InvariantCulture)
                    + ") groundBefore="
                    + FormatNullableBool(groundExistsBefore)
                    + " groundAfter="
                    + FormatNullableBool(groundExistsAfter)
                    + queueDetail
                    + updateDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeReplicationGroundExists(Type groundManagerType, object target, object position, out bool exists)
        {
            exists = false;
            try
            {
                var method = AccessTools.Method(groundManagerType, "GroundExists", new[] { position.GetType() });
                if (method == null)
                {
                    return false;
                }

                var result = method.Invoke(target, new[] { position });
                if (result is bool boolResult)
                {
                    exists = boolResult;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryQueueReplicationGroundRemoval(Type groundManagerType, object target, object position, out string detail)
        {
            try
            {
                var field = AccessTools.Field(groundManagerType, "voxelsToRemove");
                var queue = field?.GetValue(target);
                if (queue == null)
                {
                    detail = "voxelsToRemove-missing";
                    return false;
                }

                var addMethod = AccessTools.Method(queue.GetType(), "Add", new[] { position.GetType() });
                if (addMethod == null)
                {
                    detail = "voxelsToRemove-add-missing type=" + (queue.GetType().FullName ?? queue.GetType().Name);
                    return false;
                }

                addMethod.Invoke(queue, new[] { position });
                detail = "queued";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeReplicationGroundRemovalUpdate(Type groundManagerType, object target, out string detail)
        {
            try
            {
                var method = AccessTools.Method(groundManagerType, "DoUpdateVoxelsRemoved", Type.EmptyTypes)
                    ?? AccessTools.Method(groundManagerType, "DoUpdateVoxelsRemoved");
                if (method == null)
                {
                    detail = "do-update-voxels-removed-missing";
                    return false;
                }

                method.Invoke(target, null);
                detail = "DoUpdateVoxelsRemoved";
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string FormatNullableBool(bool? value)
        {
            return value.HasValue ? (value.Value ? "true" : "false") : "unknown";
        }

        private static bool TryApplyReplicationAgentAnimationDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "agent-animation-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "agent-animation-view-lookup-failed " + viewDetail + " entityId=" + entityId;
                return false;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                if (string.Equals(delta.DeltaKind, "AgentAnimationReset", StringComparison.Ordinal))
                {
                    return TryInvokeReplicationAgentViewNoArgAnimationMethod(view, "ResetTriggers", entityId, viewDetail, out detail);
                }

                if (string.Equals(delta.DeltaKind, "AgentAnimationQuit", StringComparison.Ordinal))
                {
                    return TryInvokeReplicationAgentViewNoArgAnimationMethod(view, "ForceQuitAnimation", entityId, viewDetail, out detail);
                }

                var trigger = TryReadReplicationWorldObjectDetailToken(delta.Detail, "trigger", out var parsedTrigger)
                    ? parsedTrigger
                    : delta.BlueprintId;
                if (string.IsNullOrWhiteSpace(trigger) || string.Equals(trigger, "<none>", StringComparison.Ordinal))
                {
                    detail = "agent-animation-trigger-empty entityId=" + entityId + " " + viewDetail;
                    return false;
                }

                var invokeDetail = InvokeReplicationAgentViewAnimationTrigger(view, trigger, delta.Detail);
                detail = "ok agent-animation-triggered "
                    + invokeDetail
                    + " entityId="
                    + entityId
                    + " trigger="
                    + trigger
                    + " "
                    + viewDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "agent-animation-error " + FormatReflectionExceptionDetail(ex) + " entityId=" + entityId + " " + viewDetail;
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static string InvokeReplicationAgentViewAnimationTrigger(object view, string trigger, string animatorStateDetail)
        {
            var invoked = new List<string>(3);
            SetInstancePropertyIfPresent(view, view.GetType(), "TriggeredAnimationRunning", true);

            var stateDetail = ApplyReplicationAnimatorStateDetail(view, animatorStateDetail);
            if (!string.IsNullOrWhiteSpace(stateDetail))
            {
                invoked.Add("pre:" + stateDetail);
            }

            if (TryInvokeReplicationNativeAnimationController(view, trigger, out var nativeControllerDetail))
            {
                invoked.Add(nativeControllerDetail);
                if (replicationConfigAnimationDiagnostics && TryGetReplicationViewEntityId(view, out var nativeEntityId))
                {
                    var diagnostic = FormatReplicationAnimationDiagnostic(view, nativeEntityId, "client-native-controller-replay", trigger, includeParameters: true);
                    instance?.LogReplicationInfo("Going Cooperative replication animation diagnostic client " + diagnostic);
                }

                return "methods=" + string.Join("+", invoked.ToArray());
            }

            var onTriggerAnimation = FindReplicationInstanceMethod(view.GetType(), "OnTriggerAnimation", new[] { typeof(string) });
            if (onTriggerAnimation != null)
            {
                onTriggerAnimation.Invoke(view, new object[] { trigger });
                invoked.Add(onTriggerAnimation.Name);
            }

            var trySetTrigger = FindReplicationInstanceMethod(view.GetType(), "TrySetTrigger", new[] { typeof(string) });
            if (trySetTrigger != null
                && (onTriggerAnimation == null || !string.Equals(trySetTrigger.Name, onTriggerAnimation.Name, StringComparison.Ordinal)))
            {
                trySetTrigger.Invoke(view, new object[] { trigger });
                invoked.Add(trySetTrigger.Name);
            }

            if (TryReadInstanceMemberValue(view, "Animator", out var animatorValue) && animatorValue is Animator animatorFromProperty)
            {
                animatorFromProperty.SetTrigger(trigger);
                invoked.Add("Animator.SetTrigger(property)");
            }
            else if (TryReadInstanceMemberValue(view, "animator", out animatorValue) && animatorValue is Animator animatorFromField)
            {
                animatorFromField.SetTrigger(trigger);
                invoked.Add("Animator.SetTrigger(field)");
            }

            if (replicationConfigAnimationDiagnostics && TryGetReplicationViewEntityId(view, out var entityId))
            {
                var diagnostic = FormatReplicationAnimationDiagnostic(view, entityId, "client-visual-apply", trigger, includeParameters: true);
                instance?.LogReplicationInfo("Going Cooperative replication animation diagnostic client " + diagnostic);
            }

            return invoked.Count == 0
                ? "method-missing viewType=" + FormatShortTypeName(view.GetType())
                : "methods=" + string.Join("+", invoked.ToArray());
        }

        private static string InvokeReplicationSemanticWorkAnimationTrigger(object view, string trigger, string animatorStateDetail)
        {
            if (!replicationConfigSemanticAgentPresentation)
            {
                return InvokeReplicationAgentViewAnimationTrigger(view, trigger, animatorStateDetail);
            }

            var invoked = new List<string>(4);
            SetInstancePropertyIfPresent(view, view.GetType(), "TriggeredAnimationRunning", true);

            var stateDetail = ApplyReplicationAnimatorStateDetail(view, animatorStateDetail);
            if (!string.IsNullOrWhiteSpace(stateDetail))
            {
                invoked.Add("pre:" + stateDetail);
            }

            var actionParameterDetail = ApplyReplicationPuppetActionAnimatorParameters(view, trigger);
            if (!string.IsNullOrWhiteSpace(actionParameterDetail))
            {
                invoked.Add("pre:" + actionParameterDetail);
            }

            var delivered = false;
            var onTriggerAnimation = FindReplicationInstanceMethod(view.GetType(), "OnTriggerAnimation", new[] { typeof(string) });
            if (onTriggerAnimation != null)
            {
                try
                {
                    onTriggerAnimation.Invoke(view, new object[] { trigger });
                    invoked.Add(onTriggerAnimation.Name);
                    delivered = true;
                }
                catch (Exception ex)
                {
                    invoked.Add(onTriggerAnimation.Name + "-failed=" + FormatReflectionExceptionDetail(ex));
                }
            }

            if (!delivered && TryInvokeReplicationNativeAnimationController(view, trigger, out var nativeControllerDetail))
            {
                invoked.Add(nativeControllerDetail);
                delivered = true;
            }

            if (!delivered)
            {
                var trySetTrigger = FindReplicationInstanceMethod(view.GetType(), "TrySetTrigger", new[] { typeof(string) });
                if (trySetTrigger != null)
                {
                    trySetTrigger.Invoke(view, new object[] { trigger });
                    invoked.Add(trySetTrigger.Name);
                }
                else if (TryReadInstanceMemberValue(view, "Animator", out var animatorValue) && animatorValue is Animator animatorFromProperty)
                {
                    animatorFromProperty.SetTrigger(trigger);
                    invoked.Add("Animator.SetTrigger(property)");
                }
                else if (TryReadInstanceMemberValue(view, "animator", out animatorValue) && animatorValue is Animator animatorFromField)
                {
                    animatorFromField.SetTrigger(trigger);
                    invoked.Add("Animator.SetTrigger(field)");
                }
            }

            if (replicationConfigAnimationDiagnostics && TryGetReplicationViewEntityId(view, out var entityId))
            {
                var diagnostic = FormatReplicationAnimationDiagnostic(view, entityId, "client-semantic-work-visual-apply", trigger, includeParameters: true);
                instance?.LogReplicationInfo("Going Cooperative replication animation diagnostic client " + diagnostic);
            }

            return invoked.Count == 0
                ? "method-missing viewType=" + FormatShortTypeName(view.GetType())
                : "methods=" + string.Join("+", invoked.ToArray());
        }

        private static bool TryInvokeReplicationNativeAnimationController(object view, string trigger, out string detail)
        {
            detail = "AnimationController.unavailable";
            if (!replicationConfigNativeAnimationControllerReplay)
            {
                detail = "AnimationController.disabled";
                return false;
            }

            if (!TryInvokeReplicationObjectMethod(view, "GetAgent", out var agent) || agent == null
                || !TryInvokeReplicationObjectMethod(agent, "get_AgentOwner", out var owner) || owner == null)
            {
                detail = "AnimationController.owner-missing";
                return false;
            }

            var controllerType = AccessTools.TypeByName("NSMedieval.Controllers.AnimationController");
            if (controllerType == null)
            {
                detail = "AnimationController.type-missing";
                return false;
            }

            var controller = AccessTools.Property(controllerType, "Instance")?.GetValue(null, null);
            if (controller == null)
            {
                detail = "AnimationController.instance-missing";
                return false;
            }

            var methods = controller.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var parameters = method.GetParameters();
                if (!string.Equals(method.Name, "TriggerAgentAnimation", StringComparison.Ordinal)
                    || parameters.Length != 2
                    || parameters[1].ParameterType != typeof(string)
                    || !parameters[0].ParameterType.IsAssignableFrom(owner.GetType()))
                {
                    continue;
                }

                try
                {
                    method.Invoke(controller, new[] { owner, trigger });
                    detail = "AnimationController.TriggerAgentAnimation";
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "AnimationController.invoke-error=" + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            detail = "AnimationController.trigger-method-missing";
            return false;
        }

        private static bool TryApplyReplicationAgentAnimationParameterDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "parameterB64", out var parameterToken)
                || !TryDecodeReplicationDetailBase64(parameterToken, out var parameterName)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "valueKind", out var valueKind)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "value", out var valueText)
                || string.IsNullOrWhiteSpace(entityId)
                || string.IsNullOrWhiteSpace(parameterName))
            {
                detail = "agent-animation-parameter-missing detail=" + delta.Detail;
                return false;
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail)
                || view == null
                || !TryResolveReplicationAnimator(view, out var animator)
                || animator == null)
            {
                detail = "agent-animation-parameter-view-or-animator-missing entityId=" + entityId + " " + viewDetail;
                return false;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                if (string.Equals(valueKind, "bool", StringComparison.Ordinal)
                    && bool.TryParse(valueText, out var boolValue)
                    && TrySetReplicationAnimatorBool(animator, parameterName, boolValue))
                {
                    detail = "ok agent-animation-parameter entityId=" + entityId + " parameter=" + parameterName + " value=" + valueText + " " + viewDetail;
                    return true;
                }

                if (string.Equals(valueKind, "float", StringComparison.Ordinal)
                    && float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue)
                    && TrySetReplicationAnimatorFloat(animator, parameterName, floatValue))
                {
                    detail = "ok agent-animation-parameter entityId=" + entityId + " parameter=" + parameterName + " value=" + valueText + " " + viewDetail;
                    return true;
                }

                if (string.Equals(valueKind, "int", StringComparison.Ordinal)
                    && int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                    && TrySetReplicationAnimatorInteger(animator, parameterName, intValue))
                {
                    detail = "ok agent-animation-parameter entityId=" + entityId + " parameter=" + parameterName + " value=" + valueText + " " + viewDetail;
                    return true;
                }

                detail = "agent-animation-parameter-not-applied entityId=" + entityId + " parameter=" + parameterName + " kind=" + valueKind + " value=" + valueText + " " + viewDetail;
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static string ApplyReplicationPuppetActionAnimatorParameters(object view, string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger)
                || !TryResolveReplicationAnimator(view, out var animator)
                || animator == null)
            {
                return string.Empty;
            }

            if (!IsReplicationWorkAnimationTrigger(trigger))
            {
                return string.Empty;
            }

            var applied = 0;
            if (TrySetReplicationAnimatorBool(animator, "Use", true))
            {
                applied++;
            }

            if (TrySetReplicationAnimatorBool(animator, "IsAttacking", true))
            {
                applied++;
            }

            if (TrySetReplicationAnimatorFloat(animator, "AttackSpeed", 1f))
            {
                applied++;
            }

            if (TrySetReplicationAnimatorInteger(animator, "AttackRnd", 0))
            {
                applied++;
            }

            if (TrySetReplicationAnimatorInteger(animator, "WeaponType", 1))
            {
                applied++;
            }

            return applied > 0
                ? "Animator.SetActionParams(count=" + applied.ToString(CultureInfo.InvariantCulture) + ")"
                : string.Empty;
        }

        private static string ClearReplicationPuppetActionAnimatorParameters(object view)
        {
            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return string.Empty;
            }

            var cleared = 0;
            if (TrySetReplicationAnimatorBool(animator, "Use", false))
            {
                cleared++;
            }

            if (TrySetReplicationAnimatorBool(animator, "IsAttacking", false))
            {
                cleared++;
            }

            if (TrySetReplicationAnimatorInteger(animator, "WeaponType", 0))
            {
                cleared++;
            }

            return cleared > 0
                ? "actionParamsCleared=" + cleared.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static bool IsReplicationWorkAnimationTrigger(string trigger)
        {
            return string.Equals(trigger, "Mining", StringComparison.Ordinal)
                || string.Equals(trigger, "Build", StringComparison.Ordinal)
                || string.Equals(trigger, "BuildDown", StringComparison.Ordinal)
                || string.Equals(trigger, "Producing", StringComparison.Ordinal)
                || string.Equals(trigger, "Planting", StringComparison.Ordinal);
        }

        private static bool TrySetReplicationAnimatorBool(Animator animator, string name, bool value)
        {
            if (!HasReplicationAnimatorParameter(animator, name, AnimatorControllerParameterType.Bool))
            {
                return false;
            }

            animator.SetBool(name, value);
            return true;
        }

        private static bool TrySetReplicationAnimatorFloat(Animator animator, string name, float value)
        {
            if (!HasReplicationAnimatorParameter(animator, name, AnimatorControllerParameterType.Float))
            {
                return false;
            }

            animator.SetFloat(name, value);
            return true;
        }

        private static bool TrySetReplicationAnimatorInteger(Animator animator, string name, int value)
        {
            if (!HasReplicationAnimatorParameter(animator, name, AnimatorControllerParameterType.Int))
            {
                return false;
            }

            animator.SetInteger(name, value);
            return true;
        }

        private static bool HasReplicationAnimatorParameter(Animator animator, string name, AnimatorControllerParameterType type)
        {
            foreach (var parameter in animator.parameters)
            {
                if (string.Equals(parameter.name, name, StringComparison.Ordinal) && parameter.type == type)
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatReplicationAnimationDiagnostic(object? ownerOrView, string entityId, string source, string trigger, bool includeParameters)
        {
            var throttleKey = source
                + "|"
                + entityId
                + "|"
                + (string.IsNullOrWhiteSpace(trigger) ? "_" : trigger);
            lock (ReplicationWorldObjectDeltaLock)
            {
                var now = Time.realtimeSinceStartup;
                if (ReplicationAnimationDiagnosticLastLoggedAt.TryGetValue(throttleKey, out var lastAt)
                    && now - lastAt < 0.75f)
                {
                    return "throttled source="
                        + FormatReplicationWorldObjectDetailToken(source)
                        + " entityId="
                        + entityId
                        + " trigger="
                        + FormatReplicationWorldObjectDetailToken(trigger);
                }

                ReplicationAnimationDiagnosticLastLoggedAt[throttleKey] = now;
            }

            var view = ResolveReplicationAnimationDiagnosticView(ownerOrView);
            var viewLookupDetail = string.Empty;
            if (view == null
                && !string.IsNullOrWhiteSpace(entityId)
                && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var foundView, out viewLookupDetail))
            {
                view = foundView;
            }

            var builder = new StringBuilder(512);
            builder.Append("source=")
                .Append(FormatReplicationWorldObjectDetailToken(source))
                .Append(" entityId=")
                .Append(entityId)
                .Append(" trigger=")
                .Append(FormatReplicationWorldObjectDetailToken(trigger))
                .Append(" view=")
                .Append(view == null ? "missing" : FormatShortTypeName(view.GetType()));
            if (!string.IsNullOrWhiteSpace(viewLookupDetail))
            {
                builder.Append(" viewLookup=").Append(FormatReplicationWorldObjectDetailToken(viewLookupDetail));
            }

            if (view == null)
            {
                builder.Append(" owner=").Append(ownerOrView == null ? "null" : FormatShortTypeName(ownerOrView.GetType()));
                return builder.ToString();
            }

            AppendReplicationAnimationDiagnosticMember(builder, view, "IsRunningDisabledGoap", "disabledGoap");
            AppendReplicationAnimationDiagnosticMember(builder, view, "TriggeredAnimationRunning", "triggerRunning");
            AppendReplicationAnimationDiagnosticMember(builder, view, "CombatAnimationEventsEnabled", "combatEvents");
            AppendReplicationAnimationDiagnosticMember(builder, view, "Visible", "visible");
            AppendReplicationAnimationDiagnosticMember(builder, view, "attackAnimationLayerIndex", "attackLayer");

            if (TryReadInstanceMemberValue(view, "BodyPreview", out var bodyPreview) && bodyPreview != null
                || TryReadInstanceMemberValue(view, "bodyPreview", out bodyPreview) && bodyPreview != null)
            {
                builder.Append(" bodyPreview=").Append(FormatShortTypeName(bodyPreview.GetType()));
                AppendReplicationAnimationDiagnosticMember(builder, bodyPreview, "IsWeaponVisible", "weaponVisible");
                AppendReplicationAnimationDiagnosticMember(builder, bodyPreview, "isWeaponsVisible", "weaponsVisibleField");
                AppendReplicationAnimationDiagnosticSocket(builder, bodyPreview, "CarryBodySocket", "carrySocket");
                AppendReplicationAnimationDiagnosticSocket(builder, bodyPreview, "RightHandSocket", "rightHand");
                AppendReplicationAnimationDiagnosticSocket(builder, bodyPreview, "LeftHandSocket", "leftHand");
                AppendReplicationAnimationDiagnosticArray(builder, bodyPreview, "weaponTransform", "weaponTransforms");
                AppendReplicationAnimationDiagnosticArray(builder, bodyPreview, "weaponBlueprint", "weaponBlueprints");
            }

            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                builder.Append(" animator=missing");
                return TrimFingerprintText(builder.ToString(), 1800);
            }

            builder.Append(" animator=ok layers=")
                .Append(animator.layerCount.ToString(CultureInfo.InvariantCulture))
                .Append(" params=")
                .Append(animator.parameterCount.ToString(CultureInfo.InvariantCulture));

            var layerCount = Math.Min(4, Math.Max(0, animator.layerCount));
            for (var layer = 0; layer < layerCount; layer++)
            {
                var state = animator.GetCurrentAnimatorStateInfo(layer);
                var normalized = state.normalizedTime - Mathf.Floor(state.normalizedTime);
                builder.Append(" layer")
                    .Append(layer.ToString(CultureInfo.InvariantCulture))
                    .Append("=")
                    .Append(FormatReplicationWorldObjectDetailToken(animator.GetLayerName(layer)))
                    .Append(":w")
                    .Append(Mathf.RoundToInt(animator.GetLayerWeight(layer) * 1000f).ToString(CultureInfo.InvariantCulture))
                    .Append(":full")
                    .Append(state.fullPathHash.ToString(CultureInfo.InvariantCulture))
                    .Append(":short")
                    .Append(state.shortNameHash.ToString(CultureInfo.InvariantCulture))
                    .Append(":t")
                    .Append(Mathf.RoundToInt(normalized * 1000f).ToString(CultureInfo.InvariantCulture));
            }

            if (includeParameters)
            {
                AppendReplicationAnimationDiagnosticParameters(builder, animator);
            }

            return TrimFingerprintText(builder.ToString(), 1800);
        }

        private static object? ResolveReplicationAnimationDiagnosticView(object? ownerOrView)
        {
            if (ownerOrView == null)
            {
                return null;
            }

            if (ownerOrView is MonoBehaviour)
            {
                return ownerOrView;
            }

            if (TryInvokeReplicationObjectMethod(ownerOrView, "GetView", out var view) && view != null)
            {
                return view;
            }

            if (TryReadInstanceMemberValue(ownerOrView, "View", out view) && view != null)
            {
                return view;
            }

            if (TryReadInstanceMemberValue(ownerOrView, "view", out view) && view != null)
            {
                return view;
            }

            if (TryReadInstanceMemberValue(ownerOrView, "AgentOwner", out var owner) && owner != null)
            {
                return ResolveReplicationAnimationDiagnosticView(owner);
            }

            if (TryReadInstanceMemberValue(ownerOrView, "agentOwner", out owner) && owner != null)
            {
                return ResolveReplicationAnimationDiagnosticView(owner);
            }

            return null;
        }

        private static void AppendReplicationAnimationDiagnosticMember(StringBuilder builder, object value, string memberName, string label)
        {
            if (TryReadInstanceMemberValue(value, memberName, out var memberValue) && memberValue != null)
            {
                builder.Append(" ")
                    .Append(label)
                    .Append("=")
                    .Append(FormatReplicationWorldObjectDetailToken(Convert.ToString(memberValue, CultureInfo.InvariantCulture) ?? "<null>"));
            }
        }

        private static void AppendReplicationAnimationDiagnosticSocket(StringBuilder builder, object bodyPreview, string memberName, string label)
        {
            if (TryReadInstanceMemberValue(bodyPreview, memberName, out var value) && value is Transform socket)
            {
                builder.Append(" ")
                    .Append(label)
                    .Append("Children=")
                    .Append(socket.childCount.ToString(CultureInfo.InvariantCulture));

                if (socket.childCount > 0)
                {
                    builder.Append(" ")
                        .Append(label)
                        .Append("Kids=");
                    var appended = 0;
                    for (var i = 0; i < socket.childCount && appended < 16; i++)
                    {
                        var child = socket.GetChild(i);
                        if (child == null)
                        {
                            continue;
                        }

                        if (appended > 0)
                        {
                            builder.Append(",");
                        }

                        builder.Append(FormatReplicationWorldObjectDetailToken(child.name))
                            .Append(":")
                            .Append(child.gameObject != null && child.gameObject.activeSelf ? "on" : "off")
                            .Append(":c")
                            .Append(child.childCount.ToString(CultureInfo.InvariantCulture));
                        appended++;
                    }

                    if (socket.childCount > appended)
                    {
                        builder.Append(",more")
                            .Append((socket.childCount - appended).ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private static void AppendReplicationAnimationDiagnosticArray(StringBuilder builder, object value, string memberName, string label)
        {
            if (!TryReadInstanceMemberValue(value, memberName, out var memberValue) || memberValue == null)
            {
                return;
            }

            if (memberValue is Array array)
            {
                builder.Append(" ")
                    .Append(label)
                    .Append("=");
                var appended = 0;
                for (var i = 0; i < array.Length && appended < 8; i++)
                {
                    var item = array.GetValue(i);
                    if (appended > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append(":")
                        .Append(FormatReplicationAnimationDiagnosticObject(item));
                    appended++;
                }

                if (array.Length > appended)
                {
                    builder.Append(",more")
                        .Append((array.Length - appended).ToString(CultureInfo.InvariantCulture));
                }

                return;
            }

            if (memberValue is System.Collections.IEnumerable enumerable && memberValue is not string)
            {
                builder.Append(" ")
                    .Append(label)
                    .Append("=");
                var appended = 0;
                foreach (var item in enumerable)
                {
                    if (appended >= 8)
                    {
                        builder.Append(",more");
                        break;
                    }

                    if (appended > 0)
                    {
                        builder.Append(",");
                    }

                    builder.Append(appended.ToString(CultureInfo.InvariantCulture))
                        .Append(":")
                        .Append(FormatReplicationAnimationDiagnosticObject(item));
                    appended++;
                }
            }
        }

        private static string FormatReplicationAnimationDiagnosticObject(object? value)
        {
            if (value == null)
            {
                return "null";
            }

            var typeName = FormatShortTypeName(value.GetType());
            if (value is Transform transform)
            {
                return FormatReplicationWorldObjectDetailToken(typeName
                    + "("
                    + transform.name
                    + ":"
                    + (transform.gameObject != null && transform.gameObject.activeSelf ? "on" : "off")
                    + ":c"
                    + transform.childCount.ToString(CultureInfo.InvariantCulture)
                    + ")");
            }

            if (value is UnityEngine.Object unityObject)
            {
                return FormatReplicationWorldObjectDetailToken(typeName + "(" + unityObject.name + ")");
            }

            if (TryReadInstanceMemberValue(value, "ID", out var id) && id != null
                || TryReadInstanceMemberValue(value, "Id", out id) && id != null
                || TryReadInstanceMemberValue(value, "id", out id) && id != null
                || TryReadInstanceMemberValue(value, "Name", out id) && id != null
                || TryReadInstanceMemberValue(value, "name", out id) && id != null)
            {
                return FormatReplicationWorldObjectDetailToken(typeName + "(" + Convert.ToString(id, CultureInfo.InvariantCulture) + ")");
            }

            return FormatReplicationWorldObjectDetailToken(typeName);
        }

        private static void AppendReplicationAnimationDiagnosticParameters(StringBuilder builder, Animator animator)
        {
            var appended = 0;
            var parameters = animator.parameters ?? Array.Empty<AnimatorControllerParameter>();
            for (var i = 0; i < parameters.Length && appended < 32; i++)
            {
                var parameter = parameters[i];
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.name))
                {
                    continue;
                }

                var value = parameter.type.ToString();
                try
                {
                    if (parameter.type == AnimatorControllerParameterType.Bool)
                    {
                        value = animator.GetBool(parameter.name) ? "true" : "false";
                    }
                    else if (parameter.type == AnimatorControllerParameterType.Int)
                    {
                        value = animator.GetInteger(parameter.name).ToString(CultureInfo.InvariantCulture);
                    }
                    else if (parameter.type == AnimatorControllerParameterType.Float)
                    {
                        value = animator.GetFloat(parameter.name).ToString("F2", CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception ex)
                {
                    value = "read-error:" + ex.GetType().Name;
                }

                builder.Append(" p.")
                    .Append(FormatReplicationWorldObjectDetailToken(parameter.name))
                    .Append("=")
                    .Append(FormatReplicationWorldObjectDetailToken(value));
                appended++;
            }
        }

        private static string CaptureReplicationAnimatorStateDetail(object? ownerOrView)
        {
            return CaptureReplicationAnimatorStateDetail(ownerOrView, string.Empty);
        }

        private static string CaptureReplicationAnimatorStateDetail(object? ownerOrView, string entityId)
        {
            var view = ResolveReplicationAnimationDiagnosticView(ownerOrView);
            if (view == null
                && !string.IsNullOrWhiteSpace(entityId)
                && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var foundView, out _))
            {
                view = foundView;
            }

            if (view == null)
            {
                return string.Empty;
            }

            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return string.Empty;
            }

            var layerCount = Math.Min(4, Math.Max(0, animator.layerCount));
            if (layerCount <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(160);
            builder.Append("animLayers=");
            builder.Append(layerCount.ToString(CultureInfo.InvariantCulture));
            for (var layer = 0; layer < layerCount; layer++)
            {
                var state = animator.GetCurrentAnimatorStateInfo(layer);
                var normalized = state.normalizedTime;
                normalized -= Mathf.Floor(normalized);
                var timePermille = Mathf.Clamp(Mathf.RoundToInt(normalized * 1000f), 0, 1000);
                var weightPermille = Mathf.Clamp(Mathf.RoundToInt(animator.GetLayerWeight(layer) * 1000f), 0, 1000);
                builder.Append(" animState");
                builder.Append(layer.ToString(CultureInfo.InvariantCulture));
                builder.Append("=");
                builder.Append(state.fullPathHash.ToString(CultureInfo.InvariantCulture));
                builder.Append(" animTime");
                builder.Append(layer.ToString(CultureInfo.InvariantCulture));
                builder.Append("=");
                builder.Append(timePermille.ToString(CultureInfo.InvariantCulture));
                builder.Append(" animWeight");
                builder.Append(layer.ToString(CultureInfo.InvariantCulture));
                builder.Append("=");
                builder.Append(weightPermille.ToString(CultureInfo.InvariantCulture));
            }

            AppendReplicationAnimatorBoolStateDetail(builder, animator, "Moving");
            AppendReplicationAnimatorBoolStateDetail(builder, animator, "Running");
            AppendReplicationAnimatorBoolStateDetail(builder, animator, "Use");
            AppendReplicationAnimatorBoolStateDetail(builder, animator, "IsAttacking");
            AppendReplicationAnimatorFloatStateDetail(builder, animator, "AttackSpeed");
            AppendReplicationAnimatorFloatStateDetail(builder, animator, "GenericRnd");
            AppendReplicationAnimatorIntegerStateDetail(builder, animator, "AttackRnd");
            AppendReplicationAnimatorIntegerStateDetail(builder, animator, "WeaponType");

            return builder.ToString();
        }

        private static string BuildReplicationAnimatorCaptureProbe(object? ownerOrView, string entityId, string trigger, string capturedDetail)
        {
            var safeCapturedDetail = capturedDetail ?? string.Empty;
            var builder = new StringBuilder(384);
            builder.Append("entityId=")
                .Append(entityId)
                .Append(" trigger=")
                .Append(FormatReplicationWorldObjectDetailToken(string.IsNullOrWhiteSpace(trigger) ? "<none>" : trigger))
                .Append(" owner=")
                .Append(ownerOrView == null ? "null" : FormatShortTypeName(ownerOrView.GetType()))
                .Append(" captured=")
                .Append(string.IsNullOrWhiteSpace(safeCapturedDetail) ? "empty" : "ok")
                .Append(" capturedLen=")
                .Append(safeCapturedDetail.Length.ToString(CultureInfo.InvariantCulture))
                .Append(" hasLayers=")
                .Append(safeCapturedDetail.IndexOf("animLayers=", StringComparison.Ordinal) >= 0 ? "yes" : "no")
                .Append(" hasMoving=")
                .Append(safeCapturedDetail.IndexOf("animBoolMoving=", StringComparison.Ordinal) >= 0 ? "yes" : "no");

            var directView = ResolveReplicationAnimationDiagnosticView(ownerOrView);
            builder.Append(" directView=")
                .Append(directView == null ? "missing" : FormatShortTypeName(directView.GetType()));

            object? fallbackView = null;
            var fallbackDetail = string.Empty;
            if (!string.IsNullOrWhiteSpace(entityId)
                && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var foundView, out fallbackDetail))
            {
                fallbackView = foundView;
            }

            builder.Append(" fallbackView=")
                .Append(fallbackView == null ? "missing" : FormatShortTypeName(fallbackView.GetType()));
            if (!string.IsNullOrWhiteSpace(fallbackDetail))
            {
                builder.Append(" fallbackDetail=")
                    .Append(FormatReplicationWorldObjectDetailToken(fallbackDetail));
            }

            var resolvedView = directView ?? fallbackView;
            if (resolvedView == null)
            {
                return builder.Append(" animator=view-missing").ToString();
            }

            if (!TryResolveReplicationAnimator(resolvedView, out var animator) || animator == null)
            {
                return builder.Append(" animator=missing").ToString();
            }

            builder.Append(" animator=ok layers=")
                .Append(animator.layerCount.ToString(CultureInfo.InvariantCulture))
                .Append(" params=")
                .Append(animator.parameterCount.ToString(CultureInfo.InvariantCulture));

            if (HasReplicationAnimatorParameter(animator, "Moving", AnimatorControllerParameterType.Bool))
            {
                builder.Append(" p.Moving=")
                    .Append(animator.GetBool("Moving") ? "true" : "false");
            }

            if (HasReplicationAnimatorParameter(animator, "Running", AnimatorControllerParameterType.Bool))
            {
                builder.Append(" p.Running=")
                    .Append(animator.GetBool("Running") ? "true" : "false");
            }

            if (HasReplicationAnimatorParameter(animator, "GenericRnd", AnimatorControllerParameterType.Float))
            {
                builder.Append(" p.GenericRnd=")
                    .Append(animator.GetFloat("GenericRnd").ToString("F2", CultureInfo.InvariantCulture));
            }

            return TrimFingerprintText(builder.ToString(), 1800);
        }

        private static string ExtractReplicationAnimatorStateDetail(string detail)
        {
            if (!TryReadReplicationWorldObjectDetailInt(detail, "animLayers", out var layerCount) || layerCount <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(160);
            builder.Append("animLayers=");
            builder.Append(Math.Min(4, layerCount).ToString(CultureInfo.InvariantCulture));
            for (var layer = 0; layer < Math.Min(4, layerCount); layer++)
            {
                if (!TryReadReplicationWorldObjectDetailInt(detail, "animState" + layer.ToString(CultureInfo.InvariantCulture), out var stateHash))
                {
                    continue;
                }

                var timePermille = TryReadReplicationWorldObjectDetailInt(detail, "animTime" + layer.ToString(CultureInfo.InvariantCulture), out var parsedTime)
                    ? Mathf.Clamp(parsedTime, 0, 1000)
                    : 0;
                var weightPermille = TryReadReplicationWorldObjectDetailInt(detail, "animWeight" + layer.ToString(CultureInfo.InvariantCulture), out var parsedWeight)
                    ? Mathf.Clamp(parsedWeight, 0, 1000)
                    : 1000;
                builder.Append(" animState");
                builder.Append(layer.ToString(CultureInfo.InvariantCulture));
                builder.Append("=");
                builder.Append(stateHash.ToString(CultureInfo.InvariantCulture));
                builder.Append(" animTime");
                builder.Append(layer.ToString(CultureInfo.InvariantCulture));
                builder.Append("=");
                builder.Append(timePermille.ToString(CultureInfo.InvariantCulture));
                builder.Append(" animWeight");
                builder.Append(layer.ToString(CultureInfo.InvariantCulture));
                builder.Append("=");
                builder.Append(weightPermille.ToString(CultureInfo.InvariantCulture));
            }

            AppendExtractedReplicationAnimatorStateValue(builder, detail, "animBoolUse");
            AppendExtractedReplicationAnimatorStateValue(builder, detail, "animBoolIsAttacking");
            AppendExtractedReplicationAnimatorStateValue(builder, detail, "animFloatAttackSpeed");
            AppendExtractedReplicationAnimatorStateValue(builder, detail, "animFloatGenericRnd");
            AppendExtractedReplicationAnimatorStateValue(builder, detail, "animIntAttackRnd");
            AppendExtractedReplicationAnimatorStateValue(builder, detail, "animIntWeaponType");

            return builder.ToString();
        }

        private static void AppendReplicationAnimatorBoolStateDetail(StringBuilder builder, Animator animator, string name)
        {
            if (!HasReplicationAnimatorParameter(animator, name, AnimatorControllerParameterType.Bool))
            {
                return;
            }

            builder.Append(" animBool")
                .Append(name)
                .Append("=")
                .Append(animator.GetBool(name) ? "true" : "false");
        }

        private static void AppendReplicationAnimatorFloatStateDetail(StringBuilder builder, Animator animator, string name)
        {
            if (!HasReplicationAnimatorParameter(animator, name, AnimatorControllerParameterType.Float))
            {
                return;
            }

            var value = Mathf.Clamp(Mathf.RoundToInt(animator.GetFloat(name) * 1000f), -1000000, 1000000);
            builder.Append(" animFloat")
                .Append(name)
                .Append("=")
                .Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendReplicationAnimatorIntegerStateDetail(StringBuilder builder, Animator animator, string name)
        {
            if (!HasReplicationAnimatorParameter(animator, name, AnimatorControllerParameterType.Int))
            {
                return;
            }

            builder.Append(" animInt")
                .Append(name)
                .Append("=")
                .Append(animator.GetInteger(name).ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendExtractedReplicationAnimatorStateValue(StringBuilder builder, string detail, string key)
        {
            if (!TryReadReplicationWorldObjectDetailToken(detail, key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            builder.Append(" ")
                .Append(key)
                .Append("=")
                .Append(value);
        }

        private static string ApplyReplicationAnimatorStateDetail(object view, string detail)
        {
            if (string.IsNullOrWhiteSpace(detail)
                || !TryReadReplicationWorldObjectDetailInt(detail, "animLayers", out var layerCount)
                || layerCount <= 0)
            {
                return string.Empty;
            }

            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return "animState=animator-missing";
            }

            var applied = 0;
            applied += ApplyReplicationAnimatorParameterStateDetail(animator, detail);
            var maxLayer = Math.Min(Math.Min(4, layerCount), animator.layerCount);
            for (var layer = 0; layer < maxLayer; layer++)
            {
                if (!TryReadReplicationWorldObjectDetailInt(detail, "animState" + layer.ToString(CultureInfo.InvariantCulture), out var stateHash)
                    || stateHash == 0)
                {
                    continue;
                }

                var normalized = TryReadReplicationWorldObjectDetailInt(detail, "animTime" + layer.ToString(CultureInfo.InvariantCulture), out var timePermille)
                    ? Mathf.Clamp01(timePermille / 1000f)
                    : 0f;
                if (TryReadReplicationWorldObjectDetailInt(detail, "animWeight" + layer.ToString(CultureInfo.InvariantCulture), out var weightPermille))
                {
                    animator.SetLayerWeight(layer, Mathf.Clamp01(weightPermille / 1000f));
                }

                animator.Play(stateHash, layer, normalized);
                applied++;
            }

            return applied > 0
                ? "Animator.ApplyState(count=" + applied.ToString(CultureInfo.InvariantCulture) + ")"
                : "animState=none";
        }

        private static int ApplyReplicationAnimatorParameterStateDetail(Animator animator, string detail)
        {
            var applied = 0;
            if (TryReadReplicationWorldObjectDetailBool(detail, "animBoolUse", out var use)
                && TrySetReplicationAnimatorBool(animator, "Use", use))
            {
                applied++;
            }

            if (TryReadReplicationWorldObjectDetailBool(detail, "animBoolIsAttacking", out var isAttacking)
                && TrySetReplicationAnimatorBool(animator, "IsAttacking", isAttacking))
            {
                applied++;
            }

            if (TryReadReplicationWorldObjectDetailInt(detail, "animFloatAttackSpeed", out var attackSpeed)
                && TrySetReplicationAnimatorFloat(animator, "AttackSpeed", attackSpeed / 1000f))
            {
                applied++;
            }

            if (TryReadReplicationWorldObjectDetailInt(detail, "animFloatGenericRnd", out var genericRnd)
                && TrySetReplicationAnimatorFloat(animator, "GenericRnd", genericRnd / 1000f))
            {
                applied++;
            }

            if (TryReadReplicationWorldObjectDetailInt(detail, "animIntAttackRnd", out var attackRnd)
                && TrySetReplicationAnimatorInteger(animator, "AttackRnd", attackRnd))
            {
                applied++;
            }

            if (TryReadReplicationWorldObjectDetailInt(detail, "animIntWeaponType", out var weaponType)
                && TrySetReplicationAnimatorInteger(animator, "WeaponType", weaponType))
            {
                applied++;
            }

            return applied;
        }

        private static bool TryResolveReplicationAnimator(object view, out Animator? animator)
        {
            animator = null;
            if (TryReadInstanceMemberValue(view, "Animator", out var animatorValue) && animatorValue is Animator animatorFromProperty)
            {
                animator = animatorFromProperty;
                return true;
            }

            if (TryReadInstanceMemberValue(view, "animator", out animatorValue) && animatorValue is Animator animatorFromField)
            {
                animator = animatorFromField;
                return true;
            }

            return false;
        }

        private static bool TryInvokeReplicationAgentViewNoArgAnimationMethod(object view, string methodName, string entityId, string viewDetail, out string detail)
        {
            var method = FindReplicationInstanceMethod(view.GetType(), methodName, Type.EmptyTypes);
            if (method == null)
            {
                detail = "agent-animation-method-missing method=" + methodName + " entityId=" + entityId + " viewType=" + FormatShortTypeName(view.GetType());
                return false;
            }

            method.Invoke(view, null);
            detail = "ok agent-animation-method method="
                + methodName
                + " entityId="
                + entityId
                + " "
                + viewDetail;
            return true;
        }

        private static string ApplyReplicationAgentActionStatusToVisibleWorkerEntries(string entityId, string statusText)
        {
            var type = AccessTools.TypeByName("NSMedieval.UI.WorkerEntryLayoutItemView");
            if (type == null)
            {
                return "type-missing";
            }

            UnityEngine.Object[] entries;
            try
            {
                entries = UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
            }
            catch (Exception ex)
            {
                return "scan-error=" + ex.GetType().Name;
            }

            var scanned = 0;
            var matched = 0;
            var updated = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                scanned++;
                if (!TryReadInstanceMemberValue(entry, "HumanoidInstance", out var humanoid)
                    || humanoid == null
                    || !TryGetReplicationAgentOwnerEntityId(humanoid, out var candidateId, out _)
                    || !string.Equals(candidateId, entityId, StringComparison.Ordinal))
                {
                    continue;
                }

                matched++;
                if (ApplyReplicationAgentActionStatusToWorkerEntryUi(entry, entityId, statusText, "delta-apply", logSuccess: false))
                {
                    updated++;
                }
            }

            return "scanned:"
                + scanned.ToString(CultureInfo.InvariantCulture)
                + ",matched:"
                + matched.ToString(CultureInfo.InvariantCulture)
                + ",updated:"
                + updated.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildReplicationClientActionUiSurfaceProbe(string entityId)
        {
            var builder = new StringBuilder(320);
            AppendReplicationUiSurfaceCount(builder, "NSMedieval.UI.WorkerEntryLayoutItemView", "workerEntries", entityId);
            AppendReplicationUiSurfaceCount(builder, "NSMedieval.UI.SelectionMiddleCharacterView", "selectionCharacter", entityId);
            AppendReplicationUiSurfaceCount(builder, "NSMedieval.UI.WorkerDetailsView", "workerDetails", entityId);
            AppendReplicationUiSurfaceCount(builder, "NSMedieval.UI.WorkerBioExtraPanel", "workerBio", entityId);
            AppendReplicationUiSurfaceCount(builder, "NSMedieval.UI.WorkerSkillsExtraPanel", "workerSkills", entityId);
            AppendReplicationUiSurfaceCount(builder, "NSMedieval.UI.WorkerInventoryExtraPanel", "workerInventory", entityId);
            return builder.Length == 0 ? "none" : TrimFingerprintText(builder.ToString(), 900);
        }

        private static void AppendReplicationUiSurfaceCount(StringBuilder builder, string typeName, string label, string entityId)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                AppendReplicationUiSurfacePart(builder, label + "=type-missing");
                return;
            }

            UnityEngine.Object[] entries;
            try
            {
                entries = UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
            }
            catch (Exception ex)
            {
                AppendReplicationUiSurfacePart(builder, label + "=scan-error:" + ex.GetType().Name);
                return;
            }

            var matched = 0;
            var text = string.Empty;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                if (TryReadInstanceMemberValue(entry, "HumanoidInstance", out var humanoid)
                    && humanoid != null
                    && TryGetReplicationAgentOwnerEntityId(humanoid, out var candidateId, out _)
                    && string.Equals(candidateId, entityId, StringComparison.Ordinal))
                {
                    matched++;
                    if (string.IsNullOrWhiteSpace(text)
                        && TryReadInstanceMemberValue(entry, "currentWorkerActionText", out var textElement)
                        && textElement != null
                        && (TryReadInstanceMemberValue(textElement, "text", out var textValue)
                            || TryReadInstanceMemberValue(textElement, "Text", out textValue))
                        && textValue != null)
                    {
                        text = Convert.ToString(textValue, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }
            }

            AppendReplicationUiSurfacePart(builder,
                label
                + "=count:"
                + entries.Length.ToString(CultureInfo.InvariantCulture)
                + ",matched:"
                + matched.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrWhiteSpace(text) ? string.Empty : ",text:" + FormatReplicationWorldObjectDetailToken(text)));
        }

        private static void AppendReplicationUiSurfacePart(StringBuilder builder, string value)
        {
            if (builder.Length > 0)
            {
                builder.Append(";");
            }

            builder.Append(value);
        }

        private static bool ApplyReplicationAgentActionStatusToWorkerEntryUi(
            object workerEntry,
            string entityId,
            string statusText,
            string source,
            bool logSuccess)
        {
            if (!TryReadInstanceMemberValue(workerEntry, "currentWorkerActionText", out var textElement)
                || textElement == null)
            {
                instance?.LogReplicationInfo("Going Cooperative replication agent action status ui text missing entityId="
                    + entityId
                    + " source="
                    + source);
                return false;
            }

            if (TrySetInstanceMemberValue(textElement, "text", statusText)
                || TrySetInstanceMemberValue(textElement, "Text", statusText))
            {
                if (logSuccess)
                {
                    instance?.LogReplicationInfo("Going Cooperative replication agent action status ui text updated entityId="
                        + entityId
                        + " status="
                        + FormatReplicationWorldObjectDetailToken(statusText)
                        + " source="
                        + source);
                }

                return true;
            }

            instance?.LogReplicationInfo("Going Cooperative replication agent action status ui text set failed entityId="
                + entityId
                + " source="
                + source
                + " textType="
                + FormatShortTypeName(textElement.GetType()));
            return false;
        }

        private static string NormalizeReplicationAgentActionStatusText(string goalOrStatus, bool hasStarted)
        {
            if (!hasStarted || string.IsNullOrWhiteSpace(goalOrStatus))
            {
                return "Idle";
            }

            var value = goalOrStatus.Trim();
            switch (value)
            {
                case "IdleGoal":
                case "Idle":
                    return "Idle";
                case "ChopTreeGoal":
                    return "Cutting";
                case "MineGoal":
                case "MiningGoal":
                    return "Mining";
                case "DigGoal":
                case "DiggingGoal":
                    return "Digging";
                case "HarvestGoal":
                case "HarvestPlantGoal":
                    return "Harvesting";
                case "BuildGoal":
                case "ConstructGoal":
                case "ConstructionGoal":
                case "ConstructBuildingGoal":
                    return "Building";
                case "HaulGoal":
                case "StoreResourceGoal":
                    return "Hauling";
                case "PickupResourceGoal":
                case "PickUpResourceGoal":
                    return "Picking up";
                case "EquipItemGoal":
                case "EquipGoal":
                    return "Equipping";
                default:
                    return value.EndsWith("Goal", StringComparison.Ordinal)
                        ? value.Substring(0, value.Length - "Goal".Length)
                : value;
            }
        }

        private static void ProcessReplicationPuppetActionStates()
        {
            if (replicationConfigHostMode || IsReplicationWorldObjectDeltaModeOff())
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            List<KeyValuePair<string, ReplicationPuppetActionState>> active = null!;
            var expired = new List<string>();
            lock (ReplicationWorldObjectDeltaLock)
            {
                active = new List<KeyValuePair<string, ReplicationPuppetActionState>>(ReplicationPuppetActionStateByEntityId.Count);
                foreach (var pair in ReplicationPuppetActionStateByEntityId)
                {
                    if (now > pair.Value.ExpiresRealtime)
                    {
                        expired.Add(pair.Key);
                    }
                    else
                    {
                        active.Add(pair);
                    }
                }

                for (var i = 0; i < expired.Count; i++)
                {
                    ReplicationPuppetActionStateByEntityId.Remove(expired[i]);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ClearReplicationPuppetActionVisual(expired[i]);
                ApplyReplicationAgentActionStatusToVisibleWorkerEntries(expired[i], "Idle");
            }

            for (var i = 0; i < active.Count; i++)
            {
                var pair = active[i];
                if (now < pair.Value.NextReassertRealtime)
                {
                    continue;
                }

                pair.Value.NextReassertRealtime = now + ReplicationPuppetActionReassertSeconds;
                ApplyReplicationPuppetActionVisual(pair.Key, pair.Value.AnimationToken, pair.Value.AnimatorStateDetail, pair.Value.ActionHandItemId, force: false, triggerAnimation: false);
            }
        }

        private static string ApplyReplicationPuppetActionVisual(
            string entityId,
            string animationToken,
            string animatorStateDetail,
            string actionHandItemId,
            bool force,
            bool triggerAnimation,
            bool semanticWorkPresentation = false)
        {
            if (string.IsNullOrWhiteSpace(animationToken))
            {
                return "animation-token-empty";
            }

            if (!force)
            {
                ReplicationPuppetActionState state;
                lock (ReplicationWorldObjectDeltaLock)
                {
                    if (ReplicationPuppetActionStateByEntityId.TryGetValue(entityId, out state)
                        && Time.realtimeSinceStartup < state.NextVisualRealtime)
                    {
                        return "throttled";
                    }

                    if (state != null)
                    {
                        state.NextVisualRealtime = Time.realtimeSinceStartup + ReplicationPuppetActionVisualReassertSeconds;
                    }
                }
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                return "view-missing " + viewDetail;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                var handDetail = TryApplyReplicationPuppetActionHandItemVisual(entityId, view, actionHandItemId, out var actionHandDetail)
                    ? actionHandDetail
                    : "handItem=not-applied " + actionHandDetail;
                var invokeDetail = triggerAnimation
                    ? semanticWorkPresentation
                        ? InvokeReplicationSemanticWorkAnimationTrigger(view, animationToken, animatorStateDetail)
                        : InvokeReplicationAgentViewAnimationTrigger(view, animationToken, animatorStateDetail)
                    : replicationConfigActionAnimatorStateSampling && !string.IsNullOrWhiteSpace(animatorStateDetail)
                        ? "heartbeat-" + ApplyReplicationAnimatorStateDetail(view, animatorStateDetail)
                        : "animation-trigger-skipped heartbeat/reassert";
                return "ok " + handDetail + " " + invokeDetail + " token=" + animationToken + " " + viewDetail;
            }
            catch (Exception ex)
            {
                return "error=" + FormatReflectionExceptionDetail(ex) + " " + viewDetail;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static string ClearReplicationPuppetActionVisual(string entityId)
        {
            if (replicationConfigCombatReplication
                && replicationConfigCombatPresentationReplication
                && (ReplicationCombatClientChargeByEntityId.ContainsKey(entityId)
                    || ReplicationCombatPresentationExpiryByEntityId.ContainsKey(entityId)))
            {
                return "clear-skipped active-combat-charge";
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                return "clear-view-missing " + viewDetail;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                var method = FindReplicationInstanceMethod(view.GetType(), "ForceQuitAnimation", Type.EmptyTypes)
                    ?? FindReplicationInstanceMethod(view.GetType(), "ResetTriggers", Type.EmptyTypes);
                if (method == null)
                {
                    return "clear-method-missing viewType=" + FormatShortTypeName(view.GetType()) + " " + viewDetail;
                }

                method.Invoke(view, null);
                var handDetail = ClearReplicationPuppetActionHandItemVisual(entityId, view);
                var actionParameterDetail = ClearReplicationPuppetActionAnimatorParameters(view);
                return "ok clear-method="
                    + method.Name
                    + " "
                    + handDetail
                    + (string.IsNullOrWhiteSpace(actionParameterDetail) ? string.Empty : " " + actionParameterDetail)
                    + " "
                    + viewDetail;
            }
            catch (Exception ex)
            {
                return "clear-error=" + FormatReflectionExceptionDetail(ex) + " " + viewDetail;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool TryResolveReplicationActiveHandItemId(object? ownerOrView, out string handItemId, out string detail)
        {
            return TryResolveReplicationActiveHandItemId(ownerOrView, string.Empty, out handItemId, out detail);
        }

        private static bool TryResolveReplicationActiveHandItemId(object? ownerOrView, string entityId, out string handItemId, out string detail)
        {
            handItemId = string.Empty;
            var view = ResolveReplicationAnimationDiagnosticView(ownerOrView);
            if (view == null
                && !string.IsNullOrWhiteSpace(entityId)
                && TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var fallbackView, out var fallbackDetail))
            {
                view = fallbackView;
                detail = "fallback=" + fallbackDetail;
            }
            else
            {
                detail = string.Empty;
            }

            if (view == null)
            {
                detail = string.IsNullOrWhiteSpace(detail) ? "handItem-view-missing" : "handItem-view-missing " + detail;
                return false;
            }

            if ((!TryReadInstanceMemberValue(view, "BodyPreview", out var bodyPreview) || bodyPreview == null)
                && (!TryReadInstanceMemberValue(view, "bodyPreview", out bodyPreview) || bodyPreview == null))
            {
                detail = "handItem-bodyPreview-missing viewType=" + FormatShortTypeName(view.GetType()) + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
                return false;
            }

            if (!TryReadInstanceMemberValue(bodyPreview, "RightHandSocket", out var socketValue) || socketValue is not Transform socket)
            {
                if (!TryReadInstanceMemberValue(bodyPreview, "rightHandSocket", out socketValue) || socketValue is not Transform fallbackSocket)
                {
                    detail = "handItem-right-socket-missing bodyPreviewType=" + FormatShortTypeName(bodyPreview.GetType()) + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
                    return false;
                }

                socket = fallbackSocket;
            }

            for (var i = 0; i < socket.childCount; i++)
            {
                var child = socket.GetChild(i);
                if (child == null || child.gameObject == null || (!child.gameObject.activeSelf && !child.gameObject.activeInHierarchy))
                {
                    continue;
                }

                var id = NormalizeReplicationActionHandItemId(child.name);
                if (string.IsNullOrWhiteSpace(id)
                    || id.StartsWith("B_R_", StringComparison.Ordinal)
                    || id.IndexOf("Shackles", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.StartsWith("GCoop_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (id.EndsWith("_item", StringComparison.Ordinal)
                    || id.EndsWith("item", StringComparison.Ordinal)
                    || id.IndexOf("_item_", StringComparison.Ordinal) >= 0)
                {
                    handItemId = id;
                    detail = "handItem=" + handItemId + " childIndex=" + i.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
                    return true;
                }
            }

            detail = "handItem-not-found children=" + socket.childCount.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
            return false;
        }

        private static string NormalizeReplicationActionHandItemId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim();
            var cloneIndex = normalized.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (cloneIndex >= 0)
            {
                normalized = normalized.Substring(0, cloneIndex).Trim();
            }

            return normalized;
        }

        private static bool TryApplyReplicationPuppetActionHandItemVisual(string entityId, object view, string actionHandItemId, out string detail)
        {
            actionHandItemId = NormalizeReplicationActionHandItemId(actionHandItemId);
            if (string.IsNullOrWhiteSpace(actionHandItemId))
            {
                detail = "handItem=empty";
                return true;
            }

            if (ReplicationPuppetActionHandItemByEntityId.TryGetValue(entityId, out var currentHandItemId)
                && string.Equals(currentHandItemId, actionHandItemId, StringComparison.Ordinal)
                && ReplicationPuppetActionHandPropByEntityId.TryGetValue(entityId, out var currentProp)
                && currentProp != null)
            {
                currentProp.SetActive(true);
                TryInvokeReplicationBodyPreviewNoArgMethod(view, "HideWeapons", out var hideExistingDetail);
                detail = "handItem=already-prop id=" + actionHandItemId + " active=" + currentProp.activeSelf.ToString(CultureInfo.InvariantCulture) + " " + hideExistingDetail;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(currentHandItemId))
            {
                ClearReplicationPuppetActionHandItemVisual(entityId, view);
            }

            if (!TryResolveReplicationRightHandSocket(view, out var socket, out var socketDetail) || socket == null)
            {
                detail = "handItem-socket-missing id=" + actionHandItemId + " " + socketDetail;
                return false;
            }

            var created = false;
            var sourceDetail = "source=socket-child";
            if (!TryFindReplicationDirectChild(socket, actionHandItemId, out var prop) || prop == null)
            {
                prop = TryCreateReplicationActionHandProp(actionHandItemId, out sourceDetail);
                if (prop == null)
                {
                    TryInvokeReplicationBodyPreviewNoArgMethod(view, "HideWeapons", out var hideMissingDetail);
                    detail = "handItem-prop-missing id=" + actionHandItemId + " " + socketDetail + " " + sourceDetail + " " + hideMissingDetail;
                    return false;
                }

                created = true;
                prop.transform.SetParent(socket, false);
                prop.name = actionHandItemId;
                prop.transform.localPosition = Vector3.zero;
                prop.transform.localRotation = Quaternion.identity;
                if (prop.transform.localScale == Vector3.zero)
                {
                    prop.transform.localScale = Vector3.one;
                }
            }

            prop.SetActive(true);
            ReplicationPuppetActionHandItemByEntityId[entityId] = actionHandItemId;
            ReplicationPuppetActionHandPropByEntityId[entityId] = prop;
            if (created)
            {
                ReplicationPuppetActionHandPropCreatedByEntityId.Add(entityId);
            }
            else
            {
                ReplicationPuppetActionHandPropCreatedByEntityId.Remove(entityId);
            }

            TryInvokeReplicationBodyPreviewNoArgMethod(view, "HideWeapons", out var hideDetail);
            detail = "handItem=prop id=" + actionHandItemId
                + " created=" + created.ToString(CultureInfo.InvariantCulture)
                + " childCount=" + socket.childCount.ToString(CultureInfo.InvariantCulture)
                + " " + socketDetail + " " + sourceDetail + " " + hideDetail;
            return true;
        }

        private static bool TryResolveReplicationRightHandSocket(object view, out Transform? socket, out string detail)
        {
            socket = null;
            if ((TryReadInstanceMemberValue(view, "BodyPreview", out var bodyPreview) && bodyPreview != null)
                || (TryReadInstanceMemberValue(view, "bodyPreview", out bodyPreview) && bodyPreview != null))
            {
                if (TryReadInstanceMemberValue(bodyPreview, "RightHandSocket", out var candidate) && candidate is Transform transform)
                {
                    socket = transform;
                    detail = "socket=BodyPreview.RightHandSocket";
                    return true;
                }

                if (TryReadInstanceMemberValue(bodyPreview, "rightHandSocket", out candidate) && candidate is Transform fieldTransform)
                {
                    socket = fieldTransform;
                    detail = "socket=BodyPreview.rightHandSocket";
                    return true;
                }
            }

            detail = "right-hand-socket-missing viewType=" + FormatShortTypeName(view.GetType());
            return false;
        }

        private static bool TryFindReplicationDirectChild(Transform socket, string childName, out GameObject? child)
        {
            for (var i = 0; i < socket.childCount; i++)
            {
                var candidate = socket.GetChild(i);
                if (candidate != null
                    && candidate.gameObject != null
                    && string.Equals(NormalizeReplicationActionHandItemId(candidate.name), childName, StringComparison.Ordinal))
                {
                    child = candidate.gameObject;
                    return true;
                }
            }

            child = null;
            return false;
        }

        private static GameObject? TryCreateReplicationActionHandProp(string actionHandItemId, out string detail)
        {
            var loaded = Resources.Load<GameObject>(actionHandItemId)
                ?? Resources.Load<GameObject>("Prefabs/" + actionHandItemId)
                ?? Resources.Load<GameObject>("Resources/" + actionHandItemId);
            if (loaded != null)
            {
                detail = "source=Resources.Load(" + actionHandItemId + ")";
                return UnityEngine.Object.Instantiate(loaded);
            }

            GameObject? exactSceneCandidate = null;
            var candidates = Resources.FindObjectsOfTypeAll<GameObject>();
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (candidate == null
                    || candidate.transform == null
                    || !string.Equals(NormalizeReplicationActionHandItemId(candidate.name), actionHandItemId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!candidate.scene.IsValid())
                {
                    detail = "source=loaded-prefab name=" + candidate.name;
                    return UnityEngine.Object.Instantiate(candidate);
                }

                exactSceneCandidate ??= candidate;
            }

            if (exactSceneCandidate != null)
            {
                detail = "source=loaded-scene-clone name=" + exactSceneCandidate.name;
                return UnityEngine.Object.Instantiate(exactSceneCandidate);
            }

            detail = "source-missing id=" + actionHandItemId + " scanned=" + candidates.Length.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        private static string ClearReplicationPuppetActionHandItemVisual(string entityId, object view)
        {
            if (!ReplicationPuppetActionHandItemByEntityId.TryGetValue(entityId, out var actionHandItemId)
                || string.IsNullOrWhiteSpace(actionHandItemId))
            {
                return "handItem=none";
            }

            ReplicationPuppetActionHandItemByEntityId.Remove(entityId);
            var created = ReplicationPuppetActionHandPropCreatedByEntityId.Remove(entityId);
            if (!ReplicationPuppetActionHandPropByEntityId.TryGetValue(entityId, out var prop) || prop == null)
            {
                TryInvokeReplicationBodyPreviewNoArgMethod(view, "HideWeapons", out var hideMissingDetail);
                return "handItem-clear-prop-missing id=" + actionHandItemId + " " + hideMissingDetail;
            }

            ReplicationPuppetActionHandPropByEntityId.Remove(entityId);
            if (created)
            {
                UnityEngine.Object.Destroy(prop);
            }
            else
            {
                prop.SetActive(false);
            }

            TryInvokeReplicationBodyPreviewNoArgMethod(view, "HideWeapons", out var hideDetail);
            return "handItem=cleared id=" + actionHandItemId + " created=" + created.ToString(CultureInfo.InvariantCulture) + " " + hideDetail;
        }

        private static void ClearReplicationPuppetActionHandProps()
        {
            foreach (var pair in ReplicationPuppetActionHandPropByEntityId)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                if (ReplicationPuppetActionHandPropCreatedByEntityId.Contains(pair.Key))
                {
                    UnityEngine.Object.Destroy(pair.Value);
                }
                else
                {
                    pair.Value.SetActive(false);
                }
            }

            ReplicationPuppetActionHandPropByEntityId.Clear();
            ReplicationPuppetActionHandPropCreatedByEntityId.Clear();
        }

        private static bool TryInvokeReplicationBodyPreviewNoArgMethod(object view, string methodName, out string detail)
        {
            if ((!TryReadInstanceMemberValue(view, "BodyPreview", out var bodyPreview) || bodyPreview == null)
                && (!TryReadInstanceMemberValue(view, "bodyPreview", out bodyPreview) || bodyPreview == null))
            {
                detail = "bodyPreview-missing method=" + methodName;
                return false;
            }

            var method = FindReplicationInstanceMethod(bodyPreview.GetType(), methodName, Type.EmptyTypes);
            if (method == null)
            {
                detail = "bodyPreview-method-missing method=" + methodName + " bodyPreviewType=" + FormatShortTypeName(bodyPreview.GetType());
                return false;
            }

            try
            {
                method.Invoke(bodyPreview, null);
                detail = "bodyPreview." + methodName;
                return true;
            }
            catch (Exception ex)
            {
                detail = "bodyPreview." + methodName + " " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string ResolveReplicationPuppetAnimationToken(string goalId, string statusText)
        {
            var value = string.IsNullOrWhiteSpace(goalId) ? statusText : goalId;
            switch (value)
            {
                case "ChopTreeGoal":
                    return "Mining";
                case "HarvestGoal":
                case "HarvestPlantGoal":
                    return "Harvest";
                case "MineGoal":
                case "MiningGoal":
                case "DigGoal":
                case "DiggingGoal":
                    return "Mining";
                case "BuildGoal":
                case "ConstructGoal":
                case "ConstructionGoal":
                case "ConstructBuildingGoal":
                    return "Build";
                case "HaulGoal":
                case "StoreResourceGoal":
                    return "PickUpPile";
                case "EquipItemGoal":
                case "EquipGoal":
                    return "ItemPickup";
                default:
                    return string.Empty;
            }
        }

        private static bool IsReplicationIdleActionStatus(string goalId, string statusText)
        {
            return string.Equals(goalId, "IdleGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "Idle", StringComparison.Ordinal)
                || string.Equals(statusText, "Idle", StringComparison.Ordinal);
        }

        private static bool TryApplyReplicationAgentProgressDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "agent-progress-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            var overlayTypeName = TryReadReplicationWorldObjectDetailToken(delta.Detail, "overlay", out var parsedOverlayType)
                ? parsedOverlayType
                : delta.BlueprintId;
            if (string.IsNullOrWhiteSpace(overlayTypeName))
            {
                detail = "agent-progress-overlay-missing entityId=" + entityId;
                return false;
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "agent-progress-view-lookup-failed " + viewDetail + " entityId=" + entityId;
                return false;
            }

            if (!TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out var owner) || owner == null)
            {
                detail = "agent-progress-owner-missing entityId=" + entityId + " " + viewDetail;
                return false;
            }

            var overlayType = AccessTools.TypeByName("NSMedieval.FloatingOverlaySystem.OverlayProgressBarType");
            if (overlayType == null)
            {
                detail = "agent-progress-overlay-type-missing entityId=" + entityId;
                return false;
            }

            object overlayValue;
            try
            {
                overlayValue = Enum.Parse(overlayType, overlayTypeName, ignoreCase: true);
            }
            catch
            {
                detail = "agent-progress-overlay-parse-failed entityId="
                    + entityId
                    + " overlay="
                    + overlayTypeName
                    + " "
                    + viewDetail;
                return false;
            }

            var getProgressBar = FindReplicationInstanceMethod(owner.GetType(), "GetProgressBar", new[] { overlayType });
            var destroyProgressBar = FindReplicationInstanceMethod(owner.GetType(), "DestroyProgressBar", new[] { overlayType });
            if (string.Equals(delta.DeltaKind, "AgentProgressCleared", StringComparison.Ordinal))
            {
                if (destroyProgressBar == null)
                {
                    detail = "agent-progress-destroy-bar-missing entityId=" + entityId + " ownerType=" + FormatShortTypeName(owner.GetType());
                    return false;
                }

                applyingRuntimeCommandDepth++;
                try
                {
                    destroyProgressBar.Invoke(owner, new[] { overlayValue });
                    detail = "ok agent-progress-cleared entityId="
                        + entityId
                        + " overlay="
                        + overlayTypeName
                        + " "
                        + viewDetail;
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "agent-progress-clear-error " + FormatReflectionExceptionDetail(ex) + " entityId=" + entityId + " " + viewDetail;
                    return false;
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }
            }

            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "progressPermille", out var progressPermille))
            {
                detail = "agent-progress-value-missing entityId=" + entityId + " detail=" + delta.Detail;
                return false;
            }

            if (getProgressBar == null)
            {
                detail = "agent-progress-get-bar-missing entityId=" + entityId + " ownerType=" + FormatShortTypeName(owner.GetType());
                return false;
            }

            applyingRuntimeCommandDepth++;
            try
            {
                var progressBar = getProgressBar.Invoke(owner, new[] { overlayValue });
                if (progressBar == null)
                {
                    detail = "agent-progress-bar-null entityId=" + entityId + " overlay=" + overlayTypeName + " " + viewDetail;
                    return false;
                }

                var updateMethod = FindReplicationInstanceMethod(progressBar.GetType(), "UpdateValue", new[] { typeof(float) });
                if (updateMethod == null)
                {
                    detail = "agent-progress-update-method-missing entityId="
                        + entityId
                        + " barType="
                        + FormatShortTypeName(progressBar.GetType());
                    return false;
                }

                var progress = Mathf.Clamp01(progressPermille / 1000f);
                updateMethod.Invoke(progressBar, new object[] { progress });
                if (progressPermille >= 1000 && destroyProgressBar != null)
                {
                    destroyProgressBar.Invoke(owner, new[] { overlayValue });
                }

                detail = "ok agent-progress-updated entityId="
                    + entityId
                    + " overlay="
                    + overlayTypeName
                    + " progressPermille="
                    + progressPermille.ToString(CultureInfo.InvariantCulture)
                    + (progressPermille >= 1000 && destroyProgressBar != null ? " cleared=yes" : string.Empty)
                    + " "
                    + viewDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "agent-progress-error " + FormatReflectionExceptionDetail(ex) + " entityId=" + entityId + " " + viewDetail;
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool TryApplyReplicationAgentActionStatusDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "agent-action-status-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            var goalId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "goalB64", out var goalToken)
                && TryDecodeReplicationDetailBase64(goalToken, out var decodedGoal)
                ? decodedGoal
                : delta.BlueprintId;
            var statusText = TryReadReplicationWorldObjectDetailToken(delta.Detail, "statusB64", out var statusToken)
                && TryDecodeReplicationDetailBase64(statusToken, out var decodedStatus)
                ? decodedStatus
                : goalId;
            var hasStarted = !TryReadReplicationWorldObjectDetailBool(delta.Detail, "hasStarted", out var parsedHasStarted)
                || parsedHasStarted;
            var animationToken = TryReadReplicationWorldObjectDetailToken(delta.Detail, "animationB64", out var animationTokenValue)
                && TryDecodeReplicationDetailBase64(animationTokenValue, out var decodedAnimationToken)
                ? decodedAnimationToken
                : ResolveReplicationPuppetAnimationToken(goalId, statusText);
            var actionHandItemId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "handItemB64", out var handItemToken)
                && TryDecodeReplicationDetailBase64(handItemToken, out var decodedHandItem)
                ? decodedHandItem
                : string.Empty;
            var ttlMs = TryReadReplicationWorldObjectDetailInt(delta.Detail, "ttlMs", out var parsedTtlMs)
                ? Math.Max(250, parsedTtlMs)
                : ReplicationPuppetActionTtlMs;
            var animatorStateDetail = ExtractReplicationAnimatorStateDetail(delta.Detail);
            var semanticUiOnly = replicationConfigSemanticAgentPresentation
                && IsReplicationSemanticMigratedWorkGoal(goalId);

            statusText = NormalizeReplicationAgentActionStatusText(statusText, hasStarted);
            var isHeartbeat = string.Equals(delta.DeltaKind, "AgentActionHeartbeat", StringComparison.Ordinal);
            var shouldTriggerAnimation = !isHeartbeat;
            var shouldApplyUi = !isHeartbeat;
            var shouldApplyVisual = !isHeartbeat;
            if (semanticUiOnly)
            {
                shouldTriggerAnimation = false;
                shouldApplyVisual = false;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationPuppetActionState previousPuppetState;
                if (!semanticUiOnly
                    && !shouldTriggerAnimation
                    && hasStarted
                    && !IsReplicationIdleActionStatus(goalId, statusText)
                    && ReplicationPuppetActionStateByEntityId.TryGetValue(entityId, out previousPuppetState))
                {
                    shouldTriggerAnimation = !string.Equals(previousPuppetState.GoalId, goalId, StringComparison.Ordinal)
                        || !string.Equals(previousPuppetState.AnimationToken, animationToken, StringComparison.Ordinal)
                        || !string.Equals(previousPuppetState.ActionHandItemId, actionHandItemId, StringComparison.Ordinal);
                    shouldApplyUi = shouldTriggerAnimation;
                    shouldApplyVisual = shouldTriggerAnimation;
                }
                else if (!semanticUiOnly && isHeartbeat)
                {
                    shouldApplyUi = hasStarted && !IsReplicationIdleActionStatus(goalId, statusText);
                    shouldApplyVisual = shouldApplyUi;
                }

                ReplicationAgentActionStatusByEntityId[entityId] = new ReplicationAgentActionStatus(
                    goalId,
                    statusText,
                    hasStarted,
                    Time.realtimeSinceStartup,
                    animationToken,
                    animatorStateDetail,
                    actionHandItemId);

                if (semanticUiOnly)
                {
                    ReplicationPuppetActionStateByEntityId.Remove(entityId);
                }
                else if (hasStarted && !IsReplicationIdleActionStatus(goalId, statusText))
                {
                    if (isHeartbeat
                        && !shouldTriggerAnimation
                        && ReplicationPuppetActionStateByEntityId.TryGetValue(entityId, out previousPuppetState))
                    {
                        previousPuppetState.ExpiresRealtime = Time.realtimeSinceStartup + (ttlMs / 1000f);
                    }
                    else
                    {
                        ReplicationPuppetActionStateByEntityId[entityId] = new ReplicationPuppetActionState(
                            goalId,
                            statusText,
                            animationToken,
                            animatorStateDetail,
                            actionHandItemId,
                            Time.realtimeSinceStartup + (ttlMs / 1000f));
                    }
                }
                else
                {
                    ReplicationPuppetActionStateByEntityId.Remove(entityId);
                }
            }

            var uiDetail = shouldApplyUi
                ? ApplyReplicationAgentActionStatusToVisibleWorkerEntries(entityId, statusText)
                : "unchanged";
            var uiSurfaceProbe = replicationConfigCharacterStateDiagnostics
                && string.Equals(delta.DeltaKind, "AgentActionStatus", StringComparison.Ordinal)
                ? " uiSurfaces=" + BuildReplicationClientActionUiSurfaceProbe(entityId)
                : string.Empty;
            var visualDetail = hasStarted && !IsReplicationIdleActionStatus(goalId, statusText)
                && shouldApplyVisual
                ? ApplyReplicationPuppetActionVisual(entityId, animationToken, animatorStateDetail, actionHandItemId, force: shouldTriggerAnimation, triggerAnimation: shouldTriggerAnimation)
                : !hasStarted || IsReplicationIdleActionStatus(goalId, statusText)
                    ? ClearReplicationPuppetActionVisual(entityId)
                    : "unchanged";
            var playbackDetail = semanticUiOnly
                ? "semantic-ui-only"
                : TryStartReplicationHostDrivenGoapPlayback(entityId, goalId, hasStarted, isHeartbeat);

            detail = "ok agent-action-status entityId="
                + entityId
                + " goal="
                + FormatReplicationWorldObjectDetailToken(goalId)
                + " status="
                + FormatReplicationWorldObjectDetailToken(statusText)
                + " hasStarted="
                + (hasStarted ? "true" : "false")
                + " ui="
                + uiDetail
                + uiSurfaceProbe
                + " visual="
                + visualDetail
                + " playback="
                + playbackDetail;
            return true;
        }

        private static bool TryApplyReplicationGoapActionPhaseDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out var phase)
                || string.IsNullOrWhiteSpace(entityId)
                || string.IsNullOrWhiteSpace(phase))
            {
                detail = "agent-action-phase-missing detail=" + delta.Detail;
                return false;
            }

            var actionId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "actionB64", out var encodedAction)
                && TryDecodeReplicationDetailBase64(encodedAction, out var decodedAction)
                ? decodedAction
                : delta.BlueprintId;
            var goalId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "goalB64", out var encodedGoal)
                && TryDecodeReplicationDetailBase64(encodedGoal, out var decodedGoal)
                ? decodedGoal
                : string.Empty;
            var targetId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "targetId", out var parsedTargetId)
                ? parsedTargetId
                : "0";
            var targetBlueprintId = TryReadReplicationWorldObjectDetailToken(delta.Detail, "targetBlueprintId", out var parsedTargetBlueprint)
                ? parsedTargetBlueprint
                : string.Empty;
            var animatorStateDetail = ExtractReplicationAnimatorStateDetail(delta.Detail);

            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationGoapActionPhaseByEntityId[entityId] = new ReplicationGoapActionPhase(
                    actionId,
                    goalId,
                    phase,
                    targetId,
                    targetBlueprintId,
                    delta.GridX,
                    delta.GridY,
                    delta.GridZ,
                    Time.realtimeSinceStartup);
            }

            var visualReplayDetail = TryReplayReplicationActionPhaseVisual(entityId, actionId, goalId, phase, animatorStateDetail);

            detail = "ok agent-action-phase entityId=" + entityId
                + " phase=" + phase
                + " action=" + FormatReplicationWorldObjectDetailToken(actionId)
                + " goal=" + FormatReplicationWorldObjectDetailToken(goalId)
                + " targetId=" + targetId
                + " targetBlueprintId=" + FormatReplicationWorldObjectDetailToken(targetBlueprintId)
                + " targetGrid=Vec3Int(" + delta.GridX.ToString(CultureInfo.InvariantCulture) + "," + delta.GridY.ToString(CultureInfo.InvariantCulture) + "," + delta.GridZ.ToString(CultureInfo.InvariantCulture) + ")"
                + " visualReplay=" + visualReplayDetail;
            return true;
        }

        private static string TryReplayReplicationActionPhaseVisual(string entityId, string actionId, string goalId, string phase, string animatorStateDetail)
        {
            if (!replicationConfigActionPhaseVisualReplay)
            {
                return "disabled";
            }

            if (!string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal)
                || !string.Equals(actionId, "StartObtaining", StringComparison.Ordinal)
                || !string.Equals(phase, "Init", StringComparison.Ordinal))
            {
                return "not-work-start";
            }

            ReplicationPuppetActionState puppetState;
            lock (ReplicationWorldObjectDeltaLock)
            {
                if (!ReplicationPuppetActionStateByEntityId.TryGetValue(entityId, out puppetState))
                {
                    return "action-status-missing";
                }
            }

            if (!string.Equals(puppetState.GoalId, goalId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(puppetState.AnimationToken))
            {
                return "action-status-incompatible";
            }

            return ApplyReplicationPuppetActionVisual(
                entityId,
                puppetState.AnimationToken,
                string.Empty,
                puppetState.ActionHandItemId,
                force: true,
                triggerAnimation: true);
        }

        private static bool TryApplyReplicationAgentSkillExperienceDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "agent-skill-xp-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            var skillToken = TryReadReplicationWorldObjectDetailToken(delta.Detail, "skill", out var parsedSkill)
                ? parsedSkill
                : delta.BlueprintId;
            if (string.IsNullOrWhiteSpace(skillToken) || string.Equals(skillToken, "_", StringComparison.Ordinal))
            {
                detail = "agent-skill-xp-skill-missing entityId=" + entityId;
                return false;
            }

            if (!TryReadReplicationWorldObjectDetailFloat(delta.Detail, "totalXp", out var totalExperience))
            {
                detail = "agent-skill-xp-total-missing entityId=" + entityId + " skill=" + skillToken;
                return false;
            }

            if (!TryFindReplicationAgentOwnerByEntityId(entityId, out var owner, out var ownerDetail) || owner == null)
            {
                detail = "agent-skill-xp-owner-lookup-failed entityId=" + entityId + " " + ownerDetail;
                return false;
            }

            if (!TryResolveReplicationSkillType(skillToken, out var skillType, out var skillTypeDetail) || skillType == null)
            {
                detail = "agent-skill-xp-skill-resolve-failed entityId=" + entityId + " skill=" + skillToken + " " + skillTypeDetail;
                return false;
            }

            if ((!TryReadInstanceMemberValue(owner, "Skills", out var skills) || skills == null)
                && (!TryInvokeReplicationObjectMethod(owner, "get_Skills", out skills) || skills == null))
            {
                detail = "agent-skill-xp-skills-missing entityId=" + entityId + " " + ownerDetail;
                return false;
            }

            var workerSkill = TryInvokeReplicationObjectMethod(skills, "GetSkill", new[] { skillType }, out var resolvedSkill)
                ? resolvedSkill
                : null;
            if (workerSkill == null)
            {
                detail = "agent-skill-xp-worker-skill-missing entityId=" + entityId + " skill=" + skillToken + " " + ownerDetail;
                return false;
            }

            var setExperience = FindReplicationInstanceMethod(workerSkill.GetType(), "SetExperience", new[] { typeof(float) });
            if (setExperience == null)
            {
                detail = "agent-skill-xp-setter-missing entityId=" + entityId + " skillType=" + FormatShortTypeName(workerSkill.GetType());
                return false;
            }

            try
            {
                setExperience.Invoke(workerSkill, new object[] { totalExperience });
                RefreshReplicationSkillUiForEntity(entityId);
                instance?.LogReplicationInfo("Going Cooperative replication character xp applied entityId="
                    + entityId
                    + " skill="
                    + skillToken
                    + " totalXp="
                    + totalExperience.ToString("F3", CultureInfo.InvariantCulture));
                detail = "ok agent-skill-xp entityId="
                    + entityId
                    + " skill="
                    + skillToken
                    + " totalXp="
                    + totalExperience.ToString("F3", CultureInfo.InvariantCulture)
                    + " "
                    + ownerDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "agent-skill-xp-set-failed entityId="
                    + entityId
                    + " skill="
                    + skillToken
                    + " error="
                    + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryApplyReplicationAgentCharacterStateDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "character-state-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            TryReadReplicationWorldObjectDetailBool(delta.Detail, "dead", out var hasDied);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "fainted", out var hasFainted);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "sleeping", out var isSleeping);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "wounded", out var isWounded);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "bleeding", out var isBleeding);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "beingCarried", out var isBeingCarried);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "idle", out var isIdle);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "resting", out var isResting);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "drafting", out var isDrafting);
            var combatMode = string.Empty;
            if (TryReadReplicationWorldObjectDetailToken(delta.Detail, "combatB64", out var combatToken)
                && TryDecodeReplicationDetailBase64(combatToken, out var decodedCombatMode))
            {
                combatMode = decodedCombatMode;
            }

            TryReadReplicationWorldObjectDetailHex(delta.Detail, "statsSig", out var statsSignature);
            TryReadReplicationWorldObjectDetailHex(delta.Detail, "attrsSig", out var attributesSignature);
            TryReadReplicationWorldObjectDetailHex(delta.Detail, "skillsSig", out var skillsSignature);
            TryReadReplicationWorldObjectDetailHex(delta.Detail, "sig", out var signature);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "hasHunger", out var hasHunger);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "hasSleep", out var hasSleep);
            var hungerCurrent = TryReadReplicationWorldObjectDetailFloat(delta.Detail, "hungerCurrent", out var parsedHunger)
                ? parsedHunger
                : 0f;
            var sleepCurrent = TryReadReplicationWorldObjectDetailFloat(delta.Detail, "sleepCurrent", out var parsedSleep)
                ? parsedSleep
                : 0f;
            var state = new ReplicationAgentCharacterState(
                entityId,
                delta.UniqueId,
                string.Empty,
                hasDied,
                hasFainted,
                isSleeping,
                isWounded,
                isBleeding,
                isBeingCarried,
                isIdle,
                isResting,
                isDrafting,
                combatMode,
                hasHunger,
                hungerCurrent,
                hasSleep,
                sleepCurrent,
                statsSignature,
                attributesSignature,
                skillsSignature,
                signature);

            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationClientAgentCharacterStateByEntityId[entityId] = state;
            }

            var needsDetail = "needs=gated-off";
            if (replicationConfigNeedsReplication)
            {
                TryApplyReplicationNeedsRepair(entityId, hasHunger, hungerCurrent, hasSleep, sleepCurrent, isSleeping, out needsDetail);
            }

            detail = "ok agent-character-state-observed entityId="
                + entityId
                + " dead="
                + FormatReplicationBool(hasDied)
                + " fainted="
                + FormatReplicationBool(hasFainted)
                + " sleeping="
                + FormatReplicationBool(isSleeping)
                + " idle="
                + FormatReplicationBool(isIdle)
                + " statsSig=0x"
                + statsSignature.ToString("X16", CultureInfo.InvariantCulture)
                + " attrsSig=0x"
                + attributesSignature.ToString("X16", CultureInfo.InvariantCulture)
                + " skillsSig=0x"
                + skillsSignature.ToString("X16", CultureInfo.InvariantCulture)
                + " sig=0x"
                + signature.ToString("X16", CultureInfo.InvariantCulture)
                + " "
                + needsDetail;
            return true;
        }

        private static bool TryApplyReplicationAgentCarryDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "carry-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "carry-view-lookup-failed " + viewDetail + " entityId=" + entityId;
                return false;
            }

            if (string.IsNullOrWhiteSpace(delta.BlueprintId))
            {
                detail = "carry-blueprint-id-empty entityId=" + entityId + " " + viewDetail;
                return false;
            }

            if (!TryResolveReplicationCarryVisualEquipment(delta.BlueprintId, out var equipment, out var equipmentDetail) || equipment == null)
            {
                detail = "carry-equipment-lookup-failed " + equipmentDetail + " entityId=" + entityId + " " + viewDetail;
                return false;
            }

            var methodName = string.Equals(delta.DeltaKind, "AgentCarryCleared", StringComparison.Ordinal)
                ? "DropItem"
                : "EquipItem";
            if (string.Equals(methodName, "EquipItem", StringComparison.Ordinal)
                && ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var currentBlueprintId)
                && string.Equals(currentBlueprintId, delta.BlueprintId, StringComparison.Ordinal))
            {
                detail = "ok carry-equipment-already-equipped entityId=" + entityId + " blueprintId=" + delta.BlueprintId + " " + equipmentDetail + " " + viewDetail;
                return true;
            }

            var equipmentStateDetail = "equipment-state=not-attempted";
            if (string.Equals(methodName, "EquipItem", StringComparison.Ordinal)
                && TryMirrorReplicationAgentEquipmentEquipped(view, delta.BlueprintId, out equipmentStateDetail))
            {
                ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = delta.BlueprintId;
                ReplicationAgentCarryResourceAmountByEntityId[entityId] = 1;
                detail = "ok via=InventoryInstance.Equip entityId="
                    + entityId
                    + " blueprintId="
                    + delta.BlueprintId
                    + " "
                    + equipmentStateDetail
                    + " "
                    + equipmentDetail
                    + " "
                    + viewDetail;
                return true;
            }

            if (!TryInvokeReplicationCarryVisualMethod(view, methodName, equipment, out var invokeDetail))
            {
                detail = "carry-method-failed " + equipmentStateDetail + " " + invokeDetail + " " + equipmentDetail + " entityId=" + entityId + " " + viewDetail;
                return false;
            }

            if (string.Equals(methodName, "EquipItem", StringComparison.Ordinal))
            {
                ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = delta.BlueprintId;
                ReplicationAgentCarryResourceAmountByEntityId[entityId] = 1;
            }
            else
            {
                ReplicationAgentCarryResourceBlueprintByEntityId.Remove(entityId);
                ReplicationAgentCarryResourceAmountByEntityId.Remove(entityId);
                ClearReplicationAgentCarryResourceVisual(entityId);
            }

            detail = "ok via=HumanoidView." + methodName + " entityId=" + entityId + " " + equipmentStateDetail + " " + invokeDetail + " " + equipmentDetail + " " + viewDetail;
            return true;
        }

        private static bool TryApplyReplicationAgentCarryResourceDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (replicationConfigResourceContainerReplication)
            {
                detail = "ignored-resource-container-authoritative";
                return true;
            }

            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "carry-resource-entity-id-missing detail=" + delta.Detail;
                return false;
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "carry-resource-view-lookup-failed " + viewDetail + " entityId=" + entityId;
                return false;
            }

            var hasAuthoritativeCarryAmount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "carryAmount", out var parsedCarryAmount)
                && parsedCarryAmount >= 0;
            var deltaAmount = hasAuthoritativeCarryAmount
                ? parsedCarryAmount
                : TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var parsedDeltaAmount)
                ? Math.Max(1, parsedDeltaAmount)
                : 1;
            var isInferredAmountCorrection = delta.Detail.IndexOf("inferred-amount-for=", StringComparison.Ordinal) >= 0;
            var isProvisionalInferredCarry = !isInferredAmountCorrection
                && delta.Detail.IndexOf("inferred-from=", StringComparison.Ordinal) >= 0
                && (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var inferredDetailAmount) || inferredDetailAmount <= 0);
            if (isProvisionalInferredCarry)
            {
                detail = "ok carry-resource-provisional-ignored-no-mutation entityId="
                    + entityId
                    + " blueprintId="
                    + delta.BlueprintId
                    + " "
                    + viewDetail;
                return true;
            }

            if (string.Equals(delta.DeltaKind, "AgentCarryResourceCleared", StringComparison.Ordinal))
            {
                var pileAddDetail = "pile-add=not-attempted";
                var shouldMirrorExistingPileAdd = !string.IsNullOrWhiteSpace(delta.BlueprintId)
                    && (delta.GridX != 0 || delta.GridY != 0 || delta.GridZ != 0)
                    && TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var clearDeltaAmount)
                    && clearDeltaAmount > 0;
                if (shouldMirrorExistingPileAdd)
                {
                    var spawnLocationKey = FormatReplicationWorldObjectDeltaLocationKey(delta);
                    var hasAuthoritativePileAmount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "pileAmount", out var authoritativePileAmount)
                        && authoritativePileAmount > 0;
                    if (delta.Detail.IndexOf("drop-amount-for=", StringComparison.Ordinal) >= 0
                        && WasReplicationWorldObjectSpawnLocationRecentlyApplied(spawnLocationKey))
                    {
                        pileAddDetail = "pile-add-skipped-covered-by-spawn key=" + spawnLocationKey;
                    }
                    else if (!hasAuthoritativePileAmount)
                    {
                        pileAddDetail = "pile-add-skipped-no-authoritative-pile-state key=" + spawnLocationKey;
                    }
                    else
                    {
                        var pileAddApplied = TryApplyReplicationResourcePileAmountAdded(delta, out pileAddDetail);
                        if (!pileAddApplied)
                        {
                            pileAddDetail = "pile-add-failed " + pileAddDetail;
                        }
                    }
                }

                var hasPreviousBlueprint = ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var previousBlueprintId)
                    && !string.IsNullOrWhiteSpace(previousBlueprintId);
                var clearBlueprintId = hasPreviousBlueprint
                    ? previousBlueprintId
                    : delta.BlueprintId;
                if (string.IsNullOrWhiteSpace(clearBlueprintId))
                {
                    ReplicationAgentCarryResourceAmountByEntityId.Remove(entityId);
                    ClearReplicationAgentCarryResourceVisual(entityId);
                    detail = "ok carry-resource-clear-no-known-blueprint " + pileAddDetail + " entityId=" + entityId + " " + viewDetail;
                    return true;
                }

                var previousAmount = ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var cachedAmount)
                    ? Math.Max(1, cachedAmount)
                    : deltaAmount;
                var clearAmount = TryReadReplicationWorldObjectDetailInt(delta.Detail, "amount", out var parsedClearAmount) && parsedClearAmount > 0
                    ? Math.Min(previousAmount, parsedClearAmount)
                    : previousAmount;
                var remainingAmount = Math.Max(0, previousAmount - clearAmount);
                var storageClearDetail = "storage-clear=not-attempted";
                TryClearReplicationAgentCarryStorage(view, clearBlueprintId, clearAmount, out storageClearDetail);

                if (remainingAmount > 0)
                {
                    ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = clearBlueprintId;
                    ReplicationAgentCarryResourceAmountByEntityId[entityId] = remainingAmount;
                    detail = "ok carry-resource-clear-partial-cosmetic "
                        + storageClearDetail
                        + " "
                        + pileAddDetail
                        + " entityId="
                        + entityId
                        + " blueprintId="
                        + clearBlueprintId
                        + " clearedAmount="
                        + clearAmount.ToString(CultureInfo.InvariantCulture)
                        + " remainingAmount="
                        + remainingAmount.ToString(CultureInfo.InvariantCulture)
                        + " "
                        + viewDetail;
                    return true;
                }

                if (!TryResolveReplicationResourceEquipment(clearBlueprintId, out var previousEquipment, out var previousDetail) || previousEquipment == null)
                {
                    ReplicationAgentCarryResourceBlueprintByEntityId.Remove(entityId);
                    ReplicationAgentCarryResourceAmountByEntityId.Remove(entityId);
                    ClearReplicationAgentCarryResourceVisual(entityId);
                    detail = "ok carry-resource-clear-cosmetic " + storageClearDetail + " " + previousDetail + " entityId=" + entityId + " " + viewDetail;
                    if (!string.Equals(pileAddDetail, "pile-add=not-attempted", StringComparison.Ordinal))
                    {
                        detail = "ok carry-resource-clear-cosmetic " + pileAddDetail + " " + storageClearDetail + " " + previousDetail + " entityId=" + entityId + " " + viewDetail;
                    }
                    return true;
                }

                if (!TryInvokeReplicationCarryVisualMethod(view, "DropItem", previousEquipment, out var dropDetail))
                {
                    detail = "carry-resource-drop-failed " + storageClearDetail + " " + dropDetail + " " + previousDetail + " entityId=" + entityId + " " + viewDetail;
                    return false;
                }

                ReplicationAgentCarryResourceBlueprintByEntityId.Remove(entityId);
                ReplicationAgentCarryResourceAmountByEntityId.Remove(entityId);
                ClearReplicationAgentCarryResourceVisual(entityId);
                detail = "ok via=HumanoidView.DropItem resourceBlueprintId=" + clearBlueprintId + " " + pileAddDetail + " " + storageClearDetail + " " + previousDetail + " entityId=" + entityId + " " + viewDetail;
                return true;
            }

            if (string.IsNullOrWhiteSpace(delta.BlueprintId))
            {
                detail = "carry-resource-blueprint-id-empty entityId=" + entityId + " " + viewDetail;
                return false;
            }

            if (ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var currentBlueprintId)
                && string.Equals(currentBlueprintId, delta.BlueprintId, StringComparison.Ordinal))
            {
                var currentAmount = ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var cachedCurrentAmount)
                    ? Math.Max(1, cachedCurrentAmount)
                    : 1;
                if (currentAmount != deltaAmount)
                {
                    if (isProvisionalInferredCarry && currentAmount > deltaAmount)
                    {
                        detail = "ok carry-resource-provisional-ignored entityId="
                            + entityId
                            + " blueprintId="
                            + delta.BlueprintId
                            + " currentAmount="
                            + currentAmount.ToString(CultureInfo.InvariantCulture)
                            + " provisionalAmount="
                            + deltaAmount.ToString(CultureInfo.InvariantCulture)
                            + " "
                            + viewDetail;
                        return true;
                    }

                    var shouldAccumulateAmount = !hasAuthoritativeCarryAmount && isInferredAmountCorrection && currentAmount > 1;
                    var newAmount = shouldAccumulateAmount
                        ? currentAmount + deltaAmount
                        : deltaAmount;
                    var sameBlueprintClearDetail = "storage-clear=not-attempted";
                    if (!shouldAccumulateAmount)
                    {
                        TryClearReplicationAgentCarryStorage(view, delta.BlueprintId, currentAmount, out sameBlueprintClearDetail);
                    }

                    var sameBlueprintStorageApplied = TryMirrorReplicationAgentCarryStorage(view, delta.BlueprintId, deltaAmount, out var sameBlueprintStorageDetail);
                    ReplicationAgentCarryResourceAmountByEntityId[entityId] = newAmount;
                    detail = "ok carry-resource-amount-updated entityId="
                        + entityId
                        + " blueprintId="
                        + delta.BlueprintId
                        + " oldAmount="
                        + currentAmount.ToString(CultureInfo.InvariantCulture)
                        + " newAmount="
                        + newAmount.ToString(CultureInfo.InvariantCulture)
                        + " deltaAmount="
                        + deltaAmount.ToString(CultureInfo.InvariantCulture)
                        + " clear="
                        + sameBlueprintClearDetail
                        + " add="
                        + (sameBlueprintStorageApplied ? sameBlueprintStorageDetail : "storage-mirror-failed " + sameBlueprintStorageDetail)
                        + " "
                        + viewDetail;
                    return true;
                }

                detail = "ok carry-resource-already-equipped entityId=" + entityId + " blueprintId=" + delta.BlueprintId + " amount=" + deltaAmount.ToString(CultureInfo.InvariantCulture) + " " + viewDetail;
                return true;
            }

            if (ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var oldBlueprintId)
                && TryResolveReplicationResourceEquipment(oldBlueprintId, out var oldEquipment, out _)
                && oldEquipment != null)
            {
                TryInvokeReplicationCarryVisualMethod(view, "DropItem", oldEquipment, out _);
            }
            else
            {
                ClearReplicationAgentCarryResourceVisual(entityId);
            }

            if (ReplicationAgentCarryResourceBlueprintByEntityId.TryGetValue(entityId, out var oldStorageBlueprintId))
            {
                var oldAmount = ReplicationAgentCarryResourceAmountByEntityId.TryGetValue(entityId, out var cachedOldAmount)
                    ? Math.Max(1, cachedOldAmount)
                    : 1;
                TryClearReplicationAgentCarryStorage(view, oldStorageBlueprintId, oldAmount, out _);
            }

            var amount = deltaAmount;
            var storageApplied = TryMirrorReplicationAgentCarryStorage(view, delta.BlueprintId, amount, out var storageDetail);
            if (!storageApplied)
            {
                storageDetail = "storage-mirror-failed " + storageDetail;
            }

            if (!TryResolveReplicationResourceEquipment(delta.BlueprintId, out var equipment, out var equipmentDetail) || equipment == null)
            {
                if (TryApplyReplicationResourceCarryCosmeticVisual(entityId, view, delta.BlueprintId, out var cosmeticDetail))
                {
                    ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = delta.BlueprintId;
                    ReplicationAgentCarryResourceAmountByEntityId[entityId] = amount;
                    detail = "ok via=CarryBodySocket cosmetic " + storageDetail + " " + equipmentDetail + " " + cosmeticDetail + " entityId=" + entityId + " " + viewDetail;
                    return true;
                }

                detail = "carry-resource-visual-failed " + storageDetail + " " + equipmentDetail + " " + cosmeticDetail + " entityId=" + entityId + " " + viewDetail;
                return false;
            }

            if (!TryInvokeReplicationCarryVisualMethod(view, "EquipItem", equipment, out var equipDetail))
            {
                detail = "carry-resource-equip-failed " + equipDetail + " " + equipmentDetail + " entityId=" + entityId + " " + viewDetail;
                return false;
            }

            ReplicationAgentCarryResourceBlueprintByEntityId[entityId] = delta.BlueprintId;
            ReplicationAgentCarryResourceAmountByEntityId[entityId] = amount;
            detail = "ok via=HumanoidView.EquipItem resourceBlueprintId=" + delta.BlueprintId + " " + storageDetail + " " + equipmentDetail + " entityId=" + entityId + " " + viewDetail;
            return true;
        }

        private static bool TryMirrorReplicationAgentCarryStorage(object view, string blueprintId, int amount, out string detail)
        {
            if (!TryResolveReplicationAgentStorage(view, out var storage, out var storageDetail) || storage == null)
            {
                detail = storageDetail;
                return false;
            }

            if (!TryResolveReplicationResourceModel(blueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = resourceDetail + " " + storageDetail;
                return false;
            }

            if (!TryCreateReplicationResourceInstance(resource, amount, out var resourceInstance, out var instanceDetail) || resourceInstance == null)
            {
                detail = instanceDetail + " " + resourceDetail + " " + storageDetail;
                return false;
            }

            var addMethod = FindReplicationStorageAddMethod(storage.GetType(), resourceInstance, out var addArgs, out var addSignature);
            if (addMethod == null)
            {
                detail = "storage-add-method-missing storageType=" + (storage.GetType().FullName ?? storage.GetType().Name) + " resourceInstanceType=" + (resourceInstance.GetType().FullName ?? resourceInstance.GetType().Name) + " " + resourceDetail + " " + storageDetail;
                return false;
            }

            try
            {
                addMethod.Invoke(storage, addArgs);
                TryNotifyReplicationAgentStorageChanged(view, resource, amount, out var storageViewDetail);
                detail = "storage=Add amount=" + amount.ToString(CultureInfo.InvariantCulture) + " signature=" + addSignature + " " + storageViewDetail + " " + resourceDetail + " " + instanceDetail + " " + storageDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "storage=Add " + FormatReflectionExceptionDetail(ex) + " " + resourceDetail + " " + instanceDetail + " " + storageDetail;
                return false;
            }
        }

        private static bool TryClearReplicationAgentCarryStorage(object view, string blueprintId, int amount, out string detail)
        {
            if (!TryResolveReplicationAgentStorage(view, out var storage, out var storageDetail) || storage == null)
            {
                detail = storageDetail;
                return false;
            }

            if (!TryResolveReplicationResourceModel(blueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = resourceDetail + " " + storageDetail;
                return false;
            }

            var consumeMethod = FindReplicationInstanceMethod(storage.GetType(), "Consume", new[] { resource.GetType(), typeof(int) });
            if (consumeMethod == null)
            {
                detail = "storage-consume-method-missing storageType=" + (storage.GetType().FullName ?? storage.GetType().Name) + " resourceType=" + (resource.GetType().FullName ?? resource.GetType().Name) + " " + resourceDetail + " " + storageDetail;
                return false;
            }

            try
            {
                var result = consumeMethod.Invoke(storage, new object[] { resource, amount });
                TryNotifyReplicationAgentStorageChanged(view, resource, amount, out var storageViewDetail);
                detail = "storage=Consume amount=" + amount.ToString(CultureInfo.InvariantCulture) + " result=" + Convert.ToString(result, CultureInfo.InvariantCulture) + " " + storageViewDetail + " " + resourceDetail + " " + storageDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "storage=Consume " + FormatReflectionExceptionDetail(ex) + " " + resourceDetail + " " + storageDetail;
                return false;
            }
        }

        private static bool TryNotifyReplicationAgentStorageChanged(object view, object resource, int amount, out string detail)
        {
            var simpleResourceCountType = resource.GetType().Assembly.GetType("NSMedieval.State.SimpleResourceCount");
            if (simpleResourceCountType == null)
            {
                detail = "storage-view=count-type-missing";
                return false;
            }

            var constructor = simpleResourceCountType.GetConstructor(new[] { resource.GetType(), typeof(int) });
            if (constructor == null)
            {
                detail = "storage-view=count-ctor-missing";
                return false;
            }

            object? count;
            try
            {
                count = constructor.Invoke(new object[] { resource, amount });
            }
            catch (Exception ex)
            {
                detail = "storage-view=count-ctor " + FormatReflectionExceptionDetail(ex);
                return false;
            }

            var storageChangedMethod = FindReplicationInstanceMethod(view.GetType(), "OnStorageChange", new[] { simpleResourceCountType });
            if (storageChangedMethod != null)
            {
                try
                {
                    storageChangedMethod.Invoke(view, new[] { count });
                    detail = "storage-view=OnStorageChange";
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "storage-view=OnStorageChange " + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            if (TryInvokeReplicationObjectVoidMethod(view, "UpdateViewImmediate", out var refreshDetail))
            {
                detail = "storage-view=" + refreshDetail;
                return true;
            }

            detail = "storage-view=refresh-missing " + refreshDetail;
            return false;
        }

        private static bool TryResolveReplicationAgentStorage(object view, out object? storage, out string detail)
        {
            storage = null;
            if (!TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out var owner) || owner == null)
            {
                detail = "agent-owner-missing";
                return false;
            }

            if (TryReadInstanceMemberValue(owner, "Storage", out storage) && storage != null)
            {
                detail = "owner=" + FormatShortTypeName(owner.GetType()) + " storage=Storage";
                return true;
            }

            if (TryReadInstanceMemberValue(owner, "storage", out storage) && storage != null)
            {
                detail = "owner=" + FormatShortTypeName(owner.GetType()) + " storage=storage";
                return true;
            }

            detail = "storage-missing owner=" + FormatShortTypeName(owner.GetType());
            return false;
        }

        private static MethodInfo? FindReplicationStorageAddMethod(Type storageType, object resourceInstance, out object?[] addArgs, out string signature)
        {
            addArgs = Array.Empty<object?>();
            signature = "missing";
            var resourceInstanceType = resourceInstance.GetType();
            for (var current = storageType; current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    if (!string.Equals(method.Name, "Add", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ParameterInfo[] parameters;
                    try
                    {
                        parameters = method.GetParameters();
                    }
                    catch
                    {
                        continue;
                    }

                    if (parameters.Length < 1 || !parameters[0].ParameterType.IsAssignableFrom(resourceInstanceType))
                    {
                        continue;
                    }

                    var args = new object?[parameters.Length];
                    args[0] = resourceInstance;
                    var supported = true;
                    for (var parameterIndex = 1; parameterIndex < parameters.Length; parameterIndex++)
                    {
                        if (!TryGetReplicationDefaultArgument(parameters[parameterIndex], out args[parameterIndex]))
                        {
                            supported = false;
                            break;
                        }
                    }

                    if (supported)
                    {
                        addArgs = args;
                        signature = FormatReplicationMethodSignature(method, parameters);
                        return method;
                    }
                }
            }

            return null;
        }

        private static bool TryGetReplicationDefaultArgument(ParameterInfo parameter, out object? value)
        {
            value = null;
            if (parameter.HasDefaultValue)
            {
                value = parameter.DefaultValue;
                return true;
            }

            var parameterType = parameter.ParameterType;
            if (parameterType == typeof(bool))
            {
                value = false;
                return true;
            }

            if (parameterType == typeof(int))
            {
                value = 0;
                return true;
            }

            if (parameterType == typeof(float))
            {
                value = 0f;
                return true;
            }

            if (parameterType.IsEnum)
            {
                value = Enum.ToObject(parameterType, 0);
                return true;
            }

            if (!parameterType.IsValueType)
            {
                return true;
            }

            try
            {
                value = Activator.CreateInstance(parameterType);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatReplicationMethodSignature(MethodInfo method, ParameterInfo[] parameters)
        {
            var builder = new StringBuilder();
            builder.Append(method.Name).Append("(");
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append(FormatShortTypeName(parameters[i].ParameterType));
            }

            builder.Append(")");
            return builder.ToString();
        }

        private static bool TryApplyReplicationResourceCarryCosmeticVisual(string entityId, object view, string blueprintId, out string detail)
        {
            detail = "cosmetic-not-created";
            ClearReplicationAgentCarryResourceVisual(entityId);

            if (!TryResolveReplicationCarryBodySocket(view, out var socket, out var socketDetail) || socket == null)
            {
                detail = socketDetail;
                return false;
            }

            if (!TryResolveReplicationResourceModel(blueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = resourceDetail + " " + socketDetail;
                return false;
            }

            var visual = TryCreateReplicationCarryResourcePrefab(resource, blueprintId, out var prefabDetail);
            if (visual == null)
            {
                detail = resourceDetail + " " + socketDetail + " " + prefabDetail + " visual=skipped-no-prefab";
                return true;
            }

            visual.transform.SetParent(socket, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            if (visual.transform.localScale == Vector3.one)
            {
                visual.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            }

            ReplicationAgentCarryResourceVisualByEntityId[entityId] = visual;
            detail = resourceDetail + " " + socketDetail + " " + prefabDetail;
            return true;
        }

        private static bool TryResolveReplicationCarryBodySocket(object view, out Transform? socket, out string detail)
        {
            socket = null;
            if (TryReadInstanceMemberValue(view, "BodyPreview", out var bodyPreview) && bodyPreview != null)
            {
                if (TryReadInstanceMemberValue(bodyPreview, "CarryBodySocket", out var candidate) && candidate is Transform transform)
                {
                    socket = transform;
                    detail = "socket=BodyPreview.CarryBodySocket";
                    return true;
                }

                if (TryReadInstanceMemberValue(bodyPreview, "carryBodySocket", out candidate) && candidate is Transform fieldTransform)
                {
                    socket = fieldTransform;
                    detail = "socket=BodyPreview.carryBodySocket";
                    return true;
                }
            }

            if (view is MonoBehaviour behaviour)
            {
                socket = behaviour.transform;
                detail = "socket=view-transform-fallback";
                return true;
            }

            detail = "carry-socket-missing";
            return false;
        }

        private static GameObject? TryCreateReplicationCarryResourcePrefab(object resource, string blueprintId, out string detail)
        {
            detail = "prefab=missing";
            var prefabId = string.Empty;
            if (!TryReadReplicationWorldObjectStringMember(resource, "EquippedPrefabID", "equippedPrefabID", out prefabId)
                || string.IsNullOrWhiteSpace(prefabId))
            {
                TryReadReplicationWorldObjectStringMember(resource, "PrefabPileID", "prefabPileID", out prefabId);
            }

            if (!string.IsNullOrWhiteSpace(prefabId))
            {
                var loaded = Resources.Load<GameObject>(prefabId)
                    ?? Resources.Load<GameObject>("Prefabs/" + prefabId)
                    ?? Resources.Load<GameObject>("Resources/" + prefabId);
                if (loaded != null)
                {
                    var visual = UnityEngine.Object.Instantiate(loaded);
                    visual.name = "GCoop_Carry_" + blueprintId;
                    detail = "prefab=" + prefabId;
                    return visual;
                }

                detail = "prefab-not-loaded id=" + prefabId;
            }

            return null;
        }

        private static void ClearReplicationAgentCarryResourceVisual(string entityId)
        {
            if (ReplicationAgentCarryResourceVisualByEntityId.TryGetValue(entityId, out var visual) && visual != null)
            {
                UnityEngine.Object.Destroy(visual);
            }

            ReplicationAgentCarryResourceVisualByEntityId.Remove(entityId);
        }

        private static void ClearReplicationAgentCarryResourceVisuals()
        {
            foreach (var visual in ReplicationAgentCarryResourceVisualByEntityId.Values)
            {
                if (visual != null)
                {
                    UnityEngine.Object.Destroy(visual);
                }
            }

            ReplicationAgentCarryResourceVisualByEntityId.Clear();
        }

        private static bool TryResolveReplicationResourceEquipment(string blueprintId, out object? equipment, out string detail)
        {
            equipment = null;
            if (!TryResolveReplicationResourceModel(blueprintId, out var resource, out var resourceDetail) || resource == null)
            {
                detail = resourceDetail;
                return false;
            }

            if (TryReadInstanceMemberValue(resource, "EquipmentBlueprint", out equipment) && equipment != null)
            {
                detail = resourceDetail + " equipment=EquipmentBlueprint";
                return true;
            }

            if (TryReadInstanceMemberValue(resource, "equipmentBlueprint", out equipment) && equipment != null)
            {
                detail = resourceDetail + " equipment=equipmentBlueprint";
                return true;
            }

            detail = resourceDetail + " equipment-blueprint-missing";
            return false;
        }

        private static bool TryMirrorReplicationAgentEquipmentEquipped(object view, string blueprintId, out string detail)
        {
            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "equipment-state-blueprint-empty";
                return false;
            }

            if (!TryResolveReplicationAgentInventory(view, out var inventory, out var inventoryDetail) || inventory == null)
            {
                detail = "equipment-state-inventory-missing " + inventoryDetail;
                return false;
            }

            if (TryFindReplicationEquippedItemById(inventory, blueprintId, out var existingEquipment, out var existingDetail)
                && existingEquipment != null)
            {
                detail = "equipment-state-already-equipped " + existingDetail + " " + inventoryDetail;
                return true;
            }

            if (!TryCreateReplicationEquipmentInstance(blueprintId, out var equipmentInstance, out var instanceDetail) || equipmentInstance == null)
            {
                detail = instanceDetail + " " + existingDetail + " " + inventoryDetail;
                return false;
            }

            var method = FindReplicationInstanceMethod(inventory.GetType(), "Equip", new[] { equipmentInstance.GetType(), typeof(bool) });
            if (method == null)
            {
                detail = "equipment-state-equip-method-missing inventoryType="
                    + (inventory.GetType().FullName ?? inventory.GetType().Name)
                    + " equipmentType="
                    + (equipmentInstance.GetType().FullName ?? equipmentInstance.GetType().Name)
                    + " "
                    + instanceDetail
                    + " "
                    + inventoryDetail;
                return false;
            }

            try
            {
                method.Invoke(inventory, new object[] { equipmentInstance, false });
                detail = "equipment-state=Equip manual=false " + instanceDetail + " " + inventoryDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "equipment-state=Equip " + FormatReflectionExceptionDetail(ex) + " " + instanceDetail + " " + inventoryDetail;
                return false;
            }
        }

        private static bool TryFindReplicationEquippedItemById(object inventory, string blueprintId, out object? equipment, out string detail)
        {
            equipment = null;
            detail = "equipment-existing=not-found";
            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "equipment-existing-blueprint-empty";
                return false;
            }

            if (!TryInvokeReplicationObjectMethod(inventory, "GetEquipments", out var equipments) || equipments == null)
            {
                if (!TryReadInstanceMemberValue(inventory, "Equipments", out equipments) || equipments == null)
                {
                    TryReadInstanceMemberValue(inventory, "equipments", out equipments);
                }
            }

            if (equipments is not System.Collections.IEnumerable enumerable)
            {
                detail = "equipment-existing-list-missing inventoryType=" + (inventory.GetType().FullName ?? inventory.GetType().Name);
                return false;
            }

            var scanned = 0;
            foreach (var candidate in enumerable)
            {
                if (candidate == null)
                {
                    continue;
                }

                scanned++;
                if (TryExtractReplicationResourceId(candidate, out var candidateId)
                    && string.Equals(candidateId, blueprintId, StringComparison.Ordinal))
                {
                    equipment = candidate;
                    detail = "equipment-existing=matched scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            detail = "equipment-existing=not-found scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryCreateReplicationEquipmentInstance(string blueprintId, out object? equipmentInstance, out string detail)
        {
            equipmentInstance = null;
            var type = AccessTools.TypeByName("NSMedieval.State.EquipmentInstance");
            if (type == null)
            {
                detail = "equipment-instance-type-missing";
                return false;
            }

            var constructor = type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(string), typeof(bool) },
                null);
            if (constructor == null)
            {
                detail = "equipment-instance-ctor-missing";
                return false;
            }

            try
            {
                equipmentInstance = constructor.Invoke(new object[] { blueprintId, true });
                detail = "equipmentInstance=ctor(string,bool) blueprintId=" + blueprintId;
                return equipmentInstance != null;
            }
            catch (Exception ex)
            {
                detail = "equipment-instance-ctor-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationCarryVisualEquipment(string blueprintId, out object? equipment, out string detail)
        {
            if (TryResolveReplicationResourceEquipment(blueprintId, out equipment, out var resourceEquipmentDetail) && equipment != null)
            {
                detail = resourceEquipmentDetail;
                return true;
            }

            if (TryResolveReplicationEquipmentModel(blueprintId, out equipment, out var equipmentModelDetail) && equipment != null)
            {
                detail = equipmentModelDetail;
                return true;
            }

            detail = resourceEquipmentDetail + " " + equipmentModelDetail;
            return false;
        }

        private static bool TryInvokeReplicationCarryVisualMethod(object view, string methodName, object equipment, out string detail)
        {
            var method = FindReplicationInstanceMethod(view.GetType(), methodName, new[] { equipment.GetType() });
            if (method != null)
            {
                try
                {
                    method.Invoke(view, new[] { equipment });
                    detail = "viewMethod=" + methodName;
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "viewMethod=" + methodName + " " + FormatReflectionExceptionDetail(ex);
                    return false;
                }
            }

            if (TryReadInstanceMemberValue(view, "BodyPreview", out var bodyPreview) && bodyPreview != null)
            {
                var bodyMethod = FindReplicationInstanceMethod(bodyPreview.GetType(), methodName, new[] { equipment.GetType() });
                if (bodyMethod != null)
                {
                    try
                    {
                        bodyMethod.Invoke(bodyPreview, new[] { equipment });
                        detail = "bodyPreviewMethod=" + methodName;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        detail = "bodyPreviewMethod=" + methodName + " " + FormatReflectionExceptionDetail(ex);
                        return false;
                    }
                }
            }

            detail = "method-missing method=" + methodName + " viewType=" + (view.GetType().FullName ?? view.GetType().Name) + " equipmentType=" + (equipment.GetType().FullName ?? equipment.GetType().Name);
            return false;
        }

        private static bool TryExtractReplicationResourceCarryPayload(object[]? args, object closure, out string blueprintId, out int amount)
        {
            blueprintId = string.Empty;
            amount = 0;
            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    var arg = args[i];
                    if (arg == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(blueprintId))
                    {
                        TryExtractReplicationResourceId(arg, out blueprintId);
                    }

                    if (amount <= 0)
                    {
                        try
                        {
                            if (arg is int intAmount)
                            {
                                amount = intAmount;
                            }
                            else if (arg.GetType().IsPrimitive)
                            {
                                amount = Convert.ToInt32(arg, CultureInfo.InvariantCulture);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (amount <= 0 && TryReadReplicationWorldObjectIntMember(closure, "requestedAmount", "requestedAmount", out var requestedAmount))
            {
                amount = requestedAmount;
            }

            return !string.IsNullOrWhiteSpace(blueprintId);
        }

        private static bool TryExtractReplicationResourceId(object value, out string blueprintId)
        {
            blueprintId = string.Empty;
            return TryInvokeReplicationStringMethod(value, "GetID", out blueprintId)
                || TryReadReplicationWorldObjectStringMember(value, "ID", "id", out blueprintId)
                || TryReadReplicationWorldObjectStringMember(value, "ProtoId", "protoId", out blueprintId)
                || TryReadReplicationWorldObjectStringMember(value, "GroupIdentifier", "groupIdentifier", out blueprintId);
        }

        private static bool TryGetReplicationGoapActionEntityId(object action, out string entityId, out string detail)
        {
            return TryGetReplicationGoapActionEntityId(action, string.Empty, out entityId, out detail);
        }

        private static bool TryGetReplicationGoapActionEntityId(object action, string actionHint, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "entity-source=missing";
            if (TryResolveReplicationGoapActionOwner(action, out var owner, out var ownerDetail)
                && owner != null)
            {
                if (TryGetReplicationAgentOwnerEntityId(owner, out entityId, out var entityDetail))
                {
                    detail = ownerDetail + " " + entityDetail;
                    return true;
                }

                detail = ownerDetail + " entity-source=unresolved owner=" + TrimFingerprintText(FormatCommandSurfaceValue(owner, 1), 300);
                return false;
            }

            if (TryGetCachedReplicationGoapActionEntityId(action, out entityId, out var cacheDetail))
            {
                detail = ownerDetail + " " + cacheDetail;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(actionHint)
                && TryTakeReplicationPendingActionAnimationOwner(actionHint, out entityId, out var pendingDetail))
            {
                detail = ownerDetail + " " + pendingDetail;
                return true;
            }

            if (TryGetActiveReplicationActionStatusEntityId(actionHint, out entityId, out var fallbackDetail))
            {
                detail = ownerDetail + " " + fallbackDetail + " action=" + TrimFingerprintText(FormatCommandSurfaceValue(action, 1), 300);
                return true;
            }

            detail = ownerDetail + " " + fallbackDetail + " action=" + TrimFingerprintText(FormatCommandSurfaceValue(action, 1), 300);
            return false;
        }

        private static void TryCacheReplicationGoapActionOwner(object action, string lifecycleMethod)
        {
            if (!TryResolveReplicationGoapActionOwner(action, out var owner, out var ownerDetail)
                || owner == null
                || !TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            lock (ReplicationWorldObjectDeltaLock)
            {
                PruneReplicationGoapActionOwnerBindings(now);
                ReplicationGoapActionOwnerBindings[action] = new ReplicationGoapActionOwnerBinding(entityId, now, lifecycleMethod, ownerDetail);
            }
        }

        private static bool TryReadReplicationGoapActionInfo(
            object action,
            out string actionId,
            out string goalId,
            out long targetId,
            out string targetBlueprintId,
            out int targetX,
            out int targetY,
            out int targetZ)
        {
            actionId = FormatShortTypeName(action.GetType());
            goalId = string.Empty;
            targetId = 0L;
            targetBlueprintId = string.Empty;
            targetX = 0;
            targetY = 0;
            targetZ = 0;

            if (TryReadInstanceMemberValue(action, "Id", out var id) && id != null)
            {
                actionId = Convert.ToString(id, CultureInfo.InvariantCulture) ?? actionId;
            }
            else if (TryInvokeReplicationObjectMethod(action, "get_Id", out id) && id != null)
            {
                actionId = Convert.ToString(id, CultureInfo.InvariantCulture) ?? actionId;
            }

            if ((!TryInvokeReplicationObjectMethod(action, "get_Goal", out var goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "Goal", out goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "goal", out goal) || goal == null))
            {
                return false;
            }

            goalId = FormatShortTypeName(goal.GetType());
            var targetMemberNames = new[] { "Target", "target", "TargetResource", "targetResource", "SelectedTarget", "selectedTarget", "TargetPosition", "targetPosition", "TargetGridPosition", "targetGridPosition" };
            object? target = null;
            for (var i = 0; i < targetMemberNames.Length && target == null; i++)
            {
                if (TryReadInstanceMemberValue(action, targetMemberNames[i], out target) && target != null)
                {
                    break;
                }

                TryReadInstanceMemberValue(goal, targetMemberNames[i], out target);
            }

            if (target != null)
            {
                ReadReplicationGoapTargetIdentity(target, ref targetId, ref targetBlueprintId, ref targetX, ref targetY, ref targetZ);
            }
            else if (TryGetReplicationGoapSelectedTarget(goal, out var selectedTarget))
            {
                ReadReplicationGoapTargetIdentity(selectedTarget, ref targetId, ref targetBlueprintId, ref targetX, ref targetY, ref targetZ);
            }

            return !string.IsNullOrWhiteSpace(goalId);
        }

        private static bool TryGetReplicationGoapSelectedTarget(object goal, out object target)
        {
            target = null!;
            var targetIndexType = AccessTools.TypeByName("NSMedieval.Goap.TargetIndex");
            if (targetIndexType == null)
            {
                return false;
            }

            var getTarget = FindReplicationInstanceMethod(goal.GetType(), "GetTarget", new[] { targetIndexType });
            if (getTarget == null)
            {
                return false;
            }

            try
            {
                var selectedIndex = Enum.Parse(targetIndexType, "A", ignoreCase: false);
                var selectedTarget = getTarget.Invoke(goal, new[] { selectedIndex });
                if (selectedTarget == null)
                {
                    return false;
                }

                target = selectedTarget;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ReadReplicationGoapTargetIdentity(object target, ref long targetId, ref string targetBlueprintId, ref int targetX, ref int targetY, ref int targetZ)
        {
            ReadReplicationWorldObjectIdentity(target, ref targetId, ref targetBlueprintId, ref targetX, ref targetY, ref targetZ);

            var nestedTargetNames = new[]
            {
                "ObjectInstance", "objectInstance",
                "ReachablePosition", "reachablePosition",
                "PrecisePosition", "precisePosition",
                "Target", "target", "Object", "object", "WorldObject", "worldObject",
                "MapResource", "mapResource", "Resource", "resource",
                "Position", "position", "GridPosition", "gridPosition"
            };
            for (var i = 0; i < nestedTargetNames.Length; i++)
            {
                if (!TryReadInstanceMemberValue(target, nestedTargetNames[i], out var nestedTarget) || nestedTarget == null)
                {
                    continue;
                }

                ReadReplicationWorldObjectIdentity(nestedTarget, ref targetId, ref targetBlueprintId, ref targetX, ref targetY, ref targetZ);
            }
        }

        private static void ReadReplicationWorldObjectIdentity(object target, ref long targetId, ref string targetBlueprintId, ref int targetX, ref int targetY, ref int targetZ)
        {
            // TargetObject wrappers expose their coordinates while the durable
            // object identity lives on ObjectInstance. Preserve fields already
            // collected from a sibling/nested value instead of clearing them
            // whenever the next value lacks that particular member.
            if (TryReadReplicationWorldObjectLongMember(target, "UniqueId", "uniqueId", out var resolvedTargetId)
                && resolvedTargetId != 0L)
            {
                targetId = resolvedTargetId;
            }

            if (TryReadReplicationWorldObjectStringMember(target, "BlueprintId", "blueprintId", out var resolvedBlueprintId))
            {
                targetBlueprintId = resolvedBlueprintId;
            }

            if (TryReadReplicationWorldObjectGridPosition(target, out var resolvedX, out var resolvedY, out var resolvedZ))
            {
                targetX = resolvedX;
                targetY = resolvedY;
                targetZ = resolvedZ;
            }
        }

        private static bool TryGetCachedReplicationGoapActionEntityId(object action, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "action-lifecycle-cache=miss";
            var now = Time.realtimeSinceStartup;
            lock (ReplicationWorldObjectDeltaLock)
            {
                PruneReplicationGoapActionOwnerBindings(now);
                if (!ReplicationGoapActionOwnerBindings.TryGetValue(action, out var binding))
                {
                    return false;
                }

                entityId = binding.EntityId;
                detail = "owner-source=action-lifecycle-cache method="
                    + binding.LifecycleMethod
                    + " ageMs="
                    + ((int)((now - binding.UpdatedRealtime) * 1000f)).ToString(CultureInfo.InvariantCulture)
                    + " "
                    + binding.OwnerDetail;
                return true;
            }
        }

        private static bool TryTakeReplicationPendingActionAnimationOwner(string trigger, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "pending-action-owner=none";
            var now = Time.realtimeSinceStartup;
            lock (ReplicationWorldObjectDeltaLock)
            {
                PruneReplicationPendingActionAnimationOwners(now);
                if (ReplicationPendingActionAnimationOwners.Count == 0)
                {
                    return false;
                }

                var pendingCount = ReplicationPendingActionAnimationOwners.Count;
                PendingReplicationActionAnimationOwner? match = null;
                for (var i = 0; i < pendingCount; i++)
                {
                    var candidate = ReplicationPendingActionAnimationOwners.Dequeue();
                    if (match == null && IsReplicationAnimationTriggerCompatibleWithGoal(candidate.GoalId, candidate.StatusText, trigger))
                    {
                        match = candidate;
                        continue;
                    }

                    ReplicationPendingActionAnimationOwners.Enqueue(candidate);
                }

                if (match == null)
                {
                    detail = "pending-action-owner=incompatible trigger=" + FormatReplicationWorldObjectDetailToken(trigger)
                        + " queued=" + pendingCount.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                entityId = match.EntityId;
                detail = "owner-source=pending-goal-start goal="
                    + FormatReplicationWorldObjectDetailToken(match.GoalId)
                    + " ageMs="
                    + ((int)((now - match.QueuedRealtime) * 1000f)).ToString(CultureInfo.InvariantCulture)
                    + " queued="
                    + pendingCount.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        private static void PruneReplicationPendingActionAnimationOwners(float now)
        {
            while (ReplicationPendingActionAnimationOwners.Count > 0
                && now - ReplicationPendingActionAnimationOwners.Peek().QueuedRealtime > 2f)
            {
                ReplicationPendingActionAnimationOwners.Dequeue();
            }
        }

        private static void PruneReplicationGoapActionOwnerBindings(float now)
        {
            if (ReplicationGoapActionOwnerBindings.Count == 0)
            {
                return;
            }

            List<object>? expiredActions = null;
            foreach (var pair in ReplicationGoapActionOwnerBindings)
            {
                if (now - pair.Value.UpdatedRealtime <= 120f)
                {
                    continue;
                }

                expiredActions ??= new List<object>();
                expiredActions.Add(pair.Key);
            }

            if (expiredActions == null)
            {
                return;
            }

            for (var i = 0; i < expiredActions.Count; i++)
            {
                ReplicationGoapActionOwnerBindings.Remove(expiredActions[i]);
            }
        }

        private static bool TryGetActiveReplicationActionStatusEntityId(string actionHint, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "active-action-fallback=none";
            var now = Time.realtimeSinceStartup;
            string? pendingTokenEntityId = null;
            string? onlyActiveEntityId = null;
            var pendingTokenCount = 0;
            var activeCount = 0;

            lock (ReplicationWorldObjectDeltaLock)
            {
                foreach (var pair in ReplicationAgentActionStatusByEntityId)
                {
                    var status = pair.Value;
                    if (!status.HasStarted
                        || IsReplicationIdleActionStatus(status.GoalId, status.StatusText)
                        || now - status.UpdatedRealtime > 3f)
                    {
                        continue;
                    }

                    activeCount++;
                    onlyActiveEntityId = pair.Key;
                    if (string.IsNullOrWhiteSpace(status.AnimationToken))
                    {
                        pendingTokenCount++;
                        pendingTokenEntityId = pair.Key;
                    }
                }
            }

            if (pendingTokenCount == 1 && !string.IsNullOrWhiteSpace(pendingTokenEntityId))
            {
                entityId = pendingTokenEntityId;
                detail = "active-action-fallback=pending-token hint="
                    + FormatReplicationWorldObjectDetailToken(actionHint)
                    + " active="
                    + activeCount.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (activeCount == 1 && !string.IsNullOrWhiteSpace(onlyActiveEntityId))
            {
                entityId = onlyActiveEntityId;
                detail = "active-action-fallback=single-active hint="
                    + FormatReplicationWorldObjectDetailToken(actionHint);
                return true;
            }

            detail = "active-action-fallback=ambiguous hint="
                + FormatReplicationWorldObjectDetailToken(actionHint)
                + " active="
                + activeCount.ToString(CultureInfo.InvariantCulture)
                + " pendingToken="
                + pendingTokenCount.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryResolveReplicationGoapActionOwner(object action, out object? owner, out string detail)
        {
            owner = null;
            var actionOwnerDetail = string.Empty;
            if (TryReadInstanceMemberValue(action, "AgentOwner", out owner)
                || TryReadInstanceMemberValue(action, "agentOwner", out owner))
            {
                if (owner != null)
                {
                    detail = "owner-source=action.AgentOwner";
                    return true;
                }

                actionOwnerDetail = "action.AgentOwner=null ";
            }

            if ((!TryInvokeReplicationObjectMethod(action, "get_Goal", out var goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "Goal", out goal) || goal == null)
                && (!TryReadInstanceMemberValue(action, "goal", out goal) || goal == null))
            {
                detail = actionOwnerDetail + "owner-source=action-goal-missing";
                return false;
            }

            if (TryInvokeReplicationObjectMethod(goal, "get_AgentOwner", out owner) && owner != null)
            {
                detail = actionOwnerDetail + "owner-source=action.Goal.AgentOwner goal=" + FormatShortTypeName(goal.GetType());
                return true;
            }

            if (TryReadInstanceMemberValue(goal, "AgentOwner", out owner) && owner != null)
            {
                detail = actionOwnerDetail + "owner-source=action.Goal.AgentOwner-field goal=" + FormatShortTypeName(goal.GetType());
                return true;
            }

            if (TryReadInstanceMemberValue(goal, "agentOwner", out owner) && owner != null)
            {
                detail = actionOwnerDetail + "owner-source=action.Goal.agentOwner-field goal=" + FormatShortTypeName(goal.GetType());
                return true;
            }

            detail = actionOwnerDetail + "owner-source=goal-owner-missing goal=" + FormatShortTypeName(goal.GetType());
            return false;
        }

        private static string FormatReplicationResourceCarryArgs(object[]? args)
        {
            if (args == null || args.Length == 0)
            {
                return " args=<none>";
            }

            var builder = new StringBuilder(256);
            builder.Append(" args=");
            for (var i = 0; i < args.Length && i < 4; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append(":")
                    .Append(TrimFingerprintText(FormatCommandSurfaceValue(args[i], 1), 180));
            }

            return builder.ToString();
        }

        private static bool TryResolveReplicationResourceModel(string blueprintId, out object? resource, out string detail)
        {
            resource = null;
            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "blueprint-id-empty";
                return false;
            }

            if (!TryGetReplicationResourceRepository(out var repository, out detail) || repository == null)
            {
                return false;
            }

            var repositoryType = repository.GetType();
            var lastError = string.Empty;
            for (var i = 0; i < ReplicationResourceLookupMethodNames.Length; i++)
            {
                var methodName = ReplicationResourceLookupMethodNames[i];
                var method = AccessTools.Method(repositoryType, methodName, new[] { typeof(string) });
                if (method == null)
                {
                    continue;
                }

                try
                {
                    var candidate = method.Invoke(repository, new object[] { blueprintId });
                    if (candidate != null)
                    {
                        resource = candidate;
                        detail = "resourceLookup=" + methodName + " blueprintId=" + blueprintId;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = methodName + ":" + FormatReflectionExceptionDetail(ex);
                }
            }

            detail = "resource-not-found blueprintId=" + blueprintId + (lastError.Length > 0 ? " lastError=" + lastError : string.Empty);
            return false;
        }

        private static bool TryGetReplicationResourceRepository(out object? repository, out string detail)
        {
            repository = null;
            var repositoryType = AccessTools.TypeByName("NSMedieval.Repository.ResourceRepository");
            if (repositoryType == null)
            {
                detail = "resource-repository-type-missing";
                return false;
            }

            try
            {
                var instanceProperty = AccessTools.Property(repositoryType, "Instance");
                if (instanceProperty != null)
                {
                    repository = instanceProperty.GetValue(null, null);
                    if (repository != null)
                    {
                        detail = "repository=Instance";
                        return true;
                    }
                }

                var instanceField = AccessTools.Field(repositoryType, "instance");
                if (instanceField != null)
                {
                    repository = instanceField.GetValue(null);
                    if (repository != null)
                    {
                        detail = "repository=instance-field";
                        return true;
                    }
                }

                detail = "resource-repository-instance-null";
                return false;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationEquipmentModel(string blueprintId, out object? equipment, out string detail)
        {
            equipment = null;
            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                detail = "equipment-blueprint-id-empty";
                return false;
            }

            if (!TryGetReplicationEquipmentRepository(out var repository, out var repositoryDetail) || repository == null)
            {
                detail = repositoryDetail;
                return false;
            }

            var getAllItems = AccessTools.Method(repository.GetType(), "GetAllItems", Type.EmptyTypes)
                ?? FindReplicationInstanceMethod(repository.GetType(), "GetAllItems", Type.EmptyTypes);
            if (getAllItems == null)
            {
                detail = "equipment-get-all-items-missing " + repositoryDetail;
                return false;
            }

            try
            {
                if (getAllItems.Invoke(repository, null) is not System.Collections.IEnumerable items)
                {
                    detail = "equipment-get-all-items-not-enumerable " + repositoryDetail;
                    return false;
                }

                var scanned = 0;
                foreach (var candidate in items)
                {
                    if (candidate == null)
                    {
                        continue;
                    }

                    scanned++;
                    if (TryInvokeReplicationStringMethod(candidate, "GetID", out var id)
                        && string.Equals(id, blueprintId, StringComparison.Ordinal))
                    {
                        equipment = candidate;
                        detail = "equipmentLookup=GetAllItems scanned=" + scanned.ToString(CultureInfo.InvariantCulture) + " blueprintId=" + blueprintId + " " + repositoryDetail;
                        return true;
                    }

                    if (TryReadReplicationWorldObjectStringMember(candidate, "ID", "id", out id)
                        && string.Equals(id, blueprintId, StringComparison.Ordinal))
                    {
                        equipment = candidate;
                        detail = "equipmentLookup=id-field scanned=" + scanned.ToString(CultureInfo.InvariantCulture) + " blueprintId=" + blueprintId + " " + repositoryDetail;
                        return true;
                    }
                }

                detail = "equipment-not-found blueprintId=" + blueprintId + " scanned=" + scanned.ToString(CultureInfo.InvariantCulture) + " " + repositoryDetail;
                return false;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex) + " " + repositoryDetail;
                return false;
            }
        }

        private static bool TryGetReplicationEquipmentRepository(out object? repository, out string detail)
        {
            repository = null;
            var repositoryType = AccessTools.TypeByName("NSMedieval.Repository.EquipmentRepository");
            if (repositoryType == null)
            {
                detail = "equipment-repository-type-missing";
                return false;
            }

            try
            {
                var instanceProperty = AccessTools.Property(repositoryType, "Instance");
                if (instanceProperty != null)
                {
                    repository = instanceProperty.GetValue(null, null);
                    if (repository != null)
                    {
                        detail = "equipmentRepository=Instance";
                        return true;
                    }
                }

                var instanceField = AccessTools.Field(repositoryType, "instance");
                if (instanceField != null)
                {
                    repository = instanceField.GetValue(null);
                    if (repository != null)
                    {
                        detail = "equipmentRepository=instance-field";
                        return true;
                    }
                }

                if (typeof(UnityEngine.Object).IsAssignableFrom(repositoryType))
                {
                    repository = UnityEngine.Object.FindObjectOfType(repositoryType);
                    if (repository != null)
                    {
                        detail = "equipmentRepository=FindObjectOfType";
                        return true;
                    }

                    var repositories = Resources.FindObjectsOfTypeAll(repositoryType);
                    if (repositories != null && repositories.Length > 0)
                    {
                        repository = repositories[0];
                        detail = "equipmentRepository=FindObjectsOfTypeAll";
                        return true;
                    }
                }

                detail = "equipment-repository-instance-null";
                return false;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryCreateReplicationResourceInstance(object resource, int amount, out object? resourceInstance, out string detail)
        {
            resourceInstance = null;
            var resourceInstanceType = AccessTools.TypeByName("NSMedieval.State.ResourceInstance");
            if (resourceInstanceType == null)
            {
                detail = "resource-instance-type-missing";
                return false;
            }

            try
            {
                var constructor = AccessTools.Constructor(resourceInstanceType, new[] { resource.GetType(), typeof(int) })
                    ?? FindReplicationConstructor(resourceInstanceType, resource.GetType(), typeof(int));
                if (constructor == null)
                {
                    detail = "resource-instance-constructor-missing";
                    return false;
                }

                resourceInstance = constructor.Invoke(new object[] { resource, amount });
                detail = "resourceInstance=" + FormatShortTypeName(resourceInstance.GetType());
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static ConstructorInfo? FindReplicationConstructor(Type targetType, Type firstArgumentType, Type secondArgumentType)
        {
            var constructors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < constructors.Length; i++)
            {
                var parameters = constructors[i].GetParameters();
                if (parameters.Length == 2
                    && parameters[0].ParameterType.IsAssignableFrom(firstArgumentType)
                    && parameters[1].ParameterType == secondArgumentType)
                {
                    return constructors[i];
                }
            }

            return null;
        }

        private static bool TryGetReplicationResourcePileManager(out object? manager, out string detail)
        {
            manager = null;
            var managerType = AccessTools.TypeByName("NSMedieval.Manager.ResourcePileManager");
            if (managerType == null)
            {
                detail = "resource-pile-manager-type-missing";
                return false;
            }

            try
            {
                var instanceProperty = AccessTools.Property(managerType, "Instance");
                if (instanceProperty != null)
                {
                    manager = instanceProperty.GetValue(null, null);
                    if (manager != null)
                    {
                        detail = "manager=Instance";
                        return true;
                    }
                }

                if (typeof(UnityEngine.Object).IsAssignableFrom(managerType))
                {
                    manager = UnityEngine.Object.FindObjectOfType(managerType);
                    if (manager != null)
                    {
                        detail = "manager=FindObjectOfType";
                        return true;
                    }

                    var allManagers = Resources.FindObjectsOfTypeAll(managerType);
                    if (allManagers != null && allManagers.Length > 0)
                    {
                        manager = allManagers[0];
                        detail = "manager=FindObjectsOfTypeAll";
                        return true;
                    }
                }

                detail = "resource-pile-manager-instance-null";
                return false;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static Vector3 GetReplicationWorldPosition(int x, int y, int z, out string detail)
        {
            var fallback = new Vector3(x, y, z);
            if (!TryCreateVec3Int(x, y, z, out var gridPosition, out var vecDetail))
            {
                detail = "position=fallback-grid-vector " + vecDetail + " world=" + FormatUnityVector(fallback);
                return fallback;
            }

            try
            {
                var gridUtilsType = AccessTools.TypeByName("NSMedieval.GridUtils");
                if (gridUtilsType != null)
                {
                    var vecMethod = AccessTools.Method(gridUtilsType, "GetWorldPosition", new[] { gridPosition.GetType() });
                    if (vecMethod != null && vecMethod.Invoke(null, new[] { gridPosition }) is Vector3 worldFromVec)
                    {
                        detail = "position=GridUtils.GetWorldPosition(Vec3Int) world=" + FormatUnityVector(worldFromVec);
                        return worldFromVec;
                    }

                    var intMethod = AccessTools.Method(gridUtilsType, "GetWorldPosition", new[] { typeof(int), typeof(int), typeof(int) });
                    if (intMethod != null && intMethod.Invoke(null, new object[] { x, y, z }) is Vector3 worldFromInts)
                    {
                        detail = "position=GridUtils.GetWorldPosition(int,int,int) world=" + FormatUnityVector(worldFromInts);
                        return worldFromInts;
                    }
                }

                detail = "position=fallback-grid-vector gridUtils-missing-or-unusable world=" + FormatUnityVector(fallback);
                return fallback;
            }
            catch (Exception ex)
            {
                detail = "position=fallback-grid-vector " + FormatReflectionExceptionDetail(ex) + " world=" + FormatUnityVector(fallback);
                return fallback;
            }
        }

        private static bool TrySpawnReplicationResourcePile(object manager, object resourceInstance, Vector3 worldPosition, out string detail)
        {
            try
            {
                var method = AccessTools.Method(manager.GetType(), "SpawnPile", new[] { resourceInstance.GetType(), typeof(Vector3), typeof(bool), typeof(float) })
                    ?? FindReplicationResourcePileSpawnMethod(manager.GetType(), resourceInstance.GetType());
                if (method == null)
                {
                    detail = "spawn-pile-resource-instance-method-missing";
                    return false;
                }

                var result = method.Invoke(manager, new object[] { resourceInstance, worldPosition, false, 0f });
                if (result == null)
                {
                    detail = "spawn-result-null method=" + FormatShortTypeName(method.ReturnType);
                    return false;
                }

                detail = "via=SpawnPile(ResourceInstance,Vector3,bool,float) result=" + FormatShortTypeName(result.GetType());
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryInvokeReplicationResourcePileManagerAdd(object manager, object resourceInstance, ReplicationWorldObjectDelta delta, Vector3 worldPosition, out string detail)
        {
            var methods = manager.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var attempts = new StringBuilder(256);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, "TryAddToResourcePile", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = methods[i].GetParameters();
                var args = new object?[parameters.Length];
                var usable = true;
                for (var p = 0; p < parameters.Length; p++)
                {
                    var parameterType = parameters[p].ParameterType;
                    if (parameterType.IsAssignableFrom(resourceInstance.GetType()))
                    {
                        args[p] = resourceInstance;
                    }
                    else if (parameterType == typeof(Vector3))
                    {
                        args[p] = worldPosition;
                    }
                    else if ((parameterType.FullName ?? parameterType.Name).IndexOf("Vec3Int", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!TryCreateVec3Int(delta.GridX, delta.GridY, delta.GridZ, out var gridPosition, out var vecDetail))
                        {
                            AppendReplicationAttemptDetail(attempts, methods[i], "vec3int-failed " + vecDetail);
                            usable = false;
                            break;
                        }

                        args[p] = gridPosition;
                    }
                    else if (parameterType == typeof(bool))
                    {
                        args[p] = false;
                    }
                    else if (parameterType == typeof(float))
                    {
                        args[p] = 0f;
                    }
                    else if (parameterType == typeof(int))
                    {
                        args[p] = 0;
                    }
                    else if (!parameterType.IsValueType)
                    {
                        args[p] = null;
                    }
                    else
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param=" + parameterType.FullName);
                        usable = false;
                        break;
                    }
                }

                if (!usable)
                {
                    continue;
                }

                try
                {
                    var result = methods[i].Invoke(manager, args);
                    if (result is bool boolResult && !boolResult)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "returned-false");
                        continue;
                    }

                    detail = "via=ResourcePileManager.TryAddToResourcePile signature=" + FormatReplicationMethodSignature(methods[i]) + " result=" + Convert.ToString(result, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex)
                {
                    AppendReplicationAttemptDetail(attempts, methods[i], FormatReflectionExceptionDetail(ex));
                }
            }

            detail = attempts.Length == 0 ? "manager-add-method-missing" : "manager-add-failed attempts=" + attempts;
            return false;
        }

        private static bool TryAddReplicationAmountToExistingResourcePile(object pile, object resourceInstance, int amount, out string detail)
        {
            if (!TryGetReplicationPileStoredResource(pile, out var storedResource, out var storedDetail) || storedResource == null)
            {
                detail = "stored-resource-missing " + storedDetail;
                return false;
            }

            if (!TryIncreaseReplicationResourceInstanceAmount(storedResource, amount, out var amountDetail))
            {
                detail = amountDetail + " " + storedDetail;
                return false;
            }

            var instanceLinkDetail = TrySetReplicationResourcePileInstance(resourceInstance, pile)
                ? "resourceInstance-pile-linked"
                : "resourceInstance-pile-link-skipped";
            var eventDetail = InvokeReplicationPileResourceAddedHooks(pile, resourceInstance, out var hookDetail)
                ? hookDetail
                : "hook-failed " + hookDetail;
            detail = "via=storedResource.Add amount="
                + amount.ToString(CultureInfo.InvariantCulture)
                + " "
                + amountDetail
                + " "
                + storedDetail
                + " "
                + instanceLinkDetail
                + " "
                + eventDetail;
            return true;
        }

        private static bool TrySetReplicationExistingResourcePileAmount(object pile, object resourceInstance, int amount, out string detail)
        {
            if (!TryGetReplicationPileStoredResource(pile, out var storedResource, out var storedDetail) || storedResource == null)
            {
                detail = "stored-resource-missing " + storedDetail;
                return false;
            }

            if (!TrySetReplicationResourceInstanceAmount(storedResource, amount, out var amountDetail))
            {
                detail = amountDetail + " " + storedDetail;
                return false;
            }

            var instanceLinkDetail = TrySetReplicationResourcePileInstance(resourceInstance, pile)
                ? "resourceInstance-pile-linked"
                : "resourceInstance-pile-link-skipped";
            var eventDetail = InvokeReplicationPileResourceAddedHooks(pile, resourceInstance, out var hookDetail)
                ? hookDetail
                : "hook-failed " + hookDetail;
            detail = "via=storedResource.SetAmount amount="
                + amount.ToString(CultureInfo.InvariantCulture)
                + " "
                + amountDetail
                + " "
                + storedDetail
                + " "
                + instanceLinkDetail
                + " "
                + eventDetail;
            return true;
        }

        private static bool TryIncreaseReplicationResourceInstanceAmount(object resourceInstance, int amount, out string detail)
        {
            var addAttemptDetail = "add-method-not-tried";
            if (TryInvokeReplicationResourceAmountMethod(resourceInstance, "Add", amount, out addAttemptDetail))
            {
                detail = addAttemptDetail;
                return true;
            }

            if (!TryReadReplicationWorldObjectIntMember(resourceInstance, "Amount", "amount", out var currentAmount))
            {
                detail = "resource-amount-read-failed " + addAttemptDetail + " type=" + (resourceInstance.GetType().FullName ?? resourceInstance.GetType().Name);
                return false;
            }

            var newAmount = Math.Max(0, currentAmount + amount);
            var setAttemptDetail = "set-method-not-tried";
            if (TryInvokeReplicationResourceAmountMethod(resourceInstance, "SetAmount", newAmount, out setAttemptDetail))
            {
                detail = "resourceAmount=SetAmount oldAmount="
                    + currentAmount.ToString(CultureInfo.InvariantCulture)
                    + " newAmount="
                    + newAmount.ToString(CultureInfo.InvariantCulture)
                    + " addAttempt="
                    + addAttemptDetail
                    + " "
                    + setAttemptDetail;
                return true;
            }

            detail = "resource-amount-update-failed addAttempt=" + addAttemptDetail + " setAttempt=" + setAttemptDetail + " type=" + (resourceInstance.GetType().FullName ?? resourceInstance.GetType().Name);
            return false;
        }

        private static bool TrySetReplicationResourceInstanceAmount(object resourceInstance, int amount, out string detail)
        {
            var oldAmountDetail = TryReadReplicationWorldObjectIntMember(resourceInstance, "Amount", "amount", out var oldAmount)
                ? " oldAmount=" + oldAmount.ToString(CultureInfo.InvariantCulture)
                : " oldAmount=<unread>";
            var setAmount = Math.Max(0, amount);
            if (TryInvokeReplicationResourceAmountMethod(resourceInstance, "SetAmount", setAmount, out var setAttemptDetail))
            {
                detail = "resourceAmount=SetAmount"
                    + oldAmountDetail
                    + " newAmount="
                    + setAmount.ToString(CultureInfo.InvariantCulture)
                    + " "
                    + setAttemptDetail;
                return true;
            }

            detail = "resource-amount-set-failed " + setAttemptDetail + " type=" + (resourceInstance.GetType().FullName ?? resourceInstance.GetType().Name);
            return false;
        }

        private static bool TryInvokeReplicationResourceAmountMethod(object target, string methodName, int amount, out string detail)
        {
            var attempts = new StringBuilder(256);
            for (var current = target.GetType(); current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = methods[i].GetParameters();
                    if (parameters.Length != 1)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param-count=" + parameters.Length.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    if (!TryConvertReplicationAmountArgument(amount, parameters[0].ParameterType, out var convertedAmount))
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param=" + (parameters[0].ParameterType.FullName ?? parameters[0].ParameterType.Name));
                        continue;
                    }

                    try
                    {
                        methods[i].Invoke(target, new[] { convertedAmount });
                        detail = "resourceAmount=" + methodName + " signature=" + FormatReplicationMethodSignature(methods[i]) + " amount=" + amount.ToString(CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        AppendReplicationAttemptDetail(attempts, methods[i], FormatReflectionExceptionDetail(ex));
                    }
                }
            }

            detail = attempts.Length == 0
                ? "resourceAmount=" + methodName + "-method-missing"
                : "resourceAmount=" + methodName + "-failed attempts=" + attempts;
            return false;
        }

        private static bool TryConvertReplicationAmountArgument(int amount, Type parameterType, out object convertedAmount)
        {
            convertedAmount = amount;
            var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            if (targetType == typeof(int))
            {
                convertedAmount = amount;
                return true;
            }

            if (targetType == typeof(long))
            {
                convertedAmount = (long)amount;
                return true;
            }

            if (targetType == typeof(short))
            {
                convertedAmount = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, amount));
                return true;
            }

            if (targetType == typeof(float))
            {
                convertedAmount = (float)amount;
                return true;
            }

            if (targetType == typeof(double))
            {
                convertedAmount = (double)amount;
                return true;
            }

            return false;
        }

        private static bool TryGetReplicationPileStoredResource(object pile, out object? storedResource, out string detail)
        {
            storedResource = null;
            if (TryInvokeReplicationObjectMethod(pile, "GetStoredResource", out storedResource) && storedResource != null)
            {
                detail = "storedResource=GetStoredResource";
                return true;
            }

            if (TryReadInstanceMemberValue(pile, "storage", out var storage) && storage != null)
            {
                if (TryInvokeReplicationObjectMethod(storage, "GetStoredResource", out storedResource) && storedResource != null)
                {
                    detail = "storedResource=storage.GetStoredResource";
                    return true;
                }

                if (TryReadInstanceMemberValue(storage, "StoredResource", out storedResource) && storedResource != null)
                {
                    detail = "storedResource=storage.StoredResource";
                    return true;
                }
            }

            detail = "stored-resource-not-found pileType=" + (pile.GetType().FullName ?? pile.GetType().Name);
            return false;
        }

        private static bool TrySetReplicationResourcePileInstance(object resourceInstance, object pile)
        {
            try
            {
                var setter = FindReplicationInstanceMethod(resourceInstance.GetType(), "set_ResourcePileInstance", new[] { pile.GetType() });
                if (setter != null)
                {
                    setter.Invoke(resourceInstance, new[] { pile });
                    return true;
                }

                var property = AccessTools.Property(resourceInstance.GetType(), "ResourcePileInstance");
                if (property != null && property.CanWrite)
                {
                    property.SetValue(resourceInstance, pile, null);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool InvokeReplicationPileResourceAddedHooks(object pile, object resourceInstance, out string detail)
        {
            var attempts = new StringBuilder(256);
            if (TryInvokeReplicationNamedMethodWithBestEffortArgs(pile, "OnResourceAdded", resourceInstance, attempts, out detail))
            {
                return true;
            }

            if (TryInvokeReplicationNamedMethodWithBestEffortArgs(pile, "ForceRecalculateReachablePositions", resourceInstance, attempts, out detail))
            {
                detail = "fallback-refresh=" + detail;
                return true;
            }

            detail = attempts.Length == 0 ? "resource-added-hook-missing" : attempts.ToString();
            return false;
        }

        private static bool TryInvokeReplicationNamedMethodWithBestEffortArgs(object target, string methodName, object preferredArg, StringBuilder attempts, out string detail)
        {
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = methods[i].GetParameters();
                var args = new object?[parameters.Length];
                var usable = true;
                for (var p = 0; p < parameters.Length; p++)
                {
                    if (parameters[p].ParameterType.IsAssignableFrom(preferredArg.GetType()))
                    {
                        args[p] = preferredArg;
                    }
                    else if (!parameters[p].ParameterType.IsValueType)
                    {
                        args[p] = null;
                    }
                    else
                    {
                        usable = false;
                        AppendReplicationAttemptDetail(attempts, methods[i], "unsupported-param=" + parameters[p].ParameterType.FullName);
                        break;
                    }
                }

                if (!usable)
                {
                    continue;
                }

                try
                {
                    methods[i].Invoke(target, args);
                    detail = "hook=" + methodName + " signature=" + FormatReplicationMethodSignature(methods[i]);
                    return true;
                }
                catch (Exception ex)
                {
                    AppendReplicationAttemptDetail(attempts, methods[i], FormatReflectionExceptionDetail(ex));
                }
            }

            detail = methodName + "-not-invoked";
            return false;
        }

        private static void AppendReplicationAttemptDetail(StringBuilder builder, MethodInfo? method, string detail)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(FormatReplicationMethodSignature(method))
                .Append(" ")
                .Append(detail);
        }

        private static string FormatReplicationMethodSignature(MethodInfo? method)
        {
            if (method == null)
            {
                return "direct";
            }

            var parameters = method.GetParameters();
            var builder = new StringBuilder(method.Name);
            builder.Append("(");
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append(FormatShortTypeName(parameters[i].ParameterType));
            }

            builder.Append(")");
            return builder.ToString();
        }

        private static MethodInfo? FindReplicationResourcePileSpawnMethod(Type managerType, Type resourceInstanceType)
        {
            var methods = managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, "SpawnPile", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = methods[i].GetParameters();
                if (parameters.Length == 4
                    && parameters[0].ParameterType.IsAssignableFrom(resourceInstanceType)
                    && parameters[1].ParameterType == typeof(Vector3)
                    && parameters[2].ParameterType == typeof(bool)
                    && parameters[3].ParameterType == typeof(float))
                {
                    return methods[i];
                }
            }

            return null;
        }

        private static bool TryExtractReplicationWorldObjectInfo(
            object? target,
            object[]? args,
            object? result,
            bool hasResult,
            out long uniqueId,
            out string blueprintId,
            out int x,
            out int y,
            out int z,
            out string detail)
        {
            uniqueId = 0;
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;
            detail = "not-found";

            var visited = new HashSet<int>();
            if (target != null && TryExtractReplicationWorldObjectInfoFromObject(target, 0, visited, out uniqueId, out blueprintId, out x, out y, out z, out detail))
            {
                detail = "target " + detail;
                return true;
            }

            if (hasResult && result != null && TryExtractReplicationWorldObjectInfoFromObject(result, 0, visited, out uniqueId, out blueprintId, out x, out y, out z, out detail))
            {
                FillReplicationWorldObjectPositionFromArgs(args, ref x, ref y, ref z);
                detail = "result " + detail;
                return true;
            }

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] != null && TryExtractReplicationWorldObjectInfoFromObject(args[i], 0, visited, out uniqueId, out blueprintId, out x, out y, out z, out detail))
                    {
                        FillReplicationWorldObjectPositionFromArgs(args, ref x, ref y, ref z);
                        detail = "arg" + i.ToString(CultureInfo.InvariantCulture) + " " + detail;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractReplicationWorldObjectInfoFromObject(
            object value,
            int depth,
            HashSet<int> visited,
            out long uniqueId,
            out string blueprintId,
            out int x,
            out int y,
            out int z,
            out string detail)
        {
            uniqueId = 0;
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;
            detail = "not-found";

            if (depth > 3 || value == null)
            {
                return false;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal)
            {
                return false;
            }

            if (!type.IsValueType)
            {
                var identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
                if (!visited.Add(identity))
                {
                    return false;
                }
            }

            var hasId = TryReadReplicationWorldObjectLongMember(value, "UniqueId", "uniqueId", out uniqueId);
            TryReadReplicationWorldObjectStringMember(value, "BlueprintId", "blueprintId", out blueprintId);
            var hasPosition = TryReadReplicationWorldObjectGridPosition(value, out x, out y, out z);
            if (hasId && uniqueId > 0)
            {
                detail = "type="
                    + (type.FullName ?? type.Name)
                    + " blueprintId="
                    + blueprintId
                    + " grid=Vec3Int("
                    + x.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + y.ToString(CultureInfo.InvariantCulture)
                    + ","
                    + z.ToString(CultureInfo.InvariantCulture)
                    + ")";
                return true;
            }

            for (var i = 0; i < ReplicationWorldObjectNestedMemberNames.Length; i++)
            {
                if (!TryReadInstanceMemberValue(value, ReplicationWorldObjectNestedMemberNames[i], out var nested)
                    || nested == null
                    || !TryExtractReplicationWorldObjectInfoFromObject(nested, depth + 1, visited, out uniqueId, out var nestedBlueprint, out x, out y, out z, out detail))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(nestedBlueprint))
                {
                    TryReadReplicationWorldObjectStringMember(value, "BlueprintId", "blueprintId", out nestedBlueprint);
                }

                if (!hasPosition)
                {
                    TryReadReplicationWorldObjectGridPosition(value, out x, out y, out z);
                }

                blueprintId = nestedBlueprint;
                detail = ReplicationWorldObjectNestedMemberNames[i] + " " + detail;
                return true;
            }

            return false;
        }

        private static bool TryExtractReplicationWorldObjectAmount(
            object? target,
            object[]? args,
            object? result,
            bool hasResult,
            out int amount)
        {
            amount = 0;
            var visited = new HashSet<int>();
            if (target != null && TryExtractReplicationWorldObjectAmountFromObject(target, 0, visited, out amount))
            {
                return true;
            }

            if (hasResult && result != null && TryExtractReplicationWorldObjectAmountFromObject(result, 0, visited, out amount))
            {
                return true;
            }

            if (args != null)
            {
                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] != null && TryExtractReplicationWorldObjectAmountFromObject(args[i], 0, visited, out amount))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractReplicationWorldObjectAmountFromObject(
            object value,
            int depth,
            HashSet<int> visited,
            out int amount)
        {
            amount = 0;
            if (depth > 3 || value == null)
            {
                return false;
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal)
            {
                return false;
            }

            if (!type.IsValueType)
            {
                var identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
                if (!visited.Add(identity))
                {
                    return false;
                }
            }

            if (TryReadReplicationWorldObjectIntMember(value, "Amount", "amount", out amount) && amount > 0)
            {
                return true;
            }

            var storedResourceMethod = FindReplicationInstanceMethod(type, "GetStoredResource", Type.EmptyTypes);
            if (storedResourceMethod != null)
            {
                try
                {
                    var storedResource = storedResourceMethod.Invoke(value, null);
                    if (storedResource != null && TryExtractReplicationWorldObjectAmountFromObject(storedResource, depth + 1, visited, out amount))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }

            for (var i = 0; i < ReplicationWorldObjectNestedMemberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(value, ReplicationWorldObjectNestedMemberNames[i], out var nested)
                    && nested != null
                    && TryExtractReplicationWorldObjectAmountFromObject(nested, depth + 1, visited, out amount))
                {
                    return true;
                }
            }

            return false;
        }

        private static void LogReplicationCarryProbeForWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            try
            {
                var current = instance;
                if (ReferenceEquals(current, null)
                    || !replicationConfigEnabled
                    || !replicationConfigHostMode
                    || !replicationConfigCarryDiagnostics)
                {
                    return;
                }

                var views = FindReplicationAnimatedAgentViews();
                var candidates = new List<ReplicationCarryProbeCandidate>(4);
                var deltaPosition = new Vector3(delta.GridX, delta.GridY * 3f, delta.GridZ);
                for (var i = 0; i < views.Length; i++)
                {
                    var view = views[i];
                    if (view == null
                        || view is not MonoBehaviour behaviour
                        || behaviour.gameObject == null
                        || !behaviour.gameObject.activeInHierarchy
                        || !TryClassifyReplicationView(view, out var kind)
                        || !string.Equals(kind, "worker", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var position = behaviour.transform.position;
                    var dx = position.x - deltaPosition.x;
                    var dz = position.z - deltaPosition.z;
                    var distanceSq = dx * dx + dz * dz;
                    if (distanceSq > ReplicationCarryProbeMaxDistanceSq)
                    {
                        continue;
                    }

                    InsertReplicationCarryProbeCandidate(candidates, new ReplicationCarryProbeCandidate(view, behaviour, distanceSq));
                }

                var builder = new StringBuilder(1200);
                builder.Append("Going Cooperative replication carry probe delta=")
                    .Append(delta.DeltaKind)
                    .Append(" sequence=")
                    .Append(delta.Sequence.ToString(CultureInfo.InvariantCulture))
                    .Append(" blueprintId=")
                    .Append(delta.BlueprintId)
                    .Append(" grid=Vec3Int(")
                    .Append(delta.GridX.ToString(CultureInfo.InvariantCulture))
                    .Append(",")
                    .Append(delta.GridY.ToString(CultureInfo.InvariantCulture))
                    .Append(",")
                    .Append(delta.GridZ.ToString(CultureInfo.InvariantCulture))
                    .Append(") candidates=")
                    .Append(candidates.Count.ToString(CultureInfo.InvariantCulture));

                for (var i = 0; i < candidates.Count; i++)
                {
                    AppendReplicationCarryProbeCandidate(builder, candidates[i], i);
                }

                current.LogReplicationInfo(TrimFingerprintText(builder.ToString(), 1800));
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative replication carry probe failed error="
                    + ex.GetType().Name
                    + ":"
                    + SanitizeLogDetail(ex.Message));
            }
        }

        private static bool TryFindNearestReplicationWorkerToDelta(ReplicationWorldObjectDelta delta, out string entityId, out string detail)
        {
            entityId = string.Empty;
            detail = "worker-not-found";
            var views = FindReplicationAnimatedAgentViews();
            object? bestView = null;
            MonoBehaviour? bestBehaviour = null;
            var bestDistanceSq = float.MaxValue;
            var scanned = 0;
            var deltaPosition = new Vector3(delta.GridX, delta.GridY * 3f, delta.GridZ);
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryClassifyReplicationView(view, out var kind)
                    || !string.Equals(kind, "worker", StringComparison.Ordinal))
                {
                    continue;
                }

                scanned++;
                var position = behaviour.transform.position;
                var dx = position.x - deltaPosition.x;
                var dz = position.z - deltaPosition.z;
                var distanceSq = dx * dx + dz * dz;
                if (distanceSq <= ReplicationCarryProbeMaxDistanceSq && distanceSq < bestDistanceSq)
                {
                    bestView = view;
                    bestBehaviour = behaviour;
                    bestDistanceSq = distanceSq;
                }
            }

            if (bestView == null || bestBehaviour == null || !TryGetReplicationViewEntityId(bestView, out entityId))
            {
                detail = "worker-not-found scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            detail = "worker=nearest scanned="
                + scanned.ToString(CultureInfo.InvariantCulture)
                + " distance="
                + Math.Sqrt(bestDistanceSq).ToString("F1", CultureInfo.InvariantCulture)
                + " workerPosition="
                + FormatReplicationPosition(bestBehaviour.transform.position);
            return true;
        }

        private static void InsertReplicationCarryProbeCandidate(List<ReplicationCarryProbeCandidate> candidates, ReplicationCarryProbeCandidate candidate)
        {
            var insertAt = candidates.Count;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidate.DistanceSq < candidates[i].DistanceSq)
                {
                    insertAt = i;
                    break;
                }
            }

            candidates.Insert(insertAt, candidate);
            if (candidates.Count > ReplicationCarryProbeMaxCandidates)
            {
                candidates.RemoveAt(candidates.Count - 1);
            }
        }

        private static void AppendReplicationCarryProbeCandidate(StringBuilder builder, ReplicationCarryProbeCandidate candidate, int index)
        {
            var view = candidate.View;
            var behaviour = candidate.Behaviour;
            TryGetReplicationViewEntityId(view, out var entityId);
            builder.Append(" | worker")
                .Append(index.ToString(CultureInfo.InvariantCulture))
                .Append("=")
                .Append(string.IsNullOrEmpty(entityId) ? "<no-id>" : entityId)
                .Append("@")
                .Append(FormatReplicationPosition(behaviour.transform.position))
                .Append(" dist=")
                .Append(Math.Sqrt(candidate.DistanceSq).ToString("F1", CultureInfo.InvariantCulture));

            if (TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out var owner) && owner != null)
            {
                AppendReplicationCarryProbeObject(builder, "owner", owner);
            }

            if (TryInvokeReplicationObjectMethod(view, "GetAgent", out var agent) && agent != null)
            {
                AppendReplicationCarryProbeObject(builder, "agent", agent);
            }

            AppendReplicationCarryProbeObject(builder, "view", view);
        }

        private static void AppendReplicationCarryProbeObject(StringBuilder builder, string label, object value)
        {
            if (builder.Length > 1600 || value == null)
            {
                return;
            }

            builder.Append(" ")
                .Append(label)
                .Append("=")
                .Append(FormatShortTypeName(value.GetType()));

            AppendReplicationCarryProbeNamedMembers(builder, value);
            AppendReplicationCarryProbeInterestingMembers(builder, value);
        }

        private static void AppendReplicationCarryProbeNamedMembers(StringBuilder builder, object value)
        {
            var memberNames = new[]
            {
                "Inventory",
                "inventory",
                "CurrentGoal",
                "currentGoal",
                "goalToForceStart",
                "GoalExecutionManager",
                "goalExecutionManager",
                "executionManager",
                "carriedResource",
                "CarriedResource",
                "carriedItem",
                "CarriedItem",
                "resourcePile",
                "ResourcePile",
                "targetResource",
                "TargetResource",
                "haulData",
                "HaulData"
            };

            for (var i = 0; i < memberNames.Length && builder.Length < 1600; i++)
            {
                if (!TryReadInstanceMemberValue(value, memberNames[i], out var memberValue) || memberValue == null)
                {
                    continue;
                }

                builder.Append("[")
                    .Append(memberNames[i])
                    .Append("=")
                    .Append(TrimFingerprintText(FormatCommandSurfaceValue(memberValue, 1), 160))
                    .Append("]");
            }
        }

        private static void AppendReplicationCarryProbeInterestingMembers(StringBuilder builder, object value)
        {
            var type = value.GetType();
            var appended = 0;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var current = type; current != null && current != typeof(object) && appended < 8 && builder.Length < 1600; current = current.BaseType)
            {
                var fields = current.GetFields(Flags | BindingFlags.DeclaredOnly);
                for (var i = 0; i < fields.Length && appended < 8 && builder.Length < 1600; i++)
                {
                    if (!IsReplicationCarryProbeInterestingName(fields[i].Name))
                    {
                        continue;
                    }

                    object? fieldValue;
                    try
                    {
                        fieldValue = fields[i].GetValue(value);
                    }
                    catch
                    {
                        continue;
                    }

                    if (fieldValue == null)
                    {
                        continue;
                    }

                    AppendReplicationCarryProbeMemberValue(builder, fields[i].Name, fieldValue);
                    appended++;
                }

                var properties = current.GetProperties(Flags | BindingFlags.DeclaredOnly);
                for (var i = 0; i < properties.Length && appended < 8 && builder.Length < 1600; i++)
                {
                    if (!IsReplicationCarryProbeInterestingName(properties[i].Name)
                        || properties[i].GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    object? propertyValue;
                    try
                    {
                        propertyValue = properties[i].GetValue(value, null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (propertyValue == null)
                    {
                        continue;
                    }

                    AppendReplicationCarryProbeMemberValue(builder, properties[i].Name, propertyValue);
                    appended++;
                }
            }
        }

        private static void AppendReplicationCarryProbeMemberValue(StringBuilder builder, string name, object value)
        {
            builder.Append("[")
                .Append(name)
                .Append("=")
                .Append(TrimFingerprintText(FormatCommandSurfaceValue(value, 1), 160))
                .Append("]");
        }

        private static bool IsReplicationCarryProbeInterestingName(string name)
        {
            return name.IndexOf("carry", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("haul", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("equip", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("resource", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("pile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryFindReplicationAnimatedAgentViewByEntityId(string entityId, out object? view, out string detail)
        {
            view = null;
            var views = FindReplicationAnimatedAgentViews();
            var scanned = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var candidate = views[i];
                if (candidate == null
                    || candidate is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryClassifyReplicationView(candidate, out _))
                {
                    continue;
                }

                scanned++;
                if (TryGetReplicationViewEntityId(candidate, out var candidateId)
                    && string.Equals(candidateId, entityId, StringComparison.Ordinal))
                {
                    view = candidate;
                    detail = "matched=entityId scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            detail = "not-found scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryFindReplicationAgentOwnerByEntityId(string entityId, out object? owner, out string detail)
        {
            owner = null;
            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = viewDetail;
                return false;
            }

            if (TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out owner) && owner != null)
            {
                detail = "owner=GetAgentOwner " + viewDetail;
                return true;
            }

            if (TryInvokeReplicationObjectMethod(view, "GetAgent", out var agent) && agent != null
                && TryInvokeReplicationObjectMethod(agent, "get_AgentOwner", out owner) && owner != null)
            {
                detail = "owner=GetAgent.AgentOwner " + viewDetail;
                return true;
            }

            detail = "owner-missing " + viewDetail;
            return false;
        }

        private static bool TryFindReplicationAgentOwnerBySkills(object skills, out object? owner, out string detail)
        {
            owner = null;
            var views = FindReplicationAnimatedAgentViews();
            var scanned = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var view = views[i];
                if (view == null
                    || view is not MonoBehaviour behaviour
                    || behaviour.gameObject == null
                    || !behaviour.gameObject.activeInHierarchy
                    || !TryClassifyReplicationView(view, out _)
                    || !TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out var candidateOwner)
                    || candidateOwner == null)
                {
                    continue;
                }

                scanned++;
                if ((!TryReadInstanceMemberValue(candidateOwner, "Skills", out var candidateSkills) || candidateSkills == null)
                    && (!TryInvokeReplicationObjectMethod(candidateOwner, "get_Skills", out candidateSkills) || candidateSkills == null))
                {
                    continue;
                }

                if (!ReferenceEquals(candidateSkills, skills))
                {
                    continue;
                }

                owner = candidateOwner;
                detail = "owner=skills-reference scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            detail = "owner=skills-reference-not-found scanned=" + scanned.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool TryResolveReplicationSkillType(string skillToken, out object? skillType, out string detail)
        {
            skillType = null;
            var type = AccessTools.TypeByName("NSMedieval.StatsSystem.SkillType");
            if (type == null)
            {
                detail = "skill-type-enum-missing";
                return false;
            }

            try
            {
                skillType = Enum.Parse(type, skillToken, ignoreCase: true);
                detail = "skill-type=ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = "skill-type-parse-failed error=" + ex.GetType().Name;
                return false;
            }
        }

        private static void RefreshReplicationSkillUiForEntity(string entityId)
        {
            var type = AccessTools.TypeByName("NSMedieval.UI.SkillLayoutItemView");
            if (type == null)
            {
                return;
            }

            UnityEngine.Object[] entries;
            try
            {
                entries = UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
            }
            catch
            {
                return;
            }

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                if ((!TryReadInstanceMemberValue(entry, "Humanoid", out var humanoid) || humanoid == null)
                    && (!TryReadInstanceMemberValue(entry, "humanoid", out humanoid) || humanoid == null))
                {
                    continue;
                }

                if (!TryGetReplicationAgentOwnerEntityId(humanoid, out var candidateId, out _)
                    || !string.Equals(candidateId, entityId, StringComparison.Ordinal))
                {
                    continue;
                }

                TryInvokeReplicationObjectMethod(entry, "RefreshSpecificView", Array.Empty<object>(), out _);
                TryInvokeReplicationObjectMethod(entry, "RefreshSharedView", Array.Empty<object>(), out _);
            }
        }

        private static bool TryReadReplicationWorldObjectDetailToken(string detail, string name, out string value)
        {
            value = string.Empty;
            var marker = name + "=";
            var index = detail.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            index += marker.Length;
            var end = index;
            while (end < detail.Length && !char.IsWhiteSpace(detail[end]))
            {
                end++;
            }

            value = detail.Substring(index, end - index);
            return value.Length > 0;
        }

        private static bool TryParseReplicationEntityNumericId(string entityId, out long value)
        {
            value = 0L;
            var colon = entityId.LastIndexOf(':');
            var token = colon >= 0 && colon + 1 < entityId.Length
                ? entityId.Substring(colon + 1)
                : entityId;
            return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static MethodInfo? FindReplicationInstanceMethod(Type type, string methodName, Type[] parameterTypes)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var method = current.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    parameterTypes,
                    null);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }

        private static void FillReplicationWorldObjectPositionFromArgs(object[]? args, ref int x, ref int y, ref int z)
        {
            if (args == null || (x != 0 || y != 0 || z != 0))
            {
                return;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] != null && TryReadVec3IntLikeValue(args[i], out x, out y, out z))
                {
                    var typeName = args[i].GetType().FullName ?? args[i].GetType().Name;
                    if (typeName.IndexOf("Vector3", StringComparison.OrdinalIgnoreCase) >= 0
                        && typeName.IndexOf("Vec3Int", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        y = NormalizePossibleWorldY(y);
                    }

                    return;
                }
            }
        }

        private static bool TryReadReplicationWorldObjectIntMember(object value, string upperName, string lowerName, out int number)
        {
            number = 0;
            if ((!TryReadInstanceMemberValue(value, upperName, out var memberValue) || memberValue == null)
                && (!TryReadInstanceMemberValue(value, lowerName, out memberValue) || memberValue == null))
            {
                return false;
            }

            try
            {
                number = Convert.ToInt32(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadReplicationWorldObjectLongMember(object value, string upperName, string lowerName, out long number)
        {
            number = 0L;
            if ((!TryReadInstanceMemberValue(value, upperName, out var memberValue) || memberValue == null)
                && (!TryReadInstanceMemberValue(value, lowerName, out memberValue) || memberValue == null))
            {
                return false;
            }

            try
            {
                number = Convert.ToInt64(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadReplicationWorldObjectStringMember(object value, string upperName, string lowerName, out string text)
        {
            text = string.Empty;
            if ((!TryReadInstanceMemberValue(value, upperName, out var memberValue) || memberValue == null)
                && (!TryReadInstanceMemberValue(value, lowerName, out memberValue) || memberValue == null))
            {
                return false;
            }

            text = Convert.ToString(memberValue, CultureInfo.InvariantCulture) ?? string.Empty;
            return text.Length > 0;
        }

        private static bool TryReadReplicationWorldObjectGridPosition(object value, out int x, out int y, out int z)
        {
            var type = value.GetType();
            return TryReadVec3IntMember(value, type, "gridDataPosition", out x, out y, out z)
                || TryReadVec3IntMember(value, type, "GridDataPosition", out x, out y, out z)
                || TryReadVec3IntMember(value, type, "gridPosition", out x, out y, out z)
                || TryReadVec3IntMember(value, type, "GridPosition", out x, out y, out z)
                || TryReadVec3IntMember(value, type, "position", out x, out y, out z)
                || TryReadVec3IntMember(value, type, "Position", out x, out y, out z);
        }

        private static bool IsReplicationWorldObjectDeltaModeOff()
        {
            return string.Equals(replicationConfigWorldObjectDeltaMode, "off", StringComparison.Ordinal);
        }

        private static bool IsReplicationWorldObjectDeltaModeShadow()
        {
            return string.Equals(replicationConfigWorldObjectDeltaMode, "shadow", StringComparison.Ordinal);
        }

        private static bool TryReadReplicationWorldObjectDetailInt(string detail, string name, out int value)
        {
            value = 0;
            var marker = name + "=";
            var index = detail.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            index += marker.Length;
            var end = index;
            while (end < detail.Length && (char.IsDigit(detail[end]) || (end == index && detail[end] == '-')))
            {
                end++;
            }

            return end > index
                && int.TryParse(detail.Substring(index, end - index), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadReplicationWorldObjectDetailFloat(string detail, string name, out float value)
        {
            value = 0f;
            if (!TryReadReplicationWorldObjectDetailToken(detail, name, out var token))
            {
                return false;
            }

            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadReplicationWorldObjectDetailHex(string detail, string name, out ulong value)
        {
            value = 0UL;
            if (!TryReadReplicationWorldObjectDetailToken(detail, name, out var token))
            {
                return false;
            }

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(2);
            }

            return token.Length > 0
                && ulong.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadReplicationWorldObjectDetailBool(string detail, string name, out bool value)
        {
            value = false;
            if (!TryReadReplicationWorldObjectDetailToken(detail, name, out var token))
            {
                return false;
            }

            if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "1", StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            if (string.Equals(token, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "0", StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            return false;
        }

        private static string FormatReplicationWorldObjectDeltaSpawnKey(ReplicationWorldObjectDelta delta)
        {
            return delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                + "|"
                + delta.BlueprintId
                + "|"
                + delta.GridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridZ.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatReplicationWorldObjectDeltaLocationKey(ReplicationWorldObjectDelta delta)
        {
            return FormatReplicationWorldObjectDeltaLocationKey(delta.BlueprintId, delta.GridX, delta.GridY, delta.GridZ);
        }

        private static string FormatReplicationWorldObjectDeltaCoalesceKey(ReplicationWorldObjectDelta delta)
        {
            if (string.Equals(delta.DeltaKind, CombatHealthDeltaKind, StringComparison.Ordinal))
            {
                // Health is current authoritative state. Preserve only the newest
                // unapplied sample per entity while keeping outcomes/death as events.
                return FormatReplicationEntityWorldObjectDeltaCoalesceKey(delta);
            }

            if (string.Equals(delta.DeltaKind, ManagementDeltaKind, StringComparison.Ordinal)
                && LockstepCommandPayloads.TryReadManagementPolicyPayload(
                    delta.Detail,
                    out var policy,
                    out var targetId,
                    out var key,
                    out var index,
                    out _,
                    out _))
            {
                // Management deltas describe current state, not events. If several
                // revisions arrive before the main thread can apply them, retaining
                // only the newest value prevents stale UI edits from building a queue.
                return ManagementDeltaKind
                    + "|policy=" + policy
                    + "|target=" + targetId
                    + (string.Equals(policy, "AnimalOrder", StringComparison.Ordinal) ? string.Empty : "|key=" + key)
                    + "|index=" + index.ToString(CultureInfo.InvariantCulture);
            }

            if (string.Equals(delta.DeltaKind, "GameTimeSnapshot", StringComparison.Ordinal))
            {
                return "GameTimeSnapshot";
            }

            if (string.Equals(delta.DeltaKind, "BuildingState", StringComparison.Ordinal))
            {
                return "BuildingState|"
                    + (delta.UniqueId != 0L
                        ? delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                        : FormatReplicationWorldObjectDeltaLocationKey(delta));
            }

            if (string.Equals(delta.DeltaKind, "AgentCarryResourceChanged", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentCarryResourceCleared", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentCarryEquipped", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentCarryCleared", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentActionStatus", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentActionHeartbeat", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentSkillExperience", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentCharacterState", StringComparison.Ordinal))
            {
                return FormatReplicationEntityWorldObjectDeltaCoalesceKey(delta);
            }

            if (string.Equals(delta.DeltaKind, "AgentProgressUpdated", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentProgressCleared", StringComparison.Ordinal))
            {
                var overlayType = TryReadReplicationWorldObjectDetailToken(delta.Detail, "overlay", out var parsedOverlay)
                    ? parsedOverlay
                    : TryReadReplicationWorldObjectDetailToken(delta.Detail, "overlayType", out var parsedOverlayType)
                    ? parsedOverlayType
                    : string.Empty;
                return FormatReplicationEntityWorldObjectDeltaCoalesceKey(delta)
                    + "|overlay="
                    + overlayType;
            }

            if (string.Equals(delta.DeltaKind, "AgentAnimationTriggered", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationReset", StringComparison.Ordinal)
                || string.Equals(delta.DeltaKind, "AgentAnimationQuit", StringComparison.Ordinal))
            {
                var animation = TryReadReplicationWorldObjectDetailToken(delta.Detail, "animation", out var parsedAnimation)
                    ? parsedAnimation
                    : TryReadReplicationWorldObjectDetailToken(delta.Detail, "trigger", out var parsedTrigger)
                        ? parsedTrigger
                    : string.Empty;
                return delta.DeltaKind
                    + "|"
                    + FormatReplicationEntityWorldObjectDeltaCoalesceKey(delta)
                    + "|animation="
                    + animation;
            }

            return string.Empty;
        }

        private static string FormatReplicationEntityWorldObjectDeltaCoalesceKey(ReplicationWorldObjectDelta delta)
        {
            if (TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                && !string.IsNullOrWhiteSpace(entityId))
            {
                return delta.DeltaKind + "|entity=" + entityId;
            }

            if (delta.UniqueId != 0L)
            {
                return delta.DeltaKind + "|uid=" + delta.UniqueId.ToString(CultureInfo.InvariantCulture);
            }

            return delta.DeltaKind
                + "|loc="
                + delta.GridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridZ.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatReplicationWorldObjectDeltaLocationKey(string blueprintId, int gridX, int gridY, int gridZ)
        {
            return blueprintId
                + "|"
                + gridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + gridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + gridZ.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatReplicationWorldObjectDetailToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "_";
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                builder.Append(char.IsWhiteSpace(c) ? '_' : c);
            }

            return builder.ToString();
        }

        private static string FormatReplicationBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string EncodeReplicationDetailBase64(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "_";
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static bool TryDecodeReplicationDetailBase64(string token, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(token) || string.Equals(token, "_", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                value = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                return value.Length > 0;
            }
            catch
            {
                value = string.Empty;
                return false;
            }
        }

        private static bool WasReplicationWorldObjectSpawnLocationRecentlyApplied(string locationKey)
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                CleanupRecentReplicationWorldObjectSpawnLocations();
                return ReplicationWorldObjectDeltaRecentSpawnLocationAt.ContainsKey(locationKey);
            }
        }

        private static void CleanupRecentReplicationWorldObjectSpawnLocations()
        {
            if (ReplicationWorldObjectDeltaRecentSpawnLocationAt.Count == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            var expired = new List<string>();
            foreach (var pair in ReplicationWorldObjectDeltaRecentSpawnLocationAt)
            {
                if (now - pair.Value > ReplicationWorldObjectDeltaRecentSpawnLocationSeconds)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ReplicationWorldObjectDeltaRecentSpawnLocationAt.Remove(expired[i]);
            }
        }

        private static string FormatReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta)
        {
            return "sequence="
                + delta.Sequence.ToString(CultureInfo.InvariantCulture)
                + " kind="
                + delta.DeltaKind
                + " uniqueId="
                + delta.UniqueId.ToString(CultureInfo.InvariantCulture)
                + " blueprintId="
                + delta.BlueprintId
                + " grid=Vec3Int("
                + delta.GridX.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridY.ToString(CultureInfo.InvariantCulture)
                + ","
                + delta.GridZ.ToString(CultureInfo.InvariantCulture)
                + ") detail="
                + delta.Detail;
        }

        private static readonly string[] ReplicationWorldObjectNestedMemberNames =
        {
            "resourcePileInstance",
            "ResourcePileInstance",
            "pileInstance",
            "PileInstance",
            "resourceInstance",
            "ResourceInstance",
            "mapResourceInstance",
            "MapResourceInstance",
            "plantMapResourceInstance",
            "PlantMapResourceInstance",
            "instance",
            "Instance"
        };

        private static readonly string[] ReplicationResourceLookupMethodNames =
        {
            "GetByID",
            "GetByProtoID",
            "GetByIdOrDefault",
            "GetProtoItemById",
            "GetByGroup",
            "GetByGroupIdentifier"
        };

        private const float ReplicationWorldObjectDeltaRetrySeconds = 0.75f;
        private const int ReplicationWorldObjectDeltaMaxSends = 5;
        private const float ReplicationWorldObjectDeltaRecentSpawnLocationSeconds = 4f;
        private const float ReplicationResourcePileStateSnapshotSeconds = 15f;
        private const float ReplicationResourcePileStateSnapshotClientApplyDelaySeconds = 30f;
        private const int ReplicationResourcePileStateSnapshotMaxPiles = 256;
        private const int ReplicationResourcePileStateSnapshotChunkMaxPiles = 8;
        private const int ReplicationResourcePileStateSnapshotContextMaxCount = 4;
        private const int ReplicationResourcePileStateSnapshotApplyMaxPiles = 256;
        private const int ReplicationResourcePileStateSnapshotApplyMaxQueuedSnapshots = 2;
        private const int ReplicationResourcePileStateSnapshotApplyFrameMaxPiles = 4;
        private const int ReplicationResourcePileStateSnapshotApplyFrameMaxChanges = 1;
        private const float ReplicationAgentCarryStateSnapshotSeconds = 2f;
        private const int ReplicationAgentCarryStateSnapshotMaxAgents = 128;
        private const float ReplicationAgentCharacterStateSnapshotSeconds = 2f;
        private const int ReplicationAgentCharacterStateSnapshotMaxAgents = 128;
        private const int ReplicationAgentCharacterStateCollectionMaxItems = 128;
        private const ulong ReplicationCharacterStateFnvOffset = 14695981039346656037UL;
        private const ulong ReplicationCharacterStateFnvPrime = 1099511628211UL;
        private const int ReplicationAgentProgressMinPermilleStep = 100;
        private const float ReplicationAgentActionHeartbeatSeconds = 1.0f;
        private const int ReplicationPuppetActionTtlMs = 3000;
        private const float ReplicationPuppetActionReassertSeconds = 1.25f;
        private const float ReplicationPuppetActionVisualReassertSeconds = 2.0f;
        private const float ReplicationBuildingStateSnapshotSeconds = 2f;
        private const int ReplicationBuildingStateSnapshotMaxBuildings = 256;
        private const int ReplicationBuildingStateSnapshotContextMaxCount = 4;
        private const int ReplicationResourcePileRecentLookupMaxCandidates = 12;
        private const int ReplicationResourcePileLocationIndexBudgetPerFrame = 12;
        private const float ReplicationResourcePileLocationIndexIntervalSeconds = 0.02f;
        private const int ReplicationCarryProbeMaxCandidates = 3;
        private const float ReplicationCarryProbeMaxDistanceSq = 144f;
        private const float ReplicationCarryPendingInferenceSeconds = 2f;
        private const float ReplicationCarryPendingDropSeconds = 2f;

        private sealed class PendingReplicationWorldObjectDelta
        {
            public PendingReplicationWorldObjectDelta(ReplicationWorldObjectDelta delta)
            {
                Delta = delta;
                LastSentRealtime = -ReplicationWorldObjectDeltaRetrySeconds;
            }

            public ReplicationWorldObjectDelta Delta { get; }
            public float LastSentRealtime { get; set; }
            public int SendCount { get; set; }
        }

        private sealed class PendingReplicationClientWorldObjectDeltaApply
        {
            public PendingReplicationClientWorldObjectDeltaApply(ReplicationWorldObjectDelta delta, string coalesceKey)
            {
                Delta = delta;
                CoalesceKey = coalesceKey;
            }

            public ReplicationWorldObjectDelta Delta { get; }

            public string CoalesceKey { get; }

            public bool IsStale { get; set; }
        }

        private sealed class ReplicationResourcePileState
        {
            public ReplicationResourcePileState(long uniqueId, string blueprintId, int gridX, int gridY, int gridZ, int amount)
            {
                UniqueId = uniqueId;
                BlueprintId = blueprintId;
                GridX = gridX;
                GridY = gridY;
                GridZ = gridZ;
                Amount = amount;
            }

            public long UniqueId { get; }

            public string BlueprintId { get; }

            public int GridX { get; }

            public int GridY { get; }

            public int GridZ { get; }

            public int Amount { get; }
        }

        private sealed class PendingReplicationResourcePileStateSnapshot
        {
            public PendingReplicationResourcePileStateSnapshot(long snapshotId, List<ReplicationResourcePileState> states, string collectDetail)
            {
                SnapshotId = snapshotId;
                States = states;
                CollectDetail = collectDetail;
            }

            public long SnapshotId { get; }

            public List<ReplicationResourcePileState> States { get; }

            public string CollectDetail { get; }

            public bool BeginSent { get; set; }

            public int NextIndex { get; set; }
        }

        private sealed class PendingReplicationResourcePileStateSnapshotApply
        {
            public PendingReplicationResourcePileStateSnapshotApply(long snapshotId, List<ReplicationResourcePileState> states)
            {
                SnapshotId = snapshotId;
                States = states;
            }

            public long SnapshotId { get; }

            public List<ReplicationResourcePileState> States { get; }

            public int NextIndex { get; set; }

            public int Synced { get; set; }

            public int Unchanged { get; set; }

            public int Missing { get; set; }

            public int Failed { get; set; }
        }

        private sealed class ReplicationAgentCarryState
        {
            public ReplicationAgentCarryState(string entityId, long uniqueId, string blueprintId, int amount)
            {
                EntityId = entityId;
                UniqueId = uniqueId;
                BlueprintId = blueprintId;
                Amount = amount;
            }

            public string EntityId { get; }

            public long UniqueId { get; }

            public string BlueprintId { get; }

            public int Amount { get; }
        }

        private sealed class ReplicationAgentCharacterState
        {
            public ReplicationAgentCharacterState(
                string entityId,
                long uniqueId,
                string kind,
                bool hasDied,
                bool hasFainted,
                bool isSleeping,
                bool isWounded,
                bool isBleeding,
                bool isBeingCarried,
                bool isIdle,
                bool isResting,
                bool isDrafting,
                string combatMode,
                bool hasHunger,
                float hungerCurrent,
                bool hasSleep,
                float sleepCurrent,
                ulong statsSignature,
                ulong attributesSignature,
                ulong skillsSignature,
                ulong signature)
            {
                EntityId = entityId;
                UniqueId = uniqueId;
                Kind = kind;
                HasDied = hasDied;
                HasFainted = hasFainted;
                IsSleeping = isSleeping;
                IsWounded = isWounded;
                IsBleeding = isBleeding;
                IsBeingCarried = isBeingCarried;
                IsIdle = isIdle;
                IsResting = isResting;
                IsDrafting = isDrafting;
                CombatMode = combatMode;
                HasHunger = hasHunger;
                HungerCurrent = hungerCurrent;
                HasSleep = hasSleep;
                SleepCurrent = sleepCurrent;
                StatsSignature = statsSignature;
                AttributesSignature = attributesSignature;
                SkillsSignature = skillsSignature;
                Signature = signature;
            }

            public string EntityId { get; }

            public long UniqueId { get; }

            public string Kind { get; }

            public bool HasDied { get; }

            public bool HasFainted { get; }

            public bool IsSleeping { get; }

            public bool IsWounded { get; }

            public bool IsBleeding { get; }

            public bool IsBeingCarried { get; }

            public bool IsIdle { get; }

            public bool IsResting { get; }

            public bool IsDrafting { get; }

            public string CombatMode { get; }

            public bool HasHunger { get; }

            public float HungerCurrent { get; }

            public bool HasSleep { get; }

            public float SleepCurrent { get; }

            public ulong StatsSignature { get; }

            public ulong AttributesSignature { get; }

            public ulong SkillsSignature { get; }

            public ulong Signature { get; }
        }

        private sealed class ReplicationAgentProgressOwner
        {
            public ReplicationAgentProgressOwner(string entityId, string overlayType)
            {
                EntityId = entityId;
                OverlayType = overlayType;
            }

            public string EntityId { get; }

            public string OverlayType { get; }
        }

        private sealed class ReplicationAgentActionStatus
        {
            public ReplicationAgentActionStatus(string goalId, string statusText, bool hasStarted, float updatedRealtime, string animationToken, string animatorStateDetail, string actionHandItemId)
            {
                GoalId = goalId;
                StatusText = statusText;
                HasStarted = hasStarted;
                UpdatedRealtime = updatedRealtime;
                AnimationToken = animationToken;
                AnimatorStateDetail = animatorStateDetail;
                ActionHandItemId = actionHandItemId;
            }

            public string GoalId { get; }

            public string StatusText { get; }

            public bool HasStarted { get; }

            public float UpdatedRealtime { get; }

            public string AnimationToken { get; }

            public string AnimatorStateDetail { get; }

            public string ActionHandItemId { get; }
        }

        private sealed class ReplicationGoapActionOwnerBinding
        {
            public ReplicationGoapActionOwnerBinding(string entityId, float updatedRealtime, string lifecycleMethod, string ownerDetail)
            {
                EntityId = entityId;
                UpdatedRealtime = updatedRealtime;
                LifecycleMethod = lifecycleMethod;
                OwnerDetail = ownerDetail;
            }

            public string EntityId { get; }

            public float UpdatedRealtime { get; }

            public string LifecycleMethod { get; }

            public string OwnerDetail { get; }
        }

        private sealed class PendingReplicationActionAnimationOwner
        {
            public PendingReplicationActionAnimationOwner(string entityId, string goalId, string statusText, float queuedRealtime)
            {
                EntityId = entityId;
                GoalId = goalId;
                StatusText = statusText;
                QueuedRealtime = queuedRealtime;
            }

            public string EntityId { get; }

            public string GoalId { get; }

            public string StatusText { get; }

            public float QueuedRealtime { get; }
        }

        private sealed class ReplicationPuppetActionState
        {
            public ReplicationPuppetActionState(string goalId, string statusText, string animationToken, string animatorStateDetail, string actionHandItemId, float expiresRealtime)
            {
                GoalId = goalId;
                StatusText = statusText;
                AnimationToken = animationToken;
                AnimatorStateDetail = animatorStateDetail;
                ActionHandItemId = actionHandItemId;
                ExpiresRealtime = expiresRealtime;
            }

            public string GoalId { get; }

            public string StatusText { get; }

            public string AnimationToken { get; }

            public string AnimatorStateDetail { get; }

            public string ActionHandItemId { get; }

            public float ExpiresRealtime { get; set; }

            public float NextReassertRealtime { get; set; }

            public float NextVisualRealtime { get; set; }
        }

        private sealed class ReplicationGoapActionPhase
        {
            public ReplicationGoapActionPhase(
                string actionId,
                string goalId,
                string phase,
                string targetId,
                string targetBlueprintId,
                int targetX,
                int targetY,
                int targetZ,
                float updatedRealtime)
            {
                ActionId = actionId;
                GoalId = goalId;
                Phase = phase;
                TargetId = targetId;
                TargetBlueprintId = targetBlueprintId;
                TargetX = targetX;
                TargetY = targetY;
                TargetZ = targetZ;
                UpdatedRealtime = updatedRealtime;
            }

            public string ActionId { get; }

            public string GoalId { get; }

            public string Phase { get; }

            public string TargetId { get; }

            public string TargetBlueprintId { get; }

            public int TargetX { get; }

            public int TargetY { get; }

            public int TargetZ { get; }

            public float UpdatedRealtime { get; }
        }

        private sealed class ReplicationBuildingState
        {
            public ReplicationBuildingState(
                long uniqueId,
                string blueprintId,
                int gridX,
                int gridY,
                int gridZ,
                string constructionPhase,
                int remainingTimeMs,
                bool isUnderConstruction,
                bool isForbidden,
                bool markedForDestruction)
            {
                UniqueId = uniqueId;
                BlueprintId = blueprintId;
                GridX = gridX;
                GridY = gridY;
                GridZ = gridZ;
                ConstructionPhase = constructionPhase;
                RemainingTimeMs = remainingTimeMs;
                IsUnderConstruction = isUnderConstruction;
                IsForbidden = isForbidden;
                MarkedForDestruction = markedForDestruction;
            }

            public long UniqueId { get; }

            public string BlueprintId { get; }

            public int GridX { get; }

            public int GridY { get; }

            public int GridZ { get; }

            public string ConstructionPhase { get; }

            public int RemainingTimeMs { get; }

            public bool IsUnderConstruction { get; }

            public bool IsForbidden { get; }

            public bool MarkedForDestruction { get; }
        }

        private sealed class ReplicationBuildingStateSnapshotContext
        {
            public ReplicationBuildingStateSnapshotContext(int expectedCount, int scanned, int sent, int skipped, int cap)
            {
                ExpectedCount = expectedCount;
                Scanned = scanned;
                Sent = sent;
                Skipped = skipped;
                Cap = cap;
            }

            public int ExpectedCount { get; set; }

            public int Scanned { get; }

            public int Sent { get; }

            public int Skipped { get; }

            public int Cap { get; }

            public bool EndReceived { get; set; }

            public bool HostSignatureValid { get; set; }

            public ulong HostSignature { get; set; }

            public HashSet<string> SeenKeys { get; } = new HashSet<string>(StringComparer.Ordinal);

            public Dictionary<string, ReplicationBuildingState> States { get; } = new Dictionary<string, ReplicationBuildingState>(StringComparer.Ordinal);
        }

        private sealed class ReplicationResourcePileStateSnapshotContext
        {
            public ReplicationResourcePileStateSnapshotContext(int expectedCount)
            {
                ExpectedCount = expectedCount;
            }

            public int ExpectedCount { get; set; }

            public bool EndReceived { get; set; }

            public bool HostSignatureValid { get; set; }

            public ulong HostSignature { get; set; }

            public HashSet<string> SeenKeys { get; } = new HashSet<string>(StringComparer.Ordinal);

            public Dictionary<string, ReplicationResourcePileState> States { get; } = new Dictionary<string, ReplicationResourcePileState>(StringComparer.Ordinal);
        }

        private sealed class ReplicationCarryProbeCandidate
        {
            public ReplicationCarryProbeCandidate(object view, MonoBehaviour behaviour, float distanceSq)
            {
                View = view;
                Behaviour = behaviour;
                DistanceSq = distanceSq;
            }

            public object View { get; }

            public MonoBehaviour Behaviour { get; }

            public float DistanceSq { get; }
        }

        private sealed class PendingReplicationCarryInference
        {
            public PendingReplicationCarryInference(
                string entityId,
                long uniqueId,
                int gridX,
                int gridY,
                int gridZ,
                long sourceSequence,
                float realtime)
            {
                EntityId = entityId;
                UniqueId = uniqueId;
                GridX = gridX;
                GridY = gridY;
                GridZ = gridZ;
                SourceSequence = sourceSequence;
                Realtime = realtime;
            }

            public string EntityId { get; }

            public long UniqueId { get; }

            public int GridX { get; }

            public int GridY { get; }

            public int GridZ { get; }

            public long SourceSequence { get; }

            public float Realtime { get; }
        }

        private sealed class PendingReplicationCarryDrop
        {
            public PendingReplicationCarryDrop(string blueprintId, int amount, int gridX, int gridY, int gridZ, long sourceSequence, float realtime)
            {
                BlueprintId = blueprintId;
                Amount = amount;
                GridX = gridX;
                GridY = gridY;
                GridZ = gridZ;
                SourceSequence = sourceSequence;
                Realtime = realtime;
            }

            public string BlueprintId { get; }

            public int Amount { get; }

            public int GridX { get; }

            public int GridY { get; }

            public int GridZ { get; }

            public long SourceSequence { get; }

            public float Realtime { get; }
        }
    }
}
