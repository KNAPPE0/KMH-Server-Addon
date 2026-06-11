using System.Collections.Generic;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Read the defName → human label catalog populated by patch-mod
    /// clients at handshake. Useful for extensions that render item
    /// names in custom output (Discord embeds, web dashboards, etc.).
    /// </summary>
    public interface IItemLabelsApi
    {
        /// <summary>Human label for a defName, or the defName itself when unknown.</summary>
        string LabelFor(string defName);

        /// <summary>True if the cache has a label entry for the defName.</summary>
        bool HasLabel(string defName);

        /// <summary>Total entries in the cache (debug/diagnostic).</summary>
        int Count { get; }

        /// <summary>
        /// Friendly-name resolution: "plasteel" → "Plasteel" / "power
        /// armor" → "Apparel_PowerArmor". Returns the matching defName
        /// when unambiguous; null + populated <paramref name="candidates"/>
        /// when the query matches multiple items.
        /// </summary>
        string ResolveDefNameByQuery(string query, out List<string> candidates);
    }
}
