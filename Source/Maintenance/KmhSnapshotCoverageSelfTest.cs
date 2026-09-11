using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // A store missing from the player snapshot understates what that player holds.
    internal static class KmhSnapshotCoverageSelfTest
    {
        // Value-bearing keys are marked so a future edit sees the stakes.
        private static readonly string[] RequiredKeys =
        {
            "standings", "colonist", "roster", "reputation",
            "treasury",             // value
            "guild", "guild_invites",
            "marketplace_listings", // value (escrowed listings)
            "auctions",             // value (escrowed bids/items)
            "wants",                // value (escrowed silver)
            "quests",               // value (escrowed bounty)
            "sites",
            "roadworks",            // value (reserved project escrow)
            "notifications",
            "player_mail",          // value (escrowed attachments, both directions)
            "recovery",             // value (parked undeliverable goods)
            "blocks", "linked_account", "guild_contributions", "ledger",
        };

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var results = new List<(string, bool, string)>();

            // A username that cannot exist, or the smoketest leaves a permanent empty row behind.
            const string probe = ProbeUser;
            List<string> included;
            try { included = KmhSnapshot.PlayerStoreKeys(probe); }
            catch (Exception ex)
            {
                results.Add(("Snapshot: player gather runs", false, ex.GetType().Name + ": " + ex.Message));
                Cleanup(probe);
                return results;
            }

            // Captured before the cleanup, or it proves the cleanup works rather than that the gather is clean.
            bool gatherCreatedAVault = Features.Treasury.TreasuryStore.HasVaultForUser(probe);
            Cleanup(probe);

            results.Add(("Snapshot: player gather runs", included != null, $"{included?.Count ?? 0} store(s) gathered"));
            results.Add(("Snapshot: reading a player record creates nothing", !gatherCreatedAVault,
                         gatherCreatedAVault ? "the gather left a vault behind - reading must not create" : probe));
            if (included == null) return results;

            var missing = RequiredKeys.Where(k => !included.Contains(k, StringComparer.Ordinal)).ToList();
            results.Add(("Snapshot: player record covers every store", missing.Count == 0,
                missing.Count == 0 ? $"{RequiredKeys.Length} required key(s) present"
                                   : "MISSING from the player snapshot: " + string.Join(", ", missing)));

            // "mail" once meant the notice queue - reusing it would change what archived records mean.
            results.Add(("Snapshot: no ambiguous 'mail' key", !included.Contains("mail", StringComparer.Ordinal),
                included.Contains("mail", StringComparer.Ordinal)
                    ? "'mail' is ambiguous - use notifications / player_mail"
                    : "notifications + player_mail are distinct"));

            return results;
        }

        // Stable, and carries the marker the boot sweep uses to clear rows left by the old random-name build.
        internal const string ProbeUser = "__kmh_snapshot_probe__diag";

        private static void Cleanup(string probe)
            => Features.Treasury.TreasuryStore.TryRemoveEmptyVault(
                   Features.Treasury.TreasuryStore.ResolveOwnerKeyFor(probe));
    }
}
