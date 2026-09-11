using System;

namespace KMHServerAddon.Diagnostics
{
    // Normal lines go through Printer.Title because green is the only colour RWT leaves free and file-safe.
    internal static class ServerLog
    {
        private static void ToFile(string tag, string message)
            => KmhLogSink.Write($"[{DateTime.UtcNow:O}] [{tag}] {KmhLogText.OneLine(message, 4000)}");

        // Player-controlled text reaches the console too, where an escape sequence would drive the terminal.
        private static string Safe(string message) => KmhLogText.OneLine(message, 4000);

        // The file always gets the line; ConsoleLogLevel only decides whether the terminal is also shown it.
        private static bool ToConsole(Maintenance.KmhConsoleLevel level)
        {
            try { return Maintenance.MaintenanceConfig.Current.ConsoleAllows(level); }
            catch { return true; }
        }

        public static void Info(string message)
        {
            string line = $"{Constants.LogPrefix} {Safe(message)}";
            ToFile("info", message);
            if (!ToConsole(Maintenance.KmhConsoleLevel.Info)) return;
            try { Printer.Title(line); }
            catch { WriteColored(ConsoleColor.Green, line); }
        }

        // Routine detail: the diagnostic file wants it, a live console watching a busy server does not.
        public static void Diag(string message)
            => ToFile("diag", message);

        // Same, for something that repeats on a timer. Repeats are counted and reported, not written.
        public static void Diag(string key, string message)
        {
            if (KmhLogThrottle.Allow(key, TimeSpan.FromMinutes(2), DateTime.UtcNow.Ticks, out int suppressed))
                ToFile("diag", suppressed > 0 ? $"{message}  (+{suppressed} identical since the last one)" : message);
        }

        public static void Verbose(string message)
        {
            string line = $"{Constants.LogPrefix} {Safe(message)}";
            ToFile("verbose", message);
            if (!ToConsole(Maintenance.KmhConsoleLevel.Info)) return;
            try { Printer.Title(line, Printer.Verbosity.Verbose); }
            catch { /* verbose drops silently when the logger isn't up */ }
        }

        public static void Warn(string message)
        {
            string line = $"{Constants.LogPrefix} {Safe(message)}";
            ToFile("warn", message);
            if (!ToConsole(Maintenance.KmhConsoleLevel.Warn)) return;
            try { Printer.Warning(line); }
            catch { WriteColored(ConsoleColor.Yellow, line); }
        }

        public static void Error(string message)
        {
            string line = $"{Constants.LogPrefix} {Safe(message)}";
            ToFile("error", message);
            try { Printer.Error(line); }
            catch { Console.Error.WriteLine($"[ERROR] {line}"); }
        }

        public static void Error(string message, Exception ex)
        {
            Error($"{message}: {ex}");
        }

        public static void Success(string message)
        {
            string line = $"{Constants.LogPrefix} {Safe(message)}";
            ToFile("info", message);
            if (!ToConsole(Maintenance.KmhConsoleLevel.Info)) return;
            try { Printer.Title(line); }
            catch { WriteColored(ConsoleColor.Green, line); }
        }

        // Always recorded in the file; the console copy is what the owner's debug flag actually controls.
        public static void Debug(string message)
        {
            ToFile("debug", message);
            if (!DebugEnabled) return;
            string line = $"{Constants.LogPrefix} [debug] {Safe(message)}";
            try { Printer.Title(line, Printer.Verbosity.Verbose); }
            catch { }
        }

        public static void Protocol(string message)
        {
            ToFile("protocol", message);
            if (!ProtocolConsoleEnabled) return;
            string line = $"{Constants.LogPrefix} [protocol] {Safe(message)}";
            try { Printer.Title(line, Printer.Verbosity.Verbose); }
            catch { Console.WriteLine(line); }
        }

        // Console verbosity only - deliberately NOT what gates a remote client uploading its own logs.
        public static bool DebugEnabled { get; set; }

        // Per-packet tracing is the loudest thing KMH can print, so it is its own switch rather than riding Debug.
        public static bool ProtocolConsoleEnabled { get; set; }

        private static void WriteColored(ConsoleColor colour, string line)
        {
            try
            {
                ConsoleColor prev = Console.ForegroundColor;
                Console.ForegroundColor = colour;
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
            catch { Console.WriteLine(line); }
        }
    }
}
