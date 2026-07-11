using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Treasury.Dto
{
    // Deposit/withdraw request. Legacy path uses item_def_name + qty; payload path uses payloads (deposit) or
    // fingerprint (withdraw). All fields optional so old clients (no payloads/fingerprint) still work.
    public class TreasuryItemRequest
    {
        [JsonProperty("item_def_name")] public string ItemDefName { get; set; } = "";
        [JsonProperty("qty")]           public int    Qty         { get; set; } = 0;
        [JsonProperty("fingerprint")]   public string Fingerprint { get; set; } = "";
        [JsonProperty("payloads")]      public List<Items.KmhThingPayload> Payloads { get; set; }
        // Durable-deposit txn id (new clients). Empty from old clients -> immediate-credit legacy path.
        [JsonProperty("txn_id")]        public string TxnId       { get; set; } = "";
    }
}
