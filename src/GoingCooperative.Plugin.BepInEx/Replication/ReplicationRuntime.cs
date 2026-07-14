using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static UdpNetworkTransport? replicationTransport;
        private static bool replicationRuntimeStarted;
        private static bool replicationRuntimeStartAttempted;
        private static bool replicationRemoteHelloReceived;
        private static bool replicationRemoteCompatibilityRefused;
        private static string replicationLocalBuildHash = string.Empty;
        private static float replicationNextHelloRealtime;
        private static float replicationNextHelloLogRealtime;
        private static float replicationNextBuildHashMismatchWarnRealtime;
        private static float replicationNextPumpExceptionWarnRealtime;
        private static float replicationNextTransportDropWarnRealtime;
        private static long replicationPumpHandlerExceptions;
        private static long replicationLastTransportDecodeFailures;
        private static long replicationLastTransportChunkFailures;
        private static float replicationNextSnapshotRealtime;
        private static float replicationNextStatusLogRealtime;
        private static float replicationNextSnapshotValidationRealtime;
        private static float replicationNextResourcePileStateSnapshotRealtime;
        private static float replicationNextAgentCarryStateSnapshotRealtime;
        private static float replicationNextAgentActionHeartbeatRealtime;
        private static float replicationNextAgentCharacterStateSnapshotRealtime;
        private static float replicationNextBuildingStateSnapshotRealtime;
        private static float replicationNextRegionOrderMarkerSnapshotRealtime;
        private static float replicationNextGameTimeSnapshotRealtime;
        private static int replicationLastRuntimeUpdateFrame = -1;
        private static long replicationSnapshotSequence;
        private static long replicationIntentSequence;
        private static long replicationResourcePileStateSnapshotSequence;
        private static long replicationAgentCarryStateSnapshotSequence;
        private static long replicationAgentCharacterStateSnapshotSequence;
        private static long replicationBuildingStateSnapshotSequence;
        private static long replicationRegionOrderMarkerSnapshotSequence;
        private static long replicationGameTimeSnapshotSequence;
        private static long replicationHellosReceived;
        private static long replicationSnapshotsSent;
        private static long replicationSnapshotsReceived;
        private static long replicationSnapshotsApplied;
        private static long replicationIntentsSent;
        private static long replicationIntentsReceived;
        private static long replicationIntentsApplied;
        private static long replicationCommandAcksSent;
        private static long replicationCommandAcksReceived;
        private static long replicationRegionOrderStatesSent;
        private static long replicationRegionOrderStatesReceived;
        private static long replicationRegionOrderStatesApplied;
        private static long replicationRegionOrderStateSequence;
        private static long replicationWorldObjectDeltasSent;
        private static long replicationWorldObjectDeltasReceived;
        private static long replicationWorldObjectDeltasApplied;
        private static long replicationWorldObjectDeltaSequence;
        private static long replicationWorldObjectDeltaAcksSent;
        private static long replicationWorldObjectDeltaAcksReceived;
        private static long replicationWorldObjectDeltaRetriesSent;
        private static long replicationWorldObjectDeltasCoalesced;
        private static long replicationWorldObjectDeltaApplyBudgetStops;
        private static long replicationResyncControlSequence;
        private static long replicationResyncControlsSent;
        private static long replicationResyncControlsReceived;
        private static bool replicationProofIntentSent;
        private static float replicationEarliestProofIntentRealtime;
        private static long replicationLastAppliedSnapshotSequence = -1L;
        private static int replicationLastRenderApplyFrame = -1;
        private static ReplicationTransformSnapshot? replicationPendingApplySnapshot;
        private static float replicationPendingApplySnapshotReceivedRealtime;
        private static long replicationLastValidatedSnapshotSequence = -1L;
        private static int replicationLastSnapshotEntities;
        private static int replicationLastSnapshotStableIds;
        private static int replicationLastSnapshotResolved;
        private static int replicationLastSnapshotMissing;
        private static int replicationLastCollectedEntities;
        private static int replicationLastCollectedStableIds;
        private static int replicationLastCollectedFallbackIds;
        private static string replicationLastCollectedFallbackSample = string.Empty;
        private static string replicationLastCollectedPositionSample = string.Empty;
        private static string replicationLastApplyPositionSample = string.Empty;
        private static string replicationLastIntentSummary = string.Empty;
        private static string replicationLastRegionOrderStateSummary = string.Empty;
        private static string replicationLastWorldObjectDeltaSummary = string.Empty;
        private static string replicationLastResyncSummary = string.Empty;
        private static string replicationLastGameTimeSummary = string.Empty;
        private static string replicationLastRegionOrderStateKey = string.Empty;
        private static float replicationLastRegionOrderStateRealtime;
        private static int replicationRegionOrderStateCaptureSuppressionDepth;
        private static int replicationLastApplyVisualMoving;
        private static readonly HashSet<string> replicationHostCommandIntentKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<ReplicationRegionOrderState> ReplicationRecentRegionOrderMarkerStates = new List<ReplicationRegionOrderState>();
        private const float ReplicationLocalCommandDuplicateWindowSeconds = 0.35f;
        private const float ReplicationRegionOrderDuplicateWindowSeconds = 3f;
        private const float ReplicationRegionOrderMarkerSnapshotSeconds = 3f;
        private const float ReplicationRegionOrderMarkerSnapshotRememberSeconds = 30f;
        private const int ReplicationRegionOrderMarkerSnapshotMaxStates = 64;

        private void TryStartReplicationRuntime()
        {
            TryLoadReplicationConfig(this);
            if (!replicationConfigEnabled || replicationRuntimeStartAttempted)
            {
                return;
            }

            replicationRuntimeStartAttempted = true;
            try
            {
                replicationTransport = new UdpNetworkTransport();
                if (replicationConfigHostMode)
                {
                    replicationTransport.StartHost(replicationConfigPort);
                }
                else
                {
                    replicationTransport.Connect(replicationConfigHost, replicationConfigPort);
                }

                replicationRuntimeStarted = true;
                replicationRemoteCompatibilityRefused = false;
                replicationLocalBuildHash = ComputeReplicationLocalBuildHashWithCapabilities();
                replicationNextHelloRealtime = 0f;
                replicationNextHelloLogRealtime = 0f;
                replicationNextSnapshotRealtime = 0f;
                replicationNextStatusLogRealtime = 0f;
                replicationNextSnapshotValidationRealtime = 0f;
                replicationEarliestProofIntentRealtime = Time.realtimeSinceStartup + Math.Max(0, replicationConfigProofIntentDelaySeconds);
                LogReplicationInfo("Going Cooperative replication runtime started mode="
                    + (replicationConfigHostMode ? "host" : "client")
                    + " agentPresentation="
                    + (replicationConfigSemanticAgentPresentation ? "semantic" : "legacy")
                    + " port="
                    + replicationConfigPort.ToString(CultureInfo.InvariantCulture)
                    + " protocol="
                    + ReplicationPayloadCodec.ProtocolVersion
                    + " buildHash="
                    + replicationLocalBuildHash);
            }
            catch (Exception ex)
            {
                replicationConfigEnabled = false;
                replicationRuntimeStarted = false;
                LogReplicationWarning("Going Cooperative replication runtime start failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private void UpdateReplicationRuntime()
        {
            var frame = Time.frameCount;
            if (replicationLastRuntimeUpdateFrame == frame)
            {
                return;
            }

            replicationLastRuntimeUpdateFrame = frame;
            UpdateReplicationPerfFpsProbe();

            if (multiplayerLoadingInProgress)
            {
                return;
            }

            TryStartReplicationRuntime();
            if (!replicationRuntimeStarted || replicationTransport == null)
            {
                return;
            }

            PumpReplicationTransport();
            SendReplicationHelloIfDue();
            if (replicationConfigHostMode)
            {
                // All host channels gate on the same handshake state. Previously only some
                // sends checked remoteHelloReceived, so a refused peer produced a confusing
                // half-alive session (game time and world deltas flowing, snapshots dead).
                if (replicationRemoteHelloReceived)
                {
                    SendHostTransformSnapshotIfDue();
                    SendHostReplicationResourceContainersIfDue();
                    SendHostReplicationResourcePileStateSnapshotIfDue();
                    SendHostReplicationAgentCarryStateSnapshotIfDue();
                    SendHostReplicationAgentActionHeartbeatIfDue();
                    SendHostReplicationAgentCharacterStateSnapshotIfDue();
                    SendHostReplicationBuildingStateSnapshotIfDue();
                    SendHostReplicationGameTimeSnapshotIfDue();
                    SendPendingReplicationWorldObjectDeltasIfDue();
                }
            }
            else
            {
                SendClientProofIntentIfDue();
                ProcessReplicationResourcePileLocationIndex();
                ProcessPendingReplicationWorldObjectDeltaApplies();
                ProcessPendingReplicationResourceContainerApplies();
                ProcessPendingReplicationResourcePileStateSnapshotApplies();
                TickReplicationHostDrivenGoapPlaybackAgents();
                ProcessReplicationPuppetActionStates();
                TickReplicationPuppetActionVisuals();
                ProcessPendingReplicationNeedsRepairs();
            }

            ProcessReplicationSemanticAgentMotionPresentation();
            ProcessReplicationSemanticAgentWorkPresentation();
            ProcessReplicationCombatPresentationExpiry();

            LogReplicationStatusIfDue();
        }

        private static void StopReplicationRuntime()
        {
            replicationTransport?.Stop();
            replicationTransport = null;
            replicationRuntimeStarted = false;
            replicationRuntimeStartAttempted = false;
            replicationRemoteHelloReceived = false;
            replicationRemoteCompatibilityRefused = false;
            replicationLocalBuildHash = string.Empty;
            ResetReplicationCombatRuntimeState();
            ResetReplicationSemanticAgentMotionPresentation();
            ResetReplicationSemanticAgentWorkPresentation();
            replicationNextHelloLogRealtime = 0f;
            replicationNextSnapshotValidationRealtime = 0f;
            replicationNextResourcePileStateSnapshotRealtime = 0f;
            replicationNextAgentCarryStateSnapshotRealtime = 0f;
            replicationNextAgentActionHeartbeatRealtime = 0f;
            replicationNextAgentCharacterStateSnapshotRealtime = 0f;
            replicationNextBuildingStateSnapshotRealtime = 0f;
            replicationNextRegionOrderMarkerSnapshotRealtime = 0f;
            replicationNextGameTimeSnapshotRealtime = 0f;
            replicationLastRuntimeUpdateFrame = -1;
            replicationHellosReceived = 0;
            replicationIntentSequence = 0;
            replicationResourcePileStateSnapshotSequence = 0;
            replicationAgentCarryStateSnapshotSequence = 0;
            replicationAgentCharacterStateSnapshotSequence = 0;
            replicationBuildingStateSnapshotSequence = 0;
            replicationRegionOrderMarkerSnapshotSequence = 0;
            replicationGameTimeSnapshotSequence = 0;
            replicationIntentsSent = 0;
            replicationIntentsReceived = 0;
            replicationIntentsApplied = 0;
            replicationCommandAcksSent = 0;
            replicationCommandAcksReceived = 0;
            replicationRegionOrderStatesSent = 0;
            replicationRegionOrderStatesReceived = 0;
            replicationRegionOrderStatesApplied = 0;
            replicationRegionOrderStateSequence = 0;
            replicationWorldObjectDeltasSent = 0;
            replicationWorldObjectDeltasReceived = 0;
            replicationWorldObjectDeltasApplied = 0;
            replicationWorldObjectDeltaSequence = 0;
            replicationWorldObjectDeltaAcksSent = 0;
            replicationWorldObjectDeltaAcksReceived = 0;
            replicationWorldObjectDeltaRetriesSent = 0;
            replicationWorldObjectDeltasCoalesced = 0;
            replicationWorldObjectDeltaApplyBudgetStops = 0;
            replicationResyncControlSequence = 0;
            replicationResyncControlsSent = 0;
            replicationResyncControlsReceived = 0;
            replicationProofIntentSent = false;
            replicationEarliestProofIntentRealtime = 0f;
            replicationLastCapturedLocalCommandHash = 0UL;
            replicationLastCapturedLocalCommandRealtime = 0f;
            replicationLastAppliedSnapshotSequence = -1L;
            replicationLastRenderApplyFrame = -1;
            replicationPendingApplySnapshot = null;
            replicationPendingApplySnapshotReceivedRealtime = 0f;
            replicationLastValidatedSnapshotSequence = -1L;
            replicationLastSnapshotEntities = 0;
            replicationLastSnapshotStableIds = 0;
            replicationLastSnapshotResolved = 0;
            replicationLastSnapshotMissing = 0;
            replicationLastCollectedEntities = 0;
            replicationLastCollectedStableIds = 0;
            replicationLastCollectedFallbackIds = 0;
            replicationLastCollectedFallbackSample = string.Empty;
            replicationLastCollectedPositionSample = string.Empty;
            replicationLastApplyPositionSample = string.Empty;
            replicationLastIntentSummary = string.Empty;
            replicationLastRegionOrderStateSummary = string.Empty;
            replicationLastWorldObjectDeltaSummary = string.Empty;
            replicationLastResyncSummary = string.Empty;
            replicationLastGameTimeSummary = string.Empty;
            replicationLastRegionOrderStateKey = string.Empty;
            replicationLastRegionOrderStateRealtime = 0f;
            replicationRegionOrderStateCaptureSuppressionDepth = 0;
            ReplicationRecentRegionOrderMarkerStates.Clear();
            replicationLastApplyVisualMoving = 0;
            replicationLastRegionOrderType = string.Empty;
            replicationLastRegionOrderRealtime = 0f;
            replicationLastRegionSelectionValid = false;
            replicationLastRegionStartX = 0;
            replicationLastRegionStartY = 0;
            replicationLastRegionStartZ = 0;
            replicationLastRegionEndX = 0;
            replicationLastRegionEndY = 0;
            replicationLastRegionEndZ = 0;
            replicationLastPrimaryPlantRegionValid = false;
            replicationLastPrimaryPlantRegionOrderType = string.Empty;
            replicationLastPrimaryPlantRegionRealtime = 0f;
            replicationLastPrimaryPlantRegionStartX = 0;
            replicationLastPrimaryPlantRegionStartY = 0;
            replicationLastPrimaryPlantRegionStartZ = 0;
            replicationLastPrimaryPlantRegionEndX = 0;
            replicationLastPrimaryPlantRegionEndY = 0;
            replicationLastPrimaryPlantRegionEndZ = 0;
            replicationLastAreaOrderType = string.Empty;
            replicationLastAreaOrderSubType = string.Empty;
            replicationLastAreaOrderRealtime = 0f;
            replicationNextRegionSelectionLogRealtime = 0f;
            replicationHostCommandIntentKeys.Clear();
            replicationPendingWorldObjectDeltas.Clear();
            replicationClientAppliedWorldObjectDeltaSequences.Clear();
            ReplicationClientPriorityWorldObjectDeltaApplies.Clear();
            ReplicationClientPendingWorldObjectDeltaApplies.Clear();
            ReplicationClientCoalescableWorldObjectDeltaApplies.Clear();
            ReplicationClientQueuedWorldObjectDeltaSequences.Clear();
            ReplicationAgentProgressOwnerByBar.Clear();
            ReplicationAgentProgressLastPermilleByKey.Clear();
            ReplicationAgentProgressLoggedOwnerKeys.Clear();
            ReplicationWorldObjectDeltaAppliedSpawnKeys.Clear();
            ReplicationWorldObjectDeltaRecentSpawnLocationAt.Clear();
            ReplicationResourcePileStateSnapshotContexts.Clear();
            ReplicationBuildingStateSnapshotContexts.Clear();
            ReplicationPendingResourcePileStateSnapshotApplies.Clear();
            ReplicationPendingResourcePileStateSnapshot = null;
            replicationLastResourcePileStateSnapshotSignatureValid = false;
            replicationLastResourcePileStateSnapshotSignature = 0UL;
            replicationLastBuildingStateSnapshotSignatureValid = false;
            replicationLastBuildingStateSnapshotSignature = 0UL;
            replicationDriftSignaturesMatched = 0;
            replicationDriftSignaturesMismatched = 0;
            replicationLastDriftSummary = string.Empty;
            replicationClientResourcePileStateSnapshotApplyReadyRealtime = 0f;
            ReplicationAgentCarryResourceBlueprintByEntityId.Clear();
            ReplicationAgentCarryResourceAmountByEntityId.Clear();
            ReplicationAgentActionStatusByEntityId.Clear();
            ReplicationLastAgentCharacterStateSignatureByEntityId.Clear();
            ReplicationClientAgentCharacterStateByEntityId.Clear();
            ReplicationPuppetActionStateByEntityId.Clear();
            ReplicationPuppetActionHandItemByEntityId.Clear();
            ClearReplicationPuppetActionHandProps();
            ReplicationPendingCarryInferenceByBlueprintId.Clear();
            ReplicationPendingCarryDropByEntityId.Clear();
            ClearReplicationHostIdentityMap();
            ClearReplicationAgentCarryResourceVisuals();
            ClearReplicationResourceContainerState();
            replicationVisualLocomotionByEntityId.Clear();
            replicationAnimatorParameterSupportByInstanceId.Clear();
            ClearReplicationPresentationSmoothing();
            ClearReplicationNeedsState();
            ClearReplicationGameTimeSyncCache();
        }

        private void PumpReplicationTransport()
        {
            if (replicationTransport == null)
            {
                return;
            }

            while (replicationTransport.TryReceive(out var envelope))
            {
                try
                {
                switch (envelope.Kind)
                {
                    case TransportMessageKind.ReplicationHello:
                        HandleReplicationHello(envelope);
                        break;
                    case TransportMessageKind.ReplicationTransformSnapshot:
                        HandleReplicationTransformSnapshot(envelope);
                        break;
                    case TransportMessageKind.ReplicationIntent:
                        HandleReplicationCommandIntent(envelope);
                        break;
                    case TransportMessageKind.ReplicationCommandAck:
                        HandleReplicationCommandAck(envelope);
                        break;
                    case TransportMessageKind.ReplicationRegionOrderState:
                        HandleReplicationRegionOrderState(envelope);
                        break;
                    case TransportMessageKind.ReplicationWorldObjectDelta:
                        HandleReplicationWorldObjectDelta(envelope);
                        break;
                    case TransportMessageKind.ReplicationWorldObjectDeltaAck:
                        HandleReplicationWorldObjectDeltaAck(envelope);
                        break;
                    case TransportMessageKind.ReplicationResyncControl:
                        HandleReplicationResyncControl(envelope);
                        break;
                    case TransportMessageKind.ReplicationResourceContainerBatch:
                        HandleReplicationResourceContainerBatch(envelope);
                        break;
                }
                }
                catch (Exception ex)
                {
                    // One bad message must not abort the remaining receives this frame -
                    // that pattern silently starves later channels and looks like packet loss.
                    replicationPumpHandlerExceptions++;
                    if (Time.realtimeSinceStartup >= replicationNextPumpExceptionWarnRealtime)
                    {
                        replicationNextPumpExceptionWarnRealtime = Time.realtimeSinceStartup + 10f;
                        LogReplicationWarning("Going Cooperative replication pump handler threw kind="
                            + envelope.Kind
                            + " total="
                            + replicationPumpHandlerExceptions.ToString(CultureInfo.InvariantCulture)
                            + " error="
                            + ex.GetType().Name
                            + ":"
                            + ex.Message);
                    }
                }
            }
        }

        private void SendReplicationHelloIfDue()
        {
            if (replicationTransport == null || Time.realtimeSinceStartup < replicationNextHelloRealtime)
            {
                return;
            }

            replicationNextHelloRealtime = Time.realtimeSinceStartup + 1f;
            if (replicationConfigHostMode && !replicationRemoteHelloReceived)
            {
                return;
            }

            try
            {
                var mode = replicationConfigHostMode ? "host" : "client";
                var peerId = replicationConfigHostMode ? ReplicationHostPeerId : ReplicationClientPeerId;
                replicationTransport.Send(ReplicationPayloadCodec.ForHello(
                    peerId,
                    new ReplicationHello(peerId, mode, ReplicationPayloadCodec.ProtocolVersion, GetReplicationLocalBuildHash())));
            }
            catch (InvalidOperationException)
            {
                // Host has no client endpoint until the first client packet arrives.
            }
        }

        private void HandleReplicationHello(TransportEnvelope envelope)
        {
            if (!ReplicationPayloadCodec.TryReadHello(envelope, out var hello, out var error) || hello == null)
            {
                LogReplicationWarning("Going Cooperative replication hello decode failed error=" + error);
                return;
            }

            if (!IsReplicationHelloCompatible(hello, out var compatibilityError))
            {
                replicationRemoteHelloReceived = false;
                replicationRemoteCompatibilityRefused = true;
                LogReplicationWarning("Going Cooperative replication hello refused peer="
                    + hello.PeerId
                    + " mode="
                    + hello.Mode
                    + " remoteProtocol="
                    + hello.ProtocolVersion
                    + " localProtocol="
                    + ReplicationPayloadCodec.ProtocolVersion
                    + " remoteBuildHash="
                    + hello.BuildHash
                    + " localBuildHash="
                    + GetReplicationLocalBuildHash()
                    + " reason="
                    + compatibilityError);
                return;
            }

            replicationRemoteCompatibilityRefused = false;
            replicationRemoteHelloReceived = true;
            replicationHellosReceived++;
            if (replicationHellosReceived == 1 || Time.realtimeSinceStartup >= replicationNextHelloLogRealtime)
            {
                replicationNextHelloLogRealtime = Time.realtimeSinceStartup + 30f;
                LogReplicationInfo("Going Cooperative replication hello received peer="
                    + hello.PeerId
                    + " mode="
                    + hello.Mode
                    + " protocol="
                    + hello.ProtocolVersion
                    + " buildHash="
                    + hello.BuildHash
                    + " count="
                    + replicationHellosReceived.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static bool IsReplicationHelloCompatible(ReplicationHello hello, out string error)
        {
            if (!string.Equals(hello.ProtocolVersion, ReplicationPayloadCodec.ProtocolVersion, StringComparison.Ordinal))
            {
                error = "protocol-version-mismatch";
                return false;
            }

            var localBuildHash = GetReplicationLocalBuildHash();
            var localHasAgentPresentationCapability = TryReadReplicationAgentPresentationCapability(
                localBuildHash,
                out var localAgentPresentationEnabled,
                out var localAgentPresentationWireVersion);
            var remoteHasAgentPresentationCapability = TryReadReplicationAgentPresentationCapability(
                hello.BuildHash,
                out var remoteAgentPresentationEnabled,
                out var remoteAgentPresentationWireVersion);
            if (localHasAgentPresentationCapability
                && remoteHasAgentPresentationCapability
                && localAgentPresentationEnabled != remoteAgentPresentationEnabled)
            {
                error = "agent-presentation-capability-mismatch local="
                    + (localAgentPresentationEnabled ? "1" : "0")
                    + " remote="
                    + (remoteAgentPresentationEnabled ? "1" : "0");
                return false;
            }

            if (localHasAgentPresentationCapability
                && remoteHasAgentPresentationCapability
                && (localAgentPresentationEnabled || remoteAgentPresentationEnabled)
                && !string.Equals(localAgentPresentationWireVersion, remoteAgentPresentationWireVersion, StringComparison.Ordinal))
            {
                error = "agent-presentation-format-mismatch local="
                    + localAgentPresentationWireVersion
                    + " remote="
                    + remoteAgentPresentationWireVersion;
                return false;
            }

            if (replicationConfigSemanticAgentPresentation && !remoteHasAgentPresentationCapability)
            {
                error = "agent-presentation-capability-missing remote=<legacy>";
                return false;
            }

            var localHasCombatCapabilities = TryReadReplicationCombatCapabilityFingerprint(localBuildHash, out var localCombatCapabilities);
            var remoteHasCombatCapabilities = TryReadReplicationCombatCapabilityFingerprint(hello.BuildHash, out var remoteCombatCapabilities);
            if (localHasCombatCapabilities
                && remoteHasCombatCapabilities
                && !string.Equals(localCombatCapabilities, remoteCombatCapabilities, StringComparison.Ordinal))
            {
                error = "combat-capabilities-mismatch local=" + localCombatCapabilities + " remote=" + remoteCombatCapabilities;
                return false;
            }

            // Build-hash mismatch warns but does not refuse. A hard refusal here turns a
            // slightly stale staged dll into a silent one-way break: the host flips
            // remoteHelloReceived=false, which stops hello/snapshot/pile sends while
            // ungated channels keep flowing - indistinguishable from partial network
            // failure. Protocol version remains the hard wire-format gate.
            if (!string.Equals(hello.BuildHash, localBuildHash, StringComparison.OrdinalIgnoreCase))
            {
                WarnReplicationBuildHashMismatchIfDue(hello);
            }

            error = string.Empty;
            return true;
        }

        private static void WarnReplicationBuildHashMismatchIfDue(ReplicationHello hello)
        {
            if (Time.realtimeSinceStartup < replicationNextBuildHashMismatchWarnRealtime)
            {
                return;
            }

            replicationNextBuildHashMismatchWarnRealtime = Time.realtimeSinceStartup + 30f;
            var current = instance;
            if (ReferenceEquals(current, null))
            {
                return;
            }

            current.LogReplicationWarning("Going Cooperative replication BUILD HASH MISMATCH (session continues) peer="
                + hello.PeerId
                + " remoteBuildHash="
                + hello.BuildHash
                + " localBuildHash="
                + GetReplicationLocalBuildHash()
                + " restage the remote build to rule out cross-version bugs");
        }

        private static string GetReplicationLocalBuildHash()
        {
            if (string.IsNullOrEmpty(replicationLocalBuildHash))
            {
                replicationLocalBuildHash = ComputeReplicationLocalBuildHashWithCapabilities();
            }

            return replicationLocalBuildHash;
        }

        private static string ComputeReplicationLocalBuildHashWithCapabilities()
        {
            return ComputeReplicationLocalBuildHash()
                + "|agent="
                + FormatReplicationAgentPresentationCapability()
                + "|combat="
                + FormatReplicationCombatCapabilityFingerprint();
        }

        private static string FormatReplicationAgentPresentationCapability()
        {
            return (replicationConfigSemanticAgentPresentation ? "1" : "0")
                + ":"
                + ReplicationEntityMotionMetadata.WireVersion;
        }

        private static bool TryReadReplicationAgentPresentationCapability(
            string buildHash,
            out bool enabled,
            out string wireVersion)
        {
            enabled = false;
            wireVersion = string.Empty;
            const string marker = "|agent=";
            var markerIndex = buildHash.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return false;

            var valueIndex = markerIndex + marker.Length;
            if (valueIndex >= buildHash.Length) return false;
            var value = buildHash[valueIndex];
            if (value != '0' && value != '1') return false;
            if (valueIndex + 2 >= buildHash.Length || buildHash[valueIndex + 1] != ':') return false;

            var endIndex = buildHash.IndexOf('|', valueIndex + 2);
            wireVersion = endIndex >= 0
                ? buildHash.Substring(valueIndex + 2, endIndex - (valueIndex + 2))
                : buildHash.Substring(valueIndex + 2);
            if (string.IsNullOrWhiteSpace(wireVersion))
            {
                wireVersion = string.Empty;
                return false;
            }

            enabled = value == '1';
            return true;
        }

        private static string FormatReplicationCombatCapabilityFingerprint()
        {
            return (replicationConfigCombatReplication ? "1" : "0")
                + (replicationConfigCombatDraftCommands ? "1" : "0")
                + (replicationConfigCombatAttackCommands ? "1" : "0")
                + (replicationConfigCombatStateReplication ? "1" : "0")
                + (replicationConfigCombatHealthReplication ? "1" : "0")
                + (replicationConfigCombatHealthDetailReplication ? "1" : "0")
                + (replicationConfigCombatDeathReplication ? "1" : "0")
                + (replicationConfigCombatPresentationReplication ? "1" : "0")
                + (replicationConfigCombatProjectileReplication ? "1" : "0")
                + (replicationConfigCombatExternalAgentLifecycle ? "1" : "0");
        }

        private static bool TryReadReplicationCombatCapabilityFingerprint(string buildHash, out string fingerprint)
        {
            fingerprint = string.Empty;
            const string marker = "|combat=";
            var markerIndex = buildHash.LastIndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return false;
            fingerprint = buildHash.Substring(markerIndex + marker.Length);
            if (fingerprint.Length != 10) return false;
            for (var i = 0; i < fingerprint.Length; i++)
            {
                if (fingerprint[i] != '0' && fingerprint[i] != '1') return false;
            }
            return true;
        }

        private static string ComputeReplicationLocalBuildHash()
        {
            try
            {
                var assembly = typeof(GoingCooperativePlugin).Assembly;
                var location = assembly.Location;
                if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
                {
                    using (var stream = File.OpenRead(location))
                    using (var sha256 = SHA256.Create())
                    {
                        return FormatReplicationHashBytes(sha256.ComputeHash(stream));
                    }
                }

                var moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N");
                return "mvid-" + moduleVersionId;
            }
            catch (Exception ex)
            {
                return "unavailable-" + ex.GetType().Name;
            }
        }

        private static string FormatReplicationHashBytes(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private void RequestReplicationHostSaveResync(string source)
        {
            if (!replicationConfigEnabled
                || replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null)
            {
                replicationLastResyncSummary = "request-skipped unavailable source=" + source;
                LogReplicationWarning("Going Cooperative replication resync request skipped unavailable source=" + source);
                return;
            }

            var requestId = "client-"
                + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                + "-"
                + (++replicationResyncControlSequence).ToString(CultureInfo.InvariantCulture);
            SendReplicationResyncControl(
                ReplicationClientPeerId,
                "Request",
                requestId,
                string.Empty,
                string.Empty,
                0L,
                "source=" + source + " mode=control-scaffold");
        }

        private void DrawReplicationResyncControlGui()
        {
            if (!replicationConfigEnabled || replicationConfigHostMode)
            {
                return;
            }

            var top = replicationConfigMultiplayerMenuEnabled ? 382 : 120;
            GUI.Box(new Rect(16, top, 260, 76), "Going Cooperative Resync");
            if (GUI.Button(new Rect(28, top + 26, 140, 24), "Resync From Host"))
            {
                RequestReplicationHostSaveResync("gui-button");
            }

            GUI.Label(
                new Rect(28, top + 52, 236, 18),
                string.IsNullOrEmpty(replicationLastResyncSummary) ? "Status: ready" : "Status: " + TrimFingerprintText(replicationLastResyncSummary, 90));
        }

        private void SendReplicationResyncControl(
            string senderId,
            string phase,
            string requestId,
            string saveId,
            string hash,
            long byteLength,
            string detail)
        {
            if (replicationTransport == null)
            {
                return;
            }

            var control = new ReplicationResyncControl(
                ++replicationResyncControlSequence,
                Time.realtimeSinceStartup,
                phase,
                requestId,
                saveId,
                hash,
                byteLength,
                detail);
            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForResyncControl(senderId, control));
                replicationResyncControlsSent++;
                replicationLastResyncSummary = "sent " + FormatReplicationResyncControl(control);
                LogReplicationInfo("Going Cooperative replication resync control sent " + FormatReplicationResyncControl(control));
            }
            catch (Exception ex)
            {
                replicationLastResyncSummary = "send-failed " + ex.GetType().Name + ":" + ex.Message;
                LogReplicationWarning("Going Cooperative replication resync control send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " "
                    + FormatReplicationResyncControl(control));
            }
        }

        private void HandleReplicationResyncControl(TransportEnvelope envelope)
        {
            replicationResyncControlsReceived++;
            if (!ReplicationPayloadCodec.TryReadResyncControl(envelope, out var control, out var error) || control == null)
            {
                replicationLastResyncSummary = "decode-failed " + error;
                LogReplicationWarning("Going Cooperative replication resync control decode failed error=" + error);
                return;
            }

            if (replicationConfigHostMode)
            {
                HandleReplicationResyncControlOnHost(control);
                return;
            }

            replicationLastResyncSummary = "received " + FormatReplicationResyncControl(control);
            LogReplicationInfo("Going Cooperative replication resync control received " + FormatReplicationResyncControl(control));
        }

        private void HandleReplicationResyncControlOnHost(ReplicationResyncControl control)
        {
            replicationLastResyncSummary = "host-received " + FormatReplicationResyncControl(control);
            LogReplicationInfo("Going Cooperative replication resync control host received " + FormatReplicationResyncControl(control));
            if (!string.Equals(control.Phase, "Request", StringComparison.Ordinal))
            {
                return;
            }

            SendReplicationResyncControl(
                ReplicationHostPeerId,
                "Preparing",
                control.RequestId,
                string.Empty,
                string.Empty,
                0L,
                "control-scaffold save-api-not-wired");
            SendReplicationResyncControl(
                ReplicationHostPeerId,
                "Blocked",
                control.RequestId,
                string.Empty,
                string.Empty,
                0L,
                "blocked=pending-save-load-api-discovery next=scan-save-load-apis");
        }

        private static string FormatReplicationResyncControl(ReplicationResyncControl control)
        {
            return "sequence="
                + control.Sequence.ToString(CultureInfo.InvariantCulture)
                + " phase="
                + control.Phase
                + " requestId="
                + control.RequestId
                + " saveId="
                + control.SaveId
                + " hash="
                + control.Hash
                + " bytes="
                + control.ByteLength.ToString(CultureInfo.InvariantCulture)
                + " detail="
                + control.Detail;
        }

        private void SendHostTransformSnapshotIfDue()
        {
            if (!replicationConfigSendSnapshots
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || Time.realtimeSinceStartup < replicationNextSnapshotRealtime)
            {
                return;
            }

            var interval = 1f / Math.Max(1, replicationConfigSnapshotHz);
            replicationNextSnapshotRealtime = Time.realtimeSinceStartup + interval;
            var snapshot = CollectReplicationTransformSnapshot(++replicationSnapshotSequence, replicationConfigMaxSnapshotEntities);
            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForTransformSnapshot(ReplicationHostPeerId, snapshot));
                replicationSnapshotsSent++;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication snapshot send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private void SendClientProofIntentIfDue()
        {
            if (!replicationConfigSendProofIntent
                || replicationProofIntentSent
                || replicationConfigHostMode
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || Time.realtimeSinceStartup < replicationEarliestProofIntentRealtime
                || replicationSnapshotsReceived <= 0
                || replicationLastSnapshotEntities <= 0)
            {
                return;
            }

            if (!TryCreateReplicationProofIntent(out var intent, out var error) || intent == null)
            {
                replicationProofIntentSent = true;
                replicationLastIntentSummary = "proof-intent-create-failed " + error;
                LogReplicationWarning("Going Cooperative replication proof intent create failed intent="
                    + replicationConfigProofIntent
                    + " error="
                    + error);
                return;
            }

            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForCommandIntent(ReplicationClientPeerId, intent));
                replicationProofIntentSent = true;
                replicationIntentsSent++;
                replicationLastIntentSummary = "sent " + FormatRuntimeCommandSummary(intent.Command);
                LogReplicationInfo("Going Cooperative replication proof intent sent "
                    + FormatRuntimeCommandSummary(intent.Command)
                    + " proofIntent="
                    + replicationConfigProofIntent);
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication proof intent send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message);
            }
        }

        private static bool TryCreateReplicationProofIntent(out ReplicationCommandIntent? intent, out string error)
        {
            intent = null;
            error = string.Empty;
            if (string.Equals(replicationConfigProofIntent, "speed-normal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(replicationConfigProofIntent, "speed", StringComparison.OrdinalIgnoreCase))
            {
                intent = new ReplicationCommandIntent(new LockstepCommand(
                    ReplicationClientPeerId,
                    ++replicationIntentSequence,
                    0L,
                    CommandKind.Speed,
                    LockstepCommandPayloads.CreateSetSpeedNormalPayload()));
                return true;
            }

            error = "unsupported-proof-intent=" + replicationConfigProofIntent;
            return false;
        }

        private void HandleReplicationCommandIntent(TransportEnvelope envelope)
        {
            replicationIntentsReceived++;
            if (!ReplicationPayloadCodec.TryReadCommandIntent(envelope, out var intent, out var error) || intent == null)
            {
                replicationLastIntentSummary = "decode-failed " + error;
                LogReplicationWarning("Going Cooperative replication intent decode failed error=" + error);
                return;
            }

            var command = intent.Command;
            if (!replicationConfigHostMode)
            {
                replicationLastIntentSummary = "ignored-nonhost " + FormatRuntimeCommandSummary(command);
                LogReplicationWarning("Going Cooperative replication intent ignored on non-host "
                    + FormatRuntimeCommandSummary(command));
                return;
            }

            var commandKey = BuildReplicationCommandIntentKey(command);
            if (!replicationHostCommandIntentKeys.Add(commandKey))
            {
                replicationLastIntentSummary = "host-duplicate " + FormatRuntimeCommandSummary(command);
                SendReplicationCommandAck(command, accepted: true, duplicate: true, detail: "duplicate");
                LogReplicationInfo("Going Cooperative replication intent duplicate ignored "
                    + FormatRuntimeCommandSummary(command));
                return;
            }

            BeginReplicationRegionOrderStateCaptureSuppression();
            RuntimeCommandResult result;
            try
            {
                result = ApplyRuntimeCommand(this, command);
            }
            catch (Exception ex)
            {
                // An apply exception must not eat the ack - the client would retry into
                // the duplicate gate and see the command silently vanish. Ack invoked=no
                // with the error instead.
                result = new RuntimeCommandResult(
                    false,
                    "apply-exception " + ex.GetType().Name + ":" + ex.Message);
                LogReplicationWarning("Going Cooperative replication intent apply threw error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " "
                    + FormatRuntimeCommandSummary(command));
            }
            finally
            {
                EndReplicationRegionOrderStateCaptureSuppression();
            }

            if (result.Invoked)
            {
                replicationIntentsApplied++;
            }

            SendReplicationCommandAck(command, result.Invoked, duplicate: false, detail: result.Detail);
            SendReplicationRegionOrderStateIfSupported(command, result);
            SendReplicationBuildBlueprintResultDeltaIfSupported(command, result);
            SendReplicationManagementStateIfSupported(command, result);
            SendReplicationCombatStateIfSupported(command, result);

            replicationLastIntentSummary = "host-applied invoked="
                + (result.Invoked ? "yes" : "no")
                + " detail="
                + result.Detail
                + " "
                + FormatRuntimeCommandSummary(command);
            LogReplicationInfo("Going Cooperative replication intent host apply invoked="
                + (result.Invoked ? "yes" : "no")
                + " detail="
                + result.Detail
                + " "
                + FormatRuntimeCommandSummary(command));
        }

        private void SendReplicationCommandAck(LockstepCommand command, bool accepted, bool duplicate, string detail)
        {
            if (replicationTransport == null)
            {
                return;
            }

            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForCommandAck(
                    ReplicationHostPeerId,
                    new ReplicationCommandAck(command.PlayerId, command.Sequence, accepted, duplicate, detail)));
                replicationCommandAcksSent++;
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication command ack send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " "
                    + FormatRuntimeCommandSummary(command));
            }
        }

        private void HandleReplicationCommandAck(TransportEnvelope envelope)
        {
            if (!ReplicationPayloadCodec.TryReadCommandAck(envelope, out var ack, out var error) || ack == null)
            {
                LogReplicationWarning("Going Cooperative replication command ack decode failed error=" + error);
                return;
            }

            replicationCommandAcksReceived++;
            replicationLastIntentSummary = "ack accepted="
                + (ack.Accepted ? "yes" : "no")
                + " duplicate="
                + (ack.Duplicate ? "yes" : "no")
                + " player="
                + ack.PlayerId
                + " sequence="
                + ack.Sequence.ToString(CultureInfo.InvariantCulture)
                + " detail="
                + ack.Detail;
            LogReplicationInfo("Going Cooperative replication command ack received accepted="
                + (ack.Accepted ? "yes" : "no")
                + " duplicate="
                + (ack.Duplicate ? "yes" : "no")
                + " player="
                + ack.PlayerId
                + " sequence="
                + ack.Sequence.ToString(CultureInfo.InvariantCulture)
                + " detail="
                + ack.Detail);
        }

        private static string BuildReplicationCommandIntentKey(LockstepCommand command)
        {
            return command.PlayerId + ":" + command.Sequence.ToString(CultureInfo.InvariantCulture);
        }

        private void SendReplicationRegionOrderStateIfSupported(LockstepCommand command, RuntimeCommandResult result)
        {
            if (!result.Invoked
                || command.Kind != CommandKind.RegionOrder
                || !LockstepCommandPayloads.TryReadRegionOrderPayload(
                    command.PayloadJson,
                    out var orderType,
                    out var startX,
                    out var startY,
                    out var startZ,
                    out var endX,
                    out var endY,
                    out var endZ,
                    out var allowType,
                    out var areaType,
                    out var subType)
                || !IsReplicationRegionOrderStateSupported(orderType))
            {
                return;
            }

            SendReplicationRegionOrderState(
                orderType,
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                allowType,
                areaType,
                subType,
                result.Detail);
        }

        private void SendReplicationRegionOrderState(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            string areaType,
            string subType,
            string detail)
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || IsReplicationRegionOrderStateCaptureSuppressed()
                || !IsReplicationRegionOrderStateSupported(orderType)
                || ShouldSkipDuplicateReplicationRegionOrderState(orderType, startX, startY, startZ, endX, endY, endZ, allowType, areaType, subType))
            {
                return;
            }

            var state = new ReplicationRegionOrderState(
                ++replicationRegionOrderStateSequence,
                Time.realtimeSinceStartup,
                orderType,
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                allowType,
                areaType,
                subType,
                detail);

            RememberReplicationRegionOrderMarkerState(state);
            try
            {
                replicationTransport.Send(ReplicationPayloadCodec.ForRegionOrderState(ReplicationHostPeerId, state));
                replicationRegionOrderStatesSent++;
                replicationLastRegionOrderStateSummary = "sent " + FormatReplicationRegionOrderState(state);
                LogReplicationInfo("Going Cooperative replication region order state sent " + FormatReplicationRegionOrderState(state));
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative replication region order state send failed error="
                    + ex.GetType().Name
                    + ":"
                    + ex.Message
                    + " "
                    + FormatReplicationRegionOrderState(state));
            }
        }

        private static void BeginReplicationRegionOrderStateCaptureSuppression()
        {
            replicationRegionOrderStateCaptureSuppressionDepth++;
        }

        private static void EndReplicationRegionOrderStateCaptureSuppression()
        {
            if (replicationRegionOrderStateCaptureSuppressionDepth > 0)
            {
                replicationRegionOrderStateCaptureSuppressionDepth--;
            }
        }

        private static bool IsReplicationRegionOrderStateCaptureSuppressed()
        {
            return replicationRegionOrderStateCaptureSuppressionDepth > 0;
        }

        private static bool ShouldSkipDuplicateReplicationRegionOrderState(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            string areaType,
            string subType)
        {
            var key = (orderType ?? string.Empty)
                + "|"
                + startX.ToString(CultureInfo.InvariantCulture)
                + ","
                + startY.ToString(CultureInfo.InvariantCulture)
                + ","
                + startZ.ToString(CultureInfo.InvariantCulture)
                + "|"
                + endX.ToString(CultureInfo.InvariantCulture)
                + ","
                + endY.ToString(CultureInfo.InvariantCulture)
                + ","
                + endZ.ToString(CultureInfo.InvariantCulture)
                + "|"
                + (allowType ?? string.Empty)
                + "|"
                + (areaType ?? string.Empty)
                + "|"
                + (subType ?? string.Empty);
            var now = Time.realtimeSinceStartup;
            if (string.Equals(replicationLastRegionOrderStateKey, key, StringComparison.Ordinal)
                && now - replicationLastRegionOrderStateRealtime < ReplicationRegionOrderDuplicateWindowSeconds)
            {
                return true;
            }

            replicationLastRegionOrderStateKey = key;
            replicationLastRegionOrderStateRealtime = now;
            return false;
        }

        private void SendHostReplicationRegionOrderMarkerSnapshotIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null
                || replicationLastSnapshotEntities <= 0
                || Time.realtimeSinceStartup < replicationNextRegionOrderMarkerSnapshotRealtime)
            {
                return;
            }

            replicationNextRegionOrderMarkerSnapshotRealtime = Time.realtimeSinceStartup + ReplicationRegionOrderMarkerSnapshotSeconds;
            var due = CollectRecentReplicationRegionOrderMarkerStates();
            if (due.Count == 0)
            {
                return;
            }

            var snapshotId = ++replicationRegionOrderMarkerSnapshotSequence;
            var sent = 0;
            for (var i = 0; i < due.Count; i++)
            {
                var source = due[i];
                var state = new ReplicationRegionOrderState(
                    ++replicationRegionOrderStateSequence,
                    Time.realtimeSinceStartup,
                    source.OrderType,
                    source.StartX,
                    source.StartY,
                    source.StartZ,
                    source.EndX,
                    source.EndY,
                    source.EndZ,
                    source.AllowType,
                    source.AreaType,
                    source.SubType,
                    source.Detail
                        + " markerSnapshotId="
                        + snapshotId.ToString(CultureInfo.InvariantCulture)
                        + " markerIndex="
                        + i.ToString(CultureInfo.InvariantCulture)
                        + " markerCount="
                        + due.Count.ToString(CultureInfo.InvariantCulture)
                        + " source=authoritative-order-marker-snapshot");

                try
                {
                    replicationTransport.Send(ReplicationPayloadCodec.ForRegionOrderState(ReplicationHostPeerId, state));
                    replicationRegionOrderStatesSent++;
                    sent++;
                }
                catch (Exception ex)
                {
                    LogReplicationWarning("Going Cooperative replication region order marker snapshot send failed error="
                        + ex.GetType().Name
                        + ":"
                        + ex.Message
                        + " "
                        + FormatReplicationRegionOrderState(state));
                }
            }

            replicationLastRegionOrderStateSummary = "marker-snapshot-sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + sent.ToString(CultureInfo.InvariantCulture)
                + " cached="
                + due.Count.ToString(CultureInfo.InvariantCulture);
            LogReplicationInfo("Going Cooperative replication region order marker snapshot sent snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " sent="
                + sent.ToString(CultureInfo.InvariantCulture)
                + " cached="
                + due.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static void RememberReplicationRegionOrderMarkerState(ReplicationRegionOrderState state)
        {
            if (!IsReplicationMarkerRegionOrder(state.OrderType))
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            for (var i = ReplicationRecentRegionOrderMarkerStates.Count - 1; i >= 0; i--)
            {
                var existing = ReplicationRecentRegionOrderMarkerStates[i];
                if (now - existing.SentRealtime > ReplicationRegionOrderMarkerSnapshotRememberSeconds)
                {
                    ReplicationRecentRegionOrderMarkerStates.RemoveAt(i);
                    continue;
                }

                if (AreReplicationRegionOrderStatesSameMarker(existing, state))
                {
                    ReplicationRecentRegionOrderMarkerStates.RemoveAt(i);
                }
            }

            ReplicationRecentRegionOrderMarkerStates.Add(state);
            while (ReplicationRecentRegionOrderMarkerStates.Count > ReplicationRegionOrderMarkerSnapshotMaxStates)
            {
                ReplicationRecentRegionOrderMarkerStates.RemoveAt(0);
            }
        }

        private static List<ReplicationRegionOrderState> CollectRecentReplicationRegionOrderMarkerStates()
        {
            var now = Time.realtimeSinceStartup;
            var due = new List<ReplicationRegionOrderState>();
            for (var i = ReplicationRecentRegionOrderMarkerStates.Count - 1; i >= 0; i--)
            {
                var state = ReplicationRecentRegionOrderMarkerStates[i];
                if (now - state.SentRealtime > ReplicationRegionOrderMarkerSnapshotRememberSeconds)
                {
                    ReplicationRecentRegionOrderMarkerStates.RemoveAt(i);
                    continue;
                }

                due.Add(state);
            }

            due.Reverse();
            return due;
        }

        private static bool AreReplicationRegionOrderStatesSameMarker(ReplicationRegionOrderState left, ReplicationRegionOrderState right)
        {
            return string.Equals(left.OrderType, right.OrderType, StringComparison.Ordinal)
                && left.StartX == right.StartX
                && left.StartY == right.StartY
                && left.StartZ == right.StartZ
                && left.EndX == right.EndX
                && left.EndY == right.EndY
                && left.EndZ == right.EndZ
                && string.Equals(left.AllowType, right.AllowType, StringComparison.Ordinal)
                && string.Equals(left.AreaType, right.AreaType, StringComparison.Ordinal)
                && string.Equals(left.SubType, right.SubType, StringComparison.Ordinal);
        }

        private void HandleReplicationRegionOrderState(TransportEnvelope envelope)
        {
            if (!ReplicationPayloadCodec.TryReadRegionOrderState(envelope, out var state, out var error) || state == null)
            {
                LogReplicationWarning("Going Cooperative replication region order state decode failed error=" + error);
                return;
            }

            replicationRegionOrderStatesReceived++;
            if (replicationConfigHostMode)
            {
                replicationLastRegionOrderStateSummary = "ignored-on-host " + FormatReplicationRegionOrderState(state);
                return;
            }

            var command = new LockstepCommand(
                ReplicationHostPeerId,
                state.Sequence,
                0L,
                CommandKind.RegionOrder,
                LockstepCommandPayloads.CreateRegionOrderPayload(
                    state.OrderType,
                    state.StartX,
                    state.StartY,
                    state.StartZ,
                    state.EndX,
                    state.EndY,
                    state.EndZ,
                    state.AllowType,
                    state.AreaType,
                    state.SubType),
                null,
                state.StartX,
                state.StartY,
                state.StartZ);
            BeginReplicationRegionOrderStateCaptureSuppression();
            applyingRuntimeCommandDepth++;
            RuntimeCommandResult result;
            try
            {
                result = ApplyRuntimeCommand(this, command);
            }
            finally
            {
                applyingRuntimeCommandDepth--;
                EndReplicationRegionOrderStateCaptureSuppression();
            }

            if (result.Invoked)
            {
                replicationRegionOrderStatesApplied++;
            }

            replicationLastRegionOrderStateSummary = "applied invoked="
                + (result.Invoked ? "yes" : "no")
                + " detail="
                + result.Detail
                + " "
                + FormatReplicationRegionOrderState(state);
            LogReplicationInfo("Going Cooperative replication region order state applied invoked="
                + (result.Invoked ? "yes" : "no")
                + " detail="
                + result.Detail
                + " "
                + FormatReplicationRegionOrderState(state));
        }

        private static string FormatReplicationRegionOrderState(ReplicationRegionOrderState state)
        {
            return "sequence="
                + state.Sequence.ToString(CultureInfo.InvariantCulture)
                + " orderType="
                + state.OrderType
                + " start=Vec3Int("
                + state.StartX.ToString(CultureInfo.InvariantCulture)
                + ","
                + state.StartY.ToString(CultureInfo.InvariantCulture)
                + ","
                + state.StartZ.ToString(CultureInfo.InvariantCulture)
                + ") end=Vec3Int("
                + state.EndX.ToString(CultureInfo.InvariantCulture)
                + ","
                + state.EndY.ToString(CultureInfo.InvariantCulture)
                + ","
                + state.EndZ.ToString(CultureInfo.InvariantCulture)
                + ") detail="
                + state.Detail;
        }

        private void HandleReplicationTransformSnapshot(TransportEnvelope envelope)
        {
            if (!ReplicationPayloadCodec.TryReadTransformSnapshot(envelope, out var snapshot, out var error) || snapshot == null)
            {
                LogReplicationWarning("Going Cooperative replication transform snapshot decode failed error=" + error);
                return;
            }

            replicationSnapshotsReceived++;
            replicationLastSnapshotEntities = snapshot.Entities.Count;
            UpdateReplicationHostMovementTargets(snapshot);
            if (replicationConfigApplySnapshots)
            {
                replicationPendingApplySnapshot = snapshot;
                replicationPendingApplySnapshotReceivedRealtime = Time.realtimeSinceStartup;
                if (replicationConfigSmoothReplicatedMovement)
                {
                    BufferReplicationTransformSnapshot(snapshot, replicationPendingApplySnapshotReceivedRealtime);
                }
            }
            else
            {
                ValidateReplicationTransformSnapshotIfDue(snapshot);
            }
        }

        private void PreCullReplicationRuntime()
        {
            if (!replicationRuntimeStarted || !replicationConfigApplySnapshots || replicationPendingApplySnapshot == null)
            {
                return;
            }

            if (replicationLastRenderApplyFrame == Time.frameCount)
            {
                return;
            }

            var snapshot = replicationPendingApplySnapshot;
            if (!replicationConfigSmoothReplicatedMovement
                && Time.realtimeSinceStartup - replicationPendingApplySnapshotReceivedRealtime > GetReplicationSnapshotVisualHoldSeconds())
            {
                replicationPendingApplySnapshot = null;
                return;
            }

            replicationLastRenderApplyFrame = Time.frameCount;
            replicationLastAppliedSnapshotSequence = snapshot.Sequence;
            replicationSnapshotsApplied += replicationConfigSmoothReplicatedMovement
                ? ApplyBufferedReplicationTransformSnapshot(snapshot)
                : ApplyReplicationTransformSnapshot(snapshot);
        }

        private static float GetReplicationSnapshotVisualHoldSeconds()
        {
            var snapshotHz = Math.Max(1, replicationConfigSnapshotHz);
            return Mathf.Clamp(3f / snapshotHz, 0.2f, 0.5f);
        }

        private void ValidateReplicationTransformSnapshotIfDue(ReplicationTransformSnapshot snapshot)
        {
            if (!replicationConfigValidateSnapshots || Time.realtimeSinceStartup < replicationNextSnapshotValidationRealtime)
            {
                return;
            }

            replicationNextSnapshotValidationRealtime = Time.realtimeSinceStartup + replicationConfigSnapshotValidationSeconds;
            var validation = ValidateReplicationTransformSnapshot(snapshot);
            replicationLastValidatedSnapshotSequence = snapshot.Sequence;
            replicationLastSnapshotStableIds = validation.StableIds;
            replicationLastSnapshotResolved = validation.Resolved;
            replicationLastSnapshotMissing = validation.Missing;
            LogReplicationInfo("Going Cooperative replication shadow apply snapshot="
                + snapshot.Sequence.ToString(CultureInfo.InvariantCulture)
                + " entities="
                + validation.Total.ToString(CultureInfo.InvariantCulture)
                + " stableIds="
                + validation.StableIds.ToString(CultureInfo.InvariantCulture)
                + " resolved="
                + validation.Resolved.ToString(CultureInfo.InvariantCulture)
                + " missing="
                + validation.Missing.ToString(CultureInfo.InvariantCulture));
        }

        private static long GetReplicationTransportDecodeFailures()
        {
            var transport = replicationTransport;
            return transport != null ? transport.DecodeFailures : 0L;
        }

        private static long GetReplicationTransportChunkFailures()
        {
            var transport = replicationTransport;
            return transport != null ? transport.ChunkFailures : 0L;
        }

        private void WarnReplicationTransportDropsIfDue()
        {
            var decodeFailures = GetReplicationTransportDecodeFailures();
            var chunkFailures = GetReplicationTransportChunkFailures();
            if (decodeFailures == replicationLastTransportDecodeFailures
                && chunkFailures == replicationLastTransportChunkFailures)
            {
                return;
            }

            if (Time.realtimeSinceStartup < replicationNextTransportDropWarnRealtime)
            {
                return;
            }

            replicationNextTransportDropWarnRealtime = Time.realtimeSinceStartup + 30f;
            LogReplicationWarning("Going Cooperative replication transport dropped datagrams decodeDrops="
                + decodeFailures.ToString(CultureInfo.InvariantCulture)
                + " (+" + (decodeFailures - replicationLastTransportDecodeFailures).ToString(CultureInfo.InvariantCulture) + ")"
                + " chunkDrops="
                + chunkFailures.ToString(CultureInfo.InvariantCulture)
                + " (+" + (chunkFailures - replicationLastTransportChunkFailures).ToString(CultureInfo.InvariantCulture) + ")");
            replicationLastTransportDecodeFailures = decodeFailures;
            replicationLastTransportChunkFailures = chunkFailures;
        }

        private void LogReplicationStatusIfDue()
        {
            if (!replicationConfigLogSnapshots || Time.realtimeSinceStartup < replicationNextStatusLogRealtime)
            {
                return;
            }

            replicationNextStatusLogRealtime = Time.realtimeSinceStartup + 10f;
            WarnReplicationTransportDropsIfDue();
            LogReplicationInfo("Going Cooperative replication status mode="
                + (replicationConfigHostMode ? "host" : "client")
                + " agentPresentation="
                + (replicationConfigSemanticAgentPresentation ? "semantic" : "legacy")
                + " remoteHello="
                + replicationRemoteHelloReceived
                + " compatibilityRefused="
                + replicationRemoteCompatibilityRefused
                + " protocol="
                + ReplicationPayloadCodec.ProtocolVersion
                + " buildHash="
                + GetReplicationLocalBuildHash()
                + " transportDecodeDrops="
                + GetReplicationTransportDecodeFailures().ToString(CultureInfo.InvariantCulture)
                + " transportChunkDrops="
                + GetReplicationTransportChunkFailures().ToString(CultureInfo.InvariantCulture)
                + " pumpExceptions="
                + replicationPumpHandlerExceptions.ToString(CultureInfo.InvariantCulture)
                + " "
                + FormatReplicationHostMovementCounters()
                + " sentSnapshots="
                + replicationSnapshotsSent.ToString(CultureInfo.InvariantCulture)
                + " receivedSnapshots="
                + replicationSnapshotsReceived.ToString(CultureInfo.InvariantCulture)
                + " appliedEntities="
                + replicationSnapshotsApplied.ToString(CultureInfo.InvariantCulture)
                + " intentsSent="
                + replicationIntentsSent.ToString(CultureInfo.InvariantCulture)
                + " intentsReceived="
                + replicationIntentsReceived.ToString(CultureInfo.InvariantCulture)
                + " intentsApplied="
                + replicationIntentsApplied.ToString(CultureInfo.InvariantCulture)
                + " acksSent="
                + replicationCommandAcksSent.ToString(CultureInfo.InvariantCulture)
                + " acksReceived="
                + replicationCommandAcksReceived.ToString(CultureInfo.InvariantCulture)
                + " regionStatesSent="
                + replicationRegionOrderStatesSent.ToString(CultureInfo.InvariantCulture)
                + " regionStatesReceived="
                + replicationRegionOrderStatesReceived.ToString(CultureInfo.InvariantCulture)
                + " regionStatesApplied="
                + replicationRegionOrderStatesApplied.ToString(CultureInfo.InvariantCulture)
                + " worldDeltasSent="
                + replicationWorldObjectDeltasSent.ToString(CultureInfo.InvariantCulture)
                + " worldDeltasReceived="
                + replicationWorldObjectDeltasReceived.ToString(CultureInfo.InvariantCulture)
                + " worldDeltasApplied="
                + replicationWorldObjectDeltasApplied.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaAcksSent="
                + replicationWorldObjectDeltaAcksSent.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaAcksReceived="
                + replicationWorldObjectDeltaAcksReceived.ToString(CultureInfo.InvariantCulture)
                + " worldDeltaRetriesSent="
                + replicationWorldObjectDeltaRetriesSent.ToString(CultureInfo.InvariantCulture)
                + " hostIdMap="
                + ReplicationLocalObjectByHostId.Count.ToString(CultureInfo.InvariantCulture)
                + " hostIdHits="
                + replicationHostIdentityHits.ToString(CultureInfo.InvariantCulture)
                + " hostIdMisses="
                + replicationHostIdentityMisses.ToString(CultureInfo.InvariantCulture)
                + " hostIdRegisters="
                + replicationHostIdentityRegistrations.ToString(CultureInfo.InvariantCulture)
                + " hostIdRemoves="
                + replicationHostIdentityRemovals.ToString(CultureInfo.InvariantCulture)
                + " driftMatched="
                + replicationDriftSignaturesMatched.ToString(CultureInfo.InvariantCulture)
                + " driftMismatched="
                + replicationDriftSignaturesMismatched.ToString(CultureInfo.InvariantCulture)
                + " resyncSent="
                + replicationResyncControlsSent.ToString(CultureInfo.InvariantCulture)
                + " resyncReceived="
                + replicationResyncControlsReceived.ToString(CultureInfo.InvariantCulture)
                + " lastSnapshotEntities="
                + replicationLastSnapshotEntities.ToString(CultureInfo.InvariantCulture)
                + " shadowSnapshot="
                + replicationLastValidatedSnapshotSequence.ToString(CultureInfo.InvariantCulture)
                + " shadowStableIds="
                + replicationLastSnapshotStableIds.ToString(CultureInfo.InvariantCulture)
                + " shadowResolved="
                + replicationLastSnapshotResolved.ToString(CultureInfo.InvariantCulture)
                + " shadowMissing="
                + replicationLastSnapshotMissing.ToString(CultureInfo.InvariantCulture)
                + " collectedEntities="
                + replicationLastCollectedEntities.ToString(CultureInfo.InvariantCulture)
                + " collectedStableIds="
                + replicationLastCollectedStableIds.ToString(CultureInfo.InvariantCulture)
                + " collectedFallbackIds="
                + replicationLastCollectedFallbackIds.ToString(CultureInfo.InvariantCulture)
                + " collectedFallbackSample="
                + replicationLastCollectedFallbackSample
                + " collectedPositionSample="
                + replicationLastCollectedPositionSample
                + " applyPositionSample="
                + replicationLastApplyPositionSample
                + " visualMoving="
                + replicationLastApplyVisualMoving.ToString(CultureInfo.InvariantCulture)
                + " lastIntent="
                + (string.IsNullOrEmpty(replicationLastIntentSummary) ? "<none>" : replicationLastIntentSummary)
                + " lastRegionState="
                + (string.IsNullOrEmpty(replicationLastRegionOrderStateSummary) ? "<none>" : replicationLastRegionOrderStateSummary)
                + " lastWorldDelta="
                + (string.IsNullOrEmpty(replicationLastWorldObjectDeltaSummary) ? "<none>" : replicationLastWorldObjectDeltaSummary)
                + " lastHostIdentity="
                + (string.IsNullOrEmpty(replicationLastHostIdentitySummary) ? "<none>" : replicationLastHostIdentitySummary)
                + " lastDrift="
                + (string.IsNullOrEmpty(replicationLastDriftSummary) ? "<none>" : replicationLastDriftSummary)
                + " lastGameTime="
                + (string.IsNullOrEmpty(replicationLastGameTimeSummary) ? "<none>" : replicationLastGameTimeSummary)
                + " "
                + FormatReplicationSemanticAgentMotionStatus()
                + " "
                + FormatReplicationSemanticWorkStatus()
                + " lastResync="
                + (string.IsNullOrEmpty(replicationLastResyncSummary) ? "<none>" : replicationLastResyncSummary));
        }
    }
}
