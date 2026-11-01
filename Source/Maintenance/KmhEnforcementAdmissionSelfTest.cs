using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KMHServerAddon.Features.Enforcement;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhEnforcementAdmissionSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var result = new List<(string, bool, string)>();
            void Check(string name, bool ok) => result.Add((name, ok, ok ? "verified" : "failed"));
            EnforcementAdmissionSession Fresh() => new("server", "profile", 11);
            bool Release(EnforcementAdmissionSession s) => s.TryRelease(s.Id, "server", "profile", 11, true);
            var session = Fresh();
            Check("EnforcementPostLoginHeldUntilVerified", !Release(session));
            session.AcceptHello(true);
            Check("StaleAdmissionAckCannotRelease", !session.TryRelease("stale", "server", "profile", 11, true));
            Check("OldConnectionAckCannotReleaseNewConnection", !session.TryRelease(session.Id, "server", "profile", 10, true));
            Check("WrongServerCannotRelease", !session.TryRelease(session.Id, "other", "profile", 11, true));
            Check("HashMismatchNeverReleasesPostLogin", !session.TryRelease(session.Id, "server", "other", 11, true));
            Check("ApplyFailureNeverReleasesPostLogin", !session.TryRelease(session.Id, "server", "profile", 11, false));
            int released = 0;
            Parallel.For(0, 64, _ => { if (Release(session)) System.Threading.Interlocked.Increment(ref released); });
            Check("VerifiedAdmissionReleasesPostLoginOnce", released == 1);
            Check("DuplicateVerificationDoesNotReleaseTwice", !Release(session));
            Check("DuplicateHelloCannotReopenReleasedSession", !session.AcceptHello(true) && !Release(session));
            session = Fresh();
            Check("MissingKmhCapabilityNeverReleasesEnforcedServer", !session.AcceptHello(false) && !Release(session));
            session = Fresh(); session.AcceptHello(true); session.Fail();
            Check("DeclineNeverReleasesPostLogin", !Release(session));
            var next = Fresh(); next.AcceptHello(true);
            Check("OldTicketCannotReleaseNewSession", !next.TryRelease(session.Id, "server", "profile", 11, true));
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var post = typeof(PM_Login).GetMethod("PostLogin", flags, null, new[] { typeof(ServerClient) }, null);
            Check("RwtInteropExactPostLoginSignatureResolves", post != null && post.ReturnType == typeof(void));
            var presence = typeof(PM_Mods).GetMethods(flags).Where(m => m.Name == "CheckIfModConflict").ToArray();
            Check("RwtRequiredModPresenceStillExists", presence.Length == 1 && presence[0].ReturnType == typeof(bool));
            Check("RwtPresenceIsNotAnAdmissionPatchTarget", !typeof(EnforcementAdmission).GetMethods(flags).Any(m => m.Name.Contains("ModConflict")));
            Check("RwtInteropUnknownSignatureFailsClosed", !EnforcementAdmission.IsConfigCheckSignature(null)
                && !EnforcementAdmission.IsConfigCheckSignature(post)
                && !EnforcementAdmission.LegacyPresenceOnlyLayout(typeof(KmhEnforcementAdmissionSelfTest), typeof(KmhEnforcementAdmissionSelfTest)));
            Check("RwtBrowserReportPassesAdmissionHold", EnforcementAdmission.AllowedWhileHeld("ServerBrowserTelemetry")
                && !EnforcementAdmission.AllowedWhileHeld("World"));
            Check("AdmissionFilterNeverCallsTargetClientDirectly", RwtCompat.TargetClientResolved
                && KmhIlReader.CallsMadeBy(typeof(EnforcementAdmission).FullName, "FilterPacket", 3, "get_TargetClient").Count == 0);
            bool reads;
            try
            {
                var listener = (Listener)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Listener));
                var client = (ServerClient)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ServerClient));
                reads = RwtCompat.TargetClientOf(listener) == null;
                const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var set = typeof(Listener).GetProperty("TargetClient", inst)?.GetSetMethod(true);
                if (set != null) set.Invoke(listener, new object[] { client });
                else (typeof(Listener).GetField("<TargetClient>k__BackingField", inst) ?? typeof(Listener).GetField("TargetClient", inst)).SetValue(listener, client);
                reads &= ReferenceEquals(RwtCompat.TargetClientOf(listener), client);
            }
            catch (Exception) { reads = false; }
            Check("RwtListenerTargetClientReadsWhateverItsAccess", reads);
            Check("RwtGetterReadsPrivatePropertyAndFieldOnly", RwtCompat.InstanceGetter<ProbeChild, string>("Hidden")?.Invoke(new ProbeChild("f")) == "p"
                && RwtCompat.InstanceGetter<ProbeChild, string>("_field")?.Invoke(new ProbeChild("f")) == "f"
                && RwtCompat.InstanceGetter<ProbeChild, string>("Missing") == null
                && RwtCompat.InstanceGetter<ProbeChild, int>("Hidden") == null);
            foreach (bool enabled in new[] { false, true })
            {
                bool resultValue = true;
                bool originalRuns = EnforcementAdmission.RunNativeConfigCheck(enabled, ref resultValue);
                Check(enabled ? "RwtConfigCheckSuppressedOnlyWhenKmhEnforcementEnabled" : "RwtConfigCheckRunsNormallyWhenKmhEnforcementDisabled",
                    originalRuns == !enabled && (!enabled || !resultValue));
                Check(enabled ? "RwtOrderCheckSuppressedOnlyWhenKmhEnforcementEnabled" : "RwtOrderCheckRunsNormallyWhenKmhEnforcementDisabled",
                    originalRuns == !enabled);
            }
            return result;
        }

        private class Probe { private string Hidden => "p"; }
        private sealed class ProbeChild : Probe
        {
            private readonly string _field;
            public ProbeChild(string field) { _field = field; }
            public override string ToString() => _field;
        }
    }
}
