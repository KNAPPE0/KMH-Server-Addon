using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Guilds.Contributions
{
    // Append-only storage; every derived meaning lives in KmhContributions.Derive rather than here.
    internal static class KmhGuildContributionLedger
    {
        private sealed class State { public List<KmhGuildContribution> Records { get; set; } = new List<KmhGuildContribution>(); }

        private static readonly object _lock = new object();
        private static State _state = new State();

        public static void LoadFromDisk()
        {
            lock (_lock)
                _state = JsonFileStore.TryLoad(KmhDataPaths.GuildContributionsFile, out State s) && s?.Records != null
                    ? s : new State();
        }

        // The sequence is taken under the lock but serializing happens outside, since the ledger only ever grows.
        private static void Save(State s, long seq)
        {
            try { JsonFileStore.Save(KmhDataPaths.GuildContributionsFile, s, seq); }
            catch (Exception ex) { ServerLog.Warn($"Guild contributions: save failed ({ex.Message})."); }
        }

        // Boot materializes the file so the next integrity scan reads an empty ledger instead of reporting it missing.
        public static void SaveToDisk()
        {
            State s;
            long  seq;
            lock (_lock) { s = _state; seq = JsonFileStore.NextSequence(); }
            Save(s, seq);
        }

        public static void RecordSilver(string guild, string player, long amount, string txId = "")
        {
            if (string.IsNullOrEmpty(guild) || string.IsNullOrEmpty(player) || amount <= 0) return;
            Append(new KmhGuildContribution
            {
                Id = Guid.NewGuid().ToString("N"), GuildId = guild, PlayerId = player, TransactionId = txId ?? "",
                Type = KmhContributionType.Silver, SilverValue = amount, TimestampUtc = DateTime.UtcNow.ToString("o"),
            });
        }

        public static void RecordItem(string guild, string player, long marketValue, string itemDescribe, string txId = "")
        {
            if (string.IsNullOrEmpty(guild) || string.IsNullOrEmpty(player) || marketValue <= 0) return;
            Append(new KmhGuildContribution
            {
                Id = Guid.NewGuid().ToString("N"), GuildId = guild, PlayerId = player, TransactionId = txId ?? "",
                Type = KmhContributionType.Item, ItemValue = marketValue, ItemPayload = itemDescribe ?? "",
                TimestampUtc = DateTime.UtcNow.ToString("o"),
            });
        }

        private static void Append(KmhGuildContribution c)
        {
            State s;
            long  seq;
            lock (_lock) { _state.Records.Add(c); s = _state; seq = JsonFileStore.NextSequence(); }
            Save(s, seq);
        }

        public static List<KmhGuildContribution> ForPlayer(string guild, string player)
        {
            var outp = new List<KmhGuildContribution>();
            lock (_lock)
                foreach (KmhGuildContribution c in _state.Records)
                    if (c != null && Eq(c.GuildId, guild) && Eq(c.PlayerId, player)) outp.Add(c);
            return outp;
        }

        public static KmhContributionSummary SummaryFor(string guild, string player)
            => KmhContributions.Derive(ForPlayer(guild, player));

        public static List<string> PlayersIn(string guild)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_lock)
                foreach (KmhGuildContribution c in _state.Records)
                    if (c != null && Eq(c.GuildId, guild) && !string.IsNullOrEmpty(c.PlayerId)) seen.Add(c.PlayerId);
            return new List<string>(seen);
        }

        private static bool Eq(string x, string y) => string.Equals(x ?? "", y ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
