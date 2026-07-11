using System;
using KMHServerAddon.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Economy
{
    // Server economy tuning from KMH-Data/Config/Economy.json. Generated with defaults on first boot; values are
    // clamped on load so a hand-edit can't push it unsafe (negative caps, >100% tax). Reload() re-reads at runtime.
    internal sealed class EconomyConfig
    {
        // Schema version for forward-compatible migrations (absent in old files = 1, the baseline). All changes so
        // far are additive, so nothing to migrate yet - this is the anchor a future field rename would key on.
        public int SchemaVersion { get; set; } = 1;

        // Marketplace house tax (0..50). Skimmed from each sale into the house silver pool; reduced per seller by
        // their guild's MarketplaceTaxReduction perk
        public int MarketplaceTaxPercent { get; set; } = 5;

        // Anti-exploit: when a player starts a new save/scenario, clear their personal treasury so they can't farm
        // starting resources by depositing, resetting, and repeating. Off by default; a backup is taken before any
        // reset. A solo guild's vault is cleared too (it's a personal shelter otherwise). See 'kmh treasury-reset'.
        public bool ResetEconomyOnNewSave { get; set; } = false;

        // Anti-mint: reject a single silver/item deposit larger than this. Deposits are client-trusted (the server
        // can't see the caravan), so this bounds a modified client. Keep generous - the client removes the goods
        // before sending, so a legit deposit at or under the cap must never be rejected. 0 = no cap.
        public long MaxSilverDepositPerTx   { get; set; } = 100_000_000;
        public int  MaxItemDepositQtyPerTx  { get; set; } = 100_000;

        // Dupe guard: a deposit stays PENDING until the client confirms its goods-removal is durably saved, so the same
        // silver can't be in the colony AND a spendable treasury. Old clients (no txn id) fall back to immediate credit.
        public bool RequireDurableLocalSaveForDeposits { get; set; } = true;
        public int  PendingDepositTimeoutMinutes       { get; set; } = 120;   // unconfirmed pending -> reverted; long enough that a slow-saving player's later save still confirms in time
        public bool BlockSpendOfPendingDeposits        { get; set; } = true;  // pending never counts as spendable (structural)
        public bool RequireSyncedLocalSaveForHighRiskEconomy { get; set; } = false; // reserved: gate high-risk flows when save-sync unknown

        // --- economy / treasury access modes. Standard/Remote defaults preserve current behavior. ---
        // EconomyMode is a preset: Standard (current behavior), Balanced (light cooldowns/fees), Localized (needs
        // colony/caravan context), Hardcore (strict + block during danger), or Custom (uses the granular fields below
        // verbatim). Presets resolve into an EconomyPolicy; only Custom reads the individual knobs directly.
        public string EconomyMode                { get; set; } = "Standard";
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

        // --- optional physical Guild Hall rules. ALL disabled by default -> standard guild behavior unchanged.
        // A guild's hall is a world-tile record stored on the guild; these gate guild actions on having/being near it.
        // Compatibility: a guild with no hall is never blocked by proximity (client reports "near" vacuously); the
        // Require* flags below are what force a hall to exist. Integrates with P7 GuildTreasuryAccessMode.
        public bool RequireGuildHallToCreateGuild          { get; set; } = false;
        public bool RequireGuildHallForGuildTreasury       { get; set; } = false;
        public bool RequireGuildHallForGuildContributions  { get; set; } = false;
        public int  GuildHallAccessRadiusTiles             { get; set; } = 10;   // "near the hall" = within this many tiles
        public bool RequireCaravanNearGuildHallForContribution { get; set; } = false;
        public bool RequireMemberNearGuildHallToJoin        { get; set; } = false;
        public bool AllowRemoteGuildInvites                 { get; set; } = true;   // false = inviter must be near the hall

        // Computed on load: is any Guild Hall restriction active? Drives the startup banner + audit. Not persisted.
        [JsonIgnore] public bool AnyGuildHallRule { get; private set; }

        // Dynamic supply/demand pricing (opt-in, off by default so existing servers are unchanged). When on, the
        // house tax on a sale flexes with the item's live supply (open listings) vs demand (open want-board orders):
        // in-demand goods get a tax rebate (seller keeps more), gluts get a surcharge (more flows to the house pool,
        // which funds global-quest rewards). Closed loop - buyer cost never changes, only the seller/house split.
        public bool DynamicDemandPricingEnabled { get; set; } = false;
        // Max percentage-points the demand swing can move the tax in either direction (clamped to a 0..90 final tax).
        public int  DemandTaxSwingPercent       { get; set; } = 50;

        // Hours an unsold listing lives before the sweeper refunds remaining stock to the seller's treasury. Used
        // when a post doesn't specify its own expiry
        public int MarketplaceListingLifetimeHours { get; set; } = 168; // 7 days

        // Hard cap on simultaneous open listings per seller - anti-spam.
        public int MarketplaceMaxOpenListingsPerUser { get; set; } = 25;

        // Silver-per-unit floor and ceiling for any listing - anti-flooding + overflow guard
        public int MarketplaceMinUnitPrice { get; set; } = 1;

        public int MarketplaceMaxUnitPrice { get; set; } = 100_000;

        // --- optional underpricing controls (off/loose by default so standard servers allow cheap listings) ---
        // A listing's unit price is compared to the item's trusted server-side market value (client-reported catalog
        // value). Punishing servers can warn or block suspiciously cheap transfers; standard servers leave these off.
        public double MarketplaceMinPercentOfTrustedValue     { get; set; } = 0.0;   // 0 = no floor; e.g. 0.5 = must be >=50% of value
        public double MarketplaceWarnBelowTrustedValuePercent { get; set; } = 0.25;  // audit-warn below this fraction of value (0 = never)
        public bool   MarketplaceBlockSuspiciousUnderpricedListings { get; set; } = false; // enforce the min-percent floor as a hard block
        public double MarketplaceListingFeePercent            { get; set; } = 0.0;   // reserved: post fee as % of total ask (0 = none)

        // --- auctions ---
        public int AuctionMaxOpenPerUser      { get; set; } = 5;
        public int AuctionDefaultDurationHours { get; set; } = 24;
        public int AuctionMaxDurationHours    { get; set; } = 72;
        public int AuctionAntiSnipeMinutes    { get; set; } = 5;   // a late bid extends the close by this much

        // --- want-to-buy board (buyers escrow silver up front; sellers fulfill from treasury) ---
        public int WantMaxOpenPerUser       { get; set; } = 10;
        public int WantDefaultDurationHours { get; set; } = 72;
        public int WantMaxDurationHours     { get; set; } = 168; // 7 days

        // --- cached accessor ---

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

        // Generate the file with defaults on first boot so admins have something to edit. No-op if it already exists.
        // BRAND-NEW servers get the recommended Balanced profile (caps/cooldowns/fees but still remote-convenient).
        public static void EnsureGenerated()
        {
            if (!System.IO.File.Exists(KmhDataPaths.EconomyConfigFile))
                JsonFileStore.Save(KmhDataPaths.EconomyConfigFile, new EconomyConfig { EconomyMode = "Balanced" });
        }

        // A v1.1.1 Economy.json has no EconomyMode key: the owner never chose a mode, so migrate the legacy server to the
        // recommended Balanced BEFORE backfill locks in Standard. Owner-safe - only fires when the key is absent.
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
                $"price {_current.MarketplaceMinUnitPrice}-{_current.MarketplaceMaxUnitPrice}, " +
                $"max {_current.MarketplaceMaxOpenListingsPerUser} listings/user, " +
                $"lifetime {_current.MarketplaceListingLifetimeHours}h)");
        }

        private void ClampInPlace()
        {
            if (MaxSilverDepositPerTx  < 0) MaxSilverDepositPerTx  = 0;   // 0 = no cap
            if (MaxItemDepositQtyPerTx < 0) MaxItemDepositQtyPerTx = 0;
            PendingDepositTimeoutMinutes = Clamp(PendingDepositTimeoutMinutes, 1, 24 * 60);
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

            // Any Guild Hall rule enabled?
            AnyGuildHallRule = RequireGuildHallToCreateGuild || RequireGuildHallForGuildTreasury
                || RequireGuildHallForGuildContributions || RequireCaravanNearGuildHallForContribution
                || RequireMemberNearGuildHallToJoin || !AllowRemoteGuildInvites
                || string.Equals(GuildTreasuryAccessMode, "GuildHallRequired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GuildTreasuryAccessMode, "CaravanNearGuildHall", StringComparison.OrdinalIgnoreCase);
            MarketplaceTaxPercent             = Clamp(MarketplaceTaxPercent, 0, 50);
            DemandTaxSwingPercent             = Clamp(DemandTaxSwingPercent, 0, 90);
            MarketplaceListingLifetimeHours   = Clamp(MarketplaceListingLifetimeHours, 1, 24 * 365);
            MarketplaceMaxOpenListingsPerUser = Clamp(MarketplaceMaxOpenListingsPerUser, 1, 10_000);
            MarketplaceMinUnitPrice           = Clamp(MarketplaceMinUnitPrice, 1, 1_000_000);
            MarketplaceMaxUnitPrice           = Clamp(MarketplaceMaxUnitPrice, MarketplaceMinUnitPrice, 1_000_000_000);
            MarketplaceMinPercentOfTrustedValue     = ClampD(MarketplaceMinPercentOfTrustedValue, 0.0, 1.0);
            MarketplaceWarnBelowTrustedValuePercent = ClampD(MarketplaceWarnBelowTrustedValuePercent, 0.0, 1.0);
            MarketplaceListingFeePercent            = ClampD(MarketplaceListingFeePercent, 0.0, 0.5);
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

        // Case-insensitively map a config string to its canonical value; unknown -> fallback (logged elsewhere).
        private static string NormalizeMode(string v, string fallback, string[] valid)
        {
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            foreach (string k in valid)
                if (string.Equals(k, v.Trim(), StringComparison.OrdinalIgnoreCase)) return k;
            return fallback;
        }

        // Resolve the mode preset into the effective policy the access checks read. Standard = all permissive (current
        // behavior). Custom = the granular fields verbatim. Presets define a themed policy; owners wanting exact
        // control use Custom.
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

    // The effective, resolved treasury policy the access checks read (derived from EconomyMode/Custom fields).
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
