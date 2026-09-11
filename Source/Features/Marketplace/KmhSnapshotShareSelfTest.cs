using System.Collections.Generic;

namespace KMHServerAddon.Features.Marketplace
{
    // One snapshot per share key is sound only while a shared key implies identical bytes; a null key means build alone.
    internal static class KmhSnapshotShareSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            var noneHidden = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var amySells   = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "amy" };

            r.Add(("Share key: a guild-only seller is built alone",
                   Guilds.GuildVisibility.SnapshotShareKey("amy", amySells) == null, ""));
            r.Add(("Share key: match is case-insensitive on the seller",
                   Guilds.GuildVisibility.SnapshotShareKey("AMY", amySells) == null, ""));

            string k1 = Guilds.GuildVisibility.SnapshotShareKey("bob", noneHidden);
            string k2 = Guilds.GuildVisibility.SnapshotShareKey("carl", noneHidden);
            r.Add(("Share key: same guild shares one payload", k1 != null && k1 == k2, $"{k1} / {k2}"));

            r.Add(("Share key: blank caller never shares",
                   Guilds.GuildVisibility.SnapshotShareKey("", noneHidden) == null
                   && Guilds.GuildVisibility.SnapshotShareKey(null, noneHidden) == null, ""));

            // Safe only because the caller supplies the set, so a null one still keys by guild.
            r.Add(("Share key: null seller set still yields a guild key",
                   Guilds.GuildVisibility.SnapshotShareKey("bob", null) != null, ""));

            HashSet<string> derived = MarketplaceStore.SellersWithGuildOnlyListings();
            r.Add(("Share key: seller set is derived, never stored", derived != null, $"{derived.Count} seller(s)"));

            // A hook decides per viewer for reasons core cannot see, so sharing fails closed while one is registered.
            r.Add(("Share key: a visibility hook disables sharing entirely",
                   MarketplaceStore.ShareKeyForCore("bob", noneHidden, visibilityHooks: true) == null, ""));
            r.Add(("Share key: with no visibility hook, sharing is unchanged",
                   MarketplaceStore.ShareKeyForCore("bob", noneHidden, visibilityHooks: false) == k1
                   && MarketplaceStore.ShareKeyForCore("bob", noneHidden, visibilityHooks: false) != null, ""));
            r.Add(("Share key: a hook cannot resurrect sharing for a hidden seller",
                   MarketplaceStore.ShareKeyForCore("amy", amySells, visibilityHooks: true) == null
                   && MarketplaceStore.ShareKeyForCore("amy", amySells, visibilityHooks: false) == null, ""));

            // Crediting "" fails the deposit and discards the payout with no record of it.
            r.Add(("Marketplace: a seller-less row refunds nothing",
                   MarketplaceStore.RefundableQty("", 40) == 0 && MarketplaceStore.RefundableQty(null, 40) == 0, ""));
            r.Add(("Marketplace: a real seller is refunded normally",
                   MarketplaceStore.RefundableQty("amy", 40) == 40, ""));
            r.Add(("Marketplace: a negative remainder never becomes a refund",
                   MarketplaceStore.RefundableQty("amy", -5) == 0, ""));

            return r;
        }
    }
}
