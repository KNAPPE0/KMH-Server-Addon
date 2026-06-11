using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.SubProtocol
{
    // Server-side handshake: sends kmh.hello on connect (driven by Patch_PM_Chat_KmhSendHello, a Harmony postfix on
    // PM_Chat. SendLoginChatMessages) and handles the client's kmh.hello.ack reply
    //
    // Successful handshake = client has KMH-Patch loaded. Feature code can gate per-client pushes on this in the
    // future (skip sending treasury snapshots to stock-RWT clients that would render the JSON in their chat log)
    internal static class KmhHandshakeHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.HelloAck, OnHelloAck);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.Ping,     OnPing);
        }

        // Called from PM_Logins.FinishPostLogin via the Harmony postfix on PM_Chat.SendLoginChatMessages. Stock-RWT
        // clients see this as an invisible chat message under a zero-width-prefixed username - cosmetically odd,
        // functionally harmless. Patched clients silently intercept it
        public static void SendHelloTo(ServerClient client)
        {
            string username = client?.GetData<UserFile>()?.Username ?? "?";

            bool sent = KmhRouter.SendTo(client, KmhProtocol.Kind.Hello, new
            {
                v = KmhProtocol.CurrentVersion,
            });

            if (sent)
            {
                ServerLog.Verbose($"Sent kmh.hello (v{KmhProtocol.CurrentVersion}) to {username}");
            }
        }

        private static void OnHelloAck(ServerClient client, KmhEnvelope env)
        {
            string username = client?.GetData<UserFile>()?.Username ?? "?";
            int    clientVersion = env?.GetInt("v", 0) ?? 0;

            if (clientVersion == KmhProtocol.CurrentVersion)
            {
                ServerLog.Info($"Handshake complete with {username} (client v{clientVersion})");

                // Push initial state for features the patch mod would otherwise need to request explicitly.
                // LinkedAccounts is the best example - every dialog renders usernames via
                // LinkedAccountsCache.Format, and we want that cache populated from frame one rather than after the
                // first 8s auto-refresh tick
                try
                {
                    Features.LinkedAccounts.LinkedAccountsHandler.SendSnapshotTo(client);
                    Features.Reputation.ReputationHandler.SendSnapshotTo(client);
                    Features.Enforcement.EnforcementHandler.SendSnapshotTo(client);
                }
                catch (System.Exception ex)
                {
                    ServerLog.Error("Initial snapshot push failed", ex);
                }
            }
            else
            {
                ServerLog.Warn(
                    $"Version mismatch with {username}: client v{clientVersion}, server v{KmhProtocol.CurrentVersion}");
            }
        }

        // Diagnostic round-trip - client sends Ping, we reply with Pong. First real handler beyond the lifecycle
        // handshake; useful both as a "is the protocol alive?" check and as the template for every feature handler
        // we'll add later
        private static void OnPing(ServerClient client, KmhEnvelope env)
        {
            KmhRouter.SendTo(client, KmhProtocol.Kind.Pong, null);
            ServerLog.Verbose($"Pong sent to {client?.GetData<UserFile>()?.Username ?? "?"}");
        }
    }
}
