using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly HashSet<string> ReplicationHostDrivenGoapPlaybackGoals = new HashSet<string>(StringComparer.Ordinal)
        {
            "ChopTreeGoal"
        };

        private static readonly Dictionary<string, string> ReplicationHostDrivenGoapPlaybackLastGoalByEntityId =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, object> ReplicationHostDrivenGoapPlaybackAgentsByEntityId =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, MethodInfo?> ReplicationHostDrivenGoapPlaybackTickMethodsByType =
            new Dictionary<Type, MethodInfo?>();
        private static readonly HashSet<string> ReplicationHostDrivenGoapPlaybackTickStartedEntityIds =
            new HashSet<string>(StringComparer.Ordinal);

        // This is deliberately narrow. It proves that a host-selected goal can be
        // placed on the matching client agent without re-enabling client scheduling.
        private static string TryStartReplicationHostDrivenGoapPlayback(string entityId, string goalId, bool hasStarted, bool isHeartbeat)
        {
            if (!replicationConfigHostDrivenGoapPlayback
                || replicationConfigHostMode
                || isHeartbeat
                || string.IsNullOrWhiteSpace(entityId))
            {
                return "off";
            }

            string result;
            if (!hasStarted)
            {
                ReplicationHostDrivenGoapPlaybackLastGoalByEntityId.Remove(entityId);
                ReplicationHostDrivenGoapPlaybackAgentsByEntityId.Remove(entityId);
                ReplicationHostDrivenGoapPlaybackTickStartedEntityIds.Remove(entityId);
                result = "cleared";
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, result);
                return result;
            }

            if (!ReplicationHostDrivenGoapPlaybackGoals.Contains(goalId))
            {
                ReplicationHostDrivenGoapPlaybackLastGoalByEntityId.Remove(entityId);
                ReplicationHostDrivenGoapPlaybackAgentsByEntityId.Remove(entityId);
                ReplicationHostDrivenGoapPlaybackTickStartedEntityIds.Remove(entityId);
                result = "unsupported-goal=" + goalId;
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, result);
                return result;
            }

            if (ReplicationHostDrivenGoapPlaybackLastGoalByEntityId.TryGetValue(entityId, out var previousGoal)
                && string.Equals(previousGoal, goalId, StringComparison.Ordinal))
            {
                result = "already-requested";
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, result);
                return result;
            }

            if (!TryFindReplicationHostDrivenGoapAgentByEntityId(entityId, out var agent, out var agentDetail) || agent == null)
            {
                result = "agent-lookup-failed " + agentDetail;
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, result);
                return result;
            }

            var forceNextGoal = agent.GetType().GetMethod(
                "ForceNextGoal",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            if (forceNextGoal == null)
            {
                result = "force-next-goal-missing agent=" + agent.GetType().Name;
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, result);
                return result;
            }

            try
            {
                var invokeResult = forceNextGoal.Invoke(agent, new object[] { goalId });
                ReplicationHostDrivenGoapPlaybackLastGoalByEntityId[entityId] = goalId;
                ReplicationHostDrivenGoapPlaybackAgentsByEntityId[entityId] = agent;
                var requestDetail = "requested agent="
                    + agent.GetType().Name
                    + " result="
                    + (invokeResult == null ? "<void>" : invokeResult.GetType().Name)
                    + " "
                    + agentDetail;
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, requestDetail);
                return requestDetail;
            }
            catch (Exception ex)
            {
                result = "force-next-goal-error " + ex.GetType().Name + " " + ex.Message;
                LogReplicationHostDrivenGoapPlaybackProbe(entityId, goalId, result);
                return result;
            }
        }

        private static void LogReplicationHostDrivenGoapPlaybackProbe(string entityId, string goalId, string detail)
        {
            if (!ShouldLogReplicationGoapActionProbe(entityId))
            {
                return;
            }

            LogReplicationGoapActionProbe(entityId, "host-driven-playback", "goal=" + goalId + " " + detail);
        }

        private static bool TryResolveReplicationHostDrivenGoapAgent(object owner, out object? agent, out string detail)
        {
            agent = null;

            if (TryInvokeReplicationObjectMethod(owner, "GetAgent", out agent) && agent != null)
            {
                detail = "agent-source=GetAgent";
                return true;
            }

            if (TryInvokeReplicationObjectMethod(owner, "get_WorkerGoapAgent", out agent) && agent != null)
            {
                detail = "agent-source=WorkerGoapAgent";
                return true;
            }

            if (TryInvokeReplicationObjectMethod(owner, "get_AnimalGoapAgent", out agent) && agent != null)
            {
                detail = "agent-source=AnimalGoapAgent";
                return true;
            }

            detail = "agent-source=unresolved";
            return false;
        }

        private static bool TryFindReplicationHostDrivenGoapAgentByEntityId(string entityId, out object? agent, out string detail)
        {
            agent = null;
            if (TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail)
                && view != null
                && TryInvokeReplicationObjectMethod(view, "GetAgent", out agent)
                && agent != null)
            {
                detail = "agent-source=view.GetAgent " + viewDetail;
                return true;
            }

            if (TryFindReplicationAgentOwnerByEntityId(entityId, out var owner, out var ownerDetail)
                && owner != null
                && TryResolveReplicationHostDrivenGoapAgent(owner, out agent, out var ownerAgentDetail)
                && agent != null)
            {
                detail = "agent-source=owner-fallback " + ownerDetail + " " + ownerAgentDetail;
                return true;
            }

            detail = "agent-source=unresolved " + ownerDetail;
            return false;
        }

        private static void TickReplicationHostDrivenGoapPlaybackAgents()
        {
            if (!replicationConfigHostDrivenGoapPlayback
                || !replicationConfigHostDrivenGoapPlaybackTick
                || replicationConfigHostMode
                || ReplicationHostDrivenGoapPlaybackAgentsByEntityId.Count == 0)
            {
                return;
            }

            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            foreach (var pair in ReplicationHostDrivenGoapPlaybackAgentsByEntityId)
            {
                var agent = pair.Value;
                var agentType = agent.GetType();
                if (!ReplicationHostDrivenGoapPlaybackTickMethodsByType.TryGetValue(agentType, out var tick))
                {
                    tick = agentType.GetMethod(
                        "Tick",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(float) },
                        null);
                    ReplicationHostDrivenGoapPlaybackTickMethodsByType[agentType] = tick;
                }

                if (tick == null)
                {
                    LogReplicationHostDrivenGoapPlaybackProbe(pair.Key, "<active>", "tick-missing agent=" + agentType.Name);
                    continue;
                }

                try
                {
                    tick.Invoke(agent, new object[] { deltaTime });
                    if (ReplicationHostDrivenGoapPlaybackTickStartedEntityIds.Add(pair.Key))
                    {
                        LogReplicationHostDrivenGoapPlaybackProbe(pair.Key, "<active>", "tick-started agent=" + agentType.Name);
                    }
                }
                catch (Exception ex)
                {
                    LogReplicationHostDrivenGoapPlaybackProbe(pair.Key, "<active>", "tick-error " + ex.GetType().Name + " " + ex.Message);
                }
            }
        }
    }
}
