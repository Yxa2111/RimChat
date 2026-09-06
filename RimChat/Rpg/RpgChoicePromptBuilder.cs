using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimChat.Config;
using RimChat.Core;
using UnityEngine;

namespace RimChat.Rpg
{
    public static class RpgChoicePromptBuilder
    {
        public static int RequiredChoiceCount(int round)
        {
            ResolveRoundBounds(out int softEnd, out int hardEnd);
            return round >= Math.Min(20, hardEnd) && round < hardEnd ? 3 : 2;
        }

        public static void EnforceEndingPolicy(IList<RpgDialogueChoice> choices, int round)
        {
            if (choices == null || choices.Count == 0) return;
            ResolveRoundBounds(out int softEnd, out int hardEnd);
            if (round < softEnd)
            {
                foreach (RpgDialogueChoice choice in choices) SetEnding(choice, false);
                return;
            }

            if (round >= hardEnd)
            {
                foreach (RpgDialogueChoice choice in choices) SetEnding(choice, true);
                return;
            }

            int endingCount = round >= Math.Min(20, hardEnd) ? Math.Min(2, choices.Count - 1) : 1;
            for (int i = 0; i < choices.Count; i++) SetEnding(choices[i], i < endingCount);
        }

        public static bool IsGraphChoiceAllowedAtRound(RpgDialogueChoice choice, int round, out string reason)
        {
            reason = null;
            if (choice == null) return false;
            ResolveRoundBounds(out int softEnd, out int hardEnd);
            bool successEnds = choice.Success?.EndMode != RpgDialogueEndMode.None;
            bool failureEnds = choice.Failure?.EndMode != RpgDialogueEndMode.None;
            if (round < softEnd && (successEnds || failureEnds))
            {
                reason = "ending_before_soft_round";
                return false;
            }
            if (round >= hardEnd && (!successEnds || !failureEnds))
            {
                reason = "nonending_outcome_at_hard_round";
                return false;
            }
            return true;
        }

        public static bool ValidateGraphChoiceSet(IList<RpgDialogueChoice> choices, int round, out string reason)
        {
            reason = null;
            ResolveRoundBounds(out int softEnd, out int hardEnd);
            if (round < softEnd || round >= hardEnd) return true;
            int endingChoices = choices?.Count(choice => choice?.Success?.EndMode != RpgDialogueEndMode.None || choice?.Failure?.EndMode != RpgDialogueEndMode.None) ?? 0;
            int continuingChoices = choices?.Count(choice => choice?.Success?.EndMode == RpgDialogueEndMode.None || choice?.Failure?.EndMode == RpgDialogueEndMode.None) ?? 0;
            if (round < Math.Min(20, hardEnd))
            {
                if (endingChoices >= 1 && continuingChoices >= 1) return true;
                reason = "round_requires_ending_and_continuing_choices";
                return false;
            }
            if (endingChoices >= 2 && continuingChoices >= 1) return true;
            reason = "late_round_requires_two_endings_and_one_continuation";
            return false;
        }

        public static string AppendChoiceContract(
            string basePrompt,
            int upcomingRound,
            IEnumerable<string> targetAliases,
            IEnumerable<string> lockedHighImpactActions,
            bool finalReactionOnly = false,
            bool requestTopics = false,
            string activeTopic = null,
            IEnumerable<string> completedTopics = null,
            string continuationFromNodeId = null)
        {
            var sb = new StringBuilder(basePrompt ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("=== RIMCHAT CRPG CHOICE CONTRACT (HIGHEST PRIORITY) ===");
            if (requestTopics)
            {
                sb.AppendLine("This is the conversation root. Generate 2 or 3 concrete, distinct topics that can each become a finite dialogue branch.");
                sb.AppendLine("Return exactly one JSON object with NPC opening speech, topics, and an empty choices array.");
                sb.AppendLine("Schema: {\"visible_dialogue\":\"brief in-character opening\",\"topics\":[{\"id\":\"topic_1\",\"text\":\"specific topic the player can raise\"}],\"choices\":[]}");
                sb.AppendLine("Topics are parent nodes, not player replies. Do not use generic fillers such as continue talking, keep listening, or change the subject.");
                sb.AppendLine("Never emit top-level actions, checks, outcomes, DC values, probabilities, or rolls in this stage.");
                return sb.ToString();
            }

            if (finalReactionOnly)
            {
                sb.AppendLine("Return exactly one JSON object with visible_dialogue, an empty topics array, and an empty choices array. This closes the active topic; do not offer another action.");
                sb.AppendLine("Schema: {\"visible_dialogue\":\"in-character topic-closing line\",\"topics\":[],\"choices\":[]}");
                sb.AppendLine("Never emit a top-level actions array.");
                return sb.ToString();
            }

            ResolveRoundBounds(out int softEnd, out int hardEnd);
            if (!string.IsNullOrWhiteSpace(activeTopic))
            {
                sb.AppendLine("ACTIVE TOPIC (stay inside this branch): " + activeTopic.Trim());
            }
            if (!string.IsNullOrWhiteSpace(continuationFromNodeId))
            {
                sb.AppendLine("CUSTOM REPLY CONTINUATION: The player just gave a free-form answer at script node " + continuationFromNodeId.Trim() + ". Generate a new continuation subgraph whose start node directly reacts to that answer. Do not return a single isolated reply.");
            }
            List<string> completed = (completedTopics ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (completed.Count > 0)
            {
                sb.AppendLine("COMPLETED TOPICS (never reopen or offer again): " + string.Join(" | ", completed) + ".");
            }
            sb.AppendLine($"The next player decision is round {upcomingRound}. Soft ending starts at {softEnd}; hard ending is {hardEnd}.");
            sb.AppendLine("Generate a complete directed dialogue graph in one response, not one isolated NPC line. The graph should normally contain 10-14 nodes, may branch and reconverge, must be acyclic, and every node must be reachable from start_node_id.");
            sb.AppendLine("visible_dialogue must equal the dialogue of the start node. Return empty top-level topics and choices arrays; all playable choices belong inside script_nodes.");
            sb.AppendLine("Every script node, including every terminal reaction node, must contain a non-empty id, speaker_alias, dialogue, and choices array. Terminal nodes use an empty choices array; never omit their dialogue.");
            sb.AppendLine("Every nonterminal node should contain 2 or 3 distinct player intents. Do not pad a node with generic continue/listen/change-topic/another-angle/close-conversation choices.");
            sb.AppendLine("For every choice, text is a short UI intent label; spoken_text is the complete, polished line the player character will actually say. spoken_text must be natural, in character, context-specific, and meaningfully more expressive than text. Never copy text verbatim into spoken_text.");
            sb.AppendLine("Never emit top-level actions. All gameplay changes must be nested under a visible choice outcome.");
            sb.AppendLine("Graph schema:");
            sb.AppendLine("{\"visible_dialogue\":\"same as start node dialogue\",\"topics\":[],\"choices\":[],\"start_node_id\":\"node_1\",\"script_nodes\":[{\"id\":\"node_1\",\"speaker_alias\":\"npc_1\",\"dialogue\":\"NPC speech for this node\",\"choices\":[{\"id\":\"choice_1\",\"text\":\"short intent label\",\"spoken_text\":\"complete polished line spoken by the player character\",\"check\":{\"approach\":\"persuasion|reason|intimidation|care\",\"target_alias\":\"npc_1\"},\"success\":{\"effects\":[],\"end_mode\":\"none|normal|cooldown\",\"next_node_id\":\"node_2\"},\"failure\":{\"effects\":[],\"end_mode\":\"none|normal|cooldown\",\"next_node_id\":\"node_3\"}}]}]}");
            sb.AppendLine("Every outcome must provide next_node_id so the NPC's reaction is scripted. For a continuing outcome, end_mode is none and the target node has choices. For an ending outcome, end_mode is normal or cooldown and the target is a terminal reaction node with an empty choices array. A checked choice may send success and failure to different nodes; branches may later converge on the same node.");
            sb.AppendLine("Write several turns of actual plot progression into the graph. Each node's NPC dialogue must react specifically to the player line that can reach it; if multiple branches converge, write a reaction that fits every incoming edge.");
            sb.AppendLine("Omit check for pure narrative choices. If check exists, choose an approach that matches the wording. Never provide DC, probability, roll, bonus, or effect labels; runtime computes them.");
            sb.AppendLine("Approach describes the player's method, independently of the resulting effect: persuasion=charm/negotiation/request, reason=logic/evidence/explanation, intimidation=threat/force, care=medical help/empathy/comfort.");
            sb.AppendLine("Effects per success: maximum 3 and maximum one high-impact effect. Failure: maximum one mild negative effect, only a registered negative TryGainMemory or TryAffectSocialGoodwill amount -1..-5.");

            List<string> aliases = (targetAliases ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (aliases.Count > 0)
            {
                sb.AppendLine("Allowed targets: " + string.Join(", ", aliases) + ". Use only these aliases.");
            }

            List<string> locked = (lockedHighImpactActions ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (locked.Count > 0)
            {
                sb.AppendLine("Locked for this session; do not offer again: " + string.Join(", ", locked) + ".");
            }

            if (upcomingRound < softEnd)
            {
                sb.AppendLine($"No path may end before decision round {softEnd}. Early graph layers must use end_mode none and link to another node.");
            }
            else if (upcomingRound < Math.Min(hardEnd, 20))
            {
                sb.AppendLine("At the current graph layer, at least one route should conclude the active topic and at least one should continue.");
            }
            else if (upcomingRound < hardEnd)
            {
                sb.AppendLine("At late graph layers, use three choices where practical: two ending routes and one continuing route.");
            }
            else
            {
                sb.AppendLine("This is the hard final decision for the active topic. Every success and failure outcome must use normal or cooldown end_mode. Do not allow continuation.");
            }

            return sb.ToString();
        }

        private static void ResolveRoundBounds(out int softEnd, out int hardEnd)
        {
            RimChatSettings settings = RimChatMod.Settings;
            softEnd = Mathf.Clamp(settings?.RpgChoiceSoftEndRound ?? 10, 3, 30);
            hardEnd = Mathf.Clamp(settings?.RpgChoiceHardEndRound ?? 30, softEnd + 1, 60);
        }

        private static void SetEnding(RpgDialogueChoice choice, bool shouldEnd)
        {
            if (choice == null) return;
            choice.Success = choice.Success ?? new RpgDialogueChoiceOutcome();
            choice.Failure = choice.Failure ?? new RpgDialogueChoiceOutcome();
            if (shouldEnd)
            {
                if (choice.Success.EndMode == RpgDialogueEndMode.None) choice.Success.EndMode = RpgDialogueEndMode.Normal;
                if (choice.Failure.EndMode == RpgDialogueEndMode.None) choice.Failure.EndMode = RpgDialogueEndMode.Normal;
            }
            else
            {
                choice.Success.EndMode = RpgDialogueEndMode.None;
                choice.Failure.EndMode = RpgDialogueEndMode.None;
            }
        }
    }
}
