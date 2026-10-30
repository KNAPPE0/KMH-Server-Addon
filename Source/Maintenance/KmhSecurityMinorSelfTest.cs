using System;
using System.Collections.Generic;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhSecurityMinorSelfTest
    {
        public static List<(string, bool, string)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
                finally { JsonFileStore.ClearFailureStateForTest(); }
            }
            string Fresh(string tag) => "selftest_" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            ModerationDurability(Check, Fresh);
            StatusDisclosure(Check);
            ConfigEditorLimits(Check);
            GuildOutpostClaim(Check, Fresh);
            QuestVisibilityCase(Check);
            RoadworksMalformedSteps(Check, Fresh);
            SiteStorageCollect(Check, Fresh);
            GuildSiteProduction(Check, Fresh);
            VaultCeiling(Check, Fresh);
            GuildWithdrawAccess(Check, Fresh);
            AccessFeeWaiver(Check, Fresh);
            BootIdentityAndLoads(Check);
            ArchetypePerks(Check);
            FrontierPlacementBounds(Check);
            EconomyResetStall(Check, Fresh);
            InFlightRequests(Check);
            SeasonResetJournal(Check);
            BackupBuckets(Check);
            NetworkBanWithoutTicket(Check, Fresh);
            PlayerTextAndGrowth(Check, Fresh);
            return r;
        }

        private static ServerClient Client(string user)
        {
            var c = new ServerClient(null, null, createListener: false);
            c.GetData<UserFile>(new UserFile { Username = user });
            return c;
        }

        // Other players' words must not reach any screen with markup live, nor four stores grow per request.
        private static void PlayerTextAndGrowth(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            const string marked = "<color=red><size=60>x</size></color>";
            Check("Item names: only def names are stored, so nothing drawn for other players can carry markup", () =>
            {
                bool good = Util.KmhSafe.IsItemKey("Steel") && Util.KmhSafe.IsItemKey("Gun_Revolver|Steel|3")
                         && Util.KmhSafe.IsItemKey("MinifiedThing|WoodLog|0|Table") && Util.KmhSafe.IsItemKey("Mod.Item-X||-1");
                bool bad = !Util.KmhSafe.IsItemKey(marked) && !Util.KmhSafe.IsItemKey("Steel|" + marked + "|3")
                        && !Util.KmhSafe.IsItemKey("a|b|c|d|e") && !Util.KmhSafe.IsItemKey(new string('A', 200)) && !Util.KmhSafe.IsItemKey("Steel Bar");
                string compact = Features.Treasury.TreasuryHandler.ItemClaimRefusal(
                    new Features.Treasury.Dto.TreasuryItemRequest { ItemDefName = "Steel|" + marked, Qty = 1 }, 0, out _);
                string stack = Features.Treasury.TreasuryHandler.ItemClaimRefusal(new Features.Treasury.Dto.TreasuryItemRequest
                    { Payloads = new List<Items.KmhThingPayload> { new Items.KmhThingPayload { DefName = "Steel", StuffDefName = marked, StackCount = 1 } } }, 0, out _);
                (long wantId, string wantWhy) = Features.WantBoard.WantStore.Post(Fresh("wm"), marked, 1, 10, 24, "public");
                string site = Features.Sites.SiteStore.QuoteBuild(Fresh("sm"), marked, 1, 10f, "", 1).Reason;
                bool refused = compact != null && stack != null && wantId == 0 && wantWhy == "That isn't an item name." && site == "That isn't an item name.";
                return (good && bad && refused, $"good={good} bad={bad} deposit='{compact}' stack='{stack}' want='{wantWhy}' site='{site}'");
            });

            Check("Player words in notices, site rosters and video titles are drawn as text", () =>
            {
                string note = Features.Mail.MailHandler.NewMailNote("Mal<b>", marked + new string('y', 500));
                string worker = Features.Sites.SiteStore.WorkerName(marked);
                string title = Features.Media.KmhVideoHelper.TitleOf(marked + new string('z', 500));
                bool inert = note.IndexOf('<') < 0 && worker.IndexOf('<') < 0 && title.IndexOf('<') < 0;
                bool capped = note.Length < 140 && worker.Length <= 48 && title.Length <= 120;
                return (inert && capped, $"note={note.Length} worker={worker} title={title.Length}");
            });

            Check("Mail and guild invites go only to names that have played here", () =>
            {
                string ghost = Fresh("ghost"), player = Fresh("real");
                try
                {
                    bool ghostRefused = !Features.Mail.MailHandler.RecipientKnown(ghost);
                    Features.PlayerStats.PlayerStatsStore.EnsurePlayer(player);
                    bool realKnown = Features.Mail.MailHandler.RecipientKnown(player) && Features.Mail.MailHandler.RecipientKnown(player.ToUpperInvariant());
                    return (ghostRefused && realKnown, $"ghostRefused={ghostRefused} realKnown={realKnown}");
                }
                finally { Features.PlayerStats.PlayerStatsStore.RemoveUser(player); }
            });

            Check("Withdrawals stop while a player has 32 deliveries that never landed", () =>
            {
                string user = Fresh("owed");
                // A delivery id carries the data set's identity, which a run outside a booted server may not have yet.
                bool setId = string.IsNullOrEmpty(KmhServerIdentity.Id);
                if (setId) KmhDataMeta.SetInstanceIdForTest("selftestsrv");
                try
                {
                    for (int i = 0; i < Features.Delivery.DeliveryStore.MaxOwedPerPlayer - 1; i++)
                        if (Features.Delivery.DeliveryStore.Owe(user, 1, null, null, "selftest-owed-" + i) == null) return (false, "seed " + i);
                    bool belowAllowed = !Features.Delivery.DeliveryStore.TooManyOwed(user, out _);
                    Features.Delivery.DeliveryStore.Owe(user, 1, null, null, "selftest-owed-last");
                    bool atCapStopped = Features.Delivery.DeliveryStore.TooManyOwed(user, out string why);
                    return (belowAllowed && atCapStopped, $"below={belowAllowed} atCap={atCapStopped} '{why}'");
                }
                finally
                {
                    Features.Delivery.DeliveryStore.PurgeUser(user);
                    if (setId) KmhDataMeta.SetInstanceIdForTest("");
                }
            });

            Check("A vault at its item-kind cap is refused a new kind before the client removes anything", () =>
            {
                string user = Fresh("keys");
                try
                {
                    Features.Treasury.TreasuryStore.SeedItemKeysForTest(user, Features.Treasury.TreasuryStore.MaxItemKeysPerVault);
                    bool newKind = Features.Treasury.TreasuryStore.NewItemKeyOverCap(user, "Steel");
                    bool sameKind = !Features.Treasury.TreasuryStore.NewItemKeyOverCap(user, "KmhSelftestKey7");
                    bool other = !Features.Treasury.TreasuryStore.NewItemKeyOverCap(Fresh("keys2"), "Steel");
                    return (newKind && sameKind && other, $"newRefused={newKind} existingAllowed={sameKind} emptyVaultAllowed={other}");
                }
                finally { Features.Treasury.TreasuryStore.PurgeOwnerForTest(user); }
            });

            Check("Enforcement: a repeated request for the same profile is not pushed again within minutes", () =>
            {
                var c = new ServerClient(null, null, createListener: false);
                DateTime t0 = DateTime.UtcNow;
                bool first = Features.Enforcement.EnforcementHandler.MayPushTo(c, "hash-a", t0);
                bool again = Features.Enforcement.EnforcementHandler.MayPushTo(c, "hash-a", t0.AddSeconds(30));
                bool changed = Features.Enforcement.EnforcementHandler.MayPushTo(c, "hash-b", t0.AddSeconds(31));
                bool later = Features.Enforcement.EnforcementHandler.MayPushTo(c, "hash-b", t0.AddMinutes(4));
                bool otherClient = Features.Enforcement.EnforcementHandler.MayPushTo(new ServerClient(null, null, createListener: false), "hash-b", t0.AddSeconds(32));
                bool ok = first && !again && changed && later && otherClient;
                return (ok, $"first={first} repeat={again} newProfile={changed} afterWait={later} otherClient={otherClient}");
            });

            Check("Staff: a name RWT keeps as a separate account is not the owner, on a Turkish host too", () =>
            {
                var owners = new List<string> { "Iris" };
                System.Globalization.CultureInfo was = System.Globalization.CultureInfo.CurrentCulture;
                try
                {
                    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR", false);
                    bool trVariant = Features.Identity.StaffRegistry.Contains(owners, "iris");
                    bool trSelf = Features.Identity.StaffRegistry.Contains(owners, "Iris");
                    Features.Admin.ConfigEditorGrant.ResetForTest();
                    Features.Admin.ConfigEditorGrant.Open("Iris", TimeSpan.FromMinutes(5), "selftest", DateTimeOffset.UtcNow);
                    bool trGrant = Features.Admin.ConfigEditorGrant.Allows("iris", DateTimeOffset.UtcNow);
                    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US", false);
                    bool enVariant = Features.Identity.StaffRegistry.Contains(owners, "iris");
                    Features.Admin.ConfigEditorGrant.ResetForTest();
                    Features.Admin.ConfigEditorGrant.Open("Iris", TimeSpan.FromMinutes(5), "selftest", DateTimeOffset.UtcNow);
                    bool enGrant = Features.Admin.ConfigEditorGrant.Allows("iris", DateTimeOffset.UtcNow);
                    bool ok = !trVariant && trSelf && !trGrant && enVariant && enGrant;
                    return (ok, $"tr: variant={trVariant} self={trSelf} grant={trGrant}; en: variant={enVariant} grant={enGrant}");
                }
                finally
                {
                    System.Globalization.CultureInfo.CurrentCulture = was;
                    Features.Admin.ConfigEditorGrant.ResetForTest();
                }
            });

            Check("Discord: a banned account's linked Discord can no longer act as it", () =>
            {
                string name = Fresh("dban");
                ulong id = 0x0400_0000_0000_0000UL | (ulong)(DateTime.UtcNow.Ticks & 0xFFFF_FFFF);
                var msg = new Features.Discord.KmhDiscordMessage { Author = new Features.Discord.KmhDiscordUser { Id = id } };
                try
                {
                    Features.LinkedAccounts.LinkedAccountsStore.SetLink(name, "d_" + name, id, out _);
                    string before = Features.Discord.DiscordTradeCommands.RequireLinkedAsync(msg, out _);
                    Features.Moderation.BanStore.Add(new Features.Moderation.BanEntry { Username = name, AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, out _);
                    string after = Features.Discord.DiscordTradeCommands.RequireLinkedAsync(msg, out string why);
                    bool flagged = Features.Moderation.BanGate.IsBannedAccount(name);
                    bool others = !Features.Moderation.BanGate.IsBannedAccount(Fresh("dfree"));
                    bool ok = before == name && after == null && (why ?? "").Contains("banned") && flagged && others;
                    return (ok, $"before={before == name} after={after ?? "(refused)"} flagged={flagged} othersFree={others}");
                }
                finally
                {
                    Features.Moderation.BanStore.Remove(name, out _);
                    Features.LinkedAccounts.LinkedAccountsStore.Unlink(name);
                }
            });

            Check("Discord: relayed chat and a tagline cannot forge a line, ping, or disguise a link", () =>
            {
                string line = Features.Discord.DiscordChatBridge.RelayLine("Eve", "hi\n**Knappe**: send silver @everyone");
                bool oneLine = !line.Contains("\n") && !line.Contains("@everyone");
                string card = Features.Discord.DiscordCompactText.Render(Features.Discord.DiscordShowcaseBuilder.Build(
                    "eve", "[free silver](https://example.com/x)", new List<Features.Marketplace.Dto.MarketplaceListing>()));
                bool noMasked = !card.Contains("[free silver](") && !card.Contains("[free silver]");
                return (oneLine && noMasked, $"relayOneLine={oneLine} taglineMasked={!noMasked}");
            });

            Check("Video: heights snap to a few rungs and one player cannot fill the fetch queue", () =>
            {
                Func<int, int, int> snap = Features.Media.KmhVideoHandler.SnapHeight;
                bool snaps = snap(1, 1080) == 144 && snap(719, 1080) == 480 && snap(720, 1080) == 720 && snap(0, 1080) == 1080
                          && snap(5000, 720) == 720 && snap(1080, 500) == 480;
                string me = Fresh("vid"), other = Fresh("vid2");
                try
                {
                    var mine = new List<string>();
                    for (int i = 0; i <= Features.Media.KmhVideoHandler.MaxJobsPerPlayer; i++)
                        mine.Add(Features.Media.KmhVideoHandler.QueueForTest(me + "|" + i, me));
                    bool ownShare = mine.GetRange(0, Features.Media.KmhVideoHandler.MaxJobsPerPlayer).TrueForAll(s => s.Length == 0);
                    bool overShare = mine[mine.Count - 1].Length > 0;
                    bool othersQueue = Features.Media.KmhVideoHandler.QueueForTest(other + "|0", other).Length == 0;
                    bool ok = snaps && ownShare && overShare && othersQueue;
                    return (ok, $"snaps={snaps} ownShare={ownShare} overShareRefused={overShare} othersQueue={othersQueue}");
                }
                finally
                {
                    Features.Media.KmhVideoHandler.ForgetForTest(me);
                    Features.Media.KmhVideoHandler.ForgetForTest(other);
                }
            });

            Check("Media: the decoder's frame cap follows the byte budget, not just the frame limit", () =>
            {
                var cfg = new Features.Media.MediaConfig();
                uint big = Features.Media.KmhMediaTranscode.FrameCap(4_000_000, cfg);
                uint small = Features.Media.KmhMediaTranscode.FrameCap(100 * 100, cfg);
                long budget = cfg.Images.MaxDecodedBytes / (4_000_000L * 4);
                bool ok = big == budget + 1 && big <= cfg.Images.MaxFrames && small == cfg.Images.MaxFrames + 1;
                return (ok, $"4Mpx cap={big} (budget {budget} frames), 10kpx cap={small}");
            });

            Check("Discord: the guild's own id in a role list admits nobody, reloads are spaced out, and channel posts use the public market view", () =>
            {
                var everyone = new Kmh.Discord.Wire.KmhDiscordCommandRequest { GuildId = 1081681007838777554, RoleIds = new List<string> { "1081681007838777554" } };
                var mod = new Kmh.Discord.Wire.KmhDiscordCommandRequest { GuildId = 1081681007838777554, RoleIds = new List<string> { "1081681007838777554", "222" } };
                bool roles = !Features.Discord.DiscordSlashCommands.HasAnyRole(new[] { "1081681007838777554" }, everyone)
                          && Features.Discord.DiscordSlashCommands.HasAnyRole(new[] { "1081681007838777554", "222" }, mod);
                long t0 = DateTime.UtcNow.Ticks;
                bool cooldown = Features.Discord.DiscordBridge.ReloadAllowed(0, t0)
                             && !Features.Discord.DiscordBridge.ReloadAllowed(t0, t0 + TimeSpan.FromSeconds(30).Ticks)
                             && Features.Discord.DiscordBridge.ReloadAllowed(t0, t0 + Features.Discord.DiscordBridge.ReloadCooldown.Ticks);
                bool publicView = true;
                foreach (string f in new[] { "Commands/DiscordBrowseCommands.cs", "Commands/DiscordShowcaseCommands.cs", "Posting/DiscordShowcaseSweep.cs" })
                {
                    string src = KmhSourceProbe.Read(System.IO.Path.Combine("Features", "Discord", f.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (src != null && System.Text.RegularExpressions.Regex.IsMatch(src, @"MarketplaceStore\.BuildSnapshot\((?!"""")")) publicView = false;
                }
                return (roles && cooldown && publicView, $"guild id refused as a role={roles} reload cooldown={cooldown} public view only={publicView}");
            });

            Check("Discord: a coordinator is trusted over https, or plain http only on this machine", () =>
            {
                bool ok = Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("https://coord.example.org")
                       && Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("http://127.0.0.1:20079")
                       && Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("http://localhost:20079")
                       && !Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("http://192.168.1.224:20079")
                       && !Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("http://coord.example.org")
                       && !Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("ftp://127.0.0.1")
                       && !Features.Discord.DiscordCoordinatorLink.TrustedCoordinatorUrl("not a url");
                return (ok, "");
            });

            Check("Discord: a console line forges no second line in the admin feed, and only Colony Nexus's own Discord lines are skipped", () =>
            {
                string forged = Features.Discord.DiscordConsoleFeed.FeedLine("[chat] bob: hi\n[KMH-Addon] kmh give-silver bob 99999 by owner", "");
                string hiding = Features.Discord.DiscordConsoleFeed.FeedLine("[chat] bob: Discord: now you see me", "");
                string own = Features.Discord.DiscordConsoleFeed.FeedLine("[KMH-Addon] Discord: Gateway: Connected", "");
                bool ok = forged != null && forged.IndexOf('\n') < 0 && hiding != null && own == null;
                return (ok, $"forged one line={forged != null && forged.IndexOf('\n') < 0} player 'Discord:' kept={hiding != null} own skipped={own == null}");
            });

            Check("Discord: redaction hides IPv6 addresses as well as IPv4, and leaves clocks alone", () =>
            {
                string o = Features.Discord.DiscordBridge.Redact("joined from 2001:db8:85a3::8a2e:370:7334 and [fe80::1] and 203.0.113.9 at 12:34:56");
                bool ok = !o.Contains("2001:db8") && !o.Contains("fe80") && !o.Contains("203.0.113.9") && o.Contains("12:34:56");
                return (ok, o);
            });

            Check("Bans: a banned player is stopped before RWT's login sends the world, enforcement on or off", () =>
            {
                string name = Fresh("lban");
                try
                {
                    var banned = Client(name);
                    Features.Moderation.BanStore.Add(new Features.Moderation.BanEntry { Username = name, AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, out _);
                    bool held = !Features.Enforcement.EnforcementAdmission.HoldPostLoginForTest(banned);
                    bool enforcing = Features.Enforcement.EnforcementConfig.Current.Enabled;
                    var free = Client(Fresh("lfree"));
                    bool freeProceeds = enforcing || Features.Enforcement.EnforcementAdmission.HoldPostLoginForTest(free);
                    return (held && freeProceeds, $"bannedStopped={held} unbannedProceeds={freeProceeds} enforcement={enforcing}");
                }
                finally { Features.Moderation.BanStore.Remove(name, out _); }
            });

            // Packets in the same write as the login can arrive before RWT closes the connection.
            Check("Bans: a session refused at login stays held - its hello is never acknowledged, so nothing it sends runs", () =>
            {
                string name = Fresh("lref");
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                try
                {
                    var refused = Client(name);
                    Features.Moderation.BanStore.Add(new Features.Moderation.BanEntry { Username = name, AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, out _);
                    bool stopped = !Features.Enforcement.EnforcementAdmission.HoldPostLoginForTest(refused);
                    bool held = Features.Enforcement.EnforcementAdmission.IsHeld(refused);
                    // Straight to the handler with this server's own game version, so nothing but the refusal can stop it.
                    SubProtocol.KmhEnvelope ack = SubProtocol.KmhEnvelope.ParseForTest(new
                    {
                        v = SubProtocol.KmhProtocol.CurrentVersion,
                        rimworld = Features.Global.GlobalStatusProbe.RimWorldVersion(),
                    });
                    ack.Kind = SubProtocol.KmhProtocol.Kind.HelloAck;
                    SubProtocol.KmhRouter.OutboundForTest = (c, k, d) => { };
                    SubProtocol.KmhHandshakeHandler.HelloAckForTest(refused, ack);
                    bool compatible = SubProtocol.KmhHandshakeHandler.IsCompatible(refused);
                    var free = Client(Fresh("lok"));
                    SubProtocol.KmhHandshakeHandler.HelloAckForTest(free, ack);
                    bool freeCompatible = SubProtocol.KmhHandshakeHandler.IsCompatible(free);
                    return (stopped && held && !compatible && freeCompatible,
                            $"stopped={stopped} held={held} refusedCompatibleAfterAck={compatible} unbannedCompatible={freeCompatible}");
                }
                finally { SubProtocol.KmhRouter.OutboundForTest = hadOut; Features.Moderation.BanStore.Remove(name, out _); }
            });

            Check("Bans: a session RWT itself refuses is held too - the hook is on RWT's refusal, and its hello is never acknowledged", () =>
            {
                const string id = "kmh.selftest.denyhold";
                var harmony = new HarmonyLib.Harmony(id);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                try
                {
                    bool installed = Features.Enforcement.EnforcementAdmission.InstallDenyHold(harmony);
                    var target = HarmonyLib.AccessTools.DeclaredMethod(typeof(PM_Login), "DenyConnectionWithReason");
                    var info = target == null ? null : HarmonyLib.Harmony.GetPatchInfo(target);
                    bool patched = info != null && System.Linq.Enumerable.Any(info.Prefixes, p => p.owner == id);
                    var denied = Client(Fresh("ldeny"));
                    Features.Enforcement.EnforcementAdmission.HoldDeniedForTest(denied);
                    bool held = Features.Enforcement.EnforcementAdmission.IsHeld(denied);
                    SubProtocol.KmhEnvelope ack = SubProtocol.KmhEnvelope.ParseForTest(new
                    {
                        v = SubProtocol.KmhProtocol.CurrentVersion,
                        rimworld = Features.Global.GlobalStatusProbe.RimWorldVersion(),
                    });
                    ack.Kind = SubProtocol.KmhProtocol.Kind.HelloAck;
                    SubProtocol.KmhRouter.OutboundForTest = (c, k, d) => { };
                    SubProtocol.KmhHandshakeHandler.HelloAckForTest(denied, ack);
                    bool compatible = SubProtocol.KmhHandshakeHandler.IsCompatible(denied);
                    var free = Client(Fresh("ldok"));
                    SubProtocol.KmhHandshakeHandler.HelloAckForTest(free, ack);
                    bool freeCompatible = SubProtocol.KmhHandshakeHandler.IsCompatible(free);
                    return (installed && patched && held && !compatible && freeCompatible,
                            $"installed={installed} prefix on RWT={patched} held={held} deniedCompatibleAfterAck={compatible} otherCompatible={freeCompatible}");
                }
                finally { SubProtocol.KmhRouter.OutboundForTest = hadOut; harmony.UnpatchAll(id); }
            });

            // RWT keeps these apart with the host's ToLower(); the stores fold them with OrdinalIgnoreCase or ToLowerInvariant.
            Check("Accounts: a name the stores would keep as an existing account's is a clash, under either fold, on any host", () =>
            {
                string micro = "µark", mu = "μark", sigma = "Νίκοσ", finalSigma = "Νίκος";
                var I = Features.Identity.AccountNameGuard.ClashWith("IRIS", new[] { "iris" });
                var greek = Features.Identity.AccountNameGuard.ClashWith(mu, new[] { "bob", micro });
                var sigmas = Features.Identity.AccountNameGuard.ClashWith(finalSigma, new[] { sigma });
                var kelvin = Features.Identity.AccountNameGuard.ClashWith("Kai", new[] { "kai" });
                var self = Features.Identity.AccountNameGuard.ClashWith("iris", new[] { "iris" });
                var other = Features.Identity.AccountNameGuard.ClashWith("iris2", new[] { "iris" });
                bool ok = I == "iris" && greek == micro && sigmas == sigma && kelvin == "kai" && self == null && other == null;
                return (ok, $"I={I} greek={greek != null} sigma={sigmas != null} kelvin={kelvin} self={self ?? "-"} other={other ?? "-"}");
            });

            Check("Accounts: RWT's own login packet is read, and a look-alike registration is sent down the sign-in path", () =>
            {
                object Login(string user)
                {
                    object pkt = Activator.CreateInstance(typeof(PKT_Login));
                    foreach (string member in new[] { "Username", "_username" })
                    {
                        var p = typeof(PKT_Login).GetProperty(member);
                        if (p != null && p.CanWrite) { p.SetValue(pkt, user); return pkt; }
                        var f = typeof(PKT_Login).GetField(member);
                        if (f != null) { f.SetValue(pkt, user); return pkt; }
                    }
                    return null;
                }
                Features.Identity.StaffRegistry.AccountsForTest(new Dictionary<string, Features.Identity.StaffRegistry.RwtAccount>(StringComparer.Ordinal)
                    { ["µark"] = default });
                try
                {
                    object lookAlike = Login("μark"), fresh = Login("mark2");
                    if (lookAlike == null) return (false, "RWT's PKT_Login has neither Username nor _username");
                    bool refused = Features.Identity.AccountNameGuard.AfterExistsCheckForTest(lookAlike, false);
                    bool admitted = !Features.Identity.AccountNameGuard.AfterExistsCheckForTest(fresh, false);
                    bool kept = Features.Identity.AccountNameGuard.AfterExistsCheckForTest(fresh, true);
                    return (refused && admitted && kept, $"look-alike refused={refused} new name registers={admitted} existing stays={kept}");
                }
                finally { Features.Identity.StaffRegistry.AccountsForTest(null); }
            });

            Check("Accounts: the guard patches RWT's real account check, and RWT's account list resolves", () =>
            {
                const string id = "kmh.selftest.accountnames";
                var harmony = new HarmonyLib.Harmony(id);
                try
                {
                    bool installed = Features.Identity.AccountNameGuard.Install(harmony);
                    var target = RwtCompat.UserStoreMethod("CheckIfUserExists", 2);
                    var info = target == null ? null : HarmonyLib.Harmony.GetPatchInfo(target);
                    bool patched = info != null && System.Linq.Enumerable.Any(info.Postfixes, p => p.owner == id);
                    bool list = RwtCompat.UserStoreMethod("GetAllUserFiles", 0) != null;
                    return (installed && patched && list, $"installed={installed} postfix on RWT={patched} account list={list}");
                }
                finally { harmony.UnpatchAll(id); }
            });

            Check("Accounts: two accounts already sharing a vault are both held at login, a lone one is not", () =>
            {
                Features.Identity.StaffRegistry.AccountsForTest(new Dictionary<string, Features.Identity.StaffRegistry.RwtAccount>(StringComparer.Ordinal)
                    { ["µark"] = default, ["μark"] = default, ["solo"] = default });
                try
                {
                    var twin = Client("μark");
                    bool held = !Features.Enforcement.EnforcementAdmission.HoldPostLoginForTest(twin) && Features.Enforcement.EnforcementAdmission.IsHeld(twin);
                    bool enforcing = Features.Enforcement.EnforcementConfig.Current.Enabled;
                    bool loneProceeds = enforcing || Features.Enforcement.EnforcementAdmission.HoldPostLoginForTest(Client("solo"));
                    return (held && loneProceeds, $"twin held={held} lone proceeds={loneProceeds} enforcement={enforcing}");
                }
                finally { Features.Identity.StaffRegistry.AccountsForTest(null); }
            });

            Check("Accounts: RWT's own admin and ban flags are read, so an RWT ban also stops Discord trading", () =>
            {
                Features.Identity.StaffRegistry.AccountsForTest(new Dictionary<string, Features.Identity.StaffRegistry.RwtAccount>(StringComparer.Ordinal)
                    { ["rwtbanned"] = new Features.Identity.StaffRegistry.RwtAccount(false, true), ["rwtadmin"] = new Features.Identity.StaffRegistry.RwtAccount(true, false) });
                try
                {
                    bool banned = Features.Moderation.BanGate.IsBannedAccount("rwtbanned") && !Features.Moderation.BanGate.IsBannedAccount("rwtadmin");
                    bool admin = Features.Identity.StaffRegistry.IsRwtAdmin("rwtadmin") && !Features.Identity.StaffRegistry.IsRwtAdmin("rwtbanned");
                    return (banned && admin, $"RWT ban seen={banned} RWT admin seen={admin}");
                }
                finally { Features.Identity.StaffRegistry.AccountsForTest(null); }
            });

            Check("Chat: an account named like the system name is refused and its chat never relayed", () =>
            {
                string sys = SubProtocol.KmhProtocol.SystemUsername;
                bool names = Features.Moderation.BanGate.IsReservedName(sys) && Features.Moderation.BanGate.IsReservedName("[kmh-sys]")
                          && Features.Moderation.BanGate.IsReservedName(" ⁠[KMH-CLI] ") && Features.Moderation.BanGate.IsReservedName("[​KMH-SYS]")
                          && !Features.Moderation.BanGate.IsReservedName("KMH fan")
                          && !Features.Moderation.BanGate.IsReservedName("Knappe");
                bool loginRefused = Features.Moderation.BanGate.RefusalFor(sys, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds()) != null;
                var forger = Client(sys);
                bool chatDropped = !SubProtocol.Patches.Patch_PM_Chat_KmhIntercept.Prefix(forger, new byte[] { 1, 2, 3 });
                var player = Client(Fresh("chat"));
                bool playerRelayed = SubProtocol.Patches.Patch_PM_Chat_KmhIntercept.Prefix(player, new byte[] { 1, 2, 3 });
                bool ok = names && loginRefused && chatDropped && playerRelayed;
                return (ok, $"names={names} loginRefused={loginRefused} forgerChatDropped={chatDropped} playerChatPasses={playerRelayed}");
            });

            Check("Names: no account, guild or badge passes for Colony Nexus, Global or this server's staff", () =>
            {
                string[] platform = { "Colony Nexus", "C0l0ny_N3xus", "ColonyNexus Support", "ＣＯＬＯＮＹ ＮＥＸＵＳ", "Global", "G1obal", "GIoba|",
                                      "System", "KMH Staff", "Worldweave", "CN Team" };
                string[] people = { "Global Traders", "Nexus Bob", "Kilo", "Ada", "KMH fan", "Systematic", "Knappe" };
                var missed = new List<string>();
                foreach (string n in platform) if (!Features.Moderation.BanGate.IsReservedName(n)) missed.Add(n);
                foreach (string n in people) if (Features.Moderation.BanGate.IsReservedName(n)) missed.Add("refused " + n);

                var cfg = new Features.Identity.StaffConfig { Owners = new List<string> { "Bill" }, Moderators = new List<string> { "Ann", "Kai" } };
                bool lookAlikes = Features.Moderation.BanGate.StaffLookAlike("Bi1l", cfg) == "Bill" && Features.Moderation.BanGate.StaffLookAlike("BiII", cfg) == "Bill"
                               && Features.Moderation.BanGate.StaffLookAlike("Аnn", cfg) == "Ann" && Features.Moderation.BanGate.StaffLookAlike("bill", cfg) == null
                               && Features.Moderation.BanGate.StaffLookAlike("Bob", cfg) == null && Features.Moderation.BanGate.StaffLookAlike("Kal", cfg) == null;

                bool guilds = !Features.Guilds.GuildNames.TryNormalize("Colony Nexus", out _, out _) && !Features.Guilds.GuildNames.TryNormalize("Gl0bal", out _, out _)
                           && !Features.Guilds.GuildNames.TryNormalize("Gu⁠ild", out _, out _) && Features.Guilds.GuildNames.TryNormalize("Global Traders", out _, out _);
                bool badges = Features.Identity.StaffConfig.ClampLabel("Global Mod") == "" && Features.Identity.StaffConfig.ClampLabel("CN Staff") == ""
                           && Features.Identity.StaffConfig.ClampLabel("0fficial") == "" && Features.Identity.StaffConfig.ClampLabel("Mod") == "Mod";
                bool unnamed = !Features.Moderation.KmhBrandWords.ClaimsAuthority(KmhServerIdentity.FallbackName);
                return (missed.Count == 0 && lookAlikes && guilds && badges && unnamed,
                        $"misread: [{string.Join(", ", missed)}]; staff look-alikes {lookAlikes}; guild names {guilds}; badge labels {badges}; "
                      + $"unnamed server '{KmhServerIdentity.FallbackName}' neutral {unnamed}");
            });

            Check("Treasury: folded food whose stacks wore differently is stored, with its saved copy at the averaged condition", () =>
            {
                string Blob(int hp) => "<kmhthing><t><def>RawRice</def><health>" + hp + "</health><stackCount>75</stackCount></t></kmhthing>";
                var folded = new Items.KmhThingPayload { DefName = "RawRice", StackCount = 150, HitPoints = 59, MaxHitPoints = 60, Mergeable = true, ScribeXml = Blob(60) };
                bool foldedOk = Items.KmhItemSafety.BlobMatches(folded, out string why) && folded.ScribeXml.Contains("<health>59</health>");
                var gear = new Items.KmhThingPayload { DefName = "Gun_Revolver", StackCount = 1, HitPoints = 100, MaxHitPoints = 100, ScribeXml = "<kmhthing><t><def>Gun_Revolver</def><health>40</health></t></kmhthing>" };
                bool gearRefused = !Items.KmhItemSafety.BlobMatches(gear, out _);
                var row = new Items.KmhThingPayload { DefName = "RawRice", StackCount = 75, HitPoints = 60, MaxHitPoints = 60, Mergeable = true, ScribeXml = Blob(60), Fingerprint = "keep" };
                Items.KmhItemSafety.MergeFungible(row, new Items.KmhThingPayload { DefName = "RawRice", StackCount = 75, HitPoints = 50, MaxHitPoints = 60, Mergeable = true, ScribeXml = Blob(50) });
                bool rowSynced = row.HitPoints == 55 && row.ScribeXml.Contains("<health>55</health>");
                bool rowValid = rowSynced && Items.KmhItemSafety.ValidatePayload(row) && row.Fingerprint == "keep";
                return (foldedOk && gearRefused && rowValid, $"folded={foldedOk} ({why}) gearMismatchRefused={gearRefused} mergedRowValid={rowValid} hp={row.HitPoints}");
            });

            Check("Console: 'kmh' run from Discord's console-run is not the machine's console", () =>
            {
                bool Refused(List<string> said) => said.Exists(l => l.Contains("can only be run"));
                var remoteGlobal = new List<string>(); var remoteOwner = new List<string>(); var remoteEditor = new List<string>(); var local = new List<string>();
                AdminCommands.ConsoleExecutor.AsRemote(() =>
                {
                    AdminCommands.KmhServerConsoleCommand.Invoke(new[] { "global", "nosuch" }, remoteGlobal.Add);
                    AdminCommands.KmhServerConsoleCommand.Invoke(new[] { "setup", "owner", Fresh("own") }, remoteOwner.Add);
                    AdminCommands.KmhServerConsoleCommand.Invoke(new[] { "config-editor" }, remoteEditor.Add);
                });
                AdminCommands.KmhServerConsoleCommand.Invoke(new[] { "global", "nosuch" }, local.Add);
                bool ok = Refused(remoteGlobal) && Refused(remoteOwner) && Refused(remoteEditor) && !Refused(local) && !AdminCommands.ConsoleExecutor.Remote;
                return (ok, $"remote global/owner/editor refused={Refused(remoteGlobal)}/{Refused(remoteOwner)}/{Refused(remoteEditor)} localRefused={Refused(local)}");
            });

            // An admin-typed UNC path makes the host authenticate to another machine; a manifest could name any file.
            Check("Admin: snapshot-verify and restore-preview-player only open folders under KMH-Data/Snapshots", () =>
            {
                string root = KmhDataPaths.SnapshotsRoot;
                string own = System.IO.Path.Combine(root, "selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    bool uncVerified = KmhSnapshot.Verify(@"\\203.0.113.5\share", out string uncWhy);
                    bool outsideRefused = KmhSnapshot.ResolveSnapshotDir(@"\\203.0.113.5\share") == null
                        && KmhSnapshot.ResolveSnapshotDir("//203.0.113.5/share") == null
                        && KmhSnapshot.ResolveSnapshotDir(System.IO.Path.GetTempPath()) == null
                        && KmhSnapshot.ResolveSnapshotDir(System.IO.Path.Combine("..", "Config")) == null
                        && !uncVerified;
                    var preview = new List<string>();
                    KmhSnapshot.PreviewPlayer(@"\\203.0.113.5\share", preview.Add);
                    System.IO.Directory.CreateDirectory(own);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(own, "x_manifest.json"),
                        "{\"files\":[{\"name\":\"" + System.IO.Path.Combine("..", "..", "Config", "Global.json").Replace("\\", "\\\\") + "\",\"sha256\":\"00\"}]}");
                    bool manifestRefused = !KmhSnapshot.Verify(own, out string manifestWhy) && manifestWhy.Contains("outside");
                    bool insideResolves = KmhSnapshot.ResolveSnapshotDir(System.IO.Path.GetFileName(own)) != null;
                    bool ok = outsideRefused && preview.Exists(l => l.Contains("Only a folder under")) && manifestRefused && insideResolves;
                    return (ok, $"UNC/temp/.. refused={outsideRefused} ({uncWhy}), preview refused={preview.Exists(l => l.Contains("Only a folder under"))}, "
                              + $"manifest ../.. refused={manifestRefused} ({manifestWhy}), own folder resolves={insideResolves}");
                }
                finally { try { System.IO.Directory.Delete(own, true); } catch { } }
            });

            // Replaces every player's data, so an admin badge alone must not reach it; a bogus name keeps it harmless.
            Check("Admin: a restore needs the owner or this machine's console, not just an admin badge", () =>
            {
                bool OwnerOnly(List<string> said) => said.Exists(l => l.Contains("owner-only"));
                var admin = new List<string>(); var console = new List<string>(); var remote = new List<string>();
                string[] cmd = { "restore", "selftest-no-such-backup" };
                AdminCommands.KmhServerCommands.Dispatch(cmd, true, Fresh("rst"), admin.Add);
                AdminCommands.KmhServerCommands.Dispatch(cmd, true, "console", console.Add, fromConsole: true);
                AdminCommands.ConsoleExecutor.AsRemote(() => AdminCommands.KmhServerConsoleCommand.Invoke(cmd, remote.Add));
                bool ok = OwnerOnly(admin) && !OwnerOnly(console) && OwnerOnly(remote);
                return (ok, $"adminRefused={OwnerOnly(admin)} consoleReached={!OwnerOnly(console)} discordRefused={OwnerOnly(remote)}");
            });

            // Each hands server-held value to a named player, so a moderator could pay it to themselves.
            Check("Admin: the house pool, orphaned vaults, held value, bounty approval and new or credited world quests need the owner", () =>
            {
                bool OwnerOnly(List<string> said) => said.Exists(l => l.Contains("owner-only"));
                string actor = Fresh("val");
                var drain = new List<string>(); var release = new List<string>(); var drop = new List<string>();
                var list = new List<string>(); var retry = new List<string>(); var consoleDrop = new List<string>();
                AdminCommands.KmhServerCommands.Dispatch(new[] { "drain-house", actor }, true, actor, drain.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "orphans", "release-vault", "selftest-no-such-vault", actor }, true, actor, release.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "recover", "drop", "999999999", actor }, true, actor, drop.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "orphans" }, true, actor, list.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "recover", "retry", "999999999" }, true, actor, retry.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "recover", "drop", "999999999", actor }, true, "console", consoleDrop.Add, fromConsole: true);
                var approve = new List<string>(); var reject = new List<string>(); var wqNew = new List<string>(); var wqCredit = new List<string>(); var wqList = new List<string>();
                AdminCommands.KmhServerCommands.Dispatch(new[] { "quest", "approve", "999999999" }, true, actor, approve.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "quest", "reject", "999999999" }, true, actor, reject.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "wq", "comp", "deliver", "Steel", "1", "2000000000" }, true, actor, wqNew.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "wq", "deliver", "999999999", actor, "1" }, true, actor, wqCredit.Add);
                AdminCommands.KmhServerCommands.Dispatch(new[] { "wq", "list" }, true, actor, wqList.Add);
                bool ok = OwnerOnly(drain) && OwnerOnly(release) && OwnerOnly(drop) && !OwnerOnly(list) && !OwnerOnly(retry) && !OwnerOnly(consoleDrop)
                       && OwnerOnly(approve) && !OwnerOnly(reject) && OwnerOnly(wqNew) && OwnerOnly(wqCredit) && !OwnerOnly(wqList);
                return (ok, $"admin refused: drain={OwnerOnly(drain)} release-vault={OwnerOnly(release)} drop={OwnerOnly(drop)} quest approve={OwnerOnly(approve)} "
                          + $"wq new={OwnerOnly(wqNew)} wq deliver={OwnerOnly(wqCredit)}; still open: orphans list={!OwnerOnly(list)} retry={!OwnerOnly(retry)} "
                          + $"console drop={!OwnerOnly(consoleDrop)} quest reject={!OwnerOnly(reject)} wq list={!OwnerOnly(wqList)}");
            });

            // A vault cleared alone leaves escrow and owed payouts behind, and an unconfirmed command must change nothing.
            Check("Admin: treasury-reset changes nothing without 'confirm', and one player goes through the journaled wipe", () =>
            {
                string who = Fresh("trs");
                var said = new List<string>();
                try
                {
                    if (!Features.Treasury.TreasuryStore.DepositSilver(who, 700, "selftest seed")) return (false, "seed");
                    AdminCommands.KmhServerCommands.Dispatch(new[] { "treasury-reset", who }, true, "selftest", said.Add);
                    long afterDry = Features.Treasury.TreasuryStore.GetPersonalSilver(who);
                    AdminCommands.KmhServerCommands.Dispatch(new[] { "treasury-reset", who, "confirm" }, true, "selftest", said.Add);
                    long afterConfirm = Features.Treasury.TreasuryStore.GetPersonalSilver(who);
                    bool journaled = said.Exists(l => l.Contains("=== wipe-economy")) && !Features.Economy.KmhEconomyReset.InProgress;
                    bool ok = afterDry == 700 && afterConfirm == 0 && journaled;
                    return (ok, $"afterDryRun={afterDry} (want 700) afterConfirm={afterConfirm} (want 0) viaWipeEconomy={journaled}");
                }
                finally
                {
                    Features.Economy.EconomyResetStore.Forget(who);
                    Features.Treasury.TreasuryStore.PurgeOwnerForTest(who);
                }
            });

            Check("A quest's lifetime is bounded, so it can neither overflow nor never expire", () =>
            {
                bool ok = Features.Quests.QuestStore.LifetimeHours(int.MaxValue) == Features.Quests.QuestStore.MaxLifetimeHours
                       && Features.Quests.QuestStore.LifetimeHours(240_000_000) == Features.Quests.QuestStore.MaxLifetimeHours
                       && Features.Quests.QuestStore.LifetimeHours(0) == 168 && Features.Quests.QuestStore.LifetimeHours(5) == 5;
                return (ok, $"max={Features.Quests.QuestStore.LifetimeHours(int.MaxValue)}");
            });
        }

        // A network ban checked only when a player chooses to present a join ticket is dodged by not presenting one.
        private static void NetworkBanWithoutTicket(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Network bans: a name that once verified as a banned account is refused without a ticket", () =>
            {
                string bansFile = KmhDataPaths.NetworkBansFile, knownFile = KmhDataPaths.KnownIdentitiesFile;
                byte[] bans = System.IO.File.Exists(bansFile) ? System.IO.File.ReadAllBytes(bansFile) : null;
                byte[] known = System.IO.File.Exists(knownFile) ? System.IO.File.ReadAllBytes(knownFile) : null;
                string name = Fresh("nb"), gid = "g-" + Guid.NewGuid().ToString("N");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                try
                {
                    Features.Global.KmhKnownIdentities.ResetForTest();
                    Features.Global.KmhNetworkBans.OnHeartbeatResponse(new Newtonsoft.Json.Linq.JArray(
                        new Newtonsoft.Json.Linq.JObject { ["user_id"] = gid, ["reason"] = "selftest" }));
                    bool unknownAdmitted = Features.Moderation.BanGate.RefusalFor(name, "", now) == null;
                    Features.Global.KmhKnownIdentities.Learn(name, gid, now);
                    bool refused = Features.Moderation.BanGate.RefusalFor(name.ToUpperInvariant(), "", now) != null;
                    bool othersAdmitted = Features.Moderation.BanGate.RefusalFor(Fresh("nbo"), "", now) == null;
                    // A banned account verifying as the padded twin binds the twin, never the real name.
                    string real = Fresh("nbp");
                    Features.Global.KmhKnownIdentities.Learn(real + " ", gid, now);
                    bool twinOnly = Features.Moderation.BanGate.RefusalFor(real, "", now) == null
                                 && Features.Moderation.BanGate.RefusalFor(real + " ", "", now) != null;

                    string src = KmhSecurityAuditSelfTest.ReadSource("Features/Global/KmhJoinTicketHandler.cs");
                    bool learnedFirst = src != null && After(src, "TicketResult.Ok", "KmhKnownIdentities.Learn(", "KmhNetworkBans.Find(");
                    bool ok = unknownAdmitted && refused && othersAdmitted && twinOnly && learnedFirst;
                    return (ok, $"unknown={unknownAdmitted} refused={refused} others={othersAdmitted} paddedTwinOnly={twinOnly} learnedBeforeBanCheck={learnedFirst}");
                }
                finally
                {
                    if (bans != null) System.IO.File.WriteAllBytes(bansFile, bans); else if (System.IO.File.Exists(bansFile)) System.IO.File.Delete(bansFile);
                    if (known != null) System.IO.File.WriteAllBytes(knownFile, known); else if (System.IO.File.Exists(knownFile)) System.IO.File.Delete(knownFile);
                    Features.Global.KmhNetworkBans.Reload();
                    Features.Global.KmhKnownIdentities.ResetForTest();
                }
            });
        }

        // Backups a player's new save triggers must not share one retention pool with the owner's restore points.
        private static void BackupBuckets(Action<string, Func<(bool, string)>> Check)
        {
            Check("Backups: a flood of player-triggered backups never prunes an owner's restore point", () =>
            {
                var names = new List<string> { "20260101-000000-boot", "20260102-000000-manual", "20260103-000000-pre-season-reset" };
                for (int i = 10; i < 30; i++) names.Add($"202602{i:00}-000000-{KmhDataBackup.AutoResetReason}");
                List<string> doomed = KmhDataBackup.PruneCandidates(names, 12);
                bool ownerKept = !doomed.Exists(n => !KmhDataBackup.IsPlayerTriggered(n));
                bool floodPruned = doomed.Count == 8 && doomed.TrueForAll(KmhDataBackup.IsPlayerTriggered)
                                && !doomed.Contains($"20260229-000000-{KmhDataBackup.AutoResetReason}");
                return (ownerKept && floodPruned, $"doomed={doomed.Count} ownerKept={ownerKept} floodPruned={floodPruned}");
            });

            Check("Backups: a week of scheduled backups keeps its own newest copies and never prunes a boot or pre-upgrade one", () =>
            {
                var names = new List<string> { "20260101-000000-boot", "20260102-000000-premigration-config-v6", "20260103-000000-manual" };
                for (int i = 10; i < 30; i++) names.Add($"202603{i:00}-000000-{KmhDataBackup.ScheduledReason}");
                names.Add($"20260331-000000-{KmhDataBackup.ScheduledReason}-a1b2c3");
                List<string> doomed = KmhDataBackup.PruneCandidates(names, 12);
                bool ownerKept = doomed.TrueForAll(KmhDataBackup.IsScheduled);
                bool oldestGo = doomed.Count == 9 && doomed.Contains($"20260310-000000-{KmhDataBackup.ScheduledReason}")
                             && !doomed.Contains($"20260331-000000-{KmhDataBackup.ScheduledReason}-a1b2c3");
                return (ownerKept && oldestGo, $"doomed={doomed.Count} ownerKept={ownerKept} oldestGo={oldestGo}");
            });

            Check("Backups: auto-reset backups are taken at most once per window, server-wide", () =>
            {
                KmhDataBackup.ResetAutoResetCadenceForTest();
                try
                {
                    long now = DateTime.UtcNow.Ticks;
                    bool first = KmhDataBackup.AutoResetBackupDue(now);
                    KmhDataBackup.NoteAutoResetBackup(now);
                    bool soon = KmhDataBackup.AutoResetBackupDue(now + TimeSpan.FromMinutes(1).Ticks);
                    bool later = KmhDataBackup.AutoResetBackupDue(now + KmhDataBackup.AutoResetBackupInterval.Ticks);
                    string src = KmhSecurityAuditSelfTest.ReadSource("Features/PlayerStats/PlayerStatsHandler.cs");
                    // Taken before Begin: a backup holding the open reset would run it again when restored.
                    bool wired = src != null && After(src, "KmhDataBackup.AutoResetBackupDue(", "KmhDataBackup.TryCreate(", "KmhEconomyReset.Begin(username, saveId");
                    return (first && !soon && later && wired, $"first={first} soon={soon} later={later} wired={wired}");
                }
                finally { KmhDataBackup.ResetAutoResetCadenceForTest(); }
            });
        }

        // Shutdown and restart must wait for a request already between its two writes, not only for the scheduler.
        private static void InFlightRequests(Action<string, Func<(bool, string)>> Check)
        {
            Check("Requests: waiting for idle holds while a player request is running and ends when it finishes", () =>
            {
                var started = new System.Threading.ManualResetEventSlim(false);
                var release = new System.Threading.ManualResetEventSlim(false);
                var worker = new System.Threading.Thread(() =>
                {
                    using (SubProtocol.KmhRouter.HoldMutationForTest()) { started.Set(); release.Wait(5000); }
                });
                worker.Start();
                started.Wait(5000);
                bool busy = !SubProtocol.KmhRouter.WaitForIdle(TimeSpan.FromMilliseconds(200));
                release.Set();
                worker.Join(5000);
                bool idle = SubProtocol.KmhRouter.WaitForIdle(TimeSpan.FromSeconds(2));
                // A request that starts a backup of its own must not wait for itself.
                bool selfIgnored;
                using (SubProtocol.KmhRouter.HoldMutationForTest())
                    selfIgnored = SubProtocol.KmhRouter.WaitForIdle(TimeSpan.FromMilliseconds(200));
                return (busy && idle && selfIgnored, $"busy={busy} idle={idle} selfIgnored={selfIgnored}");
            });

            Check("Requests: the router counts a mutation before its last gate check, and shutdown, backup and season reset wait for it", () =>
            {
                string router = KmhSecurityAuditSelfTest.ReadSource("SubProtocol/KmhRouter.cs");
                string shutdown = KmhSecurityAuditSelfTest.ReadSource("Maintenance/KmhShutdown.cs");
                string backup = KmhSecurityAuditSelfTest.ReadSource("Persistence/KmhDataBackup.cs");
                string season = KmhSecurityAuditSelfTest.ReadSource("Maintenance/KmhSeasonReset.cs");
                if (router == null || shutdown == null || backup == null || season == null) return (false, "source not found beside the build");
                int enter = router.IndexOf("EnterMutation();\n", StringComparison.Ordinal);
                if (enter < 0) enter = router.IndexOf("EnterMutation();\r\n", StringComparison.Ordinal);
                int recheck = enter < 0 ? -1 : router.IndexOf("KmhMaintenanceGate.ShouldBlock(env.Kind)", enter, StringComparison.Ordinal);
                int call = router.IndexOf("handler(client, env);", StringComparison.Ordinal);
                bool counted = enter >= 0 && recheck > enter && call > recheck;
                // The shared count covers every ingress, so each barrier waits on it rather than on the router's share.
                bool shutdownWaits = After(shutdown, "Enter(KmhMaintenanceReason.Shutdown)", "KmhValueOps.WaitForIdle", "KmhDataFlush.FlushAll()");
                bool backupWaits = After(backup, "Enter(Maintenance.KmhMaintenanceReason.Backup)", "KmhValueOps.WaitForIdle", "CopyLocked(");
                bool seasonWaits = After(season, "if (BeforeWipeForTest != null && !BeforeWipeForTest())", "KmhValueOps.WaitForIdle", "done = RunLocked(");
                return (counted && shutdownWaits && backupWaits && seasonWaits,
                        $"counted={counted} shutdown={shutdownWaits} backup={backupWaits} season={seasonWaits}");
            });
        }

        private static bool After(string src, string first, string then, string before)
        {
            int a = src.IndexOf(first, StringComparison.Ordinal);
            if (a < 0) return false;
            int b = src.IndexOf(then, a, StringComparison.Ordinal);
            int c = b < 0 ? -1 : src.IndexOf(before, b, StringComparison.Ordinal);
            return b > a && c > b;
        }

        // A crash mid season reset leaves half an economy wiped and the season rolled; only the journal can finish it.
        private static void SeasonResetJournal(Action<string, Func<(bool, string)>> Check)
        {
            Check("Season reset: a crash after the roll is finished at boot without rolling or bumping twice", () =>
            {
                int rolls = 0, clears = 0, vaults = 0;
                KmhSeasonReset.RollForTest = () => { rolls++; return (99, 1); };
                KmhSeasonReset.ClearersForTest = () => new Action[] { () => clears++ };
                KmhSeasonReset.ClearVaultsForTest = () => { vaults++; return 0; };
                KmhSeasonReset.AfterRollForTest = () => throw new InvalidOperationException("selftest crash");
                try
                {
                    long gen0 = Features.Economy.KmhEconomyReset.Generation;
                    try { KmhSeasonReset.Run("selftest", out _); } catch (InvalidOperationException) { }
                    KmhSeasonResetJournal j = KmhSeasonReset.PendingJournal();
                    long gen1 = Features.Economy.KmhEconomyReset.Generation;
                    bool crashedState = j != null && j.SeasonRolled && j.GenerationBumped && gen1 == gen0 + 1
                                     && rolls == 1 && clears == 0 && vaults == 0;

                    KmhSeasonReset.AfterRollForTest = null;
                    KmhSeasonReset.RecoverOnBoot();
                    bool finished = KmhSeasonReset.PendingJournal() == null && rolls == 1 && clears == 1 && vaults == 1
                                 && Features.Economy.KmhEconomyReset.Generation == gen1 && !KmhMaintenanceGate.IsActive;
                    string main = KmhSecurityAuditSelfTest.ReadSource("Main.cs");
                    bool atBoot = main != null && After(main, "Transactions.KmhTransactionRepository.LoadFromDisk();",
                                                        "KmhSeasonReset.RecoverOnBoot();", "Transactions.KmhTransactionRepository.RecoverOnBoot();");
                    return (crashedState && finished && atBoot,
                            $"crashed={crashedState} (journal={(j != null)}, gen {gen0}->{gen1}) finished={finished} rolls={rolls} clears={clears} vaults={vaults} atBoot={atBoot}");
                }
                finally
                {
                    KmhSeasonReset.RollForTest = null; KmhSeasonReset.ClearersForTest = null;
                    KmhSeasonReset.ClearVaultsForTest = null; KmhSeasonReset.AfterRollForTest = null;
                    if (System.IO.File.Exists(KmhDataPaths.SeasonResetJournalFile)) System.IO.File.Delete(KmhDataPaths.SeasonResetJournalFile);
                }
            });

            Check("Season reset: a finished reset whose journal was left behind does not wipe the new season at boot", () =>
            {
                int clears = 0, vaults = 0;
                KmhSeasonReset.RollForTest = () => (99, 1);
                KmhSeasonReset.ClearersForTest = () => new Action[] { () => clears++ };
                KmhSeasonReset.ClearVaultsForTest = () => { vaults++; return 0; };
                try
                {
                    bool ran = KmhSeasonReset.Run("selftest", out string why);
                    // What a delete that failed after the close leaves on disk.
                    JsonFileStore.Save(KmhDataPaths.SeasonResetJournalFile,
                        new KmhSeasonResetJournal { Actor = "selftest", GenerationBumped = true, SeasonRolled = true, Completed = true });
                    KmhSeasonReset.RecoverOnBoot();
                    bool ok = ran && clears == 1 && vaults == 1 && KmhSeasonReset.PendingJournal() == null && !KmhMaintenanceGate.IsActive;
                    return (ok, $"ran={ran} ({why}) clears={clears} vaults={vaults} journal left={KmhSeasonReset.PendingJournal() != null}");
                }
                finally
                {
                    KmhSeasonReset.RollForTest = null; KmhSeasonReset.ClearersForTest = null; KmhSeasonReset.ClearVaultsForTest = null;
                    if (System.IO.File.Exists(KmhDataPaths.SeasonResetJournalFile)) System.IO.File.Delete(KmhDataPaths.SeasonResetJournalFile);
                    KmhSeasonReset.ReleaseHeldForTest();
                }
            });

            Check("Season reset: a wipe its stores could not write keeps the economy paused until the retry finishes it", () =>
            {
                int clears = 0;
                IDisposable diskFull = null;
                KmhSeasonReset.RollForTest = () => (99, 1);
                KmhSeasonReset.ClearersForTest = () => new Action[] { () => clears++ };
                KmhSeasonReset.ClearVaultsForTest = () => 0;
                // After the roll, so the backup and the journal land and only the wiped stores fail to write.
                KmhSeasonReset.AfterRollForTest = () =>
                    diskFull = JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SeasonResetJournalFile ? null : "selftest: disk full");
                try
                {
                    bool ran;
                    try { ran = KmhSeasonReset.Run("selftest", out _); }
                    finally { diskFull?.Dispose(); KmhSeasonReset.AfterRollForTest = null; }
                    bool heldWhileOpen = !ran && clears == 1 && KmhSeasonReset.HeldForTest && KmhSeasonReset.PendingJournal() != null;
                    JsonFileStore.ClearFailureStateForTest();

                    bool resumed = KmhSeasonReset.ResumePending();
                    bool reopened = resumed && !KmhSeasonReset.HeldForTest && !KmhMaintenanceGate.IsActive
                                 && KmhSeasonReset.PendingJournal() == null;
                    return (heldWhileOpen && reopened,
                            $"paused while open={heldWhileOpen}, finished and reopened by the retry={reopened}, wipe passes={clears}");
                }
                finally
                {
                    KmhSeasonReset.RollForTest = null; KmhSeasonReset.ClearersForTest = null; KmhSeasonReset.ClearVaultsForTest = null;
                    KmhSeasonReset.AfterRollForTest = null;
                    diskFull?.Dispose();
                    JsonFileStore.ClearFailureStateForTest();
                    if (System.IO.File.Exists(KmhDataPaths.SeasonResetJournalFile)) System.IO.File.Delete(KmhDataPaths.SeasonResetJournalFile);
                    KmhSeasonReset.ReleaseHeldForTest();
                }
            });

            Check("Season reset: if its journal cannot be written, nothing is wiped", () =>
            {
                int rolls = 0, clears = 0;
                KmhSeasonReset.RollForTest = () => { rolls++; return (99, 1); };
                KmhSeasonReset.ClearersForTest = () => new Action[] { () => clears++ };
                KmhSeasonReset.ClearVaultsForTest = () => 0;
                try
                {
                    long gen0 = Features.Economy.KmhEconomyReset.Generation;
                    bool ran;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SeasonResetJournalFile ? "selftest" : null))
                        ran = KmhSeasonReset.Run("selftest", out string why);
                    bool ok = !ran && rolls == 0 && clears == 0 && Features.Economy.KmhEconomyReset.Generation == gen0
                           && KmhSeasonReset.PendingJournal() == null;
                    return (ok, $"ran={ran} rolls={rolls} clears={clears}");
                }
                finally
                {
                    KmhSeasonReset.RollForTest = null; KmhSeasonReset.ClearersForTest = null; KmhSeasonReset.ClearVaultsForTest = null;
                }
            });
        }

        // One failed write must not hold the server-wide barrier until restart, nor an unsaved purge count as done.
        private static void EconomyResetStall(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Economy reset: a purge that was not written down is not checkpointed, and the retry releases the barrier", () =>
            {
                string u = Fresh("rs");
                try
                {
                    if (!Features.Treasury.TreasuryStore.DepositSilver(u, 500, "selftest seed")) return (false, "seed");
                    if (!Features.Economy.KmhEconomyReset.Begin(u, "selftest-save", out string why)) return (false, "begin: " + why);
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "selftest" : null))
                        Features.Economy.KmhEconomyReset.Run(u, "selftest-save");
                    List<string> done = Features.Economy.KmhEconomyReset.DoneStepsForTest();
                    bool open = Features.Economy.KmhEconomyReset.InProgress && Features.Economy.KmhEconomyReset.StalledForTest;
                    bool held = KmhMaintenanceGate.IsActive;
                    bool notCounted = done.Contains("transactions") && !done.Contains("treasury");
                    bool resumed = Features.Economy.KmhEconomyReset.ResumeStalled();
                    bool finished = !Features.Economy.KmhEconomyReset.InProgress && !KmhMaintenanceGate.IsActive;
                    bool purged = Features.Treasury.TreasuryStore.GetPersonalSilver(u) == 0;
                    string sweep = KmhSecurityAuditSelfTest.ReadSource("Maintenance/ExpirySweeper.cs");
                    bool scheduled = sweep != null && sweep.Contains("KmhEconomyReset.ResumeStalled()");
                    bool ok = open && held && notCounted && resumed && finished && purged && scheduled;
                    return (ok, $"open={open} held={held} done=[{string.Join(",", done)}] resumed={resumed} finished={finished} purged={purged} scheduled={scheduled}");
                }
                finally { Features.Treasury.TreasuryStore.PurgeOwnerForTest(u); }
            });

            Check("Economy reset: the retry never picks up a reset its caller has not started applying", () =>
            {
                string u = Fresh("rp");
                try
                {
                    if (!Features.Economy.KmhEconomyReset.Begin(u, "selftest-save", out string why)) return (false, "begin: " + why);
                    Features.Economy.KmhEconomyReset.MarkStalledForTest();
                    bool resumed = Features.Economy.KmhEconomyReset.ResumeStalled();
                    bool stillPreparing = Features.Economy.KmhEconomyReset.ActivePhase == Features.Economy.KmhResetPhase.Preparing;
                    Features.Economy.KmhEconomyReset.Run(u, "selftest-save");
                    bool finished = !Features.Economy.KmhEconomyReset.InProgress;
                    return (!resumed && stillPreparing && finished, $"resumed={resumed} preparing={stillPreparing} finished={finished}");
                }
                finally { Features.Treasury.TreasuryStore.PurgeOwnerForTest(u); }
            });
        }

        // One modified client must not place a world-visible outpost on any tile id, whoever else is asked.
        private static void FrontierPlacementBounds(Action<string, Func<(bool, string)>> Check)
        {
            Check("Frontier: a tile past any planet is refused, and a lone answer only counts when nobody else was asked", () =>
            {
                long soon = DateTime.UtcNow.AddMinutes(10).Ticks;
                var tok = new Features.Frontier.Dto.PlacementToken { Id = "tb", Nonce = 7, ExpiresUtcTicks = soon };
                bool absurd = !Features.Frontier.FrontierPlacement.ProposalFits(tok, "Ada", "tb", int.MaxValue, "w1", DateTime.UtcNow.Ticks, out string whyFar);
                bool normal = Features.Frontier.FrontierPlacement.ProposalFits(tok, "Ada", "tb", 50000, "w1", DateTime.UtcNow.Ticks, out _);

                tok.Proposals.Add(new Features.Frontier.Dto.PlacementProposal { Username = "Ada", Tile = 42, WorldFingerprint = "w1" });
                bool loneOfThree = Features.Frontier.FrontierPlacement.TryAccept(tok, true, 3, out _, out string whyLone);
                bool loneOfOne = Features.Frontier.FrontierPlacement.TryAccept(tok, true, 1, out int t1, out _) && t1 == 42;
                bool loneUnknown = Features.Frontier.FrontierPlacement.TryAccept(tok, true, -1, out _, out _);
                tok.Proposals.Add(new Features.Frontier.Dto.PlacementProposal { Username = "Bo", Tile = 42, WorldFingerprint = "w1" });
                bool agreed = Features.Frontier.FrontierPlacement.TryAccept(tok, true, 3, out int t2, out _) && t2 == 42;

                var far = new Features.Frontier.Dto.PlacementToken { Id = "tf", ExpiresUtcTicks = soon };
                far.Proposals.Add(new Features.Frontier.Dto.PlacementProposal { Username = "Ada", Tile = int.MaxValue, WorldFingerprint = "w1" });
                bool farAccepted = Features.Frontier.FrontierPlacement.TryAccept(far, true, 1, out _, out _);

                bool ok = absurd && normal && !loneOfThree && loneOfOne && !loneUnknown && agreed && !farAccepted;
                return (ok, $"absurd={absurd} ({whyFar}) normal={normal} lone3={loneOfThree} ({whyLone}) lone1={loneOfOne} loneUnknown={loneUnknown} agreed={agreed} far={farAccepted}");
            });
        }

        // The catalog promises +8% production, storage and road progress, so something must read the multipliers.
        private static void ArchetypePerks(Action<string, Func<(bool, string)>> Check)
        {
            Check("Sites: the archetype perks the catalog shows change production, storage and road progress", () =>
            {
                Features.Sites.Dto.SiteEntry Crew(string arch)
                {
                    var s = new Features.Sites.Dto.SiteEntry { Tile = 1, OwnerUsername = "selftest", Archetype = arch, Stability = 100, ItemDefName = "Steel" };
                    s.Workers.Add("w");
                    s.WorkerProgress["w"] = new Features.Sites.Dto.WorkerProgressDto { PawnLoadId = 1 };
                    s.Buildings.Add(new Features.Sites.Dto.SiteBuilding
                    { Kind = Features.Sites.Dto.SiteBuilding.KindStorage, State = Features.Sites.Dto.SiteBuilding.StateOperational });
                    return s;
                }
                var quarry = Crew(Features.Sites.SiteArchetypes.Quarry);
                var custom = Crew(Features.Sites.SiteArchetypes.Custom);
                var road = Crew(Features.Sites.SiteArchetypes.Roadworks);
                double pq = Features.Sites.SiteStore.OutputFactor(quarry), pc = Features.Sites.SiteStore.OutputFactor(custom);
                int sq = Features.Sites.SiteBuildings.StorageCapacity(quarry), sc = Features.Sites.SiteBuildings.StorageCapacity(custom);
                double rr = Features.Sites.SiteStore.RoadworkPerCycle(road), rc = Features.Sites.SiteStore.RoadworkPerCycle(custom);
                double max = Features.Sites.SiteArchetypeRegistry.MaxPerk;
                bool bounded = Features.Sites.SiteArchetypeRegistry.ClampPerk(5.0) == max
                            && Features.Sites.SiteArchetypeRegistry.ClampPerk(0.5) == 1.0
                            && Features.Sites.SiteArchetypeRegistry.ClampPerk(double.NaN) == 1.0;
                bool ok = pc > 0 && Math.Abs(pq / pc - 1.08) < 1e-9 && sq == 108 && sc == 100
                       && rc > 0 && Math.Abs(rr / rc - 1.08) < 1e-9 && bounded;
                return (ok, $"production {pq:0.000}/{pc:0.000} storage {sq}/{sc} road {rr:0.000}/{rc:0.000} bounded={bounded}");
            });
        }

        // An unreadable .kmh-meta.json must not mint a new identity at schema 0 (regenerating every config), a read
        // exception is not corruption, and the ledger's boot prune must wait for the delivery store to load.
        private static void BootIdentityAndLoads(Action<string, Func<(bool, string)>> Check)
        {
            Check("Boot: an unreadable identity file keeps the identity, regenerates nothing, and stays not-ready across boots", () =>
            {
                string meta = KmhDataPaths.MetaFile;
                byte[] original = System.IO.File.Exists(meta) ? System.IO.File.ReadAllBytes(meta) : null;
                var copiesBefore = new HashSet<string>(System.IO.Directory.GetFiles(KmhDataPaths.Folder, ".kmh-meta.json.corrupt-*"));
                KmhReadyState prevState = KmhReadiness.State;
                string prevId = KmhDataMeta.InstanceId;
                int prevSchema = KmhDataMeta.AppliedConfigSchema;
                string treasury = KmhDataPaths.TreasuryFile;
                bool seededTreasury = !System.IO.File.Exists(treasury);
                try
                {
                    if (seededTreasury) System.IO.File.WriteAllText(treasury, "{}");
                    System.IO.File.WriteAllText(meta, "{ this is not json");
                    KmhDataMeta.ReconcileOnBoot();
                    bool firstKept = KmhDataMeta.IdentityUnreadable && KmhDataMeta.AppliedConfigSchema == KmhVersion.ConfigSchema
                                  && KmhReadiness.State == KmhReadyState.Failed && !System.IO.File.Exists(meta);
                    string firstId = KmhDataMeta.InstanceId;

                    // The next boot finds only the quarantined copy, which must not read as a first run either.
                    KmhReadiness.ResetForTest(KmhReadyState.Starting);
                    KmhDataMeta.ResetIdentityForTest("", 0);
                    KmhDataMeta.ReconcileOnBoot();
                    bool secondKept = KmhDataMeta.IdentityUnreadable && KmhReadiness.State == KmhReadyState.Failed
                                   && KmhDataMeta.AppliedConfigSchema == KmhVersion.ConfigSchema && !System.IO.File.Exists(meta);
                    return (firstKept && secondKept && firstId == KmhDataMeta.InstanceId,
                            $"first={firstKept} second={secondKept} id='{firstId}'/'{KmhDataMeta.InstanceId}' schema={KmhDataMeta.AppliedConfigSchema}");
                }
                finally
                {
                    foreach (string f in System.IO.Directory.GetFiles(KmhDataPaths.Folder, ".kmh-meta.json.corrupt-*"))
                        if (!copiesBefore.Contains(f)) System.IO.File.Delete(f);
                    if (original != null) System.IO.File.WriteAllBytes(meta, original);
                    else if (System.IO.File.Exists(meta)) System.IO.File.Delete(meta);
                    if (seededTreasury && System.IO.File.Exists(treasury)) System.IO.File.Delete(treasury);
                    KmhDataMeta.ResetIdentityForTest(prevId, prevSchema);
                    KmhReadiness.ResetForTest(prevState);
                }
            });

            Check("Boot: a brand-new server starts on the current config schema; unstamped old data still migrates from 0", () =>
            {
                int fresh = KmhDataMeta.StartingConfigSchema(false, false, 0);
                int legacy = KmhDataMeta.StartingConfigSchema(false, true, 0);
                int stamped = KmhDataMeta.StartingConfigSchema(true, true, 3);
                bool ok = fresh == KmhVersion.ConfigSchema && legacy == 0 && stamped == 3;
                return (ok, $"fresh={fresh} legacy={legacy} stamped={stamped} (current {KmhVersion.ConfigSchema})");
            });

            Check("Persistence: a file that cannot be read is left in place and never saved over; a corrupt one is still moved aside", () =>
            {
                string held = System.IO.Path.Combine(KmhDataPaths.Folder, "selftest-held-" + Guid.NewGuid().ToString("N") + ".json");
                string bad = System.IO.Path.Combine(KmhDataPaths.Folder, "selftest-bad-" + Guid.NewGuid().ToString("N") + ".json");
                string marker = System.IO.Path.Combine(KmhDataPaths.Folder, "selftest-damaged-" + Guid.NewGuid().ToString("N") + ".txt");
                JsonFileStore.DamageMarkerOverrideForTest = marker;
                JsonFileStore.IrreplaceableOverrideForTest = p => p == held;
                JsonFileStore.ReadFailureForTest = p => p == held;
                try
                {
                    System.IO.File.WriteAllText(held, "{\"Bans\":[]}");
                    System.IO.File.WriteAllText(bad, "{ not json");
                    // Simulated rather than a real lock: a real lock would also stop a wrong move aside, and hide it.
                    bool loaded = JsonFileStore.TryLoad(held, out Features.Moderation.BanFile _);
                    bool saved = JsonFileStore.Save(held, new Features.Moderation.BanFile());
                    bool inPlace = System.IO.File.Exists(held) && System.IO.File.ReadAllText(held) == "{\"Bans\":[]}";
                    bool noCopy = System.IO.Directory.GetFiles(KmhDataPaths.Folder, System.IO.Path.GetFileName(held) + ".corrupt-*").Length == 0;
                    JsonFileStore.ReadFailureForTest = null;
                    bool savedLater = JsonFileStore.Save(held, new Features.Moderation.BanFile());
                    bool reported = System.IO.File.Exists(marker);
                    bool badLoaded = JsonFileStore.TryLoad(bad, out Features.Moderation.BanFile _);
                    bool badMoved = !System.IO.File.Exists(bad)
                                 && System.IO.Directory.GetFiles(KmhDataPaths.Folder, System.IO.Path.GetFileName(bad) + ".corrupt-*").Length == 1;
                    bool ok = !loaded && !saved && inPlace && noCopy && !savedLater && reported && !badLoaded && badMoved;
                    return (ok, $"loaded={loaded} saved={saved} inPlace={inPlace} noCopy={noCopy} savedLater={savedLater} reported={reported} badMoved={badMoved}");
                }
                finally
                {
                    JsonFileStore.DamageMarkerOverrideForTest = null;
                    JsonFileStore.IrreplaceableOverrideForTest = null;
                    JsonFileStore.ReadFailureForTest = null;
                    foreach (string f in System.IO.Directory.GetFiles(KmhDataPaths.Folder, "selftest-*"))
                        if (f.Contains(System.IO.Path.GetFileNameWithoutExtension(held)) || f.Contains(System.IO.Path.GetFileNameWithoutExtension(bad)) || f == marker)
                            System.IO.File.Delete(f);
                    // The held path's save refusal ends once its file is gone.
                    JsonFileStore.Save(held, new Features.Moderation.BanFile());
                    if (System.IO.File.Exists(held)) System.IO.File.Delete(held);
                }
            });

            Check("Boot: the ledger, which prunes against the delivery store, loads after every other store", () =>
            {
                string src = KmhSecurityAuditSelfTest.ReadSource("Main.cs");
                if (src == null) return (false, "source not found beside the build - cannot check");
                const string Ledger = "Transactions.KmhTransactionRepository.LoadFromDisk();";
                int from = src.IndexOf("Features.Economy.EconomyResetStore.LoadFromDisk();", StringComparison.Ordinal);
                int to = src.IndexOf("Transactions.KmhTransactionRepository.RecoverOnBoot();", StringComparison.Ordinal);
                if (from < 0 || to < from) return (false, "boot block not found");
                string block = src.Substring(from, to - from);
                int ledger = block.IndexOf(Ledger, StringComparison.Ordinal);
                int delivery = block.IndexOf("Features.Delivery.DeliveryStore.LoadFromDisk();", StringComparison.Ordinal);
                int lastLoad = block.LastIndexOf(".LoadFromDisk();", StringComparison.Ordinal);
                bool ok = ledger >= 0 && delivery >= 0 && delivery < ledger && lastLoad == ledger + Ledger.Length - ".LoadFromDisk();".Length;
                return (ok, $"delivery@{delivery} ledger@{ledger} lastLoad@{lastLoad}");
            });
        }

        // Emptying the vault into escrow, opening it free, then recalling the silver would skip the access fee.
        private static void AccessFeeWaiver(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Treasury access: a lease opened free on an empty vault is billed once the vault can pay, and only once", () =>
            {
                var cfg = new Features.Economy.EconomyConfig();
                cfg.Treasury.AccessFeeSilver = 50;
                var prev = Features.Economy.EconomyConfig.ApplyForTest(cfg);
                var prevOut = SubProtocol.KmhRouter.OutboundForTest;
                SubProtocol.KmhRouter.OutboundForTest = (c, k, d) => { };
                string who = Fresh("fee"), poor = Fresh("feep");
                try
                {
                    long now = DateTime.UtcNow.Ticks;
                    var opened = Features.Treasury.TreasuryAccess.Open(who, now, out int charged, out _);
                    if (!Features.Treasury.TreasuryStore.DepositSilver(who, 1000, "selftest recall")) return (false, "seed");
                    bool first = Features.Treasury.TreasuryHandler.HasAccess(null, who);
                    long afterFirst = Features.Treasury.TreasuryStore.GetPersonalSilver(who);
                    bool second = Features.Treasury.TreasuryHandler.HasAccess(null, who);
                    long afterSecond = Features.Treasury.TreasuryStore.GetPersonalSilver(who);

                    // A genuinely empty vault still takes its first deposit without paying first.
                    Features.Treasury.TreasuryAccess.Open(poor, now, out _, out _);
                    bool poorFirst = Features.Treasury.TreasuryHandler.HasAccess(null, poor);
                    if (!Features.Treasury.TreasuryStore.DepositSilver(poor, 100, "selftest first deposit")) return (false, "seed poor");
                    Features.Treasury.TreasuryHandler.HasAccess(null, poor);
                    long poorAfter = Features.Treasury.TreasuryStore.GetPersonalSilver(poor);

                    bool ok = opened == Features.Treasury.TreasuryAccess.Result.Waived && charged == 0
                           && first && afterFirst == 950 && second && afterSecond == 950
                           && poorFirst && poorAfter == 50;
                    return (ok, $"opened={opened} charged={charged} after={afterFirst}/{afterSecond} poor={poorFirst} poorAfter={poorAfter}");
                }
                finally
                {
                    Features.Treasury.TreasuryAccess.Forget(who);
                    Features.Treasury.TreasuryAccess.Forget(poor);
                    Features.Treasury.TreasuryStore.PurgeOwnerForTest(who);
                    Features.Treasury.TreasuryStore.PurgeOwnerForTest(poor);
                    SubProtocol.KmhRouter.OutboundForTest = prevOut;
                    Features.Economy.EconomyConfig.ApplyForTest(prev);
                }
            });
        }

        // Withdrawals must obey the raid block, the access mode and the hall rule, as donations do.
        private static void GuildWithdrawAccess(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            (string owner, string guild) Seed()
            {
                string owner = Fresh("gw");
                string guild = "selftest guild " + Guid.NewGuid().ToString("N").Substring(0, 6);
                if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) throw new InvalidOperationException("guild: " + why);
                if (!Features.Treasury.TreasuryStore.DepositGuildSilver(guild, 1000, owner, "selftest seed")) throw new InvalidOperationException("seed");
                return (owner, guild);
            }
            void Clean(string owner, string guild)
            {
                Features.Guilds.GuildStore.Leave(owner, out _);
                Features.Treasury.TreasuryStore.PurgeOwnerForTest(guild);
                Features.Treasury.TreasuryStore.PurgeOwnerForTest(owner);
            }
            Features.Economy.EconomyConfig Custom(bool blockRaid, bool needHall, string guildAccess = "Remote")
            {
                var c = new Features.Economy.EconomyConfig { EconomyMode = "Custom" };
                c.Custom.BlockDuringRaid = blockRaid;
                c.Custom.GuildAccess = guildAccess;
                c.GuildHall.RequireForTreasury = needHall;
                return c;
            }

            Check("Guild vault: a withdrawal during a raid is refused, as a donation is", () =>
            {
                var prev = Features.Economy.EconomyConfig.ApplyForTest(Custom(blockRaid: true, needHall: false));
                (string owner, string guild) = Seed();
                try
                {
                    var raid = new Features.Economy.EconomyContext(true, true, false, true, false, false, false);
                    var calm = new Features.Economy.EconomyContext(true, true, false, false, false, false, false);
                    bool duringRaid = Features.Guilds.GuildStore.WithdrawFromGuild(owner, 50, out string why, raid);
                    long vaultAfterRaid = Features.Treasury.TreasuryStore.GetGuildSilver(guild);
                    bool afterRaid = Features.Guilds.GuildStore.WithdrawFromGuild(owner, 50, out string why2, calm);
                    bool ok = !duringRaid && vaultAfterRaid == 1000 && afterRaid;
                    return (ok, $"raid={duringRaid} ({why}) vault={vaultAfterRaid} calm={afterRaid} ({why2})");
                }
                finally { Clean(owner, guild); Features.Economy.EconomyConfig.ApplyForTest(prev); }
            });

            Check("Guild vault: with a hall required, a guild without one can neither withdraw nor escrow for Global", () =>
            {
                var prev = Features.Economy.EconomyConfig.ApplyForTest(Custom(blockRaid: false, needHall: true));
                (string owner, string guild) = Seed();
                try
                {
                    bool withdrew = Features.Guilds.GuildStore.WithdrawFromGuild(owner, 50, out string why);
                    bool escrowed = Features.Guilds.GuildStore.EscrowForFederation(owner, 50, "selftest-hall-" + Guid.NewGuid().ToString("N"), out _, out string why2);
                    long vault = Features.Treasury.TreasuryStore.GetGuildSilver(guild);
                    return (!withdrew && !escrowed && vault == 1000, $"withdrew={withdrew} ({why}) escrowed={escrowed} ({why2}) vault={vault}");
                }
                finally { Clean(owner, guild); Features.Economy.EconomyConfig.ApplyForTest(prev); }
            });

            Check("Guild vault: an escrow already taken is still reported taken on retry after the vault is switched off", () =>
            {
                var prev = Features.Economy.EconomyConfig.ApplyForTest(Custom(blockRaid: false, needHall: false));
                (string owner, string guild) = Seed();
                string marker = "selftest-replay-" + Guid.NewGuid().ToString("N");
                try
                {
                    bool first = Features.Guilds.GuildStore.EscrowForFederation(owner, 50, marker, out _, out string why);
                    Features.Economy.EconomyConfig.ApplyForTest(Custom(blockRaid: false, needHall: false, guildAccess: "Disabled"));
                    bool replay = Features.Guilds.GuildStore.EscrowForFederation(owner, 50, marker, out _, out string why2);
                    bool fresh = Features.Guilds.GuildStore.EscrowForFederation(owner, 50, marker + "-new", out _, out _);
                    long vault = Features.Treasury.TreasuryStore.GetGuildSilver(guild);
                    return (first && replay && !fresh && vault == 950, $"first={first} ({why}) replay={replay} ({why2}) fresh={fresh} vault={vault}");
                }
                finally { Clean(owner, guild); Features.Economy.EconomyConfig.ApplyForTest(prev); }
            });
        }

        // A credit past int.MaxValue that is clamped and reported as done destroys the excess.
        private static void VaultCeiling(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Treasury: silver that would pass the vault ceiling is refused whole and held in Recovery", () =>
            {
                string who = Fresh("ceil");
                try
                {
                    if (!Features.Treasury.TreasuryStore.DepositSilver(who, int.MaxValue - 5, "selftest seed")) return (false, "seed");
                    bool delivered = Items.KmhPayloadEscrow.DeliverSilver(who, 100, "selftest payout", "selftest");
                    long balance = Features.Treasury.TreasuryStore.GetPersonalSilver(who);
                    long held = 0;
                    foreach (var rec in Features.Recovery.RecoveryStore.ForUser(who)) if (rec.Kind == "silver") held += rec.Silver;
                    bool ok = !delivered && balance == int.MaxValue - 5 && held == 100;
                    return (ok, $"delivered={delivered} balance={balance} held={held}");
                }
                finally { Features.Treasury.TreasuryStore.PurgeOwnerForTest(who); Features.Recovery.RecoveryStore.ClearUser(who); }
            });

            Check("Treasury: items that would pass the stack ceiling are refused whole and held in Recovery", () =>
            {
                string who = Fresh("ceili");
                try
                {
                    if (!Features.Treasury.TreasuryStore.DepositItem(who, "Steel", int.MaxValue - 1, "selftest seed")) return (false, "seed");
                    bool delivered = Items.KmhPayloadEscrow.DeliverCompact(who, "Steel", 10, "selftest payout", "selftest");
                    var snap = Features.Treasury.TreasuryStore.GetSnapshotFor(who);
                    int count = snap.Items.TryGetValue("Steel", out int n) ? n : 0;
                    int heldUnits = 0;
                    foreach (var rec in Features.Recovery.RecoveryStore.ForUser(who)) if (rec.Payload != null) heldUnits += rec.Payload.StackCount;
                    bool ok = !delivered && count == int.MaxValue - 1 && heldUnits == 10;
                    return (ok, $"delivered={delivered} count={count} held={heldUnits}");
                }
                finally { Features.Treasury.TreasuryStore.PurgeOwnerForTest(who); Features.Recovery.RecoveryStore.ClearUser(who); }
            });
        }

        // A guild site's output the vault refuses must stay owed, not be dropped while the log says it is held.
        private static void GuildSiteProduction(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            // A guild that is gone still gets storage; covered by KmhSiteRestartSelfTest and KmhGuildIdentitySelfTest.
            Check("Sites: guild output the vault cannot write stays owed and reaches the vault exactly once on retry", () =>
            {
                string owner = Fresh("gso");
                string guild = "selftest guild " + Guid.NewGuid().ToString("N").Substring(0, 6);
                int tile = 300000 + new Random().Next(0, 90000);
                try
                {
                    if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "guild: " + why);
                    Features.Sites.SiteStore.InsertSiteForTest(tile, "", 1);
                    var s = Features.Sites.SiteStore.SiteForTest(tile);
                    s.OwnerKind = Features.Sites.Dto.SiteEntry.OwnerGuildKind;
                    s.ControllingGuild = guild;
                    Features.Sites.SiteStore.BindGuildForTest(tile, Features.Guilds.GuildIdentity.Of(guild));
                    int GuildSteel() => Features.Treasury.TreasuryStore.GuildItemForTest(guild, "Steel");

                    int delivered;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.TreasuryFile ? "selftest" : null))
                        delivered = Features.Sites.SiteStore.DeliverGuildOwnedForTest(tile, guild, "Steel", 40, Features.Sites.Dto.SiteEntry.DestTreasury);
                    int held = s.StoredItems != null && s.StoredItems.TryGetValue("Steel", out int h) ? h : 0;
                    int inVault = GuildSteel();
                    (_, int owed) = Features.Sites.SiteStore.UnpaidOwedForTest(guild);
                    Features.Sites.SiteStore.RetryOwed();
                    int inVaultAfter = GuildSteel();
                    Features.Sites.SiteStore.RetryOwed();
                    int inVaultAgain = GuildSteel();
                    bool ok = delivered == 40 && held == 0 && inVault == 0 && owed == 40 && inVaultAfter == 40 && inVaultAgain == 40
                           && Features.Sites.SiteStore.UnpaidOwedForTest(guild) == (0, 0);
                    return (ok, $"delivered={delivered} held={held} vault={inVault} owedWhileRefused={owed} vaultAfterRetry={inVaultAfter} afterSecondRetry={inVaultAgain}");
                }
                finally
                {
                    Features.Sites.SiteStore.RemoveSiteForTest(tile);
                    Features.Treasury.TreasuryStore.PurgeOwnerForTest(guild);
                    Features.Guilds.GuildStore.Leave(owner, out _);
                }
            });
        }

        // Storage credited before a sites save that can fail unnoticed is handed out again after a restart.
        private static void SiteStorageCollect(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Sites: collecting storage the server cannot write down moves nothing, and a later collect moves it once", () =>
            {
                string owner = Fresh("sc");
                int tile = 400000 + new Random().Next(0, 90000);
                Features.Sites.SiteStore.InsertSiteForTest(tile, owner, 1);
                Features.Sites.SiteStore.SiteForTest(tile).StoredItems = new Dictionary<string, int> { ["Steel"] = 300 };
                int Held() { var snap = Features.Treasury.TreasuryStore.GetSnapshotFor(owner); return snap?.Items != null && snap.Items.TryGetValue("Steel", out int n) ? n : 0; }
                try
                {
                    (bool ok, string reason) failed;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.SitesFile ? "selftest" : null))
                        failed = Features.Sites.SiteStore.CollectStorage(owner, tile);
                    var s = Features.Sites.SiteStore.SiteForTest(tile);
                    int left = s.StoredItems.TryGetValue("Steel", out int l) ? l : 0;
                    int creditedOnFailure = Held();
                    var ok = Features.Sites.SiteStore.CollectStorage(owner, tile);
                    int credited = Held();
                    int leftAfter = s.StoredItems.TryGetValue("Steel", out int la) ? la : 0;
                    bool pass = !failed.ok && left == 300 && creditedOnFailure == 0 && ok.ok && credited == 300 && leftAfter == 0;
                    return (pass, $"failed={failed.ok} left={left} creditedOnFailure={creditedOnFailure} then ok={ok.ok} credited={credited} left={leftAfter}");
                }
                finally
                {
                    Features.Sites.SiteStore.RemoveSiteForTest(tile);
                    Features.Treasury.TreasuryStore.PurgeOwnerForTest(owner);
                }
            });
        }

        // A malformed step (negative tile, self-loop) that pricing skips but the build lays is road for free.
        private static void RoadworksMalformedSteps(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            static Features.Roadworks.Dto.RoadTile T(int tile) => new Features.Roadworks.Dto.RoadTile { LayerId = 0, TileId = tile };

            Check("Roadworks: a route with a malformed step is refused before anything is charged", () =>
            {
                Features.Roadworks.RoadworksStore.ClearForNewSeason();
                string owner = Fresh("rw");
                int site = 600000 + new Random().Next(0, 90000);
                Features.Sites.SiteStore.InsertSiteForTest(site, owner, 5, Features.Sites.SiteArchetypes.Roadworks, 3);
                try
                {
                    if (!Features.Treasury.TreasuryStore.DepositSilver(owner, 100000, "selftest seed")) return (false, "seed");
                    long before = Features.Treasury.TreasuryStore.GetPersonalSilver(owner);
                    var route = new List<Features.Roadworks.Dto.RoadTile> { T(site + 5), T(site + 6), T(-1), T(site + 7), T(site + 7) };
                    bool started = Features.Roadworks.RoadworksStore.TryStartProject(owner, site, Features.Roadworks.RoadTiers.Trail,
                        route, out _, out string why);
                    long after = Features.Treasury.TreasuryStore.GetPersonalSilver(owner);
                    return (!started && after == before, $"started={started} charged={before - after} ({why})");
                }
                finally
                {
                    Features.Sites.SiteStore.RemoveSiteForTest(site);
                    Features.Treasury.TreasuryStore.PurgeOwnerForTest(owner);
                    Features.Roadworks.RoadworksStore.ClearForNewSeason();
                }
            });

            Check("Roadworks: a saved project with malformed steps builds only its real segments", () =>
            {
                Features.Roadworks.RoadworksStore.ClearForNewSeason();
                int t = 500000 + new Random().Next(0, 90000);
                long id = 9_000_000_000L + t;
                Features.Roadworks.RoadworksStore.InsertProjectForTest(new Features.Roadworks.Dto.RoadProject
                {
                    Id = id, OwnerUsername = Fresh("rwl"), SiteTile = t, Tier = Features.Roadworks.RoadTiers.Trail,
                    Route = new List<Features.Roadworks.Dto.RoadTile> { T(t), T(t + 1), T(-1), T(t + 2), T(t + 2) },
                    State = Features.Roadworks.Dto.RoadProject.StateBuilding, EscrowSilver = 100000,
                });
                try
                {
                    Features.Roadworks.RoadworksStore.AdvanceProject(id, Features.Roadworks.RoadTiers.WorkPerSegment(Features.Roadworks.RoadTiers.Trail) * 10);
                    var built = Features.Roadworks.RoadworksStore.SegmentsForTest();
                    bool bad = false;
                    foreach (var s in built)
                        if (!s.A.IsValid || !s.B.IsValid || (s.A.LayerId == s.B.LayerId && s.A.TileId == s.B.TileId)) bad = true;
                    return (built.Count == 1 && !bad, $"segments={built.Count} malformed={bad}");
                }
                finally { Features.Roadworks.RoadworksStore.ClearForNewSeason(); }
            });
        }

        // A guild claim comparing rank strings would refuse an admin or moderator while an officer passes.
        private static void GuildOutpostClaim(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Frontier: a guild admin can claim an outpost for the guild; a plain member cannot", () =>
            {
                string owner = Fresh("go"), admin = Fresh("ga"), member = Fresh("gm");
                string guild = "selftest guild " + Guid.NewGuid().ToString("N").Substring(0, 6);
                int tileA = 700000 + new Random().Next(0, 90000), tileM = tileA + 100000;
                try
                {
                    if (!Features.Guilds.GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "guild: " + why);
                    Features.Guilds.GuildStore.AddMember(admin, guild, Features.Guilds.Dto.GuildMemberDto.RankAdmin);
                    Features.Guilds.GuildStore.AddMember(member, guild, Features.Guilds.Dto.GuildMemberDto.RankMember);
                    foreach ((int tile, string who) in new[] { (tileA, admin), (tileM, member) })
                    {
                        Features.Sites.SiteStore.InsertSiteForTest(tile, "", 1);
                        var s = Features.Sites.SiteStore.SiteForTest(tile);
                        s.OutpostTemplate = Features.Sites.Dto.SiteEntry.TemplateRuins;
                        s.OutpostState = Features.Sites.Dto.SiteEntry.OutpostClaimable;
                        s.ClaimEligibleUsername = who;
                        s.ClaimWindowEndsUtcTicks = DateTime.UtcNow.AddHours(1).Ticks;
                    }
                    var byAdmin = Features.Sites.SiteStore.ClaimOutpost(admin, tileA, forGuild: true);
                    var byMember = Features.Sites.SiteStore.ClaimOutpost(member, tileM, forGuild: true);
                    bool ok = byAdmin.ok && !byMember.ok;
                    return (ok, $"admin={byAdmin.ok} ({byAdmin.reason}) member={byMember.ok} ({byMember.reason})");
                }
                finally
                {
                    Features.Sites.SiteStore.RemoveSiteForTest(tileA);
                    Features.Sites.SiteStore.RemoveSiteForTest(tileM);
                    Features.Guilds.GuildStore.Leave(member, out _);
                    Features.Guilds.GuildStore.Leave(admin, out _);
                    Features.Guilds.GuildStore.Leave(owner, out _);
                }
            });
        }

        // A visibility typed in another case must not be stored as is. Claim's own check is in the audit suite.
        private static void QuestVisibilityCase(Action<string, Func<(bool, string)>> Check)
        {
            Check("Visibility: guild-only in any case or spacing is stored as guild-only", () =>
            {
                string a = Features.Guilds.GuildVisibility.Normalize("GUILD_ONLY");
                string b = Features.Guilds.GuildVisibility.Normalize(" Guild_Only ");
                string c = Features.Guilds.GuildVisibility.Normalize("something else");
                bool ok = a == Features.Guilds.GuildVisibility.GuildOnly && b == Features.Guilds.GuildVisibility.GuildOnly
                       && c == Features.Guilds.GuildVisibility.Public;
                return (ok, $"{a} {b} {c}");
            });
        }

        // The in-game editor must not write server-assigned identity fields or executable paths the server later runs.
        private static void ConfigEditorLimits(Action<string, Func<(bool, string)>> Check)
        {
            Check("Config editor: server-assigned fields, paths and dialled addresses are refused from in game", () =>
            {
                var cases = new (string Area, string Key, string Value)[]
                {
                    ("Global", "SigningGeneration", "9"),
                    ("Global", "RegisteredServerId", "x"),
                    ("Global", "BaseUrl", "https://evil.example"),
                    ("Global", "PublicHost", "evil.example"),
                    ("Media", "Video.HelperPath", @"\\host\share\x.exe"),
                    ("Media", "Video.FfmpegPath", @"\\host\share\ff.exe"),
                    ("Discord", "Coordinator.Url", "https://evil.example"),
                };
                // A broken guard writes the file, and the runner's KMH-Data outlives the run, so it is put back.
                var before = new Dictionary<string, byte[]>();
                foreach (var t in KmhConfigValidation.ConfigTargets())
                    if (t.name == "Global" || t.name == "Media" || t.name == "Discord")
                        before[t.path] = System.IO.File.Exists(t.path) ? System.IO.File.ReadAllBytes(t.path) : null;
                var accepted = new List<string>();
                try
                {
                    foreach (var c in cases)
                        if (AdminCommands.KmhConfigApply.Apply(c.Area, new Dictionary<string, string> { [c.Key] = c.Value }, "selftest", out _))
                            accepted.Add(c.Area + "." + c.Key);
                }
                finally
                {
                    foreach (KeyValuePair<string, byte[]> kv in before)
                    {
                        if (kv.Value == null) { if (System.IO.File.Exists(kv.Key)) System.IO.File.Delete(kv.Key); }
                        else if (!System.Linq.Enumerable.SequenceEqual(System.IO.File.ReadAllBytes(kv.Key), kv.Value))
                            System.IO.File.WriteAllBytes(kv.Key, kv.Value);
                    }
                }
                return (accepted.Count == 0, accepted.Count == 0 ? "all refused" : "ACCEPTED: " + string.Join(", ", accepted));
            });

            Check("Config editor: the same fields are shown to the owner as read-only", () =>
            {
                var shown = new List<string>();
                foreach (var area in new[] { "Global", "Media" })
                    foreach (AdminCommands.ConfigFieldSpec f in AdminCommands.KmhConfigSchema.Describe(area))
                        if ((f.Path.EndsWith("HelperPath") || f.Path == "BaseUrl" || f.Path == "SigningGeneration") && !f.ReadOnly)
                            shown.Add(f.Path);
                bool plainStillEditable = !AdminCommands.KmhConfigSchema.IsReadOnly("WebsiteUrl")
                                       && !AdminCommands.KmhConfigSchema.IsReadOnly("MaxConcurrentFetches");
                return (shown.Count == 0 && plainStillEditable, shown.Count == 0 ? $"plain editable={plainStillEditable}" : "editable: " + string.Join(", ", shown));
            });
        }

        // Neither a player nor public Discord may see the host path, economy totals or the operator views.
        private static void StatusDisclosure(Action<string, Func<(bool, string)>> Check)
        {
            Check("Status: a player's view leaves out the data folder and economy internals; the admin view keeps them", () =>
            {
                var player = new System.Text.StringBuilder();
                var admin = new System.Text.StringBuilder();
                AdminCommands.KmhServerCommands.Dispatch(new[] { "status" }, false, "selftest", l => player.AppendLine(l));
                AdminCommands.KmhServerCommands.Dispatch(new[] { "status" }, true, "selftest", l => admin.AppendLine(l));
                string p = player.ToString(), a = admin.ToString();
                bool hidden = !p.Contains(KmhDataPaths.Folder) && !p.Contains("house pool") && !p.Contains("Data folder");
                bool shown = a.Contains(KmhDataPaths.Folder) && a.Contains("house pool");
                return (hidden && shown && p.Contains("Verified clients"), $"hidden={hidden} shown={shown}");
            });

            Check("Status: diag and transport are refused to a player, as their help entry says", () =>
            {
                var diag = new System.Text.StringBuilder();
                var transport = new System.Text.StringBuilder();
                AdminCommands.KmhServerCommands.Dispatch(new[] { "diag" }, false, "selftest", l => diag.AppendLine(l));
                AdminCommands.KmhServerCommands.Dispatch(new[] { "transport" }, false, "selftest", l => transport.AppendLine(l));
                string d = diag.ToString(), t = transport.ToString();
                bool refused = d.Contains("requires admin") && !d.Contains("On-disk")
                            && t.Contains("requires admin") && !t.Contains("Auth:");
                return (refused, $"diag='{d.Trim()}' transport='{t.Trim()}'");
            });

            Check("Discord: a public slash-command reply is redacted like a channel post; a private one is left alone", () =>
            {
                // Assembled, so the release scan for build-machine paths does not read this fixture as one.
                string home = "C:" + @"\Users\owner";
                string raw = "Data folder:      " + home + @"\RWT\KMH-Data";
                var pub = Features.Discord.DiscordSlashCommands.RedactPublic(
                    Kmh.Discord.Wire.KmhDiscordCommandResult.Text(raw, ephemeral: false));
                var priv = Features.Discord.DiscordSlashCommands.RedactPublic(
                    Kmh.Discord.Wire.KmhDiscordCommandResult.Text(raw, ephemeral: true));
                bool ok = !pub.Content.Contains(home) && pub.Content.Contains("KMH-Data") && priv.Content == raw;
                return (ok, pub.Content);
            });
        }

        // A ban or unlink whose save fails must not be reported as done, or it is gone after a restart.
        private static void ModerationDurability(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Bans: a ban that could not be written still holds, says so, and is retried by the flush", () =>
            {
                Features.Moderation.BanStore.ResetForTest();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string who = Fresh("ban");
                bool added, saved;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.BansFile ? "selftest" : null))
                    added = Features.Moderation.BanStore.Add(new Features.Moderation.BanEntry { Username = who, AtUnix = now }, out saved);
                bool holds = Features.Moderation.BanStore.Find(who, "", "", now) != null;
                bool dirty = Features.Moderation.BanStore.DirtyForTest;
                bool flushed = Features.Moderation.BanStore.SaveToDisk() && !Features.Moderation.BanStore.DirtyForTest;
                bool covered = KmhDataFlush.CoveredTypes().Contains(typeof(Features.Moderation.BanStore));
                Features.Moderation.BanStore.Remove(who, out _);
                Features.Moderation.BanStore.ResetForTest();
                return (added && !saved && holds && dirty && flushed && covered,
                        $"added={added} saved={saved} holds={holds} dirty={dirty} flushed={flushed} covered={covered}");
            });

            Check("Bans: lifting a ban that could not be written leaves the ban in place", () =>
            {
                Features.Moderation.BanStore.ResetForTest();
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string who = Fresh("unban");
                Features.Moderation.BanStore.Add(new Features.Moderation.BanEntry { Username = who, AtUnix = now }, out bool first);
                int lifted; bool saved;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.BansFile ? "selftest" : null))
                    lifted = Features.Moderation.BanStore.Remove(who, out saved);
                bool stillBanned = Features.Moderation.BanStore.Find(who, "", "", now) != null;
                Features.Moderation.BanStore.Remove(who, out _);
                Features.Moderation.BanStore.ResetForTest();
                return (first && lifted == 0 && !saved && stillBanned,
                        $"first={first} lifted={lifted} saved={saved} stillBanned={stillBanned}");
            });

            Check("Discord link: an unlink that could not be written takes effect and reports it was not saved", () =>
            {
                string who = Fresh("unlink");
                Features.LinkedAccounts.LinkedAccountsStore.SetLink(who, "disc_" + who, 0, out _);
                bool saved;
                using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.LinkedAccountsFile ? "selftest" : null))
                    saved = Features.LinkedAccounts.LinkedAccountsStore.Unlink(who);
                bool gone = !Features.LinkedAccounts.LinkedAccountsStore.IsLinked(who);
                bool retried = Features.LinkedAccounts.LinkedAccountsStore.SaveToDisk();
                return (!saved && gone && retried, $"saved={saved} gone={gone} retried={retried}");
            });
        }
    }
}
