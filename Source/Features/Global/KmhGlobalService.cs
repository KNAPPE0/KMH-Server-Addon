using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    // Best-effort by design: a Global outage costs a listing, never a running game server.
    internal static class KmhGlobalService
    {
        private static CancellationTokenSource _cts;
        private static Task _loop;

        public static bool Running => _loop != null && !_loop.IsCompleted;

        // Said once at boot, not per heartbeat: a misconfigured directory is a setup problem.
        private static void ReportFindings(GlobalConfig cfg, bool registered)
        {
            foreach (GlobalFinding f in GlobalConfigCheck.Inspect(cfg, registered))
            {
                if (f.Level == GlobalFindingLevel.Blocker) ServerLog.Warn($"Global: {f}");
                else ServerLog.Info($"Global: {f}");
            }
        }

        // Private owner-less identity: heartbeats only, never a listing.
        public static bool BackendMode { get; private set; }

        public static void Start()
        {
            // Started first: a pending identity change may be what lets the rest run.
            KmhIdentityChange.Start();
            GlobalConfig cfg = GlobalConfig.Current;
            BackendMode = false;
            if (!cfg.Enabled)
            {
                KmhGlobalRegistrar.Reset();
                if (!KmhGlobalBackend.Allowed)
                {
                    ServerLog.Info("Global: not enabled - this server will not appear in the Colony Nexus browser. "
                                 + "Run 'kmh global setup' to join it.");
                    return;
                }
                StartBackend(cfg);
                return;
            }

            if (string.IsNullOrEmpty(KmhServerIdentity.Id))
            {
                ServerLog.Warn("Global: this server has no instance id yet, so registration is skipped this boot.");
                return;
            }

            GlobalSetupState setup = GlobalSetupState.Current_;
            ReportFindings(cfg, setup.IsRegistered);

            // Pre-registration heartbeats are only ever rejected, so the loop waits for setup.
            if (!setup.IsRegistered)
            {
                KmhGlobalRegistrar.Reset();

                // A link the owner is about to approve finishes on its own, including across a restart.
                if (KmhGlobalSetupCompleter.ShouldWaitFor(setup, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                {
                    KmhGlobalSetupCompleter.Start();
                    return;
                }

                ServerLog.Info(setup.HasDeferred
                    ? "Global: setup was deferred, so this server is not listed. Run 'kmh global setup' to finish it."
                    : $"Global: setup is at '{setup.Current}' - not registered yet, so no heartbeat is sent.");
                return;
            }

            if (!string.Equals(cfg.RegisteredServerId, KmhServerIdentity.Id, StringComparison.Ordinal))
            {
                // A KMH-Data folder copied between servers would otherwise heartbeat under the wrong identity.
                ServerLog.Warn($"Global: this server's id ({KmhServerIdentity.Id}) does not match the registered one "
                             + $"({cfg.RegisteredServerId}). Not sending heartbeats. If this server was moved or restored, "
                             + "KMH-Data/.kmh-meta.json (it holds the server id) is missing or from another server - restore it "
                             + "from KMH-Data-Backups. Run 'kmh global setup' only for a genuinely new server.");
                return;
            }

            if (!KmhGlobalSafety.MayPublishTo(cfg.BaseUrl, out string blocked))
            {
                ServerLog.Warn($"Global: {blocked}");
                return;
            }

            StartLoop(cfg);
        }

        private static void StartBackend(GlobalConfig cfg)
        {
            if (string.IsNullOrEmpty(KmhServerIdentity.Id))
            {
                ServerLog.Warn("Global: this server has no instance id yet, so its private identity waits for the next boot.");
                return;
            }
            if (!KmhGlobalSafety.MayPublishTo(cfg.BaseUrl, out string blocked))
            {
                ServerLog.Warn($"Global: {blocked}");
                return;
            }

            GlobalSetupState setup = GlobalSetupState.Current_;
            // An owner may be mid-way through linking this identity; the completer finishes that on its own.
            if (KmhGlobalSetupCompleter.ShouldWaitFor(setup, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                KmhGlobalSetupCompleter.Start();

            if (!setup.BackendRegistered)
            {
                ServerLog.Info("Global: Guild Federation needs a Colony Nexus Global identity - registering one privately. "
                             + "This server is not listed; 'kmh setup info' shows what is sent, 'kmh global disable' stops it.");
                KmhGlobalBackend.StartRegistration();
                return;
            }

            if (!string.Equals(cfg.RegisteredServerId, KmhServerIdentity.Id, StringComparison.Ordinal))
            {
                ServerLog.Warn($"Global: this server's id ({KmhServerIdentity.Id}) does not match its private identity "
                             + $"({cfg.RegisteredServerId}). Not sending heartbeats.");
                return;
            }

            BackendMode = true;
            StartLoop(cfg);
        }

        private static readonly object _gate = new object();

        // A second start replaces the running loop and begins only once that loop has finished its beat, so two never
        // sign heartbeats at once; the token is captured here, never read back from the field.
        private static void StartLoop(GlobalConfig cfg)
        {
            long resume = cfg.LastSequence;
            lock (_gate)
            {
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                CancellationTokenSource cts = new CancellationTokenSource();
                _cts = cts;
                Task previous = _loop ?? Task.CompletedTask;
                _loop = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch (Exception) { }
                    if (cts.IsCancellationRequested) return;
                    // Resumed from disk: a fresh sequence after a reboot would read as a replay.
                    KmhGlobalRegistrar.ResumeSequence(resume);
                    await LoopAsync(cts.Token).ConfigureAwait(false);
                });
            }
            ServerLog.Info($"Global: heartbeat started for {KmhServerIdentity.Id} "
                         + $"(generation {cfg.SigningGeneration}, resuming after sequence {cfg.LastSequence})"
                         + (BackendMode ? " - private identity, not listed." : "."));
        }

        // The loop task is kept after a stop, so a restart waits for it and shutdown can.
        public static void Stop()
        {
            KmhGlobalSetupCompleter.Stop();
            KmhIdentityChange.Stop();
            KmhGlobalBackend.StopRegistration();
            lock (_gate)
            {
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                _cts = null;
            }
        }

        // A beat still in flight would persist its sequence after the final flush, so shutdown waits for it.
        public static bool StopAndWait(TimeSpan timeout)
        {
            Stop();
            Task loop;
            lock (_gate) loop = _loop;
            if (loop == null) return true;
            try { return loop.Wait(timeout); }
            catch (AggregateException) { return true; }
        }

        internal static MetadataBody BuildMetadata(GlobalConfig cfg)
        {
            return new MetadataBody
            {
                ServerId     = KmhServerIdentity.Id,
                IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Nonce        = Guid.NewGuid().ToString("N"),
                Name         = KmhServerIdentity.Name ?? "",
                Description  = cfg.Listing.Description ?? "",
                Region       = cfg.Listing.ResolvedRegion,
                Languages    = cfg.Listing.Languages ?? new string[0],
                Tags         = ServerTags.Normalize(cfg.Listing.Tags),
                // Global refuses the whole push over one link that is not https, so such a link is left off instead.
                DiscordUrl   = ListingLimits.IsHttpsUrl(cfg.Listing.DiscordUrl) ? cfg.Listing.DiscordUrl ?? "" : "",
                WebsiteUrl   = ListingLimits.IsHttpsUrl(cfg.Listing.WebsiteUrl) ? cfg.Listing.WebsiteUrl ?? "" : "",
                Visibility   = cfg.Visibility ?? "",
            };
        }

        // Tracks the last published content so an owner's change goes out now, not at the next restart.
        private static string _publishedMetadata;
        internal static bool MetadataDue() => BuildMetadata(GlobalConfig.Current).ContentKey() != _publishedMetadata;
        internal static string PublishedMetadataForTest { get => _publishedMetadata; set => _publishedMetadata = value; }
        internal static string CurrentMetadataKeyForTest() => BuildMetadata(GlobalConfig.Current).ContentKey();

        private static async Task<bool> PushMetadataAsync(KmhGlobalClient client, CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            MetadataBody body = BuildMetadata(cfg);
            string key = body.ContentKey();
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return false;

            GlobalCallResult r = await client.PatchAsync($"/api/v1/servers/{KmhServerIdentity.Id}/metadata", body, null, ct)
                                             .ConfigureAwait(false);
            if (!r.Ok)
            {
                // A moderation refusal is final until the owner changes something, so it is not retried.
                if (ReportModerationRefusal("listing", r)) { _publishedMetadata = key; return true; }
                ServerLog.Verbose($"Global: metadata push refused ({r.ErrorCode}).");
                return false;
            }
            _publishedMetadata = key;
            if (ReportHeld("listing", r)) return true;

            long revision = (long?)r.Body?["metadata_revision"] ?? 0;
            if (revision > cfg.LastMetadataRevision) { cfg.LastMetadataRevision = revision; cfg.SaveState(); }
            ServerLog.Info($"Global: listing details published (revision {revision}).");
            return true;
        }

        // KMH-MOD-801 content, 802 locked edits, 803/804 bans; name the field, never the matched words.
        internal static bool ReportModerationRefusal(string what, GlobalCallResult r)
        {
            if (r.ErrorCode == null || !r.ErrorCode.StartsWith("KMH-MOD-8", StringComparison.Ordinal)) return false;
            string message = r.Args != null && r.Args.TryGetValue("message", out string m) && !string.IsNullOrWhiteSpace(m)
                ? m : "Colony Nexus moderation refused the change.";
            ServerLog.Warn($"Global: {what} change not published. {message} (ref {r.CorrelationId})");
            return true;
        }

        internal static bool ReportHeld(string what, GlobalCallResult r)
        {
            if ((string)r.Body?["review"] != "held") return false;
            string message = (string)r.Body?["message"] ?? "";
            ServerLog.Info($"Global: {what} change is waiting for a Colony Nexus moderator; the live listing is unchanged until then. {message}");
            return true;
        }

        private static string _publishedDetails;

        // An older Global answers 404 and is not asked again.
        internal static async Task PushListingDetailsAsync(KmhGlobalClient client, CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            ListingDetailsBody body = ListingLimits.Build(cfg);
            string key = body.ContentKey();
            if (key == _publishedDetails) return;

            body.ServerId     = KmhServerIdentity.Id;
            body.IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            body.Nonce        = Guid.NewGuid().ToString("N");
            body.Generation   = cfg.SigningGeneration;
            body.Signature    = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return;

            GlobalCallResult r = await client.PutAsync($"/api/v1/servers/{KmhServerIdentity.Id}/details", body, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                _publishedDetails = key;
                if (!ReportHeld("listing details", r))
                    ServerLog.Info($"Global: listing details published (revision {(long?)r.Body?["details_revision"] ?? 0}).");
                return;
            }
            if (r.Status == 404 || r.Status == 405 || ReportModerationRefusal("listing details", r)) { _publishedDetails = key; return; }
            if (r.Status == 400)
            {
                _publishedDetails = key;
                string field = r.Args != null && r.Args.TryGetValue("field", out string f) ? f : "a field";
                ServerLog.Warn($"Global: listing details not published: Global refused {field}. Check it in KMH-Data/Config/Global.json.");
                return;
            }
            ServerLog.Verbose($"Global: listing details push refused ({r.ErrorCode}); retrying later.");
        }

        internal static void ForgetPublishedDetailsForTest() => _publishedDetails = null;

        // Sent only when the revision moves: republishing every beat would be the browser's largest traffic source.
        private static long _refusedProfileRevision = -1;

        private static async Task<long> PushModProfileAsync(KmhGlobalClient client, long publishedRevision,
                                                            CancellationToken ct)
        {
            Mods.ModProfileSnapshot snap = Mods.ModProfile.Current;
            // Unknown means RWT has not read its own mod config yet; publishing that would claim a policy.
            if (snap.Policy == Mods.ModPolicy.Unknown || snap.Revision == publishedRevision) return publishedRevision;

            ModProfileBody body = new ModProfileBody
            {
                ServerId        = KmhServerIdentity.Id,
                IssuedAtUnix    = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Nonce           = Guid.NewGuid().ToString("N"),
                Policy          = snap.PolicyName,
                EnforceSettings = snap.EnforceSettings,
                EnforceOrder    = snap.EnforceOrder,
                Required        = snap.Required.ToArray(),
                Optional        = snap.Optional.ToArray(),
                Order           = snap.Order.ToArray(),
                OrderHash       = snap.OrderHash,
                Hash            = snap.Hash,
                Revision        = snap.Revision,
                Entries         = snap.Entries.ToArray(),
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return publishedRevision;

            GlobalCallResult r = await client.PostAsync($"/api/v1/servers/{KmhServerIdentity.Id}/mod-profile", body, null, ct)
                                             .ConfigureAwait(false);
            return OnModProfileAnswer(snap, publishedRevision, r);
        }

        internal static long OnModProfileAnswer(Mods.ModProfileSnapshot snap, long publishedRevision, GlobalCallResult r)
        {
            if (!r.Ok)
            {
                // Retried every beat; said once per mod list so a refused list is not silent.
                if (!r.Unreachable && snap.Revision != _refusedProfileRevision)
                {
                    _refusedProfileRevision = snap.Revision;
                    string why = string.IsNullOrEmpty(r.ErrorCode) ? r.Status.ToString() : r.Status + ", " + r.ErrorCode;
                    ServerLog.Warn($"Global: Colony Nexus did not accept this server's mod list (HTTP {why}). "
                                 + "Players see its mods as unknown or out of date until it does; it is retried with every heartbeat.");
                }
                else ServerLog.Verbose($"Global: mod profile push refused ({r.ErrorCode}).");
                return publishedRevision;
            }

            // A 200 with stored=false means Global kept a higher revision, as after a reinstall: nothing was published.
            if ((bool?)r.Body?["stored"] == false && (long?)r.Body["revision"] is long kept)
            {
                long sent = snap.Revision;
                long next = Mods.ModProfile.ClimbPast(kept);
                ServerLog.Info($"Global: Colony Nexus still lists an earlier mod list for this server under revision {kept}, "
                             + $"above this one ({sent}). Publishing this server's list again as revision {next}.");
                return publishedRevision;
            }

            ServerLog.Info($"Global: mod profile published (revision {snap.Revision}, {snap.PolicyName}, "
                         + $"{snap.Required.Count} required, {snap.Optional.Count} optional).");
            return snap.Revision;
        }

        // At most every 5 minutes, since active time changes each minute; MayPublish enforces the owner's switch.
        internal static readonly TimeSpan LeaderboardsInterval = TimeSpan.FromMinutes(5);
        private static string _publishedBoards;
        private static DateTime _boardsSentAt;

        internal static LeaderboardPublishBody BuildLeaderboards(List<Leaderboards.LeaderboardBoardDto> boards, int season, long seasonStartedUnix, long generatedUnix)
        {
            var body = new LeaderboardPublishBody { Season = season, SeasonStartedUnix = seasonStartedUnix, GeneratedUnix = generatedUnix };
            foreach (Leaderboards.LeaderboardBoardDto b in boards)
            {
                if (b == null || body.Boards.Count >= LeaderboardPublishBody.MaxBoards) continue;
                var pb = new LeaderboardPublishBoard
                {
                    Key = b.Key, Title = b.Title, Group = b.Group ?? "", Unit = b.Unit, Scope = b.Scope, Source = b.Source,
                    Description = b.Description ?? "", Total = b.Total,
                };
                foreach (Leaderboards.LeaderboardRowDto r in b.Rows)
                {
                    if (pb.Rows.Count >= LeaderboardPublishBody.MaxRows) break;
                    pb.Rows.Add(new LeaderboardPublishRow { Rank = r.Rank, Name = Clip(r.Name), Guild = Clip(r.Guild), Colony = Clip(r.Colony),
                                                           Value = r.Value, Display = r.Display ?? "", Online = r.Online });
                }
                body.Boards.Add(pb);
            }
            return body;
        }

        // Fit to Global's limits (64 chars, no control or line breaks) instead of having the whole publish refused.
        internal static string Clip(string s)
        {
            bool had = !string.IsNullOrEmpty(s);
            s = s ?? "";
            var cleaned = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsControl(c) && c != (char)0x2028 && c != (char)0x2029) cleaned.Append(c);
            s = cleaned.ToString().Trim();
            if (s.Length == 0) return had ? "(unnamed)" : "";
            return s.Length <= 64 ? s : s.Substring(0, char.IsHighSurrogate(s[63]) ? 63 : 64);
        }

        // Everything but the moment it was built and the signature: two identical rankings are one publication.
        internal static string BoardsKey(LeaderboardPublishBody b)
            => KmhCanonicalPayload.HashSet(new[] { Newtonsoft.Json.JsonConvert.SerializeObject(b.Boards), b.Season.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        private static async Task PushLeaderboardsAsync(KmhGlobalClient client, CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            if (cfg == null || !cfg.PublishLeaderboards || BackendMode) return;
            if (DateTime.UtcNow - _boardsSentAt < LeaderboardsInterval) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            LeaderboardPublishBody body = BuildLeaderboards(Leaderboards.LeaderboardEngine.BuildAll(), Seasons.SeasonStore.CurrentSeason,
                Seasons.SeasonStore.SeasonStartedTicks > 0 ? new DateTimeOffset(Seasons.SeasonStore.SeasonStartedTicks, TimeSpan.Zero).ToUnixTimeSeconds() : 0,
                now.ToUnixTimeSeconds());
            string key = BoardsKey(body);
            if (key == _publishedBoards) return;

            body.ServerId     = KmhServerIdentity.Id;
            body.IssuedAtUnix = now.ToUnixTimeSeconds();
            body.Nonce        = Guid.NewGuid().ToString("N");
            body.Generation   = cfg.SigningGeneration;
            body.Signature    = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) return;

            _boardsSentAt = DateTime.UtcNow;
            GlobalCallResult r = await client.PostAsync($"/api/v1/servers/{KmhServerIdentity.Id}/leaderboards", body, null, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                _publishedBoards = key;
                _boardsPage = PageFrom(cfg.BaseUrl, r.Body);
                ServerLog.Info($"Global: leaderboards published ({body.Boards.Count} boards)" + (_boardsPage.Length > 0 ? $", shown at {_boardsPage}." : ", not shown on the website while this server is not listed."));
                return;
            }
            // A refusal is said once per ranking; the next change tries again.
            if (!r.Unreachable) { _publishedBoards = key; _boardsPage = ""; ServerLog.Warn($"Global: Colony Nexus did not accept this server's leaderboards (HTTP {r.Status}, {r.ErrorCode})."); }
        }

        private static string _boardsPage = "";
        private static readonly System.Text.RegularExpressions.Regex BoardsPagePath =
            new System.Text.RegularExpressions.Regex(@"^/servers/[A-Za-z0-9-]{1,64}/leaderboards$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Taken only as a path on Global's own site, so an answer can never point a Discord link elsewhere.
        internal static string PageFrom(string baseUrl, Newtonsoft.Json.Linq.JObject body)
        {
            string page = body?["page"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)body["page"] : "";
            if (string.IsNullOrEmpty(page) || !BoardsPagePath.IsMatch(page)) return "";
            if (!Uri.TryCreate(baseUrl ?? "", UriKind.Absolute, out Uri root) || (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp)) return "";
            return root.GetLeftPart(UriPartial.Path).TrimEnd('/') + page;
        }

        internal static string LeaderboardPageFor(string board) => PageFor(GlobalConfig.Current, BackendMode, _boardsPage, board);

        internal static string PageFor(GlobalConfig cfg, bool backend, string page, string board)
        {
            if (cfg == null || !cfg.Enabled || !cfg.PublishLeaderboards || backend || string.IsNullOrEmpty(page)) return "";
            return string.IsNullOrEmpty(board) ? page : page + "/" + Uri.EscapeDataString(board);
        }

        internal static void ForgetPublishedBoardsForTest() { _publishedBoards = null; _boardsSentAt = default; _boardsPage = ""; }

        // The loop starts before RWT reads its configs; a first beat then would publish no port and no player cap.
        private static async Task WaitForRwtConfigAsync(CancellationToken ct)
        {
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                try { if (Master.ServerConfig != null) return; } catch { }
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        private static async Task LoopAsync(CancellationToken ct)
        {
            KmhGlobalClient client = new KmhGlobalClient(GlobalConfig.Current.BaseUrl);
            bool listingPublished = false;
            long modProfileRevision = -1;

            try { await WaitForRwtConfigAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                int waitSeconds;
                try
                {
                    waitSeconds = await BeatOnceAsync(client, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // A bug in this loop must not take the loop down; the listing simply goes stale.
                    ServerLog.Verbose($"Global: heartbeat iteration failed: {ex.Message}");
                    waitSeconds = KmhGlobalRegistrar.NextRetrySeconds;
                }

                // Fetched once after a working beat: without it no join ticket verifies, so network bans cannot be enforced.
                if (KmhGlobalRegistrar.LastSuccessUtc != default && !KmhDirectoryKey.Known)
                {
                    try { await KmhDirectoryKey.EnsureAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Global: directory key fetch failed: {ex.Message}"); }
                }

                // A good beat proves Global is back, so crash- or outage-stranded federated silver resumes here.
                if (KmhGlobalRegistrar.LastSuccessUtc != default)
                {
                    try { await Treasury.ConnectedTreasuryService.ReconcileAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Connected treasury: reconcile failed: {ex.Message}"); }
                }

                // Queued Federation reports and leaves go out on the same proof, then the cached view catches up.
                if (KmhGlobalRegistrar.LastSuccessUtc != default)
                {
                    try { await Federation.FederationService.AfterHeartbeatAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Federation: catch-up failed: {ex.Message}"); }
                }

                // A private identity publishes no listing and no mod list: it exists for Federation only.
                if (BackendMode)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                // Only after a first accepted beat: earlier, RWT's name and player cap are still fallbacks.
                if (KmhGlobalRegistrar.LastSuccessUtc != default && MetadataDue())
                {
                    try { if (await PushMetadataAsync(client, ct).ConfigureAwait(false)) listingPublished = true; }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Global: listing push failed: {ex.Message}"); }
                }

                if (listingPublished)
                {
                    try { await PushListingDetailsAsync(client, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Global: listing details push failed: {ex.Message}"); }
                }

                if (KmhGlobalRegistrar.LastSuccessUtc != default)
                {
                    try { modProfileRevision = await PushModProfileAsync(client, modProfileRevision, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Global: mod profile push failed: {ex.Message}"); }

                    try { await PushLeaderboardsAsync(client, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { ServerLog.Verbose($"Global: leaderboards push failed: {ex.Message}"); }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private static async Task<int> BeatOnceAsync(KmhGlobalClient client, CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            HeartbeatBody body = KmhGlobalRegistrar.BuildHeartbeat(DateTimeOffset.UtcNow);
            if (string.IsNullOrEmpty(body.Signature))
            {
                ServerLog.Warn("Global: this server could not sign its heartbeat, so it will not be listed.");
                return KmhGlobalRegistrar.NextRetrySeconds;
            }

            GlobalCallResult result = await client.PostAsync("/api/v1/servers/heartbeat", body, null, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                KmhGlobalRegistrar.NoteSuccess();
                Persist(cfg, body.Sequence);
                // A request rides the response; answering it is this server's decision, not Global's.
                KmhDiagnosticsResponder.OnHeartbeatResponse((string)result.Body?["diagnostics_request"]);
                KmhNetworkBans.OnHeartbeatResponse(result.Body?["banned_accounts"]);
                NoteFederationRevisions(result.Body);
                KmhModerationNotice.OnHeartbeatResponse(result.Body?["moderation"]);

                int consoleWait = await KmhOperatorConsole.OnHeartbeatResponseAsync(
                    client,
                    (string)result.Body?["console_session"],
                    (long?)result.Body?["console_until"] ?? 0,
                    (long?)result.Body?["console_cursor"] ?? 0,
                    ct).ConfigureAwait(false);

                return consoleWait > 0 ? consoleWait : Math.Max(30, cfg.HeartbeatSeconds);
            }

            return OnBeatRefused(result);
        }

        internal static int OnBeatRefused(GlobalCallResult result)
        {
            KmhGlobalRegistrar.NoteFailure(result);

            // The expected end of an identity change in flight: the key verified at the newer generation.
            if (KmhIdentityChange.TryAdoptFromRefusal(result)) return 5;
            // The old key stopped working while a change was in flight: find out whether Global took the new one.
            if (result.ErrorCode == "KMH-SRV-303" && KmhIdentityChange.Pending?.Stage == IdentityChangeStage.Submitted)
                KmhIdentityChange.Start();

            // Banned by a moderator: retrying changes nothing, so back off and say why once.
            if (result.ErrorCode == "KMH-MOD-803")
            {
                KmhModerationNotice.OnBanned();
                return 900;
            }

            // An identity that is behind needs an owner action, not a retry, so back right off.
            if (result.ErrorCode == "KMH-SRV-317" || result.ErrorCode == "KMH-SRV-316")
            {
                ServerLog.Warn("Global: " + KmhGlobalRegistrar.OwnerDiagnostic());
                return 900;
            }

            // Global lost this private identity (a restored database); nobody else re-registers it, so this server does.
            if (BackendMode && result.ErrorCode == "KMH-SRV-301")
            {
                GlobalSetupState state = GlobalSetupState.Current_;
                state.BackendRegistered = false;
                if (!state.Save()) ServerLog.Warn("Global: could not record that the private identity must be registered again.");
                ServerLog.Warn("Global: Colony Nexus Global no longer knows this server's private identity - registering it again.");
                KmhGlobalBackend.StartRegistration();
                return KmhGlobalRegistrar.NextRetrySeconds;
            }

            ServerLog.Verbose($"Global: heartbeat refused ({result.ErrorCode}); retrying in {KmhGlobalRegistrar.NextRetrySeconds}s.");
            return KmhGlobalRegistrar.NextRetrySeconds;
        }

        // The map names each federation that moved; an older Global sends only the highest revision.
        internal static void NoteFederationRevisions(JObject body)
        {
            if (body?["federation_revisions"] is JObject perFederation)
                Federation.FederationService.OnHeartbeatRevisions(RevisionsOf(perFederation));
            else
                Federation.FederationService.OnHeartbeatRevision((long?)body?["federation_revision"] ?? -1);
        }

        internal static Dictionary<string, long> RevisionsOf(JObject perFederation)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (JProperty p in perFederation.Properties())
                if (p.Value.Type == JTokenType.Integer) map[p.Name] = (long)p.Value;
            return map;
        }

        internal static async Task<bool> BeatOnceForLiveCheckAsync(CancellationToken ct)
        {
            KmhGlobalRegistrar.ResumeSequence(GlobalConfig.Current.LastSequence);
            DateTime before = KmhGlobalRegistrar.LastSuccessUtc;
            await BeatOnceAsync(new KmhGlobalClient(GlobalConfig.Current.BaseUrl), ct).ConfigureAwait(false);
            if (KmhGlobalRegistrar.LastSuccessUtc == before) return false;
            await Federation.FederationService.AfterHeartbeatAsync(ct).ConfigureAwait(false);
            return true;
        }

        internal static void SetBackendModeForTest(bool on) => BackendMode = on;

        // Written after Global accepted it, so a crash mid-send never advances the number past what was agreed.
        private static void Persist(GlobalConfig cfg, long acceptedSequence)
        {
            try
            {
                if (cfg.LastSequence >= acceptedSequence) return;
                cfg.LastSequence = acceptedSequence;
                cfg.SaveState();
            }
            catch (Exception ex) { ServerLog.Verbose($"Global: could not record the heartbeat sequence: {ex.Message}"); }
        }
    }
}
