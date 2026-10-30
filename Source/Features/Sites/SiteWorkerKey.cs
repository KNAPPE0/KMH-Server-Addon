using System;

namespace KMHServerAddon.Features.Sites
{
    // A worker is a pawn; the account still owns it, for authority and payout.
    internal static class SiteWorkerKey
    {
        // '#' cannot occur in an RWT username, so a composite key never collides with a legacy account key.
        internal const char Separator = '#';

        public static string For(string username, int pawnLoadId)
            => string.IsNullOrEmpty(username) ? ""
             : pawnLoadId > 0 ? username + Separator + pawnLoadId.ToString(System.Globalization.CultureInfo.InvariantCulture)
             : username;   // No pawn to name: migration must not invent an id that could collide with a real one.

        // Reads both shapes: a legacy key has no separator and is its own account.
        public static string AccountOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int at = key.IndexOf(Separator);
            return at < 0 ? key : key.Substring(0, at);
        }

        public static bool IsOwnedBy(string key, string username)
            => !string.IsNullOrEmpty(username)
            && string.Equals(AccountOf(key), username, StringComparison.OrdinalIgnoreCase);

        public static bool IsPerPawn(string key) => !string.IsNullOrEmpty(key) && key.IndexOf(Separator) >= 0;
    }
}
