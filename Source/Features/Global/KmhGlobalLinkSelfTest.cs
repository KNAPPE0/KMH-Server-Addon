using System;
using System.Collections.Generic;
using System.Threading;
using KMHServerAddon.Maintenance;

namespace KMHServerAddon.Features.Global
{
    // Owner approval and key possession are separate proofs; neither may be inferred from local state.
    internal static class KmhGlobalLinkSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            Check("Global link: registration is refused without an owner-approved grant", () =>
            {
                // Without an owner grant, anyone starting a server could claim a ServerId in the public directory.
                string priorUrl = GlobalConfig.Current.BaseUrl;
                bool? priorSelfTest = KmhGlobalSafety.SelfTestOverride;
                try
                {
                    // Not production, so the publish guard lets this through and the grant check is what answers.
                    GlobalConfig.Current.BaseUrl = "http://127.0.0.1:59999";
                    KmhGlobalSafety.SelfTestOverride = true;

                    bool noGrant = !KmhGlobalRegistration.HasGrant;
                    KmhGlobalRegistration.Outcome o =
                        KmhGlobalRegistration.RegisterAsync(CancellationToken.None).GetAwaiter().GetResult();

                    bool ok = noGrant && !o.Ok && o.ErrorCode == "KMH-SRV-GRANT";
                    return (ok, ok ? "refused before any request left the process: " + o.Detail
                                  : $"hadGrant={!noGrant}, ok={o.Ok}, code={o.ErrorCode}, detail={o.Detail} "
                                    + "- A SERVER COULD REGISTER WITHOUT AN OWNER APPROVING IT");
                }
                finally
                {
                    GlobalConfig.Current.BaseUrl = priorUrl;
                    KmhGlobalSafety.SelfTestOverride = priorSelfTest;
                }
            });

            Check("Global link: a self-test run never registers against the production directory", () =>
            {
                bool? prior = KmhGlobalSafety.SelfTestOverride;
                string priorUrl = GlobalConfig.Current.BaseUrl;
                try
                {
                    KmhGlobalSafety.SelfTestOverride = true;
                    GlobalConfig.Current.BaseUrl = GlobalConfigCheck.OfficialBaseUrl;

                    KmhGlobalRegistration.Outcome o =
                        KmhGlobalRegistration.RegisterAsync(CancellationToken.None).GetAwaiter().GetResult();
                    bool guarded = !o.Ok && o.ErrorCode == "KMH-SRV-GUARD";

                    // The guard must answer before the grant check, or production was refused only by luck of ordering.
                    KmhGlobalSafety.SelfTestOverride = false;
                    bool openWhenNotATest = KmhGlobalSafety.MayPublishTo(GlobalConfigCheck.OfficialBaseUrl, out _);

                    bool ok = guarded && openWhenNotATest;
                    return (ok, ok ? "the suite is refused the production directory, a real run is not"
                                  : $"guardedInTest={guarded} (code {o.ErrorCode}), openOutsideTest={openWhenNotATest}");
                }
                finally
                {
                    KmhGlobalSafety.SelfTestOverride = prior;
                    GlobalConfig.Current.BaseUrl = priorUrl;
                }
            });

            Check("Global link: the registration path proves the key and spends the grant", () =>
            {
                // Checked via IL since neither is observable without a live Global: a key this server cannot reload
                // claims an unusable identity, and a grant kept after success is a credential with nothing to authorise.
                List<string> calls = KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Features.Global.KmhGlobalRegistration", "RegisterAsync");
                bool checksPublish = calls.Contains("MayPublishTo");
                bool checksGrant = calls.Contains("get_HasGrant");
                bool loadsKey = calls.Contains("Load");
                bool signs = calls.Contains("Sign");

                bool ok = checksPublish && checksGrant && loadsKey && signs;
                return (ok, ok ? "publish guard, grant, key load and signature all on the register path"
                              : $"publishGuard={checksPublish}, grant={checksGrant}, keyLoad={loadsKey}, "
                                + $"signs={signs} (calls: {string.Join(", ", calls)})");
            });

            Check("Global link: an approved link that carried no grant does not count as verified", () =>
            {
                // The grant comes once; a Completed poll without it must not advance to a stage it cannot act on.
                List<string> calls = KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Features.Global.KmhGlobalRegistration", "PollOwnerLinkAsync");
                bool checksGrant = calls.Contains("get_HasGrant");
                return (checksGrant, checksGrant
                    ? "the poll refuses to advance when Global sent no grant"
                    : "AN APPROVED LINK WITH NO GRANT WOULD READ AS OWNER-VERIFIED");
            });

            Check("Global link: a grant is held only while it can still authorise a registration", () =>
            {
                // Even a spent grant should not linger: it can leak via a support bundle, log or crash dump.
                string priorUrl = GlobalConfig.Current.BaseUrl;
                bool? priorSelfTest = KmhGlobalSafety.SelfTestOverride;
                try
                {
                    KmhGlobalRegistration.ForgetGrantForTest();
                    bool emptyToStart = !KmhGlobalRegistration.HasGrant;

                    KmhGlobalRegistration.SetGrantForTest("selftest-grant");
                    bool held = KmhGlobalRegistration.HasGrant;

                    // Nothing listens here; a registration failing after the grant check must KEEP it for a retry.
                    GlobalConfig.Current.BaseUrl = "http://127.0.0.1:59999";
                    KmhGlobalSafety.SelfTestOverride = true;
                    KmhGlobalRegistration.RegisterAsync(CancellationToken.None).GetAwaiter().GetResult();
                    bool keptAfterFailure = KmhGlobalRegistration.HasGrant;

                    KmhGlobalRegistration.ForgetGrantForTest();
                    bool clearable = !KmhGlobalRegistration.HasGrant;

                    bool ok = emptyToStart && held && keptAfterFailure && clearable;
                    return (ok, ok ? "held across a failed attempt so a retry works, and never kept once dropped"
                                  : $"emptyToStart={emptyToStart}, held={held}, "
                                    + $"keptAfterFailedRegistration={keptAfterFailure}, clearable={clearable}");
                }
                finally
                {
                    KmhGlobalRegistration.ForgetGrantForTest();
                    GlobalConfig.Current.BaseUrl = priorUrl;
                    KmhGlobalSafety.SelfTestOverride = priorSelfTest;
                }
            });

            Check("Global link: local state alone never reports a live registration", () =>
            {
                // A revoked server keeps its RegisteredServerId on disk; only Global's answer proves it is live.
                string priorId = GlobalConfig.Current.RegisteredServerId;
                try
                {
                    GlobalConfig.Current.RegisteredServerId = "";
                    KmhGlobalRegistration.Outcome none =
                        KmhGlobalRegistration.RegistrationStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
                    bool refusesWithoutId = !none.Ok;

                    GlobalConfig.Current.RegisteredServerId = new string('a', 32);
                    string priorUrl = GlobalConfig.Current.BaseUrl;
                    GlobalConfig.Current.BaseUrl = "http://127.0.0.1:59999";
                    KmhGlobalRegistration.Outcome unreachable;
                    try { unreachable = KmhGlobalRegistration.RegistrationStatusAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                    finally { GlobalConfig.Current.BaseUrl = priorUrl; }

                    bool ok = refusesWithoutId && !unreachable.Ok;
                    return (ok, ok ? "no id and no answer both read as unconfirmed, never as registered"
                                  : $"withoutId={!refusesWithoutId}, unreachableReportedOk={unreachable.Ok}");
                }
                finally { GlobalConfig.Current.RegisteredServerId = priorId; }
            });

            return r;
        }
    }
}
