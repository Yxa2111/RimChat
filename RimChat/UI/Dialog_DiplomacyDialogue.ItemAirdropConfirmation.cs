using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimChat.AI;
using RimChat.Dialogue;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.UI
{
    /// <summary>
    /// Dependencies: GameAIInterface prepared airdrop trade API and dialogue session runtime state.
    /// Responsibility: show final confirmation dialog for barter airdrop and commit/cancel the prepared trade.
    /// </summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private static bool IsRequestItemAirdropAction(AIAction action)
        {
            return action != null &&
                   string.Equals(action.ActionType, AIActionNames.RequestItemAirdrop, StringComparison.Ordinal);
        }

        private bool TryHandleAirdropActionWithConfirmation(
            AIAction action,
            FactionDialogueSession currentSession,
            Faction currentFaction,
            out ActionExecutionOutcome outcome)
        {
            outcome = null;
            if (!IsRequestItemAirdropAction(action))
            {
                return false;
            }

            if (IsAirdropAsyncRequestPending(currentSession))
            {
                outcome = ActionExecutionOutcome.Success(
                    action,
                    BuildAirdropSelectionInProgressSystemText(),
                    new ItemAirdropAsyncQueuedData());
                return true;
            }

            AIAction actionSnapshot = new AIAction
            {
                ActionType = action.ActionType,
                Parameters = CloneParameters(action.Parameters),
                Reason = action.Reason
            };
            if (!TryBeginAirdropTradeCardAcceptance(actionSnapshot, currentSession, currentFaction, out string acceptanceFailure))
            {
                outcome = ActionExecutionOutcome.Failure(action, acceptanceFailure);
                return true;
            }
            DialogueRuntimeContext requestContext = runtimeContext.WithCurrentRuntimeMarkers();
            string validateReason = string.Empty;
            bool resolved = DialogueContextResolver.TryResolveLiveContext(
                requestContext,
                out DialogueLiveContext liveContext,
                out string resolveReason);
            bool validated = resolved && DialogueContextValidator.ValidateRequestSend(requestContext, liveContext, out validateReason);
            if (!resolved || !validated)
            {
                string fallbackReason = string.IsNullOrWhiteSpace(validateReason) ? resolveReason : validateReason;
                Log.Warning($"[RimChat] Airdrop context validation failed: resolved={resolved}, validated={validated}, resolveReason={resolveReason}, validateReason={validateReason}, faction={currentFaction?.Name ?? "null"}, defName={currentFaction?.def?.defName ?? "null"}");
                MarkAirdropTradeCardFailed(actionSnapshot, currentSession);
                outcome = ActionExecutionOutcome.Failure(action, fallbackReason ?? "RimChat_DialogueRequestUnavailable".Translate().ToString());
                return true;
            }

            object needValue = null;
            actionSnapshot.Parameters?.TryGetValue("need", out needValue);
            Log.Message($"[RimChat] Airdrop context validation passed: faction={currentFaction?.Name}, defName={currentFaction?.def?.defName}, need={needValue ?? "null"}");

            var lease = new DialogueRequestLease(
                requestContext.DialogueSessionId,
                windowInstanceId,
                requestContext.ContextVersion);
            var prepareResult = GameAIInterface.Instance.BeginPrepareItemAirdropTradeAsync(
                currentFaction,
                actionSnapshot.Parameters,
                negotiator,
                completedResult => HandleAirdropAsyncPrepareCompleted(
                    currentSession,
                    currentFaction,
                    lease,
                    requestContext,
                    actionSnapshot,
                    currentSession?.airdropRequestGeneration ?? -1,
                    completedResult),
                (requestId, timeoutSeconds) => BindAirdropAsyncRequest(currentSession, lease, requestId, timeoutSeconds));
            if (!prepareResult.Success)
            {
                lease.Dispose();
                ResetAirdropConfirmationRuntime(currentSession, "prepare_start_failed", true, false);
                MarkAirdropTradeCardFailed(actionSnapshot, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, prepareResult?.Message ?? "prepare_start_failed");
                string failureMessage = string.IsNullOrWhiteSpace(prepareResult?.Message)
                    ? "RimChat_Unknown".Translate().ToString()
                    : prepareResult.Message;
                outcome = ActionExecutionOutcome.Failure(action, failureMessage);
                return true;
            }

            if (prepareResult.Data is ItemAirdropAsyncQueuedData)
            {
                outcome = ActionExecutionOutcome.Success(
                    action,
                    BuildAirdropSelectionInProgressSystemText(),
                    prepareResult.Data);
                return true;
            }

            if (prepareResult.Data is ItemAirdropPendingSelectionData pendingSelection)
            {
                lease.Dispose();
                MarkAirdropTradeCardFailed(actionSnapshot, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, pendingSelection.FailureCode ?? "selection_pending");
                outcome = ActionExecutionOutcome.Failure(
                    action,
                    BuildAirdropPendingSelectionSystemText(pendingSelection));
                return true;
            }

            if (!(prepareResult.Data is ItemAirdropPreparedTradeData preparedTrade))
            {
                lease.Dispose();
                ResetAirdropConfirmationRuntime(currentSession, "prepared_trade_missing", true, false);
                MarkAirdropTradeCardFailed(actionSnapshot, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, "prepared_trade_missing");
                outcome = ActionExecutionOutcome.Failure(action, "RimChat_Unknown".Translate().ToString());
                return true;
            }

            if (!TryValidatePreparedTradeAgainstAirdropTradeCard(actionSnapshot, currentSession, preparedTrade, out string termsFailure))
            {
                lease.Dispose();
                ResetAirdropConfirmationRuntime(currentSession, "prepared_trade_terms_mismatch", true, false);
                MarkAirdropTradeCardFailed(actionSnapshot, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, termsFailure);
                outcome = ActionExecutionOutcome.Failure(action, termsFailure);
                return true;
            }

            lease.Dispose();
            ResetAirdropConfirmationRuntime(currentSession, "prepared_trade_ready", true, false);
            MarkAirdropTradeCardAwaitingConfirm(actionSnapshot, currentSession);
            TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.PreparedAwaitingConfirm, preparedTrade.SelectedDefName ?? "prepared_trade");
            currentSession.airdropPreparedAwaitingConfirmTick = Find.TickManager?.TicksGame ?? 0;
            Dictionary<string, object> baseParameters = CloneParameters(actionSnapshot.Parameters);

            Log.Message(
                $"[RimChat] AirdropConfirmOpen: def={preparedTrade.SelectedDefName},count={preparedTrade.Quantity},requested={preparedTrade.RequestedQuantity},hardMax={preparedTrade.HardMax},adjustment={preparedTrade.CountAdjustmentReason},payment={preparedTrade.PaymentTotalSilver}");
            ShowAirdropTradeConfirmationDialog(currentSession, currentFaction, preparedTrade, baseParameters);
            outcome = ActionExecutionOutcome.Success(
                action,
                "RimChat_ItemAirdropAwaitingConfirmSystem".Translate().ToString(),
                preparedTrade);
            return true;
        }

        private static string BuildAirdropSelectionInProgressSystemText()
        {
            return "RimChat_ItemAirdropSelectionInProgressSystem".Translate().ToString();
        }

        private static string BuildAirdropPendingSelectionSystemText(ItemAirdropPendingSelectionData pendingSelection)
        {
            if (pendingSelection?.Options == null || pendingSelection.Options.Count == 0)
            {
                if (string.Equals(pendingSelection?.FailureCode, "need_relevance_insufficient", StringComparison.Ordinal))
                {
                    return "RimChat_ItemAirdropNeedClarifySystem".Translate().ToString();
                }

                return "RimChat_ItemAirdropAwaitingConfirmSystem".Translate().ToString();
            }

            string lines = string.Join(
                "\n",
                pendingSelection.Options
                    .OrderBy(option => option.Index)
                    .Select(option => "RimChat_ItemAirdropSelectionPendingLine".Translate(
                        option.Index,
                        option.Label ?? option.DefName ?? "RimChat_Unknown".Translate().ToString(),
                        option.DefName ?? "RimChat_Unknown".Translate().ToString(),
                        option.UnitPrice.ToString("F1", CultureInfo.InvariantCulture),
                        option.MaxLegalCount).ToString()));
            return "RimChat_ItemAirdropSelectionPendingSystem".Translate(lines).ToString();
        }

        private void ShowAirdropTradeConfirmationDialog(
            FactionDialogueSession currentSession,
            Faction currentFaction,
            ItemAirdropPreparedTradeData preparedTrade,
            Dictionary<string, object> baseParameters)
        {
            ClearPendingAirdropDialogState("reschedule_confirmation", false);
            pendingAirdropDialogState = new PendingAirdropDialogState
            {
                Session = currentSession,
                Faction = currentFaction,
                PreparedTrade = preparedTrade,
                BaseParameters = CloneParameters(baseParameters)
            };
            Log.Message(
                $"[RimChat] AirdropConfirmScheduled: def={preparedTrade?.SelectedDefName ?? "unknown"},count={preparedTrade?.Quantity ?? 0}");
        }

        private void OpenQueuedAirdropTradeConfirmationDialog(PendingAirdropDialogState state)
        {
            if (state == null || state.PreparedTrade == null)
            {
                return;
            }

            ItemAirdropPreparedTradeData trade = state.PreparedTrade;
            bool hasDeliveryBasket = trade?.DeliveryLines?.Count > 0;
            string tradeLabel = hasDeliveryBasket
                ? "RimChat_AirdropMultipleItems".Translate().ToString()
                : string.IsNullOrWhiteSpace(trade?.ResolvedLabel)
                ? (trade?.SelectedDefName ?? "")
                : trade.ResolvedLabel;
            string basketDetails = hasDeliveryBasket
                ? BuildAirdropConfirmationBasketDetails(trade)
                : string.Empty;
            int quantity = trade?.Quantity ?? 1;
            int requestedQuantity = trade?.RequestedQuantity ?? quantity;
            int paymentTotal = trade?.PaymentTotalSilver ?? 0;
            float unitPrice = trade?.NeedQuotedUnitSilver ?? 0f;
            string priceTag = BuildPriceSemanticTag(trade?.NeedPriceSemantic);
            int shippingCost = Math.Max(0, trade?.ShippingCostSilver ?? 0);
            int shippingPods = Math.Max(0, trade?.ShippingPodCount ?? 0);
            string adjustmentReason = trade?.CountAdjustmentReason ?? string.Empty;

            bool isTradeCardAcceptance = !string.IsNullOrWhiteSpace(GetAirdropTradeCardRequestId(state.BaseParameters));
            bool hasManualAlternative = isTradeCardAcceptance;
            string alternativeLabel = "RimChat_AirdropTradeCard_EditRequest".Translate().ToString();
            string cancelLabel = isTradeCardAcceptance
                ? "RimChat_AirdropTradeCard_CancelRequest".Translate().ToString()
                : "RimChat_ItemAirdropConfirmCancel".Translate().ToString();

            var confirmationDialog = new Dialog_AirdropTradeConfirmWithAlternative(
                tradeLabel,
                quantity,
                requestedQuantity,
                paymentTotal,
                unitPrice,
                priceTag,
                shippingCost,
                shippingPods,
                adjustmentReason,
                basketDetails,
                hasManualAlternative,
                alternativeLabel,
                cancelLabel,
                () => CommitConfirmedAirdropTrade(state.Session, state.Faction, state.PreparedTrade),
                () => CancelConfirmedAirdropTrade(state.Session, state.Faction),
                () =>
                {
                    if (!isTradeCardAcceptance)
                    {
                        return;
                    }

                    ItemAirdropTradeCardPayload revision = BuildPendingAirdropTradeCardEditorPayload();
                    ReturnConfirmedAirdropTradeToNegotiation(state.Session, state.Faction);
                    if (state.Session != null && state.Faction != null && revision != null)
                    {
                        Find.WindowStack.Add(new Dialog_ItemAirdropTradeCard(
                            state.Session,
                            state.Faction,
                            OnAirdropTradeCardSubmitted,
                            revision));
                    }
                });
            Find.WindowStack.Add(confirmationDialog);
        }

        private static string BuildAirdropConfirmationBasketDetails(ItemAirdropPreparedTradeData trade)
        {
            string deliveries = string.Join("\n", (trade?.DeliveryLines ?? new List<ItemAirdropTradeLine>())
                .Select(line => $"• {(string.IsNullOrWhiteSpace(line.Label) ? line.DefName : line.Label)} x{line.Count}"));
            string payments = string.Join("\n", (trade?.PaymentLines ?? new List<ItemAirdropPreparedPaymentLine>())
                .Select(line => $"• {(string.IsNullOrWhiteSpace(line.Label) ? line.DefName : line.Label)} x{line.Count}"));
            return $"{"RimChat_AirdropFactionDelivers".Translate()}:\n{deliveries}\n\n{"RimChat_AirdropPlayerPays".Translate()}:\n{payments}";
        }

        private sealed class Dialog_AirdropTradeConfirmWithAlternative : Window
        {
            private readonly string tradeLabel;
            private readonly int quantity;
            private readonly int requestedQuantity;
            private readonly int paymentTotal;
            private readonly float unitPrice;
            private readonly string priceTag;
            private readonly int shippingCost;
            private readonly int shippingPods;
            private readonly string adjustmentReason;
            private readonly string basketDetails;
            private Vector2 basketScrollPosition;
            private readonly Action onConfirm;
            private readonly Action onCancel;
            private readonly Action onAlternative;
            private readonly bool optionalAlternativeVisible;
            private readonly string alternativeLabel;
            private readonly string cancelLabel;

            public override Vector2 InitialSize => new Vector2(500f, string.IsNullOrWhiteSpace(basketDetails) ? 320f : 420f);

            public Dialog_AirdropTradeConfirmWithAlternative(
                string tradeLabel,
                int quantity,
                int requestedQuantity,
                int paymentTotal,
                float unitPrice,
                string priceTag,
                int shippingCost,
                int shippingPods,
                string adjustmentReason,
                string basketDetails,
                bool hasAlternative,
                string alternativeLabel,
                string cancelLabel,
                Action onConfirm,
                Action onCancel,
                Action onAlternative)
            {
                this.tradeLabel = tradeLabel ?? string.Empty;
                this.quantity = Math.Max(1, quantity);
                this.requestedQuantity = Math.Max(1, requestedQuantity);
                this.paymentTotal = paymentTotal;
                this.unitPrice = unitPrice;
                this.priceTag = priceTag ?? string.Empty;
                this.shippingCost = shippingCost;
                this.shippingPods = shippingPods;
                this.adjustmentReason = adjustmentReason ?? string.Empty;
                this.basketDetails = basketDetails ?? string.Empty;
                this.onConfirm = onConfirm;
                this.onCancel = onCancel;
                this.onAlternative = onAlternative;
                this.optionalAlternativeVisible = hasAlternative;
                this.alternativeLabel = alternativeLabel ?? string.Empty;
                this.cancelLabel = cancelLabel ?? string.Empty;
                forcePause = true;
                doCloseX = false;
                absorbInputAroundWindow = true;
                closeOnClickedOutside = false;
                closeOnCancel = false;
                closeOnAccept = false;
            }

            public override void DoWindowContents(Rect inRect)
            {
                float y = inRect.y;

                // Title
                Text.Font = GameFont.Medium;
                string title = "RimChat_ItemAirdropConfirmTitle".Translate();
                float titleHeight = Text.CalcHeight(title, inRect.width);
                Widgets.Label(new Rect(inRect.x, y, inRect.width, titleHeight), title);
                y += titleHeight + 12f;

                // Key info — large
                Text.Font = GameFont.Medium;
                string mainLine = "RimChat_ItemAirdropConfirmMainLine".Translate(tradeLabel, quantity, paymentTotal);
                float mainHeight = Text.CalcHeight(mainLine, inRect.width - 20f);
                Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, mainHeight), mainLine);
                y += mainHeight + 6f;

                if (!string.IsNullOrWhiteSpace(basketDetails))
                {
                    Rect basketRect = new Rect(inRect.x + 10f, y, inRect.width - 20f, 120f);
                    Widgets.DrawBoxSolid(basketRect, new Color(0.10f, 0.10f, 0.13f, 0.75f));
                    Text.Font = GameFont.Tiny;
                    float textHeight = Math.Max(basketRect.height, Text.CalcHeight(basketDetails, basketRect.width - 24f));
                    Rect view = new Rect(0f, 0f, basketRect.width - 16f, textHeight);
                    basketScrollPosition = GUI.BeginScrollView(basketRect, basketScrollPosition, view);
                    Widgets.Label(new Rect(6f, 4f, view.width - 12f, textHeight), basketDetails);
                    GUI.EndScrollView();
                    y += basketRect.height + 8f;
                }

                // Quantity adjustment note
                Text.Font = GameFont.Tiny;
                Color dimGray = new Color(0.55f, 0.55f, 0.55f);
                Color prevColor = GUI.color;

                if (quantity != requestedQuantity && requestedQuantity > 0)
                {
                    string adjStr = string.IsNullOrWhiteSpace(adjustmentReason)
                        ? "RimChat_ItemAirdropConfirmAdjusted".Translate(requestedQuantity, quantity)
                        : "RimChat_ItemAirdropConfirmAdjustedReason".Translate(requestedQuantity, quantity, adjustmentReason);
                    GUI.color = new Color(0.9f, 0.6f, 0.2f); // amber warning
                    float adjH = Text.CalcHeight(adjStr, inRect.width - 20f);
                    Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, adjH), adjStr);
                    y += adjH + 2f;
                }

                // Secondary details
                if (unitPrice > 0f)
                {
                    string unitStr = string.IsNullOrWhiteSpace(priceTag)
                        ? "RimChat_ItemAirdropConfirmUnitPrice".Translate(unitPrice.ToString("F1"))
                        : "RimChat_ItemAirdropConfirmUnitPriceTagged".Translate(unitPrice.ToString("F1"), priceTag);
                    GUI.color = dimGray;
                    float unitH = Text.CalcHeight(unitStr, inRect.width - 20f);
                    Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, unitH), unitStr);
                    y += unitH + 2f;
                }

                if (shippingCost > 0)
                {
                    string shipStr = "RimChat_ItemAirdropConfirmShippingShort".Translate(shippingCost, shippingPods);
                    GUI.color = dimGray;
                    float shipH = Text.CalcHeight(shipStr, inRect.width - 20f);
                    Widgets.Label(new Rect(inRect.x + 10f, y, inRect.width - 20f, shipH), shipStr);
                    y += shipH + 2f;
                }

                GUI.color = prevColor;

                // Buttons
                float buttonTop = inRect.yMax - 48f;
                float buttonWidth = (inRect.width - 20f) / 2f;
                Rect confirmRect = new Rect(inRect.x + 5f, buttonTop, buttonWidth, 42f);
                Rect cancelRect = new Rect(confirmRect.xMax + 10f, buttonTop, buttonWidth, 42f);

                Text.Font = GameFont.Medium;
                if (Widgets.ButtonText(confirmRect, "RimChat_ItemAirdropConfirmAccept".Translate()))
                {
                    onConfirm?.Invoke();
                    Close();
                    return;
                }

                if (Widgets.ButtonText(cancelRect, cancelLabel))
                {
                    onCancel?.Invoke();
                    Close();
                    return;
                }

                if (!optionalAlternativeVisible)
                {
                    return;
                }

                // Low-visibility alternative link — simple label-based button, no Anchor manipulation
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.45f, 0.45f, 0.45f);
                float altY = cancelRect.y - 24f;
                Rect alternativeRect = new Rect(inRect.x, altY, inRect.width, 20f);
                if (Widgets.ButtonText(alternativeRect, alternativeLabel))
                {
                    onAlternative?.Invoke();
                    Close();
                }
                GUI.color = prevColor;
            }
        }

        private static string BuildPriceSemanticTag(string semantic)
        {
            if (string.IsNullOrWhiteSpace(semantic))
            {
                return string.Empty;
            }

            string lower = semantic.ToLowerInvariant().Trim();
            if (lower.StartsWith("special_item_discount"))
                return "RimChat_ItemAirdropPriceSemanticDiscount".Translate().ToString();
            if (lower.StartsWith("special_item_scarce"))
                return "RimChat_ItemAirdropPriceSemanticScarce".Translate().ToString();
            if (lower.StartsWith("market_value") || lower.StartsWith("market_value_x"))
                return "RimChat_ItemAirdropPriceSemanticMarket".Translate().ToString();
            if (lower.StartsWith("untradeable_") || lower.StartsWith("black_market"))
                return "RimChat_ItemAirdropPriceSemanticBlackMarket".Translate().ToString();

            return string.Empty;
        }

        private void CommitConfirmedAirdropTrade(
            FactionDialogueSession currentSession,
            Faction currentFaction,
            ItemAirdropPreparedTradeData preparedTrade)
        {
            if (currentSession != null)
            {
                if (currentSession.airdropExecutionStage != AirdropExecutionStage.PreparedAwaitingConfirm)
                {
                    Log.Warning($"[RimChat] AirdropStalePendingBlocked: commit rejected because stage={currentSession.airdropExecutionStage},expected={AirdropExecutionStage.PreparedAwaitingConfirm}");
                    MarkAirdropTradeCardFailed(preparedTrade, currentSession);
                    ResetAirdropConfirmationRuntime(currentSession, "commit_rejected_wrong_stage", true, false);
                    TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, "commit_rejected_wrong_stage");
                    currentSession.AddMessage(
                        "System",
                        BuildAirdropFailureSystemMessage("selection_manual_choice"),
                        false,
                        DialogueMessageType.System);
                    SaveFactionMemory(currentSession, currentFaction);
                    return;
                }

                if (HasStalePendingAirdropSelection(currentSession, out string staleDetails))
                {
                    Log.Warning($"[RimChat] AirdropStalePendingBlocked: commit rejected because stale pending state survived until confirm. {staleDetails}");
                    MarkAirdropTradeCardFailed(preparedTrade, currentSession);
                    ResetAirdropConfirmationRuntime(currentSession, "commit_rejected_stale_pending", true, false);
                    TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, staleDetails);
                    currentSession.AddMessage(
                        "System",
                        BuildAirdropFailureSystemMessage("selection_manual_choice"),
                        false,
                        DialogueMessageType.System);
                    SaveFactionMemory(currentSession, currentFaction);
                    return;
                }

                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Committing, preparedTrade?.SelectedDefName ?? "prepared_trade");
                MarkAirdropTradeCardExecuting(preparedTrade, currentSession);
            }

            Log.Message($"[RimChat] AirdropConfirmCommitStart: def={preparedTrade?.SelectedDefName ?? "unknown"},count={preparedTrade?.Quantity ?? 0},budget={preparedTrade?.BudgetSilver ?? 0}");
            var commitResult = GameAIInterface.Instance.CommitPreparedItemAirdropTrade(currentFaction, preparedTrade);
            if (commitResult.Success)
            {
                MarkAirdropTradeCardCompleted(preparedTrade, currentSession);
                ResetAirdropConfirmationRuntime(currentSession, "commit_success", true, true);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Completed, preparedTrade?.SelectedDefName ?? "commit_success");
                var payload = commitResult.Data as ItemAirdropResultData;
                string text = payload != null
                    ? BuildAirdropSuccessSystemMessage(payload)
                    : "RimChat_ItemAirdropCommitSuccessSystem".Translate().ToString();
                currentSession?.AddMessage("System", text, false, DialogueMessageType.System);
                Log.Message($"[RimChat] AirdropConfirmCommitResult: success=True,def={payload?.SelectedDefName ?? preparedTrade?.SelectedDefName ?? "unknown"},count={payload?.Quantity ?? preparedTrade?.Quantity ?? 0},failureCode=none");
            }
            else
            {
                MarkAirdropTradeCardFailed(preparedTrade, currentSession);
                ResetAirdropConfirmationRuntime(currentSession, "commit_failed", true, false);
                string transitionReason = commitResult?.Message ?? "commit_failed";
                var payload = commitResult.Data as ItemAirdropResultData;
                if (!string.IsNullOrWhiteSpace(payload?.FailureCode))
                {
                    transitionReason = payload.FailureCode;
                }

                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, transitionReason);
                if (payload != null && !string.IsNullOrWhiteSpace(payload.FailureCode))
                {
                    currentSession?.AddMessage(
                        "System",
                        BuildAirdropFailureSystemMessage(payload.FailureCode),
                        false,
                        DialogueMessageType.System);
                }
                else
                {
                    string reason = string.IsNullOrWhiteSpace(commitResult?.Message)
                        ? "RimChat_Unknown".Translate().ToString()
                        : commitResult.Message;
                    currentSession?.AddMessage(
                        "System",
                        "RimChat_ItemAirdropCommitFailedSystem".Translate(reason),
                        false,
                        DialogueMessageType.System);
                }

                Log.Message($"[RimChat] AirdropConfirmCommitResult: success=False,def={preparedTrade?.SelectedDefName ?? "unknown"},count={preparedTrade?.Quantity ?? 0},failureCode={payload?.FailureCode ?? "none"},message={commitResult?.Message ?? "none"}");
            }

            SaveFactionMemory(currentSession, currentFaction);
        }

        private void ReturnConfirmedAirdropTradeToNegotiation(
            FactionDialogueSession currentSession,
            Faction currentFaction)
        {
            if (!string.IsNullOrWhiteSpace(currentSession?.pendingAirdropTradeCardRequestId))
            {
                currentSession.SetAirdropTradeCardStatus(
                    currentSession.pendingAirdropTradeCardRequestId,
                    AirdropTradeCardStatus.Pending);
            }

            ResetAirdropConfirmationRuntime(
                currentSession,
                "player_revising_confirmation",
                disposeLease: true,
                clearTradeCardReference: false,
                resetStageToIdle: true);
            Log.Message(
                $"[RimChat] AirdropConfirmRevision: requestId={currentSession?.pendingAirdropTradeCardRequestId ?? "none"},faction={currentFaction?.Name ?? "null"}");
            SaveFactionMemory(currentSession, currentFaction);
        }

        private void CancelConfirmedAirdropTrade(FactionDialogueSession currentSession, Faction currentFaction, bool skipSystemMessage = false)
        {
            MarkAirdropTradeCardCancelled(currentSession);
            ResetAirdropConfirmationRuntime(currentSession, "commit_cancelled", true, true, true);
            TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Idle, "player_cancelled_confirmation");
            Log.Message($"[RimChat] AirdropConfirmExplicitCancel: stage={currentSession?.airdropExecutionStage.ToString() ?? "null"},faction={currentFaction?.Name ?? "null"}");

            if (!skipSystemMessage)
            {
                currentSession?.AddMessage(
                    "System",
                    "RimChat_ItemAirdropCancelledSystem".Translate(),
                    false,
                    DialogueMessageType.System);
            }

            SaveFactionMemory(currentSession, currentFaction);
        }
    }
}
