using System;
using System.Collections.Generic;
using KMHServerAddon.Util;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    internal sealed class ListingSettings
    {
        public string   Description        { get; set; } = "";
        public string   Tagline            { get; set; } = "";

        // The shipped "Auto" is never resolved and means "not set"; published verbatim it reads as a place name.
        public string   Region             { get; set; } = "Auto";
        public string[] Languages          { get; set; } = Array.Empty<string>();
        public string[] Tags               { get; set; } = Array.Empty<string>();

        public string   PlayStyle          { get; set; } = "";
        public bool     NewPlayersWelcome  { get; set; } = false;
        public bool     Whitelist          { get; set; } = false;
        public string   ApplyUrl           { get; set; } = "";
        public string   RulesUrl           { get; set; } = "";
        public string   DiscordUrl         { get; set; } = "";
        public string   WebsiteUrl         { get; set; } = "";
        public string   WorkshopCollection { get; set; } = "";
        public string   Announcement       { get; set; } = "";
        public string   MaintenanceMessage { get; set; } = "";
        public string   MaintenanceUntilUtc { get; set; } = "";

        [JsonIgnore]
        public string ResolvedRegion => GlobalConfig.IsUnsetRegion(Region) ? "" : Region.Trim();
    }

    // Stored with the signing key so editing Global.json can never orphan a registration or rewind a generation.
    internal sealed class GlobalRuntimeState
    {
        public int    SchemaVersion        { get; set; } = 1;
        public string RegisteredServerId   { get; set; } = "";
        public long   LastSequence         { get; set; }
        public long   LastMetadataRevision { get; set; }
        public int    SigningGeneration    { get; set; } = 1;
    }

    internal sealed class GlobalConfig
    {
        public int    SchemaVersion    { get; set; } = 1;

        // Off until the owner completes first-run setup: a server never joins a public directory by accident.
        public bool   Enabled          { get; set; } = false;

        public bool   BackendIdentity  { get; set; } = true;

        public string BaseUrl          { get; set; } = "https://global.knappe0.games";

        public string Visibility       { get; set; } = "Public";

        // Blank publishes no address: KMH never guesses, since a wrong one sends every browser player to someone else.
        public string PublicHost       { get; set; } = "";

        public ListingSettings Listing { get; set; } = new ListingSettings();

        // Operational counters only, and only when asked: never player names, addresses or file paths.
        public bool   ShareDiagnostics { get; set; } = false;

        // Never inferred from "official", a flag the directory sets itself; without this switch an operator
        // could read the console of a server they do not run.
        public bool   AllowOperatorConsole { get; set; } = false;

        // Opt-in: a report names its player, and an owner who never enabled this promised Global no player names.
        public bool   ForwardReports { get; set; } = false;

        // Opt-in for the same reason: leaderboards name players.
        public bool   PublishLeaderboards { get; set; } = false;

        public int    HeartbeatSeconds { get; set; } = 60;

        // Migrated once from an older Global.json; stored only in Identity/global-state.json.
        public string RegisteredServerId { get; set; } = "";
        public long   LastSequence       { get; set; } = 0;
        public long   LastMetadataRevision { get; set; } = 0;
        // Moves only when Global authorises an identity operation; a restored snapshot brings an old one back.
        public int    SigningGeneration  { get; set; } = 1;
        public bool ShouldSerializeRegisteredServerId() => false;
        public bool ShouldSerializeLastSequence() => false;
        public bool ShouldSerializeLastMetadataRevision() => false;
        public bool ShouldSerializeSigningGeneration() => false;

        // Old Global.json key moves; PublicListingAcknowledgedUtc was never read and is dropped.
        internal static readonly IReadOnlyList<(string From, string To)> Moves = new[]
        {
            ("Description", "Listing.Description"), ("Region", "Listing.Region"), ("Languages", "Listing.Languages"),
            ("Tags", "Listing.Tags"), ("DiscordUrl", "Listing.DiscordUrl"), ("WebsiteUrl", "Listing.WebsiteUrl"),
        };

        [JsonIgnore] internal bool StateFromLegacyFile { get; private set; }

        private static GlobalConfig _current;
        public static GlobalConfig Current => _current ?? (_current = LoadOrDefault());

        internal static string DefaultLanguage()
        {
            try
            {
                string two = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                // "iv" is the invariant culture, which names no language a player could be filtering for.
                return string.IsNullOrWhiteSpace(two) || two == "iv" ? "en" : two.ToLowerInvariant();
            }
            catch (Exception) { return "en"; }
        }

        public static bool IsUnsetRegion(string region)
        {
            string r = (region ?? "").Trim();
            return r.Length == 0 || string.Equals(r, "Auto", StringComparison.OrdinalIgnoreCase);
        }

        public static GlobalConfig LoadOrDefault()
        {
            GlobalConfig cfg = JsonFileStore.TryLoad(KmhDataPaths.GlobalConfigFile, Moves, out GlobalConfig loaded) && loaded != null
                ? loaded : new GlobalConfig();
            // The identity file wins; an older Global.json's values are only a fallback until it exists.
            if (JsonFileStore.TryLoad(KmhDataPaths.GlobalStateFile, out GlobalRuntimeState state) && state != null)
            {
                cfg.RegisteredServerId   = state.RegisteredServerId ?? "";
                cfg.LastSequence         = state.LastSequence;
                cfg.LastMetadataRevision = state.LastMetadataRevision;
                cfg.SigningGeneration    = state.SigningGeneration;
            }
            else cfg.StateFromLegacyFile = !string.IsNullOrEmpty(cfg.RegisteredServerId) || cfg.SigningGeneration != 1 || cfg.LastSequence != 0;
            cfg.Clamp();
            return cfg;
        }

        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.GlobalConfigFile))
                JsonFileStore.Save(KmhDataPaths.GlobalConfigFile, new GlobalConfig());
        }

        public static void Reload() => _current = LoadOrDefault();

        // Identity first, so a failed Global.json write never loses it and a failed identity write writes nothing.
        public bool Save()
        {
            Clamp();
            bool ok = SaveState() && JsonFileStore.Save(KmhDataPaths.GlobalConfigFile, this);
            _current = this;
            return ok;
        }

        // A heartbeat moves only the sequence, so it rewrites only the identity file.
        public bool SaveState()
        {
            bool ok = JsonFileStore.Save(KmhDataPaths.GlobalStateFile, new GlobalRuntimeState
            {
                RegisteredServerId = RegisteredServerId ?? "", LastSequence = LastSequence,
                LastMetadataRevision = LastMetadataRevision, SigningGeneration = SigningGeneration,
            });
            if (ok) StateFromLegacyFile = false;
            _current = this;
            return ok;
        }

        internal static GlobalConfig ApplyForTest(GlobalConfig cfg) { GlobalConfig prev = _current; _current = cfg; return prev; }
        internal static GlobalConfig ClampForTest(GlobalConfig cfg) { cfg.Clamp(); return cfg; }

        private void Clamp()
        {
            if (Visibility != "Public" && Visibility != "Unlisted" && Visibility != "Private") Visibility = "Public";
            // A one-second heartbeat is a denial of service against Global, and an hourly one reads as offline.
            if (HeartbeatSeconds < 30) HeartbeatSeconds = 30;
            if (HeartbeatSeconds > 600) HeartbeatSeconds = 600;
            if (string.IsNullOrWhiteSpace(BaseUrl)) BaseUrl = "https://global.knappe0.games";
            PublicHost = (PublicHost ?? "").Trim();
            // A scheme or a path here would be pasted straight into a connect box that wants host only.
            if (PublicHost.IndexOf('/') >= 0 || PublicHost.IndexOf(':') >= 0 || PublicHost.Length > 190) PublicHost = "";
            ListingSettings l = Listing ??= new ListingSettings();
            // An empty list matches no language filter; seed from the machine's culture, not a hardcoded English.
            l.Languages ??= Array.Empty<string>();
            if (l.Languages.Length == 0) l.Languages = new[] { DefaultLanguage() };
            l.Tags ??= Array.Empty<string>();
            l.Description ??= "";
            l.Description = KmhSafe.Cap(l.Description, 2000);
            if (l.Languages.Length > 8) { string[] x = l.Languages; Array.Resize(ref x, 8); l.Languages = x; }
            if (l.Tags.Length > 12) { string[] t = l.Tags; Array.Resize(ref t, 12); l.Tags = t; }
            l.Region ??= "Auto";
            l.DiscordUrl ??= "";
            l.WebsiteUrl ??= "";
            l.Tagline = Cut(l.Tagline, ListingLimits.Tagline);
            l.Announcement = Cut(l.Announcement, ListingLimits.Announcement);
            l.MaintenanceMessage = Cut(l.MaintenanceMessage, ListingLimits.Maintenance);
            l.ApplyUrl = (l.ApplyUrl ?? "").Trim();
            l.RulesUrl = (l.RulesUrl ?? "").Trim();
            l.WorkshopCollection = (l.WorkshopCollection ?? "").Trim();
            l.MaintenanceUntilUtc = (l.MaintenanceUntilUtc ?? "").Trim();
            l.PlayStyle = (l.PlayStyle ?? "").Trim();
        }

        private static string Cut(string s, int max)
        {
            return KmhSafe.Cap((s ?? "").Trim(), max);
        }
    }
}
