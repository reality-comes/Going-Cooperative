using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static ReplicationTransformSnapshot CollectReplicationTransformSnapshot(long sequence, int maxEntities)
        {
            var entities = new List<ReplicationEntityTransform>(Math.Max(0, Math.Min(maxEntities, 256)));
            var stableCount = 0;
            var fallbackCount = 0;
            var fallbackSample = new StringBuilder(192);
            var positionSample = new StringBuilder(256);
            if (maxEntities <= 0)
            {
                UpdateReplicationCollectionSummary(0, 0, 0, string.Empty, string.Empty);
                return new ReplicationTransformSnapshot(sequence, Time.realtimeSinceStartup, entities);
            }

            try
            {
                var seen = new HashSet<int>();
                var seenEntityIds = new HashSet<string>(StringComparer.Ordinal);
                var views = FindReplicationAnimatedAgentViews();
                CollectReplicationViewTransforms(
                    views,
                    "worker",
                    maxEntities,
                    seen,
                    seenEntityIds,
                    entities,
                    ref stableCount,
                    ref fallbackCount,
                    fallbackSample,
                    positionSample);
                CollectReplicationViewTransforms(
                    views,
                    "animal",
                    maxEntities,
                    seen,
                    seenEntityIds,
                    entities,
                    ref stableCount,
                    ref fallbackCount,
                    fallbackSample,
                    positionSample);
            }
            catch
            {
                // Snapshot collection is best-effort. Runtime transport should survive discovery gaps.
            }

            UpdateReplicationCollectionSummary(entities.Count, stableCount, fallbackCount, fallbackSample.ToString(), positionSample.ToString());
            return new ReplicationTransformSnapshot(sequence, Time.realtimeSinceStartup, entities);
        }

        private static void CollectReplicationViewTransforms(
            UnityEngine.Object[] views,
            string wantedKind,
            int maxEntities,
            HashSet<int> seen,
            HashSet<string> seenEntityIds,
            List<ReplicationEntityTransform> entities,
            ref int stableCount,
            ref int fallbackCount,
            StringBuilder fallbackSample,
            StringBuilder positionSample)
        {
            for (var i = 0; i < views.Length && entities.Count < maxEntities; i++)
            {
                CollectReplicationViewTransform(
                    views[i],
                    wantedKind,
                    maxEntities,
                    seen,
                    seenEntityIds,
                    entities,
                    ref stableCount,
                    ref fallbackCount,
                    fallbackSample,
                    positionSample);
            }
        }

        private static void CollectReplicationViewTransform(
            UnityEngine.Object view,
            string wantedKind,
            int maxEntities,
            HashSet<int> seen,
            HashSet<string> seenEntityIds,
            List<ReplicationEntityTransform> entities,
            ref int stableCount,
            ref int fallbackCount,
            StringBuilder fallbackSample,
            StringBuilder positionSample)
        {
            if (entities.Count >= maxEntities
                || view == null
                || view is not MonoBehaviour behaviour
                || behaviour.gameObject == null
                || !behaviour.gameObject.activeInHierarchy)
            {
                return;
            }

            var gameObject = behaviour.gameObject;
            if (!TryClassifyReplicationView(view, out var kind)
                || !string.Equals(kind, wantedKind, StringComparison.Ordinal))
            {
                return;
            }

            if (!seen.Add(gameObject.GetInstanceID()))
            {
                return;
            }

            var hasStableEntityId = TryGetReplicationViewEntityId(view, seenEntityIds, out var entityId);
            if (hasStableEntityId)
            {
                stableCount++;
            }
            else
            {
                entityId = "view:" + (view.GetType().FullName ?? view.GetType().Name) + ":" + gameObject.name;
                if (!seenEntityIds.Add(entityId))
                {
                    return;
                }

                fallbackCount++;
                AppendReplicationFallbackSample(fallbackSample, kind, view, gameObject);
            }

            var transform = behaviour.transform;
            var position = transform.position;
            var rotation = transform.rotation;
            ReplicationEntityMotionMetadata? motion = null;
            if (replicationConfigSemanticAgentPresentation
                && hasStableEntityId
                && TryCollectReplicationSemanticMotionMetadata(view, behaviour, entityId, kind, out var capturedMotion))
            {
                motion = capturedMotion;
            }

            AppendReplicationPositionSample(positionSample, kind, entityId, position);
            entities.Add(new ReplicationEntityTransform(
                entityId,
                kind,
                position.x,
                position.y,
                position.z,
                rotation.x,
                rotation.y,
                rotation.z,
                rotation.w,
                motion));
        }

        private static void UpdateReplicationCollectionSummary(int entities, int stableIds, int fallbackIds, string fallbackSample, string positionSample)
        {
            replicationLastCollectedEntities = entities;
            replicationLastCollectedStableIds = stableIds;
            replicationLastCollectedFallbackIds = fallbackIds;
            replicationLastCollectedFallbackSample = string.IsNullOrEmpty(fallbackSample) ? "<none>" : fallbackSample;
            replicationLastCollectedPositionSample = string.IsNullOrEmpty(positionSample) ? "<none>" : positionSample;
        }

        private static void AppendReplicationFallbackSample(StringBuilder builder, string kind, object view, GameObject gameObject)
        {
            if (builder.Length >= 180)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(";");
            }

            builder.Append(kind)
                .Append(":")
                .Append(view.GetType().Name)
                .Append(":")
                .Append(gameObject.name);
        }

        private static void AppendReplicationPositionSample(StringBuilder builder, string kind, string entityId, Vector3 position)
        {
            if (builder.Length >= 240)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(";");
            }

            builder.Append(kind)
                .Append(":")
                .Append(TrimReplicationSampleText(entityId, 32))
                .Append("@")
                .Append(FormatReplicationPosition(position));
        }

        private static string FormatReplicationPosition(Vector3 position)
        {
            return "("
                + position.x.ToString("F2", CultureInfo.InvariantCulture)
                + ","
                + position.y.ToString("F2", CultureInfo.InvariantCulture)
                + ","
                + position.z.ToString("F2", CultureInfo.InvariantCulture)
                + ")";
        }

        private static string TrimReplicationSampleText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private static Type? replicationAnimatedAgentViewType;
        private static UnityEngine.Object[] replicationSemanticAnimatedAgentViewCache = Array.Empty<UnityEngine.Object>();
        private static float replicationSemanticAnimatedAgentViewCacheRealtime = -10f;
        private const float ReplicationSemanticAnimatedAgentViewCacheSeconds = 0.75f;

        private static UnityEngine.Object[] FindReplicationAnimatedAgentViews()
        {
            var type = replicationAnimatedAgentViewType ??= AccessTools.TypeByName("NSMedieval.View.AnimatedAgentView");
            if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                return Array.Empty<UnityEngine.Object>();
            }

            if (!replicationConfigSemanticAgentPresentation)
            {
                return UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
            }

            var now = Time.realtimeSinceStartup;
            if (now - replicationSemanticAnimatedAgentViewCacheRealtime < ReplicationSemanticAnimatedAgentViewCacheSeconds)
            {
                return replicationSemanticAnimatedAgentViewCache;
            }

            replicationSemanticAnimatedAgentViewCache = UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
            replicationSemanticAnimatedAgentViewCacheRealtime = now;
            return replicationSemanticAnimatedAgentViewCache;
        }

        private static bool TryClassifyReplicationView(object view, out string kind)
        {
            var type = view.GetType();
            while (type != null && type != typeof(object))
            {
                var fullName = type.FullName ?? type.Name;
                if (string.Equals(fullName, "NSMedieval.View.WorkerView", StringComparison.Ordinal))
                {
                    kind = "worker";
                    return true;
                }

                if (string.Equals(fullName, "NSMedieval.View.Animals.AnimalView", StringComparison.Ordinal))
                {
                    kind = "animal";
                    return true;
                }

                type = type.BaseType;
            }

            kind = string.Empty;
            return false;
        }

        private static bool TryGetReplicationViewEntityId(object view, out string entityId)
        {
            return TryGetReplicationViewEntityId(view, null, out entityId);
        }

        private static bool TryGetReplicationViewEntityId(object view, HashSet<string>? seenEntityIds, out string entityId)
        {
            if (TryInvokeReplicationObjectMethod(view, "GetAgentOwner", out var agentOwner)
                && agentOwner != null
                && TryGetReplicationStableEntityId(agentOwner, out var ownerEntityId)
                && TryUseReplicationEntityId(ownerEntityId, seenEntityIds, out entityId))
            {
                return true;
            }

            if (TryInvokeReplicationObjectMethod(view, "GetAgent", out var agent)
                && agent != null
                && TryGetReplicationStableEntityId(agent, out var agentEntityId)
                && TryUseReplicationEntityId(agentEntityId, seenEntityIds, out entityId))
            {
                return true;
            }

            if (TryInvokeReplicationStringMethod(view, "GetAnimatedAgentDataId", out var animatedAgentDataId)
                && IsUsableReplicationIdToken(animatedAgentDataId)
                && TryUseReplicationEntityId("data:" + animatedAgentDataId, seenEntityIds, out entityId))
            {
                return true;
            }

            if (TryInvokeReplicationStringMethod(view, "GetGoapAgentId", out var goapAgentId)
                && IsUsableReplicationIdToken(goapAgentId)
                && TryUseReplicationEntityId("goap:" + goapAgentId, seenEntityIds, out entityId))
            {
                return true;
            }

            entityId = string.Empty;
            return false;
        }

        private static bool TryUseReplicationEntityId(string candidate, HashSet<string>? seenEntityIds, out string entityId)
        {
            entityId = string.Empty;
            if (seenEntityIds != null && !seenEntityIds.Add(candidate))
            {
                return false;
            }

            entityId = candidate;
            return true;
        }

        private static bool IsUsableReplicationIdToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value, "0", StringComparison.Ordinal)
                || string.Equals(value, "worker", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "animal", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsDigit(value[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryInvokeReplicationObjectMethod(object owner, string methodName, out object? value)
        {
            value = null;
            try
            {
                var method = owner.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null || method.GetParameters().Length != 0)
                {
                    return false;
                }

                value = method.Invoke(owner, Array.Empty<object>());
                return value != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvokeReplicationStringMethod(object owner, string methodName, out string value)
        {
            value = string.Empty;
            try
            {
                var method = owner.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null || method.GetParameters().Length != 0)
                {
                    return false;
                }

                var result = method.Invoke(owner, Array.Empty<object>());
                if (result == null)
                {
                    return false;
                }

                value = Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                return value.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsReplicationStableEntityId(string entityId)
        {
            return entityId.StartsWith("goap:", StringComparison.Ordinal)
                || entityId.StartsWith("data:", StringComparison.Ordinal)
                || entityId.StartsWith("uid:", StringComparison.Ordinal);
        }

        private static bool TryGetReplicationStableEntityId(MonoBehaviour behaviour, out string entityId)
        {
            return TryGetReplicationStableEntityId((object)behaviour, out entityId);
        }

        private static bool TryGetReplicationStableEntityId(GameObject gameObject, out string entityId)
        {
            if (gameObject == null)
            {
                entityId = string.Empty;
                return false;
            }

            var components = gameObject.GetComponents<MonoBehaviour>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component != null && TryGetReplicationStableEntityId(component, out entityId))
                {
                    return true;
                }
            }

            entityId = string.Empty;
            return false;
        }

        private static bool TryGetReplicationStableEntityId(object owner, out string entityId)
        {
            var visited = new HashSet<int>();
            return TryGetReplicationStableEntityId(owner, 0, visited, out entityId);
        }

        private static bool TryGetReplicationStableEntityId(object owner, int depth, HashSet<int> visited, out string entityId)
        {
            if (owner == null || depth > 2)
            {
                entityId = string.Empty;
                return false;
            }

            var identityKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner);
            if (!visited.Add(identityKey))
            {
                entityId = string.Empty;
                return false;
            }

            if (TryReadSimpleReplicationIdentity(owner, out entityId))
            {
                return true;
            }

            for (var i = 0; i < ReplicationIdentityOwnerMemberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(owner, ReplicationIdentityOwnerMemberNames[i], out var member)
                    && member != null
                    && TryGetReplicationStableEntityId(member, depth + 1, visited, out entityId))
                {
                    return true;
                }
            }

            entityId = string.Empty;
            return false;
        }

        private static bool TryGetReplicationTransform(object owner, out Transform? transform)
        {
            transform = null;
            if (owner == null)
            {
                return false;
            }

            if (owner is Transform directTransform)
            {
                transform = directTransform;
                return true;
            }

            if (owner is Component component)
            {
                transform = component.transform;
                return transform != null;
            }

            if (owner is GameObject gameObject)
            {
                transform = gameObject.transform;
                return transform != null;
            }

            var visited = new HashSet<int>();
            return TryGetReplicationTransform(owner, 4, visited, out transform);
        }

        private static bool TryGetReplicationTransform(object owner, int depth, HashSet<int> visited, out Transform? transform)
        {
            transform = null;
            if (owner == null || depth < 0)
            {
                return false;
            }

            var type = owner.GetType();
            if (type.IsPrimitive || owner is string || owner is decimal)
            {
                return false;
            }

            if (owner is Transform directTransform)
            {
                transform = directTransform;
                return true;
            }

            if (owner is Component component)
            {
                transform = component.transform;
                return transform != null;
            }

            if (owner is GameObject gameObject)
            {
                transform = gameObject.transform;
                return transform != null;
            }

            if (!type.IsValueType)
            {
                var identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner);
                if (!visited.Add(identity))
                {
                    return false;
                }
            }

            for (var i = 0; i < ReplicationTransformMemberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(owner, ReplicationTransformMemberNames[i], out var member)
                    && member != null
                    && TryGetReplicationTransform(member, depth - 1, visited, out transform))
                {
                    return true;
                }
            }

            try
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Array.Sort(fields, (left, right) => string.CompareOrdinal(left.Name, right.Name));
                var inspected = 0;
                for (var i = 0; i < fields.Length && inspected < 32; i++)
                {
                    var field = fields[i];
                    if (field.IsStatic || !IsActorPathMemberName(field.Name))
                    {
                        continue;
                    }

                    inspected++;
                    object? fieldValue;
                    try
                    {
                        fieldValue = field.GetValue(owner);
                    }
                    catch
                    {
                        continue;
                    }

                    if (fieldValue != null && TryGetReplicationTransform(fieldValue, depth - 1, visited, out transform))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static readonly string[] ReplicationTransformMemberNames =
        {
            "transform",
            "Transform",
            "gameObject",
            "GameObject",
            "Worker",
            "worker",
            "Humanoid",
            "humanoid",
            "Animal",
            "animal",
            "Owner",
            "owner",
            "AgentOwner",
            "agentOwner",
            "View",
            "view",
            "WorkerView",
            "workerView",
            "Controller",
            "controller",
            "Driver",
            "driver",
            "Actor",
            "actor",
            "Model",
            "model"
        };

        private static readonly string[] ReplicationIdentityOwnerMemberNames =
        {
            "Worker",
            "worker",
            "Humanoid",
            "humanoid",
            "Animal",
            "animal",
            "AnimalInstance",
            "animalInstance",
            "Creature",
            "creature",
            "CreatureInstance",
            "creatureInstance",
            "Owner",
            "owner",
            "AgentOwner",
            "agentOwner",
            "Agent",
            "agent",
            "Instance",
            "instance",
            "Data",
            "data"
        };

        private static bool TryReadSimpleReplicationIdentity(object owner, out string entityId)
        {
            var memberNames = new[] { "UniqueId", "uniqueId" };
            for (var i = 0; i < memberNames.Length; i++)
            {
                if (TryReadInstanceMemberValue(owner, memberNames[i], out var value)
                    && value != null
                    && TryFormatSimpleValue(value, value.GetType(), out var simple)
                    && !string.IsNullOrWhiteSpace(simple)
                    && (!long.TryParse(simple, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId)
                        || numericId != 0L))
                {
                    entityId = "uid:" + simple;
                    return true;
                }
            }

            entityId = string.Empty;
            return false;
        }
    }
}
