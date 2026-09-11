namespace KMHServerAddon.SubProtocol
{
    // Keep byte-identical with client KmhProtocol; zero-width username prefix prevents player-name collisions.
    internal static class KmhProtocol
    {
        // A mismatch gates KMH off entirely, so a client can never half-use economy semantics it does not implement.
        public const int CurrentVersion = 2;

        // Detects a version gap to nudge the player; unlike CurrentVersion it never gates the connection.
        public const string BuildVersion = KmhVersion.Build;

        public const string SystemUsername = "​[KMH-SYS]"; // server -> client
        public const string ClientUsername = "​[KMH-CLI]"; // client -> server

        // Known packet kinds. Add new ones here only when their handlers are registered.
        public static class Kind
        {
            public const string Hello        = "kmh.hello";
            public const string HelloAck     = "kmh.hello.ack";

            public const string Ping         = "kmh.ping";
            public const string Pong         = "kmh.pong";

            public const string Notice       = "kmh.notice";                  // server -> client

            // Tells the client an op-id request reached a terminal decision, so it can stop treating that action as outstanding.
            public const string OpResult     = "kmh.op.result";               // server -> client

            // The client reporting from its own save state that it holds a delivery; carries no amounts, it discharges value rather than creating it.
            public const string DeliveryAck  = "kmh.delivery.ack";            // client -> server

            public const string NotifyQueued = "kmh.notify.queued";           // server -> client

            public const string PlayerStatsRequest      = "kmh.player_stats.request";    // client -> server
            public const string PlayerStatsSnapshot     = "kmh.player_stats.snapshot";   // server -> client
            public const string ColonyReport            = "kmh.colony.report";           // client -> server
            public const string PlayerActive           = "kmh.player_stats.active";      // client -> server (seconds active since its last report)
            public const string ColonistRequest         = "kmh.colonist.request";        // client -> server
            public const string ColonistProfile         = "kmh.colonist.profile";        // server -> client
            public const string ColonistRosterRequest   = "kmh.records.colonists.request"; // client -> server
            public const string ColonistRoster          = "kmh.records.colonists";         // server -> client
            public const string SeasonArchiveRequest    = "kmh.archive.season.request";    // client -> server
            public const string SeasonArchive           = "kmh.archive.season";            // server -> client

            // The caller's vault and guild are resolved from authenticated identity; a client never names either.
            public const string TreasuryRequest         = "kmh.treasury.request";        // client -> server
            public const string TreasurySnapshot        = "kmh.treasury.snapshot";       // server -> client
            public const string TreasuryDepositSilver   = "kmh.treasury.deposit_silver"; // client -> server
            public const string TreasuryWithdrawSilver  = "kmh.treasury.withdraw_silver";// client -> server
            public const string TreasuryDepositItem     = "kmh.treasury.deposit_item";   // client -> server
            public const string TreasuryWithdrawItem    = "kmh.treasury.withdraw_item";  // client -> server
            public const string TreasuryGrant           = "kmh.treasury.grant";          // server -> client (materialize a confirmed withdrawal into the colony)
            public const string TreasuryDepositConfirm  = "kmh.treasury.deposit_confirm";   // client -> server (these deposit txns are now durably saved locally)
            public const string TreasuryDepositReconcile= "kmh.treasury.deposit_reconcile"; // client -> server (full set of durably-saved deposit txns, sent on connect)
            public const string TreasuryDepositPreflight= "kmh.treasury.deposit_preflight"; // client -> server (approve BEFORE removing local goods)
            public const string TreasuryDepositApproval = "kmh.treasury.deposit_approval";  // server -> client (approve/deny + short-lived token)

            public const string MarketplaceRequest      = "kmh.marketplace.request";     // client -> server
            public const string MarketplaceSnapshot     = "kmh.marketplace.snapshot";    // server -> client
            public const string MarketplaceBuy          = "kmh.marketplace.buy";         // client -> server
            public const string MarketplaceCancel       = "kmh.marketplace.cancel";      // client -> server
            public const string MarketplacePost         = "kmh.marketplace.post";        // client -> server

            public const string QuestRequest            = "kmh.quest.request";           // client -> server
            public const string QuestSnapshot           = "kmh.quest.snapshot";          // server -> client
            public const string QuestClaim              = "kmh.quest.claim";             // client -> server
            public const string QuestSubmit             = "kmh.quest.submit";            // client -> server
            public const string QuestCancel             = "kmh.quest.cancel";            // client -> server
            public const string QuestPost               = "kmh.quest.post";              // client -> server
            public const string QuestApprove            = "kmh.quest.approve";           // client -> server (poster signs off on Bounty Submit)
            public const string QuestAbandon            = "kmh.quest.abandon";           // client -> server (claimer drops a claimed quest)
            public const string QuestSubmitProof        = "kmh.quest.submit_proof";      // client -> server (Custom: claimer submits proof for review)
            public const string QuestReview             = "kmh.quest.review";            // client -> server (poster approves/rejects a PendingReview)
            public const string QuestVerify             = "kmh.quest.verify";            // client -> server (auto-verify report for escort/defend/hunt/build)

            public const string GuildRequest            = "kmh.guild.request";            // client -> server
            public const string GuildSnapshot           = "kmh.guild.snapshot";           // server -> client
            public const string GuildPromote            = "kmh.guild.promote";            // client -> server
            public const string GuildDemote             = "kmh.guild.demote";             // client -> server
            public const string GuildKick               = "kmh.guild.kick";               // client -> server
            public const string GuildBuyPerk            = "kmh.guild.buy_perk";           // client -> server
            public const string GuildSetMotd            = "kmh.guild.set_motd";           // client -> server
            public const string GuildLeave              = "kmh.guild.leave";              // client -> server
            public const string GuildDonate             = "kmh.guild.donate";             // client -> server
            public const string GuildWithdraw           = "kmh.guild.withdraw";           // client -> server (guild vault -> personal, rank-capped)
            public const string GuildTransferOwner      = "kmh.guild.transfer_owner";     // client -> server ({ username }) Owner only
            public const string GuildProposeAlliance    = "kmh.guild.propose_alliance";   // client -> server
            public const string GuildAcceptAlliance     = "kmh.guild.accept_alliance";    // client -> server
            public const string GuildBreakAlliance      = "kmh.guild.break_alliance";     // client -> server
            public const string GuildDeclareHostile     = "kmh.guild.declare_hostile";    // client -> server
            public const string GuildClearHostile       = "kmh.guild.clear_hostile";      // client -> server
            public const string GuildSaveSettings       = "kmh.guild.save_settings";      // client -> server
            public const string GuildInvite             = "kmh.guild.invite";             // client -> server (admin/mod invites a player)
            public const string GuildDeclineInvite      = "kmh.guild.decline_invite";     // client -> server (invitee turns an invite down)
            public const string GuildInvitablesRequest  = "kmh.guild.invitables.request"; // client -> server (known guildless players for the invite picker)
            public const string GuildInvitablesSnapshot = "kmh.guild.invitables.snapshot"; // server -> client
            public const string GuildSetOpenJoin        = "kmh.guild.set_open_join";      // client -> server (admin toggles open join)
            public const string GuildJoin               = "kmh.guild.join";               // client -> server (join an open or invited guild)
            public const string GuildCreate             = "kmh.guild.create";             // client -> server (create a guild + join as admin)
            public const string GuildHallSet            = "kmh.guild.hall.set";           // client -> server (admin sets/moves the Guild Hall tile)
            public const string GuildHallRemove         = "kmh.guild.hall.remove";        // client -> server (admin removes the Guild Hall)

            public const string GuildLeaderboardRequest = "kmh.guild_leaderboard.request"; // client -> server
            public const string GuildLeaderboardSnapshot = "kmh.guild_leaderboard.snapshot"; // server -> client

            public const string LinkedAccountsRequest   = "kmh.linked_accounts.request";  // client -> server
            public const string LinkedAccountsSnapshot  = "kmh.linked_accounts.snapshot"; // server -> client
            public const string LinkRequest             = "kmh.link.request";             // client -> server (mint a Discord link code)
            public const string LinkCode                = "kmh.link.code";                // server -> client (the minted code + ttl)

            public const string ReputationRequest      = "kmh.reputation.request";      // client -> server
            public const string ReputationSnapshot     = "kmh.reputation.snapshot";     // server -> client

            public const string WorldRequest           = "kmh.world.request";           // client -> server
            public const string WorldSnapshot          = "kmh.world.snapshot";          // server -> client
            public const string WorldContribute        = "kmh.world.contribute";        // client -> server
            public const string WorldDeliver           = "kmh.world.deliver";           // client -> server

            public const string AuctionRequest         = "kmh.auction.request";         // client -> server
            public const string AuctionSnapshot        = "kmh.auction.snapshot";        // server -> client
            public const string AuctionPost            = "kmh.auction.post";            // client -> server
            public const string AuctionBid             = "kmh.auction.bid";             // client -> server (bid silver from treasury)
            public const string AuctionCancel          = "kmh.auction.cancel";          // client -> server (seller, pre-bid only)

            public const string WantRequest            = "kmh.want.request";            // client -> server
            public const string WantSnapshot           = "kmh.want.snapshot";           // server -> client
            public const string WantPost               = "kmh.want.post";               // client -> server (escrow silver)
            public const string WantFulfill            = "kmh.want.fulfill";            // client -> server (deliver items for payout)
            public const string WantCancel             = "kmh.want.cancel";             // client -> server (buyer, refund escrow)

            public const string SiteRequest            = "kmh.site.request";            // client -> server
            public const string SiteSnapshot           = "kmh.site.snapshot";           // server -> client
            public const string SiteBuild              = "kmh.site.build";              // client -> server
            public const string SiteJoin               = "kmh.site.join";               // client -> server (worker join)
            public const string SiteLeave              = "kmh.site.leave";              // client -> server (worker leave)
            public const string SiteSetDestination     = "kmh.site.set_destination";    // client -> server
            public const string SiteCancel             = "kmh.site.cancel";             // client -> server (owner removes)
            public const string SiteCatalogRequest     = "kmh.site.catalog.request";    // client -> server (curated output picker)
            public const string SiteCatalog            = "kmh.site.catalog";            // server -> client (classified allowed outputs)
            public const string SiteQuoteRequest       = "kmh.site.quote.request";      // client -> server (price a build before committing)
            public const string SiteQuote              = "kmh.site.quote";              // server -> client (what that build would cost)
            public const string SiteBuildingAdd        = "kmh.site.building.add";       // client -> server (owner, charges silver)
            public const string SiteBuildingRemove     = "kmh.site.building.remove";    // client -> server (owner, no refund)
            public const string SiteStorageCollect     = "kmh.site.storage.collect";    // client -> server (owner)
            public const string SiteClaim              = "kmh.site.claim";              // client -> server (capture an outpost)
            public const string SiteRepair             = "kmh.site.repair";             // client -> server (owner, charges silver)
            public const string SiteSetup              = "kmh.site.setup";              // client -> server (one-time config of a captured outpost)

            // Frontier Operations. The server asks capable clients where an outpost could go; it never judges geography.
            public const string FrontierPlacementRequest = "kmh.frontier.placement.request"; // server -> client
            public const string FrontierPlacementPropose = "kmh.frontier.placement.propose"; // client -> server

            // Segments are world infrastructure and reach everyone; projects and escrow are the caller's own.
            public const string RoadworksRequest       = "kmh.roadworks.request";       // client -> server
            public const string RoadworksSnapshot      = "kmh.roadworks.snapshot";      // server -> client
            public const string RoadworksStart         = "kmh.roadworks.start";         // client -> server (route + tier, charges silver)
            public const string RoadworksCancel        = "kmh.roadworks.cancel";        // client -> server (refunds unbuilt segments)

            public const string ItemLabels              = "kmh.item_labels";              // client -> server
            public const string SiteMetaPush            = "kmh.site_meta";                // client -> server
            // A separate envelope from the labels push, which is already near the frame cap.
            public const string ItemValues              = "kmh.item_values";              // client -> server
            public const string ConditionDefs           = "kmh.condition_defs";           // client -> server

            public const string DebugLog                = "kmh.debug.log";                // client -> server

            public const string EnforcementSnapshot     = "kmh.enforcement.snapshot";      // server -> client
            public const string EnforcementSnapshotRequest = "kmh.enforcement.snapshot.request"; // client -> server (refresh on dialog open, e.g. after being op'd mid-session)
            public const string EnforcementProfileRequest = "kmh.enforcement.profile.request"; // client -> server (only when the local hash differs)
            public const string EnforcementProfileBegin = "kmh.enforcement.profile.begin"; // server -> client (chunked profile header)
            public const string EnforcementProfileChunk = "kmh.enforcement.profile.chunk"; // server -> client
            public const string EnforcementProfileEnd   = "kmh.enforcement.profile.end";   // server -> client
            public const string EnforcementRestore      = "kmh.enforcement.restore";       // server -> client (lift enforcement, restore personal configs)
            public const string EnforcementSetEnabled   = "kmh.enforcement.set_enabled";   // client -> server (admin toggles enforcement)
            public const string EnforcementSetSafe      = "kmh.enforcement.set_safe";      // client -> server (admin adds/removes a safe mod)
            public const string EnforcementSetFlag      = "kmh.enforcement.set_flag";      // client -> server (admin toggles admin_bypass / preserve_personal)
            public const string EnforcementUploadBegin  = "kmh.enforcement.upload.begin";  // client -> server (admin publishes their configs as the profile)
            public const string EnforcementUploadChunk  = "kmh.enforcement.upload.chunk";  // client -> server
            public const string EnforcementUploadEnd    = "kmh.enforcement.upload.end";    // client -> server

            public const string MailSend     = "kmh.mail.send";     // client -> server (to, subject, body, attach_silver)
            public const string MailRequest  = "kmh.mail.request";  // client -> server (refresh inbox)
            public const string MailMarkRead = "kmh.mail.read";     // client -> server (id)
            public const string MailDelete   = "kmh.mail.delete";   // client -> server (id)
            public const string MailAccept   = "kmh.mail.accept";   // client -> server (id) claim escrowed attachment
            public const string MailDecline  = "kmh.mail.decline";  // client -> server (id) return attachment to sender
            public const string MailRecall   = "kmh.mail.recall";   // client -> server (id) sender pulls back their own unread attachment
            public const string MailSnapshot = "kmh.mail.snapshot"; // server -> client (inbox + unread)

            public const string ChatSend     = "kmh.chat.send";     // client -> server (channel, body, token)
            public const string ChatRequest  = "kmh.chat.request";  // client -> server (channel) -> recent history
            public const string ChatSnapshot = "kmh.chat.snapshot"; // server -> client (channel + recent messages)
            public const string ChatMessage  = "kmh.chat.message";  // server -> client (single new message)
            public const string ChatBlock        = "kmh.chat.block";        // client -> server (username, on) block/unblock a player
            public const string ChatModerationReq = "kmh.chat.moderation.request"; // client -> server (fetch my block list)
            public const string ChatModeration   = "kmh.chat.moderation";   // server -> client (my block list)
            public const string ChatRemove       = "kmh.chat.remove";       // client -> server (channel, id) admin removes a message
            // Named under kmh.chat.* so they ride the chat feature gate; only an id travels, never the bytes.
            public const string ChatMediaRequest = "kmh.chat.media.request"; // client -> server (id)
            public const string ChatMediaMeta    = "kmh.chat.media.meta";    // server -> client (id, ok, mime, bytes, chunks, hash, w, h, frames)
            public const string ChatMediaChunk   = "kmh.chat.media.chunk";   // server -> client (id, i, n, b64)
            // Discord signs CDN links for 24h but chat history keeps a message for 48, so a stored link dies first.
            public const string ChatMediaRefresh   = "kmh.chat.media.refresh";   // client -> server (ref)
            public const string ChatMediaRefreshed = "kmh.chat.media.refreshed"; // server -> client (ref, ok, url, reason)
            public const string ChatRosterRequest = "kmh.chat.roster.request";  // client -> server (who is here)
            public const string ChatRoster       = "kmh.chat.roster";          // server -> client (online, offline)
            public const string VideoResolve       = "kmh.chat.video.resolve";        // client -> server (watch_url, height)
            public const string VideoResolved      = "kmh.chat.video.resolved";       // server -> client (watch_url, port, video, audio, reason)
        }
    }
}
