using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Global
{
    // Read from the running server, never typed by its owner: these are the fields a listing could lie about.
    internal static class GlobalStatusProbe
    {
        public static string CurrentStatus()
        {
            if (!Maintenance.KmhReadiness.IsReady) return "Restarting";
            if (Maintenance.KmhMaintenanceGate.IsActive) return "Maintenance";
            // Up but refusing value moves: players should see that before joining.
            return Persistence.JsonFileStore.AnyStoreUnwritable ? "Degraded" : "Online";
        }

        // Simulated sessions count too: they hold real slots.
        public static int Players()
        {
            try { return Players(Simulation.SimulatedAccounts.RwtOnline()); }
            catch { return 0; }
        }

        internal static int Players(IEnumerable<string> verifiedNames) => verifiedNames?.Count(n => n != "?") ?? 0;

        // 0 until RWT loads its configs, and the browser reads 0 as "no cap", so the listing waits for the first beat.
        public static int MaxPlayers()
        {
            try { return Master.ServerConfig?.MaxPlayers is int i && i > 0 ? i : 0; }
            catch { return 0; }
        }

        // The version the owner states in Mods.json; the server process has no RimWorld to read it from.
        public static string RimWorldVersion() => Mods.ModPolicyConfig.Current.GameVersion;

        public static string RwtVersion()
        {
            // The assembly is stamped 1.0.0+<sha>, which matches no download, so it is only the fallback.
            string reported = RwtCompat.ExecutableVersion;
            if (!string.IsNullOrWhiteSpace(reported)) return reported.Trim();

            try
            {
                Assembly rwt = typeof(ServerClient).Assembly;
                string informational = rwt.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational)) return informational.Trim();
                Version asmVersion = rwt.GetName().Version;
                return asmVersion == null ? "" : asmVersion.ToString();
            }
            catch { return ""; }
        }

        // The RWT join address, never KMH's API port; the host is never guessed (unpublished is honest, wrong is not).
        // Port alone when no host: Global pairs it with the beat's source address for join tickets only, never published.
        public static string Endpoint()
        {
            try
            {
                string host = GlobalConfig.Current.PublicHost;
                int port = Master.ServerConfig?.Port ?? 0;
                if (port <= 0) return "";
                return string.IsNullOrWhiteSpace(host) ? $":{port}" : $"{host.Trim()}:{port}";
            }
            catch { return ""; }
        }

        public static List<string> Capabilities()
        {
            List<string> caps = new List<string>();
            try { caps.AddRange(KmhCapabilities.Manifest.Split(',')); } catch { }
            return caps;
        }

        // Mod profile state travels on its own revision so a config change never reads as a mod-list change.
        public static long ManifestRevision()
        {
            try { return Mods.ModProfile.Refresh().Revision; } catch { return 0; }
        }

        public static string ManifestHash()
        {
            try { return Mods.ModProfile.Current.Hash ?? ""; } catch { return ""; }
        }

        public static string ModPolicy()
        {
            try { return Mods.ModProfile.Current.PolicyName; } catch { return "Unknown"; }
        }

        public static int ModCount()
        {
            try { return Mods.ModProfile.Current.Count; } catch { return 0; }
        }

        // Global owns this revision; the heartbeat only echoes the last one it was told.
        public static long MetadataRevision()
        {
            try { return GlobalConfig.Current.LastMetadataRevision; } catch { return 0; }
        }
    }
}
