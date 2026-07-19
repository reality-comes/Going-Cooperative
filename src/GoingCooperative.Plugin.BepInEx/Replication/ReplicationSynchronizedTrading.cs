using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        internal const string ReplicationTradingSessionOpenDeltaKind = "TraderTradeSessionOpen";
        internal const string ReplicationTradingBasketStateDeltaKind = "TraderTradeBasketState";
        internal const string ReplicationTradingResultDeltaKind = "TraderTradeResult";

        private const float ReplicationTradingLeaseSeconds = 120f;
        private const int ReplicationTradingMaxBasketEntries = 256;
        private static HostReplicationTradingSession? replicationHostTradingSession;
        private static ClientReplicationTradingSession? replicationClientTradingSession;
        private static long replicationTradingRevision;

        private sealed class HostReplicationTradingSession
        {
            public string SessionId = string.Empty;
            public long Revision;
            public float ExpiresRealtime;
            public object Manager = null!;
            public object Player = null!;
            public object Trader = null!;
            public IList PlayerResources = null!;
            public IList TraderResources = null!;
            public bool Committed;
            public string RequestId = string.Empty;
            public long BasketRevision;
        }

        private sealed class ClientReplicationTradingSession
        {
            public string SessionId = string.Empty;
            public long Revision;
            public float ExpiresRealtime;
            public object Manager = null!;
            public long BasketRevision;
            public string LastSubmittedBasket = string.Empty;
        }

        private readonly struct ReplicationTradingBasketEntry
        {
            public ReplicationTradingBasketEntry(int playerIndex, int traderIndex, int amount)
            {
                PlayerIndex = playerIndex;
                TraderIndex = traderIndex;
                Amount = amount;
            }

            public int PlayerIndex { get; }
            public int TraderIndex { get; }
            public int Amount { get; }
        }

        private static bool SynchronizedTradingEnabled()
        {
            return replicationConfigSynchronizedTrading
                && TraderEventAuthorityEnabled()
                && replicationTraderPartyHooksReady
                && string.Equals(replicationConfigWorldObjectDeltaMode, "apply", StringComparison.OrdinalIgnoreCase)
                && IsReplicationCaptureModeSendEnabled(replicationConfigCommandCaptureMode);
        }

        private static void TryPublishReplicationSynchronizedTradingSession(object manager)
        {
            if (!SynchronizedTradingEnabled() || !replicationRemoteHelloReceived) return;
            try
            {
                var player = AccessTools.Field(manager.GetType(), "player")?.GetValue(manager);
                var trader = AccessTools.Field(manager.GetType(), "trader")?.GetValue(manager);
                var owner = TryGetTraderOwner(trader);
                if (player == null || trader == null || owner == null
                    || !TryGetReplicationWorkerBehaviourEntityId(player, out var playerId, out _)
                    || !TryGetReplicationTraderPartyNetworkId(owner, out var traderId)
                    || AccessTools.Field(manager.GetType(), "playerResources")?.GetValue(manager) is not IList playerResources
                    || AccessTools.Field(manager.GetType(), "traderResources")?.GetValue(manager) is not IList traderResources)
                {
                    instance?.LogReplicationWarning("Going Cooperative synchronized trading did not publish: participant or resource surface unresolved.");
                    return;
                }

                var revision = ++replicationTradingRevision;
                var sessionId = replicationEventHostSessionNonce + ":trade:" + revision.ToString(CultureInfo.InvariantCulture)
                    + ":" + Guid.NewGuid().ToString("N").Substring(0, 10);
                replicationHostTradingSession = new HostReplicationTradingSession
                {
                    SessionId = sessionId,
                    Revision = revision,
                    ExpiresRealtime = Time.realtimeSinceStartup + ReplicationTradingLeaseSeconds,
                    Manager = manager,
                    Player = player,
                    Trader = trader,
                    PlayerResources = playerResources,
                    TraderResources = traderResources
                };

                SendHostTraderPartyDelta(
                    ReplicationTradingSessionOpenDeltaKind,
                    sessionId,
                    1,
                    FormatReplicationTraderPartyEnvelope()
                    + " sessionB64=" + EncodeReplicationDetailBase64(sessionId)
                    + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                    + " playerB64=" + EncodeReplicationDetailBase64(playerId)
                    + " traderB64=" + EncodeReplicationDetailBase64(traderId)
                    + " playerRows=" + playerResources.Count.ToString(CultureInfo.InvariantCulture)
                    + " traderRows=" + traderResources.Count.ToString(CultureInfo.InvariantCulture));
                instance?.LogReplicationInfo("Going Cooperative synchronized trading session opened session=" + sessionId
                    + " playerRows=" + playerResources.Count.ToString(CultureInfo.InvariantCulture)
                    + " traderRows=" + traderResources.Count.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                instance?.LogReplicationWarning("Going Cooperative synchronized trading session publish failed error="
                    + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private static bool TryApplyReplicationTradingWorldDelta(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (replicationConfigHostMode || !SynchronizedTradingEnabled())
            {
                detail = "synchronized-trading-disabled";
                return false;
            }
            if (!TryReadReplicationEventEnvelope(delta.Detail, out var scope, out var epoch))
            {
                detail = "synchronized-trading-envelope-malformed";
                return false;
            }
            if (!TryAcceptReplicationEventScope(scope, epoch, out detail)) return false;

            if (string.Equals(delta.DeltaKind, ReplicationTradingSessionOpenDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTradingSessionOpen(delta.Detail, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTradingBasketStateDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTradingBasketState(delta.Detail, out detail);
            if (string.Equals(delta.DeltaKind, ReplicationTradingResultDeltaKind, StringComparison.Ordinal))
                return TryApplyReplicationTradingResult(delta.Detail, out detail);
            detail = "synchronized-trading-kind-unsupported";
            return false;
        }

        private static bool TryApplyReplicationTradingSessionOpen(string wire, out string detail)
        {
            detail = "trade-session-open-malformed";
            if (!TryReadReplicationTradingText(wire, "sessionB64", out var sessionId)
                || !TryReadReplicationWorldObjectDetailLong(wire, "revision", out var revision)
                || revision <= 0L
                || !TryReadReplicationTradingText(wire, "playerB64", out var playerId)
                || !TryReadReplicationTradingText(wire, "traderB64", out var traderId)
                || !TryResolveReplicationWorkerBehaviour(playerId, out _, out var player, out var playerDetail)
                || player == null
                || !TryGetReplicationTraderPartyObject(traderId, out var traderOwner)
                || traderOwner == null)
            {
                return false;
            }

            var trader = AccessTools.Property(traderOwner.GetType(), "TraderBehaviour")?.GetValue(traderOwner, null)
                ?? AccessTools.Field(traderOwner.GetType(), "traderBehaviour")?.GetValue(traderOwner);
            var managerType = AccessTools.TypeByName("NSMedieval.UI.TradingManager");
            var manager = managerType == null ? null : AccessTools.Property(managerType, "Instance")?.GetValue(null, null);
            var open = managerType == null ? null : AccessTools.Method(managerType, "OpenTradingMenu", new[]
            {
                AccessTools.TypeByName("NSMedieval.UI.ITrader"),
                AccessTools.TypeByName("NSMedieval.UI.ITrader")
            });
            if (trader == null || manager == null || open == null)
            {
                detail = "trade-session-native-surface-missing player=" + playerDetail;
                return false;
            }

            try
            {
                replicationTraderPartyApplicationDepth++;
                open.Invoke(manager, new[] { player, trader });
                replicationClientTradingSession = new ClientReplicationTradingSession
                {
                    SessionId = sessionId,
                    Revision = revision,
                    ExpiresRealtime = Time.realtimeSinceStartup + ReplicationTradingLeaseSeconds,
                    Manager = manager
                };
                CompleteReplicationTraderInteractionRequestOnSessionOpen();
                detail = "trade-session-opened session=" + sessionId;
                return true;
            }
            catch (Exception ex)
            {
                detail = "trade-session-open-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                replicationTraderPartyApplicationDepth--;
            }
        }

        private static bool TrySubmitReplicationSynchronizedTrade(object manager)
        {
            if (!SynchronizedTradingEnabled() || replicationConfigHostMode) return false;
            var session = replicationClientTradingSession;
            if (session == null || !ReferenceEquals(session.Manager, manager)) return false;
            if (session.ExpiresRealtime < Time.realtimeSinceStartup)
            {
                replicationClientTradingSession = null;
                ShowReplicationTradingMessage("The synchronized trading session expired. Ask the host to reopen it.");
                return true;
            }
            if (!TryEncodeReplicationTradingBasket(manager, out var basket, out var basketCount, out var encodeDetail))
            {
                ShowReplicationTradingMessage("Trade was not sent: " + encodeDetail);
                return true;
            }
            if (basketCount == 0)
            {
                ShowReplicationTradingMessage("Choose goods before applying the trade.");
                return true;
            }

            var requestId = session.SessionId + ":request:" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var payload = LockstepCommandPayloads.CreateTraderTradeCommitPayload(
                session.SessionId,
                session.Revision,
                requestId,
                basket);
            var command = new LockstepCommand(
                ReplicationClientPeerId,
                ++replicationIntentSequence,
                0L,
                CommandKind.Custom,
                payload);
            SendReplicationLocalCommandIntent(command, "synchronized-trade");
            return true;
        }

        private static void PublishReplicationSynchronizedTradingBasketChange(object manager)
        {
            if (!SynchronizedTradingEnabled()) return;
            if (!TryEncodeReplicationTradingBasket(manager, out var basket, out _, out var encodeDetail))
            {
                instance?.LogReplicationWarning("Going Cooperative synchronized trading basket capture failed detail=" + encodeDetail);
                return;
            }

            if (replicationConfigHostMode)
            {
                var host = replicationHostTradingSession;
                if (host == null || host.Committed || !ReferenceEquals(host.Manager, manager)) return;
                host.BasketRevision++;
                SendHostReplicationTradingBasketState(host, basket, "host-ui");
                return;
            }

            var client = replicationClientTradingSession;
            if (client == null || !ReferenceEquals(client.Manager, manager)
                || string.Equals(client.LastSubmittedBasket, basket, StringComparison.Ordinal)) return;
            client.LastSubmittedBasket = basket;
            var requestId = client.SessionId + ":basket:" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var payload = LockstepCommandPayloads.CreateTraderTradeBasketUpdatePayload(
                client.SessionId,
                client.Revision,
                requestId,
                basket);
            SendReplicationLocalCommandIntent(
                new LockstepCommand(
                    ReplicationClientPeerId,
                    ++replicationIntentSequence,
                    0L,
                    CommandKind.Custom,
                    payload),
                "synchronized-trade-basket");
        }

        private static bool TryApplyReplicationSynchronizedTradingBasketUpdate(
            string sessionId,
            long revision,
            string requestId,
            string basket,
            out string detail)
        {
            detail = "trade-basket-update-rejected";
            if (!replicationConfigHostMode || !SynchronizedTradingEnabled())
            {
                detail = "synchronized-trading-host-lane-disabled";
                return false;
            }
            var session = replicationHostTradingSession;
            if (session == null
                || session.Committed
                || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)
                || session.Revision != revision
                || session.ExpiresRealtime < Time.realtimeSinceStartup)
            {
                detail = "trade-basket-session-stale";
                return false;
            }
            List<ReplicationTradingBasketEntry> entries;
            if (basket.Length == 0)
                entries = new List<ReplicationTradingBasketEntry>();
            else if (!TryParseReplicationTradingBasket(basket, session, true, out entries, out detail))
                return false;
            if (!TryApplyReplicationTradingBasketToManager(session.Manager, session.PlayerResources, session.TraderResources, entries, out detail))
                return false;
            session.BasketRevision++;
            SendHostReplicationTradingBasketState(session, basket, "client-ui");
            detail = "trade-basket-applied revision=" + session.BasketRevision.ToString(CultureInfo.InvariantCulture)
                + " request=" + requestId;
            return true;
        }

        private static void SendHostReplicationTradingBasketState(
            HostReplicationTradingSession session,
            string basket,
            string source)
        {
            LogTraderTransferDiagnostic("trade-basket-state send session=" + session.SessionId
                + " revision=" + session.BasketRevision.ToString(CultureInfo.InvariantCulture)
                + " source=" + source);
            SendHostTraderPartyDelta(
                ReplicationTradingBasketStateDeltaKind,
                session.SessionId,
                1,
                FormatReplicationTraderPartyEnvelope()
                + " sessionB64=" + EncodeReplicationDetailBase64(session.SessionId)
                + " revision=" + session.Revision.ToString(CultureInfo.InvariantCulture)
                + " basketRevision=" + session.BasketRevision.ToString(CultureInfo.InvariantCulture)
                + " basketB64=" + EncodeReplicationDetailBase64(basket)
                + " sourceB64=" + EncodeReplicationDetailBase64(source));
        }

        private static bool TryApplyReplicationTradingBasketState(string wire, out string detail)
        {
            detail = "trade-basket-state-malformed";
            if (!TryReadReplicationTradingText(wire, "sessionB64", out var sessionId)
                || !TryReadReplicationWorldObjectDetailLong(wire, "revision", out var revision)
                || !TryReadReplicationWorldObjectDetailLong(wire, "basketRevision", out var basketRevision)
                || basketRevision <= 0L
                || !TryReadReplicationTradingBasketText(wire, "basketB64", out var basket)) return false;
            var client = replicationClientTradingSession;
            if (client == null
                || !string.Equals(client.SessionId, sessionId, StringComparison.Ordinal)
                || client.Revision != revision)
            {
                detail = "trade-basket-state-stale-session";
                return true;
            }
            if (basketRevision <= client.BasketRevision)
            {
                detail = "trade-basket-state-stale-revision";
                return true;
            }
            if (AccessTools.Field(client.Manager.GetType(), "playerResources")?.GetValue(client.Manager) is not IList playerRows
                || AccessTools.Field(client.Manager.GetType(), "traderResources")?.GetValue(client.Manager) is not IList traderRows)
            {
                detail = "trade-basket-client-resource-surface-missing";
                return false;
            }
            var shadow = new HostReplicationTradingSession
            {
                Manager = client.Manager,
                PlayerResources = playerRows,
                TraderResources = traderRows
            };
            List<ReplicationTradingBasketEntry> entries;
            if (basket.Length == 0)
                entries = new List<ReplicationTradingBasketEntry>();
            else if (!TryParseReplicationTradingBasket(basket, shadow, false, out entries, out detail))
            {
                LogTraderTransferDiagnostic("trade-basket-state reject session=" + sessionId
                    + " revision=" + basketRevision.ToString(CultureInfo.InvariantCulture)
                    + " detail=" + detail);
                detail = "trade-basket-state-ignored " + detail;
                return true;
            }
            if (!TryApplyReplicationTradingBasketToManager(client.Manager, playerRows, traderRows, entries, out detail))
            {
                LogTraderTransferDiagnostic("trade-basket-state projection-failed session=" + sessionId
                    + " revision=" + basketRevision.ToString(CultureInfo.InvariantCulture)
                    + " detail=" + detail);
                detail = "trade-basket-state-ignored " + detail;
                return true;
            }
            client.BasketRevision = basketRevision;
            client.LastSubmittedBasket = basket;
            detail = "trade-basket-state-applied revision=" + basketRevision.ToString(CultureInfo.InvariantCulture);
            LogTraderTransferDiagnostic("trade-basket-state apply session=" + sessionId
                + " revision=" + basketRevision.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        private static bool TryApplyReplicationTradingBasketToManager(
            object manager,
            IList playerRows,
            IList traderRows,
            IList<ReplicationTradingBasketEntry> entries,
            out string detail)
        {
            detail = "trade-basket-native-surface-missing";
            try
            {
                var tradeGoods = AccessTools.Field(manager.GetType(), "tradeGoods")?.GetValue(manager);
                var clear = AccessTools.Method(tradeGoods?.GetType(), "Clear", Type.EmptyTypes);
                var setAmount = AccessTools.Method(manager.GetType(), "SetBuySellAmount");
                if (tradeGoods == null || clear == null || setAmount == null) return false;
                replicationTraderPartyApplicationDepth++;
                clear.Invoke(tradeGoods, null);

                var panel = AccessTools.Field(manager.GetType(), "tradingPanelView")?.GetValue(manager);
                var uiRows = CollectReplicationTradingUiRows(panel);
                if (uiRows.Count > 0)
                {
                    var matched = new bool[entries.Count];
                    for (var rowIndex = 0; rowIndex < uiRows.Count; rowIndex++)
                    {
                        var row = uiRows[rowIndex];
                        var rowType = row.GetType();
                        var playerRow = AccessTools.Field(rowType, "playerResource")?.GetValue(row);
                        var traderRow = AccessTools.Field(rowType, "traderResource")?.GetValue(row);
                        var desired = 0;
                        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                        {
                            var entry = entries[entryIndex];
                            var expectedPlayer = entry.PlayerIndex < 0 ? null : playerRows[entry.PlayerIndex];
                            var expectedTrader = entry.TraderIndex < 0 ? null : traderRows[entry.TraderIndex];
                            if (!ReferenceEquals(playerRow, expectedPlayer) || !ReferenceEquals(traderRow, expectedTrader)) continue;
                            desired = entry.Amount;
                            matched[entryIndex] = true;
                            break;
                        }

                        var currentValue = AccessTools.Field(rowType, "tradeValue")?.GetValue(row);
                        var current = currentValue == null ? 0 : Convert.ToInt32(currentValue, CultureInfo.InvariantCulture);
                        if (desired == 0 && current == 0) continue;
                        AccessTools.Method(rowType, "OnTradeValueChanged", new[] { typeof(int) })
                            ?.Invoke(row, new object[] { desired });
                        var input = AccessTools.Field(rowType, "tradingInput")?.GetValue(row);
                        AccessTools.Method(input?.GetType(), "SetTradeValue", new[] { typeof(int) })
                            ?.Invoke(input, new object[] { desired });
                    }
                    for (var i = 0; i < matched.Length; i++)
                    {
                        if (matched[i]) continue;
                        detail = "trade-basket-ui-row-unresolved index=" + i.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                    detail = "ok-ui rows=" + uiRows.Count.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                for (var i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    setAmount.Invoke(manager, new[]
                    {
                        entry.PlayerIndex < 0 ? null : playerRows[entry.PlayerIndex],
                        entry.TraderIndex < 0 ? null : traderRows[entry.TraderIndex],
                        (object)entry.Amount
                    });
                }
                detail = "ok-manager-fallback";
                return true;
            }
            catch (Exception ex)
            {
                detail = "trade-basket-apply-failed " + FormatReflectionExceptionDetail(ex);
                return false;
            }
            finally
            {
                replicationTraderPartyApplicationDepth--;
            }
        }

        private static List<object> CollectReplicationTradingUiRows(object? panel)
        {
            var rows = new List<object>();
            if (panel == null) return rows;
            var seen = new HashSet<object>(ExternalReferenceComparer.Instance);
            foreach (var fieldName in new[] { "tradeEntries", "pinnedTradeEntries" })
            {
                if (AccessTools.Field(panel.GetType(), fieldName)?.GetValue(panel) is not IEnumerable values) continue;
                foreach (var value in values)
                {
                    if (value != null && seen.Add(value)) rows.Add(value);
                }
            }
            return rows;
        }

        private static bool TryEncodeReplicationTradingBasket(object manager, out string basket, out int count, out string detail)
        {
            basket = string.Empty;
            count = 0;
            detail = "ok";
            if (AccessTools.Field(manager.GetType(), "tradeGoods")?.GetValue(manager) is not IEnumerable goods
                || AccessTools.Field(manager.GetType(), "playerResources")?.GetValue(manager) is not IList playerRows
                || AccessTools.Field(manager.GetType(), "traderResources")?.GetValue(manager) is not IList traderRows)
            {
                detail = "native basket surface missing";
                return false;
            }

            var parts = new List<string>();
            foreach (var tuple in goods)
            {
                if (tuple == null || parts.Count >= ReplicationTradingMaxBasketEntries)
                {
                    detail = "basket exceeds safe entry limit";
                    return false;
                }
                var tupleType = tuple.GetType();
                var playerRow = AccessTools.Property(tupleType, "Item1")?.GetValue(tuple, null);
                var traderRow = AccessTools.Property(tupleType, "Item2")?.GetValue(tuple, null);
                var amountObject = AccessTools.Property(tupleType, "Item3")?.GetValue(tuple, null);
                if (amountObject == null) continue;
                var amount = Convert.ToInt32(amountObject, CultureInfo.InvariantCulture);
                if (amount == 0) continue;
                var playerIndex = IndexOfReference(playerRows, playerRow);
                var traderIndex = IndexOfReference(traderRows, traderRow);
                if (playerIndex < -1 || traderIndex < -1 || (playerIndex < 0 && traderIndex < 0))
                {
                    detail = "basket row could not be mapped";
                    return false;
                }
                if (!TryFormatReplicationTradingRowPairIdentity(playerRow, traderRow, out var rowIdentity)
                    || !TryFormatReplicationTradingRowPairSignature(rowIdentity, out var rowSignature))
                {
                    detail = "basket row identity could not be established";
                    return false;
                }
                parts.Add(playerIndex.ToString(CultureInfo.InvariantCulture) + ","
                    + traderIndex.ToString(CultureInfo.InvariantCulture) + ","
                    + amount.ToString(CultureInfo.InvariantCulture) + ","
                    + rowSignature + ","
                    + Convert.ToBase64String(Encoding.UTF8.GetBytes(rowIdentity)));
            }
            basket = string.Join(";", parts);
            count = parts.Count;
            return true;
        }

        private static bool TryApplyReplicationSynchronizedTrade(
            string sessionId,
            long revision,
            string requestId,
            string basket,
            out string detail)
        {
            detail = "trade-rejected";
            if (!replicationConfigHostMode || !SynchronizedTradingEnabled())
            {
                detail = "synchronized-trading-host-lane-disabled";
                return false;
            }
            var session = replicationHostTradingSession;
            if (session == null
                || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)
                || session.Revision != revision)
            {
                detail = "trade-session-stale";
                SendHostReplicationTradingResult(sessionId, revision, requestId, false, detail);
                return false;
            }
            if (session.Committed)
            {
                var duplicate = string.Equals(session.RequestId, requestId, StringComparison.Ordinal);
                detail = duplicate ? "trade-request-already-committed" : "trade-session-already-consumed";
                SendHostReplicationTradingResult(sessionId, revision, requestId, duplicate, detail);
                return duplicate;
            }
            if (session.ExpiresRealtime < Time.realtimeSinceStartup)
            {
                detail = "trade-session-expired";
                SendHostReplicationTradingResult(sessionId, revision, requestId, false, detail);
                return false;
            }
            if (!TryParseReplicationTradingBasket(basket, session, true, out var entries, out detail))
            {
                SendHostReplicationTradingResult(sessionId, revision, requestId, false, detail);
                return false;
            }

            try
            {
                if (!TryApplyReplicationTradingBasketToManager(
                        session.Manager,
                        session.PlayerResources,
                        session.TraderResources,
                        entries,
                        out detail))
                {
                    SendHostReplicationTradingResult(sessionId, revision, requestId, false, detail);
                    return false;
                }

                var panel = AccessTools.Field(session.Manager.GetType(), "tradingPanelView")?.GetValue(session.Manager);
                var enabledField = panel == null ? null : AccessTools.Field(panel.GetType(), "isApplyButtonEnabled");
                if (enabledField == null || enabledField.GetValue(panel) is not bool enabled || !enabled)
                {
                    detail = "trade-host-validation-failed";
                    SendHostReplicationTradingResult(sessionId, revision, requestId, false, detail);
                    return false;
                }

                session.Committed = true;
                session.RequestId = requestId;
                AccessTools.Method(session.Manager.GetType(), "ApplyTrade", new[] { typeof(float) })
                    ?.Invoke(session.Manager, new object[] { float.MaxValue });
                TryHideReplicationTradingPanel(session.Manager);
                detail = "trade-committed entries=" + entries.Count.ToString(CultureInfo.InvariantCulture);
                SendHostReplicationTradingResult(sessionId, revision, requestId, true, detail);
                instance?.LogReplicationInfo("Going Cooperative synchronized trade committed session=" + sessionId
                    + " entries=" + entries.Count.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                detail = "trade-commit-failed " + FormatReflectionExceptionDetail(ex);
                SendHostReplicationTradingResult(sessionId, revision, requestId, false, detail);
                return false;
            }
        }

        private static bool TryParseReplicationTradingBasket(
            string wire,
            HostReplicationTradingSession session,
            bool validateAvailability,
            out List<ReplicationTradingBasketEntry> entries,
            out string detail)
        {
            entries = new List<ReplicationTradingBasketEntry>();
            detail = "trade-basket-malformed";
            var rows = wire.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length < 1 || rows.Length > ReplicationTradingMaxBasketEntries) return false;
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < rows.Length; i++)
            {
                var columns = rows[i].Split(',');
                if (columns.Length != 5
                    || !int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var playerIndex)
                    || !int.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var traderIndex)
                    || !int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)
                    || amount == 0 || Math.Abs((long)amount) > 1000000L
                    || playerIndex < -1 || playerIndex > 10000
                    || traderIndex < -1 || traderIndex > 10000
                    || (playerIndex < 0 && traderIndex < 0)
                    || columns[3].Length != 64)
                {
                    return false;
                }
                if (!TryDecodeReplicationTradingRowIdentity(columns[4], out var wireIdentity)
                    || !TryResolveReplicationTradingHostRowPair(
                        session,
                        columns[3],
                        wireIdentity,
                        out var resolvedPlayerIndex,
                        out var resolvedTraderIndex,
                        out var playerRow,
                        out var traderRow))
                {
                    detail = "trade-basket-row-unresolved index=" + i.ToString(CultureInfo.InvariantCulture)
                        + " signature=" + columns[3].Substring(0, 12);
                    return false;
                }
                if (validateAvailability && amount > 0 && !ReplicationTradingRowHasCount(traderRow, amount))
                {
                    detail = "trade-basket-trader-stock-insufficient index=" + i.ToString(CultureInfo.InvariantCulture)
                        + " requested=" + amount.ToString(CultureInfo.InvariantCulture)
                        + " available=" + ReadReplicationTradingRowCount(traderRow).ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (validateAvailability && amount < 0 && !ReplicationTradingRowHasCount(playerRow, -(long)amount))
                {
                    detail = "trade-basket-player-stock-insufficient index=" + i.ToString(CultureInfo.InvariantCulture)
                        + " requested=" + (-(long)amount).ToString(CultureInfo.InvariantCulture)
                        + " available=" + ReadReplicationTradingRowCount(playerRow).ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                if (!unique.Add(resolvedPlayerIndex.ToString(CultureInfo.InvariantCulture) + ":"
                    + resolvedTraderIndex.ToString(CultureInfo.InvariantCulture))) return false;
                entries.Add(new ReplicationTradingBasketEntry(resolvedPlayerIndex, resolvedTraderIndex, amount));
            }
            detail = "ok";
            return true;
        }

        private static bool TryResolveReplicationTradingHostRowPair(
            HostReplicationTradingSession session,
            string signature,
            string wireIdentity,
            out int playerIndex,
            out int traderIndex,
            out object? playerRow,
            out object? traderRow)
        {
            playerIndex = traderIndex = -1;
            playerRow = traderRow = null;
            var panel = AccessTools.Field(session.Manager.GetType(), "tradingPanelView")?.GetValue(session.Manager);
            var rows = CollectReplicationTradingUiRows(panel);
            object? relaxedPlayer = null;
            object? relaxedTrader = null;
            var relaxedPlayerIndex = -1;
            var relaxedTraderIndex = -1;
            var relaxedMatches = 0;
            var relaxedWireIdentity = FormatRelaxedReplicationTradingRowIdentity(wireIdentity);
            for (var i = 0; i < rows.Count; i++)
            {
                var rowType = rows[i].GetType();
                var candidatePlayer = AccessTools.Field(rowType, "playerResource")?.GetValue(rows[i]);
                var candidateTrader = AccessTools.Field(rowType, "traderResource")?.GetValue(rows[i]);
                if (!TryFormatReplicationTradingRowPairIdentity(candidatePlayer, candidateTrader, out var candidateIdentity)
                    || !TryFormatReplicationTradingRowPairSignature(candidateIdentity, out var candidateSignature)) continue;
                var candidatePlayerIndex = IndexOfReference(session.PlayerResources, candidatePlayer);
                var candidateTraderIndex = IndexOfReference(session.TraderResources, candidateTrader);
                if (candidatePlayerIndex < -1 || candidateTraderIndex < -1
                    || (candidatePlayerIndex < 0 && candidateTraderIndex < 0)) continue;
                if (string.Equals(candidateSignature, signature, StringComparison.Ordinal))
                {
                    playerIndex = candidatePlayerIndex;
                    traderIndex = candidateTraderIndex;
                    playerRow = candidatePlayer;
                    traderRow = candidateTrader;
                    return true;
                }
                if (!string.Equals(
                        FormatRelaxedReplicationTradingRowIdentity(candidateIdentity),
                        relaxedWireIdentity,
                        StringComparison.Ordinal)) continue;
                relaxedMatches++;
                relaxedPlayerIndex = candidatePlayerIndex;
                relaxedTraderIndex = candidateTraderIndex;
                relaxedPlayer = candidatePlayer;
                relaxedTrader = candidateTrader;
            }
            if (relaxedMatches != 1) return false;
            playerIndex = relaxedPlayerIndex;
            traderIndex = relaxedTraderIndex;
            playerRow = relaxedPlayer;
            traderRow = relaxedTrader;
            return true;
        }

        private static void SendHostReplicationTradingResult(
            string sessionId,
            long revision,
            string requestId,
            bool accepted,
            string resultDetail)
        {
            SendHostTraderPartyDelta(
                ReplicationTradingResultDeltaKind,
                sessionId,
                1,
                FormatReplicationTraderPartyEnvelope()
                + " sessionB64=" + EncodeReplicationDetailBase64(sessionId)
                + " revision=" + revision.ToString(CultureInfo.InvariantCulture)
                + " requestB64=" + EncodeReplicationDetailBase64(requestId)
                + " accepted=" + (accepted ? "1" : "0")
                + " resultB64=" + EncodeReplicationDetailBase64(resultDetail));
        }

        private static bool TryApplyReplicationTradingResult(string wire, out string detail)
        {
            detail = "trade-result-malformed";
            if (!TryReadReplicationTradingText(wire, "sessionB64", out var sessionId)
                || !TryReadReplicationWorldObjectDetailLong(wire, "revision", out var revision)
                || !TryReadReplicationWorldObjectDetailInt(wire, "accepted", out var accepted)
                || !TryReadReplicationTradingText(wire, "resultB64", out var result)) return false;
            var session = replicationClientTradingSession;
            if (session == null
                || !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal)
                || session.Revision != revision)
            {
                detail = "trade-result-stale";
                return true;
            }
            replicationClientTradingSession = null;
            TryHideReplicationTradingPanel(session.Manager);
            ShowReplicationTradingMessage(accepted == 1 ? "Trade accepted by host." : "Trade rejected by host: " + result);
            detail = (accepted == 1 ? "trade-result-accepted " : "trade-result-rejected ") + result;
            return true;
        }

        private static void MarkReplicationSynchronizedTradeAppliedByHost(object manager)
        {
            if (!SynchronizedTradingEnabled()) return;
            var session = replicationHostTradingSession;
            if (session == null || !ReferenceEquals(session.Manager, manager) || session.Committed) return;
            session.Committed = true;
            session.RequestId = "host-local:" + Guid.NewGuid().ToString("N").Substring(0, 10);
            SendHostReplicationTradingResult(
                session.SessionId,
                session.Revision,
                session.RequestId,
                true,
                "trade-committed-by-host");
        }

        private static void TryHideReplicationTradingPanel(object manager)
        {
            try
            {
                var panel = AccessTools.Field(manager.GetType(), "tradingPanelView")?.GetValue(manager);
                AccessTools.Method(panel?.GetType(), "Hide", Type.EmptyTypes)?.Invoke(panel, null);
            }
            catch
            {
                // The result is authoritative even if the cosmetic close fails.
            }
        }

        private static bool TryReadReplicationTradingText(string wire, string name, out string value)
        {
            value = string.Empty;
            return TryReadReplicationWorldObjectDetailToken(wire, name, out var token)
                && TryDecodeReplicationDetailBase64(token, out value)
                && value.Length <= 512;
        }

        private static bool TryReadReplicationTradingBasketText(string wire, string name, out string value)
        {
            value = string.Empty;
            return TryReadReplicationWorldObjectDetailToken(wire, name, out var token)
                && TryDecodeReplicationDetailBase64(token, out value)
                && value.Length <= 16384;
        }

        private static int IndexOfReference(IList rows, object? target)
        {
            if (target == null) return -1;
            for (var i = 0; i < rows.Count; i++)
                if (ReferenceEquals(rows[i], target)) return i;
            return -2;
        }

        private static bool TryFormatReplicationTradingRowPairIdentity(object? playerRow, object? traderRow, out string identity)
        {
            identity = string.Empty;
            if (!TryFormatReplicationTradingRowIdentity(playerRow, out var playerIdentity)
                || !TryFormatReplicationTradingRowIdentity(traderRow, out var traderIdentity)) return false;
            identity = string.Equals(playerIdentity, "none", StringComparison.Ordinal)
                ? traderIdentity
                : string.Equals(traderIdentity, "none", StringComparison.Ordinal)
                    ? playerIdentity
                    : string.Equals(playerIdentity, traderIdentity, StringComparison.Ordinal)
                        ? playerIdentity
                        : string.CompareOrdinal(playerIdentity, traderIdentity) < 0
                            ? playerIdentity + "|paired=" + traderIdentity
                            : traderIdentity + "|paired=" + playerIdentity;
            return identity.Length > 0 && !string.Equals(identity, "none", StringComparison.Ordinal);
        }

        private static bool TryFormatReplicationTradingRowPairSignature(string identity, out string signature)
        {
            signature = string.Empty;
            if (string.IsNullOrEmpty(identity)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes("row=" + identity));
            var builder = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            signature = builder.ToString();
            return true;
        }

        private static bool TryDecodeReplicationTradingRowIdentity(string token, out string identity)
        {
            identity = string.Empty;
            try
            {
                if (token.Length == 0 || token.Length > 2048) return false;
                identity = Encoding.UTF8.GetString(Convert.FromBase64String(token));
                return identity.Length > 0 && identity.Length <= 1024;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatRelaxedReplicationTradingRowIdentity(string identity)
        {
            var parts = identity.Split('|');
            var kept = new List<string>(parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("health=", StringComparison.Ordinal)) continue;
                kept.Add(parts[i]);
            }
            return string.Join("|", kept);
        }

        private static bool TryFormatReplicationTradingRowIdentity(object? row, out string identity)
        {
            if (row == null)
            {
                identity = "none";
                return true;
            }

            // Creature trade rows are unique entities, not resource stacks.  In particular,
            // the native TradeResource used for an animal may not expose Resource/Health at
            // all, so requiring the item identity first makes animal basket changes vanish on
            // the client before they can be sent to the host.  Prefer the co-op party id for
            // visiting animals and the ordinary replicated entity id for colony animals.
            var creature = AccessTools.Property(row.GetType(), "Creature")?.GetValue(row, null);
            if (creature != null)
            {
                if (!TryGetReplicationTraderPartyNetworkId(creature, out var creatureId)
                    && !TryGetReplicationAgentOwnerEntityId(creature, out creatureId, out _))
                {
                    identity = string.Empty;
                    return false;
                }
                identity = "kind=creature|creature=" + creatureId;
                return true;
            }

            var resource = AccessTools.Property(row.GetType(), "Resource")?.GetValue(row, null);
            if (resource == null || !TryResolveReplicationModelId(resource, out var resourceId))
            {
                identity = string.Empty;
                return false;
            }
            var healthValue = AccessTools.Property(row.GetType(), "Health")?.GetValue(row, null);
            if (healthValue == null)
            {
                identity = string.Empty;
                return false;
            }
            identity = "kind=resource|" + resourceId
                + "|health=" + Math.Round(
                    Convert.ToDouble(healthValue, CultureInfo.InvariantCulture),
                    3,
                    MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool ReplicationTradingRowHasCount(object? row, long required)
        {
            if (row == null || required <= 0L) return false;
            var value = AccessTools.Property(row.GetType(), "Count")?.GetValue(row, null);
            return value != null && Convert.ToInt64(value, CultureInfo.InvariantCulture) >= required;
        }

        private static long ReadReplicationTradingRowCount(object? row)
        {
            if (row == null) return 0L;
            var value = AccessTools.Property(row.GetType(), "Count")?.GetValue(row, null);
            return value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static void ShowReplicationTradingMessage(string message)
        {
            try
            {
                var controllerType = AccessTools.TypeByName("NSMedieval.BlackBarMessageController");
                var controller = controllerType == null ? null : AccessTools.Property(controllerType, "Instance")?.GetValue(null, null);
                AccessTools.Method(controllerType, "ShowBlackBarMessage", new[] { typeof(string) })?.Invoke(controller, new object[] { message });
            }
            catch
            {
                // UI notification is best-effort; protocol state remains authoritative.
            }
        }

        private static void ResetReplicationSynchronizedTrading()
        {
            replicationHostTradingSession = null;
            replicationClientTradingSession = null;
            replicationTradingRevision = 0L;
        }
    }
}
