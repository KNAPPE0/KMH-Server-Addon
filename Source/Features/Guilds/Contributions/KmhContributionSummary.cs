using System.Collections.Generic;

namespace KMHServerAddon.Features.Guilds.Contributions
{
    // Derived rather than stored, so it can be rebuilt from the ledger at any time.
    internal readonly struct KmhContributionSummary
    {
        public readonly long Silver;       // net of returns
        public readonly long ItemValue;    // net of returns
        public readonly long Returned;
        public readonly int  Count;        // excludes returns and adjustments

        public KmhContributionSummary(long silver, long itemValue, long returned, int count)
        { Silver = silver; ItemValue = itemValue; Returned = returned; Count = count; }

        public long NetValue => Silver + ItemValue;
    }

    // Folds the whole history, so what the guild later spends can never change a member's standing.
    internal static class KmhContributions
    {
        public static KmhContributionSummary Derive(IEnumerable<KmhGuildContribution> records)
        {
            long silver = 0, itemValue = 0, returned = 0;
            int count = 0;
            if (records != null)
                foreach (KmhGuildContribution c in records)
                {
                    if (c == null) continue;
                    switch (c.Type)
                    {
                        case KmhContributionType.Silver:
                            silver += c.SilverValue; count++; break;
                        case KmhContributionType.Item:
                            itemValue += c.ItemValue; count++; break;
                        case KmhContributionType.Return:
                            silver     -= c.SilverValue;
                            itemValue  -= c.ItemValue;
                            returned   += c.SilverValue + c.ItemValue; break;
                        case KmhContributionType.Adjustment:
                            silver    += c.SilverValue;      // signed: an owner may add or subtract
                            itemValue += c.ItemValue; break;
                    }
                }
            if (silver < 0) silver = 0;
            if (itemValue < 0) itemValue = 0;
            return new KmhContributionSummary(silver, itemValue, returned, count);
        }
    }
}
