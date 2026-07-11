using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Guilds
{
    // Server-side guild ledger (lock-guarded like the other stores); wire mutations reject gracefully.
    internal static class GuildStore
    {
        private static readonly object _lock = new object();

        // Guilds keyed by name (case-insensitive). Each holds members, perks, settings, MOTD, relationships
        private static readonly Dictionary<string, GuildSnapshot> _guilds
            = new Dictionary<string, GuildSnapshot>(StringComparer.OrdinalIgnoreCase);

        // username -> guild name (lowercase usernames; values preserve casing of the guild name as created). Empty
        // when player isn't in a guild
        private static readonly Dictionary<string, string> _userToGuild
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Daily guild-vault withdraw tally keyed by guild+user (not the member row, which is destroyed on leave), so
        // leaving and rejoining can't reset it. In-memory only - a daily cap resetting on server restart is fine.
        private static readonly Dictionary<string, (long DayStartUtc, long Withdrawn)> _withdrawTally
            = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        private static string TallyKey(string guild, string user) => (guild ?? "") + "\u0001" + (user ?? "");

        // Raise the SDK GuildChanged event. Always called OUTSIDE _lock so a subscriber can't deadlock or observe
        // half-applied state
        private static void RaiseChanged(string guildName, string reason, string actor = "")
        {
            if (string.IsNullOrEmpty(guildName)) return;
            Extensibility.KmhEventBus.Instance.RaiseGuildChanged(
                new KMH.Sdk.Server.Events.GuildChangedEvent { GuildName = guildName, Reason = reason ?? "", Actor = actor ?? "" });
        }

        // --- admin/tooling API ---

        public static bool CreateGuild(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            bool ok = false;
            lock (_lock)
            {
                if (_guilds.ContainsKey(name)) return false;
                _guilds[name] = new GuildSnapshot { Name = name };
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(name, "created"); }
            return ok;
        }

        // System-level MOTD by guild name (no rank gate) - for trusted SDK extension code.
        public static bool SetMotdByName(string guildName, string motd)
        {
            if (string.IsNullOrEmpty(guildName)) return false;
            bool ok = false;
            lock (_lock)
            {
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return false;
                g.Motd = motd ?? "";
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "motd"); }
            return ok;
        }

        public static bool AddMember(string username, string guildName, string rank = GuildMemberDto.RankMember)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(guildName)) return false;
            bool ok = false;
            lock (_lock)
            {
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return false;
                if (_userToGuild.ContainsKey(username)) return false; // already in a guild
                g.Members.Add(new GuildMemberDto
                {
                    Username       = username,
                    Rank           = string.IsNullOrEmpty(rank) ? GuildMemberDto.RankMember : rank,
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                });
                _userToGuild[username] = g.Name;
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "member_joined", username); }
            return ok;
        }

        // Convenience for the chat-command path: create a guild and add the caller as its initial Admin. Atomic -
        // if either step fails, the store is rolled back
        public static bool CreateGuildAndJoinAsAdmin(string creator, string guildName, out string errorReason, int hallTile = -1)
        {
            errorReason = null;
            if (string.IsNullOrWhiteSpace(creator) || string.IsNullOrWhiteSpace(guildName))
            {
                errorReason = "Both creator and guild name are required.";
                return false;
            }
            // P8: a server can require a Guild Hall to create a guild (needs a chosen tile).
            if (!GuildHallRules.CanCreate(hallTile, out errorReason)) return false;
            guildName = Util.KmhSafe.Cap(guildName.Trim(), 48);   // bound client-supplied name (also the dict key)
            bool ok = false;
            lock (_lock)
            {
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
                g.Members.Add(new GuildMemberDto
                {
                    Username       = creator,
                    Rank           = GuildMemberDto.RankOwner,
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                });
                // A fresh guild starts with NO hall unless one was designated at creation - a reused name never
                // inherits an old hall (the hall lives on the guild record, which is brand new here).
                if (hallTile >= 0)
                    g.Hall = new GuildHallDto
                    {
                        HasHall = true, Tile = hallTile, Leader = creator,
                        RadiusTiles = Economy.EconomyConfig.Current.GuildHallAccessRadiusTiles,
                        CreatedUtcTicks = DateTime.UtcNow.Ticks,
                    };
                _guilds[guildName]    = g;
                _userToGuild[creator] = guildName;
                PurgeInvitesLocked(creator);
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "created", creator); }
            return ok;
        }

        // Set (or move) the guild's hall to a world tile. Admin-only. Radius comes from config. Overwrites any prior
        // hall so there's never a duplicate. Returns (ok, reason).
        public static (bool ok, string reason) SetGuildHall(string actor, int tile)
        {
            if (string.IsNullOrEmpty(actor)) return (false, "No user.");
            if (tile < 0) return (false, "Pick a valid world tile for the Guild Hall.");
            string guildName;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname) || !_guilds.TryGetValue(gname, out GuildSnapshot g))
                    return (false, "You are not in a guild.");
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                    return (false, "Only the guild admin can set the Guild Hall.");
                g.Hall = new GuildHallDto
                {
                    HasHall = true, Tile = tile, Leader = actor,
                    RadiusTiles = Economy.EconomyConfig.Current.GuildHallAccessRadiusTiles,
                    CreatedUtcTicks = DateTime.UtcNow.Ticks,
                };
                guildName = g.Name;
            }
            SaveToDisk();
            RaiseChanged(guildName, "hall_set", actor);
            return (true, "Guild Hall set.");
        }

        // Audit helper: (guildName, hasHall) for every guild. Halls live ON the guild record, so an orphaned hall or a
        // hall bound to a deleted/reused guild is structurally impossible - this just reports coverage.
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
                if (!_userToGuild.TryGetValue(actor, out string gname) || !_guilds.TryGetValue(gname, out GuildSnapshot g))
                    return (false, "You are not in a guild.");
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                    return (false, "Only the guild admin can remove the Guild Hall.");
                g.Hall = null;
                guildName = g.Name;
            }
            SaveToDisk();
            RaiseChanged(guildName, "hall_removed", actor);
            return (true, "Guild Hall removed.");
        }

        // Join a guild. Requires an open guild or a standing invite (which is consumed). Admins/mods issue invites
        // via Invite()
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
                bool invited = g.PendingInvites.RemoveAll(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase)) > 0;
                if (!g.OpenJoin && !invited)
                {
                    errorReason = $"'{guildName}' is invite-only - ask an admin to /kmh guild invite you.";
                    return false;
                }
                if (!GuildHallRules.CanJoin(g, ctx, out errorReason)) return false;   // P8 proximity-to-join rule
                g.Members.Add(new GuildMemberDto
                {
                    Username       = username,
                    Rank           = GuildMemberDto.RankMember,
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                });
                _userToGuild[username] = g.Name;
                PurgeInvitesLocked(username);   // invites elsewhere are stale now
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "member_joined", username); }
            return ok;
        }

        // Admin/mod invites a guildless player (offline OK - the invite persists); re-inviting refreshes the meta.
        public static bool Invite(string actor, string target, out string guildName, out string errorReason, Economy.EconomyContext ctx = default)
        {
            errorReason = null; guildName = null;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrWhiteSpace(target))
            { errorReason = "Caller + target are required."; return false; }
            target = target.Trim();
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
            { errorReason = "You can't invite yourself."; return false; }

            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname)) { errorReason = "You're not in a guild."; return false; }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))   { errorReason = "Your guild record is missing."; return false; }
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankModerator))
                { errorReason = "Only an Admin or Moderator can invite."; return false; }
                if (!GuildHallRules.CanInvite(g, ctx, out errorReason)) return false;   // P8: remote invites can be disabled
                if (_userToGuild.ContainsKey(target)) { errorReason = $"'{target}' is already in a guild."; return false; }
                if (!g.PendingInvites.Exists(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase)))
                    g.PendingInvites.Add(target);
                (g.InviteMeta ?? (g.InviteMeta = new Dictionary<string, GuildInviteMetaDto>(StringComparer.OrdinalIgnoreCase)))[target]
                    = new GuildInviteMetaDto { Inviter = actor, CreatedUtcTicks = DateTime.UtcNow.Ticks };
                guildName = gname;
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        // Invitee turns an invite down. Returns the inviter (if recorded) so the handler can tell them.
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
                if (g.PendingInvites.RemoveAll(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase)) == 0)
                { errorReason = $"'{g.Name}' has no standing invite for you."; return false; }
                if (g.InviteMeta != null && g.InviteMeta.TryGetValue(username, out GuildInviteMetaDto meta))
                { inviter = meta.Inviter; g.InviteMeta.Remove(username); }
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        // Every standing invite for this player, newest first.
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

        // Drop a user's invites from every guild - they joined/created one, the rest are stale. Call inside _lock.
        private static void PurgeInvitesLocked(string username)
        {
            foreach (GuildSnapshot g in _guilds.Values)
            {
                g.PendingInvites.RemoveAll(u => string.Equals(u, username, StringComparison.OrdinalIgnoreCase));
                g.InviteMeta?.Remove(username);
            }
        }

        // Toggle whether anyone may join without an invite. Admin only.
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
                g.OpenJoin = open;
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        // Leave the caller's current guild. Rejects when caller is the only Admin left - would orphan the guild.
        // Real solution (promote oldest Mod, or transfer-admin command) is a follow-up
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

                // Last member out -> remove the guild entirely.
                if (g.Members.Count == 0) { _guilds.Remove(gname); guildDestroyed = true; }

                ok = true;
            }
            if (ok)
            {
                // Disband: hand the vault back to the departing member (outside the guild lock) so nothing is orphaned
                // or resurrectable by re-creating the name.
                if (guildDestroyed)
                {
                    long moved = Treasury.TreasuryStore.MoveGuildVaultToPersonal(gname, username);
                    if (moved > 0) Diagnostics.ServerLog.Info($"Guild '{gname}' disbanded - {moved}s vault returned to {username}");
                }
                SaveToDisk();
                RaiseChanged(gname, "member_left", username);
            }
            return ok;
        }

        // Transfer Admin to a same-guild target (caller becomes Member); unblocks the sole-Admin leave case.
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
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(CurrentGuildOf(callerUsername), "rank_changed", targetUsername); }
            return ok;
        }

        // (name, memberCount) tuples for the /kmh guild list command.
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

        // Leaderboard row per guild (treasury silver pulled cross-feature from TreasuryStore).
        public struct GuildSummary
        {
            public string Name;
            public int    MemberCount;
            public long   TreasurySilver;
            public bool   OpenJoin;
        }
        public static List<GuildSummary> ComputeLeaderboard()
        {
            List<GuildSummary> result;
            lock (_lock)
            {
                result = new List<GuildSummary>(_guilds.Count);
                foreach (GuildSnapshot g in _guilds.Values)
                {
                    result.Add(new GuildSummary
                    {
                        Name           = g.Name,
                        MemberCount    = g.Members?.Count ?? 0,
                        // TreasuryStore's lock is independent + brief - no deadlock risk nesting it here.
                        TreasurySilver = Treasury.TreasuryStore.GetGuildSilver(g.Name),
                        OpenJoin       = g.OpenJoin,
                    });
                }
            }
            return result;
        }

        // Returns the caller's current guild name, or null if not in one.
        public static string CurrentGuildOf(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            lock (_lock)
            {
                return _userToGuild.TryGetValue(username, out string g) ? g : null;
            }
        }

        // The caller's rank in their guild ("admin"/"moderator"/"officer"/"member"), or null if not in one.
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

        // The user's guild name IF they are its only member (a solo guild), else null. The save-reset guard uses this
        // to also clear a solo-guild vault, which would otherwise shelter silver from the reset.
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

        // True iff both guilds hold a mutual Allied relationship; false for null/empty/equal names.
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

        // --- marketplace economy hooks (consumed by MarketplaceStore) ---

        // House-tax reduction (percentage points) granted by a guild's MarketplaceTaxReduction perk. 0 when the
        // guild doesn't exist
        public static int GetMarketplaceTaxReductionPoints(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            lock (_lock)
            {
                return _guilds.TryGetValue(guildName, out GuildSnapshot g) ? g.Perks.MarketplaceTaxReductionPoints : 0;
            }
        }

        // Seller's guild sale-tax percent (0 when guildless/0%). Compute-only - SaleSplit owns the actual skim.
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

        // --- site economy hooks (consumed by SiteStore) ---

        public static int SiteMaxWorkersBonusFor(string guildName)
        {
            if (string.IsNullOrEmpty(guildName)) return 0;
            lock (_lock) { return _guilds.TryGetValue(guildName, out GuildSnapshot g) ? g.Perks.SiteMaxWorkersBonus : 0; }
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

        // Skim the recipient's guild site-reward tax from a silver reward into the guild vault. Returns the
        // recipient's net
        public static int ApplyGuildSiteRewardTax(string username, int silverGross)
        {
            if (silverGross <= 0 || string.IsNullOrEmpty(username)) return silverGross;
            string guildName;
            int taxPct;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(username, out guildName)) return silverGross;
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) return silverGross;
                taxPct = Math.Max(0, Math.Min(50, g.Settings.SiteRewardSilverTaxPercent));
            }
            if (taxPct <= 0) return silverGross;
            int tax = (int)Math.Floor(silverGross * (taxPct / 100.0));
            if (tax <= 0) return silverGross;
            Treasury.TreasuryStore.DepositGuildSilver(guildName, tax, username, note: "site reward tax");
            return silverGross - tax;
        }

        // --- snapshot accessor ---

        public static GuildSnapshotEnvelope BuildEnvelopeFor(string username)
        {
            GuildSnapshotEnvelope env = new GuildSnapshotEnvelope();
            if (string.IsNullOrEmpty(username)) return env;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(username, out string gname) || !_guilds.TryGetValue(gname, out GuildSnapshot g))
                {
                    env.MyInvites = InvitesForLocked(username);   // guildless: show standing invites
                    return env;
                }
                env.InGuild = true;
                env.Guild   = CopyLocked(g);
            }
            return env;
        }

        // --- wire mutations ---

        // Member-management gating: only Admin / Moderator can act on others;
        // can never act on self; can never act on equal-or-higher rank.
        public static bool Promote(string actor, string target)
        {
            return MutateMemberRank(actor, target, promote: true);
        }

        public static bool Demote(string actor, string target)
        {
            return MutateMemberRank(actor, target, promote: false);
        }

        public static bool Kick(string actor, string target)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(target)) return false;
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase)) return false;
            bool ok = false;
            string guildName = null;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) return false;
                if (!_userToGuild.TryGetValue(target, out string tGuild)) return false;
                if (!string.Equals(aGuild, tGuild, StringComparison.OrdinalIgnoreCase)) return false;
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) return false;

                int actorOrder  = RankOrder(FindRankLocked(g, actor));
                int targetOrder = RankOrder(FindRankLocked(g, target));
                if (actorOrder  > RankOrder(GuildMemberDto.RankModerator)) return false;
                if (targetOrder <= actorOrder) return false;

                g.Members.RemoveAll(m => string.Equals(m.Username, target, StringComparison.OrdinalIgnoreCase));
                _userToGuild.Remove(target);
                // Clear leftover invite state for the target so a later re-invite starts clean (no stale pending/meta).
                g.PendingInvites?.RemoveAll(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase));
                g.InviteMeta?.Remove(target);
                guildName = aGuild;
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "member_kicked", target); }
            return ok;
        }

        // Read-only admin inspection of one guild: join mode, vault, hall, perks, members + donated, invites, problems.
        public static bool InspectGuild(string name, out System.Collections.Generic.List<string> lines)
        {
            lines = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(name)) return false;
            long vault = Treasury.TreasuryStore.GetGuildSilver(name);
            lock (_lock)
            {
                if (!_guilds.TryGetValue(name, out GuildSnapshot g) || g == null) return false;
                bool tEnabled = !string.Equals(Economy.EconomyConfig.Current.GuildTreasuryAccessMode, "Disabled", StringComparison.OrdinalIgnoreCase);
                lines.Add($"=== Guild '{g.Name}' ===");
                lines.Add($"  join: {(g.OpenJoin ? "OPEN (anyone can join)" : "invite-only")}");
                lines.Add($"  treasury: {(tEnabled ? "enabled" : "DISABLED by server (GuildTreasuryAccessMode)")}  vault {Util.SilverFmt.Format(vault)} silver (silver-only)");
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

        // Reconcile membership: rebuild the username->guild reverse map from the authoritative per-guild Members lists.
        // Clears ghosts (mapped to a guild that no longer lists them - e.g. after a wipe/desync) + fixes wrong entries.
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

        // Buy the next perk level from the GUILD vault (admin-only). Price under the lock, charge via TreasuryStore
        // outside it, re-acquire to bump; a lost race refunds so silver is never burned. Returns reason/new level+cost.
        public static bool BuyPerk(string actor, string perkKey, out string reason, out int newLevel, out int cost)
        {
            reason = null; newLevel = 0; cost = 0;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(perkKey)) { reason = "Unknown perk."; return false; }

            string guildName;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild) || !_guilds.TryGetValue(aGuild, out GuildSnapshot g))
                { reason = "You are not in a guild."; return false; }
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin))
                { reason = "Only the guild admin can buy perks."; return false; }

                int current = CurrentPerkLevelLocked(g.Perks, perkKey);
                if (current < 0)                       { reason = "Unknown perk."; return false; }
                if (current >= GuildPerksDto.MaxLevel) { reason = $"{PerkLabel(perkKey)} is already maxed (Lv {GuildPerksDto.MaxLevel})."; return false; }
                cost      = GuildPerksDto.CostFor(current);          // 5k/15k/30k ladder
                guildName = g.Name;
            }

            // Charge the guild vault. Fails if the guild can't afford it.
            if (!Treasury.TreasuryStore.WithdrawGuildSilver(guildName, cost, actor,
                    note: $"guild perk '{perkKey}' purchase"))
            {
                reason = $"The guild vault can't afford {PerkLabel(perkKey)} - {Util.SilverFmt.Format(cost)} needed. Members contribute with 'kmh guild deposit'.";
                return false;
            }

            bool bumped = false;
            lock (_lock)
            {
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g))
                {
                    bumped = BumpPerkLocked(g.Perks, perkKey);
                    if (bumped) newLevel = CurrentPerkLevelLocked(g.Perks, perkKey);
                }
            }

            if (!bumped)
            {
                // Couldn't apply after charging (maxed by a concurrent buy, or guild vanished) - refund so nothing
                // is burned
                Treasury.TreasuryStore.DepositGuildSilver(guildName, cost, actor,
                    note: $"guild perk '{perkKey}' purchase refund");
                reason = "Could not apply the perk (a concurrent change won) - your silver was refunded.";
                return false;
            }

            SaveToDisk();
            RaiseChanged(guildName, "perk", actor);
            return true;
        }

        // Human label for a perk key - keep in sync with the client's Guild Hall perk rows.
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

        // Silver cost to advance a perk from currentLevel to currentLevel+1. Ladder: 5,000 / 15,000 / 30,000.
        public static int PerkCostForLevel(int currentLevel) => GuildPerksDto.CostFor(currentLevel);

        // Current level of the named perk, or -1 for an unknown key. Mirrors the keys BumpPerkLocked accepts
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

        // --- guild treasury contributions (chat-command surface) ---

        // Move silver personal-vault -> guild-vault (any member; real transfer, not minting); credits the member row.
        public static bool DepositToGuild(string actor, int amount, out string errorReason, Economy.EconomyContext ctx = default, string txnId = null)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor)) { errorReason = "Caller username is empty."; return false; }
            if (amount <= 0)                 { errorReason = "Amount must be positive.";   return false; }

            string guildName = CurrentGuildOf(actor);
            if (string.IsNullOrEmpty(guildName)) { errorReason = "You are not in a guild."; return false; }

            // Access gate: a contribution spends personal silver into the guild vault. Guild Hall rules layer on
            // top. ctx carries the caller's game context when the contribution came from the UI (empty on a chat cmd,
            // which makes proximity checks permissive - the "require a hall exists" checks still apply).
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: true, isGuild: false, isItem: false, ctx, out errorReason)) return false;
            if (!Economy.EconomyAccess.CheckAccess(actor, isWithdraw: false, isGuild: true, isItem: false, ctx, out errorReason)) return false;
            // Cap check counts other in-flight (pending) donations so commits can't overshoot the vault cap.
            long vaultPlusPending = Treasury.TreasuryStore.GetGuildSilver(guildName)
                                  + Treasury.TreasuryStore.PendingGuildDonationTotal(guildName);
            if (Economy.EconomyAccess.WouldExceedCap(true, vaultPlusPending, amount, out errorReason)) return false;
            GuildSnapshot gForHall; lock (_lock) { _guilds.TryGetValue(guildName, out gForHall); }
            if (!GuildHallRules.CanAccessTreasury(gForHall, out errorReason)) return false;
            if (!GuildHallRules.CanContribute(gForHall, ctx, out errorReason)) return false;

            // Debit the donor and book a PENDING donation - the guild vault is credited only when the donor's save
            // confirms (same pipeline as treasury deposits); revert/timeout refunds the donor.
            if (string.IsNullOrEmpty(txnId)) txnId = Guid.NewGuid().ToString("N");
            if (!Treasury.TreasuryStore.BeginPendingGuildDonation(actor, guildName, amount, txnId, out errorReason))
                return false;
            Economy.EconomyAccess.Audit(actor, false, true, amount);
            RaiseChanged(guildName, "treasury", actor);
            return true;
        }

        // Save-confirmed donation bookkeeping - the guild vault was already credited atomically by the treasury
        // commit; this settles member totals + metrics. If the guild disbanded while pending, the credit is pulled
        // back out of the ghost vault and refunded to the donor (a revert never reaches here).
        public static void FinalizeDonation(string actor, string guildName, int amount)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(guildName) || amount <= 0) return;
            bool exists;
            lock (_lock)
            {
                exists = _guilds.ContainsKey(guildName);
                if (exists && _guilds.TryGetValue(guildName, out GuildSnapshot g))
                {
                    GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                    if (m != null) m.SilverContributed += amount;
                }
            }
            if (!exists)
            {
                if (Treasury.TreasuryStore.WithdrawGuildSilver(guildName, amount, actor, note: "guild disbanded - donation refund"))
                    Treasury.TreasuryStore.DepositSilver(actor, amount, note: $"donation refund - guild '{guildName}' no longer exists");
                Diagnostics.ServerLog.Warn($"Guild: donation of {amount}s from {actor} finalized after guild '{guildName}' disbanded - refunded.");
                return;
            }
            // A guild contribution is the real "donation" (depositing to your own vault isn't) - credit it here.
            PlayerStats.PlayerStatsStore.AddSilverDonated(actor, amount);
            SaveToDisk();
            Diagnostics.ServerLog.Info($"Guild: donation of {Util.SilverFmt.Format(amount)} from {actor} to '{guildName}' save-confirmed.");
            RaiseChanged(guildName, "treasury", actor);
        }

        // Guild vault -> caller's personal vault, bounded by the per-rank daily caps; fails if the vault can't cover it.
        public static bool WithdrawFromGuild(string actor, int amount, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor)) { errorReason = "Caller username is empty."; return false; }
            if (amount <= 0)                 { errorReason = "Amount must be positive.";   return false; }

            string guildName;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out guildName)) { errorReason = "You are not in a guild."; return false; }
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g)) { errorReason = "Your guild record is missing."; return false; }
                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                if (m == null) { errorReason = "Member record not found."; return false; }

                // Per-rank daily cap: -1 unlimited, 0 not allowed, >0 a limit.
                int cap = DailyCapFor(g.Settings, m.Rank);
                if (cap == 0) { errorReason = "Your rank can't withdraw from the vault (an Admin sets per-rank caps)."; return false; }
                if (cap > 0)
                {
                    long dayStart = DateTime.UtcNow.Date.Ticks;
                    _withdrawTally.TryGetValue(TallyKey(guildName, actor), out (long DayStartUtc, long Withdrawn) t);
                    long already = t.DayStartUtc == dayStart ? t.Withdrawn : 0; // guild+user keyed, so leave/rejoin can't reset it
                    if (already + amount > cap)
                    {
                        errorReason = $"Daily withdraw cap is {Util.SilverFmt.Format(cap)}; you've taken {Util.SilverFmt.Format(already)} today.";
                        return false;
                    }
                }
            }

            if (!Treasury.TreasuryStore.WithdrawGuildSilver(guildName, amount, actor, note: $"withdraw to {actor}"))
            {
                errorReason = "The guild vault doesn't have that much silver.";
                return false;
            }
            // Record the withdrawal against the daily tally.
            lock (_lock)
            {
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g))
                {
                    GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                    if (m != null && DailyCapFor(g.Settings, m.Rank) > 0)
                    {
                        long dayStart = DateTime.UtcNow.Date.Ticks;
                        string key = TallyKey(guildName, actor);
                        _withdrawTally.TryGetValue(key, out (long DayStartUtc, long Withdrawn) t);
                        long cur = (t.DayStartUtc == dayStart ? t.Withdrawn : 0) + amount;
                        _withdrawTally[key] = (dayStart, cur);
                        m.WithdrawnTodaySilver = cur; m.WithdrawDayStartUtc = dayStart; // mirror for client display
                    }
                }
            }
            Treasury.TreasuryStore.DepositSilver(actor, amount, note: $"withdraw from guild '{guildName}'");
            // Net the donation metric back down so contribute -> withdraw cycling can't inflate it (floored at 0).
            PlayerStats.PlayerStatsStore.AddSilverDonated(actor, -amount);
            RaiseChanged(guildName, "treasury", actor);
            return true;
        }

        // Human-readable perk levels + next-level costs for /kmh guild perks. Returns null if the caller isn't in a
        // guild
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

        public static bool SetMotd(string actor, string motd)
        {
            if (string.IsNullOrEmpty(actor)) return false;
            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) return false;
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) return false;
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin)) return false;
                g.Motd = motd ?? "";
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        public static bool SaveSettings(string actor, GuildSettingsDto settings)
        {
            if (string.IsNullOrEmpty(actor) || settings == null) return false;
            // Range gates (server is authoritative - patch mod gated too but can't be trusted)
            if (settings.SiteRewardSilverTaxPercent < 0 || settings.SiteRewardSilverTaxPercent > 50) return false;
            if (settings.MarketplaceSaleTaxPercent  < 0 || settings.MarketplaceSaleTaxPercent  > 50) return false;
            if (settings.MemberDailyWithdrawCap    < -1) return false;
            if (settings.OfficerDailyWithdrawCap   < -1) return false;
            if (settings.ModeratorDailyWithdrawCap < -1) return false;
            if (settings.AdminDailyWithdrawCap     < -1) return false;

            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) return false;
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) return false;
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin)) return false;
                g.Settings = settings;
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        public static bool SetRelationship(string actor, string otherGuild, string newState)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrWhiteSpace(otherGuild)) return false;
            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) return false;
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) return false;
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankAdmin)) return false;
                // Use the other guild's canonical stored name for keys - the relationships dict is case-sensitive, so
                // client-supplied casing must be normalized or later lookups (accept / AreAllied) would miss.
                GuildSnapshot og = _guilds.TryGetValue(otherGuild, out GuildSnapshot found) ? found : null;
                string other = og?.Name ?? otherGuild.Trim();
                if (string.Equals(other, g.Name, StringComparison.OrdinalIgnoreCase)) return false;

                if (newState == GuildSnapshot.RelationAllied)
                {
                    // Accepting an alliance: valid only if the other guild proposed (or already allied). Set BOTH
                    // sides to Allied so it's mutual - one side alone can never make AreAllied true.
                    if (og == null) return false;
                    og.Relationships.TryGetValue(g.Name, out string theirs);
                    if (theirs != GuildSnapshot.RelationAlliedRequested && theirs != GuildSnapshot.RelationAllied)
                        return false; // they haven't proposed - can't unilaterally ally
                    g.Relationships[og.Name] = GuildSnapshot.RelationAllied;
                    og.Relationships[g.Name] = GuildSnapshot.RelationAllied;
                }
                else if (newState == GuildSnapshot.RelationNone)
                {
                    // Break/clear: dropping our side is enough to end a (mutual) alliance.
                    g.Relationships.Remove(other);
                }
                else
                {
                    // Proposal / hostile - one-sided, grants nothing on its own.
                    g.Relationships[other] = newState;
                }
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        // --- helpers ---

        private static bool MutateMemberRank(string actor, string target, bool promote)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(target)) return false;
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase)) return false;
            bool ok = false;
            string guildName = null;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor,  out string aGuild)) return false;
                if (!_userToGuild.TryGetValue(target, out string tGuild)) return false;
                if (!string.Equals(aGuild, tGuild, StringComparison.OrdinalIgnoreCase)) return false;
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) return false;

                int actorOrder  = RankOrder(FindRankLocked(g, actor));
                int targetOrder = RankOrder(FindRankLocked(g, target));
                if (actorOrder  > RankOrder(GuildMemberDto.RankModerator)) return false;
                if (targetOrder <= actorOrder) return false;

                GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, target, StringComparison.OrdinalIgnoreCase));
                if (m == null) return false;

                if (promote)
                {
                    // Step toward Mod (one rung), never above the actor.
                    int newOrder = targetOrder - 1;
                    if (newOrder <= actorOrder) return false; // would equal/exceed actor's rank
                    m.Rank = RankFromOrder(newOrder);
                }
                else
                {
                    // Step toward Member, never below it.
                    int newOrder = targetOrder + 1;
                    if (newOrder > RankOrder(GuildMemberDto.RankMember)) return false;
                    m.Rank = RankFromOrder(newOrder);
                }
                guildName = aGuild;
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "rank_changed", target); }
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

        private static int DailyCapFor(GuildSettingsDto s, string rank)
        {
            switch (rank)
            {
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

        // Every guild has exactly ONE Owner: none (pre-owner migration) -> first admin, else founder, else first member;
        // duplicates (should never happen) -> keep the first, demote the rest to Admin. Logged when it changes anything.
        private static void RepairOwnerLocked(GuildSnapshot g)
        {
            if (g?.Members == null || g.Members.Count == 0) return;
            List<GuildMemberDto> owners = g.Members.FindAll(m => string.Equals(m?.Rank, GuildMemberDto.RankOwner, StringComparison.OrdinalIgnoreCase));
            if (owners.Count == 1) return;
            if (owners.Count == 0)
            {
                GuildMemberDto pick = g.Members.Find(m => string.Equals(m?.Rank, GuildMemberDto.RankAdmin, StringComparison.OrdinalIgnoreCase)) ?? g.Members[0];
                pick.Rank = GuildMemberDto.RankOwner;
                Diagnostics.ServerLog.Info($"Guild '{g.Name}': promoted '{pick.Username}' to Owner (pre-owner-era guild).");
            }
            else
            {
                for (int i = 1; i < owners.Count; i++) owners[i].Rank = GuildMemberDto.RankAdmin;
                Diagnostics.ServerLog.Warn($"Guild '{g.Name}': had {owners.Count} Owners - kept '{owners[0].Username}', demoted the rest to Admin.");
            }
        }

        private static int RankOrder(string rank)
        {
            switch (rank)
            {
                case GuildMemberDto.RankOwner:     return 0;
                case GuildMemberDto.RankAdmin:     return 1;
                case GuildMemberDto.RankModerator: return 2;
                case GuildMemberDto.RankOfficer:   return 3;
                case GuildMemberDto.RankMember:    return 4;
                default:                           return 5;
            }
        }

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

        private static GuildSnapshot CopyLocked(GuildSnapshot g)
        {
            GuildSnapshot copy = new GuildSnapshot
            {
                Name     = g.Name,
                Motd     = g.Motd,
                GuildSilver = Treasury.TreasuryStore.GetGuildSilver(g.Name),   // authoritative vault balance for the UI
                PendingDonationsSilver = Treasury.TreasuryStore.PendingGuildDonationTotal(g.Name),
                GuildTreasuryEnabled = !string.Equals(Economy.EconomyConfig.Current.GuildTreasuryAccessMode, "Disabled", StringComparison.OrdinalIgnoreCase),

                Settings = new GuildSettingsDto
                {
                    SiteRewardSilverTaxPercent = g.Settings.SiteRewardSilverTaxPercent,
                    MarketplaceSaleTaxPercent  = g.Settings.MarketplaceSaleTaxPercent,
                    MemberDailyWithdrawCap     = g.Settings.MemberDailyWithdrawCap,
                    OfficerDailyWithdrawCap    = g.Settings.OfficerDailyWithdrawCap,
                    ModeratorDailyWithdrawCap  = g.Settings.ModeratorDailyWithdrawCap,
                    AdminDailyWithdrawCap      = g.Settings.AdminDailyWithdrawCap,
                    DefaultListingsGuildOnly   = g.Settings.DefaultListingsGuildOnly,
                },
                Perks    = new GuildPerksDto
                {
                    SiteMaxWorkersBonusLevel    = g.Perks.SiteMaxWorkersBonusLevel,
                    MarketplaceTaxReductionLevel = g.Perks.MarketplaceTaxReductionLevel,
                    WorkerXpBonusLevel          = g.Perks.WorkerXpBonusLevel,
                    CustomSiteCostDiscountLevel = g.Perks.CustomSiteCostDiscountLevel,
                },
                Relationships = new Dictionary<string, string>(g.Relationships, StringComparer.OrdinalIgnoreCase),
                OpenJoin       = g.OpenJoin,
                PendingInvites = new List<string>(g.PendingInvites),
                InviteMeta     = g.InviteMeta == null
                    ? new Dictionary<string, GuildInviteMetaDto>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, GuildInviteMetaDto>(g.InviteMeta, StringComparer.OrdinalIgnoreCase),
            };
            copy.Members = new List<GuildMemberDto>(g.Members.Count);
            foreach (GuildMemberDto m in g.Members)
            {
                copy.Members.Add(new GuildMemberDto
                {
                    Username           = m.Username,
                    Rank               = m.Rank,
                    SilverContributed  = m.SilverContributed,
                    ItemsContributed   = m.ItemsContributed,
                    QuestsCompleted    = m.QuestsCompleted,
                    JoinedUtcTicks     = m.JoinedUtcTicks,
                    WithdrawnTodaySilver = m.WithdrawnTodaySilver,
                    WithdrawDayStartUtc  = m.WithdrawDayStartUtc,
                });
            }
            return copy;
        }

        // --- persistence ---

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.GuildsFile, out PersistedState state) && state != null)
            {
                lock (_lock)
                {
                    _guilds.Clear();
                    _userToGuild.Clear();
                    if (state.Guilds != null)
                    {
                        foreach (GuildSnapshot g in state.Guilds)
                        {
                            if (g == null || string.IsNullOrEmpty(g.Name)) continue;
                            _guilds[g.Name] = g;
                            RepairOwnerLocked(g);   // pre-owner-era guilds: promote the first admin/founder to Owner
                            // Rebuild username -> guild lookup from membership lists rather than persisting it
                            // separately - keeps the two views provably consistent post-load
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
                }
                Diagnostics.ServerLog.Info($"Guilds: loaded {state.Guilds?.Count ?? 0} guild(s) from disk");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Guilds = new List<GuildSnapshot>(_guilds.Values);
            }
            JsonFileStore.Save(KmhDataPaths.GuildsFile, state);
        }

        private class PersistedState
        {
            // Only persist guilds - username->guild lookup rebuilt from membership lists on Load to avoid drift
            public List<GuildSnapshot> Guilds { get; set; } = new List<GuildSnapshot>();
        }
    }
}
