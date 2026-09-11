using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.PlayerStats
{
    internal static class PlayerStatsHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.PlayerStatsRequest, OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonyReport,       OnColonyReport);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonistRequest,    OnColonistRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ColonistRosterRequest, OnColonistRosterRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.PlayerActive,        OnActive);
        }

        // A claim about a window the server measured itself, so the store decides how much of it the clock allows.
        private static void OnActive(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            PlayerStatsStore.EnsurePlayer(username);
            int claimed = env?.GetInt("seconds", 0) ?? 0;
            PlayerStatsStore.AddActiveSeconds(username, claimed, out long credited);
            PlayerStatsStore.RollSession(username, ending: false);
            if (credited > 0) ServerLog.Verbose($"{username}: +{credited}s active.");
        }

        private static void OnColonistRosterRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.ColonistRoster, PlayerStatsStore.BuildColonistRoster());
        }

        // For feature code that mutates the store and wants everyone to see it before the next auto-refresh.
        public static void BroadcastSnapshot()
        {
            PlayerStatsSnapshotAll();
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            // Or a brand-new player opening the board sees an empty list rather than themselves.
            PlayerStatsStore.EnsurePlayer(client?.GetData<UserFile>()?.Username);

            Dto.PlayerStatsSnapshot snapshot = PlayerStatsStore.BuildSnapshot();
            KmhRouter.SendTo(client, KmhProtocol.Kind.PlayerStatsSnapshot, snapshot);
            ServerLog.Verbose($"Sent player_stats.snapshot ({snapshot.Entries.Count} entries) to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }

        // Per-user rate limit so a modified client can't spam reports to force constant disk writes / work.
        private static readonly object _reportGate = new object();
        private static readonly Dictionary<string, long> _lastReportUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly long MinReportIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        // Attributed to the authenticated session, never to anything the envelope claims.
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

            // A new save clears the vault, or starting resources can be farmed by depositing and resetting.
            if (Features.Economy.EconomyConfig.Current.ResetEconomyOnNewSave
                && Features.Economy.EconomyResetStore.ShouldReset(username, report.SaveId, out _))
                AutoResetEconomy(client, username, report.SaveId);
            // No broadcast: it would amplify one client's report timer into a full snapshot pushed to everyone.
        }

        private static void AutoResetEconomy(ServerClient client, string username, string saveId)
        {
            Features.Treasury.Dto.TreasurySnapshot snap = Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            // A solo guild's vault is a shelter from this reset, so clear it too.
            string soloGuild       = Features.Guilds.GuildStore.SoloGuildOf(username);
            long   soloGuildSilver = soloGuild != null ? Features.Treasury.TreasuryStore.GetGuildSilver(soloGuild) : 0;

            // Escrowed value (marketplace/auction/want) lives outside the treasury and would otherwise shelter the reset.
            bool personalEmpty = snap.SilverBalance <= 0 && (snap.Items == null || snap.Items.Count == 0);
            bool hasEscrow = Features.Marketplace.MarketplaceStore.HasSellerListings(username)
                          || Features.Auctions.AuctionStore.HasUserActivity(username)
                          || Features.WantBoard.WantStore.HasBuyerWants(username)
                          || Features.Economy.KmhEscrowPurge.HasAny(username);
            if (personalEmpty && soloGuildSilver <= 0 && !hasEscrow)
            {
                Features.Economy.EconomyResetStore.ConfirmReset(username, saveId);
                return;
            }

            if (!Features.Economy.KmhEconomyReset.Begin(username, saveId, out string why))
            {
                ServerLog.Warn($"Auto economy reset for {username} refused - {why}. Nothing was destroyed.");
                return;
            }
            if (Persistence.KmhDataBackup.TryCreate("pre-auto-economy-reset", out string dir, out _))
                ServerLog.Verbose($"Auto economy reset backup: {System.IO.Path.GetFileName(dir)}");

            Features.Economy.KmhEconomyReset.Run(username, saveId);
            ServerLog.Warn($"Auto economy reset for {username} - new save detected, treasury cleared (personal {snap.SilverBalance}s"
                           + (soloGuild != null ? $", solo guild '{soloGuild}' {soloGuildSilver}s" : "")
                           + "), escrows purged, owned sites and worker slots removed, recovery records cleared. "
                           + "Guild membership/donations kept.");

            if (client != null)
            {
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(username));
                KmhRouter.Notify(client, "neutral", "New save detected - your KMH economy was reset: treasury, listings, sites and worker slots from the old save were cleared (guild membership and past donations kept).");
            }
            Features.Marketplace.MarketplaceHandler.BroadcastSnapshot();
            Features.Auctions.AuctionHandler.BroadcastSnapshot();
            Features.WantBoard.WantHandler.BroadcastSnapshot();
            Features.Sites.SiteHandler.BroadcastSnapshot();
            Features.Mail.MailHandler.SendSnapshotToUsername(username);
            Features.Quests.QuestHandler.BroadcastSnapshot();
        }

        private static void OnColonistRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            string target = env?.GetString("username");
            if (string.IsNullOrEmpty(target)) return;
            Dto.ColonistProfile detail = PlayerStatsStore.GetColonist(target);
            KmhRouter.SendTo(client, KmhProtocol.Kind.ColonistProfile,
                new Dto.ColonistProfileEnvelope { Username = target, Detail = detail });
        }

        // Built once and shared: every interested client gets the same snapshot object.
        private static void PlayerStatsSnapshotAll()
        {
            Dto.PlayerStatsSnapshot snapshot = PlayerStatsStore.BuildSnapshot();
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.PlayerStatsSnapshot, _ => snapshot);
        }
    }
}
