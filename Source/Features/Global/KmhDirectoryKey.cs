using System;
using System.Threading;
using System.Threading.Tasks;
using KMHServerAddon.Diagnostics;
using KMHServerAddon.Persistence;
using Newtonsoft.Json.Linq;

namespace KMHServerAddon.Features.Global
{
    internal sealed class DirectoryKeyFile
    {
        public int    SchemaVersion { get; set; } = 1;
        public string Algorithm     { get; set; } = "";
        public string PublicKey     { get; set; } = "";
        public string Fingerprint   { get; set; } = "";
        public string FetchedUtc    { get; set; } = "";
        // Empty in older pins, which are therefore fetched again.
        public string Origin        { get; set; } = "";
    }

    // On disk because a restart during a Global outage must not leave every player unverifiable.
    internal static class KmhDirectoryKey
    {
        private static readonly object _lock = new object();
        private static DirectoryKeyFile _file;
        private static int _fetching;

        private static DirectoryKeyFile File_
        {
            get
            {
                lock (_lock)
                {
                    if (_file != null) return _file;
                    _file = JsonFileStore.TryLoad(KmhDataPaths.DirectoryKeyFile, out DirectoryKeyFile loaded) && loaded != null
                          ? loaded : new DirectoryKeyFile();
                    return _file;
                }
            }
        }

        // A pin binds to its directory, or a server moved between directories keeps accepting the old one's tickets.
        private static DirectoryKeyFile CurrentLocked
            => string.Equals(File_.Origin, OriginOf(GlobalConfig.Current.BaseUrl), StringComparison.Ordinal) ? File_ : Unpinned;
        private static readonly DirectoryKeyFile Unpinned = new DirectoryKeyFile();

        public static string PublicKey   { get { lock (_lock) return CurrentLocked.PublicKey; } }
        public static string Fingerprint { get { lock (_lock) return CurrentLocked.Fingerprint; } }
        public static bool   Known       => PublicKey.Length > 0;

        internal static string OriginOf(string baseUrl)
            => Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out Uri u) ? u.GetLeftPart(UriPartial.Authority).ToLowerInvariant() : "";

        public static void Reload() { lock (_lock) _file = null; }

        // Fetched once and pinned: a silent key swap is the shape a compromise would take.
        public static async Task EnsureAsync(CancellationToken ct)
        {
            if (Known) return;
            if (Interlocked.Exchange(ref _fetching, 1) != 0) return;
            try
            {
                string baseUrl = GlobalConfig.Current.BaseUrl;
                KmhGlobalClient client = new KmhGlobalClient(baseUrl);
                GlobalCallResult r = await client.GetAsync("/api/v1/directory/key", ct).ConfigureAwait(false);
                if (!r.Ok || r.Body == null)
                {
                    ServerLog.Verbose($"Global: could not fetch the directory key ({r.ErrorCode}); join tickets stay unverified for now.");
                    return;
                }
                Adopt(r.Body, OriginOf(baseUrl));
            }
            catch (Exception ex) { ServerLog.Verbose($"Global: directory key fetch failed ({ex.GetType().Name})."); }
            finally { Volatile.Write(ref _fetching, 0); }
        }

        internal static bool Adopt(JObject body, string origin = null)
        {
            origin = origin ?? OriginOf(GlobalConfig.Current.BaseUrl);
            string pub = (string)body["public_key"] ?? "";
            string fp  = (string)body["fingerprint"] ?? "";
            string alg = (string)body["algorithm"] ?? "";
            if (pub.Length == 0 || fp.Length == 0) return false;

            // Recomputed, never believed: a fingerprint that travelled beside the key proves nothing.
            if (!string.Equals(KmhGlobalSigning.Fingerprint(pub), fp, StringComparison.Ordinal))
            {
                ServerLog.Warn("Global: directory key fingerprint does not match the key it came with - refused.");
                return false;
            }

            lock (_lock)
            {
                // Only the same directory's pin is held against a new key; another directory's is replaced.
                string had = string.Equals(File_.Origin, origin, StringComparison.Ordinal) ? File_.Fingerprint : "";
                if (File_.Fingerprint.Length > 0 && had.Length == 0 && !string.Equals(File_.Fingerprint, fp, StringComparison.Ordinal))
                    ServerLog.Warn($"Global: replacing the directory key pinned for {(File_.Origin.Length > 0 ? File_.Origin : "an unrecorded directory")} with {origin}'s.");
                if (had.Length > 0 && !string.Equals(had, fp, StringComparison.Ordinal))
                {
                    ServerLog.Error($"Global: the directory key CHANGED (was {KmhGlobalSigning.ShortFingerprint(had)}, "
                                  + $"now {KmhGlobalSigning.ShortFingerprint(fp)}). Keeping the pinned one; join "
                                  + "tickets signed by the new key will not verify until an owner clears "
                                  + $"{System.IO.Path.GetFileName(KmhDataPaths.DirectoryKeyFile)}.");
                    return false;
                }

                _file = new DirectoryKeyFile
                {
                    Algorithm = alg, PublicKey = pub, Fingerprint = fp,
                    FetchedUtc = DateTime.UtcNow.ToString("o"), Origin = origin,
                };
                if (!JsonFileStore.Save(KmhDataPaths.DirectoryKeyFile, _file))
                    ServerLog.Warn("Global: the directory key could not be written; it is held in memory only.");
            }

            ServerLog.Info($"Global: directory key pinned ({KmhGlobalSigning.ShortFingerprint(fp)}).");
            return true;
        }

        internal static void ResetForTest() { lock (_lock) _file = new DirectoryKeyFile(); }
    }
}
