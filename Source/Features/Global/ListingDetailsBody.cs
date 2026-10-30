using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Own signed endpoint so the metadata bytes never change shape; must match Global's ListingDetailsRequest bytes.
    internal sealed class ListingDetailsBody
    {
        [JsonProperty("server_id")]           public string ServerId { get; set; } = "";
        [JsonProperty("issued_at")]           public long   IssuedAtUnix { get; set; }
        [JsonProperty("nonce")]               public string Nonce { get; set; } = "";
        [JsonProperty("generation")]          public int    Generation { get; set; }

        [JsonProperty("tagline")]             public string Tagline { get; set; } = "";
        [JsonProperty("play_style")]          public string PlayStyle { get; set; } = "";
        [JsonProperty("new_players")]         public bool   NewPlayers { get; set; }
        [JsonProperty("whitelist")]           public bool   Whitelist { get; set; }
        [JsonProperty("apply_url")]           public string ApplyUrl { get; set; } = "";
        [JsonProperty("rules_url")]           public string RulesUrl { get; set; } = "";
        [JsonProperty("workshop_collection")] public string WorkshopCollection { get; set; } = "";
        [JsonProperty("announcement")]        public string Announcement { get; set; } = "";
        [JsonProperty("maintenance_message")] public string MaintenanceMessage { get; set; } = "";
        [JsonProperty("maintenance_until")]   public long   MaintenanceUntilUnix { get; set; }

        [JsonProperty("signature")]           public string Signature { get; set; } = "";

        // Field order is the contract: adding one here but not in Global's Canonical() signs bytes nobody verifies.
        public byte[] Canonical() => KmhCanonicalPayload.For(KmhSigningDomains.ListingDetails)
            .Add(ServerId)
            .Add(IssuedAtUnix)
            .Add(Nonce)
            .Add(Generation)
            .Add(Tagline)
            .Add(PlayStyle)
            .Add(NewPlayers ? 1 : 0)
            .Add(Whitelist ? 1 : 0)
            .Add(ApplyUrl)
            .Add(RulesUrl)
            .Add(WorkshopCollection)
            .Add(Announcement)
            .Add(MaintenanceMessage)
            .Add(MaintenanceUntilUnix)
            .Build();

        // Owner-written fields only; per-request fields would force a push every time.
        public string ContentKey()
            => string.Join("\u001f", Tagline, PlayStyle, NewPlayers ? "1" : "0", Whitelist ? "1" : "0", ApplyUrl, RulesUrl,
                           WorkshopCollection, Announcement, MaintenanceMessage, MaintenanceUntilUnix.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
