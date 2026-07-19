using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        // Data-layer movement authority. Instead of suppressing UpdatePosition writes and
        // forcing the view transform from snapshots (which detaches everything anchored to
        // the creature's logical position: nametags, selection, hitboxes), this redirects
        // the position argument of CreatureBase.UpdatePosition to the freshest host
        // position and lets the game's own code propagate it. One write point; the view,
        // FloatingOverlaySystem elements, and client GOAP all see host-true positions.
        private sealed class ReplicationHostMovementTarget
        {
            public Vector3 Position;
            public float ReceivedRealtime;
        }

        private const float ReplicationHostMovementStaleSeconds = 2f;

        private static readonly Dictionary<string, ReplicationHostMovementTarget> replicationHostMovementTargets =
            new Dictionary<string, ReplicationHostMovementTarget>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<object, string> replicationHostMovementEntityIdCache =
            new ConditionalWeakTable<object, string>();
        private static long replicationHostMovementOverrides;
        private static long replicationHostMovementPassthroughs;
        private static bool replicationHostMovementActiveLogged;

        private void TryInstallReplicationHostMovementAuthority(Harmony harmonyInstance)
        {
            TryLoadReplicationConfig(this);
            // The multiplayer UI assigns enabled/role/applySnapshots after plugin
            // startup. Install from the static feature gate alone; the prefix checks
            // the live session state and therefore remains inert on the host and while
            // disconnected, then activates as soon as this process becomes the client.
            if (!replicationConfigForceHostMovement)
            {
                return;
            }

            var prefix = new HarmonyMethod(typeof(GoingCooperativePlugin).GetMethod(
                nameof(ReplicationHostMovementPrefix),
                BindingFlags.Static | BindingFlags.NonPublic));
            var patched = 0;
            patched += TryPatchReplicationHostMovementMethod(harmonyInstance, prefix, "NSMedieval.State.CreatureBase");
            patched += TryPatchReplicationHostMovementMethod(harmonyInstance, prefix, "NSMedieval.State.HumanoidInstance");
            patched += TryPatchReplicationHostMovementMethod(harmonyInstance, prefix, "NSMedieval.State.HumanoidBehaviour");
            LogReplicationInfo("Going Cooperative replication host movement authority patches="
                + patched.ToString(CultureInfo.InvariantCulture));
        }

        private int TryPatchReplicationHostMovementMethod(Harmony harmonyInstance, HarmonyMethod prefix, string typeName)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    AppendPluginLog("Replication host movement type missing: " + typeName);
                    return 0;
                }

                var patched = 0;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!string.Equals(method.Name, "UpdatePosition", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(Vector3))
                    {
                        AppendPluginLog("Replication host movement skipped (signature) "
                            + typeName + ".UpdatePosition params=" + parameters.Length.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    harmonyInstance.Patch(method, prefix: prefix);
                    AppendPluginLog("Replication host movement patched: " + type.FullName + ".UpdatePosition");
                    patched++;
                }

                return patched;
            }
            catch (Exception ex)
            {
                AppendPluginLog("Replication host movement patch failed: "
                    + typeName + " " + ex.GetType().Name + " " + ex.Message);
                return 0;
            }
        }

        private static bool ReplicationHostMovementPrefix(object __instance, ref Vector3 __0)
        {
            if (!replicationConfigEnabled
                || replicationConfigHostMode
                || !replicationConfigApplySnapshots
                || !replicationConfigForceHostMovement
                || __instance == null)
            {
                return true;
            }

            var requestedPosition = __0;
            if (!TryGetReplicationHostMovementEntityId(__instance, out var entityId)
                || !replicationHostMovementTargets.TryGetValue(entityId, out var target)
                || Time.realtimeSinceStartup - target.ReceivedRealtime > ReplicationHostMovementStaleSeconds)
            {
                replicationHostMovementPassthroughs++;
                RecordReplicationGoapActionProbeMovement(entityId, requestedPosition, requestedPosition, "passthrough");
                return true;
            }

            __0 = target.Position;
            replicationHostMovementOverrides++;
            RecordReplicationGoapActionProbeMovement(entityId, requestedPosition, target.Position, "override");
            LogReplicationHostMovementActiveOnce();
            return true;
        }

        private static bool TryGetReplicationHostMovementEntityId(object instance, out string entityId)
        {
            if (replicationHostMovementEntityIdCache.TryGetValue(instance, out var cached))
            {
                entityId = cached;
                return cached.Length != 0;
            }

            var resolved = TryGetReplicationStableEntityId(instance, out var id) ? id : string.Empty;
            replicationHostMovementEntityIdCache.Add(instance, resolved);
            entityId = resolved;
            return resolved.Length != 0;
        }

        private static void UpdateReplicationHostMovementTargets(ReplicationTransformSnapshot snapshot)
        {
            if (!replicationConfigForceHostMovement || replicationConfigHostMode)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                var entity = snapshot.Entities[i];
                if (!replicationHostMovementTargets.TryGetValue(entity.EntityId, out var target))
                {
                    target = new ReplicationHostMovementTarget();
                    replicationHostMovementTargets[entity.EntityId] = target;
                }

                target.Position = new Vector3(entity.PositionX, entity.PositionY, entity.PositionZ);
                target.ReceivedRealtime = now;
            }
        }

        private static void LogReplicationHostMovementActiveOnce()
        {
            if (replicationHostMovementActiveLogged)
            {
                return;
            }

            replicationHostMovementActiveLogged = true;
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            current.AppendPluginLog("Replication host movement authority active: UpdatePosition redirected to host targets (data layer).");
        }

        private static string FormatReplicationHostMovementCounters()
        {
            return "hostMoveOverrides=" + replicationHostMovementOverrides.ToString(CultureInfo.InvariantCulture)
                + " hostMovePassthroughs=" + replicationHostMovementPassthroughs.ToString(CultureInfo.InvariantCulture);
        }
    }
}
