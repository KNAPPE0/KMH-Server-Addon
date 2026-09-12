using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Transport;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Two ServerClients answering to one username is the zombie-connection shape; built directly, since a live handshake never does it.
    internal static class KmhSessionOwnershipSelfTest
    {
        private const string ProbeKind = "kmh.selftest.session_probe";

        // VerifyClient() logs through RWT's Printer, which has no console out here, so the flag is set directly.
        private static readonly System.Reflection.FieldInfo VerifiedField =
            typeof(ServerClient).GetField("<IsVerified>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static ServerClient Session(string username)
        {
            ServerClient c = new ServerClient(null, null, createListener: false);
            VerifiedField.SetValue(c, true);
            c.GetData<UserFile>(new UserFile { Username = username });
            return c;
        }

        private static void Login(ServerClient c)  => Network.ServerClients[c] = 0;
        private static void Logout(ServerClient c) => Network.ServerClients.TryRemove(c, out _);

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { KmhApiServer.ResetForTest(); }
            }

            KmhApiServer.SetRunningForTest(true);
            try
            {
                Check("Session: liveness follows the session, not the name", () =>
                {
                    ServerClient a = Session("taz"), b = Session("taz");
                    Login(a);
                    bool aLive = KmhRouter.IsLive(a), bDead = !KmhRouter.IsLive(b);
                    Logout(a); Login(b);
                    bool aNowDead = !KmhRouter.IsLive(a), bNowLive = KmhRouter.IsLive(b);
                    Logout(b);
                    return (aLive && bDead && aNowDead && bNowLive,
                        aLive && bDead && aNowDead && bNowLive
                            ? "A live then dead, B dead then live, both named taz throughout"
                            : $"A={aLive}/{aNowDead} B={bDead}/{bNowLive} - liveness is not session-exact");
                });

                Check("Session: an unverified client is never live", () =>
                {
                    ServerClient c = new ServerClient(null, null, createListener: false);
                    c.GetData<UserFile>(new UserFile { Username = "taz" });
                    Login(c);
                    bool dead = !KmhRouter.IsLive(c);
                    Logout(c);
                    return (dead, dead ? "in ServerClients but unverified -> not live" : "an unverified session read as live");
                });

                Check("API: a push resolves the connection of that exact session", () =>
                {
                    ServerClient a = Session("taz"), b = Session("taz");
                    Login(a);
                    KmhApiServer.RegisterForTest("taz", a);
                    bool aHas = KmhApiServer.IsRegisteredFor(a), bHasNot = !KmhApiServer.IsRegisteredFor(b);
                    // B reconnects and takes over the registry slot; A's push must stop resolving, not follow the name.
                    Logout(a); Login(b);
                    KmhApiServer.RegisterForTest("taz", b);
                    bool aGone = !KmhApiServer.IsRegisteredFor(a), bNow = KmhApiServer.IsRegisteredFor(b);
                    Logout(b);
                    return (aHas && bHasNot && aGone && bNow,
                        aHas && bHasNot && aGone && bNow
                            ? "a push for A never travels over B's socket, and B's never over A's"
                            : $"A={aHas}/{aGone} B={bHasNot}/{bNow} - the API is still addressed by name");
                });

                Check("API: a failed write drops the connection instead of leaving it registered", () =>
                {
                    ServerClient a = Session("taz");
                    Login(a);
                    KmhApiServer.RegisterForTest("taz", a);
                    bool live = KmhApiServer.IsConnected(a);
                    // A test connection has no stream, so the write throws exactly where a reset socket throws.
                    KmhApiServer.SendResult first  = KmhApiServer.Send(a, new KmhEnvelope(ProbeKind, null));
                    bool dropped = !KmhApiServer.IsRegisteredFor(a) && !KmhApiServer.IsConnected(a);
                    KmhApiServer.SendResult second = KmhApiServer.Send(a, new KmhEnvelope(ProbeKind, null));
                    Logout(a);
                    bool ok = live && first == KmhApiServer.SendResult.IoFailure
                           && dropped && second == KmhApiServer.SendResult.NotConnected;
                    return (ok, ok
                        ? "the first write fails and deregisters; the second never touches the dead socket"
                        : $"live={live} first={first} dropped={dropped} second={second} - a dead socket stays registered and every later send retries it");
                });

                Check("API: closing a session leaves a namesake's connection alone", () =>
                {
                    ServerClient a = Session("taz"), b = Session("taz");
                    Login(b);
                    KmhApiServer.RegisterForTest("taz", b);
                    // A's teardown arrives late, after B has already logged in and claimed the slot.
                    KmhApiServer.CloseForSession(a);
                    bool survived = KmhApiServer.IsRegisteredFor(b);
                    Logout(b);
                    return (survived, survived ? "late teardown of A did not close B" : "A's teardown closed B's connection");
                });

                Check("API: a token dies with the session that minted it", () =>
                {
                    ServerClient a = Session("taz");
                    Login(a);
                    string token = KmhApiServer.IssueToken(a);
                    bool minted = !string.IsNullOrEmpty(token);
                    Logout(a);   // session ends before the client ever dialed
                    bool refused = !KmhApiServer.TryConsumeTokenForTest(token, out _, out _);
                    return (minted && refused, minted && refused
                        ? "an unused token stops authenticating the moment its session ends"
                        : $"minted={minted} refused={refused}");
                });

                Check("API: a token cannot be inherited by a later session of the same name", () =>
                {
                    ServerClient a = Session("taz"), b = Session("taz");
                    Login(a);
                    string token = KmhApiServer.IssueToken(a);
                    Logout(a); Login(b);   // same username, new session
                    bool refused = !KmhApiServer.TryConsumeTokenForTest(token, out _, out _);
                    Logout(b);
                    return (refused, refused ? "A's credential does not authenticate B" : "B AUTHENTICATED WITH A'S TOKEN");
                });

                Check("API: disconnect revokes that session's outstanding tokens", () =>
                {
                    ServerClient a = Session("taz"), b = Session("kim");
                    Login(a); Login(b);
                    KmhApiServer.IssueToken(a);
                    KmhApiServer.IssueToken(b);
                    int before = KmhApiServer.OutstandingTokensForTest;
                    KmhApiServer.CloseForSession(a);
                    int after = KmhApiServer.OutstandingTokensForTest;
                    Logout(a); Logout(b);
                    return (before == 2 && after == 1, $"outstanding {before} -> {after} (expected 2 -> 1: only A's revoked)");
                });

                Check("API: a session holds one credential, not one per hello", () =>
                {
                    ServerClient a = Session("taz");
                    Login(a);
                    string first = KmhApiServer.IssueToken(a);
                    string again = KmhApiServer.IssueToken(a);   // the handshake re-sends the hello once
                    int outstanding = KmhApiServer.OutstandingTokensForTest;
                    Logout(a);
                    return (first == again && outstanding == 1,
                        first == again && outstanding == 1
                            ? "the re-sent hello reuses the unconsumed token instead of leaving a spare valid"
                            : $"outstanding={outstanding}, same={first == again}");
                });

                Check("API: a consumed token is not reissued", () =>
                {
                    ServerClient a = Session("taz");
                    Login(a);
                    string first = KmhApiServer.IssueToken(a);
                    KmhApiServer.TryConsumeTokenForTest(first, out _, out _);
                    string next = KmhApiServer.IssueToken(a);
                    Logout(a);
                    return (!string.IsNullOrEmpty(next) && next != first,
                        next != first ? "a fresh credential is minted once the previous one is used"
                                      : "the same one-use token came back after being consumed");
                });

                Check("Transport: a half-sent transfer cannot finish on the next session", () =>
                {
                    ServerClient a = Session("taz"), b = Session("taz");
                    Login(a);
                    KmhHandshakeHandler.MarkCompatible(a);
                    KmhHandshakeHandler.MarkCompatible(b);
                    int delivered = 0;
                    KmhRouter.RegisterHandler(ProbeKind, (_, __) => delivered++);

                    List<KmhEnvelope> parts = KmhFragments.Split(ProbeKind, new KmhEnvelope(ProbeKind, new { pad = new string('x', 90_000) }).Serialize(), 64 * 1024);
                    if (parts == null || parts.Count < 2) return (false, $"probe payload split into {parts?.Count ?? 0} part(s), needs at least 2");

                    for (int i = 0; i < parts.Count - 1; i++) KmhRouter.HandleInbound(a, parts[i]);
                    Logout(a);
                    KmhRouter.ForgetPeer(a);           // the session teardown the disconnect hook performs
                    Login(b);
                    KmhRouter.HandleInbound(b, parts[parts.Count - 1]);   // B sends only the tail
                    int afterOrphan = delivered;

                    foreach (KmhEnvelope p in parts) KmhRouter.HandleInbound(b, p);   // B's own complete transfer
                    int afterOwn = delivered;
                    Logout(b);
                    KmhRouter.ForgetPeer(b);
                    return (afterOrphan == 0 && afterOwn == 1,
                        afterOrphan == 0 && afterOwn == 1
                            ? "A's orphaned parts assembled nothing; B's own transfer still completed"
                            : $"delivered {afterOrphan} from A's leftovers, {afterOwn - afterOrphan} from B's own");
                });

                Check("Treasury: a deposit approval belongs to the session it was given to", () =>
                {
                    ServerClient a = Session("taz"), b = Session("taz");
                    Login(a); Login(b);
                    string token = Features.Treasury.TreasuryHandler.IssuePreflightForTest(a, "taz", "silver", 500, "", 0);
                    // Same player, same shape, wrong session: an approval waives the cooldown and the per-deposit cap.
                    bool bRefused = !Features.Treasury.TreasuryHandler.ConsumePreflightForTest(b, "taz", "silver", 500, "", 0, token);
                    string token2 = Features.Treasury.TreasuryHandler.IssuePreflightForTest(a, "taz", "silver", 500, "", 0);
                    bool aAccepted = Features.Treasury.TreasuryHandler.ConsumePreflightForTest(a, "taz", "silver", 500, "", 0, token2);
                    Logout(a); Logout(b);
                    return (bRefused && aAccepted,
                        bRefused && aAccepted ? "B cannot spend A's approval; A still can"
                                              : $"bRefused={bRefused} aAccepted={aAccepted}");
                });

                Check("Treasury: an approval does not outlive its session", () =>
                {
                    ServerClient a = Session("taz");
                    Login(a);
                    Features.Treasury.TreasuryHandler.IssuePreflightForTest(a, "taz", "silver", 500, "", 0);
                    int before = Features.Treasury.TreasuryHandler.OutstandingPreflightForTest;
                    Logout(a);
                    Features.Treasury.TreasuryHandler.ReapPreflightForTest();
                    int after = Features.Treasury.TreasuryHandler.OutstandingPreflightForTest;
                    return (before > after && after == 0, $"outstanding {before} -> {after} after the session ended");
                });

                Check("Policy: the handshake always rides chat", () =>
                {
                    bool hello = KmhRouter.IsTransportControlKind(KmhProtocol.Kind.Hello);
                    bool ack   = KmhRouter.IsTransportControlKind(KmhProtocol.Kind.HelloAck);
                    bool ping  = KmhRouter.IsTransportControlKind(KmhProtocol.Kind.Ping);
                    bool pong  = KmhRouter.IsTransportControlKind(KmhProtocol.Kind.Pong);
                    bool feature = !KmhRouter.IsTransportControlKind(KmhProtocol.Kind.TreasuryWithdrawSilver);
                    return (hello && ack && ping && pong && feature,
                        hello && ack && ping && pong && feature
                            ? "hello/ack/ping/pong are control; a treasury withdrawal is not"
                            : "the control set does not match the handshake");
                });

                Check("Policy: fallback off forbids feature traffic over chat, not the handshake", () =>
                {
                    TransportConfig saved = TransportConfig.Current;
                    try
                    {
                        TransportConfig.ApplyForTest(new TransportConfig { AllowChatTransportFallback = false });
                        bool helloOk    = KmhRouter.ChatMayCarry(KmhProtocol.Kind.Hello);
                        bool featureOff = !KmhRouter.ChatMayCarry(KmhProtocol.Kind.TreasuryWithdrawSilver);
                        TransportConfig.ApplyForTest(new TransportConfig { AllowChatTransportFallback = true });
                        bool featureOn  = KmhRouter.ChatMayCarry(KmhProtocol.Kind.TreasuryWithdrawSilver);
                        return (helloOk && featureOff && featureOn,
                            helloOk && featureOff && featureOn
                                ? "owner policy governs feature traffic only"
                                : $"hello={helloOk} featureOff={featureOff} featureOn={featureOn}");
                    }
                    finally { TransportConfig.ApplyForTest(saved); }
                });
            }
            finally { KmhApiServer.SetRunningForTest(false); KmhApiServer.ResetForTest(); }

            return r;
        }
    }
}
