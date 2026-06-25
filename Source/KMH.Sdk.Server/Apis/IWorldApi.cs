using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    // World Engine API for server-wide events and house-pool-funded global quests, respecting World.json limits and funding rules.
    public interface IWorldApi
    {
        // Active live events.
        IReadOnlyList<WorldEventRecord> GetActiveEvents();

        // Active and recently-ended global quests still inside the board grace window.
        IReadOnlyList<ServerQuestRecord> GetServerQuests();

        // Fires a world event using config defaults/clamps; returns false with a reason if disabled or invalid.
        // durationMinutes: 0 = use the configured default. (Bare minutes; an admin command also accepts 2h / 1d.)
        bool FireEvent(string type, double magnitude, string target, int durationMinutes, out string reason);

        // Force-ends active events of this type; returns false if none are running.
        bool EndEvent(string type, out string reason);

        // Creates a cooperative/competitive global quest, reserves house-pool reward, and returns false with reason on invalid input.
        bool CreateQuest(string kind, string objective, string targetDefName, int goalQty, long reward,
                         int durationMinutes, string title, string description, out string reason);

        // Cancels an active global quest and refunds its reserved reward to the house pool.
        bool EndQuest(long questId, out string reason);

        // True while a tax-holiday event is live, waiving marketplace and auction house tax.
        bool IsTaxHoliday();

        // Current seller payout multiplier from economy events; 1.0 when none apply.
        double MarketPayoutMultiplierFor(string itemDefName);
    }
}
