using System;
using System.Threading.Tasks;
using Discord;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Chat
{
    internal static class ChatMediaRefresh
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatMediaRefresh, OnRefresh);
        }

        private static void OnRefresh(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(username)) return;

            string reference = (env?.GetString("ref") ?? "").Trim();
            if (!ChatDiscordMedia.TryParseRef(reference, out ulong channelId, out ulong messageId, out ulong attachmentId))
            { Reply(client, reference, "", "that media reference is not valid"); return; }

            // This points the server's own bot at a channel, so the caller must already be able to see the message.
            string owningChannel = ChatStore.ChannelOfMediaRef(reference);
            if (string.IsNullOrEmpty(owningChannel) || !ChatHandler.CanUserAccess(username, owningChannel))
            {
                ServerLog.Verbose($"Chat media refresh: refused '{reference}' from {username} - no visible message carries it");
                Reply(client, reference, "", "that media reference is not valid");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    string fresh = await ReacquireAsync(channelId, messageId, attachmentId).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(fresh))
                    {
                        // A final answer, so the client stops offering a retry that can never succeed.
                        Reply(client, reference, "", "the original Discord message is no longer available");
                        return;
                    }

                    // A refreshed link is not privileged, so it faces the same vetting as any other.
                    string vetted = ChatImagePolicy.Vet(ChatConfig.Current, fresh, typedByPlayer: false,
                                                       knownImage: true, out string why);
                    if (string.IsNullOrEmpty(vetted)) { Reply(client, reference, "", $"refused: {why}"); return; }

                    ServerLog.Debug($"Chat media refresh: {reference} -> fresh url for {username}");
                    Reply(client, reference, vetted, "");
                }
                catch (Exception ex)
                {
                    ServerLog.Warn($"Chat media refresh failed for {reference}: {ex.Message}");
                    Reply(client, reference, "", "could not reach Discord to refresh that media");
                }
            });
        }

        internal static async Task<string> ReacquireAsync(ulong channelId, ulong messageId, ulong attachmentId)
        {
            IDiscordClient bot = Discord.DiscordBridge.Client;
            if (bot == null) return "";

            if (!(await bot.GetChannelAsync(channelId).ConfigureAwait(false) is IMessageChannel channel)) return "";
            IMessage msg = await channel.GetMessageAsync(messageId).ConfigureAwait(false);
            if (msg == null) return "";

            if (msg.Attachments != null)
                foreach (IAttachment a in msg.Attachments)
                {
                    if (a == null) continue;
                    // A single-attachment message can answer without an id, so a zero is not a failure.
                    if (attachmentId != 0 && a.Id != attachmentId) continue;
                    if (!string.IsNullOrEmpty(a.Url)) return a.Url;
                }

            // The proxy copy is preferred because it is the one Discord keeps re-signing.
            if (msg.Embeds != null)
                foreach (IEmbed e in msg.Embeds)
                {
                    string u = e.Image?.ProxyUrl ?? e.Image?.Url ?? e.Thumbnail?.ProxyUrl ?? e.Thumbnail?.Url;
                    if (!string.IsNullOrEmpty(u)) return u;
                }

            return "";
        }

        private static void Reply(ServerClient client, string reference, string url, string reason)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.ChatMediaRefreshed,
                                new { @ref = reference, url, ok = !string.IsNullOrEmpty(url), reason });
    }
}
