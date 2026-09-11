using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Treasury.Dto
{
    // Mirrors the patch-side DTO - a field changed here has to change there too.
    public class TreasurySnapshot
    {
        [JsonProperty("owner_key")]            public string OwnerKey         { get; set; } = "";
        [JsonProperty("is_guild_owned")]       public bool   IsGuildOwned     { get; set; } = false;

        [JsonProperty("silver_balance")]       public int    SilverBalance    { get; set; } = 0;
        [JsonProperty("lifetime_silver_in")]   public long   LifetimeSilverIn { get; set; } = 0;
        [JsonProperty("lifetime_silver_out")]  public long   LifetimeSilverOut{ get; set; } = 0;

        // Keyed def|stuff|quality, and lossless only for proven-simple resources.
        [JsonProperty("items")]                public Dictionary<string, int> Items
            { get; set; } = new Dictionary<string, int>();

        // Merged only by fingerprint, and ScribeXml is stripped from the wire while staying on disk.
        [JsonProperty("item_payloads")]        public List<Items.KmhThingPayload> ItemPayloads
            { get; set; } = new List<Items.KmhThingPayload>();

        [JsonProperty("recent_transactions")]  public List<TreasuryTransaction> RecentTransactions
            { get; set; } = new List<TreasuryTransaction>();

        // Never counted in silver_balance or items, so nothing here is spendable until it commits.
        [JsonProperty("pending_deposits")]     public List<PendingDeposit> PendingDeposits
            { get; set; } = new List<PendingDeposit>();

        // Server-side only, so a duplicate BeginPendingDeposit for an already-committed txn cannot double-book.
        [JsonProperty("recent_committed_txns")] public List<string> RecentCommittedTxns
            { get; set; } = new List<string>();

        // The id alone does not say what was credited, and without that a commit could never be undone.
        [JsonProperty("committed_deposits")] public List<PendingDeposit> CommittedDeposits
            { get; set; } = new List<PendingDeposit>();

        // Server-side only, written in the same commit that removes the payloads, so a crash cannot leave them owned by nobody.
        [JsonProperty("pending_takes")] public List<PendingTake> PendingTakes
            { get; set; } = new List<PendingTake>();

        // A lower generation arriving means the client loaded an older save, which is the duplication rollback.
        [JsonProperty("last_save_generation")] public long LastSaveGeneration { get; set; } = 0;

        // Computed per caller when the snapshot is built, because they depend on rank and ownership.
        [JsonProperty("can_deposit")]          public bool CanDeposit  { get; set; } = false;
        [JsonProperty("can_withdraw")]         public bool CanWithdraw { get; set; } = false;

        // Stamped on the outgoing copy only, so a late snapshot cannot overwrite a newer one on the client.
        [JsonProperty("revision")]             public long Revision    { get; set; } = 0;
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

    // Committed only once the client reports the txn is in a saved game, so goods removed locally are not lost.
    public class PendingDeposit
    {
        // Only two states exist: reconcile skips anything that is not Pending, so a third would hold value forever.
        public const string StatePending     = "pending";
        public const string StateCommitted   = "committed";

        public const string KindSilver  = "silver";
        public const string KindItem    = "item";
        public const string KindPayload = "payload";
        // The guild vault is credited only on confirm, so it never sees silver the donor might roll back.
        public const string KindGuildDonate = "guild_donate";

        [JsonProperty("txn_id")]          public string TxnId          { get; set; } = "";
        [JsonProperty("username")]        public string Username       { get; set; } = "";
        [JsonProperty("created_utc")]     public long   CreatedUtcTicks{ get; set; } = 0;
        [JsonProperty("committed_utc")]   public long   CommittedUtcTicks { get; set; } = 0;   // set when it became spendable
        [JsonProperty("state")]           public string State          { get; set; } = StatePending;
        [JsonProperty("kind")]            public string Kind           { get; set; } = KindSilver;

        [JsonProperty("silver")]          public int    Silver         { get; set; } = 0;
        [JsonProperty("item_def_name")]   public string ItemDefName     { get; set; } = "";
        [JsonProperty("qty")]             public int    Qty            { get; set; } = 0;
        [JsonProperty("payloads")]        public List<Items.KmhThingPayload> Payloads { get; set; } = new List<Items.KmhThingPayload>();
        [JsonProperty("note")]            public string Note           { get; set; } = "";
        // Diagnostic only: a stale pending and one whose goods a later save removed look alike without it.
        [JsonProperty("save_gen_at_open")] public long  SaveGenAtOpen  { get; set; } = 0;
        // Held aside and credited to the house pool only on commit, never on a revert.
        [JsonProperty("fee")]             public int    Fee            { get; set; } = 0;
        [JsonProperty("guild_name")]      public string GuildName      { get; set; } = "";
    }

    // Payloads gone from the vault under a take marker but not yet named by a transaction row; the vault owns them until a row does.
    public class PendingTake
    {
        [JsonProperty("marker")]        public string TakeMarker   { get; set; } = "";
        // The key any return is deduplicated on, so the inline refund and boot recovery cannot both pay it back.
        [JsonProperty("refund_marker")] public string RefundMarker { get; set; } = "";
        [JsonProperty("username")]      public string Username     { get; set; } = "";
        [JsonProperty("taken_utc")]     public long   TakenUtcTicks{ get; set; } = 0;
        [JsonProperty("note")]          public string Note         { get; set; } = "";
        [JsonProperty("payloads")]      public List<Items.KmhThingPayload> Payloads { get; set; } = new List<Items.KmhThingPayload>();
    }
}
