using System;
using System.Collections.Generic;

namespace KMHServerAddon.Features.Global
{
    // A revoked server must stop acting registered at once and stay revoked across every later beat.
    internal static class KmhGlobalRevokeSelfTest
    {
        private const string Revoked      = "KMH-SRV-319";
        private const string SequenceGap  = "KMH-SRV-305";
        private const string WrongAccount = "KMH-SRV-302";

        private static GlobalCallResult Refused(string code, params (string K, string V)[] args)
        {
            GlobalCallResult r = new GlobalCallResult
            { Ok = false, Status = 403, ErrorCode = code, CorrelationId = "selftest-corr" };
            foreach ((string k, string v) in args) r.Args[k] = v;
            return r;
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { KmhGlobalRegistrar.ClearRevokedForTest(); KmhGlobalRegistrar.Reset(); }
            }

            Check("Global: a revoked registration stops the server acting registered", () =>
            {
                KmhGlobalRegistrar.Reset();
                KmhGlobalRegistrar.ClearRevokedForTest();
                KmhGlobalRegistrar.NoteSuccess();
                bool liveBefore = KmhGlobalRegistrar.State == GlobalLinkState.Live
                               && !KmhGlobalRegistrar.IsRevoked;

                KmhGlobalRegistrar.NoteFailure(Refused(Revoked));
                bool revoked = KmhGlobalRegistrar.IsRevoked;
                bool refusedState = KmhGlobalRegistrar.State == GlobalLinkState.Refused;

                bool ok = liveBefore && revoked && refusedState;
                return (ok, ok ? "one refusal carrying 319 is enough to stop"
                              : $"liveBefore={liveBefore}, revoked={revoked}, state={KmhGlobalRegistrar.State} "
                                + "- A REVOKED SERVER KEEPS BEATING A DEAD KEY");
            });

            Check("Global: only a revocation revokes - every other refusal leaves the link alive", () =>
            {
                // Other refusals are recoverable; treating one as a revocation would delist a working server for good.
                var stillLinked = new List<string>();
                foreach (string code in new[] { WrongAccount, "KMH-SRV-303", "KMH-SRV-309", "KMH-SRV-317",
                                                "KMH-SRV-316", "KMH-SRV-CLOCK-SKEW", "KMH-SRV-999" })
                {
                    KmhGlobalRegistrar.Reset();
                    KmhGlobalRegistrar.ClearRevokedForTest();
                    KmhGlobalRegistrar.NoteFailure(Refused(code));
                    if (KmhGlobalRegistrar.IsRevoked) stillLinked.Add(code);
                }
                KmhGlobalRegistrar.Reset();
                KmhGlobalRegistrar.ClearRevokedForTest();
                KmhGlobalRegistrar.NoteFailure(GlobalCallResult.Offline("no route"));
                bool outageOk = !KmhGlobalRegistrar.IsRevoked
                             && KmhGlobalRegistrar.State == GlobalLinkState.Unreachable;

                bool ok = stillLinked.Count == 0 && outageOk;
                return (ok, ok ? "7 refusal codes and an outage, none of them a revocation"
                              : $"treated as revoked: {string.Join(", ", stillLinked)}; outageHandled={outageOk}");
            });

            Check("Global: a revocation is sticky and is announced once, not on every beat", () =>
            {
                KmhGlobalRegistrar.Reset();
                KmhGlobalRegistrar.ClearRevokedForTest();
                KmhGlobalRegistrar.NoteFailure(Refused(Revoked));
                bool first = KmhGlobalRegistrar.IsRevoked;

                for (int i = 0; i < 3; i++) KmhGlobalRegistrar.NoteFailure(Refused(Revoked));
                KmhGlobalRegistrar.NoteSuccess();
                bool stillRevoked = KmhGlobalRegistrar.IsRevoked;

                bool ok = first && stillRevoked;
                return (ok, ok ? "revocation survives repeats and a later success; only an unlink clears it"
                              : $"revokedOnFirst={first}, stillRevokedAfterSuccess={stillRevoked}");
            });

            Check("Global: the owner is told what a revocation means, not just that something failed", () =>
            {
                string text = KmhGlobalRegistrar.DescribeRefusalFor(Revoked);
                bool namesIt = text.IndexOf("revoked", StringComparison.OrdinalIgnoreCase) >= 0;
                bool saysGameIsFine = text.IndexOf("game server is unaffected", StringComparison.OrdinalIgnoreCase) >= 0;
                // A revoked id cannot re-register, so "unlink then setup" is a dead end; only the operator's rebind works.
                bool saysFinal = text.IndexOf("revocation is final", StringComparison.OrdinalIgnoreCase) >= 0
                              && text.IndexOf("operator", StringComparison.OrdinalIgnoreCase) >= 0;
                bool namesRemedy = text.IndexOf("kmh global rebind", StringComparison.Ordinal) >= 0;
                bool noDeadEnd = text.IndexOf("unlink", StringComparison.OrdinalIgnoreCase) < 0;
                bool ok = namesIt && saysGameIsFine && saysFinal && namesRemedy && noDeadEnd;
                return (ok, ok ? "names the cause, the blast radius, that an owner's revocation is final, and the operator's remedy"
                              : $"namesRevocation={namesIt}, saysUnaffected={saysGameIsFine}, saysFinal={saysFinal}, "
                                + $"namesRemedy={namesRemedy}, noDeadEnd={noDeadEnd}");
            });

            Check("Global: a sequence Global has already seen is adopted, not retried forever", () =>
            {
                KmhGlobalRegistrar.Reset();
                KmhGlobalRegistrar.ClearRevokedForTest();
                long mine = KmhGlobalRegistrar.NextSequence();

                KmhGlobalRegistrar.NoteFailure(Refused(SequenceGap, ("last_accepted", "500")));
                long next = KmhGlobalRegistrar.NextSequence();

                KmhGlobalRegistrar.NoteFailure(Refused(SequenceGap, ("last_accepted", "not a number")));
                long afterJunk = KmhGlobalRegistrar.NextSequence();
                KmhGlobalRegistrar.NoteFailure(Refused(SequenceGap));
                long afterMissing = KmhGlobalRegistrar.NextSequence();

                // Only ever forward: an older number must not rewind it into replaying.
                KmhGlobalRegistrar.NoteFailure(Refused(SequenceGap, ("last_accepted", "5")));
                long afterOlder = KmhGlobalRegistrar.NextSequence();

                bool ok = mine == 1 && next == 501 && afterJunk == 502 && afterMissing == 503 && afterOlder == 504;
                return (ok, ok ? "adopted 500 -> next 501; junk, absence and an older number all ignored"
                              : $"mine={mine}, afterHint={next} (expected 501), afterJunk={afterJunk}, "
                                + $"afterMissing={afterMissing}, afterOlder={afterOlder}");
            });

            Check("Global: a run of failures backs off instead of retrying at the same rate", () =>
            {
                KmhGlobalRegistrar.Reset();
                KmhGlobalRegistrar.ClearRevokedForTest();
                var seen = new List<int>();
                for (int i = 0; i < 8; i++)
                {
                    KmhGlobalRegistrar.NoteFailure(GlobalCallResult.Offline("no route"));
                    seen.Add(KmhGlobalRegistrar.NextRetrySeconds);
                }
                bool grows = seen[0] < seen[^1];
                bool capped = seen[^1] == seen[^2];
                // The floor sits below seen[0]: one failure has already advanced the table by then.
                KmhGlobalRegistrar.Reset();
                int floor = KmhGlobalRegistrar.NextRetrySeconds;
                KmhGlobalRegistrar.NoteFailure(GlobalCallResult.Offline("no route"));
                KmhGlobalRegistrar.NoteSuccess();
                bool cleared = KmhGlobalRegistrar.NextRetrySeconds == floor && floor < seen[^1];

                bool ok = grows && capped && cleared;
                return (ok, ok ? $"backoff {string.Join("/", seen)}s, reset to {KmhGlobalRegistrar.NextRetrySeconds}s on success"
                              : $"grows={grows}, capped={capped}, clearedOnSuccess={cleared} ({string.Join("/", seen)})");
            });

            Check("Global: a revocation stops the heartbeat service and announces itself once", () =>
            {
                // Side effects invisible in registrar state, so only an IL check catches their removal.
                List<string> calls = Maintenance.KmhIlReader.CallsMadeBy(
                    "KMHServerAddon.Features.Global.KmhGlobalRegistrar", "Revoked");
                bool stopsService = calls.Contains("Stop");
                // The early-out reads the flag before setting it, so a repeat beat cannot re-announce.
                bool readsFlagFirst = calls.Contains("get_IsRevoked");
                bool warns = calls.Contains("Warn");

                bool ok = stopsService && readsFlagFirst && warns;
                return (ok, ok ? "stops the service, warns the owner, and only on the first refusal"
                              : $"stopsService={stopsService}, checksFlagBeforeSetting={readsFlagFirst}, warns={warns} "
                                + $"(calls: {string.Join(", ", calls)})");
            });

            Check("Global: an outage reads as unreachable, never as a refusal", () =>
            {
                // Unreachable retries; refused needs an owner.
                KmhGlobalRegistrar.Reset();
                KmhGlobalRegistrar.ClearRevokedForTest();
                KmhGlobalRegistrar.NoteFailure(GlobalCallResult.Offline("dns"));
                bool unreachable = KmhGlobalRegistrar.State == GlobalLinkState.Unreachable;
                string text = KmhGlobalRegistrar.OwnerDiagnostic();
                bool saysRetrying = text.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0
                                 && text.IndexOf("retrying", StringComparison.OrdinalIgnoreCase) >= 0;

                KmhGlobalRegistrar.NoteFailure(Refused(WrongAccount));
                bool refused = KmhGlobalRegistrar.State == GlobalLinkState.Refused;

                bool ok = unreachable && saysRetrying && refused;
                return (ok, ok ? "an outage retries; a refusal is reported to the owner"
                              : $"unreachable={unreachable}, saysRetrying={saysRetrying}, refused={refused}");
            });

            return r;
        }
    }
}
