using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Auctions.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.Auctions.Dto - same JsonProperty names + defaults.
    public class AuctionSnapshot
    {
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
    }
}
