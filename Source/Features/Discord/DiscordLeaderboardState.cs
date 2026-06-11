using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    // Persistent state for the leaderboard auto-poster's live-message edit-in-place + daily rollover machinery.
    // Keeping a single message alive avoids filling the leaderboard channel with one entry per poster tick; rolling
    // that message over on a cadence produces an archive trail readable from chat history
    //
    // Persisted to KMH-Data/Discord/LeaderboardState.json so a server restart doesn't orphan the live message or
    // reset the rollover timer
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
