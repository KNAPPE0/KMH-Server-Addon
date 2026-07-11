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

        // Simple/legacy compact items (def|stuff|quality key -> count). Lossless only for proven-simple resources;
        // pre-payload complex entries here are legacy/partial.
        [JsonProperty("items")]                public Dictionary<string, int> Items
            { get; set; } = new Dictionary<string, int>();

        // State-preserving complex items (weapons/apparel/minified/comp-heavy...). Merged only by fingerprint.
        // ScribeXml is stripped from wire snapshots (kept on disk); it rides the grant on withdraw.
        [JsonProperty("item_payloads")]        public List<Items.KmhThingPayload> ItemPayloads
            { get; set; } = new List<Items.KmhThingPayload>();

        [JsonProperty("recent_transactions")]  public List<TreasuryTransaction> RecentTransactions
            { get; set; } = new List<TreasuryTransaction>();

        // Deposits held pending durable local-save confirmation (not counted in silver_balance/items - never
        // spendable until committed). Persisted on disk; blobs stripped on the wire. See PendingDeposit.
        [JsonProperty("pending_deposits")]     public List<PendingDeposit> PendingDeposits
            { get; set; } = new List<PendingDeposit>();

        // Ring of recently-committed deposit txn ids - kept server-side (not sent on the wire) so a duplicate
        // BeginPendingDeposit for an already-committed txn can't double-book. Bounded on commit.
        [JsonProperty("recent_committed_txns")] public List<string> RecentCommittedTxns
            { get; set; } = new List<string>();

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

    // A deposit whose local goods-removal is not yet confirmed durably saved. Held out of the spendable balance;
    // committed only when the client reports the txn is in a saved game, reverted if it never confirms (rollback).
    public class PendingDeposit
    {
        public const string StatePending     = "pending";
        public const string StateCommitted   = "committed";
        public const string StateReverted    = "reverted";
        public const string StateExpired     = "expired";
        public const string StateAdminReview = "admin_review";

        public const string KindSilver  = "silver";
        public const string KindItem    = "item";
        public const string KindPayload = "payload";
        // Guild donation: silver already left the donor's spendable balance; the GUILD vault is credited only when
        // the donor's save confirms. Revert/timeout refunds the donor - the guild never sees unconfirmed silver.
        public const string KindGuildDonate = "guild_donate";

        [JsonProperty("txn_id")]          public string TxnId          { get; set; } = "";
        [JsonProperty("username")]        public string Username       { get; set; } = "";
        [JsonProperty("created_utc")]     public long   CreatedUtcTicks{ get; set; } = 0;
        [JsonProperty("state")]           public string State          { get; set; } = StatePending;
        [JsonProperty("kind")]            public string Kind           { get; set; } = KindSilver;

        [JsonProperty("silver")]          public int    Silver         { get; set; } = 0;
        [JsonProperty("item_def_name")]   public string ItemDefName     { get; set; } = "";
        [JsonProperty("qty")]             public int    Qty            { get; set; } = 0;
        [JsonProperty("payloads")]        public List<Items.KmhThingPayload> Payloads { get; set; } = new List<Items.KmhThingPayload>();
        [JsonProperty("note")]            public string Note           { get; set; } = "";
        // Silver deposit fee held aside - credited to the house pool only when this deposit COMMITS, never if it reverts.
        [JsonProperty("fee")]             public int    Fee            { get; set; } = 0;
        // Target guild for KindGuildDonate entries.
        [JsonProperty("guild_name")]      public string GuildName      { get; set; } = "";
    }
}
