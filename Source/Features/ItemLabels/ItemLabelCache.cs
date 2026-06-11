using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.ItemLabels
{
    // Server-side cache of defName -> human label, populated from connected clients at handshake completion
    //
    // Why server-side? The server is headless and doesn't load RimWorld defs, so it has no built-in way to know
    // "MealSurvivalPack" displays as "packaged survival meal". Discord commands (`!kmh-sell plasteel ...`,
    // `!kmh-market` listing labels) need this mapping to show readable text and accept friendly-name input
    //
    // The patch has the local DefDatabase; at handshake it sends its catalog and we cache the union across clients.
    // Players with different mods each contribute their slice, so the server shows items it has no def for
    //
    // Persistence: cache saves to KMH-Data/Catalog/ItemLabels.json so a server restart doesn't blank the cache
    // until the first client reconnects. First-run / fresh server starts with an empty cache
    internal static class ItemLabelCache
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _labels
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Merge an incoming label set into the cache. Last-writer-wins on collisions - newest contributor's
        // spelling/case wins. Saves to disk only if at least one entry was new (cheap dirty check avoids spamming
        // the file on identical re-pushes from the same client across reconnects)
        public static void Apply(Dictionary<string, string> incoming)
        {
            if (incoming == null || incoming.Count == 0) return;
            int added = 0;
            lock (_lock)
            {
                foreach (KeyValuePair<string, string> kv in incoming)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    if (!_labels.TryGetValue(kv.Key, out string existing)
                        || !string.Equals(existing, kv.Value, StringComparison.Ordinal))
                    {
                        _labels[kv.Key] = kv.Value;
                        added++;
                    }
                }
            }
            if (added > 0)
            {
                SaveToDisk();
                ServerLog.Verbose($"ItemLabels: cache updated ({added} new/changed, {_labels.Count} total)");
            }
        }

        // Returns the label for a defName, or the defName itself when unknown. Same fallback behavior as the
        // patch-side ItemLabels helper - the player always sees *something* readable
        public static string LabelFor(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return "?";
            lock (_lock)
            {
                return _labels.TryGetValue(defName, out string label) ? label : defName;
            }
        }

        // Friendly-name resolution for Discord-side input. Returns the matching defName when the query is
        // unambiguous, or null + a candidates list when it matches more than one item.
        //
        // Match strategy (in order):
        //   1. Exact label match (case-insensitive)  - "Plasteel" → Plasteel
        //   2. Exact defName match                   - raw defName fallback
        //   3. Substring on label                    - "knife" → multiple
        //   4. Substring on defName                  - last-resort
        public static string ResolveDefNameByQuery(string query, out List<string> candidates)
        {
            candidates = null;
            if (string.IsNullOrWhiteSpace(query)) return null;
            string q = query.Trim();

            // Single-pass over the cache, populating four priority buckets. Previous implementation scanned the
            // dictionary up to three times (exact-label, label-substring, defName-substring). For a server with
            // several thousand item labels and a busy Discord command surface this was the hottest path on the
            // addon - now it's O(N) instead of up to O(3N) and short-circuits once we hit a single exact-label or
            // exact-defName match
            List<string> exactLabel = null;
            string       exactDef   = null;
            List<string> labelHits  = null;
            List<string> defHits    = null;

            const int CandidateCap = 10;

            lock (_lock)
            {
                foreach (KeyValuePair<string, string> kv in _labels)
                {
                    string defName = kv.Key;
                    string label   = kv.Value;

                    // Priority 1: exact label match (case-insensitive).
                    if (string.Equals(label, q, StringComparison.OrdinalIgnoreCase))
                    {
                        if (exactLabel == null) exactLabel = new List<string>();
                        exactLabel.Add(defName);
                        continue; // an exact label hit doesn't also need
                                  // bucketing as a substring hit
                    }

                    // Priority 2: exact defName match (set the flag once).
                    if (exactDef == null
                        && string.Equals(defName, q, StringComparison.OrdinalIgnoreCase))
                    {
                        exactDef = defName;
                    }

                    // Priority 3 + 4: substring buckets, capped to avoid unbounded allocations during fuzzy
                    // matches
                    if (labelHits == null || labelHits.Count < CandidateCap)
                    {
                        if (label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (labelHits == null) labelHits = new List<string>();
                            labelHits.Add(defName);
                            continue;
                        }
                    }
                    if (defHits == null || defHits.Count < CandidateCap)
                    {
                        if (defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (defHits == null) defHits = new List<string>();
                            defHits.Add(defName);
                        }
                    }
                }
            }

            // Priority 1 wins outright.
            if (exactLabel != null)
            {
                if (exactLabel.Count == 1) return exactLabel[0];
                candidates = exactLabel;
                return null;
            }
            // Priority 2.
            if (exactDef != null) return exactDef;
            // Priority 3.
            if (labelHits != null)
            {
                if (labelHits.Count == 1) return labelHits[0];
                candidates = labelHits;
                return null;
            }
            // Priority 4.
            if (defHits != null)
            {
                if (defHits.Count == 1) return defHits[0];
                candidates = defHits;
                return null;
            }
            return null;
        }

        public static int Count
        {
            get { lock (_lock) { return _labels.Count; } }
        }

        // Returns up to `max` entries sorted alphabetically by defName, optionally filtered to entries whose
        // defName OR label contains `query` (case-insensitive, empty query = no filter). Used by !kmh-items /
        // !kmh-catalog for discovery without exposing the raw underlying dictionary
        public static List<KeyValuePair<string, string>> Sample(string query, int max)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            bool filtered = !string.IsNullOrWhiteSpace(query);
            string q      = filtered ? query.Trim() : null;
            lock (_lock)
            {
                List<KeyValuePair<string, string>> all = new List<KeyValuePair<string, string>>(_labels);
                all.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                foreach (KeyValuePair<string, string> kv in all)
                {
                    if (filtered)
                    {
                        bool matchKey = kv.Key  .IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool matchVal = kv.Value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!matchKey && !matchVal) continue;
                    }
                    result.Add(kv);
                    if (result.Count >= max) break;
                }
            }
            return result;
        }

        // --- persistence ---

        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.ItemLabelsFile, out PersistedState state) && state?.Labels != null)
            {
                lock (_lock)
                {
                    _labels = new Dictionary<string, string>(state.Labels, StringComparer.OrdinalIgnoreCase);
                }
                ServerLog.Info($"ItemLabels: loaded {state.Labels.Count} label(s) from disk");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Labels = new Dictionary<string, string>(_labels, StringComparer.OrdinalIgnoreCase);
            }
            JsonFileStore.Save(KmhDataPaths.ItemLabelsFile, state);
        }

        private class PersistedState
        {
            public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
        }
    }
}
