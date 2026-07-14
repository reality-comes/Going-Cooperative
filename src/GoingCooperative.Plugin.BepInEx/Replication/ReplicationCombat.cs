using System;
using System.Collections;
using System.Collections.Generic;
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
        private const string CombatStateDeltaKind = "CombatState";
        private const string CombatOutcomeDeltaKind = "CombatOutcome";
        private const string CombatHealthDeltaKind = "CombatHealth";
        private const string CombatDeathDeltaKind = "CombatDeath";
        private const string CombatPresentationDeltaKind = "CombatPresentation";
        private static long replicationCombatOutcomeSequence;
        private static long replicationCombatPresentationSequence;
        private static long replicationCombatChargeSequence;
        private static readonly Dictionary<string, object> ReplicationCombatStatTypeValueByName = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, MethodInfo> ReplicationCombatGetStatByStatsType = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> ReplicationCombatForceStatByStatType = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<string, float> ReplicationCombatLastWoundTickSentAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> ReplicationCombatPresentationExpiryByEntityId = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReplicationCombatClientTombstones = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationCombatHostChargeState> ReplicationCombatHostChargeByEntityId = new Dictionary<string, ReplicationCombatHostChargeState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationCombatHostChargeState> ReplicationCombatHostLastCompletedChargeByEntityId = new Dictionary<string, ReplicationCombatHostChargeState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationCombatClientChargeState> ReplicationCombatClientChargeByEntityId = new Dictionary<string, ReplicationCombatClientChargeState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> ReplicationCombatClientLastCompletedChargeIdByEntityId = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationCombatLatestPresentationTickByEntityId = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly List<string> ReplicationCombatExpiredHostChargeEntityIds = new List<string>();
        private static readonly List<string> ReplicationCombatExpiredClientChargeEntityIds = new List<string>();
        private static readonly List<string> ReplicationCombatExpiredPresentationEntityIds = new List<string>();

        private sealed class ReplicationCombatHostChargeState
        {
            public string EntityId = string.Empty;
            public string ChargeId = string.Empty;
            public string AttackKind = LockstepCommandPayloads.CombatPresentationAttackKindUnknown;
            public float DurationSeconds;
            public int WeaponType;
            public int AttackRnd;
            public float ExpiresGameTime;
            public bool Released;
        }

        private sealed class ReplicationCombatClientChargeState
        {
            public string ChargeId = string.Empty;
            public string AnimationToken = "Attack";
            public long LatestEventTick;
            public float DurationSeconds;
            public float StartedGameTime;
            public float ExpiresGameTime;
            public object? View;
            public object? ProgressBar;
            public object? Timer;
            public Animator? Animator;
            public bool HasAttackMotionTime;
        }

        private void TryInstallReplicationCombatHooks(Harmony harmony)
        {
            if (!replicationConfigCombatReplication) return;

            var draftPrefix = CombatHarmony(nameof(ReplicationCombatDraftStatePrefix));
            var statePostfix = CombatHarmony(nameof(ReplicationCombatStateMutationPostfix));
            var modePrefix = CombatHarmony(nameof(ReplicationCombatModePrefix));
            var movePrefix = CombatHarmony(nameof(ReplicationCombatMovePrefix));
            var attackPrefix = CombatHarmony(nameof(ReplicationCombatAttackPrefix));
            var attackPostfix = CombatHarmony(nameof(ReplicationCombatAttackPostfix));
            var cancelPrefix = CombatHarmony(nameof(ReplicationCombatCancelPrefix));
            var cancelPostfix = CombatHarmony(nameof(ReplicationCombatCancelPostfix));
            var outcomePostfix = CombatHarmony(nameof(ReplicationCombatOutcomePostfix));
            var chargeStartPrefix = CombatHarmony(nameof(ReplicationCombatChargeStartPrefix));
            var chargeStartPostfix = CombatHarmony(nameof(ReplicationCombatChargeStartPostfix));
            var chargeEndPrefix = CombatHarmony(nameof(ReplicationCombatChargeEndPrefix));
            var weaponReleasePrefix = CombatHarmony(nameof(ReplicationCombatWeaponReleasePrefix));
            var finalHealthPostfix = CombatHarmony(nameof(ReplicationCombatFinalHealthPostfix));
            var lifecycleHealthPostfix = CombatHarmony(nameof(ReplicationCombatLifecycleHealthPostfix));
            var woundTickPostfix = CombatHarmony(nameof(ReplicationCombatWoundTickPostfix));
            var animalDeathPrefix = CombatHarmony(nameof(ReplicationCombatAnimalDeathPrefix));
            var animalDeathPostfix = CombatHarmony(nameof(ReplicationCombatAnimalDeathPostfix));
            var storageDropPrefix = CombatHarmony(nameof(ReplicationCombatStorageDropPrefix));
            var count = 0;

            if (replicationConfigCombatDraftCommands)
            {
                count += PatchCombat(harmony, "NSMedieval.Controllers.DraftController", "OnStartDraft", new[] { CombatType("NSMedieval.State.HumanoidInstance") }, draftPrefix, statePostfix);
                count += PatchCombat(harmony, "NSMedieval.Controllers.DraftController", "OnEndDraft", new[] { CombatType("NSMedieval.State.HumanoidInstance") }, draftPrefix, statePostfix);
                count += PatchCombat(harmony, "NSMedieval.State.WorkerBehaviour", "SetCombatMode", new[] { CombatType("NSMedieval.State.WorkerJobs.UnitCombatModeType"), typeof(bool) }, modePrefix, statePostfix);
                count += PatchCombat(harmony, "NSMedieval.Manager.DraftManager", "MoveToLocation", new[] { typeof(Vector3) }, movePrefix, null);
            }

            if (replicationConfigCombatAttackCommands)
            {
                count += PatchCombat(harmony, "NSMedieval.Manager.DraftManager", "HandleRightClickAttack", new[] { CombatType("NSMedieval.Goap.IDamageTakingAgent"), typeof(bool) }, attackPrefix, attackPostfix);
                count += PatchCombat(harmony, "NSMedieval.View.WorkerView", "AbortGoalActionHandler", Type.EmptyTypes, cancelPrefix, cancelPostfix);
            }

            if (replicationConfigCombatPresentationReplication || replicationConfigCombatHealthReplication || replicationConfigCombatDeathReplication)
            {
                count += PatchCombat(harmony, "NSMedieval.Controllers.CombatController", "OnAttackStreamStart", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent") }, null, outcomePostfix);
                count += PatchCombat(harmony, "NSMedieval.Controllers.CombatController", "OnAttackStreamEnd", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent") }, null, outcomePostfix);
                count += PatchCombat(harmony, "NSMedieval.Controllers.CombatController", "OnHitMissed", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent"), CombatType("NSMedieval.Goap.IDamageTakingAgent"), CombatType("NSMedieval.Types.CombatMissType") }, null, outcomePostfix);
                count += PatchCombat(harmony, "NSMedieval.Controllers.CombatController", "OnHitBlocked", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent"), CombatType("NSMedieval.Goap.IDamageTakingAgent"), CombatType("NSMedieval.State.CombatHitInfo") }, null, outcomePostfix);
                count += PatchCombat(harmony, "NSMedieval.Controllers.CombatController", "OnDamageTaken", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent"), CombatType("NSMedieval.Goap.IDamageTakingAgent"), CombatType("NSMedieval.State.CombatHitInfo") }, null, outcomePostfix);
                count += PatchCombat(harmony, "NSMedieval.Controllers.CombatController", "OnAgentKilled", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent"), CombatType("NSMedieval.Goap.IDamageTakingAgent") }, null, outcomePostfix);
                count += PatchCombat(harmony, "NSMedieval.Manager.CombatHitManager", "HandleHitEffectors", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent"), CombatType("NSMedieval.Goap.IDamageTakingAgent"), CombatType("NSMedieval.State.CombatHitInfo") }, null, finalHealthPostfix);
            }

            if (replicationConfigCombatPresentationReplication)
            {
                count += PatchCombat(harmony, "NSMedieval.Goap.Goals.AttackBaseGoal", "SpawnChargeProgressBar", Type.EmptyTypes, chargeStartPrefix, chargeStartPostfix);
                count += PatchCombat(harmony, "NSMedieval.Goap.Goals.AttackBaseGoal", "DestroyChargeProgressBar", Type.EmptyTypes, chargeEndPrefix, null);
                count += PatchCombat(harmony, "NSMedieval.Goap.Actions.CombatActions", "FireRangedWeapon", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent") }, weaponReleasePrefix, null);
                count += PatchCombat(harmony, "NSMedieval.Goap.Actions.CombatActions", "AttackMelee", new[] { CombatType("NSMedieval.Goap.IDamageDealAgent") }, weaponReleasePrefix, null);
            }

            if (replicationConfigCombatHealthDetailReplication)
            {
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "StartBleeding", Type.EmptyTypes, null, lifecycleHealthPostfix);
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "StopBleeding", Type.EmptyTypes, null, lifecycleHealthPostfix);
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "Faint", Type.EmptyTypes, null, lifecycleHealthPostfix);
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "UnFaint", Type.EmptyTypes, null, lifecycleHealthPostfix);
                count += PatchCombat(harmony, "NSMedieval.StatsSystem.WoundUtils", "Tick", new[] { CombatType("NSMedieval.StatsSystem.StatsInstance"), CombatType("NSMedieval.StatsSystem.WoundEffectorInfo") }, null, woundTickPostfix);
            }

            if (replicationConfigCombatDeathReplication)
            {
                count += PatchCombat(harmony, "NSMedieval.Manager.AnimalManager", "RemoveAnimal", new[] { CombatType("NSMedieval.State.AnimalInstance"), typeof(bool) }, animalDeathPrefix, animalDeathPostfix);
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "DropStorage", new[] { CombatType("NSMedieval.Vec3Int"), typeof(float) }, storageDropPrefix, null);
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "DropFoodStorage", new[] { CombatType("NSMedieval.Vec3Int") }, storageDropPrefix, null);
                count += PatchCombat(harmony, "NSMedieval.State.CreatureBase", "DropMedicineStorage", new[] { CombatType("NSMedieval.Vec3Int") }, storageDropPrefix, null);
            }

            LogReplicationInfo("Going Cooperative combat replication hooks=" + count.ToString(CultureInfo.InvariantCulture));
        }

        private static HarmonyMethod CombatHarmony(string name)
        {
            return new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static Type CombatType(string typeName)
        {
            return AccessTools.TypeByName(typeName) ?? typeof(object);
        }

        private int PatchCombat(Harmony harmony, string typeName, string methodName, Type[] parameterTypes, HarmonyMethod? prefix, HarmonyMethod? postfix)
        {
            try
            {
                if (Array.IndexOf(parameterTypes, typeof(object)) >= 0)
                {
                    LogReplicationWarning("Going Cooperative combat patch parameter missing " + typeName + "." + methodName);
                    return 0;
                }
                var type = AccessTools.TypeByName(typeName);
                var method = type == null ? null : AccessTools.Method(type, methodName, parameterTypes);
                if (method == null)
                {
                    LogReplicationWarning("Going Cooperative combat patch method missing " + typeName + "." + methodName);
                    return 0;
                }
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                return 1;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative combat patch failed " + typeName + "." + methodName + " " + ex.GetType().Name + ":" + ex.Message);
                return 0;
            }
        }

        private static bool ReplicationCombatDraftStatePrefix(MethodBase __originalMethod, object __0, ref string? __state)
        {
            __state = null;
            if (!CombatMutationEnabled(replicationConfigCombatDraftCommands) || IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryGetReplicationAgentOwnerEntityId(__0, out var entityId, out _)) return true;
            var drafted = string.Equals(__originalMethod.Name, "OnStartDraft", StringComparison.Ordinal);
            var combatMode = TryReadCombatMode(__0);
            __state = LockstepCommandPayloads.CreateDraftStatePayload(entityId, drafted, combatMode);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, drafted ? "combat-draft" : "combat-undraft");
                return false;
            }
            return true;
        }

        private static bool ReplicationCombatModePrefix(object __instance, object __0, ref string? __state)
        {
            __state = null;
            if (!CombatMutationEnabled(replicationConfigCombatDraftCommands) || IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            var humanoid = AccessTools.Property(__instance.GetType(), "Humanoid")?.GetValue(__instance, null);
            if (humanoid == null || !TryGetReplicationAgentOwnerEntityId(humanoid, out var entityId, out _)) return true;
            var drafted = TryReadCombatDrafted(__instance);
            __state = LockstepCommandPayloads.CreateDraftStatePayload(entityId, drafted, __0?.ToString() ?? string.Empty);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "combat-mode");
                return false;
            }
            return true;
        }

        private static void ReplicationCombatStateMutationPostfix(string? __state)
        {
            if (!string.IsNullOrWhiteSpace(__state)) BroadcastHostCombatState(__state!);
        }

        private static bool ReplicationCombatMovePrefix(object __instance, Vector3 __0, ref bool __result)
        {
            if (!CombatMutationEnabled(replicationConfigCombatDraftCommands) || IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryCollectCombatSelectedWorkers(__instance, draftedOnly: true, out var ids, out var mode) || ids.Length == 0) return true;
            if (ShouldSendReplicationLocalCommandIntent())
            {
                TryConvertCombatWorldToGrid(__0, out var gridX, out var gridY, out var gridZ);
                var payload = LockstepCommandPayloads.CreateDraftMovePayload(ids, gridX, gridY, gridZ, mode);
                SendReplicationManagementIntent(payload, "combat-draft-move");
                __result = true;
                return false;
            }
            return true;
        }

        private static bool ReplicationCombatAttackPrefix(object __instance, object __0, bool __1, ref bool __result, ref string? __state)
        {
            __state = null;
            if (!CombatMutationEnabled(replicationConfigCombatAttackCommands) || IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryCollectCombatSelectedWorkers(__instance, __1, out var ids, out _) || ids.Length == 0) return true;
            ResolveCombatTargetIdentity(__0, out var kind, out var targetId, out var x, out var y, out var z);
            __state = LockstepCommandPayloads.CreateCombatAttackPayload(ids, kind, targetId, x, y, z);
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "combat-attack");
                __result = true;
                return false;
            }
            return true;
        }

        private static void ReplicationCombatAttackPostfix(bool __result, string? __state)
        {
            if (__result && !string.IsNullOrWhiteSpace(__state)) BroadcastHostCombatState(__state!);
        }

        private static bool ReplicationCombatCancelPrefix(object __instance, ref string? __state)
        {
            __state = null;
            if (!CombatMutationEnabled(replicationConfigCombatAttackCommands) || IsReplicationRegionOrderStateCaptureSuppressed()) return true;
            if (!TryResolveReplicationAgentOwnerFromView(__instance, out var owner, out _) || owner == null
                || !TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _)
                || !TryDoesCombatPreferredTargetExist(owner))
            {
                return true;
            }
            __state = LockstepCommandPayloads.CreateCombatCancelPayload(new[] { entityId });
            if (ShouldSendReplicationLocalCommandIntent())
            {
                SendReplicationManagementIntent(__state, "combat-cancel");
                return false;
            }
            return true;
        }

        private static void ReplicationCombatCancelPostfix(string? __state)
        {
            if (string.IsNullOrWhiteSpace(__state)
                || !LockstepCommandPayloads.TryReadCombatCancelPayload(__state!, out var entityIds)
                || entityIds.Length == 0
                || !TryFindReplicationAgentOwnerByEntityId(entityIds[0], out var owner, out _)
                || owner == null
                || TryDoesCombatPreferredTargetExist(owner))
            {
                return;
            }
            BroadcastHostCombatState(__state!);
        }

        private static bool TryDoesCombatPreferredTargetExist(object attacker)
        {
            var managerType = AccessTools.TypeByName("NSMedieval.Manager.CombatTargetManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            var get = manager == null ? null : FindCombatCompatibleMethod(manager.GetType(), "GetPreferredTarget", attacker);
            return get != null && get.Invoke(manager, new[] { attacker }) != null;
        }

        private static void ReplicationCombatChargeStartPrefix(object __instance, ref bool __state)
        {
            __state = TryReadInstanceMemberValue(__instance, "chargeBar", out var existingChargeBar)
                && existingChargeBar != null;
        }

        private static void ReplicationCombatChargeStartPostfix(object __instance, bool __state)
        {
            if (__state
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || !CombatMutationEnabled(replicationConfigCombatPresentationReplication)
                || !TryResolveReplicationCombatGoalOwner(__instance, out var owner)
                || owner == null
                || !TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _)
                || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            if (ReplicationCombatHostChargeByEntityId.ContainsKey(entityId))
                CompleteReplicationHostCombatCharge(entityId, LockstepCommandPayloads.CombatPresentationEndReasonCancelled);

            var duration = TryReadReplicationCombatChargeDuration(__instance, owner, out var parsedDuration)
                ? parsedDuration
                : 1f;
            duration = Mathf.Clamp(duration, 0.05f, 60f);
            var chargeNumber = ++replicationCombatChargeSequence;
            CaptureReplicationCombatAnimatorParameters(entityId, out var weaponType, out _);
            // The game applies AttackRnd just after charge creation. Use the
            // authoritative charge ordinal for a stable visual variant instead
            // of accidentally replaying the previous shot's animator value.
            var attackRnd = (int)(chargeNumber % 3L);
            var charge = new ReplicationCombatHostChargeState
            {
                EntityId = entityId,
                ChargeId = entityId + ":charge:" + chargeNumber.ToString(CultureInfo.InvariantCulture),
                DurationSeconds = duration,
                WeaponType = weaponType,
                AttackRnd = attackRnd,
                ExpiresGameTime = Time.time + duration + 3f
            };
            ReplicationCombatHostChargeByEntityId[entityId] = charge;
            SendReplicationHostCombatPresentation(charge, LockstepCommandPayloads.CombatPresentationChargeStartPhase, LockstepCommandPayloads.CombatPresentationEndReasonNone);
        }

        private static void ReplicationCombatChargeEndPrefix(object __instance)
        {
            if (!replicationConfigHostMode
                || !CombatMutationEnabled(replicationConfigCombatPresentationReplication)
                || !TryResolveReplicationCombatGoalOwner(__instance, out var owner)
                || owner == null
                || !TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _))
            {
                return;
            }

            if (ReplicationCombatHostChargeByEntityId.TryGetValue(entityId, out var charge))
            {
                CompleteReplicationHostCombatCharge(
                    entityId,
                    charge.Released
                        ? LockstepCommandPayloads.CombatPresentationEndReasonCompleted
                        : LockstepCommandPayloads.CombatPresentationEndReasonCancelled);
            }
        }

        private static void ReplicationCombatWeaponReleasePrefix(MethodBase __originalMethod, object __0)
        {
            if (!replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || !CombatMutationEnabled(replicationConfigCombatPresentationReplication)
                || !TryGetReplicationAgentOwnerEntityId(__0, out var entityId, out _)
                || !ReplicationCombatHostChargeByEntityId.TryGetValue(entityId, out var charge)
                || charge.Released)
            {
                return;
            }

            charge.Released = true;
            charge.AttackKind = string.Equals(__originalMethod.Name, "FireRangedWeapon", StringComparison.Ordinal)
                ? LockstepCommandPayloads.CombatPresentationAttackKindRanged
                : LockstepCommandPayloads.CombatPresentationAttackKindMelee;
            SendReplicationHostCombatPresentation(charge, LockstepCommandPayloads.CombatPresentationWeaponReleasePhase, LockstepCommandPayloads.CombatPresentationEndReasonNone);
        }

        private static bool TryResolveReplicationCombatGoalOwner(object goal, out object? owner)
        {
            owner = null;
            return (TryInvokeReplicationObjectMethod(goal, "get_AgentOwner", out owner) && owner != null)
                || (TryReadInstanceMemberValue(goal, "AgentOwner", out owner) && owner != null)
                || (TryReadInstanceMemberValue(goal, "agentOwner", out owner) && owner != null);
        }

        private static bool TryReadReplicationCombatChargeDuration(object goal, object owner, out float duration)
        {
            duration = 0f;
            if (TryReadInstanceMemberValue(goal, "chargeBar", out var chargeBar)
                && chargeBar != null
                && TryReadInstanceMemberValue(chargeBar, "Timer", out var timer)
                && timer != null
                && TryReadInstanceMemberValue(timer, "TotalTime", out var totalTime)
                && TryConvertFiniteReplicationCombatFloat(totalTime, out duration)
                && duration > 0f)
            {
                return true;
            }

            var calculatorType = AccessTools.TypeByName("NSMedieval.Manager.CombatCalculator");
            if (calculatorType == null) return false;
            var methods = calculatorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (var i = 0; i < methods.Length; i++)
            {
                var parameters = methods[i].GetParameters();
                if (!string.Equals(methods[i].Name, "CalculateAttackSpeed", StringComparison.Ordinal)
                    || parameters.Length != 1
                    || !parameters[0].ParameterType.IsInstanceOfType(owner))
                {
                    continue;
                }

                try
                {
                    return TryConvertFiniteReplicationCombatFloat(methods[i].Invoke(null, new[] { owner }), out duration)
                        && duration > 0f;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryConvertFiniteReplicationCombatFloat(object? value, out float parsed)
        {
            parsed = 0f;
            if (value == null) return false;
            try
            {
                parsed = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return !float.IsNaN(parsed) && !float.IsInfinity(parsed);
            }
            catch
            {
                return false;
            }
        }

        private static void CaptureReplicationCombatAnimatorParameters(string entityId, out int weaponType, out int attackRnd)
        {
            weaponType = -1;
            attackRnd = -1;
            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _)
                || view == null
                || !TryResolveReplicationAnimator(view, out var animator)
                || animator == null)
            {
                return;
            }
            if (HasReplicationAnimatorParameter(animator, "WeaponType", AnimatorControllerParameterType.Int))
                weaponType = animator.GetInteger("WeaponType");
            if (HasReplicationAnimatorParameter(animator, "AttackRnd", AnimatorControllerParameterType.Int))
                attackRnd = animator.GetInteger("AttackRnd");
        }

        private static void SendReplicationHostCombatPresentation(
            ReplicationCombatHostChargeState charge,
            string phase,
            string endReason)
        {
            var eventTick = ++replicationCombatPresentationSequence;
            var payload = LockstepCommandPayloads.CreateCombatPresentationPayload(
                charge.ChargeId,
                eventTick,
                charge.EntityId,
                phase,
                charge.AttackKind,
                string.Equals(phase, LockstepCommandPayloads.CombatPresentationChargeStartPhase, StringComparison.Ordinal)
                    ? charge.DurationSeconds
                    : 0d,
                string.Equals(phase, LockstepCommandPayloads.CombatPresentationChargeStartPhase, StringComparison.Ordinal)
                    ? "Attack"
                    : string.Empty,
                charge.WeaponType,
                charge.AttackRnd,
                endReason);
            instance?.SendCombatDelta(CombatPresentationDeltaKind, charge.EntityId, payload);
            instance?.LogReplicationInfo("Going Cooperative combat presentation host phase="
                + phase
                + " entityId=" + charge.EntityId
                + " chargeId=" + charge.ChargeId
                + " attackKind=" + charge.AttackKind
                + " duration=" + charge.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                + " endReason=" + endReason);
        }

        private static void CompleteReplicationHostCombatCharge(string entityId, string endReason)
        {
            if (!ReplicationCombatHostChargeByEntityId.TryGetValue(entityId, out var charge)) return;
            ReplicationCombatHostChargeByEntityId.Remove(entityId);
            if (string.Equals(endReason, LockstepCommandPayloads.CombatPresentationEndReasonCompleted, StringComparison.Ordinal))
                ReplicationCombatHostLastCompletedChargeByEntityId[entityId] = charge;
            else
                ReplicationCombatHostLastCompletedChargeByEntityId.Remove(entityId);
            SendReplicationHostCombatPresentation(charge, LockstepCommandPayloads.CombatPresentationChargeEndPhase, endReason);
        }

        private static void EndReplicationHostCombatStream(string entityId)
        {
            if (ReplicationCombatHostChargeByEntityId.ContainsKey(entityId))
            {
                CompleteReplicationHostCombatCharge(entityId, LockstepCommandPayloads.CombatPresentationEndReasonStreamEnded);
                return;
            }
            if (!ReplicationCombatHostLastCompletedChargeByEntityId.TryGetValue(entityId, out var lastCharge)) return;
            ReplicationCombatHostLastCompletedChargeByEntityId.Remove(entityId);
            SendReplicationHostCombatPresentation(
                lastCharge,
                LockstepCommandPayloads.CombatPresentationChargeEndPhase,
                LockstepCommandPayloads.CombatPresentationEndReasonStreamEnded);
        }

        private static void ReplicationCombatOutcomePostfix(MethodBase __originalMethod, object[] __args)
        {
            if (!replicationConfigHostMode || !replicationRuntimeStarted || !replicationRemoteHelloReceived || !replicationConfigCombatReplication) return;
            var name = __originalMethod.Name;
            var attacker = __args.Length > 0 ? __args[0] : null;
            var target = __args.Length > 1 ? __args[1] : TryResolveCombatCurrentTarget(attacker);
            TryGetReplicationAgentOwnerEntityId(attacker, out var attackerId, out _);
            if (string.Equals(name, "OnAttackStreamEnd", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(attackerId))
            {
                EndReplicationHostCombatStream(attackerId!);
            }
            ResolveCombatTargetIdentity(target, out var targetKind, out var targetId, out var x, out var y, out var z);
            var outcomeType = name;
            var amount = 0d;
            var effectId = string.Empty;
            var lethal = target != null && TryReadReplicationBooleanState(target, "HasDied", out var dead) && dead;
            if (__args.Length > 2 && __args[2] != null)
            {
                var info = __args[2];
                if (TryReadInstanceMemberValue(info, "Damage", out var damage) && damage != null) amount = Convert.ToDouble(damage, CultureInfo.InvariantCulture);
                if (string.Equals(name, "OnHitMissed", StringComparison.Ordinal)) effectId = info.ToString() ?? string.Empty;
            }
            var outcomeId = (++replicationCombatOutcomeSequence).ToString(CultureInfo.InvariantCulture);
            var payload = LockstepCommandPayloads.CreateCombatOutcomePayload(outcomeId, replicationCombatOutcomeSequence, attackerId ?? string.Empty, targetKind, targetId, x, y, z, outcomeType, amount, string.Empty, effectId, lethal);
            if (replicationConfigCombatPresentationReplication)
                instance?.SendCombatDelta(CombatOutcomeDeltaKind, targetId, payload);
            if (string.Equals(name, "OnDamageTaken", StringComparison.Ordinal)
                && replicationConfigCombatHealthReplication
                && target != null
                && !(TryReadReplicationBooleanState(target, "HasDied", out var targetDead) && targetDead))
                instance?.SendCombatHealthDelta(target, targetId, "damage");
        }

        private static void ReplicationCombatFinalHealthPostfix(object __1)
        {
            if (!replicationConfigHostMode
                || !CombatMutationEnabled(replicationConfigCombatHealthReplication)
                || IsReplicationRegionOrderStateCaptureSuppressed()
                || (TryReadReplicationBooleanState(__1, "HasDied", out var dead) && dead)
                || !TryGetReplicationAgentOwnerEntityId(__1, out var entityId, out _))
            {
                return;
            }
            instance?.SendCombatHealthDelta(__1, entityId, "post-effectors");
        }

        private static void ReplicationCombatLifecycleHealthPostfix(MethodBase __originalMethod, object __instance)
        {
            if (!replicationConfigHostMode
                || !CombatMutationEnabled(replicationConfigCombatHealthDetailReplication)
                || IsReplicationRegionOrderStateCaptureSuppressed()
                || !TryGetReplicationAgentOwnerEntityId(__instance, out var entityId, out _))
            {
                return;
            }
            instance?.SendCombatHealthDelta(__instance, entityId, __originalMethod.Name);
        }

        private static void ReplicationCombatWoundTickPostfix(object __0)
        {
            if (!replicationConfigHostMode
                || !CombatMutationEnabled(replicationConfigCombatHealthDetailReplication)
                || IsReplicationRegionOrderStateCaptureSuppressed())
            {
                return;
            }
            var owner = AccessTools.Property(__0.GetType(), "Owner")?.GetValue(__0, null);
            if (owner != null && TryGetReplicationAgentOwnerEntityId(owner, out var entityId, out _))
            {
                var now = Time.realtimeSinceStartup;
                if (ReplicationCombatLastWoundTickSentAt.TryGetValue(entityId, out var lastAt) && now - lastAt < 0.5f) return;
                ReplicationCombatLastWoundTickSentAt[entityId] = now;
                instance?.SendCombatHealthDelta(owner, entityId, "wound-tick");
            }
        }

        private sealed class ReplicationCombatDeathCapture
        {
            public string EntityId = string.Empty;
            public long SpawnTime;
        }

        private static void ReplicationCombatAnimalDeathPrefix(object __0, bool __1, ref ReplicationCombatDeathCapture? __state)
        {
            __state = null;
            if (!__1 || !replicationConfigHostMode || !CombatMutationEnabled(replicationConfigCombatDeathReplication) || IsReplicationRegionOrderStateCaptureSuppressed()) return;
            if (!TryGetReplicationAgentOwnerEntityId(__0, out var entityId, out _)) return;
            TryReadCombatSpawnTime(__0, out var spawnTime);
            __state = new ReplicationCombatDeathCapture { EntityId = entityId, SpawnTime = spawnTime };
            if (replicationConfigCombatHealthReplication)
                instance?.SendCombatHealthDelta(__0, entityId, "death");
        }

        private static void ReplicationCombatAnimalDeathPostfix(ReplicationCombatDeathCapture? __state)
        {
            if (__state == null || string.IsNullOrWhiteSpace(__state.EntityId)) return;
            instance?.SendCombatDelta(
                CombatDeathDeltaKind,
                __state.EntityId,
                "entityId=" + __state.EntityId
                    + " spawnTime=" + __state.SpawnTime.ToString(CultureInfo.InvariantCulture)
                    + " kind=animal phase=dead");
        }

        private static bool ReplicationCombatStorageDropPrefix(object __instance)
        {
            if (replicationConfigHostMode || !CombatMutationEnabled(replicationConfigCombatDeathReplication)) return true;
            if (!TryGetReplicationAgentOwnerEntityId(__instance, out var entityId, out _)) return true;
            TryReadCombatSpawnTime(__instance, out var spawnTime);
            return !ReplicationCombatClientTombstones.Contains(FormatCombatGenerationKey(entityId, spawnTime));
        }

        private static bool CombatMutationEnabled(bool childGate)
        {
            return replicationConfigCombatReplication && childGate;
        }

        private static string TryReadCombatMode(object humanoidOrBehaviour)
        {
            object? behaviour = humanoidOrBehaviour;
            if (!humanoidOrBehaviour.GetType().Name.Contains("Behaviour") && TryReadInstanceMemberValue(humanoidOrBehaviour, "WorkerBehaviour", out var resolved) && resolved != null)
                behaviour = resolved;
            return behaviour != null && TryReadInstanceMemberValue(behaviour, "CombatMode", out var mode) && mode != null ? mode.ToString() ?? string.Empty : string.Empty;
        }

        private static bool TryReadCombatDrafted(object behaviour)
        {
            return TryReadInstanceMemberValue(behaviour, "IsDrafting", out var drafted) && drafted != null && Convert.ToBoolean(drafted, CultureInfo.InvariantCulture);
        }

        private static bool TryCollectCombatSelectedWorkers(object draftManager, bool draftedOnly, out string[] entityIds, out string combatMode)
        {
            var ids = new List<string>();
            combatMode = string.Empty;
            var method = AccessTools.Method(draftManager.GetType(), "GetCurrenlySelected", new[] { typeof(bool) });
            var selected = method?.Invoke(draftManager, new object[] { draftedOnly }) as IEnumerable;
            if (selected != null)
            {
                foreach (var worker in selected)
                {
                    if (worker != null && TryGetReplicationAgentOwnerEntityId(worker, out var id, out _))
                    {
                        ids.Add(id);
                        if (combatMode.Length == 0) combatMode = TryReadCombatMode(worker);
                    }
                }
            }
            entityIds = ids.ToArray();
            return entityIds.Length > 0;
        }

        private static void ResolveCombatTargetIdentity(object? target, out string kind, out string targetId, out int x, out int y, out int z)
        {
            kind = "grid";
            targetId = string.Empty;
            x = y = z = 0;
            if (target == null) return;
            var creatureType = AccessTools.TypeByName("NSMedieval.State.CreatureBase");
            var buildingType = AccessTools.TypeByName("NSMedieval.BuildingComponents.BaseBuildingInstance");
            if (creatureType != null && creatureType.IsInstanceOfType(target)
                && TryGetReplicationAgentOwnerEntityId(target, out targetId, out _)) kind = "agent";
            else if (buildingType != null && buildingType.IsInstanceOfType(target)
                && TryGetReplicationStableEntityId(target, out targetId)) kind = "building";
            else if (TryGetReplicationStableEntityId(target, out targetId)) kind = "object";
            if (TryResolveReplicationContextualObjectGrid(target, out x, out y, out z, out _)) return;
            if (TryInvokeReplicationObjectMethod(target, "GetPosition", out var position) && position is Vector3 vector)
            {
                TryConvertCombatWorldToGrid(vector, out x, out y, out z);
            }
        }

        private static bool TryConvertCombatWorldToGrid(Vector3 worldPosition, out int x, out int y, out int z)
        {
            x = Mathf.RoundToInt(worldPosition.x);
            y = Mathf.RoundToInt(worldPosition.y);
            z = Mathf.RoundToInt(worldPosition.z);
            try
            {
                var extensionType = AccessTools.TypeByName("NSEipix.VectorExtension");
                var method = extensionType == null
                    ? null
                    : AccessTools.Method(extensionType, "ToGridVec3Int", new[] { typeof(Vector3), typeof(float) });
                var grid = method?.Invoke(null, new object[] { worldPosition, 0f });
                return grid != null && TryReadVec3IntLikeValue(grid, out x, out y, out z);
            }
            catch
            {
                return false;
            }
        }

        private static object? TryResolveCombatCurrentTarget(object? attacker)
        {
            return attacker != null && TryInvokeReplicationObjectMethod(attacker, "GetTarget", out var target) ? target : null;
        }

        private static void BroadcastHostCombatState(string payload)
        {
            if (!replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || !CombatMutationEnabled(replicationConfigCombatStateReplication)) return;
            instance?.SendCombatDelta(CombatStateDeltaKind, string.Empty, payload);
        }

        private void SendCombatDelta(string deltaKind, string entityId, string detail)
        {
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var id) ? id : 0L;
            SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(++replicationWorldObjectDeltaSequence, Time.realtimeSinceStartup, deltaKind, uniqueId, string.Empty, 0, 0, 0, detail));
        }

        private void SendReplicationCombatStateIfSupported(LockstepCommand command, RuntimeCommandResult result)
        {
            if (!result.Invoked
                || !replicationConfigHostMode
                || !CombatMutationEnabled(replicationConfigCombatStateReplication)
                || command.Kind != CommandKind.Custom)
            {
                return;
            }

            var payload = command.PayloadJson ?? string.Empty;
            if (LockstepCommandPayloads.TryReadDraftStatePayload(payload, out _, out _, out _)
                || LockstepCommandPayloads.TryReadCombatAttackPayload(payload, out _, out _, out _, out _, out _, out _)
                || LockstepCommandPayloads.TryReadCombatCancelPayload(payload, out _))
            {
                SendCombatDelta(CombatStateDeltaKind, string.Empty, payload);
            }
        }

        private void SendCombatHealthDelta(object target, string targetId, string source)
        {
            if (!CombatMutationEnabled(replicationConfigCombatHealthReplication) || string.IsNullOrWhiteSpace(targetId)) return;
            if (!TryReadCombatStat(target, "Health", out var health))
            {
                LogReplicationWarning("Going Cooperative combat health snapshot skipped missing Health entityId=" + targetId);
                return;
            }
            var wounded = TryReadReplicationBooleanState(target, "IsWounded", out var woundedValue) && woundedValue;
            var bleeding = TryReadReplicationBooleanState(target, "IsBleeding", out var bleedingValue) && bleedingValue;
            var fainted = TryReadReplicationBooleanState(target, "HasFainted", out var faintedValue) && faintedValue;
            var dead = TryReadReplicationBooleanState(target, "HasDied", out var deadValue) && deadValue;
            var detail = new StringBuilder(192)
                .Append("entityId=").Append(targetId)
                .Append(" health=").Append(health.ToString("R", CultureInfo.InvariantCulture));
            if (TryReadCombatStat(target, "Blood", out var blood))
                detail.Append(" blood=").Append(blood.ToString("R", CultureInfo.InvariantCulture));
            if (TryReadCombatStat(target, "Consciousness", out var consciousness))
                detail.Append(" consciousness=").Append(consciousness.ToString("R", CultureInfo.InvariantCulture));
            if (TryReadCombatStat(target, "Pain", out var pain))
                detail.Append(" pain=").Append(pain.ToString("R", CultureInfo.InvariantCulture));
            detail.Append(" wounded=").Append(FormatReplicationBool(wounded))
                .Append(" bleeding=").Append(FormatReplicationBool(bleeding))
                .Append(" fainted=").Append(FormatReplicationBool(fainted))
                .Append(" dead=").Append(FormatReplicationBool(dead))
                .Append(" source=").Append(source);
            SendCombatDelta(CombatHealthDeltaKind, targetId, detail.ToString());
        }

        private static bool TryApplyReplicationCombatDraftState(
            string entityId,
            bool drafted,
            string combatMode,
            out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatDraftCommands))
            {
                detail = "combat-draft-disabled";
                return false;
            }

            if (!TryFindReplicationAgentOwnerByEntityId(entityId, out var humanoid, out var lookupDetail) || humanoid == null)
            {
                detail = "combat-draft-worker-missing entityId=" + entityId + " " + lookupDetail;
                return false;
            }

            var controllerType = AccessTools.TypeByName("NSMedieval.Controllers.DraftController");
            var controller = controllerType == null ? null : ResolveReplicationUnityManagerInstance(controllerType);
            if (controller == null)
            {
                detail = "combat-draft-controller-missing entityId=" + entityId;
                return false;
            }

            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            try
            {
                TryResolveReplicationBehaviourOwner(humanoid, out var behaviour);
                var currentDrafted = behaviour != null && TryReadCombatDrafted(behaviour);
                var stateApplied = currentDrafted == drafted;
                if (!stateApplied)
                {
                    var methodName = drafted ? "OnStartDraft" : "OnEndDraft";
                    var method = FindCombatCompatibleMethod(controller.GetType(), methodName, humanoid);
                    if (method == null)
                    {
                        detail = "combat-draft-method-missing method=" + methodName + " entityId=" + entityId;
                        return false;
                    }
                    method.Invoke(controller, new[] { humanoid });
                    stateApplied = true;
                    TryResolveReplicationBehaviourOwner(humanoid, out behaviour);
                }

                var modeApplied = TryApplyReplicationCombatMode(behaviour, combatMode, out var modeDetail);
                detail = "ok combat-draft entityId=" + entityId
                    + " drafted=" + FormatReplicationBool(drafted)
                    + " stateApplied=" + FormatReplicationBool(stateApplied)
                    + " " + modeDetail + " " + lookupDetail;
                return stateApplied && modeApplied;
            }
            catch (Exception ex)
            {
                detail = "combat-draft-error entityId=" + entityId + " " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }
        }

        private static bool TryApplyReplicationCombatMode(object? behaviour, string combatMode, out string detail)
        {
            if (behaviour == null || string.IsNullOrWhiteSpace(combatMode))
            {
                detail = "combatMode=unchanged";
                return true;
            }

            var modeType = AccessTools.TypeByName("NSMedieval.State.WorkerJobs.UnitCombatModeType");
            if (modeType == null || !modeType.IsEnum)
            {
                detail = "combatMode=enum-missing";
                return false;
            }

            object parsed;
            try { parsed = Enum.Parse(modeType, combatMode, true); }
            catch
            {
                detail = "combatMode=invalid:" + combatMode;
                return false;
            }

            if (TryReadInstanceMemberValue(behaviour, "CombatMode", out var current)
                && current != null
                && string.Equals(current.ToString(), parsed.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                detail = "combatMode=already:" + parsed;
                return true;
            }

            var setMode = AccessTools.Method(behaviour.GetType(), "SetCombatMode", new[] { modeType, typeof(bool) });
            if (setMode == null)
            {
                detail = "combatMode=method-missing";
                return false;
            }
            setMode.Invoke(behaviour, new[] { parsed, (object)false });
            detail = "combatMode=set:" + parsed;
            return true;
        }

        private static bool TryApplyReplicationCombatDraftMove(
            string[] entityIds,
            int targetX,
            int targetY,
            int targetZ,
            string combatMode,
            out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatDraftCommands))
            {
                detail = "combat-draft-move-disabled";
                return false;
            }

            var orderType = AccessTools.TypeByName("NSMedieval.Draft.DraftOrderGoToLocation");
            var modeType = AccessTools.TypeByName("NSMedieval.State.WorkerJobs.UnitCombatModeType");
            var controllerType = AccessTools.TypeByName("NSMedieval.Controllers.DraftController");
            var controller = controllerType == null ? null : ResolveReplicationUnityManagerInstance(controllerType);
            if (orderType == null || modeType == null || !modeType.IsEnum || controller == null)
            {
                detail = "combat-draft-move-surface-missing";
                return false;
            }

            object parsedMode;
            try { parsedMode = Enum.Parse(modeType, string.IsNullOrWhiteSpace(combatMode) ? "Neutral" : combatMode, true); }
            catch { parsedMode = Enum.Parse(modeType, "Neutral", true); }
            var constructor = orderType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Vector3), modeType }, null);
            if (constructor == null)
            {
                detail = "combat-draft-move-constructor-missing";
                return false;
            }

            var worldPosition = GetReplicationWorldPosition(targetX, targetY, targetZ, out var positionDetail);
            var applied = 0;
            var missing = 0;
            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            try
            {
                for (var i = 0; i < entityIds.Length; i++)
                {
                    if (!TryFindReplicationAgentOwnerByEntityId(entityIds[i], out var humanoid, out _) || humanoid == null)
                    {
                        missing++;
                        continue;
                    }
                    TryResolveReplicationBehaviourOwner(humanoid, out var behaviour);
                    if (behaviour == null || !TryReadCombatDrafted(behaviour))
                    {
                        missing++;
                        continue;
                    }
                    var order = constructor.Invoke(new[] { (object)worldPosition, parsedMode });
                    var execute = FindCombatCompatibleMethod(controller.GetType(), "ExecuteDraftOrder", humanoid, order);
                    if (execute == null)
                    {
                        missing++;
                        continue;
                    }
                    execute.Invoke(controller, new[] { humanoid, order });
                    applied++;
                }
            }
            catch (Exception ex)
            {
                detail = "combat-draft-move-error " + FormatReflectionExceptionDetail(ex)
                    + " applied=" + applied.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }

            detail = "ok combat-draft-move applied=" + applied.ToString(CultureInfo.InvariantCulture)
                + " missing=" + missing.ToString(CultureInfo.InvariantCulture)
                + " target=Vec3Int(" + targetX.ToString(CultureInfo.InvariantCulture) + ","
                + targetY.ToString(CultureInfo.InvariantCulture) + "," + targetZ.ToString(CultureInfo.InvariantCulture) + ") "
                + positionDetail;
            return applied > 0;
        }

        private static bool TryApplyReplicationCombatAttack(
            string[] attackerEntityIds,
            string targetKind,
            string targetId,
            int targetX,
            int targetY,
            int targetZ,
            bool authoritativeExecution,
            out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatAttackCommands))
            {
                detail = "combat-attack-disabled";
                return false;
            }
            if (!TryResolveReplicationCombatTarget(targetKind, targetId, targetX, targetY, targetZ, out var target, out var targetDetail) || target == null)
            {
                detail = "combat-attack-target-missing " + targetDetail;
                return false;
            }

            if (!authoritativeExecution)
            {
                return TryApplyReplicationCombatTargetState(attackerEntityIds, target, out detail);
            }

            var orderType = AccessTools.TypeByName("NSMedieval.Draft.DraftOrderForceAttackTarget");
            var targetInterface = AccessTools.TypeByName("NSMedieval.Goap.IDamageTakingAgent");
            var controllerType = AccessTools.TypeByName("NSMedieval.Controllers.DraftController");
            var controller = controllerType == null ? null : ResolveReplicationUnityManagerInstance(controllerType);
            var constructor = orderType == null || targetInterface == null
                ? null
                : orderType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { targetInterface }, null);
            if (orderType == null || targetInterface == null || controller == null || constructor == null || !targetInterface.IsInstanceOfType(target))
            {
                detail = "combat-attack-order-surface-missing targetType=" + FormatShortTypeName(target.GetType()) + " " + targetDetail;
                return false;
            }

            var applied = 0;
            var rejected = 0;
            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            try
            {
                for (var i = 0; i < attackerEntityIds.Length; i++)
                {
                    if (!TryFindReplicationAgentOwnerByEntityId(attackerEntityIds[i], out var humanoid, out _) || humanoid == null
                        || !IsReplicationCombatAttackerEligible(humanoid))
                    {
                        rejected++;
                        continue;
                    }
                    var order = constructor.Invoke(new object[] { target });
                    var execute = FindCombatCompatibleMethod(controller.GetType(), "ExecuteDraftOrder", humanoid, order);
                    if (execute == null)
                    {
                        rejected++;
                        continue;
                    }
                    execute.Invoke(controller, new[] { humanoid, order });
                    applied++;
                }
            }
            catch (Exception ex)
            {
                detail = "combat-attack-error " + FormatReflectionExceptionDetail(ex)
                    + " applied=" + applied.ToString(CultureInfo.InvariantCulture) + " " + targetDetail;
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }

            detail = "ok combat-attack applied=" + applied.ToString(CultureInfo.InvariantCulture)
                + " rejected=" + rejected.ToString(CultureInfo.InvariantCulture) + " " + targetDetail;
            return applied > 0;
        }

        private static bool TryApplyReplicationCombatCancel(string[] attackerEntityIds, bool authoritativeExecution, out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatAttackCommands))
            {
                detail = "combat-cancel-disabled";
                return false;
            }

            var managerType = AccessTools.TypeByName("NSMedieval.Manager.CombatTargetManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            if (manager == null)
            {
                detail = "combat-target-manager-missing";
                return false;
            }

            var applied = 0;
            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            try
            {
                for (var i = 0; i < attackerEntityIds.Length; i++)
                {
                    if (!TryFindReplicationAgentOwnerByEntityId(attackerEntityIds[i], out var attacker, out _) || attacker == null) continue;
                    var remove = FindCombatCompatibleMethod(manager.GetType(), "RemovePreferredTarget", attacker);
                    if (remove != null) remove.Invoke(manager, new[] { attacker });
                    TryInvokeCombatOneArgumentMethod(attacker, "SetTarget", null);
                    if (TryResolveReplicationBehaviourOwner(attacker, out var behaviour) && behaviour != null)
                    {
                        TryInvokeCombatOneArgumentMethod(behaviour, "SetTarget", null);
                        if (authoritativeExecution)
                        {
                            if (TryReadReplicationObjectState(attacker, "CombatAi", out var combatAi) && combatAi != null)
                                TryInvokeReplicationObjectMethod(combatAi, "Abort", out _);
                            if (TryReadReplicationObjectState(behaviour, "LastDraftOrder", out var lastOrder)
                                && lastOrder != null
                                && lastOrder.GetType().Name.IndexOf("ForceAttackTarget", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var onDraftEnd = FindCombatCompatibleMethod(lastOrder.GetType(), "OnDraftEnd", attacker);
                                onDraftEnd?.Invoke(lastOrder, new[] { attacker });
                                TrySetInstanceMemberValue(behaviour, "LastDraftOrder", null);
                            }
                            if (TryReadReplicationObjectState(behaviour, "WorkerGoapAgent", out var workerGoap) && workerGoap != null)
                            {
                                TryInvokeCombatOneArgumentMethod(workerGoap, "ForceNextGoalExclusive", null);
                                TryInvokeReplicationObjectMethod(workerGoap, "Abort", out _);
                                TryInvokeCombatOneArgumentMethod(workerGoap, "DelayNextTick", 1.5f);
                            }
                        }
                    }
                    applied++;
                }
            }
            catch (Exception ex)
            {
                detail = "combat-cancel-error " + FormatReflectionExceptionDetail(ex)
                    + " applied=" + applied.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }

            detail = "ok combat-cancel applied=" + applied.ToString(CultureInfo.InvariantCulture)
                + " authoritative=" + FormatReplicationBool(authoritativeExecution);
            return applied > 0;
        }

        private static bool TryApplyReplicationCombatTargetState(string[] attackerEntityIds, object target, out string detail)
        {
            var managerType = AccessTools.TypeByName("NSMedieval.Manager.CombatTargetManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            if (manager == null)
            {
                detail = "combat-target-manager-missing";
                return false;
            }

            var applied = 0;
            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            try
            {
                for (var i = 0; i < attackerEntityIds.Length; i++)
                {
                    if (!TryFindReplicationAgentOwnerByEntityId(attackerEntityIds[i], out var attacker, out _) || attacker == null) continue;
                    var set = FindCombatCompatibleMethod(manager.GetType(), "SetPreferredTarget", attacker, target);
                    if (set == null) continue;
                    set.Invoke(manager, new[] { attacker, target });
                    applied++;
                }
            }
            catch (Exception ex)
            {
                detail = "combat-target-state-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }
            detail = "ok combat-target-state applied=" + applied.ToString(CultureInfo.InvariantCulture);
            return applied > 0;
        }

        private static bool TryResolveReplicationCombatTarget(
            string targetKind,
            string targetId,
            int targetX,
            int targetY,
            int targetZ,
            out object? target,
            out string detail)
        {
            target = null;
            if (string.Equals(targetKind, "agent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetKind, "creature", StringComparison.OrdinalIgnoreCase))
            {
                if (TryFindReplicationAgentOwnerByEntityId(targetId, out target, out var lookupDetail) && target != null)
                {
                    detail = "target=agent entityId=" + targetId + " " + lookupDetail;
                    return true;
                }
                detail = "target-agent-not-found entityId=" + targetId + " " + lookupDetail;
                return false;
            }

            if (string.Equals(targetKind, "object", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetKind, "building", StringComparison.OrdinalIgnoreCase))
            {
                if (TryFindReplicationBuildingBlueprintCandidate(string.Empty, targetX, targetY, targetZ, out var view, out var lookupDetail)
                    && view != null
                    && TryResolveReplicationBuildingCandidateInstance(view, out target, out var instanceDetail)
                    && target != null)
                {
                    detail = "target=building " + lookupDetail + " " + instanceDetail;
                    return true;
                }
                detail = "target-building-not-found " + lookupDetail;
                return false;
            }

            if (string.Equals(targetKind, "grid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(targetKind, "voxel", StringComparison.OrdinalIgnoreCase))
            {
                var pointType = AccessTools.TypeByName("AttackablePointTarget");
                var getPooled = pointType == null ? null : AccessTools.Method(pointType, "GetNewPooled", Type.EmptyTypes);
                target = getPooled?.Invoke(null, Array.Empty<object>());
                if (target == null)
                {
                    detail = "target-voxel-pool-missing";
                    return false;
                }
                var world = GetReplicationWorldPosition(targetX, targetY, targetZ, out var worldDetail);
                var init = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (var i = 0; i < init.Length; i++)
                {
                    var parameters = init[i].GetParameters();
                    if (!string.Equals(init[i].Name, "Init", StringComparison.Ordinal) || parameters.Length != 2 || parameters[1].ParameterType != typeof(Vector3)) continue;
                    object? map = null;
                    if (TryGetReplicationActiveVillage(out var village, out _) && village != null)
                        TryReadReplicationObjectState(village, "Map", out map);
                    if (map == null || !parameters[0].ParameterType.IsInstanceOfType(map)) continue;
                    init[i].Invoke(target, new[] { map, (object)world });
                    detail = "target=voxel " + worldDetail;
                    return true;
                }
                detail = "target-voxel-init-missing " + worldDetail;
                return false;
            }

            detail = "target-kind-unsupported kind=" + targetKind;
            return false;
        }

        private static bool IsReplicationCombatAttackerEligible(object attacker)
        {
            if (TryReadReplicationBooleanState(attacker, "HasDied", out var dead) && dead) return false;
            if (TryReadReplicationBooleanState(attacker, "HasFainted", out var fainted) && fainted) return false;
            if (TryReadReplicationBooleanState(attacker, "HasDisposed", out var disposed) && disposed) return false;
            return TryResolveReplicationBehaviourOwner(attacker, out var behaviour)
                && behaviour != null
                && TryReadCombatDrafted(behaviour);
        }

        private static MethodInfo? FindCombatCompatibleMethod(Type type, string name, params object[] arguments)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var methods = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
                    var parameters = methods[i].GetParameters();
                    if (parameters.Length != arguments.Length) continue;
                    var compatible = true;
                    for (var p = 0; p < parameters.Length; p++)
                    {
                        if (arguments[p] != null && !parameters[p].ParameterType.IsInstanceOfType(arguments[p]))
                        {
                            compatible = false;
                            break;
                        }
                    }
                    if (compatible) return methods[i];
                }
            }
            return null;
        }

        private static bool TryInvokeCombatOneArgumentMethod(object owner, string name, object? argument)
        {
            var methods = owner.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (var i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal) || methods[i].GetParameters().Length != 1) continue;
                if (argument != null && !methods[i].GetParameters()[0].ParameterType.IsInstanceOfType(argument)) continue;
                methods[i].Invoke(owner, new[] { argument });
                return true;
            }
            return false;
        }

        private static bool TryApplyReplicationCombatWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigCombatReplication)
            {
                detail = "combat-replication-disabled";
                return false;
            }

            if (string.Equals(delta.DeltaKind, CombatStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationCombatStateDelta(delta, out detail);
            if (string.Equals(delta.DeltaKind, CombatOutcomeDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationCombatOutcomeDelta(delta, out detail);
            if (string.Equals(delta.DeltaKind, CombatPresentationDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationCombatPresentationDelta(delta, out detail);
            if (string.Equals(delta.DeltaKind, CombatHealthDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationCombatHealthDelta(delta, out detail);
            if (string.Equals(delta.DeltaKind, CombatDeathDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationCombatDeathDelta(delta, out detail);

            detail = "combat-delta-kind-unsupported kind=" + delta.DeltaKind;
            return false;
        }

        private static bool TryApplyReplicationCombatStateDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatStateReplication))
            {
                detail = "combat-state-disabled";
                return false;
            }
            if (LockstepCommandPayloads.TryReadDraftStatePayload(delta.Detail, out var entityId, out var drafted, out var combatMode))
                return TryApplyReplicationCombatDraftState(entityId, drafted, combatMode, out detail);
            if (LockstepCommandPayloads.TryReadCombatAttackPayload(delta.Detail, out var attackerIds, out var targetKind, out var targetId, out var x, out var y, out var z))
                return TryApplyReplicationCombatAttack(attackerIds, targetKind, targetId, x, y, z, authoritativeExecution: false, out detail);
            if (LockstepCommandPayloads.TryReadCombatCancelPayload(delta.Detail, out var cancellingIds))
                return TryApplyReplicationCombatCancel(cancellingIds, authoritativeExecution: false, out detail);
            detail = "combat-state-payload-unsupported";
            return false;
        }

        private static bool TryApplyReplicationCombatPresentationDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigCombatPresentationReplication)
            {
                detail = "combat-presentation-disabled";
                return true;
            }
            if (!LockstepCommandPayloads.TryReadCombatPresentationPayload(
                delta.Detail,
                out var chargeId,
                out var eventTick,
                out var attackerId,
                out var phase,
                out var attackKind,
                out var parsedDuration,
                out var animationToken,
                out var weaponType,
                out var attackRnd,
                out var endReason))
            {
                detail = "combat-presentation-payload-invalid";
                return false;
            }

            if (ReplicationCombatLatestPresentationTickByEntityId.TryGetValue(attackerId, out var latestTick))
            {
                if (eventTick < latestTick)
                {
                    detail = "ok combat-presentation-stale phase=" + phase
                        + " entityId=" + attackerId
                        + " chargeId=" + chargeId
                        + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                        + " latestTick=" + latestTick.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
                if (eventTick == latestTick)
                {
                    detail = "ok combat-presentation-duplicate phase=" + phase
                        + " entityId=" + attackerId
                        + " chargeId=" + chargeId
                        + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            if (string.Equals(phase, LockstepCommandPayloads.CombatPresentationChargeStartPhase, StringComparison.Ordinal))
            {
                return TryApplyReplicationCombatChargeStart(
                    attackerId,
                    chargeId,
                    eventTick,
                    attackKind,
                    (float)parsedDuration,
                    animationToken,
                    weaponType,
                    attackRnd,
                    out detail);
            }
            if (string.Equals(phase, LockstepCommandPayloads.CombatPresentationWeaponReleasePhase, StringComparison.Ordinal))
            {
                return TryApplyReplicationCombatWeaponRelease(attackerId, chargeId, eventTick, attackKind, out detail);
            }

            return TryApplyReplicationCombatChargeEnd(attackerId, chargeId, eventTick, endReason, out detail);
        }

        private static bool TryApplyReplicationCombatChargeStart(
            string entityId,
            string chargeId,
            long eventTick,
            string attackKind,
            float durationSeconds,
            string animationToken,
            int weaponType,
            int attackRnd,
            out string detail)
        {
            durationSeconds = Mathf.Clamp(durationSeconds, 0.05f, 60f);
            if (ReplicationCombatClientChargeByEntityId.TryGetValue(entityId, out var previousCharge))
            {
                CleanupReplicationCombatClientCharge(previousCharge, clearAnimator: true);
                ReplicationCombatClientChargeByEntityId.Remove(entityId);
            }

            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "combat-presentation-start-view-missing entityId=" + entityId + " " + viewDetail;
                return false;
            }
            if (!TryResolveReplicationAgentOwnerFromView(view, out var owner, out var ownerDetail) || owner == null)
            {
                detail = "combat-presentation-start-owner-missing entityId=" + entityId + " " + ownerDetail + " " + viewDetail;
                return false;
            }
            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                detail = "combat-presentation-start-animator-missing entityId=" + entityId + " " + viewDetail;
                return false;
            }
            var hasChargeBar = TryCreateReplicationCombatChargeBar(owner, durationSeconds, out var progressBar, out var timer, out var barDetail);
            if (!hasChargeBar) barDetail = "bar=unavailable " + barDetail;

            var state = new ReplicationCombatClientChargeState
            {
                ChargeId = chargeId,
                AnimationToken = animationToken,
                LatestEventTick = eventTick,
                DurationSeconds = durationSeconds,
                StartedGameTime = Time.time,
                ExpiresGameTime = Time.time + durationSeconds + 3f,
                View = view,
                ProgressBar = progressBar,
                Timer = timer,
                Animator = animator,
                HasAttackMotionTime = HasReplicationAnimatorParameter(animator, "AttackMotionTime", AnimatorControllerParameterType.Float)
            };

            applyingRuntimeCommandDepth++;
            try
            {
                TrySetReplicationAnimatorFloat(animator, "AttackSpeed", 1f / durationSeconds);
                TrySetReplicationAnimatorFloat(animator, "AttackMotionTime", 0f);
                if (attackRnd >= 0) TrySetReplicationAnimatorInteger(animator, "AttackRnd", attackRnd);
                if (weaponType >= 0) TrySetReplicationAnimatorInteger(animator, "WeaponType", weaponType);
                TrySetReplicationAnimatorBool(animator, "IsAttacking", true);
                if (HasReplicationAnimatorParameter(animator, animationToken, AnimatorControllerParameterType.Trigger))
                    animator.ResetTrigger(animationToken);
                var animationDetail = InvokeReplicationAgentViewAnimationTrigger(view, animationToken, string.Empty);

                ReplicationCombatClientChargeByEntityId[entityId] = state;
                ReplicationCombatLatestPresentationTickByEntityId[entityId] = eventTick;
                ReplicationCombatPresentationExpiryByEntityId[entityId] = state.ExpiresGameTime;
                detail = "ok combat-presentation-start entityId=" + entityId
                    + " chargeId=" + chargeId
                    + " attackKind=" + attackKind
                    + " duration=" + durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                    + " weaponType=" + weaponType.ToString(CultureInfo.InvariantCulture)
                    + " attackRnd=" + attackRnd.ToString(CultureInfo.InvariantCulture)
                    + " " + barDetail
                    + " " + animationDetail
                    + " " + viewDetail;
                instance?.LogReplicationInfo("Going Cooperative combat presentation client " + detail);
                return true;
            }
            catch (Exception ex)
            {
                CleanupReplicationCombatClientCharge(state, clearAnimator: true);
                detail = "combat-presentation-start-error " + FormatReflectionExceptionDetail(ex)
                    + " entityId=" + entityId + " " + viewDetail;
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool TryApplyReplicationCombatWeaponRelease(
            string entityId,
            string chargeId,
            long eventTick,
            string attackKind,
            out string detail)
        {
            if (!ReplicationCombatClientChargeByEntityId.TryGetValue(entityId, out var state)
                || !string.Equals(state.ChargeId, chargeId, StringComparison.Ordinal))
            {
                detail = "combat-presentation-release-waiting-for-start entityId=" + entityId
                    + " chargeId=" + chargeId
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            state.LatestEventTick = eventTick;
            state.ExpiresGameTime = Math.Max(state.ExpiresGameTime, Time.time + 2f);
            ReplicationCombatLatestPresentationTickByEntityId[entityId] = eventTick;
            ReplicationCombatPresentationExpiryByEntityId[entityId] = state.ExpiresGameTime;
            detail = "ok combat-presentation-release entityId=" + entityId
                + " chargeId=" + chargeId
                + " attackKind=" + attackKind
                + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture);
            instance?.LogReplicationInfo("Going Cooperative combat presentation client " + detail);
            return true;
        }

        private static bool TryApplyReplicationCombatChargeEnd(
            string entityId,
            string chargeId,
            long eventTick,
            string endReason,
            out string detail)
        {
            var completed = string.Equals(endReason, LockstepCommandPayloads.CombatPresentationEndReasonCompleted, StringComparison.Ordinal);
            ReplicationCombatClientChargeByEntityId.TryGetValue(entityId, out var state);
            ReplicationCombatClientLastCompletedChargeIdByEntityId.TryGetValue(entityId, out var completedChargeId);
            var endDisposition = CombatPresentationOrderingPolicy.ResolveEnd(
                state?.ChargeId,
                state?.LatestEventTick ?? long.MinValue,
                completedChargeId,
                chargeId,
                eventTick);
            var matchedActive = endDisposition == CombatPresentationEndDisposition.ApplyActive;
            var matchedCompleted = endDisposition == CombatPresentationEndDisposition.ApplyCompleted;
            var supersededActive = endDisposition == CombatPresentationEndDisposition.SupersedeActive;
            if (endDisposition == CombatPresentationEndDisposition.WaitForStart)
            {
                // Reliable events may be reordered in transit. Do not retire the
                // lifecycle or advance its high-water mark until Start is present;
                // a negative acknowledgement makes the host retry this end event.
                detail = "combat-presentation-end-waiting-for-start entityId=" + entityId
                    + " chargeId=" + chargeId
                    + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                    + " endReason=" + endReason;
                return false;
            }
            if (matchedActive && state != null)
            {
                CleanupReplicationCombatClientCharge(state, clearAnimator: !completed);
                ReplicationCombatClientChargeByEntityId.Remove(entityId);
            }
            else if (supersededActive && state != null)
            {
                CleanupReplicationCombatClientCharge(state, clearAnimator: true);
                ReplicationCombatClientChargeByEntityId.Remove(entityId);
            }

            if (completed && matchedActive)
            {
                ReplicationCombatClientLastCompletedChargeIdByEntityId[entityId] = chargeId;
            }
            else if (!completed && (matchedActive || matchedCompleted || supersededActive))
            {
                if (!matchedActive && !supersededActive) ClearReplicationCombatStreamAnimator(entityId);
                ReplicationCombatClientLastCompletedChargeIdByEntityId.Remove(entityId);
            }

            ReplicationCombatLatestPresentationTickByEntityId[entityId] = eventTick;
            if (completed && matchedActive)
                ReplicationCombatPresentationExpiryByEntityId[entityId] = Time.time + 5f;
            else if (!completed || supersededActive)
                ReplicationCombatPresentationExpiryByEntityId.Remove(entityId);
            detail = "ok combat-presentation-end entityId=" + entityId
                + " chargeId=" + chargeId
                + " eventTick=" + eventTick.ToString(CultureInfo.InvariantCulture)
                + " endReason=" + endReason;
            instance?.LogReplicationInfo("Going Cooperative combat presentation client " + detail);
            return true;
        }

        private static bool TryCreateReplicationCombatChargeBar(
            object owner,
            float durationSeconds,
            out object? progressBar,
            out object? timer,
            out string detail)
        {
            progressBar = null;
            timer = null;
            var overlayType = AccessTools.TypeByName("NSMedieval.FloatingOverlaySystem.OverlayProgressBarType");
            var timerType = AccessTools.TypeByName("NSMedieval.State.Timers.Timer");
            if (overlayType == null || timerType == null)
            {
                detail = "combat-charge-runtime-type-missing overlay=" + (overlayType == null ? "missing" : "ok")
                    + " timer=" + (timerType == null ? "missing" : "ok");
                return false;
            }

            object overlayValue;
            try
            {
                overlayValue = Enum.Parse(overlayType, "CombatCircle", ignoreCase: false);
            }
            catch (Exception ex)
            {
                detail = "combat-charge-overlay-parse-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }

            var getProgressBar = FindReplicationInstanceMethod(owner.GetType(), "GetProgressBar", new[] { overlayType });
            var destroyProgressBar = FindReplicationInstanceMethod(owner.GetType(), "DestroyProgressBar", new[] { overlayType });
            if (getProgressBar == null)
            {
                detail = "combat-charge-get-bar-missing ownerType=" + FormatShortTypeName(owner.GetType());
                return false;
            }

            try
            {
                destroyProgressBar?.Invoke(owner, new[] { overlayValue });
                progressBar = getProgressBar.Invoke(owner, new[] { overlayValue });
                if (progressBar == null)
                {
                    detail = "combat-charge-bar-null ownerType=" + FormatShortTypeName(owner.GetType());
                    return false;
                }

                timer = Activator.CreateInstance(timerType, new object[] { durationSeconds });
                if (timer == null)
                {
                    detail = "combat-charge-timer-null";
                    return false;
                }
                var setup = FindReplicationInstanceMethod(progressBar.GetType(), "Setup", new[] { timerType, typeof(bool) });
                if (setup == null)
                {
                    detail = "combat-charge-setup-missing barType=" + FormatShortTypeName(progressBar.GetType());
                    DisposeReplicationCombatObject(timer);
                    timer = null;
                    DisposeReplicationCombatObject(progressBar);
                    progressBar = null;
                    return false;
                }

                setup.Invoke(progressBar, new[] { timer, (object)true });
                var iconDetail = TryApplyReplicationCombatChargeBarIcon(owner, progressBar);
                detail = "bar=" + FormatShortTypeName(progressBar.GetType())
                    + " timer=" + FormatShortTypeName(timer.GetType())
                    + " " + iconDetail;
                return true;
            }
            catch (Exception ex)
            {
                if (progressBar != null) DisposeReplicationCombatObject(progressBar);
                else if (timer != null) DisposeReplicationCombatObject(timer);
                progressBar = null;
                timer = null;
                detail = "combat-charge-bar-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static string TryApplyReplicationCombatChargeBarIcon(object owner, object progressBar)
        {
            var iconPath = ResolveReplicationCombatChargeIconPath(owner);
            var assetUtilsType = AccessTools.TypeByName("NSMedieval.UI.Utils.AssetUtils");
            var getSprite = assetUtilsType == null
                ? null
                : AccessTools.Method(assetUtilsType, "GetSprite", new[] { typeof(string) });
            if (getSprite == null) return "icon-method-missing path=" + iconPath;
            try
            {
                var sprite = getSprite.Invoke(null, new object[] { iconPath });
                if (sprite == null) return "icon-missing path=" + iconPath;
                if (!TryReadInstanceMemberValue(progressBar, "FillImage", out var fillImage) || fillImage == null)
                    return "icon-fill-image-missing path=" + iconPath;
                var spriteProperty = GetCachedInstanceProperty(fillImage.GetType(), "sprite");
                if (spriteProperty == null || !spriteProperty.CanWrite)
                    return "icon-sprite-property-missing path=" + iconPath;
                spriteProperty.SetValue(fillImage, sprite, null);
                return "icon=" + iconPath;
            }
            catch (Exception ex)
            {
                return "icon-error=" + FormatReflectionExceptionDetail(ex) + " path=" + iconPath;
            }
        }

        private static string ResolveReplicationCombatChargeIconPath(object owner)
        {
            if (owner.GetType().Name.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0
                && TryReadInstanceMemberValue(owner, "Blueprint", out var animalBlueprint)
                && animalBlueprint != null
                && TryReadInstanceMemberValue(animalBlueprint, "AttackIcon", out var attackIcon)
                && attackIcon != null
                && !string.IsNullOrWhiteSpace(Convert.ToString(attackIcon, CultureInfo.InvariantCulture)))
            {
                return Convert.ToString(attackIcon, CultureInfo.InvariantCulture) ?? "fist_attack";
            }

            var useFists = TryReadReplicationBooleanState(owner, "UseFistsOnly", out var parsedUseFists) && parsedUseFists;
            if (!useFists
                && ((TryReadInstanceMemberValue(owner, "Inventory", out var inventory) && inventory != null)
                    || (TryInvokeReplicationObjectMethod(owner, "get_Inventory", out inventory) && inventory != null)))
            {
                var itemType = AccessTools.TypeByName("NSMedieval.Types.ItemType");
                var getItem = itemType == null ? null : FindReplicationInstanceMethod(inventory.GetType(), "GetItem", new[] { itemType });
                if (itemType != null && getItem != null)
                {
                    try
                    {
                        var weaponType = Enum.Parse(itemType, "Weapon", ignoreCase: false);
                        var equipment = getItem.Invoke(inventory, new[] { weaponType });
                        if (equipment != null
                            && TryReadInstanceMemberValue(equipment, "Blueprint", out var equipmentBlueprint)
                            && equipmentBlueprint != null
                            && TryReadInstanceMemberValue(equipmentBlueprint, "Resource", out var resource)
                            && resource != null
                            && TryReadInstanceMemberValue(resource, "IconPath", out var iconPath)
                            && iconPath != null
                            && !string.IsNullOrWhiteSpace(Convert.ToString(iconPath, CultureInfo.InvariantCulture)))
                        {
                            return Convert.ToString(iconPath, CultureInfo.InvariantCulture) ?? "fist_attack";
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return "fist_attack";
        }

        private static void DisposeReplicationCombatObject(object value)
        {
            try
            {
                FindReplicationInstanceMethod(value.GetType(), "Dispose", Type.EmptyTypes)?.Invoke(value, null);
            }
            catch
            {
            }
        }

        private static void CleanupReplicationCombatClientCharge(ReplicationCombatClientChargeState state, bool clearAnimator)
        {
            if (state.ProgressBar != null)
                DisposeReplicationCombatObject(state.ProgressBar);
            else if (state.Timer != null)
                DisposeReplicationCombatObject(state.Timer);
            state.ProgressBar = null;
            state.Timer = null;

            try
            {
                if (state.Animator != null)
                {
                    TrySetReplicationAnimatorFloat(state.Animator, "AttackMotionTime", 0f);
                    if (HasReplicationAnimatorParameter(state.Animator, state.AnimationToken, AnimatorControllerParameterType.Trigger))
                        state.Animator.ResetTrigger(state.AnimationToken);
                }
                if (state.View != null)
                {
                    SetInstancePropertyIfPresent(state.View, state.View.GetType(), "TriggeredAnimationRunning", false);
                    if (clearAnimator)
                        ClearReplicationCombatAnimatorParameters(state.View, state.Animator);
                }
            }
            catch
            {
            }
        }

        private static void ClearReplicationCombatStreamAnimator(string entityId)
        {
            if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out _) || view == null) return;
            try
            {
                ClearReplicationCombatAnimatorParameters(view, null);
            }
            catch
            {
            }
        }

        private static bool ClearReplicationCombatAnimatorParameters(object view, Animator? knownAnimator)
        {
            var animator = knownAnimator;
            if (animator == null && (!TryResolveReplicationAnimator(view, out animator) || animator == null)) return false;
            var applied = TrySetReplicationAnimatorFloat(animator, "AttackMotionTime", 0f);
            applied = TrySetReplicationAnimatorBool(animator, "IsAttacking", false) || applied;
            if (HasReplicationAnimatorParameter(animator, "Attack", AnimatorControllerParameterType.Trigger))
                animator.ResetTrigger("Attack");
            SetInstancePropertyIfPresent(view, view.GetType(), "TriggeredAnimationRunning", false);
            return applied;
        }

        private static bool TryApplyReplicationCombatOutcomeDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigCombatPresentationReplication)
            {
                detail = "combat-presentation-disabled";
                return true;
            }
            if (!LockstepCommandPayloads.TryReadCombatOutcomePayload(
                delta.Detail,
                out var outcomeId,
                out _,
                out var attackerId,
                out _,
                out var targetId,
                out _,
                out _,
                out _,
                out var outcomeType,
                out var amount,
                out _,
                out _,
                out var lethal))
            {
                detail = "combat-outcome-payload-invalid";
                return false;
            }

            if (replicationConfigCombatPresentationReplication
                && (string.Equals(outcomeType, "OnAttackStreamStart", StringComparison.Ordinal)
                    || string.Equals(outcomeType, "OnAttackStreamEnd", StringComparison.Ordinal)))
            {
                // Reliable CombatPresentation events own stream lifecycle. The
                // older transient outcome remains diagnostic-only so a delayed
                // packet cannot clear or revive a newer charge.
                detail = "ok combat-outcome semantic-presentation-owns-stream outcomeId=" + outcomeId
                    + " type=" + outcomeType
                    + " attackerId=" + attackerId;
                return true;
            }

            if ((string.Equals(outcomeType, "OnAttackStreamStart", StringComparison.Ordinal)
                    || string.Equals(outcomeType, "OnAttackStreamEnd", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(attackerId))
            {
                if (string.Equals(outcomeType, "OnAttackStreamStart", StringComparison.Ordinal))
                    ReplicationCombatPresentationExpiryByEntityId[attackerId] = Time.time + 5f;
                else
                    ReplicationCombatPresentationExpiryByEntityId.Remove(attackerId);
            }

            if ((string.Equals(outcomeType, "OnAttackStreamStart", StringComparison.Ordinal)
                    || string.Equals(outcomeType, "OnAttackStreamEnd", StringComparison.Ordinal))
                && !string.IsNullOrWhiteSpace(attackerId)
                && TryFindReplicationAnimatedAgentViewByEntityId(attackerId, out var view, out var viewDetail)
                && view != null
                && TryResolveReplicationAnimator(view, out var animator)
                && animator != null)
            {
                var attacking = string.Equals(outcomeType, "OnAttackStreamStart", StringComparison.Ordinal);
                var set = attacking
                    ? TrySetReplicationAnimatorBool(animator, "IsAttacking", true)
                    : ClearReplicationCombatAnimatorParameters(view, animator);
                detail = "ok combat-outcome presentation=" + (set ? "animator" : "observed")
                    + " outcomeId=" + outcomeId + " type=" + outcomeType + " " + viewDetail;
                return true;
            }

            detail = "ok combat-outcome observed outcomeId=" + outcomeId
                + " type=" + outcomeType
                + " targetId=" + targetId
                + " amount=" + amount.ToString("R", CultureInfo.InvariantCulture)
                + " lethal=" + FormatReplicationBool(lethal);
            return true;
        }

        private static void ProcessReplicationCombatPresentationExpiry()
        {
            if (!replicationConfigCombatPresentationReplication) return;
            var now = Time.time;

            if (replicationConfigHostMode)
            {
                if (ReplicationCombatHostChargeByEntityId.Count == 0) return;
                var expiredHostCharges = ReplicationCombatExpiredHostChargeEntityIds;
                expiredHostCharges.Clear();
                foreach (var pair in ReplicationCombatHostChargeByEntityId)
                {
                    if (now >= pair.Value.ExpiresGameTime) expiredHostCharges.Add(pair.Key);
                }
                for (var i = 0; i < expiredHostCharges.Count; i++)
                    CompleteReplicationHostCombatCharge(expiredHostCharges[i], LockstepCommandPayloads.CombatPresentationEndReasonWatchdog);
                expiredHostCharges.Clear();
                return;
            }

            if (ReplicationCombatClientChargeByEntityId.Count > 0)
            {
                var expiredCharges = ReplicationCombatExpiredClientChargeEntityIds;
                expiredCharges.Clear();
                foreach (var pair in ReplicationCombatClientChargeByEntityId)
                {
                    var state = pair.Value;
                    if (state.Animator != null && state.HasAttackMotionTime)
                    {
                        var normalizedMotion = Mathf.Clamp01(((now - state.StartedGameTime) / state.DurationSeconds) - 0.05f);
                        state.Animator.SetFloat("AttackMotionTime", normalizedMotion);
                    }
                    if (now >= state.ExpiresGameTime) expiredCharges.Add(pair.Key);
                }
                for (var i = 0; i < expiredCharges.Count; i++)
                {
                    var entityId = expiredCharges[i];
                    if (ReplicationCombatClientChargeByEntityId.TryGetValue(entityId, out var state))
                        CleanupReplicationCombatClientCharge(state, clearAnimator: true);
                    ReplicationCombatClientChargeByEntityId.Remove(entityId);
                    ReplicationCombatPresentationExpiryByEntityId.Remove(entityId);
                    ReplicationCombatClientLastCompletedChargeIdByEntityId.Remove(entityId);
                    instance?.LogReplicationWarning("Going Cooperative combat presentation client watchdog cleanup entityId=" + entityId);
                }
                expiredCharges.Clear();
            }

            if (ReplicationCombatPresentationExpiryByEntityId.Count == 0) return;
            var expired = ReplicationCombatExpiredPresentationEntityIds;
            expired.Clear();
            foreach (var pair in ReplicationCombatPresentationExpiryByEntityId)
            {
                if (now >= pair.Value && !ReplicationCombatClientChargeByEntityId.ContainsKey(pair.Key)) expired.Add(pair.Key);
            }
            for (var i = 0; i < expired.Count; i++)
            {
                var entityId = expired[i];
                ReplicationCombatPresentationExpiryByEntityId.Remove(entityId);
                ReplicationCombatClientLastCompletedChargeIdByEntityId.Remove(entityId);
                ClearReplicationCombatStreamAnimator(entityId);
            }
            expired.Clear();
        }

        private static void ResetReplicationCombatRuntimeState()
        {
            foreach (var state in ReplicationCombatClientChargeByEntityId.Values)
                CleanupReplicationCombatClientCharge(state, clearAnimator: true);
            replicationCombatOutcomeSequence = 0;
            replicationCombatPresentationSequence = 0;
            replicationCombatChargeSequence = 0;
            ReplicationCombatClientTombstones.Clear();
            ReplicationCombatLastWoundTickSentAt.Clear();
            ReplicationCombatPresentationExpiryByEntityId.Clear();
            ReplicationCombatHostChargeByEntityId.Clear();
            ReplicationCombatHostLastCompletedChargeByEntityId.Clear();
            ReplicationCombatClientChargeByEntityId.Clear();
            ReplicationCombatClientLastCompletedChargeIdByEntityId.Clear();
            ReplicationCombatLatestPresentationTickByEntityId.Clear();
            ReplicationCombatExpiredHostChargeEntityIds.Clear();
            ReplicationCombatExpiredClientChargeEntityIds.Clear();
            ReplicationCombatExpiredPresentationEntityIds.Clear();
        }

        private static bool TryApplyReplicationCombatHealthDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatHealthReplication))
            {
                detail = "combat-health-disabled";
                return false;
            }
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || string.IsNullOrWhiteSpace(entityId)
                || !TryReadReplicationWorldObjectDetailFloat(delta.Detail, "health", out var health)
                || float.IsNaN(health)
                || float.IsInfinity(health))
            {
                detail = "combat-health-payload-invalid detail=" + delta.Detail;
                return false;
            }
            if (!TryFindReplicationAgentOwnerByEntityId(entityId, out var owner, out var lookupDetail) || owner == null)
            {
                detail = "combat-health-owner-missing entityId=" + entityId + " " + lookupDetail;
                return false;
            }

            applyingRuntimeCommandDepth++;
            BeginReplicationRegionOrderStateCaptureSuppression();
            try
            {
                var applied = TryForceReplicationCombatStat(owner, "Health", health) ? 1 : 0;
                if (replicationConfigCombatHealthDetailReplication)
                {
                    if (TryReadFiniteCombatDetailFloat(delta.Detail, "blood", out var blood) && TryForceReplicationCombatStat(owner, "Blood", blood)) applied++;
                    if (TryReadFiniteCombatDetailFloat(delta.Detail, "consciousness", out var consciousness) && TryForceReplicationCombatStat(owner, "Consciousness", consciousness)) applied++;
                    if (TryReadFiniteCombatDetailFloat(delta.Detail, "pain", out var pain) && TryForceReplicationCombatStat(owner, "Pain", pain)) applied++;
                    ApplyReplicationCombatLifecycleFlag(owner, delta.Detail, "bleeding", "IsBleeding", "StartBleeding", "StopBleeding");
                    ApplyReplicationCombatLifecycleFlag(owner, delta.Detail, "fainted", "HasFainted", "Faint", "UnFaint");
                }
                detail = "ok combat-health entityId=" + entityId + " statsApplied=" + applied.ToString(CultureInfo.InvariantCulture)
                    + " health=" + health.ToString("R", CultureInfo.InvariantCulture) + " " + lookupDetail;
                return applied > 0;
            }
            catch (Exception ex)
            {
                detail = "combat-health-error entityId=" + entityId + " " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                EndReplicationRegionOrderStateCaptureSuppression();
                applyingRuntimeCommandDepth--;
            }
        }

        private static bool TryApplyReplicationCombatDeathDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!CombatMutationEnabled(replicationConfigCombatDeathReplication))
            {
                detail = "combat-death-disabled";
                return false;
            }
            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId) || string.IsNullOrWhiteSpace(entityId))
            {
                detail = "combat-death-entity-id-missing";
                return false;
            }
            var spawnTime = TryReadReplicationWorldObjectDetailToken(delta.Detail, "spawnTime", out var spawnText)
                && long.TryParse(spawnText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSpawnTime)
                ? parsedSpawnTime
                : 0L;
            var generationKey = FormatCombatGenerationKey(entityId, spawnTime);
            ReplicationCombatClientTombstones.Add(generationKey);

            if (!TryFindReplicationAgentOwnerByEntityId(entityId, out var animal, out var lookupDetail) || animal == null)
            {
                detail = "ok combat-death already-absent entityId=" + entityId + " generation=" + spawnTime.ToString(CultureInfo.InvariantCulture) + " " + lookupDetail;
                return true;
            }
            if (spawnTime != 0L && TryReadCombatSpawnTime(animal, out var localSpawnTime) && localSpawnTime != spawnTime)
            {
                detail = "combat-death-generation-mismatch entityId=" + entityId
                    + " expected=" + spawnTime.ToString(CultureInfo.InvariantCulture)
                    + " actual=" + localSpawnTime.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            var managerType = AccessTools.TypeByName("NSMedieval.Manager.AnimalManager");
            var manager = managerType == null ? null : ResolveReplicationUnityManagerInstance(managerType);
            var remove = manager == null ? null : FindCombatCompatibleMethod(manager.GetType(), "RemoveAnimal", animal, false);
            if (manager == null || remove == null)
            {
                detail = "combat-death-remove-surface-missing entityId=" + entityId + " " + lookupDetail;
                return false;
            }

            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            try
            {
                remove.Invoke(manager, new[] { animal, (object)false });
                detail = "ok combat-death removed-animal-no-carcass entityId=" + entityId
                    + " generation=" + spawnTime.ToString(CultureInfo.InvariantCulture) + " " + lookupDetail;
                return true;
            }
            catch (Exception ex)
            {
                detail = "combat-death-remove-error entityId=" + entityId + " " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }
        }

        private static bool TryForceReplicationCombatStat(object owner, string statName, float value)
        {
            var behaviour = TryResolveReplicationBehaviourOwner(owner, out var resolved) ? resolved : null;
            var stats = TryResolveReplicationStatsOwner(owner, behaviour);
            if (stats == null || !TryResolveCombatStat(stats, statName, out var stat) || stat == null) return false;
            if (!ReplicationCombatForceStatByStatType.TryGetValue(stat.GetType(), out var force))
            {
                force = AccessTools.Method(stat.GetType(), "ForceCurrentValue", new[] { typeof(float) });
                if (force == null) return false;
                ReplicationCombatForceStatByStatType[stat.GetType()] = force;
            }
            force.Invoke(stat, new object[] { value });
            return true;
        }

        private static void ApplyReplicationCombatLifecycleFlag(
            object owner,
            string payload,
            string token,
            string stateMember,
            string enableMethod,
            string disableMethod)
        {
            if (!TryReadReplicationWorldObjectDetailBool(payload, token, out var desired)) return;
            if (TryReadReplicationBooleanState(owner, stateMember, out var current) && current == desired) return;
            TryInvokeReplicationObjectMethod(owner, desired ? enableMethod : disableMethod, out _);
        }

        private static bool TryReadCombatSpawnTime(object owner, out long spawnTime)
        {
            spawnTime = 0L;
            if (!TryReadInstanceMemberValue(owner, "SpawnTime", out var raw) || raw == null) return false;
            try
            {
                spawnTime = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static string FormatCombatGenerationKey(string entityId, long spawnTime)
        {
            return entityId + "|spawn=" + spawnTime.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryReadFiniteCombatDetailFloat(string detail, string name, out float value)
        {
            return TryReadReplicationWorldObjectDetailFloat(detail, name, out value)
                && !float.IsNaN(value)
                && !float.IsInfinity(value);
        }

        private static bool TryReadCombatStat(object owner, string statName, out float current)
        {
            current = float.NaN;
            var behaviour = TryResolveReplicationBehaviourOwner(owner, out var resolved) ? resolved : null;
            var stats = TryResolveReplicationStatsOwner(owner, behaviour);
            if (stats == null || !TryResolveCombatStat(stats, statName, out var stat) || stat == null) return false;
            return TryReadInstanceMemberValue(stat, "Current", out var raw)
                && raw != null
                && (current = Convert.ToSingle(raw, CultureInfo.InvariantCulture)) == current
                && !float.IsInfinity(current);
        }

        private static bool TryResolveCombatStat(object stats, string statName, out object? stat)
        {
            stat = null;
            var statType = AccessTools.TypeByName("NSMedieval.StatsSystem.StatType");
            if (statType == null || !statType.IsEnum) return false;
            if (!ReplicationCombatStatTypeValueByName.TryGetValue(statName, out var enumValue))
            {
                try { enumValue = Enum.Parse(statType, statName, true); }
                catch { return false; }
                ReplicationCombatStatTypeValueByName[statName] = enumValue;
            }
            if (!ReplicationCombatGetStatByStatsType.TryGetValue(stats.GetType(), out var getStat))
            {
                getStat = FindReplicationInstanceMethod(stats.GetType(), "GetStat", new[] { statType });
                if (getStat == null) return false;
                ReplicationCombatGetStatByStatsType[stats.GetType()] = getStat;
            }
            stat = getStat.Invoke(stats, new[] { enumValue });
            return stat != null;
        }
    }
}
