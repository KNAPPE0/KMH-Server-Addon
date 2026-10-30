using System;
using System.Collections.Generic;
using System.Globalization;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Discord
{
    internal sealed class DiscordLiveSlot
    {
        [JsonProperty("key")]                    public string Key = "";
        [JsonProperty("channel_id")]             public ulong  ChannelId;
        [JsonProperty("message_id")]             public ulong  MessageId;
        [JsonProperty("window_start_utc_ticks")] public long   WindowStartTicks;
        [JsonProperty("window_end_utc_ticks")]   public long   WindowEndTicks;
        [JsonProperty("last_updated_utc_ticks")] public long   LastUpdatedTicks;
        [JsonProperty("title")]                  public string Title = "";
        [JsonProperty("last_content")]           public string LastContent = "";

        public void Reset()
        {
            MessageId = 0; WindowStartTicks = 0; WindowEndTicks = 0; LastUpdatedTicks = 0; LastContent = "";
        }
    }

    internal sealed class DiscordActivityLogState
    {
        [JsonProperty("channel_id")]             public ulong  ChannelId;
        [JsonProperty("window_start_utc_ticks")] public long   WindowStartTicks;
        [JsonProperty("message_id")]             public ulong  MessageId;
        [JsonProperty("part")]                   public int    Part = 1;
        [JsonProperty("lines")]                  public List<string> Lines = new List<string>();
    }

    // Persisted, or a restart posts a second status, market board and log for the same day.
    internal sealed class DiscordLiveState
    {
        [JsonProperty("status")] public DiscordLiveSlot Status = new DiscordLiveSlot { Key = "status" };
        [JsonProperty("market")] public DiscordLiveSlot Market = new DiscordLiveSlot { Key = "market" };
        [JsonProperty("logs")]   public List<DiscordActivityLogState> Logs = new List<DiscordActivityLogState>();

        internal static string PathForTest;
        private static string FilePath => PathForTest ?? KmhDataPaths.DiscordLiveStateFile;

        public static DiscordLiveState LoadOrDefault()
        {
            if (JsonFileStore.TryLoad(FilePath, out DiscordLiveState s) && s != null)
            {
                s.Status ??= new DiscordLiveSlot { Key = "status" };
                s.Market ??= new DiscordLiveSlot { Key = "market" };
                s.Logs ??= new List<DiscordActivityLogState>();
                return s;
            }
            return new DiscordLiveState();
        }

        public void Save()
        {
            if (!JsonFileStore.Save(FilePath, this)) ServerLog.Warn("Discord: live message state save failed");
        }
    }

    // Windows are counted from 2000-01-01 at the configured hour, so every one starts on that UTC hour.
    internal static class DiscordDayWindow
    {
        private static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static (DateTime Start, DateTime End) For(DateTime nowUtc, int startHourUtc, int periodHours)
        {
            long anchor = Epoch.AddHours(Math.Clamp(startHourUtc, 0, 23)).Ticks;
            long period = TimeSpan.FromHours(Math.Clamp(periodHours, 1, 168)).Ticks;
            long since = nowUtc.Ticks - anchor;
            long k = since >= 0 ? since / period : (since - period + 1) / period;
            var start = new DateTime(anchor + k * period, DateTimeKind.Utc);
            return (start, start.AddTicks(period));
        }

        public static string Label(DateTime start, DateTime end)
        {
            if (end - start == TimeSpan.FromDays(1)) return Day(start);
            if (start.Date == end.Date || end == start.Date.AddDays(1)) return $"{Day(start)} {Hm(start)}–{Hm(end)} UTC";
            return $"{Day(start)} {Hm(start)} – {Day(end)} {Hm(end)} UTC";
        }

        public static string EndsAt(DateTime end, DateTime nowUtc)
            => end - nowUtc <= TimeSpan.FromDays(1) ? $"{Hm(end)} UTC" : $"{Hm(end)} UTC on {end.ToString("d MMM", CultureInfo.InvariantCulture)}";

        public static string Every(int minutes)
            => minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes} min";

        private static string Day(DateTime t) => t.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        private static string Hm(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
