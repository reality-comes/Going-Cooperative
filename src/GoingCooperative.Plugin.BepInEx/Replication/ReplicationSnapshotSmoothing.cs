using System;
using System.Collections.Generic;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private sealed class ReplicationPresentationSample
        {
            public long Sequence;
            public float HostRealtime;
            public Vector3 Position;
            public Quaternion Rotation;
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
            public int SpeedHash;
            public AnimatorControllerParameterType SpeedType;
        }

        private const int ReplicationPresentationMaxSamples = 24;
        private const float ReplicationPresentationTrackLifetimeSeconds = 3f;
        private const float ReplicationPresentationMaxExtrapolationSeconds = 0.2f;
        private const float ReplicationPresentationMaxSegmentSeconds = 0.5f;
        private const float ReplicationPresentationWorkerTeleportDistance = 3f;
        private const float ReplicationPresentationAnimalTeleportDistance = 6f;
        private const float ReplicationPresentationMovingSpeed = 0.08f;

        private static readonly Dictionary<string, ReplicationPresentationTrack> ReplicationPresentationTracks =
            new Dictionary<string, ReplicationPresentationTrack>(StringComparer.Ordinal);
        private static readonly Dictionary<int, ReplicationSmoothAnimatorSupport> ReplicationSmoothAnimatorSupportByInstanceId =
            new Dictionary<int, ReplicationSmoothAnimatorSupport>();
        private static float replicationNextPresentationPruneRealtime;
        private static bool replicationPresentationClockOffsetInitialized;
        private static float replicationPresentationClockOffset;
        private static long replicationPresentationInterpolatedFrames;
        private static long replicationPresentationExtrapolatedFrames;
        private static long replicationPresentationHeldFrames;
        private static long replicationPresentationDiscontinuities;

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
                        track.Discontinuity = true;
                        replicationPresentationDiscontinuities++;
                    }
                }

                samples.Add(new ReplicationPresentationSample
                {
                    Sequence = snapshot.Sequence,
                    HostRealtime = snapshot.SentRealtime,
                    Position = position,
                    Rotation = rotation
                });
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

                EvaluateReplicationPresentationTrack(track, renderHostRealtime, now, out var position, out var rotation, out var speed);
                view.Transform.position = position;
                view.Transform.rotation = rotation;
                if (UpdateReplicationSmoothLocomotion(view.Animator, track.Kind, speed))
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
            ReplicationPresentationTrack track,
            float renderHostRealtime,
            float now,
            out Vector3 position,
            out Quaternion rotation,
            out float speed)
        {
            var samples = track.Samples;
            var newest = samples[samples.Count - 1];
            if (track.Discontinuity || samples.Count == 1)
            {
                track.Discontinuity = false;
                position = newest.Position;
                rotation = newest.Rotation;
                speed = 0f;
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
                speed = GetReplicationPresentationSpeed(from, to, duration);
                replicationPresentationInterpolatedFrames++;
                PruneReplicationPresentationSamples(track, upperIndex - 1);
                return;
            }

            var oldest = samples[0];
            if (renderHostRealtime <= oldest.HostRealtime)
            {
                position = oldest.Position;
                rotation = oldest.Rotation;
                speed = 0f;
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
                var velocity = (newest.Position - previous.Position) / segmentSeconds;
                var extrapolation = Mathf.Clamp(renderHostRealtime - newest.HostRealtime, 0f, ReplicationPresentationMaxExtrapolationSeconds);
                position = newest.Position + velocity * extrapolation;
                rotation = newest.Rotation;
                speed = new Vector2(velocity.x, velocity.z).magnitude;
                replicationPresentationExtrapolatedFrames++;
                return;
            }

            position = newest.Position;
            rotation = newest.Rotation;
            speed = 0f;
            replicationPresentationHeldFrames++;
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
            }
        }

        private static bool UpdateReplicationSmoothLocomotion(Animator? animator, string kind, float speed)
        {
            if (!replicationConfigAnimateReplicatedMovement || animator == null)
            {
                return false;
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
            replicationNextPresentationPruneRealtime = 0f;
            replicationPresentationClockOffsetInitialized = false;
            replicationPresentationClockOffset = 0f;
            replicationPresentationInterpolatedFrames = 0L;
            replicationPresentationExtrapolatedFrames = 0L;
            replicationPresentationHeldFrames = 0L;
            replicationPresentationDiscontinuities = 0L;
        }

        private static string FormatReplicationPresentationSmoothingStatus()
        {
            return "smoothMovement=" + replicationConfigSmoothReplicatedMovement
                + " interpolationMs=" + replicationConfigInterpolationMs
                + " smoothTracks=" + ReplicationPresentationTracks.Count
                + " smoothInterpolated=" + replicationPresentationInterpolatedFrames
                + " smoothExtrapolated=" + replicationPresentationExtrapolatedFrames
                + " smoothHeld=" + replicationPresentationHeldFrames
                + " smoothDiscontinuities=" + replicationPresentationDiscontinuities;
        }
    }
}
