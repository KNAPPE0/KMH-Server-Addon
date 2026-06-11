using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Guilds
{
    // Server-side handler for kmh.guild.* - counterpart to the patch mod's KMHPatch.Features.Guilds.GuildHandler
    //
    // Snapshot is caller-scoped (each member sees their own guild) so broadcasting on mutation iterates only the
    // affected guild's membership rather than every connected client
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
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildProposeAlliance,   (c, e) => OnAlliance(c, e, GuildSnapshot.RelationAlliedRequested));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildAcceptAlliance,    (c, e) => OnAlliance(c, e, GuildSnapshot.RelationAllied));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildBreakAlliance,     (c, e) => OnAlliance(c, e, GuildSnapshot.RelationNone));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildDeclareHostile,    (c, e) => OnAlliance(c, e, GuildSnapshot.RelationHostile));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildClearHostile,      (c, e) => OnAlliance(c, e, GuildSnapshot.RelationNone));
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildSaveSettings,      OnSaveSettings);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildInvite,            OnInvite);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildSetOpenJoin,       OnSetOpenJoin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildJoin,              OnJoin);
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
            if (GuildStore.BuyPerk(actor, perkKey))
            {
                ServerLog.Info($"Guild: {actor} bought perk {perkKey}");
                BroadcastToGuildOf(actor);
            }
            else SendSnapshotTo(client);
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
            if (GuildStore.Invite(actor, target, out string err))
            {
                ServerLog.Info($"Guild: {actor} invited {target}");
                KmhRouter.Notify(client, "positive", $"Invited {target}.");
                BroadcastToGuildOf(actor);
            }
            else KmhRouter.Notify(client, "negative", err ?? "Invite failed.");
        }

        private static void OnSetOpenJoin(ServerClient client, KmhEnvelope env)
        {
            string actor = client?.GetData<UserFile>()?.Username;
            bool   open  = env?.GetBool("open") ?? false;
            if (GuildStore.SetOpenJoin(actor, open, out string err))
            {
                ServerLog.Info($"Guild: {actor} set open-join {open}");
                KmhRouter.Notify(client, "positive", open ? "Guild is now open to join." : "Guild is now invite-only.");
                BroadcastToGuildOf(actor);
            }
            else KmhRouter.Notify(client, "negative", err ?? "Could not change join mode.");
        }

        private static void OnJoin(ServerClient client, KmhEnvelope env)
        {
            string actor     = client?.GetData<UserFile>()?.Username;
            string guildName = env?.GetString("guild") ?? "";
            if (GuildStore.JoinGuild(actor, guildName, out string err))
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
