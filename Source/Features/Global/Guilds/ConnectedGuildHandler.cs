using System;
using System.Collections.Generic;
using System.Threading;
using KMHServerAddon.Features.Global.Treasury;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Global.Guilds
{
    // Only the amount is client-supplied: account, guild, rank, op id and completion are all server-side.
    internal static class ConnectedGuildHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildConnectedRequest,  OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildConnectedDeposit,  OnDeposit);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.GuildConnectedWithdraw, OnWithdraw);
        }

        private static string UserOf(ServerClient client) => client?.GetData<UserFile>()?.Username;

        // From this session's verified handshake only; a client-claimed account id is never read.
        private static string GlobalIdOf(ServerClient client) => KmhVerifiedPlayers.IdFor(client);

        // Also refreshes the federated balance, so opening the window surfaces a re-proof Global demanded meanwhile.
        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string user = UserOf(client);
            if (string.IsNullOrEmpty(user)) return;

            FederatedGuildLink link = ConnectedGuildService.LinkFor(user);
            if (link != null && link.MayRead && ConnectedGuildService.Available)
                Run(() => ConnectedGuildService.RefreshAsync(link.LinkId, CancellationToken.None));

            Push(client, user);
        }

        private static void OnDeposit(ServerClient client, KmhEnvelope env) => Move(client, env, deposit: true);
        private static void OnWithdraw(ServerClient client, KmhEnvelope env) => Move(client, env, deposit: false);

        private static void Move(ServerClient client, KmhEnvelope env, bool deposit)
        {
            string user = UserOf(client);
            if (string.IsNullOrEmpty(user)) return;

            long amount = env?.GetInt("amount", 0) ?? 0;
            string scope = deposit ? "guild.connected.deposit" : "guild.connected.withdraw";

            Security.KmhOpClaim op = deposit
                ? new Security.KmhOpClaim("guild.connected.deposit", user, env)
                : new Security.KmhOpClaim("guild.connected.withdraw", user, env);
            if (!op.Begin()) { Push(client, user); return; }

            ConnectedResult result;
            try
            {
                result = deposit
                    ? Run(() => ConnectedGuildService.DepositAsync(user, amount, CancellationToken.None))
                    : Run(() => ConnectedGuildService.WithdrawAsync(user, amount, CancellationToken.None));
            }
            catch (Exception ex)
            {
                op.Release();
                Diagnostics.ServerLog.Verbose($"Connected guild treasury: {scope} threw ({ex.GetType().Name}).");
                KmhRouter.Notify(client, "negative", "The Connected Guild Treasury could not handle that request.");
                Push(client, user);
                return;
            }

            // Refused moved nothing, so the request id is freed; pending moved something and keeps its claim.
            if (!result.Ok && !result.Pending) op.Release();

            KmhRouter.Notify(client, result.Ok ? "positive" : (result.Pending ? "neutral" : "negative"),
                             Describe(result, deposit));

            PushToGuild(user);
            GuildHandler.BroadcastToGuildOf(user);
        }

        private static T Run<T>(Func<System.Threading.Tasks.Task<T>> f) => f().GetAwaiter().GetResult();

        private static string Describe(ConnectedResult r, bool deposit)
        {
            if (r.Ok)
                return deposit
                    ? "Sent to the guild's Colony Nexus Global treasury."
                    : "Withdrawal reserved; the silver is on its way to the guild vault.";
            if (r.Pending)
                return "That is still in progress - it finishes on its own once Colony Nexus Global answers.";
            // A guild rule refused it, and it already says why in its own words.
            if (!string.IsNullOrEmpty(r.Detail) && r.Code == "refused") return r.Detail;

            switch (r.Code)
            {
                case "treasury.disabled":   return "The Connected Guild Treasury is off in this version of Colony Nexus. The guild vault works normally.";
                case "KMH-TRS-980":         return "Colony Nexus Global has the Connected Treasury turned off. Nothing moved, and the guild vault works normally.";
                case "global.unavailable":  return "Colony Nexus Global is unreachable, so the Connected Guild Treasury is paused. The guild vault still works.";
                case "account.unlinked":    return "Link a Colony Nexus account before using the Connected Guild Treasury.";
                case "amount.invalid":      return "That amount is not allowed.";
                case "guild.none":          return "You are not in a guild.";
                case "invite.target":       return "That is not another server's id. Ask the other guild's Admin for the server id shown in their Connected Guild window.";
                case "KMH-GLD-965":         return "Joining an existing federated guild now takes an invite from one of its officers. Ask them for one for this server.";
                case "KMH-GLD-966":         return "That invite is not valid - it may be used, withdrawn, expired, or meant for another server.";
                case "guild.unlinked":      return "This guild is not linked to Colony Nexus Global yet.";
                case "guild.linked":        return "This guild is already linked to Colony Nexus Global.";
                case "guild.revoked":       return "This guild's Colony Nexus Global link was revoked.";
                case "guild.reproof":       return "The guild's Colony Nexus Global link must be proved again before silver can move. The guild vault still works.";
                case "guild.missing":       return "The guild record is missing, so nothing was credited.";
                case "operation.inflight":  return "This guild already has a Connected Treasury operation running.";
                case "persistence.refused": return "The server could not save, so no silver was moved.";
                case "signing.unavailable": return "This server cannot sign for Colony Nexus Global right now.";
                case "link.missing":        return "Colony Nexus Global accepted the link without naming it.";
                case "KMH-TRS-964":        return "This server is not yet approved to move federated silver. Your local treasury is unaffected.";
                case "KMH-TRS-965":        return "Colony Nexus Global has suspended this server's federated silver pending a review. Nothing was lost, and your local treasury still works.";
                case "KMH-TRS-966":        return "That is more than this server may move in one operation.";
                case "KMH-TRS-967":        return "This server has reached what it may move today. It resets gradually over the next day.";
                case "KMH-TRS-968":        return "Enough silver is there, but some of it has not settled yet. It becomes available shortly.";
                case "global unreachable":  return "Colony Nexus Global is unreachable. Nothing was lost - it resumes on its own.";
                default:                    return "The Connected Guild Treasury could not handle that request.";
            }
        }

        public static void Push(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.GuildConnectedSnapshot,
                                BuildPayload(user, !string.IsNullOrEmpty(GlobalIdOf(client))));

        // Every online member gets it, or one officer's deposit leaves the others showing stale silver.
        public static void PushToGuild(string user)
        {
            string guild = GuildStore.CurrentGuildOf(user);
            if (string.IsNullOrEmpty(guild)) return;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c == null || !c.IsVerified) continue;
                string other = UserOf(c);
                if (string.IsNullOrEmpty(other)) continue;
                if (!string.Equals(GuildStore.CurrentGuildOf(other), guild, StringComparison.OrdinalIgnoreCase)) continue;
                Push(c, other);
            }
        }

        // Carries Global's last-read balance and its time: a guild has no player token to read Global itself.
        internal static object BuildPayload(string user, bool linked)
        {
            FederatedGuildLink link = ConnectedGuildService.LinkFor(user);
            string guild = GuildStore.CurrentGuildOf(user);

            var ops = new List<object>();
            long pendingIn = 0, pendingOut = 0;
            if (link != null)
                foreach (ConnectedOp o in ConnectedTreasuryStore.ForLink(link.LinkId))
                {
                    if (o.IsOpen)
                    {
                        if (o.IsDeposit) pendingIn += o.Amount;
                        else pendingOut += o.Amount;
                    }
                    ops.Add(new
                    {
                        id = o.Id, kind = o.Kind, amount = o.Amount, phase = o.Phase, open = o.IsOpen,
                        who = o.Player ?? "",
                        detail = o.Detail ?? "", updated = o.UpdatedUtcTicks,
                    });
                }

            bool mayMove = GuildStore.CanMoveGuildSilver(user, out string moveWhy);
            bool mayAdmin = GuildStore.CanAdministerFederation(user, out string adminWhy);

            return new
            {
                available = ConnectedGuildService.Available,
                linked,
                in_guild  = !string.IsNullOrEmpty(guild),
                guild     = guild ?? "",
                rank      = GuildStore.RankOf(user) ?? "",
                may_move  = mayMove,
                may_admin = mayAdmin,
                move_why  = mayMove ? "" : (moveWhy ?? ""),
                admin_why = mayAdmin ? "" : (adminWhy ?? ""),

                link_id   = link == null ? "" : link.LinkId,
                guild_id  = link == null ? "" : link.GuildId,
                state     = link == null ? "" : link.State,
                state_why = link == null ? "" : (link.Detail ?? ""),

                settled   = Bal(link, b => b.Settled),
                reserved  = Bal(link, b => b.Reserved),
                available_silver = Bal(link, b => b.Available),
                debt      = Bal(link, b => b.Debt),
                frozen    = link?.Balance != null && link.Balance.Frozen,
                // Lets the client show the balance's age instead of presenting a cache as current.
                read      = link?.Balance == null ? 0 : link.Balance.ReadUtcTicks,

                local_silver = string.IsNullOrEmpty(guild) ? 0 : Features.Treasury.TreasuryStore.GetGuildSilver(guild),
                pending_in   = pendingIn,
                pending_out  = pendingOut,
                max_amount   = ConnectedTreasuryService.MaxOperationSilver,
                operations   = ops,
                revision     = FederatedGuildStore.Revision + ConnectedTreasuryStore.Revision,
            };
        }

        private static long Bal(FederatedGuildLink link, Func<FederatedBalance, long> pick)
            => link?.Balance == null ? 0 : pick(link.Balance);
    }
}
