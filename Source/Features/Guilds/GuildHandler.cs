using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Guilds
{
    // kmh.guild.* handler; snapshots are caller-scoped so mutations broadcast only to the affected guild's members.
    internal static class GuildHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildRequest,           OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildPromote,           OnPromote);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildDemote,            OnDemote);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildKick,              OnKick);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildBuyPerk,           OnBuyPerk);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildSetMotd,           OnSetMotd);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildLeave,             OnLeave);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildDonate,            OnDonate);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildWithdraw,          OnWithdraw);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildTransferOwner,     OnTransferOwner);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildProposeAlliance,   (c, e) => OnAlliance(c, e, GuildSnapshot.RelationAlliedRequested));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildAcceptAlliance,    (c, e) => OnAlliance(c, e, GuildSnapshot.RelationAllied));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildBreakAlliance,     (c, e) => OnAlliance(c, e, GuildSnapshot.RelationNone));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildDeclareHostile,    (c, e) => OnAlliance(c, e, GuildSnapshot.RelationHostile));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildClearHostile,      (c, e) => OnAlliance(c, e, GuildSnapshot.RelationNone));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildSaveSettings,      OnSaveSettings);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildInvite,            OnInvite);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildDeclineInvite,     OnDeclineInvite);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildInvitablesRequest, OnInvitablesRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildSetOpenJoin,       OnSetOpenJoin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildJoin,              OnJoin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildCreate,            OnCreate);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildHallSet,           OnHallSet);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildHallRemove,        OnHallRemove);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildLeaderboardRequest, OnLeaderboardRequest);
        }

        // Cross-guild leaderboard request - returns every guild on the server with the metrics we track. Caller's
        // own guild isn't privileged; the snapshot is the same for everyone
        private static void OnLeaderboardRequest(ServerClient client, KmhEnvelope env)
        {
            System.Collections.Generic.List<GuildStore.GuildSummary> rows = GuildStore.ComputeLeaderboard();
            GuildLeaderboardSnapshot snap = new GuildLeaderboardSnapshot();
            foreach (GuildStore.GuildSummary g in rows)
            {
                snap.Guilds.Add(new GuildLeaderboardEntry
                {
                    Name           = g.Name,
                    MemberCount    = g.MemberCount,
                    TreasurySilver = g.TreasurySilver,
                    OpenJoin       = g.OpenJoin,
                });
            }
            KmhRouter.SendTo(client, KmhProtocol.Kind.GuildLeaderboardSnapshot, snap);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        private static void OnPromote(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            string target = env?.GetString("username") ?? "";
            if (GuildStore.Promote(actor, target))
            {
                ServerLog.Info($"Guild: {actor} promoted {target}");
                BroadcastToGuildOf(actor);
            }
            else SendSnapshotTo(client);
        }

        private static void OnDemote(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            string target = env?.GetString("username") ?? "";
            if (GuildStore.Demote(actor, target))
            {
                ServerLog.Info($"Guild: {actor} demoted {target}");
                BroadcastToGuildOf(actor);
            }
            else SendSnapshotTo(client);
        }

        private static void OnKick(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            string target = env?.GetString("username") ?? "";
            if (GuildStore.Kick(actor, target))
            {
                ServerLog.Info($"Guild: {actor} kicked {target}");
                BroadcastToGuildOf(actor);
                // The kicked player now has no guild - push them an updated snapshot too (will render the 'not in a
                // guild' empty state)
                SendSnapshotToUsername(target);
            }
            else SendSnapshotTo(client);
        }

        private static void OnBuyPerk(ServerClient client, KmhEnvelope env)
        {
            string actor   = client?.GetData<UserFile>()?.Username;
            string perkKey = env?.GetString("perk_key") ?? "";
            if (GuildStore.BuyPerk(actor, perkKey, out string reason, out int newLevel, out int cost))
            {
                ServerLog.Info($"Guild: {actor} bought perk {perkKey} -> Lv {newLevel} ({cost}s)");
                KmhRouter.Notify(client, "positive", $"Purchased {GuildStore.PerkLabel(perkKey)} (now Lv {newLevel}) for {Util.SilverFmt.Format(cost)}.");
                BroadcastToGuildOf(actor);   // refresh the Hall for the whole guild so levels update immediately
            }
            else
            {
                ServerLog.Verbose($"Guild: {actor} perk buy '{perkKey}' rejected - {reason}");
                KmhRouter.Notify(client, "negative", $"Could not buy {GuildStore.PerkLabel(perkKey)}: {reason}");
                SendSnapshotTo(client);
            }
        }

        private static void OnLeave(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            string guild = GuildStore.CurrentGuildOf(actor);
            // GuildStore.Leave handles last-admin block + disband + vault return; guild/vault/sites are never deleted here.
            if (GuildStore.Leave(actor, out string reason))
            {
                ServerLog.Info($"Guild: {actor} left '{guild}'");
                KmhRouter.Notify(client, "positive", "You left the guild.");
                SendSnapshotTo(client);   // remaining members refresh on next Hall open (no global guild broadcast)
            }
            else KmhRouter.Notify(client, "negative", reason ?? "Could not leave the guild.");
        }

        private static void OnDonate(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            int    amount = env?.GetInt("amount", 0) ?? 0;
            string reqId  = env?.GetString("req_id") ?? "";
            if (amount <= 0) { KmhRouter.Notify(client, "negative", "Donation amount must be positive."); return; }
            // Same double-click/replay guards as withdraw; the donation itself books as PENDING and only credits the
            // guild vault when the donor's save confirms (rides the treasury deposit-confirm pipeline).
            long now = System.DateTime.UtcNow.Ticks;
            if (_lastWithdrawTicks.TryGetValue("donate|" + actor, out long last) && now - last < System.TimeSpan.FromSeconds(2).Ticks)
            { KmhRouter.Notify(client, "negative", "Donation is on a short cooldown - try again in a moment."); return; }
            if (!string.IsNullOrEmpty(reqId) && !_seenWithdrawIds.TryAdd("donate|" + actor + "|" + reqId, 0))
            { SendSnapshotTo(client); return; }   // duplicate click/replay: already processed
            if (GuildStore.DepositToGuild(actor, amount, out string reason, Features.Economy.EconomyContext.FromEnvelope(env), txnId: reqId))
            {
                _lastWithdrawTicks["donate|" + actor] = now;
                KmhRouter.Notify(client, "positive", $"Donation of {Util.SilverFmt.Format(amount)} pending - save your game to finalize.");
                BroadcastToGuildOf(actor);
                SendSnapshotTo(client);
            }
            else KmhRouter.Notify(client, "negative", reason ?? "Could not donate to the guild.");
        }

        // Withdraw abuse guards: per-user cooldown + request-id dedup so a double-click/replayed packet can't withdraw twice.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastWithdrawTicks
            = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenWithdrawIds
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(System.StringComparer.Ordinal);

        private static void OnWithdraw(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            int    amount = env?.GetInt("amount", 0) ?? 0;
            string reqId  = env?.GetString("req_id") ?? "";
            if (amount <= 0) { KmhRouter.Notify(client, "negative", "Withdraw amount must be positive."); return; }
            if (string.Equals(Features.Economy.EconomyConfig.Current.GuildTreasuryAccessMode, "Disabled", System.StringComparison.OrdinalIgnoreCase))
            { KmhRouter.Notify(client, "negative", "Guild vault is disabled by server."); return; }
            long now = System.DateTime.UtcNow.Ticks;
            if (_lastWithdrawTicks.TryGetValue(actor ?? "", out long last) && now - last < System.TimeSpan.FromSeconds(3).Ticks)
            { KmhRouter.Notify(client, "negative", "Withdraw is on a short cooldown - try again in a moment."); return; }
            if (!string.IsNullOrEmpty(reqId) && !_seenWithdrawIds.TryAdd(actor + "|" + reqId, 0))
            { SendSnapshotTo(client); return; }   // duplicate click/replay: already processed, just refresh
            if (_seenWithdrawIds.Count > 512) _seenWithdrawIds.Clear();
            if (GuildStore.WithdrawFromGuild(actor, amount, out string reason))
            {
                _lastWithdrawTicks[actor ?? ""] = now;
                ServerLog.Info($"Guild: {actor} withdrew {Util.SilverFmt.Format(amount)} from the guild vault.");
                KmhRouter.Notify(client, "positive", $"Withdrew {Util.SilverFmt.Format(amount)} from the guild vault to your personal Treasury.");
                BroadcastToGuildOf(actor);
                SendSnapshotTo(client);   // refresh the caller's personal treasury line too
            }
            else KmhRouter.Notify(client, "negative", reason ?? "Could not withdraw from the guild vault.");
        }

        private static void OnTransferOwner(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            string target = env?.GetString("username") ?? "";
            if (GuildStore.TransferAdmin(actor, target, out string reason))
            {
                ServerLog.Info($"Guild: {actor} transferred guild ownership to {target}.");
                KmhRouter.Notify(client, "positive", $"Ownership transferred to {target}. You are now an Admin.");
                BroadcastToGuildOf(actor);
            }
            else KmhRouter.Notify(client, "negative", reason ?? "Could not transfer ownership.");
        }

        private static void OnSetMotd(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            string motd  = env?.GetString("motd") ?? "";
            if (GuildStore.SetMotd(actor, motd))
            {
                ServerLog.Info($"Guild: {actor} updated MOTD");
                BroadcastToGuildOf(actor);
            }
            else SendSnapshotTo(client);
        }

        private static void OnAlliance(ServerClient client, KmhEnvelope env, string newState)
        {
            string actor      = client?.GetData<UserFile>()?.Username;
            string otherGuild = env?.GetString("other_guild") ?? "";
            if (GuildStore.SetRelationship(actor, otherGuild, newState))
            {
                ServerLog.Info($"Guild: {actor} set relationship with {otherGuild} to {newState}");
                BroadcastToGuildOf(actor);
            }
            else SendSnapshotTo(client);
        }

        private static void OnSaveSettings(ServerClient client, KmhEnvelope env)
        {
            string actor    = client?.GetData<UserFile>()?.Username;
            // env.Data IS the GuildSettingsDto shape (the patch mod sends the typed DTO directly as the data
            // field)
            GuildSettingsDto settings = env?.DataAs<GuildSettingsDto>();
            if (settings != null && GuildStore.SaveSettings(actor, settings))
            {
                ServerLog.Info($"Guild: {actor} saved settings");
                BroadcastToGuildOf(actor);
            }
            else SendSnapshotTo(client);
        }

        private static void OnInvite(ServerClient client, KmhEnvelope env)
        {
            string actor  = client?.GetData<UserFile>()?.Username;
            string target = env?.GetString("username") ?? "";
            if (GuildStore.Invite(actor, target, out string gname, out string err, Features.Economy.EconomyContext.FromEnvelope(env)))
            {
                ServerLog.Info($"Guild: {actor} invited {target} to {gname}");
                KmhRouter.Notify(client, "positive", $"Invited {target}.");
                BroadcastToGuildOf(actor);
                // Invitee: live toast + fresh snapshot if online, queued mail for next connect if offline.
                Notifications.KmhMail.ToUser(target.Trim(), "positive", $"Guild invite: {gname}",
                    $"{actor} invited you to join '{gname}'. Open the Guild Hall (KMH tab) to accept or decline.");
                SendSnapshotToUsername(target.Trim());
            }
            else KmhRouter.Notify(client, "negative", err ?? "Invite failed.");
        }

        private static void OnDeclineInvite(ServerClient client, KmhEnvelope env)
        {
            string actor     = client?.GetData<UserFile>()?.Username;
            string guildName = env?.GetString("guild") ?? "";
            if (GuildStore.DeclineInvite(actor, guildName, out string inviter, out string err))
            {
                ServerLog.Info($"Guild: {actor} declined invite to {guildName}");
                KmhRouter.Notify(client, "neutral", $"Declined the invite to {guildName}.");
                SendSnapshotTo(client);
                if (!string.IsNullOrEmpty(inviter))
                    Notifications.KmhMail.ToUser(inviter, "neutral", "Guild invite declined",
                        $"{actor} declined your invite to '{guildName}'.");
            }
            else KmhRouter.Notify(client, "negative", err ?? "Could not decline that invite.");
        }

        // Known guildless players for the invite picker (online first); the invite itself stays rank-gated.
        private static void OnInvitablesRequest(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(actor) || string.IsNullOrEmpty(GuildStore.CurrentGuildOf(actor))) return;

            HashSet<string> online = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                string u = c?.IsVerified == true ? c.GetData<UserFile>()?.Username : null;
                if (!string.IsNullOrEmpty(u)) online.Add(u);
            }

            HashSet<string> seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            List<InvitablePlayerDto> players = new List<InvitablePlayerDto>();
            void Consider(string u)
            {
                if (string.IsNullOrEmpty(u) || !seen.Add(u)) return;
                if (string.Equals(u, actor, System.StringComparison.OrdinalIgnoreCase)) return;
                if (!string.IsNullOrEmpty(GuildStore.CurrentGuildOf(u))) return;
                players.Add(new InvitablePlayerDto { Username = u, Online = online.Contains(u) });
            }
            foreach (string u in online) Consider(u);
            try { foreach (var e in PlayerStats.PlayerStatsStore.BuildSnapshot().Entries) Consider(e.Username); } catch { }

            players.Sort((a, b) => a.Online != b.Online
                ? (a.Online ? -1 : 1)
                : string.Compare(a.Username, b.Username, System.StringComparison.OrdinalIgnoreCase));
            KmhRouter.SendTo(client, KmhProtocol.Kind.GuildInvitablesSnapshot,
                new GuildInvitablesSnapshot { Players = players });
        }

        private static void OnSetOpenJoin(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            bool   open  = env?.GetBool("open") ?? false;
            if (GuildStore.SetOpenJoin(actor, open, out string err))
            {
                ServerLog.Info($"Guild: {actor} set join mode to {(open ? "open" : "invite-only")}");
                KmhRouter.Notify(client, "positive", open ? "Guild is now open to join." : "Guild is now invite-only.");
                BroadcastToGuildOf(actor);
            }
            else if (err == "noop") SendSnapshotTo(client);   // already in that state: silent refresh, no log spam
            else KmhRouter.Notify(client, "negative", err ?? "Could not change join mode.");
        }

        private static void OnJoin(ServerClient client, KmhEnvelope env)
        {
            string actor     = client?.GetData<UserFile>()?.Username;
            string guildName = env?.GetString("guild") ?? "";
            if (GuildStore.JoinGuild(actor, guildName, out string err, Features.Economy.EconomyContext.FromEnvelope(env)))
            {
                ServerLog.Info($"Guild: {actor} joined {guildName}");
                KmhRouter.Notify(client, "positive", $"Joined {guildName}.");
                BroadcastToGuildOf(actor); // refreshes the joiner + existing members
            }
            else
            {
                KmhRouter.Notify(client, "negative", err ?? "Could not join that guild.");
                SendSnapshotTo(client); // refresh the joiner's no-guild view
            }
        }

        private static void OnCreate(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            string name  = env?.GetString("name") ?? "";
            int    hallTile = env?.GetInt("hall_tile", -1) ?? -1;   // P8: optional hall location at creation
            if (GuildStore.CreateGuildAndJoinAsAdmin(actor, name, out string err, hallTile))
            {
                ServerLog.Info($"Guild: {actor} created {name}");
                KmhRouter.Notify(client, "positive", $"Created guild {name}.");
                BroadcastToGuildOf(actor); // refreshes the creator into their new guild
            }
            else
            {
                KmhRouter.Notify(client, "negative", err ?? "Could not create that guild.");
                SendSnapshotTo(client); // keep the creator's no-guild view in sync
            }
        }

        private static void OnHallSet(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            int    tile  = env?.GetInt("tile", -1) ?? -1;
            (bool ok, string reason) = GuildStore.SetGuildHall(actor, tile);
            KmhRouter.Notify(client, ok ? "positive" : "negative", ok ? "Guild Hall set." : reason);
            if (ok) { ServerLog.Info($"Guild: {actor} set hall at tile {tile}"); BroadcastToGuildOf(actor); }
        }

        private static void OnHallRemove(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            (bool ok, string reason) = GuildStore.RemoveGuildHall(actor);
            KmhRouter.Notify(client, ok ? "positive" : "negative", ok ? "Guild Hall removed." : reason);
            if (ok) { ServerLog.Info($"Guild: {actor} removed hall"); BroadcastToGuildOf(actor); }
        }

        // --- snapshot delivery helpers ---

        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;
            GuildSnapshotEnvelope envelope = GuildStore.BuildEnvelopeFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.GuildSnapshot, envelope);
        }

        private static void SendSnapshotToUsername(string username)
        {
            if (string.IsNullOrEmpty(username)) return;
            // Find the connected client by username.
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                if (string.Equals(c.GetData<UserFile>()?.Username, username, System.StringComparison.OrdinalIgnoreCase))
                {
                    SendSnapshotTo(c);
                    return;
                }
            }
        }

        // After a guild mutation, push a fresh snapshot to every CURRENTLY- CONNECTED member of the actor's guild.
        // The guild membership for the post-mutation state is the canonical view
        private static void BroadcastToGuildOf(string actor)
        {
            if (string.IsNullOrEmpty(actor)) return;
            GuildSnapshotEnvelope envelope = GuildStore.BuildEnvelopeFor(actor);
            if (!envelope.InGuild || envelope.Guild == null) return;

            // Build a set of usernames currently in the guild (post-mutation).
            System.Collections.Generic.HashSet<string> memberSet
                = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (GuildMemberDto m in envelope.Guild.Members) memberSet.Add(m.Username);

            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u) || !memberSet.Contains(u)) continue;
                SendSnapshotTo(c);
            }
        }
    }
}
