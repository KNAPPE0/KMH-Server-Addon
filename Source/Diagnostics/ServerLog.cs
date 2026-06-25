using System;

namespace KMHServerAddon.Diagnostics
{
    // Thin wrapper around RWT's Printer that prepends [KMH-Addon]. Our bootstrap runs BEFORE RWT initializes the
    // logger, so each call is try-caught and falls back to Console.WriteLine until normal logging is up.
    internal static class ServerLog
    {
        public static void Info(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Message(line); }
            catch { Console.WriteLine(line); }
        }

        public static void Verbose(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Message(line, Printer.Verbosity.Verbose); }
            catch { /* verbose drops silently when the logger isn't up */ }
        }

        public static void Warn(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Warning(line); }
            catch { Console.WriteLine($"[WARN] {line}"); }
        }

        public static void Error(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Error(line); }
            catch { Console.Error.WriteLine($"[ERROR] {line}"); }
        }

        public static void Error(string message, Exception ex)
        {
            Error($"{message}: {ex}");
        }

        // milestone; green only on the pre-logger console fallback
        public static void Success(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Message(line); }
            catch { WriteColored(ConsoleColor.Green, line); }
        }

        // verbose, gated by DebugEnabled
        public static void Debug(string message)
        {
            if (!DebugEnabled) return;
            string line = $"{Constants.LogPrefix} [debug] {message}";
            try { Printer.Message(line, Printer.Verbosity.Verbose); }
            catch { /* drops until the logger is up */ }
        }

        // per-packet trace, gated by DebugEnabled
        public static void Protocol(string message)
        {
            if (!DebugEnabled) return;
            string line = $"{Constants.LogPrefix} [proto] {message}";
            try { Printer.Message(line, Printer.Verbosity.Verbose); }
            catch { Console.WriteLine(line); }
        }

        // gates Debug/Protocol
        public static bool DebugEnabled { get; set; }

        // colour + restore; pre-logger fallback only
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
