using System;
using KMHServerAddon.Features.Transport;

namespace KMHServerAddon.Maintenance
{
    // `kmh transport-test`: non-mutating security self-check of the API transport. Exercises the DoS guards on
    // throwaway instances, checks the secure-by-default config, and reports the live transport's exposure.
    internal static class KmhTransportSecurityTest
    {
        public static void Run(Action<string> reply)
        {
            int pass = 0, fail = 0;
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try
                {
                    (bool ok, string detail) = probe();
                    if (ok) { pass++; reply($"  PASS  {name}{(string.IsNullOrEmpty(detail) ? "" : $" - {detail}")}"); }
                    else    { fail++; reply($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : $" - {detail}")}"); }
                }
                catch (Exception ex) { fail++; reply($"  FAIL  {name} - threw: {ex.Message}"); }
            }

            reply("=== KMH transport security test (non-mutating) ===");

            // --- config contract: public-by-default is allowed ONLY because every safety system defaults on ---
            Check("Default posture is locked down", () =>
            {
                var c = new TransportConfig();
                bool safetyOn = c.RequireKmhApiAuth
                             && c.MaxConnections > 0 && c.MaxConnectionsPerIp > 0
                             && c.AuthTimeoutSeconds > 0 && c.IdleTimeoutSeconds > 0
                             && c.MaxFrameKb > 0
                             && c.FailedAuthPerIp > 0 && c.FailedAuthWindowSeconds > 0 && c.FailedAuthBlockSeconds > 0;
                return (safetyOn, safetyOn
                    ? $"bind={c.BindAddress} with auth+caps+throttle+timeouts+frame limits all required"
                    : "A SAFETY DEFAULT IS OFF - public default bind is not acceptable like this");
            });
            Check("Default auth is on", () =>
            {
                var c = new TransportConfig();
                return (c.RequireKmhApiAuth, c.RequireKmhApiAuth ? "RequireKmhApiAuth=true" : "AUTH OFF BY DEFAULT");
            });
            Check("IsPublicBind classifies addresses", () =>
            {
                bool loop4 = !new TransportConfig { BindAddress = "127.0.0.1" }.IsPublicBind;
                bool loop6 = !new TransportConfig { BindAddress = "::1" }.IsPublicBind;
                bool anyV4 =  new TransportConfig { BindAddress = "0.0.0.0" }.IsPublicBind;
                bool lan   =  new TransportConfig { BindAddress = "192.168.1.50" }.IsPublicBind;
                return (loop4 && loop6 && anyV4 && lan, "127.0.0.1/::1 local, 0.0.0.0/LAN public");
            });
            Check("Clamp repairs unsafe values", () =>
            {
                // out-of-range/blank values must snap to safe defaults rather than disabling a guard
                var c = TransportConfig.ClampForTest(new TransportConfig
                {
                    BindAddress = "  ", KmhApiPort = 0, MaxConnections = -5,
                    MaxConnectionsPerIp = 0, MaxFrameKb = 99999, FailedAuthPerIp = 0
                });
                bool bindOk  = !c.IsPublicBind;                 // blank -> loopback, never 0.0.0.0
                bool portOk  = c.KmhApiPort >= 1 && c.KmhApiPort <= 65535;
                bool connOk  = c.MaxConnections >= 1;
                bool perIpOk = c.MaxConnectionsPerIp >= 1 && c.MaxConnectionsPerIp <= c.MaxConnections;
                bool frameOk = c.MaxFrameKb >= 1 && c.MaxFrameKb <= 1024;
                bool failOk  = c.FailedAuthPerIp >= 1;
                return (bindOk && portOk && connOk && perIpOk && frameOk && failOk,
                        $"bind={c.BindAddress} port={c.KmhApiPort} conn={c.MaxConnections} perIp={c.MaxConnectionsPerIp} frameKb={c.MaxFrameKb} failPerIp={c.FailedAuthPerIp}");
            });

            // --- connection caps ---
            Check("Per-IP cap blocks the (N+1)th socket", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 3);
                bool a = lim.TryAdmit("1.1.1.1");
                bool b = lim.TryAdmit("1.1.1.1");
                bool c = lim.TryAdmit("1.1.1.1");
                bool d = lim.TryAdmit("1.1.1.1");   // 4th from same IP -> refused
                return (a && b && c && !d && lim.PerIp("1.1.1.1") == 3, "3 admitted, 4th refused");
            });
            Check("Release frees per-IP capacity", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 2);
                lim.TryAdmit("2.2.2.2"); lim.TryAdmit("2.2.2.2");
                bool blockedAtCap = !lim.TryAdmit("2.2.2.2");
                lim.Release("2.2.2.2");
                bool admittedAfterRelease = lim.TryAdmit("2.2.2.2");
                return (blockedAtCap && admittedAfterRelease, "freed slot reusable");
            });
            Check("Global cap blocks across IPs", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 2, maxPerIp: 10);
                bool a = lim.TryAdmit("a"); bool b = lim.TryAdmit("b");
                bool c = lim.TryAdmit("c");   // global cap of 2 hit -> refused even from a fresh IP
                return (a && b && !c && lim.Total == 2, "2 total admitted, 3rd refused");
            });
            Check("A refused socket leaks no capacity", () =>
            {
                var lim = new ConnectionLimiter(maxTotal: 100, maxPerIp: 1);
                lim.TryAdmit("3.3.3.3");
                lim.TryAdmit("3.3.3.3");   // refused
                lim.TryAdmit("3.3.3.3");   // refused
                return (lim.Total == 1 && lim.PerIp("3.3.3.3") == 1, $"total={lim.Total}");
            });

            // --- failed-auth throttle (fake clock so the test is instant and deterministic) ---
            Check("Throttle trips at the threshold", () =>
            {
                long t = 0; var th = new AuthFailThrottle(threshold: 5, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                bool trippedEarly = false;
                for (int i = 0; i < 4; i++) trippedEarly |= th.NoteFailure("4.4.4.4", out _);
                bool trippedAt5 = th.NoteFailure("4.4.4.4", out int total);
                return (!trippedEarly && trippedAt5 && th.IsBlocked("4.4.4.4") && total == 5, "blocked on the 5th failure");
            });
            Check("Block expires after the block window", () =>
            {
                long t = 0; long sec = TimeSpan.FromSeconds(1).Ticks;
                var th = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                for (int i = 0; i < 3; i++) th.NoteFailure("5.5.5.5", out _);
                bool blockedNow = th.IsBlocked("5.5.5.5");
                t += 301 * sec;                                   // advance past the 300s block
                bool freeAfter = !th.IsBlocked("5.5.5.5");
                return (blockedNow && freeAfter, "300s block then clear");
            });
            Check("Failures age out of the window", () =>
            {
                long t = 0; long sec = TimeSpan.FromSeconds(1).Ticks;
                var th = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                th.NoteFailure("6.6.6.6", out _); th.NoteFailure("6.6.6.6", out _);   // 2 in window
                t += 61 * sec;                                   // window elapses -> counter resets
                bool tripped = th.NoteFailure("6.6.6.6", out int total);
                return (!tripped && total == 1, "stale failures don't accumulate");
            });
            Check("Clean auth clears the streak", () =>
            {
                long t = 0; var th = new AuthFailThrottle(threshold: 3, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                th.NoteFailure("7.7.7.7", out _); th.NoteFailure("7.7.7.7", out _);
                th.Clear("7.7.7.7");                             // a successful login resets the IP
                bool tripped = th.NoteFailure("7.7.7.7", out int total);
                return (!tripped && total == 1, "streak reset after success");
            });
            Check("One IP's block doesn't affect others", () =>
            {
                long t = 0; var th = new AuthFailThrottle(threshold: 2, windowSeconds: 60, blockSeconds: 300, nowTicks: () => t);
                th.NoteFailure("8.8.8.8", out _); th.NoteFailure("8.8.8.8", out _);
                return (th.IsBlocked("8.8.8.8") && !th.IsBlocked("9.9.9.9"), "blocks are per-IP");
            });

            // --- live transport posture (reports exposure; only fails on the dangerous combination) ---
            Check("Live transport posture", () =>
            {
                TransportConfig live = TransportConfig.Current;
                if (!live.EnableKmhApiTransport) return (true, "API transport OFF - chat path only");
                string where = $"{live.BindAddress}:{live.KmhApiPort}";
                string auth  = live.RequireKmhApiAuth ? "auth required" : "AUTH OFF";
                if (live.IsPublicBind && !live.RequireKmhApiAuth)
                    return (false, $"PUBLIC BIND + AUTH OFF on {where} - anyone reachable can act as any player");
                string note = live.IsPublicBind ? "public bind (default; forward the port for remote KMH)" : "loopback";
                return (true, $"{where} - {auth}, {note}");
            });

            reply(fail == 0
                ? $"=== RESULT: PASS ({pass}/{pass + fail} checks) ==="
                : $"=== RESULT: FAIL ({fail} of {pass + fail} checks failed - see above) ===");
        }
    }
}
