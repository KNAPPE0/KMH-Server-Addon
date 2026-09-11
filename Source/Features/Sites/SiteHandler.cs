using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Sites
{
    internal static class SiteHandler
    {
        public static void Register()
        {
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteRequest,        OnRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteBuild,          OnBuild);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteJoin,           OnJoin);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteLeave,          OnLeave);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteSetDestination, OnSetDestination);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteCancel,         OnCancel);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteCatalogRequest, OnCatalogRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteQuoteRequest,   OnQuoteRequest);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteBuildingAdd,    OnBuildingAdd);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteBuildingRemove, OnBuildingRemove);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteStorageCollect, OnStorageCollect);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteClaim,          OnClaim);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteRepair,         OnRepair);
            KmhRouter.RegisterHandler(KmhProtocol.Kind.SiteSetup,          OnSetup);
        }

        // Validated by the same rules a normal build goes through - the client only names what it wants.
        private static void OnSetup(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            var (ok, reason) = SiteStore.ConfigureCaptured(
                user,
                env.GetInt("tile", -1),
                env.GetString("item_def_name", "") ?? "",
                env.GetInt("base_amount", 1),
                (float)env.GetInt("market_value", 0),
                env.GetString("access_mode", Dto.SiteEntry.AccessGuildOnly) ?? Dto.SiteEntry.AccessGuildOnly,
                env.GetInt("owner_tax_percent", 10),
                env.GetString("owner_destination", Dto.SiteEntry.DestTreasury) ?? Dto.SiteEntry.DestTreasury,
                env.GetInt("marketplace_unit_price", 1),
                env.GetString("archetype", "") ?? "");
            Reply(client, ok, ok ? reason : $"Setup failed: {reason}");
            if (ok) BroadcastSnapshot();
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env) => SendSnapshotTo(client);

        // Never chats a refusal: the reason rides in the quote so the build window shows it inline.
        private static void OnQuoteRequest(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user) || env == null) return;
            Dto.SiteBuildQuote quote = SiteStore.QuoteBuild(
                user,
                env.GetString("item_def_name", "") ?? "",
                env.GetInt("base_amount", 1),
                (float)env.GetInt("market_value", 0),
                env.GetString("archetype", "") ?? "",
                env.GetInt("tile", -1));
            KmhRouter.SendTo(client, KmhProtocol.Kind.SiteQuote, quote);
        }

        // Curated, server-classified output catalog for the client's Site output picker.
        private static void OnCatalogRequest(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            bool includeBlocked = env?.GetBool("include_blocked") ?? false;
            KmhRouter.SendTo(client, KmhProtocol.Kind.SiteCatalog, SiteStore.BuildOutputCatalogFor(user, includeBlocked));
        }

        // Sent on a catalog change, so an open Build-a-Site window starts filtering without being reopened.
        public static void BroadcastCatalog()
        {
            foreach (ServerClient c in Network.ServerClients.Keys)
            {
                if (c?.IsVerified != true) continue;
                string u = c.GetData<UserFile>()?.Username;
                if (string.IsNullOrEmpty(u)) continue;
                KmhRouter.SendTo(c, KmhProtocol.Kind.SiteCatalog, SiteStore.BuildOutputCatalogFor(u, false));
            }
        }

        private static void OnBuild(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            var (ok, reason) = SiteStore.Build(
                user,
                env.GetInt("tile", -1),
                env.GetString("item_def_name", "") ?? "",
                env.GetInt("base_amount", 1),
                (float)env.GetInt("market_value", 0),
                env.GetString("access_mode", Dto.SiteEntry.AccessGuildOnly) ?? Dto.SiteEntry.AccessGuildOnly,
                env.GetInt("owner_tax_percent", 10),
                env.GetString("owner_destination", Dto.SiteEntry.DestTreasury) ?? Dto.SiteEntry.DestTreasury,
                env.GetInt("marketplace_unit_price", 1),
                env.GetString("archetype", "") ?? "");
            Reply(client, ok, ok ? reason : $"Build failed: {reason}");
            if (ok) { BroadcastSnapshot(); SendTreasuryTo(client); }
        }

        private static void OnBuildingAdd(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            var op = new Security.KmhOpClaim("site.building_add", user, env);
            if (!op.Begin()) { SendSnapshotTo(client); SendTreasuryTo(client); return; }

            var (ok, reason) = SiteStore.AddBuilding(user, env.GetInt("tile", -1), env.GetString("kind", "") ?? "");
            if (!ok) op.Release();
            Reply(client, ok, reason);
            if (ok) { BroadcastSnapshot(); SendTreasuryTo(client); }
        }

        private static void OnBuildingRemove(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            var (ok, reason) = SiteStore.RemoveBuilding(user, env.GetInt("tile", -1), env.GetInt("index", -1),
                                                        env.GetString("kind", "") ?? "", env.GetString("state", "") ?? "");
            Reply(client, ok, reason);
            if (ok) BroadcastSnapshot();
        }

        private static void OnStorageCollect(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            var (ok, reason) = SiteStore.CollectStorage(user, env.GetInt("tile", -1));
            Reply(client, ok, reason);
            if (ok) { BroadcastSnapshot(); SendTreasuryTo(client); }
        }

        private static void OnRepair(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            var (ok, reason) = SiteStore.RepairSite(user, env.GetInt("tile", -1));
            Reply(client, ok, reason);
            if (ok) { BroadcastSnapshot(); SendTreasuryTo(client); }
        }

        private static void OnClaim(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (env == null) return;
            // The client says personal-or-guild but never names the guild, so it cannot claim for one it has left.
            var (ok, reason) = SiteStore.ClaimOutpost(user, env.GetInt("tile", -1), env.GetBool("for_guild", false));
            Reply(client, ok, reason);
            if (ok) BroadcastSnapshot();
        }

        private static void OnJoin(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            var (ok, reason, changed) = SiteStore.JoinWorker(user, env?.GetInt("tile", -1) ?? -1, env?.GetInt("base_skill_level", 0) ?? 0,
                env?.GetString("pawn_name") ?? "", env?.GetInt("pawn_load_id", -1) ?? -1,
                env?.GetBool("present", true) ?? true);
            if (!ok) { Reply(client, false, $"Join failed: {reason}"); return; }
            // Periodic no-change re-validations stay silent - no toast, no broadcast (would be per-worker spam).
            if (!changed) return;
            Reply(client, true, reason);
            BroadcastSnapshot();
        }

        private static void OnLeave(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            var (ok, reason) = SiteStore.LeaveWorker(user, env?.GetInt("tile", -1) ?? -1);
            Reply(client, ok, ok ? reason : $"Leave failed: {reason}");
            if (ok) BroadcastSnapshot();
        }

        private static void OnSetDestination(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            var (ok, reason) = SiteStore.SetDestination(user, env?.GetInt("tile", -1) ?? -1,
                                                        env?.GetString("destination", "") ?? "",
                                                        env?.GetString("scope", "") ?? "");
            Reply(client, ok, ok ? reason : $"Failed: {reason}");
            if (ok) BroadcastSnapshot();
        }

        private static void OnCancel(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            var (ok, reason) = SiteStore.Cancel(user, env?.GetInt("tile", -1) ?? -1);
            Reply(client, ok, ok ? reason : $"Failed: {reason}");
            if (ok) BroadcastSnapshot();
        }

        internal static void SendSnapshotTo(ServerClient client)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.SiteSnapshot, SiteStore.BuildSnapshotFor(user));
        }

        // Verified, not interested: an outpost is drawn on the world map without anyone opening Sites.
        internal static void BroadcastSnapshot()
            => KmhRouter.BroadcastToVerified(KmhProtocol.Kind.SiteSnapshot, u => SiteStore.BuildSnapshotFor(u));

        private static void SendTreasuryTo(ServerClient client)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.TreasurySnapshot, Treasury.TreasuryStore.GetSnapshotFor(user));
        }

        // Toast the action result: store success messages confirm what happened, failures explain why
        private static void Reply(ServerClient client, bool ok, string text)
            => KmhRouter.Notify(client, ok ? "positive" : "negative", text);
    }
}
