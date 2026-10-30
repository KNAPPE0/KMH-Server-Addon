using System;
using System.Threading;
using System.Threading.Tasks;

namespace KMHServerAddon.Features.Global
{
    // The only transport for signed server posts, so one test fake covers every caller and no second path can drift.
    internal static class KmhGlobalPost
    {
        // Self-test seam (no network there); nothing else assigns it.
        internal static Func<string, object, CancellationToken, Task<GlobalCallResult>> SenderForTest;

        public static Task<GlobalCallResult> SendAsync(string path, object body, CancellationToken ct)
        {
            if (SenderForTest != null) return SenderForTest(path, body, ct);
            return new KmhGlobalClient(GlobalConfig.Current.BaseUrl).PostAsync(path, body, null, ct);
        }
    }
}
