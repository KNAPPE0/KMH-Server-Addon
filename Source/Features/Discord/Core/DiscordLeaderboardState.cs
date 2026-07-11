using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    // Leaderboard live-message ids + rollover timer, persisted so a restart doesn't orphan the message or reset the
    // rollover.
    internal class DiscordLeaderboardState
    {
        [JsonProperty("live_message_id")]
        public ulong LiveMessageId { get; set; } = 0;

        [JsonProperty("live_started_utc_ticks")]
        public long LiveStartedUtcTicks { get; set; } = 0;

        [JsonProperty("last_updated_utc_ticks")]
        public long LastUpdatedUtcTicks { get; set; } = 0;

        public static DiscordLeaderboardState LoadOrDefault()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.DiscordLeaderboardStateFile, out DiscordLeaderboardState s) && s != null)
            {
                return s;
            }
            return new DiscordLeaderboardState();
        }

        public void Save()
        {
            if (!JsonFileStore.Save(KmhDataPaths.DiscordLeaderboardStateFile, this))
            {
                ServerLog.Warn("Discord: leaderboard state save failed");
            }
        }
    }
}
