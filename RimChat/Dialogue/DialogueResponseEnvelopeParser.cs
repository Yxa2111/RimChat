using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimChat.AI;
using RimChat.Rpg;
using Verse;

namespace RimChat.Dialogue
{
    /// <summary>
    /// Dependencies: ModelOutputSanitizer and RPG action parser.
    /// Responsibility: parse model output into a single dialogue envelope with a structured-first contract.
    /// </summary>
    public static class DialogueResponseEnvelopeParser
    {
        private static readonly HashSet<string> AllowedStructuredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "visible_dialogue",
            "actions",
            "meta",
            "debug",
            "dialogue_text",
            "choices",
            "topics",
            "start_node_id",
            "script_nodes"
        };

        public static DialogueResponseEnvelope Parse(
            string response,
            DialogueUsageChannel usageChannel,
            DialogueResponseExpectation expectation = DialogueResponseExpectation.Default)
        {
            string raw = response ?? string.Empty;
            string sanitized = ModelOutputSanitizer.StripReasoningTags(raw).Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return BuildFailure(raw, "empty_response");
            }

            if (TryParseStructuredEnvelope(raw, sanitized, usageChannel, out DialogueResponseEnvelope structuredEnvelope))
            {
                return ValidateExpectation(structuredEnvelope, raw, expectation);
            }

            // Structured channels prefer JSON. If structured parsing failed, try legacy extraction
            // as a fallback before reporting failure. The retry mechanism in the caller still
            // fires when both structured AND legacy paths fail.
            if (usageChannel == DialogueUsageChannel.Diplomacy || usageChannel == DialogueUsageChannel.Rpg)
            {
                if (expectation != DialogueResponseExpectation.Default)
                {
                    return BuildFailure(raw, MissingExpectationReason(expectation));
                }

                if (TryParseLegacyEnvelope(raw, sanitized, usageChannel, out DialogueResponseEnvelope legacyEnvelope))
                {
                    return legacyEnvelope;
                }

                return BuildFailure(raw, "no_structured_envelope");
            }

            if (TryParseLegacyEnvelope(raw, sanitized, usageChannel, out DialogueResponseEnvelope fallbackEnvelope))
            {
                return fallbackEnvelope;
            }

            return BuildFailure(raw, "unsupported_dialogue_contract");
        }

        private static DialogueResponseEnvelope ValidateExpectation(
            DialogueResponseEnvelope envelope,
            string raw,
            DialogueResponseExpectation expectation)
        {
            if (envelope == null || !envelope.IsValid || expectation == DialogueResponseExpectation.Default)
            {
                return envelope;
            }

            switch (expectation)
            {
                case DialogueResponseExpectation.RpgTopics:
                    int topicCount = envelope.Topics?.Count ?? 0;
                    return topicCount >= 2 && topicCount <= 3
                        ? envelope
                        : BuildFailure(raw, "invalid_topic_count:" + topicCount);
                case DialogueResponseExpectation.RpgScriptGraph:
                    if (envelope.DialogueGraph == null)
                    {
                        return BuildFailure(raw, "missing_script_graph");
                    }
                    return envelope.DialogueGraph.IsValid
                        ? envelope
                        : BuildFailure(raw, "invalid_script_graph:" + (envelope.DialogueGraph.ErrorReason ?? "unknown"));
                case DialogueResponseExpectation.RpgFinalReaction:
                    return envelope;
                default:
                    return envelope;
            }
        }

        private static string MissingExpectationReason(DialogueResponseExpectation expectation)
        {
            switch (expectation)
            {
                case DialogueResponseExpectation.RpgTopics:
                    return "missing_topics_envelope";
                case DialogueResponseExpectation.RpgScriptGraph:
                    return "missing_script_graph";
                case DialogueResponseExpectation.RpgFinalReaction:
                    return "missing_final_reaction_envelope";
                default:
                    return "no_structured_envelope";
            }
        }

        private static bool TryParseStructuredEnvelope(
            string raw,
            string sanitized,
            DialogueUsageChannel usageChannel,
            out DialogueResponseEnvelope envelope)
        {
            envelope = null;
            string payload = StripMarkdownCodeFence(sanitized);
            if (!LooksLikeSingleJsonObject(payload))
            {
                payload = ExtractJsonFromMixedContent(sanitized);
                if (string.IsNullOrWhiteSpace(payload) || !LooksLikeSingleJsonObject(payload))
                {
                    return false;
                }

                Log.Warning("[RimChat] Model output contained narrative text before/after JSON block. Extracted JSON via code-fence recovery.");
            }

            string visibleDialogue = ExtractFirstNonEmptyString(payload, "visible_dialogue", "dialogue_text");
            if (string.IsNullOrWhiteSpace(visibleDialogue))
            {
                envelope = BuildFailure(raw, "missing_visible_dialogue");
                return true;
            }

            List<string> topLevelKeys = ExtractTopLevelKeys(payload);
            if (topLevelKeys.Count == 0)
            {
                envelope = BuildFailure(raw, "invalid_structured_payload");
                return true;
            }

            string unexpectedKey = topLevelKeys.FirstOrDefault(key => !AllowedStructuredKeys.Contains(key));
            if (!string.IsNullOrWhiteSpace(unexpectedKey))
            {
                // visible_dialogue was already extracted successfully — demote to warning and continue parsing
                // rather than discarding the dialogue. The unexpected key is logged for prompt tuning.
                Log.Warning($"[RimChat] Structured envelope has unexpected top-level key: {unexpectedKey}. Continuing with extracted visible_dialogue.");
            }

            bool hasActionsKey = topLevelKeys.Any(key => string.Equals(key, "actions", StringComparison.OrdinalIgnoreCase));
            string actionsJson = hasActionsKey
                ? ExtractTopLevelArray(payload, "actions")
                : string.Empty;
            if (hasActionsKey && string.IsNullOrWhiteSpace(actionsJson))
            {
                envelope = BuildFailure(raw, "invalid_actions_array");
                return true;
            }

            bool hasChoicesKey = topLevelKeys.Any(key => string.Equals(key, "choices", StringComparison.OrdinalIgnoreCase));
            string choicesJson = hasChoicesKey
                ? ExtractTopLevelArray(payload, "choices")
                : string.Empty;
            if (hasChoicesKey && string.IsNullOrWhiteSpace(choicesJson))
            {
                envelope = BuildFailure(raw, "invalid_choices_array");
                return true;
            }

            bool hasTopicsKey = topLevelKeys.Any(key => string.Equals(key, "topics", StringComparison.OrdinalIgnoreCase));
            string topicsJson = hasTopicsKey
                ? ExtractTopLevelArray(payload, "topics")
                : string.Empty;
            if (hasTopicsKey && string.IsNullOrWhiteSpace(topicsJson))
            {
                envelope = BuildFailure(raw, "invalid_topics_array");
                return true;
            }

            bool hasScriptNodesKey = topLevelKeys.Any(key => string.Equals(key, "script_nodes", StringComparison.OrdinalIgnoreCase));
            string scriptNodesJson = hasScriptNodesKey
                ? ExtractTopLevelArray(payload, "script_nodes")
                : string.Empty;
            if (hasScriptNodesKey && string.IsNullOrWhiteSpace(scriptNodesJson))
            {
                envelope = BuildFailure(raw, "invalid_script_nodes_array");
                return true;
            }
            string startNodeId = ExtractFirstNonEmptyString(payload, "start_node_id");

            envelope = BuildSuccess(
                raw,
                visibleDialogue,
                actionsJson,
                choicesJson,
                topicsJson,
                startNodeId,
                scriptNodesJson,
                DialogueResponseProtocolKind.StructuredJson,
                usageChannel);
            return true;
        }

        private static bool TryParseLegacyEnvelope(
            string raw,
            string sanitized,
            DialogueUsageChannel usageChannel,
            out DialogueResponseEnvelope envelope)
        {
            envelope = null;
            ModelOutputSanitizer.SplitVisibleAndTrailingActions(
                sanitized,
                out string visibleDialogue,
                out string actionsJson);

            if (string.IsNullOrWhiteSpace(visibleDialogue))
            {
                return false;
            }

            envelope = BuildSuccess(
                raw,
                visibleDialogue,
                actionsJson,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                DialogueResponseProtocolKind.LegacyText,
                usageChannel);
            return true;
        }

        private static DialogueResponseEnvelope BuildSuccess(
            string raw,
            string visibleDialogue,
            string actionsJson,
            string choicesJson,
            string topicsJson,
            string startNodeId,
            string scriptNodesJson,
            DialogueResponseProtocolKind protocolKind,
            DialogueUsageChannel usageChannel)
        {
            return new DialogueResponseEnvelope
            {
                RawResponse = raw ?? string.Empty,
                VisibleDialogue = (visibleDialogue ?? string.Empty).Trim(),
                ActionsJson = (actionsJson ?? string.Empty).Trim(),
                Actions = usageChannel == DialogueUsageChannel.Rpg
                    ? LLMRpgApiResponse.ParseActionsFromJson(actionsJson)
                    : new List<LLMRpgApiResponse.ApiAction>(),
                ChoicesJson = (choicesJson ?? string.Empty).Trim(),
                Choices = usageChannel == DialogueUsageChannel.Rpg
                    ? RpgDialogueChoiceParser.Parse(choicesJson)
                    : new List<RpgDialogueChoice>(),
                TopicsJson = (topicsJson ?? string.Empty).Trim(),
                Topics = usageChannel == DialogueUsageChannel.Rpg
                    ? RpgDialogueTopicParser.Parse(topicsJson)
                    : new List<RpgDialogueTopic>(),
                StartNodeId = (startNodeId ?? string.Empty).Trim(),
                ScriptNodesJson = (scriptNodesJson ?? string.Empty).Trim(),
                DialogueGraph = usageChannel == DialogueUsageChannel.Rpg && !string.IsNullOrWhiteSpace(scriptNodesJson)
                    ? RpgDialogueGraphParser.Parse(startNodeId, scriptNodesJson)
                    : null,
                IsValid = true,
                FailureReason = string.Empty,
                ProtocolKind = protocolKind
            };
        }

        private static DialogueResponseEnvelope BuildFailure(string raw, string reason)
        {
            return new DialogueResponseEnvelope
            {
                RawResponse = raw ?? string.Empty,
                VisibleDialogue = string.Empty,
                ActionsJson = string.Empty,
                Actions = new List<LLMRpgApiResponse.ApiAction>(),
                ChoicesJson = string.Empty,
                Choices = new List<RpgDialogueChoice>(),
                TopicsJson = string.Empty,
                Topics = new List<RpgDialogueTopic>(),
                StartNodeId = string.Empty,
                ScriptNodesJson = string.Empty,
                DialogueGraph = null,
                IsValid = false,
                FailureReason = reason ?? "invalid_dialogue_contract",
                ProtocolKind = DialogueResponseProtocolKind.Unknown
            };
        }

        private static string ExtractJsonFromMixedContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // Look for ```json ... ``` blocks and extract the JSON object with visible_dialogue.
            int fenceStart = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (fenceStart < 0)
            {
                fenceStart = text.IndexOf("```", StringComparison.Ordinal);
            }

            if (fenceStart < 0)
            {
                // No code fence — try to find the first top-level JSON object containing visible_dialogue.
                return ExtractJsonObjectContainingKey(text, "visible_dialogue");
            }

            int contentStart = text.IndexOf('\n', fenceStart);
            if (contentStart < 0 || contentStart >= text.Length - 1)
            {
                return string.Empty;
            }

            contentStart++;
            int fenceEnd = text.IndexOf("```", contentStart);
            string block = fenceEnd > contentStart
                ? text.Substring(contentStart, fenceEnd - contentStart).Trim()
                : text.Substring(contentStart).Trim();

            if (LooksLikeSingleJsonObject(block))
            {
                return block;
            }

            return ExtractJsonObjectContainingKey(block, "visible_dialogue");
        }

        private static string ExtractJsonObjectContainingKey(string text, string requiredKey)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(requiredKey))
            {
                return string.Empty;
            }

            string needle = "\"" + requiredKey + "\"";
            int keyIndex = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                return string.Empty;
            }

            // Walk backwards to find the opening { of the containing object.
            int depth = 0;
            int objectStart = -1;
            for (int i = keyIndex; i >= 0; i--)
            {
                char c = text[i];
                if (c == '}')
                {
                    depth++;
                }
                else if (c == '{')
                {
                    if (depth == 0)
                    {
                        objectStart = i;
                        break;
                    }

                    depth--;
                }
            }

            if (objectStart < 0)
            {
                return string.Empty;
            }

            int objectEnd = FindMatchingBracket(text, objectStart, '{', '}');
            if (objectEnd <= objectStart)
            {
                return string.Empty;
            }

            return text.Substring(objectStart, objectEnd - objectStart + 1).Trim();
        }

        private static string StripMarkdownCodeFence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string trimmed = text.Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal) ||
                !trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                return trimmed;
            }

            int firstNewLine = trimmed.IndexOf('\n');
            if (firstNewLine < 0 || firstNewLine >= trimmed.Length - 3)
            {
                return trimmed;
            }

            string body = trimmed.Substring(firstNewLine + 1);
            if (body.EndsWith("```", StringComparison.Ordinal))
            {
                body = body.Substring(0, body.Length - 3);
            }

            return body.Trim();
        }

        private static bool LooksLikeSingleJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) ||
                !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            return FindMatchingBracket(trimmed, 0, '{', '}') == trimmed.Length - 1;
        }

        private static string ExtractFirstNonEmptyString(string json, params string[] keys)
        {
            if (keys == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                string value = ExtractTopLevelString(json, keys[i]);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static List<string> ExtractTopLevelKeys(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char current = json[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    if (depth == 1)
                    {
                        int stringEnd = FindStringEnd(json, i + 1);
                        if (stringEnd > i)
                        {
                            string key = UnescapeJson(json.Substring(i + 1, stringEnd - i - 1));
                            int cursor = SkipWhitespace(json, stringEnd + 1);
                            if (cursor < json.Length && json[cursor] == ':')
                            {
                                result.Add(key);
                            }
                        }
                    }

                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
            }

            return result;
        }

        private static string ExtractTopLevelString(string json, string key)
        {
            int valueStart = FindTopLevelKeyValueStart(json, key);
            if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '"')
            {
                return string.Empty;
            }

            int stringEnd = FindStringEnd(json, valueStart + 1);
            if (stringEnd <= valueStart)
            {
                return string.Empty;
            }

            return UnescapeJson(json.Substring(valueStart + 1, stringEnd - valueStart - 1));
        }

        private static string ExtractTopLevelArray(string json, string key)
        {
            int valueStart = FindTopLevelKeyValueStart(json, key);
            if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '[')
            {
                return string.Empty;
            }

            int arrayEnd = FindMatchingBracket(json, valueStart, '[', ']');
            if (arrayEnd <= valueStart)
            {
                return string.Empty;
            }

            return json.Substring(valueStart, arrayEnd - valueStart + 1).Trim();
        }

        private static int FindTopLevelKeyValueStart(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
            {
                return -1;
            }

            string needle = "\"" + key + "\"";
            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = 0; i <= json.Length - needle.Length; i++)
            {
                char current = json[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    if (depth == 1 &&
                        string.Compare(json, i, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        int cursor = SkipWhitespace(json, i + needle.Length);
                        if (cursor >= json.Length || json[cursor] != ':')
                        {
                            continue;
                        }

                        cursor = SkipWhitespace(json, cursor + 1);
                        return cursor < json.Length ? cursor : -1;
                    }

                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
            }

            return -1;
        }

        private static int FindMatchingBracket(string text, int start, char open, char close)
        {
            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = start; i < text.Length; i++)
            {
                char current = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == open)
                {
                    depth++;
                    continue;
                }

                if (current != close)
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindStringEnd(string text, int start)
        {
            bool escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char current = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    return i;
                }
            }

            return -1;
        }

        private static int SkipWhitespace(string text, int index)
        {
            int cursor = Math.Max(0, index);
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            return cursor;
        }

        private static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\\"", "\"")
                        .Replace("\\\\", "\\")
                        .Replace("\\n", "\n")
                        .Replace("\\r", "\r")
                        .Replace("\\t", "\t");
        }
    }
}
