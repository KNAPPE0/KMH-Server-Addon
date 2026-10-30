using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Global;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhGlobalConfigCheckSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            List<(string, bool, string)> r = new List<(string, bool, string)>();
            PrivateHosts(r);
            Findings(r);
            SetupStatePersistence(r);
            SetupCompletion(r);
            RwtBrowserListing(r);
            return r;
        }

        // Without a PublicHost RWT's browser shows the owner's home IP; with one it lists and dials that name.
        private static void RwtBrowserListing(List<(string, bool, string)> r)
        {
            const string ip = "203.0.113.5", host = "play.example.org";
            r.Add(("rwt browser: the looked-up IP is listed as the PublicHost",
                   RwtBrowserEndpoint.Announced(ip, host) == host, RwtBrowserEndpoint.Announced(ip, host)));
            r.Add(("rwt browser: with no PublicHost RWT's own address stands",
                   RwtBrowserEndpoint.Announced(ip, "") == ip && RwtBrowserEndpoint.Announced(ip, null) == ip, ""));
            r.Add(("rwt browser: a private PublicHost is never listed",
                   RwtBrowserEndpoint.Announced(ip, "192.168.1.70") == ip && RwtBrowserEndpoint.Announced(ip, "localhost") == ip, ""));
            r.Add(("rwt browser: a failed lookup still lists the name",
                   RwtBrowserEndpoint.Announced("", host) == host && RwtBrowserEndpoint.Announced(null, host) == host, ""));

            System.Reflection.MethodInfo set = RwtBrowserEndpoint.Target();
#if RWT_RT
            r.Add(("rwt browser: this RWT build's listing endpoint is found", set != null, ""));
#endif
            if (set == null) return;

            // Through the real setter, patched as at boot, so the prefix's binding is what is tested.
            HarmonyLib.Harmony h = new HarmonyLib.Harmony("kmh.selftest.rwt-browser");
            GlobalConfig prev = GlobalConfig.ApplyForTest(new GlobalConfig { PublicHost = host });
            try
            {
                RwtBrowserEndpoint.Install(h);
                object pkt = System.Activator.CreateInstance(set.DeclaringType);
                set.Invoke(pkt, new object[] { ip });
                object listed = set.DeclaringType.GetProperty("Endpoint").GetValue(pkt);
                r.Add(("rwt browser: RWT's listing packet carries the PublicHost", (listed as string) == host, $"{listed}"));
            }
            finally
            {
                h.UnpatchAll(h.Id);
                GlobalConfig.ApplyForTest(prev);
            }
        }

        // Waiting on the owner's browser approval risks a loop that never stops, so these cover when it must NOT wait.
        private static void SetupCompletion(List<(string, bool, string)> r)
        {
            const long now = 1_800_000_000;

            GlobalSetupState pending = new GlobalSetupState
            {
                PendingLinkId = "link-1", PendingLinkExpiresUnix = now + 600,
            };
            pending.Current = SetupStage.OwnerLinkPending;
            r.Add(("global setup: a live pending link is waited on, so the owner only approves and walks away",
                   KmhGlobalSetupCompleter.ShouldWaitFor(pending, now), ""));

            GlobalSetupState expired = new GlobalSetupState
            {
                PendingLinkId = "link-1", PendingLinkExpiresUnix = now - 1,
            };
            expired.Current = SetupStage.OwnerLinkPending;
            r.Add(("global setup: a lapsed code stops the wait rather than polling a dead link for ever",
                   !KmhGlobalSetupCompleter.ShouldWaitFor(expired, now), ""));

            GlobalSetupState registered = new GlobalSetupState();
            registered.Current = SetupStage.Registered;
            r.Add(("global setup: an already registered server never waits",
                   !KmhGlobalSetupCompleter.ShouldWaitFor(registered, now), ""));

            GlobalSetupState deferred = new GlobalSetupState();
            deferred.Current = SetupStage.Deferred;
            r.Add(("global setup: an owner who chose to defer is not quietly registered anyway",
                   !KmhGlobalSetupCompleter.ShouldWaitFor(deferred, now), ""));

            GlobalSetupState fresh = new GlobalSetupState();
            r.Add(("global setup: a server that never started setup does not poll Global",
                   !KmhGlobalSetupCompleter.ShouldWaitFor(fresh, now), ""));

            // The grant is memory-only, so a restart past the link stage has nothing left to register with.
            GlobalSetupState verified = new GlobalSetupState();
            verified.Current = SetupStage.OwnerVerified;
            r.Add(("global setup: a stage past the link is left to the console, not retried without a grant",
                   !KmhGlobalSetupCompleter.ShouldWaitFor(verified, now), ""));

            GlobalSetupState noId = new GlobalSetupState { PendingLinkExpiresUnix = now + 600 };
            noId.Current = SetupStage.OwnerLinkPending;
            r.Add(("global setup: a pending stage with no link id is not polled",
                   !KmhGlobalSetupCompleter.ShouldWaitFor(noId, now), ""));

            r.Add(("global setup: nothing is waiting in an offline suite", !KmhGlobalSetupCompleter.Running, ""));

            // Far enough apart that a 15-minute code gets tens of tries, not hundreds.
            bool paced = KmhGlobalSetupCompleter.PollInterval >= System.TimeSpan.FromSeconds(10)
                      && KmhGlobalSetupCompleter.PollInterval <= System.TimeSpan.FromMinutes(1);
            r.Add(("global setup: the wait polls at a human pace, not a hot loop", paced,
                   $"{(int)KmhGlobalSetupCompleter.PollInterval.TotalSeconds}s"));

            OwnerDeepLink(r);
            ProductionGuard(r);
        }

        // BaseUrl defaults to the official directory, so a test client built from defaults must refuse, not list fakes.
        private static void ProductionGuard(List<(string, bool, string)> r)
        {
            bool? prior = KmhGlobalSafety.SelfTestOverride;
            try
            {
                KmhGlobalSafety.SelfTestOverride = true;

                r.Add(("global guard: the shipped default BaseUrl is the production directory",
                       KmhGlobalSafety.IsProduction(new GlobalConfig().BaseUrl), new GlobalConfig().BaseUrl));

                bool refused = !KmhGlobalSafety.MayPublishTo(GlobalConfigCheck.OfficialBaseUrl, out string why);
                r.Add(("global guard: a self-test run cannot publish to the production directory",
                       refused && why.Contains(KmhGlobalSafety.OptInEnvVar), refused ? why : "ALLOWED"));

                r.Add(("global guard: an isolated Global is still publishable from a test",
                       KmhGlobalSafety.MayPublishTo("http://127.0.0.1:5099", out _)
                       && KmhGlobalSafety.MayPublishTo("https://global.example.test", out _), ""));

                KmhGlobalSafety.SelfTestOverride = false;
                r.Add(("global guard: a real server publishes to production normally",
                       KmhGlobalSafety.MayPublishTo(GlobalConfigCheck.OfficialBaseUrl, out _), ""));
            }
            finally { KmhGlobalSafety.SelfTestOverride = prior; }
        }

        // Prose appended to a deep-link url gives a 404; the owner's console link is built like the player's one.
        private static void OwnerDeepLink(List<(string, bool, string)> r)
        {
            const string b = "https://global.knappe0.games/link-server";

            string plain = KmhGlobalRegistration.DeepLink(b, "1SC4-D4GX");
            bool ok = plain == b + "?code=1SC4-D4GX"
                   && System.Uri.TryCreate(plain, System.UriKind.Absolute, out System.Uri u) && u.Scheme == "https";
            r.Add(("owner link: the code rides as a query parameter, not appended prose", ok, plain));

            r.Add(("owner link: it contains no spaces or literal 'code:' text",
                   plain.IndexOf(' ') < 0 && !plain.Contains("code: "), ""));

            string second = KmhGlobalRegistration.DeepLink(b + "?next=1", "AAAA-BBBB");
            r.Add(("owner link: a url that already has a query gets '&', not a second '?'",
                   second.EndsWith("?next=1&code=AAAA-BBBB", System.StringComparison.Ordinal), second));

            string weird = KmhGlobalRegistration.DeepLink(b, "A&B=C#D");
            bool safe = System.Uri.TryCreate(weird, System.UriKind.Absolute, out System.Uri wu)
                     && wu.Query.IndexOf('#') < 0 && wu.Query.IndexOf('&', 1) < 0;
            r.Add(("owner link: a malformed code cannot inject extra query or fragment", safe, weird));

            r.Add(("owner link: with no code it is still the plain valid page url",
                   KmhGlobalRegistration.DeepLink(b, "") == b, ""));
            r.Add(("owner link: with no url it is empty rather than a broken fragment",
                   KmhGlobalRegistration.DeepLink("", "ABCD") == "", ""));
        }

        private static void SetupStatePersistence(List<(string, bool, string)> r)
        {
            GlobalSetupState registered = new GlobalSetupState();
            registered.Advance(SetupStage.Registered);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(registered);

            bool onlyStage = !json.Contains("\"Current\"") && !json.Contains("\"IsRegistered\"")
                          && !json.Contains("\"HasDeferred\"") && json.Contains("\"Stage\"");
            r.Add(("global setup: only Stage is persisted, never the values derived from it", onlyStage, json));

            // A file from before that rule still carries the derived copies, and loading must not let them win.
            const string legacy = "{\"SchemaVersion\":1,\"Stage\":\"NotStarted\",\"Current\":7,\"IsRegistered\":true}";
            GlobalSetupState back = Newtonsoft.Json.JsonConvert.DeserializeObject<GlobalSetupState>(legacy);
            bool honoured = back != null && back.Current == SetupStage.NotStarted && !back.IsRegistered;
            r.Add(("global setup: a stale derived value in an old file cannot override Stage", honoured,
                   honoured ? "Stage wins" : $"OVERRIDDEN to {back?.Current}"));

            GlobalSetupState round = Newtonsoft.Json.JsonConvert.DeserializeObject<GlobalSetupState>(json);
            r.Add(("global setup: a real registered state survives a round trip",
                   round != null && round.IsRegistered, ""));
        }

        private static void PrivateHosts(List<(string, bool, string)> r)
        {
            string[] never = { "127.0.0.1", "0.0.0.0", "localhost", "192.168.1.10", "10.0.0.5",
                               "172.16.4.1", "172.31.255.254", "169.254.1.1", "100.64.0.1",
                               "::1", "kmh.local", "box.lan", "nas.home", "svc.internal" };
            List<string> leaked = never.Where(h => !GlobalConfigCheck.IsPrivateHost(h)).ToList();
            r.Add(("global config: no private or loopback address is ever publishable", leaked.Count == 0,
                   leaked.Count == 0 ? $"{never.Length} forms refused" : "PUBLISHABLE: " + string.Join(", ", leaked)));

            // 172.15 and 172.32 sit just outside the private block and must stay usable.
            string[] ok = { "rwt.knappe0.games", "example.com", "172.15.0.1", "172.32.0.1", "8.8.8.8", "203.0.113.5" };
            List<string> blocked = ok.Where(GlobalConfigCheck.IsPrivateHost).ToList();
            r.Add(("global config: a real public host is not mistaken for a private one", blocked.Count == 0,
                   blocked.Count == 0 ? "" : "REFUSED: " + string.Join(", ", blocked)));

            r.Add(("global config: a directory url on a private host is recognised as unreachable",
                   GlobalConfigCheck.IsLocalEndpoint("http://127.0.0.1:5042")
                   && GlobalConfigCheck.IsLocalEndpoint("http://192.168.1.50:8080")
                   && !GlobalConfigCheck.IsLocalEndpoint("https://global.knappe0.games"), ""));
        }

        private static GlobalConfig Cfg(string baseUrl, string host, bool enabled = true, string visibility = "Public")
            => new GlobalConfig { Enabled = enabled, BaseUrl = baseUrl, PublicHost = host, Visibility = visibility };

        // "Auto" must resolve to a real place, or the public browser shows the literal word.
        private static void Regions(List<(string, bool, string)> r)
        {
            GlobalConfig shipped = new GlobalConfig();
            r.Add(("global config: the shipped Region default is treated as unset, not published as a place",
                   GlobalConfig.IsUnsetRegion(shipped.Listing.Region) && shipped.Listing.ResolvedRegion == "",
                   $"default='{shipped.Listing.Region}'"));

            GlobalConfig set = new GlobalConfig { Listing = new ListingSettings { Region = " EU " } };
            r.Add(("global config: a real region is published, trimmed", set.Listing.ResolvedRegion == "EU", set.Listing.ResolvedRegion));

            bool unsetForms = GlobalConfig.IsUnsetRegion("") && GlobalConfig.IsUnsetRegion("   ")
                           && GlobalConfig.IsUnsetRegion("auto") && GlobalConfig.IsUnsetRegion("AUTO")
                           && GlobalConfig.IsUnsetRegion(null) && !GlobalConfig.IsUnsetRegion("NA");
            r.Add(("global config: every spelling of the placeholder counts as unset, a real one never does",
                   unsetForms, ""));

            var noRegion = GlobalConfigCheck.Inspect(Cfg(GlobalConfigCheck.OfficialBaseUrl, "rwt.example.com"), registered: true);
            GlobalFinding f = noRegion.FirstOrDefault(x => x.Code == "GLOBAL-NO-REGION");
            r.Add(("global config: an unset region is explained, with examples of what to put",
                   f != null && f.Level == GlobalFindingLevel.Info && f.Action.Contains("NA"),
                   f?.ToString() ?? "NOT DETECTED"));

            var withRegion = GlobalConfigCheck.Inspect(
                new GlobalConfig { Enabled = true, BaseUrl = GlobalConfigCheck.OfficialBaseUrl,
                                   PublicHost = "rwt.example.com", Visibility = "Public", Listing = new ListingSettings { Region = "NA" } },
                registered: true);
            r.Add(("global config: a server with a region set is not nagged about it",
                   !withRegion.Any(x => x.Code == "GLOBAL-NO-REGION"), ""));

            // A private server is not in the browser at all, so a missing region costs it nothing.
            var privateNoRegion = GlobalConfigCheck.Inspect(
                Cfg(GlobalConfigCheck.OfficialBaseUrl, "rwt.example.com", visibility: "Private"), registered: true);
            r.Add(("global config: a private server is not told about region filtering it never appears in",
                   !privateNoRegion.Any(x => x.Code == "GLOBAL-NO-REGION"), ""));
        }

        private static void Findings(List<(string, bool, string)> r)
        {
            Regions(r);

            var off = GlobalConfigCheck.Inspect(Cfg(GlobalConfigCheck.OfficialBaseUrl, "", enabled: false), registered: false);
            r.Add(("global config: a disabled directory says how to turn it on and nothing else",
                   off.Count == 1 && off[0].Code == "GLOBAL-OFF" && off[0].Action.Contains("kmh global setup"), ""));

            var stale = GlobalConfigCheck.Inspect(Cfg("http://127.0.0.1:5042", "rwt.example.com"), registered: true);
            GlobalFinding local = stale.FirstOrDefault(f => f.Code == "GLOBAL-LOCAL-ENDPOINT");
            bool named = local != null && local.Level == GlobalFindingLevel.Blocker
                      && local.Message.Contains("127.0.0.1:5042") && local.Action.Contains("global.knappe0.games");
            r.Add(("global config: a retired local directory is named, with the command that fixes it", named,
                   local?.ToString() ?? "NOT DETECTED"));

            var priv = GlobalConfigCheck.Inspect(Cfg(GlobalConfigCheck.OfficialBaseUrl, "127.0.0.1"), registered: true);
            r.Add(("global config: a private PublicHost is a blocker, not a silent empty listing",
                   priv.Any(f => f.Code == "GLOBAL-PRIVATE-ADDRESS" && f.Level == GlobalFindingLevel.Blocker), ""));

            var noAddr = GlobalConfigCheck.Inspect(Cfg(GlobalConfigCheck.OfficialBaseUrl, ""), registered: true);
            r.Add(("global config: a listed server with no address is told Join will not appear",
                   noAddr.Any(f => f.Code == "GLOBAL-NO-ADDRESS"), ""));

            var unlisted = GlobalConfigCheck.Inspect(
                Cfg(GlobalConfigCheck.OfficialBaseUrl, "", visibility: "Private"), registered: true);
            r.Add(("global config: a private server is not nagged about a missing public address",
                   !unlisted.Any(f => f.Code == "GLOBAL-NO-ADDRESS"), ""));

            // A region is part of being set up: without one the listing is filtered out of every region search.
            GlobalConfig goodCfg = Cfg(GlobalConfigCheck.OfficialBaseUrl, "rwt.knappe0.games");
            goodCfg.Listing.Region = "NA";
            var good = GlobalConfigCheck.Inspect(goodCfg, registered: true);
            r.Add(("global config: a correctly set up server reports nothing to fix",
                   good.Count == 0, good.Count == 0 ? "" : string.Join(" | ", good.Select(f => f.Code))));

            var unreg = GlobalConfigCheck.Inspect(Cfg(GlobalConfigCheck.OfficialBaseUrl, "rwt.knappe0.games"), registered: false);
            r.Add(("global config: an unregistered server is told the one command that publishes it",
                   unreg.Any(f => f.Code == "GLOBAL-NOT-REGISTERED" && f.Action.Contains("kmh global setup")), ""));

            r.Add(("global config: a blocker is distinguishable from advice",
                   GlobalConfigCheck.AnyBlocker(priv) && !GlobalConfigCheck.AnyBlocker(unreg), ""));
        }
    }
}
