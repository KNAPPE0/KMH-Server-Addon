using System.Collections.Generic;

namespace KMHServerAddon.Items
{
    // One home for payload copy and split: a duplicated clone silently drops any field added to only one copy.
    internal static class KmhItemService
    {
        // Warnings is deep-copied, or edits to a clone's list would reach the source it was copied from.
        public static KmhThingPayload ClonePayload(KmhThingPayload p, int stackCount) => new KmhThingPayload
        {
            SchemaVersion = p.SchemaVersion, DefName = p.DefName, StuffDefName = p.StuffDefName, StackCount = stackCount,
            HitPoints = p.HitPoints, MaxHitPoints = p.MaxHitPoints, Quality = p.Quality, Tainted = p.Tainted,
            ScribeXml = p.ScribeXml, Fidelity = p.Fidelity, DisplayLabel = p.DisplayLabel, MarketValue = p.MarketValue,
            Fingerprint = p.Fingerprint, Legacy = p.Legacy, Warnings = new List<string>(p.Warnings ?? new List<string>()),
            Mergeable = p.Mergeable, Splittable = p.Splittable, RotProgressTicks = p.RotProgressTicks,
        };

        // Fidelity still records that a blob existed, so a stripped copy is not mistaken for metadata-only capture.
        public static KmhThingPayload CloneWithoutBlob(KmhThingPayload p)
        {
            KmhThingPayload c = ClonePayload(p, p.StackCount);
            c.ScribeXml = "";
            return c;
        }

        public enum TakeKind { Whole, Split, Skip }

        // A partial take from an atomic stack is refused rather than shrunk, because splitting it would duplicate saved state.
        public static TakeKind PlanTake(int have, int take, bool splittable)
        {
            if (take <= 0 || have <= 0) return TakeKind.Skip;
            if (take >= have) return TakeKind.Whole;
            return splittable ? TakeKind.Split : TakeKind.Skip;
        }
    }
}
