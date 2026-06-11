using System;

namespace KMHServerAddon.Diagnostics
{
    // Thin wrapper around RWT's Printer that prepends [KMH-Addon] consistently.
    //
    // Important: our bootstrap runs BEFORE RWT calls ServerPrinter.CreateLogger, so any Printer.* call from inside
    // our addon's startup path would NPE on the uninitialized OnMessage/OnWarning/OnError actions. We try-catch
    // each call and fall back to plain Console.WriteLine so the early bootstrap stays loggable. Once RWT's Main
    // starts up the logger, normal output resumes
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
    }
}
