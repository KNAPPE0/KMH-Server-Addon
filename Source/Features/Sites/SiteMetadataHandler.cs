using System.Collections.Generic;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Features.Sites.Dto;
using KMHServerAddon.SubProtocol;
using Newtonsoft.Json;

namespace KMHServerAddon.Features.Sites
{
    // Deliberately unprivileged: the fingerprint guard decides whose facts become canonical, not a permission check.
    internal static class SiteMetadataHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteMetaPush, OnPush);
        }

        // Mirrors the client's SiteMetadataPush.
        private sealed class Push
        {
            [JsonProperty("meta")]        public List<SiteOutputMetadata> Meta { get; set; } = new List<SiteOutputMetadata>();
            [JsonProperty("chunk_index")] public int ChunkIndex { get; set; }
            [JsonProperty("chunk_total")] public int ChunkTotal { get; set; }
            [JsonProperty("fingerprint")] public string Fingerprint { get; set; } = "";
        }

        private static void OnPush(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username ?? "";
            Push payload = env?.DataAs<Push>();
            if (payload?.Meta == null || payload.Meta.Count == 0)
            {
                ServerLog.Verbose($"SiteMeta: push from {(string.IsNullOrEmpty(user) ? "?" : user)} carried no metadata.");
                return;
            }

            // Cleared as well as [JsonIgnore]d, so a future serializer change cannot quietly reopen the hole.
            foreach (SiteOutputMetadata m in payload.Meta)
            {
                if (m == null) continue;
                m.Family = SiteOutputFamilies.Unknown;
                m.Skill  = "";
                m.Source = "";
            }

            SiteCatalogStore.PushResult result = SiteCatalogStore.Accept(
                user, payload.Fingerprint, payload.ChunkIndex, payload.ChunkTotal, payload.Meta, out string detail);

            switch (result)
            {
                case SiteCatalogStore.PushResult.Accepted:
                    ServerLog.Info($"Site catalog: established from '{user}' - {detail}.");
                    Settle();
                    break;
                case SiteCatalogStore.PushResult.Refreshed:
                    ServerLog.Verbose($"Site catalog: refreshed from '{user}' - {detail}.");
                    Settle();
                    break;
                case SiteCatalogStore.PushResult.Accumulating:
                    ServerLog.Verbose($"SiteMeta: {user} - {detail}.");
                    break;
                case SiteCatalogStore.PushResult.Rejected:
                    KmhRouter.Notify(client, "neutral",
                        "Your item catalog does not match this server's established one, so it was not applied. "
                        + "Your sites still work; ask the owner if you think the server's catalog is out of date.");
                    break;
                default:
                    ServerLog.Verbose($"SiteMeta: ignored a push from {(string.IsNullOrEmpty(user) ? "?" : user)} - {detail}.");
                    break;
            }
        }

        // Classification arrives with the first player rather than at boot, so existing sites are re-skilled here.
        private static void Settle()
        {
            SiteCatalogStore.SaveToDisk();
            SiteStore.ReclassifySkills();
            SiteHandler.BroadcastCatalog();
            SiteHandler.BroadcastSnapshot();   // the re-skilled sites, so open windows show the right skill
        }
    }
}
