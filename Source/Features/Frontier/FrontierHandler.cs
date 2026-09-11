using System;
using KMHServerAddon.Features.Frontier.Dto;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Frontier
{
    // An older client has no handler for this kind and simply never replies, so no capability negotiation is needed.
    internal static class FrontierHandler
    {
        public static void Register()
            => KmhRouter.RegisterHandler(KmhProtocol.Kind.FrontierPlacementPropose, OnPropose);

        private static void OnPropose(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user) || env == null) return;

            bool accepted = KmhWorldDirector.TryRecordProposal(
                user, env.GetString("token_id", "") ?? "", env.GetInt("tile", -1),
                env.GetInt("layer", 0), env.GetString("fingerprint", "") ?? "", out string why);

            // Silent on refusal, because telling a client why its tile lost only helps someone probing the rule.
            if (!accepted) Diagnostics.ServerLog.Verbose($"Frontier: placement proposal from {user} refused - {why}");
        }

        // Sent to every verified client, not the interested ones: nobody asks for this, so nothing marks them interested.
        internal static int AskForPlacement(PlacementToken token)
        {
            if (token == null) return 0;
            var req = new PlacementRequest
            {
                TokenId = token.Id, Nonce = token.Nonce, Template = token.Template,
                ExcludeTiles = token.ExcludeTiles, ExpiresUtcTicks = token.ExpiresUtcTicks,
            };

            int asked = 0;
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                if (string.IsNullOrEmpty(c.GetData<UserFile>()?.Username)) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.FrontierPlacementRequest, req);
                asked++;
            }
            return asked;
        }
    }
}
