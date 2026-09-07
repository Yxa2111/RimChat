using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimWorld;
using Verse;

namespace RimChat.UI
{
    /// <summary>
    /// Executes OpenAI-compatible diplomacy tool calls and returns one result for every tool_call_id.
    /// </summary>
    public partial class Dialog_DiplomacyDialogue
    {
        private IReadOnlyList<NativeToolResult> ExecuteNativeToolCalls(
            IReadOnlyList<NativeToolCall> calls,
            FactionDialogueSession currentSession,
            Faction currentFaction)
        {
            var orderedResults = new NativeToolResult[calls?.Count ?? 0];
            if (calls == null || calls.Count == 0)
            {
                return orderedResults;
            }

            HashSet<string> availableTools = new HashSet<string>(
                NativeToolCatalog.Build(currentFaction, currentSession).Select(tool => tool.Name),
                StringComparer.Ordinal);
            bool airdropConflict = calls.Count(call => IsNativeTool(call, AIActionNames.AcceptItemAirdrop)) > 1 ||
                                   (calls.Any(call => IsNativeTool(call, AIActionNames.AcceptItemAirdrop)) &&
                                    calls.Any(call => IsNativeTool(call, AIActionNames.RequestItemAirdrop)));
            var executable = new List<NativeToolExecution>();

            for (int i = 0; i < calls.Count; i++)
            {
                NativeToolCall call = calls[i];
                string toolName = call?.function?.name?.Trim() ?? string.Empty;
                if (call == null || string.IsNullOrWhiteSpace(call.id) || string.IsNullOrWhiteSpace(toolName))
                {
                    orderedResults[i] = ToolFailure(call, "invalid_tool_call", "Tool call id and function name are required.");
                    continue;
                }

                if (airdropConflict &&
                    (toolName == AIActionNames.AcceptItemAirdrop || toolName == AIActionNames.RequestItemAirdrop))
                {
                    orderedResults[i] = ToolFailure(
                        call,
                        "airdrop_action_conflict",
                        "accept_item_airdrop and request_item_airdrop cannot be called in the same assistant turn.");
                    continue;
                }

                if (!NativeToolCatalog.IsKnown(toolName))
                {
                    orderedResults[i] = ToolFailure(call, "unknown_tool", $"Unknown tool: {toolName}");
                    continue;
                }
                if (!NativeToolArgumentParser.TryParseObject(call.function.arguments, out Dictionary<string, object> parsed, out string parseError))
                {
                    orderedResults[i] = ToolFailure(call, "invalid_json_arguments", parseError);
                    continue;
                }
                if (!NativeToolCatalog.TryValidateAndNormalize(
                        toolName,
                        parsed,
                        out Dictionary<string, object> parameters,
                        out string validationCode,
                        out string validationMessage))
                {
                    orderedResults[i] = ToolFailure(call, validationCode, validationMessage);
                    continue;
                }

                var action = new AIAction
                {
                    ActionType = toolName,
                    Parameters = parameters,
                    Reason = $"native_tool_call:{call.id}"
                };

                if (toolName == AIActionNames.AcceptItemAirdrop)
                {
                    string requestId = parameters["request_id"]?.ToString()?.Trim() ?? string.Empty;
                    if (TryBuildIdempotentAirdropAcceptResult(call, currentSession, requestId, out NativeToolResult idempotent))
                    {
                        orderedResults[i] = idempotent;
                        continue;
                    }
                }

                if (toolName == AIActionNames.AcceptItemAirdrop)
                {
                    if (!TryBuildAcceptedAirdropAction(action, currentSession, out AIAction fulfillment, out string acceptanceFailure))
                    {
                        orderedResults[i] = ToolFailure(call, "airdrop_request_invalid", acceptanceFailure);
                        continue;
                    }
                    action = fulfillment;
                }

                if (!availableTools.Contains(toolName))
                {
                    orderedResults[i] = ToolFailure(call, "tool_unavailable", $"Tool '{toolName}' is not available in the current faction session state.");
                    continue;
                }

                if (!TryInjectPendingAirdropTradeCardMetadata(action, currentSession, out string metadataFailure))
                {
                    orderedResults[i] = ToolFailure(call, "airdrop_request_invalid", metadataFailure);
                    continue;
                }

                executable.Add(new NativeToolExecution
                {
                    ResultIndex = i,
                    Call = call,
                    Action = action
                });
            }

            if (executable.Count > 0)
            {
                List<AIAction> actions = executable.Select(item => item.Action).ToList();
                List<ActionExecutionOutcome> outcomes = ExecuteAIActions(actions, currentSession, currentFaction);
                AppendSuccessfulActionSystemMessages(outcomes, currentSession, currentFaction);

                foreach (NativeToolExecution item in executable)
                {
                    ActionExecutionOutcome outcome = outcomes.FirstOrDefault(candidate =>
                        candidate != null && ReferenceEquals(candidate.Action, item.Action));
                    orderedResults[item.ResultIndex] = outcome == null
                        ? ToolFailure(item.Call, "tool_not_executed", "The tool was not executed because another action in the batch failed.")
                        : ToolResult(item.Call, outcome);
                }
            }

            for (int i = 0; i < orderedResults.Length; i++)
            {
                if (orderedResults[i] == null)
                {
                    orderedResults[i] = ToolFailure(calls[i], "missing_tool_result", "The tool did not return a result.");
                }
                if (!orderedResults[i].Ok)
                {
                    currentSession?.AddMessage(
                        "System",
                        "RimChat_NativeToolFailureSystem".Translate(
                            orderedResults[i].ToolName,
                            orderedResults[i].Code,
                            orderedResults[i].Message).ToString(),
                        false,
                        DialogueMessageType.System);
                }
            }

            SaveFactionMemory(currentSession, currentFaction);
            return orderedResults;
        }

        private static bool IsNativeTool(NativeToolCall call, string name)
        {
            return string.Equals(call?.function?.name, name, StringComparison.Ordinal);
        }

        private static bool TryBuildIdempotentAirdropAcceptResult(
            NativeToolCall call,
            FactionDialogueSession currentSession,
            string requestId,
            out NativeToolResult result)
        {
            result = null;
            if (currentSession == null ||
                !currentSession.TryGetAirdropTradeCardStatus(requestId, out AirdropTradeCardStatus status))
            {
                return false;
            }

            string statusCode;
            switch (status)
            {
                case AirdropTradeCardStatus.Preparing:
                    statusCode = "preparing";
                    break;
                case AirdropTradeCardStatus.AwaitingPlayerConfirm:
                    statusCode = "awaiting_player_confirmation";
                    break;
                case AirdropTradeCardStatus.Executing:
                    statusCode = "executing";
                    break;
                case AirdropTradeCardStatus.Completed:
                    statusCode = "completed";
                    break;
                default:
                    return false;
            }

            result = new NativeToolResult
            {
                ToolCallId = call.id,
                ToolName = call.function.name,
                Ok = true,
                Code = statusCode,
                Message = $"Airdrop request {requestId} is {statusCode}."
            };
            return true;
        }

        private static NativeToolResult ToolResult(NativeToolCall call, ActionExecutionOutcome outcome)
        {
            string code = outcome.IsSuccess ? ResolveSuccessfulToolCode(outcome) : "action_failed";
            return new NativeToolResult
            {
                ToolCallId = call?.id ?? string.Empty,
                ToolName = call?.function?.name ?? string.Empty,
                Ok = outcome.IsSuccess,
                Code = code,
                Message = outcome.Message ?? string.Empty,
                Data = BuildToolResultData(outcome.Data)
            };
        }

        private static string ResolveSuccessfulToolCode(ActionExecutionOutcome outcome)
        {
            if (outcome?.Data is ItemAirdropAsyncQueuedData)
            {
                return "preparing";
            }
            if (outcome?.Data is ItemAirdropPreparedTradeData)
            {
                return "awaiting_player_confirmation";
            }
            return "ok";
        }

        private static object BuildToolResultData(object data)
        {
            if (data is ItemAirdropPreparedTradeData trade)
            {
                return $"status=awaiting_player_confirmation,delivery_total={trade.BudgetSilver},payment_total={trade.PaymentTotalSilver},shipping={trade.ShippingCostSilver}";
            }
            if (data is ItemAirdropAsyncQueuedData)
            {
                return "status=preparing";
            }
            return data == null ? null : data.ToString();
        }

        private static NativeToolResult ToolFailure(NativeToolCall call, string code, string message)
        {
            return new NativeToolResult
            {
                ToolCallId = call?.id ?? string.Empty,
                ToolName = call?.function?.name ?? string.Empty,
                Ok = false,
                Code = string.IsNullOrWhiteSpace(code) ? "tool_error" : code,
                Message = string.IsNullOrWhiteSpace(message) ? "Tool execution failed." : message
            };
        }

        private static Dictionary<string, object> CloneParameters(Dictionary<string, object> source)
        {
            return source == null
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                : new Dictionary<string, object>(source, StringComparer.Ordinal);
        }

        private void AddNativeAIResponseToSession(
            string dialogueText,
            FactionDialogueSession currentSession,
            Faction currentFaction)
        {
            string visibleText = dialogueText?.Trim() ?? string.Empty;
            if (currentSession == null || currentFaction == null || string.IsNullOrWhiteSpace(visibleText))
            {
                return;
            }

            Pawn speakerPawn = ResolveFactionSpeakerPawn(currentSession, currentFaction);
            string senderName = ResolveFactionSenderName(currentFaction, speakerPawn);
            currentSession.lastAssistantVisibleText = visibleText;
            currentSession.AddMessage(senderName, visibleText, false, DialogueMessageType.Normal, speakerPawn);
            SaveFactionMemory(currentSession, currentFaction);
        }

        private sealed class NativeToolExecution
        {
            public int ResultIndex;
            public NativeToolCall Call;
            public AIAction Action;
        }
    }
}
