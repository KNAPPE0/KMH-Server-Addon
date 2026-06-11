using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Reputation
{
    // Owner-tunable reputation scoring from KMH-Data/Config/Reputation.json (score deltas + tier cutoffs). Generated
    // with defaults on first boot and clamped on load; applied at startup, so restart after editing
    internal sealed class ReputationConfig
    {
        // Score deltas per event: reward completing, punish abandoning hardest, then rejected proof, then
        // poster-side rejection.
        public int CompletedWeight        { get; set; } =  1;
        public int ProofRejectedWeight    { get; set; } = -2;
        public int AbandonedWeight        { get; set; } = -5;
        public int RejectedAsPosterWeight { get; set; } = -1;

        // Tier cutoffs: score >= TrustedScore -> Trusted; score < UnreliableBelow -> Unreliable; otherwise Neutral
        public int TrustedScore     { get; set; } = 20;
        public int UnreliableBelow  { get; set; } = 0;

        private static ReputationConfig _current;
        public static ReputationConfig Current => _current ?? (_current = LoadOrDefault());

        public static ReputationConfig LoadOrDefault()
        {
            ReputationConfig cfg =
                JsonFileStore.TryLoad(KmhDataPaths.ReputationConfigFile, out ReputationConfig loaded) && loaded != null
                    ? loaded : new ReputationConfig();
            // Keep tiers ordered: Unreliable cutoff can't sit above Trusted.
            if (cfg.UnreliableBelow > cfg.TrustedScore) cfg.UnreliableBelow = cfg.TrustedScore;
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.ReputationConfigFile))
                JsonFileStore.Save(KmhDataPaths.ReputationConfigFile, new ReputationConfig());
        }
    }
}
