using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Items;

namespace KMHServerAddon.Maintenance
{
    // Instances exist only after leaving the vault, so their row is written second; these crash between the writes.
    internal static class KmhPayloadTakeSelfTest
    {
        private static KmhThingPayload Sword(string fingerprint)
            => new KmhThingPayload
            {
                DefName = "MeleeWeapon_LongSword", StuffDefName = "Plasteel", Quality = 5,
                StackCount = 1, HitPoints = 180, MaxHitPoints = 200,
                ScribeXml = "<thing><def>MeleeWeapon_LongSword</def></thing>",
                Fingerprint = fingerprint,
            };

        private static int PayloadCount(string who)
            => TreasuryStore.GetSnapshotFor(who)?.ItemPayloads?.Count ?? 0;

        private static int PendingTakeCount(string who) => TreasuryStore.PendingTakesFor(who).Count;

        private static List<KmhThingPayload> Take(string who, out Transactions.KmhTransaction post)
        {
            post = Transactions.KmhTransactionRepository.PrepareTake(who, Transactions.KmhTxType.Listing, "selftest post", 0);
            return TreasuryStore.WithdrawPayloadsForTxn(
                who, "fp1", 1, post.TakeMarker, note: "selftest escrow", refundMarker: post.RefundMarker);
        }

        private static bool PendingOnDisk(string takeMarker)
        {
            string json = System.IO.File.ReadAllText(Persistence.KmhDataPaths.TreasuryFile);
            foreach (Newtonsoft.Json.Linq.JToken m in Newtonsoft.Json.Linq.JToken.Parse(json).SelectTokens("$..pending_takes[*].marker"))
                if ((string)m == takeMarker) return true;
            return false;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            string Who() => "selftest_take_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            Check("Payload take: the vault records what left, in the commit that removed it", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    List<KmhThingPayload> escrow = Take(who, out Transactions.KmhTransaction post);
                    if (escrow == null || escrow.Count != 1) return (false, "withdraw returned nothing");

                    // The row is deliberately NOT written: this is the instant the process is allowed to die.
                    Features.Treasury.Dto.PendingTake held =
                        TreasuryStore.PendingTakesFor(who).Find(p => p.TakeMarker == post.TakeMarker);
                    bool gone   = PayloadCount(who) == 0;
                    bool named  = held != null && held.Payloads.Count == 1;
                    // Reconstructing from a def name would lose exactly this, which is why the take is recorded whole.
                    bool intact = named && held.Payloads[0].Quality == 5 && held.Payloads[0].HitPoints == 180
                                  && held.Payloads[0].StuffDefName == "Plasteel"
                                  && !string.IsNullOrEmpty(held.Payloads[0].ScribeXml);
                    bool ok = gone && named && intact;
                    return (ok, ok ? "the vault holds the exact instances under the take marker"
                                  : $"leftVault={gone}, recorded={named}, stateIntact={intact}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: a crash before the row is written returns the goods, once", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    Take(who, out _);

                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    bool back    = PayloadCount(who) == 1;
                    bool cleared = PendingTakeCount(who) == 0;
                    var restored = TreasuryStore.GetSnapshotFor(who)?.ItemPayloads;
                    bool intact  = back && restored[0].Quality == 5 && restored[0].HitPoints == 180;

                    // Recovery runs on every boot, and a second one must not mint a second sword.
                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    bool stillOne = PayloadCount(who) == 1;

                    bool ok = back && cleared && intact && stillOne;
                    return (ok, ok ? "returned with its state, the take forgotten, and idempotent on re-run"
                                  : $"returned={back}, cleared={cleared}, stateIntact={intact}, notDoubled={stillOne}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: once a row names the take, recovery hands back nothing", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    List<KmhThingPayload> escrow = Take(who, out Transactions.KmhTransaction post);
                    if (!Transactions.KmhTransactionRepository.CommitTake(post, escrow)) return (false, "CommitTake");

                    // Row landed, vault entry not cleared: returning them too is how one crash becomes two swords.
                    bool stillHeld = PendingTakeCount(who) == 1;
                    Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
                    bool notReturned = PayloadCount(who) == 0;
                    bool cleared     = PendingTakeCount(who) == 0;

                    Transactions.KmhTransactionRepository.Settle(post);
                    bool ok = stillHeld && notReturned && cleared;
                    return (ok, ok ? "the row owns them; the vault dropped its claim without paying"
                                  : $"heldBeforeRecovery={stillHeld}, notReturned={notReturned}, cleared={cleared}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            // Reverting an unsaved clear to pending lets recovery hand back goods the buyer kept once its row is pruned.
            Check("Payload take: a clear the vault cannot save stays cleared, and the save retry writes it", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    List<KmhThingPayload> escrow = Take(who, out Transactions.KmhTransaction post);
                    if (!Transactions.KmhTransactionRepository.CommitTake(post, escrow)) return (false, "CommitTake");

                    bool cleared;
                    using (Persistence.JsonFileStore.FailWritesForTest(p => p == Persistence.KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                        cleared = TreasuryStore.ClearPendingTake(who, post.TakeMarker);
                    bool clearedInMemory = PendingTakeCount(who) == 0;
                    bool pendingOnDisk = PendingOnDisk(post.TakeMarker);
                    TreasuryStore.SaveToDisk();
                    bool writtenByRetry = !PendingOnDisk(post.TakeMarker);
                    Transactions.KmhTransactionRepository.Settle(post);
                    bool ok = cleared && clearedInMemory && pendingOnDisk && writtenByRetry;
                    return (ok, ok ? "cleared now, written by the next save"
                                  : $"reportedCleared={cleared}, clearedInMemory={clearedInMemory}, stillOnDiskAfterFailure={pendingOnDisk}, writtenByRetry={writtenByRetry}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload take: the inline refund and recovery cannot both pay it back", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Sword("fp1"), "selftest")) return (false, "deposit");
                    List<KmhThingPayload> escrow = Take(who, out Transactions.KmhTransaction post);

                    TreasuryStore.ReturnPendingTake(who, post.TakeMarker, post.RefundMarker, escrow,
                                                    "selftest inline refund");
                    bool backOnce = PayloadCount(who) == 1;

                    // Recovery replays the same goods on the same refund key, which must not pay them out again.
                    TreasuryStore.DepositEscrowOnce(who, post.RefundMarker, 0, null, escrow, "selftest replay");
                    bool stillOnce = PayloadCount(who) == 1;

                    bool ok = backOnce && stillOnce;
                    return (ok, ok ? "the refund marker is the single key, so the goods return exactly once"
                                  : $"returned={backOnce}, notDoubled={stillOnce} ({PayloadCount(who)} in vault)");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            // Two genepacks, two crated buildings or a biocoded and a plain gun agree on every field but the blob.
            KmhThingPayload Pack(string fp, string gene) => new KmhThingPayload
            {
                DefName = "Genepack", StackCount = 1, Quality = -1, HitPoints = 100, MaxHitPoints = 100, Fingerprint = fp,
                ScribeXml = "<kmhthing><t><def>Genepack</def><genes><li>" + gene + "</li></genes></t></kmhthing>",
            };
            string Genes(List<KmhThingPayload> l)
            {
                var g = new List<string>();
                foreach (KmhThingPayload p in l ?? new List<KmhThingPayload>()) g.Add(p.ScribeXml.Contains("Robust") ? "Robust" : "Delicate");
                return string.Join(",", g);
            }

            Check("Payload rows: items differing only in saved state are separate rows, and a take of 2 from one takes only it", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Pack("fp-robust", "Gene_Robust"), "selftest")
                        || !TreasuryStore.DepositPayload(who, Pack("fp-delicate", "Gene_Delicate"), "selftest")) return (false, "deposit");
                    int rows = PayloadCount(who);
                    List<KmhThingPayload> got = TreasuryStore.WithdrawPayloads(who, "fp-robust", 2, "selftest");
                    string left = Genes(TreasuryStore.StoredPayloadsForTest(who));
                    bool ok = rows == 2 && Genes(got) == "Robust" && left == "Delicate";
                    return (ok, $"rows={rows} took='{Genes(got)}' left='{left}'");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload rows: items that differ only in their own thing id share a row, and a take of 2 takes both", () =>
            {
                string who = Who();
                KmhThingPayload Twin(string fp, string id) => new KmhThingPayload
                {
                    DefName = "Genepack", StackCount = 1, Quality = -1, HitPoints = 100, MaxHitPoints = 100, Fingerprint = fp,
                    ScribeXml = "<kmhthing><t><def>Genepack</def><id>" + id + "</id><genes><li>Gene_Robust</li></genes></t></kmhthing>",
                };
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Twin("fp-twin-a", "Genepack101"), "selftest")
                        || !TreasuryStore.DepositPayload(who, Twin("fp-twin-b", "Genepack102"), "selftest")) return (false, "deposit");
                    int rows = PayloadCount(who);
                    List<KmhThingPayload> got = TreasuryStore.WithdrawPayloads(who, "fp-twin-a", 2, "selftest");
                    bool ok = rows == 1 && got != null && got.Count == 2;
                    return (ok, $"rows={rows} took={got?.Count ?? 0}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Payload rows: a client-chosen duplicate fingerprint cannot pull a different item into a take", () =>
            {
                string who = Who();
                try
                {
                    if (!TreasuryStore.DepositPayload(who, Pack("fp-same", "Gene_Robust"), "selftest")
                        || !TreasuryStore.DepositPayload(who, Pack("fp-same", "Gene_Delicate"), "selftest")) return (false, "deposit");
                    List<KmhThingPayload> got = TreasuryStore.WithdrawPayloads(who, "fp-same", 2, "selftest");
                    string left = Genes(TreasuryStore.StoredPayloadsForTest(who));
                    bool ok = Genes(got) == "Robust" && left == "Delicate";
                    return (ok, $"took='{Genes(got)}' left='{left}'");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            return r;
        }
    }
}
