using System;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // Two separate questions: smoketest asks whether this server is healthy, selftest whether the code is correct.
    internal static class KmhSmokeTest
    {
        // Both halves are pure, because a working sweeper clears the event before the uptime would qualify.
        internal static readonly TimeSpan SweepCycle = TimeSpan.FromMinutes(1);

        // Under two cycles of uptime the sweep has not run yet, so a pending event is not evidence of a fault.
        internal static bool StuckEventsAnswerable(TimeSpan uptime) => uptime >= SweepCycle + SweepCycle;

        // Between ticks an expired event is legitimately still present, so only count past a whole extra cycle.
        internal static int CountOverdue(IEnumerable<long> endsUtcTicks, long nowTicks)
        {
            if (endsUtcTicks == null) return 0;
            long cutoff = nowTicks - (SweepCycle.Ticks * 2);
            int n = 0;
            foreach (long ends in endsUtcTicks) if (ends <= cutoff) n++;
            return n;
        }

        // Every client->server request kind that must have a live server handler, or that feature is silently dead.
        private static readonly (string Kind, string Feature)[] RequiredHandlers =
        {
            (KmhProtocol.Kind.PlayerStatsRequest,      "Standings"),
            (KmhProtocol.Kind.ColonyReport,            "Colony report intake"),
            (KmhProtocol.Kind.ColonistRequest,         "Colonist profile"),
            (KmhProtocol.Kind.ColonistRosterRequest,   "Colonist roster"),
            (KmhProtocol.Kind.SeasonArchiveRequest,    "Season archive"),
            (KmhProtocol.Kind.TreasuryRequest,         "Treasury"),
            (KmhProtocol.Kind.MarketplaceRequest,      "Marketplace"),
            (KmhProtocol.Kind.QuestRequest,            "Quest board"),
            (KmhProtocol.Kind.GuildRequest,            "Guilds"),
            (KmhProtocol.Kind.GuildLeaderboardRequest, "Guild leaderboard"),
            (KmhProtocol.Kind.LeaderboardsRequest,     "Leaderboards"),
            (KmhProtocol.Kind.AuctionRequest,          "Auctions"),
            (KmhProtocol.Kind.WantRequest,             "Want board"),
            (KmhProtocol.Kind.MailRequest,             "Player mail"),
            (KmhProtocol.Kind.ChatRequest,             "Colony Nexus chat"),
            (KmhProtocol.Kind.WorldRequest,            "World engine"),
            (KmhProtocol.Kind.WorldContribute,         "Global quest contribute"),
            (KmhProtocol.Kind.WorldDeliver,            "Global quest deliver"),
            (KmhProtocol.Kind.SiteRequest,             "Sites"),
            (KmhProtocol.Kind.RoadworksRequest,        "Roadworks"),
            (KmhProtocol.Kind.ReputationRequest,       "Reputation"),
            (KmhProtocol.Kind.LinkedAccountsRequest,   "Linked accounts"),
            (KmhProtocol.Kind.ItemLabels,              "Item catalog intake"),
            (KmhProtocol.Kind.EnforcementSnapshotRequest, "Config enforcement"),
        };

        // Pass/fail tally shared by both commands, so the two surfaces can't drift in how they report.
        private sealed class Checks
        {
            private readonly Action<string> _reply;
            private int _pass, _fail;

            public Checks(Action<string> reply) { _reply = reply; }

            public void One(string name, Func<(bool ok, string detail)> probe)
            {
                try
                {
                    (bool ok, string detail) = probe();
                    if (ok) { _pass++; _reply($"  PASS  {name}{Suffix(detail)}"); }
                    else    { _fail++; _reply($"  FAIL  {name}{Suffix(detail)}"); }
                }
                catch (Exception ex) { _fail++; _reply($"  FAIL  {name} - threw: {ex.Message}"); }
            }

            public void Suite(IEnumerable<(string name, bool ok, string detail)> results)
            {
                try { foreach ((string name, bool ok, string detail) in results) One(name, () => (ok, detail)); }
                catch (Exception ex) { _fail++; _reply($"  FAIL  suite threw before reporting - {ex.Message}"); }
            }

            public void Report(string noun)
                => _reply(_fail == 0
                    ? $"=== RESULT: PASS ({_pass}/{_pass + _fail} {noun}) ==="
                    : $"=== RESULT: FAIL ({_fail} of {_pass + _fail} {noun} failed - see above) ===");

            private static string Suffix(string detail) => string.IsNullOrEmpty(detail) ? "" : $" - {detail}";
        }

        // Must stay non-mutating: it runs against a live server.
        public static void Run(Action<string> reply)
        {
            Checks c = new Checks(reply);
            reply("=== Colony Nexus smoke test - live server health (non-mutating) ===");

            c.One("Protocol version", () =>
                (!string.IsNullOrEmpty(KmhProtocol.BuildVersion) && KmhProtocol.CurrentVersion > 0,
                 $"wire v{KmhProtocol.CurrentVersion}, build {KmhProtocol.BuildVersion}"));

            c.One("Storage read/write", () =>
                (Persistence.JsonFileStore.SelfTest(out string d), d));

            c.One("Data integrity", () =>
            {
                Persistence.KmhDataIntegrity.ScanResult r = Persistence.KmhDataIntegrity.Scan();
                return (!r.CriticalDamage, r.Summary);
            });

            foreach ((string kind, string feature) in RequiredHandlers)
                c.One($"Handler: {feature}", () =>
                    (KmhRouter.IsRegistered(kind), KmhRouter.IsRegistered(kind) ? kind : $"NO handler for {kind}"));

            c.One("Standings snapshot", () =>
            {
                var s = Features.PlayerStats.PlayerStatsStore.BuildSnapshot();
                return (s?.Entries != null, $"{s?.Entries?.Count ?? 0} player(s)");
            });
            c.One("Marketplace snapshot", () =>
            {
                var s = Features.Marketplace.MarketplaceStore.BuildSnapshot(null);
                return (s?.Listings != null, $"{s?.Listings?.Count ?? 0} listing(s)");
            });
            c.One("Quest snapshot", () =>
            {
                var s = Features.Quests.QuestStore.BuildSnapshot(null);
                return (s?.Quests != null, $"{s?.Quests?.Count ?? 0} quest(s)");
            });
            c.One("Auction snapshot", () =>
            {
                var s = Features.Auctions.AuctionStore.BuildSnapshot(null);
                return (s?.Auctions != null, $"{s?.Auctions?.Count ?? 0} auction(s)");
            });
            c.One("Want snapshot", () =>
            {
                var s = Features.WantBoard.WantStore.BuildSnapshot(null);
                return (s?.Wants != null, $"{s?.Wants?.Count ?? 0} want(s)");
            });
            c.One("Sites snapshot", () =>
            {
                var s = Features.Sites.SiteStore.BuildSnapshotFor("");
                return (s?.Sites != null, $"{s?.Sites?.Count ?? 0} site(s)");
            });
            c.One("Guilds", () => (true, $"{Features.Guilds.GuildStore.ListGuilds().Count} guild(s)"));
            c.One("World engine", () =>
                (true, $"{Features.World.WorldStore.ActiveEvents().Count} event(s), {Features.World.WorldStore.ActiveQuests().Count} global quest(s)"));

            // A zero-duration event that escapes the sweep stays live forever.
            c.One("No stuck world events", () =>
            {
                TimeSpan uptime = Main_.BootstrapUtc == DateTime.MinValue
                    ? TimeSpan.Zero : DateTime.UtcNow - Main_.BootstrapUtc;
                if (!StuckEventsAnswerable(uptime))
                    return (true, $"not asked - up {(int)uptime.TotalSeconds}s, under one sweep cycle");

                var ends = new List<long>();
                foreach (var e in Features.World.WorldStore.ActiveEvents()) if (e != null) ends.Add(e.EndsUtcTicks);
                int stuck = CountOverdue(ends, DateTime.UtcNow.Ticks);
                return (stuck == 0, stuck == 0 ? "none" : $"{stuck} event(s) overdue by more than a sweep cycle");
            });

            c.One("Economy invariant", () =>
            {
                long pool = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                return (pool >= 0, $"house pool {Util.SilverFmt.Format(pool)}");
            });

            // Must answer whether Discord is on, off or failed; Discord being down must not break the server.
            c.One("Discord bridge safe", () =>
                (true, Features.Discord.DiscordBridge.DescribeStatus()));

            // An acceptance server keeps a real registration; in a self-test run none of it may reach that Global.
            c.One("Global isolation", () =>
            {
                if (!Features.Global.KmhGlobalSafety.SelfTestActive) return (true, "not a self-test run - Global is reached normally");
                if (Features.Global.KmhGlobalSafety.ProductionOptIn) return (true, $"self-test run with {Features.Global.KmhGlobalSafety.OptInEnvVar}=1 - a real Global is allowed");
                long escaped = Features.Global.KmhGlobalIsolationMonitor.Escaped;
                bool watching = Features.Global.KmhGlobalIsolationMonitor.Running;
                return (watching && escaped == 0,
                        $"self-test run: {Features.Global.KmhGlobalSafety.HeldBack} Global request(s) held back before sending, "
                        + $"{escaped} left the process{(escaped > 0 ? $" ({Features.Global.KmhGlobalIsolationMonitor.EscapedHosts})" : "")}"
                        + (watching ? "" : " - the request monitor is NOT running"));
            });

            // A failure here means a future RWT rename would strand owners on the "no server found" screen.
            c.One("RWT server discovery", () =>
            {
                bool ok = KmhRwtDiscoverySelfTest.Run(reply, out string detail);
                return (ok, detail);
            });

            c.Report("checks");
            reply("The build's contract/regression suite runs in the offline self-test runner (tools/server-selftest.ps1), never on a live server.");
        }

        // Suites seed and clear real stores, so on a live server this replaces players' state; offline runner only.
        public static void RunRegression(Action<string> reply)
        {
            if (!Features.Global.KmhGlobalSafety.SelfTestActive)
            {
                reply("kmh selftest only runs in the offline self-test runner (tools/server-selftest.ps1): it seeds and "
                    + "clears real stores, which on this server would overwrite players' guilds, federations and "
                    + "treasury operations. Use 'kmh smoketest' for a live health check.");
                return;
            }
            Checks c = new Checks(reply);
            reply("=== Colony Nexus self-test - build contract suite (synthetic data, non-mutating) ===");

            c.Suite(KmhMigrationSelfTest.Run());
            c.Suite(Policy.KmhPolicySelfTest.Run());
            c.Suite(Transactions.KmhTxSelfTest.Run());
            c.Suite(Transactions.KmhSettlementSelfTest.Run());
            c.Suite(Transactions.KmhTxRecoverySelfTest.Run());
            c.Suite(Features.Delivery.KmhDeliverySelfTest.Run());
            c.Suite(Items.KmhItemServiceSelfTest.Run());
            c.Suite(Features.Guilds.GuildPermissionsSelfTest.Run());
            c.Suite(Features.Guilds.GuildIntegritySelfTest.Run());
            c.Suite(KmhMaintenanceSelfTest.Run());
            c.Suite(Features.Guilds.Contributions.KmhContributionSelfTest.Run());
            c.Suite(Results.KmhResultSelfTest.Run());
            c.Suite(Security.KmhSeenGuardSelfTest.Run());
            c.Suite(KmhTransportGuardsSelfTest.Run());
            c.Suite(KmhTransportAuthSelfTest.Run());
            c.Suite(KmhSchedulerLifecycleSelfTest.Run());
            c.Suite(KmhValueOpsBarrierSelfTest.Run());
            c.Suite(Features.Guilds.KmhGuildIdentitySelfTest.Run());
            c.Suite(Features.Quests.KmhQuestRestartSelfTest.Run());
            c.Suite(Features.Sites.KmhSiteRestartSelfTest.Run());
            c.Suite(Features.Guilds.KmhGuildRestartSelfTest.Run());
            c.Suite(Features.Guilds.KmhGuildVaultItemSelfTest.Run());
            c.Suite(Features.Notifications.KmhPlayerNoticeSelfTest.Run());
            c.Suite(Persistence.KmhLedgerDurabilitySelfTest.Run());
            c.Suite(Features.Treasury.KmhHouseFeeRestartSelfTest.Run());
            c.Suite(KmhSessionOwnershipSelfTest.Run());
            c.Suite(Features.Transport.KmhTransportQueueSelfTest.Run());
            c.Suite(KmhEnforcementAdmissionSelfTest.Run());
            c.Suite(KmhProtocolMatrixSelfTest.Run());
            c.Suite(KmhV131StabilizationSelfTest.Run());
            c.Suite(KmhModProfileSelfTest.Run());
            c.Suite(KmhConfigPublishSelfTest.Run());
            c.Suite(KmhConfigPublishHandlerSelfTest.Run());
            c.Suite(KmhLeaderboardCostSelfTest.Run());
            c.Suite(KmhSelfTestHarnessSelfTest.Run());
            c.Suite(KmhGlobalSigningSelfTest.Run());
            c.Suite(SubProtocol.KmhFrameCodecSelfTest.Run());
            c.Suite(SubProtocol.KmhWireFuzzSelfTest.Run());
            c.Suite(SubProtocol.KmhBroadcastBatchSelfTest.Run());
            c.Suite(KmhOperatorConsoleSelfTest.Run());
            c.Suite(KmhEconomyDisableSelfTest.Run());
            c.Suite(KmhDurabilitySelfTest.Run());
            c.Suite(KmhPayloadTakeSelfTest.Run());
            c.Suite(Features.Global.Treasury.KmhConnectedTreasurySelfTest.Run());
            c.Suite(Features.Global.Guilds.KmhConnectedGuildSelfTest.Run());
            c.Suite(Features.Global.Federation.KmhFederationSelfTest.Run());
            c.Suite(KmhReleaseEdgesSelfTest.Run());
            // Empty unless KMH_FED_LIVE_URL names a development Global; see the suite for the steps.
            c.Suite(Features.Global.Federation.KmhFederationLiveSelfTest.Run());
            c.Suite(KmhLinkedIdentitySelfTest.Run());
            c.Suite(KmhBackupSelfTest.Run());
            c.Suite(KmhResetLifecycleSelfTest.Run());
            c.Suite(KmhSeasonResetAdversarialSelfTest.Run());
            c.Suite(KmhBackgroundJobSelfTest.Run());
            c.Suite(KmhJobDuplicateRunSelfTest.Run());
            c.Suite(KmhWriteFailureRollbackSelfTest.Run());
            c.Suite(KmhLifecycleSelfTest.Run());
            c.Suite(KmhWipeSelfTest.Run());
            c.Suite(KmhSecurityAuditSelfTest.Run());
            c.Suite(KmhSecurityMinorSelfTest.Run());
            c.Suite(KmhStartupSelfTest.Run());
            c.Suite(KmhLoggingSelfTest.Run());
            c.Suite(KmhClientTrustSelfTest.Run());
            c.Suite(KmhRwtLoginGuardSelfTest.Run());
            c.Suite(KmhGameVersionSelfTest.Run());
            c.Suite(KmhOpGuardSelfTest.Run());
            c.Suite(KmhRaceSelfTest.Run());
            c.Suite(SubProtocol.KmhFragmentSelfTest.Run());
            c.Suite(Features.Treasury.KmhTreasuryReconcileSelfTest.Run());
            c.Suite(Features.Recovery.KmhRecoverySelfTest.Run());
            c.Suite(KmhEnvelopeSelfTest.Run());
            c.Suite(Extensibility.KmhExtensionCompatSelfTest.Run());
            c.Suite(Extensibility.KmhHooksSelfTest.Run());
            c.Suite(KmhWorldConfigSelfTest.Run());
            c.Suite(AdminCommands.KmhConfigInspectSelfTest.Run());
            c.Suite(AdminCommands.KmhHelpRenderSelfTest.Run());
            c.Suite(KmhMailSelfTest.Run());
            c.Suite(KmhMailPersistenceSelfTest.Run());
            c.Suite(KmhMailItemStateSelfTest.Run());
            c.Suite(KmhDiscordRelaySelfTest.Run());
            c.Suite(KmhDiscordCoordinatorSelfTest.Run());
            c.Suite(KmhGlobalConfigCheckSelfTest.Run());
            c.Suite(KmhPlatformSelfTest.Run());
            c.Suite(Features.Leaderboards.KmhLeaderboardSelfTest.Run());
            c.Suite(KmhBoundedHttpSelfTest.Run());
            c.Suite(KmhPersistenceSnapshotSelfTest.Run());
            c.Suite(KmhPersistenceCoalesceSelfTest.Run());
            c.Suite(Features.Global.KmhGlobalRevokeSelfTest.Run());
            c.Suite(Features.Global.KmhGlobalLinkSelfTest.Run());
            c.Suite(Features.Global.KmhIdentityChangeSelfTest.Run());
            c.Suite(Features.Global.KmhGlobalIsolationSelfTest.Run());
            c.Suite(Features.Reports.KmhReportSelfTest.Run());
            c.Suite(KmhMediaSelfTest.Run());
            c.Suite(KmhCommsStartupSelfTest.Run());
            c.Suite(KmhRedactSelfTest.Run());
            c.Suite(KmhDependencyPreflightSelfTest.Run());
            c.Suite(KmhSupportBundleSelfTest.Run());
            c.Suite(KmhCommandHelpSelfTest.Run());
            c.Suite(KmhStaffIdentitySelfTest.Run());
            c.Suite(KmhSiteClassifierSelfTest.Run());
            c.Suite(KmhChatSelfTest.Run());
            c.Suite(KmhModerationSelfTest.Run());
            c.Suite(KmhConfigEditorSelfTest.Run());
            c.Suite(KmhChatPersistenceSelfTest.Run());
            c.Suite(KmhFeatureGateSelfTest.Run());
            c.Suite(KmhFlushCoverageSelfTest.Run());
            c.Suite(KmhDataFileCoverageSelfTest.Run());
            c.Suite(KmhSnapshotCoverageSelfTest.Run());
            c.Suite(KmhConfigCoverageSelfTest.Run());
            c.Suite(KmhConfigDriftSelfTest.Run());
            c.Suite(KmhStatusCoverageSelfTest.Run());
            c.Suite(KmhSeasonCoverageSelfTest.Run());
            c.Suite(KmhWealthCoverageSelfTest.Run());
            c.Suite(KmhWorldStateCoverageSelfTest.Run());
            c.Suite(Diagnostics.KmhRwtPreflightSelfTest.Run());
            c.Suite(Features.Sites.KmhSiteCostSelfTest.Run());
            c.Suite(Features.Sites.KmhSiteWorkerSelfTest.Run());
            c.Suite(Features.Sites.KmhSiteArchetypeSelfTest.Run());
            c.Suite(Features.Sites.KmhFrontierWorksSelfTest.Run());
            c.Suite(Features.Sites.KmhSiteRoadMatrixSelfTest.Run());
            c.Suite(Features.Roadworks.KmhRoadworksSelfTest.Run());
            c.Suite(Features.Roadworks.KmhRoadworksProgressSelfTest.Run());
            c.Suite(Features.Economy.KmhGuildHallAccessSelfTest.Run());
            c.Suite(KmhSnapshotCopySelfTest.Run());
            c.Suite(Features.Quests.KmhQuestClaimSelfTest.Run());
            c.Suite(Features.Auctions.KmhAuctionWantSelfTest.Run());
            c.Suite(Features.Frontier.KmhFrontierSelfTest.Run());
            c.Suite(Features.Frontier.KmhFrontierStateSelfTest.Run());
            c.Suite(Features.Marketplace.KmhSnapshotShareSelfTest.Run());
            c.Suite(Features.Marketplace.KmhMarketplaceRestartSelfTest.Run());
            c.Suite(Features.Auctions.KmhAuctionRestartSelfTest.Run());
            c.Suite(Features.WantBoard.KmhWantRestartSelfTest.Run());
            c.Suite(Features.Mail.KmhMailRestartSelfTest.Run());
            c.Suite(Items.KmhDeliverablePlanSelfTest.Run());
            c.Suite(KmhStuckEventCheckSelfTest.Run());
            c.Suite(Features.Discord.KmhDiscordTextSelfTest.Run());
            c.Suite(Transactions.KmhValueMoveAdoptionSelfTest.Run());

            c.Report("contract checks");
            reply("This checks the BUILD, not this server. For live server health, run: kmh smoketest");
        }
    }
}
