using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.Features.Guilds.Dto;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Federation
{
    internal static class KmhFederationSelfTest
    {
        private static readonly CancellationToken Ct = CancellationToken.None;
        private static readonly string Salt = Guid.NewGuid().ToString("N").Substring(0, 6);
        private const string ServerHex = "0123456789abcdef0123456789abcdef";
        private const string OtherServer = "fedcba9876543210fedcba9876543210";
        private const string LinkA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        private const string LinkB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb2";
        private const string FedId = "cccccccccccccccccccccccccccccccc";

        private sealed class FakeGlobal
        {
            public readonly List<(string Path, object Body)> Calls = new List<(string, object)>();
            public bool Unreachable;
            public readonly Dictionary<string, string> RefuseOn = new Dictionary<string, string>(StringComparer.Ordinal);
            // Contributions refused for one link only, the way Global refuses a link awaiting re-proof.
            public readonly Dictionary<string, string> RefuseLink = new Dictionary<string, string>(StringComparer.Ordinal);
            public int ContributionsFor(string linkId)
                => Calls.FindAll(c => c.Body is FederationContributeBody b && b.LinkId == linkId).Count;
            public string Name = "Iron Pact", Tag = "IRON", State = "Active", Leader = LinkA;
            public int Level = 1;
            public string SelfState = "Active";
            // Simulates changes made on other member servers.
            public string OtherState = "Active", Description = "", Color = "";
            public readonly List<JObject> MoreMembers = new List<JObject>();
            public readonly List<string> Achievements = new List<string>();
            public readonly HashSet<string> SeenReports = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<JObject> Objectives = new List<JObject>();

            public int Count(string suffix) => Calls.FindAll(c => c.Path.EndsWith(suffix, StringComparison.Ordinal)).Count;
            public T Last<T>(string suffix) where T : class
            {
                for (int i = Calls.Count - 1; i >= 0; i--)
                    if (Calls[i].Path.EndsWith(suffix, StringComparison.Ordinal)) return Calls[i].Body as T;
                return null;
            }

            public Task<GlobalCallResult> Send(string path, object body, CancellationToken ct)
            {
                Calls.Add((path, body));
                if (Unreachable) return Task.FromResult(GlobalCallResult.Offline("offline"));
                foreach (KeyValuePair<string, string> r in RefuseOn)
                    if (path.EndsWith(r.Key, StringComparison.Ordinal))
                        return Task.FromResult(new GlobalCallResult { Ok = false, Status = 409, ErrorCode = r.Value });
                if (body is FederationContributeBody cb && RefuseLink.TryGetValue(cb.LinkId ?? "", out string refused))
                    return Task.FromResult(new GlobalCallResult { Ok = false, Status = 409, ErrorCode = refused });

                if (path.EndsWith("/federations/create", StringComparison.Ordinal))
                {
                    var b = (FederationCreateBody)body;
                    Name = b.Name; Tag = b.Tag; Leader = LinkA;
                    return Ok(View(LinkA));
                }
                if (path.EndsWith("/federations/join", StringComparison.Ordinal)) { Leader = LinkA; return Ok(View(LinkB)); }
                if (path.EndsWith("/guilds/invites", StringComparison.Ordinal))
                    return Ok(new JObject { ["invite"] = "ABCD-EFGH-JKMN", ["invite_id"] = "dddddddddddddddddddddddddddddddd",
                                            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds() });
                if (path.EndsWith("/federations/leave", StringComparison.Ordinal))
                {
                    var b = (FederationLeaveBody)body;
                    SelfState = "Left";
                    return Ok(View(b.LinkId));
                }
                if (path.EndsWith("/federations/disband", StringComparison.Ordinal))
                {
                    State = "Disbanded"; SelfState = "Disbanded";
                    return Ok(View(((FederationDisbandBody)body).LinkId));
                }
                if (path.EndsWith("/federations/transfer", StringComparison.Ordinal))
                {
                    Leader = ((FederationTransferBody)body).TargetLinkId;
                    return Ok(View(((FederationTransferBody)body).LinkId));
                }
                if (path.EndsWith("/federations/contribute", StringComparison.Ordinal))
                {
                    var b = (FederationContributeBody)body;
                    int accepted = 0, dup = 0, rejected = 0;
                    long oldest = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400;
                    foreach (FederationContributionEntry e in b.Entries)
                    {
                        // As Global: a report older than its 7-day window is answered as rejected, never counted.
                        if (e.OccurredAtUnix > 0 && e.OccurredAtUnix < oldest) { rejected++; continue; }
                        if (SeenReports.Add(e.ReportId)) accepted++; else dup++;
                    }
                    return Ok(new JObject { ["accepted"] = accepted, ["duplicates"] = dup, ["rejected"] = rejected, ["revision"] = 7 });
                }
                if (path.EndsWith("/federations/view", StringComparison.Ordinal))
                    return Ok(View(((FederationViewBody)body).LinkId));
                if (path.EndsWith("/guilds/reprove", StringComparison.Ordinal))
                    return Ok(new JObject { ["link_id"] = LinkA, ["guild_id"] = FedId, ["state"] = "Active" });
                JObject any = body is null ? new JObject() : JObject.FromObject(body);
                return Ok(View((string)any["link_id"] ?? LinkA));
            }

            public JObject View(string self)
            {
                var objectives = new JArray();
                foreach (JObject o in Objectives) objectives.Add(o);
                var members = new JArray
                {
                    new JObject { ["link_id"] = LinkA, ["server_id"] = ServerHex, ["server_name"] = "Here", ["local_guild"] = "A",
                                  ["state"] = "Active", ["is_leader"] = Leader == LinkA },
                    new JObject { ["link_id"] = LinkB, ["server_id"] = OtherServer, ["server_name"] = "There", ["local_guild"] = "B",
                                  ["state"] = OtherState, ["is_leader"] = Leader == LinkB },
                };
                foreach (JObject m in MoreMembers) members.Add(m);
                var achievements = new JArray();
                foreach (string k in Achievements) achievements.Add(new JObject { ["key"] = k, ["achieved_at"] = 1700000000 });
                return new JObject
                {
                    ["federation"] = new JObject
                    {
                        ["federation_id"] = FedId, ["name"] = Name, ["tag"] = Tag, ["description"] = Description, ["color"] = Color,
                        ["state"] = State, ["created_at"] = 1700000000, ["leader_link_id"] = Leader, ["level"] = Level,
                        ["renown"] = 500, ["next_level_at"] = 900, ["revision"] = 3, ["active_members"] = 2,
                    },
                    ["self"] = new JObject { ["link_id"] = self, ["state"] = SelfState, ["is_leader"] = self == Leader, ["joined_at"] = 1700000000 },
                    ["members"] = members,
                    ["achievements"] = achievements,
                    ["objectives"] = objectives,
                    ["invites"] = new JArray
                    {
                        new JObject { ["invite_id"] = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", ["target_server"] = OtherServer, ["mine"] = true },
                        new JObject { ["invite_id"] = "ffffffffffffffffffffffffffffffff", ["target_server"] = OtherServer, ["mine"] = false },
                    },
                };
            }

            private static Task<GlobalCallResult> Ok(JObject b) => Task.FromResult(new GlobalCallResult { Ok = true, Status = 200, Body = b });
        }

        private sealed class Bench : IDisposable
        {
            public readonly FakeGlobal Global = new FakeGlobal();
            public readonly string Guild, Owner;
            private readonly List<string> _members = new List<string>();
            private readonly string _hadIdentity;
            private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            private readonly bool _hadFed, _hadGuilds, _hadTreasury;

            public Bench(string tag)
            {
                foreach (string f in new[] { KmhDataPaths.FederatedGuildsFile, KmhDataPaths.FederationOutboxFile,
                                             KmhDataPaths.KnownIdentitiesFile })
                    _files[f] = File.Exists(f) ? File.ReadAllBytes(f) : null;

                _hadIdentity = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                FeaturesConfig f0 = FeaturesConfig.Current;
                _hadFed = f0.GuildFederation; _hadGuilds = f0.Guilds; _hadTreasury = f0.ConnectedTreasury;
                f0.GuildFederation = true; f0.Guilds = true;

                FederatedGuildStore.ClearForTest();
                FederationOutbox.ClearForTest();
                FederationBenefits.ResetForTest();
                FederationService.ResetForTest();
                FederationNotices.ForgetForTest();
                FederationService.UnavailableForTest = "";
                KmhGlobalPost.SenderForTest = Global.Send;

                Guild = "kmh-st-fed-" + tag + "-" + Salt;
                Owner = Who("owner");
                if (!GuildStore.CreateGuildAndJoinAsAdmin(Owner, Guild, out string why))
                    throw new InvalidOperationException("could not seed a guild: " + why);
                _members.Add(Owner);
            }

            public string Who(string role) => "kmh-st-" + role + "-" + Salt + "-" + Guild.GetHashCode().ToString("x");

            public string Member(string role, string rank)
            {
                string u = Who(role);
                GuildStore.AddMember(u, Guild, rank);
                _members.Add(u);
                return u;
            }

            public FederatedGuildLink Founded()
            {
                FederationResult r = FederationService.CreateAsync(Owner, "officer-global-id", "Iron Pact", "iron", "", "", Ct)
                                                      .GetAwaiter().GetResult();
                if (!r.Ok) throw new InvalidOperationException("could not found a federation: " + r.Code);
                return FederatedGuildStore.ById(LinkA);
            }

            public void Dispose()
            {
                KmhGlobalPost.SenderForTest = null;
                FederationService.UnavailableForTest = null;
                FederationService.ResetForTest();
                FederationRewards.ExpansionRequiredForTest = null;
                FederationRewards.DefKnownForTest = null;
                KmhGlobalBackend.AllowedForTest = null;
                FeaturesConfig f0 = FeaturesConfig.Current;
                f0.GuildFederation = _hadFed; f0.Guilds = _hadGuilds; f0.ConnectedTreasury = _hadTreasury;
                KmhDataMeta.SetInstanceIdForTest(_hadIdentity);
                FederationNotices.ForgetForTest();
                FederationHandler.GuildPushForTest = null;
                foreach (string m in _members) Notifications.NotificationStore.ClearUser(m);
                for (int i = _members.Count - 1; i >= 0; i--) GuildStore.Leave(_members[i], out _);
                FederatedGuildStore.ClearForTest();
                FederationOutbox.ClearForTest();
                FederationBenefits.ResetForTest();
                foreach (KeyValuePair<string, byte[]> kv in _files)
                {
                    if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                    else if (File.Exists(kv.Key)) File.Delete(kv.Key);
                }
                // The stores re-read what was put back, so nothing a case wrote outlives it in memory either.
                FederatedGuildStore.LoadFromDisk();
                FederationOutbox.LoadFromDisk();
            }
        }

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            Gating(r);
            Migration(r);
            Backend(r);
            Lifecycle(r);
            Authority(r);
            Contributions(r);
            Departures(r);
            Benefits(r);
            Rewards(r);
            Snapshot(r);
            Sync(r);
            Notices(r);
            Verification(r);
            return r;
        }

        // Changes adopted from a heartbeat must reach the guild, not land silently.
        private static void Notices(List<(string, bool, string)> r)
        {
            List<string> Told(string user, string title)
                => Notifications.NotificationStore.PeekForUser(user).FindAll(n => n.Title == title).ConvertAll(n => n.Body);
            long rev = 100;
            void Beat()
            {
                FederationService.OnHeartbeatRevision(++rev);
                FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
            }

            Check(r, "federation notices: a removal Global reports is told once to every member, and online members get a fresh snapshot", () =>
            {
                using (Bench b = new Bench("ntremove"))
                {
                    b.Founded();
                    string m = b.Member("plain", GuildMemberDto.RankMember);
                    var pushed = new List<string>();
                    FederationHandler.GuildPushForTest = g => pushed.Add(g);
                    Beat();
                    int pushedQuiet = pushed.Count;
                    b.Global.SelfState = "Removed";
                    Beat();
                    Beat();
                    int toMember = Told(m, "Removed from the federation").Count, toOwner = Told(b.Owner, "Removed from the federation").Count;
                    bool ok = toMember == 1 && toOwner == 1 && pushed.Count > pushedQuiet && pushed.TrueForAll(g => g == b.Guild)
                              && FederatedGuildStore.ById(LinkA).State == FederatedLinkState.Removed;
                    return (ok, $"member told {toMember}x, owner {toOwner}x over two beats; pushes {pushedQuiet} before, {pushed.Count} after");
                }
            });

            Check(r, "federation notices: a change found on the first heartbeat after a restart is still told", () =>
            {
                using (Bench b = new Bench("ntboot"))
                {
                    b.Founded();
                    // A restart: nothing is remembered of what was told, only the stored link.
                    FederationNotices.ForgetForTest();
                    b.Global.SelfState = "Removed";
                    Beat();
                    int told = Told(b.Owner, "Removed from the federation").Count;
                    return (told == 1, $"told {told}x on the first beat");
                }
            });

            Check(r, "federation notices: a disband, a revocation and a leadership hand-over are each told once", () =>
            {
                var outcome = new List<string>();
                using (Bench b = new Bench("ntlead"))
                {
                    b.Global.Leader = LinkB;
                    b.Founded();
                    b.Global.Leader = LinkB;
                    Beat();
                    b.Global.Leader = LinkA;
                    Beat();
                    Beat();
                    outcome.Add($"lead={Told(b.Owner, "Your guild leads the federation").Count}");
                }
                using (Bench b = new Bench("ntdisband"))
                {
                    b.Founded();
                    Beat();
                    b.Global.State = "Disbanded";
                    Beat();
                    Beat();
                    outcome.Add($"disband={Told(b.Owner, "Federation disbanded").Count}");
                }
                using (Bench b = new Bench("ntrevoke"))
                {
                    b.Founded();
                    Beat();
                    b.Global.RefuseOn["/federations/view"] = "KMH-GLD-958";
                    Beat();
                    Beat();
                    outcome.Add($"revoke={Told(b.Owner, "Federation link revoked").Count}");
                }
                string all = string.Join(" ", outcome);
                return (all == "lead=1 disband=1 revoke=1", all);
            });

            Check(r, "federation notices: a re-proof demand reaches only the guild's leaders, who can act on it", () =>
            {
                using (Bench b = new Bench("ntreprove"))
                {
                    b.Founded();
                    string m = b.Member("plain", GuildMemberDto.RankMember);
                    Beat();
                    b.Global.RefuseOn["/federations/view"] = "KMH-GLD-959";
                    Beat();
                    int owner = Told(b.Owner, "Federation link needs proving again").Count, member = Told(m, "Federation link needs proving again").Count;
                    return (owner == 1 && member == 0, $"owner={owner} plain member={member}");
                }
            });

            Check(r, "federation notices: a completed objective is told with the reward it put in the guild vault", () =>
            {
                using (Bench b = new Bench("ntobjective"))
                {
                    b.Founded();
                    string m = b.Member("plain", GuildMemberDto.RankMember);
                    Beat();
                    string obj = "objective-" + Salt;
                    b.Global.Objectives.Add(new JObject
                    {
                        ["objective_id"] = obj, ["category"] = "site", ["target"] = 10, ["progress"] = 10,
                        ["state"] = "Completed", ["reward_eligible"] = true,
                    });
                    Beat();
                    Beat();
                    List<string> told = Told(m, "Federation objective completed");
                    string reward = FederationRewards.Describe(obj);
                    FederationRewards.Bundle bundle = FederationRewards.Choose(obj);
                    bool inVault = Features.Treasury.TreasuryStore.GuildItemCount(b.Guild, bundle.DefName) > 0;
                    bool ok = told.Count == 1 && told[0].Contains(reward) && inVault;
                    return (ok, $"told {told.Count}x: {(told.Count > 0 ? told[0] : "")}; vault holds {bundle.DefName}={inVault}");
                }
            });

            Check(r, "federation notices: a change the officer made here is not announced again by the next heartbeat", () =>
            {
                using (Bench b = new Bench("ntlocal"))
                {
                    b.Founded();
                    Beat();
                    var c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = b.Owner });
                    KmhHandshakeHandler.MarkCompatible(c);
                    KmhVerifiedPlayers.Record(c, "officer-global-id", b.Owner);
                    var enforcement = Features.Enforcement.EnforcementConfig.Current;
                    bool hadEnforcement = enforcement.Enabled;
                    var hadOut = KmhRouter.OutboundForTest;
                    try
                    {
                        enforcement.Enabled = false;
                        KmhRouter.OutboundForTest = (cl, kind, data) => { };
                        if (!KmhRouter.IsRegistered(KmhProtocol.Kind.FederationLeave)) FederationHandler.Register();
                        KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(new KmhEnvelope(KmhProtocol.Kind.FederationLeave, new { })
                                                                        { OpId = "fed-leave-" + Salt }.Serialize()));
                    }
                    finally { enforcement.Enabled = hadEnforcement; KmhRouter.OutboundForTest = hadOut; KmhVerifiedPlayers.Forget(c); }
                    string state = FederatedGuildStore.ById(LinkA)?.State;
                    Beat();
                    int told = Told(b.Owner, "Left the federation").Count;
                    return (state == FederatedLinkState.Left && told == 0, $"state={state} announced again={told}");
                }
            });

            Check(r, "federation notices: another server joining, being removed or leaving, a level, an achievement and a profile change are each told once", () =>
            {
                using (Bench b = new Bench("ntshared"))
                {
                    b.Founded();
                    string m = b.Member("plain", GuildMemberDto.RankMember);
                    Beat();
                    const string LinkC = "cccccccccccccccccccccccccccccc03";
                    b.Global.MoreMembers.Add(new JObject { ["link_id"] = LinkC, ["server_id"] = "abcdef0123456789abcdef0123456789",
                                                           ["server_name"] = "<color=#ff0000>Far</color>", ["local_guild"] = "C", ["state"] = "Active" });
                    Beat();
                    b.Global.OtherState = "Removed";
                    Beat();
                    b.Global.MoreMembers[0]["state"] = "Left";
                    Beat();
                    b.Global.Level = 3;
                    b.Global.Achievements.Add("level_3");
                    b.Global.Achievements.Add("first_ally");
                    Beat();
                    b.Global.Name = "Iron Pact Two";
                    b.Global.Description = "We hold the line.";
                    Beat();
                    Beat();
                    Beat();
                    List<string> joined = Told(m, "A server joined the federation");
                    int removed = Told(m, "A server was removed from the federation").Count, left = Told(m, "A server left the federation").Count;
                    int level = Told(m, "Federation level reached").Count;
                    List<string> achieved = Told(m, "Federation achievement"), profile = Told(m, "Federation profile changed");
                    bool inert = joined.Count == 1 && joined[0].Contains("Far") && joined[0].IndexOf('<') < 0;
                    bool ok = joined.Count == 1 && inert && removed == 1 && left == 1 && level == 1
                              && achieved.Count == 1 && achieved[0].Contains("First ally")
                              && profile.Count == 1 && profile[0].Contains("name") && profile[0].Contains("description")
                              && Told(b.Owner, "A server joined the federation").Count == 1;
                    return (ok, $"joined={joined.Count} ({(joined.Count > 0 ? joined[0] : "")}) removed={removed} left={left} level={level} "
                              + $"achievements={achieved.Count} profile={profile.Count} ({(profile.Count > 0 ? profile[0] : "")})");
                }
            });

            Check(r, "federation notices: a guild that loses leadership to another server is told once", () =>
            {
                using (Bench b = new Bench("ntlost"))
                {
                    b.Founded();
                    string m = b.Member("plain", GuildMemberDto.RankMember);
                    Beat();
                    b.Global.Leader = LinkB;
                    Beat();
                    Beat();
                    List<string> lost = Told(m, "Your guild no longer leads the federation");
                    bool ok = lost.Count == 1 && lost[0].Contains("There");
                    return (ok, $"told {lost.Count}x: {(lost.Count > 0 ? lost[0] : "")}");
                }
            });
        }

        // A signed-in player on a local address must not be told to sign in.
        private static void Verification(List<(string, bool, string)> r)
        {
            Check(r, "federation verify: a session's reported reason picks the words and the code, and a ticket refusal is its own", () =>
            {
                var c = new ServerClient(null, null, createListener: false);
                GlobalSetupState had = GlobalSetupState.ApplyForTest(new GlobalSetupState { BackendRegistered = true });
                try
                {
                    var codes = new List<string>();
                    codes.Add(FederationHandler.VerifyCode(c));
                    foreach (string why in new[] { KmhVerifiedPlayers.SignedOut, KmhVerifiedPlayers.LocalAddress,
                                                   KmhVerifiedPlayers.OtherAddress, KmhVerifiedPlayers.TicketRefused, "nonsense" })
                    {
                        KmhVerifiedPlayers.NoteUnverified(c, why);
                        codes.Add(FederationHandler.VerifyCode(c));
                    }
                    GlobalSetupState.ApplyForTest(new GlobalSetupState());
                    codes.Add(FederationHandler.VerifyCode(c));
                    const string expect = "account.unknown,account.signed_out,account.local_address,account.other_address,"
                                        + "account.ticket_refused,account.unconfirmed,global.identity";
                    string got = string.Join(",", codes);
                    bool signedOutAsksSignIn = FederationHandler.VerifyText("account.signed_out").Contains("Sign in");
                    bool signedInDoNot = !FederationHandler.VerifyText("account.local_address").Contains("Sign in")
                                      && !FederationHandler.VerifyText("account.unconfirmed").Contains("Sign in");
                    return (got == expect && signedOutAsksSignIn && signedInDoNot, $"codes={got}");
                }
                finally { GlobalSetupState.ApplyForTest(had); KmhVerifiedPlayers.Forget(c); }
            });

            Check(r, "federation verify: the snapshot carries a code beside each refusal's English, so it can be shown translated", () =>
            {
                using (Bench b = new Bench("whycodes"))
                {
                    b.Founded();
                    string m = b.Member("plain", GuildMemberDto.RankMember);
                    GlobalSetupState had = GlobalSetupState.ApplyForTest(new GlobalSetupState { BackendRegistered = true });
                    var owner = new ServerClient(null, null, createListener: false);
                    var plain = new ServerClient(null, null, createListener: false);
                    try
                    {
                        KmhVerifiedPlayers.NoteUnverified(owner, KmhVerifiedPlayers.LocalAddress);
                        JObject unverified = JObject.FromObject(FederationHandler.BuildPayload(owner, b.Owner));
                        KmhVerifiedPlayers.Record(plain, "plain-global-id", m);
                        JObject member = JObject.FromObject(FederationHandler.BuildPayload(plain, m));
                        KmhVerifiedPlayers.Record(owner, "officer-global-id", b.Owner);
                        b.Global.Leader = LinkB;
                        FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                        JObject follower = JObject.FromObject(FederationHandler.BuildPayload(owner, b.Owner));
                        string got = $"{unverified["verified_why_code"]}/{unverified["manage_why_code"]} {member["manage_why_code"]} {follower["lead_why_code"]}";
                        bool english = ((string)unverified["verified_why"]).Contains("local or private address")
                                       && ((string)member["manage_why"]).Contains("Admin");
                        return (got == "account.local_address/account.local_address rank.manage federation.not_leader" && english, got);
                    }
                    finally
                    {
                        GlobalSetupState.ApplyForTest(had);
                        KmhVerifiedPlayers.Forget(owner);
                        KmhVerifiedPlayers.Forget(plain);
                    }
                }
            });

            Check(r, "federation verify: the client's no-ticket notice is recorded for the session and answered with nothing", () =>
            {
                var c = new ServerClient(null, null, createListener: false);
                c.GetData<UserFile>(new UserFile { Username = "kmh-st-verify-" + Salt });
                var replies = new List<string>();
                var hadOut = KmhRouter.OutboundForTest;
                var enforcement = Features.Enforcement.EnforcementConfig.Current;
                bool hadEnforcement = enforcement.Enabled;
                try
                {
                    enforcement.Enabled = false;
                    if (!KmhRouter.IsRegistered(KmhProtocol.Kind.JoinTicket)) KmhJoinTicketHandler.Register();
                    KmhHandshakeHandler.MarkCompatible(c);
                    KmhRouter.OutboundForTest = (cl, kind, data) => { if (cl == c && kind == KmhProtocol.Kind.JoinVerified) replies.Add(kind); };
                    KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(new KmhEnvelope(KmhProtocol.Kind.JoinTicket,
                        new { ticket = (object)null, unverified = "local_address" }).Serialize()));
                    string noted = KmhVerifiedPlayers.UnverifiedWhy(c);
                    KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(new KmhEnvelope(KmhProtocol.Kind.JoinTicket,
                        new { ticket = new JObject { ["player_global_id"] = "x" }, unverified = "" }).Serialize()));
                    string afterBadTicket = KmhVerifiedPlayers.UnverifiedWhy(c);
                    bool ok = noted == KmhVerifiedPlayers.LocalAddress && afterBadTicket == KmhVerifiedPlayers.TicketRefused
                              && replies.Count == 1 && KmhVerifiedPlayers.IdFor(c) == "";
                    return (ok, $"noted={noted} afterBadTicket={afterBadTicket} replies={replies.Count}");
                }
                finally { enforcement.Enabled = hadEnforcement; KmhRouter.OutboundForTest = hadOut; KmhVerifiedPlayers.Forget(c); }
            });
        }

        private static void Sync(List<(string, bool, string)> r)
        {
            Check(r, "federation sync: a heartbeat re-reads exactly the federations whose revision moved", () =>
            {
                using (Bench b = new Bench("sync"))
                {
                    b.Founded();
                    int Views() => b.Global.Count("/federations/view");
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int start = Views();

                    // A second federation's far higher revision would hide this one's change behind a single highest number.
                    FederationService.OnHeartbeatRevisions(new Dictionary<string, long> { [FedId] = 3, ["dddddddddddddddddddddddddddddddd"] = 900 });
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int unchanged = Views() - start;

                    FederationService.OnHeartbeatRevisions(new Dictionary<string, long> { [FedId] = 4, ["dddddddddddddddddddddddddddddddd"] = 900 });
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int moved = Views() - start - unchanged;

                    // Global no longer counting this link at all means the copy here is out of date.
                    FederationService.OnHeartbeatRevisions(new Dictionary<string, long> { ["dddddddddddddddddddddddddddddddd"] = 900 });
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int dropped = Views() - start - unchanged - moved;

                    bool ok = unchanged == 0 && moved == 1 && dropped == 1;
                    return (ok, $"unchanged={unchanged} moved={moved} no-longer-listed={dropped}");
                }
            });

            Check(r, "federation sync: an older Global's single revision still triggers a re-read when it moves", () =>
            {
                using (Bench b = new Bench("syncold"))
                {
                    b.Founded();
                    int Views() => b.Global.Count("/federations/view");
                    FederationService.OnHeartbeatRevision(5);
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int start = Views();
                    FederationService.OnHeartbeatRevision(5);
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int same = Views() - start;
                    FederationService.OnHeartbeatRevision(6);
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int moved = Views() - start - same;
                    return (same == 0 && moved == 1, $"same={same} moved={moved}");
                }
            });

            Check(r, "federation sync: the heartbeat reads the per-federation revisions Global sends", () =>
            {
                var map = KmhGlobalService.RevisionsOf(new JObject { [FedId] = 7, ["x"] = "not a number", ["dddddddddddddddddddddddddddddddd"] = 2 });
                bool ok = map.Count == 2 && map[FedId] == 7 && map["dddddddddddddddddddddddddddddddd"] == 2;
                return (ok, string.Join(",", map));
            });

            Check(r, "federation sync: a heartbeat answer goes by the map when it has one, by the single number when not", () =>
            {
                using (Bench b = new Bench("syncwire"))
                {
                    b.Founded();
                    const string Other = "dddddddddddddddddddddddddddddddd";
                    int Views() => b.Global.Count("/federations/view");
                    FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                    int After(JObject body)
                    {
                        int before = Views();
                        KmhGlobalService.NoteFederationRevisions(body);
                        FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                        return Views() - before;
                    }
                    int same = After(new JObject { ["federation_revisions"] = new JObject { [FedId] = 3, [Other] = 900 }, ["federation_revision"] = 900 });
                    int moved = After(new JObject { ["federation_revisions"] = new JObject { [FedId] = 4, [Other] = 900 }, ["federation_revision"] = 900 });
                    int older = After(new JObject { ["federation_revision"] = 901 });
                    return (same == 0 && moved == 1 && older == 1, $"unchanged={same} moved under a higher one={moved} older Global={older}");
                }
            });
        }

        private static void Check(List<(string, bool, string)> r, string name, Func<(bool, string)> body)
        {
            try { (bool ok, string detail) = body(); r.Add((name, ok, detail)); }
            catch (Exception ex) { r.Add((name, false, "threw " + ex.GetType().Name + ": " + ex.Message)); }
        }


        private static void Gating(List<(string, bool, string)> r)
        {
            Check(r, "federation: on by default, and the Connected Treasury off by default", () =>
            {
                var d = new FeaturesConfig();
                List<string> off = d.DisabledList();
                bool ok = d.GuildFederation && d.Guilds && !d.ConnectedTreasury && off.Contains("connected") && !off.Contains("federation");
                return (ok, $"federation={d.GuildFederation} treasury={d.ConnectedTreasury} disabled=[{string.Join(",", off)}]");
            });

            Check(r, "federation: each kind needs every switch below it", () =>
            {
                string[] fed = FeaturesConfig.FeaturesForKind(KmhProtocol.Kind.FederationCreate);
                string[] gct = FeaturesConfig.FeaturesForKind(KmhProtocol.Kind.GuildConnectedDeposit);
                string[] per = FeaturesConfig.FeaturesForKind(KmhProtocol.Kind.ConnectedWithdraw);
                bool ok = string.Join(",", fed) == "guilds,federation"
                       && string.Join(",", gct) == "guilds,federation,connected"
                       && string.Join(",", per) == "connected";
                return (ok, $"federation=[{string.Join(",", fed)}] guild treasury=[{string.Join(",", gct)}] personal=[{string.Join(",", per)}]");
            });

            // Through the real router, the way a modified client that ignores its own UI would arrive.
            Check(r, "federation: the router refuses federation and treasury kinds whose switches are off", () =>
            {
                var c = new ServerClient(null, null, createListener: false);
                string user = "kmh-st-router-" + Salt;
                c.GetData<UserFile>(new UserFile { Username = user });
                KmhHandshakeHandler.MarkCompatible(c);
                // The offline runner boots without the handler table; the gate is what is under test, not the boot.
                if (!KmhRouter.IsRegistered(KmhProtocol.Kind.FederationRequest)) FederationHandler.Register();
                if (!KmhRouter.IsRegistered(KmhProtocol.Kind.ConnectedRequest)) Treasury.ConnectedTreasuryHandler.Register();
                if (!KmhRouter.IsRegistered(KmhProtocol.Kind.GuildConnectedRequest)) Guilds.ConnectedGuildHandler.Register();
                var seen = new List<string>();
                var hadOut = KmhRouter.OutboundForTest;
                KmhRouter.OutboundForTest = (cl, kind, data) => { if (cl == c) seen.Add(kind); };
                FeaturesConfig f = FeaturesConfig.Current;
                bool hadGuilds = f.Guilds, hadFed = f.GuildFederation, hadTreasury = f.ConnectedTreasury;
                // An enforcement hold lets only control traffic through, which would answer for the feature gate.
                Features.Enforcement.EnforcementConfig enforcement = Features.Enforcement.EnforcementConfig.Current;
                bool hadEnforcement = enforcement.Enabled;
                enforcement.Enabled = false;
                try
                {
                    int Sent(string kind)
                    {
                        seen.Clear();
                        KmhRouter.HandleInbound(c, KmhEnvelope.TryParse(new KmhEnvelope(kind, new { refresh = false, amount = 10 }).Serialize()));
                        return seen.Count;
                    }
                    f.Guilds = true; f.GuildFederation = true; f.ConnectedTreasury = false;
                    int open = Sent(KmhProtocol.Kind.FederationRequest);
                    int treasuryOff = Sent(KmhProtocol.Kind.ConnectedDeposit) + Sent(KmhProtocol.Kind.GuildConnectedWithdraw);
                    f.Guilds = false;
                    int guildsOff = Sent(KmhProtocol.Kind.FederationRequest);
                    f.Guilds = true; f.GuildFederation = false;
                    int fedOff = Sent(KmhProtocol.Kind.FederationRequest);
                    f.GuildFederation = true; f.ConnectedTreasury = true;
                    int treasuryOn = Sent(KmhProtocol.Kind.ConnectedDeposit);
                    bool ok = open > 0 && treasuryOff == 0 && guildsOff == 0 && fedOff == 0 && treasuryOn > 0;
                    return (ok, $"open={open} treasury off={treasuryOff} guilds off={guildsOff} federation off={fedOff} treasury on={treasuryOn}"
                              + $" (handler registered={KmhRouter.IsRegistered(KmhProtocol.Kind.FederationRequest)},"
                              + $" compatible={KmhHandshakeHandler.IsCompatible(c)}, held={Features.Enforcement.EnforcementAdmission.IsHeld(c)})");
                }
                finally
                {
                    f.Guilds = hadGuilds; f.GuildFederation = hadFed; f.ConnectedTreasury = hadTreasury;
                    enforcement.Enabled = hadEnforcement;
                    KmhRouter.OutboundForTest = hadOut;
                }
            });
        }


        private static void Migration(List<(string, bool, string)> r)
        {
            Check(r, "migration: an old Connected=true becomes Federation on and the Connected Treasury off", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "kmh-fedmig-" + Salt);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "Features.json");
                try
                {
                    File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Guilds\": true, \"Connected\": true }");
                    var report = new List<string>();
                    (int changed, string failure) = Maintenance.KmhConfigMigration.SplitConnectedFeatureIn(path, report);
                    JObject after = JObject.Parse(File.ReadAllText(path));
                    (int again, string _) = Maintenance.KmhConfigMigration.SplitConnectedFeatureIn(path, new List<string>());

                    File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Connected\": false, \"GuildFederation\": false }");
                    Maintenance.KmhConfigMigration.SplitConnectedFeatureIn(path, new List<string>());
                    JObject off = JObject.Parse(File.ReadAllText(path));

                    bool ok = failure == null && changed > 0 && after["Connected"] == null
                           && (bool)after["GuildFederation"] && !(bool)after["ConnectedTreasury"] && again == 0
                           && (bool)off["GuildFederation"] && !(bool)off["ConnectedTreasury"] && off["Connected"] == null
                           && report.Count == 1;
                    return (ok, $"changed={changed} again={again} federation={after["GuildFederation"]} treasury={after["ConnectedTreasury"]} "
                              + $"old-off federation={off["GuildFederation"]} report={string.Join("|", report)}");
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                    FeaturesConfig.Reload();
                }
            });

            Check(r, "v1.3.1: ConnectedTreasury=true written into Features.json by hand still loads as off", () =>
            {
                string path = KmhDataPaths.FeaturesConfigFile;
                string before = File.Exists(path) ? File.ReadAllText(path) : null;
                try
                {
                    File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Guilds\": true, \"GuildFederation\": true, \"ConnectedTreasury\": true }");
                    FeaturesConfig.Reload();
                    bool off = !FeaturesConfig.Current.ConnectedTreasury && !FeaturesConfig.Current.IsEnabled("connected")
                            && FeaturesConfig.Current.DisabledList().Contains("connected") && FeaturesConfig.Current.GuildFederation;
                    return (off, $"connected={FeaturesConfig.Current.ConnectedTreasury} federation={FeaturesConfig.Current.GuildFederation}");
                }
                finally
                {
                    if (before == null) File.Delete(path); else File.WriteAllText(path, before);
                    FeaturesConfig.Reload();
                }
            });

            Check(r, "migration: the config schema the split ships under is the one this build stamps", () =>
                (KmhVersion.ConfigSchema >= 5, $"config schema {KmhVersion.ConfigSchema}"));

            Check(r, "migration: an owner who switched Global off keeps the new private identity off too", () =>
            {
                string[] files = { KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile };
                var saved = new Dictionary<string, byte[]>();
                foreach (string p in files) saved[p] = File.Exists(p) ? File.ReadAllBytes(p) : null;
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = false, BackendIdentity = true });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState { Current = SetupStage.Registered });
                try
                {
                    (int silenced, string failure) = Maintenance.KmhConfigMigration.KeepGlobalOffWhereDisabled(new List<string>());
                    bool keptOff = silenced == 1 && failure == null && !GlobalConfig.Current.BackendIdentity;

                    GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = false, BackendIdentity = true });
                    GlobalSetupState.ApplyForTest(new GlobalSetupState());
                    (int fresh, string _) = Maintenance.KmhConfigMigration.KeepGlobalOffWhereDisabled(new List<string>());
                    bool freshOn = fresh == 0 && GlobalConfig.Current.BackendIdentity;

                    GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = true, BackendIdentity = true });
                    GlobalSetupState.ApplyForTest(new GlobalSetupState { Current = SetupStage.Registered });
                    (int listed, string _) = Maintenance.KmhConfigMigration.KeepGlobalOffWhereDisabled(new List<string>());
                    bool listedOn = listed == 0 && GlobalConfig.Current.BackendIdentity;

                    return (keptOff && freshOn && listedOn,
                            $"owner switched off: changed={silenced} kept off={keptOff} | never set up: changed={fresh} | listed: changed={listed}");
                }
                finally
                {
                    GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState);
                    foreach (var kv in saved)
                    {
                        if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                        else if (File.Exists(kv.Key)) File.Delete(kv.Key);
                    }
                }
            });
        }


        private static void Backend(List<(string, bool, string)> r)
        {
            Check(r, "backend identity: allowed only for an un-set-up server with Federation on and no opt-out", () =>
            {
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = false, BackendIdentity = true });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState());
                FeaturesConfig f = FeaturesConfig.Current;
                bool hadFed = f.GuildFederation;
                try
                {
                    f.GuildFederation = true;
                    bool fresh = KmhGlobalBackend.Allowed;
                    GlobalConfig.Current.BackendIdentity = false;
                    bool optedOut = KmhGlobalBackend.Allowed;
                    GlobalConfig.Current.BackendIdentity = true;
                    GlobalConfig.Current.Enabled = true;
                    bool ownerOn = KmhGlobalBackend.Allowed;
                    GlobalConfig.Current.Enabled = false;
                    f.GuildFederation = false;
                    bool fedOff = KmhGlobalBackend.Allowed;
                    f.GuildFederation = true;
                    GlobalSetupState.Current_.Current = SetupStage.Registered;
                    bool owned = KmhGlobalBackend.Allowed;
                    bool ok = fresh && !optedOut && !ownerOn && !fedOff && !owned;
                    return (ok, $"fresh={fresh} opted-out={optedOut} owner-enabled={ownerOn} federation-off={fedOff} owner-registered={owned}");
                }
                finally { f.GuildFederation = hadFed; GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState); }
            });

            Check(r, "backend identity: sends a heartbeat and federation calls, never a listing or a mod list", () =>
            {
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = false, BackendIdentity = true });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState { BackendRegistered = true });
                string hadId = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                try
                {
                    bool beat = KmhGlobalDisclosure.MayPublish("/api/v1/servers/heartbeat", out _);
                    bool fed = KmhGlobalDisclosure.MayPublish("/api/v1/federations/contribute", out _);
                    bool reg = KmhGlobalDisclosure.MayPublish(KmhGlobalBackend.RegisterPath, out _);
                    bool meta = KmhGlobalDisclosure.MayPublish($"/api/v1/servers/{ServerHex}/metadata", out string whyMeta);
                    bool mods = KmhGlobalDisclosure.MayPublish($"/api/v1/servers/{ServerHex}/mod-profile", out _);
                    bool treasury = KmhGlobalDisclosure.MayPublish("/api/v1/treasury/deposit", out _);
                    GlobalConfig.Current.BackendIdentity = false;
                    bool afterOptOut = KmhGlobalDisclosure.MayPublish("/api/v1/servers/heartbeat", out _)
                                    || KmhGlobalDisclosure.MayPublish(KmhGlobalBackend.RegisterPath, out _);
                    bool ok = beat && fed && reg && !meta && !mods && !treasury && !afterOptOut;
                    return (ok, $"heartbeat={beat} federation={fed} register={reg} metadata={meta} ({whyMeta}) mods={mods} treasury={treasury} after opt-out={afterOptOut}");
                }
                finally { KmhDataMeta.SetInstanceIdForTest(hadId); GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState); }
            });

            Check(r, "backend identity: registration signs its own domain and records an owner-less identity", () =>
            {
                string[] files = { KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile };
                var saved = new Dictionary<string, byte[]>();
                foreach (string p in files) saved[p] = File.Exists(p) ? File.ReadAllBytes(p) : null;
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = false, BackendIdentity = true, BaseUrl = "https://global.test.invalid" });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState());
                string hadId = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                var calls = new List<string>();
                BackendRegisterBody sent = null;
                KmhGlobalPost.SenderForTest = (path, body, ct) =>
                {
                    calls.Add(path);
                    if (body is BackendRegisterBody b) sent = b;
                    var res = path.EndsWith("/challenge") ? new JObject { ["challenge_id"] = "ch-1" } : new JObject { ["owned"] = false };
                    return Task.FromResult(new GlobalCallResult { Ok = true, Status = 200, Body = res });
                };
                try
                {
                    bool ok1 = KmhGlobalBackend.RegisterAsync(Ct).GetAwaiter().GetResult();
                    bool verifies = sent != null && KmhGlobalSigning.Verify(KmhServerKey.PublicKey, sent.Canonical(), sent.Signature);
                    bool ok = ok1 && calls.Count == 2 && calls[0] == KmhGlobalBackend.ChallengePath && calls[1] == KmhGlobalBackend.RegisterPath
                           && sent.ChallengeId == "ch-1" && sent.ServerId == ServerHex && verifies
                           && GlobalSetupState.Current_.BackendRegistered && !GlobalSetupState.Current_.IsOwnerRegistered
                           && GlobalSetupState.Current_.IsRegistered && GlobalConfig.Current.RegisteredServerId == ServerHex
                           && !GlobalConfig.Current.Enabled;
                    return (ok, $"ok={ok1} calls=[{string.Join(",", calls)}] verifies={verifies} backend={GlobalSetupState.Current_.BackendRegistered} enabled={GlobalConfig.Current.Enabled}");
                }
                finally
                {
                    KmhGlobalPost.SenderForTest = null;
                    KmhDataMeta.SetInstanceIdForTest(hadId);
                    GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState);
                    foreach (var kv in saved)
                    {
                        if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                        else if (File.Exists(kv.Key)) File.Delete(kv.Key);
                    }
                }
            });

            Check(r, "backend identity: an answer no retry can change stops the retries and says what to do", () =>
            {
                GlobalConfig hadCfg = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = false, BackendIdentity = true, BaseUrl = "https://global.test.invalid" });
                GlobalSetupState hadState = GlobalSetupState.ApplyForTest(new GlobalSetupState());
                string hadId = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                string answer = "KMH-SRV-322";
                KmhGlobalPost.SenderForTest = (path, body, ct) => Task.FromResult(
                    new GlobalCallResult { Ok = false, Status = answer == "KMH-SRV-323" ? 503 : 409, ErrorCode = answer });
                try
                {
                    bool owned = KmhGlobalBackend.RegisterAsync(Ct).GetAwaiter().GetResult();
                    bool ownedSettled = KmhGlobalBackend.Settled;
                    string ownedWhy = KmhGlobalBackend.LastError;
                    answer = "KMH-SRV-323";
                    bool paused = KmhGlobalBackend.RegisterAsync(Ct).GetAwaiter().GetResult();
                    bool pausedSettled = KmhGlobalBackend.Settled;
                    bool ok = !owned && ownedSettled && ownedWhy.Contains("kmh global setup") && !paused && !pausedSettled
                           && !GlobalSetupState.Current_.BackendRegistered;
                    return (ok, $"owned settled={ownedSettled} ({ownedWhy}) paused settled={pausedSettled} ({KmhGlobalBackend.LastError})");
                }
                finally
                {
                    KmhGlobalPost.SenderForTest = null;
                    KmhDataMeta.SetInstanceIdForTest(hadId);
                    GlobalConfig.ApplyForTest(hadCfg); GlobalSetupState.ApplyForTest(hadState);
                }
            });
        }


        private static void Lifecycle(List<(string, bool, string)> r)
        {
            Check(r, "federation: founding sends the normalized profile and keeps the link Global named", () =>
            {
                using (Bench b = new Bench("found"))
                {
                    FederationResult res = FederationService.CreateAsync(b.Owner, "officer-global-id", "  Iron   Pact ", "iron", "", "#c0392b", Ct)
                                                            .GetAwaiter().GetResult();
                    FederationCreateBody sent = b.Global.Last<FederationCreateBody>("/federations/create");
                    FederatedGuildLink link = FederatedGuildStore.ForGuild(b.Guild);
                    bool ok = res.Ok && sent != null && sent.Name == "Iron Pact" && sent.Tag == "IRON" && sent.Color == "#C0392B"
                           && sent.LocalGuild == b.Guild && sent.Officer == "officer-global-id"
                           && link != null && link.LinkId == LinkA && link.GuildId == FedId && link.State == FederatedLinkState.Active
                           && link.View?.Federation?.Tag == "IRON" && link.IsLeader;
                    return (ok, $"ok={res.Ok}/{res.Code} name='{sent?.Name}' tag={sent?.Tag} link={link?.LinkId}/{link?.State} leader={link?.IsLeader}");
                }
            });

            Check(r, "federation: a bad name or tag is refused here and never sent", () =>
            {
                using (Bench b = new Bench("badname"))
                {
                    FederationResult shortName = FederationService.CreateAsync(b.Owner, "officer-global-id", "ab", "IRON", "", "", Ct).GetAwaiter().GetResult();
                    FederationResult markup = FederationService.CreateAsync(b.Owner, "officer-global-id", "<b>Iron</b>", "IRON", "", "", Ct).GetAwaiter().GetResult();
                    FederationResult badTag = FederationService.CreateAsync(b.Owner, "officer-global-id", "Iron Pact", "I-1", "", "", Ct).GetAwaiter().GetResult();
                    FederationResult badColor = FederationService.CreateAsync(b.Owner, "officer-global-id", "Iron Pact", "IRON", "", "#123456", Ct).GetAwaiter().GetResult();
                    bool ok = !shortName.Ok && !markup.Ok && !badTag.Ok && !badColor.Ok && b.Global.Count("/federations/create") == 0;
                    return (ok, $"short={shortName.Code} markup={markup.Code} tag={badTag.Code} color={badColor.Code} sent={b.Global.Count("/federations/create")}");
                }
            });

            Check(r, "federation: a guild already in a federation cannot found or join another", () =>
            {
                using (Bench b = new Bench("twice"))
                {
                    b.Founded();
                    FederationResult again = FederationService.CreateAsync(b.Owner, "officer-global-id", "Second", "TWO", "", "", Ct).GetAwaiter().GetResult();
                    FederationResult join = FederationService.JoinAsync(b.Owner, "officer-global-id", "ABCD-EFGH-JKMN", Ct).GetAwaiter().GetResult();
                    bool ok = again.Code == "guild.linked" && join.Code == "guild.linked" && b.Global.Count("/federations/create") == 1
                           && b.Global.Count("/federations/join") == 0;
                    return (ok, $"create={again.Code} join={join.Code}");
                }
            });

            Check(r, "federation: joining signs the invite as typed-normalized and keeps the new link", () =>
            {
                using (Bench b = new Bench("join"))
                {
                    FederationResult res = FederationService.JoinAsync(b.Owner, "officer-global-id", " abcd-efgh-jkmn ", Ct).GetAwaiter().GetResult();
                    FederationJoinBody sent = b.Global.Last<FederationJoinBody>("/federations/join");
                    FederatedGuildLink link = FederatedGuildStore.ForGuild(b.Guild);
                    bool verifies = sent != null && KmhGlobalSigning.Verify(KmhServerKey.PublicKey, sent.Canonical(), sent.Signature);
                    bool ok = res.Ok && verifies && link?.LinkId == LinkB && !link.IsLeader;
                    FederationResult empty = FederationService.JoinAsync(b.Who("x"), "officer-global-id", "", Ct).GetAwaiter().GetResult();
                    return (ok && !empty.Ok, $"ok={res.Ok}/{res.Code} verifies={verifies} link={link?.LinkId} leader={link?.IsLeader} empty={empty.Code}");
                }
            });

            Check(r, "federation: an invite is for one other server, only from a current link", () =>
            {
                using (Bench b = new Bench("invite"))
                {
                    b.Founded();
                    FederationResult ok1 = FederationService.InviteAsync(b.Owner, "officer-global-id", OtherServer.ToUpperInvariant(), Ct).GetAwaiter().GetResult();
                    FederationResult self = FederationService.InviteAsync(b.Owner, "officer-global-id", ServerHex, Ct).GetAwaiter().GetResult();
                    FederationResult junk = FederationService.InviteAsync(b.Owner, "officer-global-id", "not-a-server", Ct).GetAwaiter().GetResult();
                    FederatedGuildStore.SetState(LinkA, FederatedLinkState.ReproofRequired, "restored");
                    FederationResult stale = FederationService.InviteAsync(b.Owner, "officer-global-id", OtherServer, Ct).GetAwaiter().GetResult();
                    GuildInviteBody sent = b.Global.Last<GuildInviteBody>("/guilds/invites");
                    bool ok = ok1.Ok && ok1.InviteCode == "ABCD-EFGH-JKMN" && sent?.TargetServerId == OtherServer && sent.LinkId == LinkA
                           && self.Code == "invite.target" && junk.Code == "invite.target" && stale.Code == "KMH-GLD-959"
                           && b.Global.Count("/guilds/invites") == 1;
                    return (ok, $"ok={ok1.Ok}/{ok1.InviteCode} self={self.Code} junk={junk.Code} stale={stale.Code} sent={b.Global.Count("/guilds/invites")}");
                }
            });

            Check(r, "federation: leaving is Global's decision, and a refusal leaves the link as it was", () =>
            {
                using (Bench b = new Bench("leave"))
                {
                    b.Founded();
                    b.Global.RefuseOn["/federations/leave"] = "KMH-FED-910";
                    FederationResult refused = FederationService.LeaveAsync(b.Owner, "officer-global-id", Ct).GetAwaiter().GetResult();
                    string afterRefusal = FederatedGuildStore.ById(LinkA)?.State;
                    b.Global.RefuseOn.Clear();
                    FederationService.Record("site", b.Guild, "", 3);
                    FederationResult left = FederationService.LeaveAsync(b.Owner, "officer-global-id", Ct).GetAwaiter().GetResult();
                    FederatedGuildLink after = FederatedGuildStore.ById(LinkA);
                    bool ok = refused.Code == "KMH-FED-910" && afterRefusal == FederatedLinkState.Active
                           && left.Ok && after.State == FederatedLinkState.Left && !after.MayRead && FederationOutbox.PendingFor(LinkA) == 0;
                    return (ok, $"refused={refused.Code} state then={afterRefusal} left={left.Ok} now={after?.State} queued={FederationOutbox.PendingFor(LinkA)}");
                }
            });

            Check(r, "federation: a removal or a disband done elsewhere ends the link here on the next read", () =>
            {
                using (Bench b = new Bench("adopt"))
                {
                    b.Founded();
                    b.Global.SelfState = "Removed";
                    FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                    string removed = FederatedGuildStore.ById(LinkA)?.State;
                    FederatedGuildStore.SetState(LinkA, FederatedLinkState.Active, "");
                    b.Global.SelfState = "Active"; b.Global.State = "Disbanded";
                    FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                    string disbanded = FederatedGuildStore.ById(LinkA)?.State;
                    bool records = !FederationService.Record("site", b.Guild, "", 1);
                    bool ok = removed == FederatedLinkState.Removed && disbanded == FederatedLinkState.Disbanded && records;
                    return (ok, $"removed->{removed} disbanded->{disbanded} record refused={records}");
                }
            });

            Check(r, "federation: an older Global without Federation is reported as such, not as an outage", () =>
            {
                using (Bench b = new Bench("old"))
                {
                    b.Founded();
                    FederationService.UnavailableForTest = null;
                    KmhGlobalPost.SenderForTest = (p, body, ct) => Task.FromResult(new GlobalCallResult { Ok = false, Status = 404 });
                    FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                    string reason = FederationService.UnavailableReason();
                    // The rest of availability is this runner's, so only the part the 404 decides is asserted.
                    bool ok = reason == "global.unsupported" || reason == "global.off" || reason == "global.setup";
                    FederationService.ResetForTest();
                    return (ok, $"reason={reason}");
                }
            });
        }


        private static void Authority(List<(string, bool, string)> r)
        {
            Check(r, "federation authority: an unverified player cannot found, join or manage", () =>
            {
                using (Bench b = new Bench("unverified"))
                {
                    FederationResult create = FederationService.CreateAsync(b.Owner, "", "Iron Pact", "IRON", "", "", Ct).GetAwaiter().GetResult();
                    FederationResult join = FederationService.JoinAsync(b.Owner, "", "ABCD-EFGH-JKMN", Ct).GetAwaiter().GetResult();
                    bool ok = create.Code == "account.unverified" && join.Code == "account.unverified" && b.Global.Calls.Count == 0;
                    return (ok, $"create={create.Code} join={join.Code} sent={b.Global.Calls.Count}");
                }
            });

            Check(r, "federation authority: ordinary ranks cannot manage, and Admins can", () =>
            {
                using (Bench b = new Bench("ranks"))
                {
                    b.Founded();
                    string member = b.Member("member", GuildMemberDto.RankMember);
                    string officer = b.Member("officer", GuildMemberDto.RankOfficer);
                    string mod = b.Member("mod", GuildMemberDto.RankModerator);
                    string admin = b.Member("admin", GuildMemberDto.RankAdmin);
                    FederationResult m = FederationService.InviteAsync(member, "g-member", OtherServer, Ct).GetAwaiter().GetResult();
                    FederationResult o = FederationService.LeaveAsync(officer, "g-officer", Ct).GetAwaiter().GetResult();
                    FederationResult md = FederationService.InviteAsync(mod, "g-mod", OtherServer, Ct).GetAwaiter().GetResult();
                    FederationResult a = FederationService.InviteAsync(admin, "g-admin", OtherServer, Ct).GetAwaiter().GetResult();
                    bool ok = m.Code == "refused" && o.Code == "refused" && md.Code == "refused" && a.Ok
                           && b.Global.Count("/guilds/invites") == 1 && b.Global.Count("/federations/leave") == 0;
                    return (ok, $"member={m.Code} officer={o.Code} moderator={md.Code} admin={a.Ok}/{a.Code}");
                }
            });

            Check(r, "federation authority: leading needs the Owner of the leading guild, checked before anything is sent", () =>
            {
                using (Bench b = new Bench("lead"))
                {
                    b.Founded();
                    string admin = b.Member("admin", GuildMemberDto.RankAdmin);
                    FederationResult byAdmin = FederationService.DisbandAsync(admin, "g-admin", "IRON", "", Ct).GetAwaiter().GetResult();
                    b.Global.Leader = LinkB;
                    FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                    FederationResult notLeader = FederationService.TransferAsync(b.Owner, "officer-global-id", LinkB, Ct).GetAwaiter().GetResult();
                    b.Global.Leader = LinkA;
                    FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                    FederationResult self = FederationService.RemoveAsync(b.Owner, "officer-global-id", LinkA, Ct).GetAwaiter().GetResult();
                    FederationResult moved = FederationService.TransferAsync(b.Owner, "officer-global-id", LinkB, Ct).GetAwaiter().GetResult();
                    bool ok = byAdmin.Code == "refused" && notLeader.Code == "KMH-FED-907" && self.Code == "KMH-FED-909"
                           && moved.Ok && b.Global.Count("/federations/disband") == 0 && b.Global.Count("/federations/transfer") == 1
                           && !FederatedGuildStore.ById(LinkA).IsLeader;
                    return (ok, $"admin={byAdmin.Code} not-leader={notLeader.Code} self={self.Code} transfer={moved.Ok} leader now={FederatedGuildStore.ById(LinkA).IsLeader}");
                }
            });

            Check(r, "federation authority: a rank taken away after the window opened is refused at the moment of acting", () =>
            {
                using (Bench b = new Bench("recheck"))
                {
                    b.Founded();
                    string admin = b.Member("admin", GuildMemberDto.RankAdmin);
                    bool before = GuildStore.CanAdministerFederation(admin, out _);
                    GuildStore.Demote(b.Owner, admin);
                    FederationResult after = FederationService.InviteAsync(admin, "g-admin", OtherServer, Ct).GetAwaiter().GetResult();
                    bool ok = before && after.Code == "refused" && b.Global.Count("/guilds/invites") == 0;
                    return (ok, $"before={before} after={after.Code} rank now={GuildStore.RankOf(admin)}");
                }
            });

            Check(r, "federation authority: disbanding takes the tag typed back, and ends the link here", () =>
            {
                using (Bench b = new Bench("disband"))
                {
                    b.Founded();
                    FederationResult wrong = FederationService.DisbandAsync(b.Owner, "officer-global-id", "IRN", "", Ct).GetAwaiter().GetResult();
                    int sentAfterWrong = b.Global.Count("/federations/disband");
                    FederationResult done = FederationService.DisbandAsync(b.Owner, "officer-global-id", " iron ", "done here", Ct).GetAwaiter().GetResult();
                    FederationDisbandBody sent = b.Global.Last<FederationDisbandBody>("/federations/disband");
                    FederatedGuildLink link = FederatedGuildStore.ById(LinkA);
                    FederationResult afterwards = FederationService.InviteAsync(b.Owner, "officer-global-id", OtherServer, Ct).GetAwaiter().GetResult();
                    bool ok = wrong.Code == "KMH-FED-912" && sentAfterWrong == 0 && done.Ok && sent?.Confirm == "IRON"
                           && link.State == FederatedLinkState.Disbanded && afterwards.Code == "guild.unlinked";
                    return (ok, $"wrong={wrong.Code} sent first={sentAfterWrong} done={done.Ok} confirm={sent?.Confirm} state={link?.State} after={afterwards.Code}");
                }
            });

            Check(r, "federation authority: an action this server cannot sign is refused here, not sent unsigned", () =>
            {
                using (Bench b = new Bench("unsigned"))
                {
                    b.Founded();
                    int before = b.Global.Calls.Count;
                    FederationResult res = FederationService.MutateAsync(FederatedGuildStore.ById(LinkA), "/api/v1/federations/update",
                        new FederationViewBody(), body => "", Ct).GetAwaiter().GetResult();
                    bool ok = !res.Ok && res.Code == "signing.unavailable" && b.Global.Calls.Count == before;
                    return (ok, $"code={res.Code} sent={b.Global.Calls.Count - before}");
                }
            });
        }


        private static void Contributions(List<(string, bool, string)> r)
        {
            Check(r, "federation reports: only a member guild's verified work is queued", () =>
            {
                using (Bench b = new Bench("record"))
                {
                    bool unlinked = FederationService.Record("site", b.Guild, "", 1);
                    b.Founded();
                    bool site = FederationService.Record("site", b.Guild, "", 2);
                    bool anonQuest = FederationService.Record("quest", b.Guild, "", 1);
                    bool quest = FederationService.Record("quest", b.Guild, "player-global-id", 1);
                    bool unknown = FederationService.Record("silver", b.Guild, "player-global-id", 1);
                    FederationConfig had = FederationConfig.ApplyForTest(new FederationConfig { ReportContributions = false });
                    bool optedOut;
                    try { optedOut = FederationService.Record("site", b.Guild, "", 1); }
                    finally { FederationConfig.ApplyForTest(had); }
                    bool ok = !unlinked && site && !anonQuest && quest && !unknown && !optedOut && FederationOutbox.PendingFor(LinkA) == 2;
                    return (ok, $"unlinked={unlinked} site={site} anon quest={anonQuest} quest={quest} unknown={unknown} opted out={optedOut} queued={FederationOutbox.PendingFor(LinkA)}");
                }
            });

            Check(r, "federation reports: one person approving their own quest through a second account earns nothing", () =>
            {
                using (Bench b = new Bench("selfdeal"))
                {
                    b.Founded();
                    string alt = b.Member("alt", GuildMemberDto.RankMember);
                    string friend = b.Who("friend");
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    KmhKnownIdentities.Learn(b.Owner, "same-person", now);
                    KmhKnownIdentities.Learn(alt, "same-person", now);
                    KmhKnownIdentities.Learn(friend, "another-person", now);
                    bool selfDeal = FederationService.RecordQuestCompletion(alt, b.Owner);
                    bool honest = FederationService.RecordQuestCompletion(alt, friend);
                    // A poster that never verified is an alt the check cannot tell apart.
                    bool unverifiedPoster = FederationService.RecordQuestCompletion(alt, b.Who("unverified"));
                    bool ok = !selfDeal && honest && !unverifiedPoster && FederationOutbox.PendingFor(LinkA) == 1;
                    return (ok, $"self-deal={selfDeal} honest={honest} unverified poster={unverifiedPoster} queued={FederationOutbox.PendingFor(LinkA)}");
                }
            });

            Check(r, "federation reports: a member's verified join counts as activity once a UTC day", () =>
            {
                using (Bench b = new Bench("daily"))
                {
                    b.Founded();
                    string member = b.Member("daily", GuildMemberDto.RankMember);
                    FederationService.OnVerifiedJoin(member, "daily-global-id");
                    FederationService.OnVerifiedJoin(member, "daily-global-id");
                    FederationService.OnVerifiedJoin(b.Owner, "owner-global-id");
                    FederationService.OnVerifiedJoin(member, "");
                    int activity = FederationOutbox.TakeBatch(LinkA, 50).FindAll(e => e.Category == "activity").Count;
                    return (activity == 2, $"activity reports={activity} (two people, once each, none without an identity)");
                }
            });

            // Global never asks who did guild work.
            Check(r, "federation reports: guild work is sent without a player id, while a quest still names its player", () =>
            {
                using (Bench b = new Bench("noplayer"))
                {
                    b.Founded();
                    KmhKnownIdentities.Learn(b.Owner, "owner-global-id-np", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    FederationService.RecordGuildWork("road", b.Owner, 2);
                    FederationService.Record("frontier", b.Guild, "someone-global-id", 1);
                    FederationService.Record("quest", b.Guild, "quest-global-id", 1);
                    List<FederationOutbox.Entry> queued = FederationOutbox.TakeBatch(LinkA, 50);
                    bool queuedBare = queued.Count == 3 && queued.TrueForAll(e => e.Category == "quest" ? e.Player == "quest-global-id" : e.Player == "");
                    // A queue an older build wrote, still holding a name for guild work.
                    FederationOutbox.Add(new FederationOutbox.Entry { LinkId = LinkA, Category = "road", Player = "legacy-global-id",
                                                                      OccurredAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    FederationContributeBody sent = b.Global.Last<FederationContributeBody>("/federations/contribute");
                    bool sentBare = sent != null && sent.Entries.Count == 4
                                 && sent.Entries.TrueForAll(e => e.Category == "quest" ? e.Player == "quest-global-id" : e.Player == "");
                    bool verifies = sent != null && KmhGlobalSigning.Verify(KmhServerKey.PublicKey, sent.Canonical(), sent.Signature);
                    bool ok = queuedBare && sentBare && verifies;
                    return (ok, $"queued without a name={queuedBare} sent without a name={sentBare} signature verifies={verifies} "
                              + $"players sent=[{(sent == null ? "" : string.Join(",", sent.Entries.ConvertAll(e => e.Category + ":" + e.Player)))}]");
                }
            });

            Check(r, "federation reports: a retry after an outage resends the same report ids, and a delivered batch leaves the queue", () =>
            {
                using (Bench b = new Bench("flush"))
                {
                    b.Founded();
                    for (int i = 0; i < 3; i++) FederationService.Record("site", b.Guild, "", 1);
                    b.Global.Unreachable = true;
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    FederationContributeBody first = b.Global.Last<FederationContributeBody>("/federations/contribute");
                    int stillQueued = FederationOutbox.PendingFor(LinkA);
                    b.Global.Unreachable = false;
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    FederationContributeBody second = b.Global.Last<FederationContributeBody>("/federations/contribute");
                    bool sameIds = first != null && second != null && first.Entries.Count == 3
                                && string.Join(",", first.Entries.ConvertAll(e => e.ReportId)) == string.Join(",", second.Entries.ConvertAll(e => e.ReportId));
                    bool verifies = second != null && KmhGlobalSigning.Verify(KmhServerKey.PublicKey, second.Canonical(), second.Signature);
                    bool ok = stillQueued == 3 && sameIds && verifies && FederationOutbox.PendingFor(LinkA) == 0 && b.Global.SeenReports.Count == 3;
                    return (ok, $"queued after outage={stillQueued} same ids={sameIds} verifies={verifies} left={FederationOutbox.PendingFor(LinkA)} counted={b.Global.SeenReports.Count}");
                }
            });

            Check(r, "federation reports: a link Global says is no longer a member drops its queue instead of retrying it", () =>
            {
                using (Bench b = new Bench("gone"))
                {
                    b.Founded();
                    FederationService.Record("site", b.Guild, "", 1);
                    b.Global.RefuseOn["/federations/contribute"] = "KMH-FED-915";
                    b.Global.SelfState = "Removed";
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    bool ok = FederationOutbox.PendingFor(LinkA) == 0 && FederatedGuildStore.ById(LinkA).State == FederatedLinkState.Removed;
                    return (ok, $"left={FederationOutbox.PendingFor(LinkA)} state={FederatedGuildStore.ById(LinkA)?.State}");
                }
            });

            // Reports held back too long age past Global's 7-day window.
            Check(r, "federation reports: a link awaiting re-proof does not hold back another guild's reports", () =>
            {
                using (Bench b = new Bench("reproof"))
                {
                    b.Founded();
                    string guild2 = "kmh-st-fed-rp2-" + Salt, owner2 = b.Who("owner2");
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner2, guild2, out string why)) return (false, "seed: " + why);
                    try
                    {
                        FederationResult joined = FederationService.JoinAsync(owner2, "officer2-global-id", "ABCD-EFGH-JKMN", Ct).GetAwaiter().GetResult();
                        if (!joined.Ok) return (false, "join: " + joined.Code);
                        // The second guild's backlog is older, so it is first in line.
                        FederationService.Record("site", guild2, "", 1);
                        FederationService.Record("site", b.Guild, "", 1);
                        b.Global.RefuseLink[LinkB] = "KMH-GLD-959";
                        FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                        // On the same flush that met the refusal, not a later one.
                        bool aSent = FederationOutbox.PendingFor(LinkA) == 0;
                        int askedForB = b.Global.ContributionsFor(LinkB);
                        FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                        bool bKept = FederationOutbox.PendingFor(LinkB) == 1 && FederatedGuildStore.ById(LinkB)?.State == FederatedLinkState.ReproofRequired;
                        bool bWaits = b.Global.ContributionsFor(LinkB) == askedForB;
                        bool ok = aSent && bKept && bWaits;
                        return (ok, $"otherGuildSent={aSent}, reproofQueueKept={bKept}, notResentBeforeReproof={bWaits}");
                    }
                    finally { GuildStore.Leave(owner2, out _); }
                }
            });

            Check(r, "federation reports: the queue is bounded, oldest first", () =>
            {
                using (Bench b = new Bench("bound"))
                {
                    b.Founded();
                    FederationOutbox.FillForTest(LinkA, FederationOutbox.MaxEntries, "r");
                    for (int i = FederationOutbox.MaxEntries; i < FederationOutbox.MaxEntries + 5; i++)
                        FederationOutbox.Add(new FederationOutbox.Entry { LinkId = LinkA, Category = "site", ReportId = "r" + i });
                    List<FederationOutbox.Entry> head = FederationOutbox.TakeBatch(LinkA, 1);
                    bool ok = FederationOutbox.PendingCount == FederationOutbox.MaxEntries && head.Count == 1 && head[0].ReportId == "r5";
                    return (ok, $"pending={FederationOutbox.PendingCount} dropped={FederationOutbox.Dropped} first={head[0].ReportId}");
                }
            });

            // Reports that will never be counted are tallied by cause and shown, not silently lost.
            Check(r, "federation reports: reports Global refuses as too old, and a link's dropped queue, are counted as never counted", () =>
            {
                using (Bench b = new Bench("lost"))
                {
                    b.Founded();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    FederationOutbox.Add(new FederationOutbox.Entry { LinkId = LinkA, Category = "site", OccurredAtUnix = now - 8 * 86400 });
                    FederationOutbox.Add(new FederationOutbox.Entry { LinkId = LinkA, Category = "site", OccurredAtUnix = now - 60 });
                    long oldestBefore = FederationOutbox.OldestPendingUnix;
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    long rejected = FederationOutbox.Rejected;
                    FederationOutbox.Add(new FederationOutbox.Entry { LinkId = "no-such-link", Category = "site", OccurredAtUnix = now });
                    FederationService.FlushContributionsAsync(Ct).GetAwaiter().GetResult();
                    bool ok = oldestBefore == now - 8 * 86400 && rejected == 1 && FederationOutbox.Discarded == 1
                           && FederationOutbox.LostTotal == 2 && FederationOutbox.PendingCount == 0 && b.Global.SeenReports.Count == 1;
                    return (ok, $"oldest={now - oldestBefore}s ago rejected={rejected} discarded={FederationOutbox.Discarded} lost={FederationOutbox.LostTotal} "
                              + $"pending={FederationOutbox.PendingCount} counted={b.Global.SeenReports.Count}");
                }
            });

            Check(r, "federation reports: a queue write that fails drops nothing, not even the oldest it trimmed", () =>
            {
                using (Bench b = new Bench("trimfail"))
                {
                    b.Founded();
                    FederationOutbox.FillForTest(LinkA, FederationOutbox.MaxEntries, "t");
                    long dropped0 = FederationOutbox.Dropped;
                    bool added;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.FederationOutboxFile ? "selftest" : null))
                        added = FederationOutbox.Add(new FederationOutbox.Entry { LinkId = LinkA, Category = "site", ReportId = "overflow" });
                    List<FederationOutbox.Entry> head = FederationOutbox.TakeBatch(LinkA, 1);
                    bool ok = !added && FederationOutbox.PendingCount == FederationOutbox.MaxEntries && FederationOutbox.Dropped == dropped0
                           && head.Count == 1 && head[0].ReportId == "t0";
                    return (ok, $"added={added} pending={FederationOutbox.PendingCount} dropped {dropped0}->{FederationOutbox.Dropped} first={head[0]?.ReportId}");
                }
            });
        }


        private static void Departures(List<(string, bool, string)> r)
        {
            Check(r, "federation: a local guild disband ends the link here at once and tells Global when it can", () =>
            {
                using (Bench b = new Bench("disbandlocal"))
                {
                    b.Founded();
                    FederationService.Record("site", b.Guild, "", 1);
                    GuildStore.Leave(b.Owner, out _);
                    string state = FederatedGuildStore.ById(LinkA)?.State;
                    bool queued = FederationOutbox.Leaves().Exists(l => l.LinkId == LinkA && l.Reason == "guild_disbanded");
                    int reports = FederationOutbox.PendingFor(LinkA);

                    b.Global.Unreachable = true;
                    FederationService.FlushLeavesAsync(Ct).GetAwaiter().GetResult();
                    bool keptThroughOutage = FederationOutbox.PendingLeaves == 1;
                    b.Global.Unreachable = false;
                    FederationService.FlushLeavesAsync(Ct).GetAwaiter().GetResult();
                    FederationLeaveBody sent = b.Global.Last<FederationLeaveBody>("/federations/leave");
                    bool ok = state == FederatedLinkState.Left && queued && reports == 0 && keptThroughOutage
                           && FederationOutbox.PendingLeaves == 0 && sent?.Reason == "guild_disbanded" && sent.Officer == "officer-global-id";
                    return (ok, $"state={state} queued={queued} reports={reports} kept={keptThroughOutage} sent reason={sent?.Reason} officer={sent?.Officer}");
                }
            });
        }


        private static void Benefits(List<(string, bool, string)> r)
        {
            Check(r, "federation benefits: small, by level, capped by the owner, and only for a live member", () =>
            {
                using (Bench b = new Bench("perks"))
                {
                    double none = FederationBenefits.SiteOutputFactor(b.Guild);
                    b.Global.Level = 3;
                    b.Founded();
                    double l3 = FederationBenefits.SiteOutputFactor(b.Guild);
                    double road3 = FederationBenefits.RoadworksFactor(b.Guild);
                    b.Global.Level = 9;
                    FederationService.RefreshViewAsync(LinkA, Ct).GetAwaiter().GetResult();
                    double l9 = FederationBenefits.SiteOutputFactor(b.Guild);
                    double road9 = FederationBenefits.RoadworksFactor(b.Guild);
                    FederationConfig had = FederationConfig.ApplyForTest(new FederationConfig { MaxSiteOutputBonusPercent = 1 });
                    double capped = FederationBenefits.SiteOutputFactor(b.Guild);
                    FederationConfig.ApplyForTest(new FederationConfig { Benefits = false });
                    double off = FederationBenefits.SiteOutputFactor(b.Guild);
                    FederationConfig.ApplyForTest(had);
                    FederatedGuildStore.ById(LinkA).ViewReadUtcTicks = DateTime.UtcNow.Ticks - FederationService.BenefitGrace.Ticks - TimeSpan.TicksPerMinute;
                    FederationBenefits.Rebuild();
                    double stale = FederationBenefits.SiteOutputFactor(b.Guild);
                    bool ok = none == 1.0 && Math.Abs(l3 - 1.01) < 1e-9 && road3 == 1.0 && Math.Abs(l9 - 1.03) < 1e-9
                           && Math.Abs(road9 - 1.03) < 1e-9 && Math.Abs(capped - 1.01) < 1e-9 && off == 1.0 && stale == 1.0;
                    return (ok, $"none={none} l3={l3} road3={road3} l9={l9} road9={road9} capped={capped} off={off} stale={stale}");
                }
            });

            Check(r, "federation benefits: no setting can raise them past this release's ceiling", () =>
            {
                var cfg = new FederationConfig { MaxSiteOutputBonusPercent = 50, MaxRoadworksBonusPercent = 99, RewardScale = 10 };
                cfg.Clamp();
                bool ok = cfg.MaxSiteOutputBonusPercent <= 3 && cfg.MaxRoadworksBonusPercent <= 3 && cfg.RewardScale <= 2;
                return (ok, $"site={cfg.MaxSiteOutputBonusPercent} road={cfg.MaxRoadworksBonusPercent} scale={cfg.RewardScale}");
            });
        }


        private static void Rewards(List<(string, bool, string)> r)
        {
            JObject Completed(string id, bool eligible) => new JObject
            {
                ["objective_id"] = id, ["category"] = "site", ["target"] = 75, ["progress"] = 80,
                ["state"] = "Completed", ["reward_renown"] = 120, ["reward_eligible"] = eligible,
            };

            Check(r, "federation rewards: a completed objective pays this guild's own vault here, once", () =>
            {
                using (Bench b = new Bench("reward"))
                {
                    b.Global.Objectives.Add(Completed("11111111111111111111111111111111", true));
                    b.Global.Objectives.Add(Completed("22222222222222222222222222222222", false));
                    FederationRewards.ExpansionRequiredForTest = _ => false;
                    b.Founded();
                    FederationRewards.Bundle bundle = FederationRewards.Choose("11111111111111111111111111111111");
                    int Items() => Features.Treasury.TreasuryStore.GuildItemForTest(b.Guild, bundle.DefName);
                    int before = Items();
                    int first = FederationRewards.GrantPending(FederatedGuildStore.ById(LinkA));
                    int afterFirst = Items();
                    int second = FederationRewards.GrantPending(FederatedGuildStore.ById(LinkA));
                    bool ok = first == 1 && second == 0 && afterFirst - before == bundle.Count && Items() == afterFirst
                           && FederatedGuildStore.HasRewarded(LinkA, "11111111111111111111111111111111")
                           && !FederatedGuildStore.HasRewarded(LinkA, "22222222222222222222222222222222");
                    return (ok, $"granted={first}/{second} items {before}->{afterFirst} bundle={bundle.Count}x {bundle.DefName}");
                }
            });

            // A rejoin loses the link's reward list; the filler deposits would push out a capped vault marker.
            Check(r, "federation rewards: a guild that leaves and rejoins is never paid the same objective twice", () =>
            {
                using (Bench b = new Bench("rejoin"))
                {
                    const string objective = "44444444444444444444444444444444";
                    const string rejoined = "dddddddddddddddddddddddddddddd03";
                    b.Global.Objectives.Add(Completed(objective, true));
                    FederationRewards.ExpansionRequiredForTest = _ => false;
                    FederatedGuildLink first = b.Founded();
                    FederationRewards.Bundle bundle = FederationRewards.Choose(objective);
                    int Items() => Features.Treasury.TreasuryStore.GuildItemForTest(b.Guild, bundle.DefName);
                    int paidOnce = FederationRewards.GrantPending(first);
                    int afterFirst = Items();

                    // The same guild back under a new link, with the same week's objective still eligible.
                    FederatedGuildLink again = FederatedGuildStore.Remember(rejoined, FedId, b.Guild, "officer-global-id",
                                                                           first.SigningGeneration, first.Incarnation);
                    FederatedGuildStore.RememberView(rejoined, first.View);
                    bool replaced = again != null && FederatedGuildStore.ById(LinkA) == null
                                 && !FederatedGuildStore.HasRewarded(rejoined, objective);
                    for (int i = 0; i < 300; i++)
                        Features.Treasury.TreasuryStore.DepositGuildItemOnce(b.Guild, "WoodLog", 1, "selftest-filler:" + i, "filler");
                    int paidAgain = FederationRewards.GrantPending(FederatedGuildStore.ById(rejoined));
                    // The vault's own refusal, not only the check in front of it.
                    bool vaultRefuses = Features.Treasury.TreasuryStore.DepositGuildItemOnce(
                        b.Guild, bundle.DefName, bundle.Count, Features.Treasury.TreasuryStore.OneTimeRewardPrefix + objective);

                    bool ok = paidOnce == 1 && replaced && paidAgain == 0 && vaultRefuses && Items() == afterFirst
                           && FederatedGuildStore.HasRewarded(rejoined, objective);
                    return (ok, $"first={paidOnce}, rowReplaced={replaced}, after 300 vault entries second={paidAgain}, "
                              + $"items {afterFirst}->{Items()} ({bundle.DefName})");
                }
            });

            // Global's view carries an empty detail, never null, so the end reason must come from EndReason.
            Check(r, "federation: a link Global ended keeps the reason Global gave for it", () =>
            {
                using (Bench b = new Bench("endreason"))
                {
                    b.Founded();
                    FederationView ended = FederationService.ParseView(JObject.Parse(
                        "{\"self\":{\"link_id\":\"" + LinkA + "\",\"state\":\"Left\",\"detail\":\"\",\"end_reason\":\"identity_rebound\"}}"));
                    FederatedGuildStore.RememberView(LinkA, ended);
                    FederatedGuildLink now = FederatedGuildStore.ById(LinkA);
                    bool ok = now != null && now.State == FederatedLinkState.Left && now.Detail == "identity_rebound";
                    return (ok, $"state={now?.State} detail='{now?.Detail}'");
                }
            });

            Check(r, "federation rewards: a reward the vault could not write is paid once, later, and never marked before", () =>
            {
                using (Bench b = new Bench("rewardfail"))
                {
                    const string objective = "55555555555555555555555555555555";
                    b.Global.Objectives.Add(Completed(objective, true));
                    FederationRewards.ExpansionRequiredForTest = _ => false;
                    FederatedGuildLink link = b.Founded();
                    FederationRewards.Bundle bundle = FederationRewards.Choose(objective);
                    int Items() => Features.Treasury.TreasuryStore.GuildItemForTest(b.Guild, bundle.DefName);
                    int before = Items();
                    int failed;
                    using (Persistence.JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                        failed = FederationRewards.GrantPending(link);
                    bool nothingMarked = failed == 0 && Items() == before && !FederatedGuildStore.HasRewarded(LinkA, objective);
                    int retried = FederationRewards.GrantPending(FederatedGuildStore.ById(LinkA));
                    int repeated = FederationRewards.GrantPending(FederatedGuildStore.ById(LinkA));
                    bool ok = nothingMarked && retried == 1 && repeated == 0 && Items() - before == bundle.Count;
                    return (ok, $"failed={failed} marked={FederatedGuildStore.HasRewarded(LinkA, objective)}, retried={retried}, "
                              + $"repeated={repeated}, items {before}->{Items()}");
                }
            });

            Check(r, "federation rewards: an expansion item is chosen only when the server requires that expansion", () =>
            {
                FederationRewards.DefKnownForTest = _ => true;
                try
                {
                    var picked = new HashSet<string>();
                    FederationRewards.ExpansionRequiredForTest = _ => false;
                    for (int i = 0; i < 400; i++) picked.Add(FederationRewards.Choose("obj-" + i).DefName);
                    bool noneWithout = !picked.Contains("HemogenPack") && !picked.Contains("Bioferrite");

                    var withDlc = new HashSet<string>();
                    FederationRewards.ExpansionRequiredForTest = e => e == "Biotech";
                    for (int i = 0; i < 400; i++) withDlc.Add(FederationRewards.Choose("obj-" + i).DefName);
                    bool biotechOnly = withDlc.Contains("HemogenPack") && !withDlc.Contains("Bioferrite");

                    FederationRewards.DefKnownForTest = _ => false;
                    var unknownDef = new HashSet<string>();
                    for (int i = 0; i < 400; i++) unknownDef.Add(FederationRewards.Choose("obj-" + i).DefName);
                    bool fallback = !unknownDef.Contains("HemogenPack") && unknownDef.Count >= 3;

                    bool stable = FederationRewards.Choose("same").DefName == FederationRewards.Choose("same").DefName;
                    bool ok = noneWithout && biotechOnly && fallback && stable;
                    return (ok, $"without={string.Join(",", picked)} | biotech={string.Join(",", withDlc)} | unknown def={string.Join(",", unknownDef)}");
                }
                finally { FederationRewards.ExpansionRequiredForTest = null; FederationRewards.DefKnownForTest = null; }
            });

            Check(r, "federation rewards: the owner can turn them off or scale them, and nothing is paid to a guild that left", () =>
            {
                using (Bench b = new Bench("rewardoff"))
                {
                    b.Global.Objectives.Add(Completed("33333333333333333333333333333333", true));
                    b.Founded();
                    FederationConfig had = FederationConfig.ApplyForTest(new FederationConfig { ObjectiveRewards = false });
                    int off;
                    try { off = FederationRewards.GrantPending(FederatedGuildStore.ById(LinkA)); }
                    finally { FederationConfig.ApplyForTest(had); }
                    FederatedGuildStore.SetState(LinkA, FederatedLinkState.Left, "left");
                    int left = FederationRewards.GrantPending(FederatedGuildStore.ById(LinkA));
                    FederationConfig.ApplyForTest(new FederationConfig { RewardScale = 0.5 });
                    int half = FederationRewards.Scaled(80);
                    FederationConfig.ApplyForTest(had);
                    bool ok = off == 0 && left == 0 && half == 40 && FederationRewards.Scaled(1) == 1;
                    return (ok, $"off={off} after leaving={left} half of 80={half}");
                }
            });
        }


        private static void Snapshot(List<(string, bool, string)> r)
        {
            Check(r, "federation window: what a player may do is decided per player, and the treasury is not in it", () =>
            {
                using (Bench b = new Bench("snap"))
                {
                    b.Founded();
                    string member = b.Member("member", GuildMemberDto.RankMember);
                    string admin = b.Member("admin", GuildMemberDto.RankAdmin);
                    ServerClient Client(string user, string id)
                    {
                        var c = new ServerClient(null, null, createListener: false);
                        c.GetData<UserFile>(new UserFile { Username = user });
                        if (id != null) KmhVerifiedPlayers.Record(c, id, user);
                        return c;
                    }
                    ServerClient owner = Client(b.Owner, "g-owner"), unverified = Client(b.Owner, null),
                                 plain = Client(member, "g-member"), adm = Client(admin, "g-admin");
                    try
                    {
                        JObject so = JObject.FromObject(FederationHandler.BuildPayload(owner, b.Owner));
                        JObject su = JObject.FromObject(FederationHandler.BuildPayload(unverified, b.Owner));
                        JObject sm = JObject.FromObject(FederationHandler.BuildPayload(plain, member));
                        JObject sa = JObject.FromObject(FederationHandler.BuildPayload(adm, admin));
                        string text = so.ToString();
                        bool noTreasury = !text.Contains("\"balance\"") && !text.Contains("deposit") && !text.Contains("withdraw")
                                       && !text.Contains("silver");
                        bool ok = (bool)so["may_manage"] && (bool)so["may_lead"] && (bool)so["linked"] && (string)so["federation"]["tag"] == "IRON"
                               && !(bool)su["may_manage"] && !(bool)su["verified"] && ((string)su["verified_why"]).Length > 0
                               && !(bool)sm["may_manage"] && ((string)sm["manage_why"]).Length > 0
                               && (bool)sa["may_manage"] && !(bool)sa["may_lead"]
                               && ((JArray)so["invites"]).Count == 2 && ((JArray)sa["invites"]).Count == 1 && ((JArray)sm["invites"]).Count == 0
                               && noTreasury && (long)so["revision"] < (long)sa["revision"];
                        return (ok, $"owner manage/lead={so["may_manage"]}/{so["may_lead"]} unverified={su["may_manage"]} member={sm["may_manage"]} "
                                  + $"admin={sa["may_manage"]}/{sa["may_lead"]} invites={((JArray)so["invites"]).Count}/{((JArray)sa["invites"]).Count}/{((JArray)sm["invites"]).Count} no treasury={noTreasury}");
                    }
                    finally
                    {
                        foreach (ServerClient c in new[] { owner, unverified, plain, adm }) KmhVerifiedPlayers.Forget(c);
                    }
                }
            });

            Check(r, "federation window: a never-read view is not stale, and bonuses carry an end time only where they apply", () =>
            {
                using (Bench b = new Bench("syncline"))
                {
                    b.Founded();
                    JObject Snap() => JObject.FromObject(FederationHandler.BuildPayload(null, b.Owner));
                    JObject fresh = Snap();
                    long grace = (long)FederationService.BenefitGrace.TotalSeconds;
                    bool freshOk = !(bool)fresh["stale"] && (long)fresh["benefits_until"] == (long)fresh["synced_at"] + grace;
                    FederatedGuildLink link = FederatedGuildStore.ById(LinkA);
                    link.ViewReadUtcTicks = 0;
                    JObject never = Snap();
                    bool neverOk = !(bool)never["stale"] && (long)never["synced_at"] == 0 && (long)never["benefits_until"] == 0;
                    link.ViewReadUtcTicks = DateTime.UtcNow.Ticks - FederationService.ViewMaxAge.Ticks * 4;
                    JObject old = Snap();
                    FederationConfig had = FederationConfig.ApplyForTest(new FederationConfig { Benefits = false });
                    JObject off;
                    try { off = Snap(); }
                    finally { FederationConfig.ApplyForTest(had); }
                    bool ok = freshOk && neverOk && (bool)old["stale"] && (long)old["benefits_until"] > 0 && (long)off["benefits_until"] == 0;
                    return (ok, $"fresh stale={fresh["stale"]} until-synced={(long)fresh["benefits_until"] - (long)fresh["synced_at"]}s | never read stale={never["stale"]} until={never["benefits_until"]} "
                              + $"| 40 min old stale={old["stale"]} | bonuses off until={off["benefits_until"]}");
                }
            });
        }
    }
}
