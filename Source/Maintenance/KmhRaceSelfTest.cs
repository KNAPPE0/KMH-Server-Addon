using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Quests;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;

namespace KMHServerAddon.Maintenance
{
    // A cross-store escrow releases the store lock, so two requests both pass one cap check unless it is retaken at the insert.
    internal static class KmhRaceSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Quests: two posts racing for the last slot commit exactly one", () =>
            {
                string who = "selftest_race_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                QuestsConfig saved = QuestsConfig.Current;
                try
                {
                    // Cap of 1 with nothing open: both posts see a free slot before either inserts.
                    QuestsConfig.ApplyForTest(new QuestsConfig { MaxOpenPerUser = 1 });
                    const int bounty = 500;
                    if (!TreasuryStore.DepositSilver(who, bounty * 2, "selftest seed")) return (false, "could not seed the vault");

                    long[] ids = new long[2];
                    using (Barrier gate = new Barrier(2))
                    {
                        Task<long> Post() => Task.Run(() =>
                        {
                            QuestEntry draft = new QuestEntry
                            {
                                Kind = QuestEntry.KindDeliverItem, Visibility = QuestEntry.VisibilityPublic,
                                Title = "selftest race", Description = "probe",
                                BountySilver = bounty, TargetItemDefName = "Steel", TargetItemQty = 1,
                            };
                            gate.SignalAndWait();   // both threads enter PostDraft together
                            return QuestStore.PostDraft(who, draft, 1, out _);
                        });
                        Task<long> a = Post(), b = Post();
                        Task.WaitAll(a, b);
                        ids[0] = a.Result; ids[1] = b.Result;
                    }

                    int committed = (ids[0] > 0 ? 1 : 0) + (ids[1] > 0 ? 1 : 0);
                    int open = QuestStore.OpenCountForTest(who);
                    long silver = TreasuryStore.GetPersonalSilver(who);

                    foreach (long id in ids) if (id > 0) QuestStore.Cancel(who, id);
                    TreasuryStore.PurgeOwnerForTest(who);

                    // The loser's bounty must come back whole - a refused post that keeps the escrow is value loss.
                    bool refunded = silver == bounty;
                    return (committed == 1 && open == 1 && refunded,
                        committed == 1 && open == 1 && refunded
                            ? "one quest committed, the other's bounty returned in full"
                            : $"committed={committed}, open={open}, silver left={silver} (expected {bounty} refunded)");
                }
                finally { QuestsConfig.ApplyForTest(saved); }
            });

            return r;
        }
    }
}
