using System.Collections.Generic;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Transactions
{
    // The row naming the instances is written after the take, so at boot the row decides ownership - never a guess about where the crash landed.
    internal static class KmhPayloadTakeRecovery
    {
        public static void RecoverOnBoot()
        {
            List<Features.Treasury.Dto.PendingTake> pending = Features.Treasury.TreasuryStore.AllPendingTakes();
            if (pending.Count == 0) return;

            var named = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (KmhTransaction t in KmhTransactionRepository.All())
                if (!string.IsNullOrEmpty(t?.TakeMarker)) named.Add(t.TakeMarker);

            int returned = 0, adopted = 0, stuck = 0;
            foreach (Features.Treasury.Dto.PendingTake p in pending)
            {
                if (named.Contains(p.TakeMarker))
                {
                    // A row names these, so the row owns them; handing them back too turns one crash into two copies.
                    if (Features.Treasury.TreasuryStore.ClearPendingTake(p.Username, p.TakeMarker)) adopted++;
                    else stuck++;
                    continue;
                }
                if (Features.Treasury.TreasuryStore.ReturnPendingTake(
                        p.Username, p.TakeMarker, p.RefundMarker, p.Payloads,
                        "returned: the post that took these was never recorded"))
                    returned++;
                else stuck++;
            }

            ServerLog.Warn($"Payload take recovery: {pending.Count} take(s) open from a previous run - " +
                           $"{returned} returned to their owner, {adopted} already recorded by a transaction, {stuck} unresolved.");
            if (stuck > 0)
                ServerLog.Error($"Payload take recovery: {stuck} take(s) could not be settled - the vault would not " +
                                "accept the write. Fix the persistence problem and restart; nothing was lost.");
        }
    }
}
