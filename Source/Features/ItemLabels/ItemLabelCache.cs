using System;
using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.ItemLabels
{
    // defName is the security key; a label is UI-only and may vary by mod or language.
    internal static class ItemLabelCache
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _labels
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // First-seen wins and is never overwritten, so one modified client cannot poison the value Site pricing trusts.
        private static Dictionary<string, long> _values
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _valueDivergenceWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Never changed by a client push, which is what makes a poisoned first-seen value fixable.
        private static HashSet<string> _ownerPinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Used only to consolidate legacy payloads that predate the per-payload mergeable flag.
        private static HashSet<string> _fungible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Saved only when something was new, or an identical re-push on every reconnect would rewrite the file.
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
                    if (_ownerPinned.Contains(kv.Key)) continue;   // owner set this by hand - client can't change it
                    if (!_values.TryGetValue(kv.Key, out long existing))
                    {
                        _values[kv.Key] = kv.Value;   // first-seen wins - this becomes the trusted baseline
                        added++;
                    }
                    else if (existing != kv.Value)
                    {
                        // The baseline is kept, and a large divergence is flagged once so an owner can spot a modified client.
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

        // A value of 0 or less unpins and reverts the def to whatever clients vouched for it.
        public static void OwnerSetValue(string defName, long value)
        {
            if (string.IsNullOrEmpty(defName)) return;
            lock (_lock)
            {
                if (value > 0) { _values[defName] = value; _ownerPinned.Add(defName); }
                else           { _ownerPinned.Remove(defName); }   // unpin; keep whatever value is there
            }
            SaveToDisk();
        }

        // (value, pinned) for owner inspection; value 0 means unknown.
        public static (long value, bool pinned) ValueInfo(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return (0, false);
            lock (_lock)
                return (_values.TryGetValue(defName, out long v) ? v : 0, _ownerPinned.Contains(defName));
        }

        // True only when something was newly added, so the caller runs its treasury compaction once rather than every push.
        public static bool MarkFungible(IEnumerable<string> defNames)
        {
            if (defNames == null) return false;
            int added = 0;
            lock (_lock)
                foreach (string d in defNames)
                    if (!string.IsNullOrEmpty(d) && _fungible.Add(d)) added++;
            if (added > 0) SaveToDisk();
            return added > 0;
        }

        // True when a client has vouched this def (bare or composed key) as a plain stackable food/resource.
        public static bool IsFungibleDef(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return false;
            if (defName.IndexOf(Util.ItemKey.Sep) >= 0) Util.ItemKey.Split(defName, out defName, out _, out _);
            lock (_lock) return _fungible.Contains(defName);
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

        // Falls back to the defName, matching the patch side, so a player always sees something readable.
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

        // Returns null plus candidates when ambiguous, so a Discord command never guesses which item was meant.
        public static string ResolveDefNameByQuery(string query, out List<string> candidates)
        {
            candidates = null;
            if (string.IsNullOrWhiteSpace(query)) return null;
            string q = query.Trim();

            // One pass into priority buckets, because this runs over thousands of labels.
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

                    // Capped, or a one-letter query would allocate a bucket the size of the whole catalog.
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

            if (exactLabel != null)
            {
                if (exactLabel.Count == 1) return exactLabel[0];
                candidates = exactLabel;
                return null;
            }
            if (exactDef != null) return exactDef;
            if (labelHits != null)
            {
                if (labelHits.Count == 1) return labelHits[0];
                candidates = labelHits;
                return null;
            }
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

        // Copies out, so a Discord command cannot hold or mutate the live dictionary.
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


        public static void LoadFromDisk()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.ItemLabelsFile, out PersistedState state) && state?.Labels != null)
            {
                lock (_lock)
                {
                    _labels = new Dictionary<string, string>(state.Labels, StringComparer.OrdinalIgnoreCase);
                    if (state.Values != null)
                        _values = new Dictionary<string, long>(state.Values, StringComparer.OrdinalIgnoreCase);
                    if (state.Fungible != null)
                        _fungible = new HashSet<string>(state.Fungible, StringComparer.OrdinalIgnoreCase);
                    if (state.Pinned != null)
                        _ownerPinned = new HashSet<string>(state.Pinned, StringComparer.OrdinalIgnoreCase);
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
                state.Fungible = new List<string>(_fungible);
                state.Pinned = new List<string>(_ownerPinned);
            }
            JsonFileStore.Save(KmhDataPaths.ItemLabelsFile, state);
        }

        private class PersistedState
        {
            public Dictionary<string, string> Labels   { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, long>   Values   { get; set; } = new Dictionary<string, long>();
            public List<string>               Fungible { get; set; } = new List<string>();
            public List<string>               Pinned   { get; set; } = new List<string>();
        }
    }
}
