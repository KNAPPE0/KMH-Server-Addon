using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read and bump the KMH player roster and its cumulative per-server totals.</summary>
    public interface IPlayerStatsApi
    {
        /// <summary>Every player seen on this server, with totals that accumulate until a destructive season reset.</summary>
        IReadOnlyList<PlayerStatRecord> GetAll();

        /// <summary>Ensure a player record exists (idempotent - safe to call on every login).</summary>
        void EnsurePlayer(string username);

        /// <summary>Bump SilverDonated by delta (can be negative).</summary>
        void AddSilverDonated(string username, long delta);

        /// <summary>Bump SalesEarned by delta + increment MarketplaceSales by 1.</summary>
        void AddSalesEarned(string username, long delta);

        /// <summary>Bump QuestsCompleted by 1.</summary>
        void BumpQuestsCompleted(string username);

        /// <summary>Bump QuestsPosted by 1.</summary>
        void BumpQuestsPosted(string username);
    }
}
