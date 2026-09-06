using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Memory;
using RimChat.Util;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimChat.Rpg
{
    public sealed class RpgChoiceExecutionResult
    {
        public bool Success { get; set; }
        public List<string> AppliedEffects { get; } = new List<string>();
        public string FailureReason { get; set; }

        public string ToHistoryText()
        {
            if (!Success)
            {
                return "RimChat_RPGChoice_EffectFailed".Translate(FailureReason ?? "unknown");
            }

            return AppliedEffects.Count == 0
                ? "RimChat_RPGChoice_NoImmediateEffect".Translate().ToString()
                : string.Join("; ", AppliedEffects);
        }
    }

    public static class RpgChoiceEffectService
    {
        private static readonly HashSet<string> HighImpactActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RomanceAttempt", "MarriageProposal", "Breakup", "Divorce", "Date", "Recruit",
            "TryTakeOrderedJob", "TriggerIncident", "ConvertIdeology"
        };

        private static readonly HashSet<string> UncertainActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TryGainMemory", "TryAffectSocialGoodwill", "ReduceResistance", "ReduceWill", "RomanceAttempt",
            "MarriageProposal", "Date", "Recruit", "TryTakeOrderedJob", "TriggerIncident", "GrantInspiration",
            "ConvertIdeology", "AdjustCertainty"
        };

        public static string NormalizeActionName(string actionName)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                return null;
            }

            string n = actionName.Trim().Replace("-", "_").ToLowerInvariant();
            switch (n)
            {
                case "romanceattempt": case "romance_attempt": case "romance": return "RomanceAttempt";
                case "marriageproposal": case "marriage_proposal": case "propose_marriage": return "MarriageProposal";
                case "breakup": case "break_up": return "Breakup";
                case "divorce": return "Divorce";
                case "date": case "dating": return "Date";
                case "trygainmemory": case "try_gain_memory": return "TryGainMemory";
                case "tryaffectsocialgoodwill": case "try_affect_social_goodwill": return "TryAffectSocialGoodwill";
                case "reduceresistance": case "reduce_resistance": return "ReduceResistance";
                case "reducewill": case "reduce_will": return "ReduceWill";
                case "recruit": return "Recruit";
                case "trytakeorderedjob": case "try_take_ordered_job": return "TryTakeOrderedJob";
                case "triggerincident": case "trigger_incident": return "TriggerIncident";
                case "grantinspiration": case "grant_inspiration": return "GrantInspiration";
                case "convertideology": case "convert_ideology": return "ConvertIdeology";
                case "adjustcertainty": case "adjust_certainty": return "AdjustCertainty";
                case "exitdialogue": case "exit_dialogue": return "ExitDialogue";
                case "exitdialoguecooldown": case "exit_dialogue_cooldown": return "ExitDialogueCooldown";
                default: return actionName.Trim();
            }
        }

        public static bool IsHighImpact(LLMRpgApiResponse.ApiAction action)
        {
            return HighImpactActions.Contains(NormalizeActionName(action?.action) ?? string.Empty);
        }

        public static bool IsUncertain(LLMRpgApiResponse.ApiAction action)
        {
            return UncertainActions.Contains(NormalizeActionName(action?.action) ?? string.Empty);
        }

        public static int GetBaseDifficulty(LLMRpgApiResponse.ApiAction action)
        {
            switch (NormalizeActionName(action?.action))
            {
                case "TryGainMemory":
                case "TryAffectSocialGoodwill":
                    return 10;
                case "ReduceResistance":
                case "ReduceWill":
                case "AdjustCertainty":
                case "GrantInspiration":
                    return 14;
                case "RomanceAttempt":
                case "Date":
                case "Recruit":
                case "TryTakeOrderedJob":
                    return 16;
                case "MarriageProposal":
                case "ConvertIdeology":
                case "TriggerIncident":
                    return 20;
                default:
                    return 10;
            }
        }

        public static bool IsApproachAllowed(RpgSkillCheckApproach approach, IEnumerable<LLMRpgApiResponse.ApiAction> effects)
        {
            // The approach describes how the line is delivered, not which effect may result.
            // Effects have their own allowlist, target, magnitude and high-impact validation.
            // Binding effects to approaches here incorrectly rejected valid combinations such
            // as a reasoned appeal that creates a memory or an intimidating recruitment offer.
            return approach == RpgSkillCheckApproach.Persuasion ||
                approach == RpgSkillCheckApproach.Reason ||
                approach == RpgSkillCheckApproach.Intimidation ||
                approach == RpgSkillCheckApproach.Care;
        }

        public static bool TryValidateChoice(RpgDialogueChoice choice, Pawn initiator, Pawn target, bool failureOnly, out string reason)
        {
            reason = string.Empty;
            if (choice == null || string.IsNullOrWhiteSpace(choice.Text))
            {
                reason = "empty_choice";
                return false;
            }
            if (initiator == null || target == null || initiator.Dead || target.Dead || initiator.Destroyed || target.Destroyed)
            {
                reason = "invalid_target";
                return false;
            }

            RpgDialogueChoiceOutcome outcome = failureOnly ? choice.Failure : choice.Success;
            List<LLMRpgApiResponse.ApiAction> effects = outcome?.Effects ?? new List<LLMRpgApiResponse.ApiAction>();
            int maxEffects = failureOnly ? 1 : 3;
            if (effects.Count > maxEffects)
            {
                reason = failureOnly ? "too_many_failure_effects" : "too_many_success_effects";
                return false;
            }

            if (!failureOnly && effects.Count(IsHighImpact) > 1)
            {
                reason = "too_many_high_impact_effects";
                return false;
            }

            if (!failureOnly && effects.Any(IsUncertain) && !choice.RequiresCheck)
            {
                reason = "missing_required_check";
                return false;
            }

            foreach (LLMRpgApiResponse.ApiAction effect in effects)
            {
                if (failureOnly && !IsAllowedFailureEffect(effect))
                {
                    reason = "unsafe_failure_effect";
                    return false;
                }

                if (!TryValidateEffect(effect, initiator, target, out reason))
                {
                    return false;
                }
            }

            if (!failureOnly && choice.RequiresCheck && !IsApproachAllowed(choice.Check.Approach, effects))
            {
                reason = "approach_not_allowed";
                return false;
            }

            return true;
        }

        public static bool TryValidateEffect(LLMRpgApiResponse.ApiAction action, Pawn initiator, Pawn target, out string reason)
        {
            reason = string.Empty;
            if (initiator == null || target == null || initiator.Dead || target.Dead || initiator.Destroyed || target.Destroyed)
            {
                reason = "invalid_target";
                return false;
            }

            switch (NormalizeActionName(action?.action))
            {
                case "TryGainMemory":
                    if (target.needs?.mood?.thoughts?.memories == null || RpgMemoryCatalog.ResolveRequestedThoughtDef(action?.defName ?? string.Empty, out _) == null)
                    { reason = "invalid_memory"; return false; }
                    return true;
                case "TryAffectSocialGoodwill":
                    if (target.Faction == null || initiator.Faction == null || action.amount == 0)
                    { reason = "invalid_goodwill"; return false; }
                    return true;
                case "ReduceResistance":
                    if (!target.IsPrisoner || target.guest == null || action.amount <= 0)
                    { reason = "invalid_resistance_target"; return false; }
                    return true;
                case "ReduceWill":
                    if (!target.IsPrisoner || target.guest == null || action.amount <= 0)
                    { reason = "invalid_will_target"; return false; }
                    return true;
                case "RomanceAttempt":
                case "Date":
                    if (target == initiator || target.relations == null || initiator.relations == null)
                    { reason = "invalid_relationship_target"; return false; }
                    if (HasPairRelation(target, initiator, PawnRelationDefOf.Lover) ||
                        HasPairRelation(target, initiator, PawnRelationDefOf.Fiance) ||
                        HasPairRelation(target, initiator, PawnRelationDefOf.Spouse))
                    { reason = "relationship_already_established"; return false; }
                    return true;
                case "MarriageProposal":
                    if (target == initiator || target.relations == null || initiator.relations == null)
                    { reason = "invalid_relationship_target"; return false; }
                    if (!HasPairRelation(target, initiator, PawnRelationDefOf.Lover) && !HasPairRelation(target, initiator, PawnRelationDefOf.Fiance))
                    { reason = "marriage_requires_relationship"; return false; }
                    return true;
                case "Breakup":
                    if (!HasPairRelation(target, initiator, PawnRelationDefOf.Lover) && !HasPairRelation(target, initiator, PawnRelationDefOf.Fiance))
                    { reason = "no_relationship_to_end"; return false; }
                    return true;
                case "Divorce":
                    if (!HasPairRelation(target, initiator, PawnRelationDefOf.Spouse))
                    { reason = "not_married"; return false; }
                    return true;
                case "Recruit":
                    if (initiator.Faction == null || target.Faction == initiator.Faction)
                    { reason = "invalid_recruit_target"; return false; }
                    return true;
                case "TryTakeOrderedJob":
                    if (!string.Equals(action?.defName, "AttackMelee", StringComparison.OrdinalIgnoreCase) || target.jobs == null)
                    { reason = "unsupported_job"; return false; }
                    return true;
                case "TriggerIncident":
                    if (DefDatabase<IncidentDef>.GetNamedSilentFail(action?.defName) == null || (target.MapHeld ?? Find.CurrentMap) == null)
                    { reason = "invalid_incident"; return false; }
                    return true;
                case "GrantInspiration":
                    if (target.mindState?.inspirationHandler == null)
                    { reason = "invalid_inspiration_target"; return false; }
                    return true;
                case "ConvertIdeology":
                case "AdjustCertainty":
                    if (!DLCCompatibility.IsIdeologyActive || target.ideo == null || initiator.ideo?.Ideo == null)
                    { reason = "ideology_unavailable"; return false; }
                    return true;
                case "ExitDialogue":
                case "ExitDialogueCooldown":
                    return true;
                default:
                    reason = "unknown_action";
                    return false;
            }
        }

        public static RpgChoiceExecutionResult Execute(RpgDialogueChoiceOutcome outcome, Pawn initiator, Pawn target)
        {
            var result = new RpgChoiceExecutionResult();
            List<LLMRpgApiResponse.ApiAction> effects = outcome?.Effects ?? new List<LLMRpgApiResponse.ApiAction>();
            foreach (LLMRpgApiResponse.ApiAction effect in effects)
            {
                if (!TryValidateEffect(effect, initiator, target, out string validationReason))
                {
                    result.FailureReason = validationReason;
                    return result;
                }
            }

            IEnumerable<LLMRpgApiResponse.ApiAction> ordered = effects.OrderBy(effect => IsHighImpact(effect) ? 1 : 0);
            foreach (LLMRpgApiResponse.ApiAction effect in ordered)
            {
                if (!ExecuteEffect(effect, initiator, target, out string applied, out string failure))
                {
                    result.FailureReason = failure;
                    return result;
                }

                if (!string.IsNullOrWhiteSpace(applied))
                {
                    result.AppliedEffects.Add(applied);
                }
            }

            result.Success = true;
            return result;
        }

        public static string Describe(LLMRpgApiResponse.ApiAction action, Pawn target)
        {
            string name = NormalizeActionName(action?.action);
            switch (name)
            {
                case "TryGainMemory":
                    ThoughtDef memory = RpgMemoryCatalog.ResolveRequestedThoughtDef(action?.defName ?? string.Empty, out _);
                    float mood = memory?.stages?.FirstOrDefault()?.baseMoodEffect ?? 0f;
                    return "RimChat_RPGChoice_EffectMemory".Translate(memory?.LabelCap ?? action?.defName ?? "?", Signed(mood));
                case "TryAffectSocialGoodwill": return "RimChat_RPGChoice_EffectGoodwill".Translate(Signed(action.amount));
                case "ReduceResistance": return "RimChat_RPGChoice_EffectResistance".Translate(Mathf.Clamp(action.amount, 1, 10));
                case "ReduceWill": return "RimChat_RPGChoice_EffectWill".Translate(Mathf.Clamp(action.amount, 1, 10));
                case "RomanceAttempt": return "RimChat_RPGChoice_EffectRomance".Translate();
                case "MarriageProposal": return "RimChat_RPGChoice_EffectMarriage".Translate();
                case "Breakup": return "RimChat_RPGChoice_EffectBreakup".Translate();
                case "Divorce": return "RimChat_RPGChoice_EffectDivorce".Translate();
                case "Date": return "RimChat_RPGChoice_EffectDate".Translate();
                case "Recruit": return "RimChat_RPGChoice_EffectRecruit".Translate(target?.LabelShort ?? "?");
                case "TryTakeOrderedJob": return "RimChat_RPGChoice_EffectJob".Translate(action?.defName ?? "?");
                case "TriggerIncident": return "RimChat_RPGChoice_EffectIncident".Translate(action?.defName ?? "?");
                case "GrantInspiration": return "RimChat_RPGChoice_EffectInspiration".Translate();
                case "ConvertIdeology": return "RimChat_RPGChoice_EffectConvertIdeology".Translate();
                case "AdjustCertainty": return "RimChat_RPGChoice_EffectCertainty".Translate(Signed(ResolveCertaintyDelta(action)));
                default: return name ?? "?";
            }
        }

        private static bool IsAllowedFailureEffect(LLMRpgApiResponse.ApiAction action)
        {
            string name = NormalizeActionName(action?.action);
            if (name == "TryAffectSocialGoodwill")
            {
                return action.amount >= -5 && action.amount <= -1;
            }

            if (name != "TryGainMemory")
            {
                return false;
            }

            ThoughtDef def = RpgMemoryCatalog.ResolveRequestedThoughtDef(action?.defName ?? string.Empty, out _);
            return (def?.stages?.FirstOrDefault()?.baseMoodEffect ?? 0f) < 0f;
        }

        private static bool ExecuteEffect(LLMRpgApiResponse.ApiAction action, Pawn initiator, Pawn target, out string applied, out string failure)
        {
            applied = Describe(action, target);
            failure = string.Empty;
            string name = NormalizeActionName(action?.action);
            try
            {
                switch (name)
                {
                    case "TryGainMemory":
                        ThoughtDef memory = RpgMemoryCatalog.ResolveRequestedThoughtDef(action?.defName ?? string.Empty, out _);
                        target.needs.mood.thoughts.memories.TryGainMemory(memory, initiator);
                        return true;
                    case "TryAffectSocialGoodwill":
                        target.Faction.TryAffectGoodwillWith(initiator.Faction, Mathf.Clamp(action.amount, -15, 15), true, true, null);
                        return true;
                    case "ReduceResistance":
                        target.guest.resistance = Mathf.Max(0f, target.guest.resistance - Mathf.Clamp(action.amount, 1, 10));
                        return true;
                    case "ReduceWill":
                        target.guest.will = Mathf.Max(0f, target.guest.will - Mathf.Clamp(action.amount, 1, 10));
                        return true;
                    case "RomanceAttempt":
                    case "Date":
                        RemovePairRelation(target, initiator, PawnRelationDefOf.ExLover);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.ExSpouse);
                        AddPairRelation(target, initiator, PawnRelationDefOf.Lover);
                        return true;
                    case "MarriageProposal":
                        RemovePairRelation(target, initiator, PawnRelationDefOf.ExSpouse);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.ExLover);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Fiance);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Lover);
                        AddPairRelation(target, initiator, PawnRelationDefOf.Spouse);
                        return true;
                    case "Breakup":
                        bool married = HasPairRelation(target, initiator, PawnRelationDefOf.Spouse);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Spouse);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Fiance);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Lover);
                        AddPairRelation(target, initiator, married ? PawnRelationDefOf.ExSpouse : PawnRelationDefOf.ExLover);
                        return true;
                    case "Divorce":
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Spouse);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Fiance);
                        RemovePairRelation(target, initiator, PawnRelationDefOf.Lover);
                        AddPairRelation(target, initiator, PawnRelationDefOf.ExSpouse);
                        return true;
                    case "Recruit":
                        RecruitUtility.Recruit(target, initiator.Faction, initiator);
                        return true;
                    case "TryTakeOrderedJob":
                        return target.jobs.TryTakeOrderedJob(new Job(JobDefOf.AttackMelee, initiator), JobTag.Misc);
                    case "TriggerIncident":
                        IncidentDef incident = DefDatabase<IncidentDef>.GetNamedSilentFail(action?.defName);
                        Map map = target.MapHeld ?? Find.CurrentMap;
                        IncidentParms parms = StorytellerUtility.DefaultParmsNow(incident.category, map);
                        parms.faction = target.Faction;
                        if (action.amount > 0) parms.points = action.amount;
                        if (!incident.Worker.TryExecute(parms)) { failure = "incident_rejected"; return false; }
                        return true;
                    case "GrantInspiration":
                        InspirationDef inspiration = DefDatabase<InspirationDef>.GetNamedSilentFail(action?.defName)
                            ?? DefDatabase<InspirationDef>.AllDefsListForReading.FirstOrDefault();
                        if (inspiration == null || !target.mindState.inspirationHandler.TryStartInspiration(inspiration))
                        { failure = "inspiration_rejected"; return false; }
                        return true;
                    case "ConvertIdeology":
                        target.ideo.SetIdeo(initiator.ideo.Ideo);
                        return true;
                    case "AdjustCertainty":
                        target.ideo.OffsetCertainty(ResolveCertaintyDelta(action));
                        return true;
                    case "ExitDialogue":
                    case "ExitDialogueCooldown":
                        return true;
                    default:
                        failure = "unknown_action";
                        return false;
                }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
                Log.Error($"[RimChat] Choice effect {name} failed: {ex}");
                return false;
            }
        }

        private static float ResolveCertaintyDelta(LLMRpgApiResponse.ApiAction action)
        {
            if (Math.Abs(action?.value ?? 0f) > 0.0001f) return Mathf.Clamp(action.value, -1f, 1f);
            int amount = action?.amount ?? 0;
            if (amount == 0) return -0.15f;
            return Mathf.Clamp(Math.Abs(amount) > 1 ? amount / 100f : amount, -1f, 1f);
        }

        private static bool HasPairRelation(Pawn a, Pawn b, PawnRelationDef def)
        {
            return a?.relations != null && b != null && def != null && a.relations.DirectRelationExists(def, b);
        }

        private static void RemovePairRelation(Pawn a, Pawn b, PawnRelationDef def)
        {
            if (HasPairRelation(a, b, def)) a.relations.RemoveDirectRelation(def, b);
        }

        private static void AddPairRelation(Pawn a, Pawn b, PawnRelationDef def)
        {
            if (a?.relations != null && b != null && def != null && !a.relations.DirectRelationExists(def, b))
                a.relations.AddDirectRelation(def, b);
        }

        private static string Signed(float value)
        {
            return value >= 0f ? "+" + value.ToString("0.##") : value.ToString("0.##");
        }
    }
}
