using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.AI;
using RimChat.Util;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.Rpg
{
    public static class RpgSkillCheckService
    {
        public static RpgSkillCheckPreview BuildPreview(RpgDialogueChoice choice, Pawn initiator, Pawn target)
        {
            if (choice?.Check == null || choice.Check.Approach == RpgSkillCheckApproach.None || initiator == null || target == null)
            {
                return null;
            }

            ResolveSkill(choice.Check.Approach, initiator, out SkillDef skill, out int level, out string skillLabel);
            int skillBonus = level - 10;
            int statusBonus = CalculateStatusBonus(choice.Check.Approach, initiator);
            int contextBonus = CalculateContextBonus(choice.Check.Approach, initiator, target) +
                CalculateEffectContextBonus(choice.Success?.Effects, initiator, target);
            int difficulty = CalculateDifficulty(choice.Success?.Effects, target);
            int totalBonus = skillBonus + statusBonus + contextBonus;
            float successChance = CalculateSuccessChance(totalBonus, difficulty);

            return new RpgSkillCheckPreview
            {
                Approach = choice.Check.Approach,
                SkillLabel = skillLabel,
                SkillLevel = level,
                SkillBonus = skillBonus,
                StatusBonus = statusBonus,
                ContextBonus = contextBonus,
                Difficulty = difficulty,
                SuccessChance = successChance,
                Breakdown = "RimChat_RPGChoice_CheckBreakdown".Translate(
                    skill?.LabelCap ?? skillLabel,
                    level,
                    Signed(skillBonus),
                    Signed(statusBonus),
                    Signed(contextBonus),
                    difficulty,
                    (successChance * 100f).ToString("0"))
            };
        }

        public static RpgSkillCheckResult Roll(RpgSkillCheckPreview preview)
        {
            if (preview == null)
            {
                return new RpgSkillCheckResult { Roll = 0, Total = 0, Success = true };
            }

            int roll = Rand.RangeInclusive(1, 20);
            int total = roll + preview.TotalBonus;
            bool naturalOne = roll == 1;
            bool naturalTwenty = roll == 20;
            bool success = IsRollSuccessful(roll, preview.TotalBonus, preview.Difficulty);
            return new RpgSkillCheckResult
            {
                Roll = roll,
                Total = total,
                Success = success,
                NaturalOne = naturalOne,
                NaturalTwenty = naturalTwenty,
                Preview = preview
            };
        }

        public static float CalculateSuccessChance(int totalBonus, int difficulty)
        {
            int successCount = 0;
            for (int roll = 1; roll <= 20; roll++)
            {
                if (IsRollSuccessful(roll, totalBonus, difficulty))
                {
                    successCount++;
                }
            }

            return successCount / 20f;
        }

        public static bool IsRollSuccessful(int roll, int totalBonus, int difficulty)
        {
            int boundedRoll = Mathf.Clamp(roll, 1, 20);
            return boundedRoll == 20 || (boundedRoll != 1 && boundedRoll + totalBonus >= difficulty);
        }

        public static int CalculateDifficulty(IEnumerable<LLMRpgApiResponse.ApiAction> effects, Pawn target)
        {
            List<LLMRpgApiResponse.ApiAction> uncertain = (effects ?? Enumerable.Empty<LLMRpgApiResponse.ApiAction>())
                .Where(RpgChoiceEffectService.IsUncertain)
                .ToList();
            int difficulty = uncertain.Count == 0 ? 10 : uncertain.Max(RpgChoiceEffectService.GetBaseDifficulty);
            if (uncertain.Count > 1)
            {
                difficulty += (uncertain.Count - 1) * 2;
            }

            foreach (LLMRpgApiResponse.ApiAction effect in uncertain)
            {
                switch (RpgChoiceEffectService.NormalizeActionName(effect?.action))
                {
                    case "Recruit":
                        if (target?.guest != null && target.IsPrisoner)
                            difficulty += Mathf.Clamp(Mathf.RoundToInt(target.guest.resistance / 5f), 0, 8);
                        break;
                    case "ConvertIdeology":
                        if (target?.ideo != null)
                            difficulty += Mathf.Clamp(Mathf.RoundToInt(target.ideo.Certainty * 4f), 0, 4);
                        break;
                    case "AdjustCertainty":
                        if (target?.ideo != null)
                            difficulty += Mathf.Clamp(Mathf.RoundToInt(target.ideo.Certainty * 2f), 0, 2);
                        break;
                }
            }

            return Mathf.Clamp(difficulty, 5, 30);
        }

        public static string GetApproachLabel(RpgSkillCheckApproach approach)
        {
            switch (approach)
            {
                case RpgSkillCheckApproach.Persuasion: return "RimChat_RPGChoice_ApproachPersuasion".Translate();
                case RpgSkillCheckApproach.Reason: return "RimChat_RPGChoice_ApproachReason".Translate();
                case RpgSkillCheckApproach.Intimidation: return "RimChat_RPGChoice_ApproachIntimidation".Translate();
                case RpgSkillCheckApproach.Care: return "RimChat_RPGChoice_ApproachCare".Translate();
                default: return "RimChat_RPGChoice_NoCheck".Translate();
            }
        }

        private static void ResolveSkill(RpgSkillCheckApproach approach, Pawn pawn, out SkillDef skill, out int level, out string label)
        {
            switch (approach)
            {
                case RpgSkillCheckApproach.Reason:
                    skill = SkillDefOf.Intellectual;
                    break;
                case RpgSkillCheckApproach.Intimidation:
                    int melee = GetSkillLevel(pawn, SkillDefOf.Melee);
                    int shooting = GetSkillLevel(pawn, SkillDefOf.Shooting);
                    skill = melee >= shooting ? SkillDefOf.Melee : SkillDefOf.Shooting;
                    break;
                case RpgSkillCheckApproach.Care:
                    skill = SkillDefOf.Medicine;
                    break;
                default:
                    skill = SkillDefOf.Social;
                    break;
            }

            level = GetSkillLevel(pawn, skill);
            label = skill?.LabelCap ?? approach.ToString();
        }

        private static int GetSkillLevel(Pawn pawn, SkillDef skill)
        {
            return Mathf.Clamp(pawn?.skills?.GetSkill(skill)?.Level ?? 0, 0, 20);
        }

        private static int CalculateStatusBonus(RpgSkillCheckApproach approach, Pawn initiator)
        {
            int result = 0;
            if (approach == RpgSkillCheckApproach.Persuasion)
            {
                float socialImpact = initiator.GetStatValue(StatDefOf.SocialImpact, true, -1);
                result += Mathf.Clamp(Mathf.RoundToInt((socialImpact - 1f) * 3f), -3, 3);
                float talking = initiator.health?.capacities?.GetLevel(PawnCapacityDefOf.Talking) ?? 1f;
                // A healthy speaker contributes +1, while impaired speech quickly becomes a penalty.
                result += Mathf.Clamp(Mathf.RoundToInt((talking - 0.8f) * 5f), -5, 1);
            }
            return Mathf.Clamp(result, -6, 3);
        }

        private static int CalculateContextBonus(RpgSkillCheckApproach approach, Pawn initiator, Pawn target)
        {
            int opinion = target.relations?.OpinionOf(initiator) ?? 0;
            int opinionBonus = Mathf.Clamp(Mathf.RoundToInt(opinion / 20f), -5, 5);
            switch (approach)
            {
                case RpgSkillCheckApproach.Reason:
                    return Mathf.Clamp(opinionBonus + CalculateRelationshipBonus(initiator, target), -7, 7);
                case RpgSkillCheckApproach.Intimidation:
                    int initiatorCombat = Math.Max(GetSkillLevel(initiator, SkillDefOf.Melee), GetSkillLevel(initiator, SkillDefOf.Shooting));
                    int targetCombat = Math.Max(GetSkillLevel(target, SkillDefOf.Melee), GetSkillLevel(target, SkillDefOf.Shooting));
                    return Mathf.Clamp(Mathf.RoundToInt((initiatorCombat - targetCombat) / 4f), -5, 5);
                case RpgSkillCheckApproach.Care:
                    float pain = target.health?.hediffSet?.PainTotal ?? 0f;
                    int injuryCount = target.health?.hediffSet?.hediffs?.Count(hediff => hediff is Hediff_Injury) ?? 0;
                    return Mathf.Clamp(opinionBonus + Mathf.RoundToInt(pain * 3f) + Mathf.Clamp(injuryCount, 0, 3), -5, 8);
                default:
                    return opinionBonus;
            }
        }

        private static int CalculateEffectContextBonus(IEnumerable<LLMRpgApiResponse.ApiAction> effects, Pawn initiator, Pawn target)
        {
            int bonus = 0;
            foreach (LLMRpgApiResponse.ApiAction effect in effects ?? Enumerable.Empty<LLMRpgApiResponse.ApiAction>())
            {
                switch (RpgChoiceEffectService.NormalizeActionName(effect?.action))
                {
                    case "RomanceAttempt":
                    case "Date":
                    case "MarriageProposal":
                        float compatibility = initiator.relations?.CompatibilityWith(target) ?? 1f;
                        bonus += Mathf.Clamp(Mathf.RoundToInt((compatibility - 1f) * 3f), -3, 3);
                        bonus += CalculateRelationshipBonus(initiator, target);
                        break;
                    case "ConvertIdeology":
                    case "AdjustCertainty":
                        if (DLCCompatibility.IsIdeologyActive)
                        {
                            float lossFactor = target.GetStatValue(StatDefOf.CertaintyLossFactor, true, -1);
                            bonus += Mathf.Clamp(Mathf.RoundToInt((lossFactor - 1f) * 3f), -3, 3);
                        }
                        break;
                }
            }
            return Mathf.Clamp(bonus, -6, 6);
        }

        private static int CalculateRelationshipBonus(Pawn initiator, Pawn target)
        {
            if (initiator?.relations == null || target == null) return 0;
            if (initiator.relations.DirectRelationExists(PawnRelationDefOf.Spouse, target)) return 3;
            if (initiator.relations.DirectRelationExists(PawnRelationDefOf.Fiance, target)) return 2;
            if (initiator.relations.DirectRelationExists(PawnRelationDefOf.Lover, target)) return 2;
            if (initiator.relations.DirectRelationExists(PawnRelationDefOf.ExSpouse, target)) return -3;
            if (initiator.relations.DirectRelationExists(PawnRelationDefOf.ExLover, target)) return -2;
            return 0;
        }

        private static string Signed(int value)
        {
            return value >= 0 ? "+" + value : value.ToString();
        }
    }
}
