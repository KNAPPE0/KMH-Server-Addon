using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Marketplace.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.Marketplace.Dto. Same JsonProperty names, same defaults
    public class MarketplaceSnapshot
    {
        [JsonProperty("listings")]                  public List<MarketplaceListing> Listings { get; set; } = new List<MarketplaceListing>();
        [JsonProperty("house_silver_pool")]         public long HouseSilverPool         { get; set; } = 0;
        [JsonProperty("lifetime_trades_completed")] public long LifetimeTradesCompleted { get; set; } = 0;
        [JsonProperty("lifetime_silver_traded")]    public long LifetimeSilverTraded    { get; set; } = 0;
        [JsonProperty("server_tax_percent")]        public int  ServerTaxPercent        { get; set; } = 0;   // base marketplace tax before guild perk reduction
    }

    public class MarketplaceListing
    {
        [JsonProperty("id")]                  public long   Id              { get; set; } = 0;
        [JsonProperty("seller_username")]     public string SellerUsername  { get; set; } = "";
        [JsonProperty("seller_treasury_key")] public string SellerTreasuryKey { get; set; } = "";
        [JsonProperty("item_def_name")]       public string ItemDefName     { get; set; } = "";
        [JsonProperty("remaining_qty")]       public int    RemainingQty    { get; set; } = 0;
        [JsonProperty("original_qty")]        public int    OriginalQty     { get; set; } = 0;
        [JsonProperty("unit_price_silver")]   public int    UnitPriceSilver { get; set; } = 0;   // rounded display / old-client fallback
        // Canonical unit price in MILLI-silver (1000 = 1 silver) so items can list below 1 full silver (e.g. 550 =
        // 0.55). Buy total = round(qty * milli / 1000). 0 on an old listing -> derive from UnitPriceSilver * 1000.
        [JsonProperty("unit_price_milli")]    public int    UnitPriceMilli  { get; set; } = 0;
        [JsonProperty("listed_utc_ticks")]    public long   ListedUtcTicks  { get; set; } = 0;
        [JsonProperty("expires_utc_ticks")]   public long   ExpiresUtcTicks { get; set; } = 0;
        [JsonProperty("is_auto_listing")]     public bool   IsAutoListing   { get; set; } = false;
        [JsonProperty("quality_index")]       public int    QualityIndex    { get; set; } = 0;
        [JsonProperty("stuff_def_name")]      public string StuffDefName    { get; set; } = "";

        // Server-side: snake_case wire field for visibility ("public" / "guild_only"). Server uses it to filter the
        // listings shown to each client. Default "public" preserves pre-visibility behavior on missing field
        [JsonProperty("visibility")]          public string Visibility      { get; set; } = "public";

        // State-preserving escrow for complex items. Persisted; STRIPPED from wire snapshots (blob stays server-side,
        // moves treasury<->listing<->buyer-treasury). When set, this is a payload listing.
        [JsonProperty("escrow_payloads")]     public List<Items.KmhThingPayload> EscrowPayloads { get; set; }
        [JsonProperty("state_fingerprint")]   public string StateFingerprint { get; set; } = "";
        [JsonProperty("state_note")]          public string StateNote        { get; set; } = "";   // UI: "(tainted, q5, legacy)"
    }
}
