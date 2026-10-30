using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read-only site registry; build cost, worker and production rules are server-owned.</summary>
    /// <remarks>React to changes via <see cref="IKmhEvents.SiteChanged"/>.</remarks>
    public interface ISitesApi
    {
        /// <summary>Every site on the server.</summary>
        IReadOnlyList<SiteRecord> GetAll();

        /// <summary>The site on a given world tile, or null if none.</summary>
        SiteRecord GetByTile(int tile);

        /// <summary>Sites owned by a username.</summary>
        IReadOnlyList<SiteRecord> GetByOwner(string username);
    }
}
