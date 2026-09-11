using System.Collections.Generic;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// The defName → human label catalog, contributed by patch-mod clients at handshake, so a server with none
    /// connected yet knows no labels.
    /// </summary>
    public interface IItemLabelsApi
    {
        /// <summary>Human label for a defName, or the defName itself when unknown.</summary>
        string LabelFor(string defName);

        /// <summary>True if the cache has a label entry for the defName.</summary>
        bool HasLabel(string defName);

        /// <summary>Total entries in the cache.</summary>
        int Count { get; }

        /// <summary>
        /// Resolve a friendly name such as "power armor" to a defName, or return null and fill
        /// <paramref name="candidates"/> when the query is ambiguous.
        /// </summary>
        string ResolveDefNameByQuery(string query, out List<string> candidates);
    }
}
