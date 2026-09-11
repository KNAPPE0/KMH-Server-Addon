using System.Collections.Generic;
using KMHServerAddon.Features.Sites.Dto;

namespace KMHServerAddon.Features.Sites
{
    // The budget is small so building kinds compete, and a raised cap is far harder to claw back than to grant.
    internal static class SiteBuildings
    {
        // Site Core is implicit and never occupies one of these.
        public static int SlotsForTier(int tier)
        {
            if (tier <= 1) return 2;
            if (tier == 2) return 3;
            return 4;                 // T3; KMH blocks tiers above 3 server-wide (MaxAllowedSiteOutputTier)
        }

        // One per site: stacked production buildings are the infinite resource printer this exists to prevent.
        public const int MaxProduction = 1;

        // Only kinds that currently do something, or the picker sells buttons that spend silver for no effect.
        public static readonly string[] Buildable =
            { SiteBuilding.KindProduction, SiteBuilding.KindHousing, SiteBuilding.KindStorage };

        public static bool IsBuildable(string kind)
        {
            foreach (string k in Buildable)
                if (string.Equals(k, kind, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static int CountOf(SiteEntry s, string kind)
        {
            int n = 0;
            if (s?.Buildings == null) return 0;
            foreach (SiteBuilding b in s.Buildings)
                if (b != null && string.Equals(b.Kind, kind, System.StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        public static int UsedSlots(SiteEntry s) => s?.Buildings?.Count ?? 0;

        public static int FreeSlots(SiteEntry s, int tier) => SlotsForTier(tier) - UsedSlots(s);

        // Pure admission rule, so "why can't I build this" has one answer everywhere it is asked.
        public static bool CanAdd(SiteEntry s, string kind, int tier, out string reason)
        {
            reason = null;
            if (s == null) { reason = "No such site."; return false; }
            if (string.IsNullOrWhiteSpace(kind)) { reason = "No building kind."; return false; }
            if (!IsBuildable(kind)) { reason = "That building can't be built yet."; return false; }
            if (FreeSlots(s, tier) <= 0)
            { reason = $"No free building slots (tier {tier} allows {SlotsForTier(tier)})."; return false; }
            if (string.Equals(kind, SiteBuilding.KindProduction, System.StringComparison.OrdinalIgnoreCase)
                && CountOf(s, SiteBuilding.KindProduction) >= MaxProduction)
            { reason = "A site can only have one production building."; return false; }
            return true;
        }

        // Additive and bounded on purpose - another multiplier on top of tier x workers x skill would compound.
        public const double ProductionBonusPerLevel = 0.10;   // +10% per level of the single production building
        public const double MaxBuildingBonus        = 0.30;   // hard ceiling regardless of level

        public static double ProductionBonus(SiteEntry s)
        {
            if (s?.Buildings == null) return 0;
            double bonus = 0;
            foreach (SiteBuilding b in s.Buildings)
            {
                if (b == null) continue;
                if (!string.Equals(b.Kind, SiteBuilding.KindProduction, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(b.State, SiteBuilding.StateOperational, System.StringComparison.OrdinalIgnoreCase)) continue;
                bonus += ProductionBonusPerLevel * (b.Level < 1 ? 1 : b.Level);
            }
            return bonus > MaxBuildingBonus ? MaxBuildingBonus : bonus;
        }

        // Bounded because held goods are off-map wealth, and an unbounded warehouse is an unbounded raid shelter.
        public const int UnitsPerStorage = 100;
        public const int MaxStorageUnits = 300;

        public static int StorageCapacity(SiteEntry s)
        {
            int n = 0;
            if (s?.Buildings == null) return 0;
            foreach (SiteBuilding b in s.Buildings)
                if (b != null
                    && string.Equals(b.Kind, SiteBuilding.KindStorage, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.State, SiteBuilding.StateOperational, System.StringComparison.OrdinalIgnoreCase))
                    n += UnitsPerStorage;
            return n > MaxStorageUnits ? MaxStorageUnits : n;
        }

        public static int StoredUnits(SiteEntry s)
        {
            int n = 0;
            if (s?.StoredItems == null) return 0;
            foreach (KeyValuePair<string, int> kv in s.StoredItems) n += kv.Value > 0 ? kv.Value : 0;
            return n;
        }

        public static int FreeStorage(SiteEntry s)
        {
            int free = StorageCapacity(s) - StoredUnits(s);
            return free < 0 ? 0 : free;
        }

        // Housing raises usable worker capacity ON TOP of the guild-perk-derived cap, still bounded.
        public const int WorkersPerHousing = 2;
        public const int MaxHousingBonus   = 4;

        public static int HousingBonus(SiteEntry s)
        {
            int n = 0;
            if (s?.Buildings == null) return 0;
            foreach (SiteBuilding b in s.Buildings)
                if (b != null
                    && string.Equals(b.Kind, SiteBuilding.KindHousing, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.State, SiteBuilding.StateOperational, System.StringComparison.OrdinalIgnoreCase))
                    n += WorkersPerHousing;
            return n > MaxHousingBonus ? MaxHousingBonus : n;
        }
    }
}
