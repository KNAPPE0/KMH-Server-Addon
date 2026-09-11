using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read and mutate the treasury: player and guild vaults.</summary>
    /// <remarks>
    /// A mutation pushes a fresh snapshot to the affected user if they are online, so you never have to. A failed
    /// mutation returns false without touching state - nothing moves partially.
    /// </remarks>
    public interface ITreasuryApi
    {
        /// <summary>Current silver balance for the user's vault. 0 if they have no vault yet.</summary>
        long GetSilver(string username);

        /// <summary>Items in the user's vault as defName → quantity, as a snapshot the store cannot mutate.</summary>
        IReadOnlyDictionary<string, int> GetItems(string username);

        /// <summary>Recent transactions, newest last, capped by the server's retention setting.</summary>
        IReadOnlyList<TreasuryTransactionRecord> GetRecentTransactions(string username);

        /// <summary>Add silver, recording the note in the transaction log. False on an empty user or non-positive amount.</summary>
        bool DepositSilver(string username, int amount, string note = "");

        /// <summary>Take silver. Returns false if the user does not have enough; a vault never goes negative.</summary>
        bool WithdrawSilver(string username, int amount, string note = "");

        /// <summary>Add an item stack to the vault.</summary>
        bool DepositItem(string username, string defName, int qty, string note = "");

        /// <summary>Take an item stack. Returns false if the quantity is not available; there are no partial withdrawals.</summary>
        bool WithdrawItem(string username, string defName, int qty, string note = "");
    }
}
