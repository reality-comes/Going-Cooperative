using System;
using System.Globalization;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        internal const string ReplicationTraderInteractionResultDeltaKind = "TraderTradeOpenResult";
        private const float ReplicationTraderInteractionRequestLeaseSeconds = 15f;
        private static PendingReplicationTraderInteraction? replicationPendingTraderInteraction;

        private sealed class PendingReplicationTraderInteraction
        {
            public string RequestId = string.Empty;
            public string TraderEntityId = string.Empty;
            public string WorkerEntityId = string.Empty;
            public float ExpiresRealtime;
        }

        private static bool ReplicationTraderTradeMenuClickPrefix(object __instance)
        {
            if (replicationConfigHostMode || replicationTraderPartyApplicationDepth > 0 || __instance == null) return true;
            var traderOwner = AccessTools.Field(__instance.GetType(), "selectedHuman")?.GetValue(__instance);
            if (traderOwner == null || !TryGetReplicationTraderPartyNetworkId(traderOwner, out _)) return true;

            var isLeaving = AccessTools.Property(traderOwner.GetType(), "IsLeaving")?.GetValue(traderOwner, null);
            var friendly = AccessTools.Method(traderOwner.GetType(), "IsFriendlyFaction", Type.EmptyTypes)?.Invoke(traderOwner, null);
            if (isLeaving is bool leaving && leaving) return true;
            if (friendly is bool isFriendly && !isFriendly) return true;

            var selectedWorker = AccessTools.Method(__instance.GetType(), "GetSelectedWorker", Type.EmptyTypes)
                ?.Invoke(__instance, null);
            var workerBehaviour = selectedWorker == null
                ? null
                : AccessTools.Property(selectedWorker.GetType(), "WorkerBehaviour")?.GetValue(selectedWorker, null);
            if (!TryRequestReplicationTraderInteraction(
                    AccessTools.Property(traderOwner.GetType(), "TraderBehaviour")?.GetValue(traderOwner, null)
                        ?? AccessTools.Field(traderOwner.GetType(), "traderBehaviour")?.GetValue(traderOwner)
                        ?? traderOwner,
                    workerBehaviour)) return true;
            return false;
        }

        private static bool TryRequestReplicationTraderInteraction(object traderBehaviour, object? workerBehaviour)
        {
            if (!SynchronizedTradingEnabled()
                || replicationConfigHostMode
                || !replicationRemoteHelloReceived
                || !ShouldSendReplicationLocalCommandIntent()) return false;
            var owner = TryGetTraderOwner(traderBehaviour);
            var workerDetail = "worker-not-resolved";
            if (owner == null
                || workerBehaviour == null
                || !TryGetReplicationTraderPartyNetworkId(owner, out var traderEntityId)
                || !TryGetReplicationWorkerBehaviourEntityId(workerBehaviour, out var workerEntityId, out workerDetail))
            {
                ShowReplicationTradingMessage("Could not identify the trader or selected settler for multiplayer trading.");
                instance?.LogReplicationWarning("Going Cooperative client trade-open capture failed worker=" + workerDetail);
                return true;
            }

            var pending = replicationPendingTraderInteraction;
            if (pending != null && pending.ExpiresRealtime > Time.realtimeSinceStartup)
            {
                ShowReplicationTradingMessage("A trading request is already pending.");
                return true;
            }

            var requestId = replicationEventClientSessionNonce + ":trade-open:"
                + Guid.NewGuid().ToString("N").Substring(0, 12);
            var payload = LockstepCommandPayloads.CreateTraderTradeOpenRequestPayload(
                replicationEventClientEpoch,
                traderEntityId,
                workerEntityId,
                requestId);
            replicationPendingTraderInteraction = new PendingReplicationTraderInteraction
            {
                RequestId = requestId,
                TraderEntityId = traderEntityId,
                WorkerEntityId = workerEntityId,
                ExpiresRealtime = Time.realtimeSinceStartup + ReplicationTraderInteractionRequestLeaseSeconds
            };
            SendReplicationLocalCommandIntent(
                new LockstepCommand(
                    ReplicationClientPeerId,
                    ++replicationIntentSequence,
                    0L,
                    CommandKind.Custom,
                    payload),
                "trader-interaction-open");
            ShowReplicationTradingMessage("Requesting trade…");
            return true;
        }

        private static bool TryApplyReplicationTraderInteractionOpenRequest(
            long epoch,
            string traderEntityId,
            string workerEntityId,
            string requestId,
            out string detail)
        {
            detail = "trade-open-request-rejected";
            if (!replicationConfigHostMode || !SynchronizedTradingEnabled())
                return RejectReplicationTraderInteractionOpen(requestId, "Trading interaction is unavailable.", out detail);
            if (epoch != replicationEventHostEpoch)
                return RejectReplicationTraderInteractionOpen(requestId, "Trading request belongs to an expired session.", out detail);
            var activeSession = replicationHostTradingSession;
            if (activeSession != null
                && !activeSession.Committed
                && activeSession.ExpiresRealtime >= Time.realtimeSinceStartup)
                return RejectReplicationTraderInteractionOpen(requestId, "Another trading session is already open.", out detail);
            if (!TryGetReplicationTraderPartyObject(traderEntityId, out var traderOwner)
                || traderOwner == null)
                return RejectReplicationTraderInteractionOpen(requestId, "The trader is no longer available.", out detail);
            if (!TryResolveReplicationWorkerBehaviour(workerEntityId, out var workerOwner, out var workerBehaviour, out var workerDetail)
                || workerOwner == null
                || workerBehaviour == null)
                return RejectReplicationTraderInteractionOpen(requestId, "The selected settler is unavailable.", out detail);

            var traderBehaviour = AccessTools.Property(traderOwner.GetType(), "TraderBehaviour")?.GetValue(traderOwner, null)
                ?? AccessTools.Field(traderOwner.GetType(), "traderBehaviour")?.GetValue(traderOwner);
            var talk = traderBehaviour == null
                ? null
                : AccessTools.Method(
                    traderBehaviour.GetType(),
                    "OnSettlerTalkTo",
                    new[] { AccessTools.TypeByName("NSMedieval.State.WorkerBehaviour") });
            if (traderBehaviour == null || talk == null)
                return RejectReplicationTraderInteractionOpen(requestId, "The native trader interaction is unavailable.", out detail);

            try
            {
                talk.Invoke(traderBehaviour, new[] { workerBehaviour });
                detail = "trade-open-request-invoked trader=" + traderEntityId
                    + " worker=" + workerEntityId
                    + " lookup=" + workerDetail;
                LogTraderTransferDiagnostic("trade-open-request invoked request=" + requestId
                    + " trader=" + traderEntityId + " worker=" + workerEntityId);
                return true;
            }
            catch (Exception ex)
            {
                return RejectReplicationTraderInteractionOpen(
                    requestId,
                    "The host could not start the trader interaction: " + ex.GetType().Name,
                    out detail);
            }
        }

        private static bool RejectReplicationTraderInteractionOpen(string requestId, string reason, out string detail)
        {
            detail = "trade-open-request-rejected reason=" + FormatReplicationWorldObjectDetailToken(reason);
            if (replicationConfigHostMode && replicationRemoteHelloReceived && !string.IsNullOrWhiteSpace(requestId))
            {
                SendHostTraderPartyDelta(
                    ReplicationTraderInteractionResultDeltaKind,
                    requestId,
                    1,
                    FormatReplicationTraderPartyEnvelope()
                    + " requestB64=" + EncodeReplicationDetailBase64(requestId)
                    + " accepted=0"
                    + " reasonB64=" + EncodeReplicationDetailBase64(reason));
            }
            return false;
        }

        private static bool TryApplyReplicationTraderInteractionResult(ReplicationWorldObjectDelta delta, out string detail)
        {
            detail = "trade-open-result-malformed";
            if (replicationConfigHostMode || !SynchronizedTradingEnabled()) return false;
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch)
                || !TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;
            if (!TryReadReplicationTradingText(delta.Detail, "requestB64", out var requestId)
                || !TryReadReplicationWorldObjectDetailInt(delta.Detail, "accepted", out var accepted)
                || !TryReadReplicationTradingText(delta.Detail, "reasonB64", out var reason)) return false;
            var pending = replicationPendingTraderInteraction;
            if (pending == null || !string.Equals(pending.RequestId, requestId, StringComparison.Ordinal))
            {
                detail = "trade-open-result-stale";
                return true;
            }
            replicationPendingTraderInteraction = null;
            if (accepted != 1) ShowReplicationTradingMessage(reason);
            detail = accepted == 1 ? "trade-open-result-accepted" : "trade-open-result-rejected";
            return true;
        }

        private static void CompleteReplicationTraderInteractionRequestOnSessionOpen()
        {
            replicationPendingTraderInteraction = null;
        }

        private static void UpdateReplicationTraderInteraction(float now)
        {
            if (replicationConfigHostMode) return;
            var pending = replicationPendingTraderInteraction;
            if (pending == null || pending.ExpiresRealtime > now) return;
            replicationPendingTraderInteraction = null;
            ShowReplicationTradingMessage("The host did not open the trading session in time.");
        }

        private static void ResetReplicationTraderInteraction()
        {
            replicationPendingTraderInteraction = null;
        }
    }
}
