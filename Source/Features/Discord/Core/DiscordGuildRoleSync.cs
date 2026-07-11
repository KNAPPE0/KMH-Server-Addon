using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Rest;
using Discord.WebSocket;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Extensibility;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Discord
{
    // Mirrors guild membership to Discord roles "<Guild> (<Rank>)" - opt-in, REST-based, bot needs Manage Roles.
    internal static class DiscordGuildRoleSync
    {
        private static bool _started;
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _applied  // username -> role name last applied
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Any role named "<something> (Admin|Moderator|Officer|Member)" is one of ours to manage.
        private static readonly Regex KmhRolePattern =
            new Regex(@"^.+ \((Admin|Moderator|Officer|Member)\)$", RegexOptions.Compiled);

        private static int _reconcilePending;   // debounce guard for the guild-wide reconcile

        public static void Start()
        {
            if (_started) return;
            _started = true;
            LoadCache();
            KmhEventBus bus = KmhEventBus.Instance;
            bus.PlayerLinked   += e => Fire(() => ReconcileUser(e.Username, e.DiscordId));
            bus.PlayerUnlinked += e => Fire(() => ReconcileUser(e.Username, e.DiscordId));
            bus.GuildChanged   += _ => ScheduleReconcileAll();   // reason/actor vary, so reconcile everyone (debounced)
            ServerLog.Info("Discord: guild-role sync subscribed (Roles.SyncGuildRoles gates actual changes)");
        }

        // On (re)connect, catch up anything that changed while the bot was offline.
        public static void OnBridgeReady() => ScheduleReconcileAll();

        private static bool Enabled()
        {
            DiscordConfig cfg = DiscordBridge.Config;
            return cfg != null && cfg.IsEnabled && cfg.SyncGuildRolesOn && DiscordBridge.Client != null;
        }

        private static void Fire(Func<Task> work)
        {
            if (!Enabled()) return;
            _ = Task.Run(async () =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception ex) { ServerLog.Verbose($"Discord role sync: {ex.Message}"); }
            });
        }

        // Coalesce bursts of guild changes into one full reconcile a couple seconds later.
        private static void ScheduleReconcileAll()
        {
            if (!Enabled()) return;
            if (Interlocked.Exchange(ref _reconcilePending, 1) == 1) return; // already scheduled
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(2500).ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref _reconcilePending, 0); }
                try { await ReconcileAll().ConfigureAwait(false); }
                catch (Exception ex) { ServerLog.Verbose($"Discord role reconcile-all: {ex.Message}"); }
            });
        }

        private static async Task ReconcileAll()
        {
            if (!Enabled()) return;
            foreach (KeyValuePair<string, ulong> kv in LinkedAccounts.LinkedAccountsStore.AllLinked())
                await ReconcileUser(kv.Key, kv.Value).ConfigureAwait(false);
        }

        // Align this user's Discord roles with their KMH guild+rank; the cache short-circuits unchanged users.
        private static async Task ReconcileUser(string username, ulong discordId)
        {
            if (!Enabled() || string.IsNullOrEmpty(username) || discordId == 0) return;

            // Target role from their CURRENT link + guild. Unlinked or guildless -> no KMH role.
            string target = "";
            if (LinkedAccounts.LinkedAccountsStore.DiscordIdFor(username) == discordId)
            {
                string guild = Guilds.GuildStore.CurrentGuildOf(username);
                if (!string.IsNullOrEmpty(guild))
                    target = $"{guild} ({RankLabel(Guilds.GuildStore.RankOf(username))})";
            }

            lock (_lock) { if (_applied.TryGetValue(username, out string prev) && prev == target) return; }

            DiscordSocketClient client = DiscordBridge.Client;
            ulong guildId = DiscordBridge.Config.AllowedGuildIds.FirstOrDefault();
            if (client == null || guildId == 0) return;
            SocketGuild sg = client.GetGuild(guildId);
            if (sg == null) return;

            RestGuildUser user = await client.Rest.GetGuildUserAsync(guildId, discordId).ConfigureAwait(false);
            if (user == null) { SetApplied(username, ""); return; }   // not a member of the Discord server

            // Strip any KMH-managed role that isn't the target (covers rank change, guild change, leave, unlink).
            foreach (ulong rid in user.RoleIds.ToArray())
            {
                SocketRole r = sg.GetRole(rid);
                if (r == null || !KmhRolePattern.IsMatch(r.Name) || r.Name == target) continue;
                try { await user.RemoveRoleAsync(r).ConfigureAwait(false); }
                catch (Exception ex) { ServerLog.Verbose($"role remove '{r.Name}' from {username}: {ex.Message}"); }
            }

            // Add the target, creating the role on first use.
            if (!string.IsNullOrEmpty(target))
            {
                IRole role = sg.Roles.FirstOrDefault(r => string.Equals(r.Name, target, StringComparison.Ordinal));
                if (role == null)
                {
                    try { role = await sg.CreateRoleAsync(target, GuildPermissions.None, null, false, false).ConfigureAwait(false); }
                    catch (Exception ex) { ServerLog.Warn($"Discord role sync: can't create '{target}' - does the bot have Manage Roles? ({ex.Message})"); return; }
                }
                if (!user.RoleIds.Contains(role.Id))
                {
                    try { await user.AddRoleAsync(role).ConfigureAwait(false); }
                    catch (Exception ex) { ServerLog.Warn($"Discord role sync: can't add '{target}' to {username} - is the bot's role above it? ({ex.Message})"); return; }
                }
            }

            SetApplied(username, target);
        }

        private static string RankLabel(string rank)
        {
            if (string.IsNullOrEmpty(rank)) return "Member";
            return char.ToUpperInvariant(rank[0]) + rank.Substring(1).ToLowerInvariant();
        }

        // --- applied-role cache (persisted, regenerable) ---

        private static void SetApplied(string username, string role)
        {
            lock (_lock) { if (string.IsNullOrEmpty(role)) _applied.Remove(username); else _applied[username] = role; }
            SaveCache();
        }

        private sealed class Cache
        {
            public Dictionary<string, string> Applied { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void LoadCache()
        {
            if (JsonFileStore.TryLoad(KmhDataPaths.DiscordGuildRolesFile, out Cache c) && c?.Applied != null)
                lock (_lock) _applied = new Dictionary<string, string>(c.Applied, StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveCache()
        {
            Cache c = new Cache();
            lock (_lock) foreach (KeyValuePair<string, string> kv in _applied) c.Applied[kv.Key] = kv.Value;
            JsonFileStore.Save(KmhDataPaths.DiscordGuildRolesFile, c);
        }
    }
}
