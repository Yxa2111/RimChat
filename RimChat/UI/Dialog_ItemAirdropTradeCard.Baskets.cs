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

            const float rowHeight = 24f;
            Rect view = new Rect(0f, 0f, Math.Max(1f, rect.width - 16f), basket.Count * rowHeight);
            scrollPosition = GUI.BeginScrollView(rect, scrollPosition, view);
            for (int i = 0; i < basket.Count; i++)
            {
                ItemAirdropTradeLine line = basket[i];
                Rect row = new Rect(0f, i * rowHeight, view.width, rowHeight - 2f);
                if (i % 2 == 0) Widgets.DrawBoxSolid(row, new Color(0.12f, 0.12f, 0.16f, 0.72f));
                Text.Font = GameFont.Tiny;
                string label = string.IsNullOrWhiteSpace(line.Label) ? line.DefName : line.Label;
                Widgets.Label(new Rect(row.x + 5f, row.y + 3f, row.width - 85f, 18f),
                    $"{label} x{line.Count}  ({(line.UnitPrice * line.Count).ToString("F0", CultureInfo.InvariantCulture)})");
                Rect remove = new Rect(row.xMax - 24f, row.y + 1f, 22f, 21f);
                if (Widgets.ButtonText(remove, "×"))
                {
                    basket.RemoveAt(i);
                    break;
                }
            }
            GUI.EndScrollView();
            Text.Font = GameFont.Small;
        }
    }
}
