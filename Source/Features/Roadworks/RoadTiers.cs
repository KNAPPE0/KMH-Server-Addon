using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Roadworks.Dto;

namespace KMHServerAddon.Features.Roadworks
{
    // KMH tier strings only - the Patch maps each to a RimWorld RoadDef in one adapter.
    internal static class RoadTiers
    {
        public const string Trail   = "trail";
        public const string Road    = "road";
        public const string Highway = "highway";

        public static readonly string[] All = { Trail, Road, Highway };

        public static string Normalize(string tier)
        {
            if (string.IsNullOrWhiteSpace(tier)) return Trail;
            string t = tier.Trim().ToLowerInvariant();
            foreach (string k in All) if (t == k) return k;
            return Trail;
        }

        public static string DisplayName(string tier)
        {
            switch (Normalize(tier))
            {
                case Road:    return "Road";
                case Highway: return "Highway";
                default:      return "Trail";
            }
        }

        // Position on the ladder, 1-based.
        public static int RankOf(string tier)
        {
            switch (Normalize(tier))
            {
                case Highway: return 3;
                case Road:    return 2;
                default:      return 1;
            }
        }

        // Mirrored byte-for-byte on the Patch: the client greys out what this refuses, the server enforces it.
        public static bool TierRankAllowed(int tierRank, int siteTier)
        {
            int max = siteTier >= 3 ? 3 : (siteTier <= 1 ? 1 : 2);
            return tierRank >= 1 && tierRank <= max;
        }

        public static bool AllowedAtSiteTier(string tier, int siteTier) => TierRankAllowed(RankOf(tier), siteTier);

        public static int SilverPerSegment(string tier)
        {
            Economy.EconomyConfig cfg = Economy.EconomyConfig.Current;
            switch (Normalize(tier))
            {
                case Road:    return cfg.RoadworksSilverPerSegmentRoad;
                case Highway: return cfg.RoadworksSilverPerSegmentHighway;
                default:      return cfg.RoadworksSilverPerSegmentTrail;
            }
        }

        public static double WorkPerSegment(string tier)
        {
            switch (Normalize(tier))
            {
                case Road:    return 2.0;
                case Highway: return 4.0;
                default:      return 1.0;
            }
        }
    }

    // Order-independent and layer-aware, so one segment has exactly one key.
    internal static class RoadKeys
    {
        // Mirrored byte-for-byte on the Patch, or KMH stops recognising its own roads.
        public static string For(int layerA, int tileA, int layerB, int tileB)
        {
            string ka = layerA + ":" + tileA, kb = layerB + ":" + tileB;
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        public static string For(RoadTile a, RoadTile b)
            => a == null || b == null ? null : For(a.LayerId, a.TileId, b.LayerId, b.TileId);

        public static string For(RoadSegment s) => s == null ? null : For(s.A, s.B);

        // Mirrored byte-for-byte on the Patch: the client quotes with this, the server charges with it.
        public static List<string> NewSegmentKeys(List<string> routeKeys, HashSet<string> existing)
        {
            var outp = new List<string>();
            if (routeKeys == null) return outp;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string k in routeKeys)
            {
                if (string.IsNullOrEmpty(k)) continue;
                if (!seen.Add(k)) continue;
                if (existing != null && existing.Contains(k)) continue;
                outp.Add(k);
            }
            return outp;
        }

        public static bool IsWellFormed(RoadTile a, RoadTile b)
            => a != null && b != null && a.IsValid && b.IsValid
               && !(a.LayerId == b.LayerId && a.TileId == b.TileId);
    }
}
