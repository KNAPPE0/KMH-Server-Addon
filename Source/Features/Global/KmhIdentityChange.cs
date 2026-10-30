using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global
{
    internal static class IdentityChangeKind
    {
        public const string Rotate  = "Rotate";
        public const string Recover = "Recover";
        public const string Rebind  = "Rebind";
    }

    internal static class IdentityChangeStage
    {
        // Heartbeats carry on with the current key meanwhile.
        public const string AwaitingApproval = "AwaitingApproval";
        // Global was asked and the answer was never seen.
        public const string Submitted        = "Submitted";
        public const string AwaitingOperator = "AwaitingOperator";
    }

    // Durable so a restart resumes rather than strands a key Global may already hold. The grant stays in memory only.
    internal sealed class IdentityChangeState
    {
        public int    SchemaVersion { get; set; } = 1;
        public string Kind { get; set; } = "";
        public string Stage { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public int    FromGeneration { get; set; }
        public string FromFingerprint { get; set; } = "";
        public string ToFingerprint { get; set; } = "";
        public int    ExpectedGeneration { get; set; }
        public string PendingLinkId { get; set; } = "";
        public long   PendingLinkExpiresUnix { get; set; }
        public string RebindId { get; set; } = "";
        // Worthless once proved: any other key presenting it is refused. Kept beside the key it proved.
        public string RebindCode { get; set; } = "";
        public long   RebindExpiresUnix { get; set; }
        public string LastErrorCode { get; set; } = "";
        public string LastDetail { get; set; } = "";
    }

    // Rotate, recover and rebind never change this server's id.
    internal static class KmhIdentityChange
    {
        public const string OwnerLinksPath = "/api/v1/owner-links";
        public const string ChallengePath  = "/api/v1/servers/registration/challenge";
        public const string ProvePath      = "/api/v1/servers/rebind/prove";
        public const string HeartbeatPath  = "/api/v1/servers/heartbeat";

        public static string RotatePath(string serverId)  => $"/api/v1/servers/{serverId}/rotate-key";
        public static string RecoverPath(string serverId) => $"/api/v1/servers/{serverId}/recover-identity";

        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan RebindPollInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

        private static string StatePath => Path.Combine(KmhDataPaths.IdentityDir, "identity-change.json");

        // Self-test seams; nothing else assigns them.
        internal static Func<string, object, string, CancellationToken, Task<GlobalCallResult>> SenderForTest;
        internal static Action RestartForTest;

        private static Task<GlobalCallResult> SendAsync(string path, object body, string bearer, CancellationToken ct)
            => SenderForTest != null ? SenderForTest(path, body, bearer, ct)
                                     : new KmhGlobalClient(GlobalConfig.Current.BaseUrl).PostAsync(path, body, bearer, ct);

        // The completer and the console both drive the same change; one at a time.
        internal static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static string _grant = "";
        private static CancellationTokenSource _cts;
        private static Task _loop;

        public static bool Running => _loop != null && !_loop.IsCompleted;

        public static IdentityChangeState Pending
            => JsonFileStore.TryLoad(StatePath, out IdentityChangeState s) && s != null && s.Kind.Length > 0 ? s : null;

        private static bool Save(IdentityChangeState s) => JsonFileStore.Save(StatePath, s);

        private static void Clear()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); }
            catch (Exception ex) { ServerLog.Warn($"Global: could not clear the finished identity change: {ex.Message}"); }
        }

        internal static void ForgetGrantForTest() => _grant = "";

        // The current key keeps working until the owner approves.
        public static async Task<string> BeginAsync(string kind, CancellationToken ct)
        {
            GlobalSetupState setup = GlobalSetupState.Current_;
            GlobalConfig cfg = GlobalConfig.Current;
            if (string.IsNullOrEmpty(KmhServerIdentity.Id)) return "This server has no instance id yet.";
            if (!setup.IsOwnerRegistered)
                return setup.BackendRegistered
                    ? (kind == IdentityChangeKind.Rotate
                        ? "This server's identity has no owner yet, and only an owner can rotate its key. Run 'kmh global setup' "
                          + "first - it keeps this server id and key - then rotate."
                        : "This server's identity has no owner, so no owner can recover it. Ask the Colony Nexus Global "
                          + "operator for a rebind code, then run 'kmh global rebind <code>'.")
                    : "This server is not registered with Colony Nexus Global, so there is no identity to change. "
                      + "'kmh global setup' registers it.";

            IdentityChangeState existing = Pending;
            if (existing != null && existing.Kind != kind)
                return $"A {existing.Kind.ToLowerInvariant()} is already in progress. Finish it, or run "
                     + $"'kmh global {existing.Kind.ToLowerInvariant()} cancel'.";
            if (existing != null && existing.Stage == IdentityChangeStage.Submitted)
            {
                string resolved = await ResolveSubmittedAsync(existing, ct).ConfigureAwait(false);
                return resolved == "done" ? "Colony Nexus Global had already installed the new key; this server has switched to it."
                                          : resolved;
            }
            if (existing != null && existing.PendingLinkId.Length > 0
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < existing.PendingLinkExpiresUnix)
            {
                Start();
                return "Still waiting for your approval in the browser - the code shown earlier stays valid until it lapses.";
            }

            if (kind == IdentityChangeKind.Rotate && KmhServerKey.Load() == null)
                return "This server's current signing key cannot be read, so it cannot sign a rotation. Use "
                     + "'kmh global recover' instead.";
            if (KmhServerKey.CreateStaged() == null)
                return "Could not create the new signing key. Check that KMH-Data/Identity is writable.";

            IdentityChangeState s = existing ?? new IdentityChangeState
            {
                Kind = kind, ServerId = KmhServerIdentity.Id, StartedUtc = DateTime.UtcNow.ToString("o"),
            };
            s.Stage = IdentityChangeStage.AwaitingApproval;
            s.FromGeneration = cfg.SigningGeneration;
            s.FromFingerprint = KmhServerKey.Exists ? KmhServerKey.Fingerprint : "";
            s.ToFingerprint = KmhServerKey.StagedFingerprint;
            if (!Save(s)) return "KMH-Data/Identity could not be written, so nothing was started.";

            if (!KmhGlobalSafety.MayPublishTo(cfg.BaseUrl, out string blocked)) return blocked;
            GlobalCallResult r = await SendAsync(OwnerLinksPath, new { server_id = KmhServerIdentity.Id }, null, ct)
                                       .ConfigureAwait(false);
            if (!r.Ok)
            {
                if (r.ErrorCode == "KMH-LNK-702")
                    return "Colony Nexus Global already has a link code waiting for this server, and a code is shown only "
                         + "once. Let it lapse (15 minutes), then run this again.";
                return Describe(r, "the approval link could not be started") + " Nothing changed; run this again to retry.";
            }

            string code = (string)r.Body?["code"] ?? "";
            s.PendingLinkId = (string)r.Body?["link_id"] ?? "";
            s.PendingLinkExpiresUnix = (long?)r.Body?["expires_at"] ?? 0;
            if (s.PendingLinkId.Length == 0 || code.Length == 0) return "Colony Nexus Global returned an incomplete link.";
            Save(s);

            Maintenance.KmhRedact.RegisterSecret(code);
            ServerLog.Info("");
            ServerLog.Info($"  COLONY NEXUS GLOBAL - APPROVE THE {(kind == IdentityChangeKind.Rotate ? "KEY ROTATION" : "IDENTITY RECOVERY")}");
            ServerLog.Info($"    1. Open {KmhGlobalRegistration.DeepLink((string)r.Body?["link_url"] ?? "", code)}");
            ServerLog.Info($"       (or enter the code yourself: {code})");
            ServerLog.Info("    2. Sign in with Steam as this server's owner and approve.");
            ServerLog.Info("    3. That is all - this server finishes on its own and keeps waiting across a restart.");
            ServerLog.Info("");
            Start();
            return "Approve the code in your browser. 'kmh global status' shows where it got to.";
        }

        // Null while still waiting.
        internal static async Task<string> StepAsync(CancellationToken ct)
        {
            IdentityChangeState s = Pending;
            if (s == null || s.Kind == IdentityChangeKind.Rebind) return "nothing to do";
            if (s.Stage == IdentityChangeStage.Submitted) return await ResolveSubmittedAsync(s, ct).ConfigureAwait(false);

            if (_grant.Length == 0)
            {
                if (s.PendingLinkId.Length == 0) return "no approval link is out - run the command again";
                GlobalCallResult polled = await SendAsync($"{OwnerLinksPath}/{s.PendingLinkId}/poll", null, null, ct)
                                                .ConfigureAwait(false);
                if (!polled.Ok) return null;
                string state = (string)polled.Body?["state"] ?? "";
                if (state == "Expired" || state == "Consumed")
                {
                    s.PendingLinkId = ""; s.PendingLinkExpiresUnix = 0;
                    Save(s);
                    return state == "Expired"
                        ? "the approval code lapsed - run the command again for a new one"
                        : "the approval was already collected before a restart - run the command again to approve once more";
                }
                if (state != "Completed") return null;
                _grant = (string)polled.Body?["registration_grant"] ?? "";
                s.PendingLinkId = ""; s.PendingLinkExpiresUnix = 0;
                Save(s);
                if (_grant.Length == 0) return "Global approved but sent no grant - run the command again";
            }
            return await SubmitAsync(s, ct).ConfigureAwait(false);
        }

        private static async Task<string> SubmitAsync(IdentityChangeState s, CancellationToken ct)
        {
            GlobalCallResult challenge = await SendAsync(ChallengePath, new { server_id = s.ServerId }, _grant, ct)
                                               .ConfigureAwait(false);
            if (!challenge.Ok) return Refused(s, challenge, "the challenge was refused");
            string challengeId = (string)challenge.Body?["challenge_id"] ?? "";
            if (challengeId.Length == 0) return Refused(s, challenge, "Global returned no challenge");

            RecoverBody body = new RecoverBody
            {
                ChallengeId = challengeId, ServerId = s.ServerId, PublicKey = KmhServerKey.StagedPublicKey,
            };
            byte[] canonical = body.Canonical();
            body.Signature = KmhServerKey.SignStaged(canonical);
            // The outgoing key signs too: proof the change came from the server that held the identity.
            if (s.Kind == IdentityChangeKind.Rotate) body.PreviousKeySignature = KmhServerKey.Sign(canonical);
            if (body.Signature.Length == 0 || (s.Kind == IdentityChangeKind.Rotate && string.IsNullOrEmpty(body.PreviousKeySignature)))
                return Refused(s, null, "a key could not sign");

            // Written before asking: if the answer is lost, the next step probes instead of asking again blind.
            s.Stage = IdentityChangeStage.Submitted;
            if (!Save(s)) return Refused(s, null, "KMH-Data/Identity could not be written");

            string path = s.Kind == IdentityChangeKind.Rotate ? RotatePath(s.ServerId) : RecoverPath(s.ServerId);
            GlobalCallResult r = await SendAsync(path, body, _grant, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                _grant = "";
                return Finish(s, (int?)r.Body?["signing_generation"] ?? s.FromGeneration + 1);
            }
            if (r.Unreachable) return null;

            // An earlier attempt landed and its answer was lost: the key Global names is already the staged one.
            if (r.ErrorCode == "KMH-SRV-309" && r.Args.TryGetValue("registered_fingerprint", out string held)
                && held == KmhGlobalSigning.ShortFingerprint(s.ToFingerprint))
                return await ResolveSubmittedAsync(s, ct).ConfigureAwait(false);

            _grant = "";
            s.Stage = IdentityChangeStage.AwaitingApproval;
            return Refused(s, r, $"{s.Kind.ToLowerInvariant()} refused");
        }

        // Signs a heartbeat with the staged key: only a Global that installed it can verify that signature.
        private static async Task<string> ResolveSubmittedAsync(IdentityChangeState s, CancellationToken ct)
        {
            if (!KmhServerKey.StagedExists)
            {
                // Promoted already; the change finished except for the record of it.
                if (KmhServerKey.Fingerprint == s.ToFingerprint) return await ProbeActiveAsync(s, ct).ConfigureAwait(false);
                Clear();
                return "the staged key is gone - nothing is pending any more";
            }

            HeartbeatBody beat = KmhGlobalRegistrar.BuildHeartbeat(DateTimeOffset.UtcNow);
            beat.Signature = KmhServerKey.SignStaged(beat.Canonical());
            GlobalCallResult r = await SendAsync(HeartbeatPath, beat, null, ct).ConfigureAwait(false);
            if (r.Ok) return Finish(s, beat.Generation);
            if (r.ErrorCode == "KMH-SRV-317" && TryGeneration(r, out int gen)) return Finish(s, gen);
            if (r.ErrorCode == "KMH-SRV-303")
            {
                s.Stage = IdentityChangeStage.AwaitingApproval;
                s.LastErrorCode = "KMH-SRV-303";
                s.LastDetail = "Global did not install the new key; the request never landed";
                Save(s);
                return "Colony Nexus Global did not install the new key - the request never landed. Run the command again "
                     + "to approve it once more; the current key still works meanwhile.";
            }
            return null;
        }

        // Key swapped in before the record was written; Global's answer says which generation it holds.
        private static async Task<string> ProbeActiveAsync(IdentityChangeState s, CancellationToken ct)
        {
            HeartbeatBody beat = KmhGlobalRegistrar.BuildHeartbeat(DateTimeOffset.UtcNow);
            GlobalCallResult r = await SendAsync(HeartbeatPath, beat, null, ct).ConfigureAwait(false);
            if (r.Ok) return Adopt(s, beat.Generation);
            if (r.ErrorCode == "KMH-SRV-317" && TryGeneration(r, out int gen)) return Adopt(s, gen);
            return null;
        }

        private static bool TryGeneration(GlobalCallResult r, out int generation)
        {
            generation = 0;
            return r.Args.TryGetValue("registered_generation", out string g) && int.TryParse(g, out generation) && generation > 0;
        }

        // Key first, then the generation, then the record: a crash between any two resumes from the next.
        private static string Finish(IdentityChangeState s, int generation)
        {
            if (KmhServerKey.StagedExists && !KmhServerKey.PromoteStaged())
                return "Colony Nexus Global installed the new key, but it could not be swapped in here. Fix KMH-Data/Identity "
                     + "permissions; this server retries on its own.";
            return Adopt(s, generation);
        }

        private static string Adopt(IdentityChangeState s, int generation)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            cfg.SigningGeneration = generation;
            if (s.Kind == IdentityChangeKind.Rebind)
            {
                // The operator's move leaves an owner-less private identity on this key; an owner links it again.
                GlobalSetupState setup = GlobalSetupState.Current_;
                cfg.RegisteredServerId = s.ServerId;
                cfg.Enabled = false;
                cfg.BackendIdentity = true;
                setup.BackendRegistered = true;
                setup.OwnerUserId = ""; setup.OwnerDisplay = "";
                if (setup.Current == SetupStage.Registered || setup.Current == SetupStage.Failed
                    || setup.Current == SetupStage.RegistrationPending)
                    setup.Current = SetupStage.NotStarted;
                if (!setup.Save()) return "Global moved the id, but KMH-Data could not be written - this server retries.";
                KmhGlobalRegistrar.ClearRevokedForTest();
            }
            if (!cfg.Save()) return "Global moved the identity, but KMH-Data could not be written - this server retries.";

            int stale = Guilds.FederatedGuildStore.InvalidateForGeneration(generation);
            Clear();
            ServerLog.Success(s.Kind == IdentityChangeKind.Rebind
                ? $"Global: the operator moved server id {s.ServerId} to this server's key "
                  + $"({KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint)}), generation {generation}. It is a private "
                  + "identity now; 'kmh global setup' links an owner and keeps the id and key."
                : $"Global: signing key {(s.Kind == IdentityChangeKind.Rotate ? "rotated" : "recovered")} - key "
                  + $"{KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint)}, generation {generation}. The previous key "
                  + "no longer authenticates.");
            if (stale > 0)
                ServerLog.Warn($"Federated guilds: {stale} guild link(s) need an officer to prove them again under the new key.");

            // Last, and outside any work above: restarting the service stops this completer.
            if (RestartForTest != null) RestartForTest();
            else Task.Run(() => { KmhGlobalService.Stop(); KmhGlobalService.Start(); });
            return "done";
        }

        public static async Task<string> ProveRebindAsync(string code, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(KmhServerIdentity.Id)) return "This server has no instance id yet.";
            if (RebindProveBody.NormalizeCode(code).Length == 0) return "Usage: kmh global rebind <code from the operator>";
            IdentityChangeState existing = Pending;
            if (existing != null && existing.Kind != IdentityChangeKind.Rebind)
                return $"A {existing.Kind.ToLowerInvariant()} is in progress. Finish it, or run "
                     + $"'kmh global {existing.Kind.ToLowerInvariant()} cancel' first.";
            if (KmhServerKey.Load() == null) return "Could not load or create this server's signing key.";
            if (!KmhGlobalSafety.MayPublishTo(GlobalConfig.Current.BaseUrl, out string blocked)) return blocked;

            IdentityChangeState s = new IdentityChangeState
            {
                Kind = IdentityChangeKind.Rebind, Stage = IdentityChangeStage.AwaitingOperator,
                ServerId = KmhServerIdentity.Id, StartedUtc = DateTime.UtcNow.ToString("o"),
                FromGeneration = GlobalConfig.Current.SigningGeneration, ToFingerprint = KmhServerKey.Fingerprint,
                RebindCode = RebindProveBody.NormalizeCode(code),
            };
            GlobalCallResult r = await SendProofAsync(s, ct).ConfigureAwait(false);
            if (!r.Ok)
                return r.ErrorCode switch
                {
                    "KMH-SRV-324" => "That rebind code is not valid for this server: wrong, cancelled or expired. Ask the operator for a new one.",
                    "KMH-SRV-325" => "A different key already proved that code. Tell the operator to cancel that rebind - this server did not prove it.",
                    "KMH-SRV-327" => "Colony Nexus Global already holds this server's key under this id; nothing needs moving.",
                    _ => Describe(r, "the proof was refused"),
                };

            s.RebindId = (string)r.Body?["rebind_id"] ?? "";
            s.ExpectedGeneration = (int?)r.Body?["signing_generation"] ?? 0;
            s.RebindExpiresUnix = (long?)r.Body?["expires_at"] ?? 0;
            if (string.Equals((string)r.Body?["state"], "Applied", StringComparison.Ordinal))
            {
                string adopted = Adopt(s, s.ExpectedGeneration);
                return adopted == "done" ? "The operator had already applied it; this server has taken the id over." : adopted;
            }
            if (!Save(s)) return "Proved, but KMH-Data/Identity could not be written - run it again once it is writable.";
            Maintenance.KmhRedact.RegisterSecret(s.RebindCode);
            Start();
            return $"Proved. Read this key to the operator before they apply it: {KmhGlobalSigning.ShortFingerprint(s.ToFingerprint)}. "
                 + $"Once applied, this server picks up generation {s.ExpectedGeneration} on its own.";
        }

        private static Task<GlobalCallResult> SendProofAsync(IdentityChangeState s, CancellationToken ct)
        {
            List<string> links = new List<string>();
            foreach (Guilds.FederatedGuildLink l in Guilds.FederatedGuildStore.All())
                if (l.MayRead && !string.IsNullOrEmpty(l.LinkId)) links.Add(l.LinkId);
            RebindProveBody body = new RebindProveBody
            {
                Code = s.RebindCode, ServerId = s.ServerId, PublicKey = KmhServerKey.PublicKey,
                IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Nonce = Guid.NewGuid().ToString("N"), Links = links,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            return SendAsync(ProvePath, body, null, ct);
        }

        internal static async Task<string> RebindStepAsync(CancellationToken ct)
        {
            IdentityChangeState s = Pending;
            if (s == null || s.Kind != IdentityChangeKind.Rebind) return "nothing to do";
            GlobalCallResult r = await SendProofAsync(s, ct).ConfigureAwait(false);
            if (r.Ok && string.Equals((string)r.Body?["state"], "Applied", StringComparison.Ordinal))
                return Adopt(s, (int?)r.Body?["signing_generation"] ?? s.ExpectedGeneration);
            if (r.ErrorCode == "KMH-SRV-324")
            {
                Clear();
                ServerLog.Warn("Global: the rebind was cancelled or lapsed before the operator applied it. Nothing changed here.");
                return "the rebind was cancelled or lapsed";
            }
            return null;
        }

        // A refused beat whose key verified at a newer generation is the expected end of a change in flight.
        internal static bool TryAdoptFromRefusal(GlobalCallResult r)
        {
            if (r == null || r.ErrorCode != "KMH-SRV-317" || !TryGeneration(r, out int gen)) return false;
            if (!Gate.Wait(0)) return false;
            try
            {
                IdentityChangeState s = Pending;
                if (s == null || !KmhServerKey.Exists || KmhServerKey.Fingerprint != s.ToFingerprint) return false;
                if (s.Kind != IdentityChangeKind.Rebind && s.Stage != IdentityChangeStage.Submitted) return false;
                return Adopt(s, gen) == "done";
            }
            finally { Gate.Release(); }
        }

        public static string DescribePending(IdentityChangeState s, long nowUnix)
        {
            if (s == null) return "";
            string kind = s.Kind.ToLowerInvariant();
            if (s.Kind == IdentityChangeKind.Rebind)
                return $"rebind proved with key {KmhGlobalSigning.ShortFingerprint(s.ToFingerprint)}, waiting for the operator "
                     + $"to apply it (generation {s.ExpectedGeneration})"
                     + (s.RebindExpiresUnix > 0 ? $" until {DateTimeOffset.FromUnixTimeSeconds(s.RebindExpiresUnix):yyyy-MM-dd HH:mm} UTC" : "");
            if (s.Stage == IdentityChangeStage.Submitted)
                return $"{kind} sent to Colony Nexus Global; its answer was not seen, so this server is checking what it holds";
            if (s.PendingLinkId.Length > 0 && nowUnix < s.PendingLinkExpiresUnix)
                return $"{kind} waiting for your approval in the browser (the code lapses in {Math.Max(1, (s.PendingLinkExpiresUnix - nowUnix) / 60 + 1)} minute(s))";
            return $"{kind} not finished - 'kmh global {kind} confirm' continues it"
                 + (s.LastErrorCode.Length > 0 ? $" (last refusal {s.LastErrorCode})" : "");
        }

        public static string Cancel(string kind)
        {
            if (!Gate.Wait(CallTimeout)) return "The identity change is busy talking to Colony Nexus Global; try again in a moment.";
            try
            {
                IdentityChangeState s = Pending;
                if (s == null || s.Kind != kind) return $"No {kind.ToLowerInvariant()} is in progress.";
                // Global may already hold the staged key; throwing it away could lock this server out.
                if (s.Stage == IdentityChangeStage.Submitted)
                    return "Colony Nexus Global was already asked and its answer was not seen. Run "
                         + $"'kmh global {kind.ToLowerInvariant()} confirm' to find out first; cancelling now could discard the only key it accepts.";
                Stop();
                _grant = "";
                if (kind != IdentityChangeKind.Rebind) KmhServerKey.DiscardStaged();
                Clear();
                return kind == IdentityChangeKind.Rebind
                    ? "Stopped waiting for the rebind here. Ask the operator to cancel the ticket too, or it can still be applied."
                    : $"The {kind.ToLowerInvariant()} was abandoned and its new key deleted. The current key is unchanged.";
            }
            finally { Gate.Release(); }
        }

        public static void Start()
        {
            if (Running) return;
            IdentityChangeState s = Pending;
            if (s == null) return;
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _loop = Task.Run(() => LoopAsync(s.Kind, ct));
        }

        public static void Stop()
        {
            try { _cts?.Cancel(); } catch (Exception) { }
            _cts = null;
            _loop = null;
        }

        internal static bool StillWaiting(IdentityChangeState s, long nowUnix)
        {
            if (s == null) return false;
            if (s.Kind == IdentityChangeKind.Rebind) return s.RebindExpiresUnix <= 0 || nowUnix <= s.RebindExpiresUnix;
            if (s.Stage == IdentityChangeStage.Submitted) return true;
            return _grant.Length > 0 || (s.PendingLinkId.Length > 0 && nowUnix < s.PendingLinkExpiresUnix);
        }

        private static async Task LoopAsync(string kind, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                IdentityChangeState s = Pending;
                bool slow = kind == IdentityChangeKind.Rebind || s?.Stage == IdentityChangeStage.Submitted;
                try { await Task.Delay(slow ? RebindPollInterval : PollInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                s = Pending;
                if (!StillWaiting(s, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                {
                    if (s?.Kind == IdentityChangeKind.Rebind)
                    {
                        Clear();
                        ServerLog.Warn("Global: the rebind lapsed before the operator applied it. Ask for a new code if it is still needed.");
                    }
                    return;
                }

                if (!await Gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) continue;
                string said;
                try
                {
                    using (CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        call.CancelAfter(CallTimeout);
                        said = kind == IdentityChangeKind.Rebind ? await RebindStepAsync(call.Token).ConfigureAwait(false)
                                                                 : await StepAsync(call.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) return; said = null; }
                catch (Exception ex) { ServerLog.Verbose($"Global: identity change step failed ({ex.GetType().Name}); retrying."); said = null; }
                finally { Gate.Release(); }

                if (said == "done") return;
                if (said != null) ServerLog.Warn("Global: " + said);
            }
        }

        private static string Refused(IdentityChangeState s, GlobalCallResult r, string what)
        {
            s.LastErrorCode = r?.ErrorCode ?? "";
            s.LastDetail = what;
            Save(s);
            string why = r == null ? what : Describe(r, what);
            // No retry changes this answer; only Colony Nexus staff can move such a server's identity.
            if (r?.ErrorCode == "KMH-SRV-328")
                return why + $" Run 'kmh global {s.Kind.ToLowerInvariant()} cancel', then ask Colony Nexus staff for a rebind code ('kmh global rebind <code>').";
            return why + $" Run 'kmh global {s.Kind.ToLowerInvariant()} confirm' to try again, or '... cancel' to abandon it.";
        }

        private static string Describe(GlobalCallResult r, string what)
        {
            if (r.Unreachable) return "Colony Nexus Global is unreachable.";
            string code = r.ErrorCode ?? "";
            string reason = code switch
            {
                "KMH-SRV-302" => "the account that approved is not this server's owner",
                "KMH-SRV-319" => "this server's registration was revoked",
                "KMH-SRV-318" => "another identity change landed first",
                "KMH-SRV-309" => "the current key is not the one Colony Nexus Global holds - use 'kmh global recover'",
                "KMH-SRV-328" => "this server is Official or relays Global Chat, so its identity cannot move to a key that proves nothing",
                "KMH-SRV-CLOCK-SKEW" => "this server's clock is too far off",
                _ => what,
            };
            return $"{char.ToUpperInvariant(reason[0])}{reason.Substring(1)} ({code}"
                 + (string.IsNullOrEmpty(r.CorrelationId) ? ")." : $", ref {r.CorrelationId}).");
        }
    }
}
