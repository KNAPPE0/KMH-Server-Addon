using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Features.Guilds;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global.Federation
{
    internal sealed class FederationResult
    {
        public bool   Ok { get; set; }
        public string Code { get; set; } = "";
        // Player-facing refusal text when only this server's rules know the reason.
        public string Detail { get; set; } = "";
        public string LinkId { get; set; } = "";
        public string InviteCode { get; set; } = "";
        public string InviteTarget { get; set; } = "";
        public long   InviteExpiresAt { get; set; }

        public static FederationResult Fail(string code) => new FederationResult { Code = code ?? "" };
        public static FederationResult Refuse(string detail) => new FederationResult { Code = "refused", Detail = detail ?? "" };
        public static FederationResult Good(string linkId) => new FederationResult { Ok = true, LinkId = linkId ?? "" };
    }

    // Global owns identity, membership and renown; nothing here moves silver, items or resources between servers.
    internal static class FederationService
    {
        private const string CreatePath       = "/api/v1/federations/create";
        private const string JoinPath         = "/api/v1/federations/join";
        private const string ViewPath         = "/api/v1/federations/view";
        private const string InvitePath       = "/api/v1/guilds/invites";
        private const string InviteRevokePath = "/api/v1/federations/invites/revoke";
        private const string LeavePath        = "/api/v1/federations/leave";
        private const string RemovePath       = "/api/v1/federations/remove";
        private const string TransferPath     = "/api/v1/federations/transfer";
        private const string UpdatePath       = "/api/v1/federations/update";
        private const string DisbandPath      = "/api/v1/federations/disband";
        private const string ContributePath   = "/api/v1/federations/contribute";
        private const string ReprovePath      = "/api/v1/guilds/reprove";

        public const int BatchSize = 200;
        public static readonly TimeSpan ViewMaxAge = TimeSpan.FromMinutes(10);
        // Local benefits ride the last good view this long, so an outage pauses them rather than ending them.
        public static readonly TimeSpan BenefitGrace = TimeSpan.FromHours(72);

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static string Nonce() => Guid.NewGuid().ToString("N");
        private static int Generation() => GlobalConfig.Current.SigningGeneration;
        private static string ServerId() => KmhServerIdentity.Id;

        private static Task<GlobalCallResult> SendAsync(string path, object body, CancellationToken ct)
            => KmhGlobalPost.SendAsync(path, body, ct);

        // An older Global answers a federation route with a bare 404; remembered so the window can say so plainly.
        private static volatile bool _unsupported;
        private static long _heartbeatRevision = -1;
        private static volatile bool _revisionMoved;
        private static readonly object _syncLock = new object();
        private static bool _catchingUp;

        internal static string UnavailableForTest;

        public static string UnavailableReason()
        {
            if (UnavailableForTest != null) return UnavailableForTest;
            FeaturesConfig f = FeaturesConfig.Current;
            if (!f.GuildFederation || !f.Guilds) return "feature.off";
            GlobalConfig cfg = GlobalConfig.Current;
            if (cfg == null || (!cfg.Enabled && !KmhGlobalBackend.Allowed)) return "global.off";
            if (!GlobalSetupState.Current_.IsRegistered || string.IsNullOrEmpty(ServerId())) return "global.setup";
            if (KmhGlobalRegistrar.IsRevoked) return "global.revoked";
            if (_unsupported) return "global.unsupported";
            if (KmhGlobalRegistrar.State == GlobalLinkState.Unreachable) return "global.unreachable";
            return "";
        }

        public static bool Available => UnavailableReason() == "";

        public static FederatedGuildLink LinkFor(string actor)
        {
            string guild = GuildStore.CurrentGuildOf(actor);
            return string.IsNullOrEmpty(guild) ? null : LinkForGuild(guild);
        }

        public static FederatedGuildLink LinkForGuild(string guild)
            => string.IsNullOrEmpty(guild) ? null : FederatedGuildStore.ForGuildInstance(guild, GuildStore.IncarnationOf(guild));


        // Authority is checked at the moment of acting, never from what a window showed.
        private static FederationResult Officer(string actor, string officer, out string guild)
        {
            guild = null;
            string why = UnavailableReason();
            if (why != "") return FederationResult.Fail(why);
            if (string.IsNullOrEmpty(actor)) return FederationResult.Fail("player.unknown");
            // Proven by this session's own Global join ticket, which is also the proof of a Steam-backed account.
            if (string.IsNullOrEmpty(officer)) return FederationResult.Fail("account.unverified");
            if (!GuildStore.CanAdministerFederation(actor, out string reason)) return FederationResult.Refuse(reason);
            guild = GuildStore.CurrentGuildOf(actor);
            if (string.IsNullOrEmpty(guild)) return FederationResult.Fail("guild.none");
            return null;
        }

        private static FederationResult Member(string actor, string officer, out FederatedGuildLink link)
        {
            link = null;
            FederationResult bad = Officer(actor, officer, out string guild);
            if (bad != null) return bad;
            link = LinkForGuild(guild);
            if (link == null || !link.MayRead) return FederationResult.Fail("guild.unlinked");
            if (!link.FederationActive) return FederationResult.Fail("KMH-FED-906");
            return null;
        }

        private static FederationResult Leader(string actor, string officer, out FederatedGuildLink link)
        {
            FederationResult bad = Member(actor, officer, out link);
            if (bad != null) return bad;
            if (!GuildStore.CanLeadFederation(actor, out string reason)) return FederationResult.Refuse(reason);
            if (!link.IsLeader) return FederationResult.Fail("KMH-FED-907");
            if (link.State != FederatedLinkState.Active) return FederationResult.Fail("KMH-GLD-959");
            return null;
        }


        public static async Task<FederationResult> CreateAsync(string actor, string officer, string name, string tag,
                                                               string description, string color, CancellationToken ct)
        {
            FederationResult bad = Officer(actor, officer, out string guild);
            if (bad != null) return bad;
            FederatedGuildLink existing = LinkForGuild(guild);
            if (existing != null && existing.MayRead) return FederationResult.Fail("guild.linked");
            string problem = FederationRules.ProfileProblem(name, tag, description, color);
            if (problem != null) return FederationResult.Refuse(problem);

            FederationCreateBody body = new FederationCreateBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LocalGuild = guild, Officer = officer, Name = FederationRules.NormalizeName(name),
                Tag = FederationRules.NormalizeTag(tag), Description = (description ?? "").Trim(),
                Color = (color ?? "").Trim().ToUpperInvariant(),
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return FederationResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(CreatePath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);
            return AdoptNewLink(r, guild, officer, "created");
        }

        public static async Task<FederationResult> JoinAsync(string actor, string officer, string invite, CancellationToken ct)
        {
            FederationResult bad = Officer(actor, officer, out string guild);
            if (bad != null) return bad;
            FederatedGuildLink existing = LinkForGuild(guild);
            if (existing != null && existing.MayRead) return FederationResult.Fail("guild.linked");
            if (GuildInviteRevokeBody.NormalizeInvite(invite).Length == 0) return FederationResult.Fail("KMH-GLD-966");

            FederationJoinBody body = new FederationJoinBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LocalGuild = guild, Officer = officer, Invite = invite ?? "",
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return FederationResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(JoinPath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);
            return AdoptNewLink(r, guild, officer, "joined");
        }

        // Persisted before it is reported: a membership unprovable after a restart is worse than one never made.
        private static FederationResult AdoptNewLink(GlobalCallResult r, string guild, string officer, string verb)
        {
            FederationView view = ParseView(r.Body);
            string linkId = view?.Self?.LinkId ?? "";
            string fedId = view?.Federation?.Id ?? "";
            if (linkId.Length == 0 || fedId.Length == 0) return FederationResult.Fail("link.missing");

            FederatedGuildLink link = FederatedGuildStore.Remember(linkId, fedId, guild, officer, Generation());
            if (link == null) return FederationResult.Fail("persistence.refused");
            FederatedGuildStore.RememberView(linkId, view);
            ServerLog.Info($"Federation: '{guild}' {verb} [{view.Federation.Tag}] {view.Federation.Name} ({fedId}).");
            return FederationResult.Good(linkId);
        }

        // An invite is for one other registered server, used once, and only while this link's authority is current.
        public static async Task<FederationResult> InviteAsync(string actor, string officer, string targetServerId,
                                                               CancellationToken ct)
        {
            FederationResult bad = Member(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            if (!link.AuthorityCurrent) return FederationResult.Fail("KMH-GLD-959");
            string target = (targetServerId ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
            if (!IsServerId(target) || target == ServerId()) return FederationResult.Fail("invite.target");

            GuildInviteBody body = new GuildInviteBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, TargetServerId = target,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return FederationResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(InvitePath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);
            string code = ValueOf(r.Body, "invite");
            if (code.Length == 0) return FederationResult.Fail("invite.missing");

            ServerLog.Info($"Federation: {actor} invited server {target} to join '{link.LocalGuild}''s federation.");
            await RefreshViewAsync(link.LinkId, ct).ConfigureAwait(false);
            return new FederationResult
            {
                Ok = true, LinkId = link.LinkId, InviteCode = code, InviteTarget = target,
                InviteExpiresAt = LongOf(r.Body, "expires_at"),
            };
        }

        public static async Task<FederationResult> RevokeInviteAsync(string actor, string officer, string inviteId,
                                                                     CancellationToken ct)
        {
            FederationResult bad = Member(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            string id = (inviteId ?? "").Trim().ToLowerInvariant();
            if (!IsServerId(id)) return FederationResult.Fail("KMH-GLD-966");

            FederationInviteRevokeBody body = new FederationInviteRevokeBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, InviteId = id,
            };
            return await MutateAsync(link, InviteRevokePath, body, b => b.Signature = KmhServerKey.Sign(b.Canonical()), ct)
                .ConfigureAwait(false);
        }

        // The leader must hand over first and the last member disbands instead; Global enforces both.
        public static async Task<FederationResult> LeaveAsync(string actor, string officer, CancellationToken ct)
        {
            FederationResult bad = Member(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            // Leaving mid-operation would strand silver already escrowed or held at Global.
            if (Treasury.ConnectedTreasuryStore.OpenOpForLink(link.LinkId) != null) return FederationResult.Fail("operation.inflight");

            FederationLeaveBody body = new FederationLeaveBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, Reason = "",
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return FederationResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(LeavePath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);

            FederatedGuildStore.SetState(link.LinkId, FederatedLinkState.Left, FederatedLinkDetail.LeftBy(actor));
            AdoptView(link.LinkId, r.Body);
            FederationOutbox.DropFor(link.LinkId);
            ServerLog.Info($"Federation: '{link.LocalGuild}' left its federation ({actor}).");
            return FederationResult.Good(link.LinkId);
        }

        public static async Task<FederationResult> RemoveAsync(string actor, string officer, string targetLinkId,
                                                               CancellationToken ct)
        {
            FederationResult bad = Leader(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            string target = (targetLinkId ?? "").Trim().ToLowerInvariant();
            if (!IsServerId(target)) return FederationResult.Fail("KMH-FED-908");
            if (target == link.LinkId) return FederationResult.Fail("KMH-FED-909");

            FederationRemoveBody body = new FederationRemoveBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, TargetLinkId = target,
            };
            return await MutateAsync(link, RemovePath, body, b => b.Signature = KmhServerKey.Sign(b.Canonical()), ct)
                .ConfigureAwait(false);
        }

        public static async Task<FederationResult> TransferAsync(string actor, string officer, string targetLinkId,
                                                                 CancellationToken ct)
        {
            FederationResult bad = Leader(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            string target = (targetLinkId ?? "").Trim().ToLowerInvariant();
            if (!IsServerId(target)) return FederationResult.Fail("KMH-FED-908");
            if (target == link.LinkId) return FederationResult.Fail("KMH-FED-909");

            FederationTransferBody body = new FederationTransferBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, TargetLinkId = target,
            };
            return await MutateAsync(link, TransferPath, body, b => b.Signature = KmhServerKey.Sign(b.Canonical()), ct)
                .ConfigureAwait(false);
        }

        public static async Task<FederationResult> UpdateAsync(string actor, string officer, string name, string tag,
                                                               string description, string color, CancellationToken ct)
        {
            FederationResult bad = Leader(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            string problem = FederationRules.ProfileProblem(name, tag, description, color);
            if (problem != null) return FederationResult.Refuse(problem);

            FederationUpdateBody body = new FederationUpdateBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, Name = FederationRules.NormalizeName(name),
                Tag = FederationRules.NormalizeTag(tag), Description = (description ?? "").Trim(),
                Color = (color ?? "").Trim().ToUpperInvariant(),
            };
            return await MutateAsync(link, UpdatePath, body, b => b.Signature = KmhServerKey.Sign(b.Canonical()), ct)
                .ConfigureAwait(false);
        }

        // A tombstone at Global, never an erasure; the typed-back tag is checked here, but Global decides.
        public static async Task<FederationResult> DisbandAsync(string actor, string officer, string confirm, string reason,
                                                                CancellationToken ct)
        {
            FederationResult bad = Leader(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;
            string tag = link.View?.Federation?.Tag ?? "";
            if (tag.Length == 0 || !string.Equals(FederationRules.NormalizeTag(confirm), tag, StringComparison.Ordinal))
                return FederationResult.Fail("KMH-FED-912");
            if (Treasury.ConnectedTreasuryStore.OpenOpForLink(link.LinkId) != null) return FederationResult.Fail("operation.inflight");

            FederationDisbandBody body = new FederationDisbandBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer, Confirm = tag,
                Reason = Util.KmhSafe.Cap((reason ?? "").Trim(), 200),
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return FederationResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(DisbandPath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);

            FederatedGuildStore.SetState(link.LinkId, FederatedLinkState.Disbanded, FederatedLinkDetail.DisbandedBy(actor));
            AdoptView(link.LinkId, r.Body);
            FederationOutbox.DropFor(link.LinkId);
            ServerLog.Info($"Federation: [{tag}] was disbanded by {actor} of '{link.LocalGuild}'.");
            return FederationResult.Good(link.LinkId);
        }

        // A restore or a signing-key change makes old authority stale; an officer proves it again here.
        public static async Task<FederationResult> ReproveAsync(string actor, string officer, CancellationToken ct)
        {
            FederationResult bad = Member(actor, officer, out FederatedGuildLink link);
            if (bad != null) return bad;

            GuildReproveBody body = new GuildReproveBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId, Officer = officer,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return FederationResult.Fail("signing.unavailable");

            GlobalCallResult r = await SendAsync(ReprovePath, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);

            // Records the generation it signed under, which is the one Global accepted.
            if (FederatedGuildStore.Remember(link.LinkId, link.GuildId, link.LocalGuild, officer, Generation()) == null)
                return FederationResult.Fail("persistence.refused");
            await RefreshViewAsync(link.LinkId, ct).ConfigureAwait(false);
            ServerLog.Info($"Federation: '{link.LocalGuild}' proved its federation link again ({actor}).");
            return FederationResult.Good(link.LinkId);
        }

        internal static async Task<FederationResult> MutateAsync<T>(FederatedGuildLink link, string path, T body,
                                                                    Func<T, string> sign, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sign(body))) return FederationResult.Fail("signing.unavailable");
            GlobalCallResult r = await SendAsync(path, body, ct).ConfigureAwait(false);
            if (r == null || !r.Ok) return Remote(r);
            AdoptView(link.LinkId, r.Body);
            return FederationResult.Good(link.LinkId);
        }


        public static async Task<bool> RefreshViewAsync(string linkId, CancellationToken ct)
        {
            FederatedGuildLink link = FederatedGuildStore.ById(linkId);
            if (link == null || string.IsNullOrEmpty(ServerId())) return false;

            FederationViewBody body = new FederationViewBody
            {
                ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                LinkId = link.LinkId,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return false;

            GlobalCallResult r = await SendAsync(ViewPath, body, ct).ConfigureAwait(false);
            if (r != null && r.Ok)
            {
                _unsupported = false;
                AdoptView(link.LinkId, r.Body);
                return true;
            }
            if (r == null || r.Unreachable) return false;
            if (r.Status == 404 && string.IsNullOrEmpty(r.ErrorCode)) { _unsupported = true; return false; }
            // Global owns link state, so a refusal it names is adopted, not argued with.
            if (r.ErrorCode == "KMH-GLD-959")
                FederatedGuildStore.SetState(link.LinkId, FederatedLinkState.ReproofRequired, FederatedLinkDetail.ReproofRequired);
            else if (r.ErrorCode == "KMH-GLD-958")
                FederatedGuildStore.SetState(link.LinkId, FederatedLinkState.Revoked, FederatedLinkDetail.LinkUnknown);
            return false;
        }

        private static void AdoptView(string linkId, JObject body)
        {
            FederationView view = ParseView(body);
            if (view?.Federation == null || string.IsNullOrEmpty(view.Federation.Id)) return;
            FederatedGuildStore.RememberView(linkId, view);
        }

        internal static FederationView ParseView(JObject body)
        {
            if (body == null) return null;
            try { return body.ToObject<FederationView>(); }
            catch (Exception ex)
            {
                ServerLog.Verbose($"Federation: Global's view could not be read ({ex.GetType().Name}).");
                return null;
            }
        }


        // From a Global that sends only the highest revision; one federation can then hide another's change.
        public static void OnHeartbeatRevision(long revision)
        {
            if (revision < 0) return;
            long was = Interlocked.Exchange(ref _heartbeatRevision, revision);
            if (was != revision) _revisionMoved = true;
        }

        private static readonly object _movedLock = new object();
        private static readonly HashSet<string> _movedLinks = new HashSet<string>(StringComparer.Ordinal);

        public static void OnHeartbeatRevisions(IDictionary<string, long> revisions)
        {
            if (revisions == null) return;
            var moved = new List<string>();
            foreach (FederatedGuildLink link in FederatedGuildStore.All())
            {
                if (!link.MayRead) continue;
                if (!revisions.TryGetValue(link.GuildId, out long rev) || rev != (link.View?.Federation?.Revision ?? -1))
                    moved.Add(link.LinkId);
            }
            lock (_movedLock) foreach (string id in moved) _movedLinks.Add(id);
        }

        private static HashSet<string> TakeMovedLinks()
        {
            lock (_movedLock)
            {
                var taken = new HashSet<string>(_movedLinks, StringComparer.Ordinal);
                _movedLinks.Clear();
                return taken;
            }
        }

        // One run at a time, so a slow Global cannot stack these up.
        public static async Task AfterHeartbeatAsync(CancellationToken ct)
        {
            lock (_syncLock) { if (_catchingUp) return; _catchingUp = true; }
            try
            {
                if (UnavailableReason() != "") return;
                // Before anything below can change a link: a re-proof demand found by the report flush counts too.
                foreach (FederatedGuildLink link in FederatedGuildStore.All()) FederationNotices.Baseline(link);
                await FlushLeavesAsync(ct).ConfigureAwait(false);
                await FlushContributionsAsync(ct).ConfigureAwait(false);

                bool moved = _revisionMoved;
                _revisionMoved = false;
                HashSet<string> flagged = TakeMovedLinks();
                foreach (FederatedGuildLink link in FederatedGuildStore.All())
                {
                    if (!link.MayRead) continue;
                    bool stale = DateTime.UtcNow.Ticks - link.ViewReadUtcTicks > ViewMaxAge.Ticks;
                    if (moved || stale || flagged.Contains(link.LinkId)) await RefreshViewAsync(link.LinkId, ct).ConfigureAwait(false);
                    FederationRewards.GrantPending(FederatedGuildStore.ById(link.LinkId));
                }
                // Every link, including one this beat ended, so its guild still hears what changed.
                foreach (FederatedGuildLink link in FederatedGuildStore.All()) FederationNotices.Announce(link);
            }
            finally { lock (_syncLock) _catchingUp = false; }
        }

        internal static async Task FlushLeavesAsync(CancellationToken ct)
        {
            foreach (FederationOutbox.Leave pending in FederationOutbox.Leaves())
            {
                FederatedGuildLink link = FederatedGuildStore.ById(pending.LinkId);
                FederationLeaveBody body = new FederationLeaveBody
                {
                    ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                    LinkId = pending.LinkId,
                    // No session exists for a guild that is gone, so the officer who last proved the link stands in.
                    Officer = link?.ProvedBy ?? "", Reason = pending.Reason ?? "",
                };
                body.Signature = KmhServerKey.Sign(body.Canonical());
                if (string.IsNullOrEmpty(body.Signature)) return;

                GlobalCallResult r = await SendAsync(LeavePath, body, ct).ConfigureAwait(false);
                if (r == null || r.Unreachable) return;
                // Done, or nothing left at Global to leave: either way the notice has served its purpose.
                if (r.Ok || r.ErrorCode == "KMH-GLD-958" || r.ErrorCode == "KMH-FED-915" || r.ErrorCode == "KMH-FED-906")
                {
                    FederationOutbox.LeaveDelivered(pending.LinkId);
                    if (r.Ok) AdoptView(pending.LinkId, r.Body);
                    continue;
                }
                ServerLog.Verbose($"Federation: Global refused the departure of link {pending.LinkId} ({r.ErrorCode}); retried later.");
            }
        }

        internal static async Task FlushContributionsAsync(CancellationToken ct)
        {
            foreach (string linkId in FederationOutbox.LinksWithEntries())
            {
                FederatedGuildLink link = FederatedGuildStore.ById(linkId);
                if (link == null || !link.MayRead || !link.FederationActive)
                {
                    int dropped = FederationOutbox.DropFor(linkId);
                    if (dropped > 0) ServerLog.Verbose($"Federation: dropped {dropped} report(s) for a link that is no longer a member.");
                    continue;
                }
                // Held until re-proof: Global refuses it, and stopping here would hold back every other guild.
                if (link.State == FederatedLinkState.ReproofRequired) continue;

                for (int pass = 0; pass < 25; pass++)
                {
                    List<FederationOutbox.Entry> batch = FederationOutbox.TakeBatch(linkId, BatchSize);
                    if (batch.Count == 0) break;

                    FederationContributeBody body = new FederationContributeBody
                    {
                        ServerId = ServerId(), Generation = Generation(), IssuedAtUnix = NowUnix(), Nonce = Nonce(),
                        LinkId = linkId, BatchId = Guid.NewGuid().ToString("N"),
                    };
                    // Older builds may have queued a player on guild work; the signed layout keeps the field.
                    foreach (FederationOutbox.Entry e in batch)
                        body.Entries.Add(new FederationContributionEntry
                        {
                            ReportId = e.ReportId, Category = e.Category, Amount = e.Amount,
                            Player = FederationRules.NeedsPlayer(e.Category) ? e.Player ?? "" : "", OccurredAtUnix = e.OccurredAtUnix,
                        });
                    body.Signature = KmhServerKey.Sign(body.Canonical());
                    if (string.IsNullOrEmpty(body.Signature)) return;

                    GlobalCallResult r = await SendAsync(ContributePath, body, ct).ConfigureAwait(false);
                    if (r == null || r.Unreachable) return;
                    if (r.Ok)
                    {
                        // Counted and already-known are one outcome (same report id); what Global refused (over a week old,
                        // unknown player) is gone for good, so it is counted.
                        FederationOutbox.Remove(batch);
                        int rejected = (int)Math.Min(int.MaxValue, Math.Max(0, LongOf(r.Body, "rejected")));
                        if (rejected > 0)
                        {
                            FederationOutbox.NoteRejected(rejected);
                            ServerLog.Warn($"Federation: Global did not count {rejected} of {batch.Count} report(s) - older than 7 days or from a player it cannot verify.");
                        }
                        long rev = LongOf(r.Body, "revision");
                        if (rev > 0 && link.View?.Federation != null && rev != link.View.Federation.Revision) _revisionMoved = true;
                        continue;
                    }
                    if (r.ErrorCode == "KMH-FED-915" || r.ErrorCode == "KMH-FED-906" || r.ErrorCode == "KMH-GLD-958")
                    {
                        FederationOutbox.DropFor(linkId);
                        await RefreshViewAsync(linkId, ct).ConfigureAwait(false);
                        break;
                    }
                    if (r.ErrorCode == "KMH-FED-916")
                    {
                        // An unreadable batch never becomes readable; keeping it would block the queue forever.
                        FederationOutbox.Remove(batch, unreadable: true);
                        ServerLog.Warn($"Federation: Global refused {batch.Count} report(s) as malformed; they were dropped.");
                        continue;
                    }
                    // This link's problem alone: the next link still flushes.
                    if (r.ErrorCode == "KMH-GLD-959")
                    {
                        FederatedGuildStore.SetState(linkId, FederatedLinkState.ReproofRequired, FederatedLinkDetail.ReproofRequired);
                        break;
                    }
                    // Anything else is kept and retried after the next beat without holding back other guilds.
                    ServerLog.Verbose($"Federation: contributions for link {linkId} refused ({r.ErrorCode}); kept and retried later.");
                    break;
                }
            }
        }


        // Never throws: a hook in another store must not fail its own work over this.
        public static bool Record(string category, string guild, string playerGlobalId, int amount = 1)
        {
            try
            {
                FeaturesConfig f = FeaturesConfig.Current;
                if (!f.GuildFederation || !f.Guilds || !FederationConfig.Current.ReportContributions) return false;
                if (!FederationRules.IsCategory(category) || amount <= 0 || string.IsNullOrEmpty(guild)) return false;
                FederatedGuildLink link = LinkForGuild(guild);
                if (link == null || link.State != FederatedLinkState.Active || !link.FederationActive) return false;
                // Only a proven identity counts for the federation.
                if (FederationRules.NeedsPlayer(category) && string.IsNullOrEmpty(playerGlobalId)) return false;

                return FederationOutbox.Add(new FederationOutbox.Entry
                {
                    LinkId = link.LinkId, Category = category, Amount = Math.Min(amount, 50),
                    // Guild work credits nobody, and Global discards a name it was never asked to count.
                    Player = FederationRules.NeedsPlayer(category) ? playerGlobalId : "",
                    OccurredAtUnix = NowUnix(),
                });
            }
            catch (Exception ex)
            {
                ServerLog.Verbose($"Federation: could not record {category} for '{guild}' ({ex.GetType().Name}).");
                return false;
            }
        }

        public static bool RecordForPlayer(string category, string username, int amount = 1)
        {
            if (string.IsNullOrEmpty(username)) return false;
            string guild = GuildStore.CurrentGuildOf(username);
            string id = KmhKnownIdentities.IdFor(username);
            return !string.IsNullOrEmpty(guild) && Record(category, guild, id, amount);
        }

        public static bool RecordGuildWork(string category, string username, int amount = 1)
        {
            if (string.IsNullOrEmpty(username) || amount <= 0) return false;
            string guild = GuildStore.CurrentGuildOf(username);
            return !string.IsNullOrEmpty(guild) && Record(category, guild, KmhKnownIdentities.IdFor(username), amount);
        }

        // Not credited when the approver is the same Global identity: one person paying themselves through an alt.
        public static bool RecordQuestCompletion(string claimer, string poster)
        {
            if (string.IsNullOrEmpty(claimer)) return false;
            string claimerId = KmhKnownIdentities.IdFor(claimer);
            string posterId = KmhKnownIdentities.IdFor(poster);
            // An unverified poster has no id to compare, so an alt that never signs in would always pass.
            if (string.IsNullOrEmpty(claimerId) || string.IsNullOrEmpty(posterId)) return false;
            if (string.Equals(claimerId, posterId, StringComparison.Ordinal)) return false;
            string guild = GuildStore.CurrentGuildOf(claimer);
            return !string.IsNullOrEmpty(guild) && Record("quest", guild, claimerId);
        }

        public static void OnVerifiedJoin(string username, string globalId)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(globalId)) return;
            string guild = GuildStore.CurrentGuildOf(username);
            FederatedGuildLink link = LinkForGuild(guild);
            if (link == null || link.State != FederatedLinkState.Active) return;
            long day = NowUnix() / 86400;
            if (FederationOutbox.FirstActivityToday(guild, globalId, day)) Record("activity", guild, globalId);
        }

        // The local guild is gone, so its link ends here at once; Global hears when it can.
        public static void OnGuildDisbanded(string guild)
        {
            FederatedGuildLink ended = FederatedGuildStore.EndLocal(guild, FederatedLinkState.Left, FederatedLinkDetail.GuildDisbanded);
            if (ended == null) return;
            FederationOutbox.DropFor(ended.LinkId);
            if (!FederationOutbox.QueueLeave(ended.LinkId, "guild_disbanded"))
                ServerLog.Warn($"Federation: '{guild}' left its federation here, but the notice for Global could not be written down.");
        }


        private static FederationResult Remote(GlobalCallResult r)
        {
            if (r != null && r.Status == 404 && string.IsNullOrEmpty(r.ErrorCode)) { _unsupported = true; return FederationResult.Fail("global.unsupported"); }
            if (r == null) return FederationResult.Fail("global.unreachable");
            if (r.Unreachable) return FederationResult.Fail("global.unreachable");
            return FederationResult.Fail(string.IsNullOrEmpty(r.ErrorCode) ? $"http.{r.Status}" : r.ErrorCode);
        }

        internal static bool IsServerId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 32) return false;
            foreach (char c in id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static string ValueOf(JObject body, string key)
        {
            JToken t = body?[key];
            return t == null || t.Type == JTokenType.Null ? "" : t.ToString();
        }

        private static long LongOf(JObject body, string key)
        {
            JToken t = body?[key];
            if (t == null || t.Type == JTokenType.Null) return 0;
            try { return (long)t; } catch { return 0; }
        }

        internal static void ResetForTest()
        {
            _unsupported = false; _heartbeatRevision = -1; _revisionMoved = false;
            lock (_movedLock) _movedLinks.Clear();
        }
    }
}
