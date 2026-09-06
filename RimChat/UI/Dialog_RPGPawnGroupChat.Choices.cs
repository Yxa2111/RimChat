using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Core;
using RimChat.Dialogue;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimChat.Rpg;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimChat.UI
{
    public partial class Dialog_RPGPawnGroupChat
    {
        private readonly List<RpgDialogueChoice> cachedGroupChoices = new List<RpgDialogueChoice>();
        private readonly List<RpgDialogueChoice> currentGroupChoices = new List<RpgDialogueChoice>();
        private readonly List<RpgDialogueTopic> groupTopics = new List<RpgDialogueTopic>();
        private readonly HashSet<string> lockedGroupChoiceActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int selectedGroupChoiceIndex;
        private int selectedGroupTopicIndex;
        private RpgDialogueTopic activeGroupTopic;
        private RpgDialogueGraph activeGroupDialogueGraph;
        private RpgDialogueGraph cachedGroupDialogueGraph;
        private string activeGroupScriptNodeId;
        private string cachedGroupScriptNodeId;
        private bool localGroupGraphTransition;
        private int localGroupGraphSpeakerIndex = -1;
        private bool requestInitialGroupTopics = true;
        private bool sendingGroupTopicSelection;
        private bool completeGroupTopicAfterFinalReaction;
        private bool groupCustomReplyExpanded;
        private bool resolvingGroupChoice;
        private RpgDialogueChoice pendingGroupChoice;
        private Pawn pendingGroupChoiceTarget;
        private RpgSkillCheckResult pendingGroupCheck;
        private RpgSkillCheckResult displayGroupCheck;
        private float groupChoiceResolveAt;
        private float groupDiceOverlayUntil;
        private string pendingGroupResultContext;
        private readonly List<string> groupRpgResultHistory = new List<string>();
        private bool groupFinalReactionRound;
        private bool groupFinalReactionReceived;
        private float groupFinalReactionCloseAt = -1f;
        private bool groupCooldownApplied;
        private float groupEscapeConfirmUntil;
        private float groupEscapeHoldStarted = -1f;
        private bool groupExitAfterSettlement;
        private Vector2 lastGroupRpgPointerPosition;
        private bool hasLastGroupRpgPointerPosition;

        private bool IsGroupChoiceModeEnabled => RimChatMod.Settings?.EnableRpgChoiceMode == true;

        private bool IsGroupDialoguePlaybackComplete => !isTyping &&
            (currentTextPages.Count <= 1 || currentTextPageIndex >= currentTextPages.Count - 1);

        private DialogueResponseExpectation ResolveGroupResponseExpectation(bool includeChoices)
        {
            if (!IsGroupChoiceModeEnabled || !includeChoices) return DialogueResponseExpectation.Default;
            if (requestInitialGroupTopics) return DialogueResponseExpectation.RpgTopics;
            if (groupFinalReactionRound) return DialogueResponseExpectation.RpgFinalReaction;
            return activeGroupTopic != null
                ? DialogueResponseExpectation.RpgScriptGraph
                : DialogueResponseExpectation.Default;
        }

        private bool CanShowGroupChoices => IsGroupChoiceModeEnabled && isPlayerTurn && IsGroupDialoguePlaybackComplete && !isSendingRequest &&
            !isShowingPlayerText && !isViewingHistory && !resolvingGroupChoice && activeGroupTopic != null && currentGroupChoices.Count > 0;

        private bool CanShowGroupTopics => IsGroupChoiceModeEnabled && !requestInitialGroupTopics && activeGroupTopic == null &&
            isPlayerTurn && IsGroupDialoguePlaybackComplete && !isSendingRequest && !isShowingPlayerText && !isViewingHistory &&
            !resolvingGroupChoice && groupTopics.Count > 0;

        private bool IsLastValidGroupSpeaker(int pawnIndex)
        {
            for (int i = pawnIndex + 1; i < participants.Count; i++)
            {
                Pawn pawn = participants[i].Pawn;
                if (pawn != null && !pawn.Dead && !pawn.Destroyed && pawn.Spawned) return false;
            }
            return true;
        }

        private void PrepareAndCacheGroupChoices(DialogueResponseEnvelope envelope, Pawn defaultTarget)
        {
            cachedGroupChoices.Clear();
            cachedGroupDialogueGraph = null;
            cachedGroupScriptNodeId = null;
            if (envelope == null) return;
            if (requestInitialGroupTopics)
            {
                groupTopics.Clear();
                groupTopics.AddRange((envelope.Topics ?? new List<RpgDialogueTopic>())
                    .Where(topic => topic != null && !string.IsNullOrWhiteSpace(topic.Text)).Take(3));
                if (groupTopics.Count == 0) groupTopics.Add(BuildTopicGenerationError(envelope.FailureReason));
                return;
            }
            if (envelope.DialogueGraph != null)
            {
                if (!envelope.DialogueGraph.IsValid)
                {
                    cachedGroupChoices.Add(BuildChoiceGenerationError(null, envelope.DialogueGraph.ErrorReason));
                }
                else
                {
                    string nodeId = string.IsNullOrWhiteSpace(envelope.StartNodeId)
                        ? envelope.DialogueGraph.StartNodeId
                        : envelope.StartNodeId;
                    RpgDialogueScriptNode node = envelope.DialogueGraph.FindNode(nodeId);
                    if (node == null)
                    {
                        cachedGroupChoices.Add(BuildChoiceGenerationError(null, "missing_script_node:" + (nodeId ?? "?")));
                    }
                    else
                    {
                        envelope.StartNodeId = node.Id;
                        envelope.DialogueText = node.Dialogue;
                        envelope.Choices = node.Choices ?? new List<RpgDialogueChoice>();
                        cachedGroupDialogueGraph = envelope.DialogueGraph;
                        cachedGroupScriptNodeId = node.Id;
                    }
                }
            }
            else if (!groupFinalReactionRound)
            {
                envelope.Choices = new List<RpgDialogueChoice>
                {
                    BuildChoiceGenerationError(null, "missing_script_graph")
                };
            }
            if (groupFinalReactionRound)
            {
                cachedGroupChoices.Clear();
                return;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<RpgDialogueChoice> candidates = envelope.Choices ?? new List<RpgDialogueChoice>();
            foreach (RpgDialogueChoice choice in candidates.Take(3))
            {
                if (choice?.IsError == true || choice?.IsReturnToTopics == true)
                {
                    if (choice.IsError)
                        choice.Text = "RimChat_RPGChoice_GenerationError".Translate(choice.ErrorSourceId ?? "?", choice.DisabledReason ?? "unknown");
                    cachedGroupChoices.Add(choice);
                    continue;
                }
                if (choice == null || string.IsNullOrWhiteSpace(choice.Text) || !seen.Add(choice.Id ?? string.Empty))
                {
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice?.Id, "missing_text_or_duplicate_id"));
                    continue;
                }
                if (!HasPolishedGroupChoiceSpeech(choice))
                {
                    Log.Warning($"[RimChat] Invalid group RPG choice '{choice.Id}': missing_or_unpolished_spoken_text");
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice.Id, "missing_or_unpolished_spoken_text"));
                    continue;
                }
                if (choice.RequiresCheck && string.IsNullOrWhiteSpace(choice.Check?.TargetAlias))
                {
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice.Id, "missing_check_target"));
                    continue;
                }
                if (!TryResolveGroupChoiceTarget(choice, defaultTarget, out Pawn targetPawn))
                {
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice.Id, "invalid_target_alias"));
                    continue;
                }
                if (!ValidateGroupEffectTargets(choice, targetPawn))
                {
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice.Id, "effect_target_mismatch"));
                    continue;
                }
                if (ContainsLockedGroupAction(choice, targetPawn))
                {
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice.Id, "high_impact_action_locked"));
                    continue;
                }
                if (!RpgChoiceEffectService.TryValidateChoice(choice, initiator, targetPawn, false, out string reason) ||
                    !RpgChoiceEffectService.TryValidateChoice(choice, initiator, targetPawn, true, out reason))
                {
                    Log.Warning($"[RimChat] Invalid group RPG choice '{choice.Id}': {reason}");
                    cachedGroupChoices.Add(BuildChoiceGenerationError(choice.Id, reason));
                    continue;
                }
                choice.Preview = choice.RequiresCheck ? RpgSkillCheckService.BuildPreview(choice, initiator, targetPawn) : null;
                cachedGroupChoices.Add(choice);
            }

            if (candidates.Count == 0) cachedGroupChoices.Add(BuildChoiceGenerationError(null, "no_choices_returned"));
            List<RpgDialogueChoice> selectable = cachedGroupChoices.Where(choice => choice.IsEnabled && !choice.IsReturnToTopics).ToList();
            if (envelope.DialogueGraph?.IsValid == true)
            {
                foreach (RpgDialogueChoice choice in selectable)
                {
                    if (!RpgChoicePromptBuilder.IsGraphChoiceAllowedAtRound(choice, currentRound, out string graphReason) ||
                        !ValidateGraphChoiceDestinations(choice, envelope.DialogueGraph, out graphReason))
                        MarkDisplayedChoiceError(choice, graphReason);
                }
                List<RpgDialogueChoice> remaining = cachedGroupChoices.Where(choice => choice.IsEnabled && !choice.IsReturnToTopics).ToList();
                if (!RpgChoicePromptBuilder.ValidateGraphChoiceSet(remaining, currentRound, out string setReason))
                    cachedGroupChoices.Add(BuildChoiceGenerationError("graph", setReason));
            }
            else if (selectable.Count > 0) RpgChoicePromptBuilder.EnforceEndingPolicy(selectable, currentRound);
            EnsureReturnToTopicsChoice(cachedGroupChoices);
        }

        private void AdoptCachedGroupChoices()
        {
            if (requestInitialGroupTopics)
            {
                if (groupTopics.Count == 0) groupTopics.Add(BuildTopicGenerationError(null));
                requestInitialGroupTopics = false;
                selectedGroupTopicIndex = FindNextEnabledGroupTopic(0, 1);
                currentGroupChoices.Clear();
                cachedGroupChoices.Clear();
                return;
            }
            if (!groupFinalReactionRound)
            {
                EnsureReturnToTopicsChoice(cachedGroupChoices);
            }
            currentGroupChoices.Clear();
            currentGroupChoices.AddRange(cachedGroupChoices);
            if (cachedGroupDialogueGraph?.IsValid == true)
            {
                activeGroupDialogueGraph = cachedGroupDialogueGraph;
                activeGroupScriptNodeId = cachedGroupScriptNodeId ?? cachedGroupDialogueGraph.StartNodeId;
            }
            cachedGroupChoices.Clear();
            cachedGroupDialogueGraph = null;
            cachedGroupScriptNodeId = null;
            selectedGroupChoiceIndex = currentGroupChoices.Count > 0 ? 0 : -1;
            pendingGroupResultContext = null;
            if (!groupFinalReactionRound && activeGroupTopic != null) EnsureReturnToTopicsChoice(currentGroupChoices);
        }

        private static bool HasPolishedGroupChoiceSpeech(RpgDialogueChoice choice)
        {
            if (choice == null || string.IsNullOrWhiteSpace(choice.Text) || string.IsNullOrWhiteSpace(choice.SpokenText)) return false;
            return !string.Equals(choice.Text.Trim(), choice.SpokenText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void CacheGroupChoiceRequestError(string reason)
        {
            if (!IsGroupChoiceModeEnabled) return;
            if (requestInitialGroupTopics)
            {
                groupTopics.Clear();
                groupTopics.Add(BuildTopicGenerationError(reason));
                return;
            }
            cachedGroupChoices.Clear();
            cachedGroupChoices.Add(BuildChoiceGenerationError(null, reason));
            EnsureReturnToTopicsChoice(cachedGroupChoices);
            cachedGroupDialogueGraph = null;
            cachedGroupScriptNodeId = null;
        }

        private static RpgDialogueTopic BuildTopicGenerationError(string reason)
        {
            string detail = string.IsNullOrWhiteSpace(reason) ? "no_topics_returned" : reason.Trim();
            return new RpgDialogueTopic
            {
                Id = "group_topic_error",
                Text = "RimChat_RPGTopic_GenerationError".Translate(detail),
                IsError = true,
                ErrorReason = detail
            };
        }

        private static RpgDialogueChoice BuildChoiceGenerationError(string choiceId, string reason)
        {
            string id = string.IsNullOrWhiteSpace(choiceId) ? "?" : choiceId.Trim();
            string detail = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
            return new RpgDialogueChoice
            {
                Id = "group_error_" + Guid.NewGuid().ToString("N"),
                Text = "RimChat_RPGChoice_GenerationError".Translate(id, detail),
                IsEnabled = false,
                IsError = true,
                ErrorSourceId = id,
                DisabledReason = detail
            };
        }

        private static RpgDialogueChoice BuildReturnToTopicsChoice()
        {
            return new RpgDialogueChoice
            {
                Id = "group_return_to_topics",
                Text = "RimChat_RPGTopic_BackToTopics".Translate(),
                IsEnabled = true,
                IsReturnToTopics = true
            };
        }

        private static void EnsureReturnToTopicsChoice(List<RpgDialogueChoice> choices)
        {
            if (choices == null || choices.Any(choice => choice?.IsEnabled == true && !choice.IsReturnToTopics)) return;
            if (!choices.Any(choice => choice?.IsReturnToTopics == true)) choices.Add(BuildReturnToTopicsChoice());
        }

        private static bool ValidateGraphChoiceDestinations(RpgDialogueChoice choice, RpgDialogueGraph graph, out string reason)
        {
            reason = null;
            foreach (RpgDialogueChoiceOutcome outcome in new[] { choice?.Success, choice?.Failure })
            {
                RpgDialogueScriptNode next = graph?.FindNode(outcome?.NextNodeId);
                if (next == null) { reason = "missing_next_script_node:" + (outcome?.NextNodeId ?? "?"); return false; }
                if (outcome.EndMode == RpgDialogueEndMode.None && next.Choices.Count == 0) { reason = "continuation_targets_terminal_node:" + next.Id; return false; }
                if (outcome.EndMode != RpgDialogueEndMode.None && next.Choices.Count > 0) { reason = "ending_targets_nonterminal_node:" + next.Id; return false; }
            }
            return true;
        }

        private static void MarkDisplayedChoiceError(RpgDialogueChoice choice, string reason)
        {
            if (choice == null) return;
            string sourceId = choice.ErrorSourceId ?? choice.Id ?? "?";
            choice.IsEnabled = false;
            choice.IsError = true;
            choice.ErrorSourceId = sourceId;
            choice.DisabledReason = reason ?? "invalid_script_edge";
            choice.Text = "RimChat_RPGChoice_GenerationError".Translate(sourceId, choice.DisabledReason);
        }

        private bool TryResolveGroupChoiceTarget(RpgDialogueChoice choice, Pawn fallback, out Pawn targetPawn)
        {
            targetPawn = fallback;
            string alias = choice?.Check?.TargetAlias;
            if (string.IsNullOrWhiteSpace(alias))
            {
                List<string> effectAliases = (choice?.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>())
                    .Concat(choice?.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>())
                    .Select(effect => effect?.targetAlias)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (effectAliases.Count > 1) return false;
                alias = effectAliases.FirstOrDefault();
            }
            if (string.IsNullOrWhiteSpace(alias)) return targetPawn != null;
            if (!alias.StartsWith("npc_", StringComparison.OrdinalIgnoreCase) || !int.TryParse(alias.Substring(4), out int oneBased)) return false;
            int index = oneBased - 1;
            if (index < 0 || index >= participants.Count) return false;
            targetPawn = participants[index].Pawn;
            return targetPawn != null && !targetPawn.Dead && !targetPawn.Destroyed;
        }

        private bool ValidateGroupEffectTargets(RpgDialogueChoice choice, Pawn resolvedTarget)
        {
            foreach (LLMRpgApiResponse.ApiAction effect in (choice.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>())
                .Concat(choice.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()))
            {
                if (string.IsNullOrWhiteSpace(effect.targetAlias)) continue;
                var proxy = new RpgDialogueChoice { Check = new RpgDialogueChoiceCheck { TargetAlias = effect.targetAlias } };
                if (!TryResolveGroupChoiceTarget(proxy, resolvedTarget, out Pawn effectTarget) || effectTarget != resolvedTarget) return false;
            }
            return true;
        }

        private bool ContainsLockedGroupAction(RpgDialogueChoice choice, Pawn targetPawn)
        {
            return (choice.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>())
                .Where(RpgChoiceEffectService.IsHighImpact)
                .Any(effect => lockedGroupChoiceActions.Contains(BuildGroupActionKey(effect, targetPawn)));
        }

        private void DrawGroupChoicePanel(Rect contentRect)
        {
            if (CanShowGroupTopics)
            {
                DrawGroupTopicPanel(contentRect);
                return;
            }
            float panelHeight = Mathf.Min(210f, contentRect.height - 35f);
            Rect panel = new Rect(contentRect.x, contentRect.yMax - panelHeight, contentRect.width, panelHeight);
            float footerHeight = 30f;
            float choiceHeight = Mathf.Clamp((panel.height - footerHeight - 4f) / Math.Max(1, currentGroupChoices.Count), 44f, 60f);
            bool pointerMoved = DidGroupRpgPointerMove();
            for (int i = 0; i < currentGroupChoices.Count; i++)
            {
                RpgDialogueChoice choice = currentGroupChoices[i];
                Rect card = new Rect(panel.x, panel.y + i * choiceHeight, panel.width, choiceHeight - 4f);
                if (pointerMoved && Mouse.IsOver(card) && choice.IsEnabled) selectedGroupChoiceIndex = i;
                bool selected = i == selectedGroupChoiceIndex;
                Widgets.DrawBoxSolid(card, !choice.IsEnabled ? new Color(0.12f, 0.12f, 0.12f, 0.8f) : selected ? new Color(0.23f, 0.35f, 0.52f, 0.96f) : new Color(0.15f, 0.18f, 0.24f, 0.9f));
                if (selected && choice.IsEnabled) Widgets.DrawBox(card, 2);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(card.x + 6f, card.y, 30f, card.height), (i + 1).ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.Label(new Rect(card.x + 42f, card.y + 4f, card.width * 0.54f, card.height - 8f), choice.Text);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.82f, 0.9f, 1f);
                Widgets.Label(new Rect(card.x + card.width * 0.57f, card.y + 4f, card.width * 0.42f - 8f, card.height - 8f), BuildGroupChoiceMeta(choice));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                TooltipHandler.TipRegion(card, BuildGroupChoiceTooltip(choice));
                if (choice.IsEnabled && Widgets.ButtonInvisible(card)) BeginResolveGroupChoice(choice);
            }

            Rect footer = new Rect(panel.x, panel.yMax - footerHeight, panel.width, footerHeight);
            Rect exitRect = new Rect(footer.xMax - 130f, footer.y, 130f, footer.height);
            Rect backRect = new Rect(exitRect.x - 158f, footer.y, 150f, footer.height);
            if (Widgets.ButtonText(exitRect, "RimChat_RPGChoice_Exit".Translate())) RequestGroupExit();
            if (Widgets.ButtonText(backRect, "RimChat_RPGTopic_BackToTopics".Translate())) ReturnToGroupTopicRoot();
            bool canCustom = activeGroupTopic != null && RimChatMod.Settings?.AllowCustomRpgReply == true && currentRound < (RimChatMod.Settings?.RpgChoiceHardEndRound ?? 30);
            Rect customRect = new Rect(footer.x, footer.y, 160f, footer.height);
            if (canCustom && Widgets.ButtonText(customRect, groupCustomReplyExpanded ? "RimChat_RPGChoice_HideCustom".Translate() : "RimChat_RPGChoice_ShowCustom".Translate()))
                groupCustomReplyExpanded = !groupCustomReplyExpanded;
            if (canCustom && groupCustomReplyExpanded)
            {
                float sendWidth = 86f;
                Rect sendRect = new Rect(backRect.x - sendWidth - 8f, footer.y, sendWidth, footer.height);
                Rect inputRect = new Rect(customRect.xMax + 8f, footer.y, sendRect.x - customRect.xMax - 16f, footer.height);
                bool keyboardSubmit = ShouldSendFromKeyboard();
                if (keyboardSubmit) Event.current.Use();
                GUI.SetNextControlName(UserReplyInputControlName);
                userReplyText = Widgets.TextField(inputRect, userReplyText ?? string.Empty);
                bool canSend = !string.IsNullOrWhiteSpace(userReplyText);
                if (Widgets.ButtonText(sendRect, "RimChat_RPGChoice_SendCustom".Translate(), active: canSend) || keyboardSubmit)
                {
                    currentGroupChoices.Clear();
                    TrySendPlayerMessage();
                }
            }
        }

        private void DrawGroupTopicPanel(Rect contentRect)
        {
            float panelHeight = Mathf.Min(210f, contentRect.height - 35f);
            Rect panel = new Rect(contentRect.x, contentRect.yMax - panelHeight, contentRect.width, panelHeight);
            float footerHeight = 30f;
            float cardHeight = Mathf.Clamp((panel.height - footerHeight - 4f) / Math.Max(1, groupTopics.Count), 44f, 58f);
            bool pointerMoved = DidGroupRpgPointerMove();
            for (int i = 0; i < groupTopics.Count; i++)
            {
                RpgDialogueTopic topic = groupTopics[i];
                Rect card = new Rect(panel.x, panel.y + i * cardHeight, panel.width, cardHeight - 4f);
                bool enabled = topic != null && topic.IsEnabled;
                bool selected = enabled && i == selectedGroupTopicIndex;
                if (pointerMoved && enabled && Mouse.IsOver(card)) selectedGroupTopicIndex = i;
                selected = enabled && i == selectedGroupTopicIndex;
                Widgets.DrawBoxSolid(card, !enabled ? new Color(0.10f, 0.10f, 0.10f, 0.82f) : selected ? new Color(0.23f, 0.35f, 0.52f, 0.96f) : new Color(0.15f, 0.18f, 0.24f, 0.9f));
                if (selected) Widgets.DrawBox(card, 2);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(card.x + 8f, card.y, 30f, card.height), (i + 1).ToString());
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = enabled ? Color.white : Color.gray;
                Widgets.Label(new Rect(card.x + 44f, card.y + 2f, card.width - 210f, card.height - 4f), topic?.Text ?? string.Empty);
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(card.xMax - 160f, card.y + 2f, 145f, card.height - 4f),
                    topic?.IsError == true ? "RimChat_RPGChoice_ErrorLabel".Translate() :
                    enabled ? "RimChat_RPGTopic_Available".Translate() : "RimChat_RPGTopic_Completed".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (enabled && Widgets.ButtonInvisible(card)) BeginGroupTopic(topic);
            }
            Rect footer = new Rect(panel.x, panel.yMax - footerHeight, panel.width, footerHeight);
            Widgets.Label(new Rect(footer.x, footer.y, footer.width - 140f, footer.height), "RimChat_RPGTopic_SelectHint".Translate());
            Rect exitRect = new Rect(footer.xMax - 130f, footer.y, 130f, footer.height);
            if (Widgets.ButtonText(exitRect, "RimChat_RPGChoice_Exit".Translate())) RequestGroupExit();
        }

        private bool DidGroupRpgPointerMove()
        {
            Vector2 current = Input.mousePosition;
            bool moved = !hasLastGroupRpgPointerPosition || (current - lastGroupRpgPointerPosition).sqrMagnitude > 0.25f;
            lastGroupRpgPointerPosition = current;
            hasLastGroupRpgPointerPosition = true;
            return moved;
        }

        private void BeginGroupTopic(RpgDialogueTopic topic)
        {
            if (topic == null || !topic.IsEnabled || activeGroupTopic != null || !isPlayerTurn) return;
            activeGroupTopic = topic;
            activeGroupDialogueGraph = null;
            activeGroupScriptNodeId = null;
            currentRound = 0;
            currentGroupChoices.Clear();
            groupCustomReplyExpanded = false;
            sendingGroupTopicSelection = true;
            userReplyText = "RimChat_RPGTopic_PlayerOpens".Translate(topic.Text);
            TrySendPlayerMessage();
        }

        private void ReturnToGroupTopicRoot()
        {
            if (activeGroupTopic == null || resolvingGroupChoice || isSendingRequest || !isPlayerTurn) return;
            int previousIndex = groupTopics.IndexOf(activeGroupTopic);
            activeGroupTopic = null;
            activeGroupDialogueGraph = null;
            activeGroupScriptNodeId = null;
            currentRound = 0;
            currentGroupChoices.Clear();
            cachedGroupChoices.Clear();
            groupCustomReplyExpanded = false;
            userReplyText = string.Empty;
            GUI.FocusControl(null);
            selectedGroupTopicIndex = previousIndex >= 0 && previousIndex < groupTopics.Count && groupTopics[previousIndex].IsEnabled
                ? previousIndex
                : FindNextEnabledGroupTopic(0, 1);
            pendingGroupResultContext = null;
            AddActionFeedback("RimChat_RPGTopic_Returned".Translate(), FeedbackInfo);
        }

        private string BuildGroupChoiceMeta(RpgDialogueChoice choice)
        {
            if (choice?.IsReturnToTopics == true) return "RimChat_RPGChoice_NavigationMeta".Translate();
            if (choice?.IsError == true) return "RimChat_RPGChoice_ErrorLabel".Translate() + "\n" + (choice.DisabledReason ?? string.Empty);
            var lines = new List<string>();
            if (choice.Preview == null) lines.Add("[" + "RimChat_RPGChoice_NoCheck".Translate() + "]");
            else lines.Add($"[{RpgSkillCheckService.GetApproachLabel(choice.Preview.Approach)} · {choice.Preview.SkillLabel} {choice.Preview.SkillLevel}] {SignedGroup(choice.Preview.TotalBonus)} · DC {choice.Preview.Difficulty} · {(choice.Preview.SuccessChance * 100f):0}%");
            if (TryResolveGroupChoiceTarget(choice, participants.LastOrDefault().Pawn, out Pawn targetPawn))
            {
                string effects = string.Join(", ", (choice.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, targetPawn)));
                lines.Add("✓ " + (string.IsNullOrWhiteSpace(effects) ? "RimChat_RPGChoice_NoImmediateEffect".Translate().ToString() : effects));
                string failure = string.Join(", ", (choice.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, targetPawn)));
                lines.Add("✗ " + (string.IsNullOrWhiteSpace(failure) ? "RimChat_RPGChoice_NoImmediateEffect".Translate().ToString() : failure));
            }
            return string.Join("\n", lines);
        }

        private string BuildGroupChoiceTooltip(RpgDialogueChoice choice)
        {
            if (choice?.IsReturnToTopics == true) return "RimChat_RPGChoice_NavigationTooltip".Translate();
            if (choice?.IsError == true) return choice.DisabledReason ?? string.Empty;
            TryResolveGroupChoiceTarget(choice, participants.LastOrDefault().Pawn, out Pawn targetPawn);
            string success = string.Join("; ", (choice?.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, targetPawn)));
            string failure = string.Join("; ", (choice?.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, targetPawn)));
            string details = "RimChat_RPGChoice_Success".Translate(string.IsNullOrWhiteSpace(success) ? "RimChat_RPGChoice_NoImmediateEffect".Translate() : success) + "\n" +
                "RimChat_RPGChoice_Failure".Translate(string.IsNullOrWhiteSpace(failure) ? "RimChat_RPGChoice_NoImmediateEffect".Translate() : failure);
            return string.IsNullOrWhiteSpace(choice?.Preview?.Breakdown) ? details : choice.Preview.Breakdown + "\n" + details;
        }

        private void BeginResolveGroupChoice(RpgDialogueChoice choice)
        {
            if (choice == null || !choice.IsEnabled || resolvingGroupChoice) return;
            if (choice.IsReturnToTopics)
            {
                ReturnToGroupTopicRoot();
                return;
            }
            if (!TryResolveGroupChoiceTarget(choice, participants.LastOrDefault().Pawn, out Pawn targetPawn)) return;
            if (!RpgChoiceEffectService.TryValidateChoice(choice, initiator, targetPawn, false, out string reason) ||
                !RpgChoiceEffectService.TryValidateChoice(choice, initiator, targetPawn, true, out reason))
            {
                AddActionFeedback("RimChat_RPGChoice_Expired".Translate(), new Color(0.95f, 0.55f, 0.55f));
                choice.IsEnabled = false;
                choice.IsError = true;
                choice.Text = "RimChat_RPGChoice_GenerationError".Translate(choice.Id ?? "?", reason ?? "unknown");
                choice.DisabledReason = reason;
                if (string.Equals(reason, "invalid_target", StringComparison.Ordinal) &&
                    !participants.Any(participant => participant.Pawn != null && !participant.Pawn.Dead && !participant.Pawn.Destroyed))
                {
                    ConfirmGroupExit();
                    return;
                }
                RefreshGroupChoicesAfterInvalidation();
                return;
            }
            resolvingGroupChoice = true;
            pendingGroupChoice = choice;
            pendingGroupChoiceTarget = targetPawn;
            pendingGroupCheck = choice.RequiresCheck ? RpgSkillCheckService.Roll(choice.Preview) : null;
            displayGroupCheck = pendingGroupCheck;
            groupChoiceResolveAt = Time.realtimeSinceStartup + (choice.RequiresCheck ? 1.15f : 0.08f);
            groupDiceOverlayUntil = groupChoiceResolveAt + 1.2f;
            currentGroupChoices.Clear();
        }

        private void UpdateGroupChoiceState()
        {
            if (resolvingGroupChoice && Time.realtimeSinceStartup >= groupChoiceResolveAt) CompleteGroupChoice();
            if (groupFinalReactionReceived && !isTyping && !isSendingRequest &&
                (currentTextPages.Count <= 1 || currentTextPageIndex >= currentTextPages.Count - 1))
            {
                if (groupFinalReactionCloseAt < 0f) groupFinalReactionCloseAt = Time.realtimeSinceStartup + 2f;
                if (Time.realtimeSinceStartup >= groupFinalReactionCloseAt) Close();
            }
            if (Input.GetKey(KeyCode.Escape))
            {
                if (groupEscapeHoldStarted < 0f) groupEscapeHoldStarted = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - groupEscapeHoldStarted >= 0.8f) ConfirmGroupExit();
            }
            else groupEscapeHoldStarted = -1f;
        }

        private void CompleteGroupChoice()
        {
            RpgDialogueChoice choice = pendingGroupChoice;
            Pawn targetPawn = pendingGroupChoiceTarget;
            pendingGroupChoice = null;
            pendingGroupChoiceTarget = null;
            resolvingGroupChoice = false;
            if (choice == null || targetPawn == null)
            {
                if (groupExitAfterSettlement) { groupExitAfterSettlement = false; Close(); }
                return;
            }
            bool success = pendingGroupCheck?.Success ?? true;
            RpgDialogueChoiceOutcome outcome = success ? choice.Success : choice.Failure;
            RpgChoiceExecutionResult execution = RpgChoiceEffectService.Execute(outcome, initiator, targetPawn);
            if (!execution.Success)
            {
                string failedContext = BuildGroupResultContext(choice, pendingGroupCheck, execution, targetPawn);
                groupRpgResultHistory.Add(failedContext);
                dialogPages.Add(new DialoguePage { speakerName = "System", text = failedContext });
                AddActionFeedback("RimChat_RPGChoice_EffectFailed".Translate(execution.FailureReason ?? "unknown"), new Color(0.95f, 0.55f, 0.55f));
                pendingGroupCheck = null;
                currentGroupChoices.Clear();
                currentGroupChoices.Add(BuildChoiceGenerationError(choice.Id, execution.FailureReason ?? "effect_execution_failed"));
                EnsureReturnToTopicsChoice(currentGroupChoices);
                selectedGroupChoiceIndex = currentGroupChoices.FindIndex(item => item.IsEnabled);
                if (groupExitAfterSettlement) { groupExitAfterSettlement = false; Close(); }
                return;
            }
            if (!success)
            {
                foreach (LLMRpgApiResponse.ApiAction effect in choice.Success?.Effects ?? Enumerable.Empty<LLMRpgApiResponse.ApiAction>())
                    if (RpgChoiceEffectService.IsHighImpact(effect)) lockedGroupChoiceActions.Add(BuildGroupActionKey(effect, targetPawn));
            }
            pendingGroupResultContext = BuildGroupResultContext(choice, pendingGroupCheck, execution, targetPawn);
            groupRpgResultHistory.Add(pendingGroupResultContext);
            dialogPages.Add(new DialoguePage { speakerName = "System", text = pendingGroupResultContext });
            groupFinalReactionRound = outcome.EndMode != RpgDialogueEndMode.None;
            if (groupFinalReactionRound && activeGroupTopic != null)
            {
                activeGroupTopic.IsCompleted = true;
                completeGroupTopicAfterFinalReaction = outcome.EndMode == RpgDialogueEndMode.Normal &&
                    groupTopics.Any(topic => topic.IsEnabled);
            }
            userReplyText = choice.SpokenText;
            groupCustomReplyExpanded = false;
            pendingGroupCheck = null;
            if (activeGroupDialogueGraph?.IsValid == true)
            {
                if (!TryQueueLocalGroupGraphTransition(choice, outcome, targetPawn))
                {
                    currentGroupChoices.Clear();
                    currentGroupChoices.Add(BuildChoiceGenerationError(choice.Id, "missing_next_script_node:" + (outcome.NextNodeId ?? "?")));
                    EnsureReturnToTopicsChoice(currentGroupChoices);
                    selectedGroupChoiceIndex = currentGroupChoices.FindIndex(item => item.IsEnabled);
                    groupFinalReactionRound = false;
                }
            }
            else
            {
                TrySendPlayerMessage();
            }
            if (groupExitAfterSettlement) { groupExitAfterSettlement = false; Close(); }
        }

        private bool TryQueueLocalGroupGraphTransition(RpgDialogueChoice choice, RpgDialogueChoiceOutcome outcome, Pawn fallbackSpeaker)
        {
            RpgDialogueScriptNode nextNode = activeGroupDialogueGraph?.FindNode(outcome?.NextNodeId);
            if (choice == null || nextNode == null || string.IsNullOrWhiteSpace(choice.SpokenText)) return false;
            int speakerIndex = ResolveGroupScriptSpeakerIndex(nextNode.SpeakerAlias, fallbackSpeaker);
            if (speakerIndex < 0 || speakerIndex >= participants.Count) return false;

            var envelope = new DialogueResponseEnvelope
            {
                VisibleDialogue = nextNode.Dialogue,
                Choices = nextNode.Choices ?? new List<RpgDialogueChoice>(),
                Topics = new List<RpgDialogueTopic>(),
                DialogueGraph = activeGroupDialogueGraph,
                StartNodeId = nextNode.Id,
                IsValid = true,
                ProtocolKind = DialogueResponseProtocolKind.StructuredJson
            };
            PrepareAndCacheGroupChoices(envelope, participants[speakerIndex].Pawn);

            string textToSend = choice.SpokenText.Trim();
            userReplyText = string.Empty;
            GUI.FocusControl(null);
            turnRecords.Add(new GroupTurnRecord
            {
                SpeakerPawnId = initiator.GetUniqueLoadID(),
                SpeakerName = initiator.LabelShort,
                DialogueText = textToSend,
                IsPlayer = true
            });
            dialogPages.Add(new DialoguePage { speakerName = initiator.LabelShort, text = textToSend });
            foreach (GroupChatParticipant participant in participants)
                RpgDialogueTraceTracker.RegisterTurn(initiator, participant.Pawn, true, textToSend, dialogueSessionId);

            currentDialogueText = textToSend;
            displayedText = string.Empty;
            visibleChars = 0;
            isTyping = true;
            lastCharTime = Time.realtimeSinceStartup;
            isShowingPlayerText = true;
            isWaitingForPlayerDelay = false;
            isPlayerTurn = false;
            isViewingHistory = false;
            nextSpeakerRequested = false;
            currentRound++;
            currentGroupChoices.Clear();
            _cachedResponses.Clear();
            _cachedResponses[speakerIndex] = nextNode.Dialogue;
            aiResponseText = nextNode.Dialogue;
            aiResponseReady = true;
            localGroupGraphTransition = true;
            localGroupGraphSpeakerIndex = speakerIndex;
            activeGroupScriptNodeId = nextNode.Id;
            ResetRoundFlags();
            return true;
        }

        private int ResolveGroupScriptSpeakerIndex(string alias, Pawn fallbackSpeaker)
        {
            if (!string.IsNullOrWhiteSpace(alias) && alias.StartsWith("npc_", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(alias.Substring(4), out int oneBased))
            {
                int index = oneBased - 1;
                if (index >= 0 && index < participants.Count && participants[index].Pawn != null) return index;
            }
            return participants.FindIndex(participant => participant.Pawn == fallbackSpeaker);
        }

        private string BuildGroupResultContext(RpgDialogueChoice choice, RpgSkillCheckResult check, RpgChoiceExecutionResult execution, Pawn targetPawn)
        {
            if (check == null) return $"[RpgChoiceResult] topic_id={activeGroupTopic?.Id ?? "none"}; choice_id={choice.Id}; target={targetPawn.LabelShort}; no_check=true; effects={execution.ToHistoryText()}; locked={string.Join(",", lockedGroupChoiceActions)}";
            return $"[RpgChoiceResult] topic_id={activeGroupTopic?.Id ?? "none"}; choice_id={choice.Id}; target={targetPawn.LabelShort}; approach={check.Preview.Approach}; roll={check.Roll}; bonus={check.Preview.TotalBonus}; total={check.Total}; dc={check.Preview.Difficulty}; success={check.Success.ToString().ToLowerInvariant()}; natural_1={check.NaturalOne.ToString().ToLowerInvariant()}; natural_20={check.NaturalTwenty.ToString().ToLowerInvariant()}; effects={execution.ToHistoryText()}; locked={string.Join(",", lockedGroupChoiceActions)}";
        }

        private string BuildGroupActionKey(LLMRpgApiResponse.ApiAction effect, Pawn targetPawn)
        {
            return (RpgChoiceEffectService.NormalizeActionName(effect?.action) ?? "unknown") + "@" + (targetPawn?.GetUniqueLoadID() ?? "none");
        }

        private void HandleGroupChoiceKeyboard()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (e.keyCode == KeyCode.Escape)
            {
                if (Time.realtimeSinceStartup <= groupEscapeConfirmUntil) ConfirmGroupExit();
                else { groupEscapeConfirmUntil = Time.realtimeSinceStartup + 2f; AddActionFeedback("RimChat_RPGChoice_EscapeConfirm".Translate(), FeedbackInfo); }
                e.Use(); return;
            }
            if (GUI.GetNameOfFocusedControl() == UserReplyInputControlName) return;
            if (e.keyCode == KeyCode.Space)
            {
                if (isTyping) { visibleChars = currentDialogueText.Length; displayedText = currentDialogueText; isTyping = false; if (!isPlayerTurn) pauseForClick = true; }
                else if (currentTextPages.Count > 1 && currentTextPageIndex < currentTextPages.Count - 1) currentTextPageIndex++;
                else if (pauseForClick && !isPlayerTurn) { pauseForClick = false; AdvanceToNextSpeaker(); }
                e.Use(); return;
            }
            if (CanShowGroupTopics)
            {
                if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.LeftArrow) { MoveGroupTopic(-1); e.Use(); }
                else if (e.keyCode == KeyCode.DownArrow || e.keyCode == KeyCode.RightArrow) { MoveGroupTopic(1); e.Use(); }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (selectedGroupTopicIndex >= 0 && selectedGroupTopicIndex < groupTopics.Count)
                        BeginGroupTopic(groupTopics[selectedGroupTopicIndex]);
                    e.Use();
                }
                return;
            }
            if (!CanShowGroupChoices) return;
            if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.LeftArrow) { MoveGroupChoice(-1); e.Use(); }
            else if (e.keyCode == KeyCode.DownArrow || e.keyCode == KeyCode.RightArrow) { MoveGroupChoice(1); e.Use(); }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (selectedGroupChoiceIndex >= 0 && selectedGroupChoiceIndex < currentGroupChoices.Count)
                    BeginResolveGroupChoice(currentGroupChoices[selectedGroupChoiceIndex]);
                e.Use();
            }
        }

        private int FindNextEnabledGroupTopic(int start, int direction)
        {
            if (groupTopics.Count == 0) return -1;
            int index = Mathf.Clamp(start, 0, groupTopics.Count - 1);
            for (int i = 0; i < groupTopics.Count; i++)
            {
                int candidate = (index + direction * i + groupTopics.Count) % groupTopics.Count;
                if (groupTopics[candidate]?.IsEnabled == true) return candidate;
            }
            return -1;
        }

        private void MoveGroupTopic(int direction)
        {
            if (!CanShowGroupTopics) return;
            int start = selectedGroupTopicIndex < 0 ? 0 : selectedGroupTopicIndex + direction;
            start = (start + groupTopics.Count) % groupTopics.Count;
            selectedGroupTopicIndex = FindNextEnabledGroupTopic(start, direction);
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
        }

        private void MoveGroupChoice(int direction)
        {
            if (currentGroupChoices.Count == 0) return;
            int start = selectedGroupChoiceIndex;
            for (int i = 0; i < currentGroupChoices.Count; i++)
            {
                start = (start + direction + currentGroupChoices.Count) % currentGroupChoices.Count;
                if (currentGroupChoices[start].IsEnabled) { selectedGroupChoiceIndex = start; break; }
            }
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
        }

        private void RefreshGroupChoicesAfterInvalidation()
        {
            currentGroupChoices.RemoveAll(choice => choice == null);
            List<RpgDialogueChoice> selectable = currentGroupChoices.Where(choice => choice.IsEnabled && !choice.IsReturnToTopics).ToList();
            if (activeGroupDialogueGraph?.IsValid == true)
            {
                foreach (RpgDialogueChoice choice in selectable)
                    if (!RpgChoicePromptBuilder.IsGraphChoiceAllowedAtRound(choice, currentRound, out string reason)) MarkDisplayedChoiceError(choice, reason);
            }
            else if (selectable.Count > 0) RpgChoicePromptBuilder.EnforceEndingPolicy(selectable, currentRound);
            EnsureReturnToTopicsChoice(currentGroupChoices);
            selectedGroupChoiceIndex = currentGroupChoices.FindIndex(choice => choice.IsEnabled);
        }

        private void DrawGroupChoiceOverlays(Rect inRect)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.82f, 0.86f, 0.92f, 0.9f);
            Widgets.Label(new Rect(20f, inRect.height - Dialog_RPGPawnDialogue.DialogueBoxHeight - 28f, inRect.width - 40f, 24f), "RimChat_RPGChoice_KeyHints".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if ((resolvingGroupChoice || Time.realtimeSinceStartup < groupDiceOverlayUntil) && displayGroupCheck != null)
            {
                Rect overlay = new Rect(inRect.center.x - 230f, inRect.center.y - 90f, 460f, 180f);
                Widgets.DrawBoxSolid(overlay, new Color(0.04f, 0.05f, 0.08f, 0.96f)); Widgets.DrawBox(overlay, 2);
                Text.Anchor = TextAnchor.MiddleCenter; Text.Font = GameFont.Medium;
                bool rolling = resolvingGroupChoice && Time.realtimeSinceStartup < groupChoiceResolveAt - 0.25f;
                int shownRoll = rolling ? 1 + (Mathf.FloorToInt(Time.realtimeSinceStartup * 28f) % 20) : displayGroupCheck.Roll;
                string label = rolling ? "..." : displayGroupCheck.Success ? "RimChat_RPGChoice_RollSuccess".Translate() : "RimChat_RPGChoice_RollFailure".Translate();
                string totals = rolling ? string.Empty : $"\n{SignedGroup(displayGroupCheck.Preview.TotalBonus)} → {displayGroupCheck.Total} / DC {displayGroupCheck.Preview.Difficulty}";
                Widgets.Label(overlay.ContractedBy(12f), $"D20: {shownRoll}{totals}\n{label}");
                Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        private void RequestGroupExit()
        {
            if (Time.realtimeSinceStartup <= groupEscapeConfirmUntil) ConfirmGroupExit();
            else { groupEscapeConfirmUntil = Time.realtimeSinceStartup + 2f; AddActionFeedback("RimChat_RPGChoice_EscapeConfirm".Translate(), FeedbackInfo); }
        }

        private void ConfirmGroupExit()
        {
            if (isWindowClosing) return;
            if (resolvingGroupChoice) { groupExitAfterSettlement = true; return; }
            Close();
        }

        private void ApplyGroupPairCooldowns()
        {
            if (groupCooldownApplied || initiator == null) return;
            groupCooldownApplied = true;
            GameComponent_RPGManager manager = Current.Game?.GetComponent<GameComponent_RPGManager>();
            if (manager == null) return;
            foreach (GroupChatParticipant participant in participants)
                if (participant.Pawn != null) manager.StartRandomRpgDialoguePairCooldown(initiator, participant.Pawn);
        }

        private static string SignedGroup(int value) => value >= 0 ? "+" + value : value.ToString();
    }
}
