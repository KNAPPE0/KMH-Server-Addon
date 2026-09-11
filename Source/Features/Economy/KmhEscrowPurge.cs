namespace KMHServerAddon.Features.Economy
{
    // Declared once for all three reset paths, so a new escrow-holding feature cannot be added to only some of them.
    internal static class KmhEscrowPurge
    {
        public static bool HasAny(string user)
            => HasQuestEscrow(user) || HasMailEscrow(user) || HasRoadworksEscrow(user);

        public static bool HasQuestEscrow(string user) => Quests.QuestStore.HasPosterEscrow(user);
        public static bool HasMailEscrow (string user) => Mail.MailStore.HasSenderEscrow(user);
        // Keyed on the project rather than its balance, because one at zero remaining still exists.
        public static bool HasRoadworksEscrow(string user) => Roadworks.RoadworksStore.HasProjects(user);

        public static (int Quests, int Mail, int Roadworks, long RoadworksSilver) BurnAll(string user)
        {
            int q = Quests.QuestStore.PurgePoster(user);
            int m = Mail.MailStore.PurgeSender(user);
            (int roads, long silver) = Roadworks.RoadworksStore.PurgeOwner(user, dryRun: false);
            return (q, m, roads, silver);
        }
    }
}
