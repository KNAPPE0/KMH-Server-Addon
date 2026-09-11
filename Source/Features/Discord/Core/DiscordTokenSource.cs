using System;

namespace KMHServerAddon.Features.Discord
{
    // The environment wins, so an owner can keep the token out of a config file they might share.
    internal static class DiscordTokenSource
    {
        internal const string EnvVar = "KMH_DISCORD_BOT_TOKEN";

        internal static string Resolve(string configured)
        {
            string env = SafeEnv();
            string token = !string.IsNullOrWhiteSpace(env) ? env.Trim() : (configured ?? "").Trim();
            if (token.Length > 0) Maintenance.KmhRedact.RegisterSecret(token);
            return token;
        }

        internal static bool FromEnvironment => !string.IsNullOrWhiteSpace(SafeEnv());

        // Names the source, never the value.
        internal static string Describe(string configured = null)
        {
            if (FromEnvironment) return $"environment ({EnvVar})";
            return string.IsNullOrWhiteSpace(configured) && string.IsNullOrWhiteSpace(TokenFromConfig())
                ? "not set" : "Discord.json";
        }

        private static string TokenFromConfig()
        {
            try { return DiscordConfig.LoadOrDefault()?.Bot?.Token; } catch { return null; }
        }

        private static string SafeEnv()
        {
            try { return Environment.GetEnvironmentVariable(EnvVar); } catch { return null; }
        }
    }
}
