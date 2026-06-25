using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.PlayerStats;
using KMHServerAddon.Features.Treasury.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Treasury
{
    // Server-side handler for kmh.treasury.* (counterpart to the client's TreasuryHandler). Flow: request -> send the
    // caller's vault snapshot; deposit/withdraw silver|item -> adjust the ledger (if sufficient), log the tx, broadcast.
    // Deposits also bump the caller's SilverDonated on PlayerStats.
    internal static class TreasuryHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryRequest,         OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositSilver,   OnDepositSilver);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryWithdrawSilver,  OnWithdrawSilver);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryDepositItem,     OnDepositItem);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.TreasuryWithdrawItem,    OnWithdrawItem);
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            SendSnapshotTo(client);
        }

        private static void OnDepositSilver(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            int    amount   = env?.GetInt("amount", 0) ?? 0;
            if (amount <= 0)
            {
                ServerLog.Verbose($"Treasury deposit_silver rejected (amount={amount}) for {username}");
                return;
            }
            if (TreasuryStore.DepositSilver(username, amount, note: ""))
            {
                ServerLog.Info($"Treasury: {username} deposited {amount}s");
                PlayerStatsStore.AddSilverDonated(username, amount); // counted on leaderboard
                SendSnapshotTo(client);
            }
        }

        private static void OnWithdrawSilver(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            int    amount   = env?.GetInt("amount", 0) ?? 0;
            if (amount <= 0) return;
            if (TreasuryStore.WithdrawSilver(username, amount, note: ""))
            {
                ServerLog.Info($"Treasury: {username} withdrew {amount}s");
                // Grant first so the client materializes the silver into the colony only after the server has
                // actually debited it
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant, new { kind = "silver", amount });
                SendSnapshotTo(client);
            }
            else
            {
                ServerLog.Verbose($"Treasury withdraw_silver rejected (insufficient) for {username}");
                KmhRouter.Notify(client, "negative", "Withdraw failed - not enough silver, or you've hit your guild withdraw limit.");
                // Push a corrective snapshot so the patch's optimistic UI gets truth.
                SendSnapshotTo(client);
            }
        }

        private static void OnDepositItem(ServerClient client, KmhEnvelope env)
        {
            string username    = client?.GetData<UserFile>()?.Username;
            string itemDefName = env?.GetString("item_def_name") ?? "";
            int    qty         = env?.GetInt("qty", 0) ?? 0;
            if (string.IsNullOrEmpty(itemDefName) || qty <= 0) return;

            if (TreasuryStore.DepositItem(username, itemDefName, qty, note: ""))
            {
                ServerLog.Info($"Treasury: {username} deposited x{qty} {itemDefName}");
                SendSnapshotTo(client);
            }
        }

        private static void OnWithdrawItem(ServerClient client, KmhEnvelope env)
        {
            string username    = client?.GetData<UserFile>()?.Username;
            string itemDefName = env?.GetString("item_def_name") ?? "";
            int    qty         = env?.GetInt("qty", 0) ?? 0;
            if (string.IsNullOrEmpty(itemDefName) || qty <= 0) return;

            if (TreasuryStore.WithdrawItem(username, itemDefName, qty, note: ""))
            {
                ServerLog.Info($"Treasury: {username} withdrew x{qty} {itemDefName}");
                KmhRouter.SendTo(client, KmhProtocol.Kind.TreasuryGrant, new { kind = "item", def_name = itemDefName, amount = qty });
                SendSnapshotTo(client);
            }
            else
            {
                ServerLog.Verbose($"Treasury withdraw_item rejected (insufficient) for {username}");
                KmhRouter.Notify(client, "negative", "Item withdraw failed - not enough in the vault, or no permission.");
                SendSnapshotTo(client);
            }
        }

        // Send the caller-scoped snapshot to a single client. Caller-scoped because the snapshot's CanDeposit /
        // CanWithdraw flags depend on who's asking
        private static void SendSnapshotTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            TreasurySnapshot snapshot = TreasuryStore.GetSnapshotFor(username);
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, snapshot);
        }
    }
}
