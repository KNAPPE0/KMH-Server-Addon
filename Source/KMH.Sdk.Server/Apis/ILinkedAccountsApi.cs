namespace KMH.Sdk.Server.Apis
{
    /// <summary>Read the in-game username ↔ Discord identity link map.</summary>
    public interface ILinkedAccountsApi
    {
        /// <summary>True if the username has an active Discord link.</summary>
        bool IsLinked(string username);

        /// <summary>Discord display name for a linked username, null otherwise.</summary>
        string DiscordDisplayFor(string username);

        /// <summary>Discord snowflake id for a linked username, 0 otherwise.</summary>
        ulong DiscordIdFor(string username);

        /// <summary>Reverse lookup by snowflake id (rename-resilient).</summary>
        string UsernameByDiscordId(ulong discordId);
    }
}
