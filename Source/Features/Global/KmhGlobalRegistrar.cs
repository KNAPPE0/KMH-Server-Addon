using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // A directory outage is a directory outage: the game server keeps running and nothing is disabled.
    internal enum GlobalLinkState { Disabled, NotRegistered, Registering, Live, Unreachable, Refused }

    internal static class KmhGlobalRegistrar
    {
        public static GlobalLinkState State { get; private set; } = GlobalLinkState.Disabled;
        public static string LastErrorCode { get; private set; } = "";
        public static string LastCorrelationId { get; private set; } = "";
        public static DateTime LastSuccessUtc { get; private set; }

        private static readonly object _lock = new object();
        private static long _sequence;
        private static int _consecutiveFailures;

        // Backoff, not a tighter loop: a Global that is down must not be hammered by every KMH server at once.
        private static readonly int[] BackoffSeconds = { 30, 60, 120, 300, 600 };

        public static int NextRetrySeconds
            => BackoffSeconds[Math.Min(_consecutiveFailures, BackoffSeconds.Length - 1)];

        public static void Reset()
        {
            lock (_lock)
            {
                State = GlobalLinkState.Disabled;
                LastErrorCode = ""; LastCorrelationId = "";
                _sequence = 0; _consecutiveFailures = 0;
            }
        }

        // Resumed from disk so a restart continues the sequence rather than replaying numbers Global has seen.
        public static void ResumeSequence(long lastAccepted)
        {
            lock (_lock) _sequence = Math.Max(_sequence, lastAccepted);
        }

        public static long NextSequence()
        {
            lock (_lock) return ++_sequence;
        }

        public static HeartbeatBody BuildHeartbeat(DateTimeOffset now)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            HeartbeatBody body = new HeartbeatBody
            {
                ServerId         = KmhServerIdentity.Id,
                Generation       = cfg.SigningGeneration,
                Sequence         = NextSequence(),
                IssuedAtUnix     = now.ToUnixTimeSeconds(),
                Nonce            = Guid.NewGuid().ToString("N"),
                Status           = GlobalStatusProbe.CurrentStatus(),
                Endpoint         = GlobalStatusProbe.Endpoint(),
                Players          = GlobalStatusProbe.Players(),
                MaxPlayers       = GlobalStatusProbe.MaxPlayers(),
                RimWorldVersion  = GlobalStatusProbe.RimWorldVersion(),
                RwtVersion       = GlobalStatusProbe.RwtVersion(),
                KmhVersion       = KmhVersion.Build,
                Protocol         = SubProtocol.KmhProtocol.CurrentVersion,
                Capabilities     = GlobalStatusProbe.Capabilities(),
                ManifestRevision = GlobalStatusProbe.ManifestRevision(),
                ManifestHash     = GlobalStatusProbe.ManifestHash(),
                MetadataRevision = GlobalStatusProbe.MetadataRevision(),
                RequiresPassword = Features.Mods.RwtServerGate.RequiresPassword,
            };
            body.Signature = KmhServerKey.Sign(body.Canonical());
            return body;
        }

        public static void NoteSuccess()
        {
            lock (_lock)
            {
                State = GlobalLinkState.Live;
                LastErrorCode = ""; LastCorrelationId = "";
                LastSuccessUtc = DateTime.UtcNow;
                _consecutiveFailures = 0;
            }
        }

        public static void NoteFailure(GlobalCallResult result)
        {
            lock (_lock)
            {
                State = result.Unreachable ? GlobalLinkState.Unreachable : GlobalLinkState.Refused;
                LastErrorCode = result.ErrorCode ?? "";
                LastCorrelationId = result.CorrelationId ?? "";
                if (_consecutiveFailures < int.MaxValue) _consecutiveFailures++;
            }

            // Adopt Global's last accepted sequence, or the same stale number is retried forever.
            if (result.ErrorCode == "KMH-SRV-305" && result.Args.TryGetValue("last_accepted", out string accepted)
                && long.TryParse(accepted, out long last))
                ResumeSequence(last);

            // Retrying a revoked registration cannot succeed, so stop rather than beat a dead key every minute.
            if (result.ErrorCode == "KMH-SRV-319") Revoked();
        }

        // A clock problem in particular must not read as "invalid signature".
        public static string OwnerDiagnostic()
        {
            lock (_lock)
            {
                switch (State)
                {
                    case GlobalLinkState.Disabled:
                        return "Colony Nexus Global is off. Your server runs normally and is not listed in the browser.";
                    case GlobalLinkState.Live:
                        return $"Listed in Colony Nexus Global. Last confirmed {LastSuccessUtc:yyyy-MM-dd HH:mm} UTC.";
                    case GlobalLinkState.Unreachable:
                        return $"Colony Nexus Global is unreachable. Your game server is unaffected; retrying in {NextRetrySeconds}s.";
                    case GlobalLinkState.Refused:
                        return DescribeRefusal();
                    default:
                        return "Colony Nexus Global registration has not been completed yet.";
                }
            }
        }

        // Sticky for the process: a revocation is not transient.
        public static bool IsRevoked { get; private set; }

        internal static void ClearRevokedForTest() => IsRevoked = false;

        private static void Revoked()
        {
            if (IsRevoked) return;
            IsRevoked = true;
            Diagnostics.ServerLog.Warn("Colony Nexus Global: " + Describe("KMH-SRV-319") + " Heartbeats have stopped.");
            KmhGlobalService.Stop();
        }

        public static string DescribeRefusalFor(string code) => Describe(code);

        private static string DescribeRefusal() => Describe(LastErrorCode);

        private static string Describe(string code)
        {
            switch (code)
            {
                case "KMH-SRV-CLOCK-SKEW":
                    return "This server's clock differs too much from Colony Nexus Global. Check the system time service; "
                         + "your game server is unaffected.";
                case "KMH-SRV-303":
                    return "Colony Nexus Global rejected this server's signature: the key in KMH-Data/Identity is not the one "
                         + "on record. Put back the newer KMH-Data/Identity from a backup, or run 'kmh global recover' to "
                         + "replace the key with your owner approval. Your game server is unaffected.";
                case "KMH-SRV-309":
                    return "Colony Nexus Global has a different signing key on record for this server id. If KMH-Data/Identity "
                         + "was replaced, put back the newer one from a backup, or run 'kmh global recover' if you own it. If "
                         + "this server never registered before, someone else holds its id: ask the Colony Nexus Global "
                         + "operator for a rebind code and run 'kmh global rebind <code>'.";
                case "KMH-SRV-302":
                    return "This server is registered to a different Colony Nexus account.";
                case "KMH-SRV-317":
                    return "Colony Nexus Global holds this server's identity at a newer signing generation than this KMH-Data "
                         + "records, which usually means an older KMH-Data backup was restored. Put back the newer "
                         + "KMH-Data/Identity, or run 'kmh global recover' to issue a new key with your owner approval. Your "
                         + "game server is unaffected in the meantime.";
                case "KMH-SRV-319":
                    return "This server's Colony Nexus Global registration was revoked. It is no longer listed and its signing "
                         + "key no longer authenticates. An owner's revocation is final; if the operator revoked it by "
                         + "mistake, ask them for a rebind code and run 'kmh global rebind <code>'. Your game server is unaffected.";
                case "KMH-SRV-316":
                    return "This server tried to advance its own signing generation, which only Colony Nexus Global may do. "
                         + "Nothing is wrong with your game server; the listing simply will not update until this is resolved.";
                default:
                    return $"Colony Nexus Global refused the last update ({code}). Reference {LastCorrelationId}. "
                         + "Your game server is unaffected.";
            }
        }
    }
}
