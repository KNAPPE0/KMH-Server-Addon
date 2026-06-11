namespace KMHServerAddon.Util
{
    // One central formatter for silver amounts across every KMH surface (Discord embeds, console logs, snapshot
    // debug output, server commands). Same shape player-side uses in SilverFmt.Format so chat lines that originate
    // on the server read identically to ones that originate in-game
    //
    // Format: leading "$" + comma-grouped digits. Negatives keep the sign inside the dollar marker ("-$50") for
    // legibility in transaction logs. Zero renders as "$0" rather than "" - explicit is better than blank for
    // ledger-style output
    //
    // Why: the bug tracker called out silver formatting drift across the codebase ("100s" vs "$100"). Concentrating
    // the format in one helper makes a global style change a one-file edit, and keeps Marketplace / Treasury /
    // Quest / Leaderboard / showcase output visually consistent
    internal static class SilverFmt
    {
        public static string Format(int amount)  => Format((long)amount);
        public static string Format(long amount) => amount < 0
            ? "-$" + (-amount).ToString("N0")
            :  "$" +   amount.ToString("N0");
    }
}
