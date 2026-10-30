using System;
using System.Collections.Generic;
using System.Threading;

namespace KMHServerAddon.Features.Guilds
{
    // Takes no lock: the treasury reads it under its own, so a disband that drops a guild here before handing its
    // vault back can never be followed by a credit that rebuilds the vault.
    internal static class GuildIdentity
    {
        private static Dictionary<string, string> _live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Replaced whole and never mutated, so a reader always sees one committed set.
        internal static void Publish(Dictionary<string, string> live)
            => Volatile.Write(ref _live, live ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public static string Of(string guild)
            => !string.IsNullOrEmpty(guild) && Volatile.Read(ref _live).TryGetValue(guild, out string id) ? id : null;

        // A null expected identity accepts whichever guild holds the name now.
        public static bool IsLive(string guild, string expected = null)
        {
            string id = Of(guild);
            return id != null && (expected == null || string.Equals(id, expected, StringComparison.Ordinal));
        }

        public static int LiveCount => Volatile.Read(ref _live).Count;
    }
}
