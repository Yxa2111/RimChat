using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimChat.DiplomacySystem;
using RimChat.Memory;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.UI
{
    public partial class Dialog_ItemAirdropTradeCard : Window
    {
        private readonly FactionDialogueSession session;
        private readonly Faction faction;
        private readonly Action<ItemAirdropTradeCardPayload> onSubmitted;
        private readonly ItemAirdropTradeCardPayload initialPayload;

        private readonly SearchStateManager searchState = new SearchStateManager();
        private readonly List<InventoryDisplayEntry> inventoryItems = new List<InventoryDisplayEntry>();
        private readonly List<InventoryDisplayEntry> filteredInventoryItems = new List<InventoryDisplayEntry>();
        private readonly List<ItemAirdropTradeLine> needBasket = new List<ItemAirdropTradeLine>();
        private readonly List<ItemAirdropTradeLine> paymentBasket = new List<ItemAirdropTradeLine>();
        private List<InventoryDisplayEntry> pendingInventoryItems;

        private string needSearchText = string.Empty;
        private string requestedCountText = "1";
        private string offerCountText = "200";
        private string inventorySearchText = string.Empty;
        private string selectedOfferDefName = string.Empty;
        private string selectedOfferLabel = string.Empty;
        private int selectedOfferStackLimit = 1;
        private float selectedOfferUnitPrice = 1f;
        private string selectedOfferPriceSemantic = "market_value_x0.6";
        private Vector2 inventoryScrollPos = Vector2.zero;
        private Vector2 needBasketScrollPos = Vector2.zero;
        private Vector2 paymentBasketScrollPos = Vector2.zero;
        private ThingDefRecord boundNeedRecord;
        private bool showInlineSuggestions;
        private bool isLoadingInventory;
        private float inventoryLoadProgress;
        private bool inventoryLoadCompleted;

        private const float TitleHeight = 62f;
        private const float SuggestionRowHeight = 38f;
        private const float FooterHeight = 130f;
        private const float Padding = 12f;
        private const float InventoryRowHeight = 46f;
        private const float CardImageSize = 54f;
        public override Vector2 InitialSize => new Vector2(960f, 700f);

        public Dialog_ItemAirdropTradeCard(
            FactionDialogueSession session,
            Faction faction,
            Action<ItemAirdropTradeCardPayload> onSubmitted,
            ItemAirdropTradeCardPayload initialPayload = null)
        {
            this.session = session;
            this.faction = faction;
            this.onSubmitted = onSubmitted;
            this.initialPayload = initialPayload;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            doCloseX = true;
            closeOnAccept = false;
            forcePause = true;
            draggable = true;
            LoadInventoryItemsAsync();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            ApplyPendingInventoryLoadIfReady();
            ApplyInitialPayload();
            EnsureOfferSelectionState();
            EnsureNeedBrowserData();
        }

        private void ApplyInitialPayload()
        {
            if (initialPayload == null)
            {
                return;
            }

            needBasket.Clear();
            paymentBasket.Clear();
            if (ItemAirdropBasket.TryNormalize(initialPayload.NeedItems, out List<ItemAirdropTradeLine> initialNeeds))
                needBasket.AddRange(ItemAirdropBasket.Copy(initialNeeds));
            else if (!string.IsNullOrWhiteSpace(initialPayload.NeedDefName))
                needBasket.Add(new ItemAirdropTradeLine { DefName = initialPayload.NeedDefName, Label = initialPayload.NeedLabel, Count = Math.Max(1, initialPayload.RequestedCount), UnitPrice = initialPayload.NeedUnitPrice });
            if (ItemAirdropBasket.TryNormalize(initialPayload.PaymentItems, out List<ItemAirdropTradeLine> initialPayments))
                paymentBasket.AddRange(ItemAirdropBasket.Copy(initialPayments));
            else if (!string.IsNullOrWhiteSpace(initialPayload.OfferItemDefName))
                paymentBasket.Add(new ItemAirdropTradeLine { DefName = initialPayload.OfferItemDefName, Label = initialPayload.OfferItemLabel, Count = Math.Max(1, initialPayload.OfferItemCount), UnitPrice = initialPayload.OfferUnitPrice });

            requestedCountText = Math.Max(1, initialPayload.RequestedCount).ToString(CultureInfo.InvariantCulture);
            offerCountText = Math.Max(1, initialPayload.OfferItemCount).ToString(CultureInfo.InvariantCulture);
            selectedOfferDefName = initialPayload.OfferItemDefName ?? string.Empty;
            selectedOfferLabel = initialPayload.OfferItemLabel ?? string.Empty;
            needSearchText = initialPayload.NeedSearchText;
            if (string.IsNullOrWhiteSpace(needSearchText))
            {
                needSearchText = initialPayload.NeedLabel ?? initialPayload.NeedDefName ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(initialPayload.NeedDefName))
            {
                ThingDef needDef = DefDatabase<ThingDef>.GetNamedSilentFail(initialPayload.NeedDefName);
                if (needDef != null)
                {
                    BindNeedRecord(ThingDefRecord.From(needDef));
                }
            }
        }

        private void EnsureOfferSelectionState()
        {
            InventoryDisplayEntry selectedEntry = FindInventoryEntryByDefName(selectedOfferDefName);
            if (selectedEntry != null)
            {
                ApplyOfferSelection(selectedEntry);
                return;
            }

            InventoryDisplayEntry fallback = FindInventoryEntryByDefName("Silver") ?? inventoryItems.FirstOrDefault();
            if (fallback == null)
            {
                ClearOfferSelection();
                return;
            }

            ApplyOfferSelection(fallback);
        }

        private AirdropTradeRuleSnapshot ResolveTradeRuleSnapshot()
        {
            return ItemAirdropTradePolicy.ResolveRuleSnapshot(
                faction,
                Find.AnyPlayerHomeMap?.wealthWatcher?.WealthItems ?? 0f,
                GameAIInterface.Instance.GetAirdropFactionTradeTotalForPolicy(faction));
        }

        private void LoadInventoryItemsAsync()
        {
            isLoadingInventory = true;
            inventoryLoadCompleted = false;
            inventoryLoadProgress = 0f;
            TechLevel factionTechLevel = faction?.def?.techLevel ?? TechLevel.Archotech;
            LongEventHandler.QueueLongEvent(() =>
            {
                var loadedItems = new List<InventoryDisplayEntry>();
                Map map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
                if (map != null)
                {
                    List<Thing> tradeables = CollectBeaconTradeableThings(map);
                    inventoryLoadProgress = 0.35f;
                    loadedItems.AddRange(tradeables
                        .Where(thing => thing?.def != null && IsWithinFactionTechLevel(thing.def, factionTechLevel))
                        .GroupBy(thing => thing.def.defName)
                        .Select(group => new InventoryDisplayEntry
                        {
                            DefName = group.Key,
                            Label = group.First().def.label ?? group.Key,
                            Count = group.Sum(thing => Math.Max(0, thing.stackCount)),
                            UnitPrice = ResolveOfferDisplayUnitPrice(group.First().def),
                            StackLimit = Math.Max(1, group.First().def.stackLimit),
                            PriceSemantic = ResolveOfferDisplayPriceSemantic(group.First().def)
                        })
                        .Where(entry => entry.Count > 0)
                        .OrderByDescending(entry => entry.Count)
                        .ThenBy(entry => entry.Label)
                        .ToList());
                }

                inventoryLoadProgress = 0.8f;
                pendingInventoryItems = loadedItems;
                inventoryLoadCompleted = true;
            }, "LoadingInventory", false, null);
        }

        private static bool IsWithinFactionTechLevel(ThingDef def, TechLevel factionTechLevel)
        {
            if (def == null)
            {
                return false;
            }
            // Items with techLevel == 0 (undefined) are always allowed
            if (def.techLevel == TechLevel.Undefined || def.techLevel == 0)
            {
                return true;
            }
            return def.techLevel <= factionTechLevel;
        }

        private float ResolveOfferDisplayUnitPrice(ThingDef def)
        {
            if (def == null)
            {
                return 0.01f;
            }

            if (ItemAirdropTradePolicy.TryResolveOfferUnitPrice(def, out float resolved, out _))
            {
                return Math.Max(0.01f, resolved);
            }

            return Math.Max(0.01f, def.BaseMarketValue);
        }

        private string ResolveOfferDisplayPriceSemantic(ThingDef def)
        {
            if (def == null)
            {
                return string.Empty;
            }

            return ItemAirdropTradePolicy.ResolveOfferPriceSemantic(def);
        }

        private void ApplyInventoryFilter()
        {
            filteredInventoryItems.Clear();
            string normalized = inventorySearchText.Trim().ToLowerInvariant();
            filteredInventoryItems.AddRange(inventoryItems
                .Where(item => selectedOfferBrowserCategory == null ||
                    IsWithinBrowserCategory(DefDatabase<ThingDef>.GetNamedSilentFail(item.DefName), selectedOfferBrowserCategory))
                .Where(item => string.IsNullOrWhiteSpace(normalized) ||
                    item.Label.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.DefName.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static List<Thing> CollectBeaconTradeableThings(Map map)
        {
            return GameAIInterface.CollectBeaconTradeableThingsShared(map);
        }

        private static bool IsValidBeaconPaymentThing(Thing thing)
        {
            return GameAIInterface.IsValidBeaconPaymentThingShared(thing);
        }

        public override void DoWindowContents(Rect inRect)
        {
            ApplyPendingInventoryLoadIfReady();

            float y = inRect.y;
            Rect titleRect = new Rect(inRect.x, y, inRect.width, TitleHeight);
            DrawTitle(titleRect);
            y += TitleHeight + Padding;

            float bodyHeight = inRect.height - (y - inRect.y) - FooterHeight - Padding;
            float cardHeight = Mathf.Clamp(bodyHeight * 0.42f, 170f, 190f);
            Rect cardsRect = new Rect(inRect.x, y, inRect.width, cardHeight);
            DrawItemCards(cardsRect);
            y += cardHeight + Padding;

            Rect browsersRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(170f, bodyHeight - cardHeight - Padding));
            DrawDualItemBrowsers(browsersRect);

            Rect footerRect = new Rect(inRect.x, inRect.yMax - FooterHeight, inRect.width, FooterHeight);
            DrawFooter(footerRect);
        }

        private void DrawTitle(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.14f, 0.14f, 0.18f));
            float textWidth = rect.width - 28f;
            Text.Font = GameFont.Medium;
            float titleHeight = Mathf.Max(26f, Text.CalcHeight("RimChat_AirdropTradeCard_Title".Translate(), textWidth));
            GUI.color = new Color(0.95f, 0.95f, 0.98f);
            Widgets.Label(new Rect(rect.x + 14f, rect.y + 6f, textWidth, titleHeight), "RimChat_AirdropTradeCard_Title".Translate());

            Text.Font = GameFont.Tiny;
            string hint = "RimChat_AirdropTradeCard_TitleHint".Translate().ToString();
            float hintY = rect.y + 8f + titleHeight;
            float hintHeight = Mathf.Max(14f, Text.CalcHeight(hint, textWidth));
            GUI.color = new Color(0.68f, 0.72f, 0.82f);
            Widgets.Label(new Rect(rect.x + 14f, hintY, textWidth, hintHeight), hint);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawSearchArea(Rect rect)
        {
            DrawPanel(rect, new Color(0.09f, 0.09f, 0.12f, 0.98f));
            Rect labelRect = new Rect(rect.x + 12f, rect.y + 8f, 90f, 20f);
            Widgets.Label(labelRect, "RimChat_AirdropTradeCard_NeedLabel".Translate());

            Rect inputRect = new Rect(rect.x + 104f, rect.y + 6f, rect.width - 116f, 28f);
            Widgets.DrawBoxSolid(inputRect, new Color(0.15f, 0.15f, 0.19f));
            string newText = Widgets.TextField(inputRect, needSearchText ?? string.Empty);
            if (!string.Equals(newText, needSearchText, StringComparison.Ordinal))
            {
                needSearchText = newText;
                if (!searchState.IsSearchTextStillMatchingBinding(needSearchText))
                {
                    ClearNeedBinding();
                }

                if (string.IsNullOrWhiteSpace(needSearchText))
                {
                    showInlineSuggestions = false;
                    searchState.ClearSuggestions();
                }
                else
                {
                    TechLevel factionTech = faction?.def?.techLevel ?? TechLevel.Archotech;
                    searchState.ComputeSuggestions(needSearchText, null, factionTech);
                    showInlineSuggestions = searchState.Suggestions.Count > 0;
                }
            }

            Rect statusRect = new Rect(rect.x + 12f, rect.y + 40f, rect.width - 24f, rect.height - 46f);
            DrawNeedBindingStatus(statusRect);
        }

        private void DrawNeedBindingStatus(Rect rect)
        {
            Text.Font = GameFont.Tiny;
            if (boundNeedRecord?.Def == null)
            {
                GUI.color = new Color(0.88f, 0.72f, 0.3f);
                Widgets.Label(rect, "RimChat_AirdropTradeCard_NeedBindingMissing".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            GUI.color = new Color(0.62f, 0.85f, 0.62f);
            string text = "RimChat_AirdropTradeCard_NeedBindingReady".Translate(boundNeedRecord.Label, boundNeedRecord.DefName).ToString();
            Widgets.Label(rect, text);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawInlineSuggestionDropDown(Rect rect)
        {
            DrawPanel(rect, new Color(0.1f, 0.1f, 0.14f, 0.98f));
            float rowY = rect.y + 3f;
            for (int i = 0; i < searchState.Suggestions.Count && i < 6; i++)
            {
                ThingDefRecord record = searchState.Suggestions[i];
                Rect rowRect = new Rect(rect.x + 4f, rowY, rect.width - 8f, SuggestionRowHeight - 2f);
                bool hovered = Mouse.IsOver(rowRect);
                Widgets.DrawBoxSolid(rowRect, hovered ? new Color(0.25f, 0.37f, 0.55f, 0.82f) : new Color(0.12f, 0.12f, 0.16f, 0.76f));
                if (Widgets.ButtonInvisible(rowRect))
                {
                    BindNeedRecord(record);
                }

                Text.Font = GameFont.Tiny;
                GUI.color = hovered ? Color.white : new Color(0.88f, 0.9f, 0.94f);
                Widgets.Label(new Rect(rowRect.x + 8f, rowRect.y + 2f, rowRect.width - 16f, 16f), record.Label);
                GUI.color = new Color(0.62f, 0.68f, 0.8f);
                Widgets.Label(new Rect(rowRect.x + 8f, rowRect.y + 18f, rowRect.width - 16f, 16f), record.DefName);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                rowY += SuggestionRowHeight;
            }
        }

        private void DrawItemCards(Rect rect)
        {
            float halfWidth = (rect.width - Padding) * 0.5f;
            DrawNeedItemCard(new Rect(rect.x, rect.y, halfWidth, rect.height));
            DrawOfferItemCard(new Rect(rect.x + halfWidth + Padding, rect.y, halfWidth, rect.height));
        }

        private void DrawNeedItemCard(Rect rect)
        {
            DrawPanel(rect, new Color(0.07f, 0.09f, 0.11f, 0.98f));
            DrawCardHeader(rect, "RimChat_AirdropTradeCard_NeedItemCard");
            DrawBasketAddButton(new Rect(rect.xMax - 82f, rect.y + 5f, 72f, 26f), boundNeedRecord?.Def != null, AddCurrentNeedToBasket);
            DrawBasketRows(new Rect(rect.x + 10f, rect.y + 37f, rect.width - 20f, rect.height - 68f), needBasket, ref needBasketScrollPos);
            DrawBasketMarketTotal(new Rect(rect.x + 10f, rect.yMax - 25f, rect.width - 20f, 18f), needBasket);
        }

        private string ResolveNeedPriceSemantic()
        {
            if (boundNeedRecord?.Def == null) return "market_value";
            return ItemAirdropTradePolicy.ResolveNeedPriceSemantic(boundNeedRecord.Def, faction);
        }

        private void DrawOfferItemCard(Rect rect)
        {
            DrawPanel(rect, new Color(0.07f, 0.09f, 0.11f, 0.98f));
            DrawCardHeader(rect, "RimChat_AirdropTradeCard_OfferItemCard");
            ThingDef offerDef = DefDatabase<ThingDef>.GetNamedSilentFail(selectedOfferDefName);
            DrawBasketAddButton(new Rect(rect.xMax - 82f, rect.y + 5f, 72f, 26f), offerDef != null, AddCurrentPaymentToBasket);
            DrawBasketRows(new Rect(rect.x + 10f, rect.y + 37f, rect.width - 20f, rect.height - 68f), paymentBasket, ref paymentBasketScrollPos);
            DrawBasketMarketTotal(new Rect(rect.x + 10f, rect.yMax - 25f, rect.width - 20f, 18f), paymentBasket);
        }

        private void DrawCardHeader(Rect rect, string key)
        {
            GUI.color = new Color(0.25f, 0.29f, 0.35f, 0.95f);
            Widgets.DrawBox(new Rect(rect.x, rect.y, rect.width, rect.height));
            GUI.color = Color.white;
            Rect headerRect = new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, 20f);
            Widgets.Label(headerRect, key.Translate());
        }

        private void DrawEmptyCard(Rect rect, string key)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.64f, 0.66f, 0.72f, 0.92f);
            Widgets.Label(new Rect(rect.x + 12f, rect.y + 44f, rect.width - 24f, 36f), key.Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static string BuildPriceSemanticTag(string semantic)
        {
            if (string.IsNullOrWhiteSpace(semantic))
            {
                return "RimChat_ItemAirdropPriceSemanticMarket".Translate().ToString();
            }

            // Special item pricing semantics (now dynamic with multiplier suffix)
            if (semantic.StartsWith("special_item_discount", StringComparison.OrdinalIgnoreCase))
            {
                return "RimChat_ItemAirdropPriceSemanticDiscount".Translate().ToString();
            }

            if (semantic.StartsWith("special_item_scarce", StringComparison.OrdinalIgnoreCase))
            {
                return "RimChat_ItemAirdropPriceSemanticScarce".Translate().ToString();
            }

            if (semantic.StartsWith("market_value_x", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the multiplier from the tag
                string suffix = semantic.Substring("market_value_x".Length);
                return "Market x" + suffix;
            }

            if (semantic.StartsWith("untradeable_", StringComparison.OrdinalIgnoreCase))
            {
                return "RimChat_ItemAirdropPriceSemanticBlackMarket".Translate().ToString();
            }

            if (string.Equals(semantic, "player_buy", StringComparison.OrdinalIgnoreCase))
            {
                return "RimChat_ItemAirdropPriceSemanticBuy".Translate().ToString();
            }

            if (string.Equals(semantic, "player_sell", StringComparison.OrdinalIgnoreCase))
            {
                return "RimChat_ItemAirdropPriceSemanticSell".Translate().ToString();
            }

            return "RimChat_ItemAirdropPriceSemanticMarket".Translate().ToString();
        }

        private void DrawThingDefCardContent(Rect rect, ThingDefRecord record, int count, float unitPrice, float totalPrice, string priceSemantic)
        {
            float contentY = rect.y + 40f;
            Rect iconRect = new Rect(rect.x + 12f, contentY, CardImageSize, CardImageSize);
            if (record.Def.uiIcon != null)
            {
                GUI.DrawTexture(iconRect, record.Def.uiIcon);
            }
            else
            {
                Widgets.DrawBoxSolid(iconRect, new Color(0.15f, 0.15f, 0.18f));
            }

            GUI.color = new Color(0.27f, 0.31f, 0.38f, 0.95f);
            Widgets.DrawBox(iconRect);
            GUI.color = Color.white;

            float textX = iconRect.xMax + 10f;
            float textWidth = rect.width - (textX - rect.x) - 12f;
            float lineHeight = 16f;

            Text.Font = GameFont.Small;
            GUI.color = new Color(0.93f, 0.94f, 0.98f);
            string label = record.Label ?? record.DefName;
            float labelHeight = Mathf.Max(20f, Text.CalcHeight(label, textWidth));
            Widgets.Label(new Rect(textX, contentY, textWidth, labelHeight), label);

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.6f, 0.66f, 0.78f);
            float defNameY = contentY + labelHeight;
            float defNameHeight = Mathf.Max(lineHeight, Text.CalcHeight(record.DefName ?? string.Empty, textWidth));
            Widgets.Label(new Rect(textX, defNameY, textWidth, defNameHeight), record.DefName);

            float metricsY = Mathf.Max(iconRect.yMax - 2f, defNameY + defNameHeight + 2f);
            float halfWidth = textWidth * 0.5f;

            GUI.color = new Color(0.84f, 0.86f, 0.92f);
            Widgets.Label(new Rect(textX, metricsY, halfWidth, lineHeight), "RimChat_Price".Translate() + ": " + unitPrice.ToString("F1", CultureInfo.InvariantCulture));
            Widgets.Label(new Rect(textX + halfWidth, metricsY, halfWidth, lineHeight), "RimChat_StackLimit".Translate() + ": " + record.StackLimit);

            GUI.color = new Color(0.78f, 0.83f, 0.9f);
            Widgets.Label(new Rect(textX, metricsY + lineHeight, halfWidth, lineHeight), "RimChat_AirdropTradeCard_CountLabel".Translate() + ": " + count);
            GUI.color = new Color(0.94f, 0.8f, 0.42f);
            Widgets.Label(new Rect(textX + halfWidth, metricsY + lineHeight, halfWidth, lineHeight), "RimChat_AirdropTradeCard_TotalPriceLabel".Translate() + ": " + totalPrice.ToString("F1", CultureInfo.InvariantCulture));

            GUI.color = new Color(0.72f, 0.78f, 0.9f);
            float semanticY = metricsY + lineHeight * 2f;
            float semanticHeight = Mathf.Max(lineHeight, Text.CalcHeight("RimChat_AirdropTradeCard_PriceSemanticLabel".Translate(BuildPriceSemanticTag(priceSemantic)).ToString(), textWidth));
            Widgets.Label(new Rect(textX, semanticY, textWidth, semanticHeight), "RimChat_AirdropTradeCard_PriceSemanticLabel".Translate(BuildPriceSemanticTag(priceSemantic)).ToString());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawInventoryPanel(Rect rect)
        {
            DrawPanel(rect, new Color(0.08f, 0.08f, 0.11f, 0.98f));
            DrawInventorySearchBar(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, 24f));
            GUI.color = new Color(0.74f, 0.8f, 0.9f, 0.95f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 38f, rect.width - 20f, 16f), ResolveSelectedOfferLabel());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect listRect = new Rect(rect.x + 4f, rect.y + 58f, rect.width - 8f, rect.height - 62f);
            if (isLoadingInventory)
            {
                DrawLoadingIndicator(listRect);
                return;
            }

            if (filteredInventoryItems.Count == 0)
            {
                string emptyKey = inventoryItems.Count == 0 ? "RimChat_AirdropTradeCard_NoInventory" : "RimChat_AirdropTradeCard_NoSuggestions";
                Widgets.Label(new Rect(listRect.x + 8f, listRect.y + 8f, listRect.width - 16f, 24f), emptyKey.Translate());
                return;
            }

            float contentHeight = Math.Max(1f, filteredInventoryItems.Count * InventoryRowHeight);
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f, contentHeight);
            inventoryScrollPos = GUI.BeginScrollView(listRect, inventoryScrollPos, viewRect);
            float rowY = 0f;
            foreach (InventoryDisplayEntry entry in filteredInventoryItems)
            {
                DrawInventoryRow(entry, viewRect.width, rowY);
                rowY += InventoryRowHeight;
            }

            GUI.EndScrollView();
        }

        private void DrawInventorySearchBar(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, 60f, 22f), "RimChat_Search".Translate());
            Rect inputRect = new Rect(rect.x + 60f, rect.y, rect.width - 60f, 22f);
            Widgets.DrawBoxSolid(inputRect, new Color(0.15f, 0.15f, 0.19f));
            string newText = Widgets.TextField(inputRect, inventorySearchText ?? string.Empty);
            if (!string.Equals(newText, inventorySearchText, StringComparison.Ordinal))
            {
                inventorySearchText = newText;
                ApplyInventoryFilter();
                EnsureOfferSelectionState();
            }
        }

        private void DrawLoadingIndicator(Rect rect)
        {
            float barWidth = rect.width * 0.58f;
            Rect progressRect = new Rect(rect.x + (rect.width - barWidth) * 0.5f, rect.y + rect.height * 0.42f, barWidth, 8f);
            Widgets.DrawBoxSolid(progressRect, new Color(0.2f, 0.2f, 0.24f));
            Widgets.DrawBoxSolid(new Rect(progressRect.x, progressRect.y, progressRect.width * inventoryLoadProgress, progressRect.height), new Color(0.38f, 0.58f, 0.84f, 0.85f));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.75f, 0.77f, 0.82f);
            Widgets.Label(new Rect(rect.x, progressRect.yMax + 8f, rect.width, 18f), "RimChat_Loading".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawInventoryRow(InventoryDisplayEntry entry, float width, float y)
        {
            Rect rowRect = new Rect(2f, y, width - 4f, InventoryRowHeight - 2f);
            Rect allButtonRect = new Rect(rowRect.xMax - 58f, rowRect.y + 9f, 52f, 26f);
            Rect selectionRect = new Rect(rowRect.x, rowRect.y, rowRect.width - 64f, rowRect.height);
            bool selected = string.Equals(selectedOfferDefName, entry.DefName, StringComparison.OrdinalIgnoreCase);
            Widgets.DrawBoxSolid(rowRect, selected ? new Color(0.19f, 0.39f, 0.63f, 0.82f) : new Color(0.12f, 0.12f, 0.16f, 0.82f));
            if (selected)
            {
                GUI.color = new Color(0.46f, 0.62f, 0.92f, 0.95f);
                Widgets.DrawBox(rowRect);
                GUI.color = Color.white;
            }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(entry.DefName);
            Rect iconRect = new Rect(rowRect.x + 6f, rowRect.y + 10f, 20f, 20f);
            if (def?.uiIcon != null)
            {
                GUI.DrawTexture(iconRect, def.uiIcon);
            }

            float textX = iconRect.xMax + 8f;
            float textWidth = allButtonRect.x - textX - 6f;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(textX, rowRect.y + 5f, textWidth * 0.62f, 16f), $"{entry.Label} ({entry.DefName})");
            GUI.color = new Color(0.72f, 0.78f, 0.9f);
            Widgets.Label(new Rect(textX, rowRect.y + 24f, textWidth * 0.32f, 16f), "x" + entry.Count.ToString(CultureInfo.InvariantCulture));
            GUI.color = new Color(0.94f, 0.8f, 0.42f);
            Widgets.Label(new Rect(textX + textWidth * 0.62f, rowRect.y + 24f, textWidth * 0.38f, 16f), "@" + entry.UnitPrice.ToString("F0", CultureInfo.InvariantCulture) + " (" + BuildPriceSemanticTag(entry.PriceSemantic) + ")");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (Widgets.ButtonInvisible(selectionRect))
            {
                ApplyOfferSelection(entry);
            }

            if (Widgets.ButtonText(allButtonRect, "RimChat_AirdropTradeCard_AddAllInventory".Translate()))
            {
                AddAllCurrentPaymentToBasket(entry);
            }
            TooltipHandler.TipRegion(allButtonRect,
                "RimChat_AirdropTradeCard_AddAllInventoryTooltip".Translate(entry.Count, entry.Label));
        }

        private void DrawFooter(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.1f, 0.1f, 0.13f));

            Rect statRect = new Rect(rect.x + 12f, rect.y + 8f, rect.width * 0.58f, 38f);
            DrawReferencePriceBlock(statRect);
            Rect comparisonRect = new Rect(statRect.xMax + 12f, statRect.y, rect.xMax - statRect.xMax - 24f, statRect.height);
            DrawMarketValueComparisonBlock(comparisonRect);

            DrawTradeRulesInfo(new Rect(rect.x + 12f, rect.y + 50f, rect.width - 24f, 32f));

            string failReason = GetSubmitDisabledReason();
            if (!string.IsNullOrWhiteSpace(failReason))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.9f, 0.74f, 0.32f);
                Widgets.Label(new Rect(rect.x + 12f, rect.y + 88f, rect.width * 0.58f, 16f), failReason);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }

            float buttonWidth = 160f;
            Rect cancelRect = new Rect(rect.xMax - buttonWidth - 12f, rect.yMax - 36f, buttonWidth, 28f);
            Rect submitRect = new Rect(cancelRect.x - buttonWidth - 10f, cancelRect.y, buttonWidth, 28f);
            bool canSubmit = CanSubmit();
            GUI.enabled = canSubmit;
            if (Widgets.ButtonText(submitRect, "RimChat_AirdropTradeCard_Submit".Translate()))
            {
                Submit();
            }

            GUI.enabled = true;
            if (Widgets.ButtonText(cancelRect, "RimChat_AirdropTradeCard_Cancel".Translate()))
            {
                Close();
            }
        }

        private void DrawTradeRulesInfo(Rect rect)
        {
            Text.Font = GameFont.Tiny;
            AirdropTradeRuleSnapshot tradeRule = ResolveTradeRuleSnapshot();
            float offerTotal = ComputeOfferTotal();
            int podCount = ComputePodCount();
            int shippingCost = podCount * tradeRule.ShippingCostPerPod;
            int factionTradeTotal = Mathf.RoundToInt(GameAIInterface.Instance.GetAirdropFactionTradeTotalForPolicy(faction));
            int tradeGrowthDelta = tradeRule.TradeGrowthDeltaSilver;

            bool limitExceeded = offerTotal > tradeRule.TradeLimitSilver;
            GUI.color = limitExceeded ? new Color(0.95f, 0.35f, 0.35f) : new Color(0.72f, 0.82f, 0.72f);
            string limitText = "RimChat_AirdropTradeCard_TradeLimit".Translate(
                tradeRule.Goodwill,
                tradeRule.TradeLimitSilver,
                FormatTradeAmountCompact(factionTradeTotal),
                tradeGrowthDelta >= 0 ? $"+{tradeGrowthDelta}" : tradeGrowthDelta.ToString(CultureInfo.InvariantCulture)).ToString();
            Widgets.Label(new Rect(rect.x, rect.y, rect.width * 0.9f, 16f), limitText);

            GUI.color = new Color(0.82f, 0.82f, 0.65f);
            string podText = "RimChat_AirdropTradeCard_PodInfo".Translate(podCount, shippingCost, tradeRule.ShippingCostPerPod).ToString();
            Widgets.Label(new Rect(rect.x, rect.y + 16f, rect.width * 0.9f, 16f), podText);

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawReferencePriceBlock(Rect rect)
        {
            DrawPanel(rect, new Color(0.11f, 0.11f, 0.15f, 0.96f));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.74f, 0.78f, 0.88f);
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 6f, 120f, 14f), "RimChat_AirdropTradeCard_ReferencePriceLabel".Translate());
            Text.Font = GameFont.Small;
            GUI.color = needBasket.Count == 0 ? new Color(0.64f, 0.66f, 0.72f) : new Color(0.96f, 0.82f, 0.4f);
            string value = needBasket.Count == 0
                ? "RimChat_AirdropTradeCard_ReferencePriceEmpty".Translate().ToString()
                : BuildReferencePriceFormulaText();
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 18f, rect.width - 20f, 20f), value);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private string BuildReferencePriceFormulaText()
        {
            int needTotal = Mathf.RoundToInt(ComputeNeedReferenceTotal());
            int podCount = ComputePodCount();
            AirdropTradeRuleSnapshot tradeRule = ResolveTradeRuleSnapshot();
            int referencePrice = needTotal + podCount * tradeRule.ShippingCostPerPod;
            int currentOffer = Mathf.RoundToInt(ComputeOfferTotal());
            return "RimChat_AirdropTradeCard_ReferencePriceFormula".Translate(
                needTotal,
                podCount,
                tradeRule.ShippingCostPerPod,
                referencePrice,
                currentOffer).ToString();
        }

        private static string FormatTradeAmountCompact(int amount)
        {
            int safe = Math.Max(0, amount);
            if (safe >= 1000)
            {
                return (safe / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            }

            return safe.ToString(CultureInfo.InvariantCulture);
        }



        private void DrawFooterInputs(Rect rect)
        {
            float groupWidth = (rect.width - 12f) * 0.5f;
            DrawIntegerField(new Rect(rect.x, rect.y, groupWidth, rect.height), "RimChat_AirdropTradeCard_RequestCountLabel".Translate().ToString(), requestedCountText, out requestedCountText, 1, 100000);
            DrawIntegerField(new Rect(rect.x + groupWidth + 12f, rect.y, groupWidth, rect.height), "RimChat_AirdropTradeCard_OfferCountLabel".Translate().ToString(), offerCountText, out offerCountText, 1, 1000000);
        }

        private static void DrawIntegerField(Rect rect, string label, string current, out string updated, int min, int max)
        {
            updated = current;
            float labelWidth = Mathf.Min(104f, rect.width * 0.42f);
            Widgets.Label(new Rect(rect.x, rect.y + 2f, labelWidth, 22f), label);
            Rect fieldRect = new Rect(rect.x + labelWidth + 4f, rect.y, rect.width - labelWidth - 4f, 24f);
            Widgets.DrawBoxSolid(fieldRect, new Color(0.17f, 0.17f, 0.21f));
            string input = Widgets.TextField(fieldRect, current ?? string.Empty);
            if (!int.TryParse(input, out int parsed))
            {
                return;
            }

            if (parsed < min || parsed > max)
            {
                return;
            }

            updated = parsed.ToString(CultureInfo.InvariantCulture);
        }

        private int ComputePodCount()
        {
            int pods = 0;
            foreach (ItemAirdropTradeLine line in needBasket)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(line.DefName);
                pods += (int)Math.Ceiling((double)Math.Max(1, line.Count) / Math.Max(1, def?.stackLimit ?? 1));
            }
            return pods;
        }

        private bool CanSubmit()
        {
            return string.IsNullOrWhiteSpace(GetSubmitDisabledReason());
        }

        private string GetSubmitDisabledReason()
        {
            if (needBasket.Count == 0)
            {
                return "RimChat_AirdropTradeCard_SubmitDisabledNeed".Translate().ToString();
            }

            if (paymentBasket.Count == 0)
            {
                return "RimChat_AirdropTradeCard_SubmitDisabledOffer".Translate().ToString();
            }

            return string.Empty;
        }

        private void Submit()
        {
            if (!CanSubmit())
            {
                return;
            }

            string validationFailure = ValidateBeforeSubmit();
            if (!string.IsNullOrWhiteSpace(validationFailure))
            {
                ShowValidationFailureDialog(validationFailure);
                return;
            }

            int podCount = ComputePodCount();
            AirdropTradeRuleSnapshot tradeRule = ResolveTradeRuleSnapshot();
            int shippingCost = podCount * tradeRule.ShippingCostPerPod;
            ItemAirdropTradeLine firstNeed = needBasket[0];
            ItemAirdropTradeLine firstPayment = paymentBasket[0];
            var payload = new ItemAirdropTradeCardPayload
            {
                Need = ItemAirdropBasket.Summary(needBasket),
                RequestedCount = firstNeed.Count,
                OfferItemDefName = firstPayment.DefName,
                OfferItemLabel = firstPayment.Label,
                OfferItemCount = firstPayment.Count,
                Scenario = "trade",
                NeedDefName = firstNeed.DefName,
                NeedLabel = firstNeed.Label,
                NeedSearchText = firstNeed.Label,
                NeedUnitPrice = firstNeed.UnitPrice,
                NeedReferenceTotalPrice = ComputeNeedReferenceTotal(),
                OfferUnitPrice = firstPayment.UnitPrice,
                OfferTotalPrice = ComputeOfferTotal(),
                ShippingPodCount = podCount,
                ShippingCostSilver = shippingCost,
                RequestId = Guid.NewGuid().ToString("N"),
                IsRevision = initialPayload != null && initialPayload.IsRevision,
                NeedItems = ItemAirdropBasket.Copy(needBasket),
                PaymentItems = ItemAirdropBasket.Copy(paymentBasket)
            };

            onSubmitted?.Invoke(payload);
            Close();
        }

        private void BindNeedRecord(ThingDefRecord record)
        {
            if (record?.Def == null)
            {
                return;
            }

            boundNeedRecord = record;
            searchState.TryBindToRecord(record);
            needSearchText = record.Label;
            showInlineSuggestions = false;
        }

        private void ApplyPendingInventoryLoadIfReady()
        {
            if (!inventoryLoadCompleted)
            {
                return;
            }

            inventoryLoadCompleted = false;
            inventoryItems.Clear();
            if (pendingInventoryItems != null && pendingInventoryItems.Count > 0)
            {
                inventoryItems.AddRange(pendingInventoryItems);
            }

            pendingInventoryItems = null;
            ApplyInventoryFilter();
            EnsureOfferSelectionState();
            inventoryLoadProgress = 1f;
            isLoadingInventory = false;
        }

        private InventoryDisplayEntry FindInventoryEntryByDefName(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName))
            {
                return null;
            }

            return inventoryItems.FirstOrDefault(entry =>
                string.Equals(entry.DefName, defName, StringComparison.OrdinalIgnoreCase));
        }

        private void ClearOfferSelection()
        {
            selectedOfferDefName = string.Empty;
            selectedOfferLabel = string.Empty;
            selectedOfferStackLimit = 1;
            selectedOfferUnitPrice = 1f;
            selectedOfferPriceSemantic = string.Empty;
        }

        private void ClearNeedBinding()
        {
            boundNeedRecord = null;
            searchState.ClearBinding();
        }

        private void ApplyOfferSelection(InventoryDisplayEntry entry)
        {
            selectedOfferDefName = entry.DefName;
            selectedOfferLabel = entry.Label;
            selectedOfferStackLimit = entry.StackLimit;
            selectedOfferUnitPrice = entry.UnitPrice;
            selectedOfferPriceSemantic = entry.PriceSemantic ?? string.Empty;
        }

        private string ResolveSelectedOfferLabel()
        {
            return string.IsNullOrWhiteSpace(selectedOfferLabel)
                ? "RimChat_AirdropTradeCard_NoOfferItem".Translate().ToString()
                : "RimChat_AirdropTradeCard_SelectedOfferItem".Translate(selectedOfferLabel).ToString();
        }

        private float ComputeNeedReferenceTotal()
        {
            return needBasket.Sum(line => Math.Max(0f, line.UnitPrice) * Math.Max(0, line.Count));
        }

        private float ResolveNeedUnitPrice()
        {
            if (boundNeedRecord?.Def == null)
            {
                return 0.01f;
            }

            float unitPrice;

            if (faction != null && 
                FactionSpecialItemsManager.Instance.TryMatchSpecialItem(faction, boundNeedRecord.DefName, out SpecialItemType specialItemType))
            {
                if (ItemAirdropTradePolicy.TryResolveSpecialItemPrice(boundNeedRecord.Def, specialItemType, out float specialPrice, out _))
                {
                    unitPrice = Math.Max(0.01f, specialPrice);
                }
                else
                {
                    unitPrice = ResolveStandardNeedFallback();
                }
            }
            else
            {
                unitPrice = ResolveStandardNeedFallback();
            }

            ItemAirdropTradePolicy.ApplyUntradeablePremium(boundNeedRecord.Def, ref unitPrice);

            return unitPrice;
        }

        private float ResolveStandardNeedFallback()
        {
            if (ItemAirdropTradePolicy.TryResolveNeedUnitPrice(boundNeedRecord.Def, out float resolved, out _))
            {
                return Math.Max(0.01f, resolved);
            }

            return Math.Max(0.01f, boundNeedRecord.MarketValue);
        }

        private float ComputeOfferTotal()
        {
            return paymentBasket.Sum(line => Math.Max(0f, line.UnitPrice) * Math.Max(0, line.Count));
        }

        private string ValidateBeforeSubmit()
        {
            Map map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
            if (map != null && Core.MapUtility.IsOrbitalBaseMap(map))
            {
                return "RimChat_AirdropSubmitOrbitalBase".Translate();
            }

            foreach (ItemAirdropTradeLine line in paymentBasket)
            {
                InventoryDisplayEntry offerEntry = FindInventoryEntryByDefName(line.DefName);
                if (offerEntry == null || offerEntry.Count < line.Count)
                    return "RimChat_AirdropSubmitInsufficientOffer".Translate(
                        line.Label ?? line.DefName ?? "RimChat_Unknown".Translate(), line.Count, offerEntry?.Count ?? 0);
            }

            return string.Empty;
        }

        private void ShowValidationFailureDialog(string message)
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                message,
                "OK".Translate(),
                null));
        }

        private static int ParsePositiveInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private static void DrawPanel(Rect rect, Color fill)
        {
            Widgets.DrawBoxSolid(rect, fill);
            GUI.color = new Color(0.24f, 0.27f, 0.34f, 0.95f);
            Widgets.DrawBox(rect);
            GUI.color = Color.white;
        }

        private sealed class InventoryDisplayEntry
        {
            public string DefName { get; set; }
            public string Label { get; set; }
            public int Count { get; set; }
            public float UnitPrice { get; set; }
            public int StackLimit { get; set; }
            public string PriceSemantic { get; set; }
        }
    }
}
