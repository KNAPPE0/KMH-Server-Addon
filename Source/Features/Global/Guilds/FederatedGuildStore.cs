using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global.Guilds
{
    // A change that cannot be written did not happen: a link only in memory would let a restart move silver
    // on authority nobody can prove.
    internal static class FederatedGuildStore
    {
        private static readonly object _lock = new object();
        private static readonly List<FederatedGuildLink> _links = new List<FederatedGuildLink>();

        private sealed class PersistedState
        {
            public List<FederatedGuildLink> Links { get; set; } = new List<FederatedGuildLink>();
        }

        private static long _revision;
        public static long Revision { get { lock (_lock) return _revision; } }

        public static int LinkedCount
        {
            get { lock (_lock) { int n = 0; foreach (FederatedGuildLink l in _links) if (l.MayRead) n++; return n; } }
        }

        public static int ReproofRequiredCount
        {
            get { lock (_lock) { int n = 0; foreach (FederatedGuildLink l in _links) if (l.State == FederatedLinkState.ReproofRequired) n++; return n; } }
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.FederatedGuildsFile, out PersistedState s) || s == null) return;
            // Links proved before incarnation ids belong to the same-named guild as it stood then.
            // Resolved before taking the lock, which must never be held across a call into the guild store.
            if (s.Links != null)
                foreach (FederatedGuildLink l in s.Links)
                    if (l != null && string.IsNullOrEmpty(l.Incarnation)) l.Incarnation = Features.Guilds.GuildStore.LegacyIncarnationFor(l.LocalGuild);
            lock (_lock)
            {
                _links.Clear();
                if (s.Links != null)
                    foreach (FederatedGuildLink l in s.Links)
                        if (l != null && !string.IsNullOrEmpty(l.LinkId)) _links.Add(l);
            }
            InvalidateForIdentity();
            Federation.FederationBenefits.Rebuild();
        }

        public static bool SaveToDisk()
        {
            bool ok;
            lock (_lock)
            {
                _revision++;
                ok = JsonFileStore.Save(KmhDataPaths.FederatedGuildsFile,
                                        new PersistedState { Links = new List<FederatedGuildLink>(_links) });
            }
            // Outside the lock: the rebuild reads the guild store, which this lock must never be held across.
            Federation.FederationBenefits.Rebuild();
            return ok;
        }

        // A link proved by another data set (backup restored onto a new identity) keeps its mapping, not its authority.
        public static int InvalidateForIdentity()
        {
            string id = KmhServerIdentity.Id;
            if (string.IsNullOrEmpty(id)) return 0;

            int changed = 0;
            lock (_lock)
                foreach (FederatedGuildLink l in _links)
                {
                    if (string.IsNullOrEmpty(l.DataSetId)) { l.DataSetId = id; continue; }
                    if (string.Equals(l.DataSetId, id, StringComparison.Ordinal)) continue;
                    if (!l.MayRead) continue;
                    l.State = FederatedLinkState.ReproofRequired;
                    l.Detail = FederatedLinkDetail.DataSetChanged;
                    changed++;
                }

            if (changed > 0)
            {
                SaveToDisk();
                Diagnostics.ServerLog.Warn(
                    $"Federated guilds: {changed} link(s) were proved by a different data set - they need re-proof before silver can move.");
            }
            return changed;
        }

        // Mirrors Global's re-proof demand so the refusal is immediate and offline, not a failing round trip.
        public static int InvalidateForGeneration(int generation)
        {
            int changed = 0;
            lock (_lock)
                foreach (FederatedGuildLink l in _links)
                {
                    if (l.State != FederatedLinkState.Active || l.SigningGeneration == generation) continue;
                    l.State = FederatedLinkState.ReproofRequired;
                    l.Detail = FederatedLinkDetail.GenerationChanged;
                    changed++;
                }
            if (changed > 0) SaveToDisk();
            return changed;
        }

        public static FederatedGuildLink ById(string linkId)
        {
            if (string.IsNullOrEmpty(linkId)) return null;
            lock (_lock)
                foreach (FederatedGuildLink l in _links)
                    if (string.Equals(l.LinkId, linkId, StringComparison.Ordinal)) return l;
            return null;
        }

        // The local guild name is a lookup key here and nowhere else: it finds a link, it never identifies one.
        public static FederatedGuildLink ForGuild(string localGuild)
        {
            if (string.IsNullOrEmpty(localGuild)) return null;
            lock (_lock)
                foreach (FederatedGuildLink l in _links)
                    if (string.Equals(l.LocalGuild, localGuild, StringComparison.OrdinalIgnoreCase)) return l;
            return null;
        }

        // The link a guild holds only while it is still the guild that proved it.
        public static FederatedGuildLink ForGuildInstance(string localGuild, string incarnation)
        {
            FederatedGuildLink l = ForGuild(localGuild);
            return l != null && !string.IsNullOrEmpty(incarnation)
                   && string.Equals(l.Incarnation, incarnation, StringComparison.Ordinal) ? l : null;
        }

        // Local only; the caller queues telling Global so an outage cannot hold the local guild hostage.
        public static FederatedGuildLink EndLocal(string localGuild, string state, string why)
        {
            if (string.IsNullOrEmpty(localGuild)) return null;
            FederatedGuildLink ended = null;
            lock (_lock)
                foreach (FederatedGuildLink l in _links)
                    if (string.Equals(l.LocalGuild, localGuild, StringComparison.OrdinalIgnoreCase) && l.MayRead)
                    {
                        l.State = state;
                        l.Detail = why ?? "";
                        ended = l;
                    }
            if (ended == null) return null;
            SaveToDisk();
            Diagnostics.ServerLog.Info($"Federation: '{localGuild}' left its federation here - {why}.");
            return ended;
        }

        // Adopted as it stands, so a removal or disband done on another server shows here as soon as it is read.
        public static bool RememberView(string linkId, Federation.FederationView view)
        {
            FederatedGuildLink link = ById(linkId);
            if (link == null || view == null) return false;
            string wasState, wasDetail;
            lock (_lock)
            {
                wasState = link.State; wasDetail = link.Detail;
                link.View = view;
                link.ViewReadUtcTicks = DateTime.UtcNow.Ticks;
                string self = view.Self?.State ?? "";
                if (!string.IsNullOrEmpty(self) && self != link.State)
                {
                    link.State = self;
                    // An ended link's reason is in EndReason; Detail is an empty string there, never null.
                    link.Detail = !FederatedLinkState.IsMember(self) && !string.IsNullOrEmpty(view.Self.EndReason)
                        ? view.Self.EndReason : view.Self.Detail ?? "";
                }
                if (view.Federation != null && view.Federation.State == "Disbanded" && link.MayRead)
                {
                    link.State = FederatedLinkState.Disbanded;
                    link.Detail = view.Federation.DisbandReason ?? "";
                }
            }
            if (SaveToDisk()) return true;
            // A cache of Global's answer: keeping it in memory is harmless, the next read replaces it either way.
            Diagnostics.ServerLog.Verbose($"Federation: the view of {linkId} could not be written down ({wasState}/{wasDetail}).");
            return true;
        }

        public static bool HasRewarded(string linkId, string objectiveId)
        {
            FederatedGuildLink link = ById(linkId);
            if (link == null || string.IsNullOrEmpty(objectiveId)) return false;
            lock (_lock) return link.RewardedObjectives != null && link.RewardedObjectives.Contains(objectiveId);
        }

        // Written after the vault took the reward; the vault marker makes a repeat before this lands harmless.
        public static bool MarkRewarded(string linkId, string objectiveId)
        {
            FederatedGuildLink link = ById(linkId);
            if (link == null || string.IsNullOrEmpty(objectiveId)) return false;
            lock (_lock)
            {
                if (link.RewardedObjectives == null) link.RewardedObjectives = new List<string>();
                if (link.RewardedObjectives.Contains(objectiveId)) return true;
                link.RewardedObjectives.Add(objectiveId);
                // Objectives are weekly, so a year of them is plenty to answer "already rewarded".
                while (link.RewardedObjectives.Count > 260) link.RewardedObjectives.RemoveAt(0);
            }
            return SaveToDisk();
        }

        public static List<FederatedGuildLink> All() { lock (_lock) return new List<FederatedGuildLink>(_links); }

        // Null when unwritten, so the caller refuses rather than act on a link that would vanish on restart.
        public static FederatedGuildLink Remember(string linkId, string guildId, string localGuild, string provedBy,
                                                  int generation, string incarnation = null)
        {
            if (string.IsNullOrEmpty(linkId) || string.IsNullOrEmpty(guildId) || string.IsNullOrEmpty(localGuild))
                return null;

            string inc = incarnation ?? Features.Guilds.GuildStore.IncarnationOf(localGuild) ?? "";
            FederatedGuildLink link = null;
            bool added = false;
            string wasState = null, wasDetail = null;
            lock (_lock)
            {
                foreach (FederatedGuildLink l in _links)
                    if (string.Equals(l.LinkId, linkId, StringComparison.Ordinal)) { link = l; break; }

                if (link == null)
                {
                    // A new link id for a federated name means Global re-issued it; the old row would shadow it forever.
                    for (int i = _links.Count - 1; i >= 0; i--)
                        if (string.Equals(_links[i].LocalGuild, localGuild, StringComparison.OrdinalIgnoreCase))
                            _links.RemoveAt(i);

                    link = new FederatedGuildLink { LinkId = linkId, CreatedUtcTicks = DateTime.UtcNow.Ticks };
                    _links.Add(link);
                    added = true;
                }
                else { wasState = link.State; wasDetail = link.Detail; }

                link.GuildId = guildId;
                link.LocalGuild = localGuild;
                link.Incarnation = inc;
                link.ProvedBy = provedBy ?? "";
                link.SigningGeneration = generation;
                link.DataSetId = KmhServerIdentity.Id ?? "";
                link.State = FederatedLinkState.Active;
                link.Detail = "";
                link.ProvedUtcTicks = DateTime.UtcNow.Ticks;
            }

            if (SaveToDisk()) return link;

            lock (_lock)
            {
                if (added) _links.Remove(link);
                else { link.State = wasState; link.Detail = wasDetail; }
            }
            Diagnostics.ServerLog.Error(
                $"Federated guilds: refusing a link for '{localGuild}' that could not be written down.");
            return null;
        }

        public static bool SetState(string linkId, string state, string detail)
        {
            FederatedGuildLink link = ById(linkId);
            if (link == null) return false;
            string wasState, wasDetail;
            lock (_lock)
            {
                wasState = link.State; wasDetail = link.Detail;
                link.State = state; link.Detail = detail ?? "";
            }
            if (SaveToDisk()) return true;
            lock (_lock) { link.State = wasState; link.Detail = wasDetail; }
            return false;
        }

        // Kept as Revoked, not deleted, so the guild can still see what became of its silver.
        public static bool Revoke(string linkId, string detail)
            => SetState(linkId, FederatedLinkState.Revoked, detail);

        // A failed write is ignored: the balance is a cache of Global's answer that the next read replaces.
        public static void RememberBalance(string linkId, FederatedBalance balance)
        {
            FederatedGuildLink link = ById(linkId);
            if (link == null || balance == null) return;
            lock (_lock) { balance.ReadUtcTicks = DateTime.UtcNow.Ticks; link.Balance = balance; }
            SaveToDisk();
        }

        internal static void ClearForTest() { lock (_lock) { _links.Clear(); _revision = 0; } }
    }
}
