using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Marketplace.Dto
{
    // Mirrors the patch mod's DTO of the same name - JsonProperty names and defaults must match on both sides.
    public class MarketplaceSnapshot
    {
        // Monotonic under the store lock: two transports can deliver snapshots out of order, and the client drops the older one.
        [JsonProperty("revision")] public long Revision { get; set; } = 0;
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
        // Canonical price in milli-silver (1000 = 1 silver); 0 on an old listing means derive it from UnitPriceSilver.
        [JsonProperty("unit_price_milli")]    public int    UnitPriceMilli  { get; set; } = 0;
        [JsonProperty("listed_utc_ticks")]    public long   ListedUtcTicks  { get; set; } = 0;
        [JsonProperty("expires_utc_ticks")]   public long   ExpiresUtcTicks { get; set; } = 0;
        [JsonProperty("is_auto_listing")]     public bool   IsAutoListing   { get; set; } = false;
        [JsonProperty("quality_index")]       public int    QualityIndex    { get; set; } = 0;
        [JsonProperty("stuff_def_name")]      public string StuffDefName    { get; set; } = "";

        // A missing field defaults to "public", which is what a pre-visibility listing has to keep behaving as.
        [JsonProperty("visibility")]          public string Visibility      { get; set; } = "public";

        // Persisted but stripped from wire snapshots - the blob never leaves the server.
        [JsonProperty("escrow_payloads")]     public List<Items.KmhThingPayload> EscrowPayloads { get; set; }
        [JsonProperty("state_fingerprint")]   public string StateFingerprint { get; set; } = "";
        [JsonProperty("state_note")]          public string StateNote        { get; set; } = "";   // UI: "(tainted, q5, legacy)"

        public MarketplaceListing ShallowClone() => (MarketplaceListing)MemberwiseClone();
    }
}
