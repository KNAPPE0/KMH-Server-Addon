using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Read player quest-reputation. Scores are server-owned and only move on
    /// quest activity (completed / abandoned / proof-rejected), so this surface
    /// is read-only - subscribe to <see cref="IKmhEvents.ReputationChanged"/> to
    /// react when one shifts.
    /// </summary>
    public interface IReputationApi
    {
        /// <summary>Score for a username (0 if unseen). Higher is better.</summary>
        int ScoreOf(string username);

        /// <summary>Tier for a username: "Trusted", "Neutral", or "Unreliable".</summary>
        string TierOf(string username);

        /// <summary>Every tracked player's reputation.</summary>
        IReadOnlyList<ReputationRecord> GetAll();
    }
}
