using System;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Economy
{
    internal sealed class EconomyConfig
    {
        public int SchemaVersion { get; set; } = 1;

        public int MarketplaceTaxPercent { get; set; } = 5;

        // A solo guild's vault is cleared too, since it would otherwise shelter the same silver.
        public bool ResetEconomyOnNewSave { get; set; } = false;

        // Enforced on every deposit whatever the mode says, because a deposit's amount is client-asserted.
        public long MaxSilverDepositPerTx   { get; set; } = 1_000_000;
        public int  MaxItemDepositQtyPerTx  { get; set; } = 5_000;

        // Holds a deposit pending until the client's save is durable, or the same silver exists in colony and treasury.
        public bool RequireDurableLocalSaveForDeposits { get; set; } = true;
        public int  PendingDepositTimeoutMinutes       { get; set; } = 120;
        // Only the player's own durable save resolves a pending deposit, so one who never saves is otherwise unbounded.
        public int  MaxUnconfirmedDepositsPerPlayer    { get; set; } = 32;

        // Only Custom reads the individual knobs below; every other mode resolves into a preset policy.
        public string EconomyMode                { get; set; } = "Balanced";
        public string PersonalTreasuryAccessMode { get; set; } = "Remote";   // Remote|ColonyOnly|CaravanOnly|TreasurySiteRequired|Disabled
        public string GuildTreasuryAccessMode    { get; set; } = "Remote";   // Remote|GuildHallRequired|CaravanNearGuildHall|Disabled

        public int    TreasuryDepositCooldownSeconds  { get; set; } = 0;     // 0 = no cooldown
        public int    TreasuryWithdrawCooldownSeconds { get; set; } = 0;
        public double TreasuryDepositFeePercent       { get; set; } = 0.0;   // 0..50, skimmed into the house pool
        public double TreasuryWithdrawFeePercent      { get; set; } = 0.0;
        public long   MaxPersonalTreasurySilver       { get; set; } = 0;     // 0 = no cap
        public long   MaxGuildTreasurySilver          { get; set; } = 0;
        public bool   RequireCaravanForItemDeposit    { get; set; } = false;
        public bool   RequireCaravanForItemWithdraw   { get; set; } = false;
        public bool   BlockTreasuryDuringRaid         { get; set; } = false;
        public bool   BlockTreasuryDuringHostileMapEvent { get; set; } = false;

        // A guild with no hall is never blocked by proximity, so these flags are what force one to exist.
        public bool RequireGuildHallToCreateGuild          { get; set; } = false;
        public bool RequireGuildHallForGuildTreasury       { get; set; } = false;
        public bool RequireGuildHallForGuildContributions  { get; set; } = false;
        public int  GuildHallAccessRadiusTiles             { get; set; } = 10;   // "near the hall" = within this many tiles
        public bool RequireCaravanNearGuildHallForContribution { get; set; } = false;
        public bool RequireMemberNearGuildHallToJoin        { get; set; } = false;
        public bool AllowRemoteGuildInvites                 { get; set; } = true;   // false = inviter must be near the hall

        [JsonIgnore] public bool AnyGuildHallRule { get; private set; }

        // A closed loop: the buyer's cost never moves, only the split between seller and house.
        public bool DynamicDemandPricingEnabled { get; set; } = false;
        public int  DemandTaxSwingPercent       { get; set; } = 50;

        // Applies only where a post did not specify its own expiry.
        public int MarketplaceListingLifetimeHours { get; set; } = 168; // 7 days

        public int MarketplaceMaxOpenListingsPerUser { get; set; } = 25;

        // KMH-owned, and never read from RWT's own road config.
        public int RoadworksSilverPerSegmentTrail   { get; set; } = 25;
        public int RoadworksSilverPerSegmentRoad    { get; set; } = 75;
        public int RoadworksSilverPerSegmentHighway { get; set; } = 200;
        public int RoadworksMaxSegmentsPerProject { get; set; } = 64;   // rejects absurd route payloads

        // Decimal silver, so a sub-1 price such as 0.55 is expressible.
        public double MarketplaceMinUnitPrice { get; set; } = 0.01;

        public double MarketplaceMaxUnitPrice { get; set; } = 100_000;

        [JsonIgnore] public long MarketplaceMinUnitPriceMilli => (long)Math.Round(MarketplaceMinUnitPrice * 1000);
        [JsonIgnore] public long MarketplaceMaxUnitPriceMilli => (long)Math.Round(MarketplaceMaxUnitPrice * 1000);

        // The obsolete-key prune must spare these until MigrateEconomyV3 has folded them into the decimal fields.
        internal static readonly string[] LegacyAliasKeys = { "MarketplaceMinUnitPriceMilli", "MarketplaceMaxUnitPriceMilli" };

        // A near-free listing launders value to an alt; enforced only where the catalog knows the item's worth.
        public double MarketplaceMinPercentOfTrustedValue     { get; set; } = 2;
        public double MarketplaceWarnBelowTrustedValuePercent { get; set; } = 25;

        // Derived from the percent, because the enforcement math compares a 0..1 fraction.
        [JsonIgnore] public double MarketplaceMinFractionOfTrustedValue  => MarketplaceMinPercentOfTrustedValue / 100.0;
        [JsonIgnore] public double MarketplaceWarnFractionOfTrustedValue => MarketplaceWarnBelowTrustedValuePercent / 100.0;
        public bool   MarketplaceBlockSuspiciousUnderpricedListings { get; set; } = true;

        public int AuctionMaxOpenPerUser      { get; set; } = 5;
        public int AuctionDefaultDurationHours { get; set; } = 24;
        public int AuctionMaxDurationHours    { get; set; } = 72;
        public int AuctionAntiSnipeMinutes    { get; set; } = 5;   // a late bid extends the close by this much

        public int WantMaxOpenPerUser       { get; set; } = 10;
        public int WantDefaultDurationHours { get; set; } = 72;
        public int WantMaxDurationHours     { get; set; } = 168; // 7 days

        private static EconomyConfig _current;
        public static EconomyConfig Current => _current ?? (_current = LoadOrDefault());

        public static EconomyConfig LoadOrDefault()
        {
            EconomyConfig cfg =
                JsonFileStore.TryLoad(KmhDataPaths.EconomyConfigFile, out EconomyConfig loaded) && loaded != null
                    ? loaded
                    : new EconomyConfig();
            cfg.ClampInPlace();
            return cfg;
        }

        // A brand-new server starts on Balanced rather than the permissive Standard default.
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.EconomyConfigFile))
                JsonFileStore.Save(KmhDataPaths.EconomyConfigFile, new EconomyConfig { EconomyMode = "Balanced" });
        }

        // Must run before backfill, which would otherwise lock a legacy file into the permissive Standard mode.
        public static bool MigrateLegacyModeToBalanced()
        {
            string path = KmhDataPaths.EconomyConfigFile;
            if (!System.IO.File.Exists(path) || JsonFileStore.FileHasKey(path, "EconomyMode")) return false;
            try
            {
                JObject o = JObject.Parse(System.IO.File.ReadAllText(path));
                o["EconomyMode"] = "Balanced";
                JsonFileStore.Save(path, o);
                return true;
            }
            catch (Exception ex) { Diagnostics.ServerLog.Warn($"EconomyMode migration skipped: {ex.Message}"); return false; }
        }

        public static void Reload()
        {
            _current = LoadOrDefault();
            Diagnostics.ServerLog.Info(
                $"Economy config reloaded (tax {_current.MarketplaceTaxPercent}%, " +
                $"price {_current.MarketplaceMinUnitPrice:0.###}-{_current.MarketplaceMaxUnitPrice:0.###}, " +
                $"max {_current.MarketplaceMaxOpenListingsPerUser} listings/user, " +
                $"lifetime {_current.MarketplaceListingLifetimeHours}h)");
        }

        // Seam for the migration harness, which has to prove a migrated file lands on values a listing can use.
        internal static EconomyConfig ClampForTest(EconomyConfig cfg) { cfg?.ClampInPlace(); return cfg; }

        private void ClampInPlace()
        {
            if (MaxSilverDepositPerTx  < 0) MaxSilverDepositPerTx  = 0;   // 0 = no cap
            if (MaxItemDepositQtyPerTx < 0) MaxItemDepositQtyPerTx = 0;
            PendingDepositTimeoutMinutes = Clamp(PendingDepositTimeoutMinutes, 1, 24 * 60);
            MaxUnconfirmedDepositsPerPlayer = Clamp(MaxUnconfirmedDepositsPerPlayer, 1, 1000);
            TreasuryDepositCooldownSeconds  = Clamp(TreasuryDepositCooldownSeconds, 0, 3600);
            TreasuryWithdrawCooldownSeconds = Clamp(TreasuryWithdrawCooldownSeconds, 0, 3600);
            TreasuryDepositFeePercent       = ClampD(TreasuryDepositFeePercent, 0.0, 50.0);
            TreasuryWithdrawFeePercent      = ClampD(TreasuryWithdrawFeePercent, 0.0, 50.0);
            if (MaxPersonalTreasurySilver < 0) MaxPersonalTreasurySilver = 0;
            if (MaxGuildTreasurySilver    < 0) MaxGuildTreasurySilver    = 0;
            EconomyMode = NormalizeMode(EconomyMode, "Standard", ValidEconomyModes);
            PersonalTreasuryAccessMode = NormalizeMode(PersonalTreasuryAccessMode, "Remote", ValidPersonalModes);
            GuildTreasuryAccessMode    = NormalizeMode(GuildTreasuryAccessMode, "Remote", ValidGuildModes);
            GuildHallAccessRadiusTiles = Clamp(GuildHallAccessRadiusTiles, 0, 1000);

            AnyGuildHallRule = RequireGuildHallToCreateGuild || RequireGuildHallForGuildTreasury
                || RequireGuildHallForGuildContributions || RequireCaravanNearGuildHallForContribution
                || RequireMemberNearGuildHallToJoin || !AllowRemoteGuildInvites
                || string.Equals(GuildTreasuryAccessMode, "GuildHallRequired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GuildTreasuryAccessMode, "CaravanNearGuildHall", StringComparison.OrdinalIgnoreCase);
            MarketplaceTaxPercent             = Clamp(MarketplaceTaxPercent, 0, 50);
            DemandTaxSwingPercent             = Clamp(DemandTaxSwingPercent, 0, 90);
            MarketplaceListingLifetimeHours   = Clamp(MarketplaceListingLifetimeHours, 1, 24 * 365);
            MarketplaceMaxOpenListingsPerUser = Clamp(MarketplaceMaxOpenListingsPerUser, 1, 10_000);
            RoadworksSilverPerSegmentTrail   = Clamp(RoadworksSilverPerSegmentTrail,   0, 1_000_000);
            RoadworksSilverPerSegmentRoad    = Clamp(RoadworksSilverPerSegmentRoad,    0, 1_000_000);
            RoadworksSilverPerSegmentHighway = Clamp(RoadworksSilverPerSegmentHighway, 0, 1_000_000);
            RoadworksMaxSegmentsPerProject = Clamp(RoadworksMaxSegmentsPerProject, 1, 512);
            MarketplaceMinUnitPrice           = ClampD(MarketplaceMinUnitPrice, 0.001, 1_000_000);
            MarketplaceMaxUnitPrice           = ClampD(MarketplaceMaxUnitPrice, MarketplaceMinUnitPrice, 2_000_000);
            MarketplaceMinPercentOfTrustedValue     = ClampD(MarketplaceMinPercentOfTrustedValue, 0.0, 100.0);
            MarketplaceWarnBelowTrustedValuePercent = ClampD(MarketplaceWarnBelowTrustedValuePercent, 0.0, 100.0);
            AuctionMaxOpenPerUser             = Clamp(AuctionMaxOpenPerUser, 1, 1_000);
            AuctionDefaultDurationHours       = Clamp(AuctionDefaultDurationHours, 1, 24 * 30);
            AuctionMaxDurationHours           = Clamp(AuctionMaxDurationHours, AuctionDefaultDurationHours, 24 * 30);
            AuctionAntiSnipeMinutes           = Clamp(AuctionAntiSnipeMinutes, 0, 60);
            WantMaxOpenPerUser                = Clamp(WantMaxOpenPerUser, 1, 1_000);
            WantDefaultDurationHours          = Clamp(WantDefaultDurationHours, 1, 24 * 30);
            WantMaxDurationHours              = Clamp(WantMaxDurationHours, WantDefaultDurationHours, 24 * 30);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double ClampD(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static readonly string[] ValidEconomyModes  = { "Standard", "Balanced", "Localized", "Hardcore", "Custom" };
        private static readonly string[] ValidPersonalModes = { "Remote", "ColonyOnly", "CaravanOnly", "TreasurySiteRequired", "Disabled" };
        private static readonly string[] ValidGuildModes    = { "Remote", "GuildHallRequired", "CaravanNearGuildHall", "Disabled" };

        private static string NormalizeMode(string v, string fallback, string[] valid)
        {
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            foreach (string k in valid)
                if (string.Equals(k, v.Trim(), StringComparison.OrdinalIgnoreCase)) return k;
            return fallback;
        }

        // Custom returns the granular fields verbatim; every other mode ignores them entirely.
        public EconomyPolicy ResolvePolicy()
        {
            string m = (EconomyMode ?? "Standard").Trim().ToLowerInvariant();
            switch (m)
            {
                case "balanced":
                    return new EconomyPolicy("Balanced", "Remote", "Remote",
                        depCd: 5, wdCd: 5, depFee: 1.0, wdFee: 1.0, maxPersonal: 0, maxGuild: 0,
                        reqCarDep: false, reqCarWd: false, blockRaid: false, blockHostile: false);
                case "localized":
                    return new EconomyPolicy("Localized", "ColonyOnly", "GuildHallRequired",
                        depCd: 5, wdCd: 5, depFee: 0.0, wdFee: 0.0, maxPersonal: 0, maxGuild: 0,
                        reqCarDep: true, reqCarWd: true, blockRaid: true, blockHostile: false);
                case "hardcore":
                    return new EconomyPolicy("Hardcore", "CaravanOnly", "CaravanNearGuildHall",
                        depCd: 10, wdCd: 10, depFee: 2.0, wdFee: 2.0, maxPersonal: 0, maxGuild: 0,
                        reqCarDep: true, reqCarWd: true, blockRaid: true, blockHostile: true);
                case "custom":
                    return new EconomyPolicy("Custom", PersonalTreasuryAccessMode, GuildTreasuryAccessMode,
                        TreasuryDepositCooldownSeconds, TreasuryWithdrawCooldownSeconds,
                        TreasuryDepositFeePercent, TreasuryWithdrawFeePercent,
                        MaxPersonalTreasurySilver, MaxGuildTreasurySilver,
                        RequireCaravanForItemDeposit, RequireCaravanForItemWithdraw,
                        BlockTreasuryDuringRaid, BlockTreasuryDuringHostileMapEvent);
                default: // Standard - current behavior, everything permissive.
                    return new EconomyPolicy("Standard", "Remote", "Remote",
                        0, 0, 0.0, 0.0, 0, 0, false, false, false, false);
            }
        }
    }

    internal readonly struct EconomyPolicy
    {
        public readonly string Mode;
        public readonly string PersonalAccess;   // Remote|ColonyOnly|CaravanOnly|TreasurySiteRequired|Disabled
        public readonly string GuildAccess;      // Remote|GuildHallRequired|CaravanNearGuildHall|Disabled
        public readonly int    DepositCooldownSec, WithdrawCooldownSec;
        public readonly double DepositFeePct, WithdrawFeePct;
        public readonly long   MaxPersonalSilver, MaxGuildSilver;
        public readonly bool   RequireCaravanForItemDeposit, RequireCaravanForItemWithdraw;
        public readonly bool   BlockDuringRaid, BlockDuringHostileEvent;

        public EconomyPolicy(string mode, string personal, string guild, int depCd, int wdCd, double depFee, double wdFee,
            long maxPersonal, long maxGuild, bool reqCarDep, bool reqCarWd, bool blockRaid, bool blockHostile)
        {
            Mode = mode; PersonalAccess = personal; GuildAccess = guild;
            DepositCooldownSec = depCd; WithdrawCooldownSec = wdCd;
            DepositFeePct = depFee; WithdrawFeePct = wdFee;
            MaxPersonalSilver = maxPersonal; MaxGuildSilver = maxGuild;
            RequireCaravanForItemDeposit = reqCarDep; RequireCaravanForItemWithdraw = reqCarWd;
            BlockDuringRaid = blockRaid; BlockDuringHostileEvent = blockHostile;
        }

        public bool IsStrict => !string.Equals(Mode, "Standard", StringComparison.OrdinalIgnoreCase);
    }
}
