namespace KMHServerAddon.SubProtocol
{
    // KMH sub-protocol constants - MUST stay byte-identical to the patch mod's KMHPatch.SubProtocol.KmhProtocol on
    // the client side. Drift here = silent handshake failure or feature data going unparsed
    //
    // The ​ (zero-width space) prefix on the usernames is what makes them collision-proof against real player names
    // - RWT lets players pick anything, but no legitimate username can start with a non-printable Unicode control
    // character
    internal static class KmhProtocol
    {
        // Bump when the wire format changes incompatibly. Handshake refuses to enable KMH features for sessions
        // where client and server disagree
        public const int CurrentVersion = 1;

        public const string SystemUsername = "​[KMH-SYS]"; // server -> client
        public const string ClientUsername = "​[KMH-CLI]"; // client -> server

        // Well-known envelope kinds. Add more as features land - each new kind below also needs its handler
        // registered in KmhBootstrap (or wherever the feature initializes itself)
        public static class Kind
        {
            // Connection lifecycle
            public const string Hello        = "kmh.hello";
            public const string HelloAck     = "kmh.hello.ack";

            // Diagnostic
            public const string Ping         = "kmh.ping";
            public const string Pong         = "kmh.pong";

            // Transient server -> client toast. Payload { level, text } where level is positive/negative/neutral.
            // Used for action feedback that doesn't warrant a full snapshot (e.g. "guild is invite-only")
            public const string Notice       = "kmh.notice";                  // server -> client

            // Player stats / leaderboard
            public const string PlayerStatsRequest      = "kmh.player_stats.request";    // client -> server
            public const string PlayerStatsSnapshot     = "kmh.player_stats.snapshot";   // server -> client

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

            // Cross-guild leaderboard - separate from GuildSnapshot which is caller-scoped (caller's own guild).
            // This carries every guild on the server in a leaderboard-friendly shape
            public const string GuildLeaderboardRequest = "kmh.guild_leaderboard.request"; // client -> server
            public const string GuildLeaderboardSnapshot = "kmh.guild_leaderboard.snapshot"; // server -> client

            // Linked accounts - in-game username -> Discord display name map. Server pushes after handshake + on
            // every link/unlink so the patch mod doesn't have to poll
            public const string LinkedAccountsRequest   = "kmh.linked_accounts.request";  // client -> server
            public const string LinkedAccountsSnapshot  = "kmh.linked_accounts.snapshot"; // server -> client

            // Player reputation roster (username -> score + tier) for badges.
            public const string ReputationRequest      = "kmh.reputation.request";      // client -> server
            public const string ReputationSnapshot     = "kmh.reputation.snapshot";     // server -> client

            // KMH custom sites - player-built production nodes with workers.
            public const string SiteRequest            = "kmh.site.request";            // client -> server
            public const string SiteSnapshot           = "kmh.site.snapshot";           // server -> client
            public const string SiteBuild              = "kmh.site.build";              // client -> server
            public const string SiteJoin               = "kmh.site.join";               // client -> server (worker join)
            public const string SiteLeave              = "kmh.site.leave";              // client -> server (worker leave)
            public const string SiteSetDestination     = "kmh.site.set_destination";    // client -> server
            public const string SiteCancel             = "kmh.site.cancel";             // client -> server (owner removes)

            // Item label catalog. Patch mod sends a defName -> label map at handshake completion so the server can
            // resolve friendly names
            // for Discord-side market commands (`!kmh-sell plasteel ...`)
            // and show readable item labels in market browse output. Server accumulates the union across all
            // reporting clients - players with different loaded mods contribute their slice
            public const string ItemLabels              = "kmh.item_labels";              // client -> server

            // Config enforcement. The server pushes the enforcement snapshot (on/off + admin-bypass + safe-mods
            // allowlist) on handshake and on change; the patch locks the Mod Options screen for non-admins
            // accordingly. The full config profile (hard enforcement) rides on the chunked profile.* kinds, and
            // restore clears it
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
