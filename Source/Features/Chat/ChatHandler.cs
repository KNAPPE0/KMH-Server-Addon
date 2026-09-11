using System;
using System.Collections.Generic;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Chat
{
    // Sender identity always comes from the authenticated ServerClient, never from the packet.
    internal static class ChatHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatSend,          OnSend);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatRequest,       OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatBlock,         OnBlock);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatModerationReq, OnModerationRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.ChatRemove,        OnRemove);
        }

        private static void OnSend(ServerClient client, KmhEnvelope env)
        {
            string from = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(from)) return;
            string channel = env?.GetString("channel") ?? ChatChannels.Server;
            string body    = env?.GetString("body") ?? "";
            string token   = env?.GetString("token");

            if (!CanAccess(from, channel)) { KmhRouter.Notify(client, "negative", "You can't post to that channel."); return; }

            // Refused rather than filtered on the way out, since a DM has one recipient and the sender can simply be told.
            if (ChatChannels.TryParseDm(channel, out string dmA, out string dmB))
            {
                string other = string.Equals(dmA, from, StringComparison.OrdinalIgnoreCase) ? dmB : dmA;
                if (ChatModerationStore.IsBlocked(other, from))
                {
                    KmhRouter.Notify(client, "negative",
                        $"{other} has blocked you - your message was not delivered.");
                    Diagnostics.ServerLog.Info($"chat[{channel}] {from} -> {other} REFUSED (blocked).");
                    return;
                }
            }

            Dto.ChatMessage msg = ChatStore.Post(from, channel, body, token, "ingame", out string reason);
            if (msg == null)
            {
                if (!string.Equals(reason, "Duplicate message ignored.", StringComparison.Ordinal))
                    KmhRouter.Notify(client, "negative", reason ?? "Message couldn't be sent.");
                return;
            }
            Diagnostics.ServerLog.Info($"chat[{msg.Channel}] {from}: {msg.Body}");
            Broadcast(msg);
            Features.Discord.KmhChatDiscordBridge.RelayOutbound(msg);
            RaisePosted(msg);
        }

        // Returns the delivered count, so a relay can log where a message landed rather than only that it was accepted.
        public static int BroadcastExternal(Dto.ChatMessage msg) { int n = Broadcast(msg); RaisePosted(msg); return n; }

        private static void RaisePosted(Dto.ChatMessage msg)
            => Extensibility.KmhEventBus.Instance.RaiseChatMessagePosted(new KMH.Sdk.Server.Events.ChatMessagePostedEvent
            { MessageId = msg.Id, Channel = msg.Channel, FromUsername = msg.FromUsername, Body = msg.Body, Origin = msg.Origin });

        private static void OnRequest(ServerClient client, KmhEnvelope env)
        {
            if (client == null) return;
            string user = client.GetData<UserFile>()?.Username;
            string channel = env?.GetString("channel") ?? ChatChannels.Server;
            if (string.IsNullOrEmpty(user) || !CanAccess(user, channel)) return;
            List<Dto.ChatMessage> recent = ChatModerationStore.FilterOut(ChatStore.Recent(channel), ChatModerationStore.BlockedSetFor(user));
            KmhRouter.SendTo(client, KmhProtocol.Kind.ChatSnapshot, new { channel, messages = recent });
        }

        private static void OnBlock(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            if (!ChatConfig.Current.BlockingEnabled) { KmhRouter.Notify(client, "negative", "Blocking is disabled on this server."); return; }
            string target = env?.GetString("username") ?? "";
            bool on = env?.GetBool("on") ?? true;
            if (string.IsNullOrEmpty(target)) return;

            if (ChatModerationStore.SetBlock(user, target, on, out string refusal))
            {
                Diagnostics.ServerLog.Info($"chat moderation: {user} {(on ? "blocked" : "unblocked")} {target}");
                KmhRouter.Notify(client, on ? "neutral" : "positive", on
                    ? $"Blocked {target}. You will not see their messages, and their direct messages to you are refused."
                    : $"Unblocked {target}. You will see their messages again.");
            }
            else if (refusal != null) { KmhRouter.Notify(client, "negative", refusal); return; }
            SendModerationTo(client, user);
        }

        private static void OnModerationRequest(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (!string.IsNullOrEmpty(user)) SendModerationTo(client, user);
        }

        private static void OnRemove(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (!CanModerate(client)) { KmhRouter.Notify(client, "negative", "Removing messages is staff-only."); return; }
            string channel = env?.GetString("channel") ?? "";
            long id = env?.GetInt("id", 0) ?? 0;
            if (!ChatStore.Remove(channel, id)) return;

            Diagnostics.ServerLog.Warn($"chat moderation: {user} removed message #{id} from {channel}");
            RebroadcastSnapshot(channel);
            // Best-effort, because Discord being unreachable must not fail a removal that already happened in game.
            Features.Discord.KmhChatDiscordBridge.RedactOutbound(id, user);
        }

        // Unreachable from the wire on purpose: authorisation happened on Discord, so there is nothing to check here.
        internal static bool RemoveRelayed(long id, string byWhom)
        {
            if (id <= 0 || !ChatStore.Remove(ChatChannels.Server, id)) return false;
            Diagnostics.ServerLog.Warn($"chat moderation: {byWhom} removed message #{id} from {ChatChannels.Server}");
            RebroadcastSnapshot(ChatChannels.Server);
            return true;
        }

        // Both answers come from the connection and the owner's config, never from anything the client asserts.
        private static bool CanModerate(ServerClient client)
        {
            UserFile uf = client?.GetData<UserFile>();
            if (uf == null) return false;
            if (uf.IsAdmin) return true;
            return Features.Identity.StaffRoles.Priority(Features.Identity.StaffRegistry.RoleFor(uf.Username))
                   >= Features.Identity.StaffRoles.Priority(Features.Identity.StaffRoles.Moderator);
        }

        // Exposed for the media refresh, which must not answer for a channel the asker cannot read.
        internal static bool CanUserAccess(string user, string channel) => CanAccess(user, channel);

        private static bool CanAccess(string user, string channel)
        {
            if (string.IsNullOrEmpty(user) || !ChatChannels.IsWellFormed(channel)) return false;
            if (channel == ChatChannels.Server) return true;
            if (ChatChannels.TryParseGuild(channel, out string guildName))
                return string.Equals(Features.Guilds.GuildStore.CurrentGuildOf(user), guildName, StringComparison.OrdinalIgnoreCase);
            return ChatChannels.IsDmParticipant(user, channel);
        }

        private static int Broadcast(Dto.ChatMessage msg)
        {
            object payload = new { message = msg };
            int sent = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u) || !CanAccess(u, msg.Channel)) continue;
                if (ChatModerationStore.IsBlocked(u, msg.FromUsername)) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.ChatMessage, payload);
                sent++;
            }
            return sent;
        }

        // For a message already in everyone's log that CHANGED, such as a late link preview attaching to it.
        internal static void RebroadcastServerChannel() => RebroadcastSnapshot(ChatChannels.Server);
        private static void RebroadcastSnapshot(string channel)
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u) || !CanAccess(u, channel)) continue;
                List<Dto.ChatMessage> recent = ChatModerationStore.FilterOut(ChatStore.Recent(channel), ChatModerationStore.BlockedSetFor(u));
                KmhRouter.SendTo(c, KmhProtocol.Kind.ChatSnapshot, new { channel, messages = recent });
            }
        }

        private static void SendModerationTo(ServerClient client, string user)
            => KmhRouter.SendTo(client, KmhProtocol.Kind.ChatModeration,
                new { blocked = ChatModerationStore.BlockedBy(user), blocking_enabled = ChatConfig.Current.BlockingEnabled });
    }
}
