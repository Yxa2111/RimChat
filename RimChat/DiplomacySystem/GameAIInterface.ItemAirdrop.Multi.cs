using System;
using System.Collections.Generic;
using System.Linq;
using RimChat.Config;
using RimChat.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.DiplomacySystem
{
    public partial class GameAIInterface
    {
        private APIResult PrepareMultiItemAirdropTradeForMap(
            Faction faction,
            Dictionary<string, object> parameters,
            Map map,
            bool requirePlayerHome,
            Pawn playerNegotiator)
        {
            RimChatSettings settings = RimChatMod.Instance?.InstanceSettings;
            if (settings == null) return APIResult.FailureResult("Settings not initialized");
            if (!settings.EnableAIItemAirdrop) return APIResult.FailureResult("request_item_airdrop is disabled in settings.");
            if (faction == null) return APIResult.FailureResult("Faction cannot be null");
            if (map == null) return FailFastAirdrop("no_home_map", "No player map available for item airdrop.", faction, parameters, sendLetter: false);
            if (requirePlayerHome && !map.IsPlayerHome)
                return FailFastAirdrop("map_not_player_home", "Barter airdrop requires a player home map context.", faction, parameters, sendLetter: false);

            if (!ItemAirdropBasket.TryRead(parameters, "need_items", out List<ItemAirdropTradeLine> needs))
                return FailFastAirdrop("need_items_invalid", "need_items must contain 1-32 rows with exact item defName and a positive count.", faction, parameters, sendLetter: false);
            if (!ItemAirdropBasket.TryRead(parameters, "payment_items", out List<ItemAirdropTradeLine> payments))
                return FailFastAirdrop("payment_items_invalid", "payment_items must contain 1-32 rows with exact item defName and a positive count.", faction, parameters, sendLetter: false);

            HashSet<string> blacklist = ParseCsv(settings.ItemAirdropBlacklistDefNamesCsv);
            HashSet<string> blockedCategories = ItemAirdropSafetyPolicy.ParseBlockedCategories(settings.ItemAirdropBlockedCategoriesCsv);
            TechLevel factionTech = faction.def?.techLevel ?? TechLevel.Archotech;
            int needTotal = 0;
            int podCount = 0;
            foreach (ItemAirdropTradeLine line in needs)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(line.DefName);
                ThingDefRecord record = def == null ? null : ThingDefRecord.From(def);
                if (record?.Def == null || def.category != ThingCategory.Item || def.IsCorpse)
                    return FailFastAirdrop("need_def_unresolved", $"Delivery item def '{line.DefName}' is unavailable.", faction, parameters, sendLetter: false);
                if (blacklist.Contains(def.defName) || ItemAirdropSafetyPolicy.IsBlockedByCategory(record, blockedCategories))
                    return FailFastAirdrop("need_item_blocked", $"Delivery item '{def.defName}' is blocked by airdrop policy.", faction, parameters, sendLetter: false);
                if (def.techLevel != TechLevel.Undefined && def.techLevel != 0 && def.techLevel > factionTech)
                    return FailFastAirdrop("need_item_tech_unavailable", $"Faction cannot supply '{def.defName}' at its current tech level.", faction, parameters, sendLetter: false);

                SpecialItemType? specialType = null;
                if (FactionSpecialItemsManager.Instance.TryMatchSpecialItem(faction, def.defName, out SpecialItemType matched)) specialType = matched;
                float unitPrice = ResolveAirdropNeedQuotedUnitPrice(record, faction, playerNegotiator, map, null, specialType);
                line.DefName = def.defName;
                line.Label = record.Label;
                line.UnitPrice = unitPrice;
                long subtotal = (long)Mathf.RoundToInt(Math.Max(0f, unitPrice) * line.Count);
                if (subtotal > int.MaxValue || (long)needTotal + subtotal > int.MaxValue)
                    return FailFastAirdrop("need_total_overflow", "Delivery basket value is too large.", faction, parameters, sendLetter: false);
                needTotal += (int)subtotal;
                podCount += ResolveAirdropShippingPodCount(def, line.Count);
            }

            Dictionary<string, object> normalizedParameters = CloneParameterDictionary(parameters);
            normalizedParameters["need_items"] = ItemAirdropBasket.ToParameters(needs);
            normalizedParameters["payment_items"] = ItemAirdropBasket.ToParameters(payments);
            APIResult paymentResult = BuildPaymentPlan(normalizedParameters, map, faction, playerNegotiator,
                out List<ItemAirdropPreparedPaymentLine> paymentLines,
                out List<ItemAirdropDeductionPlanLine> deductionPlan,
                out int budget,
                out int paymentTotal);
            if (!paymentResult.Success)
                return FailFastAirdrop((paymentResult.Data as ItemAirdropResultData)?.FailureCode ?? "payment_plan_failed", paymentResult.Message, faction, parameters);

            AirdropTradeRuleSnapshot rule = ItemAirdropTradePolicy.ResolveRuleSnapshot(
                faction, map.wealthWatcher?.WealthItems ?? 0f, GetAirdropFactionTradeTotal(faction));
            int shipping = podCount * rule.ShippingCostPerPod;
            int required = needTotal + shipping;
            if (paymentTotal < required)
                return FailFastAirdrop("payment_value_insufficient", $"Quoted payment value {paymentTotal} is below delivery plus shipping value {required}.", faction, parameters, sendLetter: false);

            ItemAirdropTradeLine firstNeed = needs[0];
            ItemAirdropPreparedPaymentLine firstPayment = paymentLines.FirstOrDefault();
            var prepared = new ItemAirdropPreparedTradeData
            {
                FactionId = faction.GetUniqueLoadID(),
                NeedText = ItemAirdropBasket.Summary(needs),
                Scenario = NormalizeScenario(ReadString(parameters, "scenario")),
                SelectedDefName = firstNeed.DefName,
                ResolvedLabel = firstNeed.Label,
                Quantity = needs.Sum(line => line.Count),
                RequestedQuantity = needs.Sum(line => line.Count),
                CountAdjustmentReason = "none",
                BudgetSilver = budget,
                NeedQuotedUnitSilver = needs.Count == 1 ? firstNeed.UnitPrice : 0f,
                PaymentTotalSilver = paymentTotal,
                PaymentItemTotalSilver = paymentTotal,
                ShippingPodCount = podCount,
                ShippingCostSilver = shipping,
                PaymentOverpaySilver = Math.Max(0, paymentTotal - required),
                MapUniqueId = map.uniqueID,
                SelectionReason = "exact_multi_item_quote",
                NeedPriceSemantic = needs.Count == 1 ? ItemAirdropTradePolicy.ResolveNeedPriceSemantic(DefDatabase<ThingDef>.GetNamedSilentFail(firstNeed.DefName), faction) : "mixed",
                PaymentPriceSemantic = paymentLines.Count == 1 ? ItemAirdropTradePolicy.ResolveOfferPriceSemantic(DefDatabase<ThingDef>.GetNamedSilentFail(firstPayment?.DefName)) : "mixed",
                DeliveryLines = ItemAirdropBasket.Copy(needs),
                PaymentLines = paymentLines,
                DeductionPlan = deductionPlan,
                ParametersSnapshot = normalizedParameters
            };
            RecordStageAudit("prepare_trade", faction, parameters,
                $"needs={ItemAirdropBasket.Reference(needs)},payment={ItemAirdropBasket.Reference(payments)},required={required},paid={paymentTotal}");
            return APIResult.SuccessResult("Multi-item airdrop trade prepared.", prepared);
        }

        private APIResult CommitPreparedMultiItemAirdropTrade(Faction faction, ItemAirdropPreparedTradeData prepared)
        {
            Map map = Find.Maps?.FirstOrDefault(m => m != null && m.uniqueID == prepared.MapUniqueId);
            if (map == null) return FailFastAirdrop("map_unavailable", "Prepared airdrop map is no longer available.", faction, prepared.ParametersSnapshot);
            if (!ItemAirdropBasket.TryNormalize(prepared.DeliveryLines, out List<ItemAirdropTradeLine> deliveryLines))
                return FailFastAirdrop("delivery_lines_invalid", "Prepared delivery basket is invalid.", faction, prepared.ParametersSnapshot);
            if (!TryFindAirdropCell(map, out IntVec3 dropCell))
                return FailFastAirdrop(MapUtility.IsOrbitalBaseMap(map) ? "orbital_drop_unavailable" : "dropcell_not_found", "No legal drop cell found near colony center.", faction, prepared.ParametersSnapshot);

            int maxStacks = RimChatMod.Instance?.InstanceSettings?.ItemAirdropMaxStacksPerDrop ?? 8;
            var things = new List<Thing>();
            foreach (ItemAirdropTradeLine line in deliveryLines)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(line.DefName);
                if (def == null) return FailFastAirdrop("selected_def_unresolved", $"Delivery def '{line.DefName}' disappeared before commit.", faction, prepared.ParametersSnapshot);
                List<Thing> stacks = BuildStacks(def, line.Count, maxStacks);
                if (stacks.Sum(thing => thing.stackCount) != line.Count)
                    return FailFastAirdrop("delivery_quantity_mismatch", $"Could not build the exact quantity for '{line.DefName}'.", faction, prepared.ParametersSnapshot);
                things.AddRange(stacks);
            }

            APIResult validation = ValidateDeductionPlan(map, prepared.DeductionPlan, out List<ThingDeductionReservation> reservations);
            if (!validation.Success)
                return FailFastAirdrop((validation.Data as ItemAirdropResultData)?.FailureCode ?? "payment_item_insufficient", validation.Message, faction, prepared.ParametersSnapshot);

            ApplyDeductionReservations(reservations);
            DropPodUtility.DropThingsNear(dropCell, map, things, 110, false, false, false);

            foreach (ItemAirdropTradeLine line in deliveryLines)
                if (FactionSpecialItemsManager.Instance.TryMatchSpecialItem(faction, line.DefName, out SpecialItemType type))
                    FactionSpecialItemsManager.Instance.MarkTraded(faction, type);

            AirdropTradeRuleSnapshot cooldownRule = ItemAirdropTradePolicy.ResolveRuleSnapshot(
                faction, map.wealthWatcher?.WealthItems ?? 0f, GetAirdropFactionTradeTotal(faction));
            float multiplier = Mathf.Clamp((float)prepared.PaymentTotalSilver / Math.Max(1, cooldownRule.TradeLimitSilver), 0.01f, 1f);
            RecordAirdropFactionTradeTotal(faction, prepared.PaymentTotalSilver);
            SetCooldown(faction, "RequestItemAirdrop", multiplier);
            RecordSuccessfulAirdropFaction(faction);

            string deliverySummary = ItemAirdropBasket.Summary(deliveryLines);
            Find.LetterStack.ReceiveLetter("RimChat_ItemAirdropArrivedTitle".Translate(),
                "RimChat_ItemAirdropArrivedMultiBody".Translate(faction.Name, deliverySummary, prepared.PaymentTotalSilver),
                LetterDefOf.PositiveEvent, new TargetInfo(dropCell, map), faction);
            RecordStageAudit("execute", faction, prepared.ParametersSnapshot,
                $"delivery={ItemAirdropBasket.Reference(deliveryLines)},payment={prepared.PaymentTotalSilver},drop={dropCell}");
            RecordAPICall("RequestItemAirdrop", true,
                $"delivery={ItemAirdropBasket.Reference(deliveryLines)},payment={prepared.PaymentTotalSilver},drop={dropCell}");

            var payload = new ItemAirdropResultData
            {
                SelectedDefName = deliveryLines[0].DefName,
                ResolvedLabel = deliverySummary,
                Quantity = deliveryLines.Sum(line => line.Count),
                BudgetUsed = prepared.BudgetSilver,
                ShippingCostSilver = prepared.ShippingCostSilver,
                PaymentTotalSilver = prepared.PaymentTotalSilver,
                DropCell = dropCell.ToString(),
                FailureCode = string.Empty,
                DeliveryLines = ItemAirdropBasket.Copy(deliveryLines)
            };
            return APIResult.SuccessResult($"Airdrop delivered: {deliverySummary}", payload);
        }
    }
}
