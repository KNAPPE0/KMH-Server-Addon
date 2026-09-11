using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.WantBoard.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class WantSnapshot
    {
        // Monotonic under the store lock: two transports can deliver snapshots out of order, and the client drops the older one.
        [JsonProperty("revision")] public long Revision { get; set; } = 0;
        [JsonProperty("wants")] public List<WantDto> Wants { get; set; } = new List<WantDto>();
    }

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

        // Default to clean simple items, so a buyer is never handed junk gear they did not opt into.
        [JsonProperty("min_quality")]         public int    MinQuality       { get; set; } = 0;   // 0 = any
        [JsonProperty("required_stuff")]      public string RequiredStuff    { get; set; } = "";   // "" = any material
        [JsonProperty("allow_complex")]       public bool   AllowComplex     { get; set; } = false; // accept full-state items
        [JsonProperty("allow_tainted")]       public bool   AllowTainted     { get; set; } = false;
        [JsonProperty("allow_damaged")]       public bool   AllowDamaged     { get; set; } = false;

        public WantDto ShallowClone() => (WantDto)MemberwiseClone();
    }
}
