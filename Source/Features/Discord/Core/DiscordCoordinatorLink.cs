using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kmh.Discord.Wire;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    // This process never opens a gateway; it dials out to the coordinator that holds the one connection.
    internal static class DiscordCoordinatorLink
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private static CancellationTokenSource _cts;
        private static string _baseUrl = "";
        private static string _session = "";
        private static volatile bool _active;

        private static readonly ConcurrentQueue<KmhDiscordOutbound> _outbox = new ConcurrentQueue<KmhDiscordOutbound>();
        private static readonly ConcurrentQueue<KmhDiscordCommandResult> _results = new ConcurrentQueue<KmhDiscordCommandResult>();
        private static readonly ConcurrentDictionary<string, KmhDiscordCommandResult> _answered
            = new ConcurrentDictionary<string, KmhDiscordCommandResult>(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> _answeredOrder = new ConcurrentQueue<string>();

        private const int MaxOutbox = 512;
        private const int MaxAnswered = 256;

        public static bool Active => _active;
        public static ulong BotUserId { get; private set; }
        public static string Alias { get; private set; } = "";

        // Set from the coordinator's reply: an owner running in coordinator mode never writes channel ids locally.
        public static Dictionary<string, ulong> Channels { get; private set; }
            = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        public static string Describe()
        {
            if (!_active) return "coordinator mode: not connected";
            return $"coordinator mode: {Alias} via {_baseUrl} (bot {BotUserId})";
        }

        public static void Start(DiscordConfig cfg)
        {
            Stop();
            _baseUrl = (cfg?.Coordinator?.Url ?? "").TrimEnd('/');
            if (_baseUrl.Length == 0)
            {
                ServerLog.Warn("Discord: coordinator mode is on but Coordinator.Url is empty - Discord is off for this server");
                return;
            }
            if (!TrustedCoordinatorUrl(_baseUrl))
            {
                ServerLog.Warn("Discord: Coordinator.Url must be https, or http on this machine (localhost) - Discord is off for this server");
                _baseUrl = "";
                return;
            }

            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            Task.Run(() => RunAsync(cfg, ct), ct);
        }

        // Its replies carry commands with admin flags and channel ids, so plain http is only trusted when it never leaves the host.
        internal static bool TrustedCoordinatorUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u)) return false;
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch (Exception) { }
            _cts = null;
            _active = false;
            _session = "";
        }

        private static async Task RunAsync(DiscordConfig cfg, CancellationToken ct)
        {
            int backoffSeconds = 5;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_session.Length == 0)
                    {
                        if (!await RegisterAsync(ct).ConfigureAwait(false))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                            backoffSeconds = Math.Min(backoffSeconds * 2, 120);
                            continue;
                        }
                        backoffSeconds = 5;
                    }

                    await FlushAsync(ct).ConfigureAwait(false);
                    await PollAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Discord: coordinator link error: {ex.Message}");
                    _active = false;
                    _session = "";
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    backoffSeconds = Math.Min(backoffSeconds * 2, 120);
                }
            }
        }

        private static async Task<bool> RegisterAsync(CancellationToken ct)
        {
            string nonce = KmhDiscordAuth.NewNonce();
            long issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string serverId = KmhDiscordIdentity.ServerId;
            long generation = GlobalConfig.Current?.SigningGeneration ?? 1;

            KmhDiscordRegisterRequest req = new KmhDiscordRegisterRequest
            {
                ServerId = serverId,
                Generation = generation,
                InstanceId = KmhDiscordIdentity.InstanceId,
                DisplayName = KmhDiscordIdentity.DisplayName,
                KmhVersion = KmhVersion.Build,
                Capabilities = Capabilities(),
                Nonce = nonce,
                IssuedAtUnix = issued,
            };
            req.Signature = KmhServerKey.Sign(
                KmhDiscordAuth.Canonical(serverId, generation, req.InstanceId, nonce, issued));

            if (req.Signature.Length == 0)
            {
                ServerLog.Warn("Discord: no Global signing key yet - run `kmh global setup` before coordinator mode");
                return false;
            }

            KmhDiscordRegisterResponse res = await PostAsync<KmhDiscordRegisterResponse>(
                "/v1/register", req, authed: false, ct).ConfigureAwait(false);

            if (res == null || !res.Ok)
            {
                ServerLog.Warn($"Discord: coordinator refused registration ({res?.Error ?? "no answer"})");
                return false;
            }

            _session = res.SessionToken ?? "";
            BotUserId = res.BotUserId;
            Alias = res.Alias ?? "";
            Channels = new Dictionary<string, ulong>(res.Channels ?? new Dictionary<string, ulong>(),
                                                     StringComparer.OrdinalIgnoreCase);
            _active = _session.Length > 0;
            ServerLog.Info($"Discord: {Describe()}");
            return _active;
        }

        private static List<string> Capabilities()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            List<string> caps = new List<string>
            {
                KmhDiscordCapability.Status,
                KmhDiscordCapability.Players,
                KmhDiscordCapability.Leaderboard,
                KmhDiscordCapability.Marketplace,
                KmhDiscordCapability.Sites,
                KmhDiscordCapability.Announcements,
                KmhDiscordCapability.ChatRelay,
                KmhDiscordCapability.Moderation,
            };
            if (cfg?.AllowConsoleCommands == true) caps.Add(KmhDiscordCapability.Console);
            return caps;
        }

        public static void Enqueue(KmhDiscordOutbound op)
        {
            if (op == null) return;
            _outbox.Enqueue(op);
            while (_outbox.Count > MaxOutbox && _outbox.TryDequeue(out _)) { }
        }

        public static string RoleForChannel(ulong channelId)
        {
            foreach (KeyValuePair<string, ulong> kv in Channels)
                if (kv.Value == channelId) return kv.Key;
            return "";
        }

        private static int _lastPlayers = -1;
        private static DateTime _lastPresenceAt = DateTime.MinValue;

        private static void QueuePresence()
        {
            int count = 0;
            foreach (TCPNetwork.ServerClient c in Network.ServerClients.Keys)
                if (c?.IsVerified == true) count++;

            // The coordinator aggregates the fleet, so only a real change is worth a call.
            if (count == _lastPlayers && DateTime.UtcNow - _lastPresenceAt < TimeSpan.FromMinutes(2)) return;
            _lastPlayers = count;
            _lastPresenceAt = DateTime.UtcNow;
            Enqueue(new KmhDiscordOutbound { Op = KmhDiscordOp.Presence, Players = count });
        }

        private static async Task FlushAsync(CancellationToken ct)
        {
            QueuePresence();
            List<KmhDiscordOutbound> batch = new List<KmhDiscordOutbound>();
            while (batch.Count < 32 && _outbox.TryDequeue(out KmhDiscordOutbound op)) batch.Add(op);

            List<KmhDiscordCommandResult> answers = new List<KmhDiscordCommandResult>();
            while (answers.Count < 32 && _results.TryDequeue(out KmhDiscordCommandResult r)) answers.Add(r);

            if (batch.Count > 0)
                await PostAsync<List<KmhDiscordOutboundResult>>("/v1/outbound", batch, authed: true, ct).ConfigureAwait(false);
            if (answers.Count > 0)
                await PostAsync<object>("/v1/results", answers, authed: true, ct).ConfigureAwait(false);
        }

        private sealed class EventBatch
        {
            [JsonProperty("events")]  public List<KmhDiscordInbound> Events { get; set; }
            [JsonProperty("dropped")] public long Dropped { get; set; }
        }

        private static long _reportedDropped;

        private static async Task PollAsync(CancellationToken ct)
        {
            EventBatch batch = await GetAsync<EventBatch>("/v1/events?wait=25", ct).ConfigureAwait(false);
            if (batch == null) { _session = ""; _active = false; return; }

            if (batch.Dropped > _reportedDropped)
            {
                ServerLog.Warn($"Discord: coordinator dropped {batch.Dropped - _reportedDropped} queued event(s) for this server");
                _reportedDropped = batch.Dropped;
            }

            foreach (KmhDiscordInbound ev in batch.Events ?? new List<KmhDiscordInbound>())
                await DispatchAsync(ev).ConfigureAwait(false);
        }

        private static async Task DispatchAsync(KmhDiscordInbound ev)
        {
            try
            {
                switch (ev.Kind)
                {
                    case KmhDiscordInboundKind.Command:   await OnCommand(ev).ConfigureAwait(false); return;
                    case KmhDiscordInboundKind.Component: await OnComponent(ev).ConfigureAwait(false); return;
                    default: await DiscordBridge.OnCoordinatorEvent(ev).ConfigureAwait(false); return;
                }
            }
            catch (Exception ex) { ServerLog.Error($"Discord: coordinator event {ev.Kind} threw", ex); }
        }

        // A coordinator retry must not run a mutating command twice, so an answered request id replays its answer.
        private static async Task OnCommand(KmhDiscordInbound ev)
        {
            KmhDiscordCommandRequest req = ev.Command;
            if (req == null || string.IsNullOrEmpty(req.RequestId)) return;

            if (_answered.TryGetValue(req.RequestId, out KmhDiscordCommandResult already))
            {
                _results.Enqueue(already);
                return;
            }

            KmhDiscordCommandResult result = await DiscordSlashCommands.ExecuteAsync(req).ConfigureAwait(false);
            result.RequestId = req.RequestId;
            Remember(req.RequestId, result);
            _results.Enqueue(result);
        }

        private static async Task OnComponent(KmhDiscordInbound ev)
        {
            string id = ev.InteractionId ?? "";
            if (id.Length == 0) return;
            if (_answered.TryGetValue(id, out KmhDiscordCommandResult already)) { _results.Enqueue(already); return; }

            List<string> lines = new List<string>();
            KmhDiscordInteraction interaction = new KmhDiscordInteraction
            {
                CustomId = ev.CustomId ?? "",
                ChannelId = ev.ChannelId,
                GuildId = ev.GuildId,
                User = new KmhDiscordUser { Id = ev.AuthorId, Username = ev.AuthorName, Display = ev.AuthorDisplay },
            };
            // The coordinator already deferred, so every line the handler produces is collected into one answer.
            interaction.Responder = (text, _) => { lines.Add(text); return Task.CompletedTask; };
            interaction.Deferrer = () => Task.CompletedTask;
            interaction.FollowupResponder = (text, _) => { lines.Add(text); return Task.CompletedTask; };

            await DiscordBuyButton.OnInteraction(interaction).ConfigureAwait(false);

            KmhDiscordCommandResult result = KmhDiscordCommandResult.Text(
                lines.Count == 0 ? "(no answer)" : string.Join("\n", lines), ephemeral: true);
            result.RequestId = id;
            Remember(id, result);
            _results.Enqueue(result);
        }

        private static void Remember(string id, KmhDiscordCommandResult result)
        {
            _answered[id] = result;
            _answeredOrder.Enqueue(id);
            while (_answeredOrder.Count > MaxAnswered && _answeredOrder.TryDequeue(out string old))
                _answered.TryRemove(old, out _);
        }

        private static async Task<T> PostAsync<T>(string path, object body, bool authed, CancellationToken ct) where T : class
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"),
            };
            if (authed) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);

            using HttpResponseMessage res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string text = await ReadAsync(res, ct).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized
             || res.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _session = "";
                _active = false;
                return null;
            }
            if (!res.IsSuccessStatusCode || text == null) return null;
            try { return JsonConvert.DeserializeObject<T>(text); } catch (Exception) { return null; }
        }

        // The coordinator's answers are small; anything larger or slower is a broken peer, read no further.
        private const int MaxResponseBytes = 1024 * 1024;
        private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);

        private static async Task<string> ReadAsync(HttpResponseMessage res, CancellationToken ct)
            => (await KMHServerAddon.Util.KmhBoundedHttp.ReadTextAsync(res, MaxResponseBytes, BodyTimeout, ct).ConfigureAwait(false)).Text;

        private static async Task<T> GetAsync<T>(string path, CancellationToken ct) where T : class
        {
            using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
            using HttpResponseMessage res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            string text = await ReadAsync(res, ct).ConfigureAwait(false);
            if (text == null) return null;
            try { return JsonConvert.DeserializeObject<T>(text); } catch (Exception) { return null; }
        }
    }
}
