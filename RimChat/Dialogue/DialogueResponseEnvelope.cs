using System.Collections.Generic;
using System.Text;
using RimChat.AI;
using RimChat.Rpg;

namespace RimChat.Dialogue
{
    public enum DialogueResponseProtocolKind
    {
        Unknown = 0,
        StructuredJson = 1,
        LegacyText = 2
    }

    public enum DialogueResponseExpectation
    {
        Default = 0,
        RpgTopics = 1,
        RpgScriptGraph = 2,
        RpgFinalReaction = 3
    }

    /// <summary>
    /// Stage-A parsed response envelope. UI/action mutation happens in stage B only.
    /// </summary>
    public sealed class DialogueResponseEnvelope
    {
        public string RawResponse { get; set; }
        public string VisibleDialogue { get; set; }
        public string ActionsJson { get; set; }
        public List<LLMRpgApiResponse.ApiAction> Actions { get; set; } = new List<LLMRpgApiResponse.ApiAction>();
        public string ChoicesJson { get; set; }
        public List<RpgDialogueChoice> Choices { get; set; } = new List<RpgDialogueChoice>();
        public string TopicsJson { get; set; }
        public List<RpgDialogueTopic> Topics { get; set; } = new List<RpgDialogueTopic>();
        public string StartNodeId { get; set; }
        public string ScriptNodesJson { get; set; }
        public RpgDialogueGraph DialogueGraph { get; set; }
        public bool IsStaleDropped { get; set; }
        public string DropReason { get; set; }
        public bool IsValid { get; set; }
        public string FailureReason { get; set; }
        public DialogueResponseProtocolKind ProtocolKind { get; set; }

        public string DialogueText
        {
            get => VisibleDialogue ?? string.Empty;
            set => VisibleDialogue = value ?? string.Empty;
        }

        public string ToLegacyText()
        {
            return ModelOutputSanitizer.ComposeVisibleAndTrailingActions(
                VisibleDialogue ?? string.Empty,
                ActionsJson ?? string.Empty);
        }

        public string ToStructuredResponseText()
        {
            string visibleDialogue = EscapeJsonString(VisibleDialogue ?? string.Empty);
            string actionsJson = string.IsNullOrWhiteSpace(ActionsJson)
                ? string.Empty
                : (ActionsJson ?? string.Empty).Trim();
            string choicesJson = string.IsNullOrWhiteSpace(ChoicesJson)
                ? string.Empty
                : (ChoicesJson ?? string.Empty).Trim();
            string topicsJson = string.IsNullOrWhiteSpace(TopicsJson)
                ? string.Empty
                : (TopicsJson ?? string.Empty).Trim();
            string scriptNodesJson = string.IsNullOrWhiteSpace(ScriptNodesJson)
                ? string.Empty
                : (ScriptNodesJson ?? string.Empty).Trim();

            var builder = new StringBuilder();
            builder.Append("{\"visible_dialogue\":\"");
            builder.Append(visibleDialogue);
            builder.Append("\"");
            if (!string.IsNullOrWhiteSpace(actionsJson))
            {
                builder.Append(",\"actions\":");
                builder.Append(actionsJson);
            }
            if (!string.IsNullOrWhiteSpace(choicesJson))
            {
                builder.Append(",\"choices\":");
                builder.Append(choicesJson);
            }
            if (!string.IsNullOrWhiteSpace(topicsJson))
            {
                builder.Append(",\"topics\":");
                builder.Append(topicsJson);
            }
            if (!string.IsNullOrWhiteSpace(scriptNodesJson))
            {
                builder.Append(",\"start_node_id\":\"");
                builder.Append(EscapeJsonString(StartNodeId ?? string.Empty));
                builder.Append("\",\"script_nodes\":");
                builder.Append(scriptNodesJson);
            }

            builder.Append("}");
            return builder.ToString();
        }

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                switch (current)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(current))
                        {
                            builder.Append("\\u");
                            builder.Append(((int)current).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(current);
                        }

                        break;
                }
            }

            return builder.ToString();
        }
    }
}
