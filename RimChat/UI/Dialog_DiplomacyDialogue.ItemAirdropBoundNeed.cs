using System;
using System.Collections.Generic;
using RimChat.AI;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using Verse;

namespace RimChat.UI
{
    /// <summary>
    /// Dependencies: diplomacy parsed actions, session-scoped airdrop trade-card reference state.
    /// Responsibility: inject bound-need metadata into request_item_airdrop actions before execution.
    /// </summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private static bool TryInjectPendingAirdropTradeCardMetadata(
            AIAction action,
            FactionDialogueSession currentSession,
            out string failureMessage)
        {
            failureMessage = string.Empty;
            if (action == null ||
                !string.Equals(action.ActionType, AIActionNames.RequestItemAirdrop, StringComparison.Ordinal))
            {
                return true;
            }

            if (action.Parameters == null)
            {
                action.Parameters = new Dictionary<string, object>(StringComparer.Ordinal);
            }

            // A live trade card can only be fulfilled by an action expanded from
            // accept_item_airdrop. A direct request_item_airdrop cannot bypass its id.
            if (currentSession?.hasPendingAirdropTradeCardReference == true)
            {
                if (!HasAirdropTradeCardRequestId(action))
                {
                    failureMessage = "RimChat_ItemAirdropAcceptUseRequestId".Translate().ToString();
                    return false;
                }

                string requestId = GetAirdropTradeCardRequestId(action);
                if (!currentSession.IsCurrentAirdropTradeCardRequest(requestId))
                {
                    failureMessage = "RimChat_ItemAirdropAcceptRequestExpired".Translate(requestId).ToString();
                    return false;
                }
            }

            return TryInjectPendingAirdropTradeCardMetadata(action.Parameters, currentSession, out failureMessage);
        }

        private static bool TryInjectPendingAirdropTradeCardMetadata(
            Dictionary<string, object> parameters,
            FactionDialogueSession currentSession,
            out string failureMessage)
        {
            failureMessage = string.Empty;
            if (parameters == null ||
                currentSession == null ||
                !currentSession.hasPendingAirdropTradeCardReference)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(currentSession.pendingAirdropTradeCardNeedDefName))
            {
                currentSession.ClearPendingAirdropTradeCardReference();
                failureMessage = BuildPendingAirdropTradeCardStateLostMessage();
                return false;
            }

            parameters[ItemAirdropParameterKeys.BoundNeedDefName] = currentSession.pendingAirdropTradeCardNeedDefName;
            parameters[ItemAirdropParameterKeys.BoundNeedLabel] = currentSession.pendingAirdropTradeCardNeedLabel ?? string.Empty;
            parameters[ItemAirdropParameterKeys.BoundNeedSearchText] = currentSession.pendingAirdropTradeCardNeedSearchText ?? string.Empty;
            parameters[ItemAirdropParameterKeys.BoundNeedSource] = "trade_card";
            return true;
        }

        private static string BuildPendingAirdropTradeCardStateLostMessage()
        {
            return "RimChat_ItemAirdropBoundNeedStateLostSystem".Translate().ToString();
        }
    }
}
