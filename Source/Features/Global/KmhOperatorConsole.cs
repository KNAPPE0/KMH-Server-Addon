using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    internal sealed class OperatorConsoleBody
    {
        [JsonProperty("server_id")]       public string ServerId { get; set; } = "";
        [JsonProperty("generation")]      public int    Generation { get; set; } = 1;
        [JsonProperty("issued_at")]       public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]           public string Nonce { get; set; } = "";
        [JsonProperty("session")]         public string Session { get; set; } = "";
        [JsonProperty("declined")]        public bool   Declined { get; set; }
        [JsonProperty("after_sequence")]  public long   AfterSequence { get; set; }
        [JsonProperty("oldest_sequence")] public long   OldestSequence { get; set; }
        [JsonProperty("newest_sequence")] public long   NewestSequence { get; set; }
        // One "<seq>\t<text>"-per-line string, so signing needs no serializer agreement between the two ends.
        [JsonProperty("lines")]           public string Lines { get; set; } = "";
        [JsonProperty("status")]          public string Status { get; set; } = "";

        [JsonProperty("signature")]       public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.OperatorConsole)
            .Add(ServerId)
            .Add(Generation)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Session)
            .Add(Declined ? "1" : "0")
            .Add(AfterSequence)
            .Add(OldestSequence)
            .Add(NewestSequence)
            .Add(Lines)
            .Add(Status)
            .Build();
    }

    // Needs both the directory's session and the owner's AllowOperatorConsole: "official" is a flag the directory
    // sets about someone else's server, so it alone is never enough.
    internal static class KmhOperatorConsole
    {
        internal const int PollSeconds  = 4;
        internal const int MaxLines     = 200;
        internal const int MaxLineBytes = 24 * 1024;

        private static string _declinedFor = "";
        private static string _streamingFor = "";
        private static long   _sent;

        // Short while a session is live, so the operator reads a terminal rather than a report.
        public static int NextWaitSecondsOrZero(long untilUnix)
            => untilUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? PollSeconds : 0;

        public static async Task<int> OnHeartbeatResponseAsync(KmhGlobalClient client, string session,
                                                               long untilUnix, long cursor, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(session) || NextWaitSecondsOrZero(untilUnix) == 0)
            {
                if (_streamingFor.Length > 0)
                    ServerLog.Warn($"Global: the operator console session has ended ({_streamingFor}); "
                                 + "this server has stopped sending its console.");
                _declinedFor = "";
                _streamingFor = "";
                return 0;
            }

            if (GlobalConfig.Current?.AllowOperatorConsole != true)
            {
                // Once per session, not once per beat: the operator needs the answer, not a refusal every 4s.
                if (!string.Equals(session, _declinedFor, StringComparison.Ordinal))
                {
                    _declinedFor = session;
                    ServerLog.Warn($"Global: an operator asked to read this server's console; "
                                 + $"Global.AllowOperatorConsole is off, so it was declined ({session}).");
                    await SendAsync(client, session, true, cursor, new List<KmhConsoleLine>(), "", ct).ConfigureAwait(false);
                }
                return 0;
            }

            // Warn once per session: an owner who opted in should still know while it is happening.
            if (!string.Equals(session, _streamingFor, StringComparison.Ordinal))
            {
                _streamingFor = session;
                ServerLog.Warn($"Global: an operator is reading this server's console until "
                             + $"{DateTimeOffset.FromUnixTimeSeconds(untilUnix).UtcDateTime:HH:mm} UTC ({session}). "
                             + "Set Global.AllowOperatorConsole to false and reload 'global' to stop it.");
            }

            List<KmhConsoleLine> lines = KmhConsoleTail.Since(cursor, MaxLines);
            await SendAsync(client, session, false, cursor, lines, Status(), ct).ConfigureAwait(false);
            return PollSeconds;
        }

        private static async Task SendAsync(KmhGlobalClient client, string session, bool declined, long cursor,
                                            List<KmhConsoleLine> lines, string status, CancellationToken ct)
        {
            try
            {
                GlobalConfig cfg = GlobalConfig.Current;
                OperatorConsoleBody body = new OperatorConsoleBody
                {
                    ServerId       = KmhServerIdentity.Id,
                    Generation     = cfg.SigningGeneration,
                    IssuedAtUnix   = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nonce          = Guid.NewGuid().ToString("N"),
                    Session        = session,
                    Declined       = declined,
                    AfterSequence  = cursor,
                    OldestSequence = KmhConsoleTail.OldestSequence,
                    NewestSequence = KmhConsoleTail.NewestSequence,
                    Lines          = declined ? "" : Pack(lines),
                    Status         = declined ? "" : status,
                };
                body.Signature = KmhServerKey.Sign(body.Canonical());
                if (string.IsNullOrEmpty(body.Signature)) return;

                GlobalCallResult r = await client.PostAsync("/api/v1/servers/console", body, null, ct).ConfigureAwait(false);
                if (r.Ok) _sent += lines.Count;
                else ServerLog.Verbose($"Global: console push refused ({r.ErrorCode}).");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ServerLog.Verbose($"Global: console push failed ({ex.GetType().Name})."); }
        }

        // Whole lines only (half a line misreads as complete); redacted because link codes and pasted credentials
        // scroll past on this same console.
        internal static string Pack(List<KmhConsoleLine> lines)
        {
            StringBuilder sb = new StringBuilder();
            int bytes = 0;
            foreach (KmhConsoleLine l in lines)
            {
                string one = l.Sequence + "\t"
                           + Maintenance.KmhRedact.Addresses(Maintenance.KmhRedact.Text(l.Text ?? "")).Replace('\n', ' ').Replace('\r', ' ') + "\n";
                int add = KmhLogText.Utf8Bytes(one);
                if (bytes + add > MaxLineBytes) break;
                sb.Append(one);
                bytes += add;
            }
            return sb.ToString();
        }

        // Field by field: the full status export carries the data folder, backups and leaderboard (paths and people).
        internal static string Status()
        {
            try
            {
                Maintenance.KmhStatusExport.KmhStatus s = Maintenance.KmhStatusExport.Build();
                var o = new Newtonsoft.Json.Linq.JObject
                {
                    ["server_id"]      = s.ServerId,
                    ["server_name"]    = s.ServerName,
                    ["build"]          = s.Build,
                    ["protocol"]       = s.WireVersion,
                    ["api_port"]       = s.ApiPort,
                    ["generated_utc"]  = s.GeneratedUtc,
                    ["uptime_seconds"] = s.UptimeSeconds,

                    ["readiness"]            = Maintenance.KmhReadiness.Describe(),
                    ["persistence_degraded"] = Persistence.JsonFileStore.AnyStoreUnwritable,
                    ["config_schema"]        = Persistence.KmhDataMeta.AppliedConfigSchema,
                    ["season"]               = s.Season,
                    ["disabled_features"]    = new Newtonsoft.Json.Linq.JArray(s.DisabledFeatures),

                    ["verified_clients"] = s.VerifiedClients,
                    ["players"]          = s.Players,
                    ["guilds"]           = s.Guilds,

                    // Load, not contents: how much there is to serve, never who owns any of it.
                    ["marketplace_listings"] = s.MarketplaceListings,
                    ["auctions"]             = s.Auctions,
                    ["wants"]                = s.Wants,
                    ["quests"]               = s.Quests,
                    ["sites"]                = s.Sites,
                    ["active_events"]        = s.ActiveEvents,
                    ["mail_messages"]        = s.MailMessages,
                    ["chat_messages"]        = s.ChatMessages,
                    ["recovery_held"]        = s.RecoveryHeldRecords,

                    // File NAME only: the absolute path carries the account the server runs as.
                    ["log_file"]           = System.IO.Path.GetFileName(KmhLogSink.CurrentFile ?? ""),
                    ["log_queue_depth"]    = KmhLogSink.QueueDepth,
                    ["log_dropped"]        = KmhLogSink.DroppedCount,
                    ["console_lines_sent"] = _sent,
                    ["working_set_mb"]     = WorkingSetMb(),
                };
                return Maintenance.KmhRedact.Json(o.ToString(Formatting.None), Formatting.None);
            }
            catch (Exception ex) { return "{\"error\":\"" + ex.GetType().Name + "\"}"; }
        }

        private static long WorkingSetMb()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024); }
            catch (Exception) { return 0; }
        }

        internal static void ResetForTest() { _declinedFor = ""; _streamingFor = ""; _sent = 0; }
    }
}
