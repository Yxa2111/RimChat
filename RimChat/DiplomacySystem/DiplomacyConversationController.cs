using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Dialogue;
using RimChat.Memory;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.DiplomacySystem
{
    /// <summary>/// Dependencies: AIChatServiceAsync, GameComponent_DiplomacyManager, FactionDialogueSession.
 /// Responsibility: own diplomacy request lifecycle, context validation, and cancellation.
 ///</summary>
    public class DiplomacyConversationController
    {
        private const int RequestDebounceTicks = 120;
        private const float RequestDebounceSeconds = 2f;
        private const int MaxNativeToolRounds = 16;

        public bool TrySendNativeToolDialogueRequest(
            FactionDialogueSession session,
            Faction faction,
            List<ChatMessageData> messages,
            IReadOnlyList<NativeToolDefinition> tools,
            DialogueRuntimeContext runtimeContext,
            string ownerWindowId,
            Func<IReadOnlyList<NativeToolCall>, IReadOnlyList<NativeToolResult>> executeTools,
            Action<string> onSuccess,
            Action<string> onError,
            Action<float> onProgress,
            Action<string> onDropped)
        {
            if (!CanStartRequest(session, faction, messages, runtimeContext))
            {
                return false;
            }

            CancelSupersededPendingRequest(session);
            session.isWaitingForResponse = true;
            session.aiRequestProgress = 0f;
            session.aiError = null;

            DialogueRuntimeContext requestContext = runtimeContext.WithCurrentRuntimeMarkers();
            DialogueRequestLease lease = new DialogueRequestLease(
                requestContext.DialogueSessionId,
                ownerWindowId,
                requestContext.ContextVersion);
            var state = new NativeToolLoopState
            {
                Session = session,
                Faction = faction,
                Messages = CloneAgentMessages(messages),
                Tools = tools == null
                    ? new List<NativeToolDefinition>()
                    : new List<NativeToolDefinition>(tools),
                RuntimeContext = requestContext,
                Lease = lease,
                ExecuteTools = executeTools,
                OnSuccess = onSuccess,
                OnError = onError,
                OnProgress = onProgress,
                OnDropped = onDropped
            };

            session.pendingRequestLease = lease;
            session.lastDiplomacyRequestQueuedTick = GetCurrentTick();
            session.lastDiplomacyRequestQueuedRealtime = Time.realtimeSinceStartup;
            if (!QueueNativeToolRound(state))
            {
                session.pendingRequestLease = null;
                lease.Dispose();
                session.isWaitingForResponse = false;
                session.aiError = "Failed to queue AI request";
                return false;
            }

            return true;
        }

        public bool IsRequestDebounced(FactionDialogueSession session)
        {
            if (session == null)
            {
                return false;
            }

            return IsWithinDebounceWindow(session);
        }

        public void CancelPendingRequest(FactionDialogueSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.pendingRequestId))
            {
                return;
            }

            string requestId = session.pendingRequestId;
            AIChatServiceAsync.Instance.CancelRequest(
                requestId,
                "dialogue_window_closed",
                "Request cancelled by dialogue close");
            session.pendingRequestId = null;
            session.pendingRequestLease?.Dispose();
            session.pendingRequestLease = null;
            session.isWaitingForResponse = false;
            session.aiRequestProgress = 0f;
        }

        public void CloseLease(FactionDialogueSession session)
        {
            if (session == null)
            {
                return;
            }

            session.pendingRequestLease?.MarkClosing();
            CancelPendingRequest(session);
        }

        private static bool CanStartRequest(
            FactionDialogueSession session,
            Faction faction,
            List<ChatMessageData> messages,
            DialogueRuntimeContext runtimeContext)
        {
            if (session == null || faction == null || faction.defeated)
            {
                return false;
            }

            if (messages == null || messages.Count == 0)
            {
                return false;
            }

            DialogueRuntimeContext currentSnapshot = runtimeContext?.WithCurrentRuntimeMarkers();
            if (runtimeContext == null ||
                !DialogueContextResolver.TryResolveLiveContext(currentSnapshot, out DialogueLiveContext liveContext, out _) ||
                !DialogueContextValidator.ValidateRequestSend(currentSnapshot, liveContext, out _))
            {
                return false;
            }

            return !IsWithinDebounceWindow(session);
        }

        private static bool QueueNativeToolRound(NativeToolLoopState state)
        {
            if (state == null || state.Session == null || state.Lease == null)
            {
                return false;
            }

            string requestId = null;
            requestId = AIChatServiceAsync.Instance.SendChatRequestAsync(
                state.Messages,
                onSuccess: null,
                onError: error => HandleNativeToolError(state, requestId, error),
                onProgress: progress => HandleNativeToolProgress(state, requestId, progress),
                usageChannel: DialogueUsageChannel.Diplomacy,
                debugSource: AIRequestDebugSource.DiplomacyDialogue,
                nativeTools: state.Tools,
                onNativeSuccess: turn => HandleNativeToolTurn(state, requestId, turn));

            if (string.IsNullOrWhiteSpace(requestId))
            {
                return false;
            }

            state.Lease.BindRequestId(requestId);
            state.Session.pendingRequestId = requestId;
            state.Session.pendingRequestLease = state.Lease;
            state.Session.aiRequestProgress = 0f;
            return true;
        }

        private static void HandleNativeToolTurn(
            NativeToolLoopState state,
            string requestId,
            NativeChatCompletionTurn turn)
        {
            if (!IsNativeToolCallbackValid(state, requestId, out string droppedReason))
            {
                state?.OnDropped?.Invoke(droppedReason);
                return;
            }

            if (turn == null || !turn.IsValid)
            {
                FinishNativeToolError(state, turn?.ErrorMessage ?? "Invalid native tool response.");
                return;
            }

            if (!turn.HasToolCalls)
            {
                string finalContent = turn.Content?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(finalContent))
                {
                    FinishNativeToolError(state, "The model returned neither dialogue nor tool calls.");
                    return;
                }

                FinishNativeToolSuccess(state, finalContent);
                return;
            }

            if (state.ToolRounds >= MaxNativeToolRounds)
            {
                FinishNativeToolError(state, $"Tool loop exceeded {MaxNativeToolRounds} rounds.");
                return;
            }

            state.Messages.Add(new ChatMessageData
            {
                role = "assistant",
                content = string.IsNullOrWhiteSpace(turn.Content) ? null : turn.Content,
                tool_calls = CloneToolCalls(turn.ToolCalls)
            });

            List<NativeToolResult> orderedResults = ExecuteToolBatch(state, turn.ToolCalls);
            for (int i = 0; i < turn.ToolCalls.Count; i++)
            {
                NativeToolCall call = turn.ToolCalls[i];
                NativeToolResult result = orderedResults[i];
                state.Messages.Add(new ChatMessageData
                {
                    role = "tool",
                    tool_call_id = call.id,
                    content = result.ToJson()
                });
            }

            state.ToolRounds++;
            state.Tools = NativeToolCatalog.Build(state.Faction, state.Session);
            if (!QueueNativeToolRound(state))
            {
                FinishNativeToolError(state, "Failed to queue the next tool round.");
            }
        }

        private static List<NativeToolResult> ExecuteToolBatch(
            NativeToolLoopState state,
            IReadOnlyList<NativeToolCall> calls)
        {
            var ordered = new NativeToolResult[calls.Count];
            var pending = new List<NativeToolCall>();
            var duplicateIds = new HashSet<string>(calls
                .Where(call => call != null && !string.IsNullOrWhiteSpace(call.id))
                .GroupBy(call => call.id, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key), StringComparer.Ordinal);
            for (int i = 0; i < calls.Count; i++)
            {
                NativeToolCall call = calls[i];
                if (call != null && duplicateIds.Contains(call.id))
                {
                    ordered[i] = BuildToolFailure(
                        call,
                        "duplicate_tool_call_id",
                        $"Tool call id '{call.id}' occurs more than once in the same assistant turn.");
                }
                else if (state.ToolResultsByCallId.TryGetValue(call.id, out NativeToolResult cached))
                {
                    ordered[i] = cached;
                }
                else
                {
                    pending.Add(call);
                }
            }

            if (pending.Count > 0)
            {
                IReadOnlyList<NativeToolResult> executed;
                try
                {
                    executed = state.ExecuteTools?.Invoke(pending);
                }
                catch (Exception ex)
                {
                    executed = pending.Select(call => BuildToolFailure(
                        call,
                        "tool_execution_exception",
                        ex.Message)).ToList();
                }

                var returned = (executed ?? new List<NativeToolResult>())
                    .Where(result => result != null && !string.IsNullOrWhiteSpace(result.ToolCallId))
                    .GroupBy(result => result.ToolCallId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                foreach (NativeToolCall call in pending)
                {
                    if (!returned.TryGetValue(call.id, out NativeToolResult result))
                    {
                        result = BuildToolFailure(call, "missing_tool_result", "The tool did not return a result.");
                    }
                    result.ToolCallId = call.id;
                    result.ToolName = call.function?.name ?? result.ToolName;
                    state.ToolResultsByCallId[call.id] = result;
                }
            }

            for (int i = 0; i < calls.Count; i++)
            {
                if (ordered[i] == null)
                {
                    ordered[i] = state.ToolResultsByCallId[calls[i].id];
                }
            }
            return ordered.ToList();
        }

        private static NativeToolResult BuildToolFailure(NativeToolCall call, string code, string message)
        {
            return new NativeToolResult
            {
                ToolCallId = call?.id ?? string.Empty,
                ToolName = call?.function?.name ?? string.Empty,
                Ok = false,
                Code = code ?? "tool_error",
                Message = message ?? "Tool execution failed."
            };
        }

        private static void HandleNativeToolError(NativeToolLoopState state, string requestId, string error)
        {
            if (!IsNativeToolCallbackValid(state, requestId, out string droppedReason))
            {
                state?.OnDropped?.Invoke(droppedReason);
                return;
            }
            FinishNativeToolError(state, error);
        }

        private static void HandleNativeToolProgress(NativeToolLoopState state, string requestId, float progress)
        {
            if (!IsNativeToolCallbackValid(state, requestId, out _))
            {
                return;
            }
            state.Session.aiRequestProgress = progress;
            state.OnProgress?.Invoke(progress);
        }

        private static bool IsNativeToolCallbackValid(
            NativeToolLoopState state,
            string requestId,
            out string reason)
        {
            reason = string.Empty;
            if (state == null || !string.Equals(state.Lease?.RequestId, requestId, StringComparison.Ordinal))
            {
                reason = "native_tool_request_mismatch";
                return false;
            }
            return IsRequestContextStillValid(
                state.Session,
                state.Faction,
                state.Lease,
                state.RuntimeContext,
                out reason);
        }

        private static void FinishNativeToolSuccess(NativeToolLoopState state, string content)
        {
            ClearNativeToolRequestState(state, true, null);
            state.OnSuccess?.Invoke(content);
        }

        private static void FinishNativeToolError(NativeToolLoopState state, string error)
        {
            ClearNativeToolRequestState(state, false, error);
            state.OnError?.Invoke(error);
        }

        private static void ClearNativeToolRequestState(NativeToolLoopState state, bool success, string error)
        {
            if (state?.Session == null)
            {
                return;
            }
            state.Session.pendingRequestId = null;
            state.Session.pendingRequestLease?.Dispose();
            state.Session.pendingRequestLease = null;
            state.Session.isWaitingForResponse = false;
            state.Session.aiRequestProgress = success ? 1f : 0f;
            state.Session.aiError = success ? null : error;
        }

        private static List<ChatMessageData> CloneAgentMessages(IEnumerable<ChatMessageData> messages)
        {
            return (messages ?? Enumerable.Empty<ChatMessageData>())
                .Where(message => message != null)
                .Select(message => new ChatMessageData
                {
                    role = message.role,
                    content = message.content,
                    name = message.name,
                    tool_call_id = message.tool_call_id,
                    tool_calls = CloneToolCalls(message.tool_calls)
                })
                .ToList();
        }

        private static List<NativeToolCall> CloneToolCalls(IEnumerable<NativeToolCall> calls)
        {
            return calls?
                .Where(call => call != null)
                .Select(call => new NativeToolCall
                {
                    id = call.id,
                    type = call.type,
                    function = call.function == null
                        ? null
                        : new NativeToolFunctionCall
                        {
                            name = call.function.name,
                            arguments = call.function.arguments
                        }
                })
                .ToList();
        }

        private sealed class NativeToolLoopState
        {
            public FactionDialogueSession Session;
            public Faction Faction;
            public List<ChatMessageData> Messages;
            public List<NativeToolDefinition> Tools;
            public DialogueRuntimeContext RuntimeContext;
            public DialogueRequestLease Lease;
            public Func<IReadOnlyList<NativeToolCall>, IReadOnlyList<NativeToolResult>> ExecuteTools;
            public Action<string> OnSuccess;
            public Action<string> OnError;
            public Action<float> OnProgress;
            public Action<string> OnDropped;
            public int ToolRounds;
            public readonly Dictionary<string, NativeToolResult> ToolResultsByCallId =
                new Dictionary<string, NativeToolResult>(StringComparer.Ordinal);
        }

        private static int GetCurrentTick()
        {
            return Find.TickManager?.TicksGame ?? 0;
        }

        private static bool IsWithinDebounceWindow(FactionDialogueSession session)
        {
            if (session == null)
            {
                return false;
            }

            bool gamePaused = Find.TickManager?.Paused ?? false;
            if (gamePaused)
            {
                return IsWithinRealtimeDebounce(session);
            }

            int lastQueuedTick = session.lastDiplomacyRequestQueuedTick;
            if (lastQueuedTick != int.MinValue)
            {
                int tickDelta = GetCurrentTick() - lastQueuedTick;
                if (tickDelta >= 0 && tickDelta < RequestDebounceTicks)
                {
                    return true;
                }
            }

            return IsWithinRealtimeDebounce(session);
        }

        private static bool IsWithinRealtimeDebounce(FactionDialogueSession session)
        {
            float lastQueuedRealtime = session.lastDiplomacyRequestQueuedRealtime;
            if (lastQueuedRealtime < 0f)
            {
                return false;
            }

            float realtimeDelta = Time.realtimeSinceStartup - lastQueuedRealtime;
            return realtimeDelta >= 0f && realtimeDelta < RequestDebounceSeconds;
        }

        private static void CancelSupersededPendingRequest(FactionDialogueSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.pendingRequestId))
            {
                return;
            }

            string supersededRequestId = session.pendingRequestId;
            AIChatServiceAsync.Instance.CancelRequest(
                supersededRequestId,
                "request_superseded",
                "Request superseded by a newer dialogue turn");
            session.pendingRequestId = null;
            session.pendingRequestLease?.Dispose();
            session.pendingRequestLease = null;
            session.isWaitingForResponse = false;
            session.aiRequestProgress = 0f;
        }

        private static bool IsRequestContextStillValid(
            FactionDialogueSession session,
            Faction faction,
            DialogueRequestLease lease,
            DialogueRuntimeContext runtimeContext,
            out string reason)
        {
            reason = string.Empty;
            if (session == null || faction == null || faction.defeated || lease == null)
            {
                reason = "request_context_null";
                return false;
            }

            string requestId = lease.RequestId;
            if (string.IsNullOrEmpty(requestId))
            {
                reason = "lease_request_id_empty";
                return false;
            }

            if (!string.Equals(session.pendingRequestId, requestId, StringComparison.Ordinal))
            {
                reason = "pending_request_mismatch";
                return false;
            }

            if (session.pendingRequestLease == null || !ReferenceEquals(session.pendingRequestLease, lease))
            {
                reason = "request_lease_mismatch";
                return false;
            }

            if (!lease.IsValidFor(requestId, runtimeContext?.DialogueSessionId ?? string.Empty, runtimeContext?.ContextVersion ?? -1))
            {
                reason = "request_lease_invalid";
                return false;
            }

            DialogueRuntimeContext resolveContext = runtimeContext?.WithCurrentRuntimeMarkers();
            if (!DialogueContextResolver.TryResolveLiveContext(resolveContext, out DialogueLiveContext liveContext, out reason))
            {
                return false;
            }

            if (!DialogueContextValidator.ValidateCallbackApply(runtimeContext, liveContext, runtimeContext?.DialogueSessionId, out reason))
            {
                return false;
            }

            FactionDialogueSession liveSession = GameComponent_DiplomacyManager.Instance?.GetSession(faction);
            if (!ReferenceEquals(liveSession, session))
            {
                reason = "session_reference_changed";
                return false;
            }

            return true;
        }
    }
}
