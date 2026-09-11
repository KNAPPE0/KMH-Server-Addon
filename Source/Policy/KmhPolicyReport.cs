using System.Collections.Generic;
using K = KMHServerAddon.Policy.KmhPolicyKeys;

namespace KMHServerAddon.Policy
{
    internal static class KmhPolicyReport
    {
        private static readonly string[] Systems =
        {
            K.System.PersonalTreasury, K.System.GuildTreasury, K.System.Marketplace, K.System.Auctions,
            K.System.WantBoard, K.System.Sites, K.System.Quests, K.System.WorldEvents, K.System.Delivery, K.System.Recovery,
        };

        private static readonly string[] Shown =
        {
            K.Enabled, K.Access, K.AllowRemote, K.FeePercent, K.MaxPerTransaction, K.CooldownSeconds, K.BlockedDuringRaid,
        };

        public static List<string> Describe(string profile, IDictionary<string, IDictionary<string, object>> ownerOverrides = null)
        {
            var lines = new List<string>
            {
                $"KMH policies (profile: {(KmhProfiles.IsKnown(profile) ? profile : KmhProfiles.Balanced + " [fallback]")})",
                "  NOTE: recorded preferences only - no feature reads these yet. The live economy is governed by",
                "  Config/Economy.json (see 'kmh config economy').",
            };

            foreach (string system in Systems)
            {
                IDictionary<string, object> owner = null;
                ownerOverrides?.TryGetValue(system, out owner);
                owner = owner ?? KmhPolicyStore.Overrides(system);   // persisted owner edits show as [Owner]

                ResolvedPolicy p = KmhPolicyBook.Resolve(system, profile, owner);
                lines.Add($"  {system}:");
                foreach (string key in Shown)
                    lines.Add($"    {key,-18} = {Fmt(p.Get(key)),-12} [{p.OriginOf(key)}]");
            }
            return lines;
        }

        private static string Fmt(object v)
        {
            if (v is bool b) return b ? "yes" : "no";
            if (v is long l) return l < 0 ? "unlimited" : l.ToString();
            return v?.ToString() ?? "-";
        }
    }
}
