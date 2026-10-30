using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Features.Mail
{
    // Cuts the power on each side of the payout write, where a crash can strand goods in no inbox, vault or row.
    internal static class KmhMailRestartSelfTest
    {
        private const string Steel = "Steel";
        private const int Qty = 7;
        private const int AttachSilver = 400;

        private static string User(string tag) => $"selftest_mr{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static int ItemsOf(string user)
        {
            int n = 0;
            var snap = TreasuryStore.GetSnapshotFor(user);
            if (snap?.Items != null)
                foreach (KeyValuePair<string, int> kv in snap.Items)
                    if (Util.ItemKey.Matches(kv.Key, Steel, "", 0)) n += kv.Value;
            return n;
        }

        private static (long Silver, int Items) Holds(string user)
        {
            long s = 0; int i = 0;
            foreach (Recovery.Dto.RecoveryRecord rec in Recovery.RecoveryStore.ForUser(user))
            {
                if (rec == null || rec.Status == "resolved") continue;
                if (rec.Kind == "silver") s += rec.Silver;
                else if (rec.Payload != null) i += rec.Payload.StackCount;
            }
            return (s, i);
        }

        // Every place mail value can legitimately be, including a payout written down but not yet paid.
        private static (long Silver, int Items) Total(string a, string b)
        {
            (long ah, int ai) = Holds(a); (long bh, int bi) = Holds(b);
            (long esc, int escN) = MailStore.EscrowedTotalsForTest();
            (long owed, int owedN) = MailStore.PendingPayoutTotalsForTest();
            return (TreasuryStore.GetPersonalSilver(a) + TreasuryStore.GetPersonalSilver(b)
                        + Marketplace.MarketplaceStore.HousePoolBalance() + ah + bh + esc + owed,
                    ItemsOf(a) + ItemsOf(b) + ai + bi + escN + owedN);
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            (bool ok, string detail) AtPhase(string phase, string action, bool recover)
            {
                string from = User("f"), to = User("t");
                try
                {
                    if (!TreasuryStore.DepositSilver(from, 5000, "selftest seed")) return (false, "seed sender silver");
                    if (!TreasuryStore.DepositItem(from, Steel, Qty, "selftest seed")) return (false, "seed sender items");

                    long id = MailStore.Send(from, to, "s", "b", AttachSilver,
                                             new Dictionary<string, int> { [Steel] = Qty }, null,
                                             out _, out _, out string why);
                    if (id == 0) return (false, "send: " + why);

                    (long silver0, int items0) = Total(from, to);

                    MailStore.AtPayoutPhaseForTest = p =>
                    {
                        if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                    };
                    try
                    {
                        switch (action)
                        {
                            case "claim":   MailStore.Claim(to, id, out _, out _, out _); break;
                            case "recall":  MailStore.Recall(from, id, out _, out _, out _); break;
                            case "decline": MailStore.Decline(to, id, out _, out _, out _); break;
                            case "discard": MailStore.Delete(to, id, out _, out _); break;
                            // Past the window and never read, which is the only shape the sweep returns.
                            case "sweep":
                                MailStore.SweepUnclaimedAttachments(
                                    DateTime.UtcNow.Ticks + TimeSpan.FromDays(3650).Ticks);
                                break;
                        }
                    }
                    catch (OperationCanceledException) { }
                    finally { MailStore.AtPayoutPhaseForTest = null; }

                    if (recover) MailStore.RecoverPendingPayouts();

                    (long silver1, int items1) = Total(from, to);
                    bool ok = silver1 == silver0 && items1 == items0;
                    return (ok, ok
                        ? $"conserved: silver {silver1}, items {items1}"
                          + (recover ? $", {MailStore.PendingPayoutCountForTest} payout(s) still owed" : "")
                        : $"silver {silver0} -> {silver1} ({silver1 - silver0:+#;-#;0}), "
                          + $"items {items0} -> {items1} ({items1 - items0:+#;-#;0})");
                }
                finally
                {
                    MailStore.AtPayoutPhaseForTest = null;
                    MailStore.PurgeUser(from);
                    MailStore.PurgeUser(to);
                    MailStore.RecoverPendingPayouts();
                    TreasuryStore.PurgeOwnerForTest(from);
                    TreasuryStore.PurgeOwnerForTest(to);
                    Recovery.RecoveryStore.ClearUser(from);
                    Recovery.RecoveryStore.ClearUser(to);
                }
            }

            void Case(string phase, string action, bool recover, string what)
            {
                string name = $"Mail restart: {action} interrupted {what}"
                            + (recover ? ", then recovered" : "");
                try { (bool ok, string detail) = AtPhase(phase, action, recover); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            foreach (string action in new[] { "claim", "recall", "decline", "discard", "sweep" })
            {
                Case("payout-recorded", action, false, "after the attachment closed, before the credit");
                Case("payout-recorded", action, true,  "after the attachment closed, before the credit");
                Case("payout-credited", action, true,  "after the credit, before the record was cleared");
            }

            // A second recovery pass must be a no-op, or the marker is not what makes the payout exactly once.
            r.Add(ReplayIsFree());
            return r;
        }

        private static (string, bool, string) ReplayIsFree()
        {
            string from = User("rf"), to = User("rt");
            const string Name = "Mail restart: recovery replayed twice pays once";
            try
            {
                if (!TreasuryStore.DepositSilver(from, 5000, "selftest seed")) return (Name, false, "seed sender silver");
                if (!TreasuryStore.DepositItem(from, Steel, Qty, "selftest seed")) return (Name, false, "seed sender items");
                long id = MailStore.Send(from, to, "s", "b", AttachSilver,
                                         new Dictionary<string, int> { [Steel] = Qty }, null,
                                         out _, out _, out string why);
                if (id == 0) return (Name, false, "send: " + why);

                MailStore.AtPayoutPhaseForTest = p =>
                {
                    if (p == "payout-recorded") throw new OperationCanceledException("selftest");
                };
                try { MailStore.Claim(to, id, out _, out _, out _); }
                catch (OperationCanceledException) { }
                finally { MailStore.AtPayoutPhaseForTest = null; }

                if (MailStore.PendingPayoutCountForTest == 0)
                    return (Name, false, "nothing was written down, so there is no replay to test");

                MailStore.RecoverPendingPayouts();
                long silverOnce = TreasuryStore.GetPersonalSilver(to);
                int itemsOnce = ItemsOf(to);
                MailStore.RecoverPendingPayouts();
                MailStore.RecoverPendingPayouts();

                bool ok = TreasuryStore.GetPersonalSilver(to) == silverOnce && ItemsOf(to) == itemsOnce
                       && silverOnce >= AttachSilver && itemsOnce >= Qty;
                return (Name, ok, ok
                    ? $"paid once: {silverOnce} silver, {itemsOnce} units, three passes"
                    : $"after three passes: {TreasuryStore.GetPersonalSilver(to)} silver, {ItemsOf(to)} units "
                      + $"(one pass gave {silverOnce}/{itemsOnce})");
            }
            catch (Exception ex) { return (Name, false, $"threw: {ex.Message}"); }
            finally
            {
                MailStore.AtPayoutPhaseForTest = null;
                MailStore.PurgeUser(from);
                MailStore.PurgeUser(to);
                MailStore.RecoverPendingPayouts();
                TreasuryStore.PurgeOwnerForTest(from);
                TreasuryStore.PurgeOwnerForTest(to);
                Recovery.RecoveryStore.ClearUser(from);
                Recovery.RecoveryStore.ClearUser(to);
            }
        }
    }
}
