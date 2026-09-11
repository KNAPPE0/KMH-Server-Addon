using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Transactions
{
    // A crash can land between any two durable writes of a cross-store move; only two outcomes are acceptable at every boundary: settled exactly once, or still recoverably owed.
    internal static class KmhTxRecoverySelfTest
    {
        private static string User() => "selftest_tx_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // Shaped like a real buy: silver genuinely debited under the take marker, goods on the counterparty leg because they came from a listing, not a vault.
        private static KmhTransaction Escrowed(string who, long silver, string counterparty, string item, int qty)
        {
            KmhTransaction t = KmhTransaction.Create(who, "marketplace", KmhTxType.Purchase, "selftest");
            t.Destination  = "personal_treasury";
            t.EscrowSilver = silver;
            t.Advance(KmhTxState.Validating);
            t.Advance(KmhTxState.Reserved);
            if (!string.IsNullOrEmpty(counterparty))
            {
                t.CounterPlayer = counterparty;
                if (!string.IsNullOrEmpty(item)) t.CounterItems[item] = qty;
            }
            if (silver > 0 && TreasuryStore.DepositSilver(who, (int)silver, "selftest seed"))
                TreasuryStore.WithdrawSilverForTxn(who, (int)silver, t.TakeMarker, "selftest take");
            return t;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Tx recovery: a crashed Reserved move settles both sides", () =>
            {
                string who = User(), seller = User();
                KmhTransaction t = Escrowed(who, 500, seller, "Steel", 12);
                bool done = KmhTransactionRepository.Compensate(t);
                long silver = TreasuryStore.GetPersonalSilver(who);
                int steel = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(who); TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = done && t.State == KmhTxState.Refunded && silver == 500 && steel == 12;
                return (ok, ok ? "buyer's 500 back, seller's 12 steel back, marked Refunded"
                              : $"done={done}, state={t.State}, buyerSilver={silver}, sellerSteel={steel}");
            });

            Check("Item take evidence: a crash before the vault debit refunds nothing", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositItem(who, "Steel", 10, "selftest seed")) return (false, "seed");

                // The row exists and names 10 Steel, but the debit never happened - exactly the pre-take crash.
                KmhTransaction t = KmhTransactionRepository.OpenTake(who, KmhTxType.Listing, "selftest pre-take", 0, 0,
                    new Dictionary<string, int> { { "Steel", 10 } }, null);
                if (t == null) return (false, "could not open");

                bool compensated = KmhTransactionRepository.Compensate(t);
                int after = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = compensated && after == 10;
                return (ok, ok ? "vault still holds the original 10 and nothing was minted"
                              : $"compensated={compensated}, steel={after} (expected 10) - THIS DUPLICATES ITEMS");
            });

            Check("Item take evidence: a crash after the vault debit restores the lot exactly once", () =>
            {
                string who = User();
                if (!TreasuryStore.DepositItem(who, "Steel", 10, "selftest seed")) return (false, "seed");
                KmhTransaction t = KmhTransactionRepository.OpenTake(who, KmhTxType.Listing, "selftest post-take", 0, 0,
                    new Dictionary<string, int> { { "Steel", 10 } }, null);
                if (t == null) return (false, "could not open");
                if (!TreasuryStore.WithdrawItemForTxn(who, "Steel", 10, t.TakeMarker, "selftest take")) return (false, "take");
                int emptied = ItemCount(who, "Steel");

                KmhTransactionRepository.Compensate(t);
                int restored = ItemCount(who, "Steel");
                // A replayed compensation is the crash-after-credit case and must not add a second copy.
                t.State = KmhTxState.Compensating;
                KmhTransactionRepository.Compensate(t);
                int afterReplay = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = emptied == 0 && restored == 10 && afterReplay == 10;
                return (ok, ok ? "taken to 0, restored to 10, still 10 after a replay"
                              : $"afterTake={emptied}, restored={restored}, afterReplay={afterReplay}");
            });

            Check("Payload take evidence: an exact lot is restored with its state, and only once taken", () =>
            {
                string who = User();
                var gear = new Items.KmhThingPayload
                {
                    DefName = "Apparel_Parka", StuffDefName = "Cloth", Quality = 4, StackCount = 1,
                    HitPoints = 77, MaxHitPoints = 120, DisplayLabel = "parka (excellent)",
                };
                if (!TreasuryStore.DepositPayload(who, gear, "selftest seed")) return (false, "seed");
                string fingerprint = TreasuryStore.GetSnapshotFor(who)?.ItemPayloads?[0]?.Fingerprint;
                if (string.IsNullOrEmpty(fingerprint)) return (false, "no fingerprint");

                // Pre-take, on its own vault: a row naming gear with no marker must not conjure it out of nothing.
                string ghost = User();
                KmhTransaction dead = KmhTransactionRepository.PrepareTake(ghost, KmhTxType.Listing, "selftest pre-take", 0);
                KmhTransactionRepository.CommitTake(dead, new List<Items.KmhThingPayload>
                {
                    new Items.KmhThingPayload
                    {
                        DefName = "Apparel_Parka", StuffDefName = "Cloth", Quality = 4, StackCount = 1,
                        HitPoints = 77, MaxHitPoints = 120, DisplayLabel = "parka (excellent)",
                    }
                });
                KmhTransactionRepository.Compensate(dead);
                bool noMint = (TreasuryStore.GetSnapshotFor(ghost)?.ItemPayloads?.Count ?? 0) == 0;
                TreasuryStore.PurgeOwnerForTest(ghost);

                KmhTransaction t = KmhTransactionRepository.PrepareTake(who, KmhTxType.Listing, "selftest payload take", 0);
                var taken = TreasuryStore.WithdrawPayloadsForTxn(who, fingerprint, 1, t.TakeMarker, "selftest take");
                if (taken == null || taken.Count != 1) return (false, "take");
                if (!KmhTransactionRepository.CommitTake(t, taken)) return (false, "commit");
                bool emptied = (TreasuryStore.GetSnapshotFor(who)?.ItemPayloads?.Count ?? 0) == 0;

                KmhTransactionRepository.Compensate(t);
                var back = TreasuryStore.GetSnapshotFor(who)?.ItemPayloads;
                bool exact = back != null && back.Count == 1 && back[0].Quality == 4
                             && back[0].HitPoints == 77 && back[0].StuffDefName == "Cloth";
                t.State = KmhTxState.Compensating;
                KmhTransactionRepository.Compensate(t);
                int afterReplay = TreasuryStore.GetSnapshotFor(who)?.ItemPayloads?.Count ?? 0;
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = noMint && emptied && exact && afterReplay == 1;
                return (ok, ok ? "no mint without a marker; exact quality/hp/stuff restored once"
                              : $"noMint={noMint}, emptied={emptied}, exactState={exact}, afterReplay={afterReplay}");
            });

            Check("Tx recovery: replaying the refund does not pay either side twice", () =>
            {
                string who = User(), seller = User();
                KmhTransaction t = Escrowed(who, 400, seller, "Steel", 5);
                KmhTransactionRepository.Compensate(t);
                // Exactly the crash-after-credit case: the state is forced back and recovery runs the move again.
                t.State = KmhTxState.Compensating;
                bool second = KmhTransactionRepository.Compensate(t);
                long silver = TreasuryStore.GetPersonalSilver(who);
                int steel = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(who); TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = second && silver == 400 && steel == 5;
                return (ok, ok ? "still 400 silver and 5 steel after a replay"
                              : $"second={second}, buyerSilver={silver} (expected 400), sellerSteel={steel} (expected 5)");
            });

            Check("Tx recovery: silver a crash prevented from being taken is not minted back", () =>
            {
                string who = User(), seller = User();
                // The ledger records the intent first, so it can name silver the debit never actually removed.
                KmhTransaction t = Escrowed(who, 0, seller, "Steel", 3);
                t.EscrowSilver = 900;
                bool done = KmhTransactionRepository.Compensate(t);
                long silver = TreasuryStore.GetPersonalSilver(who);
                int steel = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(who); TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = done && silver == 0 && steel == 3;
                return (ok, ok ? "no silver minted, seller's goods still returned"
                              : $"done={done}, buyerSilver={silver} (expected 0) - A CRASH BEFORE THE DEBIT MINTED A REFUND, steel={steel}");
            });

            Check("Tx recovery: a refund that cannot be written leaves the value owed", () =>
            {
                string who = User();
                KmhTransaction t = Escrowed(who, 300, null, null, 0);
                bool done;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    done = KmhTransactionRepository.Compensate(t);
                long silver = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = !done && t.State != KmhTxState.Refunded && silver == 0;
                return (ok, ok ? "refused, nothing credited, still owed"
                              : $"done={done}, state={t.State}, silver={silver} - A FAILED REFUND WAS CALLED SETTLED");
            });

            Check("Tx recovery: an owed refund still settles once the disk returns", () =>
            {
                string who = User();
                KmhTransaction t = Escrowed(who, 250, null, null, 0);
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    KmhTransactionRepository.Compensate(t);
                bool afterRecovery = KmhTransactionRepository.Compensate(t);
                long silver = TreasuryStore.GetPersonalSilver(who);
                TreasuryStore.PurgeOwnerForTest(who);
                bool ok = afterRecovery && t.State == KmhTxState.Refunded && silver == 250;
                return (ok, ok ? "settled exactly once on the retry"
                              : $"done={afterRecovery}, state={t.State}, silver={silver}");
            });

            Check("Tx recovery: boot settles a Reserved move and leaves Delivered alone", () =>
            {
                bool reservedRefunds  = KmhTransactionRecovery.DecideForStuck(KmhTxState.Reserved)     == KmhTxRecoveryAction.Refund;
                bool midRefundResumes = KmhTransactionRecovery.DecideForStuck(KmhTxState.Compensating) == KmhTxRecoveryAction.Refund;
                bool deliveredWaits   = KmhTransactionRecovery.DecideForStuck(KmhTxState.Delivered)    == KmhTxRecoveryAction.Reconcile;
                bool nothingReserved  = KmhTransactionRecovery.DecideForStuck(KmhTxState.Requested)    == KmhTxRecoveryAction.Reject;
                bool ok = reservedRefunds && midRefundResumes && deliveredWaits && nothingReserved;
                return (ok, ok ? "reserved+compensating refund, delivered waits for a human"
                              : $"reserved={reservedRefunds}, compensating={midRefundResumes}, delivered={deliveredWaits}, requested={nothingReserved}");
            });

            Check("Marketplace buy: a completed purchase leaves nothing owed", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 10, "selftest seed")) return (false, "seed seller");
                if (!TreasuryStore.DepositSilver(buyer, 1000, "selftest seed")) return (false, "seed buyer");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 10, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool bought = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 4, out _, out int gotQty, out int paid);
                int pendingAfter = 0;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(buyer))
                    if (!KmhTxStateMachine.IsTerminal(t.State)) pendingAfter++;

                Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = bought && gotQty == 4 && paid == 40 && pendingAfter == 0;
                return (ok, ok ? "settled, ledger left clean"
                              : $"bought={bought}, qty={gotQty}, paid={paid}, stillPending={pendingAfter}");
            });

            Check("Marketplace buy: a purchase whose intent cannot be written charges nobody", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 6, "selftest seed")) return (false, "seed seller");
                if (!TreasuryStore.DepositSilver(buyer, 500, "selftest seed")) return (false, "seed buyer");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 6, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool bought;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    bought = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 3, out _, out _, out _);

                long buyerSilver = TreasuryStore.GetPersonalSilver(buyer);
                // The units must be back on the listing, or a refused buy quietly destroys the seller's stock.
                int remaining = Features.Marketplace.MarketplaceStore.OpenSupplyQty("Steel") > 0 ? 6 : 0;
                Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = !bought && buyerSilver == 500 && remaining == 6;
                return (ok, ok ? "refused before charging, units unreserved"
                              : $"bought={bought}, buyerSilver={buyerSilver} (expected 500), listingUnits={remaining}");
            });

            Check("Marketplace buy: the same op id cannot buy twice across a restart", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 10, "selftest seed")) return (false, "seed seller");
                if (!TreasuryStore.DepositSilver(buyer, 1000, "selftest seed")) return (false, "seed buyer");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 10, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                string op = "op-" + Guid.NewGuid().ToString("N");
                bool first  = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 3, out _, out _, out _, op);
                // The op guard is process memory; this is the path a retry takes after the server has restarted.
                bool second = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 3, out _, out _, out _, op);

                long buyerSilver = TreasuryStore.GetPersonalSilver(buyer);
                Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = first && !second && buyerSilver == 970;
                return (ok, ok ? "charged once, replay refused by the durable ledger key"
                              : $"first={first}, second={second}, buyerSilver={buyerSilver} (expected 970)");
            });

            Check("Durability floor: a buy whose delivery AND recovery both fail is still owed after a restart", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 8, "selftest seed")) return (false, "seed seller");
                if (!TreasuryStore.DepositSilver(buyer, 600, "selftest seed")) return (false, "seed buyer");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 8, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                // Before the ledger existed, a buy whose downstream steps all failed is exactly where both sides' value disappeared on restart.
                Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 4, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N"));

                KmhTransaction owed = null;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(buyer))
                    if (t.TargetId == listing) owed = t;
                bool recorded = owed != null && owed.EscrowSilver == 40 && owed.CounterPlayer == seller;

                // What boot recovery would do with it, had the process stopped before the deliveries finished.
                if (recorded && !KmhTxStateMachine.IsTerminal(owed.State))
                {
                    owed.State = KmhTxState.Reserved;
                    KmhTransactionRepository.Compensate(owed);
                }
                Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                return (recorded, recorded
                    ? "the exact owed silver and goods survive in the ledger"
                    : owed == null ? "NO DURABLE RECORD OF WHAT EACH SIDE IS OWED"
                                   : $"silver={owed.EscrowSilver} (expected 40), counterparty={owed.CounterPlayer}");
            });

            Check("Marketplace cancel: a completed cancel returns the goods and owes nothing", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 9, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 9, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool cancelled = Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                int back = ItemCount(seller, "Steel");
                int open = 0;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(seller))
                    if (!KmhTxStateMachine.IsTerminal(t.State)) open++;
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = cancelled && back == 9 && open == 0;
                return (ok, ok ? "9 back, ledger clean" : $"cancelled={cancelled}, back={back}, openOperations={open}");
            });

            Check("Marketplace cancel: a refused removal leaves the listing owning the goods, not the ledger", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 7, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 7, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool cancelled;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    cancelled = Features.Marketplace.MarketplaceStore.Cancel(seller, listing);

                KmhTransaction row = null;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(seller)) if (t.TargetId == listing) row = t;
                bool listingKept = Features.Marketplace.MarketplaceStore.OpenSupplyQty("Steel") == 7;
                // The listing still owns the units, so compensating the row must hand over nothing or the seller gets the stock twice.
                bool notOwed = row != null && KmhTxStateMachine.IsTerminal(row.State)
                            && !row.HasEscrow && !row.HasCounterEscrow;
                KmhTransactionRepository.Compensate(row);
                int credited = ItemCount(seller, "Steel");

                Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = !cancelled && listingKept && notOwed && credited == 0;
                return (ok, ok ? "listing keeps the goods, ledger owes nothing, compensation credits nothing"
                              : $"cancelled={cancelled}, listingKept={listingKept}, notOwed={notOwed}, creditedAnyway={credited}");
            });

            Check("Marketplace cancel: once the removal commits, exactly one authority owns the goods", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 6, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 6, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                // The listing is durably gone but the treasury cannot take the refund, so the ledger alone knows the units exist.
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                    Features.Marketplace.MarketplaceStore.Cancel(seller, listing);

                KmhTransaction row = null;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(seller)) if (t.TargetId == listing) row = t;
                bool listingGone = Features.Marketplace.MarketplaceStore.OpenSupplyQty("Steel") == 0;
                // The escrow fallback took the goods into a recovery hold, so exactly one authority owns them now.
                bool heldOnce = Features.Recovery.RecoveryStore.ForUser(seller).Count == 1;
                bool ledgerReleased = row != null && !row.HasCounterEscrow;

                // Compensating a row whose goods a recovery hold already owns would give the seller a second copy.
                row.State = KmhTxState.Compensating;
                KmhTransactionRepository.Compensate(row);
                int credited = ItemCount(seller, "Steel");

                Features.Recovery.RecoveryStore.ClearUser(seller);
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = listingGone && heldOnce && ledgerReleased && credited == 0;
                return (ok, ok ? "listing gone, held once in recovery, ledger no longer names the goods"
                              : $"listingGone={listingGone}, heldOnce={heldOnce}, ledgerReleased={ledgerReleased}, creditedAgain={credited}");
            });

            Check("Marketplace post: the goods are recorded before they leave the vault", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 5, "selftest seed")) return (false, "seed");
                long listing;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 5, 10, "public", 0, out _);
                // With no durable record possible, the vault must keep the goods rather than lose them in the gap.
                int kept = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = listing == 0 && kept == 5;
                return (ok, ok ? "refused, vault still owns the goods"
                              : $"listing={listing}, vaultHas={kept} (expected 5)");
            });

            Check("Marketplace post: a successful listing leaves the ledger claiming nothing", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 5, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 5, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);
                int claiming = 0;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(seller))
                    if (t.HasEscrow || t.HasCounterEscrow) claiming++;
                Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                TreasuryStore.PurgeOwnerForTest(seller);
                return (claiming == 0, claiming == 0
                    ? "listing owns the goods alone" : $"{claiming} ledger row(s) still name the listed goods");
            });

            Check("Admin cancel: a refused removal leaves the listing owning the goods", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 4, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 4, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool done;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    done = Features.Marketplace.MarketplaceStore.AdminCancelListing(listing, out _, out _);

                bool listingKept = Features.Marketplace.MarketplaceStore.OpenSupplyQty("Steel") == 4;
                int claiming = 0;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(seller))
                    if (t.TargetId == listing && (t.HasEscrow || t.HasCounterEscrow)) claiming++;
                Features.Marketplace.MarketplaceStore.AdminCancelListing(listing, out _, out _);
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = !done && listingKept && claiming == 0;
                return (ok, ok ? "listing intact, ledger claims nothing"
                              : $"done={done}, listingKept={listingKept}, ledgerClaims={claiming}");
            });

            Check("Ownership: a lot is never named by two durable authorities at once", () =>
            {
                string who = User();
                KmhTransaction t = KmhTransaction.Create(who, "probe", KmhTxType.Refund, "selftest");
                t.CounterPlayer = who;
                t.CounterItems["Steel"] = 3;
                t.Advance(KmhTxState.Validating);
                t.Advance(KmhTxState.Reserved);

                // Whichever way the row closes, it must stop naming the lot: something else owns it by then.
                KmhTransactionRepository.Settle(t);
                bool settledReleases = !t.HasCounterEscrow && !t.HasEscrow;

                KmhTransaction a = KmhTransaction.Create(who, "probe", KmhTxType.Refund, "selftest");
                a.CounterPlayer = who; a.CounterItems["Steel"] = 3;
                a.Advance(KmhTxState.Validating); a.Advance(KmhTxState.Reserved);
                KmhTransactionRepository.Abort(a);
                bool abortReleases = !a.HasCounterEscrow && !a.HasEscrow && KmhTxStateMachine.IsTerminal(a.State);
                return (settledReleases && abortReleases, settledReleases && abortReleases
                    ? "settle and abort both release the claim"
                    : $"settleReleased={settledReleases}, abortReleased={abortReleases}");
            });

            Check("Scheduler: the real sweep expires a listing and returns the goods once", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 11, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 11, 10, "public", 1, out string why);
                if (listing == 0) return (false, "post: " + why);

                // Far enough past any lifetime that the sweep must select it.
                long later = DateTime.UtcNow.Ticks + TimeSpan.FromDays(400).Ticks;
                Maintenance.ExpirySweeper.TickForTest(later);
                int back = ItemCount(seller, "Steel");
                bool gone = Features.Marketplace.MarketplaceStore.OpenSupplyQty("Steel") == 0;

                // A second tick must find nothing left to settle.
                Maintenance.ExpirySweeper.TickForTest(later);
                int afterSecond = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = gone && back == 11 && afterSecond == 11;
                return (ok, ok ? "swept once, 11 returned, second tick idle"
                              : $"listingGone={gone}, back={back}, afterSecondTick={afterSecond}");
            });

            Check("Scheduler: a frozen economy defers the sweep instead of settling", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 9, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 9, 10, "public", 1, out string why);
                if (listing == 0) return (false, "post: " + why);

                long later = DateTime.UtcNow.Ticks + TimeSpan.FromDays(400).Ticks;
                Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.OwnerMaintenance);
                Maintenance.ExpirySweeper.TickForTest(later);
                Maintenance.KmhMaintenanceGate.Release();
                bool stillListed = Features.Marketplace.MarketplaceStore.OpenSupplyQty("Steel") == 9;
                int refundedDuringFreeze = ItemCount(seller, "Steel");

                Maintenance.ExpirySweeper.TickForTest(later);
                int afterRelease = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = stillListed && refundedDuringFreeze == 0 && afterRelease == 9;
                return (ok, ok ? "deferred while frozen, settled once after"
                              : $"stillListed={stillListed}, duringFreeze={refundedDuringFreeze}, afterRelease={afterRelease}");
            });

            Check("Race: cancel and expire cannot both claim one listing", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 8, "selftest seed")) return (false, "seed");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 8, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool cancelWon = Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                bool expireWon = Features.Marketplace.MarketplaceStore.ExpireListing(listing, out _);
                int back = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = cancelWon && !expireWon && back == 8;
                return (ok, ok ? "one winner, 8 refunded once"
                              : $"cancel={cancelWon}, expire={expireWon}, refunded={back} (expected 8)");
            });

            Check("Race: expire and buy cannot both claim one listing", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 8, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(buyer, 500, "selftest seed")) return (false, "seed buyer");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 8, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool expireWon = Features.Marketplace.MarketplaceStore.ExpireListing(listing, out _);
                bool buyWon = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 4, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N"));
                int sellerBack = ItemCount(seller, "Steel");
                long buyerSilver = TreasuryStore.GetPersonalSilver(buyer);
                int buyerGoods = ItemCount(buyer, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                // The loser must move nothing at all: no charge, no goods, no second refund.
                bool ok = expireWon && !buyWon && sellerBack == 8 && buyerSilver == 500 && buyerGoods == 0;
                return (ok, ok ? "expire won, buyer untouched"
                              : $"expire={expireWon}, buy={buyWon}, sellerBack={sellerBack}, buyerSilver={buyerSilver}, buyerGoods={buyerGoods}");
            });

            Check("Race: cancel and buy cannot both claim one listing", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 8, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(buyer, 500, "selftest seed")) return (false, "seed buyer");
                long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 8, 10, "public", 0, out string why);
                if (listing == 0) return (false, "post: " + why);

                bool buyWon = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 8, out _, out _, out _, "op-" + Guid.NewGuid().ToString("N"));
                bool cancelWon = Features.Marketplace.MarketplaceStore.Cancel(seller, listing);
                int sellerGoods = ItemCount(seller, "Steel");
                int buyerGoods = ItemCount(buyer, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                // A full buy closes the listing, so the cancel must find nothing and refund nothing.
                bool ok = buyWon && !cancelWon && buyerGoods == 8 && sellerGoods == 0;
                return (ok, ok ? "buy won, cancel refunded nothing"
                              : $"buy={buyWon}, cancel={cancelWon}, buyerGoods={buyerGoods}, sellerGoods={sellerGoods}");
            });

            Check("Auction post: the goods are recorded before they leave the vault", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 6, "selftest seed")) return (false, "seed");
                long id;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    (id, _) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 6, 10, 1, 0, 1, "public");
                int kept = ItemCount(seller, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = id == 0 && kept == 6;
                return (ok, ok ? "refused, vault still owns the goods" : $"auction={id}, vaultHas={kept} (expected 6)");
            });

            Check("Auction outbid: the displaced bid is owed before the auction forgets its owner", () =>
            {
                string seller = User(), first = User(), second = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 4, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(first, 300, "selftest seed")) return (false, "seed first");
                if (!TreasuryStore.DepositSilver(second, 300, "selftest seed")) return (false, "seed second");
                var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 4, 10, 1, 0, 2, "public");
                if (id == 0) return (false, "post: " + why);

                Features.Auctions.AuctionStore.Bid(first, id, 50);
                long firstAfterBid = TreasuryStore.GetPersonalSilver(first);

                // With no way to record what the displaced bid is owed, the new bid must be refused rather than voiding the old claim.
                Features.Auctions.AuctionStore.BidResult blocked;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    blocked = Features.Auctions.AuctionStore.Bid(second, id, 100);
                bool secondUncharged = TreasuryStore.GetPersonalSilver(second) == 300;
                bool firstStillHolds = TreasuryStore.GetPersonalSilver(first) == firstAfterBid;

                // With the ledger writable the displacement happens, and the old bid comes back exactly once.
                Features.Auctions.AuctionStore.Bid(second, id, 100);
                long firstBack = TreasuryStore.GetPersonalSilver(first);
                int ledgerStillClaims = 0;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(first))
                    if (t.TargetId == id && t.HasCounterEscrow) ledgerStillClaims++;

                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(first); TreasuryStore.PurgeOwnerForTest(second);
                bool ok = !blocked.Ok && secondUncharged && firstStillHolds && firstBack == 300 && ledgerStillClaims == 0;
                return (ok, ok ? "unrecordable displacement refused; recorded one returns the bid once"
                              : $"blocked={!blocked.Ok}, secondUncharged={secondUncharged}, firstHeld={firstStillHolds}, firstBack={firstBack} (expected 300), ledgerClaims={ledgerStillClaims}");
            });

            Check("Auction settlement: the two destinations are owed independently", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 5, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(buyer, 400, "selftest seed")) return (false, "seed buyer");
                var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 5, 10, 1, 0, 2, "public");
                if (id == 0) return (false, "post: " + why);
                Features.Auctions.AuctionStore.Bid(buyer, id, 60);

                Features.Auctions.AuctionStore.SettleOutcome o = Features.Auctions.AuctionStore.SettleNow(id);
                int winnerGoods = ItemCount(buyer, "Steel");
                long sellerSilver = TreasuryStore.GetPersonalSilver(seller);
                int stillOwed = 0;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(buyer)) if (t.HasCounterEscrow) stillOwed++;
                foreach (KmhTransaction t in KmhTransactionRepository.ForPlayer(seller)) if (t.HasCounterEscrow) stillOwed++;

                // A second settle of a gone auction must move nothing at all.
                Features.Auctions.AuctionStore.SettleOutcome again = Features.Auctions.AuctionStore.SettleNow(id);
                int goodsAfter = ItemCount(buyer, "Steel");
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = o.Done && o.Sold && winnerGoods == 5 && sellerSilver > 0 && stillOwed == 0
                       && !again.Done && goodsAfter == 5;
                return (ok, ok ? "both legs settled once, replay moved nothing"
                              : $"done={o.Done}, sold={o.Sold}, goods={winnerGoods}, sellerSilver={sellerSilver}, stillOwed={stillOwed}, replayDone={again.Done}, goodsAfter={goodsAfter}");
            });

            Check("Auction settlement: the sale split is taken once, and a deferral puts its boost back", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 3, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(buyer, 300, "selftest seed")) return (false, "seed buyer");
                var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 3, 10, 1, 0, 2, "public");
                if (id == 0) return (false, "post: " + why);
                Features.Auctions.AuctionStore.Bid(buyer, id, 40);

                long poolBefore = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                // A settlement that cannot record its payout must leave the pool exactly as it found it.
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    Features.Auctions.AuctionStore.SettleNow(id);
                long poolAfterDefer = Features.Marketplace.MarketplaceStore.HousePoolBalance();

                Features.Auctions.AuctionStore.SettleNow(id);
                long sellerNet = TreasuryStore.GetPersonalSilver(seller);
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = poolAfterDefer == poolBefore && sellerNet > 0;
                return (ok, ok ? "pool untouched by the deferral, settled once after"
                              : $"poolBefore={poolBefore}, poolAfterDefer={poolAfterDefer}, sellerNet={sellerNet}");
            });

            Check("Sale split: planning moves nothing, and the boost is taken only at commit", () =>
            {
                string seller = User();
                Features.Marketplace.MarketplaceStore.TryCreditHousePool(400, "selftest seed");
                long poolSeeded = Features.Marketplace.MarketplaceStore.HousePoolBalance();

                Features.Economy.SaleSplit.Compute(seller, "Steel", 100, demandDrift: false, worldPayoutEvents: true);
                bool untouchedByPlanning = Features.Marketplace.MarketplaceStore.HousePoolBalance() == poolSeeded;

                // A boom is not reproducible offline, so the boost is set directly to exercise the commit step.
                var plan = new Features.Economy.SaleSplit { BuyerCharge = 100, SellerPayout = 150, PoolBoost = 50 };
                bool committed = plan.CommitBoost("selftest boost");
                long poolAfter = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                bool tookExactly = committed && poolAfter == poolSeeded - 50 && plan.SellerPayout == 150;

                // A pool that cannot fund the boost must rebalance rather than pay silver nobody has.
                var tooBig = new Features.Economy.SaleSplit { BuyerCharge = 100, SellerPayout = 100 + poolAfter + 999, PoolBoost = poolAfter + 999 };
                bool refused = !tooBig.CommitBoost("selftest boost too big");
                bool rebalanced = refused && tooBig.PoolBoost == 0 && tooBig.SellerPayout == 100 && tooBig.Balances;
                bool ok = untouchedByPlanning && tookExactly && rebalanced;
                return (ok, ok ? "planning inert; commit takes exactly the plan or rebalances"
                              : $"planningMovedPool={!untouchedByPlanning}, tookExactly={tookExactly}, rebalanced={rebalanced}");
            });

            Check("Sale tax: a tax leg that cannot be credited is parked, not dropped", () =>
            {
                string seller = User();
                var split = new Features.Economy.SaleSplit { BuyerCharge = 100, ServerTax = 10, SellerPayout = 90 };
                Features.Economy.SaleSplit.Unsettled left;
                bool parked;
                // Parked while the write is still failing: a crash the instant Settle returns must not lose the tax.
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                {
                    left = split.Settle(seller, "selftest sale", "selftest-key-1");
                    parked = Features.Recovery.RecoveryStore.ForUser(seller).Count == 1;
                }
                bool reported = left.ToHousePool == 10;
                Features.Recovery.RecoveryStore.ClearUser(seller);
                bool ok = reported && parked;
                return (ok, ok ? "unsettled tax reported and parked"
                              : $"reportedUnsettled={left.ToHousePool} (expected 10), parked={parked}");
            });

            Check("House pool: a fee that cannot be credited is parked to the payer, a reserve return is not dropped", () =>
            {
                string payer = User();
                bool parked;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                {
                    Features.Marketplace.MarketplaceStore.CreditFeeToHousePool(payer, 25, "selftest fee");
                    parked = Features.Recovery.RecoveryStore.ForUser(payer).Count == 1;
                }
                Features.Recovery.RecoveryStore.ClearUser(payer);

                // A reserve has no payer to park to, so it stays in the pool and rides the save retry instead.
                long before = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.MarketplaceFile ? "injected: disk full" : null))
                    Features.Marketplace.MarketplaceStore.ReturnToHousePool(40, "selftest reserve return");
                bool kept = Features.Marketplace.MarketplaceStore.HousePoolBalance() == before + 40;
                bool ok = parked && kept;
                return (ok, ok ? "fee parked to the payer; reserve return kept for the retry"
                              : $"feeParked={parked}, reserveKept={kept} ({before} -> {Features.Marketplace.MarketplaceStore.HousePoolBalance()})");
            });

            Check("Sale tax: replaying one settlement credits each destination once", () =>
            {
                string seller = User(), guild = "SelftestGuild" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string key = "selftest-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                long poolBefore  = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                long guildBefore = TreasuryStore.GetGuildSilver(guild);

                var split = new Features.Economy.SaleSplit
                    { BuyerCharge = 100, ServerTax = 10, GuildTax = 20, SellerPayout = 70, GuildName = guild };
                split.Settle(seller, "selftest replay sale", key);
                long poolOnce  = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                long guildOnce = TreasuryStore.GetGuildSilver(guild);

                // Same key again = the crash-and-replay case; neither destination may take the tax a second time.
                split.Settle(seller, "selftest replay sale", key);
                bool poolOk  = Features.Marketplace.MarketplaceStore.HousePoolBalance() == poolOnce  && poolOnce  == poolBefore + 10;
                bool guildOk = TreasuryStore.GetGuildSilver(guild)      == guildOnce && guildOnce == guildBefore + 20;
                bool ok = poolOk && guildOk;
                return (ok, ok ? "house pool and guild vault each credited once across a replay"
                              : $"pool {poolBefore}->{poolOnce}->{Features.Marketplace.MarketplaceStore.HousePoolBalance()}, " +
                                $"guild {guildBefore}->{guildOnce}->{TreasuryStore.GetGuildSilver(guild)}");
            });

            Check("Auction settlement: a settlement it cannot record does not destroy the auction", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 3, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(buyer, 300, "selftest seed")) return (false, "seed buyer");
                var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 3, 10, 1, 0, 2, "public");
                if (id == 0) return (false, "post: " + why);
                Features.Auctions.AuctionStore.Bid(buyer, id, 40);

                Features.Auctions.AuctionStore.SettleOutcome o;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    o = Features.Auctions.AuctionStore.SettleNow(id);
                // The auction is still the only owner of both lots, so it must survive to be settled later.
                bool survived = Features.Auctions.AuctionStore.EscrowValueFor(seller) > 0 || !o.Done;
                Features.Auctions.AuctionStore.SettleNow(id);
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                return (!o.Done && survived, !o.Done && survived
                    ? "deferred, auction intact" : $"done={o.Done}, survived={survived}");
            });

            Check("Auction cancel: a cancellation it cannot record leaves the auction owning the goods", () =>
            {
                string seller = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 4, "selftest seed")) return (false, "seed");
                var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 4, 10, 1, 0, 2, "public");
                if (id == 0) return (false, "post: " + why);

                bool refused; long heldDuring;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                {
                    refused = !Features.Auctions.AuctionStore.Cancel(seller, id).ok;
                    heldDuring = Features.Auctions.AuctionStore.EscrowValueFor(seller);
                }
                // Refused with the goods still on the auction, and no copy handed back beside them.
                bool notReturned = ItemCount(seller, "Steel") == 0;
                bool cancelled = Features.Auctions.AuctionStore.Cancel(seller, id).ok;
                bool returnedAfter = ItemCount(seller, "Steel") == 4;
                TreasuryStore.PurgeOwnerForTest(seller);
                bool ok = refused && heldDuring > 0 && notReturned && cancelled && returnedAfter;
                return (ok, ok ? "refused while unrecordable, goods stayed on the auction, cancelled cleanly after"
                              : $"refused={refused}, stillEscrowed={heldDuring > 0}, notReturned={notReturned}, " +
                                $"cancelledAfter={cancelled}, returnedAfter={returnedAfter}");
            });

            Check("Auction void: goods and bid are each recorded before either moves", () =>
            {
                string seller = User(), buyer = User();
                if (!TreasuryStore.DepositItem(seller, "Steel", 5, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositSilver(buyer, 300, "selftest seed")) return (false, "seed buyer");
                var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 5, 10, 1, 0, 2, "public");
                if (id == 0) return (false, "post: " + why);
                if (!Features.Auctions.AuctionStore.Bid(buyer, id, 40).Ok) return (false, "bid");
                long buyerAfterBid = SilverOf(buyer);

                Features.Auctions.AuctionStore.VoidOutcome o;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    o = Features.Auctions.AuctionStore.AdminVoid(id);
                // Neither side may be paid out from a void the ledger could not write down.
                bool nothingMoved = !o.Done && ItemCount(seller, "Steel") == 0 && SilverOf(buyer) == buyerAfterBid;

                Features.Auctions.AuctionStore.VoidOutcome after = Features.Auctions.AuctionStore.AdminVoid(id);
                bool bothBack = after.Done && ItemCount(seller, "Steel") == 5 && SilverOf(buyer) == buyerAfterBid + 40;
                TreasuryStore.PurgeOwnerForTest(seller); TreasuryStore.PurgeOwnerForTest(buyer);
                bool ok = nothingMoved && bothBack;
                return (ok, ok ? "void refused intact, then returned both lots exactly once"
                              : $"nothingMoved={nothingMoved} (done={o.Done}), bothBack={bothBack}");
            });

            Check("Tx ledger: an intent that cannot be written is not kept", () =>
            {
                KmhTransaction t = KmhTransaction.Create(User(), "marketplace", KmhTxType.Purchase, "selftest");
                t.RequestKey = "selftest-" + t.Id;
                bool added;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TransactionLedgerFile ? "injected: disk full" : null))
                    added = KmhTransactionRepository.Add(t);
                // The request key must be free again, or a retry of the same action is refused as a duplicate forever.
                bool keyFree = !KmhTransactionRepository.IsDuplicate(t.RequestKey);
                bool gone = KmhTransactionRepository.Get(t.Id) == null;
                return (!added && gone && keyFree,
                    !added && gone && keyFree ? "refused, row dropped, request key released"
                                              : $"added={added}, stillThere={!gone}, keyFree={keyFree}");
            });

            return r;
        }

        private static long SilverOf(string who) => TreasuryStore.GetSnapshotFor(who)?.SilverBalance ?? 0;

        private static int ItemCount(string who, string def)
        {
            var s = TreasuryStore.GetSnapshotFor(who);
            if (s?.Items == null) return 0;
            int n = 0;
            foreach (KeyValuePair<string, int> kv in s.Items)
                if (Util.ItemKey.Matches(kv.Key, def, "", 0)) n += kv.Value;
            return n;
        }
    }
}
