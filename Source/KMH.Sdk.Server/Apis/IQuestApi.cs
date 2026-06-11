using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read + mutate the KMH quest board.</summary>
    public interface IQuestApi
    {
        /// <summary>
        /// All quests visible to the given caller (own posts always
        /// visible; guild-only quests filtered against caller's guild).
        /// </summary>
        IReadOnlyList<QuestRecord> GetVisibleQuests(string callerUsername);

        /// <summary>Post a DeliverItem quest. Returns the new quest id or 0 on failure.</summary>
        long PostDeliverItem(string posterUsername, string title, string description,
                             int bountySilver, string targetDefName, int targetQty,
                             string visibility = "public", int expiresHours = 0);

        /// <summary>Post a Bounty (manual-signoff) quest. Returns the new quest id or 0 on failure.</summary>
        long PostBounty(string posterUsername, string title, string description,
                        int bountySilver, string visibility = "public", int expiresHours = 0);

        /// <summary>Claim a quest. Returns false if already claimed or non-open.</summary>
        bool Claim(string claimerUsername, long questId);

        /// <summary>
        /// Submit a claimed quest. For DeliverItem, server checks the
        /// claimer's treasury for the target item + qty and auto-
        /// completes on match. For Bounty, transitions to Submitted
        /// awaiting the poster's <see cref="Approve"/>.
        /// </summary>
        bool Submit(string claimerUsername, long questId);

        /// <summary>Bounty-only: poster signs off on a Submitted quest, paying out the bounty.</summary>
        bool Approve(string posterUsername, long questId);

        /// <summary>Cancel a quest (must be poster). Refunds bounty silver to poster's treasury.</summary>
        bool Cancel(string posterUsername, long questId);
    }
}
