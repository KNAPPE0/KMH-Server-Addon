using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Delivery;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Items;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;
using KMHServerAddon.Transactions;

namespace KMHServerAddon.Features.Guilds
{
    // A withdrawal writes its row, takes the goods on that row's marker from the incarnation it checked and owes a
    // delivery; recovery gives an undelivered take back to the guild exactly once.
    internal static class KmhGuildVaultItemSelfTest
    {
        private const string Steel = "Steel";

        private static string User(string tag) => $"selftest_gv{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private static string Name() => "selftest gv " + Guid.NewGuid().ToString("N").Substring(0, 6);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            // A delivery is namespaced by the data-set identity, and a filtered run boots without one.
            string hadIdentity = KmhServerIdentity.Id;
            if (string.IsNullOrEmpty(hadIdentity)) KmhDataMeta.SetInstanceIdForTest("selftestsrv");
            try { Cases(r); }
            finally { if (string.IsNullOrEmpty(hadIdentity)) KmhDataMeta.SetInstanceIdForTest(hadIdentity); }
            return r;
        }

        private static void Cases(List<(string, bool, string)> r)
        {
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.GetType().Name}: {ex.Message}")); }
                finally
                {
                    GuildStore.AfterItemWithdrawCheckForTest = null;
                    GuildStore.AtItemWithdrawPhaseForTest = null;
                    DeliveryStore.AtWithdrawPhaseForTest = null;
                    GuildHandler.ClearCooldownsForTest();
                }
            }

            (string Owner, string Guild, string Id) Seed(int steel)
            {
                string o = User("o"), g = Name();
                if (!GuildStore.CreateGuildAndJoinAsAdmin(o, g, out string why)) throw new InvalidOperationException("guild: " + why);
                string id = GuildIdentity.Of(g);
                if (steel > 0 && !TreasuryStore.DepositGuildItem(g, Steel, steel, "selftest seed", id)) throw new InvalidOperationException("seed vault");
                return (o, g, id);
            }
            void Drop(string g, params string[] users)
            {
                foreach (string u in users)
                {
                    KmhTransactionRepository.AbortAllFor(u);
                    DeliveryStore.PurgeUser(u);
                    GuildStore.Leave(u, out _);
                    TreasuryStore.PurgeOwnerForTest(u);
                }
                TreasuryStore.PurgeOwnerForTest(g);
            }
            int Vault(string g) => TreasuryStore.GuildItemCount(g, Steel);
            int Owed(string u)
            {
                int n = 0;
                foreach (OutboundDelivery d in DeliveryStore.OwedFor(u))
                {
                    foreach (KeyValuePair<string, int> kv in d.Items) n += kv.Value;
                    foreach (KmhThingPayload p in d.Payloads) n += p.StackCount;
                }
                return n;
            }
            KmhThingPayload Medicine(int units) => new KmhThingPayload
            { DefName = "MedicineIndustrial", StackCount = units, HitPoints = 100, MaxHitPoints = 100, Quality = -1 };

            Check("Guild vault: the guild snapshot carries the vault's goods, full-state stacks without their saved state", () =>
            {
                (string o, string g, string id) = Seed(50);
                try
                {
                    KmhThingPayload p = Medicine(30);
                    p.ScribeXml = "";
                    if (!TreasuryStore.ReturnGuildGoodsOnce(g, id, "gv-seed:" + g, null, new[] { p }, "selftest seed")) return (false, "seed payload");
                    GuildSnapshot s = GuildStore.BuildEnvelopeFor(o).Guild;
                    int items = s?.GuildItems != null && s.GuildItems.TryGetValue(Steel, out int n) ? n : -1;
                    int loads = s?.GuildPayloads?.Count ?? -1;
                    bool stripped = s?.GuildPayloads != null && s.GuildPayloads.TrueForAll(x => string.IsNullOrEmpty(x.ScribeXml));
                    bool ok = items == 50 && loads == 1 && s.GuildPayloads[0].StackCount == 30 && stripped;
                    return (ok, $"items={items} payload rows={loads} stripped={stripped}");
                }
                finally { Drop(g, o); }
            });

            Check("Guild vault: a rank whose daily cap is 0 is refused, and nothing leaves the vault or is owed", () =>
            {
                (string o, string g, _) = Seed(50);
                string m = User("m");
                try
                {
                    if (!GuildStore.AddMember(m, g)) return (false, "member");
                    var res = GuildStore.WithdrawItemFromGuild(m, Steel, null, 10, default, out string why, out OutboundDelivery d);
                    bool ok = res == GuildStore.ItemWithdrawOutcome.Refused && d == null && Vault(g) == 50 && Owed(m) == 0
                              && (why ?? "").IndexOf("rank", StringComparison.OrdinalIgnoreCase) >= 0;
                    return (ok, $"outcome={res} ({why}) vault={Vault(g)} owed={Owed(m)}");
                }
                finally { Drop(g, m, o); }
            });

            Check("Guild vault: a withdrawal is owed as a colony delivery while its member is offline, and replays once when they join", () =>
            {
                (string o, string g, _) = Seed(50);
                var hadOut = KmhRouter.OutboundForTest;
                try
                {
                    var res = GuildStore.WithdrawItemFromGuild(o, Steel, null, 20, default, out string why, out OutboundDelivery d);
                    int owedOffline = Owed(o);
                    int grants = 0, granted = 0;
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = o });
                    KmhRouter.OutboundForTest = (cl, kind, data) =>
                    {
                        if (cl != c || kind != KmhProtocol.Kind.TreasuryGrant) return;
                        grants++;
                        var j = Newtonsoft.Json.Linq.JObject.FromObject(data);
                        granted += (int?)j["amount"] ?? 0;
                    };
                    DeliveryHandler.ReplayOwed(c);
                    bool acked = d != null && DeliveryStore.Ack(o, d.Id);
                    if (acked) KmhTransactionRepository.ConfirmDelivered(d.Id);
                    bool ok = res == GuildStore.ItemWithdrawOutcome.Delivered && Vault(g) == 30 && owedOffline == 20
                              && grants == 1 && granted == 20 && acked && Owed(o) == 0;
                    return (ok, $"outcome={res} ({why}) vault={Vault(g)} owedWhileOffline={owedOffline} replayed={grants}x{granted} acked={acked} owedAfter={Owed(o)}");
                }
                finally { KmhRouter.OutboundForTest = hadOut; Drop(g, o); }
            });

            Check("Guild vault: one withdrawal request delivered twice on its op id moves the goods once", () =>
            {
                (string o, string g, _) = Seed(50);
                var hadOut = KmhRouter.OutboundForTest;
                var enforcement = Features.Enforcement.EnforcementConfig.Current;
                bool hadEnforcement = enforcement.Enabled, hadGuilds = FeaturesConfig.Current.Guilds;
                try
                {
                    enforcement.Enabled = false;
                    FeaturesConfig.Current.Guilds = true;
                    if (!KmhRouter.IsRegistered(KmhProtocol.Kind.GuildWithdrawItem)) GuildHandler.Register();
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = o });
                    KmhHandshakeHandler.MarkCompatible(c);
                    int grants = 0;
                    KmhRouter.OutboundForTest = (cl, kind, data) => { if (cl == c && kind == KmhProtocol.Kind.TreasuryGrant) grants++; };
                    string wire = new KmhEnvelope(KmhProtocol.Kind.GuildWithdrawItem, new { item_def_name = Steel, qty = 15 })
                                  { OpId = "gv-op-" + Guid.NewGuid().ToString("N") }.Serialize();
                    KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(wire));
                    GuildHandler.ClearCooldownsForTest();
                    KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(wire));
                    bool ok = Vault(g) == 35 && Owed(o) == 15 && DeliveryStore.OwedFor(o).Count == 1 && grants == 1;
                    return (ok, $"vault={Vault(g)} owed={Owed(o)} deliveries={DeliveryStore.OwedFor(o).Count} grants={grants}");
                }
                finally
                {
                    enforcement.Enabled = hadEnforcement;
                    FeaturesConfig.Current.Guilds = hadGuilds;
                    KmhRouter.OutboundForTest = hadOut;
                    Drop(g, o);
                }
            });

            Check("Guild vault: a withdrawal checked against one incarnation takes nothing from the guild re-created under its name", () =>
            {
                (string o, string g, _) = Seed(50);
                string heir = User("h");
                try
                {
                    GuildStore.AfterItemWithdrawCheckForTest = name =>
                    {
                        if (name != g) return;
                        GuildStore.Leave(o, out _);
                        if (!GuildStore.CreateGuildAndJoinAsAdmin(heir, g, out _)) return;
                        TreasuryStore.DepositGuildItem(g, Steel, 50, "selftest successor", GuildIdentity.Of(g));
                    };
                    var res = GuildStore.WithdrawItemFromGuild(o, Steel, null, 20, default, out string why, out OutboundDelivery d);
                    bool ok = res != GuildStore.ItemWithdrawOutcome.Delivered && d == null && Vault(g) == 50 && Owed(o) == 0;
                    return (ok, $"outcome={res} ({why}) successor vault={Vault(g)} owed={Owed(o)}");
                }
                finally { Drop(g, heir, o); }
            });

            Check("Guild vault: the vault refuses a take for an incarnation that no longer holds the name, whatever the caller checked", () =>
            {
                (string o, string g, string oldId) = Seed(0);
                string heir = User("h");
                try
                {
                    GuildStore.Leave(o, out _);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(heir, g, out string why)) return (false, "recreate: " + why);
                    TreasuryStore.DepositGuildItem(g, Steel, 50, "selftest successor", GuildIdentity.Of(g));
                    bool stale = TreasuryStore.WithdrawGuildItemForTxn(g, oldId, Steel, 20, "gv-stale:" + g, o, "selftest");
                    bool live = TreasuryStore.WithdrawGuildItemForTxn(g, GuildIdentity.Of(g), Steel, 5, "gv-live:" + g, heir, "selftest");
                    bool again = TreasuryStore.WithdrawGuildItemForTxn(g, GuildIdentity.Of(g), Steel, 5, "gv-live:" + g, heir, "selftest");
                    bool ok = !stale && live && again && Vault(g) == 45;
                    return (ok, $"staleIdentityTook={stale} liveTook={live} replayOnMarker={again} vault={Vault(g)} (want 45: once)");
                }
                finally { Drop(g, heir, o); }
            });

            Check("Guild vault: a rank lowered between the checks and the take is refused, and nothing leaves the vault", () =>
            {
                (string o, string g, _) = Seed(50);
                string a = User("a");
                try
                {
                    if (!GuildStore.AddMember(a, g, GuildMemberDto.RankAdmin)) return (false, "admin");
                    GuildStore.AfterItemWithdrawCheckForTest = name => { if (name == g) GuildStore.Demote(o, a); };
                    var res = GuildStore.WithdrawItemFromGuild(a, Steel, null, 20, default, out string why, out OutboundDelivery d);
                    bool ok = res == GuildStore.ItemWithdrawOutcome.Refused && d == null && Vault(g) == 50 && Owed(a) == 0;
                    return (ok, $"outcome={res} ({why}) vault={Vault(g)} owed={Owed(a)}");
                }
                finally { Drop(g, a, o); }
            });

            foreach (string phase in new[] { "taken", "owed" })
            {
                Check($"Guild vault restart: a withdrawal cut short after '{phase}' gives the goods back to the guild or owes them - once", () =>
                {
                    (string o, string g, _) = Seed(50);
                    try
                    {
                        void Die(string p) { if (p == phase) throw new OperationCanceledException("selftest: process died at " + p); }
                        GuildStore.AtItemWithdrawPhaseForTest = Die;
                        DeliveryStore.AtWithdrawPhaseForTest = Die;
                        try { GuildStore.WithdrawItemFromGuild(o, Steel, null, 20, default, out _, out _); }
                        catch (OperationCanceledException) { }
                        GuildStore.AtItemWithdrawPhaseForTest = null;
                        DeliveryStore.AtWithdrawPhaseForTest = null;
                        int vaultAtCrash = Vault(g);

                        TreasuryStore.LoadFromDisk();
                        KmhTransactionRepository.LoadFromDisk();
                        DeliveryStore.LoadFromDisk();
                        KmhTransactionRepository.RecoverPendingForTest(o);
                        int vault1 = Vault(g), owed1 = Owed(o);
                        KmhTransactionRepository.RecoverPendingForTest(o);
                        int vault2 = Vault(g), owed2 = Owed(o);

                        bool shape = phase == "taken" ? vault1 == 50 && owed1 == 0 : vault1 == 30 && owed1 == 20;
                        bool ok = vaultAtCrash == 30 && shape && vault2 == vault1 && owed2 == owed1;
                        return (ok, $"vault at crash={vaultAtCrash}, after recovery={vault1} owed={owed1}, after a second pass={vault2} owed={owed2}");
                    }
                    finally { Drop(g, o); }
                });
            }

            Check("Guild vault: a full-state stack is withdrawn by its row, split where it can be, and owed with its state", () =>
            {
                (string o, string g, string id) = Seed(0);
                try
                {
                    if (!TreasuryStore.ReturnGuildGoodsOnce(g, id, "gv-seed:" + g, null, new[] { Medicine(30) }, "selftest seed")) return (false, "seed payload");
                    string fp = TreasuryStore.GuildGoodsFor(g).Payloads[0].Fingerprint;
                    var res = GuildStore.WithdrawItemFromGuild(o, null, fp, 10, default, out string why, out OutboundDelivery d);
                    int left = TreasuryStore.GuildGoodsFor(g).Payloads.Count == 1 ? TreasuryStore.GuildGoodsFor(g).Payloads[0].StackCount : -1;
                    int owed = d?.Payloads == null ? -1 : KmhPayloadEscrow.TotalUnits(d.Payloads);
                    bool ok = res == GuildStore.ItemWithdrawOutcome.Delivered && left == 20 && owed == 10 && d.Payloads[0].DefName == "MedicineIndustrial";
                    return (ok, $"outcome={res} ({why}) left in vault={left} owed={owed}");
                }
                finally { Drop(g, o); }
            });

            Check("Guild vault: a full-state take whose row no longer matches the vault takes nothing", () =>
            {
                (string o, string g, string id) = Seed(0);
                try
                {
                    if (!TreasuryStore.ReturnGuildGoodsOnce(g, id, "gv-seed:" + g, null, new[] { Medicine(30) }, "selftest seed")) return (false, "seed payload");
                    string fp = TreasuryStore.GuildGoodsFor(g).Payloads[0].Fingerprint;
                    bool raced = false;
                    // Another take lands between the plan the row is written for and the take itself.
                    GuildStore.AfterItemWithdrawCheckForTest = name =>
                    {
                        if (name != g) return;
                        List<KmhThingPayload> other = TreasuryStore.PlanGuildPayloadTake(g, fp, 25);
                        raced = TreasuryStore.TakeGuildPayloadsForTxn(g, id, fp, 25, other, "gv-race:" + g, o, "selftest race") != null;
                    };
                    var res = GuildStore.WithdrawItemFromGuild(o, null, fp, 10, default, out string why, out OutboundDelivery d);
                    var left = TreasuryStore.GuildGoodsFor(g).Payloads;
                    int units = left.Count == 1 ? left[0].StackCount : -1;
                    bool ok = raced && res == GuildStore.ItemWithdrawOutcome.Refused && d == null && units == 5 && Owed(o) == 0;
                    return (ok, $"raced={raced} outcome={res} ({why}) left={units} owed={Owed(o)}");
                }
                finally { Drop(g, o); }
            });

            Check("Guild vault: goods count toward a capped rank's daily withdraw cap at their value", () =>
            {
                (string o, string g, _) = Seed(200);
                string m = User("m");
                try
                {
                    long unit = Math.Max(1L, KmhItemSafety.GetTrustedMarketValue(Steel));
                    if (!GuildStore.SaveSettings(o, new GuildSettingsDto { MemberDailyWithdrawCap = (int)(unit * 100) })) return (false, "cap");
                    if (!GuildStore.AddMember(m, g)) return (false, "member");
                    var over = GuildStore.WithdrawItemFromGuild(m, Steel, null, 150, default, out string whyOver, out _);
                    var first = GuildStore.WithdrawItemFromGuild(m, Steel, null, 60, default, out string whyFirst, out _);
                    var second = GuildStore.WithdrawItemFromGuild(m, Steel, null, 60, default, out string whySecond, out _);
                    bool ok = over == GuildStore.ItemWithdrawOutcome.Refused && first == GuildStore.ItemWithdrawOutcome.Delivered
                              && second == GuildStore.ItemWithdrawOutcome.Refused && Vault(g) == 140 && Owed(m) == 60;
                    return (ok, $"150 over the cap={over} ({whyOver}); 60={first}; 60 more={second} ({whySecond}); vault={Vault(g)} owed={Owed(m)}");
                }
                finally { Drop(g, m, o); }
            });

            Check("Guild vault: goods in the vault count toward its members' off-map wealth share", () =>
            {
                (string o, string g, string id) = Seed(0);
                const string gem = "KmhSelftestVaultGem";
                try
                {
                    Features.ItemLabels.ItemLabelCache.ApplyValues(new Dictionary<string, long> { [gem] = 25 });
                    long unit = KmhItemSafety.GetTrustedMarketValue(gem);
                    long before = GuildStore.VaultShareFor(o);
                    if (!TreasuryStore.DepositGuildItem(g, gem, 4, "selftest seed", id)) return (false, "seed");
                    long after = GuildStore.VaultShareFor(o);
                    bool ok = unit > 0 && after - before == unit * 4;
                    return (ok, $"share {before} -> {after} for 4 units valued {unit}");
                }
                finally { Drop(g, o); }
            });
        }
    }
}
