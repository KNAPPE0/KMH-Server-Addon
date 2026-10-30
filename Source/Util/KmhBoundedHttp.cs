using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KMHServerAddon.Util
{
    internal enum KmhBodyFailure { None, TooLarge, BodyTimeout, Unreadable }

    // Chunked reads stop at the cap whatever the headers claim, and the body gets its own deadline because a client
    // timeout ends once the headers are in.
    internal static class KmhBoundedHttp
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public static async Task<(string Text, KmhBodyFailure Failure)> ReadTextAsync(HttpResponseMessage res, int maxBytes,
                                                                                    TimeSpan bodyTimeout, CancellationToken ct)
        {
            (byte[] bytes, KmhBodyFailure failure) = await ReadBytesAsync(res, maxBytes, bodyTimeout, ct).ConfigureAwait(false);
            if (failure != KmhBodyFailure.None) return (null, failure);
            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return (Utf8.GetString(bytes, skip, bytes.Length - skip), KmhBodyFailure.None);
        }

        public static async Task<(byte[] Bytes, KmhBodyFailure Failure)> ReadBytesAsync(HttpResponseMessage res, int maxBytes,
                                                                                       TimeSpan bodyTimeout, CancellationToken ct)
        {
            HttpContent content = res?.Content;
            if (content == null) return (Array.Empty<byte>(), KmhBodyFailure.None);
            if (content.Headers.ContentLength > maxBytes) return (null, KmhBodyFailure.TooLarge);

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(bodyTimeout);
            try
            {
                using Stream body = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using MemoryStream into = new MemoryStream();
                byte[] buffer = new byte[16 * 1024];
                int n;
                while ((n = await ReadOrDeadlineAsync(body, buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (into.Length + n > maxBytes) return (null, KmhBodyFailure.TooLarge);
                    into.Write(buffer, 0, n);
                }
                // A stream closed by the deadline can read as a clean end; what arrived by then is not the answer.
                deadline.Token.ThrowIfCancellationRequested();
                return (into.ToArray(), KmhBodyFailure.None);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, KmhBodyFailure.BodyTimeout); }
            catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) { return (null, KmhBodyFailure.BodyTimeout); }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                return (null, KmhBodyFailure.Unreadable);
            }
        }

        // A stream that ignores its cancellation token still cannot hold the caller past the deadline.
        private static async Task<int> ReadOrDeadlineAsync(Stream s, byte[] buffer, CancellationToken token)
        {
            Task<int> read = s.ReadAsync(buffer, 0, buffer.Length, token);
            if (read.IsCompleted) return await read.ConfigureAwait(false);
            TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(read, cancelled.Task).ConfigureAwait(false) != read)
                {
                    // Abandoned, so its eventual failure is observed here rather than surfacing as an unobserved task.
                    _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    token.ThrowIfCancellationRequested();
                }
            }
            return await read.ConfigureAwait(false);
        }

        public static string Describe(KmhBodyFailure f) => f switch
        {
            KmhBodyFailure.TooLarge => "response-too-large",
            KmhBodyFailure.BodyTimeout => "response-timeout",
            KmhBodyFailure.Unreadable => "response-unreadable",
            _ => "",
        };
    }
}
