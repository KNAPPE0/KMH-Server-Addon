using System.Collections.Generic;

namespace KMH.Sdk.Server.Apis
{
    /// <summary>A <c>!command</c> from a channel the KMH bot watches, hiding Discord.NET so extensions link no version of it.</summary>
    /// <remarks>Handlers run on the Discord gateway thread, so move heavy work off it; <see cref="Reply"/> never blocks.</remarks>
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

        /// <summary>The linked KMH username, empty if unlinked; authorize account actions on this, never a display name.</summary>
        string LinkedUsername { get; }

        /// <summary>Post a reply to the same channel. Fire-and-forget; safe to call once per command.</summary>
        void Reply(string text);
    }
}
