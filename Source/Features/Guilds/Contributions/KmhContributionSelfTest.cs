using System.Collections.Generic;

namespace KMHServerAddon.Features.Guilds.Contributions
{
    internal static class KmhContributionSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            KmhGuildContribution Silver(long a) => new KmhGuildContribution { Type = KmhContributionType.Silver, SilverValue = a };
            KmhGuildContribution Item(long v)   => new KmhGuildContribution { Type = KmhContributionType.Item, ItemValue = v };
            KmhGuildContribution Return(long a) => new KmhGuildContribution { Type = KmhContributionType.Return, SilverValue = a };
            KmhGuildContribution Adjust(long a) => new KmhGuildContribution { Type = KmhContributionType.Adjustment, SilverValue = a };

            var s1 = KmhContributions.Derive(new[] { Silver(100), Silver(50), Item(200) });
            r.Add(("Contrib: silver and item totals",
                s1.Silver == 150 && s1.ItemValue == 200 && s1.Count == 3 && s1.NetValue == 350, $"silver {s1.Silver}, items {s1.ItemValue}"));

            var s2 = KmhContributions.Derive(new[] { Silver(100), Return(30) });
            r.Add(("Contrib: returns reduce standing",
                s2.Silver == 70 && s2.Returned == 30, $"silver {s2.Silver}, returned {s2.Returned}"));

            var s3 = KmhContributions.Derive(new[] { Silver(20), Return(100) });
            r.Add(("Contrib: never below zero", s3.Silver == 0, $"silver {s3.Silver}"));

            var s4 = KmhContributions.Derive(new KmhGuildContribution[0]);
            var s5 = KmhContributions.Derive(null);
            r.Add(("Contrib: empty/null safe", s4.NetValue == 0 && s5.NetValue == 0, "zeroed"));

            var up = KmhContributions.Derive(new[] { Silver(100), Adjust(50) });
            var down = KmhContributions.Derive(new[] { Silver(100), Adjust(-40) });
            r.Add(("Contrib: adjustments are signed", up.Silver == 150 && down.Silver == 60, $"up {up.Silver}, down {down.Silver}"));

            return r;
        }
    }
}
