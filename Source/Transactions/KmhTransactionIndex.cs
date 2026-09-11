using System;
using System.Collections.Generic;

namespace KMHServerAddon.Transactions
{
    internal sealed class KmhTransactionIndex
    {
        private readonly List<KmhTransaction> _all = new List<KmhTransaction>();
        private readonly Dictionary<string, KmhTransaction> _byId = new Dictionary<string, KmhTransaction>(StringComparer.Ordinal);
        private readonly HashSet<string> _requestKeys = new HashSet<string>(StringComparer.Ordinal);

        public int Count => _all.Count;
        public IReadOnlyList<KmhTransaction> All => _all;

        // Outlives the row it came from: forgetting a pruned row's request key makes that action runnable a second time.
        private readonly Dictionary<string, long> _spentKeys = new Dictionary<string, long>(StringComparer.Ordinal);

        internal static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);
        private const int MaxSpentKeys = 5000;

        public IReadOnlyDictionary<string, long> SpentKeys => _spentKeys;

        public void Load(IEnumerable<KmhTransaction> records, IEnumerable<KeyValuePair<string, long>> spentKeys = null)
        {
            _all.Clear(); _byId.Clear(); _requestKeys.Clear(); _spentKeys.Clear();
            if (spentKeys != null)
                foreach (KeyValuePair<string, long> kv in spentKeys)
                    if (!string.IsNullOrEmpty(kv.Key)) _spentKeys[kv.Key] = kv.Value;
            if (records == null) return;
            foreach (KmhTransaction t in records)
            {
                if (t == null || string.IsNullOrEmpty(t.Id) || _byId.ContainsKey(t.Id)) continue;
                _all.Add(t); _byId[t.Id] = t;
                if (!string.IsNullOrEmpty(t.RequestKey)) _requestKeys.Add(t.RequestKey);
            }
        }

        // Called for a row whose detail is being dropped, so the key survives the row.
        public void RetireKey(string requestKey, long nowTicks)
        {
            if (string.IsNullOrEmpty(requestKey)) return;
            _spentKeys[requestKey] = nowTicks;
            if (_spentKeys.Count <= MaxSpentKeys) return;
            var stale = new List<string>();
            foreach (KeyValuePair<string, long> kv in _spentKeys)
                if (nowTicks - kv.Value >= KeyLifetime.Ticks) stale.Add(kv.Key);
            foreach (string k in stale) _spentKeys.Remove(k);
        }

        public bool IsDuplicate(string requestKey) => IsDuplicate(requestKey, DateTime.UtcNow.Ticks);

        public bool IsDuplicate(string requestKey, long nowTicks)
        {
            if (string.IsNullOrEmpty(requestKey)) return false;
            if (_requestKeys.Contains(requestKey)) return true;
            return _spentKeys.TryGetValue(requestKey, out long at) && nowTicks - at < KeyLifetime.Ticks;
        }

        // Refuses a duplicate id or a re-used request key, so a client retry cannot double-run the action.
        public bool Add(KmhTransaction tx)
        {
            if (tx == null || string.IsNullOrEmpty(tx.Id)) return false;
            if (_byId.ContainsKey(tx.Id)) return false;
            if (!string.IsNullOrEmpty(tx.RequestKey) && _requestKeys.Contains(tx.RequestKey)) return false;
            _all.Add(tx); _byId[tx.Id] = tx;
            if (!string.IsNullOrEmpty(tx.RequestKey)) _requestKeys.Add(tx.RequestKey);
            return true;
        }

        public KmhTransaction Get(string id)
            => !string.IsNullOrEmpty(id) && _byId.TryGetValue(id, out KmhTransaction t) ? t : null;

        // Undoes an Add whose write failed, request key included, or that key stays claimed by a row nobody kept.
        public bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out KmhTransaction t)) return false;
            _byId.Remove(id);
            _all.Remove(t);
            if (!string.IsNullOrEmpty(t.RequestKey)) _requestKeys.Remove(t.RequestKey);
            return true;
        }

        public List<KmhTransaction> ForPlayer(string player)
        {
            var outp = new List<KmhTransaction>();
            foreach (KmhTransaction t in _all)
                if (string.Equals(t.Player, player, StringComparison.OrdinalIgnoreCase)) outp.Add(t);
            return outp;
        }

        public List<KmhTransaction> Pending()
        {
            var outp = new List<KmhTransaction>();
            foreach (KmhTransaction t in _all)
                if (!KmhTxStateMachine.IsTerminal(t.State)) outp.Add(t);
            return outp;
        }
    }
}
