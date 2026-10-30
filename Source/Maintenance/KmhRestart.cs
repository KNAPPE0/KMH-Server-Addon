using System;
using System.Collections.Generic;
using System.Diagnostics;
using KMHServerAddon.Diagnostics;

namespace KMHServerAddon.Maintenance
{
    internal static class KmhRestart
    {
        // Distinct from a crash so a unit file can force a restart on it without treating it as a failure.
        public const int ExitCode = 42;

        // Set on the replacement so it waits for this process to release the listening ports before it binds.
        private const string DelayVar = "KMH_RESTART_DELAY";
        private const int    DelaySeconds = 5;

        private static int _requested;

        public static bool Supervised =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"))
            || string.Equals(Environment.GetEnvironmentVariable("KMH_SUPERVISED"), "1", StringComparison.Ordinal);

        // A request, not a promise: nothing inside the process can read the unit's Restart= policy.
        public static string Describe()
            => Supervised
             ? $"the server exits {ExitCode} and the service manager starts it again (its Restart= policy must allow it)"
             : "the server launches a fresh copy of itself";

        // A bind that races the old listener fails the boot outright, so the replacement waits.
        public static void WaitIfRelaunched()
        {
            string raw = Environment.GetEnvironmentVariable(DelayVar);
            if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out int seconds) || seconds <= 0) return;
            Environment.SetEnvironmentVariable(DelayVar, null);
            ServerLog.Info($"Restart: waiting {seconds}s for the previous instance to release its ports.");
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(Math.Min(seconds, 30)));
        }

        // Returns only if the restart could not be started; otherwise the process is on its way out.
        public static bool Request(string actor, Action<string> reply)
        {
            if (System.Threading.Interlocked.Exchange(ref _requested, 1) != 0)
            { reply("A restart is already in progress."); return false; }

            string exe = Environment.ProcessPath;
            if (!Supervised && string.IsNullOrEmpty(exe))
            {
                _requested = 0;
                reply("Cannot restart: this build cannot find its own executable. Stop and start the server by hand.");
                return false;
            }

            ServerLog.Warn($"Restart requested by {actor} - {Describe()}.");
            reply($"Restarting now - {Describe()}. Players reconnect on their own.");

            try { return Restart(exe); }
            catch (Exception ex)
            {
                // The flush may have stopped the log sink, so this goes to the file directly as well.
                _requested = 0;
                string fatal = $"Restart: failed before the server could stop ({ex.GetType().Name}: {ex.Message}). "
                             + "The server is still running and nothing was restarted.";
                KmhLogSink.WriteFinal(fatal);
                ServerLog.Error(fatal);
                reply("Restart failed - the server is still running. See the log.");
                return false;
            }
        }

        private static bool Restart(string exe)
        {
            // Flushed before the replacement is launched, or it reads a snapshot this process has not written yet.
            try { KmhShutdown.Begin(); }
            catch (Exception ex) { ServerLog.Warn($"Restart: shutdown flush failed: {ex.Message}"); }

            if (Supervised) Environment.Exit(ExitCode);

            if (!TrySpawn(exe, out string why))
            {
                // The flush stopped the log sink, and the console dies with the window.
                string fatal = $"Restart: could not launch the replacement ({why}). The server is NOT coming back "
                             + "on its own - start it by hand.";
                KmhLogSink.WriteFinal(fatal);
                ServerLog.Error(fatal);
                Environment.Exit(1);
            }

            // Zero, not ExitCode: a restart-on-failure wrapper would read 42 as "start a second one".
            Environment.Exit(0);
            return true;
        }

        private static bool TrySpawn(string exe, out string why)
        {
            why = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = Environment.CurrentDirectory,
                    UseShellExecute  = false,
                };
                foreach (string a in OwnArguments()) psi.ArgumentList.Add(a);
                psi.Environment[DelayVar] = DelaySeconds.ToString();

                if (Process.Start(psi) != null) return true;
                why = "the process did not start";
                return false;
            }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; return false; }
        }

        // argv[0] is the executable; passing it again would make it the first argument.
        private static List<string> OwnArguments()
        {
            List<string> args = new List<string>();
            string[] all = Environment.GetCommandLineArgs();
            for (int i = 1; i < all.Length; i++) args.Add(all[i]);
            return args;
        }
    }
}
