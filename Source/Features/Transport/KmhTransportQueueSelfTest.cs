using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Features.Transport
{
    internal static class KmhTransportQueueSelfTest
    {
        private const string Kind = "kmh.selftest.queue";

        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            var r = new List<(string, bool, string)>();
            void Check(string name, Func<(bool ok, string detail)> probe)
            {
                try { (bool ok, string detail) = probe(); r.Add((name, ok, detail)); }
                catch (Exception ex) { r.Add((name, false, $"threw: {ex.GetType().Name}: {ex.Message}")); }
            }

            Check("Transport: an encoded frame is its envelope's UTF-8 behind the header, byte for byte", () =>
            {
                var payloads = new List<(string What, KmhEnvelope Env)>
                {
                    ("plain", new KmhEnvelope(Kind, new { a = "plain" })),
                    ("escapes and accents", new KmhEnvelope(Kind, new { s = "Grüße – \"quoted\" \\ back\n\ttab" })),
                    ("emoji", new KmhEnvelope(Kind, new { e = "😀🎉 ok" })),
                    ("lone surrogates", new KmhEnvelope(Kind, new { lone = "x\uD800y\uDC00z\uD800" })),
                    ("no data", new KmhEnvelope(Kind, null)),
                    ("received", KmhEnvelope.TryParse("{\"kind\":\"k\",\"v\":3,\"op\":\"o1\",\"data\":{\"x\":1,\"y\":\"é\"}}")),
                    // Past the kept buffer, and long enough that a chunk boundary splits an emoji somewhere.
                    ("large, chunk-split emoji", new KmhEnvelope(Kind, new { big = string.Concat(Enumerable.Repeat("a😀", 70_000)) })),
                    ("medium after large", new KmhEnvelope(Kind, new { mid = string.Concat(Enumerable.Repeat("é😀z", 9_000)) })),
                    ("small after medium", new KmhEnvelope(Kind, new { tail = "short" })),
                };

                var bad = new List<string>();
                foreach ((string what, KmhEnvelope env) in payloads)
                {
                    byte[] want = Encoding.UTF8.GetBytes(env.Serialize());
                    KmhApiServer.Framed plain = KmhApiServer.Encode(env, false);
                    if (plain == null || !SameFrame(plain.Bytes, want, false)) { bad.Add(what + " (plain)"); continue; }

                    KmhApiServer.Framed packed = KmhApiServer.Encode(env, true);
                    if (packed == null) { bad.Add(what + " (deflate: null)"); continue; }
                    if (packed.Compressed)
                    {
                        byte[] inflated = KmhFrameCodec.Inflate(packed.Bytes.Skip(4).ToArray(), 8 * 1024 * 1024);
                        if (inflated == null || !inflated.SequenceEqual(want) || !Header(packed.Bytes, out int n, out bool z)
                            || !z || n != packed.Bytes.Length - 4 || packed.RawBytes != want.Length)
                            bad.Add(what + " (deflate)");
                    }
                    else if (!SameFrame(packed.Bytes, want, false)) bad.Add(what + " (deflate fell back)");
                }
                return (bad.Count == 0, bad.Count == 0 ? $"{payloads.Count} envelopes, plain and deflated, identical to the string path"
                                                       : "differs: " + string.Join(", ", bad));
            });

            Check("Transport: a player who stops reading holds up nobody else, and is dropped past the queue limit", () =>
            {
                var (srvSlow, cliSlow) = Pair();
                var (srvQuick, cliQuick) = Pair();
                KmhApiServer.ApiConn slow  = new KmhApiServer.ApiConn(srvSlow, srvSlow.GetStream(), "selftest-slow", null);
                KmhApiServer.ApiConn quick = new KmhApiServer.ApiConn(srvQuick, srvQuick.GetStream(), "selftest-quick", null);
                long droppedBefore = Stat("slow_peers_dropped");
                try
                {
                    KmhApiServer.Framed big = KmhApiServer.Encode(new KmhEnvelope(Kind, new { pad = new string('x', 60_000) }), false);
                    Stopwatch sw = Stopwatch.StartNew();
                    KmhApiServer.SendResult last = KmhApiServer.SendResult.Sent;
                    int queued = 0;
                    // Nobody reads the slow socket: once the OS buffers fill, every further frame waits in its queue.
                    for (int i = 0; i < 640 && last == KmhApiServer.SendResult.Sent; i++)
                        if ((last = slow.Send(big)) == KmhApiServer.SendResult.Sent) queued++;
                    long burstMs = sw.ElapsedMilliseconds;

                    sw.Restart();
                    KmhApiServer.SendResult toQuick = quick.Send(KmhApiServer.Encode(new KmhEnvelope(Kind, new { seq = 1 }), false));
                    bool arrived = ReadFrame(cliQuick, 3000) != null;
                    long quickMs = sw.ElapsedMilliseconds;

                    bool dropped = last == KmhApiServer.SendResult.IoFailure && slow.IsDead
                                   && Stat("slow_peers_dropped") > droppedBefore;
                    bool ok = burstMs < 2000 && toQuick == KmhApiServer.SendResult.Sent && arrived && quickMs < 2000 && dropped;
                    return (ok, $"{queued} frames ({queued * big.Bytes.Length / 1024} KB) handed off in {burstMs} ms with nobody reading; "
                              + $"the other player's frame arrived in {quickMs} ms (arrived={arrived}); slow player dropped: {dropped} ({last})");
                }
                finally { slow.Close(); quick.Close(); Close(cliSlow, cliQuick, srvSlow, srvQuick); }
            });

            Check("Transport: frames to one player arrive in the order sent, and a backlog goes out in shared writes", () =>
            {
                var (srv, cli) = Pair();
                KmhApiServer.ApiConn conn = new KmhApiServer.ApiConn(srv, srv.GetStream(), "selftest-order", null);
                try
                {
                    // Coalescing only happens behind a backlog, so large frames fill the buffers first.
                    const int large = 12, small = 200;
                    var frames = new List<KmhApiServer.Framed>();
                    for (int i = 0; i < large; i++)
                        frames.Add(KmhApiServer.Encode(new KmhEnvelope(Kind, new { seq = i, pad = new string('y', 60_000) }), false));
                    for (int i = 0; i < small; i++)
                        frames.Add(KmhApiServer.Encode(new KmhEnvelope(Kind, new { seq = large + i }), false));

                    long writesBefore = Stat("socket_writes");
                    for (int i = 0; i < frames.Count; i++)
                        if (conn.Send(frames[i]) != KmhApiServer.SendResult.Sent) return (false, $"send {i} was refused");

                    for (int i = 0; i < frames.Count; i++)
                    {
                        KmhEnvelope got = ReadFrame(cli, 5000);
                        if (got == null) return (false, $"frame {i} never arrived");
                        if (got.GetInt("seq", -1) != i) return (false, $"frame {i} carried seq {got.GetInt("seq", -1)}");
                    }
                    conn.WaitDrained(TimeSpan.FromSeconds(2));
                    long writes = Stat("socket_writes") - writesBefore;
                    return (writes < frames.Count / 2, $"{frames.Count} frames in order, in {writes} socket write(s)");
                }
                finally { conn.Close(); Close(cli, srv); }
            });

            // A catalog packed past a client's 64 KB unpack limit would drop its link on every push.
            Check("Transport: a payload that unpacks past what the client takes goes as fragments, not one frame", () =>
            {
                KmhApiServer.Framed packed = KmhApiServer.Encode(new KmhEnvelope(Kind, new { rows = Rows(4000) }), true);
                if (packed == null || !packed.Compressed || packed.BodyBytes > 64 * 1024 || packed.RawBytes <= 64 * 1024)
                    return (false, "the fixture did not pack into one frame while unpacking past it");

                var (srvOld, cliOld) = Pair();
                var (srvNew, cliNew) = Pair();
                var older = new KmhApiServer.ApiConn(srvOld, srvOld.GetStream(), "selftest-older", null)
                    { InflateLimitBytes = KmhApiServer.InflateLimitFor(0, 64), PeerDeflate = true, PeerFragments = true };
                var newer = new KmhApiServer.ApiConn(srvNew, srvNew.GetStream(), "selftest-newer", null)
                    { InflateLimitBytes = KmhApiServer.InflateLimitFor(8192, 64), PeerDeflate = true, PeerFragments = true };
                try
                {
                    KmhApiServer.SendResult toOlder = older.Send(packed);
                    KmhApiServer.SendResult toNewer = newer.Send(packed);
                    KmhEnvelope got = toNewer == KmhApiServer.SendResult.Sent ? ReadFrame(cliNew, 5000) : null;
                    bool ok = toOlder == KmhApiServer.SendResult.TooLarge && got != null && got.Kind == Kind;
                    return (ok, $"{packed.RawBytes / 1024} KB packed to {packed.BodyBytes / 1024} KB: a client that never said "
                              + $"what it unpacks -> {toOlder} (TooLarge = fragments); one that unpacks 8 MB -> {toNewer}, "
                              + $"arrived whole: {got != null}");
                }
                finally { older.Close(); newer.Close(); Close(cliOld, srvOld, cliNew, srvNew); }
            });

            Check("Transport: this server unpacks what it tells clients, and refuses a frame that expands past it", () =>
            {
                var (srv, cli) = Pair();
                try
                {
                    NetworkStream toServer = cli.GetStream();
                    byte[] big = Encoding.UTF8.GetBytes(new KmhEnvelope(Kind, new { rows = Rows(4000) }).Serialize());
                    byte[] bomb = new byte[KmhApiServer.MaxInflateBytes + 1];
                    Write(toServer, KmhFrameCodec.Deflate(big));
                    Write(toServer, KmhFrameCodec.Deflate(bomb));

                    NetworkStream fromClient = srv.GetStream();
                    KmhEnvelope first = KmhApiServer.ReadFrameForTest(fromClient, CancellationToken.None).GetAwaiter().GetResult();
                    KmhEnvelope second = KmhApiServer.ReadFrameForTest(fromClient, CancellationToken.None).GetAwaiter().GetResult();
                    bool ok = first != null && first.Kind == Kind && second == null;
                    return (ok, $"{big.Length / 1024} KB unpacked from one frame: {(first != null ? "read" : "REFUSED")}; "
                              + $"{bomb.Length / 1024} KB of zeroes: {(second == null ? "refused" : "INFLATED")}");
                }
                finally { Close(cli, srv); }
            });

            Check("Transport: a read that finishes in time leaves no timer behind", () =>
            {
                long before = Timer.ActiveCount;
                for (int i = 0; i < 2000; i++)
                    if (!KmhApiServer.FinishesWithin(Task.CompletedTask, 60_000, CancellationToken.None).GetAwaiter().GetResult())
                        return (false, "a finished read was reported as timed out");
                long after = Timer.ActiveCount;
                bool timesOut = !KmhApiServer.FinishesWithin(new TaskCompletionSource<bool>().Task, 30, CancellationToken.None)
                                             .GetAwaiter().GetResult();
                return (after - before < 100 && timesOut,
                        $"timers before {before}, after 2000 reads {after}; a read that never finishes still times out: {timesOut}");
            });

            return r;
        }

        // Shaped like a catalog: repeated keys and short values, which is what packs 90% and more.
        private static List<object> Rows(int n)
        {
            var rows = new List<object>(n);
            for (int i = 0; i < n; i++)
                rows.Add(new { id = i, defName = "Steel", stuff = (string)null, qty = i % 97, seller = "player_" + (i % 13), price = "12.50" });
            return rows;
        }

        private static void Write(NetworkStream s, byte[] packed)
        {
            byte[] frame = KmhFrameCodec.Frame(packed, true);
            s.Write(frame, 0, frame.Length);
        }

        private static bool SameFrame(byte[] frame, byte[] body, bool compressed)
            => frame.Length == body.Length + 4 && Header(frame, out int n, out bool z) && n == body.Length && z == compressed
               && frame.Skip(4).SequenceEqual(body);

        private static bool Header(byte[] frame, out int length, out bool compressed)
        {
            length = 0; compressed = false;
            if (frame == null || frame.Length < 4) return false;
            KmhFrameCodec.ReadHeader(frame, out length, out compressed);
            return true;
        }

        private static long Stat(string name)
        {
            foreach (KeyValuePair<string, long> kv in KmhTransportStats.Snapshot()) if (kv.Key == name) return kv.Value;
            return -1;
        }

        private static (TcpClient Server, TcpClient Client) Pair()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            TcpClient client = new TcpClient { ReceiveBufferSize = 64 * 1024 };
            client.Connect(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
            TcpClient server = l.AcceptTcpClient();
            l.Stop();
            server.SendBufferSize = 64 * 1024;
            server.NoDelay = true;
            return (server, client);
        }

        private static KmhEnvelope ReadFrame(TcpClient c, int timeoutMs)
        {
            c.ReceiveTimeout = timeoutMs;
            NetworkStream s = c.GetStream();
            byte[] head = ReadExactly(s, 4);
            if (head == null) return null;
            KmhFrameCodec.ReadHeader(head, out int len, out bool compressed);
            byte[] body = ReadExactly(s, len);
            if (body == null) return null;
            if (compressed) body = KmhFrameCodec.Inflate(body, 8 * 1024 * 1024);
            return body == null ? null : KmhEnvelope.TryParse(Encoding.UTF8.GetString(body));
        }

        private static byte[] ReadExactly(NetworkStream s, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            try
            {
                while (read < count)
                {
                    int n = s.Read(buf, read, count - read);
                    if (n <= 0) return null;
                    read += n;
                }
            }
            catch (System.IO.IOException) { return null; }
            return buf;
        }

        private static void Close(params TcpClient[] clients)
        {
            foreach (TcpClient c in clients) { try { c.Close(); } catch { } }
        }
    }
}
