using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Sites
{
    // The first catalog establishes a fingerprint, and a differing client is ignored rather than allowed to overwrite it.
    internal static class SiteCatalogStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, SiteOutputMetadata> _byDef =
            new Dictionary<string, SiteOutputMetadata>(StringComparer.OrdinalIgnoreCase);

        private static string _fingerprint = "";
        private static string _establishedBy = "";
        private static DateTime _establishedUtc;
        private static int _rejectedPushes;
        private static bool _dirty;

        public static bool HasCatalog { get { lock (_lock) return _byDef.Count > 0; } }
        public static int  Count      { get { lock (_lock) return _byDef.Count; } }
        public static string Fingerprint   { get { lock (_lock) return _fingerprint; } }
        public static string EstablishedBy { get { lock (_lock) return _establishedBy; } }
        public static int  RejectedPushes  { get { lock (_lock) return _rejectedPushes; } }

        // A partial catalog must not read as complete, or half the modpack classifies as Unknown and disappears.
        private sealed class Pending
        {
            public string Fingerprint = "";
            public int    Total;
            public readonly HashSet<int> SeenChunks = new HashSet<int>();
            public readonly List<SiteOutputMetadata> Meta = new List<SiteOutputMetadata>();
            public DateTime StartedUtc = DateTime.UtcNow;
        }

        private static readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

        // A client that disconnects mid-stream must not hold a slot forever, and its next connect re-pushes anyway.
        private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(5);

        public enum PushResult { Accumulating, Accepted, Refreshed, Rejected, Ignored }

        public static PushResult Accept(string username, string fingerprint, int chunkIndex, int chunkTotal,
                                        List<SiteOutputMetadata> meta, out string detail)
        {
            detail = "";
            if (string.IsNullOrWhiteSpace(username)) { detail = "no sender"; return PushResult.Ignored; }
            if (meta == null || meta.Count == 0)     { detail = "empty chunk"; return PushResult.Ignored; }

            fingerprint = (fingerprint ?? "").Trim();
            if (chunkTotal <= 0) chunkTotal = 1;
            if (chunkIndex <= 0) chunkIndex = 1;

            lock (_lock)
            {
                PrunePendingLocked();

                if (!_pending.TryGetValue(username, out Pending p) || p.Fingerprint != fingerprint)
                {
                    // A changed fingerprint mid-stream starts over, rather than blending two catalogs into neither.
                    p = new Pending { Fingerprint = fingerprint, Total = chunkTotal };
                    _pending[username] = p;
                }

                if (!p.SeenChunks.Add(chunkIndex)) { detail = $"duplicate chunk {chunkIndex}"; return PushResult.Ignored; }
                p.Total = Math.Max(p.Total, chunkTotal);
                p.Meta.AddRange(meta);

                if (p.SeenChunks.Count < p.Total)
                {
                    detail = $"{p.SeenChunks.Count}/{p.Total} chunks";
                    return PushResult.Accumulating;
                }

                _pending.Remove(username);
                return CommitLocked(username, p, out detail);
            }
        }

        private static PushResult CommitLocked(string username, Pending p, out string detail)
        {
            bool first = _byDef.Count == 0 || string.IsNullOrEmpty(_fingerprint);

            if (!first && !string.Equals(_fingerprint, p.Fingerprint, StringComparison.Ordinal))
            {
                _rejectedPushes++;
                detail = $"fingerprint {p.Fingerprint} != established {_fingerprint} (from {_establishedBy})";
                // Warned rather than silenced: an owner being probed by a modified client has to be able to see it.
                ServerLog.Warn($"Site catalog: REFUSED a catalog from '{username}' - {detail}. "
                             + "The established catalog is unchanged. If this is a legitimate modpack change, "
                             + "run 'kmh site-catalog reset' and let a client re-push.");
                return PushResult.Rejected;
            }

            Classify(p.Meta);

            _byDef.Clear();
            foreach (SiteOutputMetadata m in p.Meta)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.DefName)) continue;
                _byDef[m.DefName.Trim()] = m;
            }
            _fingerprint   = p.Fingerprint;
            _establishedBy = username;
            _establishedUtc = DateTime.UtcNow;
            _dirty = true;

            detail = $"{_byDef.Count} def(s), fingerprint {_fingerprint}";
            return first ? PushResult.Accepted : PushResult.Refreshed;
        }

        // An owner override beats the classifier, so a modded item can be corrected without patching KMH.
        internal static void Classify(List<SiteOutputMetadata> meta)
        {
            if (meta == null) return;
            SitesConfig cfg = SitesConfig.Current;
            foreach (SiteOutputMetadata m in meta)
            {
                if (m == null) continue;
                string over = cfg?.FamilyOverrideFor(m.DefName);
                if (!string.IsNullOrEmpty(over) && SiteOutputFamilies.IsKnown(over))
                {
                    m.Family = over;
                    m.Source = "override";
                }
                else
                {
                    string classified = SiteOutputClassifier.Classify(m);
                    m.Family = ResolveFamily(classified, cfg?.UnknownOutputFamily);
                    m.Source = m.Family == classified ? "classifier" : "unknown-default";
                }
                m.Skill = SiteOutputFamilies.SkillFor(m.Family);
            }
        }

        // Empty leaves it unclassified, which every archetype refuses - guessing a family is what made this exploitable.
        internal static string ResolveFamily(string classified, string unknownFallback)
        {
            if (classified != SiteOutputFamilies.Unknown) return classified;
            string fallback = (unknownFallback ?? "").Trim().ToLowerInvariant();
            return SiteOutputFamilies.IsKnown(fallback) ? fallback : SiteOutputFamilies.Unknown;
        }

        private static void PrunePendingLocked()
        {
            if (_pending.Count == 0) return;
            DateTime cutoff = DateTime.UtcNow - PendingTtl;
            List<string> stale = null;
            foreach (var kv in _pending)
                if (kv.Value.StartedUtc < cutoff) (stale ?? (stale = new List<string>())).Add(kv.Key);
            if (stale == null) return;
            foreach (string k in stale) _pending.Remove(k);
        }


        public static SiteOutputMetadata Lookup(string defName)
        {
            if (string.IsNullOrWhiteSpace(defName)) return null;
            lock (_lock) return _byDef.TryGetValue(defName.Trim(), out SiteOutputMetadata m) ? m : null;
        }

        // Unknown is a real answer, not a failure, and must never quietly fall back to Crafting.
        public static string FamilyOf(string defName)
            => Lookup(defName)?.Family ?? SiteOutputFamilies.Unknown;

        // Empty for Unknown: a site that cannot say which skill it needs must not pretend it needs Crafting.
        public static string SkillOf(string defName)
        {
            SiteOutputMetadata m = Lookup(defName);
            return m == null ? "" : (m.Skill ?? "");
        }


        internal sealed class PersistedState
        {
            public string Fingerprint { get; set; } = "";
            public string EstablishedBy { get; set; } = "";
            public long   EstablishedUtcTicks { get; set; }
            public List<SiteOutputMetadata> Meta { get; set; } = new List<SiteOutputMetadata>();
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.SiteCatalogFile, out PersistedState state) || state?.Meta == null) return;
            // Re-classified on load, or a stored family would silently outlive the rule that produced it.
            Classify(state.Meta);
            lock (_lock)
            {
                _byDef.Clear();
                foreach (SiteOutputMetadata m in state.Meta)
                {
                    if (m == null || string.IsNullOrWhiteSpace(m.DefName)) continue;
                    _byDef[m.DefName.Trim()] = m;
                }
                _fingerprint    = state.Fingerprint ?? "";
                _establishedBy  = state.EstablishedBy ?? "";
                _establishedUtc = state.EstablishedUtcTicks > 0 ? new DateTime(state.EstablishedUtcTicks, DateTimeKind.Utc) : DateTime.MinValue;
                _dirty = false;
            }
            ServerLog.Info($"Site catalog: loaded {Count} classified def(s) (fingerprint {Fingerprint}).");
        }

        public static void SaveToDisk()
        {
            PersistedState state;
            lock (_lock)
            {
                state = new PersistedState
                {
                    Fingerprint = _fingerprint,
                    EstablishedBy = _establishedBy,
                    EstablishedUtcTicks = _establishedUtc == DateTime.MinValue ? 0 : _establishedUtc.Ticks,
                    Meta = new List<SiteOutputMetadata>(_byDef.Values),
                };
                _dirty = false;
            }
            JsonFileStore.Save(KmhDataPaths.SiteCatalogFile, state);
        }

        public static bool Dirty { get { lock (_lock) return _dirty; } }
        public static void SaveIfDirty() { if (Dirty) SaveToDisk(); }

        // Admin-only and explicit: an automatic reset would hand the whole guard to whoever reconnects last.
        public static void Reset()
        {
            lock (_lock)
            {
                _byDef.Clear(); _pending.Clear();
                _fingerprint = ""; _establishedBy = ""; _establishedUtc = DateTime.MinValue;
                _rejectedPushes = 0; _dirty = true;
            }
            ServerLog.Warn("Site catalog: reset - the next client push will establish a new catalog.");
        }

        internal static void ResetForTest() => Reset();
    }
}
