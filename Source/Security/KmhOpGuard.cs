using System;

namespace KMHServerAddon.Security
{
    // A value-bearing request can arrive twice on one op id - a transport re-send, a second click. Operations whose repeat is already harmless skip this.
    internal static class KmhOpGuard
    {
        private static readonly KmhSeenGuard _claimed = new KmhSeenGuard(TimeSpan.FromMinutes(10));

        // The actor is in the key because op ids come from the client - otherwise one client could claim ids another is about to use.
        private static string KeyOf(string scope, string actor, string opId)
            => scope + "|" + (actor ?? "").ToLowerInvariant() + "|" + opId;

        // An empty id is an older client with no op id to dedup on, and is never suppressed.
        public static bool TryBegin(string scope, string actor, string opId, long nowTicks)
            => string.IsNullOrEmpty(opId) || _claimed.MarkIfNew(KeyOf(scope, actor, opId), nowTicks);

        public static bool TryBegin(string scope, string actor, string opId)
            => TryBegin(scope, actor, opId, DateTime.UtcNow.Ticks);

        // Nothing moved, so the id must work again - otherwise a withdrawal refused for want of silver stays refused ten minutes after a top-up.
        public static void Release(string scope, string actor, string opId)
        {
            if (!string.IsNullOrEmpty(opId)) _claimed.Release(KeyOf(scope, actor, opId));
        }

        internal static int Count => _claimed.Count;
    }

    // Carries scope, actor and id together so a handler cannot release under a different scope than it claimed.
    internal readonly struct KmhOpClaim
    {
        private readonly string _scope;
        private readonly string _actor;
        private readonly string _id;

        public KmhOpClaim(string scope, string actor, SubProtocol.KmhEnvelope env)
        {
            _scope = scope;
            _actor = actor;
            _id    = env?.OpId ?? "";
        }

        // Falls back to the payload field so a client built before the envelope carried an op id keeps its dedup.
        public KmhOpClaim(string scope, string actor, SubProtocol.KmhEnvelope env, string legacyIdField)
        {
            _scope = scope;
            _actor = actor;
            _id    = string.IsNullOrEmpty(env?.OpId) ? (env?.GetString(legacyIdField) ?? "") : env.OpId;
        }

        public bool Begin() => KmhOpGuard.TryBegin(_scope, _actor, _id);

        public void Release() => KmhOpGuard.Release(_scope, _actor, _id);
    }
}
