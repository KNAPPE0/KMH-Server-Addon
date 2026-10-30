using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Global
{
    // Field lists are reflected from the posted bodies, so the disclosure can never describe less than what travels.
    internal static class KmhGlobalDisclosure
    {
        private static readonly string[] SetupPaths =
        {
            "/api/v1/servers/registration/challenge",
            "/api/v1/servers/register",
        };

        public static bool IsSetupPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (string p in SetupPaths)
                if (string.Equals(path, p, StringComparison.Ordinal)) return true;

            // Owner linking carries nothing about the server; admit the whole branch so no forgotten leaf breaks setup.
            if (path == "/api/v1/owner-links"
                || path.StartsWith("/api/v1/owner-links/", StringComparison.Ordinal)) return true;

            // The owner's own identity commands carry only this server's id and keys, and run only when typed.
            if (path == KmhIdentityChange.ProvePath) return true;

            // Matched by both ends: the prefix alone would also admit the heartbeat.
            return path.StartsWith("/api/v1/servers/", StringComparison.Ordinal)
                && (path.EndsWith("/registration", StringComparison.Ordinal)
                    || path.EndsWith("/rotate-key", StringComparison.Ordinal)
                    || path.EndsWith("/recover-identity", StringComparison.Ordinal));
        }

        public static bool IsBackendSetupPath(string path)
            => string.Equals(path, KmhGlobalBackend.ChallengePath, StringComparison.Ordinal)
            || string.Equals(path, KmhGlobalBackend.RegisterPath, StringComparison.Ordinal);

        // Nothing leaves unless Connected Worlds is on or a private federation identity is allowed, and registration
        // finished; enforced in the client because a guard every caller must remember is one the next caller forgets.
        public static bool MayPublish(string path, out string why)
        {
            why = "";
            if (IsSetupPath(path)) return true;
            if (IsBackendSetupPath(path))
            {
                if (KmhGlobalBackend.Allowed) return true;
                why = "a private identity is not allowed here";
                return false;
            }

            GlobalConfig cfg = GlobalConfig.Current;
            bool backend = cfg != null && !cfg.Enabled && KmhGlobalBackend.Active;
            if (cfg == null || (!cfg.Enabled && !backend)) { why = "Connected Worlds is off"; return false; }
            if (!GlobalSetupState.Current_.IsRegistered) { why = "this server is not registered"; return false; }
            if (string.IsNullOrEmpty(KmhServerIdentity.Id)) { why = "this server has no identity yet"; return false; }
            // A private identity sends only what Federation and join tickets need.
            if (backend && !BackendMayUse(path)) { why = "a private identity sends nothing for a listing"; return false; }
            // Standings name players, so they go only on the owner's own switch, whatever else is on.
            if (IsLeaderboardsPath(path) && !cfg.PublishLeaderboards) { why = "Global.PublishLeaderboards is off"; return false; }
            return true;
        }

        internal static bool IsLeaderboardsPath(string path)
            => path != null && path.StartsWith("/api/v1/servers/", StringComparison.Ordinal) && path.EndsWith("/leaderboards", StringComparison.Ordinal);

        // Reports are allowed here too; Global.ForwardReports decides whether they go, on any identity.
        private static bool BackendMayUse(string path)
            => path == "/api/v1/servers/heartbeat"
            || path == "/api/v1/directory/key"
            || path.StartsWith("/api/v1/federations/", StringComparison.Ordinal)
            || path == "/api/v1/guilds/invites"
            || path == "/api/v1/guilds/reprove"
            || path == "/api/v1/guilds/status"
            || path == Reports.ReportForwarder.Path;

        public static List<string> Fields(Type bodyType)
        {
            var names = new List<string>();

            foreach (PropertyInfo p in bodyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                Add(names, p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? p.Name, p.PropertyType);
            }
            // Public fields go on the wire too, so nested records using them must be disclosed as well.
            foreach (FieldInfo f in bodyType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Add(names, f.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? f.Name, f.FieldType);

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static void Add(List<string> names, string name, Type type)
        {
            // The signature covers the other fields; it is not itself information about the server.
            if (name == "signature" || names.Contains(name)) return;

            // Nested records are expanded ("entries" hides the Workshop ids); if a walk finds nothing, the bare name stays.
            Type nested = ElementBody(type);
            List<string> children = nested == null ? null : Fields(nested);
            if (children != null && children.Count > 0)
            {
                foreach (string child in children) names.Add(name + "." + child);
                return;
            }
            names.Add(name);
        }

        // Only this assembly's own wire records are walked.
        private static Type ElementBody(Type type)
        {
            if (type == null || type == typeof(string)) return null;

            Type element = type.IsArray ? type.GetElementType()
                         : type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
                             ? type.GetGenericArguments()[0]
                         : null;

            if (element == null || element.IsPrimitive || element == typeof(string)) return null;
            return element.Assembly == typeof(KmhGlobalDisclosure).Assembly ? element : null;
        }

        public static void Describe(Action<string> reply)
        {
            GlobalConfig cfg = GlobalConfig.Current;
            bool on = cfg != null && cfg.Enabled;
            bool registered = GlobalSetupState.Current_.IsRegistered;
            bool backend = !on && KmhGlobalBackend.Allowed;

            reply("Colony Nexus Connected Worlds - what this server sends, and when.");
            reply("");
            reply(backend    ? (registered
                                ? $"  STATUS: private identity {KmhServerIdentity.Id} at {cfg.BaseUrl}, for Guild Federation only. Not listed."
                                : "  STATUS: Guild Federation is registering a private identity. Not listed.")
                : !on        ? "  STATUS: off. Nothing is sent to any directory."
                : !registered ? "  STATUS: enabled but not registered. Nothing is sent until you finish 'kmh global setup'."
                              : $"  STATUS: registered with {cfg.BaseUrl} as {KmhServerIdentity.Id}.");
            reply("");
            if (backend)
            {
                reply("  Guild Federation is on, so this server registers a private Colony Nexus Global identity on its own,");
                reply("  even though nobody set Global up. Nothing below is sent until it has completed registration, and");
                reply("  it is never listed: no listing details or mod list - only the heartbeat below and the Guild");
                reply("  Federation requests at the end. 'kmh global disable' stops all of it.");
            }
            else
            {
                reply("  Nothing below is sent unless BOTH are true: you enabled Connected Worlds, and you");
                reply("  completed registration. Turning it off or unlinking stops all of it.");
            }
            reply("");

            Section(reply, $"Every {cfg?.HeartbeatSeconds ?? 60}s while running (heartbeat)", typeof(HeartbeatBody));
            if (!backend)
            {
                Section(reply, "When your listing details change (metadata)", typeof(MetadataBody));
                Section(reply, "When your mod list changes (mod profile)", typeof(ModProfileBody));
            }

            if (Features.Discord.DiscordBridge.Config?.WorldChatChannelId != 0)
                Section(reply, "Per message, only while world chat is bridged", typeof(ChatRelayBody));
            else
                reply("  World chat relay: not configured, so no chat leaves this server.");

            bool share = cfg?.ShareDiagnostics == true;
            reply($"  Only if the directory asks AND Global.ShareDiagnostics is on (currently {(share ? "ON" : "off")}):");
            if (share) ReplyWrapped(reply, KmhDiagnosticsResponder.SnapshotFields);
            else reply("    nothing - a request is declined, and the decline says only that.");
            reply("");

            bool console = cfg?.AllowOperatorConsole == true;
            reply($"  Only while a directory operator holds an open console session AND");
            reply($"  Global.AllowOperatorConsole is on (currently {(console ? "ON" : "off")}):");
            if (console)
            {
                reply("    your server console as it is printed, every few seconds, and the full status export");
                reply("    including its data folder path and current leaderboard.");
            }
            else reply("    nothing - the session is declined, and the decline says only that.");
            reply("");

            if (FeaturesConfig.Current.GuildFederation)
            {
                reply("  Only for a guild an officer federates (Guild Federation):");
                Section(reply, "    creating or joining a federation", typeof(FederationCreateBody));
                Section(reply, "    verified activity, counted for the federation's renown and objectives",
                        typeof(FederationContributionEntry));
                reply("    'player' is the Colony Nexus Global id a join ticket proved - never an in-game name or address.");
                reply("");
            }

            bool reports = FeaturesConfig.Current.Reports && cfg?.ForwardReports == true;
            reply($"  Only when a player sends a report to the Colony Nexus developers AND Global.ForwardReports is on");
            reply($"  (currently {(cfg?.ForwardReports == true ? "ON" : "off")}):");
            if (reports)
            {
                Section(reply, "    what the player wrote and chose to attach, with this server's diagnostics",
                        typeof(Reports.ReportBody));
                reply("    This server's part is its build, health and connection counts, and the console lines that are");
                reply("    warnings, errors or about that player. Off keeps every report here, readable with 'kmh reports'.");
                reply("");
            }
            else reply("    nothing - every report stays on this server, readable with 'kmh reports'.");
            reply("");

            bool boards = cfg?.PublishLeaderboards == true && !backend;
            reply($"  Only when the standings change, at most every 5 minutes, AND Global.PublishLeaderboards is on");
            reply($"  (currently {(boards ? "ON" : "off")}):");
            if (boards)
            {
                Section(reply, "    this server's leaderboards for its public page (the top 100 of each)", typeof(LeaderboardPublishBody));
                reply("    'name' is the player's or guild's in-game name; banned players are left out.");
                reply("");
            }
            else reply("    nothing - the leaderboards stay in game and on your own Discord.");

            bool names = console || reports || boards;
            reply(names
                ? "  Never sent: player IP addresses, save files, colony contents, config files, or your Discord"
                : "  Never sent: player IP addresses, player names, save files, colony contents, config files,");
            reply(console ? "  bot token. Console lines CAN contain player names and file paths - that is what a console is."
                : reports ? "  bot token. A report names the player who sent it, and its console lines may name others."
                : boards  ? "  bot token. Leaderboards name the players and guilds on them."
                          : "  your Discord bot token, file paths, or anything from KMH-Data other than the fields above.");
            reply("");
            reply("  Turn it off:   kmh global disable        Unlink entirely: kmh global unlink");
        }

        private static void Section(Action<string> reply, string when, Type body)
        {
            reply($"  {when}:");
            ReplyWrapped(reply, Fields(body));
            reply("");
        }

        // Wrapped rather than one per line: a wall of forty lines is a disclosure nobody reads.
        private static void ReplyWrapped(Action<string> reply, IEnumerable<string> names)
        {
            string line = "    ";
            foreach (string f in names)
            {
                if (line.Length + f.Length + 2 > 96) { reply(line.TrimEnd(',', ' ')); line = "    "; }
                line += f + ", ";
            }
            if (line.Trim().Length > 0) reply(line.TrimEnd(',', ' '));
        }
    }
}
