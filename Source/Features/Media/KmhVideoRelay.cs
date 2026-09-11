using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Media
{
    // A request can only name a token this server minted; it never names a path.
    internal static class KmhVideoRelay
    {
        private const int HeaderLimit = 8 * 1024;
        private const int SocketTimeoutMs = 60_000;
        private const int IdleTimeoutMs = 15_000;
        private const int MaxSources = 256;
        private const int CopyBufferBytes = 64 * 1024;
        private static readonly TimeSpan SourceLife = TimeSpan.FromHours(12);

        private sealed class Source
        {
            public string File = "";
            public string ContentType = "video/mp4";
            public DateTime Minted = DateTime.UtcNow;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Source> Sources = new Dictionary<string, Source>(StringComparer.Ordinal);
        private static readonly List<string> Order = new List<string>();
        // On the shared port the API drops its reservation when it hands the socket over, so the relay needs its own.
        private static Transport.ConnectionLimiter _limiter =
            new Transport.ConnectionLimiter(DefaultMaxConnections, DefaultMaxPerIp);

        internal const int DefaultMaxConnections = 64;
        internal const int DefaultMaxPerIp       = 8;

        internal static int ActiveConnections => _limiter.Total;
        internal static int ActiveFor(string ip) => _limiter.PerIp(ip ?? "");

        // Seam for the self-test, which must be able to exhaust a small limit without opening 64 real sockets.
        internal static void ConfigureLimits(int maxTotal, int maxPerIp)
            => _limiter = new Transport.ConnectionLimiter(Math.Max(1, maxTotal), Math.Max(1, maxPerIp));

        internal static bool TryAdmit(string ip) => _limiter.TryAdmit(ip ?? "");
        internal static void ReleaseAdmit(string ip) => _limiter.Release(ip ?? "");

        private static string IpOf(TcpClient c)
        {
            try { return (c?.Client?.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? ""; }
            catch { return ""; }
        }

        // Shared by default: a second port is one an owner must know about, open and forward, and silently fails if not.
        private static bool _sharesApiPort;

        public static bool Running        => _sharesApiPort;
        public static bool SharesApiPort  => _sharesApiPort;
        public static int  Port           => _sharesApiPort ? Transport.KmhApiServer.Port : 0;

        public static void Start()
        {
            MediaConfig cfg = MediaConfig.Current;
            if (!cfg.VideoServerEnabled || Running) return;
            ConfigureLimits(cfg.VideoMaxConnections, cfg.VideoMaxConnectionsPerIp);
            if (!KmhVideoHelper.Available)
            {
                ServerLog.Info($"Video server: off - {KmhVideoHelper.MissingTools()} not found. Run "
                             + "Tools/get-media-tools.ps1 (or .sh on Linux) - it ships beside the server and "
                             + "downloads them - or put the binaries in that Tools folder yourself. Everything "
                             + "else runs normally without them.");
                return;
            }

            // The KMH transport is the only port KMH ever opens; video rides it rather than asking for a second.
            if (!Transport.KmhApiServer.Running)
            {
                ServerLog.Warn("Video server: the KMH API transport is not listening, so there is no port to serve video on. "
                             + "Enable EnableKmhApiTransport in Config/Transport.json.");
                return;
            }
            _sharesApiPort = true;
            ServerLog.Info($"Video server: serving on the KMH transport port {Transport.KmhApiServer.Port} - no extra port to open.");
        }

        public static void Stop()
        {
            lock (Gate)
            {
                Sources.Clear();
                Order.Clear();
                _sharesApiPort = false;
            }
        }

        public static string Publish(string file, string contentType)
        {
            if (!InCache(file) || !File.Exists(file) || !Running) return "";

            string token = NewToken();
            lock (Gate)
            {
                Prune();
                Sources[token] = new Source { File = file, ContentType = contentType ?? "video/mp4" };
                Order.Add(token);
                while (Order.Count > MaxSources) { Sources.Remove(Order[0]); Order.RemoveAt(0); }
            }
            return token;
        }

        // Only files this server wrote into its own video cache are ever served, whatever a token is later paired with.
        internal static bool InCache(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            try
            {
                string full = System.IO.Path.GetFullPath(file);
                string root = System.IO.Path.GetFullPath(KmhVideoCache.Folder);
                if (!root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    root += System.IO.Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void Prune()
        {
            DateTime cutoff = DateTime.UtcNow - SourceLife;
            for (int i = Order.Count - 1; i >= 0; i--)
            {
                if (!Sources.TryGetValue(Order[i], out Source s) || s.Minted >= cutoff) continue;
                Sources.Remove(Order[i]);
                Order.RemoveAt(i);
            }
        }

        // Returns false at capacity so the caller can close the socket while still holding its own reservation.
        internal static bool TryServeAccepted(TcpClient client, byte[] sniffed, string ip)
        {
            if (!TryAdmit(ip)) { Refuse(client); return false; }
            _ = Task.Run(() => ServeAsync(client, sniffed, ip));
            return true;
        }

        // Refused before any request is parsed, so it says only that the relay is busy.
        private static void Refuse(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream net = client.GetStream())
                {
                    net.WriteTimeout = 2000;
                    byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    net.Write(b, 0, b.Length);
                }
            }
            catch { }
        }

        private static async Task ServeAsync(TcpClient client, byte[] sniffed, string ip)
        {
            try
            {
                using (client)
                using (NetworkStream net = client.GetStream())
                {
                    net.ReadTimeout = net.WriteTimeout = SocketTimeoutMs;
                    client.NoDelay = true;

                    bool first = true;
                    while (true)
                    {
                        // A kept-open connection holds a pool thread on its read, so an idle one is dropped sooner.
                        if (!first) net.ReadTimeout = IdleTimeoutMs;
                        first = false;

                        string head = ReadHead(net, sniffed);
                        sniffed = null;   // only the first request on this socket carries the sniffed prefix
                        if (head.Length == 0) return;
                        if (!ParseRequest(head, out string method, out string path)) { Status(net, 400, "Bad Request"); return; }
                        if (method != "GET" && method != "HEAD") { Status(net, 405, "Method Not Allowed"); return; }

                        Source src;
                        lock (Gate) Sources.TryGetValue(TokenOf(path), out src);
                        if (src == null || !File.Exists(src.File)) { Status(net, 404, "Not Found"); return; }

                        using (var file = new FileStream(src.File, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                         CopyBufferBytes, FileOptions.SequentialScan))
                        {
                            bool ranged = ParseRange(HeaderValue(head, "range"), file.Length, out long from, out long to);
                            byte[] headers = Encoding.ASCII.GetBytes(ResponseHead(ranged, from, to, file.Length, src.ContentType));
                            await net.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
                            if (method != "HEAD")
                            {
                                file.Seek(from, SeekOrigin.Begin);
                                if (!await CopyAsync(file, net, to - from + 1).ConfigureAwait(false)) return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { ServerLog.Verbose($"Video server: connection ended - {ex.Message}"); }
            finally { ReleaseAdmit(ip); }
        }

        // False when the span could not be sent whole - the connection is then spent, not reusable.
        private static async Task<bool> CopyAsync(Stream file, Stream net, long count)
        {
            var buffer = new byte[CopyBufferBytes];
            while (count > 0)
            {
                int want = (int)Math.Min(buffer.Length, count);
                int read = await file.ReadAsync(buffer, 0, want).ConfigureAwait(false);
                if (read <= 0) return false;
                await net.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                count -= read;
            }
            return true;
        }

        internal static bool ParseRequest(string head, out string method, out string path)
        {
            method = ""; path = "";
            if (string.IsNullOrEmpty(head)) return false;
            int eol = head.IndexOf('\n');
            string[] parts = (eol < 0 ? head : head.Substring(0, eol)).Trim().Split(' ');
            if (parts.Length < 2) return false;
            method = parts[0].ToUpperInvariant();
            path = parts[1];
            return method.Length > 0 && path.StartsWith("/", StringComparison.Ordinal);
        }

        internal static string TokenOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.TrimStart('/');
            int q = p.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) p = p.Substring(0, q);
            int dot = p.IndexOf('.');
            return dot >= 0 ? p.Substring(0, dot) : p;
        }

        internal static string HeaderValue(string head, string name)
        {
            if (string.IsNullOrEmpty(head)) return "";
            foreach (string raw in head.Split('\n'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                if (!string.Equals(raw.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                return raw.Substring(colon + 1).Trim();
            }
            return "";
        }

        internal static bool ParseRange(string value, long length, out long from, out long to)
        {
            from = 0; to = Math.Max(0, length - 1);
            if (string.IsNullOrEmpty(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

            string spec = value.Substring(6).Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;

            string lo = spec.Substring(0, dash).Trim(), hi = spec.Substring(dash + 1).Trim();
            if (lo.Length == 0)
            {
                if (!long.TryParse(hi, out long tail) || tail <= 0) return false;
                from = Math.Max(0, length - tail);
                return true;
            }
            if (!long.TryParse(lo, out from) || from < 0 || from >= length) { from = 0; return false; }
            if (hi.Length > 0 && long.TryParse(hi, out long end)) to = Math.Min(end, length - 1);
            return true;
        }

        internal static string ResponseHead(bool ranged, long from, long to, long length, string contentType)
        {
            var sb = new StringBuilder();
            sb.Append(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(to - from + 1).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            if (ranged) sb.Append("Content-Range: bytes ").Append(from).Append('-').Append(to).Append('/').Append(length).Append("\r\n");
            // Kept open: a player seeks by asking for another range, and a fresh connection per seek is a stall.
            sb.Append("Connection: keep-alive\r\nKeep-Alive: timeout=60\r\n\r\n");
            return sb.ToString();
        }

        private static void Status(Stream net, int code, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {text}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            net.Write(bytes, 0, bytes.Length);
        }

        // prefix carries the bytes the shared listener already consumed, or the request line arrives truncated.
        private static string ReadHead(Stream net, byte[] prefix)
        {
            var sb = new StringBuilder();
            if (prefix != null) foreach (byte b in prefix) sb.Append((char)b);
            var one = new byte[1];
            while (sb.Length < HeaderLimit)
            {
                if (net.Read(one, 0, 1) <= 0) break;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 3] == '\n') break;
            }
            return sb.ToString();
        }

        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
