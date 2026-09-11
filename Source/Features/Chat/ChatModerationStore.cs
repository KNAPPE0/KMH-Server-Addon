using System;
using System.Collections.Generic;
using KMHServerAddon.Features.Chat.Dto;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Chat
{
    // Persisted, because a block is a lasting preference rather than something that ends with the session.
    internal static class ChatModerationStore
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, HashSet<string>> _blocks =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // KmhRateWindow is not self-synchronizing, so every use of this must be inside _lock.
        private static readonly Util.KmhRateWindow _changeRate = new Util.KmhRateWindow();

        public static bool IsBlocked(string blocker, string blocked)
        {
            if (string.IsNullOrEmpty(blocker) || string.IsNullOrEmpty(blocked)) return false;
            lock (_lock) return _blocks.TryGetValue(blocker, out HashSet<string> set) && set.Contains(blocked);
        }

        public static HashSet<string> BlockedSetFor(string blocker)
        {
            HashSet<string> copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(blocker)) return copy;
            lock (_lock) if (_blocks.TryGetValue(blocker, out HashSet<string> set)) copy.UnionWith(set);
            return copy;
        }

        public static List<string> BlockedBy(string blocker)
        {
            List<string> outList = new List<string>(BlockedSetFor(blocker));
            outList.Sort(StringComparer.OrdinalIgnoreCase);
            return outList;
        }

        // Only an add is refused, or a player at the cap could never get back under it.
        internal static bool AtBlockLimit(int currentCount, int max) => max > 0 && currentCount >= max;

        // Only a real change is charged, or repeating a setting would trip a limit aimed at a script.
        internal static bool RefuseForChurn(Util.KmhRateWindow window, string blocker, bool willChange, long nowTicks, ChatConfig cfg)
            => willChange && window != null && cfg != null
               && !window.Allow(blocker, nowTicks, cfg.MaxBlockChangesPerWindow, cfg.BlockChangeWindowSeconds);

        // Nothing checks the target is a real player, so without the caps below a script could grow this file unbounded.
        public static bool SetBlock(string blocker, string target, bool on, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrEmpty(blocker) || string.IsNullOrEmpty(target)
                || string.Equals(blocker, target, StringComparison.OrdinalIgnoreCase)) return false;
            bool changed;
            ChatConfig cfg = ChatConfig.Current;
            int max = cfg.MaxBlocksPerUser;
            long now = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (!_blocks.TryGetValue(blocker, out HashSet<string> set))
                {
                    if (!on) return false;
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _blocks[blocker] = set;
                }
                if (on && !set.Contains(target) && AtBlockLimit(set.Count, max))
                {
                    if (set.Count == 0) _blocks.Remove(blocker);
                    refusal = $"You've blocked the maximum of {max} players - unblock someone first.";
                    return false;
                }
                bool willChange = on ? !set.Contains(target) : set.Contains(target);
                if (RefuseForChurn(_changeRate, blocker, willChange, now, cfg))
                {
                    if (set.Count == 0) _blocks.Remove(blocker);
                    refusal = "You're changing your block list too quickly - try again in a moment.";
                    return false;
                }
                changed = on ? set.Add(target) : set.Remove(target);
                if (!on && set.Count == 0) _blocks.Remove(blocker);
            }
            if (changed) SaveToDisk();
            return changed;
        }

        public static List<ChatMessage> FilterOut(List<ChatMessage> msgs, HashSet<string> blockedSenders)
        {
            List<ChatMessage> outList = new List<ChatMessage>();
            if (msgs == null) return outList;
            foreach (ChatMessage m in msgs)
                if (m != null && (blockedSenders == null || !blockedSenders.Contains(m.FromUsername))) outList.Add(m);
            return outList;
        }

        private sealed class PersistedState
        {
            public Dictionary<string, List<string>> Blocks { get; set; } = new Dictionary<string, List<string>>();
        }

        public static void LoadFromDisk()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.ChatModerationFile, out PersistedState s) || s?.Blocks == null) return;
            lock (_lock)
            {
                _blocks.Clear();
                foreach (KeyValuePair<string, List<string>> kv in s.Blocks)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    HashSet<string> set = new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
                    set.Remove("");
                    if (set.Count > 0) _blocks[kv.Key] = set;
                }
            }
        }

        public static void SaveToDisk()
        {
            PersistedState s = new PersistedState();
            long seq;
            lock (_lock)
            {
                foreach (KeyValuePair<string, HashSet<string>> kv in _blocks) s.Blocks[kv.Key] = new List<string>(kv.Value);
                seq = JsonFileStore.NextSequence();
            }
            JsonFileStore.Save(KmhDataPaths.ChatModerationFile, s, seq);
        }
    }
}
