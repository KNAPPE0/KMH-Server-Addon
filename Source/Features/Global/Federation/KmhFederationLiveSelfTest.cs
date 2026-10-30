using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Features.Guilds;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Federation
{
    // Wire check against a non-production Global (KMH_FED_LIVE_URL), one KMH_FED_LIVE_STEP per run so two copies
    // are two servers and restarts are real; KMH_FED_LIVE_OFFICER is a Global user id, KMH_FED_LIVE_SHARE passes ids.
    internal static class KmhFederationLiveSelfTest
    {
        private static readonly CancellationToken Ct = CancellationToken.None;
        private const string Guild = "E2E Guild";
        private const string Guild2 = "E2E Second Guild";
        private const string Changed = "changed by the live check";

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            string url = Environment.GetEnvironmentVariable("KMH_FED_LIVE_URL") ?? "";
            if (url.Length == 0) return r;
            if (KmhGlobalSafety.IsProduction(url))
            {
                r.Add(("live federation: refuses the production Global", false, url));
                return r;
            }

            string step = (Environment.GetEnvironmentVariable("KMH_FED_LIVE_STEP") ?? "").Trim().ToLowerInvariant();
            string officer = Environment.GetEnvironmentVariable("KMH_FED_LIVE_OFFICER") ?? "";
            string sharePath = Environment.GetEnvironmentVariable("KMH_FED_LIVE_SHARE") ?? "";
            JObject share = File.Exists(sharePath) ? JObject.Parse(File.ReadAllText(sharePath)) : new JObject();

            // The runner never boots the server, so load what a boot would: every step is then a restart.
            Persistence.KmhDataMeta.ReconcileOnBoot();
            GuildStore.LoadFromDisk();
            KMHServerAddon.Features.Treasury.TreasuryStore.LoadFromDisk();
            FederatedGuildStore.LoadFromDisk();
            FederationOutbox.LoadFromDisk();
            string me = KmhServerIdentity.Id;

            // Only the address is set: everything else is whatever a fresh server ships with.
            GlobalConfig cfg = GlobalConfig.Current;
            cfg.BaseUrl = url;
            cfg.Save();
            string owner = "e2e-owner-" + me.Substring(0, Math.Min(8, me.Length));
            string owner2 = owner + "-2";

            void Add(string name, bool ok, string detail) => r.Add(($"live federation [{step}] {name}", ok, detail));
            void Save() { if (sharePath.Length > 0) File.WriteAllText(sharePath, share.ToString()); }
            string S(string key) => (string)share[key] ?? "";
            FederationResult Wait(System.Threading.Tasks.Task<FederationResult> t) => t.GetAwaiter().GetResult();
            FederatedGuildLink Mine() => FederationService.LinkForGuild(Guild);
            bool Refresh() { FederatedGuildLink l = Mine(); return l != null && FederationService.RefreshViewAsync(l.LinkId, Ct).GetAwaiter().GetResult(); }
            long SiteTotal()
            {
                long t = 0;
                foreach (FederationCategory c in Mine()?.View?.Categories ?? new List<FederationCategory>())
                    if (c.Category == "site") t = c.Total;
                return t;
            }
            string OtherServer()
            {
                foreach (JToken s in (JArray)share["servers"] ?? new JArray()) if ((string)s != me) return (string)s;
                return "";
            }
            List<FederationObjective> Objectives() => Mine()?.View?.Objectives ?? new List<FederationObjective>();
            string Shown(List<FederationObjective> os)
                => string.Join(",", os.ConvertAll(o => $"{o.Category}:{o.State}:{o.Progress}/{o.Target}{(o.RewardEligible ? ":eligible" : "")}"));
            int Vault(string def) => KMHServerAddon.Features.Treasury.TreasuryStore.GuildItemForTest(Guild, def);

            switch (step)
            {
                case "boot":
                {
                    FeaturesConfig f = FeaturesConfig.Current;
                    Add("a fresh server ships Federation on and the treasury off, unlisted",
                        f.GuildFederation && !f.ConnectedTreasury && !cfg.Enabled && cfg.BackendIdentity,
                        $"federation={f.GuildFederation} treasury={f.ConnectedTreasury} listed={cfg.Enabled} backend={cfg.BackendIdentity}");
                    KmhGlobalService.Start();
                    // Registration is immediate; the first beat waits up to a minute for RWT's config, absent here.
                    DateTime until = DateTime.UtcNow.AddSeconds(100);
                    while (DateTime.UtcNow < until && KmhGlobalRegistrar.LastSuccessUtc == default) Thread.Sleep(500);
                    bool beat = KmhGlobalRegistrar.LastSuccessUtc != default;
                    KmhGlobalService.Stop();
                    Add("it registers a private identity and heartbeats on its own",
                        GlobalSetupState.Current_.BackendRegistered && KmhGlobalService.BackendMode && beat,
                        $"registered={GlobalSetupState.Current_.BackendRegistered} backend={KmhGlobalService.BackendMode} beat={beat} error={KmhGlobalBackend.LastError}");
                    share["booted"] = me;
                    Save();
                    break;
                }
                case "mint":
                {
                    string key = KmhServerKey.Load() != null ? KmhServerKey.PublicKey : "";
                    Add("a new server's id is derived from its own signing key", me.Length == 32 && me == KmhServerIdentity.DeriveId(key),
                        $"id={me} derived={KmhServerIdentity.DeriveId(key)}");
                    share["victim"] = me;
                    share["victim_key"] = key;
                    Save();
                    break;
                }
                case "squat":
                {
                    // Someone who only learned the id registers it first, under their own key.
                    Persistence.KmhDataMeta.SetInstanceIdForTest(S("victim"));
                    bool ok = KmhGlobalBackend.RegisterAsync(Ct).GetAwaiter().GetResult();
                    Add("a server that only learned the id registers it first", ok && KmhServerIdentity.Id == S("victim"),
                        $"ok={ok} id={KmhServerIdentity.Id} error={KmhGlobalBackend.LastError}");
                    if (!GuildStore.GuildExists(Guild)) GuildStore.CreateGuildAndJoinAsAdmin(owner, Guild, out _);
                    FederationResult res = Wait(FederationService.CreateAsync(owner, officer, "Squat Pact", "SQT", "", "", Ct));
                    Add("and founds a federation under it", res.Ok && Mine() != null, $"ok={res.Ok} code={res.Code}");
                    share["squat_federation"] = Mine()?.GuildId ?? "";
                    Save();
                    break;
                }
                case "prove":
                {
                    bool ok = KmhGlobalBackend.RegisterAsync(Ct).GetAwaiter().GetResult();
                    int generation = GlobalConfig.Current.SigningGeneration;
                    Add("the server whose key the id came from registers it anyway, at a new generation",
                        ok && GlobalSetupState.Current_.BackendRegistered && generation == 2,
                        $"ok={ok} generation={generation} error={KmhGlobalBackend.LastError}");
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("and heartbeats", beat, $"beat={beat} code={KmhGlobalRegistrar.LastErrorCode}");
                    break;
                }
                case "evicted":
                {
                    Persistence.KmhDataMeta.SetInstanceIdForTest(S("victim"));
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("the displaced key is refused from then on", !beat && KmhGlobalRegistrar.LastErrorCode == "KMH-SRV-303",
                        $"beat={beat} code={KmhGlobalRegistrar.LastErrorCode}");
                    bool read = Refresh();
                    Add("and cannot read the federation it founded", !read, $"read={read}");
                    break;
                }
                case "register":
                {
                    bool ok = GlobalSetupState.Current_.BackendRegistered || KmhGlobalBackend.RegisterAsync(Ct).GetAwaiter().GetResult();
                    Add("this server holds a private identity", ok && GlobalSetupState.Current_.BackendRegistered,
                        $"id={me} error={KmhGlobalBackend.LastError}");
                    share["servers"] ??= new JArray();
                    ((JArray)share["servers"]).Add(me);
                    Save();
                    if (!GuildStore.GuildExists(Guild)) GuildStore.CreateGuildAndJoinAsAdmin(owner, Guild, out _);
                    if (!GuildStore.GuildExists(Guild2)) GuildStore.CreateGuildAndJoinAsAdmin(owner2, Guild2, out _);
                    Add("local guilds exist to federate", GuildStore.GuildExists(Guild) && GuildStore.GuildExists(Guild2), Guild);
                    break;
                }
                case "create":
                {
                    FederationResult res = Wait(FederationService.CreateAsync(owner, officer, S("name"), S("tag"),
                                                                              "made by the live check", "#2980B9", Ct));
                    FederatedGuildLink link = Mine();
                    Add("founding answers with a link and a federation id", res.Ok && link != null && link.GuildId.Length == 32,
                        $"ok={res.Ok} code={res.Code} link={link?.LinkId} federation={link?.GuildId}");
                    share["federation"] = link?.GuildId ?? "";
                    share["leader_link"] = link?.LinkId ?? "";
                    Add("the founder leads", link?.IsLeader == true && link.View?.Federation?.LeaderLinkId == link.LinkId,
                        $"leader={link?.View?.Federation?.LeaderLinkId}");

                    string other = OtherServer();
                    FederationResult first = Wait(FederationService.InviteAsync(owner, officer, other, Ct));
                    string firstId = "";
                    foreach (FederationInvite i in Mine()?.View?.Invites ?? new List<FederationInvite>())
                        if (i.TargetServer == other) firstId = i.InviteId;
                    FederationResult revoked = Wait(FederationService.RevokeInviteAsync(owner, officer, firstId, Ct));
                    Refresh();
                    bool gone = !(Mine()?.View?.Invites ?? new List<FederationInvite>()).Exists(i => i.InviteId == firstId);
                    Add("an unused invite can be withdrawn", first.Ok && firstId.Length > 0 && revoked.Ok && gone,
                        $"issued={first.Ok}/{first.Code} id={firstId} revoked={revoked.Ok}/{revoked.Code} gone={gone}");

                    FederationResult inv = Wait(FederationService.InviteAsync(owner, officer, other, Ct));
                    Add("an invite is issued for the other server", inv.Ok && inv.InviteCode.Length > 0 && inv.InviteTarget == other,
                        $"ok={inv.Ok} code={inv.Code} target={inv.InviteTarget}");
                    FederationResult wrongServer = Wait(FederationService.JoinAsync(owner2, officer, inv.InviteCode, Ct));
                    Add("an invite for another server cannot be used here", wrongServer.Code == "KMH-GLD-966"
                        && FederationService.LinkForGuild(Guild2) == null, $"code={wrongServer.Code}");

                    share["revoked_invite"] = first.InviteCode;
                    share["invite"] = inv.InviteCode;
                    Save();
                    break;
                }
                case "join":
                {
                    FederationResult stale = Wait(FederationService.JoinAsync(owner, officer, S("revoked_invite"), Ct));
                    Add("a withdrawn invite is refused", stale.Code == "KMH-GLD-966" && Mine() == null, $"code={stale.Code}");

                    FederationResult res = Wait(FederationService.JoinAsync(owner, officer, S("invite").ToLowerInvariant(), Ct));
                    FederatedGuildLink link = Mine();
                    Add("joining with the invite reaches the same federation", res.Ok && link?.GuildId == S("federation"),
                        $"ok={res.Ok} code={res.Code} federation={link?.GuildId}");
                    Add("a joining guild does not lead", link != null && !link.IsLeader, $"leader={link?.View?.Federation?.LeaderLinkId}");
                    share["member_link"] = link?.LinkId ?? "";
                    Save();
                    break;
                }
                case "reuse":
                {
                    // Its own step, after a Global restart: a rate limit answering first must never read as single use.
                    FederationResult reuse = Wait(FederationService.JoinAsync(owner2, officer, S("invite"), Ct));
                    Add("the invite does not work twice", reuse.Code == "KMH-GLD-966" && FederationService.LinkForGuild(Guild2) == null,
                        $"code={reuse.Code}");
                    break;
                }
                case "verify":
                {
                    // After the old federation is disbanded, the harness names the one the guilds rebuilt.
                    string federation = S("verify_federation");
                    if (federation.Length == 0) federation = S("federation");
                    bool read = Refresh();
                    FederatedGuildLink link = Mine();
                    int members = link?.View?.Members?.FindAll(m => m.State == "Active").Count ?? 0;
                    string expect = S("expect_members");
                    if (expect.Length == 0) expect = "2";
                    Add("the link survived a restart and Global still describes it", read && link.GuildId == federation,
                        $"read={read} federation={link?.GuildId} state={link?.State}");
                    Add($"{expect} live member(s) seen", members.ToString() == expect, $"members={members}");

                    // Whatever an outage left queued goes out once Global answers again.
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Add("nothing is left queued", FederationOutbox.PendingFor(link?.LinkId ?? "") == 0,
                        $"queued={FederationOutbox.PendingFor(link?.LinkId ?? "")}");
                    long seen = (long?)share["site_total"] ?? 0;
                    if (seen > 0 && federation == S("federation"))
                    {
                        Refresh();
                        Add("progress reported by another member is visible here", SiteTotal() >= seen,
                            $"site total={SiteTotal()} reported={seen}");
                    }
                    break;
                }
                case "transfer":
                {
                    FederationResult res = Wait(FederationService.TransferAsync(owner, officer, S("member_link"), Ct));
                    Refresh();
                    FederatedGuildLink link = Mine();
                    Add("the leader hands leadership to another member", res.Ok && link != null && !link.IsLeader
                        && link.View?.Federation?.LeaderLinkId == S("member_link"), $"ok={res.Ok} code={res.Code}");
                    FederationResult disband = Wait(FederationService.DisbandAsync(owner, officer, S("tag"), "", Ct));
                    Add("a former leader can no longer disband", disband.Code == "KMH-FED-907" && Mine()?.State == FederatedLinkState.Active,
                        $"code={disband.Code}");
                    break;
                }
                case "lead":
                {
                    Refresh();
                    FederatedGuildLink link = Mine();
                    Add("the new leader sees that it leads", link?.IsLeader == true, $"leader={link?.View?.Federation?.LeaderLinkId}");
                    FederationResult back = Wait(FederationService.TransferAsync(owner, officer, S("leader_link"), Ct));
                    Refresh();
                    Add("and can hand it back", back.Ok && Mine()?.IsLeader == false, $"ok={back.Ok} code={back.Code}");
                    break;
                }
                case "outage":
                {
                    FederatedGuildLink before = Mine();
                    bool queued = FederationService.Record("site", Guild, "", 1);
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Add("activity during an outage is kept for later", queued && FederationOutbox.PendingFor(before?.LinkId ?? "") >= 1,
                        $"queued={FederationOutbox.PendingFor(before?.LinkId ?? "")}");
                    bool read = Refresh();
                    FederatedGuildLink after = Mine();
                    Add("the link and its last view survive Global being down", !read && after?.State == FederatedLinkState.Active
                        && after.View?.Federation?.Id == S("federation"), $"read={read} state={after?.State}");
                    FederationResult create = Wait(FederationService.CreateAsync(owner2, officer, "Outage Pact", "OUT", "", "", Ct));
                    Add("a federation action reports Global unavailable and changes nothing", create.Code == "global.unreachable"
                        && FederationService.LinkForGuild(Guild2) == null, $"code={create.Code}");
                    bool motd = GuildStore.SetMotd(owner, "Still here while Global is down");
                    bool open = GuildStore.SetOpenJoin(owner, true, out string openWhy);
                    Add("the local guild keeps working", motd && open && GuildStore.GuildExists(Guild), $"motd={motd} open={open} {openWhy}");
                    break;
                }
                case "second":
                {
                    FederationResult res = Wait(FederationService.CreateAsync(owner2, officer, "E2E Other Pact", "OTH", "", "", Ct));
                    FederatedGuildLink other = FederationService.LinkForGuild(Guild2);
                    Add("another guild on the same server founds a second federation", res.Ok && other != null
                        && other.GuildId != S("federation"), $"ok={res.Ok} code={res.Code} federation={other?.GuildId}");
                    break;
                }
                case "update":
                {
                    FederationResult res = Wait(FederationService.UpdateAsync(owner, officer, S("name"), S("tag"), Changed, "#2980B9", Ct));
                    Add("the leader changes the federation's description", res.Ok && Mine()?.View?.Federation?.Description == Changed,
                        $"ok={res.Ok} code={res.Code} description={Mine()?.View?.Federation?.Description}");
                    break;
                }
                case "beat":
                {
                    // Both views are minutes old at most, so only the heartbeat can say which federation moved.
                    FederatedGuildLink first = Mine();
                    long before = first?.View?.Federation?.Revision ?? -1;
                    long otherRead = FederationService.LinkForGuild(Guild2)?.ViewReadUtcTicks ?? 0;
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    FederatedGuildLink after = Mine();
                    bool otherReread = (FederationService.LinkForGuild(Guild2)?.ViewReadUtcTicks ?? 0) != otherRead;
                    Add("one heartbeat names the federation that changed, and only that one is read again",
                        beat && after?.View?.Federation?.Description == Changed && after.View.Federation.Revision > before
                        && otherRead != 0 && !otherReread,
                        $"beat={beat} code={KmhGlobalRegistrar.LastErrorCode} revision={before}->{after?.View?.Federation?.Revision} "
                        + $"description={after?.View?.Federation?.Description} other re-read={otherReread}");
                    break;
                }
                case "contribute":
                {
                    FederatedGuildLink link = Mine();
                    for (int i = 0; i < 3; i++) FederationService.Record("site", Guild, "", 1);
                    List<FederationOutbox.Entry> sent = FederationOutbox.TakeBatch(link.LinkId, 100);
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Refresh();
                    long first = SiteTotal();
                    Add("reports are delivered and counted", sent.Count == 3 && FederationOutbox.PendingFor(link.LinkId) == 0 && first >= 3,
                        $"sent={sent.Count} queued={FederationOutbox.PendingFor(link.LinkId)} site total={first} renown={Mine().View?.Federation?.Renown}");

                    foreach (FederationOutbox.Entry e in sent)
                        FederationOutbox.Add(new FederationOutbox.Entry
                        {
                            LinkId = e.LinkId, ReportId = e.ReportId, Category = e.Category, Amount = e.Amount,
                            Player = e.Player, OccurredAtUnix = e.OccurredAtUnix,
                        });
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Refresh();
                    Add("the same reports sent again are not counted twice", SiteTotal() == first && FederationOutbox.PendingFor(link.LinkId) == 0,
                        $"before={first} after={SiteTotal()}");
                    share["site_total"] = first;
                    Save();
                    break;
                }
                case "reward":
                {
                    // The harness lowered this week's targets at Global; reports, completion and payment are real.
                    Refresh();
                    List<FederationObjective> open = Objectives().FindAll(o => o.State == "Active");
                    foreach (FederationObjective o in open) FederationService.Record(o.Category, Guild, officer, 1);
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Refresh();
                    List<FederationObjective> done = Objectives().FindAll(o => o.State == "Completed" && o.RewardEligible);
                    Add("this week's objectives complete from real reports, and this guild is eligible for each",
                        open.Count > 0 && done.Count == open.Count, $"reported={open.Count} completed={done.Count} {Shown(Objectives())}");

                    var expected = new Dictionary<string, int>();
                    foreach (FederationObjective o in done)
                    {
                        FederationRewards.Bundle bundle = FederationRewards.Choose(o.Id);
                        expected.TryGetValue(bundle.DefName, out int already);
                        expected[bundle.DefName] = already + FederationRewards.Scaled(bundle.Count);
                    }
                    var before = new Dictionary<string, int>();
                    foreach (string def in expected.Keys) before[def] = Vault(def);
                    int paid = FederationRewards.GrantPending(Mine());
                    int again = FederationRewards.GrantPending(Mine());
                    bool landed = expected.Count > 0;
                    var vault = new JObject();
                    foreach (KeyValuePair<string, int> e in expected)
                    {
                        landed &= Vault(e.Key) - before[e.Key] == e.Value;
                        vault[e.Key] = Vault(e.Key);
                    }
                    bool marked = done.TrueForAll(o => KMHServerAddon.Features.Treasury.TreasuryStore.HasGuildMarker(
                        Guild, KMHServerAddon.Features.Treasury.TreasuryStore.OneTimeRewardPrefix + o.Id));
                    Add("each reward lands once, in this guild's own vault on this server",
                        paid == done.Count && again == 0 && landed && marked,
                        $"paid={paid} then {again} vault={vault.ToString(Newtonsoft.Json.Formatting.None)} marked={marked}");
                    share["rewarded"] = new JArray(done.ConvertAll(o => o.Id).ToArray());
                    share["vault"] = vault;
                    Save();
                    break;
                }
                case "noreward":
                {
                    // Back under a new link, the same week's objectives are this guild's again; the vault remembers.
                    var ids = new List<string>();
                    foreach (JToken t in (JArray)share["rewarded"] ?? new JArray()) ids.Add((string)t);
                    Refresh();
                    foreach (FederationObjective o in Objectives().FindAll(o => ids.Contains(o.Id)))
                        FederationService.Record(o.Category, Guild, S("other_player"), 1);
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Refresh();
                    int eligible = Objectives().FindAll(o => ids.Contains(o.Id) && o.State == "Completed" && o.RewardEligible).Count;
                    int paid = FederationRewards.GrantPending(Mine());
                    bool same = true;
                    foreach (KeyValuePair<string, JToken> e in (JObject)share["vault"] ?? new JObject()) same &= Vault(e.Key) == (int)e.Value;
                    bool recorded = ids.TrueForAll(id => FederatedGuildStore.HasRewarded(Mine()?.LinkId ?? "", id));
                    Add("the rejoined link is eligible for the same objectives, and nothing is paid twice",
                        ids.Count > 0 && eligible == ids.Count && paid == 0 && same && recorded,
                        $"eligible={eligible}/{ids.Count} paid={paid} vault unchanged={same} recorded for the new link={recorded} {Shown(Objectives())}");
                    break;
                }
                case "leave":
                {
                    FederationResult res = Wait(FederationService.LeaveAsync(owner, officer, Ct));
                    FederatedGuildLink link = Mine();
                    Add("a member guild leaves without ending the federation", res.Ok && link?.State == FederatedLinkState.Left
                        && link.View?.Federation?.State == "Active", $"ok={res.Ok} code={res.Code} federation={link?.View?.Federation?.State}");
                    bool counted = FederationService.Record("site", Guild, "", 1);
                    Add("a guild that left can no longer report", !counted && FederationOutbox.PendingCount == 0,
                        $"recorded={counted} queued={FederationOutbox.PendingCount}");
                    break;
                }
                case "reinvite":
                {
                    FederationResult inv = Wait(FederationService.InviteAsync(owner, officer, OtherServer(), Ct));
                    Add("the guild that left can be invited back", inv.Ok && inv.InviteCode.Length > 0, $"ok={inv.Ok} code={inv.Code}");
                    share["invite3"] = inv.InviteCode;
                    Save();
                    break;
                }
                case "comeback":
                {
                    FederatedGuildLink before = Mine();
                    FederationResult res = Wait(FederationService.JoinAsync(owner, officer, S("invite3"), Ct));
                    FederatedGuildLink link = Mine();
                    int rows = FederatedGuildStore.All().FindAll(l => string.Equals(l.LocalGuild, Guild, StringComparison.OrdinalIgnoreCase)).Count;
                    Add("it rejoins the same federation as a new member", res.Ok && link?.GuildId == S("federation")
                        && link.LinkId != before?.LinkId && link.State == FederatedLinkState.Active && !link.IsLeader && rows == 1,
                        $"ok={res.Ok} code={res.Code} federation={link?.GuildId} link={before?.LinkId}->{link?.LinkId} rows={rows}");
                    long earlier = (long?)share["site_total"] ?? 0;
                    Add("what it reported before it left still counts", earlier > 0 && SiteTotal() >= earlier,
                        $"site total={SiteTotal()} reported before={earlier}");
                    bool counted = FederationService.Record("site", Guild, "", 1);
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    Add("and it reports again", counted && FederationOutbox.PendingFor(link?.LinkId ?? "") == 0,
                        $"recorded={counted} queued={FederationOutbox.PendingFor(link?.LinkId ?? "")}");
                    break;
                }
                case "disband":
                {
                    FederationResult late = Wait(FederationService.InviteAsync(owner, officer, OtherServer(), Ct));
                    share["late_invite"] = late.InviteCode;
                    Save();
                    FederationResult wrong = Wait(FederationService.DisbandAsync(owner, officer, "NOPE", "live check", Ct));
                    Add("disbanding needs the tag typed back", !wrong.Ok && wrong.Code == "KMH-FED-912" && Mine()?.State == FederatedLinkState.Active,
                        $"code={wrong.Code}");

                    // The harness raised this federation to level 3 at Global, so there is a bonus to lose.
                    Refresh();
                    double bonusBefore = FederationBenefits.SiteOutputFactor(Guild);
                    FederationResult res = Wait(FederationService.DisbandAsync(owner, officer, Mine()?.View?.Federation?.Tag ?? "", "live check", Ct));
                    FederatedGuildLink link = Mine();
                    Add("the leader disbands it into a tombstone", res.Ok && link?.State == FederatedLinkState.Disbanded
                        && link.View?.Federation?.State == "Disbanded", $"ok={res.Ok} code={res.Code} state={link?.State}");
                    Add("its local bonus stops at once", bonusBefore > 1.0 && FederationBenefits.SiteOutputFactor(Guild) == 1.0
                        && FederationBenefits.LevelFor(Guild) == 0, $"before={bonusBefore} after={FederationBenefits.SiteOutputFactor(Guild)}");
                    break;
                }
                case "late":
                {
                    // Global answers "disbanded" before it looks at the invite, which the disband also revoked.
                    FederationResult res = Wait(FederationService.JoinAsync(owner, officer, S("late_invite"), Ct));
                    Add("an invite issued before the disband no longer works", (res.Code == "KMH-FED-906" || res.Code == "KMH-GLD-966")
                        && Mine()?.State == FederatedLinkState.Left, $"code={res.Code} state={Mine()?.State}");
                    break;
                }
                case "after":
                {
                    FederatedGuildLink link = Mine();
                    Add("a restart does not bring the federation back", link?.State == FederatedLinkState.Disbanded
                        && link.View?.Federation?.State == "Disbanded", $"state={link?.State} federation={link?.View?.Federation?.State}");
                    FederationResult rename = Wait(FederationService.UpdateAsync(owner, officer, "Revived Pact", S("tag"), "", "", Ct));
                    FederationResult invite = Wait(FederationService.InviteAsync(owner, officer, OtherServer(), Ct));
                    Add("nothing can be changed or invited into it", rename.Code == "guild.unlinked" && invite.Code == "guild.unlinked",
                        $"update={rename.Code} invite={invite.Code}");
                    break;
                }
                case "recreate":
                {
                    FederationResult res = Wait(FederationService.CreateAsync(owner, officer, S("name"), S("tag"), "", "", Ct));
                    FederatedGuildLink link = Mine();
                    bool fresh = res.Ok && link != null && link.GuildId != S("federation")
                              && link.View?.Federation?.Renown == 0 && (link.View?.Members?.Count ?? 0) == 1;
                    Add("the same name makes a new federation that inherits nothing", fresh,
                        $"ok={res.Ok} code={res.Code} old={S("federation")} new={link?.GuildId} renown={link?.View?.Federation?.Renown} members={link?.View?.Members?.Count}");
                    share["recreated"] = link?.GuildId ?? "";
                    FederationResult again = Wait(FederationService.InviteAsync(owner, officer, OtherServer(), Ct));
                    Add("the new federation can invite the guild that left the old one", again.Ok, $"code={again.Code}");
                    share["invite2"] = again.InviteCode;
                    Save();
                    break;
                }
                case "rejoin":
                {
                    FederatedGuildLink before = Mine();
                    FederationResult res = Wait(FederationService.JoinAsync(owner, officer, S("invite2"), Ct));
                    FederatedGuildLink link = Mine();
                    int rows = FederatedGuildStore.All().FindAll(l => string.Equals(l.LocalGuild, Guild, StringComparison.OrdinalIgnoreCase)).Count;
                    Add("a guild that left can join another federation", res.Ok && link?.GuildId == S("recreated")
                        && link.LinkId != before?.LinkId && link.State == FederatedLinkState.Active && rows == 1,
                        $"ok={res.Ok} code={res.Code} federation={link?.GuildId} rows={rows}");
                    break;
                }

                // Identity steps: the harness plays the owner's browser and the operator's dashboard.
                case "claim":
                {
                    // Kept private: claiming an identity must not list a server its owner never chose to list.
                    cfg.Visibility = "Private";
                    cfg.Save();
                    KmhGlobalRegistration.Outcome o = KmhGlobalRegistration.BeginOwnerLinkAsync(Ct).GetAwaiter().GetResult();
                    Add("the owner starts setup on a server that already holds a private identity",
                        o.Ok && GlobalSetupState.Current_.PendingLinkId.Length > 0 && GlobalSetupState.Current_.BackendRegistered,
                        $"ok={o.Ok} detail={o.Detail} code={o.ErrorCode}");
                    break;
                }
                case "claimed":
                {
                    string key = KmhServerKey.Fingerprint;
                    int generation = cfg.SigningGeneration;
                    // What 'kmh global setup' does after approval: collect the grant, register, switch Global on.
                    KmhGlobalRegistration.Outcome polled = KmhGlobalRegistration.PollOwnerLinkAsync(Ct).GetAwaiter().GetResult();
                    KmhGlobalRegistration.Outcome reg = polled.Ok ? KmhGlobalRegistration.RegisterAsync(Ct).GetAwaiter().GetResult() : polled;
                    if (reg.Ok && !GlobalConfig.Current.Enabled) { GlobalConfig.Current.Enabled = true; GlobalConfig.Current.Save(); }
                    GlobalSetupState st = GlobalSetupState.Current_;
                    Add("the approval is collected and the server is owned now, with the same id, key and generation",
                        polled.Ok && reg.Ok && st.IsOwnerRegistered && !st.BackendRegistered && KmhServerIdentity.Id == me
                        && KmhServerKey.Fingerprint == key && GlobalConfig.Current.SigningGeneration == generation,
                        $"poll={polled.Detail} register={reg.Detail} {reg.ErrorCode} generation={GlobalConfig.Current.SigningGeneration}");
                    bool read = Refresh();
                    Add("its federation link still reads", read && Mine()?.State == FederatedLinkState.Active, $"read={read} state={Mine()?.State}");
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("and it heartbeats as an owned server", beat, $"code={KmhGlobalRegistrar.LastErrorCode}");
                    break;
                }
                case "staleproof":
                {
                    // Another machine presents the operator's code for A's id with its own key.
                    Persistence.KmhDataMeta.SetInstanceIdForTest(S("stale_target"));
                    string said = KmhIdentityChange.ProveRebindAsync(S("stale_code"), Ct).GetAwaiter().GetResult();
                    KmhIdentityChange.Stop();
                    Add("another key proves the ticket opened against A", said.StartsWith("Proved", StringComparison.Ordinal)
                        && KmhIdentityChange.Pending?.Kind == IdentityChangeKind.Rebind, said);
                    break;
                }
                case "rotate":
                case "recover":
                {
                    string kind = step == "rotate" ? IdentityChangeKind.Rotate : IdentityChangeKind.Recover;
                    string current = KmhServerKey.Fingerprint;
                    string said = KmhIdentityChange.BeginAsync(kind, Ct).GetAwaiter().GetResult();
                    KmhIdentityChange.Stop();
                    IdentityChangeState s = KmhIdentityChange.Pending;
                    Add($"the owner starts a {step}; the new key waits beside the current one",
                        s?.Kind == kind && s.Stage == IdentityChangeStage.AwaitingApproval && s.PendingLinkId.Length > 0
                        && KmhServerKey.StagedExists && KmhServerKey.Fingerprint == current && s.ToFingerprint != current, said);
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("heartbeats carry on with the current key until the owner approves", beat, $"code={KmhGlobalRegistrar.LastErrorCode}");
                    break;
                }
                case "rotated":
                case "recovered":
                {
                    KmhIdentityChange.RestartForTest = () => { };
                    int from = GlobalConfig.Current.SigningGeneration;
                    string outgoing = KmhServerKey.Fingerprint;
                    // The completer's own steps, run in the foreground: poll the approval, then change the key.
                    string said = null;
                    for (int i = 0; i < 15 && said != "done"; i++)
                    {
                        said = KmhIdentityChange.StepAsync(Ct).GetAwaiter().GetResult();
                        if (said == null) Thread.Sleep(1000);
                    }
                    string installed = KmhServerKey.Fingerprint;
                    Add("the server collects the approval and installs the new key itself, at the next generation",
                        said == "done" && KmhIdentityChange.Pending == null && !KmhServerKey.StagedExists && installed != outgoing
                        && GlobalConfig.Current.SigningGeneration == from + 1,
                        $"said={said} generation {from}->{GlobalConfig.Current.SigningGeneration}");
                    share["a_new_fp16"] = installed.Length >= 16 ? installed.Substring(0, 16) : installed;
                    Save();

                    // The outgoing key is kept on disk, and Global no longer takes anything it signs.
                    string previous = Path.Combine(Persistence.KmhDataPaths.IdentityDir, "global-signing.previous.key");
                    GlobalCallResult old = null;
                    if (File.Exists(previous))
                        using (System.Security.Cryptography.ECDsa k = System.Security.Cryptography.ECDsa.Create())
                        {
                            k.ImportPkcs8PrivateKey(Convert.FromBase64String(File.ReadAllText(previous).Trim()), out _);
                            HeartbeatBody stale = KmhGlobalRegistrar.BuildHeartbeat(DateTimeOffset.UtcNow);
                            stale.Signature = KmhGlobalSigning.Sign(k, stale.Canonical());
                            old = new KmhGlobalClient(url).PostAsync(KmhIdentityChange.HeartbeatPath, stale, null, Ct).GetAwaiter().GetResult();
                        }
                    Add("the outgoing key is kept beside the new one and no longer authenticates",
                        old != null && !old.Ok && old.ErrorCode == "KMH-SRV-303", $"kept={File.Exists(previous)} answer={old?.ErrorCode}");
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("the new key heartbeats", beat, $"code={KmhGlobalRegistrar.LastErrorCode}");

                    FederatedGuildLink waiting = Mine();
                    Add("the federation link waits to be proved again under the new key",
                        waiting?.State == FederatedLinkState.ReproofRequired, $"state={waiting?.State} detail={waiting?.Detail}");
                    FederationResult re = Wait(FederationService.ReproveAsync(owner, officer, Ct));
                    bool read = Refresh();
                    FederatedGuildLink proved = Mine();
                    Add("an officer proves it again, and it reads at the new generation",
                        re.Ok && read && proved?.State == FederatedLinkState.Active && proved.SigningGeneration == GlobalConfig.Current.SigningGeneration,
                        $"ok={re.Ok} code={re.Code} state={proved?.State} generation={proved?.SigningGeneration}");
                    break;
                }
                case "losekey":
                {
                    // Lost with its disk and no backup: the next load makes a key Global has never seen.
                    string lost = KmhServerKey.Fingerprint;
                    KmhServerKey.ResetForTest();
                    File.Delete(Path.Combine(Persistence.KmhDataPaths.IdentityDir, "global-signing.key"));
                    string fresh = KmhServerKey.Fingerprint;
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("a server whose key was lost is refused by Colony Nexus Global", !beat && fresh.Length > 0 && fresh != lost
                        && KmhGlobalRegistrar.LastErrorCode == "KMH-SRV-303", $"code={KmhGlobalRegistrar.LastErrorCode}");
                    string advice = KmhIdentityChange.BeginAsync(IdentityChangeKind.Recover, Ct).GetAwaiter().GetResult();
                    Add("with no owner to recover it, the server is sent to the operator for a rebind",
                        advice.Contains("kmh global rebind") && !KmhServerKey.StagedExists && KmhIdentityChange.Pending == null, advice);
                    bool read = Refresh();
                    Add("and it cannot read its federation meanwhile", !read, $"read={read}");
                    break;
                }
                case "rebind":
                {
                    string said = KmhIdentityChange.ProveRebindAsync(S("rebind_code"), Ct).GetAwaiter().GetResult();
                    KmhIdentityChange.Stop();
                    IdentityChangeState s = KmhIdentityChange.Pending;
                    Add("the server proves its new key with the operator's code", said.StartsWith("Proved", StringComparison.Ordinal)
                        && s?.Stage == IdentityChangeStage.AwaitingOperator && s.ToFingerprint == KmhServerKey.Fingerprint, said);
                    string early = KmhIdentityChange.RebindStepAsync(Ct).GetAwaiter().GetResult();
                    Add("and waits: nothing moves until the operator applies it",
                        early == null && KmhIdentityChange.Pending != null && GlobalConfig.Current.SigningGeneration == s?.FromGeneration,
                        $"said={early ?? "(waiting)"} generation={GlobalConfig.Current.SigningGeneration}");
                    break;
                }
                case "rebound":
                {
                    KmhIdentityChange.RestartForTest = () => { };
                    int from = GlobalConfig.Current.SigningGeneration;
                    string said = KmhIdentityChange.RebindStepAsync(Ct).GetAwaiter().GetResult();
                    Add("the server learns the operator applied it and takes its id over, privately, at the next generation",
                        said == "done" && KmhIdentityChange.Pending == null && GlobalConfig.Current.SigningGeneration == from + 1
                        && GlobalSetupState.Current_.BackendRegistered && !GlobalConfig.Current.Enabled && KmhServerIdentity.Id == me,
                        $"said={said} generation {from}->{GlobalConfig.Current.SigningGeneration}");
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    Add("its new key heartbeats", beat, $"code={KmhGlobalRegistrar.LastErrorCode}");
                    FederatedGuildLink kept = Mine();
                    Add("the link it proved with was kept, and waits to be proved again",
                        kept?.GuildId == S("recreated") && kept.State == FederatedLinkState.ReproofRequired,
                        $"federation={kept?.GuildId} state={kept?.State} detail={kept?.Detail}");
                    FederationResult re = Wait(FederationService.ReproveAsync(owner, officer, Ct));
                    bool read = Refresh();
                    int members = Mine()?.View?.Members?.FindAll(m => m.State == "Active").Count ?? 0;
                    Add("an officer proves it again and the federation reads as before", re.Ok && read && members == 2,
                        $"ok={re.Ok} code={re.Code} read={read} members={members}");
                    break;
                }
                case "revoked":
                {
                    bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                    string text = KmhGlobalRegistrar.DescribeRefusalFor("KMH-SRV-319");
                    Add("the owner's revocation stops the heartbeat, and the console says it is final",
                        !beat && KmhGlobalRegistrar.LastErrorCode == "KMH-SRV-319" && text.Contains("final"),
                        $"code={KmhGlobalRegistrar.LastErrorCode}");
                    bool read = Refresh();
                    Add("Federation closes here with the reason, and nothing can be read",
                        !read && FederationService.UnavailableReason() == "global.revoked",
                        $"read={read} unavailable={FederationService.UnavailableReason()}");
                    break;
                }
                default:
                    Add("a known step", false, "KMH_FED_LIVE_STEP is not one of the steps");
                    break;
            }
            return r;
        }
    }
}
