using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.WantBoard.Dto
{
    // Want-to-buy board wire shapes. Byte-identical to the patch-side DTO (matched by JsonProperty names).
    public class WantSnapshot
    {
        [JsonProperty("wants")] public List<WantDto> Wants { get; set; } = new List<WantDto>();
    }

    // A buyer's open request: "I'll buy up to QtyWanted of ItemDefName at UnitPriceSilver each." Silver is escrowed
    // out of the buyer's treasury at post time; sellers fulfill from their own treasury for the payout.
    public class WantDto
    {
        [JsonProperty("id")]                  public long   Id               { get; set; } = 0;
        [JsonProperty("buyer_username")]      public string BuyerUsername    { get; set; } = "";
        [JsonProperty("buyer_treasury_key")]  public string BuyerTreasuryKey { get; set; } = "";
        [JsonProperty("item_def_name")]       public string ItemDefName      { get; set; } = "";
        [JsonProperty("qty_wanted")]          public int    QtyWanted        { get; set; } = 0;
        [JsonProperty("qty_filled")]          public int    QtyFilled        { get; set; } = 0;
        [JsonProperty("unit_price_silver")]   public int    UnitPriceSilver  { get; set; } = 0;
        [JsonProperty("escrow_remaining")]    public long   EscrowRemaining  { get; set; } = 0; // unit price * (wanted - filled), held by the board
        [JsonProperty("listed_utc_ticks")]    public long   ListedUtcTicks   { get; set; } = 0;
        [JsonProperty("ends_utc_ticks")]      public long   EndsUtcTicks     { get; set; } = 0;
        [JsonProperty("visibility")]          public string Visibility       { get; set; } = "public";
    }
}
