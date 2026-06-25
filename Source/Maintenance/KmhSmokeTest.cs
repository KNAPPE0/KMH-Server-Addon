using System;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Admin smoketest: fast read-only checks for handlers, data paths, and release-killer wiring issues.
    internal static class KmhSmokeTest
    {
        // Every client->server request kind that must have a live server handler, or that feature is silently dead.
        private static readonly (string Kind, string Feature)[] RequiredHandlers =
        {
            (KmhProtocol.Kind.PlayerStatsRequest,      "Standings"),
            (KmhProtocol.Kind.ColonyReport,            "Colony report intake"),
            (KmhProtocol.Kind.ColonistRequest,         "Colonist profile"),
            (KmhProtocol.Kind.ColonistRosterRequest,   "Colonist roster"),
            (KmhProtocol.Kind.SeasonArchiveRequest,    "Season archive"),
            (KmhProtocol.Kind.TreasuryRequest,         "Treasury"),
            (KmhProtocol.Kind.MarketplaceRequest,      "Marketplace"),
            (KmhProtocol.Kind.QuestRequest,            "Quest board"),
            (KmhProtocol.Kind.GuildRequest,            "Guilds"),
            (KmhProtocol.Kind.GuildLeaderboardRequest, "Guild leaderboard"),
            (KmhProtocol.Kind.AuctionRequest,          "Auctions"),
            (KmhProtocol.Kind.WantRequest,             "Want board"),
            (KmhProtocol.Kind.WorldRequest,            "World engine"),
            (KmhProtocol.Kind.SiteRequest,             "Sites"),
            (KmhProtocol.Kind.ReputationRequest,       "Reputation"),
            (KmhProtocol.Kind.LinkedAccountsRequest,   "Linked accounts"),
            (KmhProtocol.Kind.ItemLabels,              "Item catalog intake"),
            (KmhProtocol.Kind.EnforcementSnapshotRequest, "Config enforcement"),
        };

        public static void Run(Action<string> reply)
        {
            int pass = 0, fail = 0;
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try
                {
                    (bool ok, string detail) = probe();
                    if (ok) { pass++; reply($"  PASS  {name}{(string.IsNullOrEmpty(detail) ? "" : $" - {detail}")}"); }
                    else    { fail++; reply($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : $" - {detail}")}"); }
                }
                catch (Exception ex) { fail++; reply($"  FAIL  {name} - threw: {ex.Message}"); }
            }

            reply("=== KMH smoke test (non-mutating) ===");

            Check("Protocol version", () =>
                (!string.IsNullOrEmpty(KmhProtocol.BuildVersion) && KmhProtocol.CurrentVersion > 0,
                 $"wire v{KmhProtocol.CurrentVersion}, build {KmhProtocol.BuildVersion}"));

            Check("Storage read/write", () =>
                (Persistence.JsonFileStore.SelfTest(out string d), d));

            Check("Data integrity", () =>
            {
                Persistence.KmhDataIntegrity.ScanResult r = Persistence.KmhDataIntegrity.Scan();
                return (!r.CriticalDamage, r.Summary);
            });

            // Live handler coverage - the runtime half of the protocol contract check.
            foreach ((string kind, string feature) in RequiredHandlers)
                Check($"Handler: {feature}", () =>
                    (KmhRouter.IsRegistered(kind), KmhRouter.IsRegistered(kind) ? kind : $"NO handler for {kind}"));

            // Snapshot builders must execute without throwing and return data (the server half of request/response).
            Check("Standings snapshot", () =>
            {
                var s = Features.PlayerStats.PlayerStatsStore.BuildSnapshot();
                return (s?.Entries != null, $"{s?.Entries?.Count ?? 0} player(s)");
            });
            Check("Marketplace snapshot", () =>
            {
                var s = Features.Marketplace.MarketplaceStore.BuildSnapshot(null);
                return (s?.Listings != null, $"{s?.Listings?.Count ?? 0} listing(s)");
            });
            Check("Quest snapshot", () =>
            {
                var s = Features.Quests.QuestStore.BuildSnapshot(null);
                return (s?.Quests != null, $"{s?.Quests?.Count ?? 0} quest(s)");
            });
            Check("Auction snapshot", () =>
            {
                var s = Features.Auctions.AuctionStore.BuildSnapshot(null);
                return (s?.Auctions != null, $"{s?.Auctions?.Count ?? 0} auction(s)");
            });
            Check("Want snapshot", () =>
            {
                var s = Features.WantBoard.WantStore.BuildSnapshot(null);
                return (s?.Wants != null, $"{s?.Wants?.Count ?? 0} want(s)");
            });
            Check("Sites snapshot", () =>
            {
                var s = Features.Sites.SiteStore.BuildSnapshotFor("");
                return (s?.Sites != null, $"{s?.Sites?.Count ?? 0} site(s)");
            });
            Check("Guilds", () => (true, $"{Features.Guilds.GuildStore.ListGuilds().Count} guild(s)"));
            Check("World engine", () =>
                (true, $"{Features.World.WorldStore.ActiveEvents().Count} event(s), {Features.World.WorldStore.ActiveQuests().Count} global quest(s)"));

            // Economy invariant: the house pool can never be negative.
            Check("Economy invariant", () =>
            {
                long pool = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                return (pool >= 0, $"house pool {Util.SilverFmt.Format(pool)}");
            });

            // Discord must be queryable whether on, off, or failed - "Discord down doesn't break the server".
            Check("Discord bridge safe", () =>
                (true, Features.Discord.DiscordBridge.DescribeStatus()));

            reply(fail == 0
                ? $"=== RESULT: PASS ({pass}/{pass + fail} checks) ==="
                : $"=== RESULT: FAIL ({fail} of {pass + fail} checks failed - see above) ===");
        }
    }
}
