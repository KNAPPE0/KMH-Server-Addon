namespace KMHServerAddon.Features.Discord
{
    // Single-emoji prefix per item def, a pure substring heuristic on the defName. Cheap, no DefDatabase access
    // needed (the server doesn't load RimWorld defs), and the result reads as a thematic icon even for items the
    // player doesn't recognize by name.
    //
    // Used to prefix item labels in market browse / showcase / WTB output, so the embed reads as a compact
    // at-a-glance table rather than a wall of similar-looking rows.
    //
    // CDN icon URL support (TryGetIconUrl) is intentionally deferred - it needs config wiring + a hosted asset
    // bucket not worth the v1 surface.
    //
    // Lookup cache: the result for a given defName is immutable, so we memoize after the first scan. A busy
    // !kmh-market render touches the same defNames repeatedly; caching makes later renders O(1) per row.
    internal static class DiscordItemIconMap
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        public static string EmojiFor(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return "📦";
            if (_cache.TryGetValue(defName, out string cached)) return cached;
            string computed = Compute(defName);
            _cache.TryAdd(defName, computed);
            return computed;
        }

        private static string Compute(string defName)
        {
            string lower = defName.ToLowerInvariant();

            // Silver / gold / valuables.
            if (lower.Contains("silver"))                                                 return "🪙";
            if (lower.Contains("gold"))                                                   return "🥇";
            if (lower.Contains("jade"))                                                   return "💚";
            if (lower.Contains("luci"))                                                   return "💊";
            if (lower.Contains("medicine") || lower.Contains("herbal"))                   return "💊";

            // Materials.
            if (lower.Contains("steel") || lower.Contains("plasteel")
             || lower.Contains("uranium"))                                                return "⚙";
            if (lower.Contains("wood")   || lower.Contains("log"))                        return "🪵";
            if (lower.Contains("blocks") || lower.Contains("brick")
             || lower.Contains("concrete") || lower.Contains("chunk"))                    return "🧱";
            if (lower.Contains("component"))                                              return "🔧";

            // Food.
            if (lower.Contains("meat"))                                                   return "🥩";
            if (lower.Contains("meal")   || lower.Contains("food"))                       return "🍱";
            if (lower.Contains("kibble"))                                                 return "🥣";
            if (lower.Contains("rice")   || lower.Contains("corn")
             || lower.Contains("berry")  || lower.Contains("hay")
             || lower.Contains("raw"))                                                    return "🌾";

            // Drugs / consumables.
            if (lower.Contains("smokeleaf"))                                              return "🌿";
            if (lower.Contains("psychoid"))                                               return "🍃";
            if (lower.Contains("alcohol") || lower.Contains("beer")
             || lower.Contains("ambrosia"))                                               return "🍺";
            if (lower.Contains("yayo")    || lower.Contains("flake"))                     return "❄";

            // Textiles + leather.
            if (lower.Contains("cloth")   || lower.Contains("synthread")
             || lower.Contains("hyperweave") || lower.Contains("devilstrand"))            return "🧵";
            if (lower.Contains("leather") || lower.Contains("wool")
             || lower.Contains("fur")     || lower.Contains("skin"))                      return "🦊";

            // Weapons + armour.
            if (lower.Contains("rifle")   || lower.Contains("gun")
             || lower.Contains("pistol")  || lower.Contains("smg")
             || lower.Contains("shotgun"))                                                return "🔫";
            if (lower.Contains("sword")   || lower.Contains("blade")
             || lower.Contains("axe")     || lower.Contains("club")
             || lower.Contains("knife"))                                                  return "🗡";
            if (lower.Contains("bow")     || lower.Contains("arrow"))                     return "🏹";
            if (lower.Contains("armor")   || lower.Contains("vest")
             || lower.Contains("helmet")  || lower.Contains("shield"))                    return "🛡";

            // Tech / spacer.
            if (lower.Contains("spacer")  || lower.Contains("archotech")
             || lower.Contains("ai")      || lower.Contains("techprof"))                  return "🛰";

            // Chemfuel / power.
            if (lower.Contains("chemfuel") || lower.Contains("fuel"))                     return "⛽";

            return "📦";
        }
    }
}
