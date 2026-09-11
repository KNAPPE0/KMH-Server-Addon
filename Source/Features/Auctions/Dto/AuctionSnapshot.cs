using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Auctions.Dto
{
    // JsonProperty names and defaults must stay identical to the patch mod's mirror of this DTO.
    public class AuctionSnapshot
    {
        // Monotonic under the store lock: two transports can deliver snapshots out of order, and the client drops the older one.
        [JsonProperty("revision")] public long Revision { get; set; } = 0;
        [JsonProperty("auctions")] public List<AuctionDto> Auctions { get; set; } = new List<AuctionDto>();
    }

    public class AuctionDto
    {
        [JsonProperty("id")]                  public long   Id                { get; set; } = 0;
        [JsonProperty("seller_username")]     public string SellerUsername    { get; set; } = "";
        [JsonProperty("seller_treasury_key")] public string SellerTreasuryKey { get; set; } = "";
        [JsonProperty("item_def_name")]       public string ItemDefName       { get; set; } = "";
        [JsonProperty("stuff_def_name")]      public string StuffDefName      { get; set; } = "";
        [JsonProperty("quality_index")]       public int    QualityIndex      { get; set; } = 0;
        [JsonProperty("qty")]                 public int    Qty               { get; set; } = 0;
        [JsonProperty("starting_bid")]        public long   StartingBid       { get; set; } = 0;
        [JsonProperty("min_increment")]       public long   MinIncrement      { get; set; } = 1;
        [JsonProperty("buyout_silver")]       public long   BuyoutSilver      { get; set; } = 0;  // 0 = no buyout
        [JsonProperty("current_bid")]         public long   CurrentBid        { get; set; } = 0;  // 0 = no bids yet
        [JsonProperty("high_bidder")]         public string HighBidder        { get; set; } = "";
        [JsonProperty("bid_count")]           public int    BidCount          { get; set; } = 0;
        [JsonProperty("listed_utc_ticks")]    public long   ListedUtcTicks    { get; set; } = 0;
        [JsonProperty("ends_utc_ticks")]      public long   EndsUtcTicks      { get; set; } = 0;
        [JsonProperty("visibility")]          public string Visibility        { get; set; } = "public";

        // Persisted but stripped from the wire, since an item's full state is nobody else's business.
        [JsonProperty("escrow_payloads")]     public List<Items.KmhThingPayload> EscrowPayloads { get; set; }
        [JsonProperty("state_fingerprint")]   public string StateFingerprint  { get; set; } = "";
        [JsonProperty("state_note")]          public string StateNote         { get; set; } = "";

        public AuctionDto ShallowClone() => (AuctionDto)MemberwiseClone();
    }
}
