using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Reputation
{
    internal sealed class ReputationConfig
    {
        public int SchemaVersion { get; set; } = 1;

        public int CompletedWeight        { get; set; } =  1;
        public int ProofRejectedWeight    { get; set; } = -2;
        public int AbandonedWeight        { get; set; } = -5;
        public int RejectedAsPosterWeight { get; set; } = -1;

        public int TrustedScore     { get; set; } = 20;
        public int UnreliableBelow  { get; set; } = 0;

        private static ReputationConfig _current;
        public static ReputationConfig Current => _current ?? (_current = LoadOrDefault());
        public static void Reload() { _current = null; }

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
