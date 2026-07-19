using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Dictionary<string, long> ReplicationNeedsEventSequenceByEntityId =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> ReplicationNeedsLastAppliedDeltaByEntityAndKind =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> ReplicationNeedsLastSleepVisualByEntityId =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private sealed class ReplicationSleepPresentationV2State
        {
            public bool Initialized;
            public bool DesiredSleeping;
            public bool VisualAppliedForDesiredState;
            public long LastAuthoritativeSequence;
            public bool PoseCandidateInitialized;
            public Vector3 PoseCandidatePosition;
            public Quaternion PoseCandidateRotation;
            public float PoseCandidateSinceRealtime;
            public bool PoseLocked;
            public Vector3 LockedPosePosition;
            public Quaternion LockedPoseRotation;
            public Transform? OverlayHook;
            public int OverlayHookInstanceId;
            public bool OverlayHookPositionLocked;
            public Vector3 LockedOverlayHookPosition;
        }
        private static readonly Dictionary<string, ReplicationSleepPresentationV2State> ReplicationNeedsSleepPresentationV2ByEntityId =
            new Dictionary<string, ReplicationSleepPresentationV2State>(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> ReplicationNeedsSleepEntityIdByOverlayHookInstanceId =
            new Dictionary<int, string>();
        // Sleep presentation is client-native in this build.  These keys are a
        // session boundary only: they prevent the several native SleepGoal end
        // callbacks from being replayed as separate wake transitions.
        private static readonly Dictionary<string, bool> ReplicationNeedsHostSleepSessionByEntityId =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ReplicationPendingNeedsRepair> ReplicationPendingNeedsRepairs =
            new Dictionary<string, ReplicationPendingNeedsRepair>(StringComparer.Ordinal);
        private static Dictionary<string, object>? replicationNeedsViewCache;
        private static float replicationNeedsViewCacheBuiltRealtime;
        private static Type? replicationNeedsStatType;
        private static object? replicationNeedsHungerStatTypeValue;
        private static object? replicationNeedsSleepStatTypeValue;
        private static readonly Dictionary<Type, MethodInfo> ReplicationNeedsGetStatMethodByType =
            new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> ReplicationNeedsSetStatMethodByType =
            new Dictionary<Type, MethodInfo>();
        private static long replicationNeedsEventsSent;
        private static long replicationNeedsEventsApplied;
        private static long replicationNeedsRepairValuesApplied;
        private static long replicationNeedsSleepV2TransitionsApplied;
        private static long replicationNeedsSleepV2MatchedStates;
        private static long replicationNeedsSleepV2StatRepairsSuppressed;
        private static long replicationNeedsSleepV2VisualTransitionsApplied;
        private static long replicationNeedsSleepV2RecoveryVisualsApplied;
        private static long replicationNeedsSleepV2PoseLocks;
        private static long replicationNeedsSleepV2PoseLockBreaks;
        private static long replicationNeedsSleepV2PoseHeldFrames;
        private static long replicationNeedsSleepV2OverlayHookLocks;
        private static long replicationNeedsSleepV2OverlayHookHeldFrames;
        private static long replicationNeedsSleepV2ContradictoryNativeWritesSuppressed;

        private const float ReplicationNeedsSleepV2PoseSettleSeconds = 0.3f;
        private const float ReplicationNeedsSleepV2PoseSettleDistance = 0.015f;
        private const float ReplicationNeedsSleepV2PoseSettleAngleDegrees = 0.75f;
        private const float ReplicationNeedsSleepV2PoseBreakDistance = 0.75f;

        private void TryInstallReplicationNeedsHooks(Harmony harmonyInstance)
        {
            if (!replicationConfigNeedsReplication)
            {
                LogReplicationInfo("Going Cooperative needs replication disabled; lifecycle hooks not installed.");
                return;
            }

            var goalPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationNeedsGoalLifecyclePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var viewPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationNeedsViewLifecyclePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var lifePostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationNeedsLifeLifecyclePostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var starvingPostfix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationNeedsStarvingPostfix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var overlayHolderPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationNeedsFloatingElementHolderRefreshPositionPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var isSleepingPrefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationNeedsIsSleepingPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));

            var patched = 0;
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, goalPostfix, "NSMedieval.Goap.Goals.HungerGoal",
                "ConsumeFoodAction", "OnConsumedResource", "OnAteResourceInstance", "OnAteResourcePileAtPlace", "EndGoalWith");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, goalPostfix, "NSMedieval.Goap.Goals.AnimalHungerGoal",
                "OnAtePlantMapResource", "OnAteResourcePile", "EndGoalWith");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, goalPostfix, "NSMedieval.Goap.Goals.SleepGoal",
                "GetNextAction", "EndGoalWith");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, goalPostfix, "NSMedieval.Goap.Goals.RestGoal",
                "GetNextAction", "EndGoalWith");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, viewPostfix, "NSMedieval.View.WorkerView",
                "EatParticles", "StopEatParticles");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, viewPostfix, "NSMedieval.View.Animals.AnimalView",
                "EatParticles", "StopEatParticles");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, lifePostfix, "NSMedieval.Controllers.LifeController",
                "FallAsleep", "WakeUp");
            patched += TryPatchReplicationNeedsMethods(harmonyInstance, starvingPostfix, "NSMedieval.Controllers.LifeController",
                "Starving");
            patched += TryPatchReplicationNeedsMethodsPrefix(harmonyInstance, overlayHolderPrefix,
                "NSMedieval.FloatingOverlaySystem.FloatingElementHolder", "RefreshPosition");
            patched += TryPatchReplicationNeedsMethodsPrefix(harmonyInstance, isSleepingPrefix,
                "NSMedieval.State.CreatureBase", "set_IsSleeping");
            LogReplicationInfo("Going Cooperative needs lifecycle hooks patched="
                + patched.ToString(CultureInfo.InvariantCulture));
        }

        private int TryPatchReplicationNeedsMethods(Harmony harmonyInstance, HarmonyMethod postfix, string typeName, params string[] methodNames)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                LogReplicationWarning("Going Cooperative needs hook type missing type=" + typeName);
                return 0;
            }

            var wanted = new HashSet<string>(methodNames, StringComparer.Ordinal);
            var patched = 0;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!wanted.Contains(method.Name) || method.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    harmonyInstance.Patch(method, postfix: postfix);
                    patched++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning("Going Cooperative needs hook failed method="
                        + typeName + "." + method.Name + " error=" + FormatReflectionExceptionDetail(ex));
                }
            }

            return patched;
        }

        private static void ReplicationNeedsGoalLifecyclePostfix(object __instance, MethodBase __originalMethod)
        {
            if (!ShouldCaptureReplicationNeeds() || __instance == null)
            {
                return;
            }

            if (!TryGetReplicationNeedsEntityIdFromGoal(__instance, out var entityId))
            {
                return;
            }

            var typeName = __originalMethod.DeclaringType?.Name ?? string.Empty;
            var kind = typeName.IndexOf("Hunger", StringComparison.OrdinalIgnoreCase) >= 0
                ? "hunger"
                : typeName.IndexOf("Sleep", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "sleep"
                    : "rest";
            var method = __originalMethod.Name;
            var phase = string.Equals(method, "EndGoalWith", StringComparison.Ordinal)
                ? "ended"
                : method.IndexOf("Consumed", StringComparison.OrdinalIgnoreCase) >= 0
                    || method.IndexOf("OnAte", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "consumed"
                        : string.Equals(method, "ConsumeFoodAction", StringComparison.Ordinal)
                            ? "consuming"
                            : "started";
            // LifeController.FallAsleep/WakeUp are the authoritative semantic
            // boundaries.  Goal methods are advisory and fire repeatedly while
            // the client is already rendering the native sleep state.
            if (string.Equals(kind, "sleep", StringComparison.Ordinal)
                || string.Equals(kind, "rest", StringComparison.Ordinal))
            {
                return;
            }

            SendReplicationNeedsLifecycle(entityId, kind, phase, "source=" + typeName + "." + method);
        }

        private static void ReplicationNeedsViewLifecyclePostfix(object __instance, MethodBase __originalMethod)
        {
            if (!ShouldCaptureReplicationNeeds()
                || __instance == null
                || !TryGetReplicationViewEntityId(__instance, out var entityId))
            {
                return;
            }

            var phase = string.Equals(__originalMethod.Name, "EatParticles", StringComparison.Ordinal)
                ? "visual-started"
                : "visual-ended";
            SendReplicationNeedsLifecycle(entityId, "hunger", phase,
                "source=" + (__originalMethod.DeclaringType?.Name ?? "View") + "." + __originalMethod.Name);
        }

        private static void ReplicationNeedsLifeLifecyclePostfix(object __0, MethodBase __originalMethod)
        {
            if (!ShouldCaptureReplicationNeeds()
                || __0 == null
                || !TryGetReplicationNeedsEntityIdFromStats(__0, out var entityId))
            {
                return;
            }

            var sleeping = string.Equals(__originalMethod.Name, "FallAsleep", StringComparison.Ordinal);
            if (ReplicationNeedsHostSleepSessionByEntityId.TryGetValue(entityId, out var wasSleeping)
                && wasSleeping == sleeping)
            {
                return;
            }

            ReplicationNeedsHostSleepSessionByEntityId[entityId] = sleeping;
            SendReplicationNeedsLifecycle(entityId, "sleep", sleeping ? "sleeping" : "woke",
                "source=LifeController." + __originalMethod.Name);
        }

        private static void ReplicationNeedsStarvingPostfix(object __0, bool __1)
        {
            if (!ShouldCaptureReplicationNeeds()
                || __0 == null
                || !TryGetReplicationNeedsEntityIdFromStats(__0, out var entityId))
            {
                return;
            }

            SendReplicationNeedsLifecycle(entityId, "hunger", __1 ? "starving" : "starvation-ended",
                "source=LifeController.Starving");
        }

        private static bool ShouldCaptureReplicationNeeds()
        {
            return replicationConfigNeedsReplication
                && replicationConfigEnabled
                && replicationConfigHostMode
                && !IsReplicationWorldObjectDeltaModeOff()
                && replicationRuntimeStarted;
        }

        private static void SendReplicationNeedsLifecycle(string entityId, string kind, string phase, string source)
        {
            var current = instance;
            if (ReferenceEquals(current, null) || string.IsNullOrWhiteSpace(entityId))
            {
                return;
            }

            ReplicationNeedsEventSequenceByEntityId.TryGetValue(entityId, out var eventSequence);
            eventSequence++;
            ReplicationNeedsEventSequenceByEntityId[entityId] = eventSequence;
            var uniqueId = TryParseReplicationEntityNumericId(entityId, out var parsedId) ? parsedId : 0L;
            var hasHunger = false;
            var hungerCurrent = 0f;
            var hasSleep = false;
            var sleepCurrent = 0f;
            TryReadReplicationNeedsValuesForEntity(
                entityId,
                out hasHunger,
                out hungerCurrent,
                out hasSleep,
                out sleepCurrent);
            current.SendReplicationWorldObjectDelta(new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                Time.realtimeSinceStartup,
                "AgentNeedLifecycle",
                uniqueId,
                kind,
                0,
                0,
                0,
                "entityId=" + entityId
                    + " needKind=" + kind
                    + " phase=" + phase
                    + " eventSequence=" + eventSequence.ToString(CultureInfo.InvariantCulture)
                    + " hasHunger=" + FormatReplicationBool(hasHunger)
                    + " hungerCurrent=" + hungerCurrent.ToString("R", CultureInfo.InvariantCulture)
                    + " hasSleep=" + FormatReplicationBool(hasSleep)
                    + " sleepCurrent=" + sleepCurrent.ToString("R", CultureInfo.InvariantCulture)
                    + " " + source));
            replicationNeedsEventsSent++;
        }

        private static bool TryReadReplicationNeedsValuesForEntity(
            string entityId,
            out bool hasHunger,
            out float hungerCurrent,
            out bool hasSleep,
            out float sleepCurrent)
        {
            hasHunger = false;
            hungerCurrent = 0f;
            hasSleep = false;
            sleepCurrent = 0f;
            if (!TryFindReplicationNeedsView(entityId, out var view, out _)
                || view == null
                || !TryResolveReplicationAgentOwnerFromView(view, out var owner, out _)
                || owner == null)
            {
                return false;
            }

            object? behaviourOwner = null;
            TryResolveReplicationBehaviourOwner(owner, out behaviourOwner);
            var statsOwner = TryResolveReplicationStatsOwner(owner, behaviourOwner);
            hasHunger = TryReadReplicationNeedsStat(statsOwner, "Hunger", out hungerCurrent);
            hasSleep = TryReadReplicationNeedsStat(statsOwner, "Sleep", out sleepCurrent);
            return hasHunger || hasSleep;
        }

        private static bool TryGetReplicationNeedsEntityIdFromGoal(object goal, out string entityId)
        {
            var candidates = new[] { "hungerAgent", "creatureBase", "animal", "creature", "Agent", "agent" };
            for (var i = 0; i < candidates.Length; i++)
            {
                if (TryReadInstanceMemberValue(goal, candidates[i], out var owner)
                    && owner != null
                    && TryGetReplicationStableEntityId(owner, out entityId))
                {
                    return true;
                }
            }

            entityId = string.Empty;
            return false;
        }

        private static bool TryGetReplicationNeedsEntityIdFromStats(object stats, out string entityId)
        {
            var ownerNames = new[] { "OwnerHumanoidInstance", "OwnerAnimalInstance", "Owner", "ownerHumanoidInstance", "ownerAnimalInstance", "owner" };
            for (var i = 0; i < ownerNames.Length; i++)
            {
                if (TryReadReplicationObjectState(stats, ownerNames[i], out var owner)
                    && owner != null
                    && TryGetReplicationStableEntityId(owner, out entityId))
                {
                    return true;
                }
            }

            entityId = string.Empty;
            return false;
        }

        private static bool TryReadReplicationNeedsStat(object? statsOwner, string statName, out float value)
        {
            value = float.NaN;
            if (statsOwner == null)
            {
                return false;
            }

            if (!TryResolveReplicationNeedsStatSurface(statsOwner.GetType(), statName, out var getStat, out var enumValue))
            {
                return false;
            }

            try
            {
                var stat = getStat.Invoke(statsOwner, new[] { enumValue });
                if (stat == null || !TryReadReplicationObjectState(stat, "Current", out var raw) || raw == null)
                {
                    return false;
                }

                value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyReplicationNeedsStat(object? statsOwner, string statName, float value, out string detail)
        {
            detail = "stat-owner-missing";
            if (statsOwner == null || float.IsNaN(value) || float.IsInfinity(value))
            {
                return false;
            }

            if (!TryResolveReplicationNeedsStatSurface(statsOwner.GetType(), statName, out var getStat, out var enumValue))
            {
                detail = "stat-type-missing";
                return false;
            }

            try
            {
                var stat = getStat.Invoke(statsOwner, new[] { enumValue });
                if (stat == null)
                {
                    detail = "stat-missing name=" + statName;
                    return false;
                }

                if (!ReplicationNeedsSetStatMethodByType.TryGetValue(stat.GetType(), out var force))
                {
                    force = FindReplicationInstanceMethod(stat.GetType(), "ForceCurrentValue", new[] { typeof(float) })
                        ?? FindReplicationInstanceMethod(stat.GetType(), "SetCurrent", new[] { typeof(float) });
                    if (force == null)
                    {
                        detail = "stat-setter-missing name=" + statName;
                        return false;
                    }

                    ReplicationNeedsSetStatMethodByType[stat.GetType()] = force;
                }

                force.Invoke(stat, new object[] { value });
                detail = "ok " + statName + "=" + value.ToString("R", CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                detail = FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryResolveReplicationNeedsStatSurface(Type statsType, string statName, out MethodInfo getStat, out object enumValue)
        {
            getStat = null!;
            enumValue = null!;
            if (replicationNeedsStatType == null)
            {
                replicationNeedsStatType = AccessTools.TypeByName("NSMedieval.StatsSystem.StatType");
                if (replicationNeedsStatType == null || !replicationNeedsStatType.IsEnum)
                {
                    return false;
                }

                replicationNeedsHungerStatTypeValue = Enum.Parse(replicationNeedsStatType, "Hunger", true);
                replicationNeedsSleepStatTypeValue = Enum.Parse(replicationNeedsStatType, "Sleep", true);
            }

            enumValue = string.Equals(statName, "Hunger", StringComparison.Ordinal)
                ? replicationNeedsHungerStatTypeValue!
                : replicationNeedsSleepStatTypeValue!;
            if (!ReplicationNeedsGetStatMethodByType.TryGetValue(statsType, out getStat))
            {
                var method = FindReplicationInstanceMethod(statsType, "GetStat", new[] { replicationNeedsStatType });
                if (method == null)
                {
                    return false;
                }

                getStat = method;
                ReplicationNeedsGetStatMethodByType[statsType] = method;
            }

            return true;
        }

        private static bool TryApplyReplicationNeedsRepair(
            string entityId,
            bool hasHunger,
            float hungerCurrent,
            bool hasSleep,
            float sleepCurrent,
            bool isSleeping,
            out string detail)
        {
            if (!replicationConfigNeedsReplication)
            {
                detail = "needs-repair-gated-off";
                return true;
            }

            var pending = new ReplicationPendingNeedsRepair(
                entityId,
                hasHunger,
                hungerCurrent,
                hasSleep,
                sleepCurrent,
                isSleeping,
                Time.realtimeSinceStartup + 15f);
            ReplicationPendingNeedsRepairs[entityId] = pending;
            if (TryApplyReplicationNeedsRepairNow(pending, out detail))
            {
                ReplicationPendingNeedsRepairs.Remove(entityId);
                return true;
            }

            detail = "queued " + detail;
            return true;
        }

        private static bool TryApplyReplicationNeedsRepairNow(ReplicationPendingNeedsRepair pending, out string detail)
        {
            var ownerDetail = "owner-not-resolved";
            if (!TryFindReplicationNeedsView(pending.EntityId, out var view, out var viewDetail)
                || view == null
                || !TryResolveReplicationAgentOwnerFromView(view, out var owner, out ownerDetail)
                || owner == null)
            {
                detail = "needs-repair-owner-pending " + viewDetail + " " + ownerDetail;
                return false;
            }

            object? behaviourOwner = null;
            TryResolveReplicationBehaviourOwner(owner, out behaviourOwner);
            var statsOwner = TryResolveReplicationStatsOwner(owner, behaviourOwner);
            var applied = 0;
            var parts = new List<string>(3);
            if (pending.HasHunger && ShouldApplyReplicationNeedsStat(statsOwner, "Hunger", pending.HungerCurrent))
            {
                if (TryApplyReplicationNeedsStat(statsOwner, "Hunger", pending.HungerCurrent, out var hungerDetail))
                {
                    applied++;
                }
                parts.Add(hungerDetail);
            }

            if (pending.HasSleep && replicationConfigHostSleepPresentationV2 && pending.IsSleeping)
            {
                replicationNeedsSleepV2StatRepairsSuppressed++;
                parts.Add("Sleep=suppressed-authoritative-sleep");
            }
            else if (pending.HasSleep && ShouldApplyReplicationNeedsStat(statsOwner, "Sleep", pending.SleepCurrent))
            {
                if (TryApplyReplicationNeedsStat(statsOwner, "Sleep", pending.SleepCurrent, out var sleepDetail))
                {
                    applied++;
                }
                parts.Add(sleepDetail);
            }

            if (replicationConfigHostSleepPresentationV2)
            {
                var cleanupMatchedWake = !pending.IsSleeping
                    && ReplicationNeedsLastSleepVisualByEntityId.TryGetValue(pending.EntityId, out var wasVisuallySleeping)
                    && wasVisuallySleeping;
                parts.Add(ApplyReplicationNeedsHostSleepPresentationV2(
                    pending.EntityId,
                    view,
                    pending.IsSleeping,
                    cleanupMatchedWake,
                    authoritativeSequence: 0L,
                    source: "character-state"));
            }

            // Legacy mode records the observed state without driving guessed
            // Animator booleans. V2 above reconciles the native IsSleeping property.
            ReplicationNeedsLastSleepVisualByEntityId[pending.EntityId] = pending.IsSleeping;

            replicationNeedsRepairValuesApplied += applied;
            detail = "needs-repair-ok entityId=" + pending.EntityId
                + " valuesApplied=" + applied.ToString(CultureInfo.InvariantCulture)
                + " " + string.Join(" ", parts.ToArray());
            return true;
        }

        private static bool ShouldApplyReplicationNeedsStat(object? statsOwner, string statName, float desired)
        {
            return !TryReadReplicationNeedsStat(statsOwner, statName, out var current)
                || Mathf.Abs(current - desired) >= 0.01f;
        }

        private static void ProcessPendingReplicationNeedsRepairs()
        {
            if (!replicationConfigNeedsReplication || replicationConfigHostMode || ReplicationPendingNeedsRepairs.Count == 0)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            var keys = new List<string>(Math.Min(4, ReplicationPendingNeedsRepairs.Count));
            foreach (var pair in ReplicationPendingNeedsRepairs)
            {
                keys.Add(pair.Key);
                if (keys.Count >= 4)
                {
                    break;
                }
            }

            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (!ReplicationPendingNeedsRepairs.TryGetValue(key, out var pending))
                {
                    continue;
                }

                if (now >= pending.ExpiresRealtime || TryApplyReplicationNeedsRepairNow(pending, out _))
                {
                    ReplicationPendingNeedsRepairs.Remove(key);
                }
            }
        }

        private static bool TryApplyReplicationNeedsLifecycleDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!replicationConfigNeedsReplication)
            {
                detail = "needs-replication-gated-off";
                return true;
            }

            if (!TryReadReplicationWorldObjectDetailToken(delta.Detail, "entityId", out var entityId)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "needKind", out var kind)
                || !TryReadReplicationWorldObjectDetailToken(delta.Detail, "phase", out var phase))
            {
                detail = "needs-lifecycle-payload-invalid detail=" + delta.Detail;
                return true;
            }

            var stateKey = entityId + ":" + kind;
            if (ReplicationNeedsLastAppliedDeltaByEntityAndKind.TryGetValue(stateKey, out var lastSequence)
                && delta.Sequence <= lastSequence)
            {
                detail = "needs-lifecycle-stale entityId=" + entityId + " kind=" + kind;
                return true;
            }

            if (!TryFindReplicationNeedsView(entityId, out var view, out var viewDetail) || view == null)
            {
                detail = "needs-lifecycle-view-pending entityId=" + entityId + " " + viewDetail;
                return false;
            }

            var visualDetail = "visual=none";
            if (string.Equals(kind, "hunger", StringComparison.Ordinal))
            {
                if (string.Equals(phase, "visual-started", StringComparison.Ordinal))
                {
                    TryInvokeReplicationObjectVoidMethod(view, "EatParticles", out visualDetail);
                }
                else if (string.Equals(phase, "visual-ended", StringComparison.Ordinal)
                    || string.Equals(phase, "ended", StringComparison.Ordinal))
                {
                    TryInvokeReplicationObjectVoidMethod(view, "StopEatParticles", out visualDetail);
                }
            }
            else if (string.Equals(kind, "sleep", StringComparison.Ordinal))
            {
                var sleeping = string.Equals(phase, "sleeping", StringComparison.Ordinal);
                if (replicationConfigHostSleepPresentationV2
                    && (string.Equals(phase, "sleeping", StringComparison.Ordinal)
                        || string.Equals(phase, "woke", StringComparison.Ordinal)))
                {
                    var cleanupMatchedWake = !sleeping
                        && ReplicationNeedsLastSleepVisualByEntityId.TryGetValue(entityId, out var wasVisuallySleeping)
                        && wasVisuallySleeping;
                    visualDetail = ApplyReplicationNeedsHostSleepPresentationV2(
                        entityId,
                        view,
                        sleeping,
                        cleanupMatchedWake,
                        delta.Sequence,
                        "lifecycle");
                    ReplicationNeedsLastSleepVisualByEntityId[entityId] = sleeping;
                }
                else
                {
                    // Legacy rollback path: replay the native global lifecycle event
                    // and the view animation callback at each host session boundary.
                    ReplicationNeedsLastSleepVisualByEntityId[entityId] = sleeping;
                    visualDetail = string.Equals(phase, "sleeping", StringComparison.Ordinal)
                        ? ApplyReplicationNeedsNativeSleepPresentation(view)
                        : string.Equals(phase, "woke", StringComparison.Ordinal)
                            ? ApplyReplicationNeedsNativeWakePresentation(view)
                            : "sleep-session-recorded native-visual";
                }
            }

            TryReadReplicationWorldObjectDetailBool(delta.Detail, "hasHunger", out var hasHunger);
            TryReadReplicationWorldObjectDetailBool(delta.Detail, "hasSleep", out var hasSleep);
            var hungerCurrent = TryReadReplicationWorldObjectDetailFloat(delta.Detail, "hungerCurrent", out var eventHunger)
                ? eventHunger
                : 0f;
            var sleepCurrent = TryReadReplicationWorldObjectDetailFloat(delta.Detail, "sleepCurrent", out var eventSleep)
                ? eventSleep
                : 0f;
            // During an active sleep session, writing Sleep.Current on the
            // client can make its native goal reevaluate and stand the pawn up.
            // Reconcile it once the host says the session woke instead.
            var applySleepStat = !string.Equals(kind, "sleep", StringComparison.Ordinal)
                || !string.Equals(phase, "sleeping", StringComparison.Ordinal);
            if (hasHunger || (hasSleep && applySleepStat))
            {
                var sleepingState = ReplicationNeedsLastSleepVisualByEntityId.TryGetValue(entityId, out var currentSleeping)
                    && currentSleeping;
                TryApplyReplicationNeedsRepair(entityId, hasHunger, hungerCurrent, hasSleep && applySleepStat, sleepCurrent, sleepingState, out _);
            }

            ReplicationNeedsLastAppliedDeltaByEntityAndKind[stateKey] = delta.Sequence;
            replicationNeedsEventsApplied++;
            detail = "ok needs-lifecycle entityId=" + entityId + " kind=" + kind + " phase=" + phase + " " + visualDetail;
            return true;
        }

        private static string ApplyReplicationNeedsSleepVisual(object view, bool sleeping)
        {
            if (!TryResolveReplicationAnimator(view, out var animator) || animator == null)
            {
                return "sleep-animator-missing";
            }

            var parameters = new[] { "Sleeping", "IsSleeping", "Sleep" };
            for (var i = 0; i < parameters.Length; i++)
            {
                if (TrySetReplicationAnimatorBool(animator, parameters[i], sleeping))
                {
                    return "sleep-animator=" + parameters[i] + " value=" + sleeping;
                }
            }

            return "sleep-parameter-missing";
        }

        private int TryPatchReplicationNeedsMethodsPrefix(Harmony harmonyInstance, HarmonyMethod prefix, string typeName, params string[] methodNames)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                LogReplicationWarning("Going Cooperative needs hook type missing type=" + typeName);
                return 0;
            }

            var wanted = new HashSet<string>(methodNames, StringComparer.Ordinal);
            var patched = 0;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!wanted.Contains(method.Name) || method.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    harmonyInstance.Patch(method, prefix: prefix);
                    patched++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning("Going Cooperative needs hook failed method="
                        + typeName + "." + method.Name + " error=" + FormatReflectionExceptionDetail(ex));
                }
            }

            return patched;
        }

        private static void ReplicationNeedsFloatingElementHolderRefreshPositionPrefix(object __instance)
        {
            if (!replicationConfigHostSleepPresentationV2
                || !replicationConfigEnabled
                || replicationConfigHostMode
                || !replicationRuntimeStarted
                || __instance == null)
            {
                return;
            }

            try
            {
                var hook = AccessTools.Field(__instance.GetType(), "followingToTransform")?.GetValue(__instance) as Transform;
                if (hook == null
                    || !ReplicationNeedsSleepEntityIdByOverlayHookInstanceId.TryGetValue(hook.GetInstanceID(), out var entityId)
                    || !IsReplicationNeedsSleepPresentationActive(entityId)
                    || !ReplicationNeedsSleepPresentationV2ByEntityId.TryGetValue(entityId, out var state)
                    || !ReferenceEquals(state.OverlayHook, hook)
                    || !state.PoseLocked)
                {
                    return;
                }

                if (!state.OverlayHookPositionLocked)
                {
                    state.OverlayHookPositionLocked = true;
                    state.LockedOverlayHookPosition = hook.position;
                    replicationNeedsSleepV2OverlayHookLocks++;
                    instance?.LogReplicationInfo("Going Cooperative sleep-v2 overlay hook locked entityId="
                        + entityId
                        + " position=("
                        + hook.position.x.ToString("0.000", CultureInfo.InvariantCulture) + ","
                        + hook.position.y.ToString("0.000", CultureInfo.InvariantCulture) + ","
                        + hook.position.z.ToString("0.000", CultureInfo.InvariantCulture) + ")");
                    return;
                }

                hook.position = state.LockedOverlayHookPosition;
                replicationNeedsSleepV2OverlayHookHeldFrames++;
            }
            catch
            {
                // This is a per-frame presentation correction.  If a holder is
                // being destroyed or rebuilt, let vanilla complete that frame.
            }
        }

        private static bool ReplicationNeedsIsSleepingPrefix(object __instance, ref bool __0)
        {
            if (!replicationConfigHostSleepPresentationV2
                || !replicationConfigEnabled
                || replicationConfigHostMode
                || !replicationRuntimeStarted
                || applyingRuntimeCommandDepth > 0
                || __instance == null
                || !TryGetReplicationStableEntityId(__instance, out var entityId)
                || !ReplicationNeedsSleepPresentationV2ByEntityId.TryGetValue(entityId, out var state)
                || !state.Initialized
                || __0 == state.DesiredSleeping)
            {
                return true;
            }

            replicationNeedsSleepV2ContradictoryNativeWritesSuppressed++;
            if (replicationNeedsSleepV2ContradictoryNativeWritesSuppressed <= 8
                || (replicationNeedsSleepV2ContradictoryNativeWritesSuppressed
                    & (replicationNeedsSleepV2ContradictoryNativeWritesSuppressed - 1)) == 0)
            {
                instance?.LogReplicationInfo("Going Cooperative sleep-v2 suppressed contradictory client IsSleeping write entityId="
                    + entityId
                    + " attempted=" + (__0 ? "1" : "0")
                    + " authoritative=" + (state.DesiredSleeping ? "1" : "0")
                    + " count=" + replicationNeedsSleepV2ContradictoryNativeWritesSuppressed.ToString(CultureInfo.InvariantCulture));
            }

            return false;
        }

        private static string ApplyReplicationNeedsNativeSleepTransition(object view, bool sleeping)
        {
            var action = sleeping ? "sleep" : "wake";
            try
            {
                if (!TryResolveReplicationAgentOwnerFromView(view, out var owner, out var ownerDetail)
                    || owner == null)
                {
                    return "sleep-" + action + "-owner-missing " + ownerDetail;
                }

                object? behaviourOwner = null;
                TryResolveReplicationBehaviourOwner(owner, out behaviourOwner);
                var statsOwner = TryResolveReplicationStatsOwner(owner, behaviourOwner);
                var statsType = AccessTools.TypeByName("NSMedieval.StatsSystem.StatsInstance");
                var lifeType = AccessTools.TypeByName("NSMedieval.Controllers.LifeController");
                if (statsOwner == null || statsType == null || !statsType.IsInstanceOfType(statsOwner) || lifeType == null)
                {
                    return "sleep-" + action + "-surface-missing";
                }

                var controller = ResolveReplicationUnityManagerInstance(lifeType);
                var method = FindReplicationInstanceMethod(lifeType, sleeping ? "FallAsleep" : "WakeUp", new[] { statsType });
                if (controller == null || method == null)
                {
                    return "sleep-" + action + "-method-missing";
                }

                applyingRuntimeCommandDepth++;
                try
                {
                    method.Invoke(controller, new[] { statsOwner });
                    return "sleep-" + action + "-native";
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }
            }
            catch (Exception ex)
            {
                return "sleep-" + action + "-error " + FormatReflectionExceptionDetail(ex);
            }
        }

        private static string ApplyReplicationNeedsHostSleepPresentationV2(
            string entityId,
            object view,
            bool sleeping,
            bool cleanupMatchedWake,
            long authoritativeSequence,
            string source)
        {
            try
            {
                if (!TryResolveReplicationAgentOwnerFromView(view, out var owner, out var ownerDetail)
                    || owner == null)
                {
                    return "sleep-v2-owner-missing " + ownerDetail;
                }

                if (!ReplicationNeedsSleepPresentationV2ByEntityId.TryGetValue(entityId, out var presentationState))
                {
                    presentationState = new ReplicationSleepPresentationV2State();
                    ReplicationNeedsSleepPresentationV2ByEntityId[entityId] = presentationState;
                }
                BindReplicationNeedsSleepOverlayHook(entityId, view, presentationState);

                var previouslyInitialized = presentationState.Initialized;
                var previouslySleeping = presentationState.DesiredSleeping;
                var desiredChanged = !previouslyInitialized || previouslySleeping != sleeping;
                if (desiredChanged)
                {
                    presentationState.Initialized = true;
                    presentationState.DesiredSleeping = sleeping;
                    presentationState.VisualAppliedForDesiredState = false;
                    ResetReplicationNeedsSleepPresentationPose(presentationState);
                }
                if (authoritativeSequence > presentationState.LastAuthoritativeSequence)
                {
                    presentationState.LastAuthoritativeSequence = authoritativeSequence;
                }

                var stateKnown = TryReadReplicationBooleanState(owner, "IsSleeping", out var currentSleeping);
                var logicalChanged = !stateKnown || currentSleeping != sleeping;
                if (logicalChanged)
                {
                    var setter = AccessTools.Property(owner.GetType(), "IsSleeping")?.GetSetMethod(true);
                    if (setter == null)
                    {
                        return "sleep-v2-setter-missing " + ownerDetail;
                    }

                    applyingRuntimeCommandDepth++;
                    try
                    {
                        setter.Invoke(owner, new object[] { sleeping });
                    }
                    finally
                    {
                        applyingRuntimeCommandDepth--;
                    }
                    replicationNeedsSleepV2TransitionsApplied++;
                }
                else
                {
                    replicationNeedsSleepV2MatchedStates++;
                }

                if (sleeping)
                {
                    if (presentationState.VisualAppliedForDesiredState)
                    {
                        return "sleep-v2-state-matched sleeping=1 visual=already-applied";
                    }

                    string animationDetail;
                    applyingRuntimeCommandDepth++;
                    try
                    {
                        animationDetail = InvokeReplicationSemanticWorkAnimationTrigger(view, "Sleep", string.Empty);
                    }
                    finally
                    {
                        applyingRuntimeCommandDepth--;
                    }
                    presentationState.VisualAppliedForDesiredState = true;
                    replicationNeedsSleepV2VisualTransitionsApplied++;
                    if (!previouslyInitialized)
                    {
                        replicationNeedsSleepV2RecoveryVisualsApplied++;
                    }
                    LogReplicationNeedsSleepV2VisualTransition(
                        entityId,
                        source,
                        sleeping: true,
                        desiredChanged,
                        logicalChanged,
                        authoritativeSequence,
                        animationDetail);
                    return "sleep-v2-transition sleeping=1 " + animationDetail;
                }

                var shouldCleanupWake = !presentationState.VisualAppliedForDesiredState
                    && ((previouslyInitialized && previouslySleeping) || cleanupMatchedWake);
                if (!shouldCleanupWake)
                {
                    presentationState.VisualAppliedForDesiredState = true;
                    return "sleep-v2-state-matched sleeping=0 cleanup=not-required";
                }

                var cleanupDetail = ApplyReplicationNeedsWakeVisualCleanup(view);
                presentationState.VisualAppliedForDesiredState = true;
                replicationNeedsSleepV2VisualTransitionsApplied++;
                LogReplicationNeedsSleepV2VisualTransition(
                    entityId,
                    source,
                    sleeping: false,
                    desiredChanged,
                    logicalChanged,
                    authoritativeSequence,
                    cleanupDetail);
                return "sleep-v2-transition sleeping=0 " + cleanupDetail;
            }
            catch (Exception ex)
            {
                return "sleep-v2-error " + FormatReflectionExceptionDetail(ex);
            }
        }

        private static void LogReplicationNeedsSleepV2VisualTransition(
            string entityId,
            string source,
            bool sleeping,
            bool desiredChanged,
            bool logicalChanged,
            long authoritativeSequence,
            string visualDetail)
        {
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            current.LogReplicationInfo("Going Cooperative sleep-v2 visual applied entityId="
                + entityId
                + " sleeping=" + (sleeping ? "1" : "0")
                + " source=" + source
                + " desiredChanged=" + (desiredChanged ? "1" : "0")
                + " logicalChanged=" + (logicalChanged ? "1" : "0")
                + " sequence=" + authoritativeSequence.ToString(CultureInfo.InvariantCulture)
                + " detail=" + visualDetail);
        }

        private static bool IsReplicationNeedsSleepPresentationActive(string entityId)
        {
            return replicationConfigHostSleepPresentationV2
                && !string.IsNullOrWhiteSpace(entityId)
                && ReplicationNeedsSleepPresentationV2ByEntityId.TryGetValue(entityId, out var state)
                && state.Initialized
                && state.DesiredSleeping
                && state.VisualAppliedForDesiredState;
        }

        private static bool TryStabilizeReplicationNeedsSleepPresentationPose(
            string entityId,
            float now,
            ref Vector3 position,
            ref Quaternion rotation)
        {
            if (!IsReplicationNeedsSleepPresentationActive(entityId)
                || !ReplicationNeedsSleepPresentationV2ByEntityId.TryGetValue(entityId, out var state))
            {
                return false;
            }

            if (state.PoseLocked)
            {
                var breakDistance = position - state.LockedPosePosition;
                if (breakDistance.sqrMagnitude
                    <= ReplicationNeedsSleepV2PoseBreakDistance * ReplicationNeedsSleepV2PoseBreakDistance)
                {
                    position = state.LockedPosePosition;
                    rotation = state.LockedPoseRotation;
                    replicationNeedsSleepV2PoseHeldFrames++;
                    return true;
                }

                ResetReplicationNeedsSleepPresentationPose(state);
                replicationNeedsSleepV2PoseLockBreaks++;
            }

            if (!state.PoseCandidateInitialized)
            {
                state.PoseCandidateInitialized = true;
                state.PoseCandidatePosition = position;
                state.PoseCandidateRotation = rotation;
                state.PoseCandidateSinceRealtime = now;
                return true;
            }

            var candidateDistance = position - state.PoseCandidatePosition;
            var candidateAngle = Quaternion.Angle(rotation, state.PoseCandidateRotation);
            if (candidateDistance.sqrMagnitude
                    > ReplicationNeedsSleepV2PoseSettleDistance * ReplicationNeedsSleepV2PoseSettleDistance
                || candidateAngle > ReplicationNeedsSleepV2PoseSettleAngleDegrees)
            {
                state.PoseCandidatePosition = position;
                state.PoseCandidateRotation = rotation;
                state.PoseCandidateSinceRealtime = now;
                return true;
            }

            if (now - state.PoseCandidateSinceRealtime < ReplicationNeedsSleepV2PoseSettleSeconds)
            {
                return true;
            }

            state.PoseLocked = true;
            state.LockedPosePosition = position;
            state.LockedPoseRotation = rotation;
            replicationNeedsSleepV2PoseLocks++;
            instance?.LogReplicationInfo("Going Cooperative sleep-v2 pose locked entityId="
                + entityId
                + " position=("
                + position.x.ToString("0.000", CultureInfo.InvariantCulture) + ","
                + position.y.ToString("0.000", CultureInfo.InvariantCulture) + ","
                + position.z.ToString("0.000", CultureInfo.InvariantCulture) + ")");
            return true;
        }

        private static void ResetReplicationNeedsSleepPresentationPose(ReplicationSleepPresentationV2State state)
        {
            state.PoseCandidateInitialized = false;
            state.PoseCandidatePosition = default;
            state.PoseCandidateRotation = Quaternion.identity;
            state.PoseCandidateSinceRealtime = 0f;
            state.PoseLocked = false;
            state.LockedPosePosition = default;
            state.LockedPoseRotation = Quaternion.identity;
            state.OverlayHookPositionLocked = false;
            state.LockedOverlayHookPosition = default;
        }

        private static void BindReplicationNeedsSleepOverlayHook(
            string entityId,
            object view,
            ReplicationSleepPresentationV2State state)
        {
            var hook = AccessTools.Field(view.GetType(), "gameplayOverlayHook")?.GetValue(view) as Transform;
            if (hook == null || ReferenceEquals(state.OverlayHook, hook))
            {
                return;
            }

            if (state.OverlayHookInstanceId != 0)
            {
                ReplicationNeedsSleepEntityIdByOverlayHookInstanceId.Remove(state.OverlayHookInstanceId);
            }

            state.OverlayHook = hook;
            state.OverlayHookInstanceId = hook.GetInstanceID();
            state.OverlayHookPositionLocked = false;
            state.LockedOverlayHookPosition = default;
            ReplicationNeedsSleepEntityIdByOverlayHookInstanceId[state.OverlayHookInstanceId] = entityId;
        }

        private static string ApplyReplicationNeedsNativeSleepPresentation(object view)
        {
            // LifeController.FallAsleep merely raises the WorkerManager event;
            // the visual itself is the GOAP LayDown animation.  Replaying that
            // controller trigger is presentation-only and avoids changing the
            // client's sleep simulation/stat state.
            var lifecycleDetail = ApplyReplicationNeedsNativeSleepTransition(view, true);
            try
            {
                applyingRuntimeCommandDepth++;
                try
                {
                    // Semantic replay deliberately prefers HumanoidView's
                    // OnTriggerAnimation path. The generic helper prefers
                    // AnimationController, which reports success but does not
                    // drive this view's LayDown pose in a client replica.
                    // LayDown is the action id; the actual animator trigger
                    // supplied by LayDownBaseGoal is Sleep.
                    var animationDetail = InvokeReplicationSemanticWorkAnimationTrigger(view, "Sleep", string.Empty);
                    return lifecycleDetail + " " + animationDetail;
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }
            }
            catch (Exception ex)
            {
                return lifecycleDetail + " sleep-laydown-error " + FormatReflectionExceptionDetail(ex);
            }
        }

        private static string ApplyReplicationNeedsNativeWakePresentation(object view)
        {
            var lifecycleDetail = ApplyReplicationNeedsNativeSleepTransition(view, false);
            return lifecycleDetail + " " + ApplyReplicationNeedsWakeVisualCleanup(view);
        }

        private static string ApplyReplicationNeedsWakeVisualCleanup(object view)
        {
            try
            {
                applyingRuntimeCommandDepth++;
                try
                {
                    var method = FindReplicationInstanceMethod(view.GetType(), "ForceQuitAnimation", Type.EmptyTypes)
                        ?? FindReplicationInstanceMethod(view.GetType(), "ResetTriggers", Type.EmptyTypes);
                    if (method == null)
                    {
                        return "sleep-wake-visual-method-missing";
                    }

                    method.Invoke(view, null);
                    if (TryResolveReplicationAnimator(view, out var animator) && animator != null)
                    {
                        TrySetReplicationAnimatorBool(animator, "Sleep", false);
                        TrySetReplicationAnimatorBool(animator, "Sleeping", false);
                        TrySetReplicationAnimatorBool(animator, "IsSleeping", false);
                    }
                    return "sleep-wake-visual=" + method.Name;
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }
            }
            catch (Exception ex)
            {
                return "sleep-wake-visual-error " + FormatReflectionExceptionDetail(ex);
            }
        }

        private static bool TryFindReplicationNeedsView(string entityId, out object? view, out string detail)
        {
            var now = Time.realtimeSinceStartup;
            if (replicationNeedsViewCache == null || now - replicationNeedsViewCacheBuiltRealtime >= 2f)
            {
                RefreshReplicationNeedsViewCache(now);
            }

            if (replicationNeedsViewCache != null
                && replicationNeedsViewCache.TryGetValue(entityId, out var cached)
                && cached is UnityEngine.Object unityObject
                && unityObject != null)
            {
                view = cached;
                detail = "needs-view-cache-hit";
                return true;
            }

            if (now - replicationNeedsViewCacheBuiltRealtime >= 0.5f)
            {
                RefreshReplicationNeedsViewCache(now);
                if (replicationNeedsViewCache != null
                    && replicationNeedsViewCache.TryGetValue(entityId, out cached)
                    && cached is UnityEngine.Object refreshedObject
                    && refreshedObject != null)
                {
                    view = cached;
                    detail = "needs-view-cache-refresh-hit";
                    return true;
                }
            }

            view = null;
            detail = "needs-view-cache-miss count="
                + (replicationNeedsViewCache?.Count ?? 0).ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static void RefreshReplicationNeedsViewCache(float now)
        {
            var cache = new Dictionary<string, object>(StringComparer.Ordinal);
            var views = FindReplicationAnimatedAgentViews();
            for (var i = 0; i < views.Length; i++)
            {
                var candidate = views[i];
                if (candidate != null
                    && TryGetReplicationViewEntityId(candidate, out var entityId)
                    && !string.IsNullOrWhiteSpace(entityId)
                    && !cache.ContainsKey(entityId))
                {
                    cache[entityId] = candidate;
                }
            }

            replicationNeedsViewCache = cache;
            replicationNeedsViewCacheBuiltRealtime = now;
        }

        private static void RecordReplicationNeedsGoalStatus(string entityId, string goalId, bool started)
        {
            if (!ShouldCaptureReplicationNeeds())
            {
                return;
            }

            var kind = goalId.IndexOf("Hunger", StringComparison.OrdinalIgnoreCase) >= 0
                || goalId.IndexOf("Eat", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "hunger"
                    : goalId.IndexOf("Sleep", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "sleep"
                        : goalId.IndexOf("Rest", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "rest"
                            : string.Empty;
            if (kind.Length == 0)
            {
                return;
            }

            SendReplicationNeedsLifecycle(entityId, kind, started ? "seeking" : "ended",
                "source=WorkerBehaviour.GoalUpdated goalB64=" + EncodeReplicationDetailBase64(goalId));
        }

        private static void ClearReplicationNeedsState()
        {
            ReplicationNeedsEventSequenceByEntityId.Clear();
            ReplicationNeedsLastAppliedDeltaByEntityAndKind.Clear();
            ReplicationNeedsLastSleepVisualByEntityId.Clear();
            ReplicationNeedsSleepPresentationV2ByEntityId.Clear();
            ReplicationNeedsSleepEntityIdByOverlayHookInstanceId.Clear();
            ReplicationNeedsHostSleepSessionByEntityId.Clear();
            ReplicationPendingNeedsRepairs.Clear();
            replicationNeedsViewCache = null;
            replicationNeedsViewCacheBuiltRealtime = 0f;
            ReplicationNeedsGetStatMethodByType.Clear();
            ReplicationNeedsSetStatMethodByType.Clear();
            replicationNeedsStatType = null;
            replicationNeedsHungerStatTypeValue = null;
            replicationNeedsSleepStatTypeValue = null;
            replicationNeedsEventsSent = 0L;
            replicationNeedsEventsApplied = 0L;
            replicationNeedsRepairValuesApplied = 0L;
            replicationNeedsSleepV2TransitionsApplied = 0L;
            replicationNeedsSleepV2MatchedStates = 0L;
            replicationNeedsSleepV2StatRepairsSuppressed = 0L;
            replicationNeedsSleepV2VisualTransitionsApplied = 0L;
            replicationNeedsSleepV2RecoveryVisualsApplied = 0L;
            replicationNeedsSleepV2PoseLocks = 0L;
            replicationNeedsSleepV2PoseLockBreaks = 0L;
            replicationNeedsSleepV2PoseHeldFrames = 0L;
            replicationNeedsSleepV2OverlayHookLocks = 0L;
            replicationNeedsSleepV2OverlayHookHeldFrames = 0L;
            replicationNeedsSleepV2ContradictoryNativeWritesSuppressed = 0L;
        }

        private static string FormatReplicationNeedsStatus()
        {
            return "needsReplication=" + replicationConfigNeedsReplication
                + " needsEventsSent=" + replicationNeedsEventsSent
                + " needsEventsApplied=" + replicationNeedsEventsApplied
                + " needsRepairApplied=" + replicationNeedsRepairValuesApplied
                + " sleepV2Transitions=" + replicationNeedsSleepV2TransitionsApplied
                + " sleepV2Matched=" + replicationNeedsSleepV2MatchedStates
                + " sleepV2StatSuppressed=" + replicationNeedsSleepV2StatRepairsSuppressed
                + " sleepV2Visuals=" + replicationNeedsSleepV2VisualTransitionsApplied
                + " sleepV2RecoveryVisuals=" + replicationNeedsSleepV2RecoveryVisualsApplied
                + " sleepV2PoseLocks=" + replicationNeedsSleepV2PoseLocks
                + " sleepV2PoseBreaks=" + replicationNeedsSleepV2PoseLockBreaks
                + " sleepV2PoseHeld=" + replicationNeedsSleepV2PoseHeldFrames
                + " sleepV2OverlayLocks=" + replicationNeedsSleepV2OverlayHookLocks
                + " sleepV2OverlayHeld=" + replicationNeedsSleepV2OverlayHookHeldFrames
                + " sleepV2NativeWritesSuppressed=" + replicationNeedsSleepV2ContradictoryNativeWritesSuppressed;
        }

        private sealed class ReplicationPendingNeedsRepair
        {
            public ReplicationPendingNeedsRepair(
                string entityId,
                bool hasHunger,
                float hungerCurrent,
                bool hasSleep,
                float sleepCurrent,
                bool isSleeping,
                float expiresRealtime)
            {
                EntityId = entityId;
                HasHunger = hasHunger;
                HungerCurrent = hungerCurrent;
                HasSleep = hasSleep;
                SleepCurrent = sleepCurrent;
                IsSleeping = isSleeping;
                ExpiresRealtime = expiresRealtime;
            }

            public string EntityId { get; }
            public bool HasHunger { get; }
            public float HungerCurrent { get; }
            public bool HasSleep { get; }
            public float SleepCurrent { get; }
            public bool IsSleeping { get; }
            public float ExpiresRealtime { get; }
        }
    }
}
