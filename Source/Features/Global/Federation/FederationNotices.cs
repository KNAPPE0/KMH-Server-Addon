using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Features.Guilds;

namespace KMHServerAddon.Features.Global.Federation
{
    // In memory only: after a restart the stored link is the baseline, so no change is announced twice.
    internal static class FederationNotices
    {
        internal sealed class Mark
        {
            public string State = "", FederationState = "";
            public bool   IsLeader;
            public long   Revision;
            public readonly HashSet<string> Completed = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Rewarded  = new HashSet<string>(StringComparer.Ordinal);
            public string FederationId = "", Name = "", Tag = "", Color = "", Description = "";
            public int    Level;
            public readonly HashSet<string> Achievements = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> Members = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal sealed class Notice
        {
            public string Title = "", Body = "", Tone = "neutral";
            public bool   LeadersOnly;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Mark> _told = new Dictionary<string, Mark>(StringComparer.Ordinal);

        internal static Mark MarkOf(FederatedGuildLink link)
        {
            var m = new Mark();
            if (link == null) return m;
            m.State = link.State ?? "";
            m.FederationState = link.View?.Federation?.State ?? "";
            m.IsLeader = link.IsLeader;
            m.Revision = link.View?.Federation?.Revision ?? 0;
            if (link.View?.Objectives != null)
                foreach (FederationObjective o in link.View.Objectives)
                    if (o != null && o.State == "Completed" && !string.IsNullOrEmpty(o.Id)) m.Completed.Add(o.Id);
            if (link.RewardedObjectives != null) foreach (string id in link.RewardedObjectives) if (!string.IsNullOrEmpty(id)) m.Rewarded.Add(id);
            FederationProfile f = link.View?.Federation;
            if (f == null || string.IsNullOrEmpty(f.Id)) return m;
            m.FederationId = f.Id;
            m.Name = f.Name ?? ""; m.Tag = f.Tag ?? ""; m.Color = f.Color ?? ""; m.Description = f.Description ?? "";
            m.Level = f.Level;
            if (link.View.Achievements != null)
                foreach (FederationAchievement a in link.View.Achievements) if (!string.IsNullOrEmpty(a?.Key)) m.Achievements.Add(a.Key);
            if (link.View.Members != null)
                foreach (FederationMember fm in link.View.Members)
                    if (!string.IsNullOrEmpty(fm?.LinkId) && fm.LinkId != link.LinkId) m.Members[fm.LinkId] = fm.State ?? "";
            return m;
        }

        // The first sight of a link this run: what is stored now is what its guild already knows.
        public static void Baseline(FederatedGuildLink link)
        {
            if (string.IsNullOrEmpty(link?.LinkId)) return;
            lock (_lock) if (!_told.ContainsKey(link.LinkId)) _told[link.LinkId] = MarkOf(link);
        }

        // The actor was told directly and the guild saw the push, so a local action is not announced again.
        public static void Rebase(FederatedGuildLink link)
        {
            if (string.IsNullOrEmpty(link?.LinkId)) return;
            lock (_lock) _told[link.LinkId] = MarkOf(link);
        }

        internal static void ForgetForTest() { lock (_lock) _told.Clear(); }

        // A link seen for the first time only sets the starting point.
        public static int Announce(FederatedGuildLink link, bool push = true)
        {
            if (string.IsNullOrEmpty(link?.LinkId)) return 0;
            Mark now = MarkOf(link), was;
            lock (_lock)
            {
                bool known = _told.TryGetValue(link.LinkId, out was);
                _told[link.LinkId] = now;
                if (!known) return 0;
            }
            List<Notice> notices = Diff(was, now, link);
            Deliver(link.LocalGuild, notices);
            bool moved = notices.Count > 0 || was.Revision != now.Revision || was.State != now.State || !was.Rewarded.SetEquals(now.Rewarded);
            if (push && moved) FederationHandler.PushToGuildName(link.LocalGuild);
            return notices.Count;
        }

        internal static List<Notice> Diff(Mark was, Mark now, FederatedGuildLink link)
        {
            var outp = new List<Notice>();
            if (was == null || now == null || link == null) return outp;
            string guild = link.LocalGuild ?? "";
            FederationProfile f = link.View?.Federation;
            string fed = f == null || string.IsNullOrEmpty(f.Name) ? "your federation" : $"[{Shown(f.Tag)}] {Shown(f.Name)}";
            void Add(string title, string body, string tone, bool leaders = false)
                => outp.Add(new Notice { Title = title, Body = body, Tone = tone, LeadersOnly = leaders });

            bool disbanded = now.State == FederatedLinkState.Disbanded
                             || (now.FederationState == "Disbanded" && was.FederationState != "Disbanded");
            if (was.State != now.State || disbanded)
            {
                if (disbanded && was.State != FederatedLinkState.Disbanded)
                    Add("Federation disbanded", $"The federation {fed} that '{guild}' belonged to was disbanded. Its history is kept.", "negative");
                else if (now.State == FederatedLinkState.Removed)
                    Add("Removed from the federation", $"'{guild}' was removed from {fed}. Its history stays with the federation; the Federation window shows why.", "negative");
                else if (now.State == FederatedLinkState.Revoked)
                    Add("Federation link revoked", $"Colony Nexus Global no longer accepts '{guild}''s link to {fed}. The Federation window shows why.", "negative");
                else if (now.State == FederatedLinkState.Left)
                    Add("Left the federation", $"'{guild}' is no longer a member of {fed}. The Federation window shows why.", "neutral");
                else if (now.State == FederatedLinkState.ReproofRequired)
                    Add("Federation link needs proving again",
                        $"Colony Nexus Global asks '{guild}' to prove its link to {fed} again (a restore or key change happened). "
                        + "An Admin or the Owner can do it from the Manage tab of the Federation window.", "neutral", leaders: true);
            }

            if (!was.IsLeader && now.IsLeader && FederatedLinkState.IsMember(now.State))
                Add("Your guild leads the federation",
                    $"Leadership of {fed} was handed to '{guild}'. Its Owner can now edit the profile, remove members and pass leadership on.", "positive");

            // Only while both views are of the same federation this guild is still in.
            bool shared = !disbanded && FederatedLinkState.IsMember(now.State) && was.FederationId.Length > 0
                          && was.FederationId == now.FederationId;
            if (shared)
            {
                var byLink = new Dictionary<string, FederationMember>(StringComparer.Ordinal);
                if (link.View?.Members != null) foreach (FederationMember fm in link.View.Members) if (!string.IsNullOrEmpty(fm?.LinkId)) byLink[fm.LinkId] = fm;
                string Who(string linkId) => byLink.TryGetValue(linkId, out FederationMember found) ? ServerGuild(found) : "a member server";

                if (was.IsLeader && !now.IsLeader)
                    Add("Your guild no longer leads the federation",
                        $"Leadership of {fed} passed from '{guild}' to {Who(f?.LeaderLinkId ?? "")}.", "neutral");

                foreach (KeyValuePair<string, string> m in now.Members)
                {
                    bool liveNow = FederatedLinkState.IsMember(m.Value);
                    bool known = was.Members.TryGetValue(m.Key, out string before);
                    if (liveNow && !known) Add("A server joined the federation", $"{Who(m.Key)} joined {fed}.", "positive");
                    else if (!liveNow && known && FederatedLinkState.IsMember(before))
                    {
                        if (m.Value == FederatedLinkState.Removed) Add("A server was removed from the federation", $"{Who(m.Key)} was removed from {fed}.", "neutral");
                        else if (m.Value == FederatedLinkState.Revoked) Add("A server left the federation", $"{Who(m.Key)} is no longer part of {fed}: its link was revoked.", "neutral");
                        else Add("A server left the federation", $"{Who(m.Key)} left {fed}.", "neutral");
                    }
                }
                foreach (KeyValuePair<string, string> m in was.Members)
                    if (!now.Members.ContainsKey(m.Key) && FederatedLinkState.IsMember(m.Value))
                        Add("A server left the federation", $"A member server is no longer part of {fed}.", "neutral");

                bool levelled = now.Level > was.Level;
                if (levelled) Add("Federation level reached", $"{fed} reached level {now.Level}. The Federation window shows what it unlocks here.", "positive");
                foreach (string key in now.Achievements)
                {
                    // A level achievement says what the level notice above already said.
                    if (was.Achievements.Contains(key) || (levelled && key.StartsWith("level_", StringComparison.Ordinal))) continue;
                    Add("Federation achievement", $"{fed} earned the achievement \"{AchievementName(key)}\".", "positive");
                }

                var changed = new List<string>();
                if (was.Name != now.Name) changed.Add($"name (was \"{Shown(was.Name)}\")");
                if (was.Tag != now.Tag) changed.Add($"tag (was [{Shown(was.Tag)}])");
                if (!string.Equals(was.Color, now.Color, StringComparison.OrdinalIgnoreCase)) changed.Add("colour");
                if (was.Description != now.Description) changed.Add("description");
                if (changed.Count > 0)
                    Add("Federation profile changed", $"{fed} changed its {string.Join(", ", changed)}.", "neutral");
            }

            var objectives = new Dictionary<string, FederationObjective>(StringComparer.Ordinal);
            if (link.View?.Objectives != null)
                foreach (FederationObjective o in link.View.Objectives) if (!string.IsNullOrEmpty(o?.Id)) objectives[o.Id] = o;

            foreach (string id in now.Completed)
            {
                if (was.Completed.Contains(id)) continue;
                objectives.TryGetValue(id, out FederationObjective o);
                string what = o == null ? "a weekly objective" : $"a weekly objective ({o.Category}, target {o.Target})";
                string reward = now.Rewarded.Contains(id) && !was.Rewarded.Contains(id)
                    ? $" Reward: {FederationRewards.Describe(id)} went to the guild vault." : "";
                Add("Federation objective completed", $"{fed} completed {what}.{reward}", "positive");
            }
            // A reward that landed after its objective's completion was already announced without it.
            foreach (string id in now.Rewarded)
            {
                if (was.Rewarded.Contains(id) || !was.Completed.Contains(id)) continue;
                Add("Federation reward granted", $"{FederationRewards.Describe(id)} from a completed federation objective went to '{guild}''s vault.", "positive");
            }
            return outp;
        }

        // Another server wrote these names, and a notice is drawn as rich text, so markup never reaches it.
        internal static string Shown(string s, int max = 48)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^<>]*>", ""))
                if (ch != '<' && ch != '>' && !char.IsControl(ch)) sb.Append(ch);
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) + "..." : t;
        }

        private static string ServerGuild(FederationMember m)
        {
            string server = Shown(m.ServerName), guild = Shown(m.LocalGuild);
            if (server.Length == 0) return guild.Length == 0 ? "a member server" : $"'{guild}'";
            return guild.Length == 0 ? server : $"{server} ('{guild}')";
        }

        private static string AchievementName(string key)
        {
            switch (key)
            {
                case "founded":       return "Founded";
                case "first_ally":    return "First ally";
                case "five_servers":  return "Five servers";
                case "level_3":       return "Level 3";
                case "level_5":       return "Level 5";
                case "level_10":      return "Level 10";
                case "objectives_5":  return "Five objectives";
                case "objectives_25": return "Twenty-five objectives";
                case "verified_25":   return "Twenty-five players took part";
                case "veteran":       return "Veteran";
                default:              return Shown(key);
            }
        }

        private static void Deliver(string guild, List<Notice> notices)
        {
            if (notices == null || notices.Count == 0 || string.IsNullOrEmpty(guild)) return;
            List<string> members = GuildStore.MembersOf(guild), leaders = GuildStore.LeadersOf(guild);
            foreach (Notice n in notices)
                foreach (string u in n.LeadersOnly ? leaders : members)
                    Notifications.KmhNotify.ToUser(u, n.Tone, n.Title, n.Body);
        }
    }
}
