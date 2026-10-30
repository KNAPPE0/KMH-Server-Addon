using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Global.Federation;
using KMHServerAddon.Features.Global.Guilds;
using KMHServerAddon.Persistence;

namespace KMHServerAddon.Features.Global
{
    // Refused cases run on a counting transport, so even a broken guard sends nothing to the production directory;
    // the one allowed case uses a real loopback listener.
    internal static class KmhGlobalIsolationSelfTest
    {
        private const string ServerHex = "1501a7ed1501a7ed1501a7ed1501a7ed";
        private const string LinkId    = "15015015015015015015015015015015";
        private static readonly CancellationToken Ct = CancellationToken.None;

        private sealed class Rig : IDisposable
        {
            public int Sent;
            public readonly long HeldAtStart = KmhGlobalSafety.HeldBack;
            private readonly GlobalConfig _cfg;
            private readonly GlobalSetupState _setup;
            private readonly bool? _selfTest;
            private readonly string _identity;
            private readonly bool _hadFed, _hadGuilds;
            private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            public Rig()
            {
                foreach (string f in new[] { KmhDataPaths.GlobalConfigFile, KmhDataPaths.GlobalSetupFile, KmhDataPaths.FederatedGuildsFile,
                                             KmhDataPaths.FederationOutboxFile, KmhDataPaths.DirectoryKeyFile })
                    _files[f] = File.Exists(f) ? File.ReadAllBytes(f) : null;

                _identity = KmhServerIdentity.Id;
                KmhDataMeta.SetInstanceIdForTest(ServerHex);
                _cfg = GlobalConfig.ApplyForTest(Production());
                GlobalSetupState s = new GlobalSetupState { PendingLinkId = "isolation-probe", PendingLinkExpiresUnix = long.MaxValue / 2 };
                s.Current = SetupStage.Registered;
                _setup = GlobalSetupState.ApplyForTest(s);
                _selfTest = KmhGlobalSafety.SelfTestOverride;
                KmhGlobalSafety.SelfTestOverride = true;
                FeaturesConfig f0 = FeaturesConfig.Current;
                _hadFed = f0.GuildFederation; _hadGuilds = f0.Guilds;
                f0.GuildFederation = true; f0.Guilds = true;
                KmhGlobalPost.SenderForTest = null;
                KmhGlobalClient.TransportForTest = (req, ct) =>
                {
                    Interlocked.Increment(ref Sent);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                };
            }

            public static GlobalConfig Production()
                => new GlobalConfig { Enabled = true, BaseUrl = GlobalConfigCheck.OfficialBaseUrl, RegisteredServerId = ServerHex };

            public long Held => KmhGlobalSafety.HeldBack - HeldAtStart;

            public void Dispose()
            {
                KmhGlobalClient.TransportForTest = null;
                KmhGlobalPost.SenderForTest = null;
                KmhGlobalSafety.SelfTestOverride = _selfTest;
                FederationService.UnavailableForTest = null;
                FederationService.ResetForTest();
                KmhGlobalChatRelay.ClearForTest();
                KmhGlobalRegistrar.Reset();
                FeaturesConfig f0 = FeaturesConfig.Current;
                f0.GuildFederation = _hadFed; f0.Guilds = _hadGuilds;
                GlobalSetupState.ApplyForTest(_setup);
                GlobalConfig.ApplyForTest(_cfg);
                KmhDataMeta.SetInstanceIdForTest(_identity);
                FederatedGuildStore.ClearForTest();
                FederationOutbox.ClearForTest();
                foreach (KeyValuePair<string, byte[]> kv in _files)
                {
                    if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                    else if (File.Exists(kv.Key)) File.Delete(kv.Key);
                }
                FederatedGuildStore.LoadFromDisk();
                FederationOutbox.LoadFromDisk();
                KmhDirectoryKey.Reload();
            }
        }

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.Message}")); }
            }

            // Counts only when disclosure would have allowed the request; otherwise "nothing sent" proves the wrong guard.
            (bool ok, string detail) Refused(Rig rig, string path, GlobalCallResult res, long heldBefore)
            {
                bool disclosed = KmhGlobalDisclosure.MayPublish(path, out string notDisclosed);
                bool isolated = res != null && res.Unreachable && (res.ErrorCode ?? "").Contains("self-test isolation");
                bool ok = disclosed && isolated && rig.Sent == 0 && rig.Held > heldBefore;
                return (ok, $"disclosure allows it: {disclosed}{(disclosed ? "" : $" ({notDisclosed})")}; answered by isolation: "
                          + $"{isolated}; requests made: {rig.Sent}; held back: {rig.Held - heldBefore}");
            }

            Check("Global isolation: a shared client call to the production directory makes no request", () =>
            {
                using Rig rig = new Rig();
                long before = rig.Held;
                GlobalCallResult res = new KmhGlobalClient(GlobalConfig.Current.BaseUrl)
                    .PostAsync("/api/v1/servers/heartbeat", new { probe = true }, null, Ct).GetAwaiter().GetResult();
                return Refused(rig, "/api/v1/servers/heartbeat", res, before);
            });

            Check("Global isolation: registration and the heartbeat publish nothing to production", () =>
            {
                using Rig rig = new Rig();
                long before = rig.Held;
                GlobalCallResult reg = new KmhGlobalClient(GlobalConfig.Current.BaseUrl)
                    .PostAsync("/api/v1/servers/register", new { probe = true }, "isolation-probe-grant", Ct).GetAwaiter().GetResult();
                (bool okReg, string dReg) = Refused(rig, "/api/v1/servers/register", reg, before);
                long mid = rig.Held;
                bool beat = KmhGlobalService.BeatOnceForLiveCheckAsync(Ct).GetAwaiter().GetResult();
                bool okBeat = !beat && rig.Sent == 0 && rig.Held > mid;
                return (okReg && okBeat, $"register: {dReg}; heartbeat accepted: {beat}, held back: {rig.Held - mid}");
            });

            Check("Global isolation: a Federation request a player opens makes no request", () =>
            {
                using Rig rig = new Rig();
                FederationService.UnavailableForTest = "";
                FederatedGuildLink link = FederatedGuildStore.Remember(LinkId, "isolation-federation", "kmh-st-isolation", "officer", 1, "inc-1");
                if (link == null) return (false, "could not seed a federation link");
                long before = rig.Held;
                bool read = FederationService.RefreshViewAsync(LinkId, Ct).GetAwaiter().GetResult();
                bool disclosed = KmhGlobalDisclosure.MayPublish("/api/v1/federations/view", out _);
                bool ok = !read && disclosed && rig.Sent == 0 && rig.Held > before;
                return (ok, $"view read: {read}; disclosure allows it: {disclosed}; requests made: {rig.Sent}; held back: {rig.Held - before}");
            });

            Check("Global isolation: Federation's background catch-up after a heartbeat makes no request", () =>
            {
                using Rig rig = new Rig();
                FederationService.UnavailableForTest = "";
                FederationOutbox.ClearForTest();
                if (FederatedGuildStore.Remember(LinkId, "isolation-federation", "kmh-st-isolation", "officer", 1, "inc-1") == null)
                    return (false, "could not seed a federation link");
                long before = rig.Held;
                FederationService.AfterHeartbeatAsync(Ct).GetAwaiter().GetResult();
                bool ok = rig.Sent == 0 && rig.Held > before;
                return (ok, $"requests made: {rig.Sent}; held back: {rig.Held - before}");
            });

            Check("Global isolation: Global Chat reads and the queued relay make no request", () =>
            {
                using Rig rig = new Rig();
                KmhGlobalChatRelay.ClearForTest();
                long before = rig.Held;
                List<WorldChatLine> recent = KmhGlobalChatRelay.RecentAsync(10, Ct).GetAwaiter().GetResult();
                long afterRead = rig.Held;
                // Fire-and-forget: the relay drains on its own thread, through the same client.
                KmhGlobalChatRelay.Send("kmh-st-isolation", "isolation probe", "test");
                DateTime until = DateTime.UtcNow.AddSeconds(5);
                while (rig.Held <= afterRead && DateTime.UtcNow < until) Thread.Sleep(50);
                bool ok = recent == null && afterRead > before && rig.Held > afterRead && rig.Sent == 0;
                return (ok, $"read returned {(recent == null ? "nothing" : recent.Count + " line(s)")}; held back by the read: "
                          + $"{afterRead - before}, by the queued relay: {rig.Held - afterRead}; requests made: {rig.Sent}");
            });

            Check("Global isolation: background polling - the owner-link poll and the directory key - makes no request", () =>
            {
                // Separate rigs: a failed poll marks setup Failed, and the key fetch must still be one disclosure allows.
                long pollHeld, keyHeld;
                int sent;
                string pollDetail;
                bool pollOk;
                using (Rig rig = new Rig())
                {
                    KmhGlobalRegistration.Outcome poll = KmhGlobalRegistration.PollOwnerLinkAsync(Ct).GetAwaiter().GetResult();
                    pollHeld = rig.Held; sent = rig.Sent; pollDetail = poll.Detail; pollOk = poll.Ok;
                }
                using (Rig rig = new Rig())
                {
                    KmhDirectoryKey.ResetForTest();
                    bool disclosed = KmhGlobalDisclosure.MayPublish("/api/v1/directory/key", out _);
                    KmhDirectoryKey.EnsureAsync(Ct).GetAwaiter().GetResult();
                    keyHeld = disclosed && !KmhDirectoryKey.Known ? rig.Held : 0;
                    sent += rig.Sent;
                }
                bool ok = !pollOk && pollHeld > 0 && keyHeld > 0 && sent == 0;
                return (ok, $"poll: {pollDetail}; held back by the poll: {pollHeld}, by the key fetch: {keyHeld}; requests made: {sent}");
            });

            Check("Global isolation: a local test Global is still reached, over the network", () =>
            {
                using Rig rig = new Rig();
                KmhGlobalClient.TransportForTest = null;
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Task<string> served = ServeOnceAsync(listener);
                    long before = rig.Held;
                    GlobalCallResult res = new KmhGlobalClient($"http://127.0.0.1:{port}")
                        .PostAsync("/api/v1/servers/heartbeat", new { probe = true }, null, Ct).GetAwaiter().GetResult();
                    string requestLine = served.Wait(TimeSpan.FromSeconds(10)) ? served.Result : "";
                    bool ok = res.Ok && requestLine.StartsWith("POST /api/v1/servers/heartbeat", StringComparison.Ordinal)
                           && rig.Held == before;
                    return (ok, $"answered: {res.Status}; the listener saw: '{requestLine}'; held back: {rig.Held - before}");
                }
                finally { listener.Stop(); }
            });

            Check("Global isolation: still enforced after a reload, from the runner's own KMH_SELFTEST alone", () =>
            {
                using Rig rig = new Rig();
                GlobalConfig.Reload();
                KmhGlobalRegistrar.Reset();
                GlobalConfig.ApplyForTest(Rig.Production());
                KmhGlobalSafety.SelfTestOverride = null;
                bool fromEnvironment = KmhGlobalSafety.SelfTestActive;
                long before = rig.Held;
                GlobalCallResult res = new KmhGlobalClient(GlobalConfig.Current.BaseUrl)
                    .GetAsync("/api/v1/directory/key", Ct).GetAwaiter().GetResult();
                (bool ok, string detail) = Refused(rig, "/api/v1/directory/key", res, before);
                return (fromEnvironment && ok, $"{KmhGlobalSafety.SelfTestEnvVar} active from the environment: {fromEnvironment}; {detail}");
            });

            Check("Global isolation: the request monitor sees a request that bypasses the client, and only that", () =>
            {
                bool startedHere = !KmhGlobalIsolationMonitor.Running;
                // The multi-server run's own Global is local, and a request to it is not a bypass.
                GlobalConfig had = GlobalConfig.ApplyForTest(new GlobalConfig { Enabled = true, BaseUrl = "http://127.0.0.1:9" });
                KmhGlobalIsolationMonitor.Start();
                KmhGlobalIsolationMonitor.ResetForTest();
                try
                {
                    // Never connects: the request is reported when it starts, before any lookup or connection.
                    using HttpClient stray = new HttpClient(new SocketsHttpHandler
                    {
                        ConnectCallback = (ctx, ct) => throw new IOException("isolation probe: no connection is made"),
                    });
                    try { stray.GetAsync("http://127.0.0.1:9/api/v1/probe").GetAwaiter().GetResult(); } catch (HttpRequestException) { }
                    long afterLocal = KmhGlobalIsolationMonitor.Escaped;
                    GlobalConfig.ApplyForTest(Rig.Production());
                    try { stray.GetAsync(GlobalConfigCheck.OfficialBaseUrl + "/api/v1/probe").GetAwaiter().GetResult(); } catch (HttpRequestException) { }
                    long afterGlobal = KmhGlobalIsolationMonitor.Escaped;
                    bool ok = afterLocal == 0 && afterGlobal == 1;
                    return (ok, $"local request counted: {afterLocal}; production request counted: {afterGlobal - afterLocal} "
                              + $"({KmhGlobalIsolationMonitor.EscapedHosts})");
                }
                finally
                {
                    KmhGlobalIsolationMonitor.ResetForTest();
                    if (startedHere) KmhGlobalIsolationMonitor.StopForTest();
                    GlobalConfig.ApplyForTest(had);
                }
            });

            return r;
        }

        private static async Task<string> ServeOnceAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[16384];
            StringBuilder text = new StringBuilder();
            int contentLength = -1, headerEnd = -1;
            while (true)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n <= 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                string s = text.ToString();
                if (headerEnd < 0 && (headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                {
                    foreach (string line in s.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                }
                if (headerEnd >= 0 && s.Length >= headerEnd + 4 + Math.Max(0, contentLength)) break;
            }
            byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
            await stream.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
            string all = text.ToString();
            int eol = all.IndexOf("\r\n", StringComparison.Ordinal);
            return eol > 0 ? all.Substring(0, eol) : all;
        }
    }
}
