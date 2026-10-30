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
            ReplyIfChanged(client, KmhProtocol.Kind.ColonistRoster, () => ColonistRosterForClients(PlayerStatsStore.BuildColonistRoster()));
        }

        internal const int MaxFallenSent = 100;

        // Colonist Records are a ranking, so they hide whom the boards hide: banned players, and simulated ones while unranked.
        internal static Dto.ColonistRosterSnapshot ColonistRosterForClients(Dto.ColonistRosterSnapshot s)
        {
            Leaderboards.LeaderboardEngine.ForClients(s, Leaderboards.LeaderboardEngine.HiddenNow());
            if (s?.Fallen != null && s.Fallen.Count > MaxFallenSent) s.Fallen.RemoveRange(MaxFallenSent, s.Fallen.Count - MaxFallenSent);
            return s;
        }

        public static void BroadcastSnapshot()
        {
            PlayerStatsSnapshotAll();
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            // Or a brand-new player opening the board sees an empty list rather than themselves.
            PlayerStatsStore.EnsurePlayer(client?.GetData<UserFile>()?.Username);

            if (ReplyIfChanged(client, KmhProtocol.Kind.PlayerStatsSnapshot, () => ClientSnapshot()))
                ServerLog.Verbose($"Sent player_stats.snapshot to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }

        // The client window polls every 12 s, so a connection gets the list at most once a minute, and only if changed.
        internal static readonly TimeSpan MinResend = TimeSpan.FromSeconds(60);
        // An unchanged list still goes out this often, in case the last one never reached the window.
        internal static readonly TimeSpan MaxQuiet = TimeSpan.FromMinutes(10);
        private sealed class LastReply { public string Hash; public long AtTicks; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, Dictionary<string, LastReply>> _replies
            = new System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, Dictionary<string, LastReply>>();

        // Within the window nothing is built; after it, the same bytes as last time are not resent until MaxQuiet.
        internal static bool ShouldReply(string lastHash, long lastAtTicks, long nowTicks, Func<string> hashNow, out string hash)
        {
            hash = null;
            bool clockBack = nowTicks < lastAtTicks;
            if (lastHash != null && !clockBack && nowTicks - lastAtTicks < MinResend.Ticks) return false;
            hash = hashNow();
            return hash != lastHash || clockBack || nowTicks - lastAtTicks >= MaxQuiet.Ticks;
        }

        private static bool ReplyIfChanged(ServerClient client, string kind, Func<object> build)
        {
            if (client == null) return false;
            Dictionary<string, LastReply> mine = _replies.GetValue(client, _ => new Dictionary<string, LastReply>(StringComparer.Ordinal));
            LastReply last;
            lock (mine) mine.TryGetValue(kind, out last);
            long now = DateTime.UtcNow.Ticks;
            object payload = null;
            if (!ShouldReply(last?.Hash, last?.AtTicks ?? 0, now, () => HashOf(payload = build()), out string hash)) return false;
            if (!KmhRouter.SendTo(client, kind, payload)) return false;
            lock (mine) mine[kind] = new LastReply { Hash = hash, AtTicks = now };
            return true;
        }

        private static string HashOf(object payload)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                   System.Text.Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(payload))));

        // Per user, so a modified client cannot force constant disk writes by spamming reports.
        private static readonly object _reportGate = new object();
        private static readonly Dictionary<string, long> _lastReportUtcTicks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly long MinReportIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        internal static void ColonyReportForTest(ServerClient client, KmhEnvelope env) => OnColonyReport(client, env);

        // Attributed to the authenticated session, never to anything the envelope claims.
        private static void OnColonyReport(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            Dto.ColonyReport report = env?.DataAs<Dto.ColonyReport>();
            if (report == null) return;
            // A connection's first report is never rate-limited away: it is what releases the owed deliveries.
            bool firstOnLink = !Features.Delivery.DeliveryHandler.SaveKnownOn(client);

            long now = DateTime.UtcNow.Ticks;
            bool limited;
            lock (_reportGate)
            {
                limited = _lastReportUtcTicks.TryGetValue(username, out long last) && now - last < MinReportIntervalTicks;
                if (!limited) _lastReportUtcTicks[username] = now;
            }
            if (limited && !firstOnLink) return;
            if (!limited) PlayerStatsStore.ApplyColonyReport(username, report);

            // A new save clears the vault, or starting resources can be farmed by depositing and resetting.
            if (Features.Economy.EconomyConfig.Current.ResetEconomyOnNewSave
                && Features.Economy.EconomyResetStore.ShouldReset(username, report.SaveId, out _))
                AutoResetEconomy(client, username, report.SaveId);
            // Marked after the reset decision, so neither this nor a handshake replays into a new save first.
            if (firstOnLink && Features.Delivery.DeliveryHandler.MarkSaveKnown(client)) Features.Delivery.DeliveryHandler.ReplayOwed(client);
            // No broadcast: it would amplify one client's report timer into a full snapshot pushed to everyone.
        }

        private static void AutoResetEconomy(ServerClient client, string username, string saveId)
        {
            Features.Treasury.Dto.TreasurySnapshot snap = Features.Treasury.TreasuryStore.GetSnapshotFor(username);
            // A solo guild's vault is a shelter from this reset, so clear it too.
            string soloGuild       = Features.Guilds.GuildStore.SoloGuildOf(username);
            long   soloGuildSilver = soloGuild != null ? Features.Treasury.TreasuryStore.GetGuildSilver(soloGuild) : 0;

            // Before the reset is written down, or restoring this backup would find the reset open and run it again.
            long nowTicks = DateTime.UtcNow.Ticks;
            if (!Persistence.KmhDataBackup.AutoResetBackupDue(nowTicks))
                ServerLog.Verbose("Auto economy reset: the last auto-reset backup is under 10 minutes old - not copying KMH-Data again.");
            else if (Persistence.KmhDataBackup.TryCreate(Persistence.KmhDataBackup.AutoResetReason, out string dir, out _))
            {
                Persistence.KmhDataBackup.NoteAutoResetBackup(nowTicks);
                ServerLog.Verbose($"Auto economy reset backup: {System.IO.Path.GetFileName(dir)}");
            }

            // Run even for an empty vault: payouts, refunds and recovery records owed to the old save sit elsewhere.
            if (!Features.Economy.KmhEconomyReset.Begin(username, saveId, out string why))
            {
                ServerLog.Warn($"Auto economy reset for {username} refused - {why}. Nothing was destroyed.");
                return;
            }
            Features.Economy.KmhEconomyReset.Run(username, saveId);
            if (Features.Economy.KmhEconomyReset.Resetting(username))
            {
                ServerLog.Warn($"Auto economy reset for {username} is not finished - it is retried automatically, so the player was not told it ran.");
                return;
            }
            ServerLog.Warn($"Auto economy reset for {username} - new save detected, treasury cleared (personal {snap.SilverBalance}s"
                           + (soloGuild != null ? $", solo guild '{soloGuild}' {soloGuildSilver}s" : "")
                           + "), escrows purged, owned sites and worker slots removed, recovery records cleared. "
                           + "Guild membership/donations kept.");

            if (client != null)
            {
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Features.Treasury.TreasuryStore.GetSnapshotFor(username));
                KmhRouter.Notify(client, "neutral", "New save detected - your Colony Nexus economy was reset: treasury, listings, sites and worker slots from the old save were cleared (guild membership and past donations kept).");
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

        private static Dto.PlayerStatsSnapshot ClientSnapshot()
            => Leaderboards.LeaderboardEngine.ForClients(PlayerStatsStore.BuildSnapshot(), Leaderboards.LeaderboardEngine.BannedNow());

        // Built once and shared: every interested client gets the same snapshot object.
        private static void PlayerStatsSnapshotAll()
        {
            Dto.PlayerStatsSnapshot snapshot = ClientSnapshot();
            KmhRouter.BroadcastToInterested(KmhProtocol.Kind.PlayerStatsSnapshot, _ => snapshot);
        }
    }
}
