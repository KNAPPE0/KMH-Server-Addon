namespace KMHServerAddon.Features.Discord
{
    // Single-emoji prefix per item def, a pure substring heuristic on the defName (no DefDatabase - the server
    // doesn't load RimWorld defs). Prefixes item labels in market browse / showcase / WTB so embeds read as a compact
    // table. Result per defName is immutable, so it's memoized after the first scan. (CDN icon URLs are deferred.)
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
