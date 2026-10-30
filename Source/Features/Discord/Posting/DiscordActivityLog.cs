using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // One growing message per channel per day; edits are batched, at most one per channel every MinEditGap.
    internal static class DiscordActivityLog
    {
        internal const int MaxLineChars = 200;
        internal const int MaxPending = 300;
        internal static readonly TimeSpan MinEditGap = TimeSpan.FromSeconds(20);

        private static readonly ConcurrentQueue<(ulong Channel, string Line)> _pending = new ConcurrentQueue<(ulong, string)>();
        private static int _pendingCount;
        private static int _dropped;
        // Lines a channel could not take yet (its last edit was too recent, or Discord refused), in order.
        private static readonly Dictionary<ulong, List<string>> _carry = new Dictionary<ulong, List<string>>();
        private static readonly Dictionary<ulong, DateTime> _lastEdit = new Dictionary<ulong, DateTime>();
        private static readonly object _lock = new object();

        public static bool HasWork { get { lock (_lock) return !_pending.IsEmpty || _carry.Count > 0; } }

        public static void Add(ulong channelId, string line)
        {
            if (channelId == 0 || string.IsNullOrWhiteSpace(line) || !DiscordLive.On) return;
            Enqueue(channelId, line);
        }

        // Bounded: a Discord outage must not grow memory without end, and the log says how much it lost.
        internal static void Enqueue(ulong channelId, string line)
        {
            line = DiscordText.OneLine(line).Trim();
            if (line.Length > MaxLineChars) line = line.Substring(0, MaxLineChars - 1) + "…";
            _pending.Enqueue((channelId, line));
            if (Interlocked.Increment(ref _pendingCount) > MaxPending && _pending.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _dropped);
            }
        }

        internal static void ResetForTest()
        {
            while (_pending.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _pendingCount, 0);
            Interlocked.Exchange(ref _dropped, 0);
            lock (_lock) { _carry.Clear(); _lastEdit.Clear(); }
        }

        internal static string Header(DateTime windowStart, int part)
            => $"📜 **Activity** · {windowStart.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)} · times in UTC"
             + (part > 1 ? $" · part {part}" : "");

        internal static string Compose(string header, List<string> lines) => header + "\n" + string.Join("\n", lines);

        internal static async Task<bool> FlushAsync(DiscordLiveState state, DateTime now, int dayStartHour, IDiscordLiveSink sink, bool canEdit)
        {
            var byChannel = new Dictionary<ulong, List<string>>();
            var order = new List<ulong>();
            lock (_lock)
            {
                foreach (KeyValuePair<ulong, List<string>> kv in _carry) { byChannel[kv.Key] = new List<string>(kv.Value); order.Add(kv.Key); }
                _carry.Clear();
            }
            while (_pending.TryDequeue(out (ulong Channel, string Line) item))
            {
                Interlocked.Decrement(ref _pendingCount);
                if (!byChannel.TryGetValue(item.Channel, out List<string> list)) { byChannel[item.Channel] = list = new List<string>(); order.Add(item.Channel); }
                list.Add(item.Line);
            }
            int dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) ServerLog.Warn($"Discord: {dropped} activity line(s) dropped while Discord could not keep up");

            bool changed = false;
            var window = DiscordDayWindow.For(now, dayStartHour, 24);
            foreach (ulong ch in order)
            {
                List<string> incoming = byChannel[ch];
                bool wait;
                lock (_lock) wait = _lastEdit.TryGetValue(ch, out DateTime last) && now - last < MinEditGap;
                if (wait) { Carry(ch, incoming); continue; }

                DiscordActivityLogState log = state.Logs.Find(l => l.ChannelId == ch);
                if (log == null) state.Logs.Add(log = new DiscordActivityLogState { ChannelId = ch });
                if (log.WindowStartTicks != window.Start.Ticks)
                {
                    log.WindowStartTicks = window.Start.Ticks;
                    log.MessageId = 0;
                    log.Part = 1;
                    log.Lines = new List<string>();
                    changed = true;
                }

                // No id comes back through the coordinator, so there each batch is a message of its own.
                if (!canEdit) { log.MessageId = 0; log.Lines = new List<string>(); }

                while (incoming.Count > 0)
                {
                    string header = Header(window.Start, log.Part);
                    var lines = new List<string>(log.Lines);
                    int taken = 0;
                    foreach (string l in incoming)
                    {
                        lines.Add(l);
                        if (Compose(header, lines).Length > DiscordCompactText.MaxChars) { lines.RemoveAt(lines.Count - 1); break; }
                        taken++;
                    }
                    if (taken == 0 && lines.Count == 0) { incoming.RemoveAt(0); continue; }
                    if (taken == 0)
                    {
                        // Full: this message is done, and the rest opens the next part.
                        log.MessageId = 0;
                        log.Part++;
                        log.Lines = new List<string>();
                        changed = true;
                        continue;
                    }

                    ulong id = await sink.PostOrEditAsync(ch, log.MessageId, Compose(header, lines), null, true).ConfigureAwait(false);
                    lock (_lock) _lastEdit[ch] = now;
                    incoming = incoming.GetRange(taken, incoming.Count - taken);
                    if (!canEdit) { changed = true; continue; }
                    // Kept for the next flush rather than lost: Discord may only be briefly away.
                    if (id == 0) { Carry(ch, lines.GetRange(log.Lines.Count, taken)); Carry(ch, incoming); break; }
                    log.MessageId = id;
                    log.Lines = lines;
                    changed = true;
                }
            }
            return changed;
        }

        private static void Carry(ulong ch, List<string> lines)
        {
            if (lines.Count == 0) return;
            lock (_lock)
            {
                if (!_carry.TryGetValue(ch, out List<string> list)) _carry[ch] = list = new List<string>();
                list.AddRange(lines);
                if (list.Count > MaxPending) { Interlocked.Add(ref _dropped, list.Count - MaxPending); list.RemoveRange(0, list.Count - MaxPending); }
            }
        }

        private static string At(DateTime utc) => DiscordCompactText.Hhmm(utc);
        private static string N(string s) => DiscordText.SafeName(string.IsNullOrWhiteSpace(s) ? "?" : s.Trim());

        internal static string Listed(DateTime at, string seller, int qty, string item, long unitPrice)
            => $"🛒 {At(at)} {N(seller)} listed {DiscordCompactText.Qty(qty)}× {N(item)} @ {DiscordCompactText.Silver(unitPrice)}";

        internal static string Bought(DateTime at, string buyer, int qty, string item, long total, string seller)
            => $"💰 {At(at)} {N(buyer)} bought {DiscordCompactText.Qty(qty)}× {N(item)} for {DiscordCompactText.Silver(total)} from {N(seller)}";

        internal static string Built(DateTime at, string owner, string what)
            => $"🏗️ {At(at)} {N(owner)} built {what}";

        internal static string Removed(DateTime at, string owner, int tile)
            => $"🏚️ {At(at)} {N(owner)}'s site on tile {tile} was removed";

        internal static string QuestDone(DateTime at, string claimer, string poster, long bounty)
            => $"✅ {At(at)} {N(claimer)} completed {N(poster)}'s quest for {DiscordCompactText.Silver(bounty)}";

        internal static string GuildFounded(DateTime at, string founder, string guild)
            => $"🏰 {At(at)} {N(founder)} founded the guild {N(guild)}";

        internal static string SiteWhat(string siteName, string itemLabel, int perCycle)
        {
            if (!string.IsNullOrWhiteSpace(siteName)) return N(siteName);
            if (string.IsNullOrWhiteSpace(itemLabel)) return "a site";
            return $"a {N(itemLabel)} site" + (perCycle > 0 ? $" ({DiscordCompactText.Qty(perCycle)} per cycle)" : "");
        }
    }
}
