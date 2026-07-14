using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private enum ReplicationSemanticMotionDiagnosticCategory
        {
            OwnerAccessorMissing = 0,
            PathDriverAccessorMissing = 1,
            OwnerInvocationFailed = 2,
            OwnerNull = 3,
            OwnerTypeMismatch = 4,
            PathDriverInvocationFailed = 5,
            PathDriverNull = 6,
            VelocityAccessorMissing = 7,
            IsMovingAccessorMissing = 8,
            VelocityReadFailed = 9,
            VelocityTypeMismatch = 10,
            RunningInvocationFailed = 11,
            UnexpectedException = 12,
            Count = 13
        }

        private sealed class ReplicationSemanticMotionViewAccessors
        {
            public MethodInfo? GetAgentOwner;
            public MethodInfo? GetAgentPathDriver;
            public MethodInfo? IsRunning;
            public PropertyInfo? Animator;
        }

        private sealed class ReplicationSemanticMotionDriverAccessors
        {
            public PropertyInfo? Velocity;
            public PropertyInfo? IsMoving;
            public PropertyInfo? IsSwimming;
            public PropertyInfo? IsClimbing;
            public PropertyInfo? ClimbDirection;
            public PropertyInfo? CurrentPath;
            public PropertyInfo? CurrentNodeIndex;
            public PropertyInfo? FinalDestination;
            public PropertyInfo? FinalDestinationPrecise;
        }

        private sealed class ReplicationSemanticMotionRevisionState
        {
            public long Signature;
            public long Revision;
            public bool Initialized;
            public bool WasActive;
        }

        private static readonly Dictionary<Type, ReplicationSemanticMotionViewAccessors> ReplicationSemanticMotionViewAccessorsByType =
            new Dictionary<Type, ReplicationSemanticMotionViewAccessors>();
        private static readonly Dictionary<Type, ReplicationSemanticMotionDriverAccessors> ReplicationSemanticMotionDriverAccessorsByType =
            new Dictionary<Type, ReplicationSemanticMotionDriverAccessors>();
        private static readonly Dictionary<string, ReplicationSemanticMotionRevisionState> ReplicationSemanticMotionRevisionByEntityId =
            new Dictionary<string, ReplicationSemanticMotionRevisionState>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, ulong> ReplicationSemanticMotionLoggedDiagnosticMaskByType =
            new Dictionary<Type, ulong>();
        private static readonly object[] ReplicationSemanticMotionInvokeArgument = new object[1];
        private static readonly long[] ReplicationSemanticMotionDiagnosticCounts =
            new long[(int)ReplicationSemanticMotionDiagnosticCategory.Count];
        private const int ReplicationSemanticMotionDiagnosticTypeLimit = 32;

        private static long replicationSemanticMotionMetadataCaptured;
        private static long replicationSemanticMotionMetadataFallbacks;
        private static long replicationSemanticMotionPathRevisions;

        private static bool TryCollectReplicationSemanticMotionMetadata(
            object view,
            MonoBehaviour behaviour,
            string entityId,
            string kind,
            out ReplicationEntityMotionMetadata metadata)
        {
            metadata = default;
            try
            {
                var viewAccessors = GetReplicationSemanticMotionViewAccessors(view.GetType());
                if (viewAccessors.GetAgentOwner == null)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.OwnerAccessorMissing,
                        view.GetType());
                }

                if (viewAccessors.GetAgentPathDriver == null)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.PathDriverAccessorMissing,
                        view.GetType());
                }

                object? owner;
                try
                {
                    owner = viewAccessors.GetAgentOwner.Invoke(
                        viewAccessors.GetAgentOwner.IsStatic ? null : view,
                        Array.Empty<object>());
                }
                catch (Exception ex)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.OwnerInvocationFailed,
                        view.GetType(),
                        ex);
                }

                if (owner == null)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.OwnerNull,
                        view.GetType());
                }

                var pathDriverParameters = viewAccessors.GetAgentPathDriver.GetParameters();
                if (pathDriverParameters.Length != 1
                    || !pathDriverParameters[0].ParameterType.IsInstanceOfType(owner))
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.OwnerTypeMismatch,
                        view.GetType());
                }

                object? driver;
                try
                {
                    ReplicationSemanticMotionInvokeArgument[0] = owner;
                    driver = viewAccessors.GetAgentPathDriver.Invoke(
                        viewAccessors.GetAgentPathDriver.IsStatic ? null : view,
                        ReplicationSemanticMotionInvokeArgument);
                }
                catch (Exception ex)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.PathDriverInvocationFailed,
                        view.GetType(),
                        ex);
                }
                finally
                {
                    ReplicationSemanticMotionInvokeArgument[0] = null!;
                }
                if (driver == null)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.PathDriverNull,
                        view.GetType());
                }

                var driverAccessors = GetReplicationSemanticMotionDriverAccessors(driver.GetType());
                if (driverAccessors.Velocity == null)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.VelocityAccessorMissing,
                        driver.GetType());
                }

                if (driverAccessors.IsMoving == null)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.IsMovingAccessorMissing,
                        driver.GetType());
                }

                object? velocityValue;
                try
                {
                    velocityValue = driverAccessors.Velocity.GetValue(driver, null);
                }
                catch (Exception ex)
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.VelocityReadFailed,
                        driver.GetType(),
                        ex);
                }

                if (!(velocityValue is Vector3 velocity))
                {
                    return FailReplicationSemanticMotionCapture(
                        ReplicationSemanticMotionDiagnosticCategory.VelocityTypeMismatch,
                        driver.GetType());
                }

                var isMoving = ReadReplicationSemanticMotionBool(driverAccessors.IsMoving, driver);
                var isSwimming = ReadReplicationSemanticMotionBool(driverAccessors.IsSwimming, driver);
                var isClimbing = ReadReplicationSemanticMotionBool(driverAccessors.IsClimbing, driver);
                var climbDirection = ReadReplicationSemanticMotionInt(driverAccessors.ClimbDirection, driver);
                var isRunning = false;
                if (viewAccessors.IsRunning != null)
                {
                    try
                    {
                        ReplicationSemanticMotionInvokeArgument[0] = driver;
                        isRunning = viewAccessors.IsRunning.Invoke(
                            viewAccessors.IsRunning.IsStatic ? null : view,
                            ReplicationSemanticMotionInvokeArgument) is bool running && running;
                    }
                    catch (Exception ex)
                    {
                        RecordReplicationSemanticMotionDiagnostic(
                            ReplicationSemanticMotionDiagnosticCategory.RunningInvocationFailed,
                            view.GetType(),
                            ex);
                    }
                    finally
                    {
                        ReplicationSemanticMotionInvokeArgument[0] = null!;
                    }
                }

                var movementSpeed = velocity.magnitude;
                var sprintThreshold = string.Equals(kind, "animal", StringComparison.Ordinal)
                    ? 3f
                    : 3.5f;
                var gait = !isMoving && !isSwimming && !isClimbing
                    ? ReplicationAgentLocomotionGait.Idle
                    : isClimbing
                        ? ReplicationAgentLocomotionGait.Climb
                        : isSwimming
                            ? ReplicationAgentLocomotionGait.Swim
                            : isRunning
                                ? movementSpeed >= sprintThreshold
                                    ? ReplicationAgentLocomotionGait.Sprint
                                    : ReplicationAgentLocomotionGait.Run
                                : ReplicationAgentLocomotionGait.Walk;
                var animator = viewAccessors.Animator?.GetValue(view, null) as Animator;
                if (animator == null)
                {
                    animator = behaviour.GetComponentInChildren<Animator>();
                }
                var animatorSpeed = animator == null ? 1f : Mathf.Max(0f, animator.speed);
                var signature = ComputeReplicationSemanticMotionSignature(
                    driver,
                    driverAccessors,
                    isMoving,
                    isSwimming,
                    isClimbing,
                    climbDirection);
                var active = isMoving || isSwimming || isClimbing || movementSpeed >= 0.001f;
                var revision = UpdateReplicationSemanticMotionRevision(entityId, signature, active, out var emitIdleTransition);
                CaptureReplicationSemanticAgentMotionPresentation(
                    driver,
                    driverAccessors,
                    entityId,
                    active,
                    movementSpeed,
                    gait,
                    behaviour.transform);

                // Idle entities keep the original nine-field packet. A null metadata
                // sample also tells the client to return to legacy/idle ownership, so
                // semantic mode does not add bytes to every stationary pawn at 12 Hz.
                if (!active && !emitIdleTransition)
                {
                    return false;
                }

                metadata = new ReplicationEntityMotionMetadata(
                    velocity.x,
                    velocity.y,
                    velocity.z,
                    movementSpeed,
                    gait,
                    isMoving,
                    isRunning,
                    isSwimming,
                    isClimbing,
                    climbDirection,
                    animatorSpeed,
                    revision);
                replicationSemanticMotionMetadataCaptured++;
                return true;
            }
            catch (Exception ex)
            {
                return FailReplicationSemanticMotionCapture(
                    ReplicationSemanticMotionDiagnosticCategory.UnexpectedException,
                    view.GetType(),
                    ex);
            }
        }

        private static ReplicationSemanticMotionViewAccessors GetReplicationSemanticMotionViewAccessors(Type viewType)
        {
            if (ReplicationSemanticMotionViewAccessorsByType.TryGetValue(viewType, out var cached))
            {
                return cached;
            }

            var accessors = new ReplicationSemanticMotionViewAccessors
            {
                GetAgentOwner = FindReplicationSemanticMotionMethod(
                    viewType,
                    "GetAgentOwner",
                    parameterCount: 0,
                    returnType: null,
                    requireInstance: true,
                    parameterTypeName: null,
                    returnTypeName: null),
                GetAgentPathDriver = FindReplicationSemanticMotionMethod(
                    viewType,
                    "GetAgentPathDriver",
                    parameterCount: 1,
                    returnType: null,
                    requireInstance: null,
                    parameterTypeName: "CreatureBase",
                    returnTypeName: "PathfinderAgentDriver"),
                IsRunning = FindReplicationSemanticMotionMethod(
                    viewType,
                    "IsRunning",
                    parameterCount: 1,
                    returnType: typeof(bool),
                    requireInstance: null,
                    parameterTypeName: "PathfinderAgentDriver",
                    returnTypeName: null)
            };

            accessors.Animator = FindReplicationSemanticMotionProperty(viewType, "Animator");

            ReplicationSemanticMotionViewAccessorsByType[viewType] = accessors;
            return accessors;
        }

        private static MethodInfo? FindReplicationSemanticMotionMethod(
            Type type,
            string name,
            int parameterCount,
            Type? returnType,
            bool? requireInstance,
            string? parameterTypeName,
            string? returnTypeName)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var methods = current.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly);
                for (var i = 0; i < methods.Length; i++)
                {
                    var method = methods[i];
                    var parameters = method.GetParameters();
                    if (!string.Equals(method.Name, name, StringComparison.Ordinal)
                        || parameters.Length != parameterCount
                        || returnType != null && method.ReturnType != returnType
                        || returnTypeName != null && !string.Equals(method.ReturnType.Name, returnTypeName, StringComparison.Ordinal)
                        || parameterTypeName != null
                            && (parameters.Length != 1
                                || !string.Equals(parameters[0].ParameterType.Name, parameterTypeName, StringComparison.Ordinal))
                        || requireInstance.HasValue && method.IsStatic == requireInstance.Value)
                    {
                        continue;
                    }

                    return method;
                }
            }

            return null;
        }

        private static bool FailReplicationSemanticMotionCapture(
            ReplicationSemanticMotionDiagnosticCategory category,
            Type subjectType,
            Exception? exception = null)
        {
            replicationSemanticMotionMetadataFallbacks++;
            RecordReplicationSemanticMotionDiagnostic(category, subjectType, exception);
            return false;
        }

        private static void RecordReplicationSemanticMotionDiagnostic(
            ReplicationSemanticMotionDiagnosticCategory category,
            Type subjectType,
            Exception? exception = null)
        {
            var categoryIndex = (int)category;
            if (categoryIndex < 0 || categoryIndex >= ReplicationSemanticMotionDiagnosticCounts.Length)
            {
                return;
            }

            ReplicationSemanticMotionDiagnosticCounts[categoryIndex]++;
            var bit = 1UL << categoryIndex;
            ReplicationSemanticMotionLoggedDiagnosticMaskByType.TryGetValue(subjectType, out var loggedMask);
            if ((loggedMask & bit) != 0UL)
            {
                return;
            }

            if (!ReplicationSemanticMotionLoggedDiagnosticMaskByType.ContainsKey(subjectType)
                && ReplicationSemanticMotionLoggedDiagnosticMaskByType.Count >= ReplicationSemanticMotionDiagnosticTypeLimit)
            {
                return;
            }

            ReplicationSemanticMotionLoggedDiagnosticMaskByType[subjectType] = loggedMask | bit;
            var root = exception is TargetInvocationException invocationException && invocationException.InnerException != null
                ? invocationException.InnerException
                : exception;
            instance?.LogReplicationWarning(
                "Going Cooperative semantic motion fallback category="
                + category
                + " type="
                + (subjectType.FullName ?? subjectType.Name)
                + (root == null
                    ? string.Empty
                    : " error=" + root.GetType().Name + ":" + TrimFingerprintText(root.Message, 240)));
        }

        private static ReplicationSemanticMotionDriverAccessors GetReplicationSemanticMotionDriverAccessors(Type driverType)
        {
            if (ReplicationSemanticMotionDriverAccessorsByType.TryGetValue(driverType, out var cached))
            {
                return cached;
            }

            var accessors = new ReplicationSemanticMotionDriverAccessors
            {
                Velocity = FindReplicationSemanticMotionProperty(driverType, "Velocity"),
                IsMoving = FindReplicationSemanticMotionProperty(driverType, "IsMoving"),
                IsSwimming = FindReplicationSemanticMotionProperty(driverType, "IsSwimming"),
                IsClimbing = FindReplicationSemanticMotionProperty(driverType, "IsClimbing"),
                ClimbDirection = FindReplicationSemanticMotionProperty(driverType, "ClimbDirection"),
                CurrentPath = FindReplicationSemanticMotionProperty(driverType, "CurrentPath"),
                CurrentNodeIndex = FindReplicationSemanticMotionProperty(driverType, "CurrentNodeIndex"),
                FinalDestination = FindReplicationSemanticMotionProperty(driverType, "FinalDestination"),
                FinalDestinationPrecise = FindReplicationSemanticMotionProperty(driverType, "FinalDestinationPrecise")
            };
            ReplicationSemanticMotionDriverAccessorsByType[driverType] = accessors;
            return accessors;
        }

        private static PropertyInfo? FindReplicationSemanticMotionProperty(Type type, string name)
        {
            while (type != null && type != typeof(object))
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    return property;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static bool ReadReplicationSemanticMotionBool(PropertyInfo? property, object owner)
        {
            try
            {
                return property != null && property.GetValue(owner, null) is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static int ReadReplicationSemanticMotionInt(PropertyInfo? property, object owner)
        {
            try
            {
                var value = property?.GetValue(owner, null);
                return value == null ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static long ComputeReplicationSemanticMotionSignature(
            object driver,
            ReplicationSemanticMotionDriverAccessors accessors,
            bool isMoving,
            bool isSwimming,
            bool isClimbing,
            int climbDirection)
        {
            unchecked
            {
                var signature = 17L;
                signature = (signature * 31L) + (isMoving ? 1L : 0L);
                signature = (signature * 31L) + (isSwimming ? 1L : 0L);
                signature = (signature * 31L) + (isClimbing ? 1L : 0L);
                signature = (signature * 31L) + climbDirection;
                signature = (signature * 31L) + ReadReplicationSemanticMotionInt(accessors.CurrentNodeIndex, driver);
                signature = (signature * 31L) + GetReplicationSemanticMotionObjectHash(accessors.CurrentPath, driver);
                signature = (signature * 31L) + GetReplicationSemanticMotionObjectHash(accessors.FinalDestination, driver);
                return signature;
            }
        }

        private static int GetReplicationSemanticMotionObjectHash(PropertyInfo? property, object owner)
        {
            try
            {
                var value = property?.GetValue(owner, null);
                if (value == null)
                {
                    return 0;
                }

                return value.GetType().IsValueType ? value.GetHashCode() : RuntimeHelpers.GetHashCode(value);
            }
            catch
            {
                return 0;
            }
        }

        private static long UpdateReplicationSemanticMotionRevision(
            string entityId,
            long signature,
            bool active,
            out bool emitIdleTransition)
        {
            if (!ReplicationSemanticMotionRevisionByEntityId.TryGetValue(entityId, out var state))
            {
                state = new ReplicationSemanticMotionRevisionState();
                ReplicationSemanticMotionRevisionByEntityId[entityId] = state;
            }

            emitIdleTransition = state.WasActive && !active;
            state.WasActive = active;

            if (!state.Initialized || state.Signature != signature)
            {
                state.Initialized = true;
                state.Signature = signature;
                state.Revision++;
                replicationSemanticMotionPathRevisions++;
            }

            return state.Revision;
        }

        private static void ResetReplicationSemanticMotionState()
        {
            ReplicationSemanticMotionViewAccessorsByType.Clear();
            ReplicationSemanticMotionDriverAccessorsByType.Clear();
            ReplicationSemanticMotionRevisionByEntityId.Clear();
            ReplicationSemanticMotionLoggedDiagnosticMaskByType.Clear();
            Array.Clear(ReplicationSemanticMotionDiagnosticCounts, 0, ReplicationSemanticMotionDiagnosticCounts.Length);
            ReplicationSemanticMotionInvokeArgument[0] = null!;
            replicationSemanticAnimatedAgentViewCache = Array.Empty<UnityEngine.Object>();
            replicationSemanticAnimatedAgentViewCacheRealtime = -10f;
            replicationSemanticMotionMetadataCaptured = 0L;
            replicationSemanticMotionMetadataFallbacks = 0L;
            replicationSemanticMotionPathRevisions = 0L;
        }

        private static string FormatReplicationSemanticMotionStatus()
        {
            var builder = new StringBuilder(320);
            builder.Append("semanticMotion=").Append(replicationConfigSemanticAgentPresentation)
                .Append(" semanticCaptured=").Append(replicationSemanticMotionMetadataCaptured)
                .Append(" semanticFallbacks=").Append(replicationSemanticMotionMetadataFallbacks)
                .Append(" semanticPathRevisions=").Append(replicationSemanticMotionPathRevisions)
                .Append(" semanticFallbackReasons=");
            for (var i = 0; i < ReplicationSemanticMotionDiagnosticCounts.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append((ReplicationSemanticMotionDiagnosticCategory)i)
                    .Append(':')
                    .Append(ReplicationSemanticMotionDiagnosticCounts[i]);
            }

            return builder.ToString();
        }
    }
}
