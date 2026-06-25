namespace KMHServerAddon.SubProtocol
{
    // Keep byte-identical with client KmhProtocol; zero-width username prefix prevents player-name collisions.
    internal static class KmhProtocol
    {
        // Wire compatibility version; client/server mismatch blocks KMH feature activation.
        public const int CurrentVersion = 1;

        // Human-readable release version, carried in kmh.hello purely so each side can DETECT a version gap and
        // nudge the player. It never gates the connection (that's CurrentVersion's job) and stays additive: a
        // pre-1.1.0 server omits it, so an empty value received by a client reliably means "older server".
        public const string BuildVersion = "1.1.0";

        public const string SystemUsername = "​[KMH-SYS]"; // server -> client
        public const string ClientUsername = "​[KMH-CLI]"; // client -> server

        // Known packet kinds. Add new ones here only when their handlers are registered.
        public static class Kind
        {
            // Connection lifecycle
            public const string Hello        = "kmh.hello";
            public const string HelloAck     = "kmh.hello.ack";

            // Diagnostic
            public const string Ping         = "kmh.ping";
            public const string Pong         = "kmh.pong";

            // Transient server-to-client toast: { level, text } for lightweight action feedback.
            public const string Notice       = "kmh.notice";                  // server -> client

            // Batch of notices that piled up while the player was offline; delivered once on login as letters.
            public const string NotifyQueued = "kmh.notify.queued";           // server -> client

            // Player stats / leaderboard
            public const string PlayerStatsRequest      = "kmh.player_stats.request";    // client -> server
            public const string PlayerStatsSnapshot     = "kmh.player_stats.snapshot";   // server -> client
            // Client uploads its colony summary + top colonist (display-only leaderboard data).
            public const string ColonyReport            = "kmh.colony.report";           // client -> server
            // Full top-colonist profile fetched on demand when a player card opens.
            public const string ColonistRequest         = "kmh.colonist.request";        // client -> server
            public const string ColonistProfile         = "kmh.colonist.profile";        // server -> client
            // Flattened roster of every colony's reported colonists, for the per-skill Colonist Records boards.
            public const string ColonistRosterRequest   = "kmh.records.colonists.request"; // client -> server
            public const string ColonistRoster          = "kmh.records.colonists";         // server -> client
            // Season archive: current/past season leaders + all-time server records.
            public const string SeasonArchiveRequest    = "kmh.archive.season.request";    // client -> server
            public const string SeasonArchive           = "kmh.archive.season";            // server -> client

            // Treasury - caller's vault (guild or personal). Server resolves which one from authenticated identity
            public const string TreasuryRequest         = "kmh.treasury.request";        // client -> server
            public const string TreasurySnapshot        = "kmh.treasury.snapshot";       // server -> client
            public const string TreasuryDepositSilver   = "kmh.treasury.deposit_silver"; // client -> server
            public const string TreasuryWithdrawSilver  = "kmh.treasury.withdraw_silver";// client -> server
            public const string TreasuryDepositItem     = "kmh.treasury.deposit_item";   // client -> server
            public const string TreasuryWithdrawItem    = "kmh.treasury.withdraw_item";  // client -> server
            public const string TreasuryGrant           = "kmh.treasury.grant";          // server -> client (materialize a confirmed withdrawal into the colony)

            // Marketplace - open-listings list + buy / cancel / post mutations.
            public const string MarketplaceRequest      = "kmh.marketplace.request";     // client -> server
            public const string MarketplaceSnapshot     = "kmh.marketplace.snapshot";    // server -> client
            public const string MarketplaceBuy          = "kmh.marketplace.buy";         // client -> server
            public const string MarketplaceCancel       = "kmh.marketplace.cancel";      // client -> server
            public const string MarketplacePost         = "kmh.marketplace.post";        // client -> server

            // Quest board - global posted quests.
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

            // Guild - caller-scoped (server resolves caller's guild from membership) + admin-gated mutations
            public const string GuildRequest            = "kmh.guild.request";            // client -> server
            public const string GuildSnapshot           = "kmh.guild.snapshot";           // server -> client
            public const string GuildPromote            = "kmh.guild.promote";            // client -> server
            public const string GuildDemote             = "kmh.guild.demote";             // client -> server
            public const string GuildKick               = "kmh.guild.kick";               // client -> server
            public const string GuildBuyPerk            = "kmh.guild.buy_perk";           // client -> server
            public const string GuildSetMotd            = "kmh.guild.set_motd";           // client -> server
            public const string GuildProposeAlliance    = "kmh.guild.propose_alliance";   // client -> server
            public const string GuildAcceptAlliance     = "kmh.guild.accept_alliance";    // client -> server
            public const string GuildBreakAlliance      = "kmh.guild.break_alliance";     // client -> server
            public const string GuildDeclareHostile     = "kmh.guild.declare_hostile";    // client -> server
            public const string GuildClearHostile       = "kmh.guild.clear_hostile";      // client -> server
            public const string GuildSaveSettings       = "kmh.guild.save_settings";      // client -> server
            public const string GuildInvite             = "kmh.guild.invite";             // client -> server (admin/mod invites a player)
            public const string GuildSetOpenJoin        = "kmh.guild.set_open_join";      // client -> server (admin toggles open join)
            public const string GuildJoin               = "kmh.guild.join";               // client -> server (join an open or invited guild)

            // Cross-guild leaderboard payload containing every guild in leaderboard form.
            public const string GuildLeaderboardRequest = "kmh.guild_leaderboard.request"; // client -> server
            public const string GuildLeaderboardSnapshot = "kmh.guild_leaderboard.snapshot"; // server -> client

            // Linked account map pushed after handshake and on every Discord link/unlink.
            public const string LinkedAccountsRequest   = "kmh.linked_accounts.request";  // client -> server
            public const string LinkedAccountsSnapshot  = "kmh.linked_accounts.snapshot"; // server -> client

            // Player reputation roster (username -> score + tier) for badges.
            public const string ReputationRequest      = "kmh.reputation.request";      // client -> server
            public const string ReputationSnapshot     = "kmh.reputation.snapshot";     // server -> client

            // Custom Sites and World Engine payloads for production nodes, global events, and server-owned quests.
            public const string WorldRequest           = "kmh.world.request";           // client -> server
            public const string WorldSnapshot          = "kmh.world.snapshot";          // server -> client
            // Payload: { quest_id, total }; server stores the max tally per user.
            public const string WorldContribute        = "kmh.world.contribute";        // client -> server
            // Additive deliver credit; no item refund, only counts toward an active matching quest.
            public const string WorldDeliver           = "kmh.world.deliver";           // client -> server

            // Marketplace auctions - timed bidding on treasury items.
            public const string AuctionRequest         = "kmh.auction.request";         // client -> server
            public const string AuctionSnapshot        = "kmh.auction.snapshot";        // server -> client
            public const string AuctionPost            = "kmh.auction.post";            // client -> server
            public const string AuctionBid             = "kmh.auction.bid";             // client -> server (bid silver from treasury)
            public const string AuctionCancel          = "kmh.auction.cancel";          // client -> server (seller, pre-bid only)

            // Want-to-buy board - buyers escrow silver, sellers fulfill from treasury.
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

            // Clients send defName -> label maps after handshake; server merges them for Discord commands and market output.
            public const string ItemLabels              = "kmh.item_labels";              // client -> server
            // Clients also send defName -> BaseMarketValue (RimWorld's canonical prices) so the server can value-scale
            // quest rewards / pricing. Separate envelope so it never bloats the (already near-cap) labels push.
            public const string ItemValues              = "kmh.item_values";              // client -> server

            // Pushes enforcement state and config profiles so clients can lock Mod Options and restore when cleared.
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
        }
    }
}
