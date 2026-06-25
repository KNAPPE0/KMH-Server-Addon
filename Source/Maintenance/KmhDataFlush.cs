using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    // Force-flush all stores for kmh save and process exit; each store is isolated so one failure won't block the rest.
    internal static class KmhDataFlush
    {
        public static int FlushAll(Action<string> onError = null)
        {
            int ok = 0;
            foreach ((string name, Action save) in Savers())
            {
                try { save(); ok++; }
                catch (Exception ex)
                {
                    string msg = $"force-save: {name} failed: {ex.Message}";
                    if (onError != null) onError(msg); else ServerLog.Warn(msg);
                }
            }
            return ok;
        }

        private static IEnumerable<(string, Action)> Savers()
        {
            yield return ("LinkedAccounts", Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk);
            yield return ("PlayerStats",    Features.PlayerStats.PlayerStatsStore.SaveToDisk);
            yield return ("Colonists",      Features.PlayerStats.PlayerStatsStore.SaveColonistsToDisk);
            yield return ("Treasury",       Features.Treasury.TreasuryStore.SaveToDisk);
            yield return ("Marketplace",    Features.Marketplace.MarketplaceStore.SaveToDisk);
            yield return ("Quests",         Features.Quests.QuestStore.SaveToDisk);
            yield return ("Guilds",         Features.Guilds.GuildStore.SaveToDisk);
            yield return ("Reputation",     Features.Reputation.ReputationStore.SaveToDisk);
            yield return ("Sites",          Features.Sites.SiteStore.SaveToDisk);
            yield return ("World",          Features.World.WorldStore.SaveToDisk);
            yield return ("Auctions",       Features.Auctions.AuctionStore.SaveToDisk);
            yield return ("WantBoard",      Features.WantBoard.WantStore.SaveToDisk);
            yield return ("Notifications",  Features.Notifications.NotificationStore.SaveToDisk);
            yield return ("Seasons",        Features.Seasons.SeasonStore.SaveToDisk);
            yield return ("ItemLabels",     Features.ItemLabels.ItemLabelCache.SaveToDisk);
            yield return ("DiscordUserState", Features.Discord.DiscordUserState.SaveToDisk);
            yield return ("Ledger",         Persistence.TransactionLedger.Flush);
        }
    }
}
