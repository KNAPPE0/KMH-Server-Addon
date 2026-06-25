using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Notifications.Dto
{
    // Batch of offline notices delivered to a client on login. Byte-identical to the patch-side DTO.
    public class NotificationBatch
    {
        [JsonProperty("notifications")] public List<NotificationDto> Notifications { get; set; } = new List<NotificationDto>();
    }

    public class NotificationDto
    {
        [JsonProperty("tone")]      public string Tone     { get; set; } = "neutral"; // positive / neutral / negative
        [JsonProperty("title")]     public string Title    { get; set; } = "";
        [JsonProperty("body")]      public string Body     { get; set; } = "";
        [JsonProperty("utc_ticks")] public long   UtcTicks { get; set; } = 0;
    }
}
