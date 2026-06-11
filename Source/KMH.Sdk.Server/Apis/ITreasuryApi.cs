using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Read + mutate the KMH treasury (player + guild vaults).
    /// </summary>
    /// <remarks>
    /// Mutations automatically push a fresh treasury snapshot to the
    /// affected user if they're online, so any in-game Treasury dialog
    /// they have open refreshes immediately. Failed mutations return
    /// false without touching state - silver/items aren't moved
    /// partially.
    /// </remarks>
    public interface ITreasuryApi
    {
        /// <summary>Current silver balance for the user's vault. 0 if they have no vault yet.</summary>
        long GetSilver(string username);

        /// <summary>
        /// Items currently in the user's vault as defName → quantity.
        /// Returned dictionary is a snapshot; the underlying store can
        /// mutate without affecting your copy.
        /// </summary>
        IReadOnlyDictionary<string, int> GetItems(string username);

        /// <summary>
        /// Last N transactions (newest last, matching the store's
        /// append order). Cap is the server-side recent-transactions
        /// retention (currently 100 - don't rely on the number).
        /// </summary>
        IReadOnlyList<TreasuryTransactionRecord> GetRecentTransactions(string username);

        /// <summary>
        /// Add silver. Note string lands in the transaction log so
        /// audit trails make sense. Returns false on invalid input
        /// (empty username, non-positive amount).
        /// </summary>
        bool DepositSilver(string username, int amount, string note = "");

        /// <summary>
        /// Take silver. Returns false if the user doesn't have enough
        /// - never goes negative.
        /// </summary>
        bool WithdrawSilver(string username, int amount, string note = "");

        /// <summary>Add an item stack to the vault.</summary>
        bool DepositItem(string username, string defName, int qty, string note = "");

        /// <summary>
        /// Take an item stack from the vault. Returns false if qty
        /// isn't available - partial withdrawals are not performed.
        /// </summary>
        bool WithdrawItem(string username, string defName, int qty, string note = "");
    }
}
