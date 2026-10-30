using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    // Persisted, or a restart orphans the day's live board and posts a second one beside it.
    internal class DiscordLeaderboardState
    {
        // One slot per configured board ("auto", "guilds", ...), each its own message.
        [JsonProperty("boards")]
        public List<DiscordLiveSlot> Boards { get; set; } = new List<DiscordLiveSlot>();

        internal static string PathForTest;
        private static string FilePath => PathForTest ?? KmhDataPaths.DiscordLeaderboardStateFile;

        public DiscordLiveSlot Slot(string key)
        {
            DiscordLiveSlot s = Boards.Find(b => b != null && b.Key == key);
            if (s == null) Boards.Add(s = new DiscordLiveSlot { Key = key });
            return s;
        }

        // An older file holds one embed message id instead; that message is left as it was and a new day starts.
        public static DiscordLeaderboardState LoadOrDefault()
        {
            if (JsonFileStore.TryLoad(FilePath, out DiscordLeaderboardState s) && s != null)
            {
                s.Boards ??= new List<DiscordLiveSlot>();
                s.Boards.RemoveAll(b => b == null || string.IsNullOrEmpty(b.Key));
                return s;
            }
            return new DiscordLeaderboardState();
        }

        public void Save()
        {
            if (!JsonFileStore.Save(FilePath, this))
            {
                ServerLog.Warn("Discord: leaderboard state save failed");
            }
        }
    }
}
