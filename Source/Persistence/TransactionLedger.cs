using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Persistence
{
    // Append-only economy audit log for silver/item moves, flushed off-thread as daily JSONL for crash-safe dispute records.
    internal static class TransactionLedger
    {
        private sealed class Entry
        {
            [JsonProperty("utc")]    public string Utc    { get; set; }
            [JsonProperty("seq")]    public long   Seq    { get; set; }
            [JsonProperty("vault")]  public string Vault  { get; set; }
            [JsonProperty("actor")]  public string Actor  { get; set; }
            [JsonProperty("kind")]   public string Kind   { get; set; }   // deposit / withdraw / house_credit / house_debit
            [JsonProperty("amount")] public long   Amount { get; set; }
            [JsonProperty("item")]   public string Item   { get; set; }   // "" for silver
            [JsonProperty("note")]   public string Note   { get; set; }
        }

        private static readonly ConcurrentQueue<Entry> _queue = new ConcurrentQueue<Entry>();
        private static readonly object _fileLock = new object();
        private static readonly JsonSerializerSettings _compact = new JsonSerializerSettings { Formatting = Formatting.None };
        private static long _seq;
        private static Task _flusher;
        private static CancellationTokenSource _cts;

        // Treasury chokepoint hook: one value movement against one vault.
        public static void Record(string vault, string actor, string kind, long amount, string item, string note)
        {
            _queue.Enqueue(new Entry
            {
                Utc    = DateTime.UtcNow.ToString("o"),
                Seq    = Interlocked.Increment(ref _seq),
                Vault  = vault  ?? "",
                Actor  = actor  ?? "",
                Kind   = kind   ?? "",
                Amount = amount,
                Item   = item   ?? "",
                Note   = note   ?? "",
            });
        }

        // House pool (not a treasury) - tax in / drain out.
        public static void RecordHousePool(bool credit, long amount, string note)
            => Record("_house", "", credit ? "house_credit" : "house_debit", amount, "", note);

        public static void Start()
        {
            if (_flusher != null) return;
            _cts = new CancellationTokenSource();
            _flusher = Task.Run(() => RunLoop(_cts.Token));
            ServerLog.Verbose("TransactionLedger flusher started");
        }

        private static async Task RunLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (TaskCanceledException) { break; }
                Flush();
            }
            Flush();
        }

        // Drain the queue to today's file. Safe to call from the flusher and from the shutdown path concurrently.
        public static void Flush()
        {
            if (_queue.IsEmpty) return;
            StringBuilder sb = new StringBuilder();
            int n = 0;
            while (_queue.TryDequeue(out Entry e))
            {
                sb.AppendLine(JsonConvert.SerializeObject(e, _compact));
                n++;
            }
            if (n == 0) return;
            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(KmhDataPaths.LedgerDir);
                    File.AppendAllText(TodayFile(), sb.ToString());
                }
            }
            catch (Exception ex)
            {
                ServerLog.Warn($"TransactionLedger: could not write {n} entr(y/ies): {ex.Message}");
            }
        }

        private static string TodayFile()
            => Path.Combine(KmhDataPaths.LedgerDir, $"ledger-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        // Reads newest ledger lines, optionally by user, flushing pending entries first so fresh transactions show.
        public static List<string> ReadRecent(int count, string userFilter = null)
        {
            Flush();
            List<string> outLines = new List<string>();
            string filt = string.IsNullOrWhiteSpace(userFilter) ? null : userFilter.Trim();
            try
            {
                if (!Directory.Exists(KmhDataPaths.LedgerDir)) return outLines;
                string[] files = Directory.GetFiles(KmhDataPaths.LedgerDir, "ledger-*.jsonl")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
                foreach (string f in files)
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(f); } catch { continue; }
                    for (int i = lines.Length - 1; i >= 0 && outLines.Count < count; i--)
                    {
                        string formatted = Format(lines[i], filt);
                        if (formatted != null) outLines.Add(formatted);
                    }
                    if (outLines.Count >= count) break;
                }
            }
            catch (Exception ex) { ServerLog.Warn($"TransactionLedger read failed: {ex.Message}"); }
            return outLines;
        }

        // Parse one JSONL row into a console line; returns null if it doesn't match the user filter.
        private static string Format(string json, string userFilter)
        {
            try
            {
                JObject o = JObject.Parse(json);
                string vault = (string)o["vault"] ?? "";
                string actor = (string)o["actor"] ?? "";
                if (userFilter != null)
                {
                    string vaultUser = vault.StartsWith("_personal:", StringComparison.OrdinalIgnoreCase) ? vault.Substring("_personal:".Length) : vault;
                    if (!vaultUser.Equals(userFilter, StringComparison.OrdinalIgnoreCase) &&
                        !actor.Equals(userFilter, StringComparison.OrdinalIgnoreCase)) return null;
                }
                string utc  = (string)o["utc"] ?? "";
                string kind = (string)o["kind"] ?? "";
                long   amt  = (long?)o["amount"] ?? 0;
                string item = (string)o["item"] ?? "";
                string note = (string)o["note"] ?? "";
                string what = string.IsNullOrEmpty(item) ? $"{amt} silver" : $"{amt}x {item}";
                return $"{utc}  {vault}  {kind} {what}  ({note})";
            }
            catch { return null; }
        }
    }
}
