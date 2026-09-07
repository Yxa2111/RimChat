using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.UI
{
    /// <summary>/// Dependencies: AIAction parser output, GameComponent_DiplomacyManager social APIs.
    /// Responsibility: handle explicit social post actions.
 ///</summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private bool TryHandleSocialCircleAction(
            AIAction action,
            FactionDialogueSession currentSession,
            Faction currentFaction,
            out ActionExecutionOutcome outcome)
        {
            outcome = null;
            if (action == null || !string.Equals(action.ActionType, AIActionNames.PublishPublicPost, StringComparison.Ordinal))
            {
                return false;
            }

            var manager = GameComponent_DiplomacyManager.Instance;
            if (manager == null || currentFaction == null)
            {
                outcome = ActionExecutionOutcome.Failure(action, "Social-circle manager or faction is unavailable.");
                return true;
            }

            if (!(RimChat.Core.RimChatMod.Instance?.InstanceSettings?.EnablePlayerInfluenceNews ?? true))
            {
                string message = "RimChat_SocialActionBlocked".Translate().ToString();
                outcome = ActionExecutionOutcome.Failure(action, message);
                return true;
            }

            Dictionary<string, object> parameters = action.Parameters ?? new Dictionary<string, object>();
            string targetToken = GetStringParameter(parameters, "targetFaction");
            string summary = GetStringParameter(parameters, "summary");
            string intentHint = GetStringParameter(parameters, "intentHint");
            string categoryToken = GetStringParameter(parameters, "category");
            int sentiment = ParseSentiment(parameters);

            Faction targetFaction = manager.ResolveSocialTargetFaction(targetToken, currentFaction);
            SocialPostCategory category = ParseCategory(categoryToken);
            bool ok = manager.EnqueuePublicPost(
                currentFaction,
                targetFaction,
                category,
                sentiment,
                summary,
                true,
                out SocialPostEnqueueResult enqueueResult,
                intentHint,
                DebugGenerateReason.DialogueExplicit);

            string systemMessage = ok
                ? "RimChat_SocialActionQueued".Translate()
                : "RimChat_SocialActionFailedReason".Translate(
                    GameComponent_DiplomacyManager.GetSocialFailureReasonLabel(enqueueResult.FailureReason));
            if (ok)
            {
                currentSession?.AddMessage("System", systemMessage, false, DialogueMessageType.System);
            }
            outcome = ok
                ? ActionExecutionOutcome.Success(action, systemMessage)
                : ActionExecutionOutcome.Failure(action, systemMessage);
            return true;
        }

        private static string GetStringParameter(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || string.IsNullOrEmpty(key)) return string.Empty;
            if (!parameters.TryGetValue(key, out object value) || value == null) return string.Empty;
            return value.ToString().Trim();
        }

        private static int ParseSentiment(Dictionary<string, object> parameters)
        {
            if (TryReadInt(parameters, "sentiment", out int sentiment))
            {
                return Math.Max(-2, Math.Min(2, sentiment));
            }
            if (TryReadInt(parameters, "amount", out int amount))
            {
                return Math.Max(-2, Math.Min(2, amount));
            }
            return 0;
        }

        private static bool TryReadInt(Dictionary<string, object> parameters, string key, out int value)
        {
            value = 0;
            if (parameters == null || !parameters.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is int intValue)
            {
                value = intValue;
                return true;
            }

            if (raw is float floatValue)
            {
                value = Mathf.RoundToInt(floatValue);
                return true;
            }

            return int.TryParse(raw.ToString(), out value);
        }

        private static SocialPostCategory ParseCategory(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return SocialPostCategory.Diplomatic;
            }

            if (Enum.TryParse(token, true, out SocialPostCategory parsed))
            {
                return parsed;
            }
            return SocialPostCategory.Diplomatic;
        }
    }
}


