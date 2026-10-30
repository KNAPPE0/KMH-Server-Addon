using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KMHServerAddon.Features.Global
{
    // The same bounds Global enforces, so an owner learns about a bad field here rather than from a refused push.
    internal static class ListingLimits
    {
        public const int Tagline = 90, Announcement = 280, Maintenance = 200, Url = 512;
        private static readonly Regex Digits = new Regex(@"^[0-9]{1,20}\z", RegexOptions.CultureInvariant);
        private static readonly Regex CollectionId = new Regex(@"[?&]id=([0-9]{1,20})(?:&|\z)", RegexOptions.CultureInvariant);
        private static readonly string[] DateFormats =
            { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mmZ" };

        public static string NormalizePlayStyle(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            return s == "pve" ? "PvE" : s == "pvp" ? "PvP" : s == "pvpve" || s == "mixed" ? "PvPvE" : "";
        }

        public static bool IsHttpsUrl(string raw)
            => string.IsNullOrEmpty(raw)
            || (raw.Length <= Url && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Length > 0);

        // A pasted steamcommunity.com link is reduced to its id, exactly as Global does.
        public static bool TryCollection(string raw, out string id)
        {
            id = "";
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return true;
            if (Digits.IsMatch(s)) { id = s; return true; }
            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri u) || u.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.Equals(u.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)) return false;
            Match m = CollectionId.Match(u.Query);
            if (!m.Success) return false;
            id = m.Groups[1].Value;
            return true;
        }

        public static bool TryMaintenanceUntil(string raw, out long unix)
        {
            unix = 0;
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return true;
            if (!DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at))
                return false;
            unix = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return unix > 0 && unix < 4102444800;
        }

        // Values Global would refuse are left off here; the config check reports them.
        public static ListingDetailsBody Build(GlobalConfig config)
        {
            ListingSettings cfg = config.Listing;
            TryCollection(cfg.WorkshopCollection, out string collection);
            TryMaintenanceUntil(cfg.MaintenanceUntilUtc, out long until);
            return new ListingDetailsBody
            {
                Tagline            = cfg.Tagline ?? "",
                PlayStyle          = NormalizePlayStyle(cfg.PlayStyle),
                NewPlayers         = cfg.NewPlayersWelcome,
                Whitelist          = cfg.Whitelist,
                ApplyUrl           = IsHttpsUrl(cfg.ApplyUrl) ? cfg.ApplyUrl ?? "" : "",
                RulesUrl           = IsHttpsUrl(cfg.RulesUrl) ? cfg.RulesUrl ?? "" : "",
                WorkshopCollection = collection,
                Announcement       = cfg.Announcement ?? "",
                MaintenanceMessage = cfg.MaintenanceMessage ?? "",
                MaintenanceUntilUnix = until,
            };
        }

        public static List<GlobalFinding> Inspect(GlobalConfig config)
        {
            ListingSettings cfg = config.Listing;
            List<GlobalFinding> found = new List<GlobalFinding>();
            void Bad(string code, string message, string action)
                => found.Add(new GlobalFinding { Level = GlobalFindingLevel.Warn, Code = code, Message = message, Action = action });
            if (!string.IsNullOrWhiteSpace(cfg.PlayStyle) && NormalizePlayStyle(cfg.PlayStyle).Length == 0)
                Bad("LISTING-PLAYSTYLE", $"PlayStyle '{cfg.PlayStyle}' is not PvE, PvP or PvPvE, so it is left off the listing.",
                    "Set Listing.PlayStyle in KMH-Data/Config/Global.json to PvE, PvP or PvPvE, or leave it blank.");
            if (!IsHttpsUrl(cfg.ApplyUrl))
                Bad("LISTING-APPLY-URL", "ApplyUrl is not an https link, so it is left off the listing.", "Use a full https:// address.");
            if (!IsHttpsUrl(cfg.RulesUrl))
                Bad("LISTING-RULES-URL", "RulesUrl is not an https link, so it is left off the listing.", "Use a full https:// address.");
            if (!IsHttpsUrl(cfg.DiscordUrl))
                Bad("LISTING-DISCORD-URL", "DiscordUrl is not an https link, so it is left off the listing.", "Use a full https:// address.");
            if (!IsHttpsUrl(cfg.WebsiteUrl))
                Bad("LISTING-WEBSITE-URL", "WebsiteUrl is not an https link, so it is left off the listing.", "Use a full https:// address.");
            if (!TryCollection(cfg.WorkshopCollection, out _))
                Bad("LISTING-COLLECTION", "WorkshopCollection is not a Workshop collection id or steamcommunity.com link.",
                    "Paste the collection's id or its https://steamcommunity.com/sharedfiles/filedetails/?id=... link.");
            if (!TryMaintenanceUntil(cfg.MaintenanceUntilUtc, out _))
                Bad("LISTING-MAINTENANCE-TIME", $"MaintenanceUntilUtc '{cfg.MaintenanceUntilUtc}' is not a date and time.",
                    "Write it in UTC as 2026-10-12 18:00, or leave it blank.");
            if (cfg.Whitelist && string.IsNullOrWhiteSpace(cfg.ApplyUrl))
                found.Add(new GlobalFinding
                {
                    Level = GlobalFindingLevel.Info, Code = "LISTING-NO-APPLY",
                    Message = "Whitelist is on but no ApplyUrl is set, so players cannot see how to apply.",
                    Action = "Set ApplyUrl to your application form or Discord channel.",
                });
            return found;
        }
    }
}
