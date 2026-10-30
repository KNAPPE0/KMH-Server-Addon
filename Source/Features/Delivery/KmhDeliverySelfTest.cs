using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Features.Delivery
{
    // Transport success is not receipt: these pin each boundary where value can vanish, around the send and across a restart.
    internal static class KmhDeliverySelfTest
    {
        private static string User() => "selftest_dlv_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // The offline runner never boots KmhDataMeta; without this no id below carries the namespace the first check needs.
            string bootedIdentity = KmhServerIdentity.Id;
            if (string.IsNullOrEmpty(bootedIdentity)) KmhDataMeta.SetInstanceIdForTest("selftestsrv");

            Check("Delivery: no delivery is issued before this data set has an identity", () =>
            {
                string had = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest("");
                OutboundDelivery blank = DeliveryStore.Owe(User(), 100, null, null, "");
                KmhDataMeta.SetInstanceIdForTest(had);
                return (blank == null, blank == null
                    ? "refused while the namespace is unknown"
                    : $"issued '{blank.Id}' - AN ID EVERY SERVER WOULD ALSO MINT");
            });

            Check("Delivery A: a delivery is owed before it is ever sent", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 250, null, null, "");
                bool owedNow = d != null && DeliveryStore.OwedFor(who).Count == 1;
                return (owedNow, owedNow ? "recorded before the socket is touched" : "NOT OWED BEFORE SEND");
            });

            Check("Delivery B: an unacknowledged delivery replays after a reconnect", () =>
            {
                string who = User();
                DeliveryStore.Owe(who, 400, null, null, "");
                int first = DeliveryStore.OwedFor(who).Count;
                int again = DeliveryStore.OwedFor(who).Count;   // a reconnect asks the same question
                return (first == 1 && again == 1, $"owed {first} then {again}");
            });

            Check("Delivery D: an acknowledged delivery stops being owed, and a repeat ack is harmless", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 300, null, null, "");
                bool acked  = DeliveryStore.Ack(who, d.Id);
                bool second = DeliveryStore.Ack(who, d.Id);   // the ack the client re-sends when its own ack was lost
                int owed = DeliveryStore.OwedFor(who).Count;
                bool ok = acked && !second && owed == 0;
                return (ok, ok ? "settled once, replayed ack ignored" : $"first={acked}, second={second}, owed={owed}");
            });

            Check("Delivery E: an owed delivery survives a restart", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 175, null, null, "");
                if (d == null) return (false, "could not owe");
                DeliveryStore.ClearForTest();
                DeliveryStore.LoadFromDisk();
                List<OutboundDelivery> owed = DeliveryStore.OwedFor(who);
                bool ok = owed.Count == 1 && owed[0].Silver == 175 && owed[0].Id == d.Id;
                return (ok, ok ? "same id and amount after reload" : $"owed={owed.Count} - AN OWED DELIVERY DID NOT SURVIVE A RESTART");
            });

            Check("Delivery F: ids are namespaced by this data set, so servers cannot collide", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 120, null, null, "");
                string prefix = KmhServerIdentity.Id + ":";
                bool namespaced = d != null && d.Id.StartsWith(prefix, StringComparison.Ordinal) && d.Id.Length > prefix.Length;
                bool foreign = DeliveryStore.Ack(who, "someotherserver:" + (d?.Id ?? "").Split(':')[1]);
                bool stillOwed = DeliveryStore.OwedFor(who).Count == 1;
                bool ok = namespaced && !foreign && stillOwed;
                return (ok, ok ? "id carries this data set's identity" : $"namespaced={namespaced}, foreignAccepted={foreign}, stillOwed={stillOwed}");
            });

            Check("Delivery: a restored data set never reissues an id a client may already hold", () =>
            {
                string who = User();
                if (DeliveryStore.Owe(who, 10, null, null, "") == null) return (false, "could not owe");
                string backup = System.IO.File.ReadAllText(KmhDataPaths.DeliveryFile);
                // Issued after the backup was taken, so the client holds a receipt the restored file knows nothing of.
                OutboundDelivery held = DeliveryStore.Owe(who, 20, null, null, "");
                if (held == null) return (false, "could not owe");
                System.IO.File.WriteAllText(KmhDataPaths.DeliveryFile, backup);
                DeliveryStore.ClearForTest();
                DeliveryStore.LoadFromDisk();
                OutboundDelivery next = DeliveryStore.Owe(who, 30, null, null, "");
                bool ok = next != null && next.Id != held.Id;
                return (ok, ok ? "a fresh id after the restore"
                              : $"REISSUED '{held.Id}' - the client skips and acks it, so 30 silver never lands");
            });

            Check("Delivery: a delivery whose own write fails is never written down by another save", () =>
            {
                string mine = User(), theirs = User();
                OutboundDelivery refused, other = null;
                System.Threading.Tasks.Task concurrent = null;
                int writes = 0;
                using (JsonFileStore.FailWritesForTest(p =>
                {
                    if (p != KmhDataPaths.DeliveryFile || System.Threading.Interlocked.Increment(ref writes) != 1) return null;
                    // Another delivery is saved while this one's write is still in flight.
                    concurrent = System.Threading.Tasks.Task.Run(() => other = DeliveryStore.Owe(theirs, 40, null, null, ""));
                    concurrent.Wait(1000);
                    return "injected: disk full";
                }))
                {
                    refused = DeliveryStore.Owe(mine, 500, null, null, "");
                    concurrent?.Wait(5000);
                }
                // The crash: only what reached the disk comes back.
                DeliveryStore.ClearForTest();
                DeliveryStore.LoadFromDisk();
                bool ghost = DeliveryStore.OwedFor(mine).Count > 0;
                bool otherKept = other != null && DeliveryStore.OwedFor(theirs).Count == 1;
                bool ok = refused == null && !ghost && otherKept;
                return (ok, ok ? "the refused delivery never reached the disk"
                              : $"refused={refused == null}, owedAfterRestart={ghost} (REFUNDED AND STILL REPLAYED), otherKept={otherKept}");
            });

            Check("Delivery: two callers settling one operation at once mint one delivery", () =>
            {
                string who = User(), op = "selftest-op-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                OutboundDelivery first, second = null;
                System.Threading.Tasks.Task racer = null;
                int entered = 0;
                DeliveryStore.AfterOweOnceMissForTest = () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref entered, 1) != 0) return;
                    // The second caller asks while the first has looked, found nothing, and not yet minted.
                    racer = System.Threading.Tasks.Task.Run(() => second = DeliveryStore.OweOnce(who, 75, op));
                    racer.Wait(1000);
                };
                try { first = DeliveryStore.OweOnce(who, 75, op); racer?.Wait(5000); }
                finally { DeliveryStore.AfterOweOnceMissForTest = null; }
                int owed = DeliveryStore.OwedFor(who).Count;
                bool ok = first != null && second != null && first.Id == second.Id && owed == 1;
                return (ok, ok ? "the second caller got the first one's delivery"
                              : $"first={first?.Id ?? "null"}, second={second?.Id ?? "null"}, owed={owed} - ONE OPERATION PAID {owed} TIMES");
            });

            Check("Delivery: a grant carrying more than one kind of value is refused, not half-landed", () =>
            {
                string who = User();
                OutboundDelivery twoItems = DeliveryStore.Owe(who, 0, new Dictionary<string, int> { { "Steel", 10 }, { "Plasteel", 5 } }, null, "");
                OutboundDelivery mixed = DeliveryStore.Owe(who, 50, new Dictionary<string, int> { { "Steel", 10 } }, null, "");
                OutboundDelivery empty = DeliveryStore.Owe(who, 0, null, null, "");
                OutboundDelivery one = DeliveryStore.Owe(who, 0, new Dictionary<string, int> { { "Steel", 10 } }, null, "");
                bool ok = twoItems == null && mixed == null && empty == null && one != null && DeliveryStore.OwedFor(who).Count == 1;
                return (ok, ok ? "each delivery is exactly one grant message"
                              : $"twoItems={twoItems != null}, silverAndItem={mixed != null}, empty={empty != null}, single={one != null}");
            });

            // Owed deliveries must not replay before the report that resets a new save, or old-save withdrawals land in it.
            (bool replayed, int owedAfter) Join(string who, string savedAs, string joinsWith, bool reconnect = false)
            {
                Economy.EconomyConfig cfg = KmhDeepCopy.Of(Economy.EconomyConfig.Current);
                cfg.ResetEconomyOnNewSave = true;
                Economy.EconomyConfig had = Economy.EconomyConfig.ApplyForTest(cfg);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                bool grant = false;
                try
                {
                    Economy.KmhEconomyReset.ResetForTest();
                    Economy.EconomyResetStore.Forget(who);
                    // A first sighting records the save without a reset, which would hold off the next one for hours.
                    Economy.EconomyResetStore.ShouldReset(who, savedAs, out _);
                    if (DeliveryStore.Owe(who, 250, null, null, "") == null) return (false, -1);
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = who });
                    ServerClient grantedTo = null;
                    SubProtocol.KmhRouter.OutboundForTest = (cl, kind, data) => { if (kind == SubProtocol.KmhProtocol.Kind.TreasuryGrant) { grant = true; grantedTo = cl; } };
                    // The link has handshaken but has not reported: nothing may be replayed yet.
                    bool early = DeliveryHandler.SaveKnownOn(c);
                    PlayerStats.PlayerStatsHandler.ColonyReportForTest(c, SubProtocol.KmhEnvelope.ParseForTest(new { save_id = joinsWith }));
                    if (reconnect)
                    {
                        // A new link inside the report rate limit: its first report must still release the replay.
                        grant = false;
                        var again = new ServerClient(null, null, createListener: false);
                        again.GetData<UserFile>(new UserFile { Username = who });
                        PlayerStats.PlayerStatsHandler.ColonyReportForTest(again, SubProtocol.KmhEnvelope.ParseForTest(new { save_id = joinsWith }));
                        grant = grant && ReferenceEquals(grantedTo, again);
                    }
                    return (grant && !early, DeliveryStore.OwedFor(who).Count);
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    Economy.EconomyConfig.ApplyForTest(had);
                    DeliveryStore.PurgeUser(who);
                    Treasury.TreasuryStore.PurgeOwnerForTest(who);
                    Economy.EconomyResetStore.Forget(who);
                    Economy.KmhEconomyReset.ResetForTest();
                }
            }

            Check("Delivery: a new save's first report resets what the old save was owed, and nothing is replayed into it", () =>
            {
                (bool replayed, int owed) = Join(User(), "save-a", "save-b");
                string main = Maintenance.KmhSecurityAuditSelfTest.ReadSource(System.IO.Path.Combine("SubProtocol", "KmhHandshakeHandler.cs")) ?? "";
                bool gated = main.Contains("Features.Delivery.DeliveryHandler.SaveKnownOn(client))");
                bool ok = !replayed && owed == 0 && gated;
                return (ok, ok ? "reset first, nothing replayed, and the handshake waits for the report"
                              : $"replayedIntoNewSave={replayed}, stillOwed={owed}, handshakeGated={gated}");
            });

            Check("Delivery: the same save's first report replays what it is owed", () =>
            {
                (bool replayed, int owed) = Join(User(), "save-a", "save-a");
                bool ok = replayed && owed == 1;
                return (ok, ok ? "replayed once the save was known" : $"replayed={replayed}, owed={owed}");
            });

            Check("Delivery: a reconnect inside the report rate limit still gets its replay", () =>
            {
                (bool replayed, int owed) = Join(User(), "save-a", "save-a", reconnect: true);
                bool ok = replayed && owed == 1;
                return (ok, ok ? "the new link's first report replayed it" : $"replayedOnTheNewLink={replayed}, owed={owed}");
            });

            // A bare grant has no record to replay, so it is gone if the link drops or the drop fails.
            Check("Delivery: site output sent to the colony is owed until the colony confirms it", () =>
            {
                string who = User();
                var c = new ServerClient(null, null, createListener: false);
                c.GetData<UserFile>(new UserFile { Username = who });
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                string sentId = null;
                try
                {
                    Sites.SiteStore.OnlineClientForTest = u => string.Equals(u, who, StringComparison.OrdinalIgnoreCase) ? c : null;
                    SubProtocol.KmhRouter.OutboundForTest = (cl, kind, data) =>
                    {
                        if (kind == SubProtocol.KmhProtocol.Kind.TreasuryGrant)
                            sentId = Newtonsoft.Json.Linq.JObject.FromObject(data)["delivery_id"]?.ToString();
                    };
                    int landed = Sites.SiteStore.DeliverForTest(who, 1234, "Steel", 25);
                    List<OutboundDelivery> owed = DeliveryStore.OwedFor(who);
                    bool ok = landed == 25 && owed.Count == 1 && !string.IsNullOrEmpty(sentId) && owed[0].Id == sentId;
                    return (ok, ok ? "sent with its delivery id and owed until acked"
                                  : $"landed={landed}, owed={owed.Count}, sentId={sentId ?? "none"} - A BARE GRANT HAS NO RECORD TO REPLAY");
                }
                finally
                {
                    Sites.SiteStore.OnlineClientForTest = null;
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    DeliveryStore.PurgeUser(who);
                }
            });

            Check("Delivery: one player cannot acknowledge another's", () =>
            {
                string mine = User(), theirs = User();
                OutboundDelivery d = DeliveryStore.Owe(mine, 90, null, null, "");
                bool stolen = DeliveryStore.Ack(theirs, d.Id);
                bool stillOwed = DeliveryStore.OwedFor(mine).Count == 1;
                return (!stolen && stillOwed, !stolen && stillOwed
                    ? "ack is bound to the owner" : $"stolen={stolen}, stillOwed={stillOwed}");
            });

            Check("Delivery: a delivery that cannot be written down is not handed out", () =>
            {
                string who = User();
                OutboundDelivery d;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.DeliveryFile ? "injected: disk full" : null))
                    d = DeliveryStore.Owe(who, 500, null, null, "");
                bool nothingOwed = DeliveryStore.OwedFor(who).Count == 0;
                return (d == null && nothingOwed, d == null && nothingOwed
                    ? "refused, caller keeps the value" : $"record={(d == null ? "null" : d.Id)}, owed={!nothingOwed}");
            });

            Check("Link D: an economic commit with an unacked delivery is not finished", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 200, null, null);
                if (d == null) return (false, "could not open the operation");
                KmhTransaction t = null;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who)) if (x.DeliveryId == d.Id) t = x;
                bool linked = t != null && t.State == KmhTxState.Delivered;
                // Terminal-looking is not finished while the colony has not confirmed holding the value.
                bool notFinished = t != null && !KmhTxPrune.IsFinished(t);
                bool ok = linked && notFinished;
                return (ok, ok ? "Delivered, linked, and not prunable"
                              : $"state={t?.State.ToString() ?? "none"}, finished={!notFinished}");
            });

            Check("Link E/F: the ack finalises the operation, and only then may it be pruned", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 150, null, null);
                if (d == null) return (false, "could not open the operation");
                KmhTransaction t = null;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who)) if (x.DeliveryId == d.Id) t = x;
                bool heldBefore = t != null && !KmhTxPrune.IsFinished(t);

                DeliveryStore.Ack(who, d.Id);
                KmhTransactionRepository.ConfirmDelivered(d.Id);
                bool confirmed = t != null && t.State == KmhTxState.Confirmed;
                bool prunable  = t != null && KmhTxPrune.IsFinished(t);
                // A repeat of the whole settle must not move it anywhere else.
                KmhTransactionRepository.ConfirmDelivered(d.Id);
                bool stable = t != null && t.State == KmhTxState.Confirmed;
                bool ok = heldBefore && confirmed && prunable && stable;
                return (ok, ok ? "confirmed once on ack, then prunable"
                              : $"heldBefore={heldBefore}, confirmed={confirmed}, prunable={prunable}, stable={stable}");
            });

            Check("Link F: a settled operation is still retained while its delivery is owed", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 210, null, null);
                if (d == null) return (false, "could not open the operation");
                KmhTransaction t = null;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who)) if (x.DeliveryId == d.Id) t = x;
                if (t == null) return (false, "operation not linked");

                // Terminal without the client confirming: dropping the row now orphans a delivery still being replayed.
                t.State = KmhTxState.Refunded;
                bool retained = !KmhTxPrune.IsFinished(t);
                DeliveryStore.Ack(who, d.Id);
                bool prunableAfterAck = KmhTxPrune.IsFinished(t);
                bool ok = retained && prunableAfterAck;
                return (ok, ok ? "kept while owed, prunable once settled"
                              : $"retainedWhileOwed={retained}, prunableAfterAck={prunableAfterAck}");
            });

            Check("Link: a delivery that cannot be recorded does not leave an operation half-open", () =>
            {
                string who = User();
                OutboundDelivery d;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.DeliveryFile ? "injected: disk full" : null))
                    d = DeliveryStore.OweForOperation(who, KmhTxType.Withdrawal, "selftest", 320, null, null);
                int stillOpen = 0;
                foreach (KmhTransaction x in KmhTransactionRepository.ForPlayer(who))
                    if (!KmhTxStateMachine.IsTerminal(x.State)) stillOpen++;
                return (d == null && stillOpen == 0, d == null && stillOpen == 0
                    ? "refused and the operation was compensated"
                    : $"delivery={(d == null ? "null" : d.Id)}, openOperations={stillOpen}");
            });

            Check("Delivery: an ack the disk refuses leaves the delivery owed", () =>
            {
                string who = User();
                OutboundDelivery d = DeliveryStore.Owe(who, 60, null, null, "");
                bool acked;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.DeliveryFile ? "injected: disk full" : null))
                    acked = DeliveryStore.Ack(who, d.Id);
                bool stillOwed = DeliveryStore.OwedFor(who).Count == 1;
                return (!acked && stillOwed, !acked && stillOwed
                    ? "still owed until the ack is durable" : $"acked={acked}, stillOwed={stillOwed}");
            });

            return r;
        }
    }
}
