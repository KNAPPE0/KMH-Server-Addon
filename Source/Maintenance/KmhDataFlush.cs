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
            foreach ((string name, Delegate save) in Savers())
            {
                string msg = null;
                try
                {
                    // A refused write counted as done is the report an owner trusts before pulling the plug.
                    if (save is Func<bool> reports)
                    {
                        if (reports()) ok++;
                        else msg = $"force-save: {name} could not be written - that data is NOT on disk";
                    }
                    else { ((Action)save)(); ok++; }
                }
                catch (Exception ex) { msg = $"force-save: {name} failed: {ex.Message}"; }
                if (msg == null) continue;
                if (onError != null) onError(msg); else ServerLog.Warn(msg);
            }
            return ok;
        }

        // Types actually covered by FlushAll, taken from the delegates themselves so the list can't lie.
        public static HashSet<Type> CoveredTypes()
        {
            HashSet<Type> set = new HashSet<Type>();
            foreach ((string _, Delegate save) in Savers())
                if (save?.Method?.DeclaringType != null) set.Add(save.Method.DeclaringType);
            return set;
        }

        private static IEnumerable<(string, Delegate)> Savers()
        {
            yield return ("LinkedAccounts", (Func<bool>)Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk);
            yield return ("PlayerStats",    (Action)Features.PlayerStats.PlayerStatsStore.SaveToDisk);
            yield return ("Colonists",      (Action)Features.PlayerStats.PlayerStatsStore.SaveColonistsToDisk);
            yield return ("Treasury",       (Func<bool>)Features.Treasury.TreasuryStore.SaveToDisk);
            yield return ("Marketplace",    (Func<bool>)Features.Marketplace.MarketplaceStore.SaveToDisk);
            yield return ("Quests",         (Func<bool>)Features.Quests.QuestStore.SaveToDisk);
            yield return ("Guilds",         (Func<bool>)Features.Guilds.GuildStore.SaveToDisk);
            yield return ("Reputation",     (Action)Features.Reputation.ReputationStore.SaveToDisk);
            yield return ("Sites",          (Func<bool>)Features.Sites.SiteStore.SaveToDisk);
            yield return ("SiteCatalog",    (Action)Features.Sites.SiteCatalogStore.SaveToDisk);
            yield return ("World",          (Action)Features.World.WorldStore.SaveToDisk);
            yield return ("Auctions",       (Func<bool>)Features.Auctions.AuctionStore.SaveToDisk);
            yield return ("WantBoard",      (Func<bool>)Features.WantBoard.WantStore.SaveToDisk);
            yield return ("Notifications",  (Func<bool>)Features.Notifications.NotificationStore.SaveToDisk);
            yield return ("Seasons",        (Action)Features.Seasons.SeasonStore.SaveToDisk);
            yield return ("Mail",           (Func<bool>)Features.Mail.MailStore.SaveToDisk);
            yield return ("ChatModeration", (Action)Features.Chat.ChatModerationStore.SaveToDisk);
            yield return ("Chat",           (Action)Features.Chat.ChatStore.SaveToDisk);
            yield return ("Recovery",       (Func<bool>)Features.Recovery.RecoveryStore.SaveToDisk);
            yield return ("Delivery",       (Func<bool>)Features.Delivery.DeliveryStore.SaveToDisk);
            yield return ("EconomyReset",   (Func<bool>)Features.Economy.EconomyResetStore.SaveToDisk);
            yield return ("Policies",       (Action)Policy.KmhPolicyStore.SaveToDisk);
            yield return ("ItemLabels",     (Action)Features.ItemLabels.ItemLabelCache.SaveToDisk);
            yield return ("DiscordUserState", (Action)Features.Discord.DiscordUserState.SaveToDisk);
            yield return ("Ledger",         (Action)Persistence.TransactionLedger.Flush);
            yield return ("Roadworks",      (Func<bool>)Features.Roadworks.RoadworksStore.SaveToDisk);
            yield return ("Frontier",       (Func<bool>)Features.Frontier.KmhWorldDirector.SaveToDisk);
            yield return ("Transactions",   (Func<bool>)Transactions.KmhTransactionRepository.SaveToDisk);
            yield return ("GuildContributions", (Action)Features.Guilds.Contributions.KmhGuildContributionLedger.SaveToDisk);
        }
    }
}
