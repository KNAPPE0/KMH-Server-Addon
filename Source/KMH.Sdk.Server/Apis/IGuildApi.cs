using System.Collections.Generic;
using KMH.Sdk.Server.Records;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// Read AND mutate the KMH guild registry. Mutations here are
    /// server-authoritative and SYSTEM-level - they are not gated by an
    /// acting member's in-guild rank (your extension is trusted server
    /// code). For player-driven, rank-gated changes, players use the
    /// in-game GuildHall dialog and /kmh guild chat commands.
    /// </summary>
    public interface IGuildApi
    {
        /// <summary>Guild name → member count + treasury silver, for every guild on the server.</summary>
        IReadOnlyList<GuildSummaryRecord> GetAll();

        /// <summary>The guild a username belongs to, or empty string if guildless.</summary>
        string CurrentGuildOf(string username);

        /// <summary>True if two guild names are mutually allied.</summary>
        bool AreAllied(string guildA, string guildB);

        // -- system-level mutations --

        /// <summary>Create an empty guild. Returns false if the name is blank or already taken.</summary>
        bool CreateGuild(string name);

        /// <summary>Add a player to a guild at the given rank ("member"/"officer"/"moderator"/"admin"). Returns false if the guild is missing or the player is already in a guild.</summary>
        bool AddMember(string username, string guildName, string rank = "member");

        /// <summary>Set a guild's MOTD. Returns false if the guild doesn't exist.</summary>
        bool SetMotd(string guildName, string motd);

        // -- guild treasury (the shared guild vault, keyed by guild name) --

        /// <summary>Current silver in a guild's vault. 0 if the guild has no vault yet.</summary>
        long GetGuildSilver(string guildName);

        /// <summary>Add silver to a guild's vault. The contributor name is recorded in the vault's transaction log. Returns false on invalid input.</summary>
        bool DepositGuildSilver(string guildName, int amount, string contributor, string note = "");

        /// <summary>Take silver from a guild's vault. Returns false if the vault can't cover it (never goes negative).</summary>
        bool WithdrawGuildSilver(string guildName, int amount, string actor, string note = "");
    }
}
