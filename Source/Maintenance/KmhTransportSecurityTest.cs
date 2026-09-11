using System;
using KMHServerAddon.Features.Transport;

namespace KMHServerAddon.Maintenance
{
    // Non-mutating: the guards run on throwaway instances, so an owner can check a live server safely.
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

            // The pure checks live in the guards suite so `kmh smoke` runs them too.
            foreach ((string name, bool ok, string detail) in KmhTransportGuardsSelfTest.Run())
            {
                if (ok) { pass++; reply($"  PASS  {name}{(string.IsNullOrEmpty(detail) ? "" : $" - {detail}")}"); }
                else    { fail++; reply($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : $" - {detail}")}"); }
            }

            // Reports exposure, and only fails on the dangerous combination.
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
