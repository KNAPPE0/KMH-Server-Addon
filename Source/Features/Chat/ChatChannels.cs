using System;

namespace KMHServerAddon.Features.Chat
{
    // Format only: nothing here authorizes anyone, so a handler must still check the caller itself.
    internal static class ChatChannels
    {
        public const string Server = "server";
        private const string GuildPrefix = "guild:";
        private const string DmPrefix    = "dm:";

        public static string Guild(string guildName) => GuildPrefix + (guildName ?? "");

        private const char Sep = '|';

        // A '|' in a name would collide distinct pairs: ("a","b|c") and ("a|b","c") both yield "dm:a|b|c".
        public static bool CanDm(string username)
            => !string.IsNullOrEmpty(username) && username.IndexOf(Sep) < 0;

        // Lower-cased and ordered so a pair maps to one channel whichever way round, and whatever the casing.
        public static string Dm(string a, string b)
        {
            if (!CanDm(a) || !CanDm(b)) return "";
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            bool aFirst = string.CompareOrdinal(a, b) <= 0;
            return aFirst ? $"{DmPrefix}{a}{Sep}{b}" : $"{DmPrefix}{b}{Sep}{a}";
        }

        public static bool IsWellFormed(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return false;
            if (channel == Server) return true;
            if (channel.StartsWith(GuildPrefix, StringComparison.Ordinal)) return channel.Length > GuildPrefix.Length;
            return TryParseDm(channel, out _, out _);
        }

        public static bool TryParseGuild(string channel, out string guildName)
        {
            guildName = null;
            if (channel == null || !channel.StartsWith(GuildPrefix, StringComparison.Ordinal)) return false;
            guildName = channel.Substring(GuildPrefix.Length);
            return guildName.Length > 0;
        }

        public static bool TryParseDm(string channel, out string a, out string b)
        {
            a = b = null;
            if (channel == null || !channel.StartsWith(DmPrefix, StringComparison.Ordinal)) return false;
            string rest = channel.Substring(DmPrefix.Length);
            int bar = rest.IndexOf('|');
            if (bar <= 0 || bar >= rest.Length - 1) return false;
            a = rest.Substring(0, bar);
            b = rest.Substring(bar + 1);
            return a.Length > 0 && b.Length > 0 && b.IndexOf('|') < 0;
        }

        public static bool IsDmParticipant(string user, string channel)
        {
            if (string.IsNullOrEmpty(user) || !TryParseDm(channel, out string a, out string b)) return false;
            return string.Equals(user, a, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
