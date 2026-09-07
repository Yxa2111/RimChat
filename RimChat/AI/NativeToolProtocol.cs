using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimChat.Persistence;

namespace RimChat.AI
{
    [Serializable]
    public sealed class NativeToolFunctionCall
    {
        public string name;
        public string arguments;
    }

    [Serializable]
    public sealed class NativeToolCall
    {
        public string id;
        public string type = "function";
        public NativeToolFunctionCall function;
    }

    public sealed class NativeToolDefinition
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string ParametersJson { get; set; }
    }

    public sealed class NativeChatCompletionTurn
    {
        public bool IsValid { get; set; }
        public string Content { get; set; } = string.Empty;
        public List<NativeToolCall> ToolCalls { get; set; } = new List<NativeToolCall>();
        public string FinishReason { get; set; } = string.Empty;
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;

        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;
    }

    public sealed class NativeToolResult
    {
        public string ToolCallId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public object Data { get; set; }

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"ok\":").Append(Ok ? "true" : "false");
            sb.Append(",\"code\":\"").Append(Escape(Code)).Append('\"');
            sb.Append(",\"message\":\"").Append(Escape(Message)).Append('\"');
            if (Data != null)
            {
                sb.Append(",\"data\":\"").Append(Escape(Data.ToString())).Append('\"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return RimChat.Util.JsonEscapeHelper.EscapeString(value ?? string.Empty);
        }
    }

    internal static class NativeToolArgumentParser
    {
        public static bool TryParseObject(string json, out Dictionary<string, object> values, out string error)
        {
            values = null;
            error = string.Empty;
            string payload = string.IsNullOrWhiteSpace(json) ? "{}" : json.Trim();
            if (!ReflectionJsonFieldDeserializer.TryParseUntyped(payload, out object parsed))
            {
                error = "Tool arguments are not valid JSON.";
                return false;
            }

            if (!(parsed is Dictionary<string, object> dictionary))
            {
                error = "Tool arguments must be a JSON object.";
                return false;
            }

            values = dictionary;
            return true;
        }
    }

    internal static class NativeChatCompletionParser
    {
        public static NativeChatCompletionTurn Parse(string json)
        {
            if (!ReflectionJsonFieldDeserializer.TryDeserialize(json, out NativeChatCompletionResponseWire response) || response == null)
            {
                return Failure("invalid_provider_json", "The model provider returned invalid JSON.");
            }

            if (response.error != null && !string.IsNullOrWhiteSpace(response.error.message))
            {
                return Failure(
                    string.IsNullOrWhiteSpace(response.error.code) ? "provider_error" : response.error.code,
                    response.error.message);
            }

            NativeChatChoiceWire choice = response.choices?.OrderBy(item => item?.index ?? int.MaxValue).FirstOrDefault();
            NativeChatMessageWire message = choice?.message;
            if (message == null)
            {
                return Failure("missing_assistant_message", "The model provider response did not contain choices[0].message.");
            }

            var toolCalls = message.tool_calls ?? new List<NativeToolCall>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (NativeToolCall call in toolCalls)
            {
                if (call == null || string.IsNullOrWhiteSpace(call.id))
                {
                    return Failure("invalid_tool_call_id", "A tool call did not contain an id.");
                }
                if (!ids.Add(call.id))
                {
                    return Failure("duplicate_tool_call_id", $"Duplicate tool_call_id: {call.id}");
                }
                if (!string.Equals(call.type ?? "function", "function", StringComparison.Ordinal) ||
                    call.function == null ||
                    string.IsNullOrWhiteSpace(call.function.name))
                {
                    return Failure("invalid_tool_call", $"Tool call {call.id} is not a valid function call.");
                }
            }

            string content = message.content ?? string.Empty;
            if (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(content))
            {
                return Failure("empty_assistant_turn", "The model returned neither text nor tool calls.");
            }

            return new NativeChatCompletionTurn
            {
                IsValid = true,
                Content = content.Trim(),
                ToolCalls = toolCalls,
                FinishReason = choice?.finish_reason ?? string.Empty
            };
        }

        private static NativeChatCompletionTurn Failure(string code, string message)
        {
            return new NativeChatCompletionTurn
            {
                IsValid = false,
                ErrorCode = code ?? "native_tool_protocol_error",
                ErrorMessage = message ?? "Native tool protocol error."
            };
        }

        [Serializable]
        public sealed class NativeChatCompletionResponseWire
        {
            public List<NativeChatChoiceWire> choices;
            public NativeProviderErrorWire error;
        }

        [Serializable]
        public sealed class NativeChatChoiceWire
        {
            public int index;
            public string finish_reason;
            public NativeChatMessageWire message;
        }

        [Serializable]
        public sealed class NativeChatMessageWire
        {
            public string role;
            public string content;
            public List<NativeToolCall> tool_calls;
        }

        [Serializable]
        public sealed class NativeProviderErrorWire
        {
            public string code;
            public string message;
        }
    }
}
