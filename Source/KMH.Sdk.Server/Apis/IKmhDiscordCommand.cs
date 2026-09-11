using System.Collections.Generic;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>
    /// A <c>!command</c> that arrived in a channel the KMH bot watches, hiding Discord.NET's SocketMessage so
    /// extension code never links against a specific Discord.NET version.
    /// </summary>
    /// <remarks>
    /// Handlers run on the Discord gateway thread, so hand heavy work to your own thread; <see cref="Reply"/> is
    /// fire-and-forget and never blocks the gateway.
    /// </remarks>
    public interface IKmhDiscordCommand
    {
        /// <summary>The command word, lowercased, WITHOUT the bot prefix (e.g. "myext-foo" for "!myext-foo a b").</summary>
        string Command { get; }

        /// <summary>Whitespace-split arguments after the command word. Never null.</summary>
        IReadOnlyList<string> Args { get; }

        /// <summary>Everything after the command word as a single string. Empty when there were no args.</summary>
        string RawArgs { get; }

        /// <summary>Discord display name of the author.</summary>
        string AuthorDisplay { get; }

        /// <summary>Discord snowflake id of the author. Stable across handle renames.</summary>
        ulong AuthorId { get; }

        /// <summary>Id of the channel the command was sent in.</summary>
        ulong ChannelId { get; }

        /// <summary>
        /// The KMH username this author is linked to, empty if unlinked. Authorize account-scoped actions on this,
        /// never on a display name.
        /// </summary>
        string LinkedUsername { get; }

        /// <summary>Post a reply to the same channel. Fire-and-forget; safe to call once per command.</summary>
        void Reply(string text);
    }
}
