using System;
using System.Collections.Generic;
using System.Globalization;

namespace KMHServerAddon.Features.Global
{
    internal enum GlobalFindingLevel { Info, Warn, Blocker }

    internal sealed class GlobalFinding
    {
        public GlobalFindingLevel Level;
        public string Code = "";
        public string Message = "";
        public string Action = "";

        public override string ToString()
            => Action.Length == 0 ? Message : Message + " " + Action;
    }

    // Pure so the boot banner, `kmh global status` and the suite all read the same answer.
    internal static class GlobalConfigCheck
    {
        public const string OfficialBaseUrl = "https://global.knappe0.games";

        public static List<GlobalFinding> Inspect(GlobalConfig cfg, bool registered)
        {
            List<GlobalFinding> found = new List<GlobalFinding>();
            if (cfg == null) return found;

            string baseUrl = (cfg.BaseUrl ?? "").Trim();

            if (!cfg.Enabled)
            {
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Info,
                    Code = "GLOBAL-OFF",
                    Message = "Colony Nexus Global: disabled.",
                    Action = "Run 'kmh global setup' to publish this server in the Colony Nexus browser.",
                });
                return found;
            }

            if (IsLocalEndpoint(baseUrl))
            {
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Blocker,
                    Code = "GLOBAL-LOCAL-ENDPOINT",
                    Message = $"This server points at a local directory at {baseUrl}, which no player can reach.",
                    Action = $"Run 'kmh global setup' to register it with {OfficialBaseUrl}.",
                });
            }
            else if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Warn,
                    Code = "GLOBAL-INSECURE-ENDPOINT",
                    Message = $"The directory endpoint {baseUrl} is not https.",
                    Action = "Set BaseUrl to an https endpoint in KMH-Data/Config/Global.json.",
                });
            }

            if (!registered)
            {
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Warn,
                    Code = "GLOBAL-NOT-REGISTERED",
                    Message = "Colony Nexus Global: not linked.",
                    Action = "Run 'kmh global setup' to publish this server.",
                });
            }

            string host = (cfg.PublicHost ?? "").Trim();
            bool listed = !string.Equals(cfg.Visibility, "Private", StringComparison.OrdinalIgnoreCase);

            // Nothing detects an "Auto" region, and a regionless listing misses every region filter.
            if (listed && GlobalConfig.IsUnsetRegion(cfg.Listing?.Region))
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Info,
                    Code = "GLOBAL-NO-REGION",
                    Message = "No region is set, so players filtering the browser by region will not see this server.",
                    Action = "Set Listing.Region in KMH-Data/Config/Global.json (for example NA, EU, AS, OC, SA, AF).",
                });

            if (host.Length == 0)
            {
                if (listed)
                    found.Add(new GlobalFinding
                    {
                        Level = GlobalFindingLevel.Warn,
                        Code = "GLOBAL-NO-ADDRESS",
                        Message = "No public address is set, so the browser shows this server but cannot offer Join.",
                        Action = "Set PublicHost in KMH-Data/Config/Global.json to the hostname players connect to.",
                    });
            }
            else if (IsPrivateHost(host))
            {
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Blocker,
                    Code = "GLOBAL-PRIVATE-ADDRESS",
                    Message = $"PublicHost '{host}' is a private or loopback address, so it is never published.",
                    Action = "Set PublicHost to the hostname players outside your network use, or clear it.",
                });
            }

            if (listed) found.AddRange(ListingLimits.Inspect(cfg));
            if (KmhModerationNotice.Current is { } notice && notice.Summary.Length > 0)
                found.Add(new GlobalFinding
                {
                    Level = notice.Blocks ? GlobalFindingLevel.Blocker : GlobalFindingLevel.Warn,
                    Code = "GLOBAL-MODERATION",
                    Message = "Colony Nexus moderation: " + notice.Summary + ".",
                    Action = notice.Remedy,
                });
            return found;
        }

        public static bool IsLocalEndpoint(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri uri)) return false;
            return IsPrivateHost(uri.Host);
        }

        // Must match Global's join-address refusal, so an owner learns here, not from a listing with no address.
        public static bool IsPrivateHost(string host)
        {
            string h = (host ?? "").Trim().Trim('[', ']').ToLowerInvariant();
            if (h.Length == 0) return false;

            if (h == "localhost" || h == "::1" || h == "0.0.0.0" || h == "::") return true;
            if (h.EndsWith(".localhost", StringComparison.Ordinal)) return true;
            if (h.EndsWith(".local", StringComparison.Ordinal)) return true;
            if (h.EndsWith(".lan", StringComparison.Ordinal)) return true;
            if (h.EndsWith(".home", StringComparison.Ordinal)) return true;
            if (h.EndsWith(".internal", StringComparison.Ordinal)) return true;

            string[] parts = h.Split('.');
            if (parts.Length != 4) return false;
            int[] o = new int[4];
            for (int i = 0; i < 4; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out o[i]) || o[i] > 255)
                    return false;

            if (o[0] == 10 || o[0] == 127 || o[0] == 0) return true;
            if (o[0] == 192 && o[1] == 168) return true;
            if (o[0] == 172 && o[1] >= 16 && o[1] <= 31) return true;
            if (o[0] == 169 && o[1] == 254) return true;
            // Carrier-grade NAT: reachable for the owner's own tests, never for the public.
            if (o[0] == 100 && o[1] >= 64 && o[1] <= 127) return true;
            return false;
        }

        public static bool AnyBlocker(List<GlobalFinding> findings)
        {
            if (findings == null) return false;
            foreach (GlobalFinding f in findings) if (f.Level == GlobalFindingLevel.Blocker) return true;
            return false;
        }
    }
}
