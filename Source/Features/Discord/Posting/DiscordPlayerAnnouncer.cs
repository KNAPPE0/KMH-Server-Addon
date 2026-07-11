using System;
using System.Collections.Generic;
using KMHServerAddon.Features.LinkedAccounts;

namespace KMHServerAddon.Features.Discord
{

    internal static class DiscordPlayerAnnouncer
    {
        private const int PruneIntervalMs = 60_000;

        private static readonly object _lock = new object();
        private static readonly HashSet<string> _joined
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static long _lastPruneUtcTicks;

        public static void AnnounceJoined(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;

            bool fresh;
            lock (_lock)
            {
                PruneStaleLocked();
                fresh = _joined.Add(username);
            }
            if (!fresh) return; // already announced in this session

            string display = ResolveDisplay(username);
            string text    = $"🟢 **{display}** has joined the server!";
            PostIfConfigured(text);

            // Notify extensions. Same dedup as Discord posting - only raised the first time we see this user in a
            // session
            Extensibility.KmhEventBus.Instance.RaisePlayerJoined(
                new KMH.Sdk.Server.Events.PlayerJoinedEvent { Username = username });
        }

        public static void AnnounceLeft(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;

            bool wasTracked;
            lock (_lock) { wasTracked = _joined.Remove(username); }
            // Only relay if we previously announced this user as joined. Stops duplicate-disconnect events from
            // producing duplicate leave lines
            if (!wasTracked) return;

            string display = ResolveDisplay(username);
            string text    = $"⚫ **{display}** has left the server!";
            PostIfConfigured(text);

            Extensibility.KmhEventBus.Instance.RaisePlayerLeft(
                new KMH.Sdk.Server.Events.PlayerLeftEvent { Username = username });
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _joined.Clear();
                _lastPruneUtcTicks = 0;
            }
        }

        // Show "username (DiscordHandle)" when the player is linked, plain username otherwise.
        private static string ResolveDisplay(string username)
        {
            if (LinkedAccountsStore.TryGetLink(username, out string discord)
                && !string.IsNullOrEmpty(discord))
            {
                return $"{username} ({discord})";
            }
            return username;
        }

        private static void PostIfConfigured(string text)
        {
            DiscordConfig cfg = DiscordBridge.Config;
            if (cfg == null) return;
            DiscordBridge.PostToChannel(cfg.PlayerAnnounceChannelId, text);
        }

        // Caller must hold _lock. Drops anyone in _joined who is no longer in the connected-clients list - guards
        // against the rare case where a client drops without OnDisconnect firing (crash, network blip that's
        // resolved by the time we check)
        private static void PruneStaleLocked()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long ivlTicks = TimeSpan.FromMilliseconds(PruneIntervalMs).Ticks;
            if (_lastPruneUtcTicks != 0 && nowTicks - _lastPruneUtcTicks < ivlTicks) return;
            _lastPruneUtcTicks = nowTicks;

            HashSet<string> connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (TCPNetwork.ServerClient sc in Network.ServerClients.Keys)
                {
                    string u = sc?.GetData<UserFile>()?.Username;
                    if (!string.IsNullOrEmpty(u)) connected.Add(u);
                }
            }
            catch { return; }

            List<string> stale = null;
            foreach (string u in _joined)
            {
                if (!connected.Contains(u))
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(u);
                }
            }
            if (stale != null)
            {
                foreach (string u in stale) _joined.Remove(u);
            }
        }
    }
}
