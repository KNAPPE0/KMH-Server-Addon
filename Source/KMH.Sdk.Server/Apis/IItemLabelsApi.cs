using System.Collections.Generic;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>The defName → label catalog, contributed by clients at handshake; empty until one has connected.</summary>
    public interface IItemLabelsApi
    {
        /// <summary>Human label for a defName, or the defName itself when unknown.</summary>
        string LabelFor(string defName);

        /// <summary>True if the cache has a label entry for the defName.</summary>
        bool HasLabel(string defName);

        /// <summary>Total entries in the cache.</summary>
        int Count { get; }

        /// <summary>Resolves a friendly name such as "power armor" to a defName.</summary>
        /// <returns>Null, with <paramref name="candidates"/> filled, when the query is ambiguous.</returns>
        string ResolveDefNameByQuery(string query, out List<string> candidates);
    }
}
