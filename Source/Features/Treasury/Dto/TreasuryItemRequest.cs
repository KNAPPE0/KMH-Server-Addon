using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Treasury.Dto
{
    // Every field is optional, so an old client that sends no payloads or fingerprint still works.
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
