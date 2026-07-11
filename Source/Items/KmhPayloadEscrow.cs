using System.Collections.Generic;

namespace KMHServerAddon.Items
{
    // Shared payload-escrow helpers so every feature (auctions, wants, quests - marketplace has equivalent inline
    // versions from P3a) moves complex items the same way: pop exact units, refund to a treasury, strip blobs for
    // wire, count units. One system, no per-feature item formats.
    internal static class KmhPayloadEscrow
    {
        public static int TotalUnits(List<KmhThingPayload> escrow)
        {
            int n = 0;
            if (escrow != null) foreach (KmhThingPayload p in escrow) n += p?.StackCount ?? 0;
            return n;
        }

        public static KmhThingPayload Clone(KmhThingPayload p, int stackCount) => new KmhThingPayload
        {
            SchemaVersion = p.SchemaVersion, DefName = p.DefName, StuffDefName = p.StuffDefName, StackCount = stackCount,
            HitPoints = p.HitPoints, MaxHitPoints = p.MaxHitPoints, Quality = p.Quality, Tainted = p.Tainted,
            ScribeXml = p.ScribeXml, Fidelity = p.Fidelity, DisplayLabel = p.DisplayLabel, MarketValue = p.MarketValue,
            Fingerprint = p.Fingerprint, Legacy = p.Legacy, Warnings = new List<string>(p.Warnings ?? new List<string>()),
        };

        // Pop up to `qty` units off an escrow list (mutates it). Blob instances are atomic; metadata stacks split.
        public static List<KmhThingPayload> PopUnits(List<KmhThingPayload> escrow, int qty)
        {
            List<KmhThingPayload> outp = new List<KmhThingPayload>();
            if (escrow == null) return outp;
            int rem = qty;
            foreach (KmhThingPayload e in new List<KmhThingPayload>(escrow))
            {
                if (rem <= 0) break;
                if (e.StackCount <= rem) { outp.Add(e); escrow.Remove(e); rem -= e.StackCount; }
                else if (string.IsNullOrEmpty(e.ScribeXml)) { outp.Add(Clone(e, rem)); e.StackCount -= rem; rem = 0; }
            }
            return outp;
        }

        // Deposit every escrow payload into a user's treasury (cancel/expire/void refund, or winner delivery). If a
        // deposit can't land (invalid/deleted owner, disbanded guild, unusable payload) the item is PARKED in the
        // recovery queue instead of vanishing - a headless server can't drop pods, so silent loss was the alternative.
        public static void RefundTo(string username, IEnumerable<KmhThingPayload> escrow, string note)
        {
            if (escrow == null) return;
            foreach (KmhThingPayload p in escrow) Deliver(username, p, note, note);
        }

        // Deposit one payload to a user; on failure hold it in the recovery queue. Returns true only when it landed.
        public static bool Deliver(string username, KmhThingPayload payload, string source, string note)
        {
            if (payload == null) return false;
            if (Features.Treasury.TreasuryStore.DepositPayload(username, payload, note)) return true;
            Features.Recovery.RecoveryStore.HoldItem(username, payload, source ?? note ?? "", "treasury deposit failed (invalid owner or payload)");
            return false;
        }

        // Wire copy with the deep blob removed (metadata only for display); never send scribe_xml in a snapshot.
        public static List<KmhThingPayload> StripBlobs(List<KmhThingPayload> src)
        {
            List<KmhThingPayload> outList = new List<KmhThingPayload>();
            if (src == null) return outList;
            foreach (KmhThingPayload p in src) { KmhThingPayload c = Clone(p, p.StackCount); c.ScribeXml = ""; outList.Add(c); }
            return outList;
        }

        // Compact "(plasteel, q5, tainted, legacy)" note for a payload stack.
        public static string StateNote(KmhThingPayload p) => KmhItemSafety.DescribeStateForLedger(p);
    }
}
