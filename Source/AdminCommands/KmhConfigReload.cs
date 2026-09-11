using System;
using System.Collections.Generic;

namespace KMHServerAddon.AdminCommands
{
    // A self-test walks the assembly for config classes and fails if one is missing here, so a new config must land in this list.
    internal static class KmhConfigReload
    {
        internal sealed class Entry
        {
            public string Area;
            public Type   ConfigType;      // what the coverage test matches against
            public Action Reload;          // null = not reloadable
            public string ExemptReason;
            public string Note;
        }

        public static IEnumerable<Entry> All()
        {
            yield return new Entry
            {
                Area = "economy", ConfigType = typeof(Features.Economy.EconomyConfig),
                // Site pricing reads economy values, so reloading one without the other leaves them disagreeing.
                Reload = () => { Features.Economy.EconomyConfig.Reload(); Features.Sites.SitesConfig.Reload(); },
                Note = "site pricing reloaded with it",
            };
            yield return new Entry { Area = "sites",       ConfigType = typeof(Features.Sites.SitesConfig),
                                     Reload = Features.Sites.SitesConfig.Reload };
            yield return new Entry { Area = "world",       ConfigType = typeof(Features.World.WorldConfig),
                                     Reload = Features.World.WorldConfig.Reload };
            yield return new Entry { Area = "frontier",    ConfigType = typeof(Features.Frontier.FrontierConfig),
                                     Reload = Features.Frontier.FrontierConfig.Reload };
            yield return new Entry { Area = "maintenance", ConfigType = typeof(Maintenance.MaintenanceConfig),
                                     Reload = Maintenance.MaintenanceConfig.Reload };
            yield return new Entry { Area = "quests",      ConfigType = typeof(Features.Quests.QuestsConfig),
                                     Reload = Features.Quests.QuestsConfig.Reload };
            yield return new Entry { Area = "reputation",  ConfigType = typeof(Features.Reputation.ReputationConfig),
                                     Reload = Features.Reputation.ReputationConfig.Reload };
            yield return new Entry
            {
                Area = "chat", ConfigType = typeof(Features.Chat.ChatConfig),
                // Colours and the marker ride the hello, so without a re-send nothing changes until every player reconnects.
                Reload = () => Features.Comms.CommsStartup.ApplyChat(push: true),
                Note = "colours and the Discord marker pushed to connected clients",
            };
            yield return new Entry { Area = "media",       ConfigType = typeof(Features.Media.MediaConfig),
                                     Reload = () => Features.Comms.CommsStartup.ApplyMedia(push: true),
                                     Note = "resolver limits pushed to connected clients; the cache keeps what it holds" };
            yield return new Entry { Area = "mail",        ConfigType = typeof(Features.Mail.MailConfig),
                                     Reload = Features.Mail.MailConfig.Reload };
            yield return new Entry
            {
                Area = "staff", ConfigType = typeof(Features.Identity.StaffConfig),
                // Labels ride the hello but WHO holds a role rides the player-stats snapshot, so both have to go out.
                Reload = () => Features.Comms.CommsStartup.ApplyStaff(push: true),
                Note = "badges pushed to connected clients",
            };
            yield return new Entry { Area = "enforcement", ConfigType = typeof(Features.Enforcement.EnforcementConfig),
                                     Reload = Features.Enforcement.EnforcementConfig.Reload };

            // Features and Discord do more than drop a cache, so they keep their own handlers in KmhServerCommands.
            yield return new Entry { Area = "features", ConfigType = typeof(Features.FeaturesConfig),        Reload = null,
                                     ExemptReason = "handled separately - it also re-sends the hello to every client" };
            yield return new Entry { Area = "discord",  ConfigType = typeof(Features.Discord.DiscordConfig), Reload = null,
                                     ExemptReason = "handled separately - it restarts the bot rather than re-reading a file" };

            yield return new Entry
            {
                Area = "transport", ConfigType = typeof(Features.Transport.TransportConfig), Reload = null,
                ExemptReason = "the API listener is already bound to its port - a new port needs a restart",
            };
        }

        public static List<string> ReloadableAreas()
        {
            var names = new List<string>();
            foreach (Entry e in All()) if (e.Reload != null) names.Add(e.Area);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public static Entry Find(string area)
        {
            if (string.IsNullOrWhiteSpace(area)) return null;
            string a = area.Trim().ToLowerInvariant();
            foreach (Entry e in All()) if (e.Area == a) return e;
            return null;
        }

        public static bool Run(string area, Action<string> reply, out string why)
        {
            why = "";
            Entry e = Find(area);
            if (e == null) { why = $"No config area '{area}'."; return false; }
            if (e.Reload == null) { why = $"'{e.Area}' cannot be reloaded - {e.ExemptReason}."; return false; }

            e.Reload();
            reply($"{e.Area}: reloaded{(string.IsNullOrEmpty(e.Note) ? "" : " (" + e.Note + ")")}.");
            return true;
        }
    }
}
