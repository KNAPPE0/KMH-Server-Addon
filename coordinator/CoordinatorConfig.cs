using System;
using System.Collections.Generic;
using System.IO;
using Kmh.Discord.Wire;
using Newtonsoft.Json;

namespace KmhDiscordCoordinator
{
    // Everything the coordinator needs except the bot token, which only ever comes from the environment.
    public sealed class CoordinatorConfig
    {
        [JsonProperty("schema_version")] public int SchemaVersion { get; set; } = 1;
        [JsonProperty("listen")]         public string Listen { get; set; } = "http://127.0.0.1:20079";
        [JsonProperty("guild_id")]       public string GuildId { get; set; } = "";
        [JsonProperty("global_base_url")] public string GlobalBaseUrl { get; set; } = "https://global.knappe0.games";

        [JsonProperty("command_timeout_seconds")] public int CommandTimeoutSeconds { get; set; } = 15;
        [JsonProperty("lease_seconds")]           public int LeaseSeconds { get; set; } = 600;
        [JsonProperty("revocation_check_seconds")] public int RevocationCheckSeconds { get; set; } = 300;
        // How long an already-confirmed server keeps its route while Global is unreachable.
        [JsonProperty("global_grace_seconds")]     public int GlobalGraceSeconds { get; set; } = 1800;

        [JsonProperty("servers")] public List<ServerEntry> Servers { get; set; } = new List<ServerEntry>();

        public sealed class ServerEntry
        {
            [JsonProperty("server_id")]  public string ServerId { get; set; } = "";
            [JsonProperty("alias")]      public string Alias { get; set; } = "";
            [JsonProperty("public_key")] public string PublicKey { get; set; } = "";
            [JsonProperty("enabled")]    public bool   Enabled { get; set; } = true;
            [JsonProperty("official")]   public bool   Official { get; set; }

            // Role -> channel id. A channel may belong to exactly one server; see Validate.
            [JsonProperty("channels")] public Dictionary<string, string> Channels { get; set; }
                = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static string TokenFromEnvironment()
        {
            try { return (Environment.GetEnvironmentVariable("KMH_DISCORD_BOT_TOKEN") ?? "").Trim(); }
            catch (Exception) { return ""; }
        }

        public static CoordinatorConfig Load(string path)
        {
            string text = File.ReadAllText(path);
            CoordinatorConfig cfg = JsonConvert.DeserializeObject<CoordinatorConfig>(text)
                ?? throw new InvalidDataException("coordinator config did not parse");
            cfg.Validate();
            return cfg;
        }

        // A channel shared by two servers relays one chat line into two worlds, so it is fatal, not a warning.
        public void Validate()
        {
            Dictionary<ulong, string> owner = new Dictionary<ulong, string>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ServerEntry s in Servers ?? new List<ServerEntry>())
            {
                if (string.IsNullOrWhiteSpace(s.ServerId)) throw new InvalidDataException("a server entry has no server_id");
                if (string.IsNullOrWhiteSpace(s.Alias))    throw new InvalidDataException($"{s.ServerId} has no alias");
                if (string.IsNullOrWhiteSpace(s.PublicKey)) throw new InvalidDataException($"{s.Alias} has no public_key");
                if (!ids.Add(s.ServerId))  throw new InvalidDataException($"duplicate server_id {s.ServerId}");
                if (!aliases.Add(s.Alias)) throw new InvalidDataException($"duplicate alias {s.Alias}");

                foreach (KeyValuePair<string, string> kv in s.Channels)
                {
                    if (!ulong.TryParse((kv.Value ?? "").Trim(), out ulong id) || id == 0) continue;
                    if (owner.TryGetValue(id, out string other) && other != s.Alias)
                        throw new InvalidDataException(
                            $"channel {id} is mapped by both '{other}' and '{s.Alias}' - one channel belongs to one server");
                    owner[id] = s.Alias;
                }
            }
        }

        public static CoordinatorConfig Sample()
        {
            CoordinatorConfig c = new CoordinatorConfig();
            c.Servers.Add(new ServerEntry
            {
                ServerId = "<the server_id kmh global status prints>",
                Alias = "vanilla",
                PublicKey = "<the public key kmh global status prints>",
                Official = true,
                Channels =
                {
                    [KmhChannelRole.KmhChat]       = "",
                    [KmhChannelRole.Chat]          = "",
                    [KmhChannelRole.Announcements] = "",
                    [KmhChannelRole.Leaderboard]   = "",
                    [KmhChannelRole.Marketplace]   = "",
                    [KmhChannelRole.SiteEvents]    = "",
                    [KmhChannelRole.Admin]         = "",
                    [KmhChannelRole.Commands]      = "",
                },
            });
            return c;
        }
    }
}
