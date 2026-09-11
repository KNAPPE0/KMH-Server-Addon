using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Media
{
    internal static class KmhMediaFetch
    {
        internal sealed class Result
        {
            public bool   Ok;
            public string Error = "";
            public byte[] Bytes;
            public string ContentType = "";
            public string FinalUrl = "";
            public int    Hops;
        }

        internal static async Task<Result> GetAsync(string url, MediaConfig cfg, CancellationToken outer)
        {
            var result = new Result();
            if (cfg == null) { result.Error = "no config"; return result; }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(outer);
            budget.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
            CancellationToken ct = budget.Token;

            string current = url;
            for (int hop = 0; hop <= cfg.MaxRedirects; hop++)
            {
                // A url that passes and then redirects elsewhere is how a host rule gets bypassed, so every hop is re-judged.
                if (!Allowed(current, cfg, out string why))
                {
                    result.Error = hop == 0 ? $"refused: {why}" : $"redirect refused: {why}";
                    return result;
                }

                Uri uri;
                try { uri = new Uri(current); }
                catch { result.Error = "unparseable url"; return result; }

                if (!KmhMediaGuard.ResolveAndCheck(uri.Host, out List<IPAddress> addresses, out string addrWhy))
                {
                    result.Error = $"destination refused: {addrWhy}";
                    return result;
                }

                using HttpClient http = BuildClient(addresses, cfg);
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                // Nothing here may identify the operator or carry authority from elsewhere in KMH.
                req.Headers.TryAddWithoutValidation("User-Agent", "KMH-Server-Addon media resolver");
                req.Headers.TryAddWithoutValidation("Accept", "image/*");

                HttpResponseMessage res;
                try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { result.Error = "timed out"; return result; }
                catch (Exception ex) { result.Error = "fetch failed: " + ex.GetBaseException().Message; return result; }

                using (res)
                {
                    int code = (int)res.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string location = res.Headers.Location?.ToString();
                        if (string.IsNullOrEmpty(location)) { result.Error = $"redirect {code} with no target"; return result; }
                        try { current = new Uri(uri, location).ToString(); }
                        catch { result.Error = "unfollowable redirect"; return result; }
                        result.Hops = hop + 1;
                        continue;
                    }

                    if (code != 200) { result.Error = $"HTTP {code}"; return result; }

                    // The header is only a claim, so it is trusted to refuse early but never to permit.
                    long? declared = res.Content.Headers.ContentLength;
                    if (declared.HasValue && declared.Value > cfg.MaxSourceBytes)
                    {
                        result.Error = $"source too large: {declared.Value} bytes over the {cfg.MaxSourceBytes} limit";
                        return result;
                    }

                    byte[] body;
                    try { body = await ReadCapped(res, cfg.MaxSourceBytes, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { result.Error = "timed out reading"; return result; }
                    catch (InvalidDataException ex) { result.Error = ex.Message; return result; }
                    catch (Exception ex) { result.Error = "read failed: " + ex.GetBaseException().Message; return result; }

                    result.Ok = true;
                    result.Bytes = body;
                    result.ContentType = res.Content.Headers.ContentType?.MediaType ?? "";
                    result.FinalUrl = uri.ToString();
                    return result;
                }
            }

            result.Error = $"too many redirects (over {cfg.MaxRedirects})";
            return result;
        }

        internal static bool AllowedForTest(string url, MediaConfig cfg, out string why) => Allowed(url, cfg, out why);

        // An empty allow-list is not "no control" - the address guard still refuses every private range.
        private static bool Allowed(string url, MediaConfig cfg, out string why)
        {
            why = "";
            Uri uri;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out uri) || uri == null) { why = "unparseable url"; return false; }
            if (uri.Scheme != Uri.UriSchemeHttps) { why = "https only"; return false; }

            string[] allowed = cfg?.SourceHostAllowList;
            if (allowed == null || allowed.Length == 0) return true;
            if (Chat.ChatImagePolicy.IsAllowedHost(uri.Host, allowed)) return true;

            why = $"host '{uri.Host}' is not in Media.json SourceHostAllowList";
            return false;
        }

        // Connects to an already-validated IP, so no second name lookup can move the target after the check.
        private static HttpClient BuildClient(List<IPAddress> validated, MediaConfig cfg)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect       = false,   // every hop is judged by the policy above, never by HttpClient
                UseCookies              = false,
                UseProxy                = false,
                Credentials             = null,
                AutomaticDecompression  = DecompressionMethods.All,
                ConnectTimeout          = TimeSpan.FromSeconds(Math.Min(10, cfg.TimeoutSeconds)),
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            };

            handler.ConnectCallback = async (context, token) =>
            {
                Exception last = null;
                foreach (IPAddress ip in validated)
                {
                    // Re-checked at the moment of connecting, not just when it was resolved.
                    if (!KmhMediaGuard.IsPublicAddress(ip, out string why))
                    {
                        last = new IOException($"refused {ip}: {why}");
                        continue;
                    }
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) { socket.Dispose(); last = ex; }
                }
                throw last ?? new IOException("no usable address");
            };

            return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds) };
        }

        // Capped as it reads: buffering the whole body first would hold the very thing the cap exists to refuse.
        private static async Task<byte[]> ReadCapped(HttpResponseMessage res, int cap, CancellationToken ct)
        {
            using Stream stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > cap)
                    throw new InvalidDataException($"source too large: over the {cap} byte limit");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
