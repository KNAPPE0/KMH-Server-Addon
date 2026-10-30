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
    internal static class DiscordGuildRoleSync
    {
        private static bool _started;
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _applied
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<ulong> _created = new HashSet<ulong>();
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Regex KmhRolePattern =
            new Regex(@"^.+ \((Admin|Moderator|Officer|Member)\)$", RegexOptions.Compiled);

        internal enum RoleChoice { UseOurs, Create, Refuse }

        // A guild named after an owner's own role must not be handed that role: only roles this server made are used.
        internal static RoleChoice Choose(IEnumerable<(ulong Id, string Name)> roles, string target, ICollection<ulong> created, out ulong id)
        {
            id = 0;
            bool taken = false;
            foreach ((ulong Id, string Name) r in roles)
            {
                if (!string.Equals(r.Name, target, StringComparison.Ordinal)) continue;
                if (created.Contains(r.Id)) { id = r.Id; return RoleChoice.UseOurs; }
                taken = true;
            }
            return taken ? RoleChoice.Refuse : RoleChoice.Create;
        }

        // Only roles this server made are taken away, whatever their name looks like.
        internal static bool MayRemove(ulong roleId, string name, string target, ICollection<ulong> created)
            => created.Contains(roleId) && KmhRolePattern.IsMatch(name) && name != target;

        private static int _reconcilePending;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            LoadCache();
            KmhEventBus bus = KmhEventBus.Instance;
            bus.PlayerLinked   += e => Fire(() => ReconcileUser(e.Username, e.DiscordId));
            bus.PlayerUnlinked += e => Fire(() => ReconcileUser(e.Username, e.DiscordId));
            bus.GuildChanged   += _ => ScheduleReconcileAll();
            ServerLog.Diag("Discord: guild-role sync subscribed (Roles.SyncGuildRoles gates actual changes)");
        }

        // Catches up whatever changed while the bot was offline.
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

        // Debounced, or a burst of guild changes would drive one full reconcile each.
        private static void ScheduleReconcileAll()
        {
            if (!Enabled()) return;
            if (Interlocked.Exchange(ref _reconcilePending, 1) == 1) return;
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

        private static async Task ReconcileUser(string username, ulong discordId)
        {
            if (!Enabled() || string.IsNullOrEmpty(username) || discordId == 0) return;

            // Empty target is the unlinked or guildless case, which then strips every KMH role below.
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
            if (user == null) { SetApplied(username, ""); return; }

            HashSet<ulong> created;
            lock (_lock) created = new HashSet<ulong>(_created);
            foreach (ulong rid in user.RoleIds.ToArray())
            {
                SocketRole r = sg.GetRole(rid);
                if (r == null || !MayRemove(r.Id, r.Name, target, created)) continue;
                try { await user.RemoveRoleAsync(r).ConfigureAwait(false); }
                catch (Exception ex) { ServerLog.Verbose($"role remove '{r.Name}' from {username}: {ex.Message}"); }
            }

            if (!string.IsNullOrEmpty(target))
            {
                IRole role = null;
                switch (Choose(sg.Roles.Select(r => (r.Id, r.Name)), target, created, out ulong ours))
                {
                    case RoleChoice.UseOurs:
                        role = sg.GetRole(ours);
                        break;
                    case RoleChoice.Refuse:
                        bool first;
                        lock (_lock) first = _warned.Add(target);
                        if (first) ServerLog.Warn($"Discord role sync: a role named '{target}' already exists and was not made by Colony Nexus, so it is not given to guild members. Rename that role or the guild.");
                        return;
                    default:
                        try { role = await sg.CreateRoleAsync(target, GuildPermissions.None, null, false, false).ConfigureAwait(false); }
                        catch (Exception ex) { ServerLog.Warn($"Discord role sync: can't create '{target}' - does the bot have Manage Roles? ({ex.Message})"); return; }
                        lock (_lock) _created.Add(role.Id);
                        SaveCache();
                        break;
                }
                if (role == null) return;
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

        private static void SetApplied(string username, string role)
        {
            lock (_lock) { if (string.IsNullOrEmpty(role)) _applied.Remove(username); else _applied[username] = role; }
            SaveCache();
        }

        private sealed class Cache
        {
            public Dictionary<string, string> Applied { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<ulong> Created { get; set; } = new List<ulong>();
        }

        private static void LoadCache()
        {
            if (!JsonFileStore.TryLoad(KmhDataPaths.DiscordGuildRolesFile, out Cache c) || c == null) return;
            lock (_lock)
            {
                if (c.Applied != null) _applied = new Dictionary<string, string>(c.Applied, StringComparer.OrdinalIgnoreCase);
                if (c.Created != null) _created = new HashSet<ulong>(c.Created);
            }
        }

        private static void SaveCache()
        {
            Cache c = new Cache();
            lock (_lock)
            {
                foreach (KeyValuePair<string, string> kv in _applied) c.Applied[kv.Key] = kv.Value;
                c.Created.AddRange(_created);
            }
            JsonFileStore.Save(KmhDataPaths.DiscordGuildRolesFile, c);
        }
    }
}
