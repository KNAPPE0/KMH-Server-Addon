using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Features.Global;
using KMHServerAddon.Util;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhBoundedHttpSelfTest
    {
        public static List<(string Name, bool Ok, string Detail)> Run()
        {
            List<(string, bool, string)> r = new List<(string, bool, string)>();
            Run(r, "bounded http: a normal answer is read whole", Normal);
            Run(r, "bounded http: a declared length over the cap is refused before any byte is read", DeclaredTooLarge);
            Run(r, "bounded http: a body with no length stops at the cap", ChunkedTooLarge);
            Run(r, "bounded http: a stalled body that honours cancellation times out", () => Stalled(honoursToken: true));
            Run(r, "bounded http: a stalled body that ignores cancellation still times out", () => Stalled(honoursToken: false));
            Run(r, "bounded http: the caller's cancellation is thrown, not reported as a timeout", CallerCancels);
            Run(r, "bounded http: a Global answer over the cap reads as unreachable, never parsed", GlobalClientRefusesOversized);
            Run(r, "bounded http: a Global request that times out reads as unreachable, never as a shutdown", GlobalTimeoutIsOffline);
            Run(r, "bounded http: a proxy's 502 in front of a stopped Global reads as unreachable; Global's own 503 is an answer", GlobalProxyFailureIsOffline);
            return r;
        }

        private static void Run(List<(string, bool, string)> r, string name, Func<Task<(bool, string)>> test)
        {
            try
            {
                // A regression here is a hang, so the suite itself must not wait for ever.
                Task<(bool, string)> running = Task.Run(test);
                if (!running.Wait(TimeSpan.FromSeconds(20))) { r.Add((name, false, "hung for 20 s")); return; }
                (bool ok, string detail) = running.GetAwaiter().GetResult();
                r.Add((name, ok, detail));
            }
            catch (Exception ex) { r.Add((name, false, ex.GetType().Name + ": " + ex.Message)); }
        }

        private static HttpResponseMessage With(HttpContent content) => new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

        private static Task<GlobalCallResult> PostProbe(CancellationToken ct)
            => new KmhGlobalClient("http://127.0.0.1:9").PostAsync("/api/v1/owner-links", new { probe = true }, null, ct);

        private static async Task<(bool, string)> Normal()
        {
            using HttpResponseMessage res = With(new StringContent("{\"ok\":true,\"name\":\"Küche\"}", Encoding.UTF8, "application/json"));
            (string text, KmhBodyFailure f) = await KmhBoundedHttp.ReadTextAsync(res, 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            return (f == KmhBodyFailure.None && text == "{\"ok\":true,\"name\":\"Küche\"}", $"{f} '{text}'");
        }

        private static async Task<(bool, string)> DeclaredTooLarge()
        {
            ExplodingContent content = new ExplodingContent();
            content.Headers.ContentLength = 10_000;
            using HttpResponseMessage res = With(content);
            (string text, KmhBodyFailure f) = await KmhBoundedHttp.ReadTextAsync(res, 1024, TimeSpan.FromSeconds(5), CancellationToken.None);
            return (f == KmhBodyFailure.TooLarge && text == null && !content.Read, $"{f}, body read: {content.Read}");
        }

        private static async Task<(bool, string)> ChunkedTooLarge()
        {
            EndlessStream endless = new EndlessStream();
            using HttpResponseMessage res = With(new StreamContent(endless));
            (byte[] bytes, KmhBodyFailure f) = await KmhBoundedHttp.ReadBytesAsync(res, 100_000, TimeSpan.FromSeconds(5), CancellationToken.None);
            return (f == KmhBodyFailure.TooLarge && bytes == null && res.Content.Headers.ContentLength == null && endless.Served <= 100_000 + 16 * 1024,
                    $"{f}, served {endless.Served} bytes");
        }

        private static async Task<(bool, string)> Stalled(bool honoursToken)
        {
            using HttpResponseMessage res = With(new StreamContent(new StalledStream(honoursToken)));
            Stopwatch sw = Stopwatch.StartNew();
            (string text, KmhBodyFailure f) = await KmhBoundedHttp.ReadTextAsync(res, 1024, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            sw.Stop();
            return (f == KmhBodyFailure.BodyTimeout && text == null && sw.Elapsed < TimeSpan.FromSeconds(5), $"{f} after {sw.ElapsedMilliseconds} ms");
        }

        private static async Task<(bool, string)> CallerCancels()
        {
            using HttpResponseMessage res = With(new StreamContent(new StalledStream(honoursToken: true)));
            using CancellationTokenSource caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try
            {
                await KmhBoundedHttp.ReadTextAsync(res, 1024, TimeSpan.FromSeconds(30), caller.Token);
                return (false, "returned instead of throwing");
            }
            catch (OperationCanceledException) { return (true, "OperationCanceledException"); }
        }

        // HttpClient's own timeout arrives as a cancellation, which the heartbeat loop must not take as shutdown.
        private static async Task<(bool, string)> GlobalTimeoutIsOffline()
        {
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> before = KmhGlobalClient.TransportForTest;
            try
            {
                KmhGlobalClient.TransportForTest = (req, ct) =>
                    Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                                                                                      new TimeoutException()));
                GlobalCallResult timedOut = await PostProbe(CancellationToken.None);

                // The caller's own cancellation is still a cancellation: that one is a real shutdown.
                bool callerCancelThrows = false;
                using (CancellationTokenSource stop = new CancellationTokenSource())
                {
                    stop.Cancel();
                    KmhGlobalClient.TransportForTest = (req, ct) => Task.FromCanceled<HttpResponseMessage>(ct);
                    try { await PostProbe(stop.Token); }
                    catch (OperationCanceledException) { callerCancelThrows = true; }
                }
                bool ok = !timedOut.Ok && timedOut.Unreachable && timedOut.ErrorCode == "timeout" && callerCancelThrows;
                return (ok, $"timeout -> unreachable={timedOut.Unreachable} code={timedOut.ErrorCode}; caller cancel throws={callerCancelThrows}");
            }
            finally { KmhGlobalClient.TransportForTest = before; }
        }

        private static async Task<(bool, string)> GlobalProxyFailureIsOffline()
        {
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> before = KmhGlobalClient.TransportForTest;
            try
            {
                KmhGlobalClient.TransportForTest = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                    { Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html") });
                GlobalCallResult proxy = await PostProbe(CancellationToken.None);

                KmhGlobalClient.TransportForTest = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{\"error\":{\"code\":\"KMH-SRV-503\"}}", Encoding.UTF8, "application/json") });
                GlobalCallResult global = await PostProbe(CancellationToken.None);

                bool ok = proxy.Unreachable && proxy.ErrorCode == "gateway 502" && !global.Unreachable && global.Status == 503 && global.ErrorCode == "KMH-SRV-503";
                return (ok, $"proxy 502 -> unreachable={proxy.Unreachable} code={proxy.ErrorCode}; Global 503 -> unreachable={global.Unreachable} code={global.ErrorCode}");
            }
            finally { KmhGlobalClient.TransportForTest = before; }
        }

        private static async Task<(bool, string)> GlobalClientRefusesOversized()
        {
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> before = KmhGlobalClient.TransportForTest;
            bool parsed = false;
            try
            {
                KmhGlobalClient.TransportForTest = (req, ct) =>
                {
                    ExplodingContent big = new ExplodingContent();
                    big.Headers.ContentLength = KmhGlobalClient.MaxResponseBytes + 1L;
                    return Task.FromResult(With(big));
                };
                GlobalCallResult res = await PostProbe(CancellationToken.None);
                parsed = res.Body != null;
                return (!res.Ok && res.Unreachable && res.ErrorCode == "response-too-large" && !parsed, $"ok={res.Ok} status={res.Status} code={res.ErrorCode}");
            }
            finally { KmhGlobalClient.TransportForTest = before; }
        }

        // Fails the test if anything reads it: a declared length over the cap must be refused on the header alone.
        private sealed class ExplodingContent : HttpContent
        {
            public bool Read;
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) { Read = true; throw new InvalidOperationException("read"); }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
        }

        private sealed class EndlessStream : Stream
        {
            public long Served;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Served; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'x', offset, count); Served += count; return count; }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class StalledStream : Stream
        {
            private readonly bool _honours;
            public StalledStream(bool honoursToken) => _honours = honoursToken;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                => _honours ? Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => 0, ct) : new TaskCompletionSource<int>().Task;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
