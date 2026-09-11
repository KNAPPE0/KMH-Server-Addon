using System;

namespace KMHServerAddon.Features.Frontier
{
    // Reclaim cost and reward move together with the population: a busier server is a bigger contest, not a cheaper one.
    internal static class FrontierScale
    {
        // Pure, so the curve can be proven without a server: the caller supplies both counts.
        internal static double For(int online, int known, FrontierConfig cfg)
        {
            if (cfg == null) return 1d;
            double scale = 1d
                         + Math.Max(0, online - 1) * cfg.ReclaimScalePerActivePlayer
                         + Math.Max(0, known  - 1) * cfg.ReclaimScalePerKnownPlayer;
            double ceiling = Math.Max(1d, cfg.ReclaimScaleMax);
            return scale < 1d ? 1d : (scale > ceiling ? ceiling : scale);
        }

        // Counted here and nowhere else, so cost and reward can never be sized from different populations.
        internal static double Live(FrontierConfig cfg) => For(OnlineCount(), KnownCount(), cfg);

        internal static int Cost(FrontierConfig cfg, double scale)
            => Grow(cfg?.ReclaimMaterialQty ?? 0, scale, 1, 100000);

        internal static int Reward(FrontierConfig cfg, double scale)
            => Grow(cfg?.ReclaimRewardSilver ?? 0, scale, 0, 10000000);

        private static int Grow(int baseValue, double scale, int lo, int hi)
        {
            double v = Math.Round(baseValue * (scale <= 0d ? 1d : scale), MidpointRounding.AwayFromZero);
            if (v < lo) return lo;
            return v > hi ? hi : (int)v;
        }

        private static int OnlineCount()
        {
            int n = 0;
            try
            {
                foreach (TCPNetwork.ServerClient c in TCPNetwork.Network.ServerClients.Keys)
                    if (c?.IsVerified == true) n++;
            }
            catch { }
            return n;
        }

        private static int KnownCount()
        {
            try { return PlayerStats.PlayerStatsStore.PlayerCount; }
            catch { return 0; }
        }
    }
}
