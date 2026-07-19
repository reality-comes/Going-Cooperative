using GoingCooperative.Core.Replication;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private sealed class ReplicationSnapshotValidationResult
        {
            public int Total;
            public int StableIds;
            public int Resolved;
            public int Missing;
        }

        private sealed class ReplicationAppliedView
        {
            public readonly UnityEngine.Object View;
            public readonly Transform Transform;
            public readonly Animator? Animator;

            public ReplicationAppliedView(UnityEngine.Object view, Transform transform, Animator? animator)
            {
                View = view;
                Transform = transform;
                Animator = animator;
            }
        }

        private sealed class ReplicationVisualLocomotionState
        {
            public long LastSequence = -1L;
            public bool HasTarget;
            public Vector3 LastTarget;
            public float MoveUntilRealtime;
        }

        private sealed class ReplicationAnimatorParameterSupport
        {
            public bool HasMoving;
            public AnimatorControllerParameterType MovingType;
            public bool HasRunning;
            public AnimatorControllerParameterType RunningType;
        }

        private const string ReplicationAnimatorMovingParameterName = "Moving";
        private const string ReplicationAnimatorRunningParameterName = "Running";
        private const float ReplicationVisualMovementThresholdSqr = 0.0001f;

        private static readonly int ReplicationAnimatorMovingParameterHash = Animator.StringToHash(ReplicationAnimatorMovingParameterName);
        private static readonly int ReplicationAnimatorRunningParameterHash = Animator.StringToHash(ReplicationAnimatorRunningParameterName);
        private static readonly Dictionary<string, ReplicationVisualLocomotionState> replicationVisualLocomotionByEntityId =
            new Dictionary<string, ReplicationVisualLocomotionState>(StringComparer.Ordinal);
        private static readonly Dictionary<int, ReplicationAnimatorParameterSupport> replicationAnimatorParameterSupportByInstanceId =
            new Dictionary<int, ReplicationAnimatorParameterSupport>();

        private const float ReplicationViewLookupCacheSeconds = 2f;
        private const float ReplicationViewLookupMissRefreshSeconds = 0.5f;

        private static Dictionary<string, ReplicationAppliedView>? replicationViewLookupCache;
        private static float replicationViewLookupCacheBuiltRealtime;

        // FindObjectsOfType + reflection per frame was a major client FPS cost. The lookup
        // is cached and refreshed on a short TTL, sooner when snapshot entities go missing
        // (spawn/despawn), and stale destroyed views are skipped by the existing null checks.
        private static Dictionary<string, ReplicationAppliedView> GetReplicationViewLookupCached(ReplicationTransformSnapshot snapshot)
        {
            var now = Time.realtimeSinceStartup;
            var cache = replicationViewLookupCache;
            if (cache != null && now - replicationViewLookupCacheBuiltRealtime <= ReplicationViewLookupCacheSeconds)
            {
                if (now - replicationViewLookupCacheBuiltRealtime <= ReplicationViewLookupMissRefreshSeconds)
                {
                    return cache;
                }

                var missing = false;
                for (var i = 0; i < snapshot.Entities.Count; i++)
                {
                    if (!cache.ContainsKey(snapshot.Entities[i].EntityId))
                    {
                        missing = true;
                        break;
                    }
                }

                if (!missing)
                {
                    return cache;
                }
            }

            cache = BuildReplicationViewLookup();
            replicationViewLookupCache = cache;
            replicationViewLookupCacheBuiltRealtime = now;
            return cache;
        }

        private static int ApplyReplicationTransformSnapshot(ReplicationTransformSnapshot snapshot)
        {
            var viewsByEntityId = GetReplicationViewLookupCached(snapshot);
            var applied = 0;
            var visuallyMoving = 0;
            var positionSample = new StringBuilder(256);
            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                var entity = snapshot.Entities[i];
                if (!viewsByEntityId.TryGetValue(entity.EntityId, out var view) || view == null || view.Transform == null)
                {
                    continue;
                }

                var transform = view.Transform;
                var before = transform.position;
                var target = new Vector3(entity.PositionX, entity.PositionY, entity.PositionZ);
                var rotation = new Quaternion(entity.RotationX, entity.RotationY, entity.RotationZ, entity.RotationW);
                AppendReplicationApplyPositionSample(positionSample, entity.EntityId, before, target);
                // Always write the view transform: it is the unconditional corrector that
                // keeps the client visually host-true even for pawns the local sim never
                // ticks. The UpdatePosition redirect (forceHostMovement) additionally
                // corrects the logical position whenever the sim moves a creature, which
                // is what keeps nametags/selection attached. Both write the same target.
                transform.position = target;
                transform.rotation = rotation;
                ApplyReplicationAuthoritativeAnimalTargetRotation(view, entity.Kind, rotation);
                var semanticMetadata = entity.Motion;
                var semanticMotion = replicationConfigSemanticAgentPresentation && semanticMetadata.HasValue;
                var semanticWorkActive = IsReplicationSemanticWorkPresentationActive(entity.EntityId);
                if (!semanticWorkActive
                    && (semanticMotion
                        ? ApplyReplicationSemanticLocomotion(entity.EntityId, view.Animator, semanticMetadata.GetValueOrDefault())
                        : UpdateReplicationVisualLocomotion(snapshot.Sequence, entity.EntityId, target, view.Animator)))
                {
                    visuallyMoving++;
                }

                if (replicationConfigSemanticAgentPresentation && !semanticMotion && !semanticWorkActive)
                {
                    ResetReplicationSemanticLocomotion(view.Animator);
                }

                applied++;
            }

            replicationLastApplyPositionSample = positionSample.Length == 0 ? "<none>" : positionSample.ToString();
            replicationLastApplyVisualMoving = visuallyMoving;
            return applied;
        }

        private static void AppendReplicationApplyPositionSample(StringBuilder builder, string entityId, Vector3 before, Vector3 target)
        {
            if (builder.Length >= 240)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(";");
            }

            builder.Append(TrimReplicationSampleText(entityId, 32))
                .Append(":")
                .Append(FormatReplicationPosition(before))
                .Append("->")
                .Append(FormatReplicationPosition(target));
        }

        private static ReplicationSnapshotValidationResult ValidateReplicationTransformSnapshot(ReplicationTransformSnapshot snapshot)
        {
            var result = new ReplicationSnapshotValidationResult
            {
                Total = snapshot.Entities.Count
            };

            var viewsByEntityId = BuildReplicationViewLookup();
            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                var entity = snapshot.Entities[i];
                if (IsReplicationStableEntityId(entity.EntityId))
                {
                    result.StableIds++;
                }

                if (viewsByEntityId.TryGetValue(entity.EntityId, out var view) && view != null && view.Transform != null)
                {
                    result.Resolved++;
                }
                else
                {
                    result.Missing++;
                }
            }

            return result;
        }

        private static Dictionary<string, ReplicationAppliedView> BuildReplicationViewLookup()
        {
            var lookup = new Dictionary<string, ReplicationAppliedView>(StringComparer.Ordinal);
            var views = FindReplicationAnimatedAgentViews();
            for (var i = 0; i < views.Length; i++)
            {
                AddReplicationViewToLookup(views[i], lookup);
            }

            return lookup;
        }

        private static void AddReplicationViewToLookup(UnityEngine.Object view, Dictionary<string, ReplicationAppliedView> lookup)
        {
            if (view == null
                || view is not MonoBehaviour behaviour
                || behaviour.gameObject == null
                || !behaviour.gameObject.activeInHierarchy
                || !TryClassifyReplicationView(view, out _))
            {
                return;
            }

            var transform = behaviour.transform;
            var appliedView = new ReplicationAppliedView(view, transform, TryGetReplicationViewAnimator(view, behaviour));
            if (TryGetReplicationViewEntityId(view, out var entityId) && !lookup.ContainsKey(entityId))
            {
                lookup[entityId] = appliedView;
                return;
            }

            var fallbackId = "view:" + (view.GetType().FullName ?? view.GetType().Name) + ":" + behaviour.gameObject.name;
            if (!lookup.ContainsKey(fallbackId))
            {
                lookup[fallbackId] = appliedView;
            }
        }

        private static void ApplyReplicationAuthoritativeAnimalTargetRotation(
            ReplicationAppliedView view,
            string kind,
            Quaternion rotation)
        {
            if (!replicationConfigSemanticAnimalPresentationV2
                || !string.Equals(kind, "animal", StringComparison.Ordinal)
                || view.View is not NSMedieval.View.AnimatedAgentView animatedView)
            {
                return;
            }

            // AnimatedAgentView.Update rotates the root toward TargetRotation every
            // frame.  Keep that native presentation target aligned with the same
            // interpolated host rotation we write to the root, otherwise the two
            // writers visibly tug animals back and forth between snapshots.
            animatedView.TargetRotation = rotation;
        }

        private static Animator? TryGetReplicationViewAnimator(UnityEngine.Object view, MonoBehaviour behaviour)
        {
            if (TryInvokeReplicationObjectMethod(view, "get_Animator", out var animatorObject)
                && animatorObject is Animator propertyAnimator)
            {
                return propertyAnimator;
            }

            try
            {
                return behaviour.GetComponentInChildren<Animator>();
            }
            catch
            {
                return null;
            }
        }

        private static bool UpdateReplicationVisualLocomotion(long sequence, string entityId, Vector3 target, Animator? animator)
        {
            if (!replicationConfigAnimateReplicatedMovement || animator == null)
            {
                return false;
            }

            if (!replicationVisualLocomotionByEntityId.TryGetValue(entityId, out var state))
            {
                state = new ReplicationVisualLocomotionState();
                replicationVisualLocomotionByEntityId[entityId] = state;
            }

            if (state.LastSequence != sequence)
            {
                var moved = state.HasTarget && (target - state.LastTarget).sqrMagnitude >= ReplicationVisualMovementThresholdSqr;
                state.LastSequence = sequence;
                state.LastTarget = target;
                state.HasTarget = true;
                state.MoveUntilRealtime = moved ? Time.realtimeSinceStartup + GetReplicationVisualMovementHoldSeconds() : 0f;
            }

            var moving = Time.realtimeSinceStartup <= state.MoveUntilRealtime;
            SetReplicationAnimatorLocomotion(animator, moving);
            return moving;
        }

        private static float GetReplicationVisualMovementHoldSeconds()
        {
            var snapshotHz = Math.Max(1, replicationConfigSnapshotHz);
            return Mathf.Clamp(2.5f / snapshotHz, 0.2f, 0.5f);
        }

        private static void SetReplicationAnimatorLocomotion(Animator animator, bool moving)
        {
            var support = GetReplicationAnimatorParameterSupport(animator);
            if (!support.HasMoving && !support.HasRunning)
            {
                return;
            }

            try
            {
                if (moving && !animator.enabled)
                {
                    animator.enabled = true;
                }

                if (support.HasMoving)
                {
                    SetReplicationAnimatorParameter(animator, ReplicationAnimatorMovingParameterHash, support.MovingType, moving);
                }

                if (support.HasRunning)
                {
                    SetReplicationAnimatorParameter(animator, ReplicationAnimatorRunningParameterHash, support.RunningType, false);
                }
            }
            catch
            {
            }
        }

        private static ReplicationAnimatorParameterSupport GetReplicationAnimatorParameterSupport(Animator animator)
        {
            var instanceId = animator.GetInstanceID();
            if (replicationAnimatorParameterSupportByInstanceId.TryGetValue(instanceId, out var support))
            {
                return support;
            }

            support = new ReplicationAnimatorParameterSupport();
            try
            {
                var parameters = animator.parameters;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (string.Equals(parameter.name, ReplicationAnimatorMovingParameterName, StringComparison.Ordinal))
                    {
                        support.HasMoving = true;
                        support.MovingType = parameter.type;
                    }
                    else if (string.Equals(parameter.name, ReplicationAnimatorRunningParameterName, StringComparison.Ordinal))
                    {
                        support.HasRunning = true;
                        support.RunningType = parameter.type;
                    }
                }
            }
            catch
            {
            }

            replicationAnimatorParameterSupportByInstanceId[instanceId] = support;
            return support;
        }

        private static void SetReplicationAnimatorParameter(Animator animator, int parameterHash, AnimatorControllerParameterType parameterType, bool enabled)
        {
            switch (parameterType)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(parameterHash, enabled);
                    break;
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(parameterHash, enabled ? 1f : 0f);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(parameterHash, enabled ? 1 : 0);
                    break;
            }
        }
    }
}
