using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Guilds
{
    internal static class GuildStore
    {
        private static readonly object _lock = new object();

        // One id per guild for its whole life: a name is reused the moment a guild disbands, so anything granted to a
        // guild (a federation link above all) is bound to this id, never to the name.
        private static readonly Dictionary<string, string> _incarnation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string LegacyIncarnationFor(string name) => "legacy:" + (name ?? "").ToLowerInvariant();

        // Committed state only: read from the published identity set, never from a change still inside the lock.
        public static string IncarnationOf(string guildName) => GuildIdentity.Of(guildName);

        // Caller holds _lock. Published on every commit, rollback and load, so the set is always one the disk took.
        private static void PublishIdentityLocked()
        {
            var live = new Dictionary<string, string>(_guilds.Count, StringComparer.OrdinalIgnoreCase);
            foreach (string name in _guilds.Keys)
                live[name] = _incarnation.TryGetValue(name, out string id) ? id : LegacyIncarnationFor(name);
            GuildIdentity.Publish(live);
        }

        private static readonly Dictionary<string, GuildSnapshot> _guilds
            = new Dictionary<string, GuildSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _userToGuild
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Keyed by guild+user rather than the member row, so leaving and rejoining cannot reset the daily cap.
        private static readonly Dictionary<string, (long DayStartUtc, long Withdrawn)> _withdrawTally
            = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        private static string TallyKey(string guild, string user) => (guild ?? "") + "\u0001" + (user ?? "");

        // Always called outside _lock, so a subscriber cannot deadlock or observe half-applied state.
        private static void RaiseChanged(string guildName, string reason, string actor = "")
        {
            if (string.IsNullOrEmpty(guildName)) return;
            Extensibility.KmhEventBus.Instance.RaiseGuildChanged(
                new KMH.Sdk.Server.Events.GuildChangedEvent { GuildName = guildName, Reason = reason ?? "", Actor = actor ?? "" });
        }

        public static bool CreateGuild(string name)
        {
            if (!GuildNames.TryNormalize(name, out name, out _)) return false;
            Treasury.TreasuryStore.PrepareGuildName(name);
            bool ok = false;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (_guilds.ContainsKey(name)) return false;
                foreach (GuildSnapshot other in _guilds.Values) ForgetNameLocked(other, name);
                _guilds[name] = new GuildSnapshot { Name = name };
                _incarnation[name] = Guid.NewGuid().ToString("N");
                ok = CommitAuthorityLocked(before);
            }
            if (ok) { AfterCreateCommitForTest?.Invoke(name); ForgetGuildName(name); RaiseChanged(name, "created"); }
            return ok;
        }

        internal const int MaxMotdLength = 512;

        // No rank gate: only trusted SDK extension code calls this.
        public static bool SetMotdByName(string guildName, string motd)
        {
            if (string.IsNullOrEmpty(guildName)) return false;
            bool ok = false;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return false;
                g.Motd = Util.KmhSafe.Cap(motd ?? "", MaxMotdLength);
                ok = CommitAuthorityLocked(before);
            }
            if (ok) RaiseChanged(guildName, "motd");
            return ok;
        }

        public static bool AddMember(string username, string guildName, string rank = GuildMemberDto.RankMember)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(guildName)) return false;
            bool ok = false;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return false;
                if (_userToGuild.ContainsKey(username)) return false;
                g.Members.Add(NewMember(username, string.IsNullOrEmpty(rank) ? GuildMemberDto.RankMember : rank));
                _userToGuild[username] = g.Name;
                ok = CommitAuthorityLocked(before);
            }
            if (ok) RaiseChanged(guildName, "member_joined", username);
            return ok;
        }

        // Atomic: if either step fails the store is rolled back.
        public static bool CreateGuildAndJoinAsAdmin(string creator, string guildName, out string errorReason, int hallTile = -1)
        {
            errorReason = null;
            if (string.IsNullOrWhiteSpace(creator) || string.IsNullOrWhiteSpace(guildName))
            {
                errorReason = "Both creator and guild name are required.";
                return false;
            }
            if (!GuildHallRules.CanCreate(hallTile, out errorReason)) return false;
            // The name is the treasury key too, so it goes through the one rule that keeps it off a personal vault.
            if (!GuildNames.TryNormalize(guildName, out guildName, out errorReason)) return false;
            // Outside the guild lock: whatever an earlier guild of this name left is set aside before the new one exists.
            Treasury.TreasuryStore.PrepareGuildName(guildName);
            bool ok = false;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (_guilds.ContainsKey(guildName))
                {
                    errorReason = $"A guild named '{guildName}' already exists.";
                    return false;
                }
                if (_userToGuild.ContainsKey(creator))
                {
                    errorReason = "You are already in a guild - /kmh guild leave first.";
                    return false;
                }
                GuildSnapshot g = new GuildSnapshot { Name = guildName };
                g.Members.Add(NewMember(creator, GuildMemberDto.RankOwner));
                // The hall lives on the guild record, so a reused name never inherits an old one.
                if (hallTile >= 0) g.Hall = NewHall(hallTile, creator);
                // A name freed before this build may still be referenced; the new guild inherits none of it.
                foreach (GuildSnapshot other in _guilds.Values) ForgetNameLocked(other, guildName);
                _guilds[guildName]    = g;
                _incarnation[guildName] = Guid.NewGuid().ToString("N");
                _userToGuild[creator] = guildName;
                PurgeInvitesLocked(creator);
                ok = CommitAuthorityLocked(before);
                if (!ok) errorReason = "The server could not save that guild - nothing was created. Try again shortly.";
            }
            if (ok) { AfterCreateCommitForTest?.Invoke(guildName); ForgetGuildName(guildName); RaiseChanged(guildName, "created", creator); }
            return ok;
        }

        private static GuildMemberDto NewMember(string username, string rank)
            => new GuildMemberDto { Username = username, Rank = rank, JoinedUtcTicks = DateTime.UtcNow.Ticks };

        private static GuildHallDto NewHall(int tile, string leader)
            => new GuildHallDto
            {
                HasHall = true, Tile = tile, Leader = leader,
                RadiusTiles = Economy.EconomyConfig.Current.GuildHall.AccessRadiusTiles,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            };

        public static (bool ok, string reason) SetGuildHall(string actor, int tile)
        {
            if (string.IsNullOrEmpty(actor)) return (false, "No user.");
            if (tile < 0) return (false, "Pick a valid world tile for the Guild Hall.");
            string guildName;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_userToGuild.TryGetValue(actor, out string gname) || !_guilds.TryGetValue(gname, out GuildSnapshot g))
                    return (false, "You are not in a guild.");
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                    return (false, "Only the guild admin can set the Guild Hall.");
                g.Hall = NewHall(tile, actor);
                guildName = g.Name;
                if (!CommitAuthorityLocked(before)) return (false, "The server could not save that - the Guild Hall is unchanged.");
            }
            RaiseChanged(guildName, "hall_set", actor);
            return (true, "Guild Hall set.");
        }

        public static System.Collections.Generic.List<(string Name, bool HasHall)> AllGuildHallStatus()
        {
            var list = new System.Collections.Generic.List<(string, bool)>();
            lock (_lock)
                foreach (var kv in _guilds)
                    list.Add((kv.Key, kv.Value?.Hall != null && kv.Value.Hall.HasHall));
            return list;
        }

        public static (bool ok, string reason) RemoveGuildHall(string actor)
        {
            if (string.IsNullOrEmpty(actor)) return (false, "No user.");
            string guildName;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_userToGuild.TryGetValue(actor, out string gname) || !_guilds.TryGetValue(gname, out GuildSnapshot g))
                    return (false, "You are not in a guild.");
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                    return (false, "Only the guild admin can remove the Guild Hall.");
                g.Hall = null;
                guildName = g.Name;
                if (!CommitAuthorityLocked(before)) return (false, "The server could not save that - the Guild Hall is unchanged.");
            }
            RaiseChanged(guildName, "hall_removed", actor);
            return (true, "Guild Hall removed.");
        }

        public static bool JoinGuild(string username, string guildName, out string errorReason, Economy.EconomyContext ctx = default)
        {
            errorReason = null;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(guildName))
            {
                errorReason = "Both your username and guild name are required.";
                return false;
            }
            bool ok = false;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (_userToGuild.ContainsKey(username))
                {
                    errorReason = "You are already in a guild - /kmh guild leave first.";
                    return false;
                }
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g))
                {
                    errorReason = $"No guild named '{guildName}' exists.";
                    return false;
                }
                // Not consumed here: a later gate can still fail the join, which would burn the invite.
                bool invited = g.PendingInvites.Exists(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase));
                if (!g.OpenJoin && !invited)
                {
                    errorReason = $"'{guildName}' is invite-only - ask an admin to /kmh guild invite you.";
                    return false;
                }
                if (!GuildHallRules.CanJoin(g, ctx, out errorReason)) return false;
                g.Members.Add(NewMember(username, GuildMemberDto.RankMember));
                _userToGuild[username] = g.Name;
                PurgeInvitesLocked(username);   // invites elsewhere are stale now
                ok = CommitAuthorityLocked(before);
                if (!ok) errorReason = "The server could not save that join - nothing changed. Try again shortly.";
            }
            if (ok) RaiseChanged(guildName, "member_joined", username);
            return ok;
        }

        public static bool Invite(string actor, string target, out string guildName, out string errorReason, Economy.EconomyContext ctx = default)
        {
            errorReason = null; guildName = null;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrWhiteSpace(target))
            { errorReason = "Caller + target are required."; return false; }
            target = target.Trim();
            if (target.Length > Util.KmhSafe.MaxUsernameLength) { errorReason = "That player name is too long."; return false; }
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
            { errorReason = "You can't invite yourself."; return false; }

            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname)) { errorReason = "You're not in a guild."; return false; }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))   { errorReason = "Your guild record is missing."; return false; }
                if (!GuildPermissions.Can(FindRankLocked(g, actor), GuildCapability.Invite))
                { errorReason = "Only an Admin or Moderator can invite."; return false; }
                if (!GuildHallRules.CanInvite(g, ctx, out errorReason)) return false;
                if (_userToGuild.ContainsKey(target)) { errorReason = $"'{target}' is already in a guild."; return false; }
                AuthorityPoint before = AuthorityPointLocked();
                PruneInvitesLocked(g, DateTime.UtcNow.Ticks);
                if (g.PendingInvites.Count >= MaxPendingInvites
                    && !g.PendingInvites.Exists(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase)))
                { errorReason = $"This guild already has {MaxPendingInvites} pending invites."; return false; }
                if (!g.PendingInvites.Exists(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase)))
                    g.PendingInvites.Add(target);
                (g.InviteMeta ?? (g.InviteMeta = new Dictionary<string, GuildInviteMetaDto>(StringComparer.OrdinalIgnoreCase)))[target]
                    = new GuildInviteMetaDto { Inviter = actor, CreatedUtcTicks = DateTime.UtcNow.Ticks };
                guildName = gname;
                ok = CommitAuthorityLocked(before);
            }
            if (!ok) errorReason = "The server couldn't save that invite - nothing changed. Try again shortly.";
            return ok;
        }

        public static bool DeclineInvite(string username, string guildName, out string inviter, out string errorReason)
        {
            inviter = null; errorReason = null;
            if (string.IsNullOrEmpty(username) || string.IsNullOrWhiteSpace(guildName))
            { errorReason = "Guild name is required."; return false; }
            bool ok = false;
            lock (_lock)
            {
                if (!_guilds.TryGetValue(guildName.Trim(), out GuildSnapshot g))
                { errorReason = $"No guild named '{guildName}' exists."; return false; }
                AuthorityPoint before = AuthorityPointLocked();
                if (g.PendingInvites.RemoveAll(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase)) == 0)
                { errorReason = $"'{g.Name}' has no standing invite for you."; return false; }
                if (g.InviteMeta != null && g.InviteMeta.TryGetValue(username, out GuildInviteMetaDto meta))
                { inviter = meta.Inviter; g.InviteMeta.Remove(username); }
                ok = CommitAuthorityLocked(before);
            }
            if (!ok) errorReason = "The server couldn't save that - nothing changed. Try again shortly.";
            return ok;
        }

        public static List<GuildInviteDto> InvitesFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return new List<GuildInviteDto>();
            lock (_lock) return InvitesForLocked(username);
        }

        private static List<GuildInviteDto> InvitesForLocked(string username)
        {
            List<GuildInviteDto> outList = new List<GuildInviteDto>();
            foreach (GuildSnapshot g in _guilds.Values)
            {
                if (!g.PendingInvites.Exists(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase))) continue;
                GuildInviteMetaDto meta = null;
                g.InviteMeta?.TryGetValue(username, out meta);
                outList.Add(new GuildInviteDto
                {
                    GuildName       = g.Name,
                    Inviter         = meta?.Inviter ?? "",
                    CreatedUtcTicks = meta?.CreatedUtcTicks ?? 0,
                    Members         = g.Members.Count,
                });
            }
            outList.Sort((a, b) => b.CreatedUtcTicks.CompareTo(a.CreatedUtcTicks));
            return outList;
        }

        private static void PurgeInvitesLocked(string username)
        {
            foreach (GuildSnapshot g in _guilds.Values)
            {
                g.PendingInvites.RemoveAll(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase));
                g.InviteMeta?.Remove(username);
            }
        }

        public static bool SetOpenJoin(string actor, bool open, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor)) { errorReason = "Caller is empty."; return false; }
            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname)) { errorReason = "You're not in a guild."; return false; }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))   { errorReason = "Your guild record is missing."; return false; }
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                { errorReason = "Only an Admin can change join mode."; return false; }
                if (g.OpenJoin == open) { errorReason = "noop"; return false; }   // already in that mode - no save, no log
                AuthorityPoint before = AuthorityPointLocked();
                g.OpenJoin = open;
                ok = CommitAuthorityLocked(before);
            }
            if (!ok) errorReason = "The server couldn't save that - nothing changed. Try again shortly.";
            return ok;
        }

        // The instant after a disband hands its vault back: a credit that lands here is the orphan-vault defect.
        internal static Action<string> AfterVaultHandBackForTest;
        // Between a new guild's commit and the clean-up of everything that named its predecessor.
        internal static Action<string> AfterCreateCommitForTest;
        // Between a donation's checks and its booking.
        internal static Action<string> AfterDonationCheckForTest;

        public static bool Leave(string username, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(username))
            {
                errorReason = "Caller username is empty.";
                return false;
            }
            bool   ok             = false;
            bool   guildDestroyed = false;
            string gname          = null;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_userToGuild.TryGetValue(username, out gname))
                {
                    errorReason = "You are not in a guild.";
                    return false;
                }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))
                {
                    errorReason = "Your guild record is missing - server state may be corrupted.";
                    return false;
                }

                // The Owner can't walk out on a populated guild - ownership must move first (alone, leaving disbands).
                GuildMemberDto callerM = g.Members.Find(m => string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase));
                if (callerM != null && string.Equals(callerM.Rank, GuildMemberDto.RankOwner, StringComparison.OrdinalIgnoreCase)
                    && g.Members.Count > 1)
                {
                    errorReason = "The guild Owner must transfer ownership or disband the guild before leaving.";
                    return false;
                }

                g.Members.RemoveAll(m => string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase));
                _userToGuild.Remove(username);

                // Last member out -> remove the guild entirely, and every reference to it: a guild re-created
                // under this name is somebody else, and must not inherit an alliance or a pending donation.
                if (g.Members.Count == 0)
                {
                    _guilds.Remove(gname); guildDestroyed = true;
                    // Dropped with the guild, so the commit publishes it dead before the vault is handed back below.
                    _incarnation.Remove(gname);
                    foreach (GuildSnapshot other in _guilds.Values) ForgetNameLocked(other, gname);
                }

                // Commit before the vault moves, or a disband the disk never took leaves the guild its silver already handed out.
                ok = CommitAuthorityLocked(before);
                if (!ok) { guildDestroyed = false; errorReason = "The server could not save that - you are still in the guild. Try again shortly."; }
            }
            if (ok)
            {
                // Handed back outside the guild lock, so re-creating the name cannot resurrect the vault.
                if (guildDestroyed)
                {
                    long moved = Treasury.TreasuryStore.MoveGuildVaultToPersonal(gname, username);
                    AfterVaultHandBackForTest?.Invoke(gname);
                    if (moved > 0) Diagnostics.ServerLog.Info($"Guild '{gname}' disbanded - {moved}s vault returned to {username}");
                    int orphaned = Sites.SiteStore.CountGuildSites(gname);
                    if (orphaned > 0)
                        Diagnostics.ServerLog.Warn($"Guild '{gname}' disbanded owning {orphaned} site(s) - they are paused and keep their stored goods until an admin rebinds them ('kmh orphans').");
                    Global.Federation.FederationService.OnGuildDisbanded(gname);
                    ForgetGuildName(gname);
                }
                RaiseChanged(gname, "member_left", username);
            }
            return ok;
        }

        // A wiped Owner hands the guild to its senior member first, so the others keep their guild and its vault.
        internal static bool LeaveForReset(string username, out string heir, out string errorReason)
        {
            heir = null; errorReason = null;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(username) || !_userToGuild.TryGetValue(username, out string gname)) return true;
                if (_guilds.TryGetValue(gname, out GuildSnapshot g)
                    && string.Equals(FindRankLocked(g, username), GuildMemberDto.RankOwner, StringComparison.OrdinalIgnoreCase))
                {
                    GuildMemberDto pick = null;
                    foreach (GuildMemberDto m in g.Members)
                        if (!string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase)
                            && (pick == null || RankOrder(m.Rank) < RankOrder(pick.Rank))) pick = m;
                    heir = pick?.Username;
                }
            }
            if (heir != null && !TransferAdmin(username, heir, out errorReason)) return false;
            return Leave(username, out errorReason);
        }

        public static bool TransferAdmin(string callerUsername, string targetUsername, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(callerUsername) || string.IsNullOrWhiteSpace(targetUsername))
            {
                errorReason = "Caller + target usernames are required.";
                return false;
            }
            if (string.Equals(callerUsername, targetUsername, StringComparison.OrdinalIgnoreCase))
            {
                errorReason = "Cannot transfer Admin to yourself.";
                return false;
            }

            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(callerUsername, out string cGuild))
                {
                    errorReason = "You are not in a guild.";
                    return false;
                }
                if (!_userToGuild.TryGetValue(targetUsername, out string tGuild))
                {
                    errorReason = $"'{targetUsername}' is not in any guild.";
                    return false;
                }
                if (!string.Equals(cGuild, tGuild, StringComparison.OrdinalIgnoreCase))
                {
                    errorReason = $"'{targetUsername}' is not in your guild.";
                    return false;
                }
                AuthorityPoint before = AuthorityPointLocked();
                if (!_guilds.TryGetValue(cGuild, out GuildSnapshot g))
                {
                    errorReason = "Your guild record is missing - server state may be corrupted.";
                    return false;
                }

                GuildMemberDto caller = g.Members.Find(m => string.Equals(m.Username, callerUsername, StringComparison.OrdinalIgnoreCase));
                GuildMemberDto target = g.Members.Find(m => string.Equals(m.Username, targetUsername, StringComparison.OrdinalIgnoreCase));
                if (caller == null || target == null)
                {
                    errorReason = "Member record not found.";
                    return false;
                }
                if (!string.Equals(caller.Rank, GuildMemberDto.RankOwner, StringComparison.OrdinalIgnoreCase))
                {
                    errorReason = "Only the guild Owner can transfer ownership.";
                    return false;
                }

                target.Rank = GuildMemberDto.RankOwner;
                caller.Rank = GuildMemberDto.RankAdmin;   // outgoing owner keeps admin powers, never a second owner
                ok = CommitAuthorityLocked(before);
                if (!ok) errorReason = "The server could not save that transfer - ownership is unchanged.";
            }
            if (ok) RaiseChanged(CurrentGuildOf(callerUsername), "rank_changed", targetUsername);
            return ok;
        }

        // Returns "" when it could not be recorded; the donation itself is unaffected either way.
        private static string RecordCompletedDonationTx(string actor, string guildName, long amount)
        {
            try
            {
                Transactions.KmhTransaction tx = Transactions.KmhTransaction.Create(
                    actor, "guild", Transactions.KmhTxType.GuildContribution, $"{amount}s to '{guildName}'");
                tx.Guild = guildName;
                tx.Validated = tx.Requested;
                if (!Transactions.KmhTransactionRepository.Add(tx)) return "";
                foreach (Transactions.KmhTxState s in new[]
                { Transactions.KmhTxState.Validating, Transactions.KmhTxState.Reserved, Transactions.KmhTxState.Approved,
                  Transactions.KmhTxState.Delivered, Transactions.KmhTxState.Confirmed })
                    tx.Advance(s);
                Transactions.KmhTransactionRepository.Update(tx);
                return tx.Id;
            }
            catch { return ""; }
        }

        public static List<(string Name, int MemberCount)> ListGuilds()
        {
            List<(string, int)> result;
            lock (_lock)
            {
                result = new List<(string, int)>(_guilds.Count);
                foreach (GuildSnapshot g in _guilds.Values)
                {
                    result.Add((g.Name, g.Members?.Count ?? 0));
                }
            }
            result.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        public struct GuildSummary
        {
            public string Name;
            public int    MemberCount;
            public long   TreasurySilver;
            public bool   OpenJoin;
            public bool   AllSimulated;   // every member is a playtest account, so the boards can leave it out
        }
        // A modified client can ask for the leaderboard at will, so without this cache each request is a full
        // cross-store aggregate; a ranking a few seconds old is imperceptible.
        internal static readonly TimeSpan LeaderboardMaxAge = TimeSpan.FromSeconds(3);

        private static readonly object _leaderboardLock = new object();
        private static List<GuildSummary> _leaderboard;
        private static DateTime _leaderboardAtUtc;

        internal static int LeaderboardComputations;

        public static List<GuildSummary> ComputeLeaderboard() => ComputeLeaderboard(DateTime.UtcNow);

        // One person in it makes it a real guild; an empty one is never counted as simulated.
        internal static bool AllSimulated(List<string> members)
            => members != null && members.Count > 0 && members.TrueForAll(Simulation.SimulatedAccounts.IsSimulated);

        internal static List<GuildSummary> ComputeLeaderboard(DateTime nowUtc)
        {
            lock (_leaderboardLock)
                if (_leaderboard != null && nowUtc - _leaderboardAtUtc < LeaderboardMaxAge)
                    return new List<GuildSummary>(_leaderboard);

            // Names first, under the guild lock only: taking the treasury lock per guild inside it queued every operation.
            List<GuildSummary> rows;
            var memberNames = new List<List<string>>();
            lock (_lock)
            {
                rows = new List<GuildSummary>(_guilds.Count);
                foreach (GuildSnapshot g in _guilds.Values)
                {
                    rows.Add(new GuildSummary { Name = g.Name, MemberCount = g.Members?.Count ?? 0, OpenJoin = g.OpenJoin });
                    var names = new List<string>();
                    foreach (GuildMemberDto m in g.Members ?? new List<GuildMemberDto>()) names.Add(m?.Username);
                    memberNames.Add(names);
                }
            }

            for (int i = 0; i < rows.Count; i++)
            {
                GuildSummary row = rows[i];
                row.TreasurySilver = Treasury.TreasuryStore.GetGuildSilver(row.Name);
                row.AllSimulated = AllSimulated(memberNames[i]);
                rows[i] = row;
            }

            lock (_leaderboardLock)
            {
                _leaderboard = rows;
                _leaderboardAtUtc = nowUtc;
                LeaderboardComputations++;
                return new List<GuildSummary>(rows);
            }
        }

        // A lock() on another thread rather than Monitor.TryEnter, because the lock-order scanner only sees lock().
        internal static bool GuildLockIsFreeForTest(int ms)
            => System.Threading.Tasks.Task.Run(() => { lock (_lock) { } }).Wait(ms);

        internal static void ResetLeaderboardCacheForTest()
        {
            lock (_leaderboardLock) { _leaderboard = null; _leaderboardAtUtc = default; LeaderboardComputations = 0; }
        }

        // Attributed once across members, so a shared vault is never counted in full for each of them.
        public static long VaultShareFor(string username)
        {
            if (string.IsNullOrEmpty(username)) return 0;
            string guild = CurrentGuildOf(username);
            if (string.IsNullOrEmpty(guild)) return 0;
            // Goods included: rewards and site output land there as items, and a silver-only share let them shelter.
            long vault = Treasury.TreasuryStore.GuildVaultValue(guild);
            if (vault <= 0) return 0;

            lock (_lock)
            {
                if (!_guilds.TryGetValue(guild, out Dto.GuildSnapshot g) || g?.Members == null || g.Members.Count == 0) return 0;
                long mine = 0, total = 0; bool member = false;
                foreach (Dto.GuildMemberDto m in g.Members)
                {
                    if (m == null) continue;
                    long c = Math.Max(0L, m.SilverContributed);
                    total += c;
                    if (string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase)) { mine = c; member = true; }
                }
                if (!member) return 0;
                return total > 0 ? (long)(vault * ((double)mine / total)) : vault / g.Members.Count;
            }
        }

        public static string CurrentGuildOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock)
            {
                return _userToGuild.TryGetValue(username, out string g) ? g : null;
            }
        }

        // The guild now holding this name existed no later than its earliest member joined; 0 when that is unknown.
        internal static long EarliestJoinTicks(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            lock (_lock)
            {
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return 0;
                long first = 0;
                foreach (GuildMemberDto m in g.Members)
                    if (m.JoinedUtcTicks > 0 && (first == 0 || m.JoinedUtcTicks < first)) first = m.JoinedUtcTicks;
                return first;
            }
        }

        public static string RankOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(username, out string gname)) return null;
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g)) return null;
                return FindRankLocked(g, username);
            }
        }

        // Null unless the user is the guild's only member, since a solo guild vault would shelter a save reset.
        public static string SoloGuildOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(username, out string gname)) return null;
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g)) return null;
                return (g.Members != null && g.Members.Count == 1) ? gname : null;
            }
        }

        public static bool AreAllied(string guildA, string guildB)
        {
            if (string.IsNullOrWhiteSpace(guildA) || string.IsNullOrWhiteSpace(guildB)) return false;
            if (string.Equals(guildA, guildB, StringComparison.OrdinalIgnoreCase)) return false;

            // Mutual only: BOTH guilds must list the other as Allied. A one-sided declaration must never grant access.
            lock (_lock)
            {
                return _guilds.TryGetValue(guildA, out GuildSnapshot ga) && ga.Relationships != null
                    && ga.Relationships.TryGetValue(guildB, out string relA) && relA == GuildSnapshot.RelationAllied
                    && _guilds.TryGetValue(guildB, out GuildSnapshot gb) && gb.Relationships != null
                    && gb.Relationships.TryGetValue(guildA, out string relB) && relB == GuildSnapshot.RelationAllied;
            }
        }

        public static int GetMarketplaceTaxReductionPoints(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            lock (_lock)
            {
                return _guilds.TryGetValue(guildName, out GuildSnapshot g) ? g.Perks.MarketplaceTaxReductionPoints : 0;
            }
        }

        // Compute-only: SaleSplit owns the actual skim.
        public static int GetGuildSaleTaxPercent(string sellerUsername, out string guildName)
        {
            guildName = null;
            if (string.IsNullOrEmpty(sellerUsername)) return 0;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(sellerUsername, out guildName)) return 0;
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g))      return 0;
                return Math.Max(0, Math.Min(50, g.Settings.MarketplaceSaleTaxPercent));
            }
        }

        public static int SiteMaxWorkersBonusFor(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            lock (_lock) { return _guilds.TryGetValue(guildName, out GuildSnapshot g) ? g.Perks.SiteMaxWorkersBonus : 0; }
        }

        // Cached member summary, exactly like SilverContributed - the contribution ledger stays authoritative.
        public static void AddItemsContributed(string guildName, string actor, long qty)
        {
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(actor) || qty <= 0) return;
            lock (_lock)
            {
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g) || g == null) return;
                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                if (m == null) return;
                m.ItemsContributed += qty;
            }
            SaveToDisk();
        }

        // Bounded, or nothing but accepting or declining would ever remove a pending invite.
        public const int InviteLifetimeDays   = 14;
        public const int MaxPendingInvites    = 25;

        // Undated invites are stamped on first sight, so an upgrade does not wipe every outstanding one.
        internal static int PruneInvitesLocked(GuildSnapshot g, long nowTicks)
        {
            if (g?.PendingInvites == null) return 0;
            long cutoff = nowTicks - TimeSpan.FromDays(InviteLifetimeDays).Ticks;
            var dead = new List<string>();
            foreach (string u in g.PendingInvites)
            {
                GuildInviteMetaDto meta = null;
                g.InviteMeta?.TryGetValue(u, out meta);
                if (meta == null)
                {
                    (g.InviteMeta ?? (g.InviteMeta = new Dictionary<string, GuildInviteMetaDto>(StringComparer.OrdinalIgnoreCase)))[u]
                        = new GuildInviteMetaDto { Inviter = "", CreatedUtcTicks = nowTicks };
                    continue;
                }
                if (meta.CreatedUtcTicks <= 0) { meta.CreatedUtcTicks = nowTicks; continue; }
                if (meta.CreatedUtcTicks < cutoff) dead.Add(u);
            }
            foreach (string u in dead) { g.PendingInvites.Remove(u); g.InviteMeta?.Remove(u); }
            // Metadata for someone no longer invited is an orphan whichever way it got there.
            if (g.InviteMeta != null)
            {
                var orphans = new List<string>();
                foreach (KeyValuePair<string, GuildInviteMetaDto> kv in g.InviteMeta)
                    if (!g.PendingInvites.Exists(u => string.Equals(u, kv.Key, StringComparison.OrdinalIgnoreCase)))
                        orphans.Add(kv.Key);
                foreach (string k in orphans) g.InviteMeta.Remove(k);
            }
            return dead.Count;
        }

        public static int PruneExpiredInvites()
        {
            int removed = 0;
            long now = DateTime.UtcNow.Ticks;
            lock (_lock) foreach (GuildSnapshot g in _guilds.Values) removed += PruneInvitesLocked(g, now);
            if (removed > 0) { SaveToDisk(); Diagnostics.ServerLog.Info($"Guilds: {removed} invite(s) expired after {InviteLifetimeDays} days."); }
            return removed;
        }

        public static double WorkerXpMultiplierFor(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 1.0;
            lock (_lock) { return _guilds.TryGetValue(guildName, out GuildSnapshot g) ? g.Perks.WorkerXpMultiplier : 1.0; }
        }

        public static double CustomSiteCostMultiplierFor(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 1.0;
            lock (_lock) { return _guilds.TryGetValue(guildName, out GuildSnapshot g) ? g.Perks.CustomSiteCostMultiplier : 1.0; }
        }

        // The guild and the cut its site-reward tax takes from this gross, moving nothing: the caller writes both legs
        // down together, so neither can land without the other.
        public static (string Guild, int Tax) SiteRewardTaxFor(string username, int silverGross)
        {
            if (silverGross <= 0 || string.IsNullOrEmpty(username)) return ("", 0);
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(username, out string guildName)) return ("", 0);
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return ("", 0);
                int pct = Math.Max(0, Math.Min(50, g.Settings.SiteRewardSilverTaxPercent));
                int tax = (int)Math.Floor(silverGross * (pct / 100.0));
                return tax > 0 ? (guildName, tax) : ("", 0);
            }
        }

        public static GuildSnapshotEnvelope BuildEnvelopeFor(string username)
        {
            GuildSnapshotEnvelope env = new GuildSnapshotEnvelope();
            if (string.IsNullOrEmpty(username)) return env;
            lock (_lock)
            {
                // Stamped before either exit: the guildless reply is a snapshot too, and a late one must not overwrite a newer push.
                env.Revision = Util.KmhSnapshotRevision.Next();
                if (!_userToGuild.TryGetValue(username, out string gname) || !_guilds.TryGetValue(gname, out GuildSnapshot g))
                {
                    env.MyInvites = InvitesForLocked(username);
                    return env;
                }
                env.InGuild = true;
                env.Guild   = CopyLocked(g);
                DiplomacyViewLocked(g, env.Guild);
            }
            return env;
        }

        // A proposal lives only on the proposer's record, so the receiver's view is built from the other guilds here.
        private static void DiplomacyViewLocked(GuildSnapshot g, GuildSnapshot view)
        {
            foreach (GuildSnapshot other in _guilds.Values)
            {
                if (other == null || ReferenceEquals(other, g)) continue;
                string theirs = RelationLocked(other, g.Name);
                string ours = RelationLocked(g, other.Name);
                if (theirs == GuildSnapshot.RelationAlliedRequested && ours != GuildSnapshot.RelationAllied)
                    view.IncomingProposals.Add(other.Name);
                // An alliance an older build ended on one side only is not one.
                if (ours == GuildSnapshot.RelationAllied && theirs != GuildSnapshot.RelationAllied)
                    ForgetNameLocked(view, other.Name);
            }
            view.IncomingProposals.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private static string RelationLocked(GuildSnapshot g, string other)
        {
            if (g?.Relationships == null || string.IsNullOrEmpty(other)) return GuildSnapshot.RelationNone;
            foreach (KeyValuePair<string, string> kv in g.Relationships)
                if (string.Equals(kv.Key, other, StringComparison.OrdinalIgnoreCase)) return kv.Value ?? GuildSnapshot.RelationNone;
            return GuildSnapshot.RelationNone;
        }

        public static bool Promote(string actor, string target) => MutateMemberRank(actor, target, promote: true, out _);

        public static bool Demote(string actor, string target) => MutateMemberRank(actor, target, promote: false, out _);

        public static bool Promote(string actor, string target, out string reason) => MutateMemberRank(actor, target, promote: true, out reason);

        public static bool Demote(string actor, string target, out string reason) => MutateMemberRank(actor, target, promote: false, out reason);

        private const string NotSaved = "The server could not save that change - nothing changed. Try again shortly.";

        public static bool Kick(string actor, string target) => Kick(actor, target, out _);

        public static bool Kick(string actor, string target, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(target)) { reason = "Name a member to kick."; return false; }
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase)) { reason = "You cannot kick yourself - leave the guild instead."; return false; }
            bool ok = false;
            string guildName = null;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) { reason = "You are not in a guild."; return false; }
                if (!_userToGuild.TryGetValue(target, out string tGuild)
                    || !string.Equals(aGuild, tGuild, StringComparison.OrdinalIgnoreCase)) { reason = $"'{target}' is not in your guild."; return false; }
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) { reason = "Your guild record is missing."; return false; }

                if (!GuildPermissions.CanActOnMember(FindRankLocked(g, actor), FindRankLocked(g, target)))
                { reason = $"Your rank cannot kick {target}: only a Moderator or above can kick, and only lower ranks."; return false; }

                g.Members.RemoveAll(m => string.Equals(m.Username, target, StringComparison.OrdinalIgnoreCase));
                _userToGuild.Remove(target);
                // So a later re-invite starts clean.
                g.PendingInvites?.RemoveAll(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase));
                g.InviteMeta?.Remove(target);
                guildName = aGuild;
                ok = CommitAuthorityLocked(before);
                if (!ok) reason = NotSaved;
            }
            if (ok) RaiseChanged(guildName, "member_kicked", target);
            return ok;
        }

        // Owner and Admins: who a guild's diplomacy and ownership notices go to.
        public static List<string> LeadersOf(string guildName)
        {
            var outp = new List<string>();
            if (string.IsNullOrEmpty(guildName)) return outp;
            lock (_lock)
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g) && g?.Members != null)
                    foreach (GuildMemberDto m in g.Members)
                        if (m != null && !string.IsNullOrEmpty(m.Username) && RankOrder(m.Rank) <= RankOrder(GuildMemberDto.RankAdmin))
                            outp.Add(m.Username);
            return outp;
        }

        public static List<string> MembersOf(string guildName)
        {
            var outp = new List<string>();
            if (string.IsNullOrEmpty(guildName)) return outp;
            lock (_lock)
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g) && g?.Members != null)
                    foreach (GuildMemberDto m in g.Members) if (!string.IsNullOrEmpty(m?.Username)) outp.Add(m.Username);
            return outp;
        }

        public static string CanonicalName(string guildName)
        {
            if (string.IsNullOrWhiteSpace(guildName)) return null;
            lock (_lock) return _guilds.TryGetValue(guildName.Trim(), out GuildSnapshot g) ? g.Name : null;
        }

        public static bool InspectGuild(string name, out System.Collections.Generic.List<string> lines)
        {
            lines = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(name)) return false;
            long vault = Treasury.TreasuryStore.GetGuildSilver(name);
            (Dictionary<string, int> goods, List<Items.KmhThingPayload> loads) = Treasury.TreasuryStore.GuildGoodsFor(name);
            long units = 0;
            foreach (int n in goods.Values) units += n;
            foreach (Items.KmhThingPayload p in loads) units += p?.StackCount ?? 0;
            lock (_lock)
            {
                if (!_guilds.TryGetValue(name, out GuildSnapshot g) || g == null) return false;
                bool tEnabled = !string.Equals(Economy.EconomyAccess.Policy.GuildAccess, "Disabled", StringComparison.OrdinalIgnoreCase);
                lines.Add($"=== Guild '{g.Name}' ===");
                lines.Add($"  join: {(g.OpenJoin ? "OPEN (anyone can join)" : "invite-only")}");
                lines.Add($"  treasury: {(tEnabled ? "enabled" : "DISABLED by server (GuildTreasuryAccessMode)")}  vault {Util.SilverFmt.Format(vault)} silver"
                        + $", {goods.Count + loads.Count} item kind(s) ({units:N0} unit(s))");
                lines.Add($"  hall: {(g.Hall != null && g.Hall.HasHall ? $"tile {g.Hall.Tile} (radius {g.Hall.RadiusTiles}, set by {g.Hall.Leader})" : "not set (optional unless server requires it)")}");
                lines.Add($"  perks: workers Lv{g.Perks.SiteMaxWorkersBonusLevel} | tax Lv{g.Perks.MarketplaceTaxReductionLevel} | xp Lv{g.Perks.WorkerXpBonusLevel} | siteCost Lv{g.Perks.CustomSiteCostDiscountLevel}");
                lines.Add($"  members ({g.Members.Count}):");
                foreach (GuildMemberDto m in g.Members)
                    lines.Add($"    {m.Username} [{m.Rank}] donated {Util.SilverFmt.Format(m.SilverContributed)}");
                if (g.PendingInvites != null && g.PendingInvites.Count > 0)
                    lines.Add($"  pending invites: {string.Join(", ", g.PendingInvites)}");
                System.Collections.Generic.List<string> problems = new System.Collections.Generic.List<string>();
                foreach (GuildMemberDto m in g.Members)
                {
                    if (string.IsNullOrEmpty(m.Username)) { problems.Add("member with empty username"); continue; }
                    if (!_userToGuild.TryGetValue(m.Username, out string ug) || !string.Equals(ug, g.Name, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"'{m.Username}' Members/reverse-map mismatch (run 'kmh guild repair')");
                }
                if (g.Members.Count > 0 && !g.Members.Exists(m => string.Equals(m.Rank, GuildMemberDto.RankOwner, StringComparison.OrdinalIgnoreCase)))
                    problems.Add("NO Owner (repaired automatically on next boot, or run 'kmh guild repair')");
                if (problems.Count == 0) lines.Add("  problems: none");
                else foreach (string p in problems) lines.Add($"  [PROBLEM] {p}");
            }
            return true;
        }

        // The per-guild Members lists are authoritative, so the reverse map is rebuilt from them.
        public static int RepairMembership(out System.Collections.Generic.List<string> notes)
        {
            notes = new System.Collections.Generic.List<string>();
            int changed = 0;
            lock (_lock)
            {
                foreach (KeyValuePair<string, GuildSnapshot> okv in _guilds) RepairOwnerLocked(okv.Value);
                var rebuilt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, GuildSnapshot> kv in _guilds)
                {
                    if (kv.Value?.Members == null) continue;
                    foreach (Dto.GuildMemberDto m in kv.Value.Members)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Username)) continue;
                        if (rebuilt.TryGetValue(m.Username, out string other) && !string.Equals(other, kv.Key, StringComparison.OrdinalIgnoreCase))
                        { notes.Add($"'{m.Username}' is listed by both '{other}' and '{kv.Key}' - kept '{other}'"); continue; }
                        rebuilt[m.Username] = kv.Key;
                    }
                }
                foreach (KeyValuePair<string, string> kv in _userToGuild)
                    if (!rebuilt.ContainsKey(kv.Key)) { notes.Add($"cleared ghost: '{kv.Key}' -> '{kv.Value}' (not a member of any guild)"); changed++; }
                foreach (KeyValuePair<string, string> kv in rebuilt)
                    if (!_userToGuild.TryGetValue(kv.Key, out string cur) || !string.Equals(cur, kv.Value, StringComparison.OrdinalIgnoreCase))
                    { notes.Add($"fixed membership: '{kv.Key}' -> '{kv.Value}'"); changed++; }
                if (changed > 0) { _userToGuild.Clear(); foreach (KeyValuePair<string, string> kv in rebuilt) _userToGuild[kv.Key] = kv.Value; }
            }
            if (changed > 0) SaveToDisk();
            return changed;
        }

        // Priced under the lock and charged outside it, so a lost race refunds rather than burning silver.
        public static bool BuyPerk(string actor, string perkKey, out string reason, out int newLevel, out int cost)
        {
            reason = null; newLevel = 0; cost = 0;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(perkKey)) { reason = "Unknown perk."; return false; }
            if (VaultDisabled(out reason)) return false;

            string guildName, identity;
            int pricedLevel = 0;   // the level this purchase is PRICED for; re-checked under the bump lock below
            long ticket;
            lock (_lock)
            {
                ticket = AuthorityTicketLocked();
                if (!_userToGuild.TryGetValue(actor, out string aGuild) || !_guilds.TryGetValue(aGuild, out GuildSnapshot g))
                { reason = "You are not in a guild."; return false; }
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                { reason = "Only the guild admin can buy perks."; return false; }

                int current = CurrentPerkLevelLocked(g.Perks, perkKey);
                if (current < 0)                       { reason = "Unknown perk."; return false; }
                if (current >= GuildPerksDto.MaxLevel) { reason = $"{PerkLabel(perkKey)} is already maxed (Lv {GuildPerksDto.MaxLevel})."; return false; }
                cost        = GuildPerksDto.CostFor(current);
                pricedLevel = current;
                guildName   = g.Name;
                // Pinned with the price: the charge and any refund follow this incarnation, never the name.
                identity    = GuildIdentity.Of(guildName);
            }
            if (string.IsNullOrEmpty(identity)) { reason = "Your guild changed while that was processing - nothing was charged."; return false; }

            // Written down BEFORE the vault is charged, so boot can tell a charge that bought the perk from one to refund.
            var intent = new PendingPerk
            {
                Marker = $"perk:{identity}:{perkKey}:{pricedLevel}:{Guid.NewGuid():N}",
                Guild = guildName, Identity = identity, Perk = perkKey,
                PricedLevel = pricedLevel, Cost = cost, Actor = actor,
            };
            lock (_lock)
            {
                _pendingPerks[intent.Marker] = intent;
                if (!SaveToDisk())
                {
                    _pendingPerks.Remove(intent.Marker);
                    reason = "The server couldn't record that purchase - nothing was charged. Try again shortly.";
                    return false;
                }
            }
            AtPerkPhaseForTest?.Invoke("recorded");
            if (!Treasury.TreasuryStore.WithdrawGuildSilverOnce(guildName, intent.Marker, cost, actor,
                                                                note: $"guild perk '{perkKey}' purchase", identity: identity))
            {
                lock (_lock) _pendingPerks.Remove(intent.Marker);
                SaveToDisk();
                reason = GuildIdentity.IsLive(guildName, identity)
                    ? $"The guild vault can't afford {PerkLabel(perkKey)} - {Util.SilverFmt.Format(cost)} needed. Members contribute with 'kmh guild deposit'."
                    : "Your guild changed while that was processing - nothing was charged.";
                return false;
            }
            AtPerkPhaseForTest?.Invoke("charged");
            if (!FinishPerk(intent, ticket, out reason, out newLevel)) return false;
            RaiseChanged(guildName, "perk", actor);
            return true;
        }

        internal static Action<string> AtPerkPhaseForTest;

        // A perk purchase between its record and its outcome. Server-only, saved with the guilds.
        internal sealed class PendingPerk
        {
            [Newtonsoft.Json.JsonProperty("marker")]   public string Marker { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("guild")]    public string Guild { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("guild_id")] public string Identity { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("perk")]     public string Perk { get; set; } = "";
            [Newtonsoft.Json.JsonProperty("level")]    public int PricedLevel { get; set; }
            [Newtonsoft.Json.JsonProperty("cost")]     public int Cost { get; set; }
            [Newtonsoft.Json.JsonProperty("actor")]    public string Actor { get; set; } = "";
        }

        private static readonly Dictionary<string, PendingPerk> _pendingPerks = new Dictionary<string, PendingPerk>(StringComparer.Ordinal);

        // Charged: applies what it bought if the guild is still at the priced level, else refunds on the purchase's own
        // marker - then the record goes. A null ticket is boot, where no caller's rank is in play.
        private static bool FinishPerk(PendingPerk p, long? ticket, out string reason, out int newLevel)
        {
            reason = null; newLevel = 0;
            bool bumped = false;
            lock (_lock)
            {
                if (!_pendingPerks.ContainsKey(p.Marker)) { reason = "That purchase was already settled."; return false; }
                // The vault charge released the lock, so re-check both rank and priced level or two buys jump two levels for one price.
                if ((ticket == null || AuthorityTicketLocked() == ticket.Value)
                    && string.Equals(GuildIdentity.Of(p.Guild), p.Identity, StringComparison.Ordinal)
                    && _guilds.TryGetValue(p.Guild, out GuildSnapshot g)
                    && CurrentPerkLevelLocked(g.Perks, p.Perk) == p.PricedLevel)
                {
                    bumped = BumpPerkLocked(g.Perks, p.Perk);
                    if (bumped) { newLevel = CurrentPerkLevelLocked(g.Perks, p.Perk); _pendingPerks.Remove(p.Marker); }
                }
            }
            if (bumped)
            {
                // Not undone if this write fails: the record on disk still names the charge, so boot applies it again.
                if (!SaveToDisk()) Diagnostics.ServerLog.Warn($"Guild '{p.Guild}': perk '{p.Perk}' was bought but could not be saved yet - boot re-applies it from its purchase record.");
                return true;
            }
            string refund = p.Marker + ":refund";
            bool live = GuildIdentity.IsLive(p.Guild, p.Identity);
            bool back = live
                ? Treasury.TreasuryStore.DepositGuildSilverOnce(p.Guild, refund, p.Cost, p.Actor, note: $"guild perk '{p.Perk}' purchase refund", identity: p.Identity)
                // The guild is gone and its vault already handed out: the silver was the guild's, not the buyer's, so it
                // is held once for an admin to settle ('kmh recover') rather than paid to whoever pressed buy.
                : Recovery.RecoveryStore.HoldSilverOnce(p.Actor, p.Cost, refund,
                      $"guild perk '{p.Perk}' bought for guild '{p.Guild}', which disbanded before the perk could be applied") > 0;
            if (!back)
            {
                reason = "The perk could not be applied and its refund could not be written yet - it is retried at the next boot.";
                return false;
            }
            lock (_lock) _pendingPerks.Remove(p.Marker);
            SaveToDisk();
            reason = live ? "Could not apply the perk (a concurrent change won) - the guild vault was refunded."
                          : "Your guild disbanded before the perk could be applied - the silver is held for a server admin to settle.";
            return false;
        }

        // Boot: a purchase recorded and then interrupted. Its marker in the guild vault says it was charged, and it is then
        // applied or refunded exactly as the live path would; never charged, the record is simply dropped.
        public static void RecoverPendingPerks()
        {
            List<PendingPerk> rows;
            lock (_lock) rows = new List<PendingPerk>(_pendingPerks.Values);
            if (rows.Count == 0) return;
            int applied = 0, dropped = 0;
            foreach (PendingPerk p in rows)
            {
                if (!Treasury.TreasuryStore.KnowsGuildTxnMarker(p.Guild, p.Marker)) { lock (_lock) _pendingPerks.Remove(p.Marker); dropped++; continue; }
                if (FinishPerk(p, null, out _, out _)) applied++;
            }
            SaveToDisk();
            Diagnostics.ServerLog.Warn($"Guilds: {rows.Count} interrupted perk purchase(s) - {applied} applied, {dropped} never charged, the rest refunded or retried.");
        }

        internal static int PendingPerkCountForTest { get { lock (_lock) return _pendingPerks.Count; } }
        internal static int PerkLevelForTest(string guild, string perk) { lock (_lock) return _guilds.TryGetValue(guild ?? "", out GuildSnapshot g) ? CurrentPerkLevelLocked(g.Perks, perk) : -1; }

        // Must match the client's Guild Hall perk rows.
        public static string PerkLabel(string perkKey)
        {
            switch (perkKey)
            {
                case "site_max_workers":     return "Site Max Workers";
                case "marketplace_tax_cut":  return "Market Tax Cut";
                case "worker_xp_bonus":      return "Worker XP Bonus";
                case "custom_site_cost_cut": return "Custom Site Cost";
                default:                     return perkKey;
            }
        }

        public static int PerkCostForLevel(int currentLevel) => GuildPerksDto.CostFor(currentLevel);

        // Must accept exactly the keys BumpPerkLocked does; -1 for an unknown key.
        private static int CurrentPerkLevelLocked(GuildPerksDto p, string perkKey)
        {
            switch (perkKey)
            {
                case "site_max_workers":    return p.SiteMaxWorkersBonusLevel;
                case "marketplace_tax_cut": return p.MarketplaceTaxReductionLevel;
                case "worker_xp_bonus":     return p.WorkerXpBonusLevel;
                case "custom_site_cost_cut":return p.CustomSiteCostDiscountLevel;
                default:                    return -1;
            }
        }

        public static bool DepositToGuild(string actor, int amount, out string errorReason, Economy.EconomyContext ctx = default, string txnId = null)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor)) { errorReason = "Caller username is empty."; return false; }
            if (amount <= 0)                 { errorReason = "Amount must be positive.";   return false; }
            if (VaultDisabled(out errorReason)) return false;

            string guildName, identity;
            lock (_lock) { _userToGuild.TryGetValue(actor, out guildName); identity = GuildIdentity.Of(guildName); }
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(identity)) { errorReason = "You are not in a guild."; return false; }

            // An empty ctx comes from a chat command, which makes proximity checks permissive.
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: true, isGuild: false, isItem: false, ctx, out errorReason)) return false;
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: false, isGuild: true, isItem: false, ctx, out errorReason)) return false;
            // Counts in-flight donations too, so their commits cannot overshoot the vault cap.
            long vaultPlusPending = Treasury.TreasuryStore.GetGuildSilver(guildName)
                                  + Treasury.TreasuryStore.PendingGuildDonationTotal(guildName);
            if (Economy.EconomyAccess.WouldExceedCap(true, vaultPlusPending, amount, out errorReason)) return false;
            GuildSnapshot gForHall; lock (_lock) { _guilds.TryGetValue(guildName, out gForHall); }
            if (!GuildHallRules.CanAccessTreasury(gForHall, out errorReason)) return false;
            if (!GuildHallRules.CanContribute(gForHall, ctx, out errorReason)) return false;

            // The guild is credited only once the donor's save confirms; a revert refunds them.
            if (string.IsNullOrEmpty(txnId)) txnId = Guid.NewGuid().ToString("N");
            AfterDonationCheckForTest?.Invoke(guildName);
            // Booked against the incarnation checked above: a same-named guild formed in between is somebody else.
            if (!Treasury.TreasuryStore.BeginPendingGuildDonation(actor, guildName, amount, txnId, out errorReason, identity))
                return false;
            Economy.EconomyAccess.Audit(actor, false, true, amount);
            RaiseChanged(guildName, "treasury", actor);
            return true;
        }

        // Bookkeeping after the treasury commit credited that incarnation's vault. A guild that disbanded since took the
        // silver out with its vault hand-back, so nothing is taken back from whatever vault holds the name now.
        public static void FinalizeDonation(string actor, string guildName, int amount, string identity)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(guildName) || amount <= 0) return;
            bool exists;
            lock (_lock)
            {
                exists = _guilds.ContainsKey(guildName) && GuildIdentity.IsLive(guildName, identity ?? "");
                if (exists && _guilds.TryGetValue(guildName, out GuildSnapshot g))
                {
                    GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                    if (m != null) m.SilverContributed += amount;   // cached summary; the ledger below is authoritative

                    // Audit only: the donation already committed above.
                    string txId = RecordCompletedDonationTx(actor, guildName, amount);
                    Contributions.KmhGuildContributionLedger.RecordSilver(guildName, actor, amount, txId);
                }
            }
            if (!exists)
            {
                Diagnostics.ServerLog.Warn($"Guild: donation of {amount}s from {actor} was confirmed into guild '{guildName}', which disbanded "
                    + "before its bookkeeping ran - the silver left with that guild's vault hand-back.");
                return;
            }
            // Only a guild contribution counts as a donation; a deposit into your own vault does not.
            PlayerStats.PlayerStatsStore.AddSilverDonated(actor, amount);
            SaveToDisk();
            Diagnostics.ServerLog.Info($"Guild: donation of {Util.SilverFmt.Format(amount)} from {actor} to '{guildName}' save-confirmed.");
            RaiseChanged(guildName, "treasury", actor);
        }

        // The owner's kill switch lives here, not in one handler, so every way into the vault honours it.
        private static bool VaultDisabled(out string reason)
        {
            reason = string.Equals(Economy.EconomyAccess.Policy.GuildAccess, "Disabled", StringComparison.OrdinalIgnoreCase)
                ? "The guild vault is disabled on this server." : null;
            return reason != null;
        }

        // The outbound mirror of a donation's checks: guild side out, personal side in, and the hall rule.
        private static bool VaultReachable(string actor, Economy.EconomyContext ctx, out string reason)
        {
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: true, isGuild: true, isItem: false, ctx, out reason)) return false;
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: false, isGuild: false, isItem: false, ctx, out reason)) return false;
            string guild = CurrentGuildOf(actor);
            GuildSnapshot g = null;
            if (!string.IsNullOrEmpty(guild)) lock (_lock) _guilds.TryGetValue(guild, out g);
            return GuildHallRules.CanAccessTreasury(g, out reason);
        }

        public static bool WithdrawFromGuild(string actor, int amount, out string errorReason, Economy.EconomyContext ctx = default)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor)) { errorReason = "Caller username is empty."; return false; }
            if (amount <= 0)                 { errorReason = "Amount must be positive.";   return false; }
            if (VaultDisabled(out errorReason)) return false;
            if (!VaultReachable(actor, ctx, out errorReason)) return false;

            string guildName;
            bool reservedTally = false; long reservedDayStart = 0, ticket;
            lock (_lock)
            {
                ticket = AuthorityTicketLocked();
                if (!_userToGuild.TryGetValue(actor, out guildName)) { errorReason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) { errorReason = "Your guild record is missing."; return false; }
                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                if (m == null) { errorReason = "Member record not found."; return false; }

                // Per-rank daily cap: -1 unlimited, 0 not allowed, >0 a limit.
                int cap = DailyCapFor(g.Settings, m.Rank);
                if (cap == 0) { errorReason = "Your rank can't withdraw from the vault (an Admin sets per-rank caps)."; return false; }
                if (cap > 0)
                {
                    if (!TryReserveTallyLocked(guildName, actor, m, cap, amount, out long already, out long dayStart))
                    {
                        errorReason = $"Daily withdraw cap is {Util.SilverFmt.Format(cap)}; you've taken {Util.SilverFmt.Format(already)} today.";
                        return false;
                    }
                    reservedTally = true; reservedDayStart = dayStart;
                }
            }

            // The tally reaches disk before the silver moves, or a restart hands back a fresh daily cap on top of what was taken.
            if (reservedTally && !SaveToDisk())
            {
                ReleaseWithdrawTally(guildName, actor, amount, reservedDayStart);
                errorReason = "The server couldn't record that withdrawal - the vault was untouched. Try again shortly.";
                return false;
            }
            // The vault's debit and the member's credit are one treasury commit, made while the guild lock keeps the
            // authority checked above current: no mid-flight rank change to undo, and no moment the silver is with nobody.
            bool moved, authorized;
            lock (_lock)
            {
                authorized = AuthorityTicketLocked() == ticket;
                moved = authorized && Treasury.TreasuryStore.TransferGuildToMember(guildName, GuildIdentity.Of(guildName), actor, amount,
                                                                                  $"withdraw from guild '{guildName}' to {actor}");
            }
            if (!moved)
            {
                // A refused attempt must not burn the daily cap.
                if (reservedTally) { ReleaseWithdrawTally(guildName, actor, amount, reservedDayStart); SaveToDisk(); }
                errorReason = authorized ? "The guild vault doesn't have that much silver."
                                         : "Your guild membership changed while that was processing - nothing was taken.";
                return false;
            }
            // Netted back down (floored at 0), or contribute-and-withdraw cycling would inflate the donation stat.
            PlayerStats.PlayerStatsStore.AddSilverDonated(actor, -amount);
            RaiseChanged(guildName, "treasury", actor);
            return true;
        }

        internal enum ItemWithdrawOutcome { Delivered, Refused, Unrecorded, Compensated }

        // Between the checks (rank, cap, incarnation, what will leave) and the take.
        internal static Action<string> AfterItemWithdrawCheckForTest;
        // After the take, before the delivery is written: where a crash leaves goods named only by the row.
        internal static Action<string> AtItemWithdrawPhaseForTest;

        private const string RankCannotMoveGoods =
            "Your rank cannot take anything out of the guild vault (an Admin sets the per-rank caps).";

        // Same rank rule and daily cap as silver (goods at trusted value), bound to the incarnation checked here; the row
        // is written first, the take carries its marker, and recovery gives an undelivered take back to the guild.
        public static ItemWithdrawOutcome WithdrawItemFromGuild(string actor, string itemKey, string fingerprint, int qty,
                                                                Economy.EconomyContext ctx, out string reason,
                                                                out Delivery.OutboundDelivery delivery)
        {
            reason = null; delivery = null;
            bool byPayload = !string.IsNullOrEmpty(fingerprint);
            if (string.IsNullOrEmpty(actor)) { reason = "Caller username is empty."; return ItemWithdrawOutcome.Refused; }
            if (qty <= 0) { reason = "Quantity must be positive."; return ItemWithdrawOutcome.Refused; }
            if (!byPayload && string.IsNullOrEmpty(itemKey)) { reason = "That names no item."; return ItemWithdrawOutcome.Refused; }
            if (VaultDisabled(out reason)) return ItemWithdrawOutcome.Refused;
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: true, isGuild: true, isItem: true, ctx, out reason))
                return ItemWithdrawOutcome.Refused;

            string guildName, identity;
            Dictionary<string, int> legs = null;
            List<Items.KmhThingPayload> planned = null;
            bool reservedTally = false; long reservedDayStart = 0, value = 0, ticket;
            lock (_lock)
            {
                ticket = AuthorityTicketLocked();
                if (!_userToGuild.TryGetValue(actor, out guildName) || !_guilds.TryGetValue(guildName, out GuildSnapshot g))
                { reason = "You are not in a guild."; return ItemWithdrawOutcome.Refused; }
                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                if (m == null) { reason = "Member record not found."; return ItemWithdrawOutcome.Refused; }
                int cap = DailyCapFor(g.Settings, m.Rank);
                if (cap == 0) { reason = RankCannotMoveGoods; return ItemWithdrawOutcome.Refused; }
                if (!GuildHallRules.CanAccessTreasury(g, out reason)) return ItemWithdrawOutcome.Refused;
                identity = GuildIdentity.Of(guildName);
                if (string.IsNullOrEmpty(identity)) { reason = "Your guild changed while that was processing - nothing was taken."; return ItemWithdrawOutcome.Refused; }

                // Priced now, from exactly what the row will name, so the cap is charged for the goods that leave.
                if (byPayload)
                {
                    planned = Treasury.TreasuryStore.PlanGuildPayloadTake(guildName, fingerprint, qty);
                    if (planned == null || planned.Count == 0)
                    { reason = "Nothing left to withdraw from that row - the guild vault view has been refreshed."; return ItemWithdrawOutcome.Refused; }
                    foreach (Items.KmhThingPayload p in planned) value += UnitValue(p.DefName) * p.StackCount;
                }
                else
                {
                    if (Treasury.TreasuryStore.GuildItemCount(guildName, itemKey) < qty)
                    { reason = "The guild vault doesn't hold that many."; return ItemWithdrawOutcome.Refused; }
                    legs = new Dictionary<string, int>(StringComparer.Ordinal) { { itemKey, qty } };
                    value = UnitValue(itemKey.Split('|')[0]) * qty;
                }

                if (cap > 0)
                {
                    if (!TryReserveTallyLocked(guildName, actor, m, cap, value, out long already, out long dayStart))
                    {
                        reason = $"Daily withdraw cap is {Util.SilverFmt.Format(cap)}; you've taken {Util.SilverFmt.Format(already)} today and these goods are worth {Util.SilverFmt.Format(value)}.";
                        return ItemWithdrawOutcome.Refused;
                    }
                    reservedTally = true; reservedDayStart = dayStart;
                }
            }

            void GiveBackCap()
            {
                if (!reservedTally) return;
                ReleaseWithdrawTally(guildName, actor, value, reservedDayStart);
                SaveToDisk();
            }

            // The tally reaches disk before anything moves, or a restart hands back a fresh daily cap on top of it.
            if (reservedTally && !SaveToDisk())
            {
                ReleaseWithdrawTally(guildName, actor, value, reservedDayStart);
                reason = "The server couldn't record that withdrawal - the vault was untouched. Try again shortly.";
                return ItemWithdrawOutcome.Unrecorded;
            }
            AfterItemWithdrawCheckForTest?.Invoke(guildName);

            string what = byPayload ? $"x{Items.KmhPayloadEscrow.TotalUnits(planned)} full-state item(s)" : $"x{qty} {itemKey}";
            Transactions.KmhTransaction row = Transactions.KmhTransactionRepository.OpenGuildWithdrawal(
                actor, guildName, identity, $"withdraw {what} from guild '{guildName}'", legs, planned);
            if (row == null)
            {
                GiveBackCap();
                reason = "The server couldn't record that withdrawal - nothing was taken.";
                return ItemWithdrawOutcome.Unrecorded;
            }

            // Taken while the guild lock holds the authority checked above still current, from that incarnation only.
            bool authorized, taken = false;
            List<Items.KmhThingPayload> granted = null;
            string note = $"withdraw {what} from guild '{guildName}' by {actor}";
            lock (_lock)
            {
                authorized = AuthorityTicketLocked() == ticket;
                if (authorized && byPayload)
                {
                    granted = Treasury.TreasuryStore.TakeGuildPayloadsForTxn(guildName, identity, fingerprint, qty, planned,
                                                                            row.TakeMarker, actor, note);
                    taken = granted != null;
                }
                else if (authorized)
                    taken = Treasury.TreasuryStore.WithdrawGuildItemForTxn(guildName, identity, itemKey, qty, row.TakeMarker, actor, note);
            }
            if (!taken)
            {
                Transactions.KmhTransactionRepository.Abort(row);
                GiveBackCap();
                reason = !authorized ? "Your guild membership changed while that was processing - nothing was taken."
                       : !GuildIdentity.IsLive(guildName, identity) ? "Your guild changed while that was processing - nothing was taken."
                       : "The guild vault no longer holds that - nothing was taken.";
                return ItemWithdrawOutcome.Refused;
            }
            AtItemWithdrawPhaseForTest?.Invoke("taken");

            delivery = Delivery.DeliveryStore.OweForWithdrawal(row, 0, legs, granted);
            if (delivery == null)
            {
                GiveBackCap();
                reason = "The server couldn't hand that over - the goods are back in the guild vault.";
                return ItemWithdrawOutcome.Compensated;
            }
            Diagnostics.ServerLog.Info($"Guild: {actor} withdrew {what} from '{guildName}' - delivery {delivery.Id} owed.");
            RaiseChanged(guildName, "treasury", actor);
            return ItemWithdrawOutcome.Delivered;
        }

        // At least one silver a unit, so goods the catalog has no price for still count against a capped rank.
        private static long UnitValue(string defName) => Math.Max(1L, Items.KmhItemSafety.GetTrustedMarketValue(defName ?? ""));

        // Same authority, cap and mid-flight membership check as WithdrawFromGuild, or routing via Global makes the cap
        // advisory; idempotent on the marker, so a retry after a crash takes it once.
        public static bool EscrowForFederation(string actor, int amount, string marker, out string guildName,
                                               out string errorReason)
        {
            guildName = null;
            errorReason = null;
            if (string.IsNullOrEmpty(actor))  { errorReason = "Caller username is empty."; return false; }
            if (amount <= 0)                  { errorReason = "Amount must be positive.";  return false; }
            if (string.IsNullOrEmpty(marker)) { errorReason = "No operation to charge it to."; return false; }

            bool reservedTally = false; long reservedDayStart = 0, ticket;
            string identity;
            lock (_lock)
            {
                ticket = AuthorityTicketLocked();
                if (!_userToGuild.TryGetValue(actor, out guildName)) { errorReason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) { errorReason = "Your guild record is missing."; return false; }
                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                if (m == null) { errorReason = "Member record not found."; return false; }
                identity = GuildIdentity.Of(guildName) ?? "";

                // A replay must not be charged to the cap twice, so a committed marker short-circuits before any reservation.
                if (Treasury.TreasuryStore.KnowsGuildTxnMarker(guildName, marker)) return true;

                // After the replay check: an escrow that already left the vault must not be refused on retry.
                // With no client context the guild access mode refuses only "Disabled", which VaultDisabled is.
                if (VaultDisabled(out errorReason)) return false;
                if (!GuildHallRules.CanAccessTreasury(g, out errorReason)) return false;

                int cap = DailyCapFor(g.Settings, m.Rank);
                if (cap == 0) { errorReason = RankCannotMoveSilver; return false; }
                if (cap > 0)
                {
                    if (!TryReserveTallyLocked(guildName, actor, m, cap, amount, out long already, out long dayStart))
                    {
                        errorReason = $"Daily withdraw cap is {Util.SilverFmt.Format(cap)}; {Util.SilverFmt.Format(already)} has been taken today.";
                        return false;
                    }
                    reservedTally = true; reservedDayStart = dayStart;
                }
            }

            if (!Treasury.TreasuryStore.WithdrawGuildSilverOnce(guildName, marker, amount, actor,
                                                                note: $"connected treasury escrow by {actor}", identity: identity))
            {
                if (reservedTally) ReleaseWithdrawTally(guildName, actor, amount, reservedDayStart);
                errorReason = "The guild vault doesn't have that much silver.";
                return false;
            }

            bool stillAuthorized;
            lock (_lock) stillAuthorized = AuthorityTicketLocked() == ticket;
            if (!stillAuthorized)
            {
                if (reservedTally) ReleaseWithdrawTally(guildName, actor, amount, reservedDayStart);
                Treasury.TreasuryStore.ReturnGuildSilver(guildName, amount, actor,
                                                         $"connected escrow by {actor} - membership changed mid-operation");
                errorReason = "Your guild membership changed while that was processing - nothing was taken.";
                return false;
            }
            // The tally reaches disk before the operation continues, or a restart hands back a fresh daily cap.
            if (!SaveToDisk())
            {
                if (reservedTally) ReleaseWithdrawTally(guildName, actor, amount, reservedDayStart);
                Treasury.TreasuryStore.ReturnGuildSilver(guildName, amount, actor,
                                                         $"connected escrow by {actor} could not be recorded");
                errorReason = "The server couldn't record that - the vault was untouched. Try again shortly.";
                return false;
            }
            RaiseChanged(guildName, "treasury", actor);
            return true;
        }

        // Idempotent on its own marker, so a retried refund of an abandoned federated operation cannot pay twice.
        public static bool RefundFederationEscrow(string guildName, int amount, string actor, string marker)
        {
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
            bool replay = !string.IsNullOrEmpty(marker) && Treasury.TreasuryStore.KnowsGuildTxnMarker(guildName, marker);
            bool ok = Treasury.TreasuryStore.DepositGuildSilverOnce(guildName, marker, amount, actor,
                                                                     note: $"connected treasury refund for {actor}");
            // The escrow counted against the officer's daily cap; returning it gives that back, once.
            if (ok && !replay) ReleaseWithdrawTally(guildName, actor, amount, DateTime.UtcNow.Date.Ticks);
            if (ok) RaiseChanged(guildName, "treasury", actor);
            return ok;
        }

        // No cap applies to value coming in; the marker makes a replayed settlement credit the vault once.
        public static bool CreditFromFederation(string guildName, int amount, string actor, string marker)
        {
            if (string.IsNullOrEmpty(guildName) || amount <= 0) return false;
            // A vault conjured for a guild that no longer exists is an orphan nobody can ever spend.
            lock (_lock) if (!_guilds.ContainsKey(guildName)) return false;
            bool ok = Treasury.TreasuryStore.DepositGuildSilverOnce(guildName, marker, amount, actor,
                                                                     note: $"connected treasury withdrawal by {actor}");
            if (ok) RaiseChanged(guildName, "treasury", actor);
            return ok;
        }

        public static bool GuildExists(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return false;
            lock (_lock) return _guilds.ContainsKey(guildName);
        }

        private const string RankCannotMoveSilver =
            "Your rank cannot move silver out of the vault (an Admin sets the per-rank caps).";

        // Only explains a refusal up front; it never replaces the check each withdrawal makes.
        public static bool CanMoveGuildSilver(string actor, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(actor)) { reason = "Caller username is empty."; return false; }
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname)) { reason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g)) { reason = "Your guild record is missing."; return false; }
                if (DailyCapFor(g.Settings, FindRankLocked(g, actor)) == 0) { reason = RankCannotMoveSilver; return false; }
            }
            return true;
        }

        // Federating changes how the guild works, so it sits at the same bar as its other settings.
        public static bool CanAdministerFederation(string actor, out string reason) => FederationRank(actor, lead: false, out _, out reason);

        // Leading a federation is the Owner's call, like disbanding; whether this guild leads is Global's answer.
        public static bool CanLeadFederation(string actor, out string reason) => FederationRank(actor, lead: true, out _, out reason);

        // Both bars in one place, with a code beside the words so a client can show the reason in the player's language.
        public static bool FederationRank(string actor, bool lead, out string code, out string reason)
        {
            code = ""; reason = null;
            if (string.IsNullOrEmpty(actor)) { code = "player.unknown"; reason = "Caller username is empty."; return false; }
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname)) { code = "guild.none"; reason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g)) { code = "guild.missing"; reason = "Your guild record is missing."; return false; }
                if (!GuildPermissions.Can(FindRankLocked(g, actor), lead ? GuildCapability.Disband : GuildCapability.SaveSettings))
                {
                    code   = lead ? "rank.lead" : "rank.manage";
                    reason = lead ? "Only the guild Owner can lead the federation."
                                  : "Only a guild Admin or the Owner can manage the guild's federation.";
                    return false;
                }
            }
            return true;
        }

        public static List<string> DescribePerksFor(string actor)
        {
            string guildName = CurrentGuildOf(actor);
            if (string.IsNullOrEmpty(guildName)) return null;

            List<string> lines = new List<string>();
            lock (_lock)
            {
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return lines;
                AppendPerkLine(lines, "site_max_workers",     "Site max workers",      g.Perks.SiteMaxWorkersBonusLevel);
                AppendPerkLine(lines, "marketplace_tax_cut",  "Marketplace tax cut",   g.Perks.MarketplaceTaxReductionLevel);
                AppendPerkLine(lines, "worker_xp_bonus",      "Worker XP bonus",       g.Perks.WorkerXpBonusLevel);
                AppendPerkLine(lines, "custom_site_cost_cut", "Custom site cost cut",  g.Perks.CustomSiteCostDiscountLevel);
            }
            return lines;
        }

        private static void AppendPerkLine(List<string> lines, string key, string label, int level)
        {
            string next = level >= GuildPerksDto.MaxLevel
                ? "MAX"
                : $"next {PerkCostForLevel(level)}s";
            lines.Add($"  {key}  ({label})  lvl {level}/{GuildPerksDto.MaxLevel}  [{next}]");
        }

        public static bool SetMotd(string actor, string motd) => SetMotd(actor, motd, out _);

        public static bool SetMotd(string actor, string motd, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(actor)) { reason = "Caller username is empty."; return false; }
            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) { reason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) { reason = "Your guild record is missing."; return false; }
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                { reason = "Only a guild Admin or the Owner can change the message of the day."; return false; }
                AuthorityPoint before = AuthorityPointLocked();
                g.Motd = Util.KmhSafe.Cap(motd ?? "", MaxMotdLength);
                ok = CommitAuthorityLocked(before);
                if (!ok) reason = NotSaved;
            }
            return ok;
        }

        public static bool SaveSettings(string actor, GuildSettingsDto settings) => SaveSettings(actor, settings, out _);

        public static bool SaveSettings(string actor, GuildSettingsDto settings, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(actor) || settings == null) { reason = "Those settings could not be read."; return false; }
            // The client gates these too, but it cannot be trusted.
            if (settings.SiteRewardSilverTaxPercent < 0 || settings.SiteRewardSilverTaxPercent > 50
                || settings.MarketplaceSaleTaxPercent < 0 || settings.MarketplaceSaleTaxPercent > 50)
            { reason = "Guild taxes must be between 0% and 50%."; return false; }
            if (settings.MemberDailyWithdrawCap < -1 || settings.OfficerDailyWithdrawCap < -1
                || settings.ModeratorDailyWithdrawCap < -1 || settings.AdminDailyWithdrawCap < -1)
            { reason = "A daily withdraw cap is -1 (unlimited), 0 (none) or a positive amount."; return false; }

            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) { reason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) { reason = "Your guild record is missing."; return false; }
                string rank = FindRankLocked(g, actor);
                if (RankOrder(rank) > RankOrder(GuildMemberDto.RankAdmin)) { reason = "Only a guild Admin or the Owner can change guild settings."; return false; }
                // The admin cap binds the admins, so only the owner moves it.
                settings = settings.ShallowClone();
                if (rank != GuildMemberDto.RankOwner)
                {
                    GuildSettingsDto now = g.Settings ?? new GuildSettingsDto();
                    settings.AdminDailyWithdrawCap = now.AdminDailyWithdrawCap;
                    // Nor the ranks below past it: an admin's alt at an unlimited member cap drained what the admin could not.
                    if (LiftsPastAdminCap(now, settings))
                    { reason = $"Only the Owner can raise a rank's daily withdraw cap above the Admin cap ({now.AdminDailyWithdrawCap})."; return false; }
                }
                AuthorityPoint before = AuthorityPointLocked();
                g.Settings = settings;
                ok = CommitAuthorityLocked(before);
                if (!ok) reason = NotSaved;
            }
            return ok;
        }

        // Older builds left these behind when a guild was destroyed; a test puts one back to prove creation clears it.
        internal static void InjectRelationshipForTest(string guild, string other, string state)
        {
            lock (_lock) if (_guilds.TryGetValue(guild, out GuildSnapshot g)) g.Relationships[other] = state;
        }

        internal static bool HasRelationshipForTest(string guild, string other)
        {
            lock (_lock)
                if (_guilds.TryGetValue(guild, out GuildSnapshot g))
                    foreach (string k in g.Relationships.Keys) if (string.Equals(k, other, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void ForgetNameLocked(GuildSnapshot g, string name)
        {
            if (g?.Relationships == null) return;
            foreach (string k in new List<string>(g.Relationships.Keys))
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) g.Relationships.Remove(k);
        }

        // Everything outside this store that names a guild only by its name: donations still pending the donor's
        // save go back to the donor, and the chat history goes. Run when a name is freed and when it is taken.
        private static void ForgetGuildName(string name)
        {
            int refunded = Treasury.TreasuryStore.RevertPendingGuildDonations(name);
            if (refunded > 0) Diagnostics.ServerLog.Info($"Guild '{name}': {refunded} pending donation(s) returned to their donors.");
            Chat.ChatStore.ClearGuildChannel(name);
        }

        public static bool SetRelationship(string actor, string otherGuild, string newState)
            => SetRelationship(actor, otherGuild, newState, out _);

        public static bool SetRelationship(string actor, string otherGuild, string newState, out string reason)
            => SetRelationship(actor, otherGuild, newState, false, out reason, out _);

        internal enum DiplomacyChange { Proposed, Allied, Ended, Withdrawn, Declined, Rival, RivalEndedAlliance, RivalCleared }

        // Consent both ways: accepting needs their open proposal, and ending or answering one clears both records.
        internal static bool SetRelationship(string actor, string otherGuild, string newState, bool clearRival,
                                             out string reason, out DiplomacyChange change)
        {
            reason = null;
            change = DiplomacyChange.Ended;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrWhiteSpace(otherGuild)) { reason = "Pick another guild first."; return false; }
            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) { reason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) { reason = "Your guild record is missing."; return false; }
                if (!GuildPermissions.Can(FindRankLocked(g, actor), GuildCapability.SetRelationship))
                { reason = "Only a guild Admin or the Owner can change diplomacy."; return false; }
                GuildSnapshot og = _guilds.TryGetValue(otherGuild.Trim(), out GuildSnapshot found) ? found : null;
                string other = og?.Name ?? otherGuild.Trim();
                if (string.Equals(other, g.Name, StringComparison.OrdinalIgnoreCase)) { reason = "That is your own guild."; return false; }

                string ours = RelationLocked(g, other), theirs = RelationLocked(og, g.Name);
                bool allied = ours == GuildSnapshot.RelationAllied && theirs == GuildSnapshot.RelationAllied;
                bool oursOpen = ours == GuildSnapshot.RelationAlliedRequested, theirsOpen = theirs == GuildSnapshot.RelationAlliedRequested;
                // A stored proposal or mark naming a free name would pass to whoever registers it.
                bool needsGuild = !clearRival && newState != GuildSnapshot.RelationNone;
                if (needsGuild && og == null)
                {
                    reason = ours != GuildSnapshot.RelationNone ? $"The guild '{other}' no longer exists."
                                                               : $"There is no guild called '{other}' on this server.";
                    return false;
                }

                AuthorityPoint before = AuthorityPointLocked();
                if (clearRival)
                {
                    if (ours != GuildSnapshot.RelationHostile) { reason = $"Your guild does not mark '{other}' as a rival."; return false; }
                    ForgetNameLocked(g, other);
                    change = DiplomacyChange.RivalCleared;
                }
                else if (newState == GuildSnapshot.RelationAllied)
                {
                    if (allied) { reason = $"Your guild and '{og.Name}' are already allied."; return false; }
                    if (!theirsOpen) { reason = $"'{og.Name}' has no alliance proposal open to you - propose one and wait for them to accept."; return false; }
                    SetRelationLocked(g, og.Name, GuildSnapshot.RelationAllied);
                    SetRelationLocked(og, g.Name, GuildSnapshot.RelationAllied);
                    change = DiplomacyChange.Allied;
                }
                else if (newState == GuildSnapshot.RelationNone)
                {
                    bool ends = ours == GuildSnapshot.RelationAllied || theirs == GuildSnapshot.RelationAllied;
                    if (!ends && !oursOpen && !theirsOpen) { reason = $"There is no alliance or proposal between your guild and '{other}'."; return false; }
                    if (ours == GuildSnapshot.RelationAllied || oursOpen) ForgetNameLocked(g, other);
                    if (og != null && (theirs == GuildSnapshot.RelationAllied || theirsOpen)) ForgetNameLocked(og, g.Name);
                    change = ends ? DiplomacyChange.Ended : theirsOpen ? DiplomacyChange.Declined : DiplomacyChange.Withdrawn;
                }
                else if (newState == GuildSnapshot.RelationAlliedRequested)
                {
                    if (allied) { reason = $"Your guild and '{og.Name}' are already allied."; return false; }
                    if (oursOpen) { reason = $"Your proposal to '{og.Name}' is already waiting for their answer."; return false; }
                    SetRelationLocked(g, og.Name, GuildSnapshot.RelationAlliedRequested);
                    change = DiplomacyChange.Proposed;
                }
                else if (newState == GuildSnapshot.RelationHostile)
                {
                    if (ours == GuildSnapshot.RelationHostile) { reason = $"Your guild already marks '{og.Name}' as a rival."; return false; }
                    SetRelationLocked(g, og.Name, GuildSnapshot.RelationHostile);
                    if (theirs == GuildSnapshot.RelationAllied) ForgetNameLocked(og, g.Name);
                    change = allied ? DiplomacyChange.RivalEndedAlliance : DiplomacyChange.Rival;
                }
                else { reason = "That is not a diplomacy change."; return false; }
                ok = CommitAuthorityLocked(before);
                if (!ok) reason = NotSaved;
            }
            return ok;
        }

        // Older records were written with whatever casing the client sent, so a variant is cleared before the set.
        private static void SetRelationLocked(GuildSnapshot g, string other, string state)
        {
            if (g.Relationships == null) g.Relationships = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ForgetNameLocked(g, other);
            g.Relationships[other] = state;
        }

        private static bool MutateMemberRank(string actor, string target, bool promote, out string reason)
        {
            reason = null;
            string verb = promote ? "promote" : "demote";
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(target)) { reason = $"Name a member to {verb}."; return false; }
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase)) { reason = $"You cannot {verb} yourself."; return false; }
            bool ok = false;
            string guildName = null;
            lock (_lock)
            {
                AuthorityPoint before = AuthorityPointLocked();
                if (!_userToGuild.TryGetValue(actor,  out string aGuild)) { reason = "You are not in a guild."; return false; }
                if (!_userToGuild.TryGetValue(target, out string tGuild)
                    || !string.Equals(aGuild, tGuild, StringComparison.OrdinalIgnoreCase)) { reason = $"'{target}' is not in your guild."; return false; }
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) { reason = "Your guild record is missing."; return false; }

                int actorOrder  = RankOrder(FindRankLocked(g, actor));
                int targetOrder = RankOrder(FindRankLocked(g, target));
                if (actorOrder  > RankOrder(GuildMemberDto.RankModerator)) { reason = $"Only a Moderator or above can {verb} members."; return false; }
                if (targetOrder <= actorOrder) { reason = $"You can only {verb} members ranked below you."; return false; }

                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, target, StringComparison.OrdinalIgnoreCase));
                if (m == null) { reason = "Member record not found."; return false; }

                if (promote)
                {
                    int newOrder = targetOrder - 1;
                    if (newOrder <= actorOrder) { reason = $"A promotion cannot raise {target} to your own rank."; return false; }
                    m.Rank = RankFromOrder(newOrder);
                }
                else
                {
                    int newOrder = targetOrder + 1;
                    if (newOrder > RankOrder(GuildMemberDto.RankMember)) { reason = $"{target} is already a Member - there is no lower rank."; return false; }
                    m.Rank = RankFromOrder(newOrder);
                }
                guildName = aGuild;
                ok = CommitAuthorityLocked(before);
                if (!ok) reason = NotSaved;
            }
            if (ok) RaiseChanged(guildName, "rank_changed", target);
            return ok;
        }

        private static bool BumpPerkLocked(GuildPerksDto p, string key)
        {
            switch (key)
            {
                case "site_max_workers":
                    if (p.SiteMaxWorkersBonusLevel >= GuildPerksDto.MaxLevel) return false;
                    p.SiteMaxWorkersBonusLevel += 1;  return true;
                case "marketplace_tax_cut":
                    if (p.MarketplaceTaxReductionLevel >= GuildPerksDto.MaxLevel) return false;
                    p.MarketplaceTaxReductionLevel += 1; return true;
                case "worker_xp_bonus":
                    if (p.WorkerXpBonusLevel >= GuildPerksDto.MaxLevel) return false;
                    p.WorkerXpBonusLevel += 1; return true;
                case "custom_site_cost_cut":
                    if (p.CustomSiteCostDiscountLevel >= GuildPerksDto.MaxLevel) return false;
                    p.CustomSiteCostDiscountLevel += 1; return true;
                default:
                    return false;
            }
        }

        // Caller holds _lock. Reserved with the check, or two concurrent withdraws could both pass the daily cap.
        private static bool TryReserveTallyLocked(string guildName, string actor, GuildMemberDto m, int cap, long amount,
                                                  out long already, out long dayStart)
        {
            dayStart = DateTime.UtcNow.Date.Ticks;
            _withdrawTally.TryGetValue(TallyKey(guildName, actor), out (long DayStartUtc, long Withdrawn) t);
            already = WithdrawnToday(dayStart, t.DayStartUtc, t.Withdrawn, m.WithdrawDayStartUtc, m.WithdrawnTodaySilver);
            if (already + amount > cap) return false;
            long nowTally = already + amount;
            _withdrawTally[TallyKey(guildName, actor)] = (dayStart, nowTally);
            m.WithdrawnTodaySilver = nowTally; m.WithdrawDayStartUtc = dayStart;
            return true;
        }

        // Hands back a reservation whose withdrawal never happened, so a refused attempt costs nothing off the cap.
        private static void ReleaseWithdrawTally(string guildName, string actor, long amount, long reservedDayStart)
        {
            lock (_lock)
            {
                string key = TallyKey(guildName, actor);
                if (!_withdrawTally.TryGetValue(key, out (long DayStartUtc, long Withdrawn) t) || t.DayStartUtc != reservedDayStart) return;
                long back = Math.Max(0, t.Withdrawn - amount);
                _withdrawTally[key] = (t.DayStartUtc, back);
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g)
                    && g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase)) is GuildMemberDto gm
                    && gm.WithdrawDayStartUtc == reservedDayStart)
                    gm.WithdrawnTodaySilver = back;
            }
        }

        // The in-memory tally needs the persisted member mirror, or a restart would hand out a fresh daily cap.
        internal static long WithdrawnToday(long dayStart, long tallyDay, long tallyAmount, long memberDay, long memberAmount)
            => tallyDay  == dayStart ? tallyAmount
             : memberDay == dayStart ? memberAmount
             : 0;

        // -1 is unlimited, the highest cap there is. Lowering is always allowed, and a value the owner already set stays.
        internal static bool LiftsPastAdminCap(GuildSettingsDto now, GuildSettingsDto want)
        {
            long Cap(int c) => c < 0 ? long.MaxValue : c;
            long admin = Cap(now.AdminDailyWithdrawCap);
            bool Lifts(int before, int after) => Cap(after) > Cap(before) && Cap(after) > admin;
            return Lifts(now.MemberDailyWithdrawCap, want.MemberDailyWithdrawCap)
                || Lifts(now.OfficerDailyWithdrawCap, want.OfficerDailyWithdrawCap)
                || Lifts(now.ModeratorDailyWithdrawCap, want.ModeratorDailyWithdrawCap);
        }

        private static int DailyCapFor(GuildSettingsDto s, string rank)
        {
            switch (rank)
            {
                case GuildMemberDto.RankOwner:     return -1;
                case GuildMemberDto.RankAdmin:     return s.AdminDailyWithdrawCap;
                case GuildMemberDto.RankModerator: return s.ModeratorDailyWithdrawCap;
                case GuildMemberDto.RankOfficer:   return s.OfficerDailyWithdrawCap;
                default:                           return s.MemberDailyWithdrawCap;
            }
        }

        private static string FindRankLocked(GuildSnapshot g, string username)
        {
            GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
            return m?.Rank ?? GuildMemberDto.RankMember;
        }

        // Every guild ends with exactly one Owner, so a missing or duplicated one is repaired here.
        private static void RepairOwnerLocked(GuildSnapshot g)
        {
            GuildIntegrity.Result r = GuildIntegrity.EnsureSingleOwner(g);
            if (r.Fix == GuildIntegrity.OwnerFix.PromotedNoOwner)
                Diagnostics.ServerLog.Info($"Guild '{g.Name}': promoted '{r.KeptOrPromoted}' to Owner (pre-owner-era guild).");
            else if (r.Fix == GuildIntegrity.OwnerFix.DemotedExtraOwners)
                Diagnostics.ServerLog.Warn($"Guild '{g.Name}': had {r.OwnersBefore} Owners - kept '{r.KeptOrPromoted}', demoted the rest to Admin.");
        }

        private static int RankOrder(string rank) => GuildPermissions.RankOrder(rank);

        // Owner (order 0) is deliberately unreachable here - ownership moves only via TransferAdmin.
        private static string RankFromOrder(int order)
        {
            switch (order)
            {
                case 1:  return GuildMemberDto.RankAdmin;
                case 2:  return GuildMemberDto.RankModerator;
                case 3:  return GuildMemberDto.RankOfficer;
                case 4:  return GuildMemberDto.RankMember;
                default: return GuildMemberDto.RankMember;
            }
        }

        internal static GuildSnapshot CopyForTest(GuildSnapshot g) => CopyLocked(g);

        private static GuildSnapshot CopyLocked(GuildSnapshot g)
        {
            // Clones rather than listing fields, so a newly added one still reaches clients.
            GuildSnapshot copy = g.ShallowClone();
            copy.Settings = g.Settings?.ShallowClone() ?? new GuildSettingsDto();
            copy.Perks    = g.Perks?.ShallowClone()    ?? new GuildPerksDto();
            copy.Hall     = g.Hall?.ShallowClone();
            copy.Relationships = new Dictionary<string, string>(g.Relationships, StringComparer.OrdinalIgnoreCase);
            copy.IncomingProposals = new List<string>();
            copy.PendingInvites = new List<string>(g.PendingInvites);
            copy.InviteMeta = g.InviteMeta == null
                ? new Dictionary<string, GuildInviteMetaDto>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, GuildInviteMetaDto>(g.InviteMeta, StringComparer.OrdinalIgnoreCase);

            // Live, not stored: authoritative elsewhere, so never carried over from the guild record.
            copy.GuildSilver = Treasury.TreasuryStore.GetGuildSilver(g.Name);
            (copy.GuildItems, copy.GuildPayloads) = Treasury.TreasuryStore.GuildGoodsFor(g.Name);
            copy.PendingDonationsSilver = Treasury.TreasuryStore.PendingGuildDonationTotal(g.Name);
            copy.GuildTreasuryEnabled = !string.Equals(Economy.EconomyAccess.Policy.GuildAccess, "Disabled", StringComparison.OrdinalIgnoreCase);

            copy.Members = new List<GuildMemberDto>(g.Members.Count);
            foreach (GuildMemberDto m in g.Members) copy.Members.Add(m.ShallowClone());
            return copy;
        }

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.GuildsFile, out PersistedState state) && state != null)
            {
                lock (_lock)
                {
                    _guilds.Clear();
                    _userToGuild.Clear();
                    _incarnation.Clear();
                    _pendingPerks.Clear();
                    if (state.PendingPerks != null)
                        foreach (PendingPerk pp in state.PendingPerks) if (!string.IsNullOrEmpty(pp?.Marker)) _pendingPerks[pp.Marker] = pp;
                    // A guild from before ids existed keeps a stable one derived from its name until it disbands.
                    if (state.Incarnations != null)
                        foreach (KeyValuePair<string, string> kv in state.Incarnations)
                            if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value)) _incarnation[kv.Key] = kv.Value;
                    if (state.Guilds != null)
                    {
                        foreach (GuildSnapshot g in state.Guilds)
                        {
                            if (g == null || string.IsNullOrEmpty(g.Name)) continue;
                            // Loading it would hand its members someone's personal vault; the members are named so it can be traced.
                            if (GuildNames.CollidesWithPersonalKey(g.Name))
                            {
                                var who = new List<string>();
                                if (g.Members != null) foreach (GuildMemberDto m in g.Members) if (m?.Username != null) who.Add(m.Username);
                                Diagnostics.ServerLog.Warn($"Guilds: refused to load guild '{g.Name}' - its name is a personal vault key. "
                                                         + $"Members: {string.Join(", ", who)}. It is dropped on the next save.");
                                continue;
                            }
                            _guilds[g.Name] = g;
                            RepairOwnerLocked(g);   // pre-owner-era guilds: promote the first admin/founder to Owner
                            // Rebuilt rather than persisted separately, so the two views cannot disagree after a load.
                            if (g.Members != null)
                            {
                                foreach (GuildMemberDto m in g.Members)
                                {
                                    if (string.IsNullOrEmpty(m?.Username)) continue;
                                    _userToGuild[m.Username] = g.Name;
                                }
                            }
                        }
                    }
                    PublishIdentityLocked();
                }
                Diagnostics.ServerLog.Diag($"Guilds: loaded {state.Guilds?.Count ?? 0} guild(s) from disk");
            }
        }

        // False means in memory only: perks and the withdraw tally are vault-paid, so an untaken write burns silver or refunds cap.
        public static bool SaveToDisk()
        {
            PersistedState state = new PersistedState();
            long seq;
            lock (_lock)
            {
                state.Guilds = KmhDeepCopy.OfList(_guilds.Values);
                foreach (string name in _guilds.Keys)
                    if (_incarnation.TryGetValue(name, out string id)) state.Incarnations[name] = id;
                state.PendingPerks = new List<PendingPerk>(_pendingPerks.Values);
                seq = JsonFileStore.NextSequence();
            }
            return JsonFileStore.Save(KmhDataPaths.GuildsFile, state, seq);
        }

        private sealed class AuthorityPoint
        {
            public List<GuildSnapshot> Guilds;
            public Dictionary<string, string> UserToGuild;
            public Dictionary<string, string> Incarnations;
        }

        private static AuthorityPoint AuthorityPointLocked()
            => new AuthorityPoint
            {
                Guilds = JsonFileStore.FromJson<List<GuildSnapshot>>(JsonFileStore.ToJson(new List<GuildSnapshot>(_guilds.Values))),
                UserToGuild = new Dictionary<string, string>(_userToGuild, StringComparer.OrdinalIgnoreCase),
                Incarnations = new Dictionary<string, string>(_incarnation, StringComparer.OrdinalIgnoreCase),
            };

        // Bumped only by authority changes the disk took; an operation that leaves the lock carries the ticket and refuses if it moved.
        private static long _authorityGeneration;
        private static long AuthorityTicketLocked() => _authorityGeneration;

        private static bool CommitAuthorityLocked(AuthorityPoint before)
        {
            if (SaveToDisk()) { _authorityGeneration++; PublishIdentityLocked(); return true; }
            if (before?.Guilds != null)
            {
                _guilds.Clear();
                foreach (GuildSnapshot g in before.Guilds)
                    if (!string.IsNullOrEmpty(g?.Name)) _guilds[g.Name] = g;
                _userToGuild.Clear();
                foreach (KeyValuePair<string, string> kv in before.UserToGuild) _userToGuild[kv.Key] = kv.Value;
                if (before.Incarnations != null)
                {
                    _incarnation.Clear();
                    foreach (KeyValuePair<string, string> kv in before.Incarnations) _incarnation[kv.Key] = kv.Value;
                }
            }
            PublishIdentityLocked();
            Diagnostics.ServerLog.Warn("Guilds: rolled back an unsaved authority change - the request was refused rather than acknowledged.");
            return false;
        }

        private class PersistedState
        {
            public List<GuildSnapshot> Guilds { get; set; } = new List<GuildSnapshot>();
            public Dictionary<string, string> Incarnations { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Absent in older files, which had no perk purchase in flight.
            public List<PendingPerk> PendingPerks { get; set; } = new List<PendingPerk>();
        }
    }
}
