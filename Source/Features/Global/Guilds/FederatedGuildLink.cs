using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global.Guilds
{
    // Same values as Global's states, so a status read is stored as-is with no second vocabulary to drift.
    internal static class FederatedLinkState
    {
        public const string Active          = "Active";
        // Proof no longer holds: mapping and local vault still work; only federation changes and value moves stop.
        public const string ReproofRequired = "ReproofRequired";
        // Terminal for this link; rejoining makes a new one.
        public const string Revoked         = "Revoked";
        public const string Left            = "Left";
        public const string Removed         = "Removed";
        public const string Disbanded       = "Disbanded";

        public static bool IsMember(string state) => state == Active || state == ReproofRequired;
    }

    // Codes, not sentences: the client localizes them. Global's own codes are stored as they arrive.
    internal static class FederatedLinkDetail
    {
        public const string GuildDisbanded    = "guild_disbanded";
        public const string ReproofRequired   = "reproof_required";
        public const string LinkUnknown       = "link_unknown";
        public const string DataSetChanged    = "data_set_changed";
        public const string GenerationChanged = "generation_changed";

        public static string LeftBy(string actor) => "left_by:" + (actor ?? "");
        public static string DisbandedBy(string actor) => "disbanded_by:" + (actor ?? "");
    }

    // A cache of Global's last answer, never local accounting: nothing is authorised from it, hence the timestamp.
    internal sealed class FederatedBalance
    {
        [JsonProperty("settled")]   public long Settled { get; set; }
        [JsonProperty("reserved")]  public long Reserved { get; set; }
        [JsonProperty("available")] public long Available { get; set; }
        [JsonProperty("debt")]      public long Debt { get; set; }
        [JsonProperty("frozen")]    public bool Frozen { get; set; }
        [JsonProperty("read")]      public long ReadUtcTicks { get; set; }
    }

    // Keyed by LinkId, never name: same-named guilds on two servers differ, and a rename keeps the federation.
    internal sealed class FederatedGuildLink
    {
        [JsonProperty("link_id")]    public string LinkId { get; set; } = "";
        // Global's FederationId.
        [JsonProperty("guild_id")]   public string GuildId { get; set; } = "";
        [JsonProperty("local")]      public string LocalGuild { get; set; } = "";
        // A same-name guild recreated after a disband has a different incarnation, so it inherits nothing.
        [JsonProperty("incarnation")] public string Incarnation { get; set; } = "";
        [JsonProperty("state")]      public string State { get; set; } = FederatedLinkState.Active;
        [JsonProperty("detail")]     public string Detail { get; set; } = "";
        // A restored backup signs fine under an older generation, so this is compared, not trusted.
        [JsonProperty("generation")] public int    SigningGeneration { get; set; }
        // A restore that mints a new identity must not inherit authority the original server proved.
        [JsonProperty("data_set")]   public string DataSetId { get; set; } = "";
        [JsonProperty("proved_by")]  public string ProvedBy { get; set; } = "";
        [JsonProperty("created")]    public long   CreatedUtcTicks { get; set; }
        [JsonProperty("proved")]     public long   ProvedUtcTicks { get; set; }
        [JsonProperty("balance")]    public FederatedBalance Balance { get; set; }

        // Display and local benefits only; never authorises anything Global decides.
        [JsonProperty("view")]       public Federation.FederationView View { get; set; }
        [JsonProperty("view_read")]  public long ViewReadUtcTicks { get; set; }
        [JsonProperty("rewarded")]   public List<string> RewardedObjectives { get; set; } = new List<string>();

        // Proven under the current signing generation; a link awaiting reproof may read but not act.
        [JsonIgnore] public bool AuthorityCurrent => State == FederatedLinkState.Active;
        [JsonIgnore] public bool MayMoveValue => AuthorityCurrent;
        [JsonIgnore] public bool MayRead      => FederatedLinkState.IsMember(State);
        [JsonIgnore] public bool IsLeader     => View?.Self?.IsLeader == true;
        [JsonIgnore] public bool FederationActive => View?.Federation == null || View.Federation.State != "Disbanded";
    }
}
