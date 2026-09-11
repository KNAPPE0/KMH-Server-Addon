using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Sites
{
    // Every tier carries the same field set, so an owner reading the config sees one shape rather than four.
    public sealed class SiteOutputTier
    {
        public bool     Enabled              { get; set; } = true;
        public string   Name                 { get; set; } = "";
        public int      MaxAmount            { get; set; } = 50;   // hard cap on amount/cycle for this tier
        public double   CostMultiplier       { get; set; } = 1.0;  // build cost * this
        public double   CycleMultiplier      { get; set; } = 1.0;  // base cycle minutes * this
        public double   MaxSpeedMultiplier   { get; set; } = 3.0;  // ceiling on worker speed-up (cycle time)
        public double   MaxOutputMultiplier  { get; set; } = 2.0;  // ceiling on worker output multiply (amount)
        public bool     AllowAutoClassifiedItems { get; set; } = true; // accept items auto-sorted into this tier
        public string[] AllowedDefNames      { get; set; } = Array.Empty<string>();
        public string[] BlockedDefNames      { get; set; } = Array.Empty<string>();
        public string[] AllowedKeywords      { get; set; } = Array.Empty<string>();
        public string[] BlockedKeywords      { get; set; } = Array.Empty<string>();

        public static SiteOutputTier[] Defaults() => new[]
        {
            new SiteOutputTier
            {
                Name = "Basic", MaxAmount = 50, CostMultiplier = 1.0, CycleMultiplier = 1.0,
                MaxSpeedMultiplier = 2.0, MaxOutputMultiplier = 1.5, AllowAutoClassifiedItems = true,
            },
            new SiteOutputTier
            {
                Name = "Refined", MaxAmount = 25, CostMultiplier = 2.0, CycleMultiplier = 1.5,
                MaxSpeedMultiplier = 1.75, MaxOutputMultiplier = 1.25, AllowAutoClassifiedItems = true,
            },
            new SiteOutputTier
            {
                Name = "Advanced", MaxAmount = 10, CostMultiplier = 5.0, CycleMultiplier = 3.0,
                MaxSpeedMultiplier = 1.5, MaxOutputMultiplier = 1.0, AllowAutoClassifiedItems = true,
            },
            new SiteOutputTier
            {
                // Crafted / rare / tech. Blocked by default: never a printer for gear, tech, drugs, genes, relics.
                Name = "Crafted/Rare/Tech", Enabled = false, MaxAmount = 1, CostMultiplier = 25.0, CycleMultiplier = 12.0,
                MaxSpeedMultiplier = 1.25, MaxOutputMultiplier = 1.0, AllowAutoClassifiedItems = false,
            },
        };
    }

    // Result of classifying a site output. Server is the final authority; the client only previews.
    public struct SiteOutputClass
    {
        public bool   IsAllowed;
        public int    TierNumber;          // 1..4
        public string TierName;
        public string RelevantSkill;
        public int    MaxAmount;
        public double CostMultiplier;
        public double CycleMultiplier;
        public double MaxSpeedMultiplier;
        public double MaxOutputMultiplier;
        public string BlockReason;
        public bool   IsUnknownOrSuspicious;
        public bool   IsExplicitlyAllowed;
        public bool   IsExplicitlyBlocked;
    }

    // Never trusts a client-supplied market value, and an unknown item lands on the configured unknown tier.
    internal static class SiteOutputRules
    {
        // Resolution order: explicit block, then explicit allow, then the highest matching auto-tier, then unknown.
        private static readonly string[] Tier1Keywords =
        {
            "wood", "hay", "raw", "rice", "corn", "potato", "berry", "berries", "haygrass", "meat", "milk", "egg_",
        };
        private static readonly string[] Tier2Keywords =
        {
            "steel", "cloth", "chemfuel", "leather", "wool", "fur", "block", "brick", "medicineherbal", "kibble",
            "pemmican", "meal", "textile", "cotton", "neutroamine", "chocolate", "insectjelly", "babyfood",
        };
        private static readonly string[] Tier3Keywords =
        {
            // "silver" is deliberately absent - a Site must not print money.
            "plasteel", "uranium", "gold", "jade", "componentindustrial", "medicineindustrial", "smokeleaf", "psychoid",
            "devilstrand", "synthread", "hyperweave",
        };
        // Tier 4 / always-suspicious markers - these force the crafted/rare/tech tier regardless of anything else.
        private static readonly string[] BlockedMarkers =
        {
            "techprint", "techprof", "subpersona", "persona", "aicore", "aipersona", "archotech", "bionic", "implant",
            "gun_", "bow_", "weapon", "meleeweapon", "apparel_", "armor", "power_", "ammo", "shell_", "relic", "artifact",
            "book", "textbook", "schematic", "gene", "xenogerm", "genepack", "endogene", "generemover", "luciferium",
            "minified", "wornbycorpse", "corpse_", "megasloth", "thrumbo", "eltex", "psytrainer", "neurotrainer",
            // real-catalog coverage: spacer/ultratech + catastrophic/rare modded markers
            "componentspacer", "reinforcedbarrel", "glitterworld", "ultratech", "medicineultratech", "warhead",
            "missile", "nuclear", "plutonium", "fuelrod", "reactor", "powercore", "permit", "nameplate",
            "orbital", "tornado", "targeter", "shield", "flake", "yayo", "gojuice", "wakeup", "joint",
        };

        // Keywords stay fuzzy but never bypass the complex-item pre-filter; value is not consulted, because at this point the caller's figure is still the client's.
        public static SiteOutputClass Classify(string defName, string label)
        {
            SitesConfig cfg = SitesConfig.Current;
            SiteOutputTier[] tiers = Tiers(cfg);   // resolved once: a malformed config hands back a fresh array per call
            string def = (defName ?? "").Trim();
            string lbl = (label ?? "").Trim();
            string kw  = (def + " " + lbl).ToLowerInvariant();   // fuzzy keyword haystack

            SiteOutputClass r = new SiteOutputClass
            {
                RelevantSkill = SiteStore.DetermineRelevantSkill(def),
                TierNumber = ClampTier(cfg.DefaultUnknownOutputTier),
            };

            // 1) explicit BLOCK always wins (exact defName OR exact label, or a block keyword).
            foreach (SiteOutputTier t in tiers)
                if (MatchesNameOrLabel(def, lbl, t.BlockedDefNames) || MatchesKeyword(kw, t.BlockedKeywords))
                {
                    r.TierNumber = 4; r.IsExplicitlyBlocked = true;
                    return Finalize(r, cfg, allowedFlagFromTier: false, blockReason: "blocked by server rules");
                }

            // An exact allow may override the complex-item pre-filter; a keyword allow must not.
            int tierIdx = -1; bool exactAllow = false, keywordAllow = false;
            for (int i = 0; i < tiers.Length; i++)
                if (MatchesNameOrLabel(def, lbl, tiers[i].AllowedDefNames)) { tierIdx = i; exactAllow = true; break; }
            if (tierIdx < 0)
                for (int i = 0; i < tiers.Length; i++)
                    if (MatchesKeyword(kw, tiers[i].AllowedKeywords)) { tierIdx = i; keywordAllow = true; break; }
            bool explicitAllow = exactAllow || keywordAllow;

            // Overridable only when the owner opted in AND the target tier is enabled and within the server max.
            if (!Items.KmhItemSafety.IsAllowedSiteOutput(def, out string hardReason))
            {
                int idx = tierIdx >= 0 ? tierIdx : ClampTier(cfg.DefaultUnknownOutputTier) - 1;
                SiteOutputTier tt = tiers[idx];
                bool canOverride = exactAllow && cfg.AllowExplicitComplexSiteOutputs && tt.Enabled
                                   && (idx + 1) <= ClampTier(cfg.MaxAllowedSiteOutputTier);
                if (!canOverride)
                {
                    r.TierNumber = 4; r.IsUnknownOrSuspicious = true;
                    return Finalize(r, cfg, allowedFlagFromTier: false, blockReason: hardReason);
                }
                Diagnostics.ServerLog.Warn($"Sites: COMPLEX output '{def}' ({lbl}) allowed by exact allowlist + AllowExplicitComplexSiteOutputs (tier {idx + 1}) - owner opted in.");
                r.TierNumber = idx + 1; r.IsExplicitlyAllowed = true;
                return Finalize(r, cfg, allowedFlagFromTier: true, blockReason: null);
            }

            // 4) suspicious markers -> tier 4 unless an explicit allow already pinned it.
            if (tierIdx < 0 && MatchesKeyword(kw, BlockedMarkers))
            {
                r.TierNumber = 4; r.IsUnknownOrSuspicious = true;
                return Finalize(r, cfg, allowedFlagFromTier: false, blockReason: "rare/tech/crafted item - not a basic resource");
            }

            // 5) auto-classify by keyword (highest tier match wins so plasteel != steel).
            if (tierIdx < 0)
            {
                if      (MatchesKeyword(kw, Tier3Keywords)) tierIdx = 2;
                else if (MatchesKeyword(kw, Tier2Keywords)) tierIdx = 1;
                else if (MatchesKeyword(kw, Tier1Keywords)) tierIdx = 0;
            }

            // 6) unknown -> default unknown tier (blocked by default).
            bool unknown = tierIdx < 0;
            if (unknown) tierIdx = ClampTier(cfg.DefaultUnknownOutputTier) - 1;

            r.TierNumber = tierIdx + 1;
            r.IsExplicitlyAllowed = explicitAllow;
            r.IsUnknownOrSuspicious = unknown;

            SiteOutputTier tier = tiers[tierIdx];
            bool autoAllowed = explicitAllow || (tier.AllowAutoClassifiedItems && !unknown);
            string reason = (unknown && !explicitAllow) ? "unknown item - not on an allowed tier" : null;
            return Finalize(r, cfg, autoAllowed, reason);
        }

        private static SiteOutputClass Finalize(SiteOutputClass r, SitesConfig cfg, bool allowedFlagFromTier, string blockReason)
        {
            int idx = ClampTier(r.TierNumber) - 1;
            SiteOutputTier tier = Tiers(cfg)[idx];
            r.TierNumber          = idx + 1;
            r.TierName            = string.IsNullOrEmpty(tier.Name) ? $"Tier {idx + 1}" : tier.Name;
            r.MaxAmount           = Math.Max(1, tier.MaxAmount);
            r.CostMultiplier      = tier.CostMultiplier <= 0 ? 1.0 : tier.CostMultiplier;
            r.CycleMultiplier     = tier.CycleMultiplier <= 0 ? 1.0 : tier.CycleMultiplier;
            r.MaxSpeedMultiplier  = Math.Max(1.0, tier.MaxSpeedMultiplier);
            // Tier 4 never multiplies output, whatever the config says.
            r.MaxOutputMultiplier = r.TierNumber >= 4 ? 1.0 : Math.Max(1.0, tier.MaxOutputMultiplier);

            bool tierEnabled  = tier.Enabled;
            bool underMaxTier = r.TierNumber <= ClampTier(cfg.MaxAllowedSiteOutputTier);
            r.IsAllowed = allowedFlagFromTier && tierEnabled && underMaxTier && string.IsNullOrEmpty(blockReason);

            if (!r.IsAllowed && string.IsNullOrEmpty(blockReason))
            {
                if (!tierEnabled)       blockReason = $"{r.TierName} outputs are disabled on this server.";
                else if (!underMaxTier) blockReason = $"{r.TierName} exceeds this server's max site-output tier.";
                else                    blockReason = "That item can't be produced by a site here.";
            }
            r.BlockReason = r.IsAllowed ? "" : blockReason;
            return r;
        }

        private static SiteOutputTier[] Tiers(SitesConfig cfg)
        {
            SiteOutputTier[] t = cfg.OutputTiers;
            if (t == null || t.Length < 4) return SiteOutputTier.Defaults();  // defensive: always 4 tiers
            return t;
        }

        private static int ClampTier(int t) => t < 1 ? 1 : (t > 4 ? 4 : t);

        // Exact only, never a partial, so an owner's entry cannot widen itself into neighbouring defNames.
        private static bool MatchesNameOrLabel(string def, string label, string[] entries)
        {
            if (entries == null) return false;
            foreach (string e in entries)
            {
                if (string.IsNullOrEmpty(e)) continue;
                if (string.Equals(e, def, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(label) && string.Equals(e, label, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool MatchesKeyword(string lower, string[] keywords)
        {
            if (keywords == null) return false;
            foreach (string k in keywords)
                if (!string.IsNullOrEmpty(k) && lower.Contains(k.ToLowerInvariant())) return true;
            return false;
        }
    }
}
