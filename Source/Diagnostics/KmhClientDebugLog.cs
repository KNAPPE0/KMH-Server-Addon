using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Diagnostics
{
    // Client KMH log lines -> KMH-Data/Debug/<user>_<stamp>.txt, hard-bounded (rate/line/file caps) against abuse.
    internal static class KmhClientDebugLog
    {
        private const int  MaxLineChars     = 400;
        private const int  LinesPerMinute   = 300;
        private const long MaxFileBytes     = 10L * 1024 * 1024;
        private const int  PruneOlderDays   = 14;

        private sealed class UserState
        {
            public string Path;
            public long   Bytes;
            public int    WindowCount;
            public long   WindowStartTicks;
            public bool   CapWarned;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, UserState> _users
            = new Dictionary<string, UserState>(StringComparer.OrdinalIgnoreCase);
        private static bool _pruned;

        public static void Register() => KmhRouter.RegisterHandler(KmhProtocol.Kind.DebugLog, OnPush);

        private static void OnPush(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            List<string> lines = env?.DataAs<Dto.DebugLogPush>()?.Lines;
            if (string.IsNullOrEmpty(user) || lines == null || lines.Count == 0) return;

            try { Append(user, lines); }
            catch (Exception ex) { ServerLog.Verbose($"Client debug log ({user}): {ex.Message}"); }
        }

        private static void Append(string user, List<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            lock (_lock)
            {
                if (!_pruned) { _pruned = true; PruneOld(); }

                if (!_users.TryGetValue(user, out UserState s))
                {
                    string dir = Path.Combine(KmhDataPaths.Folder, "Debug");
                    Directory.CreateDirectory(dir);
                    s = new UserState { Path = Path.Combine(dir, $"{Sanitize(user)}_{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt") };
                    _users[user] = s;
                    File.AppendAllText(s.Path,
                        $"# KMH client debug log for {user} - session opened {DateTime.UtcNow:o} (server {KmhServerIdentity.Name}, build {SubProtocol.KmhProtocol.BuildVersion}){Environment.NewLine}");
                    ServerLog.Info($"Client debug log: receiving from {user} -> {Path.GetFileName(s.Path)}");
                }

                long now = DateTime.UtcNow.Ticks;
                if (now - s.WindowStartTicks > TimeSpan.TicksPerMinute) { s.WindowStartTicks = now; s.WindowCount = 0; }

                foreach (string raw in lines)
                {
                    if (s.WindowCount >= LinesPerMinute || s.Bytes >= MaxFileBytes)
                    {
                        if (!s.CapWarned)
                        {
                            s.CapWarned = true;
                            sb.AppendLine($"[{DateTime.UtcNow:o}] -- rate/size cap hit; further lines dropped this window --");
                            ServerLog.Warn($"Client debug log: {user} hit the rate/size cap - dropping excess lines.");
                        }
                        break;
                    }
                    string line = (raw ?? "").Replace('\r', ' ').Replace('\n', ' ');
                    if (line.Length > MaxLineChars) line = line.Substring(0, MaxLineChars) + "…";
                    sb.Append('[').Append(DateTime.UtcNow.ToString("o")).Append("] ").AppendLine(line);
                    s.WindowCount++;
                    if (s.WindowCount < LinesPerMinute) s.CapWarned = false;
                }

                if (sb.Length > 0)
                {
                    File.AppendAllText(s.Path, sb.ToString());
                    s.Bytes += sb.Length;
                }
            }
        }

        private static void PruneOld()
        {
            try
            {
                string dir = Path.Combine(KmhDataPaths.Folder, "Debug");
                if (!Directory.Exists(dir)) return;
                DateTime cutoff = DateTime.UtcNow.AddDays(-PruneOlderDays);
                foreach (string f in Directory.GetFiles(dir, "*.txt"))
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                        try { File.Delete(f); } catch { }
            }
            catch { }
        }

        private static string Sanitize(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length == 0 ? "_" : sb.ToString();
        }
    }
}
