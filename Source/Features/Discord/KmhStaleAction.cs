namespace KMHServerAddon.Features.Discord
{
    // A posted interaction outlives what it names, and ids restart at 1 after a season reset.
    internal static class KmhStaleAction
    {
        public static bool StillCurrent(long builtUnderGeneration)
            => builtUnderGeneration == Economy.KmhEconomyReset.Generation;

        public static string Stamp() => Economy.KmhEconomyReset.Generation.ToString();
    }
}
