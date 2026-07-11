using KMHServerAddon.Diagnostics;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Sites
{
    // Wire handler for kmh.site.* - build / join / leave / set-destination / cancel + snapshot. Mutations reply
    // with a chat reason and rebroadcast a caller-scoped snapshot so every open Sites dialog refreshes
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
        }

        private static void OnRequest(ServerClient client, KmhEnvelope env) => SendSnapshotTo(client);

        // Curated, server-classified output catalog for the client's Site output picker.
        private static void OnCatalogRequest(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            bool includeBlocked = env?.GetBool("include_blocked") ?? false;
            KmhRouter.SendTo(client, KmhProtocol.Kind.SiteCatalog, SiteStore.BuildOutputCatalogFor(user, includeBlocked));
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
                env.GetInt("marketplace_unit_price", 1));
            Reply(client, ok, ok ? reason : $"Build failed: {reason}");
            if (ok) { BroadcastSnapshot(); SendTreasuryTo(client); }
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
            var (ok, reason) = SiteStore.SetDestination(user, env?.GetInt("tile", -1) ?? -1, env?.GetString("destination", "") ?? "");
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

        // --- delivery helpers ---

        internal static void SendSnapshotTo(ServerClient client)
        {
            string user = client?.GetData<UserFile>()?.Username;
            if (string.IsNullOrEmpty(user)) return;
            KmhRouter.SendTo(client, KmhProtocol.Kind.SiteSnapshot, SiteStore.BuildSnapshotFor(user));
        }

        internal static void BroadcastSnapshot()
            => KmhRouter.BroadcastToInterested(KmhProtocol.Kind.SiteSnapshot, u => SiteStore.BuildSnapshotFor(u));

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
