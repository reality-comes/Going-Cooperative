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

            if (pending.HasSleep && ShouldApplyReplicationNeedsStat(statsOwner, "Sleep", pending.SleepCurrent))
            {
                if (TryApplyReplicationNeedsStat(statsOwner, "Sleep", pending.SleepCurrent, out var sleepDetail))
                {
                    applied++;
                }
                parts.Add(sleepDetail);
            }

            if (!ReplicationNeedsLastSleepVisualByEntityId.TryGetValue(pending.EntityId, out var lastSleeping)
                || lastSleeping != pending.IsSleeping)
            {
                parts.Add(ApplyReplicationNeedsSleepVisual(view, pending.IsSleeping));
                ReplicationNeedsLastSleepVisualByEntityId[pending.EntityId] = pending.IsSleeping;
            }

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
                if (string.Equals(phase, "sleeping", StringComparison.Ordinal)
                    || string.Equals(phase, "woke", StringComparison.Ordinal)
                    || string.Equals(phase, "ended", StringComparison.Ordinal))
                {
                    var sleeping = string.Equals(phase, "sleeping", StringComparison.Ordinal);
                    visualDetail = ApplyReplicationNeedsSleepVisual(view, sleeping);
                    ReplicationNeedsLastSleepVisualByEntityId[entityId] = sleeping;
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
            if (hasHunger || hasSleep)
            {
                var sleepingState = ReplicationNeedsLastSleepVisualByEntityId.TryGetValue(entityId, out var currentSleeping)
                    && currentSleeping;
                TryApplyReplicationNeedsRepair(entityId, hasHunger, hungerCurrent, hasSleep, sleepCurrent, sleepingState, out _);
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
        }

        private static string FormatReplicationNeedsStatus()
        {
            return "needsReplication=" + replicationConfigNeedsReplication
                + " needsEventsSent=" + replicationNeedsEventsSent
                + " needsEventsApplied=" + replicationNeedsEventsApplied
                + " needsRepairApplied=" + replicationNeedsRepairValuesApplied;
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
