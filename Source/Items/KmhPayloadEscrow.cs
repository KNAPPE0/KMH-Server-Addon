using System.Collections.Generic;

namespace KMHServerAddon.Items
{
    // Split and copy live in KmhItemService, so escrow and a treasury withdraw divide stacks identically.
    internal static class KmhPayloadEscrow
    {
        public static int TotalUnits(List<KmhThingPayload> escrow)
        {
            int n = 0;
            if (escrow != null) foreach (KmhThingPayload p in escrow) n += p?.StackCount ?? 0;
            return n;
        }

        public static KmhThingPayload Clone(KmhThingPayload p, int stackCount) => KmhItemService.ClonePayload(p, stackCount);

        // Dividing one captured stack is a weaker claim than merging two, because RimWorld already held these units together.
        public static bool IsSplittable(KmhThingPayload p)
            => p != null && (string.IsNullOrEmpty(p.ScribeXml) || p.Splittable || p.Mergeable);

        // A caller that charges for a fill must size the charge with this, or it bills for goods an atomic stack never hands over.
        public static int DeliverableUnits(IEnumerable<KmhThingPayload> taken, int want)
        {
            if (taken == null || want <= 0) return 0;
            int owed = want, delivered = 0;
            foreach (KmhThingPayload p in taken)
            {
                if (p == null || owed <= 0) continue;
                if (owed >= p.StackCount) { delivered += p.StackCount; owed -= p.StackCount; }
                else if (IsSplittable(p))  { delivered += owed; owed = 0; }
                // else: atomic stack larger than what's owed - it cannot contribute at all.
            }
            return delivered;
        }

        // Same take plan as a treasury withdraw, so both paths keep the identical stacks atomic.
        public static List<KmhThingPayload> PopUnits(List<KmhThingPayload> escrow, int qty)
        {
            List<KmhThingPayload> outp = new List<KmhThingPayload>();
            if (escrow == null) return outp;
            int rem = qty;
            foreach (KmhThingPayload e in new List<KmhThingPayload>(escrow))
            {
                if (rem <= 0) break;
                switch (KmhItemService.PlanTake(e.StackCount, rem, IsSplittable(e)))
                {
                    case KmhItemService.TakeKind.Whole: outp.Add(e); escrow.Remove(e); rem -= e.StackCount; break;
                    case KmhItemService.TakeKind.Split: outp.Add(Clone(e, rem)); e.StackCount -= rem; rem = 0; break;
                }
            }
            return outp;
        }

        public static void RefundTo(string username, IEnumerable<KmhThingPayload> escrow, string note)
        {
            if (escrow == null) return;
            foreach (KmhThingPayload p in escrow) Deliver(username, p, note, note);
        }

        // A headless server cannot drop pods, so a deposit that will not land is parked in recovery rather than lost.
        public static bool Deliver(string username, KmhThingPayload payload, string source, string note)
        {
            if (payload == null) return false;
            if (Features.Treasury.TreasuryStore.DepositPayload(username, payload, note)) return true;
            Features.Recovery.RecoveryStore.HoldItem(username, payload, source ?? note ?? "", "treasury deposit failed (invalid owner or payload)");
            return false;
        }

        // Every feature routes owed compact goods through here, so a buyer never pays and receives nothing.
        public static bool DeliverCompact(string username, string itemKey, int qty, string source, string reason)
        {
            if (qty <= 0 || string.IsNullOrEmpty(itemKey)) return true;
            if (Features.Treasury.TreasuryStore.DepositItem(username, itemKey, qty, source)) return true;
            Util.ItemKey.Split(itemKey, out string def, out string stuff, out int q);
            Features.Recovery.RecoveryStore.HoldItem(username,
                KmhItemSafety.MarkLegacyPartial(def, stuff, q, qty, def), source ?? "", reason ?? "delivery failed");
            return false;
        }

        // DepositSilver refuses an empty owner, so a payout read off a malformed row is held in recovery rather than vanishing.
        public static bool DeliverSilver(string username, long amount, string source, string reason)
        {
            if (amount <= 0) return true;
            long remaining = amount;
            while (remaining > 0)
            {
                int chunk = (int)System.Math.Min(remaining, int.MaxValue);
                if (!Features.Treasury.TreasuryStore.DepositSilver(username, chunk, source)) break;
                remaining -= chunk;
            }
            if (remaining <= 0) return true;
            Features.Recovery.RecoveryStore.HoldSilver(username, remaining, source ?? "", reason ?? "silver delivery failed");
            return false;
        }

        public static List<KmhThingPayload> StripBlobs(List<KmhThingPayload> src)
        {
            List<KmhThingPayload> outList = new List<KmhThingPayload>();
            if (src == null) return outList;
            foreach (KmhThingPayload p in src) outList.Add(KmhItemService.CloneWithoutBlob(p));
            return outList;
        }

        public static string StateNote(KmhThingPayload p) => KmhItemSafety.DescribeStateForLedger(p);
    }
}
