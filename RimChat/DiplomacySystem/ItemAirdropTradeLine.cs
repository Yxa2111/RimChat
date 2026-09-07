using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Verse;

namespace RimChat.DiplomacySystem
{
    // Value snapshot used by the editor, historical cards and prepared orders.
    public sealed class ItemAirdropTradeLine : IExposable
    {
        public string DefName = string.Empty;
        public string Label = string.Empty;
        public int Count;
        public float UnitPrice;
        public ItemAirdropTradeLine Clone() => new ItemAirdropTradeLine
        {
            DefName = DefName, Label = Label, Count = Count, UnitPrice = UnitPrice
        };
        public void ExposeData()
        {
            Scribe_Values.Look(ref DefName, "defName", string.Empty);
            Scribe_Values.Look(ref Label, "label", string.Empty);
            Scribe_Values.Look(ref Count, "count", 0);
            Scribe_Values.Look(ref UnitPrice, "unitPrice", 0f);
        }
    }

    public static class ItemAirdropBasket
    {
        public const int MaxLines = 32;
        public const int MaxCount = 1000000;

        public static List<ItemAirdropTradeLine> Copy(IEnumerable<ItemAirdropTradeLine> lines) =>
            lines?.Select(line => line?.Clone()).ToList() ?? new List<ItemAirdropTradeLine>();

        // Merge duplicate defs before inventory reservation; never reserve the same stock twice.
        public static bool TryNormalize(IEnumerable<ItemAirdropTradeLine> lines,
            out List<ItemAirdropTradeLine> normalized)
        {
            normalized = new List<ItemAirdropTradeLine>();
            if (lines == null) return false;
            foreach (ItemAirdropTradeLine line in lines)
            {
                if (line == null || string.IsNullOrWhiteSpace(line.DefName) || line.Count <= 0 || line.Count > MaxCount)
                    return false;
                ItemAirdropTradeLine existing = normalized.FirstOrDefault(row =>
                    string.Equals(row.DefName, line.DefName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    if (normalized.Count >= MaxLines) return false;
                    normalized.Add(line.Clone());
                }
                else
                {
                    if ((long)existing.Count + line.Count > MaxCount) return false;
                    existing.Count += line.Count;
                }
            }
            return normalized.Count > 0;
        }

        public static bool SameTerms(IEnumerable<ItemAirdropTradeLine> expected, IEnumerable<ItemAirdropTradeLine> actual)
        {
            return TryNormalize(expected, out var left) && TryNormalize(actual, out var right) &&
                left.Count == right.Count && left.All(a => right.Any(b =>
                    string.Equals(a.DefName, b.DefName, StringComparison.OrdinalIgnoreCase) && a.Count == b.Count));
        }

        public static List<Dictionary<string, object>> ToParameters(IEnumerable<ItemAirdropTradeLine> lines) =>
            lines.Select(line => new Dictionary<string, object>
            { ["item"] = line.DefName, ["count"] = line.Count }).ToList();

        public static bool TryRead(Dictionary<string, object> parameters, string key, out List<ItemAirdropTradeLine> lines)
        {
            lines = null;
            if (parameters == null || !parameters.TryGetValue(key, out var raw) || !(raw is IEnumerable<object> rows)) return false;
            var parsed = new List<ItemAirdropTradeLine>();
            foreach (object row in rows)
            {
                if (!(row is Dictionary<string, object> data) || !data.TryGetValue("item", out var item) ||
                    !(item is string defName) || !data.TryGetValue("count", out var count) ||
                    !int.TryParse(Convert.ToString(count, CultureInfo.InvariantCulture), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int quantity)) return false;
                parsed.Add(new ItemAirdropTradeLine { DefName = defName, Count = quantity });
                if (parsed.Count > MaxLines) return false;
            }
            return TryNormalize(parsed, out lines);
        }

        public static string Summary(IEnumerable<ItemAirdropTradeLine> lines) => string.Join(", ",
            (lines ?? Enumerable.Empty<ItemAirdropTradeLine>()).Where(l => l != null).Select(l =>
                $"{(string.IsNullOrWhiteSpace(l.Label) ? l.DefName : l.Label)} x{l.Count}"));

        public static string Reference(IEnumerable<ItemAirdropTradeLine> lines) => string.Join("; ",
            lines.Select(l => $"item={l.DefName}, count={l.Count}, unit_value={l.UnitPrice.ToString("F2", CultureInfo.InvariantCulture)}"));
    }
}
