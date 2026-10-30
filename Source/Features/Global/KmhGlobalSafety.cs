using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Features.Global
{
    // BaseUrl defaults to the official directory and acceptance servers keep a real registration,
    // so a self-test reaches only a local test Global unless an operator opts in.
    internal static class KmhGlobalSafety
    {
        internal const string SelfTestEnvVar = "KMH_SELFTEST";
        internal const string OptInEnvVar    = "KMH_ALLOW_PRODUCTION_GLOBAL";

        // Null reads the environment; a value forces it so a suite can exercise both sides.
        internal static bool? SelfTestOverride;

        private static long _heldBack;
        private static readonly ConcurrentDictionary<string, bool> _warned =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static bool SelfTestActive =>
            SelfTestOverride ?? string.Equals(Env(SelfTestEnvVar), "1", StringComparison.Ordinal);

        public static bool ProductionOptIn =>
            string.Equals(Env(OptInEnvVar), "1", StringComparison.Ordinal);

        public static long HeldBack => Interlocked.Read(ref _heldBack);

        public static bool IsProduction(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return false;
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri uri)) return false;
            return string.Equals(uri.Host, OfficialHost, StringComparison.OrdinalIgnoreCase);
        }

        // Loopback, or a name reserved never to resolve to a real host (RFC 6761): nothing real is reachable there.
        public static bool IsLocalTestHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string h = host.Trim().Trim('[', ']').TrimEnd('.');
            if (IPAddress.TryParse(h, out IPAddress ip)) return IPAddress.IsLoopback(ip);
            return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || h.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
                || h.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
                || h.EndsWith(".example", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MayContact(Uri target, out string why)
        {
            why = "";
            if (!SelfTestActive || ProductionOptIn) return true;
            if (target != null && target.IsAbsoluteUri && IsLocalTestHost(target.Host)) return true;

            string host = target != null && target.IsAbsoluteUri ? target.Host : "an invalid address";
            string what = string.Equals(host, OfficialHost, StringComparison.OrdinalIgnoreCase)
                ? $"the production directory ({host})" : $"{host}, which is not a local test Global";
            why = $"self-test isolation: not contacting {what}. A self-test run reaches only a loopback, localhost "
                + $"or reserved test address. Point {nameof(GlobalConfig)}.BaseUrl at one, or set {OptInEnvVar}=1 "
                + "to allow it deliberately.";
            return false;
        }

        public static bool MayContact(string baseUrl, out string why)
        {
            Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out Uri uri);
            return MayContact(uri, out why);
        }

        // Asked up front so the console can say why nothing is listed; the client still enforces it.
        public static bool MayPublishTo(string baseUrl, out string why) => MayContact(baseUrl, out why);

        // Logged once per host so a polling loop does not flood the console.
        internal static void NoteHeldBack(Uri target, string path, string why)
        {
            Interlocked.Increment(ref _heldBack);
            string host = target != null && target.IsAbsoluteUri ? target.Host : "";
            if (_warned.TryAdd(host, true)) ServerLog.Warn($"Global: {why}");
            else ServerLog.Verbose($"Global: not sending {KmhGlobalClient.Loggable(path)} - held back by self-test isolation.");
        }

        private static string OfficialHost
        {
            get
            {
                return Uri.TryCreate(GlobalConfigCheck.OfficialBaseUrl, UriKind.Absolute, out Uri u)
                    ? u.Host : "global.knappe0.games";
            }
        }

        private static string Env(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); }
            catch (Exception) { return null; }
        }
    }
}
