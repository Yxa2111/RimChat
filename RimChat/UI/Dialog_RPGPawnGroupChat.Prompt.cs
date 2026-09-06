using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimChat.AI;
using RimChat.Config;
using RimChat.Core;
using RimChat.Persistence;
using RimChat.Rpg;
using RimWorld;
using Verse;

namespace RimChat.UI
{
    public partial class Dialog_RPGPawnGroupChat
    {
        private List<ChatMessageData> BuildGroupRequestMessages(GroupChatParticipant speaker, bool isFirstTurn, bool includeChoices)
        {
            var request = new List<ChatMessageData>();

            // System prompt: speaker's persona + group context
            string systemPrompt = BuildGroupSystemPrompt(speaker, isFirstTurn, includeChoices);
            request.Add(new ChatMessageData { role = "system", content = systemPrompt });

            // Build context from accumulated turns
            string contextMessage = BuildTurnContextMessage(speaker, isFirstTurn);
            request.Add(new ChatMessageData { role = "user", content = contextMessage });

            return request;
        }

        private string BuildGroupSystemPrompt(GroupChatParticipant speaker, bool isFirstTurn, bool includeChoices)
        {
            // Reuse existing RPG prompt for this speaker as a base
            string basePrompt;
            try
            {
                basePrompt = PromptPersistenceService.Instance.BuildRPGFullSystemPrompt(
                    initiator, speaker.Pawn,
                    isProactive: false,
                    additionalSceneTags: new List<string> { "group_chat" });
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[RimChat] Failed to build RPG prompt for {speaker.DisplayName}, using fallback: {ex.Message}");
                basePrompt = BuildFallbackPersonaPrompt(speaker);
            }

            var sb = new StringBuilder();
            sb.AppendLine(basePrompt);
            sb.AppendLine();
            sb.AppendLine("=== GROUP CHAT CONTEXT ===");
            sb.AppendLine($"You are in a group conversation with {participants.Count} other characters and the player.");
            sb.AppendLine("Other participants: " + string.Join(", ", participants.Where(p => p.PawnId != speaker.PawnId).Select(p => p.DisplayName)));
            sb.AppendLine("The player character is: " + initiator.LabelShort);
            sb.AppendLine();
            sb.AppendLine("GROUP CHAT RULES:");
            sb.AppendLine("- Respond naturally as your character in the group setting.");
            sb.AppendLine("- You may react to what other characters have said before you.");
            sb.AppendLine("- Stay in character. Generate 1-3 sentences of dialogue.");
            sb.AppendLine("- Do not speak for other characters or the player.");
            sb.AppendLine();
            if (!IsGroupChoiceModeEnabled)
            {
                sb.AppendLine("OUTPUT FORMAT (strict, same as 1-on-1):");
                sb.AppendLine("- Write natural dialogue as plain text.");
                sb.AppendLine("- If gameplay effects are needed, append exactly one raw JSON object in the form {\"actions\":[...]} after the dialogue.");
                sb.AppendLine("- Never wrap dialogue into JSON fields like \"dialogue\", \"response\", or \"content\".");
                sb.AppendLine("- Do NOT output ONLY JSON. Always include visible dialogue text first.");
            }

            if (IsGroupChoiceModeEnabled)
            {
                if (includeChoices)
                {
                    return RpgChoicePromptBuilder.AppendChoiceContract(
                        sb.ToString(),
                        currentRound,
                        participants.Select((participant, index) => $"npc_{index + 1}={participant.DisplayName}"),
                        lockedGroupChoiceActions,
                        groupFinalReactionRound,
                        requestInitialGroupTopics,
                        activeGroupTopic?.Text,
                        groupTopics.Where(topic => topic.IsCompleted).Select(topic => topic.Text),
                        activeGroupDialogueGraph?.IsValid == true ? activeGroupScriptNodeId : null);
                }

                sb.AppendLine();
                sb.AppendLine("=== RIMCHAT GROUP CRPG CONTRACT (HIGHEST PRIORITY) ===");
                sb.AppendLine("Return exactly {\"visible_dialogue\":\"your in-character line\",\"choices\":[]}. This is an intermediate group speaker; do not offer player choices and never emit top-level actions.");
            }

            return sb.ToString();
        }

        private string BuildFallbackPersonaPrompt(GroupChatParticipant speaker)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"You are {speaker.DisplayName}.");
            sb.AppendLine($"You are a {speaker.Pawn.KindLabel} from {speaker.Pawn.Faction?.Name ?? "an unknown faction"}.");
            sb.AppendLine("Speak naturally in character.");
            return sb.ToString();
        }

        private string BuildTurnContextMessage(GroupChatParticipant speaker, bool isFirstTurn)
        {
            var sb = new StringBuilder();

            if (isFirstTurn && turnRecords.Count == 0)
            {
                sb.AppendLine($"[Group chat begins. {speaker.DisplayName} speaks first.]");
                AppendPendingGroupResultContext(sb);
                return sb.ToString();
            }

            if (isFirstTurn && turnRecords.Count > 0)
            {
                // New round with full context
                sb.AppendLine($"[New round of group conversation. {speaker.DisplayName} responds next.]");
                sb.AppendLine();
                sb.AppendLine("Previous conversation:");
                foreach (var record in turnRecords)
                {
                    string prefix = record.IsPlayer ? $"{record.SpeakerName} (player):" : $"{record.SpeakerName}:";
                    sb.AppendLine($"{prefix} \"{record.DialogueText}\"");
                }
            }
            else
            {
                sb.AppendLine($"[Now {speaker.DisplayName} responds to the conversation.]");
                sb.AppendLine();
                if (turnRecords.Count > 0)
                {
                    sb.AppendLine("What has been said so far:");
                    foreach (var record in turnRecords)
                    {
                        string prefix = record.IsPlayer ? $"{record.SpeakerName} (player):" : $"{record.SpeakerName}:";
                        sb.AppendLine($"{prefix} \"{record.DialogueText}\"");
                    }
                }
            }

            AppendPendingGroupResultContext(sb);
            return sb.ToString();
        }

        private void AppendPendingGroupResultContext(StringBuilder sb)
        {
            foreach (string historicalResult in groupRpgResultHistory
                .Take(Math.Max(0, groupRpgResultHistory.Count - 1))
                .Skip(Math.Max(0, groupRpgResultHistory.Count - 12)))
            {
                sb.AppendLine();
                sb.AppendLine(historicalResult);
            }
            if (string.IsNullOrWhiteSpace(pendingGroupResultContext)) return;
            sb.AppendLine();
            sb.AppendLine("The following result was already rolled and applied by local game code. React to it exactly as stated; never change the roll, DC, success, or effects:");
            sb.AppendLine(pendingGroupResultContext);
        }
    }
}
