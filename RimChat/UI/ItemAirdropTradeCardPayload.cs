using System;
using System.Collections.Generic;
using RimChat.DiplomacySystem;
using System.Globalization;
using Verse;

namespace RimChat.UI
{
    public class ItemAirdropTradeCardPayload
    {
        public string Need { get; set; }
        public int RequestedCount { get; set; }
        public string OfferItemDefName { get; set; }
        public string OfferItemLabel { get; set; }
        public int OfferItemCount { get; set; }
        public string Scenario { get; set; } = "trade";
        public string RequestId { get; set; }
        public bool IsRevision { get; set; }
        public List<ItemAirdropTradeLine> NeedItems { get; set; } = new List<ItemAirdropTradeLine>();
        public List<ItemAirdropTradeLine> PaymentItems { get; set; } = new List<ItemAirdropTradeLine>();

        public string NeedDefName { get; set; }
        public string NeedLabel { get; set; }
        public string NeedSearchText { get; set; }
        public float NeedUnitPrice { get; set; }
        public float NeedReferenceTotalPrice { get; set; }
        public float OfferUnitPrice { get; set; }
        public float OfferTotalPrice { get; set; }
        public int ShippingPodCount { get; set; }
        public int ShippingCostSilver { get; set; }

        public bool HasBoundNeed => (NeedItems?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(NeedDefName);

        public string GetNeedReferenceText()
        {
            if ((NeedItems?.Count ?? 0) > 0) return ItemAirdropBasket.Summary(NeedItems);
            int requestedCount = Math.Max(1, RequestedCount);
            if (!string.IsNullOrWhiteSpace(NeedLabel))
            {
                return $"{NeedLabel} x{requestedCount}";
            }

            if (!string.IsNullOrWhiteSpace(Need))
            {
                return Need;
            }

            if (!string.IsNullOrWhiteSpace(NeedDefName))
            {
                return $"{NeedDefName} x{requestedCount}";
            }

            return string.Empty;
        }

        public string ToVisibleSummary()
        {
            if ((NeedItems?.Count ?? 0) > 0) return "RimChat_AirdropBasketSummary".Translate(
                ItemAirdropBasket.Summary(NeedItems), ItemAirdropBasket.Summary(PaymentItems)).ToString();
            string offerLabel = string.IsNullOrWhiteSpace(OfferItemLabel)
                ? (OfferItemDefName ?? string.Empty)
                : OfferItemLabel;
            string needDisplay = string.IsNullOrWhiteSpace(NeedLabel) ? (NeedDefName ?? Need ?? string.Empty) : NeedLabel;
            return "RimChat_AirdropTradeCardSubmitSummary".Translate(
                needDisplay,
                Math.Max(1, RequestedCount),
                offerLabel,
                Math.Max(1, OfferItemCount)).ToString();
        }
    }
}
