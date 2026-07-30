using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Discord
{
    // Live console feed batches server lines to Discord, skipping bot chatter to avoid feedback loops.
    internal static class DiscordConsoleFeed
    {
        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static int _dropped;
        private const int MaxQueued = 1000;        // cap so an unconfigured channel can't grow memory unbounded
        private const int MaxLinesPerFlush = 40;   // don't dump a huge backlog in one tick
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

        private static volatile bool _enabled;     // mirrors config each tick; gates the tee cheaply
        private static bool _hooked;
        private static Action<object, Printer.Verbosity> _msgWrap, _warnWrap, _errWrap, _titleWrap;
        private static CancellationTokenSource _cts;

        public static void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            Task.Run(() => RunLoop(_cts.Token));
        }

        public static void Stop() { _cts?.Cancel(); _cts = null; }

        private static async Task RunLoop(CancellationToken ct)
        {
            // Let RWT's Main bring up its Printer logger before we hook on top of it.
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    DiscordConfig cfg = DiscordBridge.Config;
                    _enabled = cfg != null && cfg.ConsoleLiveFeed;
                    if (_enabled)
                    {
                        EnsureHooked();
                        Flush(cfg);
                    }
                }
                catch (Exception ex) { ServerLog.Warn($"Console feed tick failed: {ex.Message}"); }

                try { await Task.Delay(FlushInterval, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
            }
        }

        // Install the console tee once over current Printer delegates; re-hooking would stack wrappers and fight ConsoleExecutor.
        private static void EnsureHooked()
        {
            if (_hooked) return;
            Printer p = Printer.Instance;
            if (p == null) return;

            Action<object, Printer.Verbosity> innerMsg = p.OnMessage, innerWarn = p.OnWarning, innerErr = p.OnError, innerTitle = p.OnTitle;
            _msgWrap   = (o, v) => { Enqueue(o, "",        v); innerMsg?.Invoke(o, v); };
            _warnWrap  = (o, v) => { Enqueue(o, "[warn] ", v); innerWarn?.Invoke(o, v); };
            _errWrap   = (o, v) => { Enqueue(o, "[err]  ", v); innerErr?.Invoke(o, v); };
            _titleWrap = (o, v) => { Enqueue(o, "",        v); innerTitle?.Invoke(o, v); };   // KMH lines + RWT command output
            p.OnMessage = _msgWrap; p.OnWarning = _warnWrap; p.OnError = _errWrap; p.OnTitle = _titleWrap;
            _hooked = true;
            ServerLog.Info("Discord: live console feed hooked");
        }

        private static void Enqueue(object o, string prefix, Printer.Verbosity v)
        {
            if (!_enabled || o == null) return;
            if (!PassesVerbosity(v)) return;               // respect the server's verbosity, like the console does
            string line = o.ToString();
            if (string.IsNullOrEmpty(line)) return;
            if (line.Contains("Discord:")) return; // loop guard + drops the bot's own gateway chatter
            if (_queue.Count >= MaxQueued) { _queue.TryDequeue(out _); _dropped++; }
            _queue.Enqueue(prefix + line);
        }

        // Match RWT's verbosity filter so Discord only receives what the server console would print.
        private static bool PassesVerbosity(Printer.Verbosity v)
        {
            int configured;
            try { configured = Master.ServerConfig?.Verbosity ?? 0; }
            catch { configured = 0; }
            switch (v)
            {
                case Printer.Verbosity.Verbose: return configured >= 1;
                case Printer.Verbosity.Extreme: return configured >= 2;
                default:                        return true; // Normal (and anything unknown) always shows
            }
        }

        private static void Flush(DiscordConfig cfg)
        {
            if (_queue.IsEmpty) return;
            ulong ch = cfg.AdminChannelId;
            if (ch == 0) { while (_queue.TryDequeue(out _)) { } return; } // no channel - drain, never accumulate

            StringBuilder sb = new StringBuilder();
            int taken = 0;
            while (taken < MaxLinesPerFlush && _queue.TryDequeue(out string line))
            {
                if (sb.Length + line.Length + 1 > 1900) { PostBlock(ch, sb.ToString()); sb.Clear(); }
                sb.Append(line).Append('\n');
                taken++;
            }
            if (_dropped > 0) { sb.Append($"…({_dropped} earlier lines dropped)\n"); _dropped = 0; }
            if (sb.Length > 0) PostBlock(ch, sb.ToString());
        }

        private static void PostBlock(ulong ch, string body)
            => DiscordBridge.PostToChannel(ch, $"```\n{body.TrimEnd()}\n```");
    }
}
