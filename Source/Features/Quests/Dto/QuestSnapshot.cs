using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Quests.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.Quests.Dto.QuestSnapshot.
    public class QuestSnapshot
    {
        [JsonProperty("quests")]                      public List<QuestEntry> Quests { get; set; } = new List<QuestEntry>();
        [JsonProperty("lifetime_quests_posted")]      public long             LifetimeQuestsPosted     { get; set; } = 0;
        [JsonProperty("lifetime_quests_completed")]   public long             LifetimeQuestsCompleted  { get; set; } = 0;
        [JsonProperty("lifetime_bounty_silver_paid")] public long             LifetimeBountySilverPaid { get; set; } = 0;
    }

    public class QuestEntry
    {
        public const string KindDeliverItem = "deliver_item";
        public const string KindBounty      = "bounty";
        // Quest kinds (append-only - never reorder/rename wire values).
        public const string KindEscort      = "escort";
        public const string KindDefend      = "defend";
        public const string KindHunt        = "hunt";
        public const string KindBuild       = "build";
        public const string KindCustom      = "custom";

        public const string StateOpen          = "open";
        public const string StateClaimed       = "claimed";
        public const string StateSubmitted     = "submitted";
        public const string StatePendingReview = "pending_review"; // proof submitted, awaiting poster
        public const string StateCompleted     = "completed";
        public const string StateExpired       = "expired";
        public const string StateCancelled     = "cancelled";

        public const string VisibilityPublic    = "public";
        public const string VisibilityGuildOnly = "guild_only";

        // Hunt target kinds.
        public const string HuntNone          = "none";
        public const string HuntAnimalSpecies = "animal_species";
        public const string HuntPawnKind      = "pawn_kind";
        public const string HuntNamedRaider   = "named_raider";

        // Poster-review states (Custom + verifiable-kind dispute fallback).
        public const string ReviewNotApplicable = "not_applicable";
        public const string ReviewPending       = "pending";
        public const string ReviewApproved      = "approved";
        public const string ReviewRejected      = "rejected";
        public const string ReviewNeedsMoreInfo = "needs_more_info";

        [JsonProperty("id")]                  public long   Id              { get; set; } = 0;

        [JsonProperty("kind")]                public string Kind            { get; set; } = KindDeliverItem;
        [JsonProperty("state")]               public string State           { get; set; } = StateOpen;
        [JsonProperty("visibility")]          public string Visibility      { get; set; } = VisibilityPublic;

        [JsonProperty("poster_username")]     public string PosterUsername     { get; set; } = "";
        [JsonProperty("poster_treasury_key")] public string PosterTreasuryKey  { get; set; } = "";

        [JsonProperty("title")]               public string Title           { get; set; } = "";
        [JsonProperty("description")]         public string Description     { get; set; } = "";

        [JsonProperty("bounty_silver")]       public int    BountySilver    { get; set; } = 0;
        [JsonProperty("bounty_items")]        public Dictionary<string, int> BountyItems
            { get; set; } = new Dictionary<string, int>();

        [JsonProperty("target_item_def_name")] public string TargetItemDefName { get; set; } = "";
        [JsonProperty("target_item_qty")]      public int    TargetItemQty     { get; set; } = 0;
        // 0 = any quality; 1..7 = Awful..Legendary, delivered items must match or beat it
        [JsonProperty("target_quality_index")] public int    TargetQualityIndex { get; set; } = 0;
        [JsonProperty("target_treasury_key")]  public string TargetTreasuryKey { get; set; } = "";

        [JsonProperty("posted_utc_ticks")]     public long   PostedUtcTicks  { get; set; } = 0;
        [JsonProperty("expires_utc_ticks")]    public long   ExpiresUtcTicks { get; set; } = 0;

        [JsonProperty("claimed_by_username")]  public string ClaimedByUsername  { get; set; } = "";
        [JsonProperty("claimed_utc_ticks")]    public long   ClaimedUtcTicks    { get; set; } = 0;
        [JsonProperty("completed_utc_ticks")]  public long   CompletedUtcTicks  { get; set; } = 0;

        // per-kind fields. Default-valued so old saves + the two base kinds stay wire-compatible; unused fields are
        // ignored.

        // Escort
        [JsonProperty("escort_pickup_tile")]   public int    EscortPickupTile        { get; set; } = -1;
        [JsonProperty("escort_dropoff_tile")]  public int    EscortDropoffTile       { get; set; } = -1;
        [JsonProperty("escort_target_desc")]   public string EscortTargetDescription { get; set; } = "";

        // Defend
        [JsonProperty("defend_colony_tile")]         public int  DefendColonyTile        { get; set; } = -1;
        [JsonProperty("defend_duration_game_ticks")] public long DefendDurationGameTicks { get; set; } = 0;

        // Hunt
        [JsonProperty("hunt_target_kind")]     public string HuntTargetKind    { get; set; } = HuntNone;
        [JsonProperty("hunt_target_def_name")] public string HuntTargetDefName { get; set; } = "";
        [JsonProperty("hunt_target_count")]    public int    HuntTargetCount   { get; set; } = 0;

        // Build
        [JsonProperty("build_at_tile")]            public int    BuildAtTile           { get; set; } = -1;
        [JsonProperty("build_structure_def_name")] public string BuildStructureDefName { get; set; } = "";
        [JsonProperty("build_count")]              public int    BuildCount            { get; set; } = 1;

        // Proof / poster review
        [JsonProperty("proof_text")]                 public string ProofText               { get; set; } = "";
        [JsonProperty("proof_image_url")]            public string ProofImageUrl           { get; set; } = "";
        [JsonProperty("proof_submitted_utc_ticks")]  public long   ProofSubmittedUtcTicks  { get; set; } = 0;
        [JsonProperty("review_state")]               public string ReviewState             { get; set; } = ReviewNotApplicable;
        [JsonProperty("review_note")]                public string ReviewNote              { get; set; } = "";
        [JsonProperty("review_submitted_utc_ticks")] public long   ReviewSubmittedUtcTicks { get; set; } = 0;
    }
}
