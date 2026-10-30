using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Global.Federation
{
    // Only names, a tag, an invite or a link id come from the client; identity, guild, rank and federation never do.
    internal static class FederationHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationRequest,      OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationCreate,       OnCreate);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationJoin,         OnJoin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationInvite,       OnInvite);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationInviteRevoke, OnInviteRevoke);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationLeave,        OnLeave);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationRemove,       OnRemove);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationTransfer,     OnTransfer);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationUpdate,       OnUpdate);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationDisband,      OnDisband);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.FederationReprove,      OnReprove);
        }

        private static string UserOf(ServerClient client) => client?.GetData<UserFile>()?.Username;
        private static string Text(KmhEnvelope env, string key) => (env?.GetString(key, "") ?? "").Trim();

        // A refresh asked for by a window is spaced out per link, so an open window cannot hammer Global.
        private static readonly ConcurrentDictionary<string, long> _lastAsked = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private static readonly TimeSpan AskSpacing = TimeSpan.FromSeconds(30);

        // The code an officer just created, shown back to that session only and never written down.
        private sealed class Fresh { public string Code, Target; public long ExpiresAt; }
        private static readonly ConcurrentDictionary<ServerClient, Fresh> _fresh = new ConcurrentDictionary<ServerClient, Fresh>();

        public static void Forget(ServerClient client) { if (client != null) _fresh.TryRemove(client, out _); }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string user = UserOf(client);
            if (string.IsNullOrEmpty(user)) return;
            Push(client, user);

            if (!(env?.GetBool("refresh", false) ?? false)) return;
            FederatedGuildLink link = FederationService.LinkFor(user);
            if (link == null || !link.MayRead || !FederationService.Available) return;
            long now = DateTime.UtcNow.Ticks;
            if (_lastAsked.TryGetValue(link.LinkId, out long last) && now - last < AskSpacing.Ticks) return;
            _lastAsked[link.LinkId] = now;

            // Off the request path: the cached view answers at once, and the fresh one follows when Global does.
            Task.Run(async () =>
            {
                try
                {
                    FederationNotices.Baseline(link);
                    if (await FederationService.RefreshViewAsync(link.LinkId, CancellationToken.None).ConfigureAwait(false))
                    {
                        FederationRewards.GrantPending(FederatedGuildStore.ById(link.LinkId));
                        PushToGuild(user);
                    }
                    // Whatever the refresh adopted is announced once, here or by the heartbeat.
                    FederationNotices.Announce(FederatedGuildStore.ById(link.LinkId), push: false);
                }
                catch (Exception ex) { Diagnostics.ServerLog.Verbose($"Federation: refresh threw ({ex.GetType().Name})."); }
            });
        }

        // Each handler claims its op id under its own scope and releases it when nothing happened, so a retry works.
        private static void OnCreate(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.create", UserOf(c), env);
            if (!Act(c, op, "create", (u, id) => Run(() => FederationService.CreateAsync(u, id, Text(env, "name"),
                     Text(env, "tag"), Text(env, "description"), Text(env, "color"), CancellationToken.None))))
                op.Release();
        }

        private static void OnJoin(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.join", UserOf(c), env);
            if (!Act(c, op, "join", (u, id) => Run(() => FederationService.JoinAsync(u, id, Text(env, "invite"), CancellationToken.None))))
                op.Release();
        }

        private static void OnInvite(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.invite", UserOf(c), env);
            if (!Act(c, op, "invite", (u, id) => Run(() => FederationService.InviteAsync(u, id, Text(env, "target_server"), CancellationToken.None))))
                op.Release();
        }

        private static void OnInviteRevoke(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.invite_revoke", UserOf(c), env);
            if (!Act(c, op, "invite_revoke", (u, id) => Run(() => FederationService.RevokeInviteAsync(u, id, Text(env, "invite_id"), CancellationToken.None))))
                op.Release();
        }

        private static void OnLeave(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.leave", UserOf(c), env);
            if (!Act(c, op, "leave", (u, id) => Run(() => FederationService.LeaveAsync(u, id, CancellationToken.None))))
                op.Release();
        }

        private static void OnRemove(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.remove", UserOf(c), env);
            if (!Act(c, op, "remove", (u, id) => Run(() => FederationService.RemoveAsync(u, id, Text(env, "link_id"), CancellationToken.None))))
                op.Release();
        }

        private static void OnTransfer(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.transfer", UserOf(c), env);
            if (!Act(c, op, "transfer", (u, id) => Run(() => FederationService.TransferAsync(u, id, Text(env, "link_id"), CancellationToken.None))))
                op.Release();
        }

        private static void OnUpdate(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.update", UserOf(c), env);
            if (!Act(c, op, "update", (u, id) => Run(() => FederationService.UpdateAsync(u, id, Text(env, "name"),
                     Text(env, "tag"), Text(env, "description"), Text(env, "color"), CancellationToken.None))))
                op.Release();
        }

        private static void OnDisband(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.disband", UserOf(c), env);
            if (!Act(c, op, "disband", (u, id) => Run(() => FederationService.DisbandAsync(u, id, Text(env, "confirm"),
                     Text(env, "reason"), CancellationToken.None))))
                op.Release();
        }

        private static void OnReprove(ServerClient c, KmhEnvelope env)
        {
            var op = new Security.KmhOpClaim("federation.reprove", UserOf(c), env);
            if (!Act(c, op, "reprove", (u, id) => Run(() => FederationService.ReproveAsync(u, id, CancellationToken.None))))
                op.Release();
        }

        private static T Run<T>(Func<Task<T>> f) => f().GetAwaiter().GetResult();

        // False only when this request claimed its id and changed nothing; a repeat keeps the claim and returns true.
        private static bool Act(ServerClient client, Security.KmhOpClaim op, string action,
                                Func<string, string, FederationResult> act)
        {
            string user = UserOf(client);
            if (string.IsNullOrEmpty(user)) return true;
            if (!op.Begin()) { Push(client, user); return true; }

            FederationResult result;
            try { result = act(user, KmhVerifiedPlayers.IdFor(client)); }
            catch (Exception ex)
            {
                Diagnostics.ServerLog.Verbose($"Federation: {action} threw ({ex.GetType().Name}).");
                KmhRouter.Notify(client, "negative", "Guild Federation could not handle that request.");
                Push(client, user);
                return false;
            }

            if (result.Ok && action == "invite")
                _fresh[client] = new Fresh { Code = result.InviteCode, Target = result.InviteTarget, ExpiresAt = result.InviteExpiresAt };
            // The actor is told below and the guild gets the push, so the heartbeat must not announce it again.
            FederationNotices.Rebase(!string.IsNullOrEmpty(result.LinkId) ? FederatedGuildStore.ById(result.LinkId) : FederationService.LinkFor(user));

            // An unproven session is told which kind it is, so a signed-in player is never asked to sign in.
            KmhRouter.Notify(client, result.Ok ? "positive" : "negative",
                result.Code == "account.unverified" ? VerifyText(VerifyCode(client)) : Describe(result, action));
            PushToGuild(user);
            // A guild that left or was removed still needs the actor's own window to show it.
            Push(client, user);
            return result.Ok;
        }

        internal static string Describe(FederationResult r, string action)
        {
            if (r.Ok)
            {
                switch (action)
                {
                    case "create":        return "Your federation is founded. Invite other servers from the Manage tab.";
                    case "join":          return "Your guild joined the federation.";
                    case "invite":        return $"Invite code {r.InviteCode} created for server {r.InviteTarget}. It works once, for that server only, and expires in 30 minutes.";
                    case "invite_revoke": return "That invite is withdrawn and can no longer be used.";
                    case "leave":         return "Your guild left the federation. Its history stays with the federation.";
                    case "remove":        return "That server's guild was removed from the federation.";
                    case "transfer":      return "Leadership of the federation was handed over.";
                    case "update":        return "The federation's profile was updated.";
                    case "disband":       return "The federation was disbanded. Its history is kept, and its name can be used again by a new federation.";
                    case "reprove":       return "Your guild's federation link is proved again.";
                    default:              return "Done.";
                }
            }
            if (r.Code == "refused" && !string.IsNullOrEmpty(r.Detail)) return r.Detail;

            switch (r.Code)
            {
                case "feature.off":          return "Guild Federation is turned off on this server.";
                case "global.off":           return "Colony Nexus Global is turned off on this server, so Guild Federation is paused here.";
                case "global.setup":         return "This server is still registering with Colony Nexus Global. Try again in a few minutes.";
                case "global.unreachable":   return "Colony Nexus Global is unreachable right now. Your guild works normally; try again later.";
                case "global.unsupported":   return "Colony Nexus Global does not support Guild Federation yet.";
                case "global.revoked":       return "This server's Colony Nexus Global registration was revoked, so Guild Federation is closed here.";
                case "account.unverified":   return "Colony Nexus Global has not confirmed who you are on this connection, so this server cannot act for you yet.";
                case "player.unknown":       return "This server does not know who you are yet.";
                case "guild.none":           return "You are not in a guild.";
                case "guild.linked":         return "Your guild is already in a federation.";
                case "guild.unlinked":       return "Your guild is not in a federation.";
                case "invite.target":        return "That is not another server's Server ID. Ask an Admin or the Owner of that server's guild to copy it from the Manage tab of their Federation window.";
                case "invite.missing":       return "Colony Nexus Global made the invite but sent no code back. Try again.";
                case "link.missing":         return "Colony Nexus Global answered without naming the federation. Try again.";
                case "operation.inflight":   return "A Connected Treasury operation for your guild is still finishing. Try again once it has.";
                case "persistence.refused":  return "This server could not write the change down, so it did not happen. Try again shortly.";
                case "signing.unavailable":  return "This server cannot sign for Colony Nexus Global right now.";
                case "KMH-GLD-959":          return "Your guild's federation link must be proved again first (a restore or key change happened). An Admin can do it from the Manage tab.";
                case "KMH-GLD-965":          return "Joining a federation takes an invite code from one of its officers, made for this server.";
                case "KMH-GLD-966":          return "That invite is not valid - it may be used, withdrawn, expired, or meant for another server.";
                case "KMH-FED-901":          return "That federation name is not allowed (3 to 32 characters, no < or >).";
                case "KMH-FED-902":          return "That tag is not allowed (2 to 5 letters or digits).";
                case "KMH-FED-903":          return "Another active federation already uses that name.";
                case "KMH-FED-904":          return "Another active federation already uses that tag.";
                case "KMH-FED-905":          return "That description or colour is not allowed.";
                case "KMH-FED-906":          return "That federation was disbanded. Its history is kept, but it can no longer change.";
                case "KMH-FED-907":          return "Only the federation's leading guild can do that.";
                case "KMH-FED-908":          return "That guild is no longer a member of the federation.";
                case "KMH-FED-909":          return "You cannot do that to your own guild. Use Leave or Disband instead.";
                case "KMH-FED-910":          return "The leading guild must hand leadership to another member before it can leave.";
                case "KMH-FED-911":          return "Your guild is the last member. Disband the federation instead of leaving it.";
                case "KMH-FED-912":          return "Type the federation's tag exactly to confirm disbanding it.";
                case "KMH-FED-913":          return "Silver is still in flight for this federation. Try again once it settles.";
                case "KMH-FED-914":          return "This server already has a guild in that federation.";
                case "KMH-FED-915":          return "Your guild is no longer a member of that federation.";
                case "KMH-SRV-319":          return "This server's Colony Nexus Global identity was revoked.";
                default:                     return "Guild Federation could not handle that request (" + (string.IsNullOrEmpty(r.Code) ? "no answer" : r.Code) + ").";
            }
        }


        public static void Push(ServerClient client, string user)
        {
            if (client == null || string.IsNullOrEmpty(user)) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.FederationSnapshot, BuildPayload(client, user));
        }

        public static void PushToGuild(string user) => PushToGuildName(GuildStore.CurrentGuildOf(user));

        internal static Action<string> GuildPushForTest;

        public static void PushToGuildName(string guild)
        {
            if (string.IsNullOrEmpty(guild)) return;
            GuildPushForTest?.Invoke(guild);
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                string other = UserOf(c);
                if (string.IsNullOrEmpty(other)) continue;
                if (!string.Equals(GuildStore.CurrentGuildOf(other), guild, StringComparison.OrdinalIgnoreCase)) continue;
                Push(c, other);
            }
        }

        internal static object BuildPayload(ServerClient client, string user)
        {
            // Stamped before reading: no single lock spans these stores, so a later stamp could let an older read win.
            long revision = Util.KmhSnapshotRevision.Next();
            string guild = GuildStore.CurrentGuildOf(user) ?? "";
            FederatedGuildLink link = FederationService.LinkFor(user);
            FederationView view = link?.View;
            string unavailable = FederationService.UnavailableReason();
            string globalId = KmhVerifiedPlayers.IdFor(client);
            bool verified = !string.IsNullOrEmpty(globalId);
            bool inGuild = guild.Length > 0;
            bool member = link != null && link.MayRead;

            string verifyCode = verified ? "" : VerifyCode(client);
            string manageWhy = "", manageCode = "";
            bool mayManage = unavailable == "" && verified && inGuild && GuildStore.FederationRank(user, false, out manageCode, out manageWhy);
            if (!mayManage && manageWhy == null) manageWhy = "";
            if (!mayManage && manageWhy.Length == 0)
            {
                manageCode = !inGuild ? "guild.none" : !verified ? verifyCode : unavailable;
                manageWhy  = !inGuild ? "You are not in a guild."
                           : !verified ? VerifyText(verifyCode)
                           : unavailable != "" ? Describe(FederationResult.Fail(unavailable), "") : "";
            }

            string leadWhy = "", leadCode = "";
            bool mayLead = mayManage && member && link.State == FederatedLinkState.Active && link.FederationActive
                           && GuildStore.FederationRank(user, true, out leadCode, out leadWhy) && link.IsLeader;
            if (!mayLead)
            {
                if (!member) { leadWhy = ""; leadCode = ""; }
                else if (!link.IsLeader) { leadWhy = "Only the federation's leading guild can do this."; leadCode = "federation.not_leader"; }
                else if (leadWhy == null) { leadWhy = manageWhy; leadCode = manageCode; }
            }

            FederationProfile p = view?.Federation;
            Dictionary<string, FederationMember> byLink = new Dictionary<string, FederationMember>(StringComparer.Ordinal);
            if (view?.Members != null) foreach (FederationMember m in view.Members) if (m != null) byLink[m.LinkId] = m;
            FederationMember leader = p != null && byLink.TryGetValue(p.LeaderLinkId ?? "", out FederationMember lm) ? lm : null;

            var members = new List<object>();
            if (view?.Members != null)
                foreach (FederationMember m in view.Members)
                {
                    if (m == null) continue;
                    members.Add(new
                    {
                        link_id = m.LinkId, server_id = m.ServerId, server_name = m.ServerName, guild = m.LocalGuild,
                        state = m.State, joined_at = m.JoinedAt, is_leader = m.IsLeader,
                        is_self = link != null && m.LinkId == link.LinkId, online = m.Online,
                        renown_total = m.RenownTotal, renown_7d = m.Renown7d,
                    });
                }

            bool rewardsOn = FederationConfig.Current.ObjectiveRewards && FederationConfig.Current.RewardScale > 0;
            var objectives = new List<object>();
            if (view?.Objectives != null)
                foreach (FederationObjective o in view.Objectives)
                {
                    if (o == null) continue;
                    string rewardText = rewardsOn && !string.IsNullOrEmpty(o.Id) ? FederationRewards.Describe(o.Id) : "";
                    objectives.Add(new
                    {
                        id = o.Id, category = o.Category, target = o.Target, progress = o.Progress,
                        starts_at = o.StartsAt, ends_at = o.EndsAt, state = o.State, reward_renown = o.RewardRenown,
                        my_progress = o.MyProgress, reward_eligible = o.RewardEligible,
                        reward_granted = link != null && FederatedGuildStore.HasRewarded(link.LinkId, o.Id),
                        reward_text = rewardText,
                    });
                }

            var achievements = new List<object>();
            if (view?.Achievements != null)
                foreach (FederationAchievement a in view.Achievements)
                    if (a != null) achievements.Add(new { key = a.Key, achieved_at = a.AchievedAt });

            var events = new List<object>();
            if (view?.Events != null)
                foreach (FederationEvent e in view.Events)
                    if (e != null) events.Add(new { at = e.At, kind = e.Kind, server = e.ServerName, guild = e.LocalGuild, detail = e.Detail });

            var categories = new List<object>();
            if (view?.Categories != null)
                foreach (FederationCategory k in view.Categories)
                    if (k != null) categories.Add(new { category = k.Category, total = k.Total, last_7d = k.Last7d });

            var invites = new List<object>();
            if (mayManage && view?.Invites != null)
                foreach (FederationInvite i in view.Invites)
                    if (i != null && (i.Mine || mayLead))
                        invites.Add(new { invite_id = i.InviteId, target_server = i.TargetServer, target_name = i.TargetName,
                                          expires_at = i.ExpiresAt, mine = i.Mine });

            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            object fresh = new { code = "", target_server = "", expires_at = 0L };
            if (client != null && _fresh.TryGetValue(client, out Fresh f))
            {
                if (f.ExpiresAt > nowUnix) fresh = new { code = f.Code, target_server = f.Target, expires_at = f.ExpiresAt };
                else _fresh.TryRemove(client, out _);
            }

            long readTicks = link?.ViewReadUtcTicks ?? 0;
            long syncedAt = readTicks > 0 ? new DateTimeOffset(readTicks, TimeSpan.Zero).ToUnixTimeSeconds() : 0;
            // A view never read is "not read yet", not stale: there is no time to say it is stale since.
            bool stale = member && readTicks > 0 && DateTime.UtcNow.Ticks - readTicks > FederationService.ViewMaxAge.Ticks * 3;
            bool bonusesApply = member && link.State == FederatedLinkState.Active && link.FederationActive && readTicks > 0
                                && FederationConfig.Current.Benefits && FeaturesConfig.Current.GuildFederation;
            long benefitsUntil = bonusesApply ? syncedAt + (long)FederationService.BenefitGrace.TotalSeconds : 0;

            return new
            {
                revision,
                available = unavailable == "",
                unavailable,
                verified,
                verified_why = verified ? "" : VerifyText(verifyCode),
                verified_why_code = verifyCode,
                in_guild = inGuild,
                guild,
                rank = GuildStore.RankOf(user) ?? "",
                may_manage = mayManage,
                manage_why = mayManage ? "" : manageWhy ?? "",
                manage_why_code = mayManage ? "" : manageCode ?? "",
                may_lead = mayLead,
                lead_why = mayLead ? "" : leadWhy ?? "",
                lead_why_code = mayLead ? "" : leadCode ?? "",
                server_id = KmhServerIdentity.Id ?? "",
                linked = member,
                link = new
                {
                    link_id = link?.LinkId ?? "", state = link?.State ?? "", detail = link?.Detail ?? "",
                    joined_at = view?.Self?.JoinedAt ?? 0, is_leader = link?.IsLeader ?? false,
                },
                federation = new
                {
                    id = p?.Id ?? "", name = p?.Name ?? "", tag = p?.Tag ?? "", description = p?.Description ?? "",
                    color = p?.Color ?? "", state = p?.State ?? "", created_at = p?.CreatedAt ?? 0,
                    disbanded_at = p?.DisbandedAt ?? 0, disband_reason = p?.DisbandReason ?? "",
                    founder_server = p?.FounderServerName ?? "", founder_guild = p?.FounderGuild ?? "",
                    leader_link_id = p?.LeaderLinkId ?? "", leader_server = leader?.ServerName ?? "",
                    leader_guild = leader?.LocalGuild ?? "", level = p?.Level ?? 1, renown = p?.Renown ?? 0,
                    level_floor = p?.LevelFloor ?? 0, next_level_at = p?.NextLevelAt ?? 0,
                    active_members = p?.ActiveMembers ?? 0, lifetime_members = p?.LifetimeMembers ?? 0,
                    verified_players_30d = p?.VerifiedPlayers30d ?? 0,
                    lifetime_verified_players = p?.LifetimeVerifiedPlayers ?? 0,
                    objectives_completed = p?.ObjectivesCompleted ?? 0,
                    renown_today = p?.RenownToday ?? 0, renown_day_cap = p?.RenownDayCap ?? 0,
                },
                members,
                objectives,
                achievements,
                events,
                categories,
                invites,
                benefits = FederationBenefits.Describe(guild, member ? (p?.Level ?? 0) : 0),
                fresh_invite = fresh,
                synced_at = syncedAt,
                stale,
                benefits_until = benefitsUntil,
                pending_reports = link == null ? 0 : FederationOutbox.PendingFor(link.LinkId),
                // Server-wide, so a player can tell "waiting" from "never counted".
                lost_reports = FederationOutbox.LostTotal,
            };
        }

        // Signed-out and unconfirmed need different fixes; a client too old to say which gets wording that fits both.
        internal static string VerifyCode(ServerClient client)
        {
            if (!GlobalSetupState.Current_.IsRegistered) return "global.identity";
            switch (KmhVerifiedPlayers.UnverifiedWhy(client))
            {
                case KmhVerifiedPlayers.SignedOut:     return "account.signed_out";
                case KmhVerifiedPlayers.LocalAddress:  return "account.local_address";
                case KmhVerifiedPlayers.OtherAddress:  return "account.other_address";
                case KmhVerifiedPlayers.Unconfirmed:   return "account.unconfirmed";
                case KmhVerifiedPlayers.TicketRefused: return "account.ticket_refused";
                default:                               return "account.unknown";
            }
        }

        internal static string VerifyText(string code)
        {
            switch (code)
            {
                case "global.identity":        return "This server is still getting its Colony Nexus Global identity, so it cannot confirm who you are yet.";
                case "account.signed_out":     return "Sign in to Colony Nexus Global with Steam under Account & links. This server confirms who you are as soon as you do.";
                case "account.local_address":  return "You are signed in to Colony Nexus Global, but this connection is unverified: you joined through a local or private address, and Global only confirms players who join through the server's public address.";
                case "account.other_address":  return "You are signed in to Colony Nexus Global, but this connection is unverified: you joined through an address that is not the one this server registered with Global.";
                case "account.unconfirmed":    return "You are signed in to Colony Nexus Global, but it has not confirmed this connection yet. Colony Nexus keeps trying on its own.";
                case "account.ticket_refused": return "You are signed in to Colony Nexus Global, but this server could not accept your identity ticket. Colony Nexus keeps trying on its own.";
                default:                       return "This server has no proof of your Colony Nexus Global identity on this connection yet. Sign in with Steam under Account & links if you have not.";
            }
        }
    }
}
