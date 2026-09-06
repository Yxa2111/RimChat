using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Dialogue;
using RimChat.Memory;
using RimChat.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.UI
{
    public partial class Dialog_RPGPawnGroupChat
    {
        // Serial queue: each pawn's response is cached; next request fires immediately after current arrives
        private readonly Dictionary<int, string> _cachedResponses = new Dictionary<int, string>();
        private int _queuedRequestIndex = -1; // next pawn index to request

        // ── Start a round: queue first request ──

        private void StartRound()
        {
            _cachedResponses.Clear();
            currentSpeakerIndex = 0;
            isPlayerTurn = false;
            ResetRoundFlags();

            SkipInvalidPawnsForward();
            if (currentSpeakerIndex >= participants.Count)
            {
                TransitionToPlayerTurn();
                return;
            }

            // Fire first request immediately
            isSendingRequest = true;
            _queuedRequestIndex = currentSpeakerIndex;
            SendSerialRequest(currentSpeakerIndex);
        }

        private void SendSerialRequest(int pawnIndex)
        {
            if (pawnIndex < 0 || pawnIndex >= participants.Count) return;

            var speaker = participants[pawnIndex];
            bool includeChoices = IsLastValidGroupSpeaker(pawnIndex);
            List<ChatMessageData> requestMessages;
            try
            {
                requestMessages = BuildGroupRequestMessages(speaker, isFirstTurn: turnRecords.Count == 0, includeChoices: includeChoices);
            }
            catch (Exception ex)
            {
                Log.Error($"[RimChat] Request build failed for {speaker.DisplayName}: {ex.Message}");
                _cachedResponses[pawnIndex] = "RimChat_GroupConverse_Skipped".Translate(speaker.DisplayName);
                OnResponseReceived(pawnIndex);
                return;
            }

            conversationController.TrySend(
                runtimeContext.WithCurrentRuntimeMarkers(),
                windowInstanceId,
                requestMessages,
                ResolveGroupResponseExpectation(includeChoices),
                onReady: envelope =>
                {
                    if (isWindowClosing) return;
                    if (IsGroupChoiceModeEnabled && includeChoices)
                        PrepareAndCacheGroupChoices(envelope, speaker.Pawn);
                    string text = envelope?.DialogueText ?? "";
                    int js = text.LastIndexOf("{\"actions\"");
                    if (js >= 0) text = text.Substring(0, js).TrimEnd();
                    if (string.IsNullOrWhiteSpace(text)) text = "…";
                    _cachedResponses[pawnIndex] = text;
                    if (!IsGroupChoiceModeEnabled && envelope?.Actions != null && envelope.Actions.Count > 0)
                        ExecuteActionsForSpeaker(speaker, envelope.Actions);
                    OnResponseReceived(pawnIndex);
                },
                onError: error =>
                {
                    if (isWindowClosing) return;
                    if (IsGroupChoiceModeEnabled && includeChoices)
                        CacheGroupChoiceRequestError(error);
                    _cachedResponses[pawnIndex] = "RimChat_GroupConverse_Error".Translate(speaker.DisplayName, error);
                    OnResponseReceived(pawnIndex);
                },
                onDropped: reason =>
                {
                    if (isWindowClosing) return;
                    if (IsGroupChoiceModeEnabled && includeChoices)
                        CacheGroupChoiceRequestError(reason);
                    _cachedResponses[pawnIndex] = "RimChat_DialogueResponseDropped".Translate(reason ?? "unknown");
                    OnResponseReceived(pawnIndex);
                });
        }

        // Called when a serial response arrives. Cache it, then queue the next request.
        private void OnResponseReceived(int pawnIndex)
        {
            // Queue the NEXT pawn's request immediately (serial, don't wait for click)
            int nextIdx = pawnIndex + 1;
            while (nextIdx < participants.Count)
            {
                var c = participants[nextIdx];
                if (c.Pawn != null && !c.Pawn.Dead && !c.Pawn.Destroyed && c.Pawn.Spawned)
                    break;
                nextIdx++;
            }
            if (nextIdx < participants.Count && !_cachedResponses.ContainsKey(nextIdx))
            {
                _queuedRequestIndex = nextIdx;
                SendSerialRequest(nextIdx);
            }

            // Only auto-display if we're waiting for this speaker AND not showing player text
            if (isShowingPlayerText) return;
            if (isPlayerTurn) return;
            if (pawnIndex != currentSpeakerIndex) return;

            // This is the current speaker — display immediately
            isSendingRequest = false;
            string text = _cachedResponses[pawnIndex];
            currentDialogueText = text;
            displayedText = "";
            visibleChars = 0;
            isTyping = true;
            lastCharTime = Time.realtimeSinceStartup;
            pauseForClick = false;

            var sp = participants[pawnIndex];
            turnRecords.Add(new GroupTurnRecord { SpeakerPawnId = sp.PawnId, SpeakerName = sp.DisplayName, DialogueText = text, IsPlayer = false });
            dialogPages.Add(new DialoguePage { speakerName = sp.DisplayName, text = text });

            if (!string.IsNullOrWhiteSpace(text) && text != "…")
                RpgDialogueTraceTracker.RegisterTurn(initiator, sp.Pawn, false, text, dialogueSessionId);
        }

        // ── Constructor entry ──

        private void SendFirstSpeakerRequest()
        {
            if (participants.Count == 0) return;
            currentRound = 1;
            StartRound();
        }

        // ── Advance (click-to-continue) → next cached speaker ──

        private void AdvanceToNextSpeaker()
        {
            if (isPlayerTurn) return;

            if (localGroupGraphTransition)
            {
                localGroupGraphTransition = false;
                localGroupGraphSpeakerIndex = -1;
                TransitionToPlayerTurn();
                return;
            }

            int nextIdx = currentSpeakerIndex + 1;
            while (nextIdx < participants.Count)
            {
                var c = participants[nextIdx];
                if (c.Pawn != null && !c.Pawn.Dead && !c.Pawn.Destroyed && c.Pawn.Spawned)
                    break;
                nextIdx++;
            }

            if (nextIdx >= participants.Count)
            {
                TransitionToPlayerTurn();
                return;
            }

            currentSpeakerIndex = nextIdx;

            if (_cachedResponses.TryGetValue(nextIdx, out string text))
            {
                // Already cached — display immediately
                currentDialogueText = text;
                displayedText = "";
                visibleChars = 0;
                isTyping = true;
                lastCharTime = Time.realtimeSinceStartup;
                pauseForClick = false;

                var sp = participants[nextIdx];
                turnRecords.Add(new GroupTurnRecord { SpeakerPawnId = sp.PawnId, SpeakerName = sp.DisplayName, DialogueText = text, IsPlayer = false });
                dialogPages.Add(new DialoguePage { speakerName = sp.DisplayName, text = text });

                if (!string.IsNullOrWhiteSpace(text) && text != "…")
                    RpgDialogueTraceTracker.RegisterTurn(initiator, sp.Pawn, false, text, dialogueSessionId);
            }
            else
            {
                // Still waiting — show loading
                isSendingRequest = true;
                currentDialogueText = "";
                displayedText = "";
                visibleChars = 0;
                isTyping = false;
                pauseForClick = false;
            }
        }

        // ── Per-frame: check if pending response arrived for current speaker ──

        private void CheckPendingResponse()
        {
            if (isShowingPlayerText || isPlayerTurn) return;
            int idx = currentSpeakerIndex;
            if (idx < 0 || idx >= participants.Count) return;
            if (!_cachedResponses.TryGetValue(idx, out string text)) return;
            // Only act if we're still waiting (sending OR showing stale "…")
            if (!isSendingRequest && !string.IsNullOrEmpty(currentDialogueText) && currentDialogueText != "…") return;

            isSendingRequest = false;
            currentDialogueText = text;
            displayedText = "";
            visibleChars = 0;
            isTyping = true;
            lastCharTime = Time.realtimeSinceStartup;

            var sp = participants[idx];
            turnRecords.Add(new GroupTurnRecord { SpeakerPawnId = sp.PawnId, SpeakerName = sp.DisplayName, DialogueText = text, IsPlayer = false });
            dialogPages.Add(new DialoguePage { speakerName = sp.DisplayName, text = text });

            if (!string.IsNullOrWhiteSpace(text) && text != "…")
                RpgDialogueTraceTracker.RegisterTurn(initiator, sp.Pawn, false, text, dialogueSessionId);
        }

        // ── Player turn ──

        private void TransitionToPlayerTurn()
        {
            if (IsGroupChoiceModeEnabled && groupFinalReactionRound)
            {
                groupFinalReactionRound = false;
                if (completeGroupTopicAfterFinalReaction && groupTopics.Any(topic => topic.IsEnabled))
                {
                    completeGroupTopicAfterFinalReaction = false;
                    activeGroupTopic = null;
                    activeGroupDialogueGraph = null;
                    activeGroupScriptNodeId = null;
                    currentRound = 0;
                    isPlayerTurn = true;
                    currentSpeakerIndex = -1;
                    pauseForClick = false;
                    isSendingRequest = false;
                    selectedGroupTopicIndex = FindNextEnabledGroupTopic(0, 1);
                    return;
                }
                completeGroupTopicAfterFinalReaction = false;
                activeGroupDialogueGraph = null;
                activeGroupScriptNodeId = null;
                groupFinalReactionReceived = true;
                groupFinalReactionCloseAt = -1f;
                isPlayerTurn = false;
                currentSpeakerIndex = -1;
                pauseForClick = false;
                isSendingRequest = false;
                return;
            }

            isPlayerTurn = true;
            currentSpeakerIndex = -1;
            pauseForClick = false;
            currentDialogueText = "";
            displayedText = "";
            visibleChars = 0;
            isSendingRequest = false;
            if (IsGroupChoiceModeEnabled)
                AdoptCachedGroupChoices();
        }

        private void TrySendPlayerMessage()
        {
            if (!isPlayerTurn || string.IsNullOrWhiteSpace(userReplyText)) return;

            string textToSend = userReplyText.Trim();
            userReplyText = "";
            GUI.FocusControl(null);

            turnRecords.Add(new GroupTurnRecord { SpeakerPawnId = initiator.GetUniqueLoadID(), SpeakerName = initiator.LabelShort, DialogueText = textToSend, IsPlayer = true });
            dialogPages.Add(new DialoguePage { speakerName = initiator.LabelShort, text = textToSend });

            foreach (var p in participants)
                RpgDialogueTraceTracker.RegisterTurn(initiator, p.Pawn, true, textToSend, dialogueSessionId);

            currentDialogueText = textToSend;
            displayedText = "";
            visibleChars = 0;
            isTyping = true;
            isShowingPlayerText = true;
            isWaitingForPlayerDelay = false;
            isPlayerTurn = false;
            isViewingHistory = false;
            nextSpeakerRequested = false;
            if (IsGroupChoiceModeEnabled && sendingGroupTopicSelection)
            {
                sendingGroupTopicSelection = false;
                currentRound = 1;
            }
            else
            {
                currentRound++;
            }
            if (IsGroupChoiceModeEnabled)
                currentGroupChoices.Clear();
            ResetRoundFlags();

            // Start new round: serial queue from first pawn
            StartRound();
        }

        // ── Player text → first NPC (from serial queue) ──

        private void CheckPlayerTextTransition()
        {
            if (!isShowingPlayerText) return;
            if (isTyping) return;

            if (!isWaitingForPlayerDelay)
            {
                isWaitingForPlayerDelay = true;
                timePlayerTextFinished = Time.realtimeSinceStartup;
                return;
            }

            float elapsed = Time.realtimeSinceStartup - timePlayerTextFinished;
            // Wait for first pawn's response to be ready, or 3s timeout
            int firstSpeakerIndex = localGroupGraphTransition ? localGroupGraphSpeakerIndex : 0;
            bool firstReady = _cachedResponses.ContainsKey(firstSpeakerIndex);
            if (!firstReady && elapsed < 3.0f) return;
            if (elapsed < 1.0f) return;
            if (nextSpeakerRequested) return;

            nextSpeakerRequested = true;
            isShowingPlayerText = false;
            isWaitingForPlayerDelay = false;
            isSendingRequest = false;

            currentSpeakerIndex = firstSpeakerIndex;
            SkipInvalidPawnsForward();
            if (currentSpeakerIndex >= participants.Count)
            {
                isPlayerTurn = true;
                return;
            }

            ShowSpeakerFromCache(currentSpeakerIndex);
        }

        private void ShowSpeakerFromCache(int idx)
        {
            string text;
            if (_cachedResponses.TryGetValue(idx, out text) && !string.IsNullOrWhiteSpace(text) && text != "…")
            {
                currentDialogueText = text;
                isSendingRequest = false;
            }
            else
            {
                currentDialogueText = "…";
                // Keep isSendingRequest true so CheckPendingResponse can pick up late response
                isSendingRequest = true;
            }
            displayedText = "";
            visibleChars = 0;
            isTyping = true;
            lastCharTime = Time.realtimeSinceStartup;
            pauseForClick = false;

            if (idx >= 0 && idx < participants.Count)
            {
                var sp = participants[idx];
                turnRecords.Add(new GroupTurnRecord { SpeakerPawnId = sp.PawnId, SpeakerName = sp.DisplayName, DialogueText = currentDialogueText, IsPlayer = false });
                dialogPages.Add(new DialoguePage { speakerName = sp.DisplayName, text = currentDialogueText });

                if (!string.IsNullOrWhiteSpace(text) && text != "…")
                    RpgDialogueTraceTracker.RegisterTurn(initiator, sp.Pawn, false, text, dialogueSessionId);
            }
        }

        // ── Called from DoWindowContents ──

        private void UpdateFlowControl()
        {
            CheckPlayerTextTransition();
            CheckPendingResponse();
        }

        // ── Helpers ──

        private void SkipInvalidPawnsForward()
        {
            while (currentSpeakerIndex < participants.Count)
            {
                var p = participants[currentSpeakerIndex];
                if (p.Pawn == null || p.Pawn.Dead || p.Pawn.Destroyed || !p.Pawn.Spawned)
                    currentSpeakerIndex++;
                else break;
            }
        }

        private void ResetRoundFlags()
        {
            for (int i = 0; i < participants.Count; i++)
            {
                var p = participants[i];
                p.HasSpokenThisRound = false;
                participants[i] = p;
            }
        }

        private void ExecuteActionsForSpeaker(GroupChatParticipant speaker, List<LLMRpgApiResponse.ApiAction> actions)
        {
            if (actions == null || actions.Count == 0) return;
            foreach (var action in actions)
            {
                string n = Dialog_RPGPawnDialogue.NormalizeRpgActionName(action?.action);
                if (string.IsNullOrEmpty(n)) continue;
                try { ExecuteGroupAction(speaker, n, action); }
                catch (Exception ex) { Log.Error($"[RimChat] Action failed: {ex.Message}"); }
            }
        }

        private static readonly Color FeedbackSuccess = new Color(0.45f, 0.9f, 0.55f);
        private static readonly Color FeedbackInfo = new Color(0.55f, 0.78f, 0.98f);

        private void ExecuteGroupAction(GroupChatParticipant speaker, string normalizedName, LLMRpgApiResponse.ApiAction action)
        {
            Pawn t = speaker.Pawn; string n = speaker.DisplayName;
            switch (normalizedName)
            {
                case "RomanceAttempt": if (t?.relations != null && initiator?.relations != null && t != initiator && !t.relations.DirectRelationExists(PawnRelationDefOf.Spouse, initiator) && !t.relations.DirectRelationExists(PawnRelationDefOf.Fiance, initiator) && !t.relations.DirectRelationExists(PawnRelationDefOf.Lover, initiator)) { t.relations.RemoveDirectRelation(PawnRelationDefOf.ExLover, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.ExSpouse, initiator); t.relations.AddDirectRelation(PawnRelationDefOf.Lover, initiator); AddActionFeedback("RimChat_DragMenu_Romance".Translate() + " → " + n, FeedbackSuccess); } break;
                case "MarriageProposal": if (t?.relations != null && initiator?.relations != null && t != initiator) { t.relations.RemoveDirectRelation(PawnRelationDefOf.ExSpouse, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.ExLover, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.Fiance, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.Lover, initiator); t.relations.AddDirectRelation(PawnRelationDefOf.Spouse, initiator); AddActionFeedback("RimChat_DragMenu_Marry".Translate() + " → " + n, FeedbackSuccess); } break;
                case "Breakup": if (t?.relations != null && initiator?.relations != null) { bool h = t.relations.DirectRelationExists(PawnRelationDefOf.Spouse, initiator); if (h) { t.relations.RemoveDirectRelation(PawnRelationDefOf.Spouse, initiator); t.relations.AddDirectRelation(PawnRelationDefOf.ExSpouse, initiator); } t.relations.RemoveDirectRelation(PawnRelationDefOf.Lover, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.Fiance, initiator); if (!h) t.relations.AddDirectRelation(PawnRelationDefOf.ExLover, initiator); AddActionFeedback("RimChat_DragMenu_Breakup".Translate() + " → " + n, FeedbackSuccess); } break;
                case "Divorce": if (t?.relations != null && initiator?.relations != null) { t.relations.RemoveDirectRelation(PawnRelationDefOf.Fiance, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.Lover, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.Spouse, initiator); t.relations.AddDirectRelation(PawnRelationDefOf.ExSpouse, initiator); AddActionFeedback("RimChat_DragMenu_Divorce".Translate() + " → " + n, FeedbackSuccess); } break;
                case "Date": if (t?.relations != null && initiator?.relations != null && t != initiator && !t.relations.DirectRelationExists(PawnRelationDefOf.Spouse, initiator) && !t.relations.DirectRelationExists(PawnRelationDefOf.Fiance, initiator) && !t.relations.DirectRelationExists(PawnRelationDefOf.Lover, initiator)) { t.relations.RemoveDirectRelation(PawnRelationDefOf.ExLover, initiator); t.relations.RemoveDirectRelation(PawnRelationDefOf.ExSpouse, initiator); t.relations.AddDirectRelation(PawnRelationDefOf.Lover, initiator); AddActionFeedback("RimChat_DragMenu_Date".Translate() + " → " + n, FeedbackSuccess); } break;
                case "TryGainMemory": if (t?.needs?.mood?.thoughts?.memories != null) { var d = RpgMemoryCatalog.ResolveRequestedThoughtDef(action?.defName ?? "", out _); if (d != null) { t.needs.mood.thoughts.memories.TryGainMemory(d, initiator); AddActionFeedback("RimChat_DragMenu_Gift".Translate() + " → " + n, FeedbackSuccess); } } break;
                case "TryAffectSocialGoodwill": if (t?.Faction != null && initiator?.Faction != null) { t.Faction.TryAffectGoodwillWith(initiator.Faction, action.amount, true, true, null); AddActionFeedback("Goodwill " + (action.amount >= 0 ? "+" : "") + action.amount, FeedbackInfo); } break;
                case "Recruit": if (t != null && initiator?.Faction != null && t.Faction != initiator.Faction) { RecruitUtility.Recruit(t, initiator.Faction, initiator); AddActionFeedback("RimChat_DragMenu_Recruit".Translate() + " → " + n, FeedbackSuccess); } break;
                case "ReduceResistance": if (t?.guest != null && t.IsPrisoner && action.amount > 0) { t.guest.resistance = Mathf.Max(0f, t.guest.resistance - action.amount); AddActionFeedback("RimChat_DragMenu_ReduceResist".Translate() + " → " + n, FeedbackSuccess); } break;
                case "ReduceWill": if (t?.guest != null && t.IsPrisoner && action.amount > 0) { t.guest.will = Mathf.Max(0f, t.guest.will - action.amount); AddActionFeedback("RimChat_DragMenu_ReduceWill".Translate() + " → " + n, FeedbackSuccess); } break;
                case "GrantInspiration": if (t?.mindState?.inspirationHandler != null) { var defs = DefDatabase<InspirationDef>.AllDefsListForReading; if (defs != null && defs.Count > 0) { t.mindState.inspirationHandler.TryStartInspiration(defs.RandomElement()); AddActionFeedback("RimChat_DragMenu_Inspiration".Translate() + " → " + n, FeedbackSuccess); } } break;
                case "TriggerIncident": { var iDef = DefDatabase<IncidentDef>.GetNamedSilentFail(action?.defName); Map m = t?.MapHeld ?? Find.CurrentMap; if (iDef != null && m != null) { var ip = StorytellerUtility.DefaultParmsNow(iDef.category, m); ip.faction = t?.Faction; if (action.amount > 0) ip.points = action.amount; iDef.Worker.TryExecute(ip); AddActionFeedback("RimChat_DragMenu_Incident".Translate() + " → " + n, FeedbackSuccess); } } break;
                case "ConvertIdeology": if (DLCCompatibility.IsIdeologyActive && t?.ideo != null && initiator?.ideo != null) { t.ideo.SetIdeo(initiator.Ideo); AddActionFeedback("RimChat_DragMenu_ConvertIdeo".Translate() + " → " + n, FeedbackSuccess); } break;
                case "AdjustCertainty": if (DLCCompatibility.IsIdeologyActive && t?.ideo != null && action.amount != 0) { t.ideo.OffsetCertainty(action.amount); AddActionFeedback("RimChat_DragMenu_AdjCertainty".Translate() + " → " + n, FeedbackSuccess); } break;
            }
        }
    }
}
