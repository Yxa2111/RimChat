using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimWorld;
using Verse;

namespace RimChat.UI
{
    /// <summary>
    /// Dependencies: pending airdrop trade-card reference and parsed diplomacy actions.
    /// Responsibility: validate the business request id and expand accept_item_airdrop into the existing fulfillment flow.
    /// </summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private const string AirdropTradeCardRequestIdParameterKey = "__airdrop_trade_card_request_id";

        private static bool IsAcceptItemAirdropAction(AIAction action)
        {
            return action != null &&
                   string.Equals(action.ActionType, AIActionNames.AcceptItemAirdrop, StringComparison.Ordinal);
        }

        private static bool TryExpandAirdropAcceptanceActions(
            ParsedResponse response,
            FactionDialogueSession currentSession)
        {
            if (response?.Actions == null || response.Actions.Count == 0)
            {
                return true;
            }

            List<AIAction> acceptActions = response.Actions.Where(IsAcceptItemAirdropAction).ToList();
            List<AIAction> directAirdropActions = response.Actions
                .Where(IsRequestItemAirdropAction)
                .ToList();

            if (acceptActions.Count > 1 || (acceptActions.Count > 0 && directAirdropActions.Count > 0))
            {
                response.Actions = response.Actions
                    .Where(action => !IsAcceptItemAirdropAction(action) && !IsRequestItemAirdropAction(action))
                    .ToList();
                response.DialogueText = "RimChat_ItemAirdropAcceptConflict".Translate().ToString();
                return false;
            }

            if (acceptActions.Count > 0)
            {
                AIAction acceptAction = acceptActions[0];
                if (!TryBuildAcceptedAirdropAction(acceptAction, currentSession, out AIAction fulfillmentAction, out string failureMessage))
                {
                    response.Actions = response.Actions
                        .Where(action => !IsAcceptItemAirdropAction(action))
                        .ToList();
                    response.DialogueText = string.IsNullOrWhiteSpace(failureMessage)
                        ? "RimChat_ItemAirdropAcceptInvalidRequest".Translate().ToString()
                        : failureMessage;
                    return false;
                }

                response.Actions = response.Actions
                    .Select(action => ReferenceEquals(action, acceptAction) ? fulfillmentAction : action)
                    .ToList();
            }

            // Once a trade card is active, the old free-form request action must not bypass its request id.
            if (currentSession?.hasPendingAirdropTradeCardReference == true)
            {
                List<AIAction> bypassActions = response.Actions
                    .Where(action => IsRequestItemAirdropAction(action) && !HasAirdropTradeCardRequestId(action))
                    .ToList();
                if (bypassActions.Count > 0)
                {
                    response.Actions = response.Actions
                        .Where(action => !bypassActions.Contains(action))
                        .ToList();
                    response.DialogueText = "RimChat_ItemAirdropAcceptUseRequestId".Translate().ToString();
                    return false;
                }
            }

            return true;
        }

        private static bool TryBuildAcceptedAirdropAction(
            AIAction acceptAction,
            FactionDialogueSession currentSession,
            out AIAction fulfillmentAction,
            out string failureMessage)
        {
            fulfillmentAction = null;
            failureMessage = string.Empty;
            if (currentSession == null)
            {
                failureMessage = "RimChat_ItemAirdropAcceptNoPendingRequest".Translate().ToString();
                return false;
            }

            if (!TryReadAirdropTradeCardRequestId(acceptAction?.Parameters, out string suppliedRequestId))
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestIdRequired".Translate().ToString();
                return false;
            }

            if (!currentSession.hasPendingAirdropTradeCardReference)
            {
                if (currentSession.TryGetAirdropTradeCardStatus(suppliedRequestId, out AirdropTradeCardStatus historicalStatus) &&
                    historicalStatus == AirdropTradeCardStatus.Completed)
                {
                    failureMessage = "RimChat_ItemAirdropAcceptAlreadyCompleted".Translate(suppliedRequestId).ToString();
                    return false;
                }

                if (historicalStatus == AirdropTradeCardStatus.Cancelled ||
                    historicalStatus == AirdropTradeCardStatus.Superseded)
                {
                    failureMessage = "RimChat_ItemAirdropAcceptRequestExpired".Translate(suppliedRequestId).ToString();
                    return false;
                }

                failureMessage = "RimChat_ItemAirdropAcceptNoPendingRequest".Translate().ToString();
                return false;
            }

            if (!TryEnsurePendingAirdropTradeCardRequestId(currentSession, out string requestId))
            {
                failureMessage = "RimChat_ItemAirdropAcceptNoPendingRequest".Translate().ToString();
                return false;
            }

            if (acceptAction.Parameters != null &&
                acceptAction.Parameters.Keys.Any(key => !string.Equals(key, "request_id", StringComparison.Ordinal)))
            {
                failureMessage = "RimChat_ItemAirdropAcceptTermsInvalid".Translate(suppliedRequestId).ToString();
                return false;
            }

            if (!string.Equals(requestId, suppliedRequestId, StringComparison.Ordinal))
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestIdMismatch".Translate(suppliedRequestId).ToString();
                return false;
            }

            AirdropTradeCardStatus status = currentSession.pendingAirdropTradeCardStatus;
            if (status == AirdropTradeCardStatus.Completed ||
                status == AirdropTradeCardStatus.Cancelled ||
                status == AirdropTradeCardStatus.Superseded)
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestExpired".Translate(requestId).ToString();
                return false;
            }

            if (status == AirdropTradeCardStatus.Preparing ||
                status == AirdropTradeCardStatus.AwaitingPlayerConfirm ||
                status == AirdropTradeCardStatus.Executing)
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestInProgress".Translate(requestId).ToString();
                return false;
            }

            if (!ItemAirdropBasket.TryNormalize(currentSession.GetPendingNeedItems(), out var needs) ||
                !ItemAirdropBasket.TryNormalize(currentSession.GetPendingPaymentItems(), out var payments))
            {
                failureMessage = "RimChat_ItemAirdropAcceptTermsInvalid".Translate(requestId).ToString();
                return false;
            }

            var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["need"] = ItemAirdropBasket.Summary(needs),
                ["need_items"] = ItemAirdropBasket.ToParameters(needs),
                ["payment_items"] = ItemAirdropBasket.ToParameters(payments),
                ["scenario"] = string.IsNullOrWhiteSpace(currentSession.pendingAirdropTradeCardScenario)
                    ? "trade"
                    : currentSession.pendingAirdropTradeCardScenario,
                [AirdropTradeCardRequestIdParameterKey] = requestId
            };

            fulfillmentAction = new AIAction
            {
                ActionType = AIActionNames.RequestItemAirdrop,
                Parameters = parameters,
                Reason = $"accept_item_airdrop:{requestId}"
            };
            return true;
        }

        private static bool TryEnsurePendingAirdropTradeCardRequestId(
            FactionDialogueSession currentSession,
            out string requestId)
        {
            requestId = currentSession?.pendingAirdropTradeCardRequestId?.Trim() ?? string.Empty;
            if (currentSession == null || !currentSession.hasPendingAirdropTradeCardReference)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(requestId))
            {
                requestId = Guid.NewGuid().ToString("N");
                currentSession.pendingAirdropTradeCardRequestId = requestId;
                currentSession.pendingAirdropTradeCardStatus = AirdropTradeCardStatus.Pending;
                if (currentSession.airdropTradeCardStatusByRequestId == null)
                {
                    currentSession.airdropTradeCardStatusByRequestId = new Dictionary<string, AirdropTradeCardStatus>(StringComparer.Ordinal);
                }
                currentSession.airdropTradeCardStatusByRequestId[requestId] = AirdropTradeCardStatus.Pending;
                DialogueMessageData latestCard = currentSession.messages?
                    .LastOrDefault(message => message != null && message.isPlayer && message.IsAirdropTradeCard());
                if (latestCard != null)
                {
                    latestCard.airdropRequestId = requestId;
                }
            }

            return true;
        }

        private static bool TryReadAirdropTradeCardRequestId(
            Dictionary<string, object> parameters,
            out string requestId)
        {
            requestId = string.Empty;
            if (parameters == null || !parameters.TryGetValue("request_id", out object raw) || raw == null)
            {
                return false;
            }

            requestId = raw.ToString()?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(requestId);
        }

        private static bool HasAirdropTradeCardRequestId(AIAction action)
        {
            return action?.Parameters != null &&
                   action.Parameters.TryGetValue(AirdropTradeCardRequestIdParameterKey, out object value) &&
                   !string.IsNullOrWhiteSpace(value?.ToString());
        }

        private static string GetAirdropTradeCardRequestId(AIAction action)
        {
            if (action?.Parameters == null ||
                !action.Parameters.TryGetValue(AirdropTradeCardRequestIdParameterKey, out object value))
            {
                return string.Empty;
            }

            return value?.ToString()?.Trim() ?? string.Empty;
        }

        private static bool IsAirdropTradeCardBoundAction(AIAction action)
        {
            return IsRequestItemAirdropAction(action) && HasAirdropTradeCardRequestId(action);
        }

        private static bool TryBeginAirdropTradeCardAcceptance(
            AIAction action,
            FactionDialogueSession currentSession,
            Faction currentFaction,
            out string failureMessage)
        {
            failureMessage = string.Empty;
            string requestId = GetAirdropTradeCardRequestId(action);
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return true;
            }

            if (currentSession == null || currentFaction == null || currentSession.faction == null ||
                currentSession.faction.loadID != currentFaction.loadID)
            {
                failureMessage = "RimChat_ItemAirdropAcceptFactionMismatch".Translate(requestId).ToString();
                return false;
            }

            if (!currentSession.IsCurrentAirdropTradeCardRequest(requestId))
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestExpired".Translate(requestId).ToString();
                return false;
            }

            AirdropTradeCardStatus status = currentSession.pendingAirdropTradeCardStatus;
            if (status == AirdropTradeCardStatus.Preparing ||
                status == AirdropTradeCardStatus.AwaitingPlayerConfirm ||
                status == AirdropTradeCardStatus.Executing)
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestInProgress".Translate(requestId).ToString();
                return false;
            }

            if (status == AirdropTradeCardStatus.Completed ||
                status == AirdropTradeCardStatus.Cancelled ||
                status == AirdropTradeCardStatus.Superseded)
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestExpired".Translate(requestId).ToString();
                return false;
            }

            currentSession.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.Preparing);
            return true;
        }

        private static void MarkAirdropTradeCardFailed(AIAction action, FactionDialogueSession currentSession)
        {
            string requestId = GetAirdropTradeCardRequestId(action);
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                // A failed preparation or commit leaves the quote negotiable;
                // no transaction has been committed, so the same id remains
                // valid for a later explicit acceptance retry.
                currentSession?.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.Pending);
            }
        }

        private static void MarkAirdropTradeCardFailed(
            ItemAirdropPreparedTradeData preparedTrade,
            FactionDialogueSession currentSession)
        {
            string requestId = GetAirdropTradeCardRequestId(preparedTrade);
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                currentSession?.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.Pending);
            }
        }

        private static void MarkAirdropTradeCardCompleted(
            ItemAirdropPreparedTradeData preparedTrade,
            FactionDialogueSession currentSession)
        {
            string requestId = GetAirdropTradeCardRequestId(preparedTrade);
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                currentSession?.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.Completed);
            }
        }

        private static void MarkAirdropTradeCardCancelled(FactionDialogueSession currentSession)
        {
            if (!string.IsNullOrWhiteSpace(currentSession?.pendingAirdropTradeCardRequestId))
            {
                currentSession.SetAirdropTradeCardStatus(
                    currentSession.pendingAirdropTradeCardRequestId,
                    AirdropTradeCardStatus.Cancelled);
            }
        }

        private static void MarkAirdropTradeCardAwaitingConfirm(AIAction action, FactionDialogueSession currentSession)
        {
            string requestId = GetAirdropTradeCardRequestId(action);
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                currentSession?.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.AwaitingPlayerConfirm);
            }
        }

        private static void MarkAirdropTradeCardExecuting(
            ItemAirdropPreparedTradeData preparedTrade,
            FactionDialogueSession currentSession)
        {
            string requestId = GetAirdropTradeCardRequestId(preparedTrade?.ParametersSnapshot);
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                currentSession?.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.Executing);
            }
        }

        private static string GetAirdropTradeCardRequestId(
            Dictionary<string, object> parameters)
        {
            if (parameters == null ||
                !parameters.TryGetValue(AirdropTradeCardRequestIdParameterKey, out object value))
            {
                return string.Empty;
            }

            return value?.ToString()?.Trim() ?? string.Empty;
        }

        private static bool TryValidatePreparedTradeAgainstAirdropTradeCard(
            AIAction action,
            FactionDialogueSession currentSession,
            ItemAirdropPreparedTradeData preparedTrade,
            out string failureMessage)
        {
            failureMessage = string.Empty;
            string requestId = GetAirdropTradeCardRequestId(action);
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return true;
            }

            if (preparedTrade == null || currentSession == null ||
                !currentSession.IsCurrentAirdropTradeCardRequest(requestId))
            {
                failureMessage = "RimChat_ItemAirdropAcceptRequestExpired".Translate(requestId).ToString();
                return false;
            }

            if (preparedTrade.DeliveryLines?.Count > 0)
            {
                bool matches = ItemAirdropBasket.SameTerms(currentSession.GetPendingNeedItems(), preparedTrade.DeliveryLines) &&
                    ItemAirdropBasket.SameTerms(currentSession.GetPendingPaymentItems(), preparedTrade.PaymentLines.Select(p =>
                        new ItemAirdropTradeLine { DefName = p.DefName, Count = p.Count })) &&
                    ItemAirdropBasket.SameTerms(currentSession.GetPendingPaymentItems(), preparedTrade.DeductionPlan.Select(p =>
                        new ItemAirdropTradeLine { DefName = p.DefName, Count = p.Count })) &&
                    string.Equals(preparedTrade.Scenario, currentSession.pendingAirdropTradeCardScenario, StringComparison.Ordinal) &&
                    string.Equals(preparedTrade.FactionId, currentSession.faction?.GetUniqueLoadID(), StringComparison.Ordinal) &&
                    preparedTrade.ShippingPodCount == currentSession.pendingAirdropTradeCardShippingPodCount &&
                    preparedTrade.ShippingCostSilver == currentSession.pendingAirdropTradeCardShippingCost;
                if (!matches) failureMessage = "RimChat_AirdropBasketTermsChanged".Translate().ToString();
                return matches;
            }

            string expectedDef = currentSession.pendingAirdropTradeCardNeedDefName?.Trim() ?? string.Empty;
            int expectedCount = Math.Max(1, currentSession.pendingAirdropTradeCardRequestedCount);
            string expectedPaymentDef = currentSession.pendingAirdropTradeCardPaymentItemDef?.Trim() ?? string.Empty;
            int expectedPaymentCount = Math.Max(1, currentSession.pendingAirdropTradeCardPaymentItemCount);
            string actualDef = preparedTrade.SelectedDefName?.Trim() ?? string.Empty;
            ItemAirdropPreparedPaymentLine payment = preparedTrade.PaymentLines?.Count == 1
                ? preparedTrade.PaymentLines[0]
                : null;

            bool exact = string.Equals(expectedDef, actualDef, StringComparison.Ordinal) &&
                         preparedTrade.Quantity == expectedCount &&
                         preparedTrade.RequestedQuantity == expectedCount &&
                         payment != null &&
                         string.Equals(expectedPaymentDef, payment.DefName?.Trim(), StringComparison.Ordinal) &&
                         payment.Count == expectedPaymentCount &&
                         preparedTrade.ShippingPodCount == Math.Max(0, currentSession.pendingAirdropTradeCardShippingPodCount) &&
                         preparedTrade.ShippingCostSilver == Math.Max(0, currentSession.pendingAirdropTradeCardShippingCost);
            if (exact)
            {
                return true;
            }

            failureMessage = "RimChat_ItemAirdropAcceptTermsChanged".Translate(
                expectedDef,
                expectedCount,
                expectedPaymentDef,
                expectedPaymentCount).ToString();
            return false;
        }

        private static string GetAirdropTradeCardRequestId(ItemAirdropPreparedTradeData preparedTrade)
        {
            return GetAirdropTradeCardRequestId(preparedTrade?.ParametersSnapshot);
        }
    }
}
