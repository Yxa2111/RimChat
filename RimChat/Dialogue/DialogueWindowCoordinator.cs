using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.UI;
using RimChat.DiplomacySystem;
using RimWorld;
using Verse;

namespace RimChat.Dialogue
{
    /// <summary>
    /// Centralized fail-fast window open coordinator for diplomacy and RPG dialogues.
    /// </summary>
    public static class DialogueWindowCoordinator
    {
        public static bool TryOpen(DialogueOpenIntent intent, out string reason)
        {
            reason = string.Empty;
            if (intent?.RuntimeContext == null)
            {
                reason = "intent_context_null";
                return false;
            }

            DialogueRuntimeContext snapshot = intent.RuntimeContext.WithCurrentRuntimeMarkers();

            // Group chat path: skip single-target resolver/validator
            if (snapshot.ParticipantPawnIds != null && snapshot.ParticipantPawnIds.Count > 0)
            {
                return TryOpenGroupChat(snapshot, intent, out reason);
            }

            if (!DialogueContextResolver.TryResolveLiveContext(snapshot, out DialogueLiveContext liveContext, out reason))
            {
                return false;
            }

            if (!DialogueContextValidator.ValidateWindowOpen(snapshot, liveContext, out reason))
            {
                return false;
            }

            if (IsDuplicateWindow(snapshot))
            {
                reason = "duplicate_window";
                return false;
            }

            if (snapshot.Channel == DialogueChannel.Diplomacy)
            {
                var window = new Dialog_DiplomacyDialogue(
                    liveContext.Faction,
                    liveContext.Negotiator,
                    intent.MuteOpenSound,
                    snapshot,
                    snapshot.WindowKey);
                Find.WindowStack.Add(window);
                return true;
            }

            GameComponent_RPGManager rpgManager = Current.Game?.GetComponent<GameComponent_RPGManager>();
            if (rpgManager != null &&
                (rpgManager.IsRpgDialogueOnCooldown(liveContext.Target, out _) ||
                 rpgManager.IsRpgDialoguePairOnCooldown(liveContext.Initiator, liveContext.Target, out _)))
            {
                reason = "rpg_dialogue_cooldown";
                return false;
            }

            var rpgWindow = new Dialog_RPGPawnDialogue(
                liveContext.Initiator,
                liveContext.Target,
                intent.ProactiveOpening,
                snapshot,
                snapshot.WindowKey,
                intent.ProactiveChoiceIntent);
            Find.WindowStack.Add(rpgWindow);
            return true;
        }

        private static bool TryOpenGroupChat(DialogueRuntimeContext snapshot, DialogueOpenIntent intent, out string reason)
        {
            reason = string.Empty;

            if (Current.ProgramState != ProgramState.Playing)
            {
                reason = "program_state_not_playing";
                return false;
            }

            if (Current.Game == null || Find.WindowStack == null)
            {
                reason = "game_or_window_stack_null";
                return false;
            }

            if (!DialogueContextResolver.TryResolvePawn(snapshot.InitiatorPawnId, out Pawn initiator))
            {
                reason = "initiator_unresolvable";
                return false;
            }

            Map map = initiator.Map;
            if (snapshot.MapUniqueId > 0 && (map == null || map.uniqueID != snapshot.MapUniqueId))
            {
                reason = "map_invalid";
                return false;
            }

            List<Pawn> participants = new List<Pawn>();
            foreach (string pawnId in snapshot.ParticipantPawnIds)
            {
                if (DialogueContextResolver.TryResolvePawn(pawnId, out Pawn participant)
                    && participant != initiator)
                {
                    participants.Add(participant);
                }
            }

            if (participants.Count == 0)
            {
                reason = "no_valid_participants";
                return false;
            }

            GameComponent_RPGManager rpgManager = Current.Game?.GetComponent<GameComponent_RPGManager>();
            if (rpgManager != null && participants.Any(participant =>
                    rpgManager.IsRpgDialogueOnCooldown(participant, out _) ||
                    rpgManager.IsRpgDialoguePairOnCooldown(initiator, participant, out _)))
            {
                reason = "rpg_group_participant_cooldown";
                return false;
            }

            if (IsDuplicateWindow(snapshot))
            {
                reason = "duplicate_window";
                return false;
            }

            var groupWindow = new Dialog_RPGPawnGroupChat(
                initiator,
                participants,
                snapshot,
                snapshot.WindowKey);
            Find.WindowStack.Add(groupWindow);
            return true;
        }

        private static bool IsDuplicateWindow(DialogueRuntimeContext runtimeContext)
        {
            if (Find.WindowStack?.Windows == null || runtimeContext == null)
            {
                return false;
            }

            return Find.WindowStack.Windows.Any(window =>
            {
                if (window is Dialog_DiplomacyDialogue diplomacyWindow)
                {
                    return diplomacyWindow.MatchesWindowLifecycleKey(runtimeContext.WindowKey);
                }

                if (window is Dialog_RPGPawnDialogue rpgWindow)
                {
                    return rpgWindow.MatchesWindowLifecycleKey(runtimeContext.WindowKey);
                }

                if (window is Dialog_RPGPawnGroupChat groupWindow)
                {
                    return groupWindow.MatchesWindowLifecycleKey(runtimeContext.WindowKey);
                }

                return false;
            });
        }
    }
}
