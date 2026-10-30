using System;
using System.Collections.Generic;
using System.Linq;
using KMHServerAddon.Features.Guilds;
using KMHServerAddon.Features.Moderation;
using KMHServerAddon.Features.Treasury;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Maintenance
{
    // Each check drives the exploit as a player would send it.
    internal static class KmhSecurityAuditSelfTest
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

            GuildNameCollision(Check, Fresh);
            PaddedOwnerName(Check);
            QuestSubmitKinds(Check, Fresh);
            TxnNamespace(Check, Fresh);
            DonationRollback(Check, Fresh);
            WithdrawalCrashWindows(Check, Fresh);
            ApiHelloProof(Check);
            ConsoleAndDiscordGates(Check);
            SharedDiscordChannel(Check);
            GuildRankLimits(Check, Fresh);
            ItemDepositClaims(Check, Fresh);
            ValueLeaks(Check, Fresh);
            WantCrashWindows(Check, Fresh);
            EscrowCrashWindows(Check, Fresh);
            BootOrder(Check);
            TransportLimits(Check, Fresh);
            GuildNameReuse(Check, Fresh);
            LegacyStaleAlliance(Check, Fresh);
            AllianceConsent(Check, Fresh);
            GuildChatIngress(Check, Fresh);
            QuestClaimLocks(Check, Fresh);
            EnforcementSwitchOn(Check, Fresh);
            WashTradeBoost(Check, Fresh);
            GearListingVeto(Check, Fresh);
            WebpFrameBomb(Check);
            CatalogIntake(Check, Fresh);
            DurableStores(Check);
            GearBlobBinding(Check, Fresh);
            WorldDeliveryDurability(Check, Fresh);
            LedgerGrowth(Check, Fresh);
            PersistedBounds(Check, Fresh);
            SaveTickets(Check);
            EconomyContainment(Check, Fresh);
            Check("Connected withdrawal: Global saying the hold was released ends the operation rather than retrying it", () =>
            {
                var closed = new Features.Global.GlobalCallResult { Status = 409, ErrorCode = "KMH-TRS-972" };
                bool personal = Features.Global.Treasury.ConnectedTreasuryService.ProvesNothingLanded(closed, guild: false);
                bool guild = Features.Global.Treasury.ConnectedTreasuryService.ProvesNothingLanded(closed, guild: true);
                return (personal && guild, $"personal={personal} guild={guild}");
            });
            return r;
        }

        private static ServerClient Client(string user)
        {
            var c = new ServerClient(null, null, createListener: false);
            c.GetData<UserFile>(new UserFile { Username = user });
            return c;
        }

        // A tokenless hello matched by address would let anyone behind the same NAT, VPN or tunnel act as that player.
        // Read from source, because the offline runner has no RWT session to drive a real hello against.
        private static void ApiHelloProof(Action<string, Func<(bool, string)>> Check)
        {
            Check("API transport: a hello is admitted on its token alone, never on a name and an address", () =>
            {
                string src = ReadSource(System.IO.Path.Combine("Features", "Transport", "KmhApiServer.cs"));
                if (src == null) return (false, "source not found beside the build - cannot check");
                int start = src.IndexOf("if (_requireAuth)", StringComparison.Ordinal);
                int end = start < 0 ? -1 : src.IndexOf("unauth mode", start, StringComparison.Ordinal);
                if (start < 0 || end < 0) return (false, "auth branch not found");
                string branch = src.Substring(start, end - start);
                bool tokenOnly = branch.Contains("TryConsumeToken(token, out user, out owner)")
                              && !branch.Contains("ResolveClient(") && !branch.Contains("RemoteEndPoint")
                              && !branch.Contains("else if (");
                bool noFallback = !src.Contains("TryAuthBySession") && !src.Contains("IpMatches(");
                return (tokenOnly && noFallback, $"tokenOnly={tokenOnly} fallbackGone={noFallback}");
            });
        }

        // Tier-4 reach by account name, Discord role name or foreign Discord server; owner-link code sent to Admin.
        private static void ConsoleAndDiscordGates(Action<string, Func<(bool, string)>> Check)
        {
            Check("kmh global: an admin account called \"console\" is refused; the real console still runs it", () =>
            {
                var said = new List<string>();
                AdminCommands.KmhServerCommands.Dispatch(new[] { "global", "unlink", "confirm" }, true, "console", said.Add);
                bool refused = said.Exists(s => s.Contains("only be run from the server console"));
                var console = new List<string>();
                AdminCommands.KmhServerCommands.Dispatch(new[] { "global", "nosuch" }, true, "someone", console.Add, fromConsole: true);
                bool runs = console.Contains(AdminCommands.KmhGlobalCommands.UsageForTest);
                return (refused && runs, $"refused={refused} consoleRuns={runs}");
            });

            Check("Discord: a registered secret such as the owner-link code never reaches a Discord post", () =>
            {
                string code = "QZXW-" + Guid.NewGuid().ToString("N").Substring(0, 4).ToUpperInvariant();
                KmhRedact.RegisterSecret(code);
                string line = Features.Discord.DiscordBridge.Redact($"    1. Open https://global.example/link-server?code={code}");
                return (!line.Contains(code), line);
            });

            const ulong OurGuild = 111000000000000001, OtherGuild = 999000000000000009;
            const ulong AdminCh = 222000000000000002, OtherCh = 888000000000000008;
            const string ConsoleRole = "333000000000000003";
            Features.Discord.DiscordConfig Cfg(string guild)
            {
                var c = new Features.Discord.DiscordConfig();
                c.Bot.GuildId = guild;
                c.Channels.Admin = AdminCh.ToString();
                c.Roles.Moderators = new[] { "moderator" };
                c.Roles.ConsoleAccess = new[] { "owner", ConsoleRole };
                c.Console.Allow = true;
                return c;
            }
            Kmh.Discord.Wire.KmhDiscordCommandRequest Req(ulong guild, ulong channel, bool admin, string roleId, string roleName)
            {
                var q = new Kmh.Discord.Wire.KmhDiscordCommandRequest { GuildId = guild, ChannelId = channel, IsGuildAdministrator = admin, Path = "status" };
                if (roleId != null) q.RoleIds.Add(roleId);
                if (roleName != null) q.RoleNames.Add(roleName);
                return q;
            }

            Check("Discord: Administrator of some other Discord server is no moderator here", () =>
            {
                var cfg = Cfg("");
                bool foreign = Features.Discord.DiscordSlashCommands.IsMod(Req(OtherGuild, OtherCh, true, null, null), cfg);
                bool ours = Features.Discord.DiscordSlashCommands.IsMod(Req(OurGuild, AdminCh, true, null, null), cfg);
                var pinned = Cfg(OurGuild.ToString());
                bool wrongGuild = Features.Discord.DiscordSlashCommands.FromAllowedGuild(Req(OtherGuild, AdminCh, true, null, null), pinned);
                bool rightGuild = Features.Discord.DiscordSlashCommands.FromAllowedGuild(Req(OurGuild, AdminCh, true, null, null), pinned);
                return (!foreign && ours && !wrongGuild && rightGuild,
                        $"foreignAdmin={foreign} ours={ours} wrongGuild={wrongGuild} rightGuild={rightGuild}");
            });

            Check("Discord: with Bot.GuildId set, a command from another Discord server is not served at all", () =>
            {
                var had = Features.Discord.DiscordBridge.Config;
                try
                {
                    Features.Discord.DiscordBridge.SetConfigForTest(Cfg(OurGuild.ToString()));
                    var res = Features.Discord.DiscordSlashCommands.ExecuteAsync(Req(OtherGuild, OtherCh, false, null, null)).GetAwaiter().GetResult();
                    return (res.IsError && (res.Content ?? "").Contains("does not serve"), $"error={res.IsError} '{res.Content}'");
                }
                finally { Features.Discord.DiscordBridge.SetConfigForTest(had); }
            });

            Check("Discord: console and moderator roles match by id only - a self-made role called \"owner\" gets nothing", () =>
            {
                var cfg = Cfg(OurGuild.ToString());
                bool byName = Features.Discord.DiscordSlashCommands.RequireConsole(Req(OurGuild, AdminCh, false, "444000000000000004", "Owner"), cfg) == null;
                bool byId = Features.Discord.DiscordSlashCommands.RequireConsole(Req(OurGuild, AdminCh, false, ConsoleRole, "whatever"), cfg) == null;
                bool modByName = Features.Discord.DiscordSlashCommands.IsMod(Req(OurGuild, AdminCh, false, "555000000000000005", "moderator"), cfg);
                var noAdminChannel = Cfg(OurGuild.ToString());
                noAdminChannel.Channels.Admin = "";
                bool anywhere = Features.Discord.DiscordSlashCommands.RequireConsole(Req(OurGuild, OtherCh, false, ConsoleRole, null), noAdminChannel) == null;
                bool wrongChannel = Features.Discord.DiscordSlashCommands.RequireConsole(Req(OurGuild, OtherCh, false, ConsoleRole, null), cfg) == null;
                List<string> named = Features.Discord.DiscordConfig.NamedRoleEntries(cfg.Roles);
                bool warned = named.Count == 2 && named.Contains("owner") && named.Contains("moderator");
                return (!byName && byId && !modByName && !anywhere && !wrongChannel && warned,
                        $"consoleByName={byName} consoleById={byId} modByName={modByName} noAdminChannel={anywhere} wrongChannel={wrongChannel} named={string.Join(",", named)}");
            });

            // An owner reading "no token" after switching the bridge off would believe the file holds no credential.
            Check("Discord: a bridge switched off with a token stored says it is off, not that there is no token", () =>
            {
                var had = Features.Discord.DiscordBridge.Config;
                try
                {
                    var off = Cfg(OurGuild.ToString());
                    off.Enabled = false;
                    off.Bot.Token = "fixture-not-a-token";
                    Features.Discord.DiscordBridge.SetConfigForTest(off);
                    string offSays = Features.Discord.DiscordBridge.DescribeStatus();
                    var empty = Cfg(OurGuild.ToString());
                    Features.Discord.DiscordBridge.SetConfigForTest(empty);
                    string emptySays = Features.Discord.DiscordBridge.DescribeStatus();
                    bool emptyOk = Features.Discord.DiscordTokenSource.FromEnvironment || emptySays.Contains("no Bot.Token");
                    bool ok = offSays.Contains("Enabled is false") && !offSays.Contains("no Bot.Token") && emptyOk;
                    return (ok, $"off: '{offSays}'; on without a token: '{emptySays}'");
                }
                finally { Features.Discord.DiscordBridge.SetConfigForTest(had); }
            });
        }

        // On a shared Discord channel, world chat must leave pictures and Global-off text to KMH chat.
        private static void SharedDiscordChannel(Action<string, Func<(bool, string)>> Check)
        {
            Check("Discord: on a shared channel, world chat takes only what it will carry; the rest stays with KMH chat", () =>
            {
                var cfg = new Features.Discord.DiscordConfig();
                cfg.Channels.Chat = cfg.KmhChat.Channel = cfg.WorldChat.Channel = "777000000000000007";
                ulong ch = 777000000000000007;
                bool picture = Features.Discord.DiscordBridge.WorldCarries(cfg, ch, hasText: false, fromBot: false, relayUp: true);
                bool offline = Features.Discord.DiscordBridge.WorldCarries(cfg, ch, hasText: true, fromBot: false, relayUp: false);
                bool bot = Features.Discord.DiscordBridge.WorldCarries(cfg, ch, hasText: true, fromBot: true, relayUp: true);
                bool text = Features.Discord.DiscordBridge.WorldCarries(cfg, ch, hasText: true, fromBot: false, relayUp: true);
                return (!picture && !offline && !bot && text, $"picture={picture} globalOff={offline} bot={bot} text={text}");
            });
        }

        // An admin must not lift the admin withdraw cap, nor any member cancel or demolish a guild site.
        private static void GuildRankLimits(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            (string guild, string owner, string admin, string officer, string member) Seed()
            {
                string g = "Rank " + Guid.NewGuid().ToString("N").Substring(0, 8);
                string o = Fresh("gown"), a = Fresh("gadm"), f = Fresh("goff"), m = Fresh("gmem");
                if (!GuildStore.CreateGuildAndJoinAsAdmin(o, g, out string why)) throw new Exception("seed: " + why);
                GuildStore.SetOpenJoin(o, true, out _);
                foreach (string u in new[] { a, f, m })
                    if (!GuildStore.JoinGuild(u, g, out why)) throw new Exception("join: " + why);
                GuildStore.Promote(o, f);
                for (int i = 0; i < 3; i++) GuildStore.Promote(o, a);
                if (GuildStore.RankOf(a) != Features.Guilds.Dto.GuildMemberDto.RankAdmin
                 || GuildStore.RankOf(f) != Features.Guilds.Dto.GuildMemberDto.RankOfficer)
                    throw new Exception($"ranks: admin={GuildStore.RankOf(a)} officer={GuildStore.RankOf(f)}");
                return (g, o, a, f, m);
            }
            void Drop((string guild, string owner, string admin, string officer, string member) s)
            {
                foreach (string u in new[] { s.member, s.officer, s.admin, s.owner }) GuildStore.Leave(u, out _);
            }
            int AdminCap(string who) => GuildStore.BuildEnvelopeFor(who).Guild?.Settings?.AdminDailyWithdrawCap ?? int.MinValue;

            Check("Guild settings: an admin cannot lift the admin withdraw cap; the owner can set it", () =>
            {
                var s = Seed();
                try
                {
                    var capped = new Features.Guilds.Dto.GuildSettingsDto { AdminDailyWithdrawCap = 1000 };
                    bool ownerSet = GuildStore.SaveSettings(s.owner, capped) && AdminCap(s.owner) == 1000;
                    var lifted = new Features.Guilds.Dto.GuildSettingsDto { AdminDailyWithdrawCap = -1, MemberDailyWithdrawCap = 50 };
                    GuildStore.SaveSettings(s.admin, lifted);
                    bool held = AdminCap(s.owner) == 1000;
                    bool otherFieldsSaved = GuildStore.BuildEnvelopeFor(s.owner).Guild?.Settings?.MemberDailyWithdrawCap == 50;
                    bool ownerAgain = GuildStore.SaveSettings(s.owner, new Features.Guilds.Dto.GuildSettingsDto { AdminDailyWithdrawCap = 500 })
                                   && AdminCap(s.owner) == 500;
                    // Nor via a lower rank: an unlimited member cap hands the admin's alt what the admin cap withholds.
                    int MemberCap() => GuildStore.BuildEnvelopeFor(s.owner).Guild?.Settings?.MemberDailyWithdrawCap ?? int.MinValue;
                    bool unlimitedRefused = !GuildStore.SaveSettings(s.admin, new Features.Guilds.Dto.GuildSettingsDto { MemberDailyWithdrawCap = -1 }, out _);
                    bool overRefused = !GuildStore.SaveSettings(s.admin, new Features.Guilds.Dto.GuildSettingsDto { ModeratorDailyWithdrawCap = 501 }, out _);
                    bool underSaved = GuildStore.SaveSettings(s.admin, new Features.Guilds.Dto.GuildSettingsDto { MemberDailyWithdrawCap = 400 }) && MemberCap() == 400;
                    bool ownerLifts = GuildStore.SaveSettings(s.owner, new Features.Guilds.Dto.GuildSettingsDto { AdminDailyWithdrawCap = 500, MemberDailyWithdrawCap = -1 })
                                   && MemberCap() == -1;
                    bool lowerRanks = unlimitedRefused && overRefused && underSaved && ownerLifts;
                    return (ownerSet && held && otherFieldsSaved && ownerAgain && lowerRanks,
                            $"ownerSet={ownerSet} capAfterAdmin={AdminCap(s.owner)} memberCapSaved={otherFieldsSaved} ownerAgain={ownerAgain} "
                          + $"lowerRank unlimitedRefused={unlimitedRefused} overRefused={overRefused} underSaved={underSaved} ownerLifts={ownerLifts}");
                }
                finally { Drop(s); }
            });

            Check("Guild sites: a plain member cannot cancel or demolish one; an officer can, but never over stored goods", () =>
            {
                var s = Seed();
                int tile = 7_000_000 + new Random().Next(0, 900_000);
                int mine = tile + 1;
                try
                {
                    Features.Sites.SiteStore.InsertSiteForTest(tile, "", 4);
                    Features.Sites.Dto.SiteEntry site = Features.Sites.SiteStore.SiteForTest(tile);
                    site.OwnerKind = Features.Sites.Dto.SiteEntry.OwnerGuildKind;
                    site.ControllingGuild = s.guild;
                    Features.Sites.SiteStore.BindGuildForTest(tile, Features.Guilds.GuildIdentity.Of(s.guild));
                    site.Buildings = new List<Features.Sites.Dto.SiteBuilding> { new Features.Sites.Dto.SiteBuilding { Kind = Features.Sites.Dto.SiteBuilding.KindStorage } };
                    site.StoredItems = new Dictionary<string, int> { ["Steel"] = 10 };

                    bool memberDemolish = Features.Sites.SiteStore.RemoveBuilding(s.member, tile, 0).ok;
                    bool memberCancel = Features.Sites.SiteStore.Cancel(s.member, tile).ok;
                    bool officerOverGoods = Features.Sites.SiteStore.Cancel(s.officer, tile).ok;
                    bool goodsKept = Features.Sites.SiteStore.SiteForTest(tile)?.StoredItems?["Steel"] == 10;
                    Features.Sites.SiteStore.SiteForTest(tile).StoredItems.Clear();
                    bool memberCancelEmpty = Features.Sites.SiteStore.Cancel(s.member, tile).ok;
                    bool officerDemolish = Features.Sites.SiteStore.RemoveBuilding(s.officer, tile, 0).ok;
                    bool officerCancel = Features.Sites.SiteStore.Cancel(s.officer, tile).ok;

                    Features.Sites.SiteStore.InsertSiteForTest(mine, s.member, 4);
                    bool personal = Features.Sites.SiteStore.Cancel(s.member, mine).ok;

                    bool pass = !memberDemolish && !memberCancel && !memberCancelEmpty && !officerOverGoods && goodsKept && officerDemolish && officerCancel && personal;
                    return (pass, $"memberDemolish={memberDemolish} memberCancel={memberCancel}/{memberCancelEmpty} officerOverGoods={officerOverGoods} "
                                + $"goodsKept={goodsKept} officerDemolish={officerDemolish} officerCancel={officerCancel} ownPersonal={personal}");
                }
                finally
                {
                    Features.Sites.SiteStore.RemoveSiteForTest(tile);
                    Features.Sites.SiteStore.RemoveSiteForTest(mine);
                    Drop(s);
                }
            });
        }

        // Each way a client's deposit count could be trusted: uncapped with a preflight, summed after negatives,
        // parked unchecked, no cooldown.
        private static void ItemDepositClaims(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            long InVault(string who)
            {
                Features.Treasury.Dto.TreasurySnapshot s = TreasuryStore.GetSnapshotFor(who);
                long n = 0;
                foreach (int q in s.Items.Values) n += q;
                foreach (Items.KmhThingPayload p in s.ItemPayloads) n += p.StackCount;
                return n;
            }
            void Send(ServerClient c, object data)
                => Features.Treasury.TreasuryHandler.DepositItemForTest(c, SubProtocol.KmhEnvelope.ParseForTest(data));
            object Stack(string def, int n) => new { def_name = def, stack_count = n, scribe_xml = "" };

            Check("Item deposits: fabricated stack counts are refused, never stored, parked or pended", () =>
            {
                string user = Fresh("idep");
                ServerClient c = Client(user);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                SubProtocol.KmhRouter.OutboundForTest = (cl, k, d) => { };
                try
                {
                    Features.Treasury.TreasuryAccess.Grant(user, DateTime.UtcNow.Ticks);
                    int cap = Features.Economy.EconomyConfig.Current.Treasury.MaxItemDepositQtyPerTx;
                    string token = Features.Treasury.TreasuryHandler.IssuePreflightForTest(c, user, "item", 0, "Plasteel", 1);
                    Send(c, new { item_def_name = "Plasteel", qty = 1, deposit_token = token, payloads = new[] { Stack("Plasteel", 2_000_000_000) } });
                    long afterToken = InVault(user);
                    Send(c, new { item_def_name = "Plasteel", qty = 1, payloads = new[] { Stack("Plasteel", 2_000_000_000), Stack("Plasteel", -1_999_999_999) } });
                    long afterNegative = InVault(user);
                    Send(c, new { item_def_name = "Plasteel", qty = 1, payloads = new[] { Stack("Plasteel", int.MaxValue), Stack("Plasteel", int.MaxValue) } });
                    long afterWrap = InVault(user);
                    var many = new object[Features.Treasury.TreasuryHandler.MaxPayloadsPerDeposit + 1];
                    for (int i = 0; i < many.Length; i++) many[i] = Stack("Steel", 1);
                    Send(c, new { item_def_name = "Steel", qty = many.Length, payloads = many });
                    long afterMany = InVault(user);

                    // With the cooldown armed the gate fails, and the claim must not be parked in Recovery as-is.
                    Features.Economy.EconomyAccess.RecordAction(user, false);
                    Send(c, new { item_def_name = "Steel", qty = cap > 0 ? cap + 1 : int.MaxValue });
                    Send(c, new { item_def_name = "Gun_ChargeRifle", qty = 1 });
                    int held = Features.Recovery.RecoveryStore.HeldCountFor(user);
                    int pending = TreasuryStore.UnconfirmedDepositCount(user);

                    bool pass = afterToken == 0 && afterNegative == 0 && afterWrap == 0 && afterMany == 0 && held == 0 && pending == 0;
                    return (pass, $"vault token={afterToken} negative={afterNegative} wrap={afterWrap} many={afterMany}; held={held} pending={pending}");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    TreasuryStore.ResetPersonal(user);
                    Features.Recovery.RecoveryStore.ClearUser(user);
                }
            });

            // Silver sent as an item would skip the silver deposit fee and MaxPersonalSilver, compact or full-state.
            Check("Item deposits: silver offered as an item, compact or full-state, is refused at preflight and at deposit and lands nowhere", () =>
            {
                string user = Fresh("idep");
                ServerClient c = Client(user);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                SubProtocol.KmhRouter.OutboundForTest = (cl, k, d) => { };
                try
                {
                    Features.Treasury.TreasuryAccess.Grant(user, DateTime.UtcNow.Ticks);
                    bool compactOk = Features.Treasury.TreasuryHandler.ItemPreflightForTest(user, "Silver", 500, false, out string why1, out bool retry1);
                    bool payloadOk = Features.Treasury.TreasuryHandler.ItemPreflightForTest(user, "Silver", 500, true, out string why2, out bool retry2);
                    Send(c, new { item_def_name = "Silver", qty = 500 });
                    long afterCompact = InVault(user);
                    Send(c, new { item_def_name = "Silver", qty = 500, payloads = new[] { Stack("Silver", 500) } });
                    long afterPayload = InVault(user);
                    long silver = TreasuryStore.GetPersonalSilver(user);
                    int held = Features.Recovery.RecoveryStore.HeldCountFor(user);
                    bool preflightRefused = !compactOk && !payloadOk && !retry1 && !retry2
                                            && why1 == Items.KmhItemSafety.SilverAsItemRefusal && why2 == why1;
                    bool pass = preflightRefused && afterCompact == 0 && afterPayload == 0 && silver == 0 && held == 0
                             && TreasuryStore.UnconfirmedDepositCount(user) == 0;
                    return (pass, $"preflightRefused={preflightRefused} (retryAsPayload={retry1}) vaultAfterCompact={afterCompact} "
                                + $"vaultAfterPayload={afterPayload} silver={silver} held={held}");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    TreasuryStore.ResetPersonal(user);
                    Features.Recovery.RecoveryStore.ClearUser(user);
                }
            });

            Check("Item deposits: an honest one still lands, and it arms the deposit cooldown", () =>
            {
                string user = Fresh("idep");
                ServerClient c = Client(user);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                SubProtocol.KmhRouter.OutboundForTest = (cl, k, d) => { };
                try
                {
                    if (Features.Economy.EconomyAccess.Policy.DepositCooldownSec <= 0) return (false, "no deposit cooldown configured - cannot check");
                    Features.Treasury.TreasuryAccess.Grant(user, DateTime.UtcNow.Ticks);
                    Send(c, new { item_def_name = "Steel", qty = 10 });
                    long stored = InVault(user);
                    bool cooling = Features.Economy.EconomyAccess.OnCooldown(user, false, out int left);
                    return (stored == 10 && cooling, $"stored={stored} cooldownArmed={cooling} ({left}s)");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    TreasuryStore.ResetPersonal(user);
                    Features.Recovery.RecoveryStore.ClearUser(user);
                }
            });
        }

        // A pushed label could turn Silver into a site output; escrow for a segment someone else built could vanish;
        // an unpaid world quest could burn its house-pool reserve.
        private static void ValueLeaks(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Item labels: the first label seen for a def is kept, and Silver is never a site output", () =>
            {
                string def = "KmhSelftestDef" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Features.ItemLabels.ItemLabelCache.Apply(new Dictionary<string, string> { [def] = "honest thing" });
                Features.ItemLabels.ItemLabelCache.Apply(new Dictionary<string, string> { [def] = "wood" });
                string label = Features.ItemLabels.ItemLabelCache.LabelFor(def);
                bool silverAsWood = Features.Sites.SiteOutputRules.Classify("Silver", "wood").IsAllowed;
                bool silverAsItself = Features.Sites.SiteOutputRules.Classify("Silver", "silver").IsAllowed;
                bool steel = Features.Sites.SiteOutputRules.Classify("Steel", "steel").IsAllowed;
                return (label == "honest thing" && !silverAsWood && !silverAsItself && steel,
                        $"label='{label}' silverAsWood={silverAsWood} silver={silverAsItself} steelStillAllowed={steel}");
            });

            // The cached label is whichever language (or modified client) reached the server first.
            Check("Site rules: built-in tiers and markers read the defName, never the label; owner lists match a label but cannot unlock a complex item by one", () =>
            {
                Features.Sites.SitesConfig sc = Features.Sites.SitesConfig.Current;
                Features.Sites.SiteOutput had = sc.Output;
                try
                {
                    Features.Sites.SiteOutputTier[] tiers = Features.Sites.SiteOutputTier.Defaults();
                    tiers[0].AllowedDefNames = new[] { "kmh selftest friendly", "Gun_KmhSelftestOwned" };
                    tiers[1].BlockedDefNames = new[] { "kmh selftest banned" };
                    sc.Output = new Features.Sites.SiteOutput { AllowExplicitComplex = true, Tiers = tiers };
                    Features.Sites.SiteOutputClass C(string def, string label) => Features.Sites.SiteOutputRules.Classify(def, label);
                    int tierByLabel = C("RawKmhSelftest", "plasteel").TierNumber;
                    bool markerByLabel = !C("RawKmhSelftest", "bionic relic").IsAllowed;
                    bool markerByDef = !C("Meat_Thrumbo", "carne").IsAllowed;
                    bool ownerBlockByLabel = !C("RawKmhSelftest", "kmh selftest banned").IsAllowed;
                    bool complexByLabel = C("Gun_KmhSelftest", "kmh selftest friendly").IsAllowed;
                    bool complexByDef = C("Gun_KmhSelftestOwned", "anything").IsAllowed;
                    bool ok = tierByLabel == 1 && !markerByLabel && markerByDef && ownerBlockByLabel && !complexByLabel && complexByDef;
                    return (ok, $"tierWithPlasteelLabel={tierByLabel} blockedByLabelMarker={markerByLabel} thrumboBlockedByDef={markerByDef} "
                              + $"ownerBlockByLabel={ownerBlockByLabel} complexUnlockedByLabel={complexByLabel} complexUnlockedByDef={complexByDef}");
                }
                finally { sc.Output = had; }
            });

            Check("Roadworks: escrow for a segment someone else built first goes back when the project finishes, once", () =>
            {
                string owner = Fresh("road");
                string tier = Features.Roadworks.RoadTiers.Trail;
                int per = Features.Roadworks.RoadTiers.SilverPerSegment(tier);
                if (per <= 0) return (false, "trail segments cost nothing in this config - cannot check");
                int t0 = 8_000_000 + new Random().Next(0, 900_000);
                var a = new Features.Roadworks.Dto.RoadTile { TileId = t0 };
                var b = new Features.Roadworks.Dto.RoadTile { TileId = t0 + 1 };
                var c = new Features.Roadworks.Dto.RoadTile { TileId = t0 + 2 };
                long id = 9_000_000_000 + t0;
                var built = new Features.Roadworks.Dto.RoadSegment { A = a, B = b, Tier = tier, OwnerUsername = "someone else" };
                try
                {
                    Features.Roadworks.RoadworksStore.InsertSegmentForTest(built);
                    Features.Roadworks.RoadworksStore.InsertProjectForTest(new Features.Roadworks.Dto.RoadProject
                    {
                        Id = id, OwnerUsername = owner, Tier = tier, Route = new List<Features.Roadworks.Dto.RoadTile> { a, b, c },
                        EscrowSilver = 2 * per, EscrowSilverSpent = 0,
                    });
                    int laid = Features.Roadworks.RoadworksStore.AdvanceProject(id, 1000);
                    var p = Features.Roadworks.RoadworksStore.ProjectForTest(id);
                    long refunded = TreasuryStore.GetPersonalSilver(owner);
                    long again = Features.Roadworks.RoadworksStore.RefundFinished();
                    long after = TreasuryStore.GetPersonalSilver(owner);
                    bool pass = laid == 1 && p?.State == Features.Roadworks.Dto.RoadProject.StateComplete
                             && refunded == per && again == 0 && after == per && p.EscrowSilverSpent == p.EscrowSilver;
                    return (pass, $"laid={laid} state={p?.State} refunded={refunded} (want {per}) second={again} after={after}");
                }
                finally
                {
                    Features.Roadworks.RoadworksStore.RemoveForTest(id, new[] { Features.Roadworks.RoadKeys.For(a, b), Features.Roadworks.RoadKeys.For(b, c) });
                    TreasuryStore.ResetPersonal(owner);
                }
            });

            Check("World quests: an unpaid self-reported completion returns its reserve, and none can be funded to begin with", () =>
            {
                string who = Fresh("wq");
                Features.World.WorldConfig saved = Features.World.WorldConfig.Current;
                try
                {
                    Features.World.WorldConfig.ApplyForTest(new Features.World.WorldConfig
                    { WorldQuests = new Features.World.WorldQuestSettings { PayForSelfReported = false } });
                    var made = Features.World.WorldEngine.CreateWorldQuest(Features.World.Dto.ServerQuestDto.KindCooperative,
                        Features.World.Dto.ServerQuestDto.ObjHunt, "Muffalo", 10, 5000, 60, "", "", "selftest");
                    bool refusedFunding = !made.ok && (made.reason ?? "").Contains("PayForSelfReported");

                    long poolBefore = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                    var q = Features.World.WorldStore.CreateQuest(Features.World.Dto.ServerQuestDto.KindCooperative,
                        Features.World.Dto.ServerQuestDto.ObjHunt, "Muffalo", "selftest hunt", "probe", 10, 700, 60, reservedFromPool: 700);
                    Features.World.WorldEngine.ApplyContribution(who, q.Id, 10);
                    long returned = Features.Marketplace.MarketplaceStore.HousePoolBalance() - poolBefore;
                    long twice = Features.World.WorldStore.TakeUnpaidReserve(q.Id);
                    long paid = TreasuryStore.GetPersonalSilver(who);
                    if (returned > 0) Features.Marketplace.MarketplaceStore.TryDebitHousePool(returned, "selftest reserve undo");
                    bool pass = refusedFunding && returned == 700 && twice == 0 && paid == 0;
                    return (pass, $"fundingRefused={refusedFunding} ('{made.reason}') poolReturned={returned} second={twice} paid={paid}");
                }
                finally
                {
                    Features.World.WorldConfig.ApplyForTest(saved);
                    TreasuryStore.ResetPersonal(who);
                }
            });
        }

        // A want post must not debit before anything names it, nor a fill take goods with no marker or pay on no leg.
        // Each window is crashed into, then boot recovery runs for real.
        private static void WantCrashWindows(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            const int Qty = 5, Unit = 100;
            int SteelOf(string who, bool payload)
            {
                Features.Treasury.Dto.TreasurySnapshot s = TreasuryStore.GetSnapshotFor(who);
                int n = 0;
                if (payload) { foreach (Items.KmhThingPayload p in s.ItemPayloads) if (p.DefName == "Steel") n += p.StackCount; }
                else foreach (KeyValuePair<string, int> kv in s.Items) if (Util.ItemKey.Matches(kv.Key, "Steel", "", 0)) n += kv.Value;
                return n;
            }
            (long filled, long escrow, bool open) WantState(long id)
            {
                foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin())
                    if (w.Id == id) return (w.QtyFilled, w.EscrowRemaining, true);
                return (0, 0, false);
            }
            int OpenRows(string a, string b)
            {
                int n = 0;
                foreach (Transactions.KmhTransaction t in Transactions.KmhTransactionRepository.Pending())
                    if (t.Player == a || t.Player == b || t.CounterPlayer == a || t.CounterPlayer == b) n++;
                return n;
            }

            foreach (bool payload in new[] { false, true })
            foreach (string phase in new[] { "taken", "legs-written", "fill-recorded", "goods-delivered", "taxes-taken" })
            {
                if (payload && phase == "taken") continue;
                Check($"Want fill: a crash at '{phase}' ({(payload ? "full-state" : "compact")}) recovers to one whole outcome, nothing lost or doubled", () =>
                {
                    string buyer = Fresh("wfb"), seller = Fresh("wfs");
                    try
                    {
                        if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "could not seed");
                        bool seeded = payload
                            ? TreasuryStore.DepositPayload(seller, new Items.KmhThingPayload { DefName = "Steel", StackCount = Qty, ScribeXml = "<thing><def>Steel</def></thing>" })
                            : TreasuryStore.DepositItem(seller, "Steel", Qty, "selftest seed");
                        if (!seeded) return (false, "could not seed the seller");
                        (long id, string why) = Features.WantBoard.WantStore.Post(buyer, "Steel", Qty, Unit, 1, "public", 0, "", allowComplex: payload);
                        if (id == 0) return (false, "post: " + why);

                        long house0 = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                        long silver0 = TreasuryStore.GetPersonalSilver(buyer) + TreasuryStore.GetPersonalSilver(seller) + house0 + WantState(id).escrow;
                        int items0 = SteelOf(buyer, payload) + SteelOf(seller, payload);

                        Features.WantBoard.WantStore.AtFulfillPhaseForTest = p => { if (p == phase) throw new OperationCanceledException("died at " + p); };
                        try { Features.WantBoard.WantStore.Fulfill(seller, id, Qty); }
                        catch (OperationCanceledException) { }
                        finally { Features.WantBoard.WantStore.AtFulfillPhaseForTest = null; }
                        Transactions.KmhTransactionRepository.RecoverOnBoot();
                        Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();

                        var (filled, escrow, open) = WantState(id);
                        long silver1 = TreasuryStore.GetPersonalSilver(buyer) + TreasuryStore.GetPersonalSilver(seller)
                                     + Features.Marketplace.MarketplaceStore.HousePoolBalance() + escrow;
                        int buyerSteel = SteelOf(buyer, payload), sellerSteel = SteelOf(seller, payload);
                        int rows = OpenRows(buyer, seller);
                        int takes = TreasuryStore.PendingTakesFor(seller).Count;
                        bool recorded = phase != "taken" && phase != "legs-written";
                        bool outcome = recorded
                            ? buyerSteel == Qty && sellerSteel == 0 && !open && TreasuryStore.GetPersonalSilver(seller) > 0
                            : sellerSteel == Qty && buyerSteel == 0 && open && filled == 0;
                        bool conserved = silver1 == silver0 && buyerSteel + sellerSteel == items0;
                        return (outcome && conserved && rows == 0 && takes == 0,
                                $"silver {silver0}->{silver1} steel buyer={buyerSteel} seller={sellerSteel} want open={open} filled={filled} "
                              + $"sellerSilver={TreasuryStore.GetPersonalSilver(seller)} openRows={rows} pendingTakes={takes}");
                    }
                    finally
                    {
                        Features.WantBoard.WantStore.AtFulfillPhaseForTest = null;
                        Features.WantBoard.WantStore.PurgeBuyer(buyer);
                        TreasuryStore.PurgeOwnerForTest(buyer);
                        TreasuryStore.PurgeOwnerForTest(seller);
                    }
                });
            }

            foreach (string phase in new[] { "debited", "saved" })
            {
                Check($"Want post: a crash at '{phase}' leaves the escrow in the vault or in the want, never neither or both", () =>
                {
                    string buyer = Fresh("wpb");
                    try
                    {
                        if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "could not seed");
                        long id = 0;
                        Features.WantBoard.WantStore.AtPostPhaseForTest = p => { if (p == phase) throw new OperationCanceledException("died at " + p); };
                        try { id = Features.WantBoard.WantStore.Post(buyer, "Steel", Qty, Unit, 1, "public", 0, "").id; }
                        catch (OperationCanceledException) { }
                        finally { Features.WantBoard.WantStore.AtPostPhaseForTest = null; }
                        Transactions.KmhTransactionRepository.RecoverOnBoot();

                        long vault = TreasuryStore.GetPersonalSilver(buyer);
                        long inWants = 0;
                        foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin())
                            if (w.BuyerUsername == buyer) inWants += w.EscrowRemaining;
                        int rows = OpenRows(buyer, buyer);
                        return (vault + inWants == 5000 && rows == 0, $"vault={vault} inWants={inWants} openRows={rows}");
                    }
                    finally
                    {
                        Features.WantBoard.WantStore.AtPostPhaseForTest = null;
                        Features.WantBoard.WantStore.PurgeBuyer(buyer);
                        TreasuryStore.PurgeOwnerForTest(buyer);
                    }
                });
            }
        }

        // An auction bid, attachment or bounty that leaves the vault before anything names it is lost on a crash.
        private static void EscrowCrashWindows(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            int OpenRows(params string[] who)
            {
                int n = 0;
                foreach (Transactions.KmhTransaction t in Transactions.KmhTransactionRepository.Pending())
                    foreach (string w in who) if (t.Player == w || t.CounterPlayer == w) { n++; break; }
                return n;
            }
            void Recover()
            {
                Transactions.KmhTransactionRepository.RecoverOnBoot();
                Transactions.KmhPayloadTakeRecovery.RecoverOnBoot();
            }
            T Crash<T>(Action<Action<string>> arm, string phase, Func<T> act)
            {
                arm(p => { if (p == phase) throw new OperationCanceledException("died at " + p); });
                try { return act(); }
                catch (OperationCanceledException) { return default(T); }
                finally { arm(null); }
            }

            foreach (string phase in new[] { "bid-debited", "bid-saved" })
                Check($"Auction bid: a crash at '{phase}' leaves the bid in the vault or standing on the auction, never neither or both", () =>
                {
                    string seller = Fresh("abs"), bidder = Fresh("abb");
                    long auction = 0;
                    try
                    {
                        if (!TreasuryStore.DepositItem(seller, "Steel", 3, "selftest seed")) return (false, "could not seed");
                        if (!TreasuryStore.DepositSilver(bidder, 5000, "selftest seed")) return (false, "could not seed");
                        auction = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 3, 100, 10, 0, 24, "public").id;
                        if (auction == 0) return (false, "could not post an auction");
                        Crash<Features.Auctions.AuctionStore.BidResult>(h => Features.Auctions.AuctionStore.AtValuePhaseForTest = h, phase,
                            () => Features.Auctions.AuctionStore.Bid(bidder, auction, 400));
                        Recover();
                        long vault = TreasuryStore.GetPersonalSilver(bidder);
                        bool leads = Features.Auctions.AuctionStore.HighBidderIs(auction, bidder);
                        bool pass = leads ? vault == 4600 : vault == 5000;
                        return (pass && OpenRows(bidder) == 0, $"vault={vault} leads={leads} openRows={OpenRows(bidder)}");
                    }
                    finally
                    {
                        Features.Auctions.AuctionStore.AtValuePhaseForTest = null;
                        Features.Auctions.AuctionStore.PurgeUser(seller);
                        TreasuryStore.PurgeOwnerForTest(seller);
                        TreasuryStore.PurgeOwnerForTest(bidder);
                    }
                });

            Check("Auction bid: a lead that moved after the outbid was recorded refuses the bid, so the row never names the wrong player", () =>
            {
                string seller = Fresh("abs"), a = Fresh("aba"), b = Fresh("abb"), c = Fresh("abc");
                try
                {
                    if (!TreasuryStore.DepositItem(seller, "Steel", 3, "selftest seed")) return (false, "could not seed");
                    foreach (string u in new[] { a, b, c }) TreasuryStore.DepositSilver(u, 5000, "selftest seed");
                    long auction = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 3, 100, 10, 0, 24, "public").id;
                    Features.Auctions.AuctionStore.Bid(a, auction, 100);
                    bool fired = false;
                    Features.Auctions.AuctionStore.AtValuePhaseForTest = p =>
                    {
                        if (p != "outbid-recorded" || fired) return;
                        fired = true;
                        Features.Auctions.AuctionStore.Bid(c, auction, 150);
                    };
                    Features.Auctions.AuctionStore.BidResult rb;
                    try { rb = Features.Auctions.AuctionStore.Bid(b, auction, 500); }
                    finally { Features.Auctions.AuctionStore.AtValuePhaseForTest = null; }
                    long va = TreasuryStore.GetPersonalSilver(a), vb = TreasuryStore.GetPersonalSilver(b), vc = TreasuryStore.GetPersonalSilver(c);
                    bool pass = fired && !rb.Ok && Features.Auctions.AuctionStore.HighBidderIs(auction, c)
                             && va == 5000 && vb == 5000 && vc == 4850 && OpenRows(a, b, c) == 0;
                    return (pass, $"raceRan={fired} bOk={rb.Ok} a={va} b={vb} c={vc} cLeads={Features.Auctions.AuctionStore.HighBidderIs(auction, c)} openRows={OpenRows(a, b, c)}");
                }
                finally
                {
                    Features.Auctions.AuctionStore.AtValuePhaseForTest = null;
                    Features.Auctions.AuctionStore.PurgeUser(seller);
                    foreach (string u in new[] { seller, a, b, c }) TreasuryStore.PurgeOwnerForTest(u);
                }
            });

            foreach (string phase in new[] { "gear-named", "escrowed", "saved" })
                Check($"Mail send: a crash at '{phase}' leaves silver, items and gear with the sender or in the message, never neither or both", () =>
                {
                    string from = Fresh("mls"), to = Fresh("mlr");
                    try
                    {
                        if (!TreasuryStore.DepositSilver(from, 5000, "selftest seed")) return (false, "could not seed");
                        if (!TreasuryStore.DepositItem(from, "Steel", 10, "selftest seed")) return (false, "could not seed");
                        if (!TreasuryStore.DepositPayload(from, new Items.KmhThingPayload { DefName = "Gun_Revolver", StackCount = 1, ScribeXml = "<thing/>" })) return (false, "could not seed");
                        List<Items.KmhThingPayload> held = TreasuryStore.StoredPayloadsForTest(from);
                        if (held.Count != 1) return (false, "could not seed gear");
                        long fee = Features.Mail.MailConfig.Current.FeeFor(300, true);
                        var before = Features.Mail.MailStore.EscrowedTotalsForTest();
                        Crash<long>(h => Features.Mail.MailStore.AtSendPhaseForTest = h, phase, () =>
                            Features.Mail.MailStore.Send(from, to, "s", "b", 300, new Dictionary<string, int> { ["Steel"] = 4 },
                                new Dictionary<string, int> { [held[0].Fingerprint] = 1 }, out _, out _, out _));
                        Recover();
                        var after = Features.Mail.MailStore.EscrowedTotalsForTest();
                        long vault = TreasuryStore.GetPersonalSilver(from);
                        int steel = TreasuryStore.GetSnapshotFor(from).Items.TryGetValue("Steel", out int s) ? s : 0;
                        int gear = TreasuryStore.StoredPayloadsForTest(from).Count;
                        long inMail = after.Silver - before.Silver; int unitsInMail = after.Items - before.Items;
                        bool sent = inMail > 0;
                        bool pass = sent ? vault == 5000 - 300 - fee && steel == 6 && gear == 0 && inMail == 300 && unitsInMail == 5
                                         : vault == 5000 && steel == 10 && gear == 1 && inMail == 0 && unitsInMail == 0;
                        int takes = TreasuryStore.PendingTakesFor(from).Count;
                        return (pass && OpenRows(from) == 0 && takes == 0,
                                $"sent={sent} vault={vault} (fee {fee}) steel={steel} gear={gear} inMail={inMail}/{unitsInMail} openRows={OpenRows(from)} pendingTakes={takes}");
                    }
                    finally
                    {
                        Features.Mail.MailStore.AtSendPhaseForTest = null;
                        Features.Mail.MailStore.PurgeSender(from);
                        TreasuryStore.PurgeOwnerForTest(from);
                        TreasuryStore.PurgeOwnerForTest(to);
                    }
                });

            foreach (string phase in new[] { "debited", "saved" })
                Check($"Quest post: a crash at '{phase}' leaves the bounty in the vault or on the quest, never neither or both", () =>
                {
                    string poster = Fresh("qps");
                    try
                    {
                        if (!TreasuryStore.DepositSilver(poster, 5000, "selftest seed")) return (false, "could not seed");
                        Crash<long>(h => Features.Quests.QuestStore.AtPostPhaseForTest = h, phase, () =>
                            Features.Quests.QuestStore.Post(poster, Features.Quests.Dto.QuestEntry.KindDeliverItem, "public",
                                "selftest bounty", "Bring steel for the selftest please", 700, "Steel", 5, 24));
                        Recover();
                        long vault = TreasuryStore.GetPersonalSilver(poster);
                        int open = Features.Quests.QuestStore.OpenCountForTest(poster);
                        bool pass = open == 1 ? vault == 4300 : open == 0 && vault == 5000;
                        return (pass && OpenRows(poster) == 0, $"vault={vault} openQuests={open} openRows={OpenRows(poster)}");
                    }
                    finally
                    {
                        Features.Quests.QuestStore.AtPostPhaseForTest = null;
                        Features.Quests.QuestStore.PurgePoster(poster);
                        TreasuryStore.PurgeOwnerForTest(poster);
                    }
                });
        }

        // Transport limits: a peer that stops reading, unbounded op ids, identity-less traffic, unmetered fragments.
        private static void TransportLimits(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Op ids: a 60,000-character id is stored as a short key and still deduplicates", () =>
            {
                string huge = new string('x', 60_000) + Guid.NewGuid().ToString("N");
                string key = Security.KmhOpGuard.KeyOf("selftest.scope", "someone", huge);
                string other = Security.KmhOpGuard.KeyOf("selftest.scope", "someone", huge + "y");
                bool first = Security.KmhOpGuard.TryBegin("selftest.scope", "someone", huge);
                bool again = Security.KmhOpGuard.TryBegin("selftest.scope", "someone", huge);
                bool distinct = key != other;
                Security.KmhOpGuard.Release("selftest.scope", "someone", huge);
                return (key.Length < 200 && first && !again && distinct, $"keyLength={key.Length} first={first} repeat={again} distinct={distinct}");
            });

            Check("Router: a connection with no account is metered as refused, not waved through", () =>
            {
                var anon = new ServerClient(null, null, createListener: false);
                var named = Client(Fresh("rt"));
                bool anonIn = SubProtocol.KmhRouter.AllowInboundForTest(anon, "kmh.chat.send");
                bool anonHs = SubProtocol.KmhRouter.AllowHandshakeForTest(anon);
                bool anonFrag = SubProtocol.KmhRouter.AllowFragmentForTest(anon);
                bool namedIn = SubProtocol.KmhRouter.AllowInboundForTest(named, "kmh.chat.send");
                string src = ReadSource(System.IO.Path.Combine("SubProtocol", "Patches", "Patch_PM_Chat_KmhIntercept.cs"));
                int guard = src?.IndexOf("GetData<UserFile>()?.Username", StringComparison.Ordinal) ?? -1;
                int route = src?.IndexOf("KmhRouter.HandleInbound(", StringComparison.Ordinal) ?? -1;
                bool intercept = guard > 0 && route > guard;
                return (!anonIn && !anonHs && !anonFrag && namedIn && intercept,
                        $"anon inbound={anonIn} handshake={anonHs} fragment={anonFrag}; named inbound={namedIn}; intercept guards first={intercept}");
            });

            Check("Fragments: each chunk is metered per player, and an abandoned transfer is swept without a further chunk", () =>
            {
                var named = Client(Fresh("fr"));
                int allowed = 0;
                for (int i = 0; i < SubProtocol.KmhRouter.MaxFragmentsPerWindow + 5; i++)
                    if (SubProtocol.KmhRouter.AllowFragmentForTest(named)) allowed++;

                var asm = new SubProtocol.KmhFragments.Assembler();
                string body = new SubProtocol.KmhEnvelope("kmh.selftest.big", new { pad = new string('p', 80_000) }).Serialize();
                List<SubProtocol.KmhEnvelope> parts = SubProtocol.KmhFragments.Split("kmh.selftest.big", body, 16 * 1024);
                asm.Accept(SubProtocol.KmhEnvelope.TryParse(parts[0].Serialize()), out string why);
                int open = asm.OpenCount;
                SubProtocol.KmhFragments.Assembler.AgeForTest = TimeSpan.FromMinutes(5);
                int swept;
                try { swept = asm.PruneExpired(); }
                finally { SubProtocol.KmhFragments.Assembler.AgeForTest = TimeSpan.Zero; }

                var gone = new ServerClient(null, null, createListener: false);
                SubProtocol.KmhRouter.AddAssemblerForTest(gone);
                int before = SubProtocol.KmhRouter.AssemblerCountForTest;
                Maintenance.ExpirySweeper.TickForTest(DateTime.UtcNow.Ticks);
                int after = SubProtocol.KmhRouter.AssemblerCountForTest;

                bool pass = allowed == SubProtocol.KmhRouter.MaxFragmentsPerWindow && parts.Count > 1 && open == 1
                         && swept == 1 && asm.OpenCount == 0 && after < before;
                return (pass, $"allowed={allowed}/{SubProtocol.KmhRouter.MaxFragmentsPerWindow + 5} parts={parts.Count} open={open} swept={swept} assemblers {before}->{after} {why}");
            });

            Check("API transport: a peer that stops reading times the write out and cannot hold other senders", () =>
            {
                string src = ReadSource(System.IO.Path.Combine("Features", "Transport", "KmhApiServer.cs"));
                if (src == null) return (false, "source not found beside the build - cannot check");
                bool timeout = src.Contains("client.SendTimeout = SendTimeoutMs;");
                bool boundedWrite = src.Contains("new CancellationTokenSource(SendTimeoutMs)") && src.Contains("Stream.WriteAsync(");
                bool capped = src.Contains("queued > MaxQueuedBytes") && src.Contains("KmhTransportStats.SlowPeerDropped()");
                bool noSendLock = !src.Contains("_sendLock") && !src.Contains("Stream.Write(");
                return (timeout && boundedWrite && capped && noSendLock && Features.Transport.KmhApiServer.SendTimeoutMs <= 30_000
                        && Features.Transport.KmhApiServer.ApiConn.MaxQueuedBytes <= 64L * 1024 * 1024,
                        $"sendTimeout={timeout} boundedWrite={boundedWrite} queueCapped={capped} noSharedSendLock={noSendLock}");
            });
        }

        // A new guild under a freed name must not inherit the old one's alliance offers, donations or private chat.
        private static void GuildNameReuse(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Guild names: a guild re-created under a freed name inherits no alliance, donation or chat", () =>
            {
                string a = Fresh("gna"), w = Fresh("gnw"), donor = Fresh("gnd"), impostor = Fresh("gni");
                string ga = "Alpha " + Guid.NewGuid().ToString("N").Substring(0, 6);
                string gw = "Wolves " + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out string why)) return (false, "seed A: " + why);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(w, gw, out why)) return (false, "seed W: " + why);
                    GuildStore.SetOpenJoin(w, true, out _);
                    GuildStore.JoinGuild(donor, gw, out _);
                    if (!TreasuryStore.DepositSilver(donor, 1000, "selftest seed")) return (false, "could not seed");
                    bool proposed = GuildStore.SetRelationship(a, gw, Features.Guilds.Dto.GuildSnapshot.RelationAlliedRequested);
                    bool donated = GuildStore.DepositToGuild(donor, 400, out string dwhy, default, "selftest-don-" + Guid.NewGuid().ToString("N"));
                    Features.Chat.ChatStore.Post(w, Features.Chat.ChatChannels.Guild(gw), "the vault code is 1234", Guid.NewGuid().ToString("N"), "selftest", out _);
                    bool hadChat = Features.Chat.ChatStore.Recent(Features.Chat.ChatChannels.Guild(gw)).Count > 0;

                    GuildStore.Leave(donor, out _);
                    GuildStore.Leave(w, out _);
                    bool staleAfterDestroy = GuildStore.HasRelationshipForTest(ga, gw);
                    long donorBack = TreasuryStore.GetPersonalSilver(donor);
                    int donorPending = TreasuryStore.UnconfirmedDepositCount(donor);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(impostor, gw, out why)) return (false, "re-create: " + why);
                    bool accepted = GuildStore.SetRelationship(impostor, ga, Features.Guilds.Dto.GuildSnapshot.RelationAllied);
                    bool allied = GuildStore.AreAllied(ga, gw);
                    int chat = Features.Chat.ChatStore.Recent(Features.Chat.ChatChannels.Guild(gw)).Count
                             + Features.Chat.ChatStore.Recent(Features.Chat.ChatChannels.Guild(gw.ToLowerInvariant())).Count;
                    long impostorVault = TreasuryStore.GetGuildSilver(gw);
                    bool toNowhere = GuildStore.SetRelationship(a, "Nobody " + Guid.NewGuid().ToString("N"), Features.Guilds.Dto.GuildSnapshot.RelationAlliedRequested);

                    bool pass = proposed && donated && hadChat && !staleAfterDestroy && !accepted && !allied && chat == 0
                             && donorBack == 1000 && donorPending == 0 && impostorVault == 0 && !toNowhere;
                    return (pass, $"proposed={proposed} donated={donated}{(donated ? "" : " (" + dwhy + ")")} hadChat={hadChat} staleAfterDestroy={staleAfterDestroy} | after reuse: accepted={accepted} allied={allied} "
                                + $"chatLeft={chat} donorSilver={donorBack} donorPending={donorPending} impostorVault={impostorVault} proposalToNoGuild={toNowhere}");
                }
                finally
                {
                    foreach (string u in new[] { impostor, donor, w, a }) GuildStore.Leave(u, out _);
                    foreach (string u in new[] { impostor, donor, w, a }) TreasuryStore.ResetPersonal(u);
                }
            });
        }

        private static void LegacyStaleAlliance(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Guild names: an alliance offer an older build left pointing at a free name is cleared when the name is taken", () =>
            {
                string a = Fresh("gla"), impostor = Fresh("gli");
                string ga = "Alpha " + Guid.NewGuid().ToString("N").Substring(0, 6);
                string ghost = "Ghost " + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out string why)) return (false, "seed: " + why);
                    GuildStore.InjectRelationshipForTest(ga, ghost, Features.Guilds.Dto.GuildSnapshot.RelationAlliedRequested);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(impostor, ghost, out why)) return (false, "create: " + why);
                    bool accepted = GuildStore.SetRelationship(impostor, ga, Features.Guilds.Dto.GuildSnapshot.RelationAllied);
                    bool stale = GuildStore.HasRelationshipForTest(ga, ghost);
                    return (!accepted && !stale && !GuildStore.AreAllied(ga, ghost), $"accepted={accepted} staleLeft={stale}");
                }
                finally { GuildStore.Leave(impostor, out _); GuildStore.Leave(a, out _); }
            });
        }

        // A proposal must reach its receiver, and a broken alliance must not be restorable by one side alone.
        private static void AllianceConsent(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            const string Allied = Features.Guilds.Dto.GuildSnapshot.RelationAllied;
            const string Asked = Features.Guilds.Dto.GuildSnapshot.RelationAlliedRequested;
            const string None = Features.Guilds.Dto.GuildSnapshot.RelationNone;
            string Name(string tag) => tag + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
            bool Lists(List<string> names, string guild) => names != null && names.Exists(n => string.Equals(n, guild, StringComparison.OrdinalIgnoreCase));
            string RelIn(string user, string guild)
                => GuildStore.BuildEnvelopeFor(user).Guild?.Relationships is Dictionary<string, string> d && d.TryGetValue(guild, out string v) ? v : None;

            Check("Alliances: the guild a proposal is sent to sees it and answers it; the proposer cannot accept its own", () =>
            {
                string a = Fresh("ala"), w = Fresh("alw");
                string ga = Name("Alpha"), gw = Name("Wolves");
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out string why)) return (false, "seed A: " + why);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(w, gw, out why)) return (false, "seed W: " + why);
                    bool proposed = GuildStore.SetRelationship(a, gw, Asked);
                    bool receiverSees = Lists(GuildStore.BuildEnvelopeFor(w).Guild?.IncomingProposals, ga);
                    bool proposerSent = RelIn(a, gw) == Asked && !Lists(GuildStore.BuildEnvelopeFor(a).Guild?.IncomingProposals, gw);
                    bool selfAccept = GuildStore.SetRelationship(a, gw, Allied);
                    bool declined = GuildStore.SetRelationship(w, ga, None);
                    bool goneAfterDecline = !Lists(GuildStore.BuildEnvelopeFor(w).Guild?.IncomingProposals, ga) && !GuildStore.HasRelationshipForTest(ga, gw);
                    bool again = GuildStore.SetRelationship(a, gw, Asked);
                    bool accepted = GuildStore.SetRelationship(w, ga, Allied);
                    bool allied = GuildStore.AreAllied(ga, gw) && !Lists(GuildStore.BuildEnvelopeFor(w).Guild?.IncomingProposals, ga);
                    bool ok = proposed && receiverSees && proposerSent && !selfAccept && declined && goneAfterDecline && again && accepted && allied;
                    return (ok, $"proposed={proposed} receiverSees={receiverSees} proposerSeesSent={proposerSent} proposerAcceptedOwn={selfAccept} "
                              + $"declined={declined} clearedByDecline={goneAfterDecline} reproposed={again} accepted={accepted} allied={allied}");
                }
                finally { GuildStore.Leave(w, out _); GuildStore.Leave(a, out _); }
            });

            Check("Alliances: ending one ends it for both guilds, so neither can restore it alone", () =>
            {
                string a = Fresh("aea"), w = Fresh("aew");
                string ga = Name("Alpha"), gw = Name("Wolves");
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out string why)) return (false, "seed A: " + why);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(w, gw, out why)) return (false, "seed W: " + why);
                    if (!GuildStore.SetRelationship(a, gw, Asked) || !GuildStore.SetRelationship(w, ga, Allied)) return (false, "could not ally");
                    bool broke = GuildStore.SetRelationship(a, gw, None);
                    bool otherSideEnded = !GuildStore.HasRelationshipForTest(gw, ga) && RelIn(w, ga) == None;
                    bool aAlone = GuildStore.SetRelationship(a, gw, Allied, out string aWhy);
                    bool wAlone = GuildStore.SetRelationship(w, ga, Allied, out _);
                    // Legacy state from older builds: one side broke, the other still says allied.
                    GuildStore.InjectRelationshipForTest(gw, ga, Allied);
                    bool legacyAlone = GuildStore.SetRelationship(a, gw, Allied, out _);
                    bool legacyShown = RelIn(w, ga) == Allied;
                    bool stillApart = !GuildStore.AreAllied(ga, gw);
                    bool fresh = GuildStore.SetRelationship(a, gw, Asked) && GuildStore.SetRelationship(w, ga, Allied) && GuildStore.AreAllied(ga, gw);
                    bool ok = broke && otherSideEnded && !aAlone && !wAlone && !legacyAlone && !legacyShown && stillApart && fresh;
                    return (ok, $"broke={broke} otherSideEnded={otherSideEnded} breakerRestoredAlone={aAlone} ({aWhy}) otherRestoredAlone={wAlone} "
                              + $"restoredOffLegacyHalf={legacyAlone} legacyHalfShownAsAlly={legacyShown} apart={stillApart} freshProposalWorks={fresh}");
                }
                finally { GuildStore.Leave(w, out _); GuildStore.Leave(a, out _); }
            });

            Check("Alliances: a name with stray spaces finds the guild, and an unknown one is not called gone", () =>
            {
                string a = Fresh("ana"), w = Fresh("anw");
                string ga = Name("Alpha"), gw = Name("Wolves");
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out string why)) return (false, "seed A: " + why);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(w, gw, out why)) return (false, "seed W: " + why);
                    bool padded = GuildStore.SetRelationship(a, "  " + gw.ToLowerInvariant() + " ", Asked, out string padWhy);
                    bool sent = RelIn(a, gw) == Asked;
                    bool unknown = GuildStore.SetRelationship(a, "Nobody " + Guid.NewGuid().ToString("N").Substring(0, 6), Asked, out string unknownWhy);
                    bool honest = unknownWhy != null && unknownWhy.Contains("on this server") && !unknownWhy.Contains("any more");
                    bool ok = padded && sent && !unknown && honest;
                    return (ok, $"padded={padded} ({padWhy}) sent={sent} unknownRefused={!unknown} reason='{unknownWhy}'");
                }
                finally { GuildStore.Leave(w, out _); GuildStore.Leave(a, out _); }
            });

            Check("Alliances: diplomacy takes the SetRelationship capability, which Moderators and below lack", () =>
            {
                string a = Fresh("aca"), w = Fresh("acw"), mod = Fresh("acm"), off = Fresh("aco"), adm = Fresh("acd");
                string ga = Name("Alpha"), gw = Name("Wolves");
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(a, ga, out string why)) return (false, "seed A: " + why);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(w, gw, out why)) return (false, "seed W: " + why);
                    if (!GuildStore.AddMember(mod, ga, Features.Guilds.Dto.GuildMemberDto.RankModerator)
                        || !GuildStore.AddMember(off, ga, Features.Guilds.Dto.GuildMemberDto.RankOfficer)
                        || !GuildStore.AddMember(adm, ga, Features.Guilds.Dto.GuildMemberDto.RankAdmin)) return (false, "could not seed ranks");
                    bool modDid = GuildStore.SetRelationship(mod, gw, Asked, out string modWhy);
                    bool offDid = GuildStore.SetRelationship(off, gw, Features.Guilds.Dto.GuildSnapshot.RelationHostile);
                    bool admDid = GuildStore.SetRelationship(adm, gw, Asked);
                    bool viaCapability = KmhIlReader.CallsMadeBy("KMHServerAddon.Features.Guilds.GuildStore", "SetRelationship", 6).Contains("Can")
                                      && !GuildPermissions.Can(Features.Guilds.Dto.GuildMemberDto.RankModerator, GuildCapability.SetRelationship);
                    bool ok = !modDid && !offDid && admDid && viaCapability;
                    return (ok, $"moderator={modDid} ({modWhy}) officer={offDid} admin={admDid} checkedByCapability={viaCapability}");
                }
                finally { foreach (string u in new[] { adm, off, mod, w, a }) GuildStore.Leave(u, out _); }
            });
        }

        // A claim that never expires and only the claimer can release lets one player hold every bounty for ever.
        private static void QuestClaimLocks(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Quests: one player's claims are capped, and a claim does not outlive the quest's expiry", () =>
            {
                string poster = Fresh("qlp"), griefer = Fresh("qlg");
                var ids = new List<long>();
                try
                {
                    if (!TreasuryStore.DepositSilver(poster, 5000, "selftest seed")) return (false, "could not seed");
                    int want = Features.Quests.QuestStore.MaxActiveClaims + 1;
                    for (int i = 0; i < want; i++)
                        ids.Add(Features.Quests.QuestStore.Post(poster, Features.Quests.Dto.QuestEntry.KindDeliverItem, "public",
                            "selftest lock " + i, "Bring steel for the selftest please", 10, "Steel", 1, 24));
                    if (ids.Contains(0)) return (false, "could not post the quests");
                    int claimed = 0;
                    foreach (long id in ids) if (Features.Quests.QuestStore.Claim(griefer, id)) claimed++;

                    long before = TreasuryStore.GetPersonalSilver(poster);
                    List<long> due = Features.Quests.QuestStore.CollectExpiredOpenIds(DateTime.UtcNow.AddDays(30).Ticks);
                    bool claimedDue = due.Contains(ids[0]);
                    bool expired = Features.Quests.QuestStore.ExpireQuest(ids[0], out _);
                    string admin = Features.Quests.QuestHandler.AdminCancel(ids[1]);
                    long after = TreasuryStore.GetPersonalSilver(poster);
                    bool pass = claimed == Features.Quests.QuestStore.MaxActiveClaims && claimedDue && expired
                             && !admin.Contains("not found") && after - before == 20;
                    return (pass, $"claimed={claimed}/{want} claimedQuestDue={claimedDue} expired={expired} admin='{admin}' refunded={after - before}");
                }
                finally
                {
                    Features.Quests.QuestStore.PurgePoster(poster);
                    TreasuryStore.PurgeOwnerForTest(poster);
                    TreasuryStore.PurgeOwnerForTest(griefer);
                }
            });
        }

        // Switching Config Enforcement on must not hold every player already online until they reconnect.
        private static void EnforcementSwitchOn(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Enforcement: switching it on does not hold players who were already admitted; new logins are held", () =>
            {
                var cfg = Features.Enforcement.EnforcementConfig.Current;
                bool had = cfg.Enabled;
                var online = Client(Fresh("enf"));
                var arriving = Client(Fresh("enf"));
                try
                {
                    cfg.Enabled = false;
                    bool ran = Features.Enforcement.EnforcementAdmission.HoldPostLoginForTest(online);
                    cfg.Enabled = true;
                    bool onlineHeld = Features.Enforcement.EnforcementAdmission.IsHeld(online);
                    bool arrivingHeld = Features.Enforcement.EnforcementAdmission.IsHeld(arriving);
                    return (ran && !onlineHeld && arrivingHeld, $"loginRan={ran} alreadyOnlineHeld={onlineHeld} newLoginHeld={arrivingHeld}");
                }
                finally { cfg.Enabled = had; }
            });
        }

        // A boom boost paid on the asking price lets two accounts trading one unit at the ceiling drain the house pool.
        private static void WashTradeBoost(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Sale boost: a boom scales on the goods' trusted worth, not on the price two players agree", () =>
            {
                string def = "KmhSelftestBoom" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, 10);
                Features.Marketplace.MarketplaceStore.TryCreditHousePool(1_000_000, "selftest seed");
                using (Features.World.WorldStore.BoomForTest(def, 1.5))
                {
                    var wash = Features.Economy.SaleSplit.Compute(Fresh("bs"), def, 100_000, demandDrift: false, worldPayoutEvents: true, units: 1);
                    var noUnits = Features.Economy.SaleSplit.Compute(Fresh("bs"), def, 100_000, demandDrift: false, worldPayoutEvents: true);
                    bool capped = wash.PoolBoost > 0 && wash.PoolBoost <= 5;
                    Features.Marketplace.MarketplaceStore.TryDebitHousePool(1_000_000, "selftest seed returned");
                    return (capped && noUnits.PoolBoost == 0, $"washBoost={wash.PoolBoost} (worth 10 x 1 at 1.5x) boostWithoutUnits={noUnits.PoolBoost}");
                }
            });

            Check("Sale boost: one seller's boom boost stops at the daily allowance, which survives a reload; another seller keeps theirs", () =>
            {
                string def = "KmhSelftestBoom" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string a = Fresh("ba"), b = Fresh("bb"), c = Fresh("bc");
                Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, 1000);
                Features.Economy.EconomyConfig pinned = KmhDeepCopy.Of(Features.Economy.EconomyConfig.Current);
                pinned.Marketplace.BoomBoostMaxPerSellerPerDay = 300;
                Features.Economy.EconomyConfig prev = Features.Economy.EconomyConfig.ApplyForTest(pinned);
                long prior = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                Features.Marketplace.MarketplaceStore.TryCreditHousePool(1_000_000, "selftest seed");
                long pool0 = Features.Marketplace.MarketplaceStore.HousePoolBalance();
                try
                {
                    long Round(string seller)
                    {
                        var s = Features.Economy.SaleSplit.Compute(seller, def, 1000, demandDrift: false, worldPayoutEvents: true, units: 1);
                        return s.CommitBoost("selftest boom round") ? s.PoolBoost : -1;
                    }
                    long aTotal = 0, bFirst;
                    using (Features.World.WorldStore.BoomForTest(def, 1.5))
                    {
                        for (int i = 0; i < 6; i++) aTotal += Round(a);
                        Features.Marketplace.MarketplaceStore.SaveToDisk();
                        Features.Marketplace.MarketplaceStore.LoadFromDisk();
                        aTotal += Round(a);
                        bFirst = Round(b);
                    }
                    // The commit re-checks the allowance, so a split planned before another sale spent it is refused there.
                    var stale = new Features.Economy.SaleSplit { BuyerCharge = 1000, SellerPayout = 1400, PoolBoost = 400, Seller = c };
                    bool staleRefused = !stale.CommitBoost("selftest over-allowance commit") && stale.PoolBoost == 0 && stale.Balances;
                    long drawn = pool0 - Features.Marketplace.MarketplaceStore.HousePoolBalance();
                    bool ok = aTotal == 300 && bFirst == 300 && staleRefused && drawn == 600
                           && Features.Marketplace.MarketplaceStore.BoostPaidTodayForTest(a) == 300;
                    return (ok, $"sellerA={aTotal} over 7 rounds (allowance 300) sellerB={bFirst} staleCommitRefused={staleRefused} poolDrawn={drawn}");
                }
                finally
                {
                    Features.Economy.EconomyConfig.ApplyForTest(prev);
                    Features.Marketplace.MarketplaceStore.TryDebitHousePool(Math.Max(0, Features.Marketplace.MarketplaceStore.HousePoolBalance() - prior), "selftest seed returned");
                }
            });
        }

        // A memory-edited client can bank silver and goods at will, so deposits are capped server-side per day.
        private static void EconomyContainment(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Features.Economy.EconomyConfig Pin(long daily, long newDaily, int hours)
            {
                Features.Economy.EconomyConfig pinned = KmhDeepCopy.Of(Features.Economy.EconomyConfig.Current);
                pinned.AntiCheat.DailyDepositLimit = daily;
                pinned.AntiCheat.NewPlayerDailyDepositLimit = newDaily;
                pinned.AntiCheat.NewPlayerHours = hours;
                pinned.AntiCheat.AlertStaff = true;
                return Features.Economy.EconomyConfig.ApplyForTest(pinned);
            }
            SubProtocol.KmhEnvelope Env(object data) => SubProtocol.KmhEnvelope.ParseForTest(data);

            Check("Deposit limit: a new account gets the new-player limit, an established one the daily limit, a simulated one none", () =>
            {
                string fresh = Fresh("dlnew"), old = Fresh("dlold"), sim = Fresh("dlsim");
                var prev = Pin(1000, 100, 10);
                try
                {
                    Features.PlayerStats.PlayerStatsStore.SetConnectedSecondsForTest(old, 10 * 3600);
                    Features.PlayerStats.PlayerStatsStore.SetConnectedSecondsForTest(fresh, 10 * 3600 - 1);
                    long lNew = KmhEconomyGuard.LimitFor(fresh, out bool nNew);
                    long lOld = KmhEconomyGuard.LimitFor(old, out bool nOld);
                    long lSim;
                    using (Features.Simulation.SimulatedAccounts.UseForTest(new Features.Simulation.SimulatedAccountsFile { Accounts = new List<string> { sim } }))
                        lSim = KmhEconomyGuard.LimitFor(sim, out _);
                    bool ok = lNew == 100 && nNew && lOld == 1000 && !nOld && lSim == 0;
                    return (ok, $"new={lNew} ({nNew}) established={lOld} ({nOld}) simulated={lSim}");
                }
                finally
                {
                    Features.Economy.EconomyConfig.ApplyForTest(prev);
                    Features.PlayerStats.PlayerStatsStore.RemoveUser(fresh);
                    Features.PlayerStats.PlayerStatsStore.RemoveUser(old);
                }
            });

            Check("Deposit limit: the last 24 hours count, older deposits and other accounts do not, and a staff allowance adds room", () =>
            {
                string a = Fresh("dlwin"), b = Fresh("dlwin");
                var prev = Pin(1000, 100, 10);
                const long t0 = 1_800_000_000;
                try
                {
                    KmhEconomyGuard.NowForTest = t0 - 24 * 3600; KmhEconomyGuard.Record(a, 70);
                    KmhEconomyGuard.NowForTest = t0 - 3600;      KmhEconomyGuard.Record(a, 30);
                    KmhEconomyGuard.NowForTest = t0;             KmhEconomyGuard.Record(a, 20); KmhEconomyGuard.Record(b, 900);
                    long used = KmhEconomyGuard.DepositedLast24h(a);
                    bool refused = !KmhEconomyGuard.AllowsDeposit(a, 60, 0, "selftest", KmhEconomyGuard.AtPreflight, out string why);
                    KmhEconomyGuard.Allow(a, 20, 24, "selftest", out _);
                    bool roomAfterAllow = KmhEconomyGuard.AllowsDeposit(a, 60, 0, "selftest", KmhEconomyGuard.AtPreflight, out _);
                    bool ok = used == 50 && refused && (why ?? "").Contains("daily deposit limit") && roomAfterAllow;
                    return (ok, $"used={used} (want 50) refused 50+60 of 100={refused} allowance made room={roomAfterAllow}");
                }
                finally { Features.Economy.EconomyConfig.ApplyForTest(prev); KmhEconomyGuard.ResetForTest(); }
            });

            Check("Deposit limit: preflight refuses past the limit, counting approvals still in flight", () =>
            {
                string u = Fresh("dlpre");
                var prev = Pin(1000, 100, 10);
                try
                {
                    bool alone = TreasuryHandler.SilverPreflightForTest(u, 90, out _);
                    TreasuryHandler.IssuePreflightForTest(Client(u), u, "silver", 70, "", 0);
                    bool inFlight = TreasuryHandler.SilverPreflightForTest(u, 40, out string why);
                    TreasuryHandler.ReapPreflightForTest();   // the test session is not live, so its approval lapses
                    bool lapsed = TreasuryHandler.SilverPreflightForTest(u, 40, out _);
                    bool ok = alone && !inFlight && (why ?? "").Contains("daily deposit limit") && lapsed;
                    return (ok, $"90 alone={alone} 40 beside 70 in flight={inFlight} ({why}) 40 after it lapsed={lapsed}");
                }
                finally { Features.Economy.EconomyConfig.ApplyForTest(prev); TreasuryHandler.ReapPreflightForTest(); KmhEconomyGuard.ResetForTest(); }
            });

            Check("Deposit limit: a zero or negative preflight is refused, and an approval of one never offsets real approvals", () =>
            {
                string u = Fresh("dlneg");
                var prev = Pin(1000, 100, 10);
                try
                {
                    bool negRefused = !TreasuryHandler.SilverPreflightForTest(u, -2_000_000_000, out string negWhy);
                    bool zeroRefused = !TreasuryHandler.SilverPreflightForTest(u, 0, out _);
                    bool itemRefused = !TreasuryHandler.ItemPreflightForTest(u, "Steel", -5, false, out _, out _);
                    // As an approval issued before this check existed would be.
                    TreasuryHandler.IssuePreflightForTest(Client(u), u, "silver", -2_000_000_000, "", 0);
                    bool notNegative = TreasuryHandler.ReservedValueForTest(u) == 0;
                    TreasuryHandler.IssuePreflightForTest(Client(u), u, "silver", 70, "", 0);
                    bool stillCounted = !TreasuryHandler.SilverPreflightForTest(u, 40, out string why);
                    bool ok = negRefused && zeroRefused && itemRefused && notNegative && stillCounted && (why ?? "").Contains("daily deposit limit");
                    return (ok, $"-2e9 refused={negRefused} ({negWhy}), 0 refused={zeroRefused}, item x-5 refused={itemRefused}; "
                              + $"-2e9 approval reserves 0={notNegative}; 40 beside -2e9 and 70 in flight refused={stillCounted} ({why})");
                }
                finally { Features.Economy.EconomyConfig.ApplyForTest(prev); TreasuryHandler.ReapPreflightForTest(); KmhEconomyGuard.ResetForTest(); }
            });

            Check("Deposit: one sent without an approval and refused by the treasury gate is never parked in Recovery", () =>
            {
                string u = Fresh("dlpark");
                var prev = Pin(1000, 100, 10);
                Features.Economy.EconomyConfig cd = KmhDeepCopy.Of(Features.Economy.EconomyConfig.Current);
                cd.EconomyMode = "Custom";
                cd.Custom.DepositCooldownSeconds = 60;
                Features.Economy.EconomyConfig.ApplyForTest(cd);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                SubProtocol.KmhRouter.OutboundForTest = (cl, k, d) => { };
                try
                {
                    // On cooldown, so the gate refuses; an older build parked the claimed silver for an admin to "return".
                    Features.Economy.EconomyAccess.RecordAction(u, false);
                    bool onCooldown = Features.Economy.EconomyAccess.OnCooldown(u, false, out _);
                    TreasuryHandler.DepositSilverForTest(Client(u), Env(new { amount = 50, txn_id = "dlpark" + Guid.NewGuid().ToString("N").Substring(0, 8) }));
                    int held = Features.Recovery.RecoveryStore.HeldCountFor(u);
                    long silver = TreasuryStore.GetPersonalSilver(u);
                    int pending = TreasuryStore.UnconfirmedDepositCount(u);
                    bool ok = onCooldown && held == 0 && silver == 0 && pending == 0;
                    return (ok, $"gate refusing (cooldown)={onCooldown} held={held} silver={silver} pending={pending}");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    Features.Economy.EconomyConfig.ApplyForTest(prev);
                    TreasuryHandler.ReapPreflightForTest();
                    TreasuryStore.PurgeOwnerForTest(u);
                    Features.Recovery.RecoveryStore.ClearUser(u);
                    KmhEconomyGuard.ResetForTest();
                }
            });

            Check("Deposit limit: past it, a deposit with no approval or carrying more than its approval lands nowhere; an approved one counts", () =>
            {
                string u = Fresh("dlraw");
                string def = "KmhSelftestLim" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Features.ItemLabels.ItemLabelCache.OwnerSetValue(def, 10);
                var prev = Pin(1000, 100, 10);
                ServerClient c = Client(u);
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                SubProtocol.KmhRouter.OutboundForTest = (cl, k, d) => { };
                try
                {
                    Features.Treasury.TreasuryAccess.Grant(u, DateTime.UtcNow.Ticks);
                    TreasuryHandler.DepositSilverForTest(c, Env(new { amount = 150, txn_id = "dlraw" + Guid.NewGuid().ToString("N").Substring(0, 8) }));
                    string itemToken = TreasuryHandler.IssuePreflightForTest(c, u, "item", 0, def, 1);
                    TreasuryHandler.DepositItemForTest(c, Env(new { item_def_name = def, qty = 1, deposit_token = itemToken,
                        txn_id = "dlraw" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        payloads = new[] { new { def_name = def, stack_count = 50, scribe_xml = "" } } }));
                    int strayPending = TreasuryStore.UnconfirmedDepositCount(u);
                    int held = Features.Recovery.RecoveryStore.HeldCountFor(u);
                    string silverToken = TreasuryHandler.IssuePreflightForTest(c, u, "silver", 50, "", 0);
                    TreasuryHandler.DepositSilverForTest(c, Env(new { amount = 50, deposit_token = silverToken, txn_id = "dlraw" + Guid.NewGuid().ToString("N").Substring(0, 8) }));
                    int honestPending = TreasuryStore.UnconfirmedDepositCount(u);
                    long counted = KmhEconomyGuard.DepositedLast24h(u);
                    bool ok = strayPending == 0 && held == 0 && honestPending == 1 && counted == 50;
                    return (ok, $"pending after unapproved 150 + swapped 50x stack={strayPending} held={held}; approved 50: pending={honestPending} counted={counted}");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    Features.Economy.EconomyConfig.ApplyForTest(prev);
                    TreasuryHandler.ReapPreflightForTest();
                    TreasuryStore.PurgeOwnerForTest(u);
                    Features.Recovery.RecoveryStore.ClearUser(u);
                    KmhEconomyGuard.ResetForTest();
                }
            });

            Check("Anti-cheat alerts: one per account per half hour, and none at all with AlertStaff off", () =>
            {
                var alerts = new List<string>();
                var prev = Pin(1000, 100, 10);
                KmhEconomyGuard.ResetAlertsForTest();
                KmhEconomyGuard.AlertForTest = alerts.Add;
                try
                {
                    Features.Economy.EconomyConfig.Current.AntiCheat.AlertStaff = false;
                    KmhEconomyGuard.AlertStaff("selftest-off", "off");
                    Features.Economy.EconomyConfig.Current.AntiCheat.AlertStaff = true;
                    KmhEconomyGuard.AlertStaff("selftest-a", "a1");
                    KmhEconomyGuard.AlertStaff("selftest-a", "a2");
                    KmhEconomyGuard.AlertStaff("selftest-b", "b1");
                    bool ok = alerts.SequenceEqual(new[] { "a1", "b1" });
                    return (ok, "posted: " + string.Join(", ", alerts));
                }
                finally { KmhEconomyGuard.AlertForTest = null; KmhEconomyGuard.ResetAlertsForTest(); Features.Economy.EconomyConfig.ApplyForTest(prev); }
            });

            Check("Freeze: a frozen account's deposits, trades and transfers stop at the router, while chat, reports and reads pass", () =>
            {
                string u = Fresh("frz");
                ServerClient c = Client(u);
                SubProtocol.KmhHandshakeHandler.MarkCompatible(c);
                var notices = new List<string>();
                var hadOut = SubProtocol.KmhRouter.OutboundForTest;
                bool hadEnforcement = Features.Enforcement.EnforcementConfig.Current.Enabled;
                try
                {
                    Features.Enforcement.EnforcementConfig.Current.Enabled = false;
                    if (!SubProtocol.KmhRouter.IsRegistered(SubProtocol.KmhProtocol.Kind.TreasuryDepositPreflight)) TreasuryHandler.Register();
                    SubProtocol.KmhRouter.OutboundForTest = (cl, k, d) =>
                    {
                        if (cl == c && k == SubProtocol.KmhProtocol.Kind.Notice)
                            notices.Add(Newtonsoft.Json.Linq.JObject.FromObject(d)["text"]?.ToString() ?? "");
                    };
                    KmhEconomyGuard.Freeze(u, "selftest", "selftest", out _);
                    SubProtocol.KmhRouter.HandleInbound(c, SubProtocol.KmhEnvelope.TryParse(new SubProtocol.KmhEnvelope(
                        SubProtocol.KmhProtocol.Kind.TreasuryDepositPreflight, new { req_id = "frz", kind = "silver", amount = 10 }).Serialize()));
                    bool routerRefused = notices.Contains(KmhEconomyGuard.FrozenRefusal);
                    string[] blocked =
                    {
                        SubProtocol.KmhProtocol.Kind.TreasuryOpen, SubProtocol.KmhProtocol.Kind.TreasuryDepositSilver,
                        SubProtocol.KmhProtocol.Kind.TreasuryWithdrawSilver, SubProtocol.KmhProtocol.Kind.MarketplaceBuy,
                        SubProtocol.KmhProtocol.Kind.MarketplacePost, SubProtocol.KmhProtocol.Kind.AuctionBid,
                        SubProtocol.KmhProtocol.Kind.GuildDonate, SubProtocol.KmhProtocol.Kind.GuildWithdraw,
                        SubProtocol.KmhProtocol.Kind.MailSend, SubProtocol.KmhProtocol.Kind.WorldDeliver,
                        SubProtocol.KmhProtocol.Kind.WantFulfill, SubProtocol.KmhProtocol.Kind.SiteBuild,
                        SubProtocol.KmhProtocol.Kind.QuestPost, SubProtocol.KmhProtocol.Kind.ConnectedDeposit,
                    };
                    string[] open =
                    {
                        SubProtocol.KmhProtocol.Kind.ChatSend, SubProtocol.KmhProtocol.Kind.ReportSubmit,
                        SubProtocol.KmhProtocol.Kind.TreasuryRequest, SubProtocol.KmhProtocol.Kind.ColonyReport,
                        SubProtocol.KmhProtocol.Kind.EnforcementProfileRequest, SubProtocol.KmhProtocol.Kind.GuildLeave,
                        SubProtocol.KmhProtocol.Kind.Hello, SubProtocol.KmhProtocol.Kind.Ping,
                        SubProtocol.KmhProtocol.Kind.TreasuryDepositConfirm, SubProtocol.KmhProtocol.Kind.TreasuryDepositReconcile,
                    };
                    var leak = blocked.Where(k => KmhEconomyGuard.RefusalFor(u, k) == null).ToList();
                    var shut = open.Where(k => KmhEconomyGuard.RefusalFor(u, k) != null).ToList();
                    KmhEconomyGuard.Unfreeze(u, out _);
                    bool thawed = blocked.All(k => KmhEconomyGuard.RefusalFor(u, k) == null);
                    bool ok = routerRefused && leak.Count == 0 && shut.Count == 0 && thawed;
                    return (ok, $"routerRefused={routerRefused} [{string.Join(" | ", notices)}] stillOpen=[{string.Join(",", leak)}] "
                              + $"wronglyShut=[{string.Join(",", shut)}] allOpenAfterUnfreeze={thawed}");
                }
                finally
                {
                    SubProtocol.KmhRouter.OutboundForTest = hadOut;
                    Features.Enforcement.EnforcementConfig.Current.Enabled = hadEnforcement;
                    KmhEconomyGuard.ResetForTest();
                }
            });

            Check("Freeze: a frozen account is off the boards, and its listings and auctions cannot be bought until it is lifted", () =>
            {
                string seller = Fresh("frzs"), buyer = Fresh("frzb");
                try
                {
                    if (!TreasuryStore.DepositItem(seller, "Steel", 20, "selftest seed") || !TreasuryStore.DepositSilver(buyer, 50_000, "selftest seed"))
                        return (false, "seed deposits refused");
                    long listing = Features.Marketplace.MarketplaceStore.Post(seller, "Steel", 10, 10, "public", 0, out string postWhy);
                    (long auction, string auctionWhy) = Features.Auctions.AuctionStore.Post(seller, "Steel", "", 0, 10, 100, 0, 0, 1, "public");
                    if (listing == 0 || auction == 0) return (false, $"seed: listing {postWhy} / auction {auctionWhy}");
                    KmhEconomyGuard.Freeze(seller, "selftest", "selftest", out _);
                    bool hidden = Features.Leaderboards.LeaderboardEngine.BannedNow()(seller) && Features.Leaderboards.LeaderboardEngine.HiddenNow()(seller);
                    bool boughtFrozen = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 1, out _, out _, out _);
                    var bidFrozen = Features.Auctions.AuctionStore.Bid(buyer, auction, 100);
                    KmhEconomyGuard.Unfreeze(seller, out _);
                    bool listedAgain = !Features.Leaderboards.LeaderboardEngine.BannedNow()(seller);
                    bool boughtThawed = Features.Marketplace.MarketplaceStore.Buy(buyer, listing, 1, out _, out _, out _);
                    bool ok = hidden && !boughtFrozen && !bidFrozen.Ok && listedAgain && boughtThawed;
                    return (ok, $"hidden={hidden} boughtWhileFrozen={boughtFrozen} bidWhileFrozen={bidFrozen.Ok} ({bidFrozen.Reason}) "
                              + $"onBoardsAfter={listedAgain} boughtAfter={boughtThawed}");
                }
                finally
                {
                    KmhEconomyGuard.ResetForTest();
                    Features.Marketplace.MarketplaceStore.PurgeSeller(seller);
                    Features.Auctions.AuctionStore.PurgeUser(seller);
                    Transactions.KmhTransactionRepository.AbortAllFor(buyer);
                    Features.Delivery.DeliveryStore.PurgeUser(buyer);
                    TreasuryStore.PurgeOwnerForTest(seller);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                }
            });

            Check("Freeze: mail a frozen account sent before the freeze cannot be accepted until it is lifted", () =>
            {
                string sender = Fresh("frzm"), alt = Fresh("frza");
                try
                {
                    if (!TreasuryStore.DepositSilver(sender, 5_000, "selftest seed")) return (false, "seed deposit refused");
                    long id = Features.Mail.MailStore.Send(sender, alt, "s", "b", 1_000, null, null, out _, out _, out string sendWhy);
                    if (id == 0) return (false, "send: " + sendWhy);
                    KmhEconomyGuard.Freeze(sender, "selftest", "selftest", out _);
                    bool claimedFrozen = Features.Mail.MailStore.Claim(alt, id, out _, out _, out string why);
                    long altWhileFrozen = TreasuryStore.GetPersonalSilver(alt);
                    KmhEconomyGuard.Unfreeze(sender, out _);
                    bool claimedThawed = Features.Mail.MailStore.Claim(alt, id, out _, out _, out _);
                    bool ok = !claimedFrozen && altWhileFrozen == 0 && claimedThawed && TreasuryStore.GetPersonalSilver(alt) == 1_000;
                    return (ok, $"claimed while frozen={claimedFrozen} ({why}) alt silver then={altWhileFrozen}; claimed after={claimedThawed}");
                }
                finally
                {
                    KmhEconomyGuard.ResetForTest();
                    Features.Mail.MailStore.PurgeUser(alt);
                    Features.Mail.MailStore.PurgeUser(sender);
                    TreasuryStore.PurgeOwnerForTest(sender);
                    TreasuryStore.PurgeOwnerForTest(alt);
                }
            });

            Check("Freeze: a guild withdraw typed in chat, and a Discord trade, are refused for a frozen account", () =>
            {
                var handle = typeof(Features.Guilds.Patches.Patch_PM_Chat_GuildCommands).GetMethod("HandleCommand",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (handle == null) return (false, "HandleCommand not found");
                string owner = Fresh("frzg");
                string guild = "Frozengate " + Guid.NewGuid().ToString("N").Substring(0, 6);
                ulong id = 0x0500_0000_0000_0000UL | (ulong)(DateTime.UtcNow.Ticks & 0xFFFF_FFFF);
                var msg = new Features.Discord.KmhDiscordMessage { Author = new Features.Discord.KmhDiscordUser { Id = id } };
                ServerClient client = Client(owner);
                bool hadEnforcement = Features.Enforcement.EnforcementConfig.Current.Enabled;
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "seed: " + why);
                    TreasuryStore.DepositGuildSilverOnce(guild, "selftest-seed-" + Guid.NewGuid().ToString("N"), 1000, owner, "selftest seed");
                    Features.LinkedAccounts.LinkedAccountsStore.SetLink(owner, "d_" + owner, id, out _);
                    SubProtocol.KmhHandshakeHandler.MarkCompatible(client);
                    Features.Enforcement.EnforcementConfig.Current.Enabled = false;
                    KmhEconomyGuard.Freeze(owner, "selftest", "selftest", out _);
                    handle.Invoke(null, new object[] { client, "/kmh guild withdraw 100" });
                    long frozenVault = TreasuryStore.GetGuildSilver(guild);
                    string frozenTrader = Features.Discord.DiscordTradeCommands.RequireLinkedAsync(msg, out string tradeWhy);
                    KmhEconomyGuard.Unfreeze(owner, out _);
                    handle.Invoke(null, new object[] { client, "/kmh guild withdraw 100" });
                    long thawedVault = TreasuryStore.GetGuildSilver(guild);
                    string thawedTrader = Features.Discord.DiscordTradeCommands.RequireLinkedAsync(msg, out _);
                    bool ok = frozenVault == 1000 && frozenTrader == null && tradeWhy == KmhEconomyGuard.FrozenRefusal
                           && thawedVault == 900 && thawedTrader == owner;
                    return (ok, $"chat withdraw while frozen left {frozenVault}, after {thawedVault}; Discord trade while frozen="
                              + $"{frozenTrader ?? "(refused)"} after={thawedTrader ?? "(refused)"}");
                }
                finally
                {
                    Features.Enforcement.EnforcementConfig.Current.Enabled = hadEnforcement;
                    KmhEconomyGuard.ResetForTest();
                    Features.LinkedAccounts.LinkedAccountsStore.Unlink(owner);
                    Transactions.KmhTransactionRepository.AbortAllFor(owner);
                    Features.Delivery.DeliveryStore.PurgeUser(owner);
                    GuildStore.Leave(owner, out _);
                    TreasuryStore.PurgeOwnerForTest(owner);
                    TreasuryStore.PurgeOwnerForTest(guild);
                }
            });

            Check("Freeze undo: needs a freeze, takes back donations still in the guild vault once, and clears the account's own treasury", () =>
            {
                string cheat = Fresh("frzu"), owner = Fresh("frzo");
                string guild = "Clawback " + Guid.NewGuid().ToString("N").Substring(0, 6);
                var lines = new List<string>();
                void Undo(string mode) => KmhEconomyGuardCommands.Freeze(new[] { "freeze", cheat, "undo", mode }, "selftest", lines.Add);
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "seed: " + why);
                    TreasuryStore.DepositGuildSilverOnce(guild, "selftest-seed-" + Guid.NewGuid().ToString("N"), 800, owner, "selftest seed");
                    Features.Guilds.Contributions.KmhGuildContributionLedger.RecordSilver(guild, cheat, 500);
                    if (!TreasuryStore.DepositSilver(cheat, 1234, "selftest seed")) return (false, "seed deposit refused");
                    Undo("confirm");
                    long unfrozen = TreasuryStore.GetGuildSilver(guild);
                    KmhEconomyGuard.Freeze(cheat, "selftest", "selftest", out _);
                    Undo("dry");
                    long dry = TreasuryStore.GetGuildSilver(guild);
                    Undo("confirm");
                    long undone = TreasuryStore.GetGuildSilver(guild);
                    long net = Features.Guilds.Contributions.KmhGuildContributionLedger.SummaryFor(guild, cheat).Silver;
                    long personal = TreasuryStore.GetPersonalSilver(cheat);
                    Undo("confirm");
                    bool saidOnce = lines.Any(l => l.StartsWith("Already undone", StringComparison.Ordinal));
                    bool ok = unfrozen == 800 && dry == 800 && undone == 300 && net == 0 && personal == 0 && saidOnce
                           && TreasuryStore.GetGuildSilver(guild) == 300;
                    return (ok, $"unfrozen={unfrozen} dry={dry} undone={undone} netDonated={net} personal={personal} secondUndoRefused={saidOnce}");
                }
                finally
                {
                    KmhEconomyGuard.ResetForTest();
                    Transactions.KmhTransactionRepository.AbortAllFor(owner);
                    GuildStore.Leave(owner, out _);
                    TreasuryStore.PurgeOwnerForTest(owner);
                    TreasuryStore.PurgeOwnerForTest(cheat);
                    TreasuryStore.PurgeOwnerForTest(guild);
                }
            });

            // Marked done, a second 'undo confirm' is refused, so the half that did not land could never be finished.
            Check("Freeze undo: an undo whose wipe could not run is not marked done, so it can be run again", () =>
            {
                string cheat = Fresh("frzw"), other = Fresh("frzx");
                var lines = new List<string>();
                try
                {
                    if (!TreasuryStore.DepositSilver(cheat, 900, "selftest seed")) return (false, "seed deposit refused");
                    KmhEconomyGuard.Freeze(cheat, "selftest", "selftest", out _);
                    // Another reset already open: wipe-economy refuses to start, as it does when a reset is still running.
                    if (!Features.Economy.KmhEconomyReset.Begin(other, "", out string why)) return (false, "begin: " + why);
                    KmhEconomyGuardCommands.Freeze(new[] { "freeze", cheat, "undo", "confirm" }, "selftest", lines.Add);
                    bool notMarked = (KmhEconomyGuard.FreezeOf(cheat)?.UndoneAtUnix ?? -1) == 0;
                    bool untouched = TreasuryStore.GetPersonalSilver(cheat) == 900;
                    bool ok = notMarked && untouched;
                    return (ok, $"notMarkedUndone={notMarked} treasuryUntouched={untouched}");
                }
                finally
                {
                    Features.Economy.KmhEconomyReset.ResetForTest();
                    Features.Economy.EconomyResetStore.Forget(other);
                    Features.Economy.EconomyResetStore.Forget(cheat);
                    KmhEconomyGuard.ResetForTest();
                    TreasuryStore.PurgeOwnerForTest(cheat);
                }
            });

            // The ledger is keyed by name, so a guild re-founded under an old one would pay for the old guild's donations.
            Check("Freeze undo: donations to an earlier guild of the same name are not taken from the guild holding it now", () =>
            {
                string cheat = Fresh("frzr"), owner = Fresh("frzn");
                string guild = "Refounded " + Guid.NewGuid().ToString("N").Substring(0, 6);
                var lines = new List<string>();
                try
                {
                    Features.Guilds.Contributions.KmhGuildContributionLedger.RecordSilver(guild, cheat, 500);
                    System.Threading.Thread.Sleep(20);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "seed: " + why);
                    TreasuryStore.DepositGuildSilverOnce(guild, "selftest-seed-" + Guid.NewGuid().ToString("N"), 800, owner, "selftest seed");
                    KmhEconomyGuard.Freeze(cheat, "selftest", "selftest", out _);
                    KmhEconomyGuardCommands.Freeze(new[] { "freeze", cheat, "undo", "confirm" }, "selftest", lines.Add);
                    long vault = TreasuryStore.GetGuildSilver(guild);
                    bool ok = vault == 800;
                    return (ok, ok ? "the new guild kept all 800" : $"the new guild holds {vault} of 800 - IT PAID FOR THE OLD GUILD'S DONATIONS");
                }
                finally
                {
                    KmhEconomyGuard.ResetForTest();
                    Features.Economy.EconomyResetStore.Forget(cheat);
                    GuildStore.Leave(owner, out _);
                    TreasuryStore.PurgeOwnerForTest(owner);
                    TreasuryStore.PurgeOwnerForTest(cheat);
                    TreasuryStore.PurgeOwnerForTest(guild);
                }
            });

            Check("Economy guard: a freeze and a staff allowance survive a reload", () =>
            {
                string u = Fresh("frzp");
                try
                {
                    KmhEconomyGuard.Freeze(u, "selftest", "selftest", out bool saved);
                    KmhEconomyGuard.Allow(u, 500, 24, "selftest", out bool allowSaved);
                    KmhEconomyGuard.Reload();
                    bool ok = saved && allowSaved && KmhEconomyGuard.IsFrozen(u) && KmhEconomyGuard.AllowanceFor(u) == 500;
                    return (ok, $"saved={saved}/{allowSaved} frozenAfterReload={KmhEconomyGuard.IsFrozen(u)} allowance={KmhEconomyGuard.AllowanceFor(u)}");
                }
                finally { KmhEconomyGuard.Unfreeze(u, out _); KmhEconomyGuard.ResetForTest(); }
            });
        }

        // An animated WebP's header can under-report its frames, and decoding them all at once is the bomb.
        private static void WebpFrameBomb(Action<string, Func<(bool, string)>> Check)
        {
            byte[] AnimatedWebp(int frames)
            {
                using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(16, 16))
                {
                    for (int i = 1; i < frames; i++) img.Frames.AddFrame(img.Frames.RootFrame);
                    using (var ms = new System.IO.MemoryStream())
                    {
                        img.Save(ms, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossless });
                        return ms.ToArray();
                    }
                }
            }

            Check("Media: an animated WebP's frames are counted from the file itself and refused before any decode", () =>
            {
                var cfg = new Features.Media.MediaConfig();
                int over = cfg.Images.MaxFrames + 3;
                byte[] bomb = AnimatedWebp(over);
                int counted = Features.Media.KmhMediaTranscode.WebpFrameCount(bomb);
                var res = Features.Media.KmhMediaTranscode.Convert(bomb, cfg);
                bool refused = !res.Ok && (res.Error ?? "").Contains("too many frames");
                // With every decoder slot taken, only a refusal made before decoding can still answer "too many frames".
                TimeSpan had = Features.Media.KmhMediaTranscode.DecodeWait;
                Features.Media.KmhMediaTranscode.DecodeWait = TimeSpan.FromMilliseconds(50);
                Features.Media.KmhMediaTranscode.Result early;
                try
                {
                    using (Features.Media.KmhMediaTranscode.HoldDecodeSlotsForTest(Features.Media.KmhMediaTranscode.MaxConcurrentDecodes))
                        early = Features.Media.KmhMediaTranscode.Convert(bomb, cfg);
                }
                finally { Features.Media.KmhMediaTranscode.DecodeWait = had; }
                bool beforeDecode = !early.Ok && (early.Error ?? "").Contains("too many frames");
                return (counted == over && refused && beforeDecode,
                        $"frames={over} counted={counted} ok={res.Ok} '{res.Error}' beforeDecode={beforeDecode} ('{early.Error}')");
            });

            Check("Media: decodes share a small fixed number of slots, so a burst of resolves cannot pile up", () =>
            {
                var cfg = new Features.Media.MediaConfig();
                byte[] still = AnimatedWebp(1);
                TimeSpan had = Features.Media.KmhMediaTranscode.DecodeWait;
                Features.Media.KmhMediaTranscode.DecodeWait = TimeSpan.FromMilliseconds(50);
                try
                {
                    Features.Media.KmhMediaTranscode.Result busy;
                    using (Features.Media.KmhMediaTranscode.HoldDecodeSlotsForTest(Features.Media.KmhMediaTranscode.MaxConcurrentDecodes))
                        busy = Features.Media.KmhMediaTranscode.Convert(still, cfg);
                    var free = Features.Media.KmhMediaTranscode.Convert(still, cfg);
                    bool pass = !busy.Ok && (busy.Error ?? "").Contains("busy") && free.Ok;
                    return (pass, $"whileFull ok={busy.Ok} '{busy.Error}'; afterwards ok={free.Ok} '{free.Error}'");
                }
                finally { Features.Media.KmhMediaTranscode.DecodeWait = had; }
            });
        }

        // The site-catalog fingerprint is broadcast by the server, so it proves nothing; label intake needs bounds.
        private static void CatalogIntake(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Site catalog: a push under the published fingerprint cannot replace the catalog; a repeat is a no-op", () =>
            {
                List<Features.Sites.Dto.SiteOutputMetadata> Meta(string cat, string label) => new List<Features.Sites.Dto.SiteOutputMetadata>
                {
                    new Features.Sites.Dto.SiteOutputMetadata { DefName = "Steel", Label = label, Categories = new List<string> { cat } },
                    new Features.Sites.Dto.SiteOutputMetadata { DefName = "WoodLog", Label = label, Categories = new List<string> { "ResourcesRaw" } },
                };
                try
                {
                    Features.Sites.SiteCatalogStore.ResetForTest();
                    var first = Features.Sites.SiteCatalogStore.Accept(Fresh("sc"), "fp-selftest", 1, 1, Meta("ResourcesRaw", "steel"), out _);
                    var forged = Features.Sites.SiteCatalogStore.Accept(Fresh("sc"), "fp-selftest", 1, 1, Meta("Weapons", "steel"), out string fwhy);
                    var again = Features.Sites.SiteCatalogStore.Accept(Fresh("sc"), "fp-selftest", 1, 1, Meta("ResourcesRaw", "Stahl"), out string awhy);
                    var huge = Features.Sites.SiteCatalogStore.Accept(Fresh("sc"), "fp-selftest", 1, Features.Sites.SiteCatalogStore.MaxChunks + 1, Meta("x", "y"), out _);
                    string steelFamily = Features.Sites.SiteCatalogStore.Lookup("Steel")?.Categories?[0] ?? "";
                    bool pass = first == Features.Sites.SiteCatalogStore.PushResult.Accepted
                             && forged == Features.Sites.SiteCatalogStore.PushResult.Rejected
                             && again == Features.Sites.SiteCatalogStore.PushResult.Ignored
                             && huge == Features.Sites.SiteCatalogStore.PushResult.Ignored && steelFamily == "ResourcesRaw";
                    return (pass, $"first={first} forged={forged} ('{fwhy}') repeat={again} ('{awhy}') oversized={huge} steelStill={steelFamily}");
                }
                finally
                {
                    Features.Sites.SiteCatalogStore.ResetForTest();
                    Features.Sites.SiteCatalogStore.LoadFromDisk();
                }
            });

            Check("Item labels: intake is bounded per push, per player and per entry, and only new fungible defs compact the vaults", () =>
            {
                var c = Client(Fresh("il"));
                bool oversizedPush = Features.ItemLabels.ItemLabelsHandler.Admit(c, Features.ItemLabels.ItemLabelsHandler.MaxEntriesPerPush + 1);
                int admitted = 0;
                var d = Client(Fresh("il"));
                for (int i = 0; i < Features.ItemLabels.ItemLabelsHandler.MaxPushesPerWindow + 3; i++)
                    if (Features.ItemLabels.ItemLabelsHandler.Admit(d, 10)) admitted++;
                string longKey = new string('k', Features.ItemLabels.ItemLabelCache.MaxKeyLength + 1);
                Features.ItemLabels.ItemLabelCache.Apply(new Dictionary<string, string> { [longKey] = "x" });
                bool longKept = Features.ItemLabels.ItemLabelCache.LabelFor(longKey) == "x";

                string fdef = "KmhSelftestFungible" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var e = Client(Fresh("il"));
                object push = new { labels = new Dictionary<string, string> { [fdef] = "thing" }, fungible = new[] { fdef }, chunk_index = 1, chunk_total = 1 };
                int before = Features.ItemLabels.ItemLabelsHandler.CompactionsRequested;
                Features.ItemLabels.ItemLabelsHandler.PushForTest(e, SubProtocol.KmhEnvelope.ParseForTest(push));
                Features.ItemLabels.ItemLabelsHandler.PushForTest(e, SubProtocol.KmhEnvelope.ParseForTest(push));
                int compactions = Features.ItemLabels.ItemLabelsHandler.CompactionsRequested - before;

                bool pass = !oversizedPush && admitted == Features.ItemLabels.ItemLabelsHandler.MaxPushesPerWindow && !longKept && compactions == 1;
                return (pass, $"oversizedPush={oversizedPush} admitted={admitted}/{Features.ItemLabels.ItemLabelsHandler.MaxPushesPerWindow + 3} longKeyKept={longKept} compactions={compactions}");
            });
        }

        // A save renamed into place before reaching disk can leave an unreadable store that boots empty, server Ready.
        private static void DurableStores(Action<string, Func<(bool, string)>> Check)
        {
            Check("Stores: an irreplaceable file that loads unreadable is written down, and keeps KMH out of Ready", () =>
            {
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kmh-selftest-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(dir);
                string store = System.IO.Path.Combine(dir, "Precious.json"), cheap = System.IO.Path.Combine(dir, "Cheap.json");
                try
                {
                    JsonFileStore.DamageMarkerOverrideForTest = System.IO.Path.Combine(dir, "DAMAGED.txt");
                    JsonFileStore.IrreplaceableOverrideForTest = p => string.Equals(p, store, StringComparison.OrdinalIgnoreCase);
                    System.IO.File.WriteAllText(cheap, "{ not json");
                    JsonFileStore.TryLoad(cheap, out Dictionary<string, int> _);
                    bool quietForCheap = !JsonFileStore.DamageReported(out _);
                    System.IO.File.WriteAllText(store, "{ not json");
                    bool loaded = JsonFileStore.TryLoad(store, out Dictionary<string, int> _);
                    bool reported = JsonFileStore.DamageReported(out string why);
                    string marker = System.IO.File.Exists(JsonFileStore.DamageMarkerPath) ? System.IO.File.ReadAllText(JsonFileStore.DamageMarkerPath) : "";

                    string main = ReadSource("Main.cs");
                    int gate = main?.IndexOf("JsonFileStore.DamageReported(out string damage)", StringComparison.Ordinal) ?? -1;
                    int ready = main?.IndexOf("Maintenance.KmhReadiness.Ready();", StringComparison.Ordinal) ?? -1;
                    string save = ReadSource(System.IO.Path.Combine("Persistence", "JsonFileStore.cs")) ?? "";
                    bool flushed = save.Contains("fs.Flush(flushToDisk: true);") && save.IndexOf("fs.Flush(flushToDisk: true);", StringComparison.Ordinal)
                                   < save.IndexOf("File.Move(tmp, path, overwrite: true);", StringComparison.Ordinal);
                    bool classified = JsonFileStore.IrreplaceableLabel(KmhDataPaths.TreasuryFile) != null
                                   && JsonFileStore.IrreplaceableLabel(KmhDataPaths.ItemLabelsFile) == null;
                    bool pass = quietForCheap && !loaded && reported && marker.Contains("Precious.json") && gate > 0 && ready > gate && flushed && classified;
                    return (pass, $"cheapQuiet={quietForCheap} loaded={loaded} reported={reported} markerNamesFile={marker.Contains("Precious.json")} "
                                + $"gateBeforeReady={gate > 0 && ready > gate} flushedBeforeRename={flushed} treasuryIrreplaceable/labelsRegenerable={classified}");
                }
                finally
                {
                    JsonFileStore.DamageMarkerOverrideForTest = null;
                    JsonFileStore.IrreplaceableOverrideForTest = null;
                    try { System.IO.Directory.Delete(dir, true); } catch { }
                }
            });
        }

        // Matching and pricing read a gear stack's DefName and quality, while the recipient's game loads its blob.
        private static void GearBlobBinding(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Gear: a stack whose saved item is not the one it names is refused at deposit", () =>
            {
                Items.KmhThingPayload P(string def, int quality, int hp, string blob)
                    => new Items.KmhThingPayload { DefName = def, Quality = quality, HitPoints = hp, MaxHitPoints = 100, StackCount = 1, ScribeXml = blob };
                string honest = "<kmhthing><t Class=\"ThingWithComps\"><def>Gun_Revolver</def><quality>Excellent</quality><health>80</health></t></kmhthing>";
                string swapped = "<kmhthing><t Class=\"ThingWithComps\"><def>WoodLog</def></t></kmhthing>";
                string dtd = "<!DOCTYPE x [<!ENTITY a \"b\">]><kmhthing><t><def>Gun_Revolver</def></t></kmhthing>";
                bool okHonest = Items.KmhItemSafety.BlobMatches(P("Gun_Revolver", 5, 80, honest), out _);
                bool okSwap = Items.KmhItemSafety.BlobMatches(P("Gun_Revolver", 5, 80, swapped), out string swapWhy);
                bool okQuality = Items.KmhItemSafety.BlobMatches(P("Gun_Revolver", 7, 80, honest), out _);
                bool okHp = Items.KmhItemSafety.BlobMatches(P("Gun_Revolver", 5, 100, honest), out _);
                bool okDtd = Items.KmhItemSafety.BlobMatches(P("Gun_Revolver", 5, 80, dtd), out _);
                string who = Fresh("gb");
                bool stored;
                try { stored = TreasuryStore.DepositPayload(who, P("Plasteel", 0, -1, swapped.Replace("WoodLog", "WoodLog"))); }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
                bool pass = okHonest && !okSwap && !okQuality && !okHp && !okDtd && !stored;
                return (pass, $"honest={okHonest} swapped={okSwap} ('{swapWhy}') quality={okQuality} hp={okHp} dtd={okDtd} depositedSwapped={stored}");
            });

            Check("Gear: a saved item hidden behind an XML namespace or a second <t> is refused, since the game loads it by bare name", () =>
            {
                Items.KmhThingPayload P(string blob)
                    => new Items.KmhThingPayload { DefName = "Steel", Quality = -1, HitPoints = -1, StackCount = 1, ScribeXml = blob };
                string decoy = "<t><def>Steel</def></t>";
                string nsFirst  = "<kmhthing><t xmlns=\"urn:x\" Class=\"Pawn\"><def>Mech_Scyther</def></t>" + decoy + "</kmhthing>";
                string nsOnly   = "<kmhthing><t xmlns=\"urn:x\" Class=\"Pawn\"><def>Mech_Scyther</def></t></kmhthing>";
                string prefixed = "<kmhthing xmlns:x=\"urn:x\"><x:t Class=\"Pawn\"><def>Mech_Scyther</def></x:t>" + decoy + "</kmhthing>";
                string twoPlain = "<kmhthing><t Class=\"Pawn\"><def>Mech_Scyther</def></t>" + decoy + "</kmhthing>";
                string nsAttr   = "<kmhthing xmlns:x=\"urn:x\"><t x:Class=\"Pawn\"><def>Steel</def></t></kmhthing>";
                string plain    = "<kmhthing>" + decoy + "</kmhthing>";
                string empty    = "<kmhthing></kmhthing>";
                string noItem   = "<thing Class=\"Apparel\"><def>Apparel_PowerArmor</def><stuff>Plasteel</stuff></thing>";
                bool refused = !Items.KmhItemSafety.BlobMatches(P(nsFirst), out string why) && !Items.KmhItemSafety.BlobMatches(P(nsOnly), out _)
                            && !Items.KmhItemSafety.BlobMatches(P(prefixed), out _) && !Items.KmhItemSafety.BlobMatches(P(twoPlain), out _)
                            && !Items.KmhItemSafety.BlobMatches(P(nsAttr), out _);
                bool kept = Items.KmhItemSafety.BlobMatches(P(plain), out _) && Items.KmhItemSafety.BlobMatches(P(empty), out _)
                         && Items.KmhItemSafety.BlobMatches(P(noItem), out _);
                return (refused && kept, $"namespaced/second <t> refused={refused} ('{why}'), plain and empty kept={kept}");
            });
        }

        // A world quest delivery must not take goods before anything names them, ignore a failed world save, or pay
        // rewards after its save with nothing recording that they are owed.
        private static void WorldDeliveryDurability(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            int SteelOf(string who) => TreasuryStore.GetSnapshotFor(who).Items.TryGetValue("Steel", out int n) ? n : 0;
            int OpenRows(string who)
            {
                int n = 0;
                foreach (Transactions.KmhTransaction t in Transactions.KmhTransactionRepository.Pending()) if (t.Player == who) n++;
                return n;
            }
            Features.World.Dto.ServerQuestDto NewQuest() => Features.World.WorldStore.CreateQuest(Features.World.Dto.ServerQuestDto.KindCooperative,
                Features.World.Dto.ServerQuestDto.ObjDeliver, "Steel", "selftest delivery", "probe", 5, 500, 60, reservedFromPool: 0);
            int Progress(long id)
            {
                foreach (Features.World.Dto.ServerQuestDto q in Features.World.WorldStore.AllQuestsForTest()) if (q.Id == id) return q.ProgressQty;
                return -1;
            }

            foreach (string phase in new[] { "taken", "credited" })
                Check($"World delivery: a crash at '{phase}' recovers to one outcome, and the reward is paid exactly once", () =>
                {
                    string who = Fresh("wd");
                    try
                    {
                        if (!TreasuryStore.DepositItem(who, "Steel", 10, "selftest seed")) return (false, "could not seed");
                        var q = NewQuest();
                        Features.World.WorldEngine.AtDeliveryPhaseForTest = p => { if (p == phase) throw new OperationCanceledException("died at " + p); };
                        try { Features.World.WorldEngine.ApplyDelivery(who, q.Id, "Steel", 5); }
                        catch (OperationCanceledException) { }
                        finally { Features.World.WorldEngine.AtDeliveryPhaseForTest = null; }
                        Transactions.KmhTransactionRepository.RecoverOnBoot();
                        Features.World.WorldEngine.ResumePendingPayouts();
                        Features.World.WorldEngine.ResumePendingPayouts();
                        int steel = SteelOf(who);
                        long silver = TreasuryStore.GetPersonalSilver(who);
                        bool credited = phase == "credited";
                        bool pass = credited ? steel == 5 && Progress(q.Id) == 5 && silver == 500 : steel == 10 && Progress(q.Id) == 0 && silver == 0;
                        return (pass && OpenRows(who) == 0, $"steel={steel} progress={Progress(q.Id)} reward={silver} openRows={OpenRows(who)}");
                    }
                    finally
                    {
                        Features.World.WorldEngine.AtDeliveryPhaseForTest = null;
                        TreasuryStore.PurgeOwnerForTest(who);
                    }
                });

            Check("World delivery: a crash after a reward lands but before it is marked paid does not pay it again", () =>
            {
                string who = Fresh("wd");
                try
                {
                    if (!TreasuryStore.DepositItem(who, "Steel", 10, "selftest seed")) return (false, "could not seed");
                    var q = NewQuest();
                    Features.World.WorldEngine.AtDeliveryPhaseForTest = p => { if (p == "reward-paid") throw new OperationCanceledException("died at " + p); };
                    try { Features.World.WorldEngine.ApplyDelivery(who, q.Id, "Steel", 5); }
                    catch (OperationCanceledException) { }
                    finally { Features.World.WorldEngine.AtDeliveryPhaseForTest = null; }
                    Features.World.WorldEngine.ResumePendingPayouts();
                    long silver = TreasuryStore.GetPersonalSilver(who);
                    return (silver == 500, $"reward={silver} (500 once)");
                }
                finally
                {
                    Features.World.WorldEngine.AtDeliveryPhaseForTest = null;
                    TreasuryStore.PurgeOwnerForTest(who);
                }
            });

            Check("World delivery: a world save that fails hands the goods back and credits nothing", () =>
            {
                string who = Fresh("wd");
                try
                {
                    if (!TreasuryStore.DepositItem(who, "Steel", 10, "selftest seed")) return (false, "could not seed");
                    var q = NewQuest();
                    bool ok;
                    using (JsonFileStore.FailWritesForTest(p => p == KmhDataPaths.WorldFile ? "injected: disk full" : null))
                        ok = Features.World.WorldEngine.ApplyDelivery(who, q.Id, "Steel", 5);
                    int steel = SteelOf(who);
                    return (!ok && steel == 10 && Progress(q.Id) == 0 && OpenRows(who) == 0, $"ok={ok} steel={steel} progress={Progress(q.Id)} openRows={OpenRows(who)}");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });
        }

        // Without pruning, the live transaction index keeps every row and spent key for the whole uptime.
        private static void LedgerGrowth(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Ledger: the live index drops old finished rows but keeps their keys spent; stale keys are evicted", () =>
            {
                var idx = new Transactions.KmhTransactionIndex();
                long now = DateTime.UtcNow.Ticks;
                string old = DateTime.UtcNow.AddHours(-3).ToString("o"), recent = DateTime.UtcNow.ToString("o");
                var rows = new List<Transactions.KmhTransaction>();
                for (int i = 0; i < 30; i++)
                {
                    var t = Transactions.KmhTransaction.Create(Fresh("lg"), "selftest", Transactions.KmhTxType.Sale, "row " + i);
                    t.RequestKey = "selftest-key-" + Guid.NewGuid().ToString("N");
                    t.Advance(Transactions.KmhTxState.Rejected);
                    t.UpdatedUtc = i < 25 ? old : recent;
                    rows.Add(t);
                    idx.Add(t);
                }
                int dropped = idx.PruneFinished(10, TimeSpan.FromHours(1), now);
                bool recentKept = idx.Get(rows[29].Id) != null;
                bool oldGone = idx.Get(rows[0].Id) == null;
                bool keySpent = idx.IsDuplicate(rows[0].RequestKey, now);
                bool keyExpires = !idx.IsDuplicate(rows[0].RequestKey, now + Transactions.KmhTransactionIndex.KeyLifetime.Ticks + 1);
                int evicted = idx.EvictStaleKeys(now + Transactions.KmhTransactionIndex.KeyLifetime.Ticks + 1);
                bool pass = dropped == 15 && idx.Count == 15 && recentKept && oldGone && keySpent && keyExpires && evicted == 15;
                return (pass, $"dropped={dropped} left={idx.Count} recentKept={recentKept} oldGone={oldGone} keyStillSpent={keySpent} "
                            + $"keyExpires={keyExpires} evicted={evicted}");
            });

            Check("Ledger: a confirmed delivery's row lets go of the goods it carried", () =>
            {
                string who = Fresh("lgc");
                var t = Transactions.KmhTransactionRepository.OpenDelivery(who, Transactions.KmhTxType.Withdrawal, "selftest", 5, null,
                    new[] { new Items.KmhThingPayload { DefName = "Gun_Revolver", StackCount = 1, ScribeXml = "<thing/>" } });
                if (t == null) return (false, "could not open a delivery row");
                t.DeliveryId = "selftest-delivery-" + Guid.NewGuid().ToString("N");
                Transactions.KmhTransactionRepository.Update(t);
                Transactions.KmhTransactionRepository.ConfirmDelivered(t.DeliveryId);
                bool confirmed = t.State == Transactions.KmhTxState.Confirmed;
                bool empty = t.EscrowPayloads.Count == 0 && t.EscrowSilver == 0;
                return (confirmed && empty, $"state={t.State} payloadsLeft={t.EscrowPayloads.Count} silverLeft={t.EscrowSilver}");
            });
        }

        // A save ticket taken after the snapshot lock lets an older snapshot land over a newer one, both reporting success.
        private static void SaveTickets(Action<string, Func<(bool, string)>> Check)
        {
            Check("Stores: every value store takes its save ticket inside the lock that snapshots it", () =>
            {
                var bad = new List<string>();
                foreach (string rel in new[] { "Delivery/DeliveryStore.cs", "Recovery/RecoveryStore.cs", "Guilds/GuildStore.cs",
                                               "Sites/SiteStore.cs", "ItemLabels/ItemLabelCache.cs", "Sites/SiteCatalogStore.cs",
                                               "World/WorldStore.cs", "WantBoard/WantStore.cs" })
                {
                    string[] parts = rel.Split('/');
                    string src = ReadSource(System.IO.Path.Combine("Features", parts[0], parts[1]));
                    if (src == null) { bad.Add(rel + " (not found)"); continue; }
                    int at = src.IndexOf("SaveToDisk()", StringComparison.Ordinal);
                    int lk = at < 0 ? -1 : src.IndexOf("lock (_lock)", at, StringComparison.Ordinal);
                    int tk = lk < 0 ? -1 : src.IndexOf("JsonFileStore.NextSequence()", lk, StringComparison.Ordinal);
                    int sv = tk < 0 ? -1 : src.IndexOf("JsonFileStore.Save(", tk, StringComparison.Ordinal);
                    int end = sv < 0 ? -1 : src.IndexOf(");", sv, StringComparison.Ordinal);
                    bool ticketed = sv > 0 && end > sv && src.Substring(sv, end - sv).Contains(", seq");
                    if (!ticketed) bad.Add(System.IO.Path.GetFileName(rel));
                }
                string rec = ReadSource(System.IO.Path.Combine("Features", "Recovery", "RecoveryStore.cs")) ?? "";
                bool scoped = rec.Contains("_memoryOnly.Remove(r.Id)") && !rec.Contains("_memoryOnly.Clear();");
                return (bad.Count == 0 && scoped, bad.Count == 0 ? $"all ticketed; recovery clears only what it saved={scoped}" : "unticketed: " + string.Join(", ", bad));
            });
        }

        // An unbounded client string lands in files every save rewrites for everyone.
        private static void PersistedBounds(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Stores: oversized client strings are refused or cut before they are persisted", () =>
            {
                string huge = new string('z', 8_000);
                string owner = Fresh("pb"), buyer = Fresh("pb");
                string guild = "Bounds " + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "seed: " + why);
                    GuildStore.SetMotd(owner, huge);
                    int motd = GuildStore.BuildEnvelopeFor(owner).Guild?.Motd?.Length ?? -1;
                    bool invite = GuildStore.Invite(owner, huge, out _, out _);
                    bool item = TreasuryStore.DepositItem(owner, huge, 1, "selftest");
                    if (!TreasuryStore.DepositSilver(buyer, 5000, "selftest seed")) return (false, "could not seed");
                    long wantDef = Features.WantBoard.WantStore.Post(buyer, huge, 1, 10, 1, "public").id;
                    long wantVis = Features.WantBoard.WantStore.Post(buyer, "Steel", 1, 10, 1, huge).id;
                    string storedVis = "";
                    foreach (Features.WantBoard.Dto.WantDto w in Features.WantBoard.WantStore.AllForAdmin()) if (w.Id == wantVis) storedVis = w.Visibility;
                    Features.Mail.MailStore.TryPrepare(owner, huge, "s", "b", out _, out _, out string mailWhy);
                    var req = new Features.Treasury.Dto.TreasuryItemRequest { Payloads = new List<Items.KmhThingPayload>
                        { new Items.KmhThingPayload { DefName = "Steel", StackCount = 1, ScribeXml = new string('x', Features.Treasury.TreasuryHandler.MaxScribeChars + 1) } } };
                    string blobWhy = Features.Treasury.TreasuryHandler.ItemClaimRefusal(req, 5000, out _);
                    bool pass = motd >= 0 && motd < huge.Length && motd <= GuildStore.MaxMotdLength && !invite && !item && wantDef == 0
                             && storedVis == Features.Guilds.GuildVisibility.Public && (mailWhy ?? "").Contains("too long") && blobWhy != null;
                    return (pass, $"motd={motd} invite={invite} itemKey={item} wantDef={wantDef} storedVisibility='{storedVis}' mail='{mailWhy}' blob='{blobWhy}'");
                }
                finally
                {
                    Features.WantBoard.WantStore.PurgeBuyer(buyer);
                    GuildStore.Leave(owner, out _);
                    TreasuryStore.PurgeOwnerForTest(owner);
                    TreasuryStore.PurgeOwnerForTest(buyer);
                }
            });
        }

        // An extension's listing veto asked on the compact paths only would let gear listed by fingerprint skip it.
        private const string VetoedDef = "KmhSelftestVetoedGear";
        private static bool _vetoRegistered;

        private static void GearListingVeto(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            if (!_vetoRegistered)
            {
                _vetoRegistered = true;
                Extensibility.KmhHooks.Instance.OnMarketplaceListing(c => c.ItemDefName == VetoedDef ? KMH.Sdk.Server.Hooks.KmhHookVerdict.Deny("selftest veto") : KMH.Sdk.Server.Hooks.KmhHookVerdict.Allow);
                Extensibility.KmhHooks.Instance.OnAuctionListing(c => c.ItemDefName == VetoedDef ? KMH.Sdk.Server.Hooks.KmhHookVerdict.Deny("selftest veto") : KMH.Sdk.Server.Hooks.KmhHookVerdict.Allow);
            }
            Check("Listing vetoes: gear listed by fingerprint is asked the same veto, and comes back when refused", () =>
            {
                string seller = Fresh("vet");
                try
                {
                    if (!TreasuryStore.DepositPayload(seller, new Items.KmhThingPayload { DefName = VetoedDef, StackCount = 1, ScribeXml = "<thing/>" })) return (false, "could not seed");
                    string fp = TreasuryStore.StoredPayloadsForTest(seller)[0].Fingerprint;
                    long market = Features.Marketplace.MarketplaceStore.PostPayload(seller, fp, 1, 100, "public", 24, out string mwhy);
                    int afterMarket = TreasuryStore.StoredPayloadsForTest(seller).Count;
                    fp = TreasuryStore.StoredPayloadsForTest(seller)[0].Fingerprint;
                    var auction = Features.Auctions.AuctionStore.PostPayload(seller, fp, 1, 100, 10, 0, 24, "public");
                    int afterAuction = TreasuryStore.StoredPayloadsForTest(seller).Count;
                    bool pass = market == 0 && (mwhy ?? "").Contains("selftest veto") && afterMarket == 1
                             && auction.id == 0 && (auction.reason ?? "").Contains("selftest veto") && afterAuction == 1;
                    return (pass, $"market={market} '{mwhy}' gearBack={afterMarket}; auction={auction.id} '{auction.reason}' gearBack={afterAuction}");
                }
                finally
                {
                    Features.Marketplace.MarketplaceStore.PurgeSeller(seller);
                    Features.Auctions.AuctionStore.PurgeUser(seller);
                    TreasuryStore.PurgeOwnerForTest(seller);
                }
            });
        }

        // "/kmh guild withdraw" typed in chat is its own ingress and must honour the kill switch, toggle and freezes.
        private static void GuildChatIngress(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Guild chat commands: a withdraw typed in chat honours maintenance and the vault kill switch", () =>
            {
                var handle = typeof(Features.Guilds.Patches.Patch_PM_Chat_GuildCommands).GetMethod("HandleCommand",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (handle == null) return (false, "HandleCommand not found");
                string owner = Fresh("gco");
                string guild = "Chatgate " + Guid.NewGuid().ToString("N").Substring(0, 6);
                var client = Client(owner);
                Features.Economy.EconomyConfig had = null;
                bool? enforcing = null;
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(owner, guild, out string why)) return (false, "seed: " + why);
                    TreasuryStore.DepositGuildSilverOnce(guild, "selftest-seed-" + Guid.NewGuid().ToString("N"), 1000, owner, "selftest seed");
                    // Admitted, so each refusal below is the gate it names and not the handshake's.
                    SubProtocol.KmhHandshakeHandler.MarkCompatible(client);
                    enforcing = Features.Enforcement.EnforcementConfig.Current.Enabled;
                    Features.Enforcement.EnforcementConfig.Current.Enabled = false;

                    Maintenance.KmhMaintenanceGate.Enter(Maintenance.KmhMaintenanceReason.OwnerMaintenance);
                    try { handle.Invoke(null, new object[] { client, "/kmh guild withdraw 100" }); }
                    finally { Maintenance.KmhMaintenanceGate.Release(); }
                    long afterFreeze = TreasuryStore.GetGuildSilver(guild);

                    had = Features.Economy.EconomyConfig.ApplyForTest(new Features.Economy.EconomyConfig { DisableGuildTreasury = true });
                    bool storeRefused = !GuildStore.WithdrawFromGuild(owner, 100, out string disabledWhy);
                    handle.Invoke(null, new object[] { client, "/kmh guild withdraw 100" });
                    long afterDisabled = TreasuryStore.GetGuildSilver(guild);
                    Features.Economy.EconomyConfig.ApplyForTest(had); had = null;

                    var unshaken = Client(owner);
                    handle.Invoke(null, new object[] { unshaken, "/kmh guild withdraw 100" });
                    long afterUnshaken = TreasuryStore.GetGuildSilver(guild);
                    Features.Enforcement.EnforcementConfig.Current.Enabled = true;
                    handle.Invoke(null, new object[] { client, "/kmh guild withdraw 100" });
                    long afterHeld = TreasuryStore.GetGuildSilver(guild);
                    Features.Enforcement.EnforcementConfig.Current.Enabled = false;

                    handle.Invoke(null, new object[] { client, "/kmh guild withdraw 100" });
                    long afterNormal = TreasuryStore.GetGuildSilver(guild);
                    bool pass = afterFreeze == 1000 && storeRefused && afterDisabled == 1000 && afterUnshaken == 1000 && afterHeld == 1000 && afterNormal == 900;
                    return (pass, $"vault after freeze={afterFreeze} disabled={afterDisabled} (store refused={storeRefused}: {disabledWhy}) "
                                + $"unshaken={afterUnshaken} held={afterHeld} normal={afterNormal}");
                }
                finally
                {
                    if (enforcing.HasValue) Features.Enforcement.EnforcementConfig.Current.Enabled = enforcing.Value;
                    if (had != null) Features.Economy.EconomyConfig.ApplyForTest(had);
                    GuildStore.Leave(owner, out _);
                    TreasuryStore.ResetPersonal(owner);
                }
            });
        }

        // An unfinished reset, like every other boot recovery, must not run before the stores it purges are loaded.
        private static void BootOrder(Action<string, Func<(bool, string)>> Check)
        {
            Check("Boot: every store is loaded before any boot recovery runs", () =>
            {
                string src = ReadSource("Main.cs");
                if (src == null) return (false, "source not found beside the build - cannot check");
                int from = src.IndexOf("Features.Economy.EconomyResetStore.LoadFromDisk();", StringComparison.Ordinal);
                int to = src.IndexOf("KmhWorldDirector.Reconcile();", StringComparison.Ordinal);
                if (from < 0 || to < from) return (false, "boot block not found");
                string block = src.Substring(from, to - from);
                int lastLoad = block.LastIndexOf(".LoadFromDisk();", StringComparison.Ordinal);
                int firstRecover = int.MaxValue;
                foreach (string call in new[] { ".RecoverOnBoot();", ".RecoverPendingReserves();", ".RecoverPendingPayouts();" })
                {
                    int i = block.IndexOf(call, StringComparison.Ordinal);
                    if (i >= 0 && i < firstRecover) firstRecover = i;
                }
                if (firstRecover == int.MaxValue) return (false, "no recovery call found");
                string lastStore = block.Substring(0, lastLoad);
                lastStore = lastStore.Substring(lastStore.LastIndexOf('\n') + 1).Trim();
                return (lastLoad < firstRecover, $"last load '{lastStore}' @{lastLoad}, first recovery @{firstRecover}");
            });
        }

        internal static string ReadSource(string relative)
        {
            try
            {
                // BaseDirectory, not Assembly.Location, which is empty in the single-file publish.
                string dir = System.IO.Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/'));
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    string candidate = System.IO.Path.Combine(dir, "Source", relative);
                    if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                    candidate = System.IO.Path.Combine(dir, relative);
                    if (System.IO.File.Exists(candidate)) return System.IO.File.ReadAllText(candidate);
                    dir = System.IO.Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        // An unmarked debit before delivery loses silver on a crash while recovery logs a refund it never made.
        private static void WithdrawalCrashWindows(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            long OwedSilver(string who)
            {
                long s = 0;
                foreach (Features.Delivery.OutboundDelivery d in Features.Delivery.DeliveryStore.OwedFor(who)) s += d.Silver;
                return s;
            }
            long Total(string who) => TreasuryStore.GetPersonalSilver(who) + OwedSilver(who) + Features.Marketplace.MarketplaceStore.HousePoolBalance();

            foreach (string phase in new[] { "row-open", "debited", "owed" })
            {
                Check($"Treasury restart: a withdrawal cut short after '{phase}' neither loses nor mints silver", () =>
                {
                    string who = Fresh("wd");
                    // A delivery is namespaced by the data-set identity, and the offline runner boots without one.
                    string hadIdentity = KmhServerIdentity.Id;
                    if (string.IsNullOrEmpty(hadIdentity)) KmhDataMeta.SetInstanceIdForTest("selftestsrv");
                    try
                    {
                        if (!TreasuryStore.DepositSilver(who, 5000, "selftest seed")) return (false, "seed");
                        long before = Total(who);
                        Features.Delivery.DeliveryStore.AtWithdrawPhaseForTest = p =>
                        {
                            if (p == phase) throw new OperationCanceledException("selftest: process died at " + p);
                        };
                        try { Features.Treasury.TreasuryHandler.WithdrawSilverToColony(who, 1000, out _); }
                        catch (OperationCanceledException) { }
                        finally { Features.Delivery.DeliveryStore.AtWithdrawPhaseForTest = null; }

                        Transactions.KmhTransactionRepository.RecoverOnBoot();
                        long after = Total(who);
                        long vault = TreasuryStore.GetPersonalSilver(who);
                        int rows = Features.Delivery.DeliveryStore.OwedFor(who).Count;
                        // Up to the debit the silver is back in the vault; once owed, it is owed exactly once.
                        bool shape = phase == "owed" ? vault == 4000 && rows == 1 : vault == 5000 && rows == 0;
                        bool ok = after == before && shape;
                        return (ok, ok ? $"total {before} before and after; vault {vault}, {rows} delivery owed"
                                       : $"total {before} -> {after} ({after - before:+#;-#;0}); vault {vault}; owed rows {rows}");
                    }
                    finally
                    {
                        if (string.IsNullOrEmpty(hadIdentity)) KmhDataMeta.SetInstanceIdForTest(hadIdentity);
                        Features.Delivery.DeliveryStore.AtWithdrawPhaseForTest = null;
                        Transactions.KmhTransactionRepository.AbortAllFor(who);
                        Features.Delivery.DeliveryStore.PurgeUser(who);
                        TreasuryStore.PurgeOwnerForTest(who);
                    }
                });
            }
        }

        // A donation confirm whose save fails must restore both sides, or the guild keeps a credit that can pay twice.
        private static void DonationRollback(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Treasury: a donation whose confirm cannot be saved leaves both vaults as they were, then lands once", () =>
            {
                string donor = Fresh("dn");
                string guild = "selftest guild " + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(donor, guild, out string gwhy)) return (false, "guild: " + gwhy);
                    if (!TreasuryStore.DepositSilver(donor, 10000, "selftest seed")) return (false, "seed donor");
                    string txn = Guid.NewGuid().ToString("N");
                    if (!TreasuryStore.BeginPendingGuildDonation(donor, guild, 4000, txn, out string why)) return (false, "begin: " + why);
                    long guildBefore = TreasuryStore.GetGuildSilver(guild);

                    int failed;
                    using (JsonFileStore.FailWritesForTest(path => path == KmhDataPaths.TreasuryFile ? "injected: disk full" : null))
                        failed = TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    JsonFileStore.ClearFailureStateForTest();
                    long guildAfterFail = TreasuryStore.GetGuildSilver(guild);
                    bool stillPending = TreasuryStore.GetSnapshotFor(donor)?.PendingDeposits?.Exists(d => d.TxnId == txn) == true;

                    int ok1 = TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    int ok2 = TreasuryStore.ConfirmDeposits(donor, new[] { txn });
                    long guildFinal = TreasuryStore.GetGuildSilver(guild);
                    long donorFinal = TreasuryStore.GetPersonalSilver(donor);

                    bool ok = failed == 0 && guildAfterFail == guildBefore && stillPending
                           && ok1 == 1 && ok2 == 0 && guildFinal == guildBefore + 4000 && donorFinal == 6000;
                    return (ok, ok ? "refused and rolled back on both sides; the retry credits 4000 once"
                                   : $"failedCommit={failed} guild {guildBefore}->{guildAfterFail}->{guildFinal} pending={stillPending} "
                                     + $"retry={ok1}/{ok2} donor={donorFinal}");
                }
                finally
                {
                    GuildStore.Leave(donor, out _);
                    TreasuryStore.ClearGuildVault(guild);
                    TreasuryStore.PurgeOwnerForTest(donor);
                }
            });
        }

        // A pending client deposit planted under the next Connected op id would make the withdrawal skip its local
        // debit while Global still credits the account.
        private static void TxnNamespace(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Treasury: a client cannot plant a server-shaped id as a pending deposit", () =>
            {
                string who = Fresh("tx");
                try
                {
                    var planted = new List<string>();
                    foreach (string id in new[] { "d89d16b84a25498198b9ad22f53b20ae:t20", "638100000000000000-7:w", "638100000000000000-7:cp",
                                                  "mail:12:claim:bob", "sale-1:house", "has space", "", new string('a', 65) })
                        if (TreasuryStore.BeginPendingDeposit(who, new Features.Treasury.Dto.PendingDeposit
                            { TxnId = id, Kind = Features.Treasury.Dto.PendingDeposit.KindSilver, Silver = 1 }))
                            planted.Add(id.Length > 40 ? id.Substring(0, 40) + "..." : id);
                    bool guidOk = TreasuryStore.BeginPendingDeposit(who, new Features.Treasury.Dto.PendingDeposit
                        { TxnId = Guid.NewGuid().ToString("N"), Kind = Features.Treasury.Dto.PendingDeposit.KindSilver, Silver = 1 });
                    bool ok = planted.Count == 0 && guidOk;
                    return (ok, ok ? "8 server or malformed shapes refused; a real client id still opens"
                                   : $"guid={guidOk} planted=[{string.Join(", ", planted)}]");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });

            Check("Treasury: a pending client deposit never counts as a server step having run", () =>
            {
                string who = Fresh("tx");
                try
                {
                    if (!TreasuryStore.DepositSilver(who, 1000, "selftest seed")) return (false, "seed");
                    string id = Guid.NewGuid().ToString("N");
                    if (!TreasuryStore.BeginPendingDeposit(who, new Features.Treasury.Dto.PendingDeposit
                        { TxnId = id, Kind = Features.Treasury.Dto.PendingDeposit.KindSilver, Silver = 1 })) return (false, "plant");
                    bool took = TreasuryStore.WithdrawSilverForTxn(who, 100, id, "selftest take");
                    long left = TreasuryStore.GetPersonalSilver(who);
                    bool ok = took && left == 900;
                    return (ok, ok ? "the take debited 100 despite a pending deposit under the same id"
                                   : $"took={took} balance={left} - A PENDING DEPOSIT SKIPPED A REAL DEBIT");
                }
                finally { TreasuryStore.PurgeOwnerForTest(who); }
            });
        }

        // Treating a non-Bounty submit as a delivery of nothing would pay a Custom quest's bounty with no review.
        private static void QuestSubmitKinds(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            void Clean(params string[] users)
            {
                foreach (string u in users) { Features.Quests.QuestStore.PurgePoster(u); TreasuryStore.PurgeOwnerForTest(u); }
            }

            Check("Quests: only a delivery completes on submit; verified and reviewed kinds pay nothing", () =>
            {
                var paid = new List<string>();
                foreach (string kind in new[] { "escort", "defend", "hunt", "build", "custom" })
                {
                    string poster = Fresh("qp"), claimer = Fresh("qc");
                    try
                    {
                        if (!TreasuryStore.DepositSilver(poster, 5000, "selftest seed")) return (false, "seed poster");
                        // Each drafted as the stock post dialog would, so validation passes and only Submit is on trial.
                        var draft = new Features.Quests.Dto.QuestEntry
                        {
                            Kind = kind, Title = "selftest " + kind, Description = "selftest: a quest a stock client would post", BountySilver = 5000,
                            EscortPickupTile = 10, EscortDropoffTile = 20,
                            DefendColonyTile = 30, DefendDurationGameTicks = 60_000,
                            HuntTargetKind = Features.Quests.Dto.QuestEntry.HuntAnimalSpecies, HuntTargetDefName = "Muffalo",
                            BuildAtTile = 40, BuildStructureDefName = "Wall",
                        };
                        long id = Features.Quests.QuestStore.PostDraft(poster, draft, 24, out string why);
                        if (id == 0) return (false, $"post {kind}: {why}");
                        if (!Features.Quests.QuestStore.Claim(claimer, id)) return (false, $"claim {kind} failed");
                        bool submitted = Features.Quests.QuestStore.Submit(claimer, id, out _);
                        long got = TreasuryStore.GetPersonalSilver(claimer);
                        if (submitted || got != 0) paid.Add($"{kind}: submitted={submitted} paid={got}");
                    }
                    finally { Clean(poster, claimer); }
                }
                return (paid.Count == 0, paid.Count == 0 ? "5 kinds refused, nothing paid"
                                                          : "PAID ON SUBMIT: " + string.Join("; ", paid));
            });

            // The kind gate alone: a crafted Custom quest carrying a real delivery target passes every other check.
            Check("Quests: a reviewed quest with a delivery target smuggled in still does not complete on submit", () =>
            {
                string poster = Fresh("qp"), claimer = Fresh("qc");
                try
                {
                    if (!TreasuryStore.DepositSilver(poster, 5000, "selftest seed")) return (false, "seed poster");
                    if (!TreasuryStore.DepositItem(claimer, "Steel", 1, "selftest seed")) return (false, "seed claimer");
                    long id = Features.Quests.QuestStore.PostDraft(poster, new Features.Quests.Dto.QuestEntry
                    {
                        Kind = Features.Quests.Dto.QuestEntry.KindCustom, Title = "selftest custom",
                        Description = "selftest: a quest a stock client would post", BountySilver = 5000,
                        TargetItemDefName = "Steel", TargetItemQty = 1,
                    }, 24, out string why);
                    if (id == 0) return (false, "post: " + why);
                    if (!Features.Quests.QuestStore.Claim(claimer, id)) return (false, "claim failed");
                    bool submitted = Features.Quests.QuestStore.Submit(claimer, id, out _);
                    long got = TreasuryStore.GetPersonalSilver(claimer);
                    bool ok = !submitted && got == 0;
                    return (ok, ok ? "refused; one steel does not buy a 5000 bounty" : $"submitted={submitted} paid={got}");
                }
                finally { Clean(poster, claimer); }
            });

            // The zero-requirement guard alone: a delivery row whose target is empty, as an older save may hold.
            Check("Quests: a delivery of nothing never counts as delivered", () =>
            {
                string poster = Fresh("qp"), claimer = Fresh("qc");
                try
                {
                    if (!TreasuryStore.DepositSilver(poster, 800, "selftest seed")) return (false, "seed poster");
                    long id = Features.Quests.QuestStore.PostDraft(poster, new Features.Quests.Dto.QuestEntry
                    {
                        Kind = Features.Quests.Dto.QuestEntry.KindDeliverItem, Title = "selftest delivery",
                        Description = "selftest", BountySilver = 800, TargetItemDefName = "Steel", TargetItemQty = 5,
                    }, 24, out string why);
                    if (id == 0) return (false, "post: " + why);
                    if (!Features.Quests.QuestStore.Claim(claimer, id)) return (false, "claim failed");
                    if (!Features.Quests.QuestStore.ClearTargetForTest(id)) return (false, "seam");
                    bool submitted = Features.Quests.QuestStore.Submit(claimer, id, out _);
                    long got = TreasuryStore.GetPersonalSilver(claimer);
                    bool ok = !submitted && got == 0;
                    return (ok, ok ? "refused, nothing paid" : $"submitted={submitted} paid={got}");
                }
                finally { Clean(poster, claimer); }
            });

            Check("Quests: a guild-only quest cannot be claimed from outside the guild by guessing its id", () =>
            {
                string poster = Fresh("qp"), member = Fresh("qm"), outsider = Fresh("qo");
                string guild = "selftest guild " + Guid.NewGuid().ToString("N").Substring(0, 6);
                try
                {
                    if (!TreasuryStore.DepositSilver(poster, 300, "selftest seed")) return (false, "seed poster");
                    long id = Features.Quests.QuestStore.PostDraft(poster, new Features.Quests.Dto.QuestEntry
                    {
                        Kind = Features.Quests.Dto.QuestEntry.KindBounty, Title = "selftest", Description = "selftest", BountySilver = 300,
                    }, 24, out string why);
                    if (id == 0) return (false, "post: " + why);
                    if (!GuildStore.CreateGuildAndJoinAsAdmin(member, guild, out string gwhy)) return (false, "guild: " + gwhy);
                    if (!Features.Quests.QuestStore.MakeGuildOnlyForTest(id, guild)) return (false, "seam");
                    bool outsiderClaimed = Features.Quests.QuestStore.Claim(outsider, id);
                    bool memberClaimed = !outsiderClaimed && Features.Quests.QuestStore.Claim(member, id);
                    bool ok = !outsiderClaimed && memberClaimed;
                    return (ok, $"outsider={outsiderClaimed} member={memberClaimed}");
                }
                finally { GuildStore.Leave(member, out _); Clean(poster, member, outsider); }
            });
        }

        // RWT keeps "Name " and "Name" apart; trimming the session name would give a padded account owner rights.
        private static void PaddedOwnerName(Action<string, Func<(bool, string)>> Check)
        {
            Check("Staff: a padded or disguised account is not the owner it imitates", () =>
            {
                var owners = new List<string> { "Knappe", "  SpacedInConfig  " };
                bool real   = Features.Identity.StaffRegistry.Contains(owners, "Knappe")
                           && Features.Identity.StaffRegistry.Contains(owners, "knappe")
                           && Features.Identity.StaffRegistry.Contains(owners, "SpacedInConfig");
                var passed = new List<string>();
                // Built from code points, so no editor or tool can quietly turn the disguise into the plain name.
                string zw = ((char)0x200B).ToString(), bomc = ((char)0xFEFF).ToString();
                foreach (string fake in new[] { "Knappe ", " Knappe", "Knappe" + (char)9, "Knappe" + zw, bomc + "Knappe", "Knappe" + (char)10 })
                    if (Features.Identity.StaffRegistry.Contains(owners, fake)) passed.Add(((int)fake[fake.Length - 1]).ToString());
                var cfg = new Features.Identity.StaffConfig { Owners = owners };
                bool noRole = Features.Identity.StaffRegistry.Resolve("Knappe ", cfg, false) == Features.Identity.StaffRoles.None;
                bool ok = real && passed.Count == 0 && noRole;
                return (ok, ok ? "the real name matches in any case; 6 disguises match nothing"
                               : $"real={real} noRole={noRole} passed=[{string.Join(", ", passed)}]");
            });

            Check("Staff: a console settings grant opens for one exact account, not its padded twin", () =>
            {
                Features.Admin.ConfigEditorGrant.ResetForTest();
                try
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    Features.Admin.ConfigEditorGrant.Open("Knappe", TimeSpan.FromMinutes(5), "selftest", now);
                    bool real = Features.Admin.ConfigEditorGrant.Allows("Knappe", now);
                    bool twin = Features.Admin.ConfigEditorGrant.Allows("Knappe ", now)
                             || Features.Admin.ConfigEditorGrant.Allows(" Knappe", now)
                             || Features.Admin.ConfigEditorGrant.Allows("Knappe" + (char)0x200B, now);
                    return (real && !twin, $"real={real} twin={twin}");
                }
                finally { Features.Admin.ConfigEditorGrant.ResetForTest(); }
            });
        }

        // A guild named "_personal:<victim>" would address the victim's vault, and Leave would move it to the attacker.
        private static void GuildNameCollision(Action<string, Func<(bool, string)>> Check, Func<string, string> Fresh)
        {
            Check("Guild names: a guild cannot be named like a personal vault, in any spelling", () =>
            {
                string attacker = Fresh("gatk"), victim = Fresh("gvic");
                var made = new List<string>();
                foreach (string name in new[]
                {
                    TreasuryStore.PersonalKeyFor(victim),
                    "_PERSONAL:" + victim.ToUpperInvariant(),
                    "  _personal:" + victim,
                    "_anything",
                    "Iron:Ledger",
                    "Iron\u0000Ledger",
                })
                {
                    if (GuildStore.CreateGuildAndJoinAsAdmin(attacker, name, out _)) { made.Add(name); GuildStore.Leave(attacker, out _); }
                    if (GuildStore.CreateGuild(name)) made.Add("(sdk) " + name);
                }
                return (made.Count == 0, made.Count == 0 ? "6 shapes refused on both creation paths"
                                                          : "CREATED: " + string.Join(" | ", made));
            });

            Check("Guild names: the exploit end to end leaves the victim's vault where it was", () =>
            {
                string attacker = Fresh("gatk"), victim = Fresh("gvic");
                try
                {
                    if (!TreasuryStore.DepositSilver(victim, 5000, "selftest seed")) return (false, "could not seed the victim");
                    GuildStore.CreateGuildAndJoinAsAdmin(attacker, TreasuryStore.PersonalKeyFor(victim), out _);
                    GuildStore.Leave(attacker, out _);
                    long victimAfter = TreasuryStore.GetPersonalSilver(victim);
                    long attackerAfter = TreasuryStore.GetPersonalSilver(attacker);
                    bool ok = victimAfter == 5000 && attackerAfter == 0;
                    return (ok, ok ? "victim 5000, attacker 0"
                                   : $"victim {victimAfter}, attacker {attackerAfter} - A PERSONAL VAULT WAS DRAINED THROUGH A GUILD NAME");
                }
                finally { TreasuryStore.ResetPersonal(victim); TreasuryStore.ResetPersonal(attacker); }
            });

            // Defence in depth: a legacy colliding guild or a caller that skips the store still cannot reach it.
            Check("Treasury: no guild call reaches a personal vault, whatever the caller", () =>
            {
                string victim = Fresh("gvic"), thief = Fresh("gatk");
                try
                {
                    if (!TreasuryStore.DepositSilver(victim, 5000, "selftest seed")) return (false, "could not seed the victim");
                    var reached = new List<string>();
                    foreach (string key in new[] { TreasuryStore.PersonalKeyFor(victim), "_PERSONAL:" + victim.ToUpperInvariant() })
                    {
                        if (TreasuryStore.GetGuildSilver(key) != 0) reached.Add("read");
                        if (TreasuryStore.WithdrawGuildSilver(key, 100, thief, "selftest")) reached.Add("withdraw");
                        if (TreasuryStore.WithdrawGuildSilverOnce(key, "m-" + Guid.NewGuid().ToString("N"), 100, thief, "selftest")) reached.Add("withdraw-once");
                        if (TreasuryStore.DepositGuildSilver(key, 100, thief, "selftest")) reached.Add("deposit");
                        if (TreasuryStore.DepositGuildItem(key, "Steel", 1, "selftest")) reached.Add("deposit-item");
                        if (TreasuryStore.MoveGuildVaultToPersonal(key, thief) != 0) reached.Add("move");
                        if (TreasuryStore.ClearGuildVault(key) >= 0) reached.Add("clear");
                        if (TreasuryStore.BeginPendingGuildDonation(thief, key, 100, "t-" + Guid.NewGuid().ToString("N"), out _)) reached.Add("donation");
                    }
                    long left = TreasuryStore.GetPersonalSilver(victim);
                    bool ok = reached.Count == 0 && left == 5000;
                    return (ok, ok ? "all eight refused on both spellings, victim still 5000"
                                   : $"reached: {string.Join(", ", reached)}; victim {left}");
                }
                finally { TreasuryStore.ResetPersonal(victim); TreasuryStore.ResetPersonal(thief); }
            });

            Check("Guild names: ordinary names still work, and an old colliding one is recognised at load", () =>
            {
                bool plain = GuildNames.TryNormalize("  Iron Ledger ", out string n1, out _) && n1 == "Iron Ledger"
                          && GuildNames.TryNormalize("The Cartographers' Guild", out _, out _)
                          && GuildNames.TryNormalize("Ørnulf's Kin", out _, out _);
                bool collide = GuildNames.CollidesWithPersonalKey("_personal:alice")
                            && GuildNames.CollidesWithPersonalKey(" _Personal:Alice")
                            && !GuildNames.CollidesWithPersonalKey("Iron Ledger");
                return (plain && collide, $"plain={plain} collide={collide}");
            });

            // Federation shows a guild's name on other servers' clients, which parse rich text.
            Check("Guild names: markup is refused where a guild is made", () =>
            {
                string creator = "kmh-st-markup-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var made = new List<string>();
                try
                {
                    foreach (string name in new[] { "<b>Iron</b>", "Iron <color=red>Pact", "Iron > Ledger" })
                    {
                        if (GuildStore.CreateGuildAndJoinAsAdmin(creator, name, out _)) made.Add(name);
                        if (GuildStore.GuildExists(name)) { if (!made.Contains(name)) made.Add(name); GuildStore.Leave(creator, out _); }
                    }
                    bool plain = GuildStore.CreateGuildAndJoinAsAdmin(creator, "Iron Pact " + creator.Substring(14), out string why);
                    return (made.Count == 0 && plain, made.Count == 0 ? $"all three refused; a plain name works ({why ?? "ok"})"
                                                                        : "MADE: " + string.Join(", ", made));
                }
                finally { GuildStore.Leave(creator, out _); }
            });
        }
    }
}
