using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.ItemLabels
{
    // Headless server's defName -> label cache, union of clients' catalogs (persisted). Security: labels are UI-only
    // (last-writer-wins, vary by mod/language); defName is the security key and values are first-seen-wins (anti-poison).
    internal static class ItemLabelCache
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _labels
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // defName -> RimWorld BaseMarketValue, contributed by clients (the live game economy). Lets the World Engine
        // value-scale quest rewards to what the requested goods are actually worth. FIRST-SEEN WINS (see ApplyValues):
        // once a value is recorded it isn't overwritten by a later client, so one modified client can't poison the
        // trusted value used for Site pricing.
        private static Dictionary<string, long> _values
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _valueDivergenceWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        // Merge an incoming defName -> base market value set. Last-writer-wins; saves only when something changed.
        public static void ApplyValues(Dictionary<string, long> incoming)
        {
            if (incoming == null || incoming.Count == 0) return;
            int added = 0, flagged = 0;
            lock (_lock)
            {
                foreach (KeyValuePair<string, long> kv in incoming)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                    if (!_values.TryGetValue(kv.Key, out long existing))
                    {
                        _values[kv.Key] = kv.Value;   // first-seen wins - this becomes the trusted baseline
                        added++;
                    }
                    else if (existing != kv.Value)
                    {
                        // A later client reported a DIFFERENT value. Keep the trusted baseline (anti-poison); flag a
                        // large divergence once per def so an owner can spot a modified client skewing values.
                        double ratio = existing > 0 ? (double)kv.Value / existing : 0;
                        if ((ratio > 1.5 || ratio < 0.5) && _valueDivergenceWarned.Add(kv.Key))
                        {
                            flagged++;
                            ServerLog.Warn($"ItemLabels: {kv.Key} value {kv.Value} diverges from trusted {existing} - kept trusted (possible modified client).");
                        }
                    }
                }
            }
            if (added > 0)
            {
                SaveToDisk();
                ServerLog.Verbose($"ItemLabels: value cache +{added} first-seen ({_values.Count} total)" + (flagged > 0 ? $", {flagged} divergent ignored" : ""));
            }
        }

        // RimWorld base market value for a defName (bare or composed key), or 0 when no client has reported it yet.
        public static long BaseValue(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return 0;
            if (defName.IndexOf(Util.ItemKey.Sep) >= 0)
                Util.ItemKey.Split(defName, out defName, out _, out _);
            lock (_lock)
                return _values.TryGetValue(defName, out long v) ? v : 0;
        }

        // Returns the label for a defName, or the defName itself when unknown. Same fallback behavior as the
        // patch-side ItemLabels helper - the player always sees *something* readable
        public static string LabelFor(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return "?";
            // composed treasury keys (def|stuff|quality) resolve to a full label
            if (defName.IndexOf(Util.ItemKey.Sep) >= 0)
            {
                Util.ItemKey.Split(defName, out string d, out string st, out int q);
                return LabelFor(d, st, q);
            }
            lock (_lock)
            {
                return _labels.TryGetValue(defName, out string label) ? label : defName;
            }
        }

        // "Excellent plasteel longsword" from listing-style parts
        public static string LabelFor(string defName, string stuffDefName, int qualityIndex)
        {
            string baseLabel = LabelFor(defName);
            if (!string.IsNullOrEmpty(stuffDefName))
                baseLabel = $"{LabelFor(stuffDefName)} {baseLabel}";
            string q = Util.ItemKey.QualityName(qualityIndex);
            return q.Length > 0 ? $"{q} {baseLabel}" : baseLabel;
        }

        // Friendly-name -> defName for Discord input. Unambiguous -> defName; multiple matches -> null + candidates.
        // Tries exact label, exact defName, then substring on each.
        public static string ResolveDefNameByQuery(string query, out List<string> candidates)
        {
            candidates = null;
            if (string.IsNullOrWhiteSpace(query)) return null;
            string q = query.Trim();

            // Single O(N) pass into priority buckets (hot path with thousands of labels); short-circuits on a lone
            // exact match.
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

        // Every known (defName, label, value) - used to build the server-authoritative site output catalog.
        public static List<(string DefName, string Label, long Value)> AllForCatalog()
        {
            List<(string, string, long)> result = new List<(string, string, long)>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, string> kv in _labels)
                {
                    _values.TryGetValue(kv.Key, out long v);
                    result.Add((kv.Key, kv.Value, v));
                }
            }
            return result;
        }

        // Random deliver-quest target: value in [min,max], sane def; null until the catalog has candidates.
        public static (string defName, string label, long value)? RandomDeliverable(long minValue, long maxValue, Random rng)
        {
            List<(string, string, long)> pool = new List<(string, string, long)>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, long> kv in _values)
                {
                    if (kv.Value < minValue || kv.Value > maxValue) continue;
                    string d = kv.Key;
                    if (d.StartsWith("Unfinished", StringComparison.OrdinalIgnoreCase)
                        || d.StartsWith("Minified", StringComparison.OrdinalIgnoreCase)
                        || d.StartsWith("Corpse_",  StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_labels.TryGetValue(d, out string label) || string.IsNullOrEmpty(label)) continue;
                    pool.Add((d, label, kv.Value));
                }
            }
            if (pool.Count == 0) return null;
            return pool[rng.Next(pool.Count)];
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
                    if (state.Values != null)
                        _values = new Dictionary<string, long>(state.Values, StringComparer.OrdinalIgnoreCase);
                }
                ServerLog.Info($"ItemLabels: loaded {state.Labels.Count} label(s), {state.Values?.Count ?? 0} value(s) from disk");
            }
        }

        public static void SaveToDisk()
        {
            PersistedState state = new PersistedState();
            lock (_lock)
            {
                state.Labels = new Dictionary<string, string>(_labels, StringComparer.OrdinalIgnoreCase);
                state.Values = new Dictionary<string, long>(_values, StringComparer.OrdinalIgnoreCase);
            }
            JsonFileStore.Save(KmhDataPaths.ItemLabelsFile, state);
        }

        private class PersistedState
        {
            public Dictionary<string, string> Labels { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, long>   Values { get; set; } = new Dictionary<string, long>();
        }
    }
}
