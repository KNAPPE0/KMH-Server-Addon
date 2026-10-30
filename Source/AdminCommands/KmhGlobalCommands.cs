using System;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.AdminCommands
{
    // Console only: linking to a Global account is an ownership decision, and an in-game admin is not the owner.
    internal static class KmhGlobalCommands
    {
        // A hair longer than the client's own 20s, so a real timeout reports as a timeout.
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);

        // fromConsole comes from the caller: an RWT account can be called "console".
        public static void Run(string[] args, string actorName, Action<string> reply, bool fromConsole)
        {
            if (!fromConsole)
            {
                reply("'global' can only be run from the server console - it links this server to an owner account.");
                return;
            }

            switch (args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "status")
            {
                case "status":     Status(reply);                                         break;
                case "setup":      Setup(args.Length > 2 ? args[2] : "", reply);          break;
                case "defer":      Defer(reply);                                          break;
                case "disable":    Disable(reply);                                        break;
                case "visibility": Visibility(args.Length > 2 ? args[2] : "", reply);      break;
                case "unlink":     Unlink(args.Length > 2 ? args[2] : "", reply);          break;
                case "rotate":     IdentityChange(IdentityChangeKind.Rotate, args.Length > 2 ? args[2] : "", reply);  break;
                case "recover":    IdentityChange(IdentityChangeKind.Recover, args.Length > 2 ? args[2] : "", reply); break;
                // A code read off the operator's page may be typed with spaces.
                case "rebind":     Rebind(args.Length > 2 ? string.Join(" ", args, 2, args.Length - 2) : "", reply); break;
                default:
                    reply(UsageForTest);
                    break;
            }
        }

        internal const string UsageForTest =
            "Usage: kmh global status | setup [new] | defer | disable | unlink confirm | visibility <Public|Unlisted|Private>"
            + " | rotate [confirm|cancel] | recover [confirm|cancel] | rebind <code>|cancel";

        private static void Status(Action<string> reply)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            GlobalSetupState state = GlobalSetupState.Current_;

            reply($"Colony Nexus Global: {(cfg.Enabled ? "enabled" : "not enabled")} - setup stage {state.Current}");
            if (!cfg.Enabled)
                reply(state.BackendRegistered && KmhGlobalBackend.Allowed
                    ? "  Identity     private, for Guild Federation only - not listed, no owner yet ('kmh global setup' links one)"
                    : KmhGlobalBackend.Allowed
                        ? "  Identity     none yet - a private one is registered for Guild Federation"
                          + (string.IsNullOrEmpty(KmhGlobalBackend.LastError) ? "" : $" (last try: {KmhGlobalBackend.LastError})")
                        : "  Identity     none - Guild Federation cannot reach Colony Nexus Global"
                          + (cfg.BackendIdentity ? "" : " (turned off by 'kmh global disable')"));
            reply($"  Endpoint     {cfg.BaseUrl}");
            reply($"  Server id    {(string.IsNullOrEmpty(KmhServerIdentity.Id) ? "(none yet)" : KmhServerIdentity.Id)}");
            reply($"  Signing key  {(KmhServerKey.Exists ? KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint) : "(not created yet)")}");
            reply($"  Visibility   {cfg.Visibility}");
            reply($"  Address      {(string.IsNullOrEmpty(cfg.PublicHost) ? "(none published)" : cfg.PublicHost)}");

            if (!string.IsNullOrEmpty(state.OwnerDisplay)) reply($"  Owner        {state.OwnerDisplay}");
            if (state.IsRegistered)
                reply($"  Registered   generation {cfg.SigningGeneration}, last accepted sequence {cfg.LastSequence}");
            if (KmhIdentityChange.Pending is IdentityChangeState change)
                reply($"  Identity     {KmhIdentityChange.DescribePending(change, DateTimeOffset.UtcNow.ToUnixTimeSeconds())}");
            if (!string.IsNullOrEmpty(state.LastErrorCode))
                reply($"  Last error   {state.LastErrorCode}"
                    + (string.IsNullOrEmpty(state.LastCorrelationId) ? "" : $" (ref {state.LastCorrelationId})"));

            reply(KmhGlobalService.Running
                ? $"  Heartbeat    running every {cfg.HeartbeatSeconds}s"
                : "  Heartbeat    not running");

            foreach (GlobalFinding f in GlobalConfigCheck.Inspect(cfg, state.IsRegistered))
                reply($"  {(f.Level == GlobalFindingLevel.Blocker ? "!" : "-")} {f}");

            // Asked rather than inferred: a revoked registration looks identical here until a beat is refused.
            if (!state.IsRegistered) { reply("  Global says   (not registered)"); return; }

            KmhGlobalRegistration.Outcome remote = Await(KmhGlobalRegistration.RegistrationStatusAsync, reply);
            if (remote == null) return;
            if (!remote.Ok)
            {
                reply($"  Global says   unavailable - {remote.Detail}"
                    + (string.IsNullOrEmpty(remote.CorrelationId) ? "" : $" (ref {remote.CorrelationId})"));
                reply("  The listing state above is this server's last known view, not Global's.");
                return;
            }

            string st = (string)remote.Body?["registration_state"] ?? "?";
            string listing = (string)remote.Body?["listing_state"];
            if (string.IsNullOrEmpty(listing))
                // Global answers anyone who holds the id, so for a server that is not Public it says only this much.
                reply($"  Global says   {st} (it shares only the registration state for a server that is not Public)");
            else
            {
                reply($"  Global says   {st}, listing {listing}, "
                    + $"visibility {(string)remote.Body?["visibility"] ?? "?"}, health {(string)remote.Body?["health_state"] ?? "?"}");
                reply($"                generation {remote.Body?["signing_generation"]}, last accepted sequence {remote.Body?["last_sequence"]}");
                // Global's word, not this config's: it says so only while it shows the page (listed, owned, heard from).
                if (cfg.Enabled && (bool?)remote.Body?["public_page"] == true)
                    reply($"  Web page     {cfg.BaseUrl.TrimEnd('/')}/servers/{cfg.RegisteredServerId}");
            }
            if (!string.Equals(st, "Registered", StringComparison.OrdinalIgnoreCase))
                reply("  " + KmhGlobalRegistrar.DescribeRefusalFor("KMH-SRV-319"));
        }

        // Resumable on purpose: the owner runs it once to get a code, approves in a browser, and runs it again.
        private static void Setup(string option, Action<string> reply)
        {
            // A code is shown once and never stored, so 'new' abandons an unreadable pending link for a visible one.
            bool forceNew = string.Equals(option, "new", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(KmhServerIdentity.Id))
            {
                reply("This server has no instance id yet. Start it once so KMH-Data is created, then try again.");
                return;
            }

            GlobalSetupState state = GlobalSetupState.Current_;
            if (state.IsOwnerRegistered)
            {
                reply($"Already registered as {GlobalConfig.Current.RegisteredServerId}. Use 'kmh global status'.");
                return;
            }
            // Setup is the owner opting in again, so a server that was told to keep quiet may speak once more.
            if (!GlobalConfig.Current.BackendIdentity) { GlobalConfig.Current.BackendIdentity = true; GlobalConfig.Current.Save(); }
            if (state.BackendRegistered)
                reply($"This server already has a private identity ({GlobalConfig.Current.RegisteredServerId}) for Guild "
                    + "Federation. Linking an owner keeps it, its federations and its key.");

            if (forceNew && !state.IsOwnerRegistered)
            {
                // Global keeps one pending link per server, so a live one has to lapse before a new code exists.
                long left = state.PendingLinkExpiresUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (state.PendingLinkId.Length > 0 && left > 0)
                {
                    reply($"A link code for this server is already pending and cannot be replaced for another "
                        + $"{Math.Max(1, left / 60 + 1)} minute(s).");
                    reply("Approve the code you were given, or run 'kmh global setup new' again after it lapses.");
                    return;
                }

                state.PendingLinkId = "";
                state.PendingLinkExpiresUnix = 0;
                state.Advance(SetupStage.NotStarted);
                Begin(reply);
                return;
            }

            switch (state.Current)
            {
                case SetupStage.OwnerLinkPending: Resume(reply); return;
                case SetupStage.OwnerVerified:
                case SetupStage.MetadataConfigured:
                case SetupStage.PreflightPassed:
                case SetupStage.RegistrationPending: Register(reply); return;
                default: Begin(reply); return;
            }
        }

        private static void Begin(Action<string> reply)
        {
            // Created before a code is spent, so a key that cannot be written stops setup first.
            if (KmhServerKey.Load() == null)
            {
                reply("Could not create this server's signing key. Check that KMH-Data/Identity is writable.");
                return;
            }

            KmhGlobalRegistration.Outcome outcome = Await(KmhGlobalRegistration.BeginOwnerLinkAsync, reply);
            if (outcome == null) return;

            if (outcome.Ok)
            {
                // From here the server watches the link itself, so approving in the browser is the last step.
                Features.Global.KmhGlobalSetupCompleter.Start();
                reply("Approve the link in your browser - this server finishes registering on its own, and keeps "
                    + "waiting across a restart. 'kmh global status' shows where it got to.");
                return;
            }

            // Global keeps one pending link per server, and the code it already issued cannot be read back.
            if (string.Equals(outcome.ErrorCode, "KMH-LNK-702", StringComparison.Ordinal))
            {
                reply("This server already has a link code waiting on Global, and a code is only ever shown once.");
                reply("Enter the code you were given, or wait for it to lapse (15 minutes) and run 'kmh global setup new'.");
                return;
            }

            reply($"Could not start the owner link: {outcome.Detail}"
                + (string.IsNullOrEmpty(outcome.ErrorCode) ? "." : $" ({outcome.ErrorCode})."));
        }

        private static void Resume(Action<string> reply)
        {
            GlobalSetupState state = GlobalSetupState.Current_;

            // An expired code can never be approved, so mint a new one rather than poll a dead link for ever.
            if (state.PendingLinkExpiresUnix > 0
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= state.PendingLinkExpiresUnix)
            {
                reply("The previous link code expired. Starting a new one.");
                Begin(reply);
                return;
            }

            KmhGlobalRegistration.Outcome polled = Await(KmhGlobalRegistration.PollOwnerLinkAsync, reply);
            if (polled == null) return;

            if (!polled.Ok)
            {
                if (string.Equals(polled.ErrorCode, "KMH-LNK-701", StringComparison.Ordinal))
                {
                    reply("That link is no longer valid. Starting a new one.");
                    Begin(reply);
                    return;
                }
                reply($"Not linked yet: {polled.Detail}.");
                return;
            }

            // Straight on: the grant is memory-only and short-lived, so a second typed command would waste it.
            Register(reply);
        }

        private static void Register(Action<string> reply)
        {
            KmhGlobalRegistration.Outcome outcome = Await(KmhGlobalRegistration.RegisterAsync, reply);
            if (outcome == null) return;

            if (!outcome.Ok)
            {
                reply($"Registration refused: {outcome.Detail}"
                    + (string.IsNullOrEmpty(outcome.ErrorCode) ? "" : $" ({outcome.ErrorCode})")
                    + (string.IsNullOrEmpty(outcome.CorrelationId) ? "" : $", ref {outcome.CorrelationId}"));
                return;
            }

            GlobalConfig cfg = GlobalConfig.Current;
            if (!cfg.Enabled) { cfg.Enabled = true; cfg.Save(); }

            KmhGlobalService.Stop();
            KmhGlobalService.Start();
            reply($"Registered as {cfg.RegisteredServerId}. Heartbeats started; the listing appears within a minute.");
        }

        private static void Defer(Action<string> reply)
        {
            GlobalSetupState state = GlobalSetupState.Current_;
            if (state.IsOwnerRegistered) { reply("Already registered - use 'kmh global disable' to stop being listed."); return; }

            state.DeferredUtc = DateTime.UtcNow.ToString("o");
            state.Advance(SetupStage.Deferred);
            reply("Global setup deferred. This server will not be listed until you run 'kmh global setup'.");
        }

        // Never revokes on Global (the owner's browser action): a machine that lost its key must not end its registration.
        private static void Unlink(string confirm, Action<string> reply)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            GlobalSetupState state = GlobalSetupState.Current_;
            string serverId = cfg.RegisteredServerId ?? "";

            if (!string.Equals(confirm, "confirm", StringComparison.OrdinalIgnoreCase))
            {
                reply(string.IsNullOrEmpty(serverId)
                    ? "This server has no Colony Nexus Global identity to forget."
                    : $"'kmh global unlink confirm' makes this server forget its Global registration ({serverId}).");
                reply("  Its server id and signing key are kept: Colony Nexus Global knows this server by them, and");
                reply("  'kmh global setup' afterwards links the same identity again. A revoked registration cannot be");
                reply("  linked again under this id.");
                return;
            }

            KmhGlobalService.Stop();

            cfg.Enabled = false;
            cfg.RegisteredServerId = "";
            // Sequence and generation stay: the same identity links again, and Global remembers both for it.
            cfg.LastMetadataRevision = 0;
            bool cfgWritten = cfg.Save();

            state.OwnerUserId = ""; state.OwnerDisplay = "";
            state.PendingLinkId = ""; state.PendingLinkExpiresUnix = 0;
            state.LastErrorCode = ""; state.LastCorrelationId = "";
            state.BackendRegistered = false;
            bool stateWritten = state.Advance(SetupStage.NotStarted);

            // A half-written unlink would boot still half-registered; the owner has to know before restarting.
            if (!cfgWritten || !stateWritten)
            {
                reply("KMH-Data could not be written - this server is unlinked in memory only.");
                reply("  Fix free space/permissions on KMH-Data and run 'kmh global unlink confirm' again BEFORE restarting.");
                return;
            }

            KmhGlobalRegistrar.ClearRevokedForTest();
            ServerLog.Warn($"Global: local identity unlinked by console{(string.IsNullOrEmpty(serverId) ? "" : $" (was {serverId})")}. "
                         + "Heartbeats stopped; this server is no longer using any Global credential.");
            reply(string.IsNullOrEmpty(serverId)
                ? "Forgotten. This server holds no Colony Nexus Global identity."
                : $"Forgotten the Colony Nexus Global registration of {serverId}. This server no longer heartbeats.");
            reply("  The server id and signing key are kept; 'kmh global setup' links this same identity again.");
        }

        // Keeps the Global registration for re-enabling, but stops Federation too: "disable" means send Global nothing.
        private static void Disable(Action<string> reply)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            if (!cfg.Enabled && !cfg.BackendIdentity) { reply("Colony Nexus Global is already disabled on this server."); return; }

            cfg.Enabled = false;
            cfg.BackendIdentity = false;
            bool written = cfg.Save();
            KmhGlobalService.Stop();
            reply(written
                ? "Colony Nexus Global disabled. This server stops heartbeating, drops out of the browser shortly, and "
                + "Guild Federation pauses here. 'kmh global setup' turns it back on."
                : "Colony Nexus Global stopped, but KMH-Data could not be written - it re-enables itself on the next restart.");
        }

        private static void Visibility(string value, Action<string> reply)
        {
            string wanted = (value ?? "").Trim();
            if (!string.Equals(wanted, "Public", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(wanted, "Unlisted", StringComparison.OrdinalIgnoreCase)
             && !string.Equals(wanted, "Private", StringComparison.OrdinalIgnoreCase))
            { reply("Usage: kmh global visibility <Public|Unlisted|Private>"); return; }

            GlobalConfig cfg = GlobalConfig.Current;
            cfg.Visibility = char.ToUpperInvariant(wanted[0]) + wanted.Substring(1).ToLowerInvariant();
            if (!cfg.Save())
            { reply($"Visibility is {cfg.Visibility} for now, but KMH-Data could not be written - it reverts on restart."); return; }

            reply(GlobalSetupState.Current_.IsOwnerRegistered
                ? $"Visibility set to {cfg.Visibility}. It reaches Global with the next heartbeat."
                : $"Visibility set to {cfg.Visibility}; it is sent when an owner links this server. Until then it stays private.");
        }

        // Shown in full before anything is created: the owner has to know what stops working, and what to do next.
        private static void IdentityChange(string kind, string option, Action<string> reply)
        {
            string verb = kind.ToLowerInvariant();
            if (string.Equals(option, "cancel", StringComparison.OrdinalIgnoreCase)) { reply(KmhIdentityChange.Cancel(kind)); return; }
            if (string.Equals(option, "confirm", StringComparison.OrdinalIgnoreCase))
            {
                reply(AwaitText(ct => KmhIdentityChange.BeginAsync(kind, ct)));
                return;
            }

            GlobalConfig cfg = GlobalConfig.Current;
            string id = string.IsNullOrEmpty(KmhServerIdentity.Id) ? "(none yet)" : KmhServerIdentity.Id;
            string key = KmhServerKey.Exists ? KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint) : "(none on disk)";
            if (kind == IdentityChangeKind.Rotate)
            {
                reply($"'kmh global rotate confirm' replaces this server's signing key and keeps its server id ({id}).");
                reply($"  Now      key {key}, generation {cfg.SigningGeneration}");
                reply($"  After    a new key, generation {cfg.SigningGeneration + 1} at Colony Nexus Global");
                reply("  - The new key is created first and kept beside this one; heartbeats carry on until you approve.");
                reply("  - You approve in your browser, signed in as this server's owner; the server finishes on its own.");
                reply("  - The current key then stops working, and anything it signed before is refused.");
            }
            else
            {
                reply($"'kmh global recover confirm' replaces a signing key that is lost or refused, and keeps this server id ({id}).");
                reply($"  Now      key {key}, generation {cfg.SigningGeneration}");
                reply("  - For when Colony Nexus Global refuses this key (KMH-SRV-303, 309 or 317) and no newer KMH-Data/Identity");
                reply("    backup exists to put back. Restoring that backup is the better fix when you have it.");
                reply("  - A new key is created; you approve in your browser as the owner; Global moves to the next generation.");
                reply("  - Every copy of the old key stops working, including one somebody else may hold.");
            }
            reply("  - Each federated guild then needs an officer to prove its federation link again before it can act.");
            reply($"  - Interrupted? Run 'kmh global {verb} confirm' again - it first finds out what Global already did.");
            if (KmhIdentityChange.Pending is IdentityChangeState pending)
                reply($"  In progress: {KmhIdentityChange.DescribePending(pending, DateTimeOffset.UtcNow.ToUnixTimeSeconds())}");
            reply($"Type 'kmh global {verb} confirm' to go ahead, or 'kmh global {verb} cancel' to abandon one in progress.");
        }

        private static void Rebind(string code, Action<string> reply)
        {
            if (string.Equals(code.Trim(), "cancel", StringComparison.OrdinalIgnoreCase))
            {
                reply(KmhIdentityChange.Cancel(IdentityChangeKind.Rebind));
                return;
            }
            if (code.Trim().Length == 0)
            {
                reply("'kmh global rebind <code>' proves this server's key to a rebind the Colony Nexus Global operator opened.");
                reply("  It is for a server id another key holds, or one the operator revoked; the operator gives you the code.");
                reply($"  This server's key is {(KmhServerKey.Exists ? KmhGlobalSigning.ShortFingerprint(KmhServerKey.Fingerprint) : "not created yet")}"
                    + " - read it to the operator so they can match it before applying.");
                if (KmhIdentityChange.Pending is IdentityChangeState pending)
                    reply($"  In progress: {KmhIdentityChange.DescribePending(pending, DateTimeOffset.UtcNow.ToUnixTimeSeconds())}");
                return;
            }
            reply(AwaitText(ct => KmhIdentityChange.ProveRebindAsync(code, ct)));
        }

        private static string AwaitText(Func<CancellationToken, Task<string>> call)
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(CallTimeout))
            {
                bool held = false;
                try
                {
                    held = KmhIdentityChange.Gate.Wait(CallTimeout);
                    return call(cts.Token).GetAwaiter().GetResult() ?? "Colony Nexus Global did not answer yet; this server keeps trying.";
                }
                catch (OperationCanceledException)
                {
                    return $"Colony Nexus Global did not answer within {(int)CallTimeout.TotalSeconds}s. Nothing was lost; run it again.";
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Global: identity command failed: {ex.Message}");
                    return $"Failed: {ex.Message}";
                }
                finally { if (held) KmhIdentityChange.Gate.Release(); }
            }
        }

        // Null means the owner already got an explanation and the caller should stop.
        private static KmhGlobalRegistration.Outcome Await(
            Func<CancellationToken, Task<KmhGlobalRegistration.Outcome>> call, Action<string> reply)
        {
            using (CancellationTokenSource cts = new CancellationTokenSource(CallTimeout))
            {
                // The background completer may be mid-poll on the same link; whoever gets here second waits.
                bool held = false;
                try
                {
                    held = KmhGlobalRegistration.SetupGate.Wait(CallTimeout);
                    return call(cts.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    reply($"Colony Nexus Global did not answer within {(int)CallTimeout.TotalSeconds}s. Check the server's "
                        + "outbound internet access, then try again.");
                    return null;
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Global: setup command failed: {ex.Message}");
                    reply($"Global setup failed: {ex.Message}");
                    return null;
                }
                finally { if (held) KmhGlobalRegistration.SetupGate.Release(); }
            }
        }
    }
}
