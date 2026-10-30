using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Global.Treasury;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Features.Treasury;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Guilds
{
    // Group money-move failures (rank change mid-flight, two officers, a restored backup, an unlink in flight):
    // silver must exist exactly once, and nobody moves it who could not move the guild's own.
    internal static class KmhConnectedGuildSelfTest
    {
        private static readonly CancellationToken Ct = CancellationToken.None;

        // Counts what it was asked, so "exactly once" is measured, not assumed.
        private sealed class FakeGlobal
        {
            public int Statuses, Deposits, Reserves, Acks;
            public readonly List<string> DepositOps = new List<string>();
            public readonly List<string> ReserveOps = new List<string>();
            public readonly List<bool>   AckDelivered = new List<bool>();

            public bool Unreachable;
            public string RefuseWith = "";
            // Refuses only value routes, like a link awaiting re-proof at Global; reads and acks still answer.
            public string RefuseValueWith = "";
            public string LinkId = "link-1";
            public string GuildId = "guild-1";
            public string ReservationId = "gres-1";
            public bool OmitReservationId;
            public string StateSays = "";
            public long Settled, Reserved, Available, Debt;

            public Task<GlobalCallResult> Send(string path, object body, CancellationToken ct)
            {
                bool value = false;
                if (path.EndsWith("/guilds/status", StringComparison.Ordinal)) Statuses++;
                // Counted whatever the body is: a personal request reaching here is a send the checks must see.
                else if (path.EndsWith("/treasury/deposit", StringComparison.Ordinal))
                { Deposits++; DepositOps.Add((body as GuildDepositBody)?.OperationId ?? ""); value = true; }
                else if (path.EndsWith("/treasury/reserve", StringComparison.Ordinal))
                { Reserves++; ReserveOps.Add((body as GuildReserveBody)?.OperationId ?? ""); value = true; }
                else if (path.EndsWith("/treasury/ack", StringComparison.Ordinal))
                { Acks++; AckDelivered.Add((body as GuildAckBody)?.Delivered ?? false); }

                if (Unreachable) return Task.FromResult(GlobalCallResult.Offline("offline"));
                if (!string.IsNullOrEmpty(RefuseWith))
                    return Task.FromResult(new GlobalCallResult { Ok = false, Status = 409, ErrorCode = RefuseWith });
                if (value && !string.IsNullOrEmpty(RefuseValueWith))
                    return Task.FromResult(new GlobalCallResult { Ok = false, Status = 409, ErrorCode = RefuseValueWith });

                JObject b = new JObject { ["link_id"] = LinkId, ["guild_id"] = GuildId, ["invite"] = "ABCD-EFGH-JKMN" };
                if (!string.IsNullOrEmpty(StateSays)) b["state"] = StateSays;
                if (!OmitReservationId) b["reservation_id"] = ReservationId;
                b["balance"] = new JObject
                {
                    ["settled"] = Settled, ["reserved"] = Reserved,
                    ["available"] = Available, ["debt"] = Debt, ["frozen"] = false,
                };
                return Task.FromResult(new GlobalCallResult { Ok = true, Status = 200, Body = b });
            }
        }

        private sealed class Bench : IDisposable
        {
            public readonly FakeGlobal Global = new FakeGlobal();
            public readonly string Guild;
            public readonly string Owner;

            private readonly string _hadIdentity;
            private readonly bool _hadTreasury;
            private readonly List<string> _members = new List<string>();

            public Bench(string tag, long vaultSilver, bool treasuryOn = true)
            {
                // Op ids are namespaced by the data-set identity, which the offline runner boots without.
                _hadIdentity = KmhServerIdentity.Id;
                if (string.IsNullOrEmpty(_hadIdentity))
                    Persistence.KmhDataMeta.SetInstanceIdForTest("selftestsrv");

                ConnectedTreasuryStore.ClearForTest();
                FederatedGuildStore.ClearForTest();
                ConnectedTreasuryService.AvailableForTest = true;
                ConnectedTreasuryService.SenderForTest = Global.Send;
                // Money rules run with the release switch on unless a case is about the switch itself.
                _hadTreasury = FeaturesConfig.Current.ConnectedTreasury;
                FeaturesConfig.Current.ConnectedTreasury = treasuryOn;

                Guild = "kmh-st-guild-" + tag + "-" + Salt;
                Owner = Who("owner");
                if (!GuildStore.CreateGuildAndJoinAsAdmin(Owner, Guild, out string why))
                    throw new InvalidOperationException("could not seed a guild: " + why);
                _members.Add(Owner);
                if (vaultSilver > 0) TreasuryStore.DepositGuildSilver(Guild, (int)vaultSilver, Owner, "selftest seed");
            }

            public string Who(string role) => "kmh-st-" + role + "-" + Salt + "-" + Guild.GetHashCode().ToString("x");

            // Seeded directly so a case can name its exact rank without depending on who may promote whom.
            public string Member(string role, string rank)
            {
                string u = Who(role);
                GuildStore.AddMember(u, Guild, rank);
                _members.Add(u);
                return u;
            }

            // The per-rank daily caps ARE the guild's treasury permissions, set exactly as an admin would in game.
            public void AllowRank(string rank, int cap)
            {
                GuildSettingsDto s = new GuildSettingsDto();
                if (rank == GuildMemberDto.RankOfficer)        s.OfficerDailyWithdrawCap = cap;
                else if (rank == GuildMemberDto.RankModerator) s.ModeratorDailyWithdrawCap = cap;
                else if (rank == GuildMemberDto.RankMember)    s.MemberDailyWithdrawCap = cap;
                else                                           s.AdminDailyWithdrawCap = cap;
                GuildStore.SaveSettings(Owner, s);
            }

            // A link as Guild Federation leaves it; how one is made is KmhFederationSelfTest's business.
            public FederatedGuildLink Link()
            {
                FederatedGuildStore.Remember(Global.LinkId, Global.GuildId, Guild, "officer-global-id",
                                             GlobalConfig.Current.SigningGeneration);
                return FederatedGuildStore.ForGuild(Guild);
            }

            public long Vault => TreasuryStore.GetGuildSilver(Guild);

            public ConnectedOp Only
            {
                get
                {
                    FederatedGuildLink l = FederatedGuildStore.ForGuild(Guild);
                    if (l == null) return null;
                    List<ConnectedOp> ops = ConnectedTreasuryStore.ForLink(l.LinkId);
                    return ops.Count > 0 ? ops[ops.Count - 1] : null;
                }
            }

            public int OpCount
            {
                get
                {
                    FederatedGuildLink l = FederatedGuildStore.ForGuild(Guild);
                    return l == null ? 0 : ConnectedTreasuryStore.ForLink(l.LinkId).Count;
                }
            }

            public void Dispose()
            {
                if (string.IsNullOrEmpty(_hadIdentity)) Persistence.KmhDataMeta.SetInstanceIdForTest(_hadIdentity);
                FeaturesConfig.Current.ConnectedTreasury = _hadTreasury;
                ConnectedTreasuryService.SenderForTest = null;
                ConnectedTreasuryService.AvailableForTest = null;
                ConnectedTreasuryStore.ClearForTest();
                FederatedGuildStore.ClearForTest();
                // Owner last: leaving disbands a guild once nobody is left in it.
                for (int i = _members.Count - 1; i >= 0; i--) GuildStore.Leave(_members[i], out _);
            }
        }

        // Unique per run: a suite executed twice in one process must not inherit the first run's guild.
        private static readonly string Salt = Guid.NewGuid().ToString("N").Substring(0, 6);

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            LinkRules(r);
            Authority(r);
            Deposits(r);
            Withdrawals(r);
            Restore(r);
            Outage(r);
            AuditRegressions(r);
            return r;
        }

        // A refused guild deposit must refund, and a same-name re-created guild must not inherit the link.
        private static void AuditRegressions(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("dis1", 500))
            {
                b.Link();
                string before = FederatedGuildStore.ForGuild(b.Guild)?.State;
                GuildStore.Leave(b.Owner, out _);   // the sole member leaving disbands the guild
                string afterDisband = FederatedGuildStore.ForGuild(b.Guild)?.State;

                string heir = b.Who("heir");
                bool recreated = GuildStore.CreateGuildAndJoinAsAdmin(heir, b.Guild, out _);
                int reservesBefore = b.Global.Reserves;
                ConnectedResult take = ConnectedGuildService.WithdrawAsync(heir, 100, Ct).GetAwaiter().GetResult();
                ConnectedResult give = ConnectedGuildService.DepositAsync(heir, 100, Ct).GetAwaiter().GetResult();
                bool linked = ConnectedGuildService.LinkFor(heir) != null;
                GuildStore.Leave(heir, out _);

                // Ended locally at once, with Global told via the outbox, so a gone guild does not stay a member there.
                bool queued = Federation.FederationOutbox.Leaves().Exists(l => l.LinkId == "link-1" && l.Reason == "guild_disbanded");
                Federation.FederationOutbox.LeaveDelivered("link-1");
                bool ok = before == FederatedLinkState.Active && afterDisband == FederatedLinkState.Left && queued && recreated
                       && !take.Ok && !give.Ok && !linked && b.Global.Reserves == reservesBefore;
                r.Add(("guild federation: a guild re-created under a disbanded one's name inherits none of its link",
                       ok, $"link {before}->{afterDisband}, leave queued={queued}, recreated={recreated}, withdraw={take.Ok}/{take.Code}, "
                           + $"deposit={give.Ok}/{give.Code}, linked={linked}, reserves sent={b.Global.Reserves - reservesBefore}"));
            }

            using (Bench b = new Bench("off1", 500, treasuryOn: false))
            {
                b.Link();
                ConnectedResult give = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                ConnectedResult take = ConnectedGuildService.WithdrawAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                ConnectedResult mine = ConnectedTreasuryService.DepositAsync(b.Owner, "player-global-id", 50, Ct).GetAwaiter().GetResult();
                ConnectedResult grant = ConnectedTreasuryService.WithdrawAsync(b.Owner, "player-global-id", 50, Ct, "grant").GetAwaiter().GetResult();
                bool ok = give.Code == "treasury.disabled" && take.Code == "treasury.disabled"
                       && mine.Code == "treasury.disabled" && grant.Code == "treasury.disabled"
                       && b.Vault == 500 && b.Global.Deposits == 0 && b.Global.Reserves == 0 && b.OpCount == 0;
                r.Add(("connected treasury off: no new deposit or withdrawal starts, guild or personal, and nothing is sent",
                       ok, $"guild={give.Code}/{take.Code} personal={mine.Code}/{grant.Code} vault={b.Vault} sent={b.Global.Deposits}/{b.Global.Reserves}"));
            }

            using (Bench b = new Bench("off2", 500))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                FeaturesConfig.Current.ConnectedTreasury = false;
                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                bool ok = b.Only?.Phase == ConnectedOpPhase.Credited && b.Vault == 400 && b.Global.Deposits >= 1;
                r.Add(("connected treasury off: a deposit already escrowed still completes through reconciliation",
                       ok, $"phase={b.Only?.Phase} vault={b.Vault} sent={b.Global.Deposits}"));
            }

            // Global answers 980 only for an op id it holds nothing for, so the silver comes back.
            using (Bench b = new Bench("off3", 500))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-TRS-980";
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                bool ok = !res.Ok && !res.Pending && b.Vault == 500 && b.Only?.Phase == ConnectedOpPhase.Cancelled;
                r.Add(("connected treasury off at Global: a refused deposit is refunded to the vault, once",
                       ok, $"code={res.Code} pending={res.Pending} vault={b.Vault} phase={b.Only?.Phase}"));
            }

            r.Add(("guild federation: an invite is signed over its code however it was typed",
                   GuildInviteRevokeBody.NormalizeInvite(" abcd-efgh-jkmn ") == "ABCDEFGHJKMN"
                   && GuildInviteRevokeBody.NormalizeInvite("") == "", ""));

            r.Add(("guild federation: a guild from before ids existed keeps a stable one derived from its name",
                   GuildStore.LegacyIncarnationFor("Iron Ledger") == GuildStore.LegacyIncarnationFor("iron ledger")
                   && GuildStore.LegacyIncarnationFor("Iron Ledger") != GuildStore.LegacyIncarnationFor("Iron Ledger2"), ""));

            using (Bench b = new Bench("ref1", 500))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-TRS-964";
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                long afterRefusal = b.Vault;
                string phase = b.Only?.Phase;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                bool ok = !res.Ok && !res.Pending && afterRefusal == 500 && b.Vault == 500
                       && phase == ConnectedOpPhase.Cancelled;
                r.Add(("guild deposit: a refusal that proves nothing landed returns the silver to the vault, once",
                       ok, $"code={res.Code} pending={res.Pending} vault={afterRefusal}->{b.Vault} phase={phase}"));
            }

            using (Bench b = new Bench("ref2", 0))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-TRS-964";
                ConnectedResult first = ConnectedGuildService.WithdrawAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                b.Global.RefuseValueWith = "";
                ConnectedResult second = ConnectedGuildService.WithdrawAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                bool ok = !first.Ok && !first.Pending && second.Ok && b.Vault == 100;
                r.Add(("guild withdraw: a refused reserve ends the operation, so the guild can move silver again",
                       ok, $"first={first.Code}/{first.Pending} second={second.Ok}/{second.Code} vault={b.Vault}"));
            }
        }


        private static void LinkRules(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("fed1", 0))
            {
                FederatedGuildLink link = b.Link();
                r.Add(("guild federation: a link records the KMH guild id, never the guild's name",
                       link != null && link.GuildId == "guild-1" && link.LinkId == "link-1"
                       && link.GuildId != b.Guild && link.State == FederatedLinkState.Active,
                       $"guild_id={link?.GuildId} link_id={link?.LinkId} state={link?.State}"));
            }

            using (Bench b = new Bench("fed3", 0))
            {
                b.Link();
                FederatedGuildLink byName = FederatedGuildStore.ForGuild(b.Guild);
                FederatedGuildLink byId = FederatedGuildStore.ById("link-1");
                r.Add(("guild federation: a link is found by its id, and the name is only a local pointer to it",
                       byId != null && ReferenceEquals(byName, byId) && byId.LocalGuild == b.Guild,
                       $"byId={(byId == null ? "null" : byId.LinkId)} local={byId?.LocalGuild}"));
            }

            using (Bench b = new Bench("fed4", 0))
            {
                b.Link();
                b.Global.LinkId = "link-2";
                FederatedGuildStore.Remember("link-2", "guild-1", b.Guild, "officer-global-id", 1);
                r.Add(("guild federation: a re-issued link replaces the old row for that guild",
                       FederatedGuildStore.All().Count == 1
                       && FederatedGuildStore.ForGuild(b.Guild).LinkId == "link-2",
                       $"rows={FederatedGuildStore.All().Count} link={FederatedGuildStore.ForGuild(b.Guild)?.LinkId}"));
            }

            using (Bench b = new Bench("fed5", 0))
            {
                b.Link();
                FederatedGuildStore.SetState("link-1", FederatedLinkState.Left, "left");
                FederatedGuildLink after = FederatedGuildStore.ForGuild(b.Guild);
                ConnectedResult res = ConnectedGuildService.WithdrawAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild federation: a link that left keeps the mapping visible and moves no value",
                       after != null && after.State == FederatedLinkState.Left && !after.MayMoveValue && !after.MayRead
                       && !res.Ok && b.Global.Reserves == 0,
                       $"state={after?.State} code={res.Code} reserves={b.Global.Reserves}"));
            }

            using (Bench b = new Bench("fed6", 500))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                bool inFlight = ConnectedTreasuryStore.OpenOpForLink("link-1") != null;
                Federation.FederationService.UnavailableForTest = "";
                Federation.FederationResult gone;
                try
                {
                    gone = Federation.FederationService.LeaveAsync(b.Owner, "officer-global-id", Ct).GetAwaiter().GetResult();
                }
                finally { Federation.FederationService.UnavailableForTest = null; }
                r.Add(("guild federation: leaving is refused while a treasury operation is still in flight",
                       inFlight && !gone.Ok && gone.Code == "operation.inflight"
                       && FederatedGuildStore.ForGuild(b.Guild).State == FederatedLinkState.Active,
                       $"inflight={inFlight} code={gone.Code} state={FederatedGuildStore.ForGuild(b.Guild)?.State}"));
            }

            using (Bench b = new Bench("fed7", 0))
            {
                b.Link();
                b.Global.StateSays = FederatedLinkState.ReproofRequired;
                ConnectedGuildService.RefreshAsync("link-1", Ct).GetAwaiter().GetResult();
                r.Add(("guild federation: a re-proof demanded at Global shows up on the next status read",
                       FederatedGuildStore.ById("link-1").State == FederatedLinkState.ReproofRequired
                       && b.Global.Statuses == 1,
                       $"state={FederatedGuildStore.ById("link-1")?.State} reads={b.Global.Statuses}"));
            }

            using (Bench b = new Bench("fed8", 0))
            {
                b.Link();
                b.Global.Settled = 4200; b.Global.Reserved = 100; b.Global.Available = 4100; b.Global.Debt = 7;
                ConnectedGuildService.RefreshAsync("link-1", Ct).GetAwaiter().GetResult();
                FederatedBalance bal = FederatedGuildStore.ById("link-1").Balance;
                r.Add(("guild federation: the federated balance is stored as read from Global, with its read time",
                       bal != null && bal.Settled == 4200 && bal.Reserved == 100 && bal.Debt == 7
                       && bal.ReadUtcTicks > 0,
                       $"settled={bal?.Settled} reserved={bal?.Reserved} debt={bal?.Debt} read={bal?.ReadUtcTicks}"));
            }
        }


        private static void Authority(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("auth1", 500))
            {
                b.Link();
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: the owner may move federated silver",
                       res.Ok && b.Vault == 400, $"ok={res.Ok} vault={b.Vault}"));
            }

            using (Bench b = new Bench("auth2", 500))
            {
                b.Link();
                string admin = b.Member("admin", GuildMemberDto.RankAdmin);
                ConnectedResult res = ConnectedGuildService.DepositAsync(admin, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: an admin may move federated silver",
                       res.Ok && b.Vault == 400, $"ok={res.Ok} vault={b.Vault}"));
            }

            using (Bench b = new Bench("auth3", 500))
            {
                b.Link();
                string member = b.Member("member", GuildMemberDto.RankMember);
                ConnectedResult res = ConnectedGuildService.DepositAsync(member, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: a plain member is refused, and no silver leaves the vault",
                       !res.Ok && b.Vault == 500 && b.Global.Deposits == 0 && b.OpCount == 0,
                       $"ok={res.Ok} vault={b.Vault} sent={b.Global.Deposits} ops={b.OpCount}"));
            }

            using (Bench b = new Bench("auth4", 500))
            {
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                ConnectedResult before = ConnectedGuildService.DepositAsync(officer, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: an officer is refused while the guild gives that rank no treasury access",
                       !before.Ok && b.Vault == 500, $"ok={before.Ok} vault={b.Vault}"));
            }

            using (Bench b = new Bench("auth5", 500))
            {
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                b.AllowRank(GuildMemberDto.RankOfficer, 1000);
                ConnectedResult after = ConnectedGuildService.DepositAsync(officer, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: the same officer is allowed once the guild's own permissions allow it",
                       after.Ok && b.Vault == 400, $"ok={after.Ok} vault={b.Vault}"));
            }

            using (Bench b = new Bench("auth6", 500))
            {
                b.Link();
                string member = b.Member("member", GuildMemberDto.RankMember);
                b.AllowRank(GuildMemberDto.RankMember, 250);
                ConnectedResult res = ConnectedGuildService.DepositAsync(member, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: a member allowed by the guild's own permissions may move silver",
                       res.Ok && b.Vault == 400, $"ok={res.Ok} vault={b.Vault}"));
            }

            using (Bench b = new Bench("auth7", 500))
            {
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                b.AllowRank(GuildMemberDto.RankOfficer, 1000);
                ConnectedGuildService.DepositAsync(officer, 100, Ct).GetAwaiter().GetResult();
                b.AllowRank(GuildMemberDto.RankOfficer, 0);
                ConnectedResult after = ConnectedGuildService.DepositAsync(officer, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: withdrawing a rank's permission refuses the next operation immediately",
                       !after.Ok && b.Vault == 400 && b.Global.Deposits == 1,
                       $"ok={after.Ok} vault={b.Vault} sent={b.Global.Deposits}"));
            }

            using (Bench b = new Bench("auth8", 5000))
            {
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                b.AllowRank(GuildMemberDto.RankOfficer, 150);
                ConnectedResult over = ConnectedGuildService.DepositAsync(officer, 400, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: the per-rank daily cap is not bypassed by routing through the federation",
                       !over.Ok && b.Vault == 5000 && b.Global.Deposits == 0,
                       $"ok={over.Ok} vault={b.Vault} sent={b.Global.Deposits}"));
            }

            using (Bench b = new Bench("auth9", 500))
            {
                // Managing a federation is a settings-level permission, not one invented for it.
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                b.AllowRank(GuildMemberDto.RankOfficer, 1000);
                Federation.FederationService.UnavailableForTest = "";
                Federation.FederationResult res;
                try { res = Federation.FederationService.LeaveAsync(officer, "officer-global-id", Ct).GetAwaiter().GetResult(); }
                finally { Federation.FederationService.UnavailableForTest = null; }
                r.Add(("guild authority: moving silver does not by itself let a rank take the guild out of its federation",
                       !res.Ok && res.Code == "refused" && FederatedGuildStore.ForGuild(b.Guild).State == FederatedLinkState.Active,
                       $"ok={res.Ok} code={res.Code} state={FederatedGuildStore.ForGuild(b.Guild)?.State}"));
            }

            using (Bench b = new Bench("auth10", 500))
            {
                b.Link();
                string mod = b.Member("mod", GuildMemberDto.RankModerator);
                b.AllowRank(GuildMemberDto.RankModerator, 1000);
                ConnectedResult res = ConnectedGuildService.DepositAsync(mod, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild authority: a moderator follows the same rule as any other rank the guild permits",
                       res.Ok && b.Vault == 400, $"ok={res.Ok} vault={b.Vault}"));
            }

            // The service's rank check is a courtesy; the escrow's own check protects the vault, so it is exercised
            // directly here and a future caller that skips the service is still refused.
            using (Bench b = new Bench("auth11", 500))
            {
                b.Link();
                string member = b.Member("member", GuildMemberDto.RankMember);
                bool moved = GuildStore.EscrowForFederation(member, 100, "mut-escrow-member", out string _,
                                                            out string why);
                r.Add(("guild authority: the escrow itself refuses a rank the guild gives no treasury access",
                       !moved && b.Vault == 500 && !string.IsNullOrEmpty(why),
                       $"moved={moved} vault={b.Vault} why={why}"));
            }

            using (Bench b = new Bench("auth12", 500))
            {
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                b.AllowRank(GuildMemberDto.RankOfficer, 1000);
                bool moved = GuildStore.EscrowForFederation(officer, 100, "mut-escrow-officer", out string _,
                                                            out string why);
                // The other half, so the refusal above is a rule, not an escrow that never works.
                r.Add(("guild authority: the escrow itself allows a rank the guild does permit",
                       moved && b.Vault == 400, $"moved={moved} vault={b.Vault} why={why}"));
            }

            using (Bench b = new Bench("auth13", 5000))
            {
                b.Link();
                string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                b.AllowRank(GuildMemberDto.RankOfficer, 150);
                bool over = GuildStore.EscrowForFederation(officer, 400, "mut-escrow-cap", out string _, out string _);
                r.Add(("guild authority: the escrow itself enforces the per-rank daily cap",
                       !over && b.Vault == 5000, $"moved={over} vault={b.Vault}"));
            }

            using (Bench b = new Bench("auth14", 500))
            {
                b.Link();
                // A retry after a crash must take the silver once and not charge the daily cap twice.
                bool first = GuildStore.EscrowForFederation(b.Owner, 100, "mut-escrow-twice", out string _, out string _);
                bool again = GuildStore.EscrowForFederation(b.Owner, 100, "mut-escrow-twice", out string _, out string _);
                r.Add(("guild authority: repeating an escrow with the same marker takes the silver once",
                       first && again && b.Vault == 400, $"first={first} again={again} vault={b.Vault}"));
            }
        }


        private static void Deposits(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("dep1", 500))
            {
                b.Link();
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 200, Ct).GetAwaiter().GetResult();
                r.Add(("guild deposit: the vault is escrowed locally and credited at Global exactly once",
                       res.Ok && b.Vault == 300 && b.Global.Deposits == 1
                       && b.Only.Phase == ConnectedOpPhase.Credited,
                       $"ok={res.Ok} vault={b.Vault} sent={b.Global.Deposits} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench("dep2", 500))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                string admin = b.Member("admin", GuildMemberDto.RankAdmin);
                ConnectedResult second = ConnectedGuildService.DepositAsync(admin, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild deposit: a second officer cannot open a second operation on the same vault",
                       !second.Ok && second.Code == "operation.inflight" && b.Vault == 400 && b.OpCount == 1,
                       $"code={second.Code} vault={b.Vault} ops={b.OpCount}"));
            }

            using (Bench b = new Bench("dep3", 500))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                long escrowed = b.Vault;

                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                bool sameId = b.Global.DepositOps.Count > 0
                           && b.Global.DepositOps[0] == b.Global.DepositOps[b.Global.DepositOps.Count - 1];
                r.Add(("guild deposit: a retry after an outage reuses the operation id and takes the silver once",
                       escrowed == 400 && b.Vault == 400 && sameId && b.Only.Phase == ConnectedOpPhase.Credited,
                       $"vault={b.Vault} ops={string.Join(",", b.Global.DepositOps)} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench("dep4", 500))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                string opId = b.Only.Id;

                // Simulates a crash before the phase write; recovery must ask the vault marker, not its own memory.
                ConnectedTreasuryStore.Advance(opId, ConnectedOpPhase.Opening);
                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild deposit: a crash before the phase was written is recovered from the vault marker",
                       b.Only.Phase == ConnectedOpPhase.Credited && b.Vault == 400 && b.Global.Deposits >= 1,
                       $"phase={b.Only?.Phase} vault={b.Vault}"));
            }

            using (Bench b = new Bench("dep5", 500))
            {
                b.Link();
                ConnectedOp ghost = ConnectedTreasuryStore.OpenGuild(b.Owner, "link-1", b.Guild,
                                                                      ConnectedOpKind.Deposit, 100);
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild deposit: an operation whose escrow never happened is cancelled, never credited",
                       ConnectedTreasuryStore.Find(ghost.Id).Phase == ConnectedOpPhase.Cancelled
                       && b.Global.Deposits == 0 && b.Vault == 500,
                       $"phase={ConnectedTreasuryStore.Find(ghost.Id)?.Phase} sent={b.Global.Deposits}"));
            }

            using (Bench b = new Bench("dep6", 500))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-GLD-959";
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild deposit: a re-proof demanded mid-deposit leaves the silver escrowed, not lost",
                       res.Pending && b.Only.Phase == ConnectedOpPhase.Escrowed && b.Vault == 400
                       && FederatedGuildStore.ById("link-1").State == FederatedLinkState.ReproofRequired,
                       $"phase={b.Only?.Phase} vault={b.Vault} state={FederatedGuildStore.ById("link-1")?.State}"));
            }
        }


        private static void Withdrawals(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("wd1", 0))
            {
                b.Link();
                ConnectedResult res = ConnectedGuildService.WithdrawAsync(b.Owner, 300, Ct).GetAwaiter().GetResult();
                r.Add(("guild withdrawal: the hold is taken first, then the vault is credited, then it settles",
                       res.Ok && b.Global.Reserves == 1 && b.Vault == 300
                       && b.Only.Phase == ConnectedOpPhase.Settled && b.Global.Acks == 1
                       && b.Global.AckDelivered[0],
                       $"ok={res.Ok} vault={b.Vault} phase={b.Only?.Phase} acks={b.Global.Acks}"));
            }

            using (Bench b = new Bench("wd2", 0))
            {
                b.Link();
                b.Global.OmitReservationId = true;
                ConnectedResult res = ConnectedGuildService.WithdrawAsync(b.Owner, 300, Ct).GetAwaiter().GetResult();
                r.Add(("guild withdrawal: nothing is credited when Global names no reservation to settle against",
                       res.Pending && b.Vault == 0 && b.Only.Phase == ConnectedOpPhase.Reserving,
                       $"code={res.Code} vault={b.Vault} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench("wd3", 0))
            {
                b.Link();
                ConnectedGuildService.WithdrawAsync(b.Owner, 300, Ct).GetAwaiter().GetResult();
                long after = b.Vault;
                // Simulates a crash after the credit, before the ack.
                ConnectedTreasuryStore.Advance(b.Only.Id, ConnectedOpPhase.Delivering, deliveryId: b.Only.Id);
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild withdrawal: replaying the delivery step credits the vault once, never twice",
                       after == 300 && b.Vault == 300 && b.Only.Phase == ConnectedOpPhase.Settled,
                       $"vault={b.Vault} phase={b.Only?.Phase}"));
            }

            using (Bench b = new Bench("wd4", 0))
            {
                b.Link();
                ConnectedGuildService.WithdrawAsync(b.Owner, 300, Ct).GetAwaiter().GetResult();
                int acks = b.Global.Acks;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild withdrawal: a settled operation is not acknowledged again by reconciliation",
                       acks == 1 && b.Global.Acks == 1 && b.Vault == 300,
                       $"acks={b.Global.Acks} vault={b.Vault}"));
            }

            using (Bench b = new Bench("wd5", 0))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-TRS-955";
                ConnectedResult res = ConnectedGuildService.WithdrawAsync(b.Owner, 300, Ct).GetAwaiter().GetResult();
                r.Add(("guild withdrawal: a refused hold credits nothing and leaves the operation open",
                       res.Pending && b.Vault == 0 && b.Only.Phase == ConnectedOpPhase.Reserving
                       && b.Only.Detail == "KMH-TRS-955",
                       $"phase={b.Only?.Phase} vault={b.Vault} detail={b.Only?.Detail}"));
            }

            using (Bench b = new Bench("wd6", 0))
            {
                b.Link();
                ConnectedGuildService.WithdrawAsync(b.Owner, 300, Ct).GetAwaiter().GetResult();
                // Re-proof set only after delivery: the ack must still go through, or a real delivery is carried as debt.
                ConnectedTreasuryStore.Advance(b.Only.Id, ConnectedOpPhase.Delivered);
                FederatedGuildStore.SetState("link-1", FederatedLinkState.ReproofRequired, "restored");
                int before = b.Global.Acks;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild withdrawal: re-proof becoming required does not block acknowledging silver already delivered",
                       b.Global.Acks == before + 1 && b.Only.Phase == ConnectedOpPhase.Settled && b.Vault == 300,
                       $"acks={b.Global.Acks} phase={b.Only?.Phase} vault={b.Vault}"));
            }
        }


        private static void Restore(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("res1", 500))
            {
                b.Link();
                FederatedGuildStore.InvalidateForGeneration(GlobalConfig.Current.SigningGeneration + 1);
                FederatedGuildLink link = FederatedGuildStore.ById("link-1");
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild restore: a changed signing generation blocks federated value and keeps the mapping",
                       link.State == FederatedLinkState.ReproofRequired && link.MayRead && !link.MayMoveValue
                       && !res.Ok && res.Code == "guild.reproof" && b.Vault == 500 && b.Global.Deposits == 0,
                       $"state={link.State} code={res.Code} vault={b.Vault}"));
            }

            using (Bench b = new Bench("res2", 500))
            {
                b.Link();
                FederatedGuildStore.InvalidateForGeneration(GlobalConfig.Current.SigningGeneration + 1);
                bool localStillWorks = GuildStore.WithdrawFromGuild(b.Owner, 100, out string why);
                r.Add(("guild restore: the local guild vault keeps working while the federation is blocked",
                       localStillWorks && b.Vault == 400, $"ok={localStillWorks} why={why} vault={b.Vault}"));
            }

            using (Bench b = new Bench("res3", 500))
            {
                b.Link();
                FederatedGuildStore.InvalidateForGeneration(GlobalConfig.Current.SigningGeneration + 1);
                Federation.FederationService.UnavailableForTest = "";
                Federation.FederationResult proved;
                try { proved = Federation.FederationService.ReproveAsync(b.Owner, "officer-global-id", Ct).GetAwaiter().GetResult(); }
                finally { Federation.FederationService.UnavailableForTest = null; }
                ConnectedResult after = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                r.Add(("guild restore: a successful re-proof restores value movement",
                       proved.Ok && FederatedGuildStore.ById("link-1").MayMoveValue && after.Ok && b.Vault == 400,
                       $"proved={proved.Ok}/{proved.Code} deposit={after.Ok} vault={b.Vault}"));
            }

            using (Bench b = new Bench("res4", 500))
            {
                b.Link();
                FederatedGuildStore.InvalidateForGeneration(GlobalConfig.Current.SigningGeneration + 1);
                b.Global.RefuseWith = "KMH-GLD-960";
                Federation.FederationService.UnavailableForTest = "";
                Federation.FederationResult refused;
                try { refused = Federation.FederationService.ReproveAsync(b.Owner, "officer-global-id", Ct).GetAwaiter().GetResult(); }
                finally { Federation.FederationService.UnavailableForTest = null; }
                r.Add(("guild restore: a refused re-proof leaves the link blocked",
                       !refused.Ok && !FederatedGuildStore.ById("link-1").MayMoveValue,
                       $"ok={refused.Ok} state={FederatedGuildStore.ById("link-1")?.State}"));
            }

            using (Bench b = new Bench("res5", 0))
            {
                b.Link();
                FederatedGuildStore.ById("link-1").DataSetId = "some-other-data-set";
                FederatedGuildStore.InvalidateForIdentity();
                r.Add(("guild restore: a link proved by a different data set stops moving value",
                       FederatedGuildStore.ById("link-1").State == FederatedLinkState.ReproofRequired
                       && FederatedGuildStore.ById("link-1").MayRead,
                       $"state={FederatedGuildStore.ById("link-1")?.State}"));
            }

            using (Bench b = new Bench("res6", 0))
            {
                b.Link();
                FederatedGuildStore.Remember("link-other", "guild-1", b.Guild + "-second", "officer-global-id",
                                             GlobalConfig.Current.SigningGeneration);
                FederatedGuildStore.ById("link-1").SigningGeneration = GlobalConfig.Current.SigningGeneration - 1;
                FederatedGuildStore.InvalidateForGeneration(GlobalConfig.Current.SigningGeneration);
                r.Add(("guild restore: one stale link does not invalidate another that is still current",
                       FederatedGuildStore.ById("link-1").State == FederatedLinkState.ReproofRequired
                       && FederatedGuildStore.ById("link-other").State == FederatedLinkState.Active,
                       $"stale={FederatedGuildStore.ById("link-1")?.State} healthy={FederatedGuildStore.ById("link-other")?.State}"));
            }

            using (Bench b = new Bench("res7", 0))
            {
                b.Link();
                FederatedGuildStore.Revoke("link-1", "unlinked");
                FederatedGuildStore.ById("link-1").SigningGeneration = GlobalConfig.Current.SigningGeneration - 1;
                FederatedGuildStore.InvalidateForGeneration(GlobalConfig.Current.SigningGeneration);
                FederatedGuildStore.ById("link-1").DataSetId = "elsewhere";
                FederatedGuildStore.InvalidateForIdentity();
                r.Add(("guild restore: a revoked link stays revoked through generation and identity sweeps",
                       FederatedGuildStore.ById("link-1").State == FederatedLinkState.Revoked,
                       $"state={FederatedGuildStore.ById("link-1")?.State}"));
            }
        }


        private static void Outage(List<(string, bool, string)> r)
        {
            using (Bench b = new Bench("out1", 500))
            {
                b.Link();
                ConnectedTreasuryService.AvailableForTest = false;
                ConnectedResult res = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                bool localStillWorks = GuildStore.WithdrawFromGuild(b.Owner, 100, out _);
                r.Add(("guild outage: federated moves are refused while the local guild vault keeps working",
                       !res.Ok && res.Code == "global.unavailable" && localStillWorks && b.Vault == 400,
                       $"code={res.Code} local={localStillWorks} vault={b.Vault}"));
            }

            using (Bench b = new Bench("out2", 500))
            {
                b.Link();
                ConnectedTreasuryService.AvailableForTest = false;
                int advanced = ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild outage: reconciliation does nothing at all while Global is away",
                       advanced == 0 && b.Vault == 500 && b.Global.Deposits == 0,
                       $"advanced={advanced} vault={b.Vault}"));
            }

            using (Bench b = new Bench("out3", 500))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild outage: an operation left open by an outage finishes when Global returns",
                       b.Only.Phase == ConnectedOpPhase.Credited && b.Vault == 400 && b.Global.Deposits >= 1,
                       $"phase={b.Only?.Phase} vault={b.Vault} sent={b.Global.Deposits}"));
            }

            // Suspension cases check the local vault, not a flag: that is what a player would notice.
            using (Bench b = new Bench("susp1", 500))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-TRS-965";

                ConnectedResult refused = ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                bool localWithdraw = GuildStore.WithdrawFromGuild(b.Owner, 50, out _);
                bool localDeposit = TreasuryStore.DepositGuildSilver(b.Guild, 25, b.Owner, "selftest local");

                // 500 - 100 escrowed - 50 withdrawn + 25 deposited.
                r.Add(("guild suspension: the local guild vault keeps working while federated silver is suspended",
                       refused.Pending && localWithdraw && localDeposit && b.Vault == 375,
                       $"pending={refused.Pending} withdraw={localWithdraw} deposit={localDeposit} vault={b.Vault}"));
            }

            using (Bench b = new Bench("susp2", 500))
            {
                b.Link();
                b.Global.RefuseValueWith = "KMH-TRS-965";
                ConnectedGuildService.DepositAsync(b.Owner, 100, Ct).GetAwaiter().GetResult();
                long escrowedVault = b.Vault;

                b.Global.RefuseValueWith = "";
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();

                r.Add(("guild suspension: silver escrowed during a suspension completes once it is lifted",
                       escrowedVault == 400 && b.Vault == 400 && b.Only.Phase == ConnectedOpPhase.Credited
                       && b.Global.Deposits >= 1,
                       $"vault={b.Vault} phase={b.Only?.Phase} sent={b.Global.Deposits}"));
            }

            using (Bench b = new Bench("out4", 0))
            {
                b.Link();
                b.Global.Unreachable = true;
                ConnectedGuildService.WithdrawAsync(b.Owner, 200, Ct).GetAwaiter().GetResult();
                bool nothingCredited = b.Vault == 0;
                b.Global.Unreachable = false;
                ConnectedTreasuryService.ReconcileAsync(Ct).GetAwaiter().GetResult();
                r.Add(("guild outage: a withdrawal interrupted before its hold credits nothing, then completes",
                       nothingCredited && b.Vault == 200 && b.Only.Phase == ConnectedOpPhase.Settled
                       && b.Global.Reserves >= 1,
                       $"vault={b.Vault} phase={b.Only?.Phase} reserves={b.Global.Reserves}"));
            }
        }
    }
}
