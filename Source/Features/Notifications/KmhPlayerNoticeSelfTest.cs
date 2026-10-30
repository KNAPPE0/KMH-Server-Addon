using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Features.Marketplace;
using KMHServerAddon.Features.Notifications.Dto;
using KMHServerAddon.Features.Quests;
using KMHServerAddon.Features.Quests.Dto;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Notifications
{
    // Each case runs the real handler as an offline counterpart and reads the queued notice, or the refusal the actor saw.
    internal static class KmhPlayerNoticeSelfTest
    {
        private static string User(string tag) => $"selftest_pn{tag}_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            var enforcement = Features.Enforcement.EnforcementConfig.Current;
            FeaturesConfig f = FeaturesConfig.Current;
            bool hadEnforcement = enforcement.Enabled, hadGuilds = f.Guilds, hadQuests = f.Quests;
            var hadOut = KmhRouter.OutboundForTest;
            try
            {
                enforcement.Enabled = false;
                f.Guilds = true; f.Quests = true;
                if (!KmhRouter.IsRegistered(KmhProtocol.Kind.QuestClaim)) QuestHandler.Register();
                if (!KmhRouter.IsRegistered(KmhProtocol.Kind.GuildKick)) GuildHandler.Register();
                Quests(r);
                Expiry(r);
                Guilds(r);
            }
            finally
            {
                enforcement.Enabled = hadEnforcement;
                f.Guilds = hadGuilds; f.Quests = hadQuests;
                KmhRouter.OutboundForTest = hadOut;
            }
            return r;
        }

        private static void Check(List<(string, bool, string)> r, string name, Func<(bool ok, string detail)> probe)
        {
            try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
            catch (Exception ex) { r.Add((name, false, $"threw: {ex.GetType().Name}: {ex.Message}")); }
        }

        // A player connected through the router, never through Network.ServerClients, so everyone else counts as offline.
        private static ServerClient Session(string user)
        {
            var c = new ServerClient(null, null, createListener: false);
            c.GetData<UserFile>(new UserFile { Username = user });
            KmhHandshakeHandler.MarkCompatible(c);
            return c;
        }

        private static void Send(ServerClient c, string kind, object data)
            => KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(new KmhEnvelope(kind, data) { OpId = Guid.NewGuid().ToString("N") }.Serialize()));

        private static NotificationDto Queued(string user, string title)
            => NotificationStore.PeekForUser(user).Find(n => string.Equals(n.Title, title, StringComparison.Ordinal));

        private static string Titles(string user) => string.Join(" | ", NotificationStore.PeekForUser(user).ConvertAll(n => n.Title));

        private static List<string> NoticesTo(ServerClient c, Action act)
        {
            var seen = new List<string>();
            var had = KmhRouter.OutboundForTest;
            KmhRouter.OutboundForTest = (cl, kind, data) =>
            {
                if (cl != c || kind != KmhProtocol.Kind.Notice) return;
                seen.Add((string)Newtonsoft.Json.Linq.JObject.FromObject(data)["text"] ?? "");
            };
            try { act(); }
            finally { KmhRouter.OutboundForTest = had; }
            return seen;
        }

        private static long PostBounty(string poster, string kind = QuestEntry.KindBounty, int hours = 24)
        {
            if (!TreasuryStore.DepositSilver(poster, 1000, "selftest seed")) throw new InvalidOperationException("seed");
            long id = QuestStore.PostDraft(poster, new QuestEntry
            {
                Kind = kind, Title = "Selftest <b>notice</b> quest",
                Description = "Selftest quest whose description is long enough for a custom quest.", BountySilver = 100,
            }, hours, out string why);
            if (id == 0) throw new InvalidOperationException("post: " + why);
            return id;
        }

        private static void Drop(params string[] users)
        {
            foreach (string u in users)
            {
                QuestStore.PurgePoster(u);
                NotificationStore.ClearUser(u);
                Delivery.DeliveryStore.PurgeUser(u);
                Transactions.KmhTransactionRepository.AbortAllFor(u);
                GuildStore.Leave(u, out _);
                TreasuryStore.PurgeOwnerForTest(u);
            }
        }

        private static void Quests(List<(string, bool, string)> r)
        {
            Check(r, "Quest notices: a claim tells the offline poster, with the title made inert", () =>
            {
                string poster = User("p"), claimer = User("c");
                try
                {
                    long id = PostBounty(poster);
                    Send(Session(claimer), KmhProtocol.Kind.QuestClaim, new { quest_id = id });
                    NotificationDto n = Queued(poster, "Quest claimed");
                    bool ok = n != null && n.Body.Contains(claimer) && n.Body.Contains("#" + id) && n.Body.IndexOf('<') < 0;
                    return (ok, n == null ? "poster has: " + Titles(poster) : n.Body);
                }
                finally { Drop(poster, claimer); }
            });

            Check(r, "Quest notices: a bounty submission asks the poster for a review and names the auto-release", () =>
            {
                string poster = User("p"), claimer = User("c");
                try
                {
                    long id = PostBounty(poster);
                    ServerClient c = Session(claimer);
                    Send(c, KmhProtocol.Kind.QuestClaim, new { quest_id = id });
                    Send(c, KmhProtocol.Kind.QuestSubmit, new { quest_id = id });
                    Send(c, KmhProtocol.Kind.QuestSubmit, new { quest_id = id });
                    int hours = QuestsConfig.Current.ReviewTimeoutHours;
                    int asked = NotificationStore.PeekForUser(poster).FindAll(x => x.Title == "Quest awaiting your review").Count;
                    NotificationDto n = Queued(poster, "Quest awaiting your review");
                    bool names = hours <= 0 || (n?.Body ?? "").Contains($"{hours} h");
                    bool ok = asked == 1 && names && QuestStore.StateForTest(id) == QuestEntry.StateSubmitted;
                    return (ok, $"asked {asked} time(s) for two submits; state={QuestStore.StateForTest(id)}; body: {n?.Body}");
                }
                finally { Drop(poster, claimer); }
            });

            Check(r, "Quest notices: a custom quest's proof asks the poster for a review", () =>
            {
                string poster = User("p"), claimer = User("c");
                try
                {
                    long id = PostBounty(poster, QuestEntry.KindCustom);
                    ServerClient c = Session(claimer);
                    Send(c, KmhProtocol.Kind.QuestClaim, new { quest_id = id });
                    Send(c, KmhProtocol.Kind.QuestSubmitProof, new { quest_id = id, proof_text = "done, see screenshot", proof_image_url = "" });
                    NotificationDto n = Queued(poster, "Quest awaiting your review");
                    return (n != null && QuestStore.StateForTest(id) == QuestEntry.StatePendingReview,
                            $"state={QuestStore.StateForTest(id)} poster has: {Titles(poster)}");
                }
                finally { Drop(poster, claimer); }
            });

            Check(r, "Quest notices: an approval tells the offline claimer they were paid", () =>
            {
                string poster = User("p"), claimer = User("c");
                try
                {
                    long id = PostBounty(poster);
                    ServerClient c = Session(claimer);
                    Send(c, KmhProtocol.Kind.QuestClaim, new { quest_id = id });
                    Send(c, KmhProtocol.Kind.QuestSubmit, new { quest_id = id });
                    Send(Session(poster), KmhProtocol.Kind.QuestApprove, new { quest_id = id });
                    NotificationDto n = Queued(claimer, "Quest approved");
                    bool ok = n != null && n.Body.Contains(poster) && QuestStore.StateForTest(id) == QuestEntry.StateCompleted;
                    return (ok, $"state={QuestStore.StateForTest(id)} claimer has: {Titles(claimer)}");
                }
                finally { Drop(poster, claimer); }
            });

            Check(r, "Quest notices: a review approval and a rejection each tell the claimer, the rejection with its note", () =>
            {
                string poster = User("p"), claimer = User("c"), other = User("o");
                try
                {
                    long id = PostBounty(poster, QuestEntry.KindCustom);
                    ServerClient c = Session(claimer), p = Session(poster);
                    Send(c, KmhProtocol.Kind.QuestClaim, new { quest_id = id });
                    Send(c, KmhProtocol.Kind.QuestSubmitProof, new { quest_id = id, proof_text = "first try", proof_image_url = "" });
                    Send(p, KmhProtocol.Kind.QuestReview, new { quest_id = id, approve = false, note = "needs <color=red>more</color> walls" });
                    NotificationDto rejected = Queued(claimer, "Quest submission rejected");

                    long id2 = PostBounty(other, QuestEntry.KindCustom);
                    Send(c, KmhProtocol.Kind.QuestClaim, new { quest_id = id2 });
                    Send(c, KmhProtocol.Kind.QuestSubmitProof, new { quest_id = id2, proof_text = "done", proof_image_url = "" });
                    Send(Session(other), KmhProtocol.Kind.QuestReview, new { quest_id = id2, approve = true, note = "" });
                    NotificationDto approved = Queued(claimer, "Quest approved");

                    bool ok = rejected != null && rejected.Body.Contains("walls") && rejected.Body.IndexOf('<') < 0
                              && QuestStore.StateForTest(id) == QuestEntry.StateOpen && approved != null;
                    return (ok, $"rejected: {rejected?.Body} | approved: {approved?.Body}");
                }
                finally { Drop(poster, claimer, other); }
            });
        }

        private static void Expiry(List<(string, bool, string)> r)
        {
            long Later() => DateTime.UtcNow.Ticks + TimeSpan.FromDays(400).Ticks;

            Check(r, "Marketplace notices: a purchase tells the buyer what they bought, as a sale tells the seller", () =>
            {
                string seller = User("ms"), buyer = User("mb");
                bool hadMarket = FeaturesConfig.Current.Marketplace;
                try
                {
                    FeaturesConfig.Current.Marketplace = true;
                    if (!KmhRouter.IsRegistered(KmhProtocol.Kind.MarketplaceBuy)) MarketplaceHandler.Register();
                    if (!TreasuryStore.DepositItem(seller, "Steel", 12, "seed") || !TreasuryStore.DepositSilver(buyer, 1000, "seed")) return (false, "seed");
                    long id = MarketplaceStore.Post(seller, "Steel", 12, 10, "public", 1);
                    if (id == 0) return (false, "post refused");
                    ServerClient c = Session(buyer);
                    List<string> told = NoticesTo(c, () => Send(c, KmhProtocol.Kind.MarketplaceBuy, new { listing_id = id, qty = 5 }));
                    string mine = told.Find(t => t.StartsWith("Bought", StringComparison.Ordinal));
                    bool ok = mine != null && mine.Contains("x5") && Queued(seller, "Marketplace sale") != null;
                    return (ok, $"buyer told: '{mine ?? string.Join(" | ", told)}'; seller has: {Titles(seller)}");
                }
                finally
                {
                    FeaturesConfig.Current.Marketplace = hadMarket;
                    MarketplaceStore.AdminPurgeSeller(seller, dryRun: false);
                    Drop(seller, buyer);
                }
            });

            Check(r, "Expiry notices: an expired listing tells the seller what went back to their treasury", () =>
            {
                string seller = User("s");
                try
                {
                    if (!TreasuryStore.DepositItem(seller, "Steel", 12, "seed")) return (false, "seed");
                    long id = MarketplaceStore.Post(seller, "Steel", 12, 10, "public", 1);
                    if (id == 0) return (false, "post refused");
                    Maintenance.ExpirySweeper.TickForTest(Later());
                    NotificationDto n = Queued(seller, "Listing expired");
                    bool ok = n != null && n.Body.Contains("#" + id) && n.Body.Contains("12x");
                    return (ok, n == null ? "seller has: " + Titles(seller) : n.Body);
                }
                finally { Drop(seller); }
            });

            Check(r, "Expiry notices: an expired quest tells its poster of the refund and its claimer that it ended", () =>
            {
                string poster = User("p"), claimer = User("c");
                try
                {
                    long id = PostBounty(poster, QuestEntry.KindBounty, 1);
                    Send(Session(claimer), KmhProtocol.Kind.QuestClaim, new { quest_id = id });
                    Maintenance.ExpirySweeper.TickForTest(Later());
                    NotificationDto toPoster = Queued(poster, "Quest expired"), toClaimer = Queued(claimer, "Quest expired");
                    bool ok = toPoster != null && toClaimer != null && toPoster.Body.Contains("#" + id);
                    return (ok, $"poster: {toPoster?.Body ?? Titles(poster)} | claimer: {toClaimer?.Body ?? Titles(claimer)}");
                }
                finally { Drop(poster, claimer); }
            });
        }

        private static void Guilds(List<(string, bool, string)> r)
        {
            string NewGuild() => "selftest pn " + Guid.NewGuid().ToString("N").Substring(0, 6);

            Check(r, "Guild notices: a kicked player is told who removed them, even offline", () =>
            {
                string owner = User("o"), mate = User("m"), g = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, g, out string why) || !GuildStore.AddMember(mate, g)) return (false, "seed: " + why);
                    Send(Session(owner), KmhProtocol.Kind.GuildKick, new { username = mate });
                    NotificationDto n = Queued(mate, "Removed from your guild");
                    bool ok = n != null && n.Body.Contains(owner) && GuildStore.CurrentGuildOf(mate) == null;
                    return (ok, n == null ? "kicked player has: " + Titles(mate) : n.Body);
                }
                finally { Drop(mate, owner); }
            });

            Check(r, "Guild notices: an alliance proposal reaches the other guild's leaders, not its plain members", () =>
            {
                string a = User("a"), b = User("b"), plain = User("m"), ga = NewGuild(), gb = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out _) || !GuildStore.CreateGuildAndJoinAsAdmin(b, gb, out _)
                        || !GuildStore.AddMember(plain, gb)) return (false, "seed");
                    Send(Session(a), KmhProtocol.Kind.GuildProposeAlliance, new { other_guild = gb.ToUpperInvariant() });
                    NotificationDto n = Queued(b, "Alliance proposed");
                    bool ok = n != null && n.Body.Contains(ga) && Queued(plain, "Alliance proposed") == null;
                    return (ok, n == null ? "leader has: " + Titles(b) : $"{n.Body} (plain member told: {Queued(plain, "Alliance proposed") != null})");
                }
                finally { Drop(plain, a, b); }
            });

            Check(r, "Guild notices: the new owner after a transfer is told", () =>
            {
                string owner = User("o"), heir = User("h"), g = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, g, out _) || !GuildStore.AddMember(heir, g)) return (false, "seed");
                    Send(Session(owner), KmhProtocol.Kind.GuildTransferOwner, new { username = heir });
                    NotificationDto n = Queued(heir, "You now own your guild");
                    bool ok = n != null && GuildStore.RankOf(heir) == GuildMemberDto.RankOwner;
                    return (ok, n == null ? "heir has: " + Titles(heir) : n.Body);
                }
                finally { Drop(owner, heir); }
            });

            Check(r, "Guild snapshots: a donation's save-confirm pushes the new vault balance to every online member", () =>
            {
                string owner = User("o"), donor = User("d"), g = NewGuild();
                var hadOnline = GuildHandler.OnlineForTest;
                var hadOut = KmhRouter.OutboundForTest;
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, g, out _) || !GuildStore.AddMember(donor, g)) return (false, "seed");
                    if (!TreasuryStore.DepositSilver(donor, 100, "selftest seed")) return (false, "seed silver");
                    string txn = Guid.NewGuid().ToString("N");
                    if (!TreasuryStore.BeginPendingGuildDonation(donor, g, 50, txn, out string why)) return (false, "donate: " + why);
                    ServerClient co = Session(owner), cd = Session(donor);
                    var seen = new Dictionary<ServerClient, long>();
                    GuildHandler.OnlineForTest = () => new[] { co, cd };
                    KmhRouter.OutboundForTest = (cl, kind, data) =>
                    {
                        if (kind == KmhProtocol.Kind.GuildSnapshot && data is GuildSnapshotEnvelope e && e.Guild != null) seen[cl] = e.Guild.GuildSilver;
                    };
                    int committed = TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    bool ok = committed == 1 && seen.TryGetValue(co, out long toOwner) && toOwner == 50
                              && seen.TryGetValue(cd, out long toDonor) && toDonor == 50;
                    return (ok, $"committed={committed} pushed to owner={(seen.ContainsKey(co) ? seen[co].ToString() : "none")} donor={(seen.ContainsKey(cd) ? seen[cd].ToString() : "none")}");
                }
                finally
                {
                    GuildHandler.OnlineForTest = hadOnline;
                    KmhRouter.OutboundForTest = hadOut;
                    Drop(donor, owner);
                    TreasuryStore.PurgeOwnerForTest(g);
                }
            });

            Check(r, "Guild refusals: promote, demote, kick, MOTD and alliance refusals each tell the actor why", () =>
            {
                string owner = User("o"), mate = User("m"), other = User("x"), g = NewGuild(), og = NewGuild();
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, g, out _) || !GuildStore.AddMember(mate, g)
                        || !GuildStore.CreateGuildAndJoinAsAdmin(other, og, out _)) return (false, "seed");
                    ServerClient c = Session(mate);
                    var said = new Dictionary<string, string>();
                    void Try(string what, string kind, object data)
                    {
                        List<string> got = NoticesTo(c, () => Send(c, kind, data));
                        said[what] = got.Count > 0 ? got[got.Count - 1] : "";
                    }
                    Try("promote", KmhProtocol.Kind.GuildPromote, new { username = owner });
                    Try("demote",  KmhProtocol.Kind.GuildDemote,  new { username = owner });
                    Try("kick",    KmhProtocol.Kind.GuildKick,    new { username = owner });
                    Try("motd",    KmhProtocol.Kind.GuildSetMotd, new { motd = "hello" });
                    Try("ally",    KmhProtocol.Kind.GuildProposeAlliance, new { other_guild = og });
                    var silent = new List<string>();
                    foreach (KeyValuePair<string, string> kv in said) if (string.IsNullOrWhiteSpace(kv.Value)) silent.Add(kv.Key);
                    bool ok = silent.Count == 0 && said["kick"].Contains("Moderator") && said["motd"].Contains("Admin")
                              && said["ally"].Contains("Admin") && GuildStore.RankOf(owner) == GuildMemberDto.RankOwner;
                    var lines = new List<string>();
                    foreach (KeyValuePair<string, string> kv in said) lines.Add($"{kv.Key}: '{kv.Value}'");
                    return (ok, (silent.Count > 0 ? "silent: " + string.Join(",", silent) + "; " : "") + string.Join("; ", lines));
                }
                finally { Drop(mate, owner, other); }
            });
        }
    }
}
