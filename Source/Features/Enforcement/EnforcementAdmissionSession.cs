using System;

namespace KMHServerAddon.Features.Enforcement
{
    // A ticket belongs to one authenticated connection. The caller serializes policy changes with this session.
    internal sealed class EnforcementAdmissionSession
    {
        internal enum Phase { AwaitingHello, AwaitingVerification, Verified, Released, Failed }
        internal readonly object Sync = new object();
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly string ServerId;
        internal readonly string ProfileHash;
        internal readonly long Generation;
        internal readonly string Policy;
        internal Phase State { get; private set; } = Phase.AwaitingHello;

        internal EnforcementAdmissionSession(string serverId, string profileHash, long generation, string policy = "")
        {
            ServerId = serverId;
            ProfileHash = profileHash ?? "";
            Generation = generation;
            Policy = policy;
        }

        internal bool AcceptHello(bool capable)
        {
            lock (Sync)
            {
                if (State == Phase.Failed || State == Phase.Released) return false;
                if (!capable) { State = Phase.Failed; return false; }
                State = Phase.AwaitingVerification;
                return true;
            }
        }

        internal bool TryRelease(string id, string serverId, string hash, long generation, bool active)
        {
            lock (Sync)
            {
                if (State != Phase.AwaitingVerification || !active || id != Id || serverId != ServerId
                    || hash != ProfileHash || generation != Generation) return false;
                State = Phase.Verified;
                State = Phase.Released;
                return true;
            }
        }

        internal void Fail() { lock (Sync) State = Phase.Failed; }
    }
}
