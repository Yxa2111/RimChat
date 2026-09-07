using RimChat.Memory;
using RimWorld;
using Verse;

namespace RimChat.UI
{
    /// <summary>
    /// Dependencies: diplomacy dialogue session runtime state.
    /// Responsibility: centralize airdrop confirmation state transitions and stale-pending cleanup.
    /// </summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private static void TransitionAirdropExecutionStage(
            FactionDialogueSession currentSession,
            AirdropExecutionStage nextStage,
            string reason)
        {
            if (currentSession == null)
            {
                return;
            }

            AirdropExecutionStage previousStage = currentSession.airdropExecutionStage;
            currentSession.airdropExecutionStage = nextStage;
            Log.Message($"[RimChat] AirdropStateTransition: {previousStage} -> {nextStage} reason={reason ?? "none"}");
        }

        private static void ResetAirdropConfirmationRuntime(
            FactionDialogueSession currentSession,
            string reason,
            bool disposeLease,
            bool clearTradeCardReference = false,
            bool resetStageToIdle = false)
        {
            if (currentSession == null)
            {
                return;
            }

            bool hadAsyncState =
                currentSession.isWaitingForAirdropSelection ||
                !string.IsNullOrWhiteSpace(currentSession.pendingAirdropRequestId) ||
                currentSession.pendingAirdropRequestLease != null;
            if (hadAsyncState)
            {
                ClearAirdropAsyncRequestState(currentSession, disposeLease);
            }

            if (clearTradeCardReference && currentSession.hasPendingAirdropTradeCardReference)
            {
                currentSession.ClearPendingAirdropTradeCardReference();
            }

            if (resetStageToIdle)
            {
                currentSession.airdropExecutionStage = AirdropExecutionStage.Idle;
                currentSession.airdropPreparedAwaitingConfirmTick = 0;
            }

            if (hadAsyncState || clearTradeCardReference || resetStageToIdle)
            {
                currentSession.airdropRequestGeneration++;
                Log.Message(
                    $"[RimChat] AirdropRuntimeReset: reason={reason ?? "none"},clearedAsyncState={hadAsyncState},clearedTradeCard={clearTradeCardReference},resetStageToIdle={resetStageToIdle},generation={currentSession.airdropRequestGeneration}");
            }
        }

        private static bool HasStalePendingAirdropSelection(
            FactionDialogueSession currentSession,
            out string details)
        {
            details = string.Empty;
            if (currentSession == null)
            {
                return false;
            }

            bool hasAsyncState =
                currentSession.isWaitingForAirdropSelection ||
                !string.IsNullOrWhiteSpace(currentSession.pendingAirdropRequestId) ||
                currentSession.pendingAirdropRequestLease != null;
            if (!hasAsyncState)
            {
                return false;
            }

            details =
                $"stage={currentSession.airdropExecutionStage},isWaitingForSelection={currentSession.isWaitingForAirdropSelection}," +
                $"requestId={currentSession.pendingAirdropRequestId ?? "none"},hasLease={(currentSession.pendingAirdropRequestLease != null)}";
            return true;
        }

        private const int AirdropPreparedAwaitingConfirmTimeoutTicks = 5000; // 2 game hours

        private static bool ReturnUncommittedAirdropTradeCardToPending(
            FactionDialogueSession currentSession,
            string reason)
        {
            if (currentSession == null)
            {
                return false;
            }

            bool hasRecoverableCard = currentSession.hasPendingAirdropTradeCardReference &&
                (currentSession.pendingAirdropTradeCardStatus == AirdropTradeCardStatus.Preparing ||
                 currentSession.pendingAirdropTradeCardStatus == AirdropTradeCardStatus.AwaitingPlayerConfirm);
            bool hasUncommittedRuntime = currentSession.airdropExecutionStage == AirdropExecutionStage.SelectingCandidate ||
                currentSession.airdropExecutionStage == AirdropExecutionStage.PreparedAwaitingConfirm;
            if (!hasRecoverableCard && !hasUncommittedRuntime)
            {
                return false;
            }

            if (hasRecoverableCard && !string.IsNullOrWhiteSpace(currentSession.pendingAirdropTradeCardRequestId))
            {
                currentSession.SetAirdropTradeCardStatus(
                    currentSession.pendingAirdropTradeCardRequestId,
                    AirdropTradeCardStatus.Pending);
            }

            ResetAirdropConfirmationRuntime(
                currentSession,
                reason,
                disposeLease: true,
                clearTradeCardReference: false,
                resetStageToIdle: true);
            return true;
        }

        private void TryAutoCleanupStaleAirdropConfirmation(FactionDialogueSession session, Faction faction)
        {
            if (session == null || faction == null) return;
            if (session.airdropExecutionStage != AirdropExecutionStage.PreparedAwaitingConfirm) return;
            if (session.airdropPreparedAwaitingConfirmTick <= 0) return;

            int currentTick = Find.TickManager?.TicksGame ?? 0;
            int elapsed = currentTick - session.airdropPreparedAwaitingConfirmTick;
            if (elapsed < AirdropPreparedAwaitingConfirmTimeoutTicks) return;

            Log.Warning($"[RimChat] Airdrop auto-cleanup: PreparedAwaitingConfirm stale for {elapsed} ticks (> {AirdropPreparedAwaitingConfirmTimeoutTicks}). Returning the quote to Pending for faction={faction.Name}.");
            ReturnUncommittedAirdropTradeCardToPending(session, "auto_cleanup_stale_confirmation");
            ClearPendingAirdropDialogState("auto_cleanup", true);
            SaveFactionMemory(session, faction);
        }
    }
}
