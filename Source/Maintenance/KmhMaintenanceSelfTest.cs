using System.Collections.Generic;
using K = KMHServerAddon.SubProtocol.KmhProtocol.Kind;

namespace KMHServerAddon.Maintenance
{
    // A wrong classification here either freezes the economy for nothing or lets a deposit slip through a restore.
    internal static class KmhMaintenanceSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();

            // Value-moving requests are blockable; reads and infrastructure are not.
            bool mutations =
                KmhMaintenanceGate.WouldBlock(K.TreasuryDepositSilver) &&
                KmhMaintenanceGate.WouldBlock(K.TreasuryWithdrawItem)  &&
                KmhMaintenanceGate.WouldBlock(K.MarketplaceBuy)        &&
                KmhMaintenanceGate.WouldBlock(K.AuctionBid)            &&
                KmhMaintenanceGate.WouldBlock(K.GuildDonate);
            r.Add(("Maintenance: mutations are blockable", mutations, "deposit/withdraw/buy/bid/donate"));

            bool readsAllowed =
                !KmhMaintenanceGate.WouldBlock(K.TreasuryRequest)    &&
                !KmhMaintenanceGate.WouldBlock(K.MarketplaceRequest) &&
                !KmhMaintenanceGate.WouldBlock(K.GuildRequest);
            r.Add(("Maintenance: reads are never blocked", readsAllowed, ".request kinds pass"));

            bool infraAllowed =
                !KmhMaintenanceGate.WouldBlock(K.HelloAck) &&
                !KmhMaintenanceGate.WouldBlock(K.Ping)     &&
                !KmhMaintenanceGate.WouldBlock(K.ItemLabels);   // catalog push latches client-side; refusing it loses pricing
            r.Add(("Maintenance: infra/handshake pass", infraAllowed, "hello/ping/catalog pass"));

            // These carry no feature key, so classifying by feature let every one of them through a freeze.
            bool siteRoadBlocked =
                KmhMaintenanceGate.WouldBlock(K.SiteBuild)          &&
                KmhMaintenanceGate.WouldBlock(K.SiteBuildingAdd)    &&
                KmhMaintenanceGate.WouldBlock(K.SiteBuildingRemove) &&
                KmhMaintenanceGate.WouldBlock(K.SiteRepair)         &&
                KmhMaintenanceGate.WouldBlock(K.SiteClaim)          &&
                KmhMaintenanceGate.WouldBlock(K.SiteCancel)         &&
                KmhMaintenanceGate.WouldBlock(K.SiteSetup)          &&
                KmhMaintenanceGate.WouldBlock(K.SiteJoin)           &&
                KmhMaintenanceGate.WouldBlock(K.SiteLeave)          &&
                KmhMaintenanceGate.WouldBlock(K.SiteSetDestination) &&
                KmhMaintenanceGate.WouldBlock(K.SiteStorageCollect) &&
                KmhMaintenanceGate.WouldBlock(K.RoadworksStart)     &&
                KmhMaintenanceGate.WouldBlock(K.RoadworksCancel);
            r.Add(("Maintenance: Site and Roadworks mutations are blockable", siteRoadBlocked,
                   siteRoadBlocked ? "13 site/roadworks mutations" : "A SITE OR ROADWORKS MUTATION RUNS DURING A FREEZE"));

            // A kind nobody classified must refuse rather than pass, or the next feature repeats the same gap.
            bool unknownBlocked = KmhMaintenanceGate.WouldBlock("kmh.some.future.feature")
                               && !KmhMaintenanceGate.WouldBlock("kmh.some.future.feature.request");
            r.Add(("Maintenance: an unclassified kind blocks by default", unknownBlocked,
                   unknownBlocked ? "fail-closed" : "an unknown mutation would pass"));

            // Every ingress, not only the router: Discord and the scheduler reach the same stores directly.
            KmhMaintenanceGate.Release();
            bool openWhenIdle = KmhAdmission.AllowsValueMutation(KmhIngress.Discord, out _)
                             && KmhAdmission.AllowsValueMutation(KmhIngress.Scheduler, out _);
            KmhMaintenanceGate.Enter(KmhMaintenanceReason.OwnerMaintenance);
            bool discordStops   = !KmhAdmission.AllowsValueMutation(KmhIngress.Discord, out _);
            bool schedulerStops = !KmhAdmission.AllowsValueMutation(KmhIngress.Scheduler, out _);
            bool clientStops    = !KmhAdmission.AllowsValueMutation(KmhIngress.ClientApi, out _);
            // Recovery and admin repair are how a freeze ends, so gating them would deadlock against the freeze.
            bool recoveryRuns   = KmhAdmission.AllowsValueMutation(KmhIngress.Recovery, out _)
                               && KmhAdmission.AllowsValueMutation(KmhIngress.Admin, out _);
            KmhMaintenanceGate.Release();
            bool ingress = openWhenIdle && discordStops && schedulerStops && clientStops && recoveryRuns;
            r.Add(("Maintenance: every player ingress stops, recovery does not", ingress,
                   ingress ? "discord/scheduler/client refused, recovery+admin allowed"
                           : $"idle={openWhenIdle}, discord={discordStops}, scheduler={schedulerStops}, client={clientStops}, recovery={recoveryRuns}"));

            // Feature enablement is a separate question from admission, and player ingress owes both answers.
            bool featureOffRefused = !KmhAdmission.AllowsPlayerFeature(KmhIngress.Discord, "definitely_not_a_feature", out _)
                                   || Features.FeaturesConfig.Current.IsEnabled("definitely_not_a_feature");
            r.Add(("Maintenance: a disabled feature refuses player ingress too", featureOffRefused,
                   "feature policy and mutation admission stay separate"));

            // ShouldBlock is inert until the gate is entered.
            KmhMaintenanceGate.Release();   // ensure clean start (idempotent)
            bool inertWhenOff = !KmhMaintenanceGate.ShouldBlock(K.MarketplaceBuy) && !KmhMaintenanceGate.IsActive;

            KmhMaintenanceGate.Enter(KmhMaintenanceReason.Migration);
            bool blocksWhenOn = KmhMaintenanceGate.ShouldBlock(K.MarketplaceBuy) && !KmhMaintenanceGate.ShouldBlock(K.MarketplaceRequest);
            r.Add(("Maintenance: gate off inert, on blocks", inertWhenOff && blocksWhenOn, "toggles correctly"));

            // Re-entrant: a nested Enter needs a matching Release before the gate lifts.
            KmhMaintenanceGate.Enter(KmhMaintenanceReason.Restore);
            KmhMaintenanceGate.Release();                       // inner
            bool stillActive = KmhMaintenanceGate.IsActive;     // outer still holds
            KmhMaintenanceGate.Release();                       // outer
            bool nowReleased = !KmhMaintenanceGate.IsActive;
            r.Add(("Maintenance: re-entrant depth", stillActive && nowReleased, "nested release keeps gate until depth 0"));

            // Degradation is a condition, not a scope, so an unrelated Release must not cancel it.
            bool freezeOn = MaintenanceConfig.Current.FreezeEconomyOnPersistenceFailure;
            KmhMaintenanceGate.SetPersistenceDegraded(true);
            bool frozen = !freezeOn || (KmhMaintenanceGate.IsActive
                                        && KmhMaintenanceGate.Reason == KmhMaintenanceReason.PersistenceFailure
                                        && KmhMaintenanceGate.ShouldBlock(K.TreasuryDepositSilver));
            r.Add(("Maintenance: unwritable data blocks deposits", frozen,
                   freezeOn ? "" : "FreezeEconomyOnPersistenceFailure=false"));

            // Players must still be able to SEE their state while the economy is frozen.
            r.Add(("Maintenance: degraded still allows reads",
                   !KmhMaintenanceGate.ShouldBlock(K.TreasuryRequest), ""));

            // A stray Release must not clear it - only writability coming back does.
            KmhMaintenanceGate.Release();
            r.Add(("Maintenance: Release cannot clear degradation",
                   !freezeOn || KmhMaintenanceGate.IsActive, ""));

            KmhMaintenanceGate.SetPersistenceDegraded(false);
            r.Add(("Maintenance: clears when saves recover",
                   !KmhMaintenanceGate.IsActive && KmhMaintenanceGate.Reason == KmhMaintenanceReason.None, ""));

            // An explicit maintenance scope still names itself while degraded, so the log says which one it is.
            KmhMaintenanceGate.SetPersistenceDegraded(true);
            KmhMaintenanceGate.Enter(KmhMaintenanceReason.Migration);
            r.Add(("Maintenance: active scope names itself over degradation",
                   KmhMaintenanceGate.Reason == KmhMaintenanceReason.Migration, KmhMaintenanceGate.Reason.ToString()));
            KmhMaintenanceGate.Release();
            KmhMaintenanceGate.SetPersistenceDegraded(false);
            r.Add(("Maintenance: gate left clean", !KmhMaintenanceGate.IsActive, ""));

            // Quieting the terminal must never quiet the file, so this only ever decides what the console is shown.
            var all   = new MaintenanceConfig { ConsoleLogLevel = "all"   };
            var warn  = new MaintenanceConfig { ConsoleLogLevel = "warn"  };
            var err   = new MaintenanceConfig { ConsoleLogLevel = "error" };
            var quiet = new MaintenanceConfig { ConsoleLogLevel = "quiet" };
            var junk  = new MaintenanceConfig { ConsoleLogLevel = "  NoNsEnSe " };
            r.Add(("Maintenance: ConsoleLogLevel filters the terminal by severity, and an unknown value shows everything",
                   all.ConsoleAllows(KmhConsoleLevel.Info) && all.ConsoleAllows(KmhConsoleLevel.Error)
                   && !warn.ConsoleAllows(KmhConsoleLevel.Info) && warn.ConsoleAllows(KmhConsoleLevel.Warn)
                   && !err.ConsoleAllows(KmhConsoleLevel.Warn) && err.ConsoleAllows(KmhConsoleLevel.Error)
                   && !quiet.ConsoleAllows(KmhConsoleLevel.Error)
                   && junk.ConsoleAllows(KmhConsoleLevel.Info),
                   "all/warn/error/quiet and a bad value"));

            return r;
        }
    }
}
