using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;
using RimWorld;
using RimChat.Dialogue;
using RimChat.Core;
using RimChat.WorldState;

namespace RimChat.Comp
{
    public class CompPawnDialogue : ThingComp
    {
        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            if (selPawn == null)
                yield break;

            Pawn targetPawn = this.parent as Pawn;

            // Hide: self-to-self
            if (targetPawn == null || targetPawn == selPawn)
                yield break;

            RimChatTrackedEntityRegistry.TrackPawn(selPawn);
            RimChatTrackedEntityRegistry.TrackPawn(targetPawn);

            // Keep RPG dialogue option available for external pawns (including visitors/caravans)
            // to avoid compatibility regressions in complex mod environments.

            // Hide: target dead/destroyed or not on same map
            if (targetPawn.Dead || targetPawn.Destroyed)
                yield break;

            if (!selPawn.Spawned || !targetPawn.Spawned || selPawn.Map != targetPawn.Map)
                yield break;

            // --- Below this point, always show the button (enabled or disabled) ---

            // Check race eligibility — show disabled if incompatible (including RaceProps null)
            string raceReason = PawnDialogueRoutingPolicy.GetIneligibleRaceReason(targetPawn);
            if (raceReason != null)
            {
                yield return DisabledOption(raceReason);
                yield break;
            }

            // Check RPG dialogue enabled — show disabled if off
            if (RimChatMod.Settings == null || !RimChatMod.Settings.EnableRPGDialogue)
            {
                yield return DisabledOption("RimChat_Converse_Disabled_RpgOff");
                yield break;
            }

            // Check reachability — show disabled if unreachable
            if (!selPawn.CanReach(targetPawn, PathEndMode.InteractionCell, Danger.Deadly))
            {
                yield return DisabledOption("RimChat_Converse_Disabled_Unreachable");
                yield break;
            }

            // Hide: either pawn in combat (matches vanilla style — hide instead of disable)
            if (PawnCombatStateUtility.IsEitherPawnInCombat(selPawn, targetPawn))
                yield break;

            // Disable: either pawn drafted (show disabled button with reason)
            if (PawnCombatStateUtility.IsEitherPawnDrafted(selPawn, targetPawn))
            {
                yield return DisabledOption("RimChat_Converse_Disabled_Drafted");
                yield break;
            }

            // Check asleep/downed — show disabled with fine-grained reason
            if (!RestUtility.Awake(targetPawn) || targetPawn.Downed)
            {
                if (targetPawn.Downed && !RestUtility.Awake(targetPawn))
                    yield return DisabledOption("RimChat_Converse_Disabled_DownedAsleep");
                else if (targetPawn.Downed)
                    yield return DisabledOption("RimChat_Converse_Disabled_Downed");
                else
                    yield return DisabledOption("RimChat_Converse_Disabled_Asleep");
                yield break;
            }

            // Check cooldown — show disabled with remaining time
            var rpgManager = Current.Game?.GetComponent<RimChat.DiplomacySystem.GameComponent_RPGManager>();
            int remainingTicks = 0;
            if (rpgManager != null &&
                (rpgManager.IsRpgDialogueOnCooldown(targetPawn, out remainingTicks) ||
                 rpgManager.IsRpgDialoguePairOnCooldown(selPawn, targetPawn, out remainingTicks)))
            {
                float remainingHours = System.Math.Max(0f, remainingTicks / 2500f);
                string cooldownLabel = "RimChat_Converse_Disabled_Cooldown".Translate(remainingHours.ToString("F1"));
                yield return new FloatMenuOption(cooldownLabel, null);
                yield break;
            }

            // All checks passed — show enabled button
            string label = "RimChat_Converse".Translate();
            yield return new FloatMenuOption(label, () =>
            {
                RimChatTrackedEntityRegistry.TrackPawn(selPawn);
                RimChatTrackedEntityRegistry.TrackPawn(targetPawn);

                JobDef dialogueJobDef = DefDatabase<JobDef>.GetNamedSilentFail("RimChat_RPGDialogue");
                if (dialogueJobDef == null)
                {
                    Log.Warning("[RimChat] Missing JobDef RimChat_RPGDialogue, fallback to direct dialogue open.");
                    DialogueWindowCoordinator.TryOpen(
                        DialogueOpenIntent.CreateRpg(selPawn, targetPawn, selPawn.Map),
                        out _);
                    return;
                }

                Job dialogueJob = JobMaker.MakeJob(dialogueJobDef, targetPawn);
                dialogueJob.playerForced = true;

                if (selPawn.jobs?.curJob != null)
                {
                    selPawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }

                selPawn.jobs.TryTakeOrderedJob(dialogueJob, JobTag.Misc);
            }, MenuOptionPriority.Low);

            // Group chat option: detect nearby pawns around the right-clicked pawn
            List<Pawn> nearbyPawns = DetectNearbyPawns(targetPawn, selPawn);
            if (nearbyPawns.Count >= 1)
            {
                int maxNpcSlots = Math.Min(6, 1 + nearbyPawns.Count);
                List<Pawn> groupParticipants = new List<Pawn> { targetPawn };
                groupParticipants.AddRange(nearbyPawns.Take(maxNpcSlots - 1));

                string groupLabel = "RimChat_GroupConverse".Translate()
                    + " (" + string.Join(", ", groupParticipants.Select(p => p.LabelShort)) + ")";

                List<Pawn> captured = new List<Pawn>(groupParticipants);
                yield return new FloatMenuOption(groupLabel, () =>
                {
                    RimChatTrackedEntityRegistry.TrackPawn(selPawn);
                    foreach (Pawn p in captured)
                        RimChatTrackedEntityRegistry.TrackPawn(p);
                    DialogueWindowCoordinator.TryOpen(
                        DialogueOpenIntent.CreateRpgGroup(selPawn, captured, selPawn.Map),
                        out _);
                });
            }
        }

        /// <summary>
        /// Create a disabled (greyed-out) float menu option with a reason.
        /// </summary>
        private static FloatMenuOption DisabledOption(string reasonKey)
        {
            string label = "RimChat_Converse_Disabled".Translate(reasonKey.Translate());
            return new FloatMenuOption(label, null);
        }

        private static List<Pawn> DetectNearbyPawns(Pawn center, Pawn excludePawn)
        {
            const float BaseRadius = 12f;
            const float HalfRadius = 6f;
            var excluded = new HashSet<Pawn> { center, excludePawn };
            var result = new List<Pawn>();

            Map map = center.Map;
            if (map == null) return result;

            foreach (Pawn candidate in map.mapPawns.AllPawnsSpawned)
            {
                if (excluded.Contains(candidate)) continue;
                if (candidate.Dead || candidate.Destroyed || !candidate.Spawned) continue;
                // Animal/Mechanoid: half detection range
                float radius = (candidate.RaceProps?.Animal == true || candidate.RaceProps?.IsMechanoid == true)
                    ? HalfRadius : BaseRadius;
                if (center.Position.DistanceTo(candidate.Position) > radius) continue;
                if (!PawnDialogueRoutingPolicy.IsRpgDialogueEligibleRace(candidate)) continue;
                if (!center.CanReach(candidate, PathEndMode.InteractionCell, Danger.Deadly)) continue;
                if (!RestUtility.Awake(candidate) || candidate.Downed) continue;
                if (PawnCombatStateUtility.IsPawnInCombat(candidate)) continue;
                if (PawnCombatStateUtility.IsPawnDrafted(candidate)) continue;
                result.Add(candidate);
            }

            result.Sort((a, b) =>
                center.Position.DistanceToSquared(a.Position)
                    .CompareTo(center.Position.DistanceToSquared(b.Position)));
            return result;
        }

    }

    public class CompProperties_PawnDialogue : CompProperties
    {
        public CompProperties_PawnDialogue()
        {
            this.compClass = typeof(CompPawnDialogue);
        }
    }
}
