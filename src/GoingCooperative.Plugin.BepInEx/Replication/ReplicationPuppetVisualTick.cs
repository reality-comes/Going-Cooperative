using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Dictionary<Type, MethodInfo?> ReplicationPuppetVisualLateTickMethodsByType =
            new Dictionary<Type, MethodInfo?>();
        private static readonly HashSet<string> ReplicationPuppetVisualTickStartedEntityIds =
            new HashSet<string>(StringComparer.Ordinal);

        // This is presentation-only. It advances a view that the host has already
        // placed into an active puppet action; it never ticks an agent or manager.
        private static void TickReplicationPuppetActionVisuals()
        {
            if (!replicationConfigPuppetVisualTick
                || replicationConfigHostMode
                || !replicationConfigApplySnapshots)
            {
                return;
            }

            var deltaTime = Time.deltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            List<string> activeEntityIds;
            lock (ReplicationWorldObjectDeltaLock)
            {
                activeEntityIds = new List<string>(ReplicationPuppetActionStateByEntityId.Count);
                foreach (var pair in ReplicationPuppetActionStateByEntityId)
                {
                    if (Time.realtimeSinceStartup <= pair.Value.ExpiresRealtime)
                    {
                        activeEntityIds.Add(pair.Key);
                    }
                }
            }

            for (var i = 0; i < activeEntityIds.Count; i++)
            {
                var entityId = activeEntityIds[i];
                if (!TryFindReplicationAnimatedAgentViewByEntityId(entityId, out var view, out var viewDetail) || view == null)
                {
                    LogReplicationPuppetVisualTickProbe(entityId, "view-missing " + viewDetail);
                    continue;
                }

                var viewType = view.GetType();
                if (!ReplicationPuppetVisualLateTickMethodsByType.TryGetValue(viewType, out var lateTick))
                {
                    lateTick = FindReplicationInstanceMethod(viewType, "OnLateTick", new[] { typeof(float) });
                    ReplicationPuppetVisualLateTickMethodsByType[viewType] = lateTick;
                }

                if (lateTick == null)
                {
                    LogReplicationPuppetVisualTickProbe(entityId, "on-late-tick-missing view=" + viewType.Name);
                    continue;
                }

                applyingRuntimeCommandDepth++;
                try
                {
                    lateTick.Invoke(view, new object[] { deltaTime });
                    if (ReplicationPuppetVisualTickStartedEntityIds.Add(entityId))
                    {
                        LogReplicationPuppetVisualTickProbe(entityId, "on-late-tick-started view=" + viewType.Name + " " + viewDetail);
                    }
                }
                catch (Exception ex)
                {
                    LogReplicationPuppetVisualTickProbe(entityId, "on-late-tick-error " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    applyingRuntimeCommandDepth--;
                }
            }
        }

        private static void LogReplicationPuppetVisualTickProbe(string entityId, string detail)
        {
            if (!ShouldLogReplicationGoapActionProbe(entityId))
            {
                return;
            }

            LogReplicationGoapActionProbe(entityId, "puppet-visual-tick", detail);
        }
    }
}
