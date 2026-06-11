using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Treasury.Dto
{
    // Mirror of the patch mod's KMHPatch.Features.Treasury.Dto. TreasurySnapshot - same JSON property names, same
    // field set, same wire semantics. Drift here = the patch dialog renders broken data
    public class TreasurySnapshot
    {
        [JsonProperty("owner_key")]            public string OwnerKey         { get; set; } = "";
        [JsonProperty("is_guild_owned")]       public bool   IsGuildOwned     { get; set; } = false;

        [JsonProperty("silver_balance")]       public int    SilverBalance    { get; set; } = 0;
        [JsonProperty("lifetime_silver_in")]   public long   LifetimeSilverIn { get; set; } = 0;
        [JsonProperty("lifetime_silver_out")]  public long   LifetimeSilverOut{ get; set; } = 0;

        [JsonProperty("items")]                public Dictionary<string, int> Items
            { get; set; } = new Dictionary<string, int>();

        [JsonProperty("recent_transactions")]  public List<TreasuryTransaction> RecentTransactions
            { get; set; } = new List<TreasuryTransaction>();

        // Per-caller permissions - computed by the server when building the snapshot for a specific client (depends
        // on rank / ownership)
        [JsonProperty("can_deposit")]          public bool CanDeposit  { get; set; } = false;
        [JsonProperty("can_withdraw")]         public bool CanWithdraw { get; set; } = false;
    }

    public class TreasuryTransaction
    {
        public const string KindDeposit            = "deposit";
        public const string KindWithdraw           = "withdraw";
        public const string KindSiteRewardSilver   = "site_reward_silver";
        public const string KindSiteRewardItem     = "site_reward_item";
        public const string KindMarketplaceSale    = "marketplace_sale";
        public const string KindMarketplaceTax     = "marketplace_tax";
        public const string KindMarketplaceRefund  = "marketplace_refund";

        [JsonProperty("utc_ticks")]      public long   UtcTicks    { get; set; } = 0;
        [JsonProperty("username")]       public string Username    { get; set; } = "";
        [JsonProperty("kind")]           public string Kind        { get; set; } = "";

        [JsonProperty("amount")]         public int    Amount      { get; set; } = 0;
        [JsonProperty("item_def_name")]  public string ItemDefName { get; set; } = "";
        [JsonProperty("note")]           public string Note        { get; set; } = "";
    }
}
