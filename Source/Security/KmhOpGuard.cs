using System;

namespace KMHServerAddon.Security
{
    // A value-bearing request can arrive twice on one op id (a re-send, a second click); harmless repeats skip this.
    internal static class KmhOpGuard
    {
        private static readonly KmhSeenGuard _claimed = new KmhSeenGuard(TimeSpan.FromMinutes(10));

        // Keyed by actor because op ids are client-chosen, or one client could claim ids another is about to use.
        // A long id is hashed: it is held ten minutes, and its length is the client's choice.
        internal static string KeyOf(string scope, string actor, string opId)
            => scope + "|" + (actor ?? "").ToLowerInvariant() + "|" + (opId != null && opId.Length > MaxRawIdLength ? "#" + Hash(opId) : opId);

        internal const int MaxRawIdLength = 64;

        private static string Hash(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).Replace("-", "");
        }

        // An empty id is an older client with no op id to dedup on, and is never suppressed.
        public static bool TryBegin(string scope, string actor, string opId, long nowTicks)
            => string.IsNullOrEmpty(opId) || _claimed.MarkIfNew(KeyOf(scope, actor, opId), nowTicks);

        public static bool TryBegin(string scope, string actor, string opId)
            => TryBegin(scope, actor, opId, DateTime.UtcNow.Ticks);

        // Nothing moved, so the id must work again, or a refused withdrawal stays refused ten minutes after a top-up.
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
