using System;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // Federation-only identity: never listed, sends no metadata or mod list; an owner may claim it later, links kept.
    internal static class KmhGlobalBackend
    {
        public const string ChallengePath = "/api/v1/servers/backend/challenge";
        public const string RegisterPath  = "/api/v1/servers/backend/register";

        // Self-test seam; nothing else assigns it.
        internal static bool? AllowedForTest;

        public static bool Allowed
        {
            get
            {
                if (AllowedForTest.HasValue) return AllowedForTest.Value;
                GlobalConfig cfg = GlobalConfig.Current;
                FeaturesConfig f = FeaturesConfig.Current;
                if (cfg == null || f == null || cfg.Enabled || !cfg.BackendIdentity) return false;
                if (!f.GuildFederation || !f.Guilds) return false;
                return !GlobalSetupState.Current_.IsOwnerRegistered;
            }
        }

        public static bool Active => Allowed && GlobalSetupState.Current_.BackendRegistered;

        public static string LastError { get; private set; } = "";

        public static async Task<bool> RegisterAsync(CancellationToken ct)
        {
            Settled = false;
            if (!Allowed) { LastError = "not allowed"; return false; }
            if (string.IsNullOrEmpty(KmhServerIdentity.Id)) { LastError = "no server id yet"; return false; }
            if (KmhServerKey.Load() == null) { LastError = "no usable signing key"; return false; }

            GlobalConfig cfg = GlobalConfig.Current;
            if (!KmhGlobalSafety.MayPublishTo(cfg.BaseUrl, out string blocked)) { LastError = blocked; return false; }

            GlobalCallResult challenge = await KmhGlobalPost.SendAsync(ChallengePath,
                new { server_id = KmhServerIdentity.Id }, ct).ConfigureAwait(false);
            if (!challenge.Ok) return Refused(challenge, "challenge refused");

            string challengeId = (string)challenge.Body?["challenge_id"] ?? "";
            if (string.IsNullOrEmpty(challengeId)) { LastError = "Global returned no challenge id"; return false; }

            BackendRegisterBody body = new BackendRegisterBody
            {
                ChallengeId = challengeId,
                ServerId    = KmhServerIdentity.Id,
                PublicKey   = KmhServerKey.PublicKey,
                Name        = KmhServerIdentity.Name ?? "",
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            if (string.IsNullOrEmpty(body.Signature)) { LastError = "could not sign"; return false; }

            GlobalCallResult reg = await KmhGlobalPost.SendAsync(RegisterPath, body, ct).ConfigureAwait(false);
            if (!reg.Ok) return Refused(reg, "registration refused");

            GlobalSetupState state = GlobalSetupState.Current_;
            cfg.RegisteredServerId = KmhServerIdentity.Id;
            cfg.SigningGeneration = (int?)reg.Body?["signing_generation"] ?? cfg.SigningGeneration;
            state.BackendRegistered = true;
            bool written = cfg.Save() & state.Save();
            LastError = "";
            if (!written)
                ServerLog.Error($"Global: private identity {KmhServerIdentity.Id} was issued but KMH-Data could not be written; "
                              + "it registers again after a restart.");
            else
                ServerLog.Success($"Global: this server has a private Colony Nexus Global identity ({KmhServerIdentity.Id}) "
                                + "for Guild Federation. It is not listed in the browser.");
            return true;
        }

        // Answers no retry can change: an owner exists, the operator revoked it, or another key holds this id.
        public static bool Settled { get; private set; }

        private static bool Refused(GlobalCallResult r, string what)
        {
            string code = r.ErrorCode ?? "";
            Settled = code == "KMH-SRV-322" || code == "KMH-SRV-319" || code == "KMH-SRV-309";
            LastError = r.Unreachable ? "Colony Nexus Global is unreachable"
                      : r.Status == 404 ? "this Colony Nexus Global does not offer private identities yet"
                      : code == "KMH-SRV-322" ? "this server already has an owner at Colony Nexus Global - run 'kmh global setup' to link it"
                      : code == "KMH-SRV-319" ? "Colony Nexus Global revoked this server's identity, and a revoked id cannot register again on its own - if that was a mistake, ask the operator for a rebind code and run 'kmh global rebind <code>'"
                      : code == "KMH-SRV-309" ? "another key is registered for this server id at Colony Nexus Global - put back the key this server registered with, from a KMH-Data backup, or ask the operator for a rebind code and run 'kmh global rebind <code>'"
                      : code == "KMH-SRV-323" ? "Colony Nexus Global is not issuing private identities right now; retried later"
                      : $"{what} ({code})";
            ServerLog.Verbose($"Global: private identity {what} ({(r.Unreachable ? "unreachable" : code)}).");
            if (Settled) ServerLog.Warn($"Global: no private identity for Guild Federation - {LastError}.");
            return false;
        }

        private static CancellationTokenSource _cts;
        private static Task _registration;

        public static void StartRegistration()
        {
            StopRegistration();
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _registration = Task.Run(async () =>
            {
                int[] backoff = { 30, 60, 120, 300, 900, 1800 };
                for (int attempt = 0; !ct.IsCancellationRequested; attempt++)
                {
                    bool ok;
                    try { ok = await RegisterAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { LastError = ex.GetType().Name; ok = false; }
                    if (ok) { KmhGlobalService.Stop(); KmhGlobalService.Start(); return; }
                    if (!Allowed || Settled) return;
                    try { await Task.Delay(TimeSpan.FromSeconds(backoff[Math.Min(attempt, backoff.Length - 1)]), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            });
        }

        public static void StopRegistration()
        {
            try { _cts?.Cancel(); } catch (Exception) { }
            _cts = null;
        }

        // A late background registration would save Global.json and restart the service after a test restored files.
        internal static bool AwaitRegistrationForTest(TimeSpan timeout)
        {
            Task t = _registration;
            try { return t == null || t.Wait(timeout); }
            catch (AggregateException) { return true; }
        }
    }
}
