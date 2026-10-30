using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Mods
{
    // RWT's admission reads only these; a mod in neither is refused by absence, as RWT does with any unknown entry.
    internal static class ModRole
    {
        public const string Required = "required";
        public const string Optional = "optional";
    }

    // What a player has to DO about an entry, which is not the same question as what it is.
    internal static class ModKind
    {
        public const string Workshop  = "workshop";
        // A paid expansion the player owns or not, so a server demanding one must say so, not list a missing mod.
        public const string Expansion = "expansion";
        // Core, or a mod installed by hand. Named only; the player has to find it themselves.
        public const string Local     = "local";
    }

    // JSON names are spelled out, not left to the serializer: these fields are the signed wire format, and a name
    // differing on one side makes the two sides sign different bytes.
    internal sealed class ModEntry
    {
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("role")] public string Role = ModRole.Required;
        [JsonProperty("kind")] public string Kind = ModKind.Local;
        // Steam Workshop file id when Kind is workshop; the expansion identifier when it is an expansion.
        [JsonProperty("id")] public string Id = "";
        // RimWorld's packageId, when the load order names one for this mod. Lower-cased by RimWorld itself.
        [JsonProperty("package_id")] public string PackageId = "";
        [JsonProperty("author")]     public string Author = "";

        public bool Subscribable => Kind == ModKind.Workshop && LooksLikeWorkshopId(Id);

        // By the id RimWorld and RWT share; Core is deliberately absent, since every install has it.
        private static readonly HashSet<string> Expansions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Royalty", "Ideology", "Biotech", "Anomaly", "Odyssey",
        };

        internal static bool IsExpansion(string id) => !string.IsNullOrEmpty(id) && Expansions.Contains(id.Trim());

        // RWT puts the expansion name or "Core" in this slot, so all-digits is what marks a subscribable Workshop id.
        internal static bool LooksLikeWorkshopId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string s = id.Trim();
            if (s.Length < 6 || s.Length > 20) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        internal static string KindOf(string id)
            => LooksLikeWorkshopId(id) ? ModKind.Workshop
             : IsExpansion(id)         ? ModKind.Expansion
             :                           ModKind.Local;
    }
}
