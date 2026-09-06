using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Persistence;

namespace RimChat.Rpg
{
    public enum RpgDialogueEndMode
    {
        None = 0,
        Normal = 1,
        Cooldown = 2
    }

    public enum RpgSkillCheckApproach
    {
        None = 0,
        Persuasion = 1,
        Reason = 2,
        Intimidation = 3,
        Care = 4
    }

    public sealed class RpgDialogueTopic
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsError { get; set; }
        public string ErrorReason { get; set; } = string.Empty;
        public bool IsEnabled => !IsCompleted && !IsError;
    }

    public sealed class RpgDialogueChoice
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string SpokenText { get; set; }
        public RpgDialogueChoiceCheck Check { get; set; }
        public RpgDialogueChoiceOutcome Success { get; set; } = new RpgDialogueChoiceOutcome();
        public RpgDialogueChoiceOutcome Failure { get; set; } = new RpgDialogueChoiceOutcome();
        public RpgSkillCheckPreview Preview { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string DisabledReason { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public string ErrorSourceId { get; set; }
        public bool IsReturnToTopics { get; set; }

        public bool RequiresCheck => Check != null && Check.Approach != RpgSkillCheckApproach.None;
    }

    public sealed class RpgDialogueChoiceCheck
    {
        public RpgSkillCheckApproach Approach { get; set; }
        public string TargetAlias { get; set; }
    }

    public sealed class RpgDialogueChoiceOutcome
    {
        public List<LLMRpgApiResponse.ApiAction> Effects { get; set; } = new List<LLMRpgApiResponse.ApiAction>();
        public RpgDialogueEndMode EndMode { get; set; }
        public string NextNodeId { get; set; }
    }

    public sealed class RpgDialogueScriptNode
    {
        public string Id { get; set; }
        public string SpeakerAlias { get; set; }
        public string Dialogue { get; set; }
        public List<RpgDialogueChoice> Choices { get; set; } = new List<RpgDialogueChoice>();
    }

    public sealed class RpgDialogueGraph
    {
        public string StartNodeId { get; set; }
        public List<RpgDialogueScriptNode> Nodes { get; set; } = new List<RpgDialogueScriptNode>();
        public bool IsValid { get; set; }
        public string ErrorReason { get; set; } = string.Empty;

        public RpgDialogueScriptNode FindNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) return null;
            return Nodes?.FirstOrDefault(node => string.Equals(node?.Id, nodeId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class RpgSkillCheckPreview
    {
        public RpgSkillCheckApproach Approach { get; set; }
        public string SkillLabel { get; set; }
        public int SkillLevel { get; set; }
        public int SkillBonus { get; set; }
        public int StatusBonus { get; set; }
        public int ContextBonus { get; set; }
        public int Difficulty { get; set; }
        public float SuccessChance { get; set; }
        public string Breakdown { get; set; }

        public int TotalBonus => SkillBonus + StatusBonus + ContextBonus;
    }

    public sealed class RpgSkillCheckResult
    {
        public int Roll { get; set; }
        public int Total { get; set; }
        public bool Success { get; set; }
        public bool NaturalOne { get; set; }
        public bool NaturalTwenty { get; set; }
        public RpgSkillCheckPreview Preview { get; set; }
    }

    // These DTO fields are populated by the reflection JSON codec.
#pragma warning disable CS0649
    [Serializable]
    internal sealed class RpgDialogueChoicesWireRoot
    {
        public List<RpgDialogueChoiceWire> choices = new List<RpgDialogueChoiceWire>();
    }

    [Serializable]
    internal sealed class RpgDialogueTopicsWireRoot
    {
        public List<RpgDialogueTopicWire> topics = new List<RpgDialogueTopicWire>();
    }

    [Serializable]
    internal sealed class RpgDialogueTopicWire
    {
        public string id;
        public string text;
    }

    [Serializable]
    internal sealed class RpgDialogueChoiceWire
    {
        public string id;
        public string text;
        public string spoken_text;
        public string spokenText;
        public RpgDialogueChoiceCheckWire check;
        public RpgDialogueChoiceOutcomeWire success;
        public RpgDialogueChoiceOutcomeWire failure;
    }

    [Serializable]
    internal sealed class RpgDialogueChoiceCheckWire
    {
        public string approach;
        public string target_alias;
        public string targetAlias;
    }

    [Serializable]
    internal sealed class RpgDialogueChoiceOutcomeWire
    {
        public List<RpgDialogueChoiceEffectWire> effects = new List<RpgDialogueChoiceEffectWire>();
        public string end_mode;
        public string endMode;
        public string next_node_id;
        public string nextNodeId;
    }

    [Serializable]
    internal sealed class RpgDialogueScriptNodesWireRoot
    {
        public List<RpgDialogueScriptNodeWire> nodes = new List<RpgDialogueScriptNodeWire>();
    }

    [Serializable]
    internal sealed class RpgDialogueScriptNodeWire
    {
        public string id;
        public string speaker_alias;
        public string speakerAlias;
        public string dialogue;
        public string visible_dialogue;
        public List<RpgDialogueChoiceWire> choices = new List<RpgDialogueChoiceWire>();
    }

    [Serializable]
    internal sealed class RpgDialogueChoiceEffectWire
    {
        public string action;
        public string name;
        public string defName;
        public string def_name;
        public string reason;
        public int amount;
        public float value;
        public string target_alias;
        public string targetAlias;
        public RpgDialogueChoiceEffectParametersWire parameters;

        public LLMRpgApiResponse.ApiAction ToApiAction()
        {
            RpgDialogueChoiceEffectParametersWire p = parameters;
            return new LLMRpgApiResponse.ApiAction
            {
                action = FirstNonEmpty(action, name),
                defName = FirstNonEmpty(p?.defName, p?.def_name, defName, def_name),
                reason = FirstNonEmpty(p?.reason, reason),
                amount = p != null && p.amount != 0 ? p.amount : amount,
                value = p != null && Math.Abs(p.value) > 0.0001f ? p.value : value,
                targetAlias = FirstNonEmpty(p?.target_alias, p?.targetAlias, target_alias, targetAlias)
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }
    }

    [Serializable]
    internal sealed class RpgDialogueChoiceEffectParametersWire
    {
        public string defName;
        public string def_name;
        public string reason;
        public int amount;
        public float value;
        public string target_alias;
        public string targetAlias;
    }
#pragma warning restore CS0649

    public static class RpgDialogueTopicParser
    {
        public static List<RpgDialogueTopic> Parse(string topicsJson)
        {
            var result = new List<RpgDialogueTopic>();
            if (string.IsNullOrWhiteSpace(topicsJson)) return result;

            try
            {
                string wrapped = "{\"topics\":" + topicsJson.Trim() + "}";
                if (!ReflectionJsonFieldDeserializer.TryDeserialize(wrapped, out RpgDialogueTopicsWireRoot root) || root?.topics == null)
                    return result;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RpgDialogueTopicWire wire in root.topics)
                {
                    if (wire == null || string.IsNullOrWhiteSpace(wire.text)) continue;
                    string id = string.IsNullOrWhiteSpace(wire.id) ? Guid.NewGuid().ToString("N") : wire.id.Trim();
                    if (!seen.Add(id)) continue;
                    result.Add(new RpgDialogueTopic { Id = id, Text = wire.text.Trim() });
                }
            }
            catch (Exception)
            {
                return new List<RpgDialogueTopic>();
            }

            return result;
        }
    }

    public static class RpgDialogueChoiceParser
    {
        public static List<RpgDialogueChoice> Parse(string choicesJson)
        {
            var result = new List<RpgDialogueChoice>();
            if (string.IsNullOrWhiteSpace(choicesJson))
            {
                return result;
            }

            try
            {
                string wrapped = "{\"choices\":" + choicesJson.Trim() + "}";
                if (!ReflectionJsonFieldDeserializer.TryDeserialize(wrapped, out RpgDialogueChoicesWireRoot root) || root?.choices == null)
                {
                    return result;
                }

                foreach (RpgDialogueChoiceWire wire in root.choices)
                {
                    RpgDialogueChoice choice = Convert(wire);
                    if (choice != null)
                    {
                        result.Add(choice);
                    }
                }
            }
            catch (Exception)
            {
                return new List<RpgDialogueChoice>();
            }

            return result;
        }

        internal static RpgDialogueChoice Convert(RpgDialogueChoiceWire wire)
        {
            if (wire == null || string.IsNullOrWhiteSpace(wire.text))
            {
                return BuildParseError(wire?.id, "missing_choice_text");
            }

            RpgDialogueChoiceCheck check = ConvertCheck(wire.check);
            if (wire.check != null && check == null) return BuildParseError(wire.id, "approach_not_allowed");

            return new RpgDialogueChoice
            {
                Id = string.IsNullOrWhiteSpace(wire.id) ? Guid.NewGuid().ToString("N") : wire.id.Trim(),
                Text = wire.text.Trim(),
                SpokenText = FirstNonEmpty(wire.spoken_text, wire.spokenText),
                Check = check,
                Success = ConvertOutcome(wire.success),
                Failure = ConvertOutcome(wire.failure)
            };
        }

        private static RpgDialogueChoice BuildParseError(string choiceId, string reason)
        {
            string id = string.IsNullOrWhiteSpace(choiceId) ? "?" : choiceId.Trim();
            return new RpgDialogueChoice
            {
                Id = "parse_error_" + Guid.NewGuid().ToString("N"),
                Text = "Choice error (" + id + "): " + reason,
                IsEnabled = false,
                IsError = true,
                ErrorSourceId = id,
                DisabledReason = reason
            };
        }

        private static RpgDialogueChoiceCheck ConvertCheck(RpgDialogueChoiceCheckWire wire)
        {
            if (wire == null || !TryParseApproach(wire.approach, out RpgSkillCheckApproach approach))
            {
                return null;
            }

            return new RpgDialogueChoiceCheck
            {
                Approach = approach,
                TargetAlias = FirstNonEmpty(wire.target_alias, wire.targetAlias)
            };
        }

        private static RpgDialogueChoiceOutcome ConvertOutcome(RpgDialogueChoiceOutcomeWire wire)
        {
            var outcome = new RpgDialogueChoiceOutcome();
            if (wire == null)
            {
                return outcome;
            }

            outcome.EndMode = ParseEndMode(FirstNonEmpty(wire.end_mode, wire.endMode));
            outcome.NextNodeId = FirstNonEmpty(wire.next_node_id, wire.nextNodeId);
            if (wire.effects != null)
            {
                outcome.Effects = wire.effects
                    .Where(effect => effect != null)
                    .Select(effect => effect.ToApiAction())
                    .Where(effect => !string.IsNullOrWhiteSpace(effect.action))
                    .ToList();
            }

            return outcome;
        }

        private static bool TryParseApproach(string raw, out RpgSkillCheckApproach approach)
        {
            string normalized = (raw ?? string.Empty).Trim().Replace("-", "_").ToLowerInvariant();
            switch (normalized)
            {
                case "persuasion":
                case "persuade":
                case "social":
                    approach = RpgSkillCheckApproach.Persuasion;
                    return true;
                case "reason":
                case "logic":
                case "intellectual":
                    approach = RpgSkillCheckApproach.Reason;
                    return true;
                case "intimidation":
                case "intimidate":
                case "threaten":
                    approach = RpgSkillCheckApproach.Intimidation;
                    return true;
                case "care":
                case "medical":
                case "empathy":
                    approach = RpgSkillCheckApproach.Care;
                    return true;
                default:
                    approach = RpgSkillCheckApproach.None;
                    return false;
            }
        }

        private static RpgDialogueEndMode ParseEndMode(string raw)
        {
            switch ((raw ?? string.Empty).Trim().Replace("-", "_").ToLowerInvariant())
            {
                case "normal":
                case "end":
                case "exit":
                    return RpgDialogueEndMode.Normal;
                case "cooldown":
                case "exit_cooldown":
                    return RpgDialogueEndMode.Cooldown;
                default:
                    return RpgDialogueEndMode.None;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }
    }

    public static class RpgDialogueGraphParser
    {
        public static RpgDialogueGraph Parse(string startNodeId, string nodesJson)
        {
            var graph = new RpgDialogueGraph { StartNodeId = (startNodeId ?? string.Empty).Trim() };
            if (string.IsNullOrWhiteSpace(nodesJson)) return Fail(graph, "missing_script_nodes");
            try
            {
                string wrapped = "{\"nodes\":" + nodesJson.Trim() + "}";
                if (!ReflectionJsonFieldDeserializer.TryDeserialize(wrapped, out RpgDialogueScriptNodesWireRoot root) || root?.nodes == null)
                    return Fail(graph, "invalid_script_nodes");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<RpgDialogueScriptNodeWire> wires = root.nodes.Take(40).ToList();
                for (int nodeIndex = 0; nodeIndex < wires.Count; nodeIndex++)
                {
                    RpgDialogueScriptNodeWire wire = wires[nodeIndex];
                    if (wire == null) return Fail(graph, "null_script_node:index_" + nodeIndex);
                    if (string.IsNullOrWhiteSpace(wire.id))
                        return Fail(graph, "script_node_missing_id:index_" + nodeIndex);
                    string id = wire.id.Trim();
                    if (string.IsNullOrWhiteSpace(FirstNonEmpty(wire.dialogue, wire.visible_dialogue)))
                        return Fail(graph, "script_node_missing_dialogue:" + id);
                    if (!seen.Add(id)) return Fail(graph, "duplicate_script_node:" + id);
                    graph.Nodes.Add(new RpgDialogueScriptNode
                    {
                        Id = id,
                        SpeakerAlias = FirstNonEmpty(wire.speaker_alias, wire.speakerAlias),
                        Dialogue = FirstNonEmpty(wire.dialogue, wire.visible_dialogue),
                        Choices = (wire.choices ?? new List<RpgDialogueChoiceWire>())
                            .Take(3)
                            .Select(RpgDialogueChoiceParser.Convert)
                            .Where(choice => choice != null)
                            .ToList()
                    });
                }

                if (graph.Nodes.Count == 0) return Fail(graph, "empty_script_graph");
                if (string.IsNullOrWhiteSpace(graph.StartNodeId)) graph.StartNodeId = graph.Nodes[0].Id;
                if (graph.FindNode(graph.StartNodeId) == null) return Fail(graph, "missing_start_node:" + graph.StartNodeId);

                foreach (RpgDialogueScriptNode node in graph.Nodes)
                {
                    foreach (RpgDialogueChoice choice in node.Choices)
                    {
                        if (choice?.IsError == true) continue;
                        if (!ValidateEdge(graph, choice?.Success, out string error) ||
                            !ValidateEdge(graph, choice?.Failure, out error))
                        {
                            MarkChoiceError(choice, error);
                        }
                    }
                }

                RpgDialogueScriptNode startNode = graph.FindNode(graph.StartNodeId);
                if (startNode.Choices.Count == 0) return Fail(graph, "start_node_has_no_choices:" + startNode.Id);
                RpgDialogueScriptNode brokenNode = graph.Nodes.FirstOrDefault(node =>
                    node.Choices.Count > 0 && node.Choices.All(choice => choice?.IsError == true));
                if (brokenNode != null) return Fail(graph, "node_has_no_valid_choices:" + brokenNode.Id);

                var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (HasCycle(graph, graph.StartNodeId, visiting, visited)) return Fail(graph, "cyclic_script_graph");

                graph.IsValid = true;
                return graph;
            }
            catch (Exception ex)
            {
                return Fail(graph, "script_parse_exception:" + ex.GetType().Name);
            }
        }

        private static bool ValidateEdge(RpgDialogueGraph graph, RpgDialogueChoiceOutcome outcome, out string error)
        {
            error = null;
            if (outcome == null) return true;
            if (string.IsNullOrWhiteSpace(outcome.NextNodeId))
            {
                error = "outcome_without_next_node";
                return false;
            }
            RpgDialogueScriptNode next = graph.FindNode(outcome.NextNodeId);
            if (next == null)
            {
                error = "missing_next_node:" + outcome.NextNodeId;
                return false;
            }
            if (outcome.EndMode != RpgDialogueEndMode.None && next.Choices.Count > 0)
            {
                error = "ending_outcome_targets_nonterminal_node:" + outcome.NextNodeId;
                return false;
            }
            if (outcome.EndMode == RpgDialogueEndMode.None && next.Choices.Count == 0)
            {
                error = "continuing_outcome_targets_terminal_node:" + outcome.NextNodeId;
                return false;
            }
            return true;
        }

        private static bool HasCycle(
            RpgDialogueGraph graph,
            string nodeId,
            HashSet<string> visiting,
            HashSet<string> visited)
        {
            if (visited.Contains(nodeId)) return false;
            if (!visiting.Add(nodeId)) return true;
            RpgDialogueScriptNode node = graph.FindNode(nodeId);
            foreach (string nextId in (node?.Choices ?? new List<RpgDialogueChoice>())
                .Where(choice => choice?.IsError != true)
                .SelectMany(choice => new[] { choice?.Success?.NextNodeId, choice?.Failure?.NextNodeId })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (HasCycle(graph, nextId, visiting, visited)) return true;
            }
            visiting.Remove(nodeId);
            visited.Add(nodeId);
            return false;
        }

        private static void MarkChoiceError(RpgDialogueChoice choice, string reason)
        {
            if (choice == null) return;
            string sourceId = choice.Id ?? "?";
            choice.IsEnabled = false;
            choice.IsError = true;
            choice.ErrorSourceId = sourceId;
            choice.DisabledReason = reason ?? "invalid_script_edge";
            choice.Text = "Choice error (" + sourceId + "): " + choice.DisabledReason;
        }

        private static RpgDialogueGraph Fail(RpgDialogueGraph graph, string reason)
        {
            graph.IsValid = false;
            graph.ErrorReason = reason ?? "invalid_script_graph";
            return graph;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        }
    }
}
