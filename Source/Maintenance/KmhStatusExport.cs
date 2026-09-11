using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Gathered defensively, because one failing subsystem must not blank the whole file.
    internal static class KmhStatusExport
    {
        internal sealed class KmhStatus
        {
            public int    SchemaVersion { get; set; } = 1;
            public string ServerName    { get; set; } = "";
            public string ServerId      { get; set; } = "";
            public int    ApiPort       { get; set; }   // KMH API transport port; 0 when the API transport is off
            public string Build         { get; set; } = "";
            public int    WireVersion   { get; set; }
            public string GeneratedUtc  { get; set; } = "";
            public long   UptimeSeconds { get; set; }
            public string DataFolder    { get; set; } = "";

            public int VerifiedClients { get; set; }
            public int Players         { get; set; }
            public int Guilds          { get; set; }
            public int LinkedAccounts  { get; set; }

            public int MarketplaceListings { get; set; }
            public int Auctions           { get; set; }
            public int Wants              { get; set; }
            public int Quests             { get; set; }
            public int Sites              { get; set; }
            public int ActiveEvents       { get; set; }
            public int ActiveGlobalQuests { get; set; }
            public int MailMessages       { get; set; }
            public int ChatMessages       { get; set; }

            public long HousePoolSilver { get; set; }
            public long ReportedWealth  { get; set; }

            // Value outside any treasury: unclaimed mail attachments, and goods parked by a failed delivery.
            public long MailUnclaimedEscrowSilver { get; set; }
            public int  RecoveryHeldRecords       { get; set; }

            public int          Season           { get; set; }
            public List<string> DisabledFeatures { get; set; } = new List<string>();
            public List<KmhStatusLeader> Leaders { get; set; } = new List<KmhStatusLeader>();
            public List<KmhStatusBackup> Backups { get; set; } = new List<KmhStatusBackup>();
        }

        // Current-season leaderboard toppers (player, colony, trade, guild, treasury, ...) for external dashboards.
        internal sealed class KmhStatusLeader
        {
            public string Category { get; set; } = "";
            public string Holder   { get; set; } = "";
            public string Detail   { get; set; } = "";
            public long   Value    { get; set; }
        }

        internal sealed class KmhStatusBackup
        {
            public string Name  { get; set; } = "";
            public string Utc   { get; set; } = "";
            public int    Files { get; set; }
            public long   Bytes { get; set; }
        }

        public static KmhStatus Build()
        {
            KmhStatus s = new KmhStatus
            {
                ServerName   = KmhServerIdentity.Name,
                ServerId     = KmhServerIdentity.Id,
                Build        = KmhProtocol.BuildVersion,
                WireVersion  = KmhProtocol.CurrentVersion,
                GeneratedUtc = DateTime.UtcNow.ToString("o"),
                DataFolder   = Persistence.KmhDataPaths.Folder,
            };
            Try(() => s.ApiPort = Features.Transport.TransportConfig.Current.EnableKmhApiTransport
                                    ? Features.Transport.TransportConfig.Current.KmhApiPort : 0);
            Try(() => s.UptimeSeconds = Main_.BootstrapUtc == DateTime.MinValue ? 0 : (long)(DateTime.UtcNow - Main_.BootstrapUtc).TotalSeconds);
            Try(() =>
            {
                int clients = 0;
                foreach (ServerClient c in Network.ServerClients.Keys) if (c?.IsVerified == true) clients++;
                s.VerifiedClients = clients;
            });
            Try(() => s.Players        = Features.PlayerStats.PlayerStatsStore.PlayerCount);
            Try(() => s.Guilds         = Features.Guilds.GuildStore.ListGuilds().Count);
            Try(() => s.LinkedAccounts = Features.LinkedAccounts.LinkedAccountsStore.BuildSnapshot().Links.Count);
            Try(() => s.MarketplaceListings = Features.Marketplace.MarketplaceStore.BuildSnapshot(null).Listings.Count);
            Try(() => s.Auctions       = Features.Auctions.AuctionStore.AllForAdmin().Count);
            Try(() => s.Wants          = Features.WantBoard.WantStore.AllForAdmin().Count);
            Try(() => s.Quests         = Features.Quests.QuestStore.BuildSnapshot(null).Quests.Count);
            Try(() => s.Sites          = Features.Sites.SiteStore.AllForApi().Count);
            Try(() => s.ActiveEvents       = Features.World.WorldStore.ActiveEvents().Count);
            Try(() => s.ActiveGlobalQuests = Features.World.WorldStore.ActiveQuests().Count);
            Try(() => s.MailMessages       = Features.Mail.MailStore.TotalCount);
            Try(() => s.ChatMessages       = Features.Chat.ChatStore.TotalMessageCount);
            Try(() => s.MailUnclaimedEscrowSilver = Features.Mail.MailStore.UnclaimedEscrowSilver);
            Try(() => s.RecoveryHeldRecords       = Features.Recovery.RecoveryStore.HeldCount);
            Try(() => s.HousePoolSilver = Features.Marketplace.MarketplaceStore.HousePoolBalance());
            Try(() => s.ReportedWealth  = Features.PlayerStats.PlayerStatsStore.TotalReportedWealth());
            Try(() => s.Season          = Features.Seasons.SeasonStore.CurrentSeason);
            Try(() => s.DisabledFeatures = Features.FeaturesConfig.Current.DisabledList());
            Try(() =>
            {
                foreach (Features.Seasons.Dto.SeasonRecordDto r in Features.Seasons.SeasonStore.BuildCurrentLeaders())
                    s.Leaders.Add(new KmhStatusLeader { Category = r.Category, Holder = r.Holder, Detail = r.Detail, Value = r.Value });
            });
            Try(() =>
            {
                foreach (Persistence.KmhDataBackup.BackupInfo b in Persistence.KmhDataBackup.List())
                    s.Backups.Add(new KmhStatusBackup
                    {
                        Name = b.Name, Utc = Persistence.KmhDataBackup.StampUtcIso(b.Name), Files = b.Files, Bytes = b.Bytes,
                    });
            });
            return s;
        }

        public static bool WriteToDisk()
        {
            try { return Persistence.JsonFileStore.Save(Persistence.KmhDataPaths.StatusFile, Build()); }
            catch (Exception ex) { ServerLog.Verbose($"Status export failed: {ex.Message}"); return false; }
        }

        private static void Try(Action a) { try { a(); } catch { /* leave that field at its default */ } }
    }
}
