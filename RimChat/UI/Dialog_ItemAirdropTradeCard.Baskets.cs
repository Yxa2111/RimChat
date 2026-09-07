using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimChat.DiplomacySystem;
using UnityEngine;
using Verse;

namespace RimChat.UI
{
    public partial class Dialog_ItemAirdropTradeCard
    {
        private void AddCurrentNeedToBasket()
        {
            if (boundNeedRecord?.Def == null) return;
            AddOrMergeBasketLine(needBasket, boundNeedRecord.DefName, boundNeedRecord.Label,
                ParsePositiveInt(requestedCountText, 1), ResolveNeedUnitPrice());
        }

        private void AddCurrentPaymentToBasket()
        {
            if (string.IsNullOrWhiteSpace(selectedOfferDefName)) return;
            AddOrMergeBasketLine(paymentBasket, selectedOfferDefName, selectedOfferLabel,
                ParsePositiveInt(offerCountText, 1), selectedOfferUnitPrice);
        }

        private void AddAllCurrentPaymentToBasket(InventoryDisplayEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.DefName) || entry.Count <= 0) return;

            ApplyOfferSelection(entry);
            int allCount = Math.Min(ItemAirdropBasket.MaxCount, entry.Count);
            offerCountText = allCount.ToString(CultureInfo.InvariantCulture);
            SetBasketLineCount(paymentBasket, entry.DefName, entry.Label, allCount, entry.UnitPrice);
        }

        private static void AddOrMergeBasketLine(List<ItemAirdropTradeLine> basket, string defName, string label, int count, float unitPrice)
        {
            ItemAirdropTradeLine existing = basket.FirstOrDefault(line => string.Equals(line.DefName, defName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (basket.Count >= ItemAirdropBasket.MaxLines) return;
                basket.Add(new ItemAirdropTradeLine { DefName = defName, Label = label, Count = count, UnitPrice = unitPrice });
                return;
            }
            existing.Count = Math.Min(ItemAirdropBasket.MaxCount, existing.Count + count);
            existing.Label = label;
            existing.UnitPrice = unitPrice;
        }

        private static void SetBasketLineCount(List<ItemAirdropTradeLine> basket, string defName, string label, int count, float unitPrice)
        {
            ItemAirdropTradeLine existing = basket.FirstOrDefault(line =>
                string.Equals(line.DefName, defName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (basket.Count >= ItemAirdropBasket.MaxLines) return;
                basket.Add(new ItemAirdropTradeLine { DefName = defName, Label = label, Count = count, UnitPrice = unitPrice });
                return;
            }

            existing.Count = count;
            existing.Label = label;
            existing.UnitPrice = unitPrice;
        }

        private static void DrawBasketAddButton(Rect rect, bool enabled, Action onClick)
        {
            bool previous = GUI.enabled;
            GUI.enabled = enabled;
            if (Widgets.ButtonText(rect, "RimChat_AirdropTradeCard_AddItem".Translate())) onClick?.Invoke();
            GUI.enabled = previous;
        }

        private static void DrawBasketRows(Rect rect, List<ItemAirdropTradeLine> basket, ref Vector2 scrollPosition)
        {
            if (basket == null || basket.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.64f, 0.66f, 0.72f, 0.92f);
                Widgets.Label(rect, "RimChat_AirdropTradeCard_BasketEmpty".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            const float rowHeight = 30f;
            Rect view = new Rect(0f, 0f, Math.Max(1f, rect.width - 16f), basket.Count * rowHeight);
            scrollPosition = GUI.BeginScrollView(rect, scrollPosition, view);
            for (int i = 0; i < basket.Count; i++)
            {
                ItemAirdropTradeLine line = basket[i];
                Rect row = new Rect(0f, i * rowHeight, view.width, rowHeight - 2f);
                if (i % 2 == 0) Widgets.DrawBoxSolid(row, new Color(0.12f, 0.12f, 0.16f, 0.72f));
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(line.DefName);
                Rect iconRect = new Rect(row.x + 4f, row.y + 3f, 22f, 22f);
                if (def?.uiIcon != null)
                {
                    GUI.DrawTexture(iconRect, def.uiIcon);
                }
                Text.Font = GameFont.Tiny;
                string label = string.IsNullOrWhiteSpace(line.Label) ? line.DefName : line.Label;
                Widgets.Label(new Rect(iconRect.xMax + 6f, row.y + 5f, row.width - 116f, 18f),
                    $"{label} x{line.Count}  ({(line.UnitPrice * line.Count).ToString("F0", CultureInfo.InvariantCulture)})");
                Rect remove = new Rect(row.xMax - 26f, row.y + 2f, 24f, 24f);
                if (Widgets.ButtonText(remove, "×"))
                {
                    basket.RemoveAt(i);
                    break;
                }
            }
            GUI.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private static float ComputeBasketMarketValue(IEnumerable<ItemAirdropTradeLine> basket)
        {
            return (basket ?? Enumerable.Empty<ItemAirdropTradeLine>()).Where(line => line != null).Sum(line =>
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(line.DefName);
                return def == null ? 0f : Math.Max(0.01f, def.BaseMarketValue) * Math.Max(0, line.Count);
            });
        }

        private static string FormatMarketValue(float value)
        {
            return Math.Max(0f, value).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static void DrawBasketMarketTotal(Rect rect, List<ItemAirdropTradeLine> basket)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.9f, 0.78f, 0.42f);
            Widgets.Label(rect, "RimChat_AirdropTradeCard_MarketValueTotal".Translate(
                FormatMarketValue(ComputeBasketMarketValue(basket))));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawMarketValueComparisonBlock(Rect rect)
        {
            DrawPanel(rect, new Color(0.11f, 0.11f, 0.15f, 0.96f));
            float needMarketValue = ComputeBasketMarketValue(needBasket);
            float paymentMarketValue = ComputeBasketMarketValue(paymentBasket);
            float difference = paymentMarketValue - needMarketValue;
            string ratio = needMarketValue > 0.0001f
                ? (paymentMarketValue / needMarketValue * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : "—";

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.74f, 0.78f, 0.88f);
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 4f, rect.width - 20f, 14f),
                "RimChat_AirdropTradeCard_MarketComparison".Translate(
                    FormatMarketValue(needMarketValue), FormatMarketValue(paymentMarketValue)));

            GUI.color = difference > 0.005f
                ? new Color(0.62f, 0.85f, 0.62f)
                : difference < -0.005f
                    ? new Color(0.95f, 0.55f, 0.42f)
                    : new Color(0.82f, 0.82f, 0.82f);
            string signedDifference = difference.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 19f, rect.width - 20f, 14f),
                "RimChat_AirdropTradeCard_MarketDifference".Translate(signedDifference, ratio));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }
    }
}
