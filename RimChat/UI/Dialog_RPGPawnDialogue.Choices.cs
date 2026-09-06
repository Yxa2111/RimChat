using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Config;
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
    public partial class Dialog_RPGPawnDialogue
    {
        private readonly List<RpgDialogueChoice> currentRpgChoices = new List<RpgDialogueChoice>();
        private readonly List<RpgDialogueTopic> rpgTopics = new List<RpgDialogueTopic>();
        private readonly HashSet<string> lockedChoiceActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int selectedRpgChoiceIndex;
        private int selectedRpgTopicIndex;
        private int completedRpgChoiceRounds;
        private RpgDialogueTopic activeRpgTopic;
        private int activeRpgTopicHistoryStartIndex = -1;
        private RpgDialogueGraph activeRpgDialogueGraph;
        private string activeRpgScriptNodeId;
        private bool requestInitialRpgTopics = true;
        private bool sendingRpgTopicSelection;
        private bool completeTopicAfterFinalReaction;
        private bool customRpgReplyExpanded;
        private bool isResolvingRpgChoice;
        private RpgDialogueChoice pendingRpgChoice;
        private RpgSkillCheckResult pendingRpgCheckResult;
        private RpgSkillCheckResult displayRpgCheckResult;
        private float resolveRpgChoiceAt;
        private float diceOverlayUntil;
        private string pendingChoiceSystemContext;
        private bool requestFinalRpgReactionOnly;
        private bool finalRpgReactionReceived;
        private float finalRpgReactionCloseAt = -1f;
        private bool pairCooldownApplied;
        private float escapeConfirmUntil;
        private float escapeHoldStarted = -1f;
        private bool exitAfterChoiceSettlement;
        private bool proactiveChoiceBootstrapPending;
        private Vector2 lastRpgPointerPosition;
        private bool hasLastRpgPointerPosition;

        private bool IsRpgChoiceModeEnabled => RimChatMod.Settings?.EnableRpgChoiceMode == true;

        private bool IsRpgDialoguePlaybackComplete => !isTyping &&
            (currentTextPages.Count <= 1 || currentTextPageIndex >= currentTextPages.Count - 1);

        private bool CanShowRpgChoices => IsRpgChoiceModeEnabled &&
            IsRpgDialoguePlaybackComplete && !isSendingInitialMessage && !isShowingUserText && !isViewingHistory &&
            !isDialogueEndedByNpc && !isResolvingRpgChoice && activeRpgTopic != null && currentRpgChoices.Count > 0;

        private bool CanShowRpgTopics => IsRpgChoiceModeEnabled &&
            !requestInitialRpgTopics && activeRpgTopic == null &&
            IsRpgDialoguePlaybackComplete && !isSendingInitialMessage && !isShowingUserText && !isViewingHistory &&
            !isDialogueEndedByNpc && !isResolvingRpgChoice && rpgTopics.Count > 0;

        private int UpcomingRpgChoiceRound => completedRpgChoiceRounds + 1;

        private DialogueResponseExpectation ResolveRpgResponseExpectation()
        {
            if (!IsRpgChoiceModeEnabled) return DialogueResponseExpectation.Default;
            if (requestInitialRpgTopics) return DialogueResponseExpectation.RpgTopics;
            if (requestFinalRpgReactionOnly) return DialogueResponseExpectation.RpgFinalReaction;
            return activeRpgTopic != null
                ? DialogueResponseExpectation.RpgScriptGraph
                : DialogueResponseExpectation.Default;
        }

        private void PrepareChoiceEnvelope(DialogueResponseEnvelope envelope)
        {
            if (!IsRpgChoiceModeEnabled || envelope == null)
            {
                return;
            }

            envelope.Actions = new List<LLMRpgApiResponse.ApiAction>();
            if (requestInitialRpgTopics)
            {
                List<RpgDialogueTopic> topics = (envelope.Topics ?? new List<RpgDialogueTopic>())
                    .Where(topic => topic != null && !string.IsNullOrWhiteSpace(topic.Text))
                    .Take(3)
                    .ToList();
                if (topics.Count == 0) topics.Add(BuildTopicGenerationError(envelope.FailureReason));
                envelope.Topics = topics;
                envelope.Choices = new List<RpgDialogueChoice>();
                return;
            }

            if (envelope.DialogueGraph != null)
            {
                if (!envelope.DialogueGraph.IsValid)
                {
                    envelope.Choices = new List<RpgDialogueChoice>
                    {
                        BuildChoiceGenerationError(null, envelope.DialogueGraph.ErrorReason)
                    };
                }
                else
                {
                    string nodeId = string.IsNullOrWhiteSpace(envelope.StartNodeId)
                        ? envelope.DialogueGraph.StartNodeId
                        : envelope.StartNodeId;
                    RpgDialogueScriptNode node = envelope.DialogueGraph.FindNode(nodeId);
                    if (node == null)
                    {
                        envelope.Choices = new List<RpgDialogueChoice>
                        {
                            BuildChoiceGenerationError(null, "missing_script_node:" + (nodeId ?? "?"))
                        };
                    }
                    else
                    {
                        envelope.StartNodeId = node.Id;
                        envelope.DialogueText = node.Dialogue;
                        envelope.Choices = node.Choices ?? new List<RpgDialogueChoice>();
                    }
                }
            }
            else if (!requestFinalRpgReactionOnly)
            {
                envelope.Choices = new List<RpgDialogueChoice>
                {
                    BuildChoiceGenerationError(null, "missing_script_graph")
                };
            }

            List<RpgDialogueChoice> candidates = envelope.Choices ?? new List<RpgDialogueChoice>();
            var displayed = new List<RpgDialogueChoice>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RpgDialogueChoice choice in candidates.Take(3))
            {
                if (choice?.IsError == true || choice?.IsReturnToTopics == true)
                {
                    if (choice.IsError)
                        choice.Text = "RimChat_RPGChoice_GenerationError".Translate(choice.ErrorSourceId ?? "?", choice.DisabledReason ?? "unknown");
                    displayed.Add(choice);
                    continue;
                }
                if (choice == null || string.IsNullOrWhiteSpace(choice.Text) || !seenIds.Add(choice.Id ?? string.Empty))
                {
                    displayed.Add(BuildChoiceGenerationError(choice?.Id, "missing_text_or_duplicate_id"));
                    continue;
                }

                if (!HasPolishedChoiceSpeech(choice))
                {
                    Log.Warning($"[RimChat] Invalid RPG choice '{choice.Id}': missing_or_unpolished_spoken_text");
                    displayed.Add(BuildChoiceGenerationError(choice.Id, "missing_or_unpolished_spoken_text"));
                    continue;
                }

                if (!ResolveSingleChoiceTarget(choice, out Pawn choiceTarget))
                {
                    displayed.Add(BuildChoiceGenerationError(choice.Id, "invalid_target_alias"));
                    continue;
                }

                if (!ValidateSingleChoiceEffectTargets(choice))
                {
                    displayed.Add(BuildChoiceGenerationError(choice.Id, "effect_target_mismatch"));
                    continue;
                }

                if (ContainsLockedHighImpactAction(choice, choiceTarget))
                {
                    displayed.Add(BuildChoiceGenerationError(choice.Id, "high_impact_action_locked"));
                    continue;
                }

                if (!RpgChoiceEffectService.TryValidateChoice(choice, initiator, choiceTarget, false, out string reason) ||
                    !RpgChoiceEffectService.TryValidateChoice(choice, initiator, choiceTarget, true, out reason))
                {
                    Log.Warning($"[RimChat] Invalid RPG choice '{choice.Id}': {reason}");
                    displayed.Add(BuildChoiceGenerationError(choice.Id, reason));
                    continue;
                }

                choice.Preview = choice.RequiresCheck ? RpgSkillCheckService.BuildPreview(choice, initiator, choiceTarget) : null;
                choice.IsEnabled = true;
                displayed.Add(choice);
            }

            if (candidates.Count == 0)
                displayed.Add(BuildChoiceGenerationError(null, "no_choices_returned"));

            if (!requestFinalRpgReactionOnly)
            {
                List<RpgDialogueChoice> selectable = displayed.Where(choice => choice.IsEnabled && !choice.IsReturnToTopics).ToList();
                if (envelope.DialogueGraph?.IsValid == true)
                {
                    foreach (RpgDialogueChoice choice in selectable)
                    {
                        if (!RpgChoicePromptBuilder.IsGraphChoiceAllowedAtRound(choice, UpcomingRpgChoiceRound, out string graphReason) ||
                            !ValidateGraphChoiceDestinations(choice, envelope.DialogueGraph, out graphReason))
                            MarkDisplayedChoiceError(choice, graphReason);
                    }
                    List<RpgDialogueChoice> remaining = displayed.Where(choice => choice.IsEnabled && !choice.IsReturnToTopics).ToList();
                    if (!RpgChoicePromptBuilder.ValidateGraphChoiceSet(remaining, UpcomingRpgChoiceRound, out string setReason))
                        displayed.Add(BuildChoiceGenerationError("graph", setReason));
                }
                else if (selectable.Count > 0) RpgChoicePromptBuilder.EnforceEndingPolicy(selectable, UpcomingRpgChoiceRound);
                EnsureReturnToTopicsChoice(displayed);
            }

            envelope.Choices = requestFinalRpgReactionOnly ? new List<RpgDialogueChoice>() : displayed;
        }

        private void AdoptChoiceEnvelope(DialogueResponseEnvelope envelope)
        {
            if (!IsRpgChoiceModeEnabled || envelope == null)
            {
                return;
            }

            currentRpgChoices.Clear();
            if (requestInitialRpgTopics)
            {
                rpgTopics.Clear();
                rpgTopics.AddRange(envelope.Topics ?? new List<RpgDialogueTopic>());
                if (rpgTopics.Count == 0) rpgTopics.Add(BuildTopicGenerationError(envelope.FailureReason));
                requestInitialRpgTopics = false;
                selectedRpgTopicIndex = FindNextEnabledTopic(0, 1);
                return;
            }

            if (envelope.Choices != null)
            {
                currentRpgChoices.AddRange(envelope.Choices);
            }
            if (envelope.DialogueGraph?.IsValid == true)
            {
                activeRpgDialogueGraph = envelope.DialogueGraph;
                activeRpgScriptNodeId = envelope.StartNodeId ?? envelope.DialogueGraph.StartNodeId;
            }
            selectedRpgChoiceIndex = FindNextEnabledChoice(0, 1);

            if (!requestFinalRpgReactionOnly && activeRpgTopic != null) EnsureReturnToTopicsChoice(currentRpgChoices);

            if (requestFinalRpgReactionOnly)
            {
                requestFinalRpgReactionOnly = false;
                if (completeTopicAfterFinalReaction && rpgTopics.Any(topic => topic.IsEnabled))
                {
                    activeRpgTopic = null;
                    activeRpgTopicHistoryStartIndex = -1;
                    activeRpgDialogueGraph = null;
                    activeRpgScriptNodeId = null;
                    completedRpgChoiceRounds = 0;
                    completeTopicAfterFinalReaction = false;
                    selectedRpgTopicIndex = FindNextEnabledTopic(0, 1);
                    finalRpgReactionReceived = false;
                    finalRpgReactionCloseAt = -1f;
                }
                else
                {
                    completeTopicAfterFinalReaction = false;
                    activeRpgDialogueGraph = null;
                    activeRpgScriptNodeId = null;
                    finalRpgReactionReceived = true;
                    finalRpgReactionCloseAt = -1f;
                }
            }
        }

        private static RpgDialogueTopic BuildTopicGenerationError(string reason)
        {
            string detail = string.IsNullOrWhiteSpace(reason) ? "no_topics_returned" : reason.Trim();
            return new RpgDialogueTopic
            {
                Id = "local_topic_error",
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
                Id = "local_error_" + Guid.NewGuid().ToString("N"),
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
                Id = "local_return_to_topics",
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

        private static bool HasPolishedChoiceSpeech(RpgDialogueChoice choice)
        {
            if (choice == null || string.IsNullOrWhiteSpace(choice.Text) || string.IsNullOrWhiteSpace(choice.SpokenText)) return false;
            return !string.Equals(choice.Text.Trim(), choice.SpokenText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private bool ResolveSingleChoiceTarget(RpgDialogueChoice choice, out Pawn choiceTarget)
        {
            choiceTarget = target;
            string alias = choice?.Check?.TargetAlias;
            if (string.IsNullOrWhiteSpace(alias) || string.Equals(alias, "npc_1", StringComparison.OrdinalIgnoreCase))
            {
                return target != null;
            }

            return false;
        }

        private static bool ValidateSingleChoiceEffectTargets(RpgDialogueChoice choice)
        {
            return (choice?.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>())
                .Concat(choice?.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>())
                .All(effect => string.IsNullOrWhiteSpace(effect?.targetAlias) ||
                    string.Equals(effect.targetAlias, "npc_1", StringComparison.OrdinalIgnoreCase));
        }

        private bool ContainsLockedHighImpactAction(RpgDialogueChoice choice, Pawn choiceTarget)
        {
            foreach (LLMRpgApiResponse.ApiAction effect in choice?.Success?.Effects ?? Enumerable.Empty<LLMRpgApiResponse.ApiAction>())
            {
                if (!RpgChoiceEffectService.IsHighImpact(effect)) continue;
                string key = BuildChoiceActionLockKey(effect, choiceTarget);
                if (lockedChoiceActions.Contains(key)) return true;
            }
            return false;
        }

        private void DrawRpgChoicePanel(Rect contentRect)
        {
            if (CanShowRpgTopics)
            {
                DrawRpgTopicPanel(contentRect);
                return;
            }

            float panelHeight = Mathf.Min(210f, contentRect.height - 35f);
            Rect panel = new Rect(contentRect.x, contentRect.yMax - panelHeight, contentRect.width, panelHeight);
            float footerHeight = 30f;
            float available = panel.height - footerHeight - 4f;
            float choiceHeight = Mathf.Clamp(available / Math.Max(1, currentRpgChoices.Count), 44f, 60f);
            bool pointerMoved = DidRpgPointerMove();

            for (int i = 0; i < currentRpgChoices.Count; i++)
            {
                RpgDialogueChoice choice = currentRpgChoices[i];
                Rect card = new Rect(panel.x, panel.y + i * choiceHeight, panel.width, choiceHeight - 4f);
                bool hovered = Mouse.IsOver(card);
                if (pointerMoved && hovered && choice.IsEnabled) selectedRpgChoiceIndex = i;
                bool selected = i == selectedRpgChoiceIndex;

                Color background = !choice.IsEnabled
                    ? new Color(0.12f, 0.12f, 0.12f, 0.8f)
                    : selected
                        ? new Color(0.23f, 0.35f, 0.52f, 0.96f)
                        : new Color(0.15f, 0.18f, 0.24f, 0.9f);
                Widgets.DrawBoxSolid(card, background);
                if (selected && choice.IsEnabled)
                {
                    Widgets.DrawBox(new Rect(card.x, card.y, card.width, card.height), 2);
                }

                Rect indexRect = new Rect(card.x + 8f, card.y + 4f, 28f, card.height - 8f);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(indexRect, (i + 1).ToString());
                Text.Anchor = TextAnchor.UpperLeft;

                Rect textRect = new Rect(card.x + 42f, card.y + 4f, card.width * 0.54f, card.height - 8f);
                Widgets.Label(textRect, choice.Text);

                Rect metaRect = new Rect(textRect.xMax + 8f, card.y + 4f, card.xMax - textRect.xMax - 18f, card.height - 8f);
                Text.Font = GameFont.Tiny;
                GUI.color = choice.IsEnabled ? new Color(0.82f, 0.9f, 1f) : Color.gray;
                Widgets.Label(metaRect, BuildChoiceMetaText(choice));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (choice.IsReturnToTopics || choice.IsError || !string.IsNullOrWhiteSpace(choice.Preview?.Breakdown))
                {
                    string breakdown = choice.Preview?.Breakdown;
                    TooltipHandler.TipRegion(card, string.IsNullOrWhiteSpace(breakdown)
                        ? BuildChoiceEffectsTooltip(choice)
                        : breakdown + "\n" + BuildChoiceEffectsTooltip(choice));
                }

                if (choice.IsEnabled && Widgets.ButtonInvisible(card))
                {
                    selectedRpgChoiceIndex = i;
                    BeginResolveRpgChoice(choice);
                }
            }

            Rect footer = new Rect(panel.x, panel.yMax - footerHeight, panel.width, footerHeight);
            float exitWidth = 130f;
            Rect exitRect = new Rect(footer.xMax - exitWidth, footer.y, exitWidth, footer.height);
            float backWidth = 150f;
            Rect backRect = new Rect(exitRect.x - backWidth - 8f, footer.y, backWidth, footer.height);
            if (Widgets.ButtonText(exitRect, "RimChat_RPGChoice_Exit".Translate()))
            {
                RequestRpgExit();
            }
            if (Widgets.ButtonText(backRect, "RimChat_RPGTopic_BackToTopics".Translate()))
            {
                ReturnToRpgTopicRoot();
            }

            bool canCustom = activeRpgTopic != null && RimChatMod.Settings?.AllowCustomRpgReply == true &&
                UpcomingRpgChoiceRound < (RimChatMod.Settings?.RpgChoiceHardEndRound ?? 30);
            Rect customRect = new Rect(footer.x, footer.y, 160f, footer.height);
            if (canCustom && Widgets.ButtonText(customRect, customRpgReplyExpanded
                    ? "RimChat_RPGChoice_HideCustom".Translate()
                    : "RimChat_RPGChoice_ShowCustom".Translate()))
            {
                customRpgReplyExpanded = !customRpgReplyExpanded;
                if (!customRpgReplyExpanded) GUI.FocusControl(null);
            }

            if (canCustom && customRpgReplyExpanded)
            {
                float sendWidth = 86f;
                Rect sendRect = new Rect(backRect.x - sendWidth - 8f, footer.y, sendWidth, footer.height);
                Rect inputRect = new Rect(customRect.xMax + 8f, footer.y, sendRect.x - customRect.xMax - 16f, footer.height);
                bool submitFromKeyboard = ShouldSendFromKeyboard(Event.current);
                if (submitFromKeyboard) Event.current.Use();
                GUI.SetNextControlName(UserReplyInputControlName);
                userReplyText = Widgets.TextField(inputRect, userReplyText ?? string.Empty);
                bool submit = submitFromKeyboard;
                bool canSend = CanSendUserReplyFromKeyboard();
                if (Widgets.ButtonText(sendRect, "RimChat_RPGChoice_SendCustom".Translate(), active: canSend))
                {
                    submit = canSend;
                }
                if (submit)
                {
                    currentRpgChoices.Clear();
                    TrySendMessage();
                }
            }
        }

        private void DrawRpgTopicPanel(Rect contentRect)
        {
            float panelHeight = Mathf.Min(210f, contentRect.height - 35f);
            Rect panel = new Rect(contentRect.x, contentRect.yMax - panelHeight, contentRect.width, panelHeight);
            float footerHeight = 30f;
            float cardHeight = Mathf.Clamp((panel.height - footerHeight - 4f) / Math.Max(1, rpgTopics.Count), 44f, 58f);
            bool pointerMoved = DidRpgPointerMove();

            for (int i = 0; i < rpgTopics.Count; i++)
            {
                RpgDialogueTopic topic = rpgTopics[i];
                Rect card = new Rect(panel.x, panel.y + i * cardHeight, panel.width, cardHeight - 4f);
                bool enabled = topic != null && topic.IsEnabled;
                bool selected = enabled && i == selectedRpgTopicIndex;
                if (pointerMoved && enabled && Mouse.IsOver(card)) selectedRpgTopicIndex = i;
                selected = enabled && i == selectedRpgTopicIndex;
                Widgets.DrawBoxSolid(card, !enabled
                    ? new Color(0.10f, 0.10f, 0.10f, 0.82f)
                    : selected ? new Color(0.23f, 0.35f, 0.52f, 0.96f) : new Color(0.15f, 0.18f, 0.24f, 0.9f));
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

                if (enabled && Widgets.ButtonInvisible(card)) BeginRpgTopic(topic);
            }

            Rect footer = new Rect(panel.x, panel.yMax - footerHeight, panel.width, footerHeight);
            Widgets.Label(new Rect(footer.x, footer.y, footer.width - 140f, footer.height), "RimChat_RPGTopic_SelectHint".Translate());
            Rect exitRect = new Rect(footer.xMax - 130f, footer.y, 130f, footer.height);
            if (Widgets.ButtonText(exitRect, "RimChat_RPGChoice_Exit".Translate())) RequestRpgExit();
        }

        private bool DidRpgPointerMove()
        {
            Vector2 current = Input.mousePosition;
            bool moved = !hasLastRpgPointerPosition || (current - lastRpgPointerPosition).sqrMagnitude > 0.25f;
            lastRpgPointerPosition = current;
            hasLastRpgPointerPosition = true;
            return moved;
        }

        private void BeginRpgTopic(RpgDialogueTopic topic)
        {
            if (topic == null || !topic.IsEnabled || activeRpgTopic != null || isSendingInitialMessage) return;
            activeRpgTopicHistoryStartIndex = chatHistory.Count;
            activeRpgTopic = topic;
            activeRpgDialogueGraph = null;
            activeRpgScriptNodeId = null;
            completedRpgChoiceRounds = 0;
            currentRpgChoices.Clear();
            customRpgReplyExpanded = false;
            sendingRpgTopicSelection = true;
            userReplyText = "RimChat_RPGTopic_PlayerOpens".Translate(topic.Text);
            TrySendMessage();
        }

        private void ReturnToRpgTopicRoot()
        {
            if (activeRpgTopic == null || isResolvingRpgChoice || isSendingInitialMessage) return;
            int previousIndex = rpgTopics.IndexOf(activeRpgTopic);
            activeRpgTopic = null;
            activeRpgTopicHistoryStartIndex = -1;
            activeRpgDialogueGraph = null;
            activeRpgScriptNodeId = null;
            completedRpgChoiceRounds = 0;
            currentRpgChoices.Clear();
            customRpgReplyExpanded = false;
            userReplyText = string.Empty;
            GUI.FocusControl(null);
            selectedRpgTopicIndex = previousIndex >= 0 && previousIndex < rpgTopics.Count && rpgTopics[previousIndex].IsEnabled
                ? previousIndex
                : FindNextEnabledTopic(0, 1);
            pendingChoiceSystemContext = null;
            AddSystemFeedback("RimChat_RPGTopic_Returned".Translate(), 2.5f);
        }

        private string BuildChoiceMetaText(RpgDialogueChoice choice)
        {
            if (choice?.IsReturnToTopics == true) return "RimChat_RPGChoice_NavigationMeta".Translate();
            if (choice?.IsError == true) return "RimChat_RPGChoice_ErrorLabel".Translate() + "\n" + (choice.DisabledReason ?? string.Empty);
            var lines = new List<string>();
            if (choice.Preview != null)
            {
                lines.Add($"[{RpgSkillCheckService.GetApproachLabel(choice.Preview.Approach)} · {choice.Preview.SkillLabel} {choice.Preview.SkillLevel}] {Signed(choice.Preview.TotalBonus)} · DC {choice.Preview.Difficulty} · {(choice.Preview.SuccessChance * 100f):0}%");
            }
            else
            {
                lines.Add("[" + "RimChat_RPGChoice_NoCheck".Translate() + "]");
            }

            string success = string.Join(", ", (choice.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, target)));
            lines.Add("✓ " + (string.IsNullOrWhiteSpace(success) ? "RimChat_RPGChoice_NoImmediateEffect".Translate().ToString() : success));
            string failure = string.Join(", ", (choice.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, target)));
            lines.Add("✗ " + (string.IsNullOrWhiteSpace(failure) ? "RimChat_RPGChoice_NoImmediateEffect".Translate().ToString() : failure));
            return string.Join("\n", lines);
        }

        private string BuildChoiceEffectsTooltip(RpgDialogueChoice choice)
        {
            if (choice?.IsReturnToTopics == true) return "RimChat_RPGChoice_NavigationTooltip".Translate();
            if (choice?.IsError == true) return choice.DisabledReason ?? string.Empty;
            string success = string.Join("; ", (choice.Success?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, target)));
            string failure = string.Join("; ", (choice.Failure?.Effects ?? new List<LLMRpgApiResponse.ApiAction>()).Select(effect => RpgChoiceEffectService.Describe(effect, target)));
            return "RimChat_RPGChoice_Success".Translate(string.IsNullOrWhiteSpace(success) ? "RimChat_RPGChoice_NoImmediateEffect".Translate() : success) + "\n" +
                   "RimChat_RPGChoice_Failure".Translate(string.IsNullOrWhiteSpace(failure) ? "RimChat_RPGChoice_NoImmediateEffect".Translate() : failure);
        }

        private void BeginResolveRpgChoice(RpgDialogueChoice choice)
        {
            if (choice == null || !choice.IsEnabled || isResolvingRpgChoice) return;
            if (choice.IsReturnToTopics)
            {
                ReturnToRpgTopicRoot();
                return;
            }
            if (!ResolveSingleChoiceTarget(choice, out Pawn choiceTarget) || !ValidateSingleChoiceEffectTargets(choice)) return;
            if (!RpgChoiceEffectService.TryValidateChoice(choice, initiator, choiceTarget, false, out string reason) ||
                !RpgChoiceEffectService.TryValidateChoice(choice, initiator, choiceTarget, true, out reason))
            {
                choice.IsEnabled = false;
                choice.IsError = true;
                choice.Text = "RimChat_RPGChoice_GenerationError".Translate(choice.Id ?? "?", reason ?? "unknown");
                choice.DisabledReason = reason;
                AddSystemFeedback("RimChat_RPGChoice_Expired".Translate(), 4f);
                if (string.Equals(reason, "invalid_target", StringComparison.Ordinal))
                {
                    ConfirmRpgExit();
                    return;
                }
                RefreshSingleChoicesAfterInvalidation();
                return;
            }

            isResolvingRpgChoice = true;
            pendingRpgChoice = choice;
            pendingRpgCheckResult = choice.RequiresCheck ? RpgSkillCheckService.Roll(choice.Preview) : null;
            displayRpgCheckResult = pendingRpgCheckResult;
            float delay = choice.RequiresCheck ? 1.15f : 0.08f;
            resolveRpgChoiceAt = Time.realtimeSinceStartup + delay;
            diceOverlayUntil = resolveRpgChoiceAt + 1.2f;
            currentRpgChoices.Clear();
        }

        private void UpdateRpgChoiceResolution()
        {
            if (proactiveChoiceBootstrapPending && !isTyping && !isSendingInitialMessage && !isShowingUserText)
            {
                proactiveChoiceBootstrapPending = false;
                SendInitialMessage();
            }

            if (isResolvingRpgChoice && Time.realtimeSinceStartup >= resolveRpgChoiceAt)
            {
                CompleteRpgChoiceResolution();
            }

            if (finalRpgReactionReceived && !isTyping && !isShowingUserText && !isSendingInitialMessage &&
                (currentTextPages.Count <= 1 || currentTextPageIndex >= currentTextPages.Count - 1))
            {
                if (finalRpgReactionCloseAt < 0f) finalRpgReactionCloseAt = Time.realtimeSinceStartup + 2f;
                if (Time.realtimeSinceStartup >= finalRpgReactionCloseAt) Close();
            }

            if (Input.GetKey(KeyCode.Escape))
            {
                if (escapeHoldStarted < 0f) escapeHoldStarted = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - escapeHoldStarted >= 0.8f) ConfirmRpgExit();
            }
            else
            {
                escapeHoldStarted = -1f;
            }
        }

        private void CompleteRpgChoiceResolution()
        {
            RpgDialogueChoice choice = pendingRpgChoice;
            pendingRpgChoice = null;
            isResolvingRpgChoice = false;
            if (choice == null)
            {
                if (exitAfterChoiceSettlement) { exitAfterChoiceSettlement = false; Close(); }
                return;
            }

            bool checkSuccess = pendingRpgCheckResult?.Success ?? true;
            RpgDialogueChoiceOutcome outcome = checkSuccess ? choice.Success : choice.Failure;
            RpgChoiceExecutionResult execution = RpgChoiceEffectService.Execute(outcome, initiator, target);
            if (!execution.Success)
            {
                string failedContext = BuildChoiceResultContext(choice, pendingRpgCheckResult, execution);
                chatHistory.Add(new ChatMessageData { role = "system", content = failedContext });
                dialogPages.Add(new DialoguePage { speakerName = "System", text = failedContext });
                RecordSessionDialogueTurn("System", failedContext, false);
                AddSystemFeedback("RimChat_RPGChoice_EffectFailed".Translate(execution.FailureReason ?? "unknown"), 4.5f);
                pendingRpgCheckResult = null;
                currentRpgChoices.Clear();
                currentRpgChoices.Add(BuildChoiceGenerationError(choice.Id, execution.FailureReason ?? "effect_execution_failed"));
                EnsureReturnToTopicsChoice(currentRpgChoices);
                selectedRpgChoiceIndex = FindNextEnabledChoice(0, 1);
                if (exitAfterChoiceSettlement) { exitAfterChoiceSettlement = false; Close(); }
                return;
            }

            if (!checkSuccess)
            {
                foreach (LLMRpgApiResponse.ApiAction effect in choice.Success?.Effects ?? Enumerable.Empty<LLMRpgApiResponse.ApiAction>())
                {
                    if (RpgChoiceEffectService.IsHighImpact(effect))
                        lockedChoiceActions.Add(BuildChoiceActionLockKey(effect, target));
                }
            }

            pendingChoiceSystemContext = BuildChoiceResultContext(choice, pendingRpgCheckResult, execution);
            requestFinalRpgReactionOnly = outcome.EndMode != RpgDialogueEndMode.None;
            if (requestFinalRpgReactionOnly && activeRpgTopic != null)
            {
                activeRpgTopic.IsCompleted = true;
                completeTopicAfterFinalReaction = outcome.EndMode == RpgDialogueEndMode.Normal &&
                    rpgTopics.Any(topic => topic.IsEnabled);
            }
            userReplyText = choice.SpokenText;
            customRpgReplyExpanded = false;
            if (activeRpgDialogueGraph?.IsValid == true)
            {
                if (!TryQueueLocalRpgGraphTransition(choice, outcome))
                {
                    currentRpgChoices.Clear();
                    currentRpgChoices.Add(BuildChoiceGenerationError(choice.Id, "missing_next_script_node:" + (outcome.NextNodeId ?? "?")));
                    EnsureReturnToTopicsChoice(currentRpgChoices);
                    selectedRpgChoiceIndex = FindNextEnabledChoice(0, 1);
                    requestFinalRpgReactionOnly = false;
                }
            }
            else
            {
                TrySendMessage();
            }
            pendingRpgCheckResult = null;

            if (exitAfterChoiceSettlement)
            {
                exitAfterChoiceSettlement = false;
                Close();
            }
        }

        private bool TryQueueLocalRpgGraphTransition(RpgDialogueChoice choice, RpgDialogueChoiceOutcome outcome)
        {
            RpgDialogueScriptNode nextNode = activeRpgDialogueGraph?.FindNode(outcome?.NextNodeId);
            if (choice == null || nextNode == null || string.IsNullOrWhiteSpace(choice.SpokenText)) return false;

            string textToSend = choice.SpokenText.Trim();
            chatHistory.Add(new ChatMessageData { role = "user", content = textToSend });
            completedRpgChoiceRounds++;
            currentRpgChoices.Clear();
            if (!string.IsNullOrWhiteSpace(pendingChoiceSystemContext))
            {
                chatHistory.Add(new ChatMessageData { role = "system", content = pendingChoiceSystemContext });
                dialogPages.Add(new DialoguePage { speakerName = "System", text = pendingChoiceSystemContext });
                RecordSessionDialogueTurn("System", pendingChoiceSystemContext, false);
                pendingChoiceSystemContext = null;
            }
            dialogPages.Add(new DialoguePage { speakerName = initiator.LabelShort, text = textToSend });
            RecordSessionDialogueTurn(initiator.LabelShort, textToSend, true);
            RpgDialogueTraceTracker.RegisterTurn(initiator, target, true, textToSend, dialogueSessionId);
            userReplyText = string.Empty;
            GUI.FocusControl(null);
            isViewingHistory = false;
            ResetDialogueTextPaging();
            currentSpeakerName = initiator.LabelShort;
            currentDialogueText = textToSend;
            displayedText = string.Empty;
            visibleChars = 0;
            isTyping = true;
            isShowingUserText = true;
            isWaitingForDelayAfterUser = false;
            lastCharTime = Time.realtimeSinceStartup;

            var envelope = new DialogueResponseEnvelope
            {
                VisibleDialogue = nextNode.Dialogue,
                Choices = nextNode.Choices ?? new List<RpgDialogueChoice>(),
                Topics = new List<RpgDialogueTopic>(),
                DialogueGraph = activeRpgDialogueGraph,
                StartNodeId = nextNode.Id,
                IsValid = true,
                ProtocolKind = DialogueResponseProtocolKind.StructuredJson
            };
            PrepareChoiceEnvelope(envelope);
            pendingResponseEnvelope = envelope;
            activeRpgScriptNodeId = nextNode.Id;
            aiResponseText = envelope.DialogueText ?? nextNode.Dialogue;
            aiResponseReady = true;
            chatHistory.Add(new ChatMessageData { role = "assistant", content = aiResponseText });
            RpgDialogueTraceTracker.RegisterTurn(initiator, target, false, aiResponseText, dialogueSessionId);
            return true;
        }

        private string BuildChoiceResultContext(RpgDialogueChoice choice, RpgSkillCheckResult check, RpgChoiceExecutionResult execution)
        {
            if (check == null)
            {
                return $"[RpgChoiceResult] topic_id={activeRpgTopic?.Id ?? "none"}; choice_id={choice.Id}; no_check=true; effects={execution.ToHistoryText()}; locked={string.Join(",", lockedChoiceActions)}";
            }

            return $"[RpgChoiceResult] topic_id={activeRpgTopic?.Id ?? "none"}; choice_id={choice.Id}; approach={check.Preview.Approach}; roll={check.Roll}; bonus={check.Preview.TotalBonus}; total={check.Total}; dc={check.Preview.Difficulty}; success={check.Success.ToString().ToLowerInvariant()}; natural_1={check.NaturalOne.ToString().ToLowerInvariant()}; natural_20={check.NaturalTwenty.ToString().ToLowerInvariant()}; effects={execution.ToHistoryText()}; locked={string.Join(",", lockedChoiceActions)}";
        }

        private string BuildChoiceActionLockKey(LLMRpgApiResponse.ApiAction effect, Pawn choiceTarget)
        {
            return (RpgChoiceEffectService.NormalizeActionName(effect?.action) ?? "unknown") + "@" + (choiceTarget?.GetUniqueLoadID() ?? "none");
        }

        private int FindNextEnabledChoice(int start, int direction)
        {
            if (currentRpgChoices.Count == 0) return -1;
            int index = Mathf.Clamp(start, 0, currentRpgChoices.Count - 1);
            for (int i = 0; i < currentRpgChoices.Count; i++)
            {
                int candidate = (index + direction * i + currentRpgChoices.Count) % currentRpgChoices.Count;
                if (currentRpgChoices[candidate].IsEnabled) return candidate;
            }
            return -1;
        }

        private int FindNextEnabledTopic(int start, int direction)
        {
            if (rpgTopics.Count == 0) return -1;
            int index = Mathf.Clamp(start, 0, rpgTopics.Count - 1);
            for (int i = 0; i < rpgTopics.Count; i++)
            {
                int candidate = (index + direction * i + rpgTopics.Count) % rpgTopics.Count;
                if (rpgTopics[candidate]?.IsEnabled == true) return candidate;
            }
            return -1;
        }

        private void RefreshSingleChoicesAfterInvalidation()
        {
            currentRpgChoices.RemoveAll(choice => choice == null);
            List<RpgDialogueChoice> selectable = currentRpgChoices.Where(choice => choice.IsEnabled && !choice.IsReturnToTopics).ToList();
            if (activeRpgDialogueGraph?.IsValid == true)
            {
                foreach (RpgDialogueChoice choice in selectable)
                    if (!RpgChoicePromptBuilder.IsGraphChoiceAllowedAtRound(choice, UpcomingRpgChoiceRound, out string reason)) MarkDisplayedChoiceError(choice, reason);
            }
            else if (selectable.Count > 0) RpgChoicePromptBuilder.EnforceEndingPolicy(selectable, UpcomingRpgChoiceRound);
            EnsureReturnToTopicsChoice(currentRpgChoices);
            selectedRpgChoiceIndex = FindNextEnabledChoice(0, 1);
        }

        private void MoveRpgChoiceSelection(int direction)
        {
            if (!CanShowRpgChoices) return;
            int start = selectedRpgChoiceIndex < 0 ? 0 : selectedRpgChoiceIndex + direction;
            start = (start + currentRpgChoices.Count) % currentRpgChoices.Count;
            selectedRpgChoiceIndex = FindNextEnabledChoice(start, direction);
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
        }

        private void MoveRpgTopicSelection(int direction)
        {
            if (!CanShowRpgTopics) return;
            int start = selectedRpgTopicIndex < 0 ? 0 : selectedRpgTopicIndex + direction;
            start = (start + rpgTopics.Count) % rpgTopics.Count;
            selectedRpgTopicIndex = FindNextEnabledTopic(start, direction);
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
        }

        private void HandleRpgChoiceKeyboardInput()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.Escape)
            {
                if (Time.realtimeSinceStartup <= escapeConfirmUntil) ConfirmRpgExit();
                else
                {
                    escapeConfirmUntil = Time.realtimeSinceStartup + 2f;
                    AddSystemFeedback("RimChat_RPGChoice_EscapeConfirm".Translate(), 2.1f);
                }
                e.Use();
                return;
            }

            bool inputFocused = IsUserReplyInputFocused();
            if (inputFocused) return;

            if (e.keyCode == KeyCode.Space)
            {
                if (isTyping)
                {
                    visibleChars = currentDialogueText.Length;
                    displayedText = currentDialogueText;
                    isTyping = false;
                    if (isShowingUserText)
                    {
                        isWaitingForDelayAfterUser = true;
                        timeUserTextFinished = Time.realtimeSinceStartup;
                    }
                }
                else if (currentTextPages.Count > 1 && currentTextPageIndex < currentTextPages.Count - 1)
                {
                    currentTextPageIndex++;
                }
                e.Use();
                return;
            }

            if (CanShowRpgTopics)
            {
                if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.LeftArrow)
                {
                    MoveRpgTopicSelection(-1);
                    e.Use();
                }
                else if (e.keyCode == KeyCode.DownArrow || e.keyCode == KeyCode.RightArrow)
                {
                    MoveRpgTopicSelection(1);
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (selectedRpgTopicIndex >= 0 && selectedRpgTopicIndex < rpgTopics.Count)
                        BeginRpgTopic(rpgTopics[selectedRpgTopicIndex]);
                    e.Use();
                }
                return;
            }

            if (!CanShowRpgChoices) return;
            if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.LeftArrow)
            {
                MoveRpgChoiceSelection(-1);
                e.Use();
            }
            else if (e.keyCode == KeyCode.DownArrow || e.keyCode == KeyCode.RightArrow)
            {
                MoveRpgChoiceSelection(1);
                e.Use();
            }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (selectedRpgChoiceIndex >= 0 && selectedRpgChoiceIndex < currentRpgChoices.Count)
                    BeginResolveRpgChoice(currentRpgChoices[selectedRpgChoiceIndex]);
                e.Use();
            }
        }

        private void DrawRpgChoiceOverlays(Rect inRect)
        {
            Rect hint = new Rect(20f, inRect.height - DialogueBoxHeight - 28f, inRect.width - 40f, 24f);
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.82f, 0.86f, 0.92f, 0.9f);
            Widgets.Label(hint, "RimChat_RPGChoice_KeyHints".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if ((isResolvingRpgChoice || Time.realtimeSinceStartup < diceOverlayUntil) && displayRpgCheckResult != null)
            {
                Rect overlay = new Rect(inRect.center.x - 230f, inRect.center.y - 90f, 460f, 180f);
                Widgets.DrawBoxSolid(overlay, new Color(0.04f, 0.05f, 0.08f, 0.96f));
                Widgets.DrawBox(overlay, 2);
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font = GameFont.Medium;
                bool rolling = isResolvingRpgChoice && Time.realtimeSinceStartup < resolveRpgChoiceAt - 0.25f;
                int shownRoll = rolling ? 1 + (Mathf.FloorToInt(Time.realtimeSinceStartup * 28f) % 20) : displayRpgCheckResult.Roll;
                string result = rolling ? "..." : displayRpgCheckResult.Success ? "RimChat_RPGChoice_RollSuccess".Translate() : "RimChat_RPGChoice_RollFailure".Translate();
                string totals = rolling ? string.Empty : $"\n{Signed(displayRpgCheckResult.Preview.TotalBonus)}  →  {displayRpgCheckResult.Total} / DC {displayRpgCheckResult.Preview.Difficulty}";
                Widgets.Label(overlay.ContractedBy(12f), $"D20: {shownRoll}{totals}\n{result}");
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        private void RequestRpgExit()
        {
            if (Time.realtimeSinceStartup <= escapeConfirmUntil) ConfirmRpgExit();
            else
            {
                escapeConfirmUntil = Time.realtimeSinceStartup + 2f;
                AddSystemFeedback("RimChat_RPGChoice_EscapeConfirm".Translate(), 2.1f);
            }
        }

        private void ConfirmRpgExit()
        {
            if (isWindowClosing) return;
            if (isResolvingRpgChoice)
            {
                exitAfterChoiceSettlement = true;
                return;
            }
            Close();
        }

        private void ApplyPairCooldownOnClose()
        {
            if (pairCooldownApplied || initiator == null || target == null) return;
            pairCooldownApplied = true;
            Current.Game?.GetComponent<GameComponent_RPGManager>()?.StartRandomRpgDialoguePairCooldown(initiator, target);
        }

        private void RecoverChoiceModeAfterRequestFailure()
        {
            if (!IsRpgChoiceModeEnabled) return;
            isSendingInitialMessage = false;
            if (requestFinalRpgReactionOnly)
            {
                requestFinalRpgReactionOnly = false;
                finalRpgReactionReceived = true;
                finalRpgReactionCloseAt = -1f;
                return;
            }

            currentRpgChoices.Clear();
            if (requestInitialRpgTopics)
            {
                rpgTopics.Clear();
                rpgTopics.Add(BuildTopicGenerationError(currentDialogueText));
                requestInitialRpgTopics = false;
                selectedRpgTopicIndex = FindNextEnabledTopic(0, 1);
                return;
            }
            currentRpgChoices.Add(BuildChoiceGenerationError(null, currentDialogueText));
            EnsureReturnToTopicsChoice(currentRpgChoices);
            selectedRpgChoiceIndex = FindNextEnabledChoice(0, 1);
            AddSystemFeedback("RimChat_RPGTopic_RequestFailed".Translate(), 4f);
        }

        private string BuildChoicePromptContract(string basePrompt)
        {
            if (!IsRpgChoiceModeEnabled) return basePrompt;
            if (completedRpgChoiceRounds == 0 && !string.IsNullOrWhiteSpace(proactiveChoiceIntent))
            {
                basePrompt += "\n\n[ProactiveChoiceIntent]\n" + proactiveChoiceIntent +
                    "\nUse this only as story intent for the opening choices. Runtime will recompute all checks from the live pawns.";
            }
            return RpgChoicePromptBuilder.AppendChoiceContract(
                basePrompt,
                UpcomingRpgChoiceRound,
                new[] { "npc_1=" + (target?.LabelShort ?? "NPC") },
                lockedChoiceActions,
                requestFinalRpgReactionOnly,
                requestInitialRpgTopics,
                activeRpgTopic?.Text,
                rpgTopics.Where(topic => topic.IsCompleted).Select(topic => topic.Text),
                activeRpgDialogueGraph?.IsValid == true ? activeRpgScriptNodeId : null);
        }

        private static string Signed(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }
    }
}
