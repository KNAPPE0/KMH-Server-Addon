using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KMHServerAddon.Persistence;
using KMHServerAddon.SubProtocol;

namespace KMHServerAddon.Diagnostics
{
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
            public int    HighestSeq = -1;
            public readonly List<string> Gaps = new List<string>();
        }

        private static readonly object _lock = new object();
        // Keyed on the connection, not the name, or a reconnect inherits the old session's exhausted caps and file.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, UserState> _sessions
            = new System.Runtime.CompilerServices.ConditionalWeakTable<ServerClient, UserState>();
        // Re-prune daily rather than once per process, or a server up for months keeps files past the retention.
        private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(24);
        private static long _lastPruneTicks;

        public static void Register() => KmhRouter.RegisterHandler(KmhProtocol.Kind.DebugLog, OnPush);

        internal static bool HasStateForTest(ServerClient c)
        {
            lock (_lock) return c != null && _sessions.TryGetValue(c, out _);
        }

        internal static long BytesForTest(ServerClient c)
        {
            lock (_lock) return c != null && _sessions.TryGetValue(c, out UserState s) ? s.Bytes : -1;
        }

        internal static void AppendForTest(ServerClient c, string user, List<string> lines, string sessionId, long seq)
            => Append(c, user, lines, sessionId, seq);

        private static void OnPush(ServerClient client, KmhEnvelope env)
        {
            string user = client?.GetData<UserFile>()?.Username;
            Dto.DebugLogPush push = env?.DataAs<Dto.DebugLogPush>();
            List<string> lines = push?.Lines;
            if (client == null || string.IsNullOrEmpty(user) || lines == null || lines.Count == 0) return;
            // A packet from a connection that is no longer the live one for this name must not write anywhere.
            if (!SubProtocol.KmhRouter.IsLive(client)) return;

            try { Append(client, user, lines, push.SessionId ?? "", push.Sequence); }
            catch (Exception ex) { ServerLog.Diag($"Client debug log ({user}): {ex.Message}"); }
        }

        private static void Append(ServerClient client, string user, List<string> lines, string sessionId, long seq)
        {
            StringBuilder sb = new StringBuilder();
            lock (_lock)
            {
                long nowPrune = DateTime.UtcNow.Ticks;
                if (nowPrune - _lastPruneTicks > PruneEvery.Ticks) { _lastPruneTicks = nowPrune; PruneOld(); }

                if (!_sessions.TryGetValue(client, out UserState s))
                {
                    string dir = Path.Combine(KmhDataPaths.Folder, "Debug");
                    Directory.CreateDirectory(dir);
                    // The client's own session id, so its local log and this one name the same run without matching timestamps.
                    string stamp = string.IsNullOrEmpty(sessionId)
                        ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                        : Sanitize(sessionId);
                    s = new UserState { Path = Path.Combine(dir, $"{Sanitize(user)}_{stamp}.txt") };
                    _sessions.Add(client, s);
                    File.AppendAllText(s.Path,
                        $"# KMH client debug log for {user} - session opened {DateTime.UtcNow:o} (server {KmhServerIdentity.Name}, build {SubProtocol.KmhProtocol.BuildVersion}, client session {(string.IsNullOrEmpty(sessionId) ? "unknown" : sessionId)}){Environment.NewLine}");
                    ServerLog.Info($"Client debug log: receiving from {user} -> {Path.GetFileName(s.Path)}");
                }

                // The stream's own numbering, so a file with a hole in it is not read as the whole story.
                if (seq > 0)
                {
                    int n = (int)Math.Min(int.MaxValue, seq);
                    if (s.HighestSeq >= 0 && n > s.HighestSeq + 1 && s.Gaps.Count < 32)
                    {
                        string gap = s.HighestSeq + 1 == n - 1 ? $"{n - 1}" : $"{s.HighestSeq + 1}-{n - 1}";
                        s.Gaps.Add(gap);
                        sb.AppendLine($"--- diagnostic stream incomplete: missing chunk(s) {gap} ---");
                    }
                    if (n > s.HighestSeq) s.HighestSeq = n;
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
                            // Written into the file itself, or a reader cannot tell a truncated session from a short one.
                            bool sizeCap = s.Bytes >= MaxFileBytes;
                            sb.AppendLine("--- KMH DEBUG UPLOAD TRUNCATED ---");
                            sb.AppendLine($"Reason       : {(sizeCap ? "per-session size limit reached" : "per-minute line rate exceeded")}");
                            sb.AppendLine($"AcceptedBytes: {s.Bytes}");
                            sb.AppendLine($"TimestampUtc : {DateTime.UtcNow:o}");
                            sb.AppendLine("Local logging continues; further remote lines are not uploaded.");
                            ServerLog.Warn($"Client debug log: {user} hit the {(sizeCap ? "size" : "rate")} cap - dropping excess lines.");
                        }
                        break;
                    }
                    string line = KmhLogText.OneLine(raw, MaxLineChars);
                    sb.Append('[').Append(DateTime.UtcNow.ToString("o")).Append("] ").AppendLine(line);
                    s.WindowCount++;
                    if (s.WindowCount < LinesPerMinute) s.CapWarned = false;
                }

                if (sb.Length > 0)
                {
                    string text = sb.ToString();
                    File.AppendAllText(s.Path, text);
                    // Bytes, not chars: AppendAllText writes UTF-8, so multi-byte lines would overshoot a char count.
                    s.Bytes += Encoding.UTF8.GetByteCount(text);
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
