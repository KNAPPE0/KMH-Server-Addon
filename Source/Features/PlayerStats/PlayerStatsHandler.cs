using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.PlayerStats
{
    // Server-side handler for kmh.player_stats.* (counterpart to the client's PlayerStatsHandler). Answers requests
    // from PlayerStatsStore; the client's 8s auto-refresh covers updates until unsolicited push is wired.
    internal static class PlayerStatsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.PlayerStatsRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonyReport,       OnColonyReport);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonistRequest,    OnColonistRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonistRosterRequest, OnColonistRosterRequest);
        }

        // The Colonist Records board asks for the flattened roster of every colony's colonists.
        private static void OnColonistRosterRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ColonistRoster, PlayerStatsStore.BuildColonistRoster());
        }

        // Optional: broadcast a fresh snapshot to every connected verified client. Called from feature code that
        // mutates the store. (Wired up when the first mutating feature lands.)
        public static void BroadcastSnapshot()
        {
            PlayerStatsSnapshotAll();
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            // Always EnsurePlayer the requester first so a brand-new player who just connected and clicked Player
            // Leaderboard sees themselves in the list rather than an empty board
            PlayerStatsStore.EnsurePlayer(client?.GetData<UserFile>()?.Username);

            Dto.PlayerStatsSnapshot snapshot = PlayerStatsStore.BuildSnapshot();
            KmhRouter.SendTo(client, KmhProtocol.Kind.PlayerStatsSnapshot, snapshot);
            ServerLog.Verbose($"Sent player_stats.snapshot ({snapshot.Entries.Count} entries) to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }

        // Per-user rate limit so a modified client can't spam reports to force constant disk writes / work.
        private static readonly object _reportGate = new object();
        private static readonly Dictionary<string, long> _lastReportUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly long MinReportIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        // Client uploaded its colony summary + colonist. Attribute to the authenticated session, never the envelope.
        private static void OnColonyReport(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            long now = DateTime.UtcNow.Ticks;
            lock (_reportGate)
            {
                if (_lastReportUtcTicks.TryGetValue(username, out long last) && now - last < MinReportIntervalTicks) return;
                _lastReportUtcTicks[username] = now;
            }

            Dto.ColonyReport report = env?.DataAs<Dto.ColonyReport>();
            if (report == null) return;
            PlayerStatsStore.ApplyColonyReport(username, report);

            // Anti-exploit: if the player started a new save, clear their treasury so they can't farm starting
            // resources by depositing, resetting, and repeating. Opt-in; backs up first (see EconomyConfig).
            if (Features.Economy.EconomyConfig.Current.ResetEconomyOnNewSave
                && Features.Economy.EconomyResetStore.RecordAndDetectReset(username, report.SaveId))
                AutoResetEconomy(client, username);
            // Deliberately NO broadcast: open leaderboards auto-refresh on their own ~8s tick and on open.
            // Broadcasting on every client-timed report would amplify one client's uploads into a full snapshot
            // pushed to everyone - a needless fan-out / DoS lever.
        }

        // Back up, then clear the player's personal treasury (plus a solo guild's vault) and refresh their view.
        private static void AutoResetEconomy(ServerClient client, string username)
        {
            Features.Treasury.Dto.TreasurySnapshot snap = Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            // A solo guild's vault is a shelter from this reset, so clear it too.
            string soloGuild       = Features.Guilds.GuildStore.SoloGuildOf(username);
            long   soloGuildSilver = soloGuild != null ? Features.Treasury.TreasuryStore.GetGuildSilver(soloGuild) : 0;

            // Escrowed value (marketplace/auction/want) lives outside the treasury and would otherwise shelter the reset.
            bool personalEmpty = snap.SilverBalance <= 0 && (snap.Items == null || snap.Items.Count == 0);
            bool hasEscrow = Features.Marketplace.MarketplaceStore.HasSellerListings(username)
                          || Features.Auctions.AuctionStore.HasUserActivity(username)
                          || Features.WantBoard.WantStore.HasBuyerWants(username);
            if (personalEmpty && soloGuildSilver <= 0 && !hasEscrow) return; // nothing to clear

            if (Persistence.KmhDataBackup.TryCreate("pre-auto-economy-reset", out string dir, out _))
                ServerLog.Verbose($"Auto economy reset backup: {System.IO.Path.GetFileName(dir)}");
            Features.Treasury.TreasuryStore.ResetPersonal(username);
            if (soloGuild != null) Features.Treasury.TreasuryStore.ClearGuildVault(soloGuild);
            int mpPurged = Features.Marketplace.MarketplaceStore.PurgeSeller(username);
            var (auRemoved, auRetracted) = Features.Auctions.AuctionStore.PurgeUser(username);
            int wantPurged = Features.WantBoard.WantStore.PurgeBuyer(username);
            // Sites: the old save's pawns no longer exist, so remove owned sites + every worker slot (guild-owned sites
            // stay; this player just stops working them). Old-save recovery value is cleared too (logged; backup above).
            var (sitesRemoved, _) = Features.Sites.SiteStore.PurgeOwner(username, dryRun: false);
            int workerSlots = Features.Sites.SiteStore.RemoveWorkerEverywhere(username);
            int recCleared  = Features.Recovery.RecoveryStore.ClearUser(username);
            ServerLog.Warn($"Auto economy reset for {username} - new save detected, treasury cleared (personal {snap.SilverBalance}s"
                           + (soloGuild != null ? $", solo guild '{soloGuild}' {soloGuildSilver}s" : "")
                           + $") + escrows purged, {sitesRemoved} owned site(s) removed, {workerSlots} worker slot(s) cleared, {recCleared} recovery record(s) cleared. Guild membership/donations kept.");

            // Refresh the caller's treasury; broadcast only the shared boards that actually changed.
            if (client != null)
            {
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(username));
                KmhRouter.Notify(client, "neutral", "New save detected - your KMH economy was reset: treasury, listings, sites and worker slots from the old save were cleared (guild membership and past donations kept).");
            }
            if (mpPurged > 0)                       Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
            if (auRemoved + auRetracted > 0)        Features.Auctions.AuctionHandler.BroadcastSnapshot();
            if (wantPurged > 0)                     Features.WantBoard.WantHandler.BroadcastSnapshot();
            if (sitesRemoved + workerSlots > 0)     Features.Sites.SiteHandler.BroadcastSnapshot();
        }

        // A client opened someone's card - send that player's full colonist profile (or an empty one if none).
        private static void OnColonistRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            string target = env?.GetString("username");
            if (string.IsNullOrEmpty(target)) return;
            Dto.ColonistProfile detail = PlayerStatsStore.GetColonist(target);
            KmhRouter.SendTo(client, KmhProtocol.Kind.ColonistProfile,
                new Dto.ColonistProfileEnvelope { Username = target, Detail = detail });
        }

        // Push the current snapshot to clients currently viewing standings (built once, sent to interested only).
        private static void PlayerStatsSnapshotAll()
        {
            Dto.PlayerStatsSnapshot snapshot = PlayerStatsStore.BuildSnapshot();
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.PlayerStatsSnapshot, _ => snapshot);
        }
    }
}
