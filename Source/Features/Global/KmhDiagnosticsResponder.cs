using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    internal sealed class DiagnosticsBody
    {
        [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
        [JsonProperty("generation")] public int    Generation { get; set; } = 1;
        [JsonProperty("issued_at")]  public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]      public string Nonce { get; set; } = "";
        [JsonProperty("request_id")] public string RequestId { get; set; } = "";
        [JsonProperty("declined")]   public bool   Declined { get; set; }
        [JsonProperty("payload")]    public string Payload { get; set; } = "";
        [JsonProperty("signature")]  public string Signature { get; set; } = "";

        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.Diagnostics)
            .Add(ServerId)
            .Add(Generation)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(RequestId)
            .Add(Declined ? "1" : "0")
            .Add(Payload)
            .Build();
    }

    // Declined unless the owner opted in, and the "no" is sent explicitly so the request never waits forever.
    internal static class KmhDiagnosticsResponder
    {
        private static string _answered = "";

        public static void OnHeartbeatResponse(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return;
            // The flag rides every heartbeat until a reply is recorded; answering each time would send one per beat.
            if (string.Equals(requestId, _answered, StringComparison.Ordinal)) return;
            _answered = requestId;

            bool allowed = GlobalConfig.Current?.ShareDiagnostics == true;
            ServerLog.Warn(allowed
                ? $"Global asked this server for a diagnostics snapshot; Global.ShareDiagnostics is on, so one was sent ({requestId})."
                : $"Global asked this server for a diagnostics snapshot; Global.ShareDiagnostics is off, so it was declined ({requestId}).");

            Task.Run(() => SendAsync(requestId, allowed));
        }

        private static async Task SendAsync(string requestId, bool allowed)
        {
            try
            {
                GlobalConfig cfg = GlobalConfig.Current;
                DiagnosticsBody body = new DiagnosticsBody
                {
                    ServerId     = KmhServerIdentity.Id,
                    Generation   = cfg.SigningGeneration,
                    IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Nonce        = Guid.NewGuid().ToString("N"),
                    RequestId    = requestId,
                    Declined     = !allowed,
                    Payload      = allowed ? Snapshot() : "",
                };
                body.Signature = KmhServerKey.Sign(body.Canonical());
                if (string.IsNullOrEmpty(body.Signature)) return;

                KmhGlobalClient client = new KmhGlobalClient(cfg.BaseUrl);
                GlobalCallResult r = await client.PostAsync("/api/v1/servers/diagnostics", body, null,
                                                            CancellationToken.None).ConfigureAwait(false);
                if (!r.Ok) ServerLog.Verbose($"Global: diagnostics reply refused ({r.ErrorCode}).");
            }
            catch (Exception ex) { ServerLog.Verbose($"Global: diagnostics reply failed ({ex.GetType().Name})."); }
        }

        // Projected from the owner-readable status export so it cannot drift; DataFolder (an absolute local path)
        // and Leaders (player names) are deliberately left out.
        internal static string Snapshot()
        {
            Maintenance.KmhStatusExport.KmhStatus s = Maintenance.KmhStatusExport.Build();
            StringBuilder sb = new StringBuilder();

            Line(sb, "build", s.Build);
            Line(sb, "wire_version", s.WireVersion);
            Line(sb, "uptime_seconds", s.UptimeSeconds);
            Line(sb, "verified_clients", s.VerifiedClients);
            Line(sb, "players", s.Players);
            Line(sb, "guilds", s.Guilds);
            Line(sb, "linked_accounts", s.LinkedAccounts);
            Line(sb, "marketplace_listings", s.MarketplaceListings);
            Line(sb, "auctions", s.Auctions);
            Line(sb, "wants", s.Wants);
            Line(sb, "quests", s.Quests);
            Line(sb, "sites", s.Sites);
            Line(sb, "active_events", s.ActiveEvents);
            Line(sb, "mail_messages", s.MailMessages);
            Line(sb, "chat_messages", s.ChatMessages);
            Line(sb, "season", s.Season);
            Line(sb, "backups", s.Backups?.Count ?? 0);
            Line(sb, "disabled_features", string.Join(",", (s.DisabledFeatures ?? new System.Collections.Generic.List<string>()).ToArray()));
            Line(sb, "persistence_degraded", Persistence.JsonFileStore.AnyStoreUnwritable ? "yes" : "no");
            return sb.ToString();
        }

        // Must list exactly the keys Snapshot() writes, so the disclosure and the payload cannot disagree.
        internal static readonly string[] SnapshotFields =
        {
            "build", "wire_version", "uptime_seconds", "verified_clients", "players", "guilds",
            "linked_accounts", "marketplace_listings", "auctions", "wants", "quests", "sites",
            "active_events", "mail_messages", "chat_messages", "season", "backups",
            "disabled_features", "persistence_degraded",
        };

        private static void Line(StringBuilder sb, string key, object value)
            => sb.Append(key).Append('=').Append(value).Append('\n');

        internal static void ResetForTest() => _answered = "";
    }
}
