using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kmh.Discord.Wire;

namespace KmhDiscordCoordinator
{
    public sealed class LiveServer
    {
        public string Alias { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public long   Generation { get; set; }
        public string DisplayName { get; set; } = "";
        public string KmhVersion { get; set; } = "";
        public bool   Official { get; set; }

        public string SessionToken { get; set; } = "";
        public DateTimeOffset LeaseExpiresAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public int    Players { get; set; }

        public HashSet<string> Capabilities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ulong> Channels { get; } = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        // Bounded: a server that stops polling must not grow the coordinator's memory without limit.
        public const int MaxQueued = 512;
        public readonly ConcurrentQueue<KmhDiscordInbound> Inbox = new ConcurrentQueue<KmhDiscordInbound>();
        public long Dropped;
        private long _seq;

        public long NextSeq() => Interlocked.Increment(ref _seq);

        public bool Online(DateTimeOffset now) => SessionToken.Length > 0 && LeaseExpiresAt > now;

        public ulong Channel(string role)
            => role != null && Channels.TryGetValue(role, out ulong id) ? id : 0UL;

        public void Enqueue(KmhDiscordInbound ev)
        {
            ev.Seq = NextSeq();
            Inbox.Enqueue(ev);
            while (Inbox.Count > MaxQueued && Inbox.TryDequeue(out _)) Interlocked.Increment(ref Dropped);
        }
    }

    // The routing table: nothing else decides which server a channel, alias or command belongs to.
    public sealed class ServerRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, LiveServer> _byAlias = new Dictionary<string, LiveServer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, string> _channelOwner = new Dictionary<ulong, string>();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<KmhDiscordCommandResult>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<KmhDiscordCommandResult>>();

        private readonly CoordinatorConfig _cfg;
        private readonly Func<DateTimeOffset> _clock;

        public ServerRegistry(CoordinatorConfig cfg, Func<DateTimeOffset> clock = null)
        {
            _cfg = cfg;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            Reload(cfg);
        }

        public void Reload(CoordinatorConfig cfg)
        {
            lock (_lock)
            {
                _channelOwner.Clear();
                foreach (CoordinatorConfig.ServerEntry e in cfg.Servers)
                {
                    if (!_byAlias.TryGetValue(e.Alias, out LiveServer s))
                    {
                        s = new LiveServer { Alias = e.Alias, ServerId = e.ServerId };
                        _byAlias[e.Alias] = s;
                    }
                    s.ServerId = e.ServerId;
                    s.Official = e.Official;
                    s.Channels.Clear();
                    foreach (KeyValuePair<string, string> kv in e.Channels)
                        if (ulong.TryParse((kv.Value ?? "").Trim(), out ulong id) && id != 0)
                        {
                            s.Channels[kv.Key] = id;
                            _channelOwner[id] = e.Alias;
                        }
                }

                // An alias dropped from the config stops being routable immediately.
                foreach (string alias in _byAlias.Keys.ToList())
                    if (!cfg.Servers.Any(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                        _byAlias.Remove(alias);
            }
        }

        public CoordinatorConfig.ServerEntry Entry(string serverId)
            => _cfg.Servers.FirstOrDefault(s =>
                   string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase) && s.Enabled);

        public LiveServer ByAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias)) return null;
            lock (_lock) return _byAlias.TryGetValue(alias.Trim(), out LiveServer s) ? s : null;
        }

        public LiveServer ByServerId(string serverId)
        {
            if (string.IsNullOrWhiteSpace(serverId)) return null;
            lock (_lock) return _byAlias.Values.FirstOrDefault(
                s => string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
        }

        public LiveServer BySession(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (_lock) return _byAlias.Values.FirstOrDefault(s => s.SessionToken == token);
        }

        public LiveServer ByChannel(ulong channelId)
        {
            if (channelId == 0) return null;
            lock (_lock)
                return _channelOwner.TryGetValue(channelId, out string alias) && _byAlias.TryGetValue(alias, out LiveServer s)
                    ? s : null;
        }

        public List<LiveServer> All()
        {
            lock (_lock) return _byAlias.Values.OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public List<LiveServer> OnlineServers()
        {
            DateTimeOffset now = _clock();
            return All().Where(s => s.Online(now)).ToList();
        }

        // A second claimant wins only if at least as current; the loser is logged, never multiplexed into two worlds.
        public enum ClaimOutcome { Accepted, Superseded, UnknownServer }

        public ClaimOutcome Claim(KmhDiscordRegisterRequest req, CoordinatorConfig.ServerEntry entry,
                                  string sessionToken, out LiveServer server, out string note)
        {
            note = "";
            lock (_lock)
            {
                if (!_byAlias.TryGetValue(entry.Alias, out server)) { server = null; return ClaimOutcome.UnknownServer; }

                bool hadRoute = server.SessionToken.Length > 0 && server.LeaseExpiresAt > _clock();
                if (hadRoute && server.InstanceId != req.InstanceId)
                {
                    if (req.Generation < server.Generation)
                    {
                        note = $"instance {req.InstanceId} at generation {req.Generation} is behind the live "
                             + $"{server.InstanceId} at {server.Generation}";
                        return ClaimOutcome.Superseded;
                    }
                    note = $"instance {req.InstanceId} replaced {server.InstanceId} for {entry.Alias}";
                    while (server.Inbox.TryDequeue(out _)) { }
                }

                server.InstanceId  = req.InstanceId ?? "";
                server.Generation  = req.Generation;
                server.DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? entry.Alias : req.DisplayName;
                server.KmhVersion  = req.KmhVersion ?? "";
                server.SessionToken = sessionToken;
                server.LeaseExpiresAt = _clock().AddSeconds(_cfg.LeaseSeconds);
                server.LastSeenAt = _clock();
                server.Capabilities.Clear();
                foreach (string c in req.Capabilities ?? new List<string>()) server.Capabilities.Add(c);
                return ClaimOutcome.Accepted;
            }
        }

        public void Drop(LiveServer s, string why)
        {
            if (s == null) return;
            lock (_lock)
            {
                s.SessionToken = "";
                s.LeaseExpiresAt = DateTimeOffset.MinValue;
                while (s.Inbox.TryDequeue(out _)) { }
            }
            CoordLog.Info($"route dropped for {s.Alias}: {why}");
        }

        public void Renew(LiveServer s, int players)
        {
            if (s == null) return;
            lock (_lock)
            {
                s.LeaseExpiresAt = _clock().AddSeconds(_cfg.LeaseSeconds);
                s.LastSeenAt = _clock();
                s.Players = players;
            }
        }

        public Task<KmhDiscordCommandResult> Await(string requestId, TimeSpan timeout, CancellationToken ct)
        {
            TaskCompletionSource<KmhDiscordCommandResult> tcs =
                new TaskCompletionSource<KmhDiscordCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;

            // Registered before the wait so an early result is never dropped; the timer keeps the dictionary bounded.
            _ = Task.Delay(timeout, ct).ContinueWith(_ =>
            {
                if (_pending.TryRemove(requestId, out TaskCompletionSource<KmhDiscordCommandResult> late))
                    late.TrySetResult(KmhDiscordCommandResult.Error(
                        "The server did not respond in time.", KmhDiscordError.Timeout));
            }, TaskScheduler.Default);

            return tcs.Task;
        }

        public bool Complete(string requestId, KmhDiscordCommandResult result)
            => _pending.TryRemove(requestId, out TaskCompletionSource<KmhDiscordCommandResult> tcs)
               && tcs.TrySetResult(result);

        public int PendingCount => _pending.Count;
    }
}
