using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    // A transport failure and a refusal differ: the first is retried, the second is reported with Global's code.
    internal sealed class GlobalCallResult
    {
        public bool   Ok { get; set; }
        public int    Status { get; set; }
        public string ErrorCode { get; set; } = "";
        public string CorrelationId { get; set; } = "";
        public Dictionary<string, string> Args { get; set; } = new Dictionary<string, string>();
        public JObject Body { get; set; }

        public bool Unreachable => Status == 0;

        public static GlobalCallResult Offline(string why)
            => new GlobalCallResult { Ok = false, Status = 0, ErrorCode = why };
    }

    // Thrown, not returned, so no caller can miss a self-test isolation refusal.
    internal sealed class KmhGlobalIsolationException : Exception
    {
        public KmhGlobalIsolationException(string why) : base(why) { }
    }

    internal sealed class KmhGlobalClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // Far above any Global answer; a proxy or hostile endpoint streaming more is cut off, not buffered.
        internal const int MaxResponseBytes = 4 * 1024 * 1024;
        // HttpClient's timeout stops at the headers (ResponseHeadersRead), so the body gets its own.
        internal static TimeSpan BodyTimeout = TimeSpan.FromSeconds(20);

        // Self-test seam; nothing else assigns it.
        internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> TransportForTest;

        // The only network exit for Global requests, so the isolation rule lives here.
        private static Task<HttpResponseMessage> Send(HttpRequestMessage req, string path, CancellationToken ct)
        {
            if (!KmhGlobalSafety.MayContact(req.RequestUri, out string why))
            {
                KmhGlobalSafety.NoteHeldBack(req.RequestUri, path, why);
                throw new KmhGlobalIsolationException(why);
            }
            return TransportForTest != null ? TransportForTest(req, ct) : Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        private readonly string _baseUrl;
        public KmhGlobalClient(string baseUrl) => _baseUrl = (baseUrl ?? "").TrimEnd('/');

        // A 502/503/504 with no KMH error code came from something in front of Global, not from it.
        internal static bool IsProxyFailure(GlobalCallResult r)
            => r != null && !r.Ok && (r.Status == 502 || r.Status == 503 || r.Status == 504) && string.IsNullOrEmpty(r.ErrorCode);

        public Task<GlobalCallResult> PostAsync(string path, object body, string bearer, CancellationToken ct)
            => SendAsync(HttpMethod.Post, path, body, bearer, ct);

        public Task<GlobalCallResult> PatchAsync(string path, object body, string bearer, CancellationToken ct)
            => SendAsync(new HttpMethod("PATCH"), path, body, bearer, ct);

        public Task<GlobalCallResult> PutAsync(string path, object body, CancellationToken ct)
            => SendAsync(HttpMethod.Put, path, body, null, ct);

        public Task<GlobalCallResult> GetAsync(string path, CancellationToken ct)
            => SendAsync(HttpMethod.Get, path, null, null, ct);

        // An owner link's id is the credential its poll redeems, so no log line carries it.
        internal static string Loggable(string path)
        {
            const string links = "/api/v1/owner-links/";
            if (path == null || !path.StartsWith(links, StringComparison.Ordinal) || path.Length == links.Length) return path;
            int end = path.IndexOf('/', links.Length);
            return links + "{id}" + (end < 0 ? "" : path.Substring(end));
        }

        private async Task<GlobalCallResult> SendAsync(HttpMethod method, string path, object body, string bearer, CancellationToken ct)
        {
            // The disclosure choke point: a caller that forgets to check cannot leak anything past here.
            if (!KmhGlobalDisclosure.MayPublish(path, out string why))
            {
                ServerLog.Verbose($"Global: not sending {Loggable(path)} - {why}.");
                return GlobalCallResult.Offline(why);
            }

            try
            {
                using HttpRequestMessage req = new HttpRequestMessage(method, _baseUrl + path);
                if (body != null)
                    req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(bearer))
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);

                using HttpResponseMessage res = await Send(req, path, ct).ConfigureAwait(false);
                (string text, KmhBodyFailure failure) = await KmhBoundedHttp.ReadTextAsync(res, MaxResponseBytes, BodyTimeout, ct).ConfigureAwait(false);
                if (failure != KmhBodyFailure.None)
                {
                    ServerLog.Verbose($"Global: {method} {Loggable(path)} answered {(int)res.StatusCode} but the body was not read: {KmhBoundedHttp.Describe(failure)}.");
                    return GlobalCallResult.Offline(KmhBoundedHttp.Describe(failure));
                }

                GlobalCallResult result = new GlobalCallResult { Status = (int)res.StatusCode, Ok = res.IsSuccessStatusCode };
                if (res.Headers.TryGetValues("X-KMH-Correlation-Id", out IEnumerable<string> ids))
                    foreach (string id in ids) { result.CorrelationId = id; break; }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        JObject parsed = JObject.Parse(text);
                        result.Body = parsed;
                        JToken err = parsed["error"];
                        if (err != null)
                        {
                            result.ErrorCode = (string)err["code"] ?? "";
                            if (string.IsNullOrEmpty(result.CorrelationId))
                                result.CorrelationId = (string)err["correlation_id"] ?? "";
                            if (err["args"] is JObject args)
                                foreach (KeyValuePair<string, JToken> kv in args)
                                    result.Args[kv.Key] = kv.Value?.ToString() ?? "";
                        }
                    }
                    catch (JsonException) { }   // a proxy error page is not a protocol violation worth throwing over
                }
                // A 502 from the proxy of a stopped Global must read as offline, not as Global saying no.
                if (IsProxyFailure(result)) return GlobalCallResult.Offline("gateway " + result.Status);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            // HttpClient's timeout surfaces as a cancellation; rethrown, loops would read it as shutdown and stop.
            catch (OperationCanceledException)
            {
                ServerLog.Verbose($"Global: {method} {Loggable(path)} timed out.");
                return GlobalCallResult.Offline("timeout");
            }
            catch (KmhGlobalIsolationException iso) { return GlobalCallResult.Offline(iso.Message); }
            catch (Exception ex)
            {
                // Never logged with the request body: a registration payload carries the owner's bearer token.
                ServerLog.Verbose($"Global: {method} {Loggable(path)} did not reach the service: {ex.Message}");
                return GlobalCallResult.Offline("unreachable");
            }
        }
    }
}
