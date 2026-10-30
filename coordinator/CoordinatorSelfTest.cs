using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Kmh.Discord.Wire;

namespace KmhDiscordCoordinator
{
    // Runs without Discord or a game server; everything below the gateway edge is decided here.
    public static class CoordinatorSelfTest
    {
        public static int Run()
        {
            List<(string Name, bool Ok, string Detail)> r = new List<(string, bool, string)>();
            Routing(r);
            Claims(r);
            Auth(r);
            Timeouts(r);
            Contract(r);
            ConfigGuards(r);
            Revocation(r);
            Hardening(r);

            int pass = r.Count(x => x.Ok);
            foreach ((string name, bool ok, string detail) in r)
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? "" : "  - " + detail)}");
            Console.WriteLine($"\nchecks: {pass} pass, {r.Count - pass} fail");
            return r.Count == pass ? 0 : 1;
        }

        private static CoordinatorConfig TwoServers()
        {
            CoordinatorConfig c = new CoordinatorConfig { CommandTimeoutSeconds = 1, LeaseSeconds = 60 };
            c.Servers.Add(new CoordinatorConfig.ServerEntry
            {
                ServerId = "srv-vanilla", Alias = "vanilla", PublicKey = "x", Official = true,
                Channels = { [KmhChannelRole.KmhChat] = "1001", [KmhChannelRole.Announcements] = "1002" },
            });
            c.Servers.Add(new CoordinatorConfig.ServerEntry
            {
                ServerId = "srv-test", Alias = "test-current", PublicKey = "y",
                Channels = { [KmhChannelRole.KmhChat] = "2001", [KmhChannelRole.Announcements] = "2002" },
            });
            return c;
        }

        private static KmhDiscordRegisterRequest Reg(string serverId, string instance, long gen = 1)
            => new KmhDiscordRegisterRequest
            {
                ServerId = serverId, InstanceId = instance, Generation = gen,
                DisplayName = serverId, Capabilities = new List<string>(KmhDiscordCapability.All),
            };

        private static void Routing(List<(string, bool, string)> r)
        {
            CoordinatorConfig cfg = TwoServers();
            ServerRegistry reg = new ServerRegistry(cfg);
            reg.Claim(Reg("srv-vanilla", "i1"), cfg.Servers[0], "tok-v", out LiveServer v, out _);
            reg.Claim(Reg("srv-test", "i2"), cfg.Servers[1], "tok-t", out LiveServer t, out _);

            bool split = reg.ByChannel(1001)?.Alias == "vanilla" && reg.ByChannel(2001)?.Alias == "test-current";
            r.Add(("routing: a chat channel reaches exactly one server", split,
                   split ? "1001 -> vanilla, 2001 -> test-current" : "CROSS-TALK"));

            bool unknown = reg.ByChannel(9999) == null;
            r.Add(("routing: an unmapped channel belongs to nobody", unknown, unknown ? "9999 -> none" : "MATCHED"));

            bool byAlias = reg.ByAlias("vanilla") == v && reg.ByServerId("srv-test") == t;
            r.Add(("routing: alias and durable id both resolve, and to different servers", byAlias && v != t, ""));

            bool announce = v.Channel(KmhChannelRole.Announcements) == 1002
                         && t.Channel(KmhChannelRole.Announcements) == 2002;
            r.Add(("routing: announcements go to each server's own channel", announce, ""));

            bool session = reg.BySession("tok-v") == v && reg.BySession("tok-t") == t && reg.BySession("nope") == null;
            r.Add(("routing: a session token identifies exactly its own server", session, ""));
        }

        private static void Claims(List<(string, bool, string)> r)
        {
            CoordinatorConfig cfg = TwoServers();
            ServerRegistry reg = new ServerRegistry(cfg);
            reg.Claim(Reg("srv-vanilla", "i1", gen: 5), cfg.Servers[0], "tok-1", out LiveServer live, out _);
            live.Enqueue(new KmhDiscordInbound { Kind = KmhDiscordInboundKind.Message, Content = "stale" });

            // An older process must not take a live route away from a newer one.
            ServerRegistry.ClaimOutcome older = reg.Claim(Reg("srv-vanilla", "i0", gen: 4), cfg.Servers[0], "tok-2",
                                                          out _, out string why);
            bool refused = older == ServerRegistry.ClaimOutcome.Superseded && live.SessionToken == "tok-1";
            r.Add(("duplicate instance: an older generation is refused rather than given a second route", refused, why));

            ServerRegistry.ClaimOutcome newer = reg.Claim(Reg("srv-vanilla", "i2", gen: 6), cfg.Servers[0], "tok-3",
                                                          out LiveServer after, out string note);
            bool replaced = newer == ServerRegistry.ClaimOutcome.Accepted
                         && after.SessionToken == "tok-3" && after.Inbox.IsEmpty;
            r.Add(("duplicate instance: a newer one replaces the route and inherits no queued events", replaced, note));

            bool single = reg.BySession("tok-1") == null;
            r.Add(("duplicate instance: the replaced session stops working at once", single, ""));
        }

        private static void Auth(List<(string, bool, string)> r)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pub = KmhDiscordAuth.Base64Url(key.ExportSubjectPublicKeyInfo());
            byte[] canonical = KmhDiscordAuth.Canonical("srv-a", 3, "inst-1", "nonce-1", 1_700_000_000);
            string sig = KmhDiscordAuth.Sign(key, canonical);

            r.Add(("auth: a server's own signature verifies", KmhDiscordAuth.Verify(pub, canonical, sig), ""));

            byte[] tampered = KmhDiscordAuth.Canonical("srv-b", 3, "inst-1", "nonce-1", 1_700_000_000);
            r.Add(("auth: another server id does not verify under the same signature",
                   !KmhDiscordAuth.Verify(pub, tampered, sig), ""));

            byte[] regen = KmhDiscordAuth.Canonical("srv-a", 4, "inst-1", "nonce-1", 1_700_000_000);
            r.Add(("auth: a changed generation does not verify", !KmhDiscordAuth.Verify(pub, regen, sig), ""));

            using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string otherPub = KmhDiscordAuth.Base64Url(other.ExportSubjectPublicKeyInfo());
            r.Add(("auth: a different key cannot impersonate a registered server",
                   !KmhDiscordAuth.Verify(otherPub, canonical, sig), ""));

            // Field framing: two different splits must never build the same bytes.
            byte[] a = KmhDiscordAuth.Canonical("ab", 1, "c", "d", 1);
            byte[] b = KmhDiscordAuth.Canonical("a", 1, "bc", "d", 1);
            r.Add(("auth: length-prefixed fields cannot be shifted into one another",
                   !a.SequenceEqual(b), ""));

            r.Add(("auth: a session token is long and random",
                   KmhDiscordAuth.NewSessionToken().Length >= 40
                   && KmhDiscordAuth.NewSessionToken() != KmhDiscordAuth.NewSessionToken(), ""));
        }

        private static void Timeouts(List<(string, bool, string)> r)
        {
            CoordinatorConfig cfg = TwoServers();
            ServerRegistry reg = new ServerRegistry(cfg);

            KmhDiscordCommandResult timedOut = reg
                .Await("req-1", TimeSpan.FromMilliseconds(80), CancellationToken.None).GetAwaiter().GetResult();
            bool says = timedOut.IsError && timedOut.ErrorCode == KmhDiscordError.Timeout;
            r.Add(("timeouts: a silent server produces a stated timeout, not a hang", says, timedOut.Content));

            bool swept = reg.PendingCount == 0;
            r.Add(("timeouts: the pending map does not grow after a timeout", swept, $"pending={reg.PendingCount}"));

            Task<KmhDiscordCommandResult> pending = reg.Await("req-2", TimeSpan.FromSeconds(5), CancellationToken.None);
            bool accepted = reg.Complete("req-2", KmhDiscordCommandResult.Text("done"));
            bool answered = pending.GetAwaiter().GetResult().Content == "done";
            r.Add(("timeouts: a result completes exactly the request it names", accepted && answered, ""));

            bool secondIgnored = !reg.Complete("req-2", KmhDiscordCommandResult.Text("again"));
            r.Add(("timeouts: a duplicate result for the same request is dropped", secondIgnored, ""));

            LiveServer v = reg.ByAlias("vanilla");
            for (int i = 0; i < LiveServer.MaxQueued + 50; i++)
                v.Enqueue(new KmhDiscordInbound { Kind = KmhDiscordInboundKind.Message });
            bool bounded = v.Inbox.Count <= LiveServer.MaxQueued && v.Dropped >= 50;
            r.Add(("offline server: its queue is bounded and the drop is counted", bounded,
                   $"queued={v.Inbox.Count}, dropped={v.Dropped}"));
        }

        private static void Contract(List<(string, bool, string)> r)
        {
            r.Add(("slash: a private command stays private", KmhSlashTree.IsPrivate("console.run")
                   && KmhSlashTree.IsPrivate("whois") && !KmhSlashTree.IsPrivate("status"), ""));

            r.Add(("slash: the coordinator answers only its own paths",
                   KmhSlashTree.IsCoordinatorPath("servers") && !KmhSlashTree.IsCoordinatorPath("players"), ""));

            r.Add(("slash: a routed path names the capability it needs",
                   KmhSlashTree.CapabilityFor("market.list") == KmhDiscordCapability.Marketplace
                   && KmhSlashTree.CapabilityFor("console.run") == KmhDiscordCapability.Console, ""));

            // The tree is built once per process; building it must not throw on any option shape.
            bool tree;
            try { tree = KmhSlashTree.Build() != null; } catch (Exception ex) { tree = false; r.Add(("slash tree", false, ex.Message)); }
            r.Add(("slash: the whole /kmh tree builds", tree, ""));

            KmhEmbedSpec spec = new KmhEmbedSpecBuilder().WithTitle("t").WithDescription("d").WithColor(0x112233)
                .AddField("f", "v", true).WithFooter("foot").WithCurrentTimestamp().Build();
            Discord.Embed rendered = KmhEmbedRender.ToEmbed(spec);
            bool embed = rendered.Title == "t" && rendered.Description == "d"
                      && rendered.Color?.RawValue == 0x112233 && rendered.Fields.Length == 1
                      && rendered.Footer?.Text == "foot" && rendered.Timestamp != null;
            r.Add(("embed: a neutral spec renders to the same Discord embed", embed, ""));

            KmhEmbedSpecBuilder huge = new KmhEmbedSpecBuilder().WithTitle(new string('x', 400));
            for (int i = 0; i < 40; i++) huge.AddField("n" + i, new string('y', 2000));
            Discord.Embed capped = KmhEmbedRender.ToEmbed(huge.Build());
            int total = capped.Length;
            bool bounded = capped.Title.Length <= 256 && capped.Fields.Length <= 25
                        && capped.Fields.All(f => f.Value.Length <= 1024) && total <= 6000;
            r.Add(("embed: oversized content is trimmed rather than refused by Discord", bounded,
                   $"title={capped.Title.Length}, fields={capped.Fields.Length}, total={total}"));

            KmhComponentSpec buttons = new KmhComponentSpec();
            buttons.Buttons.Add(new KmhButtonSpec { Label = "Buy 1", CustomId = "kmh-buy:1:2:1", Style = "success" });
            r.Add(("components: a neutral button renders", KmhEmbedRender.ToComponents(buttons) != null, ""));
        }

        private static void ConfigGuards(List<(string, bool, string)> r)
        {
            CoordinatorConfig shared = TwoServers();
            shared.Servers[1].Channels[KmhChannelRole.KmhChat] = "1001";
            bool refused = false;
            string why = "";
            try { shared.Validate(); }
            catch (InvalidDataException ex) { refused = true; why = ex.Message; }
            r.Add(("config: one channel cannot belong to two servers", refused, why));

            CoordinatorConfig dupe = TwoServers();
            dupe.Servers[1].Alias = "vanilla";
            bool aliasRefused = false;
            try { dupe.Validate(); } catch (InvalidDataException) { aliasRefused = true; }
            r.Add(("config: two servers cannot share an alias", aliasRefused, ""));

            CoordinatorConfig noKey = TwoServers();
            noKey.Servers[0].PublicKey = "";
            bool keyRefused = false;
            try { noKey.Validate(); } catch (InvalidDataException) { keyRefused = true; }
            r.Add(("config: a server with no public key is refused at load", keyRefused, ""));

            r.Add(("config: the bot token is never a config field",
                   !Newtonsoft.Json.JsonConvert.SerializeObject(CoordinatorConfig.Sample())
                       .Contains("token", StringComparison.OrdinalIgnoreCase), ""));

            CoordLog.RegisterSecret("super-secret-token-value");
            r.Add(("logging: a registered secret never reaches a line",
                   !CoordLog.Redact("token=super-secret-token-value").Contains("super-secret", StringComparison.Ordinal), ""));
        }

        private static void Revocation(List<(string, bool, string)> r)
        {
            // No Global configured at all is the single-owner case, where there is nothing to revoke against.
            GlobalRevocation.Configure("", 300, 1800);
            r.Add(("revocation: with no directory configured every server is allowed",
                   GlobalRevocation.Allowed("srv-any"), ""));

            // Unreachable Global, server never confirmed: it must not be admitted just because nobody said no.
            GlobalRevocation.Configure("http://127.0.0.1:1", 300, 1800);
            bool refused = !GlobalRevocation.AllowRegistrationAsync("srv-never-seen").GetAwaiter().GetResult();
            r.Add(("revocation: an unconfirmed server is refused while Global is unreachable", refused,
                   refused ? "registration denied" : "ADMITTED WITHOUT PROOF"));

            bool steady = !GlobalRevocation.Allowed("srv-never-seen");
            r.Add(("revocation: an unconfirmed server is not routable either", steady, ""));

            r.Add(("revocation: the outage lease is bounded and stated",
                   GlobalRevocation.Grace > TimeSpan.Zero && GlobalRevocation.Grace <= TimeSpan.FromHours(2),
                   $"{GlobalRevocation.Grace.TotalMinutes:N0} minutes"));
        }

        private static void Hardening(List<(string, bool, string)> r)
        {
            GlobalRevocation.Configure("", 300, 1800);
            CoordinatorConfig cfg = TwoServers();
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            cfg.Servers[0].PublicKey = KmhDiscordAuth.Base64Url(key.ExportSubjectPublicKeyInfo());
            ServerRegistry reg = new ServerRegistry(cfg);
            reg.Claim(Reg("srv-vanilla", "i1"), cfg.Servers[0], "tok-v", out LiveServer v, out _);

            LiveServer self = new LiveServer { Alias = "test-current", DisplayName = "Vanilla (Official) **[x](https://e.example)**" };
            LiveServer copy = new LiveServer { Alias = "test-current", DisplayName = "vanilla" };
            LiveServer real = new LiveServer { Alias = "vanilla", DisplayName = "Vanilla", Official = true };
            string selfLabel = GatewayHost.Label(self), copyLabel = GatewayHost.Label(copy);
            bool labels = selfLabel.IndexOf("official", StringComparison.OrdinalIgnoreCase) < 0 && selfLabel.EndsWith("[test-current]")
                       && selfLabel.IndexOfAny(new[] { '*', '(', ')' }) < 0 && copyLabel != GatewayHost.Label(real) && GatewayHost.Label(real) == "Vanilla (Official)";
            r.Add(("labels: a server cannot call itself Official or pass for another server's label", labels,
                   $"'{selfLabel}', copy '{copyLabel}', official '{GatewayHost.Label(real)}'"));

            KmhDiscordCommandResult planted = CommandRouter.Finish(new KmhDiscordCommandResult { Components = new KmhComponentSpec() }, v);
            r.Add(("commands: a routed answer carries no buttons, since clicks run on the channel's own server",
                   planted.Components == null, ""));

            KmhDiscordOutboundResult role = new GatewayHost(cfg, reg).ApplyAsync(v, new KmhDiscordOutbound
            {
                Op = KmhDiscordOp.Role, RoleOp = "assign", RoleId = 1, TargetUserId = 2,
            }).GetAwaiter().GetResult();
            r.Add(("roles: a server can neither create nor grant guild roles",
                   !role.Ok && role.Error == KmhDiscordError.Unsupported, role.Error));

            string fits = HttpApi.ReadBoundedAsync(new StringReader(new string('x', 100)), 100).GetAwaiter().GetResult();
            string over = HttpApi.ReadBoundedAsync(new StringReader(new string('x', 20001)), 20000).GetAwaiter().GetResult();
            r.Add(("body: a request past the cap is refused while it is read", fits?.Length == 100 && over == null,
                   $"fits={fits?.Length}, over={(over == null ? "refused" : over.Length.ToString())}"));

            int port;
            System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            cfg.Listen = $"http://localhost:{port}/";
            HttpApi api = new HttpApi(cfg, reg, new GatewayHost(cfg, reg));
            using CancellationTokenSource cts = new CancellationTokenSource();
            try { api.Start(cts.Token); }
            catch (Exception ex) { r.Add(("register: an unsigned request leaves no replay entry", false, "listener: " + ex.Message)); return; }
            try
            {
                using ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using System.Net.Http.HttpClient http = new System.Net.Http.HttpClient();
                List<int> statuses = new List<int>();
                for (int i = 0; i < 3; i++)
                {
                    KmhDiscordRegisterRequest req = Reg("srv-vanilla", "i9");
                    req.Nonce = "flood-" + i;
                    req.IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    req.Signature = KmhDiscordAuth.Sign(stranger, KmhDiscordAuth.Canonical(
                        req.ServerId, req.Generation, req.InstanceId, req.Nonce, req.IssuedAtUnix));
                    var body = new System.Net.Http.StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(req),
                                                                 System.Text.Encoding.UTF8, "application/json");
                    statuses.Add((int)http.PostAsync($"http://localhost:{port}/v1/register", body).GetAwaiter().GetResult().StatusCode);
                }
                bool ok = api.NonceCount == 0 && statuses.TrueForAll(s => s == 401);
                r.Add(("register: an unsigned request leaves no replay entry", ok,
                       $"nonces={api.NonceCount}, statuses={string.Join(",", statuses)}"));

                // Global stops vouching while the server is connected: its next poll must already be refused.
                System.Net.Http.HttpRequestMessage Poll()
                {
                    var m = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://localhost:{port}/v1/events?wait=0");
                    m.Headers.TryAddWithoutValidation("Authorization", "Bearer tok-v");
                    return m;
                }
                int before = (int)http.SendAsync(Poll()).GetAwaiter().GetResult().StatusCode;
                GlobalRevocation.Configure("http://127.0.0.1:1", 300, 1800);
                int after = (int)http.SendAsync(Poll()).GetAwaiter().GetResult().StatusCode;
                bool dropped = reg.BySession("tok-v") == null || !v.Online(DateTimeOffset.UtcNow);
                r.Add(("revocation: a connected server loses every route as soon as Global stops vouching for it",
                       before == 200 && after == 401 && dropped, $"before={before} after={after} dropped={dropped}"));
            }
            finally { cts.Cancel(); api.Stop(); GlobalRevocation.Configure("", 300, 1800); }
        }
    }
}
