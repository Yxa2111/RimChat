using System;
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
    /// Dependencies: Dialogue runtime context guards, GameAIInterface async airdrop API, session runtime state.
    /// Responsibility: track async airdrop request lifecycle and apply async completion safely.
    /// </summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private static bool IsAirdropAsyncRequestPending(FactionDialogueSession currentSession)
        {
            return currentSession != null &&
                   currentSession.isWaitingForAirdropSelection &&
                   !string.IsNullOrWhiteSpace(currentSession.pendingAirdropRequestId);
        }

        private static void BindAirdropAsyncRequest(
            FactionDialogueSession currentSession,
            DialogueRequestLease lease,
            string requestId,
            int timeoutSeconds)
        {
            if (currentSession == null || lease == null || string.IsNullOrWhiteSpace(requestId))
            {
                return;
            }

            lease.BindRequestId(requestId);
            currentSession.airdropRequestGeneration++;
            int requestGeneration = currentSession.airdropRequestGeneration;
            currentSession.pendingAirdropRequestId = requestId;
            currentSession.pendingAirdropRequestLease = lease;
            currentSession.isWaitingForAirdropSelection = true;
            currentSession.pendingAirdropRequestStartedRealtime = Time.realtimeSinceStartup;
            currentSession.pendingAirdropRequestTimeoutSeconds = Mathf.Max(0, timeoutSeconds);
            TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.SelectingCandidate, $"requestId={requestId},timeout={timeoutSeconds},generation={requestGeneration}");
        }

        private static void ClearAirdropAsyncRequestState(FactionDialogueSession currentSession, bool disposeLease)
        {
            if (currentSession == null)
            {
                return;
            }

            if (disposeLease)
            {
                currentSession.pendingAirdropRequestLease?.Dispose();
            }

            currentSession.pendingAirdropRequestId = null;
            currentSession.pendingAirdropRequestLease = null;
            currentSession.isWaitingForAirdropSelection = false;
            currentSession.pendingAirdropRequestStartedRealtime = -1f;
            currentSession.pendingAirdropRequestTimeoutSeconds = 0;
        }

        private void HandleAirdropAsyncPrepareCompleted(
            FactionDialogueSession currentSession,
            Faction currentFaction,
            DialogueRequestLease lease,
            DialogueRuntimeContext requestContext,
            AIAction sourceAction,
            int expectedGeneration,
            GameAIInterface.APIResult prepareResult)
        {
            if (!IsAirdropAsyncContextValid(currentSession, currentFaction, lease, requestContext, sourceAction, expectedGeneration))
            {
                Log.Warning($"[RimChat] AirdropStalePendingBlocked: requestId={lease?.RequestId ?? "none"},stage={currentSession?.airdropExecutionStage.ToString() ?? "null"},expectedGeneration={expectedGeneration},actualGeneration={currentSession?.airdropRequestGeneration ?? -1},faction={currentFaction?.Name ?? "null"}");
                return;
            }

            ClearAirdropAsyncRequestState(currentSession, true);

            if (prepareResult == null)
            {
                ResetAirdropConfirmationRuntime(currentSession, "prepareResult=null", true, false);
                MarkAirdropTradeCardFailed(sourceAction, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, "prepareResult=null");
                currentSession.AddMessage(
                    "System",
                    "RimChat_ItemAirdropCommitFailedSystem".Translate("RimChat_Unknown".Translate().ToString()),
                    false,
                    DialogueMessageType.System);
                SaveFactionMemory(currentSession, currentFaction);
                return;
            }

            if (!prepareResult.Success)
            {
                string reason = string.IsNullOrWhiteSpace(prepareResult.Message)
                    ? "RimChat_Unknown".Translate().ToString()
                    : prepareResult.Message;
                ResetAirdropConfirmationRuntime(currentSession, "async_prepare_failed", true, false);
                MarkAirdropTradeCardFailed(sourceAction, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, reason);
                currentSession.AddMessage(
                    "System",
                    "RimChat_ItemAirdropCommitFailedSystem".Translate(reason),
                    false,
                    DialogueMessageType.System);
                SaveFactionMemory(currentSession, currentFaction);
                return;
            }

            if (prepareResult.Data is ItemAirdropPendingSelectionData pendingSelection)
            {
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.SelectingCandidate, pendingSelection.FailureCode ?? "selection_pending");
                if (DeterminePendingSelectionResolution(pendingSelection) == AirdropPendingResolution.AutoPickTop1 &&
                    TryAutoPickPendingAirdropSelection(sourceAction, pendingSelection, currentSession, currentFaction, out _))
                {
                    SaveFactionMemory(currentSession, currentFaction);
                }
                else
                {
                    CacheAirdropPendingSelectionIntent(currentSession, sourceAction, pendingSelection);
                    currentSession.AddMessage(
                        "System",
                        "RimChat_ItemAirdropCommitFailedSystem".Translate(BuildAirdropPendingSelectionSystemText(pendingSelection)),
                        false,
                        DialogueMessageType.System);
                    SaveFactionMemory(currentSession, currentFaction);
                }
                return;
            }

            if (prepareResult.Data is ItemAirdropPreparedTradeData preparedTrade)
            {
                ClearPendingAirdropDialogState("async_prepare_new_confirmation", false);
                currentSession?.ClearPendingAirdropExecutionState();
                if (!TryValidatePreparedTradeAgainstAirdropTradeCard(sourceAction, currentSession, preparedTrade, out string termsFailure))
                {
                    ResetAirdropConfirmationRuntime(currentSession, "async_prepared_trade_terms_mismatch", true, false);
                    MarkAirdropTradeCardFailed(sourceAction, currentSession);
                    TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.Failed, termsFailure);
                    currentSession.AddMessage(
                        "System",
                        "RimChat_ItemAirdropCommitFailedSystem".Translate(termsFailure),
                        false,
                        DialogueMessageType.System);
                    SaveFactionMemory(currentSession, currentFaction);
                    return;
                }

                ResetAirdropConfirmationRuntime(currentSession, "async_prepared_trade_ready", true, false);
                MarkAirdropTradeCardAwaitingConfirm(sourceAction, currentSession);
                TransitionAirdropExecutionStage(currentSession, AirdropExecutionStage.PreparedAwaitingConfirm, preparedTrade.SelectedDefName ?? "prepared_trade");
                ShowAirdropTradeConfirmationDialog(currentSession, currentFaction, preparedTrade, null, null);
                SaveFactionMemory(currentSession, currentFaction);
            }
        }

        private static bool IsAirdropAsyncContextValid(
            FactionDialogueSession currentSession,
            Faction currentFaction,
            DialogueRequestLease lease,
            DialogueRuntimeContext requestContext,
            AIAction sourceAction,
            int expectedGeneration)
        {
            if (currentSession == null || currentFaction == null || currentFaction.defeated || lease == null)
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=session_null_or_faction_defeated_or_lease_null sessionNull={currentSession == null} factionNull={currentFaction == null} factionDefeated={currentFaction?.defeated} leaseNull={lease == null}");
                return false;
            }

            if (expectedGeneration != currentSession.airdropRequestGeneration)
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=generation_mismatch expected={expectedGeneration} actual={currentSession.airdropRequestGeneration}");
                return false;
            }

            string requestId = lease.RequestId;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=request_id_empty");
                return false;
            }

            if (!string.Equals(currentSession.pendingAirdropRequestId, requestId, StringComparison.Ordinal))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=request_id_mismatch sessionId={currentSession.pendingAirdropRequestId} leaseId={requestId}");
                return false;
            }

            string tradeCardRequestId = GetAirdropTradeCardRequestId(sourceAction);
            if (!string.IsNullOrWhiteSpace(tradeCardRequestId) &&
                !currentSession.IsCurrentAirdropTradeCardRequest(tradeCardRequestId))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=trade_card_request_id_mismatch cardId={tradeCardRequestId},currentCardId={currentSession.pendingAirdropTradeCardRequestId}");
                return false;
            }

            if (currentSession.pendingAirdropRequestLease == null ||
                !ReferenceEquals(currentSession.pendingAirdropRequestLease, lease))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=lease_reference_mismatch sessionLeaseNull={currentSession.pendingAirdropRequestLease == null}");
                return false;
            }

            if (!lease.IsValidFor(requestId, requestContext?.DialogueSessionId ?? string.Empty, requestContext?.ContextVersion ?? -1))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=lease_not_valid sessionId={requestContext?.DialogueSessionId} version={requestContext?.ContextVersion}");
                return false;
            }

            DialogueRuntimeContext resolveContext = requestContext?.WithCurrentRuntimeMarkers();
            if (!DialogueContextResolver.TryResolveLiveContext(resolveContext, out DialogueLiveContext liveContext, out _))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=resolve_live_context_failed");
                return false;
            }

            if (!DialogueContextValidator.ValidateCallbackApply(requestContext, liveContext, requestContext?.DialogueSessionId, out _))
            {
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=callback_validate_failed");
                return false;
            }

            FactionDialogueSession liveSession = GameComponent_DiplomacyManager.Instance?.GetSession(currentFaction);
            bool sessionMatch = ReferenceEquals(liveSession, currentSession);
            if (!sessionMatch)
                Log.Warning($"[RimChat] AirdropAsyncContextInvalid: reason=session_reference_mismatch");
            return sessionMatch;
        }

        private void CancelPendingAirdropSelectionRequest()
        {
            if (session == null || string.IsNullOrWhiteSpace(session.pendingAirdropRequestId))
            {
                return;
            }

            GameAIInterface.Instance.CancelItemAirdropAsyncRequest(
                session.pendingAirdropRequestId,
                "airdrop_selection_cancelled_by_window_close",
                "Airdrop selection request cancelled by dialogue close.");
            ClearAirdropAsyncRequestState(session, true);
        }

        private bool TryGetPendingAirdropRequestStatus(out AIRequestResult status)
        {
            status = null;
            if (session == null || string.IsNullOrWhiteSpace(session.pendingAirdropRequestId))
            {
                return false;
            }

            status = AIChatServiceAsync.Instance.GetRequestStatus(session.pendingAirdropRequestId);
            return status != null;
        }

        private bool TryBuildAirdropAsyncStatusText(out string statusText)
        {
            statusText = string.Empty;
            if (!IsAirdropAsyncRequestPending(session))
            {
                return false;
            }

            if (TryGetPendingAirdropRequestStatus(out AIRequestResult status) && IsQueuedRequestState(status))
            {
                int requestsAhead = GetQueuedRequestsAhead(status);
                if (requestsAhead == 0)
                {
                    statusText = "RimChat_DiplomacyRequestQueuedHead".Translate().ToString();
                }
                else
                {
                    statusText = "RimChat_DiplomacyRequestQueued".Translate(requestsAhead).ToString();
                }
                return true;
            }

            statusText = "RimChat_ItemAirdropSelectionInProgressBar".Translate().ToString();
            return true;
        }
    }
}
