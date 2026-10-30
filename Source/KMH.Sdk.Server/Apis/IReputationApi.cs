using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read-only player quest reputation; scores are server-owned and move only on quest activity.</summary>
    /// <remarks>Subscribe to <see cref="IKmhEvents.ReputationChanged"/> to react when one shifts.</remarks>
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
