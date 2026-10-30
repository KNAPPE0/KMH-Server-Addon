using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Transport;

namespace KMHServerAddon.Maintenance
{
    // Throwaway limiters and a fake clock, so the DoS guards can be proven with no sockets and no live config.
    internal static class KmhTransportGuardsSelfTest
    {
        // Opening the KMH tab fires one request per feature, so the cap must stay clear of that burst.
        private const int LegitBurst = 20;

        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // Encoding once for every peer is only safe if those bytes match the per-client path: body, 4-byte header, order.
            Check("Transport: a payload framed once is byte-identical to framing it per client", () =>
            {
                var env = new SubProtocol.KmhEnvelope("kmh.selftest.frame", new { pad = new string('z', 5000), n = 42 });
                KmhApiServer.Framed a = KmhApiServer.Encode(env);
                KmhApiServer.Framed b = KmhApiServer.Encode(env);
                if (a == null || b == null) return (false, "Encode returned null for a serializable payload");

                byte[] body = System.Text.Encoding.UTF8.GetBytes(env.Serialize());
                bool sameBody = a.BodyBytes == body.Length && a.Bytes.Length == body.Length + 4;
                bool header = a.Bytes[0] == (byte)(body.Length >> 24) && a.Bytes[1] == (byte)(body.Length >> 16)
                           && a.Bytes[2] == (byte)(body.Length >> 8)  && a.Bytes[3] == (byte)body.Length;
                bool payload = true;
                for (int i = 0; i < body.Length && payload; i++) if (a.Bytes[i + 4] != body[i]) payload = false;
                bool stable = a.Bytes.Length == b.Bytes.Length;
                bool ok = sameBody && header && payload && stable && a.Kind == "kmh.selftest.frame";
                return (ok, ok ? $"{a.BodyBytes} body bytes, 4-byte header, reused by every recipient"
                              : $"body={sameBody} header={header} payload={payload} stable={stable}");
            });
            Check("Transport: a payload that cannot serialize fails once, not once per client", () =>
            {
                KmhApiServer.Framed f = KmhApiServer.Encode(null);
                return (f == null, f == null ? "a null envelope encodes to nothing" : "NULL ENVELOPE PRODUCED A FRAME");
            });

            // A 1 KB compressed frame can inflate to 1 MB, so it must be refused before the hello is checked.
            Check("Transport: compressed frames are refused before auth, and capped per connection after", () =>
            {
                byte[] raw = System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"kmh.api.hello\",\"data\":{\"p\":\"" + new string('A', 1024 * 1024) + "\"}}");
                byte[] body = SubProtocol.KmhFrameCodec.Deflate(raw);
                byte[] frame = new byte[4 + body.Length];
                SubProtocol.KmhFrameCodec.WriteHeader(frame, body.Length, compressed: true);
                Buffer.BlockCopy(body, 0, frame, 4, body.Length);
                SubProtocol.KmhEnvelope Read(Features.Transport.KmhApiServer.InflateBudget b)
                    => Features.Transport.KmhApiServer.ReadFrame(new System.IO.MemoryStream(frame), null, System.Threading.CancellationToken.None, b)
                        .GetAwaiter().GetResult();
                bool preAuthRefused = Read(null) == null;
                var budget = new Features.Transport.KmhApiServer.InflateBudget();
                bool afterAuthRead = Read(budget)?.Kind == "kmh.api.hello";
                int more = 0;
                while (more < 40 && Read(budget) != null) more++;
                bool capped = more < 40;
                return (preAuthRefused && afterAuthRead && capped, $"preAuthRefused={preAuthRefused} afterAuth={afterAuthRead} framesBeforeCap={more + 1}");
            });

            // Public-by-default is allowed only because every safety system defaults on.
            Check("Transport: default posture is locked down", () =>
            {
                var c = new TransportConfig();
                bool safetyOn = c.RequireKmhApiAuth
                             && c.Advanced.MaxConnections > 0 && c.Advanced.MaxConnectionsPerIp > 0
                             && c.Advanced.AuthTimeoutSeconds > 0 && c.Advanced.IdleTimeoutSeconds > 0
                             && c.Advanced.MaxFrameKb > 0
                             && c.Advanced.FailedAuthPerIp > 0 && c.Advanced.FailedAuthWindowSeconds > 0 && c.Advanced.FailedAuthBlockSeconds > 0;
                return (safetyOn, safetyOn
                    ? $"bind={c.BindAddress} with auth+caps+throttle+timeouts+frame limits all required"
                    : "A SAFETY DEFAULT IS OFF - public default bind is not acceptable like this");
            });
            Check("Transport: default auth is on", () =>
            {
                var c = new TransportConfig();
                return (c.RequireKmhApiAuth, c.RequireKmhApiAuth ? "RequireKmhApiAuth=true" : "AUTH OFF BY DEFAULT");
            });
            Check("Transport: IsPublicBind classifies addresses", () =>
            {
                bool loop4 = !new TransportConfig { BindAddress = "127.0.0.1" }.IsPublicBind;
                bool loop6 = !new TransportConfig { BindAddress = "::1" }.IsPublicBind;
                bool anyV4 =  new TransportConfig { BindAddress = "0.0.0.0" }.IsPublicBind;
                bool lan   =  new TransportConfig { BindAddress = "192.168.1.50" }.IsPublicBind;
                return (loop4 && loop6 && anyV4 && lan, "127.0.0.1/::1 local, 0.0.0.0/LAN public");
            });
            Check("Transport: clamp repairs unsafe values", () =>
            {
                // An out-of-range value must snap to a safe default rather than disabling a guard.
                var c = TransportConfig.ClampForTest(new TransportConfig
                {
                    BindAddress = "  ", KmhApiPort = 0,
                    Advanced = new TransportAdvanced { MaxConnections = -5, MaxConnectionsPerIp = 0, MaxFrameKb = 99999, FailedAuthPerIp = 0 },
                });
                bool bindOk  = !c.IsPublicBind;                 // blank -> loopback, never 0.0.0.0
                bool portOk  = c.KmhApiPort >= 1 && c.KmhApiPort <= 65535;
                bool connOk  = c.Advanced.MaxConnections >= 1;
                bool perIpOk = c.Advanced.MaxConnectionsPerIp >= 1 && c.Advanced.MaxConnectionsPerIp <= c.Advanced.MaxConnections;
                bool frameOk = c.Advanced.MaxFrameKb >= 1 && c.Advanced.MaxFrameKb <= 1024;
                bool failOk  = c.Advanced.FailedAuthPerIp >= 1;
                return (bindOk && portOk && connOk && perIpOk && frameOk && failOk,
                        $"bind={c.BindAddress} port={c.KmhApiPort} conn={c.Advanced.MaxConnections} perIp={c.Advanced.MaxConnectionsPerIp} frameKb={c.Advanced.MaxFrameKb} failPerIp={c.Advanced.FailedAuthPerIp}");
            });

            Check("Transport: the auth throttle stays bounded under a flood of addresses, and an active block outlives it", () =>
            {
                long now = DateTime.UtcNow.Ticks;
                var t = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 600, nowTicks: () => now);
                for (int i = 0; i < 3; i++) t.NoteFailure("198.51.100.7", out _);
                for (int i = 0; i < AuthFailThrottle.MaxTracked + 5_000; i++) t.NoteFailure($"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}", out _);
                bool bounded = t.Tracked <= AuthFailThrottle.MaxTracked;
                bool stillBlocked = t.IsBlocked("198.51.100.7");
                now += TimeSpan.FromSeconds(61).Ticks;
                for (int i = 0; i < 300; i++) t.NoteFailure("192.0.2.1", out _);
                bool aged = t.Tracked < 100 && t.IsBlocked("198.51.100.7");
                return (bounded && stillBlocked && aged, $"tracked {t.Tracked}, block kept={stillBlocked}, after the window {aged}");
            });
            Check("Transport: an IPv6 host rotating inside its /64 meets the same block", () =>
            {
                var t = new AuthFailThrottle(threshold: 5, windowSeconds: 60, blockSeconds: 300);
                for (int i = 1; i <= 5; i++) t.NoteFailure($"2001:db8:1:2::{i:x}", out _);
                bool sameNet = t.IsBlocked("2001:db8:1:2:ffff::1");
                bool otherNet = !t.IsBlocked("2001:db8:1:3::1");
                for (int i = 0; i < 5; i++) t.NoteFailure("::ffff:203.0.113.9", out _);
                bool mapped = t.IsBlocked("203.0.113.9");
                return (sameNet && otherNet && mapped && t.Tracked == 2, $"same /64 blocked={sameNet}, next /64 free={otherNet}, mapped IPv4 one key={mapped}, tracked {t.Tracked}");
            });
            Check("Transport: a rate window forgets names it has not seen for a whole window", () =>
            {
                var w = new Util.KmhRateWindow();
                long now = DateTime.UtcNow.Ticks;
                for (int i = 0; i < 2000; i++) w.Allow("user" + i, now, 5, 60);
                int before = w.Tracked;
                now += TimeSpan.FromSeconds(61).Ticks;
                for (int i = 0; i < 600; i++) w.Allow("active", now + i, 1000, 60);
                return (before == 2000 && w.Tracked <= 2, $"{before} names, {w.Tracked} left a window later");
            });

            Check("Transport: per-IP cap blocks the (N+1)th socket", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 3);
                bool a = lim.TryAdmit("1.1.1.1");
                bool b = lim.TryAdmit("1.1.1.1");
                bool c = lim.TryAdmit("1.1.1.1");
                bool d = lim.TryAdmit("1.1.1.1");
                return (a && b && c && !d && lim.PerIp("1.1.1.1") == 3, "3 admitted, 4th refused");
            });
            Check("Transport: release frees per-IP capacity", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 2);
                lim.TryAdmit("2.2.2.2"); lim.TryAdmit("2.2.2.2");
                bool blockedAtCap = !lim.TryAdmit("2.2.2.2");
                lim.Release("2.2.2.2");
                bool admittedAfterRelease = lim.TryAdmit("2.2.2.2");
                return (blockedAtCap && admittedAfterRelease, "freed slot reusable");
            });
            Check("Transport: global cap blocks across IPs", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 2, maxPerIp: 10);
                bool a = lim.TryAdmit("a"); bool b = lim.TryAdmit("b");
                bool c = lim.TryAdmit("c");
                return (a && b && !c && lim.Total == 2, "2 total admitted, 3rd refused");
            });
            Check("Transport: a refused socket leaks no capacity", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 1);
                lim.TryAdmit("3.3.3.3");
                lim.TryAdmit("3.3.3.3");
                lim.TryAdmit("3.3.3.3");
                return (lim.Total == 1 && lim.PerIp("3.3.3.3") == 1, $"total={lim.Total}");
            });
            Check("Transport: an IPv6 host rotating through its /64 shares one per-IP cap", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 2);
                bool a = lim.TryAdmit("2001:db8:1:2::a"), b = lim.TryAdmit("2001:db8:1:2::b");
                bool rotated = lim.TryAdmit("2001:db8:1:2:ffff::c");
                bool otherNet = lim.TryAdmit("2001:db8:1:3::a");
                lim.Release("2001:db8:1:2::a");
                bool afterRelease = lim.TryAdmit("2001:db8:1:2::d");
                bool ok = a && b && !rotated && otherNet && afterRelease && lim.Total == 3;
                return (ok, $"rotatedAdmitted={rotated} otherNet={otherNet} afterRelease={afterRelease} total={lim.Total}");
            });

            Check("Transport: throttle trips at the threshold", () =>
            {
                long t = 0; var th = new AuthFailThrottle(threshold: 5, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                bool trippedEarly = false;
                for (int i = 0; i < 4; i++) trippedEarly |= th.NoteFailure("4.4.4.4", out _);
                bool trippedAt5 = th.NoteFailure("4.4.4.4", out int total);
                return (!trippedEarly && trippedAt5 && th.IsBlocked("4.4.4.4") && total == 5, "blocked on the 5th failure");
            });
            Check("Transport: block expires after the block window", () =>
            {
                long t = 0; long sec = TimeSpan.FromSeconds(1).Ticks;
                var th = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                for (int i = 0; i < 3; i++) th.NoteFailure("5.5.5.5", out _);
                bool blockedNow = th.IsBlocked("5.5.5.5");
                t += 301 * sec;
                bool freeAfter = !th.IsBlocked("5.5.5.5");
                return (blockedNow && freeAfter, "300s block then clear");
            });
            Check("Transport: failures age out of the window", () =>
            {
                long t = 0; long sec = TimeSpan.FromSeconds(1).Ticks;
                var th = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                th.NoteFailure("6.6.6.6", out _); th.NoteFailure("6.6.6.6", out _);
                t += 61 * sec;
                bool tripped = th.NoteFailure("6.6.6.6", out int total);
                return (!tripped && total == 1, "stale failures don't accumulate");
            });
            Check("Transport: clean auth clears the streak", () =>
            {
                long t = 0; var th = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                th.NoteFailure("7.7.7.7", out _); th.NoteFailure("7.7.7.7", out _);
                th.Clear("7.7.7.7");
                bool tripped = th.NoteFailure("7.7.7.7", out int total);
                return (!tripped && total == 1, "streak reset after success");
            });
            Check("Transport: one IP's block doesn't affect others", () =>
            {
                long t = 0; var th = new AuthFailThrottle(threshold: 2, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                th.NoteFailure("8.8.8.8", out _); th.NoteFailure("8.8.8.8", out _);
                return (th.IsBlocked("8.8.8.8") && !th.IsBlocked("9.9.9.9"), "blocks are per-IP");
            });

            Check("Transport: inbound flood cap leaves room for a legit refresh burst", () =>
            {
                int cap = SubProtocol.KmhRouter.MaxInboundPerWindow;
                int win = SubProtocol.KmhRouter.InboundWindowSeconds;
                return (cap >= LegitBurst * 2 && win >= 1,
                    $"{cap} msgs/{win}s vs a ~{LegitBurst}-message open-the-tab burst");
            });

            // The relay answers unauthenticated HTTP, so its own caps are the only thing bounding a stalled peer.
            RunRelayChecks(Check);

            return r;
        }

        // Driven through the relay's real admission calls and restored after, so the live server keeps its caps.
        private static void RunRelayChecks(Action<string, Func<(bool, string)>> Check)
        {
            Features.Media.MediaConfig live = Features.Media.MediaConfig.Current;
            try
            {
                Check("Video relay: stalled connections cannot exceed the global cap", () =>
                {
                    Features.Media.KmhVideoRelay.ConfigureLimits(maxTotal: 3, maxPerIp: 3);
                    int admitted = 0;
                    for (int i = 0; i < 10; i++)
                        if (Features.Media.KmhVideoRelay.TryAdmit($"10.0.0.{i}")) admitted++;
                    int active = Features.Media.KmhVideoRelay.ActiveConnections;
                    for (int i = 0; i < admitted; i++) Features.Media.KmhVideoRelay.ReleaseAdmit($"10.0.0.{i}");
                    return (admitted == 3 && active == 3, $"{admitted} admitted of 10 against a cap of 3");
                });

                Check("Video relay: one IP cannot exceed the per-IP cap", () =>
                {
                    Features.Media.KmhVideoRelay.ConfigureLimits(maxTotal: 50, maxPerIp: 2);
                    int mine = 0;
                    for (int i = 0; i < 8; i++) if (Features.Media.KmhVideoRelay.TryAdmit("10.1.1.1")) mine++;
                    bool otherStillFits = Features.Media.KmhVideoRelay.TryAdmit("10.1.1.2");
                    for (int i = 0; i < mine; i++) Features.Media.KmhVideoRelay.ReleaseAdmit("10.1.1.1");
                    if (otherStillFits) Features.Media.KmhVideoRelay.ReleaseAdmit("10.1.1.2");
                    return (mine == 2 && otherStillFits, $"one IP got {mine} of 8; a second IP was unaffected");
                });

                Check("Video relay: a finished or timed-out request returns its permit", () =>
                {
                    Features.Media.KmhVideoRelay.ConfigureLimits(maxTotal: 2, maxPerIp: 2);
                    Features.Media.KmhVideoRelay.TryAdmit("10.2.2.2");
                    Features.Media.KmhVideoRelay.TryAdmit("10.2.2.2");
                    bool fullNow = !Features.Media.KmhVideoRelay.TryAdmit("10.2.2.2");
                    Features.Media.KmhVideoRelay.ReleaseAdmit("10.2.2.2");
                    bool freedAfterRelease = Features.Media.KmhVideoRelay.TryAdmit("10.2.2.2");
                    Features.Media.KmhVideoRelay.ReleaseAdmit("10.2.2.2");
                    Features.Media.KmhVideoRelay.ReleaseAdmit("10.2.2.2");
                    return (fullNow && freedAfterRelease
                            && Features.Media.KmhVideoRelay.ActiveFor("10.2.2.2") == 0,
                            "full at the cap, admits again once a permit comes back, and settles at zero");
                });

                Check("Video relay: releases are balanced, so the cap cannot drift open", () =>
                {
                    Features.Media.KmhVideoRelay.ConfigureLimits(maxTotal: 4, maxPerIp: 4);
                    for (int round = 0; round < 20; round++)
                    {
                        if (Features.Media.KmhVideoRelay.TryAdmit("10.3.3.3"))
                            Features.Media.KmhVideoRelay.ReleaseAdmit("10.3.3.3");
                    }
                    int leaked = Features.Media.KmhVideoRelay.ActiveConnections;
                    int stillAdmits = 0;
                    for (int i = 0; i < 4; i++) if (Features.Media.KmhVideoRelay.TryAdmit("10.3.3.4")) stillAdmits++;
                    for (int i = 0; i < stillAdmits; i++) Features.Media.KmhVideoRelay.ReleaseAdmit("10.3.3.4");
                    return (leaked == 0 && stillAdmits == 4, $"{leaked} leaked after 20 cycles; full {stillAdmits}/4 still available");
                });

                Check("Video relay: both port modes share one limiter", () =>
                {
                    // Both port modes go through these calls, so a cap proven here is the same cap in either.
                    Features.Media.KmhVideoRelay.ConfigureLimits(maxTotal: 1, maxPerIp: 1);
                    bool first  = Features.Media.KmhVideoRelay.TryAdmit("10.4.4.4");
                    bool second = Features.Media.KmhVideoRelay.TryAdmit("10.4.4.5");
                    if (first)  Features.Media.KmhVideoRelay.ReleaseAdmit("10.4.4.4");
                    if (second) Features.Media.KmhVideoRelay.ReleaseAdmit("10.4.4.5");
                    return (first && !second, "the second connection is refused whichever listener accepted it");
                });

                Check("Video relay: shipped defaults bound an unauthenticated peer", () =>
                {
                    var c = new Features.Media.MediaConfig();
                    bool bounded = c.Video.MaxConnections > 0 && c.Video.MaxConnectionsPerIp > 0
                                && c.Video.MaxConnectionsPerIp < c.Video.MaxConnections;
                    return (bounded, $"{c.Video.MaxConnections} total / {c.Video.MaxConnectionsPerIp} per IP");
                });
            }
            finally
            {
                Features.Media.KmhVideoRelay.ConfigureLimits(
                    live?.Video.MaxConnections ?? Features.Media.KmhVideoRelay.DefaultMaxConnections,
                    live?.Video.MaxConnectionsPerIp ?? Features.Media.KmhVideoRelay.DefaultMaxPerIp);
            }
        }
    }
}
