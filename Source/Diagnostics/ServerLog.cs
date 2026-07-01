using System;

namespace KMHServerAddon.Diagnostics
{
    // Wrapper around RWT's Printer that prepends [KMH-Addon]. Normal KMH lines go through Printer.Title so they render
    // GREEN on the console (RWT's only spare file-safe colour - it's Console.ForegroundColor, so the log file stays
    // plain), standing out from RWT's white spam; warn/error keep RWT's yellow/red. Bootstrap runs before RWT's logger
    // is up, so each call is try-caught and falls back to a coloured Console.WriteLine until then.
    internal static class ServerLog
    {
        public static void Info(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Title(line); }
            catch { WriteColored(ConsoleColor.Green, line); }
        }

        public static void Verbose(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Title(line, Printer.Verbosity.Verbose); }
            catch { /* verbose drops silently when the logger isn't up */ }
        }

        public static void Warn(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Warning(line); }
            catch { WriteColored(ConsoleColor.Yellow, line); }
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

        // milestone
        public static void Success(string message)
        {
            string line = $"{Constants.LogPrefix} {message}";
            try { Printer.Title(line); }
            catch { WriteColored(ConsoleColor.Green, line); }
        }

        // verbose, gated by DebugEnabled
        public static void Debug(string message)
        {
            if (!DebugEnabled) return;
            string line = $"{Constants.LogPrefix} [debug] {message}";
            try { Printer.Title(line, Printer.Verbosity.Verbose); }
            catch { /* drops until the logger is up */ }
        }

        // per-packet trace, gated by DebugEnabled
        public static void Protocol(string message)
        {
            if (!DebugEnabled) return;
            string line = $"{Constants.LogPrefix} [proto] {message}";
            try { Printer.Title(line, Printer.Verbosity.Verbose); }
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
