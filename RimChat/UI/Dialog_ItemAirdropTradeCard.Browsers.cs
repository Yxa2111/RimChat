using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimChat.DiplomacySystem;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimChat.UI
{
    public partial class Dialog_ItemAirdropTradeCard
    {
        private readonly List<ThingDefRecord> needBrowserItems = new List<ThingDefRecord>();
        private readonly List<ThingDefRecord> filteredNeedBrowserItems = new List<ThingDefRecord>();
        private string needBrowserSearchText = string.Empty;
        private ThingCategoryDef selectedNeedBrowserCategory;
        private ThingCategoryDef selectedOfferBrowserCategory;
        private Vector2 needBrowserScrollPos = Vector2.zero;
        private bool needBrowserDataReady;

        private const float BrowserToolbarHeight = 66f;

        private void EnsureNeedBrowserData()
        {
            if (needBrowserDataReady) return;

            TechLevel factionTechLevel = faction?.def?.techLevel ?? TechLevel.Archotech;
            needBrowserItems.Clear();
            needBrowserItems.AddRange(ThingDefCatalog.GetRecords()
                .Where(record => record?.Def != null)
                .Where(record => IsWithinFactionTechLevel(record.Def, factionTechLevel))
                .Where(record => ThingDefCatalog.CanCandidateForNeed(record, ItemAirdropNeedFamily.Unknown))
                .OrderBy(record => record.Label)
                .ThenBy(record => record.DefName));
            needBrowserDataReady = true;
            ApplyNeedBrowserFilter();
        }

        private void ApplyNeedBrowserFilter()
        {
            filteredNeedBrowserItems.Clear();
            string query = (needBrowserSearchText ?? string.Empty).Trim();
            filteredNeedBrowserItems.AddRange(needBrowserItems
                .Where(record => selectedNeedBrowserCategory == null ||
                    IsWithinBrowserCategory(record.Def, selectedNeedBrowserCategory))
                .Where(record => string.IsNullOrWhiteSpace(query) ||
                    record.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    record.DefName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    record.SearchText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void DrawDualItemBrowsers(Rect rect)
        {
            float halfWidth = (rect.width - Padding) * 0.5f;
            DrawNeedBrowserPanel(new Rect(rect.x, rect.y, halfWidth, rect.height));
            DrawOfferBrowserPanel(new Rect(rect.x + halfWidth + Padding, rect.y, halfWidth, rect.height));
        }

        private void DrawNeedBrowserPanel(Rect rect)
        {
            EnsureNeedBrowserData();
            DrawPanel(rect, new Color(0.08f, 0.08f, 0.11f, 0.98f));
            DrawBrowserFilterToolbar(rect, true);
            Rect listRect = new Rect(rect.x + 5f, rect.y + BrowserToolbarHeight, rect.width - 10f,
                rect.height - BrowserToolbarHeight - 5f);
            DrawNeedBrowserRows(listRect);
        }

        private void DrawOfferBrowserPanel(Rect rect)
        {
            DrawPanel(rect, new Color(0.08f, 0.08f, 0.11f, 0.98f));
            DrawBrowserFilterToolbar(rect, false);
            Rect listRect = new Rect(rect.x + 5f, rect.y + BrowserToolbarHeight, rect.width - 10f,
                rect.height - BrowserToolbarHeight - 5f);
            if (isLoadingInventory)
            {
                DrawLoadingIndicator(listRect);
                return;
            }

            DrawOfferBrowserRows(listRect);
        }

        private void DrawBrowserFilterToolbar(Rect rect, bool needSide)
        {
            Rect categoryRect = new Rect(rect.x + 8f, rect.y + 7f, 126f, 24f);
            ThingCategoryDef category = needSide ? selectedNeedBrowserCategory : selectedOfferBrowserCategory;
            string categoryText = category == null
                ? "RimChat_AirdropTradeCard_AllCategories".Translate().ToString()
                : category.LabelCap.ToString();
            if (Widgets.ButtonText(categoryRect, categoryText))
            {
                OpenBrowserCategoryMenu(needSide);
            }

            Rect searchLabelRect = new Rect(categoryRect.xMax + 8f, rect.y + 9f, 44f, 22f);
            Widgets.Label(searchLabelRect, "RimChat_Search".Translate());
            Rect searchRect = new Rect(searchLabelRect.xMax, rect.y + 7f, rect.xMax - searchLabelRect.xMax - 8f, 24f);
            Widgets.DrawBoxSolid(searchRect, new Color(0.15f, 0.15f, 0.19f));
            string currentSearch = needSide ? needBrowserSearchText : inventorySearchText;
            string updatedSearch = Widgets.TextField(searchRect, currentSearch ?? string.Empty);
            if (!string.Equals(updatedSearch, currentSearch, StringComparison.Ordinal))
            {
                if (needSide)
                {
                    needBrowserSearchText = updatedSearch;
                    needBrowserScrollPos = Vector2.zero;
                    ApplyNeedBrowserFilter();
                }
                else
                {
                    inventorySearchText = updatedSearch;
                    inventoryScrollPos = Vector2.zero;
                    ApplyInventoryFilter();
                }
            }

            Rect countRect = new Rect(rect.x + 8f, rect.y + 36f, 188f, 24f);
            if (needSide)
            {
                DrawBrowserCountEditor(countRect, "RimChat_AirdropTradeCard_RequestCountLabel", ref requestedCountText, 100000);
            }
            else
            {
                DrawBrowserCountEditor(countRect, "RimChat_AirdropTradeCard_OfferCountLabel", ref offerCountText, ItemAirdropBasket.MaxCount);
            }

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.7f, 0.76f, 0.88f);
            string selection = needSide
                ? (boundNeedRecord?.Label ?? "RimChat_AirdropTradeCard_NoNeedSelection".Translate().ToString())
                : (selectedOfferLabel ?? "RimChat_AirdropTradeCard_NoOfferItem".Translate().ToString());
            Widgets.Label(new Rect(countRect.xMax + 8f, rect.y + 40f, rect.xMax - countRect.xMax - 16f, 18f), selection);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void DrawBrowserCountEditor(Rect rect, string labelKey, ref string countText, int max)
        {
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x, rect.y + 3f, 76f, 18f), labelKey.Translate());
            Rect fieldRect = new Rect(rect.x + 78f, rect.y, rect.width - 78f, 23f);
            Widgets.DrawBoxSolid(fieldRect, new Color(0.15f, 0.15f, 0.19f));
            string input = Widgets.TextField(fieldRect, countText ?? string.Empty);
            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed > 0 && parsed <= max)
            {
                countText = parsed.ToString(CultureInfo.InvariantCulture);
            }
            Text.Font = GameFont.Small;
        }

        private void DrawNeedBrowserRows(Rect rect)
        {
            if (filteredNeedBrowserItems.Count == 0)
            {
                Widgets.Label(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 24f),
                    "RimChat_AirdropTradeCard_NoSuggestions".Translate());
                return;
            }

            const float rowHeight = 48f;
            Rect view = new Rect(0f, 0f, Math.Max(1f, rect.width - 16f), filteredNeedBrowserItems.Count * rowHeight);
            needBrowserScrollPos = GUI.BeginScrollView(rect, needBrowserScrollPos, view);
            int first = Math.Max(0, Mathf.FloorToInt(needBrowserScrollPos.y / rowHeight) - 1);
            int last = Math.Min(filteredNeedBrowserItems.Count - 1,
                Mathf.CeilToInt((needBrowserScrollPos.y + rect.height) / rowHeight) + 1);
            for (int i = first; i <= last; i++)
            {
                DrawNeedBrowserRow(filteredNeedBrowserItems[i], view.width, i * rowHeight);
            }
            GUI.EndScrollView();
        }

        private void DrawNeedBrowserRow(ThingDefRecord record, float width, float y)
        {
            Rect row = new Rect(2f, y, width - 4f, 46f);
            Rect addRect = new Rect(row.xMax - 58f, row.y + 10f, 52f, 26f);
            Rect selectionRect = new Rect(row.x, row.y, row.width - 64f, row.height);
            bool selected = string.Equals(boundNeedRecord?.DefName, record.DefName, StringComparison.OrdinalIgnoreCase);
            Widgets.DrawBoxSolid(row, selected ? new Color(0.19f, 0.39f, 0.63f, 0.82f) : new Color(0.12f, 0.12f, 0.16f, 0.82f));
            if (selected)
            {
                GUI.color = new Color(0.46f, 0.62f, 0.92f, 0.95f);
                Widgets.DrawBox(row);
                GUI.color = Color.white;
            }

            DrawBrowserThingInfo(row, addRect.x - 6f, record.Def, record.Label, record.DefName,
                "RimChat_AirdropTradeCard_NeedBrowserPrice".Translate(FormatMarketValue(record.MarketValue)).ToString());
            if (Widgets.ButtonInvisible(selectionRect)) BindNeedRecord(record);
            if (Widgets.ButtonText(addRect, "RimChat_AirdropTradeCard_AddItem".Translate()))
            {
                BindNeedRecord(record);
                AddCurrentNeedToBasket();
            }
        }

        private void DrawOfferBrowserRows(Rect rect)
        {
            if (filteredInventoryItems.Count == 0)
            {
                string key = inventoryItems.Count == 0 ? "RimChat_AirdropTradeCard_NoInventory" : "RimChat_AirdropTradeCard_NoSuggestions";
                Widgets.Label(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 24f), key.Translate());
                return;
            }

            const float rowHeight = 48f;
            Rect view = new Rect(0f, 0f, Math.Max(1f, rect.width - 16f), filteredInventoryItems.Count * rowHeight);
            inventoryScrollPos = GUI.BeginScrollView(rect, inventoryScrollPos, view);
            int first = Math.Max(0, Mathf.FloorToInt(inventoryScrollPos.y / rowHeight) - 1);
            int last = Math.Min(filteredInventoryItems.Count - 1,
                Mathf.CeilToInt((inventoryScrollPos.y + rect.height) / rowHeight) + 1);
            for (int i = first; i <= last; i++)
            {
                DrawOfferBrowserRow(filteredInventoryItems[i], view.width, i * rowHeight);
            }
            GUI.EndScrollView();
        }

        private void DrawOfferBrowserRow(InventoryDisplayEntry entry, float width, float y)
        {
            Rect row = new Rect(2f, y, width - 4f, 46f);
            Rect allRect = new Rect(row.xMax - 56f, row.y + 10f, 50f, 26f);
            Rect addRect = new Rect(allRect.x - 48f, row.y + 10f, 42f, 26f);
            Rect selectionRect = new Rect(row.x, row.y, row.width - 110f, row.height);
            bool selected = string.Equals(selectedOfferDefName, entry.DefName, StringComparison.OrdinalIgnoreCase);
            Widgets.DrawBoxSolid(row, selected ? new Color(0.19f, 0.39f, 0.63f, 0.82f) : new Color(0.12f, 0.12f, 0.16f, 0.82f));
            if (selected)
            {
                GUI.color = new Color(0.46f, 0.62f, 0.92f, 0.95f);
                Widgets.DrawBox(row);
                GUI.color = Color.white;
            }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(entry.DefName);
            string detail = "RimChat_AirdropTradeCard_OfferBrowserDetail".Translate(
                entry.Count,
                FormatMarketValue(def?.BaseMarketValue ?? 0f),
                entry.UnitPrice.ToString("0.##", CultureInfo.InvariantCulture)).ToString();
            DrawBrowserThingInfo(row, addRect.x - 6f, def, entry.Label, entry.DefName, detail);

            if (Widgets.ButtonInvisible(selectionRect)) ApplyOfferSelection(entry);
            if (Widgets.ButtonText(addRect, "+"))
            {
                ApplyOfferSelection(entry);
                AddCurrentPaymentToBasket();
            }
            if (Widgets.ButtonText(allRect, "RimChat_AirdropTradeCard_AddAllInventory".Translate()))
            {
                AddAllCurrentPaymentToBasket(entry);
            }
            TooltipHandler.TipRegion(allRect,
                "RimChat_AirdropTradeCard_AddAllInventoryTooltip".Translate(entry.Count, entry.Label));
        }

        private static void DrawBrowserThingInfo(Rect row, float rightEdge, ThingDef def, string label, string defName, string detail)
        {
            Rect icon = new Rect(row.x + 6f, row.y + 9f, 28f, 28f);
            if (def?.uiIcon != null) GUI.DrawTexture(icon, def.uiIcon);
            float textX = icon.xMax + 7f;
            float textWidth = Math.Max(20f, rightEdge - textX);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.93f, 0.94f, 0.98f);
            Widgets.Label(new Rect(textX, row.y + 5f, textWidth, 18f), label ?? defName);
            GUI.color = new Color(0.68f, 0.75f, 0.88f);
            Widgets.Label(new Rect(textX, row.y + 24f, textWidth, 18f), detail);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(row, $"{label} ({defName})\n{detail}");
        }

        private void OpenBrowserCategoryMenu(bool needSide)
        {
            IEnumerable<ThingDef> defs = needSide
                ? needBrowserItems.Select(record => record.Def)
                : inventoryItems.Select(entry => DefDatabase<ThingDef>.GetNamedSilentFail(entry.DefName));
            List<ThingCategoryDef> categories = CollectBrowserCategories(defs);
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RimChat_AirdropTradeCard_AllCategories".Translate(), () => SelectBrowserCategory(needSide, null))
            };
            foreach (ThingCategoryDef item in categories)
            {
                ThingCategoryDef captured = item;
                int count = defs.Count(def => IsWithinBrowserCategory(def, captured));
                options.Add(new FloatMenuOption($"{captured.LabelCap} ({count})", () => SelectBrowserCategory(needSide, captured)));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void SelectBrowserCategory(bool needSide, ThingCategoryDef category)
        {
            if (needSide)
            {
                selectedNeedBrowserCategory = category;
                needBrowserScrollPos = Vector2.zero;
                ApplyNeedBrowserFilter();
            }
            else
            {
                selectedOfferBrowserCategory = category;
                inventoryScrollPos = Vector2.zero;
                ApplyInventoryFilter();
            }
        }

        private static List<ThingCategoryDef> CollectBrowserCategories(IEnumerable<ThingDef> defs)
        {
            return (defs ?? Enumerable.Empty<ThingDef>())
                .Where(def => def != null)
                .SelectMany(GetTopLevelBrowserCategories)
                .GroupBy(category => category.defName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(category => category.LabelCap.ToString())
                .ToList();
        }

        private static IEnumerable<ThingCategoryDef> GetTopLevelBrowserCategories(ThingDef def)
        {
            if (def?.thingCategories == null) yield break;
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ThingCategoryDef directCategory in def.thingCategories)
            {
                ThingCategoryDef current = directCategory;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (current?.parent != null &&
                       !string.Equals(current.parent.defName, "Root", StringComparison.OrdinalIgnoreCase) &&
                       visited.Add(current.defName ?? string.Empty))
                {
                    current = current.parent;
                }

                if (current != null && !string.Equals(current.defName, "Root", StringComparison.OrdinalIgnoreCase) &&
                    yielded.Add(current.defName ?? string.Empty))
                {
                    yield return current;
                }
            }
        }

        private static bool IsWithinBrowserCategory(ThingDef def, ThingCategoryDef selectedCategory)
        {
            if (selectedCategory == null) return true;
            if (def?.thingCategories == null) return false;
            foreach (ThingCategoryDef directCategory in def.thingCategories)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (ThingCategoryDef current = directCategory;
                     current != null && visited.Add(current.defName ?? string.Empty);
                     current = current.parent)
                {
                    if (string.Equals(current.defName, selectedCategory.defName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
