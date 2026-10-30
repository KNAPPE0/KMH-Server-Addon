using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KMHServerAddon.Features.Transport;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Maintenance
{
    // A reachable listener never runs without auth, and a hello is admitted on its token alone.
    internal static class KmhTransportAuthSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            // These start and stop the listener, so on a live server they would cut every player off the API transport.
            if (!Features.Global.KmhGlobalSafety.SelfTestActive || KmhApiServer.Running)
            {
                r.Add(("transport auth: socket checks run only in the offline self-test runner", false,
                       $"selfTest={Features.Global.KmhGlobalSafety.SelfTestActive} listenerRunning={KmhApiServer.Running}"));
                return r;
            }
            void Check(string name, Func<(bool, string)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw {ex.GetType().Name}: {ex.Message}")); }
                finally { KmhApiServer.Stop(); }
            }

            // The hello quotes this number and every client dials it, so it must name a socket that is really there.
            Check("transport: the advertised port answers, and is 0 whenever nothing is listening", () =>
            {
                bool idleIsZero = !KmhApiServer.Running && KmhApiServer.Port == 0;
                int free = FreePort();
                int advertised;
                bool up, answered;
                using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = "127.0.0.1", KmhApiPort = free, RequireKmhApiAuth = true }))
                {
                    KmhApiServer.Start();
                    up = KmhApiServer.Running;
                    advertised = KmhApiServer.Port;
                    answered = up && advertised > 0 && Answers(advertised);
                    KmhApiServer.Stop();
                }
                bool stoppedIsZero = !KmhApiServer.Running && KmhApiServer.Port == 0;
                bool ok = idleIsZero && up && advertised == free && answered && stoppedIsZero;
                return (ok, ok ? $"advertised {advertised} and it accepted a connection"
                               : $"idle0={idleIsZero}, up={up}, advertised={advertised}, wanted={free}, answered={answered}, stopped0={stoppedIsZero}");
            });

            Check("transport: who connected from where is a bounded, expiring hint, not a history", () =>
            {
                KmhApiServer.ResetIngressForTest();
                long now = DateTime.UtcNow.Ticks;
                for (int i = 0; i < KmhApiServer.MaxIngressTracked + 1000; i++)
                    KmhApiServer.NoteSharedIngress($"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}", "player" + i, now + i);
                bool bounded = KmhApiServer.IngressTrackedForTest <= KmhApiServer.MaxIngressTracked;
                long later = now + KmhApiServer.IngressTtl.Ticks + TimeSpan.TicksPerMinute * 2;
                KmhApiServer.NoteSharedIngress("192.0.2.50", "late", later);
                int left = KmhApiServer.IngressTrackedForTest;
                KmhApiServer.ResetIngressForTest();
                return (bounded && left == 1, $"bounded={bounded}, {left} address(es) left after the quiet period");
            });

            Check("transport: a peer silent for the whole auth window counts as a failed login; one that closes at once does not", () =>
            {
                bool rule = KmhApiServer.HeldSilent(10_000, 10_000) && KmhApiServer.HeldSilent(9_950, 10_000)
                         && !KmhApiServer.HeldSilent(3, 10_000) && !KmhApiServer.HeldSilent(5_000, 10_000) && !KmhApiServer.HeldSilent(10_000, 0);
                string src = KmhSourceProbe.Read(System.IO.Path.Combine("Features", "Transport", "KmhApiServer.cs"));
                if (src == null) return (rule, "rule only (no source tree beside this build)");
                bool wired = src.Contains("if (HeldSilent(Environment.TickCount64 - started, _authTimeoutMs)) NoteAuthFail(ip);");
                return (rule && wired, $"rule={rule} wiredIntoFirstRead={wired}");
            });

            // `kmh transporttest` runs the guards suite on a live server, so it must not stop the real listener.
            Check("transport: the live transport test leaves a running listener alone", () =>
            {
                int port = FreePort();
                using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = "127.0.0.1", KmhApiPort = port, RequireKmhApiAuth = true }))
                {
                    KmhApiServer.Start();
                    if (!KmhApiServer.Running) return (false, "listener did not start: " + KmhApiServer.StartRefusal);
                    KmhTransportGuardsSelfTest.Run();
                    bool ok = KmhApiServer.Running && KmhApiServer.Port == port && Answers(port);
                    return (ok, ok ? "still listening on the same port afterwards" : $"running={KmhApiServer.Running} port={KmhApiServer.Port}");
                }
            });

            Check("transport auth: bind addresses are read one way everywhere", () =>
            {
                (string Bind, bool Public, bool Valid)[] cases =
                {
                    ("127.0.0.1", false, true), ("127.5.6.7", false, true), ("::1", false, true), ("[::1]", false, true),
                    ("localhost", false, true), ("::ffff:127.0.0.1", false, true),
                    ("0.0.0.0", true, true), ("::", true, true), ("192.168.1.50", true, true), ("fe80::1", true, true),
                    ("not-an-ip", true, false), ("1", true, false), ("127.1", true, false), ("", true, false),
                };
                var wrong = new List<string>();
                foreach (var c in cases)
                {
                    var cfg = new TransportConfig { BindAddress = c.Bind };
                    bool valid = TransportConfig.TryResolveBind(c.Bind, out _);
                    if (cfg.IsPublicBind != c.Public || valid != c.Valid) wrong.Add($"'{c.Bind}' public={cfg.IsPublicBind} valid={valid}");
                }
                return (wrong.Count == 0, wrong.Count == 0 ? $"{cases.Length} addresses classified" : string.Join("; ", wrong));
            });

            Check("transport auth: a reachable bind with auth off refuses to start and opens no port", () =>
            {
                var refused = new List<string>();
                foreach (string bind in new[] { "0.0.0.0", "::", "192.168.1.50" })
                {
                    int port = FreePort();
                    using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = bind, KmhApiPort = port, RequireKmhApiAuth = false }))
                    {
                        KmhApiServer.Start();
                        bool listening = Answers(port);
                        if (KmhApiServer.Running || KmhApiServer.StartRefusal == null || listening)
                            refused.Add($"{bind}: running={KmhApiServer.Running} refusal={(KmhApiServer.StartRefusal != null)} listening={listening}");
                        KmhApiServer.Stop();
                    }
                }
                return (refused.Count == 0, refused.Count == 0 ? "0.0.0.0, :: and a LAN address all refused, nothing listening" : string.Join("; ", refused));
            });

            Check("transport auth: an unreadable bind address refuses to start even with auth on", () =>
            {
                int port = FreePort();
                using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = "my-server.example", KmhApiPort = port, RequireKmhApiAuth = true }))
                {
                    KmhApiServer.Start();
                    bool ok = !KmhApiServer.Running && KmhApiServer.StartRefusal != null && KmhApiServer.StartRefusal.Contains("not an IP address");
                    return (ok, KmhApiServer.StartRefusal ?? $"running={KmhApiServer.Running}");
                }
            });

            Check("transport auth: loopback with auth off still starts for local testing", () =>
            {
                int port = FreePort();
                using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = "localhost", KmhApiPort = port, RequireKmhApiAuth = false }))
                {
                    KmhApiServer.Start();
                    bool ok = KmhApiServer.Running && KmhApiServer.StartRefusal == null && Answers(port);
                    return (ok, $"running={KmhApiServer.Running} refusal={KmhApiServer.StartRefusal ?? "none"}");
                }
            });

            Check("transport auth: a hello naming a player without a valid token is refused", () =>
            {
                int port = FreePort();
                using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = "127.0.0.1", KmhApiPort = port, RequireKmhApiAuth = true }))
                {
                    KmhApiServer.Start();
                    if (!KmhApiServer.Running) return (false, "listener did not start: " + KmhApiServer.StartRefusal);
                    string none = Hello(port, new { v = KmhProtocol.CurrentVersion, username = "victim" });
                    string forged = Hello(port, new { v = KmhProtocol.CurrentVersion, username = "victim", token = Convert.ToBase64String(new byte[24]) });
                    bool ok = none == "auth" && forged == "auth";
                    return (ok, $"no token -> {none}, forged token -> {forged}");
                }
            });

            Check("transport auth: turning auth off in the file while running does not turn it off on the socket", () =>
            {
                int port = FreePort();
                var on = new TransportConfig { EnableKmhApiTransport = true, BindAddress = "127.0.0.1", KmhApiPort = port, RequireKmhApiAuth = true };
                using (Use(on))
                {
                    KmhApiServer.Start();
                    if (!KmhApiServer.Running) return (false, "listener did not start: " + KmhApiServer.StartRefusal);
                    using (Use(new TransportConfig { EnableKmhApiTransport = true, BindAddress = "127.0.0.1", KmhApiPort = port, RequireKmhApiAuth = false }))
                    {
                        string answer = Hello(port, new { v = KmhProtocol.CurrentVersion, username = "victim" });
                        return (answer == "auth", $"tokenless hello after the reload -> {answer}");
                    }
                }
            });

            return r;
        }

        private sealed class Swap : IDisposable
        {
            private readonly TransportConfig _prev;
            public Swap(TransportConfig cfg) { _prev = TransportConfig.ApplyForTest(TransportConfig.ClampForTest(cfg)); }
            public void Dispose() => TransportConfig.ApplyForTest(_prev);
        }

        private static IDisposable Use(TransportConfig cfg) => new Swap(cfg);

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static bool Answers(int port)
        {
            try
            {
                using (var c = new TcpClient())
                    return c.ConnectAsync(IPAddress.Loopback, port).Wait(1000) && c.Connected;
            }
            catch { return false; }
        }

        private static string Hello(int port, object data)
        {
            using (var c = new TcpClient())
            {
                if (!c.ConnectAsync(IPAddress.Loopback, port).Wait(2000)) return "no connect";
                NetworkStream s = c.GetStream();
                s.ReadTimeout = 5000;
                byte[] frame = KmhFrameCodec.Frame(Encoding.UTF8.GetBytes(new KmhEnvelope("kmh.api.hello", data).Serialize()), false);
                s.Write(frame, 0, frame.Length);
                byte[] head = ReadExactly(s, 4);
                if (head == null) return "closed";
                KmhFrameCodec.ReadHeader(head, out int len, out _);
                byte[] body = ReadExactly(s, len);
                KmhEnvelope ack = body == null ? null : KmhEnvelope.TryParse(Encoding.UTF8.GetString(body));
                if (ack == null) return "unreadable";
                return ack.GetBool("ok", false) ? "ok" : ack.GetString("reason") ?? "refused";
            }
        }

        private static byte[] ReadExactly(NetworkStream s, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }
    }
}
