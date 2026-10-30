using System;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    // Owner approval and signing proof are separate facts: a linked owner alone cannot claim a ServerId.
    internal static class KmhGlobalRegistration
    {
        public sealed class Outcome
        {
            public bool   Ok;
            public string ErrorCode = "";
            public string CorrelationId = "";
            public string Detail = "";
            // Set only by the read-only status call; Global returns no key material.
            public Newtonsoft.Json.Linq.JObject Body;
        }

        // Memory only: GlobalSetupState gets pasted into support threads; a restart mid-setup simply re-links.
        private static string _grant = "";

        // A poll consumes the link; console and completer take turns or the loser sees "already consumed" on success.
        internal static readonly SemaphoreSlim SetupGate = new SemaphoreSlim(1, 1);

        public static bool HasGrant => !string.IsNullOrEmpty(_grant);

        // Test seams: without them the grant lifecycle cannot be driven or tested.
        internal static void SetGrantForTest(string grant) => _grant = grant ?? "";
        internal static void ForgetGrantForTest() => _grant = "";

        // The code goes in the query string; appended as prose the page answers 404.
        internal static string DeepLink(string url, string code)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (string.IsNullOrWhiteSpace(code)) return url;
            return url + (url.IndexOf('?') >= 0 ? "&" : "?") + "code=" + Uri.EscapeDataString(code.Trim());
        }

        internal static string ReadOwnerDisplay(JObject response) => (string)response?["display_name"] ?? "";
        internal static string OwnerVerifiedMessage(string display)
            => string.IsNullOrWhiteSpace(display) ? "Global: owner verified." : $"Global: owner verified as {display.Trim()}.";

        // Local state cannot tell a live registration from a revoked one until a beat is refused.
        public static async Task<Outcome> RegistrationStatusAsync(CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            string serverId = cfg.RegisteredServerId ?? "";
            if (string.IsNullOrEmpty(serverId))
                return new Outcome { Ok = false, Detail = "this server has no Global registration" };

            KmhGlobalClient client = new KmhGlobalClient(cfg.BaseUrl);
            GlobalCallResult r = await client.GetAsync($"/api/v1/servers/{serverId}/registration", ct).ConfigureAwait(false);
            if (!r.Ok)
                return new Outcome
                {
                    Ok = false,
                    Detail = r.Unreachable ? "Colony Nexus Global is unreachable" : $"Global answered {r.ErrorCode}",
                    ErrorCode = r.ErrorCode, CorrelationId = r.CorrelationId,
                };

            return new Outcome { Ok = true, Detail = (string)r.Body?["registration_state"] ?? "", Body = r.Body };
        }

        public static async Task<Outcome> BeginOwnerLinkAsync(CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            GlobalSetupState state = GlobalSetupState.Current_;
            if (!KmhGlobalSafety.MayPublishTo(cfg.BaseUrl, out string blocked))
                return new Outcome { Ok = false, ErrorCode = "KMH-SRV-GUARD", Detail = blocked };

            KmhGlobalClient client = new KmhGlobalClient(cfg.BaseUrl);

            GlobalCallResult r = await client.PostAsync("/api/v1/owner-links",
                new { server_id = KmhServerIdentity.Id }, null, ct).ConfigureAwait(false);

            if (!r.Ok) return Failed(state, r, "owner link could not be started");

            string linkId = (string)r.Body?["link_id"] ?? "";
            string code   = (string)r.Body?["code"] ?? "";
            string url    = (string)r.Body?["link_url"] ?? "";
            long   expiry = (long?)r.Body?["expires_at"] ?? 0;
            if (string.IsNullOrEmpty(linkId) || string.IsNullOrEmpty(code))
                return Failed(state, r, "Global returned an incomplete owner link");

            state.PendingLinkId = linkId;
            state.PendingLinkExpiresUnix = expiry;
            state.Advance(SetupStage.OwnerLinkPending);

            // The code claims ownership and console lines can leave the machine, so redact it before it is ever printed.
            Maintenance.KmhRedact.RegisterSecret(code);

            // The code is shown once, to the operator at the console, and never written to the setup file.
            ServerLog.Info("");
            ServerLog.Info("  COLONY NEXUS GLOBAL - LINK THIS SERVER TO YOUR ACCOUNT");
            ServerLog.Info($"    1. Open {DeepLink(url, code)}");
            ServerLog.Info("       (the code is already filled in; if you opened the page by hand, "
                         + $"enter it yourself: {code})");
            ServerLog.Info($"    2. Sign in with Steam and approve this server.");
            ServerLog.Info($"    3. That is all - this server registers itself. The code expires in "
                         + $"{Math.Max(0, (expiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 60)} minute(s).");
            ServerLog.Info("");

            return new Outcome { Ok = true, Detail = "owner link pending" };
        }

        public static async Task<Outcome> PollOwnerLinkAsync(CancellationToken ct)
        {
            GlobalSetupState state = GlobalSetupState.Current_;
            if (string.IsNullOrEmpty(state.PendingLinkId))
                return new Outcome { Ok = false, Detail = "no owner link is pending" };

            KmhGlobalClient client = new KmhGlobalClient(GlobalConfig.Current.BaseUrl);
            GlobalCallResult r = await client.PostAsync(
                $"/api/v1/owner-links/{state.PendingLinkId}/poll", null, null, ct).ConfigureAwait(false);

            if (!r.Ok) return Failed(state, r, "owner link poll failed");

            string linkState = (string)r.Body?["state"] ?? "";
            if (linkState == "Completed")
            {
                _grant = (string)r.Body?["registration_grant"] ?? "";

                // Handed over once: a second poll returns 'Consumed', so with no grant the only way on is a fresh link.
                if (!HasGrant)
                    return new Outcome { Ok = false, Detail = "Global approved the link but sent no registration "
                                                            + "grant - run 'kmh global setup' again" };

                state.OwnerUserId  = (string)r.Body?["owner_user_id"] ?? "";
                state.OwnerDisplay = ReadOwnerDisplay(r.Body);
                state.PendingLinkId = "";
                state.Advance(SetupStage.OwnerVerified);
                ServerLog.Success(OwnerVerifiedMessage(state.OwnerDisplay));
                return new Outcome { Ok = true, Detail = "owner verified" };
            }

            if (linkState == "Expired")
            {
                state.PendingLinkId = "";
                state.Advance(SetupStage.GlobalExplained);
                return new Outcome { Ok = false, Detail = "the link code expired - start setup again" };
            }

            return new Outcome { Ok = false, Detail = $"owner link {linkState.ToLowerInvariant()}" };
        }

        public static async Task<Outcome> RegisterAsync(CancellationToken ct)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            GlobalSetupState state = GlobalSetupState.Current_;
            if (!KmhGlobalSafety.MayPublishTo(cfg.BaseUrl, out string blocked))
                return new Outcome { Ok = false, ErrorCode = "KMH-SRV-GUARD", Detail = blocked };

            KmhGlobalClient client = new KmhGlobalClient(cfg.BaseUrl);

            if (!HasGrant)
                return new Outcome { Ok = false, ErrorCode = "KMH-SRV-GRANT",
                                     Detail = "no registration grant - run 'kmh global setup' to link an owner" };

            // The key must exist and be provably reloadable before its public half is ever sent.
            if (KmhServerKey.Load() == null)
                return new Outcome { Ok = false, ErrorCode = "KMH-SRV-KEY", Detail = "no usable signing key on disk" };

            state.Advance(SetupStage.RegistrationPending);

            GlobalCallResult challenge = await client.PostAsync("/api/v1/servers/registration/challenge",
                new { server_id = KmhServerIdentity.Id }, _grant, ct).ConfigureAwait(false);
            if (!challenge.Ok) return Failed(state, challenge, "registration challenge refused");

            string challengeId = (string)challenge.Body?["challenge_id"] ?? "";
            if (string.IsNullOrEmpty(challengeId))
                return Failed(state, challenge, "Global returned no challenge id");

            RegisterBody body = new RegisterBody
            {
                ChallengeId = challengeId,
                ServerId    = KmhServerIdentity.Id,
                PublicKey   = KmhServerKey.PublicKey,
                Name        = KmhServerIdentity.Name,
                Visibility  = cfg.Visibility,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());

            GlobalCallResult reg = await client.PostAsync("/api/v1/servers/register", body, _grant, ct)
                                               .ConfigureAwait(false);

            if (!reg.Ok)
            {
                // These two are not retryable: the owner has to rotate, recover, or transfer instead.
                if (reg.ErrorCode == "KMH-SRV-309" || reg.ErrorCode == "KMH-SRV-302")
                    ServerLog.Warn("Global: " + KmhGlobalRegistrar.DescribeRefusalFor(reg.ErrorCode));
                return Failed(state, reg, "registration refused");
            }

            // Global spent it on success, so holding it any longer only widens the window it can leak in.
            _grant = "";

            cfg.RegisteredServerId = KmhServerIdentity.Id;
            cfg.SigningGeneration  = (int?)reg.Body?["signing_generation"] ?? cfg.SigningGeneration;
            cfg.BackendIdentity    = true;
            // Claiming a private identity keeps its ServerId, key and federation links; it is simply owned now.
            state.BackendRegistered = false;

            // Mirrors Global's re-proof on a generation move, so refusal is immediate and the UI never shows a stale link.
            int stale = Guilds.FederatedGuildStore.InvalidateForGeneration(cfg.SigningGeneration);
            if (stale > 0)
                ServerLog.Warn($"Federated guilds: {stale} guild link(s) were proved under an older signing "
                             + "generation - an officer must prove them again before federated silver can move.");

            // Already issued: an unwritten ServerId is not a failure but one a restart would orphan by registering again.
            bool cfgWritten   = cfg.Save();
            bool stateWritten = state.Advance(SetupStage.Registered);
            if (!cfgWritten || !stateWritten)
                ServerLog.Error($"Global: registered {KmhServerIdentity.Id}, but KMH-Data could not be written. Do not "
                              + "restart until it is writable, or this server registers again as a different identity.");
            else
                ServerLog.Success($"Global: registered {KmhServerIdentity.Id} "
                                + $"(generation {cfg.SigningGeneration}, key {KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint)}).");

            return new Outcome { Ok = true, Detail = "registered" };
        }

        private static Outcome Failed(GlobalSetupState state, GlobalCallResult r, string what)
        {
            state.Fail(r.ErrorCode, r.CorrelationId);
            ServerLog.Warn($"Global: {what} ({(string.IsNullOrEmpty(r.ErrorCode) ? "unreachable" : r.ErrorCode)}"
                         + $"{(string.IsNullOrEmpty(r.CorrelationId) ? "" : ", ref " + r.CorrelationId)}).");
            return new Outcome { Ok = false, ErrorCode = r.ErrorCode, CorrelationId = r.CorrelationId, Detail = what };
        }
    }
}
