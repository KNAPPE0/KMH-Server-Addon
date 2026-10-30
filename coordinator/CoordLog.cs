using System;
using System.Collections.Generic;

namespace KmhDiscordCoordinator
{
    // Stdout only: systemd captures it, and nothing here is ever worth a second log file to rotate.
    public static class CoordLog
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Secrets = new List<string>();

        // Registered once at boot so a token can never reach a line even through an exception message.
        public static void RegisterSecret(string secret)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 8) return;
            lock (Gate) Secrets.Add(secret);
        }

        public static void Info(string line)  => Write("INFO ", line);
        public static void Warn(string line)  => Write("WARN ", line);
        public static void Error(string line, Exception ex = null)
            => Write("ERROR", ex == null ? line : line + ": " + ex.Message);

        private static void Write(string level, string line)
        {
            string safe = Redact(line ?? "");
            lock (Gate) Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level} {safe}");
        }

        public static string Redact(string line)
        {
            lock (Gate)
                foreach (string s in Secrets)
                    if (line.Contains(s, StringComparison.Ordinal)) line = line.Replace(s, "<redacted>");
            return line;
        }
    }
}
