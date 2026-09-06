using System;
using System.Linq;
using RimChat.Memory;
using RimWorld;
using Verse;

namespace RimChat.UI
{
    public partial class Dialog_DiplomacyDialogue
    {
        private bool IsEditableAirdropTradeCardPending(FactionDialogueSession currentSession)
        {
            if (currentSession == null || !currentSession.hasPendingAirdropTradeCardReference)
            {
                return false;
            }

            if (currentSession.isWaitingForResponse ||
                currentSession.isWaitingForAirdropSelection ||
                !string.IsNullOrWhiteSpace(currentSession.pendingAirdropRequestId))
            {
                return false;
            }

            return currentSession.pendingAirdropTradeCardStatus == AirdropTradeCardStatus.Pending;
        }

        private DialogueMessageData GetLatestEditableAirdropTradeCard()
        {
            return session?.messages?
                .LastOrDefault(message => message != null && message.isPlayer && message.IsAirdropTradeCard());
        }

        private bool IsEditableAirdropTradeCard(DialogueMessageData message)
        {
            DialogueMessageData latest = GetLatestEditableAirdropTradeCard();
            return ReferenceEquals(latest, message) && IsEditableAirdropTradeCardPending(session);
        }

        private ItemAirdropTradeCardPayload BuildPendingAirdropTradeCardEditorPayload()
        {
            DialogueMessageData latest = GetLatestEditableAirdropTradeCard();
            if (latest == null || session == null || !session.hasPendingAirdropTradeCardReference)
            {
                return null;
            }

            TryEnsurePendingAirdropTradeCardRequestId(session, out string requestId);
            return new ItemAirdropTradeCardPayload
            {
                Need = string.IsNullOrWhiteSpace(latest.airdropNeedLabel)
                    ? latest.airdropNeedDefName
                    : $"{latest.airdropNeedLabel} x{Math.Max(1, latest.airdropRequestedCount)}",
                RequestedCount = Math.Max(1, latest.airdropRequestedCount),
                OfferItemDefName = latest.airdropOfferDefName,
                OfferItemLabel = latest.airdropOfferLabel,
                OfferItemCount = Math.Max(1, latest.airdropOfferCount),
                Scenario = session.pendingAirdropTradeCardScenario,
                NeedDefName = latest.airdropNeedDefName,
                NeedLabel = latest.airdropNeedLabel,
                NeedSearchText = session.pendingAirdropTradeCardNeedSearchText,
                NeedUnitPrice = latest.airdropNeedUnitPrice,
                NeedReferenceTotalPrice = latest.airdropNeedReferenceTotalPrice,
                OfferUnitPrice = latest.airdropOfferUnitPrice,
                OfferTotalPrice = latest.airdropOfferTotalPrice,
                ShippingPodCount = latest.airdropShippingPodCount,
                ShippingCostSilver = latest.airdropShippingCostSilver,
                RequestId = requestId,
                IsRevision = true
            };
        }

        private void OpenPendingAirdropTradeCardEditor()
        {
            if (!CanSendMessageNow() || !IsEditableAirdropTradeCardPending(session))
            {
                return;
            }

            ItemAirdropTradeCardPayload payload = BuildPendingAirdropTradeCardEditorPayload();
            if (payload == null || faction == null)
            {
                return;
            }

            Find.WindowStack.Add(new Dialog_ItemAirdropTradeCard(
                session,
                faction,
                OnAirdropTradeCardSubmitted,
                payload));
        }

        private void CancelPendingAirdropTradeCardRequest()
        {
            if (!IsEditableAirdropTradeCardPending(session))
            {
                return;
            }

            string requestId = session.pendingAirdropTradeCardRequestId;
            session.SetAirdropTradeCardStatus(requestId, AirdropTradeCardStatus.Cancelled);
            session.ClearPendingAirdropTradeCardReference();
            session.ClearPendingAirdropExecutionState();
            session.AddMessage(
                "System",
                "RimChat_ItemAirdropTradeCardCancelled".Translate().ToString(),
                false,
                DialogueMessageType.System);
            SaveFactionMemory(session, faction);
        }
    }
}
