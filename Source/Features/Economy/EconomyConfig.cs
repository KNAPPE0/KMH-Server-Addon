using System;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Economy
{
    internal sealed class CustomEconomyRules
    {
        public string PersonalAccess { get; set; } = "Remote";
        public string GuildAccess    { get; set; } = "Remote";

        public int    DepositCooldownSeconds  { get; set; } = 0; // 0 = no cooldown
        public int    WithdrawCooldownSeconds { get; set; } = 0;
        public double DepositFeePercent       { get; set; } = 0.0;   // paid into the house pool
        public double WithdrawFeePercent      { get; set; } = 0.0;

        public long MaxPersonalSilver { get; set; } = 0;   // 0 = no cap
        public long MaxGuildSilver    { get; set; } = 0;

        public bool RequireCaravanForItemDeposit  { get; set; } = false;
        public bool RequireCaravanForItemWithdraw { get; set; } = false;
        public bool BlockDuringRaid               { get; set; } = false;
        public bool BlockDuringHostileMapEvent    { get; set; } = false;
    }

    internal sealed class TreasuryRules
    {
        // Prices opening a vault, not a trade, so it applies in every mode; 0 = off, waived while the vault is empty.
        public int AccessFeeSilver { get; set; } = 0;

        // Enforced on every deposit whatever the mode says, because a deposit's amount is client-asserted.
        public long MaxSilverDepositPerTx  { get; set; } = 1_000_000;
        public int  MaxItemDepositQtyPerTx { get; set; } = 5_000;

        // Holds a deposit pending until the client's save is durable, or the same silver exists in colony and treasury.
        public bool RequireDurableLocalSave   { get; set; } = true;
        public int  PendingTimeoutMinutes     { get; set; } = 120;
        // Only the player's own durable save resolves a pending deposit, so one who never saves is otherwise unbounded.
        public int  MaxUnconfirmedPerPlayer   { get; set; } = 32;

        internal void Clamp()
        {
            if (MaxSilverDepositPerTx  < 0) MaxSilverDepositPerTx  = 0;   // 0 = no cap
            if (MaxItemDepositQtyPerTx < 0) MaxItemDepositQtyPerTx = 0;
            PendingTimeoutMinutes   = EconomyConfig.Bound(PendingTimeoutMinutes, 1, 24 * 60);
            MaxUnconfirmedPerPlayer = EconomyConfig.Bound(MaxUnconfirmedPerPlayer, 1, 1000);
            // Negative would pay players to open the vault; the ceiling keeps a typo from locking everyone out.
            AccessFeeSilver = EconomyConfig.Bound(AccessFeeSilver, 0, 1_000_000);
        }
    }

    // A client decides what its colony holds, so a memory editor can mint goods; these bound what reaches the economy.
    internal sealed class AntiCheatRules
    {
        // Silver-equivalent value one account may deposit in any 24 hours; 0 = no limit.
        public long DailyDepositLimit          { get; set; } = 200_000;
        // The same for accounts with fewer connected hours this season than NewPlayerHours; 0 = DailyDepositLimit.
        public long NewPlayerDailyDepositLimit { get; set; } = 25_000;
        public int  NewPlayerHours             { get; set; } = 10;
        public bool AlertStaff                 { get; set; } = true;

        internal void Clamp()
        {
            if (DailyDepositLimit          < 0) DailyDepositLimit          = 0;
            if (NewPlayerDailyDepositLimit < 0) NewPlayerDailyDepositLimit = 0;
            NewPlayerHours = EconomyConfig.Bound(NewPlayerHours, 0, 10_000);
        }
    }

    internal sealed class GuildHallSettings
    {
        // A guild with no hall is never blocked by proximity, so these flags are what force one to exist.
        public bool RequireToCreateGuild      { get; set; } = false;
        public bool RequireForTreasury        { get; set; } = false;
        public bool RequireForContributions   { get; set; } = false;
        public int  AccessRadiusTiles         { get; set; } = 10;
        public bool RequireCaravanForContribution { get; set; } = false;
        public bool RequireMemberNearHallToJoin   { get; set; } = false;
        public bool AllowRemoteInvites            { get; set; } = true;   // false = inviter must be near the hall

        internal void Clamp() => AccessRadiusTiles = EconomyConfig.Bound(AccessRadiusTiles, 0, 1000);

        // Excludes the Custom access modes, which EconomyConfig folds in - this block cannot see them.
        internal bool AnyRuleHere => RequireToCreateGuild || RequireForTreasury || RequireForContributions
                                  || RequireCaravanForContribution || RequireMemberNearHallToJoin
                                  || !AllowRemoteInvites;
    }

    internal sealed class MarketplaceRules
    {
        public int TaxPercent { get; set; } = 5;

        // Applies only where a post did not specify its own expiry.
        public int ListingLifetimeHours   { get; set; } = 168;
        public int MaxOpenListingsPerUser { get; set; } = 25;

        // Decimal silver, so a sub-1 price such as 0.55 is expressible.
        public double MinUnitPrice { get; set; } = 0.01;
        public double MaxUnitPrice { get; set; } = 100_000;

        [JsonIgnore] public long MinUnitPriceMilli => (long)Math.Round(MinUnitPrice * 1000);
        [JsonIgnore] public long MaxUnitPriceMilli => (long)Math.Round(MaxUnitPrice * 1000);

        // A near-free listing launders value to an alt; enforced only where the catalog knows the item's worth.
        public double MinPercentOfTrustedValue     { get; set; } = 2;
        public double WarnBelowTrustedValuePercent { get; set; } = 25;

        // Derived from the percent, because the enforcement math compares a 0..1 fraction.
        [JsonIgnore] public double MinFractionOfTrustedValue  => MinPercentOfTrustedValue / 100.0;
        [JsonIgnore] public double WarnFractionOfTrustedValue => WarnBelowTrustedValuePercent / 100.0;

        public bool BlockSuspiciousUnderpricedListings { get; set; } = true;

        // A closed loop: the buyer's cost never moves, only the split between seller and house.
        public bool DynamicDemandPricingEnabled { get; set; } = false;
        public int  DemandTaxSwingPercent       { get; set; } = 50;

        // Daily per-seller boom boost cap, so two accounts relisting to each other cannot drain the pool; 0 = no boost.
        public int  BoomBoostMaxPerSellerPerDay { get; set; } = 1000;

        internal void Clamp()
        {
            TaxPercent            = EconomyConfig.Bound(TaxPercent, 0, 50);
            BoomBoostMaxPerSellerPerDay = EconomyConfig.Bound(BoomBoostMaxPerSellerPerDay, 0, 10_000_000);
            DemandTaxSwingPercent = EconomyConfig.Bound(DemandTaxSwingPercent, 0, 90);
            ListingLifetimeHours   = EconomyConfig.Bound(ListingLifetimeHours, 1, 24 * 365);
            MaxOpenListingsPerUser = EconomyConfig.Bound(MaxOpenListingsPerUser, 1, 10_000);
            MinUnitPrice = EconomyConfig.BoundD(MinUnitPrice, 0.001, 1_000_000);
            MaxUnitPrice = EconomyConfig.BoundD(MaxUnitPrice, MinUnitPrice, 2_000_000);
            MinPercentOfTrustedValue     = EconomyConfig.BoundD(MinPercentOfTrustedValue, 0.0, 100.0);
            WarnBelowTrustedValuePercent = EconomyConfig.BoundD(WarnBelowTrustedValuePercent, 0.0, 100.0);
        }
    }

    internal sealed class AuctionRules
    {
        public int MaxOpenPerUser       { get; set; } = 5;
        public int DefaultDurationHours { get; set; } = 24;
        public int MaxDurationHours     { get; set; } = 72;
        public int AntiSnipeMinutes     { get; set; } = 5;

        internal void Clamp()
        {
            MaxOpenPerUser       = EconomyConfig.Bound(MaxOpenPerUser, 1, 1_000);
            DefaultDurationHours = EconomyConfig.Bound(DefaultDurationHours, 1, 24 * 30);
            MaxDurationHours     = EconomyConfig.Bound(MaxDurationHours, DefaultDurationHours, 24 * 30);
            AntiSnipeMinutes     = EconomyConfig.Bound(AntiSnipeMinutes, 0, 60);
        }
    }

    internal sealed class WantRules
    {
        public int MaxOpenPerUser       { get; set; } = 10;
        public int DefaultDurationHours { get; set; } = 72;
        public int MaxDurationHours     { get; set; } = 168;

        internal void Clamp()
        {
            MaxOpenPerUser       = EconomyConfig.Bound(MaxOpenPerUser, 1, 1_000);
            DefaultDurationHours = EconomyConfig.Bound(DefaultDurationHours, 1, 24 * 30);
            MaxDurationHours     = EconomyConfig.Bound(MaxDurationHours, DefaultDurationHours, 24 * 30);
        }
    }

    internal sealed class RoadworksCosts
    {
        // KMH-owned, and never read from RWT's own road config.
        public int SilverPerSegmentTrail   { get; set; } = 25;
        public int SilverPerSegmentRoad    { get; set; } = 75;
        public int SilverPerSegmentHighway { get; set; } = 200;
        public int MaxSegmentsPerProject   { get; set; } = 64;   // rejects absurd route payloads

        internal void Clamp()
        {
            SilverPerSegmentTrail   = EconomyConfig.Bound(SilverPerSegmentTrail,   0, 1_000_000);
            SilverPerSegmentRoad    = EconomyConfig.Bound(SilverPerSegmentRoad,    0, 1_000_000);
            SilverPerSegmentHighway = EconomyConfig.Bound(SilverPerSegmentHighway, 0, 1_000_000);
            MaxSegmentsPerProject   = EconomyConfig.Bound(MaxSegmentsPerProject, 1, 512);
        }
    }

    internal sealed class EconomyConfig
    {
        public int SchemaVersion { get; set; } = 1;

        // Every mode but Custom resolves to a preset.
        public string EconomyMode { get; set; } = "Balanced";

        // Read ONLY when EconomyMode is Custom; grouped so the file itself shows that.
        public CustomEconomyRules Custom { get; set; } = new CustomEconomyRules();

        // Kill switches that outrank every preset, so no preset can turn a disabled treasury back on.
        public bool DisablePersonalTreasury { get; set; } = false;
        public bool DisableGuildTreasury    { get; set; } = false;

        // A solo guild's vault is cleared too, since it would otherwise shelter the same silver.
        public bool ResetEconomyOnNewSave { get; set; } = false;

        public TreasuryRules    Treasury    { get; set; } = new TreasuryRules();
        public GuildHallSettings GuildHall  { get; set; } = new GuildHallSettings();
        public MarketplaceRules Marketplace { get; set; } = new MarketplaceRules();
        public AuctionRules     Auctions    { get; set; } = new AuctionRules();
        public WantRules        Wants       { get; set; } = new WantRules();
        public RoadworksCosts   Roadworks   { get; set; } = new RoadworksCosts();
        public AntiCheatRules   AntiCheat   { get; set; } = new AntiCheatRules();

        // Folds in the Custom access modes, which GuildHallSettings cannot see from inside its own block.
        [JsonIgnore] public bool AnyGuildHallRule { get; private set; }

        private static EconomyConfig _current;
        public static EconomyConfig Current => _current ?? (_current = LoadOrDefault());

        // Seam for the policy harness, which has to prove every caller resolves one effective answer.
        internal static EconomyConfig ApplyForTest(EconomyConfig cfg) { EconomyConfig prev = _current; _current = cfg; return prev; }

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
            Diagnostics.ServerLog.Diag(
                $"Economy config reloaded (tax {_current.Marketplace.TaxPercent}%, " +
                $"price {_current.Marketplace.MinUnitPrice:0.###}-{_current.Marketplace.MaxUnitPrice:0.###}, " +
                $"max {_current.Marketplace.MaxOpenListingsPerUser} listings/user, " +
                $"lifetime {_current.Marketplace.ListingLifetimeHours}h)");
        }

        // Seam for the migration harness, which has to prove a migrated file lands on values a listing can use.
        internal static EconomyConfig ClampForTest(EconomyConfig cfg) { cfg?.ClampInPlace(); return cfg; }

        private void ClampInPlace()
        {
            // A hand-edited file can null a block outright, which would throw on the first read.
            Custom      ??= new CustomEconomyRules();
            Treasury    ??= new TreasuryRules();
            GuildHall   ??= new GuildHallSettings();
            Marketplace ??= new MarketplaceRules();
            Auctions    ??= new AuctionRules();
            Wants       ??= new WantRules();
            Roadworks   ??= new RoadworksCosts();
            AntiCheat   ??= new AntiCheatRules();

            Custom.DepositCooldownSeconds  = Bound(Custom.DepositCooldownSeconds, 0, 3600);
            Custom.WithdrawCooldownSeconds = Bound(Custom.WithdrawCooldownSeconds, 0, 3600);
            Custom.DepositFeePercent       = BoundD(Custom.DepositFeePercent, 0.0, 50.0);
            Custom.WithdrawFeePercent      = BoundD(Custom.WithdrawFeePercent, 0.0, 50.0);
            if (Custom.MaxPersonalSilver < 0) Custom.MaxPersonalSilver = 0;
            if (Custom.MaxGuildSilver    < 0) Custom.MaxGuildSilver    = 0;
            // A typo lands on the shipped default, never on Standard, the most permissive preset.
            EconomyMode           = NormalizeMode(EconomyMode, "Balanced", ValidEconomyModes);
            Custom.PersonalAccess = NormalizeMode(Custom.PersonalAccess, "Remote", ValidPersonalModes);
            Custom.GuildAccess    = NormalizeMode(Custom.GuildAccess, "Remote", ValidGuildModes);

            Treasury.Clamp();
            GuildHall.Clamp();
            Marketplace.Clamp();
            Auctions.Clamp();
            Wants.Clamp();
            Roadworks.Clamp();
            AntiCheat.Clamp();

            AnyGuildHallRule = GuildHall.AnyRuleHere
                || string.Equals(Custom.GuildAccess, "GuildHallRequired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Custom.GuildAccess, "CaravanNearGuildHall", StringComparison.OrdinalIgnoreCase);
        }

        internal static int    Bound(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        internal static double BoundD(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        internal static readonly string[] ValidEconomyModes  = { "Standard", "Balanced", "Localized", "Hardcore", "Custom" };
        private static readonly string[] ValidPersonalModes = { "Remote", "ColonyOnly", "CaravanOnly", "TreasurySiteRequired", "Disabled" };
        private static readonly string[] ValidGuildModes    = { "Remote", "GuildHallRequired", "CaravanNearGuildHall", "Disabled" };

        private static string NormalizeMode(string v, string fallback, string[] valid)
        {
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            foreach (string k in valid)
                if (string.Equals(k, v.Trim(), StringComparison.OrdinalIgnoreCase)) return k;
            return fallback;
        }

        // Disabled is a kill switch, not a tuning knob, so no preset may re-enable a treasury turned off.
        public EconomyPolicy ResolvePolicy()
        {
            EconomyPolicy p = ResolveModePolicy();
            bool personalOff = DisablePersonalTreasury
                || string.Equals(p.PersonalAccess, "Disabled", StringComparison.OrdinalIgnoreCase);
            bool guildOff    = DisableGuildTreasury
                || string.Equals(p.GuildAccess, "Disabled", StringComparison.OrdinalIgnoreCase);
            return personalOff || guildOff
                ? p.WithAccess(personalOff ? "Disabled" : p.PersonalAccess, guildOff ? "Disabled" : p.GuildAccess)
                : p;
        }

        // Custom returns the granular fields verbatim; every other mode ignores them entirely.
        private EconomyPolicy ResolveModePolicy()
        {
            string m = NormalizeMode(EconomyMode, "Balanced", ValidEconomyModes).ToLowerInvariant();
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
                    return new EconomyPolicy("Custom", Custom.PersonalAccess, Custom.GuildAccess,
                        Custom.DepositCooldownSeconds, Custom.WithdrawCooldownSeconds,
                        Custom.DepositFeePercent, Custom.WithdrawFeePercent,
                        Custom.MaxPersonalSilver, Custom.MaxGuildSilver,
                        Custom.RequireCaravanForItemDeposit, Custom.RequireCaravanForItemWithdraw,
                        Custom.BlockDuringRaid, Custom.BlockDuringHostileMapEvent);
                default: // Standard: everything permissive.
                    return new EconomyPolicy("Standard", "Remote", "Remote",
                        0, 0, 0.0, 0.0, 0, 0, false, false, false, false);
            }
        }
    }

    internal readonly struct EconomyPolicy
    {
        public readonly string Mode;
        public readonly string PersonalAccess;
        public readonly string GuildAccess;
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

        public EconomyPolicy WithAccess(string personal, string guild)
            => new EconomyPolicy(Mode, personal, guild, DepositCooldownSec, WithdrawCooldownSec,
                DepositFeePct, WithdrawFeePct, MaxPersonalSilver, MaxGuildSilver,
                RequireCaravanForItemDeposit, RequireCaravanForItemWithdraw, BlockDuringRaid, BlockDuringHostileEvent);
    }
}
