using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;

namespace KMHServerAddon.Persistence
{
    // Subscribed to the same event bus extensions use, so a new feature appears here without being wired in.
    internal static class KmhHistory
    {
        private const int KeepMonths = 3;
        private static readonly object _lock = new object();

        public static string Dir => Path.Combine(KmhDataPaths.Folder, "History");

        public static void Start()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var bus = Extensibility.KmhEventBus.Instance;
                bus.MarketplacePost      += e => Write("market",   "post",      e);
                bus.MarketplaceBuy       += e => Write("market",   "buy",       e);
                bus.MarketplaceCancel    += e => Write("market",   "cancel",    e);
                bus.AuctionPosted        += e => Write("auctions", "post",      e);
                bus.AuctionBid           += e => Write("auctions", "bid",       e);
                bus.AuctionSettled       += e => Write("auctions", "settled",   e);
                bus.QuestPosted          += e => Write("quests",   "posted",    e);
                bus.QuestClaimed         += e => Write("quests",   "claimed",   e);
                bus.QuestSubmitted       += e => Write("quests",   "submitted", e);
                bus.QuestApproved        += e => Write("quests",   "approved",  e);
                bus.QuestCancelled       += e => Write("quests",   "cancelled", e);
                bus.GuildChanged         += e => Write("guilds",   "changed",   e);
                bus.SiteChanged          += e => Write("sites",    "changed",   e);
                bus.GlobalQuestCreated   += e => Write("world",    "gq-created",   e);
                bus.GlobalQuestCompleted += e => Write("world",    "gq-completed", e);
                bus.GlobalQuestExpired   += e => Write("world",    "gq-expired",   e);
                bus.SeasonRolled         += e => Write("admin",    "season-rolled", e);
                bus.BackupCreated        += e => Write("admin",    "backup",    e);
                bus.RestoreApplied       += e => Write("admin",    "restore",   e);
                PruneOld();
                // Retention has to keep running: pruning only at boot means a server up for months never prunes again.
                Maintenance.KmhScheduler.Register("history-prune", TimeSpan.FromHours(24), PruneOld, TimeSpan.FromHours(24));
                ServerLog.Info($"History: recording state changes to {Dir} (kept {KeepMonths} months; inspect via 'kmh history').");
            }
            catch (Exception ex) { ServerLog.Warn($"History: could not start ({ex.Message}) - state-change history disabled."); }
        }

        private static void Write(string domain, string action, object data)
        {
            try
            {
                string line = JsonConvert.SerializeObject(new
                {
                    utc = DateTime.UtcNow.ToString("o"),
                    action,
                    data,
                }, Formatting.None);
                string file = Path.Combine(Dir, $"{domain}-{DateTime.UtcNow:yyyy-MM}.jsonl");
                lock (_lock) File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { /* history is best-effort; never let it break the action it records */ }
        }

        public static List<string> ReadRecent(string domain, string contains, int count)
        {
            List<string> result = new List<string>();
            try
            {
                var files = Directory.Exists(Dir)
                    ? Directory.GetFiles(Dir, $"{domain}-*.jsonl").OrderByDescending(f => f).ToList()
                    : new List<string>();
                foreach (string f in files)
                {
                    string[] lines;
                    lock (_lock) lines = File.ReadAllLines(f);
                    for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
                        if (string.IsNullOrEmpty(contains) || lines[i].IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                            result.Add(lines[i]);
                    if (result.Count >= count) break;
                }
            }
            catch (Exception ex) { result.Add($"(history read failed: {ex.Message})"); }
            return result;
        }

        public static IEnumerable<string> Domains => new[] { "market", "auctions", "quests", "guilds", "sites", "world", "admin" };

        private static void PruneOld()
        {
            try
            {
                string cutoff = DateTime.UtcNow.AddMonths(-KeepMonths).ToString("yyyy-MM");
                foreach (string f in Directory.GetFiles(Dir, "*.jsonl"))
                {
                    // filename: <domain>-yyyy-MM.jsonl - lexicographic month compare works.
                    string name  = Path.GetFileNameWithoutExtension(f);
                    int    dash  = name.LastIndexOf('-');
                    dash = dash > 0 ? name.LastIndexOf('-', dash - 1) : -1;
                    if (dash <= 0) continue;
                    string month = name.Substring(dash + 1);
                    if (string.CompareOrdinal(month, cutoff) < 0) File.Delete(f);
                }
            }
            catch { /* pruning is best-effort */ }
        }
    }
}
