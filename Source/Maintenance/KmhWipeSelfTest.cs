using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Economy;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Maintenance
{
    // A destructive cleanup must destroy exactly what it claims, survive a restart, and hand no value back.
    internal static class KmhWipeSelfTest
    {
        private static string User() => "selftest_wipe_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Wipe: a pending transaction cannot refund value the wipe destroyed", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositItem(who, "Steel", 12, "selftest seed")) return (false, "seed");

                KmhTransaction open = KmhTransactionRepository.OpenTake(who, KmhTxType.Listing, "selftest open", 0, 0,
                    new Dictionary<string, int> { { "Steel", 12 } }, null);
                if (open == null) return (false, "could not open");
                if (!TreasuryStore.WithdrawItemForTxn(who, "Steel", 12, open.TakeMarker, "selftest take")) return (false, "take");

                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                KmhEconomyReset.Run(who, "");

                // Boot recovery after a wipe must not treat the old row as owed - the goods were destroyed on purpose.
                KmhTransactionRepository.Compensate(open);
                int steel = ItemCount(who, "Steel");
                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = steel == 0;
                return (ok, ok ? "the wipe stands; the closed row returned nothing"
                              : $"steelBack={steel} (expected 0) - A WIPE WAS UNDONE BY COMPENSATION");
            });

            Check("Wipe: a payout written down but not yet paid is not paid into the new save", () =>
            {
                string who = User();
                long quest = -Math.Abs(Guid.NewGuid().GetHashCode()) - 1;
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                try
                {
                    PlantPending(who, quest);
                    if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                    KmhEconomyReset.Run(who, "");
                    RecoverPending();
                    long silver = TreasuryStore.GetPersonalSilver(who);
                    int steel = ItemCount(who, "Steel");
                    bool ok = silver == 0 && steel == 0;
                    return (ok, ok ? "all three went with the wipe" : $"{silver} silver and {steel} steel landed in the new save - THE WIPE WAS UNDONE AT BOOT");
                }
                finally
                {
                    DropPending(who);
                    TreasuryStore.PurgeOwnerForTest(who);
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                }
            });

            Check("Wipe: a boot that finds a wipe unfinished drops what it owed that player instead of paying it", () =>
            {
                string who = User();
                long quest = -Math.Abs(Guid.NewGuid().GetHashCode()) - 1;
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                try
                {
                    // Begun and not yet applied: what a crash between the two leaves for the next boot.
                    if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                    PlantPending(who, quest);
                    RecoverPending();
                    long silver = TreasuryStore.GetPersonalSilver(who);
                    int steel = ItemCount(who, "Steel");
                    bool ok = silver == 0 && steel == 0;
                    return (ok, ok ? "nothing paid while the wipe is unfinished" : $"{silver} silver and {steel} steel paid into a vault the wipe may already have emptied");
                }
                finally
                {
                    DropPending(who);
                    TreasuryStore.PurgeOwnerForTest(who);
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                }
            });

            // Refunding an innocent high bid before the purge is saved lets a crash in between refund it twice.
            foreach (string phase in new[] { "purged", "saved", "refund-credited" })
            {
                string name = $"Wipe: a bid on a purged auction is refunded once after a crash ({phase})";
                string seller = User(), bidder = User();
                Check(name, () =>
                {
                    try
                    {
                        if (!TreasuryStore.DepositItem(seller, "Steel", 4, "selftest seed") || !TreasuryStore.DepositSilver(bidder, 300, "selftest seed"))
                            return (false, "seed");
                        var (id, why) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 4, 10, 1, 0, 2, "public");
                        if (id == 0) return (false, "post: " + why);
                        if (!Features.Auctions.AuctionStore.Bid(bidder, id, 40).Ok) return (false, "bid");
                        Features.Auctions.AuctionStore.AtPurgePhaseForTest = p => { if (p == phase) throw new OperationCanceledException("selftest: process died at " + p); };
                        try { Features.Auctions.AuctionStore.PurgeUser(seller); }
                        catch (OperationCanceledException) { }
                        finally { Features.Auctions.AuctionStore.AtPurgePhaseForTest = null; }
                        // The restart: the auctions come back as last written, the purge resumes, then boot settles refunds.
                        Features.Auctions.AuctionStore.LoadFromDisk();
                        Features.Auctions.AuctionStore.PurgeUser(seller);
                        Features.Auctions.AuctionStore.SettlePendingRefunds();
                        long silver = TreasuryStore.GetPersonalSilver(bidder);
                        bool ok = silver == 300 && !Features.Auctions.AuctionStore.Exists(id);
                        return (ok, ok ? "the 40 came back once" : $"bidder has {silver} of 300"
                                       + (silver > 300 ? " - REFUNDED TWICE" : silver < 300 ? " - THE BID WAS LOST" : "")
                                       + (Features.Auctions.AuctionStore.Exists(id) ? ", auction still open" : ""));
                    }
                    finally
                    {
                        Features.Auctions.AuctionStore.AtPurgePhaseForTest = null;
                        Features.Auctions.AuctionStore.DropPendingRefundsFor(bidder);
                        TreasuryStore.PurgeOwnerForTest(seller);
                        TreasuryStore.PurgeOwnerForTest(bidder);
                    }
                });
            }

            Check("Wipe: an unacked delivery does not hand the value back on the next join", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(who, 250, "selftest seed")) return (false, "seed");
                // Run alone, nothing has named this data set, and a delivery is refused without that namespace.
                // Named for this check only: later suites must see the identity the run started with.
                bool named = string.IsNullOrEmpty(KmhServerIdentity.Id);
                if (named) KmhDataMeta.SetInstanceIdForTest("selftestsrv");
                try
                {
                    var owed = Features.Delivery.DeliveryStore.Owe(who, 250, null, null, "selftest delivery");
                    if (owed == null) return (false, "could not owe");
                    bool owedBefore = Features.Delivery.DeliveryStore.OwedFor(who).Count == 1;

                    if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                    KmhEconomyReset.Run(who, "");

                    bool owedAfter = Features.Delivery.DeliveryStore.OwedFor(who).Count == 0;
                    TreasuryStore.PurgeOwnerForTest(who);
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                    bool ok = owedBefore && owedAfter;
                    return (ok, ok ? "the owed delivery went with the wipe"
                                  : $"owedBefore={owedBefore}, clearedByWipe={owedAfter}");
                }
                finally { if (named) KmhDataMeta.SetInstanceIdForTest(""); }
            });

            // A stalled reset has not purged them yet, and a join in that window would hand the old save's goods to the new one.
            Check("Wipe: deliveries owed to the old save are not replayed while its reset is unfinished", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                bool named = string.IsNullOrEmpty(KmhServerIdentity.Id);
                if (named) KmhDataMeta.SetInstanceIdForTest("selftestsrv");
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                try
                {
                    if (Features.Delivery.DeliveryStore.Owe(who, 250, null, null, "selftest delivery") == null) return (false, "could not owe");
                    if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = who });
                    int sent = 0;
                    SubProtocol.KmhRouter.OutboundForTest = (cl, kind, data) => { if (cl == c) sent++; };
                    Features.Delivery.DeliveryHandler.ReplayOwed(c);
                    int sentMidReset = sent;
                    KmhEconomyReset.Run(who, "");
                    bool purged = Features.Delivery.DeliveryStore.OwedFor(who).Count == 0;
                    bool ok = sentMidReset == 0 && purged;
                    return (ok, $"sentWhileResetting={sentMidReset} (want 0) purgedByTheReset={purged}");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    Features.Delivery.DeliveryStore.PurgeUser(who);
                    TreasuryStore.PurgeOwnerForTest(who);
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                    if (named) KmhDataMeta.SetInstanceIdForTest("");
                }
            });

            Check("Wipe: the mailbox goes, and a third party's attachment goes home rather than burning", () =>
            {
                string who = User(), friend = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(friend, 400, "selftest seed")) return (false, "seed friend");

                long id = Features.Mail.MailStore.Send(friend, who, "hi", "body", 300, null, null,
                                                       out _, out _, out string sendWhy);
                if (id <= 0) return (false, "send: " + sendWhy);
                long friendAfterSend = TreasuryStore.GetPersonalSilver(friend);
                bool inboxSeeded = Features.Mail.MailStore.CountFor(who) == 1;

                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);
                KmhEconomyReset.Run(who, "");

                bool inboxGone = Features.Mail.MailStore.CountFor(who) == 0;
                // The silver was never the wiped player's, so destroying it would take a third party's value.
                bool returned = TreasuryStore.GetPersonalSilver(friend) == friendAfterSend + 300;
                TreasuryStore.PurgeOwnerForTest(who); TreasuryStore.PurgeOwnerForTest(friend);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = inboxSeeded && inboxGone && returned;
                return (ok, ok ? "mailbox removed, the sender's escrow returned to them"
                              : $"seeded={inboxSeeded}, inboxGone={inboxGone}, senderRefunded={returned}");
            });

            Check("Wipe: interrupted part-way, one resume produces exactly one complete wipe", () =>
            {
                string who = User();
                KmhEconomyReset.ResetForTest();
                EconomyResetStore.Forget(who);
                if (!TreasuryStore.DepositSilver(who, 700, "selftest seed")) return (false, "seed");
                if (!TreasuryStore.DepositItem(who, "Steel", 9, "selftest seed")) return (false, "seed items");
                if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Player, out string why)) return (false, "begin: " + why);

                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.EconomyResetFile ? "injected: disk full" : null))
                    KmhEconomyReset.Run(who, "");
                bool stillOpen = KmhEconomyReset.InProgress;
                bool barred = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                KmhEconomyReset.RecoverOnBoot();
                var snap = TreasuryStore.GetSnapshotFor(who);
                bool empty = TreasuryStore.GetPersonalSilver(who) == 0
                          && (snap?.Items == null || snap.Items.Count == 0);
                bool finished = !KmhEconomyReset.InProgress;
                bool released = KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);

                // A second boot must not start the wipe again on a player who has since earned something back.
                if (!TreasuryStore.DepositSilver(who, 50, "post-wipe earnings")) return (false, "reseed");
                KmhEconomyReset.RecoverOnBoot();
                bool keptEarnings = TreasuryStore.GetPersonalSilver(who) == 50;

                TreasuryStore.PurgeOwnerForTest(who);
                EconomyResetStore.Forget(who);
                KmhEconomyReset.ResetForTest();
                bool ok = stillOpen && barred && finished && empty && released && keptEarnings;
                return (ok, ok ? "stayed open and barred, resumed once, no second wipe on the next boot"
                              : $"stayedOpen={stillOpen}, barred={barred}, finished={finished}, emptied={empty}, " +
                                $"released={released}, keptPostWipeEarnings={keptEarnings}");
            });

            Check("Wipe scope: an economy wipe keeps identity, a player wipe removes it", () =>
            {
                string eco = User(), full = User();
                KmhEconomyReset.ResetForTest();
                Features.PlayerStats.PlayerStatsStore.EnsurePlayer(eco);
                Features.PlayerStats.PlayerStatsStore.EnsurePlayer(full);

                if (!KmhEconomyReset.Begin(eco, "", KmhResetScope.Economy, out string w1)) return (false, "begin eco: " + w1);
                KmhEconomyReset.Run(eco, "");
                bool ecoKeptStats = Features.PlayerStats.PlayerStatsStore.HasPlayer(eco);

                if (!KmhEconomyReset.Begin(full, "", KmhResetScope.Player, out string w2)) return (false, "begin full: " + w2);
                KmhEconomyReset.Run(full, "");
                bool fullLostStats = !Features.PlayerStats.PlayerStatsStore.HasPlayer(full);

                Features.PlayerStats.PlayerStatsStore.RemoveUser(eco);
                EconomyResetStore.Forget(eco); EconomyResetStore.Forget(full);
                KmhEconomyReset.ResetForTest();
                bool ok = ecoKeptStats && fullLostStats;
                return (ok, ok ? "a new-save reset keeps standings; an admin player wipe does not"
                              : $"economyKeptStats={ecoKeptStats}, playerWipeRemovedStats={fullLostStats}");
            });

            // "verify: OK" over a reset still open would tell the admin it is done while the economy stays paused.
            Check("Wipe: a cleanup whose reset is still open says so, never 'verify: OK'", () =>
            {
                string who = User();
                var said = new List<string>();
                KmhEconomyReset.ResetForTest();
                try
                {
                    if (!KmhEconomyReset.Begin(who, "", KmhResetScope.Economy, out string why)) return (false, "begin: " + why);
                    bool reportedDone = KmhPlayerCleanup.FinishForTest(who, said.Add);
                    bool saidOpen = said.Exists(l => l.Contains("NOT FINISHED")) && !said.Exists(l => l.Contains("verify: OK"));
                    bool ok = !reportedDone && saidOpen;
                    return (ok, $"reportedDone={reportedDone} saidNotFinished={saidOpen}");
                }
                finally
                {
                    EconomyResetStore.Forget(who);
                    KmhEconomyReset.ResetForTest();
                }
            });

            // An Owner cannot leave a populated guild, so a wipe that only tried to leave kept them in charge of it.
            Check("Wipe: a guild Owner's wipe passes the guild to its senior member, and the others keep it", () =>
            {
                string owner = User(), officer = User(), member = User();
                string guild = "selftest wipe " + Guid.NewGuid().ToString("N").Substring(0, 6);
                KmhEconomyReset.ResetForTest();
                try
                {
                    if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "guild: " + why);
                    if (!Features.Guilds.GuildStore.AddMember(member, guild)
                        || !Features.Guilds.GuildStore.AddMember(officer, guild, Features.Guilds.Dto.GuildMemberDto.RankOfficer))
                        return (false, "members");
                    if (!KmhEconomyReset.Begin(owner, "", KmhResetScope.Player, out why)) return (false, "begin: " + why);
                    KmhEconomyReset.Run(owner, "");
                    bool finished = !KmhEconomyReset.InProgress;
                    bool gone = string.IsNullOrEmpty(Features.Guilds.GuildStore.CurrentGuildOf(owner));
                    string heir = Features.Guilds.GuildStore.RankOf(officer);
                    bool kept = string.Equals(Features.Guilds.GuildStore.CurrentGuildOf(member), guild, StringComparison.OrdinalIgnoreCase);
                    bool ok = finished && gone && heir == Features.Guilds.Dto.GuildMemberDto.RankOwner && kept;
                    return (ok, $"finished={finished} ownerGone={gone} officerNow={heir} memberKept={kept}");
                }
                finally
                {
                    if (KmhEconomyReset.InProgress) KmhEconomyReset.ResetForTest();
                    Features.Guilds.GuildStore.Leave(member, out _);
                    Features.Guilds.GuildStore.Leave(officer, out _);
                    Features.Guilds.GuildStore.Leave(owner, out _);
                    TreasuryStore.PurgeOwnerForTest(guild);
                    EconomyResetStore.Forget(owner);
                    KmhEconomyReset.ResetForTest();
                }
            });

            return r;
        }

        private static void PlantPending(string who, long quest)
        {
            Features.Mail.MailStore.PlantPendingPayoutForTest(who, 300);
            Features.World.WorldStore.PlantPendingPayoutForTest(quest, who, 200);
            Features.Marketplace.MarketplaceStore.PlantPendingReserveForTest(who, "Steel", 7);
        }

        private static void RecoverPending()
        {
            Features.Mail.MailStore.RecoverPendingPayouts();
            Features.World.WorldEngine.ResumePendingPayouts();
            Features.Marketplace.MarketplaceStore.RecoverPendingReserves();
        }

        private static void DropPending(string who)
        {
            Features.Mail.MailStore.DropPendingPayoutsFor(who);
            Features.World.WorldStore.DropPendingPayoutsFor(who);
            Features.Marketplace.MarketplaceStore.DropPendingReservesFor(who);
        }

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
