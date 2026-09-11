namespace KMHServerAddon.Features.Sites
{
    // Deliberately not a resource: no passive decay and no upkeep, so a site left alone is fine.
    internal static class SiteStability
    {
        public const int Healthy = 100;

        // Output flattens to this rather than scaling linearly, or one bad event would make a site pointless.
        public const double Floor = 0.55;

        public static int Clamp(int stability) => stability < 0 ? 0 : (stability > 100 ? 100 : stability);

        // 100 -> 1.00 (full output), 50 -> ~0.78, 1 -> ~0.55 (the floor), 0 -> 0 (fallen, produces nothing).
        public static double OutputFactor(int stability)
        {
            int s = Clamp(stability);
            if (s <= 0) return 0;                       // fallen
            return Floor + (1.0 - Floor) * (s / 100.0);
        }

        public static bool IsFallen(int stability) => Clamp(stability) <= 0;

        // Priced on the damage actually undone, and pure, so the quote and the charge cannot disagree.
        public static int RepairCost(int stability, int perPoint)
        {
            int missing = Healthy - Clamp(stability);
            if (missing <= 0 || perPoint <= 0) return 0;
            long cost = (long)missing * perPoint;
            return cost > int.MaxValue ? int.MaxValue : (int)cost;
        }

        public static string Describe(int stability)
        {
            int s = Clamp(stability);
            if (s <= 0)  return "Fallen";
            if (s < 40)  return "Severely damaged";
            if (s < 80)  return "Damaged";
            return "Operational";
        }
    }
}
