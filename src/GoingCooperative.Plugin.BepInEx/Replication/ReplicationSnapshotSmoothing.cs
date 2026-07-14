using System;
using System.Collections.Generic;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private readonly struct ReplicationPresentationSample
        {
            public ReplicationPresentationSample(
                long sequence,
                float hostRealtime,
                Vector3 position,
                Quaternion rotation,
                ReplicationEntityMotionMetadata? motion)
            {
                Sequence = sequence;
                HostRealtime = hostRealtime;
                Position = position;
                Rotation = rotation;
                Motion = motion;
            }

            public long Sequence { get; }
            public float HostRealtime { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public ReplicationEntityMotionMetadata? Motion { get; }
        }

        private sealed class ReplicationPresentationTrack
        {
            public readonly List<ReplicationPresentationSample> Samples = new List<ReplicationPresentationSample>(8);
            public string Kind = string.Empty;
            public float LastReceivedRealtime;
            public bool Discontinuity;
        }

        private sealed class ReplicationSmoothAnimatorSupport
        {
            public int MovingHash;
            public AnimatorControllerParameterType MovingType;
            public int RunningHash;
            public AnimatorControllerParameterType RunningType;
            public int SprintingHash;
            public AnimatorControllerParameterType SprintingType;
            public int SwimmingHash;
            public AnimatorControllerParameterType SwimmingType;
            public int ClimbDirectionHash;
            public AnimatorControllerParameterType ClimbDirectionType;
            public int SpeedHash;
            public AnimatorControllerParameterType SpeedType;
        }

        private sealed class ReplicationSemanticAnimalPresentationState
        {
            public bool Moving;
            public bool Running;
            public bool Sprinting;
            public float MovingHoldUntilRealtime;
            public float RunningHoldUntilRealtime;
            public float LastMovementSpeed;
            public float LastAnimatorSpeed = 1f;
            public bool FacingInitialized;
            public Vector3 StableFacingDirection;
            public bool PendingFacingInitialized;
            public Vector3 PendingFacingDirection;
            public float PendingFacingSinceRealtime;
        }

        private const int ReplicationPresentationMaxSamples = 24;
        private const float ReplicationPresentationTrackLifetimeSeconds = 3f;
        private const float ReplicationPresentationMaxExtrapolationSeconds = 0.2f;
        private const float ReplicationPresentationMaxSegmentSeconds = 0.5f;
        private const float ReplicationPresentationWorkerTeleportDistance = 3f;
        private const float ReplicationPresentationAnimalTeleportDistance = 6f;
        private const float ReplicationPresentationMovingSpeed = 0.08f;
        private const float ReplicationPresentationSemanticCurveStrength = 0.7f;
        private const float ReplicationPresentationSemanticCurveMaximumMeters = 0.35f;
        private const float ReplicationPresentationAnimalMoveStartSpeed = 0.08f;
        private const float ReplicationPresentationAnimalMoveStopSpeed = 0.035f;
        private const float ReplicationPresentationAnimalMoveHoldSeconds = 0.16f;
        private const float ReplicationPresentationAnimalRunStartSpeed = 1.2f;
        private const float ReplicationPresentationAnimalRunStopSpeed = 0.9f;
        private const float ReplicationPresentationAnimalRunHoldSeconds = 0.18f;
        private const float ReplicationPresentationAnimalFacingDeadbandDot = 0.9925f;
        private const float ReplicationPresentationAnimalFacingPendingDot = 0.978f;
        private const float ReplicationPresentationAnimalFacingCommitSeconds = 0.06f;

        private static readonly Dictionary<string, ReplicationPresentationTrack> ReplicationPresentationTracks =
            new Dictionary<string, ReplicationPresentationTrack>(StringComparer.Ordinal);
        private static readonly Dictionary<int, ReplicationSmoothAnimatorSupport> ReplicationSmoothAnimatorSupportByInstanceId =
            new Dictionary<int, ReplicationSmoothAnimatorSupport>();
        private static readonly Dictionary<string, ReplicationSemanticAnimalPresentationState> ReplicationSemanticAnimalPresentationByEntityId =
            new Dictionary<string, ReplicationSemanticAnimalPresentationState>(StringComparer.Ordinal);
        private static float replicationNextPresentationPruneRealtime;
        private static bool replicationPresentationClockOffsetInitialized;
        private static float replicationPresentationClockOffset;
        private static long replicationPresentationInterpolatedFrames;
        private static long replicationPresentationExtrapolatedFrames;
        private static long replicationPresentationHeldFrames;
        private static long replicationPresentationDiscontinuities;
        private static long replicationPresentationSemanticInterpolatedFrames;
        private static long replicationPresentationAnimalMotionHeldFrames;
        private static long replicationPresentationAnimalFacingHeldFrames;

        private static void BufferReplicationTransformSnapshot(ReplicationTransformSnapshot snapshot, float receivedRealtime)
        {
            var observedClockOffset = receivedRealtime - snapshot.SentRealtime;
            if (!replicationPresentationClockOffsetInitialized)
            {
                replicationPresentationClockOffset = observedClockOffset;
                replicationPresentationClockOffsetInitialized = true;
            }
            else if (observedClockOffset < replicationPresentationClockOffset)
            {
                // The lowest observed offset is the best available estimate of the two
                // Unity clocks without transient queueing delay folded into it.
                replicationPresentationClockOffset = Mathf.Lerp(replicationPresentationClockOffset, observedClockOffset, 0.25f);
            }

            for (var i = 0; i < snapshot.Entities.Count; i++)
            {
                var entity = snapshot.Entities[i];
                if (!ReplicationPresentationTracks.TryGetValue(entity.EntityId, out var track))
                {
                    track = new ReplicationPresentationTrack { Kind = entity.Kind };
                    ReplicationPresentationTracks[entity.EntityId] = track;
                }

                if (replicationConfigSemanticAgentPresentation
                    && string.Equals(entity.Kind, "animal", StringComparison.Ordinal)
                    && !ReplicationSemanticAnimalPresentationByEntityId.ContainsKey(entity.EntityId))
                {
                    ReplicationSemanticAnimalPresentationByEntityId[entity.EntityId] =
                        new ReplicationSemanticAnimalPresentationState();
                }

                var samples = track.Samples;
                if (samples.Count > 0 && snapshot.Sequence <= samples[samples.Count - 1].Sequence)
                {
                    continue;
                }

                var position = new Vector3(entity.PositionX, entity.PositionY, entity.PositionZ);
                var rotation = NormalizeReplicationPresentationRotation(new Quaternion(
                    entity.RotationX,
                    entity.RotationY,
                    entity.RotationZ,
                    entity.RotationW));
                if (samples.Count > 0)
                {
                    var previous = samples[samples.Count - 1];
                    var teleportDistance = string.Equals(entity.Kind, "animal", StringComparison.Ordinal)
                        ? ReplicationPresentationAnimalTeleportDistance
                        : ReplicationPresentationWorkerTeleportDistance;
                    if ((position - previous.Position).sqrMagnitude >= teleportDistance * teleportDistance)
                    {
                        samples.Clear();
                        ClearReplicationSemanticAgentMotionGuidance(entity.EntityId);
                        ReplicationSemanticAnimalPresentationByEntityId.Remove(entity.EntityId);
                        if (replicationConfigSemanticAgentPresentation
                            && string.Equals(entity.Kind, "animal", StringComparison.Ordinal))
                        {
                            ReplicationSemanticAnimalPresentationByEntityId[entity.EntityId] =
                                new ReplicationSemanticAnimalPresentationState();
                        }
                        track.Discontinuity = true;
                        replicationPresentationDiscontinuities++;
                    }
                }

                samples.Add(new ReplicationPresentationSample(
                    snapshot.Sequence,
                    snapshot.SentRealtime,
                    position,
                    rotation,
                    entity.Motion));
                ObserveReplicationSemanticAgentMotionSnapshot(entity.EntityId, entity.Motion);
                while (samples.Count > ReplicationPresentationMaxSamples)
                {
                    samples.RemoveAt(0);
                }

                track.Kind = entity.Kind;
                track.LastReceivedRealtime = receivedRealtime;
            }
        }

        private static int ApplyBufferedReplicationTransformSnapshot(ReplicationTransformSnapshot latestSnapshot)
        {
            var now = Time.realtimeSinceStartup;
            var renderHostRealtime = now
                - replicationPresentationClockOffset
                - Mathf.Max(0f, replicationConfigInterpolationMs / 1000f);
            var viewsByEntityId = GetReplicationViewLookupCached(latestSnapshot);
            var applied = 0;
            var moving = 0;

            foreach (var pair in ReplicationPresentationTracks)
            {
                var track = pair.Value;
                if (track.Samples.Count == 0
                    || !viewsByEntityId.TryGetValue(pair.Key, out var view)
                    || view == null
                    || view.Transform == null)
                {
                    continue;
                }

                EvaluateReplicationPresentationTrack(
                    pair.Key,
                    track,
                    renderHostRealtime,
                    now,
                    out var position,
                    out var rotation,
                    out var speed,
                    out var motion);
                view.Transform.position = position;
                view.Transform.rotation = rotation;
                if (UpdateReplicationSmoothLocomotion(pair.Key, view.Animator, track.Kind, speed, motion))
                {
                    moving++;
                }

                applied++;
            }

            replicationLastApplyVisualMoving = moving;
            PruneReplicationPresentationTracksIfDue(now);
            return applied;
        }

        private static void EvaluateReplicationPresentationTrack(
            string entityId,
            ReplicationPresentationTrack track,
            float renderHostRealtime,
            float now,
            out Vector3 position,
            out Quaternion rotation,
            out float speed,
            out ReplicationEntityMotionMetadata? motion)
        {
            var samples = track.Samples;
            var newest = samples[samples.Count - 1];
            if (track.Discontinuity || samples.Count == 1)
            {
                track.Discontinuity = false;
                position = newest.Position;
                rotation = newest.Rotation;
                motion = newest.Motion;
                speed = motion.HasValue ? motion.Value.MovementSpeed : 0f;
                replicationPresentationHeldFrames++;
                return;
            }

            var upperIndex = -1;
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i].HostRealtime >= renderHostRealtime)
                {
                    upperIndex = i;
                    break;
                }
            }

            if (upperIndex > 0)
            {
                var from = samples[upperIndex - 1];
                var to = samples[upperIndex];
                var duration = Mathf.Max(0.0001f, to.HostRealtime - from.HostRealtime);
                var t = Mathf.Clamp01((renderHostRealtime - from.HostRealtime) / duration);
                position = Vector3.LerpUnclamped(from.Position, to.Position, t);
                rotation = Quaternion.SlerpUnclamped(from.Rotation, to.Rotation, t);
                if (replicationConfigSemanticAgentPresentation)
                {
                    TryApplyReplicationSemanticNormalInterpolation(
                        entityId,
                        track.Kind,
                        from,
                        to,
                        t,
                        now,
                        ref position,
                        ref rotation);
                }
                motion = to.Motion;
                speed = motion.HasValue
                    ? motion.Value.MovementSpeed
                    : replicationConfigSemanticAgentPresentation
                        ? 0f
                        : GetReplicationPresentationSpeed(from, to, duration);
                replicationPresentationInterpolatedFrames++;
                PruneReplicationPresentationSamples(track, upperIndex - 1);
                return;
            }

            var oldest = samples[0];
            if (renderHostRealtime <= oldest.HostRealtime)
            {
                position = oldest.Position;
                rotation = oldest.Rotation;
                motion = oldest.Motion;
                speed = motion.HasValue ? motion.Value.MovementSpeed : 0f;
                replicationPresentationHeldFrames++;
                return;
            }

            var previous = samples[samples.Count - 2];
            var segmentSeconds = newest.HostRealtime - previous.HostRealtime;
            var packetAge = now - track.LastReceivedRealtime;
            if (segmentSeconds > 0.0001f
                && segmentSeconds <= ReplicationPresentationMaxSegmentSeconds
                && packetAge <= ReplicationPresentationMaxSegmentSeconds)
            {
                motion = newest.Motion;
                var velocity = motion.HasValue
                    ? new Vector3(
                        motion.Value.VelocityX,
                        motion.Value.VelocityY,
                        motion.Value.VelocityZ)
                    : (newest.Position - previous.Position) / segmentSeconds;
                var extrapolation = Mathf.Clamp(renderHostRealtime - newest.HostRealtime, 0f, ReplicationPresentationMaxExtrapolationSeconds);
                position = newest.Position + velocity * extrapolation;
                rotation = newest.Rotation;
                if (TryGuideReplicationSemanticAgentMotion(
                        entityId,
                        newest.Position,
                        velocity,
                        extrapolation,
                        out var guidedPosition,
                        out var guidedFacing))
                {
                    position = guidedPosition;
                    var horizontalFacing = new Vector3(guidedFacing.x, 0f, guidedFacing.z);
                    if (horizontalFacing.sqrMagnitude > 0.0001f)
                    {
                        var targetRotation = Quaternion.LookRotation(horizontalFacing.normalized, Vector3.up);
                        rotation = Quaternion.Slerp(newest.Rotation, targetRotation, Mathf.Clamp01(extrapolation * 8f));
                    }
                }
                speed = motion.HasValue
                    ? motion.Value.MovementSpeed
                    : new Vector2(velocity.x, velocity.z).magnitude;
                replicationPresentationExtrapolatedFrames++;
                return;
            }

            position = newest.Position;
            rotation = newest.Rotation;
            motion = newest.Motion;
            speed = motion.HasValue ? motion.Value.MovementSpeed : 0f;
            replicationPresentationHeldFrames++;
        }

        private static bool TryApplyReplicationSemanticNormalInterpolation(
            string entityId,
            string kind,
            ReplicationPresentationSample from,
            ReplicationPresentationSample to,
            float t,
            float now,
            ref Vector3 position,
            ref Quaternion rotation)
        {
            // The raw Lerp/Slerp values supplied by the caller are the unconditional
            // fallback. In particular, never touch either authoritative endpoint.
            if (!replicationConfigSemanticAgentPresentation
                || t <= 0f
                || t >= 1f
                || !to.Motion.HasValue
                || to.Motion.Value.IsClimbing
                || to.Motion.Value.IsSwimming)
            {
                return false;
            }

            var segment = to.Position - from.Position;
            var horizontalSegment = new Vector3(segment.x, 0f, segment.z);
            var horizontalDistance = horizontalSegment.magnitude;
            if (horizontalDistance <= 0.001f
                || !TryGetReplicationSemanticAgentMotionInterpolationGuidance(
                    entityId,
                    from.Position,
                    to.Position,
                    to.Motion.Value.Gait,
                    now,
                    out var fromDirection,
                    out var toDirection))
            {
                return false;
            }

            fromDirection.y = 0f;
            toDirection.y = 0f;
            if (fromDirection.sqrMagnitude <= 0.0001f || toDirection.sqrMagnitude <= 0.0001f)
            {
                return false;
            }

            fromDirection.Normalize();
            toDirection.Normalize();
            var tangentLength = horizontalDistance / 3f;
            var controlFrom = from.Position + fromDirection * tangentLength;
            var controlTo = to.Position - toDirection * tangentLength;
            var inverseT = 1f - t;
            var inverseTSquared = inverseT * inverseT;
            var tSquared = t * t;
            var guided = from.Position * (inverseTSquared * inverseT)
                + controlFrom * (3f * inverseTSquared * t)
                + controlTo * (3f * inverseT * tSquared)
                + to.Position * (tSquared * t);
            var correction = guided - position;
            correction.y = 0f;
            var maximumCorrection = Mathf.Min(
                ReplicationPresentationSemanticCurveMaximumMeters,
                horizontalDistance * 0.4f);
            if (correction.sqrMagnitude > maximumCorrection * maximumCorrection)
            {
                correction = correction.normalized * maximumCorrection;
            }

            var authoritativeY = position.y;
            position += correction * ReplicationPresentationSemanticCurveStrength;
            position.y = authoritativeY;

            var tangent = (controlFrom - from.Position) * (3f * inverseTSquared)
                + (controlTo - controlFrom) * (6f * inverseT * t)
                + (to.Position - controlTo) * (3f * tSquared);
            tangent.y = 0f;
            if (tangent.sqrMagnitude > 0.0001f)
            {
                var facing = tangent.normalized;
                var animal = string.Equals(kind, "animal", StringComparison.Ordinal);
                if (animal)
                {
                    facing = StabilizeReplicationSemanticAnimalFacing(entityId, facing, to.Motion.Value, now);
                }

                if (facing.sqrMagnitude > 0.0001f)
                {
                    var targetRotation = Quaternion.LookRotation(facing, Vector3.up);
                    var endpointEnvelope = 4f * t * (1f - t);
                    var facingWeight = endpointEnvelope * (animal ? 0.55f : 0.3f);
                    rotation = Quaternion.SlerpUnclamped(rotation, targetRotation, facingWeight);
                }
            }

            replicationPresentationSemanticInterpolatedFrames++;
            replicationSemanticMotionInterpolationGuidedFrames++;
            return true;
        }

        private static Vector3 StabilizeReplicationSemanticAnimalFacing(
            string entityId,
            Vector3 candidate,
            ReplicationEntityMotionMetadata motion,
            float now)
        {
            if (!ReplicationSemanticAnimalPresentationByEntityId.TryGetValue(entityId, out var state))
            {
                return candidate;
            }

            candidate.y = 0f;
            if (candidate.sqrMagnitude <= 0.0001f)
            {
                return state.FacingInitialized ? state.StableFacingDirection : candidate;
            }

            candidate.Normalize();
            if (!state.FacingInitialized)
            {
                state.FacingInitialized = true;
                state.StableFacingDirection = candidate;
                return candidate;
            }

            var explicitlyMoving = motion.IsMoving
                || motion.IsRunning
                || motion.Gait == ReplicationAgentLocomotionGait.Run
                || motion.Gait == ReplicationAgentLocomotionGait.Sprint;
            if (!explicitlyMoving && motion.MovementSpeed < ReplicationPresentationAnimalMoveStartSpeed)
            {
                replicationPresentationAnimalFacingHeldFrames++;
                return state.StableFacingDirection;
            }

            var stableDot = Vector3.Dot(state.StableFacingDirection, candidate);
            var blend = 1f - Mathf.Exp(-12f * Mathf.Max(0.001f, Time.unscaledDeltaTime));
            if (stableDot >= ReplicationPresentationAnimalFacingDeadbandDot)
            {
                state.PendingFacingInitialized = false;
                state.StableFacingDirection = Vector3.Slerp(state.StableFacingDirection, candidate, blend).normalized;
                return state.StableFacingDirection;
            }

            if (!state.PendingFacingInitialized
                || Vector3.Dot(state.PendingFacingDirection, candidate) < ReplicationPresentationAnimalFacingPendingDot)
            {
                state.PendingFacingInitialized = true;
                state.PendingFacingDirection = candidate;
                state.PendingFacingSinceRealtime = now;
                replicationPresentationAnimalFacingHeldFrames++;
                return state.StableFacingDirection;
            }

            state.PendingFacingDirection = Vector3.Slerp(state.PendingFacingDirection, candidate, blend).normalized;
            if (now - state.PendingFacingSinceRealtime < ReplicationPresentationAnimalFacingCommitSeconds)
            {
                replicationPresentationAnimalFacingHeldFrames++;
                return state.StableFacingDirection;
            }

            state.StableFacingDirection = Vector3.Slerp(
                state.StableFacingDirection,
                state.PendingFacingDirection,
                blend).normalized;
            if (Vector3.Dot(state.StableFacingDirection, state.PendingFacingDirection)
                >= ReplicationPresentationAnimalFacingDeadbandDot)
            {
                state.PendingFacingInitialized = false;
            }

            return state.StableFacingDirection;
        }

        private static float GetReplicationPresentationSpeed(
            ReplicationPresentationSample from,
            ReplicationPresentationSample to,
            float duration)
        {
            if (duration <= 0.0001f || duration > ReplicationPresentationMaxSegmentSeconds)
            {
                return 0f;
            }

            var delta = to.Position - from.Position;
            return new Vector2(delta.x, delta.z).magnitude / duration;
        }

        private static Quaternion NormalizeReplicationPresentationRotation(Quaternion rotation)
        {
            var magnitude = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
            if (magnitude <= 0.0001f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))
            {
                return Quaternion.identity;
            }

            var inverse = 1f / magnitude;
            return new Quaternion(rotation.x * inverse, rotation.y * inverse, rotation.z * inverse, rotation.w * inverse);
        }

        private static void PruneReplicationPresentationSamples(ReplicationPresentationTrack track, int keepFromIndex)
        {
            if (keepFromIndex <= 0)
            {
                return;
            }

            track.Samples.RemoveRange(0, keepFromIndex);
        }

        private static void PruneReplicationPresentationTracksIfDue(float now)
        {
            if (now < replicationNextPresentationPruneRealtime)
            {
                return;
            }

            replicationNextPresentationPruneRealtime = now + 1f;
            var expired = new List<string>();
            foreach (var pair in ReplicationPresentationTracks)
            {
                if (now - pair.Value.LastReceivedRealtime > ReplicationPresentationTrackLifetimeSeconds)
                {
                    expired.Add(pair.Key);
                }
            }

            for (var i = 0; i < expired.Count; i++)
            {
                ReplicationPresentationTracks.Remove(expired[i]);
                ReplicationSemanticAnimalPresentationByEntityId.Remove(expired[i]);
            }
        }

        private static bool UpdateReplicationSmoothLocomotion(
            string entityId,
            Animator? animator,
            string kind,
            float speed,
            ReplicationEntityMotionMetadata? motion)
        {
            if (!replicationConfigAnimateReplicatedMovement || animator == null)
            {
                return false;
            }

            if (replicationConfigSemanticAgentPresentation)
            {
                // Work presentation owns the animator for its full active lifetime. This
                // guard must precede both semantic and no-metadata/legacy locomotion writes.
                if (IsReplicationSemanticWorkPresentationActive(entityId))
                {
                    return false;
                }

                var animal = string.Equals(kind, "animal", StringComparison.Ordinal);
                if (motion.HasValue)
                {
                    return ApplyReplicationSemanticLocomotion(entityId, animator, motion.Value, animal);
                }

                if (animal && TryApplyReplicationSemanticAnimalLocomotionHold(entityId, animator))
                {
                    return true;
                }

                ResetReplicationSemanticLocomotion(animator);
            }

            var moving = speed >= ReplicationPresentationMovingSpeed;
            var runningThreshold = string.Equals(kind, "animal", StringComparison.Ordinal) ? 1.2f : 1.7f;
            var sprintingThreshold = string.Equals(kind, "animal", StringComparison.Ordinal) ? 3f : 3.5f;
            var running = speed >= runningThreshold;
            var sprinting = speed >= sprintingThreshold;
            var support = GetReplicationSmoothAnimatorSupport(animator);

            try
            {
                if (moving && !animator.enabled)
                {
                    animator.enabled = true;
                }

                SetReplicationAnimatorParameterIfSupported(animator, support.MovingHash, support.MovingType, moving ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.RunningHash, support.RunningType, running ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.SprintingHash, support.SprintingType, sprinting ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.SpeedHash, support.SpeedType, speed);
            }
            catch
            {
            }

            return moving;
        }

        private static bool ApplyReplicationSemanticLocomotion(
            string entityId,
            Animator? animator,
            ReplicationEntityMotionMetadata motion)
        {
            var animal = ReplicationPresentationTracks.TryGetValue(entityId, out var track)
                && string.Equals(track.Kind, "animal", StringComparison.Ordinal);
            return ApplyReplicationSemanticLocomotion(entityId, animator, motion, animal);
        }

        private static bool ApplyReplicationSemanticLocomotion(
            string entityId,
            Animator? animator,
            ReplicationEntityMotionMetadata motion,
            bool animal)
        {
            if (!replicationConfigAnimateReplicatedMovement || animator == null)
            {
                return false;
            }

            if (replicationConfigSemanticAgentPresentation
                && IsReplicationSemanticWorkPresentationActive(entityId))
            {
                return false;
            }

            var moving = motion.IsMoving || motion.IsSwimming || motion.IsClimbing;
            var running = motion.IsRunning;
            var sprinting = motion.Gait == ReplicationAgentLocomotionGait.Sprint;
            var movementSpeed = motion.MovementSpeed;
            var animatorSpeed = motion.AnimatorSpeed;
            if (replicationConfigSemanticAgentPresentation && animal)
            {
                ApplyReplicationSemanticAnimalLocomotionHysteresis(
                    entityId,
                    motion,
                    ref moving,
                    ref running,
                    ref sprinting,
                    ref movementSpeed,
                    ref animatorSpeed);
            }

            return WriteReplicationSemanticLocomotion(
                animator,
                moving,
                running,
                sprinting,
                motion.IsSwimming,
                motion.ClimbDirection,
                movementSpeed,
                animatorSpeed);
        }

        private static void ApplyReplicationSemanticAnimalLocomotionHysteresis(
            string entityId,
            ReplicationEntityMotionMetadata motion,
            ref bool moving,
            ref bool running,
            ref bool sprinting,
            ref float movementSpeed,
            ref float animatorSpeed)
        {
            if (!ReplicationSemanticAnimalPresentationByEntityId.TryGetValue(entityId, out var state))
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            var velocitySpeed = new Vector2(motion.VelocityX, motion.VelocityZ).magnitude;
            var motionSignal = Mathf.Max(motion.MovementSpeed, velocitySpeed);
            var explicitlyMoving = motion.IsMoving || motion.IsSwimming || motion.IsClimbing;
            var movingSignal = explicitlyMoving
                || (!state.Moving
                    ? motionSignal >= ReplicationPresentationAnimalMoveStartSpeed
                    : motionSignal > ReplicationPresentationAnimalMoveStopSpeed);
            var movingHeld = false;
            if (movingSignal)
            {
                state.Moving = true;
                state.MovingHoldUntilRealtime = now + ReplicationPresentationAnimalMoveHoldSeconds;
                if (explicitlyMoving || motionSignal >= ReplicationPresentationAnimalMoveStartSpeed)
                {
                    state.LastMovementSpeed = Mathf.Max(motion.MovementSpeed, ReplicationPresentationAnimalMoveStartSpeed);
                    state.LastAnimatorSpeed = Mathf.Max(0f, motion.AnimatorSpeed);
                }
            }
            else if (state.Moving && now < state.MovingHoldUntilRealtime)
            {
                movingHeld = true;
            }
            else
            {
                state.Moving = false;
            }

            var explicitlyRunning = motion.IsRunning
                || motion.Gait == ReplicationAgentLocomotionGait.Run
                || motion.Gait == ReplicationAgentLocomotionGait.Sprint;
            var runningSignal = explicitlyRunning
                || (!state.Running
                    ? motionSignal >= ReplicationPresentationAnimalRunStartSpeed
                    : motionSignal > ReplicationPresentationAnimalRunStopSpeed);
            var runningHeld = false;
            if (runningSignal && state.Moving)
            {
                state.Running = true;
                state.Sprinting = motion.Gait == ReplicationAgentLocomotionGait.Sprint;
                state.RunningHoldUntilRealtime = now + ReplicationPresentationAnimalRunHoldSeconds;
            }
            else if (state.Running && state.Moving && now < state.RunningHoldUntilRealtime)
            {
                runningHeld = true;
            }
            else
            {
                state.Running = false;
                state.Sprinting = false;
            }

            moving = state.Moving;
            running = state.Running && moving;
            sprinting = state.Sprinting && running;
            if (movingHeld || runningHeld)
            {
                var holdWeight = Mathf.Clamp01(
                    (state.MovingHoldUntilRealtime - now) / ReplicationPresentationAnimalMoveHoldSeconds);
                movementSpeed = Mathf.Max(movementSpeed, state.LastMovementSpeed * holdWeight);
                animatorSpeed = Mathf.Max(animatorSpeed, Mathf.Lerp(1f, state.LastAnimatorSpeed, holdWeight));
                replicationPresentationAnimalMotionHeldFrames++;
            }
        }

        private static bool TryApplyReplicationSemanticAnimalLocomotionHold(string entityId, Animator animator)
        {
            if (!ReplicationSemanticAnimalPresentationByEntityId.TryGetValue(entityId, out var state)
                || !state.Moving)
            {
                return false;
            }

            var now = Time.realtimeSinceStartup;
            if (now >= state.MovingHoldUntilRealtime)
            {
                state.Moving = false;
                state.Running = false;
                state.Sprinting = false;
                return false;
            }

            if (state.Running && now >= state.RunningHoldUntilRealtime)
            {
                state.Running = false;
                state.Sprinting = false;
            }

            var holdWeight = Mathf.Clamp01(
                (state.MovingHoldUntilRealtime - now) / ReplicationPresentationAnimalMoveHoldSeconds);
            replicationPresentationAnimalMotionHeldFrames++;
            return WriteReplicationSemanticLocomotion(
                animator,
                moving: true,
                running: state.Running,
                sprinting: state.Sprinting && state.Running,
                swimming: false,
                climbDirection: 0,
                movementSpeed: state.LastMovementSpeed * holdWeight,
                animatorSpeed: Mathf.Lerp(1f, state.LastAnimatorSpeed, holdWeight));
        }

        private static bool WriteReplicationSemanticLocomotion(
            Animator animator,
            bool moving,
            bool running,
            bool sprinting,
            bool swimming,
            int climbDirection,
            float movementSpeed,
            float animatorSpeed)
        {
            var support = GetReplicationSmoothAnimatorSupport(animator);
            try
            {
                if (moving && !animator.enabled)
                {
                    animator.enabled = true;
                }

                SetReplicationAnimatorParameterIfSupported(animator, support.MovingHash, support.MovingType, moving ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.RunningHash, support.RunningType, running ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.SprintingHash, support.SprintingType, sprinting ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.SwimmingHash, support.SwimmingType, swimming ? 1f : 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.ClimbDirectionHash, support.ClimbDirectionType, climbDirection);
                SetReplicationAnimatorParameterIfSupported(animator, support.SpeedHash, support.SpeedType, movementSpeed);
                animator.speed = Mathf.Clamp(animatorSpeed, 0f, 4f);
            }
            catch
            {
            }

            return moving;
        }

        private static void ResetReplicationSemanticLocomotion(Animator? animator)
        {
            if (!replicationConfigSemanticAgentPresentation
                || !replicationConfigAnimateReplicatedMovement
                || animator == null)
            {
                return;
            }

            var support = GetReplicationSmoothAnimatorSupport(animator);
            try
            {
                SetReplicationAnimatorParameterIfSupported(animator, support.MovingHash, support.MovingType, 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.RunningHash, support.RunningType, 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.SprintingHash, support.SprintingType, 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.SwimmingHash, support.SwimmingType, 0f);
                SetReplicationAnimatorParameterIfSupported(animator, support.ClimbDirectionHash, support.ClimbDirectionType, 0f);
                animator.speed = 1f;
            }
            catch
            {
            }
        }

        private static ReplicationSmoothAnimatorSupport GetReplicationSmoothAnimatorSupport(Animator animator)
        {
            var instanceId = animator.GetInstanceID();
            if (ReplicationSmoothAnimatorSupportByInstanceId.TryGetValue(instanceId, out var cached))
            {
                return cached;
            }

            var support = new ReplicationSmoothAnimatorSupport();
            try
            {
                var parameters = animator.parameters;
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (support.MovingHash == 0 && MatchesReplicationAnimatorParameter(parameter.name, "Moving", "IsMoving", "Move"))
                    {
                        support.MovingHash = parameter.nameHash;
                        support.MovingType = parameter.type;
                    }
                    else if (support.RunningHash == 0 && MatchesReplicationAnimatorParameter(parameter.name, "Running", "IsRunning", "Run"))
                    {
                        support.RunningHash = parameter.nameHash;
                        support.RunningType = parameter.type;
                    }
                    else if (support.SprintingHash == 0 && MatchesReplicationAnimatorParameter(parameter.name, "Sprinting", "IsSprinting", "Sprint"))
                    {
                        support.SprintingHash = parameter.nameHash;
                        support.SprintingType = parameter.type;
                    }
                    else if (support.SwimmingHash == 0 && MatchesReplicationAnimatorParameter(parameter.name, "Swimming", "IsSwimming", "Swim"))
                    {
                        support.SwimmingHash = parameter.nameHash;
                        support.SwimmingType = parameter.type;
                    }
                    else if (support.ClimbDirectionHash == 0 && MatchesReplicationAnimatorParameter(parameter.name, "ClimbDir", "ClimbDirection"))
                    {
                        support.ClimbDirectionHash = parameter.nameHash;
                        support.ClimbDirectionType = parameter.type;
                    }
                    else if (support.SpeedHash == 0 && MatchesReplicationAnimatorParameter(parameter.name, "Speed", "MovementSpeed", "MoveSpeed", "Velocity", "LocomotionSpeed"))
                    {
                        support.SpeedHash = parameter.nameHash;
                        support.SpeedType = parameter.type;
                    }
                }
            }
            catch
            {
            }

            ReplicationSmoothAnimatorSupportByInstanceId[instanceId] = support;
            return support;
        }

        private static bool MatchesReplicationAnimatorParameter(string value, params string[] candidates)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                if (string.Equals(value, candidates[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetReplicationAnimatorParameterIfSupported(
            Animator animator,
            int parameterHash,
            AnimatorControllerParameterType parameterType,
            float value)
        {
            if (parameterHash == 0)
            {
                return;
            }

            switch (parameterType)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(parameterHash, value > 0f);
                    break;
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(parameterHash, value, 0.08f, Time.unscaledDeltaTime);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(parameterHash, Mathf.RoundToInt(value));
                    break;
            }
        }

        private static void ClearReplicationPresentationSmoothing()
        {
            ReplicationPresentationTracks.Clear();
            ReplicationSmoothAnimatorSupportByInstanceId.Clear();
            ReplicationSemanticAnimalPresentationByEntityId.Clear();
            replicationNextPresentationPruneRealtime = 0f;
            replicationPresentationClockOffsetInitialized = false;
            replicationPresentationClockOffset = 0f;
            replicationPresentationInterpolatedFrames = 0L;
            replicationPresentationExtrapolatedFrames = 0L;
            replicationPresentationHeldFrames = 0L;
            replicationPresentationDiscontinuities = 0L;
            replicationPresentationSemanticInterpolatedFrames = 0L;
            replicationPresentationAnimalMotionHeldFrames = 0L;
            replicationPresentationAnimalFacingHeldFrames = 0L;
            ResetReplicationSemanticMotionState();
            ResetReplicationSemanticAgentMotionPresentation();
        }

        private static string FormatReplicationPresentationSmoothingStatus()
        {
            return "smoothMovement=" + replicationConfigSmoothReplicatedMovement
                + " interpolationMs=" + replicationConfigInterpolationMs
                + " smoothTracks=" + ReplicationPresentationTracks.Count
                + " smoothInterpolated=" + replicationPresentationInterpolatedFrames
                + " smoothExtrapolated=" + replicationPresentationExtrapolatedFrames
                + " smoothHeld=" + replicationPresentationHeldFrames
                + " smoothDiscontinuities=" + replicationPresentationDiscontinuities
                + " smoothSemanticCurves=" + replicationPresentationSemanticInterpolatedFrames
                + " animalMotionHeld=" + replicationPresentationAnimalMotionHeldFrames
                + " animalFacingHeld=" + replicationPresentationAnimalFacingHeldFrames
                + " " + FormatReplicationSemanticMotionStatus();
        }
    }
}
