using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Guilds
{
    // Server-side guild ledger, same lock-guarded in-memory pattern as the other stores. Public API covers both the
    // wire mutations (from the handler) and admin/bootstrap entry points (CreateGuild, AddMember). Wire mutations
    // operate on existing guild state and reject gracefully when the caller isn't in a guild.
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

        // System-level MOTD set by guild name (no acting-member rank gate). Used by the SDK IGuildApi for trusted
        // extension code. Returns false
        // if the guild doesn't exist.
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
        public static bool CreateGuildAndJoinAsAdmin(string creator, string guildName, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrWhiteSpace(creator) || string.IsNullOrWhiteSpace(guildName))
            {
                errorReason = "Both creator and guild name are required.";
                return false;
            }
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
                    Rank           = GuildMemberDto.RankAdmin,
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                });
                _guilds[guildName]    = g;
                _userToGuild[creator] = guildName;
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "created", creator); }
            return ok;
        }

        // Join a guild. Requires an open guild or a standing invite (which is consumed). Admins/mods issue invites
        // via Invite()
        public static bool JoinGuild(string username, string guildName, out string errorReason)
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
                g.Members.Add(new GuildMemberDto
                {
                    Username       = username,
                    Rank           = GuildMemberDto.RankMember,
                    JoinedUtcTicks = DateTime.UtcNow.Ticks,
                });
                _userToGuild[username] = g.Name;
                ok = true;
            }
            if (ok) { SaveToDisk(); RaiseChanged(guildName, "member_joined", username); }
            return ok;
        }

        // Admin/mod invites a player to their guild. Target must be guildless.
        public static bool Invite(string actor, string target, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrWhiteSpace(target))
            { errorReason = "Caller + target are required."; return false; }
            if (string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
            { errorReason = "You can't invite yourself."; return false; }

            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string gname)) { errorReason = "You're not in a guild."; return false; }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))   { errorReason = "Your guild record is missing."; return false; }
                if (RankOrder(FindRankLocked(g, actor)) > RankOrder(GuildMemberDto.RankModerator))
                { errorReason = "Only an Admin or Moderator can invite."; return false; }
                if (_userToGuild.ContainsKey(target)) { errorReason = $"'{target}' is already in a guild."; return false; }
                if (!g.PendingInvites.Exists(u => string.Equals(u, target, StringComparison.OrdinalIgnoreCase)))
                    g.PendingInvites.Add(target);
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
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
                if (RankOrder(FindRankLocked(g, actor)) != RankOrder(GuildMemberDto.RankAdmin))
                { errorReason = "Only an Admin can change join mode."; return false; }
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
            bool ok = false;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(username, out string gname))
                {
                    errorReason = "You are not in a guild.";
                    return false;
                }
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))
                {
                    errorReason = "Your guild record is missing - server state may be corrupted.";
                    return false;
                }

                // Block last-Admin leaves to avoid orphaning the guild.
                bool callerIsAdmin = false;
                int  adminCount    = 0;
                foreach (GuildMemberDto m in g.Members)
                {
                    if (m.Rank == GuildMemberDto.RankAdmin) adminCount += 1;
                    if (string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase) &&
                        m.Rank == GuildMemberDto.RankAdmin)
                        callerIsAdmin = true;
                }
                if (callerIsAdmin && adminCount <= 1 && g.Members.Count > 1)
                {
                    errorReason = "You are the only Admin - promote another member to Admin first.";
                    return false;
                }

                g.Members.RemoveAll(m => string.Equals(m.Username, username, StringComparison.OrdinalIgnoreCase));
                _userToGuild.Remove(username);

                // Last member out -> remove the guild entirely.
                if (g.Members.Count == 0) _guilds.Remove(gname);

                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        // Transfer Admin rank from caller to target. Both must be in the same guild; target is promoted to Admin,
        // caller demoted to Member. Unblocks the sole-Admin leave case - admin transfers, then leaves normally.
        //
        // No restriction on multiple admins existing afterward - Admin is a count-1-or-more rank, not a singleton.
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
                if (caller.Rank != GuildMemberDto.RankAdmin)
                {
                    errorReason = "Only an Admin can transfer Admin rank.";
                    return false;
                }

                target.Rank = GuildMemberDto.RankAdmin;
                caller.Rank = GuildMemberDto.RankMember;
                ok = true;
            }
            if (ok) SaveToDisk();
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

        // Compose a leaderboard-ready snapshot for every guild on the server. Cross-feature: pulls current treasury
        // silver from TreasuryStore. Surfaces only the metrics our existing stores track; perks / lifetime
        // contributions / quests-completed land as their tracking arrives.
        public struct GuildSummary
        {
            public string Name;
            public int    MemberCount;
            public long   TreasurySilver;
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
                        // Read outside the GuildStore lock would be cleaner (no nested lock dependency between
                        // GuildStore and TreasuryStore), but TreasuryStore's lock is independent and brief - no
                        // deadlock risk
                        TreasurySilver = Treasury.TreasuryStore.GetGuildSilver(g.Name),
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

        // True iff guildA and guildB have a mutual Allied relationship. Two- way symmetric (either side could have
        // set it; we only need to find one with Allied - the patch side renders both pending and confirmed states
        // so users know when their alliance is one-sided still)
        //
        // Returns false on null / empty / equal-guild input - alliance is between distinct guilds
        public static bool AreAllied(string guildA, string guildB)
        {
            if (string.IsNullOrWhiteSpace(guildA) || string.IsNullOrWhiteSpace(guildB)) return false;
            if (string.Equals(guildA, guildB, StringComparison.OrdinalIgnoreCase)) return false;

            lock (_lock)
            {
                if (_guilds.TryGetValue(guildA, out GuildSnapshot ga) &&
                    ga.Relationships != null &&
                    ga.Relationships.TryGetValue(guildB, out string rel) &&
                    rel == GuildSnapshot.RelationAllied)
                    return true;
                if (_guilds.TryGetValue(guildB, out GuildSnapshot gb) &&
                    gb.Relationships != null &&
                    gb.Relationships.TryGetValue(guildA, out string rel2) &&
                    rel2 == GuildSnapshot.RelationAllied)
                    return true;
            }
            return false;
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

        // Skim the seller's guild sale-tax from a marketplace payout and route it into the GUILD vault. Returns the
        // seller's net. No-op if the seller isn't in a guild or the guild's MarketplaceSaleTaxPercent is 0.
        //
        // The guild sale-tax goes to the guild vault, not back to the seller.
        public static int ApplyGuildMarketplaceSaleTax(string sellerUsername, int sellerGross)
        {
            if (sellerGross <= 0 || string.IsNullOrEmpty(sellerUsername)) return sellerGross;

            string guildName;
            int    taxPct;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(sellerUsername, out guildName)) return sellerGross;
                if (!_guilds.TryGetValue(guildName, out GuildSnapshot g))      return sellerGross;
                taxPct = Math.Max(0, Math.Min(50, g.Settings.MarketplaceSaleTaxPercent));
            }
            if (taxPct <= 0) return sellerGross;

            int tax = (int)Math.Floor(sellerGross * (taxPct / 100.0));
            if (tax <= 0) return sellerGross;

            Treasury.TreasuryStore.DepositGuildSilver(guildName, tax, sellerUsername, note: "marketplace sale tax");
            return sellerGross - tax;
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
                if (!_userToGuild.TryGetValue(username, out string gname)) return env;
                if (!_guilds.TryGetValue(gname, out GuildSnapshot g))      return env;

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
                ok = true;
            }
            if (ok) SaveToDisk();
            return ok;
        }

        // Buy the next level of a perk, paid for out of the GUILD treasury (not the buyer's personal silver).
        // Admin-only. Returns false if the caller isn't an admin, the perk key is unknown, the perk is already
        // maxed, or the guild vault can't afford the next level's cost
        //
        // Ordering: we validate + price under the GuildStore lock, release it, charge the guild vault through
        // TreasuryStore (its own lock - no nesting, no event dispatch under our lock), then re-acquire and bump. If
        // the bump can't apply after the charge (e.g. a concurrent buy maxed it), we refund so silver is never
        // burned for nothing
        public static bool BuyPerk(string actor, string perkKey)
        {
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(perkKey)) return false;

            string guildName;
            int    cost;
            lock (_lock)
            {
                if (!_userToGuild.TryGetValue(actor, out string aGuild)) return false;
                if (!_guilds.TryGetValue(aGuild, out GuildSnapshot g)) return false;
                if (RankOrder(FindRankLocked(g, actor)) != RankOrder(GuildMemberDto.RankAdmin)) return false;

                int current = CurrentPerkLevelLocked(g.Perks, perkKey);
                if (current < 0) return false;                       // unknown perk key
                if (current >= GuildPerksDto.MaxLevel) return false; // already maxed
                cost      = GuildPerksDto.CostFor(current);          // 5k/15k/30k ladder
                guildName = g.Name;
            }

            // Charge the guild vault. Fails if the guild can't afford it.
            if (!Treasury.TreasuryStore.WithdrawGuildSilver(guildName, cost, actor,
                    note: $"guild perk '{perkKey}' purchase"))
                return false;

            bool bumped = false;
            lock (_lock)
            {
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g))
                    bumped = BumpPerkLocked(g.Perks, perkKey);
            }

            if (!bumped)
            {
                // Couldn't apply after charging (maxed by a concurrent buy, or guild vanished) - refund so nothing
                // is burned
                Treasury.TreasuryStore.DepositGuildSilver(guildName, cost, actor,
                    note: $"guild perk '{perkKey}' purchase refund");
                return false;
            }

            SaveToDisk();
            RaiseChanged(guildName, "perk", actor);
            return true;
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

        // Contribute personal silver into the caller's guild vault. Any member may contribute. Moves silver
        // personal-vault -> guild-vault so it's a real transfer, not minting. Records the contributor on the member
        // row for the leaderboard
        public static bool DepositToGuild(string actor, int amount, out string errorReason)
        {
            errorReason = null;
            if (string.IsNullOrEmpty(actor)) { errorReason = "Caller username is empty."; return false; }
            if (amount <= 0)                 { errorReason = "Amount must be positive.";   return false; }

            string guildName = CurrentGuildOf(actor);
            if (string.IsNullOrEmpty(guildName)) { errorReason = "You are not in a guild."; return false; }

            // Pull from the contributor's personal vault first - fails if they don't actually hold the silver (no
            // minting from thin air)
            if (!Treasury.TreasuryStore.WithdrawSilver(actor, amount, note: $"contribution to guild '{guildName}'"))
            {
                errorReason = "You don't have that much silver in your personal vault.";
                return false;
            }
            Treasury.TreasuryStore.DepositGuildSilver(guildName, amount, actor, note: $"contribution from {actor}");

            // Track the contribution on the member row for guild leaderboards.
            lock (_lock)
            {
                if (_guilds.TryGetValue(guildName, out GuildSnapshot g))
                {
                    GuildMemberDto m = g.Members.Find(x => string.Equals(x.Username, actor, StringComparison.OrdinalIgnoreCase));
                    if (m != null) m.SilverContributed += amount;
                }
            }
            SaveToDisk();
            RaiseChanged(guildName, "treasury", actor);
            return true;
        }

        // Withdraw silver from the guild vault back to the caller's personal vault. Admin-only for now (the
        // per-rank daily caps in GuildSettings are a separate enforcement feature). Fails if the vault can't cover
        // it
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
                    if (m.WithdrawDayStartUtc != dayStart) { m.WithdrawDayStartUtc = dayStart; m.WithdrawnTodaySilver = 0; }
                    if (m.WithdrawnTodaySilver + amount > cap)
                    {
                        errorReason = $"Daily withdraw cap is {Util.SilverFmt.Format(cap)}; you've taken {Util.SilverFmt.Format(m.WithdrawnTodaySilver)} today.";
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
                    if (m != null && DailyCapFor(g.Settings, m.Rank) > 0) m.WithdrawnTodaySilver += amount;
                }
            }
            Treasury.TreasuryStore.DepositSilver(actor, amount, note: $"withdraw from guild '{guildName}'");
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
                if (RankOrder(FindRankLocked(g, actor)) != RankOrder(GuildMemberDto.RankAdmin)) return false;
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
                if (RankOrder(FindRankLocked(g, actor)) != RankOrder(GuildMemberDto.RankAdmin)) return false;
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
                if (RankOrder(FindRankLocked(g, actor)) != RankOrder(GuildMemberDto.RankAdmin)) return false;
                if (string.Equals(otherGuild, aGuild, StringComparison.OrdinalIgnoreCase)) return false;

                if (newState == GuildSnapshot.RelationNone) g.Relationships.Remove(otherGuild);
                else                                        g.Relationships[otherGuild] = newState;
                // Two-way symmetry for Allied - when both sides have set AlliedRequested, this is where we'd
                // auto-promote. For v1 we just track the caller's side; the other guild's admin needs to Accept to
                // flip their side too
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
                ok = true;
            }
            if (ok) SaveToDisk();
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

        private static int RankOrder(string rank)
        {
            switch (rank)
            {
                case GuildMemberDto.RankAdmin:     return 0;
                case GuildMemberDto.RankModerator: return 1;
                case GuildMemberDto.RankOfficer:   return 2;
                case GuildMemberDto.RankMember:    return 3;
                default:                           return 4;
            }
        }

        private static string RankFromOrder(int order)
        {
            switch (order)
            {
                case 0:  return GuildMemberDto.RankAdmin;
                case 1:  return GuildMemberDto.RankModerator;
                case 2:  return GuildMemberDto.RankOfficer;
                case 3:  return GuildMemberDto.RankMember;
                default: return GuildMemberDto.RankMember;
            }
        }

        private static GuildSnapshot CopyLocked(GuildSnapshot g)
        {
            GuildSnapshot copy = new GuildSnapshot
            {
                Name     = g.Name,
                Motd     = g.Motd,
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
