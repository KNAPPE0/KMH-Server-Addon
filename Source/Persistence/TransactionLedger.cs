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
    // Append-only: a dispute record that can be edited in place is not evidence.
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

        // Stopped after the ordered shutdown flush, so its own final flush cannot append behind that snapshot.
        public static void Stop()
        {
            CancellationTokenSource cts = _cts;
            _cts = null; _flusher = null;
            try { cts?.Cancel(); } catch { /* already gone */ }
            Flush();
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

        // Held between the queue and disk: dequeuing straight into the write let a throw lose the dispute evidence from memory and file at once.
        private static readonly List<Entry> _pending = new List<Entry>();
        private static Func<string> _failAppendForTest;

        internal static int PendingCountForTest { get { lock (_fileLock) return _pending.Count; } }

        internal static IDisposable FailAppendForTest(Func<string> reason)
        {
            _failAppendForTest = reason;
            return new AppendFailureScope();
        }

        private sealed class AppendFailureScope : IDisposable
        {
            public void Dispose() => _failAppendForTest = null;
        }

        // Called by the flusher and the shutdown path at the same time, so it has to be safe concurrently.
        public static void Flush()
        {
            lock (_fileLock)
            {
                while (_queue.TryDequeue(out Entry e)) _pending.Add(e);
                if (_pending.Count == 0) return;

                StringBuilder sb = new StringBuilder();
                foreach (Entry e in _pending) sb.AppendLine(JsonConvert.SerializeObject(e, _compact));
                try
                {
                    string injected = _failAppendForTest?.Invoke();
                    if (injected != null) throw new IOException(injected);
                    Directory.CreateDirectory(KmhDataPaths.LedgerDir);
                    File.AppendAllText(TodayFile(), sb.ToString());
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"TransactionLedger: could not write {_pending.Count} entr(y/ies), " +
                                   $"held for the next flush: {ex.Message}");
                    return;
                }
                _pending.Clear();
            }
        }

        private static string TodayFile()
            => Path.Combine(KmhDataPaths.LedgerDir, $"ledger-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        // Flushes first, or a transaction still queued reads as though it never happened.
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
