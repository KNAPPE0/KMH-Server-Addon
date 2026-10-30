using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>World Engine API: server-wide events and house-pool-funded global quests, within World.json limits.</summary>
    public interface IWorldApi
    {
        /// <summary>Active live events.</summary>
        IReadOnlyList<WorldEventRecord> GetActiveEvents();

        /// <summary>Active and recently-ended global quests still inside the board grace window.</summary>
        IReadOnlyList<ServerQuestRecord> GetServerQuests();

        /// <summary>Fires a world event within config clamps; a <paramref name="durationMinutes"/> of 0 uses the default.</summary>
        /// <returns>False, with a reason, if disabled or invalid.</returns>
        bool FireEvent(string type, double magnitude, string target, int durationMinutes, out string reason);

        /// <summary>Force-ends active events of this type; returns false if none are running.</summary>
        bool EndEvent(string type, out string reason);

        /// <summary>Creates a cooperative or competitive global quest, reserving its house-pool reward; false if invalid.</summary>
        bool CreateQuest(string kind, string objective, string targetDefName, int goalQty, long reward,
                         int durationMinutes, string title, string description, out string reason);

        /// <summary>Cancels an active global quest and refunds its reserved reward to the house pool.</summary>
        bool EndQuest(long questId, out string reason);

        /// <summary>True while a tax-holiday event is live, waiving marketplace and auction house tax.</summary>
        bool IsTaxHoliday();

        /// <summary>Current seller payout multiplier from economy events; 1.0 when none apply.</summary>
        double MarketPayoutMultiplierFor(string itemDefName);
    }
}
