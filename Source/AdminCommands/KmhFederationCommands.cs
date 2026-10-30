using System;
using System.Threading;
using KMHServerAddon.Features.Global.Federation;
using KMHServerAddon.Features.Global.Guilds;

namespace KMHServerAddon.AdminCommands
{
    // Read-only ('sync' only re-reads Global): membership is a guild officer's in-game decision, never the console's.
    internal static class KmhFederationCommands
    {
        public static void Run(string[] args, Action<string> reply)
        {
            switch (args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "status")
            {
                case "status": Status(reply); break;
                case "sync":   Sync(reply);   break;
                default:       reply("Usage: kmh federation status | sync"); break;
            }
        }

        private static void Status(Action<string> reply)
        {
            string why = FederationService.UnavailableReason();
            reply($"Guild Federation: {(Features.FeaturesConfig.Current.GuildFederation ? "on" : "off")}"
                + (why == "" ? ", reaching Colony Nexus Global" : $" - {Describe(why)}"));
            long oldest = FederationOutbox.OldestPendingUnix;
            reply($"  Queued reports {FederationOutbox.PendingCount} (limit {FederationOutbox.MaxEntries})"
                + (oldest > 0 ? $", oldest {Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - oldest) / 3600)}h old" : "")
                + $", departures waiting {FederationOutbox.PendingLeaves}");
            if (FederationOutbox.LostTotal > 0)
                reply($"  Never counted: {FederationOutbox.Dropped} dropped by the queue limit, {FederationOutbox.Rejected} refused by Global "
                    + $"(older than 7 days or unverifiable), {FederationOutbox.Discarded} for links that ended or batches Global could not read.");

            int shown = 0;
            foreach (FederatedGuildLink l in FederatedGuildStore.All())
            {
                shown++;
                FederationProfile p = l.View?.Federation;
                string fed = p == null ? l.GuildId : $"[{p.Tag}] {p.Name} ({p.State}, level {p.Level})";
                string read = l.ViewReadUtcTicks > 0
                    ? $"{Math.Max(0, (int)(DateTime.UtcNow - new DateTime(l.ViewReadUtcTicks, DateTimeKind.Utc)).TotalMinutes)} min ago"
                    : "never";
                reply($"  '{l.LocalGuild}': {l.State}{(l.IsLeader ? ", leads" : "")} in {fed}; read {read}"
                    + (string.IsNullOrEmpty(l.Detail) ? "" : $" - {l.Detail}"));
            }
            if (shown == 0) reply("  No guild on this server is in a federation.");
        }

        private static void Sync(Action<string> reply)
        {
            if (FederationService.UnavailableReason() != "")
            {
                reply("Guild Federation cannot reach Colony Nexus Global right now - " + Describe(FederationService.UnavailableReason()));
                return;
            }
            int read = 0, failed = 0;
            foreach (FederatedGuildLink l in FederatedGuildStore.All())
            {
                if (!l.MayRead) continue;
                try
                {
                    if (FederationService.RefreshViewAsync(l.LinkId, CancellationToken.None).GetAwaiter().GetResult()) read++;
                    else failed++;
                }
                catch (Exception) { failed++; }
            }
            reply($"Re-read {read} federation link(s) from Colony Nexus Global" + (failed > 0 ? $"; {failed} could not be read." : "."));
        }

        private static string Describe(string why)
        {
            switch (why)
            {
                case "feature.off":        return "GuildFederation is off in Config/Features.json";
                case "global.off":         return "Colony Nexus Global is switched off here ('kmh global setup' turns it on)";
                case "global.setup":       return "this server's Global identity is not registered yet ('kmh global status')";
                case "global.unsupported": return "Colony Nexus Global does not offer Federation yet";
                case "global.revoked":     return "Colony Nexus Global revoked this server's registration, for good ('kmh global status')";
                case "global.unreachable": return "Colony Nexus Global is unreachable";
                default:                   return why;
            }
        }
    }
}
