using System;
using System.Collections.Generic;
using System.Threading;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Global.Treasury
{
    // Only the amount is client-supplied: account, op id, silver movement and completion are all server-side.
    internal static class ConnectedTreasuryHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConnectedRequest,  OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConnectedDeposit,  OnDeposit);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ConnectedWithdraw, OnWithdraw);
        }

        private static string UserOf(ServerClient client) => client?.GetData<UserFile>()?.Username;

        // From this session's verified handshake only; a client-claimed account id is never read.
        private static string GlobalIdOf(ServerClient client) => KmhVerifiedPlayers.IdFor(client);

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            string user = UserOf(client);
            if (string.IsNullOrEmpty(user)) return;
            Push(client, user);
        }

        private static void OnDeposit(ServerClient client, KmhEnvelope env) => Move(client, env, deposit: true);
        private static void OnWithdraw(ServerClient client, KmhEnvelope env) => Move(client, env, deposit: false);

        private static void Move(ServerClient client, KmhEnvelope env, bool deposit)
        {
            string user = UserOf(client);
            if (string.IsNullOrEmpty(user)) return;

            long amount = env?.GetInt("amount", 0) ?? 0;
            string scope = deposit ? "connected.deposit" : "connected.withdraw";

            // A re-sent packet or double click must not start a second operation; scopes are literal, not assembled,
            // so the contract check can see what is guarded.
            Security.KmhOpClaim op = deposit
                ? new Security.KmhOpClaim("connected.deposit", user, env)
                : new Security.KmhOpClaim("connected.withdraw", user, env);
            if (!op.Begin()) { Push(client, user); return; }

            string globalId = GlobalIdOf(client);
            if (string.IsNullOrEmpty(globalId))
            {
                op.Release();
                KmhRouter.Notify(client, "negative",
                    "Link a Colony Nexus account before using the Connected Treasury.");
                Push(client, user);
                return;
            }

            ConnectedResult result;
            try
            {
                result = deposit
                    ? ConnectedTreasuryService.DepositAsync(user, globalId, amount, CancellationToken.None)
                        .GetAwaiter().GetResult()
                    : ConnectedTreasuryService.WithdrawAsync(user, globalId, amount, CancellationToken.None,
                                                             (env?.GetString("grant", "") ?? "").Trim())
                        .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                op.Release();
                Diagnostics.ServerLog.Verbose($"Connected treasury: {scope} threw ({ex.GetType().Name}).");
                KmhRouter.Notify(client, "negative", "The Connected Treasury could not handle that request.");
                Push(client, user);
                return;
            }

            // Refused moved nothing, so the request id is freed; pending moved something and keeps its claim.
            if (!result.Ok && !result.Pending) op.Release();

            KmhRouter.Notify(client, result.Ok ? "positive" : (result.Pending ? "neutral" : "negative"),
                             Describe(result, deposit));

            // Always the durable state, never an optimistic echo of what was asked for.
            Push(client, user);
            if (deposit) KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot,
                                          Features.Treasury.TreasuryStore.GetSnapshotFor(user));
        }

        private static string Describe(ConnectedResult r, bool deposit)
        {
            if (r.Ok)
                return deposit
                    ? "Deposit sent to your Colony Nexus account."
                    : "Withdrawal reserved; the silver is on its way.";
            if (r.Pending)
                return "That is still in progress - it finishes on its own once Colony Nexus Global answers.";

            switch (r.Code)
            {
                case "treasury.disabled":   return "The Connected Treasury is off in this version of Colony Nexus. Your local treasury works normally.";
                case "KMH-TRS-980":         return "Colony Nexus Global has the Connected Treasury turned off. Nothing moved, and your local treasury works normally.";
                case "global.unavailable":  return "Colony Nexus Global is unreachable, so the Connected Treasury is paused.";
                case "account.unlinked":    return "Link a Colony Nexus account before using the Connected Treasury.";
                case "amount.invalid":      return "That amount is not allowed.";
                case "insufficient.local":  return "Your local treasury does not hold that much silver.";
                case "operation.inflight":  return "You already have a Connected Treasury operation running.";
                case "persistence.refused": return "The server could not save, so no silver was moved.";
                case "escrow.failed":       return "The silver could not be taken from your local treasury.";
                case "KMH-TRS-964":        return "This server is not yet approved to move federated silver. Your local treasury is unaffected.";
                case "KMH-TRS-965":        return "Colony Nexus Global has suspended this server's federated silver pending a review. Nothing was lost, and your local treasury still works.";
                case "KMH-TRS-966":        return "That is more than this server may move in one operation.";
                case "KMH-TRS-967":        return "This server has reached what it may move today. It resets gradually over the next day.";
                case "KMH-TRS-968":        return "Enough silver is there, but some of it has not settled yet. It becomes available shortly.";

                default:                    return "The Connected Treasury could not handle that request.";
            }
        }

        // Linked is a property of this session, not of the username, so it is resolved per client.
        public static void Push(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.ConnectedSnapshot,
                                BuildPayload(user, !string.IsNullOrEmpty(GlobalIdOf(client))));

        // Federated balance deliberately absent: the client reads it from Global itself, so no server can misreport it.
        internal static object BuildPayload(string user, bool linked)
        {
            List<ConnectedOp> mine = ConnectedTreasuryStore.For(user);
            var ops = new List<object>(mine.Count);
            long pendingIn = 0, pendingOut = 0;

            foreach (ConnectedOp o in mine)
            {
                if (o.IsOpen)
                {
                    if (o.IsDeposit) pendingIn += o.Amount;
                    else pendingOut += o.Amount;
                }
                ops.Add(new
                {
                    id      = o.Id,
                    kind    = o.Kind,
                    amount  = o.Amount,
                    phase   = o.Phase,
                    open    = o.IsOpen,
                    detail  = o.Detail ?? "",
                    updated = o.UpdatedUtcTicks,
                });
            }

            return new
            {
                available   = ConnectedTreasuryService.Available,
                linked,
                pending_in  = pendingIn,
                pending_out = pendingOut,
                max_amount  = ConnectedTreasuryService.MaxOperationSilver,
                operations  = ops,
                revision    = ConnectedTreasuryStore.Revision,
            };
        }
    }
}
